// EditMode tests for GhostHandsDirector and StrumMath (Runtime/GhostHand/GhostHandsDirector.cs, StrumMath.cs).
// Expected values are written out by hand from the rules in StrumMath's header (slot = floor(beat * slotsPerBeat) mod n,
// stroke in the first half of a D/U slot, eased out), never computed with StrumMath's own helpers. Tests marked "pure" call
// no engine function; the rest build GameObjects and need Unity (mono-tests reports them as ENGINE).
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UntitledGuitarApp.Runtime.GhostHand;
using UntitledGuitarApp.Runtime.Logging;
using UntitledGuitarApp.Runtime.Neck;
using UntitledGuitarApp.Runtime.Neck.Dev;
using UntitledGuitarApp.Runtime.Practice;

namespace UntitledGuitarApp.Tests.EditMode
{
    public sealed class GhostHandsDirectorTests
    {
        // riptide-loop.song.json: 100 bpm, 4/4, strum.subdivision 8, slots D - D U - U D U; chords Am 2, G 2, C 4 beats.
        static readonly string[] Riptide = { "D", "-", "D", "U", "-", "U", "D", "U" };
        const int RiptideSlotsPerBeat = 2;
        const float RiptideBps = 100f / 60f;

        readonly List<GameObject> _made = new List<GameObject>();
        Action<LogType, string> _savedOutput;
        string _folder;

        [SetUp]
        public void SetUp()
        {
            _savedOutput = UGALog.Output;
            UGALog.Output = (type, line) => { };
            _folder = null;
        }

        [TearDown]
        public void TearDown()
        {
            UGALog.Output = _savedOutput;
            for (int i = 0; i < _made.Count; i++)
                if (_made[i] != null) UnityEngine.Object.DestroyImmediate(_made[i]);
            _made.Clear();
            try
            {
                if (_folder != null && Directory.Exists(_folder)) Directory.Delete(_folder, true);
            }
            catch (Exception)
            {
                // a leftover temp folder must not fail a test
            }
        }

        // ================================================================================================ slots (pure)

        [Test]
        public void SlotKind_ReadsDownUpAndEverythingElseAsNoStroke()   // pure
        {
            Assert.AreEqual('D', StrumMath.SlotKind("D"));
            Assert.AreEqual('D', StrumMath.SlotKind("d"));
            Assert.AreEqual('U', StrumMath.SlotKind("U"));
            Assert.AreEqual('U', StrumMath.SlotKind("u"));
            Assert.AreEqual('-', StrumMath.SlotKind("-"));
            Assert.AreEqual('-', StrumMath.SlotKind(null));
            Assert.AreEqual('-', StrumMath.SlotKind(""));
            Assert.AreEqual('-', StrumMath.SlotKind("DU"));
            Assert.AreEqual('-', StrumMath.SlotKind("x"));
        }

        [TestCase(0.0, 0, 0f)]
        [TestCase(0.25, 0, 0.5f)]       // half of the first eighth
        [TestCase(0.5, 1, 0f)]
        [TestCase(1.1, 2, 0.2f)]        // fractional beat: 2.2 slots
        [TestCase(3.75, 7, 0.5f)]       // the last slot of the bar
        [TestCase(4.0, 0, 0f)]          // wraps to the next bar
        [TestCase(4.6, 1, 0.2f)]
        [TestCase(1000.25, 0, 0.5f)]    // 2000.5 slots: 2000 mod 8 = 0
        [TestCase(-0.25, 7, 0.5f)]      // negative positions wrap too (the count-in is handled before this)
        public void Locate_PicksTheSlotAndPhase_WithWrapAndFractionalBeats(double beat, int slot, float phase)   // pure
        {
            int s;
            float p;
            Assert.IsTrue(StrumMath.Locate(beat, RiptideSlotsPerBeat, Riptide.Length, out s, out p));
            Assert.AreEqual(slot, s, "slot at beat " + beat);
            Assert.AreEqual(phase, p, 1e-4f, "phase at beat " + beat);
        }

