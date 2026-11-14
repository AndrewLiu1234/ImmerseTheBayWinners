using System;
using System.Collections.Generic;

namespace UntitledGuitarApp.Core.Practice
{
    /// <summary>Tunables for the change drill. Each number says why it exists.</summary>
    public sealed class ChangeDrillConfig
    {
        /// <summary>A change is clean if the next target chord is heard within this long after the previous chord (3 s: a beginner's first G-C lands around 3-6 s; faster is a real gain).</summary>
        public double CleanWithinSec = 3.0;
        /// <summary>Rate window for changes-per-minute (60 s = the unit people quote).</summary>
        public double RateWindowSec = 60.0;
        /// <summary>A slow change repeated this many times in a row triggers the late-change tip once per run of slow changes (one slow change can be a fluke).</summary>
        public int SlowChangesBeforeTip = 2;
    }

    public enum ChangeOutcome { None, Clean, Slow, Wrong }

    /// <summary>
    /// State after each input. Target = the chord to play now. LateChangeKey is "lateChange" (tip: "Start moving one beat earlier",
    /// docs/MASTER_PLAN.md B6) when slow changes pile up, else null.
    /// </summary>
    public readonly struct ChangeDrillState
    {
        public readonly string Target, Previous;
        public readonly bool Started;
        public readonly int CleanChanges, SlowChanges, WrongStrums, Streak, BestStreak;
        public readonly double ChangesPerMinute, BestChangesPerMinute, LastChangeSec;
        public readonly ChangeOutcome Outcome;
        public readonly string LateChangeKey;
        public ChangeDrillState(string target, string previous, bool started, int clean, int slow, int wrong, int streak, int bestStreak,
            double cpm, double bestCpm, double lastChangeSec, ChangeOutcome outcome, string lateKey)
        {
            Target = target; Previous = previous; Started = started; CleanChanges = clean; SlowChanges = slow; WrongStrums = wrong;
            Streak = streak; BestStreak = bestStreak; ChangesPerMinute = cpm; BestChangesPerMinute = bestCpm;
            LastChangeSec = lastChangeSec; Outcome = outcome; LateChangeKey = lateKey;
        }
    }

    /// <summary>
    /// Alternates two target chords (e.g. G and C). First correct strum of chord A starts the drill (not a change); then each
    /// correct strum of the other chord is a change: clean if within CleanWithinSec of the previous correct chord, else slow.
    /// Re-strumming the chord you are already on is ignored; any other chord counts as wrong and breaks the streak.
    /// Time is seconds on a caller clock, non-decreasing; passed in, never read. Deterministic.
    /// </summary>
    public sealed class ChangeDrillSession
    {
        public static readonly string[] GtoC = { "G", "C" };
        public static readonly string[] AmToG = { "Am", "G" };
        public const string LateChangeKeyName = "lateChange";

        readonly string[] _pair;
        readonly ChangeDrillConfig _cfg;
        readonly Queue<double> _window = new Queue<double>();
        int _target, _clean, _slow, _wrong, _streak, _bestStreak, _slowRun;
        bool _started;
        double _lastChange = double.NaN, _lastNow = double.NegativeInfinity, _bestCpm;

        public ChangeDrillSession(string chordA, string chordB, ChangeDrillConfig config = null)
        {
            if (string.IsNullOrEmpty(chordA) || string.IsNullOrEmpty(chordB) || chordA == chordB)
                throw new ArgumentException("the drill needs two different chords");
            _pair = new[] { chordA, chordB };
            _cfg = config ?? new ChangeDrillConfig();
        }

        public ChangeDrillState State => Make(ChangeOutcome.None, null, _lastNow);

        /// <summary>A detected whole-chord strum (null/"" = unrecognised) at nowSec.</summary>
        public ChangeDrillState OnStrum(string chord, double nowSec)
        {
            if (double.IsNaN(nowSec) || double.IsInfinity(nowSec)) nowSec = double.IsInfinity(_lastNow) ? 0 : _lastNow;
            double now = Math.Max(nowSec, double.IsInfinity(_lastNow) ? nowSec : _lastNow);
            _lastNow = now;
            var outcome = ChangeOutcome.None; string key = null;

            if (!_started)
            {
                if (chord == _pair[0]) { _started = true; _target = 1; _lastChange = now; }
                else if (!string.IsNullOrEmpty(chord) && chord != _pair[0]) { _wrong++; outcome = ChangeOutcome.Wrong; }
                return Make(outcome, key, now);
            }
            if (chord == _pair[1 - _target]) return Make(ChangeOutcome.None, null, now); // still on the previous chord
            if (chord == _pair[_target])
            {
                double gap = now - _lastChange;
                if (gap <= _cfg.CleanWithinSec)
                {
                    _clean++; _streak++; _slowRun = 0; outcome = ChangeOutcome.Clean;
                    if (_streak > _bestStreak) _bestStreak = _streak;
                    _window.Enqueue(now);
                    Trim(now);
                    if (_window.Count > _bestCpm) _bestCpm = _window.Count;
                }
                else
                {
                    _slow++; _streak = 0; outcome = ChangeOutcome.Slow;
                    if (++_slowRun >= _cfg.SlowChangesBeforeTip) key = LateChangeKeyName;
                }
                _lastChange = now; _target = 1 - _target;
                return Make(outcome, key, now);
            }
            if (!string.IsNullOrEmpty(chord)) { _wrong++; _streak = 0; outcome = ChangeOutcome.Wrong; }
            return Make(outcome, null, now);
        }

        void Trim(double now)
        {
            while (_window.Count > 0 && _window.Peek() <= now - _cfg.RateWindowSec) _window.Dequeue();
        }

        ChangeDrillState Make(ChangeOutcome outcome, string key, double now)
        {
            if (!double.IsInfinity(now)) Trim(now);
            // Rate in changes per minute: changes inside the window scaled to 60 s.
            double cpm = _window.Count * 60.0 / _cfg.RateWindowSec;
            double best = _bestCpm * 60.0 / _cfg.RateWindowSec;
            return new ChangeDrillState(_pair[_target], _pair[1 - _target], _started, _clean, _slow, _wrong, _streak, _bestStreak,
                cpm, best, double.IsNaN(_lastChange) ? 0 : _lastChange, outcome, key);
        }
    }
}
