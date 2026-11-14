# art/test: spike M-S1 test image

`sbs-test-3840x1080.png` is a side-by-side stereo test frame for the XREAL glasses: the left half (1920x1080) is red with a large
white "L", the right half is blue with a large white "R", and both halves carry the same thin white frame, ticks and a 2x3 grid of
small crosses. `make_sbs_test.py` (Python 3, standard library only) writes it and reads it back: `python3 make_sbs_test.py`
(`--check` re-verifies the PNG without rewriting it).

## Show it full-screen on the glasses (macOS)

1. Plug the glasses into the MacBook. System Settings > Displays: they must be an **extended** display, not mirrored.
2. Hold the glasses' brightness-up button about 3 s until it beeps (side-by-side 3D), then pick **3840 x 1080** (90 Hz if listed) for that display (`docs/HOW_IT_WORKS.md` section 4).
3. Double-click `sbs-test-3840x1080.png` (it opens in Preview) and drag the window onto the glasses' display (the 32:9 one).
4. In Preview: View > Zoom to Fit (Cmd-9), then View > Enter Full Screen (Ctrl-Cmd-F). The picture must fill the display edge to edge, with no toolbar.
5. Move the pointer back to the Mac screen so it does not show on the glasses, then put the glasses on.
6. Cover one eye with your palm and look with the other, then swap eyes, then open both.

## What "pass" looks like (spike M-S1: each eye sees only its half)

- Left eye alone: only red with the white "L", no blue anywhere. Right eye alone: only blue with the white "R", no red anywhere.
- The thin frame is visible on all four sides and the ticks are evenly spaced: nothing is cropped or scaled.
- Both eyes open: the two frames and the six crosses fuse into one frame and one grid of crosses (the red L and the blue R compete with each other, which is normal).
- System Settings > Displays lists 3840 x 1080 at 90 Hz for the glasses (the target in `docs/MASTER_PLAN.md` section 25, if the Mac offers it; otherwise write down the rate it does offer).

If the left eye sees the blue "R", the glasses show the halves to the opposite eyes: set `render.swapEyes` to true in the flags (risk R5) and say so in `docs/STATUS.md`.
If either eye sees both colours, a squeezed copy of the whole picture, or a doubled image, the display is not in 3840 x 1080 side-by-side mode (it is mirrored, in 2D mode, or at another resolution): fix steps 1 and 2 first.

The same check through the real pipeline: `StereoRig` has a `debugEyeColors` switch (left eye camera clears to red, right eye camera to blue) that you can tick in the Inspector in Play Mode.