        [Test]
        public void Locate_RefusesAnEmptyPatternAndNonFiniteBeats_AndCountsSlotsPerBeatBelowOneAsOne()   // pure
        {
            int s;
            float p;
            Assert.IsFalse(StrumMath.Locate(1.0, 2, 0, out s, out p));
            Assert.AreEqual(-1, s);
            Assert.IsFalse(StrumMath.Locate(double.NaN, 2, 8, out s, out p));
            Assert.IsFalse(StrumMath.Locate(double.PositiveInfinity, 2, 8, out s, out p));
            Assert.IsTrue(StrumMath.Locate(2.5, 0, 4, out s, out p));
            Assert.AreEqual(2, s);
            Assert.AreEqual(0.5f, p, 1e-5f);
        }

        // ================================================================================================ strokes (pure)

        [Test]
        public void Sample_DownSlot_StrokesFromDownStartToDownEndInTheFirstHalf_EasedOut()   // pure
        {
            StrumSample s = Sample(0.0);
            Assert.AreEqual(StrumPhase.Stroke, s.Phase);
            Assert.AreEqual("down-start", s.From);
            Assert.AreEqual("down-end", s.To);
            Assert.AreEqual(0f, s.Blend, 1e-6f);
            Assert.AreEqual(0, s.Slot);
            Assert.AreEqual('D', s.Kind);
            Assert.IsTrue(s.Striking);

            s = Sample(0.125);                         // slot phase 0.25 = half the stroke: ease-out 1 - (1 - 0.5)^2 = 0.75
            Assert.AreEqual(StrumPhase.Stroke, s.Phase);
            Assert.AreEqual(0.75f, s.Blend, 1e-4f);
            Assert.AreEqual(0.25f, s.SlotPhase, 1e-4f);
        }

        [Test]
        public void Sample_SecondHalfOfAStroke_RecoversToTheNextSlotsEntryPose()   // pure
        {
            StrumSample s = Sample(0.25);              // slot 0 (D) phase 0.5; slot 1 is "-": back to pick-ready
            Assert.AreEqual(StrumPhase.Recover, s.Phase);
            Assert.AreEqual("down-end", s.From);
            Assert.AreEqual("pick-ready", s.To);
            Assert.AreEqual(0f, s.Blend, 1e-5f);
            Assert.IsFalse(s.Striking);

            s = Sample(0.375);                         // phase 0.75 = half the recovery: smoothstep(0.5) = 0.5
            Assert.AreEqual(0.5f, s.Blend, 1e-4f);

            s = Sample(1.25);                          // slot 2 (D) phase 0.5; slot 3 is U: toward up-start
            Assert.AreEqual(StrumPhase.Recover, s.Phase);
            Assert.AreEqual("down-end", s.From);
            Assert.AreEqual("up-start", s.To);
        }

        [Test]
        public void Sample_UpSlot_StrokesFromUpStartToUpEnd()   // pure
        {
            StrumSample s = Sample(1.5 + 0.0625);      // slot 3 (U) phase 0.125
            Assert.AreEqual(StrumPhase.Stroke, s.Phase);
            Assert.AreEqual(3, s.Slot);
            Assert.AreEqual('U', s.Kind);
            Assert.AreEqual("up-start", s.From);
            Assert.AreEqual("up-end", s.To);
            Assert.AreEqual(1f - 0.75f * 0.75f, s.Blend, 1e-4f);   // stroke u = 0.25: 1 - 0.75^2
        }

        [Test]
        public void Sample_NoStrokeSlot_HoversAtPickReady_ThenPreparesTheNextStroke()   // pure
        {
            StrumSample s = Sample(0.5);               // slot 1 "-" phase 0
            Assert.AreEqual(StrumPhase.Hover, s.Phase);
            Assert.AreEqual("pick-ready", s.From);
            Assert.AreEqual("pick-ready", s.To);
            Assert.IsFalse(s.Striking);

            s = Sample(0.6);                           // phase 0.2: still holding
            Assert.AreEqual("pick-ready", s.To);
            Assert.AreEqual(0f, s.Blend, 1e-6f);

            s = Sample(0.875);                         // phase 0.75: halfway to slot 2's down-start
            Assert.AreEqual(StrumPhase.Hover, s.Phase);
            Assert.AreEqual("pick-ready", s.From);
            Assert.AreEqual("down-start", s.To);
            Assert.AreEqual(0.5f, s.Blend, 1e-4f);
            Assert.IsFalse(s.Striking);
        }

