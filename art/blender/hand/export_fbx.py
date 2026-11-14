"""
export_fbx.py - export both ghost hands from ghost_hands.blend to FBX for Unity (bind pose, mesh + 16-bone armature),
then check the two files twice: read each FBX's node tree the way Unity's importer will see it, and re-import each FBX
into an empty Blender scene and compare it with the source rig. Headless; nothing is saved back into the .blend.

USAGE (Blender 5.2; options after the "--"; --python-exit-code must come before --python):

  /Applications/Blender.app/Contents/MacOS/Blender --factory-startup -b --python-exit-code 1 \
      --python art/blender/hand/export_fbx.py -- [options]

  --blend PATH     the posed hands, default: ghost_hands.blend next to this script
  --out-dir DIR    default: <repo>/unity/Assets/UntitledGuitarApp/Art/Resources/GhostHand
                   (Unity: Resources.Load<GameObject>("GhostHand/ghost_hand_fretting") and ".../ghost_hand_strumming")
  --poses DIR      pose files whose bind block the FBX must match, default <repo>/unity/Assets/StreamingAssets/poses
  --check-only     export nothing; check the FBX files already in --out-dir against the .blend
  --vertex-colours none|shader   default none: the FBX carries no vertex colours. shader: one colour layer in the
                   ghost shader's convention (see VERTEX DATA). The .blend's FingerMask colour is never exported.

Exit code 1 (with --python-exit-code 1) when anything does not match. Writes no .meta files (Unity makes those).

WHAT THE FILES HOLD (one per hand: ghost_hand_fretting.fbx = GhostHand_Fretting, the player's LEFT hand;
ghost_hand_strumming.fbx = GhostHand_Strumming, the RIGHT hand)
  * the armature with exactly the 16 pinned bones (wrist; thumb|index|middle|ring|pinky x proximal|intermediate|distal),
    in the BIND (rest) pose: actions are dropped and every pose bone is reset first. No leaf ("_end") bones, so a
    fingertip (the tail of a *_distal bone) is not a node: take it from the pose files' bind.fingertips;
  * ONE skinned mesh in one connected piece (5,376 triangles, at most 4 bones per vertex) with ONE material slot,
    "GhostHand": overlapping separate parts would add up into bright seams on the additive display.

THE FRAME UNITY GETS (the reason for everything in prepare_hand below)
  The pose files measure rotations in the hand's MODEL space: the armature's own axes with the axis rule
  (x, y, z)_unity = (X, Z, Y)_blender, in metres here (README of the poses folder, section 3). This script makes
  that frame the frame of every imported GameObject above the bones, at 1 Unity unit = 1 m:
      prefab root, armature GameObject and mesh GameObject all have the identity transform, and each bone sits where
      bind.joints says (+x = the little-finger side of the right hand, +y = back of the hand, +z = fingers).
  How: Blender's FBX exporter writes the standard FBX Y-up file (axis_forward -Z, axis_up Y) and folds that axis
  change into the root nodes' transforms; Unity's importer then only mirrors x (see "FBX settings" in README.md). So
  the rig is re-expressed in the export session with the rotation G: (X, Y, Z) -> (-X, Z, Y) (bones and mesh data),
  and the armature object gets the inverse of the exporter's axis rotation. After the exporter (rotation) and Unity
  (x -> -x) the data lands on (X, Z, Y) with nothing left on the root transforms.

VERTEX DATA (Runtime/Resources/Shaders/GhostHand.shader reads vertex colour as control data: tip = r - g, fade = b - g)
  colour   default: none. The .blend's FingerMask attribute (one finger per channel) would light the index finger
           and hide the ring finger, so it never goes into the colour. With --vertex-colours shader: one layer,
           written raw (linear): r = fingertip glow (GhostMask u), g = 0, b = 1 - GhostMask v (1 at the end of the
           wrist stub), a = 1.
  UV0 'GhostMask'      (u = fingertip glow, v = 1 on the hand -> 0 at the stub end), as in the .blend
  UV1 'FingerMask_IM'  (u = index, v = middle)   per-finger masks, 1 on that finger, for a finger-by-finger shader
  UV2 'FingerMask_RP'  (u = ring,  v = pinky)    (the .blend's FingerMask colour attribute, moved out of the colour)
"""

import argparse
import hashlib
import math
import os
import struct
import sys
import time
import zlib

import bpy
from bpy_extras.io_utils import axis_conversion
from mathutils import Matrix, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import build_hand_rig as rig  # noqa: E402  (the 16 canonical bone names and parents)

SCRIPT_VERSION = "1.1.0"  # 1.1.0: no vertex colours by default (--vertex-colours shader for the shader's convention)
HANDS = (("fretting", "GhostHand_Fretting"), ("strumming", "GhostHand_Strumming"))
FBX_NAME = "ghost_hand_%s.fbx"
MAX_BYTES = 2 * 1024 * 1024
MM = 1000.0                              # metres -> millimetres

