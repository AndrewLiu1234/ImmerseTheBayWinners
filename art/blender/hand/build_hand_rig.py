"""
build_hand_rig.py - build the ghost hand: a stylized adult hand mesh and its 16-bone armature, procedurally, headless.

No downloaded asset: the mesh is a hand-written quad cage (a box-modelled palm, four fingers, a thumb) smoothed with two
levels of Catmull-Clark subdivision, then skinned with analytic weights. One closed mesh per hand, about 5,400 triangles.

USAGE (Blender 5.2; options after the "--"; --python-exit-code must come before --python):

  /Applications/Blender.app/Contents/MacOS/Blender --factory-startup -b --python-exit-code 1 \
      --python art/blender/hand/build_hand_rig.py -- [options]

  --hand-length MM   wrist crease to middle fingertip of the hand to imitate   default 187 (an average adult hand)
  --fbx-dir DIR      write ghost_hand_fretting.fbx and ghost_hand_strumming.fbx there (bind pose, mesh + armature)
  --preview DIR      render rig-bind.png and rig-curl.png there (the bare hands in the ghost look)
  --out PATH         also save the two hands as a .blend (for a look around; nothing else needs it)

It starts from an empty scene, so it must run headless. pose_hands.py imports this file and calls build_hand().

THE HAND'S OWN AXES (the armature's space = "model space" of the pose files and of the FBX; 1 Blender unit = 1 m):
  origin  the wrist joint (head of the bone 'wrist')
  +Y      forward, from the wrist toward the knuckles (the way the straight fingers point)
  +Z      dorsal, out of the back of the hand (the palm faces -Z)
  +X      toward the little finger for the RIGHT (strumming) hand; the LEFT (fretting) hand is its exact mirror image,
          so its thumb is on +X
Unity gets (x, y, z) = (X, Z, Y): fingers along +z, back of the hand up (+y). The same frame as CapsuleHandDimensions.

BONES (16, the names export_poses.py and GhostPose.cs expect): wrist; <thumb|index|middle|ring|pinky>_<proximal|
intermediate|distal>. Every digit bone has +Y along the bone, +Z toward the palm side of that digit and +X = the flexion
axis, so "curl" is a positive rotation about local X on every bone of both hands. The tail of each *_distal bone is the
fingertip, and it sits exactly on the tip of the mesh. thumb_proximal is the thumb's metacarpal.
"""

import argparse
import math
import os
import sys

import bmesh
import bpy
from mathutils import Matrix, Quaternion, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ghost_look  # noqa: E402

SCRIPT_VERSION = "1.0.0"
MM = 0.001
REFERENCE_HAND_LENGTH_MM = 187.0  # the hand described by the numbers below (wrist crease to middle fingertip)

DIGITS = ("thumb", "index", "middle", "ring", "pinky")
FINGERS = ("index", "middle", "ring", "pinky")
BONES = [("wrist", None)]
for _d in DIGITS:
    BONES += [(_d + "_proximal", "wrist"), (_d + "_intermediate", _d + "_proximal"), (_d + "_distal", _d + "_intermediate")]
BONE_NAMES = [b[0] for b in BONES]

# ---- the RIGHT hand in millimetres (x toward the little finger, y forward, z dorsal); the left hand mirrors x ---------
# mcp = knuckle joint (x, y); splay = degrees toward +x; L = the three segment lengths (joint to joint, last one to the
# tip of the finger); r = radius at the knuckle, the middle joint, the last joint and the fingertip pad.
FINGER_DATA = {
    "index": dict(mcp=(-27.5, 90.0), splay=-6.0, L=(43.0, 25.0, 23.0), r=(8.9, 8.2, 7.3, 6.3)),
    "middle": dict(mcp=(-8.5, 95.0), splay=-1.5, L=(47.0, 28.0, 24.0), r=(9.1, 8.4, 7.5, 6.5)),
    "ring": dict(mcp=(10.0, 91.0), splay=4.0, L=(44.0, 27.0, 23.5), r=(8.7, 8.0, 7.1, 6.2)),
    "pinky": dict(mcp=(27.5, 83.0), splay=10.0, L=(34.0, 20.0, 21.0), r=(7.8, 7.1, 6.4, 5.6)),
}
FINGER_BASE_Z = 1.5                 # centre line of the fingers at the knuckles
REST_BEND_DEG = (6.0, 8.0, 6.0)     # the bind pose is slightly curled: knuckle, middle joint, last joint
WEB_AT = 0.36                       # the fingers separate this far along the first segment
THUMB_DATA = dict(
    cmc=(-25.0, 15.0, -6.0),        # the thumb's base joint, inside the heel of the hand
    dirs=((-0.62, 0.70, -0.35), (-0.50, 0.82, -0.27), (-0.42, 0.87, -0.24)),
    L=(44.0, 32.0, 27.0),           # metacarpal, proximal, distal (to the tip)
    r=(13.0, 10.6, 9.4, 8.6),       # mid-metacarpal, knuckle, last joint, pad
    pad=(0.75, 0.0, -0.66),         # the way the thumb's pad faces (its palm side)
)
PIVOT_LIFT = 0.30                   # joints sit this fraction of the radius toward the back of the finger, so a bent
                                    # knuckle keeps its shape under plain linear skinning (what Unity does)