        [Test]
        public void Sample_LastSlotOfTheBar_RecoversToTheFirstSlotOfTheNextBar()   // pure
        {
            StrumSample s = Sample(3.75 + 0.125);      // slot 7 (U) phase 0.75, wraps to slot 0 (D)
            Assert.AreEqual(7, s.Slot);
            Assert.AreEqual(StrumPhase.Recover, s.Phase);
            Assert.AreEqual("up-end", s.From);
            Assert.AreEqual("down-start", s.To);
        }

        [Test]
        public void Sample_EverySlotEndsOnThePoseTheNextOneStartsWith_SoTheHandNeverJumps()   // pure
        {
            const double eps = 1e-7;
            for (int boundary = 1; boundary <= 3 * Riptide.Length; boundary++)
            {
                double beat = boundary / (double)RiptideSlotsPerBeat;
                StrumSample before = Sample(beat - eps), after = Sample(beat);
                string end = before.Blend > 0.5f ? before.To : before.From;
                string start = after.Blend > 0.5f ? after.To : after.From;
                Assert.AreEqual(end, start, "pose at the boundary before slot " + boundary);
                if (before.From != before.To) Assert.Greater(before.Blend, 0.999f, "end of slot " + (boundary - 1));
                if (after.From != after.To) Assert.Less(after.Blend, 0.001f, "start of slot " + boundary);
            }
            // and inside each stroke the stroke's own end pose meets the recovery's start
            for (int slot = 0; slot < Riptide.Length; slot++)
            {
                if (Riptide[slot] == "-") continue;
                double mid = (slot + StrumMath.StrokeFraction) / RiptideSlotsPerBeat;
                StrumSample before = Sample(mid - eps), after = Sample(mid);
                Assert.AreEqual(before.To, after.From, "slot " + slot);
                Assert.Greater(before.Blend, 0.999f);
                Assert.Less(after.Blend, 0.001f);
            }
        }

        [Test]
        public void Sample_StrikesOnlyOnDAndUSlots_ExactlyFromTheSlotOnset()   // pure
        {
            for (int i = 0; i < 4000; i++)
            {
                double beat = i * 0.001;               // one bar in 1 ms of beat steps
                StrumSample s = Sample(beat);
                int slot = (int)Math.Floor(beat * RiptideSlotsPerBeat) % Riptide.Length;
                double phase = beat * RiptideSlotsPerBeat - Math.Floor(beat * RiptideSlotsPerBeat);
                bool expected = Riptide[slot] != "-" && phase < 0.5 - 1e-9;
                Assert.AreEqual(expected, s.Striking, "beat " + beat.ToString("0.000"));
            }
        }

        // ================================================================================================ tempo (pure)

        [TestCase(0.5f)]
        [TestCase(0.75f)]
        [TestCase(1.0f)]
        public void Strokes_StartOnTheBeatCursorsSlotOnsets_AtEveryTempoPreset(float preset)   // pure
        {
            // a playhead advancing at 100 bpm * preset, sampled at 240 frames per second: every D/U stroke must begin in the
            // frame that crosses its slot onset (beat = slot / 2), whatever the tempo.
            float bps = RiptideBps * preset;
            const double frame = 1.0 / 240.0;
            double slotSeconds = 1.0 / (bps * RiptideSlotsPerBeat);
            var onsets = new List<double>();
            bool striking = false;
            for (double t = 0.0; t < 2.0 * Riptide.Length * slotSeconds; t += frame)
            {
                StrumSample s = StrumMath.Sample(true, false, t * bps, Riptide, RiptideSlotsPerBeat);
                if (s.Striking && !striking) onsets.Add(t);
                striking = s.Striking;
            }
            var expected = new List<double>();
            for (int bar = 0; bar < 2; bar++)
                for (int slot = 0; slot < Riptide.Length; slot++)
                    if (Riptide[slot] != "-") expected.Add((bar * Riptide.Length + slot) * slotSeconds);
            // consecutive strokes ("D U" at slots 2-3, "D U" at 6-7, the bar line 7-0) are separate onsets: the stroke ends
            // half-way through its slot, so the striking flag drops between them
            Assert.AreEqual(expected.Count, onsets.Count, "number of strokes in two bars at " + preset);
            for (int i = 0; i < expected.Count; i++)
                Assert.AreEqual(expected[i], onsets[i], frame + 1e-9, "stroke " + i + " at preset " + preset);
        }

