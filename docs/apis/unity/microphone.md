# Unity 2021.3 microphone capture: exact excerpts

Sat 2026-11-14 03:17 PT · Person 1 · for `Runtime/Audio/MicCapture` and spike M-S3. Excerpts are verbatim; "NOT FOUND" marks what no source states.
**Version:** Unity **2021.3.3f1** (the editor on the Mac). The compiler check (`cs-check.sh`) compiles against that version's real `UnityEngine.AudioModule.dll`.

## Sources (S1–S5)

- **S1** Offline Scripting API shipped with the editor, page footer "Version: 2021.3 … Publication Date: 2022-05-27":
  `/Applications/Unity/Hub/Editor/2021.3.3f1/Unity.app/Contents/Documentation/en/ScriptReference/<Page>.html`
  (`Microphone`, `Microphone.Start`, `Microphone.End`, `Microphone.GetDeviceCaps`, `Microphone.GetPosition`, `Microphone.IsRecording`,
  `Microphone-devices`, `AudioClip.GetData`, `AudioClip-frequency`, `Application.RequestUserAuthorization`, `Application.HasUserAuthorization`, `UserAuthorization`,
  `Application-runInBackground`, `MonoBehaviour.OnApplicationFocus`). Online twins (they follow the NEWEST 2021.3 patch, so wording can be newer than 3.3f1):
  `https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Microphone.Start.html` (and the same pattern for the other pages).
- **S2** XML doc comments of the same build: `…/Contents/Managed/UnityEngine/UnityEngine.AudioModule.xml`.
- **S3** The managed IL of the same build, disassembled with the editor's own tool (read-only, no editor started):
  `U=/Applications/Unity/Hub/Editor/2021.3.3f1/Unity.app/Contents; "$U/MonoBleedingEdge/bin/monodis" "$U/Managed/UnityEngine/UnityEngine.AudioModule.dll"`.
  This shows what the thin C# wrappers do before the native call (the native side is closed source: NOT FOUND).
- **S4** Unity Support, "How to: Request/check iOS & macOS microphone permission in Unity" (page says updated 2025-09-09): `https://support.unity.com/hc/en-us/articles/4431473872020`.
- **S5** Community, unofficial: `https://discussions.unity.com/t/microphone-audio-is-silent/767557`, `https://discussions.unity.com/t/opt-in-permissions-and-info-plist-in-macos-catalina/787347`.

## `UnityEngine.Microphone` (static class, `UnityEngine.AudioModule`)  [S1, S2]

| Member | Declaration (verbatim) | Doc text (verbatim) |
|---|---|---|
| `devices` | `public static string[] devices;` | "A list of available microphone devices, identified by name." |
| `Start` | `public static AudioClip Start(string deviceName, bool loop, int lengthSec, int frequency);` | "Start Recording with device." Returns: "The function returns null if the recording fails to start." |
| `End` | `public static void End(string deviceName);` | "Stops recording." |
| `GetDeviceCaps` | `public static void GetDeviceCaps(string deviceName, out int minFreq, out int maxFreq);` | "Get the frequency capabilities of a device." |
| `GetPosition` | `public static int GetPosition(string deviceName);` | "Get the position in samples of the recording." |
| `IsRecording` | `public static bool IsRecording(string deviceName);` | "Query if a device is currently recording." |

Parameters of `Start`: `deviceName` "The name of the device." · `loop` "Indicates whether the recording should continue recording if lengthSec is reached, and wrap around and record from the beginning of the AudioClip." · `lengthSec` "Is the length of the AudioClip produced by the recording." · `frequency` "The sample rate of the AudioClip produced by the recording."

Verbatim sentences that matter:
- Every method: "If you pass a null or empty string for the device name then the default microphone will be used. You can get a list of available microphone devices from the devices property." (S1)
- `Start`: "You can find the range of sample rates supported by a microphone using the GetDeviceCaps property." (S1)
- `GetDeviceCaps`: "When a value of zero is returned in the minFreq and maxFreq parameters, this indicates that the device supports any frequency." (S1)
- `GetPosition`: "You can use this to control latency. If you want a 30ms latency, poll GetPosition() until 30ms (in samples) has gone and then start the audio." (S1)
- Community, relevant to start-up (S5, "Microphone Audio is Silent", reply of 3 Dec 2019, Unity 2018.3 / macOS 10.14.4): "you have to yield until microphone position is > 0 before calling Play()". Treat position 0 right after `Start` as "no data yet", not as an error.