CAGE_TO_LIMIT = 0.925               # a square cage of half-width a becomes a tube of radius about 0.925 a

# palm cage: five lines across the hand (0 = thumb side ... 4 = little-finger side) for each row
PALM_ROWS = {
    "stub": dict(y=(-48.0,) * 5, x=(-26.5, -13.5, 0.0, 13.5, 26.0), zt=(12.5, 15.5, 16.5, 15.5, 12.5), zb=(-14.5, -18.0, -18.5, -18.0, -14.5)),
    "wrist": dict(y=(2.0,) * 5, x=(-29.5, -15.0, 0.0, 15.0, 28.5), zt=(11.5, 14.0, 14.5, 13.5, 11.0), zb=(-16.0, -17.5, -16.5, -17.0, -15.5)),
    "mid": dict(y=(44.0, 47.0, 48.0, 47.0, 44.0), x=(-40.5, -19.5, 0.5, 19.5, 37.0), zt=(10.5, 13.0, 13.5, 12.0, 9.5), zb=(-17.5, -13.5, -11.5, -13.0, -14.5)),
    "knuckle": dict(zt=(10.5, 12.8, 13.0, 12.0, 9.5), zb=(-11.5, -13.0, -13.0, -12.5, -11.0)),  # x, y follow the knuckles
}
THUMB_BASE_OWN = {"top_wrist": 0.10, "top_mid": 0.12, "bot_mid": 0.55, "bot_wrist": 0.35}


class BuildError(Exception):
    pass


def smoothstep(a, b, x):
    if b <= a:
        return 1.0 if x >= b else 0.0
    t = min(1.0, max(0.0, (x - a) / (b - a)))
    return t * t * (3.0 - 2.0 * t)


# ----------------------------------------------------------------------------------------------------------------------
# geometry of the right hand, in millimetres
# ----------------------------------------------------------------------------------------------------------------------
class Digit(object):
    """Centre line, frames and joints of one digit. c[0..3] = joint centres and the tip; d, n, lat = per-segment axes."""

    def station(self, s):
        """Centre-line point and frame (axis, dorsal normal, lateral) at distance s from the base joint."""
        L = self.L
        bounds = (0.0, L[0], L[0] + L[1], L[0] + L[1] + L[2])
        for j in (1, 2):                                    # exactly on a joint: the frame bisects the two segments
            if abs(s - bounds[j]) < 1e-6:
                n = (self.n[j - 1] + self.n[j]).normalized()
                return self.c[j].copy(), n, (self.lat[j - 1] + self.lat[j]).normalized()
        seg = 0 if s < bounds[1] else (1 if s < bounds[2] else 2)
        return self.c[seg] + (s - bounds[seg]) * self.d[seg], self.n[seg].copy(), self.lat[seg].copy()

    def radius(self, s):
        L = self.L
        xs = (0.0, L[0], L[0] + L[1], L[0] + L[1] + L[2])
        r = self.r
        if s <= xs[0]:
            return r[0]
        for j in range(3):
            if s <= xs[j + 1]:
                t = (s - xs[j]) / (xs[j + 1] - xs[j])
                return r[j] + t * (r[j + 1] - r[j])
        return r[3]

    def ring(self, s, r, wide=1.05, thick=0.95, shrink=1.0):
        p, n, lat = self.station(s)
        hw = wide * r * shrink / CAGE_TO_LIMIT
        hh = thick * r * shrink / CAGE_TO_LIMIT
        return [p - hw * lat + hh * n, p + hw * lat + hh * n, p + hw * lat - hh * n, p - hw * lat - hh * n]


