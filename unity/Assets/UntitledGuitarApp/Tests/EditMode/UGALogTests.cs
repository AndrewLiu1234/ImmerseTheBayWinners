// UGALogTests.cs: EditMode tests for Runtime/Logging (UGALog, SessionLineWriter, SessionRecorder).
// No Play Mode, no network, no sleeping. Anything that would block (a stalled disk) is simulated with a writer the test
// controls; threads are joined, never slept on. Lines are captured through UGALog.Output, so nothing reaches the console
// (Unity's test runner fails a test that logs an error).
//
// The JSON checks use StrictJson below: a small RFC 8259 validator and decoder that shares no code with UGALog, so a line
// that passes is valid JSON AND decodes back to exactly the values that went in.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Tests.EditMode
{
    /// <summary>A strict JSON validator and string decoder, independent of UGALog.</summary>
    internal static class StrictJson
    {
        /// <summary>null when <paramref name="s"/> is exactly one valid JSON value, otherwise what is wrong.</summary>
        public static string Check(string s)
        {
            int i = 0;
            try
            {
                SkipSpace(s, ref i);
                ParseValue(s, ref i, 0);
                SkipSpace(s, ref i);
                return i == s.Length ? null : "trailing characters at index " + i;
            }
            catch (FormatException e)
            {
                return e.Message + " (at index " + i + ")";
            }
        }

        /// <summary>Decodes the JSON string that starts at s[i] (the opening quote) and moves i past the closing quote.</summary>
        public static string ReadString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') throw new FormatException("expected a string");
            i++;
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("unterminated string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c < 0x20) throw new FormatException("raw control character U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture) + " inside a string");
                if (c == '\\')
                {
                    if (i >= s.Length) throw new FormatException("dangling backslash");
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw new FormatException("short \\u escape");
                            int code = 0;
                            for (int k = 0; k < 4; k++)
                            {
                                int d = Hex(s[i + k]);
                                if (d < 0) throw new FormatException("bad hex digit in \\u escape");
                                code = code * 16 + d;
                            }
                            i += 4;
                            sb.Append((char)code);
                            break;
                        default: throw new FormatException("bad escape \\" + e);
                    }
                    continue;
                }
                if (char.IsHighSurrogate(c))
                {
                    if (i < s.Length && char.IsLowSurrogate(s[i])) { sb.Append(c); sb.Append(s[i++]); continue; }
                    throw new FormatException("unpaired high surrogate");
                }
                if (char.IsLowSurrogate(c)) throw new FormatException("unpaired low surrogate");
                sb.Append(c);
            }
        }

        static int Hex(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        static void SkipSpace(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        static void ParseValue(string s, ref int i, int depth)
        {
            if (depth > 20) throw new FormatException("nesting too deep");
            if (i >= s.Length) throw new FormatException("unexpected end of text");
            char c = s[i];
            if (c == '{') ParseObject(s, ref i, depth);
            else if (c == '[') ParseArray(s, ref i, depth);
            else if (c == '"') ReadString(s, ref i);
            else if (c == 't') Literal(s, ref i, "true");
            else if (c == 'f') Literal(s, ref i, "false");
            else if (c == 'n') Literal(s, ref i, "null");
            else ParseNumber(s, ref i);
        }

        static void ParseObject(string s, ref int i, int depth)
        {
            i++;
            SkipSpace(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return; }
            while (true)
            {
                SkipSpace(s, ref i);
                ReadString(s, ref i);
                SkipSpace(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':'");
                i++;
                SkipSpace(s, ref i);
                ParseValue(s, ref i, depth + 1);
                SkipSpace(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return; }
                throw new FormatException("expected ',' or '}'");
            }
        }

        static void ParseArray(string s, ref int i, int depth)
        {
            i++;
            SkipSpace(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return; }
            while (true)
            {
                SkipSpace(s, ref i);
                ParseValue(s, ref i, depth + 1);
                SkipSpace(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return; }
                throw new FormatException("expected ',' or ']'");
            }
        }

        static void Literal(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw new FormatException("expected " + word);
            i += word.Length;
        }

        static bool IsDigit(string s, int i)
        {
            return i < s.Length && s[i] >= '0' && s[i] <= '9';
        }

        static void ParseNumber(string s, ref int i)
        {
            if (i < s.Length && s[i] == '-') i++;
            if (!IsDigit(s, i)) throw new FormatException("bad number");
            if (s[i] == '0') i++;
            else while (IsDigit(s, i)) i++;
            if (i < s.Length && s[i] == '.')
            {
                i++;
                if (!IsDigit(s, i)) throw new FormatException("digits expected after '.'");
                while (IsDigit(s, i)) i++;
            }
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                i++;
                if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                if (!IsDigit(s, i)) throw new FormatException("digits expected in the exponent");
                while (IsDigit(s, i)) i++;
            }
        }
    }

    /// <summary>Diverts UGALog.Output into a list for the lifetime of the object.</summary>
    internal sealed class LogCapture : IDisposable
    {
        public readonly List<KeyValuePair<LogType, string>> Lines = new List<KeyValuePair<LogType, string>>();
        readonly Action<LogType, string> _previous;

        public LogCapture()
        {
            _previous = UGALog.Output;
            UGALog.Output = delegate(LogType type, string line)
            {
                lock (Lines) Lines.Add(new KeyValuePair<LogType, string>(type, line));
            };
        }

        public void Dispose()
        {
            UGALog.Output = _previous;
        }

        public string[] Texts()
        {
            lock (Lines)
            {
                string[] texts = new string[Lines.Count];
                for (int i = 0; i < texts.Length; i++) texts[i] = Lines[i].Value;
                return texts;
            }
        }
    }

    // ================================================================================================================
    public class UGALogTests
    {
        static string J(string s) { return s.Replace('\'', '"'); }

        enum Mode { Aligned, HeadLocked }

        // The text between the prefix and the end of the line, checked to be one valid JSON object on one line.
        static string JsonOf(string line)
        {
            StringAssert.StartsWith(UGALog.Prefix, line);
            Assert.AreEqual(-1, line.IndexOfAny(new[] { '\n', '\r', (char)0x85, (char)0x2028, (char)0x2029 }), "the line must be a single line: " + line);
            string json = line.Substring(UGALog.Prefix.Length);
            Assert.IsNull(StrictJson.Check(json), "not valid JSON: " + json);
            Assert.AreEqual('{', json[0]);
            return json;
        }

        // ---- the line --------------------------------------------------------------------------------------------------

        [Test]
        public void Format_IsThePrefixAndOneJsonObject()
        {
            Assert.AreEqual("[UGA] ", UGALog.Prefix);
            Assert.AreEqual(J("[UGA] {'t':1.500,'evt':'hello'}"), UGALog.Format(1.5, "hello"));
            Assert.AreEqual(J("{'t':1.500,'evt':'hello'}"), UGALog.FormatJson(1.5, "hello"));
            Assert.AreEqual(J("[UGA] {'t':0.000,'evt':'e','a':1,'b':'two','c':true}"),
                UGALog.Format(0, "e", "a", 1, "b", "two", "c", true));
        }

        [Test]
        public void Format_TimeHasThreeDecimals()
        {
            Assert.AreEqual(J("{'t':0.000,'evt':'e'}"), UGALog.FormatJson(0.0, "e"));
            Assert.AreEqual(J("{'t':12.346,'evt':'e'}"), UGALog.FormatJson(12.3456, "e"));
            Assert.AreEqual(J("{'t':60.000,'evt':'e'}"), UGALog.FormatJson(59.9996, "e"));
            Assert.AreEqual(J("{'t':3600.500,'evt':'e'}"), UGALog.FormatJson(3600.5, "e"));
            Assert.AreEqual(J("{'t':0.001,'evt':'e'}"), UGALog.FormatJson(0.0006, "e"));
            Assert.AreEqual(J("{'t':0.000,'evt':'e'}"), UGALog.FormatJson(0.0004, "e"));
            Assert.AreEqual(J("{'t':0.050,'evt':'e'}"), UGALog.FormatJson(0.05, "e"));
            Assert.AreEqual(J("{'t':1000000000.000,'evt':'e'}"), UGALog.FormatJson(1e9, "e"));
            Assert.AreEqual(J("{'t':-1.500,'evt':'e'}"), UGALog.FormatJson(-1.5, "e"));
            Assert.AreEqual(J("{'t':0.000,'evt':'e'}"), UGALog.FormatJson(-0.0001, "e"), "a negative zero is not written");
            Assert.AreEqual(J("{'t':0.000,'evt':'e'}"), UGALog.FormatJson(double.NaN, "e"));
            Assert.AreEqual(J("{'t':0.000,'evt':'e'}"), UGALog.FormatJson(double.PositiveInfinity, "e"));
        }

        [Test]
        public void Now_IsNonNegative_AndNeverGoesBackwards()
        {
            double last = UGALog.Now;
            Assert.GreaterOrEqual(last, 0.0);
            for (int i = 0; i < 1000; i++)
            {
                double now = UGALog.Now;
                Assert.GreaterOrEqual(now, last);
                last = now;
            }
        }

        [Test]
        public void Event_OverloadsWriteTheDocumentedLines_ThroughTheOutputSink()
        {
            using (LogCapture capture = new LogCapture())
            {
                UGALog.Event("plain");
                UGALog.Event("one", "a", 1);
                UGALog.Event("two", "a", 1, "b", "x");
                UGALog.Event("three", "a", 1, "b", "x", "c", null);

                Assert.AreEqual(4, capture.Lines.Count);
                foreach (KeyValuePair<LogType, string> line in capture.Lines) Assert.AreEqual(LogType.Log, line.Key);
                string[] json = new string[4];
                for (int i = 0; i < 4; i++) json[i] = JsonOf(capture.Lines[i].Value);

                StringAssert.StartsWith(J("{'t':"), json[0]);
                StringAssert.EndsWith(J(",'evt':'plain'}"), json[0]);
                StringAssert.EndsWith(J(",'evt':'one','a':1}"), json[1]);
                StringAssert.EndsWith(J(",'evt':'two','a':1,'b':'x'}"), json[2]);
                StringAssert.EndsWith(J(",'evt':'three','a':1,'b':'x','c':null}"), json[3]);
            }
        }

        [Test]
        public void WarnAndError_CarryALevelAndTheirTextInMsg()
        {
            using (LogCapture capture = new LogCapture())
            {
                UGALog.Warn("w_evt", "careful: \"quote\"");
                UGALog.Error("e_evt", "bad\nthing");

                Assert.AreEqual(2, capture.Lines.Count);
                Assert.AreEqual(LogType.Warning, capture.Lines[0].Key);
                Assert.AreEqual(LogType.Error, capture.Lines[1].Key);
                StringAssert.EndsWith(J(",'evt':'w_evt','lvl':'warn','msg':'careful: \\'quote\\''}"), JsonOf(capture.Lines[0].Value));
                StringAssert.EndsWith(J(",'evt':'e_evt','lvl':'error','msg':'bad\\nthing'}"), JsonOf(capture.Lines[1].Value));
            }
        }

        [Test]
        public void Output_IsWhereLinesGo_AndCanBeRestored()
        {
            Action<LogType, string> before = UGALog.Output;
            Action<LogType, string> mine = delegate { };
            UGALog.Output = mine;
            try
            {
                Assert.AreSame(mine, UGALog.Output);
            }
            finally
            {
                UGALog.Output = before;
            }
            Assert.AreSame(before, UGALog.Output);
        }

        [Test]
        public void NothingThrows_NotEvenForNullsOrASinkThatThrows()
        {
            using (LogCapture capture = new LogCapture())
            {
                Assert.DoesNotThrow(delegate { UGALog.Event(null); });
                Assert.DoesNotThrow(delegate { UGALog.Event(null, null, null); });
                Assert.DoesNotThrow(delegate { UGALog.Event(null, null, null, null, null, null, null); });
                Assert.DoesNotThrow(delegate { UGALog.Warn(null, null); });
                Assert.DoesNotThrow(delegate { UGALog.Error(null, null); });
                JsonOf(capture.Lines[0].Value);
                StringAssert.EndsWith(J("'evt':''}"), capture.Lines[0].Value, "a null event name is written as an empty string");
                StringAssert.EndsWith(J("'msg':null}"), capture.Lines[3].Value, "a null message is written as null");
            }
            Action<LogType, string> before = UGALog.Output;
            UGALog.Output = delegate { throw new InvalidOperationException("sink failure"); };
            try
            {
                Assert.DoesNotThrow(delegate { UGALog.Event("x", "k", 1); });
                Assert.DoesNotThrow(delegate { UGALog.Error("x", "y"); });
            }
            finally
            {
                UGALog.Output = before;
            }
        }

        // ---- escaping ---------------------------------------------------------------------------------------------------

        [Test]
        public void Strings_AreEscaped()
        {
            // input -> the text expected between the quotes
            string[][] cases =
            {
                new[] { "plain text 123", "plain text 123" },
                new[] { "he said \"hi\"", "he said \\\"hi\\\"" },
                new[] { "a\\b", "a\\\\b" },
                new[] { "line1\nline2", "line1\\nline2" },
                new[] { "tab\there", "tab\\there" },
                new[] { "cr\rhere", "cr\\rhere" },
                new[] { "bs\bff\f", "bs\\bff\\f" },
                new[] { "\r\n", "\\r\\n" },
                new[] { "nul" + (char)0 + "end", "nul\\u0000end" },
                new[] { "ctl" + (char)1 + (char)0x1f, "ctl\\u0001\\u001f" },
                new[] { "ctl" + (char)0x0b + (char)0x0c, "ctl\\u000b\\f" },
                new[] { "del" + (char)0x7f, "del" + (char)0x7f },
                new[] { "nel" + (char)0x85, "nel\\u0085" },
                new[] { "ls" + (char)0x2028 + "ps" + (char)0x2029, "ls\\u2028ps\\u2029" },
                new[] { "slash/slash", "slash/slash" },
                new[] { "'single' <b> & = ;", "'single' <b> & = ;" },
            };
            foreach (string[] c in cases)
            {
                string json = UGALog.FormatJson(1, "e", "s", c[0]);
                Assert.AreEqual(J("{'t':1.000,'evt':'e','s':'") + c[1] + J("'}"), json, "input: " + Regex.Escape(c[0]));
                Assert.IsNull(StrictJson.Check(json), json);
            }
        }

        [Test]
        public void NonAsciiText_PassesThroughUnchanged()
        {
            string accented = "caf" + (char)0xE9;
            string cjk = "" + (char)0x5938 + (char)0x5938 + (char)0x96C5 + (char)0x601D;
            string emoji = char.ConvertFromUtf32(0x1F600);
            foreach (string s in new[] { accented, cjk, emoji, accented + cjk + emoji })
            {
                string json = UGALog.FormatJson(1, "e", "s", s);
                Assert.AreEqual(J("{'t':1.000,'evt':'e','s':'") + s + J("'}"), json);
                Assert.IsNull(StrictJson.Check(json));
            }
        }

        [Test]
        public void UnpairedSurrogates_BecomeTheReplacementCharacter()
        {
            string high = "a" + (char)0xD800 + "b";
            string low = "a" + (char)0xDC00 + "b";
            string atEnd = "a" + (char)0xD83D;
            string reversed = "" + (char)0xDE00 + (char)0xD83D;
            foreach (string s in new[] { high, low, atEnd, reversed })
            {
                string json = UGALog.FormatJson(1, "e", "s", s);
                Assert.IsNull(StrictJson.Check(json), "lone surrogate must not produce invalid JSON: " + json);
                StringAssert.Contains("\\ufffd", json);
            }
            Assert.AreEqual(J("{'t':1.000,'evt':'e','s':'a\\ufffdb'}"), UGALog.FormatJson(1, "e", "s", high));
        }

        [Test]
        public void EveryStringRoundTripsThroughAStrictParser()
        {
            List<string> inputs = new List<string>();
            StringBuilder everyChar = new StringBuilder();
            for (int c = 0; c < 0x300; c++) everyChar.Append((char)c);
            inputs.Add(everyChar.ToString());
            inputs.Add("{\"nested\": [1, 2, {\"a\": null}], \"x\": \"y\"}");
            inputs.Add("C:\\Users\\name\\file.json");
            inputs.Add(new string('\\', 7) + new string('"', 7));
            System.Random rng = new System.Random(12345);          // a fixed seed: the same inputs on every run
            for (int n = 0; n < 200; n++)
            {
                StringBuilder sb = new StringBuilder();
                int length = rng.Next(0, 40);
                for (int i = 0; i < length; i++)
                {
                    int pick = rng.Next(0, 10);
                    if (pick < 4) sb.Append((char)rng.Next(0, 0x80));
                    else if (pick < 6) sb.Append((char)rng.Next(0x80, 0x800));
                    else if (pick < 8) sb.Append((char)rng.Next(0x800, 0xD800));
                    else if (pick < 9) sb.Append(char.ConvertFromUtf32(rng.Next(0x10000, 0x10FFFF)));
                    else sb.Append((char)rng.Next(0xE000, 0x10000));
                }
                inputs.Add(sb.ToString());
            }
            foreach (string s in inputs)
            {
                string json = UGALog.FormatJson(2.5, "round\ttrip", "key\"" + s.Length, s);
                Assert.IsNull(StrictJson.Check(json), json);
                // decode the "s" value with the independent decoder and compare
                int at = json.IndexOf("\":\"", json.IndexOf("\"key", StringComparison.Ordinal), StringComparison.Ordinal) + 2;
                string decoded = StrictJson.ReadString(json, ref at);
                Assert.AreEqual(s, decoded);
            }
        }

        [Test]
        public void LongStrings_AreCut_AndTheCutIsMarked()
        {
            string exact = new string('x', UGALog.MaxStringChars);
            Assert.AreEqual(J("{'t':1.000,'evt':'e','s':'") + exact + J("'}"), UGALog.FormatJson(1, "e", "s", exact));

            string longer = new string('x', UGALog.MaxStringChars + 904);
            string json = UGALog.FormatJson(1, "e", "s", longer);
            Assert.IsNull(StrictJson.Check(json));
            Assert.AreEqual(J("{'t':1.000,'evt':'e','s':'") + new string('x', UGALog.MaxStringChars) + J("...[+904]'}"), json);

            // never cut a surrogate pair in half
            string pairAtTheCut = new string('x', UGALog.MaxStringChars - 1) + char.ConvertFromUtf32(0x1F600) + "tail";
            string json2 = UGALog.FormatJson(1, "e", "s", pairAtTheCut);
            Assert.IsNull(StrictJson.Check(json2), json2);
        }

        [Test]
        public void KeysAreEscaped_NullKeysAreSkipped_AndReservedKeysAreRenamed()
        {
            Assert.AreEqual(J("{'t':1.000,'evt':'e','a\\'b':1}"), UGALog.FormatJson(1, "e", "a\"b", 1));
            Assert.AreEqual(J("{'t':1.000,'evt':'e','x':2}"), UGALog.FormatJson(1, "e", null, 1, "x", 2));
            Assert.AreEqual(J("{'t':1.000,'evt':'e','_t':5,'_evt':'x','_lvl':'y'}"),
                UGALog.FormatJson(1, "e", "t", 5, "evt", "x", "lvl", "y"));
            Assert.AreEqual(J("{'t':1.000,'evt':'e','':1}"), UGALog.FormatJson(1, "e", "", 1));
            Assert.AreEqual(J("{'t':1.000,'evt':'ev\\'il\\n'}"), UGALog.FormatJson(1, "ev\"il\n"));
        }

        // ---- values -----------------------------------------------------------------------------------------------------

        [Test]
        public void Integers_AreWrittenAsPlainNumbers()
        {
            Assert.AreEqual("42", ValueOf(42));
            Assert.AreEqual("-7", ValueOf(-7));
            Assert.AreEqual("0", ValueOf(0));
            Assert.AreEqual("-2147483648", ValueOf(int.MinValue));
            Assert.AreEqual("2147483647", ValueOf(int.MaxValue));
            Assert.AreEqual("-9223372036854775808", ValueOf(long.MinValue));
            Assert.AreEqual("9223372036854775807", ValueOf(long.MaxValue));
            Assert.AreEqual("18446744073709551615", ValueOf(ulong.MaxValue));
            Assert.AreEqual("4294967295", ValueOf(uint.MaxValue));
            Assert.AreEqual("255", ValueOf((byte)255));
            Assert.AreEqual("-128", ValueOf((sbyte)-128));
            Assert.AreEqual("-32768", ValueOf(short.MinValue));
            Assert.AreEqual("65535", ValueOf(ushort.MaxValue));
            Assert.AreEqual("1234567890123", ValueOf(1234567890123L));
            Assert.AreEqual("1.50", ValueOf(1.50m));
        }

        [Test]
        public void FloatsAndDoubles_AreNumbers_WithAPoint_AndRoundTrip()
        {
            Assert.AreEqual("0.5", ValueOf(0.5f));
            Assert.AreEqual("0.1", ValueOf(0.1f));
            Assert.AreEqual("100", ValueOf(100f));
            Assert.AreEqual("-0.25", ValueOf(-0.25f));
            Assert.AreEqual("0.5", ValueOf(0.5));
            Assert.AreEqual("12.345", ValueOf(12.345));
            foreach (float f in new[] { 0.1f, 1f / 3f, 1e-5f, 123456789f, 3.4028235e38f, 1.17549435E-38f, 16777216f, -42.125f, 0.15f })
            {
                string token = ValueOf(f);
                Assert.IsNull(StrictJson.Check(token), token);
                Assert.AreEqual(f, float.Parse(token, CultureInfo.InvariantCulture), "float " + token);
            }
            foreach (double d in new[] { 0.1, 1.0 / 3.0, 1e-5, 1e21, 123456789012345678.0, double.Epsilon, double.MaxValue, -42.125 })
            {
                string token = ValueOf(d);
                Assert.IsNull(StrictJson.Check(token), token);
                Assert.AreEqual(d, double.Parse(token, CultureInfo.InvariantCulture), "double " + token);
            }
        }

        [Test]
        public void NaNAndInfinity_AreNull_BecauseJsonHasNoSuchNumbers()
        {
            Assert.AreEqual("null", ValueOf(float.NaN));
            Assert.AreEqual("null", ValueOf(float.PositiveInfinity));
            Assert.AreEqual("null", ValueOf(float.NegativeInfinity));
            Assert.AreEqual("null", ValueOf(double.NaN));
            Assert.AreEqual("null", ValueOf(double.PositiveInfinity));
            Assert.AreEqual("null", ValueOf(double.NegativeInfinity));
        }

        [Test]
        public void BooleansNullsCharsAndEnums()
        {
            Assert.AreEqual("true", ValueOf(true));
            Assert.AreEqual("false", ValueOf(false));
            Assert.AreEqual("null", ValueOf(null));
            Assert.AreEqual(J("'x'"), ValueOf('x'));
            Assert.AreEqual(J("'\\''"), ValueOf('"'));
            Assert.AreEqual(J("'Aligned'"), ValueOf(Mode.Aligned));
            Assert.AreEqual(J("'HeadLocked'"), ValueOf(Mode.HeadLocked));
            Assert.AreEqual(J("'Warning'"), ValueOf(LogType.Warning));
        }

        [Test]
        public void UnityVectors_AreNumberArrays()
        {
            Assert.AreEqual("[1,2.5,-3]", ValueOf(new Vector3(1f, 2.5f, -3f)));
            Assert.AreEqual("[0.5,-1]", ValueOf(new Vector2(0.5f, -1f)));
            Assert.AreEqual("[0,0,0,1]", ValueOf(Quaternion.identity));
            Assert.AreEqual("[1,2,3,4]", ValueOf(new Vector4(1f, 2f, 3f, 4f)));
            Assert.AreEqual("[null,0,0]", ValueOf(new Vector3(float.NaN, 0f, 0f)));
        }

        [Test]
        public void ArraysAndLists_AreJsonArrays_WithLimits()
        {
            Assert.AreEqual("[1,2,3]", ValueOf(new[] { 1, 2, 3 }));
            Assert.AreEqual("[]", ValueOf(new int[0]));
            Assert.AreEqual(J("['a','b\\'c']"), ValueOf(new List<string> { "a", "b\"c" }));
            Assert.AreEqual(J("[1,'two',null,true,[3,4]]"), ValueOf(new object[] { 1, "two", null, true, new[] { 3, 4 } }));
            Assert.AreEqual(J("[0.5,0.75]"), ValueOf(new[] { 0.5f, 0.75f }));

            int[] many = new int[100];
            for (int i = 0; i < many.Length; i++) many[i] = i;
            string text = ValueOf(many);
            Assert.IsNull(StrictJson.Check(text));
            StringAssert.StartsWith("[0,1,2,", text);
            StringAssert.EndsWith(J(",63,'...']"), text, "at most 64 items are written");

            object deep = new object[] { new object[] { new object[] { new object[] { 1 } } } };
            string deepText = ValueOf(deep);
            Assert.IsNull(StrictJson.Check(deepText), deepText);
            StringAssert.Contains(J("'...'"), deepText, "nesting stops after 3 levels");
        }

        [Test]
        public void ExceptionsAndOtherObjects_AreWrittenAsStrings()
        {
            Assert.AreEqual(J("'InvalidOperationException: boom'"), ValueOf(new InvalidOperationException("boom")));
            Assert.AreEqual(J("'custom text'"), ValueOf(new CustomToString("custom text")));
            Assert.AreEqual(J("'<ToString failed: InvalidOperationException>'"), ValueOf(new ThrowingToString()));
            Assert.AreEqual(J("'2026-11-14'"), ValueOf(new CustomToString("2026-11-14")));
        }

        sealed class CustomToString
        {
            readonly string _text;
            public CustomToString(string text) { _text = text; }
            public override string ToString() { return _text; }
        }

        sealed class ThrowingToString
        {
            public override string ToString() { throw new InvalidOperationException("no string for you"); }
        }

        sealed class LogsFromToString
        {
            public override string ToString()
            {
                // a value that logs while its own line is being built: must not corrupt the outer line
                UGALog.Event("inner", "from", "tostring");
                return "outer value";
            }
        }

        [Test]
        public void AValueThatLogsFromItsOwnToString_DoesNotCorruptTheLine()
        {
            using (LogCapture capture = new LogCapture())
            {
                UGALog.Event("outer", "a", 1, "b", new LogsFromToString(), "c", 3);
                Assert.AreEqual(2, capture.Lines.Count);
                string inner = JsonOf(capture.Lines[0].Value);
                string outer = JsonOf(capture.Lines[1].Value);
                StringAssert.EndsWith(J(",'evt':'inner','from':'tostring'}"), inner);
                StringAssert.EndsWith(J(",'evt':'outer','a':1,'b':'outer value','c':3}"), outer);
            }
        }

        // The value part of {"t":0.000,"evt":"v","k":<value>}
        static string ValueOf(object value)
        {
            string json = UGALog.FormatJson(0, "v", "k", value);
            Assert.IsNull(StrictJson.Check(json), json);
            string head = J("{'t':0.000,'evt':'v','k':");
            StringAssert.StartsWith(head, json);
            return json.Substring(head.Length, json.Length - head.Length - 1);
        }

        // ---- culture ---------------------------------------------------------------------------------------------------

        [Test]
        public void Output_DoesNotDependOnTheCurrentCulture()
        {
            object[] values = { 1.5f, 1.5, 1234567.891, -0.001f, -42, 1234567L, 0.1m, new Vector3(1.5f, -2.25f, 1000.5f), new[] { 1.5f, 2.5f } };
            string[] expected = new string[values.Length];
            for (int i = 0; i < values.Length; i++) expected[i] = UGALog.FormatJson(1234.5678, "culture", "v", values[i]);

            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                foreach (string name in new[] { "de-DE", "fr-FR", "sv-SE", "tr-TR", "ru-RU" })
                {
                    CultureInfo culture;
                    try { culture = new CultureInfo(name); }
                    catch (ArgumentException) { continue; }          // a culture this runtime does not know: skip it
                    Thread.CurrentThread.CurrentCulture = culture;
                    for (int i = 0; i < values.Length; i++)
                    {
                        string actual = UGALog.FormatJson(1234.5678, "culture", "v", values[i]);
                        Assert.AreEqual(expected[i], actual, "culture " + name);
                        Assert.IsNull(StrictJson.Check(actual), name + ": " + actual);
                    }
                    StringAssert.Contains(J("'t':1234.568"), UGALog.FormatJson(1234.5678, "x"), name);
                }
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
            Assert.AreEqual(saved, Thread.CurrentThread.CurrentCulture, "the test must restore the culture it changed");
        }

        [Test]
        public void AGermanCulture_WouldHaveBrokenNaiveFormatting_SoThisTestIsMeaningful()
        {
            // the claim above only means something if the culture really changes number formatting on this runtime
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                CultureInfo german;
                try { german = new CultureInfo("de-DE"); }
                catch (ArgumentException) { Assert.Inconclusive("de-DE is not available on this runtime"); return; }
                Thread.CurrentThread.CurrentCulture = german;
                Assert.AreEqual("1,5", (1.5).ToString(), "if this fails the culture tests above prove nothing");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        // ---- threads ---------------------------------------------------------------------------------------------------

        [Test]
        public void ManyThreads_ProduceOnlyValidLines_NothingLostOrMixedUp()
        {
            const int threadCount = 8;
            const int perThread = 250;
            using (LogCapture capture = new LogCapture())
            {
                Thread[] threads = new Thread[threadCount];
                for (int t = 0; t < threadCount; t++)
                {
                    int id = t;
                    threads[t] = new Thread(delegate()
                    {
                        for (int i = 0; i < perThread; i++)
                            UGALog.Event("burst", "thread", id, "seq", i, "text", "quote\"back\\slash\nnewline " + id + "/" + i);
                    });
                    threads[t].Start();
                }
                foreach (Thread t in threads) Assert.IsTrue(t.Join(30000), "a logging thread did not finish");

                string[] lines = capture.Texts();
                Assert.AreEqual(threadCount * perThread, lines.Length);
                bool[,] seen = new bool[threadCount, perThread];
                Regex pattern = new Regex("^\\{\"t\":\\d+\\.\\d{3},\"evt\":\"burst\",\"thread\":(\\d+),\"seq\":(\\d+),\"text\":(\".*\")\\}$");
                foreach (string line in lines)
                {
                    string json = JsonOf(line);
                    Match m = pattern.Match(json);
                    Assert.IsTrue(m.Success, "unexpected line: " + json);
                    int thread = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    int seq = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    Assert.IsFalse(seen[thread, seq], "line logged twice: " + json);
                    seen[thread, seq] = true;
                    int at = 0;
                    string text = StrictJson.ReadString(m.Groups[3].Value, ref at);
                    Assert.AreEqual("quote\"back\\slash\nnewline " + thread + "/" + seq, text, "another thread's text leaked into this line");
                }
                for (int t = 0; t < threadCount; t++)
                    for (int i = 0; i < perThread; i++)
                        Assert.IsTrue(seen[t, i], "missing line " + t + "/" + i);
            }
        }

        // ---- stack traces (Application.SetStackTraceLogType) ------------------------------------------------------------

        [Test]
        public void Configure_TurnsStackTracesOffForPlainEvents()
        {
            StackTraceLogType before = Application.GetStackTraceLogType(LogType.Log);
            string seenText = null;
            string seenStack = null;
            Application.LogCallback callback = delegate(string condition, string stackTrace, LogType type)
            {
                if (type == LogType.Log && condition.IndexOf("\"evt\":\"stack_probe\"", StringComparison.Ordinal) >= 0)
                {
                    seenText = condition;
                    seenStack = stackTrace;
                }
            };
            Application.logMessageReceived += callback;
            try
            {
                Assert.IsNull(UGALog.Output, "this test sends the line through the real Debug.Log route");
                UGALog.Configure();
                Assert.AreEqual(StackTraceLogType.None, Application.GetStackTraceLogType(LogType.Log));
                UGALog.Event("stack_probe", "n", 1);
            }
            finally
            {
                Application.logMessageReceived -= callback;
                Application.SetStackTraceLogType(LogType.Log, before);
            }
            Assert.IsNotNull(seenText, "the event did not reach Unity's log");
            Assert.IsNull(StrictJson.Check(seenText.Substring(UGALog.Prefix.Length)));
            // Informational: with stack traces off the callback should see an empty stack trace.
            Debug.Log("UGALog stack-trace probe: stackTrace length seen by logMessageReceived = " + (seenStack == null ? -1 : seenStack.Length));
        }
    }

    // ================================================================================================================
    /// <summary>A TextWriter whose writes wait for the test to open a gate: a disk that has stalled.</summary>
    internal sealed class GateWriter : TextWriter
    {
        public readonly ManualResetEventSlim Gate = new ManualResetEventSlim(false);
        readonly StringBuilder _text = new StringBuilder();

        public override Encoding Encoding { get { return Encoding.UTF8; } }

        void WaitForGate()
        {
            if (!Gate.Wait(30000)) throw new TimeoutException("the test never opened the gate");
        }

        public override void Write(char value)
        {
            WaitForGate();
            lock (_text) _text.Append(value);
        }

        public override void Write(string value)
        {
            WaitForGate();
            lock (_text) _text.Append(value);
        }

        public string Snapshot()
        {
            lock (_text) return _text.ToString();
        }
    }

    internal sealed class FailingWriter : TextWriter
    {
        public override Encoding Encoding { get { return Encoding.UTF8; } }
        public override void Write(char value) { throw new IOException("disk full"); }
        public override void Write(string value) { throw new IOException("disk full"); }
    }

    public class SessionRecorderTests
    {
        static string J(string s) { return s.Replace('\'', '"'); }

        string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "uga-sessions-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            SessionRecorder.Stop();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        // ---- SessionLineWriter ------------------------------------------------------------------------------------------

        [Test]
        public void LineWriter_WritesEveryAcceptedLine()
        {
            StringWriter sink = new StringWriter();
            SessionLineWriter writer = new SessionLineWriter(sink, 100);
            Assert.IsTrue(writer.TryWrite("one"));
            Assert.IsTrue(writer.TryWrite("two"));
            Assert.IsTrue(writer.TryWrite("three"));
            Assert.IsTrue(writer.Close(30000));
            Assert.AreEqual("one\ntwo\nthree\n", sink.ToString());
            Assert.AreEqual(3, writer.Accepted);
            Assert.AreEqual(3, writer.Written);
            Assert.AreEqual(0, writer.Dropped);
            Assert.AreEqual(0, writer.Pending);
            Assert.IsNull(writer.Error);
        }

        [Test]
        public void LineWriter_FullQueue_DropsAndCountsTheLine_AndNeverBlocks()
        {
            GateWriter stalledDisk = new GateWriter();
            SessionLineWriter writer = new SessionLineWriter(stalledDisk, 3);

            int accepted = 0;
            for (int i = 0; i < 10; i++)                       // would hang here if TryWrite waited for the stalled disk
                if (writer.TryWrite("line " + i)) accepted++;

            Assert.AreEqual(3, accepted, "capacity 3 = three lines outstanding");
            Assert.AreEqual(3, writer.Accepted);
            Assert.AreEqual(7, writer.Dropped);
            Assert.AreEqual(3, writer.Pending);

            stalledDisk.Gate.Set();                            // the disk wakes up
            Assert.IsTrue(writer.Close(30000));
            Assert.AreEqual("line 0\nline 1\nline 2\n", stalledDisk.Snapshot(), "the lines that were accepted are all written, in order");
            Assert.AreEqual(3, writer.Written);
            Assert.AreEqual(7, writer.Dropped);
        }

        [Test]
        public void LineWriter_AcceptsAgainOnceTheBacklogIsWritten()
        {
            GateWriter disk = new GateWriter();
            SessionLineWriter writer = new SessionLineWriter(disk, 2);
            Assert.IsTrue(writer.TryWrite("a"));
            Assert.IsTrue(writer.TryWrite("b"));
            Assert.IsFalse(writer.TryWrite("c"));
            disk.Gate.Set();
            // wait (bounded, by polling the counter the writer itself maintains) until the backlog is gone
            DateTime limit = DateTime.UtcNow.AddSeconds(30);
            while (writer.Pending > 0 && DateTime.UtcNow < limit) Thread.Yield();
            Assert.AreEqual(0, writer.Pending);
            Assert.IsTrue(writer.TryWrite("d"));
            Assert.IsTrue(writer.Close(30000));
            Assert.AreEqual("a\nb\nd\n", disk.Snapshot());
        }

        [Test]
        public void LineWriter_ManyThreads_NothingLostOrDuplicated_AndEachThreadsOrderKept()
        {
            const int threadCount = 4;
            const int perThread = 1000;
            StringWriter sink = new StringWriter();            // only the writer thread touches it
            SessionLineWriter writer = new SessionLineWriter(sink, 100000);
            Thread[] threads = new Thread[threadCount];
            for (int t = 0; t < threadCount; t++)
            {
                int id = t;
                threads[t] = new Thread(delegate()
                {
                    for (int i = 0; i < perThread; i++) writer.TryWrite("t" + id + "-" + i);
                });
                threads[t].Start();
            }
            foreach (Thread t in threads) Assert.IsTrue(t.Join(30000));
            Assert.IsTrue(writer.Close(30000));

            string[] lines = sink.ToString().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Assert.AreEqual(threadCount * perThread, lines.Length);
            Assert.AreEqual(0, writer.Dropped);
            int[] next = new int[threadCount];
            foreach (string line in lines)
            {
                Match m = Regex.Match(line, "^t(\\d+)-(\\d+)$");
                Assert.IsTrue(m.Success, line);
                int thread = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                int i = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                Assert.AreEqual(next[thread], i, "lines of one thread must stay in order");
                next[thread]++;
            }
            for (int t = 0; t < threadCount; t++) Assert.AreEqual(perThread, next[t]);
        }

        [Test]
        public void LineWriter_NewlinesInsideALine_BecomeSpaces_SoOneCallIsOneLine()
        {
            StringWriter sink = new StringWriter();
            SessionLineWriter writer = new SessionLineWriter(sink, 10);
            Assert.IsTrue(writer.TryWrite("a\nb\r\nc"));
            Assert.IsTrue(writer.Close(30000));
            Assert.AreEqual("a b  c\n", sink.ToString());
        }

        [Test]
        public void LineWriter_AfterClose_RefusesLines_AndCloseIsRepeatable()
        {
            StringWriter sink = new StringWriter();
            SessionLineWriter writer = new SessionLineWriter(sink, 10);
            Assert.IsTrue(writer.TryWrite("first"));
            Assert.IsTrue(writer.Close(30000));
            Assert.IsFalse(writer.TryWrite("late"));
            Assert.IsTrue(writer.Close(30000));
            Assert.AreEqual("first\n", sink.ToString());
            Assert.IsFalse(writer.TryWrite(null));
        }

        [Test]
        public void LineWriter_ADiskError_StopsTheWriting_ItDoesNotThrowIntoAnyone()
        {
            SessionLineWriter writer = new SessionLineWriter(new FailingWriter(), 10);
            Assert.IsTrue(writer.TryWrite("this one will fail"));
            Assert.IsTrue(writer.Close(30000));
            Assert.IsNotNull(writer.Error);
            StringAssert.Contains("disk full", writer.Error);
            Assert.AreEqual(0, writer.Written);
            Assert.AreEqual(1, writer.Dropped, "the line that could not be written is counted as dropped");
        }

        // ---- SessionRecorder --------------------------------------------------------------------------------------------

        [Test]
        public void Recorder_StartWriteStop_LeavesAValidJsonlFile()
        {
            Assert.IsFalse(SessionRecorder.IsRecording);
            Assert.IsTrue(SessionRecorder.Start(_dir));
            Assert.IsTrue(SessionRecorder.IsRecording);
            string path = SessionRecorder.CurrentPath;
            Assert.AreEqual(_dir, Path.GetDirectoryName(path));
            Assert.IsTrue(Regex.IsMatch(Path.GetFileName(path), "^session-\\d{8}-\\d{6}(-\\d+)?\\.jsonl$"), Path.GetFileName(path));

            Assert.IsTrue(SessionRecorder.Write(UGALog.FormatJson(1.25, "onset", "ms", 12.5)));
            Assert.IsTrue(SessionRecorder.Record("pose", "yaw", 10f));
            Assert.IsTrue(SessionRecorder.Record("pair", "a", 1, "b", "two"));
            SessionRecorder.Stop();
            Assert.IsFalse(SessionRecorder.IsRecording);
            Assert.AreEqual(path, SessionRecorder.CurrentPath, "the path of the last session stays readable after Stop()");

            string[] lines = File.ReadAllLines(path);
            Assert.AreEqual(5, lines.Length, string.Join("\n", lines));   // session_start + 3 lines + session_end
            foreach (string line in lines)
            {
                Assert.IsNull(StrictJson.Check(line), line);
                Assert.AreEqual('{', line[0], "a session file holds bare JSON objects, no [UGA] prefix");
            }
            StringAssert.Contains(J("'evt':'session_start'"), lines[0]);
            StringAssert.Contains(J("'evt':'onset','ms':12.5"), lines[1]);
            StringAssert.Contains(J("'evt':'pose','yaw':10"), lines[2]);
            StringAssert.Contains(J("'evt':'pair','a':1,'b':'two'"), lines[3]);
            StringAssert.Contains(J("'evt':'session_end','accepted':4,'dropped':0"), lines[4]);
            Assert.AreEqual(0, SessionRecorder.DroppedLines);
            Assert.AreEqual(5, SessionRecorder.WrittenLines);
        }

        [Test]
        public void Recorder_WhenNotRecording_WriteAndRecordDoNothing()
        {
            Assert.IsFalse(SessionRecorder.IsRecording);
            Assert.IsFalse(SessionRecorder.Write("{\"x\":1}"));
            Assert.IsFalse(SessionRecorder.Record("x"));
            Assert.IsFalse(SessionRecorder.Record("x", "a", 1));
            Assert.IsFalse(SessionRecorder.Record("x", "a", 1, "b", 2));
            Assert.IsFalse(SessionRecorder.Record("x", "a", 1, "b", 2, "c", 3));
            Assert.DoesNotThrow(delegate { SessionRecorder.Stop(); });
        }

        [Test]
        public void Recorder_StartTwice_IsANoOp_AndALaterSessionGetsItsOwnFile()
        {
            Assert.IsTrue(SessionRecorder.Start(_dir));
            string first = SessionRecorder.CurrentPath;
            Assert.IsTrue(SessionRecorder.Start(_dir), "starting while recording is harmless");
            Assert.AreEqual(first, SessionRecorder.CurrentPath);
            SessionRecorder.Stop();

            Assert.IsTrue(SessionRecorder.Start(_dir));
            string second = SessionRecorder.CurrentPath;
            SessionRecorder.Stop();
            Assert.AreNotEqual(first, second, "even inside the same second the new session must not overwrite the old one");
            Assert.IsTrue(File.Exists(first));
            Assert.IsTrue(File.Exists(second));
        }

        [Test]
        public void Recorder_TeesEveryUGALogLine_WithoutThePrefix()
        {
            using (LogCapture capture = new LogCapture())
            {
                Assert.IsTrue(SessionRecorder.Start(_dir));
                UGALog.Event("hello", "n", 3, "s", "a\"b");
                UGALog.Warn("careful", "watch out");
                UGALog.Error("broken", "oops");
                SessionRecorder.Stop();

                Assert.AreEqual(3, capture.Lines.Count, "the console still gets every line");
                StringAssert.StartsWith(UGALog.Prefix, capture.Lines[0].Value);
            }
            string[] lines = File.ReadAllLines(SessionRecorder.CurrentPath);
            Assert.AreEqual(1 + 3 + 1, lines.Length, string.Join("\n", lines));
            foreach (string line in lines) Assert.IsNull(StrictJson.Check(line), line);
            StringAssert.EndsWith(J(",'evt':'hello','n':3,'s':'a\\'b'}"), lines[1]);
            StringAssert.EndsWith(J(",'evt':'careful','lvl':'warn','msg':'watch out'}"), lines[2]);
            StringAssert.EndsWith(J(",'evt':'broken','lvl':'error','msg':'oops'}"), lines[3]);
        }

        [Test]
        public void Recorder_ManyThreadsLogging_EveryLineInTheFileIsValid_AndNoneIsLost()
        {
            const int threadCount = 4;
            const int perThread = 500;
            using (LogCapture capture = new LogCapture())
            {
                Assert.IsTrue(SessionRecorder.Start(_dir));
                Thread[] threads = new Thread[threadCount];
                for (int t = 0; t < threadCount; t++)
                {
                    int id = t;
                    threads[t] = new Thread(delegate()
                    {
                        for (int i = 0; i < perThread; i++) UGALog.Event("burst", "thread", id, "i", i);
                    });
                    threads[t].Start();
                }
                foreach (Thread t in threads) Assert.IsTrue(t.Join(30000));
                SessionRecorder.Stop();
            }
            Assert.AreEqual(0, SessionRecorder.DroppedLines);

            string[] lines = File.ReadAllLines(SessionRecorder.CurrentPath);
            bool[,] seen = new bool[threadCount, perThread];
            int burst = 0;
            foreach (string line in lines)
            {
                Assert.IsNull(StrictJson.Check(line), line);
                Match m = Regex.Match(line, "\"evt\":\"burst\",\"thread\":(\\d+),\"i\":(\\d+)\\}$");
                if (!m.Success) continue;
                int thread = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                int i = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                Assert.IsFalse(seen[thread, i], line);
                seen[thread, i] = true;
                burst++;
            }
            Assert.AreEqual(threadCount * perThread, burst);
        }

        [Test]
        public void Recorder_AnUnusableFolder_ReturnsFalse_WithAWarning_AndDoesNotThrow()
        {
            string blocker = Path.Combine(_dir, "blocker");
            File.WriteAllText(blocker, "a file where a folder is needed");
            using (LogCapture capture = new LogCapture())
            {
                bool started = true;
                Assert.DoesNotThrow(delegate { started = SessionRecorder.Start(Path.Combine(blocker, "sessions")); });
                Assert.IsFalse(started);
                Assert.IsFalse(SessionRecorder.IsRecording);
                Assert.AreEqual(1, capture.Lines.Count);
                Assert.AreEqual(LogType.Warning, capture.Lines[0].Key);
                StringAssert.Contains("session_start_failed", capture.Lines[0].Value);
            }
        }
    }
}
