using System;
using System.Collections.Generic;
using UntitledGuitarApp.Core.Theory;

namespace UntitledGuitarApp.Core.Audio
{
    /// <summary>One judged strum: when it started (caller clock, s) and what the strum check heard.</summary>
    public readonly struct StrumEvent
    {
        /// <summary>Onset time on the caller's clock (clock origin + samples / rate), s.</summary>
        public readonly double TimeSec;
        public readonly StrumCheckResult Result;
        public StrumEvent(double timeSec, in StrumCheckResult result) { TimeSec = timeSec; Result = result; }
        /// <summary>The chord heard (best match), "" when nothing rang — the string PlayAlongSession/ChangeDrillSession.OnStrum take.</summary>
        public string HeardChord => Result.BestMatch ?? "";
    }

    /// <summary>
    /// Streaming onset → strum check: feed mono mic samples; every onset (<see cref="SpectralFluxOnset"/>) is judged by
    /// <see cref="StrumVerifier"/> against the expectation passed with the samples once <see cref="StrumVerifier.SamplesAfterOnset"/>
    /// samples have arrived (250 ms), so strum-to-verdict ≈ 250 ms + one chunk. Time is passed in as a clock origin; nothing reads a
    /// clock. Preallocated: <see cref="Process"/> allocates nothing. One instance per audio worker; not thread-safe. Deterministic.
    /// </summary>
    public sealed class StrumFeed
    {
        const int PendingCapacity = 16, EventCapacity = 32;

        readonly SpectralFluxOnset _onsets;
        readonly StrumVerifier _verifier;
        readonly float[] _ring;          // power-of-two ring of the newest samples
        readonly int _mask, _hop, _need;
        readonly float[] _window;        // linear copy of [onset, onset + need)
        readonly long[] _pending = new long[PendingCapacity];
        readonly StrumEvent[] _events = new StrumEvent[EventCapacity];
        int _pendCount, _evHead, _evCount;
        long _written;
        double _origin;

        public int SampleRate { get; }
        public StrumVerifier Verifier => _verifier;
        /// <summary>Onsets that could not be judged (queue full, audio overwritten, or no expected chord at the time).</summary>
        public int Dropped { get; private set; }

        public StrumFeed(AudioConfig config, int sampleRate, double clockOriginSec = 0)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            SampleRate = sampleRate;
            _onsets = new SpectralFluxOnset(config, sampleRate);
            _verifier = new StrumVerifier(config, sampleRate);
            _hop = config.HopSize;
            _need = _verifier.SamplesAfterOnset;
            int len = 1; while (len < 2 * (_need + config.OnsetFftSize + _hop)) len <<= 1;
            _ring = new float[len];
            _mask = len - 1;
            _window = new float[_need];
            _origin = double.IsNaN(clockOriginSec) || double.IsInfinity(clockOriginSec) ? 0 : clockOriginSec;
        }

        /// <summary>Forget all audio and queued strums; sample 0 is at <paramref name="clockOriginSec"/> on the caller's clock.</summary>
        public void Reset(double clockOriginSec)
        {
            _onsets.Reset();
            Array.Clear(_ring, 0, _ring.Length);
            _written = 0; _pendCount = 0; _evHead = 0; _evCount = 0; Dropped = 0;
            _origin = double.IsNaN(clockOriginSec) || double.IsInfinity(clockOriginSec) ? 0 : clockOriginSec;
        }

        /// <summary>
        /// Feeds samples; judges every onset whose window is complete against <paramref name="expected"/> (null = nothing to
        /// judge: those onsets are dropped) and <paramref name="confusers"/> (e.g. the song's other chords,
        /// <see cref="StrumVerifier.DefaultConfusers"/>). Returns the number of new <see cref="StrumEvent"/>s for <see cref="TryRead"/>.
        /// </summary>
        public int Process(float[] samples, int offset, int count, ChordShape expected, IReadOnlyList<ChordShape> confusers)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            if (offset < 0 || count < 0 || offset > samples.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            int made = 0;
            for (int done = 0; done < count;)
            {
                int n = Math.Min(_hop, count - done);
                for (int i = 0; i < n; i++)
                {
                    float v = samples[offset + done + i];
                    _ring[(int)((_written + i) & _mask)] = float.IsNaN(v) || float.IsInfinity(v) ? 0f : v;
                }
                _onsets.Process(samples, offset + done, n);
                _written += n;
                done += n;
                while (_onsets.TryReadOnset(out double t))
                {
                    long at = (long)Math.Round(t * SampleRate);
                    if (_pendCount == PendingCapacity) { Dropped++; continue; }
                    _pending[_pendCount++] = at;
                }
                made += JudgeReady(expected, confusers);
            }
            return made;
        }

        /// <summary>Oldest undelivered strum; false when none.</summary>
        public bool TryRead(out StrumEvent e)
        {
            if (_evCount == 0) { e = default; return false; }
            e = _events[_evHead];
            _events[_evHead] = default;
            _evHead = (_evHead + 1) % EventCapacity; _evCount--;
            return true;
        }

        int JudgeReady(ChordShape expected, IReadOnlyList<ChordShape> confusers)
        {
            int made = 0;
            while (_pendCount > 0 && _pending[0] + _need <= _written)
            {
                long at = _pending[0];
                for (int i = 1; i < _pendCount; i++) _pending[i - 1] = _pending[i];
                _pendCount--;
                if (expected == null || _written - at > _ring.Length || at < 0) { Dropped++; continue; }
                for (int i = 0; i < _need; i++) _window[i] = _ring[(int)((at + i) & _mask)];
                var r = _verifier.Verify(_window, 0, _need, 0, expected, confusers);
                if (_evCount == EventCapacity) { Dropped++; continue; }
                _events[(_evHead + _evCount) % EventCapacity] = new StrumEvent(_origin + (double)at / SampleRate, r);
                _evCount++; made++;
            }
            return made;
        }
    }
}