def make_finger(name):
    f = FINGER_DATA[name]
    g = Digit()
    g.name, g.L, g.r = name, f["L"], f["r"]
    a = math.radians(f["splay"])
    fwd = Vector((math.sin(a), math.cos(a), 0.0))
    lat = Vector((math.cos(a), -math.sin(a), 0.0))
    up = Vector((0.0, 0.0, 1.0))
    pitch, g.d, g.n, g.lat = 0.0, [], [], []
    for bend in REST_BEND_DEG:
        pitch += math.radians(bend)
        g.d.append(math.cos(pitch) * fwd - math.sin(pitch) * up)
        g.n.append(math.sin(pitch) * fwd + math.cos(pitch) * up)
        g.lat.append(lat.copy())
    g.c = [Vector((f["mcp"][0], f["mcp"][1], FINGER_BASE_Z))]
    for j in range(3):
        g.c.append(g.c[j] + g.L[j] * g.d[j])
    g.palmar = [-n for n in g.n]
    # joints (bone heads): lifted toward the back of the finger; the tip is set from the finished mesh
    g.joint = [g.c[0] + PIVOT_LIFT * g.r[0] * g.n[0],
               g.c[1] + PIVOT_LIFT * g.r[1] * (g.n[0] + g.n[1]).normalized(),
               g.c[2] + PIVOT_LIFT * g.r[2] * (g.n[1] + g.n[2]).normalized()]
    return g


def make_thumb():
    t = THUMB_DATA
    g = Digit()
    g.name, g.L = "thumb", t["L"]
    g.r = (t["r"][1], t["r"][1], t["r"][2], t["r"][3])
    pad0 = Vector(t["pad"]).normalized()
    g.d, g.n, g.lat, g.palmar = [], [], [], []
    for dv in t["dirs"]:
        d = Vector(dv).normalized()
        pad = (pad0 - pad0.dot(d) * d).normalized()
        g.d.append(d)
        g.palmar.append(pad)
        g.n.append(-pad)
        g.lat.append(d.cross(-pad).normalized())
    g.c = [Vector(t["cmc"])]
    for j in range(3):
        g.c.append(g.c[j] + g.L[j] * g.d[j])
    g.joint = [g.c[0].copy(),
               g.c[1] + PIVOT_LIFT * t["r"][1] * (g.n[0] + g.n[1]).normalized(),
               g.c[2] + PIVOT_LIFT * t["r"][2] * (g.n[1] + g.n[2]).normalized()]
    return g


class Cage(object):
    def __init__(self):
        self.verts, self.own, self.faces = [], [], []

    def add(self, p, own=None):
        self.verts.append(Vector(p))
        self.own.append(dict(own or {}))
        return len(self.verts) - 1

    def quad(self, a, b, c, d):
        self.faces.append((a, b, c, d))

    def tube(self, base, rings, own):
        prev = base
        for corners in rings:
            cur = [self.add(p, own) for p in corners]
            for m in range(4):
                self.quad(prev[m], prev[(m + 1) % 4], cur[(m + 1) % 4], cur[m])
            prev = cur
        self.quad(*prev)


