// LearnAudioDriver: the REAL verdict source for learn mode (replaces the scripted Dev/DevLearnScript in the app; Dev/* stays
// for the editor preview). It owns Core's LearnChordSession per chord and judges live audio with Core's verifiers:
//
//   FingerByFinger  PluckVerifier on the finger step the UI shows (the UI's order, finger 1 first); a pass advances the flow
//                   (OnFingerPlaced) and the session (Skip, so they stay in lockstep); a fault shows Core's tip on the card.
//   PluckCheck      PluckVerifier on the session's active string, lowest first; every verdict goes through the session and
//                   marks the neck column; the last correct string starts the flow's all-correct hold (its 1.5 s wait).
//   StrumCheck      Core's StrumFeed (onset + StrumVerifier) against the chord; StrumRouting feeds the session; a clean
//                   strum ends learn mode (Done), a fault shows its top issue.
//
// Audio comes from MicCapture, mic only (LearnMicTuner's rule): the WAV replay is a recording of whole chords, so it keeps
// driving the scripted dev run exactly as before, with the SIMULATED INPUT badge showing. Main thread only; no allocation
// per frame. With no usable microphone the driver produces nothing: DemoDirector falls back to the scripted run and shows
// the SIMULATED CHECKS label.
using System;
using System.Collections.Generic;
using UnityEngine;
using UntitledGuitarApp.Core.Audio;
using UntitledGuitarApp.Core.Practice;
using UntitledGuitarApp.Core.Theory;
using UntitledGuitarApp.Runtime.Audio;
using UntitledGuitarApp.Runtime.Hud.Learn;
using UntitledGuitarApp.Runtime.Logging;
using CoreStage = UntitledGuitarApp.Core.Practice.LearnStage;

namespace UntitledGuitarApp.Runtime.Practice.Learn
{
    [DefaultExecutionOrder(19985)]   // after LearnMicTuner (19980), before LearnPresenter (19990)
    [DisallowMultipleComponent]
    public sealed class LearnAudioDriver : MonoBehaviour
    {
        /// <summary>Samples read from MicCapture per call (a frame's new audio is ~800; 4096 is headroom).</summary>
        public const int ChunkSamples = 4096;

        MicCapture _mic;
        LearnPresenter _presenter;
        ChordShape _shape;
        LearnChordSession _session;
        LearnPluckPipe _pluck;
        StrumFeed _strum;
        IReadOnlyList<ChordShape> _confusers;
        readonly float[] _chunk = new float[ChunkSamples];
        bool _active;
        int _rate = -1, _epoch = -1;
        bool _drained;
        LearnStage _seenStage = LearnStage.Idle;
        bool _warnedRate;
        int _plucksJudged, _strumsJudged;

        /// <summary>True from Play until Stop, Done or Idle.</summary>
        public bool Active { get { return _active; } }

        /// <summary>Core's session for this run (null outside a run). Read it; drive it through this class.</summary>
        public LearnChordSession Session { get { return _session; } }

        /// <summary>The chord shape being taught (Core's ChordShape), null outside a run.</summary>
        public ChordShape Shape { get { return _shape; } }

        /// <summary>Pluck verdicts accepted this run (finger steps + pluck-check strings).</summary>
        public int PlucksJudged { get { return _plucksJudged; } }

        /// <summary>Strum verdicts judged this run.</summary>
        public int StrumsJudged { get { return _strumsJudged; } }

        /// <summary>Core's audio runs at these rates only (the same rule as LearnMicTuner).</summary>
        public static bool SupportedRate(int sampleRate)
        {
            return sampleRate == 44100 || sampleRate == 48000;
        }

        /// <summary>True while real microphone audio can drive the verdicts (mic only: see the file summary).</summary>
        public bool IsLive
        {
            get
            {
                if (_mic == null || !_mic.IsCapturing || _mic.IsReplay) return false;
                return SupportedRate(_mic.SampleRate);
            }
        }

        public static LearnAudioDriver Create(Transform parent, MicCapture mic, LearnPresenter presenter)
        {
            var go = new GameObject("LearnAudioDriver");
            if (parent != null) go.transform.SetParent(parent, false);
            LearnAudioDriver driver = go.AddComponent<LearnAudioDriver>();
            driver._mic = mic;
            driver._presenter = presenter;
            return driver;
        }

