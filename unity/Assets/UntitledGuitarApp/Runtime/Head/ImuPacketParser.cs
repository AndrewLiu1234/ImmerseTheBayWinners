// ImuPacketParser: reads one IMU datagram from tools/imu-bridge without allocating anything.
//
// Wire format (pinned with the bridge): one UDP datagram is one UTF-8 JSON object
//     {"t": <seconds since the bridge started>, "q": [x, y, z, w]}
// q is a unit quaternion already in Unity's frame (left-handed, +X right, +Y up, +Z forward). It rotates head-local
// vectors into a gravity-aligned world frame and is used as new Quaternion(x, y, z, w) with no axis conversion.
// Keys may come in any order, whitespace is free, numbers may use exponents, and unknown keys of any JSON type are
// skipped (they must still be well-formed JSON).
//
// Why hand-written: the receive thread parses 200+ packets per second, so no string, no list and no boxing may be
// created per packet. The grammar accepted is exactly RFC 8259 for the pieces it reads (so NaN, Infinity, leading zeros,
// "1.", ".5", "+1" and single quotes are all rejected), plus an optional UTF-8 byte order mark. Everything here is
// managed C#: it touches no Unity engine call, so it is safe on any thread.

using System;
using UnityEngine;

namespace UntitledGuitarApp.Runtime.Head
{
    public static class ImuPacketParser
    {
        /// A quaternion whose length differs from 1 by more than this is rejected as corrupt; a smaller error is
        /// removed by normalizing (senders print a rounded decimal, so the length is never exactly 1).
        public const double MaxLengthError = 0.1;

        const int MaxDepth = 64;          // deepest nesting we are willing to skip inside an unknown key (one bit each in a ulong)
        const int MaxExponentDigits = 100000;   // a larger exponent only says "overflow" or "underflow" anyway

        const int KeyOther = 0;
        const int KeyT = 1;
        const int KeyQ = 2;

        // Every power of ten up to 1e22 is exactly representable as a double; scaling by these is correctly rounded.
        static readonly double[] Pow10 =
        {
            1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11,
            1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19, 1e20, 1e21, 1e22
        };

        /// <summary>
        /// Parses buffer[0 .. length). Returns false (and identity / 0) for anything that is not one complete, well-formed
        /// packet with a finite "t" and a finite 4-number "q" of length 1 +/- <see cref="MaxLengthError"/>.
        /// On success q is normalized. A key given twice keeps its last value.
        /// </summary>
        public static bool TryParse(byte[] buffer, int length, out double t, out Quaternion q)
        {
            t = 0.0;
            q = Quaternion.identity;
            if (buffer == null || length < 2 || length > buffer.Length) return false;

            int i = 0;
            int n = length;
            if (n >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF) i = 3;   // UTF-8 byte order mark

            SkipWhitespace(buffer, ref i, n);
            if (i >= n || buffer[i] != (byte)'{') return false;
            i++;

            bool haveT = false;
            bool haveQ = false;
            double tv = 0.0;
            double qx = 0.0, qy = 0.0, qz = 0.0, qw = 0.0;

            SkipWhitespace(buffer, ref i, n);
            if (i < n && buffer[i] == (byte)'}') return false;   // "{}" carries neither key

            while (true)
            {
                int key;
                if (!ScanMemberName(buffer, ref i, n, out key)) return false;

                if (key == KeyT)
                {
                    if (!ScanNumber(buffer, ref i, n, out tv) || !IsFinite(tv)) return false;
                    haveT = true;
                }
                else if (key == KeyQ)
                {
                    if (!ScanQuaternion(buffer, ref i, n, out qx, out qy, out qz, out qw)) return false;
                    haveQ = true;
                }
                else if (!SkipValue(buffer, ref i, n))
                {
                    return false;
                }

                SkipWhitespace(buffer, ref i, n);
                if (i >= n) return false;                 // truncated: no closing brace
                byte c = buffer[i++];
                if (c == (byte)',') continue;
                if (c == (byte)'}') break;
                return false;
            }

            SkipWhitespace(buffer, ref i, n);
            if (i != n || !haveT || !haveQ) return false;   // trailing garbage, or a required key is missing

            double len = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            if (!(len >= 1.0 - MaxLengthError && len <= 1.0 + MaxLengthError)) return false;   // false for NaN too

            double inv = 1.0 / len;
            t = tv;
            q = new Quaternion((float)(qx * inv), (float)(qy * inv), (float)(qz * inv), (float)(qw * inv));
            return true;
        }

