"""
export_poses.py - export the posed ghost hand from Blender to Pose JSON v1 (one file per pose, Unity-ready).

The format and the maths are specified in unity/Assets/StreamingAssets/poses/README.md. In short: for every one of the
16 canonical bones the file holds how far the bone has turned from its bind (rest) orientation relative to its parent,
converted from Blender's axes to Unity's; plus the wrist position and the fingertip positions in neck-local millimetres,
and the bind positions of the joints so that Unity can rebuild every joint from the rotations alone. The numbers come
from each bone's final evaluated pose matrix, so IK, constraints and drivers are included. It does not matter whether
you placed the hand on the neck by moving the armature object or by posing the wrist bone: both give the same file.

Before a file is written the exporter rebuilds the fingertips from the rotations it is about to write, the way Unity
will, and refuses to write if they land more than 0.5 mm from where Blender has them (a stretched or moved finger bone).

WHAT YOU NEED IN THE .blend
  * an armature whose 16 bones are named exactly like the README table (wrist, thumb_proximal ... pinky_distal).
    More bones are fine: they are not exported. A bone above the wrist (a forearm) may be posed freely. A helper bone
    between the wrist and a finger must stay at rest, and finger bones may only rotate (no location, scale or stretch);
  * the object 'Neck' from build_neck_mockup.py (the reference frame, and the fingertip targets). Without it the world
    frame is used and the 3 mm target check is skipped;
  * 1 Blender unit = 1 m (Scene > Units > Unit Scale 1.0), as the mock-up is built.

POSE SOURCES (--source, or CONFIG["source"])
  markers  one pose per marker: Timeline markers and Pose Markers of the armature's action. Name a marker like the pose,
           "G" or "fretting-G" / "strumming-down-start". Key the pose at that frame (select all bones, press I).
           A marker that names the other hand ("strumming-..." while exporting the fretting hand) is skipped.
  actions  one pose per Action named like a pose (Pose Library "Create Pose Asset" gives you exactly that). A bone the
           action has no keys for is exported as it is on screen, and the report says which bones those were.
  current  the pose on screen right now, saved under --name (for example G). It also exports unkeyed changes.
  auto     (default) markers if any marker is named like a pose, else actions, else current.
  Only known pose names are exported: rest, G, C, D, Em, Am, E, A, Dm (fretting);
  rest, down-start, down-end, up-start, up-end, pick-ready (strumming). Other markers and actions are listed and skipped.
  To add a name, add it to KNOWN_POSES below and to GhostPose.cs (FrettingPoseNames / StrummingPoseNames): Unity
  refuses pose files whose name it does not know.

markers and actions step the timeline. Your scene is put back afterwards (frame, action, and the unkeyed transforms of
bones and objects), and the report says if you had unkeyed changes, because those are not in the exported files.

A) IN BLENDER (the usual way): Scripting workspace > Text Editor > Text > Open... > this file; edit CONFIG below;
   Text > Run Script (Alt+P). A message box lists what was written; details also go to the terminal Blender started from.
B) HEADLESS on a saved file (options after the "--"; --python-exit-code must come before --python):
   /Applications/Blender.app/Contents/MacOS/Blender --factory-startup -b art/blender/hand_poses.blend \
       --python-exit-code 1 --python art/blender/export_poses.py -- --hand fretting --source markers
   --hand fretting|strumming   which hand this armature is                    (CONFIG["hand"])
   --source auto|markers|actions|current                                      (CONFIG["source"])
   --name G                    pose name for --source current                  (CONFIG["name"])
   --armature NAME             the armature object (default: active armature, or the only one)
   --neck NAME                 frame object, default Neck
   --out-dir DIR               default <repo>/unity/Assets/StreamingAssets/poses
   --tolerance-mm 3            fingertip-to-target limit for the report (SL4 acceptance: 3 mm)
   --strict                    exit 1 and write nothing if any fingertip is farther than the tolerance from its dot,
                               or if a chord pose has no dots to be checked against
   --dry-run                   print the JSON, write no files
Exit code 1 (with --python-exit-code 1) on any error. Output files are named <hand>-<name>.pose.json.
"""

import argparse
import datetime
import json
import math
import os
import re
import sys

import bpy
from mathutils import Matrix, Vector

EXPORTER_VERSION = "1.1.0"  # 1.1.0: the wrist rotation carries the armature object's rotation; adds the "bind" block
SCHEMA = 1
MM_PER_UNIT = 1000.0        # the mock-up is built with 1 Blender unit = 1 m
FK_LIMIT_MM = 0.5           # rotations must reproduce the file's own fingertips this closely (GhostPose.cs: FkToleranceMm)

# ---- CONFIG: used when the script runs from Blender's Text Editor; command-line options override it -------------------
CONFIG = {
    "hand": "fretting",   # "fretting" (the player's left hand for a right-handed player) or "strumming"
    "source": "auto",     # auto | markers | actions | current
    "name": None,         # pose name for source "current", for example "G"
    "armature": None,     # armature object name; None = the active armature, or the only one in the file
    "neck": "Neck",       # frame object made by build_neck_mockup.py
    "out_dir": None,      # None = <repo>/unity/Assets/StreamingAssets/poses
    "tolerance_mm": 3.0,  # fingertip-to-target limit (docs/TODO.md SL4: within 3 mm of the dot)
    "strict": False,
    "dry_run": False,
}

