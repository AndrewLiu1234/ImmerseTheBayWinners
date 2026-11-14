# art/blender/hand — the ghost hands, from Blender to Unity

Two stylized, procedurally built hands (no downloaded asset), each a 16-bone armature with one skinned mesh of
5,376 triangles: `GhostHand_Fretting` (the player's LEFT hand) and `GhostHand_Strumming` (the RIGHT hand). Everything
here is rebuilt by script, headless, with Blender 5.2 (tested with 5.2.2 LTS).

| File | What it is |
|---|---|
| `build_hand_rig.py` | builds one hand: mesh, 16 bones, skin weights, vertex masks (`--preview` renders the bare hands) |
| `pose_hands.py` | puts both hands on the neck mock-up and keys the 12 poses as timeline markers; writes `ghost_hands.blend` |
| `ghost_hands.blend` | the posed hands on the mock-up (committed; the input of the three steps below) |
| `export_fbx.py` | writes the two FBX files for Unity and checks them (simulated Unity import + Blender re-import) |
| `render_previews.py` | one PNG per pose in the ghost look, plus `renders/contact-sheet.png` |
| `ghost_look.py` | the additive blue preview material and camera helpers (shared) |
| `renders/` | `rig-*.png` (bare hands), `<hand>-<pose>.png`, `contact-sheet.png` |

Outputs elsewhere: `unity/Assets/StreamingAssets/poses/<hand>-<pose>.pose.json` (12 files, format in that folder's
README) and `unity/Assets/UntitledGuitarApp/Art/Resources/GhostHand/ghost_hand_{fretting,strumming}.fbx`.

## Rebuild everything after measuring the real guitar

Run from the repo root, in this order (step 2 is only a look at the bare rig; step 3 rebuilds the rig itself).
`--python-exit-code 1` must come before `--python`, or a script error still exits 0. The current files were built
with the defaults: scale length 648, nut width 43, string spread 35 / 53, nut-to-soundhole 440, soundhole 100,
neck depth 22 (ASSUMED), hand length 187 (all mm).

```bash
B=/Applications/Blender.app/Contents/MacOS/Blender

# 1. neck mock-up with the measured numbers -> art/blender/neck_mockup.blend (generated, not committed)
$B --factory-startup -b --python-exit-code 1 --python art/blender/build_neck_mockup.py -- \
   --scale-length 648 --nut-width 43 --nut-spread 35 --saddle-spread 53 --nut-to-soundhole 440

# 2. (optional) the bare rig in the ghost look -> renders/rig-bind.png, rig-curl.png, rig-curl-palm.png
$B --factory-startup -b --python-exit-code 1 --python art/blender/hand/build_hand_rig.py -- --preview art/blender/hand/renders

# 3. both hands posed on the mock-up -> art/blender/hand/ghost_hands.blend (exit 1 if a fingertip is > 1 mm off its dot)
$B --factory-startup -b --python-exit-code 1 --python art/blender/hand/pose_hands.py -- \
   --neck art/blender/neck_mockup.blend --neck-depth 22

# 4. the 12 pose files, strict (writes nothing unless every fretting fingertip is within 3 mm of its dot); one per hand
$B --factory-startup -b art/blender/hand/ghost_hands.blend --python-exit-code 1 --python art/blender/export_poses.py -- \
   --hand fretting --armature GhostHand_Fretting --source markers --strict
$B --factory-startup -b art/blender/hand/ghost_hands.blend --python-exit-code 1 --python art/blender/export_poses.py -- \
   --hand strumming --armature GhostHand_Strumming --source markers --strict

# 5. the two FBX files + both checks (add "-- --check-only" to check the committed files without exporting)
$B --factory-startup -b --python-exit-code 1 --python art/blender/hand/export_fbx.py

# 6. the renders and the contact sheet
$B --factory-startup -b art/blender/hand/ghost_hands.blend --python-exit-code 1 --python art/blender/hand/render_previews.py
```

Options worth knowing: `pose_hands.py --hand-length MM --only fretting-G,...`, `build_hand_rig.py --hand-length MM`,
`export_poses.py --dry-run`, `export_fbx.py -- --out-dir DIR --poses DIR`. Each script's header lists the rest.
After step 3 every later output changes: run 4, 5 and 6 and commit `ghost_hands.blend`, the 12 pose files, both FBX
files and `renders/` together. The FBX export is byte-reproducible: re-running step 5 on an unchanged `.blend` leaves
git clean.

## FBX settings (export_fbx.py)

Source of the option names and behaviour: Blender 5.2.2's own exporter, `io_scene_fbx` 5.15.0, at
`/Applications/Blender.app/Contents/Resources/5.2/scripts/addons_core/io_scene_fbx/` (`__init__.py` class `ExportFBX`;
`export_fbx_bin.py` `save_single`; `fbx_utils.py` `RIGHT_HAND_AXES`, `ObjectWrapper.fbx_object_matrix`). Unity facts
come from the manual and scripting reference that ship with Unity 2021.3.3f1 (`Unity.app/Contents/Documentation/en`,
published 2022-05-27) and from Unity's own FBX Exporter source (`github.com/Unity-Technologies/com.unity.formats.fbx`
at commit `b91b092`, `com.unity.formats.fbx/Editor/FbxExporter.cs` lines 3934-3937 and 4064-4068).

