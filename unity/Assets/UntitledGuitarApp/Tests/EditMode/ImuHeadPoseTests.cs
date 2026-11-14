// EditMode tests for Runtime/Head: ImuPacketParser, ImuSampleSlot and HeadPoseFilter (the pure logic behind ImuHeadPose).
// No Play Mode, device, network or sleeping: time is always passed in. The helpers below build rotations by hand
// (axis-angle products) instead of calling Quaternion.Euler / Angle, so the tests do not depend on native engine calls;
// one guarded test checks that the hand-built convention matches Quaternion.Euler when it can run.

using System;
using System.Globalization;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Head;
using Random = System.Random;   // UnityEngine.Random is also in scope

namespace UntitledGuitarApp.Tests.EditMode
{
    public class ImuHeadPoseTests
    {
        const double Deg = Math.PI / 180.0;
        const string FullPacket = "{'t': 12.345678, 'q': [0.0, 0.7071067811865476, 0.0, 0.7071067811865476]}";

        // ================================================================================================ helpers

        // JSON written with 'single quotes' in the source reads better than escaped double quotes.
        static string J(string s) { return s.Replace('\'', '"'); }

        static byte[] Utf8(string s) { return Encoding.UTF8.GetBytes(s); }

        static bool TryParse(string json, out double t, out Quaternion q)
        {
            byte[] bytes = Utf8(json);
            return ImuPacketParser.TryParse(bytes, bytes.Length, out t, out q);
        }

        static bool Accepts(string json)
        {
            double t;
            Quaternion q;
            return TryParse(json, out t, out q);
        }

        static void ExpectParse(string json, double t, double x, double y, double z, double w)
        {
            double pt;
            Quaternion pq;
            Assert.IsTrue(TryParse(json, out pt, out pq), "should accept: " + json);
            Assert.AreEqual(t, pt, 1e-9 * Math.Max(1.0, Math.Abs(t)), "t of " + json);
            Assert.AreEqual(x, pq.x, 1e-6, "x of " + json);
            Assert.AreEqual(y, pq.y, 1e-6, "y of " + json);
            Assert.AreEqual(z, pq.z, 1e-6, "z of " + json);
            Assert.AreEqual(w, pq.w, 1e-6, "w of " + json);
        }

        static Quaternion AxisAngle(double ax, double ay, double az, double degrees)
        {
            double h = degrees * Deg * 0.5;
            double s = Math.Sin(h);
            return new Quaternion((float)(ax * s), (float)(ay * s), (float)(az * s), (float)Math.Cos(h));
        }

        // Same convention as Quaternion.Euler(pitch, yaw, roll): roll about Z first, then pitch about X, then yaw about Y.
        // Positive yaw turns right (+Z toward +X), positive pitch looks down, positive roll tilts +X toward +Y.
        static Quaternion Euler(double yawDeg, double pitchDeg, double rollDeg)
        {
            return AxisAngle(0, 1, 0, yawDeg) * AxisAngle(1, 0, 0, pitchDeg) * AxisAngle(0, 0, 1, rollDeg);
        }

        // Smallest rotation between two orientations in degrees, in double precision (Quaternion.Angle is only
        // good to about 0.15 degrees near zero).
        static double AngleDeg(Quaternion a, Quaternion b)
        {
            double bx = -b.x, by = -b.y, bz = -b.z, bw = b.w;
            double x = a.w * bx + a.x * bw + a.y * bz - a.z * by;
            double y = a.w * by + a.y * bw + a.z * bx - a.x * bz;
            double z = a.w * bz + a.z * bw + a.x * by - a.y * bx;
            double w = a.w * bw - a.x * bx - a.y * by - a.z * bz;
            return 2.0 * Math.Atan2(Math.Sqrt(x * x + y * y + z * z), Math.Abs(w)) / Deg;
        }

        // Which way the forward vector points around the vertical, in degrees (0 = +Z, positive = toward +X).
        static double HeadingDeg(Quaternion q)
        {
            Vector3 f = q * Vector3.forward;
            return Math.Atan2(f.x, f.z) / Deg;
        }

        static void AssertSameRotation(Quaternion actual, Quaternion expected, double toleranceDeg, string what)
        {
            double angle = AngleDeg(actual, expected);
            Assert.IsTrue(angle <= toleranceDeg,
                what + ": rotations differ by " + angle.ToString("0.0000", CultureInfo.InvariantCulture) + " deg (allowed " + toleranceDeg + ")");
        }

        static void AssertUnitAndFinite(Quaternion q, string what)
        {
            double len = Math.Sqrt((double)q.x * q.x + (double)q.y * q.y + (double)q.z * q.z + (double)q.w * q.w);
            Assert.IsFalse(double.IsNaN(len) || double.IsInfinity(len), what + ": not finite");
            Assert.AreEqual(1.0, len, 1e-5, what + ": length");
        }

        static HeadPoseFilter NewFilter(double predictionMs = 10.0, double staleMs = 250.0,
                                        bool invertYaw = false, bool invertPitch = false, bool invertRoll = false)
        {
            HeadPoseFilter f = new HeadPoseFilter();
            f.Configure((float)staleMs, (float)predictionMs, invertYaw, invertPitch, invertRoll);
            return f;
        }

        // ================================================================================================ parser: valid input

        [Test]
        public void Parser_ValidPacket_GivesTimeAndQuaternion()
        {
            ExpectParse(J(FullPacket), 12.345678, 0.0, 0.70710678, 0.0, 0.70710678);
        }

        // Real datagrams from tools/imu-bridge/bridge.py --fake: t, q, then the extra keys "seq" and "src" that receivers ignore.
        // The first was captured on the merged bridge; the second is the example in docs/apis/glasses/imu-protocol.md.
        const string BridgeDatagramCaptured = "{\"t\":0.744376,\"q\":[0.0787635,0.0943976,0.0383792,0.9916715],\"seq\":186,\"src\":\"fake\"}";
        const string BridgeDatagramDocumented = "{\"t\":2.996300,\"q\":[-0.0160335,0.2056157,-0.0462472,0.9774080],\"seq\":749,\"src\":\"fake\"}";

        [Test]
        public void Parser_TheBridgesRealFakeModeDatagrams_AreAccepted()
        {
            ExpectBridgeDatagram(BridgeDatagramCaptured, 0.744376, 0.0787635, 0.0943976, 0.0383792, 0.9916715);
            ExpectBridgeDatagram(BridgeDatagramDocumented, 2.996300, -0.0160335, 0.2056157, -0.0462472, 0.9774080);
        }

        [Test]
        public void Parser_TheBridgesRealDatagram_SurvivesAnyCutOrAnyPaddingAfterIt()
        {
            byte[] bytes = Utf8(BridgeDatagramCaptured);
            double t;
            Quaternion q;
            for (int length = 0; length < bytes.Length; length++)
                Assert.IsFalse(ImuPacketParser.TryParse(bytes, length, out t, out q), "cut to " + length + " bytes");
            byte[] padded = new byte[4096];
            Array.Copy(bytes, padded, bytes.Length);
            Assert.IsTrue(ImuPacketParser.TryParse(padded, bytes.Length, out t, out q), "the receiver passes the datagram length, not the buffer size");
            Assert.AreEqual(0.744376, t, 1e-12);
        }

        // The bridge prints seven decimals, so its quaternion has length 1 only to about 1e-7; the parser normalizes it.
        static void ExpectBridgeDatagram(string json, double t, double x, double y, double z, double w)
        {
            double len = Math.Sqrt(x * x + y * y + z * z + w * w);
            ExpectParse(json, t, x / len, y / len, z / len, w / len);
            double pt;
            Quaternion pq;
            Assert.IsTrue(TryParse(json, out pt, out pq));
            AssertUnitAndFinite(pq, json);
        }

        [Test]
        public void Parser_CompactPacket_WithoutAnyWhitespace()
        {
            ExpectParse("{\"t\":0,\"q\":[0,0,0,1]}", 0.0, 0, 0, 0, 1);
        }

