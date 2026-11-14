using System;
using System.Collections.Generic;
using UntitledGuitarApp.Core.Songs;

namespace UntitledGuitarApp.Core.Practice
{
    /// <summary>Tunables for play-along. Each number says why it exists.</summary>
    public sealed class PlayAlongConfig
    {
        /// <summary>Wait mode: the playhead holds at each chord change until the right chord is strummed (beginner-safe default).</summary>
        public bool WaitMode = true;
        /// <summary>Escape hatch: a hold longer than this auto-skips the chord so a stuck player is never trapped (8 s ≈ 4 bars at 60 bpm).</summary>
        public double HoldTimeoutSec = 8.0;
        /// <summary>Escape hatch: this many wrong strums while holding on one chord also auto-skip it (a beginner who keeps missing should move on).</summary>
        public int MaxWrongStrums = 6;
        /// <summary>A matching strum up to this long before a chord starts still counts for it (people change chords slightly early).</summary>
        public double EarlyWindowSec = 0.30;
    }

    public enum ChordOutcome { Pending, Hit, Missed, Skipped }

    /// <summary>Per-chord result, published when the playhead leaves the chord. ErrorSec = best strum time − chord start (NaN if none).</summary>
    public sealed class ChordResult
    {
        public int Index;
        public string Chord;
        public ChordOutcome Outcome;
        public double ErrorSec = double.NaN;
    }

    /// <summary>Output for the HUD: what to play now, what comes next and how long until the change.</summary>
    public readonly struct ChordTarget
    {
        public readonly int CurrentIndex, NextIndex;
        public readonly string Current, Next;
        public readonly double BeatsToChange, PositionSeconds;
        public readonly bool Holding, Finished;
        public ChordTarget(int ci, string cur, int ni, string next, double beats, double pos, bool holding, bool finished)
        { CurrentIndex = ci; Current = cur; NextIndex = ni; Next = next; BeatsToChange = beats; PositionSeconds = pos; Holding = holding; Finished = finished; }
    }

    /// <summary>
    /// Play-along over a SongTimeline. Wait mode or free-running; the best-timed matching strum per chord counts.
    /// Input: OnStrum(chord, timeSec) (detected whole-chord strum; null/"" or another chord = wrong). Time is seconds on a
    /// caller clock, non-decreasing; passed in, never read. Deterministic.
    /// </summary>
    public sealed class PlayAlongSession
    {
        readonly SongTimeline _tl;
        readonly PlayAlongConfig _cfg;
        readonly int _n;
        readonly bool[] _satisfied, _skipped, _done;
        readonly double[] _bestErr;
        readonly List<ChordResult> _results = new List<ChordResult>();

        double _pos, _lastNow, _holdStart;
        int _enter, _holdSpan = -1, _wrong;
        bool _started, _finished;

        public PlayAlongSession(SongTimeline timeline, PlayAlongConfig config = null)
        {
            _tl = timeline ?? throw new ArgumentNullException(nameof(timeline));
            _cfg = config ?? new PlayAlongConfig();
            _n = timeline.Spans.Count;
            _satisfied = new bool[_n]; _skipped = new bool[_n]; _done = new bool[_n]; _bestErr = new double[_n];
            for (int i = 0; i < _n; i++) _bestErr[i] = double.NaN;
        }

        public IReadOnlyList<ChordResult> Results => _results;
        public bool Finished => _finished;
        public int Hits { get; private set; }
        public int Missed { get; private set; }
        public int Skipped { get; private set; }
        public double MeanAbsErrorSec { get; private set; }
        double _absErrSum; int _errCount;

        public ChordTarget Start(double nowSec)
        {
            _started = true; _lastNow = nowSec; _pos = 0;
            if (_n == 0) _finished = true;
            return Advance(nowSec);
        }

        public ChordTarget Update(double nowSec) => _started ? Advance(nowSec) : Start(nowSec);