# The chosen Blender FBX exporter settings (README.md "FBX settings" says why, with sources). Every other option of
# bpy.ops.export_scene.fbx is left at its default.
AXIS_FORWARD, AXIS_UP = "-Z", "Y"        # the standard FBX/Maya Y-up file: UpAxis +Y, FrontAxis +Z, CoordAxis +X
FBX_OPTIONS = dict(
    use_selection=True, use_visible=False, use_active_collection=False, collection="",
    object_types={"ARMATURE", "MESH"},
    global_scale=1.0, apply_unit_scale=True, apply_scale_options="FBX_SCALE_ALL",   # UnitScaleFactor 100 = metres
    axis_forward=AXIS_FORWARD, axis_up=AXIS_UP, use_space_transform=True,
    bake_space_transform=False,          # "Apply Transform": documented as broken with armatures
    use_mesh_modifiers=False,            # the mesh as stored = its bind shape (the Armature modifier is never applied)
    mesh_smooth_type="FACE", use_subsurf=False, use_mesh_edges=False, use_tspace=False, use_triangles=False,
    colors_type="NONE", prioritize_active_color=False,       # --vertex-colours shader sets LINEAR (values as stored)
    use_custom_props=False,
    add_leaf_bones=False, primary_bone_axis="Y", secondary_bone_axis="X",
    use_armature_deform_only=True, armature_nodetype="NULL",
    bake_anim=False,                     # poses come from the pose files, never from the FBX
    path_mode="AUTO", embed_textures=False, batch_mode="OFF",
)

# G re-expresses the hand: (X, Y, Z) -> (-X, Z, Y). A proper rotation (180 degrees about (0, 1, 1)), so no mirroring.
G = Matrix(((-1.0, 0.0, 0.0, 0.0), (0.0, 0.0, 1.0, 0.0), (0.0, 1.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0)))
# The exporter's axis rotation (Blender -> FBX for -Z forward, Y up); the armature object gets its inverse.
AXIS = axis_conversion(to_forward=AXIS_FORWARD, to_up=AXIS_UP).to_4x4()
AXIS_FIX = AXIS.inverted()
# In Blender world space the exported hand is turned by AXIS_FIX @ G (= 180 degrees about Z); re-imports show that.
EXPORT_TURN = AXIS_FIX @ G
# The pose files' axis rule, armature space (Blender) -> model space (Unity): (x, y, z) = (X, Z, Y). Det -1.
C3 = Matrix(((1.0, 0.0, 0.0), (0.0, 0.0, 1.0), (0.0, 1.0, 0.0)))
M3 = Matrix(((-1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0)))   # Unity's importer: x -> -x

POS_TOL_MM = 0.001                       # FBX / re-import vs the source rig
JSON_TOL_MM = 0.002                      # vs the pose files (they round to 0.001 mm)
ANGLE_TOL_DEG = 0.01


class ExportError(Exception):
    pass


def say(text=""):
    print(text)
    sys.stdout.flush()


# ----------------------------------------------------------------------------------------------------------------------
# paths and the source rig
# ----------------------------------------------------------------------------------------------------------------------
def repo_root():
    here = os.path.dirname(os.path.abspath(__file__))           # <repo>/art/blender/hand
    root = os.path.dirname(os.path.dirname(os.path.dirname(here)))
    if not os.path.isdir(os.path.join(root, "unity", "Assets")):
        raise ExportError("cannot find the repo (no unity/Assets above %s): pass --out-dir and --poses" % here)
    return root


def hand_objects(arm_name):
    arm = bpy.data.objects.get(arm_name)
    if arm is None or arm.type != "ARMATURE":
        raise ExportError("no armature object %r in %s" % (arm_name, bpy.data.filepath))
    meshes = [ob for ob in bpy.data.objects if ob.type == "MESH" and ob.parent is arm
              and any(m.type == "ARMATURE" and m.object is arm for m in ob.modifiers)]
    if len(meshes) != 1:
        raise ExportError("%s must have exactly one skinned mesh child, found %d" % (arm_name, len(meshes)))
    return arm, meshes[0]


def capture(arm, mesh):
    """Everything the checks compare against, from the .blend as saved (rest data; the current pose plays no part)."""
    bones = arm.data.bones
    names = [b.name for b in bones]
    if sorted(names) != sorted(rig.BONE_NAMES):
        raise ExportError("%s must have exactly the 16 pinned bones; it has %s" % (arm.name, ", ".join(names)))
    for name, parent in rig.BONES:
        have = bones[name].parent.name if bones[name].parent else None
        if have != parent:
            raise ExportError("%s: bone %s has parent %s, the pinned hierarchy says %s" % (arm.name, name, have, parent))
        if not bones[name].use_deform:
            raise ExportError("%s: bone %s is not a deform bone (Only Deform Bones would drop it)" % (arm.name, name))
    rel = arm.matrix_world.inverted() @ mesh.matrix_world
    if max(abs(rel[i][j] - (1.0 if i == j else 0.0)) for i in range(4) for j in range(4)) > 1e-6:
        raise ExportError("%s must sit on its armature's origin with no rotation or scale (as build_hand_rig.py makes it)" % mesh.name)
    me = mesh.data
    if "GhostMask" not in me.uv_layers or "FingerMask" not in me.color_attributes:
        raise ExportError("%s needs the UV map 'GhostMask' and the colour attribute 'FingerMask' (build_hand_rig.py)" % mesh.name)
    groups = {g.index: g.name for g in mesh.vertex_groups}
    weights = []
    for v in me.vertices:
        weights.append({groups[g.group]: g.weight for g in v.groups if g.weight > 0.0})
    if max(len(w) for w in weights) > 4:
        raise ExportError("%s has vertices with more than 4 bone weights (Unity's default skin weights: 4)" % mesh.name)
    gm = me.uv_layers["GhostMask"].data
    fm = me.color_attributes["FingerMask"].data
    return dict(
        arm_name=arm.name, mesh_name=mesh.name,
        parent={b.name: (b.parent.name if b.parent else None) for b in bones},
        head={b.name: b.head_local.copy() for b in bones},
        tail={b.name: b.tail_local.copy() for b in bones},
        rest={b.name: b.matrix_local.copy() for b in bones},
        verts=[v.co.copy() for v in me.vertices],
        polys=len(me.polygons), tris=sum(len(p.vertices) - 2 for p in me.polygons), loops=len(me.loops),
        weights=weights,
        ghost=[tuple(d.uv) for d in gm],
        finger=[tuple(d.color) for d in fm],
        materials=[m.name if m else None for m in me.materials],
    )


