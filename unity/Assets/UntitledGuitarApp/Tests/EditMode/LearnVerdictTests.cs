// LearnVerdictTests: the real-audio learn pieces in Runtime/Practice/Learn, offline (mono-tests.sh) as well as in Unity.
// Covers LearnVerdictMap (Core's verdicts -> the Learn HUD's marks/icons), LearnPluckPipe (onset -> PluckVerifier with the
// expectation captured at the onset), the LearnFlow driver hooks (no pass restarts) and the driver's session-sync statics.
// The synthetic signals are plain sines into Core's real verifiers: expected values come from standard tuning
// (A2 = 110 Hz, G3 = 196 Hz, A3 = 220 Hz, C3 = 130.81 Hz), not from running the code.
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UntitledGuitarApp.Core.Audio;
using UntitledGuitarApp.Core.Practice;
using UntitledGuitarApp.Core.Theory;
using UntitledGuitarApp.Runtime.Hud.Learn;
using UntitledGuitarApp.Runtime.Practice.Learn;
using CoreStage = UntitledGuitarApp.Core.Practice.LearnStage;
using FlowStage = UntitledGuitarApp.Runtime.Practice.Learn.LearnStage;
using LearnMark = UntitledGuitarApp.Runtime.Hud.Learn.StringMark;

namespace UntitledGuitarApp.Tests.EditMode
{
    public class LearnVerdictTests
    {
        sealed class RecordingView : ILearnView
        {
            public readonly List<string> Calls = new List<string>();
            public void EnterStage(FlowStage stage, LearnChord chord) { Calls.Add("enter:" + stage); }
            public void LeaveStage(FlowStage stage, LearnChord chord) { Calls.Add("leave:" + stage); }
            public void ShowTunerTarget(int s, string note) { Calls.Add("target:" + s + ":" + note); }
            public void ShowTunerReading(float cents, float confidence) { Calls.Add("reading"); }
            public void ShowFingerStep(int finger, int s, int fret, int step, int count) { Calls.Add("step:f" + finger + ":s" + s + ":fret" + fret + ":" + step + "/" + count); }
            public void ShowStringMark(int s, LearnMark mark) { Calls.Add("mark:" + s + ":" + mark); }
            public void ShowTip(string tip, TipIcon icon) { Calls.Add("tip:" + tip + ":" + icon); }
            public void ShowStrum(bool clean, string issue, TipIcon icon) { Calls.Add("strum:" + clean); }
            public bool Has(string call) { return Calls.Contains(call); }
        }

        // ------------------------------------------------------------------ verdict mapping

        [TestCase(PluckVerdict.Correct, LearnMark.Correct)]
        [TestCase(PluckVerdict.OpenStringInstead, LearnMark.Wrong)]
        [TestCase(PluckVerdict.WrongFretLow, LearnMark.Wrong)]
        [TestCase(PluckVerdict.WrongFretHigh, LearnMark.Wrong)]
        [TestCase(PluckVerdict.WrongString, LearnMark.Wrong)]
        [TestCase(PluckVerdict.ExtraString, LearnMark.Wrong)]
        [TestCase(PluckVerdict.Muted, LearnMark.Muted)]
        [TestCase(PluckVerdict.Unclear, LearnMark.Unclear)]
        [TestCase(PluckVerdict.ShortRing, LearnMark.LetRing)]
        public void MarkForPluck_MapsCoreVerdictsToTheLearnColumn(PluckVerdict verdict, LearnMark expected)
        {
            Assert.AreEqual(expected, LearnVerdictMap.MarkForPluck(verdict));
        }

        [TestCase(PluckVerdict.Correct, TipIcon.Good)]
        [TestCase(PluckVerdict.OpenStringInstead, TipIcon.Press)]
        [TestCase(PluckVerdict.WrongFretLow, TipIcon.Move)]
        [TestCase(PluckVerdict.WrongString, TipIcon.Move)]
        [TestCase(PluckVerdict.Muted, TipIcon.Arch)]
        [TestCase(PluckVerdict.ExtraString, TipIcon.Skip)]
        [TestCase(PluckVerdict.Unclear, TipIcon.Again)]
        [TestCase(PluckVerdict.ShortRing, TipIcon.Ring)]
        public void IconForPluck_MapsCoreVerdictsToTipIcons(PluckVerdict verdict, TipIcon expected)
        {
            Assert.AreEqual(expected, LearnVerdictMap.IconForPluck(verdict));
        }

