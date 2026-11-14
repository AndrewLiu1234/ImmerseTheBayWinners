using NUnit.Framework;
using UntitledGuitarApp.Core.Theory;
using UntitledGuitarApp.Runtime.Neck;
using UntitledGuitarApp.Runtime.Neck.Dev;
// Core.Theory has its own FingertipDot (Core's NeckPoint-based one); the neck's is meant here.
using FingertipDot = UntitledGuitarApp.Runtime.Neck.FingertipDot;

namespace UntitledGuitarApp.Tests.EditMode
{
    public class CoreNeckLayoutTests
    {
        static FretboardGeometry Geo { get { return CoreNeckLayout.DefaultGeometry; } }

        [Test]
        public void Layout_FretsAreCoresFretDistances()
        {
            NeckLayout layout = CoreNeckLayout.Create(Geo);
            Assert.AreEqual(CoreNeckLayout.LastFret + 1, layout.fretXMm.Length);
            for (int n = 0; n <= CoreNeckLayout.LastFret; n++)
                Assert.AreEqual(Geo.FretDistanceMm(n), layout.fretXMm[n], 1e-3, "fret " + n);
            Assert.AreEqual(648f, layout.scaleLengthMm, 1e-3);
        }

        [Test]
        public void Layout_String1IsOnPlusZ_String6OnMinusZ()
        {
            // Core: across is + toward string 6. The neck frame: +z toward string 1. So string 1 must come out positive.
            NeckLayout layout = CoreNeckLayout.Create(Geo);
            Assert.AreEqual(+17.5f, layout.stringZAtNutMm[0], 1e-3, "string 1 at the nut");
            Assert.AreEqual(-17.5f, layout.stringZAtNutMm[5], 1e-3, "string 6 at the nut");
            Assert.AreEqual(+26.75f, layout.stringZAtSaddleMm[0], 1e-3, "string 1 at the saddle (53.5 mm span)");
            for (int i = 1; i < NeckLayout.StringCount; i++)
                Assert.Less(layout.stringZAtNutMm[i], layout.stringZAtNutMm[i - 1], "z falls from string 1 to string 6");
        }

        [Test]
        public void Layout_FretboardIsWiderThanTheStrings()
        {
            NeckLayout layout = CoreNeckLayout.Create(Geo);
            Assert.AreEqual(35f + CoreNeckLayout.FretboardMarginMm, layout.fretboardWidthAtNutMm, 1e-3);
            Assert.Greater(layout.fretboardWidthAtLastFretMm, layout.fretboardWidthAtNutMm);
        }

        [Test]
        public void G_HasThreeDots_AndOpenMarksOn432()
        {
            var dots = new FingertipDot[6];
            var marks = new StringMark[6];
            int n = CoreNeckLayout.ChordMarks(Geo, "G", dots, marks);
            Assert.AreEqual(3, n);
            // string 6 first: (6, fret 3, finger 2), (5, fret 2, finger 1), (1, fret 3, finger 3)
            Assert.AreEqual(6, dots[0].stringNumber); Assert.AreEqual(2, dots[0].finger);
            Assert.AreEqual(5, dots[1].stringNumber); Assert.AreEqual(1, dots[1].finger);
            Assert.AreEqual(1, dots[2].stringNumber); Assert.AreEqual(3, dots[2].finger);
            Assert.AreEqual(Geo.FingertipAlongMm(3), dots[0].xMm, 1e-3);
            Assert.AreEqual(Geo.FingertipAlongMm(2), dots[1].xMm, 1e-3);
            Assert.Less(dots[0].zMm, 0f, "string 6 is on -z");
            Assert.Greater(dots[2].zMm, 0f, "string 1 is on +z");
            Assert.AreEqual(StringMark.None, marks[0]);
            Assert.AreEqual(StringMark.Open, marks[1]);
            Assert.AreEqual(StringMark.Open, marks[2]);
            Assert.AreEqual(StringMark.Open, marks[3]);
            Assert.AreEqual(StringMark.None, marks[4]);
            Assert.AreEqual(StringMark.None, marks[5]);
        }

