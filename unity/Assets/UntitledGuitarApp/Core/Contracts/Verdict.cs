namespace UntitledGuitarApp.Core.Contracts
{
    /// <summary>
    /// Per-string judgement from the pluck check (and the few bass-string faults the strum check can see), MASTER_PLAN §19.
    /// Tie precedence when two hypotheses fit equally: <see cref="OpenStringInstead"/> &gt; WrongFret* &gt; <see cref="WrongString"/>.
    /// Append new members at the end only (contract is additive after Sat 09:00).
    /// </summary>
    public enum Verdict
    {
        /// <summary>The expected pitch, within the cents tolerance (flag <c>audio.pluckCentsTol</c>).</summary>
        Correct = 0,
        /// <summary>The open string sounded: the finger isn't pressing hard enough or is missing.</summary>
        OpenStringInstead = 1,
        /// <summary>One semitone (or more) flat: finger on a lower fret than the target.</summary>
        WrongFretLow = 2,
        /// <summary>One semitone (or more) sharp: finger on a higher fret than the target.</summary>
        WrongFretHigh = 3,
        /// <summary>A neighbour string's expected note sounded instead.</summary>
        WrongString = 4,
        /// <summary>The expected string gave no pitch above the noise gate (a finger is touching it, or it wasn't played).</summary>
        Muted = 5,
        /// <summary>A string that should not sound did (e.g. open low E under a D chord).</summary>
        ExtraString = 6,
        /// <summary>Pitch found but no hypothesis fits with enough confidence. Never shown as a fault to the player.</summary>
        Unclear = 7,
        /// <summary>Right pitch, but it died before the ring-out time (AudioConfig, 0.8 s): "let string N ring" (SL2).</summary>
        ShortRing = 8,
    }
}