        // ------------------------------------------------------------------------------------------------ scanning

        static bool IsFinite(double v)
        {
            return !(double.IsNaN(v) || double.IsInfinity(v));
        }

        static void SkipWhitespace(byte[] b, ref int i, int n)
        {
            while (i < n)
            {
                byte c = b[i];
                if (c == (byte)' ' || c == (byte)'\t' || c == (byte)'\n' || c == (byte)'\r') i++;
                else break;
            }
        }

        // Scans optional whitespace, a key string and its colon. kind says whether the key is "t", "q" or something else.
        static bool ScanMemberName(byte[] b, ref int i, int n, out int kind)
        {
            SkipWhitespace(b, ref i, n);
            if (!ScanString(b, ref i, n, out kind)) return false;
            SkipWhitespace(b, ref i, n);
            if (i >= n || b[i] != (byte)':') return false;
            i++;
            SkipWhitespace(b, ref i, n);
            return true;
        }

        // Scans a JSON string starting at its opening quote. Escapes are decoded only far enough to classify a key
        // as exactly "t" or "q" (so "t" still counts as t). Raw bytes of 0x80 and above are not UTF-8 validated.
        static bool ScanString(byte[] b, ref int i, int n, out int kind)
        {
            kind = KeyOther;
            if (i >= n || b[i] != (byte)'"') return false;
            i++;

            int count = 0;        // decoded characters so far
            int first = -1;       // the first decoded character
            while (true)
            {
                if (i >= n) return false;   // truncated inside the string
                byte c = b[i];
                if (c == (byte)'"')
                {
                    i++;
                    break;
                }
                if (c < 0x20) return false;   // an unescaped control character is not allowed

                int decoded;
                if (c == (byte)'\\')
                {
                    i++;
                    if (i >= n) return false;
                    byte e = b[i];
                    switch (e)
                    {
                        case (byte)'"': decoded = '"'; i++; break;
                        case (byte)'\\': decoded = '\\'; i++; break;
                        case (byte)'/': decoded = '/'; i++; break;
                        case (byte)'b': decoded = 8; i++; break;
                        case (byte)'f': decoded = 12; i++; break;
                        case (byte)'n': decoded = 10; i++; break;
                        case (byte)'r': decoded = 13; i++; break;
                        case (byte)'t': decoded = 9; i++; break;
                        case (byte)'u':
                        {
                            if (i + 4 >= n) return false;   // four hex digits and at least the closing quote must follow
                            int v = 0;
                            for (int k = 1; k <= 4; k++)
                            {
                                int h = HexValue(b[i + k]);
                                if (h < 0) return false;
                                v = (v << 4) | h;
                            }
                            decoded = v;
                            i += 5;
                            break;
                        }
                        default: return false;
                    }
                }
                else
                {
                    decoded = c;
                    i++;
                }

                if (count == 0) first = decoded;
                count++;
            }

            if (count == 1)
            {
                if (first == 't') kind = KeyT;
                else if (first == 'q') kind = KeyQ;
            }
            return true;
        }

        static int HexValue(byte c)
        {
            if (c >= (byte)'0' && c <= (byte)'9') return c - (byte)'0';
            if (c >= (byte)'a' && c <= (byte)'f') return c - (byte)'a' + 10;
            if (c >= (byte)'A' && c <= (byte)'F') return c - (byte)'A' + 10;
            return -1;
        }

