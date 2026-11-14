using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Hud.Learn;
using UntitledGuitarApp.Runtime.Neck;
using UntitledGuitarApp.Runtime.Practice.Learn;
using UntitledGuitarApp.Runtime.Practice.Learn.Dev;
using LearnMark = UntitledGuitarApp.Runtime.Hud.Learn.StringMark;
using NeckMark = UntitledGuitarApp.Runtime.Neck.StringMark;

namespace UntitledGuitarApp.Tests.EditMode
{
    // Pure tests for learn mode: the chord shapes, the LearnFlow state machine (through a view that records what it is told),
    // the one-mark-per-string column rule and the SIMULATED dev run's order. No engine objects, no Play Mode, no waiting.
    // Expected values come from docs/MASTER_PLAN.md Appendix B2 (chords) and B6 (the Muted tip rule), not from running the code.
    public class LearnFlowTests
    {
        sealed class RecordingView : ILearnView
        {
            public readonly List<string> Calls = new List<string>();
            public void EnterStage(LearnStage stage, LearnChord chord) { Calls.Add("enter:" + stage); }
            public void LeaveStage(LearnStage stage, LearnChord chord) { Calls.Add("leave:" + stage); }
            public void ShowTunerTarget(int s, string note) { Calls.Add("target:" + s + ":" + note); }
            public void ShowTunerReading(float cents, float confidence) { Calls.Add("reading"); }
            public void ShowFingerStep(int finger, int s, int fret, int step, int count) { Calls.Add("step:f" + finger + ":s" + s + ":fret" + fret + ":" + step + "/" + count); }
            public void ShowStringMark(int s, LearnMark mark) { Calls.Add("mark:" + s + ":" + mark); }
            public void ShowTip(string tip, TipIcon icon) { Calls.Add("tip:" + tip + ":" + icon); }
            public void ShowStrum(bool clean, string issue, TipIcon icon) { Calls.Add("strum:" + clean); }
            public string Last { get { return Calls.Count > 0 ? Calls[Calls.Count - 1] : ""; } }
            public bool Has(string call) { return Calls.Contains(call); }
        }

        static LearnChord G { get { return LearnChords.Get("G"); } }

        static void TuneAll(LearnFlow flow)
        {
            for (int s = 6; s >= 1; s--) flow.OnStringInTune(s);
        }

        static void PlaceAll(LearnFlow flow)
        {
            for (int k = 0; k < flow.Chord.StepCount; k++) flow.OnFingerPlaced(flow.Chord.Step(k).finger);
        }

        static void PluckAll(LearnFlow flow, int faultString, LearnMark fault)
        {
            for (int s = flow.Chord.LowestSoundingString; s >= 1; s--)
            {
                if (!flow.Chord.IsSounding(s)) continue;
                flow.OnPluckResult(s, s == faultString ? fault : LearnMark.Correct, s == faultString ? "Arch finger 3, B string muted" : null, TipIcon.Arch);
            }
        }

        // ------------------------------------------------------------------ chords (Appendix B2)

        [Test]
        public void GChordHasTheTableShapeAndFingerOrder()
        {
            LearnChord g = G;
            Assert.AreEqual("G", g.Name);
            int[] fretsString1To6 = { 3, 0, 0, 0, 2, 3 };
            for (int s = 1; s <= 6; s++) Assert.AreEqual(fretsString1To6[s - 1], g.Fret(s), "fret on string " + s);
            Assert.AreEqual(3, g.StepCount);
            Assert.AreEqual(new LearnChordStep(1, 5, 2), g.Step(0));
            Assert.AreEqual(new LearnChordStep(2, 6, 3), g.Step(1));
            Assert.AreEqual(new LearnChordStep(3, 1, 3), g.Step(2));
            Assert.AreEqual(6, g.LowestSoundingString);
            Assert.AreEqual(6, g.SoundingCount);
        }

