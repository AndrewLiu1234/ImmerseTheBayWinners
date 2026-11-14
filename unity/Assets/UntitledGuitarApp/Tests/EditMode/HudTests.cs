using System.Globalization;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UntitledGuitarApp.Runtime.Hud;
using UntitledGuitarApp.Runtime.Spectator;

namespace UntitledGuitarApp.Tests.EditMode
{
    // Pure-logic tests for the HUD and the spectator view. They need no Play Mode, no device and no rendering: the decisions
    // (sizes, meter mapping, "rebuild or not", badge visibility, framing) live in static functions on purpose.
    // Expected numbers were derived independently (python3 math), not by running this code.
    public class HudTests
    {
        const double Tight = 1e-4;

        // ---------------------------------------------------------------- angular size

        [Test]
        public void MinimumTextAt1p2m_IsAbout2p5cm()
        {
            // docs/MASTER_PLAN.md section 17: 1.2 degrees is "about 2.5 cm cap height at 1.2 m".
            Assert.AreEqual(0.0251337, HudText.SizeForAngularHeight(HudText.MinTextDegrees, 1.2f), Tight);
        }

        [Test]
        public void MinimumTextAt0p8m_IsAbout1p7cm()
        {
            Assert.AreEqual(0.0167558, HudText.SizeForAngularHeight(HudText.MinTextDegrees, 0.8f), Tight);
        }

        [Test]
        public void ChordNameMinimumAt1m_IsAbout5p2cm()
        {
            Assert.AreEqual(0.0523718, HudText.SizeForAngularHeight(HudText.ChordNameMinDegrees, 1f), Tight);
        }

        [Test]
        public void ProjectMinimumsAreTheDocumentedOnes()
        {
            Assert.AreEqual(1.2f, HudText.MinTextDegrees);
            Assert.AreEqual(3f, HudText.ChordNameMinDegrees);
            Assert.GreaterOrEqual(ChordLabel.CurrentDegrees, HudText.ChordNameMinDegrees);
            Assert.GreaterOrEqual(ChordLabel.NextDegrees, HudText.MinTextDegrees);
            Assert.GreaterOrEqual(MicMeter.TextDegrees, HudText.MinTextDegrees);
            Assert.GreaterOrEqual(SimulatedBadge.Degrees, HudText.MinTextDegrees);
        }

        [Test]
        public void SizeGrowsLinearlyWithDistance()
        {
            float near = HudText.SizeForAngularHeight(3f, 0.5f);
            float far = HudText.SizeForAngularHeight(3f, 2f);
            Assert.AreEqual(4f * near, far, 1e-5);
        }

        [TestCase(1.2f, 1.2f)]
        [TestCase(3f, 0.9f)]
        [TestCase(10f, 2f)]
        [TestCase(0.5f, 5f)]
        public void AngularSizeRoundTrips(float degrees, float distanceM)
        {
            float size = HudText.SizeForAngularHeight(degrees, distanceM);
            Assert.AreEqual(degrees, HudText.AngularHeightForSize(size, distanceM), 1e-3);
        }

        [Test]
        public void AngularHeightOf2p5cmAt1p2m_IsJustUnderMinimum()
        {
            Assert.AreEqual(1.19362, HudText.AngularHeightForSize(0.025f, 1.2f), 1e-3);
        }

        [TestCase(0f, 1f)]
        [TestCase(-1f, 1f)]
        [TestCase(3f, 0f)]
        [TestCase(3f, -2f)]
        [TestCase(float.NaN, 1f)]
        [TestCase(3f, float.NaN)]
        [TestCase(float.PositiveInfinity, 1f)]
        [TestCase(3f, float.PositiveInfinity)]
        public void SizeForBadInputIsZero(float degrees, float distanceM)
        {
            Assert.AreEqual(0f, HudText.SizeForAngularHeight(degrees, distanceM));
        }

        [Test]
        public void HugeAnglesStayFinite()
        {
            float size = HudText.SizeForAngularHeight(179.9f, 1f);
            Assert.IsFalse(float.IsNaN(size) || float.IsInfinity(size));
            Assert.Greater(size, 0f);
        }

        [Test]
        public void ClampCapDegrees_NeverGoesBelowTheFloors()
        {
            Assert.AreEqual(1.2f, HudText.ClampCapDegrees(0.5f, 0f), 1e-6);                       // global minimum
            Assert.AreEqual(1.2f, HudText.ClampCapDegrees(-3f, 0.3f), 1e-6);
            Assert.AreEqual(2f, HudText.ClampCapDegrees(2f, 1.2f), 1e-6);                          // already big enough
            Assert.AreEqual(3f, HudText.ClampCapDegrees(2f, HudText.ChordNameMinDegrees), 1e-6);   // chord name floor
            Assert.AreEqual(4f, HudText.ClampCapDegrees(4f, HudText.ChordNameMinDegrees), 1e-6);
            Assert.AreEqual(1.2f, HudText.ClampCapDegrees(float.NaN, 0f), 1e-6);
        }