def build_cage(fingers, thumb, tip_shift):
    """The control cage of the right hand in millimetres. tip_shift moves each digit's last two rings along the digit."""
    cage = Cage()
    lane = [{"index": 1.0}, {"index": 0.5, "middle": 0.5}, {"middle": 0.5, "ring": 0.5}, {"ring": 0.5, "pinky": 0.5}, {"pinky": 1.0}]
    fl = [fingers[n] for n in FINGERS]

    def lift(p, z):
        return Vector((p.x, p.y, z))

    rows_top, rows_bot = [], []
    for key in ("stub", "wrist", "mid"):
        row = PALM_ROWS[key]
        rows_top.append([cage.add((row["x"][i], row["y"][i], row["zt"][i]), lane[i]) for i in range(5)])
        rows_bot.append([cage.add((row["x"][i], row["y"][i], row["zb"][i]), lane[i]) for i in range(5)])

    # knuckle row: between the knuckles, a little behind the joint line
    kn = PALM_ROWS["knuckle"]
    pts = []
    for i in range(5):
        if i == 0:
            p = fl[0].c[0] - 12.5 * fl[0].lat[0]
        elif i == 4:
            p = fl[3].c[0] + 11.5 * fl[3].lat[0]
        else:
            p = 0.5 * (fl[i - 1].c[0] + fl[i].c[0])
        pts.append(p - Vector((0.0, 3.0, 0.0)))
    rows_top.append([cage.add(lift(pts[i], kn["zt"][i]), lane[i]) for i in range(5)])
    rows_bot.append([cage.add(lift(pts[i], kn["zb"][i]), lane[i]) for i in range(5)])

    # front row: where the fingers leave the palm; neighbouring fingers share the web between them
    base = [f.ring(WEB_AT * f.L[0], f.radius(WEB_AT * f.L[0])) for f in fl]
    web_back = Vector((0.0, 2.0, 0.0))
    top, bot = [], []
    for i in range(5):
        if i == 0:
            t, b = base[0][0], base[0][3]
        elif i == 4:
            t, b = base[3][1], base[3][2]
        else:
            t = 0.5 * (base[i - 1][1] + base[i][0]) - web_back
            b = 0.5 * (base[i - 1][2] + base[i][3]) - web_back
        top.append(cage.add(t, lane[i]))
        bot.append(cage.add(b, lane[i]))
    rows_top.append(top)
    rows_bot.append(bot)

    for r in range(4):
        for i in range(4):
            cage.quad(rows_top[r][i], rows_top[r][i + 1], rows_top[r + 1][i + 1], rows_top[r + 1][i])
            cage.quad(rows_bot[r][i], rows_bot[r + 1][i], rows_bot[r + 1][i + 1], rows_bot[r][i + 1])
        cage.quad(rows_top[r][4], rows_bot[r][4], rows_bot[r + 1][4], rows_top[r + 1][4])
        if r != 1:  # the thumb grows out of the side between the wrist row and the mid row
            cage.quad(rows_top[r][0], rows_top[r + 1][0], rows_bot[r + 1][0], rows_bot[r][0])
    for i in range(4):
        cage.quad(rows_top[0][i], rows_bot[0][i], rows_bot[0][i + 1], rows_top[0][i + 1])

    for k, f in enumerate(fl):
        S = sum(f.L)
        shift = tip_shift.get(f.name, 0.0)
        stations = [
            (0.68 * f.L[0], f.radius(0.68 * f.L[0]) * 0.98, 1.0),
            (f.L[0], f.r[1] * 1.07, 1.0),
            (f.L[0] + 0.5 * f.L[1], f.radius(f.L[0] + 0.5 * f.L[1]) * 0.97, 1.0),
            (f.L[0] + f.L[1], f.r[2] * 1.05, 1.0),
            (S - 1.15 * f.r[3] + shift, f.r[3] * 1.02, 1.0),
            (S - 0.30 * f.r[3] + shift, f.r[3], 0.58),
        ]
        rings = [f.ring(s, r, shrink=shrink) for (s, r, shrink) in stations]
        cage.tube([rows_top[4][k], rows_top[4][k + 1], rows_bot[4][k + 1], rows_bot[4][k]], rings, {f.name: 1.0})

    # thumb: its base ring is the open side of the palm (wrist row to mid row on the thumb side)
    t = thumb
    td = THUMB_DATA
    S = sum(t.L)
    shift = tip_shift.get("thumb", 0.0)
    rings = [
        t.ring(0.52 * t.L[0], td["r"][0], wide=1.08, thick=0.96),
        t.ring(t.L[0], td["r"][1] * 1.05),
        t.ring(t.L[0] + 0.5 * t.L[1], 0.5 * (td["r"][1] + td["r"][2]) * 0.97),
        t.ring(t.L[0] + t.L[1], td["r"][2] * 1.05),
        t.ring(S - 1.15 * td["r"][3] + shift, td["r"][3], wide=1.12, thick=0.90),
        t.ring(S - 0.30 * td["r"][3] + shift, td["r"][3], shrink=0.58),
    ]
    a, b, c, d = rows_top[1][0], rows_top[2][0], rows_bot[2][0], rows_bot[1][0]
    cage.own[a]["thumb"] = THUMB_BASE_OWN["top_wrist"]
    cage.own[b]["thumb"] = THUMB_BASE_OWN["top_mid"]
    cage.own[c]["thumb"] = THUMB_BASE_OWN["bot_mid"]
    cage.own[d]["thumb"] = THUMB_BASE_OWN["bot_wrist"]
    cage.own[rows_bot[1][1]]["thumb"] = 0.15   # the heel of the thumb moves a little with it
    cage.own[rows_bot[2][1]]["thumb"] = 0.15
    cage.tube([a, b, c, d], rings, {"thumb": 1.0})
    return cage