        [Test]
        public void Parser_KeysInAnyOrder()
        {
            ExpectParse(J("{'q': [0.5, 0.5, 0.5, 0.5], 't': 3.5}"), 3.5, 0.5, 0.5, 0.5, 0.5);
            ExpectParse(J("{'t': 3.5, 'q': [0.5, 0.5, 0.5, 0.5]}"), 3.5, 0.5, 0.5, 0.5, 0.5);
        }

        [Test]
        public void Parser_AnyJsonWhitespace_AroundEveryToken()
        {
            ExpectParse(J(" \t\r\n{ \r\n\t'q'\t:\t[ 0 ,\n0 , 0\r\n, 1 ] , \n 't' : 1.25 \n}\r\n  "), 1.25, 0, 0, 0, 1);
        }

        [Test]
        public void Parser_OnlyJsonWhitespaceCounts()
        {
            Assert.IsFalse(Accepts(J("{\u000b'q':[0,0,0,1],'t':1}")), "vertical tab");
            Assert.IsFalse(Accepts(J("{\u000c'q':[0,0,0,1],'t':1}")), "form feed");
            Assert.IsFalse(Accepts(J("{\u00a0'q':[0,0,0,1],'t':1}")), "no-break space");
            Assert.IsFalse(Accepts(J("{'q':[0,0,0,1],'t':1}\0")), "trailing NUL");
        }

        [Test]
        public void Parser_Utf8ByteOrderMark_IsIgnored()
        {
            byte[] body = Utf8(J("{'t':2,'q':[0,0,0,1]}"));
            byte[] withBom = new byte[body.Length + 3];
            withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
            Array.Copy(body, 0, withBom, 3, body.Length);
            double t;
            Quaternion q;
            Assert.IsTrue(ImuPacketParser.TryParse(withBom, withBom.Length, out t, out q));
            Assert.AreEqual(2.0, t, 0.0);
        }

        [Test]
        public void Parser_NumbersInEveryJsonForm()
        {
            string[] forms =
            {
                "0", "-0", "7", "-7", "0.5", "-0.5", "123.456", "1e2", "1E2", "1e+2", "1e-2", "1.5e3", "-1.5E-3",
                "0e0", "10e-1", "0.0001e4", "1e22", "1e23", "1e-22", "1e-23", "123456789012345678901234567890",
                "0.000000000000000000000000000001", "9007199254740993", "1e-300", "1e300",
                "0.30000000000000004", "100000000000000000000", "1e0", "0.1e1"
            };
            foreach (string form in forms)
            {
                double expected = double.Parse(form, CultureInfo.InvariantCulture);
                double t;
                Quaternion q;
                Assert.IsTrue(TryParse("{\"t\":" + form + ",\"q\":[0,0,0,1]}", out t, out q), "should accept t = " + form);
                Assert.AreEqual(expected, t, Math.Abs(expected) * 1e-14, "t = " + form);
            }
        }

        [Test]
        public void Parser_RandomNumberStrings_AgreeWithDoubleParse()
        {
            Random rng = new Random(20261114);
            StringBuilder sb = new StringBuilder();
            for (int n = 0; n < 4000; n++)
            {
                sb.Length = 0;
                if (rng.Next(2) == 0) sb.Append('-');
                int intDigits = rng.Next(1, 25);
                sb.Append((char)('1' + rng.Next(9)));
                for (int i = 1; i < intDigits; i++) sb.Append((char)('0' + rng.Next(10)));
                if (rng.Next(3) != 0)
                {
                    sb.Append('.');
                    int fracDigits = rng.Next(1, 25);
                    for (int i = 0; i < fracDigits; i++) sb.Append((char)('0' + rng.Next(10)));
                }
                if (rng.Next(2) == 0)
                {
                    sb.Append(rng.Next(2) == 0 ? 'e' : 'E');
                    int sign = rng.Next(3);
                    if (sign == 0) sb.Append('-'); else if (sign == 1) sb.Append('+');
                    sb.Append(rng.Next(0, 40));
                }
                string text = sb.ToString();
                double expected = double.Parse(text, CultureInfo.InvariantCulture);
                double t;
                Quaternion q;
                Assert.IsTrue(TryParse("{\"t\":" + text + ",\"q\":[0,0,0,1]}", out t, out q), text);
                Assert.AreEqual(expected, t, Math.Abs(expected) * 1e-14, text);
            }
        }

        [Test]
        public void Parser_ExtraKeysOfEveryJsonType_AreSkipped()
        {
            string[] values =
            {
                "null", "true", "false", "0", "-1.5e-3", "'x'", "''", "'a \\\"quoted\\\" {not} [json] \\\\ \\/ \\n \\u00e9 \\ud83d\\ude00'",
                "[]", "{}", "[ ]", "{ }", "[1, [2, [3]], {}]", "{'a': {'b': [true, false, null, 1.5, 'z']}}",
                "[[[[[[[[[[1]]]]]]]]]]", "{'t': 'decoy', 'q': [9, 9]}", "[{'q': 1}, {'t': 2}]"
            };
            foreach (string value in values)
            {
                string extra = "'extra': " + value;
                ExpectParse(J("{" + extra + ", 't': 1.5, 'q': [0,0,0,1]}"), 1.5, 0, 0, 0, 1);
                ExpectParse(J("{'t': 1.5, " + extra + ", 'q': [0,0,0,1]}"), 1.5, 0, 0, 0, 1);
                ExpectParse(J("{'t': 1.5, 'q': [0,0,0,1], " + extra + "}"), 1.5, 0, 0, 0, 1);
            }
        }

        [Test]
        public void Parser_KeysThatMerelyResembleTOrQ_AreExtraKeys()
        {
            ExpectParse(J("{'tt': 5, 'T': 6, 'q2': [1,2], 'Q': 7, 'time': 8, ' t': 9, 't': 1, 'q': [0,0,0,1]}"), 1, 0, 0, 0, 1);
            Assert.IsFalse(Accepts(J("{'T': 1, 'Q': [0,0,0,1]}")), "keys are case sensitive");
        }

        [Test]
        public void Parser_NestedTAndQ_DoNotLeakOut()
        {
            ExpectParse(J("{'meta': {'t': 99, 'q': [1, 0, 0, 0]}, 't': 2, 'q': [0, 0, 1, 0]}"), 2, 0, 0, 1, 0);
        }

        [Test]
        public void Parser_EscapedKeyNames_StillMatch()
        {
            ExpectParse(J("{'\\u0074': 1.5, 'q': [0,0,0,1]}"), 1.5, 0, 0, 0, 1);
            ExpectParse(J("{'t': 1.5, '\\u0071': [0,0,0,1]}"), 1.5, 0, 0, 0, 1);
            Assert.IsFalse(Accepts(J("{'t': 1.5, '\\u0051': [0,0,0,1]}")), "\\u0051 is an upper-case Q");
            Assert.IsFalse(Accepts(J("{'t': 1.5, 'q\\u0000': [0,0,0,1]}")), "a NUL after the q makes a different key");
        }

        [Test]
        public void Parser_DuplicateKey_LastOneWins()
        {
            ExpectParse(J("{'t': 1, 't': 2, 'q': [1,0,0,0], 'q': [0,0,0,1]}"), 2, 0, 0, 0, 1);
        }

        [Test]
        public void Parser_NegativeZeroAndNegativeTime_AreFinite_AndAccepted()
        {
            ExpectParse(J("{'t': -0, 'q': [-0.0, 0, -0, 1]}"), 0, 0, 0, 0, 1);
            ExpectParse(J("{'t': -2.5, 'q': [0,0,0,1]}"), -2.5, 0, 0, 0, 1);
        }