| Option | Value | Why |
|---|---|---|
| `axis_forward`, `axis_up` | `-Z`, `Y` (defaults) | writes the standard FBX Y-up file: `RIGHT_HAND_AXES[('Y', '-Z')]` = Up +Y, Front +Z, Coord +X. Unity is "left handed, Z forward, Y-up" (`ModelImporter.bakeAxisConversion`) and Unity's FBX Exporter describes the two systems as "Y up, Z forward, X to the right (left handed system with odd parity)" and "Y up, Z forward, X to the left (right handed system with odd parity)": the only conversion is x -> -x, no root rotation, so **Bake Axis Conversion** on or off gives the same result |
| `use_space_transform` | on (default) | the exporter folds the Z-up -> Y-up turn into the root nodes ("pre-multiply the global matrix", `fbx_object_matrix`). `export_fbx.py` cancels it on purpose (below) |
| `bake_space_transform` ("Apply Transform") | off (default) | tooltip: "experimental option, use at own risk, known to be broken with armatures/animations"; the code applies it to "only meshes and the like" |
| `apply_scale_options` / `apply_unit_scale` / `global_scale` | `FBX_SCALE_ALL` ("FBX All") / on / 1.0 | "Apply custom scaling and units scaling to FBX scale": `UnitScaleFactor` = 100 cm per unit and no scale on any node. Unity's `fileScale` is "the multiplier that converts the unit of measurement, of the imported FBX file, to meters" = 1.0 with **Convert Units** on (default). The default `FBX_SCALE_NONE` writes `UnitScaleFactor` 1 and puts x100 on the root nodes |
| `add_leaf_bones` | off (default is on) | on adds 5 nodes `<finger>_distal_end`; off keeps exactly the 16 pinned bones |
| `use_armature_deform_only` | on | "Only write deforming bones (and non-deforming ones when they have deforming children)"; all 16 bones deform |
| `primary_bone_axis`, `secondary_bone_axis` | `Y`, `X` (defaults) | no bone-axis correction is applied (only when not Y/X) |
| `armature_nodetype` | `NULL` (default) | the armature becomes a Null node = an empty GameObject |
| `object_types`, `use_selection` | armature + mesh, selected only | one hand per file, nothing from the mock-up |
| `bake_anim` | off | no animation stack: poses come from the pose files |
| `use_mesh_modifiers` | off | the mesh as stored is its bind shape (the Armature modifier is never applied anyway) |
| `mesh_smooth_type`, `use_triangles`, `use_tspace` | `FACE`, off, off | normals + face smoothing; Unity triangulates the 2,688 quads into 5,376 triangles; the shader needs no tangents |
| `colors_type` | `NONE` (default `SRGB`) | no vertex colours (see "Vertex data"). With `--vertex-colours shader` it is `LINEAR`: `SRGB` writes `color_srgb`, which would gamma-encode the mask values |

What `export_fbx.py` does beyond the options, in the export session only (the `.blend` is never saved): drops the
actions, resets every pose bone (bind pose) and mutes the IK constraints; unparents the armature from `Neck`;
re-expresses bones and mesh with the rotation G: (X, Y, Z) -> (-X, Z, Y) and gives the armature object the inverse of
the exporter's axis turn. After the exporter's turn and Unity's x mirror the data lands on the pose files' model
axes, (x, y, z)_unity = (X, Z, Y)_blender, with nothing left on the nodes above the bones. It also moves the
`FingerMask` colour attribute into two UV maps (below), keeps only the file name of the `.blend` in the FBX header (no
local path) and gives the FBX objects stable ids, so the same input gives the same bytes. Without the re-expression
(plain defaults) the hand would import turned 180 degrees about y with -90 degrees on the armature node.

Each run then checks both files and exits 1 on any mismatch: a reader of its own (no code shared with Blender's
importer) applies Unity's rules to the FBX data (axes, units, hierarchy, identity transforms, the 16 bones against the
source rig and against the pose files' `bind` block, one mesh in one connected piece with one material, the vertex
data), and each FBX is re-imported into an empty Blender scene (bone names and parents, bone heads, bind pose,
vertices, skin weights, triangle count, bounds). A mutation run (leaf bones, `FingerMask` leaking into the colour,
sRGB colours, no re-expression, a pose left on, All Local scaling, a second mesh piece, a second material) made a
check fail each time.

