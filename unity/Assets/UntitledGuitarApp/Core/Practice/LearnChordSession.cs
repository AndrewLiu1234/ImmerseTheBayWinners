using System;
using System.Collections.Generic;
using UntitledGuitarApp.Core.Audio;
using UntitledGuitarApp.Core.Theory;

namespace UntitledGuitarApp.Core.Practice
{
    /// <summary>Tunables for Learn mode. Each number says why it exists.</summary>
    public sealed class LearnChordConfig
    {
        /// <summary>The finger stage only confirms the finger lands on the right fret; ring quality is judged in the pluck check,
        /// so a right-pitch-but-short pluck (ShortRing) still advances the finger stage (with its tip shown).</summary>
        public bool FingerStageAcceptsShortRing = true;
        /// <summary>After this many faulty plucks on one step the HUD may offer "skip" (3 = the wait-mode escape hatch, PRD).</summary>
        public int StuckAfterAttempts = 3;
        /// <summary>…or after this long on one step, s (20 s: a beginner placing one finger takes 5–10 s; 20 s means stuck).</summary>
        public double StuckAfterSec = 20.0;
    }

    public enum LearnStage
    {
        /// <summary>One fretted note at a time (ghost hand: active finger bright, the rest at 30 %); pluck that string.</summary>
        FingerByFinger,
        /// <summary>Whole shape held; pluck every played string, low → high; each must be Correct (incl. ring-out).</summary>
        PluckCheck,
        /// <summary>Strum the whole chord. The strum check is SL3: the caller reports its verdict via <see cref="LearnChordSession.OnStrumVerdict"/>.</summary>
        WholeChord,
        Mastered,
    }

    /// <summary>One step of the finger or pluck stage.</summary>
    public readonly struct LearnStep
    {
        public readonly LearnStage Stage;
        /// <summary>String to pluck, 1 = high E … 6 = low E.</summary>
        public readonly int String;
        /// <summary>Fret on that string (0 = open).</summary>
        public readonly int Fret;
        /// <summary>Finger to place (1 = index … 4 = pinky); 0 for an open string.</summary>
        public readonly int Finger;
        public readonly int ExpectedMidi;
        public LearnStep(LearnStage stage, int stringNumber, int fret, int finger, int expectedMidi)
        { Stage = stage; String = stringNumber; Fret = fret; Finger = finger; ExpectedMidi = expectedMidi; }
    }

    /// <summary>What the HUD and the ghost hand show after each input.</summary>
    public readonly struct LearnChordState
    {
        public readonly string Chord;
        public readonly LearnStage Stage;
        /// <summary>Finger to highlight on the ghost hand (PracticeEvent ActiveFinger); 0 = none (open string, strum, mastered).</summary>
        public readonly int ActiveFinger;
        /// <summary>String the player should pluck now; 0 outside the pluck stages.</summary>
        public readonly int ActiveString;
        public readonly int ExpectedMidi;
        /// <summary>Step within the current stage and the stage's step count.</summary>
        public readonly int StepIndex, StepCount;
        /// <summary>The verdict of the last accepted input (pluck verdict; for WholeChord Correct = clean strum).</summary>
        public readonly bool HasLastVerdict;
        public readonly PluckVerdict LastVerdict;
        public readonly string LastTip, LastTipKey;
        /// <summary>The last input advanced to the next step or stage.</summary>
        public readonly bool Advanced;
        /// <summary>Faulty inputs on this step (Unclear doesn't count: it is never a fault).</summary>
        public readonly int Attempts;
        /// <summary>The step looks stuck (see <see cref="LearnChordConfig"/>): offer "skip".</summary>
        public readonly bool Stuck;
        /// <summary>Seconds since the session started (caller clock).</summary>
        public readonly double ElapsedSec;

        public LearnChordState(string chord, LearnStage stage, int activeFinger, int activeString, int expectedMidi, int stepIndex,
                               int stepCount, bool hasLast, PluckVerdict last, string tip, string tipKey, bool advanced, int attempts,
                               bool stuck, double elapsed)
        {
            Chord = chord; Stage = stage; ActiveFinger = activeFinger; ActiveString = activeString; ExpectedMidi = expectedMidi;
            StepIndex = stepIndex; StepCount = stepCount; HasLastVerdict = hasLast; LastVerdict = last; LastTip = tip;
            LastTipKey = tipKey; Advanced = advanced; Attempts = attempts; Stuck = stuck; ElapsedSec = elapsed;
        }
    }

