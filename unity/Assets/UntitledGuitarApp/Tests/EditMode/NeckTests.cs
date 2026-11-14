// EditMode tests for the neck visual (Runtime/Neck). NeckTests covers the pure code: layout validation, string
// interpolation, the dot-to-local mapping in the pinned neck frame, left-handed mirroring, floating placement, sizes in
// visual angle, finger-number layout, the brightness slew limiter and the mesh builder. NeckComponentTests builds a real
// FloatingNeck in the editor (no Play Mode) and checks its anchors and placement.
// Expected numbers come from docs/MASTER_PLAN.md Appendix B2 (chord table) and B4 (fretboard geometry), not from the code.
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Neck;
using UntitledGuitarApp.Runtime.Neck.Dev;

namespace UntitledGuitarApp.Tests.EditMode
{
    public class NeckTests
    {
        const float Mm = 0.05f;   // tolerance for millimetre values quoted to one decimal in the plan

        // Appendix B2 rows, string 6 -> 1 (-1 = x, 0 = open; finger 0 = none).
        static readonly int[] EmFrets = { 0, 2, 2, 0, 0, 0 };
        static readonly int[] EmFingers = { 0, 2, 3, 0, 0, 0 };
        static readonly int[] CFrets = { -1, 3, 2, 0, 1, 0 };
        static readonly int[] CFingers = { 0, 3, 2, 0, 1, 0 };
        static readonly int[] DFrets = { -1, -1, 0, 2, 3, 2 };
        static readonly int[] DFingers = { 0, 0, 0, 1, 3, 2 };
        static readonly int[] AmFrets = { -1, 0, 2, 2, 1, 0 };
        static readonly int[] AmFingers = { 0, 0, 2, 3, 1, 0 };
        static readonly int[] AFrets = { -1, 0, 2, 2, 2, 0 };
        static readonly int[] AFingers = { 0, 0, 1, 2, 3, 0 };

        static FingertipDot[] NewDots() { return new FingertipDot[FloatingNeck.MaxDots]; }
        static StringMark[] NewMarks() { return new StringMark[NeckLayout.StringCount]; }

        static void AssertVector(Vector3 actual, Vector3 expected, float tolerance, string what)
        {
            Assert.That((actual - expected).magnitude, Is.LessThan(tolerance), what + ": expected " + expected.ToString("F5") + " but was " + actual.ToString("F5"));
        }

        static void AssertContains(List<string> errors, string fragment)
        {
            foreach (string e in errors)
                if (e.Contains(fragment)) return;
            Assert.Fail("no error mentions '" + fragment + "'. Errors: " + string.Join(" | ", errors));
        }

        // ------------------------------------------------------------------ layout validation

        [Test]
        public void DevLayout_IsValid()
        {
            Assert.That(DevNeckLayout.Create().Validate(), Is.Empty);
        }

        [Test]
        public void Validate_SaysWhenTheLayoutIsInMetres()
        {
            NeckLayout layout = DevNeckLayout.Create();
            layout.scaleLengthMm = 0.648f;
            AssertContains(layout.Validate(), "looks like metres");
        }

        [Test]
        public void Validate_SaysWhenStringsAreListedFromLowEToHighE()
        {
            NeckLayout layout = DevNeckLayout.Create();
            System.Array.Reverse(layout.stringZAtNutMm);
            AssertContains(layout.Validate(), "+z points toward string 1");
        }

        [Test]
        public void Validate_SaysWhenFretsAreOutOfOrder()
        {
            NeckLayout layout = DevNeckLayout.Create();
            layout.fretXMm[3] = layout.fretXMm[1];
            AssertContains(layout.Validate(), "fretXMm must increase");
        }

        [Test]
        public void Validate_SaysWhenTheNutIsNotAtZero()
        {
            NeckLayout layout = DevNeckLayout.Create();
            layout.fretXMm[0] = 5f;
            AssertContains(layout.Validate(), "fretXMm[0] is the nut");
        }

        [Test]
        public void Validate_SaysWhenAStringArrayHasTheWrongLength()
        {
            NeckLayout layout = DevNeckLayout.Create();
            layout.stringZAtSaddleMm = new float[5];
            AssertContains(layout.Validate(), "stringZAtSaddleMm must have 6 values");
        }

        [Test]
        public void Validate_SaysWhenAStringLiesOutsideTheFretboard()
        {
            NeckLayout layout = DevNeckLayout.Create();
            layout.fretboardWidthAtNutMm = 30f;   // narrower than the 35 mm string spread
            AssertContains(layout.Validate(), "outside the fretboard");
        }

        [Test]
        public void Validate_SaysWhenTheSoundholeIsOnTheFrets()
        {
            NeckLayout layout = DevNeckLayout.Create();
            layout.nutToSoundholeMm = 100f;
            AssertContains(layout.Validate(), "nutToSoundholeMm");
        }

        [Test]
        public void Validate_AcceptsAMeasuredSoundhole_AndMissingArraysDoNotThrow()
        {
            NeckLayout layout = DevNeckLayout.Create();
            layout.nutToSoundholeMm = 480f;
            Assert.That(layout.Validate(), Is.Empty);

            Assert.That(new NeckLayout().Validate(), Is.Not.Empty);   // all defaults: several errors, no exception
        }

        [Test]
        public void Clone_IsADeepCopy()
        {
            NeckLayout layout = DevNeckLayout.Create();
            NeckLayout copy = layout.Clone();
            layout.fretXMm[1] = 999f;
            layout.stringZAtNutMm[0] = 999f;
            Assert.That(copy.fretXMm[1], Is.EqualTo(36.37f).Within(Mm));
            Assert.That(copy.stringZAtNutMm[0], Is.EqualTo(17.5f).Within(0.001f));
            Assert.That(copy.LastFret, Is.EqualTo(5));
        }

        // ------------------------------------------------------------------ dev data against Appendix B4

        [Test]
        public void DevLayout_FretsMatchAppendixB4()
        {
            NeckLayout layout = DevNeckLayout.Create();
            float[] expected = { 0f, 36.4f, 70.7f, 103.1f, 133.7f, 162.5f };   // L = 648 mm
            Assert.That(layout.fretXMm.Length, Is.EqualTo(expected.Length));
            for (int n = 0; n < expected.Length; n++)
                Assert.That(layout.fretXMm[n], Is.EqualTo(expected[n]).Within(Mm), "fret " + n);
            Assert.That(DevNeckLayout.FretXMm(12), Is.EqualTo(324.0f).Within(Mm), "fret 12 is half the scale");
        }

        [Test]
        public void DevLayout_FingertipTargetsMatchAppendixB4()
        {
            Assert.That(DevNeckLayout.FingertipXMm(1), Is.EqualTo(27.3f).Within(Mm));
            Assert.That(DevNeckLayout.FingertipXMm(2), Is.EqualTo(62.1f).Within(Mm));
            Assert.That(DevNeckLayout.FingertipXMm(3), Is.EqualTo(95.0f).Within(Mm));
        }

        [Test]
        public void DevLayout_StringsSpreadFrom35To53Mm_String1OnThePlusZSide()
        {
            NeckLayout layout = DevNeckLayout.Create();
            Assert.That(layout.stringZAtNutMm[0] - layout.stringZAtNutMm[5], Is.EqualTo(35f).Within(0.001f));
            Assert.That(layout.stringZAtSaddleMm[0] - layout.stringZAtSaddleMm[5], Is.EqualTo(53f).Within(0.001f));
            Assert.That(layout.stringZAtNutMm[0], Is.EqualTo(17.5f).Within(0.001f), "string 1 (high E) is at +z");
            Assert.That(layout.stringZAtNutMm[5], Is.EqualTo(-17.5f).Within(0.001f), "string 6 (low E) is at -z");
            Assert.That(NeckMath.StringPitchAtNutMm(layout), Is.EqualTo(7f).Within(0.001f));
            Assert.That(layout.fretboardWidthAtNutMm, Is.EqualTo(43f));
        }

        // ------------------------------------------------------------------ string interpolation

        [Test]
        public void StringZ_RunsStraightFromNutToSaddle()
        {
            NeckLayout layout = DevNeckLayout.Create();
            for (int s = 1; s <= 6; s++)
            {
                float nut = layout.stringZAtNutMm[s - 1], saddle = layout.stringZAtSaddleMm[s - 1];
                Assert.That(NeckMath.StringZMm(layout, s, 0f), Is.EqualTo(nut).Within(1e-4f));
                Assert.That(NeckMath.StringZMm(layout, s, layout.scaleLengthMm), Is.EqualTo(saddle).Within(1e-4f));
                Assert.That(NeckMath.StringZMm(layout, s, layout.scaleLengthMm * 0.5f), Is.EqualTo((nut + saddle) * 0.5f).Within(1e-4f));
            }
            // String 1 at the fret-3 fingertip (x = 95.0): 17.5 + (26.5 - 17.5) * 95 / 648 = 18.82 mm.
            Assert.That(NeckMath.StringZMm(layout, 1, 95f), Is.EqualTo(18.82f).Within(0.01f));
            Assert.That(NeckMath.StringZMm(17.5f, 26.5f, 648f, 95f), Is.EqualTo(18.82f).Within(0.01f));
        }

