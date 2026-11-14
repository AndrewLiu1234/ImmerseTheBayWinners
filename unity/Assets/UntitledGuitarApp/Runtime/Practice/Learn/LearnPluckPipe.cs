// LearnPluckPipe: streaming onset -> pluck verdict for learn mode's finger and pluck-check stages (Core's SL2 pieces,
// read-only here). Feed mic samples; Core's SpectralFluxOnset finds onsets, and once an onset's full window (pre-roll +
// ring-out) has arrived, Core's PluckVerifier judges it against the expectation captured AT THE ONSET (the string the UI
// asked for then), so a player moving to the next string while the previous one still rings does not lose the verdict.
// Preallocated: Process allocates nothing. Not thread-safe (main thread, like the rest of learn mode). Deterministic.
using System;
using UntitledGuitarApp.Core.Audio;
using UntitledGuitarApp.Core.Theory;

namespace UntitledGuitarApp.Runtime.Practice.Learn
{
    /// <summary>A judged pluck: the onset time (seconds on the pipe's clock) and Core's verdict.</summary>
    public readonly struct PluckEvent
    {
        public readonly double TimeSec;
        public readonly PluckResult Result;
        public PluckEvent(double timeSec, in PluckResult result) { TimeSec = timeSec; Result = result; }
    }

    public sealed class LearnPluckPipe
    {
        const int PendingCapacity = 8, EventCapacity = 8;

        readonly SpectralFluxOnset _onsets;
        readonly PluckVerifier _verifier;
        readonly int _hop, _pre, _after, _windowLen;
        readonly float[] _ring;
        readonly int _mask;
        readonly float[] _window;
        readonly long[] _pendOnset = new long[PendingCapacity];
        readonly int[] _pendString = new int[PendingCapacity];
        readonly int[] _pendMidi = new int[PendingCapacity];
        readonly ChordShape[] _pendChord = new ChordShape[PendingCapacity];
        readonly PluckEvent[] _events = new PluckEvent[EventCapacity];
        int _pendHead, _pendCount, _evHead, _evCount;
        long _written;
        double _origin;

        public int SampleRate { get; }
        /// <summary>Onsets that could not be judged (nothing expected at the time, queue full, or audio overwritten).</summary>
        public int Dropped { get; private set; }
        /// <summary>Onsets queued, waiting for their ring-out window to complete.</summary>
        public int PendingCount { get { return _pendCount; } }

        public LearnPluckPipe(AudioConfig config, int sampleRate, double clockOriginSec = 0)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            SampleRate = sampleRate;
            _onsets = new SpectralFluxOnset(config, sampleRate);
            _verifier = new PluckVerifier(config, sampleRate);
            _hop = config.HopSize;
            _pre = _verifier.PreRollSamples;
            _after = _verifier.SamplesAfterOnsetForRing;
            _windowLen = _pre + _after;
            int len = 1;
            while (len < 2 * (_windowLen + config.OnsetFftSize + _hop)) len <<= 1;
            _ring = new float[len];
            _mask = len - 1;
            _window = new float[_windowLen];
            _origin = Finite(clockOriginSec, 0);
        }

        /// <summary>Forgets all audio and queued plucks; sample 0 is at <paramref name="clockOriginSec"/> on the caller's clock.</summary>
        public void Reset(double clockOriginSec)
        {
            _onsets.Reset();
            Array.Clear(_ring, 0, _ring.Length);
            _written = 0; _pendHead = 0; _pendCount = 0; _evHead = 0; _evCount = 0; Dropped = 0;
            _origin = Finite(clockOriginSec, 0);
        }

        /// <summary>
        /// Feeds mono samples. Every onset is queued with the expectation current NOW (stringNumber &lt; 1 or expectedMidi &lt; 0
        /// means nothing is expected: those onsets are dropped) and judged once its full window has arrived, always against its
        /// own captured expectation. Returns the number of new <see cref="PluckEvent"/>s for <see cref="TryRead"/>. Allocates nothing.
        /// </summary>
        public int Process(float[] samples, int offset, int count, int stringNumber, int expectedMidi, ChordShape chord)
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
                    if (stringNumber < 1 || expectedMidi < 0 || at < 0 || _written - at > _ring.Length)
                    {
                        Dropped++;
                        continue;
                    }
                    if (_pendCount == PendingCapacity) { Dropped++; continue; }
                    int i = (_pendHead + _pendCount) % PendingCapacity;
                    _pendOnset[i] = at; _pendString[i] = stringNumber; _pendMidi[i] = expectedMidi; _pendChord[i] = chord;
                    _pendCount++;
                }
                made += JudgeReady();
            }
            return made;
        }

        /// <summary>Oldest undelivered pluck; false when none.</summary>
        public bool TryRead(out PluckEvent e)
        {
            if (_evCount == 0) { e = default(PluckEvent); return false; }
            e = _events[_evHead];
            _events[_evHead] = default(PluckEvent);
            _evHead = (_evHead + 1) % EventCapacity; _evCount--;
            return true;
        }

        int JudgeReady()
        {
            int made = 0;
            while (_pendCount > 0 && _pendOnset[_pendHead] + _after <= _written)
            {
                long onset = _pendOnset[_pendHead];
                int s = _pendString[_pendHead], midi = _pendMidi[_pendHead];
                ChordShape chord = _pendChord[_pendHead];
                _pendHead = (_pendHead + 1) % PendingCapacity; _pendCount--;

                long lo = onset - _pre;
                if (lo < _written - _ring.Length) { Dropped++; continue; }   // the window's head left the ring
                long start = lo < 0 ? 0 : lo;                                  // before the stream: silence (the ring starts cleared)
                for (int i = 0; i < _windowLen; i++) _window[i] = _ring[(int)((start + i) & _mask)];
                int onsetIndex = (int)(onset - start);
                PluckResult r = _verifier.Verify(_window, 0, _windowLen, onsetIndex, s, midi, chord);
                if (_evCount == EventCapacity) { Dropped++; continue; }
                _events[(_evHead + _evCount) % EventCapacity] = new PluckEvent(_origin + (double)onset / SampleRate, r);
                _evCount++; made++;
            }
            return made;
        }

        static double Finite(double v, double fallback) => double.IsNaN(v) || double.IsInfinity(v) ? fallback : v;
    }
}
