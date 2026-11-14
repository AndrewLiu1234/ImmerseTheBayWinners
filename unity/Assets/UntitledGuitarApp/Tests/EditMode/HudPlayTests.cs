using NUnit.Framework;
using UnityEngine;
using UntitledGuitarApp.Runtime.Hud;
using UntitledGuitarApp.Runtime.Hud.Play;

namespace UntitledGuitarApp.Tests.EditMode
{
    // Pure-logic tests for the play-along HUD (Runtime/Hud/Play). No Play Mode, no device, no rendering and no font: the
    // decisions (ring size over time, where the cursor is, where a chord name goes above a lyric, the chords-only fallback,
    // where the card sits, how often the beat may pulse) live in static functions on purpose.
    // Expected numbers were derived independently (python3 math), not by running this code.
    // The lyric strings below are made-up letters, never song words.
    public class HudPlayTests
    {
        const double Tight = 1e-4;

        // ---------------------------------------------------------------- approach rings: radius over time

        [Test]
        public void Ring_LandsExactlyOnTheTargetAtTheHit()
        {
            Assert.AreEqual(1f, ApproachRings.RingScale(0f, 1.2f, ApproachRings.StartScale), 1e-6);
            Assert.AreEqual(7f, ApproachRings.RingRadiusMm(0f, 1.2f, 7f, ApproachRings.StartScale), 1e-5);
        }

        [Test]
        public void Ring_StartsAtTheStartScaleAndIsHalfWayAtHalfTime()
        {
            Assert.AreEqual(3.2f, ApproachRings.RingScale(1.2f, 1.2f, 3.2f), 1e-6);
            Assert.AreEqual(3.2f, ApproachRings.RingScale(5f, 1.2f, 3.2f), 1e-6);          // before the approach starts
            Assert.AreEqual(2.1f, ApproachRings.RingScale(0.6f, 1.2f, 3.2f), 1e-5);        // half-way in: (1 + 3.2) / 2
            Assert.AreEqual(14.7f, ApproachRings.RingRadiusMm(0.6f, 1.2f, 7f, 3.2f), 1e-4);
        }

        [Test]
        public void Ring_ShrinksLinearly_SoTheLandingCanBePredicted()
        {
            float a = ApproachRings.RingScale(0.9f, 1.2f, 3.2f);
            float b = ApproachRings.RingScale(0.6f, 1.2f, 3.2f);
            float c = ApproachRings.RingScale(0.3f, 1.2f, 3.2f);
            Assert.AreEqual(a - b, b - c, 1e-5);                                            // equal steps in equal times
            float previous = float.MaxValue;
            for (float t = 1.5f; t >= -0.5f; t -= 0.05f)
            {
                float s = ApproachRings.RingScale(t, 1.2f, 3.2f);
                Assert.LessOrEqual(s, previous + 1e-6f);                                    // never grows on the way in
                Assert.GreaterOrEqual(s, 1f);
                previous = s;
            }
        }

        [Test]
        public void Ring_StaysLandedAfterTheHit_AndHandlesBadInput()
        {
            Assert.AreEqual(1f, ApproachRings.RingScale(-0.3f, 1.2f, 3.2f));
            Assert.AreEqual(1f, ApproachRings.RingScale(float.NaN, 1.2f, 3.2f));
            Assert.AreEqual(3.2f, ApproachRings.RingScale(0.5f, 0f, 3.2f));                // no approach time: not started
        }

        [Test]
        public void Ring_BrightnessFadesInHoldsAndFadesOut()
        {
            const float approach = 1.2f, hold = 0.12f, fade = 0.35f;
            Assert.AreEqual(0f, ApproachRings.RingLevel(1.3f, approach, hold, fade));       // not started
            Assert.AreEqual(0f, ApproachRings.RingLevel(1.2f, approach, hold, fade));
            Assert.AreEqual(0.5f, ApproachRings.RingLevel(1.02f, approach, hold, fade), 1e-4);   // half-way through the fade-in (0.36 s)
            Assert.AreEqual(1f, ApproachRings.RingLevel(0.84f, approach, hold, fade), 1e-5);     // fade-in done
            Assert.AreEqual(1f, ApproachRings.RingLevel(0.6f, approach, hold, fade));
            Assert.AreEqual(1f, ApproachRings.RingLevel(0f, approach, hold, fade));         // the hit: held while wait mode holds
            Assert.AreEqual(1f, ApproachRings.RingLevel(-0.1f, approach, hold, fade));
            Assert.AreEqual(0.5f, ApproachRings.RingLevel(-(hold + fade * 0.5f), approach, hold, fade), 1e-4);
            Assert.AreEqual(0f, ApproachRings.RingLevel(-(hold + fade), approach, hold, fade), 1e-5);
            Assert.AreEqual(0f, ApproachRings.RingLevel(-5f, approach, hold, fade));
            Assert.AreEqual(0f, ApproachRings.RingLevel(float.NaN, approach, hold, fade));
        }

        [Test]
        public void Ring_IsOverOnlyAfterTheFadeOut()
        {
            Assert.IsFalse(ApproachRings.IsOver(0.5f));
            Assert.IsFalse(ApproachRings.IsOver(0f));
            Assert.IsFalse(ApproachRings.IsOver(-0.4f));
            Assert.IsTrue(ApproachRings.IsOver(-(ApproachRings.AfterHoldSeconds + ApproachRings.FadeOutSeconds) - 0.01f));
        }

        [Test]
        public void Ring_LandingSwellPeaksAtTheHit()
        {
            Assert.AreEqual(1f, ApproachRings.LandBloom(0f, 0.09f), 1e-6);
            Assert.AreEqual(0.5f, ApproachRings.LandBloom(0.045f, 0.09f), 1e-4);
            Assert.AreEqual(0.5f, ApproachRings.LandBloom(-0.045f, 0.09f), 1e-4);
            Assert.AreEqual(0f, ApproachRings.LandBloom(0.09f, 0.09f), 1e-6);
            Assert.AreEqual(0f, ApproachRings.LandBloom(1f, 0.09f));
            Assert.AreEqual(0f, ApproachRings.LandBloom(0f, 0f));
        }

        [TestCase(0.6666667f, 1.3333333f)]   // Happy Birthday, 90 bpm at 100%
        [TestCase(1.3333333f, 2.4f)]         // the same at 50%: two beats would be 2.67 s, capped
        [TestCase(0.6f, 1.2f)]               // Riptide, 100 bpm
        [TestCase(1f, 2f)]                   // Stand By Me, 80 bpm at 75%
        [TestCase(0.3f, 0.8f)]               // 200 bpm: never shorter than 0.8 s
        public void Ring_ApproachTimeIsTwoBeatsWithinLimits(float beatSeconds, float expected)
        {
            Assert.AreEqual(expected, ApproachRings.ApproachForBeat(beatSeconds), 1e-4);
        }

        [Test]
        public void Ring_ApproachTimeForAnUnknownTempoIsTheDefault()
        {
            Assert.AreEqual(ApproachRings.DefaultApproachSeconds, ApproachRings.ApproachForBeat(0f));
            Assert.AreEqual(ApproachRings.DefaultApproachSeconds, ApproachRings.ApproachForBeat(float.NaN));
        }

        [Test]
        public void RingNormal_FlatOnTheFretboardWhenSeenSteeply()
        {
            Vector3 n = new Vector3(0f, 1f, 0f);
            Vector3 eye = new Vector3(0f, Mathf.Sin(60f * Mathf.Deg2Rad), Mathf.Cos(60f * Mathf.Deg2Rad));   // 60 degrees above the plane
            AssertVector(0f, 1f, 0f, ApproachRings.RingNormal(n, eye, 35f));
            AssertVector(0f, 1f, 0f, ApproachRings.RingNormal(n * 3f, eye * 0.4f, 35f));                    // lengths do not matter
        }

        [Test]
        public void RingNormal_TipsTowardTheEyesAtAGrazingView()
        {
            // The eyes are 10 degrees above the fretboard plane (a seated player). The ring is tipped 25 degrees toward
            // them, so it is seen at exactly 35 degrees: (0, cos 25, sin 25).
            Vector3 n = new Vector3(0f, 1f, 0f);
            Vector3 eye = new Vector3(0f, Mathf.Sin(10f * Mathf.Deg2Rad), Mathf.Cos(10f * Mathf.Deg2Rad));
            Vector3 r = ApproachRings.RingNormal(n, eye, 35f);
            AssertVector(0f, 0.9063078f, 0.4226183f, r);
            Assert.AreEqual(1f, r.magnitude, 1e-5);
            Assert.AreEqual(55.0, PlayHudLayout.AngleBetween(r, eye), 1e-2);
        }

        [Test]
        public void RingNormal_StillFacesTheViewerFromBehindTheFretboard_AndCanBeSwitchedOff()
        {
            Vector3 n = new Vector3(0f, 1f, 0f);
            Vector3 behind = new Vector3(0f, -0.2f, 1f);
            Vector3 r = ApproachRings.RingNormal(n, behind, 35f);
            Assert.AreEqual(55.0, PlayHudLayout.AngleBetween(r, behind), 1e-2);
            AssertVector(0f, 1f, 0f, ApproachRings.RingNormal(n, behind, 0f));              // 0 = always flat on the fretboard
            AssertVector(0f, 1f, 0f, ApproachRings.RingNormal(n, Vector3.zero, 35f));       // no eye position
        }

        // ---------------------------------------------------------------- pattern strip: cursor position

        [Test]
        public void Cursor_IsCentredOnACellExactlyWhenItsSlotIsDue()
        {
            const float pitch = 0.02f;
            for (int i = 0; i < 8; i++)
                Assert.AreEqual(PatternStrip.CellCenter(i, pitch), PatternStrip.CursorX(PatternStrip.WrapSlot(i, 8), pitch), 1e-7);
            Assert.AreEqual(0.01f, PatternStrip.CellCenter(0, pitch), 1e-7);
            Assert.AreEqual(0.09f, PatternStrip.CellCenter(4, pitch), 1e-7);                // slot 4 of D - D U - U D U
        }

