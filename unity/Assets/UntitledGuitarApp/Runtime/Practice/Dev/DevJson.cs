// TEMPORARY (dev play-along driver; delete with the rest of Runtime/Practice/Dev when Core's song model lands).
//
// A small, strict JSON reader for the song files (and, in tests, the pose files). Why not UnityEngine.JsonUtility:
//  * JsonUtility is native engine code, so anything built on it can only be tested inside Unity; this is plain C#, so the
//    timeline and its tests run offline (mono-tests.sh) as well as in the editor.
//  * JsonUtility cannot tell a missing key from 0 / null: song files leave out "at" on chords-only lines (it must become
//    -1, "no words") and "pickupBeats" on most songs. Here a missing key is visible as missing.
// Values come back as Dictionary<string, object> (objects), List<object> (arrays), string, double, bool or null.
// Load-time only: it allocates freely and must never run per frame.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UntitledGuitarApp.Runtime.Practice.Dev
{
    /// <summary>TEMPORARY. Strict JSON text to plain .NET values (objects, lists, strings, doubles, bools, null).</summary>
    public static class DevJson
    {
        /// <summary>Most nesting levels accepted (song files use 6).</summary>
        public const int MaxDepth = 64;

        /// <summary>Parses <paramref name="text"/>. False with a readable <paramref name="error"/> (and a character index) on bad JSON.</summary>
        public static bool TryParse(string text, out object value, out string error)
        {
            value = null;
            error = null;
            if (text == null)
            {
                error = "no text";
                return false;
            }
            var parser = new Parser(text);
            try
            {
                parser.SkipSpace();
                value = parser.ReadValue(0);
                parser.SkipSpace();
                if (!parser.AtEnd) throw parser.Fail("unexpected text after the value");
                return true;
            }
            catch (FormatException e)
            {
                value = null;
                error = e.Message;
                return false;
            }
        }

        // ------------------------------------------------------------------ accessors (null-safe)

        /// <summary>The object stored under <paramref name="key"/> of <paramref name="node"/> (an object).</summary>
        public static bool TryGetObject(object node, string key, out Dictionary<string, object> result)
        {
            result = Get(node, key) as Dictionary<string, object>;
            return result != null;
        }

        /// <summary>The array stored under <paramref name="key"/>.</summary>
        public static bool TryGetArray(object node, string key, out List<object> result)
        {
            result = Get(node, key) as List<object>;
            return result != null;
        }

        /// <summary>The number stored under <paramref name="key"/>.</summary>
        public static bool TryGetNumber(object node, string key, out double result)
        {
            object v = Get(node, key);
            if (v is double)
            {
                result = (double)v;
                return true;
            }
            result = 0;
            return false;
        }

        /// <summary>The string stored under <paramref name="key"/>.</summary>
        public static bool TryGetString(object node, string key, out string result)
        {
            result = Get(node, key) as string;
            return result != null;
        }

        /// <summary>True when <paramref name="node"/> is an object that has <paramref name="key"/> (even with a null value).</summary>
        public static bool Has(object node, string key)
        {
            var obj = node as Dictionary<string, object>;
            return obj != null && key != null && obj.ContainsKey(key);
        }

        static object Get(object node, string key)
        {
            var obj = node as Dictionary<string, object>;
            object v;
            if (obj == null || key == null || !obj.TryGetValue(key, out v)) return null;
            return v;
        }

        // ------------------------------------------------------------------ the parser

        sealed class Parser
        {
            readonly string _s;
            int _i;

            public Parser(string s)
            {
                _s = s;
            }

            public bool AtEnd { get { return _i >= _s.Length; } }

            public FormatException Fail(string what)
            {
                return new FormatException(what + " (at character " + _i.ToString(CultureInfo.InvariantCulture) + ")");
            }

            public void SkipSpace()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '﻿') _i++;
                    else break;
                }
            }

            public object ReadValue(int depth)
            {
                if (depth > MaxDepth) throw Fail("nested too deeply");
                if (AtEnd) throw Fail("unexpected end of the text");
                char c = _s[_i];
                switch (c)
                {
                    case '{': return ReadObject(depth);
                    case '[': return ReadArray(depth);
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                }
                if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                throw Fail("unexpected character '" + c + "'");
            }

            void Expect(string word)
            {
                if (_i + word.Length > _s.Length || string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0)
                    throw Fail("expected " + word);
                _i += word.Length;
            }

            Dictionary<string, object> ReadObject(int depth)
            {
                var obj = new Dictionary<string, object>(StringComparer.Ordinal);
                _i++;   // {
                SkipSpace();
                if (!AtEnd && _s[_i] == '}')
                {
                    _i++;
                    return obj;
                }
                while (true)
                {
                    SkipSpace();
                    if (AtEnd || _s[_i] != '"') throw Fail("expected a key in quotes");
                    string key = ReadString();
                    SkipSpace();
                    if (AtEnd || _s[_i] != ':') throw Fail("expected ':' after the key \"" + key + "\"");
                    _i++;
                    SkipSpace();
                    obj[key] = ReadValue(depth + 1);
                    SkipSpace();
                    if (AtEnd) throw Fail("unexpected end inside an object");
                    char c = _s[_i++];
                    if (c == ',') continue;
                    if (c == '}') return obj;
                    _i--;
                    throw Fail("expected ',' or '}'");
                }
            }

            List<object> ReadArray(int depth)
            {
                var list = new List<object>();
                _i++;   // [
                SkipSpace();
                if (!AtEnd && _s[_i] == ']')
                {
                    _i++;
                    return list;
                }
                while (true)
                {
                    SkipSpace();
                    list.Add(ReadValue(depth + 1));
                    SkipSpace();
                    if (AtEnd) throw Fail("unexpected end inside an array");
                    char c = _s[_i++];
                    if (c == ',') continue;
                    if (c == ']') return list;
                    _i--;
                    throw Fail("expected ',' or ']'");
                }
            }

            string ReadString()
            {
                _i++;   // opening quote
                StringBuilder sb = null;
                int runStart = _i;
                while (true)
                {
                    if (AtEnd) throw Fail("unterminated string");
                    char c = _s[_i];
                    if (c == '"')
                    {
                        string tail = _s.Substring(runStart, _i - runStart);
                        _i++;
                        if (sb == null) return tail;
                        sb.Append(tail);
                        return sb.ToString();
                    }
                    if (c < ' ') throw Fail("control character inside a string");
                    if (c != '\\')
                    {
                        _i++;
                        continue;
                    }
                    if (sb == null) sb = new StringBuilder();
                    sb.Append(_s, runStart, _i - runStart);
                    _i++;
                    if (AtEnd) throw Fail("unterminated escape");
                    char e = _s[_i++];
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
                            if (_i + 4 > _s.Length) throw Fail("short \\u escape");
                            int code;
                            if (!int.TryParse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                throw Fail("bad \\u escape");
                            sb.Append((char)code);
                            _i += 4;
                            break;
                        default:
                            throw Fail("unknown escape \\" + e);
                    }
                    runStart = _i;
                }
            }

            double ReadNumber()
            {
                int start = _i;
                if (_s[_i] == '-') _i++;
                while (!AtEnd)
                {
                    char c = _s[_i];
                    if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') _i++;
                    else break;
                }
                double v;
                string token = _s.Substring(start, _i - start);
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out v) || double.IsNaN(v) || double.IsInfinity(v))
                    throw Fail("bad number '" + token + "'");
                return v;
            }
        }
    }
}
