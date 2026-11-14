using System;

namespace UntitledGuitarApp.Core.Audio
{
    /// <summary>
    /// Every tunable number of the audio engine (MASTER_PLAN §19). Immutable; build variants with the named optional
    /// constructor arguments, e.g. <c>new AudioConfig(yinThreshold: 0.15)</c>. Threshold changes need
    /// ./scripts/audio-eval.sh numbers before/after in the commit.
    /// </summary>
    public sealed class AudioConfig
    {
        // ---- Framing ----
        /// <summary>Analysis frame, samples. 2048 @ 48 kHz = 42.7 ms: after reserving the longest lag (70 Hz ≈ 686 samples)
        /// YIN still integrates over ~1360 samples ≈ 2.3 periods of low E — enough for a stable dip, short enough to track a peg turn.</summary>
        public int FrameSize { get; }
        /// <summary>Hop between frames, samples. 512 @ 48 kHz = 10.7 ms ≈ 94 needle updates/s; matches the onset detector's hop (§19).</summary>
        public int HopSize { get; }

        // ---- YIN search range ----
        /// <summary>Lowest searchable f0, Hz. Below drop-D D2 (73.4 Hz) so a badly flat low E is still found. Full range on purpose:
        /// a narrow window around the expected note turns wrong-string plucks into false "muted" (audio-dsp rule).</summary>
        public double MinHz { get; }
        /// <summary>Highest searchable f0, Hz. Above B5 (987.8 Hz, high E fret 19), covering every note a beginner plays.</summary>
        public double MaxHz { get; }

        // ---- YIN decisions ----
        /// <summary>Absolute threshold on the cumulative-mean-normalised difference d′(τ): the first dip below it is the period (§19: 0.12;
        /// de Cheveigné &amp; Kawahara 2002 use 0.10–0.15).</summary>
        public double YinThreshold { get; }
        /// <summary>Second-pass threshold when no dip is under <see cref="YinThreshold"/>: accept the first dip under this one
        /// (noisy but still pitched). 0.2 = confidence 0.8, the pluck check's minimum (§19), so anything weaker is "no pitch".</summary>
        public double MaxAperiodicity { get; }
        /// <summary>Frames quieter than this (dBFS RMS, DC removed) are Silent without running YIN. −60 dBFS sits well under a guitar at
        /// 50 cm (≈ −35…−15 dBFS) and above a quiet room; the calibrated noise floor (setup, §19) will replace it.</summary>
        public double SilenceRmsDbfs { get; }

        // ---- Octave correction (harmonic support) ----
        /// <summary>Correct YIN octave errors by harmonic support, both ways: down when the period found is half the true one (dominant
        /// 2nd harmonic, weak fundamental — common on low E); up when it is 2–4× the true one (attack transient, low SNR).</summary>
        public bool OctaveCorrection { get; }
        /// <summary>Sub-octave odd partials (f/2, 3f/2, 5f/2) must carry at least this fraction of the power of the candidate's own partials
        /// (f, 2f, 3f) to move the pitch down an octave (and a candidate period kT whose extra partials carry less moves up to T). 0.01 = −20 dB: a true f leaks ≤ −40 dB into bins 3.5 bins away (Hann, 2048 @
        /// 48 kHz), so 20 dB of headroom against false corrections, while a weak-but-real fundamental at −16 dB is still caught.</summary>
        public double OctaveSupportRatio { get; }
        /// <summary>Partials summed on each side of the octave test. 3 covers the strong low partials of a plucked string without reaching
        /// the inharmonic upper partials.</summary>
        public int OctaveCheckPartials { get; }
        /// <summary>Stepping down an octave also needs d′(2τ) below this fraction of d′(τ). A weak fundamental gives d′(2τ) ≪ d′(τ)
        /// (≈ 0.1×); white noise at 10 dB SNR makes the two dips equal ± jitter and alone puts ≈ 0.007 into the sub-octave bins,
        /// so 0.5 keeps noise from faking support (synthetic sweep: D#3 at 10 dB read D#2 without it).</summary>
        public double OctaveDownDipRatio { get; }
        /// <summary>A period τ/k whose d′ is within this of the chosen τ's replaces it (YIN prefers the shortest good-enough lag).
        /// 0.03 ≈ the dip jitter from white noise at 10 dB SNR; well under the gap a real weak-fundamental note shows (≥ 0.1).</summary>
        public double OctaveUpSlack { get; }