        [Test]
        public void Cursor_MovesEvenlyBetweenSlots()
        {
            const float pitch = 0.02f;
            Assert.AreEqual(0.1f, PatternStrip.CursorX(4.5f, pitch), 1e-7);                 // half-way from cell 4 to cell 5
            Assert.AreEqual(PatternStrip.CursorX(3.25f, pitch) - PatternStrip.CursorX(3f, pitch),
                PatternStrip.CursorX(6.75f, pitch) - PatternStrip.CursorX(6.5f, pitch), 1e-7);
        }

        [TestCase(0f, 8, 0f)]
        [TestCase(7.5f, 8, 7.5f)]
        [TestCase(8f, 8, 0f)]
        [TestCase(9.25f, 8, 1.25f)]
        [TestCase(-0.5f, 8, 7.5f)]
        [TestCase(13f, 6, 1f)]
        [TestCase(3f, 0, 0f)]
        public void Cursor_SlotPositionWrapsIntoThePattern(float position, int slots, float expected)
        {
            Assert.AreEqual(expected, PatternStrip.WrapSlot(position, slots), 1e-5);
        }

        [Test]
        public void Cursor_ReentersFromTheLeftDuringTheLastSlot()
        {
            Assert.AreEqual(0f, PatternStrip.WrapBlend(0f, 8));
            Assert.AreEqual(0f, PatternStrip.WrapBlend(7f, 8));                             // still fully on the last cell
            Assert.AreEqual(0.5f, PatternStrip.WrapBlend(7.5f, 8), 1e-5);
            Assert.AreEqual(1f, PatternStrip.WrapBlend(7.9999f, 8), 1e-3);
            // The incoming cursor is one pattern-length to the left and reaches cell 0 exactly on the bar line.
            Assert.AreEqual(PatternStrip.CellCenter(0, 0.02f), PatternStrip.CursorX(8f - 8, 0.02f), 1e-7);
            Assert.AreEqual(0f, PatternStrip.WrapBlend(3f, 0));
        }

        [Test]
        public void Glow_PeaksExactlyOnTheSlotAndEasesEitherSide()
        {
            Assert.AreEqual(1f, PatternStrip.Glow(4f, 4, 8), 1e-6);                         // the moment the stroke is due
            Assert.AreEqual(0.5f, PatternStrip.Glow(3.75f, 4, 8), 1e-4);                    // rising, a quarter slot before
            Assert.AreEqual(0f, PatternStrip.Glow(3.5f, 4, 8), 1e-6);
            Assert.AreEqual(0.25f, PatternStrip.Glow(4.5f, 4, 8), 1e-4);                    // relaxing, half a slot after
            Assert.AreEqual(0f, PatternStrip.Glow(5f, 4, 8), 1e-6);
            Assert.AreEqual(0f, PatternStrip.Glow(1f, 4, 8));
        }

        [Test]
        public void Glow_WrapsRoundTheBar_AndOnlyOneCellPeaksAtATime()
        {
            Assert.AreEqual(0.5f, PatternStrip.Glow(7.75f, 0, 8), 1e-4);                    // the first cell rises before the bar line
            Assert.AreEqual(1f, PatternStrip.Glow(8f, 0, 8), 1e-6);
            for (float p = 0f; p < 8f; p += 0.125f)
            {
                int peaks = 0;
                for (int i = 0; i < 8; i++)
                {
                    float g = PatternStrip.Glow(p, i, 8);
                    Assert.GreaterOrEqual(g, 0f);
                    Assert.LessOrEqual(g, 1f);
                    if (g > 0.99f) peaks++;
                }
                Assert.LessOrEqual(peaks, 1, "position " + p);
            }
        }

        [Test]
        public void Glow_EachCellLightsOncePerBar_SoNothingFlashesFasterThan3Hz()
        {
            // Riptide at full tempo: 100 bpm, 8 slots over 4 beats = 2.4 s a bar. A cell lights once a bar: 0.42 Hz.
            int rises = 0;
            float previous = PatternStrip.Glow(0f, 3, 8);
            for (float p = 0.01f; p < 16f; p += 0.01f)                                      // two bars
            {
                float g = PatternStrip.Glow(p, 3, 8);
                if (previous < 0.5f && g >= 0.5f) rises++;
                previous = g;
            }
            Assert.AreEqual(2, rises);
        }

        [TestCase("D", false, PatternSlotKind.Down)]
        [TestCase("U", false, PatternSlotKind.Up)]
        [TestCase("-", false, PatternSlotKind.Rest)]
        [TestCase("d", false, PatternSlotKind.Down)]
        [TestCase(" U ", false, PatternSlotKind.Up)]
        [TestCase("", false, PatternSlotKind.Rest)]
        [TestCase(null, false, PatternSlotKind.Rest)]
        [TestCase("B", true, PatternSlotKind.Bass)]
        [TestCase("3", true, PatternSlotKind.String)]
        [TestCase("1", true, PatternSlotKind.String)]
        [TestCase("6", true, PatternSlotKind.String)]
        [TestCase("-", true, PatternSlotKind.Rest)]
        [TestCase("7", true, PatternSlotKind.Other)]
        [TestCase("B", false, PatternSlotKind.Other)]
        [TestCase("PM", false, PatternSlotKind.Other)]
        public void Pattern_SlotsAreClassified(string slot, bool picking, PatternSlotKind expected)
        {
            Assert.AreEqual(expected, PatternStrip.Classify(slot, picking));
        }

        [Test]
        public void Pattern_StringNumbersAndSlotIndex()
        {
            Assert.AreEqual(3, PatternStrip.StringNumber("3"));
            Assert.AreEqual(6, PatternStrip.StringNumber(" 6 "));
            Assert.AreEqual(0, PatternStrip.StringNumber("B"));
            Assert.AreEqual(0, PatternStrip.StringNumber("7"));
            Assert.AreEqual(0, PatternStrip.StringNumber(null));
            Assert.AreEqual(4, PatternStrip.SlotIndex(4f, 8));
            Assert.AreEqual(4, PatternStrip.SlotIndex(4.99f, 8));
            Assert.AreEqual(0, PatternStrip.SlotIndex(8f, 8));
            Assert.AreEqual(7, PatternStrip.SlotIndex(-0.25f, 8));
        }

        [Test]
        public void Pattern_PitchIsSqueezedToFitTheColumnButNeverBelowTheMinimum()
        {
            Assert.AreEqual(1.8f, PlayHudLayout.PatternPitch(8, 1.8f, 18f, 1.1f), 1e-6);    // 14.4 degrees: fits
            Assert.AreEqual(1.8f, PlayHudLayout.PatternPitch(6, 1.8f, 18f, 1.1f), 1e-6);
            Assert.AreEqual(1.5f, PlayHudLayout.PatternPitch(12, 1.8f, 18f, 1.1f), 1e-6);   // 12 slots: 18 / 12
            Assert.AreEqual(1.125f, PlayHudLayout.PatternPitch(16, 1.8f, 18f, 1.1f), 1e-6);
            Assert.AreEqual(1.1f, PlayHudLayout.PatternPitch(32, 1.8f, 18f, 1.1f), 1e-6);
            Assert.AreEqual(2.25f, PlayHudLayout.PatternPitch(8, 1.8f * HudText.LargeTextScale, 18f, 1.1f), 1e-5);   // largeText: capped by the column
        }

        // ---------------------------------------------------------------- lyric strip: chord above the character index

        [Test]
        public void ChordColumn_StaysInsideTheLyric()
        {
            Assert.AreEqual(0, LyricStrip.ChordColumn(0, 21));
            Assert.AreEqual(18, LyricStrip.ChordColumn(18, 21));
            Assert.AreEqual(21, LyricStrip.ChordColumn(40, 21));
            Assert.AreEqual(0, LyricStrip.ChordColumn(-1, 21));
            Assert.AreEqual(0, LyricStrip.ChordColumn(5, 0));
        }

        [Test]
        public void ChordOffsets_PutEachChordAboveItsCharacter()
        {
            // 21 characters, each 1 unit wide: a chord at index 18 starts 18 units in (the shape of the song file's
            // first line: chords at 0 and 18).
            float[] prefix = UnitPrefix(21);
            var x = new float[2];
            LyricStrip.ChordOffsets(new[] { 0, 18 }, new[] { 1f, 1f }, 2, prefix, 21, 0.5f, x);
            Assert.AreEqual(0f, x[0], 1e-6);
            Assert.AreEqual(18f, x[1], 1e-6);
        }

        [Test]
        public void ChordOffsets_UseRealGlyphWidths_NotCharacterCounts()
        {
            // Proportional font: widths 2, 1, 1, 3, 1 -> prefix 0, 2, 3, 4, 7, 8.
            float[] prefix = { 0f, 2f, 3f, 4f, 7f, 8f };
            var x = new float[3];
            LyricStrip.ChordOffsets(new[] { 0, 3, 4 }, new[] { 1f, 1f, 1f }, 3, prefix, 5, 0.25f, x);
            Assert.AreEqual(0f, x[0], 1e-6);
            Assert.AreEqual(4f, x[1], 1e-6);
            Assert.AreEqual(7f, x[2], 1e-6);
        }

        [Test]
        public void ChordOffsets_PushNamesApartInsteadOfOverlapping()
        {
            // The shape of the song file's last line: chords at 0, 15 and 18. With names 4 units wide and a gap of 1, the
            // third name cannot start at 18 (the second ends at 19): it moves to 20.
            float[] prefix = UnitPrefix(21);
            var x = new float[3];
            LyricStrip.ChordOffsets(new[] { 0, 15, 18 }, new[] { 4f, 4f, 4f }, 3, prefix, 21, 1f, x);
            Assert.AreEqual(0f, x[0], 1e-6);
            Assert.AreEqual(15f, x[1], 1e-6);
            Assert.AreEqual(20f, x[2], 1e-6);
        }

        [Test]
        public void ChordOffsets_TolerateIndexesOutsideTheLyric()
        {
            float[] prefix = UnitPrefix(10);
            var x = new float[3];
            LyricStrip.ChordOffsets(new[] { -1, 99, 99 }, new[] { 1f, 1f, 1f }, 3, prefix, 10, 0.5f, x);
            Assert.AreEqual(0f, x[0], 1e-6);
            Assert.AreEqual(10f, x[1], 1e-6);                                               // clamped to the end of the line
            Assert.AreEqual(11.5f, x[2], 1e-6);                                             // and pushed past the previous name
            Assert.DoesNotThrow(() => LyricStrip.ChordOffsets(null, null, 3, prefix, 10, 0.5f, x));
            Assert.DoesNotThrow(() => LyricStrip.ChordOffsets(new[] { 0 }, null, 1, null, 10, 0.5f, x));
        }

