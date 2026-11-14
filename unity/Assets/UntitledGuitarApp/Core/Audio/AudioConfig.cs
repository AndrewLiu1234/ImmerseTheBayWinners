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

        // ---- Pluck check (SL2, §19 "Pluck verdict" + "Clean / quality") ----
        /// <summary>Attack skipped after the onset, s (§19: 40 ms). The pick transient is broadband and its first periods are
        /// inharmonic; YIN on them reads octave errors.</summary>
        public double PluckAttackSkipSec { get; }
        /// <summary>Window after the attack over which the median f0 is taken, s (§19: 200 ms ≈ 11 hops). Long enough to outvote
        /// a bad frame, short enough to stay inside the ring of a short-ringing pluck.</summary>
        public double PluckAnalysisSec { get; }
        /// <summary>Minimum YIN confidence for a frame to vote (§19: &gt; 0.8; = 1 − <see cref="MaxAperiodicity"/>).</summary>
        public double PluckMinConfidence { get; }
        /// <summary>Confidently pitched analysis frames needed for the pluck to have a pitch (fewer when the window holds fewer
        /// frames). 3 frames = pitch audible from 40 to ≥ 125 ms: a muted thud dies inside the 40 ms skip (0 frames), while a
        /// short-ringing note still pitches its first 3–5 frames even at 10 dB SNR. A fraction of all frames failed here:
        /// noise swallows a short ring's later frames (synthetic grid: ShortRing 0/52 at 20 dB with "≥ 40 %").</summary>
        public int PluckMinPitchedFrames { get; }
        /// <summary>A median f0 within this many cents of a hypothesis matches it (flag audio.pluckCentsTol = 45). Hypotheses
        /// are ≥ 100 cents apart, so 45 leaves a 10-cent no-man's-land (→ Unclear) and tolerates a slightly out-of-tune string.</summary>
        public double PluckMatchCents { get; }
        /// <summary>Octave alternatives (f/2, 2f of the YIN median) are tried only when the direct reading matches no hypothesis,
        /// and need harmonic support: power at f's sub-octave odd partials vs f's own partials at or above (f/2 accepted) or
        /// below (2f accepted) this ratio. 0.05 = −13 dB: looser than YIN's own 0.01 because the expected-note prior already
        /// narrows the choice to a handful of notes.</summary>
        public double PluckOctaveSupportRatio { get; }
        /// <summary>Harmonics of a hypothesis summed for the rise/salience tests. 4 covers the strong low partials of a plucked
        /// string; the fundamental alone is weak on low E through a laptop mic.</summary>
        public int PluckPartials { get; }
        /// <summary>The matched note's partial power must rise by this much from the pre-onset frame to the first analysis
        /// frame (dB, §19 "onset-relative spectral rise"), so a string still ringing from the previous pluck is not credited to
        /// this one. 6 dB = a fresh pluck at least doubles the amplitude of a string that still rings at half level.</summary>
        public double PluckRiseDb { get; }
        /// <summary>Broadband attack rise (dB: max 512-sample block RMS around the onset vs the pre-onset RMS) meaning "the pick
        /// hit something". A pitchless window without it is Unclear (nothing played), with it Muted. 3 dB ≈ 10× the
        /// block-to-block RMS jitter of steady noise.</summary>
        public double PluckAttackRiseDb { get; }
        /// <summary>Fallback when YIN gives no usable pitch (too few confident frames, or a pre-existing ring): a hypothesis whose
        /// <see cref="PluckPartials"/> partials rose by <see cref="PluckRiseDb"/> and carry at least this fraction of the first
        /// analysis frame's energy is taken as the note. 0.05: white noise alone puts ≈ 0.01 there (synthetic, 4 partials), a
        /// short-ringing D3 at 10 dB SNR ≈ 0.09, a free note ≈ 0.2 (Karplus–Strong spreads energy over many partials).</summary>
        public double PluckMinSalience { get; }
        /// <summary>… and the first analysis frame must be no more than this many dB below the attack (else the "note" is the
        /// residue of a deadened string: a muted thud is ≈ 50 dB down 40 ms after the pick, a short ring ≈ 8 dB).</summary>
        public double PluckSustainDb { get; }
        /// <summary>Ring-out target, s (§19 quality: every expected string still rings ≥ 0.8 s). Shorter → ShortRing
        /// ("let string N ring"). Judged only when the window reaches this far past the onset.</summary>
        public double RingOutSec { get; }
        /// <summary>Partials tracked for ring-out. 2 (fundamental + octave): higher ones collide with a neighbouring string that
        /// still rings (G3's 5th partial 980 Hz sits 8 Hz from B3's 4th, 988 Hz — synthetic: a free B3 over a ringing G3 read
        /// 0.56 s with 4 partials) and die first on any real string anyway.</summary>
        public int RingPartials { get; }
        /// <summary>Sustain = time for the fitted decay of the first <see cref="RingPartials"/> partials to fall this many dB
        /// from the first analysis frame. 30 dB: a free steel string (T60 ≥ 2 s) needs ≥ 1 s for it; a finger leaning on the
        /// string (T60 ≈ 0.8 s) gets there in 0.4 s. Pass line: T60 ≈ 1.5 s.</summary>
        public double RingDropDb { get; }
        /// <summary>Ring probes count only when this many dB above the same partials' pre-onset level (room noise, other
        /// strings). 6 dB: a pure-noise probe (2 partials ≈ χ² with 4 dof) passes 4× its mean 0.3 % of the time, vs 9 % at 3 dB —
        /// a 3 dB floor let the noise extend a short ring to 0.8 s at 20 dB SNR (synthetic). The decay fit, not the last
        /// crossing, decides the sustain, so a note sinking into the noise early is not penalised.</summary>
        public double RingFloorDb { get; }
        /// <summary>If the probes vanish while the fitted decay still predicts a level this many dB above the floor, the note was
        /// cut (a finger damped it) and the sustain is the last audible probe. 10 dB ≫ the ±3 dB probe jitter.</summary>
        public double RingCutDb { get; }
        /// <summary>Step between ring-out probes, s. 50 ms resolution on the reported sustain is plenty for a "let it ring" tip
        /// and costs ~16 two-partial Goertzels instead of 75.</summary>
        public double RingStepSec { get; }

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
            double tunerHoldSec = 0.25,
            double pluckAttackSkipSec = 0.040,
            double pluckAnalysisSec = 0.200,
            double pluckMinConfidence = 0.8,
            int pluckMinPitchedFrames = 3,
            double pluckMatchCents = 45.0,
            double pluckOctaveSupportRatio = 0.05,
            int pluckPartials = 4,
            double pluckRiseDb = 6.0,
            double pluckAttackRiseDb = 3.0,
            double pluckMinSalience = 0.05,
            double pluckSustainDb = 20.0,
            double ringOutSec = 0.8,
            int ringPartials = 2,
            double ringDropDb = 30.0,
            double ringFloorDb = 6.0,
            double ringCutDb = 10.0,
            double ringStepSec = 0.05)
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
            if (!(pluckAttackSkipSec >= 0) || pluckAttackSkipSec > 0.5) throw new ArgumentOutOfRangeException(nameof(pluckAttackSkipSec));
            if (!(pluckAnalysisSec > 0) || pluckAnalysisSec > 2) throw new ArgumentOutOfRangeException(nameof(pluckAnalysisSec));
            if (!(pluckMinConfidence >= 0) || pluckMinConfidence > 1) throw new ArgumentOutOfRangeException(nameof(pluckMinConfidence));
            if (pluckMinPitchedFrames < 1) throw new ArgumentOutOfRangeException(nameof(pluckMinPitchedFrames));
            if (!(pluckMatchCents > 0) || pluckMatchCents >= 100) throw new ArgumentOutOfRangeException(nameof(pluckMatchCents));
            if (!(pluckOctaveSupportRatio > 0)) throw new ArgumentOutOfRangeException(nameof(pluckOctaveSupportRatio));
            if (pluckPartials < 1 || pluckPartials > 8) throw new ArgumentOutOfRangeException(nameof(pluckPartials));
            if (!(pluckRiseDb >= 0)) throw new ArgumentOutOfRangeException(nameof(pluckRiseDb));
            if (!(pluckAttackRiseDb >= 0)) throw new ArgumentOutOfRangeException(nameof(pluckAttackRiseDb));
            if (!(pluckMinSalience > 0) || pluckMinSalience > 1) throw new ArgumentOutOfRangeException(nameof(pluckMinSalience));
            if (!(pluckSustainDb > 0)) throw new ArgumentOutOfRangeException(nameof(pluckSustainDb));
            if (!(ringOutSec > 0) || ringOutSec > 5) throw new ArgumentOutOfRangeException(nameof(ringOutSec));
            if (!(ringDropDb > 0)) throw new ArgumentOutOfRangeException(nameof(ringDropDb));
            if (ringPartials < 1 || ringPartials > 8) throw new ArgumentOutOfRangeException(nameof(ringPartials));
            if (!(ringFloorDb >= 0)) throw new ArgumentOutOfRangeException(nameof(ringFloorDb));
            if (!(ringCutDb >= 0)) throw new ArgumentOutOfRangeException(nameof(ringCutDb));
            if (!(ringStepSec > 0)) throw new ArgumentOutOfRangeException(nameof(ringStepSec));

            FrameSize = frameSize; HopSize = hopSize; MinHz = minHz; MaxHz = maxHz;
            YinThreshold = yinThreshold; MaxAperiodicity = maxAperiodicity; SilenceRmsDbfs = silenceRmsDbfs;
            OctaveCorrection = octaveCorrection; OctaveSupportRatio = octaveSupportRatio; OctaveCheckPartials = octaveCheckPartials;
            OctaveDownDipRatio = octaveDownDipRatio; OctaveUpSlack = octaveUpSlack;
            TunerInTuneCents = tunerInTuneCents; TunerSmoothingSec = tunerSmoothingSec; TunerHoldSec = tunerHoldSec;
            PluckAttackSkipSec = pluckAttackSkipSec; PluckAnalysisSec = pluckAnalysisSec; PluckMinConfidence = pluckMinConfidence;
            PluckMinPitchedFrames = pluckMinPitchedFrames; PluckMatchCents = pluckMatchCents;
            PluckOctaveSupportRatio = pluckOctaveSupportRatio; PluckPartials = pluckPartials; PluckRiseDb = pluckRiseDb;
            PluckAttackRiseDb = pluckAttackRiseDb; PluckMinSalience = pluckMinSalience; PluckSustainDb = pluckSustainDb;
            RingOutSec = ringOutSec; RingPartials = ringPartials; RingDropDb = ringDropDb; RingFloorDb = ringFloorDb; RingCutDb = ringCutDb; RingStepSec = ringStepSec;
        }

        public static readonly AudioConfig Default = new AudioConfig();
    }
}