        // ---- Tuner ----
        /// <summary>±cents counted as "in tune". 5 cents ≈ the just-noticeable difference and the green zone of clip-on tuners;
        /// SL1's device check compares against a clip-on within 10.</summary>
        public double TunerInTuneCents { get; }
        /// <summary>Needle smoothing time constant, s (one-pole on cents). 0.12 s removes frame jitter but follows a peg turn within ~0.3 s.</summary>
        public double TunerSmoothingSec { get; }
        /// <summary>A gap without pitch longer than this (s) restarts the smoothing, so a new pluck shows its own pitch at once.</summary>
        public double TunerHoldSec { get; }

        public AudioConfig(
            int frameSize = 2048,
            int hopSize = 512,
            double minHz = 70.0,
            double maxHz = 1000.0,
            double yinThreshold = 0.12,
            double maxAperiodicity = 0.2,
            double silenceRmsDbfs = -60.0,
            bool octaveCorrection = true,
            double octaveSupportRatio = 0.01,
            int octaveCheckPartials = 3,
            double octaveDownDipRatio = 0.5,
            double octaveUpSlack = 0.03,
            double tunerInTuneCents = 5.0,
            double tunerSmoothingSec = 0.12,
            double tunerHoldSec = 0.25)
        {
            if (frameSize < 256) throw new ArgumentOutOfRangeException(nameof(frameSize));
            if (hopSize < 1 || hopSize > frameSize) throw new ArgumentOutOfRangeException(nameof(hopSize));
            if (!(minHz > 0) || !(maxHz > minHz)) throw new ArgumentOutOfRangeException(nameof(maxHz));
            if (!(yinThreshold > 0) || !(yinThreshold < 1)) throw new ArgumentOutOfRangeException(nameof(yinThreshold));
            if (!(maxAperiodicity >= yinThreshold) || !(maxAperiodicity < 1)) throw new ArgumentOutOfRangeException(nameof(maxAperiodicity));
            if (double.IsNaN(silenceRmsDbfs) || silenceRmsDbfs > 0) throw new ArgumentOutOfRangeException(nameof(silenceRmsDbfs));
            if (!(octaveSupportRatio > 0)) throw new ArgumentOutOfRangeException(nameof(octaveSupportRatio));
            if (octaveCheckPartials < 1 || octaveCheckPartials > 8) throw new ArgumentOutOfRangeException(nameof(octaveCheckPartials));
            if (!(octaveDownDipRatio > 0) || octaveDownDipRatio > 1) throw new ArgumentOutOfRangeException(nameof(octaveDownDipRatio));
            if (!(octaveUpSlack >= 0) || octaveUpSlack > 0.5) throw new ArgumentOutOfRangeException(nameof(octaveUpSlack));
            if (!(tunerInTuneCents > 0)) throw new ArgumentOutOfRangeException(nameof(tunerInTuneCents));
            if (!(tunerSmoothingSec >= 0)) throw new ArgumentOutOfRangeException(nameof(tunerSmoothingSec));
            if (!(tunerHoldSec > 0)) throw new ArgumentOutOfRangeException(nameof(tunerHoldSec));

            FrameSize = frameSize; HopSize = hopSize; MinHz = minHz; MaxHz = maxHz;
            YinThreshold = yinThreshold; MaxAperiodicity = maxAperiodicity; SilenceRmsDbfs = silenceRmsDbfs;
            OctaveCorrection = octaveCorrection; OctaveSupportRatio = octaveSupportRatio; OctaveCheckPartials = octaveCheckPartials;
            OctaveDownDipRatio = octaveDownDipRatio; OctaveUpSlack = octaveUpSlack;
            TunerInTuneCents = tunerInTuneCents; TunerSmoothingSec = tunerSmoothingSec; TunerHoldSec = tunerHoldSec;
        }

        public static readonly AudioConfig Default = new AudioConfig();
    }
}