        [Test]
        public void ChordsWithStringsNotPlayedStartPluckingLower()
        {
            Assert.AreEqual(5, LearnChords.Get("C").LowestSoundingString);
            Assert.AreEqual(4, LearnChords.Get("D").LowestSoundingString);
            Assert.AreEqual(5, LearnChords.Get("Am").LowestSoundingString);
            Assert.AreEqual(6, LearnChords.Get("Em").LowestSoundingString);
            Assert.AreEqual(LearnChord.Muted, LearnChords.Get("C").Fret(6));
            Assert.AreEqual(new LearnChordStep(1, 2, 1), LearnChords.Get("C").Step(0));
            Assert.AreEqual(2, LearnChords.Get("Em").StepCount);
        }

        [Test]
        public void FlagsPickTheChordElseG()
        {
            var flags = new FlagSet();
            Assert.AreEqual("G", LearnChords.NameFromFlags(null));
            Assert.AreEqual("G", LearnChords.NameFromFlags(flags));
            flags.chords = new string[0];
            Assert.AreEqual("G", LearnChords.NameFromFlags(flags));
            flags.chords = new[] { "Em" };                      // tools/flags/judge.json
            Assert.AreEqual("Em", LearnChords.NameFromFlags(flags));
            flags.chords = new[] { " am ", "G" };
            Assert.AreEqual("Am", LearnChords.NameFromFlags(flags));
            flags.chords = new[] { "F#m" };
            Assert.AreEqual("G", LearnChords.NameFromFlags(flags));
            Assert.AreEqual("Em", LearnChords.FromFlags(new FlagSet { chords = new[] { "Em" } }).Name);
        }

        // ------------------------------------------------------------------ the state machine

        [Test]
        public void BeginStartsTuningString6AndReadingsOnlyCountWhileTuning()
        {
            var view = new RecordingView();
            var flow = new LearnFlow(view);
            flow.OnTunerReading(3f, 1f);
            Assert.AreEqual(0, view.Calls.Count, "nothing happens before Begin");
            Assert.IsTrue(flow.Begin(G));
            Assert.AreEqual(LearnStage.Tune, flow.Stage);
            CollectionAssert.AreEqual(new[] { "enter:Tune", "target:6:E" }, view.Calls);
            flow.OnTunerReading(-12f, 0.9f);
            Assert.AreEqual("reading", view.Last);
            flow.OnStringInTune(5);                              // not the string being tuned
            Assert.AreEqual(6, flow.TuneString);
            flow.OnStringInTune(6);
            Assert.AreEqual("target:5:A", view.Last);
            Assert.IsFalse(flow.Begin(null));
        }

        [Test]
        public void TuningSixStringsLeadsToFingerOneThenEachFingerInOrder()
        {
            var view = new RecordingView();
            var flow = new LearnFlow(view);
            flow.Begin(G);
            TuneAll(flow);
            Assert.AreEqual(LearnStage.FingerByFinger, flow.Stage);
            Assert.IsTrue(view.Has("leave:Tune"));
            Assert.AreEqual("step:f1:s5:fret2:1/3", view.Last);
            flow.OnFingerPlaced(3);                              // not the finger asked for
            Assert.AreEqual(0, flow.StepIndex);
            flow.OnTunerReading(1f, 1f);
            Assert.AreEqual("step:f1:s5:fret2:1/3", view.Last, "a tuner reading after tuning is ignored");
            flow.OnFingerPlaced(1);
            Assert.AreEqual("step:f2:s6:fret3:2/3", view.Last);
            flow.OnFingerPlaced(2);
            Assert.AreEqual("step:f3:s1:fret3:3/3", view.Last);
            flow.OnFingerPlaced(3);
            Assert.AreEqual(LearnStage.PluckCheck, flow.Stage);
            Assert.AreEqual("tip:Pluck each string, 6 to 1:None", view.Last);
        }