## The hierarchy Unity sees

Default import settings: Scale Factor 1, Convert Units on, Bake Axis Conversion off (no effect here), Preserve
Hierarchy off, Sort Hierarchy By Name on. Blender writes each node name as `<name>\x00\x01Model` (FBX's class
separator), which Unity shows as `<name>`: no prefix, no extra root node, no `_end` bones. The file has two top-level
nodes (Blender's exporter hangs a skinned mesh on the scene root, not under its armature), so Unity adds its own
prefab root named after the file:

```
ghost_hand_fretting                 prefab root (made by Unity)            identity transform
├── GhostHand_Fretting              empty GameObject (the armature node)  identity transform, scale 1
│   └── wrist                       localPosition (0, 0, 0) = the wrist joint      (a > b: b is the child of a)
│       ├── index_proximal > index_intermediate > index_distal
│       ├── middle_proximal > middle_intermediate > middle_distal
│       ├── pinky_proximal > pinky_intermediate > pinky_distal
│       ├── ring_proximal > ring_intermediate > ring_distal
│       └── thumb_proximal > thumb_intermediate > thumb_distal
└── GhostHand_Fretting_Mesh         SkinnedMeshRenderer, 16 bones, 1 material slot "GhostHand"   identity transform
                                    (one mesh in one connected piece: no overlapping parts to add up into seams)
```

`ghost_hand_strumming` is the same with `GhostHand_Strumming` and `GhostHand_Strumming_Mesh`. The prefab root's axes
are the pose files' **hand model space**: origin at the wrist joint, +y out of the back of the hand, +z along the
fingers, +x toward the little finger of the right hand (the left hand is its mirror image, so its thumb is on +x);
1 unit = 1 m. Each bone's position relative to the root equals `bind.joints` / 1000 (checked to 0.0008 mm); its local
+y points along the bone. Fingertips are not nodes (no leaf bones): use `bind.fingertips`. Bounds of the fretting
hand: x -0.045..0.084, y -0.043..0.015, z -0.046..0.192 m (the strumming hand mirrors x).

Keep **Optimize Game Objects** off (Rig tab): it would strip the bone GameObjects. Unity picks the Animation Type on
first import ("If Unity has never imported the file, the Animation Type is set to None"); the pose driver writes the
bone transforms itself and must not depend on an Animator.

This hierarchy is computed by `export_fbx.py` from the FBX data with the rules above; nobody has imported the files
into Unity yet. Unity writes the `.meta` files on first import.

## Vertex data (for `Runtime/Resources/Shaders/GhostHand.shader`)

The shader reads the vertex colour as control data (fingertip glow = r - g, fade = b - g). The `.blend`'s
`FingerMask` colour attribute holds one finger per channel, so exported as a colour it would light the whole index
finger and hide the ring finger. It is therefore never exported as a colour (the `.blend` itself keeps it).

| Channel | Content |
|---|---|
| vertex colour | **none** (default). `export_fbx.py -- --vertex-colours shader` writes one layer in the shader's convention instead, raw: r = fingertip glow (GhostMask u), g = 0, b = 1 - GhostMask v (1 at the end of the wrist stub), a = 1 |
| UV0 `GhostMask` | u = fingertip glow (1 at the tips), v = 1 on the hand -> 0 at the end of the wrist stub (what the Blender preview material reads) |
| UV1 `FingerMask_IM`, UV2 `FingerMask_RP` | per-finger masks (u, v) = (index, middle) and (ring, pinky), 1 on that finger: for a finger-by-finger shader |

## What to check by eye

1. `renders/contact-sheet.png`: on every fretting chord the index, middle and ring tips sit on their dots, the thumb
   is behind the neck and the knuckles clear the strings; the strumming hand is over the strum line, never in it.
2. `renders/rig-curl.png` and `rig-curl-palm.png`: smooth closed hands, no collapsed knuckles when the fingers curl.
3. The end of the `export_fbx.py` log says `export_fbx.py: OK`, the bone and vertex errors are 0.000x mm, each mesh
   is `1 connected piece(s); 1 material(s)` and there are no vertex colour layers.
4. In Unity, once imported: the hierarchy matches the one above (nothing above the bones is rotated or scaled); both
   prefabs dropped at the origin are about 19 cm long (100 times too big or too small means the unit settings
   changed), fingers along +z, back of the hand up; the fretting thumb is on +x, the strumming thumb on -x.
5. With the `UntitledGuitarApp/GhostHand` material the hand is evenly see-through blue with a bright rim and no
   bright seams. A whole finger glowing or vanishing means some vertex colour reached the shader's vertex control.
