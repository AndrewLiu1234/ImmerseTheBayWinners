// TEMPORARY until Core's verdicts land (tuner YIN, pluck check, strum check; Person 2). SIMULATED learn run for a live demo.
// Pure: the ordered, timed list of verdict events DevLearnScript plays into a LearnPresenter, derived from the chord's shape
// (works for G, C, D, Em, Am), plus named moments for previews. Unit-tested in LearnFlowTests.
//
//   tune     each string 6 .. 1: the needle sweeps in from about 20-30 cents off (1.3 s), rests in tune (0.8 s), next string
//   fingers  the chord's fingers, finger 1 first, 2.6 s each
//   pluck    every sounding string, lowest first, 0.75 s apart, with ONE deliberate mistake: an open string muted by the finger
//            beside it (G: "Arch finger 3, B string muted", the demo script's mistake); 3.2 s to read the tip; then a
//            clean retry pass (0.6 s apart). LearnFlow holds the all-correct pass 1.5 s, then starts the strum check
//   strum    2 s for the prompt, then one clean strum; the run ends 2.5 s later
using System.Collections.Generic;
using UnityEngine;
using UntitledGuitarApp.Runtime.Hud.Learn;

namespace UntitledGuitarApp.Runtime.Practice.Learn.Dev
{
    public enum DevLearnEventKind
    {
        /// <summary>Feed tuner readings for <c>duration</c> seconds: a sweep into tune, then a rest in tune.</summary>
        TuneSweep = 0,
        StringInTune = 1,
        FingerPlaced = 2,
        Pluck = 3,
        Strum = 4,
        /// <summary>The run is over (nothing is sent).</summary>
        End = 5,
    }

    public struct DevLearnEvent
    {
        public float time;
        public DevLearnEventKind kind;
        public LearnStage stage;
        public int stringNumber;
        public int finger;
        public StringMark mark;
        public string tip;
        public TipIcon icon;
        public float startCents;     // TuneSweep: where the needle starts
        public float duration;       // TuneSweep: sweep + rest
        public bool clean;           // Strum
        public string issue;         // Strum
    }

    /// <summary>A named instant of the run (for preview renders and tests).</summary>
    public struct DevLearnMoment
    {
        public string name;
        public float time;
        public LearnStage stage;

        public DevLearnMoment(string name, float time, LearnStage stage)
        {
            this.name = name;
            this.time = time;
            this.stage = stage;
        }
    }

    public sealed class DevLearnTimeline
    {
        public const float LeadIn = 0.6f;
        public const float SweepSeconds = 1.3f;
        public const float RestInTuneSeconds = 0.8f;
        public const float NextStringGap = 0.3f;
        public const float FingerSeconds = 2.6f;
        public const float PluckPromptSeconds = 1.8f;
        public const float PluckInterval = 0.75f;
        public const float TipReadSeconds = 3.2f;
        public const float RetryInterval = 0.6f;
        public const float StrumPromptSeconds = 2.0f;
        public const float EndHold = 2.5f;
        public const float Confidence = 0.95f;

        // Where the needle starts for strings 1 .. 6: alternately sharp and flat, never the same twice in a row.
        static readonly float[] s_StartCents = { 16f, -24f, 26f, -18f, 22f, -30f };

        readonly List<DevLearnEvent> _events = new List<DevLearnEvent>(64);
        readonly List<DevLearnMoment> _moments = new List<DevLearnMoment>(16);
        readonly float[] _stageStart = new float[(int)LearnStage.Done + 1];

        public LearnChord Chord { get; private set; }
        public int MistakeString { get; private set; }
        public int MistakeFinger { get; private set; }
        public string MistakeTip { get; private set; }
        public float Duration { get; private set; }
        public int Count { get { return _events.Count; } }
        public DevLearnEvent this[int index] { get { return _events[index]; } }
        public IList<DevLearnMoment> Moments { get { return _moments.AsReadOnly(); } }

        /// <summary>When the flow enters <paramref name="stage"/> during this run, seconds from the start.</summary>
        public float StageStart(LearnStage stage)
        {
            int i = (int)stage;
            return i >= 0 && i < _stageStart.Length ? _stageStart[i] : 0f;
        }

        /// <summary>The run for <paramref name="chord"/>; null for a null chord.</summary>
        public static DevLearnTimeline Build(LearnChord chord)
        {
            if (chord == null) return null;
            var t = new DevLearnTimeline();
            t.Chord = chord;
            t.Compose();
            return t;
        }