# The 16 canonical bones, parents first. Keep in step with GhostPose.BoneNames / BoneParents (C#) and the README table.
BONES = [
    ("wrist", -1),
    ("thumb_proximal", 0), ("thumb_intermediate", 1), ("thumb_distal", 2),
    ("index_proximal", 0), ("index_intermediate", 4), ("index_distal", 5),
    ("middle_proximal", 0), ("middle_intermediate", 7), ("middle_distal", 8),
    ("ring_proximal", 0), ("ring_intermediate", 10), ("ring_distal", 11),
    ("pinky_proximal", 0), ("pinky_intermediate", 13), ("pinky_distal", 14),
]
BONE_NAMES = [b[0] for b in BONES]
DIGIT_DISTAL = {0: "thumb_distal", 1: "index_distal", 2: "middle_distal", 3: "ring_distal", 4: "pinky_distal"}  # finger number -> bone
HANDS = ("fretting", "strumming")
KNOWN_POSES = {
    "fretting": ("rest", "G", "C", "D", "Em", "Am", "E", "A", "Dm"),
    "strumming": ("rest", "down-start", "down-end", "up-start", "up-end", "pick-ready"),
}
CHORD_POSES = ("G", "C", "D", "Em", "Am", "E", "A", "Dm")  # poses that have fingertip targets in the mock-up
NAME_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9-]*$")

# Blender axes -> Unity axes: (x, y, z)_unity = (x, z, y)_blender. A reflection (det -1), symmetric, so C = C^T = C^-1.
C3 = Matrix(((1.0, 0.0, 0.0), (0.0, 0.0, 1.0), (0.0, 1.0, 0.0)))


class UserError(Exception):
    """Something the user can fix (reported in one message, exit code 1)."""


# ----------------------------------------------------------------------------------------------------------------------
# axis conversion (README section "Coordinate convention")
# ----------------------------------------------------------------------------------------------------------------------
def to_unity_vector(v):
    """Position: (x, y, z)_blender -> (x, z, y)_unity."""
    return (v.x, v.z, v.y)


def to_unity_quaternion(q):
    """Rotation: Blender quaternion (w, x, y, z) -> Unity (x, y, z, w) = (-x, -z, -y, w). Sign chosen so that w >= 0.

    Derivation: a rotation matrix R in Blender axes is R_unity = C R C^T. For a unit quaternion (w, v) the conjugation by
    the reflection C gives the quaternion (w, -C v); C v = (vx, vz, vy), hence (x, y, z, w) = (-vx, -vz, -vy, w).
    """
    if q.w < 0.0:
        q = -q
    return (-q.x, -q.z, -q.y, q.w)


def fmt(value, decimals):
    """A JSON number without exponent, trailing zeros or a minus on zero."""
    s = ("%.*f" % (decimals, value)).rstrip("0").rstrip(".")
    return "0" if s in ("", "-0") else s


# ---- what Unity does with the file: UnityEngine.Quaternion's own formulas, on (x, y, z, w) tuples ---------------------
def unity_qmul(lhs, rhs):
    lx, ly, lz, lw = lhs
    rx, ry, rz, rw = rhs
    return (lw * rx + lx * rw + ly * rz - lz * ry,
            lw * ry + ly * rw + lz * rx - lx * rz,
            lw * rz + lz * rw + lx * ry - ly * rx,
            lw * rw - lx * rx - ly * ry - lz * rz)


def unity_qrot(q, p):
    x, y, z, w = q
    x2, y2, z2 = x * 2.0, y * 2.0, z * 2.0
    xx, yy, zz = x * x2, y * y2, z * z2
    xy, xz, yz = x * y2, x * z2, y * z2
    wx, wy, wz = w * x2, w * y2, w * z2
    return ((1.0 - (yy + zz)) * p[0] + (xy - wz) * p[1] + (xz + wy) * p[2],
            (xy + wz) * p[0] + (1.0 - (xx + zz)) * p[1] + (yz - wx) * p[2],
            (xz - wy) * p[0] + (yz + wx) * p[1] + (1.0 - (xx + yy)) * p[2])


def unity_fk(quats, root, bind_joints, bind_tips):
    """README "Forward kinematics": joint positions (16) and fingertip positions (5) from the 16 rotations alone.

    quats, bind_joints: lists in canonical bone order; bind_tips: thumb..pinky. Everything in Unity axes, millimetres.
    """
    total, pos = [None] * 16, [None] * 16
    for i, (_name, parent) in enumerate(BONES):
        if parent < 0:
            total[i], pos[i] = quats[i], tuple(root)
        else:
            total[i] = unity_qmul(total[parent], quats[i])
            off = unity_qrot(total[parent], tuple(bind_joints[i][k] - bind_joints[parent][k] for k in range(3)))
            pos[i] = tuple(pos[parent][k] + off[k] for k in range(3))
    tips = []
    for digit in sorted(DIGIT_DISTAL):
        i = BONE_NAMES.index(DIGIT_DISTAL[digit])
        off = unity_qrot(total[i], tuple(bind_tips[digit][k] - bind_joints[i][k] for k in range(3)))
        tips.append(tuple(pos[i][k] + off[k] for k in range(3)))
    return pos, tips


def distance(a, b):
    return math.sqrt(sum((a[k] - b[k]) ** 2 for k in range(3)))