        [Test]
        public void IconForStrum_MapsTheChordLevelIssues()
        {
            Assert.AreEqual(TipIcon.Good, LearnVerdictMap.IconForStrum(StrumResult("G", clean: true, issue: StrumIssue.None)));
            Assert.AreEqual(TipIcon.Mismatch, LearnVerdictMap.IconForStrum(StrumResult("G", clean: false, issue: StrumIssue.WrongChord)));
            Assert.AreEqual(TipIcon.Skip, LearnVerdictMap.IconForStrum(StrumResult("C", clean: false, issue: StrumIssue.ExtraString)));
            Assert.AreEqual(TipIcon.Press, LearnVerdictMap.IconForStrum(StrumResult("G", clean: false, issue: StrumIssue.OpenStringInstead)));
            Assert.AreEqual(TipIcon.Move, LearnVerdictMap.IconForStrum(StrumResult("G", clean: false, issue: StrumIssue.WrongFretLow)));
            Assert.AreEqual(TipIcon.Arch, LearnVerdictMap.IconForStrum(StrumResult("Em", clean: false, issue: StrumIssue.Muted)));
            Assert.AreEqual(TipIcon.Again, LearnVerdictMap.IconForStrum(StrumResult("G", clean: false, issue: StrumIssue.Unclear)));
        }

        static StrumCheckResult StrumResult(string chord, bool clean, StrumIssue issue) =>
            new StrumCheckResult(chord, clean ? chord : null, clean ? 0.9 : 0, clean ? 0.5 : 0, clean, issue,
                                 issue == StrumIssue.Muted ? 3 : 0, -1, -1, 0, null, 0.9, 0, 0, 30, 0.2f);

        // ------------------------------------------------------------------ the pluck pipe (real Core verifiers, synthetic sines)

        const int Rate = 48000;

        static float[] Pluck(double hz, float seconds = 1.6f, float startSec = 0.1f, float amplitude = 0.4f)
        {
            int n = (int)(seconds * Rate);
            var x = new float[n];
            int start = (int)(startSec * Rate);
            for (int i = start; i < n; i++)
            {
                double t = (i - start) / (double)Rate;
                x[i] = (float)(amplitude * Mathf.Sin((float)(2.0 * Mathf.PI * hz * t)));
            }
            return x;
        }

        static PluckEvent JudgeOnce(LearnPluckPipe pipe, float[] signal, int stringNumber, int midi)
        {
            for (int done = 0; done < signal.Length; done += 1024)
            {
                int n = Mathf.Min(1024, signal.Length - done);
                pipe.Process(signal, done, n, stringNumber, midi, null);
            }
            PluckEvent e;
            Assert.IsTrue(pipe.TryRead(out e), "the onset was not judged");
            PluckEvent extra;
            Assert.IsFalse(pipe.TryRead(out extra), "one onset expected, the pipe gave more");
            return e;
        }

        [Test]
        public void Pipe_JudgesTheExpectedPitchClean_WithRingOut()
        {
            var pipe = new LearnPluckPipe(AudioConfig.Default, Rate, 0);
            PluckEvent e = JudgeOnce(pipe, Pluck(196.0), 3, 55);   // G3, the open G string
            Assert.AreEqual(3, e.Result.String);
            Assert.AreEqual(55, e.Result.ExpectedMidi);
            Assert.AreEqual(PluckVerdict.Correct, e.Result.Verdict, "196 Hz for string 3 must be Correct: " + e.Result);
            Assert.IsTrue(e.Result.RingJudged, "the full window judges ring-out");
        }

