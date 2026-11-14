using System;
using System.Threading;

namespace UntitledGuitarApp.Runtime.Audio
{
    /// <summary>
    /// Lock-free single-producer / single-consumer ring buffer of mono float samples.
    ///
    /// Threading contract: exactly ONE thread calls <see cref="Write"/> (the producer; MicCapture on the main thread) and
    /// exactly ONE thread calls <see cref="Read"/> (the consumer; the audio worker). No locks, no allocation after the
    /// constructor. Both counters are monotonic 64-bit totals; the producer publishes the samples with a release store of
    /// the write counter and the consumer frees space with a release store of the read counter, so neither side ever sees
    /// half-written data. The plain properties (<see cref="Available"/>, <see cref="Capacity"/>, the totals) may be read
    /// from any thread.
    ///
    /// Capacity is always a power of two (the constructor rounds the requested size UP), so an index is a bit-mask, not a
    /// modulo.
    ///
    /// OVERFLOW POLICY = DROP NEWEST. The producer never blocks and never overwrites samples the consumer has not read yet
    /// (that would race with a consumer that is mid-copy). When the buffer is full, <see cref="Write"/> stores the part of
    /// the block that still fits (its OLDEST samples), discards the rest, adds the discarded count to
    /// <see cref="DroppedSamples"/> and returns how many it stored. The consumer therefore always reads an unbroken run of
    /// the oldest unread samples; the gap, if any, is at the newest end. A consumer that prefers fresh audio over old audio
    /// can drain faster (reading is cheap) or compare <see cref="DroppedSamples"/> before and after a read to learn that a
    /// gap exists.
    /// </summary>
    public sealed class FloatRingBuffer
    {
        /// <summary>Largest capacity the constructor accepts (2^30 samples = 4 GiB of floats, far beyond any use here).</summary>
        public const int MaxCapacity = 1 << 30;

        readonly float[] _data;
        readonly int _mask;
        long _written;   // total samples ever stored: written only by the producer
        long _read;      // total samples ever consumed: written only by the consumer
        long _dropped;   // total samples refused because the buffer was full: written only by the producer

        /// <param name="minCapacity">Wanted capacity in samples; the real capacity is the next power of two at or above it.</param>
        public FloatRingBuffer(int minCapacity)
        {
            if (minCapacity < 1 || minCapacity > MaxCapacity)
                throw new ArgumentOutOfRangeException(nameof(minCapacity), "capacity must be between 1 and " + MaxCapacity);
            int cap = 1;
            while (cap < minCapacity) cap <<= 1;
            _data = new float[cap];
            _mask = cap - 1;
        }

        /// <summary>Real capacity in samples (a power of two, at least the size asked for).</summary>
        public int Capacity { get { return _data.Length; } }

        /// <summary>
        /// Samples stored and not yet read. Exact when called by the consumer (it can only grow under the consumer's feet);
        /// from the producer or any other thread it is a snapshot that may already be stale.
        /// </summary>
        public int Available
        {
            get
            {
                long r = Volatile.Read(ref _read);
                long w = Volatile.Read(ref _written);
                long n = w - r;
                if (n < 0) return 0;
                if (n > _data.Length) return _data.Length;
                return (int)n;
            }
        }

        /// <summary>Capacity minus <see cref="Available"/>: what the producer can store right now without dropping.</summary>
        public int FreeSpace { get { return _data.Length - Available; } }

        /// <summary>Total samples ever stored (monotonic; use it as a sample clock for the newest sample).</summary>
        public long TotalWritten { get { return Volatile.Read(ref _written); } }

        /// <summary>Total samples ever read (monotonic; the consumer's position on the same sample clock).</summary>
        public long TotalRead { get { return Volatile.Read(ref _read); } }

        /// <summary>Total samples discarded by the overflow policy (monotonic). A change means audio was lost.</summary>
        public long DroppedSamples { get { return Volatile.Read(ref _dropped); } }

        /// <summary>
        /// PRODUCER ONLY. Copies <paramref name="count"/> samples from <paramref name="src"/> starting at
        /// <paramref name="offset"/>. Returns how many were stored: <paramref name="count"/>, or less when the buffer filled
        /// up (see the overflow policy in the class summary). Never blocks.
        /// </summary>
        public int Write(float[] src, int offset, int count)
        {
            CheckRange(src, offset, count, "src");
            if (count == 0) return 0;

            long w = _written;                       // the producer owns this counter, so a plain read is exact
            long r = Volatile.Read(ref _read);       // acquire: everything the consumer finished copying is visible
            int free = _data.Length - (int)(w - r);
            int n = count <= free ? count : free;
            if (n < count) Volatile.Write(ref _dropped, _dropped + (count - n));
            if (n <= 0) return 0;

            int start = (int)(w & _mask);
            int firstRun = _data.Length - start;
            if (firstRun > n) firstRun = n;
            Array.Copy(src, offset, _data, start, firstRun);
            if (n > firstRun) Array.Copy(src, offset + firstRun, _data, 0, n - firstRun);
            Volatile.Write(ref _written, w + n);     // release: publishes the samples copied above
            return n;
        }

        /// <summary>
        /// CONSUMER ONLY. Copies up to <paramref name="count"/> of the oldest unread samples into
        /// <paramref name="dst"/> starting at <paramref name="offset"/> and returns how many were copied
        /// (0 when empty; fewer than asked when less is available: a partial read). Never blocks.
        /// </summary>
        public int Read(float[] dst, int offset, int count)
        {
            CheckRange(dst, offset, count, "dst");
            if (count == 0) return 0;

            long r = _read;                          // the consumer owns this counter
            long w = Volatile.Read(ref _written);    // acquire: the samples below w are fully written
            long avail = w - r;
            int n = count <= avail ? count : (int)avail;
            if (n <= 0) return 0;

            int start = (int)(r & _mask);
            int firstRun = _data.Length - start;
            if (firstRun > n) firstRun = n;
            Array.Copy(_data, start, dst, offset, firstRun);
            if (n > firstRun) Array.Copy(_data, 0, dst, offset + firstRun, n - firstRun);
            Volatile.Write(ref _read, r + n);        // release: the producer may now reuse these slots
            return n;
        }

        static void CheckRange(float[] array, int offset, int count, string name)
        {
            if (array == null) throw new ArgumentNullException(name);
            if (offset < 0 || count < 0 || offset > array.Length - count)
                throw new ArgumentOutOfRangeException(name, "offset " + offset + " + count " + count + " does not fit in an array of " + array.Length);
        }
    }
}
