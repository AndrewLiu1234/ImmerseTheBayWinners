// LearnContracts: the two small seams of learn mode (SL1-SL3, Person 1's UI half).
//
//   verdict source --(ILearnVerdictSink)--> LearnFlow (pure state machine) --(ILearnView)--> LearnPresenter (HUD, neck, hand)
//
// A verdict source is TEMPORARY Dev/DevLearnScript today (a scripted, simulated run). When Core's tuner (YIN), pluck check
// and strum check land (Person 2), a small adapter maps Core's verdict events onto ILearnVerdictSink; nothing else changes.
// Display-side types only: the marks and icons are the Learn HUD's (Runtime/Hud/Learn/LearnTypes.cs), whose summaries hold
// the suggested mapping from Core's verdict classes. Main thread only.
using UntitledGuitarApp.Runtime.Hud.Learn;

namespace UntitledGuitarApp.Runtime.Practice.Learn
{
    /// <summary>The stages of learning one chord, in order (docs/MASTER_PLAN.md: tune, finger by finger, pluck check, strum check).</summary>
    public enum LearnStage
    {
        /// <summary>Learn mode is off: the neck shows the chord as usual.</summary>
        Idle = 0,
        /// <summary>Tune each string, 6 down to 1, with the needle.</summary>
        Tune = 1,
        /// <summary>Place the fingers one at a time.</summary>
        FingerByFinger = 2,
        /// <summary>Pluck each sounding string: one mark per string, plus one tip.</summary>
        PluckCheck = 3,
        /// <summary>Strum the whole chord: a check, or its top issue.</summary>
        StrumCheck = 4,
        /// <summary>The chord rang clean: the fretting ghost fades (ghost.fadeAfterSuccess).</summary>
        Done = 5,
    }

    /// <summary>
    /// What a verdict source tells learn mode. Every call is ignored unless it fits the current stage (a reading while
    /// nothing is being tuned, a finger that is not the one asked for, a strum during the pluck check), so a source never
    /// needs to know the stage. Strings are numbered 1 (high E) to 6 (low E); fingers 1 (index) to 4 (little).
    /// </summary>
    public interface ILearnVerdictSink
    {
        /// <summary>A pitch reading for the string being tuned: cents from its target (negative = flat) and confidence 0..1.</summary>
        void OnTunerReading(float cents, float confidence);

        /// <summary>The string being tuned is in tune: move to the next one (ignored for any other string).</summary>
        void OnStringInTune(int stringNumber);

        /// <summary>The finger asked for is in place (for example its string was plucked at the right pitch).</summary>
        void OnFingerPlaced(int finger);

        /// <summary>The verdict on one plucked string, with an optional tip (null or empty keeps the current message).</summary>
        void OnPluckResult(int stringNumber, StringMark mark, string tip, TipIcon icon);

        /// <summary>The verdict on one strum of the whole chord: clean, or its single top issue.</summary>
        void OnStrumResult(bool clean, string issue, TipIcon icon);
    }

    /// <summary>
    /// What learn mode asks the screen to do. LearnPresenter draws it with the Learn HUD, the floating neck and the ghost hand;
    /// tests record it. Stage changes come as a Leave of the old stage, then an Enter of the new one.
    /// </summary>
    public interface ILearnView
    {
        void EnterStage(LearnStage stage, LearnChord chord);
        void LeaveStage(LearnStage stage, LearnChord chord);
        void ShowTunerTarget(int stringNumber, string noteName);
        void ShowTunerReading(float cents, float confidence);
        void ShowFingerStep(int finger, int stringNumber, int fret, int step, int stepCount);
        void ShowStringMark(int stringNumber, StringMark mark);
        void ShowTip(string tip, TipIcon icon);
        void ShowStrum(bool clean, string issue, TipIcon icon);
    }
}
