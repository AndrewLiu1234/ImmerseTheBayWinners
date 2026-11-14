using System;

namespace UntitledGuitarApp.Core.Theory
{
    /// <summary>MIDI note ↔ frequency helpers (A4 = MIDI 69 = 440 Hz; f = 440·2^((m−69)/12), MASTER_PLAN Appendix B1).</summary>
    public static class Midi
    {
        /// <summary>Concert pitch reference. Fixed: the app assumes standard A440 tuning.</summary>
        public const double A4Hz = 440.0;
        public const int A4Midi = 69;

        static readonly string[] SharpNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        public static double ToHz(double midi) => A4Hz * Math.Pow(2.0, (midi - A4Midi) / 12.0);

        /// <summary>Fractional MIDI number for a frequency; NaN for non-positive or non-finite input (never throws).</summary>
        public static double FromHz(double hz)
        {
            if (!(hz > 0.0) || double.IsInfinity(hz)) return double.NaN;
            return A4Midi + 12.0 * Math.Log(hz / A4Hz, 2.0);
        }

        /// <summary>Cents from <paramref name="referenceHz"/> to <paramref name="hz"/> (positive = sharp). NaN on invalid input.</summary>
        public static double CentsBetween(double referenceHz, double hz)
        {
            if (!(hz > 0.0) || !(referenceHz > 0.0) || double.IsInfinity(hz) || double.IsInfinity(referenceHz)) return double.NaN;
            return 1200.0 * Math.Log(hz / referenceHz, 2.0);
        }

        /// <summary>Scientific pitch name with sharps, e.g. 40 → "E2", 56 → "G#3".</summary>
        public static string Name(int midi)
        {
            int pc = ((midi % 12) + 12) % 12;
            int octave = (int)Math.Floor(midi / 12.0) - 1;
            return SharpNames[pc] + octave;
        }
    }
}