# ----------------------------------------------------------------------------------------------------------------------
# Blender objects
# ----------------------------------------------------------------------------------------------------------------------
def _subdivided(cage, to_space, collection, name):
    """Cage -> a new mesh: two levels of Catmull-Clark on the limit surface. Returns (mesh, own) with own[v][digit]."""
    me = bpy.data.meshes.new(name + "_cage")
    me.from_pydata([tuple(to_space(v)) for v in cage.verts], [], cage.faces)
    me.update()
    bm = bmesh.new()
    bm.from_mesh(me)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(me)
    bm.free()
    ob = bpy.data.objects.new(name + "_cage", me)
    collection.objects.link(ob)
    for digit in DIGITS:
        vg = ob.vertex_groups.new(name="own_" + digit)
        for i, own in enumerate(cage.own):
            if own.get(digit, 0.0) > 0.0:
                vg.add([i], own[digit], "REPLACE")
    mod = ob.modifiers.new("Subdivision", "SUBSURF")
    mod.subdivision_type = "CATMULL_CLARK"
    mod.levels = mod.render_levels = 2
    mod.use_limit_surface = True
    mod.quality = 4
    bpy.context.view_layer.update()
    depsgraph = bpy.context.evaluated_depsgraph_get()
    out = bpy.data.meshes.new_from_object(ob.evaluated_get(depsgraph), preserve_all_data_layers=True, depsgraph=depsgraph)
    own = []
    for v in out.vertices:
        row = [0.0] * len(DIGITS)
        for g in v.groups:
            if g.group < len(DIGITS):
                row[g.group] = g.weight
        own.append(row)
    bpy.data.objects.remove(ob)
    bpy.data.meshes.remove(me)
    return out, own


def _chain_param(v, joints, axes, lengths):
    s = (v - joints[0]).dot(axes[0])
    if s <= lengths[0]:
        return s
    s = (v - joints[1]).dot(axes[1])
    if s <= lengths[1]:
        return lengths[0] + max(s, 0.0)
    return lengths[0] + lengths[1] + max((v - joints[2]).dot(axes[2]), 0.0)


