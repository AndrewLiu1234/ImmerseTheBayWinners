using System;
using System.IO;
using System.Text;

namespace UntitledGuitarApp.Core.Tests.Audio
{
    /// <summary>
    /// Minimal RIFF/WAVE reader for fixtures: PCM 8/16/24/32-bit and IEEE float 32/64, incl. WAVE_FORMAT_EXTENSIBLE.
    /// Multichannel files are averaged to mono (the app captures mono, §19). Test-side only; Runtime has its own capture path.
    /// </summary>
    public static class WavReader
    {
        public static float[] ReadMono(string path, out int sampleRate)
        {
            using (var fs = File.OpenRead(path)) return ReadMono(fs, out sampleRate);
        }

        public static float[] ReadMono(Stream stream, out int sampleRate)
        {
            var r = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            if (Tag(r) != "RIFF") throw new InvalidDataException("not a RIFF file");
            r.ReadUInt32();
            if (Tag(r) != "WAVE") throw new InvalidDataException("not a WAVE file");

            int format = 0, channels = 0, bits = 0; sampleRate = 0;
            while (stream.Position + 8 <= stream.Length)
            {
                string id = Tag(r);
                long size = r.ReadUInt32();
                long next = stream.Position + size + (size & 1);  // chunks are word-aligned
                if (id == "fmt ")
                {
                    format = r.ReadUInt16(); channels = r.ReadUInt16(); sampleRate = r.ReadInt32();
                    r.ReadInt32(); r.ReadUInt16(); bits = r.ReadUInt16();
                    if (format == 0xFFFE && size >= 40) { r.ReadUInt16(); r.ReadUInt16(); r.ReadUInt32(); format = r.ReadUInt16(); }
                }
                else if (id == "data")
                {
                    if (channels < 1 || sampleRate < 1) throw new InvalidDataException("data chunk before fmt chunk");
                    if (size > stream.Length - stream.Position) size = stream.Length - stream.Position;  // tolerate truncated/streamed headers
                    int bytesPer = bits / 8;
                    if (bytesPer < 1) throw new InvalidDataException($"unsupported bit depth {bits}");
                    long frames = size / (bytesPer * channels);
                    var mono = new float[frames];
                    for (long f = 0; f < frames; f++)
                    {
                        double acc = 0;
                        for (int c = 0; c < channels; c++) acc += Sample(r, format, bits);
                        mono[f] = (float)(acc / channels);
                    }
                    return mono;
                }
                stream.Position = Math.Min(next, stream.Length);
            }
            throw new InvalidDataException("no data chunk");
        }

        static double Sample(BinaryReader r, int format, int bits)
        {
            if (format == 1)
            {
                switch (bits)
                {
                    case 8: return (r.ReadByte() - 128) / 128.0;
                    case 16: return r.ReadInt16() / 32768.0;
                    case 24: { int b0 = r.ReadByte(), b1 = r.ReadByte(), b2 = (sbyte)r.ReadByte(); return ((b2 << 16) | (b1 << 8) | b0) / 8388608.0; }
                    case 32: return r.ReadInt32() / 2147483648.0;
                }
            }
            else if (format == 3)
            {
                if (bits == 32) return r.ReadSingle();
                if (bits == 64) return r.ReadDouble();
            }
            throw new InvalidDataException($"unsupported WAV format {format} / {bits} bit");
        }

        static string Tag(BinaryReader r) => Encoding.ASCII.GetString(r.ReadBytes(4));

        /// <summary>Writes mono 16-bit PCM or 32-bit float (for round-trip tests only; never used to create fixtures).</summary>
        public static byte[] Encode(float[] samples, int sampleRate, bool asFloat, int channels = 1)
        {
            int bits = asFloat ? 32 : 16, bytesPer = bits / 8;
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            int dataBytes = samples.Length * bytesPer * channels;
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + 12 + dataBytes); w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((ushort)(asFloat ? 3 : 1)); w.Write((ushort)channels);
            w.Write(sampleRate); w.Write(sampleRate * bytesPer * channels); w.Write((ushort)(bytesPer * channels)); w.Write((ushort)bits);
            w.Write(Encoding.ASCII.GetBytes("LIST")); w.Write(4); w.Write(Encoding.ASCII.GetBytes("INFO"));  // a chunk to skip
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(dataBytes);
            foreach (var s in samples)
                for (int c = 0; c < channels; c++)
                {
                    if (asFloat) w.Write(s);
                    else w.Write((short)Math.Max(-32768, Math.Min(32767, Math.Round(s * 32768.0))));
                }
            w.Flush();
            return ms.ToArray();
        }
    }
}