        [Test]
        public void StringZ_KeepsTheStringOrderAlongTheWholeNeck()
        {
            NeckLayout layout = DevNeckLayout.Create();
            for (float x = 0f; x <= layout.scaleLengthMm; x += 54f)
                for (int s = 2; s <= 6; s++)
                    Assert.That(NeckMath.StringZMm(layout, s, x), Is.LessThan(NeckMath.StringZMm(layout, s - 1, x)), "string " + s + " at x = " + x);
        }

        [Test]
        public void FretboardHalfWidth_TapersFromTheNutToTheLastFret()
        {
            NeckLayout layout = DevNeckLayout.Create();
            Assert.That(NeckMath.FretboardHalfWidthMm(layout, 0f), Is.EqualTo(21.5f).Within(1e-4f));
            Assert.That(NeckMath.FretboardHalfWidthMm(layout, layout.LastFretXMm), Is.EqualTo(layout.fretboardWidthAtLastFretMm * 0.5f).Within(1e-4f));
            Assert.That(layout.fretboardWidthAtLastFretMm, Is.GreaterThan(layout.fretboardWidthAtNutMm));
        }

        // ------------------------------------------------------------------ dots in the pinned frame

        [Test]
        public void GChord_IsTheAppendixB2Row()
        {
            NeckLayout layout = DevNeckLayout.Create();
            FingertipDot[] dots = NewDots();
            StringMark[] marks = NewMarks();
            int count = DevNeckLayout.GChord(layout, dots, marks);

            // G: frets 3 2 0 0 0 3, fingers 2 1 - - - 3 (string 6 -> 1).
            Assert.That(count, Is.EqualTo(3));
            Assert.That(dots[0].stringNumber, Is.EqualTo(6)); Assert.That(dots[0].finger, Is.EqualTo(2)); Assert.That(dots[0].xMm, Is.EqualTo(95.0f).Within(Mm));
            Assert.That(dots[1].stringNumber, Is.EqualTo(5)); Assert.That(dots[1].finger, Is.EqualTo(1)); Assert.That(dots[1].xMm, Is.EqualTo(62.1f).Within(Mm));
            Assert.That(dots[2].stringNumber, Is.EqualTo(1)); Assert.That(dots[2].finger, Is.EqualTo(3)); Assert.That(dots[2].xMm, Is.EqualTo(95.0f).Within(Mm));
            Assert.That(marks, Is.EqualTo(new[] { StringMark.None, StringMark.Open, StringMark.Open, StringMark.Open, StringMark.None, StringMark.None }),
                        "index 0 is string 1: strings 2, 3 and 4 are open");
        }

        [Test]
        public void ChordRow_MarksUnplayedStringsWithX()
        {
            NeckLayout layout = DevNeckLayout.Create();
            FingertipDot[] dots = NewDots();
            StringMark[] marks = NewMarks();
            int count = DevNeckLayout.FromChordRow(layout, DFrets, DFingers, dots, marks);
            Assert.That(count, Is.EqualTo(3));
            Assert.That(marks[5], Is.EqualTo(StringMark.Muted), "D: string 6 is not played");
            Assert.That(marks[4], Is.EqualTo(StringMark.Muted), "D: string 5 is not played");
            Assert.That(marks[3], Is.EqualTo(StringMark.Open), "D: string 4 is open");
        }

        [Test]
        public void DotLocalPosition_IsTheDotInMetres_OnTheStrings()
        {
            NeckLayout layout = DevNeckLayout.Create();
            FingertipDot[] dots = NewDots();
            int count = DevNeckLayout.GChord(layout, dots, NewMarks());
            Assert.That(count, Is.EqualTo(3));

            // String 6 (low E), fret 3: x = 95.0 mm along the neck; z = -(17.5 + 9 * 95 / 648) = -18.82 mm (the -z side); y = string height.
            AssertVector(NeckMath.DotLocalPosition(layout, dots[0]), new Vector3(0.09501f, 0.002f, -0.018820f), 0.00006f, "string 6 fret 3");
            // String 1 (high E), fret 3: the mirror image across the centre line, on the +z side.
            AssertVector(NeckMath.DotLocalPosition(layout, dots[2]), new Vector3(0.09501f, 0.002f, 0.018820f), 0.00006f, "string 1 fret 3");
            // String 5, fret 2: x = 62.1 mm; z = -(10.5 + 5.4 * 62.1 / 648) = -11.02 mm.
            AssertVector(NeckMath.DotLocalPosition(layout, dots[1]), new Vector3(0.06213f, 0.002f, -0.011018f), 0.00006f, "string 5 fret 2");
        }

        [Test]
        public void DotLocalPosition_UsesTheDotsOwnNumbers_NotTheStringNumber()
        {
            NeckLayout layout = DevNeckLayout.Create();
            var dot = new FingertipDot(3, 50f, -4.25f, 1);   // z deliberately not on string 3
            AssertVector(NeckMath.DotLocalPosition(layout, dot), new Vector3(0.050f, 0.002f, -0.00425f), 1e-6f, "data wins");
            AssertVector(NeckMath.LocalMetres(1000f, 2000f, 3000f), new Vector3(1f, 2f, 3f), 1e-6f, "axes keep their names; only the unit changes");
        }

        // ------------------------------------------------------------------ floating placement

        [Test]
        public void Floating_IsAheadLowerLeft_AtTheDefaultDistance()
        {
            NeckPose pose = NeckPlacement.Floating(FloatingPlacement.Default, false);
            Assert.That(pose.position.magnitude, Is.EqualTo(0.9f).Within(1e-4f), "eye to nut");
            Assert.That(pose.position.magnitude, Is.InRange(0.8f, 1.0f));
            Assert.That(pose.position.z, Is.GreaterThan(0.8f), "ahead");
            Assert.That(pose.position.x, Is.LessThan(0f), "left of centre");
            Assert.That(pose.position.y, Is.LessThan(0f), "below centre");
            Assert.That(pose.scale.x, Is.InRange(1.0f, 1.2f));
            Assert.That(pose.scale, Is.EqualTo(new Vector3(pose.scale.x, pose.scale.x, pose.scale.x)), "right-handed: no mirror");
            Assert.That(pose.IsMirrored, Is.False);
        }

        [Test]
        public void Floating_NutOnTheLeft_LowENearestThePlayer()
        {
            NeckLayout layout = DevNeckLayout.Create();
            NeckPose pose = NeckPlacement.Floating(FloatingPlacement.Default, false);
            float h = layout.stringHeightMm;

            Vector3 nut = pose.TransformPoint(Vector3.zero);
            Vector3 fret5 = pose.TransformPoint(NeckMath.LocalMetres(layout.LastFretXMm, 0f, 0f));
            Assert.That(fret5.x, Is.GreaterThan(nut.x + 0.15f), "the neck runs to the player's right, so the nut is on the left");
            Assert.That(fret5.y, Is.EqualTo(nut.y).Within(1e-4f), "and level");

            Vector3 lowE = pose.TransformPoint(NeckMath.LocalMetres(80f, h, NeckMath.StringZMm(layout, 6, 80f)));
            Vector3 highE = pose.TransformPoint(NeckMath.LocalMetres(80f, h, NeckMath.StringZMm(layout, 1, 80f)));
            Assert.That(lowE.magnitude, Is.LessThan(highE.magnitude), "low E is nearer the eye");
            Assert.That(lowE.y, Is.LessThan(highE.y), "and lower in the view, as when looking down at your own neck");

            // The playing surface faces the eye: +y (out of the fretboard) points back toward the head at the origin.
            Vector3 normal = pose.TransformDirection(new Vector3(0f, 1f, 0f));
            Assert.That(Vector3.Dot(normal, -nut.normalized), Is.GreaterThan(0.85f));
        }

        [Test]
        public void Floating_EverythingFitsInsideTheGlassesFieldOfView()
        {
            NeckLayout layout = DevNeckLayout.Create();
            float horizontal, vertical;
            NeckMath.FovFromDiagonal(52f, 16f / 9f, out horizontal, out vertical);
            foreach (bool leftHanded in new[] { false, true })
            {
                NeckPose pose = NeckPlacement.Floating(FloatingPlacement.Default, leftHanded);
                float strumX = NeckMath.FloatingStrumZoneXMm(layout, 110f) + 80f;   // the far end of the strum patch
                float halfWidth = 60f;                                              // fretboard plus the finger numbers
                foreach (Vector3 corner in new[]
                         {
                             NeckMath.LocalMetres(-25f, 0f, -halfWidth), NeckMath.LocalMetres(-25f, 0f, halfWidth),
                             NeckMath.LocalMetres(strumX, 0f, -halfWidth), NeckMath.LocalMetres(strumX, 0f, halfWidth),
                         })
                {
                    Vector3 p = pose.TransformPoint(corner);
                    float azimuth = Mathf.Atan2(p.x, p.z) * Mathf.Rad2Deg;
                    float elevation = Mathf.Atan2(p.y, p.z) * Mathf.Rad2Deg;
                    Assert.That(Mathf.Abs(azimuth), Is.LessThan(horizontal * 0.5f - 1f), "azimuth of " + corner + (leftHanded ? " (left-handed)" : ""));
                    Assert.That(Mathf.Abs(elevation), Is.LessThan(vertical * 0.5f - 1f), "elevation of " + corner);
                }
            }
        }

