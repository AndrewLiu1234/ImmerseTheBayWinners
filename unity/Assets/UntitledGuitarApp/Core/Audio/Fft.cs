using System;

namespace UntitledGuitarApp.Core.Audio
{
    /// <summary>
    /// In-place iterative radix-2 complex FFT of one fixed power-of-two size. Twiddles and the bit-reversal table are built in
    /// the constructor; <see cref="Forward"/> allocates nothing. Deterministic (same input → bit-identical output). Not thread-safe
    /// only in the sense that callers must not share the re/im buffers; the tables are read-only.
    /// </summary>
    public sealed class Fft
    {
        readonly int _n;
        readonly int[] _rev;
        readonly float[] _cos, _sin;   // e^{−2πik/N}, k < N/2

        public int Size => _n;

        public Fft(int size)
        {
            if (size < 2 || (size & (size - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(size), "power of two ≥ 2");
            _n = size;
            int bits = 0; while ((1 << bits) < size) bits++;
            _rev = new int[size];
            for (int i = 0; i < size; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
                _rev[i] = r;
            }
            _cos = new float[size / 2]; _sin = new float[size / 2];
            for (int k = 0; k < size / 2; k++)
            {
                double a = -2.0 * Math.PI * k / size;
                _cos[k] = (float)Math.Cos(a); _sin[k] = (float)Math.Sin(a);
            }
        }

        /// <summary>X[k] = Σ x[n]·e^{−2πikn/N}, in place. Both arrays need ≥ <see cref="Size"/> elements.</summary>
        public void Forward(float[] re, float[] im)
        {
            if (re == null || im == null) throw new ArgumentNullException(re == null ? nameof(re) : nameof(im));
            if (re.Length < _n || im.Length < _n) throw new ArgumentException("buffers shorter than the FFT size");
            int n = _n;
            for (int i = 0; i < n; i++)
            {
                int j = _rev[i];
                if (j > i) { float t = re[i]; re[i] = re[j]; re[j] = t; t = im[i]; im[i] = im[j]; im[j] = t; }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                int half = len >> 1, step = n / len;
                for (int i = 0; i < n; i += len)
                {
                    for (int k = 0; k < half; k++)
                    {
                        float wr = _cos[k * step], wi = _sin[k * step];
                        int a = i + k, b = a + half;
                        float xr = re[b] * wr - im[b] * wi;
                        float xi = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - xr; im[b] = im[a] - xi;
                        re[a] += xr; im[a] += xi;
                    }
                }
            }
        }
    }
}
