using System;
using UntitledGuitarApp.Core.Theory;

namespace UntitledGuitarApp.Core.Audio
{
    /// <summary>
    /// Per-string judgement of one pluck (MASTER_PLAN §19). Member names and values mirror the draft
    /// <c>Contracts.Verdict</c> (branch contracts/v1) one to one, so the adapter is a cast.
    /// Tie precedence when two hypotheses are the same note: OpenStringInstead &gt; WrongFret* &gt; WrongString.
    /// </summary>
    public enum PluckVerdict
    {
        /// <summary>The expected pitch within <see cref="AudioConfig.PluckMatchCents"/>, and (when judged) it rang long enough.</summary>
        Correct = 0,
        /// <summary>The open string sounded: the finger is missing or not pressing.</summary>
        OpenStringInstead = 1,
        /// <summary>1–2 semitones flat: the finger is on a lower fret than the target.</summary>
        WrongFretLow = 2,
        /// <summary>1–2 semitones sharp: the finger is on a higher fret than the target (or touches a fret on an open string).</summary>
        WrongFretHigh = 3,
        /// <summary>A neighbour string's note sounded instead (the picking hand hit the next string).</summary>
        WrongString = 4,
        /// <summary>The pick hit something (attack rise) but no pitch rang: a finger is deadening the string.</summary>
        Muted = 5,
        /// <summary>A string that should stay silent (expected MIDI = <see cref="ChordShape.Muted"/>) rang.</summary>
        ExtraString = 6,
        /// <summary>No evidence either way (silence, steady noise, a pitch no hypothesis explains). Never shown as a fault.</summary>
        Unclear = 7,
        /// <summary>Right pitch, but it died before <see cref="AudioConfig.RingOutSec"/>: "let string N ring".</summary>
        ShortRing = 8,
    }

