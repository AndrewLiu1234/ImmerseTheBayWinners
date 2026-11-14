using System;
using UntitledGuitarApp.Core.Theory;

namespace UntitledGuitarApp.Core.Tests.Synth
{
    /// <summary>The pluck-check fault classes the synthetic grid injects (MASTER_PLAN §19 / Appendix B2).</summary>
    public enum PluckFault { Correct, OpenString, FretLow, FretHigh, NeighbourString, Muted, ShortRing, Silence }

    /// <summary>
    /// Deterministic pluck-check scenes: <see cref="PreSec"/> of pre-roll, an onset, then <see cref="PostSec"/> of one
    /// Karplus–Strong pluck, optionally over a string that still rings from an earlier pluck, plus seeded white noise at a fixed
    /// room level. SNR is defined against a reference pluck (G3, amplitude 0.5) over the analysis window — like a real room,
    /// the noise floor does not shrink when the player plucks softly or mutes the string.
    /// </summary>
    public static class PluckScenes
    {
        public const int Sr = 48000;
        public const double PreSec = 0.25;
        public const double PostSec = 1.0;
        public static int OnsetIndex => (int)(PreSec * Sr);

        /// <summary>T60 of a free steel string (s).</summary>
        public const double FreeT60 = 4.0;
        /// <summary>T60 of a finger-deadened string: one period of excitation, then a thud.</summary>
        public const double MutedT60 = 0.05;
        /// <summary>T60 of a string a neighbouring finger leans on: right pitch, loses 30 dB within 0.4 s (half the ring-out
        /// target; a free string needs T60 ≥ 1.6 s to pass AudioConfig.RingDropDb at RingOutSec).</summary>
        public const double ShortT60 = 0.8;

        static double _refRms = -1;

        /// <summary>RMS of the reference pluck (G3, amplitude 0.5, free) over 40–240 ms after its onset.</summary>
        public static double ReferenceRms
        {
            get
            {
                if (_refRms > 0) return _refRms;
                var p = KarplusStrong.Pluck(55, 300, Sr, seed: 999, t60Sec: FreeT60);
                int a = (int)(0.04 * Sr), b = (int)(0.24 * Sr);
                double e = 0; for (int i = a; i < b; i++) e += (double)p[i] * p[i];
                _refRms = Math.Sqrt(e / (b - a));
                return _refRms;
            }
        }

        /// <summary>The MIDI note a fault class sounds when the string should give <paramref name="expected"/>; −1 = no note.</summary>
        public static int FaultMidi(PluckFault fault, int stringNumber, int expected, ChordShape chord, int neighbour)
        {
            switch (fault)
            {
                case PluckFault.OpenString: return Tuning.StandardMidi(stringNumber);
                case PluckFault.FretLow: return expected - 1;
                case PluckFault.FretHigh: return expected + 1;
                case PluckFault.NeighbourString:
                    int nm = chord != null ? chord.MidiOn(neighbour) : -1;
                    return nm == ChordShape.Muted ? Tuning.StandardMidi(neighbour) : nm;
                case PluckFault.Silence: return -1;
                default: return expected;
            }
        }

        /// <summary>Builds a scene. <paramref name="midi"/> −1 = no pluck (silence or noise only).</summary>
        /// <param name="ringingMidi">An earlier note still ringing (plucked <paramref name="ringingAgoSec"/> before the onset); −1 = none.</param>
        public static float[] Scene(int midi, double t60, int seed, double snrDb = double.PositiveInfinity, float amplitude = 0.5f,
                                    int ringingMidi = -1, double ringingAgoSec = 0.4, double postSec = PostSec)
        {
            int onset = OnsetIndex;
            var x = new float[onset + (int)(postSec * Sr)];
            if (ringingMidi >= 0)
            {
                int at = Math.Max(0, onset - (int)(ringingAgoSec * Sr));
                var r = KarplusStrong.Pluck(ringingMidi, (int)((x.Length - at) * 1000L / Sr), Sr, seed: seed + 7777, t60Sec: FreeT60);
                Signals.MixInto(x, r, at, 1f);
            }
            if (midi >= 0)
            {
                var p = KarplusStrong.Pluck(midi, (int)(postSec * 1000), Sr, seed: seed, t60Sec: t60, amplitude: amplitude);
                Signals.MixInto(x, p, onset, 1f);
            }
            if (!double.IsPositiveInfinity(snrDb))
            {
                double noiseRms = ReferenceRms / Math.Pow(10.0, snrDb / 20.0);
                var rng = new XorShift32(seed * 31 + 5);
                for (int i = 0; i < x.Length; i++) x[i] += (float)(noiseRms * rng.NextGaussian());
            }
            return x;
        }

        public static double T60For(PluckFault fault) =>
            fault == PluckFault.Muted ? MutedT60 : fault == PluckFault.ShortRing ? ShortT60 : FreeT60;
    }
}
