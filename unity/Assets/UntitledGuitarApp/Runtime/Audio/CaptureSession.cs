using System;
using System.Globalization;
using System.IO;
using System.Text;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Logging;

namespace UntitledGuitarApp.Runtime.Audio
{
    /// <summary>
    /// What <see cref="CaptureSession"/> needs from <c>UnityEngine.Microphone</c> and <c>Application</c>. MicCapture supplies the
    /// real thing; tests supply a fake, so permission denial, the watchdog and device choice are tested without a microphone.
    /// </summary>
    public interface IMicBackend
    {
        /// <summary><c>Application.HasUserAuthorization(UserAuthorization.Microphone)</c>. Unity 2021.3 documents it as always true outside the Web Player.</summary>
        bool HasPermission { get; }
        /// <summary><c>Application.RequestUserAuthorization(UserAuthorization.Microphone)</c>; the docs say it only does something on iOS and WebGL. Fire and forget.</summary>
        void RequestPermission();
        /// <summary><c>Microphone.devices</c> (allocates a new array: called at start and restart only).</summary>
        string[] Devices();
        /// <summary><c>Microphone.GetDeviceCaps</c>; (0, 0) means "any frequency" (or an unknown name).</summary>
        void GetDeviceCaps(string device, out int minFreq, out int maxFreq);
        /// <summary><c>Microphone.Start(device, true, lengthSec, sampleRate)</c>. Null means "failed to start"; may also throw (Unity throws for an unknown device name).</summary>
        IClipData Start(string device, int lengthSec, int sampleRate);
        bool IsRecording(string device);
        /// <summary><c>Microphone.GetPosition</c>.</summary>
        int GetPosition(string device);
        /// <summary><c>Microphone.End</c> and release of the clip.</summary>
        void End(string device);
    }

    /// <summary>Loads and decodes a WAV file (same shape as <see cref="WavReader.TryReadFile"/>).</summary>
    public delegate bool WavLoader(string path, out WavData data, out string error);

    /// <summary>
    /// All the decisions behind MicCapture, as a plain C# class driven by <see cref="Tick"/> calls: which source (microphone or
    /// WAV replay), which device, the start-up checks, the capture watchdog, retry after a denied permission, the level meter.
    /// MicCapture is a thin MonoBehaviour around one of these.
    ///
    /// Source: <c>FlagStore.Current.audioSource == "wav"</c> replays <c>wavReplayFile</c> (relative to StreamingAssets) through
    /// the same ring buffer, so downstream code reads one source whatever the flag says; anything else uses the microphone.
    /// Microphone: the first device whose name contains <c>audio.device</c> (case-insensitive), else the default device;
    /// a 10 s looping clip at 48 kHz (clamped to what the device reports); <see cref="SampleRate"/> is what the clip really got.
    ///
    /// Failure model (nothing here throws into the frame loop):
    ///  - "Denied": no devices listed, <c>Start</c> null or throwing, or a clip with no frames. <see cref="IsCapturing"/> false,
    ///    ONE warning (<see cref="WarnCount"/>), then a retry each time the app regains focus (<see cref="OnFocus"/>).
    ///  - "Stalled": capture started but no new frames. Watchdog: restart after 2 s without new frames once audio has flowed
    ///    (5 s grace before the very first frame, because the macOS permission dialog can hold it back).
    /// </summary>
    public sealed class CaptureSession
    {
        /// <summary>Rate asked for (Hz). The real rate is whatever the clip reports.</summary>
        public const int RequestedSampleRate = 48000;
        /// <summary>Length of the looping recording clip in seconds (the buffer between Unity's recorder and us).</summary>
        public const int ClipSeconds = 10;
        /// <summary>Watchdog: restart after this long without new frames once audio has flowed.</summary>
        public const double StallRestartSec = 2.0;
        /// <summary>Watchdog: how long to wait for the very first frame after a (re)start.</summary>
        public const double FirstFrameGraceSec = 5.0;
        /// <summary>Ring buffer holds at least this many seconds (rounded up to a power of two samples).</summary>
        public const double RingSeconds = 2.0;
        /// <summary>One warning when the input has been exactly 0.0 for this long.</summary>
        public const double SilenceWarnSec = 3.0;
        /// <summary>Shown as <see cref="DeviceName"/> when the default device is used (its real name is not exposed).</summary>
        public const string DefaultDeviceLabel = "(default input)";

        enum State { Idle, Starting, Running, Failed, AwaitingPermission }

