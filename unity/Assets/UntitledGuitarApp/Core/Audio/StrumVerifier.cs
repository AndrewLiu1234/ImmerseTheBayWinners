using System;
using System.Collections.Generic;
using UntitledGuitarApp.Core.Theory;

namespace UntitledGuitarApp.Core.Audio
{
    /// <summary>The top chord-level issue of one strum (MASTER_PLAN §19, Appendix B2). Chord level plus the few one-string faults
    /// that change the chord's pitch classes or bass note; anything finer is the pluck check's job.</summary>
    public enum StrumIssue
    {
        /// <summary>Clean: the expected chord, by the margin, and no fault hypothesis explains the sound better.</summary>
        None = 0,
        /// <summary>Another chord fits better: "Heard {X} — target is {Y}".</summary>
        WrongChord = 1,
        /// <summary>A should-be-silent bass string rang (C/Am + low E, D + low E/A).</summary>
        ExtraString = 2,
        /// <summary>A fretted string rang open (missing finger / not pressing).</summary>
        OpenStringInstead = 3,
        /// <summary>A fretted string rang a fret low (flat).</summary>
        WrongFretLow = 4,
        /// <summary>A fretted string rang a fret high (sharp).</summary>
        WrongFretHigh = 5,
        /// <summary>A string whose note nothing else in the chord provides (a unique pitch class, or the bass) did not ring.</summary>
        Muted = 6,
        /// <summary>Nothing harmonic rang (silence, noise, a thud), the window was too short, or the evidence is ambiguous.
        /// Never shown as a fault: "strum again".</summary>
        Unclear = 7,
    }