        // Scans "[ n , n , n , n ]" with finite numbers only.
        static bool ScanQuaternion(byte[] b, ref int i, int n, out double x, out double y, out double z, out double w)
        {
            x = 0.0; y = 0.0; z = 0.0; w = 0.0;
            if (i >= n || b[i] != (byte)'[') return false;
            i++;
            return ScanComponent(b, ref i, n, (byte)',', out x)
                && ScanComponent(b, ref i, n, (byte)',', out y)
                && ScanComponent(b, ref i, n, (byte)',', out z)
                && ScanComponent(b, ref i, n, (byte)']', out w);
        }

        // Scans optional whitespace, a finite number, optional whitespace and the given separator (consumed).
        static bool ScanComponent(byte[] b, ref int i, int n, byte separator, out double value)
        {
            SkipWhitespace(b, ref i, n);
            if (!ScanNumber(b, ref i, n, out value) || !IsFinite(value)) return false;
            SkipWhitespace(b, ref i, n);
            if (i >= n || b[i] != separator) return false;
            i++;
            return true;
        }

        // JSON number: -?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?
        // Returns false if the text is not a JSON number. The value may still be +/-Infinity (e.g. 1e999); callers that
        // need a finite value check it. Digits are gathered into a 64-bit mantissa (19 digits) and scaled by a power of ten.
        static bool ScanNumber(byte[] b, ref int i, int n, out double value)
        {
            value = 0.0;
            int p = i;
            bool negative = false;
            if (p < n && b[p] == (byte)'-')
            {
                negative = true;
                p++;
            }
            if (p >= n) return false;

            ulong mantissa = 0;
            int significant = 0;     // digits stored in the mantissa, counted from the first non-zero digit
            int exponent10 = 0;

            byte c = b[p];
            if (c == (byte)'0')
            {
                p++;                 // a lone leading zero: "01" is not JSON, and the caller will choke on the "1"
            }
            else if (c >= (byte)'1' && c <= (byte)'9')
            {
                while (p < n)
                {
                    c = b[p];
                    if (c < (byte)'0' || c > (byte)'9') break;
                    if (significant < 19)
                    {
                        mantissa = mantissa * 10UL + (ulong)(c - (byte)'0');
                        significant++;
                    }
                    else
                    {
                        exponent10++;   // more digits than a ulong holds: they only scale the value
                    }
                    p++;
                }
            }
            else
            {
                return false;
            }

            if (p < n && b[p] == (byte)'.')
            {
                p++;
                if (p >= n || b[p] < (byte)'0' || b[p] > (byte)'9') return false;   // "1." needs a digit
                while (p < n)
                {
                    c = b[p];
                    if (c < (byte)'0' || c > (byte)'9') break;
                    int digit = c - (byte)'0';
                    if (mantissa == 0 && digit == 0)
                    {
                        exponent10--;   // leading zeros of the fraction only scale the value
                    }
                    else if (significant < 19)
                    {
                        mantissa = mantissa * 10UL + (ulong)digit;
                        significant++;
                        exponent10--;
                    }
                    // else: beyond the precision of a double anyway, dropped
                    p++;
                }
            }

            if (p < n && (b[p] == (byte)'e' || b[p] == (byte)'E'))
            {
                p++;
                bool exponentNegative = false;
                if (p < n && (b[p] == (byte)'+' || b[p] == (byte)'-'))
                {
                    exponentNegative = b[p] == (byte)'-';
                    p++;
                }
                if (p >= n || b[p] < (byte)'0' || b[p] > (byte)'9') return false;   // "1e" needs a digit
                int e = 0;
                while (p < n)
                {
                    c = b[p];
                    if (c < (byte)'0' || c > (byte)'9') break;
                    if (e < MaxExponentDigits) e = e * 10 + (c - (byte)'0');
                    p++;
                }
                exponent10 += exponentNegative ? -e : e;
            }

            i = p;
            if (mantissa == 0)
            {
                value = negative ? -0.0 : 0.0;
                return true;
            }

            double v = (double)mantissa;
            if (exponent10 > 0)
                v = exponent10 <= 22 ? v * Pow10[exponent10] : v * Math.Pow(10.0, exponent10);
            else if (exponent10 < 0)
                v = exponent10 >= -22 ? v / Pow10[-exponent10] : v * Math.Pow(10.0, exponent10);
            value = negative ? -v : v;
            return true;
        }