        [Test]
        public void ChordIndex_FollowsTheBeatCounts()
        {
            float[] beats = { 4f, 2f };                                                     // first line: 4 beats, then 2
            Assert.AreEqual(0, LyricStrip.ChordIndexAtBeat(beats, 2, -1f));                 // during the count-in
            Assert.AreEqual(0, LyricStrip.ChordIndexAtBeat(beats, 2, 0f));
            Assert.AreEqual(0, LyricStrip.ChordIndexAtBeat(beats, 2, 2.15f));               // beat 2 of the first full bar
            Assert.AreEqual(0, LyricStrip.ChordIndexAtBeat(beats, 2, 3.99f));
            Assert.AreEqual(1, LyricStrip.ChordIndexAtBeat(beats, 2, 4f));                  // the change
            Assert.AreEqual(1, LyricStrip.ChordIndexAtBeat(beats, 2, 5.9f));
            Assert.AreEqual(1, LyricStrip.ChordIndexAtBeat(beats, 2, 99f));                 // the line is over: stay on the last chord
            Assert.AreEqual(-1, LyricStrip.ChordIndexAtBeat(beats, 0, 1f));
            Assert.AreEqual(-1, LyricStrip.ChordIndexAtBeat(null, 2, 1f));
            Assert.AreEqual(4f, LyricStrip.ChordStartBeat(beats, 1), 1e-6);
            Assert.AreEqual(0f, LyricStrip.ChordStartBeat(beats, 0), 1e-6);
        }

        [Test]
        public void Caret_IsUnderAChordsCharacterWhenThatChordBegins_AndGlidesBetween()
        {
            float[] prefix = UnitPrefix(21);
            float[] beats = { 4f, 2f };
            int[] ats = { 0, 18 };
            Assert.AreEqual(0f, LyricStrip.CaretX(0f, beats, ats, 2, prefix, 21), 1e-5);
            Assert.AreEqual(9f, LyricStrip.CaretX(2f, beats, ats, 2, prefix, 21), 1e-5);    // half-way through the first chord
            Assert.AreEqual(18f, LyricStrip.CaretX(4f, beats, ats, 2, prefix, 21), 1e-5);   // the change: exactly under character 18
            Assert.AreEqual(19.5f, LyricStrip.CaretX(5f, beats, ats, 2, prefix, 21), 1e-5); // gliding to the end of the line
            Assert.AreEqual(21f, LyricStrip.CaretX(6f, beats, ats, 2, prefix, 21), 1e-5);
            Assert.AreEqual(21f, LyricStrip.CaretX(60f, beats, ats, 2, prefix, 21), 1e-5);  // never past the end
            Assert.AreEqual(0f, LyricStrip.CaretX(-3f, beats, ats, 2, prefix, 21), 1e-5);   // never before the start
            Assert.AreEqual(0f, LyricStrip.CaretX(1f, beats, ats, 0, prefix, 21));
        }

        // ---------------------------------------------------------------- lyric strip: chords-only fallback

        [Test]
        public void ChordsOnly_ALineWithoutWordsFallsBackToTheChordSequence()
        {
            var loop = new LyricLine("", new LyricChord("Am", 2f), new LyricChord("G", 2f), new LyricChord("C", 4f));
            var sung = new LyricLine("abcde fghij", new LyricChord("G", 4f, 0), new LyricChord("D", 2f, 6));
            Assert.IsTrue(LyricStrip.IsChordsOnly(loop));
            Assert.IsTrue(LyricStrip.IsChordsOnly(new LyricLine(null)));
            Assert.IsTrue(LyricStrip.IsChordsOnly(new LyricLine("   ")));
            Assert.IsTrue(LyricStrip.IsChordsOnly((LyricLine)null));
            Assert.IsFalse(LyricStrip.IsChordsOnly(sung));

            Assert.IsTrue(LyricStrip.IsChordsOnly(new[] { loop, loop }, 2));                // every lyric empty: a chords-only song
            Assert.IsFalse(LyricStrip.IsChordsOnly(new[] { loop, sung }, 2));
            Assert.IsTrue(LyricStrip.IsChordsOnly(new[] { loop, sung }, 1));                // only the first line counted
            Assert.IsTrue(LyricStrip.IsChordsOnly(null, 3));
        }

        [Test]
        public void ChordsOnly_NamesAreSpacedByTheirBeatCounts()
        {
            // The Riptide loop: Am for 2 beats, G for 2, C for 4. One beat = 1 unit.
            var x = new float[3];
            LyricStrip.SequenceOffsets(new[] { 2f, 2f, 4f }, new[] { 1.5f, 0.8f, 0.8f }, 3, 1f, 0.3f, x);
            Assert.AreEqual(0f, x[0], 1e-6);
            Assert.AreEqual(2f, x[1], 1e-6);
            Assert.AreEqual(4f, x[2], 1e-6);

            // Stand By Me's second line: C 4, D 4, G 8 at half a unit per beat.
            LyricStrip.SequenceOffsets(new[] { 4f, 4f, 8f }, new[] { 0.8f, 0.8f, 0.8f }, 3, 0.5f, 0.3f, x);
            Assert.AreEqual(0f, x[0], 1e-6);
            Assert.AreEqual(2f, x[1], 1e-6);
            Assert.AreEqual(4f, x[2], 1e-6);
        }

        [Test]
        public void ChordsOnly_NamesNeverOverlapEvenForOneBeatChords()
        {
            var x = new float[3];
            LyricStrip.SequenceOffsets(new[] { 1f, 1f, 2f }, new[] { 1.6f, 1.6f, 1.6f }, 3, 1f, 0.4f, x);
            Assert.AreEqual(0f, x[0], 1e-6);
            Assert.AreEqual(2f, x[1], 1e-6);                                                // pushed from 1 to 1.6 + 0.4
            Assert.AreEqual(4f, x[2], 1e-6);
        }

        [Test]
        public void ChordsOnly_BeatPitchFitsTheLineIntoTheColumn()
        {
            Assert.AreEqual(2.25f, LyricStrip.SequencePitch(8f, 18f, 2.4f, 0.85f), 1e-6);   // 8 beats: 18 / 8
            Assert.AreEqual(1.125f, LyricStrip.SequencePitch(16f, 18f, 2.4f, 0.85f), 1e-6); // 16 beats
            Assert.AreEqual(2.4f, LyricStrip.SequencePitch(4f, 18f, 2.4f, 0.85f), 1e-6);    // short line: capped
            Assert.AreEqual(0.85f, LyricStrip.SequencePitch(64f, 18f, 2.4f, 0.85f), 1e-6);  // very long line: floor
            Assert.AreEqual(2.4f, LyricStrip.SequencePitch(0f, 18f, 2.4f, 0.85f), 1e-6);
        }

        [Test]
        public void LyricLine_CountsItsBeatsAndChords()
        {
            var line = new LyricLine("", new LyricChord("Am", 2f), new LyricChord("G", 2f), new LyricChord("C", 4f));
            Assert.AreEqual(8f, line.TotalBeats, 1e-6);
            Assert.AreEqual(3, line.ChordCount);
            Assert.IsFalse(line.HasLyric);
            Assert.AreEqual(-1, line.Chords[0].At);                                         // no "at" for a line without words

            line.Count = 2;                                                                 // a reused, partly filled line
            Assert.AreEqual(4f, line.TotalBeats, 1e-6);
            Assert.AreEqual(2, line.ChordCount);
            line.Count = 99;
            Assert.AreEqual(3, line.ChordCount);
            Assert.AreEqual(0, new LyricLine().ChordCount);
            Assert.AreEqual(0f, new LyricLine().TotalBeats);
        }

        // ---------------------------------------------------------------- beat dots: countdown and the pulse-rate cap

        [TestCase(1.5f, 4, 1)]      // 90 bpm
        [TestCase(1.6666667f, 4, 1)] // 100 bpm
        [TestCase(2f, 4, 1)]        // 120 bpm: exactly 2 Hz is allowed
        [TestCase(2.01f, 4, 2)]     // just over: every second beat
        [TestCase(2.5f, 4, 2)]      // 150 bpm in 4/4: beats 1 and 3
        [TestCase(2.5f, 3, 3)]      // 150 bpm in 3/4: only beat 1 (2 does not divide 3)
        [TestCase(4.5f, 4, 4)]      // 270 bpm: only beat 1
        [TestCase(9f, 4, 8)]        // absurd: every second bar
        [TestCase(0f, 4, 1)]        // unknown tempo
        [TestCase(-1f, 4, 1)]
        public void Pulse_DividerKeepsTheBeatPulseAtOrBelow2Hz(float beatsPerSecond, int beatsPerBar, int expected)
        {
            Assert.AreEqual(expected, BeatDots.PulseEveryBeats(beatsPerSecond, beatsPerBar, BeatDots.MaxPulseHz));
        }

        [Test]
        public void Pulse_RateNeverExceedsTheCapAtAnyTempo()
        {
            Assert.LessOrEqual(BeatDots.MaxPulseHz, 2f);                                    // docs/MASTER_PLAN.md section 17
            for (int bar = 2; bar <= 6; bar++)
            {
                for (float bps = 0.25f; bps <= 14f; bps += 0.05f)
                    Assert.LessOrEqual(BeatDots.PulseHz(bps, bar, BeatDots.MaxPulseHz), BeatDots.MaxPulseHz * 1.001f, bps + " beats/s in " + bar);
            }
            Assert.AreEqual(1.5f, BeatDots.PulseHz(1.5f, 4, 2f), 1e-6);                     // slow songs pulse on every beat
            Assert.AreEqual(1.25f, BeatDots.PulseHz(2.5f, 4, 2f), 1e-6);
            Assert.AreEqual(0f, BeatDots.PulseHz(0f, 4, 2f));
        }

