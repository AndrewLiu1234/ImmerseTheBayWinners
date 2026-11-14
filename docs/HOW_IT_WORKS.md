# How the glasses-only Mac setup works (one USB-C cable)

*For humans and agents. Read this before touching `Runtime/Render`, `Runtime/Head`, `Runtime/Audio` or `tools/imu-bridge`.*

The XREAL Air 2 Ultra has no battery, no processor and no Wi-Fi. On a Mac it is three things at once: **a second monitor**,
**a motion sensor** and **a USB microphone/speaker**. Everything else (rendering, tracking, listening, judging) runs on the Mac.

## 1. The one-cable picture

```
 MacBook Pro (Apple silicon)                       USB-C cable                    XREAL Air 2 Ultra
 ┌──────────────────────────────┐                                              ┌──────────────────────┐
 │ GPU ── display 2 ────────────┼── video: DisplayPort Alt Mode ───────────────▶│ two 1080p OLEDs      │
 │                              │   (3840×1080 SBS: left half → left eye,      │ (one per eye)        │
 │                              │    right half → right eye)                   │                      │
 │ tools/imu-bridge (Python) ◀──┼── USB data: HID (IMU + buttons) ◀────────────│ gyro + accelerometer │
 │ Unity Microphone ◀───────────┼── USB data: audio in (2 mics) ◀──────────────│ mics                 │
 │ macOS audio out ─────────────┼── USB data: audio out ───────────────────────▶│ speakers             │
 │ USB-C port power ────────────┼── power ─────────────────────────────────────▶│ (no battery)         │
 └──────────────────────────────┘                                              └──────────────────────┘
```

| Channel | Direction | What it carries | Who reads/writes it |
|---|---|---|---|
| DisplayPort Alt Mode | Mac → glasses | the picture, as an external display ("display 2") | macOS + Unity's `StereoRig` |
| USB HID | glasses → Mac | raw gyro/accelerometer packets and button presses (vendor protocol) | `tools/imu-bridge/bridge.py` (hidapi) |
| USB audio | both ways | 2 mics in, 2 speakers out | Unity `Microphone` / macOS output |
| Power | Mac → glasses | everything the glasses need | — |

## 2. What happens every frame

1. **Head motion.** `bridge.py` reads the IMU over USB HID, fuses gyro + accelerometer into an orientation (a quaternion),
   and sends `{"t": seconds, "q": [x, y, z, w]}` to `127.0.0.1:47800` many times per second.
2. **Head pose.** Unity's `Runtime/Head/ImuHeadPose` keeps the newest packet, predicts a few milliseconds ahead, and rotates
   the virtual head. If packets stop for 250 ms it switches to **headLocked** (content follows your head).
3. **Stereo render.** `Runtime/Render/StereoRig` has two eye cameras an IPD apart. Each renders the scene with a frustum that
   matches the glasses' optics (52° diagonal ≈ 46°×27°). The left eye goes into the left half of a 3840×1080 frame and the
   right eye into the right half.
4. **Display.** macOS sends that frame to the glasses (display 2). In 3D mode the glasses show the left half to your left eye
   and the right half to your right eye, so your brain fuses one 3D scene.
5. **Listening (in parallel).** `Runtime/Audio/MicCapture` → ring buffer → `AudioWorker` thread → Core DSP (onsets, YIN pitch,
   chord verification) → verdicts → back to the main thread → HUD, ghost hands, toasts.
6. **Spectator.** Display 1 (the MacBook screen) shows a mono view of the same virtual layer for judges and the video.

The glasses are see-through and **additive**: they can only add light. Black pixels are invisible, so we draw bright, glowing
things (the blue ghost hands) on top of the real guitar.

## 3. How the overlay lands on your real guitar without cameras

The glasses can tell **which way your head points** (3 degrees of freedom), not **where your head is** or where the guitar is.
So we measure the guitar once with your eyes:

1. Sit, hold the guitar, and look at 4 points for about a second each: nut at the low-E string, nut at the high-E string,
   fret 5, fret 12. A reticle in the middle of the view shows where you are looking.
2. `Core/Geometry/NeckSolver` knows the real fret spacing (from the measured scale length) and fits the neck to those 4 gaze
   rays. It reports a residual in millimetres.
3. From then on the IMU keeps the virtual neck (and the ghost hands) fixed in the room while you turn your head.
4. Leaning or moving the guitar breaks the fit. **Re-anchor** with one glance at the nut (about 2 s).