        [Test]
        public void PluckCheckWithAMutedStringStaysUntilACleanPassThenHoldsThenStrum()
        {
            var view = new RecordingView();
            var flow = new LearnFlow(view);
            flow.Begin(G);
            TuneAll(flow);
            PlaceAll(flow);
            PluckAll(flow, 2, LearnMark.Muted);
            Assert.AreEqual(LearnStage.PluckCheck, flow.Stage);
            Assert.AreEqual(1, flow.PluckPasses);
            Assert.IsTrue(view.Has("mark:2:Muted"));
            Assert.IsTrue(view.Has("tip:Arch finger 3, B string muted:Arch"));
            Assert.IsFalse(flow.Holding);

            PluckAll(flow, 0, LearnMark.None);                   // the retry: every string correct
            Assert.AreEqual(2, flow.PluckPasses);
            Assert.AreEqual("tip:" + LearnFlow.AllCorrectTip + ":Good", view.Last);
            Assert.IsTrue(flow.Holding);
            flow.OnPluckResult(3, LearnMark.Wrong, null, TipIcon.None);
            Assert.IsFalse(view.Has("mark:3:Wrong"), "verdicts during the hold are ignored");
            flow.Tick(LearnFlow.AllCorrectHoldSeconds - 0.1f);
            Assert.AreEqual(LearnStage.PluckCheck, flow.Stage);
            flow.Tick(0.2f);
            Assert.AreEqual(LearnStage.StrumCheck, flow.Stage);
            Assert.AreEqual("tip:" + LearnFlow.StrumPrompt + ":None", view.Last);
        }

        [Test]
        public void PluckingTheSameStringAgainStartsANewPass()
        {
            var view = new RecordingView();
            var flow = new LearnFlow(view);
            flow.Begin(G);
            flow.SkipTo(LearnStage.PluckCheck);
            for (int s = 6; s >= 3; s--) flow.OnPluckResult(s, LearnMark.Correct, null, TipIcon.None);
            flow.OnPluckResult(6, LearnMark.Correct, null, TipIcon.None);   // started over
            for (int s = 2; s >= 1; s--) flow.OnPluckResult(s, LearnMark.Correct, null, TipIcon.None);
            Assert.AreEqual(0, flow.PluckPasses, "strings 5..3 of the new pass are still missing");
            for (int s = 5; s >= 3; s--) flow.OnPluckResult(s, LearnMark.Correct, null, TipIcon.None);
            Assert.AreEqual(1, flow.PluckPasses);
            Assert.IsTrue(flow.Holding);
        }

        [Test]
        public void ACChordPassNeedsOnlyItsFiveSoundingStrings()
        {
            var view = new RecordingView();
            var flow = new LearnFlow(view);
            flow.Begin(LearnChords.Get("C"));
            flow.SkipTo(LearnStage.PluckCheck);
            Assert.AreEqual("tip:Pluck each string, 5 to 1:None", view.Last);
            for (int s = 5; s >= 1; s--) flow.OnPluckResult(s, LearnMark.Correct, null, TipIcon.None);
            Assert.AreEqual(1, flow.PluckPasses);
            Assert.IsTrue(flow.Holding);
        }

        [Test]
        public void StrumCheckEndsOnlyOnACleanStrum()
        {
            var view = new RecordingView();
            var flow = new LearnFlow(view);
            flow.Begin(G);
            flow.OnStrumResult(true, null, TipIcon.Good);
            Assert.AreEqual(LearnStage.Tune, flow.Stage, "a strum while tuning is ignored");
            flow.SkipTo(LearnStage.StrumCheck);
            flow.OnStrumResult(false, "Heard C, target is G", TipIcon.Mismatch);
            Assert.AreEqual(LearnStage.StrumCheck, flow.Stage);
            flow.OnStrumResult(true, null, TipIcon.Good);
            Assert.AreEqual(LearnStage.Done, flow.Stage);
            Assert.AreEqual("enter:Done", view.Last);
        }

