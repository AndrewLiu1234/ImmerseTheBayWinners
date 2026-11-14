// TEMPORARY (dev play-along driver). Plays a DevSong into Runtime/Practice/PracticeFrame, the seam the ghost hands and the
// play HUD read, until Core's PlayAlongSession (Person 2) lands; then an adapter fills the same struct and this goes.
//
// FLOW. Start -> count-in (one bar on negative beats, before the first chord) -> chords -> end with results.
// Tick(dt) moves Beat by dt * BeatsPerSecond (tempoBpm / 60 * tempo preset).
// WAIT MODE (flag waitModeDefault): at every chord change, the first chord included, the playhead stops ON the change
// (Beat = the change beat, Chord = the new chord, Waiting = true) until the chord is confirmed; then it moves on.
// CONFIRMATION. The real verdict will come from Core's strum check through ConfirmChord. Nothing produces one yet (and the
// "safe" flag profile replays a WAV file that does not exist), so by default a SIMULATED confirmation arrives
// SimulatedConfirmSeconds after each chord starts, waiting or not. Results built on it are labelled Simulated.
// The SIMULATED badge (Hud/SimulatedBadge) already shows whenever audioSource is not "mic".
//
// Plain C#: no MonoBehaviour, no engine calls except FlagStore in StartFromFlags. Tick allocates nothing.
using System;
using UntitledGuitarApp.Runtime.Flags;

namespace UntitledGuitarApp.Runtime.Practice.Dev
{
    /// <summary>TEMPORARY. Where the dev clock is.</summary>
    public enum DevPlayState
    {
        /// <summary>No song (never started, or stopped).</summary>
        Idle = 0,
        /// <summary>The one-bar count-in before the first chord.</summary>
        CountIn = 1,
        /// <summary>Chords are sounding (wait-mode holds included).</summary>
        Playing = 2,
        /// <summary>The last chord ended; <see cref="DevPlayAlongClock.Results"/> holds the score.</summary>
        Ended = 3,
    }

    /// <summary>TEMPORARY. The score of one run through a song.</summary>
    public struct DevPlayResults
    {
        /// <summary>Chords confirmed while they were sounding.</summary>
        public int ChordsHit;

        /// <summary>Chords in the song (chord runs: each one is a change to play).</summary>
        public int ChordsTotal;

        /// <summary>Mean timing of the confirmations, ms, + = late; NaN = none measured.</summary>
        public float MeanTimingMs;

        /// <summary>True when any confirmation was simulated (no audio verdict): show it as such.</summary>
        public bool Simulated;
    }

    /// <summary>TEMPORARY dev driver of <see cref="PracticeFrame"/>. Main thread (or any one thread); not thread-safe.</summary>
    public sealed class DevPlayAlongClock
    {
        /// <summary>The tempo preset used when nothing else is asked for (75%).</summary>
        public const float DefaultTempoPreset = 0.75f;

        /// <summary>Default delay of a simulated confirmation after a chord starts, seconds.</summary>
        public const float DefaultSimulatedConfirmSeconds = 0.6f;

        /// <summary>Default timing reported for a simulated confirmation, ms (+ = late). Labelled simulated wherever shown.</summary>
        public const float DefaultSimulatedTimingMs = 40f;

        /// <summary>Delay of a simulated confirmation after each chord starts, seconds (real time, wait holds included).</summary>
        public float SimulatedConfirmSeconds = DefaultSimulatedConfirmSeconds;

        /// <summary>Timing recorded for each simulated confirmation, ms.</summary>
        public float SimulatedTimingMs = DefaultSimulatedTimingMs;

        /// <summary>True (default): confirm each chord by simulation. Set false once a real verdict calls <see cref="ConfirmChord"/>.</summary>
        public bool SimulateConfirmation = true;

        DevSong _song;
        DevPlayState _state;
        float _preset = DefaultTempoPreset;
        float _bps;
        bool _waitMode;
        double _beat;
        int _run = -1;
        bool _waiting;
        bool _confirmed;
        float _runSeconds;
        float _waitSeconds;
        int _hits;
        int _timed;
        double _timingSum;
        int _simulated;
        DevPlayResults _results;
        PracticeFrame _frame = PracticeFrame.Idle;