# ----------------------------------------------------------------------------------------------------------------------
# finding things in the .blend
# ----------------------------------------------------------------------------------------------------------------------
def find_repo_root():
    starts = []
    try:
        starts.append(os.path.dirname(os.path.abspath(__file__)))
    except NameError:
        pass
    if bpy.data.filepath:
        starts.append(os.path.dirname(bpy.data.filepath))
    starts.append(os.getcwd())
    for start in starts:
        d = start
        while True:
            if os.path.isdir(os.path.join(d, "unity", "Assets", "StreamingAssets")):
                return d
            parent = os.path.dirname(d)
            if parent == d:
                break
            d = parent
    return None


def find_armature(name):
    if name:
        obj = bpy.data.objects.get(name)
        if obj is None or obj.type != "ARMATURE":
            raise UserError("armature object %r not found (armatures in this file: %s)" % (name, ", ".join(sorted(o.name for o in bpy.data.objects if o.type == "ARMATURE")) or "none"))
        return obj
    active = bpy.context.view_layer.objects.active
    if active is not None and active.type == "ARMATURE":
        return active
    arms = [o for o in bpy.context.scene.objects if o.type == "ARMATURE"]
    if len(arms) == 1:
        return arms[0]
    if not arms:
        raise UserError("no armature object in the scene")
    raise UserError("several armatures (%s): click the one you want to export, or set CONFIG['armature'] / --armature" % ", ".join(sorted(o.name for o in arms)))


def check_rig(arm):
    """The canonical bones must exist, and each canonical parent must be an ancestor of its child in the real hierarchy."""
    bones = arm.data.bones
    missing = [n for n in BONE_NAMES if n not in bones]
    if missing:
        have = sorted(b.name for b in bones)
        raise UserError("armature %r has no bone named %s. The 16 canonical names are: %s. Bones in the armature: %s%s" % (
            arm.name, ", ".join(missing), ", ".join(BONE_NAMES), ", ".join(have[:40]), " ..." if len(have) > 40 else ""))
    for name, parent in BONES:
        if parent < 0:
            continue
        want = BONE_NAMES[parent]
        walker = bones[name].parent
        while walker is not None and walker.name != want:
            walker = walker.parent
        if walker is None:
            raise UserError("bone %r must be a descendant of bone %r (the canonical hierarchy, see the README)" % (name, want))


def helpers_between(arm, name, parent_name):
    """Names of the non-canonical bones that sit between a canonical bone and its canonical parent."""
    out = []
    walker = arm.data.bones[name].parent
    while walker is not None and walker.name != parent_name:
        out.append(walker.name)
        walker = walker.parent
    return out


def frame_matrix(arm_eval, neck):
    """Armature space -> neck frame (4x4) and the armature's uniform scale in that frame. The neck must be a 1:1 frame."""
    mn = neck.matrix_world.copy() if neck is not None else Matrix.Identity(4)
    if neck is not None:
        scale = mn.to_scale()
        if max(abs(scale.x - 1.0), abs(scale.y - 1.0), abs(scale.z - 1.0)) > 1e-3:
            raise UserError("object %r is scaled (%.3f, %.3f, %.3f): the neck frame must be 1:1, apply or reset its scale" % (neck.name, scale.x, scale.y, scale.z))
    if mn.to_3x3().determinant() <= 0.0:
        raise UserError("the neck frame is mirrored (negative scale)")
    f = mn.inverted() @ arm_eval.matrix_world
    m3 = f.to_3x3()
    lengths = [m3.col[i].length for i in range(3)]
    if min(lengths) < 1e-9:
        raise UserError("the armature object has a zero scale")
    if m3.determinant() <= 0.0:
        raise UserError("the armature object is mirrored (negative scale): apply its scale (Ctrl+A) or rebuild it unmirrored")
    skew = max(abs(m3.col[i].dot(m3.col[j])) / (lengths[i] * lengths[j]) for i, j in ((0, 1), (0, 2), (1, 2)))
    if max(lengths) / min(lengths) > 1.001 or skew > 1e-4:
        raise UserError("the armature object is scaled unevenly (%.3f, %.3f, %.3f): apply its scale (Object > Apply > Scale)" % tuple(lengths))
    return f, sum(lengths) / 3.0


# ----------------------------------------------------------------------------------------------------------------------
# reading a pose
# ----------------------------------------------------------------------------------------------------------------------
def orthonormal(m3):
    return m3.normalized()  # columns normalised: removes bone scale (scale is not exported; a warning covers big values)