        [Test]
        public void Pipe_ReadsAFretTooHigh()
        {
            var pipe = new LearnPluckPipe(AudioConfig.Default, Rate, 0);
            PluckEvent e = JudgeOnce(pipe, Pluck(220.0), 3, 55);   // A3 = G3 + 2 semitones
            Assert.AreEqual(PluckVerdict.WrongFretHigh, e.Result.Verdict, "220 Hz for G3 must be WrongFretHigh: " + e.Result);
        }

        [Test]
        public void Pipe_ReadsTheOpenStringInstead()
        {
            var pipe = new LearnPluckPipe(AudioConfig.Default, Rate, 0);
            PluckEvent e = JudgeOnce(pipe, Pluck(110.0), 5, 48);   // A2 for a C3 (string 5 fret 3) expected
            Assert.AreEqual(PluckVerdict.OpenStringInstead, e.Result.Verdict, "110 Hz for C3 must be OpenStringInstead: " + e.Result);
        }

        [Test]
        public void Pipe_ReadsAMutedPluck()
        {
            var pipe = new LearnPluckPipe(AudioConfig.Default, Rate, 0);
            var x = new float[(int)(1.6 * Rate)];
            int start = (int)(0.1 * Rate);
            for (int i = 0; i < (int)(0.02 * Rate); i++)
                x[start + i] = (float)(0.4 * Mathf.Sin((float)(2.0 * Mathf.PI * 1000.0 * i / Rate)));
            PluckEvent e = JudgeOnce(pipe, x, 3, 55);
            Assert.AreEqual(PluckVerdict.Muted, e.Result.Verdict, "a struck but silent string must be Muted: " + e.Result);
        }

        [Test]
        public void Pipe_JudgesWithTheExpectationCapturedAtTheOnset()
        {
            var pipe = new LearnPluckPipe(AudioConfig.Default, Rate, 0);
            float[] signal = Pluck(110.0);
            int half = signal.Length / 2;
            pipe.Process(signal, 0, half, 5, 48, null);          // A2 for C3: the onset happens in this half
            pipe.Process(signal, half, signal.Length - half, 4, 50, null);   // the expectation changes while the window completes
            PluckEvent e;
            Assert.IsTrue(pipe.TryRead(out e));
            Assert.AreEqual(5, e.Result.String, "the onset must keep the expectation it was queued with");
            Assert.AreEqual(48, e.Result.ExpectedMidi);
            Assert.AreEqual(PluckVerdict.OpenStringInstead, e.Result.Verdict);
        }

        [Test]
        public void Pipe_SilenceProducesNothing()
        {
            var pipe = new LearnPluckPipe(AudioConfig.Default, Rate, 0);
            var x = new float[48000];
            pipe.Process(x, 0, x.Length, 3, 55, null);
            PluckEvent e;
            Assert.IsFalse(pipe.TryRead(out e), "silence must not be judged");
            Assert.AreEqual(0, pipe.PendingCount);
        }

        [Test]
        public void Pipe_NothingExpected_DropsTheOnset()
        {
            var pipe = new LearnPluckPipe(AudioConfig.Default, Rate, 0);
            float[] signal = Pluck(196.0);
            pipe.Process(signal, 0, signal.Length, 0, -1, null);   // no active string
            PluckEvent e;
            Assert.IsFalse(pipe.TryRead(out e), "an onset without an expectation must be dropped, not judged");
            Assert.GreaterOrEqual(pipe.Dropped, 1);
        }

        // ------------------------------------------------------------------ the LearnFlow driver hooks

        static LearnFlow FlowAt(FlowStage stage, RecordingView view)
        {
            var flow = new LearnFlow(view);
            Assert.IsTrue(flow.Begin(LearnChords.Get("G")));
            for (int s = 6; s >= 1; s--) flow.OnStringInTune(s);
            if (stage == FlowStage.FingerByFinger) return flow;
            for (int k = 0; k < flow.Chord.StepCount; k++) flow.OnFingerPlaced(flow.Chord.Step(k).finger);
            return flow;
        }