        [Test]
        public void Pulse_PatternRepeatsEveryBar()
        {
            Assert.IsTrue(BeatDots.BeatPulses(0, 4, 1, 0));
            Assert.IsTrue(BeatDots.BeatPulses(3, 4, 1, 0));
            Assert.IsTrue(BeatDots.BeatPulses(0, 4, 2, 0));
            Assert.IsFalse(BeatDots.BeatPulses(1, 4, 2, 0));
            Assert.IsTrue(BeatDots.BeatPulses(2, 4, 2, 5));
            Assert.IsFalse(BeatDots.BeatPulses(3, 4, 2, 0));
            Assert.IsTrue(BeatDots.BeatPulses(0, 3, 3, 0));
            Assert.IsFalse(BeatDots.BeatPulses(1, 3, 3, 0));
            Assert.IsFalse(BeatDots.BeatPulses(2, 3, 3, 0));
            Assert.IsTrue(BeatDots.BeatPulses(0, 4, 8, 0));                                 // every second bar
            Assert.IsFalse(BeatDots.BeatPulses(0, 4, 8, 1));
            Assert.IsTrue(BeatDots.BeatPulses(0, 4, 8, 2));
            Assert.IsFalse(BeatDots.BeatPulses(2, 4, 8, 0));
        }

        [Test]
        public void Pulse_PeaksOnTheBeatAndEasesOut()
        {
            Assert.AreEqual(1f, BeatDots.PulseEnvelope(0f), 1e-6);
            Assert.AreEqual(0.25f, BeatDots.PulseEnvelope(0.25f), 1e-5);
            Assert.AreEqual(0f, BeatDots.PulseEnvelope(0.5f));
            Assert.AreEqual(0f, BeatDots.PulseEnvelope(0.9f));
            Assert.AreEqual(0f, BeatDots.PulseEnvelope(float.NaN));
            float previous = 2f;
            for (float p = 0f; p < 1f; p += 0.02f)
            {
                float e = BeatDots.PulseEnvelope(p);
                Assert.LessOrEqual(e, previous);
                previous = e;
            }
        }

        [Test]
        public void Dots_TheHighlightWalksTowardTheChange()
        {
            Assert.AreEqual(0, BeatDots.CurrentIndex(4, 4));                                // four beats left: the first dot
            Assert.AreEqual(1, BeatDots.CurrentIndex(4, 3));
            Assert.AreEqual(2, BeatDots.CurrentIndex(4, 2));                                // "beat 2 of 3" of the first line: two left
            Assert.AreEqual(3, BeatDots.CurrentIndex(4, 1));                                // the last dot
            Assert.AreEqual(4, BeatDots.CurrentIndex(4, 0));                                // due now (wait mode): no current dot
            Assert.AreEqual(0, BeatDots.CurrentIndex(8, 16));                               // long wait: the first dot stays current
            Assert.AreEqual(0, BeatDots.CurrentIndex(8, 8));
            Assert.AreEqual(1, BeatDots.CurrentIndex(8, 7));
            Assert.AreEqual(0, BeatDots.CurrentIndex(0, 3));
        }

        [Test]
        public void Dots_TheCountdownRestartsWhenANewChordBegins()
        {
            int span = BeatDots.NextSpan(0, int.MinValue, 4);                               // first call
            Assert.AreEqual(4, span);
            span = BeatDots.NextSpan(span, 4, 3);
            span = BeatDots.NextSpan(span, 3, 2);
            span = BeatDots.NextSpan(span, 2, 1);
            Assert.AreEqual(4, span);                                                       // the row keeps its four places
            span = BeatDots.NextSpan(span, 1, 0);                                           // holding at the change
            Assert.AreEqual(4, span);
            span = BeatDots.NextSpan(span, 0, 2);                                           // next chord: two beats
            Assert.AreEqual(2, span);
            Assert.AreEqual(0, BeatDots.NextSpan(0, int.MinValue, -3));
        }

        [Test]
        public void Dots_SizesAreContinuousAcrossTheBeat_AndTheSwellPeaksOnIt()
        {
            // Just before the beat the next dot has grown to the pulse peak; just after, as the current dot, it has the same size.
            float before = BeatDots.DotSizeDeg(2, 1, 0.9999f, true, true);
            float after = BeatDots.DotSizeDeg(2, 2, 0f, true, true);
            Assert.AreEqual(PlayHudLayout.DotCurrentDeg * (1f + BeatDots.PulseGain), after, 1e-5);
            Assert.AreEqual(after, before, 1e-3);

            // The dot that was current has shrunk to a played dot by the end of its beat.
            Assert.AreEqual(PlayHudLayout.DotPassedDeg, BeatDots.DotSizeDeg(1, 1, 0.9999f, true, true), 1e-3);
            Assert.AreEqual(PlayHudLayout.DotPassedDeg, BeatDots.DotSizeDeg(1, 2, 0f, true, true), 1e-6);

            // Without a pulse (a capped beat) the current dot is simply the large size.
            Assert.AreEqual(PlayHudLayout.DotCurrentDeg, BeatDots.DotSizeDeg(2, 2, 0f, false, false), 1e-6);
            Assert.AreEqual(PlayHudLayout.DotCurrentDeg, BeatDots.DotSizeDeg(2, 2, 0.6f, true, true), 1e-6);   // swell over, hand-over not begun
            Assert.AreEqual(PlayHudLayout.DotUpcomingDeg, BeatDots.DotSizeDeg(3, 1, 0.5f, true, true), 1e-6);
        }

        [Test]
        public void Dots_MeaningIsCarriedBySizeNotOnlyByBrightness()
        {
            Assert.Less(PlayHudLayout.DotPassedDeg, PlayHudLayout.DotUpcomingDeg);
            Assert.Less(PlayHudLayout.DotUpcomingDeg, PlayHudLayout.DotCurrentDeg);
            Assert.Less(BeatDots.DotLevel(0, 2, 0.3f), BeatDots.DotLevel(3, 2, 0.3f));      // played dimmer than coming
            Assert.Less(BeatDots.DotLevel(3, 2, 0.3f), BeatDots.DotLevel(2, 2, 0.3f));      // coming dimmer than current
            Assert.AreEqual(1f, BeatDots.Currentness(2, 2, 0.3f), 1e-6);
            Assert.AreEqual(0f, BeatDots.Currentness(3, 2, 0.3f), 1e-6);
            Assert.AreEqual(0.5f, BeatDots.Currentness(2, 2, 0.925f), 1e-4);                // half-way through the hand-over
            Assert.AreEqual(0.5f, BeatDots.Currentness(3, 2, 0.925f), 1e-4);
        }

        [Test]
        public void Dots_FirstBeatOfEachBarIsMarked()
        {
            // 3/4, playing the pickup (beat 3, index 2) with 4 beats to the change: the next dot is beat 1 of the bar.
            Assert.IsFalse(BeatDots.IsDownbeat(0, 0, 4, 4, 2, 3));
            Assert.IsTrue(BeatDots.IsDownbeat(1, 0, 4, 4, 2, 3));
            Assert.IsFalse(BeatDots.IsDownbeat(2, 0, 4, 4, 2, 3));
            Assert.IsFalse(BeatDots.IsDownbeat(3, 0, 4, 4, 2, 3));
            // One beat later the same dots stand for the same beats.
            Assert.IsFalse(BeatDots.IsDownbeat(0, 1, 4, 3, 0, 3));
            Assert.IsTrue(BeatDots.IsDownbeat(1, 1, 4, 3, 0, 3));
            Assert.IsFalse(BeatDots.IsDownbeat(2, 1, 4, 3, 0, 3));
            // 4/4 from the bar line with 8 beats: dots 0 and 4.
            Assert.IsTrue(BeatDots.IsDownbeat(0, 0, 8, 8, 0, 4));
            Assert.IsTrue(BeatDots.IsDownbeat(4, 0, 8, 8, 0, 4));
            Assert.IsFalse(BeatDots.IsDownbeat(5, 0, 8, 8, 0, 4));
        }

        // ---------------------------------------------------------------- layout: where the card sits

        [Test]
        public void FretDistance_Fret5OfA648mmScaleIs162p5mm()
        {
            // docs/MASTER_PLAN.md appendix B4: d1 = 36.4, d5 = 162.5, d12 = 324.0 mm for L = 648 mm.
            Assert.AreEqual(162.5485, PlayHudLayout.FretDistanceMm(648f, 5f), 1e-2);
            Assert.AreEqual(36.3694, PlayHudLayout.FretDistanceMm(648f, 1f), 1e-2);
            Assert.AreEqual(324.0, PlayHudLayout.FretDistanceMm(648f, 12f), 1e-2);
            Assert.AreEqual(0f, PlayHudLayout.FretDistanceMm(648f, 0f), 1e-6);
            Assert.AreEqual(0f, PlayHudLayout.FretDistanceMm(0f, 5f));
        }

        [Test]
        public void Card_DefaultsAreThePlansNumbers()
        {
            // docs/MASTER_PLAN.md section 17: "about 8 cm above the neck near fret 5".
            Assert.AreEqual(5f, PlayHudLayout.DefaultAnchorFret);
            Assert.AreEqual(80f, PlayHudLayout.DefaultLiftMm);
        }

        [Test]
        public void Lift_GoesOutOfTheFretboardFrontWhenTheNeckIsSeenAtAGrazingAngle()
        {
            // Neck along x, fretboard facing +z, eyes above and slightly in front (a guitar on the lap, seen from above).
            Vector3 lift = PlayHudLayout.LiftDirection(Vector3.right, Vector3.forward, new Vector3(0f, 0.9f, 0.2f), Vector3.up);
            AssertVector(0f, -0.2169305f, 0.9761871f, lift);
            Assert.AreEqual(1f, lift.magnitude, 1e-5);
            Assert.AreEqual(0f, Vector3.Dot(lift, Vector3.right), 1e-5);                    // square to the neck
            Assert.AreEqual(0f, Vector3.Dot(lift, new Vector3(0f, 0.9f, 0.2f).normalized), 1e-5);   // and to the line of sight
            Assert.Greater(Vector3.Dot(lift, Vector3.forward), 0.9f);                       // on the fretboard's front side
        }