        /// <summary>A detected strum of <paramref name="chord"/> at nowSec.</summary>
        public ChordTarget OnStrum(string chord, double nowSec)
        {
            if (!_started) Start(nowSec);
            Advance(nowSec);
            if (_finished) return Target();
            double now = Math.Max(nowSec, _lastNow);

            if (_holdSpan >= 0)
            {
                if (chord == _tl.Spans[_holdSpan].Chord)
                {
                    Record(_holdSpan, now - _holdStart);
                    _satisfied[_holdSpan] = true;
                    _holdSpan = -1; _wrong = 0;
                    return Advance(now);
                }
                if (++_wrong >= _cfg.MaxWrongStrums) return Skip(now);
                return Target();
            }

            int best = -1; double bestAbs = double.MaxValue;
            int lo = Math.Max(0, _enter - 1), hi = Math.Min(_n - 1, _enter);
            for (int i = lo; i <= hi; i++)
            {
                var sp = _tl.Spans[i];
                if (_done[i] || sp.Chord != chord) continue;
                if (_pos < sp.StartSeconds - _cfg.EarlyWindowSec || _pos > sp.EndSeconds) continue;
                double a = Math.Abs(_pos - sp.StartSeconds);
                if (a < bestAbs) { bestAbs = a; best = i; }
            }
            if (best >= 0) { Record(best, _pos - _tl.Spans[best].StartSeconds); _satisfied[best] = true; }
            return Advance(now);
        }

        /// <summary>Escape hatch by hand (also used by the timeout): give up on the chord being held and move on.</summary>
        public ChordTarget Skip(double nowSec)
        {
            if (_holdSpan >= 0)
            {
                _skipped[_holdSpan] = true; _satisfied[_holdSpan] = true;
                _holdSpan = -1; _wrong = 0;
            }
            return Advance(nowSec);
        }

        void Record(int i, double err)
        {
            if (double.IsNaN(_bestErr[i]) || Math.Abs(err) < Math.Abs(_bestErr[i])) _bestErr[i] = err; // best-onset wins
        }

        ChordTarget Advance(double nowSec)
        {
            double now = Math.Max(nowSec, _lastNow);
            while (!_finished)
            {
                if (_holdSpan >= 0)
                {
                    if (now - _holdStart >= _cfg.HoldTimeoutSec)
                    {
                        _lastNow = _holdStart + _cfg.HoldTimeoutSec;
                        _skipped[_holdSpan] = true; _satisfied[_holdSpan] = true;
                        _holdSpan = -1; _wrong = 0;
                        continue;
                    }
                    _lastNow = now;
                    break;
                }
                double target = _pos + (now - _lastNow);
                bool held = false;
                while (_enter < _n && target >= _tl.Spans[_enter].StartSeconds)
                {
                    var sp = _tl.Spans[_enter];
                    if (_cfg.WaitMode && !_satisfied[_enter])
                    {
                        _lastNow += Math.Max(0, sp.StartSeconds - _pos);
                        _pos = sp.StartSeconds;
                        _holdSpan = _enter; _holdStart = _lastNow; _wrong = 0;
                        held = true; break;
                    }
                    if (_enter > 0) Finalize(_enter - 1);
                    _enter++;
                }
                if (held) continue;
                _pos = target; _lastNow = now;
                if (_pos >= _tl.TotalSeconds)
                {
                    for (int i = 0; i < _n; i++) Finalize(i);
                    _finished = true;
                }
                break;
            }
            return Target();
        }

        void Finalize(int i)
        {
            if (_done[i]) return;
            _done[i] = true;
            var r = new ChordResult { Index = i, Chord = _tl.Spans[i].Chord, ErrorSec = _bestErr[i] };
            if (_skipped[i]) { r.Outcome = ChordOutcome.Skipped; Skipped++; }
            else if (!double.IsNaN(_bestErr[i]))
            {
                r.Outcome = ChordOutcome.Hit; Hits++;
                _absErrSum += Math.Abs(_bestErr[i]); _errCount++;
                MeanAbsErrorSec = _absErrSum / _errCount;
            }
            else { r.Outcome = ChordOutcome.Missed; Missed++; }
            _results.Add(r);
        }

        ChordTarget Target()
        {
            if (_finished) return new ChordTarget(-1, null, -1, null, 0, _pos, false, true);
            if (_holdSpan >= 0)
            {
                var h = _tl.Spans[_holdSpan];
                bool more = _holdSpan + 1 < _n;
                return new ChordTarget(_holdSpan, h.Chord, more ? _holdSpan + 1 : -1, more ? _tl.Spans[_holdSpan + 1].Chord : null,
                    h.Beats, _pos, true, false);
            }
            var q = _tl.At(_pos);
            return new ChordTarget(q.CurrentIndex, q.CurrentIndex >= 0 ? _tl.Spans[q.CurrentIndex].Chord : null,
                q.NextIndex, q.NextIndex >= 0 ? _tl.Spans[q.NextIndex].Chord : null, q.BeatsUntilChange, _pos, false, false);
        }
    }
}