        [Test]
        public void LargeTextOnlyMakesTextBigger()
        {
            Assert.AreEqual(1f, HudText.TextScale(false));
            Assert.AreEqual(HudText.LargeTextScale, HudText.TextScale(true));
            Assert.Greater(HudText.LargeTextScale, 1f);
            Assert.GreaterOrEqual(HudText.MinTextDegrees * HudText.TextScale(true), HudText.MinTextDegrees);
        }

        [TestCase(1.2f, 67)]
        [TestCase(1.8f, 101)]
        [TestCase(3f, 168)]
        [TestCase(4f, 223)]
        public void FontSizeMatchesGlassesPixels(float capDegrees, int expected)
        {
            Assert.AreEqual(expected, HudText.FontSizeForCapDegrees(capDegrees, HudText.DefaultPixelsPerDegree));
        }

        [Test]
        public void FontSizeIsClampedAndMonotonic()
        {
            Assert.AreEqual(HudText.MinFontSize, HudText.FontSizeForCapDegrees(0.01f, 40f));
            Assert.AreEqual(HudText.MinFontSize, HudText.FontSizeForCapDegrees(float.NaN, 40f));
            Assert.AreEqual(HudText.MaxFontSize, HudText.FontSizeForCapDegrees(100f, 40f));
            Assert.Less(HudText.FontSizeForCapDegrees(2f, 40f), HudText.FontSizeForCapDegrees(3f, 40f));
        }

        // ---------------------------------------------------------------- the size constant (no run-time measurement)

        [Test]
        public void CapConstant_IsTheCalibratedValue()
        {
            // Changing this number needs a new Editor/HudTextCalibration.cs run (build/previews/hudtext/hudtext-calibration.json:
            // "H" measured 0.071411 to 0.07159 units per font size, mean 0.071521, control quad 0.999), not a guess.
            Assert.AreEqual(0.07152f, HudText.CapUnitsPerFontSize, 1e-7);
            Assert.AreEqual(223 * 0.07152f, HudText.CapHeightUnits(223), 1e-5);
            // every per-size measurement is drawn within 0.2% with this one constant
            foreach (double measured in new[] { 0.071411, 0.071502, 0.07159, 0.071581 })
                Assert.AreEqual(1.0, measured / HudText.CapUnitsPerFontSize, 0.002);
        }

        // Expected values computed here from the definition, independently of HudText's own code path:
        // cap metres = 2 d tan(deg / 2); font size = round(deg * 40 / 0.716); scale = metres / (fontSize * constant * parent).
        [TestCase(1.2f, 1.0f, 67)]
        [TestCase(2f, 1.0f, 112)]
        [TestCase(3f, 1.2f, 168)]
        [TestCase(4f, 1.0f, 223)]
        [TestCase(4f, 1.2f, 223)]
        public void CapScale_IsWhatTheConstantSays(float degrees, float distanceM, int fontSize)
        {
            double meters = 2.0 * distanceM * System.Math.Tan(degrees * 0.5 * System.Math.PI / 180.0);
            Assert.AreEqual(fontSize, HudText.FontSizeForCapDegrees(degrees, HudText.DefaultPixelsPerDegree));
            float scale = HudText.ScaleForCap(HudText.SizeForAngularHeight(degrees, distanceM), fontSize, 1f);
            Assert.AreEqual(meters / (fontSize * (double)HudText.CapUnitsPerFontSize), scale, 1e-7);
            // and the drawn cap height (font units x constant x scale) is the asked one
            Assert.AreEqual(meters, fontSize * HudText.CapUnitsPerFontSize * scale, 1e-6);
        }

        [Test]
        public void CapScale_ParentScaleDividesOut()
        {
            float one = HudText.ScaleForCap(0.05f, 168, 1f);
            Assert.AreEqual(one / 2f, HudText.ScaleForCap(0.05f, 168, 2f), 1e-7);
            Assert.AreEqual(one * 4f, HudText.ScaleForCap(0.05f, 168, 0.25f), 1e-6);
        }

        [Test]
        public void CapScale_IsTheSameOnEveryCall()
        {
            // The old code measured Renderer.localBounds and changed its answer once the text had been drawn; this is a pure function.
            float first = HudText.ScaleForCap(0.0698408f, 223, 1f);
            for (int i = 0; i < 5; i++) Assert.AreEqual(first, HudText.ScaleForCap(0.0698408f, 223, 1f));
        }

        [TestCase(0f, 223, 1f)]
        [TestCase(-0.05f, 223, 1f)]
        [TestCase(float.NaN, 223, 1f)]
        [TestCase(float.PositiveInfinity, 223, 1f)]
        [TestCase(0.05f, 0, 1f)]
        [TestCase(0.05f, -5, 1f)]
        [TestCase(0.05f, 223, 0f)]
        [TestCase(0.05f, 223, float.NaN)]
        public void CapScale_BadInputIsZero(float capMeters, int fontSize, float parentScale)
        {
            Assert.AreEqual(0f, HudText.ScaleForCap(capMeters, fontSize, parentScale));
        }