        readonly IMicBackend _mic;
        readonly Func<FlagSet> _flags;
        readonly string _streamingAssetsPath;
        readonly WavLoader _wavLoader;
        readonly InputLevelMeter _meter = new InputLevelMeter();
        readonly LoopingClipPump _pump = new LoopingClipPump();
        readonly float[] _scratch = new float[4096];

        volatile FloatRingBuffer _ring;
        volatile int _sampleRate = RequestedSampleRate;
        volatile int _epoch;

        State _state = State.Idle;
        bool _started;
        string _deviceName = "";
        string _micDevice;              // name handed to Microphone.* (null = default device)
        bool _micRunning;               // Microphone.Start succeeded and End has not been called
        WavReplaySource _replay;
        bool _cfgWav;
        string _cfgDevice = "";
        string _cfgWavFile = "";
        bool _retryOnFocus;
        bool _warnedFailure;
        bool _warnedPumpError;
        bool _warnedSilent;
        bool _everFlowed;
        double _lastFrameSec;
        int _consecutiveStalls;
        long _zeroRunFrames;
        long _droppedCarry;
        long _lastReportedLoss;
        double _lastLossWarnSec = -1e9;
        double _nextLevelLogSec;
        string[] _loggedDevices;

        public CaptureSession(IMicBackend mic, Func<FlagSet> flags, string streamingAssetsPath, WavLoader wavLoader)
        {
            if (mic == null) throw new ArgumentNullException("mic");
            if (flags == null) throw new ArgumentNullException("flags");
            _mic = mic;
            _flags = flags;
            _streamingAssetsPath = streamingAssetsPath ?? "";
            _wavLoader = wavLoader ?? new WavLoader(WavReader.TryReadFile);
        }

        // ---- what MicCapture exposes ---------------------------------------------------------------------------

        /// <summary>Sample rate of the samples <see cref="Read"/> returns; 48000 until a capture has started. Safe from any thread.</summary>
        public int SampleRate { get { return _sampleRate; } }

        /// <summary>Counts every start, restart and failure. A change means the sample stream has a gap (and maybe a new rate): reset DSP state. Safe from any thread.</summary>
        public int Epoch { get { return _epoch; } }

        /// <summary>Copies up to <paramref name="count"/> unread samples (mono floats) into <paramref name="dst"/>; 0 when none. Consumer thread; never blocks.</summary>
        public int Read(float[] dst, int offset, int count)
        {
            FloatRingBuffer r = _ring;
            return r == null ? 0 : r.Read(dst, offset, count);
        }

        /// <summary>True while samples are flowing (microphone delivering frames, or the WAV replay running).</summary>
        public bool IsCapturing { get { return _state == State.Running; } }
        /// <summary>True when the input is the WAV replay: the HUD must show SIMULATED INPUT.</summary>
        public bool IsReplay { get { return _replay != null; } }
        /// <summary>Name of the device in use, <see cref="DefaultDeviceLabel"/>, "wav:&lt;file&gt;" in replay mode, or "" when nothing is capturing.</summary>
        public string DeviceName { get { return _deviceName; } }
        /// <summary>Smoothed RMS, linear 0..1.</summary>
        public float LevelRms { get { return _meter.Rms; } }
        /// <summary>Smoothed peak, linear 0..1.</summary>
        public float LevelPeak { get { return _meter.Peak; } }
        /// <summary>Raised on the thread that calls Tick/Start whenever <see cref="DeviceName"/> changes.</summary>
        public event Action<string> DeviceChanged;

        // ---- diagnostics ---------------------------------------------------------------------------------------

        /// <summary>Audio lost so far: ring buffer overflow (reader too slow) plus frames skipped after a long main-thread stall.</summary>
        public long LostSamples
        {
            get
            {
                FloatRingBuffer r = _ring;
                return _droppedCarry + (r == null ? 0 : r.DroppedSamples) + _pump.SkippedFrames;
            }
        }
        public int WarnCount { get; private set; }
        public int RestartCount { get; private set; }
        /// <summary>Spike aid: when above 0, logs <c>mic.level</c> (dB) this often. Off by default (the log call allocates).</summary>
        public float LevelLogIntervalSec;

        // ---- control -------------------------------------------------------------------------------------------

        /// <summary>(Re)starts capture from the current flags. Ends whatever was running first.</summary>
        public void Start(double nowSec)
        {
            _started = true;
            StopCore();
            FlagSet f = _flags();
            _cfgWav = f != null && string.Equals(f.audioSource, "wav", StringComparison.OrdinalIgnoreCase);
            _cfgDevice = (f != null && f.audio != null && f.audio.device != null) ? f.audio.device : "";
            _cfgWavFile = (f != null && f.wavReplayFile != null) ? f.wavReplayFile : "";
            _retryOnFocus = false;
            _everFlowed = false;
            _lastFrameSec = nowSec;
            _zeroRunFrames = 0;
            _warnedSilent = false;
            _warnedPumpError = false;
            if (_cfgWav) StartReplay(nowSec); else StartMic(nowSec);
            _epoch = _epoch + 1;    // after the ring and rate are published, so a reader that sees the new epoch also sees the new rate
        }

