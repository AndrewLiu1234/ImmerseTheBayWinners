# macOS audio input: device names, sample rates, microphone permission, Voice Isolation

Sat 2026-11-14 03:25 PT · Person 1 · for `Runtime/Audio/MicCapture` and spike M-S3. Companion of `docs/apis/unity/microphone.md`.
Nothing below was observed on the glasses or inside Unity (no editor was started, the glasses were not plugged in): "measured" = this MacBook only; "NOT FOUND" = no source says so.

## 1. Measured on this Mac (MacBook, macOS 26.5.1, arm64, 03:20 PT)

`swift audioin.swift` (script at the end of this file; read-only CoreAudio enumeration, it never opens a device) printed:
```
name="MacBook Pro Microphone" uid="BuiltInMicrophoneDevice" transport='bltn' inChannels=1 nominal=48000 rates=[44100,48000,88200,96000] DEFAULT_INPUT
name="Microsoft Teams Audio" uid="MSLoopbackDriverDevice_UID" transport='virt' inChannels=2 nominal=48000 rates=[48000]
```
`system_profiler SPAudioDataType` agrees: "MacBook Pro Microphone … Default Input Device: Yes · Input Channels: 1 · Manufacturer: Apple Inc. · Current SampleRate: 48000 · Transport: Built-in".

