namespace UntitledGuitarApp.Core.Contracts
{
    /// <summary>
    /// Outcome of checking one string (pluck check). Immutable, so it can cross from the audio worker to the main thread.
    /// Never contains NaN/Inf: "no pitch" is <see cref="DetectedHz"/> = 0 and <see cref="Cents"/> = 0.
    /// </summary>
    public sealed class StringResult
    {
        /// <summary>String number, 1 = high E … 6 = low E (as in <see cref="Theory.ChordShape"/>).</summary>
        public readonly int String;
        /// <summary>MIDI note the player should have produced on this string.</summary>
        public readonly int ExpectedMidi;
        /// <summary>Median f0 after the attack, Hz; 0 when no pitch was found.</summary>
        public readonly double DetectedHz;
        /// <summary>Signed distance of <see cref="DetectedHz"/> from <see cref="ExpectedMidi"/> in cents; 0 when no pitch.</summary>
        public readonly double Cents;
        public readonly Verdict Verdict;
        /// <summary>Stable machine key for the beginner tip (e.g. "arch-finger"), for logs and <c>Practice/Tips</c>; null if none.</summary>
        public readonly string TipKey;

        public StringResult(int stringNumber, int expectedMidi, double detectedHz, double cents, Verdict verdict, string tipKey)
        {
            String = stringNumber; ExpectedMidi = expectedMidi; DetectedHz = detectedHz; Cents = cents; Verdict = verdict; TipKey = tipKey;
        }
    }
}
