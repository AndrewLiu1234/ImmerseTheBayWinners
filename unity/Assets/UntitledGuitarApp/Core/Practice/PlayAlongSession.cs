using System;
using System.Collections.Generic;
using UntitledGuitarApp.Core.Songs;
using UntitledGuitarApp.Core.Theory;

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
        /// <summary>Audio latency to subtract from the time passed to OnStrum, ms (flag audio.latencyOffsetMs). Positive = detections arrive late; 0 when the caller already passes onset times (StrumFeed does).</summary>
        public double LatencyOffsetMs = 0;
        /// <summary>Largest clock step one call may take, s: guards against clock jumps/stalls that would run the song or chain hold timeouts (20 s &gt; one hold timeout plus a long chord, so honest catch-up after a hitch still works; at most one hold timeout fires per call).</summary>
        public double MaxStepSec = 20.0;
        /// <summary>How long after the playhead leaves a chord a late-judged strum may still be credited to it, s (strum check ≈ 0.25 s + one frame).</summary>
        public double LateGraceSec = 0.5;
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
    public readonly struct PlayAlongTarget
    {
        public readonly int CurrentIndex, NextIndex;
        public readonly string Current, Next;
        public readonly double BeatsToChange, PositionSeconds;
        public readonly bool Holding, Finished;
        public PlayAlongTarget(int ci, string cur, int ni, string next, double beats, double pos, bool holding, bool finished)
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
        bool _started, _finished, _paused;

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

        /// <summary>Begins (or restarts) the song at nowSec: every per-chord array, counter, result and the Finished flag are reset.</summary>
        public PlayAlongTarget Start(double nowSec)
        {
            double now = Clean(nowSec);
            _started = true; _paused = false; _lastNow = now; _pos = 0; _holdStart = 0;
            _enter = 0; _holdSpan = -1; _wrong = 0; _finished = false;
            for (int i = 0; i < _n; i++) { _satisfied[i] = false; _skipped[i] = false; _done[i] = false; _bestErr[i] = double.NaN; }
            _results.Clear();
            Hits = 0; Missed = 0; Skipped = 0; MeanAbsErrorSec = 0; _absErrSum = 0; _errCount = 0;
            if (_n == 0) _finished = true;
            return Advance(now);
        }

        public PlayAlongTarget Update(double nowSec) => _started ? Advance(nowSec) : Start(nowSec);

        /// <summary>Freezes the playhead and the hold timers (menu, app focus lost). Strums while paused are ignored.</summary>
        public PlayAlongTarget Pause(double nowSec)
        {
            if (!_started || _paused) return Target();
            Advance(nowSec);
            _paused = true;
            return Target();
        }

        /// <summary>Continues after <see cref="Pause"/>; the paused time does not count toward the playhead or a hold timeout.</summary>
        public PlayAlongTarget Resume(double nowSec)
        {
            if (!_started || !_paused) return Target();
            double ns = Math.Max(Clean(nowSec), _lastNow);
            if (_holdSpan >= 0) _holdStart += ns - _lastNow;
            _lastNow = ns; _paused = false;
            return Target();
        }

        public bool Paused => _paused;

        double Clean(double v) => double.IsNaN(v) || double.IsInfinity(v) ? _lastNow : v;

        /// <summary>
        /// A detected strum of <paramref name="chord"/>. <paramref name="nowSec"/> is the onset time on the caller clock (as
        /// <see cref="Audio.StrumEvent.TimeSec"/>) or the delivery time when <see cref="PlayAlongConfig.LatencyOffsetMs"/> is set;
        /// the playhead position AT THE ONSET is reconstructed from it, so a strum judged 250 ms later is still timed correctly.
        /// </summary>
        public PlayAlongTarget OnStrum(string chord, double nowSec)
        {
            if (!_started) Start(nowSec);
            if (_paused) return Target();
            double ns = Clean(nowSec);
            Advance(ns);
            if (_finished) return Target();
            double onsetClock = ns - _cfg.LatencyOffsetMs / 1000.0;

            if (_holdSpan >= 0)
            {
                if (chord == _tl.Spans[_holdSpan].Chord)
                {
                    Record(_holdSpan, onsetClock - _holdStart);
                    _satisfied[_holdSpan] = true;
                    _holdSpan = -1; _wrong = 0;
                    return Advance(Math.Max(ns, _lastNow));
                }
                if (CountsAsWrong(chord) && ++_wrong >= _cfg.MaxWrongStrums) return Skip(ns);
                return Target();
            }

            double posAt = Math.Max(0, _pos - Math.Max(0, _lastNow - onsetClock));
            int best = -1; double bestAbs = double.MaxValue;
            int hi = Math.Min(_n - 1, _enter);
            for (int i = hi; i >= 0; i--)
            {
                var sp = _tl.Spans[i];
                if (i < _enter && sp.EndSeconds < posAt) break;           // spans are in order: nothing earlier can contain the onset
                if (_done[i] || sp.Chord != chord) continue;
                if (posAt < sp.StartSeconds - _cfg.EarlyWindowSec || posAt > sp.EndSeconds) continue;
                double a = Math.Abs(posAt - sp.StartSeconds);
                if (a < bestAbs) { bestAbs = a; best = i; }
            }
            if (best >= 0) { Record(best, posAt - _tl.Spans[best].StartSeconds); _satisfied[best] = true; }
            return Advance(ns);
        }

        // Only a recognised chord that is neither the held one nor the one just played counts against the player:
        // silence/noise ("" / null) and a late ring of the previous chord are not wrong answers.
        bool CountsAsWrong(string chord)
        {
            if (string.IsNullOrEmpty(chord) || !ChordLibrary.TryGet(chord, out _)) return false;
            return !(_holdSpan > 0 && chord == _tl.Spans[_holdSpan - 1].Chord);
        }

        /// <summary>Escape hatch by hand (also used by the timeout): give up on the chord being held and move on.</summary>
        public PlayAlongTarget Skip(double nowSec)
        {
            if (_paused) return Target();
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

        PlayAlongTarget Advance(double nowSec)
        {
            if (_paused) return Target();
            // A clock jump or a long stall must not run the whole song or chain every hold timeout in one call.
            double now = Math.Min(Math.Max(Clean(nowSec), _lastNow), _lastNow + _cfg.MaxStepSec);
            bool timedOut = false;
            while (!_finished)
            {
                if (_holdSpan >= 0)
                {
                    if (now - _holdStart >= _cfg.HoldTimeoutSec)
                    {
                        _lastNow = _holdStart + _cfg.HoldTimeoutSec;
                        _skipped[_holdSpan] = true; _satisfied[_holdSpan] = true;
                        _holdSpan = -1; _wrong = 0; timedOut = true;
                        continue;
                    }
                    _lastNow = now;
                    FinalizeDue(now - _holdStart);
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
                        if (timedOut) _lastNow = now;     // fresh timer after a timeout in this same call
                        _holdSpan = _enter; _holdStart = _lastNow; _wrong = 0;
                        held = true; break;
                    }
                    _enter++;
                }
                if (held) continue;
                _pos = target; _lastNow = now;
                if (_pos >= _tl.TotalSeconds)
                {
                    for (int i = 0; i < _n; i++) Finalize(i);
                    _finished = true;
                }
                else FinalizeDue(0);
                break;
            }
            return Target();
        }

        // A chord's result is published LateGraceSec after the playhead leaves it (holding time counts), so a strum judged
        // ~250 ms after it was played can still be credited to the chord it belongs to.
        void FinalizeDue(double heldFor)
        {
            double p = _pos + heldFor - _cfg.LateGraceSec;
            for (int i = 0; i < _enter && i < _n; i++)
            {
                if (_done[i]) continue;
                if (_tl.Spans[i].EndSeconds > p) break;
                Finalize(i);
            }
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

        PlayAlongTarget Target()
        {
            if (_finished) return new PlayAlongTarget(-1, null, -1, null, 0, _pos, false, true);
            if (_holdSpan >= 0)
            {
                var h = _tl.Spans[_holdSpan];
                bool more = _holdSpan + 1 < _n;
                return new PlayAlongTarget(_holdSpan, h.Chord, more ? _holdSpan + 1 : -1, more ? _tl.Spans[_holdSpan + 1].Chord : null,
                    h.Beats, _pos, true, false);
            }
            var q = _tl.At(_pos);
            return new PlayAlongTarget(q.CurrentIndex, q.CurrentIndex >= 0 ? _tl.Spans[q.CurrentIndex].Chord : null,
                q.NextIndex, q.NextIndex >= 0 ? _tl.Spans[q.NextIndex].Chord : null, q.BeatsUntilChange, _pos, false, false);
        }
    }
}