        // ================================================================================================ no strokes (pure)

        [Test]
        public void Sample_CountIn_HoldsPickReady_WithNoStrokes_ThenLeadsIntoTheFirstStroke()   // pure
        {
            for (double beat = -4.0; beat < 0.0; beat += 0.01)
            {
                StrumSample s = Sample(beat);
                Assert.IsFalse(s.Striking, "count-in beat " + beat);
                Assert.AreEqual(-1, s.Slot);
                Assert.AreEqual("pick-ready", s.From);
            }
            StrumSample early = Sample(-2.0);
            Assert.AreEqual(StrumPhase.Ready, early.Phase);
            Assert.AreEqual("pick-ready", early.To);

            StrumSample lead = Sample(-0.2);           // -0.4 slots: 0.1 into the 0.5-slot lead-in = 0.2 -> smoothstep 0.104
            Assert.AreEqual(StrumPhase.LeadIn, lead.Phase);
            Assert.AreEqual("down-start", lead.To);
            Assert.AreEqual(0.104f, lead.Blend, 1e-3f);
            Assert.Greater(Sample(-1e-7).Blend, 0.999f, "the lead-in ends on the first stroke's start pose");
        }

        [Test]
        public void Sample_CountIn_BeforeAPatternThatStartsWithNoStroke_StaysAtPickReady()   // pure
        {
            string[] slots = { "-", "D" };
            StrumSample s = StrumMath.Sample(true, false, -0.1, slots, 2);
            Assert.AreEqual(StrumPhase.Ready, s.Phase);
            Assert.AreEqual("pick-ready", s.To);
        }

        [Test]
        public void Sample_Waiting_HoldsPickReady_WithNoStrokes()   // pure
        {
            for (double beat = -1.0; beat < 8.0; beat += 0.0625)
            {
                StrumSample s = StrumMath.Sample(true, true, beat, Riptide, RiptideSlotsPerBeat);
                Assert.AreEqual(StrumPhase.Ready, s.Phase, "beat " + beat);
                Assert.AreEqual("pick-ready", s.From);
                Assert.AreEqual("pick-ready", s.To);
                Assert.IsFalse(s.Striking);
            }
        }

        [Test]
        public void Sample_InactiveOrWithoutAPattern_Rests()   // pure
        {
            Assert.AreEqual(StrumPhase.Rest, StrumMath.Sample(false, false, 1.0, Riptide, 2).Phase);
            Assert.AreEqual(StrumPhase.Rest, StrumMath.Sample(true, false, 1.0, null, 2).Phase);
            Assert.AreEqual(StrumPhase.Rest, StrumMath.Sample(true, false, 1.0, new string[0], 2).Phase);
            StrumSample s = StrumMath.Sample(false, true, 1.0, Riptide, 2);
            Assert.AreEqual("rest", s.From);
            Assert.AreEqual("rest", s.To);
            Assert.IsFalse(s.Striking);
            Assert.AreEqual(StrumPhase.Ready, StrumMath.Sample(true, false, double.NaN, Riptide, 2).Phase, "a NaN beat never strokes");
        }

        [Test]
        public void Sample_ThreeFourPattern_WithOneSlotPerBeat_WrapsOnItsOwnLength()   // pure
        {
            string[] waltz = { "D", "-", "U" };
            Assert.AreEqual(0, StrumMath.Sample(true, false, 3.1, waltz, 1).Slot);
            Assert.AreEqual(2, StrumMath.Sample(true, false, 2.2, waltz, 1).Slot);
            Assert.AreEqual("up-start", StrumMath.Sample(true, false, 2.2, waltz, 1).From);
        }