### What the 2021.3.3f1 managed code does around the native calls  [S3]

Read from the IL of `Microphone` (the docs do not say this):
- `Start(name, loop, lengthSec, frequency)` first maps the name to a device id; if the id is -1 it **throws** `ArgumentException("Couldn't acquire device ID for device name " + name)`. It also throws `ArgumentException` for `lengthSec <= 0` ("Length of recording must be greater than zero seconds"), `lengthSec > 3600` ("Length of recording must be less than one hour") and `frequency <= 0` ("Frequency of recording must be greater than zero"). Then it calls the native `StartRecord(deviceID, loop, (float)lengthSec, frequency)`. **So a stale device name throws instead of returning null: wrap `Start` in try/catch.**
- With an unknown name the other calls fail quietly: `GetPosition` returns 0, `IsRecording` returns false, `End` returns, and `GetDeviceCaps` leaves `minFreq = maxFreq = 0`, which looks exactly like "supports any frequency". Do not trust (0, 0) from a name you did not take from `devices`.
- `devices` is an internal call named `GetRecordDevices` that builds a managed `string[]`; assume it **allocates a new array per call** (inference from the signature, not stated anywhere). Read it at start/restart only, never per frame.
- `GetMicrophoneDeviceIDFromName` and `GetRecordPosition` carry `IsThreadSafe` on their native bindings. We still call every Unity API on the main thread (project rule).

## `UnityEngine.AudioClip`

`AudioClip.GetData`  [S1, S2, S3]
```csharp
public bool GetData(float[] data, int offsetSamples);
```
"Fills an array with sample data from the clip. The samples are floats ranging from -1.0f to 1.0f. The sample count is determined by the length of the float array. Use the offsetSamples parameter to start the read from a specific position in the clip. If the read length from the offset is longer than the clip length, the read will wrap around and read the remaining samples from the start of the clip." (S1)
The current online 2021.3 page adds: "For multi-channel audio clips, the data is interleaved", "If `GetData` can't read the audio clip, the `data` parameter will contain zeroes for all sample values, the console will log an error, and `GetData` will return false." and "For the best performance, use the Span version because you don't need to allocate managed memory." The `Span<float>` overload is NOT in 2021.3.3f1 (only `GetData(float[], int)` exists in the S3 IL); we do not use it.

What it allocates: the 3.3f1 wrapper (S3) is `numSamples = data.Length / channels; return GetData(this, data, numSamples, offsetSamples);` with an `[Out]` array handed to the native side, and a `Debug.Log` + `return false` when `channels <= 0`. It never allocates the array: **you pass a buffer and it fills it, so a preallocated buffer means zero managed allocation per call.** Consequences used by `LoopingClipPump`: (1) the number of samples read is `data.Length / channels`, so a fixed-size buffer always reads that many; we keep one buffer per power-of-two size and read each block exactly; (2) `offsetSamples` and the count are in sample FRAMES (per channel); (3) no block we request crosses the end of the looping clip, so we never depend on the documented wrap-around.

Properties (S1): `public int frequency;` "The sample frequency of the clip in Hertz. (Read Only)" · `public int samples;` "The length of the audio clip in samples. (Read Only)" · `public int channels;` "The number of channels in the audio clip. (Read Only)" · `public float length;` "The length of the audio clip in seconds. (Read Only)".

## Permission APIs  [S1]

```csharp
public static AsyncOperation RequestUserAuthorization(UserAuthorization mode);
public static bool HasUserAuthorization(UserAuthorization mode);
```
- `RequestUserAuthorization`: "Request authorization to use the webcam or microphone on iOS and WebGL." and "Note: Use Application.HasUserAuthorization to query the result of the operation." (the XML comment in S2 says "on iOS")
- `HasUserAuthorization`: "Check if the user has authorized use of the webcam or microphone in the Web Player. This is used to check the result of a previous call to Application.RequestUserAuthorization. Explicit user consent to use features is only needed in the Web Player. **In other build targets, this function will always return true.**" (the online page repeats this sentence)
- `UserAuthorization` enum values: `WebCam` "Request permission to use any video input sources attached to the computer." · `Microphone` "Request permission to use any audio input sources attached to the computer."
- S4 (updated 2025) shows `yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);` then `Application.HasUserAuthorization(UserAuthorization.Microphone)` for "iOS & macOS". Its text says nothing about macOS-specific behaviour and lists no Unity versions, so it contradicts nothing in S1 for 2021.3.3f1 but does not prove macOS support there. The Mac player library contains the `UnityEngine.Application::HasUserAuthorization` / `RequestUserAuthorization` registrations (checked with `strings` on `UnityPlayer.dylib`, arm64 variation) but no `requestAccessForMediaType`/`authorizationStatusForMediaType` strings.