Honest accuracy: about 1–2 cm. That is enough to show the *shape and place* of the hand; the exact string and fret come from
the fingertip dots, the chord diagram and the audio check. Spike M-S5 and gate G1 (Sat 11:00) measure it.

## 4. Setup on the Mac (each session)

1. Plug the glasses into a USB-C/Thunderbolt port on the MacBook.
2. System Settings › Displays: the glasses must be an **extended** display, not mirrored.
3. Hold the glasses' **brightness-up** button for about 3 s until it beeps: the glasses switch to 3D side-by-side.
   Set the glasses' display to **3840×1080** in Displays.
4. System Settings › Sound: note the glasses' mic and speaker names (spike M-S3 decides which mic we use).
5. `./scripts/imu-bridge.sh --list` once to see the glasses' HID interfaces; then `./scripts/imu-bridge.sh`.
6. Unity: open the project, `GlassesGameView` → Play (or run the macOS build fullscreen on display 2).
7. Optional: `./scripts/logs.sh` in a second terminal.

## 5. What this setup cannot do

- No camera access, no hand tracking, no 6DoF position, no image tracking. Those only exist in XREAL's SDK on Android.
- Tethered: the glasses need the Mac's cable for video and power. There is no wireless option without extra hardware.
- The IMU protocol on macOS comes from community drivers. It is documented for older Air models; the Ultra is our biggest
  unknown (spike M-S2). Fallback: headLocked mode with a floating neck.

## 6. Grill: could an Android emulator get us XREAL's developer kit?

**Decided Sat 00:23 (Andrew): keep the Mac.** No emulator and no Android build for the demo. The reasoning stays below for the record.

You have Android tooling on this Mac, so it's a fair question. Short answer: **the emulator can't drive the glasses**, so it
doesn't help the demo.

| Option | What you get | What it costs | Verdict |
|---|---|---|---|
| **A. Mac route (current)** | Our app on the glasses tonight; 3DoF head tracking; mics; 2–5 min test loop; spectator on the Mac | no hand tracking or 6DoF; IMU driver risk (M-S2) | **Recommended** |
| **B. Android emulator on the Mac** (Android Studio, Genymotion) | XREAL SDK code that compiles and runs in a phone window | The official emulator has no virtual USB hardware, and the glasses' video comes from the Mac's GPU, not from the emulated phone. The XREAL runtime never sees the glasses, so they stay dark. You would build features you can't see or test | Not useful |
| **C. Unity Editor + XREAL's XR Device Simulator** | SDK-style interactions simulated with mouse and keyboard | Same as B: nothing reaches the glasses; hands and 6DoF are simulated, not real | Only useful if D happens |
| **D. A real Beam Pro or recent Samsung flagship (borrowed)** | The real SDK on the glasses: 6DoF, hand tracking, image tracking | APK build loop, new risk mid-event, a different spectator setup. The Core audio engine ports unchanged | Worth it only if a device is in hand by Sat ~10:00 **and** the Mac alignment (G1) fails |

**Q-EMU-01 (one question, grill-me style):** Can you get a Beam Pro or a recent Samsung Galaxy flagship (XREAL's SDK docs say
S24 or newer) into your hands by Sat 10:00, from a teammate, a sponsor table or a friend?
→ **Recommended answer:** if no, we're done: Mac route. If yes, keep the Mac route for the main demo and consider an Android
"hand check" stretch only after `good-1600`.

## Sources

- [XREAL SDK — Getting Started (Android host required)](https://docs.xreal.com/Getting%20Started%20with%20XREAL%20SDK)
- [XREAL SDK — XR Device Simulator](https://docs.xreal.com/Tools/Emulator)
- [Android Emulator — limitations (no virtual USB hardware)](https://developer.android.com/studio/run/advanced-emulator-usage)
- [XREAL community — SBS 3840×1080 via a 2–3 s brightness press](https://community.xreal.com/t/viewing-3d-vr-videos/4578)
- [Monado `xreal_air` driver — HID interfaces and the Air 2 Ultra product id](https://monado.pages.freedesktop.org/monado/xreal__air__interface_8h_source.html)
- [bevy-xreal-ar-demo — IMU on macOS via ar-drivers-rs (Air, Air 2, Air 2 Pro)](https://github.com/SimDoes/bevy-xreal-ar-demo)
- [VRARWiki — Nreal/XREAL Air: USB-C DisplayPort Alt Mode, power from host, single cable](https://www.vrarwiki.com/wiki/Nreal_Air)
