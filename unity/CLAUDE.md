# unity/ — Unity project (macOS; the glasses are display 2)
- Unity 2021.3.3f1 (Built-in render pipeline), no upgrades during the event. NO XREAL SDK (it's Android-only). Packages: com.unity.nuget.newtonsoft-json
  (+ TextMeshPro). Target: macOS standalone (Apple silicon). Player settings: Company "UntitledGuitarApp", Product "Untitled Guitar App", bundle id "com.untitledguitarapp.app".
- One scene: Assets/UntitledGuitarApp/Scenes/Main.unity with one `Bootstrap` object; everything else spawns from code/prefabs.
  Scene/prefab writer: Person 1's session only.
- Assemblies: UntitledGuitarApp.Core (noEngineReferences; precompiled ref Newtonsoft.Json.dll) · UntitledGuitarApp.Runtime → Core ·
  UntitledGuitarApp.Editor (BuildScript, GlassesGameView, sim helpers) · UntitledGuitarApp.Tests.EditMode.
- Rendering: Render/StereoRig = two eye cameras (IPD flag; asymmetric frusta from the 52° diagonal FOV) side by side into
  3840×1080 on display 2; 2D fallback = one camera at 1920×1080. SpectatorCam on display 1 (Mac screen).
- Head pose: Head/ImuHeadPose reads UDP 127.0.0.1:47800 from tools/imu-bridge; stale >250 ms → headLocked mode.
- Neck: Neck/NeckAligner (4 dwells → Core/Geometry/NeckSolver; re-anchor) · Neck/FloatingNeck (fallback).
- Compile check: Unity MCP refresh_unity → read_console (zero errors from our assemblies) → run_tests (EditMode).
- Run on the glasses: GlassesGameView (borderless Game view on display 2) in Play Mode, or BuildScript.BuildMac → build/UntitledGuitarApp.app.
- Never hand-edit .unity/.prefab/.asset/.meta/ProjectSettings (hook-blocked); use Unity MCP tools or an Editor script.
- Performance: ≥60 fps stereo at 3840×1080; unlit/emissive; no shadows/post; no per-frame allocations (pool HUD elements).
- Threading: MicCapture → ring buffer → AudioWorker thread → ConcurrentQueue → Update. IMU UDP on its own thread → latest-value slot.
- Mic: Microphone device chosen by flag audio.device (glasses mic or MacBook mic); handle a denied permission.
- Logs: UGALog writes "[UGA] {json}" lines with stack traces off for LogType.Log.
- Flags: StreamingAssets/flags.default.json, overridden by persistentDataPath/flags.json (hot-reload ≤1 s).
- EditorSimRig (mouse-look head + WAV replay) must keep working for every feature (L4).
