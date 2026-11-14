using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UntitledGuitarApp.Runtime.Audio;
using UntitledGuitarApp.Runtime.Flags;

namespace UntitledGuitarApp.Tests.EditMode
{
    /// <summary>
    /// Mic plumbing (SL0): ring buffer, WAV reader, WAV replay pacing, the clip pump (wrap-around), the meter, device
    /// choice and the capture session (permission denial, watchdog, flags, replay). No microphone, no sleeping: time is
    /// passed in, the microphone is a fake. MicCapture itself (the MonoBehaviour shell) is not tested here.
    /// </summary>
    [TestFixture]
    public class AudioCaptureTests
    {
        // =============================================================================================================
        // helpers
        // =============================================================================================================

        static float[] Seq(int start, int n)
        {
            float[] a = new float[n];
            for (int i = 0; i < n; i++) a[i] = start + i;
            return a;
        }

        static void AssertSeq(float[] buf, int offset, int n, int start)
        {
            for (int i = 0; i < n; i++)
                if (buf[offset + i] != (float)(start + i))
                    Assert.Fail("sample " + i + ": expected " + (start + i) + " but got " + buf[offset + i]);
        }

        // ---- WAV bytes -----------------------------------------------------------------------------------------

        static byte[] Le16(int v) { return new byte[] { (byte)v, (byte)(v >> 8) }; }
        static byte[] Le32(uint v) { return new byte[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) }; }
        static byte[] Ascii(string s) { byte[] b = new byte[s.Length]; for (int i = 0; i < s.Length; i++) b[i] = (byte)s[i]; return b; }

        static byte[] Cat(params byte[][] parts)
        {
            int n = 0;
            foreach (byte[] p in parts) n += p.Length;
            byte[] r = new byte[n];
            int at = 0;
            foreach (byte[] p in parts) { Buffer.BlockCopy(p, 0, r, at, p.Length); at += p.Length; }
            return r;
        }

        // A chunk with its true size and the pad byte for odd sizes.
        static byte[] Chunk(string id, byte[] body)
        {
            return Cat(Ascii(id), Le32((uint)body.Length), body, (body.Length & 1) == 1 ? new byte[1] : new byte[0]);
        }

        // A chunk whose declared size lies (streaming writers, truncated files).
        static byte[] ChunkDeclared(string id, uint declared, byte[] body)
        {
            return Cat(Ascii(id), Le32(declared), body);
        }

        static byte[] FmtBody(int tag, int channels, uint rate, int bits, int blockAlign = -1)
        {
            int ba = blockAlign >= 0 ? blockAlign : channels * (bits / 8);
            return Cat(Le16(tag), Le16(channels), Le32(rate), Le32(rate * (uint)Math.Max(ba, 0)), Le16(ba), Le16(bits));
        }

        static readonly byte[] StdGuidTail = { 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 };

        static byte[] FmtExtBody(int subTag, int channels, uint rate, int bits, int validBits, byte[] guidTail = null)
        {
            int ba = channels * (bits / 8);
            return Cat(Le16(0xFFFE), Le16(channels), Le32(rate), Le32(rate * (uint)ba), Le16(ba), Le16(bits),
                       Le16(22), Le16(validBits), Le32(3), Le16(subTag), guidTail ?? StdGuidTail);
        }

        static byte[] Riff(params byte[][] chunks)
        {
            byte[] body = Cat(chunks);
            return Cat(Ascii("RIFF"), Le32((uint)(4 + body.Length)), Ascii("WAVE"), body);
        }

        static byte[] Pcm16(params int[] v)
        {
            byte[] b = new byte[v.Length * 2];
            for (int i = 0; i < v.Length; i++) { b[2 * i] = (byte)v[i]; b[2 * i + 1] = (byte)(v[i] >> 8); }
            return b;
        }

        static byte[] Pcm24(params int[] v)
        {
            byte[] b = new byte[v.Length * 3];
            for (int i = 0; i < v.Length; i++) { b[3 * i] = (byte)v[i]; b[3 * i + 1] = (byte)(v[i] >> 8); b[3 * i + 2] = (byte)(v[i] >> 16); }
            return b;
        }

        static byte[] Pcm32(params int[] v)
        {
            byte[] b = new byte[v.Length * 4];
            for (int i = 0; i < v.Length; i++) Buffer.BlockCopy(Le32((uint)v[i]), 0, b, 4 * i, 4);
            return b;
        }

        static byte[] F32(params float[] v)
        {
            byte[] b = new byte[v.Length * 4];
            for (int i = 0; i < v.Length; i++) Buffer.BlockCopy(Le32((uint)BitConverter.SingleToInt32Bits(v[i])), 0, b, 4 * i, 4);
            return b;
        }

        static byte[] SimpleWav(int tag, int channels, uint rate, int bits, byte[] data)
        {
            return Riff(Chunk("fmt ", FmtBody(tag, channels, rate, bits)), Chunk("data", data));
        }

        static WavData ParseOk(byte[] bytes)
        {
            WavData d; string err;
            bool ok = WavReader.TryParse(bytes, out d, out err);
            Assert.IsTrue(ok, "expected the WAV to parse, but: " + err);
            Assert.IsNull(err);
            Assert.IsNotNull(d);
            return d;
        }

        static void AssertRejected(byte[] bytes, string reasonFragment)
        {
            WavData d; string err = null;
            bool ok = true;
            Assert.DoesNotThrow(() => ok = WavReader.TryParse(bytes, out d, out err), "TryParse must never throw");
            Assert.IsFalse(ok, "expected the file to be rejected");
            Assert.IsFalse(string.IsNullOrEmpty(err), "a rejection must carry a reason");
            StringAssert.Contains(reasonFragment.ToLowerInvariant(), err.ToLowerInvariant());
        }

        // =============================================================================================================
        // FloatRingBuffer
        // =============================================================================================================

        [TestCase(1, 1)]
        [TestCase(2, 2)]
        [TestCase(3, 4)]
        [TestCase(1000, 1024)]
        [TestCase(1024, 1024)]
        [TestCase(1025, 2048)]
        [TestCase(96000, 131072)]
        public void Ring_CapacityRoundsUpToPowerOfTwo(int asked, int expected)
        {
            Assert.AreEqual(expected, new FloatRingBuffer(asked).Capacity);
        }

