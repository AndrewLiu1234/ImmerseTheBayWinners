// CapsuleHandDimensions: the default proportions (metres) of an adult hand, in the palm's local frame, for CapsuleHand.
// Plain C#, no Unity object. The left hand is the exact mirror image (x -> -x) of the right hand.
//
// PALM-LOCAL FRAME (right hand; the left hand mirrors x). This is the local space of the "wrist" bone.
//   +z = forward, from the wrist toward the knuckles (the direction the straight fingers point)
//   +y = dorsal, out of the back of the hand;  the palm side is -y, and it is the direction every finger curls toward
//   +x = across the hand toward the little finger; the right thumb is on -x
//   origin = the wrist (the centre of the wrist joint); the knuckle line is about 9 cm ahead of it.
//
// FINGER ORDER everywhere in this code: 0 = index, 1 = middle, 2 = ring, 3 = little (= fingers 1-4 of a chord diagram), 4 = thumb.
//
// BONE NAMES (pinned for every hand rig in the project, parents first): see BoneNames. For the thumb, "proximal" is the
// metacarpal (it hinges at the wrist), "intermediate" the proximal phalanx and "distal" the distal phalanx.
using UnityEngine;

namespace UntitledGuitarApp.Runtime.GhostHand
{
    public sealed class CapsuleHandDimensions
    {
        public const int FingerCount = 5;       // index, middle, ring, little, thumb
        public const int ThumbIndex = 4;
        public const int BonesPerFinger = 3;    // three phalanges per finger (the thumb: metacarpal, proximal, distal)
        public const int BoneCount = 16;

        /// <summary>The 16 pinned bone names, parents first. Index 0 is the wrist; then thumb, index, middle, ring, pinky.</summary>
        public static readonly string[] BoneNames =
        {
            "wrist",
            "thumb_proximal", "thumb_intermediate", "thumb_distal",
            "index_proximal", "index_intermediate", "index_distal",
            "middle_proximal", "middle_intermediate", "middle_distal",
            "ring_proximal", "ring_intermediate", "ring_distal",
            "pinky_proximal", "pinky_intermediate", "pinky_distal",
        };

        /// <summary>Index into BoneNames of a finger's bone. finger: 0 = index .. 3 = little, 4 = thumb. joint: 0 = proximal, 1 = intermediate, 2 = distal.</summary>
        public static int BoneIndex(int finger, int joint)
        {
            int block = finger == ThumbIndex ? 0 : finger + 1;      // the thumb's three names come first, right after the wrist
            return 1 + block * BonesPerFinger + joint;
        }

        public static string BoneName(int finger, int joint) { return BoneNames[BoneIndex(finger, joint)]; }

        /// <summary>One cross-section of the palm (a rounded slab), from the forearm end to the knuckle line.</summary>
        public struct PalmStation
        {
            public float z;             // along the palm, wrist = 0
            public float halfWidth;     // x radius
            public float halfThickness; // y radius
            public float alpha;         // how strongly this ring is drawn: 1 = full, 0 = invisible (both ends fade into nothing)
            public PalmStation(float z, float halfWidth, float halfThickness, float alpha)
            { this.z = z; this.halfWidth = halfWidth; this.halfThickness = halfThickness; this.alpha = alpha; }
        }

        public readonly bool leftHand;

        /// <summary>Per finger: knuckle, curl plane (forward, curl), bone lengths, joint limits. Palm-local, unscaled.</summary>
        public readonly FingerIkChain[] chains = new FingerIkChain[FingerCount];

        /// <summary>Thickness (radius) at joint 0..3 of each finger, index finger*4 + joint; joint 3 is the fingertip centre.</summary>
        public readonly float[] radius = new float[FingerCount * 4];

        /// <summary>Where each finger's skin starts inside the palm (the fingers run back along their metacarpals toward the
        /// wrist and fade out there, so the knuckle is a rounded bend of one continuous skin). For the thumb this is its first joint.</summary>
        public readonly Vector3[] skinStart = new Vector3[FingerCount];
        public readonly float[] skinStartRadius = new float[FingerCount];

        /// <summary>Loose curl of a free hand (radians): first-joint and second-joint flexion. The third follows by the coupling.</summary>
        public readonly float[] relaxedMcp = new float[FingerCount];
        public readonly float[] relaxedPip = new float[FingerCount];

        /// <summary>Curl of a finger that hovers over the strings without fretting (the fretting hand's unused fingers).</summary>
        public readonly float[] hoverMcp = new float[FingerCount];
        public readonly float[] hoverPip = new float[FingerCount];

        /// <summary>Palm-local fingertip centre of the relaxed / hovering pose of each finger.</summary>
        public readonly Vector3[] relaxedTip = new Vector3[FingerCount];
        public readonly Vector3[] hoverTip = new Vector3[FingerCount];

        /// <summary>The palm slab, forearm end first. Its cross-section is a superellipse (exponent 2 = ellipse, larger = boxier).</summary>
        public readonly PalmStation[] palm;
        public readonly float palmExponent = 2.5f;

        public static CapsuleHandDimensions CreateDefault(bool leftHand)
        {
            return new CapsuleHandDimensions(leftHand);
        }

        public float Radius(int finger, int joint) { return radius[finger * 4 + joint]; }

        /// <summary>Radius of the fingertip (joint 3) of a finger.</summary>
        public float TipRadius(int finger) { return radius[finger * 4 + 3]; }