        /// <summary>Stops capturing and clears the device name.</summary>
        public void Stop()
        {
            _started = false;
            StopCore();
            _state = State.Idle;
            SetDevice("");
            _epoch = _epoch + 1;
        }

        /// <summary>Call every frame with a monotonic clock and the frame time.</summary>
        public void Tick(double nowSec, float dtSec)
        {
            switch (_state)
            {
                case State.Starting:
                case State.Running:
                    if (_replay != null) TickReplay(nowSec, dtSec); else TickMic(nowSec, dtSec);
                    break;
                case State.AwaitingPermission:
                    _meter.Idle(dtSec);
                    if (_mic.HasPermission) Start(nowSec);
                    break;
                default:
                    _meter.Idle(dtSec);
                    break;
            }
            MaybeLogLevel(nowSec);
        }

        /// <summary>The app gained or lost focus. Regaining it retries a capture that failed to start (e.g. permission just granted).</summary>
        public void OnFocus(bool hasFocus, double nowSec)
        {
            if (!hasFocus || !_started) return;
            if (_state == State.Failed && _retryOnFocus)
            {
                UGALog.Event("mic.retry", "reason", "app regained focus");
                Start(nowSec);
            }
        }

        /// <summary>Flags were reloaded: restart if the source, device or file changed (hot reload).</summary>
        public void OnFlagsChanged(double nowSec)
        {
            if (!_started) return;
            FlagSet f = _flags();
            bool wav = f != null && string.Equals(f.audioSource, "wav", StringComparison.OrdinalIgnoreCase);
            string device = (f != null && f.audio != null && f.audio.device != null) ? f.audio.device : "";
            string file = (f != null && f.wavReplayFile != null) ? f.wavReplayFile : "";
            bool changed = wav != _cfgWav
                || (!wav && !string.Equals(device, _cfgDevice, StringComparison.Ordinal))
                || (wav && !string.Equals(file, _cfgWavFile, StringComparison.Ordinal));
            if (!changed) return;
            UGALog.Event("mic.reconfigure", "source", wav ? "wav" : "mic", "device", device, "file", file);
            Start(nowSec);
        }

        // ---- start-up paths ------------------------------------------------------------------------------------

        void StartMic(double nowSec)
        {
            if (!_mic.HasPermission)
            {
                _mic.RequestPermission();
                _state = State.AwaitingPermission;
                SetDevice("");
                WarnOnce("mic.permission", "microphone permission is not granted (Application.HasUserAuthorization is false); it was requested, capture starts when it is granted");
                return;
            }

            string[] devices = SafeDevices();
            LogDevices(devices, _cfgDevice);
            if (devices == null || devices.Length == 0)
            {
                Fail("mic.denied", "no microphone devices are listed: none is connected, or microphone permission is denied (macOS: System Settings > Privacy & Security > Microphone). Capture is off; it retries when the app regains focus");
                return;
            }

            int idx = MicDevicePicker.Pick(devices, _cfgDevice);
            string device = idx >= 0 ? devices[idx] : null;      // null = default device
            int minFreq = 0, maxFreq = 0;
            try { _mic.GetDeviceCaps(device, out minFreq, out maxFreq); } catch (Exception) { minFreq = 0; maxFreq = 0; }
            int rate = MicDevicePicker.ChooseSampleRate(minFreq, maxFreq, RequestedSampleRate);

            IClipData clip;
            try { clip = _mic.Start(device, ClipSeconds, rate); }
            catch (Exception e)
            {
                Fail("mic.denied", "Microphone.Start(" + (device ?? "default") + ") threw " + e.GetType().Name + ": " + e.Message + ". Capture is off; it retries when the app regains focus");
                return;
            }
            if (clip == null)
            {
                Fail("mic.denied", "Microphone.Start returned null for " + (device ?? "the default device") + " (device busy, unplugged, or microphone permission denied). Capture is off; it retries when the app regains focus");
                return;
            }
            _micDevice = device;
            _micRunning = true;

            if (clip.Frames <= 0 || clip.Channels <= 0)
            {
                StopCore();
                Fail("mic.denied", "Microphone.Start gave a clip with " + clip.Frames + " frames and " + clip.Channels + " channels. Capture is off; it retries when the app regains focus");
                return;
            }
            int actualRate = clip.SampleRate > 0 ? clip.SampleRate : rate;
            EnsureRing(actualRate);
            _pump.Attach(clip, _ring, actualRate);                // catch-up limit: 1 s
            _state = State.Starting;
            SetDevice(device ?? DefaultDeviceLabel);

            bool recording = false;
            try { recording = _mic.IsRecording(device); } catch (Exception) { }
            UGALog.Event("mic.start", "device", _deviceName, "rate", actualRate,
                "info", "requested=" + rate + " caps=" + minFreq + "-" + maxFreq + " channels=" + clip.Channels + " clipFrames=" + clip.Frames + " recording=" + recording);
        }

