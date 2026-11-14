using System;

namespace UntitledGuitarApp.Core.Audio
{
    /// <summary>Why a frame has (or has no) pitch.</summary>
    public enum PitchStatus
    {
        /// <summary>A periodic signal was found; <see cref="PitchResult.Hz"/> is valid.</summary>
        Pitched,
        /// <summary>RMS under <see cref="AudioConfig.SilenceRmsDbfs"/> — nothing played (or a fully muted string).</summary>
        Silent,
        /// <summary>Loud enough, but no YIN dip under <see cref="AudioConfig.MaxAperiodicity"/> — noise, a thud, talking.</summary>
        Aperiodic,
        /// <summary>The window is shorter than <see cref="YinPitchTracker.MinWindow"/> samples.</summary>
        TooShort,
        /// <summary>The window contained NaN or ±Infinity samples.</summary>
        InvalidInput,
    }

    /// <summary>One frame's pitch estimate. Every field is finite; Hz = 0 unless <see cref="HasPitch"/>.</summary>
    public readonly struct PitchResult
    {
        public readonly PitchStatus Status;
        /// <summary>Fundamental frequency, Hz (0 when there is no pitch).</summary>
        public readonly double Hz;
        /// <summary>YIN's first-dip estimate before octave correction (= Hz when no correction applied; 0 without pitch).
        /// Lets a caller with an expected note (tuner target, pluck check) test both octave hypotheses.</summary>
        public readonly double UncorrectedHz;
        /// <summary>1 − aperiodicity, clamped to [0, 1]. The pluck check wants &gt; 0.8 (§19).</summary>
        public readonly float Confidence;
        /// <summary>YIN d′ at the chosen period (0 = perfectly periodic, ~1 = noise), clamped to [0, 1].</summary>
        public readonly float Aperiodicity;
        /// <summary>Frame RMS after removing DC, linear full scale (1.0 = 0 dBFS sine peak ±1.414…).</summary>
        public readonly float Rms;

        public bool HasPitch => Status == PitchStatus.Pitched;

        public PitchResult(PitchStatus status, double hz, float aperiodicity, float rms) : this(status, hz, hz, aperiodicity, rms) { }

        public PitchResult(PitchStatus status, double hz, double uncorrectedHz, float aperiodicity, float rms)
        {
            Status = status;
            Hz = status == PitchStatus.Pitched ? hz : 0.0;
            UncorrectedHz = status == PitchStatus.Pitched ? uncorrectedHz : 0.0;
            Aperiodicity = aperiodicity < 0f ? 0f : aperiodicity > 1f ? 1f : aperiodicity;
            Confidence = 1f - Aperiodicity;
            Rms = rms;
        }
    }

    /// <summary>
    /// YIN pitch tracker (de Cheveigné &amp; Kawahara 2002) over the full <see cref="AudioConfig.MinHz"/>–<see cref="AudioConfig.MaxHz"/>
    /// range: difference function → cumulative-mean normalisation → first dip under the threshold → parabolic interpolation, plus
    /// octave correction by harmonic support (Goertzel power at the sub-octave's odd partials vs the candidate's own partials).
    /// All buffers are preallocated in the constructor; <see cref="Process"/> allocates nothing. Not thread-safe: one instance
    /// per audio worker. Deterministic: the result depends only on the samples passed in.
    /// </summary>
    public sealed class YinPitchTracker
    {
        readonly AudioConfig _cfg;
        readonly int _minTau;        // shortest lag searched (MaxHz)
        readonly int _maxTau;        // longest lag searched (MinHz)
        readonly int _lagCount;      // d(τ) is computed for τ = 0 … _maxTau + 1 (one extra for interpolation)
        readonly float[] _x;         // DC-removed copy of the analysed window
        readonly float[] _xw;        // Hann-windowed copy (octave check only)
        readonly float[] _hann;      // Hann table for FrameSize-long windows
        readonly double[] _d;        // raw difference function d(τ)
        readonly double[] _dp;       // cumulative-mean-normalised d′(τ)
        readonly double _silenceRms;
        int _n;                      // samples in the current window
        bool _windowed;              // _xw is valid for the current window

        public int SampleRate { get; }
        /// <summary>Shortest window that can be analysed: two periods of <see cref="AudioConfig.MinHz"/> (+ interpolation margin).</summary>
        public int MinWindow => 2 * _lagCount;

        public YinPitchTracker(AudioConfig config, int sampleRate)
        {
            _cfg = config ?? throw new ArgumentNullException(nameof(config));
            if (sampleRate < 8000 || sampleRate > 384000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            SampleRate = sampleRate;
            _minTau = Math.Max(2, (int)Math.Floor(sampleRate / config.MaxHz));
            _maxTau = (int)Math.Ceiling(sampleRate / config.MinHz);
            _lagCount = _maxTau + 2;
            if (config.FrameSize < 2 * _lagCount)
                throw new ArgumentException($"FrameSize {config.FrameSize} is too short for {config.MinHz} Hz at {sampleRate} Hz (needs {2 * _lagCount})");
            _x = new float[config.FrameSize];
            _xw = new float[config.FrameSize];
            _hann = new float[config.FrameSize];
            for (int i = 0; i < _hann.Length; i++) _hann[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (_hann.Length - 1)));
            _d = new double[_lagCount];
            _dp = new double[_lagCount];
            _silenceRms = Math.Pow(10.0, config.SilenceRmsDbfs / 20.0);
        }

