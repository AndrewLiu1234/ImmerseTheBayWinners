using System;

namespace UntitledGuitarApp.Runtime.Audio
{
    /// <summary>
    /// The slice of a Unity <c>AudioClip</c> that <see cref="LoopingClipPump"/> uses. MicCapture wraps the real clip;
    /// tests wrap an array, so the wrap-around logic runs without a microphone.
    /// </summary>
    public interface IClipData
    {
        /// <summary>Length of the looping clip in sample frames (<c>AudioClip.samples</c>).</summary>
        int Frames { get; }
        /// <summary>Interleaved channel count (<c>AudioClip.channels</c>).</summary>
        int Channels { get; }
        /// <summary>Sample rate of the clip in Hz (<c>AudioClip.frequency</c>).</summary>
        int SampleRate { get; }
        /// <summary>
        /// Fills <paramref name="data"/> completely, starting at frame <paramref name="offsetFrames"/>
        /// (<c>AudioClip.GetData</c>: the frame count read is <c>data.Length / Channels</c>). Returns false on failure.
        /// </summary>
        bool GetData(float[] data, int offsetFrames);
    }

    /// <summary>
    /// Copies only the NEW samples of a looping recording clip into a <see cref="FloatRingBuffer"/>, without allocating.
    ///
    /// Each <see cref="Pump"/> call gets the clip's current write position (<c>Microphone.GetPosition</c>) and works out how
    /// many frames were recorded since the previous call, handling the position wrapping back to 0 at the end of the loop.
    /// <c>AudioClip.GetData</c> always fills the whole array it is given, so the new frames are read as a few exact
    /// power-of-two blocks (largest first), each from its own preallocated array (a count of 800 is 512 + 256 + 32: three
    /// calls, no allocation, no over-read, no partial block left waiting). A block never crosses the end of the loop, so
    /// the code does not depend on GetData's wrap-around behaviour. Clips with more than one channel are mixed down to mono.
    ///
    /// Catch-up limit: if the caller was away for longer than <c>maxCatchUpFrames</c> (a hitch, a debugger pause), the oldest
    /// frames are skipped and counted in <see cref="SkippedFrames"/> instead of stalling the next frame.
    /// Main thread only; the sink's consumer may read from another thread.
    /// </summary>
    public sealed class LoopingClipPump
    {
        /// <summary>Largest block read in one GetData call is 2^15 = 32768 frames.</summary>
        public const int MaxBlockLog2 = 15;

        float[][] _blocks;         // _blocks[k] holds (1 << k) frames x _blockChannels floats
        int _blockChannels;
        float[] _mono;             // mix-down target, only used for multi-channel clips
        IClipData _clip;
        FloatRingBuffer _sink;
        int _frames;
        int _channels;
        int _readPos;              // next frame to read, 0 .. _frames - 1
        int _maxCatchUp;

        /// <summary>Frames delivered by the most recent <see cref="Pump"/> call.</summary>
        public int LastFrames { get; private set; }
        /// <summary>Sum of squared samples of those frames (for the level meter).</summary>
        public double LastSumSquares { get; private set; }
        /// <summary>Largest absolute sample of those frames.</summary>
        public float LastPeak { get; private set; }
        /// <summary>Total frames skipped because the caller was away for more than the catch-up limit (cumulative over every <see cref="Attach"/>).</summary>
        public long SkippedFrames { get; private set; }
        /// <summary>Total frames delivered (cumulative over every <see cref="Attach"/>).</summary>
        public long TotalFrames { get; private set; }
        /// <summary>Next frame the pump will read (for tests and diagnostics).</summary>
        public int ReadPosition { get { return _readPos; } }
        public bool IsAttached { get { return _clip != null; } }

        /// <summary>
        /// Starts pumping a fresh clip (its write position is 0) into <paramref name="sink"/>. Reuses the block arrays when the
        /// channel count is unchanged, so a watchdog restart does not allocate.
        /// </summary>
        public void Attach(IClipData clip, FloatRingBuffer sink, int maxCatchUpFrames)
        {
            if (clip == null) throw new ArgumentNullException("clip");
            if (sink == null) throw new ArgumentNullException("sink");
            if (clip.Frames <= 0) throw new ArgumentException("the clip has no frames", "clip");
            if (clip.Channels <= 0) throw new ArgumentException("the clip has no channels", "clip");

            if (_blocks == null || _blockChannels != clip.Channels)
            {
                _blocks = new float[MaxBlockLog2 + 1][];
                for (int k = 0; k <= MaxBlockLog2; k++) _blocks[k] = new float[(1 << k) * clip.Channels];
                _blockChannels = clip.Channels;
                _mono = clip.Channels > 1 ? new float[1 << MaxBlockLog2] : null;
            }
            _clip = clip;
            _sink = sink;
            _frames = clip.Frames;
            _channels = clip.Channels;
            _readPos = 0;
            _maxCatchUp = maxCatchUpFrames < 1 ? 1 : maxCatchUpFrames;
            LastFrames = 0; LastSumSquares = 0; LastPeak = 0;
        }

        /// <summary>Stops pumping (the clip is about to be destroyed).</summary>
        public void Detach()
        {
            _clip = null;
            _sink = null;
        }

        /// <summary>
        /// Delivers every frame recorded since the previous call. <paramref name="writePosition"/> is the clip's current
        /// write position in frames. Returns the number of frames delivered (0 when nothing new).
        /// </summary>
        public int Pump(int writePosition)
        {
            LastFrames = 0; LastSumSquares = 0; LastPeak = 0;
            if (_clip == null || writePosition < 0) return 0;
            if (writePosition >= _frames) writePosition %= _frames;

            int delta = writePosition - _readPos;
            if (delta < 0) delta += _frames;                 // the write position wrapped past the end of the loop
            if (delta == 0) return 0;
            if (delta > _maxCatchUp)
            {
                int skip = delta - _maxCatchUp;
                _readPos = (int)(((long)_readPos + skip) % _frames);
                SkippedFrames += skip;
                delta = _maxCatchUp;
            }

            int remaining = delta;
            int delivered = 0;
            double sumSq = 0;
            float peak = 0f;
            while (remaining > 0)
            {
                int untilEnd = _frames - _readPos;           // frames before the loop wraps
                int limit = remaining < untilEnd ? remaining : untilEnd;
                int k = 0;
                while (k < MaxBlockLog2 && (2 << k) <= limit) k++;
                int size = 1 << k;                           // largest power of two <= limit (capped)

                float[] block = _blocks[k];
                if (!_clip.GetData(block, _readPos)) break;

                float[] mono = block;
                if (_channels > 1)
                {
                    mono = _mono;
                    int ch = _channels;
                    float inv = 1f / ch;
                    for (int i = 0, src = 0; i < size; i++)
                    {
                        float s = 0f;
                        for (int c = 0; c < ch; c++) s += block[src++];
                        mono[i] = s * inv;
                    }
                }

                double s2; float p;
                InputLevelMeter.Measure(mono, 0, size, out s2, out p);
                sumSq += s2;
                if (p > peak) peak = p;
                _sink.Write(mono, 0, size);

                _readPos += size;
                if (_readPos >= _frames) _readPos -= _frames;
                remaining -= size;
                delivered += size;
            }

            LastFrames = delivered;
            LastSumSquares = sumSq;
            LastPeak = peak;
            TotalFrames += delivered;
            return delivered;
        }
    }
}