        [Test]
        public void G_MatchesTheOldDevLayoutWithinATenthOfAMillimetre()
        {
            // The ghost-hand poses were authored on DevNeckLayout (saddle span 53 mm; Core says 53.5): the dots must not move.
            var core = new FingertipDot[6];
            var dev = new FingertipDot[6];
            var marks = new StringMark[6];
            int nc = CoreNeckLayout.ChordMarks(Geo, "G", core, marks);
            int nd = DevNeckLayout.GChord(DevNeckLayout.Create(), dev, new StringMark[6]);
            Assert.AreEqual(nd, nc);
            for (int i = 0; i < nc; i++)
            {
                Assert.AreEqual(dev[i].stringNumber, core[i].stringNumber);
                Assert.AreEqual(dev[i].finger, core[i].finger);
                Assert.AreEqual(dev[i].xMm, core[i].xMm, 0.1f, "x of dot " + i);
                Assert.AreEqual(dev[i].zMm, core[i].zMm, 0.1f, "z of dot " + i);
            }
        }

        [TestCase("C", 3, new[] { StringMark.Open, StringMark.None, StringMark.Open, StringMark.None, StringMark.None, StringMark.Muted })]
        [TestCase("D", 3, new[] { StringMark.None, StringMark.None, StringMark.None, StringMark.Open, StringMark.Muted, StringMark.Muted })]
        [TestCase("Em", 2, new[] { StringMark.Open, StringMark.Open, StringMark.Open, StringMark.None, StringMark.None, StringMark.Open })]
        [TestCase("Am", 3, new[] { StringMark.Open, StringMark.None, StringMark.None, StringMark.None, StringMark.Open, StringMark.Muted })]
        public void MustChords_DotCountsAndMarks(string chord, int expectedDots, StringMark[] expectedMarksString1To6)
        {
            var dots = new FingertipDot[6];
            var marks = new StringMark[6];
            Assert.AreEqual(expectedDots, CoreNeckLayout.ChordMarks(Geo, chord, dots, marks));
            CollectionAssert.AreEqual(expectedMarksString1To6, marks);
            for (int i = 0; i < expectedDots; i++)
            {
                Assert.That(dots[i].finger, Is.InRange(1, 4), chord + " dot " + i + " has a finger");
                Assert.LessOrEqual(dots[i].xMm, Geo.FretDistanceMm(CoreNeckLayout.LastFret), chord + " dot " + i + " is on the drawn frets");
            }
        }

        [Test]
        public void UnknownChord_ReturnsMinusOne_AndLeavesTheBuffersAlone()
        {
            var dots = new FingertipDot[6];
            var marks = new StringMark[6];
            marks[0] = StringMark.Muted;
            Assert.AreEqual(-1, CoreNeckLayout.ChordMarks(Geo, "H7", dots, marks));
            Assert.AreEqual(-1, CoreNeckLayout.ChordMarks(Geo, (string)null, dots, marks));
            Assert.AreEqual(StringMark.Muted, marks[0], "untouched");
        }

        [Test]
        public void SmallDotBuffer_IsFilledWithoutOverflow()
        {
            var dots = new FingertipDot[2];
            var marks = new StringMark[6];
            Assert.AreEqual(2, CoreNeckLayout.ChordMarks(Geo, "G", dots, marks), "two of G's three dots fit");
            Assert.AreEqual(StringMark.Open, marks[1], "marks are still complete");
        }

        [Test]
        public void ZMm_FlipsTheSign()
        {
            Assert.AreEqual(-3.5f, CoreNeckLayout.ZMm(3.5), 1e-6);
            Assert.AreEqual(17.5f, CoreNeckLayout.ZMm(-17.5), 1e-6);
        }
    }
}