    /// <summary>
    /// Outcome of checking one string. Fields mirror the draft <c>Contracts.StringResult</c> (String, ExpectedMidi, DetectedHz,
    /// Cents, Verdict, TipKey) plus diagnostics. Value type: crosses threads without allocation. Never NaN/Inf.
    /// </summary>
    public readonly struct PluckResult
    {
        /// <summary>String number, 1 = high E … 6 = low E.</summary>
        public readonly int String;
        /// <summary>MIDI the string should produce (<see cref="ChordShape.Muted"/> = −1 when it should stay silent).</summary>
        public readonly int ExpectedMidi;
        /// <summary>Median f0 after the attack (octave-resolved toward the matched hypothesis), Hz; 0 without pitch. When the
        /// note was found by the salience fallback (YIN locked onto an older ringing string) this is the hypothesis frequency.</summary>
        public readonly double DetectedHz;
        /// <summary>Signed cents of <see cref="DetectedHz"/> from <see cref="ExpectedMidi"/>; 0 without pitch or expectation.</summary>
        public readonly double Cents;
        public readonly PluckVerdict Verdict;
        /// <summary>Stable machine key for the tip (see <see cref="PluckTipKeys"/>); null for Correct.</summary>
        public readonly string TipKey;
        /// <summary>MIDI of the hypothesis that matched (−1 = none).</summary>
        public readonly int HeardMidi;
        /// <summary>String of the hypothesis that matched (the expected string, or the neighbour for WrongString); 0 = none.</summary>
        public readonly int HeardString;
        /// <summary>Median YIN confidence of the voting frames (0 when none voted).</summary>
        public readonly float Confidence;
        /// <summary>RMS over the analysis frames, linear full scale.</summary>
        public readonly float Rms;
        /// <summary>Measured sustain of the expected note from the onset, s, capped at <see cref="AudioConfig.RingOutSec"/>; 0 when not judged.</summary>
        public readonly double RingSec;
        /// <summary>The window reached <see cref="AudioConfig.RingOutSec"/> past the onset, so ring-out was judged.</summary>
        public readonly bool RingJudged;
        /// <summary>Analysis frames that were confidently pitched / analysed.</summary>
        public readonly int PitchedFrames, AnalysedFrames;

        public PluckResult(int stringNumber, int expectedMidi, double detectedHz, double cents, PluckVerdict verdict, string tipKey,
                           int heardMidi, int heardString, float confidence, float rms, double ringSec, bool ringJudged,
                           int pitchedFrames, int analysedFrames)
        {
            String = stringNumber; ExpectedMidi = expectedMidi; DetectedHz = Finite(detectedHz); Cents = Finite(cents);
            Verdict = verdict; TipKey = tipKey; HeardMidi = heardMidi; HeardString = heardString;
            Confidence = float.IsNaN(confidence) ? 0f : confidence; Rms = float.IsNaN(rms) || float.IsInfinity(rms) ? 0f : rms;
            RingSec = Finite(ringSec); RingJudged = ringJudged; PitchedFrames = pitchedFrames; AnalysedFrames = analysedFrames;
        }

        public bool IsCorrect => Verdict == PluckVerdict.Correct;

        static double Finite(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0.0 : v;

        public override string ToString() =>
            $"s{String} exp {ExpectedMidi} → {Verdict} ({DetectedHz:F1} Hz, {Cents:+0;-0} c, conf {Confidence:F2}, ring {RingSec:F2}s{(RingJudged ? "" : "?")})";
    }

    /// <summary>Stable tip keys (logs, <c>Practice/Tips</c>). Strings are interned constants: no allocation.</summary>
    public static class PluckTipKeys
    {
        public const string PressFret = "press-fret";
        public const string FretHigher = "fret-higher";
        public const string FretLower = "fret-lower";
        public const string PlayOpen = "play-open";
        public const string WrongString = "wrong-string";
        public const string PressFingertip = "press-fingertip";
        public const string ArchFinger = "arch-finger";
        public const string SkipString = "skip-string";
        public const string PluckAgain = "pluck-again";
        public const string LetRing = "let-ring";

        /// <param name="fretted">The expected note is fretted (fret ≥ 1) on its string.</param>
        public static string For(PluckVerdict v, bool fretted)
        {
            switch (v)
            {
                case PluckVerdict.OpenStringInstead: return PressFret;
                case PluckVerdict.WrongFretLow: return FretHigher;
                case PluckVerdict.WrongFretHigh: return fretted ? FretLower : PlayOpen;
                case PluckVerdict.WrongString: return WrongString;
                case PluckVerdict.Muted: return fretted ? PressFingertip : ArchFinger;
                case PluckVerdict.ExtraString: return SkipString;
                case PluckVerdict.Unclear: return PluckAgain;
                case PluckVerdict.ShortRing: return LetRing;
                default: return null;
            }
        }
    }

    /// <summary>
    /// Pluck check: is the EXPECTED note sounding on one string after an onset? Hypothesis testing, not transcription (§19).
    /// <list type="number">
    /// <item>Attack rise: max 512-sample RMS around the onset vs the pre-onset RMS → "the pick hit something".</item>
    /// <item>YIN (full 70–1000 Hz, octave-corrected) on frames from onset+40 ms over 200 ms; median f0 of confident frames.</item>
    /// <item>Nearest hypothesis in cents: expected, open string, expected ±1/±2 semitones, the neighbour strings' notes
    /// (from the chord when given). If none fits directly, the octave alternatives f/2 and 2f are tried, each only with
    /// harmonic support (Goertzel power at the sub-octave's odd partials), so a YIN octave slip never becomes a false fault.</item>
    /// <item>Onset-relative spectral rise: the matched note's partials must have grown since before the onset; otherwise it is
    /// an older string still ringing, and the hypothesis whose partials rose and dominate the frame is taken instead.</item>
    /// <item>No pitch: Muted when struck, else Unclear. Correct + window long enough: ring-out of the expected note's first two
    /// partials until <see cref="AudioConfig.RingOutSec"/> → else ShortRing.</item>
    /// </list>
    /// Preallocated; <see cref="Verify(float[], int, int, int, int, int, ChordShape)"/> allocates nothing. Not thread-safe.
    /// Deterministic. Silence/NaN/short input → Unclear, never an exception (bad arguments still throw).
    /// </summary>
    public sealed class PluckVerifier
    {
        const int MaxHyp = 12;

        readonly AudioConfig _cfg;
        readonly YinPitchTracker _yin;
        readonly int _frame, _hop, _skip, _analysis, _ringOut, _ringStep;
        readonly double[] _hz;
        readonly float[] _conf;
        readonly float[] _post;           // Hann-windowed first analysis frame
        readonly float[] _pre;            // Hann-windowed pre-onset frame
        readonly float[] _probe;          // Hann-windowed ring-out probe
        readonly float[] _hann;
        readonly int[] _hypMidi = new int[MaxHyp];
        readonly int[] _hypString = new int[MaxHyp];
        readonly PluckVerdict[] _hypVerdict = new PluckVerdict[MaxHyp];
        int _hypCount;
        int _preLen;                      // 0 = no usable pre-onset frame
        double _postWeightSum, _preWeightSum, _postMsw, _attackMs;
        readonly double _silenceRms;

        public int SampleRate { get; }
        public AudioConfig Config => _cfg;

        public PluckVerifier(AudioConfig config, int sampleRate)
        {
            _cfg = config ?? throw new ArgumentNullException(nameof(config));
            _yin = new YinPitchTracker(config, sampleRate);
            SampleRate = sampleRate;
            _frame = config.FrameSize;
            _hop = config.HopSize;
            _skip = (int)Math.Round(config.PluckAttackSkipSec * sampleRate);
            _analysis = Math.Max(_frame, (int)Math.Round(config.PluckAnalysisSec * sampleRate));
            _ringOut = (int)Math.Round(config.RingOutSec * sampleRate);
            _ringStep = Math.Max(1, (int)Math.Round(config.RingStepSec * sampleRate));
            int maxFrames = (_analysis - _frame) / _hop + 2;
            _hz = new double[maxFrames];
            _conf = new float[maxFrames];
            _post = new float[_frame];
            _pre = new float[_frame];
            _probe = new float[_frame];
            _hann = new float[_frame];
            for (int i = 0; i < _frame; i++) _hann[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (_frame - 1)));
            _silenceRms = Math.Pow(10.0, config.SilenceRmsDbfs / 20.0);
        }

        /// <summary>Samples needed after the onset for a full verdict including ring-out.</summary>
        public int SamplesAfterOnsetForRing => _ringOut + _frame / 2;
        /// <summary>Samples needed after the onset for a pitch verdict (no ring-out).</summary>
        public int SamplesAfterOnsetForPitch => _skip + _frame;
        /// <summary>Pre-onset samples the rise tests use (hop + frame).</summary>
        public int PreRollSamples => _hop + _frame;

        /// <summary>Draft-contract shape (<c>IPluckVerifier.Verify</c>): the whole array is the window.</summary>
        public PluckResult Verify(float[] window, int sampleRate, int onsetIndex, int stringNumber, int expectedMidi)
        {
            if (sampleRate != SampleRate) throw new ArgumentException($"verifier built for {SampleRate} Hz, got {sampleRate}", nameof(sampleRate));
            if (window == null) throw new ArgumentNullException(nameof(window));
            return Verify(window, 0, window.Length, onsetIndex, stringNumber, expectedMidi, null);
        }

        /// <summary>
        /// Verifies <c>samples[offset … offset+count)</c>; <paramref name="onsetIndex"/> is relative to <paramref name="offset"/>.
        /// Include ≥ <see cref="PreRollSamples"/> before the onset (rise tests) and ≥ <see cref="SamplesAfterOnsetForRing"/> after it
        /// (ring-out); with less, the rise test assumes silence before and ring-out is not judged.
        /// </summary>
        /// <param name="stringNumber">1 = high E … 6 = low E.</param>
        /// <param name="expectedMidi">The note that string should give; <see cref="ChordShape.Muted"/> (−1) = it should stay silent.</param>
        /// <param name="chord">Optional: neighbour hypotheses use this chord's notes (else the neighbours' open and same-fret notes).</param>
        public PluckResult Verify(float[] samples, int offset, int count, int onsetIndex, int stringNumber, int expectedMidi,
                                  ChordShape chord = null)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            if (offset < 0 || count < 0 || offset > samples.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            Tuning.CheckString(stringNumber);
            if (expectedMidi != ChordShape.Muted && (expectedMidi < 0 || expectedMidi > 127)) throw new ArgumentOutOfRangeException(nameof(expectedMidi));

            int openMidi = Tuning.StandardMidi(stringNumber);
            bool fretted = expectedMidi > openMidi;
            int end = offset + count;
            int onset = offset + Math.Max(0, Math.Min(count, onsetIndex));

            for (int i = offset; i < end; i++)
            {
                float v = samples[i];
                if (float.IsNaN(v) || float.IsInfinity(v)) return Unclear(stringNumber, expectedMidi, fretted, 0f, 0, 0);
            }

            // 1. Attack rise.
            int preEnd = Math.Max(offset, onset - _hop);
            int preStart = Math.Max(offset, preEnd - _frame);
            double preRms = Rms(samples, preStart, preEnd - preStart);
            double attackRms = 0;
            int aEnd = Math.Min(end, onset + _skip + _hop);
            for (int b = Math.Max(offset, onset - _hop); b < aEnd; b += _hop / 2)
                attackRms = Math.Max(attackRms, Rms(samples, b, Math.Min(_hop, aEnd - b)));
            _attackMs = attackRms * attackRms;
            bool struck = attackRms > _silenceRms && attackRms >= preRms * Math.Pow(10.0, _cfg.PluckAttackRiseDb / 20.0);

            // 2. YIN over the analysis frames.
            int first = onset + _skip;
            int lastStart = first + _analysis - _frame;
            int frames = 0, pitched = 0;
            double energy = 0;
            for (int st = first; st + _frame <= end && st <= lastStart; st += _hop)
            {
                var r = _yin.Process(samples, st, _frame);
                frames++;
                energy += (double)r.Rms * r.Rms;
                if (r.HasPitch && r.Confidence >= _cfg.PluckMinConfidence) { _hz[pitched] = r.Hz; _conf[pitched] = r.Confidence; pitched++; }
            }
            float rms = frames > 0 ? (float)Math.Sqrt(energy / frames) : 0f;
            if (frames == 0) return Unclear(stringNumber, expectedMidi, fretted, rms, 0, 0);

            // Windowed copies for the harmonic tests: the first analysis frame and the pre-onset frame.
            WindowInto(samples, first, _frame, _post, out _postWeightSum, out _postMsw);
            _preLen = preEnd - preStart >= _frame / 2 ? preEnd - preStart : 0;
            if (_preLen > 0) WindowInto(samples, preStart, _preLen, _pre, out _preWeightSum, out _);

            bool hasPitch = pitched > 0 && pitched >= Math.Min(_cfg.PluckMinPitchedFrames, frames);
            double f = 0; float conf = 0f;
            if (hasPitch)
            {
                SortPair(pitched);
                f = _hz[(pitched - 1) / 2];          // lower median: never averages two octaves
                conf = _conf[(pitched - 1) / 2];
            }

            // Should-be-silent string: anything newly pitched is an extra string.
            if (expectedMidi == ChordShape.Muted)
            {
                if (hasPitch && Rose(f))
                    return new PluckResult(stringNumber, expectedMidi, f, 0, PluckVerdict.ExtraString, PluckTipKeys.SkipString,
                                           RoundMidi(f), stringNumber, conf, rms, 0, false, pitched, frames);
                return new PluckResult(stringNumber, expectedMidi, 0, 0, PluckVerdict.Correct, null, -1, 0, conf, rms, 0, false, pitched, frames);
            }

            if (!struck && !hasPitch) return Unclear(stringNumber, expectedMidi, fretted, rms, pitched, frames);

            // 3. Hypotheses + match (+ octave alternatives with harmonic support).
            BuildHypotheses(stringNumber, expectedMidi, openMidi, chord);
            int h = -1;
            double fUsed = f;
            bool oldRing = false, fromSalience = false;
            if (!hasPitch)
            {
                // Struck, but YIN found too few confident frames (noise lowers its confidence before it hides the note):
                // test the hypotheses directly. A deadened string leaves nothing salient after the attack skip → Muted.
                h = SalienceFallback();
                fromSalience = true;
                oldRing = true;
            }
            else
                h = Match(f, out fUsed);
            if (hasPitch && h >= 0 && !Rose(Midi.ToHz(_hypMidi[h])))
            {
                // 4. The pitch was already ringing before this onset: look for a hypothesis that rose instead.
                oldRing = true;
                h = SalienceFallback();
                fromSalience = true;
            }
            else if (hasPitch && h < 0)
            {
                // No hypothesis explains the median (often two strings ringing at once confuse YIN): ask the hypotheses
                // directly. If none rose and the median pitch did not rise either, an old note rang and nothing new sounded.
                oldRing = !Rose(f);
                h = SalienceFallback();
                fromSalience = true;
            }
            if (h < 0)
            {
                if (oldRing)
                {
                    if (!struck) return Unclear(stringNumber, expectedMidi, fretted, rms, pitched, frames);
                    return new PluckResult(stringNumber, expectedMidi, 0, 0, PluckVerdict.Muted, PluckTipKeys.For(PluckVerdict.Muted, fretted),
                                           -1, 0, 0f, rms, 0, false, pitched, frames);
                }
                // A new pitch that no hypothesis explains.
                return new PluckResult(stringNumber, expectedMidi, f, Midi.CentsBetween(Midi.ToHz(expectedMidi), f), PluckVerdict.Unclear,
                                       PluckTipKeys.PluckAgain, -1, 0, conf, rms, 0, false, pitched, frames);
            }
            if (fromSalience) fUsed = Midi.ToHz(_hypMidi[h]);

            var verdict = _hypVerdict[h];
            double cents = Midi.CentsBetween(Midi.ToHz(expectedMidi), fUsed);

            // 5. Ring-out (Correct only).
            double ringSec = 0; bool judged = false;
            if (verdict == PluckVerdict.Correct)
            {
                judged = MeasureRing(samples, offset, onset, end, Midi.ToHz(expectedMidi), out ringSec);
                if (judged && ringSec < _cfg.RingOutSec - 1e-9) verdict = PluckVerdict.ShortRing;
            }
            return new PluckResult(stringNumber, expectedMidi, fUsed, cents, verdict, PluckTipKeys.For(verdict, fretted),
                                   _hypMidi[h], _hypString[h], conf, rms, ringSec, judged, pitched, frames);
        }

        static PluckResult Unclear(int s, int expectedMidi, bool fretted, float rms, int pitched, int frames) =>
            new PluckResult(s, expectedMidi, 0, 0, PluckVerdict.Unclear, PluckTipKeys.PluckAgain, -1, 0, 0f, rms, 0, false, pitched, frames);

        static int RoundMidi(double hz)
        {
            double m = Midi.FromHz(hz);
            return double.IsNaN(m) ? -1 : (int)Math.Round(m);
        }

        // Precedence order = insertion order; a note already present keeps its earlier (higher-precedence) verdict.
        void BuildHypotheses(int s, int expected, int open, ChordShape chord)
        {
            _hypCount = 0;
            AddHyp(expected, PluckVerdict.Correct, s);
            if (expected > open) AddHyp(open, PluckVerdict.OpenStringInstead, s);
            for (int d = 1; d <= 2; d++)
            {
                if (expected - d >= open) AddHyp(expected - d, PluckVerdict.WrongFretLow, s);
                AddHyp(expected + d, PluckVerdict.WrongFretHigh, s);
            }
            int fret = expected - open;
            for (int k = 0; k < 2; k++)
            {
                int n = k == 0 ? s + 1 : s - 1;                 // bass-side neighbour first
                if (n < 1 || n > Tuning.StringCount) continue;
                int nOpen = Tuning.StandardMidi(n);
                if (chord != null)
                {
                    int nm = chord.MidiOn(n);
                    AddHyp(nm == ChordShape.Muted ? nOpen : nm, PluckVerdict.WrongString, n);
                }
                else
                {
                    AddHyp(nOpen, PluckVerdict.WrongString, n);
                    if (fret > 0) AddHyp(nOpen + fret, PluckVerdict.WrongString, n);
                }
            }
        }

        void AddHyp(int midi, PluckVerdict v, int s)
        {
            if (midi < 0 || _hypCount >= MaxHyp) return;
            for (int i = 0; i < _hypCount; i++) if (_hypMidi[i] == midi) return;
            _hypMidi[_hypCount] = midi; _hypVerdict[_hypCount] = v; _hypString[_hypCount] = s; _hypCount++;
        }

        int Nearest(double f, out double absCents)
        {
            int best = -1; absCents = double.MaxValue;
            for (int i = 0; i < _hypCount; i++)
            {
                double a = Math.Abs(Midi.CentsBetween(Midi.ToHz(_hypMidi[i]), f));
                if (a < absCents) { absCents = a; best = i; }   // strict <: earlier hypothesis wins exact ties (precedence)
            }
            return best;
        }

        int Match(double f, out double fUsed)
        {
            fUsed = f;
            int h = Nearest(f, out double a);
            if (h >= 0 && a <= _cfg.PluckMatchCents) return h;
            // YIN read an octave high (weak fundamental): f/2 needs real energy at f/2's odd partials.
            int down = Nearest(f * 0.5, out double aDown);
            if (down >= 0 && aDown <= _cfg.PluckMatchCents && SubRatio(f) >= _cfg.PluckOctaveSupportRatio) { fUsed = f * 0.5; return down; }
            // YIN read an octave low (sub-octave lock): 2f needs f's odd partials to be weak.
            int up = Nearest(f * 2.0, out double aUp);
            if (up >= 0 && aUp <= _cfg.PluckMatchCents && SubRatio(f * 2.0) < _cfg.PluckOctaveSupportRatio) { fUsed = f * 2.0; return up; }
            return -1;
        }

        // Power at g/2's odd partials (g/2, 3g/2 …) over power at g's own partials (g, 2g …) in the first analysis frame.
        double SubRatio(double g)
        {
            double own = 0, sub = 0, nyq = SampleRate * 0.5;
            for (int k = 1; k <= _cfg.PluckPartials; k++)
            {
                if (k * g < nyq) own += Goertzel(_post, _frame, k * g);
                if ((k - 0.5) * g < nyq) sub += Goertzel(_post, _frame, (k - 0.5) * g);
            }
            return own > 1e-30 ? sub / own : 0.0;
        }

        // Partial power (normalised to amplitude²/4 per sinusoid) of the first PluckPartials partials of hz.
        double PartialPower(float[] xw, int n, double weightSum, double hz, int partials)
        {
            if (!(weightSum > 0)) return 0;
            double p = 0, nyq = SampleRate * 0.5;
            for (int k = 1; k <= partials; k++) { if (k * hz >= nyq) break; p += Goertzel(xw, n, k * hz); }
            return p / (weightSum * weightSum);
        }

        bool Rose(double hz)
        {
            if (_preLen == 0) return true;   // no pre-roll: assume silence before the onset
            double post = PartialPower(_post, _frame, _postWeightSum, hz, _cfg.PluckPartials);
            double pre = PartialPower(_pre, _preLen, _preWeightSum, hz, _cfg.PluckPartials);
            return post > 1e-20 && post >= pre * Math.Pow(10.0, _cfg.PluckRiseDb / 10.0);
        }

        int SalienceFallback()
        {
            if (!(_postMsw > 1e-20)) return -1;
            if (_postMsw < _attackMs * Math.Pow(10.0, -_cfg.PluckSustainDb / 10.0)) return -1;
            int best = -1; double bestS = 0;
            for (int i = 0; i < _hypCount; i++)
            {
                double hz = Midi.ToHz(_hypMidi[i]);
                double s = 2.0 * PartialPower(_post, _frame, _postWeightSum, hz, _cfg.PluckPartials) / _postMsw;
                if (s >= _cfg.PluckMinSalience && s > bestS && Rose(hz)) { bestS = s; best = i; }
            }
            return best;
        }

        // Sustain of hz from the onset, as a decay-rate estimate rather than a threshold crossing (a crossing near the noise
        // floor flips with every noise frame). Probes every RingStepSec from the first analysis frame to RingOutSec keep the
        // ones clearly above the floor (pre-onset level of the same partials, averaged over up to 3 frames, + RingFloorDb);
        // two probes in a row under the floor end the scan. A least-squares line through the kept probes (dB vs s) gives the
        // decay rate; sustain = time for the line to fall RingDropDb from its start. If the scan ended while the line still
        // predicted a level RingCutDb above the floor, the note was cut (a finger damped it): sustain = the last kept probe.
        bool MeasureRing(float[] x, int lo, int onset, int end, double hz, out double ringSec)
        {
            ringSec = 0;
            int half = _frame / 2;
            if (onset + _ringOut + half > end) return false;
            int partials = _cfg.RingPartials;
            double pre = 0; int preFrames = 0;
            for (int k = 0; k < 3; k++)
            {
                int st = onset - _hop - _frame - k * 2 * _hop;
                if (st < lo) break;
                pre += ProbePower(x, st, hz, partials); preFrames++;
            }
            double floor = Math.Max(1e-20, preFrames > 0 ? pre / preFrames * Math.Pow(10.0, _cfg.RingFloorDb / 10.0) : 0);
            double sr = SampleRate;
            int t0 = _skip + half;                               // centre of the first analysis frame
            int n = 0, below = 0, stopT = _ringOut;
            double st_ = 0, sl = 0, stt = 0, stl = 0, lastKept = 0;
            for (int t = t0; ; t += _ringStep)
            {
                if (t > _ringOut) t = _ringOut;
                double p = ProbePower(x, onset + t - half, hz, partials);
                if (p > floor)
                {
                    double ts = t / sr, l = 10.0 * Math.Log10(p);
                    n++; st_ += ts; sl += l; stt += ts * ts; stl += ts * l; lastKept = ts; below = 0;
                }
                else if (++below >= 2) { stopT = t; break; }
                if (t == _ringOut) break;
            }
            if (n == 0) return true;                             // judged, ringSec = 0
            if (n == 1) { ringSec = lastKept; return true; }
            double den = n * stt - st_ * st_;
            double slope = den > 1e-12 ? (n * stl - st_ * sl) / den : 0;   // dB/s, negative while decaying
            double icpt = (sl - slope * st_) / n;
            double tStart = t0 / sr;
            double sustain = slope < -1e-9 ? tStart + _cfg.RingDropDb / -slope : double.MaxValue;
            if (stopT < _ringOut)
            {
                double predicted = icpt + slope * (stopT / sr);
                if (predicted > 10.0 * Math.Log10(floor) + _cfg.RingCutDb) sustain = Math.Min(sustain, lastKept);
            }
            ringSec = Math.Min(_cfg.RingOutSec, Math.Max(0, sustain));
            return true;
        }

        double ProbePower(float[] x, int start, double hz, int partials)
        {
            WindowInto(x, start, _frame, _probe, out double ws, out _);
            return PartialPower(_probe, _frame, ws, hz, partials);
        }

        // Hann-windowed, DC-removed copy of x[start … start+n) into dst; returns Σw and the weighted mean square Σ(xw)²/Σw².
        void WindowInto(float[] x, int start, int n, float[] dst, out double weightSum, out double msw)
        {
            double mean = 0;
            for (int i = 0; i < n; i++) mean += x[start + i];
            mean /= n;
            double ws = 0, w2 = 0, e = 0;
            bool full = n == _frame;
            for (int i = 0; i < n; i++)
            {
                double w = full ? _hann[i] : 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (n - 1));
                double v = (x[start + i] - mean) * w;
                dst[i] = (float)v;
                ws += w; w2 += w * w; e += v * v;
            }
            weightSum = ws;
            msw = w2 > 0 ? e / w2 : 0;
        }