        /// <summary>Starts the real run for <paramref name="chord"/> (Core's shape). False for an unknown chord or no presenter.</summary>
        public bool Play(LearnChord chord)
        {
            if (_presenter == null || chord == null) return false;
            ChordShape shape;
            if (!ChordLibrary.TryGet(chord.Name, out shape)) return false;
            _shape = shape;
            _session = new LearnChordSession(shape, Time.realtimeSinceStartupAsDouble);
            _confusers = StrumVerifier.DefaultConfusers(shape);
            _active = true;
            _drained = false;
            _seenStage = _presenter.Stage;
            _plucksJudged = 0;
            _strumsJudged = 0;
            UGALog.Event("learn_driver", "chord", chord.Name, "live", IsLive);
            return true;
        }

        /// <summary>Ends the run (the presenter returns to idle).</summary>
        public void Stop()
        {
            _active = false;
            _session = null;
            _shape = null;
            if (_presenter != null) _presenter.Stop();
        }

        /// <summary>Moves on by one stage (operator S), the session kept in lockstep.</summary>
        public void Skip()
        {
            if (!_active || _presenter == null) return;
            _presenter.Skip();
            SyncSession(_session, _presenter.Stage, Time.realtimeSinceStartupAsDouble);
        }

        /// <summary>The session stage that renders the same screen as <paramref name="flowStage"/> (the UI flow has Tune, Core does not). Pure.</summary>
        public static CoreStage SessionStageFor(LearnStage flowStage)
        {
            switch (flowStage)
            {
                case LearnStage.StrumCheck: return CoreStage.WholeChord;
                case LearnStage.Done: return CoreStage.Mastered;
                case LearnStage.PluckCheck: return CoreStage.PluckCheck;
                case LearnStage.FingerByFinger: return CoreStage.FingerByFinger;
                default: return CoreStage.Mastered;
            }
        }

        /// <summary>Advances the session (Skip per step) until it is at least at the stage matching <paramref name="flowStage"/>. Pure except for session state.</summary>
        public static void SyncSession(LearnChordSession session, LearnStage flowStage, double nowSec)
        {
            if (session == null) return;
            CoreStage target = SessionStageFor(flowStage);
            for (int guard = 0; guard < 16 && session.Stage < target; guard++) session.Skip(nowSec);
        }

        void Update()
        {
            if (!_active || _presenter == null) return;
            LearnStage stage = _presenter.Stage;
            if (stage == LearnStage.Idle || stage == LearnStage.Done)
            {
                _active = false;   // the flow ended the run itself (a clean strum)
                return;
            }
            if (stage == LearnStage.Tune) return;   // the tuner owns the microphone during tuning
            if (!IsLive)
            {
                if (!_warnedRate && _mic != null && _mic.IsCapturing && !SupportedRate(_mic.SampleRate))
                {
                    _warnedRate = true;
                    UGALog.Warn("learn_audio_rate", "mic runs at " + _mic.SampleRate + " Hz; Core needs 44100 or 48000, so learn verdicts are off");
                }
                return;
            }
            int rate = _mic.SampleRate;
            if (_pluck == null || rate != _rate || _mic.Epoch != _epoch) RebuildDsp(rate);
            if (stage != _seenStage) EnterStage(stage);
            Pump(stage);
        }

        void RebuildDsp(int rate)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            _pluck = new LearnPluckPipe(AudioConfig.Default, rate, now);
            _strum = new StrumFeed(AudioConfig.Default, rate, now);
            _rate = rate;
            _epoch = _mic.Epoch;
            _drained = false;
        }