        /// <summary>The song being played (null when idle).</summary>
        public DevSong Song { get { return _song; } }

        /// <summary>Where the clock is.</summary>
        public DevPlayState State { get { return _state; } }

        /// <summary>True from Start to the end of the last chord.</summary>
        public bool IsRunning { get { return _state == DevPlayState.CountIn || _state == DevPlayState.Playing; } }

        /// <summary>The tempo preset in use (0.75 = 75%).</summary>
        public float TempoPreset { get { return _preset; } }

        /// <summary>Whether wait mode is on for this run.</summary>
        public bool WaitMode { get { return _waitMode; } }

        /// <summary>Beats per second after the preset.</summary>
        public float BeatsPerSecond { get { return _bps; } }

        /// <summary>Song position in beats (negative in the count-in).</summary>
        public double Beat { get { return _beat; } }

        /// <summary>The playhead in seconds on the song clock (Beat / BeatsPerSecond): it stops while wait mode holds.</summary>
        public double SongSeconds { get { return _bps > 0f ? _beat / _bps : 0; } }

        /// <summary>Index into Song.Runs of the chord sounding now; -1 in the count-in.</summary>
        public int RunIndex { get { return _run; } }

        /// <summary>True while wait mode holds the playhead.</summary>
        public bool Waiting { get { return _waiting; } }

        /// <summary>Seconds the current hold has lasted (0 when not waiting).</summary>
        public float WaitSeconds { get { return _waiting ? _waitSeconds : 0f; } }

        /// <summary>The score once <see cref="State"/> is Ended (and the score so far before that).</summary>
        public DevPlayResults Results { get { return _state == DevPlayState.Ended ? _results : CurrentResults(); } }

        /// <summary>This frame's practice state (a copy; StrumSlots is shared and read-only).</summary>
        public PracticeFrame Frame { get { return _frame; } }

        /// <summary>Copies this frame's practice state into <paramref name="frame"/> (no allocation).</summary>
        public void FillFrame(ref PracticeFrame frame)
        {
            frame = _frame;
        }

        /// <summary>
        /// Starts <paramref name="song"/> with the flags as they are now (read once, here, not per frame): the tempo preset is
        /// the one in FlagStore.Current.tempoPresets nearest to <paramref name="wantedPreset"/>, wait mode is waitModeDefault.
        /// </summary>
        public bool StartFromFlags(DevSong song, float wantedPreset)
        {
            FlagSet flags = FlagStore.Current;
            float preset = DevTimeline.NearestPreset(wantedPreset, flags != null ? flags.tempoPresets : null);
            bool wait = flags == null || flags.waitModeDefault;
            return Start(song, preset, wait);
        }

        /// <summary>Starts <paramref name="song"/> at the count-in. False (and Idle) when the song is null or has no chords.</summary>
        public bool Start(DevSong song, float tempoPreset, bool waitMode)
        {
            Stop();
            if (song == null || song.Runs == null || song.Runs.Length == 0) return false;
            float preset = tempoPreset > 0f && !float.IsInfinity(tempoPreset) ? tempoPreset : DefaultTempoPreset;
            float bps = DevTimeline.BeatsPerSecond(song.TempoBpm, preset);
            if (!(bps > 0f)) return false;

            _song = song;
            _preset = preset;
            _bps = bps;
            _waitMode = waitMode;
            _beat = song.CountInStartBeat;
            _run = -1;
            _state = DevPlayState.CountIn;
            UpdateFrame();
            return true;
        }

        /// <summary>Back to Idle; the frame becomes PracticeFrame.Idle.</summary>
        public void Stop()
        {
            _song = null;
            _state = DevPlayState.Idle;
            _bps = 0f;
            _beat = 0;
            _run = -1;
            _waiting = false;
            _confirmed = false;
            _runSeconds = 0f;
            _waitSeconds = 0f;
            _hits = 0;
            _timed = 0;
            _timingSum = 0;
            _simulated = 0;
            _results = default(DevPlayResults);
            _frame = PracticeFrame.Idle;
        }