def build_hand(side, name, collection, hand_length_mm=REFERENCE_HAND_LENGTH_MM, xform=None):
    """Build one hand in its bind pose. side = +1 for the right hand, -1 for the left. Returns (armature, mesh, info).

    xform (4x4, a rotation) re-expresses the whole hand in another set of axes; the FBX export uses it.
    """
    if side not in (1, -1):
        raise BuildError("side must be +1 (right hand) or -1 (left hand)")
    scale = hand_length_mm / REFERENCE_HAND_LENGTH_MM
    rot = (xform.to_3x3() if xform is not None else Matrix.Identity(3))

    def to_dir(v):       # a direction of the right hand (mm axes) -> the hand's final axes
        return rot @ Vector((side * v.x, v.y, v.z))

    def to_space(v):     # a point of the right hand in mm -> the hand's final axes in metres
        return to_dir(v) * (MM * scale)

    fingers = {n: make_finger(n) for n in FINGERS}
    thumb = make_thumb()
    digits = dict(fingers)
    digits["thumb"] = thumb

    # the fingertip is wherever the smooth surface ends: build, measure, move the last rings, build again
    tip_shift = {}
    mesh, own = None, None
    for _pass in range(3):
        if mesh is not None:
            bpy.data.meshes.remove(mesh)
        mesh, own = _subdivided(build_cage(fingers, thumb, tip_shift), to_space, collection, name)
        apex = {}
        for di, dname in enumerate(DIGITS):
            g = digits[dname]
            axis = to_dir(g.d[2]).normalized()
            best, best_s = None, -1e9
            for v in mesh.vertices:
                if own[v.index][di] > 0.999:
                    s = v.co.dot(axis)
                    if s > best_s:
                        best, best_s = v.co.copy(), s
            if best is None:
                raise BuildError("no vertices found for the %s" % dname)
            apex[dname] = best
            want = to_space(g.c[3]).dot(axis)
            tip_shift[dname] = tip_shift.get(dname, 0.0) + (want - best_s) / (MM * scale)
    mesh.name = name + "_Mesh"

    # ---- bones, in the final axes ------------------------------------------------------------------------------------
    chain = {}
    for dname in DIGITS:
        g = digits[dname]
        pts = [to_space(g.joint[0]), to_space(g.joint[1]), to_space(g.joint[2]), apex[dname]]
        axes = [(pts[j + 1] - pts[j]).normalized() for j in range(3)]
        lengths = [(pts[j + 1] - pts[j]).length for j in range(3)]
        chain[dname] = dict(pts=pts, axes=axes, lengths=lengths, palmar=[to_dir(p) for p in g.palmar])

    arm_data = bpy.data.armatures.new(name)
    arm = bpy.data.objects.new(name, arm_data)
    collection.objects.link(arm)
    arm_data.display_type = "STICK"
    arm.show_in_front = True
    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode="EDIT")
    eb = arm_data.edit_bones.new("wrist")
    eb.head = (0.0, 0.0, 0.0)
    eb.tail = to_space(Vector((0.0, 35.0, 0.0)))
    eb.align_roll(to_dir(Vector((0.0, 0.0, -1.0))))
    for dname in DIGITS:
        c = chain[dname]
        parent = arm_data.edit_bones["wrist"]
        for j, part in enumerate(("proximal", "intermediate", "distal")):
            eb = arm_data.edit_bones.new("%s_%s" % (dname, part))
            eb.head, eb.tail = c["pts"][j], c["pts"][j + 1]
            eb.align_roll(c["palmar"][j])
            eb.parent = parent
            eb.use_connect = j > 0
            parent = eb
    bpy.ops.object.mode_set(mode="OBJECT")
    for pb in arm.pose.bones:
        pb.rotation_mode = "QUATERNION"

    # ---- skin weights and masks --------------------------------------------------------------------------------------
    unit = MM * scale
    forward = to_dir(Vector((0.0, 1.0, 0.0)))
    weights, finger_mask, glow, fade = [], [], [], []
    for v in mesh.vertices:
        w = {}
        masks = [0.0, 0.0, 0.0, 0.0]
        tip = 0.0
        for di, dname in enumerate(DIGITS):
            g = own[v.index][di]
            if g <= 1e-4:
                continue
            c = chain[dname]
            L = c["lengths"]
            s = _chain_param(v.co, c["pts"], c["axes"], L)
            if dname == "thumb":
                b1 = smoothstep(-10.0 * unit, 22.0 * unit, s)
                j1, j2 = 6.0 * unit, 5.0 * unit
            else:
                b1 = smoothstep(-14.0 * unit, 9.0 * unit, s)
                j1, j2 = 4.6 * unit, 4.0 * unit
            b2 = min(b1, smoothstep(L[0] - j1, L[0] + j1, s))
            b3 = min(b2, smoothstep(L[0] + L[1] - j2, L[0] + L[1] + j2, s))
            for bone, amount in ((dname + "_proximal", b1 - b2), (dname + "_intermediate", b2 - b3), (dname + "_distal", b3)):
                if g * amount > 1e-5:
                    w[bone] = w.get(bone, 0.0) + g * amount
            total = sum(L)
            tip = max(tip, g * smoothstep(total - 13.0 * unit, total - 2.0 * unit, s))
            if dname != "thumb":
                masks[FINGERS.index(dname)] = g * smoothstep(0.05 * L[0], 0.5 * L[0], s)
        top = sorted(w.items(), key=lambda kv: -kv[1])[:3]     # at most 4 influences with the wrist
        total = sum(x for _n, x in top)
        if total > 1.0:
            top = [(n, x / total) for n, x in top]
            total = 1.0
        w = dict(top)
        if 1.0 - total > 1e-5:
            w["wrist"] = 1.0 - total
        norm = sum(w.values())
        weights.append({n: x / norm for n, x in w.items()})
        finger_mask.append(masks)
        glow.append(tip)
        fade.append(smoothstep(-46.0 * unit, -10.0 * unit, v.co.dot(forward)))

    ob = bpy.data.objects.new(name + "_Mesh", mesh)
    collection.objects.link(ob)
    ob.vertex_groups.clear()
    groups = {n: ob.vertex_groups.new(name=n) for n in BONE_NAMES}
    for i, w in enumerate(weights):
        for bone, x in w.items():
            groups[bone].add([i], x, "REPLACE")
    while mesh.uv_layers:
        mesh.uv_layers.remove(mesh.uv_layers[0])
    uv = mesh.uv_layers.new(name="GhostMask")
    for name_ in [a.name for a in mesh.color_attributes]:
        mesh.color_attributes.remove(mesh.color_attributes[name_])
    col = mesh.color_attributes.new(name="FingerMask", type="FLOAT_COLOR", domain="CORNER")
    for loop in mesh.loops:
        vi = loop.vertex_index
        uv.data[loop.index].uv = (glow[vi], fade[vi])
        col.data[loop.index].color = finger_mask[vi]
    try:
        mesh.color_attributes.render_color_index = 0
        mesh.color_attributes.active_color_index = 0
    except Exception:
        pass
    for poly in mesh.polygons:
        poly.use_smooth = True
    mesh.update()

    ob.parent = arm
    mod = ob.modifiers.new("Armature", "ARMATURE")
    mod.object = arm
    mod.use_deform_preserve_volume = False   # plain linear skinning, like Unity

    info = check_hand(arm, ob, chain)
    info.update(side="right" if side == 1 else "left", hand_length_mm=hand_length_mm, scale=scale)
    arm["builder"] = "build_hand_rig.py " + SCRIPT_VERSION
    arm["hand_side"] = info["side"]
    arm["hand_length_mm"] = hand_length_mm
    return arm, ob, info


