using System;
using NUnit.Framework;
using UnityEngine;
using UntitledGuitarApp.Runtime.Hud;
using UntitledGuitarApp.Runtime.Hud.Learn;
using UntitledGuitarApp.Runtime.Spectator;

namespace UntitledGuitarApp.Tests.EditMode
{
    // Pure-logic tests for the Learn HUD (tuner, per-string marks, tip, strum verdict, finger prompt, layout). They need no
    // Play Mode, no device, no rendering and no waiting: every rule lives in a static function or a plain struct that is
    // stepped with a made-up time step. Expected numbers were derived independently (python3 math), not by running this code.
    public class HudLearnTests
    {
        const float Frame = 1f / 60f;

        // The messages the project plans to show (docs/MASTER_PLAN.md Appendix B2 and B6, docs/PRD.md), with real numbers filled in.
        static readonly string[] PlannedMessages =
        {
            "Arch finger 3, B string muted",
            "String 2 muted — arch finger 1",
            "Arch fingers — B string muted",
            "Finger 2: press string 5, fret 2",
            "Finger 2: string 5, fret 2",
            "Finger 3: move to fret 3",
            "Finger 3: fret 3, not 2",
            "Finger 3 goes on string 1",
            "Don't strum string 6",
            "Strum only the top four",
            "Skip string 6",
            "Pluck string 5 again, slowly",
            "Start moving one beat earlier",
            "Heard C, target is G",
            "Heard Am — target is Em",
            StrumVerdict.DefaultIssueText,
            TunerNeedle.TooLowText,
            TunerNeedle.TooHighText,
            TunerNeedle.InTuneText,
        };

        // ---------------------------------------------------------------- tuner: cents to needle angle

        [TestCase(0f, 0f)]
        [TestCase(50f, 40f)]
        [TestCase(-50f, -40f)]
        [TestCase(25f, 20f)]
        [TestCase(-12.5f, -10f)]
        [TestCase(5f, 4f)]
        [TestCase(80f, 40f)]        // beyond the dial: rests on the end stop
        [TestCase(-300f, -40f)]
        [TestCase(float.NaN, 0f)]
        public void NeedleAngle_IsLinearInCentsAndStopsAtTheEnds(float cents, float expectedDegrees)
        {
            Assert.AreEqual(expectedDegrees, TunerMath.NeedleAngleDegrees(cents), 1e-4);
        }

        [Test]
        public void Needle_FlatLeansLeft_SharpLeansRight_InTunePointsUp()
        {
            Vector2 flat = TunerMath.NeedleTip(-50f, 1f);
            Vector2 sharp = TunerMath.NeedleTip(50f, 1f);
            Vector2 centre = TunerMath.NeedleTip(0f, 1f);
            Assert.AreEqual(-0.642788, flat.x, 1e-4);
            Assert.AreEqual(0.766044, flat.y, 1e-4);
            Assert.AreEqual(0.642788, sharp.x, 1e-4);
            Assert.AreEqual(0.766044, sharp.y, 1e-4);
            Assert.AreEqual(0.0, centre.x, 1e-6);
            Assert.AreEqual(1.0, centre.y, 1e-6);
            Assert.Less(TunerMath.NeedleTip(-3f, 1f).x, 0f);      // position alone tells flat from sharp
            Assert.Greater(TunerMath.NeedleTip(3f, 1f).x, 0f);
        }

        [Test]
        public void NeedleRotation_TurnsAnUprightNeedleTheSameWayAsNeedleTip()
        {
            // A needle drawn straight up, rotated for +50 cents, must point where NeedleTip says (to the viewer's right).
            Vector3 tip = TunerMath.NeedleRotation(TunerMath.NeedleAngleDegrees(50f)) * Vector3.up;
            Assert.AreEqual(0.642788, tip.x, 1e-4);
            Assert.AreEqual(0.766044, tip.y, 1e-4);
            Assert.AreEqual(0.0, tip.z, 1e-6);

            Vector3 flat = TunerMath.NeedleRotation(TunerMath.NeedleAngleDegrees(-25f)) * Vector3.up;
            Assert.AreEqual(-0.342020, flat.x, 1e-4);
            Assert.AreEqual(0.939693, flat.y, 1e-4);
        }

        [Test]
        public void TunerConstants_AreTheBriefedOnes()
        {
            Assert.AreEqual(50f, TunerMath.RangeCents);                     // an arc of +-50 cents
            Assert.AreEqual(5f, TunerMath.InTuneEnterCents);                // in-tune zone +-5 cents
            Assert.Greater(TunerMath.InTuneExitCents, TunerMath.InTuneEnterCents);   // with hysteresis
            Assert.AreEqual(4.0, TunerMath.ZoneHalfDegrees, 1e-5);          // 5 of 50 cents on a 40 degree half-dial
            Assert.Greater(TunerMath.InTuneDwellSeconds, 0f);
        }

        // ---------------------------------------------------------------- tuner: confidence gate

        [TestCase(-12f, 0.9f, 0.8f, true)]
        [TestCase(-12f, 0.8f, 0.8f, true)]
        [TestCase(-12f, 0.79f, 0.8f, false)]
        [TestCase(-12f, 0f, 0.8f, false)]
        [TestCase(float.NaN, 0.95f, 0.8f, false)]
        [TestCase(float.PositiveInfinity, 0.95f, 0.8f, false)]
        [TestCase(-12f, float.NaN, 0.8f, false)]
        public void ConfidenceGate(float cents, float confidence, float minConfidence, bool expected)
        {
            Assert.AreEqual(expected, TunerMath.IsConfident(cents, confidence, minConfidence));
        }

        [Test]
        public void Tuner_IsIdleWithoutATarget_AndListensUntilAClearNote()
        {
            var m = new TunerModel();
            Assert.AreEqual(TunerState.Idle, m.State);
            Assert.IsFalse(m.SetReading(-20f, 0.95f, 0.8f), "no target: readings are ignored");
            Assert.AreEqual(TunerState.Idle, m.State);

            m.SetTarget();
            Assert.AreEqual(TunerState.Listening, m.State);
            Assert.IsFalse(m.SetReading(-20f, 0.5f, 0.8f), "an unclear reading must not move the needle");
            m.Tick(0.5f);
            Assert.AreEqual(TunerState.Listening, m.State);
            Assert.IsFalse(m.HasReading);

            Assert.IsTrue(m.SetReading(-20f, 0.9f, 0.8f));
            Assert.AreEqual(TunerState.TooLow, m.State);
            Assert.AreEqual(-20.0, m.ShownCents, 1e-5, "the first note appears where it is, with no sweep across the dial");
        }

        [Test]
        public void Tuner_UnclearReadingsNeverMoveTheNeedle()
        {
            var m = new TunerModel();
            m.SetTarget();
            m.SetReading(-20f, 0.9f, 0.8f);
            for (int i = 0; i < 30; i++)
            {
                m.SetReading(45f, 0.3f, 0.8f);    // noise
                m.Tick(Frame);
            }
            Assert.AreEqual(-20.0, m.ShownCents, 1e-4);
            Assert.AreEqual(TunerState.TooLow, m.State);
        }

        // ---------------------------------------------------------------- tuner: smoothing

        [Test]
        public void ExpSmooth_OneTimeConstantCovers63Percent_AndNeverOvershoots()
        {
            Assert.AreEqual(0.6321206, LearnTiming.ExpSmooth(0f, 1f, 0.12f, 0.12f), 1e-5);
            Assert.AreEqual(0.4f, LearnTiming.ExpSmooth(0.4f, 1f, 0f, 0.12f));          // no time passed
            Assert.AreEqual(0.4f, LearnTiming.ExpSmooth(0.4f, 1f, -1f, 0.12f));
            Assert.AreEqual(1f, LearnTiming.ExpSmooth(0f, 1f, 0.01f, 0f));              // no smoothing: jump
            Assert.LessOrEqual(LearnTiming.ExpSmooth(0f, 1f, 100f, 0.12f), 1f);
            Assert.GreaterOrEqual(LearnTiming.ExpSmooth(1f, 0f, 100f, 0.12f), 0f);
        }

        [Test]
        public void ExpSmooth_DoesNotDependOnTheFrameRate()
        {
            float oneStep = LearnTiming.ExpSmooth(0f, 1f, 0.12f, 0.12f);
            float many = 0f;
            for (int i = 0; i < 12; i++) many = LearnTiming.ExpSmooth(many, 1f, 0.01f, 0.12f);
            Assert.AreEqual(oneStep, many, 1e-4);
        }