        // ================================================================================================ fretting morph (pure)

        [Test]
        public void Morph_StartsExactlyOneBeatEarly_AndArrivesAtTheChange()   // pure
        {
            Assert.AreEqual(0f, StrumMath.MorphProgress(1.5, RiptideBps));
            Assert.AreEqual(0f, StrumMath.MorphProgress(1.0, RiptideBps), "not before one beat early");
            Assert.Greater(StrumMath.MorphProgress(1.0 - 1e-6, RiptideBps), 0f, "it has begun just after");
            Assert.AreEqual(0.5f, StrumMath.MorphProgress(0.5, RiptideBps), 1e-5f, "100 bpm: a 0.6 s beat, the morph is the whole beat");
            Assert.AreEqual(1f, StrumMath.MorphProgress(0.0, RiptideBps), 1e-6f);
            Assert.AreEqual(0f, StrumMath.MorphProgress(double.PositiveInfinity, RiptideBps), "no next chord");
            Assert.AreEqual(0f, StrumMath.MorphProgress(double.NaN, RiptideBps));

            string from, to;
            float u;
            StrumMath.FrettingTarget("Am", "G", 1.0, RiptideBps, out from, out to, out u);
            Assert.AreEqual("Am", from);
            Assert.AreEqual("G", to);
            Assert.AreEqual(0f, u);
            StrumMath.FrettingTarget("Am", "G", 0.25, RiptideBps, out from, out to, out u);
            Assert.AreEqual(0.75f, u, 1e-5f);
        }

        [Test]
        public void Morph_LengthIsOneBeat_ClampedTo025And08Seconds_ButNeverLongerThanTheBeat()   // pure
        {
            Assert.AreEqual(0.6f, StrumMath.MorphSeconds(100f / 60f), 1e-5f);
            Assert.AreEqual(0.8f, StrumMath.MorphSeconds(0.5f * 80f / 60f), 1e-5f, "50% of 80 bpm: a 1.5 s beat");
            Assert.AreEqual(0.25f, StrumMath.MorphSeconds(5f), 1e-5f, "300 bpm");
            Assert.AreEqual(PosedGhostHand.DefaultMorphSeconds, StrumMath.MorphSeconds(0f), 1e-6f);

            float slow = 0.5f * 80f / 60f;                                  // 0.8 s of a 1.5 s beat = 0.5333 beats
            Assert.AreEqual(0.8 * slow, StrumMath.MorphBeats(slow), 1e-5);
            Assert.AreEqual(1f, StrumMath.MorphProgress(1.0 - 0.8 * slow, slow), 1e-4f, "complete 0.7 s before the change");
            Assert.AreEqual(1.0, StrumMath.MorphBeats(5f), 1e-9, "a beat shorter than 0.25 s still morphs in one beat");
            Assert.AreEqual(1f, StrumMath.MorphProgress(0.0, 5f), 1e-6f, "and arrives at the change");
        }

        [Test]
        public void Morph_OnASixtyFpsTimeline_BeginsWithinOneFrameOfOneBeatEarly()   // pure
        {
            // riptide's first change: Am for beats 0-2, G from beat 2. A 60 fps playhead at 100 bpm.
            double firstMorphBeat = double.NaN;
            for (int f = 0; f < 200; f++)
            {
                double beat = f / 60.0 * RiptideBps;
                if (beat >= 2.0) break;
                string from, to;
                float u;
                StrumMath.FrettingTarget("Am", "G", 2.0 - beat, RiptideBps, out from, out to, out u);
                if (u > 0f) { firstMorphBeat = beat; break; }
            }
            Assert.IsFalse(double.IsNaN(firstMorphBeat));
            Assert.Greater(firstMorphBeat, 1.0);
            Assert.LessOrEqual(firstMorphBeat, 1.0 + RiptideBps / 60.0 + 1e-9, "the first frame after beat 1");
        }