        [Test]
        public void DriverPluckResults_MarkTheirStrings_AndACompletePassHolds()
        {
            var view = new RecordingView();
            LearnFlow flow = FlowAt(FlowStage.PluckCheck, view);
            flow.OnDriverPluckResult(6, LearnMark.Correct, null, TipIcon.None, false);
            Assert.IsTrue(view.Has("mark:6:Correct"));
            Assert.IsFalse(flow.Holding);

            flow.OnDriverPluckResult(5, LearnMark.Wrong, "Press string 5", TipIcon.Press, false);
            Assert.IsTrue(view.Has("tip:Press string 5:Press"), "a fault shows its tip");
            Assert.IsFalse(flow.Holding, "a fault never completes the pass");

            flow.OnDriverPluckResult(4, LearnMark.Correct, null, TipIcon.None, true);
            Assert.IsTrue(flow.Holding, "Core's completed pass starts the all-correct hold");
            Assert.IsTrue(view.Has("tip:" + LearnFlow.AllCorrectTip + ":Good"));

            flow.Tick(LearnFlow.AllCorrectHoldSeconds);
            Assert.AreEqual(FlowStage.StrumCheck, flow.Stage, "the hold leads to the strum check");
        }

        [Test]
        public void DriverPluckResult_NoRestartOnRepeat_UnlikeTheDevSink()
        {
            var view = new RecordingView();
            LearnFlow flow = FlowAt(FlowStage.PluckCheck, view);
            flow.OnDriverPluckResult(6, LearnMark.Correct, null, TipIcon.None, false);
            flow.OnDriverPluckResult(6, LearnMark.Wrong, "Pluck string 6 only", TipIcon.Move, false);   // a retry of the same string
            Assert.IsTrue(view.Has("mark:6:Correct"), "a driver result never erases the earlier marks");
        }

        [Test]
        public void DriverFingerFault_ShowsOnlyInTheFingerStage()
        {
            var view = new RecordingView();
            LearnFlow flow = FlowAt(FlowStage.FingerByFinger, view);
            flow.OnFingerStageFault("Arch finger 3, B string muted", TipIcon.Arch);
            Assert.IsTrue(view.Has("tip:Arch finger 3, B string muted:Arch"));

            for (int k = 0; k < flow.Chord.StepCount; k++) flow.OnFingerPlaced(flow.Chord.Step(k).finger);
            view.Calls.Clear();
            flow.OnFingerStageFault("Arch finger 3, B string muted", TipIcon.Arch);
            Assert.IsFalse(view.Has("tip:Arch finger 3, B string muted:Arch"), "pluck-check stage must not show finger faults");
        }

        // ------------------------------------------------------------------ the driver's session sync

        [TestCase(FlowStage.FingerByFinger, CoreStage.FingerByFinger)]
        [TestCase(FlowStage.PluckCheck, CoreStage.PluckCheck)]
        [TestCase(FlowStage.StrumCheck, CoreStage.WholeChord)]
        [TestCase(FlowStage.Done, CoreStage.Mastered)]
        public void SessionStageFor_MapsTheFlowStages(FlowStage flow, CoreStage expected)
        {
            Assert.AreEqual(expected, LearnAudioDriver.SessionStageFor(flow));
        }

        [Test]
        public void SyncSession_AdvancesCoreToTheFlowStage_StepByStep()
        {
            ChordShape shape;
            Assert.IsTrue(ChordLibrary.TryGet("G", out shape));
            var session = new LearnChordSession(shape, 0);
            Assert.AreEqual(CoreStage.FingerByFinger, session.Stage);

            LearnAudioDriver.SyncSession(session, FlowStage.PluckCheck, 1.0);
            Assert.AreEqual(CoreStage.PluckCheck, session.Stage, "the finger steps are skipped in lockstep with the UI");

            LearnAudioDriver.SyncSession(session, FlowStage.StrumCheck, 2.0);
            Assert.AreEqual(CoreStage.WholeChord, session.Stage);

            LearnAudioDriver.SyncSession(session, FlowStage.Done, 3.0);
            Assert.AreEqual(CoreStage.Mastered, session.Stage);
        }
    }
}