        [Test]
        public void Tuner_NeedleEasesTowardANewReading()
        {
            var m = new TunerModel();
            m.SetTarget();
            m.SetReading(-20f, 0.9f, 0.8f);
            m.SetReading(20f, 0.9f, 0.8f);
            Assert.AreEqual(-20.0, m.ShownCents, 1e-5, "a live needle never jumps");
            m.Tick(TunerMath.NeedleSmoothSeconds);
            Assert.AreEqual(5.28482, m.ShownCents, 1e-3);     // -20 + 40 * (1 - 1/e)
            for (int i = 0; i < 120; i++)
            {
                m.SetReading(20f, 0.9f, 0.8f);
                m.Tick(Frame);
            }
            Assert.AreEqual(20.0, m.ShownCents, 1e-2);
            Assert.LessOrEqual(m.ShownCents, 20f, "no overshoot");
        }

        [Test]
        public void Tuner_JitteryReadingsGiveASteadyNeedle()
        {
            // Readings flick between +10 and +14 cents every frame (4 cents of jitter around +12).
            var m = new TunerModel();
            m.SetTarget();
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < 180; i++)
            {
                m.SetReading(i % 2 == 0 ? 10f : 14f, 0.9f, 0.8f);
                m.Tick(Frame);
                if (i >= 60)
                {
                    min = Mathf.Min(min, m.ShownCents);
                    max = Mathf.Max(max, m.ShownCents);
                }
            }
            Assert.Less(max - min, 0.5f, "the needle must move far less than the 4 cents of input jitter");
            Assert.AreEqual(12.0, 0.5f * (min + max), 0.3);
            Assert.AreEqual(TunerState.TooHigh, m.State);
        }

        [Test]
        public void Tuner_ReadingsBeyondTheDialRestOnTheEndStop()
        {
            var m = new TunerModel();
            m.SetTarget();
            m.SetReading(180f, 0.9f, 0.8f);
            Assert.AreEqual(50.0, m.ShownCents, 1e-5);
            Assert.AreEqual(40.0, m.NeedleDegrees, 1e-4);
            Assert.AreEqual(TunerState.TooHigh, m.State);
        }

        // ---------------------------------------------------------------- tuner: in-tune zone with hysteresis

        [TestCase(false, 0f, true)]
        [TestCase(false, 5f, true)]
        [TestCase(false, -5f, true)]
        [TestCase(false, 5.2f, false)]
        [TestCase(false, 7f, false)]      // between the two thresholds: not enough to enter...
        [TestCase(true, 7f, true)]        // ...but enough to stay
        [TestCase(true, -8f, true)]
        [TestCase(true, 8.2f, false)]
        [TestCase(true, -30f, false)]
        public void InTuneZone_HasHysteresis(bool wasInTune, float cents, bool expected)
        {
            Assert.AreEqual(expected, TunerMath.InZone(wasInTune, cents));
        }

        [Test]
        public void Tuner_InTuneNeedsTheNeedleToRestInTheZone()
        {
            var m = new TunerModel();
            m.SetTarget();
            m.SetReading(2f, 0.9f, 0.8f);
            Assert.AreEqual(TunerState.Listening, m.State, "inside the zone but not yet rested: no 'too high' is claimed");
            for (int i = 0; i < 25; i++)      // 0.25 s
            {
                m.SetReading(2f, 0.9f, 0.8f);
                m.Tick(0.01f);
            }
            Assert.IsFalse(m.InTune);
            for (int i = 0; i < 15; i++)      // 0.40 s in total
            {
                m.SetReading(2f, 0.9f, 0.8f);
                m.Tick(0.01f);
            }
            Assert.IsTrue(m.InTune);
            Assert.AreEqual(TunerState.InTune, m.State);
        }

        [Test]
        public void Tuner_SweepingThroughTheZoneIsNotInTune()
        {
            // From -20 to +20 cents: the needle crosses the +-5 zone in about 0.06 s, far less than the dwell.
            var m = new TunerModel();
            m.SetTarget();
            m.SetReading(-20f, 0.9f, 0.8f);
            for (int i = 0; i < 90; i++)
            {
                m.SetReading(20f, 0.9f, 0.8f);
                m.Tick(Frame);
                Assert.IsFalse(m.InTune, "frame " + i);
            }
            Assert.AreEqual(TunerState.TooHigh, m.State);
        }

        [Test]
        public void Tuner_OnceInTune_StaysUntilItLeavesTheWiderZone()
        {
            var m = new TunerModel();
            m.SetTarget();
            Settle(ref m, 2f, 60);
            Assert.IsTrue(m.InTune);

            Settle(ref m, 7f, 120);           // drifts to 7 cents: inside the exit threshold
            Assert.AreEqual(7.0, m.ShownCents, 0.05);
            Assert.IsTrue(m.InTune, "7 cents is not enough to lose a verdict already given");

            Settle(ref m, 9.5f, 120);         // beyond 8 cents
            Assert.IsFalse(m.InTune);
            Assert.AreEqual(TunerState.TooHigh, m.State);

            Settle(ref m, 7f, 120);           // back to 7: not enough to regain it
            Assert.IsFalse(m.InTune, "7 cents is not enough to earn the verdict");
            Assert.AreEqual(TunerState.TooHigh, m.State);

            Settle(ref m, -3f, 120);
            Assert.IsTrue(m.InTune);
        }

        [Test]
        public void Tuner_WhenTheNoteDiesAway_NeedleAndVerdictStay_ThenANewNoteStartsAfresh()
        {
            var m = new TunerModel();
            m.SetTarget();
            m.SetReading(-20f, 0.9f, 0.8f);
            m.Tick(0.5f);
            Assert.IsTrue(m.NeedleLive);
            m.Tick(0.7f);                      // 1.2 s since the last clear reading
            Assert.IsFalse(m.NeedleLive, "an old reading is drawn dim");
            m.Tick(5f);
            Assert.AreEqual(-20.0, m.ShownCents, 1e-4, "the needle must not drop to the centre in silence");
            Assert.AreEqual(TunerState.TooLow, m.State, "the instruction stays while the player turns the peg");

            m.SetReading(30f, 0.9f, 0.8f);     // the next pluck
            Assert.AreEqual(30.0, m.ShownCents, 1e-5, "a new note starts where it is");
            Assert.AreEqual(TunerState.TooHigh, m.State);
            Assert.IsTrue(m.NeedleLive);
        }

        [Test]
        public void Tuner_ANewTargetForgetsTheOldString()
        {
            var m = new TunerModel();
            m.SetTarget();
            Settle(ref m, 1f, 60);
            Assert.IsTrue(m.InTune);
            m.SetTarget();
            Assert.AreEqual(TunerState.Listening, m.State);
            Assert.IsFalse(m.InTune);
            Assert.IsFalse(m.HasReading);
            Assert.AreEqual(0.0, m.ShownCents, 1e-6);
            m.Clear();
            Assert.AreEqual(TunerState.Idle, m.State);
            Assert.IsFalse(m.HasTarget);
        }

