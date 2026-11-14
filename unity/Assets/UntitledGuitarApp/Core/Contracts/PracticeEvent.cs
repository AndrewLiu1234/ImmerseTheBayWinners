using UntitledGuitarApp.Core.Theory;

namespace UntitledGuitarApp.Core.Contracts
{
    /// <summary>
    /// Something the HUD, ghost hands, approach rings or logger must react to. Emitted by an <see cref="IPracticeSession"/>
    /// on the main thread, synchronously inside OnAudio/Tick/OnCommand. Dispatch with a type switch
    /// (<c>case ChordTarget c:</c>). Immutable. Allocated per event (a few per second), never per frame.
    /// New kinds may be added (additive); consumers must ignore kinds they don't know.
    /// </summary>
    public abstract class PracticeEvent
    {
        /// <summary>Session-clock seconds when the event takes effect (same clock as <see cref="IPracticeSession.Tick"/>).</summary>
        public double TimeSec { get; }

        protected PracticeEvent(double timeSec) { TimeSec = timeSec; }
    }

    /// <summary>
    /// The chord to hold now and the one coming. Emitted on every change and on every beat while timed, so
    /// <see cref="BeatsToChange"/> counts down. The fretting ghost morphs to <see cref="Next"/> when BeatsToChange ≤ 1
    /// (one beat early, GHOST_HANDS.md); approach rings close as it reaches 0.
    /// </summary>
    public sealed class ChordTarget : PracticeEvent
    {
        public ChordShape Current { get; }
        /// <summary>Null at the end of the song or in untimed Learn mode.</summary>
        public ChordShape Next { get; }
        /// <summary>Beats until <see cref="Next"/> is due, ≥ 0; 0 when there's no Next.</summary>
        public double BeatsToChange { get; }
        /// <summary>Seconds per beat at the current tempo preset (lets Runtime interpolate between events); 0 = untimed.</summary>
        public double SecondsPerBeat { get; }
        /// <summary>Wait mode is holding the playhead until the player plays <see cref="Next"/> (pattern loops slowly).</summary>
        public bool Holding { get; }

        public ChordTarget(double timeSec, ChordShape current, ChordShape next, double beatsToChange, double secondsPerBeat, bool holding)
            : base(timeSec)
        {
            Current = current; Next = next; BeatsToChange = beatsToChange; SecondsPerBeat = secondsPerBeat; Holding = holding;
        }
    }

    /// <summary>Finger-by-finger build: this finger (of the current <see cref="ChordTarget.Current"/>) at full brightness with its number, the others at 30%.</summary>
    public sealed class ActiveFinger : PracticeEvent
    {
        /// <summary>1 = index … 4 = pinky; 0 = whole chord (all fingers at full brightness).</summary>
        public int Finger { get; }

        public ActiveFinger(double timeSec, int finger) : base(timeSec) { Finger = finger; }
    }

    /// <summary>Which pattern a <see cref="PatternSlot"/> belongs to (song JSON <c>strum</c> or <c>pick</c>).</summary>
    public enum PatternKind { Strum = 0, Pick = 1 }

    /// <summary>What the strumming ghost does in one slot.</summary>
    public enum Stroke
    {
        /// <summary>"-": no stroke; the cursor still advances.</summary>
        Rest = 0,
        /// <summary>"D": sweep string 6 → 1.</summary>
        Down = 1,
        /// <summary>"U": sweep back over the top 3–4 strings.</summary>
        Up = 2,
        /// <summary>A digit or "B": pick one string (<see cref="PatternSlot.StringNumber"/>).</summary>
        Pick = 3,
    }

