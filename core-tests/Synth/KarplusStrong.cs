using System;
using UntitledGuitarApp.Core.Theory;

namespace UntitledGuitarApp.Core.Tests.Synth
{
    /// <summary>
    /// Deterministic, seeded Karplus–Strong plucked string for synthetic tests (correctness, never accuracy claims).
    /// Loop = integer delay line N + two-point averager (exactly 0.5 samples of linear-phase delay) + a first-order
    /// allpass fractional delay Δ ∈ [0.1, 1.1) (Jaffe &amp; Smith 1983), so the period P = N + 0.5 + Δ = sampleRate / f0
    /// exactly at low frequencies. Plain integer-delay KS would be up to ±6 cents off on high E at 48 kHz, which would
    /// make a ±3-cent tracker test meaningless; KarplusStrongTests proves the synth's own pitch error is far below 1 cent.
    /// </summary>
    public static class KarplusStrong
    {
        /// <summary>Plucks MIDI note <paramref name="midi"/> (optionally detuned) for <paramref name="ms"/> milliseconds.</summary>
        /// <param name="detuneCents">Fractional detune, positive = sharp.</param>
        /// <param name="t60Sec">Time for the loop to decay by 60 dB (a steel string rings ~3–8 s).</param>
        /// <param name="amplitude">Peak level of the excitation burst (0.5 ≈ −6 dBFS).</param>
        public static float[] Pluck(int midi, int ms, int sampleRate = 48000, int seed = 0,
                                    double detuneCents = 0.0, double t60Sec = 4.0, float amplitude = 0.5f)
        {
            double f0 = Midi.ToHz(midi) * Math.Pow(2.0, detuneCents / 1200.0);
            return PluckHz(f0, ms, sampleRate, seed, t60Sec, amplitude);
        }

        public static float[] PluckHz(double f0, int ms, int sampleRate = 48000, int seed = 0,
                                      double t60Sec = 4.0, float amplitude = 0.5f)
        {
            if (!(f0 > 0) || f0 > sampleRate / 4.0) throw new ArgumentOutOfRangeException(nameof(f0));
            int length = (int)((long)ms * sampleRate / 1000);
            var output = new float[length];

            double period = sampleRate / f0;                 // P = N + 0.5 + Δ
            int n = (int)Math.Floor(period - 0.5 - 0.1);     // keep Δ ≥ 0.1 (allpass stability / flat phase delay)
            double delta = period - 0.5 - n;                 // Δ ∈ [0.1, 1.1)
            double c = (1.0 - delta) / (1.0 + delta);        // allpass coefficient: phase delay ≈ Δ at low ω
            double rho = Math.Pow(10.0, -3.0 / (t60Sec * f0)); // per-period loop gain for the requested T60

            // Excitation: seeded white noise, DC removed, scaled to the requested peak.
            var line = new double[n];
            var rng = new XorShift32(seed);
            double mean = 0;
            for (int i = 0; i < n; i++) { line[i] = rng.NextBipolar(); mean += line[i]; }
            mean /= n;
            double peak = 1e-12;
            for (int i = 0; i < n; i++) { line[i] -= mean; peak = Math.Max(peak, Math.Abs(line[i])); }
            for (int i = 0; i < n; i++) line[i] *= amplitude / peak;

            int idx = 0;
            double prev = 0, apX1 = 0, apY1 = 0;
            for (int t = 0; t < length; t++)
            {
                double y = line[idx];
                output[t] = (float)y;
                double avg = rho * 0.5 * (y + prev);         // two-point average: 0.5-sample linear-phase delay
                prev = y;
                double ap = c * avg + apX1 - c * apY1;       // H(z) = (c + z⁻¹) / (1 + c z⁻¹)
                apX1 = avg; apY1 = ap;
                line[idx] = ap;
                idx++; if (idx == n) idx = 0;
            }
            return output;
        }
    }

    /// <summary>Tiny deterministic PRNG (same sequence on every runtime, unlike System.Random).</summary>
    public struct XorShift32
    {
        uint _s;
        public XorShift32(int seed) { _s = (uint)seed * 2654435761u + 0x9E3779B9u; if (_s == 0) _s = 1; }
        public uint NextUInt() { _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5; return _s; }
        /// <summary>Uniform in [−1, 1).</summary>
        public double NextBipolar() => NextUInt() / 2147483648.0 - 1.0;
        /// <summary>Approximately standard normal (sum of 4 uniforms, scaled to unit variance).</summary>
        public double NextGaussian()
        {
            double s = 0; for (int i = 0; i < 4; i++) s += NextBipolar();
            return s * Math.Sqrt(3.0 / 4.0);
        }
    }

