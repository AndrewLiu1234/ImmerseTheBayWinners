// FlagStore.cs: the runtime flags. StreamingAssets/flags.default.json, with <persistentDataPath>/flags.json laid over it
// (that file is what scripts/use-flags.sh writes), hot-reloaded while the app runs.
//
// CONTRACT (SeamStubs.cs): Current, Changed, Initialize(), Poll() and Parse(defaultsJson, overrideJson) are used by other
// workers exactly as declared there. Members marked "addition" are extra.
//
// Usage: Bootstrap calls Initialize() once, then Poll() every frame (no allocation on the frames where it does not look).
// Read FlagStore.Current whenever you need a value, or cache it and refresh the cache in a Changed handler: every reload
// publishes a NEW FlagSet, so a reference you cached stays a consistent snapshot and never changes under you.
// Current may be read from any thread (the reference is published volatile, the object is never modified afterwards).
// Initialize(), Poll(), Changed and the file reads are main-thread; Parse() is pure and may run anywhere.
//
// ---------------------------------------------------------------------------------------------------------------------
// MERGE RULES (what Parse, Initialize and Poll guarantee; profile authors and consumers may rely on them)
//   1. Layers, lowest first: the built-in defaults (new FlagSet()), then flags.default.json, then flags.json.
//      Every (re)load rebuilds from scratch, so a profile that leaves a key out gets the layer below, never a value left
//      over from the previous profile (switching judge -> dev puts "chords" and "largeText" back to their defaults).
//   2. A key that is present replaces the value from the layers below; a key that is absent keeps it. Unknown keys are
//      ignored. Keys are case-sensitive (they are the C# field names in FlagSet.cs).
//   3. Groups (audio, ghost, render, imu) merge field by field: {"audio":{"noiseGateDb":20}} changes only that field. A group
//      that is missing from a layer leaves the group from the layers below untouched. Today no JSON file has "render" or
//      "imu"; those groups simply stay at their built-in values until a file names them.
//   4. Arrays (tempoPresets, chords) are replaced as a whole when present, even with fewer items than before, and are left
//      alone when absent. tempoPresets is never null or empty afterwards (an empty list falls back to the built-in presets);
//      chords may be null, which means "every chord".
//   5. A layer that is not valid JSON, or not a JSON object, is ignored as a whole (never half-applied). Blank text means
//      "no layer". Initialize() warns and carries on with the layers that did parse. Poll() keeps the previous values and
//      warns once per distinct problem; an EMPTY override file is ignored silently (a writer is mid-copy), so write {} to
//      mean "no overrides".
//
// WHAT UnityEngine.JsonUtility.FromJsonOverwrite DOES, AND WHAT IS ONLY ASSUMED (Unity 2021.3):
//   documented  "If a field of the object is not present in the JSON representation, that field will be left unchanged."
//               https://docs.unity3d.com/2021.3/Documentation/ScriptReference/JsonUtility.FromJsonOverwrite.html
//   documented  JSON values that match no field are ignored; "FromJsonOverwrite allocates GC memory only as necessary for
//               written fields (for example, strings and arrays)"; the string overload may be used from a worker thread.
//               https://docs.unity3d.com/2021.3/Documentation/Manual/JSONSerialization.html
//   NOT documented: what happens to (a) a nested object that is present but names only some of its fields, (b) an array
//               that is present, (c) a nested object or array that is absent. Assumed when this was written: (a) merges in
//               place, (b) is replaced whole (resized to the JSON length), (c) is left unchanged.
//   OBSERVED in Unity 2021.3.3f1 (Sat 2026-11-14 08:25 PT, headless EditMode run, 439/439 tests green, including the (b) and
//               (c) tests): FlagsTests.JsonUtilityProbe_PrintsHowNestedObjectsAndArraysMerge logged
//               {"evt":"jsonutility_probe","nested":"merge/same-instance","arrays":"empty=[]","invalid":"ArgumentException"}:
//               (a) a partial nested object merges into the SAME instance; an empty JSON array gives an empty array; broken
//               JSON throws ArgumentException.
//   This code does not depend on (a): FlagMerge checks each group object by reference after the engine call and, if the
//   engine swapped it for a new instance, puts the original back and re-applies that group's own JSON text to it (a
//   top-level overwrite, which IS documented). It does depend on (b) and (c); FlagsTests covers both with the real
//   profile files (judge.json shrinks tempoPresets from 5 items to 2 and adds chords).
// ---------------------------------------------------------------------------------------------------------------------
using System;
using System.IO;
using UnityEngine;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.Flags
{
    public static class FlagStore
    {
        /// <summary>addition: file name inside StreamingAssets.</summary>
        public const string DefaultsFileName = "flags.default.json";

        /// <summary>addition: file name inside Application.persistentDataPath (what scripts/use-flags.sh writes).</summary>
        public const string OverrideFileName = "flags.json";

        static volatile FlagSet s_current = new FlagSet();
        static FlagLoader s_loader;
        static long s_clockStart;

        // ---- the contract (SeamStubs.cs) -------------------------------------------------------------------------------

        /// <summary>Never null; the built-in defaults until Initialize() has run.</summary>
        public static FlagSet Current { get { return s_current; } }

        /// <summary>Raised on the main thread after every successful (re)load, with the new FlagSet.</summary>
        public static event Action<FlagSet> Changed;

        /// <summary>
        /// Loads StreamingAssets/flags.default.json, then lays &lt;persistentDataPath&gt;/flags.json over it, and raises
        /// Changed. Main thread. Calling it again reloads everything from disk.
        /// Application.streamingAssetsPath is an ordinary folder inside the macOS app, so System.IO reads it (Unity only rules
        /// System.IO out on Android and WebGL):
        /// https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Application-streamingAssetsPath.html
        /// Application.persistentDataPath on macOS is ~/Library/Application Support/&lt;company&gt;/&lt;product&gt;:
        /// https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Application-persistentDataPath.html
        /// </summary>
        public static void Initialize()
        {
            UGALog.Configure();
            Initialize(Path.Combine(Application.streamingAssetsPath, DefaultsFileName),
                       Path.Combine(Application.persistentDataPath, OverrideFileName));
        }

        /// <summary>
        /// Call every frame (Bootstrap does). Looks at the override file at most 4 times a second and, when it changed,
        /// reloads and raises Changed: within about 250 ms of the change. On every other frame it does a subtraction and
        /// a compare and allocates nothing. Does nothing before Initialize().
        /// </summary>
        public static void Poll()
        {
            FlagLoader loader = s_loader;
            if (loader == null) return;
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - s_clockStart;
            long nowMs = ticks * 1000L / System.Diagnostics.Stopwatch.Frequency;
            if (!loader.Poll(nowMs)) return;
            FlagSet set = loader.Current;
            s_current = set;
            UGALog.Event("flags_reloaded", "profile", set.profile, "override", loader.OverrideApplied);
            RaiseChanged(set);
        }

        /// <summary>
        /// Pure: builds a FlagSet from the two JSON texts with the merge rules above. Reads no file, logs nothing, touches no
        /// static state, never throws and never returns null: a null, empty or invalid text is skipped (see TryParse to learn
        /// about invalid text). Safe to call from any thread. This is what the tests (and other workers' tests) use.
        /// </summary>
        public static FlagSet Parse(string defaultsJson, string overrideJson)
        {
            string defaultsError, overrideError;
            return FlagMerge.Build(defaultsJson, overrideJson, out defaultsError, out overrideError);
        }

        // ---- additions --------------------------------------------------------------------------------------------------

        /// <summary>True after Initialize() has run.</summary>
        public static bool IsInitialized { get { return s_loader != null; } }

        /// <summary>True when Initialize() has not run (or a domain reload reset the statics).</summary>
        public static bool NeedsInitialization { get { return s_loader == null; } }

        /// <summary>
        /// Self-healing init: a domain reload (script recompile during Play Mode) resets statics to the built-in defaults
        /// while the restored scene objects keep running, silently disabling every flag override. Bootstrap.Update calls
        /// this every frame; it is a one-line no-op once initialized (STATUS 2026-11-14 camera hunt).
        /// </summary>
        public static void EnsureInitialized()
        {
            if (s_loader == null)
            {
                try { Initialize(); }
                catch (Exception e) { UGALog.Error("flags_init", e.ToString()); }
            }
        }

        /// <summary>The flags.default.json that was read, or null before Initialize().</summary>
        public static string DefaultsPath { get { FlagLoader l = s_loader; return l != null ? l.DefaultsPath : null; } }

        /// <summary>The flags.json that is watched, or null before Initialize().</summary>
        public static string OverridePath { get { FlagLoader l = s_loader; return l != null ? l.OverridePath : null; } }

        /// <summary>
        /// Like Parse, but says what was wrong. Returns true when both texts were usable (or blank). On false,
        /// <paramref name="result"/> is still the best FlagSet the valid layers give (never null) and
        /// <paramref name="error"/> names the layer ("defaults: ..." / "override: ...") and what the parser said.
        /// </summary>
        public static bool TryParse(string defaultsJson, string overrideJson, out FlagSet result, out string error)
        {
            string defaultsError, overrideError;
            result = FlagMerge.Build(defaultsJson, overrideJson, out defaultsError, out overrideError);
            if (defaultsError != null && overrideError != null) error = "defaults: " + defaultsError + "; override: " + overrideError;
            else if (defaultsError != null) error = "defaults: " + defaultsError;
            else if (overrideError != null) error = "override: " + overrideError;
            else error = null;
            return error == null;
        }

        /// <summary>
        /// Initialize() with explicit file paths (tests, tools). Does not touch Unity's log settings. Main thread.
        /// </summary>
        public static void Initialize(string defaultsPath, string overridePath)
        {
            FlagLoader loader = new FlagLoader(defaultsPath, overridePath);
            loader.LoadInitial();
            s_loader = loader;
            s_clockStart = System.Diagnostics.Stopwatch.GetTimestamp();
            FlagSet set = loader.Current;
            s_current = set;
            UGALog.Event("flags_loaded", "profile", set.profile, "defaults", loader.DefaultsApplied, "override", loader.OverrideApplied);
            RaiseChanged(set);
        }

        /// <summary>Back to "never initialized": stops watching and makes Current the built-in defaults. For tests.</summary>
        public static void ResetForTests()
        {
            s_loader = null;
            s_current = new FlagSet();
        }

        // Each handler runs in its own try/catch: one subscriber that throws must not stop the others, and must not take
        // down the frame (Poll runs inside Bootstrap's Update).
        static void RaiseChanged(FlagSet set)
        {
            Action<FlagSet> handlers = Changed;
            if (handlers == null) return;
            Delegate[] list = handlers.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                try
                {
                    ((Action<FlagSet>)list[i])(set);
                }
                catch (Exception e)
                {
                    UGALog.Error("flags_changed_handler", e.ToString());
                }
            }
        }
    }
}