        [Test]
        public void FloatingPlacement_IsClampedToThePinnedRanges()
        {
            var wild = new FloatingPlacement { distanceM = 5f, azimuthDeg = 40f, elevationDeg = 30f, tiltDeg = 200f, scale = 3f };
            FloatingPlacement c = wild.Clamped();
            Assert.That(c.distanceM, Is.EqualTo(1.0f));
            Assert.That(c.scale, Is.EqualTo(1.2f));
            Assert.That(c.azimuthDeg, Is.LessThan(0f), "always left of centre");
            Assert.That(c.elevationDeg, Is.LessThan(0f), "always below centre");
            Assert.That(c.tiltDeg, Is.EqualTo(60f));

            var broken = new FloatingPlacement { distanceM = float.NaN, azimuthDeg = float.NaN, elevationDeg = float.NaN, tiltDeg = float.NaN, scale = float.NaN };
            FloatingPlacement d = FloatingPlacement.Default;
            Assert.That(broken.Clamped().distanceM, Is.EqualTo(d.distanceM));
            Assert.That(broken.Clamped().scale, Is.EqualTo(d.scale));
            Assert.That(default(FloatingPlacement).Clamped().distanceM, Is.EqualTo(0.8f), "an unset placement still lands in range");
        }

        [Test]
        public void RotationFromAxes_CarriesXAndY_AndZIsTheirCross()
        {
            Vector3 x = new Vector3(0.6f, 0f, 0.8f);
            Vector3 y = new Vector3(0f, 1f, 0f);
            Quaternion q = NeckPlacement.RotationFromAxes(x, y);
            AssertVector(q * new Vector3(1f, 0f, 0f), x, 1e-5f, "+x");
            AssertVector(q * new Vector3(0f, 1f, 0f), y, 1e-5f, "+y");
            AssertVector(q * new Vector3(0f, 0f, 1f), Vector3.Cross(x, y), 1e-5f, "+z");

            // All four branches of the matrix-to-quaternion conversion (trace > 0 and each axis largest).
            foreach (Vector3 axisX in new[] { new Vector3(1f, 0f, 0f), new Vector3(-1f, 0f, 0f), new Vector3(0f, 0f, -1f), new Vector3(0f, -1f, 0f) })
            {
                foreach (Vector3 axisY in new[] { new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f), new Vector3(1f, 0f, 0f), new Vector3(0f, -1f, 0f) })
                {
                    if (Mathf.Abs(Vector3.Dot(axisX, axisY)) > 0.5f) continue;
                    Quaternion r = NeckPlacement.RotationFromAxes(axisX, axisY);
                    AssertVector(r * new Vector3(1f, 0f, 0f), axisX, 1e-5f, "+x for " + axisX + axisY);
                    AssertVector(r * new Vector3(0f, 1f, 0f), axisY, 1e-5f, "+y for " + axisX + axisY);
                }
            }
            Assert.That(NeckPlacement.RotationFromAxes(Vector3.zero, y), Is.EqualTo(Quaternion.identity), "degenerate input");
        }

        [Test]
        public void NeckPose_InverseTransformPoint_UndoesTransformPoint()
        {
            foreach (bool leftHanded in new[] { false, true })
            {
                NeckPose pose = NeckPlacement.Floating(FloatingPlacement.Default, leftHanded);
                Vector3 local = new Vector3(0.095f, 0.002f, -0.0188f);
                AssertVector(pose.InverseTransformPoint(pose.TransformPoint(local)), local, 1e-5f, "round trip");
            }
        }

        [Test]
        public void ToWorld_FollowsTheHead()
        {
            NeckPose pose = NeckPlacement.Floating(FloatingPlacement.Default, false);
            Vector3 headPosition = new Vector3(1f, 1.2f, -3f);
            Quaternion headRotation = NeckPlacement.RotationFromAxes(new Vector3(0f, 0f, -1f), new Vector3(0f, 1f, 0f));   // looking along +x
            Vector3 worldPosition;
            Quaternion worldRotation;
            NeckPlacement.ToWorld(headPosition, headRotation, pose, out worldPosition, out worldRotation);
            AssertVector(worldPosition, headPosition + headRotation * pose.position, 1e-5f, "position");
            Assert.That((worldPosition - headPosition).x, Is.GreaterThan(0.8f), "0.9 m ahead of a head that looks along +x");
            AssertVector(worldRotation * new Vector3(1f, 0f, 0f), headRotation * (pose.rotation * new Vector3(1f, 0f, 0f)), 1e-5f, "rotation");
        }

        // ------------------------------------------------------------------ left-handed mirroring

        [Test]
        public void LeftHanded_IsTheMirrorImageInTheHeadsLeftRightPlane()
        {
            NeckLayout layout = DevNeckLayout.Create();
            NeckPose right = NeckPlacement.Floating(FloatingPlacement.Default, false);
            NeckPose left = NeckPlacement.Floating(FloatingPlacement.Default, true);
            Assert.That(left.IsMirrored, Is.True);
            Assert.That(left.scale, Is.EqualTo(new Vector3(right.scale.x, right.scale.y, -right.scale.z)), "the mirror is the sign of scale.z");

            // Every point of the neck lands at (-x, y, z) of where the right-handed neck has it.
            foreach (Vector3 local in new[]
                     {
                         Vector3.zero, NeckMath.LocalMetres(162.5f, 0f, 0f), NeckMath.LocalMetres(95f, 2f, 18.8f),
                         NeckMath.LocalMetres(95f, 2f, -18.8f), NeckMath.LocalMetres(30f, 10f, 5f),
                     })
            {
                Vector3 r = right.TransformPoint(local);
                AssertVector(left.TransformPoint(local), new Vector3(-r.x, r.y, r.z), 1e-5f, "mirror of " + local.ToString("F4"));
            }

            Vector3 nut = left.TransformPoint(Vector3.zero);
            Vector3 fret5 = left.TransformPoint(NeckMath.LocalMetres(layout.LastFretXMm, 0f, 0f));
            Assert.That(nut.x, Is.GreaterThan(0f), "the nut is now on the right");
            Assert.That(fret5.x, Is.LessThan(nut.x - 0.15f), "and the neck runs to the left");
            Vector3 lowE = left.TransformPoint(NeckMath.LocalMetres(80f, 2f, -18f));
            Vector3 highE = left.TransformPoint(NeckMath.LocalMetres(80f, 2f, 18f));
            Assert.That(lowE.magnitude, Is.LessThan(highE.magnitude), "low E is still nearest the player");
        }

        [Test]
        public void LeftHanded_RotationStillCarriesTheRealNeckAxes()
        {
            NeckPose left = NeckPlacement.Floating(FloatingPlacement.Default, true);
            AssertVector(left.rotation * new Vector3(1f, 0f, 0f), new Vector3(-1f, 0f, 0f), 1e-5f, "nut to bridge points left");
            NeckPose right = NeckPlacement.Floating(FloatingPlacement.Default, false);
            Vector3 normalRight = right.rotation * new Vector3(0f, 1f, 0f);
            AssertVector(left.rotation * new Vector3(0f, 1f, 0f), new Vector3(-normalRight.x, normalRight.y, normalRight.z), 1e-5f, "the fretboard normal is mirrored");
            // The rotation alone would put string 1 on the wrong side; the negative z scale puts it back on the far side.
            Assert.That(left.TransformDirection(new Vector3(0f, 0f, 1f)).y, Is.GreaterThan(0.5f), "string 1 is still toward the top of the view");
        }

        [Test]
        public void MirrorLeftRight_Twice_IsTheOriginalPose()
        {
            NeckPose pose = NeckPlacement.Floating(FloatingPlacement.Default, false);
            NeckPose twice = NeckPlacement.MirrorLeftRight(NeckPlacement.MirrorLeftRight(pose));
            AssertVector(twice.position, pose.position, 1e-6f, "position");
            AssertVector(twice.scale, pose.scale, 1e-6f, "scale");
            foreach (Vector3 local in new[] { new Vector3(0.1f, 0f, 0f), new Vector3(0f, 0.1f, 0f), new Vector3(0f, 0f, 0.1f) })
                AssertVector(twice.TransformPoint(local), pose.TransformPoint(local), 1e-5f, "point " + local);
        }

