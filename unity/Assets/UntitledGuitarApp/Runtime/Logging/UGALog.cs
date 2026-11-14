// UGALog.cs: one structured log line per call, safe to call from any thread.
//
//     [UGA] {"t":12.345,"evt":"name","key":value,"other":"text"}
//
//   t    seconds since start-up, 3 decimals (a monotonic clock that restarts when a play session starts)
//   evt  the event name you passed
//   lvl  only on Warn ("warn") and Error ("error"); Warn/Error carry their text in "msg"
//   ...  your key/value pairs, in the order you passed them. A key named t, evt or lvl is renamed _t, _evt, _lvl.
//
// Guarantees (tests: Tests/EditMode/UGALogTests.cs):
//   * The text after "[UGA] " is always ONE line of valid JSON: strings are escaped (quotes, backslashes, control characters,
//     U+0085/U+2028/U+2029, unpaired surrogates), numbers and booleans are not quoted, null is null.
//   * Numbers are formatted with the invariant culture whatever the machine's culture is (a German or French Mac must not
//     log 1,5). NaN and infinities are not legal JSON numbers and are written as null.
//   * Values: string, char, bool, every integer type, float, double, decimal, enums (as their name), Vector2/3/4 and
//     Quaternion (as number arrays), arrays and lists (as JSON arrays, at most 64 items, 3 levels deep) and exceptions
//     ("Type: message"). Anything else is written as a string using its invariant ToString(). A value whose ToString()
//     throws is replaced by a marker; a logger never throws into its caller.
//   * Strings longer than MaxStringChars are cut and end in "...[+N]".
//
// Threading: every method may be called from any thread. The line is built in a [ThreadStatic] StringBuilder (taken out of
// its slot while in use, so a ToString() that logs again just allocates its own builder), then handed to Debug.Log, which
// Unity allows from any thread (the managed binding DebugLogHandler.Internal_Log carries ThreadAndSerializationSafe;
// Application.logMessageReceivedThreaded is documented to fire for messages from any thread:
// https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Application-logMessageReceivedThreaded.html).
// The only Unity calls that are main-thread only are in Configure(), which is why it is not called lazily from here.
//
// While SessionRecorder is recording, every line is also appended (without the "[UGA] " prefix) to the session file, so a
// session recording contains every event the other systems log.
using System;
using System.Collections;
using System.Globalization;
using System.Text;
using System.Threading;
using UnityEngine;

namespace UntitledGuitarApp.Runtime.Logging
{
    public static class UGALog
    {
        /// <summary>Every line starts with this; scripts/logs.sh greps for it.</summary>
        public const string Prefix = "[UGA] ";

        /// <summary>Longer strings are cut to this many UTF-16 units and end in "...[+N]".</summary>
        public const int MaxStringChars = 4096;

        const int MaxItems = 64;               // array/list items written per value
        const int MaxDepth = 3;                // nesting of arrays inside arrays
        const int MaxBuilderCapacity = 16 * 1024;

        static long s_startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        static volatile Action<LogType, string> s_output;
        [ThreadStatic] static StringBuilder t_builder;

        // ---- the contract (SeamStubs.cs) -------------------------------------------------------------------------------

        public static void Event(string evt)
        {
            Emit(LogType.Log, null, evt, null, null, null, null, null, null);
        }

        public static void Event(string evt, string k1, object v1)
        {
            Emit(LogType.Log, null, evt, k1, v1, null, null, null, null);
        }

        public static void Event(string evt, string k1, object v1, string k2, object v2)
        {
            Emit(LogType.Log, null, evt, k1, v1, k2, v2, null, null);
        }

        public static void Event(string evt, string k1, object v1, string k2, object v2, string k3, object v3)
        {
            Emit(LogType.Log, null, evt, k1, v1, k2, v2, k3, v3);
        }

        public static void Warn(string evt, string message)
        {
            Emit(LogType.Warning, "warn", evt, "msg", message, null, null, null, null);
        }

        public static void Error(string evt, string message)
        {
            Emit(LogType.Error, "error", evt, "msg", message, null, null, null, null);
        }

        // ---- additions beyond the contract -----------------------------------------------------------------------------

        /// <summary>Seconds since start-up: the clock behind "t". Monotonic, readable from any thread.</summary>
        public static double Now
        {
            get
            {
                long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - Volatile.Read(ref s_startTimestamp);
                return ticks / (double)System.Diagnostics.Stopwatch.Frequency;
            }
        }

