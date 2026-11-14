// LearnMicTuner: the REAL tuner for learn mode. While LearnPresenter is in its Tune stage it reads the live microphone samples
// (MicCapture.Read), runs Person 2's Core tuner on the newest window with the string being tuned as the target
// (Core/Audio/Tuner.Process(samples, offset, count, nowSec, targetString) -> TunerReading; auto mode can mistake strings) and
// sends the reading to LearnPresenter.Verdicts. The needle's own in-tune rule then moves to the next string (the presenter's
// AutoAdvanceTuning).
//
// IsLive is false (and DevLearnScript's scripted sweep drives the needle instead) when audioSource is not "mic" (the WAV
// replay is a recording of chords, not of tuning), when the mic is not capturing, or when the rate is not 44.1 / 48 kHz
// (the only rates Core's tuner supports).
//
// It is the ONLY reader of MicCapture's samples, and only while tuning (MicCapture.Read allows one reader). When an audio
// worker thread arrives for the play-along it must own the samples and hand this the window instead.
// Main thread. Cost: one Core tuner window (FrameSize samples) at most every HopSize new samples, at most once a frame;
// no allocation per frame.
using System;
using UnityEngine;
using UntitledGuitarApp.Core.Audio;
using UntitledGuitarApp.Runtime.Audio;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.Practice.Learn
{
    [DefaultExecutionOrder(19980)]   // just before LearnPresenter (19990)
    [DisallowMultipleComponent]
    public sealed class LearnMicTuner : MonoBehaviour
    {
        MicCapture _mic;
        LearnPresenter _presenter;
        Tuner _tuner;
        int _tunerRate;
        int _epoch = -1;
        float[] _window;
        float[] _chunk;
        int _filled;
        int _sinceProcess;
        int _target;
        bool _warnedRate;

        public static LearnMicTuner Create(Transform parent, MicCapture mic, LearnPresenter presenter)
        {
            var go = new GameObject("LearnMicTuner");
            if (parent != null) go.transform.SetParent(parent, false);
            LearnMicTuner t = go.AddComponent<LearnMicTuner>();
            t._mic = mic;
            t._presenter = presenter;
            return t;
        }

        /// <summary>The last reading sent (for the spectator and debugging).</summary>
        public TunerReading LastReading { get; private set; }

        /// <summary>True when real microphone pitch drives the needle (see the file summary).</summary>
        public bool IsLive
        {
            get
            {
                if (_mic == null || !_mic.IsCapturing || _mic.IsReplay) return false;
                FlagSet flags = FlagStore.Current;
                if (flags != null && !string.Equals(flags.audioSource, "mic", StringComparison.Ordinal)) return false;
                return SupportedRate(_mic.SampleRate);
            }
        }

        /// <summary>Core's tuner runs at these rates only (Core/Audio: "44.1/48 kHz only").</summary>
        public static bool SupportedRate(int sampleRate)
        {
            return sampleRate == 44100 || sampleRate == 48000;
        }

        void Update()
        {
            if (_presenter == null || _presenter.Stage != LearnStage.Tune || !IsLive)
            {
                _filled = 0;
                _sinceProcess = 0;
                if (_mic != null && !_warnedRate && _mic.IsCapturing && !SupportedRate(_mic.SampleRate))
                {
                    _warnedRate = true;
                    UGALog.Warn("learn_tuner_rate", "mic runs at " + _mic.SampleRate + " Hz; Core's tuner needs 44100 or 48000, so the needle is scripted");
                }
                return;
            }
            Pump(Time.realtimeSinceStartup);
        }

        void Pump(double nowSec)
        {
            int rate = _mic.SampleRate;
            if (_tuner == null || rate != _tunerRate || _mic.Epoch != _epoch)
            {
                _tuner = new Tuner(AudioConfig.Default, rate);   // a new rate or a gap in the stream: start afresh
                _tunerRate = rate;
                _epoch = _mic.Epoch;
                int frame = Mathf.Max(AudioConfig.Default.FrameSize, 2048);
                _window = new float[frame];
                _chunk = new float[frame];
                _filled = 0;
                _sinceProcess = 0;
            }

            int target = _presenter.Flow.TuneString;
            if (target != _target)
            {
                _target = target;
                _tuner.Reset();
            }
            if (target < 1 || target > 6) return;

            // Keep the newest window: read whatever arrived and slide it in at the end.
            int n;
            while ((n = _mic.Read(_chunk, 0, _chunk.Length)) > 0)
            {
                int size = _window.Length;
                if (n >= size)
                {
                    Array.Copy(_chunk, n - size, _window, 0, size);
                    _filled = size;
                }
                else
                {
                    int keep = Mathf.Min(_filled, size - n);
                    Array.Copy(_window, _filled - keep, _window, 0, keep);
                    Array.Copy(_chunk, 0, _window, keep, n);
                    _filled = keep + n;
                }
                _sinceProcess += n;
            }

            int hop = Mathf.Max(1, AudioConfig.Default.HopSize);
            if (_filled < _window.Length || _sinceProcess < hop) return;
            _sinceProcess = 0;
            TunerReading reading = _tuner.Process(_window, 0, _window.Length, nowSec, target);
            LastReading = reading;
            if (reading.HasPitch) _presenter.Verdicts.OnTunerReading((float)reading.Cents, reading.Confidence);
        }
    }
}
