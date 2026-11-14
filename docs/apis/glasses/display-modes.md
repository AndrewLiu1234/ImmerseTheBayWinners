# XREAL Air 2 Ultra: display modes (side-by-side 3D, resolutions, refresh rates, field of view)

Used by `Runtime/Render/StereoRig.cs`, `StereoMath.cs` and `art/test/`. Read on 2026-11-14. Anything that no source states is marked **NOT FOUND**. "Search summary" means the page itself could not be opened from here (see the notes) and only a search engine's summary of it was seen: treat those lines as unconfirmed.

## 1. Facts from XREAL itself (Air 2 Ultra)

Source A: XREAL developer site for the Air 2 Ultra, https://developer.xreal.com/ , fetched 2026-11-14, section "Specs > Display" (exact lines):
> 52 Degree FOV
> Micro OLED panel from SONY
> Resolution: 1920*1080 pixels per eye
> Refresh rate: Up to 120Hz (2D mode 120 Hz, 3D mode 90Hz)
> Brightness: Up to 500 nits (2D mode 30-500 nit, 3D mode 20-250 nit)
> Unit-based color calibration
> **3D refers to the 3840*1080 stereoscopic mode, while 2D refers to the 1920*1080 monoscopic mode.**

Same page: "Connector: USB-C DisplayPort Alt mode"; Compatibility, "Screen Mirroring": "USB-C Video Output, HDMI Output devices ... iPhone, Android, MacBook, Steam Deck, ROG Ally, Windows PC"; "Buttons: Multi-functional key. Single click to switch between dimming levels. Double-click to turn on/off the display. Up/Down button"; also "Video recording and photography are not supported", "Weight 83g". The page says **nothing** about how to switch 3D mode on, and nothing about a virtual image distance.

Source B: XREAL SDK docs, "XREAL Glasses", https://docs.xreal.com/XREALDevices/XREAL%20Glasses (Version 3.1.0), comparison table, Air 2 Ultra column: Resolution Per Eye `1920 * 1080`; FOV `52 Degrees`; Frame Rate - MR Space `60Hz; 72Hz; 90Hz; 120Hz`; IMU `Yes`; Grayscale Camera `Yes`; Connection `USB-C Cable`; text: "the Air 2 Ultra adds spatial computing capabilities with integrated 6DoF tracking through dual grayscale cameras. It features a wider 52-degree FOV". (That table is for the SDK's Android "MR space"; the refresh list is not the Mac list.) The SDK's ControlGlasses page (https://docs.xreal.com/Tools/ControlGlasses) calls ControlGlasses "the official tool for switching XREAL glasses into 3D mode" (an Android app; not usable here).

## 2. Per mode (what we can say)