        static void Settle(ref TunerModel m, float cents, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                m.SetReading(cents, 0.95f, 0.8f);
                m.Tick(Frame);
            }
        }

        // ---------------------------------------------------------------- timing: easing, fades, the 3 Hz rule

        [Test]
        public void TimingConstants_KeepTheProjectRules()
        {
            // Nothing flashes faster than 3 Hz: one element changes at most once per MinChangeSeconds.
            Assert.GreaterOrEqual(LearnTiming.MinChangeSeconds * LearnTiming.MaxFlashHz, 1f);
            // A verdict is on screen within 350 ms of Show: even a held-back change, plus the ease-in, plus a frame.
            Assert.LessOrEqual(LearnTiming.MinChangeSeconds, LearnTiming.VerdictBudgetSeconds);
            Assert.LessOrEqual(LearnTiming.EaseInSeconds + Frame, LearnTiming.VerdictBudgetSeconds);
            Assert.AreEqual(0.35f, LearnTiming.VerdictBudgetSeconds);
            Assert.GreaterOrEqual(LearnTiming.StartProgress, 0.5f);
            Assert.Greater(LearnTiming.FadeOutSeconds, LearnTiming.EaseInSeconds);
        }

        [Test]
        public void EasingCurves()
        {
            Assert.AreEqual(0.0, LearnTiming.SmoothStep01(0f), 1e-6);
            Assert.AreEqual(0.15625, LearnTiming.SmoothStep01(0.25f), 1e-6);
            Assert.AreEqual(0.5, LearnTiming.SmoothStep01(0.5f), 1e-6);
            Assert.AreEqual(1.0, LearnTiming.SmoothStep01(1f), 1e-6);
            Assert.AreEqual(0.0, LearnTiming.SmoothStep01(-2f), 1e-6);
            Assert.AreEqual(1.0, LearnTiming.SmoothStep01(7f), 1e-6);
            Assert.AreEqual(0.0, LearnTiming.SmoothStep01(float.NaN), 1e-6);

            Assert.AreEqual(0.0, LearnTiming.EaseOutCubic(0f), 1e-6);
            Assert.AreEqual(0.875, LearnTiming.EaseOutCubic(0.5f), 1e-6);
            Assert.AreEqual(1.0, LearnTiming.EaseOutCubic(1f), 1e-6);
            Assert.AreEqual(1.0, LearnTiming.EaseOutCubic(3f), 1e-6);
        }

        [Test]
        public void PopAndPulse_AreSmallSizeChanges_NotFlashes()
        {
            Assert.AreEqual(0.85, LearnTiming.PopScale(0f), 1e-5);
            Assert.AreEqual(0.98125, LearnTiming.PopScale(0.5f * LearnTiming.EaseInSeconds), 1e-4);
            Assert.AreEqual(1.0, LearnTiming.PopScale(LearnTiming.EaseInSeconds), 1e-6);
            Assert.AreEqual(1.0, LearnTiming.PopScale(10f), 1e-6);

            Assert.AreEqual(1.0, LearnTiming.PulseScale(0f), 1e-6);
            Assert.AreEqual(1.12, LearnTiming.PulseScale(0.5f * LearnTiming.PulseSeconds), 1e-4);
            Assert.AreEqual(1.0, LearnTiming.PulseScale(LearnTiming.PulseSeconds), 1e-6);
            Assert.AreEqual(1.0, LearnTiming.PulseScale(-1f), 1e-6);
            Assert.AreEqual(1.0, LearnTiming.PulseScale(float.NaN), 1e-6);
            // One swell lasts 0.25 s, and swells are gated to one per 0.34 s, so the size never wobbles at 3 Hz or more.
            Assert.Less(LearnTiming.PulseSeconds, LearnTiming.MinChangeSeconds);
        }

        [TestCase(1, 1.3f)]
        [TestCase(5, 2.5f)]
        [TestCase(7, 3.1f)]
        [TestCase(20, 4f)]
        [TestCase(0, 1f)]
        [TestCase(-3, 1f)]
        public void ReadSeconds_GrowsWithTheWords(int words, float expected)
        {
            Assert.AreEqual(expected, LearnTiming.ReadSeconds(words), 1e-5);
        }

        [Test]
        public void Fader_StartsHidden_AndCostsNothingWhileNothingMoves()
        {
            var f = new HudFader();
            Assert.IsFalse(f.IsVisible);
            Assert.IsFalse(f.Target);
            Assert.IsTrue(f.IsSettled);
            Assert.AreEqual(0.0, f.Opacity, 1e-6);
            Assert.IsFalse(f.Tick(Frame), "a settled element reports no change, so its owner does no work");
        }

        [Test]
        public void Fader_IsReadableOnTheFirstFrame_AndFullWithinTheEaseIn()
        {
            var f = new HudFader();
            f.Show();
            Assert.IsTrue(f.Tick(0f), "Show must be reported even before any time passes");
            Assert.GreaterOrEqual(f.Opacity, 0.5f, "half bright at once: a verdict is never late because of its animation");
            Assert.AreEqual(0.85, f.Scale, 1e-5);

            float t = 0f;
            int frames = 0;
            while (f.Opacity < 1f && frames < 1000)
            {
                f.Tick(Frame);
                t += Frame;
                frames++;
            }
            Assert.LessOrEqual(t, LearnTiming.EaseInSeconds + 1e-4f);
            Assert.LessOrEqual(t, LearnTiming.VerdictBudgetSeconds);

            for (int i = 0; i < 30; i++) f.Tick(Frame);
            Assert.AreEqual(1.0, f.Scale, 1e-5);
            Assert.IsTrue(f.IsSettled);
            Assert.IsFalse(f.Tick(Frame));
        }

        [Test]
        public void Fader_FadesOutSmoothlyOverTheFadeTime()
        {
            var f = new HudFader();
            f.ShowNow();
            f.Tick(0f);
            Assert.AreEqual(1.0, f.Opacity, 1e-6);
            f.Hide();
            Assert.IsTrue(f.Tick(0.5f * LearnTiming.FadeOutSeconds));
            Assert.AreEqual(0.5, f.Opacity, 1e-3);
            Assert.AreEqual(1.0, f.Scale, 1e-6, "fading out changes brightness only");
            Assert.IsTrue(f.IsVisible);

            float previous = f.Opacity;
            for (int i = 0; i < 60; i++)
            {
                f.Tick(Frame);
                Assert.LessOrEqual(f.Opacity, previous + 1e-6f, "brightness only ever falls while fading out");
                previous = f.Opacity;
            }
            Assert.AreEqual(0.0, f.Opacity, 1e-6);
            Assert.IsFalse(f.IsVisible);
            Assert.IsTrue(f.IsSettled);
        }

        [Test]
        public void Fader_ShowDuringAFadeOutContinuesFromWhereItIs()
        {
            var f = new HudFader();
            f.ShowNow();
            f.Hide();
            f.Tick(0.2f * LearnTiming.FadeOutSeconds);     // 80% along
            float before = f.Opacity;
            f.Show();
            f.Tick(0f);
            Assert.GreaterOrEqual(f.Opacity, before - 1e-6f, "no dip to a lower brightness");
            f.Tick(1f);
            Assert.AreEqual(1.0, f.Opacity, 1e-6);
        }

        [Test]
        public void Fader_PulseSwellsOnceAndOnlyWhileShown()
        {
            var hidden = new HudFader();
            hidden.Pulse();
            Assert.IsFalse(hidden.Tick(Frame), "a hidden element cannot pulse");

            var f = new HudFader();
            f.ShowNow();
            f.Tick(0f);
            f.Pulse();
            Assert.IsTrue(f.Tick(0.5f * LearnTiming.PulseSeconds));
            Assert.AreEqual(1.12, f.Scale, 1e-3);
            Assert.AreEqual(1.0, f.Opacity, 1e-6, "a pulse is a change of size, never of brightness");
            f.Tick(LearnTiming.PulseSeconds);
            Assert.AreEqual(1.0, f.Scale, 1e-5);
            Assert.IsTrue(f.IsSettled);
        }

        [Test]
        public void Fader_HideNowAndShowNowSkipTheAnimation()
        {
            var f = new HudFader();
            f.ShowNow();
            Assert.IsTrue(f.Tick(0f));
            Assert.AreEqual(1.0, f.Opacity, 1e-6);
            Assert.AreEqual(1.0, f.Scale, 1e-6);
            Assert.IsTrue(f.IsSettled);
            f.HideNow();
            Assert.IsTrue(f.Tick(0f));
            Assert.AreEqual(0.0, f.Opacity, 1e-6);
            Assert.IsFalse(f.IsVisible);
        }

        [Test]
        public void Gate_FirstChangePassesAtOnce_ABurstIsHeldBack()
        {
            var g = new ChangeGate();
            Assert.IsFalse(g.Tick(Frame), "nothing requested, nothing passes");
            g.Request();
            Assert.IsTrue(g.Pending);
            Assert.IsTrue(g.Tick(0f), "after a quiet spell a change is not delayed at all");
            Assert.IsFalse(g.Pending);

            g.Request();
            Assert.IsFalse(g.Tick(0f));
            Assert.IsFalse(g.Tick(0.2f));
            Assert.IsTrue(g.Pending);
            Assert.IsTrue(g.Tick(0.2f), "0.4 s after the last change the next one passes");

            g.Request();
            g.Cancel();
            Assert.IsFalse(g.Tick(1f), "a cancelled request never passes");
        }

        [Test]
        public void Gate_NeverLetsMoreThanThreeChangesASecondThrough()
        {
            // Hammer it: a change is requested on every frame for 5 seconds.
            var g = new ChangeGate();
            float now = 0f;
            float last = float.NegativeInfinity;
            int passes = 0;
            for (int i = 0; i < 300; i++)
            {
                g.Request();
                if (g.Tick(Frame))
                {
                    Assert.GreaterOrEqual(now - last, LearnTiming.MinChangeSeconds - 1e-3f, "pass " + passes);
                    last = now;
                    passes++;
                }
                now += Frame;
            }
            Assert.LessOrEqual(passes, 15);                    // 5 s at under 3 a second
            Assert.GreaterOrEqual(passes, 13, "but it must not starve either");
            Assert.Greater(3f * LearnTiming.MinChangeSeconds, 1f, "three gaps are longer than a second, so a fourth change cannot fit in it");
        }

        // ---------------------------------------------------------------- text: length limits

        [TestCase("Arch finger 3, B string muted", 6)]
        [TestCase("String 2 muted — arch finger 1", 6)]      // the dash is not a word
        [TestCase("Finger 2: press string 5, fret 2", 7)]
        [TestCase("Heard C - target is G", 5)]
        [TestCase("Don't strum string 6", 4)]
        [TestCase("  two   words ", 2)]
        [TestCase("Clean!", 1)]
        [TestCase("—", 0)]
        [TestCase("", 0)]
        [TestCase(null, 0)]
        public void CountWords(string text, int expected)
        {
            Assert.AreEqual(expected, LearnText.CountWords(text));
        }

        [Test]
        public void EveryPlannedMessage_IsSevenWordsOrFewer_AndOneGlanceWide()
        {
            Assert.AreEqual(7, LearnText.MaxTipWords);
            foreach (string message in PlannedMessages)
            {
                Assert.IsTrue(LearnText.WithinWordLimit(message), message);
                string a, b;
                LearnText.WrapTwoLines(message, out a, out b);
                Assert.LessOrEqual(a.Length, LearnText.MaxLineChars, message);
                Assert.LessOrEqual(b.Length, LearnText.MaxLineChars, message);
                Assert.IsTrue(LearnText.FitsAtAGlance(message), message);
            }
            Assert.IsFalse(LearnText.WithinWordLimit("This tip has far too many words to read"));
            Assert.IsFalse(LearnText.FitsAtAGlance("Supercalifragilisticexpialidocious"));
        }

        [TestCase("Arch finger 3, B string muted", "Arch finger 3", "B string muted")]
        [TestCase("String 2 muted — arch finger 1", "String 2 muted", "arch finger 1")]
        [TestCase("String 2 muted - arch finger 1", "String 2 muted", "arch finger 1")]
        [TestCase("Finger 2: press string 5, fret 2", "Finger 2: press", "string 5, fret 2")]
        [TestCase("Finger 2: string 5, fret 2", "Finger 2", "string 5, fret 2")]
        [TestCase("Finger 3: move to fret 3", "Finger 3", "move to fret 3")]
        [TestCase("Heard C, target is G", "Heard C", "target is G")]
        [TestCase("Heard C — target is G", "Heard C", "target is G")]
        [TestCase("Don't strum string 6", "Don't strum", "string 6")]
        [TestCase("Pluck string 5 again, slowly", "Pluck string 5", "again, slowly")]
        [TestCase("Start moving one beat earlier", "Start moving", "one beat earlier")]
        [TestCase("Skip string 6", "Skip string 6", "")]                  // short: one line
        [TestCase("Clean!", "Clean!", "")]
        [TestCase("  Skip string 6  ", "Skip string 6", "")]
        [TestCase("Supercalifragilistic", "Supercalifragilistic", "")]   // nowhere to break: never cut
        [TestCase("", "", "")]
        [TestCase(null, "", "")]
        public void WrapTwoLines_BreaksWhereTheSentenceBreaks(string text, string line1, string line2)
        {
            string a, b;
            LearnText.WrapTwoLines(text, out a, out b);
            Assert.AreEqual(line1, a);
            Assert.AreEqual(line2, b);
        }

        [Test]
        public void WrapTwoLines_NeverLosesAWord_AndNeverStrandsANumber()
        {
            foreach (string message in PlannedMessages)
            {
                string a, b;
                LearnText.WrapTwoLines(message, out a, out b);
                Assert.AreEqual(LettersAndDigits(message), LettersAndDigits(a + b), message);
                if (b.Length > 0) Assert.IsFalse(char.IsDigit(b[0]), "'" + message + "' must not start its second line with a number");
            }
        }

        static string LettersAndDigits(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s)
                if (char.IsLetterOrDigit(c) || c == '\'' || c == '!') sb.Append(c);
            return sb.ToString();
        }

        [Test]
        public void WidthEstimate_MatchesBoldArialMetrics()
        {
            Assert.AreEqual(4.2682, LearnText.WidthInCaps("Clean!"), 1e-3);
            Assert.AreEqual(8.6145, LearnText.WidthInCaps("Arch finger 3"), 1e-3);
            Assert.AreEqual(9.8547, LearnText.WidthInCaps("B string muted"), 1e-3);
            Assert.AreEqual(0.9316, LearnText.WidthInCaps("—"), 1e-3);      // anything outside ASCII counts as an average capital
            Assert.AreEqual(0f, LearnText.WidthInCaps(""));
            Assert.AreEqual(0f, LearnText.WidthInCaps(null));
            Assert.Less(LearnText.WidthInCaps("ill"), LearnText.WidthInCaps("mmm"));
        }

        [Test]
        public void TheBriefsExampleTip_IsAboutSixteenDegreesWide()
        {
            // Icon, gap and the wider line of "Arch finger 3" / "B string muted" at the tip's text size.
            float width = TipLine.IconDegrees + TipLine.GapDegrees + TipLine.BlockWidthInCaps("Arch finger 3", "B string muted") * TipLine.TextDegrees;
            Assert.AreEqual(15.611, width, 0.01);
            Assert.LessOrEqual(width, 16f);
        }

        [Test]
        public void CachedWords_AreNotRebuilt_SoShowingAPromptAllocatesNothing()
        {
            Assert.AreEqual("5", LearnText.Digit(5));
            Assert.AreSame(LearnText.Digit(5), LearnText.Digit(5));
            Assert.AreEqual("12", LearnText.Digit(12));

            Assert.AreEqual("Finger 2", LearnText.FingerHeadline(2));
            Assert.AreSame(LearnText.FingerHeadline(4), LearnText.FingerHeadline(4));
            Assert.AreEqual("Finger 7", LearnText.FingerHeadline(7));

            Assert.AreEqual("string 5, fret 2", LearnText.StringFret(5, 2));
            Assert.AreSame(LearnText.StringFret(5, 2), LearnText.StringFret(5, 2));
            Assert.AreEqual("string 6, open", LearnText.StringFret(6, 0));
            Assert.AreEqual("string 1, fret 12", LearnText.StringFret(1, 12));
            Assert.AreEqual("string 9, fret 30", LearnText.StringFret(9, 30));   // out of range: still words, never an exception

            Assert.AreEqual("string 6", LearnText.StringLabel(6));
            Assert.AreSame(LearnText.StringLabel(1), LearnText.StringLabel(1));
            Assert.AreEqual("Pluck string 6", LearnText.PluckPrompt(6));
            Assert.AreSame(LearnText.PluckPrompt(3), LearnText.PluckPrompt(3));
        }

        [Test]
        public void CentsText_IsRoundedSignedClampedAndCached()
        {
            Assert.AreEqual("-12 cents", LearnText.CentsText(-12.4f));
            Assert.AreEqual("+7 cents", LearnText.CentsText(7.4f));
            Assert.AreEqual("0 cents", LearnText.CentsText(0.2f));
            Assert.AreEqual("0 cents", LearnText.CentsText(-0.3f));
            Assert.AreEqual("+99 cents", LearnText.CentsText(250f));
            Assert.AreEqual("-99 cents", LearnText.CentsText(-250f));
            Assert.AreEqual("0 cents", LearnText.CentsText(float.NaN));
            Assert.AreSame(LearnText.CentsText(-11.8f), LearnText.CentsText(-12.2f));
        }

        // ---------------------------------------------------------------- marks and icons: shape as well as colour

        static readonly StringMark[] Marks =
        {
            StringMark.Correct, StringMark.Wrong, StringMark.Muted, StringMark.Unclear, StringMark.LetRing,
        };

        [Test]
        public void StringMarks_CheckIsGreen_CrossIsRedOrange_AndTheShapesDiffer()
        {
            Assert.AreEqual(LearnGlyph.Check, StringMarkStyle.Glyph(StringMark.Correct));
            Assert.AreEqual(LearnGlyph.Cross, StringMarkStyle.Glyph(StringMark.Wrong));

            Color good = StringMarkStyle.Color(StringMark.Correct);
            Color bad = StringMarkStyle.Color(StringMark.Wrong);
            Assert.Greater(good.g, good.r, "green");
            Assert.Greater(good.g, good.b);
            Assert.AreEqual(1.0, bad.r, 1e-6, "red-orange");
            Assert.Greater(bad.g, bad.b);
            Assert.Less(bad.g, 0.6f);

            // Every mark has its own shape: colour never carries the meaning alone.
            for (int i = 0; i < Marks.Length; i++)
                for (int j = i + 1; j < Marks.Length; j++)
                    Assert.AreNotEqual(StringMarkStyle.Glyph(Marks[i]), StringMarkStyle.Glyph(Marks[j]), Marks[i] + " and " + Marks[j]);
        }

        [Test]
        public void StringMarks_MarkShapesHaveDifferentOutlines()
        {
            // Not just different enum values: the drawings themselves differ in width, height or inked area by more than 10%.
            var g = new StrokeGeometry();
            var w = new float[Marks.Length];
            var h = new float[Marks.Length];
            var area = new float[Marks.Length];
            for (int i = 0; i < Marks.Length; i++)
            {
                LearnGlyphs.Build(StringMarkStyle.Glyph(Marks[i]), g);
                Vector2 min, max;
                Assert.IsTrue(g.TryGetBounds(out min, out max));
                w[i] = max.x - min.x;
                h[i] = max.y - min.y;
                area[i] = g.TriangleArea();
            }
            for (int i = 0; i < Marks.Length; i++)
            {
                for (int j = i + 1; j < Marks.Length; j++)
                {
                    bool differs = Differs(w[i], w[j]) || Differs(h[i], h[j]) || Differs(area[i], area[j]);
                    Assert.IsTrue(differs, Marks[i] + " and " + Marks[j] + " look alike");
                }
            }
        }

        static bool Differs(float a, float b)
        {
            return Mathf.Abs(a - b) > 0.1f * Mathf.Max(a, b);
        }

        [Test]
        public void StringMarks_APassFadesByItself_AProblemStays()
        {
            Assert.AreEqual(1.6f, StringMarkStyle.DefaultHoldSeconds(StringMark.Correct));
            Assert.Greater(StringMarkStyle.DefaultHoldSeconds(StringMark.Correct), LearnTiming.MinChangeSeconds);
            Assert.IsTrue(float.IsPositiveInfinity(StringMarkStyle.DefaultHoldSeconds(StringMark.Wrong)));
            Assert.IsTrue(float.IsPositiveInfinity(StringMarkStyle.DefaultHoldSeconds(StringMark.Muted)));
            Assert.IsTrue(float.IsPositiveInfinity(StringMarkStyle.DefaultHoldSeconds(StringMark.Unclear)));
            Assert.IsTrue(float.IsPositiveInfinity(StringMarkStyle.DefaultHoldSeconds(StringMark.LetRing)));
        }

        [Test]
        public void StringMarks_HavePlainWordsForTheSpectatorPanel()
        {
            Assert.AreEqual("correct", StringMarkStyle.Word(StringMark.Correct));
            Assert.AreEqual("muted", StringMarkStyle.Word(StringMark.Muted));
            Assert.AreEqual(string.Empty, StringMarkStyle.Word(StringMark.None));
            foreach (StringMark mark in Marks) Assert.Greater(StringMarkStyle.Word(mark).Length, 0, mark.ToString());
        }

        [Test]
        public void Palette_HasNoDarkColours()
        {
            // Black is invisible on the additive glasses, so even the quietest colour must be bright in at least one channel.
            Color[] colours =
            {
                LearnPalette.Good, LearnPalette.Bad, LearnPalette.Caution, LearnPalette.Text, LearnPalette.Info,
                LearnPalette.Accent, LearnPalette.Quiet,
                LearnPalette.Finger(1, true), LearnPalette.Finger(2, true), LearnPalette.Finger(3, true), LearnPalette.Finger(4, true),
            };
            foreach (Color c in colours)
            {
                Assert.GreaterOrEqual(Mathf.Max(c.r, Mathf.Max(c.g, c.b)), 0.5f, c.ToString());
                Assert.AreEqual(1.0, c.a, 1e-6);
            }
        }

        [Test]
        public void FingerColours_FollowTheFlag()
        {
            for (int finger = 1; finger <= 4; finger++)
                Assert.AreEqual(LearnPalette.Accent, LearnPalette.Finger(finger, false), "flag off: every finger is the ghost-hand blue");
            for (int a = 1; a <= 4; a++)
                for (int b = a + 1; b <= 4; b++)
                    Assert.AreNotEqual(LearnPalette.Finger(a, true), LearnPalette.Finger(b, true), a + " and " + b);
            Assert.AreEqual(LearnPalette.Accent, LearnPalette.Finger(9, true));
        }

        [Test]
        public void TipIcons_EveryIconButNoneHasADrawing()
        {
            Assert.IsFalse(TipIconStyle.HasGlyph(TipIcon.None));
            foreach (TipIcon icon in (TipIcon[])Enum.GetValues(typeof(TipIcon)))
            {
                if (icon == TipIcon.None) continue;
                Assert.IsTrue(TipIconStyle.HasGlyph(icon), icon.ToString());
            }
            Assert.AreEqual(LearnGlyph.Check, TipIconStyle.Glyph(TipIcon.Good));
            Assert.AreEqual(LearnPalette.Good, TipIconStyle.Color(TipIcon.Good));
            Assert.AreEqual(LearnGlyph.Arch, TipIconStyle.Glyph(TipIcon.Arch));
            Assert.AreEqual(LearnPalette.Caution, TipIconStyle.Color(TipIcon.Arch));
            Assert.AreNotEqual(TipIconStyle.Glyph(TipIcon.Press), TipIconStyle.Glyph(TipIcon.Move));
            Assert.AreNotEqual(TipIconStyle.Glyph(TipIcon.Skip), TipIconStyle.Glyph(TipIcon.Mismatch));
        }

        // ---------------------------------------------------------------- drawn shapes: the geometry itself

        [Test]
        public void EveryGlyph_IsWellFormed_Inked_AndInsideItsBox()
        {
            Assert.AreEqual(LearnGlyphs.Count, Enum.GetValues(typeof(LearnGlyph)).Length, "LearnGlyphs.Count must match the enum");
            var g = new StrokeGeometry();
            for (int i = 0; i < LearnGlyphs.Count; i++)
            {
                var glyph = (LearnGlyph)i;
                LearnGlyphs.Build(glyph, g);
                Assert.IsTrue(g.IsWellFormed(), glyph + ": triangle indices and coordinates");
                Assert.GreaterOrEqual(g.Triangles.Count, 3, glyph.ToString());
                Assert.Greater(g.TriangleArea(), 0.004f, glyph + " has no ink");

                Vector2 min, max;
                Assert.IsTrue(g.TryGetBounds(out min, out max), glyph.ToString());
                if (LearnGlyphs.FitsUnitBox(glyph))
                {
                    Assert.GreaterOrEqual(min.x, -0.5005f, glyph.ToString());
                    Assert.GreaterOrEqual(min.y, -0.5005f, glyph.ToString());
                    Assert.LessOrEqual(max.x, 0.5005f, glyph.ToString());
                    Assert.LessOrEqual(max.y, 0.5005f, glyph.ToString());
                    Assert.Greater(Mathf.Max(max.x - min.x, max.y - min.y), 0.6f, glyph + " should fill most of its box");
                }
            }
        }

        [Test]
        public void MarkStrokes_AreHeavy()
        {
            // Contrast on an additive display comes from stroke weight: a 1.5 degree mark gets strokes of about 10 glasses pixels.
            Assert.GreaterOrEqual(LearnGlyphs.MarkStroke, 0.15f);
            Assert.GreaterOrEqual(LearnGlyphs.IconStroke, 0.1f);
            float strokeDegrees = LearnGlyphs.MarkStroke * LearnHudLayout.MarkDegrees;
            Assert.GreaterOrEqual(strokeDegrees * HudText.DefaultPixelsPerDegree, 9f);
        }

        [Test]
        public void MutedBar_IsFlat_AndTheCrossIsSymmetric()
        {
            var g = new StrokeGeometry();
            Vector2 min, max;

            LearnGlyphs.Build(LearnGlyph.Bar, g);
            g.TryGetBounds(out min, out max);
            Assert.Less(max.y - min.y, 0.3f * (max.x - min.x));

            LearnGlyphs.Build(LearnGlyph.Cross, g);
            g.TryGetBounds(out min, out max);
            Assert.AreEqual(0.0, min.x + max.x, 1e-4);
            Assert.AreEqual(0.0, min.y + max.y, 1e-4);
            Assert.AreEqual(max.x - min.x, max.y - min.y, 1e-4);

            LearnGlyphs.Build(LearnGlyph.Check, g);
            g.TryGetBounds(out min, out max);
            Assert.Greater(max.x - min.x, max.y - min.y, "a check is wider than it is tall, unlike the square cross");
        }

        [Test]
        public void TunerDial_SpansFiftyCentsEachSide_WithAHeavierInTuneZone()
        {
            var g = new StrokeGeometry();
            Vector2 min, max;

            LearnGlyphs.Build(LearnGlyph.DialTrack, g);
            g.TryGetBounds(out min, out max);
            Assert.AreEqual(0.0, min.x + max.x, 1e-3, "the dial is symmetric");
            Assert.AreEqual(0.7414, max.x, 0.01);          // the +50 cent tick: 1.13 * sin(40 deg), plus half its width
            Assert.AreEqual(1.145, max.y, 0.01);           // the centre tick
            Assert.Greater(min.y, 0.7f, "the dial is only an arc: the middle stays free for the note name");
            float trackThickness = 0.03f;

            LearnGlyphs.Build(LearnGlyph.DialZone, g);
            g.TryGetBounds(out min, out max);
            Assert.AreEqual(0.07324, max.x, 1e-3);         // +5 cents = 4 degrees of needle angle, at the zone's outer edge
            Assert.AreEqual(-0.07324, min.x, 1e-3);
            Assert.Greater(max.y - min.y, 2.5f * trackThickness, "the zone is drawn heavier than the track, not only in another colour");

            LearnGlyphs.Build(LearnGlyph.DialNeedle, g);
            g.TryGetBounds(out min, out max);
            Assert.AreEqual(0.0, min.x + max.x, 1e-4, "the pointer is drawn straight up; rotation leans it");
            Assert.AreEqual(LearnGlyphs.NeedleInner - 0.025, min.y, 1e-3);
            Assert.AreEqual(LearnGlyphs.NeedleOuter + 0.025, max.y, 1e-3);
            Assert.Greater(LearnGlyphs.NeedleOuter, 1f, "the pointer reaches the dial");
            Assert.Greater(LearnGlyphs.NeedleInner, 0.5f, "and leaves the middle to the note name");
        }

        [Test]
        public void StrokeGeometry_AnglesRunClockwiseFromStraightUp()
        {
            AssertPoint(0f, 1f, StrokeGeometry.OnCircle(Vector2.zero, 1f, 0f));
            AssertPoint(1f, 0f, StrokeGeometry.OnCircle(Vector2.zero, 1f, 90f));
            AssertPoint(0f, -1f, StrokeGeometry.OnCircle(Vector2.zero, 1f, 180f));
            AssertPoint(-1f, 0f, StrokeGeometry.OnCircle(Vector2.zero, 1f, 270f));
            AssertPoint(2.5f, 3f, StrokeGeometry.OnCircle(new Vector2(2f, 3f), 0.5f, 90f));
        }

        [Test]
        public void StrokeGeometry_Primitives()
        {
            var g = new StrokeGeometry();
            Assert.IsFalse(g.AddSegment(Vector2.zero, Vector2.zero, 0.1f), "a zero-length stroke adds nothing");
            Assert.AreEqual(0, g.Vertices.Count);
            Vector2 min, max;
            Assert.IsFalse(g.TryGetBounds(out min, out max));

            Assert.IsTrue(g.AddSegment(new Vector2(0f, 0f), new Vector2(2f, 0f), 0.5f));
            Assert.AreEqual(1.0, g.TriangleArea(), 1e-5);          // 2 long, 0.5 wide
            g.TryGetBounds(out min, out max);
            AssertPoint(0f, -0.25f, min);
            AssertPoint(2f, 0.25f, max);

            g.Clear();
            g.AddDisc(Vector2.zero, 0.5f, 32);
            Assert.AreEqual(0.780361, g.TriangleArea(), 1e-4);     // a 32-sided polygon of radius 0.5
            Assert.IsTrue(g.IsWellFormed());

            g.Clear();
            g.AddArc(Vector2.zero, 1f, 0f, 90f, 0.1f, 64, false);
            Assert.AreEqual(0.157080, g.TriangleArea(), 1e-3);     // a quarter of a ring: (pi / 2) * 1 * 0.1
            Assert.AreEqual(2 * 65, g.Vertices.Count);
            g.TryGetBounds(out min, out max);
            Assert.AreEqual(1.05, max.x, 1e-4);
            Assert.AreEqual(1.05, max.y, 1e-4);
            Assert.GreaterOrEqual(min.x, -1e-5f, "a quarter arc from straight up, clockwise, stays in the upper right");
            Assert.GreaterOrEqual(min.y, -1e-5f);

            g.Clear();
            g.AddCapsule(new Vector2(0.3f, 0.3f), new Vector2(0.3f, 0.3f), 0.2f, 12);   // both ends at one point: just a dot
            Assert.IsTrue(g.IsWellFormed());
            Assert.Greater(g.TriangleArea(), 0.02f);
        }

        // ---------------------------------------------------------------- finger prompt

        [TestCase(0, 2, 3, FingerPip.Done)]
        [TestCase(1, 2, 3, FingerPip.Active)]
        [TestCase(2, 2, 3, FingerPip.Todo)]
        [TestCase(3, 2, 3, FingerPip.Hidden)]      // finger 2 of 3: only three pips
        [TestCase(0, 1, 1, FingerPip.Active)]
        [TestCase(1, 1, 1, FingerPip.Hidden)]
        [TestCase(3, 4, 4, FingerPip.Active)]
        [TestCase(2, 4, 4, FingerPip.Done)]
        [TestCase(0, 0, 3, FingerPip.Active)]      // a step below 1 is treated as the first
        [TestCase(2, 9, 3, FingerPip.Active)]      // a step beyond the last is treated as the last
        [TestCase(3, 2, 9, FingerPip.Todo)]        // more than four steps: four pips
        [TestCase(4, 2, 9, FingerPip.Hidden)]
        [TestCase(0, 1, 0, FingerPip.Active)]      // no count: one pip
        [TestCase(-1, 1, 3, FingerPip.Hidden)]
        public void FingerPips(int pipIndex, int step, int stepCount, FingerPip expected)
        {
            Assert.AreEqual(expected, FingerStepPrompt.PipFor(pipIndex, step, stepCount));
        }

        [Test]
        public void FingerPips_ExactlyOneIsActive()
        {
            for (int count = 1; count <= FingerStepPrompt.MaxSteps; count++)
            {
                for (int step = 1; step <= count; step++)
                {
                    int active = 0, drawn = 0;
                    for (int i = 0; i < FingerStepPrompt.MaxSteps; i++)
                    {
                        FingerPip pip = FingerStepPrompt.PipFor(i, step, count);
                        if (pip == FingerPip.Active) active++;
                        if (pip != FingerPip.Hidden) drawn++;
                    }
                    Assert.AreEqual(1, active, step + " of " + count);
                    Assert.AreEqual(count, drawn, step + " of " + count);
                }
            }
        }

        [Test]
        public void FingerPrompt_WordsAreTheBriefedOnes()
        {
            // "Finger 2: string 5, fret 2", set as a headline and a detail line.
            Assert.AreEqual("Finger 2", LearnText.FingerHeadline(2));
            Assert.AreEqual("string 5, fret 2", LearnText.StringFret(5, 2));
            Assert.LessOrEqual(LearnText.StringFret(6, 12).Length, LearnText.MaxLineChars);
        }

        // ---------------------------------------------------------------- strum verdict

        [Test]
        public void StrumVerdict_WordsAndHoldTimes()
        {
            Assert.AreEqual("Clean!", StrumVerdict.CleanText);
            Assert.AreEqual(StrumVerdict.DefaultIssueText, StrumVerdict.NormalizeIssue(null));
            Assert.AreEqual(StrumVerdict.DefaultIssueText, StrumVerdict.NormalizeIssue(""));
            Assert.AreEqual(StrumVerdict.DefaultIssueText, StrumVerdict.NormalizeIssue("   "));
            Assert.AreEqual("Heard C, target is G", StrumVerdict.NormalizeIssue("  Heard C, target is G "));

            Assert.AreEqual(1.3, StrumVerdict.HoldSeconds(true, null), 1e-5);
            Assert.AreEqual(1.3, StrumVerdict.HoldSeconds(true, "ignored when clean"), 1e-5);
            Assert.AreEqual(2.5, StrumVerdict.HoldSeconds(false, "Heard C, target is G"), 1e-5);   // 5 words
            Assert.AreEqual(2.2, StrumVerdict.HoldSeconds(false, "Don't strum string 6"), 1e-5);   // 4 words
            Assert.AreEqual(1.9, StrumVerdict.HoldSeconds(false, null), 1e-5);                     // "Almost: strum again"
            // Held long enough to read, and always longer than the 3 Hz gate, so a verdict can never just blink.
            Assert.Greater(StrumVerdict.HoldSeconds(true, null), 2f * LearnTiming.MinChangeSeconds);
        }

        // ---------------------------------------------------------------- sizes: legibility minimums

        [Test]
        public void TextSizes_RespectTheProjectMinimums()
        {
            float min = HudText.MinTextDegrees;
            Assert.GreaterOrEqual(TipLine.TextDegrees, min);
            Assert.GreaterOrEqual(StrumVerdict.IssueTextDegrees, min);
            Assert.GreaterOrEqual(StrumVerdict.CleanTextDegrees, 2f * min, "'Clean!' is big");
            Assert.GreaterOrEqual(FingerStepPrompt.DetailDegrees, min);
            Assert.GreaterOrEqual(FingerStepPrompt.HeadlineDegrees, min);
            Assert.GreaterOrEqual(StringMarks.DigitDegrees, min);
            Assert.GreaterOrEqual(TunerNeedle.StringDegrees, min);
            Assert.GreaterOrEqual(TunerNeedle.StatusDegrees, min);
            Assert.GreaterOrEqual(TunerNeedle.DebugDegrees, min);
            Assert.GreaterOrEqual(TunerNeedle.NoteDegrees, HudText.ChordNameMinDegrees, "the note name is as large as a chord name");
            Assert.GreaterOrEqual(LearnHudLayout.MarkDegrees, min);
            Assert.Greater(StrumVerdict.CleanMarkDegrees, LearnHudLayout.MarkDegrees);
        }

        // ---------------------------------------------------------------- layout

        [Test]
        public void FretDistances_FollowTheTwelfthRootRule()
        {
            // docs/MASTER_PLAN.md Appendix B4, L = 648 mm: d1 = 36.4, d5 = 162.5, d12 = 324.0 mm.
            Assert.AreEqual(0.03637, LearnHudLayout.FretDistanceM(0.648f, 1), 1e-4);
            Assert.AreEqual(0.16255, LearnHudLayout.FretDistanceM(0.648f, 5), 1e-4);
            Assert.AreEqual(0.32400, LearnHudLayout.FretDistanceM(0.648f, 12), 1e-4);
            Assert.AreEqual(0f, LearnHudLayout.FretDistanceM(0.648f, 0));
            Assert.AreEqual(0f, LearnHudLayout.FretDistanceM(0f, 5));
            Assert.AreEqual(0f, LearnHudLayout.FretDistanceM(float.NaN, 5));
        }

        [Test]
        public void NeckCard_IsEightCentimetresAboveFretFive()
        {
            Vector3 card = LearnHudLayout.NeckCardLocal(LearnHudLayout.DefaultScaleLengthM);
            AssertVector(0.16255f, 0.08f, 0f, card, 1e-4f);
            Assert.AreEqual(5, LearnHudLayout.CardFret);
            AssertVector(0.2f, 0.1f, 0f, LearnHudLayout.NeckCardLocal(0.2f, 0.1f), 1e-6f);
        }

        [Test]
        public void TheMessage_IsWithinTenDegreesOfTheNeck_ForATypicalPlayer()
        {
            Vector3 eye = LearnHudLayout.TypicalEyeInNeckFrame;
            Vector3 card = LearnHudLayout.NeckCardLocal(LearnHudLayout.DefaultScaleLengthM);
            float neckLength = LearnHudLayout.FretDistanceM(LearnHudLayout.DefaultScaleLengthM, 12);
            float angle = LearnHudLayout.AngleFromNeckDegrees(eye, card, Vector3.zero, Vector3.right, neckLength);
            Assert.AreEqual(9.59, angle, 0.05);
            Assert.LessOrEqual(angle, LearnHudLayout.GlanceDegrees);
            Assert.AreEqual(0.4732, (card - eye).magnitude, 1e-3, "about half a metre away, the default viewing distance");
            Assert.AreEqual(1.0, LearnHudLayout.TypicalUpInNeckFrame.magnitude, 1e-3);
        }

        [Test]
        public void AngleBetween_KnownAngles()
        {
            Assert.AreEqual(90.0, LearnHudLayout.AngleBetweenDegrees(Vector3.zero, Vector3.right, Vector3.up), 1e-3);
            Assert.AreEqual(45.0, LearnHudLayout.AngleBetweenDegrees(Vector3.zero, Vector3.forward, new Vector3(1f, 0f, 1f)), 1e-3);
            Assert.AreEqual(0.0, LearnHudLayout.AngleBetweenDegrees(Vector3.one, new Vector3(1f, 1f, 3f), new Vector3(1f, 1f, 9f)), 1e-2);
            Assert.AreEqual(180.0, LearnHudLayout.AngleBetweenDegrees(Vector3.zero, Vector3.left, Vector3.right), 1e-2);
            Assert.AreEqual(0f, LearnHudLayout.AngleBetweenDegrees(Vector3.zero, Vector3.zero, Vector3.right));   // a point at the eye
        }

        [Test]
        public void Facing_PointsAwayFromTheEye_WithASquareUnitUp()
        {
            Vector3 eye = LearnHudLayout.TypicalEyeInNeckFrame;
            Vector3 card = LearnHudLayout.NeckCardLocal(LearnHudLayout.DefaultScaleLengthM);
            Vector3 forward, up;
            Assert.IsTrue(LearnHudLayout.TryFacing(eye, card, LearnHudLayout.TypicalUpInNeckFrame, out forward, out up));
            AssertVector(-0.4173f, 0f, 0.9088f, forward, 1e-3f);        // from the eye to the card: HudText is read from behind +Z
            AssertVector(-0.8084f, 0.4569f, -0.3712f, up, 2e-3f);
            Assert.AreEqual(0.0, Vector3.Dot(forward, up), 1e-5);
            Assert.AreEqual(1.0, up.magnitude, 1e-5);
            Assert.AreEqual(1.0, forward.magnitude, 1e-5);
        }

        [Test]
        public void Facing_SurvivesBadInput()
        {
            Vector3 forward, up;
            Assert.IsFalse(LearnHudLayout.TryFacing(Vector3.one, Vector3.one, Vector3.up, out forward, out up), "the eye is at the point");
            AssertVector(0f, 0f, 1f, forward, 1e-6f);
            AssertVector(0f, 1f, 0f, up, 1e-6f);

            // An up hint along the line of sight is useless: some square direction is chosen instead of NaN.
            Assert.IsTrue(LearnHudLayout.TryFacing(Vector3.zero, Vector3.up * 2f, Vector3.up, out forward, out up));
            AssertVector(0f, 1f, 0f, forward, 1e-6f);
            Assert.AreEqual(0.0, Vector3.Dot(forward, up), 1e-5);
            Assert.AreEqual(1.0, up.magnitude, 1e-5);
        }

        [Test]
        public void MarkRow_ForATypicalPlayer_RunsSquareToTheNeck_WithStringOneOnTheFretboardSide()
        {
            Vector3 eye = LearnHudLayout.TypicalEyeInNeckFrame;
            Vector3 centre, row;
            Assert.IsTrue(LearnHudLayout.TryMarkRow(eye, Vector3.zero, Vector3.right, Vector3.forward, Vector3.up,
                LearnHudLayout.MarkBeyondNutM, out centre, out row));

            AssertVector(-0.03f, 0f, 0f, centre, 1e-6f);               // 3 cm beyond the nut, over the headstock
            AssertVector(0f, 0.9831f, 0.1829f, row, 1e-3f);
            Assert.AreEqual(1.0, row.magnitude, 1e-5);

            Vector3 view = (centre - eye).normalized;
            Assert.AreEqual(0.0, Vector3.Dot(row, view), 1e-5, "the row faces the eye: no foreshortening");
            Vector3 neckAsSeen = Vector3.right - Vector3.Dot(Vector3.right, view) * view;
            Assert.AreEqual(0.0, Vector3.Dot(row, neckAsSeen.normalized), 1e-5, "and crosses the neck at a right angle, like the X/O row of a chord diagram");
        }

        [Test]
        public void MarkRow_SixMarksNeverTouch_AndStayCloseToTheNut()
        {
            Vector3 eye = LearnHudLayout.TypicalEyeInNeckFrame;
            Vector3 centre, row;
            LearnHudLayout.TryMarkRow(eye, Vector3.zero, Vector3.right, Vector3.forward, Vector3.up,
                LearnHudLayout.MarkBeyondNutM, out centre, out row);
            float distance = (centre - eye).magnitude;
            Assert.AreEqual(0.586, distance, 1e-3);
            float pitch = HudText.SizeForAngularHeight(LearnHudLayout.MarkPitchDegrees, distance);
            Assert.AreEqual(0.01841, pitch, 1e-4);

            Assert.Greater(LearnHudLayout.MarkPitchDegrees, LearnHudLayout.MarkDegrees, "the spacing is wider than a mark");
            Vector3 previous = Vector3.zero;
            for (int s = 1; s <= 6; s++)
            {
                Vector3 p = LearnHudLayout.MarkSlot(centre, row, s, pitch);
                Assert.LessOrEqual(LearnHudLayout.AngleBetweenDegrees(eye, p, Vector3.zero), 5.1f, "string " + s + " is within a glance of the nut");
                if (s > 1)
                {
                    float apart = LearnHudLayout.AngleBetweenDegrees(eye, p, previous);
                    Assert.AreEqual(1.8, apart, 0.02, "strings " + (s - 1) + " and " + s);
                    Assert.Greater(apart, LearnHudLayout.MarkDegrees);
                }
                previous = p;
            }

            // The real strings are far too close together for marks: 7 mm at 0.59 m is under 0.7 degrees.
            Assert.Less(HudText.AngularHeightForSize(0.007f, distance), 0.5f * LearnHudLayout.MarkDegrees);
        }

        [Test]
        public void MarkRow_OrdersTheStringsAsTheyAppear()
        {
            Vector3 centre, row;

            // Seen face-on (a teacher's view, or the spectator): the row lies along the strings' own direction.
            Assert.IsTrue(LearnHudLayout.TryMarkRow(new Vector3(0f, 0.5f, 0f), Vector3.zero, Vector3.right, Vector3.forward, Vector3.up, 0.03f, out centre, out row));
            AssertVector(0f, 0f, 1f, row, 1e-3f);

            // Seen exactly edge-on from above the low-E side: string 1 goes to the side the fretboard faces.
            Assert.IsTrue(LearnHudLayout.TryMarkRow(new Vector3(0f, 0f, -0.5f), Vector3.zero, Vector3.right, Vector3.forward, Vector3.up, 0.03f, out centre, out row));
            AssertVector(0f, 1f, 0f, row, 1e-3f);

            // A mirrored (left-handed) neck gives the mirrored row.
            Vector3 eye = LearnHudLayout.TypicalEyeInNeckFrame;
            Vector3 mirroredEye = new Vector3(-eye.x, eye.y, eye.z);
            Vector3 rowRight, rowLeft, c1, c2;
            LearnHudLayout.TryMarkRow(eye, Vector3.zero, Vector3.right, Vector3.forward, Vector3.up, 0.03f, out c1, out rowRight);
            LearnHudLayout.TryMarkRow(mirroredEye, Vector3.zero, Vector3.left, Vector3.forward, Vector3.up, 0.03f, out c2, out rowLeft);
            AssertVector(-rowRight.x, rowRight.y, rowRight.z, rowLeft, 1e-4f);
            AssertVector(-c1.x, c1.y, c1.z, c2, 1e-6f);
        }

        [Test]
        public void MarkRow_SurvivesBadInput()
        {
            Vector3 centre, row;
            Assert.IsFalse(LearnHudLayout.TryMarkRow(new Vector3(-0.03f, 0f, 0f), Vector3.zero, Vector3.right, Vector3.forward, Vector3.up, 0.03f, out centre, out row), "the eye is at the row");
            AssertVector(0f, 0f, 1f, row, 1e-6f);
            Assert.IsFalse(LearnHudLayout.TryMarkRow(new Vector3(1f, 0f, 0f), Vector3.zero, Vector3.right, Vector3.forward, Vector3.up, 0.03f, out centre, out row), "looking exactly along the neck");
            AssertVector(0f, 0f, 1f, row, 1e-6f);
            AssertVector(-0.03f, 0f, 0f, centre, 1e-6f);
        }

        [TestCase(1, 2.5f)]
        [TestCase(2, 1.5f)]
        [TestCase(3, 0.5f)]
        [TestCase(4, -0.5f)]
        [TestCase(6, -2.5f)]
        [TestCase(0, 2.5f)]       // out of range: clamped to the nearest string
        [TestCase(9, -2.5f)]
        public void MarkSlotOffset_CentresTheSixStrings(int stringNumber, float expected)
        {
            Assert.AreEqual(expected, LearnHudLayout.MarkSlotOffset(stringNumber), 1e-6);
        }

        [Test]
        public void MarkSlot_StepsAlongTheRow()
        {
            Vector3 centre = new Vector3(1f, 2f, 3f);
            AssertVector(1f, 2.25f, 3f, LearnHudLayout.MarkSlot(centre, Vector3.up, 1, 0.1f), 1e-6f);
            AssertVector(1f, 1.75f, 3f, LearnHudLayout.MarkSlot(centre, Vector3.up, 6, 0.1f), 1e-6f);
        }

        [Test]
        public void Tuner_SitsStraightAhead_AndFitsTheGlassesView()
        {
            AssertVector(0f, -0.017452f, 0.999848f, LearnHudLayout.TunerHeadLocal(), 1e-5f);

            float halfHeight = 0.5f * SpectatorLayout.VerticalFovFromDiagonal(52f, 16f / 9f);       // 13.45 degrees
            float halfWidth = 0.5f * SpectatorLayout.HorizontalFovFromDiagonal(52f, 16f / 9f);     // 23.03 degrees
            Assert.LessOrEqual(Mathf.Abs(LearnHudLayout.TunerElevationDegrees) + TunerNeedle.BlockHalfHeightDegrees, halfHeight);
            Assert.LessOrEqual(Mathf.Abs(LearnHudLayout.TunerAzimuthDegrees) + TunerNeedle.BlockHalfWidthDegrees, halfWidth);

            // The widest status line with its icon fits the declared half width.
            float status = TunerNeedle.StatusIconDegrees + 0.5f + LearnText.WidthInCaps(TunerNeedle.TooHighText) * TunerNeedle.StatusDegrees;
            Assert.LessOrEqual(0.5f * status, TunerNeedle.BlockHalfWidthDegrees);
            // The dial itself: radius 8, ticks to 1.13 radii, sits above the pivot.
            Assert.AreEqual(8f, TunerNeedle.DialRadiusDegrees);
        }

        [Test]
        public void FloatingMessage_SitsBelowTheCentreOfView()
        {
            Vector3 p = LearnHudLayout.FloatingMessageHeadLocal();
            Assert.AreEqual(0.0, p.x, 1e-6);
            Assert.Less(p.y, 0f);
            Assert.AreEqual(LearnHudLayout.FloatingMessageDistanceM, p.magnitude, 1e-5);
        }

        [TestCase(0.5f, 0.5f, false)]
        [TestCase(0.5f, 0.53f, false)]      // 6%: not worth re-measuring text
        [TestCase(0.5f, 0.56f, true)]       // 12%
        [TestCase(0.5f, 0.44f, true)]
        [TestCase(0f, 0.5f, true)]          // nothing applied yet
        [TestCase(0.5f, 0f, false)]         // no usable new distance
        [TestCase(0.5f, float.NaN, false)]
        public void DistanceChanged_OnlyForRealChanges(float applied, float now, bool expected)
        {
            Assert.AreEqual(expected, LearnHudLayout.DistanceChanged(applied, now, 0.08f));
        }

        // ---------------------------------------------------------------- helpers

        static void AssertPoint(float x, float y, Vector2 actual)
        {
            Assert.AreEqual(x, actual.x, 1e-5);
            Assert.AreEqual(y, actual.y, 1e-5);
        }

        static void AssertVector(float x, float y, float z, Vector3 actual, float tolerance)
        {
            Assert.AreEqual(x, actual.x, tolerance);
            Assert.AreEqual(y, actual.y, tolerance);
            Assert.AreEqual(z, actual.z, tolerance);
        }
    }
}