        /// <summary>
        /// Where finished lines go. null (the default) = the Unity console / log file through Debug.Log, Debug.LogWarning and
        /// Debug.LogError. Tests replace it to capture lines without touching the console (Unity's test runner fails a test
        /// that logs an error, so a test that provokes UGALog.Error must divert it here). Called on the logging thread.
        /// </summary>
        public static Action<LogType, string> Output
        {
            get { return s_output; }
            set { s_output = value; }
        }

        /// <summary>
        /// Turns stack traces off for LogType.Log, so each event stays a single line in Editor.log / Player.log (Warning and
        /// Error keep theirs). MAIN THREAD ONLY and idempotent. Runs by itself when a play session or the player starts
        /// (see OnSubsystemRegistration) and from FlagStore.Initialize(); call it yourself only from tools or tests.
        /// API: Application.SetStackTraceLogType(LogType, StackTraceLogType), "Set stack trace logging options. The default
        /// value is StackTraceLogType.ScriptOnly."
        /// https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Application.SetStackTraceLogType.html
        /// The managed binding is not marked thread safe (checked in UnityEngine.CoreModule.dll), so a call from another
        /// thread throws; Configure() swallows that and does nothing. Cheap, so calling it again is harmless.
        /// </summary>
        public static void Configure()
        {
            try
            {
                Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            }
            catch (Exception)
            {
                // wrong thread or engine not ready: nothing to do, the next main-thread call will do it
            }
        }

        /// <summary>
        /// Pure: the full console line for an event, "[UGA] {...}", with an explicit time. No console, no clock, no state, so
        /// tests can compare strings. A pair whose key is null is skipped.
        /// </summary>
        public static string Format(double t, string evt, string k1 = null, object v1 = null,
            string k2 = null, object v2 = null, string k3 = null, object v3 = null)
        {
            return Build(true, t, evt, null, k1, v1, k2, v2, k3, v3);
        }

        /// <summary>Pure: the same line without the "[UGA] " prefix. This is what goes into a .jsonl session file.</summary>
        public static string FormatJson(double t, string evt, string k1 = null, object v1 = null,
            string k2 = null, object v2 = null, string k3 = null, object v3 = null)
        {
            return Build(false, t, evt, null, k1, v1, k2, v2, k3, v3);
        }

        // Runs on the main thread at the start of every play session (also with "Reload Domain" switched off) and when a
        // player starts, before the first scene loads.
        // https://docs.unity3d.com/2021.3/Documentation/ScriptReference/RuntimeInitializeLoadType.html
        // (SubsystemRegistration: "Callback used for registration of subsystems")
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void OnSubsystemRegistration()
        {
            Volatile.Write(ref s_startTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());   // t restarts with the session
            Configure();
        }

        // ---- emitting -----------------------------------------------------------------------------------------------------

        static void Emit(LogType type, string level, string evt,
            string k1, object v1, string k2, object v2, string k3, object v3)
        {
            try
            {
                string line = Build(true, Now, evt, level, k1, v1, k2, v2, k3, v3);
                Write(type, line);
                if (SessionRecorder.IsRecording) SessionRecorder.Write(line.Substring(Prefix.Length));
            }
            catch (Exception)
            {
                // A logger must never throw into the code that is logging.
            }
        }

        static void Write(LogType type, string line)
        {
            Action<LogType, string> output = s_output;
            if (output != null)
            {
                output(type, line);
                return;
            }
            switch (type)
            {
                case LogType.Warning: Debug.LogWarning(line); break;
                case LogType.Error: Debug.LogError(line); break;
                default: Debug.Log(line); break;
            }
        }

        // ---- building the JSON --------------------------------------------------------------------------------------------

        static string Build(bool withPrefix, double t, string evt, string level,
            string k1, object v1, string k2, object v2, string k3, object v3)
        {
            StringBuilder sb = Rent();
            try
            {
                if (withPrefix) sb.Append(Prefix);
                sb.Append("{\"t\":");
                AppendSeconds(sb, t);
                sb.Append(",\"evt\":\"");
                AppendEscaped(sb, evt ?? string.Empty);
                sb.Append('"');
                if (level != null)
                {
                    sb.Append(",\"lvl\":\"").Append(level).Append('"');
                }
                AppendPair(sb, k1, v1);
                AppendPair(sb, k2, v2);
                AppendPair(sb, k3, v3);
                sb.Append('}');
                return sb.ToString();
            }
            finally
            {
                Return(sb);
            }
        }

