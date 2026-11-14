// FlagsTests.cs: EditMode tests for Runtime/Flags (FlagSet, FlagStore, FlagLoader, FlagJson).
// No Play Mode, no network, no sleeping: time is passed in as numbers and file timestamps are set explicitly.
//
// Three fixtures:
//   FlagsTests       parsing and merging; the real files flags.default.json and tools/flags/*.json are read from the repo
//                    (Application.dataPath is <repo>/unity/Assets in the Editor, so the repo root is two folders up)
//   FlagLoaderTests  loading, hot reload, throttling and the warn-once rules, with temp files; plus the FlagStore facade
//   FlagJsonTests    the little scanner used to repair nested objects
//
// The expected values for the repo files come from an independent oracle (regular expressions over the JSON text), not from
// the code under test.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Tests.EditMode
{
    internal static class FlagTestSupport
    {
        /// <summary>Single quotes become double quotes, so JSON in the tests stays readable.</summary>
        public static string J(string s) { return s.Replace('\'', '"'); }

        public static string RepoRoot
        {
            get { return Path.GetFullPath(Path.Combine(Application.dataPath, Path.Combine("..", ".."))); }
        }

        public static string DefaultsFile
        {
            get { return Path.Combine(Path.Combine(Application.dataPath, "StreamingAssets"), "flags.default.json"); }
        }

        public static string[] ProfileFiles()
        {
            string dir = Path.Combine(Path.Combine(RepoRoot, "tools"), "flags");
            Assert.IsTrue(Directory.Exists(dir), "expected the flag profiles in " + dir);
            List<string> files = new List<string>();
            foreach (string f in Directory.GetFiles(dir, "*.json"))
            {
                if (Path.GetFileName(f).StartsWith("secrets", StringComparison.OrdinalIgnoreCase)) continue;   // never read secrets
                files.Add(f);
            }
            files.Sort(StringComparer.Ordinal);
            return files.ToArray();
        }

        /// <summary>Compares two objects field by field (public instance fields, recursing into the flag groups).</summary>
        public static void AssertSameFields(object expected, object actual, string path)
        {
            List<string> problems = new List<string>();
            CollectDifferences(expected, actual, path, problems);
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems.ToArray()));
        }

        static void CollectDifferences(object expected, object actual, string path, List<string> problems)
        {
            foreach (FieldInfo f in expected.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object e = f.GetValue(expected);
                object a = f.GetValue(actual);
                string p = path + "." + f.Name;
                if (IsGroup(f.FieldType))
                {
                    if (a == null) problems.Add(p + " is null");
                    else CollectDifferences(e, a, p, problems);
                }
                else if (!SameValue(e, a))
                {
                    problems.Add(p + ": expected " + Show(e) + " but was " + Show(a));
                }
            }
        }

        public static bool IsGroup(Type t)
        {
            return t.IsClass && t != typeof(string) && !t.IsArray;
        }

        public static bool SameValue(object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a is float) return b is float && Math.Abs((float)a - (float)b) <= 1e-6f * Math.Max(1f, Math.Abs((float)a));
            Array aa = a as Array;
            if (aa != null)
            {
                Array bb = b as Array;
                if (bb == null || aa.Length != bb.Length) return false;
                for (int i = 0; i < aa.Length; i++)
                    if (!SameValue(aa.GetValue(i), bb.GetValue(i))) return false;
                return true;
            }
            return a.Equals(b);
        }

        public static string Show(object v)
        {
            if (v == null) return "null";
            Array arr = v as Array;
            if (arr != null)
            {
                StringBuilder sb = new StringBuilder("[");
                for (int i = 0; i < arr.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(Show(arr.GetValue(i)));
                }
                return sb.Append("]").ToString();
            }
            if (v is string) return "\"" + v + "\"";
            if (v is float) return ((float)v).ToString("R", CultureInfo.InvariantCulture);
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// An independent reader for the flag files: pulls a value for a key straight out of the JSON text with a regular
    /// expression. Good enough for these files because every field name is unique across the groups (a test guards that).
    /// </summary>
    internal static class FlagOracle
    {
        public static bool TryRaw(string json, string key, out string raw)
        {
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(\"(?:[^\"\\\\]|\\\\.)*\"|\\[[^\\]]*\\]|[^,}\\s\\]]+)");
            raw = m.Success ? m.Groups[1].Value : null;
            return m.Success;
        }

        public static object Convert(Type type, string raw)
        {
            if (type == typeof(string)) return Unquote(raw);
            if (type == typeof(bool)) return raw == "true";
            if (type == typeof(int)) return int.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(float)) return float.Parse(raw, CultureInfo.InvariantCulture);
            if (type == typeof(float[]))
            {
                string inner = raw.Substring(1, raw.Length - 2).Trim();
                if (inner.Length == 0) return new float[0];
                string[] parts = inner.Split(',');
                float[] result = new float[parts.Length];
                for (int i = 0; i < parts.Length; i++) result[i] = float.Parse(parts[i].Trim(), CultureInfo.InvariantCulture);
                return result;
            }
            if (type == typeof(string[]))
            {
                List<string> items = new List<string>();
                foreach (Match m in Regex.Matches(raw, "\"((?:[^\"\\\\]|\\\\.)*)\""))
                    items.Add(Unquote("\"" + m.Groups[1].Value + "\""));
                return items.ToArray();
            }
            throw new NotSupportedException("oracle cannot read a " + type.Name);
        }

        static string Unquote(string raw)
        {
            string s = raw.Substring(1, raw.Length - 2);
            return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\/", "/");
        }

        /// <summary>
        /// Checks every field of <paramref name="actual"/> (recursing into the groups): the value must be the one the highest
        /// layer that names the key says, or the built-in value when no layer names it.
        /// </summary>
        public static void Compare(object actual, object builtIn, string[] layersHighestFirst, string path, List<string> problems)
        {
            foreach (FieldInfo f in actual.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                string p = path + "." + f.Name;
                object a = f.GetValue(actual);
                if (FlagTestSupport.IsGroup(f.FieldType))
                {
                    if (a == null) problems.Add(p + " is null");
                    else Compare(a, f.GetValue(builtIn), layersHighestFirst, p, problems);
                    continue;
                }
                object expected = f.GetValue(builtIn);
                foreach (string layer in layersHighestFirst)
                {
                    string raw;
                    if (layer != null && TryRaw(layer, f.Name, out raw))
                    {
                        expected = Convert(f.FieldType, raw);
                        break;
                    }
                }
                if (!FlagTestSupport.SameValue(expected, a))
                    problems.Add(p + ": the file says " + FlagTestSupport.Show(expected) + " but Parse gave " + FlagTestSupport.Show(a));
            }
        }
    }

    // ================================================================================================================
    public class FlagsTests
    {
        static string J(string s) { return FlagTestSupport.J(s); }

        // ---- the real files ---------------------------------------------------------------------------------------------

        [Test]
        public void DefaultsFile_ParsesToTheValuesInTheFile()
        {
            string defaults = File.ReadAllText(FlagTestSupport.DefaultsFile);
            FlagSet parsed = FlagStore.Parse(defaults, null);

            List<string> problems = new List<string>();
            FlagOracle.Compare(parsed, new FlagSet(), new[] { defaults }, "flags.default.json", problems);
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems.ToArray()));
        }

        [Test]
        public void EveryProfile_OverDefaults_ParsesToTheValuesInItsFile()
        {
            string defaults = File.ReadAllText(FlagTestSupport.DefaultsFile);
            string[] profiles = FlagTestSupport.ProfileFiles();
            Assert.Greater(profiles.Length, 0, "no profiles found in tools/flags");

            List<string> problems = new List<string>();
            foreach (string path in profiles)
            {
                string profile = File.ReadAllText(path);
                FlagSet parsed = FlagStore.Parse(defaults, profile);
                // highest layer first: the profile, then the defaults file, then (inside Compare) the built-in value
                FlagOracle.Compare(parsed, new FlagSet(), new[] { profile, defaults }, Path.GetFileName(path), problems);
            }
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems.ToArray()));
        }

        [Test]
        public void EveryProfile_ParsesWithoutADefaultsFile_ToTheValuesInItsFile()
        {
            List<string> problems = new List<string>();
            foreach (string path in FlagTestSupport.ProfileFiles())
            {
                string profile = File.ReadAllText(path);
                FlagSet parsed = FlagStore.Parse(null, profile);
                FlagOracle.Compare(parsed, new FlagSet(), new[] { profile }, Path.GetFileName(path), problems);
            }
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems.ToArray()));
        }

        [Test]
        public void TheJudgeProfile_ShrinksTempoPresets_AndAddsChordsAndLargeText()
        {
            string defaults = File.ReadAllText(FlagTestSupport.DefaultsFile);
            string judgePath = Path.Combine(Path.Combine(Path.Combine(FlagTestSupport.RepoRoot, "tools"), "flags"), "judge.json");
            if (!File.Exists(judgePath)) Assert.Inconclusive("tools/flags/judge.json is not there");
            FlagSet judge = FlagStore.Parse(defaults, File.ReadAllText(judgePath));

            Assert.AreEqual("judge", judge.profile);
            Assert.AreEqual("headLocked", judge.anchorMode);
            Assert.AreEqual(2, judge.tempoPresets.Length, "an array that is present replaces the default array, it does not merge");
            Assert.AreEqual(0.5f, judge.tempoPresets[0], 1e-6f);
            Assert.AreEqual(0.75f, judge.tempoPresets[1], 1e-6f);
            Assert.IsNotNull(judge.chords);
            Assert.AreEqual(new[] { "Em" }, judge.chords);
            Assert.IsTrue(judge.largeText);
        }

        [Test]
        public void GroupsThatNoFileNames_KeepTheirBuiltInDefaults()
        {
            string defaults = File.ReadAllText(FlagTestSupport.DefaultsFile);
            List<string> texts = new List<string>();
            texts.Add(defaults);
            foreach (string path in FlagTestSupport.ProfileFiles()) texts.Add(File.ReadAllText(path));

            foreach (string text in texts)
            {
                FlagSet parsed = FlagStore.Parse(defaults, text);
                // the premise of this test: "render" and "imu" appear in no JSON file yet. When one does, that file's
                // values are checked by the oracle tests above, and this check simply skips the group.
                if (!text.Contains("\"render\"")) FlagTestSupport.AssertSameFields(new RenderFlags(), parsed.render, "render");
                if (!text.Contains("\"imu\"")) FlagTestSupport.AssertSameFields(new ImuFlags(), parsed.imu, "imu");
            }
        }

        [Test]
        public void FieldNames_AreUniqueAcrossTheGroups_SoTheOracleStaysHonest()
        {
            HashSet<string> seen = new HashSet<string>();
            List<Type> types = new List<Type> { typeof(FlagSet), typeof(AudioFlags), typeof(GhostFlags), typeof(RenderFlags), typeof(ImuFlags) };
            foreach (Type t in types)
                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (FlagTestSupport.IsGroup(f.FieldType)) continue;
                    Assert.IsTrue(seen.Add(f.Name), "the field name '" + f.Name + "' exists in two groups: make the oracle path-aware");
                }
        }

        // ---- the contract's built-in values ----------------------------------------------------------------------------

        [Test]
        public void BuiltInDefaults_AreTheContractValues()
        {
            FlagSet f = new FlagSet();
            Assert.AreEqual("dev", f.profile);
            Assert.AreEqual("aligned", f.anchorMode);
            Assert.AreEqual("mic", f.audioSource);
            Assert.AreEqual("fixtures-demo/learn-g.wav", f.wavReplayFile);
            Assert.IsTrue(f.waitModeDefault);
            Assert.AreEqual(new[] { 0.5f, 0.6f, 0.75f, 0.9f, 1.0f }, f.tempoPresets);
            Assert.IsFalse(f.leftHanded);
            Assert.AreEqual("hostScreen", f.spectator);
            Assert.IsFalse(f.debugOverlay);
            Assert.AreEqual("off", f.coach);
            Assert.IsNull(f.chords);
            Assert.IsFalse(f.largeText);

            Assert.AreEqual("MacBook", f.audio.device);
            Assert.AreEqual(12f, f.audio.noiseGateDb);
            Assert.AreEqual(45f, f.audio.pluckCentsTol);
            Assert.AreEqual(0.15f, f.audio.strumMinMargin);
            Assert.AreEqual(0f, f.audio.latencyOffsetMs);

            Assert.AreEqual("#4FC3F7", f.ghost.color);
            Assert.AreEqual(0.2f, f.ghost.fill);
            Assert.IsFalse(f.ghost.fingerColors);
            Assert.AreEqual(0.4f, f.ghost.fadeAfterSuccess);

            Assert.IsTrue(f.render.stereo);
            Assert.AreEqual(1, f.render.glassesDisplay);
            Assert.AreEqual(63f, f.render.ipdMm);
            Assert.AreEqual(52f, f.render.fovDiagonalDeg);
            Assert.AreEqual(1920, f.render.eyeWidth);
            Assert.AreEqual(1080, f.render.eyeHeight);
            Assert.IsFalse(f.render.swapEyes);
            Assert.AreEqual(0.1f, f.render.nearM);
            Assert.AreEqual(20f, f.render.farM);
            Assert.AreEqual(0f, f.render.convergenceM);

            Assert.AreEqual(47800, f.imu.udpPort);
            Assert.AreEqual(250f, f.imu.staleMs);
            Assert.AreEqual(10f, f.imu.predictionMs);
            Assert.IsFalse(f.imu.invertYaw);
            Assert.IsFalse(f.imu.invertPitch);
            Assert.IsFalse(f.imu.invertRoll);
        }

        [Test]
        public void Parse_WithNothingToRead_GivesTheBuiltInDefaults()
        {
            FlagTestSupport.AssertSameFields(new FlagSet(), FlagStore.Parse(null, null), "FlagSet");
            FlagTestSupport.AssertSameFields(new FlagSet(), FlagStore.Parse("", "   \n\t "), "FlagSet");
            FlagTestSupport.AssertSameFields(new FlagSet(), FlagStore.Parse("{}", "{}"), "FlagSet");
        }

        // ---- the override wins ------------------------------------------------------------------------------------------

        [Test]
        public void Override_ReplacesScalarsStringsAndBooleans()
        {
            FlagSet f = FlagStore.Parse(
                J("{ 'profile':'dev', 'anchorMode':'aligned', 'audioSource':'mic', 'leftHanded':false, 'debugOverlay':true }"),
                J("{ 'profile':'judge', 'anchorMode':'headLocked', 'audioSource':'wav', 'leftHanded':true, 'debugOverlay':false, 'largeText':true, 'coach':'on' }"));
            Assert.AreEqual("judge", f.profile);
            Assert.AreEqual("headLocked", f.anchorMode);
            Assert.AreEqual("wav", f.audioSource);
            Assert.IsTrue(f.leftHanded);
            Assert.IsFalse(f.debugOverlay);
            Assert.IsTrue(f.largeText);
            Assert.AreEqual("on", f.coach);
        }

        [Test]
        public void Override_KeysItDoesNotName_KeepTheDefaultsFileValues()
        {
            FlagSet f = FlagStore.Parse(
                J("{ 'profile':'fromDefaults', 'spectator':'fromDefaults', 'debugOverlay':true }"),
                J("{ 'anchorMode':'headLocked' }"));
            Assert.AreEqual("fromDefaults", f.profile);
            Assert.AreEqual("fromDefaults", f.spectator);
            Assert.IsTrue(f.debugOverlay);
            Assert.AreEqual("headLocked", f.anchorMode);
        }

        [Test]
        public void Override_OfAPartialGroup_ChangesOnlyTheFieldsItNames()
        {
            // the device name differs from the built-in on purpose: a merge that fell back to the built-ins would show it
            FlagSet f = FlagStore.Parse(
                J("{ 'audio': { 'device':'USB Mic', 'noiseGateDb':15, 'pluckCentsTol':50 } }"),
                J("{ 'audio': { 'noiseGateDb':20 } }"));
            Assert.AreEqual(20f, f.audio.noiseGateDb);
            Assert.AreEqual("USB Mic", f.audio.device, "a field the override does not name keeps the defaults-file value");
            Assert.AreEqual(50f, f.audio.pluckCentsTol);
            Assert.AreEqual(0.15f, f.audio.strumMinMargin, "a field no layer names keeps the built-in value");
        }

        [Test]
        public void Override_OfAPartialGroup_InEveryGroup()
        {
            FlagSet f = FlagStore.Parse(
                J("{ 'ghost': { 'color':'#112233', 'fill':0.5 }, 'render': { 'ipdMm':60, 'eyeWidth':1000 }, 'imu': { 'udpPort':5000, 'staleMs':100 } }"),
                J("{ 'ghost': { 'fill':0.9 }, 'render': { 'ipdMm':65 }, 'imu': { 'staleMs':400 } }"));
            Assert.AreEqual("#112233", f.ghost.color);
            Assert.AreEqual(0.9f, f.ghost.fill, 1e-6f);
            Assert.AreEqual(0.4f, f.ghost.fadeAfterSuccess, 1e-6f);
            Assert.AreEqual(65f, f.render.ipdMm);
            Assert.AreEqual(1000, f.render.eyeWidth);
            Assert.AreEqual(1080, f.render.eyeHeight);
            Assert.AreEqual(5000, f.imu.udpPort);
            Assert.AreEqual(400f, f.imu.staleMs);
        }

        [Test]
        public void Override_WithoutAGroup_LeavesTheDefaultsGroupAlone()
        {
            FlagSet f = FlagStore.Parse(
                J("{ 'ghost': { 'color':'#FF0000', 'fill':0.7 }, 'audio': { 'device':'Glasses' } }"),
                J("{ 'profile':'x' }"));
            Assert.AreEqual("#FF0000", f.ghost.color);
            Assert.AreEqual(0.7f, f.ghost.fill, 1e-6f);
            Assert.AreEqual("Glasses", f.audio.device);
            Assert.AreEqual("x", f.profile);
        }

        [Test]
        public void Override_CanSetTheGroupsThatNoFileNamesYet()
        {
            FlagSet f = FlagStore.Parse(null, J("{ 'render': { 'stereo':false, 'convergenceM':1.5 }, 'imu': { 'invertYaw':true, 'udpPort':47801 } }"));
            Assert.IsFalse(f.render.stereo);
            Assert.AreEqual(1.5f, f.render.convergenceM, 1e-6f);
            Assert.AreEqual(63f, f.render.ipdMm, "the rest of the group keeps its built-in value");
            Assert.IsTrue(f.imu.invertYaw);
            Assert.AreEqual(47801, f.imu.udpPort);
            Assert.IsFalse(f.imu.invertPitch);
        }

        [Test]
        public void Arrays_ArePresentReplacedWhole_AndAbsentLeftAlone()
        {
            string defaults = J("{ 'tempoPresets': [0.5, 0.6, 0.75, 0.9, 1.0] }");

            FlagSet shorter = FlagStore.Parse(defaults, J("{ 'tempoPresets': [0.5, 0.75] }"));
            Assert.AreEqual(new[] { 0.5f, 0.75f }, shorter.tempoPresets);

            FlagSet longer = FlagStore.Parse(defaults, J("{ 'tempoPresets': [0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0] }"));
            Assert.AreEqual(7, longer.tempoPresets.Length);
            Assert.AreEqual(1.0f, longer.tempoPresets[6], 1e-6f);

            FlagSet absent = FlagStore.Parse(defaults, J("{ 'profile': 'p' }"));
            Assert.AreEqual(new[] { 0.5f, 0.6f, 0.75f, 0.9f, 1.0f }, absent.tempoPresets);
        }

        [Test]
        public void TempoPresets_AreNeverEmpty()
        {
            FlagSet f = FlagStore.Parse(J("{ 'tempoPresets': [0.5] }"), J("{ 'tempoPresets': [] }"));
            Assert.AreEqual(new[] { 0.5f, 0.6f, 0.75f, 0.9f, 1.0f }, f.tempoPresets, "an empty list falls back to the built-in presets");
        }

        [Test]
        public void Chords_AreNullUntilALayerNamesThem()
        {
            Assert.IsNull(FlagStore.Parse(null, J("{ 'profile':'x' }")).chords);
            Assert.AreEqual(new[] { "G", "Em" }, FlagStore.Parse(null, J("{ 'chords': ['G', 'Em'] }")).chords);
            Assert.AreEqual(new[] { "Em" }, FlagStore.Parse(J("{ 'chords': ['G', 'Em', 'C'] }"), J("{ 'chords': ['Em'] }")).chords);
            Assert.AreEqual(new[] { "G", "Em", "C" }, FlagStore.Parse(J("{ 'chords': ['G', 'Em', 'C'] }"), J("{ 'profile':'x' }")).chords);
        }

        [Test]
        public void Parse_ReturnsIndependentInstances()
        {
            FlagSet a = FlagStore.Parse(null, null);
            FlagSet b = FlagStore.Parse(null, null);
            Assert.AreNotSame(a, b);
            Assert.AreNotSame(a.audio, b.audio);
            Assert.AreNotSame(a.tempoPresets, b.tempoPresets);
            a.tempoPresets[0] = 9f;
            a.audio.device = "changed";
            Assert.AreEqual(0.5f, b.tempoPresets[0]);
            Assert.AreEqual("MacBook", b.audio.device);
            Assert.AreEqual(0.5f, new FlagSet().tempoPresets[0]);
        }

        // ---- unknown keys and bad input -------------------------------------------------------------------------------

        [Test]
        public void UnknownKeys_AreIgnored()
        {
            string clean = J("{ 'profile':'a', 'ghost': { 'color':'#fff' } }");
            string noisy = J("{ 'profile':'a', 'mystery': 5, 'ghost': { 'color':'#fff', 'nope': true }, 'extraGroup': { 'x': [1, 2, { 'y': 3 }] }, 'list': ['a', 'b'] }");
            FlagTestSupport.AssertSameFields(FlagStore.Parse(null, clean), FlagStore.Parse(null, noisy), "FlagSet");
            FlagTestSupport.AssertSameFields(FlagStore.Parse(clean, null), FlagStore.Parse(noisy, null), "FlagSet");
        }

        [Test]
        public void Keys_AreCaseSensitive()
        {
            FlagSet f = FlagStore.Parse(null, J("{ 'Profile':'x', 'ANCHORMODE':'headLocked', 'Audio': { 'noiseGateDb': 99 } }"));
            Assert.AreEqual("dev", f.profile);
            Assert.AreEqual("aligned", f.anchorMode);
            Assert.AreEqual(12f, f.audio.noiseGateDb);
        }

        static readonly string[] BrokenJson =
        {
            "{ 'profile': 'x'",                 // truncated: what a half-written file looks like
            "{ 'profile': }",
            "{ 'profile' 'x' }",
            "{ 'audio': { 'noiseGateDb': 15 }",
            "not json at all",
            "{ 'profile': 'unterminated",
            "}{",
        };

        [Test]
        public void InvalidOverride_IsIgnoredAsAWhole_NeverHalfApplied()
        {
            string defaults = J("{ 'profile':'fromDefaults', 'anchorMode':'aligned' }");
            FlagSet reference = FlagStore.Parse(defaults, null);
            foreach (string broken in BrokenJson)
            {
                string text = J(broken);
                // a valid key BEFORE the damage must not leak through
                string withLeadingKey = text.StartsWith("{") ? J("{ 'anchorMode':'headLocked', ") + text.Substring(1) : text;
                FlagSet parsed = FlagStore.Parse(defaults, withLeadingKey);
                FlagTestSupport.AssertSameFields(reference, parsed, "override [" + withLeadingKey + "]");
            }
        }

        [Test]
        public void InvalidDefaults_FallBackToBuiltIns_ButTheOverrideStillApplies()
        {
            FlagSet f = FlagStore.Parse(J("{ 'profile': 'broken', 'ghost': "), J("{ 'anchorMode':'headLocked' }"));
            Assert.AreEqual("dev", f.profile, "the invalid defaults layer contributes nothing, not even its first key");
            Assert.AreEqual("headLocked", f.anchorMode);
        }

        [Test]
        public void BothInvalid_GiveTheBuiltInDefaults()
        {
            FlagTestSupport.AssertSameFields(new FlagSet(), FlagStore.Parse("{ nope", "also nope"), "FlagSet");
        }

        [Test]
        public void JsonThatIsNotAnObject_IsInvalid()
        {
            foreach (string text in new[] { "[1, 2]", "'text'", "42", "null", "true" })
            {
                string json = J(text);
                FlagSet set;
                string error;
                Assert.IsFalse(FlagStore.TryParse(null, json, out set, out error), json);
                StringAssert.StartsWith("override: ", error);
                FlagTestSupport.AssertSameFields(new FlagSet(), set, "result for [" + json + "]");
            }
        }

        [Test]
        public void BlankText_MeansNoLayer()
        {
            string defaults = J("{ 'profile':'fromDefaults' }");
            string bom = ((char)0xFEFF).ToString();
            foreach (string blank in new[] { null, "", " ", "\n\t  \r\n", bom })
            {
                FlagSet set;
                string error;
                Assert.IsTrue(FlagStore.TryParse(defaults, blank, out set, out error), "blank override [" + blank + "] is not an error");
                Assert.IsNull(error);
                Assert.AreEqual("fromDefaults", set.profile);
                Assert.IsTrue(FlagStore.TryParse(blank, defaults, out set, out error));
                Assert.AreEqual("fromDefaults", set.profile);
            }
        }

        [Test]
        public void ALeadingByteOrderMark_IsTolerated()
        {
            string bom = ((char)0xFEFF).ToString();
            Assert.AreEqual("withBom", FlagStore.Parse(null, bom + J("{ 'profile':'withBom' }")).profile);
        }

        [Test]
        public void TryParse_SaysWhichLayerFailed()
        {
            FlagSet set;
            string error;

            Assert.IsTrue(FlagStore.TryParse(J("{ 'profile':'a' }"), J("{ 'profile':'b' }"), out set, out error));
            Assert.IsNull(error);
            Assert.AreEqual("b", set.profile);

            Assert.IsFalse(FlagStore.TryParse(J("{ 'profile':'a' }"), "[1]", out set, out error));
            StringAssert.StartsWith("override: ", error);
            Assert.AreEqual("a", set.profile, "the result is still the best the valid layers give");

            Assert.IsFalse(FlagStore.TryParse("[1]", J("{ 'profile':'b' }"), out set, out error));
            StringAssert.StartsWith("defaults: ", error);
            Assert.AreEqual("b", set.profile);

            Assert.IsFalse(FlagStore.TryParse("[1]", "[2]", out set, out error));
            StringAssert.Contains("defaults: ", error);
            StringAssert.Contains("override: ", error);
            Assert.IsNotNull(set);
        }

        [Test]
        public void Parse_NeverThrows_AndNeverReturnsNull()
        {
            string[] nasty =
            {
                null, "", "{", "}", "[", "]", "\"", "\0", "{\0}", "{ 'a': [1, 2, ", "{ 'a': { 'b': { 'c': ",
                "{ 'profile': 1 }", "{ 'profile': [1] }", "{ 'tempoPresets': 'x' }", "{ 'audio': 5 }", "{ 'audio': null }",
                "{ 'audio': [] }", "{ 'chords': null }", "{ 'ghost': { 'fill': 'a lot' } }", new string('{', 40), new string('x', 100000),
            };
            foreach (string text in nasty)
            {
                string json = text == null ? null : J(text);
                FlagSet set = null;
                Assert.DoesNotThrow(delegate { set = FlagStore.Parse(json, json); });
                Assert.IsNotNull(set);
                Assert.IsNotNull(set.audio);
                Assert.IsNotNull(set.ghost);
                Assert.IsNotNull(set.render);
                Assert.IsNotNull(set.imu);
                Assert.IsNotNull(set.tempoPresets);
                Assert.Greater(set.tempoPresets.Length, 0);
            }
        }

        [Test]
        public void Parse_IsPure_NoChangedEvent_NoStateChange()
        {
            FlagStore.ResetForTests();
            FlagSet before = FlagStore.Current;
            int raised = 0;
            Action<FlagSet> handler = delegate { raised++; };
            FlagStore.Changed += handler;
            try
            {
                FlagStore.Parse(J("{ 'profile':'x' }"), J("{ 'profile':'y' }"));
            }
            finally
            {
                FlagStore.Changed -= handler;
            }
            Assert.AreEqual(0, raised);
            Assert.AreSame(before, FlagStore.Current);
            Assert.IsFalse(FlagStore.IsInitialized);
        }

        // ---- what the engine does (documented rules asserted; the undocumented ones printed) ----------------------------

        [Serializable] public class ProbeInner { public int a = 1; public int b = 2; }
        [Serializable] public class ProbeOuter { public ProbeInner inner = new ProbeInner(); public int[] list = { 1, 2, 3 }; public int top = 7; }

        static string Join(int[] a)
        {
            if (a == null) return "null";
            string[] parts = new string[a.Length];
            for (int i = 0; i < a.Length; i++) parts[i] = a[i].ToString(CultureInfo.InvariantCulture);
            return "[" + string.Join(",", parts) + "]";
        }

        /// <summary>
        /// Settles, in the real editor, the questions Unity's documentation leaves open about
        /// JsonUtility.FromJsonOverwrite. It ASSERTS what the loader relies on (a field absent from the JSON is left
        /// unchanged, nested objects and arrays included; a present array is replaced whole; broken JSON throws) and PRINTS
        /// what the loader does not depend on (a nested object that is present but names only some of its fields). Read the
        /// "[UGA] ... jsonutility_probe" line in the console, and correct the paragraph "WHAT UnityEngine.JsonUtility
        /// FromJsonOverwrite DOES" at the top of Runtime/Flags/FlagStore.cs if it disagrees.
        /// </summary>
        [Test]
        public void JsonUtilityProbe_PrintsHowNestedObjectsAndArraysMerge()
        {
            // documented: unknown keys are ignored, a field the JSON does not name is left unchanged
            ProbeOuter plain = new ProbeOuter();
            plain.top = 70;
            JsonUtility.FromJsonOverwrite(J("{ 'unknown': 1 }"), plain);
            Assert.AreEqual(70, plain.top);

            // (c) a nested object and an array that are absent stay as they were
            ProbeOuter absent = new ProbeOuter();
            absent.inner.b = 99;
            absent.list[0] = 11;
            JsonUtility.FromJsonOverwrite(J("{ 'top': 9 }"), absent);
            Assert.AreEqual(9, absent.top);
            Assert.AreEqual(1, absent.inner.a);
            Assert.AreEqual(99, absent.inner.b, "an absent nested object must be left unchanged");
            Assert.AreEqual(new[] { 11, 2, 3 }, absent.list, "an absent array must be left unchanged");

            // (b) a present array is replaced whole, whatever its length
            ProbeOuter fewer = new ProbeOuter();
            JsonUtility.FromJsonOverwrite(J("{ 'list': [9] }"), fewer);
            Assert.AreEqual(new[] { 9 }, fewer.list, "a shorter JSON array must not leave old items behind");
            ProbeOuter more = new ProbeOuter();
            JsonUtility.FromJsonOverwrite(J("{ 'list': [7, 8, 9, 10, 11] }"), more);
            Assert.AreEqual(new[] { 7, 8, 9, 10, 11 }, more.list);
            ProbeOuter empty = new ProbeOuter();
            JsonUtility.FromJsonOverwrite(J("{ 'list': [] }"), empty);
            string emptyResult = empty.list == null ? "null" : Join(empty.list);

            // (a) a nested object that is present but names only some of its fields: merge, or replace?
            ProbeOuter partial = new ProbeOuter();
            partial.inner.b = 99;
            ProbeInner before = partial.inner;
            JsonUtility.FromJsonOverwrite(J("{ 'inner': { 'a': 5 } }"), partial);
            string nested;
            if (partial.inner == null) nested = "null";
            else if (partial.inner.a != 5) nested = "not-applied";
            else if (partial.inner.b == 99) nested = "merge";
            else if (partial.inner.b == 2) nested = "replace-with-defaults";
            else nested = "other-b=" + partial.inner.b;
            nested += ReferenceEquals(before, partial.inner) ? "/same-instance" : "/new-instance";

            // broken JSON throws (the loader's "ignore an invalid file" rule is built on this)
            Exception thrown = Assert.Catch(delegate { JsonUtility.FromJsonOverwrite(J("{ 'top': "), new ProbeOuter()); });

            UGALog.Event("jsonutility_probe", "nested", nested, "arrays", "empty=" + emptyResult, "invalid", thrown.GetType().Name);
        }
    }

    // ================================================================================================================
    public class FlagLoaderTests
    {
        static string J(string s) { return FlagTestSupport.J(s); }

        string _dir;
        string _defaults;
        string _override;
        List<KeyValuePair<LogType, string>> _log;
        Action<LogType, string> _previousOutput;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "uga-flags-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _defaults = Path.Combine(_dir, "flags.default.json");
            _override = Path.Combine(_dir, "flags.json");
            _log = new List<KeyValuePair<LogType, string>>();
            _previousOutput = UGALog.Output;
            UGALog.Output = delegate(LogType type, string line) { lock (_log) _log.Add(new KeyValuePair<LogType, string>(type, line)); };
            FlagStore.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            UGALog.Output = _previousOutput;
            FlagStore.ResetForTests();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        // Writes a file and gives it a distinct, explicit last-write time, so a rewrite is never mistaken for "unchanged"
        // however coarse the file system's clock is.
        static void Put(string path, string text, int version)
        {
            File.WriteAllText(path, text);
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc).AddSeconds(version));
        }

        int Warnings()
        {
            int n = 0;
            lock (_log) foreach (KeyValuePair<LogType, string> e in _log) if (e.Key == LogType.Warning) n++;
            return n;
        }

        int Warnings(string evt)
        {
            int n = 0;
            lock (_log)
                foreach (KeyValuePair<LogType, string> e in _log)
                    if (e.Key == LogType.Warning && e.Value.Contains("\"evt\":\"" + evt + "\"")) n++;
            return n;
        }

        // ---- the first load ---------------------------------------------------------------------------------------------

        [Test]
        public void Initial_WithoutAnOverrideFile_UsesTheDefaultsFile_AndStaysQuiet()
        {
            Put(_defaults, J("{ 'profile':'dev', 'audio': { 'noiseGateDb': 13 } }"), 1);
            FlagLoader loader = new FlagLoader(_defaults, _override);
            loader.LoadInitial();
            Assert.AreEqual("dev", loader.Current.profile);
            Assert.AreEqual(13f, loader.Current.audio.noiseGateDb);
            Assert.IsTrue(loader.DefaultsApplied);
            Assert.IsFalse(loader.OverrideApplied);
            Assert.AreEqual(1, loader.LoadCount);
            Assert.AreEqual(0, Warnings(), "a missing override file is normal, not a problem");
        }

        [Test]
        public void Initial_LaysTheOverrideOverTheDefaults()
        {
            Put(_defaults, J("{ 'profile':'dev', 'anchorMode':'aligned', 'audio': { 'noiseGateDb': 13, 'device':'USB' } }"), 1);
            Put(_override, J("{ 'profile':'demo', 'audio': { 'noiseGateDb': 15 } }"), 1);
            FlagLoader loader = new FlagLoader(_defaults, _override);
            loader.LoadInitial();
            Assert.AreEqual("demo", loader.Current.profile);
            Assert.AreEqual("aligned", loader.Current.anchorMode);
            Assert.AreEqual(15f, loader.Current.audio.noiseGateDb);
            Assert.AreEqual("USB", loader.Current.audio.device);
            Assert.IsTrue(loader.OverrideApplied);
        }

        [Test]
        public void Initial_MissingDefaultsFile_WarnsOnce_AndUsesTheBuiltInDefaults()
        {
            FlagLoader loader = new FlagLoader(_defaults, _override);
            loader.LoadInitial();
            FlagTestSupport.AssertSameFields(new FlagSet(), loader.Current, "FlagSet");
            Assert.IsFalse(loader.DefaultsApplied);
            Assert.AreEqual(1, Warnings());
            Assert.AreEqual(1, Warnings("flags_defaults_missing"));
        }

        [Test]
        public void Initial_InvalidOverride_WarnsOnce_AndFallsBackToTheDefaultsFile()
        {
            Put(_defaults, J("{ 'profile':'fromDefaults' }"), 1);
            Put(_override, J("{ 'profile': }"), 1);
            FlagLoader loader = new FlagLoader(_defaults, _override);
            loader.LoadInitial();
            Assert.AreEqual("fromDefaults", loader.Current.profile);
            Assert.IsFalse(loader.OverrideApplied);
            Assert.AreEqual(1, Warnings());
            Assert.AreEqual(1, Warnings("flags_invalid"));
        }

        [Test]
        public void Initial_InvalidDefaults_WarnsOnce_UsesBuiltIns_AndStillAppliesTheOverride()
        {
            Put(_defaults, "[1]", 1);
            Put(_override, J("{ 'anchorMode':'headLocked' }"), 1);
            FlagLoader loader = new FlagLoader(_defaults, _override);
            loader.LoadInitial();
            Assert.AreEqual("dev", loader.Current.profile);
            Assert.AreEqual("headLocked", loader.Current.anchorMode);
            Assert.IsFalse(loader.DefaultsApplied);
            Assert.IsTrue(loader.OverrideApplied);
            Assert.AreEqual(1, Warnings());
        }

        [Test]
        public void Initial_CurrentIsNeverNull_EvenBeforeLoading()
        {
            FlagLoader loader = new FlagLoader(_defaults, _override);
            Assert.IsNotNull(loader.Current);
            Assert.AreEqual("dev", loader.Current.profile);
            Assert.AreEqual(0, loader.LoadCount);
        }

        [Test]
        public void Initial_NullPaths_DoNotThrow()
        {
            FlagLoader loader = new FlagLoader(null, null);
            Assert.DoesNotThrow(delegate { loader.LoadInitial(); });
            Assert.IsFalse(loader.Poll(0));
            FlagTestSupport.AssertSameFields(new FlagSet(), loader.Current, "FlagSet");
        }

        // ---- polling --------------------------------------------------------------------------------------------------

        [Test]
        public void Poll_LooksAtTheFileAtMostOncePerInterval()
        {
            Put(_defaults, J("{ 'profile':'dev' }"), 1);
            Put(_override, J("{ 'profile':'one' }"), 1);
            FlagLoader loader = new FlagLoader(_defaults, _override);
            loader.LoadInitial();
            Assert.AreEqual("one", loader.Current.profile);

            Put(_override, J("{ 'profile':'two' }"), 2);
            Assert.IsTrue(loader.Poll(0), "the first Poll after loading looks straight away");
            Assert.AreEqual("two", loader.Current.profile);

            Put(_override, J("{ 'profile':'three' }"), 3);
            Assert.IsFalse(loader.Poll(1), "inside the interval: no look, so the change is not seen yet");
            Assert.IsFalse(loader.Poll(100));
            Assert.IsFalse(loader.Poll(FlagLoader.PollIntervalMs - 1));
            Assert.AreEqual("two", loader.Current.profile);
            Assert.IsTrue(loader.Poll(FlagLoader.PollIntervalMs), "one interval after the last look: it looks again");
            Assert.AreEqual("three", loader.Current.profile);
        }

        [Test]
        public void Poll_IntervalIsAtLeastFourLooksASecond_AndNoMoreThanOneSecondOfLatency()
        {
            Assert.LessOrEqual(FlagLoader.PollIntervalMs, 250, "at most ~4 looks per second means an interval of at least 250 ms");
            Assert.GreaterOrEqual(FlagLoader.PollIntervalMs, 200);
            Assert.Less(FlagLoader.PollIntervalMs, 1000, "a reload must happen within 1 s of a change");
        }

        [Test]
        public void Poll_ReturnsFalse_WhileNothingChanges()
        {
            Put(_defaults, J("{ 'profile':'dev' }"), 1);
            Put(_override, J("{ 'profile':'one' }"), 1);
            FlagLoader loader = new FlagLoader(_defaults, _override);
            loader.LoadInitial();
            FlagSet first = loader.Current;
            for (long t = 0; t < 5000; t += 100) Assert.IsFalse(loader.Poll(t));
            Assert.AreSame(first, loader.Current);
            Assert.AreEqual(1, loader.LoadCount);
        }

        [Test]
        public void Poll_PublishesANewInstance_ForEveryReload()
        {
            Put(_override, J("{ 'profile':'one' }"), 1);
            FlagLoader loader = new FlagLoader(_defaults, _override);
            loader.LoadInitial();
            FlagSet one = loader.Current;
            Put(_override, J("{ 'profile':'two' }"), 2);
            Assert.IsTrue(loader.Poll(0));
            Assert.AreNotSame(one, loader.Current);
            Assert.AreEqual("one", one.profile, "a snapshot a consumer kept is never modified");
            Assert.AreEqual("two", loader.Current.profile);
        }

        [Test]
        public void Poll_SameSizeRewrite_IsDetectedByTheTimestamp()
        {
            Put(_override, J("{ 'profile':'aaa' }"), 1);
            FlagLoader loader = new FlagLoader(_defaults, _override);
            loader.LoadInitial();
            Put(_override, J("{ 'profile':'bbb' }"), 2);          // same length, different time
            Assert.IsTrue(loader.Poll(0));
            Assert.AreEqual("bbb", loader.Current.profile);
        }

        [Test]
        public void Poll_ARewriteWithTheSameTimestampButAnotherLength_IsDetectedByTheLength()
        {
            Put(_override, J("{ 'profile':'a' }"), 1);
            FlagLoader loader = new FlagLoader(_defaults, _override);
            loader.LoadInitial();
            Put(_override, J("{ 'profile':'longer' }"), 1);       // same explicit timestamp
            Assert.IsTrue(loader.Poll(0));
            Assert.AreEqual("longer", loader.Current.profile);
        }

        [Test]
        public void Poll_ClockGoingBackwards_StillLooks()
        {
            Put(_override, J("{ 'profile':'one' }"), 1);
            FlagLoader loader = new FlagLoader(_defaults, _override);
            loader.LoadInitial();
            Assert.IsFalse(loader.Poll(10000));
            Put(_override, J("{ 'profile':'two' }"), 2);
            Assert.IsTrue(loader.Poll(5), "negative elapsed time must not freeze the polling");
            Assert.AreEqual("two", loader.Current.profile);
        }

        [Test]
        public void Poll_InvalidJson_KeepsThePreviousValues_AndWarnsOncePerDistinctError()
        {
            Put(_defaults, J("{ 'profile':'dev' }"), 1);
            Put(_override, J("{ 'profile':'one', 'audio': { 'noiseGateDb': 30 } }"), 1);
            FlagLoader loader = new FlagLoader(_defaults, _override);
            loader.LoadInitial();
            Assert.AreEqual(0, Warnings());
            long t = 0;

            Put(_override, "[1]", 2);                                    // error A: not an object
            Assert.IsFalse(loader.Poll(t += 300));
            Assert.AreEqual("one", loader.Current.profile, "the previous values stay in force");
            Assert.AreEqual(30f, loader.Current.audio.noiseGateDb);
            Assert.AreEqual(1, Warnings());

            Assert.IsFalse(loader.Poll(t += 300));                       // the same unchanged file, looked at again: silent
            Assert.AreEqual(1, Warnings());

            Put(_override, "[2]", 3);                                    // different bytes, same error: still one warning
            Assert.IsFalse(loader.Poll(t += 300));
            Assert.AreEqual(1, Warnings());

            Put(_override, J("{ 'profile': }"), 4);                      // a different error: one more warning
            Assert.IsFalse(loader.Poll(t += 300));
            Assert.AreEqual(2, Warnings());
            Assert.AreEqual("one", loader.Current.profile);

            Put(_override, J("{ 'profile':'two' }"), 5);                 // fixed: loads, and the episode is over
            Assert.IsTrue(loader.Poll(t += 300));
            Assert.AreEqual("two", loader.Current.profile);
            Assert.AreEqual(2, Warnings());

            Put(_override, "[1]", 6);                                    // broken again: a new episode warns again
            Assert.IsFalse(loader.Poll(t += 300));
            Assert.AreEqual(3, Warnings());
            Assert.AreEqual("two", loader.Current.profile);
        }

        [Test]
        public void Poll_AHalfWrittenFile_KeepsThePreviousValues_ThenLoadsTheFinishedFile()
        {
            Put(_override, J("{ 'profile':'one' }"), 1);
            FlagLoader loader = new FlagLoader(_defaults, _override);
            loader.LoadInitial();

            Put(_override, J("{ 'profile': 'two', 'anchorMode': 'headLo"), 2);   // cp is mid-write
            Assert.IsFalse(loader.Poll(0));
            Assert.AreEqual("one", loader.Current.profile);

            Put(_override, J("{ 'profile': 'two', 'anchorMode': 'headLocked' }"), 3);   // cp is done
            Assert.IsTrue(loader.Poll(300));
            Assert.AreEqual("two", loader.Current.profile);
            Assert.AreEqual("headLocked", loader.Current.anchorMode);
        }

        [Test]
        public void Poll_AnEmptyFile_IsIgnoredWithoutAWarning_UntilContentArrives()
        {
            Put(_override, J("{ 'profile':'one' }"), 1);
            FlagLoader loader = new FlagLoader(_defaults, _override);
            loader.LoadInitial();

            Put(_override, "", 2);                                        // `cp` truncates first
            Assert.IsFalse(loader.Poll(0));
            Assert.AreEqual("one", loader.Current.profile);
            Assert.AreEqual(0, Warnings());
            Assert.IsFalse(loader.Poll(300), "an unchanged empty file is not read again and again");

            Put(_override, J("{ 'profile':'two' }"), 3);
            Assert.IsTrue(loader.Poll(600));
            Assert.AreEqual("two", loader.Current.profile);
        }

        [Test]
        public void Poll_AnEmptyObject_MeansNoOverrides()
        {
            Put(_defaults, J("{ 'profile':'dev', 'anchorMode':'aligned' }"), 1);
            Put(_override, J("{ 'profile':'judge', 'anchorMode':'headLocked' }"), 1);
            FlagLoader loader = new FlagLoader(_defaults, _override);
            loader.LoadInitial();
            Assert.AreEqual("headLocked", loader.Current.anchorMode);
            Put(_override, "{}", 2);
            Assert.IsTrue(loader.Poll(0));
            Assert.AreEqual("dev", loader.Current.profile);
            Assert.AreEqual("aligned", loader.Current.anchorMode);
        }

        [Test]
        public void Poll_ADeletedFile_KeepsThePreviousValues_WarnsOnce_AndLoadsWhenItReturns()
        {
            Put(_override, J("{ 'profile':'one' }"), 1);
            FlagLoader loader = new FlagLoader(_defaults, _override);
            loader.LoadInitial();

            File.Delete(_override);
            Assert.IsFalse(loader.Poll(0));
            Assert.AreEqual("one", loader.Current.profile);
            Assert.AreEqual(1, Warnings());
            Assert.AreEqual(1, Warnings("flags_override_missing"));
            Assert.IsFalse(loader.Poll(300));
            Assert.IsFalse(loader.Poll(600));
            Assert.AreEqual(1, Warnings(), "still gone: no further warnings");

            Put(_override, J("{ 'profile':'two' }"), 5);
            Assert.IsTrue(loader.Poll(900));
            Assert.AreEqual("two", loader.Current.profile);
        }

        [Test]
        public void Poll_AnOverrideCreatedAfterStart_IsLoaded()
        {
            Put(_defaults, J("{ 'profile':'dev' }"), 1);
            FlagLoader loader = new FlagLoader(_defaults, _override);
            loader.LoadInitial();
            Assert.IsFalse(loader.Poll(0), "no file yet: nothing to do, and nothing to complain about");
            Assert.AreEqual(0, Warnings());

            Put(_override, J("{ 'profile':'demo' }"), 2);
            Assert.IsTrue(loader.Poll(300));
            Assert.AreEqual("demo", loader.Current.profile);
            Assert.IsTrue(loader.OverrideApplied);
        }

        [Test]
        public void Poll_SwitchingProfile_DropsTheValuesTheNewProfileLeavesOut()
        {
            string defaults = J("{ 'profile':'dev', 'anchorMode':'aligned', 'tempoPresets':[0.5,0.6,0.75,0.9,1.0] }");
            string judge = J("{ 'profile':'judge', 'anchorMode':'headLocked', 'tempoPresets':[0.5,0.75], 'chords':['Em'], 'largeText':true }");
            string dev = J("{ 'profile':'dev' }");
            Put(_defaults, defaults, 1);
            Put(_override, judge, 1);
            FlagLoader loader = new FlagLoader(_defaults, _override);
            loader.LoadInitial();
            Assert.AreEqual(new[] { "Em" }, loader.Current.chords);
            Assert.IsTrue(loader.Current.largeText);
            Assert.AreEqual(2, loader.Current.tempoPresets.Length);

            Put(_override, dev, 2);
            Assert.IsTrue(loader.Poll(0));
            Assert.AreEqual("dev", loader.Current.profile);
            Assert.AreEqual("aligned", loader.Current.anchorMode);
            Assert.IsNull(loader.Current.chords, "chords came from the judge profile and must not outlive it");
            Assert.IsFalse(loader.Current.largeText);
            Assert.AreEqual(5, loader.Current.tempoPresets.Length);
        }

        [Test]
        public void Poll_ReadsTheRealProfiles_WhenTheyAreSwitchedOneAfterAnother()
        {
            string defaults = File.ReadAllText(FlagTestSupport.DefaultsFile);
            Put(_defaults, defaults, 1);
            FlagLoader loader = new FlagLoader(_defaults, _override);
            loader.LoadInitial();
            long t = 0;
            int version = 1;
            foreach (string path in FlagTestSupport.ProfileFiles())
            {
                string text = File.ReadAllText(path);
                Put(_override, text, ++version);
                Assert.IsTrue(loader.Poll(t += 300), Path.GetFileName(path) + " should have been picked up");
                List<string> problems = new List<string>();
                FlagOracle.Compare(loader.Current, new FlagSet(), new[] { text, defaults }, Path.GetFileName(path), problems);
                Assert.AreEqual(0, problems.Count, string.Join("\n", problems.ToArray()));
            }
            Assert.AreEqual(0, Warnings());
        }

        // ---- the FlagStore facade -------------------------------------------------------------------------------------

        [Test]
        public void Store_Current_IsNeverNull_BeforeInitialize()
        {
            Assert.IsNotNull(FlagStore.Current);
            Assert.AreEqual("dev", FlagStore.Current.profile);
            Assert.IsFalse(FlagStore.IsInitialized);
        }

        [Test]
        public void Store_Poll_BeforeInitialize_DoesNothing()
        {
            int raised = 0;
            Action<FlagSet> handler = delegate { raised++; };
            FlagStore.Changed += handler;
            try
            {
                Assert.DoesNotThrow(delegate { FlagStore.Poll(); });
            }
            finally
            {
                FlagStore.Changed -= handler;
            }
            Assert.AreEqual(0, raised);
        }

        [Test]
        public void Store_Initialize_RaisesChangedOnce_WithCurrent()
        {
            Put(_defaults, J("{ 'profile':'fromFile' }"), 1);
            List<FlagSet> seen = new List<FlagSet>();
            Action<FlagSet> handler = delegate(FlagSet s) { seen.Add(s); };
            FlagStore.Changed += handler;
            try
            {
                FlagStore.Initialize(_defaults, _override);
            }
            finally
            {
                FlagStore.Changed -= handler;
            }
            Assert.AreEqual(1, seen.Count);
            Assert.AreSame(FlagStore.Current, seen[0]);
            Assert.AreEqual("fromFile", FlagStore.Current.profile);
            Assert.IsTrue(FlagStore.IsInitialized);
            Assert.AreEqual(_defaults, FlagStore.DefaultsPath);
            Assert.AreEqual(_override, FlagStore.OverridePath);
        }

        // FlagStore.Poll() throttles with a real clock, but the first Poll() after Initialize() always looks at the file at
        // once, so these two tests need neither sleeping nor a fake clock (the throttle itself is tested on FlagLoader above).
        [Test]
        public void Store_Poll_WithoutAChange_RaisesNothing()
        {
            Put(_defaults, J("{ 'profile':'dev' }"), 1);
            Put(_override, J("{ 'profile':'one' }"), 1);
            FlagStore.Initialize(_defaults, _override);
            FlagSet before = FlagStore.Current;
            int raised = 0;
            Action<FlagSet> handler = delegate { raised++; };
            FlagStore.Changed += handler;
            try
            {
                FlagStore.Poll();
            }
            finally
            {
                FlagStore.Changed -= handler;
            }
            Assert.AreEqual(0, raised);
            Assert.AreSame(before, FlagStore.Current);
        }

        [Test]
        public void Store_Poll_RaisesChanged_WhenTheOverrideChanged()
        {
            Put(_defaults, J("{ 'profile':'dev' }"), 1);
            Put(_override, J("{ 'profile':'one' }"), 1);
            FlagStore.Initialize(_defaults, _override);
            FlagSet one = FlagStore.Current;
            Assert.AreEqual("one", one.profile);

            List<FlagSet> seen = new List<FlagSet>();
            Action<FlagSet> handler = delegate(FlagSet s) { seen.Add(s); };
            FlagStore.Changed += handler;
            try
            {
                Put(_override, J("{ 'profile':'two' }"), 2);
                FlagStore.Poll();
            }
            finally
            {
                FlagStore.Changed -= handler;
            }
            Assert.AreEqual(1, seen.Count);
            Assert.AreEqual("two", seen[0].profile);
            Assert.AreSame(seen[0], FlagStore.Current);
            Assert.AreNotSame(one, FlagStore.Current);
            Assert.AreEqual("one", one.profile, "the snapshot taken before the reload is untouched");
        }

        [Test]
        public void Store_Changed_ASubscriberThatThrows_DoesNotStopTheOthers_OrTheFrame()
        {
            Put(_defaults, J("{ 'profile':'dev' }"), 1);
            int secondCalls = 0;
            Action<FlagSet> bad = delegate { throw new InvalidOperationException("subscriber bug"); };
            Action<FlagSet> good = delegate { secondCalls++; };
            FlagStore.Changed += bad;
            FlagStore.Changed += good;
            try
            {
                Assert.DoesNotThrow(delegate { FlagStore.Initialize(_defaults, _override); });
            }
            finally
            {
                FlagStore.Changed -= bad;
                FlagStore.Changed -= good;
            }
            Assert.AreEqual(1, secondCalls);
            bool errorLogged = false;
            lock (_log) foreach (KeyValuePair<LogType, string> e in _log) if (e.Key == LogType.Error && e.Value.Contains("subscriber bug")) errorLogged = true;
            Assert.IsTrue(errorLogged, "the failing subscriber is reported through UGALog.Error");
        }

        [Test]
        public void Store_ResetForTests_ReturnsToTheBuiltInDefaults()
        {
            Put(_defaults, J("{ 'profile':'fromFile' }"), 1);
            FlagStore.Initialize(_defaults, _override);
            Assert.AreEqual("fromFile", FlagStore.Current.profile);
            FlagStore.ResetForTests();
            Assert.AreEqual("dev", FlagStore.Current.profile);
            Assert.IsFalse(FlagStore.IsInitialized);
        }
    }

    // ================================================================================================================
    public class FlagJsonTests
    {
        static string J(string s) { return FlagTestSupport.J(s); }

        [Test]
        public void FindsATopLevelObject_IncludingNestedBracesBracketsAndStrings()
        {
            string text = J("{ 'a': 1, 'audio': { 'x': { 'y': [1, 2, '}'] }, 'z': 'q' }, 'b': 2 }");
            string found;
            Assert.IsTrue(FlagJson.TryGetObject(text, "audio", out found));
            Assert.AreEqual(J("{ 'x': { 'y': [1, 2, '}'] }, 'z': 'q' }"), found);
        }

        [Test]
        public void AKeyNestedDeeper_IsNotATopLevelMatch()
        {
            string text = J("{ 'other': { 'audio': { 'n': 1 } }, 'audio': { 'n': 2 } }");
            string found;
            Assert.IsTrue(FlagJson.TryGetObject(text, "audio", out found));
            Assert.AreEqual(J("{ 'n': 2 }"), found);
        }

        [Test]
        public void QuotesAndBracesInsideStrings_DoNotConfuseIt()
        {
            string text = J("{ 's': 'a \\\" } { [ \\\\', 'audio': { 'n': 3 } }");
            string found;
            Assert.IsTrue(FlagJson.TryGetObject(text, "audio", out found));
            Assert.AreEqual(J("{ 'n': 3 }"), found);
        }

        [Test]
        public void WhitespaceAndNewlines_AreSkipped()
        {
            string text = "  {\r\n\t\"a\" :\n 1 ,\r\n \"audio\"\t:\r\n{ \"n\" : 4 }\n}\n";
            string found;
            Assert.IsTrue(FlagJson.TryGetObject(text, "audio", out found));
            Assert.AreEqual("{ \"n\" : 4 }", found);
        }

        [Test]
        public void AbsentKey_OrAValueThatIsNotAnObject_IsFalse()
        {
            string found;
            Assert.IsFalse(FlagJson.TryGetObject(J("{ 'a': 1 }"), "audio", out found));
            Assert.IsNull(found);
            Assert.IsFalse(FlagJson.TryGetObject(J("{ 'audio': null }"), "audio", out found));
            Assert.IsFalse(FlagJson.TryGetObject(J("{ 'audio': 5 }"), "audio", out found));
            Assert.IsFalse(FlagJson.TryGetObject(J("{ 'audio': [ { 'n': 1 } ] }"), "audio", out found));
            Assert.IsFalse(FlagJson.TryGetObject(J("{ 'audio': 'text' }"), "audio", out found));
            Assert.IsFalse(FlagJson.TryGetObject(J("{ 'audio': true }"), "audio", out found));
        }

        [Test]
        public void KeysMustMatchExactly()
        {
            string found;
            Assert.IsFalse(FlagJson.TryGetObject(J("{ 'audioX': { 'n': 1 } }"), "audio", out found), "a longer key is not a match");
            Assert.IsFalse(FlagJson.TryGetObject(J("{ 'aud': { 'n': 1 } }"), "audio", out found), "a shorter key is not a match");
            Assert.IsFalse(FlagJson.TryGetObject(J("{ 'Audio': { 'n': 1 } }"), "audio", out found), "keys are case-sensitive");
            Assert.IsFalse(FlagJson.TryGetObject(J("{ 'a\\u0075dio': { 'n': 1 } }"), "audio", out found), "a key spelled with an escape never matches");
            Assert.IsTrue(FlagJson.TryGetObject(J("{ 'audioX': { 'n': 1 }, 'audio': { 'n': 2 } }"), "audio", out found));
            Assert.AreEqual(J("{ 'n': 2 }"), found);
        }

        [Test]
        public void MalformedText_NeverThrows_AndIsNotFound()
        {
            string[] broken = { null, "", " ", "{", "{ 'audio'", "{ 'audio':", "{ 'audio': {", "{ 'audio': { 'n': 1 ", "[", "[ { 'audio': {} } ]", "'audio'", "{ 'x': 'unterminated", "{ 5: 6 }", "{ 'a' 'b' }" };
            foreach (string text in broken)
            {
                string found;
                string json = text == null ? null : J(text);
                bool result = true;
                Assert.DoesNotThrow(delegate { result = FlagJson.TryGetObject(json, "audio", out found); });
                Assert.IsFalse(result, "[" + json + "]");
            }
            string none;
            Assert.IsFalse(FlagJson.TryGetObject(J("{ 'audio': {} }"), null, out none));
        }

        [Test]
        public void AnEmptyObject_IsFound()
        {
            string found;
            Assert.IsTrue(FlagJson.TryGetObject(J("{ 'audio': {} }"), "audio", out found));
            Assert.AreEqual("{}", found);
        }
    }
}