        /// <summary>
        /// Estimates the pitch of <c>samples[offset … offset+count)</c>. If <paramref name="count"/> exceeds
        /// <see cref="AudioConfig.FrameSize"/>, only the LAST FrameSize samples (the newest audio) are analysed.
        /// Throws only on programmer errors (null buffer, range outside the array); bad audio returns a status instead.
        /// </summary>
        public PitchResult Process(float[] samples, int offset, int count)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            if (offset < 0 || count < 0 || offset > samples.Length - count) throw new ArgumentOutOfRangeException(nameof(count));

            int n = Math.Min(count, _cfg.FrameSize);
            int start = offset + count - n;
            _n = n; _windowed = false;

            // DC removal + finiteness + RMS.
            double sum = 0;
            for (int i = 0; i < n; i++) sum += samples[start + i];
            if (double.IsNaN(sum) || double.IsInfinity(sum)) return new PitchResult(PitchStatus.InvalidInput, 0, 1f, 0f);
            float mean = n > 0 ? (float)(sum / n) : 0f;
            double energy = 0;
            for (int i = 0; i < n; i++) { float v = samples[start + i] - mean; _x[i] = v; energy += (double)v * v; }
            if (double.IsNaN(energy) || double.IsInfinity(energy)) return new PitchResult(PitchStatus.InvalidInput, 0, 1f, 0f);
            float rms = n > 0 ? (float)Math.Sqrt(energy / n) : 0f;

            if (n < MinWindow) return new PitchResult(PitchStatus.TooShort, 0, 1f, rms);
            if (rms < _silenceRms) return new PitchResult(PitchStatus.Silent, 0, 1f, rms);

            ComputeDifference(n - _lagCount);
            ComputeCmndf();

            int tau = FirstDip(_cfg.YinThreshold);
            if (tau < 0) tau = FirstDip(_cfg.MaxAperiodicity);
            if (tau < 0) return new PitchResult(PitchStatus.Aperiodic, 0, (float)MinCmndf(), rms);

            double uncorrectedHz = SampleRate / Interpolate(tau);
            int up = _cfg.OctaveCorrection ? TryShorterPeriod(ref tau) : 0;
            if (up < 0) return new PitchResult(PitchStatus.Aperiodic, 0, (float)_dp[tau], rms);
            if (_cfg.OctaveCorrection && up == 0)
            {
                // At most two steps down (f → f/2 → f/4). Each needs (a) the longer lag to explain the waveform clearly better
                // (d′(2τ) < OctaveDownDipRatio · d′(τ): a weak fundamental, not noise jitter between two equally good lags) and
                // (b) harmonic support at the sub-octave's odd partials.
                for (int step = 0; step < 2; step++)
                {
                    int longer = LocalMinNear(2 * tau, 3);
                    if (longer < 0 || !(_dp[longer] < _cfg.OctaveDownDipRatio * _dp[tau])) break;
                    if (SubharmonicSupport(SampleRate / Interpolate(tau), 2) < _cfg.OctaveSupportRatio) break;
                    tau = longer;
                }
            }

            double hz = SampleRate / Interpolate(tau);
            return new PitchResult(PitchStatus.Pitched, hz, uncorrectedHz, (float)_dp[tau], rms);
        }

        // The first dip can sit at a multiple kT of the true period when d′(T) is just above the threshold (a pluck's attack,
        // where each period is duller than the last; low SNR, where noise lifts every dip to ≈ the threshold). τ/k wins if
        // (a) its dip is within OctaveUpSlack of the chosen one (YIN's own rule: the shortest good-enough lag), or (b) it is
        // usable and harmonic support rejects the long period: f = sr/τ's partials that are NOT multiples of k carry less than
        // OctaveSupportRatio of kf's. Highest candidate first. Returns +1 moved, 0 kept, −1 ambiguous (the right period's dip
        // is past MaxAperiodicity → better "no pitch" than a wrong octave).
        int TryShorterPeriod(ref int tau)
        {
            double f = SampleRate / Interpolate(tau);
            for (int k = 4; k >= 2; k--)
            {
                int centre = (int)Math.Round(tau / (double)k);
                if (centre < _minTau) continue;
                int shorter = LocalMinNear(centre, 2);
                if (shorter < 0) continue;
                double dShort = _dp[shorter];
                bool close = dShort <= _dp[tau] + _cfg.OctaveUpSlack;
                bool unsupported = !close && dShort <= _cfg.MaxAperiodicity && SubharmonicSupport(k * f, k) < _cfg.OctaveSupportRatio;
                if (!close && !unsupported) continue;
                if (dShort > _cfg.MaxAperiodicity) return -1;
                tau = shorter;
                return 1;
            }
            return 0;
        }

