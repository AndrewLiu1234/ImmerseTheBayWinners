// FlagLoader.cs: reads the two flag files, builds the FlagSet from them and re-reads the override file when it changes.
// Instance-based with no statics and an explicit clock, so the EditMode tests can drive it with temp files and fake time;
// FlagStore owns the one real instance. Pure .NET plus FlagMerge (JsonUtility) and UGALog; no Unity object is touched, so it
// is safe to use from any single thread.
//
// What it guarantees (tests: FlagsTests.cs, class FlagLoaderTests):
//   * Poll(nowMs) looks at the override file at most once per PollIntervalMs (250 ms = 4 times a second). Every other call
//     is a subtraction and a compare: no file access, no allocation. A changed file is therefore picked up within about
//     250 ms, well inside the 1 s promise.
//   * "Changed" means the file's last-write time OR its length differs from the last look (macOS gives sub-second times;
//     Unity's Mono 6.13 passes them on, measured). The file is re-stat'ed through one reused FileInfo.Refresh().
//   * The whole FlagSet is rebuilt from the layers on every load, so switching profile never leaves values behind from the
//     previous profile.
//   * Warn-only failures. An invalid or unreadable override keeps the previous values and produces ONE UGALog.Warn per
//     distinct error until the next successful load. A file that disappears keeps the previous values (one warning). An
//     empty file is ignored without a warning: `cp` truncates before it writes, and the next look sees the real content.
//     (Write `{}` to mean "no overrides".)
//   * A missing or invalid flags.default.json is one warning; the built-in defaults (new FlagSet()) are used instead.
using System;
using System.Collections.Generic;
using System.IO;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.Flags
{
    public sealed class FlagLoader
    {
        /// <summary>Minimum time between two looks at the override file.</summary>
        public const int PollIntervalMs = 250;
        const int MaxRememberedWarnings = 32;

        readonly string _defaultsPath;
        readonly string _overridePath;
        readonly FileInfo _overrideFile;               // reused for every look: Refresh() re-stats it
        readonly HashSet<string> _warned = new HashSet<string>();

        string _defaultsJson;                          // text of flags.default.json, only when it parsed
        FlagSet _current = new FlagSet();
        long _lastCheckMs = long.MinValue / 2;         // so that the first Poll looks straight away
        bool _haveStamp;
        long _stampTicks;
        long _stampLength;
        bool _defaultsApplied;
        bool _overrideApplied;
        int _loadCount;

        public FlagLoader(string defaultsPath, string overridePath)
        {
            _defaultsPath = defaultsPath;
            _overridePath = overridePath;
            _overrideFile = string.IsNullOrEmpty(overridePath) ? null : new FileInfo(overridePath);
        }

        public string DefaultsPath { get { return _defaultsPath; } }
        public string OverridePath { get { return _overridePath; } }

        /// <summary>The FlagSet of the last successful load. Never null: built-in defaults until LoadInitial() has run.</summary>
        public FlagSet Current { get { return _current; } }

        /// <summary>True when flags.default.json was found and valid, and is part of Current.</summary>
        public bool DefaultsApplied { get { return _defaultsApplied; } }

        /// <summary>True when a valid, non-empty flags.json is part of Current.</summary>
        public bool OverrideApplied { get { return _overrideApplied; } }

        /// <summary>How many loads succeeded, the initial one included.</summary>
        public int LoadCount { get { return _loadCount; } }

        /// <summary>Reads both files and builds Current. Always produces a usable FlagSet; problems are warnings.</summary>
        public void LoadInitial()
        {
            _warned.Clear();
            string error;

            string defaultsText = null;
            if (string.IsNullOrEmpty(_defaultsPath) || !File.Exists(_defaultsPath))
                Warn("flags_defaults_missing", "flags.default.json not found at '" + _defaultsPath + "'; using the built-in defaults");
            else if (!TryRead(_defaultsPath, out defaultsText, out error))
                Warn("flags_defaults_unreadable", "cannot read '" + _defaultsPath + "': " + error + "; using the built-in defaults");

            string overrideText = null;
            _haveStamp = false;
            long ticks, length;
            if (Stat(out ticks, out length))
            {
                if (TryRead(_overridePath, out overrideText, out error))
                {
                    _haveStamp = true;
                    _stampTicks = ticks;
                    _stampLength = length;
                }
                else
                {
                    Warn("flags_override_unreadable", "cannot read '" + _overridePath + "': " + error + "; ignoring it for now");
                }
            }

            string defaultsError, overrideError;
            FlagSet set = FlagMerge.Build(defaultsText, overrideText, out defaultsError, out overrideError);
            if (defaultsError != null)
            {
                Warn("flags_invalid", "flags.default.json is not valid: " + defaultsError + "; using the built-in defaults");
                defaultsText = null;
            }
            if (overrideError != null)
                Warn("flags_invalid", "flags.json is not valid: " + overrideError + "; ignoring it");

            _defaultsJson = FlagMerge.IsBlank(defaultsText) ? null : defaultsText;
            _defaultsApplied = _defaultsJson != null;
            _overrideApplied = overrideError == null && !FlagMerge.IsBlank(overrideText);
            _current = set;
            _loadCount++;
        }

        /// <summary>
        /// Call every frame with a monotonic millisecond clock. Returns true when the override file changed and Current was
        /// replaced by a newly built FlagSet; false otherwise (not time to look yet, nothing changed, or the new content was
        /// unusable and the previous values were kept).
        /// </summary>
        public bool Poll(long nowMs)
        {
            long elapsed = nowMs - _lastCheckMs;
            if (elapsed >= 0 && elapsed < PollIntervalMs) return false;   // the common case: no file access, no allocation
            _lastCheckMs = nowMs;                                          // (a clock that went backwards looks right away)
            return CheckOverride();
        }

        bool CheckOverride()
        {
            long ticks, length;
            if (!Stat(out ticks, out length))
            {
                if (_haveStamp)
                {
                    _haveStamp = false;
                    Warn("flags_override_missing", "'" + _overridePath + "' disappeared; keeping the previous values");
                }
                return false;
            }
            if (_haveStamp && ticks == _stampTicks && length == _stampLength) return false;

            string text, error;
            if (!TryRead(_overridePath, out text, out error))
            {
                Warn("flags_override_unreadable", "cannot read '" + _overridePath + "': " + error + "; will look again");
                return false;                                              // stamp not recorded: the next look retries
            }
            _haveStamp = true;
            _stampTicks = ticks;
            _stampLength = length;

            if (FlagMerge.IsBlank(text)) return false;                     // a writer is between truncating and writing

            string defaultsError, overrideError;
            FlagSet set = FlagMerge.Build(_defaultsJson, text, out defaultsError, out overrideError);
            if (overrideError != null)
            {
                Warn("flags_invalid", "'" + _overridePath + "' is not valid: " + overrideError + "; keeping the previous values");
                return false;
            }
            _warned.Clear();                                               // a good load ends the episode: a new error warns again
            _current = set;
            _overrideApplied = true;
            _loadCount++;
            return true;
        }

        // One stat through the reused FileInfo. False = no such file (or it vanished while we looked).
        bool Stat(out long ticks, out long length)
        {
            ticks = 0;
            length = 0;
            if (_overrideFile == null) return false;
            try
            {
                _overrideFile.Refresh();
                if (!_overrideFile.Exists) return false;
                ticks = _overrideFile.LastWriteTimeUtc.Ticks;
                length = _overrideFile.Length;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        static bool TryRead(string path, out string text, out string error)
        {
            try
            {
                text = File.ReadAllText(path);                             // strips a UTF-8 byte-order mark
                error = null;
                return true;
            }
            catch (Exception e)
            {
                text = null;
                error = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        // One UGALog.Warn per distinct message until the next successful load.
        void Warn(string evt, string message)
        {
            if (!_warned.Add(message)) return;
            if (_warned.Count > MaxRememberedWarnings)
            {
                _warned.Clear();
                _warned.Add(message);
            }
            UGALog.Warn(evt, message);
        }
    }
}