        [Test]
        public void ScaleFor_MirrorsOnlyAcrossTheNeck()
        {
            Assert.That(NeckPlacement.ScaleFor(1f, false), Is.EqualTo(new Vector3(1f, 1f, 1f)));
            Assert.That(NeckPlacement.ScaleFor(1.2f, true), Is.EqualTo(new Vector3(1.2f, 1.2f, -1.2f)));
        }

        [Test]
        public void LabelFacing_TurnsTheNumbersTowardTheViewer_AboutTheNeckAxisOnly()
        {
            Vector3 about = new Vector3(0.08f, 0.002f, 0f);

            // Viewer straight above the fretboard: the label lies flat, reading along the neck, its top toward string 1.
            Quaternion flat = NeckPlacement.LabelFacing(about + new Vector3(0f, 0.5f, 0f), about);
            AssertVector(flat * new Vector3(1f, 0f, 0f), new Vector3(1f, 0f, 0f), 1e-5f, "reads along the neck");
            AssertVector(flat * new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f), 1e-5f, "top toward string 1");
            AssertVector(flat * new Vector3(0f, 0f, 1f), new Vector3(0f, -1f, 0f), 1e-5f, "a TextMesh is read looking along its +z: down onto the neck");

            // Viewer on the low-E side and above: the label tips up to face them, but still reads along the neck.
            Vector3 viewer = about + new Vector3(0.3f, 0.4f, -0.3f);
            Quaternion tipped = NeckPlacement.LabelFacing(viewer, about);
            AssertVector(tipped * new Vector3(1f, 0f, 0f), new Vector3(1f, 0f, 0f), 1e-5f, "still along the neck");
            Vector3 lookAlong = tipped * new Vector3(0f, 0f, 1f);
            AssertVector(lookAlong, new Vector3(0f, -0.8f, 0.6f), 1e-5f, "faces the viewer across the neck");
            Assert.That((tipped * new Vector3(0f, 1f, 0f)).z, Is.GreaterThan(0f), "top still toward string 1");
        }

        // ------------------------------------------------------------------ sizes in visual angle

        [Test]
        public void SizeForAngle_MatchesThePlansTextSizes()
        {
            // MASTER_PLAN section 17: 1.2 degrees is about 2.5 cm at 1.2 m and about 1.7 cm at 0.8 m.
            Assert.That(NeckMath.SizeForAngle(1.2f, 1.2f), Is.EqualTo(0.0251f).Within(0.0002f));
            Assert.That(NeckMath.SizeForAngle(1.2f, 0.8f), Is.EqualTo(0.0168f).Within(0.0002f));
            Assert.That(NeckMath.AngleForSize(NeckMath.SizeForAngle(1.2f, 0.9f), 0.9f), Is.EqualTo(1.2f).Within(1e-4f));
            Assert.That(NeckMath.SizeForAngle(0f, 1f), Is.EqualTo(0f));
            Assert.That(NeckMath.SizeForAngle(1f, -1f), Is.EqualTo(0f));
        }

        [Test]
        public void FovFromDiagonal_GivesAbout46By27DegreesForTheGlasses()
        {
            float horizontal, vertical;
            NeckMath.FovFromDiagonal(52f, 16f / 9f, out horizontal, out vertical);
            Assert.That(horizontal, Is.EqualTo(46.1f).Within(0.1f));
            Assert.That(vertical, Is.EqualTo(26.9f).Within(0.1f));
            Assert.That(NeckMath.PixelsPerDegreeAtCentre(vertical, 1080), Is.EqualTo(39.4f).Within(0.3f));
        }

        [Test]
        public void Sizes_TextIsAtLeast1Point2Degrees_AndNoLineIsThinnerThanThreePixels()
        {
            NeckLayout layout = DevNeckLayout.Create();
            float horizontal, vertical;
            NeckMath.FovFromDiagonal(52f, 16f / 9f, out horizontal, out vertical);
            float pixelsPerDegree = NeckMath.PixelsPerDegreeAtCentre(vertical, 1080);

            foreach (float distance in new[] { 0.8f, 0.9f, 1.0f, 0.55f })
            {
                foreach (float scale in new[] { 1.0f, 1.2f })
                {
                    NeckSizes sizes = NeckStyle.Resolve(layout, distance, scale, false);
                    float cap = NeckMath.AngleForSize(sizes.labelCapMm * scale * 0.001f, distance);
                    Assert.That(cap, Is.EqualTo(1.2f).Within(0.001f), "finger numbers at " + distance + " m, scale " + scale);

                    foreach (float lineMm in new[] { sizes.stringThinMm, sizes.stringThickMm, sizes.fretMm, sizes.edgeMm, sizes.markStrokeMm, sizes.nutMm })
                    {
                        float degrees = NeckMath.AngleForSize(lineMm * scale * 0.001f, distance);
                        Assert.That(degrees, Is.GreaterThanOrEqualTo(NeckStyle.MinLineDeg - 1e-4f));
                        Assert.That(degrees * pixelsPerDegree, Is.GreaterThan(2.9f), "a line must stay about 3 px wide or it flickers");
                    }
                    Assert.That(sizes.StringWidthMm(6), Is.GreaterThan(sizes.StringWidthMm(1)), "the low E string is drawn thicker");
                    Assert.That(sizes.StringWidthMm(1), Is.EqualTo(sizes.stringThinMm));
                    Assert.That(sizes.StringWidthMm(6), Is.EqualTo(sizes.stringThickMm));
                    Assert.That(sizes.nutMm, Is.GreaterThan(sizes.stringThickMm * 2f), "the nut is a bar, not a line");
                }
            }

            NeckSizes large = NeckStyle.Resolve(layout, 0.9f, 1.2f, true);
            NeckSizes normal = NeckStyle.Resolve(layout, 0.9f, 1.2f, false);
            Assert.That(large.labelCapMm, Is.EqualTo(normal.labelCapMm * NeckStyle.LargeTextScale).Within(0.01f), "largeText makes the numbers bigger");
            Assert.That(NeckStyle.LabelCapDegrees(false), Is.GreaterThanOrEqualTo(1.2f));
        }

        [Test]
        public void Sizes_DotsFitBetweenTheirNeighbours_AndMarksDoNotTouch()
        {
            NeckLayout layout = DevNeckLayout.Create();
            NeckSizes sizes = NeckStyle.Resolve(layout, 0.9f, 1.2f, false);
            float pitch = NeckMath.StringPitchAtNutMm(layout);
            Assert.That(sizes.dotRadiusMm * 2f, Is.LessThanOrEqualTo(pitch), "two dots on neighbouring strings in one fret do not merge");
            Assert.That(sizes.dotRadiusMm * 2f, Is.GreaterThan(pitch * 0.8f), "but the dot is as big as the spacing allows");
            Assert.That(sizes.markRadiusMm * 2f, Is.LessThan(pitch));
            Assert.That(sizes.haloRadiusMm, Is.GreaterThan(sizes.dotRadiusMm));
            Assert.That(sizes.hotRadiusMm, Is.LessThan(sizes.dotRadiusMm));
        }

        [Test]
        public void FingerColors_AreThePlansPalette()
        {
            // MASTER_PLAN section 17: 1 yellow, 2 magenta, 3 lime, 4 orange.
            Color32 yellow = NeckStyle.FingerColor(1), magenta = NeckStyle.FingerColor(2), lime = NeckStyle.FingerColor(3), orange = NeckStyle.FingerColor(4);
            Assert.That(yellow.r > 200 && yellow.g > 200 && yellow.b < 120, "1 is yellow");
            Assert.That(magenta.r > 200 && magenta.b > 200 && magenta.g < 120, "2 is magenta");
            Assert.That(lime.g > 200 && lime.r > 120 && lime.r < 220 && lime.b < 120, "3 is lime");
            Assert.That(orange.r > 200 && orange.g > 100 && orange.g < 200 && orange.b < 80, "4 is orange");
            Assert.That(NeckStyle.FingerColor(0), Is.EqualTo(NeckStyle.DotWhite));
            Assert.That(NeckStyle.Dim(new Color32(200, 100, 50, 255), 0.5f), Is.EqualTo(new Color32(100, 50, 25, 255)));
            Assert.That(NeckStyle.Lighten(new Color32(0, 0, 0, 255), 1f), Is.EqualTo(new Color32(255, 255, 255, 255)));
        }

        // ------------------------------------------------------------------ strum zone

        [Test]
        public void StrumZone_UsesTheMeasuredSoundhole_OrTheDocumentedDefault()
        {
            NeckLayout layout = DevNeckLayout.Create();
            Assert.That(NeckMath.StrumZoneXMm(layout), Is.EqualTo(648f * NeckMath.DefaultSoundholeFractionOfScale).Within(0.01f), "unknown: default");
            layout.nutToSoundholeMm = 470f;
            Assert.That(NeckMath.StrumZoneXMm(layout), Is.EqualTo(470f));

            // The floating neck is drawn shortened: the strum zone sits just beyond the last fret, never beyond the real place.
            Assert.That(NeckMath.FloatingStrumZoneXMm(layout, 110f), Is.EqualTo(layout.LastFretXMm + 110f).Within(0.01f));
            Assert.That(NeckMath.FloatingStrumZoneXMm(layout, 5000f), Is.EqualTo(470f));
            Assert.That(NeckMath.FloatingStrumZoneXMm(layout, 0f), Is.EqualTo(470f), "0 = keep the real distance");
        }

        // ------------------------------------------------------------------ finger-number layout

        static int Layout(NeckLayout layout, int[] frets, int[] fingers, FingertipDot[] dots, LabelSlot[] slots, out NeckSizes sizes)
        {
            sizes = NeckStyle.Resolve(layout, 0.9f, 1.2f, false);
            int count = DevNeckLayout.FromChordRow(layout, frets, fingers, dots, NewMarks());
            return NeckLabelLayout.Compute(layout, dots, count, sizes.labelCapMm, sizes.labelCellMm, sizes.labelGapMm, slots);
        }

        static LabelSlot SlotOfFinger(FingertipDot[] dots, LabelSlot[] slots, int slotCount, int finger)
        {
            for (int i = 0; i < slotCount; i++)
                if (dots[slots[i].dotIndex].finger == finger) return slots[i];
            Assert.Fail("finger " + finger + " has no number");
            return default(LabelSlot);
        }

        static void AssertNoOverlap(LabelSlot[] slots, int count, NeckSizes sizes)
        {
            for (int a = 0; a < count; a++)
                for (int b = a + 1; b < count; b++)
                    if (slots[a].side == slots[b].side)
                        Assert.That(Mathf.Abs(slots[a].xMm - slots[b].xMm), Is.GreaterThanOrEqualTo(sizes.labelCellMm), "numbers " + a + " and " + b + " overlap");
        }

        [Test]
        public void Labels_G_EachNumberSitsOutsideTheFretboardLevelWithItsDot()
        {
            NeckLayout layout = DevNeckLayout.Create();
            FingertipDot[] dots = NewDots();
            var slots = new LabelSlot[FloatingNeck.MaxDots];
            NeckSizes sizes;
            int n = Layout(layout, new[] { 3, 2, 0, 0, 0, 3 }, new[] { 2, 1, 0, 0, 0, 3 }, dots, slots, out sizes);
            Assert.That(n, Is.EqualTo(3));

            LabelSlot two = SlotOfFinger(dots, slots, n, 2), one = SlotOfFinger(dots, slots, n, 1), three = SlotOfFinger(dots, slots, n, 3);
            Assert.That(two.side, Is.EqualTo(-1), "finger 2 is on string 6: its number goes on the low-E side");
            Assert.That(one.side, Is.EqualTo(-1), "finger 1 is on string 5: low-E side");
            Assert.That(three.side, Is.EqualTo(1), "finger 3 is on string 1: high-E side");
            Assert.That(two.xMm, Is.EqualTo(95.0f).Within(Mm));
            Assert.That(one.xMm, Is.EqualTo(62.1f).Within(Mm));
            Assert.That(three.xMm, Is.EqualTo(95.0f).Within(Mm));
            for (int i = 0; i < n; i++)
            {
                float edge = NeckMath.FretboardHalfWidthMm(layout, slots[i].xMm);
                Assert.That(Mathf.Abs(slots[i].zMm) - sizes.labelCapMm * 0.5f, Is.GreaterThan(edge), "the whole digit is outside the fretboard");
                Assert.That(Mathf.Sign(slots[i].zMm), Is.EqualTo((float)slots[i].side));
            }
            AssertNoOverlap(slots, n, sizes);
        }

        [Test]
        public void Labels_TwoDotsInOneFret_TakeOppositeSides()
        {
            NeckLayout layout = DevNeckLayout.Create();
            FingertipDot[] dots = NewDots();
            var slots = new LabelSlot[FloatingNeck.MaxDots];
            NeckSizes sizes;

            // Em: strings 5 and 4 at fret 2. String 5 is nearer the low-E edge and keeps that side; string 4 goes to the other.
            int n = Layout(layout, EmFrets, EmFingers, dots, slots, out sizes);
            Assert.That(n, Is.EqualTo(2));
            Assert.That(SlotOfFinger(dots, slots, n, 2).side, Is.EqualTo(-1));
            Assert.That(SlotOfFinger(dots, slots, n, 3).side, Is.EqualTo(1));
            Assert.That(SlotOfFinger(dots, slots, n, 2).xMm, Is.EqualTo(62.1f).Within(Mm), "no sliding along the neck");
            Assert.That(SlotOfFinger(dots, slots, n, 3).xMm, Is.EqualTo(62.1f).Within(Mm));

            // D: strings 3 and 1 at fret 2 are both on the high-E half; string 1 keeps it, string 3 crosses to the low-E side.
            n = Layout(layout, DFrets, DFingers, dots, slots, out sizes);
            Assert.That(n, Is.EqualTo(3));
            Assert.That(SlotOfFinger(dots, slots, n, 2).side, Is.EqualTo(1), "finger 2, string 1");
            Assert.That(SlotOfFinger(dots, slots, n, 1).side, Is.EqualTo(-1), "finger 1, string 3");
            Assert.That(SlotOfFinger(dots, slots, n, 3).side, Is.EqualTo(1), "finger 3, string 2 at fret 3");
            AssertNoOverlap(slots, n, sizes);
        }

        [Test]
        public void Labels_NeverOverlap_ForEveryChordInTheTable()
        {
            NeckLayout layout = DevNeckLayout.Create();
            FingertipDot[] dots = NewDots();
            var slots = new LabelSlot[FloatingNeck.MaxDots];
            NeckSizes sizes;
            int[][] frets = { EmFrets, CFrets, DFrets, AmFrets, AFrets };
            int[][] fingers = { EmFingers, CFingers, DFingers, AmFingers, AFingers };
            int[] expected = { 2, 3, 3, 3, 3 };
            for (int c = 0; c < frets.Length; c++)
            {
                int n = Layout(layout, frets[c], fingers[c], dots, slots, out sizes);
                Assert.That(n, Is.EqualTo(expected[c]), "chord " + c);
                AssertNoOverlap(slots, n, sizes);
                for (int i = 0; i < n; i++)
                    Assert.That(slots[i].xMm, Is.GreaterThan(0f), "numbers stay on the fretboard side of the nut");
            }
        }

        [Test]
        public void Labels_DotsWithoutAFingerGetNoNumber_AndBadInputGivesNone()
        {
            NeckLayout layout = DevNeckLayout.Create();
            var dots = new[] { new FingertipDot(3, 62f, 3.7f, 0), new FingertipDot(2, 27f, 10.7f, 1), new FingertipDot(4, 95f, -3.9f, 7) };
            var slots = new LabelSlot[FloatingNeck.MaxDots];
            int n = NeckLabelLayout.Compute(layout, dots, dots.Length, 16f, 13f, 5f, slots);
            Assert.That(n, Is.EqualTo(1));
            Assert.That(slots[0].dotIndex, Is.EqualTo(1));
            Assert.That(NeckLabelLayout.Compute(layout, null, 3, 16f, 13f, 5f, slots), Is.EqualTo(0));
            Assert.That(NeckLabelLayout.Compute(layout, dots, dots.Length, 16f, 13f, 5f, new LabelSlot[0]), Is.EqualTo(0));
        }

        // ------------------------------------------------------------------ brightness slew (nothing flashes faster than 3 Hz)

        [Test]
        public void Slew_ReachesTheTarget_AtTheGivenRate()
        {
            Assert.That(NeckMath.Slew(0f, 1f, 2f, 0.1f), Is.EqualTo(0.2f).Within(1e-6f));
            Assert.That(NeckMath.Slew(1f, 0f, 2f, 0.1f), Is.EqualTo(0.8f).Within(1e-6f));
            Assert.That(NeckMath.Slew(0.95f, 1f, 2f, 0.1f), Is.EqualTo(1f), "never overshoots");
            Assert.That(NeckMath.Slew(0.5f, 1f, 2f, 0f), Is.EqualTo(0.5f), "no time, no change");

            // The 30% -> 100% swing of an active finger takes 250 ms.
            float gain = NeckStyle.InactiveGain;
            int frames = 0;
            while (gain < 1f && frames < 1000)
            {
                gain = NeckMath.Slew(gain, 1f, NeckStyle.GainSlewPerSecond, 1f / 60f);
                frames++;
            }
            Assert.That(frames / 60f, Is.EqualTo(0.25f).Within(0.02f));
        }

        [Test]
        public void Slew_ATargetToggledAt10Hz_CannotFlashFasterThan3Hz()
        {
            // A caller flips the active finger every 50 ms (a 10 Hz square wave). The shown brightness may only wobble:
            // a "flash" is a swing across most of the range, and no such swing can complete faster than 3 times a second.
            const float dt = 1f / 60f;
            float gain = 1f, min = 1f, max = NeckStyle.InactiveGain;
            for (int frame = 0; frame < 600; frame++)
            {
                float target = (frame / 3) % 2 == 0 ? NeckStyle.InactiveGain : 1f;
                gain = NeckMath.Slew(gain, target, NeckStyle.GainSlewPerSecond, dt);
                if (frame > 60)
                {
                    min = Mathf.Min(min, gain);
                    max = Mathf.Max(max, gain);
                }
            }
            Assert.That(max - min, Is.LessThan(0.5f * (1f - NeckStyle.InactiveGain)), "10 Hz toggling is smoothed to a small ripple");

            // And the fastest possible full on-off-on cycle is 2 x 250 ms = 2 Hz.
            float fullCycleSeconds = 2f * (1f - NeckStyle.InactiveGain) / NeckStyle.GainSlewPerSecond;
            Assert.That(1f / fullCycleSeconds, Is.LessThanOrEqualTo(3f));
        }

        // ------------------------------------------------------------------ mesh builder

        static NeckBuildOptions Floating(NeckLayout layout)
        {
            return new NeckBuildOptions { strumZoneXMm = NeckMath.FloatingStrumZoneXMm(layout, 110f) };
        }

        static bool QuadIs(NeckGeometry g, int quad, float shape, int group)
        {
            return g.shape[quad * 4].z == shape && Mathf.RoundToInt(g.extra[quad * 4].x) == group;
        }

        static Vector3 QuadCentre(NeckGeometry g, int quad)
        {
            int v = quad * 4;
            return (g.positions[v] + g.positions[v + 1] + g.positions[v + 2] + g.positions[v + 3]) * 0.25f;
        }

        [Test]
        public void BuildLines_DrawsFretsNutEdgesAndSixStrings()
        {
            NeckLayout layout = DevNeckLayout.Create();
            NeckSizes sizes = NeckStyle.Resolve(layout, 0.9f, 1.2f, false);
            var g = new NeckGeometry(192);
            NeckMeshBuilder.BuildLines(layout, sizes, Floating(layout), g);

            // 5 frets + 1 nut + 2 edges x 2 parts + 6 strings x 2 parts (unbroken to past the strum zone, then a fade).
            Assert.That(g.QuadCount, Is.EqualTo(5 + 1 + 4 + 6 * 2));
            Assert.That(g.Dropped, Is.EqualTo(0));
            Assert.That(g.VertexCount, Is.EqualTo(g.QuadCount * 4));
            Assert.That(g.IndexCount, Is.EqualTo(g.QuadCount * 6));
            for (int i = 0; i < g.VertexCount; i++)
            {
                Vector3 p = g.positions[i];
                Assert.That(NeckMath.IsFinite(p.x) && NeckMath.IsFinite(p.y) && NeckMath.IsFinite(p.z), "vertex " + i);
                Assert.That(p.x, Is.InRange(-0.02f, 0.40f), "metres, along the neck");
                Assert.That(Mathf.Abs(p.z), Is.LessThan(0.04f), "metres, across the neck");
            }

            for (int s = 1; s <= 6; s++)
            {
                int group = NeckGeometry.StringGroup(s), quads = 0;
                for (int q = 0; q < g.QuadCount; q++)
                {
                    if (!QuadIs(g, q, NeckGeometry.ShapeRect, group)) continue;
                    quads++;
                    Vector3 c = QuadCentre(g, q);
                    Assert.That(c.z, Is.EqualTo(NeckMath.StringZMm(layout, s, c.x * 1000f) * 0.001f).Within(2e-5f), "string " + s + " lies on its line");
                    Assert.That(c.y, Is.EqualTo(layout.stringHeightMm * 0.001f).Within(1e-6f), "at string height");
                }
                Assert.That(quads, Is.EqualTo(2), "string " + s);
            }
        }

        // The pieces (x from, x to, in mm) one string is drawn with, sorted along the neck, and how far it stays at full brightness.
        static List<Vector2> StringPieces(NeckGeometry g, int stringNumber, out float fullBrightnessToMm)
        {
            var pieces = new List<Vector2>();
            fullBrightnessToMm = 0f;
            int group = NeckGeometry.StringGroup(stringNumber);
            for (int q = 0; q < g.QuadCount; q++)
            {
                if (!QuadIs(g, q, NeckGeometry.ShapeRect, group)) continue;
                int v = q * 4;   // a line's vertices 0 and 1 are its start and end (minus the half width, which is across the neck)
                float from = g.positions[v].x * 1000f, to = g.positions[v + 1].x * 1000f;
                pieces.Add(new Vector2(from, to));
                Color32 a = g.colors[v], b = g.colors[v + 1];
                if (a.r == b.r && a.g == b.g && a.b == b.b && b.g > 0) fullBrightnessToMm = Mathf.Max(fullBrightnessToMm, to);
            }
            pieces.Sort((m, n) => m.x.CompareTo(n.x));
            return pieces;
        }

        [Test]
        public void BuildLines_FloatingStrings_RunUnbrokenFromTheNutToPastTheStrumZone()
        {
            // The review render of the floating neck showed the strings fading out after fret 5 and starting again before the
            // strum zone. They must read as one neck: every string is one run of pieces joined end to end, starting at the nut.
            NeckLayout layout = DevNeckLayout.Create();
            NeckSizes sizes = NeckStyle.Resolve(layout, 0.9f, 1.2f, false);
            NeckBuildOptions options = Floating(layout);
            var g = new NeckGeometry(192);
            NeckMeshBuilder.BuildLines(layout, sizes, options, g);
            for (int s = 1; s <= 6; s++)
            {
                float full;
                List<Vector2> pieces = StringPieces(g, s, out full);
                Assert.That(pieces.Count, Is.GreaterThan(0));
                Assert.That(pieces[0].x, Is.EqualTo(0f).Within(0.01f), "string " + s + " starts at the nut");
                for (int i = 1; i < pieces.Count; i++)
                    Assert.That(pieces[i].x, Is.EqualTo(pieces[i - 1].y).Within(0.01f), "string " + s + ": no gap before piece " + i);
                Assert.That(full, Is.GreaterThan(options.strumZoneXMm + 10f), "string " + s + " is at full brightness under the strumming hand");
                Assert.That(pieces[pieces.Count - 1].y, Is.LessThanOrEqualTo(layout.scaleLengthMm + 0.01f), "and never runs past the saddle");
            }
        }

        [Test]
        public void BuildLines_AlignedNeck_CrossesTheRibbonsOfStringsAndEdges()
        {
            NeckLayout layout = DevNeckLayout.Create();
            NeckSizes sizes = NeckStyle.Resolve(layout, 0.55f, 1f, false);
            var g = new NeckGeometry(192);
            var options = new NeckBuildOptions { crossRibbons = true, breakStrings = true, strumZoneXMm = NeckMath.StrumZoneXMm(layout) };
            NeckMeshBuilder.BuildLines(layout, sizes, options, g);
            Assert.That(g.QuadCount, Is.EqualTo(5 + 1 + 2 * 4 + 2 * 6 * 5));
            Assert.That(g.Dropped, Is.EqualTo(0));

            // On the real neck the strings break after the frets (the real strings show) and come back under the strumming hand.
            float full;
            List<Vector2> pieces = StringPieces(g, 3, out full);
            bool gap = false;
            for (int i = 1; i < pieces.Count; i++)
                if (pieces[i].x > pieces[i - 1].y + 1f) gap = true;
            Assert.That(gap, Is.True, "aligned: a break between the frets and the strum zone");
            Assert.That(full, Is.GreaterThan(options.strumZoneXMm), "a full-brightness stretch under the strumming hand");

            options.strumZoneXMm = 0f;   // no strum patch
            NeckMeshBuilder.BuildLines(layout, sizes, options, g);
            Assert.That(g.QuadCount, Is.EqualTo(5 + 1 + 2 * 4 + 2 * 6 * 2));
        }

        [Test]
        public void BuildDots_PutsEachDotOnItsDataPosition_InItsFingersGroup()
        {
            NeckLayout layout = DevNeckLayout.Create();
            NeckSizes sizes = NeckStyle.Resolve(layout, 0.9f, 1.2f, false);
            FingertipDot[] dots = NewDots();
            StringMark[] marks = NewMarks();
            int count = DevNeckLayout.GChord(layout, dots, marks);
            var slots = new LabelSlot[FloatingNeck.MaxDots];
            int labels = NeckLabelLayout.Compute(layout, dots, count, sizes.labelCapMm, sizes.labelCellMm, sizes.labelGapMm, slots);
            var g = new NeckGeometry(192);
            NeckMeshBuilder.BuildDots(layout, sizes, Floating(layout), dots, count, marks, slots, labels, g);
            Assert.That(g.Dropped, Is.EqualTo(0));

            // The first nine quads are the three dots: glow, core, white-hot centre, all centred on the dot's own position.
            for (int i = 0; i < count; i++)
            {
                int group = NeckGeometry.FingerGroup(dots[i].finger);
                for (int part = 0; part < 3; part++)
                {
                    int q = i * 3 + part;
                    Assert.That(QuadIs(g, q, NeckGeometry.ShapeDisc, group), "dot " + i + " part " + part);
                    AssertVector(QuadCentre(g, q), NeckMath.DotLocalPosition(layout, dots[i]), 1e-6f, "dot " + i + " part " + part);
                }
                float radius = (g.positions[(i * 3 + 1) * 4 + 1] - g.positions[(i * 3 + 1) * 4]).magnitude * 0.5f;
                Assert.That(radius, Is.EqualTo(sizes.dotRadiusMm * 0.001f).Within(1e-6f), "core radius");
            }

            // G has three open strings (2, 3, 4): three rings on the headstock side of the nut, level with their strings.
            int rings = 0, beads = 0;
            for (int q = count * 3; q < g.QuadCount; q++)
            {
                if (g.shape[q * 4].z != NeckGeometry.ShapeDisc) continue;
                if (g.shape[q * 4].w > 0f)
                {
                    rings++;
                    Vector3 c = QuadCentre(g, q);
                    Assert.That(c.x, Is.LessThan(-sizes.nutMm * 0.001f), "marks sit beyond the nut");
                    bool onAnOpenString = false;
                    foreach (int s in new[] { 2, 3, 4 })
                        if (Mathf.Abs(c.z - layout.stringZAtNutMm[s - 1] * 0.001f) < 1e-6f) onAnOpenString = true;
                    Assert.That(onAnOpenString, "ring at z = " + c.z);
                }
                else
                {
                    beads++;
                }
            }
            Assert.That(rings, Is.EqualTo(3));
            Assert.That(beads, Is.GreaterThan(0), "leaders are drawn from the dots toward the numbers");
            Assert.That(g.QuadCount, Is.EqualTo(count * 3 + rings + beads), "G has no X marks");
        }

        [Test]
        public void BuildDots_DrawsAnXOfTwoStrokesForEachUnplayedString()
        {
            NeckLayout layout = DevNeckLayout.Create();
            NeckSizes sizes = NeckStyle.Resolve(layout, 0.9f, 1.2f, false);
            var marks = new[] { StringMark.None, StringMark.None, StringMark.None, StringMark.None, StringMark.Muted, StringMark.Muted };
            var g = new NeckGeometry(192);
            NeckMeshBuilder.BuildDots(layout, sizes, Floating(layout), null, 0, marks, null, 0, g);
            Assert.That(g.QuadCount, Is.EqualTo(4), "two strokes per X");
            for (int q = 0; q < g.QuadCount; q++)
            {
                Assert.That(QuadIs(g, q, NeckGeometry.ShapeRect, NeckGeometry.GroupFixed));
                Assert.That(g.colors[q * 4], Is.EqualTo(NeckStyle.MarkMuted), "X marks are red");
                Vector3 c = QuadCentre(g, q);
                float z = layout.stringZAtNutMm[q < 2 ? 4 : 5] * 0.001f;
                Assert.That(c.z, Is.EqualTo(z).Within(1e-6f), "level with string " + (q < 2 ? 5 : 6));
                Assert.That(c.x, Is.LessThan(0f));
            }
        }

        [Test]
        public void BuildDots_FingerColorsFlag_ColoursTheDotCore()
        {
            NeckLayout layout = DevNeckLayout.Create();
            NeckSizes sizes = NeckStyle.Resolve(layout, 0.9f, 1.2f, false);
            var dots = new[] { new FingertipDot(5, 62.1f, -11f, 1) };
            var g = new NeckGeometry(64);
            NeckBuildOptions options = Floating(layout);
            NeckMeshBuilder.BuildDots(layout, sizes, options, dots, 1, null, null, 0, g);
            Assert.That(g.colors[4], Is.EqualTo(NeckStyle.DotWhite), "flag off: white core");
            options.fingerColors = true;
            NeckMeshBuilder.BuildDots(layout, sizes, options, dots, 1, null, null, 0, g);
            Assert.That(g.colors[4], Is.EqualTo(NeckStyle.Finger1), "flag on: finger 1 is yellow");
            Assert.That(g.colors[8], Is.EqualTo(NeckStyle.DotWhite), "the centre stays white-hot, so the dot is still the brightest thing");
        }

        [Test]
        public void Geometry_CountsQuadsThatDoNotFit_InsteadOfThrowing()
        {
            var g = new NeckGeometry(2);
            for (int i = 0; i < 5; i++)
                g.Disc(Vector3.zero, new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 1f), NeckStyle.DotWhite, NeckGeometry.Filled, 0, 0f);
            Assert.That(g.QuadCount, Is.EqualTo(2));
            Assert.That(g.Dropped, Is.EqualTo(3));
            g.Clear();
            Assert.That(g.QuadCount, Is.EqualTo(0));
            Assert.That(g.Dropped, Is.EqualTo(0));
            Assert.That(g.triangles, Is.EqualTo(new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 }));
        }

        [Test]
        public void Geometry_GainGroups_AreDistinctAndFitTheShadersTwelve()
        {
            var seen = new HashSet<int>();
            for (int finger = 0; finger <= 4; finger++) Assert.That(seen.Add(NeckGeometry.FingerGroup(finger)), "finger " + finger);
            for (int s = 1; s <= 6; s++) Assert.That(seen.Add(NeckGeometry.StringGroup(s)), "string " + s);
            foreach (int group in seen) Assert.That(group, Is.InRange(1, 12));
            Assert.That(NeckGeometry.GroupCount, Is.EqualTo(13));
            Assert.That(seen.Contains(NeckGeometry.GroupFixed), Is.False);
        }

        [Test]
        public void Geometry_Line_CapsOnlyTheEndsItIsToldTo()
        {
            var g = new NeckGeometry(1);
            g.Line(new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 0.1f), NeckStyle.DotWhite, new Color32(0, 0, 0, 255), true, false, 7, 0.25f);
            Assert.That(g.shape[0].x, Is.EqualTo(-1f), "capped start: the shader fades it over one pixel");
            Assert.That(g.shape[1].x, Is.EqualTo(0f), "open end: no seam where the next segment joins");
            Assert.That(g.shape[0].y, Is.EqualTo(-1f));
            Assert.That(g.shape[3].y, Is.EqualTo(1f));
            Assert.That(g.colors[0], Is.EqualTo(NeckStyle.DotWhite));
            Assert.That(g.colors[1], Is.EqualTo(new Color32(0, 0, 0, 255)), "fades to black = fades out on the additive display");
            Assert.That(g.extra[2], Is.EqualTo(new Vector4(7f, 0.25f, 0f, 0f)));
            AssertVector(g.positions[3] - g.positions[0], new Vector3(0f, 0f, 0.2f), 1e-6f, "full width");
        }

        // ------------------------------------------------------------------ flags

        [Test]
        public void NeckOptions_ReadTheFourFlags()
        {
            var flags = new FlagSet();
            NeckOptions off = NeckOptions.FromFlags(flags);
            Assert.That(off.leftHanded || off.fingerColors || off.largeText || off.debugOverlay, Is.False);

            flags.leftHanded = true;
            flags.ghost.fingerColors = true;
            flags.largeText = true;
            flags.debugOverlay = true;
            NeckOptions on = NeckOptions.FromFlags(flags);
            Assert.That(on.leftHanded && on.fingerColors && on.largeText && on.debugOverlay, Is.True);
            Assert.That(on.SameAs(off), Is.False);
            Assert.That(on.SameAs(NeckOptions.FromFlags(flags)), Is.True);
            Assert.That(NeckOptions.FromFlags(null).leftHanded, Is.False, "no flags: defaults");
        }
    }

    /// <summary>Builds a real FloatingNeck in the editor (no Play Mode, nothing rendered) and checks its anchors and placement.</summary>
    public class NeckComponentTests
    {
        readonly List<GameObject> _created = new List<GameObject>();
        NeckLayout _layout;
        FloatingNeck _neck;
        Transform _world;
        Transform _head;

        [SetUp]
        public void SetUp()
        {
            _layout = DevNeckLayout.Create();
            _world = NewObject("TestWorld").transform;
            _head = NewObject("TestHead").transform;
            _neck = FloatingNeck.Create(_world);
            _neck.UseOptions(new NeckOptions());   // independent of whatever flags file is on this machine
            var dots = new FingertipDot[FloatingNeck.MaxDots];
            var marks = new StringMark[NeckLayout.StringCount];
            int count = DevNeckLayout.GChord(_layout, dots, marks);
            Assert.That(_neck.Build(_layout), Is.True);
            _neck.SetDots(dots, count);
            _neck.SetStringMarks(marks);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _created)
                if (go != null) Object.DestroyImmediate(go);
            _created.Clear();
        }

        GameObject NewObject(string name)
        {
            var go = new GameObject(name);
            _created.Add(go);
            return go;
        }

        static void AssertNear(Vector3 actual, Vector3 expected, float tolerance, string what)
        {
            Assert.That((actual - expected).magnitude, Is.LessThan(tolerance), what + ": expected " + expected.ToString("F5") + " but was " + actual.ToString("F5"));
        }

        [Test]
        public void Create_MakesTheNeckWithItsAnchors_FromCodeAlone()
        {
            Assert.That(_neck.transform.parent, Is.SameAs(_world));
            Assert.That(_neck.Layout, Is.Not.Null);
            Assert.That(_neck.Layout, Is.Not.SameAs(_layout), "Build keeps its own copy");
            Assert.That(_neck.DotCount, Is.EqualTo(3));
            Assert.That(_neck.LabelCount, Is.EqualTo(3), "G: fingers 1, 2 and 3");
            Assert.That(_neck.Mode, Is.EqualTo(NeckMode.Floating));

            Assert.That(_neck.Socket.parent, Is.SameAs(_neck.transform));
            AssertNear(_neck.Socket.localPosition, Vector3.zero, 1e-7f, "the socket is the neck frame's origin: the nut");
            Assert.That(_neck.Socket.localRotation, Is.EqualTo(Quaternion.identity));
            Assert.That(_neck.StrumZone.parent, Is.SameAs(_neck.transform));
            AssertNear(_neck.StrumZone.localPosition, NeckMath.LocalMetres(_layout.LastFretXMm + 110f, _layout.stringHeightMm, 0f), 1e-6f,
                       "floating: the strum zone sits just beyond the last fret, on the centre line at string height");

            Assert.That(_neck.GetComponentsInChildren<MeshFilter>(true).Length, Is.EqualTo(2), "one mesh for the lines, one for the dots");
            foreach (MeshFilter filter in _neck.GetComponentsInChildren<MeshFilter>(true))
            {
                Assert.That(filter.sharedMesh, Is.Not.Null);
                Assert.That(filter.sharedMesh.vertexCount, Is.GreaterThan(0), filter.name);
                Assert.That(filter.GetComponent<MeshRenderer>().sharedMaterial, Is.Not.Null, "the overlay shader was found in Resources");
            }
        }

        [Test]
        public void ADotIsWhereAGhostHandParentedToTheSocketExpectsIt()
        {
            _neck.PlaceFloating(_head);
            var dot = new FingertipDot(6, 95f, -18.82f, 2);
            Vector3 viaSocket = _neck.Socket.TransformPoint(new Vector3(dot.xMm, _layout.stringHeightMm, dot.zMm) * 0.001f);
            Vector3 viaNeck = _neck.transform.TransformPoint(NeckMath.DotLocalPosition(_layout, dot));
            AssertNear(viaSocket, viaNeck, 1e-6f, "socket-local metres = neck-local millimetres / 1000");
        }

        [Test]
        public void PlaceFloating_HangsTheNeckInFrontOfTheHead_AndFollowsIt()
        {
            _head.SetPositionAndRotation(new Vector3(2f, 1.5f, -1f), Quaternion.Euler(10f, 70f, 0f));
            _neck.PlaceFloating(_head);
            Assert.That(_neck.Mode, Is.EqualTo(NeckMode.Floating));
            Assert.That(_neck.transform.parent, Is.SameAs(_head), "a child of the head: it stays put in the view");

            NeckPose expected = NeckPlacement.Floating(_neck.Floating, false);
            AssertNear(_neck.transform.localPosition, expected.position, 1e-5f, "local position");
            AssertNear(_neck.transform.localScale, expected.scale, 1e-6f, "scale");
            Vector3 nutInHead = _head.InverseTransformPoint(_neck.Socket.position);
            Assert.That(nutInHead.magnitude, Is.InRange(0.8f, 1.0f), "eye to nut");
            Assert.That(nutInHead.x, Is.LessThan(0f), "left");
            Assert.That(nutInHead.y, Is.LessThan(0f), "below");
            Assert.That(nutInHead.z, Is.GreaterThan(0.75f), "ahead");
            Vector3 fret5InHead = _head.InverseTransformPoint(_neck.transform.TransformPoint(NeckMath.LocalMetres(_layout.LastFretXMm, 0f, 0f)));
            Assert.That(fret5InHead.x, Is.GreaterThan(nutInHead.x + 0.15f), "nut on the left");

            _head.Rotate(0f, 45f, 0f);
            AssertNear(_head.InverseTransformPoint(_neck.Socket.position), nutInHead, 1e-4f, "turning the head takes the neck along");
        }

        [Test]
        public void PlaceFloating_WithoutFollowing_StaysInTheWorld()
        {
            _head.SetPositionAndRotation(new Vector3(0f, 1.5f, 0f), Quaternion.identity);
            _neck.PlaceFloating(_head, false);
            Assert.That(_neck.transform.parent, Is.SameAs(_world));
            Vector3 nut = _neck.Socket.position;
            Assert.That((nut - _head.position).magnitude, Is.InRange(0.8f, 1.0f));
            _head.Rotate(0f, 45f, 0f);
            AssertNear(_neck.Socket.position, nut, 1e-6f, "the neck did not move with the head");
        }

        [Test]
        public void LeftHanded_MirrorsTheNeckAndEverythingOnItsAnchors()
        {
            _neck.PlaceFloating(_head);
            Vector3 rightNut = _head.InverseTransformPoint(_neck.Socket.position);
            Vector3 rightString1 = _head.InverseTransformPoint(_neck.Socket.TransformPoint(new Vector3(0.095f, 0.002f, 0.0188f)));

            _neck.UseOptions(new NeckOptions { leftHanded = true });
            Assert.That(_neck.transform.localScale.z, Is.LessThan(0f), "mirrored across the neck");
            Assert.That(_neck.transform.localScale.x, Is.GreaterThan(0f));
            Vector3 leftNut = _head.InverseTransformPoint(_neck.Socket.position);
            Vector3 leftString1 = _head.InverseTransformPoint(_neck.Socket.TransformPoint(new Vector3(0.095f, 0.002f, 0.0188f)));
            AssertNear(leftNut, new Vector3(-rightNut.x, rightNut.y, rightNut.z), 1e-5f, "the nut moves to the right of the view");
            AssertNear(leftString1, new Vector3(-rightString1.x, rightString1.y, rightString1.z), 1e-5f, "a point given in the neck frame is mirrored with it");

            _neck.UseOptions(new NeckOptions());
            AssertNear(_head.InverseTransformPoint(_neck.Socket.position), rightNut, 1e-5f, "and back");
        }

        [Test]
        public void SetWorldPose_PutsTheNutThere_AtLifeSize_BackUnderItsOwnParent()
        {
            _neck.PlaceFloating(_head);
            Vector3 nut = new Vector3(-0.25f, 0.9f, 0.4f);
            Quaternion rotation = Quaternion.Euler(-20f, 15f, 8f);
            _neck.SetWorldPose(nut, rotation);

            Assert.That(_neck.Mode, Is.EqualTo(NeckMode.Aligned));
            Assert.That(_neck.transform.parent, Is.SameAs(_world), "no longer a child of the head");
            AssertNear(_neck.Socket.position, nut, 1e-6f, "the socket is on the nut");
            AssertNear(_neck.transform.localScale, new Vector3(1f, 1f, 1f), 1e-6f, "life size");
            AssertNear(_neck.transform.TransformPoint(NeckMath.LocalMetres(100f, 0f, 0f)), nut + rotation * new Vector3(0.1f, 0f, 0f), 1e-6f,
                       "+x runs from the nut toward the bridge");
            AssertNear(_neck.StrumZone.localPosition, NeckMath.LocalMetres(NeckMath.StrumZoneXMm(_layout), _layout.stringHeightMm, 0f), 1e-6f,
                       "aligned: the strum zone is at its real distance");
            _head.Rotate(0f, 30f, 0f);
            AssertNear(_neck.Socket.position, nut, 1e-6f, "the aligned neck stays in the world when the head turns");

            _neck.UseOptions(new NeckOptions { leftHanded = true });
            AssertNear(_neck.transform.localScale, new Vector3(1f, 1f, -1f), 1e-6f, "left-handed: mirrored across the neck, still life size");
            AssertNear(_neck.Socket.position, nut, 1e-6f, "the nut does not move");
        }

        [Test]
        public void SetActiveFinger_AndRepeatedSetDots_AreAccepted()
        {
            _neck.SetActiveFinger(2);
            Assert.That(_neck.ActiveFinger, Is.EqualTo(2));
            _neck.SetActiveFinger(9);
            Assert.That(_neck.ActiveFinger, Is.EqualTo(-1), "anything outside 0-4 means all fingers");

            var dots = new FingertipDot[FloatingNeck.MaxDots];
            var marks = new StringMark[NeckLayout.StringCount];
            int count = DevNeckLayout.GChord(_layout, dots, marks);
            _neck.SetDots(dots, count);   // the same dots again: nothing to do
            Assert.That(_neck.DotCount, Is.EqualTo(3));
            _neck.SetDots(null, 0);
            Assert.That(_neck.DotCount, Is.EqualTo(0));
            Assert.That(_neck.LabelCount, Is.EqualTo(0));
            _neck.SetStringMarks(null);
        }
    }
}