def check_hand(arm, ob, chain):
    """The promises of this file, measured on what was built. Raises BuildError when one is broken."""
    mesh = ob.data
    tris = sum(len(p.vertices) - 2 for p in mesh.polygons)
    bm = bmesh.new()
    bm.from_mesh(mesh)
    open_edges = sum(1 for e in bm.edges if len(e.link_faces) != 2)
    loose = sum(1 for v in bm.verts if not v.link_faces)
    volume = bm.calc_volume(signed=True)
    bm.free()
    if open_edges or loose:
        raise BuildError("%s is not a closed surface: %d edges without exactly two faces, %d loose vertices" % (ob.name, open_edges, loose))
    if volume <= 0.0:
        raise BuildError("%s is inside out (signed volume %.3e)" % (ob.name, volume))
    worst_sum, most = 0.0, 0
    for v in mesh.vertices:
        total = sum(g.weight for g in v.groups)
        worst_sum = max(worst_sum, abs(total - 1.0))
        most = max(most, len(v.groups))
    if worst_sum > 1e-4 or most > 4:
        raise BuildError("%s: skin weights sum off by %.5f, up to %d bones per vertex (limit 4)" % (ob.name, worst_sum, most))
    tip_gap = 0.0
    for dname in DIGITS:
        tail = arm.data.bones[dname + "_distal"].tail_local
        nearest = min((v.co - tail).length for v in mesh.vertices)
        tip_gap = max(tip_gap, nearest)
        if nearest > 1e-6:
            raise BuildError("%s: the %s fingertip (bone tail) is %.4f mm from the mesh" % (ob.name, dname, nearest / MM))
    if tris > 6000:
        raise BuildError("%s has %d triangles (limit 6000)" % (ob.name, tris))
    return dict(triangles=tris, vertices=len(mesh.vertices), faces=len(mesh.polygons), volume_cm3=volume * 1e6,
                max_influences=most, tip_gap_mm=tip_gap / MM)