        // true / false / null
        static bool ScanLiteral(byte[] b, ref int i, int n)
        {
            if (i >= n) return false;
            byte c = b[i];
            if (c == (byte)'t') return Match(b, ref i, n, "true");
            if (c == (byte)'f') return Match(b, ref i, n, "false");
            if (c == (byte)'n') return Match(b, ref i, n, "null");
            return false;
        }

        static bool Match(byte[] b, ref int i, int n, string literal)
        {
            if (i + literal.Length > n) return false;
            for (int k = 0; k < literal.Length; k++)
            {
                if (b[i + k] != (byte)literal[k]) return false;
            }
            i += literal.Length;
            return true;
        }

        // Skips one complete JSON value of any type, validating it as it goes. Iterative on purpose: a hostile packet
        // of 60 000 opening brackets must be rejected, not overflow the stack. A bit per open container remembers
        // whether it is an object (bit set) or an array, so "[}" and "{]" are caught.
        static bool SkipValue(byte[] b, ref int i, int n)
        {
            ulong isObject = 0;
            int depth = 0;
            while (true)
            {
                // ---- a value has to start here ----
                SkipWhitespace(b, ref i, n);
                if (i >= n) return false;
                byte c = b[i];
                if (c == (byte)'{')
                {
                    i++;
                    SkipWhitespace(b, ref i, n);
                    if (i >= n) return false;
                    if (b[i] == (byte)'}')
                    {
                        i++;   // {} is a complete value
                    }
                    else
                    {
                        if (depth >= MaxDepth) return false;
                        isObject |= 1UL << depth;
                        depth++;
                        int ignoredKind;
                        if (!ScanMemberName(b, ref i, n, out ignoredKind)) return false;
                        continue;   // the member's value comes next
                    }
                }
                else if (c == (byte)'[')
                {
                    i++;
                    SkipWhitespace(b, ref i, n);
                    if (i >= n) return false;
                    if (b[i] == (byte)']')
                    {
                        i++;   // [] is a complete value
                    }
                    else
                    {
                        if (depth >= MaxDepth) return false;
                        isObject &= ~(1UL << depth);
                        depth++;
                        continue;   // the first element comes next
                    }
                }
                else if (c == (byte)'"')
                {
                    int ignoredKind;
                    if (!ScanString(b, ref i, n, out ignoredKind)) return false;
                }
                else if (c == (byte)'-' || (c >= (byte)'0' && c <= (byte)'9'))
                {
                    double ignoredValue;
                    if (!ScanNumber(b, ref i, n, out ignoredValue)) return false;
                }
                else if (!ScanLiteral(b, ref i, n))
                {
                    return false;
                }

                // ---- a value just ended: close containers, or move on to the next member / element ----
                while (true)
                {
                    if (depth == 0) return true;
                    SkipWhitespace(b, ref i, n);
                    if (i >= n) return false;
                    byte d = b[i++];
                    bool inObject = ((isObject >> (depth - 1)) & 1UL) != 0;
                    if (d == (byte)',')
                    {
                        if (inObject)
                        {
                            int ignoredKind;
                            if (!ScanMemberName(b, ref i, n, out ignoredKind)) return false;
                        }
                        break;   // the next value of the same container
                    }
                    if ((inObject && d == (byte)'}') || (!inObject && d == (byte)']'))
                    {
                        depth--;
                        continue;   // that container is itself a finished value
                    }
                    return false;
                }
            }
        }
    }
}
