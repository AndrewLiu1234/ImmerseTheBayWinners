using System;

namespace UntitledGuitarApp.Core.Contracts
{
    /// <summary>
    /// The whole Core audio pipeline behind one call: noise floor, onset detection, YIN tuner, pluck and strum checks
    /// (implemented in <c>Core/Audio</c>). Runtime's <c>AudioWorker</c> thread only moves samples: it reads an
    /// <see cref="IAudioSource"/>, calls <see cref="Process"/>, and enqueues what comes out. Single-threaded: call only
    /// from that one worker thread. No allocations per hop (verdict objects are allocated per onset only).
    /// </summary>
    public interface IAudioAnalyzer
    {
        /// <summary>Sample rate the analyzer was built for; must equal the source's <see cref="IAudioSource.SampleRate"/>.</summary>
        int SampleRate { get; }

        /// <summary>
        /// Forget all buffered audio and state (onset history, ringing strings). <paramref name="startSec"/> is the
        /// session-clock time of the first sample passed to the next <see cref="Process"/> call; event times are
        /// startSec + samplesSinceReset / SampleRate. Call at start and after any capture gap.
        /// </summary>
        void Reset(double startSec);

        /// <summary>
        /// Feed the next <paramref name="count"/> contiguous mono samples (any count; hop framing is internal).
        /// <paramref name="expecting"/> is the latest expectation (never null; use <see cref="AudioExpectation.Idle"/>);
        /// a change takes effect from the next onset. Each event is passed to <paramref name="emit"/> synchronously.
        /// Never throws on silence, clipping or noise.
        /// </summary>
        void Process(float[] samples, int offset, int count, AudioExpectation expecting, Action<AudioEvent> emit);
    }
}