def curl(arm, amount):
    """A test pose: every joint flexed by 'amount' (0..1) of a fist. Returns nothing; changes the pose in place."""
    full = {"proximal": 75.0, "intermediate": 95.0, "distal": 65.0}
    for pb in arm.pose.bones:
        if pb.name == "wrist":
            continue
        digit, part = pb.name.split("_")
        deg = full[part] * amount * (0.45 if digit == "thumb" else 1.0)
        if digit == "thumb" and part == "proximal":
            deg = 15.0 * amount
        pb.rotation_quaternion = Quaternion((1.0, 0.0, 0.0), math.radians(deg))


# ----------------------------------------------------------------------------------------------------------------------
# command line
# ----------------------------------------------------------------------------------------------------------------------
def parse_args(argv):
    ap = argparse.ArgumentParser(prog="build_hand_rig.py", description="Build the ghost hand mesh and rig (see the file header).")
    ap.add_argument("--hand-length", type=float, default=REFERENCE_HAND_LENGTH_MM)
    ap.add_argument("--fbx-dir", default=None)
    ap.add_argument("--preview", default=None)
    ap.add_argument("--out", default=None)
    p = ap.parse_args(argv)
    if not 120.0 <= p.hand_length <= 260.0:
        raise BuildError("--hand-length %.0f mm is not a hand (expected 120..260)" % p.hand_length)
    return p


def render_previews(folder, hands):
    scene = bpy.context.scene
    ghost_look.setup_scene_look(scene)
    mat = ghost_look.make_ghost_material()
    for arm, ob in hands:
        ob.data.materials.clear()
        ob.data.materials.append(mat)
        arm.hide_render = True
    (larm, _lob), (rarm, _rob) = hands
    larm.location = (-0.105, 0.0, 0.0)
    rarm.location = (0.105, 0.0, 0.0)
    cam = ghost_look.ensure_camera(scene)
    ghost_look.aim_camera(cam, (0.0, -0.10, 0.52), (0.0, 0.075, 0.0), up=(0.0, 1.0, 0.0), lens=58.0)
    written = [ghost_look.render_to(scene, cam, os.path.join(folder, "rig-bind.png"))]
    for arm, _ob in hands:
        curl(arm, 0.8)
    ghost_look.aim_camera(cam, (0.0, -0.36, 0.26), (0.0, 0.05, -0.02), up=(0.0, 0.0, 1.0), lens=58.0)
    written.append(ghost_look.render_to(scene, cam, os.path.join(folder, "rig-curl.png")))
    ghost_look.aim_camera(cam, (0.0, -0.10, -0.52), (0.0, 0.075, 0.0), up=(0.0, 1.0, 0.0), lens=58.0)
    written.append(ghost_look.render_to(scene, cam, os.path.join(folder, "rig-curl-palm.png")))
    for arm, _ob in hands:
        curl(arm, 0.0)
        arm.location = (0.0, 0.0, 0.0)
    return written


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = parse_args(argv)
    if not bpy.app.background:
        raise BuildError("run this script headless (see the usage at the top): it starts from an empty scene")
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1.0
    col = bpy.data.collections.new("GhostHands")
    scene.collection.children.link(col)
    hands = []
    for side, name in ((-1, "GhostHand_Fretting"), (1, "GhostHand_Strumming")):
        arm, ob, info = build_hand(side, name, col, p.hand_length)
        hands.append((arm, ob))
        print("%s (%s hand): %d triangles, %d vertices, closed surface, volume %.0f cm3, <= %d bones per vertex, fingertips on the bone tails (%.4f mm)" % (
            name, info["side"], info["triangles"], info["vertices"], info["volume_cm3"], info["max_influences"], info["tip_gap_mm"]))
    if p.preview:
        for path in render_previews(os.path.abspath(p.preview), hands):
            print("wrote %s" % path)
    if p.out:
        out = os.path.abspath(p.out)
        os.makedirs(os.path.dirname(out), exist_ok=True)
        bpy.ops.wm.save_as_mainfile(filepath=out, check_existing=False)
        print("wrote %s" % out)


if __name__ == "__main__":
    try:
        main()
    except BuildError as e:
        sys.stderr.write("build_hand_rig.py: ERROR: %s\n" % e)
        sys.exit(1)
