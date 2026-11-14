using System;
using UnityEngine;
using UntitledGuitarApp.Runtime.Audio;
using UntitledGuitarApp.Runtime.Flags;
using UntitledGuitarApp.Runtime.Gaze;
using UntitledGuitarApp.Runtime.GhostHand;
using UntitledGuitarApp.Runtime.Head;
using UntitledGuitarApp.Runtime.Hud;
using UntitledGuitarApp.Runtime.Logging;
using UntitledGuitarApp.Runtime.Neck;
using UntitledGuitarApp.Runtime.Practice.Learn;
using UntitledGuitarApp.Runtime.Render;
using UntitledGuitarApp.Runtime.Spectator;

namespace UntitledGuitarApp.Runtime
{
    /// <summary>
    /// The only object in Scenes/Main.unity. Everything else is created from code here, so nobody ever hand-edits scene
    /// YAML (unity/CLAUDE.md: "one Bootstrap object; everything else spawns from code/prefabs"). Editor/SceneBuilder.cs
    /// makes the scene. Composition order: flags and the session recorder, the stereo rig (its Head is the wearer's head),
    /// IMU head pose driving that head (head-locked when packets stop), the spectator view on the Mac screen, the
    /// head-locked HUD (chord label, mic meter, SIMULATED badge), audio capture (mic, or WAV replay when flags say
    /// audioSource = wav), gaze-dwell input with its reticle, and the floating neck with the G chord's fingertip dots and the
    /// capsule fretting ghost hand forming G on it. Operator keys (DemoKeys, run by DemoDirector, which also composes the strumming hand, play-along and learn mode):
    /// L learn, P play, N next song/chord, [ ] tempo, S skip, R recenter the IMU heading and re-place the neck, I/Esc idle (Space is gaze
    /// confirm). In the editor, menu UGA > Editor Sim Rig on the Head gives mouse-look; it writes the head's world rotation
    /// after the IMU (execution order 10000 vs -100), so it simply wins while attached.
    /// The neck layout and the G dots come from Person 2's Core (FretboardGeometry + ChordLibrary) through
    /// Neck/CoreNeckLayout; Neck/Dev/DevNeckLayout remains only for previews and tests written before Core landed.
    /// Not wired yet: neck alignment (Core's NeckSolver is in; the aligner is next), the strumming hand, Core's practice events.
    /// Smoke run of a built player (no window, quits by itself, writes Player.log):
    ///   build/UntitledGuitarApp.app/Contents/MacOS/"Untitled Guitar App" -batchmode -nographics -ugaSmokeSeconds 5 -logFile build/smoke.log
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Bootstrap : MonoBehaviour
    {
        // Head-locked placement for the walking skeleton, in degrees from the view centre at HudDistanceM, for text at its TRUE
        // size (HudText since 14 Nov 12:00; before, it drew ~0.79 of the request). Derived, not eyeballed, from the 11:38
        // player frame in both eyes (the view is 46.1 x 26.9 degrees, 39.4 px/degree; "comfortable" = every ink edge at least
        // 2 degrees inside each eye's frame). Obstacles: the neck with its X/O row, finger numbers and dots, and the fretting
        // hand in all six poses (skeleton from the pose files, plus 2.14 degrees of flesh measured on the frame's G hand; zone
        // top +5.3). Chord row ("G  next C": ChordLabel puts "next" beside the name since 14 Nov), worst case "Am  next  Am":
        // ink el +6.7 to +11.0, az -18.8 to +2.0, 1.4 degrees above the hand zone. Mic meter, top right: bar +9.5 to +10.7,
        // label +6.7 to +8.0 (az +2.5 to +19.5 with "NO DATA"), dB line +3.9 to +5.2. SIMULATED badge (23 degrees wide):
        // bottom centre, el -11.2 to -9.3, 1.5 degrees under the finger numbers. Clean at a 0.4 degree clearance in both
        // eyes; the check flags the 11:38 overlap and a planted 2 degree drop of the chord row. The neck HUD card (Hud/Play)
        // takes over the chord name once Core's practice events drive it.
        const float HudDistanceM = 1f;
        const float ChordAzimuthDeg = -14.5f, ChordElevationDeg = 8.5f;
        const float MeterAzimuthDeg = 2.5f, MeterElevationDeg = 11f;     // top-left corner of the meter; its lines hang below
        const float BadgeAzimuthDeg = 0f, BadgeElevationDeg = -10.25f;
        const float StatusIntervalSec = 0.5f;
        const string SmokeArgument = "-ugaSmokeSeconds";
        // With -ugaSnapshot <file.png>, the smoke run renders both eye cameras into one side-by-side frame (what the glasses
        // show) and writes it before quitting. Needs graphics: use -batchmode without -nographics.
        const string SnapshotArgument = "-ugaSnapshot";

        /// <summary>The live instance (one per app). Null before Awake and after the object is destroyed.</summary>
        public static Bootstrap Instance { get; private set; }

        public StereoRig Rig { get; private set; }
        /// <summary>The wearer's head. Head pose (IMU, or EditorSimRig in the editor) rotates it; HUD widgets hang under it.</summary>
        public Transform Head { get { return Rig != null ? Rig.Head : null; } }
        public ImuHeadPose HeadPose { get; private set; }
        public SpectatorCam Spectator { get; private set; }
        public ChordLabel Chords { get; private set; }
        public MicMeter Meter { get; private set; }
        public SimulatedBadge Badge { get; private set; }
        public MicCapture Mic { get; private set; }
        public GazeDwell Gaze { get; private set; }
        public Reticle Reticle { get; private set; }
        public FloatingNeck Neck { get; private set; }
        /// <summary>The fallback fretting ghost hand (procedural capsule hand), parented to the neck's socket: neck frame, metres.
        /// Hidden while the Blender-modelled hand (<see cref="PosedFrettingHand"/>) is available.</summary>
        public CapsuleHand FrettingHand { get; private set; }
        /// <summary>The Blender-modelled fretting hand driven by the exported pose files, or null when its FBX or poses are
        /// missing (then the capsule hand shows: still a hand, never dots only).</summary>
        public PosedGhostHand PosedFrettingHand { get; private set; }
        /// <summary>Every pose file in StreamingAssets/poses (12 from Blender: fretting rest/G/C/D/Em/Am, six strumming).</summary>
        public GhostPoseLibrary Poses { get; private set; }
        /// <summary>The operator's modes over this rig (idle / learn / play, DemoKeys); null if it failed to compose, and the
        /// walking skeleton still runs.</summary>
        public DemoDirector Demo { get; private set; }
        /// <summary>Where the neck lives: head-locked, placed in the world, or aligned to the real guitar (SL8).</summary>
        public NeckAnchor Anchor { get; private set; }
        /// <summary>The 4-glance alignment and the one-glance re-anchor (operator keys A and Z, through DemoDirector).</summary>
        public NeckAligner Aligner { get; private set; }

        readonly FingertipDot[] _dots = new FingertipDot[FloatingNeck.MaxDots];
        readonly StringMark[] _marks = new StringMark[6];
        readonly Vector3[] _fingertipTargets = new Vector3[4];
        int _dotCount;
        float _nextStatusAt;
        int _shownSampleRate = -1;
        int _statusFps = -1, _statusRate = -1, _statusImuHz = -1;
        bool _statusCapturing, _statusReplay, _statusStereo, _statusTracking;
        string _statusDevice = "";
        string _statusDemo = "";
        float _smokeQuitAt = -1f;
        string _snapshotPath;
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

            // IMU over UDP from tools/imu-bridge (127.0.0.1:47800). Head-locked (identity) when packets stop for imu.staleMs
            // or when flags say anchorMode = headLocked; tracking resumes by itself when packets return.
            var headPoseObject = new GameObject("ImuHeadPose");
            headPoseObject.transform.SetParent(transform, false);
            HeadPose = headPoseObject.AddComponent<ImuHeadPose>();
            HeadPose.Target = Rig.Head;

            Spectator = SpectatorCam.Create(null);
            Spectator.Head = Rig.Head;

            Chords = ChordLabel.Create(Rig.Head, HudDistanceM);
            HudLayout.Place(Chords.transform, ChordAzimuthDeg, ChordElevationDeg, HudDistanceM);
            // The walking skeleton's chord comes from the flags (flags.chords[0], the judge profile's chord; else G), like
            // learn and idle modes. SetChords hides "next" until practice events drive it.
            string skeletonChord = LearnChords.NameFromFlags(flags);
            Chords.SetChords(skeletonChord, null);

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

            ComposeNeckAndHand(flags);
            try
            {
                Demo = DemoDirector.Create(this, flags);   // strumming hand, play-along and learn mode, driven by DemoKeys
            }
            catch (Exception e)
            {
                UGALog.Error("demo_failed", e.ToString());   // the walking skeleton keeps running without the modes
            }

            // (stereo.apply already logs the target display; UGALog.Event takes at most three key/value pairs)
            UGALog.Event("boot_done", "stereo", Rig.IsStereo, "displays", Display.displays.Length, "dots", _dotCount);
        }

