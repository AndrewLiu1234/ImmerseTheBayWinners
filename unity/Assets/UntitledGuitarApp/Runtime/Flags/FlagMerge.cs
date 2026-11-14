// FlagMerge.cs: the pure layering logic behind FlagStore.Parse and FlagLoader. The rules it implements are written down in
// FlagStore.cs ("MERGE RULES"); this file is how they are kept whatever UnityEngine.JsonUtility does with nested objects.
//
// UnityEngine.JsonUtility.FromJsonOverwrite(json, obj): "If a field of the object is not present in the JSON representation,
// that field will be left unchanged." (scripting reference, 2021.3) and "you can 'patch' objects by deliberately overwriting
// them with JSON that only contains a small subset of fields" (manual, JSON Serialization). It "can be used from a worker
// thread" too. Neither page says what happens to a nested object that IS present but lists only some of its fields, or to an
// array, so this file does not depend on either:
//   * every layer is applied to ONE FlagSet that already holds the lower layers;
//   * after FromJsonOverwrite, each group object (audio, ghost, render, imu) is checked by reference. If the engine kept the
//     instance (merged in place) nothing more happens. If the engine swapped it for a new one, the original instance is put
//     back and the group's own JSON text is applied to it directly, which is a top-level overwrite and therefore documented.
// https://docs.unity3d.com/2021.3/Documentation/ScriptReference/JsonUtility.FromJsonOverwrite.html
// https://docs.unity3d.com/2021.3/Documentation/Manual/JSONSerialization.html
using System;
using UnityEngine;

namespace UntitledGuitarApp.Runtime.Flags
{
    internal static class FlagMerge
    {
        /// <summary>True for null, empty and whitespace-only text (and a lone byte-order mark): "there is no layer".</summary>
        public static bool IsBlank(string json)
        {
            if (json == null) return true;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (!char.IsWhiteSpace(c) && c != (char)0xFEFF) return false;
            }
            return true;
        }

        /// <summary>
        /// Applies one JSON layer on top of <paramref name="set"/>. Throws (ArgumentException from the engine, or ours) when the
        /// text is not a JSON object. After a throw <paramref name="set"/> may be half-applied: callers discard it.
        /// </summary>
        public static void Apply(FlagSet set, string json)
        {
            json = TrimStart(json);
            if (json.Length == 0 || json[0] != '{') throw new ArgumentException("flag JSON must be an object ({...})");

            AudioFlags audio = set.audio;
            GhostFlags ghost = set.ghost;
            RenderFlags render = set.render;
            ImuFlags imu = set.imu;

            JsonUtility.FromJsonOverwrite(json, set);

            if (!ReferenceEquals(set.audio, audio)) { set.audio = audio; Patch(json, "audio", audio); }
            if (!ReferenceEquals(set.ghost, ghost)) { set.ghost = ghost; Patch(json, "ghost", ghost); }
            if (!ReferenceEquals(set.render, render)) { set.render = render; Patch(json, "render", render); }
            if (!ReferenceEquals(set.imu, imu)) { set.imu = imu; Patch(json, "imu", imu); }

            // Consumers index tempoPresets: it is never null or empty. (chords may be null: null = every chord.)
            if (set.tempoPresets == null || set.tempoPresets.Length == 0) set.tempoPresets = new FlagSet().tempoPresets;
        }

        /// <summary>
        /// Builds a FlagSet from the layers, lowest first: built-in defaults, then <paramref name="defaultsJson"/>, then
        /// <paramref name="overrideJson"/>. Never throws and never returns null. A blank layer is skipped; a layer that is
        /// not valid is skipped as a whole and its problem is returned in the matching error out parameter (otherwise null),
        /// so the result is always the best the valid layers can give.
        /// </summary>
        public static FlagSet Build(string defaultsJson, string overrideJson, out string defaultsError, out string overrideError)
        {
            defaultsError = null;
            overrideError = null;
            FlagSet set = new FlagSet();

            bool haveDefaults = !IsBlank(defaultsJson);
            if (haveDefaults)
            {
                try
                {
                    Apply(set, defaultsJson);
                }
                catch (Exception e)
                {
                    defaultsError = Describe(e);
                    set = new FlagSet();                        // discard the half-applied layer
                    haveDefaults = false;
                }
            }

            if (!IsBlank(overrideJson))
            {
                try
                {
                    Apply(set, overrideJson);
                }
                catch (Exception e)
                {
                    overrideError = Describe(e);
                    set = new FlagSet();                        // rebuild from the layers that are known to be good
                    if (haveDefaults)
                    {
                        try { Apply(set, defaultsJson); }
                        catch (Exception) { set = new FlagSet(); }
                    }
                }
            }
            return set;
        }

        static void Patch(string json, string key, object group)
        {
            string text;
            if (FlagJson.TryGetObject(json, key, out text)) JsonUtility.FromJsonOverwrite(text, group);
        }

        static string TrimStart(string s)
        {
            int i = 0;
            while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == (char)0xFEFF)) i++;
            return i == 0 ? s : s.Substring(i);
        }

        // One short line: the engine's messages can span lines and are shown inside log lines and warnings.
        static string Describe(Exception e)
        {
            string text = e.Message;
            if (string.IsNullOrEmpty(text)) text = e.GetType().Name;
            text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length > 300 ? text.Substring(0, 300) : text;
        }
    }
}