    /// <summary>Other deterministic test signals.</summary>
    public static class Signals
    {
        /// <summary>Additive harmonic tone: amplitudes[k] is the amplitude of partial k+1; exponential decay with T60.</summary>
        public static float[] Harmonic(double f0, double[] amplitudes, int ms, int sampleRate = 48000, double t60Sec = 4.0)
        {
            int length = (int)((long)ms * sampleRate / 1000);
            var o = new float[length];
            double decayPerSample = Math.Pow(10.0, -3.0 / (t60Sec * sampleRate));
            double env = 1.0;
            for (int t = 0; t < length; t++)
            {
                double s = 0;
                for (int k = 0; k < amplitudes.Length; k++)
                {
                    double f = f0 * (k + 1);
                    if (f >= sampleRate / 2.0) break;
                    s += amplitudes[k] * Math.Sin(2 * Math.PI * f * t / sampleRate + 0.3 * k);
                }
                o[t] = (float)(env * s);
                env *= decayPerSample;
            }
            return o;
        }

        /// <summary>Adds <paramref name="src"/>·gain into <paramref name="dst"/> starting at <paramref name="at"/> (clipped to dst).</summary>
        public static void MixInto(float[] dst, float[] src, int at, float gain)
        {
            for (int i = 0; i < src.Length; i++)
            {
                int j = at + i;
                if (j < 0) continue;
                if (j >= dst.Length) break;
                dst[j] += gain * src[i];
            }
        }

        public static float[] WhiteNoise(int ms, double rms, int seed, int sampleRate = 48000)
        {
            int length = (int)((long)ms * sampleRate / 1000);
            var o = new float[length];
            var rng = new XorShift32(seed);
            for (int t = 0; t < length; t++) o[t] = (float)(rms * rng.NextGaussian());
            return o;
        }

        /// <summary>Adds seeded white noise in place so that signal RMS / noise RMS = <paramref name="snrDb"/> over [from, to).</summary>
        public static void AddNoise(float[] x, double snrDb, int seed, int from, int to)
        {
            double e = 0; for (int i = from; i < to; i++) e += (double)x[i] * x[i];
            double rms = Math.Sqrt(e / Math.Max(1, to - from));
            double noiseRms = rms / Math.Pow(10.0, snrDb / 20.0);
            var rng = new XorShift32(seed);
            for (int i = 0; i < x.Length; i++) x[i] += (float)(noiseRms * rng.NextGaussian());
        }

        /// <summary>
        /// Independent long-window reference frequency estimator (not YIN): complex-demodulates the signal at
        /// <paramref name="nominalHz"/> in two Hann windows <paramref name="gapSec"/> apart and converts the phase advance
        /// into a frequency offset. Unambiguous while |f − nominal| &lt; 1 / (2·gap); with gap = 0.5 s that is ±1 Hz.
        /// </summary>
        public static double ReferenceHz(float[] x, double nominalHz, int sampleRate, double startSec, double windowSec, double gapSec)
        {
            double PhaseAt(int start, int len)
            {
                double re = 0, im = 0;
                for (int i = 0; i < len; i++)
                {
                    double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (len - 1));
                    double ph = 2 * Math.PI * nominalHz * (start + i) / sampleRate;
                    re += w * x[start + i] * Math.Cos(ph);
                    im -= w * x[start + i] * Math.Sin(ph);
                }
                return Math.Atan2(im, re);
            }
            int len = (int)(windowSec * sampleRate);
            int a = (int)(startSec * sampleRate);
            int gap = (int)(gapSec * sampleRate);
            double dPhi = PhaseAt(a + gap, len) - PhaseAt(a, len);
            while (dPhi > Math.PI) dPhi -= 2 * Math.PI;
            while (dPhi < -Math.PI) dPhi += 2 * Math.PI;
            return nominalHz + dPhi / (2 * Math.PI * gap / (double)sampleRate);
        }
    }
}