        [Test]
        public void Fretting_CountInHoldsTheFirstChord_OrRestWithoutOne()   // pure
        {
            string from, to;
            float u;
            StrumMath.FrettingTarget(null, "Am", 0.5, RiptideBps, out from, out to, out u);
            Assert.AreEqual("Am", from);
            Assert.AreEqual("Am", to);
            Assert.AreEqual(0f, u, "no morph from nothing: the first chord is simply held");
            StrumMath.FrettingTarget(null, null, double.PositiveInfinity, RiptideBps, out from, out to, out u);
            Assert.AreEqual("rest", from);
            Assert.AreEqual("rest", to);
            StrumMath.FrettingTarget("C", null, double.PositiveInfinity, RiptideBps, out from, out to, out u);
            Assert.AreEqual("C", to, "end of the song: hold the last chord");
            Assert.AreEqual(0f, u);
        }

        [Test]
        public void FrettingFade_FadesOnlyWhileTheConfirmedChordIsHeld()   // pure
        {
            Assert.AreEqual(0.4f, StrumMath.FrettingFade(true, 0f, 0.4f), 1e-6f);
            Assert.AreEqual(1f, StrumMath.FrettingFade(true, 0.01f, 0.4f), "bright again while forming the next chord");
            Assert.AreEqual(1f, StrumMath.FrettingFade(false, 0f, 0.4f));
            Assert.AreEqual(0.4f, StrumMath.FrettingFade(true, 0f, float.NaN), 1e-6f);
            Assert.AreEqual(1f, StrumMath.FrettingFade(true, 0f, 3f));
            Assert.AreEqual(0f, StrumMath.FrettingFade(true, 0f, -1f));
        }

        // ================================================================================================ the director without hands (pure)

        [Test]
        public void Director_WithNullHands_ApplyAndIdleDoNotThrow_AndStillReportTheTimeline()   // pure
        {
            GhostHandsDirector d = GhostHandsDirector.Create(null, null);
            Assert.IsNotNull(d);
            Assert.IsFalse(d.DrivesFretting);
            Assert.IsFalse(d.DrivesStrumming);
            PracticeFrame f = RiptideFrame(2.0 - 0.3);   // inside the Am -> G morph
            Assert.DoesNotThrow(() => d.Apply(f, 1f / 60f));
            Assert.AreEqual("Am", d.FrettingFrom);
            Assert.AreEqual("G", d.FrettingTo);
            Assert.AreEqual(0.7f, d.FrettingMorph, 1e-4f);
            Assert.AreEqual(StrumPhase.Stroke, d.LastStrum.Phase, "beat 1.7 = 3.4 slots: slot 3 (U) at phase 0.4");
            Assert.AreEqual("up-start", d.LastStrum.From);
            Assert.DoesNotThrow(() => d.Apply(PracticeFrame.Idle, 1f / 60f));
            Assert.AreEqual(StrumPhase.Rest, d.LastStrum.Phase, "an inactive frame rests");
            Assert.DoesNotThrow(() => d.Idle("G", 1f / 60f));
            Assert.AreEqual("G", d.FrettingFrom);
            Assert.DoesNotThrow(() => d.Idle(null, 0f));
            Assert.AreEqual("rest", d.FrettingFrom);
            Assert.AreEqual(1f, d.FrettingFade);
        }

        [Test]
        public void Director_ConfirmedChord_AsksForTheFlagFade_UntilTheMorphBegins()   // pure
        {
            GhostHandsDirector d = GhostHandsDirector.Create(null, null);
            PracticeFrame f = RiptideFrame(0.5);
            f.ChordConfirmed = true;
            d.Apply(f, 0.016f);
            Assert.AreEqual(0.4f, d.FrettingFade, 1e-6f, "flags.default: ghost.fadeAfterSuccess 0.4");
            f = RiptideFrame(1.5);
            f.ChordConfirmed = true;
            d.Apply(f, 0.016f);
            Assert.AreEqual(1f, d.FrettingFade, "forming G: full brightness");
        }