def read_pose(arm, neck, warnings):
    """Evaluate the armature as it is now.

    Returns a dict. rotations: Unity (x, y, z, w) per bone. Everything else in millimetres and Blender axes:
    root_mm, tips_mm {digit: Vector}, heads_mm {bone: Vector} in the neck frame; bind_joints_mm {bone: Vector} and
    bind_tips_mm {digit: Vector} in the armature's own axes (the hand's model space), at the size the hand has on the neck.
    """
    bpy.context.view_layer.update()
    depsgraph = bpy.context.evaluated_depsgraph_get()
    ev = arm.evaluated_get(depsgraph)
    frame, scale = frame_matrix(ev, neck)
    if abs(scale - 1.0) > 1e-3:
        warnings.append("the armature object is scaled by %.4f: the millimetres in the file include it, so give the FBX the same scale" % scale)

    # D[b] = how far bone b has turned from its bind orientation. The posed orientation is taken in the neck frame and the
    # bind orientation in the armature's own axes, so D[wrist] also holds the rotation of the armature object: the file
    # is the same whether the hand was placed by moving the object or by posing the wrist.
    total = {}
    for name, _parent in BONES:
        pb = ev.pose.bones[name]
        posed = orthonormal((frame @ pb.matrix).to_3x3())
        rest = orthonormal(arm.data.bones[name].matrix_local.to_3x3())
        if abs(posed.determinant() - 1.0) > 1e-3:
            warnings.append("bone %s has shear or a mirrored scale in this pose; its rotation is approximate" % name)
        s = pb.matrix.to_scale()
        if max(abs(s.x - 1.0), abs(s.y - 1.0), abs(s.z - 1.0)) > 0.02:
            warnings.append("bone %s is scaled (%.2f, %.2f, %.2f) in this pose; scale is not exported" % (name, s.x, s.y, s.z))
        total[name] = posed @ rest.transposed()

    rotations = []
    worst = 0.0
    for name, parent in BONES:
        rho = total[name] if parent < 0 else total[BONE_NAMES[parent]].transposed() @ total[name]  # relative to the parent
        q = rho.to_quaternion()
        unity = to_unity_quaternion(q)
        # cross-check the closed form against the matrix conjugation R_unity = C R C^T
        qm = (C3 @ rho @ C3).to_quaternion()
        dot = abs(unity[3] * qm.w + unity[0] * qm.x + unity[1] * qm.y + unity[2] * qm.z)
        worst = max(worst, 1.0 - dot)
        rotations.append(unity)
    if worst > 1e-5:
        raise UserError("internal check failed: quaternion formula and matrix conjugation disagree (%.2e)" % worst)

    to_mm = MM_PER_UNIT
    heads = {name: (frame @ ev.pose.bones[name].head) * to_mm for name in BONE_NAMES}
    tips = {digit: (frame @ ev.pose.bones[bone].tail) * to_mm for digit, bone in DIGIT_DISTAL.items()}  # tail of the distal bone = the fingertip point
    bind_joints = {name: arm.data.bones[name].head_local * (scale * to_mm) for name in BONE_NAMES}
    bind_tips = {digit: arm.data.bones[bone].tail_local * (scale * to_mm) for digit, bone in DIGIT_DISTAL.items()}

    # Only rotations are exported. Find the bones that are not where rotations alone would put them.
    pos, drift = {}, {}
    for name, parent in BONES:
        if parent < 0:
            pos[name] = heads[name].copy()
        else:
            pn = BONE_NAMES[parent]
            pos[name] = pos[pn] + total[pn] @ (bind_joints[name] - bind_joints[pn])
        drift[name] = (pos[name] - heads[name]).length
    for name, parent in BONES:
        if parent >= 0 and drift[name] > 0.05 and drift[BONE_NAMES[parent]] <= 0.05:
            between = helpers_between(arm, name, BONE_NAMES[parent])
            warnings.append("bone %s is %.2f mm from where rotations alone put it: it was moved or stretched (location, scale, Stretch To)%s. Only rotations are exported" % (
                name, drift[name], ", or the helper bone %s between it and %s is posed" % (" / ".join(between), BONE_NAMES[parent]) if between else ""))
    return {"rotations": rotations, "root_mm": heads["wrist"], "tips_mm": tips, "heads_mm": heads,
            "bind_joints_mm": bind_joints, "bind_tips_mm": bind_tips}


def target_report(hand, name, pose, fk_tips, neck, tolerance, warnings):
    """Compare fingertips with the mock-up's tip_<chord>_f<finger> empties.

    Both the fingertip Blender shows and the one Unity rebuilds from the rotations must be on the dot.
    Returns (lines, n_fail, max_error_mm, n_checked).
    """
    if name not in CHORD_POSES or hand != "fretting":
        return [], 0, 0.0, 0
    targets = [o for o in bpy.data.objects if o.name.startswith("tip_%s_f" % name) and "finger" in o.keys()]
    if neck is None or not targets:
        warnings.append("no fingertip targets 'tip_%s_f*' found (build the neck with build_neck_mockup.py and keep its Neck object): the %.0f mm check was skipped" % (name, tolerance))
        return [], 0, 0.0, 0
    mn_inv = neck.matrix_world.inverted()
    lines, fails, worst = [], 0, 0.0
    for obj in sorted(targets, key=lambda o: int(o["finger"])):
        finger = int(obj["finger"])
        if finger not in DIGIT_DISTAL:
            warnings.append("target %s names finger %d, which is not a finger (0 = thumb ... 4 = pinky)" % (obj.name, finger))
            continue
        want = (mn_inv @ obj.matrix_world).translation * MM_PER_UNIT
        err = max((pose["tips_mm"][finger] - want).length, distance(fk_tips[finger], to_unity_vector(want)))
        worst = max(worst, err)
        ok = err <= tolerance
        fails += 0 if ok else 1
        lines.append("    finger %d %-12s %5.2f mm from its dot  %s" % (finger, DIGIT_DISTAL[finger], err, "ok" if ok else "TOO FAR (limit %.1f mm)" % tolerance))
    return lines, fails, worst, len(lines)


