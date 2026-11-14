using System;
using System.Collections.Generic;
using UntitledGuitarApp.Core.Theory;

namespace UntitledGuitarApp.Core.Tests.Synth
{
    /// <summary>The strum faults the synthetic grid injects (MASTER_PLAN §19 evaluation, Appendix B2).</summary>
    public enum StrumMistakeKind
    {
        /// <summary>Missing finger: the fretted string sounds open.</summary>
        OpenString,
        /// <summary>The finger sits <see cref="StrumMistake.FretDelta"/> frets off its target.</summary>
        WrongFret,
        /// <summary>A finger deadens the string: a short thud, no ring.</summary>
        MutedString,
        /// <summary>A should-be-silent bass string is strummed open.</summary>
        ExtraString,
    }

    public readonly struct StrumMistake
    {
        public readonly StrumMistakeKind Kind;
        /// <summary>1 = high E … 6 = low E.</summary>
        public readonly int String;
        /// <summary>WrongFret only: +1 = one fret too high (sharp), −1 = one fret too low.</summary>
        public readonly int FretDelta;

        public StrumMistake(StrumMistakeKind kind, int stringNumber, int fretDelta = 0) { Kind = kind; String = stringNumber; FretDelta = fretDelta; }

        public static StrumMistake Open(int s) => new StrumMistake(StrumMistakeKind.OpenString, s);
        public static StrumMistake Fret(int s, int delta) => new StrumMistake(StrumMistakeKind.WrongFret, s, delta);
        public static StrumMistake Mute(int s) => new StrumMistake(StrumMistakeKind.MutedString, s);
        public static StrumMistake Extra(int s) => new StrumMistake(StrumMistakeKind.ExtraString, s);

        public override string ToString() =>
            Kind == StrumMistakeKind.WrongFret ? $"fret{(FretDelta > 0 ? "+" : "")}{FretDelta}@s{String}" :
            Kind == StrumMistakeKind.OpenString ? $"open@s{String}" :
            Kind == StrumMistakeKind.MutedString ? $"mute@s{String}" : $"extra@s{String}";
    }

    /// <summary>
    /// Deterministic strum scenes for the strum check. Per string: seeded level (±25 %), seeded Karplus–Strong excitation, free
    /// ring (T60 4 s); muted strings give a 50 ms-T60 thud at a third of the level; extra strings ring open. Down-strum order
    /// 6 → 1 with a seeded ±25 % jitter on the spread. Synthetic correctness only — never an accuracy claim.
    /// </summary>
    public static class StrumScenes
    {
        public const int Sr = 48000;
        public const double PreSec = 0.30;
        public const double PostSec = 0.60;
        public static int OnsetIndex => (int)(PreSec * Sr);
        public const double FreeT60 = 4.0;
        public const double MutedT60 = 0.05;

        /// <summary>MIDI per string (index 0 = string 6) the strum actually sounds; −1 = silent, −2 = muted thud.</summary>
        public static int[] SoundedNotes(ChordShape chord, StrumMistake[] mistakes)
        {
            var notes = new int[Tuning.StringCount];
            for (int s = Tuning.StringCount; s >= 1; s--) notes[Tuning.StringCount - s] = chord.MidiOn(s);
            if (mistakes == null) return notes;
            foreach (var m in mistakes)
            {
                int i = Tuning.StringCount - m.String, open = Tuning.StandardMidi(m.String);
                switch (m.Kind)
                {
                    case StrumMistakeKind.OpenString: notes[i] = open; break;
                    case StrumMistakeKind.WrongFret: notes[i] = Math.Max(open, chord.MidiOn(m.String) + m.FretDelta); break;
                    case StrumMistakeKind.MutedString: notes[i] = -2; break;
                    case StrumMistakeKind.ExtraString: notes[i] = open; break;
                }
            }
            return notes;
        }

        public static float[] Render(ChordShape chord, StrumMistake[] mistakes, double spreadMs, double snrDb, int seed,
                                     int sampleRate = Sr, double brightness = 1.0, double detuneCents = 0.0)
        {
            var notes = SoundedNotes(chord, mistakes);
            int onset = (int)(PreSec * sampleRate);
            var x = new float[onset + (int)(PostSec * sampleRate)];
            var rng = new XorShift32(seed * 7919 + 13);
            double spread = spreadMs * (1 + 0.25 * rng.NextBipolar());
            var order = new List<int>();
            for (int i = 0; i < notes.Length; i++) if (notes[i] != -1) order.Add(i);
            for (int k = 0; k < order.Count; k++)
            {
                int i = order[k];
                double dt = order.Count > 1 ? spread / 1000.0 * k / (order.Count - 1) : 0;
                int at = onset + (int)Math.Round(dt * sampleRate);
                float amp = (float)(0.3 * (1 + 0.25 * rng.NextBipolar()));
                int sseed = (int)rng.NextUInt();
                double det = detuneCents * rng.NextBipolar();
                float[] p;
                if (notes[i] == -2)
                {
                    int expected = chord.MidiOn(Tuning.StringCount - i);
                    if (expected < 0) expected = Tuning.StandardMidi(Tuning.StringCount - i);
                    p = KarplusStrong.Pluck(expected, (int)(PostSec * 1000), sampleRate, sseed, det, MutedT60, amp / 3f, brightness);
                }
                else p = KarplusStrong.Pluck(notes[i], (int)(PostSec * 1000), sampleRate, sseed, det, FreeT60, amp, brightness);
                Signals.MixInto(x, p, at, 1f);
            }
            if (!double.IsPositiveInfinity(snrDb))
            {
                int a = onset + (int)(0.03 * sampleRate), b = onset + (int)(0.25 * sampleRate);
                Signals.AddNoise(x, snrDb, seed * 31 + 7, a, b);
            }
            return x;
        }

        /// <summary>White noise only (no strum), at the level a 10 dB-SNR strum would have.</summary>
        public static float[] NoiseOnly(int seed, double rms = 0.05, int sampleRate = Sr)
        {
            var x = new float[(int)((PreSec + PostSec) * sampleRate)];
            var rng = new XorShift32(seed);
            for (int i = 0; i < x.Length; i++) x[i] = (float)(rms * rng.NextGaussian());
            return x;
        }
    }
}
