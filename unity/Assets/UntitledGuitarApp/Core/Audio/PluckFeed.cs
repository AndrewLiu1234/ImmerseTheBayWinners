using System;

namespace UntitledGuitarApp.Core.Audio
{
    /// <summary>One judged pluck: onset time (caller clock, s) and what the pluck check heard.</summary>
    public readonly struct PluckEvent
    {
        /// <summary>Onset time on the caller's clock (clock origin + samples / rate), s.</summary>
        public readonly double TimeSec;
        public readonly PluckResult Result;
        public PluckEvent(double timeSec, in PluckResult result) { TimeSec = timeSec; Result = result; }
    }

    /// <summary>
    /// Streaming onset → pluck check: feed mono mic samples in any chunk size; every onset (<see cref="SpectralFluxOnset"/>) is judged
    /// by <see cref="PluckVerifier"/> once <see cref="PluckVerifier.SamplesAfterOnsetForRing"/> samples have arrived, with
    /// <see cref="PluckVerifier.PreRollSamples"/> of pre-onset audio. The expectation (string, MIDI) is the one set via
    /// <see cref="SetExpectation"/> AT THE MOMENT THE ONSET WAS DETECTED (not when the verdict is produced): the player's pluck is
    /// judged against what the HUD asked for when they played it, even if the UI has moved on during the ring-out.
    /// Onsets with no expectation (string 0 / midi ≤ 0) are skipped without calling the verifier. Preallocated: Process allocates
    /// nothing. Not thread-safe; one instance per audio worker. Deterministic.
    /// </summary>
    public sealed class PluckFeed
    {
        const int PendingCapacity = 8, EventCapacity = 16;

        readonly SpectralFluxOnset _onsets;
        readonly PluckVerifier _verifier;
        readonly float[] _ring;
        readonly int _mask, _hop, _pre, _post;
        readonly float[] _window;
        readonly long[] _pending = new long[PendingCapacity];
        readonly int[] _pendString = new int[PendingCapacity], _pendMidi = new int[PendingCapacity];
        readonly PluckEvent[] _events = new PluckEvent[EventCapacity];
        int _pendCount, _evHead, _evCount, _expString, _expMidi;
        long _written;
        double _origin;

        public int SampleRate { get; }
        public PluckVerifier Verifier => _verifier;
        /// <summary>Onsets that could not be judged (queue full, audio overwritten, or no expectation at the time).</summary>
        public int Dropped { get; private set; }

        public PluckFeed(AudioConfig config, int sampleRate, double clockOriginSec = 0)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            SampleRate = sampleRate;
            _onsets = new SpectralFluxOnset(config, sampleRate);
            _verifier = new PluckVerifier(config, sampleRate);
            _hop = config.HopSize;
            _pre = _verifier.PreRollSamples;
            _post = _verifier.SamplesAfterOnsetForRing;
            int len = 1; while (len < 2 * (_pre + _post + config.OnsetFftSize + _hop)) len <<= 1;
            _ring = new float[len];
            _mask = len - 1;
            _window = new float[_pre + _post];
            _origin = Finite(clockOriginSec);
        }

        static double Finite(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : v;

        /// <summary>Forget all audio and queued plucks; sample 0 is at <paramref name="clockOriginSec"/> on the caller's clock.</summary>
        public void Reset(double clockOriginSec)
        {
            _onsets.Reset();
            Array.Clear(_ring, 0, _ring.Length);
            _written = 0; _pendCount = 0; _evHead = 0; _evCount = 0; Dropped = 0;
            _origin = Finite(clockOriginSec);
        }

        /// <summary>String (1 = high E … 6 = low E) and MIDI note the next onsets are judged against. String 0 or midi ≤ 0 = none.</summary>
        public void SetExpectation(int stringNumber, int expectedMidi) { _expString = stringNumber; _expMidi = expectedMidi; }

        /// <summary>Clears the expectation (onsets are ignored until <see cref="SetExpectation"/>).</summary>
        public void ClearExpectation() { _expString = 0; _expMidi = 0; }

        /// <summary>Feeds samples (any count ≥ 0). Returns the number of new <see cref="PluckEvent"/>s for <see cref="TryRead"/>.</summary>
        public int Process(float[] samples, int offset, int count)
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
                    bool valid = _expString >= 1 && _expString <= 6 && _expMidi > 0 && _expMidi <= 127;
                    if (!valid || _pendCount == PendingCapacity) { Dropped++; continue; }
                    _pending[_pendCount] = at; _pendString[_pendCount] = _expString; _pendMidi[_pendCount] = _expMidi;
                    _pendCount++;
                }
                made += JudgeReady();
            }
            return made;
        }

        /// <summary>Oldest undelivered pluck; false when none.</summary>
        public bool TryRead(out PluckEvent e)
        {
            if (_evCount == 0) { e = default; return false; }
            e = _events[_evHead];
            _events[_evHead] = default;
            _evHead = (_evHead + 1) % EventCapacity; _evCount--;
            return true;
        }

        int JudgeReady()
        {
            int made = 0;
            while (_pendCount > 0 && _pending[0] + _post <= _written)
            {
                long at = _pending[0]; int s = _pendString[0], m = _pendMidi[0];
                for (int i = 1; i < _pendCount; i++) { _pending[i - 1] = _pending[i]; _pendString[i - 1] = _pendString[i]; _pendMidi[i - 1] = _pendMidi[i]; }
                _pendCount--;
                long start = Math.Max(0, at - _pre);
                int len = (int)(at + _post - start);
                if (_written - start > _ring.Length) { Dropped++; continue; }
                for (int i = 0; i < len; i++) _window[i] = _ring[(int)((start + i) & _mask)];
                var r = _verifier.Verify(_window, 0, len, (int)(at - start), s, m, null);
                if (_evCount == EventCapacity) { Dropped++; continue; }
                _events[(_evHead + _evCount) % EventCapacity] = new PluckEvent(_origin + (double)at / SampleRate, r);
                _evCount++; made++;
            }
            return made;
        }
    }
}