# ----------------------------------------------------------------------------------------------------------------------
# export
# ----------------------------------------------------------------------------------------------------------------------
def prepare_hand(arm, mesh, src, colours="none"):
    """Put this hand in its bind pose, in the export frame, with its vertex data. Changes the open scene only."""
    if arm.animation_data is not None:
        arm.animation_data_clear()                     # no action may pull the pose back
    for pb in arm.pose.bones:
        pb.rotation_mode = "QUATERNION"
        pb.location = (0.0, 0.0, 0.0)
        pb.rotation_quaternion = (1.0, 0.0, 0.0, 0.0)
        pb.scale = (1.0, 1.0, 1.0)
        for con in pb.constraints:
            con.mute = True
    arm.data.pose_position = "POSE"
    arm.parent = None
    arm.matrix_world = Matrix.Identity(4)
    arm.data.transform(G)                              # bones re-expressed: (X, Y, Z) -> (-X, Z, Y)
    mesh.data.transform(G)
    mesh.data.update()
    for name, rest in src["rest"].items():             # the bones must have turned with G exactly (roll included)
        want, have = G @ rest, arm.data.bones[name].matrix_local
        err = max(abs(want[i][j] - have[i][j]) for i in range(4) for j in range(4))
        if err > 1e-5:
            raise ExportError("%s: bone %s did not follow the re-expression (%.2e)" % (arm.name, name, err))
    arm.matrix_world = AXIS_FIX

    me = mesh.data
    for ca in [a.name for a in me.color_attributes]:   # FingerMask leaves the colour in every mode (it moves to UVs)
        me.color_attributes.remove(me.color_attributes[ca])
    for name, pick in (("FingerMask_IM", (0, 1)), ("FingerMask_RP", (2, 3))):
        layer = me.uv_layers.new(name=name, do_init=False)
        layer.data.foreach_set("uv", [c[k] for c in src["finger"] for k in pick])
    if colours == "shader":
        col = me.color_attributes.new(name="GhostColor", type="FLOAT_COLOR", domain="CORNER")
        flat = []
        for u, v in src["ghost"]:
            flat.extend((u, 0.0, 1.0 - v, 1.0))
        col.data.foreach_set("color", flat)
        if len(col.data) != len(me.loops):
            raise ExportError("internal: colour layer size")
    me.update()
    bpy.context.view_layer.update()


def scrub_header(elem_root, blend_name):
    """The exporter writes the absolute path of the open .blend and the export time into the FBX header. Keep the
    file name only and a fixed time, so the file holds no local path and the same input gives the same bytes."""
    done = []
    for elem in elem_root.elems:
        if elem.id != b"FBXHeaderExtension":
            continue
        for sub in elem.elems:
            if sub.id == b"CreationTimeStamp":
                for field in sub.elems:
                    if field.id in (b"Year", b"Month", b"Day", b"Hour", b"Minute", b"Second", b"Millisecond"):
                        value = {b"Year": 2026, b"Month": 1, b"Day": 1}.get(field.id, 0)
                        field.props[0] = struct.pack("<i", value)
                        done.append(field.id.decode())
            if sub.id == b"SceneInfo":
                for props in sub.elems:
                    if props.id != b"Properties70":
                        continue
                    for p in props.elems:
                        if p.props and p.props[0][4:] == b"Original|ApplicationNativeFile":
                            name = blend_name.encode("utf-8")
                            p.props[-1] = struct.pack("<I", len(name)) + name
                            done.append("ApplicationNativeFile")
    return done


def export_hand(arm, mesh, path, blend_name, colours="none"):
    options = dict(FBX_OPTIONS)
    options["colors_type"] = "LINEAR" if colours == "shader" else "NONE"
    for ob in bpy.context.view_layer.objects:
        ob.select_set(False)
    for ob in (arm, mesh):
        ob.hide_set(False)
        ob.hide_viewport = False
        ob.hide_select = False
        ob.select_set(True)
    bpy.context.view_layer.objects.active = arm
    # Two small wrappers around the exporter, removed again right after: (1) scrub_header before the file is encoded;
    # (2) object ids from a stable hash of the exporter's own keys. The exporter uses Python's hash(), which changes in
    # every Blender session, so without this the same .blend gives different bytes on every run (a git change each time).
    from io_scene_fbx import encode_bin, fbx_utils
    original_write, original_uuid = encode_bin.write, fbx_utils._key_to_uuid
    scrubbed = []

    def write(fn, elem_root, version):
        scrubbed.extend(scrub_header(elem_root, blend_name))
        return original_write(fn, elem_root, version)

    def stable_uuid(uuids, key):
        if not (isinstance(key, int) and 0 <= key < 2 ** 63):
            key = int.from_bytes(hashlib.blake2b(repr(key).encode("utf-8"), digest_size=8).digest(), "little") & (2 ** 63 - 1)
        return original_uuid(uuids, key)

    encode_bin.write, fbx_utils._key_to_uuid = write, stable_uuid
    try:
        result = bpy.ops.export_scene.fbx(filepath=path, check_existing=False, **options)
    finally:
        encode_bin.write, fbx_utils._key_to_uuid = original_write, original_uuid
    if "FINISHED" not in result:
        raise ExportError("the FBX exporter returned %s for %s" % (result, path))
    if "ApplicationNativeFile" not in scrubbed:
        say("  warning: could not take the local .blend path out of the FBX header (exporter changed?)")