        [Test]
        public void Parser_SlightlyOffUnitQuaternion_IsNormalized()
        {
            double t;
            Quaternion q;
            Assert.IsTrue(TryParse(J("{'t':1,'q':[0,0,0,1.05]}"), out t, out q));
            Assert.AreEqual(1.0, q.w, 1e-6);
            Assert.IsTrue(TryParse(J("{'t':1,'q':[0.5,0.5,0.5,0.55]}"), out t, out q));
            AssertUnitAndFinite(q, "normalized");
            Assert.AreEqual(0.5 / Math.Sqrt(1.0525), q.x, 1e-6);
            Assert.AreEqual(0.55 / Math.Sqrt(1.0525), q.w, 1e-6);
            Assert.IsTrue(TryParse(J("{'t':1,'q':[0,0,0,0.95]}"), out t, out q));
            Assert.AreEqual(1.0, q.w, 1e-6);
        }

        [Test]
        public void Parser_RespectsTheLengthArgument()
        {
            byte[] packet = Utf8(J("{'t':1,'q':[0,0,0,1]}"));
            byte[] padded = new byte[packet.Length + 8];
            Array.Copy(packet, padded, packet.Length);
            for (int i = packet.Length; i < padded.Length; i++) padded[i] = (byte)'#';   // stale bytes from an earlier, longer datagram
            double t;
            Quaternion q;
            Assert.IsTrue(ImuPacketParser.TryParse(padded, packet.Length, out t, out q), "bytes beyond length are ignored");
            Assert.IsFalse(ImuPacketParser.TryParse(padded, padded.Length, out t, out q), "but they are garbage when included");
            Assert.IsFalse(ImuPacketParser.TryParse(padded, packet.Length - 1, out t, out q), "cut one byte short");
        }

        [Test]
        public void Parser_InvalidArguments_AreRejected()
        {
            double t;
            Quaternion q;
            byte[] packet = Utf8(J("{'t':1,'q':[0,0,0,1]}"));
            Assert.IsFalse(ImuPacketParser.TryParse(null, 10, out t, out q), "null buffer");
            Assert.IsFalse(ImuPacketParser.TryParse(packet, 0, out t, out q), "length 0");
            Assert.IsFalse(ImuPacketParser.TryParse(packet, -4, out t, out q), "negative length");
            Assert.IsFalse(ImuPacketParser.TryParse(packet, packet.Length + 1, out t, out q), "length beyond the buffer");
            Assert.IsFalse(ImuPacketParser.TryParse(new byte[0], 0, out t, out q), "empty buffer");
        }

        [Test]
        public void Parser_FailureLeavesIdentityAndZero()
        {
            double t;
            Quaternion q;
            Assert.IsFalse(TryParse("garbage", out t, out q));
            Assert.AreEqual(0.0, t, 0.0);
            Assert.AreEqual(0.0, q.x, 0.0);
            Assert.AreEqual(1.0, q.w, 0.0);
        }

        // ================================================================================================ parser: bad input

        [Test]
        public void Parser_TruncatedAtEveryLength_IsRejected()
        {
            string[] packets =
            {
                J("{'t':1,'q':[0,0,0,1]}"),
                J(FullPacket),
                J("{ 'seq': 7, 'note': 'a \\\"b\\\" {x}', 'q': [0, 0, 0, 1], 'nest': {'a': [1, {'b': null}]}, 't': 1e2 }")
            };
            foreach (string packet in packets)
            {
                byte[] bytes = Utf8(packet);
                double t;
                Quaternion q;
                Assert.IsTrue(ImuPacketParser.TryParse(bytes, bytes.Length, out t, out q), "the full packet is valid: " + packet);
                for (int length = 0; length < bytes.Length; length++)
                    Assert.IsFalse(ImuPacketParser.TryParse(bytes, length, out t, out q), "prefix of " + length + " bytes of " + packet);
            }
        }

        [Test]
        public void Parser_Garbage_IsRejected()
        {
            string[] bad =
            {
                "", " ", "hello", "{", "}", "{}", "[]", "[0,0,0,1]", "null", "true", "1", "\"t\"", "{\"t\"", "{\"t\":", "{\"t\":1",
                "{'t':1,'q':[0,0,0,1]}",                                   // single quotes are not JSON
                "{t:1,q:[0,0,0,1]}",                                       // bare keys are not JSON
                J("{'t':1}"), J("{'q':[0,0,0,1]}"), J("{'t':1,'x':[0,0,0,1]}"),
                J("{'t':'1','q':[0,0,0,1]}"), J("{'t':null,'q':[0,0,0,1]}"), J("{'t':[1],'q':[0,0,0,1]}"), J("{'t':true,'q':[0,0,0,1]}"),
                J("{'t':1,'q':null}"), J("{'t':1,'q':'0,0,0,1'}"), J("{'t':1,'q':{'x':0}}"), J("{'t':1,'q':[]}"),
                J("{'t':1,'q':[0,0,0]}"), J("{'t':1,'q':[0,0,0,1,0]}"), J("{'t':1,'q':[0,0,'0',1]}"), J("{'t':1,'q':[0,0,null,1]}"),
                J("{'t':1,'q':[0,0,[0],1]}"), J("{'t':1,'q':[0,0,0,1,]}"), J("{'t':1,'q':[,0,0,0,1]}"), J("{'t':1,'q':[0 0 0 1]}"),
                J("{'t':1,'q':[0,0,0,1}"), J("{'t':1,'q':0,0,0,1]}"),
                J("{'t':1,'q':[0,0,0,1]} x"), J("{'t':1,'q':[0,0,0,1]}}"), J("{'t':1,'q':[0,0,0,1]}{'t':1,'q':[0,0,0,1]}"),
                J("x{'t':1,'q':[0,0,0,1]}"), J("{'t':1,'q':[0,0,0,1],}"), J("{,'t':1,'q':[0,0,0,1]}"), J("{'t':1,,'q':[0,0,0,1]}"),
                J("{'t' 1,'q':[0,0,0,1]}"), J("{'t'::1,'q':[0,0,0,1]}"), J("{'t':1 'q':[0,0,0,1]}"), J("{'t':1;'q':[0,0,0,1]}"),
                J("{'t':1,'q':[0,0,0,1]"), J("{'t':1,'q':[0,0,0,1]]"),
                J("{'t':1,'q':[0,0,0,1],'x':[1,2,}"), J("{'t':1,'q':[0,0,0,1],'x':{'a':1]}"), J("{'t':1,'q':[0,0,0,1],'x':[1,2}"),
                J("{'t':1,'q':[0,0,0,1],'x':[1,,2]}"), J("{'t':1,'q':[0,0,0,1],'x':{'a'}}"), J("{'t':1,'q':[0,0,0,1],'x':{1:2}}"),
                J("{'t':1,'q':[0,0,0,1],'x':tru}"), J("{'t':1,'q':[0,0,0,1],'x':nul}"), J("{'t':1,'q':[0,0,0,1],'x':truex}"),
                // literals that are wrong only in their last characters (the structure around them stays valid)
                J("{'t':1,'q':[0,0,0,1],'x':trux}"), J("{'t':1,'q':[0,0,0,1],'x':nul1}"), J("{'t':1,'q':[0,0,0,1],'x':fals3}"),
                J("{'t':1,'q':[0,0,0,1],'x':truE}"), J("{'t':1,'q':[0,0,0,1],'x':nulll}"), J("{'t':1,'q':[0,0,0,1],'x':falsee}"),
                J("{'t':1,'q':[0,0,0,1],'x':True}"), J("{'t':1,'q':[0,0,0,1],'x':NULL}"), J("{'t':1,'q':[0,0,0,1],'x':None}"),
                J("{'t':1,'q':[0,0,0,1],'x':'unterminated}"), J("{'t':1,'q':[0,0,0,1],'x':'bad \\q escape'}"),
                J("{'t':1,'q':[0,0,0,1],'x':'bad \\u12G4 escape'}"), J("{'t':1,'q':[0,0,0,1],'x':'short \\u12'}"),
                J("{'t':1,'q':[0,0,0,1],'x':'raw\ttab'}"), J("{'t':1,'q':[0,0,0,1],'x':'raw\nnewline'}"),
                J("{'t':1,'q':[0,0,0,1],'x':01}"), J("{'t':1,'q':[0,0,0,1],'x':1.}"), J("{'t':1,'q':[0,0,0,1],'x':.5}"),
                J("{'t':1,'q':[0,0,0,1],'x':+1}"), J("{'t':1,'q':[0,0,0,1],'x':-}"), J("{'t':1,'q':[0,0,0,1],'x':1e}"),
                J("{'t':1,'q':[0,0,0,1],'x':NaN}"), J("{'t':1,'q':[0,0,0,1],'x':Infinity}"),
            };
            foreach (string json in bad)
                Assert.IsFalse(Accepts(json), "should reject: [" + json + "]");
        }