        [Test]
        public void HudText_SizeIsSetAtOnce_WithNoFrame()
        {
            // Needs the engine (GameObject, TextMesh): run in Unity's EditMode tests. No frame is rendered or advanced here, so a
            // size that depended on a measurement after drawing would fail this.
            var parent = new GameObject("HudTextTestParent");
            try
            {
                parent.transform.localScale = new Vector3(2f, 2f, 2f);
                HudText text = HudText.Create(parent.transform, "HudTextTest");
                text.SetAngularHeight(4f, 1f);
                float meters = HudText.SizeForAngularHeight(4f, 1f);
                float expected = HudText.ScaleForCap(meters, HudText.FontSizeForCapDegrees(4f, HudText.DefaultPixelsPerDegree), 2f);
                Assert.Greater(expected, 0f);
                Assert.AreEqual(expected, text.transform.localScale.y, 1e-7);
                Assert.AreEqual(meters, text.CapHeightMeters, 1e-7);
                Assert.IsTrue(text.SizeMeasured);

                text.SetText("G");                                   // new words: the size must not move
                Assert.AreEqual(expected, text.transform.localScale.y, 1e-7);
                text.SetAngularHeight(4f, 1f);                       // the same request again: the same size
                Assert.AreEqual(expected, text.transform.localScale.y, 1e-7);
                text.SetAngularHeight(0.5f, 1f);                     // below the minimum: raised to 1.2 degrees
                Assert.AreEqual(HudText.SizeForAngularHeight(HudText.MinTextDegrees, 1f), text.CapHeightMeters, 1e-7);
            }
            finally
            {
                Object.DestroyImmediate(parent);
            }
        }

        // ---------------------------------------------------------------- change detection ("rebuild only when the words change")

        [TestCase("G", "G", false)]
        [TestCase("G", "C", true)]
        [TestCase("", "", false)]
        [TestCase(null, null, false)]
        [TestCase(null, "", false)]
        [TestCase("", null, false)]
        [TestCase(null, "G", true)]
        [TestCase("G", null, true)]
        [TestCase("g", "G", true)]
        [TestCase("G", "G ", true)]
        [TestCase("Em", "Em", false)]
        public void NeedsRebuild_OnlyWhenTheWordsDiffer(string current, string next, bool expected)
        {
            Assert.AreEqual(expected, HudText.NeedsRebuild(current, next));
        }

        [Test]
        public void NeedsRebuild_IsFalseForEqualButDistinctStringInstances()
        {
            string a = new string(new[] { 'A', 'm' });
            string b = new string(new[] { 'A', 'm' });
            Assert.IsFalse(ReferenceEqualsHelper(a, b), "test setup: these must be two different instances");
            Assert.IsFalse(HudText.NeedsRebuild(a, b));
        }

        static bool ReferenceEqualsHelper(string a, string b)
        {
            return ReferenceEquals(a, b);
        }

        // ---------------------------------------------------------------- chord label

        [Test]
        public void FormatNext_AddsThePrefixOnlyWhenThereIsAChord()
        {
            Assert.AreEqual("next  C", ChordLabel.FormatNext("C"));
            Assert.AreEqual("next  Em", ChordLabel.FormatNext("Em"));
            Assert.AreEqual(string.Empty, ChordLabel.FormatNext(""));
            Assert.AreEqual(string.Empty, ChordLabel.FormatNext(null));
        }

        [Test]
        public void NextOffset_GrowsWithBothSizes()
        {
            Assert.AreEqual(0.061f, ChordLabel.NextOffsetMeters(0.05f, 0.02f), 1e-5);
            Assert.Less(ChordLabel.NextOffsetMeters(0.05f, 0.02f), ChordLabel.NextOffsetMeters(0.06f, 0.02f));
            Assert.Less(ChordLabel.NextOffsetMeters(0.05f, 0.02f), ChordLabel.NextOffsetMeters(0.05f, 0.03f));
        }