        static StringBuilder Rent()
        {
            StringBuilder sb = t_builder;
            if (sb == null) return new StringBuilder(256);
            t_builder = null;                  // out of its slot while in use: a nested log call gets its own builder
            return sb;
        }

        static void Return(StringBuilder sb)
        {
            if (sb.Capacity > MaxBuilderCapacity) return;   // do not keep a huge buffer alive on every thread
            sb.Length = 0;
            t_builder = sb;
        }

        static void AppendPair(StringBuilder sb, string key, object value)
        {
            if (key == null) return;
            sb.Append(",\"");
            if (key == "t" || key == "evt" || key == "lvl") sb.Append('_');   // never shadow a reserved key
            AppendEscaped(sb, key);
            sb.Append("\":");
            AppendValue(sb, value, 0);
        }

        static void AppendValue(StringBuilder sb, object v, int depth)
        {
            if (v == null) { sb.Append("null"); return; }
            string s = v as string;
            if (s != null) { AppendQuoted(sb, s); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is int) { AppendInt64(sb, (int)v); return; }
            if (v is float) { AppendSingle(sb, (float)v); return; }
            if (v is double) { AppendDouble(sb, (double)v); return; }
            if (v is long) { AppendInt64(sb, (long)v); return; }
            if (v is short) { AppendInt64(sb, (short)v); return; }
            if (v is byte) { AppendInt64(sb, (byte)v); return; }
            if (v is sbyte) { AppendInt64(sb, (sbyte)v); return; }
            if (v is ushort) { AppendInt64(sb, (ushort)v); return; }
            if (v is uint) { AppendInt64(sb, (uint)v); return; }
            if (v is ulong) { AppendUInt64(sb, (ulong)v); return; }
            if (v is decimal) { sb.Append(((decimal)v).ToString(CultureInfo.InvariantCulture)); return; }
            if (v is char) { AppendQuoted(sb, ((char)v).ToString()); return; }
            if (v is Enum) { AppendQuoted(sb, v.ToString()); return; }
            if (v is Vector3) { Vector3 p = (Vector3)v; AppendTuple(sb, 3, p.x, p.y, p.z, 0f); return; }
            if (v is Vector2) { Vector2 p = (Vector2)v; AppendTuple(sb, 2, p.x, p.y, 0f, 0f); return; }
            if (v is Quaternion) { Quaternion q = (Quaternion)v; AppendTuple(sb, 4, q.x, q.y, q.z, q.w); return; }
            if (v is Vector4) { Vector4 p = (Vector4)v; AppendTuple(sb, 4, p.x, p.y, p.z, p.w); return; }
            Exception ex = v as Exception;
            if (ex != null) { AppendQuoted(sb, ex.GetType().Name + ": " + ex.Message); return; }
            IEnumerable sequence = v as IEnumerable;
            if (sequence != null) { AppendSequence(sb, sequence, depth); return; }

            string text;
            try { text = Convert.ToString(v, CultureInfo.InvariantCulture); }
            catch (Exception e) { text = "<ToString failed: " + e.GetType().Name + ">"; }
            AppendQuoted(sb, text ?? string.Empty);
        }

        static void AppendSequence(StringBuilder sb, IEnumerable sequence, int depth)
        {
            if (depth >= MaxDepth) { sb.Append("\"...\""); return; }
            sb.Append('[');
            int n = 0;
            try
            {
                foreach (object item in sequence)
                {
                    if (n > 0) sb.Append(',');
                    if (n >= MaxItems) { sb.Append("\"...\""); break; }
                    AppendValue(sb, item, depth + 1);
                    n++;
                }
            }
            catch (Exception)
            {
                if (n > 0) sb.Append(',');
                sb.Append("\"<enumeration failed>\"");
            }
            sb.Append(']');
        }

        static void AppendTuple(StringBuilder sb, int count, float a, float b, float c, float d)
        {
            sb.Append('[');
            AppendSingle(sb, a);
            if (count > 1) { sb.Append(','); AppendSingle(sb, b); }
            if (count > 2) { sb.Append(','); AppendSingle(sb, c); }
            if (count > 3) { sb.Append(','); AppendSingle(sb, d); }
            sb.Append(']');
        }

        // "R" is the round-trip format: 0.1f -> 0.1, 1e-5f -> 1E-05, 1/3f -> 0.333333343 (checked under Unity's Mono 6.13;
        // all of these are legal JSON numbers). The invariant culture keeps the decimal point a point.
        static void AppendSingle(StringBuilder sb, float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) { sb.Append("null"); return; }
            sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
        }