# ----------------------------------------------------------------------------------------------------------------------
# check 1: the FBX as Unity reads it (own reader; shares no code with Blender's FBX importer)
# ----------------------------------------------------------------------------------------------------------------------
class Node(object):
    __slots__ = ("name", "props", "children")

    def __init__(self, name, props, children):
        self.name, self.props, self.children = name, props, children

    def first(self, name):
        for c in self.children:
            if c.name == name:
                return c
        return None

    def all(self, name):
        return [c for c in self.children if c.name == name]


def read_fbx(path):
    """Binary FBX 7.x -> a Node tree. Format: Blender's io_scene_fbx/encode_bin.py and parse_fbx.py describe it."""
    data = open(path, "rb").read()
    if not data.startswith(b"Kaydara FBX Binary  \x00"):
        raise ExportError("%s is not a binary FBX" % path)
    version = struct.unpack_from("<I", data, 23)[0]
    wide = version >= 7500

    def node_at(pos):
        if wide:
            end, count, _plen = struct.unpack_from("<QQQ", data, pos)
            pos += 24
        else:
            end, count, _plen = struct.unpack_from("<III", data, pos)
            pos += 12
        nlen = data[pos]
        pos += 1
        if end == 0:
            return None, pos
        name = data[pos:pos + nlen].decode("ascii")
        pos += nlen
        props = []
        for _ in range(count):
            t = chr(data[pos])
            pos += 1
            if t in "YCIFDL":
                fmt, size = {"Y": ("<h", 2), "C": ("<?", 1), "I": ("<i", 4), "F": ("<f", 4), "D": ("<d", 8), "L": ("<q", 8)}[t]
                props.append(struct.unpack_from(fmt, data, pos)[0])
                pos += size
            elif t in "fdlib":
                n, enc, clen = struct.unpack_from("<III", data, pos)
                pos += 12
                raw = data[pos:pos + clen]
                pos += clen
                if enc == 1:
                    raw = zlib.decompress(raw)
                props.append(list(struct.unpack("<%d%s" % (n, {"f": "f", "d": "d", "l": "q", "i": "i", "b": "b"}[t]), raw)))
            elif t in "SR":
                n = struct.unpack_from("<I", data, pos)[0]
                pos += 4
                props.append(data[pos:pos + n])
                pos += n
            else:
                raise ExportError("%s: unknown FBX property type %r" % (path, t))
        children = []
        while pos < end:
            child, pos = node_at(pos)
            if child is None:
                break
            children.append(child)
        return Node(name, props, children), end

    top, pos = [], 27
    while True:
        node, pos = node_at(pos)
        if node is None:
            break
        top.append(node)
    return version, Node("", [], top)


def p70(node):
    """A node's Properties70 as {name: [values]}."""
    out = {}
    block = node.first("Properties70") if node is not None else None
    for p in (block.children if block is not None else []):
        out[p.props[0].decode("utf-8", "replace")] = p.props[4:]
    return out


def fbx_name(raw):
    return raw.split(b"\x00\x01")[0].decode("utf-8")


def euler_xyz(deg):
    """FBX eEulerXYZ: rotate about X, then Y, then Z (R = Rz Ry Rx)."""
    rx, ry, rz = (math.radians(a) for a in deg)
    return (Matrix.Rotation(rz, 3, "Z") @ Matrix.Rotation(ry, 3, "Y") @ Matrix.Rotation(rx, 3, "X"))


def local_matrix(props):
    for key in ("RotationOffset", "RotationPivot", "ScalingOffset", "ScalingPivot", "PostRotation", "GeometricTranslation",
                "GeometricRotation", "GeometricScaling"):
        if key in props:
            raise ExportError("FBX node uses %s, which this check does not model" % key)
    if int((props.get("RotationOrder") or [0])[0]) != 0:
        raise ExportError("FBX node uses a rotation order other than XYZ")
    t = Vector(props.get("Lcl Translation", [0.0, 0.0, 0.0]))
    r = euler_xyz(props.get("Lcl Rotation", [0.0, 0.0, 0.0]))
    if "PreRotation" in props:
        r = euler_xyz(props["PreRotation"]) @ r
    s = props.get("Lcl Scaling", [1.0, 1.0, 1.0])
    m = (r @ Matrix.Diagonal(Vector(s))).to_4x4()
    m.translation = t
    return m


def unity_euler(r):
    """UnityEngine.Quaternion.eulerAngles for a rotation matrix: Unity applies Z, then X, then Y (R = Ry Rx Rz)."""
    x = math.degrees(math.asin(max(-1.0, min(1.0, -r[1][2]))))
    y = math.degrees(math.atan2(r[0][2], r[2][2]))
    z = math.degrees(math.atan2(r[1][0], r[1][1]))
    return tuple((a + 360.0) % 360.0 if abs(a) > 1e-7 else 0.0 for a in (x, y, z))