        /// <summary>
        /// The floating neck with the G chord and the fretting ghost hand on it. Head-locked (follows the head) when flags say
        /// anchorMode = headLocked; otherwise placed once in front of the head and left in the world, so it can be looked at
        /// while the IMU tracks (neck alignment, the real "aligned" mode, needs Core's NeckSolver and comes later).
        /// </summary>
        void ComposeNeckAndHand(FlagSet flags)
        {
            Neck = FloatingNeck.Create(transform);
            // Person 2's Core: FretboardGeometry (default guitar until the demo guitar is measured) + ChordLibrary, mapped to
            // the neck frame by CoreNeckLayout (Core's across axis is + toward string 6; ours is + toward string 1). The chord
            // is the flags' (the judge profile sets chords = ["Em"]), else G.
            NeckLayout layout = CoreNeckLayout.Create(CoreNeckLayout.DefaultGeometry);
            string chord = LearnChords.NameFromFlags(flags);
            _dotCount = Mathf.Max(0, CoreNeckLayout.ChordMarks(CoreNeckLayout.DefaultGeometry, chord, _dots, _marks));
            if (!Neck.Build(layout))
            {
                UGALog.Warn("neck_layout_invalid", "the dev neck layout failed NeckLayout.Validate; see the warnings above");
                return;
            }
            Neck.SetDots(_dots, _dotCount);
            Neck.SetStringMarks(_marks);
            // Where the neck lives (SL8): NeckAnchor follows the head whenever anchorMode is headLocked OR the head is not
            // tracked (the review's P0: a world-placed neck vanished below the view when tracking dropped), places it once in
            // the world while tracked, and holds the aligned pose after a successful NeckAligner run (operator key A).
            Anchor = NeckAnchor.Create(transform, Neck, Rig.Head, HeadPose);
            Aligner = NeckAligner.Create(transform, Rig.Head, Neck, HeadPose, Anchor);
            Aligner.Reticle = Reticle;

            // Left hand for a right-handed player; for a left-handed player the neck's mirrored socket mirrors the hand too.
            FrettingHand = CapsuleHand.Create(Neck.Socket, true);
            FrettingHand.SetFingertipTargets(FingertipTargets(_dots, _dotCount, _fingertipTargets), true);

            // The Blender-modelled hand takes over when its FBX (Resources/GhostHand/ghost_hand_fretting) and the pose files
            // are present; PosedGhostHand logs one warning and reports IsAvailable = false otherwise, and the capsule stays.
            Poses = new GhostPoseLibrary();
            Poses.Reload();
            PosedGhostHand posed = PosedGhostHand.Create(Neck.Socket, GhostPose.HandFretting, Poses);
            if (posed != null && posed.IsAvailable && posed.SetPose("G", true))
            {
                PosedFrettingHand = posed;
                FrettingHand.gameObject.SetActive(false);
            }
            else if (posed != null)
            {
                Destroy(posed.gameObject);
            }
            UGALog.Event("ghost_hand", "posed", PosedFrettingHand != null, "poses", Poses.Count,
                         "reason", posed != null && !posed.IsAvailable ? posed.UnavailableReason : "");
        }