        CapsuleHandDimensions(bool leftHand)
        {
            this.leftHand = leftHand;

            // ---- right-hand data: knuckle x/z, splay (degrees, toward +x), bone lengths, radii at joints 0..3 ----
            //                index        middle       ring         little
            float[] kx =     { -0.0295f,    -0.0100f,     0.0100f,     0.0285f };
            float[] kz =     {  0.0925f,     0.0970f,     0.0930f,     0.0850f };
            float[] splay =  { -3f,          -1f,          2f,          6f };
            float[] l1 =     {  0.043f,      0.048f,      0.044f,      0.035f };
            float[] l2 =     {  0.025f,      0.028f,      0.027f,      0.020f };
            float[] l3 =     {  0.020f,      0.021f,      0.020f,      0.018f };
            float[] r0 =     {  0.0094f,     0.0096f,     0.0092f,     0.0082f };   // the knuckle: a little proud of the finger
            float[] r1 =     {  0.0082f,     0.0084f,     0.0080f,     0.0070f };
            float[] r2 =     {  0.0074f,     0.0076f,     0.0072f,     0.0063f };
            float[] r3 =     {  0.0068f,     0.0070f,     0.0066f,     0.0058f };
            // the metacarpals fan out from the wrist: each finger's skin starts part of the way back toward this point
            float[] cx =     { -0.0130f,    -0.0045f,     0.0045f,     0.0130f };
            const float carpalZ = 0.010f;
            const float skinBack = 0.42f;       // fraction of the knuckle -> carpal distance covered by the skin

            float[] relMcp = { 0.32f, 0.40f, 0.47f, 0.54f };
            float[] relPip = { 0.52f, 0.60f, 0.66f, 0.72f };
            float[] hovMcp = { 0.72f, 0.76f, 0.80f, 0.84f };
            float[] hovPip = { 1.00f, 1.04f, 1.06f, 1.08f };

            for (int i = 0; i < 4; i++)
            {
                float a = splay[i] * Mathf.Deg2Rad;
                FingerIkChain c;
                c.knuckle = new Vector3(kx[i], 0f, kz[i]);
                c.forward = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                c.curl = new Vector3(0f, -1f, 0f);
                c.length1 = l1[i]; c.length2 = l2[i]; c.length3 = l3[i];
                c.limits = FingerIkLimits.Finger;
                chains[i] = c;
                radius[i * 4 + 0] = r0[i]; radius[i * 4 + 1] = r1[i]; radius[i * 4 + 2] = r2[i]; radius[i * 4 + 3] = r3[i];
                Vector3 carpal = new Vector3(cx[i], 0f, carpalZ);
                skinStart[i] = Vector3.Lerp(c.knuckle, carpal, skinBack);
                skinStartRadius[i] = r0[i] * 0.80f;
                relaxedMcp[i] = relMcp[i]; relaxedPip[i] = relPip[i];
                hoverMcp[i] = hovMcp[i]; hoverPip[i] = hovPip[i];
            }

            // thumb: base joint at the radial side of the wrist; it points forward and out, and curls across the palm
            {
                FingerIkChain c;
                c.knuckle = new Vector3(-0.021f, -0.004f, 0.018f);
                c.forward = new Vector3(-0.60f, -0.15f, 0.78f);
                c.curl = new Vector3(0.70f, -0.60f, 0.25f);
                c.length1 = 0.045f; c.length2 = 0.033f; c.length3 = 0.024f;
                c.limits = FingerIkLimits.Thumb;
                chains[ThumbIndex] = c;
                radius[ThumbIndex * 4 + 0] = 0.0118f; radius[ThumbIndex * 4 + 1] = 0.0102f;
                radius[ThumbIndex * 4 + 2] = 0.0090f; radius[ThumbIndex * 4 + 3] = 0.0080f;
                skinStart[ThumbIndex] = c.knuckle;
                skinStartRadius[ThumbIndex] = radius[ThumbIndex * 4 + 0];
                relaxedMcp[ThumbIndex] = 0.15f; relaxedPip[ThumbIndex] = 0.30f;
                hoverMcp[ThumbIndex] = 0.15f; hoverPip[ThumbIndex] = 0.30f;
            }

            palm = new PalmStation[]
            {
                new PalmStation(-0.070f, 0.0265f, 0.0205f, 0.00f),  // forearm end: faded out completely
                new PalmStation(-0.046f, 0.0268f, 0.0202f, 0.22f),
                new PalmStation(-0.022f, 0.0275f, 0.0194f, 0.50f),
                new PalmStation( 0.000f, 0.0290f, 0.0182f, 0.74f),  // wrist
                new PalmStation( 0.022f, 0.0330f, 0.0170f, 0.92f),
                new PalmStation( 0.045f, 0.0368f, 0.0155f, 1.00f),
                new PalmStation( 0.064f, 0.0392f, 0.0140f, 0.84f),
                new PalmStation( 0.081f, 0.0400f, 0.0125f, 0.42f),
                new PalmStation( 0.097f, 0.0395f, 0.0110f, 0.00f),  // knuckle line: the fingers' own skin has taken over
            };

            if (leftHand) MirrorX();

            for (int i = 0; i < FingerCount; i++)
            {
                FingerIkResult r;
                FingerIk.Pose(chains[i], 0f, relaxedMcp[i], relaxedPip[i], out r);
                relaxedTip[i] = r.tip;
                FingerIk.Pose(chains[i], 0f, hoverMcp[i], hoverPip[i], out r);
                hoverTip[i] = r.tip;
            }
        }

        // The left hand is the right hand reflected through the plane x = 0 (every vector that has a side loses its sign on x).
        void MirrorX()
        {
            for (int i = 0; i < FingerCount; i++)
            {
                FingerIkChain c = chains[i];
                c.knuckle.x = -c.knuckle.x;
                c.forward.x = -c.forward.x;
                c.curl.x = -c.curl.x;
                chains[i] = c;
                skinStart[i].x = -skinStart[i].x;
            }
        }
    }
}