def unity_view(path, src, hand, poses_dir, colour_mode="none"):
    """Simulate Unity 2021.3's FBX import with default settings (README.md "The hierarchy Unity sees") and compare."""
    say("  -- as Unity will import it (simulated from the FBX data; Unity itself was not run) --")
    version, root = read_fbx(path)
    gs = p70(root.first("GlobalSettings"))
    axes = tuple(int(gs[k][0]) for k in ("UpAxis", "UpAxisSign", "FrontAxis", "FrontAxisSign", "CoordAxis", "CoordAxisSign"))
    unit_cm = float(gs["UnitScaleFactor"][0])
    file_scale = unit_cm / 100.0                       # ModelImporter.fileScale: FBX unit -> metres
    def axis(i):
        return ("+" if axes[2 * i + 1] > 0 else "-") + "XYZ"[axes[2 * i]]
    say("  FBX %d; GlobalSettings Up %s, Front %s, Coord %s (%s); UnitScaleFactor %g cm -> Unity fileScale %g" % (
        version, axis(0), axis(1), axis(2),
        "standard Y-up: Unity only mirrors x" if axes == (1, 1, 2, 1, 0, 1) else "NOT the standard Y-up system", unit_cm, file_scale))
    problems = []
    if axes != (1, 1, 2, 1, 0, 1):
        problems.append("axis system is not the standard Y-up one")
    if abs(file_scale - 1.0) > 1e-9:
        problems.append("UnitScaleFactor %g: 1 FBX unit is not 1 m" % unit_cm)

    objects = root.first("Objects")
    models = {m.props[0]: (fbx_name(m.props[1]), m.props[2].decode(), p70(m)) for m in objects.all("Model")}
    parent_of, children = {}, {}
    for c in root.first("Connections").all("C"):
        if c.props[0] == b"OO" and c.props[1] in models and (c.props[2] in models or c.props[2] == 0):
            parent_of[c.props[1]] = c.props[2]
            children.setdefault(c.props[2], []).append(c.props[1])
    tops = sorted(children.get(0, []), key=lambda u: models[u][0])
    world = {}

    def walk(uid, parent_world):
        world[uid] = parent_world @ local_matrix(models[uid][2])
        for kid in children.get(uid, []):
            walk(kid, world[uid])
    for uid in tops:
        walk(uid, Matrix.Identity(4))

    geoms = objects.all("Geometry")
    skins = [d for d in objects.all("Deformer") if d.props[2] == b"Skin"]
    clusters = [d for d in objects.all("Deformer") if d.props[2] == b"Cluster"]
    stacks = objects.all("AnimationStack")
    file_name = os.path.splitext(os.path.basename(path))[0]
    root_note = ("made by Unity, named after the file: the FBX has %d top-level nodes" % len(tops)) if len(tops) > 1 else \
        "the single top-level node is reused as the prefab root (Preserve Hierarchy off)"
    say("  hierarchy (children sorted by name, Unity's default; transforms are Unity localPosition m / localEulerAngles deg):")
    say("    %s   [prefab root, %s]" % (file_name, root_note))

    def show(uid, depth):
        name, kind, _props = models[uid]
        loc = world[uid] if parent_of.get(uid, 0) == 0 else world[parent_of[uid]].inverted() @ world[uid]
        t = M3 @ (loc.translation * file_scale)
        r = M3 @ loc.to_3x3().normalized() @ M3
        s = loc.to_scale()
        e = unity_euler(r)
        extra = {"Null": "empty GameObject (the armature)", "LimbNode": "bone",
                 "Mesh": "SkinnedMeshRenderer (%d skin clusters)" % len(clusters)}.get(kind, kind)
        say("    %s%-22s %-34s pos (%8.5f, %8.5f, %8.5f)  rot (%6.2f, %6.2f, %6.2f)%s" % (
            "   " * depth, name, "[" + extra + "]", t.x, t.y, t.z, e[0], e[1], e[2],
            "" if max(abs(c - 1.0) for c in s) < 1e-6 else "  scale (%.4f, %.4f, %.4f)" % tuple(s)))
        for kid in sorted(children.get(uid, []), key=lambda u: models[u][0]):
            show(kid, depth + 1)
    for uid in tops:
        show(uid, 1)

    limbs = {models[u][0]: u for u in models if models[u][1] == "LimbNode"}
    nulls = [u for u in models if models[u][1] == "Null"]
    meshes = [u for u in models if models[u][1] == "Mesh"]
    if sorted(limbs) != sorted(rig.BONE_NAMES) or len(limbs) != len(rig.BONE_NAMES):
        problems.append("bone nodes are %s, not the 16 pinned names" % sorted(limbs))
    for name, parent in rig.BONES:
        if name in limbs:
            have = models.get(parent_of.get(limbs[name], 0), ("(none)",))[0]
            want = parent if parent else src["arm_name"]
            if have != want:
                problems.append("bone %s hangs under %s, expected %s" % (name, have, want))
    if len(tops) != 2 or len(nulls) != 1 or len(meshes) != 1:
        problems.append("expected 2 top-level nodes (armature, mesh), 1 Null and 1 Mesh; found %d, %d, %d" % (len(tops), len(nulls), len(meshes)))
    for uid in nulls + meshes:                          # the frame above the bones must be the identity
        w = world[uid]
        off = max(abs(w[i][j] - (1.0 if i == j else 0.0)) for i in range(4) for j in range(4))
        if off > 1e-6:
            problems.append("%s does not have the identity transform (%.2e)" % (models[uid][0], off))
    if len(skins) != 1 or len(clusters) != 16:
        problems.append("expected 1 skin with 16 clusters, found %d and %d" % (len(skins), len(clusters)))
    if stacks:
        problems.append("the file has %d animation stack(s); it should hold the bind pose only" % len(stacks))

    # bones: Unity position and rotation in the prefab root's space, vs the source rig through the pose files' axis rule
    worst_p, worst_r = 0.0, 0.0
    unity_pos = {}
    for name, uid in limbs.items():
        if name not in src["head"]:                    # an extra node (e.g. a leaf "_end" bone): reported above
            continue
        w = world[uid]
        p = M3 @ (w.translation * file_scale)
        r = M3 @ w.to_3x3().normalized() @ M3
        unity_pos[name] = p
        want_p = C3 @ src["head"][name]
        want_r = C3 @ src["rest"][name].to_3x3().normalized() @ M3   # Blender bone axes, mirrored by Unity, in model axes
        worst_p = max(worst_p, (p - want_p).length * MM)
        worst_r = max(worst_r, math.degrees((want_r.to_quaternion().rotation_difference(r.to_quaternion())).angle))
    say("  bones vs the source rig (model axes): worst position %.4f mm, worst orientation %.4f deg" % (worst_p, worst_r))
    if worst_p > POS_TOL_MM or worst_r > ANGLE_TOL_DEG:
        problems.append("bones are off the source rig (%.4f mm, %.4f deg)" % (worst_p, worst_r))

    # mesh: vertices, triangles, colours, UVs
    geo = geoms[0] if len(geoms) == 1 else None
    if geo is None:
        problems.append("expected 1 geometry, found %d" % len(geoms))
    else:
        mesh_world = world[meshes[0]] if meshes else Matrix.Identity(4)
        co = geo.first("Vertices").props[0]
        pvi = geo.first("PolygonVertexIndex").props[0]
        polys = sum(1 for i in pvi if i < 0)
        tris, count = 0, 0
        for i in pvi:
            count += 1
            if i < 0:
                tris += count - 2
                count = 0
        pts = [M3 @ ((mesh_world @ Vector(co[k:k + 3])) * file_scale) for k in range(0, len(co), 3)]
        lo = [min(p[i] for p in pts) for i in range(3)]
        hi = [max(p[i] for p in pts) for i in range(3)]
        worst_v = max((p - C3 @ v).length for p, v in zip(pts, src["verts"])) * MM if len(pts) == len(src["verts"]) else float("inf")
        say("  mesh: %d vertices, %d polygons -> %d triangles; bounds in Unity metres x [%.4f, %.4f] y [%.4f, %.4f] z [%.4f, %.4f]" % (
            len(pts), polys, tris, lo[0], hi[0], lo[1], hi[1], lo[2], hi[2]))
        say("        vertices vs the source mesh (model axes): worst %.4f mm" % worst_v)
        if len(pts) != len(src["verts"]) or tris != src["tris"] or worst_v > POS_TOL_MM:
            problems.append("mesh differs from the source (%d/%d vertices, %d/%d triangles, %.4f mm)" % (
                len(pts), len(src["verts"]), tris, src["tris"], worst_v))
        # one piece: overlapping separate parts would add up into bright seams on the additive display
        nverts = len(co) // 3
        link = list(range(nverts))

        def find(a):
            while link[a] != a:
                link[a] = link[link[a]]
                a = link[a]
            return a
        poly = []
        for i in pvi:
            poly.append(i if i >= 0 else ~i)           # the last index of a polygon is stored as -(index + 1)
            if i < 0:
                for b in poly[1:]:
                    ra, rb = find(poly[0]), find(b)
                    if ra != rb:
                        link[ra] = rb
                poly = []
        pieces = len({find(v) for v in range(nverts)})
        materials = objects.all("Material")
        mat_layers = geo.all("LayerElementMaterial")
        mat_index = set()
        if mat_layers and mat_layers[0].first("Materials") is not None:
            mat_index = set(mat_layers[0].first("Materials").props[0])
        say("        %d connected piece(s); %d material(s) %s, polygon material indices %s" % (
            pieces, len(materials), [fbx_name(m.props[1]) for m in materials], sorted(mat_index)))
        if pieces != 1:
            problems.append("the mesh is %d separate pieces, not one" % pieces)
        if len(materials) != 1 or not mat_index <= {0}:
            problems.append("expected one material slot used by every polygon")

        colours = geo.all("LayerElementColor")
        uvs = geo.all("LayerElementUV")
        uv_names = [fbx_name(u.first("Name").props[0]) for u in uvs]
        say("        vertex colour layers %s (--vertex-colours %s); UV layers %s" % (
            [fbx_name(c.first("Name").props[0]) for c in colours], colour_mode, uv_names))
        want_layers = 1 if colour_mode == "shader" else 0
        if len(colours) != want_layers:
            problems.append("expected %d vertex colour layer(s) for --vertex-colours %s, found %d" % (want_layers, colour_mode, len(colours)))
        elif colours:
            table, index = colours[0].first("Colors").props[0], colours[0].first("ColorIndex").props[0]
            got = [table[4 * i:4 * i + 4] for i in index]
            want = [(u, 0.0, 1.0 - v, 1.0) for u, v in src["ghost"]]
            worst_c = max(max(abs(a - b) for a, b in zip(g, w)) for g, w in zip(got, want)) if len(got) == len(want) else float("inf")
            chans = ["%s %.3f..%.3f" % ("rgba"[k], min(g[k] for g in got), max(g[k] for g in got)) for k in range(4)]
            say("        vertex colour (r = tip glow, b = fade out): %s; vs GhostMask: worst %.2e" % (", ".join(chans), worst_c))
            if worst_c > 1e-6:
                problems.append("vertex colours do not follow the shader convention (%.2e)" % worst_c)
        if uv_names != ["GhostMask", "FingerMask_IM", "FingerMask_RP"]:
            problems.append("expected UV layers GhostMask, FingerMask_IM, FingerMask_RP, found %s" % uv_names)
        else:
            for layer, pick in ((uvs[1], (0, 1)), (uvs[2], (2, 3))):
                table, index = layer.first("UV").props[0], layer.first("UVIndex").props[0]
                worst_u = max(max(abs(table[2 * i] - f[pick[0]]), abs(table[2 * i + 1] - f[pick[1]]))
                              for i, f in zip(index, src["finger"]))
                if worst_u > 1e-6:
                    problems.append("UV %s does not carry the finger masks (%.2e)" % (fbx_name(layer.first("Name").props[0]), worst_u))

    # the pose files: their bind block must describe this very rig in this very frame
    files = sorted(f for f in os.listdir(poses_dir) if f.startswith(hand + "-") and f.endswith(".pose.json")) if os.path.isdir(poses_dir) else []
    if files:
        import json
        worst_j = 0.0
        for f in files:
            d = json.load(open(os.path.join(poses_dir, f), encoding="utf-8"))
            for j in d["bind"]["joints"]:          # joints: the FBX bones as Unity places them
                p = j["positionMm"]
                if j["bone"] not in unity_pos:
                    worst_j = float("inf")
                    continue
                worst_j = max(worst_j, (unity_pos[j["bone"]] * MM - Vector((p["x"], p["y"], p["z"]))).length)
            for t in d["bind"]["fingertips"]:      # fingertips are no FBX node (no leaf bones): the source rig's tails
                p = t["positionMm"]
                want = C3 @ src["tail"][t["bone"]] * MM
                worst_j = max(worst_j, (want - Vector((p["x"], p["y"], p["z"]))).length)
        say("  bones vs bind.joints / bind.fingertips of %d pose file(s) in %s: worst %.4f mm" % (len(files), poses_dir, worst_j))
        if worst_j > JSON_TOL_MM:
            problems.append("the FBX bind pose is %.4f mm off the pose files' bind block" % worst_j)
    else:
        say("  (no %s-*.pose.json in %s: bind block not compared)" % (hand, poses_dir))
    return problems