        /// <summary>
        /// Fingers 1-4 (index .. pinky) to the socket-local points their pads should touch: each dot's (x, z) in the neck frame,
        /// millimetres to metres, on the fretboard plane (the hand hovers above it by itself). A finger with no dot gets a NaN x,
        /// which CapsuleHand reads as "not used".
        /// </summary>
        public static Vector3[] FingertipTargets(FingertipDot[] dots, int count, Vector3[] result)
        {
            for (int f = 0; f < result.Length; f++)
                result[f] = new Vector3(float.NaN, 0f, 0f);
            for (int i = 0; i < count && i < dots.Length; i++)
            {
                int finger = dots[i].finger;
                if (finger < 1 || finger > result.Length) continue;
                result[finger - 1] = new Vector3(dots[i].xMm * 0.001f, 0f, dots[i].zMm * 0.001f);
            }
            return result;
        }

        void Update()
        {
            FlagStore.Poll();
            if (Demo == null && HeadPose != null && Input.GetKeyDown(KeyCode.R)) RecenterView();   // else DemoKeys handles R
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
                if (!string.IsNullOrEmpty(_snapshotPath)) WriteSnapshot(_snapshotPath);
                UGALog.Event("smoke_done", "composed", _composed, "seconds", now);
                Application.Quit(_composed ? 0 : 1);
            }
        }

        /// <summary>The current heading becomes "forward" (logs imu_recenter) and the floating neck is placed in front again.</summary>
        public void RecenterView()
        {
            if (Anchor != null) Anchor.Recenter();   // recenters the IMU heading and keeps an alignment on the real guitar
            else if (HeadPose != null) HeadPose.Recenter();
        }

        /// <summary>Two lines on the Mac-screen spectator (the operator's demo line first when the modes are composed),
        /// rebuilt only when what they show has changed.</summary>
        void UpdateStatus()
        {
            if (Spectator == null || Rig == null) return;
            int fps = Mathf.RoundToInt(Rig.SmoothedFps);
            int rate = Mic != null ? Mic.SampleRate : 0;
            bool capturing = Mic != null && Mic.IsCapturing;
            bool replay = Mic != null && Mic.IsReplay;
            string device = Mic != null ? Mic.DeviceName : "";
            bool stereo = Rig.IsStereo;
            bool tracking = HeadPose != null && HeadPose.State == HeadTrackingState.Tracking;
            int imuHz = HeadPose != null ? Mathf.RoundToInt(HeadPose.PacketRateHz) : 0;
            string demo = Demo != null ? Demo.StatusText : "";   // DemoDirector rebuilds it only on change
            if (fps == _statusFps && rate == _statusRate && capturing == _statusCapturing && replay == _statusReplay &&
                stereo == _statusStereo && tracking == _statusTracking && imuHz == _statusImuHz &&
                string.Equals(device, _statusDevice, StringComparison.Ordinal) && ReferenceEquals(demo, _statusDemo))
                return;
            _statusFps = fps; _statusRate = rate; _statusCapturing = capturing; _statusReplay = replay;
            _statusStereo = stereo; _statusDevice = device; _statusTracking = tracking; _statusImuHz = imuHz;
            _statusDemo = demo;
            string audio = (replay ? "WAV replay (simulated)" : capturing ? "mic: " + device : "mic: not capturing") +
                           "  " + (rate / 1000f).ToString("0.#") + " kHz";
            string display = (stereo ? "stereo" : "2D") + " on display " + (Rig.TargetDisplay + 1) + "  " + fps + " fps  " +
                             (tracking ? "IMU " + imuHz + " Hz" : "IMU off: head-locked");
            if (string.IsNullOrEmpty(demo)) Spectator.SetStatus(audio, display);
            else Spectator.SetStatus(demo, audio + "  ·  " + display);
        }

        void ReadSmokeArgument()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (string.Equals(args[i], SnapshotArgument, StringComparison.Ordinal))
                {
                    _snapshotPath = args[i + 1];
                    continue;
                }
                if (!string.Equals(args[i], SmokeArgument, StringComparison.Ordinal)) continue;
                float seconds;
                if (float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out seconds) && seconds > 0f)
                {
                    _smokeQuitAt = Time.unscaledTime + seconds;
                    UGALog.Event("smoke_start", "seconds", seconds);
                }
            }
        }

        /// <summary>
        /// Renders the eye cameras into one side-by-side texture (their viewport rects split it into halves, exactly as on
        /// display 2) and writes it as PNG. Test aid for smoke runs; never called in normal use.
        /// APIs: Camera.targetTexture / Camera.Render, RenderTexture, Texture2D.ReadPixels, ImageConversion.EncodeToPNG.
        /// </summary>
        void WriteSnapshot(string path)
        {
            if (Rig == null) return;
            int width = Rig.IsStereo ? 3840 : 1920, height = 1080;
            var rt = new RenderTexture(width, height, 24);
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            RenderTexture previous = RenderTexture.active;
            try
            {
                Camera[] eyes = { Rig.LeftEye, Rig.RightEye };
                foreach (Camera eye in eyes)
                {
                    if (eye == null || !eye.enabled) continue;
                    eye.targetTexture = rt;
                    eye.Render();
                    eye.targetTexture = null;
                }
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
                UGALog.Event("snapshot", "path", path, "width", width, "height", height);
            }
            catch (Exception e)
            {
                UGALog.Error("snapshot_failed", e.Message);
            }
            finally
            {
                RenderTexture.active = previous;
                rt.Release();
                Destroy(rt);
                Destroy(tex);
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
