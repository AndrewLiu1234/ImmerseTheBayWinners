using System;

namespace UntitledGuitarApp.Core.Audio
{
    /// <summary>
    /// Streaming onset detector (MASTER_PLAN §19 "Onset"): every <see cref="AudioConfig.HopSize"/> samples, a Hann-windowed
    /// <see cref="AudioConfig.OnsetFftSize"/>-point FFT of the newest audio → log-compressed magnitudes of the bins below
    /// <see cref="AudioConfig.OnsetMaxHz"/> → SuperFlux-style spectral flux (mean positive change per bin vs the max-filtered spectrum
    /// <see cref="AudioConfig.OnsetDiffLagHops"/> hops earlier) → peak picking
    /// against an adaptive threshold k · median(flux over the last <see cref="AudioConfig.OnsetMedianSec"/>) + floor, with
    /// <see cref="AudioConfig.OnsetMinGapSec"/> between onsets. A peak is confirmed one hop later (it must not be followed by a
    /// larger flux), so onsets come out ≈ 1–2 hops (11–21 ms) after the attack; their timestamps are the attack time, not the
    /// detection time. Time is the sample count since construction/<see cref="Reset"/> divided by the sample rate — the caller
    /// adds its own clock origin. Preallocated: <see cref="Process"/> allocates nothing. One instance per audio worker.
    /// Deterministic. Silence and NaN/Inf input never produce onsets or exceptions (bad samples count as 0).
    /// </summary>
    public sealed class SpectralFluxOnset
    {
        const int QueueCapacity = 64;

        readonly AudioConfig _cfg;
        readonly Fft _fft;
        readonly int _n, _hop, _maxBin, _historyLen, _minGapHops;
        readonly float[] _frame, _hann, _re, _im, _curLog;
        readonly float[][] _logRing;   // the last OnsetDiffLagHops log spectra; _logRing[_ringPos] is the oldest
        int _ringPos;
        readonly double _ampScale, _silenceMs;
        readonly double[] _history, _scratch;
        readonly double[] _queue = new double[QueueCapacity];
        int _histCount, _histPos, _pending, _qHead, _qCount;
        long _hops;                 // hops analysed
        long _lastOnsetHop;
        double _fPrev2, _fPrev1;    // flux at hops c−1 and c (candidate), see Analyse
        bool _havePrev1, _havePrev2;

        public int SampleRate { get; }
        /// <summary>Total onsets dropped because the caller did not drain the queue (capacity 64).</summary>
        public int DroppedOnsets { get; private set; }
        /// <summary>Most recent onset time, s (NaN before the first).</summary>
        public double LastOnsetSec { get; private set; } = double.NaN;
        /// <summary>Flux and threshold of the last analysed hop (debug overlay / logs).</summary>
        public double LastFlux { get; private set; }
        public double LastThreshold { get; private set; }
        /// <summary>Samples consumed since construction / <see cref="Reset"/>.</summary>
        public long SamplesProcessed { get; private set; }

        public SpectralFluxOnset(AudioConfig config, int sampleRate)
        {
            _cfg = config ?? throw new ArgumentNullException(nameof(config));
            if (sampleRate < 8000 || sampleRate > 384000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            SampleRate = sampleRate;
            _n = config.OnsetFftSize;
            _hop = config.HopSize;
            _fft = new Fft(_n);
            _maxBin = Math.Max(1, Math.Min(_n / 2, (int)Math.Floor(config.OnsetMaxHz * _n / sampleRate)));
            _frame = new float[_n];
            _hann = new float[_n];
            double wsum = 0;
            for (int i = 0; i < _n; i++) { _hann[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / _n)); wsum += _hann[i]; }
            _ampScale = 2.0 / wsum;                      // |X|·2/Σw = amplitude of a bin-centred sinusoid
            _re = new float[_n];
            _im = new float[_n];
            _logRing = new float[config.OnsetDiffLagHops][];
            for (int i = 0; i < _logRing.Length; i++) _logRing[i] = new float[_maxBin + 1];
            _curLog = new float[_maxBin + 1];
            _historyLen = Math.Max(3, (int)Math.Round(config.OnsetMedianSec * sampleRate / _hop));
            _history = new double[_historyLen];
            _scratch = new double[_historyLen];
            _minGapHops = (int)Math.Ceiling(config.OnsetMinGapSec * sampleRate / _hop - 1e-9);
            double silenceRms = Math.Pow(10.0, config.SilenceRmsDbfs / 20.0);
            _silenceMs = silenceRms * silenceRms;
            Reset();
        }

        /// <summary>Forget all state; time restarts at 0.</summary>
        public void Reset()
        {
            Array.Clear(_frame, 0, _frame.Length);
            foreach (var a in _logRing) Array.Clear(a, 0, a.Length);
            _ringPos = 0;
            _histCount = _histPos = _pending = _qHead = _qCount = 0;
            _hops = 0; _lastOnsetHop = long.MinValue / 2;
            _fPrev1 = _fPrev2 = 0; _havePrev1 = _havePrev2 = false;
            SamplesProcessed = 0; DroppedOnsets = 0; LastOnsetSec = double.NaN; LastFlux = LastThreshold = 0;
        }