        [Test]
        public void Parser_MalformedNumbers_AreRejected()
        {
            string[] bad = { "01", "00", "-01", "1.", "-1.", ".5", "-.5", "+1", "--1", "-", "1e", "1e+", "1e-", "1E", "0x10", "1.2.3", "1,5", "1_000", "\u0661\u0662" };   // the last one is Arabic-Indic digits
            foreach (string form in bad)
            {
                Assert.IsFalse(Accepts("{\"t\":" + form + ",\"q\":[0,0,0,1]}"), "t = " + form);
                Assert.IsFalse(Accepts("{\"t\":1,\"q\":[0,0," + form + ",1]}"), "q[2] = " + form);
            }
        }

        [Test]
        public void Parser_NonFiniteNumbers_AreRejected()
        {
            // NaN, Infinity and -Infinity are what Python's json.dumps prints for non-finite floats.
            string[] forms = { "NaN", "Infinity", "-Infinity", "nan", "inf", "-inf", "1e999", "-1e999", "1E+400", "1e99999999999999999999" };
            foreach (string form in forms)
            {
                Assert.IsFalse(Accepts("{\"t\":" + form + ",\"q\":[0,0,0,1]}"), "t = " + form);
                for (int slot = 0; slot < 4; slot++)
                {
                    string[] parts = { "0", "0", "0", "1" };
                    parts[slot] = form;
                    string json = "{\"t\":1,\"q\":[" + string.Join(",", parts) + "]}";
                    Assert.IsFalse(Accepts(json), "q[" + slot + "] = " + form);
                }
            }
            Assert.IsTrue(Accepts("{\"t\":1e-999,\"q\":[0,0,0,1]}"), "underflow to zero is finite");
            Assert.IsTrue(Accepts("{\"t\":0e99999999,\"q\":[0,0,0,1]}"), "zero with a huge exponent is still zero");
        }

        [Test]
        public void Parser_QuaternionFarFromUnitLength_IsRejected()
        {
            string[] bad =
            {
                "[0,0,0,0]", "[0,0,0,2]", "[1,1,1,1]", "[0,0,0,0.5]", "[3,0,0,0]", "[0,0,0,1.2]", "[0,0,0,0.8]", "[1e-20,0,0,0]",
                "[1e200,1e200,0,0]", "[1e154,1e154,1e154,1e154]", "[10,0,0,0]", "[0.5,0.5,0.5,0.9]"
            };
            foreach (string q in bad)
                Assert.IsFalse(Accepts("{\"t\":1,\"q\":" + q + "}"), "q = " + q);
            Assert.IsTrue(Accepts("{\"t\":1,\"q\":[0,0,0,1.09]}"), "9 percent off is within the tolerance");
            Assert.IsTrue(Accepts("{\"t\":1,\"q\":[0,0,0,0.91]}"), "9 percent off is within the tolerance");
        }

        [Test]
        public void Parser_HostileNesting_IsRejectedWithoutOverflowingTheStack()
        {
            string deepArrays = new string('[', 60000);
            Assert.IsFalse(Accepts("{\"x\":" + deepArrays + ",\"t\":1,\"q\":[0,0,0,1]}"), "60000 open brackets");
            Assert.IsFalse(Accepts("{\"x\":" + new string('{', 100) + "}"), "unterminated objects");
            StringBuilder deepObjects = new StringBuilder();
            for (int i = 0; i < 5000; i++) deepObjects.Append("{\"a\":");
            Assert.IsFalse(Accepts("{\"x\":" + deepObjects + "1,\"t\":1,\"q\":[0,0,0,1]}"), "5000 nested objects");

            // Well-formed nesting: 64 levels inside an unknown key are fine, 65 are refused (arrays and objects alike).
            foreach (int levels in new[] { 1, 10, 60, 64 })
            {
                ExpectParse("{\"x\":" + NestedArrays(levels) + ",\"t\":1,\"q\":[0,0,0,1]}", 1, 0, 0, 0, 1);
                ExpectParse("{\"x\":" + NestedObjects(levels) + ",\"t\":1,\"q\":[0,0,0,1]}", 1, 0, 0, 0, 1);
            }
            foreach (int levels in new[] { 65, 70, 200 })
            {
                Assert.IsFalse(Accepts("{\"x\":" + NestedArrays(levels) + ",\"t\":1,\"q\":[0,0,0,1]}"), levels + " nested arrays");
                Assert.IsFalse(Accepts("{\"x\":" + NestedObjects(levels) + ",\"t\":1,\"q\":[0,0,0,1]}"), levels + " nested objects");
            }
            Assert.IsFalse(Accepts("{\"x\":[}],\"t\":1,\"q\":[0,0,0,1]}"), "mismatched brackets");
            Assert.IsFalse(Accepts("{\"x\":{]},\"t\":1,\"q\":[0,0,0,1]}"), "mismatched brackets");
            Assert.IsFalse(Accepts("{\"x\":[[1,2}],\"t\":1,\"q\":[0,0,0,1]}"), "mismatched inner bracket");
        }

        static string NestedArrays(int levels)
        {
            return new string('[', levels) + "1" + new string(']', levels);
        }