    /// <summary>
    /// Learn one chord (PRD "Learn a chord"): finger by finger (each fretted note, low string first, confirmed by a pluck
    /// check of that string) → pluck check of every played string low → high (✓/✕ + tip per string, ring-out included) →
    /// whole-chord strum (SL3 hook) → mastered. Audio-free: the caller runs <see cref="PluckVerifier"/> on the
    /// <see cref="ActiveString"/>/<see cref="ExpectedMidi"/> this session asks for and passes the result in. Time is the caller's
    /// clock in seconds (non-decreasing; earlier values are clamped). Deterministic. Main-thread object.
    /// </summary>
    public sealed class LearnChordSession
    {
        readonly LearnChordConfig _cfg;
        readonly List<LearnStep> _fingerSteps = new List<LearnStep>();
        readonly List<LearnStep> _pluckSteps = new List<LearnStep>();
        readonly bool[] _hasVerdict = new bool[Tuning.StringCount + 1];
        readonly PluckVerdict[] _verdict = new PluckVerdict[Tuning.StringCount + 1];
        readonly string[] _tip = new string[Tuning.StringCount + 1];
        readonly double _startSec;
        double _now, _stepStartSec;
        int _step, _attempts;
        bool _hasLast, _advanced;
        PluckVerdict _last;
        string _lastTip, _lastKey;

        public ChordShape Chord { get; }
        public LearnStage Stage { get; private set; }
        public IReadOnlyList<LearnStep> FingerSteps => _fingerSteps;
        public IReadOnlyList<LearnStep> PluckSteps => _pluckSteps;

        public LearnChordSession(ChordShape chord, double nowSec, LearnChordConfig config = null)
        {
            Chord = chord ?? throw new ArgumentNullException(nameof(chord));
            _cfg = config ?? new LearnChordConfig();
            foreach (var n in chord.FrettedNotes())
                _fingerSteps.Add(new LearnStep(LearnStage.FingerByFinger, n.String, n.Fret, n.Finger, chord.MidiOn(n.String)));
            for (int s = Tuning.StringCount; s >= 1; s--)
                if (chord.IsPlayed(s))
                    _pluckSteps.Add(new LearnStep(LearnStage.PluckCheck, s, chord.FretOn(s), chord.FingerOn(s), chord.MidiOn(s)));
            _startSec = _now = _stepStartSec = Finite(nowSec, 0);
            Stage = _fingerSteps.Count > 0 ? LearnStage.FingerByFinger : LearnStage.PluckCheck;   // an all-open chord skips the finger stage
        }

        /// <summary>The stage takes plucks (FingerByFinger or PluckCheck).</summary>
        public bool ExpectsPluck => Stage == LearnStage.FingerByFinger || Stage == LearnStage.PluckCheck;
        LearnStep? Current => Stage == LearnStage.FingerByFinger ? _fingerSteps[_step]
                            : Stage == LearnStage.PluckCheck ? _pluckSteps[_step] : (LearnStep?)null;
        /// <summary>String the player should pluck now (0 when no pluck is expected).</summary>
        public int ActiveString => Current?.String ?? 0;
        /// <summary>MIDI the active string should give (−1 when no pluck is expected).</summary>
        public int ExpectedMidi => Current?.ExpectedMidi ?? -1;
        /// <summary>Finger to highlight (0 = none).</summary>
        public int ActiveFinger => Current?.Finger ?? 0;

        /// <summary>Pluck-check verdict per string (1–6) from the PluckCheck stage, or null if not checked yet.</summary>
        public PluckVerdict? VerdictFor(int stringNumber)
        {
            Tuning.CheckString(stringNumber);
            return _hasVerdict[stringNumber] ? _verdict[stringNumber] : (PluckVerdict?)null;
        }

