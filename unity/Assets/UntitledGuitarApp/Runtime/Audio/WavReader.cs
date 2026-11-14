using System;
using System.IO;

namespace UntitledGuitarApp.Runtime.Audio
{
    /// <summary>A decoded WAV: mono float samples in -1..1 at the file's own sample rate (never resampled).</summary>
    public sealed class WavData
    {
        /// <summary>Mono samples. Stereo files are mixed down as (L + R) / 2. NaN and infinity in float files become 0.</summary>
        public readonly float[] Samples;
        /// <summary>Sample rate stored in the file, reported as it is.</summary>
        public readonly int SampleRate;
        /// <summary>Channels in the file (1 or 2) before the mix-down.</summary>
        public readonly int SourceChannels;
        /// <summary>Bits per sample in the file (16, 24 or 32).</summary>
        public readonly int SourceBitsPerSample;
        /// <summary>True when the file held IEEE float samples, false for integer PCM.</summary>
        public readonly bool SourceIsFloat;

        public WavData(float[] samples, int sampleRate, int sourceChannels, int sourceBitsPerSample, bool sourceIsFloat)
        {
            if (samples == null) throw new ArgumentNullException("samples");
            Samples = samples;
            SampleRate = sampleRate;
            SourceChannels = sourceChannels;
            SourceBitsPerSample = sourceBitsPerSample;
            SourceIsFloat = sourceIsFloat;
        }

        public double DurationSec { get { return SampleRate > 0 ? Samples.Length / (double)SampleRate : 0.0; } }
    }

    /// <summary>
    /// Minimal RIFF/WAVE decoder for the replay source and the EditorSimRig. Plain C#, no Unity types.
    /// Accepts: integer PCM 16 / 24 / 32-bit and IEEE float 32-bit (also inside WAVE_FORMAT_EXTENSIBLE), mono or stereo
    /// (stereo is mixed down to mono), any sample rate (reported, not resampled), extra chunks (LIST, fact, ...), odd-sized
    /// chunks with their pad byte, and the "streaming" data size 0xFFFFFFFF (= up to the end of the file). A trailing
    /// partial sample frame is ignored.
    /// Anything else is REJECTED with a human-readable reason; no method here throws into the caller (this runs in the
    /// start-up path of the frame loop).
    /// </summary>
    public static class WavReader
    {
        /// <summary>Files larger than this are refused instead of being loaded into memory (a replay clip is seconds long).</summary>
        public const long MaxFileBytes = 256L * 1024 * 1024;