        static string NestedObjects(int levels)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < levels; i++) sb.Append("{\"a\":");
            sb.Append('1');
            sb.Append('}', levels);
            return sb.ToString();
        }

        // ================================================================================================ parser: properties

        [Test]
        public void Parser_RandomRotations_RoundTripThroughEveryNumberFormat()
        {
            Random rng = new Random(1114);
            string[] formats = { "R", "E12", "F9", "G17", "E3" };
            for (int n = 0; n < 3000; n++)
            {
                double x = Gaussian(rng), y = Gaussian(rng), z = Gaussian(rng), w = Gaussian(rng);
                double len = Math.Sqrt(x * x + y * y + z * z + w * w);
                x /= len; y /= len; z /= len; w /= len;
                double t = rng.NextDouble() * 100000.0;
                string format = formats[n % formats.Length];
                string json = string.Format(CultureInfo.InvariantCulture, "{{\"t\": {0}, \"q\": [{1}, {2}, {3}, {4}]}}",
                    t.ToString("R", CultureInfo.InvariantCulture), x.ToString(format, CultureInfo.InvariantCulture),
                    y.ToString(format, CultureInfo.InvariantCulture), z.ToString(format, CultureInfo.InvariantCulture),
                    w.ToString(format, CultureInfo.InvariantCulture));
                double pt;
                Quaternion pq;
                Assert.IsTrue(TryParse(json, out pt, out pq), json);
                Assert.AreEqual(t, pt, 1e-9, "t of " + json);
                double tolerance = format == "E3" ? 2e-3 : 1e-6;
                Assert.AreEqual(x, pq.x, tolerance, "x of " + json);
                Assert.AreEqual(y, pq.y, tolerance, "y of " + json);
                Assert.AreEqual(z, pq.z, tolerance, "z of " + json);
                Assert.AreEqual(w, pq.w, tolerance, "w of " + json);
                AssertUnitAndFinite(pq, json);
            }
        }

        static double Gaussian(Random rng)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        [Test]
        public void Parser_MutatedPackets_NeverThrow_AndOnlyAcceptSaneValues()
        {
            byte[][] seeds =
            {
                Utf8(J("{'t':1.5,'q':[0,0.6,0,0.8]}")),
                Utf8(J("{ 'seq': 7, 'note': 'a \\\"b\\\" {x}', 'q': [0, 0, 0, 1], 'nest': {'a': [1, {'b': null}]}, 't': 1e2 }"))
            };
            Random rng = new Random(77);
            int accepted = 0;
            for (int n = 0; n < 30000; n++)
            {
                byte[] seed = seeds[n % seeds.Length];
                byte[] work = new byte[seed.Length + 4];
                Array.Copy(seed, work, seed.Length);
                int length = seed.Length;
                int edits = rng.Next(1, 4);
                for (int e = 0; e < edits; e++)
                {
                    switch (rng.Next(4))
                    {
                        case 0: work[rng.Next(length)] = (byte)rng.Next(256); break;                       // overwrite a byte
                        case 1:                                                                              // delete a byte
                            if (length > 1)
                            {
                                int at = rng.Next(length);
                                Array.Copy(work, at + 1, work, at, length - at - 1);
                                length--;
                            }
                            break;
                        case 2: length = rng.Next(1, length + 1); break;                                     // truncate
                        default:                                                                             // swap two bytes
                        {
                            int a = rng.Next(length), b = rng.Next(length);
                            byte tmp = work[a]; work[a] = work[b]; work[b] = tmp;
                            break;
                        }
                    }
                }
                double t;
                Quaternion q;
                bool ok = ImuPacketParser.TryParse(work, length, out t, out q);
                if (ok)
                {
                    accepted++;
                    Assert.IsFalse(double.IsNaN(t) || double.IsInfinity(t), "accepted a non-finite t");
                    AssertUnitAndFinite(q, "accepted quaternion");
                }
            }
            Assert.IsTrue(accepted > 0, "the mutations should leave some packets valid (e.g. a changed digit)");
        }

        // ================================================================================================ latest-value slot

        static ImuSample Sample(long seq)
        {
            ImuSample s;
            s.Seq = seq;
            s.RecvSec = seq * 0.005;
            s.T = seq * 0.005 + 100.0;
            s.Q = new Quaternion(0f, 0f, 0f, 1f);
            return s;
        }

        [Test]
        public void Slot_StartsEmpty_AndOnlyTheNewestSurvives()
        {
            ImuSampleSlot slot = new ImuSampleSlot();
            ImuSample got = Sample(-1);
            Assert.IsFalse(slot.TryTake(ref got), "nothing published yet");
            Assert.AreEqual(-1L, got.Seq, "an empty take leaves the sample alone");

            slot.Publish(Sample(1));
            slot.Publish(Sample(2));
            slot.Publish(Sample(3));
            Assert.IsTrue(slot.TryTake(ref got));
            Assert.AreEqual(3L, got.Seq, "three publishes, one take: the newest wins");
            Assert.AreEqual(3 * 0.005 + 100.0, got.T, 0.0);

            Assert.IsFalse(slot.TryTake(ref got), "already taken");
            Assert.AreEqual(3L, got.Seq);

            slot.Publish(Sample(4));
            Assert.IsTrue(slot.TryTake(ref got));
            Assert.AreEqual(4L, got.Seq);
        }

        [Test]
        public void Slot_InterleavedPublishAndTake_NeverGoesBackwards()
        {
            ImuSampleSlot slot = new ImuSampleSlot();
            Random rng = new Random(5);
            long published = 0, lastTaken = 0;
            ImuSample got = Sample(0);
            for (int step = 0; step < 20000; step++)
            {
                if (rng.Next(3) != 0)
                {
                    published++;
                    slot.Publish(Sample(published));
                }
                else
                {
                    bool fresh = slot.TryTake(ref got);
                    Assert.AreEqual(published > lastTaken, fresh, "fresh exactly when something was published since the last take");
                    if (fresh)
                    {
                        Assert.AreEqual(published, got.Seq, "a take returns the newest value");
                        lastTaken = got.Seq;
                    }
                }
            }
        }

        // ================================================================================================ filter: state

        [Test]
        public void Filter_WithoutSamples_IsHeadLockedAndIdentity()
        {
            HeadPoseFilter f = NewFilter();
            Assert.IsFalse(f.HasSample);
            Assert.AreEqual(HeadTrackingState.HeadLocked, f.GetState(0.0));
            AssertSameRotation(f.GetRotation(5.0), Quaternion.identity, 1e-6, "no data");
            Assert.AreEqual(0f, f.GetPacketRateHz(5.0), 0f);
            Assert.IsFalse(f.Recenter(5.0), "nothing to recenter on");
        }

        [Test]
        public void Filter_SingleSample_IsTracking_WithoutPrediction()
        {
            HeadPoseFilter f = NewFilter();
            f.Push(10.0, 1.0, Euler(30, 0, 0));
            Assert.IsTrue(f.HasSample);
            Assert.AreEqual(HeadTrackingState.Tracking, f.GetState(10.0));
            AssertSameRotation(f.GetRotation(10.0), Euler(30, 0, 0), 0.001, "newest sample");
            AssertSameRotation(f.GetRotation(10.2), Euler(30, 0, 0), 0.001, "no velocity known from a single sample");
        }

        [Test]
        public void Filter_GoesStaleAfterStaleMs_AndRecovers()
        {
            HeadPoseFilter f = NewFilter();     // 250 ms
            f.Push(10.0, 1.0, Euler(30, 10, 0));
            Assert.AreEqual(HeadTrackingState.Tracking, f.GetState(10.2499));
            Assert.AreEqual(HeadTrackingState.Tracking, f.GetState(10.25), "exactly staleMs old still counts as fresh");
            Assert.AreEqual(HeadTrackingState.HeadLocked, f.GetState(10.2501));
            AssertSameRotation(f.GetRotation(10.2501), Quaternion.identity, 1e-6, "stale means identity");
            Assert.AreEqual(0f, f.GetPacketRateHz(10.2501), 0f);

            f.Push(11.0, 2.0, Euler(-40, 5, 0));
            Assert.AreEqual(HeadTrackingState.Tracking, f.GetState(11.0), "a new packet ends the head lock");
            AssertSameRotation(f.GetRotation(11.0), Euler(-40, 5, 0), 0.001, "back on the new sample, no leftover from before");
        }

        [Test]
        public void Filter_StaleMs_FollowsConfigure_AndIsClamped()
        {
            HeadPoseFilter f = NewFilter(staleMs: 1000);
            f.Push(10.0, 1.0, Quaternion.identity);
            Assert.AreEqual(HeadTrackingState.Tracking, f.GetState(10.9));
            Assert.AreEqual(HeadTrackingState.HeadLocked, f.GetState(11.1));
            f.Configure(100f, 10f, false, false, false);
            Assert.AreEqual(HeadTrackingState.HeadLocked, f.GetState(10.15), "the new, shorter limit applies to the sample already held");

            f.Configure(0f, 10f, false, false, false);                 // 0 would make everything stale: raised to MinStaleMs
            Assert.AreEqual(HeadTrackingState.Tracking, f.GetState(10.015));
            Assert.AreEqual(HeadTrackingState.HeadLocked, f.GetState(10.025));
            f.Configure(float.NaN, 10f, false, false, false);          // NaN falls back to the 250 ms default
            Assert.AreEqual(HeadTrackingState.Tracking, f.GetState(10.2));
            Assert.AreEqual(HeadTrackingState.HeadLocked, f.GetState(10.3));
        }

        [Test]
        public void Filter_Defaults_MatchTheFlagDefaults()
        {
            ImuFlags flags = new ImuFlags();
            Assert.AreEqual((double)flags.staleMs, (double)HeadPoseFilter.DefaultStaleMs, 0.0);
            Assert.AreEqual((double)flags.predictionMs, (double)HeadPoseFilter.DefaultPredictionMs, 0.0);
        }

        [Test]
        public void Filter_NonFiniteOrZeroInput_IsIgnored()
        {
            HeadPoseFilter f = NewFilter();
            f.Push(1.0, 1.0, new Quaternion(float.NaN, 0f, 0f, 1f));
            f.Push(1.0, 1.0, new Quaternion(0f, 0f, 0f, 0f));
            f.Push(1.0, 1.0, new Quaternion(float.PositiveInfinity, 0f, 0f, 1f));
            f.Push(double.NaN, 1.0, Quaternion.identity);
            f.Push(1.0, double.PositiveInfinity, Quaternion.identity);
            Assert.IsFalse(f.HasSample, "none of those is a usable sample");
            Assert.AreEqual(HeadTrackingState.HeadLocked, f.GetState(1.0));

            f.Push(1.0, 1.0, Euler(20, 0, 0));
            f.Push(1.001, 1.001, new Quaternion(float.NaN, 0f, 0f, 1f));
            AssertSameRotation(f.GetRotation(1.001), Euler(20, 0, 0), 0.001, "a bad packet does not disturb the held sample");
            Assert.AreEqual(HeadTrackingState.HeadLocked, f.GetState(double.NaN), "NaN time never reads as fresh");
        }

        [Test]
        public void Filter_NonUnitInput_IsNormalized()
        {
            HeadPoseFilter f = NewFilter();
            Quaternion q = Euler(30, 20, 10);
            f.Push(1.0, 1.0, new Quaternion(q.x * 3f, q.y * 3f, q.z * 3f, q.w * 3f));
            AssertSameRotation(f.GetRotation(1.0), q, 0.001, "scaled input");
            AssertUnitAndFinite(f.GetRotation(1.0), "output");
        }

        [Test]
        public void Filter_Reset_ForgetsSamples_ButKeepsTheRecenterOffset()
        {
            HeadPoseFilter f = NewFilter(0);
            f.Push(1.0, 1.0, Euler(40, 0, 0));
            Assert.IsTrue(f.Recenter(1.0));
            double offset = f.YawOffsetRadians;
            f.Reset();
            Assert.IsFalse(f.HasSample);
            Assert.AreEqual(HeadTrackingState.HeadLocked, f.GetState(1.0));
            Assert.AreEqual(offset, f.YawOffsetRadians, 0.0);
            f.Push(2.0, 1.0, Euler(40, 0, 0));
            Assert.AreEqual(0.0, HeadingDeg(f.GetRotation(2.0)), 0.01, "offset still applies after the reset");
        }

        // ================================================================================================ filter: prediction

        [Test]
        public void Filter_Prediction_ExtrapolatesByPredictionMs()
        {
            HeadPoseFilter f = NewFilter();     // 10 ms
            f.Push(10.000, 5.000, Euler(0, 0, 0));
            f.Push(10.005, 5.005, Euler(1, 0, 0));      // 1 degree in 5 ms = 200 deg/s
            AssertSameRotation(f.GetRotation(10.005), Euler(3, 0, 0), 0.01, "10 ms ahead at 200 deg/s adds 2 degrees");
        }

        [Test]
        public void Filter_Prediction_CoversTheAgeOfTheNewestSample()
        {
            HeadPoseFilter f = NewFilter();
            f.Push(10.000, 5.000, Euler(0, 0, 0));
            f.Push(10.005, 5.005, Euler(1, 0, 0));
            AssertSameRotation(f.GetRotation(10.0075), Euler(3.5, 0, 0), 0.01, "2.5 ms old + 10 ms ahead = 12.5 ms at 200 deg/s");
            AssertSameRotation(f.GetRotation(10.010), Euler(4, 0, 0), 0.01, "5 ms old + 10 ms ahead");
        }

        [Test]
        public void Filter_Prediction_ZeroMeansOff()
        {
            HeadPoseFilter f = NewFilter(predictionMs: 0);
            f.Push(10.000, 5.000, Euler(0, 0, 0));
            f.Push(10.005, 5.005, Euler(1, 0, 0));
            AssertSameRotation(f.GetRotation(10.005), Euler(1, 0, 0), 0.001, "no extrapolation");
            AssertSameRotation(f.GetRotation(10.1), Euler(1, 0, 0), 0.001, "not even for the age of the sample");
            f.Configure(250f, -5f, false, false, false);
            AssertSameRotation(f.GetRotation(10.005), Euler(1, 0, 0), 0.001, "a negative setting also means off");
        }

        [Test]
        public void Filter_Prediction_StandingStill_StaysPut()
        {
            HeadPoseFilter f = NewFilter();
            for (int i = 0; i < 10; i++) f.Push(10.0 + i * 0.005, 5.0 + i * 0.005, Euler(25, 10, 5));
            AssertSameRotation(f.GetRotation(10.045), Euler(25, 10, 5), 0.001, "no motion, no extrapolation");
        }

        [Test]
        public void Filter_Prediction_FollowsTheAxisOfMotion()
        {
            HeadPoseFilter f = NewFilter();
            f.Push(10.000, 5.000, Euler(0, 10, 0));
            f.Push(10.005, 5.005, Euler(0, 11, 0));
            AssertSameRotation(f.GetRotation(10.005), Euler(0, 13, 0), 0.01, "pitch 200 deg/s");

            HeadPoseFilter g = NewFilter();
            g.Push(10.000, 5.000, Euler(0, 0, -4));
            g.Push(10.005, 5.005, Euler(0, 0, -5));
            AssertSameRotation(g.GetRotation(10.005), Euler(0, 0, -7), 0.01, "roll -200 deg/s");

            HeadPoseFilter h = NewFilter();
            h.Push(10.000, 5.000, Euler(179, 0, 0));
            h.Push(10.005, 5.005, Euler(-179, 0, 0));    // 179 -> 181 degrees: +2 degrees the short way, 400 deg/s
            AssertSameRotation(h.GetRotation(10.005), Euler(-175, 0, 0), 0.01, "across the +/-180 seam: 181 + 4 = 185 = -175");
        }

        [Test]
        public void Filter_Prediction_ClampsAngularSpeed()
        {
            HeadPoseFilter f = NewFilter();
            f.Push(10.000, 5.000, Euler(0, 0, 0));
            f.Push(10.005, 5.005, Euler(90, 0, 0));     // 18000 deg/s: a glitch, not a head
            double extra = HeadPoseFilter.MaxAngularSpeedDegPerSec * 0.010;
            AssertSameRotation(f.GetRotation(10.005), Euler(90 + extra, 0, 0), 0.01, "clamped to the maximum speed");
        }

        [Test]
        public void Filter_Prediction_CapsTheLookAhead()
        {
            HeadPoseFilter f = NewFilter(staleMs: 5000);
            f.Push(10.000, 5.000, Euler(0, 0, 0));
            f.Push(10.005, 5.005, Euler(1, 0, 0));
            double capped = 1.0 + 200.0 * HeadPoseFilter.MaxHorizonSec;
            AssertSameRotation(f.GetRotation(12.005), Euler(capped, 0, 0), 0.01, "two seconds old: the look-ahead stops at MaxHorizonSec");
        }

        [Test]
        public void Filter_Prediction_NeedsTwoSamplesCloseEnoughInBridgeTime()
        {
            HeadPoseFilter gap = NewFilter();
            gap.Push(10.0, 1.0, Euler(0, 0, 0));
            gap.Push(10.2, 1.2, Euler(10, 0, 0));        // 0.2 s apart: a gap, not a velocity
            AssertSameRotation(gap.GetRotation(10.2), Euler(10, 0, 0), 0.001, "gap");

            HeadPoseFilter together = NewFilter();
            together.Push(10.0, 1.0000, Euler(0, 0, 0));
            together.Push(10.0, 1.0001, Euler(5, 0, 0)); // 0.1 ms apart: noise, not a velocity
            AssertSameRotation(together.GetRotation(10.0), Euler(5, 0, 0), 0.001, "simultaneous");
        }

        [Test]
        public void Filter_BridgeClockGoingBackwards_StartsOver()
        {
            HeadPoseFilter f = NewFilter();
            f.Push(10.000, 100.000, Euler(10, 0, 0));
            f.Push(10.005, 100.005, Euler(11, 0, 0));
            AssertSameRotation(f.GetRotation(10.005), Euler(13, 0, 0), 0.01, "moving at 200 deg/s");

            f.Push(10.010, 0.002, Euler(50, 0, 0));      // the bridge restarted: its clock went back to ~0
            AssertSameRotation(f.GetRotation(10.010), Euler(50, 0, 0), 0.001, "no velocity across the discontinuity");

            f.Push(10.015, 0.007, Euler(51, 0, 0));
            AssertSameRotation(f.GetRotation(10.015), Euler(53, 0, 0), 0.01, "predicting again after one more packet");
        }

        [Test]
        public void Filter_Prediction_UsesBridgeTimeNotArrivalTime()
        {
            // Packets arrive in a bunch (arrival 1 ms apart) but were taken 5 ms apart: the velocity must follow the bridge's clock.
            HeadPoseFilter f = NewFilter();
            f.Push(10.000, 5.000, Euler(0, 0, 0));
            f.Push(10.001, 5.005, Euler(1, 0, 0));
            AssertSameRotation(f.GetRotation(10.001), Euler(3, 0, 0), 0.01, "200 deg/s by the bridge's clock");
        }

        // ================================================================================================ filter: recenter

        [Test]
        public void Filter_Recenter_MakesTheCurrentHeadingForward_AndKeepsPitchAndRoll()
        {
            HeadPoseFilter f = NewFilter(predictionMs: 0);
            f.Push(10.0, 1.0, Euler(40, 20, 10));
            AssertSameRotation(f.GetRotation(10.0), Euler(40, 20, 10), 0.001, "before recentering");
            Assert.IsTrue(f.Recenter(10.0));
            AssertSameRotation(f.GetRotation(10.0), Euler(0, 20, 10), 0.01, "heading zero, pitch and roll kept");
            Assert.AreEqual(-40.0 * Deg, f.YawOffsetRadians, 1e-5);

            f.Push(10.005, 1.005, Euler(55, 20, 10));
            AssertSameRotation(f.GetRotation(10.005), Euler(15, 20, 10), 0.01, "15 degrees to the right of where recentering happened");
            f.Push(10.010, 1.010, Euler(-20, 5, -3));
            AssertSameRotation(f.GetRotation(10.010), Euler(-60, 5, -3), 0.01, "60 degrees to the left");
        }

        [Test]
        public void Filter_Recenter_IsAbsoluteNotCumulative()
        {
            HeadPoseFilter f = NewFilter(predictionMs: 0);
            f.Push(10.0, 1.0, Euler(40, 0, 0));
            Assert.IsTrue(f.Recenter(10.0));
            double first = f.YawOffsetRadians;
            Assert.IsTrue(f.Recenter(10.0));
            Assert.AreEqual(first, f.YawOffsetRadians, 1e-9, "same heading, same offset");

            f.Push(10.1, 1.1, Euler(55, 0, 0));
            Assert.IsTrue(f.Recenter(10.1));
            AssertSameRotation(f.GetRotation(10.1), Euler(0, 0, 0), 0.01, "recentering again faces forward again");
            Assert.AreEqual(-55.0 * Deg, f.YawOffsetRadians, 1e-5, "the offset is the heading itself, not a sum");
        }

        [Test]
        public void Filter_Recenter_NeedsFreshData_AndChangesNothingOtherwise()
        {
            HeadPoseFilter f = NewFilter(predictionMs: 0);
            Assert.IsFalse(f.Recenter(1.0));
            f.Push(10.0, 1.0, Euler(40, 0, 0));
            Assert.IsFalse(f.Recenter(11.0), "the sample is stale");
            Assert.AreEqual(0.0, f.YawOffsetRadians, 0.0);
            Assert.IsTrue(f.Recenter(10.1));
            f.ClearRecenter();
            Assert.AreEqual(0.0, f.YawOffsetRadians, 0.0);
            AssertSameRotation(f.GetRotation(10.1), Euler(40, 0, 0), 0.001, "cleared");
        }

        [Test]
        public void Filter_Recenter_UsesTheSampleNotThePrediction()
        {
            HeadPoseFilter f = NewFilter();     // prediction on
            f.Push(10.000, 5.000, Euler(0, 0, 0));
            f.Push(10.005, 5.005, Euler(1, 0, 0));
            Assert.IsTrue(f.Recenter(10.005));
            Assert.AreEqual(-1.0 * Deg, f.YawOffsetRadians, 1e-5, "the measured heading, 1 degree");
            Assert.AreEqual(2.0, HeadingDeg(f.GetRotation(10.005)), 0.01, "what remains is the prediction itself");
        }

        [Test]
        public void Filter_Recenter_LookingStraightDown_StaysFinite()
        {
            HeadPoseFilter f = NewFilter(predictionMs: 0);
            f.Push(10.0, 1.0, Euler(30, 90, 0));
            Assert.IsTrue(f.Recenter(10.0));
            Quaternion q = f.GetRotation(10.0);
            AssertUnitAndFinite(q, "straight down");
            Vector3 up = q * Vector3.up;      // with the view pointing down, the head's up vector shows the heading
            Assert.AreEqual(0.0, up.x, 5e-3);
            Assert.AreEqual(1.0, up.z, 5e-3);
            Vector3 forward = q * Vector3.forward;
            Assert.AreEqual(-1.0, forward.y, 5e-3, "still looking straight down");
        }

        [Test]
        public void Filter_YawOffset_CanBeRestoredAndRejectsNonFinite()
        {
            HeadPoseFilter f = NewFilter(predictionMs: 0);
            f.YawOffsetRadians = 0.5;
            Assert.AreEqual(0.5, f.YawOffsetRadians, 0.0);
            f.Push(10.0, 1.0, Euler(10, 0, 0));
            Assert.AreEqual(10.0 + 0.5 / Deg, HeadingDeg(f.GetRotation(10.0)), 0.01);
            f.YawOffsetRadians = double.NaN;
            Assert.AreEqual(0.0, f.YawOffsetRadians, 0.0);
        }

        // ================================================================================================ filter: inversion

        [Test]
        public void Filter_NoInvertFlags_PassTheRotationThroughUntouched()
        {
            HeadPoseFilter f = NewFilter(predictionMs: 0);
            f.Push(10.0, 1.0, Euler(30, 20, 10));
            AssertSameRotation(f.GetRotation(10.0), Euler(30, 20, 10), 0.001, "pass-through");
        }

        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(false, false, true)]
        [TestCase(true, true, false)]
        [TestCase(true, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, true, true)]
        public void Filter_InvertFlags_NegateOnlyTheirOwnAngle(bool invertYaw, bool invertPitch, bool invertRoll)
        {
            HeadPoseFilter f = NewFilter(predictionMs: 0, invertYaw: invertYaw, invertPitch: invertPitch, invertRoll: invertRoll);
            f.Push(10.0, 1.0, Euler(30, 20, 10));
            Quaternion expected = Euler(invertYaw ? -30 : 30, invertPitch ? -20 : 20, invertRoll ? -10 : 10);
            AssertSameRotation(f.GetRotation(10.0), expected, 0.01, "invertYaw=" + invertYaw + " invertPitch=" + invertPitch + " invertRoll=" + invertRoll);
        }

        [Test]
        public void Filter_InvertFlags_AreAppliedLive_NotBakedIntoSamples()
        {
            HeadPoseFilter f = NewFilter(predictionMs: 0);
            f.Push(10.0, 1.0, Euler(30, 20, 10));
            f.Configure(250f, 0f, true, false, false);
            AssertSameRotation(f.GetRotation(10.0), Euler(-30, 20, 10), 0.01, "flag turned on after the sample arrived");
            f.Configure(250f, 0f, false, false, false);
            AssertSameRotation(f.GetRotation(10.0), Euler(30, 20, 10), 0.01, "and off again");
        }

        [Test]
        public void Filter_InvertYaw_AlsoFlipsThePredictedMotion()
        {
            HeadPoseFilter f = NewFilter(invertYaw: true);
            f.Push(10.000, 5.000, Euler(0, 0, 0));
            f.Push(10.005, 5.005, Euler(1, 0, 0));
            AssertSameRotation(f.GetRotation(10.005), Euler(-3, 0, 0), 0.01, "yaw 3 degrees to the right becomes 3 degrees to the left");
        }

        [Test]
        public void Filter_Recenter_WorksInTheInvertedFrame()
        {
            HeadPoseFilter f = NewFilter(predictionMs: 0, invertYaw: true);
            f.Push(10.0, 1.0, Euler(30, 0, 0));          // inverted: heading -30
            AssertSameRotation(f.GetRotation(10.0), Euler(-30, 0, 0), 0.01, "inverted");
            Assert.IsTrue(f.Recenter(10.0));
            AssertSameRotation(f.GetRotation(10.0), Euler(0, 0, 0), 0.01, "recentered");
            f.Push(10.005, 1.005, Euler(40, 0, 0));      // inverted: -40, i.e. 10 further left than at recentering
            AssertSameRotation(f.GetRotation(10.005), Euler(-10, 0, 0), 0.01, "10 degrees further left");
        }

        [Test]
        public void Filter_InvertFlags_StayFiniteAtThePoles()
        {
            foreach (double pitch in new double[] { 90.0, -90.0, 89.9999999, -89.9999999 })
            {
                HeadPoseFilter f = NewFilter(predictionMs: 0, invertYaw: true, invertPitch: true, invertRoll: true);
                f.Push(10.0, 1.0, Euler(30, pitch, 15));
                AssertUnitAndFinite(f.GetRotation(10.0), "pitch " + pitch);
                Assert.IsTrue(f.Recenter(10.0), "pitch " + pitch);
                AssertUnitAndFinite(f.GetRotation(10.0), "recentered at pitch " + pitch);
            }
        }

        [Test]
        public void Filter_Euler_RoundTripsAcrossTheWholeRange()
        {
            Random rng = new Random(2026);
            for (int n = 0; n < 2000; n++)
            {
                double yaw = (rng.NextDouble() * 2 - 1) * 179.0, pitch = (rng.NextDouble() * 2 - 1) * 88.0, roll = (rng.NextDouble() * 2 - 1) * 179.0;
                HeadPoseFilter f = NewFilter(predictionMs: 0, invertYaw: true, invertPitch: true, invertRoll: true);
                f.Push(1.0, 1.0, Euler(yaw, pitch, roll));
                AssertSameRotation(f.GetRotation(1.0), Euler(-yaw, -pitch, -roll), 0.01, "yaw " + yaw + " pitch " + pitch + " roll " + roll);
            }
        }

        [Test]
        public void Helper_EulerConvention_MatchesUnityQuaternionEuler()
        {
            // The conventions of invertYaw / invertPitch / invertRoll are defined by Quaternion.Euler(pitch, yaw, roll).
            // Quaternion.Euler is a native call, so this only runs where the engine is available.
            double[][] cases = { new[] { 30.0, 20.0, 10.0 }, new[] { -120.0, 45.0, -60.0 }, new[] { 0.0, 0.0, 90.0 }, new[] { 179.0, -80.0, 5.0 } };
            foreach (double[] c in cases)
            {
                Quaternion engine;
                try { engine = Quaternion.Euler((float)c[1], (float)c[0], (float)c[2]); }
                catch (Exception) { Assert.Ignore("Quaternion.Euler needs the Unity engine"); return; }
                AssertSameRotation(Euler(c[0], c[1], c[2]), engine, 0.001, "yaw " + c[0] + " pitch " + c[1] + " roll " + c[2]);
            }
        }

        // ================================================================================================ filter: packet rate

        [Test]
        public void Filter_PacketRate_EveryPacketPushed()
        {
            HeadPoseFilter f = NewFilter();
            double now = 10.0;
            for (int i = 0; i < 400; i++)
            {
                now = 10.0 + i * 0.005;
                f.Push(now, i * 0.005, Quaternion.identity);
                if (i == 20) Assert.AreEqual(200.0, f.GetPacketRateHz(now), 1.0, "already right after 100 ms, no slow start");
            }
            Assert.AreEqual(200.0, f.GetPacketRateHz(now), 0.5);
        }

        [Test]
        public void Filter_PacketRate_CountsDatagramsNotCalls()
        {
            // 60 frames per second, each seeing only the newest of 3 or 4 packets: 200 packets per second in total.
            HeadPoseFilter f = NewFilter();
            double now = 20.0;
            int[] perFrame = { 3, 3, 4 };
            for (int frame = 0; frame < 180; frame++)
            {
                now = 20.0 + frame / 60.0;
                f.Push(now, now - 20.0, Quaternion.identity, perFrame[frame % 3]);
            }
            Assert.AreEqual(200.0, f.GetPacketRateHz(now), 2.0);
        }

        [Test]
        public void Filter_PacketRate_JitterDoesNotBiasIt()
        {
            Random rng = new Random(3);
            HeadPoseFilter f = NewFilter();
            double now = 0;
            double ideal = 0;
            for (int i = 0; i < 600; i++)
            {
                ideal = 30.0 + i * 0.005;
                now = ideal + (rng.NextDouble() - 0.5) * 0.004;     // arrivals scatter by +/- 2 ms; the order is kept
                f.Push(now, ideal, Quaternion.identity);
            }
            Assert.AreEqual(200.0, f.GetPacketRateHz(now), 1.5);
        }

        [Test]
        public void Filter_PacketRate_IsZeroWhenStale_AndIsNotDraggedDownAfterAGap()
        {
            HeadPoseFilter f = NewFilter();
            double now = 0;
            for (int i = 0; i < 200; i++)
            {
                now = 10.0 + i * 0.005;
                f.Push(now, i * 0.005, Quaternion.identity);
            }
            Assert.AreEqual(200.0, f.GetPacketRateHz(now + 0.1), 0.5, "still fresh 100 ms after the last packet");
            Assert.AreEqual(0f, f.GetPacketRateHz(now + 0.3), 0f, "stale");

            double resume = now + 3.0;           // three seconds of silence, then 100 packets per second
            for (int i = 0; i < 100; i++)
            {
                now = resume + i * 0.01;
                f.Push(now, 100.0 + i * 0.01, Quaternion.identity);
            }
            Assert.AreEqual(100.0, f.GetPacketRateHz(now), 1.5, "the gap is not averaged in");
        }

        [Test]
        public void Filter_PacketRate_SurvivesAStallOfTheCaller()
        {
            // The caller (main thread) hitches for 0.4 s while 200 packets per second keep arriving: that is one call
            // standing for 80 datagrams, and must not read as "the stream stopped".
            HeadPoseFilter f = NewFilter();
            double now = 0;
            for (int frame = 0; frame < 60; frame++)
            {
                now = 40.0 + frame / 60.0;
                f.Push(now, now - 40.0, Quaternion.identity, frame == 0 ? 1 : 3 + (frame % 3 == 0 ? 1 : 0));
            }
            now += 0.4;
            f.Push(now, now - 40.0, Quaternion.identity, 80);
            Assert.AreEqual(200.0, f.GetPacketRateHz(now), 8.0);
        }

        [Test]
        public void Filter_PacketRate_NeedsMoreThanOneSample()
        {
            HeadPoseFilter f = NewFilter();
            f.Push(10.0, 1.0, Quaternion.identity);
            Assert.AreEqual(0f, f.GetPacketRateHz(10.0), 0f);
            f.Push(10.0005, 1.0005, Quaternion.identity);
            Assert.AreEqual(0f, f.GetPacketRateHz(10.0005), 0f, "half a millisecond of history is not a rate");
        }
    }
}