        [Test]
        public void Lift_GoesUpInTheDisplayWhenTheNeckIsSeenFaceOn()
        {
            // The floating neck: the fretboard faces the eyes, so "out of the fretboard" would hide the neck behind the card.
            Vector3 lift = PlayHudLayout.LiftDirection(Vector3.right, Vector3.forward, Vector3.forward, Vector3.up);
            AssertVector(0f, 1f, 0f, lift);
            Vector3 flipped = PlayHudLayout.LiftDirection(Vector3.right, Vector3.forward, Vector3.forward, Vector3.down);
            AssertVector(0f, -1f, 0f, flipped);
        }

        [Test]
        public void Lift_DegenerateViewsStillGiveAUnitDirection()
        {
            Vector3 alongNeck = PlayHudLayout.LiftDirection(Vector3.right, Vector3.up, Vector3.right, Vector3.up);
            AssertVector(0f, 1f, 0f, alongNeck);                                            // looking straight down the neck
            Vector3 noEye = PlayHudLayout.LiftDirection(Vector3.right, Vector3.up, Vector3.zero, Vector3.up);
            Assert.AreEqual(1f, noEye.magnitude, 1e-5);
        }

        [Test]
        public void Anchor_Is8cmFromTheNeckAtFret5_AndScalesWithTheNeck()
        {
            Vector3 eye = new Vector3(0.1625f, 0.5f, 0.1f);
            Vector3 anchor = PlayHudLayout.AnchorWorld(Vector3.zero, Vector3.right, Vector3.forward, eye, Vector3.up, 162.5f, 80f);
            Assert.AreEqual(0.1625f, anchor.x, 1e-5);                                       // still above fret 5
            Assert.AreEqual(0.08f, (anchor - new Vector3(0.1625f, 0f, 0f)).magnitude, 1e-5);

            // A neck drawn 1.2 times life size: fret 5 is 1.2 times as far along, the card 1.2 times as far off.
            Vector3 big = PlayHudLayout.AnchorWorld(Vector3.zero, Vector3.right * 1.2f, Vector3.forward * 1.2f, eye, Vector3.up, 162.5f, 80f);
            Assert.AreEqual(0.195f, big.x, 1e-5);
            Assert.AreEqual(0.096f, (big - new Vector3(0.195f, 0f, 0f)).magnitude, 1e-5);

            // A mirrored (left-handed) neck: +x points the other way, the card follows it.
            Vector3 mirrored = PlayHudLayout.AnchorWorld(Vector3.zero, Vector3.left, Vector3.forward, eye, Vector3.up, 162.5f, 80f);
            Assert.AreEqual(-0.1625f, mirrored.x, 1e-5);
        }

        [Test]
        public void Billboard_FacesTheEyesAndStaysUpright()
        {
            Vector3 card = new Vector3(-0.25f, -0.4f, 0.2f);
            PlayHudLayout.BillboardBasis(card, Vector3.zero, Vector3.up, out Vector3 right, out Vector3 up, out Vector3 forward);
            AssertVector(-0.4881f, -0.7809f, 0.3904f, forward, 1e-3f);                      // from the eyes to the card
            Assert.AreEqual(1f, right.magnitude, 1e-5);
            Assert.AreEqual(1f, up.magnitude, 1e-5);
            Assert.AreEqual(0f, Vector3.Dot(right, up), 1e-5);
            Assert.AreEqual(0f, Vector3.Dot(right, forward), 1e-5);
            Assert.AreEqual(0f, Vector3.Dot(up, forward), 1e-5);
            Assert.AreEqual(0f, right.y, 1e-5);                                             // level: no roll against the up hint
            Assert.Greater(up.y, 0f);
            // Left-handed-safe: the axes are a proper Unity frame (right x up... = forward), never a mirror image.
            AssertVector(forward.x, forward.y, forward.z, Vector3.Cross(right, up));
        }

        [Test]
        public void Billboard_StraightAheadIsTheIdentityFrame_AndDegenerateInputIsSafe()
        {
            PlayHudLayout.BillboardBasis(new Vector3(0f, 0f, 2f), Vector3.zero, Vector3.up, out Vector3 right, out Vector3 up, out Vector3 forward);
            AssertVector(1f, 0f, 0f, right);
            AssertVector(0f, 1f, 0f, up);
            AssertVector(0f, 0f, 1f, forward);

            PlayHudLayout.BillboardBasis(new Vector3(0f, -1f, 0f), Vector3.zero, Vector3.up, out right, out up, out forward);   // looking straight down
            Assert.AreEqual(1f, right.magnitude, 1e-5);
            Assert.AreEqual(1f, up.magnitude, 1e-5);
            PlayHudLayout.BillboardBasis(Vector3.zero, Vector3.zero, Vector3.zero, out right, out up, out forward);            // nothing known
            Assert.AreEqual(1f, right.magnitude, 1e-5);
            Assert.AreEqual(1f, forward.magnitude, 1e-5);
        }

        [Test]
        public void Card_KeepsItsAngularSizeAtAnyDistance()
        {
            Assert.AreEqual(1f, PlayHudLayout.ScaleForDistance(0.5f, PlayHudLayout.DesignDistanceM), 1e-6);
            Assert.AreEqual(1.06f, PlayHudLayout.ScaleForDistance(0.53f, 0.5f), 1e-5);
            Assert.AreEqual(2f, PlayHudLayout.ScaleForDistance(1f, 0.5f), 1e-6);            // the floating neck, a metre away
            Assert.AreEqual(1f, PlayHudLayout.ScaleForDistance(0f, 0.5f));
            Assert.AreEqual(1f, PlayHudLayout.ScaleForDistance(float.NaN, 0.5f));
            // A 4 degree chord name authored for 0.5 m, scaled for 1 m, is exactly a 4 degree chord name at 1 m.
            float authored = PlayHudLayout.Size(4f, 0.5f);
            Assert.AreEqual(0.0349208, authored, Tight);
            Assert.AreEqual(PlayHudLayout.Size(4f, 1f), authored * PlayHudLayout.ScaleForDistance(1f, 0.5f), 1e-6);
        }

        [Test]
        public void Offset_IsDistanceTimesTangent()
        {
            Assert.AreEqual(0.0262039, PlayHudLayout.Offset(3f, 0.5f), Tight);
            Assert.AreEqual(-0.0262039, PlayHudLayout.Offset(-3f, 0.5f), Tight);
            Assert.AreEqual(0f, PlayHudLayout.Offset(0f, 0.5f));
            Assert.AreEqual(0f, PlayHudLayout.Offset(3f, 0f));
        }

        // The signed-offset trap (Hud/Learn had it too): HudText.SizeForAngularHeight returns 0 for a negative angle, so a
        // layout helper built on it puts everything that belongs below or left of its origin ON the origin. This test fails
        // on the old PlayHudLayout.Size (it returned 0 for -2 degrees).
        [Test]
        public void Size_IsSigned_SoAPartBelowOrLeftOfItsOriginNeverCollapsesOntoIt()
        {
            // 2 * 0.5 m * tan(1 degree) = 0.0174551 m (python3: 2*0.5*math.tan(math.radians(1)))
            Assert.AreEqual(0.0174551, PlayHudLayout.Size(2f, 0.5f), Tight);
            Assert.AreEqual(-0.0174551, PlayHudLayout.Size(-2f, 0.5f), Tight);
            Assert.AreEqual(-PlayHudLayout.Size(1.55f, 0.5f), PlayHudLayout.Size(-1.55f, 0.5f), 1e-7);   // the cursor lane drop
            Assert.AreEqual(0f, PlayHudLayout.Size(0f, 0.5f));
            Assert.AreEqual(0f, PlayHudLayout.Size(-2f, 0f));
            Assert.AreEqual(0f, PlayHudLayout.Size(float.NaN, 0.5f));
            Assert.AreEqual(0f, PlayHudLayout.Size(float.NegativeInfinity, 0.5f));
        }

        [Test]
        public void SignedMeters_MirrorsTheUnsignedSizeAndKeepsTheSign()
        {
            for (float deg = 0.25f; deg < 20f; deg += 1.5f)
            {
                float unsigned = HudText.SizeForAngularHeight(deg, 0.7f);
                Assert.AreEqual(unsigned, PlayHudLayout.SignedMeters(deg, 0.7f), 1e-7);
                Assert.AreEqual(-unsigned, PlayHudLayout.SignedMeters(-deg, 0.7f), 1e-7);
                Assert.AreEqual(PlayHudLayout.SignedMeters(-deg, 0.7f), PlayHudLayout.Size(-deg, 0.7f), 1e-7);
            }
            Assert.AreEqual(0f, PlayHudLayout.SignedMeters(-3f, -1f));
            Assert.AreEqual(0f, PlayHudLayout.SignedMeters(float.PositiveInfinity, 0.5f));
        }

        [Test]
        public void Layout_TextSizesRespectTheProjectMinimums()
        {
            Assert.GreaterOrEqual(PlayHudLayout.ChordCapDeg, HudText.ChordNameMinDegrees);   // chord names 3 degrees
            float[] caps =
            {
                PlayHudLayout.NextWordCapDeg, PlayHudLayout.NextChordCapDeg, PlayHudLayout.WaitCueCapDeg, PlayHudLayout.PatternDigitCapDeg,
                PlayHudLayout.LyricLineCapDeg, PlayHudLayout.LyricChordCapDeg, PlayHudLayout.LyricNextCapDeg,
                PlayHudLayout.SequenceNameCapDeg, PlayHudLayout.SequenceNextCapDeg, PlayHudLayout.CountInCapDeg,
                ResultsCard.TitleCapDeg, ResultsCard.ScoreCapDeg, ResultsCard.LabelCapDeg, ResultsCard.TimingCapDeg, ResultsCard.LineCapDeg,
            };
            foreach (float cap in caps) Assert.GreaterOrEqual(cap, HudText.MinTextDegrees);
            Assert.Less(PlayHudLayout.LyricNextCapDeg, PlayHudLayout.LyricLineCapDeg);       // "the next line smaller"
            Assert.AreEqual(1.2f, PlayHudLayout.TextDeg(0.5f, false), 1e-6);                 // never below the minimum
            Assert.AreEqual(1.5f * HudText.LargeTextScale, PlayHudLayout.TextDeg(1.5f, true), 1e-5);
        }