# ----------------------------------------------------------------------------------------------------------------------
# JSON
# ----------------------------------------------------------------------------------------------------------------------
def build_json(hand, name, pose, source_blend):
    now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    def xyz(v):
        return '{ "x": %s, "y": %s, "z": %s }' % tuple(fmt(c, 3) for c in to_unity_vector(v))

    out = ["{",
           '  "schema": %d,' % SCHEMA,
           '  "hand": %s,' % json.dumps(hand),
           '  "name": %s,' % json.dumps(name),
           '  "units": "mm",',
           '  "space": "neck-local-unity",',
           '  "source": { "blend": %s, "date": "%s", "exporter": %s, "blender": %s },' % (
               json.dumps(source_blend), now, json.dumps("export_poses.py " + EXPORTER_VERSION), json.dumps(bpy.app.version_string)),
           '  "rootPositionMm": %s,' % xyz(pose["root_mm"]),
           '  "bones": [']
    for i, (bone, _parent) in enumerate(BONES):
        x, y, z, w = pose["rotations"][i]
        out.append('    { "name": %s, "rotation": { "x": %s, "y": %s, "z": %s, "w": %s } }%s' % (
            json.dumps(bone), fmt(x, 6), fmt(y, 6), fmt(z, 6), fmt(w, 6), "," if i < len(BONES) - 1 else ""))
    out.append("  ],")
    digits = sorted(DIGIT_DISTAL)
    out.append('  "fingertips": [')
    for k, digit in enumerate(digits):
        out.append('    { "bone": %s, "positionMm": %s }%s' % (json.dumps(DIGIT_DISTAL[digit]), xyz(pose["tips_mm"][digit]), "," if k < len(digits) - 1 else ""))
    out.append("  ],")
    out.append('  "bind": {')
    out.append('    "joints": [')
    for i, (bone, _parent) in enumerate(BONES):
        out.append('      { "bone": %s, "positionMm": %s }%s' % (json.dumps(bone), xyz(pose["bind_joints_mm"][bone]), "," if i < len(BONES) - 1 else ""))
    out.append("    ],")
    out.append('    "fingertips": [')
    for k, digit in enumerate(digits):
        out.append('      { "bone": %s, "positionMm": %s }%s' % (json.dumps(DIGIT_DISTAL[digit]), xyz(pose["bind_tips_mm"][digit]), "," if k < len(digits) - 1 else ""))
    out.append("    ]")
    out.append("  }")
    out.append("}")
    return "\n".join(out) + "\n"


def check_json(text, hand, name):
    """Parse what we are about to write and apply the rules of the Unity loader (GhostPose.TryParse).

    Returns (fk_tips {digit: (x, y, z) Unity mm}, worst_mm): the fingertips Unity rebuilds from the rotations, and how far
    the worst one is from the fingertip stored in the file.
    """
    def fail(what):
        raise UserError("internal check failed for %s-%s: %s" % (hand, name, what))

    def vec(d):
        v = (d["x"], d["y"], d["z"])
        if not all(isinstance(c, (int, float)) and math.isfinite(c) for c in v):
            fail("a position is not a finite number")
        return v

    d = json.loads(text)
    if not (d["schema"] == SCHEMA and d["hand"] == hand and d["name"] == name and d["units"] == "mm" and d["space"] == "neck-local-unity"):
        fail("header fields")
    names = [b["name"] for b in d["bones"]]
    if names != BONE_NAMES:
        fail("the bone list is not the 16 canonical bones in order")
    quats = []
    for b in d["bones"]:
        r = b["rotation"]
        q = (r["x"], r["y"], r["z"], r["w"])
        n = math.sqrt(sum(c * c for c in q))
        if not (math.isfinite(n) and abs(n - 1.0) <= 1e-3):
            fail("bone %s: rotation is not a unit quaternion (norm %r)" % (b["name"], n))
        quats.append(tuple(c / n for c in q))
    digits = sorted(DIGIT_DISTAL)
    if [t["bone"] for t in d["fingertips"]] != [DIGIT_DISTAL[k] for k in digits]:
        fail("the fingertip list is not thumb..pinky")
    if [j["bone"] for j in d["bind"]["joints"]] != BONE_NAMES or [t["bone"] for t in d["bind"]["fingertips"]] != [DIGIT_DISTAL[k] for k in digits]:
        fail("the bind block is not the 16 joints and 5 fingertips in order")
    stored = [vec(t["positionMm"]) for t in d["fingertips"]]
    _pos, tips = unity_fk(quats, vec(d["rootPositionMm"]), [vec(j["positionMm"]) for j in d["bind"]["joints"]],
                          [vec(t["positionMm"]) for t in d["bind"]["fingertips"]])
    worst = max(distance(tips[k], stored[k]) for k in range(len(digits)))
    return {digit: tips[k] for k, digit in enumerate(digits)}, worst


