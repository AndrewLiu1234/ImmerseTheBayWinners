using System;
using UntitledGuitarApp.Core.Theory;

namespace UntitledGuitarApp.Core.Audio
{
    /// <summary>What the tuner needle shows for one frame. All fields are finite.</summary>
    public readonly struct TunerReading
    {
        public readonly PitchStatus Status;
        /// <summary>Detected f0, Hz (0 without pitch).</summary>
        public readonly double Hz;
        /// <summary>String the needle refers to: the caller's target, else the nearest open string; 0 = none (no pitch, no target).</summary>
        public readonly int String;
        /// <summary>Standard-tuning MIDI of <see cref="String"/> (0 when String = 0).</summary>
        public readonly int TargetMidi;
        /// <summary>Standard-tuning frequency of <see cref="String"/>, Hz (0 when String = 0).</summary>
        public readonly double TargetHz;
        /// <summary>Smoothed cents from the target (positive = sharp → tune down). 0 without pitch.</summary>
        public readonly double Cents;
        /// <summary>This frame's unsmoothed cents from the target. 0 without pitch.</summary>
        public readonly double RawCents;
        /// <summary>|Cents| ≤ <see cref="AudioConfig.TunerInTuneCents"/> with a pitch present.</summary>
        public readonly bool InTune;
        public readonly float Confidence;
        public readonly float Rms;

        public bool HasPitch => Status == PitchStatus.Pitched;

        public TunerReading(PitchStatus status, double hz, int stringNumber, int targetMidi, double targetHz,
                            double cents, double rawCents, bool inTune, float confidence, float rms)
        {
            Status = status; Hz = hz; String = stringNumber; TargetMidi = targetMidi; TargetHz = targetHz;
            Cents = cents; RawCents = rawCents; InTune = inTune; Confidence = confidence; Rms = rms;
        }
    }

    /// <summary>
    /// Guitar tuner over <see cref="YinPitchTracker"/>: window in → Hz, the string it refers to (nearest open string, or the
    /// caller-chosen target), cents off, in-tune flag. Optional one-pole smoothing on cents driven by the caller's clock
    /// (<c>nowSec</c>; never DateTime). Preallocated; no allocation per call. One instance per audio worker.
    /// </summary>
    public sealed class Tuner
    {
        readonly AudioConfig _cfg;
        readonly YinPitchTracker _tracker;
        bool _haveSmoothed;
        double _smoothedCents;
        int _smoothedString;
        double _lastPitchSec;

        public YinPitchTracker Tracker => _tracker;

        public Tuner(AudioConfig config, int sampleRate)
        {
            _cfg = config ?? throw new ArgumentNullException(nameof(config));
            _tracker = new YinPitchTracker(config, sampleRate);
        }

        /// <summary>Forget the smoothing state (e.g. when the tuner screen opens).</summary>
        public void Reset() { _haveSmoothed = false; }

        /// <param name="nowSec">Caller's monotonic clock, seconds (timestamp of the window's end).</param>
        /// <param name="targetString">1 = high E … 6 = low E to tune a chosen string; 0 = auto (nearest open string).</param>
        public TunerReading Process(float[] samples, int offset, int count, double nowSec, int targetString = 0)
        {
            if (targetString != 0) Tuning.CheckString(targetString);
            var p = _tracker.Process(samples, offset, count);

            if (!p.HasPitch)
            {
                if (_haveSmoothed && !(nowSec - _lastPitchSec <= _cfg.TunerHoldSec)) _haveSmoothed = false;
                int tm = targetString != 0 ? Tuning.StandardMidi(targetString) : 0;
                double th = targetString != 0 ? Midi.ToHz(tm) : 0.0;
                return new TunerReading(p.Status, 0.0, targetString, tm, th, 0.0, 0.0, false, p.Confidence, p.Rms);
            }

            int s = targetString != 0 ? targetString : NearestString(p.Hz);
            int midi = Tuning.StandardMidi(s);
            double targetHz = Midi.ToHz(midi);
            double hz = p.Hz;
            // Expected-string hypothesis: with a target, YIN's uncorrected octave wins when it is closer to the target
            // (e.g. high E plucked while the low E rings sympathetically, which makes the mix periodic at E2).
            if (targetString != 0 &&
                Math.Abs(Midi.CentsBetween(targetHz, p.UncorrectedHz)) < Math.Abs(Midi.CentsBetween(targetHz, hz)))
                hz = p.UncorrectedHz;
            double raw = Midi.CentsBetween(targetHz, hz);

            // One-pole smoothing with a time-based coefficient; restart on a string change, a long gap or time going backwards.
            double dt = nowSec - _lastPitchSec;
            bool restart = !_haveSmoothed || s != _smoothedString || !(dt >= 0) || dt > _cfg.TunerHoldSec || double.IsInfinity(nowSec);
            if (restart || _cfg.TunerSmoothingSec <= 0) _smoothedCents = raw;
            else _smoothedCents += (1.0 - Math.Exp(-dt / _cfg.TunerSmoothingSec)) * (raw - _smoothedCents);
            _haveSmoothed = true; _smoothedString = s; _lastPitchSec = nowSec;

            bool inTune = Math.Abs(_smoothedCents) <= _cfg.TunerInTuneCents;
            return new TunerReading(PitchStatus.Pitched, hz, s, midi, targetHz, _smoothedCents, raw, inTune, p.Confidence, p.Rms);
        }

        /// <summary>Open string (1–6) closest in cents to <paramref name="hz"/>.</summary>
        public static int NearestString(double hz)
        {
            int best = 1; double bestAbs = double.MaxValue;
            for (int s = 1; s <= Tuning.StringCount; s++)
            {
                double c = Math.Abs(Midi.CentsBetween(Tuning.StandardHz(s), hz));
                if (c < bestAbs) { bestAbs = c; best = s; }
            }
            return best;
        }
    }
}
