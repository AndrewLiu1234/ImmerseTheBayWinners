// CorePlayAlong: the adapter from Person 2's Core play-along to the XR seam. It loads a song file with Core's SongValidator
// (SongParser + the content rules), plays it with Core's SongTimeline + PlayAlongSession at a tempo preset from the flags, and
// fills Runtime/Practice/PracticeFrame every frame for the ghost hands and the play HUD. Core is read-only here (Person 2).
//
// WHAT THE ADAPTER ADDS ON TOP OF CORE
//  * Count-in: one bar before the first chord, on negative beats. Core's session has none, so it is started (Start(now))
//    exactly when the count-in ends; until then the frame shows no chord and the first chord as the next one.
//  * Beat convention: Core counts beats from the first chord (pickup included); PracticeFrame from the first downbeat, so
//    frame beat = Core beat - pickupBeats (PlayAlongMath.FrameBeat).
//  * Next DIFFERENT chord: Core's spans are one per chord of the file, so a chord that crosses a line break is two spans and
//    Core's Next / BeatsToChange point at the end of the span. The frame's NextChord / BeatsToChange point at the next span
//    whose chord differs (PlayAlongMath.NextDifferent).
//  * ChordConfirmed: true once a matching strum was accepted for the span that is sounding.
//  * SIMULATED strums: Core has no strum check yet (SL3), and the "safe" flag profile replays a WAV that does not exist, so
//    by default the adapter itself calls the session's OnStrum(expected chord, now): in wait mode SimulatedHoldSec into each
//    hold, free-running SimulatedOffsetSec after each chord starts. Each one is logged (play_strum_simulated) and the
//    results are marked Simulated. Turn SimulateStrums off and call OnStrum from the detector once it exists.
//  * Wait mode holds at EVERY span start, the first chord included and also a same-chord span after a line break: that is
//    Core's rule (PlayAlongSession), not changed here. Core's escape hatch (8 s or 6 wrong strums) still applies.
//
// Plain C#: no MonoBehaviour. Engine calls only in TryLoadSong (Application.streamingAssetsPath), StartFromFlags (FlagStore)
// and the simulated-strum log line (UGALog, once per chord). Tick allocates nothing except inside Core's session.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UntitledGuitarApp.Core.Audio;
using UntitledGuitarApp.Core.Practice;
using UntitledGuitarApp.Core.Songs;
using UntitledGuitarApp.Core.Theory;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.Practice
{
    /// <summary>Where a play-along run is.</summary>
    public enum PlayAlongPhase
    {
        /// <summary>No song (never started, or stopped).</summary>
        Idle = 0,
        /// <summary>The one-bar count-in before the first chord.</summary>
        CountIn = 1,
        /// <summary>Core's session is running (wait-mode holds included).</summary>
        Playing = 2,
        /// <summary>The session finished; <see cref="CorePlayAlong.Results"/> holds the score.</summary>
        Ended = 3,
    }

    /// <summary>The score of one run, from Core's session.</summary>
    public struct PlayAlongResults
    {
        /// <summary>Chords (Core spans) with an accepted strum.</summary>
        public int ChordsHit;
        /// <summary>Chords that passed with no accepted strum.</summary>
        public int ChordsMissed;
        /// <summary>Chords given up by the escape hatch.</summary>
        public int ChordsSkipped;
        /// <summary>Chords in the song (Core spans).</summary>
        public int ChordsTotal;
        /// <summary>Signed mean of Core's per-chord errors (best strum - chord start), ms, + = late. NaN when not measured (every strum simulated).</summary>
        public float MeanTimingMs;
        /// <summary>True when any strum was simulated: show it as such.</summary>
        public bool Simulated;
    }

    /// <summary>Core's play-along session as a <see cref="PracticeFrame"/> producer. Main thread; not thread-safe.</summary>
    public sealed class CorePlayAlong
    {
        public const string SongsFolderName = "songs";
        public const string SongFileSuffix = ".song.json";

        /// <summary>The demo song.</summary>
        public const string DefaultSongId = "riptide-loop";

        /// <summary>The preset asked for when nothing else is (the nearest one in the flags' tempoPresets is used).</summary>
        public const float DefaultTempoPreset = 0.75f;

        /// <summary>Wait mode: a simulated strum arrives this long into each hold, seconds.</summary>
        public const float DefaultSimulatedHoldSec = 0.6f;

        /// <summary>Free-running: a simulated strum arrives this long after each chord starts, seconds.</summary>
        public const float DefaultSimulatedOffsetSec = 0.04f;

        /// <summary>Wait mode: seconds into each hold at which the simulated strum is played.</summary>
        public float SimulatedHoldSec = DefaultSimulatedHoldSec;

        /// <summary>Free-running: seconds after each chord starts at which the simulated strum is played.</summary>
        public float SimulatedOffsetSec = DefaultSimulatedOffsetSec;

        /// <summary>True (default): play the expected chord by simulation. Set false once a real strum detector calls <see cref="OnStrum"/>.</summary>
        public bool SimulateStrums = true;

        /// <summary>Log each simulated strum (UGALog event play_strum_simulated). Tests turn it off.</summary>
        public bool LogSimulatedStrums = true;

        Song _song;
        SongTimeline _timeline;
        PlayAlongSession _session;
        StrumFeed _feed;                       // real strums (SL3): onset -> strum check -> OnStrum
        int _feedRate = -1;
        IReadOnlyList<ChordShape> _confusers;  // the song's distinct chords, built once per run
        int[] _nextDifferent = new int[0];
        string[] _strumSlots;
        int _slotsPerBeat = 1;
        PlayAlongPhase _phase;
        float _preset = DefaultTempoPreset;
        bool _waitMode;
        double _now;
        double _countInSec;
        double _spb = 1;
        ChordTarget _target;
        int _confirmedSpan = -1;
        int _seenSpan = -1;
        bool _seenHolding;
        double _seenSince;
        int _simulatedStrums;
        int _realStrums;
        PlayAlongResults _results;
        PracticeFrame _frame = PracticeFrame.Idle;

        // ------------------------------------------------------------------ state

        /// <summary>The song being played (null when idle).</summary>
        public Song Song { get { return _song; } }

        /// <summary>Core's timeline at the chosen preset (null when idle).</summary>
        public SongTimeline Timeline { get { return _timeline; } }

        /// <summary>Core's session (null when idle). Read it; drive it only through this class.</summary>
        public PlayAlongSession Session { get { return _session; } }

        /// <summary>Where the run is.</summary>
        public PlayAlongPhase Phase { get { return _phase; } }

        /// <summary>True from Start until the session finishes.</summary>
        public bool IsRunning { get { return _phase == PlayAlongPhase.CountIn || _phase == PlayAlongPhase.Playing; } }

        /// <summary>The tempo preset in use (0.75 = 75%).</summary>
        public float TempoPreset { get { return _preset; } }

        /// <summary>Whether wait mode is on for this run.</summary>
        public bool WaitMode { get { return _waitMode; } }

        /// <summary>The adapter's clock: seconds since Start (the count-in starts at 0).</summary>
        public double Now { get { return _now; } }

        /// <summary>Index of Core's span sounding now; -1 in the count-in and after the end.</summary>
        public int CurrentSpan { get { return _phase == PlayAlongPhase.Playing ? _target.CurrentIndex : -1; } }

        /// <summary>True while wait mode holds the playhead.</summary>
        public bool Waiting { get { return _phase == PlayAlongPhase.Playing && _target.Holding; } }

        /// <summary>Seconds the current hold has lasted, as seen by the adapter (0 when not waiting).</summary>
        public double HoldSeconds { get { return Waiting && _seenHolding ? _now - _seenSince : 0; } }

        /// <summary>Strums this adapter simulated so far.</summary>
        public int SimulatedStrums { get { return _simulatedStrums; } }

        /// <summary>Real (audio-detected) strums routed into Core's session so far.</summary>
        public int RealStrums { get { return _realStrums; } }

        /// <summary>The score (final once Phase is Ended).</summary>
        public PlayAlongResults Results { get { return _phase == PlayAlongPhase.Ended ? _results : BuildResults(); } }

        /// <summary>This frame's practice state (a copy; StrumSlots is shared and read-only).</summary>
        public PracticeFrame Frame { get { return _frame; } }

        /// <summary>Copies this frame's practice state into <paramref name="frame"/> (no allocation).</summary>
        public void FillFrame(ref PracticeFrame frame)
        {
            frame = _frame;
        }

        // ------------------------------------------------------------------ loading

        /// <summary>StreamingAssets/songs (engine call: Application.streamingAssetsPath).</summary>
        public static string SongsFolder
        {
            get { return Path.Combine(Application.streamingAssetsPath, SongsFolderName); }
        }

        /// <summary>folder/&lt;id&gt;.song.json.</summary>
        public static string SongPath(string folder, string songId)
        {
            return Path.Combine(folder ?? string.Empty, (songId ?? string.Empty) + SongFileSuffix);
        }

        /// <summary>Loads and validates StreamingAssets/songs/&lt;id&gt;.song.json with Core's SongValidator. Never throws.</summary>
        public static bool TryLoadSong(string songId, out Song song, out string error)
        {
            string folder;
            try
            {
                folder = SongsFolder;
            }
            catch (Exception e)
            {
                song = null;
                error = "no StreamingAssets path (" + e.GetType().Name + ")";
                return false;
            }
            return TryLoadSongFile(SongPath(folder, songId), out song, out error);
        }

        /// <summary>Loads and validates one song file (plain .NET plus Core). Never throws.</summary>
        public static bool TryLoadSongFile(string path, out Song song, out string error)
        {
            song = null;
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception e)
            {
                error = "cannot read " + path + " (" + e.GetType().Name + ": " + e.Message + ")";
                return false;
            }
            ParseResult result = SongValidator.Load(text);
            if (!result.Ok)
            {
                error = Path.GetFileName(path) + ": " + (result.Errors.Count > 0 ? string.Join("; ", result.Errors) : "not a song");
                return false;
            }
            song = result.Song;
            error = null;
            return true;
        }

        // ------------------------------------------------------------------ control

        /// <summary>
        /// Starts <paramref name="song"/> with the flags as they are now (read here, once, not per frame): the preset of
        /// FlagStore.Current.tempoPresets nearest to <paramref name="wantedPreset"/>, and waitModeDefault.
        /// </summary>
        public bool StartFromFlags(Song song, float wantedPreset)
        {
            FlagSet flags = FlagStore.Current;
            float preset = PlayAlongMath.NearestPreset(wantedPreset, flags != null ? flags.tempoPresets : null);
            return Start(song, preset, flags == null || flags.waitModeDefault, null);
        }

        /// <summary>
        /// Starts <paramref name="song"/> at the count-in. <paramref name="config"/> (may be null) carries Core's other tunables;
        /// its WaitMode is replaced by <paramref name="waitMode"/>. False (and Idle) for a song Core cannot time.
        /// </summary>
        public bool Start(Song song, float tempoPreset, bool waitMode, PlayAlongConfig config)
        {
            Stop();
            if (song == null || !(song.TempoBpm > 0) || song.BeatsPerBar < 1) return false;
            float preset = tempoPreset > 0f && !float.IsInfinity(tempoPreset) ? tempoPreset : DefaultTempoPreset;
            SongTimeline timeline;
            try
            {
                timeline = new SongTimeline(song, preset);
            }
            catch (Exception)
            {
                return false;
            }
            if (timeline.Spans.Count == 0) return false;

            PlayAlongConfig cfg = config ?? new PlayAlongConfig();
            cfg.WaitMode = waitMode;
            _song = song;
            _timeline = timeline;
            _session = new PlayAlongSession(timeline, cfg);
            _preset = preset;
            _waitMode = waitMode;
            _spb = timeline.SecondsPerBeat;
            _nextDifferent = PlayAlongMath.NextDifferent(timeline.Spans);
            _strumSlots = song.Strum != null && song.Strum.Slots != null && song.Strum.Slots.Count > 0 ? song.Strum.Slots.ToArray() : null;
            _slotsPerBeat = _strumSlots != null ? PlayAlongMath.SlotsPerBeat(song.Strum.Subdivision, song.BeatsPerBar) : 1;
            _confusers = TimelineConfusers(timeline);
            _feed = null;
            _feedRate = -1;
            _now = 0;
            _countInSec = song.BeatsPerBar * _spb;
            _phase = PlayAlongPhase.CountIn;
            UpdateFrame();
            return true;
        }

        /// <summary>Back to Idle; the frame becomes PracticeFrame.Idle.</summary>
        public void Stop()
        {
            _song = null;
            _timeline = null;
            _session = null;
            _feed = null;
            _feedRate = -1;
            _confusers = null;
            _nextDifferent = new int[0];
            _strumSlots = null;
            _slotsPerBeat = 1;
            _phase = PlayAlongPhase.Idle;
            _now = 0;
            _target = default(ChordTarget);
            _confirmedSpan = -1;
            _seenSpan = -1;
            _seenHolding = false;
            _simulatedStrums = 0;
            _realStrums = 0;
            _results = default(PlayAlongResults);
            _frame = PracticeFrame.Idle;
        }

        /// <summary>Advances by <paramref name="deltaSeconds"/> of real time (NaN, negative and infinite count as 0).</summary>
        public void Tick(float deltaSeconds)
        {
            if (!IsRunning) return;
            _now += deltaSeconds > 0f && !float.IsInfinity(deltaSeconds) ? deltaSeconds : 0f;
            if (_phase == PlayAlongPhase.CountIn)
            {
                if (_now < _countInSec - PlayAlongMath.Epsilon)
                {
                    UpdateFrame();
                    return;
                }
                _target = _session.Start(_countInSec);
                _phase = PlayAlongPhase.Playing;
            }
            _target = _session.Update(_now);
            Observe();
            if (_target.Finished) Finish();
            else if (SimulateStrums) Simulate();
            UpdateFrame();
        }

        /// <summary>A detected strum of <paramref name="chord"/> now (the real detector's entry; Core's OnStrum). Ignored unless playing.</summary>
        public void OnStrum(string chord)
        {
            Strum(chord, false);
            UpdateFrame();
        }

        /// <summary>Core's escape hatch by hand: give up the chord being held.</summary>
        public void Skip()
        {
            if (_phase != PlayAlongPhase.Playing) return;
            _target = _session.Skip(_now);
            Observe();
            if (_target.Finished) Finish();
            UpdateFrame();
        }

        /// <summary>
        /// Real strum audio (the detector's entry, SL3): mic or WAV samples on the adapter's clock. Every onset is judged by
        /// Core's StrumFeed against the chord sounding now and routed with StrumRouting into the session's OnStrum. Onsets
        /// during the count-in are dropped (nothing to judge yet), so a count-in strum can never start the song early. The
        /// feed is built lazily at the first call (origin = the adapter's clock now, so feed times line up with <see cref="Now"/>);
        /// <paramref name="sampleRate"/> changing rebuilds it. Main thread; allocates nothing per frame.
        /// </summary>
        public void FeedAudio(float[] samples, int offset, int count, int sampleRate)
        {
            if (_phase != PlayAlongPhase.CountIn && _phase != PlayAlongPhase.Playing) return;
            if (_session == null || samples == null || sampleRate < 8000) return;
            if (_feed == null || sampleRate != _feedRate)
            {
                _feed = new StrumFeed(AudioConfig.Default, sampleRate, _now);
                _feedRate = sampleRate;
            }
            ChordShape expected = null;
            if (_phase == PlayAlongPhase.Playing && _target.CurrentIndex >= 0 && _target.CurrentIndex < _timeline.Spans.Count)
                ChordLibrary.TryGet(_timeline.Spans[_target.CurrentIndex].Chord, out expected);
            _feed.Process(samples, offset, count, expected, _confusers);
            StrumEvent e;
            while (_feed.TryRead(out e))
            {
                if (_phase != PlayAlongPhase.Playing || _session == null) continue;   // count-in strums never reach the session
                ChordTarget before = _target;
                _target = StrumRouting.Feed(_session, e);
                _realStrums++;
                IReadOnlyList<ChordSpan> spans = _timeline.Spans;
                if (before.CurrentIndex >= 0 && !string.IsNullOrEmpty(e.HeardChord))
                {
                    if (before.CurrentIndex < spans.Count &&
                        string.Equals(spans[before.CurrentIndex].Chord, e.HeardChord, StringComparison.Ordinal))
                        _confirmedSpan = before.CurrentIndex;
                    else if (before.CurrentIndex - 1 >= 0 && before.CurrentIndex - 1 < spans.Count &&
                             string.Equals(spans[before.CurrentIndex - 1].Chord, e.HeardChord, StringComparison.Ordinal))
                        _confirmedSpan = before.CurrentIndex - 1;
                }
                UGALog.Event("play_strum_real", "chord", e.HeardChord, "span", before.CurrentIndex, "held", before.Holding);
                Observe();
                if (_target.Finished) Finish();
            }
            UpdateFrame();
        }

        /// <summary>Forgets the strum feed (call when the mic restarts or its epoch changes: the stream has a gap). It rebuilds lazily in <see cref="FeedAudio"/>.</summary>
        public void ResetAudio()
        {
            _feed = null;
            _feedRate = -1;
        }

        /// <summary>The song's distinct chords as Core shapes (the strum check's confusers). Allocates once per song.</summary>
        static IReadOnlyList<ChordShape> TimelineConfusers(SongTimeline timeline)
        {
            var list = new List<ChordShape>();
            if (timeline == null) return list;
            for (int i = 0; i < timeline.Spans.Count; i++)
            {
                string name = timeline.Spans[i].Chord;
                ChordShape shape;
                if (string.IsNullOrEmpty(name) || !ChordLibrary.TryGet(name, out shape)) continue;
                bool known = false;
                for (int k = 0; k < list.Count; k++)
                    if (string.Equals(list[k].Name, shape.Name, StringComparison.Ordinal)) { known = true; break; }
                if (!known) list.Add(shape);
            }
            return list;
        }

        // ------------------------------------------------------------------ internals

        void Observe()
        {
            int span = _target.CurrentIndex;
            if (span != _seenSpan || _target.Holding != _seenHolding)
            {
                _seenSpan = span;
                _seenHolding = _target.Holding;
                _seenSince = _now;
            }
        }

        void Simulate()
        {
            int span = _target.CurrentIndex;
            if (span < 0 || span == _confirmedSpan) return;
            double due = _target.Holding ? SimulatedHoldSec : SimulatedOffsetSec;
            if (_now - _seenSince + PlayAlongMath.Epsilon >= due) Strum(_target.Current, true);
        }

        void Strum(string chord, bool simulated)
        {
            if (_phase != PlayAlongPhase.Playing || _session == null) return;
            ChordTarget before = _target;
            _target = _session.OnStrum(chord, _now);
            if (simulated) _simulatedStrums++;
            else _realStrums++;
            if (before.CurrentIndex >= 0 && string.Equals(chord, before.Current, StringComparison.Ordinal))
                _confirmedSpan = before.CurrentIndex;
            if (simulated && LogSimulatedStrums)
                UGALog.Event("play_strum_simulated", "chord", chord, "span", before.CurrentIndex, "held", before.Holding);
            Observe();
            if (_target.Finished) Finish();
        }

        void Finish()
        {
            if (_phase == PlayAlongPhase.Ended) return;
            _phase = PlayAlongPhase.Ended;
            _results = BuildResults();
        }

        PlayAlongResults BuildResults()
        {
            PlayAlongResults r = default(PlayAlongResults);
            r.MeanTimingMs = float.NaN;
            if (_session == null) return r;
            r.ChordsTotal = _timeline.Spans.Count;
            r.ChordsHit = _session.Hits;
            r.ChordsMissed = _session.Missed;
            r.ChordsSkipped = _session.Skipped;
            r.Simulated = _simulatedStrums > 0;
            if (_realStrums > 0)
            {
                // Core's per-chord error is signed (best strum - chord start); its MeanAbsErrorSec is not, the card wants the sign.
                double sum = 0;
                int n = 0;
                IReadOnlyList<ChordResult> results = _session.Results;
                for (int i = 0; i < results.Count; i++)
                {
                    ChordResult c = results[i];
                    if (c.Outcome != ChordOutcome.Hit || double.IsNaN(c.ErrorSec)) continue;
                    sum += c.ErrorSec;
                    n++;
                }
                if (n > 0) r.MeanTimingMs = (float)(sum / n * 1000.0);
            }
            return r;
        }

        void UpdateFrame()
        {
            PracticeFrame f = PracticeFrame.Idle;
            if (_song != null) f.BeatsPerBar = Math.Max(1, _song.BeatsPerBar);
            if (IsRunning)
            {
                IReadOnlyList<ChordSpan> spans = _timeline.Spans;
                double pickup = _song.PickupBeats;
                f.Active = true;
                f.BeatsPerSecond = (float)(1.0 / _spb);
                f.StrumSlots = _strumSlots;
                f.SlotsPerBeat = _strumSlots != null ? _slotsPerBeat : 1;
                if (_phase == PlayAlongPhase.CountIn)
                {
                    double coreBeat = (_now - _countInSec) / _spb;    // negative: before the first chord
                    f.Beat = PlayAlongMath.FrameBeat(coreBeat, pickup);
                    f.NextChord = spans[0].Chord;
                    f.BeatsToChange = Math.Max(0, -coreBeat);
                }
                else
                {
                    double coreBeat = _target.PositionSeconds / _spb;
                    f.Beat = PlayAlongMath.FrameBeat(coreBeat, pickup);
                    int span = _target.CurrentIndex;
                    if (span >= 0 && span < spans.Count)
                    {
                        f.Chord = spans[span].Chord;
                        int next = _nextDifferent[span];
                        if (next >= 0)
                        {
                            f.NextChord = spans[next].Chord;
                            f.BeatsToChange = Math.Max(0, spans[next].StartBeat - coreBeat);
                        }
                    }
                    f.Waiting = _target.Holding;
                    f.ChordConfirmed = span >= 0 && span == _confirmedSpan;
                }
            }
            _frame = f;
        }
    }
}