        // d(τ) = Σ_{j<W} (x_j − x_{j+τ})², τ = 1 … _maxTau+1.
        void ComputeDifference(int w)
        {
            var x = _x;
            _d[0] = 0;
            for (int tau = 1; tau < _lagCount; tau++)
            {
                float a0 = 0, a1 = 0, a2 = 0, a3 = 0;
                int j = 0;
                for (; j + 3 < w; j += 4)
                {
                    float e0 = x[j] - x[j + tau], e1 = x[j + 1] - x[j + 1 + tau];
                    float e2 = x[j + 2] - x[j + 2 + tau], e3 = x[j + 3] - x[j + 3 + tau];
                    a0 += e0 * e0; a1 += e1 * e1; a2 += e2 * e2; a3 += e3 * e3;
                }
                for (; j < w; j++) { float e = x[j] - x[j + tau]; a0 += e * e; }
                _d[tau] = (double)a0 + a1 + a2 + a3;
            }
        }

        // d′(0) = 1, d′(τ) = d(τ) · τ / Σ_{k≤τ} d(k).
        void ComputeCmndf()
        {
            _dp[0] = 1.0;
            double running = 0;
            for (int tau = 1; tau < _lagCount; tau++)
            {
                running += _d[tau];
                _dp[tau] = running > 1e-20 ? _d[tau] * tau / running : 1.0;
            }
        }

        // First τ in [minTau, maxTau] with d′ < threshold, then walk down to the bottom of that dip. −1 if none.
        int FirstDip(double threshold)
        {
            for (int tau = _minTau; tau <= _maxTau; tau++)
            {
                if (_dp[tau] < threshold)
                {
                    while (tau + 1 <= _maxTau && _dp[tau + 1] < _dp[tau]) tau++;
                    return tau;
                }
            }
            return -1;
        }

        double MinCmndf()
        {
            double m = 1.0;
            for (int tau = _minTau; tau <= _maxTau; tau++) if (_dp[tau] < m) m = _dp[tau];
            return m;
        }

        // Lowest d′ within ±radius of centre (inside the search range), or −1 if centre is out of range.
        int LocalMinNear(int centre, int radius)
        {
            if (centre > _maxTau) return -1;
            int lo = Math.Max(_minTau, centre - radius), hi = Math.Min(_maxTau, centre + radius);
            int best = lo;
            for (int t = lo + 1; t <= hi; t++) if (_dp[t] < _dp[best]) best = t;
            return best;
        }

        // Parabolic interpolation of the raw difference around integer lag τ (raw d avoids the normalisation's slope bias).
        double Interpolate(int tau)
        {
            if (tau < 1 || tau + 1 >= _lagCount) return tau;
            double a = _d[tau - 1], b = _d[tau], c = _d[tau + 1];
            double denom = a - 2.0 * b + c;
            if (!(denom > 1e-20)) return tau;
            double shift = 0.5 * (a - c) / denom;
            if (shift > 1.0) shift = 1.0; else if (shift < -1.0) shift = -1.0;
            return tau + shift;
        }

        // Power at the partials of f/k that are not partials of f (k = 2: f/2, 3f/2, 5f/2 …) divided by the power at f's own
        // partials (f, 2f, 3f …), OctaveCheckPartials of f's partials each. High = the frame really is periodic at f/k.
        double SubharmonicSupport(double f, int k)
        {
            if (!_windowed) WindowCurrentFrame();
            double nyquist = SampleRate * 0.5, sub = 0, own = 0, fk = f / k;
            for (int m = 1; m <= k * _cfg.OctaveCheckPartials; m++)
            {
                double fm = m * fk;
                if (fm >= nyquist) break;
                if (m % k == 0) own += GoertzelPower(fm); else sub += GoertzelPower(fm);
            }
            return own > 1e-30 ? sub / own : 0.0;
        }

        void WindowCurrentFrame()
        {
            int n = _n;
            if (n == _hann.Length) for (int i = 0; i < n; i++) _xw[i] = _x[i] * _hann[i];
            else for (int i = 0; i < n; i++) _xw[i] = (float)(_x[i] * (0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (n - 1))));
            _windowed = true;
        }

        // Generalised Goertzel: |X(f)|² of the windowed frame at an arbitrary (non-bin) frequency.
        double GoertzelPower(double f)
        {
            double w = 2.0 * Math.PI * f / SampleRate, coeff = 2.0 * Math.Cos(w);
            double s1 = 0, s2 = 0;
            for (int i = 0; i < _n; i++) { double s0 = _xw[i] + coeff * s1 - s2; s2 = s1; s1 = s0; }
            return s1 * s1 + s2 * s2 - coeff * s1 * s2;
        }
    }
}