        [Test]
        public void NextBeside_IsWhereTheDefinitionPutsIt()
        {
            // 4 and 1.8 degree caps at 1 m: 0.0698408 and 0.0314191 m (2 tan(deg / 2)); computed here, not by the label's code.
            Vector2 o = ChordLabel.NextLocalOffset(0.0698408f, 0.0314191f);
            Assert.AreEqual((2.26 / 2 + 0.25) * 0.0698408, o.x, 1e-6);        // 0.0963803 m: right of the widest name
            Assert.AreEqual(-0.485 * (0.0698408 - 0.0314191), o.y, 1e-6);    // -0.0186345 m: baselines on one line
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NextBeside_ClearsTheWidestNameAndSharesItsBaseline(bool largeText)
        {
            float s = HudText.TextScale(largeText);
            float c = HudText.SizeForAngularHeight(ChordLabel.CurrentDegrees * s, 1f);
            float n = HudText.SizeForAngularHeight(ChordLabel.NextDegrees * s, 1f);
            Vector2 off = ChordLabel.NextLocalOffset(c, n);
            float nameRight = 0.5f * ChordLabel.WidestNameCaps * c;              // the current chord is centred on the origin
            Assert.GreaterOrEqual(off.x - nameRight, 0.2f * c, "clear space between the widest name and \"next\"");
            float currentBaseline = -ChordLabel.AnchorAboveBaselineCaps * c;
            float nextBaseline = off.y - ChordLabel.AnchorAboveBaselineCaps * n;
            Assert.AreEqual(currentBaseline, nextBaseline, 1e-6);
        }

        [Test]
        public void WidestNameCaps_CoversTheChordNamesWeShow()
        {
            // Arial Bold advance widths in font units ('hmtx'), cap height 1466: A 1479, D 1479, E 1366, G 1593, m 1821.
            Assert.GreaterOrEqual(ChordLabel.WidestNameCaps, (1479 + 1821) / 1466f);   // "Am", "Dm": 2.251
            Assert.GreaterOrEqual(ChordLabel.WidestNameCaps, (1366 + 1821) / 1466f);   // "Em": 2.174
            Assert.GreaterOrEqual(ChordLabel.WidestNameCaps, 1593 / 1466f);            // "G": 1.087
        }

        [Test]
        public void ChordLabel_PutsNextBesideTheName_FromTheFirstFrame()
        {
            // Needs the engine (GameObject, TextMesh): run in Unity's EditMode tests.
            var root = new GameObject("ChordLabelTest");
            try
            {
                ChordLabel label = ChordLabel.Create(root.transform, 1f);
                label.SetChords("G", "C");
                Transform next = label.transform.Find("NextChord");
                Assert.IsNotNull(next);
                float s = HudText.TextScale(UntitledGuitarApp.Runtime.Flags.FlagStore.Current.largeText);
                Vector2 o = ChordLabel.NextLocalOffset(HudText.SizeForAngularHeight(ChordLabel.CurrentDegrees * s, 1f),
                                                       HudText.SizeForAngularHeight(ChordLabel.NextDegrees * s, 1f));
                Assert.AreEqual(o.x, next.localPosition.x, 1e-6);
                Assert.AreEqual(o.y, next.localPosition.y, 1e-6);
                Assert.AreEqual(TextAnchor.MiddleLeft, next.GetComponent<TextMesh>().anchor);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // ---------------------------------------------------------------- mic meter: dB to bar

        [Test]
        public void LinearToDb_KnownLevels()
        {
            Assert.AreEqual(0.0, MicMeter.LinearToDb(1f), 1e-6);
            Assert.AreEqual(-6.0206, MicMeter.LinearToDb(0.5f), 1e-3);
            Assert.AreEqual(-20.0, MicMeter.LinearToDb(0.1f), 1e-3);
            Assert.AreEqual(-40.0, MicMeter.LinearToDb(0.01f), 1e-3);
            Assert.AreEqual(6.0206, MicMeter.LinearToDb(2f), 1e-3);   // over full scale is a positive dB, not clamped here
        }

        [Test]
        public void LinearToDb_SilenceIsAFiniteFloor()
        {
            Assert.AreEqual(-120f, MicMeter.LinearToDb(0f));
            Assert.AreEqual(-120f, MicMeter.LinearToDb(-0.5f));
            Assert.AreEqual(-120f, MicMeter.LinearToDb(float.NaN));
            Assert.AreEqual(-120f, MicMeter.LinearToDb(1e-9f));
        }

        [Test]
        public void DbToBar_SilenceMidRangeAndClipping()
        {
            Assert.AreEqual(0f, MicMeter.DbToBar(-60f), 1e-6);    // empty at the floor
            Assert.AreEqual(0f, MicMeter.DbToBar(-120f), 1e-6);   // silence
            Assert.AreEqual(0.25f, MicMeter.DbToBar(-45f), 1e-6);
            Assert.AreEqual(0.5f, MicMeter.DbToBar(-30f), 1e-6);  // mid-range
            Assert.AreEqual(0.75f, MicMeter.DbToBar(-15f), 1e-6);
            Assert.AreEqual(1f, MicMeter.DbToBar(0f), 1e-6);      // full scale
            Assert.AreEqual(1f, MicMeter.DbToBar(6f), 1e-6);      // clipping: clamped, never past full
        }

        [Test]
        public void DbToBar_HandlesNonFiniteInput()
        {
            Assert.AreEqual(0f, MicMeter.DbToBar(float.NaN));
            Assert.AreEqual(0f, MicMeter.DbToBar(float.NegativeInfinity));
            Assert.AreEqual(1f, MicMeter.DbToBar(float.PositiveInfinity));
        }

        [Test]
        public void LevelToBar_FromLinearAmplitude()
        {
            Assert.AreEqual(0f, MicMeter.LevelToBar(0f), 1e-6);        // silence
            Assert.AreEqual(1f, MicMeter.LevelToBar(1f), 1e-6);        // full scale
            Assert.AreEqual(1f, MicMeter.LevelToBar(1.5f), 1e-6);      // clipping
            Assert.AreEqual(0.5f, MicMeter.LevelToBar(0.0316228f), 1e-3);   // -30 dBFS
            Assert.AreEqual(0f, MicMeter.LevelToBar(0.001f), 1e-3);    // -60 dBFS
        }

        [Test]
        public void LevelToBar_IsMonotonic()
        {
            float previous = -1f;
            for (float level = 0f; level <= 1.2f; level += 0.01f)
            {
                float bar = MicMeter.LevelToBar(level);
                Assert.GreaterOrEqual(bar, previous);
                previous = bar;
            }
        }

        [Test]
        public void LitSegments_RoundsToTheNearestSegmentAndClamps()
        {
            Assert.AreEqual(0, MicMeter.LitSegments(0f, 16));
            Assert.AreEqual(16, MicMeter.LitSegments(1f, 16));
            Assert.AreEqual(8, MicMeter.LitSegments(0.5f, 16));
            Assert.AreEqual(0, MicMeter.LitSegments(0.03f, 16));
            Assert.AreEqual(1, MicMeter.LitSegments(0.04f, 16));
            Assert.AreEqual(0, MicMeter.LitSegments(-1f, 16));
            Assert.AreEqual(16, MicMeter.LitSegments(2f, 16));
            Assert.AreEqual(0, MicMeter.LitSegments(float.NaN, 16));
            Assert.AreEqual(0, MicMeter.LitSegments(0.5f, 0));
        }

        [Test]
        public void SilenceLightsNothingAndClippingLightsEverything()
        {
            Assert.AreEqual(0, MicMeter.LitSegments(MicMeter.LevelToBar(0f), MicMeter.Segments));
            Assert.AreEqual(MicMeter.Segments, MicMeter.LitSegments(MicMeter.LevelToBar(1.2f), MicMeter.Segments));
            Assert.AreEqual(MicMeter.Segments / 2, MicMeter.LitSegments(MicMeter.LevelToBar(0.0316228f), MicMeter.Segments));
        }

        [TestCase(0.5f, false)]
        [TestCase(0.98f, false)]
        [TestCase(0.99f, true)]
        [TestCase(1f, true)]
        [TestCase(1.4f, true)]
        [TestCase(float.NaN, false)]
        public void IsClipping_AtMinusPoint1dBFS(float peak, bool expected)
        {
            Assert.AreEqual(expected, MicMeter.IsClipping(peak));
        }

        // ---------------------------------------------------------------- mic meter: sample rate, label, strings, smoothing

        [TestCase(48000, "48 kHz")]
        [TestCase(44100, "44.1 kHz")]
        [TestCase(22050, "22.05 kHz")]
        [TestCase(16000, "16 kHz")]
        [TestCase(0, "-- kHz")]
        [TestCase(-5, "-- kHz")]
        public void FormatSampleRate(int hz, string expected)
        {
            Assert.AreEqual(expected, MicMeter.FormatSampleRate(hz));
        }

        [Test]
        public void FormatSampleRate_IgnoresTheCurrentCulture()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.AreEqual("44.1 kHz", MicMeter.FormatSampleRate(44100));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Test]
        public void LabelText_ShowsTheRateAndOnlyTheWarningThatApplies()
        {
            Assert.AreEqual("MIC 48 kHz", MicMeter.LabelText(48000, false, false));
            StringAssert.Contains("MIC 48 kHz", MicMeter.LabelText(48000, true, false));
            StringAssert.Contains("CLIP", MicMeter.LabelText(48000, true, false));
            StringAssert.Contains("NO DATA", MicMeter.LabelText(48000, false, true));
            StringAssert.DoesNotContain("CLIP", MicMeter.LabelText(48000, true, true));   // no data wins: the peak is stale
            StringAssert.DoesNotContain("NO DATA", MicMeter.LabelText(48000, true, false));
            Assert.AreEqual("MIC -- kHz", MicMeter.LabelText(0, false, false));
        }

        [Test]
        public void BarMarkup_AlwaysHasExactlyOneLetterPerSegmentAndBalancedTags()
        {
            for (int lit = 0; lit <= MicMeter.Segments; lit++)
            {
                for (int peak = 0; peak <= MicMeter.Segments; peak++)
                {
                    string markup = MicMeter.BarMarkup(lit, peak);
                    Assert.AreEqual(MicMeter.Segments, Count(markup, "I"), "lit " + lit + " peak " + peak);
                    Assert.AreEqual(Count(markup, "<color="), Count(markup, "</color>"), "lit " + lit + " peak " + peak);
                }
            }
        }

        [Test]
        public void BarMarkup_ColourRunsFollowTheZones()
        {
            Assert.AreEqual(1, Count(MicMeter.BarMarkup(0, 0), "<color="));     // all dim
            Assert.AreEqual(2, Count(MicMeter.BarMarkup(4, 4), "<color="));     // green, dim
            Assert.AreEqual(4, Count(MicMeter.BarMarkup(4, 8), "<color="));     // green, dim, peak tick, dim
            Assert.AreEqual(2, Count(MicMeter.BarMarkup(0, 16), "<color="));    // dim, peak tick at the very end
            Assert.AreEqual(2, Count(MicMeter.BarMarkup(12, 12), "<color="));   // green up to -15 dBFS, then dim
            Assert.AreEqual(3, Count(MicMeter.BarMarkup(13, 13), "<color="));   // green, amber, dim
            Assert.AreEqual(3, Count(MicMeter.BarMarkup(16, 16), "<color="));   // green, amber, red-orange
        }

        [Test]
        public void BarMarkup_ClampsOutOfRangeArguments()
        {
            Assert.AreEqual(MicMeter.BarMarkup(0, 0), MicMeter.BarMarkup(-5, -5));
            Assert.AreEqual(MicMeter.BarMarkup(16, 16), MicMeter.BarMarkup(99, 99));
            Assert.AreEqual(MicMeter.BarMarkup(8, 8), MicMeter.BarMarkup(8, 3));   // a peak below the level is drawn as no peak tick
        }

        [Test]
        public void BarMarkup_IsCached_SoUpdatingTheBarAllocatesNothing()
        {
            MicMeter.Prewarm();
            Assert.AreSame(MicMeter.BarMarkup(5, 9), MicMeter.BarMarkup(5, 9));
        }

        [Test]
        public void DbText_IsRoundedClampedAndCached()
        {
            Assert.AreEqual("0 dBFS", MicMeter.DbText(0f));
            Assert.AreEqual("-23 dBFS", MicMeter.DbText(-23.4f));
            Assert.AreEqual("-24 dBFS", MicMeter.DbText(-23.6f));
            Assert.AreEqual("-90 dBFS", MicMeter.DbText(-200f));
            Assert.AreEqual("0 dBFS", MicMeter.DbText(5f));
            Assert.AreEqual("-90 dBFS", MicMeter.DbText(float.NaN));
            Assert.AreSame(MicMeter.DbText(-12.2f), MicMeter.DbText(-11.8f));
        }

        [Test]
        public void Smooth_RisesFastFallsSlowAndNeverOvershoots()
        {
            Assert.AreEqual(1f - Mathf.Exp(-1f), MicMeter.Smooth(0f, 1f, 0.03f, 0.03f, 0.3f), 1e-5);     // attack: one time constant
            Assert.AreEqual(1f - (1f - Mathf.Exp(-0.1f)), MicMeter.Smooth(1f, 0f, 0.03f, 0.03f, 0.3f), 1e-5);  // release: a tenth of one
            Assert.Greater(MicMeter.Smooth(0f, 1f, 0.03f, 0.03f, 0.3f), 1f - MicMeter.Smooth(1f, 0f, 0.03f, 0.03f, 0.3f));
            Assert.AreEqual(0.4f, MicMeter.Smooth(0.4f, 1f, 0f, 0.03f, 0.3f));       // no time passed
            Assert.AreEqual(1f, MicMeter.Smooth(0f, 1f, 0.01f, 0f, 0.3f));           // zero attack jumps
            Assert.LessOrEqual(MicMeter.Smooth(0f, 1f, 100f, 0.03f, 0.3f), 1f);      // huge step: no overshoot
            Assert.GreaterOrEqual(MicMeter.Smooth(1f, 0f, 100f, 0.03f, 0.3f), 0f);
        }

        // ---------------------------------------------------------------- simulated-input badge (honesty rule)

        [TestCase("wav", true)]
        [TestCase("WAV", true)]
        [TestCase("Wav", true)]
        [TestCase("  wav  ", true)]
        [TestCase("mic", false)]
        [TestCase("MIC", false)]
        [TestCase(" mic ", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        [TestCase("   ", false)]
        [TestCase("wave", true)]      // unknown values disclose rather than hide
        [TestCase("file", true)]
        public void Badge_IsShownForWavAndAnythingUnrecognised(string audioSource, bool expected)
        {
            Assert.AreEqual(expected, SimulatedBadge.ShouldShow(audioSource));
        }

        [Test]
        public void Badge_VisibilityDependsOnTheAudioSourceAlone()
        {
            // The honesty rule: no other flag may hide the badge. ShouldShow takes exactly one string, and the badge has no
            // public way to change anything (its only declared public instance members are read-only property getters).
            MethodInfo shouldShow = typeof(SimulatedBadge).GetMethod("ShouldShow", BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(shouldShow);
            ParameterInfo[] parameters = shouldShow.GetParameters();
            Assert.AreEqual(1, parameters.Length);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);

            MethodInfo[] instanceMethods = typeof(SimulatedBadge).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (MethodInfo method in instanceMethods)
                StringAssert.StartsWith("get_", method.Name, "SimulatedBadge must not expose a way to hide or change it: " + method.Name);
        }

        [Test]
        public void Badge_TextIsTheProjectsWording()
        {
            Assert.AreEqual("SIMULATED INPUT", SimulatedBadge.BadgeText);
        }

        // ---------------------------------------------------------------- hud layout by view angle

        [Test]
        public void PointAtAngles_StraightAheadAndAxes()
        {
            AssertVector(0f, 0f, 1f, HudLayout.PointAtAngles(0f, 0f, 1f));
            AssertVector(2f, 0f, 0f, HudLayout.PointAtAngles(90f, 0f, 2f));     // 90 degrees right
            AssertVector(-0.5f, 0f, 0.8660254f, HudLayout.PointAtAngles(-30f, 0f, 1f));
            AssertVector(0f, 0.5f, 0.8660254f, HudLayout.PointAtAngles(0f, 30f, 1f));   // 30 degrees up
            AssertVector(0f, -3f, 0f, HudLayout.PointAtAngles(0f, -90f, 3f));
        }

        [Test]
        public void PointAtAngles_KeepsTheDistance()
        {
            Vector3 p = HudLayout.PointAtAngles(17f, -23f, 1.7f);
            Assert.AreEqual(1.7f, p.magnitude, 1e-4);
        }

        static void AssertVector(float x, float y, float z, Vector3 actual)
        {
            Assert.AreEqual(x, actual.x, 1e-4);
            Assert.AreEqual(y, actual.y, 1e-4);
            Assert.AreEqual(z, actual.z, 1e-4);
        }

        // ---------------------------------------------------------------- spectator: field of view and framing

        [Test]
        public void GlassesFieldOfView_52DiagonalAt16by9_Is46By27()
        {
            // docs/MASTER_PLAN.md section 17: "the view is only about 46 x 27 degrees (52 degree diagonal)".
            const float aspect = 16f / 9f;
            Assert.AreEqual(26.8957, SpectatorLayout.VerticalFovFromDiagonal(52f, aspect), 1e-2);
            Assert.AreEqual(46.0603, SpectatorLayout.HorizontalFovFromDiagonal(52f, aspect), 1e-2);
        }

        [Test]
        public void FieldOfView_SquareImageHasEqualAxes()
        {
            Assert.AreEqual(70.5288, SpectatorLayout.VerticalFovFromDiagonal(90f, 1f), 1e-2);
            Assert.AreEqual(70.5288, SpectatorLayout.HorizontalFovFromDiagonal(90f, 1f), 1e-2);
        }

        [Test]
        public void FieldOfView_BadInputGivesZeroOrTheDefaultAspect()
        {
            Assert.AreEqual(0f, SpectatorLayout.VerticalFovFromDiagonal(0f, 16f / 9f));
            Assert.AreEqual(0f, SpectatorLayout.VerticalFovFromDiagonal(179.5f, 16f / 9f));
            Assert.AreEqual(0f, SpectatorLayout.VerticalFovFromDiagonal(float.NaN, 16f / 9f));
            Assert.AreEqual(SpectatorLayout.VerticalFovFromDiagonal(52f, 16f / 9f), SpectatorLayout.VerticalFovFromDiagonal(52f, 0f), 1e-4);
        }

        [Test]
        public void FrameViewportRect_LetterboxesTheGlassesFrameAboveThePanel()
        {
            // 1512x982 MacBook-like screen: a 16:9 frame fits by height above the 30% panel, centred with side bars.
            Rect r = SpectatorLayout.FrameViewportRect(1512, 982, 16f / 9f, 0.30f);
            Assert.AreEqual(0.095885, r.x, 1e-4);
            Assert.AreEqual(0.3, r.y, 1e-4);
            Assert.AreEqual(0.808230, r.width, 1e-4);
            Assert.AreEqual(0.7, r.height, 1e-4);

            Rect hd = SpectatorLayout.FrameViewportRect(1920, 1080, 16f / 9f, 0.30f);
            Assert.AreEqual(0.15, hd.x, 1e-4);
            Assert.AreEqual(0.3, hd.y, 1e-4);
            Assert.AreEqual(0.7, hd.width, 1e-4);
            Assert.AreEqual(0.7, hd.height, 1e-4);
        }

        [Test]
        public void FrameViewportRect_WithNoPanelOnA16by9ScreenIsFullScreen()
        {
            Rect r = SpectatorLayout.FrameViewportRect(1920, 1080, 16f / 9f, 0f);
            Assert.AreEqual(0f, r.x, 1e-5);
            Assert.AreEqual(0f, r.y, 1e-5);
            Assert.AreEqual(1f, r.width, 1e-5);
            Assert.AreEqual(1f, r.height, 1e-5);
        }

        [Test]
        public void FrameViewportRect_TallScreenIsLetterboxedVertically()
        {
            Rect r = SpectatorLayout.FrameViewportRect(1000, 1000, 16f / 9f, 0.30f);
            Assert.AreEqual(0f, r.x, 1e-5);
            Assert.AreEqual(0.36875, r.y, 1e-5);
            Assert.AreEqual(1f, r.width, 1e-5);
            Assert.AreEqual(0.5625, r.height, 1e-5);
        }

        [Test]
        public void Frame_ShapeIsAlways16by9_StaysOnScreen_AndClearsThePanel()
        {
            int[] widths = { 800, 1280, 1512, 1920, 2560, 3024, 3440 };
            int[] heights = { 600, 720, 982, 1080, 1440, 1964 };
            const float aspect = 16f / 9f;
            foreach (int w in widths)
            {
                foreach (int h in heights)
                {
                    Rect v = SpectatorLayout.FrameViewportRect(w, h, aspect, SpectatorLayout.PanelFraction);
                    Assert.GreaterOrEqual(v.x, -1e-5f, w + "x" + h);
                    Assert.GreaterOrEqual(v.y, SpectatorLayout.PanelFraction - 1e-4f, w + "x" + h);   // never under the panel
                    Assert.LessOrEqual(v.xMax, 1f + 1e-5f, w + "x" + h);
                    Assert.LessOrEqual(v.yMax, 1f + 1e-5f, w + "x" + h);
                    Assert.AreEqual(aspect, (v.width * w) / (v.height * h), 1e-3, w + "x" + h);       // exactly the glasses' shape

                    Rect px = SpectatorLayout.FramePixelRect(w, h, aspect, SpectatorLayout.PanelFraction);
                    Assert.AreEqual(v.width * w, px.width, 1e-2, w + "x" + h);
                    Assert.AreEqual(v.height * h, px.height, 1e-2, w + "x" + h);
                    Assert.LessOrEqual(px.yMax, h * (1f - SpectatorLayout.PanelFraction) + 1e-2f, w + "x" + h);
                }
            }
        }

        [Test]
        public void Frame_DegenerateScreenFallsBackToFullViewport()
        {
            Rect r = SpectatorLayout.FrameViewportRect(0, 0, 16f / 9f, 0.3f);
            Assert.AreEqual(1f, r.width);
            Assert.AreEqual(1f, r.height);
        }

        // ---------------------------------------------------------------- spectator: panel sizes, fitting, guards

        [Test]
        public void PanelSizesFollowTheScreenHeight()
        {
            Assert.AreEqual(324, SpectatorLayout.PanelHeightPx(1080));
            Assert.AreEqual(130, SpectatorLayout.Line1FontPx(324));
            Assert.AreEqual(97, SpectatorLayout.Line2FontPx(324));
            Assert.AreEqual(32, SpectatorLayout.CaptionFontPx(1080));
            Assert.AreEqual(14, SpectatorLayout.CaptionFontPx(100));
            Assert.AreEqual(12, SpectatorLayout.Line1FontPx(10));
            Assert.AreEqual(0, SpectatorLayout.PanelHeightPx(0));
            Assert.Greater(SpectatorLayout.Line1FontPx(324), SpectatorLayout.Line2FontPx(324));
        }

        [Test]
        public void FitFontSize_ShrinksOnlyWhatDoesNotFit()
        {
            Assert.AreEqual(100, SpectatorLayout.FitFontSize(100, 900f, 1000f, 12));    // fits
            Assert.AreEqual(100, SpectatorLayout.FitFontSize(100, 1000f, 1000f, 12));   // exactly fits
            Assert.AreEqual(48, SpectatorLayout.FitFontSize(100, 2000f, 1000f, 12));    // half the width available: 100 * 0.5 * 0.97
            Assert.AreEqual(12, SpectatorLayout.FitFontSize(100, 90000f, 1000f, 12));   // never below the minimum
            Assert.AreEqual(100, SpectatorLayout.FitFontSize(100, 0f, 1000f, 12));      // nothing measured
            Assert.AreEqual(100, SpectatorLayout.FitFontSize(100, 2000f, 0f, 12));      // no room information
            Assert.AreEqual(10, SpectatorLayout.FitFontSize(10, 5000f, 100f, 12));      // base already below the minimum: leave it
        }

        [Test]
        public void IsSbsGlassesSurface_MatchesOnlyTheTwoEyeTarget()
        {
            Assert.IsTrue(SpectatorLayout.IsSbsGlassesSurface(3840, 1080, 1920, 1080));
            Assert.IsFalse(SpectatorLayout.IsSbsGlassesSurface(1920, 1080, 1920, 1080));   // a 1080p Mac screen is fine
            Assert.IsFalse(SpectatorLayout.IsSbsGlassesSurface(1512, 982, 1920, 1080));
            Assert.IsFalse(SpectatorLayout.IsSbsGlassesSurface(3840, 2160, 1920, 1080));
            Assert.IsFalse(SpectatorLayout.IsSbsGlassesSurface(3840, 1080, 0, 0));
        }

        [TestCase("off", true)]
        [TestCase("OFF", true)]
        [TestCase(" off ", true)]
        [TestCase("none", true)]
        [TestCase("false", true)]
        [TestCase("disabled", true)]
        [TestCase("0", true)]
        [TestCase("hostScreen", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        [TestCase("somethingNew", false)]
        public void SpectatorFlag_OnlyExplicitOffValuesSwitchItOff(string flag, bool expectedOff)
        {
            Assert.AreEqual(expectedOff, SpectatorLayout.IsOffValue(flag));
        }

        [Test]
        public void Spectator_CaptionIsTheDocumentedWording_AndTheMacScreenIsDisplayZero()
        {
            Assert.AreEqual("glasses view (virtual layer)", SpectatorCam.CaptionText);
            Assert.AreEqual(0, SpectatorCam.SpectatorDisplayIndex);
        }

        // ---------------------------------------------------------------- helpers

        static int Count(string haystack, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = haystack.IndexOf(needle, index, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }
    }
}