        /// <summary>Feeds samples (any count; partial hops are buffered). Returns how many onsets were detected in this call;
        /// read them with <see cref="TryReadOnset"/>.</summary>
        public int Process(float[] samples, int offset, int count)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            if (offset < 0 || count < 0 || offset > samples.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            int found = 0;
            int keep = _n - _hop;                         // the newest hop is written at _frame[keep + _pending]
            for (int i = 0; i < count; i++)
            {
                float v = samples[offset + i];
                if (float.IsNaN(v) || float.IsInfinity(v)) v = 0f;
                _frame[keep + _pending++] = v;
                if (_pending == _hop)
                {
                    if (Analyse()) found++;
                    Array.Copy(_frame, _hop, _frame, 0, keep);
                    _pending = 0;
                }
            }
            SamplesProcessed += count;
            return found;
        }

        /// <summary>Oldest undelivered onset time (s since start); false when none.</summary>
        public bool TryReadOnset(out double onsetSec)
        {
            if (_qCount == 0) { onsetSec = 0; return false; }
            onsetSec = _queue[_qHead];
            _qHead = (_qHead + 1) % QueueCapacity; _qCount--;
            return true;
        }

        // One hop: flux of the frame that ends now (hop index _hops), then decide whether the PREVIOUS hop was a peak.
        bool Analyse()
        {
            double ms = 0;
            for (int i = 0; i < _n; i++) { float x = _frame[i]; ms += (double)x * x; _re[i] = x * _hann[i]; _im[i] = 0f; }
            ms /= _n;
            double flux = 0;
            var prev = _logRing[_ringPos];               // spectrum OnsetDiffLagHops hops ago
            if (ms < _silenceMs)
            {
                Array.Clear(_curLog, 0, _curLog.Length);   // a note after silence is a full rise
            }
            else
            {
                _fft.Forward(_re, _im);
                double gamma = _cfg.OnsetLogGamma;
                int w = _cfg.OnsetMaxFilterBins;
                for (int k = 1; k <= _maxBin; k++)
                {
                    double mag = Math.Sqrt((double)_re[k] * _re[k] + (double)_im[k] * _im[k]) * _ampScale;
                    _curLog[k] = (float)Math.Log(1.0 + gamma * mag);
                }
                for (int k = 1; k <= _maxBin; k++)
                {
                    // SuperFlux (Böck & Widmer 2013): compare with the max of the earlier spectrum around k, so partials that
                    // beat or drift by a bin don't read as new energy.
                    float ref_ = prev[k];
                    for (int j = Math.Max(1, k - w); j <= Math.Min(_maxBin, k + w); j++) if (prev[j] > ref_) ref_ = prev[j];
                    double d = _curLog[k] - ref_;
                    if (d > 0) flux += d;
                }
                flux /= _maxBin;
            }
            Array.Copy(_curLog, prev, prev.Length);
            _ringPos = (_ringPos + 1) % _logRing.Length;
            long hop = _hops++;
            LastFlux = flux;

            // Candidate = previous hop (c = hop − 1): a local max over its neighbours, above the threshold from the hops before it.
            bool onset = false;
            if (_havePrev1)
            {
                double fc = _fPrev1;
                double thr = _cfg.OnsetThresholdK * Median() + _cfg.OnsetMinFlux;
                LastThreshold = thr;
                long c = hop - 1;
                bool rising = !_havePrev2 || fc > _fPrev2;
                if (rising && fc >= flux && fc > thr && c - _lastOnsetHop >= _minGapHops)
                {
                    _lastOnsetHop = c;
                    // Frame c spans samples [(c+1)·hop − N, (c+1)·hop) and is compared with frame c − lag; with lag 2 the new
                    // energy entered during [(c−1)·hop, (c+1)·hop), whose centre is c·hop. Synthetic bias vs the FIRST string
                    // of a strum: +11…+14 ms (≈ the middle of a 30 ms strum spread); single plucks +5…+7 ms. The device's
                    // latency calibration (PatternJudgeConfig.LatencyOffsetMs) absorbs the rest.
                    double sec = (double)c * _hop / SampleRate;
                    LastOnsetSec = sec;
                    Enqueue(sec);
                    onset = true;
                }
                PushHistory(fc);
            }
            _fPrev2 = _fPrev1; _havePrev2 = _havePrev1;
            _fPrev1 = flux; _havePrev1 = true;
            return onset;
        }

        void Enqueue(double sec)
        {
            if (_qCount == QueueCapacity) { _qHead = (_qHead + 1) % QueueCapacity; _qCount--; DroppedOnsets++; }
            _queue[(_qHead + _qCount) % QueueCapacity] = sec;
            _qCount++;
        }

        void PushHistory(double f)
        {
            _history[_histPos] = f;
            _histPos = (_histPos + 1) % _historyLen;
            if (_histCount < _historyLen) _histCount++;
        }

        // Median of the flux history (0 while empty). Insertion sort of ≤ ~30 values: deterministic, allocation-free.
        double Median()
        {
            int n = _histCount;
            if (n == 0) return 0;
            for (int i = 0; i < n; i++)
            {
                double v = _history[i]; int j = i - 1;
                while (j >= 0 && _scratch[j] > v) { _scratch[j + 1] = _scratch[j]; j--; }
                _scratch[j + 1] = v;
            }
            return (n & 1) == 1 ? _scratch[n / 2] : 0.5 * (_scratch[n / 2 - 1] + _scratch[n / 2]);
        }
    }
}