# ----------------------------------------------------------------------------------------------------------------------
# check 2: re-import into an empty Blender scene
# ----------------------------------------------------------------------------------------------------------------------
def reimport_check(path, src, colour_mode="none"):
    say("  -- re-imported into an empty Blender scene (bpy.ops.import_scene.fbx, defaults, colours read as linear) --")
    bpy.ops.wm.read_factory_settings(use_empty=True)
    result = bpy.ops.import_scene.fbx(filepath=path, colors_type="LINEAR")
    problems = []
    if "FINISHED" not in result:
        return ["the FBX importer returned %s" % result]
    arms = [o for o in bpy.context.scene.objects if o.type == "ARMATURE"]
    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    others = [o.name for o in bpy.context.scene.objects if o.type not in ("ARMATURE", "MESH")]
    if len(arms) != 1 or len(meshes) != 1 or others:
        return ["expected 1 armature and 1 mesh, got %d, %d and %s" % (len(arms), len(meshes), others)]
    arm, mesh = arms[0], meshes[0]
    bpy.context.view_layer.update()
    bones = arm.data.bones
    say("  objects: armature %r, mesh %r (parent %r)" % (arm.name, mesh.name, mesh.parent.name if mesh.parent else None))
    say("  bones (%d), name <- parent:" % len(bones))
    for name, _parent in rig.BONES:
        b = bones.get(name)
        say("    %-20s <- %s" % (name, (b.parent.name if b.parent else "(armature)") if b else "MISSING"))
    extra = sorted(set(b.name for b in bones) - set(rig.BONE_NAMES))
    if extra or len(bones) != 16:
        problems.append("bone count %d, extra bones %s" % (len(bones), extra))
    for name, parent in rig.BONES:
        b = bones.get(name)
        if b is None or (b.parent.name if b.parent else None) != parent:
            problems.append("bone %s missing or under the wrong parent" % name)
    worst_h, worst_pose = 0.0, 0.0
    for name in rig.BONE_NAMES:
        if name not in bones:
            continue
        got = arm.matrix_world @ bones[name].head_local
        want = EXPORT_TURN @ src["head"][name]
        worst_h = max(worst_h, (got - want).length * MM)
        basis = arm.pose.bones[name].matrix_basis
        worst_pose = max(worst_pose, basis.to_translation().length * MM, math.degrees(basis.to_quaternion().angle))
    say("  bone heads vs the source rig (turned by the export frame): worst %.4f mm; pose = rest within %.4f (mm/deg)" % (worst_h, worst_pose))
    if worst_h > POS_TOL_MM or worst_pose > 0.01:
        problems.append("bones off the source (%.4f mm) or not in the bind pose (%.4f)" % (worst_h, worst_pose))
    me = mesh.data
    tris = sum(len(p.vertices) - 2 for p in me.polygons)
    pts = [mesh.matrix_world @ v.co for v in me.vertices]
    lo = [min(p[i] for p in pts) for i in range(3)]
    hi = [max(p[i] for p in pts) for i in range(3)]
    say("  mesh: %d vertices, %d polygons, %d triangles; world bounds (Blender axes, m) x [%.4f, %.4f] y [%.4f, %.4f] z [%.4f, %.4f]" % (
        len(me.vertices), len(me.polygons), tris, lo[0], hi[0], lo[1], hi[1], lo[2], hi[2]))
    want_pts = [EXPORT_TURN @ v for v in src["verts"]]
    wlo = [min(p[i] for p in want_pts) for i in range(3)]
    whi = [max(p[i] for p in want_pts) for i in range(3)]
    say("        source, same frame:                                     x [%.4f, %.4f] y [%.4f, %.4f] z [%.4f, %.4f]" % (
        wlo[0], whi[0], wlo[1], whi[1], wlo[2], whi[2]))
    if len(pts) == len(want_pts):
        worst_v = max((p - w).length for p, w in zip(pts, want_pts)) * MM
    else:
        worst_v = float("inf")
    groups = {g.index: g.name for g in mesh.vertex_groups}
    worst_w, most = 0.0, 0
    if len(me.vertices) == len(src["weights"]):
        for v, want in zip(me.vertices, src["weights"]):
            got = {groups[g.group]: g.weight for g in v.groups if g.weight > 0.0}
            most = max(most, len(got))
            for key in set(got) | set(want):
                worst_w = max(worst_w, abs(got.get(key, 0.0) - want.get(key, 0.0)))
    say("        vertices vs source: worst %.4f mm; skin weights vs source: worst %.2e, up to %d bones per vertex; triangles %d (source %d)" % (
        worst_v, worst_w, most, tris, src["tris"]))
    if tris != src["tris"] or worst_v > POS_TOL_MM or worst_w > 1e-4 or most > 4:
        problems.append("mesh or skin differs from the source")
    say("        UV maps %s; colour attributes %s; materials %s" % ([u.name for u in me.uv_layers],
        [(c.name, c.data_type, c.domain) for c in me.color_attributes], [m.name if m else None for m in me.materials]))
    if len(me.color_attributes) != (1 if colour_mode == "shader" else 0):
        problems.append("%d colour attribute(s) for --vertex-colours %s" % (len(me.color_attributes), colour_mode))
    if len(me.materials) != 1:
        problems.append("%d material slots, expected 1" % len(me.materials))
    return problems