        // Generalised Goertzel: |X(f)|² of a windowed frame at an arbitrary frequency.
        double Goertzel(float[] xw, int n, double f)
        {
            double w = 2.0 * Math.PI * f / SampleRate, coeff = 2.0 * Math.Cos(w);
            double s1 = 0, s2 = 0;
            for (int i = 0; i < n; i++) { double s0 = xw[i] + coeff * s1 - s2; s2 = s1; s1 = s0; }
            double p = s1 * s1 + s2 * s2 - coeff * s1 * s2;
            return p > 0 ? p : 0;
        }

        static double Rms(float[] x, int start, int n)
        {
            if (n <= 0) return 0;
            double mean = 0;
            for (int i = 0; i < n; i++) mean += x[start + i];
            mean /= n;
            double e = 0;
            for (int i = 0; i < n; i++) { double v = x[start + i] - mean; e += v * v; }
            return Math.Sqrt(e / n);
        }

        // Insertion sort of _hz[0..n) carrying _conf along (n ≤ ~16; deterministic, allocation-free).
        void SortPair(int n)
        {
            for (int i = 1; i < n; i++)
            {
                double h = _hz[i]; float c = _conf[i]; int j = i - 1;
                while (j >= 0 && _hz[j] > h) { _hz[j + 1] = _hz[j]; _conf[j + 1] = _conf[j]; j--; }
                _hz[j + 1] = h; _conf[j + 1] = c;
            }
        }
    }
}