**Conclusion for macOS standalone / Editor in 2021.3.3f1: `HasUserAuthorization(Microphone)` cannot report a denial (documented "always true"). Denial has to be detected by behaviour:** `Microphone.devices` empty, `Start` returning null or throwing, `GetPosition` never moving, or all-zero samples. `MicCapture` still calls `HasUserAuthorization` first (and `RequestUserAuthorization` if it ever returns false) so the code is right on the platforms where the docs say it matters.

## macOS player settings for the microphone message  [S3, compile check]

- Editor API (not in the offline docs, but present and compiles against 3.3f1): `UnityEditor.PlayerSettings.macOS.microphoneUsageDescription` (`static string`, get/set; also `cameraUsageDescription`, `bluetoothUsageDescription`). Found in `UnityEditor.CoreModule.dll` and used by the Mac build (`MacStandalonePlayerBuildProgram.Data.dll`: `MacPlayerBuildConfig.MicrophoneUsageDescription`). Proof: `cs-check.sh --editor --no-stubs` of a file that reads and writes it gave 0 errors.
- Inspector: **Project Settings › Player › the "PC, Mac & Linux Standalone" tab › Other Settings › Configuration › "Mac Configuration" › "Microphone Usage Description\*"**. Tooltip, verbatim from the editor IL: "String shown to the user when requesting to use the device microphone. Written to the NSMicrophoneUsageDescription field in Xcode project's info.plist file".
- NOT FOUND: the code that writes `NSMicrophoneUsageDescription` into the built `.app` (the build tool is closed source; the player Info.plist template in `PlaybackEngines/MacStandaloneSupport/Source/Player/MacPlayer/MacPlayerEntryPoint/Info.plist` has no such key, so the build must add it). Verify after the first build: `plutil -p build/UntitledGuitarApp.app/Contents/Info.plist | grep -i microphone`.
- The **Editor itself** (`Unity.app/Contents/Info.plist`, version "Unity version 2021.3.3f1") has NSCameraUsageDescription, NSPhotoLibraryUsageDescription, NSBluetoothAlwaysUsageDescription and NSAppleEventsUsageDescription but **no NSMicrophoneUsageDescription**; the binary is a thin **x86_64** Mach-O (runs under Rosetta on Apple silicon) and is not signed with the hardened runtime (`codesign -dv`: `flags=0x0(none)`). What macOS does when this Editor first opens the microphone is NOT FOUND (see `docs/apis/macos/audio.md`); community reports: the Editor/Hub never prompts and the mic stays silent or the Editor crashes (S5, and Unity issue 1227607 "macOS Editor crashes when trying to access audio capture devices", status "Won't Fix", builds 2019.1–2020.2, tied to microphone permission state; known only through a search-engine summary because the tracker page did not load).
- `Application.runInBackground` (S1): "Should the player be running when the application is in the background? Default is false (application pauses when it is in the background)." A standalone build with this off stops calling `Update` whenever the Mac app loses focus, so capture is only pumped while the window is focused. `MicCapture` does not change it; it logs one warning when it is false. Bootstrap should set it (Player Settings › Resolution and Presentation › Run In Background, or `Application.runInBackground = true`).
- `MonoBehaviour.OnApplicationFocus(bool hasFocus)` (S1): "Sent to all GameObjects when the player gets or loses focus." The retry-after-denial hook. In the Editor "this corresponds to whether the game view has focus".

## Not found (do not assume)

- The exact strings `Microphone.devices` returns on macOS (expected to equal the CoreAudio names in `docs/apis/macos/audio.md`) and their order; which entry is the default device. Spike M-S3 step 1 logs them.
- Whether `clip.frequency` equals the requested `frequency` when the device cannot do it, and whether Unity resamples or changes the device rate. `MicCapture` reads `clip.frequency` back and reports THAT as `SampleRate`.
- Whether a Unity microphone clip can have more than one channel (the docs never say). `MicCapture` mixes interleaved multi-channel clips down to mono and logs the channel count at start.
- How long `GetPosition` stays 0 after `Start`, what it returns after an unplug, and what a permission-denied Mac delivers (zeros, no frames, or a crash).
- Whether the Editor 2021.3.3f1 can access the microphone at all without a prior macOS grant for Unity or Unity Hub.
