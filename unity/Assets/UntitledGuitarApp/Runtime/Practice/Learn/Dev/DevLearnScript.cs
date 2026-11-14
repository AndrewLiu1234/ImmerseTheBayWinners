// TEMPORARY until Core's verdicts land (Person 2): plays a SIMULATED learn run (DevLearnTimeline) into a LearnPresenter, so the
// whole learn flow can be shown before the tuner, pluck check and strum check exist. Delete it when the Core adapter feeds
// LearnPresenter.Verdicts. Everything it sends is invented: never use it to judge audio.
//
// The chord comes from the flags (FlagSet.chords[0]; the judge profile sets Em), else G. Keys while it runs (old Input
// Manager, the same as Bootstrap's R key): L starts (or restarts) the run, N skips to the next stage, K stops learn mode.
// Set HandleKeys = false to drive it from code only (Play, Skip, Stop). Main thread only; no allocation per frame.
using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Logging;
using UntitledGuitarApp.Runtime.Neck;

namespace UntitledGuitarApp.Runtime.Practice.Learn.Dev
{
    [DisallowMultipleComponent]
    public sealed class DevLearnScript : MonoBehaviour
    {
        public const KeyCode StartKey = KeyCode.L;
        public const KeyCode SkipKey = KeyCode.N;
        public const KeyCode StopKey = KeyCode.K;

        LearnPresenter _presenter;
        DevLearnTimeline _timeline;
        readonly FingertipDot[] _dots = new FingertipDot[FloatingNeck.MaxDots];
        readonly StringMark[] _neckMarks = new StringMark[6];
        float _clock;
        int _next;
        bool _playing;

        /// <summary>Read L / N / K from the keyboard. Default true.</summary>
        public bool HandleKeys = true;

        public LearnPresenter Presenter { get { return _presenter; } }
        public DevLearnTimeline Timeline { get { return _timeline; } }
        public bool Playing { get { return _playing; } }

        /// <summary>Seconds since Play.</summary>
        public float Clock { get { return _clock; } }

        public static DevLearnScript Create(Transform parent, LearnPresenter presenter)
        {
            var go = new GameObject("DevLearnScript (SIMULATED verdicts)");
            if (parent != null) go.transform.SetParent(parent, false);
            DevLearnScript script = go.AddComponent<DevLearnScript>();
            script._presenter = presenter;
            return script;
        }

        /// <summary>Starts the run for the chord the flags ask for (else G).</summary>
        public bool Play()
        {
            return Play(DevLearnChords.FromFlags(FlagStore.Current));
        }

        /// <summary>Starts the run for <paramref name="chord"/>: the neck and the hand take its shape, the tuner comes up.</summary>
        public bool Play(LearnChord chord)
        {
            if (_presenter == null || chord == null) return false;
            _timeline = DevLearnTimeline.Build(chord);
            int dotCount = -1;
            FloatingNeck neck = _presenter.Neck;
            if (neck != null && neck.Layout != null) dotCount = DevLearnChords.ToNeck(chord, neck.Layout, _dots, _neckMarks);
            _presenter.AutoAdvanceTuning = false;   // this script says when each string is in tune
            if (!_presenter.Begin(chord, dotCount >= 0 ? _dots : null, dotCount)) return false;
            _clock = 0f;
            _next = 0;
            _playing = true;
            UGALog.Event("learn_dev_script", "chord", chord.Name, "mistake", _timeline.MistakeTip, "seconds", _timeline.Duration);
            return true;
        }

        /// <summary>Jumps to the start of the next stage (the run and learn mode together).</summary>
        public void Skip()
        {
            if (!_playing || _timeline == null) return;
            LearnStage next = _presenter.Stage + 1;
            if (next > LearnStage.Done) return;
            _clock = _timeline.StageStart(next);
            _next = 0;
            while (_next < _timeline.Count && (_timeline[_next].stage < next || _timeline[_next].time < _clock)) _next++;
            _presenter.SkipTo(next);
        }

        /// <summary>Stops the run and learn mode.</summary>
        public void Stop()
        {
            _playing = false;
            if (_presenter != null) _presenter.Stop();
        }

        void Update()
        {
            if (HandleKeys)
            {
                if (UnityEngine.Input.GetKeyDown(StartKey)) Play();
                else if (UnityEngine.Input.GetKeyDown(SkipKey)) Skip();
                else if (UnityEngine.Input.GetKeyDown(StopKey)) Stop();
            }
            if (_playing) Advance(Time.unscaledDeltaTime);
        }

        /// <summary>Moves the run on by <paramref name="dt"/> seconds: sends every event that is due, and one tuner reading while a sweep runs.</summary>
        public void Advance(float dt)
        {
            if (!_playing || _timeline == null || _presenter == null) return;
            _clock += dt > 0f ? dt : 0f;
            ILearnVerdictSink sink = _presenter.Verdicts;

            // The sweep that covers the clock feeds one reading per call (readings stop between strings: nothing is plucked).
            for (int i = _next - 1; i >= 0 && i >= _next - 2; i--)
            {
                DevLearnEvent e = _timeline[i];
                if (e.kind != DevLearnEventKind.TuneSweep) continue;
                float into = _clock - e.time;
                if (into >= 0f && into < e.duration)
                    sink.OnTunerReading(DevLearnTimeline.SweepCents(e.startCents, into), DevLearnTimeline.Confidence);
                break;
            }

            while (_next < _timeline.Count && _timeline[_next].time <= _clock)
            {
                DevLearnEvent e = _timeline[_next++];
                switch (e.kind)
                {
                    case DevLearnEventKind.TuneSweep:
                        sink.OnTunerReading(DevLearnTimeline.SweepCents(e.startCents, _clock - e.time), DevLearnTimeline.Confidence);
                        break;
                    case DevLearnEventKind.StringInTune:
                        sink.OnStringInTune(e.stringNumber);
                        break;
                    case DevLearnEventKind.FingerPlaced:
                        sink.OnFingerPlaced(e.finger);
                        break;
                    case DevLearnEventKind.Pluck:
                        sink.OnPluckResult(e.stringNumber, e.mark, e.tip, e.icon);
                        break;
                    case DevLearnEventKind.Strum:
                        sink.OnStrumResult(e.clean, e.issue, e.icon);
                        break;
                    case DevLearnEventKind.End:
                        _playing = false;
                        UGALog.Event("learn_dev_script_done", "chord", _timeline.Chord.Name, "stage", _presenter.Stage.ToString());
                        break;
                }
            }
        }
    }
}