        void StartReplay(double nowSec)
        {
            if (_cfgWavFile.Length == 0)
            {
                Fail("mic.wav", "audioSource is \"wav\" but wavReplayFile is empty. Capture is off; it retries when the app regains focus");
                return;
            }
            string path = Path.Combine(_streamingAssetsPath, _cfgWavFile);
            WavData data;
            string error;
            bool ok;
            try { ok = _wavLoader(path, out data, out error); }
            catch (Exception e) { data = null; error = e.GetType().Name + ": " + e.Message; ok = false; }
            if (!ok || data == null)
            {
                Fail("mic.wav", "audioSource is \"wav\" but the replay file could not be used: " + error + ". Capture is off; it retries when the app regains focus");
                return;
            }
            _replay = new WavReplaySource(data);
            EnsureRing(data.SampleRate);
            _replay.Advance(nowSec);                              // anchors the replay clock
            _state = State.Running;
            _warnedFailure = false;
            SetDevice("wav:" + _cfgWavFile);
            UGALog.Event("mic.start", "device", _deviceName, "rate", data.SampleRate,
                "info", "replay seconds=" + data.DurationSec.ToString("F2", CultureInfo.InvariantCulture) + " sourceChannels=" + data.SourceChannels + " bits=" + data.SourceBitsPerSample);
        }

        // ---- per-frame work ------------------------------------------------------------------------------------

        void TickMic(double nowSec, float dtSec)
        {
            int frames = 0;
            try { frames = _pump.Pump(_mic.GetPosition(_micDevice)); }
            catch (Exception e)
            {
                frames = 0;
                if (!_warnedPumpError)
                {
                    _warnedPumpError = true;
                    Warn("mic.error", "reading the microphone clip failed: " + e.GetType().Name + ": " + e.Message + " (the watchdog will restart capture if no frames arrive)");
                }
            }

            if (frames > 0)
            {
                _lastFrameSec = nowSec;
                _everFlowed = true;
                _consecutiveStalls = 0;
                if (_state == State.Starting)
                {
                    _state = State.Running;
                    _warnedFailure = false;
                    _warnedPumpError = false;
                }
                _meter.Add(_pump.LastSumSquares, _pump.LastPeak, frames, _sampleRate);
                TrackSilence(_pump.LastPeak, frames);
                CheckLoss(nowSec);
                return;
            }

            _meter.Idle(dtSec);
            double limit = _everFlowed ? StallRestartSec : FirstFrameGraceSec;
            if (nowSec - _lastFrameSec >= limit) Restart(nowSec, _everFlowed ? "no new frames for " + StallRestartSec.ToString("0.#", CultureInfo.InvariantCulture) + " s" : "no frames " + FirstFrameGraceSec.ToString("0.#", CultureInfo.InvariantCulture) + " s after start");
        }

        void TickReplay(double nowSec, float dtSec)
        {
            _replay.Advance(nowSec);
            double sumSq = 0;
            float peak = 0f;
            int total = 0;
            FloatRingBuffer ring = _ring;
            int n;
            while ((n = _replay.Read(_scratch, 0, _scratch.Length)) > 0)
            {
                double s; float p;
                InputLevelMeter.Measure(_scratch, 0, n, out s, out p);
                sumSq += s;
                if (p > peak) peak = p;
                total += n;
                ring.Write(_scratch, 0, n);
            }
            if (total > 0) _meter.Add(sumSq, peak, total, _sampleRate); else _meter.Idle(dtSec);
            CheckLoss(nowSec);
        }