def write_atomically(path, text):
    """Unity may be reading the folder: never leave a half-written pose file. (Unity ignores dot-files, so no .meta.)"""
    tmp = os.path.join(os.path.dirname(path), "." + os.path.basename(path) + ".tmp")
    with open(tmp, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    os.replace(tmp, path)


# ----------------------------------------------------------------------------------------------------------------------
# which poses to export
# ----------------------------------------------------------------------------------------------------------------------
def parse_pose_label(label):
    """'G' -> (None, 'G'); 'fretting-G' -> ('fretting', 'G'); None if it cannot be a pose name."""
    for hand in HANDS:
        if label.startswith(hand + "-"):
            rest = label[len(hand) + 1:]
            return (hand, rest) if NAME_RE.match(rest) else None
    return (None, label) if NAME_RE.match(label) else None


def known_names_text():
    return "; ".join("%s: %s" % (h, ", ".join(KNOWN_POSES[h])) for h in HANDS)


def action_bone_paths(action):
    """The fcurve data paths of an action that drive pose bones (slotted actions, Blender 4.4+)."""
    from bpy_extras import anim_utils
    paths = set()
    for slot in action.slots:
        bag = anim_utils.action_get_channelbag_for_slot(action, slot)
        if bag is not None:
            paths.update(fc.data_path for fc in bag.fcurves if fc.data_path.startswith('pose.bones["'))
    return paths


class Job(object):
    def __init__(self, hand, name, label, frame=None, action=None):
        self.hand, self.name, self.label, self.frame, self.action = hand, name, label, frame, action


def collect_jobs(arm, cfg, notes):
    hand = cfg["hand"]
    scene = bpy.context.scene
    source = cfg["source"]

    def take(label, frame, action, found, skipped, other_hand):
        parsed = parse_pose_label(label)
        if parsed is None:
            skipped.append(label)
        elif parsed[0] is not None and parsed[0] != hand:
            other_hand.append(label)
        elif parsed[1] not in KNOWN_POSES[hand]:
            skipped.append(label)
        else:
            found.append(Job(hand, parsed[1], label, frame=frame, action=action))

    markers_found, markers_skipped, markers_other = [], [], []
    for m in scene.timeline_markers:
        take(m.name, m.frame, None, markers_found, markers_skipped, markers_other)
    ad = arm.animation_data
    if ad is not None and ad.action is not None:
        for m in ad.action.pose_markers:
            take(m.name, m.frame, None, markers_found, markers_skipped, markers_other)
    actions_found, actions_skipped, actions_other = [], [], []
    for act in bpy.data.actions:
        if not action_bone_paths(act):
            continue  # an object's action (an IK target, a camera): never a hand pose
        take(act.name, int(round(act.frame_range[0])), act, actions_found, actions_skipped, actions_other)

    if source == "auto":
        source = "markers" if markers_found else ("actions" if actions_found else "current")
        notes.append("source auto -> %s" % source)
    if source == "markers":
        if not markers_found:
            raise UserError("no marker is named like a %s pose. Add markers (Timeline > Marker > Add Marker, F2 to rename) called e.g. G or fretting-G; known names: %s" % (hand, known_names_text()))
        if markers_skipped:
            notes.append("skipped markers (not %s pose names): %s" % (hand, ", ".join(markers_skipped)))
        if markers_other:
            notes.append("skipped markers of the other hand: %s" % ", ".join(markers_other))
        return markers_found, source
    if source == "actions":
        if not actions_found:
            raise UserError("no action is named like a %s pose (known names: %s)" % (hand, known_names_text()))
        if actions_skipped:
            notes.append("skipped actions (not %s pose names): %s" % (hand, ", ".join(actions_skipped)))
        if actions_other:
            notes.append("skipped actions of the other hand: %s" % ", ".join(actions_other))
        return actions_found, source
    # current
    name = cfg["name"]
    if not name:
        raise UserError("--source current needs a pose name: --name G (or CONFIG['name'])")
    parsed = parse_pose_label(name)
    if parsed is None:
        raise UserError("%r is not a valid pose name (letters, digits and '-', for example G or down-start)" % name)
    if parsed[0] is not None and parsed[0] != hand:
        raise UserError("the name %r is for the %s hand, but this export is for the %s hand (--hand)" % (name, parsed[0], hand))
    if parsed[1] not in KNOWN_POSES[hand]:
        raise UserError("%r is not a %s pose the app knows (%s). To add one, add it to KNOWN_POSES in this script and to GhostPose.cs: Unity refuses unknown names" % (
            parsed[1], hand, ", ".join(KNOWN_POSES[hand])))
    return [Job(hand, parsed[1], name)], source


# ----------------------------------------------------------------------------------------------------------------------
# keeping the scene as it was while we step through frames or actions
# ----------------------------------------------------------------------------------------------------------------------
def _channels(item):
    """Transform channels of an object or a pose bone, as plain values."""
    return (item.rotation_mode, item.location.copy(), item.rotation_quaternion.copy(), tuple(item.rotation_euler),
            tuple(item.rotation_axis_angle), item.scale.copy())


def _apply_channels(item, snap):
    mode, loc, quat, eul, aa, scl = snap
    item.rotation_mode = mode
    item.location, item.rotation_quaternion, item.rotation_euler, item.rotation_axis_angle, item.scale = loc, quat, eul, aa, scl


def _differs(item, snap):
    _mode, loc, quat, eul, aa, scl = snap
    return ((item.location - loc).length > 1e-7 or (item.rotation_quaternion - quat).magnitude > 1e-7
            or max(abs(a - b) for a, b in zip(item.rotation_euler, eul)) > 1e-7
            or max(abs(a - b) for a, b in zip(item.rotation_axis_angle, aa)) > 1e-7 or (item.scale - scl).length > 1e-7)


class SceneGuard(object):
    """Snapshot the frame, the armature's action and the transforms of every object and pose bone; put them back on exit.

    Stepping the timeline throws away transforms that are not keyed. The snapshot brings them back on screen, and
    'unkeyed' lists what they were, because the exported files hold the keyed values instead.
    """

    def __init__(self, arm):
        self.arm = arm
        scene = bpy.context.scene
        self.frame, self.subframe = scene.frame_current, scene.frame_subframe
        self.objects = {obj.name: _channels(obj) for obj in scene.objects}
        self.bones = {}
        for obj in scene.objects:
            if obj.type == "ARMATURE" and obj.pose is not None:
                for pb in obj.pose.bones:
                    self.bones[(obj.name, pb.name)] = _channels(pb)
        ad = arm.animation_data
        self.had_anim_data = ad is not None
        self.action = ad.action if ad else None
        self.slot = ad.action_slot if ad else None
        self.unkeyed = []

    def start_from_screen(self):
        """Give the hand the pose it had on screen: every action is applied on top of that, never on the previous one."""
        for pb in self.arm.pose.bones:
            _apply_channels(pb, self.bones[(self.arm.name, pb.name)])

    def assign_action(self, action):
        if self.arm.animation_data is None:
            self.arm.animation_data_create()
        ad = self.arm.animation_data
        ad.action = action
        if ad.action_slot is None:
            from bpy_extras import anim_utils
            slot = anim_utils.action_get_first_suitable_slot(action, "OBJECT")
            if slot is None:
                raise UserError("action %r has no slot that fits an object" % action.name)
            ad.action_slot = slot

    def go_to(self, frame):
        bpy.context.scene.frame_set(int(frame))
        bpy.context.view_layer.update()

    def restore(self):
        scene = bpy.context.scene
        ad = self.arm.animation_data
        if ad is not None:
            if not self.had_anim_data:
                self.arm.animation_data_clear()
            else:
                ad.action = self.action
                if self.action is not None and self.slot is not None:
                    try:
                        ad.action_slot = self.slot
                    except Exception:
                        pass
        scene.frame_set(self.frame, subframe=self.subframe)
        bpy.context.view_layer.update()
        for obj in scene.objects:  # what the timeline now shows differs from the snapshot = it was not keyed
            snap = self.objects.get(obj.name)
            if snap is not None:
                if _differs(obj, snap):
                    self.unkeyed.append(obj.name)
                _apply_channels(obj, snap)
            if obj.type == "ARMATURE" and obj.pose is not None:
                for pb in obj.pose.bones:
                    snap = self.bones.get((obj.name, pb.name))
                    if snap is not None:
                        if _differs(pb, snap):
                            self.unkeyed.append("%s:%s" % (obj.name, pb.name))
                        _apply_channels(pb, snap)
        bpy.context.view_layer.update()


# ----------------------------------------------------------------------------------------------------------------------
# main
# ----------------------------------------------------------------------------------------------------------------------
def run(cfg):
    report, warnings, notes = [], [], []
    if cfg["hand"] not in HANDS:
        raise UserError("hand must be 'fretting' or 'strumming', not %r" % cfg["hand"])
    if cfg["source"] not in ("auto", "markers", "actions", "current"):
        raise UserError("source must be auto, markers, actions or current, not %r" % cfg["source"])
    arm = find_armature(cfg["armature"])
    if arm.mode == "EDIT":
        raise UserError("the armature is in Edit Mode: switch to Pose Mode or Object Mode first (mode menu at the top left of the 3D viewport)")
    check_rig(arm)
    if arm.data.pose_position == "REST":
        warnings.append("the armature's Pose Position is 'Rest Position', so every pose exports as the bind pose; switch it back to 'Pose Position'")
    unit_scale = bpy.context.scene.unit_settings.scale_length
    if abs(unit_scale - 1.0) > 1e-6:
        warnings.append("the scene's Unit Scale is %g, not 1.0: this exporter and the neck mock-up take 1 Blender unit as 1 m" % unit_scale)
    neck = bpy.data.objects.get(cfg["neck"]) if cfg["neck"] else None
    if neck is None:
        warnings.append("no object named %r: using the world frame and skipping the fingertip check" % cfg["neck"])

    out_dir = cfg["out_dir"]
    if not out_dir:
        root = find_repo_root()
        if root is None:
            raise UserError("cannot find the repo (a folder holding unity/Assets/StreamingAssets): set CONFIG['out_dir'] or --out-dir")
        out_dir = os.path.join(root, "unity", "Assets", "StreamingAssets", "poses")
    out_dir = os.path.abspath(out_dir)

    jobs, source = collect_jobs(arm, cfg, notes)
    seen = {}
    for j in jobs:
        key = (j.hand, j.name)
        if key in seen:
            raise UserError("two poses would both be written to %s-%s.pose.json (%r and %r)" % (j.hand, j.name, seen[key], j.label))
        seen[key] = j.label

    source_blend = os.path.basename(bpy.data.filepath) if bpy.data.filepath else "(unsaved .blend)"
    results = []  # (job, text, fails, worst, lines, warns, fk_worst, pose)
    guard = SceneGuard(arm) if source in ("markers", "actions") else None
    try:
        for j in jobs:
            warns = []
            if guard is not None:
                if j.action is not None:
                    guard.start_from_screen()
                    guard.assign_action(j.action)
                    keyed = action_bone_paths(j.action)
                    loose = [n for n in BONE_NAMES if not any(p.startswith('pose.bones["%s"].rotation' % n) for p in keyed)]
                    if loose:
                        warns.append("action %r has no rotation keys for %s: exported as on screen" % (j.action.name, ", ".join(loose)))
                guard.go_to(j.frame)
            pose = read_pose(arm, neck, warns)
            text = build_json(j.hand, j.name, pose, source_blend)
            fk_tips, fk_worst = check_json(text, j.hand, j.name)
            if fk_worst > FK_LIMIT_MM:
                raise UserError("%s-%s: Unity would put a fingertip %.2f mm from where Blender has it (limit %.1f mm), because only rotations are exported. %s" % (
                    j.hand, j.name, fk_worst, FK_LIMIT_MM, " ".join(w for w in warns if "rotations alone" in w) or "Check for scaled or stretched bones."))
            lines, fails, worst, checked = target_report(j.hand, j.name, pose, fk_tips, neck, cfg["tolerance_mm"], warns)
            if cfg["strict"] and j.hand == "fretting" and j.name in CHORD_POSES and checked == 0:
                raise UserError("--strict: %s-%s has no fingertip targets to be checked against (tip_%s_f* from build_neck_mockup.py, built with --chords including %s)" % (j.hand, j.name, j.name, j.name))
            results.append((j, text, fails, worst, lines, warns, fk_worst, pose))
    finally:
        if guard is not None:
            guard.restore()
    if guard is not None and guard.unkeyed:
        shown = ", ".join(guard.unkeyed[:8]) + (" ..." if len(guard.unkeyed) > 8 else "")
        warnings.append("unkeyed changes at frame %d (%s) are back on screen, but they are NOT in these files: key them, or use --source current to export what you see" % (guard.frame, shown))
    for i, first in enumerate(results):  # two files with the same numbers: a pose that was never keyed at its marker
        for second in results[i + 1:]:
            pa, pb = first[7], second[7]
            same = (pa["root_mm"] - pb["root_mm"]).length < 0.001 and all(
                abs(x - y) < 1e-6 for qa, qb in zip(pa["rotations"], pb["rotations"]) for x, y in zip(qa, qb))
            if same:
                warnings.append("%s-%s and %s-%s are exactly the same pose: was each one keyed at its own frame? Unkeyed changes are lost when the timeline moves" % (
                    first[0].hand, first[0].name, second[0].hand, second[0].name))

    total_fail = sum(r[2] for r in results)
    if cfg["strict"] and total_fail:
        detail = "; ".join("%s-%s %.2f mm" % (r[0].hand, r[0].name, r[3]) for r in results if r[2])
        raise UserError("--strict: %d fingertip(s) farther than %.1f mm from their dot (%s); nothing was written" % (total_fail, cfg["tolerance_mm"], detail))
    if not cfg["dry_run"]:
        os.makedirs(out_dir, exist_ok=True)
    written = []
    for j, text, fails, worst, lines, warns, fk_worst, _pose in results:
        path = os.path.join(out_dir, "%s-%s.pose.json" % (j.hand, j.name))
        if cfg["dry_run"]:
            report.append("--- %s (dry run) ---\n%s" % (path, text))
        else:
            write_atomically(path, text)
            written.append(path)
        head = "%s-%s: " % (j.hand, j.name)
        head += ("worst fingertip %.2f mm from its dot" % worst) if lines else "written (no fingertip targets for this pose)"
        report.append(head + "; rotations rebuild the fingertips to %.3f mm" % fk_worst)
        report.extend(lines)
        for w in warns:
            warnings.append("%s-%s: %s" % (j.hand, j.name, w))
    return {"written": written, "report": report, "warnings": warnings, "notes": notes, "out_dir": out_dir, "fails": total_fail, "count": len(results)}


def show_popup(title, lines, icon="INFO"):
    """In the GUI the terminal is usually not visible, so also show the result in a message box."""
    if bpy.app.background:
        return
    try:
        def draw(self, _context):
            for line in lines[:26]:
                self.layout.label(text=line[:110])
        bpy.context.window_manager.popup_menu(draw, title=title, icon=icon)
    except Exception:
        pass


def parse_cli(argv):
    ap = argparse.ArgumentParser(prog="export_poses.py", description="Export ghost-hand poses to Pose JSON v1 (see the file header).")
    ap.add_argument("--hand", choices=HANDS)
    ap.add_argument("--source", choices=("auto", "markers", "actions", "current"))
    ap.add_argument("--name")
    ap.add_argument("--armature")
    ap.add_argument("--neck")
    ap.add_argument("--out-dir")
    ap.add_argument("--tolerance-mm", type=float)
    ap.add_argument("--strict", action="store_true")
    ap.add_argument("--dry-run", action="store_true")
    a = ap.parse_args(argv)
    cfg = dict(CONFIG)
    for key in ("hand", "source", "name", "armature", "neck", "out_dir", "tolerance_mm"):
        v = getattr(a, key)
        if v is not None:
            cfg[key] = v
    cfg["strict"] = cfg["strict"] or a.strict
    cfg["dry_run"] = cfg["dry_run"] or a.dry_run
    return cfg


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    cfg = parse_cli(argv)
    try:
        result = run(cfg)
    except UserError as e:
        print("export_poses.py: ERROR: %s" % e, file=sys.stderr)
        show_popup("Pose export FAILED", [str(e)], icon="ERROR")
        if bpy.app.background:
            sys.exit(1)
        return
    lines = list(result["notes"]) + result["report"]
    for w in result["warnings"]:
        lines.append("warning: " + w)
    lines.append("%d pose file(s) %s %s" % (result["count"], "would go to" if cfg["dry_run"] else "written to", result["out_dir"]))
    print("\n".join(lines))
    show_popup("Pose export: %d file(s)%s" % (result["count"], ", %d fingertip(s) too far" % result["fails"] if result["fails"] else ""),
               lines, icon="ERROR" if result["fails"] else "INFO")


if __name__ == "__main__":
    main()
