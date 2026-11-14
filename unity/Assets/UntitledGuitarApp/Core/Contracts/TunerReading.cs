namespace UntitledGuitarApp.Core.Contracts
{
    /// <summary>
    /// One tuner update (YIN, one per 512-sample hop while listening in <see cref="ListenMode.Tune"/>). A struct so the
    /// per-hop path doesn't allocate. "No pitch" = <see cref="HasPitch"/> false and all numbers 0 (never NaN).
    /// </summary>
    public readonly struct TunerReading
    {
        public readonly bool HasPitch;
        /// <summary>Detected f0, Hz (70–1000 Hz range); 0 when no pitch.</summary>
        public readonly double Hz;
        /// <summary>YIN confidence 0 … 1 (1 − aperiodicity).</summary>
        public readonly double Confidence;
        /// <summary>The string being tuned (1 = high E … 6 = low E): the requested one, or the nearest open string in auto mode; 0 when no pitch.</summary>
        public readonly int StringNumber;
        /// <summary>Signed cents from that string's standard-tuning open pitch (+ = sharp). The needle value.</summary>
        public readonly double CentsOff;

        public TunerReading(bool hasPitch, double hz, double confidence, int stringNumber, double centsOff)
        {
            HasPitch = hasPitch; Hz = hz; Confidence = confidence; StringNumber = stringNumber; CentsOff = centsOff;
        }
    }
}