        /// <summary>
        /// The verdict seam: the chord sounding now was played (<paramref name="timingMs"/> from the change, + = late; NaN = not
        /// measured). Releases a wait-mode hold. Ignored in the count-in, when idle or when the chord is already confirmed.
        /// </summary>
        public void ConfirmChord(float timingMs)
        {
            Confirm(timingMs, false);
        }

        /// <summary>Advances the clock by <paramref name="deltaSeconds"/> of real time. No allocation.</summary>
        public void Tick(float deltaSeconds)
        {
            if (!IsRunning) return;
            float dt = deltaSeconds > 0f && !float.IsInfinity(deltaSeconds) ? deltaSeconds : 0f;   // NaN, negative -> 0

            if (_run >= 0) _runSeconds += dt;
            if (_waiting)
            {
                _waitSeconds += dt;
                if (SimulateConfirmation && !_confirmed && _runSeconds >= SimulatedConfirmSeconds) Confirm(SimulatedTimingMs, true);
                UpdateFrame();
                return;   // the playhead moves again from the next tick
            }
            if (SimulateConfirmation && _run >= 0 && !_confirmed && _runSeconds >= SimulatedConfirmSeconds) Confirm(SimulatedTimingMs, true);

            DevChordRun[] runs = _song.Runs;
            double target = _beat + dt * (double)_bps;
            while (true)
            {
                int next = _run + 1;
                double boundary = next < runs.Length ? runs[next].StartBeat : _song.EndBeat;
                if (target < boundary - DevTimeline.Epsilon)
                {
                    _beat = target;
                    break;
                }
                if (next >= runs.Length)
                {
                    _beat = _song.EndBeat;
                    Finish();
                    break;
                }
                EnterRun(next);
                if (_waitMode)
                {
                    _beat = boundary;
                    _waiting = true;
                    _waitSeconds = 0f;
                    break;
                }
            }
            UpdateFrame();
        }

        void EnterRun(int index)
        {
            _run = index;
            _state = DevPlayState.Playing;
            _confirmed = false;
            _runSeconds = 0f;
        }

        void Confirm(float timingMs, bool simulated)
        {
            if (_state != DevPlayState.Playing || _run < 0 || _confirmed) return;
            _confirmed = true;
            _hits++;
            if (simulated) _simulated++;
            if (!float.IsNaN(timingMs) && !float.IsInfinity(timingMs))
            {
                _timingSum += timingMs;
                _timed++;
            }
            if (_waiting)
            {
                _waiting = false;
                _waitSeconds = 0f;
            }
            UpdateFrame();
        }

        void Finish()
        {
            _results = CurrentResults();
            _state = DevPlayState.Ended;
            _waiting = false;
            _run = _song != null ? _song.Runs.Length : -1;
        }

        DevPlayResults CurrentResults()
        {
            DevPlayResults r;
            r.ChordsHit = _hits;
            r.ChordsTotal = _song != null ? _song.Runs.Length : 0;
            r.MeanTimingMs = _timed > 0 ? (float)(_timingSum / _timed) : float.NaN;
            r.Simulated = _simulated > 0;
            return r;
        }

        void UpdateFrame()
        {
            PracticeFrame f = PracticeFrame.Idle;
            if (_song != null) f.BeatsPerBar = Math.Max(1, _song.BeatsPerBar);
            if (IsRunning)
            {
                DevChordRun[] runs = _song.Runs;
                f.Active = true;
                f.BeatsPerSecond = _bps;
                f.Beat = _beat;
                f.Chord = _run >= 0 && _run < runs.Length ? runs[_run].Chord : null;
                int next = _run + 1;
                f.NextChord = next >= 0 && next < runs.Length ? runs[next].Chord : null;
                f.BeatsToChange = DevTimeline.BeatsToNextChange(runs, _run, _beat);
                f.Waiting = _waiting;
                f.ChordConfirmed = _confirmed;
                f.StrumSlots = _song.StrumSlots;
                f.SlotsPerBeat = _song.StrumSlots != null ? Math.Max(1, _song.SlotsPerBeat) : 1;
            }
            _frame = f;
        }
    }
}
