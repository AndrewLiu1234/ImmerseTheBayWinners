using System;
using UnityEngine;
using UntitledGuitarApp.Runtime.Audio;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Gaze;
using UntitledGuitarApp.Runtime.Hud;
using UntitledGuitarApp.Runtime.Logging;
using UntitledGuitarApp.Runtime.Render;
using UntitledGuitarApp.Runtime.Spectator;

namespace UntitledGuitarApp.Runtime
{
    /// <summary>
    /// The only object in Scenes/Main.unity. Everything else is created from code here, so nobody ever hand-edits scene
    /// YAML (unity/CLAUDE.md: "one Bootstrap object; everything else spawns from code/prefabs"). Editor/SceneBuilder.cs
    /// makes the scene. Composition order: flags and the session recorder, the stereo rig (its Head is the wearer's head),
    /// the spectator view on the Mac screen, the head-locked HUD (chord label, mic meter, SIMULATED badge), audio capture
    /// (mic, or WAV replay when flags say audioSource = wav), and gaze-dwell input with its reticle.
    /// Not wired yet (their modules are still being built): IMU head pose, the floating neck, the ghost hands.
    /// Smoke run of a built player (no window, quits by itself, writes Player.log):
    ///   build/UntitledGuitarApp.app/Contents/MacOS/"Untitled Guitar App" -batchmode -nographics -ugaSmokeSeconds 5 -logFile build/smoke.log
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Bootstrap : MonoBehaviour
    {
        // Head-locked placement for the walking skeleton, in degrees from the view centre at HudDistanceM. They sit to the
        // lower left, where the floating neck will hang; the neck HUD card replaces them once the neck and play-along HUD land.
        const float HudDistanceM = 1f;
        const float ChordAzimuthDeg = -12f, ChordElevationDeg = -4f;
        const float MeterAzimuthDeg = -18f, MeterElevationDeg = -12f;
        const float BadgeAzimuthDeg = 0f, BadgeElevationDeg = 11f;
        const float StatusIntervalSec = 0.5f;
        const string SmokeArgument = "-ugaSmokeSeconds";

        /// <summary>The live instance (one per app). Null before Awake and after the object is destroyed.</summary>
        public static Bootstrap Instance { get; private set; }

        public StereoRig Rig { get; private set; }
        /// <summary>The wearer's head. Head pose (IMU, or EditorSimRig in the editor) rotates it; HUD widgets hang under it.</summary>
        public Transform Head { get { return Rig != null ? Rig.Head : null; } }
        public SpectatorCam Spectator { get; private set; }
        public ChordLabel Chords { get; private set; }
        public MicMeter Meter { get; private set; }
        public SimulatedBadge Badge { get; private set; }
        public MicCapture Mic { get; private set; }
        public GazeDwell Gaze { get; private set; }
        public Reticle Reticle { get; private set; }

        float _nextStatusAt;
        int _shownSampleRate = -1;
        int _statusFps = -1, _statusRate = -1;
        bool _statusCapturing, _statusReplay, _statusStereo;
        string _statusDevice = "";
        float _smokeQuitAt = -1f;
        bool _composed;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);   // a second Bootstrap (for example from an additive scene) must never compose twice
                return;
            }
            Instance = this;
            try
            {
                Compose();
                _composed = true;
            }
            catch (Exception e)
            {
                // Keep whatever was built running (the spectator shows it); a half-built rig is better than a dead app.
                UGALog.Error("boot_failed", e.ToString());
            }
            ReadSmokeArgument();
        }

        void Compose()
        {
            // The glasses view and the mic must keep running while the Mac screen or another window has focus.
            // (Also a project setting via Editor/ProjectSetup; StereoRig sets it in players too.)
            Application.runInBackground = true;

            FlagStore.Initialize();
            SessionRecorder.Start();
            FlagSet flags = FlagStore.Current;
            UGALog.Event("boot", "profile", flags.profile, "anchorMode", flags.anchorMode, "audioSource", flags.audioSource);

            Rig = StereoRig.Create(transform);
            Rig.Head.gameObject.AddComponent<AudioListener>();   // the rig adds none, and Unity warns every frame without one

            Spectator = SpectatorCam.Create(null);
            Spectator.Head = Rig.Head;

            Chords = ChordLabel.Create(Rig.Head, HudDistanceM);
            HudLayout.Place(Chords.transform, ChordAzimuthDeg, ChordElevationDeg, HudDistanceM);
            Chords.SetChords("G", "C");   // walking skeleton: fixed until Core's practice events drive it

            Meter = MicMeter.Create(Rig.Head, HudDistanceM);
            HudLayout.Place(Meter.transform, MeterAzimuthDeg, MeterElevationDeg, HudDistanceM);

            Badge = SimulatedBadge.Create(Rig.Head, HudDistanceM);   // shows itself whenever audioSource is not "mic"
            HudLayout.Place(Badge.transform, BadgeAzimuthDeg, BadgeElevationDeg, HudDistanceM);

            var micObject = new GameObject("MicCapture");
            micObject.transform.SetParent(transform, false);
            Mic = micObject.AddComponent<MicCapture>();   // mic, or the WAV in flags.wavReplayFile when audioSource = wav

            Gaze = GazeDwell.GetOrCreate();
            Gaze.Head = Rig.Head;   // the head moves with the IMU on the glasses, or with EditorSimRig's mouse-look in the editor
            var reticleObject = new GameObject("Reticle");
            reticleObject.transform.SetParent(transform, false);
            Reticle = reticleObject.AddComponent<Reticle>();
            Reticle.Gaze = Gaze;
            Reticle.Head = Rig.Head;

            UGALog.Event("boot_done", "stereo", Rig.IsStereo, "display", Rig.TargetDisplay, "displays", Display.displays.Length);
        }

        void Update()
        {
            FlagStore.Poll();
            if (Meter != null && Mic != null)
            {
                Meter.SetLevel(Mic.LevelRms, Mic.LevelPeak);
                int rate = Mic.SampleRate;
                if (rate != _shownSampleRate)
                {
                    Meter.SetSampleRate(rate);
                    _shownSampleRate = rate;
                }
            }
            float now = Time.unscaledTime;
            if (now >= _nextStatusAt)
            {
                _nextStatusAt = now + StatusIntervalSec;
                UpdateStatus();
            }
            if (_smokeQuitAt >= 0f && now >= _smokeQuitAt)
            {
                _smokeQuitAt = -1f;
                UGALog.Event("smoke_done", "composed", _composed, "seconds", now);
                Application.Quit(_composed ? 0 : 1);
            }
        }

        /// <summary>Two lines on the Mac-screen spectator, rebuilt only when what they show has changed.</summary>
        void UpdateStatus()
        {
            if (Spectator == null || Rig == null) return;
            int fps = Mathf.RoundToInt(Rig.SmoothedFps);
            int rate = Mic != null ? Mic.SampleRate : 0;
            bool capturing = Mic != null && Mic.IsCapturing;
            bool replay = Mic != null && Mic.IsReplay;
            string device = Mic != null ? Mic.DeviceName : "";
            bool stereo = Rig.IsStereo;
            if (fps == _statusFps && rate == _statusRate && capturing == _statusCapturing && replay == _statusReplay &&
                stereo == _statusStereo && string.Equals(device, _statusDevice, StringComparison.Ordinal))
                return;
            _statusFps = fps; _statusRate = rate; _statusCapturing = capturing; _statusReplay = replay;
            _statusStereo = stereo; _statusDevice = device;
            string audio = replay ? "WAV replay (simulated)" : capturing ? "mic: " + device : "mic: not capturing";
            Spectator.SetStatus(audio + "  " + (rate / 1000f).ToString("0.#") + " kHz",
                                (stereo ? "stereo" : "2D") + " on display " + (Rig.TargetDisplay + 1) + "  " + fps + " fps");
        }

        void ReadSmokeArgument()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (!string.Equals(args[i], SmokeArgument, StringComparison.Ordinal)) continue;
                float seconds;
                if (float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out seconds) && seconds > 0f)
                {
                    _smokeQuitAt = Time.unscaledTime + seconds;
                    UGALog.Event("smoke_start", "seconds", seconds);
                }
                return;
            }
        }

        void OnApplicationQuit()
        {
            SessionRecorder.Stop();
        }

        void OnDestroy()
        {
            if (Instance != this) return;
            SessionRecorder.Stop();
            Instance = null;
        }
    }
}
