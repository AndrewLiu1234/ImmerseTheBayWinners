namespace UntitledGuitarApp.Core.Contracts
{
    /// <summary>
    /// A mono float sample stream. Runtime implements it (<c>Runtime/Audio/MicCapture</c>, <c>WavReplaySource</c>);
    /// core-tests implement it for eval clips. Read on the audio worker thread only.
    /// </summary>
    public interface IAudioSource
    {
        /// <summary>Samples per second (48 000 for the mic, MASTER_PLAN §19). Constant for the life of the source.</summary>
        int SampleRate { get; }

        /// <summary>
        /// Copies up to <paramref name="count"/> new mono samples into <paramref name="dst"/> starting at
        /// <paramref name="offset"/>. Non-blocking: returns how many were copied (0 when nothing is new). Samples are
        /// contiguous across calls; a gap (device restart) must be reported by the owner calling
        /// <see cref="IAudioAnalyzer.Reset"/>. Never allocates.
        /// </summary>
        int Read(float[] dst, int offset, int count);
    }
}