        [Test]
        public void Layout_RowsAreStackedWithoutOverlap_AndTheColumnFitsTheView()
        {
            // Bottom to top: cursor lane, strip, dots, chord, next lyric line, caret, lyric line, marker, chord names.
            float laneBottom = PlayHudLayout.PatternRowDeg - PlayHudLayout.PatternLaneDropDeg - PlayHudLayout.PatternCaretDeg * 0.5f;
            float stripTop = PlayHudLayout.PatternRowDeg + PlayHudLayout.PatternGlyphDeg * 0.5f * (1f + PatternStrip.HitGain);
            float dotsBottom = PlayHudLayout.DotsRowDeg - PlayHudLayout.DotCurrentDeg * 0.5f * (1f + BeatDots.PulseGain) * BeatDots.DownbeatGain;
            float dotsTop = PlayHudLayout.DotsRowDeg + PlayHudLayout.DotCurrentDeg * 0.5f * (1f + BeatDots.PulseGain) * BeatDots.DownbeatGain;
            float chordBottom = PlayHudLayout.ChordRowDeg - PlayHudLayout.ChordCapDeg * 0.5f * (1f + NeckHudCard.WaitBreathGain);
            float chordTop = PlayHudLayout.ChordRowDeg + PlayHudLayout.ChordCapDeg * 0.5f * (1f + NeckHudCard.WaitBreathGain);
            float nextLineBottom = PlayHudLayout.LyricRowDeg - PlayHudLayout.LyricNextDropDeg - PlayHudLayout.LyricNextCapDeg * 0.8f;
            float nextLineTop = PlayHudLayout.LyricRowDeg - PlayHudLayout.LyricNextDropDeg + PlayHudLayout.LyricNextCapDeg * 0.5f;
            float caretRow = PlayHudLayout.LyricRowDeg - PlayHudLayout.LyricCaretDropDeg;
            float lineBottom = PlayHudLayout.LyricRowDeg - PlayHudLayout.LyricLineCapDeg * 0.5f;
            float lineTop = PlayHudLayout.LyricRowDeg + PlayHudLayout.LyricLineCapDeg * 0.5f;
            float markerRow = PlayHudLayout.LyricRowDeg + PlayHudLayout.LyricMarkerRiseDeg;
            float chordNamesBottom = PlayHudLayout.LyricRowDeg + PlayHudLayout.LyricChordRiseDeg - PlayHudLayout.LyricChordCapDeg * 0.5f;
            float chordNamesTop = PlayHudLayout.LyricRowDeg + PlayHudLayout.LyricChordRiseDeg + PlayHudLayout.LyricChordCapDeg * 0.5f;

            Assert.Less(stripTop, dotsBottom);
            Assert.Less(dotsTop, chordBottom);
            Assert.Less(chordTop, nextLineBottom);
            Assert.Less(nextLineTop, caretRow - PlayHudLayout.LyricCaretDeg * 0.5f);
            Assert.Less(caretRow + PlayHudLayout.LyricCaretDeg * 0.5f, lineBottom);
            Assert.Less(lineTop, markerRow - PlayHudLayout.LyricMarkerHeightDeg * 0.5f);
            Assert.Less(markerRow + PlayHudLayout.LyricMarkerHeightDeg * 0.5f, chordNamesBottom);

            Assert.Less(chordNamesTop - laneBottom, PlayHudLayout.ViewHeightDeg);            // the whole column fits one 26.9 degree eye
            Assert.LessOrEqual(PlayHudLayout.ColumnMaxWidthDeg, PlayHudLayout.ViewWidthDeg * 0.5f);
        }

        // Ink, not cap boxes: "Happy" and "birthday" hang 'p' and 'y' below the line. Arial's descender is 434/2048 em and its
        // cap height 1467/2048 em, so a descender reaches 0.296 cap heights below the baseline; lowercase ascenders (b, d, h)
        // stand as tall as the capitals. The caret that glides under the words must clear both the descenders above it and
        // the ascenders of the next line below it. (The old spacing put the caret 0.24 degrees into the descenders.)
        [Test]
        public void Layout_LyricCaretClearsTheDescendersAbove_AndTheNextLineBelow()
        {
            const float descenderOfCap = 434f / 1467f;
            const float clearanceDeg = 0.08f;                                                    // ~3 glasses pixels
            float descenderBottom = PlayHudLayout.LyricRowDeg - PlayHudLayout.LyricLineCapDeg * (0.5f + descenderOfCap);
            float caretRow = PlayHudLayout.LyricRowDeg - PlayHudLayout.LyricCaretDropDeg;
            float caretTop = caretRow + PlayHudLayout.LyricCaretDeg * 0.5f;
            float caretBottom = caretRow - PlayHudLayout.LyricCaretDeg * 0.5f;
            float nextInkTop = PlayHudLayout.LyricRowDeg - PlayHudLayout.LyricNextDropDeg + PlayHudLayout.LyricNextCapDeg * 0.5f;
            Assert.Less(caretTop + clearanceDeg, descenderBottom);
            Assert.Less(nextInkTop + clearanceDeg, caretBottom);
            // ...and the next line's own descenders stay clear of the big chord name below it (it may breathe while waiting).
            float nextDescenderBottom = PlayHudLayout.LyricRowDeg - PlayHudLayout.LyricNextDropDeg - PlayHudLayout.LyricNextCapDeg * (0.5f + descenderOfCap);
            float chordTop = PlayHudLayout.ChordRowDeg + PlayHudLayout.ChordCapDeg * 0.5f * (1f + NeckHudCard.WaitBreathGain);
            Assert.Less(chordTop + clearanceDeg, nextDescenderBottom);
        }

        [Test]
        public void Layout_WhatIsNeededWhilePlayingStaysNearTheAnchor()
        {
            // Fixed requirement 2 and section 17: the chord, the countdown and the strip sit within about 10 degrees.
            Assert.LessOrEqual(Mathf.Abs(PlayHudLayout.ChordRowDeg) + PlayHudLayout.ChordCapDeg * 0.5f, PlayHudLayout.PlayZoneDeg);
            Assert.LessOrEqual(Mathf.Abs(PlayHudLayout.DotsRowDeg), PlayHudLayout.PlayZoneDeg);
            Assert.LessOrEqual(Mathf.Abs(PlayHudLayout.PatternRowDeg) + PlayHudLayout.PatternLaneDropDeg + PlayHudLayout.PatternCaretDeg * 0.5f, PlayHudLayout.PlayZoneDeg);
            Assert.LessOrEqual(Mathf.Abs(PlayHudLayout.NextChordRowDeg), PlayHudLayout.PlayZoneDeg);
            // The chord is the element closest to the anchor row of all text rows.
            Assert.Less(Mathf.Abs(PlayHudLayout.ChordRowDeg), Mathf.Abs(PlayHudLayout.LyricRowDeg - PlayHudLayout.LyricNextDropDeg));
            // The strip for the songs' 6 and 8 slot patterns fits the column.
            Assert.LessOrEqual(8 * PlayHudLayout.PatternPitchDeg, PlayHudLayout.ColumnMaxWidthDeg);
            // The count-in sits in the next-chord block, clear of the big chord.
            Assert.Greater(PlayHudLayout.CountInXDeg, PlayHudLayout.NextBlockLeftDeg);
        }

        [Test]
        public void FitCap_ShrinksALongLineToTheColumnButNeverBelowTheMinimum()
        {
            // 10.56 em (a 21 letter line) at 1.5 degrees would be 22.1 degrees wide: shrunk to fit 18 degrees.
            Assert.AreEqual(1.22045, PlayHudLayout.FitCapDegrees(10.56f, 1.5f, 18f, 1.2f, 0.716f), 1e-3);
            // 12.72 em (a 26 letter line) would need 1.01 degrees: held at the 1.2 degree minimum (it runs a little wide).
            Assert.AreEqual(1.2f, PlayHudLayout.FitCapDegrees(12.72f, 1.5f, 18f, 1.2f, 0.716f), 1e-6);
            // A short line keeps the preferred size.
            Assert.AreEqual(1.5f, PlayHudLayout.FitCapDegrees(4f, 1.5f, 18f, 1.2f, 0.716f), 1e-6);
            // Unusable input: the preferred size, never less than the minimum.
            Assert.AreEqual(1.5f, PlayHudLayout.FitCapDegrees(0f, 1.5f, 18f, 1.2f, 0.716f), 1e-6);
            Assert.AreEqual(1.2f, PlayHudLayout.FitCapDegrees(5f, 0.4f, 18f, 0.2f, 0.716f), 1e-6);
        }

        [Test]
        public void Maths_PositiveModAndEase()
        {
            Assert.AreEqual(1.25f, PlayHudLayout.PositiveMod(9.25f, 8f), 1e-5);
            Assert.AreEqual(7.5f, PlayHudLayout.PositiveMod(-0.5f, 8f), 1e-5);
            Assert.AreEqual(0f, PlayHudLayout.PositiveMod(8f, 8f), 1e-6);
            Assert.AreEqual(0f, PlayHudLayout.PositiveMod(3f, 0f));
            Assert.AreEqual(0f, PlayHudLayout.PositiveMod(float.NaN, 4f));
            Assert.AreEqual(2, PlayHudLayout.PositiveMod(-1, 3));
            Assert.AreEqual(1, PlayHudLayout.PositiveMod(7, 3));
            Assert.AreEqual(0, PlayHudLayout.PositiveMod(7, 0));

            Assert.AreEqual(0f, PlayHudLayout.Ease01(-1f));
            Assert.AreEqual(0f, PlayHudLayout.Ease01(0f));
            Assert.AreEqual(0.15625f, PlayHudLayout.Ease01(0.25f), 1e-6);
            Assert.AreEqual(0.5f, PlayHudLayout.Ease01(0.5f), 1e-6);
            Assert.AreEqual(0.84375f, PlayHudLayout.Ease01(0.75f), 1e-6);
            Assert.AreEqual(1f, PlayHudLayout.Ease01(1f));
            Assert.AreEqual(1f, PlayHudLayout.Ease01(7f));
            Assert.AreEqual(0f, PlayHudLayout.Ease01(float.NaN));
        }