| Mode | What the glasses show | Resolution the host sends | Refresh | Brightness | Source |
|---|---|---|---|---|---|
| 2D (monoscopic) | the same picture to both eyes | 1920x1080 | up to 120 Hz | 30-500 nits | A |
| 3D (stereoscopic, "side-by-side") | left half of the frame to the left eye, right half to the right eye | **3840x1080** = 2 x 1920x1080 | up to 90 Hz | 20-250 nits | A (resolution, rate, nits); the half-to-eye mapping is the project's assumption (docs/HOW_IT_WORKS.md), tested by spike M-S1 |
| Per-eye resolution | | 1920x1080 | | | A, B |
| 60 Hz and 72 Hz modes on a Mac | NOT FOUND (only B lists 60/72/90/120 Hz for the SDK's MR space; ar-drivers-rs below has 60/72/90/120 Hz mode bytes for the Air family) | | | | |

Half-SBS (a 1920x1080 source squeezed into 960x540 per eye) exists in ar-drivers-rs (section 4) and on the XREAL One tutorial (section 3); we do **not** use it: we send full 3840x1080.

## 3. How 3D mode is switched on

- **Our assumption, written in docs/HOW_IT_WORKS.md section 4 and MASTER_PLAN section 25:** hold the glasses' brightness-up button for about 3 s until it beeps. Sources for it, none official for the Air 2 Ultra: (a) 3DHD Club's guide for the Air 2, https://3dhdclub.tawk.help/article/how-to-watch-3d-movies-on-xreal-air-2 , fetched 2026-11-14: "To switch your XREAL Air glasses to 3D mode, press and hold the brightness '+' button." and "The resolution of the glasses is 3840x1080 pixels" (no duration given); (b) an XREAL community thread "3D mode, the image is reversed", https://community.xreal.com/t/3d-mode-the-image-is-reversed/4907 (cited as X15 in MASTER_PLAN): **search summary only**, it reports that holding the brightness key for three seconds enabled 3D on an Air 2 used with an iPad Pro, with the left and right images swapped; (c) a post by the XREAL Team on r/Xreal, "Explanation of XREAL Air button functions" (Aug 2023), https://www.reddit.com/r/Xreal/comments/15tglf7/explanation_of_xreal_air_button_functions/ : **search summary only**, it appears to say the 2D/3D toggle on the original Air is a hold of about 6 s with two beeps. Reddit returned HTTP 403 and community.xreal.com does not resolve (DNS) from here, and the Wayback Machine refused the requests, so none of (b) and (c) could be read.
- **NOT FOUND:** an official statement of the button (and hold time) that switches the **Air 2 Ultra** to 3D mode, and of what the Ultra's "Up/Down button" does when held (the official page, Source A, lists only dimming and display on/off). So the human should try a 3 s hold of brightness-up first, then about 6 s, and write down what worked in docs/STATUS.md ("Spike results").
- Official, for a different product: on the XREAL One series, "Enable 3D Mode: Press and hold the X button. The glasses enter Full SBS mode by default.", "3D Mode is not retained after the glasses are powered off" (https://tutorials.xreal.com/docs/glasses/xbxa01/osd/3d-mode). Not our glasses, but it shows 3D mode is a per-power-cycle state: repeat the button press after every reconnect.
- Software switch (Air family, **not verified on the Ultra**): ar-drivers-rs (MIT), https://github.com/badicsalex/ar-drivers-rs at commit `56bfa489ab697706ea95b36c2e43c5ca15d6a263` (2026-05-14). `src/nreal_air.rs`: `NREAL_VID = 0x3318`, `AIR_2_ULTRA_PID = 0x0426`; `set_display_mode` sends MCU command `0x08` with one data byte: `SameOnBoth => 1, HalfSBS => 8, Stereo => 3, HighRefreshRate => 11, HighRefreshRateSBS => 9`; `get_display_mode` (MCU command `0x7`) maps the reply: `1` "Mirror 60Hz", `3` "SBS 60Hz", `4` "SBS 72Hz", `5` "Mirror 72Hz", `8` "Horizontally stretched SBS, 60Hz", `9` "SBS 90Hz", `10` "Mirror 90Hz", `11` "Mirror 120Hz". `src/lib.rs`: `Stereo`: "Set display to 3840*1080 or 3840x1200, where the left half is the left eye, the right half is the right eye"; `HalfSBS`: "presents itself as 1920*1080 resolution, but actually scales down everything to 960x540, then upscales to 3840x1080"; `examples/set_to_3d.rs` (`--keep-running`: "This is needed for some AR glasses to stay on in SBS mode."). The HID packet framing belongs to `docs/apis/glasses/imu-protocol.md` (Person 1's M-S2 note); nothing here is copied from it and no byte layout in this file is used by our code.

## 4. Field of view and optics

| Fact | Value | Source |
|---|---|---|
| Field of view | **52 degrees**, a manufacturer figure; the Air, Air 2, Air 2 Pro are 46 | A, B; GamesBeat "a field-of-view (FOV) of 52°" (https://gamesbeat.com/xreal-unveils-xreal-air-2-ultra-ar-glasses-for-developers/ , published Jan 7, 2024, updated June 18, 2025) |
| Is 52 the diagonal? | Not stated by A or B. The only direct statement found is a retailer listing's "roughly 52° measured diagonally" (search summary, https://channelxr.com/collections/all/products/xreal-air-2-ultra-glasses). The project treats it as the diagonal, which also reproduces XREAL's "154-inch at 13 ft" below (46.2 x 27.0 deg = 52.1 deg diagonal) | derived in StereoMath.cs |
| Measured by press | "The field of view of the Air 2 Ultra optics is slightly larger than the other Air glasses, at 45°×25°, compared to 40°×22°." (UploadVR, 7 January 2024, https://www.uploadvr.com/xreal-air-2-ultra-ar-glasses/ , fetched; axes not labelled). In a flat-image model 45 x 25 is a 50.3 degree diagonal | UploadVR |
| What 52 deg diagonal at 16:9 is | 46.06 x 26.90 degrees (`StereoMath`) | computed |
| Pixel density | **42 pixels per degree** (manufacturer figure, GamesBeat "42 pixels per degree (PPD)"; VR & AR Wiki https://vrarwiki.com/wiki/Xreal_Air_2_Ultra : "42 PPD (manufacturer figure)"). Our arithmetic: 1920 px / 46.06 deg = 41.7 px/deg horizontally, 1080 / 26.90 = 40.2 vertically | GamesBeat, vrarwiki |
| Optics | "Birdbath optical engine", Micro OLED from Sony | vrarwiki, A |
| Virtual image distance | **NOT FOUND** as an optical focal distance in any source. The closest statements are marketing equivalents for the 2D "virtual screen casting": "mimics a 154-inch display from 13 feet away" (GamesBeat) and "a 2D virtual screen equivalent to 154 inches viewed from 13 feet (about 4 m)" (vrarwiki). A community reply seen only as a search summary says the original Air defaults to a 4 m virtual distance in 2D and that changing it in software does not change the optical focus. | GamesBeat, vrarwiki |
| IPD of the glasses | **NOT FOUND** (no source states a fixed optical IPD; `render.ipdMm` defaults to 63) | |

What follows for the code: `render.fovDiagonalDeg = 52`, `render.ipdMm = 63`, and `render.convergenceM` stays **0** (parallel axes) until a person judges depth at the neck during spike M-S1/M-S4. If near objects look doubled or too close, try `convergenceM` = 1 to 4 (4 m is the only distance any source mentions), then record the best value. Everything about convergence is a hypothesis: the glasses' real optical axes and virtual image distance are NOT FOUND.

## 5. Other sources read (agree with the above, add nothing new)

VR & AR Wiki, https://vrarwiki.com/wiki/Xreal_Air_2_Ultra (CC BY-SA 4.0): "1920 x 1080 per eye (Full HD)", "Up to 120 Hz (2D mode); up to 90 Hz (3D mode)", "Brightness is adjustable from 30 to 500 nits in 2D mode and from 20 to 250 nits in 3D mode". UploadVR: "The OLED microdisplays in Air 2 Ultra are the same as in Air 2 Pro, 1080p with 120Hz refresh rate and 500 nits of brightness."

## 6. Not verified (needs the glasses, L5)

Which eye gets which half (spike M-S1 and `render.swapEyes`), that macOS offers 3840x1080 at 90 Hz for the glasses, and that 3D mode survives macOS re-arranging displays (one community report, search summary only, says Windows reshuffled displays and dropped 3D when the button was used; keep the glasses as an extended display and press the button once the cable is in).
