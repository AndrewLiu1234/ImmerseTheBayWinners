using System;
using System.Threading;

namespace UntitledGuitarApp.Runtime.Audio
{
    /// <summary>
    /// Plays a decoded WAV back as if it were a live microphone, WITHOUT a guitar: same <see cref="SampleRate"/> /
    /// <see cref="Read"/> shape as MicCapture (and as Core's future IAudioSource), paced by a clock the CALLER passes in.
    /// Plain C#; it never looks at the wall clock itself, so tests drive it with made-up times and no sleeping.
    ///
    /// How it paces: <see cref="Advance"/>(nowSec) is called with a monotonic time in seconds (the first call anchors
    /// t = 0). Everything up to floor((nowSec - anchor) * SampleRate) samples counts as "recorded so far". <see cref="Read"/>
    /// hands out those samples in order and returns 0 once the reader has caught up with the clock. The file loops: after
    /// its last sample it continues at its first. The source never drops audio; if the reader falls behind it simply finds a
    /// backlog (unlike the mic path, there is no tape to overflow).
    ///
    /// Threads: one thread may call Advance and another may call Read (the counters are published with volatile
    /// reads/writes); using both from one thread is equally fine.
    /// </summary>
    public sealed class WavReplaySource
    {
        readonly float[] _samples;
        readonly int _sampleRate;
        bool _anchored;
        double _anchorSec;
        long _released;    // samples the clock has released so far; written by Advance only
        long _consumed;    // samples handed out so far; written by Read only

        public WavReplaySource(WavData data) : this(RequireData(data).Samples, data.SampleRate) { }

        public WavReplaySource(float[] monoSamples, int sampleRate)
        {
            if (monoSamples == null) throw new ArgumentNullException("monoSamples");
            if (monoSamples.Length == 0) throw new ArgumentException("a replay source needs at least one sample", "monoSamples");
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException("sampleRate", "sample rate must be positive");
            _samples = monoSamples;
            _sampleRate = sampleRate;
        }

        /// <summary>The file's sample rate, reported as it is (no resampling).</summary>
        public int SampleRate { get { return _sampleRate; } }

        /// <summary>Length of one pass through the file, in samples.</summary>
        public int LengthSamples { get { return _samples.Length; } }

        /// <summary>Length of one pass through the file, in seconds.</summary>
        public double DurationSec { get { return _samples.Length / (double)_sampleRate; } }

        /// <summary>Samples released by the clock and not yet read.</summary>
        public long Backlog { get { return Volatile.Read(ref _released) - Volatile.Read(ref _consumed); } }

        /// <summary>Total samples handed out (can exceed <see cref="LengthSamples"/>: the file loops).</summary>
        public long TotalConsumed { get { return Volatile.Read(ref _consumed); } }

        /// <summary>
        /// Moves the replay clock to <paramref name="nowSec"/> (any monotonic seconds). The first call only anchors the start
        /// time. A time earlier than one already seen is ignored.
        /// </summary>
        public void Advance(double nowSec)
        {
            if (double.IsNaN(nowSec) || double.IsInfinity(nowSec)) return;
            if (!_anchored)
            {
                _anchored = true;
                _anchorSec = nowSec;
                return;
            }
            double elapsed = nowSec - _anchorSec;
            if (elapsed <= 0) return;
            long target = (long)Math.Floor(elapsed * _sampleRate);
            if (target > _released) Volatile.Write(ref _released, target);
        }

        /// <summary>
        /// Copies up to <paramref name="count"/> of the samples the clock has released and nobody has read yet (looping the
        /// file as needed) into <paramref name="dst"/>. Returns how many were copied; 0 when the reader is up to date.
        /// </summary>
        public int Read(float[] dst, int offset, int count)
        {
            if (dst == null) throw new ArgumentNullException("dst");
            if (offset < 0 || count < 0 || offset > dst.Length - count) throw new ArgumentOutOfRangeException("count", "offset + count does not fit in dst");
            long consumed = _consumed;
            long avail = Volatile.Read(ref _released) - consumed;
            int n = count <= avail ? count : (int)avail;
            if (n <= 0) return 0;

            int len = _samples.Length;
            int pos = (int)(consumed % len);
            int done = 0;
            while (done < n)
            {
                int chunk = n - done;
                if (chunk > len - pos) chunk = len - pos;
                Array.Copy(_samples, pos, dst, offset + done, chunk);
                done += chunk;
                pos += chunk;
                if (pos == len) pos = 0;
            }
            Volatile.Write(ref _consumed, consumed + n);
            return n;
        }

        static WavData RequireData(WavData data)
        {
            if (data == null) throw new ArgumentNullException("data");
            return data;
        }
    }
}