        void EnterStage(LearnStage stage)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (stage == LearnStage.StrumCheck && _strum != null) _strum.Reset(now);
            else if ((stage == LearnStage.FingerByFinger || stage == LearnStage.PluckCheck) && _pluck != null) _pluck.Reset(now);
            SyncSession(_session, stage, now);
            _seenStage = stage;
        }

        void Pump(LearnStage stage)
        {
            if (!_drained)
            {
                _drained = true;
                while (_mic.Read(_chunk, 0, _chunk.Length) > 0) { }   // drop the old audio the ring held for the tuner
            }
            int n;
            while ((n = _mic.Read(_chunk, 0, _chunk.Length)) > 0)
            {
                LearnStage now = _presenter.Stage;   // a verdict can move the stage mid-pump
                if (now != stage)
                {
                    EnterStage(now);
                    stage = now;
                }
                if (stage == LearnStage.StrumCheck)
                {
                    if (_strum.Process(_chunk, 0, n, _shape, _confusers) > 0) DrainStrums();
                }
                else if (stage == LearnStage.FingerByFinger || stage == LearnStage.PluckCheck)
                {
                    if (_pluck.Process(_chunk, 0, n, PluckString(stage), PluckMidi(stage), _shape) > 0) DrainPlucks();
                }
            }
        }

        int PluckString(LearnStage stage)
        {
            if (stage == LearnStage.FingerByFinger)
            {
                LearnChord chord = _presenter.Chord;
                int k = _presenter.Flow.StepIndex;
                if (chord != null && k >= 0 && k < chord.StepCount) return chord.Step(k).stringNumber;
                return 0;
            }
            return _session != null && _session.ExpectsPluck ? _session.ActiveString : 0;
        }

        int PluckMidi(LearnStage stage)
        {
            if (stage == LearnStage.FingerByFinger)
            {
                LearnChord chord = _presenter.Chord;
                int k = _presenter.Flow.StepIndex;
                if (chord != null && k >= 0 && k < chord.StepCount) return _shape.MidiOn(chord.Step(k).stringNumber);
                return -1;
            }
            return _session != null && _session.ExpectsPluck ? _session.ExpectedMidi : -1;
        }

        void DrainPlucks()
        {
            PluckEvent e;
            while (_pluck.TryRead(out e)) HandlePluck(in e);
        }

        void HandlePluck(in PluckEvent e)
        {
            PluckResult r = e.Result;
            double now = Time.realtimeSinceStartupAsDouble;
            if (_presenter.Stage == LearnStage.FingerByFinger)
            {
                LearnChord chord = _presenter.Chord;
                int k = _presenter.Flow.StepIndex;
                if (chord == null || k < 0 || k >= chord.StepCount) return;
                LearnChordStep step = chord.Step(k);
                if (r.String != step.stringNumber) return;   // a stale window: the step moved on
                bool pass = r.Verdict == PluckVerdict.Correct || r.Verdict == PluckVerdict.ShortRing;
                _plucksJudged++;
                UGALog.Event("learn_pluck", "chord", _shape.Name, "string", r.String, "verdict", r.Verdict.ToString());
                if (pass)
                {
                    _presenter.Verdicts.OnFingerPlaced(step.finger);
                    SyncSession(_session, _presenter.Stage, now);
                }
                else if (r.Verdict != PluckVerdict.Unclear)
                {
                    _presenter.Flow.OnFingerStageFault(Tips.ForPluck(r, _shape), LearnVerdictMap.IconForPluck(r.Verdict));
                }
                return;
            }
            if (_presenter.Stage != LearnStage.PluckCheck || _session == null || !_session.ExpectsPluck) return;
            bool current = r.String == _session.ActiveString;
            _session.OnPluck(r, now);
            _plucksJudged++;
            UGALog.Event("learn_pluck", "chord", _shape.Name, "string", r.String, "verdict", r.Verdict.ToString());
            if (!current) return;   // the session moved on while this window completed: stale, already handled
            bool complete = _session.Stage == CoreStage.WholeChord;
            string tip = r.Verdict == PluckVerdict.Correct ? null : Tips.ForPluck(r, _shape);
            _presenter.Flow.OnDriverPluckResult(r.String, LearnVerdictMap.MarkForPluck(r.Verdict), tip,
                                                LearnVerdictMap.IconForPluck(r.Verdict), complete);
        }

        void DrainStrums()
        {
            StrumEvent e;
            while (_strum.TryRead(out e)) HandleStrum(in e);
        }

        void HandleStrum(in StrumEvent e)
        {
            if (_presenter.Stage != LearnStage.StrumCheck || _session == null) return;
            if (e.Result.ExpectedChord != _shape.Name) return;   // the feed only judges _shape; keep the guard anyway
            _session.OnStrum(e.Result, Time.realtimeSinceStartupAsDouble);
            _strumsJudged++;
            UGALog.Event("learn_strum", "chord", _shape.Name, "clean", e.Result.Clean, "issue", e.Result.Issue.ToString());
            if (e.Result.Clean)
            {
                _presenter.Verdicts.OnStrumResult(true, null, TipIcon.Good);
                return;
            }
            _presenter.Verdicts.OnStrumResult(false, Tips.ForStrum(e.Result, _shape), LearnVerdictMap.IconForStrum(e.Result));
        }
    }
}
