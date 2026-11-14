// FingertipDot and StringMark: what a chord looks like on the neck, as data in the neck frame (see NeckLayout.cs).
// Core's ChordLibrary + FretboardGeometry (Person 2) produce these; the visual only draws them.
using System;

namespace UntitledGuitarApp.Runtime.Neck
{
    /// <summary>Where one fingertip presses, in neck-local millimetres, and which finger it is.</summary>
    [Serializable]
    public struct FingertipDot
    {
        /// <summary>1 (high E) to 6 (low E).</summary>
        public int stringNumber;

        /// <summary>Along the neck from the nut, in mm.</summary>
        public float xMm;

        /// <summary>Across the neck, in mm; + is toward string 1 (high E).</summary>
        public float zMm;

        /// <summary>0 = no finger number; 1 index, 2 middle, 3 ring, 4 little.</summary>
        public int finger;

        public FingertipDot(int stringNumber, float xMm, float zMm, int finger)
        {
            this.stringNumber = stringNumber;
            this.xMm = xMm;
            this.zMm = zMm;
            this.finger = finger;
        }
    }

    /// <summary>The mark drawn at the nut end of one string.</summary>
    public enum StringMark : byte
    {
        /// <summary>Nothing (the string is fretted).</summary>
        None = 0,

        /// <summary>O, white: play the string open.</summary>
        Open = 1,

        /// <summary>X, red: do not play the string. The string itself is dimmed too.</summary>
        Muted = 2,
    }

    /// <summary>Whether the neck hangs in front of the player or sits on the real neck.</summary>
    public enum NeckMode
    {
        /// <summary>Hangs in front of the player (headLocked fallback and judge profile).</summary>
        Floating = 0,

        /// <summary>Placed on the real neck by another component (NeckAligner) through FloatingNeck.SetWorldPose.</summary>
        Aligned = 1,
    }
}