        [Test]
        public void SkipMovesOneStageAtATimeAndStopReturnsToIdle()
        {
            var view = new RecordingView();
            var flow = new LearnFlow(view);
            flow.Skip();
            Assert.AreEqual(LearnStage.Idle, flow.Stage, "Skip before Begin does nothing");
            flow.Begin(G);
            LearnStage[] expected = { LearnStage.FingerByFinger, LearnStage.PluckCheck, LearnStage.StrumCheck, LearnStage.Done, LearnStage.Done };
            for (int i = 0; i < expected.Length; i++)
            {
                flow.Skip();
                Assert.AreEqual(expected[i], flow.Stage, "after skip " + (i + 1));
            }
            flow.SkipTo(LearnStage.Tune);
            Assert.AreEqual(LearnStage.Done, flow.Stage, "SkipTo never goes back");
            view.Calls.Clear();
            flow.Stop();
            Assert.AreEqual(LearnStage.Idle, flow.Stage);
            CollectionAssert.AreEqual(new[] { "leave:Done", "enter:Idle" }, view.Calls);
            flow.Stop();
            Assert.AreEqual(2, view.Calls.Count, "a second Stop does nothing");
        }

        [Test]
        public void EveryStageChangeIsALeaveThenAnEnter()
        {
            var view = new RecordingView();
            var flow = new LearnFlow(view);
            flow.Begin(G);
            TuneAll(flow);
            PlaceAll(flow);
            var stages = new List<string>();
            foreach (string c in view.Calls)
                if (c.StartsWith("enter:") || c.StartsWith("leave:")) stages.Add(c);
            CollectionAssert.AreEqual(new[] { "enter:Tune", "leave:Tune", "enter:FingerByFinger", "leave:FingerByFinger", "enter:PluckCheck" }, stages);
        }

        // ------------------------------------------------------------------ one mark per string in the neck's column

        [Test]
        public void ColumnKeepsOnlyXMarksThatNoVerdictReplaced()
        {
            var result = new NeckMark[6];
            var verdicts = new LearnMark[6];
            LearnNeckColumn.ChordMarks(LearnChords.Get("C"), result);
            CollectionAssert.AreEqual(new[] { NeckMark.Open, NeckMark.None, NeckMark.Open, NeckMark.None, NeckMark.None, NeckMark.Muted }, result);

            LearnNeckColumn.ColumnMarksDuringPluck(LearnChords.Get("C"), verdicts, result);
            CollectionAssert.AreEqual(new[] { NeckMark.None, NeckMark.None, NeckMark.None, NeckMark.None, NeckMark.None, NeckMark.Muted }, result,
                "O marks give their slots to the verdicts; the X on string 6 stays");
            verdicts[5] = LearnMark.Wrong;                         // string 6 was played after all
            LearnNeckColumn.ColumnMarksDuringPluck(LearnChords.Get("C"), verdicts, result);
            Assert.AreEqual(NeckMark.None, result[5], "the verdict takes the X's slot");

            LearnNeckColumn.ColumnMarksDuringPluck(G, new LearnMark[6], result);
            CollectionAssert.AreEqual(new NeckMark[6], result, "G has no X: the whole column is the verdicts'");
        }

        [Test]
        public void ColumnSlotsAreTheNecksRowInStringOrder()
        {
            NeckSizes sizes = default(NeckSizes);
            sizes.markRowXMm = -20f;
            sizes.markPitchMm = 12f;
            var slots = new Vector3[6];
            Vector3 along, across;
            Assert.IsTrue(LearnNeckColumn.SlotCentres(sizes, 2f, false, Vector3.zero, slots, out along, out across));
            // Without a viewer the neck lays its marks flat: across = +z (toward string 1), string 1 at +2.5 spacings.
            Assert.AreEqual(0f, Vector3.Distance(new Vector3(-0.020f, 0.002f, 0.030f), slots[0]), 1e-6f);
            Assert.AreEqual(0f, Vector3.Distance(new Vector3(-0.020f, 0.002f, -0.030f), slots[5]), 1e-6f);
            for (int i = 0; i < 5; i++) Assert.AreEqual(0.012f, Vector3.Distance(slots[i], slots[i + 1]), 1e-6f);
            Assert.IsFalse(LearnNeckColumn.SlotCentres(sizes, 2f, false, Vector3.zero, new Vector3[5], out along, out across));
        }