        void Compose()
        {
            int mistakeString, mistakeFinger;
            PickMistake(Chord, out mistakeString, out mistakeFinger);
            MistakeString = mistakeString;
            MistakeFinger = mistakeFinger;
            MistakeTip = MistakeTipFor(mistakeString, mistakeFinger);

            float t = 0f;
            _stageStart[(int)LearnStage.Tune] = 0f;
            t += LeadIn;
            for (int s = LearnChord.StringCount; s >= 1; s--)
            {
                var sweep = new DevLearnEvent { time = t, kind = DevLearnEventKind.TuneSweep, stage = LearnStage.Tune, stringNumber = s,
                                                startCents = s_StartCents[s - 1], duration = SweepSeconds + RestInTuneSeconds };
                Add(sweep);
                if (s == LearnChord.StringCount)
                {
                    _moments.Add(new DevLearnMoment("tune-6-flat", t + 0.45f, LearnStage.Tune));
                    _moments.Add(new DevLearnMoment("tune-6-in-tune", t + sweep.duration - 0.05f, LearnStage.Tune));
                }
                t += sweep.duration;
                Add(new DevLearnEvent { time = t, kind = DevLearnEventKind.StringInTune, stage = LearnStage.Tune, stringNumber = s });
                if (s > 1) t += NextStringGap;
            }

            _stageStart[(int)LearnStage.FingerByFinger] = t;
            for (int k = 0; k < Chord.StepCount; k++)
            {
                LearnChordStep step = Chord.Step(k);
                _moments.Add(new DevLearnMoment("finger-" + step.finger, t + FingerSeconds - 0.4f, LearnStage.FingerByFinger));
                t += FingerSeconds;
                Add(new DevLearnEvent { time = t, kind = DevLearnEventKind.FingerPlaced, stage = LearnStage.FingerByFinger, finger = step.finger });
            }

            _stageStart[(int)LearnStage.PluckCheck] = t;
            t += PluckPromptSeconds;
            float last = t;
            for (int s = Chord.LowestSoundingString; s >= 1; s--)
            {
                if (!Chord.IsSounding(s)) continue;
                bool fault = s == MistakeString;
                Add(new DevLearnEvent { time = t, kind = DevLearnEventKind.Pluck, stage = LearnStage.PluckCheck, stringNumber = s,
                                        mark = fault ? MistakeMark : StringMark.Correct, tip = fault ? MistakeTip : null,
                                        icon = fault ? MistakeIcon : TipIcon.None });
                last = t;
                t += PluckInterval;
            }
            _moments.Add(new DevLearnMoment("pluck-" + MistakeName() + "-muted", last + 0.6f, LearnStage.PluckCheck));
            t = last + TipReadSeconds;
            for (int s = Chord.LowestSoundingString; s >= 1; s--)
            {
                if (!Chord.IsSounding(s)) continue;
                Add(new DevLearnEvent { time = t, kind = DevLearnEventKind.Pluck, stage = LearnStage.PluckCheck, stringNumber = s,
                                        mark = StringMark.Correct, icon = TipIcon.None });
                last = t;
                t += RetryInterval;
            }
            _moments.Add(new DevLearnMoment("pluck-retry-all-correct", last + 0.5f, LearnStage.PluckCheck));

            t = last + LearnFlow.AllCorrectHoldSeconds;
            _stageStart[(int)LearnStage.StrumCheck] = t;
            t += StrumPromptSeconds;
            Add(new DevLearnEvent { time = t, kind = DevLearnEventKind.Strum, stage = LearnStage.StrumCheck, clean = true, icon = TipIcon.Good });
            _moments.Add(new DevLearnMoment("strum-clean", t + 0.5f, LearnStage.Done));
            _stageStart[(int)LearnStage.Done] = t;
            t += EndHold;
            Add(new DevLearnEvent { time = t, kind = DevLearnEventKind.End, stage = LearnStage.Done });
            Duration = t;
        }

        void Add(DevLearnEvent e)
        {
            _events.Add(e);
        }

        string MistakeName()
        {
            string n = LearnChord.StringName(MistakeString);
            return n.Replace(' ', '-').ToLowerInvariant();
        }

        StringMark MistakeMark { get { return MistakeFinger > 0 ? StringMark.Muted : StringMark.Unclear; } }

        TipIcon MistakeIcon { get { return MistakeFinger > 0 ? TipIcon.Arch : TipIcon.Again; } }

        /// <summary>
        /// The deliberate mistake for a chord: an open string muted by a fretting finger beside it. Per the tip table
        /// (docs/MASTER_PLAN.md B6, "Muted"), the finger blamed is the one on the bass-side neighbouring string, else the
        /// treble-side neighbour. Of the open strings that have such a finger, the highest-pitched is chosen (the finger on
        /// the thinnest strings has to reach farthest across). G: B string, finger 3 (the demo script's mistake); C and Am:
        /// high E, finger 1; D: D string, finger 1; Em: G string, finger 3. With no such open string: string 1, finger 0
        /// (an "unclear, pluck again" verdict instead). Pure.
        /// </summary>
        public static void PickMistake(LearnChord chord, out int stringNumber, out int finger)
        {
            stringNumber = 1;
            finger = 0;
            if (chord == null) return;
            for (int s = 1; s <= LearnChord.StringCount; s++)
            {
                if (!chord.IsOpen(s)) continue;
                int g = chord.Finger(s + 1);
                if (g == 0) g = chord.Finger(s - 1);
                if (g == 0) continue;
                stringNumber = s;
                finger = g;
                return;
            }
        }

        /// <summary>"Arch finger 3, B string muted"; the E strings say "high E muted" / "low E muted" to stay short. Pure.</summary>
        public static string MistakeTipFor(int stringNumber, int finger)
        {
            if (finger <= 0) return "Pluck string " + stringNumber + " again, slowly";
            string name = LearnChord.StringName(stringNumber);
            bool eString = stringNumber == 1 || stringNumber == 6;
            return "Arch finger " + finger + ", " + name + (eString ? " muted" : " string muted");
        }

        /// <summary>
        /// The needle's reading <paramref name="t"/> seconds into a TuneSweep that starts at <paramref name="startCents"/>:
        /// an eased approach that reaches 0 at <see cref="SweepSeconds"/> (with a small, fading wobble so it looks played),
        /// then a rest within 1 cent of the target. Pure, deterministic.
        /// </summary>
        public static float SweepCents(float startCents, float t)
        {
            if (t <= 0f) return startCents;
            if (t < SweepSeconds)
            {
                float u = t / SweepSeconds;
                float remain = (1f - u) * (1f - u);
                return startCents * remain + 1.5f * Mathf.Sin(t * 9f) * (1f - u);
            }
            return 0.6f * Mathf.Sin(t * 5f);
        }
    }
}
