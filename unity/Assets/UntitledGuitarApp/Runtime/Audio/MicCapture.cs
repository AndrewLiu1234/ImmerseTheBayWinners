using System;
using UnityEngine;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.Audio
{
    /// <summary>
    /// Microphone capture for the whole app (Unity main thread side). Create it from code:
    /// <code>var mic = new GameObject("MicCapture").AddComponent&lt;MicCapture&gt;();</code>
    /// It starts when enabled and needs no Inspector wiring. Everything it decides lives in <see cref="CaptureSession"/>
    /// (plain C#, unit-tested); this class only feeds it the clock, the focus events and the flag reloads, and wraps the real
    /// <c>UnityEngine.Microphone</c> behind <see cref="IMicBackend"/>.
    ///
    /// Data flow: <c>Microphone</c> looping clip (48 kHz mono, 10 s) -> each Update copies only the new frames -> 2 s ring buffer
    /// -> <see cref="Read"/> from the audio worker thread. With <c>FlagStore.Current.audioSource == "wav"</c> the same ring is fed
    /// from <c>wavReplayFile</c> (relative to StreamingAssets) instead; <see cref="IsReplay"/> is then true and the HUD must show
    /// the SIMULATED INPUT badge.
    ///
    /// The shape of <see cref="SampleRate"/> + <see cref="Read"/> is exactly Core's future <c>IAudioSource</c>; this class does not
    /// reference Core. <see cref="SampleRate"/>, <see cref="Read"/>, <see cref="Epoch"/> and <see cref="LostSamples"/> are safe to call from
    /// the worker thread; everything else is main-thread only.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MicCapture : MonoBehaviour
    {
        static bool s_warnedRunInBackground;

        CaptureSession _session;
        Action<string> _deviceChanged;

        /// <summary>Sample rate of what <see cref="Read"/> returns (the clip's real rate; 48000 before capture starts). Any thread.</summary>
        public int SampleRate { get { CaptureSession s = _session; return s != null ? s.SampleRate : CaptureSession.RequestedSampleRate; } }

        /// <summary>Copies up to <paramref name="count"/> unread mono samples into <paramref name="dst"/>; returns how many (0 when none). Any ONE reader thread; never blocks.</summary>
        public int Read(float[] dst, int offset, int count) { CaptureSession s = _session; return s != null ? s.Read(dst, offset, count) : 0; }

        /// <summary>Smoothed RMS level, linear 0..1 (main thread, for the meter).</summary>
        public float LevelRms { get { CaptureSession s = _session; return s != null ? s.LevelRms : 0f; } }

        /// <summary>Smoothed peak level, linear 0..1 (main thread, for the meter).</summary>
        public float LevelPeak { get { CaptureSession s = _session; return s != null ? s.LevelPeak : 0f; } }

        /// <summary>The device in use ("(default input)" for the default one, "wav:file" in replay mode), or "" when not capturing.</summary>
        public string DeviceName { get { CaptureSession s = _session; return s != null ? s.DeviceName : ""; } }

        /// <summary>True while audio is flowing. False when permission was denied, no device is available, or the watchdog is restarting.</summary>
        public bool IsCapturing { get { CaptureSession s = _session; return s != null && s.IsCapturing; } }

        /// <summary>True when the input is the WAV replay (flag audioSource = "wav" AND the file loaded). This, not the flag, is what the SIMULATED INPUT badge must show.</summary>
        public bool IsReplay { get { CaptureSession s = _session; return s != null && s.IsReplay; } }

        /// <summary>True when audioSource = "wav" but the replay file could not be used, so the microphone is the input instead (IsReplay is false).</summary>
        public bool ReplayFallback { get { CaptureSession s = _session; return s != null && s.ReplayFallback; } }

        /// <summary>Counts every start, restart and failure of the stream. When it changes the samples have a gap (and maybe a new rate): reset DSP state. Any thread.</summary>
        public int Epoch { get { CaptureSession s = _session; return s != null ? s.Epoch : 0; } }

        /// <summary>Samples lost to a full ring buffer or a long main-thread stall since the first <see cref="Read"/> (0 is healthy). Before anything reads, a full ring is not counted.</summary>
        public long LostSamples { get { CaptureSession s = _session; return s != null ? s.LostSamples : 0; } }

        /// <summary>True once anything has called <see cref="Read"/>. A reader that starts late first gets up to ~2.7 s of OLD audio (the ring keeps the oldest): drain with Read until it returns 0. Any thread.</summary>
        public bool HasReader { get { CaptureSession s = _session; return s != null && s.HasReader; } }

        /// <summary>Spike M-S3 aid: set above 0 to log <c>mic.level</c> (RMS and peak in dB) that often. 0 = off.</summary>
        public float LevelLogIntervalSec
        {
            get { CaptureSession s = _session; return s != null ? s.LevelLogIntervalSec : 0f; }
            set { CaptureSession s = _session; if (s != null) s.LevelLogIntervalSec = value; }
        }

        /// <summary>Raised on the main thread with the new <see cref="DeviceName"/> whenever it changes (start, flag change, restart onto another device, failure -> "").</summary>
        public event Action<string> DeviceChanged
        {
            add { _deviceChanged += value; }
            remove { _deviceChanged -= value; }
        }

        void Awake()
        {
            _session = new CaptureSession(new UnityMicBackend(), () => FlagStore.Current, Application.streamingAssetsPath, null);
            _session.DeviceChanged += RaiseDeviceChanged;
        }

        void OnEnable()
        {
            if (_session == null) return;
            FlagStore.Changed += OnFlagsChanged;
            if (!Application.runInBackground && !s_warnedRunInBackground)
            {
                s_warnedRunInBackground = true;
                UGALog.Warn("mic.runinbackground", "Application.runInBackground is false: Unity stops calling Update (and so stops pumping the microphone) whenever the app loses focus. Set Player Settings > Resolution and Presentation > Run In Background, or Application.runInBackground = true in Bootstrap.");
            }
            _session.Start(Time.realtimeSinceStartupAsDouble);
        }

        void OnDisable()
        {
            FlagStore.Changed -= OnFlagsChanged;
            if (_session != null) _session.Stop();
        }

        void Update()
        {
            _session.Tick(Time.realtimeSinceStartupAsDouble, Time.unscaledDeltaTime);
        }

        void OnApplicationFocus(bool hasFocus)
        {
            if (_session != null && isActiveAndEnabled) _session.OnFocus(hasFocus, Time.realtimeSinceStartupAsDouble);
        }

        void OnFlagsChanged(FlagSet flags)
        {
            if (_session != null) _session.OnFlagsChanged(Time.realtimeSinceStartupAsDouble);
        }

        void RaiseDeviceChanged(string name)
        {
            Action<string> h = _deviceChanged;
            if (h != null) h(name);
        }
    }

    /// <summary><see cref="IMicBackend"/> over the real <c>UnityEngine.Microphone</c> (Unity 2021.3; see docs/apis/unity/microphone.md).</summary>
    sealed class UnityMicBackend : IMicBackend
    {
        readonly UnityClipData _data = new UnityClipData();
        AudioClip _clip;
        AsyncOperation _authOp;

        public bool HasPermission { get { return Application.HasUserAuthorization(UserAuthorization.Microphone); } }

        public void RequestPermission()
        {
            if (_authOp == null || _authOp.isDone) _authOp = Application.RequestUserAuthorization(UserAuthorization.Microphone);
        }

        public string[] Devices() { return Microphone.devices; }

        public void GetDeviceCaps(string device, out int minFreq, out int maxFreq) { Microphone.GetDeviceCaps(device, out minFreq, out maxFreq); }

        public IClipData Start(string device, int lengthSec, int sampleRate)
        {
            AudioClip clip = Microphone.Start(device, true, lengthSec, sampleRate);
            if (clip == null) return null;
            _clip = clip;
            _data.Bind(clip);
            return _data;
        }

        public bool IsRecording(string device) { return Microphone.IsRecording(device); }

        public int GetPosition(string device) { return Microphone.GetPosition(device); }

        public void End(string device)
        {
            Microphone.End(device);
            _data.Bind(null);
            if (_clip != null)
            {
                UnityEngine.Object.Destroy(_clip);
                _clip = null;
            }
        }
    }

    /// <summary><see cref="IClipData"/> over a Unity <c>AudioClip</c>; one instance is re-bound to each new clip.</summary>
    sealed class UnityClipData : IClipData
    {
        AudioClip _clip;

        public void Bind(AudioClip clip) { _clip = clip; }

        public int Frames { get { return _clip != null ? _clip.samples : 0; } }
        public int Channels { get { return _clip != null ? _clip.channels : 0; } }
        public int SampleRate { get { return _clip != null ? _clip.frequency : 0; } }

        public bool GetData(float[] data, int offsetFrames) { return _clip != null && _clip.GetData(data, offsetFrames); }
    }
}