        // ---------------------------------------------------------------- wait cue, count-in, results

        [Test]
        public void WaitCue_SaysWhatToPlayAndNeverNags()
        {
            Assert.AreEqual("strum G", NeckHudCard.WaitCueText("G"));
            Assert.AreEqual("strum Em", NeckHudCard.WaitCueText("Em"));
            Assert.AreEqual(string.Empty, NeckHudCard.WaitCueText(null));
            Assert.AreEqual(string.Empty, NeckHudCard.WaitCueText(""));

            Assert.LessOrEqual(NeckHudCard.WaitBreathHz, 1f);                                // slow, far below the 3 Hz limit
            Assert.LessOrEqual(NeckHudCard.WaitBreathGain, 0.1f);                            // and small
            Assert.AreEqual(0f, NeckHudCard.WaitBreath(0f, 0.6f), 1e-6);                     // starts from rest: no jump
            Assert.AreEqual(1f, NeckHudCard.WaitBreath(0.5f / 0.6f, 0.6f), 1e-5);            // top of the breath after half a period
            Assert.AreEqual(0f, NeckHudCard.WaitBreath(1f / 0.6f, 0.6f), 1e-5);
            for (float t = 0f; t < 5f; t += 0.03f)
            {
                float b = NeckHudCard.WaitBreath(t, 0.6f);
                Assert.GreaterOrEqual(b, 0f);
                Assert.LessOrEqual(b, 1f);
            }
            Assert.AreEqual(0f, NeckHudCard.WaitBreath(-1f, 0.6f));
        }

        [Test]
        public void CountIn_ShowsTheBeatsLeftAndSettlesWithoutAJump()
        {
            Assert.AreEqual("4", CountIn.TextFor(4));
            Assert.AreEqual("2", CountIn.TextFor(2));
            Assert.AreEqual("1", CountIn.TextFor(1));
            Assert.AreEqual(string.Empty, CountIn.TextFor(0));
            Assert.AreEqual(string.Empty, CountIn.TextFor(-2));
            Assert.AreEqual("16", CountIn.TextFor(99));
            Assert.AreSame(CountIn.TextFor(3), CountIn.TextFor(3));                          // cached: no allocation per beat

            Assert.AreEqual(1f + CountIn.PopGain, CountIn.PopScale(0f, 0.2f, CountIn.PopGain), 1e-6);
            Assert.AreEqual(1f + CountIn.PopGain * 0.25f, CountIn.PopScale(0.1f, 0.2f, CountIn.PopGain), 1e-5);
            Assert.AreEqual(1f, CountIn.PopScale(0.2f, 0.2f, CountIn.PopGain), 1e-6);
            Assert.AreEqual(1f, CountIn.PopScale(3f, 0.2f, CountIn.PopGain));
            Assert.AreEqual(1f, CountIn.PopScale(0.1f, 0f, CountIn.PopGain));
        }

        [Test]
        public void Results_ScoreAndMarks()
        {
            Assert.AreEqual("7 of 8", ResultsCard.ScoreText(7, 8));
            Assert.AreEqual("8 of 8", ResultsCard.ScoreText(12, 8));
            Assert.AreEqual("0 of 8", ResultsCard.ScoreText(-3, 8));
            Assert.AreEqual("done", ResultsCard.ScoreText(0, 0));
            Assert.AreEqual(0.875f, ResultsCard.HitFraction(7, 8), 1e-6);
            Assert.AreEqual(0f, ResultsCard.HitFraction(3, 0));

            Assert.AreEqual(8, ResultsCard.MarkCount(8));
            Assert.AreEqual(7, ResultsCard.FilledMarks(7, 8));
            Assert.AreEqual(16, ResultsCard.MarkCount(40));
            Assert.AreEqual(16, ResultsCard.FilledMarks(40, 40));
            Assert.AreEqual(15, ResultsCard.FilledMarks(39, 40));                            // not perfect: never all filled
            Assert.AreEqual(1, ResultsCard.FilledMarks(1, 40));                              // something clean: at least one
            Assert.AreEqual(8, ResultsCard.FilledMarks(20, 40));
            Assert.AreEqual(0, ResultsCard.FilledMarks(0, 40));
            Assert.AreEqual(0, ResultsCard.MarkCount(0));
        }

        [Test]
        public void Results_TimingInPlainWords()
        {
            Assert.AreEqual("timing: on the beat", ResultsCard.TimingText(0f));
            Assert.AreEqual("timing: on the beat", ResultsCard.TimingText(-45f));
            Assert.AreEqual("timing: a little late (85 ms)", ResultsCard.TimingText(85f));
            Assert.AreEqual("timing: a little early (90 ms)", ResultsCard.TimingText(-90f));
            Assert.AreEqual("timing: late (210 ms)", ResultsCard.TimingText(210f));
            Assert.AreEqual("timing: early (300 ms)", ResultsCard.TimingText(-300f));
            Assert.AreEqual(string.Empty, ResultsCard.TimingText(float.NaN));
        }

        [Test]
        public void Results_TheDefaultLineIsAlwaysKindAndShort()
        {
            int[][] runs = { new[] { 8, 8 }, new[] { 7, 8 }, new[] { 5, 8 }, new[] { 1, 8 }, new[] { 0, 8 }, new[] { 0, 0 } };
            foreach (int[] run in runs)
            {
                string line = ResultsCard.DefaultEncouragement(run[0], run[1]);
                Assert.IsNotEmpty(line);
                Assert.LessOrEqual(line.Split(' ').Length, 7, line);                         // tips are 7 words or fewer
                StringAssert.DoesNotContain("wrong", line.ToLowerInvariant());
                StringAssert.DoesNotContain("fail", line.ToLowerInvariant());
                StringAssert.DoesNotContain("bad", line.ToLowerInvariant());
            }
            Assert.AreNotEqual(ResultsCard.DefaultEncouragement(8, 8), ResultsCard.DefaultEncouragement(1, 8));
        }

        // ---------------------------------------------------------------- shape geometry

        [Test]
        public void Shape_FeatheredPolygonHasAnOpaqueInsideAndATransparentEdge()
        {
            Vector2[] outline = HudShapeGeometry.CircleOutline(0.455f, 40);
            HudShapeGeometry.FeatheredPolygon(outline, Vector2.zero, 0.045f, out Vector3[] v, out Color32[] c, out int[] t);
            Assert.AreEqual(81, v.Length);                                                   // kernel + 40 + 40
            Assert.AreEqual(81, c.Length);
            Assert.AreEqual(360, t.Length);                                                  // 40 * (1 fill + 2 feather) triangles
            Assert.AreEqual(255, c[0].a);
            for (int i = 1; i <= 40; i++)
            {
                Assert.AreEqual(255, c[i].a);
                Assert.AreEqual(0.455f, ((Vector2)v[i]).magnitude, 1e-4);
            }
            for (int i = 41; i <= 80; i++)
            {
                Assert.AreEqual(0, c[i].a);
                Assert.AreEqual(0.5f, ((Vector2)v[i]).magnitude, 2e-3);                      // the feather ends on the unit circle
            }
            foreach (int index in t)
            {
                Assert.GreaterOrEqual(index, 0);
                Assert.Less(index, v.Length);
            }
        }

        [Test]
        public void Shape_OutlinesRunCounterClockwise()
        {
            Assert.Greater(SignedArea(HudShapeGeometry.CircleOutline(0.5f, 24)), 0f);
            Assert.Greater(SignedArea(HudShapeGeometry.ArrowOutline(0.66f, 0.46f, 0.24f)), 0f);
            Assert.Greater(SignedArea(HudShapeGeometry.TriangleOutline(1.1f)), 0f);
            Assert.Greater(SignedArea(HudShapeGeometry.CapsuleOutline(1f, 0.2f, 6)), 0f);    // wide
            Assert.Greater(SignedArea(HudShapeGeometry.CapsuleOutline(0.2f, 1f, 6)), 0f);    // tall
            Assert.AreEqual(0.7854, SignedArea(HudShapeGeometry.CircleOutline(0.5f, 96)), 2e-3);   // pi / 4
        }

        [Test]
        public void Shape_ArrowIsOneTall_PointsUp_AndIsSymmetric()
        {
            Vector2[] arrow = HudShapeGeometry.ArrowOutline(0.66f, 0.46f, 0.24f);
            Assert.AreEqual(7, arrow.Length);
            float minY = float.MaxValue, maxY = float.MinValue, minX = float.MaxValue, maxX = float.MinValue;
            foreach (Vector2 p in arrow)
            {
                minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
            }
            Assert.AreEqual(-0.5f, minY, 1e-6);
            Assert.AreEqual(0.5f, maxY, 1e-6);
            Assert.AreEqual(-0.33f, minX, 1e-6);
            Assert.AreEqual(0.33f, maxX, 1e-6);
            AssertVector(0f, 0.5f, 0f, arrow[0]);                                            // the tip is at the top: the mesh points up
            Vector2 kernel = HudShapeGeometry.ArrowKernel(0.46f);
            Assert.AreEqual(0f, kernel.x, 1e-6);
            Assert.Greater(kernel.y, 0.5f - 0.46f);                                          // inside the head, above its base
            Assert.Less(kernel.y, 0.5f);
        }

        [Test]
        public void Shape_CapsuleHasTheRequestedSize()
        {
            foreach (bool wide in new[] { true, false })
            {
                Vector2[] bar = wide ? HudShapeGeometry.CapsuleOutline(1f, 0.2f, 6) : HudShapeGeometry.CapsuleOutline(0.2f, 1f, 6);
                float minY = float.MaxValue, maxY = float.MinValue, minX = float.MaxValue, maxX = float.MinValue;
                foreach (Vector2 p in bar)
                {
                    minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
                    minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                }
                Assert.AreEqual(wide ? 1f : 0.2f, maxX - minX, 1e-5);
                Assert.AreEqual(wide ? 0.2f : 1f, maxY - minY, 1e-5);
            }
        }