        [Test]
        public void FingertipTargetsMapFingersToDotsInMetres()
        {
            var dots = new[] { new FingertipDot(5, 62.1f, 10.5f, 1), new FingertipDot(6, 95f, -17.5f, 2) };
            Vector3[] t = LearnNeckColumn.FingertipTargets(dots, 2, new Vector3[4]);
            Assert.AreEqual(0.0621f, t[0].x, 1e-6f);
            Assert.AreEqual(-0.0175f, t[1].z, 1e-6f);
            Assert.IsTrue(float.IsNaN(t[2].x));
            Assert.IsTrue(float.IsNaN(t[3].x));
        }

        // ------------------------------------------------------------------ the SIMULATED dev run

        [Test]
        public void DevRunForGIsInTheRightOrder()
        {
            DevLearnTimeline run = DevLearnTimeline.Build(G);
            Assert.AreEqual(2, run.MistakeString);
            Assert.AreEqual(3, run.MistakeFinger);
            Assert.AreEqual("Arch finger 3, B string muted", run.MistakeTip);

            var order = new List<string>();
            float lastTime = -1f;
            LearnStage lastStage = LearnStage.Idle;
            for (int i = 0; i < run.Count; i++)
            {
                DevLearnEvent e = run[i];
                Assert.GreaterOrEqual(e.time, lastTime, "event " + i + " goes back in time");
                Assert.GreaterOrEqual((int)e.stage, (int)lastStage, "event " + i + " goes back a stage");
                lastTime = e.time;
                lastStage = e.stage;
                switch (e.kind)
                {
                    case DevLearnEventKind.TuneSweep: order.Add("sweep" + e.stringNumber); break;
                    case DevLearnEventKind.StringInTune: order.Add("tuned" + e.stringNumber); break;
                    case DevLearnEventKind.FingerPlaced: order.Add("finger" + e.finger); break;
                    case DevLearnEventKind.Pluck: order.Add("pluck" + e.stringNumber + (e.mark == LearnMark.Correct ? "ok" : e.mark.ToString())); break;
                    case DevLearnEventKind.Strum: order.Add(e.clean ? "strumClean" : "strumIssue"); break;
                    case DevLearnEventKind.End: order.Add("end"); break;
                }
            }
            CollectionAssert.AreEqual(new[]
            {
                "sweep6", "tuned6", "sweep5", "tuned5", "sweep4", "tuned4", "sweep3", "tuned3", "sweep2", "tuned2", "sweep1", "tuned1",
                "finger1", "finger2", "finger3",
                "pluck6ok", "pluck5ok", "pluck4ok", "pluck3ok", "pluck2Muted", "pluck1ok",
                "pluck6ok", "pluck5ok", "pluck4ok", "pluck3ok", "pluck2ok", "pluck1ok",
                "strumClean", "end",
            }, order);
            Assert.Less(run.Duration, 60f, "a judge watches the whole run");
            Assert.Greater(run.Duration, 25f, "each step stays long enough to read");
        }

        [Test]
        public void DevRunPicksASensibleMistakeForEveryChord()
        {
            // B6 "Muted": the finger on the bass-side neighbour, else the treble-side one; the highest open string that has one.
            string[] names = { "G", "C", "D", "Em", "Am" };
            int[] strings = { 2, 1, 4, 3, 1 };
            int[] fingers = { 3, 1, 1, 3, 1 };
            string[] tips =
            {
                "Arch finger 3, B string muted", "Arch finger 1, high E muted", "Arch finger 1, D string muted",
                "Arch finger 3, G string muted", "Arch finger 1, high E muted",
            };
            for (int i = 0; i < names.Length; i++)
            {
                DevLearnTimeline run = DevLearnTimeline.Build(LearnChords.Get(names[i]));
                Assert.AreEqual(strings[i], run.MistakeString, names[i]);
                Assert.AreEqual(fingers[i], run.MistakeFinger, names[i]);
                Assert.AreEqual(tips[i], run.MistakeTip, names[i]);
                Assert.IsTrue(LearnText.WithinWordLimit(run.MistakeTip), names[i] + ": 7 words or fewer");
                Assert.IsTrue(LearnText.FitsAtAGlance(run.MistakeTip), names[i] + ": two short lines");
                Assert.IsTrue(LearnChords.Get(names[i]).IsOpen(run.MistakeString), names[i] + ": the muted string rings open in the chord");
            }
            Assert.IsTrue(LearnText.FitsAtAGlance(LearnFlow.PluckPromptFor(G)));
            Assert.IsTrue(LearnText.FitsAtAGlance(LearnFlow.StrumPrompt));
            Assert.IsTrue(LearnText.FitsAtAGlance(LearnFlow.AllCorrectTip));
        }