        /// <summary>Reads and decodes a file. Returns false and fills <paramref name="error"/> on any problem (missing file, I/O error, bad WAV).</summary>
        public static bool TryReadFile(string path, out WavData data, out string error)
        {
            data = null;
            try
            {
                if (string.IsNullOrEmpty(path)) { error = "no file path given"; return false; }
                if (!File.Exists(path)) { error = "file not found: " + path; return false; }
                long size = new FileInfo(path).Length;
                if (size > MaxFileBytes) { error = "file is too large to load (" + size + " bytes, limit " + MaxFileBytes + "): " + path; return false; }
                byte[] bytes = File.ReadAllBytes(path);
                return TryParse(bytes, out data, out error);
            }
            catch (Exception e)
            {
                data = null;
                error = "could not read " + path + ": " + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        /// <summary>Decodes WAV bytes. Returns false and fills <paramref name="error"/> when the bytes are not a WAV we can play.</summary>
        public static bool TryParse(byte[] bytes, out WavData data, out string error)
        {
            data = null;
            try
            {
                return Parse(bytes, out data, out error);
            }
            catch (Exception e)
            {
                data = null;
                error = "unexpected failure while decoding WAV: " + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        const ushort FormatPcm = 1;
        const ushort FormatFloat = 3;
        const ushort FormatExtensible = 0xFFFE;

        static bool Parse(byte[] b, out WavData data, out string error)
        {
            data = null;
            if (b == null) return Fail("no data (null array)", out error);
            long len = b.Length;
            if (len < 12) return Fail("file too short for a RIFF/WAVE header (" + len + " bytes)", out error);
            if (len > MaxFileBytes) return Fail("file is too large to decode (" + len + " bytes, limit " + MaxFileBytes + ")", out error);

            if (!Tag(b, 0, "RIFF"))
            {
                if (Tag(b, 0, "RF64") || Tag(b, 0, "BW64")) return Fail("RF64/BW64 files are not supported", out error);
                return Fail("not a RIFF file (starts with '" + Printable(b, 0) + "')", out error);
            }
            if (!Tag(b, 8, "WAVE")) return Fail("not a WAVE file (RIFF form type is '" + Printable(b, 8) + "')", out error);

            // Walk the chunks. Both 'fmt ' and 'data' are needed; their order in the file is not enforced.
            bool haveFmt = false, haveData = false;
            long fmtAt = 0, fmtSize = 0, dataAt = 0, dataSize = 0;
            long pos = 12;
            while (pos + 8 <= len && !(haveFmt && haveData))
            {
                uint size = U32(b, pos + 4);
                long body = pos + 8;
                if (Tag(b, pos, "fmt "))
                {
                    if (!haveFmt)
                    {
                        if (size < 16) return Fail("'fmt ' chunk is " + size + " bytes, at least 16 are needed", out error);
                        if (body + size > len) return Fail("'fmt ' chunk runs past the end of the file (truncated file)", out error);
                        haveFmt = true; fmtAt = body; fmtSize = size;
                    }
                }
                else if (Tag(b, pos, "data"))
                {
                    if (!haveData)
                    {
                        long avail = len - body;
                        if (size == 0xFFFFFFFFu) dataSize = avail;            // streaming writers: "until the end of the file"
                        else if (size > avail) return Fail("'data' chunk declares " + size + " bytes but only " + avail + " remain (truncated file)", out error);
                        else dataSize = size;
                        haveData = true; dataAt = body;
                        if (size == 0xFFFFFFFFu) break;                       // nothing can follow it
                    }
                }
                long next = body + size + (size & 1);                          // chunks are padded to an even size
                if (next > len) break;
                pos = next;
            }
            if (!haveFmt) return Fail("missing 'fmt ' chunk", out error);
            if (!haveData) return Fail("missing 'data' chunk", out error);

            int tag = U16(b, fmtAt);
            int channels = U16(b, fmtAt + 2);
            long sampleRate = U32(b, fmtAt + 4);
            int blockAlign = U16(b, fmtAt + 12);
            int bits = U16(b, fmtAt + 14);

            if (tag == FormatExtensible)
            {
                if (fmtSize < 40) return Fail("WAVE_FORMAT_EXTENSIBLE 'fmt ' chunk is " + fmtSize + " bytes, at least 40 are needed", out error);
                int cb = U16(b, fmtAt + 16);
                if (cb < 22) return Fail("WAVE_FORMAT_EXTENSIBLE extension is " + cb + " bytes, at least 22 are needed", out error);
                if (!IsStandardSubFormatTail(b, fmtAt + 26)) return Fail("WAVE_FORMAT_EXTENSIBLE sub-format GUID is not a standard format-tag GUID", out error);
                tag = U16(b, fmtAt + 24);                                      // the first two GUID bytes hold the ordinary format tag
            }

            bool isFloat;
            if (tag == FormatPcm) isFloat = false;
            else if (tag == FormatFloat) isFloat = true;
            else return Fail("unsupported WAV format tag " + tag + " (supported: 1 = integer PCM, 3 = IEEE float)", out error);

            if (channels == 0) return Fail("channel count is 0", out error);
            if (channels > 2) return Fail("unsupported channel count " + channels + " (only mono and stereo)", out error);
            if (sampleRate == 0) return Fail("sample rate is 0", out error);
            if (sampleRate > int.MaxValue) return Fail("sample rate " + sampleRate + " is out of range", out error);
            if (isFloat) { if (bits != 32) return Fail("unsupported float sample size " + bits + " bits (only 32-bit float)", out error); }
            else if (bits != 16 && bits != 24 && bits != 32) return Fail("unsupported PCM sample size " + bits + " bits (supported: 16, 24, 32)", out error);

            int bytesPerSample = bits / 8;
            int frameBytes = channels * bytesPerSample;
            if (blockAlign != frameBytes)
                return Fail("'fmt ' block align " + blockAlign + " does not match " + channels + " channel(s) x " + bytesPerSample + " bytes = " + frameBytes, out error);
            if (dataSize == 0) return Fail("'data' chunk is empty (0 bytes of audio)", out error);
            long frames = dataSize / frameBytes;
            if (frames == 0) return Fail("'data' chunk holds less than one sample frame (" + dataSize + " bytes)", out error);

            float[] mono = new float[frames];
            long p = dataAt;
            for (long i = 0; i < frames; i++)
            {
                float v = Decode(b, p, bits, isFloat);
                p += bytesPerSample;
                if (channels == 2)
                {
                    float r = Decode(b, p, bits, isFloat);
                    p += bytesPerSample;
                    v = (v + r) * 0.5f;
                }
                mono[i] = v;
            }
            data = new WavData(mono, (int)sampleRate, channels, bits, isFloat);
            error = null;
            return true;
        }

        static float Decode(byte[] b, long p, int bits, bool isFloat)
        {
            int ip = (int)p;
            if (isFloat)
            {
                int raw = b[ip] | (b[ip + 1] << 8) | (b[ip + 2] << 16) | (b[ip + 3] << 24);
                float f = BitConverter.Int32BitsToSingle(raw);
                return (float.IsNaN(f) || float.IsInfinity(f)) ? 0f : f;
            }
            switch (bits)
            {
                case 16:
                    return (short)(b[ip] | (b[ip + 1] << 8)) / 32768f;
                case 24:
                {
                    int s = b[ip] | (b[ip + 1] << 8) | (b[ip + 2] << 16);
                    if ((s & 0x800000) != 0) s |= unchecked((int)0xFF000000);
                    return s / 8388608f;
                }
                default: // 32
                {
                    int s = b[ip] | (b[ip + 1] << 8) | (b[ip + 2] << 16) | (b[ip + 3] << 24);
                    return s / 2147483648f;
                }
            }
        }

        // The standard sub-format GUID is {XXXXXXXX-0000-0010-8000-00AA00389B71}; bytes 2..15 of it are checked here.
        static bool IsStandardSubFormatTail(byte[] b, long at)
        {
            byte[] tail = { 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 };
            for (int i = 0; i < tail.Length; i++) if (b[at + i] != tail[i]) return false;
            return true;
        }

        static bool Fail(string reason, out string error) { error = reason; return false; }

        static bool Tag(byte[] b, long at, string tag)
        {
            if (at + 4 > b.Length) return false;
            for (int i = 0; i < 4; i++) if (b[at + i] != (byte)tag[i]) return false;
            return true;
        }

        static string Printable(byte[] b, long at)
        {
            char[] c = new char[4];
            for (int i = 0; i < 4; i++)
            {
                byte v = at + i < b.Length ? b[at + i] : (byte)'?';
                c[i] = (v >= 32 && v < 127) ? (char)v : '?';
            }
            return new string(c);
        }

        static ushort U16(byte[] b, long at) { return (ushort)(b[at] | (b[at + 1] << 8)); }

        static uint U32(byte[] b, long at)
        {
            return (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));
        }
    }
}
