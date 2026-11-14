# art/blender — ghost-hand poses, from Blender to Unity

Run both scripts from the repo root with Blender 5.2. `--python-exit-code 1` must come before `--python`, or a crash inside a script still exits 0.

1. Build the 1:1 neck (add your measured guitar: `--scale-length 648 --nut-width 43 --nut-spread 35 --saddle-spread 53 --nut-to-soundhole MM`):
   `/Applications/Blender.app/Contents/MacOS/Blender --factory-startup -b --python-exit-code 1 --python art/blender/build_neck_mockup.py`
   It writes `art/blender/neck_mockup.blend` (generated, not committed). Re-running replaces it.
2. Open it in Blender and **File > Save As** a working file (for example `art/blender/hand/hand_poses.blend`). Never pose in `neck_mockup.blend`.
3. Add the hand armature. Its 16 bones must be named `wrist` and `<thumb|index|middle|ring|pinky>_<proximal|intermediate|distal>`, each finger chain a descendant of `wrist`. The tail of each `*_distal` bone is the fingertip.
4. Pose a chord: show only `Targets_<chord>` and put the fingertips on `tip_<chord>_f<finger>` (finger 1 = index … 4 = pinky). Finger bones may only rotate (no location, scale or stretch). Place the hand by moving the armature object or the wrist bone; both export the same file.
5. Key each pose (select all bones, `I`) and add a timeline marker named like it: `G`, `Em`, `rest`, or `fretting-G` / `strumming-down-start` when both hands share a file. Unkeyed changes are lost when the timeline moves.
6. Save, then export (one file per marker, `<hand>-<name>.pose.json` in `unity/Assets/StreamingAssets/poses/`):
   `/Applications/Blender.app/Contents/MacOS/Blender --factory-startup -b art/blender/hand/hand_poses.blend --python-exit-code 1 --python art/blender/export_poses.py -- --hand fretting --source markers --strict`
   `--strict` writes nothing unless every fingertip is within 3 mm of its dot. `--source current --name G` exports the pose on screen instead; `--armature NAME` picks the armature when there are two; `--dry-run` prints without writing.
7. In Unity, `GhostPoseLibrary.Reload()` picks the files up. The EditMode test "every pose JSON loads" checks them.

Axes: Blender +X along the neck toward the bridge, +Y toward string 1, +Z up out of the fretboard; Unity (x, y, z) = Blender (X, Z, Y). The file format is specified in `unity/Assets/StreamingAssets/poses/README.md`. More options are in each script's header.
