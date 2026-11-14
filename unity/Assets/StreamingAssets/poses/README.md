# Pose JSON v1 — ghost-hand poses

One file per hand pose: `<hand>-<name>.pose.json`, for example `fretting-G.pose.json`.
**Written by** `art/blender/export_poses.py` (never by hand). **Read by** `Runtime/GhostHand/GhostPoseLibrary` and `GhostPose`.
How to author and export a pose: `art/blender/README.md`.

| | |
|---|---|
| Hands | `fretting` (the left hand of a right-handed player), `strumming` |
| Fretting pose names | `rest`, `G`, `C`, `D`, `Em`, `Am`, `E`, `A`, `Dm` |
| Strumming pose names | `rest`, `down-start`, `down-end`, `up-start`, `up-end`, `pick-ready` |
| Units | millimetres for every position; rotations are unit quaternions |
| Text | UTF-8, LF; positions to 3 decimals, quaternions to 6 |

Names are case-sensitive. A new pose name must be added in two places, `KNOWN_POSES` in `export_poses.py` and
`FrettingPoseNames` / `StrummingPoseNames` in `GhostPose.cs`: the exporter and the loader both refuse unknown names.

## 1. The neck frame and the axis rule

Everything is measured in the **neck frame**. Its origin is on the centre line of the neck, at the nut face, on the
playing surface of the fretboard.

| Direction | Blender (`Neck` object, right-handed) | Unity (left-handed) |
|---|---|---|
| along the neck, nut toward bridge; fret *n* is at L·(1 − 2^(−n/12)) | +X | +x |
| up, out of the fretboard | +Z | +y |
| across the neck, toward string 1 (high E) | +Y | +z |

**Positions:** `(x, y, z)_unity = (X, Z, Y)_blender`. A file stores Unity axes, in mm; multiply by 0.001 for Unity metres.

**Rotations:** a Blender quaternion `(w, x, y, z)` becomes the Unity quaternion `(x, y, z, w) = (−x, −z, −y, w)`.
(The axis rule is a mirror, so a rotation about an axis becomes the opposite rotation about the mirrored axis.)
Example: a quarter turn about Blender +Z, which carries +X to +Y, is `(w, x, y, z) = (0.7071, 0, 0, 0.7071)` in Blender and
`(x, y, z, w) = (0, −0.7071, 0, 0.7071)` in Unity, where it carries +x to +z.

## 2. The 16 bones

Parents come before children. The index is the bone's index in `GhostPose` (`GhostPose.BoneNames`, `GhostPose.BoneParents`).

| # | name | parent | # | name | parent |
|---|---|---|---|---|---|
| 0 | `wrist` | none (−1) | 8 | `middle_intermediate` | 7 |
| 1 | `thumb_proximal` | 0 | 9 | `middle_distal` | 8 |
| 2 | `thumb_intermediate` | 1 | 10 | `ring_proximal` | 0 |
| 3 | `thumb_distal` | 2 | 11 | `ring_intermediate` | 10 |
| 4 | `index_proximal` | 0 | 12 | `ring_distal` | 11 |
| 5 | `index_intermediate` | 4 | 13 | `pinky_proximal` | 0 |
| 6 | `index_distal` | 5 | 14 | `pinky_intermediate` | 13 |
| 7 | `middle_proximal` | 0 | 15 | `pinky_distal` | 14 |

The **fingertip** of a digit is the far end (Blender: the tail) of its `*_distal` bone. Digits are numbered 0 thumb,
1 index, 2 middle, 3 ring, 4 pinky, so guitar finger numbers 1–4 are digits 1–4.

## 3. Fields