        void Restart(double nowSec, string reason)
        {
            RestartCount++;
            _consecutiveStalls++;
            if (_consecutiveStalls == 3)
                Warn("mic.stalled", "the microphone delivered no audio after 3 restarts (" + reason + "). Possible causes: macOS microphone permission denied for the app that started Unity (System Settings > Privacy & Security > Microphone), device unplugged or in use by another app");
            if (RestartCount <= 3 || RestartCount % 10 == 0)
                UGALog.Event("mic.restart", "reason", reason, "count", RestartCount, "device", _deviceName);
            int stalls = _consecutiveStalls;
            Start(nowSec);                                        // re-reads the flags and the device list
            _consecutiveStalls = stalls;
        }

        void TrackSilence(float peak, int frames)
        {
            if (peak > 0f) { _zeroRunFrames = 0; return; }
            _zeroRunFrames += frames;
            if (!_warnedSilent && _zeroRunFrames >= (long)(SilenceWarnSec * _sampleRate))
            {
                _warnedSilent = true;
                Warn("mic.silent", "the input has been exactly 0.0 for " + SilenceWarnSec.ToString("0.#", CultureInfo.InvariantCulture)
                    + " s: a muted device, a noise gate in the device or driver, or denied microphone permission (macOS delivers nothing useful then)");
            }
        }

        void CheckLoss(double nowSec)
        {
            long lost = LostSamples;
            if (lost == _lastReportedLoss) return;
            if (nowSec - _lastLossWarnSec < 5.0) return;
            Warn("mic.overflow", "audio lost: " + lost + " samples in total (the ring buffer was full because the reader is too slow, or the main thread stalled for more than 1 s)");
            _lastReportedLoss = lost;
            _lastLossWarnSec = nowSec;
        }

        void MaybeLogLevel(double nowSec)
        {
            float interval = LevelLogIntervalSec;
            if (!(interval > 0f) || nowSec < _nextLevelLogSec) return;
            _nextLevelLogSec = nowSec + interval;
            UGALog.Event("mic.level", "device", _deviceName, "rmsDb", Db(_meter.Rms), "peakDb", Db(_meter.Peak));
        }

        static float Db(float linear)
        {
            return (float)Math.Round(20.0 * Math.Log10(Math.Max(linear, 1e-6)), 1);
        }

        // ---- helpers -------------------------------------------------------------------------------------------

        void StopCore()
        {
            if (_micRunning)
            {
                _pump.Detach();
                try { _mic.End(_micDevice); } catch (Exception) { }
                _micRunning = false;
            }
            _replay = null;
        }

        void Fail(string evt, string message)
        {
            _state = State.Failed;
            _retryOnFocus = true;
            SetDevice("");
            WarnOnce(evt, message);
        }

        void WarnOnce(string evt, string message)
        {
            if (_warnedFailure) return;
            _warnedFailure = true;
            Warn(evt, message);
        }

        void Warn(string evt, string message)
        {
            WarnCount++;
            UGALog.Warn(evt, message);
        }

        void SetDevice(string name)
        {
            if (string.Equals(_deviceName, name, StringComparison.Ordinal)) return;
            _deviceName = name;
            Action<string> handler = DeviceChanged;
            if (handler == null) return;
            try { handler(name); }
            catch (Exception e) { Warn("mic.devicechanged", "a DeviceChanged handler threw " + e.GetType().Name + ": " + e.Message); }
        }

        // Keeps the current ring when the rate is unchanged (unread audio stays valid); a new rate gets a new ring.
        void EnsureRing(int rate)
        {
            FloatRingBuffer old = _ring;
            if (old != null && _sampleRate == rate) return;
            if (old != null) _droppedCarry += old.DroppedSamples;
            int capacity = (int)Math.Ceiling(RingSeconds * rate);
            _ring = new FloatRingBuffer(capacity);
            _sampleRate = rate;
        }

        string[] SafeDevices()
        {
            try { return _mic.Devices(); }
            catch (Exception e)
            {
                Warn("mic.error", "Microphone.devices threw " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        // Logs the device list once, and again only if it changed (e.g. the glasses were plugged in).
        void LogDevices(string[] devices, string wanted)
        {
            if (SameList(devices, _loggedDevices)) return;
            _loggedDevices = devices == null ? null : (string[])devices.Clone();
            int count = devices == null ? 0 : devices.Length;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                int lo = 0, hi = 0;
                try { _mic.GetDeviceCaps(devices[i], out lo, out hi); } catch (Exception) { }
                if (i > 0) sb.Append(" | ");
                sb.Append('"').Append(devices[i]).Append("\" [").Append(lo).Append('-').Append(hi).Append(" Hz]");
            }
            UGALog.Event("mic.devices", "count", count, "list", sb.ToString(), "wanted", wanted ?? "");
        }

        static bool SameList(string[] a, string[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }
    }
}