- **Built-in mic**: shows up as "MacBook Pro Microphone" on this Mac (other Mac models: NOT FOUND, do not assume the same string; "Built-in Microphone" in Unity's `Microphone.Start` doc page is only an example). One input channel, nominal 48000 Hz, can also run at 44100 / 88200 / 96000. The flag default `audio.device = "MacBook"` (substring, case-insensitive) matches it; it does not match "Microsoft Teams Audio".
- **Virtual devices** (Teams, loopback drivers) are listed like real inputs with two channels. They are the reason a loose substring such as "Audio" or "Mic" is a bad `audio.device`.
- **USB audio devices** (the glasses): enumerate as ordinary CoreAudio input devices with `transport='usb '`, per CoreAudio convention (not verified here). Their name string, channel count and rates are **NOT FOUND**: the glasses were not connected, XREAL's product page returned HTTP 429, and third-party spec pages only claim "dual microphones" without any macOS name. `docs/HOW_IT_WORKS.md` says "2 mics in, 2 speakers out" (repo claim, unverified). Spike M-S3 reads the real name: plug the glasses in, run the script below or open System Settings › Sound › Input, and copy the name into `audio.device` (a unique substring of it).
- **Does Unity show the same strings?** NOT FOUND. Unity's `Microphone.devices` is built by closed native code. MicCapture logs the list once at start (`mic.devices`, each entry with its `GetDeviceCaps` range) so M-S3 can compare it with this section.

## 2. Sample rates

- CoreAudio gives every device a nominal rate plus a list of rates it accepts (`kAudioDevicePropertyNominalSampleRate`, `kAudioDevicePropertyAvailableNominalSampleRates`): above, the built-in mic is at 48000 and accepts four rates.
- What `Microphone.Start(name, true, len, 48000)` does when the device cannot do 48000 (resample, change the device rate, or hand back a clip at another rate) is **NOT FOUND**. MicCapture asks for 48000 clamped into the `GetDeviceCaps` range, then reports `clip.frequency` as `SampleRate`, so the HUD meter shows the truth.
- Changing a device's rate from inside the app can also change what other apps hear; NOT FOUND whether Unity does that.

## 3. Microphone permission (macOS TCC)

Apple, "Requesting authorization to capture and save media", `https://developer.apple.com/documentation/avfoundation/requesting-authorization-to-capture-and-save-media` (fetched 2026-11-14; text page written for iOS and macOS 10.14+):
- "In iOS and macOS 10.14 and later, the user must explicitly grant permission for each app to access the camera and microphone. Before your app can use a capture device for the first time, the system presents an alert with an app-specific message that you specify, to ask the user to grant your app access to the capture device."
- "The system remembers the user's response to each access alert, so subsequent uses of the corresponding capture device don't cause the alert to appear again. The user can change permission settings for your app in Settings > Privacy in iOS and in System Preferences > Security & Privacy in macOS." (On current macOS the pane is System Settings › Privacy & Security › Microphone.)
- "If your app uses device microphones, include the NSMicrophoneUsageDescription key in your app's `Info.plist` file."
- "In macOS, you also need to enable the following entitlements in Signing & Capabilities for your app targets: … If your app uses device microphones, enable the `com.apple.security.device.audio-input`."
- "Important: Your app needs to contain the appropriate key in its `Info.plist` file, and the appropriate entitlement enabled in macOS, before it requests authorization or attempts to use a capture device. Otherwise, the system terminates your app."

Apple, `NSMicrophoneUsageDescription` (`https://developer.apple.com/documentation/bundleresources/information-property-list/nsmicrophoneusagedescription`, macOS 10.14+): "A message that tells people why the app is requesting access to the device's microphone. Important: This key is required if your app uses APIs that access the device's microphone."
Apple, Audio Input Entitlement (`https://developer.apple.com/documentation/bundleresources/entitlements/com.apple.security.device.audio-input`, macOS 10.7+): "A Boolean value that indicates whether the app may record audio using the built-in microphone and access audio input using Core Audio. … To add this entitlement to your app, first enable the Hardened Runtime capability in Xcode, and then under Resource Access, select Audio Input."
Unity's own Vivox page (`https://docs.unity.com/en-us/vivox-core/developer-guide/macos/macos-requirements`): the entitlement is for apps "shipping with Hardened Runtime enabled" outside the Mac App Store. Our local build is not hardened (see below), so by that page only the Info.plist key is needed; this combination was **not tested**.

How this lands in Unity 2021.3.3f1 (details and sources in `docs/apis/unity/microphone.md`):
- **Built app**: set Player Settings › PC, Mac & Linux Standalone › Other Settings › Configuration › Mac Configuration › **Microphone Usage Description** (a project-settings change: a human or the bootstrap worker does it, I may not touch `ProjectSettings`). Unity's support page: "If you request permissions and this value is not set, your application will terminate with an error." After a build check `plutil -p build/UntitledGuitarApp.app/Contents/Info.plist | grep -i microphone`.
- **Editor**: `Unity.app/Contents/Info.plist` (bundle id `com.unity3d.UnityEditor5.x`) has **no** `NSMicrophoneUsageDescription`; it is an x86_64 binary under Rosetta; it is not hardened (`codesign -dv`: `flags=0x0(none)`). `/Applications/Unity Hub.app` (`com.unity3d.unityhub`) **does** have the key ("This app needs access to the microphone"). Which app macOS credits (Editor, Hub, or the Terminal that started the Editor) and whether the prompt appears at all: **NOT FOUND**. Unofficial reports (Unity forum thread "Microphone Audio is Silent", 2019–2023) say the Hub, not the Editor, has to be granted microphone access, and Unity issue 1227607 reports Editor crashes on first audio-capture access when no grant exists. Do not edit the TCC database by hand (the thread's workaround needs SIP off).
- **Reset a grant** to see the prompt again: `tccutil reset Microphone <bundle id>` (the tool exists here: `Usage: tccutil reset SERVICE [BUNDLE_ID]`; not run).
- **What a denied app receives** (silence, an error, no frames): **NOT FOUND** in Apple's pages. MicCapture therefore treats "no devices", `Start` null/throwing, no frames after the start grace, and "exactly 0.0 for 3 s" as the possible symptoms, warns once, and retries when the app regains focus.

## 4. Voice Isolation / Mic Mode caveat

- Apple, `AVCaptureDevice.MicrophoneMode` (`https://developer.apple.com/documentation/avfoundation/avcapturedevice/microphonemode`, macOS 12+): "standard: A mode that processes microphone audio with standard voice DSP." · "wideSpectrum: A mode that minimizes microphone audio processing to capture all sounds in the room." · "voiceIsolation: A mode that processes microphone audio to isolate the voice and attenuate other signals."
- `preferredMicrophoneMode`: "The microphone mode that the user selects in Control Center." (the user chooses it, per app). `activeMicrophoneMode`: "The device's active microphone mode. The value may differ from the value of the preferredMicrophoneMode property if the app's active audio route doesn't support the mode."
- Apple Support, "Use Mic Modes on your Mac" (`https://support.apple.com/guide/mac-help/mchle82b42f0/mac`): "Go to an app that supports Mic Modes, such as FaceTime. Click … in the menu bar, then click Mic Mode." and "To revert to the standard mic mode, select Standard." / "To filter out background sounds, select Voice Isolation." / "To include the sounds around you, select Wide Spectrum."
- Why it matters: Voice Isolation suppresses everything that is not speech, which would eat or pump guitar sustain and break the "ring-out" quality check (`docs/MASTER_PLAN.md` §19). The repo claim "macOS delivers raw input unless an app turns on voice processing" is **not confirmed** by any source I found. No Apple page says whether a plain CoreAudio input stream (what Unity's Mac player links: `CoreAudio`, `AudioUnit`, `AVFoundation`, `CoreMedia`) is eligible for a mic mode, and `strings` on the arm64 `UnityPlayer.dylib` shows no `VoiceProcessing`/`AUVoiceIO` names (weak evidence that Unity uses plain input). Whether the glasses' USB mic applies its own DSP is also NOT FOUND (`docs/MASTER_PLAN.md` §19 says "unverified").
- What to check in M-S3 (cheap): while MicCapture is running, open Control Center. If a "Mic Mode" entry shows for the app, set **Standard** (or **Wide Spectrum**), never Voice Isolation. If there is no entry, record that fact. Then compare the level decay of a ringing open string on both mics (natural decay vs sudden cut = gating/AGC).

## 5. Script: list input devices (no permission needed, nothing is recorded)

Save as `audioin.swift`, run `swift audioin.swift` (about 3 s). Plug the glasses in first to read their name, channels and rates.
```swift
import CoreAudio
import Foundation
func addr(_ s: AudioObjectPropertySelector, _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) -> AudioObjectPropertyAddress {
    AudioObjectPropertyAddress(mSelector: s, mScope: scope, mElement: kAudioObjectPropertyElementMain) }
func str(_ id: AudioObjectID, _ s: AudioObjectPropertySelector) -> String {
    var a = addr(s); var size = UInt32(MemoryLayout<CFString?>.size); var v: Unmanaged<CFString>?
    let st = withUnsafeMutablePointer(to: &v) { AudioObjectGetPropertyData(id, &a, 0, nil, &size, $0) }
    return st == noErr ? ((v?.takeRetainedValue() as String?) ?? "?") : "?" }
func u32(_ id: AudioObjectID, _ s: AudioObjectPropertySelector) -> UInt32 {
    var a = addr(s); var size = UInt32(4); var v: UInt32 = 0
    return AudioObjectGetPropertyData(id, &a, 0, nil, &size, &v) == noErr ? v : 0 }
func inChannels(_ id: AudioObjectID) -> Int {
    var a = addr(kAudioDevicePropertyStreamConfiguration, kAudioDevicePropertyScopeInput); var size: UInt32 = 0
    guard AudioObjectGetPropertyDataSize(id, &a, 0, nil, &size) == noErr, size > 0 else { return 0 }
    let raw = UnsafeMutableRawPointer.allocate(byteCount: Int(size), alignment: 16); defer { raw.deallocate() }
    guard AudioObjectGetPropertyData(id, &a, 0, nil, &size, raw) == noErr else { return 0 }
    return UnsafeMutableAudioBufferListPointer(raw.assumingMemoryBound(to: AudioBufferList.self)).reduce(0) { $0 + Int($1.mNumberChannels) } }
func rates(_ id: AudioObjectID) -> String {
    var a = addr(kAudioDevicePropertyAvailableNominalSampleRates); var size: UInt32 = 0
    guard AudioObjectGetPropertyDataSize(id, &a, 0, nil, &size) == noErr, size > 0 else { return "?" }
    var r = [AudioValueRange](repeating: AudioValueRange(), count: Int(size) / MemoryLayout<AudioValueRange>.size)
    guard AudioObjectGetPropertyData(id, &a, 0, nil, &size, &r) == noErr else { return "?" }
    return r.map { $0.mMinimum == $0.mMaximum ? "\(Int($0.mMinimum))" : "\(Int($0.mMinimum))-\(Int($0.mMaximum))" }.joined(separator: ",") }
func four(_ v: UInt32) -> String { String(bytes: [UInt8(v >> 24 & 255), UInt8(v >> 16 & 255), UInt8(v >> 8 & 255), UInt8(v & 255)], encoding: .ascii) ?? "\(v)" }
let sys = AudioObjectID(kAudioObjectSystemObject)
var a = addr(kAudioHardwarePropertyDevices); var size: UInt32 = 0
AudioObjectGetPropertyDataSize(sys, &a, 0, nil, &size)
var ids = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
AudioObjectGetPropertyData(sys, &a, 0, nil, &size, &ids)
let def = u32(sys, kAudioHardwarePropertyDefaultInputDevice)
for id in ids where inChannels(id) > 0 {
    var nom: Float64 = 0; var s8 = UInt32(8); var an = addr(kAudioDevicePropertyNominalSampleRate)
    AudioObjectGetPropertyData(id, &an, 0, nil, &s8, &nom)
    print("name=\"\(str(id, kAudioObjectPropertyName))\" uid=\"\(str(id, kAudioDevicePropertyDeviceUID))\" transport='\(four(u32(id, kAudioDevicePropertyTransportType)))' inChannels=\(inChannels(id)) nominal=\(Int(nom)) rates=[\(rates(id))]\(id == def ? " DEFAULT_INPUT" : "")")
}
```

## 6. NOT FOUND (summary)

The glasses' microphone name, channel count, rates and any internal DSP/gating · Unity's exact `Microphone.devices` strings and order · what happens when the Editor 2021.3.3f1 first opens the mic (prompt, silence or crash) and which app macOS credits · what a denied app receives · whether Unity's capture path is subject to Voice Isolation / Wide Spectrum · whether Unity resamples or retunes the device when 48000 is unsupported.