        static void AppendDouble(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
            sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        // Integers are written digit by digit: StringBuilder.Append(long) formats with the CURRENT culture (its negative sign
        // and digits may differ), and this allocates nothing.
        static void AppendInt64(StringBuilder sb, long v)
        {
            if (v < 0)
            {
                sb.Append('-');
                AppendUInt64(sb, (ulong)(-(v + 1)) + 1UL);   // safe for long.MinValue
            }
            else
            {
                AppendUInt64(sb, (ulong)v);
            }
        }

        static void AppendUInt64(StringBuilder sb, ulong v)
        {
            if (v == 0) { sb.Append('0'); return; }
            ulong div = 1;
            while (v / div >= 10) div *= 10;
            while (div > 0)
            {
                sb.Append((char)('0' + (int)(v / div % 10)));
                div /= 10;
            }
        }

        static void AppendSeconds(StringBuilder sb, double t)
        {
            if (double.IsNaN(t) || double.IsInfinity(t)) t = 0.0;
            bool negative = t < 0.0;
            if (negative) t = -t;
            if (t > 9.0e12) t = 9.0e12;                              // keeps the long conversion in range
            long ms = (long)(t * 1000.0 + 0.5);                      // 3 decimals, halves round up
            if (negative && ms != 0) sb.Append('-');
            AppendUInt64(sb, (ulong)(ms / 1000));
            sb.Append('.');
            long frac = ms % 1000;
            if (frac < 100) sb.Append('0');
            if (frac < 10) sb.Append('0');
            AppendUInt64(sb, (ulong)frac);
        }

        static void AppendQuoted(StringBuilder sb, string s)
        {
            sb.Append('"');
            AppendEscaped(sb, s);
            sb.Append('"');
        }

        // JSON string escaping (RFC 8259 section 7) plus the characters that line-oriented readers treat as line breaks
        // (U+0085, U+2028, U+2029) and unpaired surrogates, which no JSON parser can round-trip.
        static void AppendEscaped(StringBuilder sb, string s)
        {
            int n = s.Length;
            int cut = 0;
            if (n > MaxStringChars)
            {
                n = MaxStringChars;
                if (char.IsHighSurrogate(s[n - 1])) n--;             // never split a surrogate pair
                cut = s.Length - n;
            }
            int run = 0;                                             // start of the pending stretch that needs no escaping
            for (int i = 0; i < n; i++)
            {
                char c = s[i];
                if (c >= 0x20 && c < 0x7F && c != '"' && c != '\\') continue;   // plain printable ASCII
                string esc = null;
                switch (c)
                {
                    case '"': esc = "\\\""; break;
                    case '\\': esc = "\\\\"; break;
                    case '\n': esc = "\\n"; break;
                    case '\r': esc = "\\r"; break;
                    case '\t': esc = "\\t"; break;
                    case '\b': esc = "\\b"; break;
                    case '\f': esc = "\\f"; break;
                }
                if (esc == null && c >= 0x7F)
                {
                    if (char.IsHighSurrogate(c))
                    {
                        if (i + 1 < n && char.IsLowSurrogate(s[i + 1])) { i++; continue; }   // a valid pair goes through
                    }
                    else if (!(char.IsLowSurrogate(c) || c == 0x85 || c == 0x2028 || c == 0x2029))
                    {
                        continue;                                    // any other non-ASCII character goes through as is
                    }
                }
                if (i > run) sb.Append(s, run, i - run);
                if (esc != null) sb.Append(esc);
                else if (c >= 0xD800 && c <= 0xDFFF) sb.Append("\\ufffd");   // unpaired surrogate
                else AppendU4(sb, c);
                run = i + 1;
            }
            if (n > run) sb.Append(s, run, n - run);
            if (cut > 0)
            {
                sb.Append("...[+");
                AppendUInt64(sb, (ulong)cut);
                sb.Append(']');
            }
        }

        static void AppendU4(StringBuilder sb, int c)
        {
            const string Hex = "0123456789abcdef";
            sb.Append("\\u");
            sb.Append(Hex[(c >> 12) & 0xF]);
            sb.Append(Hex[(c >> 8) & 0xF]);
            sb.Append(Hex[(c >> 4) & 0xF]);
            sb.Append(Hex[c & 0xF]);
        }
    }
}