        [Test]
        public void StrummingHandLocalPosition_PutsTheAuthoredSoundholeOnTheStrumZone()   // pure
        {
            Vector3 zone = new Vector3(0.2726f, 0.002f, 0f);   // the dev layout's floating strum zone (162.6 + 110 mm), 2 mm strings
            Vector3 local = GhostHandsDirector.StrummingHandLocalPosition(zone);
            Assert.AreEqual(-0.44f, local.x, 1e-6f);
            Assert.AreEqual(-0.002f, local.y, 1e-6f);
            Assert.AreEqual(0f, local.z, 1e-6f);
            // a pose point at the authored soundhole centre (440, 0, 0 mm in the pose frame) lands on the strum zone's x
            Vector3 soundholeInNeck = zone + local + new Vector3(0.44f, 0f, 0f);
            Assert.AreEqual(zone.x, soundholeInNeck.x, 1e-6f);
            Assert.AreEqual(0f, soundholeInNeck.y, 1e-6f, "on the fretboard surface, as the poses were authored");
        }

        // ================================================================================================ with GameObjects (engine)

        [Test]
        public void Director_WithUnavailableHands_DoesNotThrow_AndDrivesNothing()
        {
            _folder = Path.Combine(Path.GetTempPath(), "uga-director-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            var empty = new GhostPoseLibrary(_folder);
            empty.Reload();
            PosedGhostHand fretting = Track(PosedGhostHand.Create(null, GhostPose.HandFretting, empty, null));
            PosedGhostHand strumming = Track(PosedGhostHand.Create(null, GhostPose.HandStrumming, empty, null));
            Assert.IsFalse(fretting.IsAvailable);
            Assert.IsFalse(strumming.IsAvailable);
            GhostHandsDirector d = GhostHandsDirector.Create(fretting, strumming);
            Assert.IsFalse(d.DrivesFretting);
            Assert.IsFalse(d.DrivesStrumming);
            Assert.DoesNotThrow(() => d.Apply(RiptideFrame(0.1), 0.016f));
            Assert.DoesNotThrow(() => d.Idle("G", 0.016f));
            UnityEngine.Object.DestroyImmediate(strumming.gameObject);   // destroyed under the director's feet
            Assert.DoesNotThrow(() => d.Apply(RiptideFrame(0.2), 0.016f));
        }

        [Test]
        public void CreateStrummingHand_HangsUnderTheStrumZone_AndFollowsItWhenTheNeckMoves()
        {
            FloatingNeck neck = FloatingNeck.Create(null);
            _made.Add(neck.gameObject);
            Assert.IsTrue(neck.Build(DevNeckLayout.Create()));
            neck.PlaceFloating(null);
            PosedGhostHand strumming = GhostHandsDirector.CreateStrummingHand(neck, new GhostPoseLibrary());
            Assert.AreSame(neck.StrumZone, strumming.transform.parent);
            Vector3 zone = neck.StrumZone.localPosition;
            Assert.AreEqual(NeckMath.FloatingStrumZoneXMm(neck.Layout, 110f) * 0.001f, zone.x, 1e-5f, "floating strum zone (FloatingNeck._floatingStrumGapMm 110)");
            AssertNear(GhostHandsDirector.StrummingHandLocalPosition(zone), strumming.transform.localPosition, 1e-6f, "placed");
            if (!strumming.IsAvailable)
                Assert.Inconclusive("the strumming FBX or poses are not imported (" + strumming.UnavailableReason + "); the placement was checked, the following was not");

            GhostHandsDirector d = GhostHandsDirector.Create(null, strumming);
            neck.SetWorldPose(Vector3.zero, Quaternion.identity);       // aligned mode: the strum zone moves to the real soundhole
            Vector3 aligned = neck.StrumZone.localPosition;
            Assert.AreNotEqual(zone.x, aligned.x, "the strum zone moved");
            d.Idle(null, 0.016f);
            AssertNear(GhostHandsDirector.StrummingHandLocalPosition(aligned), strumming.transform.localPosition, 1e-6f, "followed");
        }

        [Test]
        public void Director_RealHands_StrokeAndMorphFollowTheBeat()
        {
            var library = new GhostPoseLibrary();
            library.Reload();
            Transform socket = Track(new GameObject("Socket")).transform;
            PosedGhostHand fretting = PosedGhostHand.Create(socket, GhostPose.HandFretting, library);
            PosedGhostHand strumming = PosedGhostHand.Create(socket, GhostPose.HandStrumming, library);
            if (!fretting.IsAvailable || !strumming.IsAvailable)
                Assert.Inconclusive("the ghost-hand FBX files or poses are not imported: " + fretting.UnavailableReason + " / " + strumming.UnavailableReason + ". This is not a pass.");
            GhostHandsDirector d = GhostHandsDirector.Create(fretting, strumming);
            Assert.IsTrue(d.DrivesFretting && d.DrivesStrumming);

            // play from the count-in to mid-stroke of slot 0 at 60 fps, ticking the eases as Update would
            const float dt = 1f / 60f;
            for (double beat = -2.0; beat < 0.125 - 1e-9; beat += dt * RiptideBps)
            {
                d.Apply(RiptideFrame(beat), dt);
                fretting.Tick(dt);
                strumming.Tick(dt);
            }
            d.Apply(RiptideFrame(0.125), dt);
            GhostPose downStart, downEnd;
            Assert.IsTrue(strumming.TryGetPose("down-start", out downStart));
            Assert.IsTrue(strumming.TryGetPose("down-end", out downEnd));
            Vector3 expected = Vector3.Lerp(downStart.RootPositionMm, downEnd.RootPositionMm, 0.75f);   // stroke blend 0.75
            AssertNear(expected, strumming.Driver.CurrentRootPositionMm, 0.5f, "strumming wrist at mid-stroke");

            // the fretting hand: Am held, then half-way to G half a beat before the change
            d.Apply(RiptideFrame(1.5), dt);
            for (int i = 0; i < 30; i++) { d.Apply(RiptideFrame(1.5), dt); fretting.Tick(dt); }   // past the catch-up
            GhostPose am, g;
            Assert.IsTrue(fretting.TryGetPose("Am", out am));
            Assert.IsTrue(fretting.TryGetPose("G", out g));
            Vector3 half = Vector3.Lerp(am.RootPositionMm, g.RootPositionMm, 0.5f);           // smoothstep(0.5) = 0.5
            AssertNear(half, fretting.Driver.CurrentRootPositionMm, 0.5f, "fretting wrist half-way from Am to G");
        }

        // ================================================================================================ helpers

        static StrumSample Sample(double beat)
        {
            return StrumMath.Sample(true, false, beat, Riptide, RiptideSlotsPerBeat);
        }

        // What the play-along producer would give for riptide-loop at 100% (Am 2 beats, G 2, C 4, looping every 8 beats).
        static PracticeFrame RiptideFrame(double beat)
        {
            PracticeFrame f = PracticeFrame.Idle;
            f.Active = true;
            f.BeatsPerSecond = RiptideBps;
            f.BeatsPerBar = 4;
            f.Beat = beat;
            f.StrumSlots = Riptide;
            f.SlotsPerBeat = RiptideSlotsPerBeat;
            if (beat < 0.0)
            {
                f.Chord = null;
                f.NextChord = "Am";
                f.BeatsToChange = -beat;
                return f;
            }
            double inLoop = beat % 8.0;
            if (inLoop < 2.0) { f.Chord = "Am"; f.NextChord = "G"; f.BeatsToChange = 2.0 - inLoop; }
            else if (inLoop < 4.0) { f.Chord = "G"; f.NextChord = "C"; f.BeatsToChange = 4.0 - inLoop; }
            else { f.Chord = "C"; f.NextChord = "Am"; f.BeatsToChange = 8.0 - inLoop; }
            return f;
        }

        T Track<T>(T component) where T : Component
        {
            if (component != null) _made.Add(component.gameObject);
            return component;
        }

        GameObject Track(GameObject go)
        {
            _made.Add(go);
            return go;
        }

        static void AssertNear(Vector3 expected, Vector3 actual, float tolerance, string what)
        {
            Assert.LessOrEqual((expected - actual).magnitude, tolerance,
                what + ": expected " + expected.ToString("F4") + ", got " + actual.ToString("F4"));
        }
    }
}
