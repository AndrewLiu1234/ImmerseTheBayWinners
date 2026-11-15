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

        // ---- Onsets (SL6, §19 "Onset": spectral flux + adaptive median threshold) ----
        /// <summary>FFT size for the onset detector, samples (§19: 1024 = 21 ms @ 48 kHz). Short enough to place a strum within one
        /// 512-sample hop; long enough for 47 Hz bins below 4 kHz. Must be a power of two ≥ <see cref="HopSize"/>.</summary>
        public int OnsetFftSize { get; }
        /// <summary>Highest bin used in the flux, Hz. 4 kHz (§19): pick/strum energy is mostly below it, and the optional
        /// metronome click (6–8 kHz) never registers as a strum.</summary>
        public double OnsetMaxHz { get; }
        /// <summary>Log compression log(1 + γ·amplitude) of each bin before differencing. γ = 1000 makes the flux respond to
        /// relative change (a soft pluck on a quiet room counts like a loud strum), and a −60 dBFS bin still sits on the curve's knee.</summary>
        public double OnsetLogGamma { get; }
        /// <summary>SuperFlux maximum filter: each bin is compared with the max of the previous spectrum over ±this many bins
        /// (Böck &amp; Widmer 2013). 1 bin = ±47 Hz: a ringing chord's beating partials stop reading as new energy (synthetic:
        /// a ringing G's flux drops from ≈ 0.2 to ≤ 0.1) while a new pick attack, broadband, still reads ≈ 2.</summary>
        public int OnsetMaxFilterBins { get; }
        /// <summary>The flux compares the newest spectrum with the one this many hops earlier. 2 hops = 21 ms = one full
        /// non-overlapping 1024 frame: a strum's strings arrive over ~30 ms, and with lag 1 each hop saw only part of the rise
        /// (synthetic: re-strums on ringing strings read 0.2–0.4 vs a 0.06 background with lag 1).</summary>
        public int OnsetDiffLagHops { get; }
        /// <summary>Adaptive threshold = k · median(flux over the last <see cref="OnsetMedianSec"/>) + <see cref="OnsetMinFlux"/>
        /// (§19: k = 1.5). Steady noise gives a flux with ≈ 10 % spread, so 1.5× its median is several σ above it.</summary>
        public double OnsetThresholdK { get; }
        /// <summary>Median window, s (§19: 300 ms ≈ 28 hops). Shorter than one slot at the fastest preset (300 ms at 100 bpm
        /// eighths), so ringing chords and room noise set the floor, not the previous strum.</summary>
        public double OnsetMedianSec { get; }
        /// <summary>Absolute flux floor added to the threshold (mean log-compressed rise per bin). Matters right after silence,
        /// when the median is 0: a ringing chord's flux stays ≤ 0.2 between strums, an up-strum over 3–4 ringing top strings
        /// reaches 0.3–0.8, a strum after silence ≈ 2.7 (synthetic, SuperFlux lag 2). 0.15 = 0.30 floor gave up-strum misses
        /// (strum F1 0.67 at 100 % tempo); 0.1 with lag 1 let the ring itself trigger.</summary>
        public double OnsetMinFlux { get; }
        /// <summary>Minimum time between onsets, s (refractory; §19 had 60 ms). A strum's strings must count once: a real beginner
        /// strum spreads over up to ~130 ms (dev recordings: C and G strums re-triggered 106–128 ms after the first onset, when the
        /// treble strings arrived), so 130 ms; the fastest strum pattern we ship (eighths at 100 bpm) is 300 ms apart and 16ths at
        /// 100 bpm (150 ms) still fit.</summary>
        public double OnsetMinGapSec { get; }


        // ---- Strum check (SL3, §19 "Strum verdict" + "Clean / quality"; chord level only) ----
        /// <summary>Analysis window start after the onset, s (§19 had 30 ms). Skips the pick noise, the strum's spread (real
        /// beginner strums: 30–100 ms; the onset fires on the first strings) and the loudest part of the body's air-resonance
        /// thump (≈ 98 Hz, decays ≈ 15 dB in the first 250 ms). Dev set: 30 ms → 68 %, 80 ms → 76 % strum accuracy (alone).</summary>
        public double StrumWindowStartSec { get; }
        /// <summary>Analysis window end after the onset, s (§19 had 250 ms). 220 ms long like before (same resolution); strum-to-verdict
        /// ≤ 350 ms still leaves 50 ms for onset confirmation (≈ 2 hops = 21 ms) and the analysis itself (&lt; 5 ms).</summary>
        public double StrumWindowEndSec { get; }
        /// <summary>FFT size = smallest power of two ≥ this × the window length. 1.5: 220 ms @ 48 kHz (10,560 samples) → 16,384
        /// points, 2.9 Hz bins — under the window's 4.5 Hz resolution, so the nearest bin is never more than a third of a
        /// resolution cell off (and the coverage tables model the exact bins anyway).</summary>
        public double StrumFftPadding { get; }
        /// <summary>Lowest chord note the strum check models, MIDI (E2 = open low E: nothing in standard tuning goes lower).</summary>
        public int StrumMinMidi { get; }
        /// <summary>Highest chord note the strum check models, MIDI. A4 (69) covers every open-position chord note in the
        /// library (G4) plus faults a fret or two sharp.</summary>
        public int StrumMaxMidi { get; }
        /// <summary>Highest semitone probe, MIDI. C6 (1047 Hz) = the 12th partial of low E: high enough for the 2nd–6th partials
        /// of the bass strings (§19: where semitone gaps exceed the window's resolution), low enough that string inharmonicity
        /// (≈ 865·B·n² cents, B ≈ 1e-4 → 12 cents at n = 12) still lands inside a probe.</summary>
        public int StrumProbeMaxMidi { get; }
        /// <summary>Each semitone probe takes the max FFT amplitude within ± this many cents (always ≥ the nearest bin). 30 cents
        /// tolerates a slightly out-of-tune string and inharmonicity while staying clear of the neighbouring semitone (100).</summary>
        public double StrumProbeCents { get; }
        /// <summary>Probe amplitudes minus this × the median FFT amplitude (the broadband noise floor; partials occupy a small
        /// share of the bins, so the median is noise). 2: the max of a few Rayleigh-distributed noise bins sits ≈ 1.5–2× their median.</summary>
        public double StrumNoiseFloorK { get; }
        /// <summary>The loudest probe must stand this many dB above the median FFT amplitude for the window to be a strum (else
        /// Unclear, "strum again"). White noise alone reaches ≤ 11 dB (max of ~45 pooled probes, 20 seeds); the synthetic
        /// grid's strums at 10 dB SNR read ≥ 28 dB.</summary>
        public double StrumMinPeakDb { get; }
        /// <summary>A probe is "explained" by a hypothesis when some partial of one of its notes puts at least this fraction of
        /// its amplitude there (pooled Hann response). 0.1 = −20 dB: inside the main lobe; beyond it only −31 dB sidelobes remain.</summary>
        public double StrumCoverMin { get; }
        /// <summary>A note's own evidence = the probes nearest its partials 1 … this that no other note of the hypothesis
        /// explains. 6 (§19: "harmonics 2–6"): with 4, a bass string whose fundamental the mic rolls off often kept a single
        /// own probe, and clean G/Em strums through a simulated laptop-mic high-pass read "string 6 muted" (held-out sweep:
        /// clean accuracy 90 % with 4, 100 % with 6).</summary>
        public int StrumOwnPartials { get; }
        /// <summary>… and only probes up to this note, MIDI (G5, 784 Hz). Above it the partials of six strings are dense, and a
        /// detuned or inharmonic 12th–13th partial of low E lands on an absent note's probe and makes it look present (held-out
        /// sweep: G#3 "heard" in an Em; clean accuracy 88 % with 84, 100 % with 76–80, 95 % with 72).</summary>
        public int StrumOwnMaxMidi { get; }
        /// <summary>A note is fully present when the upper median of its own probes (after removing the leakage a neighbouring
        /// semitone's peak can put there) reaches this fraction of the loudest probe; below that presence scales linearly.
        /// 0.01 (−40 dB) sits mid-plateau (0.005–0.02 all 100 % held-out): weak fundamentals still count, while a muted
        /// string's thud has decayed ≈ 45 dB by the window start and the noise floor is subtracted.</summary>
        public double StrumPresenceRatio { get; }
        /// <summary>Precision weighs each probe by amplitude^this. 0.5 (square-root compression): a foreign string's few own
        /// partials are not drowned out by the strong low partials of the others (0.3 gave identical synthetic results).</summary>
        public double StrumPrecisionGamma { get; }
        /// <summary>Probes weaker than this fraction of the loudest one are left out of the precision. 0.05 (−26 dB) guards against
        /// Hann sidelobes (−31 dB) and the low-level residue of real recordings (body modes, sympathetic strings) counting as
        /// foreign; 0, 0.02 and 0.05 gave identical synthetic results, and 0.05 beat 0.02 on the dev recordings (Am, C, D).</summary>
        public double StrumPrecisionFloor { get; }
        /// <summary>Clean needs score(expected) − best other chord ≥ this (flag audio.strumMinMargin = 0.15, §19).</summary>
        public double StrumMinMargin { get; }
        /// <summary>A fault hypothesis (the expected chord with one string open / a fret off / muted, or extra bass strings) wins
        /// when it beats the expected chord's score by ≥ this. 0.02: clean strums leave the best fault ≤ 0 (180/180 seeded clean
        /// strums stay clean at 0.02); a foreign note with only two own partials (G with high E open → E4) opens just 0.02–0.04,
        /// so 0.04 let 7/108 such faults through as clean.</summary>
        public double StrumFaultMargin { get; }
        /// <summary>Lowest probe left out of the precision, MIDI (F2, 87 Hz). Every attack on an acoustic guitar rings its air
        /// resonance (A0, typically 90–110 Hz): on the dev recordings a 98.1 Hz peak follows every strum and every single pluck
        /// (even high E), 6–16 dB below the loudest partial in the strum window, and is absent in silence. It is no chord note, so
        /// it cut every hypothesis's precision and sat on G2, making clean D and Em read as G. E2 (40) stays: the low E is
        /// a real bass string (and extra low E is a fault).</summary>
        public int StrumBodyMinMidi { get; }
        /// <summary>Highest probe left out of the precision, MIDI (G#2, 104 Hz): the top of the observed A0 peak and its leakage
        /// (F#2–G#2). A2 (45, open A) stays, so "A string strummed" under D still costs precision. Recall is unaffected: G2 can still
        /// prove G's bass note. Dev set: band 41–44 and 42–44 give the same accuracy (90.1 %); off (−1): D clean 1–4/5.</summary>
        public int StrumBodyMaxMidi { get; }
        /// <summary>A note of a rival hypothesis (confuser chord or fault variant) whose pitch class the expected chord lacks is fully
        /// present only at this fraction of the loudest probe (−22 dB); the expected chord's own notes keep <see cref="StrumPresenceRatio"/>.
        /// Rejecting the expected chord needs clear evidence: on real audio body resonances, the G3 body-mode sidebands and
        /// sympathetic strings leave −30…−40 dB residue that made E's G#3, Dm's F4 and G's B2 look "present" (dev clean
        /// accuracy: 0.03 → 87.3 %, 0.05 → 88.7 %, 0.08 → 90.1 %).</summary>
        public double StrumDistinctPresenceRatio { get; }
        /// <summary>A fault variant that plays a different note (open string, fret off, extra string) can only win when that note,
        /// judged on its own probes, has at least this presence (0 … 1). Otherwise a variant could win just because removing the
        /// expected note re-assigned shared probes to another note (Em with "s4 a fret low" while D#3 was absent). 0.5 = half the
        /// presence level; a variant whose note cannot be judged (no own probes) is unaffected.</summary>
        public double StrumFaultMinPresence { get; }

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
            double ringStepSec = 0.05,
            int onsetFftSize = 1024,
            double onsetMaxHz = 4000.0,
            double onsetLogGamma = 1000.0,
            int onsetMaxFilterBins = 1,
            int onsetDiffLagHops = 2,
            double onsetThresholdK = 1.5,
            double onsetMedianSec = 0.3,
            double onsetMinFlux = 0.15,
            double onsetMinGapSec = 0.13,
            double strumWindowStartSec = 0.080,
            double strumWindowEndSec = 0.300,
            double strumFftPadding = 1.5,
            int strumMinMidi = 40,
            int strumMaxMidi = 69,
            int strumProbeMaxMidi = 84,
            double strumProbeCents = 30.0,
            double strumNoiseFloorK = 2.0,
            double strumMinPeakDb = 20.0,
            double strumCoverMin = 0.1,
            int strumOwnPartials = 6,
            int strumOwnMaxMidi = 79,
            double strumPresenceRatio = 0.01,
            double strumPrecisionGamma = 0.5,
            double strumPrecisionFloor = 0.05,
            double strumMinMargin = 0.15,
            double strumFaultMargin = 0.02,
            int strumBodyMinMidi = 41,
            int strumBodyMaxMidi = 44,
            double strumDistinctPresenceRatio = 0.08,
            double strumFaultMinPresence = 0.5)
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
            if (onsetFftSize < 64 || (onsetFftSize & (onsetFftSize - 1)) != 0 || onsetFftSize < hopSize)
                throw new ArgumentOutOfRangeException(nameof(onsetFftSize), "power of two ≥ hopSize");
            if (!(onsetMaxHz > 0)) throw new ArgumentOutOfRangeException(nameof(onsetMaxHz));
            if (!(onsetLogGamma > 0)) throw new ArgumentOutOfRangeException(nameof(onsetLogGamma));
            if (onsetMaxFilterBins < 0 || onsetMaxFilterBins > 8) throw new ArgumentOutOfRangeException(nameof(onsetMaxFilterBins));
            if (onsetDiffLagHops < 1 || onsetDiffLagHops > 8) throw new ArgumentOutOfRangeException(nameof(onsetDiffLagHops));
            if (!(onsetThresholdK >= 1)) throw new ArgumentOutOfRangeException(nameof(onsetThresholdK));
            if (!(onsetMedianSec > 0) || onsetMedianSec > 5) throw new ArgumentOutOfRangeException(nameof(onsetMedianSec));
            if (!(onsetMinFlux >= 0)) throw new ArgumentOutOfRangeException(nameof(onsetMinFlux));
            if (!(onsetMinGapSec >= 0) || onsetMinGapSec > 2) throw new ArgumentOutOfRangeException(nameof(onsetMinGapSec));

            if (!(strumWindowStartSec >= 0) || !(strumWindowEndSec > strumWindowStartSec + 0.02) || strumWindowEndSec > 1.0)
                throw new ArgumentOutOfRangeException(nameof(strumWindowEndSec));
            if (!(strumFftPadding >= 1) || strumFftPadding > 8) throw new ArgumentOutOfRangeException(nameof(strumFftPadding));
            if (strumMinMidi < 20 || strumMaxMidi <= strumMinMidi || strumMaxMidi > 100) throw new ArgumentOutOfRangeException(nameof(strumMaxMidi));
            if (strumProbeMaxMidi < strumMaxMidi || strumProbeMaxMidi > 120) throw new ArgumentOutOfRangeException(nameof(strumProbeMaxMidi));
            if (!(strumProbeCents >= 0) || strumProbeCents >= 50) throw new ArgumentOutOfRangeException(nameof(strumProbeCents));
            if (!(strumNoiseFloorK >= 0)) throw new ArgumentOutOfRangeException(nameof(strumNoiseFloorK));
            if (!(strumMinPeakDb >= 0)) throw new ArgumentOutOfRangeException(nameof(strumMinPeakDb));
            if (!(strumCoverMin > 0) || strumCoverMin >= 1) throw new ArgumentOutOfRangeException(nameof(strumCoverMin));
            if (strumOwnMaxMidi < strumMinMidi) throw new ArgumentOutOfRangeException(nameof(strumOwnMaxMidi));
            if (strumOwnPartials < 1 || strumOwnPartials > 32) throw new ArgumentOutOfRangeException(nameof(strumOwnPartials));
            if (!(strumPresenceRatio > 0) || strumPresenceRatio > 1) throw new ArgumentOutOfRangeException(nameof(strumPresenceRatio));
            if (!(strumPrecisionGamma > 0) || strumPrecisionGamma > 2) throw new ArgumentOutOfRangeException(nameof(strumPrecisionGamma));
            if (!(strumPrecisionFloor >= 0) || strumPrecisionFloor >= 1) throw new ArgumentOutOfRangeException(nameof(strumPrecisionFloor));
            if (!(strumMinMargin >= 0) || strumMinMargin > 1) throw new ArgumentOutOfRangeException(nameof(strumMinMargin));
            if (!(strumFaultMargin >= 0) || strumFaultMargin > 1) throw new ArgumentOutOfRangeException(nameof(strumFaultMargin));
            if (strumBodyMaxMidi > 120) throw new ArgumentOutOfRangeException(nameof(strumBodyMaxMidi), "≤ 120 (below StrumBodyMinMidi = off)");
            if (!(strumDistinctPresenceRatio >= 0) || strumDistinctPresenceRatio > 1) throw new ArgumentOutOfRangeException(nameof(strumDistinctPresenceRatio));
            if (!(strumFaultMinPresence >= 0) || strumFaultMinPresence > 1) throw new ArgumentOutOfRangeException(nameof(strumFaultMinPresence));

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
            OnsetFftSize = onsetFftSize; OnsetMaxHz = onsetMaxHz; OnsetLogGamma = onsetLogGamma; OnsetMaxFilterBins = onsetMaxFilterBins; OnsetDiffLagHops = onsetDiffLagHops; OnsetThresholdK = onsetThresholdK;
            OnsetMedianSec = onsetMedianSec; OnsetMinFlux = onsetMinFlux; OnsetMinGapSec = onsetMinGapSec;
            StrumWindowStartSec = strumWindowStartSec; StrumWindowEndSec = strumWindowEndSec; StrumFftPadding = strumFftPadding;
            StrumMinMidi = strumMinMidi; StrumMaxMidi = strumMaxMidi; StrumProbeMaxMidi = strumProbeMaxMidi; StrumProbeCents = strumProbeCents;
            StrumNoiseFloorK = strumNoiseFloorK; StrumMinPeakDb = strumMinPeakDb; StrumCoverMin = strumCoverMin; StrumOwnPartials = strumOwnPartials; StrumOwnMaxMidi = strumOwnMaxMidi;
            StrumPresenceRatio = strumPresenceRatio; StrumPrecisionGamma = strumPrecisionGamma; StrumPrecisionFloor = strumPrecisionFloor; StrumMinMargin = strumMinMargin; StrumFaultMargin = strumFaultMargin;
            StrumBodyMinMidi = strumBodyMinMidi; StrumBodyMaxMidi = strumBodyMaxMidi;
            StrumDistinctPresenceRatio = strumDistinctPresenceRatio; StrumFaultMinPresence = strumFaultMinPresence;
        }

        public static readonly AudioConfig Default = new AudioConfig();
    }
}