    /// <summary>
    /// One pattern slot starting now. Drives the strumming ghost, the strum-arrow / picking strip cursor and the
    /// "next string glows" cue. Emitted for every slot, rests included; also in wait-mode loops.
    /// </summary>
    public sealed class PatternSlot : PracticeEvent
    {
        public PatternKind Pattern { get; }
        /// <summary>Index into the song's <c>slots</c> array (0-based).</summary>
        public int Index { get; }
        public Stroke Stroke { get; }
        /// <summary>For <see cref="Stroke.Pick"/>: the string, 1 = high E … 6 = low E, with "B" already resolved to the chord's bass string. Otherwise 0.</summary>
        public int StringNumber { get; }
        /// <summary>Slot length in seconds at the current tempo (stroke animation time budget).</summary>
        public double DurationSec { get; }

        public PatternSlot(double timeSec, PatternKind pattern, int index, Stroke stroke, int stringNumber, double durationSec)
            : base(timeSec)
        {
            Pattern = pattern; Index = index; Stroke = stroke; StringNumber = stringNumber; DurationSec = durationSec;
        }
    }

    /// <summary>
    /// The session's audio hypothesis changed. Runtime must hand <see cref="Expectation"/> to the audio worker
    /// (<see cref="IAudioAnalyzer.Process"/>); the HUD may use it ("pluck string 2").
    /// </summary>
    public sealed class ListenFor : PracticeEvent
    {
        public AudioExpectation Expectation { get; }

        public ListenFor(double timeSec, AudioExpectation expectation) : base(timeSec) { Expectation = expectation; }
    }

    /// <summary>Pluck-check verdict for one string: ✓/✕ on that string and, if wrong, a tip.</summary>
    public sealed class StringVerdict : PracticeEvent
    {
        public StringResult Result { get; }
        /// <summary>Ready-to-show English tip from <c>Practice/Tips</c> ("Arch finger 3, B string muted"); null when correct.</summary>
        public string TipText { get; }

        public StringVerdict(double timeSec, StringResult result, string tipText) : base(timeSec) { Result = result; TipText = tipText; }
    }

    /// <summary>Timing judgement of a strum against its scheduled slot (MASTER_PLAN §19 Timing).</summary>
    public enum TimingVerdict
    {
        /// <summary>Untimed (Learn mode) or no slot to compare with.</summary>
        NotJudged = 0,
        /// <summary>Within ±120 ms (scaled by tempo).</summary>
        OnTime = 1,
        /// <summary>Within ±250 ms (scaled by tempo).</summary>
        Close = 2,
        /// <summary>Outside the close window.</summary>
        Off = 3,
    }

    /// <summary>Strum-check verdict for the whole chord (✓ or its top chord-level issue), with timing when play-along.</summary>
    public sealed class StrumVerdict : PracticeEvent
    {
        public StrumResult Result { get; }
        /// <summary>Ready-to-show English tip; null when clean.</summary>
        public string TipText { get; }
        public TimingVerdict Timing { get; }
        /// <summary>Onset minus scheduled time after the latency offset, ms (− = early); 0 when <see cref="Timing"/> is NotJudged.</summary>
        public double TimingOffsetMs { get; }

        public StrumVerdict(double timeSec, StrumResult result, string tipText, TimingVerdict timing, double timingOffsetMs)
            : base(timeSec)
        {
            Result = result; TipText = tipText; Timing = timing; TimingOffsetMs = timingOffsetMs;
        }
    }

    /// <summary>The session finished (song end, chord learned, drill over). Aggregated stats only (also the AI coach input, SL10).</summary>
    public sealed class SessionEnded : PracticeEvent
    {
        /// <summary>Checks attempted (strums in play-along, chord attempts in Learn).</summary>
        public int Attempts { get; }
        /// <summary>Attempts judged clean/correct.</summary>
        public int Clean { get; }
        /// <summary>Share of timed strums that were <see cref="TimingVerdict.OnTime"/>, 0 … 1; 0 when untimed.</summary>
        public double OnTimeFraction { get; }

        public SessionEnded(double timeSec, int attempts, int clean, double onTimeFraction) : base(timeSec)
        {
            Attempts = attempts; Clean = clean; OnTimeFraction = onTimeFraction;
        }
    }
}