        /// <summary>The tip shown under string <paramref name="stringNumber"/> after its last pluck-check verdict (null if none/Correct).</summary>
        public string TipFor(int stringNumber) { Tuning.CheckString(stringNumber); return _tip[stringNumber]; }

        public LearnChordState State => Snapshot();

        /// <summary>A pluck verdict for the active string. Results for another string (stale window) are ignored.</summary>
        public LearnChordState OnPluck(in PluckResult r, double nowSec)
        {
            Clock(nowSec);
            _advanced = false;
            var cur = Current;
            if (cur == null || r.String != cur.Value.String) return Snapshot();

            _hasLast = true; _last = r.Verdict;
            _lastTip = Tips.ForPluck(r, Chord);
            _lastKey = r.TipKey;
            if (Stage == LearnStage.PluckCheck)
            {
                _hasVerdict[r.String] = true; _verdict[r.String] = r.Verdict; _tip[r.String] = _lastTip;
            }
            bool pass = r.Verdict == PluckVerdict.Correct
                        || (Stage == LearnStage.FingerByFinger && r.Verdict == PluckVerdict.ShortRing && _cfg.FingerStageAcceptsShortRing);
            if (pass) Advance();
            else if (r.Verdict != PluckVerdict.Unclear) _attempts++;
            return Snapshot();
        }

        /// <summary>SL3 hook: the strum check's chord-level verdict for the WholeChord stage. Clean → Mastered.</summary>
        public LearnChordState OnStrumVerdict(bool clean, double nowSec, string tip = null, string tipKey = null)
        {
            Clock(nowSec);
            _advanced = false;
            if (Stage != LearnStage.WholeChord) return Snapshot();
            _hasLast = true; _last = clean ? PluckVerdict.Correct : PluckVerdict.Unclear;
            _lastTip = clean ? null : tip; _lastKey = clean ? null : tipKey;
            if (clean) Advance(); else _attempts++;
            return Snapshot();
        }

        /// <summary>Escape hatch (gaze button / space bar): move on from the current step.</summary>
        public LearnChordState Skip(double nowSec)
        {
            Clock(nowSec);
            _advanced = false;
            if (Stage != LearnStage.Mastered) Advance();
            return Snapshot();
        }

        /// <summary>Advance the clock (for <see cref="LearnChordState.Stuck"/> by time).</summary>
        public LearnChordState Tick(double nowSec) { Clock(nowSec); _advanced = false; return Snapshot(); }

        void Advance()
        {
            _advanced = true;
            _attempts = 0;
            _stepStartSec = _now;
            _step++;
            if (Stage == LearnStage.FingerByFinger && _step >= _fingerSteps.Count) { Stage = LearnStage.PluckCheck; _step = 0; }
            else if (Stage == LearnStage.PluckCheck && _step >= _pluckSteps.Count) { Stage = LearnStage.WholeChord; _step = 0; }
            else if (Stage == LearnStage.WholeChord) { Stage = LearnStage.Mastered; _step = 0; }
        }

        void Clock(double nowSec) { double t = Finite(nowSec, _now); if (t > _now) _now = t; }

        static double Finite(double v, double fallback) => double.IsNaN(v) || double.IsInfinity(v) ? fallback : v;

        LearnChordState Snapshot()
        {
            var cur = Current;
            int count = Stage == LearnStage.FingerByFinger ? _fingerSteps.Count : Stage == LearnStage.PluckCheck ? _pluckSteps.Count
                      : Stage == LearnStage.WholeChord ? 1 : 0;
            bool stuck = Stage != LearnStage.Mastered &&
                         (_attempts >= _cfg.StuckAfterAttempts || _now - _stepStartSec >= _cfg.StuckAfterSec);
            return new LearnChordState(Chord.Name, Stage, cur?.Finger ?? 0, cur?.String ?? 0, cur?.ExpectedMidi ?? -1,
                                       Stage == LearnStage.Mastered ? 0 : _step, count, _hasLast, _last, _lastTip, _lastKey,
                                       _advanced, _attempts, stuck, _now - _startSec);
        }
    }
}