        [Test]
        public void Ring_RejectsImpossibleCapacity()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new FloatRingBuffer(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FloatRingBuffer(-4));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FloatRingBuffer(FloatRingBuffer.MaxCapacity + 1));
        }

        [Test]
        public void Ring_WriteThenRead_RoundTripsInOrder()
        {
            FloatRingBuffer rb = new FloatRingBuffer(16);
            Assert.AreEqual(0, rb.Available);
            Assert.AreEqual(5, rb.Write(Seq(10, 5), 0, 5));
            Assert.AreEqual(5, rb.Available);
            Assert.AreEqual(11, rb.FreeSpace);
            float[] dst = new float[5];
            Assert.AreEqual(5, rb.Read(dst, 0, 5));
            AssertSeq(dst, 0, 5, 10);
            Assert.AreEqual(0, rb.Available);
            Assert.AreEqual(5, rb.TotalWritten);
            Assert.AreEqual(5, rb.TotalRead);
            Assert.AreEqual(0, rb.DroppedSamples);
        }

        [Test]
        public void Ring_PartialReads_ReturnWhatIsThere()
        {
            FloatRingBuffer rb = new FloatRingBuffer(16);
            rb.Write(Seq(0, 10), 0, 10);
            float[] dst = new float[100];
            Assert.AreEqual(4, rb.Read(dst, 0, 4));
            AssertSeq(dst, 0, 4, 0);
            Assert.AreEqual(6, rb.Available);
            Assert.AreEqual(6, rb.Read(dst, 0, 100), "asking for more than is available returns the rest");
            AssertSeq(dst, 0, 6, 4);
            Assert.AreEqual(0, rb.Read(dst, 0, 100), "empty buffer: 0, no blocking");
            Assert.AreEqual(0, rb.Read(dst, 0, 0), "count 0 is a no-op");
        }

        [Test]
        public void Ring_WrapAround_KeepsTheStreamContinuous()
        {
            FloatRingBuffer rb = new FloatRingBuffer(8);
            float[] dst = new float[8];
            Assert.AreEqual(6, rb.Write(Seq(0, 6), 0, 6));
            Assert.AreEqual(6, rb.Read(dst, 0, 6));
            // the next block starts at slot 6 and wraps to slots 0..2
            Assert.AreEqual(5, rb.Write(Seq(100, 5), 0, 5));
            Assert.AreEqual(5, rb.Available);
            Assert.AreEqual(5, rb.Read(dst, 0, 5));
            AssertSeq(dst, 0, 5, 100);
        }

        [Test]
        public void Ring_ManyLaps_WithIrregularBlockSizes_NeverLosesOrReordersSamples()
        {
            FloatRingBuffer rb = new FloatRingBuffer(64);
            Random rng = new Random(7);
            float[] src = new float[40];
            float[] dst = new float[40];
            int written = 0, read = 0;
            for (int step = 0; step < 5000; step++)
            {
                int w = rng.Next(0, 41);
                for (int i = 0; i < w; i++) src[i] = written + i;
                int stored = rb.Write(src, 0, w);
                Assert.AreEqual(Math.Min(w, 64 - (written - read)), stored);
                written += stored;
                // blocks that did not fit are refused whole-tail; the next block continues the sequence of what was stored
                int r = rng.Next(0, 41);
                int got = rb.Read(dst, 0, r);
                for (int i = 0; i < got; i++)
                    if (dst[i] != (float)(read + i)) Assert.Fail("step " + step + ": wrong sample at " + (read + i));
                read += got;
            }
            Assert.AreEqual(written - read, rb.Available);
        }

        [Test]
        public void Ring_Overflow_DropsNewest_KeepsUnreadSamplesIntact()
        {
            FloatRingBuffer rb = new FloatRingBuffer(8);
            Assert.AreEqual(6, rb.Write(Seq(0, 6), 0, 6));
            // only 2 slots are free: the OLDEST two samples of the new block are kept, the other three are refused
            Assert.AreEqual(2, rb.Write(Seq(100, 5), 0, 5));
            Assert.AreEqual(3, rb.DroppedSamples);
            Assert.AreEqual(8, rb.Available);
            float[] dst = new float[8];
            Assert.AreEqual(8, rb.Read(dst, 0, 8));
            Assert.AreEqual(new float[] { 0, 1, 2, 3, 4, 5, 100, 101 }, dst, "unread data was not overwritten and nothing was reordered");
        }

        [Test]
        public void Ring_Full_RefusesEverything_ThenAcceptsAgainAfterARead()
        {
            FloatRingBuffer rb = new FloatRingBuffer(4);
            Assert.AreEqual(4, rb.Write(Seq(0, 4), 0, 4));
            Assert.AreEqual(0, rb.Write(Seq(9, 3), 0, 3));
            Assert.AreEqual(3, rb.DroppedSamples);
            float[] dst = new float[2];
            Assert.AreEqual(2, rb.Read(dst, 0, 2));
            Assert.AreEqual(2, rb.Write(Seq(50, 2), 0, 2));
            Assert.AreEqual(3, rb.DroppedSamples, "no new loss");
            float[] all = new float[4];
            Assert.AreEqual(4, rb.Read(all, 0, 4));
            Assert.AreEqual(new float[] { 2, 3, 50, 51 }, all);
        }

        [Test]
        public void Ring_HonoursSourceAndDestinationOffsets()
        {
            FloatRingBuffer rb = new FloatRingBuffer(8);
            Assert.AreEqual(3, rb.Write(Seq(0, 10), 2, 3));            // samples 2, 3, 4
            float[] dst = { -1, -1, -1, -1, -1 };
            Assert.AreEqual(3, rb.Read(dst, 1, 3));
            Assert.AreEqual(new float[] { -1, 2, 3, 4, -1 }, dst);
        }

        [Test]
        public void Ring_BadArguments_AreProgrammerErrors()
        {
            FloatRingBuffer rb = new FloatRingBuffer(8);
            float[] a = new float[4];
            Assert.Throws<ArgumentNullException>(() => rb.Write(null, 0, 1));
            Assert.Throws<ArgumentNullException>(() => rb.Read(null, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => rb.Write(a, -1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => rb.Write(a, 0, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => rb.Write(a, 3, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => rb.Read(a, 0, 5));
            Assert.AreEqual(0, rb.Available, "failed calls changed nothing");
        }

        [Test]
        public void Ring_OneProducerThread_OneConsumerThread_NoLossNoReorder()
        {
            const int total = 400000;
            FloatRingBuffer rb = new FloatRingBuffer(4096);
            int bad = 0;
            AbortFlag abort = new AbortFlag();
            Thread producer = new Thread(() =>
            {
                Random rng = new Random(99);
                float[] buf = new float[512];
                int next = 0;
                while (next < total && !abort.Value)
                {
                    int chunk = Math.Min(total - next, 1 + rng.Next(512));
                    for (int i = 0; i < chunk; i++) buf[i] = next + i;
                    int off = 0;
                    while (off < chunk && !abort.Value)
                    {
                        int w = rb.Write(buf, off, chunk - off);
                        off += w;
                        if (w == 0) Thread.Yield();
                    }
                    next += chunk;
                }
            });
            producer.IsBackground = true;
            producer.Start();

            float[] dst = new float[300];
            int expected = 0;
            System.Diagnostics.Stopwatch guard = System.Diagnostics.Stopwatch.StartNew();   // a hang guard only, never a sleep
            while (expected < total && guard.Elapsed.TotalSeconds < 30)
            {
                int n = rb.Read(dst, 0, dst.Length);
                if (n == 0) { Thread.Yield(); continue; }
                for (int i = 0; i < n; i++)
                {
                    if (dst[i] != (float)expected) bad++;
                    expected++;
                }
            }
            abort.Value = true;
            producer.Join(5000);
            Assert.AreEqual(total, expected, "the consumer did not see every sample within the hang guard");
            Assert.AreEqual(0, bad, "samples arrived out of order or damaged");
        }

        sealed class AbortFlag
        {
            volatile bool _v;
            public bool Value { get { return _v; } set { _v = value; } }
        }

        // =============================================================================================================
        // WavReader
        // =============================================================================================================

        [Test]
        public void Wav_Pcm16_Mono_DecodesExactly()
        {
            WavData d = ParseOk(SimpleWav(1, 1, 44100, 16, Pcm16(0, 16384, -16384, 32767, -32768)));
            Assert.AreEqual(44100, d.SampleRate);
            Assert.AreEqual(1, d.SourceChannels);
            Assert.AreEqual(16, d.SourceBitsPerSample);
            Assert.IsFalse(d.SourceIsFloat);
            Assert.AreEqual(new float[] { 0f, 0.5f, -0.5f, 32767f / 32768f, -1f }, d.Samples);
            Assert.AreEqual(5 / 44100.0, d.DurationSec, 1e-12);
        }

        [Test]
        public void Wav_Pcm24_Mono_DecodesExactly()
        {
            WavData d = ParseOk(SimpleWav(1, 1, 48000, 24, Pcm24(0, 4194304, -4194304, 8388607, -8388608)));
            Assert.AreEqual(24, d.SourceBitsPerSample);
            Assert.AreEqual(new float[] { 0f, 0.5f, -0.5f, 8388607f / 8388608f, -1f }, d.Samples);
        }

        [Test]
        public void Wav_Pcm32_Mono_DecodesExactly()
        {
            WavData d = ParseOk(SimpleWav(1, 1, 48000, 32, Pcm32(0, 1073741824, -1073741824, int.MaxValue, int.MinValue)));
            Assert.AreEqual(32, d.SourceBitsPerSample);
            Assert.AreEqual(0f, d.Samples[0]);
            Assert.AreEqual(0.5f, d.Samples[1]);
            Assert.AreEqual(-0.5f, d.Samples[2]);
            Assert.AreEqual(1f, d.Samples[3], 1e-6f);
            Assert.AreEqual(-1f, d.Samples[4]);
        }

        [Test]
        public void Wav_Float32_Mono_KeepsValues_AndZeroesNaNAndInfinity()
        {
            WavData d = ParseOk(SimpleWav(3, 1, 48000, 32, F32(0.25f, -0.75f, 1.5f, float.NaN, float.PositiveInfinity, float.NegativeInfinity)));
            Assert.IsTrue(d.SourceIsFloat);
            Assert.AreEqual(new float[] { 0.25f, -0.75f, 1.5f, 0f, 0f, 0f }, d.Samples);
        }

        [Test]
        public void Wav_Stereo_Pcm16_MixesDownToTheAverage()
        {
            // frames: (L, R)
            WavData d = ParseOk(SimpleWav(1, 2, 44100, 16, Pcm16(16384, 0, 16384, 16384, -32768, 0, 32767, -32768)));
            Assert.AreEqual(2, d.SourceChannels);
            Assert.AreEqual(4, d.Samples.Length);
            Assert.AreEqual(0.25f, d.Samples[0], 1e-7f);
            Assert.AreEqual(0.5f, d.Samples[1], 1e-7f);
            Assert.AreEqual(-0.5f, d.Samples[2], 1e-7f);
            Assert.AreEqual((32767f / 32768f - 1f) * 0.5f, d.Samples[3], 1e-7f);
        }

        [Test]
        public void Wav_Stereo_Float32_MixesDownToTheAverage()
        {
            WavData d = ParseOk(SimpleWav(3, 2, 22050, 32, F32(1f, 0f, 0.5f, -0.5f, -1f, -1f)));
            Assert.AreEqual(new float[] { 0.5f, 0f, -1f }, d.Samples);
            Assert.AreEqual(22050, d.SampleRate);
        }

        [TestCase(8000u)]
        [TestCase(11025u)]
        [TestCase(22050u)]
        [TestCase(44100u)]
        [TestCase(48000u)]
        [TestCase(96000u)]
        [TestCase(12345u)]
        public void Wav_AnySampleRate_IsReportedNotResampled(uint rate)
        {
            WavData d = ParseOk(SimpleWav(1, 1, rate, 16, Pcm16(1, 2, 3, 4)));
            Assert.AreEqual((int)rate, d.SampleRate);
            Assert.AreEqual(4, d.Samples.Length);
        }

        [Test]
        public void Wav_Extensible_Pcm24_IsAccepted()
        {
            byte[] wav = Riff(Chunk("fmt ", FmtExtBody(1, 2, 48000, 24, 24)), Chunk("data", Pcm24(4194304, 0, -4194304, -4194304)));
            WavData d = ParseOk(wav);
            Assert.AreEqual(new float[] { 0.25f, -0.5f }, d.Samples);
            Assert.AreEqual(24, d.SourceBitsPerSample);
            Assert.IsFalse(d.SourceIsFloat);
        }

        [Test]
        public void Wav_Extensible_Float32_IsAccepted()
        {
            byte[] wav = Riff(Chunk("fmt ", FmtExtBody(3, 1, 48000, 32, 32)), Chunk("data", F32(0.5f, -0.25f)));
            WavData d = ParseOk(wav);
            Assert.IsTrue(d.SourceIsFloat);
            Assert.AreEqual(new float[] { 0.5f, -0.25f }, d.Samples);
        }

        [Test]
        public void Wav_ExtraChunks_AreSkipped_AndOddSizesUseThePadByte()
        {
            byte[] wav = Riff(
                Chunk("LIST", new byte[] { 1, 2, 3, 4, 5 }),               // odd size 5 + 1 pad byte, before fmt
                Chunk("fmt ", FmtBody(1, 1, 44100, 16)),
                Chunk("fact", new byte[] { 9, 9, 9, 9 }),
                Chunk("data", Pcm16(100, 200, 300)),
                Chunk("id3 ", new byte[] { 7 }));                          // after the data
            WavData d = ParseOk(wav);
            Assert.AreEqual(3, d.Samples.Length);
            Assert.AreEqual(300 / 32768f, d.Samples[2]);
        }

        [Test]
        public void Wav_DataChunkBeforeFmt_IsAccepted()
        {
            byte[] wav = Riff(Chunk("data", Pcm16(1000, -1000)), Chunk("fmt ", FmtBody(1, 1, 16000, 16)));
            WavData d = ParseOk(wav);
            Assert.AreEqual(16000, d.SampleRate);
            Assert.AreEqual(2, d.Samples.Length);
        }

        [Test]
        public void Wav_StreamingDataSize_MeansUntilEndOfFile()
        {
            byte[] wav = Riff(Chunk("fmt ", FmtBody(1, 1, 44100, 16)), ChunkDeclared("data", 0xFFFFFFFFu, Pcm16(1, 2, 3, 4, 5, 6)));
            Assert.AreEqual(6, ParseOk(wav).Samples.Length);
        }

        [Test]
        public void Wav_TrailingPartialFrame_IsIgnored()
        {
            // stereo 16-bit = 4 bytes per frame; 6 bytes = 1 frame + 2 stray bytes
            byte[] wav = Riff(Chunk("fmt ", FmtBody(1, 2, 44100, 16)), Chunk("data", Cat(Pcm16(16384, 16384), new byte[] { 5, 6 })));
            WavData d = ParseOk(wav);
            Assert.AreEqual(1, d.Samples.Length);
            Assert.AreEqual(0.5f, d.Samples[0]);
        }

        [Test]
        public void Wav_Malformed_NullAndTooShort()
        {
            AssertRejected(null, "no data");
            AssertRejected(new byte[0], "too short");
            AssertRejected(new byte[11], "too short");
        }

        [Test]
        public void Wav_Malformed_WrongMagic()
        {
            byte[] notRiff = SimpleWav(1, 1, 44100, 16, Pcm16(1, 2));
            notRiff[0] = (byte)'X';
            AssertRejected(notRiff, "not a RIFF file");

            byte[] rf64 = SimpleWav(1, 1, 44100, 16, Pcm16(1, 2));
            rf64[0] = (byte)'R'; rf64[1] = (byte)'F'; rf64[2] = (byte)'6'; rf64[3] = (byte)'4';
            AssertRejected(rf64, "RF64");

            byte[] notWave = SimpleWav(1, 1, 44100, 16, Pcm16(1, 2));
            notWave[8] = (byte)'A'; notWave[9] = (byte)'V'; notWave[10] = (byte)'I'; notWave[11] = (byte)' ';
            AssertRejected(notWave, "not a WAVE file");

            byte[] garbage = new byte[200];
            for (int i = 0; i < garbage.Length; i++) garbage[i] = 0x55;
            AssertRejected(garbage, "not a RIFF file");
        }

        [Test]
        public void Wav_Malformed_MissingOrBrokenChunks()
        {
            AssertRejected(Riff(Chunk("data", Pcm16(1, 2))), "missing 'fmt '");
            AssertRejected(Riff(Chunk("fmt ", FmtBody(1, 1, 44100, 16))), "missing 'data'");
            AssertRejected(Riff(Chunk("fmt ", new byte[10]), Chunk("data", Pcm16(1, 2))), "at least 16");
            AssertRejected(Riff(ChunkDeclared("fmt ", 5000, new byte[16])), "truncated");
            AssertRejected(Riff(Chunk("fmt ", FmtBody(1, 1, 44100, 16)), ChunkDeclared("data", 100, Pcm16(1, 2, 3, 4, 5))), "truncated");
            AssertRejected(Riff(Chunk("fmt ", FmtBody(1, 1, 44100, 16)), Chunk("data", new byte[0])), "empty");
            AssertRejected(Riff(Chunk("fmt ", FmtBody(1, 2, 44100, 16)), Chunk("data", new byte[] { 1, 2 })), "less than one sample frame");
        }

        [Test]
        public void Wav_Malformed_UnsupportedOrInconsistentFormat()
        {
            AssertRejected(SimpleWav(2, 1, 44100, 4, Pcm16(1, 2)), "unsupported wav format tag 2");
            AssertRejected(SimpleWav(1, 1, 44100, 8, new byte[] { 1, 2 }), "unsupported pcm sample size 8");
            AssertRejected(SimpleWav(3, 1, 44100, 64, new byte[16]), "unsupported float sample size 64");
            AssertRejected(SimpleWav(3, 1, 44100, 16, Pcm16(1, 2)), "unsupported float sample size 16");
            AssertRejected(SimpleWav(1, 0, 44100, 16, Pcm16(1, 2)), "channel count is 0");
            AssertRejected(SimpleWav(1, 3, 44100, 16, Pcm16(1, 2, 3)), "unsupported channel count 3");
            AssertRejected(SimpleWav(1, 1, 0, 16, Pcm16(1, 2)), "sample rate is 0");
            AssertRejected(Riff(Chunk("fmt ", FmtBody(1, 2, 44100, 16, 3)), Chunk("data", Pcm16(1, 2, 3, 4))), "block align");
        }

        [Test]
        public void Wav_Malformed_ExtensibleProblems()
        {
            byte[] badGuid = new byte[14];
            AssertRejected(Riff(Chunk("fmt ", FmtExtBody(1, 1, 48000, 16, 16, badGuid)), Chunk("data", Pcm16(1, 2))), "sub-format guid");
            byte[] shortExt = Cat(FmtBody(0xFFFE, 1, 48000, 16), new byte[2]);          // 18 bytes: no extension block
            AssertRejected(Riff(Chunk("fmt ", shortExt), Chunk("data", Pcm16(1, 2))), "at least 40");
        }

        [Test]
        public void Wav_EveryTruncation_IsRejected_WithoutAnException()
        {
            byte[] good = SimpleWav(1, 2, 44100, 16, Pcm16(1, 2, 3, 4, 5, 6, 7, 8));
            ParseOk(good);
            for (int len = 0; len < good.Length; len++)
            {
                byte[] cut = new byte[len];
                Buffer.BlockCopy(good, 0, cut, 0, len);
                WavData d; string err = null;
                bool ok = true;
                Assert.DoesNotThrow(() => ok = WavReader.TryParse(cut, out d, out err), "length " + len);
                Assert.IsFalse(ok, "a file cut to " + len + " bytes must be rejected");
                Assert.IsFalse(string.IsNullOrEmpty(err), "length " + len + " needs a reason");
            }
        }

        [Test]
        public void Wav_RandomByteDamage_NeverThrows()
        {
            byte[] good = SimpleWav(1, 2, 44100, 16, Pcm16(1, 2, 3, 4, 5, 6, 7, 8));
            Random rng = new Random(2024);
            for (int trial = 0; trial < 2000; trial++)
            {
                byte[] b = (byte[])good.Clone();
                int hits = 1 + rng.Next(4);
                for (int h = 0; h < hits; h++) b[rng.Next(b.Length)] = (byte)rng.Next(256);
                WavData d; string err;
                bool ok = false;
                Assert.DoesNotThrow(() => ok = WavReader.TryParse(b, out d, out err), "trial " + trial);
                if (ok) { WavData good2 = ParseOk(b); Assert.Greater(good2.Samples.Length, 0); Assert.Greater(good2.SampleRate, 0); }
            }
        }

        [Test]
        public void Wav_TryReadFile_MissingEmptyAndRealFiles()
        {
            WavData d; string err;
            Assert.IsFalse(WavReader.TryReadFile(null, out d, out err));
            Assert.IsFalse(string.IsNullOrEmpty(err));
            Assert.IsFalse(WavReader.TryReadFile(Path.Combine(Path.GetTempPath(), "uga-no-such-file-" + Guid.NewGuid().ToString("N") + ".wav"), out d, out err));
            StringAssert.Contains("not found", err);
            Assert.IsNull(d);

            string path = Path.Combine(Path.GetTempPath(), "uga-test-" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                File.WriteAllBytes(path, SimpleWav(1, 1, 22050, 16, Pcm16(16384, -16384)));
                Assert.IsTrue(WavReader.TryReadFile(path, out d, out err), err);
                Assert.AreEqual(22050, d.SampleRate);
                Assert.AreEqual(new float[] { 0.5f, -0.5f }, d.Samples);

                File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
                Assert.IsFalse(WavReader.TryReadFile(path, out d, out err));
                StringAssert.Contains("too short", err);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // =============================================================================================================
        // WavReplaySource (rate 8 or 1024 Hz and times that are exact in binary, so the counts are exact)
        // =============================================================================================================

        [Test]
        public void Replay_ReportsTheFilesRate_AndLength()
        {
            WavReplaySource r = new WavReplaySource(new float[] { 1, 2, 3, 4 }, 22050);
            Assert.AreEqual(22050, r.SampleRate);
            Assert.AreEqual(4, r.LengthSamples);
            Assert.AreEqual(4 / 22050.0, r.DurationSec, 1e-12);
        }

        [Test]
        public void Replay_IsPacedByThePassedInTime()
        {
            WavReplaySource r = new WavReplaySource(Seq(0, 4096), 1024);
            float[] dst = new float[2000];
            r.Advance(10.0);                                       // anchors t = 0, releases nothing
            Assert.AreEqual(0, r.Read(dst, 0, 2000));
            r.Advance(10.25);                                      // 0.25 s * 1024 = 256 samples
            Assert.AreEqual(256, r.Backlog);
            Assert.AreEqual(100, r.Read(dst, 0, 100));
            AssertSeq(dst, 0, 100, 0);
            Assert.AreEqual(156, r.Read(dst, 0, 2000), "partial read: only what the clock has released");
            AssertSeq(dst, 0, 156, 100);
            Assert.AreEqual(0, r.Read(dst, 0, 2000), "caught up with the clock");
            r.Advance(10.25);                                      // same time again: nothing new
            Assert.AreEqual(0, r.Read(dst, 0, 2000));
            r.Advance(11.0);                                       // 1 s since the anchor = 1024 samples in total
            Assert.AreEqual(1024 - 256, r.Read(dst, 0, 2000));
            Assert.AreEqual(1024, r.TotalConsumed);
        }

        [Test]
        public void Replay_LoopsSeamlessly_AcrossReadBoundaries()
        {
            WavReplaySource r = new WavReplaySource(new float[] { 1, 2, 3, 4, 5 }, 8);
            r.Advance(0.0);
            r.Advance(1.5);                                        // 12 samples
            float[] all = new float[12];
            int at = 0;
            int[] chunks = { 3, 4, 5, 9 };                         // the last one asks for more than is left
            foreach (int c in chunks)
            {
                float[] tmp = new float[c];
                int got = r.Read(tmp, 0, c);
                Array.Copy(tmp, 0, all, at, got);
                at += got;
            }
            Assert.AreEqual(12, at);
            Assert.AreEqual(new float[] { 1, 2, 3, 4, 5, 1, 2, 3, 4, 5, 1, 2 }, all);
        }

        [Test]
        public void Replay_ReadingMoreThanOneFileLengthAtOnce_StillLoops()
        {
            WavReplaySource r = new WavReplaySource(new float[] { 7, 8, 9 }, 8);
            r.Advance(0.0);
            r.Advance(2.0);                                        // 16 samples released
            float[] dst = new float[16];
            Assert.AreEqual(16, r.Read(dst, 0, 16));
            for (int i = 0; i < 16; i++) Assert.AreEqual(7 + i % 3, dst[i]);
        }

        [Test]
        public void Replay_TimeNeverRunsBackwards_AndBadTimesAreIgnored()
        {
            WavReplaySource r = new WavReplaySource(Seq(0, 100), 8);
            r.Advance(5.0);
            r.Advance(6.0);
            Assert.AreEqual(8, r.Backlog);
            r.Advance(5.5);                                        // earlier than before: ignored
            r.Advance(double.NaN);
            r.Advance(double.PositiveInfinity);
            Assert.AreEqual(8, r.Backlog);
            r.Advance(6.5);
            Assert.AreEqual(12, r.Backlog);
        }

        [Test]
        public void Replay_FallingBehindBuildsABacklog_NothingIsDropped()
        {
            WavReplaySource r = new WavReplaySource(Seq(0, 10), 8);
            r.Advance(0.0);
            r.Advance(1000.0);                                     // a long hitch
            Assert.AreEqual(8000, r.Backlog);
            float[] dst = new float[256];
            long total = 0;
            int n;
            while ((n = r.Read(dst, 0, dst.Length)) > 0) total += n;
            Assert.AreEqual(8000, total);
            Assert.AreEqual(0, r.Backlog);
        }

        [Test]
        public void Replay_ReadHonoursTheDestinationOffset()
        {
            WavReplaySource r = new WavReplaySource(new float[] { 1, 2, 3 }, 8);
            r.Advance(0.0);
            r.Advance(0.25);                                       // 2 samples
            float[] dst = { -1, -1, -1, -1 };
            Assert.AreEqual(2, r.Read(dst, 1, 3));
            Assert.AreEqual(new float[] { -1, 1, 2, -1 }, dst);
        }

        [Test]
        public void Replay_ConstructorRejectsUnusableInput()
        {
            Assert.Throws<ArgumentNullException>(() => new WavReplaySource((float[])null, 8));
            Assert.Throws<ArgumentException>(() => new WavReplaySource(new float[0], 8));
            Assert.Throws<ArgumentOutOfRangeException>(() => new WavReplaySource(new float[] { 1 }, 0));
            Assert.Throws<ArgumentNullException>(() => new WavReplaySource((WavData)null));
        }

        [Test]
        public void Replay_FromAParsedStereoWav_PlaysTheMonoMix()
        {
            WavData d = ParseOk(SimpleWav(1, 2, 8, 16, Pcm16(16384, 0, 0, 16384, -32768, -32768)));
            WavReplaySource r = new WavReplaySource(d);
            Assert.AreEqual(8, r.SampleRate);
            r.Advance(0.0);
            r.Advance(0.5);                                        // 4 samples: 3 from the file, then it loops to the first again
            float[] dst = new float[4];
            Assert.AreEqual(4, r.Read(dst, 0, 4));
            Assert.AreEqual(new float[] { 0.25f, 0.25f, -1f, 0.25f }, dst);
        }

        // =============================================================================================================
        // LoopingClipPump (the wrap-around logic behind MicCapture), driven by a fake looping clip
        // =============================================================================================================

        sealed class FakeClip : IClipData
        {
            readonly float[] _tape;
            readonly int _channels, _frames, _rate;
            public int WriteHead;            // like Microphone.GetPosition: next frame to be recorded, 0 .. frames - 1
            public long Recorded;            // total frames recorded; frame f carries the value f (+ 0.5 on channel 1)
            public bool FailGetData;
            public bool CrossedEnd;          // set if the pump ever asked for a block that runs past the end of the loop
            public int GetDataCalls;
            public readonly HashSet<float[]> Buffers = new HashSet<float[]>();

            public FakeClip(int frames, int channels, int rate)
            {
                _frames = frames; _channels = channels; _rate = rate;
                _tape = new float[frames * channels];
            }

            public int Frames { get { return _frames; } }
            public int Channels { get { return _channels; } }
            public int SampleRate { get { return _rate; } }

            public void Record(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    for (int c = 0; c < _channels; c++) _tape[WriteHead * _channels + c] = Recorded + c * 0.5f;
                    Advance();
                }
            }

            public void RecordValue(int n, float v)
            {
                for (int i = 0; i < n; i++)
                {
                    for (int c = 0; c < _channels; c++) _tape[WriteHead * _channels + c] = v;
                    Advance();
                }
            }

            void Advance() { WriteHead++; if (WriteHead == _frames) WriteHead = 0; Recorded++; }

            public bool GetData(float[] data, int offsetFrames)
            {
                GetDataCalls++;
                Buffers.Add(data);
                if (FailGetData) return false;
                Assert.GreaterOrEqual(offsetFrames, 0);
                Assert.Less(offsetFrames, _frames);
                int count = data.Length / _channels;
                if (offsetFrames + count > _frames) CrossedEnd = true;
                for (int i = 0; i < count; i++)
                {
                    int f = (offsetFrames + i) % _frames;           // Unity documents the wrap-around; the pump must not need it
                    for (int c = 0; c < _channels; c++) data[i * _channels + c] = _tape[f * _channels + c];
                }
                return true;
            }
        }

        [Test]
        public void Pump_DeliversEveryNewFrameExactlyOnce_AcrossManyLoopLaps()
        {
            FakeClip clip = new FakeClip(1000, 1, 48000);
            FloatRingBuffer ring = new FloatRingBuffer(1 << 16);
            LoopingClipPump pump = new LoopingClipPump();
            pump.Attach(clip, ring, 1000000);
            Random rng = new Random(3);
            float[] dst = new float[1 << 16];
            long expected = 0;
            for (int i = 0; i < 400; i++)
            {
                int n = 1 + rng.Next(700);                                // never a whole lap between two pumps
                clip.Record(n);
                int got = pump.Pump(clip.WriteHead);
                Assert.AreEqual(n, got, "poll " + i);
                Assert.AreEqual(n, pump.LastFrames);
                int read = ring.Read(dst, 0, dst.Length);
                Assert.AreEqual(n, read);
                for (int k = 0; k < read; k++)
                    if (dst[k] != (float)(expected + k)) Assert.Fail("poll " + i + ": wrong frame " + (expected + k) + ", got " + dst[k]);
                expected += read;
            }
            Assert.Greater(clip.Recorded, 100000, "the test must go round the 1000-frame loop many times");
            Assert.AreEqual(clip.Recorded, pump.TotalFrames);
            Assert.IsFalse(clip.CrossedEnd, "no block may run past the end of the loop");
            Assert.AreEqual(0, ring.DroppedSamples);
        }

        [Test]
        public void Pump_NothingNew_DeliversNothing()
        {
            FakeClip clip = new FakeClip(500, 1, 48000);
            LoopingClipPump pump = new LoopingClipPump();
            pump.Attach(clip, new FloatRingBuffer(1024), 500);
            Assert.AreEqual(0, pump.Pump(0), "position 0 right after Start means no data yet");
            clip.Record(100);
            Assert.AreEqual(100, pump.Pump(100));
            Assert.AreEqual(0, pump.Pump(100), "same position again");
            Assert.AreEqual(0, pump.Pump(-1), "a negative position is ignored");
            Assert.AreEqual(0, pump.LastFrames);
        }

        [Test]
        public void Pump_UsesOnlyPreallocatedBlocks()
        {
            FakeClip clip = new FakeClip(48000, 1, 48000);
            LoopingClipPump pump = new LoopingClipPump();
            pump.Attach(clip, new FloatRingBuffer(1 << 17), 48000);
            Random rng = new Random(11);
            for (int i = 0; i < 2000; i++)
            {
                clip.Record(1 + rng.Next(3000));
                pump.Pump(clip.WriteHead);
            }
            Assert.LessOrEqual(clip.Buffers.Count, LoopingClipPump.MaxBlockLog2 + 1, "one array per power-of-two size, reused for ever");
            Assert.IsFalse(clip.CrossedEnd);
        }

        [Test]
        public void Pump_Stereo_IsMixedDownToMono()
        {
            FakeClip clip = new FakeClip(700, 2, 48000);                  // channel 0 = f, channel 1 = f + 0.5
            FloatRingBuffer ring = new FloatRingBuffer(4096);
            LoopingClipPump pump = new LoopingClipPump();
            pump.Attach(clip, ring, 5000);
            clip.Record(500);
            Assert.AreEqual(500, pump.Pump(clip.WriteHead));
            clip.Record(400);                                              // wraps the 700-frame loop
            Assert.AreEqual(400, pump.Pump(clip.WriteHead));
            float[] dst = new float[900];
            Assert.AreEqual(900, ring.Read(dst, 0, 900));
            for (int i = 0; i < 900; i++) Assert.AreEqual(i + 0.25f, dst[i], 1e-4f);
            Assert.IsFalse(clip.CrossedEnd);
        }

        [Test]
        public void Pump_AfterALongAbsence_SkipsTheOldestAndCountsIt()
        {
            FakeClip clip = new FakeClip(1000, 1, 48000);
            FloatRingBuffer ring = new FloatRingBuffer(1024);
            LoopingClipPump pump = new LoopingClipPump();
            pump.Attach(clip, ring, 100);                                  // catch-up limit: 100 frames
            clip.Record(450);
            Assert.AreEqual(100, pump.Pump(clip.WriteHead));
            Assert.AreEqual(350, pump.SkippedFrames);
            float[] dst = new float[100];
            Assert.AreEqual(100, ring.Read(dst, 0, 100));
            AssertSeq(dst, 0, 100, 350);                                   // the NEWEST 100 frames
        }

        [Test]
        public void Pump_WhenGetDataFails_NothingIsLost_TheNextPumpRetries()
        {
            FakeClip clip = new FakeClip(1000, 1, 48000);
            FloatRingBuffer ring = new FloatRingBuffer(1024);
            LoopingClipPump pump = new LoopingClipPump();
            pump.Attach(clip, ring, 1000);
            clip.Record(300);
            clip.FailGetData = true;
            Assert.AreEqual(0, pump.Pump(clip.WriteHead));
            Assert.AreEqual(0, ring.Available);
            Assert.AreEqual(0, pump.ReadPosition, "the read position did not move");
            clip.FailGetData = false;
            Assert.AreEqual(300, pump.Pump(clip.WriteHead));
            float[] dst = new float[300];
            Assert.AreEqual(300, ring.Read(dst, 0, 300));
            AssertSeq(dst, 0, 300, 0);
        }

        [Test]
        public void Pump_ReportsLevelStatsOfWhatItDelivered()
        {
            FakeClip clip = new FakeClip(1000, 1, 48000);
            LoopingClipPump pump = new LoopingClipPump();
            pump.Attach(clip, new FloatRingBuffer(2048), 1000);
            clip.RecordValue(300, 0.5f);
            Assert.AreEqual(300, pump.Pump(clip.WriteHead));
            Assert.AreEqual(300 * 0.25, pump.LastSumSquares, 1e-6);
            Assert.AreEqual(0.5f, pump.LastPeak);
            clip.RecordValue(100, 0f);
            pump.Pump(clip.WriteHead);
            Assert.AreEqual(0.0, pump.LastSumSquares);
            Assert.AreEqual(0f, pump.LastPeak);
        }

        [Test]
        public void Pump_ReattachingStartsAFreshClip_AndKeepsTheBlockArrays()
        {
            FakeClip first = new FakeClip(800, 1, 48000);
            FloatRingBuffer ring = new FloatRingBuffer(4096);
            LoopingClipPump pump = new LoopingClipPump();
            pump.Attach(first, ring, 800);
            first.Record(300);
            pump.Pump(first.WriteHead);
            FakeClip second = new FakeClip(800, 1, 48000);
            pump.Attach(second, ring, 800);
            Assert.AreEqual(0, pump.ReadPosition);
            second.Record(300);
            Assert.AreEqual(300, pump.Pump(second.WriteHead));
            Assert.IsTrue(first.Buffers.SetEquals(second.Buffers), "the same block arrays are reused after a restart");
        }

        [Test]
        public void Pump_RejectsUnusableClips()
        {
            LoopingClipPump pump = new LoopingClipPump();
            FloatRingBuffer ring = new FloatRingBuffer(16);
            Assert.Throws<ArgumentNullException>(() => pump.Attach(null, ring, 10));
            Assert.Throws<ArgumentNullException>(() => pump.Attach(new FakeClip(10, 1, 48000), null, 10));
            Assert.Throws<ArgumentException>(() => pump.Attach(new FakeClip(0, 1, 48000), ring, 10));
            Assert.AreEqual(0, pump.Pump(5), "not attached: nothing happens");
        }

        // =============================================================================================================
        // LevelMeter
        // =============================================================================================================

        [Test]
        public void Meter_Measure_GivesSumOfSquaresAndPeak()
        {
            double sq; float peak;
            LevelMeter.Measure(new float[] { 0.5f, -1f, 0.25f, 0f }, 0, 4, out sq, out peak);
            Assert.AreEqual(0.25 + 1.0 + 0.0625, sq, 1e-9);
            Assert.AreEqual(1f, peak);
            LevelMeter.Measure(new float[] { 9f, 0.5f, -0.5f }, 1, 2, out sq, out peak);
            Assert.AreEqual(0.5, sq, 1e-9);
            Assert.AreEqual(0.5f, peak);
        }

        [Test]
        public void Meter_RisesToTheSignalLevel_AndPeakJumpsAtOnce()
        {
            LevelMeter m = new LevelMeter();
            m.Add(960 * 0.25, 0.5f, 960, 48000);                           // 20 ms of a 0.5 square wave
            Assert.Greater(m.Rms, 0.1f);
            Assert.Less(m.Rms, 0.5f, "RMS is smoothed, it does not jump");
            Assert.AreEqual(0.5f, m.Peak, "peak jumps up at once");
            for (int i = 0; i < 40; i++) m.Add(960 * 0.25, 0.5f, 960, 48000);
            Assert.AreEqual(0.5f, m.Rms, 0.001f);
            Assert.AreEqual(0.5f, m.Peak, 1e-6f);
        }

        [Test]
        public void Meter_Falls_WhenNoSamplesArrive_AndSettlesAtZero()
        {
            LevelMeter m = new LevelMeter();
            for (int i = 0; i < 50; i++) m.Add(960 * 0.25, 0.5f, 960, 48000);
            for (int i = 0; i < 10; i++) m.Idle(0.1f);                     // one second of "deaf"
            Assert.AreEqual(0.5 * Math.Exp(-1.0 / LevelMeter.RmsReleaseSec), m.Rms, 1e-3);
            Assert.AreEqual(0.5 * Math.Exp(-1.0 / LevelMeter.PeakReleaseSec), m.Peak, 1e-3);
            for (int i = 0; i < 100; i++) m.Idle(0.1f);
            Assert.AreEqual(0f, m.Rms);
            Assert.AreEqual(0f, m.Peak);
        }

        [Test]
        public void Meter_QuietBlocksPullTheLevelDown_AndBadInputIsHarmless()
        {
            LevelMeter m = new LevelMeter();
            for (int i = 0; i < 50; i++) m.Add(960 * 0.25, 0.5f, 960, 48000);
            float loud = m.Rms;
            m.Add(0.0, 0f, 960, 48000);
            Assert.Less(m.Rms, loud);
            m.Add(double.NaN, float.NaN, 960, 48000);
            Assert.IsFalse(float.IsNaN(m.Rms));
            Assert.IsFalse(float.IsNaN(m.Peak));
            float before = m.Rms;
            m.Add(1.0, 1f, 0, 48000);                                      // zero frames: ignored
            m.Add(1.0, 1f, 960, 0);                                        // no rate: ignored
            Assert.AreEqual(before, m.Rms);
            m.Reset();
            Assert.AreEqual(0f, m.Rms);
            Assert.AreEqual(0f, m.Peak);
        }

        // =============================================================================================================
        // MicDevicePicker
        // =============================================================================================================

        [Test]
        public void Picker_MatchesTheFlagCaseInsensitivelyAsASubstring_FirstMatchWins()
        {
            string[] devices = { "Microsoft Teams Audio", "MacBook Pro Microphone", "XREAL Air 2 Ultra", "xreal Air Mic" };
            Assert.AreEqual(1, MicDevicePicker.Pick(devices, "MacBook"));
            Assert.AreEqual(1, MicDevicePicker.Pick(devices, "macbook pro"));
            Assert.AreEqual(2, MicDevicePicker.Pick(devices, "XREAL"), "first match wins");
            Assert.AreEqual(3, MicDevicePicker.Pick(devices, "air mic"));
            Assert.AreEqual(2, MicDevicePicker.Pick(devices, "  ultra  "), "surrounding blanks are trimmed");
        }

        [Test]
        public void Picker_NoMatchBlankOrNoDevices_MeansDefaultDevice()
        {
            string[] devices = { "MacBook Pro Microphone" };
            Assert.AreEqual(-1, MicDevicePicker.Pick(devices, "XREAL"));
            Assert.AreEqual(-1, MicDevicePicker.Pick(devices, ""));
            Assert.AreEqual(-1, MicDevicePicker.Pick(devices, "   "));
            Assert.AreEqual(-1, MicDevicePicker.Pick(devices, null));
            Assert.AreEqual(-1, MicDevicePicker.Pick(null, "MacBook"));
            Assert.AreEqual(-1, MicDevicePicker.Pick(new string[0], "MacBook"));
            Assert.AreEqual(1, MicDevicePicker.Pick(new string[] { null, "MacBook" }, "mac"), "null names are skipped");
        }

        [TestCase(0, 0, 48000, 48000)]          // (0, 0) = any frequency
        [TestCase(44100, 96000, 48000, 48000)]
        [TestCase(8000, 16000, 48000, 16000)]
        [TestCase(96000, 192000, 48000, 96000)]
        [TestCase(0, 16000, 48000, 16000)]
        [TestCase(44100, 0, 48000, 48000)]
        [TestCase(44100, 0, 22050, 44100)]
        [TestCase(96000, 44100, 48000, 48000)]  // a swapped range is repaired
        public void Picker_ChoosesTheRateWithinWhatTheDeviceReports(int min, int max, int wanted, int expected)
        {
            Assert.AreEqual(expected, MicDevicePicker.ChooseSampleRate(min, max, wanted));
        }

        // =============================================================================================================
        // CaptureSession: a fake microphone, made-up time
        // =============================================================================================================

        sealed class FakeMic : IMicBackend
        {
            public bool Permission = true;
            public int RequestCount;
            public string[] DeviceList = { "MacBook Pro Microphone", "XREAL Air 2 Ultra" };
            public int CapsMin, CapsMax;                       // reported for every device
            public bool StartReturnsNull, StartThrows;
            public int ClipFrames = 480000, ClipChannels = 1, ClipRateOverride;     // override 0 = the rate that was asked for
            public int StartCalls, EndCalls, DevicesCalls, LastLengthSec, LastStartRate;
            public string LastStartDevice = "<never started>";
            public FakeClip Clip;

            public bool HasPermission { get { return Permission; } }
            public void RequestPermission() { RequestCount++; }
            public string[] Devices() { DevicesCalls++; return DeviceList; }
            public void GetDeviceCaps(string device, out int minFreq, out int maxFreq) { minFreq = CapsMin; maxFreq = CapsMax; }

            public IClipData Start(string device, int lengthSec, int sampleRate)
            {
                StartCalls++;
                LastStartDevice = device;
                LastLengthSec = lengthSec;
                LastStartRate = sampleRate;
                if (StartThrows) throw new ArgumentException("Couldn't acquire device ID for device name " + device);
                if (StartReturnsNull) return null;
                Clip = new FakeClip(ClipFrames, ClipChannels, ClipRateOverride > 0 ? ClipRateOverride : sampleRate);
                return Clip;
            }

            public bool IsRecording(string device) { return Clip != null; }
            public int GetPosition(string device) { return Clip != null ? Clip.WriteHead : 0; }
            public void End(string device) { EndCalls++; Clip = null; }
        }

        const float Step = 0.0625f;                            // 1/16 s: exact in binary, so the clock arithmetic is exact

        static FlagSet MicFlags(string device)
        {
            FlagSet f = new FlagSet();
            f.audioSource = "mic";
            f.audio.device = device;
            return f;
        }

        static CaptureSession NewSession(FakeMic mic, FlagSet flags, WavLoader loader = null)
        {
            return new CaptureSession(mic, () => flags, "/streaming", loader);
        }

        // Records `frames` per tick and ticks the session once per Step; returns the new time.
        static double Feed(CaptureSession s, FakeMic m, double t, int ticks, int frames)
        {
            for (int i = 0; i < ticks; i++)
            {
                t += Step;
                if (m.Clip != null) m.Clip.Record(frames);
                s.Tick(t, Step);
            }
            return t;
        }

        // Ticks with no new audio.
        static double Quiet(CaptureSession s, double t, int ticks)
        {
            for (int i = 0; i < ticks; i++) { t += Step; s.Tick(t, Step); }
            return t;
        }

        [Test]
        public void Session_PicksTheDeviceByFlag_CaseInsensitively_AndAnnouncesIt()
        {
            FakeMic mic = new FakeMic();
            CaptureSession s = NewSession(mic, MicFlags("xreal"));
            List<string> seen = new List<string>();
            s.DeviceChanged += n => seen.Add(n);
            s.Start(0.0);
            Assert.AreEqual("XREAL Air 2 Ultra", mic.LastStartDevice);
            Assert.AreEqual("XREAL Air 2 Ultra", s.DeviceName);
            Assert.AreEqual(new[] { "XREAL Air 2 Ultra" }, seen.ToArray());
            Assert.AreEqual(10, mic.LastLengthSec);
            Assert.AreEqual(48000, mic.LastStartRate);
            Assert.AreEqual(1, mic.StartCalls);
        }

        [Test]
        public void Session_UsesTheDefaultDevice_WhenNothingMatches()
        {
            FakeMic mic = new FakeMic();
            CaptureSession s = NewSession(mic, MicFlags("no such device"));
            s.Start(0.0);
            Assert.IsNull(mic.LastStartDevice, "null = Unity's default device");
            Assert.AreEqual(CaptureSession.DefaultDeviceLabel, s.DeviceName);

            FakeMic mic2 = new FakeMic();
            NewSession(mic2, MicFlags("")).Start(0.0);
            Assert.IsNull(mic2.LastStartDevice, "an empty flag also means the default device");
        }

        [Test]
        public void Session_AsksForARateInsideTheDeviceRange_AndReportsWhatTheClipReallyGot()
        {
            FakeMic mic = new FakeMic { CapsMin = 8000, CapsMax = 16000 };
            CaptureSession s = NewSession(mic, MicFlags("MacBook"));
            Assert.AreEqual(48000, s.SampleRate, "48000 until a capture has started");
            s.Start(0.0);
            Assert.AreEqual(16000, mic.LastStartRate);
            Assert.AreEqual(16000, s.SampleRate);

            FakeMic mic2 = new FakeMic { ClipRateOverride = 44100 };      // the device gives another rate than asked
            CaptureSession s2 = NewSession(mic2, MicFlags("MacBook"));
            s2.Start(0.0);
            Assert.AreEqual(48000, mic2.LastStartRate);
            Assert.AreEqual(44100, s2.SampleRate, "SampleRate is the clip's real rate, not the request");
        }

        [Test]
        public void Session_CopiesTheNewSamples_AcrossTheLoop_IntoRead()
        {
            FakeMic mic = new FakeMic { ClipFrames = 2000 };               // a short loop, so it wraps many times
            CaptureSession s = NewSession(mic, MicFlags("MacBook"));
            s.Start(0.0);
            Assert.IsFalse(s.IsCapturing, "no frames yet");
            double t = 0;
            float[] dst = new float[800];
            long expected = 0;
            for (int i = 0; i < 40; i++)
            {
                t = Feed(s, mic, t, 1, 800);
                if (i == 0) Assert.IsTrue(s.IsCapturing, "frames are flowing");
                int got = s.Read(dst, 0, dst.Length);
                Assert.AreEqual(800, got, "tick " + i);
                for (int k = 0; k < got; k++)
                    if (dst[k] != (float)(expected + k)) Assert.Fail("tick " + i + ": wrong sample " + (expected + k));
                expected += got;
            }
            Assert.Greater(mic.Clip.Recorded, 10000);
            Assert.AreEqual(0, s.LostSamples);
            Assert.Greater(s.LevelRms, 0f);
            Assert.Greater(s.LevelPeak, 0f);
            Assert.AreEqual(0, s.WarnCount);
        }

        [Test]
        public void Session_MultiChannelClip_ComesOutMono()
        {
            FakeMic mic = new FakeMic { ClipFrames = 1000, ClipChannels = 2 };
            CaptureSession s = NewSession(mic, MicFlags("MacBook"));
            s.Start(0.0);
            Feed(s, mic, 0, 1, 400);
            float[] dst = new float[400];
            Assert.AreEqual(400, s.Read(dst, 0, 400));
            for (int i = 0; i < 400; i++) Assert.AreEqual(i + 0.25f, dst[i], 1e-4f);
        }

        [Test]
        public void Session_NoDevices_MeansDenied_OneWarning_ThenRetriesWhenTheAppRegainsFocus()
        {
            FakeMic mic = new FakeMic { DeviceList = new string[0] };
            FlagSet flags = MicFlags("MacBook");
            CaptureSession s = NewSession(mic, flags);
            s.Start(0.0);
            Assert.IsFalse(s.IsCapturing);
            Assert.AreEqual("", s.DeviceName);
            Assert.AreEqual(1, s.WarnCount);
            Assert.AreEqual(0, mic.StartCalls);

            double t = Quiet(s, 0, 80);
            Assert.AreEqual(1, s.WarnCount, "no repeated warnings while it waits");
            Assert.AreEqual(1, mic.DevicesCalls, "no polling: it only retries on focus");

            s.OnFocus(false, t);
            Assert.AreEqual(1, mic.DevicesCalls, "losing focus does nothing");
            s.OnFocus(true, t);                                            // still denied: retries, stays quiet
            Assert.AreEqual(2, mic.DevicesCalls);
            Assert.AreEqual(1, s.WarnCount, "the warning is not repeated");
            Assert.IsFalse(s.IsCapturing);

            mic.DeviceList = new[] { "MacBook Pro Microphone" };           // the user granted permission
            s.OnFocus(true, t);
            Assert.AreEqual(1, mic.StartCalls);
            t = Feed(s, mic, t, 2, 800);
            Assert.IsTrue(s.IsCapturing);
            Assert.AreEqual("MacBook Pro Microphone", s.DeviceName);
            Assert.AreEqual(1, s.WarnCount);

            s.OnFocus(true, t);                                            // a running capture is not restarted by focus
            Assert.AreEqual(1, mic.StartCalls);

            mic.StartReturnsNull = true;                                   // denied again later: one NEW warning is allowed
            flags.audio.device = "XREAL";
            s.OnFlagsChanged(t);
            Assert.IsFalse(s.IsCapturing);
            Assert.AreEqual(2, s.WarnCount);
            s.OnFocus(true, t);
            Assert.AreEqual(2, s.WarnCount, "and again only one");
        }

        [Test]
        public void Session_StartReturningNull_IsDenied_AndStartThrowing_IsHandled()
        {
            FakeMic nullMic = new FakeMic { StartReturnsNull = true };
            CaptureSession s = NewSession(nullMic, MicFlags("MacBook"));
            s.Start(0.0);
            Assert.IsFalse(s.IsCapturing);
            Assert.AreEqual(1, s.WarnCount);
            Assert.AreEqual("", s.DeviceName);
            Quiet(s, 0, 100);
            Assert.AreEqual(1, nullMic.StartCalls, "no retry loop");
            s.OnFocus(true, 10.0);
            Assert.AreEqual(2, nullMic.StartCalls, "retry on focus");
            Assert.AreEqual(1, s.WarnCount);

            FakeMic throwMic = new FakeMic { StartThrows = true };
            CaptureSession s2 = NewSession(throwMic, MicFlags("MacBook"));
            Assert.DoesNotThrow(() => s2.Start(0.0));
            Assert.IsFalse(s2.IsCapturing);
            Assert.AreEqual(1, s2.WarnCount);
        }

        [Test]
        public void Session_PermissionNotGranted_RequestsItAndStartsWhenItArrives()
        {
            FakeMic mic = new FakeMic { Permission = false };
            CaptureSession s = NewSession(mic, MicFlags("MacBook"));
            s.Start(0.0);
            Assert.AreEqual(1, mic.RequestCount);
            Assert.AreEqual(0, mic.DevicesCalls);
            Assert.AreEqual(0, mic.StartCalls);
            Assert.AreEqual(1, s.WarnCount);
            double t = Quiet(s, 0, 20);
            Assert.AreEqual(0, mic.StartCalls, "still waiting");
            mic.Permission = true;
            t = Feed(s, mic, t, 3, 800);
            Assert.AreEqual(1, mic.StartCalls);
            Assert.IsTrue(s.IsCapturing);
        }

        [Test]
        public void Session_Watchdog_RestartsAfterTwoSecondsWithoutNewFrames()
        {
            FakeMic mic = new FakeMic();
            CaptureSession s = NewSession(mic, MicFlags("MacBook"));
            s.Start(0.0);
            double t = Feed(s, mic, 0, 16, 600);                           // frames flow until t = 1.0
            Assert.AreEqual(1.0, t);
            Assert.IsTrue(s.IsCapturing);
            int epoch = s.Epoch;

            s.Tick(2.9375, Step);                                          // 1.9375 s of silence: not yet
            Assert.AreEqual(1, mic.StartCalls);
            Assert.AreEqual(0, s.RestartCount);
            s.Tick(3.0, Step);                                             // exactly 2.0 s: restart
            Assert.AreEqual(2, mic.StartCalls);
            Assert.AreEqual(1, mic.EndCalls, "the dead capture was ended first");
            Assert.AreEqual(1, s.RestartCount);
            Assert.IsFalse(s.IsCapturing, "not capturing until frames flow again");
            Assert.AreNotEqual(epoch, s.Epoch, "the stream has a gap: the epoch changed");
            Assert.AreEqual(0, s.WarnCount);

            t = Feed(s, mic, 3.0, 4, 600);                                 // the new clip delivers
            Assert.IsTrue(s.IsCapturing);
            Assert.AreEqual(1, s.RestartCount);
        }

        [Test]
        public void Session_Watchdog_WaitsLongerForTheVeryFirstFrame()
        {
            FakeMic mic = new FakeMic();
            CaptureSession s = NewSession(mic, MicFlags("MacBook"));
            s.Start(0.0);
            s.Tick(2.0, Step);
            s.Tick(4.9375, Step);
            Assert.AreEqual(1, mic.StartCalls, "inside the start-up grace");
            s.Tick(5.0, Step);
            Assert.AreEqual(2, mic.StartCalls, "no frame at all for 5 s: restart");
            Assert.AreEqual(1, s.RestartCount);
        }

        [Test]
        public void Session_AMicThatNeverDelivers_WarnsOnce_AndKeepsTrying()
        {
            FakeMic mic = new FakeMic();
            CaptureSession s = NewSession(mic, MicFlags("MacBook"));
            s.Start(0.0);
            double t = 0;
            for (int restart = 1; restart <= 6; restart++)
            {
                t += CaptureSession.FirstFrameGraceSec;
                s.Tick(t, Step);
                Assert.AreEqual(restart, s.RestartCount);
            }
            Assert.AreEqual(1, s.WarnCount, "one warning for the whole episode, at the third failed restart");
            Assert.AreEqual(7, mic.StartCalls);
            Assert.AreEqual(6, mic.EndCalls);
            Assert.IsFalse(s.IsCapturing);
        }

        [Test]
        public void Session_LevelFallsWhenTheMicGoesDeaf()
        {
            FakeMic mic = new FakeMic();
            CaptureSession s = NewSession(mic, MicFlags("MacBook"));
            s.Start(0.0);
            double t = 0;
            float[] drain = new float[8192];
            for (int i = 0; i < 16; i++)                                   // 1 s of a constant 0.5 signal
            {
                t += Step;
                mic.Clip.RecordValue(3000, 0.5f);
                s.Tick(t, Step);
                s.Read(drain, 0, drain.Length);
            }
            Assert.AreEqual(0.5f, s.LevelRms, 0.01f);
            Assert.AreEqual(0.5f, s.LevelPeak, 1e-6f);
            t = Quiet(s, t, 16);                                           // 1 s without frames: still inside the watchdog time
            Assert.AreEqual(0, s.RestartCount);
            Assert.Less(s.LevelRms, 0.05f, "RMS fell: a deaf mic must look flat");
            Assert.Less(s.LevelPeak, 0.15f, "peak fell too");
        }

        [Test]
        public void Session_ExactlyZeroInput_WarnsOnceAfterThreeSeconds_ButKeepsCapturing()
        {
            FakeMic mic = new FakeMic();
            CaptureSession s = NewSession(mic, MicFlags("MacBook"));
            s.Start(0.0);
            double t = 0;
            float[] drain = new float[8192];
            // 3000 frames per 1/16 s tick = 48 kHz of exact zeros
            for (int i = 0; i < 40; i++)                                   // 2.5 s
            {
                t += Step;
                mic.Clip.RecordValue(3000, 0f);
                s.Tick(t, Step);
                s.Read(drain, 0, drain.Length);
            }
            Assert.AreEqual(0, s.WarnCount, "not yet 3 s");
            for (int i = 0; i < 16; i++)                                   // 3.5 s
            {
                t += Step;
                mic.Clip.RecordValue(3000, 0f);
                s.Tick(t, Step);
                s.Read(drain, 0, drain.Length);
            }
            Assert.AreEqual(1, s.WarnCount);
            Assert.IsTrue(s.IsCapturing, "silence is a warning, not a failure");
            for (int i = 0; i < 40; i++)
            {
                t += Step;
                mic.Clip.RecordValue(3000, 0f);
                s.Tick(t, Step);
                s.Read(drain, 0, drain.Length);
            }
            Assert.AreEqual(1, s.WarnCount, "only once");
            Assert.AreEqual(0, s.RestartCount);
        }

        [Test]
        public void Session_FlagReload_RestartsOnlyWhenTheAudioSettingsChanged()
        {
            FakeMic mic = new FakeMic();
            FlagSet flags = MicFlags("MacBook");
            CaptureSession s = NewSession(mic, flags);
            List<string> seen = new List<string>();
            s.DeviceChanged += n => seen.Add(n);
            s.Start(0.0);
            double t = Feed(s, mic, 0, 4, 800);
            flags.ghost.fill = 0.5f;                                       // unrelated
            s.OnFlagsChanged(t);
            Assert.AreEqual(1, mic.StartCalls);

            flags.audio.device = "XREAL";
            s.OnFlagsChanged(t);
            Assert.AreEqual(2, mic.StartCalls);
            Assert.AreEqual("XREAL Air 2 Ultra", mic.LastStartDevice);
            Assert.AreEqual("XREAL Air 2 Ultra", s.DeviceName);
            Assert.AreEqual(new[] { "MacBook Pro Microphone", "XREAL Air 2 Ultra" }, seen.ToArray());

            s.OnFlagsChanged(t);                                           // same flags again: nothing
            Assert.AreEqual(2, mic.StartCalls);
        }

        [Test]
        public void Session_Stop_EndsTheMicrophoneAndClearsTheDevice()
        {
            FakeMic mic = new FakeMic();
            CaptureSession s = NewSession(mic, MicFlags("MacBook"));
            s.Start(0.0);
            Feed(s, mic, 0, 2, 800);
            s.Stop();
            Assert.AreEqual(1, mic.EndCalls);
            Assert.IsFalse(s.IsCapturing);
            Assert.AreEqual("", s.DeviceName);
            s.OnFocus(true, 1.0);                                          // stopped: nothing restarts it
            s.OnFlagsChanged(1.0);
            s.Tick(10.0, Step);
            Assert.AreEqual(1, mic.StartCalls);
        }

        // ---- WAV replay through the session ------------------------------------------------------------------

        static WavLoader FakeLoader(float[] samples, int rate, List<string> pathsSeen, bool succeed = true)
        {
            return delegate (string path, out WavData data, out string error)
            {
                pathsSeen.Add(path);
                if (!succeed) { data = null; error = "file not found: " + path; return false; }
                data = new WavData(samples, rate, 1, 16, false);
                error = null;
                return true;
            };
        }

        [Test]
        public void Session_WavMode_ReplaysTheFileThroughTheSameRead_AndNeverTouchesTheMicrophone()
        {
            FakeMic mic = new FakeMic();
            FlagSet flags = new FlagSet { audioSource = "wav", wavReplayFile = "fixtures-demo/learn-g.wav" };
            List<string> paths = new List<string>();
            CaptureSession s = NewSession(mic, flags, FakeLoader(new float[] { 0.1f, 0.2f, 0.3f, 0.4f }, 8, paths));
            s.Start(10.0);
            Assert.IsTrue(s.IsReplay);
            Assert.IsTrue(s.IsCapturing);
            Assert.AreEqual(8, s.SampleRate, "the file's rate, not resampled");
            Assert.AreEqual("wav:fixtures-demo/learn-g.wav", s.DeviceName);
            Assert.AreEqual(Path.Combine("/streaming", "fixtures-demo/learn-g.wav"), paths[0]);

            float[] dst = new float[16];
            s.Tick(10.0, Step);
            Assert.AreEqual(0, s.Read(dst, 0, 16), "nothing is released before time passes");
            s.Tick(10.5, 0.5f);                                            // 0.5 s * 8 Hz = 4 samples
            Assert.AreEqual(4, s.Read(dst, 0, 16));
            Assert.AreEqual(new float[] { 0.1f, 0.2f, 0.3f, 0.4f }, new[] { dst[0], dst[1], dst[2], dst[3] });
            s.Tick(11.0, 0.5f);                                            // it loops
            Assert.AreEqual(4, s.Read(dst, 0, 16));
            Assert.AreEqual(0.1f, dst[0]);
            Assert.Greater(s.LevelRms, 0f);
            Assert.AreEqual(0, mic.StartCalls);
            Assert.AreEqual(0, mic.DevicesCalls);
        }

        [Test]
        public void Session_WavMode_MissingFile_FailsLoudly_WithoutFallingBackToTheMicrophone()
        {
            FakeMic mic = new FakeMic();
            FlagSet flags = new FlagSet { audioSource = "wav", wavReplayFile = "nope.wav" };
            List<string> paths = new List<string>();
            CaptureSession s = NewSession(mic, flags, FakeLoader(null, 0, paths, false));
            s.Start(0.0);
            Assert.IsFalse(s.IsCapturing);
            Assert.IsFalse(s.IsReplay);
            Assert.AreEqual(1, s.WarnCount);
            Assert.AreEqual(0, mic.StartCalls, "the SIMULATED INPUT badge must never sit on top of a real microphone");
            s.Tick(1.0, 1f);
            s.OnFocus(true, 2.0);                                          // retries the load on focus
            Assert.AreEqual(2, paths.Count);
            Assert.AreEqual(1, s.WarnCount);
        }

        [Test]
        public void Session_FlagSwitchBetweenMicAndWav_SwitchesTheSource()
        {
            FakeMic mic = new FakeMic();
            FlagSet flags = MicFlags("MacBook");
            flags.wavReplayFile = "a.wav";
            List<string> paths = new List<string>();
            CaptureSession s = NewSession(mic, flags, FakeLoader(new float[] { 0.5f, 0.5f }, 8, paths));
            s.Start(0.0);
            Assert.IsFalse(s.IsReplay);
            flags.audioSource = "wav";
            s.OnFlagsChanged(1.0);
            Assert.IsTrue(s.IsReplay);
            Assert.AreEqual(1, mic.EndCalls, "the microphone was released");
            Assert.AreEqual(8, s.SampleRate);
            flags.audioSource = "mic";
            s.OnFlagsChanged(2.0);
            Assert.IsFalse(s.IsReplay);
            Assert.AreEqual(2, mic.StartCalls);
            Assert.AreEqual(48000, s.SampleRate, "a new rate gets a new ring: readers see the new SampleRate");
        }
    }
}
