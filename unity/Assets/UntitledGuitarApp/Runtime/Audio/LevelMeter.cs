using System;

namespace UntitledGuitarApp.Runtime.Audio
{
    /// <summary>
    /// Smoothed input level for the mic meter. Plain C#, no allocation. Levels are LINEAR amplitude (0 = silence,
    /// 1 = full scale); the HUD converts to dB or a bar length.
    /// RMS rises fast (so a strum shows at once) and falls slowly (so the bar is readable); peak jumps up instantly and
    /// falls slower still. The time constants are measured in AUDIO time (frames / sample rate), so the meter behaves the
    /// same at 30 fps and at 90 fps.
    /// </summary>
    public sealed class LevelMeter
    {
        /// <summary>RMS rise time constant (seconds): about 30 ms, so a pluck registers within a couple of frames.</summary>
        public const float RmsAttackSec = 0.03f;
        /// <summary>RMS fall time constant (seconds): 300 ms, slow enough to read at a glance.</summary>
        public const float RmsReleaseSec = 0.30f;
        /// <summary>Peak fall time constant (seconds): 600 ms; the peak itself rises instantly.</summary>
        public const float PeakReleaseSec = 0.60f;

        /// <summary>Smoothed RMS, linear 0..1.</summary>
        public float Rms { get; private set; }
        /// <summary>Smoothed peak, linear 0..1.</summary>
        public float Peak { get; private set; }

        /// <summary>
        /// Feeds one block (everything delivered during one frame). <paramref name="sumSquares"/> is the sum of squared
        /// samples of the block, <paramref name="blockPeak"/> its largest absolute sample, <paramref name="frames"/> its length.
        /// </summary>
        public void Add(double sumSquares, float blockPeak, int frames, int sampleRate)
        {
            if (frames <= 0 || sampleRate <= 0) return;
            double dt = frames / (double)sampleRate;

            double r = Math.Sqrt(sumSquares / frames);
            if (double.IsNaN(r) || double.IsInfinity(r)) r = 0;
            double tau = r > Rms ? RmsAttackSec : RmsReleaseSec;
            double a = 1.0 - Math.Exp(-dt / tau);
            Rms = (float)(Rms + (r - Rms) * a);

            float p = float.IsNaN(blockPeak) ? 0f : blockPeak;
            float decayed = (float)(Peak * Math.Exp(-dt / PeakReleaseSec));
            Peak = p > decayed ? p : decayed;
        }

        /// <summary>No samples arrived for <paramref name="dtSec"/> seconds: let both levels fall toward 0 ("deaf" must look flat).</summary>
        public void Idle(float dtSec)
        {
            if (!(dtSec > 0f)) return;
            Rms = Settle((float)(Rms * Math.Exp(-dtSec / RmsReleaseSec)));
            Peak = Settle((float)(Peak * Math.Exp(-dtSec / PeakReleaseSec)));
        }

        public void Reset() { Rms = 0f; Peak = 0f; }

        /// <summary>Sum of squares and largest absolute value of <paramref name="count"/> samples. NaN samples are ignored for the peak.</summary>
        public static void Measure(float[] buf, int offset, int count, out double sumSquares, out float peak)
        {
            double s = 0;
            float p = 0f;
            int end = offset + count;
            for (int i = offset; i < end; i++)
            {
                float v = buf[i];
                s += (double)v * v;
                float a = v < 0f ? -v : v;
                if (a > p) p = a;
            }
            sumSquares = s;
            peak = p;
        }

        static float Settle(float v) { return v < 1e-6f ? 0f : v; }
    }
}