    /// <summary>
    /// Outcome of one strum check. Chord-level fields mirror the draft <c>Contracts.StrumResult</c> (ExpectedChord, BestMatch,
    /// Confidence, Margin, Clean); the draft's <c>Strings</c> list maps to at most one entry built from <see cref="IssueString"/>,
    /// <see cref="IssueExpectedMidi"/>, <see cref="IssueHeardMidi"/>, <see cref="StringVerdict"/> and <see cref="TipKey"/> when
    /// <see cref="HasStringFault"/>. Value type: no allocation; never NaN/Inf.
    /// </summary>
    public readonly struct StrumCheckResult
    {
        /// <summary><see cref="ChordShape.Name"/> of the chord the player should have strummed.</summary>
        public readonly string ExpectedChord;
        /// <summary>Best-scoring chord among the expected chord and the confusers; null when nothing rang.</summary>
        public readonly string BestMatch;
        /// <summary>Score of <see cref="BestMatch"/>, 0 … 1 (chroma F-measure blended with the bass-note term).</summary>
        public readonly double Confidence;
        /// <summary>Score(expected) − best confuser score (−1 … 1); negative when another chord won; 0 when nothing rang.</summary>
        public readonly double Margin;
        /// <summary>BestMatch = expected, Margin ≥ <see cref="AudioConfig.StrumMinMargin"/>, and no fault hypothesis won.</summary>
        public readonly bool Clean;
        public readonly StrumIssue Issue;
        /// <summary>String of a one-string issue, 1 = high E … 6 = low E (the lowest extra string for ExtraString); 0 = none.</summary>
        public readonly int IssueString;
        /// <summary>MIDI that string should give (−1 = it should stay silent, or no string issue).</summary>
        public readonly int IssueExpectedMidi;
        /// <summary>MIDI the winning fault hypothesis puts on that string (−1 = silent / no string issue).</summary>
        public readonly int IssueHeardMidi;
        /// <summary>Extra strings rung for ExtraString (1 or 2); 0 otherwise.</summary>
        public readonly int ExtraStringCount;
        /// <summary>Stable tip key (<see cref="StrumTipKeys"/> / <see cref="PluckTipKeys"/>); null when clean.</summary>
        public readonly string TipKey;
        /// <summary>Diagnostics: score of the expected chord, of the best confuser (0 if none) and of the best fault hypothesis.</summary>
        public readonly double ExpectedScore, ConfuserScore, FaultScore;
        /// <summary>Loudest probe over the median FFT amplitude, dB (how far the strum stands above the noise floor).</summary>
        public readonly double PeakDb;
        /// <summary>RMS of the analysis window, linear full scale.</summary>
        public readonly float Rms;

        public StrumCheckResult(string expectedChord, string bestMatch, double confidence, double margin, bool clean, StrumIssue issue,
                                int issueString, int issueExpectedMidi, int issueHeardMidi, int extraStringCount, string tipKey,
                                double expectedScore, double confuserScore, double faultScore, double peakDb, float rms)
        {
            ExpectedChord = expectedChord; BestMatch = bestMatch; Confidence = F(confidence); Margin = F(margin); Clean = clean;
            Issue = issue; IssueString = issueString; IssueExpectedMidi = issueExpectedMidi; IssueHeardMidi = issueHeardMidi;
            ExtraStringCount = extraStringCount; TipKey = tipKey; ExpectedScore = F(expectedScore); ConfuserScore = F(confuserScore);
            FaultScore = F(faultScore); PeakDb = F(peakDb); Rms = float.IsNaN(rms) || float.IsInfinity(rms) ? 0f : rms;
        }

        static double F(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0.0 : v;

        /// <summary>The issue names one string (ExtraString, OpenStringInstead, WrongFret*, Muted).</summary>
        public bool HasStringFault => Issue >= StrumIssue.ExtraString && Issue <= StrumIssue.Muted;

        /// <summary>The issue as a per-string verdict (for <c>Contracts.StringResult</c>); Unclear when no string is named.</summary>
        public PluckVerdict StringVerdict
        {
            get
            {
                switch (Issue)
                {
                    case StrumIssue.ExtraString: return PluckVerdict.ExtraString;
                    case StrumIssue.OpenStringInstead: return PluckVerdict.OpenStringInstead;
                    case StrumIssue.WrongFretLow: return PluckVerdict.WrongFretLow;
                    case StrumIssue.WrongFretHigh: return PluckVerdict.WrongFretHigh;
                    case StrumIssue.Muted: return PluckVerdict.Muted;
                    case StrumIssue.None: return PluckVerdict.Correct;
                    default: return PluckVerdict.Unclear;
                }
            }
        }

        public override string ToString() =>
            $"{ExpectedChord}: heard {BestMatch ?? "-"} conf {Confidence:F2} margin {Margin:+0.00;-0.00} {(Clean ? "CLEAN" : Issue.ToString())}" +
            (IssueString > 0 ? $" s{IssueString} {IssueExpectedMidi}→{IssueHeardMidi}" : "") +
            $" (E {ExpectedScore:F2} C {ConfuserScore:F2} F {FaultScore:F2} peak {PeakDb:F0} dB)";
    }

    /// <summary>Stable strum tip keys (logs, <c>Practice/Tips</c>). One-string issues reuse <see cref="PluckTipKeys"/>.</summary>
    public static class StrumTipKeys
    {
        public const string WrongChord = "wrong-chord";
        /// <summary>Extra bass strings under a chord whose bass is string 4 (D, Dm): "Strum only the top four".</summary>
        public const string StrumTopFour = "strum-top-four";
        public const string StrumAgain = "strum-again";
    }

    /// <summary>
    /// Strum check (MASTER_PLAN §19): does the window after an onset sound like the EXPECTED chord? Hypothesis testing, not
    /// transcription.
    /// <list type="number">
    /// <item>30–250 ms after the onset: Hann window, zero-padded FFT, amplitude at every semitone probe E2 … C6 (max within
    /// ±<see cref="AudioConfig.StrumProbeCents"/>), minus the median-bin noise floor.</item>
    /// <item>Every hypothesis — the expected chord, each confuser chord, and the expected chord's one-string fault variants
    /// (a fretted string open or a fret off, a muted string, extra bass strings) whose pitch classes or bass note differ from
    /// it — is a set of exact notes. Its partials (all harmonics up to the top probe, through the exact Hann response and
    /// probe pooling, so low-string leakage is modelled) say which probes it can explain.</item>
    /// <item>Precision P = share of the (square-root compressed) probe amplitude the hypothesis explains (a foreign note —
    /// open A2 under Em, low E under C — leaves amplitude it cannot explain). Recall R = mean over the hypothesis's pitch
    /// classes of the presence of their notes, each judged only on its own probes: the home probes of its partials 1–6 below
    /// G5 that no other note of the hypothesis explains, after removing the leakage a neighbouring semitone's peak can put
    /// there, upper median (a missing bass, a muted G string on C). A note with no own probes (E4 over E3, B3 over B2) cannot
    /// be judged and is skipped: that is the pluck check's job. Score = F = 2PR / (P + R), 0 … 1.</item>
    /// <item>Clean = the expected chord beats every confuser by <see cref="AudioConfig.StrumMinMargin"/> and no fault variant
    /// beats it by <see cref="AudioConfig.StrumFaultMargin"/>. Otherwise the top issue: wrong chord, or the winning fault.</item>
    /// </list>
    /// Preallocated: Verify allocates nothing. Not thread-safe (one per audio worker). Deterministic.
    /// Silence/noise/NaN/short input → Unclear, never an exception (bad arguments still throw).
    /// </summary>
    public sealed class StrumVerifier
    {
        const int MaxHyp = 64;
        const int S = 6;   // strings

        readonly AudioConfig _cfg;
        readonly Fft _fft;
        readonly int _n, _winStart, _winLen, _probeMin, _probes, _noteMin, _noteCount, _floorLo, _floorHi;
        readonly float[] _re, _im, _hann;
        readonly double[] _pow, _scratch;
        readonly int[] _lo, _hi;
        readonly double[] _x;           // probe amplitudes (noise-floor subtracted)
        readonly double[] _xw;          // … ^ StrumPrecisionGamma (precision weights)
        readonly double[] _xo;          // … minus the leakage a neighbouring probe's peak can put there (own-note evidence)
        readonly float[] _leak;         // [probe * 4 + (o = −2, −1, +1, +2)]: worst-case response at the probe of a partial in probe j+o
        readonly float[] _cov;          // [note * probes + probe]: max pooled response of the note's partials at the probe
        readonly int[] _home;           // [note * ownPartials + h − 1]: the probe nearest partial h (−1 = above the top probe)
        readonly int _ownH;
        readonly double _ampScale, _silenceRms;
        double _sumX, _sumXw, _maxX;

        // hypotheses
        readonly int[] _hNotes = new int[MaxHyp * S];   // index 0 = string 6
        readonly int[] _hKind = new int[MaxHyp];        // 0 expected, 1 confuser, 2 fault variant
        readonly string[] _hName = new string[MaxHyp];
        readonly StrumIssue[] _hIssue = new StrumIssue[MaxHyp];
        readonly int[] _hString = new int[MaxHyp], _hExpMidi = new int[MaxHyp], _hHeardMidi = new int[MaxHyp], _hExtra = new int[MaxHyp];
        readonly double[] _hScore = new double[MaxHyp], _hP = new double[MaxHyp], _hR = new double[MaxHyp];
        readonly int[] _dn = new int[S];                 // distinct in-range notes of the hypothesis being scored
        readonly double[] _pcPres = new double[12];
        readonly bool[] _pcSeen = new bool[12];
        readonly double[] _ownBuf = new double[32];
        int _hypCount;

        public int SampleRate { get; }
        public AudioConfig Config => _cfg;
        /// <summary>Samples needed after the onset (the window ends at <see cref="AudioConfig.StrumWindowEndSec"/>).</summary>
        public int SamplesAfterOnset => _winStart + _winLen;
        /// <summary>Loudest probe over the median FFT amplitude in the last Verify, dB (diagnostics; 0 when not computed).</summary>
        public double LastPeakDb { get; private set; }

        public StrumVerifier(AudioConfig config, int sampleRate)
        {
            _cfg = config ?? throw new ArgumentNullException(nameof(config));
            if (sampleRate < 8000 || sampleRate > 384000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            SampleRate = sampleRate;
            _winStart = (int)Math.Round(config.StrumWindowStartSec * sampleRate);
            _winLen = (int)Math.Round((config.StrumWindowEndSec - config.StrumWindowStartSec) * sampleRate);
            _n = 1; while (_n < config.StrumFftPadding * _winLen) _n <<= 1;
            _fft = new Fft(_n);
            _re = new float[_n]; _im = new float[_n];
            _hann = new float[_winLen];
            double wsum = 0;
            for (int i = 0; i < _winLen; i++) { _hann[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (_winLen - 1))); wsum += _hann[i]; }
            _ampScale = 2.0 / wsum;
            _silenceRms = Math.Pow(10.0, config.SilenceRmsDbfs / 20.0);

            _noteMin = config.StrumMinMidi;
            _noteCount = config.StrumMaxMidi - config.StrumMinMidi + 1;
            _probeMin = config.StrumMinMidi;
            int probeMax = config.StrumProbeMaxMidi;
            double binHz = (double)sampleRate / _n;
            double tol = Math.Pow(2.0, config.StrumProbeCents / 1200.0);
            while (probeMax > _probeMin && Midi.ToHz(probeMax) * tol >= sampleRate * 0.5 - binHz) probeMax--;
            _probes = probeMax - _probeMin + 1;
            _lo = new int[_probes]; _hi = new int[_probes];
            for (int j = 0; j < _probes; j++)
            {
                double f = Midi.ToHz(_probeMin + j);
                int nearest = (int)Math.Round(f / binHz);
                int lo = (int)Math.Ceiling(f / tol / binHz), hi = (int)Math.Floor(f * tol / binHz);
                _lo[j] = Math.Min(lo, nearest); _hi[j] = Math.Max(hi, nearest);
            }
            _floorLo = _lo[0]; _floorHi = _hi[_probes - 1];
            _pow = new double[_floorHi + 1];
            _scratch = new double[_floorHi - _floorLo + 1];
            _x = new double[_probes];
            _xw = new double[_probes];
            _xo = new double[_probes];

            // Coverage: the pooled Hann response of each note's partials (every harmonic that can reach a probe) at each probe.
            _cov = new float[_noteCount * _probes];
            _ownH = config.StrumOwnPartials;
            _home = new int[_noteCount * _ownH];
            double cellsPerHz = (double)_winLen / sampleRate;
            double topHz = _hi[_probes - 1] * binHz + 3.0 / cellsPerHz;
            for (int nt = 0; nt < _noteCount; nt++)
            {
                double f0 = Midi.ToHz(_noteMin + nt);
                for (int h = 1; h <= _ownH; h++)
                {
                    int jh = (int)Math.Round(Midi.FromHz(h * f0)) - _probeMin;
                    _home[nt * _ownH + h - 1] = jh >= 0 && jh < _probes && _probeMin + jh <= config.StrumOwnMaxMidi ? jh : -1;
                }
                for (int h = 1; h * f0 <= topHz; h++)
                {
                    double fp = h * f0;
                    for (int j = 0; j < _probes; j++)
                    {
                        double best = 0;
                        for (int k = _lo[j]; k <= _hi[j]; k++)
                        {
                            double d = Math.Abs(k * binHz - fp) * cellsPerHz;
                            if (d < 2.5) best = Math.Max(best, HannResponse(d));
                        }
                        int idx = nt * _probes + j;
                        if (best > _cov[idx]) _cov[idx] = (float)best;
                    }
                }
            }

            // Leakage between semitone probes (low strings: a semitone is ~1 resolution cell): a partial anywhere inside probe
            // j+o's tolerance, at its edge nearest to j (worst case), seen through probe j's pooling — main lobe and sidelobes.
            _leak = new float[_probes * 4];
            for (int j = 0; j < _probes; j++)
                for (int oi = 0; oi < 4; oi++)
                {
                    int o = LeakOffsets[oi], jj = j + o;
                    if (jj < 0 || jj >= _probes) continue;
                    double fp = Midi.ToHz(_probeMin + jj) * (o > 0 ? 1.0 / tol : tol);
                    double best = 0;
                    for (int k = _lo[j]; k <= _hi[j]; k++)
                    {
                        double d = Math.Abs(k * binHz - fp) * cellsPerHz;
                        if (d < 6) best = Math.Max(best, HannResponse(d));
                    }
                    _leak[j * 4 + oi] = (float)best;
                }
        }

        static readonly int[] LeakOffsets = { -2, -1, 1, 2 };

        /// <summary>Normalised Hann main-lobe amplitude response at <paramref name="d"/> resolution cells from the partial.</summary>
        static double HannResponse(double d)
        {
            d = Math.Abs(d);
            if (d < 1e-9) return 1.0;
            if (Math.Abs(d - 1.0) < 1e-9) return 0.5;
            double s = Math.Sin(Math.PI * d) / (Math.PI * d);
            return Math.Abs(s / (1.0 - d * d));
        }

        /// <summary>Every other chord of <see cref="ChordLibrary.All"/> (allocates; call at setup, not per strum).</summary>
        public static IReadOnlyList<ChordShape> DefaultConfusers(ChordShape expected)
        {
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            var l = new List<ChordShape>();
            foreach (var c in ChordLibrary.All) if (c.Name != expected.Name) l.Add(c);
            return l;
        }

        /// <summary>Noise-floor-subtracted amplitude at the semitone probe of <paramref name="midi"/> in the last Verify (0 outside the probes).</summary>
        public double ProbeAmplitude(int midi) => midi >= _probeMin && midi < _probeMin + _probes ? _x[midi - _probeMin] : 0.0;

        /// <summary>Diagnostics of the last Verify: hypotheses (0 = expected, then confusers, then fault variants).</summary>
        public int HypothesisCount => _hypCount;
        public string DescribeHypothesis(int i) =>   // allocates: tests and logs only
            $"{(_hKind[i] == 0 ? "expected" : _hKind[i] == 1 ? "confuser" : "fault")} {_hName[i]}" +
            (_hKind[i] == 2 ? $" {_hIssue[i]} s{_hString[i]} {_hExpMidi[i]}→{_hHeardMidi[i]}" : "") +
            $": F {_hScore[i]:F3} (P {_hP[i]:F3} R {_hR[i]:F3}) notes " + NotesDebug(i);

        string NotesDebug(int h)
        {
            Score(h);
            int nd = 0;
            for (int i = 0; i < S; i++)
            {
                int m = _hNotes[h * S + i];
                if (m < _noteMin || m >= _noteMin + _noteCount) continue;
                bool dup = false;
                for (int k = 0; k < nd; k++) if (_dn[k] == m) { dup = true; break; }
                if (!dup) _dn[nd++] = m;
            }
            var sb = new System.Text.StringBuilder();
            for (int k = 0; k < nd; k++)
            {
                double p = NotePresence(k, nd, _cfg.StrumPresenceRatio * _maxX, out int own);
                sb.Append(_dn[k]).Append(own == 0 ? ":-" : $":{p:F2}/{own}");
                sb.Append('[');
                for (int hh = 1; hh <= _ownH; hh++)
                {
                    int j = _home[(_dn[k] - _noteMin) * _ownH + hh - 1];
                    if (j < 0) continue;
                    bool shared = false;
                    for (int q = 0; q < nd; q++) if (q != k && _cov[(_dn[q] - _noteMin) * _probes + j] >= _cfg.StrumCoverMin) shared = true;
                    if (!shared) sb.Append(_probeMin + j).Append('=').Append((_xo[j] * 1e4).ToString("F0")).Append(',');
                }
                sb.Append("] ");
            }
            return sb.ToString();
        }

        /// <summary>Draft-contract shape (<c>IStrumVerifier.Verify</c>): the whole array is the window.</summary>
        public StrumCheckResult Verify(float[] window, int sampleRate, int onsetIndex, ChordShape expected, IReadOnlyList<ChordShape> confusers)
        {
            if (sampleRate != SampleRate) throw new ArgumentException($"verifier built for {SampleRate} Hz, got {sampleRate}", nameof(sampleRate));
            if (window == null) throw new ArgumentNullException(nameof(window));
            return Verify(window, 0, window.Length, onsetIndex, expected, confusers);
        }

        /// <summary>Verifies <c>samples[offset … offset+count)</c>; <paramref name="onsetIndex"/> is relative to <paramref name="offset"/>.
        /// Needs <see cref="SamplesAfterOnset"/> samples after the onset (a window less than half full → Unclear).</summary>
        /// <param name="confusers">Other chord hypotheses (other song chords, <see cref="DefaultConfusers"/>). May be null/empty;
        /// entries named like <paramref name="expected"/> are skipped.</param>
        public StrumCheckResult Verify(float[] samples, int offset, int count, int onsetIndex, ChordShape expected,
                                       IReadOnlyList<ChordShape> confusers)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            if (offset < 0 || count < 0 || offset > samples.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            LastPeakDb = 0;
            Array.Clear(_x, 0, _x.Length);

            int start = offset + Math.Max(0, Math.Min(count, onsetIndex)) + _winStart;
            int end = offset + count;
            int avail = Math.Min(_winLen, end - start);
            if (avail < _winLen / 2) return Unclear(expected, 0f, 0);

            // 1. Window → spectrum → semitone probes.
            double mean = 0;
            for (int i = 0; i < avail; i++)
            {
                float v = samples[start + i];
                if (float.IsNaN(v) || float.IsInfinity(v)) return Unclear(expected, 0f, 0);
                mean += v;
            }
            mean /= avail;
            double e = 0;
            for (int i = 0; i < _n; i++)
            {
                float v = i < avail ? (float)(samples[start + i] - mean) : 0f;
                if (i < avail) e += (double)v * v;
                _re[i] = i < _winLen ? v * _hann[i] : 0f;
                _im[i] = 0f;
            }
            float rms = (float)Math.Sqrt(e / avail);
            if (rms <= _silenceRms) return Unclear(expected, rms, 0);
            _fft.Forward(_re, _im);
            for (int k = _floorLo; k <= _floorHi; k++)
            {
                double p = (double)_re[k] * _re[k] + (double)_im[k] * _im[k];
                _pow[k] = p;
                _scratch[k - _floorLo] = p;
            }
            double medianAmp = Math.Sqrt(Median(_scratch, _scratch.Length)) * _ampScale;
            double floorAmp = medianAmp * _cfg.StrumNoiseFloorK;
            double rawMax = 0;
            _sumX = 0; _maxX = 0;
            for (int j = 0; j < _probes; j++)
            {
                double pk = 0;
                for (int k = _lo[j]; k <= _hi[j]; k++) if (_pow[k] > pk) pk = _pow[k];
                double amp = Math.Sqrt(pk) * _ampScale;
                if (amp > rawMax) rawMax = amp;
                amp -= floorAmp;
                _x[j] = amp > 0 ? amp : 0;
                _sumX += _x[j];
                if (_x[j] > _maxX) _maxX = _x[j];
            }
            _sumXw = 0;
            double pfloor = _cfg.StrumPrecisionFloor * _maxX;
            for (int j = 0; j < _probes; j++)
            {
                _xw[j] = _x[j] > pfloor ? Math.Pow(_x[j], _cfg.StrumPrecisionGamma) : 0;
                _sumXw += _xw[j];
                double leak = 0;
                for (int oi = 0; oi < 4; oi++)
                {
                    int jj = j + LeakOffsets[oi];
                    if (jj >= 0 && jj < _probes) leak = Math.Max(leak, _leak[j * 4 + oi] * _x[jj]);
                }
                _xo[j] = _x[j] > leak ? _x[j] - leak : 0;
            }
            double peakDb = medianAmp > 1e-30 ? 20.0 * Math.Log10(rawMax / medianAmp) : (rawMax > 0 ? 300.0 : 0.0);
            LastPeakDb = peakDb;
            if (!(_sumX > 1e-20) || peakDb < _cfg.StrumMinPeakDb) return Unclear(expected, rms, peakDb);

            // 2. Hypotheses and scores.
            _hypCount = 0;
            AddChord(expected, 0);
            if (confusers != null)
                for (int i = 0; i < confusers.Count; i++)
                {
                    var c = confusers[i];
                    if (c != null && c.Name != expected.Name) AddChord(c, 1);
                }
            AddVariants(expected);
            for (int h = 0; h < _hypCount; h++) _hScore[h] = Score(h);

            // 3. Decision.
            double sE = _hScore[0], sC = double.NegativeInfinity, sV = double.NegativeInfinity;
            int bc = -1, bv = -1;
            for (int h = 1; h < _hypCount; h++)
            {
                if (_hKind[h] == 1 && _hScore[h] > sC) { sC = _hScore[h]; bc = h; }
                else if (_hKind[h] == 2 && _hScore[h] > sV) { sV = _hScore[h]; bv = h; }
            }
            bool hasC = bc >= 0, hasV = bv >= 0;
            double margin = sE - (hasC ? sC : 0.0);
            string best = hasC && sC > sE ? _hName[bc] : expected.Name;
            double conf = hasC && sC > sE ? sC : sE;
            double cs = hasC ? sC : 0, vs = hasV ? sV : 0;

            if (hasC && sC > sE && (!hasV || sC >= sV))
                return Make(expected, best, conf, margin, false, StrumIssue.WrongChord, -1, StrumTipKeys.WrongChord, sE, cs, vs, peakDb, rms);
            if (hasV && sV >= sE + _cfg.StrumFaultMargin)
                return Make(expected, best, conf, margin, false, _hIssue[bv], bv, TipKeyFor(bv, expected), sE, cs, vs, peakDb, rms);
            if (hasC && sC > sE)
                return Make(expected, best, conf, margin, false, StrumIssue.WrongChord, -1, StrumTipKeys.WrongChord, sE, cs, vs, peakDb, rms);
            if (margin < _cfg.StrumMinMargin)
            {
                if (hasV && sV > sE)
                    return Make(expected, best, conf, margin, false, _hIssue[bv], bv, TipKeyFor(bv, expected), sE, cs, vs, peakDb, rms);
                return Make(expected, best, conf, margin, false, StrumIssue.Unclear, -1, StrumTipKeys.StrumAgain, sE, cs, vs, peakDb, rms);
            }
            return Make(expected, best, conf, margin, true, StrumIssue.None, -1, null, sE, cs, vs, peakDb, rms);
        }

        StrumCheckResult Make(ChordShape expected, string best, double conf, double margin, bool clean, StrumIssue issue, int hyp,
                              string tipKey, double sE, double sC, double sV, double peakDb, float rms)
        {
            int s = hyp >= 0 ? _hString[hyp] : 0, em = hyp >= 0 ? _hExpMidi[hyp] : -1, hm = hyp >= 0 ? _hHeardMidi[hyp] : -1;
            int extra = hyp >= 0 ? _hExtra[hyp] : 0;
            return new StrumCheckResult(expected.Name, best, conf, margin, clean, issue, s, em, hm, extra, tipKey, sE, sC, sV, peakDb, rms);
        }

        static StrumCheckResult Unclear(ChordShape expected, float rms, double peakDb) =>
            new StrumCheckResult(expected.Name, null, 0, 0, false, StrumIssue.Unclear, 0, -1, -1, 0, StrumTipKeys.StrumAgain, 0, 0, 0, peakDb, rms);

        string TipKeyFor(int h, ChordShape expected)
        {
            int s = _hString[h];
            bool fretted = s >= 1 && expected.FretOn(s) > 0;
            switch (_hIssue[h])
            {
                case StrumIssue.ExtraString: return expected.BassString == 4 ? StrumTipKeys.StrumTopFour : PluckTipKeys.SkipString;
                case StrumIssue.OpenStringInstead: return PluckTipKeys.PressFret;
                case StrumIssue.WrongFretLow: return PluckTipKeys.FretHigher;
                case StrumIssue.WrongFretHigh: return PluckTipKeys.FretLower;
                case StrumIssue.Muted: return fretted ? PluckTipKeys.PressFingertip : PluckTipKeys.ArchFinger;
                default: return StrumTipKeys.StrumAgain;
            }
        }

        // ---- hypotheses ----

        int NewHyp(int kind, string name, StrumIssue issue, int s, int expMidi, int heardMidi, int extra)
        {
            if (_hypCount >= MaxHyp) return -1;
            int h = _hypCount++;
            _hKind[h] = kind; _hName[h] = name; _hIssue[h] = issue; _hString[h] = s; _hExpMidi[h] = expMidi; _hHeardMidi[h] = heardMidi; _hExtra[h] = extra;
            return h;
        }

        void AddChord(ChordShape c, int kind)
        {
            int h = NewHyp(kind, c.Name, StrumIssue.None, 0, -1, -1, 0);
            if (h < 0) return;
            for (int i = 0; i < S; i++) _hNotes[h * S + i] = c.ExpectedMidiLowToHigh[i];
        }

        // One-string variants of the expected chord (hypothesis 0) that change its pitch classes or bass note.
        void AddVariants(ChordShape c)
        {
            for (int s = S; s >= 1; s--)
            {
                int fret = c.FretOn(s), open = Tuning.StandardMidi(s), m = c.MidiOn(s);
                if (fret > 0)
                {
                    AddVariant(c, StrumIssue.OpenStringInstead, s, m, open, 0);
                    if (fret - 1 >= 1) AddVariant(c, StrumIssue.WrongFretLow, s, m, m - 1, 0);
                    AddVariant(c, StrumIssue.WrongFretHigh, s, m, m + 1, 0);
                }
                if (fret >= 0) AddVariant(c, StrumIssue.Muted, s, m, -1, 0);
            }
            // Extra bass strings: each unplayed string below the bass, then all of them together.
            int extras = 0;
            for (int s = c.BassString + 1; s <= S; s++)
                if (!c.IsPlayed(s)) { AddVariant(c, StrumIssue.ExtraString, s, -1, Tuning.StandardMidi(s), 1); extras++; }
            if (extras > 1)
            {
                int h = NewHyp(2, c.Name, StrumIssue.ExtraString, S, -1, Tuning.StandardMidi(S), extras);
                if (h < 0) return;
                for (int i = 0; i < S; i++)
                {
                    int s = S - i;
                    _hNotes[h * S + i] = s > c.BassString && !c.IsPlayed(s) ? Tuning.StandardMidi(s) : c.MidiOn(s);
                }
                if (SameChordLevel(h)) _hypCount--;
            }
        }

        void AddVariant(ChordShape c, StrumIssue issue, int s, int expMidi, int heardMidi, int extra)
        {
            int h = NewHyp(2, c.Name, issue, s, expMidi, heardMidi, extra);
            if (h < 0) return;
            int played = 0;
            for (int i = 0; i < S; i++)
            {
                int note = S - i == s ? heardMidi : c.ExpectedMidiLowToHigh[i];
                _hNotes[h * S + i] = note;
                if (note >= 0) played++;
            }
            if (played < 2 || SameChordLevel(h)) _hypCount--;   // invisible at chord level: the pluck check's job
        }

        // Same pitch-class set and the same bass note as the expected chord (hypothesis 0)?
        bool SameChordLevel(int h)
        {
            int m0 = 0, m1 = 0, b0 = int.MaxValue, b1 = int.MaxValue;
            for (int i = 0; i < S; i++)
            {
                int a = _hNotes[i], b = _hNotes[h * S + i];
                if (a >= 0) { m0 |= 1 << (a % 12); if (a < b0) b0 = a; }
                if (b >= 0) { m1 |= 1 << (b % 12); if (b < b1) b1 = b; }
            }
            return m0 == m1 && b0 == b1;
        }

        // F-measure of (P = explained share of the probe amplitude, R = mean presence of the hypothesis's pitch classes).
        double Score(int h)
        {
            int nd = 0;
            for (int i = 0; i < S; i++)
            {
                int m = _hNotes[h * S + i];
                if (m < _noteMin || m >= _noteMin + _noteCount) continue;
                bool dup = false;
                for (int k = 0; k < nd; k++) if (_dn[k] == m) { dup = true; break; }
                if (!dup) _dn[nd++] = m;
            }
            if (nd == 0) { _hP[h] = _hR[h] = 0; return 0; }
            double cmin = _cfg.StrumCoverMin;

            // Precision: amplitude at probes some partial of the hypothesis can explain.
            double explained = 0;
            for (int j = 0; j < _probes; j++)
            {
                if (_x[j] <= 0) continue;
                for (int k = 0; k < nd; k++)
                    if (_cov[(_dn[k] - _noteMin) * _probes + j] >= cmin) { explained += _xw[j]; break; }
            }
            double prec = _sumXw > 0 ? explained / _sumXw : 0;

            // Recall: each note judged on its own probes; a pitch class is present when any of its judged notes is.
            for (int pc = 0; pc < 12; pc++) { _pcPres[pc] = 0; _pcSeen[pc] = false; }
            double level = _cfg.StrumPresenceRatio * _maxX;
            for (int k = 0; k < nd; k++)
            {
                double pres = NotePresence(k, nd, level, out int own);
                if (own == 0) continue;                       // cannot be judged inside a strum
                int pc = _dn[k] % 12;
                _pcSeen[pc] = true;
                if (pres > _pcPres[pc]) _pcPres[pc] = pres;
            }
            double rec = 0; int pcs = 0;
            for (int pc = 0; pc < 12; pc++) if (_pcSeen[pc]) { rec += _pcPres[pc]; pcs++; }
            rec = pcs > 0 ? rec / pcs : 1.0;
            _hP[h] = prec; _hR[h] = rec;
            return prec + rec > 1e-12 ? 2 * prec * rec / (prec + rec) : 0;
        }

        // Presence of note _dn[k] among the nd notes of the hypothesis being scored: mean leakage-corrected amplitude of its own
        // probes (partials 1 … StrumOwnPartials that no other note of the hypothesis reaches) over level, capped at 1.
        double NotePresence(int k, int nd, double level, out int own)
        {
            double cmin = _cfg.StrumCoverMin;
            int nt = _dn[k] - _noteMin;
            own = 0;
            for (int h = 1; h <= _ownH; h++)
            {
                int j = _home[nt * _ownH + h - 1];
                if (j < 0) continue;
                bool shared = false;
                for (int q = 0; q < nd; q++)
                    if (q != k && _cov[(_dn[q] - _noteMin) * _probes + j] >= cmin) { shared = true; break; }
                if (shared) continue;
                int i = own++;
                double v = _xo[j];
                while (i > 0 && _ownBuf[i - 1] > v) { _ownBuf[i] = _ownBuf[i - 1]; i--; }
                _ownBuf[i] = v;
            }
            if (own == 0 || !(level > 0)) return 0;
            double med = _ownBuf[own / 2];     // upper median: one stray partial cannot fake a note with ≥ 3 own probes, and a
                                               // note with 2 (a bass string whose fundamental the mic rolls off) needs only one
            return Math.Min(1.0, med / level);
        }

        // Quickselect median of a[0..n) (destroys the order; allocation-free, deterministic).
        static double Median(double[] a, int n)
        {
            int k = n / 2, lo = 0, hi = n - 1;
            while (lo < hi)
            {
                double pivot = a[(lo + hi) >> 1];
                int i = lo, j = hi;
                while (i <= j)
                {
                    while (a[i] < pivot) i++;
                    while (a[j] > pivot) j--;
                    if (i <= j) { double t = a[i]; a[i] = a[j]; a[j] = t; i++; j--; }
                }
                if (k <= j) hi = j; else if (k >= i) lo = i; else break;
            }
            return a[k];
        }
    }
}