        [Test]
        public void Shape_RingKeepsItsStrokeWhileTheRadiusChanges()
        {
            const int segments = 56;
            var v = new Vector3[HudShapeGeometry.RingVertexCount(segments)];
            Assert.AreEqual(224, v.Length);
            foreach (float radius in new[] { 0.007f, 0.0147f, 0.0224f })
            {
                HudShapeGeometry.FillRingVertices(v, segments, radius, 0.00065f, 0.0005f);
                for (int i = 0; i < segments; i++)
                {
                    Assert.AreEqual(radius - 0.00065f - 0.0005f, v[i].magnitude, 1e-6);                  // feather in
                    Assert.AreEqual(radius - 0.00065f, v[segments + i].magnitude, 1e-6);                // stroke inner edge
                    Assert.AreEqual(radius + 0.00065f, v[2 * segments + i].magnitude, 1e-6);            // stroke outer edge
                    Assert.AreEqual(radius + 0.00065f + 0.0005f, v[3 * segments + i].magnitude, 1e-6);  // feather out
                    Assert.AreEqual(0f, v[i].z);
                }
            }
            Color32[] colors = HudShapeGeometry.RingColors(segments);
            Assert.AreEqual(0, colors[0].a);
            Assert.AreEqual(255, colors[segments].a);
            Assert.AreEqual(255, colors[2 * segments].a);
            Assert.AreEqual(0, colors[3 * segments].a);
            int[] triangles = HudShapeGeometry.RingTriangles(segments);
            Assert.AreEqual(3 * segments * 6, triangles.Length);
            foreach (int index in triangles) Assert.Less(index, v.Length);

            // A ring smaller than its own stroke does not turn inside out.
            HudShapeGeometry.FillRingVertices(v, segments, 0.0002f, 0.00065f, 0.0005f);
            Assert.AreEqual(0f, v[0].magnitude, 1e-9);
            Assert.GreaterOrEqual(v[2 * segments].magnitude, v[segments].magnitude);
        }

        [Test]
        public void Shape_HaloFadesToNothingAtItsEdge()
        {
            HudShapeGeometry.Halo(24, out Vector3[] v, out Color32[] c, out int[] t);
            Assert.AreEqual(1 + 3 * 24, v.Length);
            Assert.AreEqual(255, c[0].a);
            Assert.AreEqual(0, c[v.Length - 1].a);
            Assert.AreEqual(0.5f, v[v.Length - 1].magnitude, 1e-5);
            Assert.AreEqual(24 * 3 + 2 * 24 * 6, t.Length);
            foreach (int index in t) Assert.Less(index, v.Length);
        }

        // ---------------------------------------------------------------- relational overlap check (the preview's checker)

        [Test]
        public void Overlap_PlantedOverlapFires_AndDisjointPartsDoNot()
        {
            // Two "texts" whose ink shares a 6 x 5 block (planted), and a third far away.
            var chord = new PixelMask(64, 32);
            chord.FillRect(2, 2, 20, 12);
            var lyric = new PixelMask(64, 32);
            lyric.FillRect(15, 8, 40, 20);
            var far = new PixelMask(64, 32);
            far.FillRect(50, 25, 60, 30);

            Assert.AreEqual(6 * 5, PixelMask.OverlapPixels(chord, lyric));            // x 15..20, y 8..12
            Assert.AreEqual(PixelMask.OverlapPixels(lyric, chord), PixelMask.OverlapPixels(chord, lyric));
            Assert.AreEqual(0, PixelMask.OverlapPixels(chord, far));
            Assert.IsTrue(PixelMask.OverlapBox(chord, lyric, out int x0, out int y0, out int x1, out int y1));
            Assert.AreEqual(15, x0); Assert.AreEqual(8, y0); Assert.AreEqual(20, x1); Assert.AreEqual(12, y1);

            string why;
            Assert.AreEqual(OverlapKind.Problem, PlayHudOverlap.Classify(
                PlayHudOverlap.Id(PlayHudOverlap.CardGroup, "ChordPivot/CurrentChord"),
                PlayHudOverlap.Id(PlayHudOverlap.CardGroup, "LyricStrip/Lyric"), out why));
        }

        [Test]
        public void Overlap_IsJudgedOnInkNotOnBoxes()
        {
            // A ring (hollow square here) around a dot: their boxes overlap, their pixels do not.
            var ring = new PixelMask(40, 40);
            for (int i = 5; i <= 30; i++) { ring.Set(i, 5); ring.Set(i, 30); ring.Set(5, i); ring.Set(30, i); }
            var dot = new PixelMask(40, 40);
            dot.FillRect(15, 15, 20, 20);
            Assert.AreEqual(0, PixelMask.OverlapPixels(ring, dot));
            Assert.AreEqual(0, PixelMask.BoxGap(ring, dot));                           // the boxes do meet
            dot.FillRect(28, 15, 31, 16);                                              // now the dot's ink reaches the stroke
            Assert.Greater(PixelMask.OverlapPixels(ring, dot), 0);
        }

        [Test]
        public void Overlap_MaskFromPixels_UsesBrightnessNotAlpha_AndFindsTheEdge()
        {
            var pixels = new Color32[4 * 3];
            pixels[0] = new Color32(0, 0, 0, 255);          // black: what the glasses do not show
            pixels[1] = new Color32(0, 0, 200, 0);          // bright blue with zero alpha: lit
            pixels[6] = new Color32(30, 10, 10, 255);       // just above the threshold
            pixels[7] = new Color32(24, 24, 24, 255);       // at the threshold: not lit
            PixelMask mask = PixelMask.FromPixels(pixels, 4, 3, 24);
            Assert.AreEqual(2, mask.Count);
            Assert.IsTrue(mask.Get(1, 0));
            Assert.IsTrue(mask.Get(2, 1));
            Assert.IsFalse(mask.Get(3, 1));
            Assert.IsTrue(mask.TouchesEdge);                 // row 0 is the bottom edge of the frame
            var inside = new PixelMask(10, 10);
            inside.FillRect(3, 3, 5, 5);
            Assert.IsFalse(inside.TouchesEdge);
            var right = new PixelMask(10, 10);
            right.Set(9, 4);                                 // columns 6, 7 and 8 lie between x = 5 and x = 9
            Assert.AreEqual(3, PixelMask.BoxGap(inside, right));
            Assert.AreEqual(3, PixelMask.BoxGap(right, inside));
        }

        [Test]
        public void Overlap_ClassifiesByDesignPairs()
        {
            string why;
            string card = PlayHudOverlap.CardGroup;
            Assert.AreEqual(OverlapKind.ByDesign, PlayHudOverlap.Classify(PlayHudOverlap.Id(card, "PatternStrip/Slot3/Halo"),
                PlayHudOverlap.Id(card, "PatternStrip/Slot3/Arrow"), out why));
            Assert.AreEqual(OverlapKind.Note, PlayHudOverlap.Classify(PlayHudOverlap.Id(card, "PatternStrip/Slot3/Halo"),
                PlayHudOverlap.Id(card, "PatternStrip/Slot4/Arrow"), out why));
            Assert.AreEqual(OverlapKind.Problem, PlayHudOverlap.Classify(PlayHudOverlap.Id(card, "PatternStrip/Slot3/Label"),
                PlayHudOverlap.Id(card, "PatternStrip/Slot3/BassBar"), out why));
            Assert.AreEqual(OverlapKind.ByDesign, PlayHudOverlap.Classify(PlayHudOverlap.Id(card, "PatternStrip/Cursor"),
                PlayHudOverlap.Id(card, "PatternStrip/Beat2"), out why));
            Assert.AreEqual(OverlapKind.Problem, PlayHudOverlap.Classify(PlayHudOverlap.Id(card, "LyricStrip/Caret"),
                PlayHudOverlap.Id(card, "LyricStrip/Lyric"), out why));
            Assert.AreEqual(OverlapKind.ByDesign, PlayHudOverlap.Classify(PlayHudOverlap.Id(PlayHudOverlap.RingsGroup, "Ring0_2"),
                PlayHudOverlap.Id(PlayHudOverlap.RingsGroup, "Target0_2"), out why));
            Assert.AreEqual(OverlapKind.Note, PlayHudOverlap.Classify(PlayHudOverlap.Id(PlayHudOverlap.RingsGroup, "Ring0_2"),
                PlayHudOverlap.Id(PlayHudOverlap.RingsGroup, "Target0_1"), out why));
            Assert.AreEqual(OverlapKind.ByDesign, PlayHudOverlap.Classify(PlayHudOverlap.Id(PlayHudOverlap.RingsGroup, "Ring0_2"),
                PlayHudOverlap.Id(PlayHudOverlap.NeckGroup, "Dots"), out why));
            // The card on the neck is always a problem: it must never hide a dot, a mark or a finger number.
            Assert.AreEqual(OverlapKind.Problem, PlayHudOverlap.Classify(PlayHudOverlap.Id(card, "BeatDots/Dot0"),
                PlayHudOverlap.Id(PlayHudOverlap.NeckGroup, "FingerNumber1/Text"), out why));
            Assert.IsFalse(PlayHudOverlap.ShouldCompare(PlayHudOverlap.Id(PlayHudOverlap.NeckGroup, "Dots"),
                PlayHudOverlap.Id(PlayHudOverlap.NeckGroup, "FingerNumber0/Text")));
            Assert.IsTrue(PlayHudOverlap.ShouldCompare(PlayHudOverlap.Id(card, "NextChord"),
                PlayHudOverlap.Id(PlayHudOverlap.NeckGroup, "Dots")));
        }

        // ---------------------------------------------------------------- helpers

        static float[] UnitPrefix(int characters)
        {
            var prefix = new float[characters + 1];
            for (int i = 0; i <= characters; i++) prefix[i] = i;
            return prefix;
        }

        static float SignedArea(Vector2[] outline)
        {
            float area = 0f;
            for (int i = 0; i < outline.Length; i++)
            {
                Vector2 a = outline[i];
                Vector2 b = outline[(i + 1) % outline.Length];
                area += a.x * b.y - b.x * a.y;
            }
            return area * 0.5f;
        }

        static void AssertVector(float x, float y, float z, Vector3 actual, float tolerance = 1e-4f)
        {
            Assert.AreEqual(x, actual.x, tolerance);
            Assert.AreEqual(y, actual.y, tolerance);
            Assert.AreEqual(z, actual.z, tolerance);
        }
    }
}
