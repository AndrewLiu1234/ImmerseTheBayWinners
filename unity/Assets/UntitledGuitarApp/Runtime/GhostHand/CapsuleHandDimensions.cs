// CapsuleHandDimensions: the default proportions (metres) of an adult hand, in the palm's local frame, for CapsuleHand.
// Plain C#, no Unity object. The left hand is the exact mirror image (x -> -x) of the right hand.
//
// PALM-LOCAL FRAME (right hand; the left hand mirrors x)
//   +z = forward, from the wrist toward the knuckles (the direction the straight fingers point)
//   +y = dorsal, out of the back of the hand;  the palm side is -y, and it is the direction every finger curls toward
//   +x = across the hand toward the little finger; the right thumb is on -x
//   origin = the wrist (the centre of the wrist joint); the knuckle line is about 9 cm ahead of it.
//
// FINGER ORDER everywhere in this code: 0 = index, 1 = middle, 2 = ring, 3 = little (= fingers 1-4 of a chord diagram), 4 = thumb.
using UnityEngine;

namespace UntitledGuitarApp.Runtime.GhostHand
{
    public sealed class CapsuleHandDimensions
    {
        public const int FingerCount = 5;       // index, middle, ring, little, thumb
        public const int ThumbIndex = 4;
        public const int BonesPerFinger = 3;    // three capsule phalanges per finger (the thumb: metacarpal, proximal, distal)

        /// <summary>One cross-section of the palm slab (an ellipse), from the forearm end to the knuckle line.</summary>
        public struct PalmStation
        {
            public float z;             // along the palm, wrist = 0
            public float halfWidth;     // x radius
            public float halfThickness; // y radius
            public float fadeOut;       // 0 = fully drawn, 1 = fully faded (the forearm stub fades into nothing)
            public PalmStation(float z, float halfWidth, float halfThickness, float fadeOut)
            { this.z = z; this.halfWidth = halfWidth; this.halfThickness = halfThickness; this.fadeOut = fadeOut; }
        }

        public readonly bool leftHand;

        /// <summary>Per finger: knuckle, curl plane (forward, curl), bone lengths, joint limits. Palm-local, unscaled.</summary>
        public readonly FingerIkChain[] chains = new FingerIkChain[FingerCount];

        /// <summary>Thickness (radius) at joint 0..3 of each finger, index finger*4 + joint; joint 3 is the fingertip centre.
        /// A bone runs from joint j (radius j) to joint j+1 (radius j+1), so neighbouring bones meet with equal radius.</summary>
        public readonly float[] radius = new float[FingerCount * 4];

        /// <summary>Loose, unused-finger curl (radians): first-joint and second-joint flexion. The third follows by the coupling.</summary>
        public readonly float[] relaxedMcp = new float[FingerCount];
        public readonly float[] relaxedPip = new float[FingerCount];

        /// <summary>Palm-local fingertip centre of the relaxed pose of each finger.</summary>
        public readonly Vector3[] relaxedTip = new Vector3[FingerCount];

        /// <summary>The palm slab, forearm end first. The last station is the knuckle line; the slab ends in a rounded edge.</summary>
        public readonly PalmStation[] palm;
        public readonly float palmEndCapDepth = 0.012f;

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
            float[] r0 =     {  0.0090f,     0.0092f,     0.0088f,     0.0078f };
            float[] r1 =     {  0.0082f,     0.0084f,     0.0080f,     0.0070f };
            float[] r2 =     {  0.0074f,     0.0076f,     0.0072f,     0.0063f };
            float[] r3 =     {  0.0068f,     0.0070f,     0.0066f,     0.0058f };

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
                relaxedMcp[i] = 0.40f;
                relaxedPip[i] = 0.60f;
            }

            // thumb: base joint at the radial side of the wrist; it points forward and out, and curls across the palm
            {
                FingerIkChain c;
                c.knuckle = new Vector3(-0.021f, -0.004f, 0.018f);
                c.forward = new Vector3(-0.60f, -0.15f, 0.78f);
                c.curl = new Vector3(0.70f, -0.60f, 0.25f);
                c.length1 = 0.045f; c.length2 = 0.033f; c.length3 = 0.027f;
                c.limits = FingerIkLimits.Thumb;
                chains[ThumbIndex] = c;
                radius[ThumbIndex * 4 + 0] = 0.0115f; radius[ThumbIndex * 4 + 1] = 0.0102f;
                radius[ThumbIndex * 4 + 2] = 0.0090f; radius[ThumbIndex * 4 + 3] = 0.0080f;
                relaxedMcp[ThumbIndex] = 0.15f;
                relaxedPip[ThumbIndex] = 0.30f;
            }

            palm = new PalmStation[]
            {
                new PalmStation(-0.075f, 0.029f, 0.024f, 1.00f),    // forearm end: faded out completely
                new PalmStation(-0.040f, 0.029f, 0.022f, 0.55f),
                new PalmStation( 0.000f, 0.029f, 0.019f, 0.00f),    // wrist
                new PalmStation( 0.050f, 0.036f, 0.0155f, 0.00f),
                new PalmStation( 0.092f, 0.043f, 0.0125f, 0.00f),   // knuckle line
            };

            if (leftHand) MirrorX();

            for (int i = 0; i < FingerCount; i++)
            {
                FingerIkResult r;
                FingerIk.Pose(chains[i], 0f, relaxedMcp[i], relaxedPip[i], out r);
                relaxedTip[i] = r.tip;
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
            }
        }
    }
}