# ----------------------------------------------------------------------------------------------------------------------
# main
# ----------------------------------------------------------------------------------------------------------------------
def parse_args(argv):
    here = os.path.dirname(os.path.abspath(__file__))
    ap = argparse.ArgumentParser(prog="export_fbx.py", description="Export and check the ghost-hand FBX files (see the file header).")
    ap.add_argument("--blend", default=os.path.join(here, "ghost_hands.blend"))
    ap.add_argument("--out-dir", default=None)
    ap.add_argument("--poses", default=None)
    ap.add_argument("--check-only", action="store_true")
    ap.add_argument("--vertex-colours", choices=("none", "shader"), default="none")
    a = ap.parse_args(argv)
    if a.out_dir is None or a.poses is None:
        root = repo_root()
        a.out_dir = a.out_dir or os.path.join(root, "unity", "Assets", "UntitledGuitarApp", "Art", "Resources", "GhostHand")
        a.poses = a.poses or os.path.join(root, "unity", "Assets", "StreamingAssets", "poses")
    a.blend, a.out_dir, a.poses = (os.path.abspath(p) for p in (a.blend, a.out_dir, a.poses))
    if not os.path.isfile(a.blend):
        raise ExportError("no .blend at %s (build it with pose_hands.py)" % a.blend)
    return a


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    a = parse_args(argv)
    if not bpy.app.background:
        raise ExportError("run this script headless (see the usage at the top): it opens ghost_hands.blend over the open scene")
    started = time.time_ns()
    bpy.ops.wm.open_mainfile(filepath=a.blend)
    if abs(bpy.context.scene.unit_settings.scale_length - 1.0) > 1e-9:
        raise ExportError("the scene's Unit Scale must be 1.0 (1 Blender unit = 1 m)")
    say("export_fbx.py %s, Blender %s, FBX add-on %s; %s" % (
        SCRIPT_VERSION, bpy.app.version_string, ".".join(map(str, __import__("io_scene_fbx").bl_info["version"])), a.blend))
    sources, paths = {}, {}
    for hand, arm_name in HANDS:
        arm, mesh = hand_objects(arm_name)
        sources[hand] = capture(arm, mesh)
        paths[hand] = os.path.join(a.out_dir, FBX_NAME % hand)
    if not a.check_only:
        os.makedirs(a.out_dir, exist_ok=True)
        for hand, arm_name in HANDS:
            arm, mesh = hand_objects(arm_name)
            prepare_hand(arm, mesh, sources[hand], a.vertex_colours)
            export_hand(arm, mesh, paths[hand], os.path.basename(a.blend), a.vertex_colours)
            st = os.stat(paths[hand])
            if st.st_mtime_ns <= started:
                raise ExportError("%s was not written by this run" % paths[hand])
            say("wrote %s (%d bytes)" % (paths[hand], st.st_size))
    failures = []
    for hand, _arm_name in HANDS:
        path, src = paths[hand], sources[hand]
        if not os.path.isfile(path):
            failures.append("%s: missing" % path)
            continue
        size = os.path.getsize(path)
        say("")
        say("%s  %d bytes (%.0f KB)%s" % (path, size, size / 1024.0, "" if size <= MAX_BYTES else "  TOO BIG (limit 2 MB)"))
        if size > MAX_BYTES:
            failures.append("%s is bigger than 2 MB" % path)
        if b"/Users/" in open(path, "rb").read():
            say("  warning: the file contains a local path ('/Users/')")
        failures += ["%s: %s" % (os.path.basename(path), p) for p in unity_view(path, src, hand, a.poses, a.vertex_colours)]
    for hand, _arm_name in HANDS:              # last: each re-import starts from an empty scene
        if os.path.isfile(paths[hand]):
            say("")
            say(os.path.basename(paths[hand]))
            failures += ["%s (re-import): %s" % (os.path.basename(paths[hand]), p)
                         for p in reimport_check(paths[hand], sources[hand], a.vertex_colours)]
    say("")
    if failures:
        raise ExportError("%d check(s) failed:\n  " % len(failures) + "\n  ".join(failures))
    say("export_fbx.py: OK - both FBX files match the source rig (vertex colours: %s)%s" % (
        a.vertex_colours, " (check only)" if a.check_only else ""))


if __name__ == "__main__":
    try:
        main()
    except ExportError as e:
        sys.stderr.write("export_fbx.py: ERROR: %s\n" % e)
        sys.stderr.flush()
        sys.exit(1)
