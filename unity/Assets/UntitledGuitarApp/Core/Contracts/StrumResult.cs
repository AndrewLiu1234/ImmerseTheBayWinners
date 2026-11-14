using System.Collections.Generic;

namespace UntitledGuitarApp.Core.Contracts
{
    /// <summary>
    /// Outcome of the whole-chord strum check (chord level only, MASTER_PLAN §19). The onset time lives on the carrying
    /// <see cref="AudioEvent.TimeSec"/>, not here. Immutable; never contains NaN/Inf.
    /// </summary>
    public sealed class StrumResult
    {
        /// <summary><see cref="Theory.ChordShape.Name"/> of the chord the player should have strummed.</summary>
        public readonly string ExpectedChord;
        /// <summary>Name of the best-matching hypothesis (expected, other song chords, confusers); null if nothing rang.</summary>
        public readonly string BestMatch;
        /// <summary>Hypothesis score of <see cref="BestMatch"/>, 0 … 1 (chroma F-measure blended with the bass-note term;
        /// see Core/Audio/StrumVerifier). A relative score for ranking chords, not a probability.</summary>
        public readonly double Confidence;
        /// <summary>Score(expected) − best score among the other hypotheses; negative when another chord won.</summary>
        public readonly double Margin;
        /// <summary>BestMatch == ExpectedChord and Margin ≥ flag <c>audio.strumMinMargin</c> (0.15).</summary>
        public readonly bool Clean;
        /// <summary>Only the distinctive bass-string faults a strum can reveal (e.g. open A2 instead of C3 in C). Never null; often empty.
        /// Per-string diagnosis comes from the pluck check.</summary>
        public readonly IReadOnlyList<StringResult> Strings;

        public StrumResult(string expectedChord, string bestMatch, double confidence, double margin, bool clean, IReadOnlyList<StringResult> strings)
        {
            ExpectedChord = expectedChord; BestMatch = bestMatch; Confidence = confidence; Margin = margin; Clean = clean; Strings = strings;
        }
    }
}
