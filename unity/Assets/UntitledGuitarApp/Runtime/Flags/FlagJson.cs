// FlagJson.cs: a tiny scanner for the one thing UnityEngine.JsonUtility cannot do for us: hand back the raw text of one
// member of a JSON object. FlagMerge uses it to re-apply a flag group (audio, ghost, render, imu) to the group object itself
// in case the engine replaced that nested object instead of merging into it (see the merge rules in FlagStore.cs).
//
// It does not validate. It is only ever run on text that JsonUtility has already accepted as a whole, and it answers "not
// found" (false) for anything it does not understand, so the worst case is that a group is not patched a second time.
// Public only so the EditMode tests (a separate assembly) can exercise it.
using System;

namespace UntitledGuitarApp.Runtime.Flags
{
    public static class FlagJson
    {
        /// <summary>
        /// Finds the top-level member <paramref name="key"/> of the JSON object <paramref name="json"/>. When its value is
        /// itself an object, returns true and its exact text (braces included) in <paramref name="objectJson"/>. Returns false
        /// when the key is absent, its value is not an object (null, number, array...), or the text is not understood.
        /// Keys are compared as written: a key spelled with escape sequences never matches.
        /// </summary>
        public static bool TryGetObject(string json, string key, out string objectJson)
        {
            objectJson = null;
            if (json == null || key == null) return false;
            int n = json.Length;
            int i = SkipSpace(json, 0);
            if (i >= n || json[i] != '{') return false;
            i++;
            while (true)
            {
                i = SkipSpace(json, i);
                if (i >= n || json[i] != '"') return false;        // '}' = key not found; anything else = not understood
                int keyStart = i + 1;
                int keyEnd = SkipString(json, i);                   // index just after the closing quote
                if (keyEnd < 0) return false;
                bool match = keyEnd - 1 - keyStart == key.Length
                             && string.CompareOrdinal(json, keyStart, key, 0, key.Length) == 0;
                i = SkipSpace(json, keyEnd);
                if (i >= n || json[i] != ':') return false;
                i = SkipSpace(json, i + 1);
                if (i >= n) return false;
                int valueStart = i;
                i = SkipValue(json, i);
                if (i < 0) return false;
                if (match)
                {
                    if (json[valueStart] != '{') return false;      // present, but nothing to patch
                    objectJson = json.Substring(valueStart, i - valueStart);
                    return true;
                }
                i = SkipSpace(json, i);
                if (i < n && json[i] == ',') { i++; continue; }
                return false;                                       // '}' or garbage: not found
            }
        }

        static int SkipSpace(string s, int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c != ' ' && c != '\t' && c != '\r' && c != '\n') break;
                i++;
            }
            return i;
        }

        // s[i] is the opening quote. Returns the index just after the closing quote, or -1.
        static int SkipString(string s, int i)
        {
            i++;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\') { i += 2; continue; }
                if (c == '"') return i + 1;
                i++;
            }
            return -1;
        }

        // Returns the index just after the value that starts at s[i], or -1.
        static int SkipValue(string s, int i)
        {
            char c = s[i];
            if (c == '"') return SkipString(s, i);
            if (c == '{' || c == '[')
            {
                int depth = 0;
                for (; i < s.Length; i++)
                {
                    c = s[i];
                    if (c == '"')
                    {
                        i = SkipString(s, i);
                        if (i < 0) return -1;
                        i--;                                        // the loop's i++ lands just after the closing quote
                    }
                    else if (c == '{' || c == '[') depth++;
                    else if (c == '}' || c == ']')
                    {
                        depth--;
                        if (depth == 0) return i + 1;
                    }
                }
                return -1;
            }
            int j = i;                                              // number, true, false, null: up to the next delimiter
            while (j < s.Length)
            {
                c = s[j];
                if (c == ',' || c == '}' || c == ']' || c == ' ' || c == '\t' || c == '\r' || c == '\n') break;
                j++;
            }
            return j > i ? j : -1;
        }
    }
}