        [Test]
        public void DevRunPlayedIntoTheFlowReachesDoneThroughEveryMoment()
        {
            foreach (string name in LearnChords.Names)
            {
                LearnChord chord = LearnChords.Get(name);
                DevLearnTimeline run = DevLearnTimeline.Build(chord);
                var view = new RecordingView();
                var flow = new LearnFlow(view);
                flow.Begin(chord);
                int next = 0;
                int moment = 0;
                const float dt = 1f / 60f;
                for (float t = 0f; t <= run.Duration + 0.1f; t += dt)
                {
                    flow.Tick(dt);
                    while (next < run.Count && run[next].time <= t)
                    {
                        DevLearnEvent e = run[next++];
                        if (e.kind == DevLearnEventKind.StringInTune) flow.OnStringInTune(e.stringNumber);
                        else if (e.kind == DevLearnEventKind.FingerPlaced) flow.OnFingerPlaced(e.finger);
                        else if (e.kind == DevLearnEventKind.Pluck) flow.OnPluckResult(e.stringNumber, e.mark, e.tip, e.icon);
                        else if (e.kind == DevLearnEventKind.Strum) flow.OnStrumResult(e.clean, e.issue, e.icon);
                    }
                    while (moment < run.Moments.Count && run.Moments[moment].time <= t)
                    {
                        Assert.AreEqual(run.Moments[moment].stage, flow.Stage, name + " at moment " + run.Moments[moment].name);
                        moment++;
                    }
                }
                Assert.AreEqual(run.Moments.Count, moment, name + ": every moment was reached");
                Assert.AreEqual(LearnStage.Done, flow.Stage, name);
                Assert.AreEqual(2, flow.PluckPasses, name + ": one pass with the mistake, one clean");
                Assert.IsTrue(view.Has("mark:" + run.MistakeString + ":Muted"), name);
            }
        }

        [Test]
        public void DevRunStageStartsAreInOrderAndMatchTheEvents()
        {
            DevLearnTimeline run = DevLearnTimeline.Build(LearnChords.Get("Em"));
            float previous = -1f;
            for (LearnStage s = LearnStage.Tune; s <= LearnStage.Done; s++)
            {
                Assert.Greater(run.StageStart(s), previous, s.ToString());
                previous = run.StageStart(s);
            }
            for (int i = 0; i < run.Count; i++)
                Assert.GreaterOrEqual(run[i].time, run.StageStart(run[i].stage), "event " + i + " before its stage starts");
        }

        [Test]
        public void SweepStartsOffAndEndsInTune()
        {
            Assert.AreEqual(-30f, DevLearnTimeline.SweepCents(-30f, 0f), 1e-4f);
            Assert.Less(Mathf.Abs(DevLearnTimeline.SweepCents(-30f, DevLearnTimeline.SweepSeconds * 0.4f)), 30f);
            Assert.Greater(Mathf.Abs(DevLearnTimeline.SweepCents(-30f, DevLearnTimeline.SweepSeconds * 0.4f)), 5f, "still visibly off early on");
            for (float t = DevLearnTimeline.SweepSeconds; t < DevLearnTimeline.SweepSeconds + DevLearnTimeline.RestInTuneSeconds; t += 0.05f)
                Assert.Less(Mathf.Abs(DevLearnTimeline.SweepCents(-30f, t)), 1.01f, "rests within the 5 cent in-tune zone at " + t);
        }
    }
}
