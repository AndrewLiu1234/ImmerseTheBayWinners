using System.Collections.Generic;
using UntitledGuitarApp.Core.Theory;

namespace UntitledGuitarApp.Core.Contracts
{
    /// <summary>
    /// Pluck check: is the expected note sounding on one string? Hypothesis testing, not transcription (MASTER_PLAN §19).
    /// Called by Core's <see cref="IAudioAnalyzer"/> and the eval harness; deterministic; silence/noise → Muted/Unclear, never throws.
    /// </summary>
    public interface IPluckVerifier
    {
        /// <param name="window">Mono samples around one onset. The whole array is the window (zero-pad short input);
        /// it includes pre-roll so the verifier can measure the onset-relative spectral rise.</param>
        /// <param name="sampleRate">Hz of <paramref name="window"/>.</param>
        /// <param name="onsetIndex">Index in <paramref name="window"/> of the detected onset.</param>
        /// <param name="stringNumber">1 = high E … 6 = low E (was <c>stringIndex</c> in the sketch).</param>
        /// <param name="expectedMidi">The note that string should produce for the current chord.</param>
        StringResult Verify(float[] window, int sampleRate, int onsetIndex, int stringNumber, int expectedMidi);
    }

    /// <summary>
    /// Strum check: which chord rang, and is it clean? Chord level only (chroma vs expected + confusers), MASTER_PLAN §19.
    /// Budget &lt; 5 ms per onset; deterministic; never throws on silence.
    /// </summary>
    public interface IStrumVerifier
    {
        /// <param name="window">Mono samples around one onset (whole array; analysis uses 30–250 ms after the onset).</param>
        /// <param name="sampleRate">Hz of <paramref name="window"/>.</param>
        /// <param name="onsetIndex">Index in <paramref name="window"/> of the detected onset.</param>
        /// <param name="expected">The chord the player should be strumming.</param>
        /// <param name="confusers">Other hypotheses (other song chords, typical confusions). Never null; may be empty; must not contain <paramref name="expected"/>.</param>
        StrumResult Verify(float[] window, int sampleRate, int onsetIndex, ChordShape expected, IReadOnlyList<ChordShape> confusers);
    }
}