| Field | Meaning |
|---|---|
| `schema` | `1`. A reader refuses any other number |
| `hand`, `name` | the hand and the pose name (tables above). The file name must be `<hand>-<name>.pose.json` |
| `units` | `"mm"` |
| `space` | `"neck-local-unity"`: neck frame, Unity axes |
| `source.blend` | file name of the .blend the pose came from |
| `source.date` | export time, UTC, ISO 8601 |
| `source.exporter` | `export_poses.py <version>` |
| `source.blender` | Blender version that ran the export |
| `rootPositionMm` | position of the wrist joint (head of the `wrist` bone) in the neck frame |
| `bones[]` | 16 entries `{ name, rotation {x, y, z, w} }`, one per bone of section 2: see section 4 |
| `fingertips[]` | 5 entries `{ bone, positionMm }`: where each fingertip is in this pose, in the neck frame (`bone` = the digit's distal bone) |
| `bind.joints[]` | 16 entries `{ bone, positionMm }`: each joint (bone head) in the **bind pose**, in the hand's model space |
| `bind.fingertips[]` | 5 entries `{ bone, positionMm }`: each fingertip in the bind pose, in the hand's model space |

The **hand's model space** is the armature's own space in Blender, converted with the axis rule of section 1, at the
size the hand has on the neck. Every pose of one hand carries the same `bind` block, because it describes the rig, not
the pose.

## 4. What a rotation is relative to

For a bone *b* write `B_b` for its bind (rest) orientation in the hand's model space and `P_b` for its orientation in
this pose in the neck frame. Its **total turn** is `D_b = P_b · B_b⁻¹`. The file stores

- `wrist`: `q = D_wrist`. This is the turn that takes the hand from its bind pose, standing with its model axes on the
  neck axes, to where it sits on the neck in this pose. It includes any rotation the artist gave the armature object,
  so it does not matter whether the hand was placed by moving the object or by posing the wrist bone.
- every other bone: `q = D_parent⁻¹ · D_b`: how far the bone has turned from its bind orientation beyond what its
  parent did, measured in the hand's model axes. It is **not** measured in the bone's own axes, so bone roll and an
  importer's bone-axis conventions do not affect it.

All 16 are then converted to Unity with the rule of section 1. A pose with every rotation equal to `(0, 0, 0, 1)` is
the bind pose with its model axes on the neck axes.

### Using the rotations in Unity

```
D[0] = q[0]                          D[b] = D[parent(b)] * q[b]                    (GhostPose.AccumulateRotations)
P[0] = rootPositionMm                P[b] = P[parent(b)] + D[parent(b)] * (bind[b] − bind[parent(b)])
tip[d] = P[distal(d)] + D[distal(d)] * (bindTip[d] − bind[distal(d)])              (GhostPose.ComputeJointPositionsMm)
```

`P` and `tip` are the 21 joint positions in the neck frame, in mm. That is enough to draw a hand without any imported
rig (capsules between joints). **Rule of the format:** the `tip` rebuilt this way must be within **0.5 mm** of the
stored `fingertips` entry. The exporter checks it before writing and the loader checks it again, so a file whose
rotations follow another convention is refused instead of drawn wrongly.

For an imported, skinned rig: the neck-frame rotation of bone *b* is `D[b] * bindModel[b]`, where `bindModel[b]` is
the bone's rotation in the rig's model space in its bind pose. As a local rotation, when the bone's parent transform
is its canonical parent: `localRotation[b] = Inverse(bindModel[parent]) * q[b] * bindModel[parent] * bindLocal[b]`.
This holds when the rig's model space in Unity is the mirror image of the armature's space under section 1. An FBX
import may add a fixed turn of its own. Do not guess it: compare the rig's bone positions in its bind pose with
`bind.joints`, and put the fixed rotation that makes them agree on the rig's parent object.

Blend between two poses of the same hand by slerping each of the 16 rotations (shortest path) and moving
`rootPositionMm` on a straight line: `GhostPose.Blend`.

## 5. Example

A real export from a throwaway 16-bone test rig posed on the G dots of the default mock-up (648 mm scale). It shows the
shape of a file; it is **not** the product's G pose. No sample file is kept in this folder: every `*.pose.json` here is loaded.

```json
{
  "schema": 1,
  "hand": "fretting",
  "name": "G",
  "units": "mm",
  "space": "neck-local-unity",
  "source": { "blend": "hand_ik.blend", "date": "2026-11-14T16:07:01Z", "exporter": "export_poses.py 1.1.0", "blender": "5.2.2 LTS" },
  "rootPositionMm": { "x": 85.049, "y": 61.008, "z": 121.828 },
  "bones": [
    { "name": "wrist", "rotation": { "x": -0.055501, "y": 0.995095, "z": -0.073505, "w": 0.036082 } },
    { "name": "thumb_proximal", "rotation": { "x": -0.026203, "y": 0.238822, "z": -0.005911, "w": 0.970692 } },
    { "name": "thumb_intermediate", "rotation": { "x": -0.016354, "y": -0.138382, "z": -0.20384, "w": 0.969037 } },
    { "name": "thumb_distal", "rotation": { "x": 0.066133, "y": -0.09762, "z": -0.234221, "w": 0.965006 } },
    { "name": "index_proximal", "rotation": { "x": -0.189425, "y": 0.007296, "z": 0.0289, "w": 0.981443 } },
    { "name": "index_intermediate", "rotation": { "x": 0.322828, "y": 0.009601, "z": -0.025473, "w": 0.946066 } },
    { "name": "index_distal", "rotation": { "x": 0.297022, "y": 0.004375, "z": -0.025322, "w": 0.954525 } },
    { "name": "middle_proximal", "rotation": { "x": -0.193598, "y": -0.009942, "z": -0.012155, "w": 0.980955 } },
    { "name": "middle_intermediate", "rotation": { "x": 0.310115, "y": -0.013902, "z": -0.011574, "w": 0.950527 } },
    { "name": "middle_distal", "rotation": { "x": 0.281585, "y": -0.006087, "z": -0.008019, "w": 0.959483 } },
    { "name": "ring_proximal", "rotation": { "x": -0.048538, "y": 0.066655, "z": 0.060541, "w": 0.994754 } },
    { "name": "ring_intermediate", "rotation": { "x": 0.451786, "y": 0.046224, "z": 0.100097, "w": 0.885287 } },
    { "name": "ring_distal", "rotation": { "x": 0.356523, "y": 0.021056, "z": 0.066855, "w": 0.931654 } },
    { "name": "pinky_proximal", "rotation": { "x": 0.271638, "y": 0.009053, "z": -0.001516, "w": 0.962356 } },
    { "name": "pinky_intermediate", "rotation": { "x": 0.32612, "y": 0.037359, "z": 0.096063, "w": 0.939693 } },
    { "name": "pinky_distal", "rotation": { "x": 0.154026, "y": 0.045893, "z": 0.083547, "w": 0.983458 } }
  ],
  "fingertips": [
    { "bone": "thumb_distal", "positionMm": { "x": 19.271, "y": -4.164, "z": 43.82 } },
    { "bone": "index_distal", "positionMm": { "x": 62.116, "y": 1.5, "z": -11.018 } },
    { "bone": "middle_distal", "positionMm": { "x": 94.999, "y": 1.5, "z": -18.819 } },
    { "bone": "ring_distal", "positionMm": { "x": 94.999, "y": 1.5, "z": 18.819 } },
    { "bone": "pinky_distal", "positionMm": { "x": 126.005, "y": -0.01, "z": 50.936 } }
  ],
  "bind": {
    "joints": [
      { "bone": "wrist", "positionMm": { "x": 0, "y": 0, "z": 0 } },
      { "bone": "thumb_proximal", "positionMm": { "x": 32, "y": -6, "z": 28 } },
      { "bone": "thumb_intermediate", "positionMm": { "x": 56.055, "y": -12.599, "z": 56.667 } },
      { "bone": "thumb_distal", "positionMm": { "x": 73.532, "y": -25.277, "z": 77.496 } },
      { "bone": "index_proximal", "positionMm": { "x": 30, "y": 0, "z": 86 } },
      { "bone": "index_intermediate", "positionMm": { "x": 34.241, "y": -10.87, "z": 126.347 } },
      { "bone": "index_distal", "positionMm": { "x": 36.467, "y": -25.783, "z": 147.528 } },
      { "bone": "middle_proximal", "positionMm": { "x": 10, "y": 0, "z": 88 } },
      { "bone": "middle_intermediate", "positionMm": { "x": 10, "y": -11.906, "z": 132.433 } },
      { "bone": "middle_distal", "positionMm": { "x": 10, "y": -28.539, "z": 156.188 } },
      { "bone": "ring_proximal", "positionMm": { "x": -10, "y": 0, "z": 86 } },
      { "bone": "ring_intermediate", "positionMm": { "x": -13.536, "y": -10.87, "z": 126.414 } },
      { "bone": "ring_distal", "positionMm": { "x": -15.463, "y": -26.357, "z": 148.447 } },
      { "bone": "pinky_proximal", "positionMm": { "x": -28, "y": 0, "z": 80 } },
      { "bone": "pinky_intermediate", "positionMm": { "x": -34.627, "y": -8.541, "z": 111.179 } },
      { "bone": "pinky_distal", "positionMm": { "x": -38.034, "y": -20.013, "z": 127.204 } }
    ],
    "fingertips": [
      { "bone": "thumb_distal", "positionMm": { "x": 86.334, "y": -41.99, "z": 92.753 } },
      { "bone": "index_distal", "positionMm": { "x": 37.786, "y": -43.805, "z": 160.077 } },
      { "bone": "middle_distal", "positionMm": { "x": 10, "y": -47.38, "z": 169.38 } },
      { "bone": "ring_distal", "positionMm": { "x": -16.563, "y": -44.378, "z": 161.018 } },
      { "bone": "pinky_distal", "positionMm": { "x": -40.299, "y": -35.576, "z": 137.864 } }
    ]
  }
}
```

In it, the index, middle and ring fingertips sit on the G dots: `y = 1.5` is the string height above the fretboard,
`x = 62.1` and `95.0` are 75% of the way to frets 2 and 3, and `z = −11.0, −18.8, +18.8` are strings 5, 6 and 1.

## 6. What the loader accepts

`GhostPoseLibrary` refuses a file (it is logged as `ghost_pose_rejected` and skipped; the other files still load) when:

- it is not JSON, `schema` is not 1, `units` is not `mm` or `space` is not `neck-local-unity`;
- `hand` or `name` is not in the tables at the top, or the file name is not `<hand>-<name>.pose.json`;
- a canonical bone is missing or listed twice, a bone name is unknown, or a rotation's length differs from 1 by more than 0.001;
- a fingertip is missing, repeated or not on a distal bone, or any position is not a finite number;
- it has a `bind` block that is incomplete, or its rotations do not rebuild its fingertips within 0.5 mm (section 4).

It loads a file with a **warning** (`ghost_pose_warning`) when the file has no `bind` block, or when its `bind` joints
differ by more than 0.05 mm from another pose of the same hand (two rigs mixed in one folder).
Keys the loader does not know are ignored, so new optional keys do not need a new schema number. Changing the
meaning of an existing key, the bone list or the axis rule does: bump `schema` in the exporter and in `GhostPose.SchemaVersion`.

## 7. In code

```csharp
var library = new GhostPoseLibrary();          // reads StreamingAssets/poses; new GhostPoseLibrary(folder) for tests
library.Reload();                              // again whenever the files change; never throws
GhostPose g, c;
if (library.TryGet("fretting", "G", out g) && library.TryGet("fretting", "C", out c))
{
    GhostPose.Blend(g, c, t, rotations, out rootMm);                          // Quaternion[16], eased, no allocation
    g.ComputeJointPositionsMm(rotations, rootMm, totals, positionsMm);        // Quaternion[16], Vector3[21]
}
```

The EditMode test `GhostPoseLibraryTests.EveryPoseJsonInStreamingAssetsLoads` loads this folder and fails on any refused
file or warning. With no pose file here yet it reports **Inconclusive**, not passed.
