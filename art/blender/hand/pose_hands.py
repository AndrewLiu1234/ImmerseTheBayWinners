"""
pose_hands.py - put both ghost hands on a fresh neck mock-up and key every pose, headless. Writes ghost_hands.blend.

USAGE (Blender 5.2; options after the "--"; --python-exit-code must come before --python):

  /Applications/Blender.app/Contents/MacOS/Blender --factory-startup -b --python-exit-code 1 \
      --python art/blender/hand/pose_hands.py -- --neck art/blender/neck_mockup.blend [options]

  --neck PATH          the mock-up from art/blender/build_neck_mockup.py (built with --nut-to-soundhole)   required
  --out PATH           default: ghost_hands.blend next to this script
  --hand-length MM     wrist crease to middle fingertip of the hand to imitate                 default 187
  --neck-depth MM      fretboard surface to the back of the neck at fret 1 (ASSUMED 22; measure it)
  --only LIST          comma list of poses to build, e.g. fretting-G,fretting-Em (default: all twelve)
  --debug-renders DIR  three quick views of every pose (front, along the neck, player's eye)

WHAT IT MAKES
  GhostHand_Fretting (the player's LEFT hand) and GhostHand_Strumming (the RIGHT hand), each an armature with its mesh,
  both children of 'Neck' with no offset, so the armature's axes are the neck's axes. The hand is placed by the wrist
  bone. Every pose is a timeline marker with all 16 bones keyed at that frame:
      fretting-rest, fretting-G, fretting-C, fretting-D, fretting-Em, fretting-Am,
      strumming-rest, strumming-down-start, strumming-down-end, strumming-up-start, strumming-up-end, strumming-pick-ready
  export_poses.py reads exactly these names (--hand fretting|strumming --armature GhostHand_<Hand> --source markers).

HOW A POSE IS FOUND
  1. the palm: a small search over the wrist's position and turn that keeps every fretting finger comfortably arched
     over its dot, the knuckles clear of the neck and the thumb behind it;
  2. the fingers: each one is solved in its own bending plane so its tip is exactly on its target, arched (steep last
     joint) so it clears the neighbouring strings; unused fingers hover in a loose curl;
  3. Blender's IK (chain length 3, a pole target on the knuckle side) runs from that pose and has the last word on the
     fingertip; the result is baked to plain rotation keys and the IK constraints are switched off (they stay on the
     *_distal bones, with their targets keyed per pose, so a pose can be re-solved by hand).
To tweak a pose by hand: go to its marker, rotate bones, select all bones and press I. Then export again.
"""

import argparse
import math
import os
import sys

import bpy
from mathutils import Matrix, Quaternion, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import build_hand_rig as rig  # noqa: E402
import ghost_look  # noqa: E402

SCRIPT_VERSION = "1.0.0"
MM = 0.001
DEG = math.pi / 180.0

FRETTING_POSES = ("rest", "G", "C", "D", "Em", "Am")
STRUMMING_POSES = ("rest", "down-start", "down-end", "up-start", "up-end", "pick-ready")
FRAMES = {}
for _i, _n in enumerate(FRETTING_POSES):
    FRAMES["fretting-" + _n] = 1 + 10 * _i
for _i, _n in enumerate(STRUMMING_POSES):
    FRAMES["strumming-" + _n] = 61 + 10 * _i
HOME_FRET = {"index": 1, "middle": 2, "ring": 3, "pinky": 4}
FINGER_OF = {1: "index", 2: "middle", 3: "ring", 4: "pinky"}


class PoseError(Exception):
    pass


# ----------------------------------------------------------------------------------------------------------------------
# the neck, read back from the mock-up (metres; Blender neck axes)
# ----------------------------------------------------------------------------------------------------------------------
class Neck(object):
    def __init__(self, obj, depth_mm):
        def prop(key, default):
            return float(obj[key]) if key in obj.keys() else default
        self.obj = obj
        self.scale_length = prop("scale_length_mm", 648.0) * MM
        self.nut_width = prop("nut_width_mm", 43.0) * MM
        self.nut_spread = prop("nut_spread_mm", 35.0) * MM
        self.saddle_spread = prop("saddle_spread_mm", 53.0) * MM
        self.string_height = prop("string_height_mm", 1.5) * MM
        self.tip_fraction = prop("tip_fraction", 0.75)
        self.depth = depth_mm * MM
        sound = bpy.data.objects.get("Soundhole")
        self.soundhole_x = sound.location.x if sound is not None else None
        self.soundhole_radius = (max(v.co.x for v in sound.data.vertices) if sound is not None and sound.type == "MESH" else 0.05)

    def fret_x(self, n):
        return self.scale_length * (1.0 - 2.0 ** (-n / 12.0))

    def target_x(self, fret):
        a, b = self.fret_x(fret - 1), self.fret_x(fret)
        return a + self.tip_fraction * (b - a)

    def string_y(self, string_no, x):
        offset = 3.5 - string_no
        at_nut = offset * self.nut_spread / 5.0
        at_saddle = offset * self.saddle_spread / 5.0
        return at_nut + (at_saddle - at_nut) * (x / self.scale_length)

    def half_width(self, x):
        return 0.5 * (self.nut_width + (self.saddle_spread - self.nut_spread) * (x / self.scale_length))

    def profile(self, x):
        """Cross-section of the neck at x as a convex polygon in (y, z): flat fretboard on top, rounded back."""
        hw, flat = self.half_width(x), 5.0 * MM
        pts = [(-hw, 0.0), (hw, 0.0), (hw, -flat)]
        for k in range(1, 8):
            th = math.pi * k / 8.0
            pts.append((hw * math.cos(th), -flat - (self.depth - flat) * math.sin(th)))
        pts.append((-hw, -flat))
        return pts

    def clearance(self, p, radius):
        """How far a ball of this radius at p is from the neck (negative = it is inside the wood)."""
        pts = self.profile(p.x)
        best, inside = 1e9, True
        n = len(pts)
        for i in range(n):
            ay, az = pts[i]
            by, bz = pts[(i + 1) % n]
            ey, ez = by - ay, bz - az
            py, pz = p.y - ay, p.z - az
            if ey * pz - ez * py > 0.0:      # the polygon runs clockwise in (y, z): left of an edge = outside
                inside = False
            t = max(0.0, min(1.0, (py * ey + pz * ez) / (ey * ey + ez * ez)))
            best = min(best, math.hypot(py - t * ey, pz - t * ez))
        return (-best if inside else best) - radius


# ----------------------------------------------------------------------------------------------------------------------
# the rig as plain matrices (so a pose can be computed without stepping Blender)
# ----------------------------------------------------------------------------------------------------------------------
class RigModel(object):
    def __init__(self, arm):
        bones = arm.data.bones
        self.arm = arm
        self.rest = {b.name: b.matrix_local.copy() for b in bones}
        self.parent = {b.name: (b.parent.name if b.parent else None) for b in bones}
        self.length = {b.name: (b.tail_local - b.head_local).length for b in bones}
        self.rel = {n: self.rest[p].inverted() @ self.rest[n] for n, p in self.parent.items() if p}
        self.scale = float(arm.get("hand_length_mm", rig.REFERENCE_HAND_LENGTH_MM)) / rig.REFERENCE_HAND_LENGTH_MM

    def chain(self, digit):
        return [digit + "_proximal", digit + "_intermediate", digit + "_distal"]

    def complete(self, mats):
        """Fill in every bone that has no matrix yet with its rest position under its (posed) parent."""
        for name in rig.BONE_NAMES:
            if name not in mats:
                mats[name] = mats[self.parent[name]] @ self.rel[name]
        return mats

    def tip(self, mats, digit):
        name = digit + "_distal"
        return mats[name] @ Vector((0.0, self.length[name], 0.0))


def frame_matrix(x, y, z, origin):
    m = Matrix.Identity(4)
    for i in range(3):
        m[i][0], m[i][1], m[i][2], m[i][3] = x[i], y[i], z[i], origin[i]
    return m


def rotate_about(v, axis, angle):
    return Quaternion(axis, angle) @ v


FINGER_STYLE = dict(limits=((-30.0, 95.0), (2.0, 115.0), (-10.0, 85.0)), ideal=(15.0, 75.0, 45.0), spread=(40.0, 35.0, 30.0),
                    elev=75.0, w_elev=1.5, w_phi=1.0, phi_spread=18.0, twists=(0.0,), clear=9.0)
HOVER_STYLE = dict(limits=((-30.0, 95.0), (2.0, 115.0), (-10.0, 85.0)), ideal=(25.0, 50.0, 30.0), spread=(25.0, 25.0, 20.0),
                   elev=45.0, w_elev=0.3, w_phi=1.0, phi_spread=14.0, twists=(0.0,), clear=0.0)
THUMB_STYLE = dict(limits=((-50.0, 50.0), (-12.0, 60.0), (-20.0, 80.0)), ideal=(0.0, 15.0, 15.0), spread=(35.0, 30.0, 35.0),
                   elev=None, w_elev=0.0, w_phi=0.3, phi_spread=30.0, twists=(-60.0, -40.0, -20.0, 0.0, 20.0, 40.0, 60.0), clear=0.0)


def solve_digit(model, digit, wrist, target, style, pref_dir=None, pad_dir=None):
    """Bend one digit in a plane so its tip is on 'target'. Returns a dict (mats, angles, cost, miss)."""
    names = model.chain(digit)
    base = wrist @ model.rel[names[0]]
    p0 = base.translation
    r3 = base.to_3x3()
    y0, z0 = r3.col[1].normalized(), r3.col[2].normalized()
    L1, L2, L3 = (model.length[n] for n in names)
    v = target - p0
    dist = v.length
    best = None
    for twist in style["twists"]:
        zt = rotate_about(z0, v.normalized(), twist * DEG) if (twist and dist > 1e-6) else z0
        yt = rotate_about(y0, v.normalized(), twist * DEG) if (twist and dist > 1e-6) else y0
        h = v - v.dot(zt) * zt
        fwd = h.normalized() if h.length > 1e-6 else yt.copy()
        if fwd.dot(yt) < 0.0:
            fwd = -fwd
        phi = math.atan2(yt.cross(fwd).dot(zt), yt.dot(fwd)) / DEG
        xax = fwd.cross(zt).normalized()
        u, w = v.dot(fwd), v.dot(zt)
        line = math.atan2(w, u)
        for step in range(-70, 161, 2):
            a3 = line + step * DEG
            du, dw = u - L3 * math.cos(a3), w - L3 * math.sin(a3)
            d = math.hypot(du, dw)
            if d > L1 + L2 - 1e-6 or d < abs(L1 - L2) + 1e-6:
                continue
            a1 = math.atan2(dw, du) - math.acos(max(-1.0, min(1.0, (L1 * L1 + d * d - L2 * L2) / (2.0 * L1 * d))))
            fl2 = math.pi - math.acos(max(-1.0, min(1.0, (L1 * L1 + L2 * L2 - d * d) / (2.0 * L1 * L2))))
            a2 = a1 + fl2
            fl = (a1 / DEG, fl2 / DEG, (a3 - a2) / DEG)
            cost, hard = 0.0, 0.0
            for j in range(3):
                lo, hi = style["limits"][j]
                hard += max(0.0, lo - fl[j], fl[j] - hi)
                cost += ((fl[j] - style["ideal"][j]) / style["spread"][j]) ** 2
            cost += style["w_phi"] * (phi / style["phi_spread"]) ** 2 + (twist / 45.0) ** 2 + 4.0 * hard * hard
            y3 = math.cos(a3) * fwd + math.sin(a3) * zt
            if style["elev"] is not None:
                elev = math.asin(max(-1.0, min(1.0, -y3.z))) / DEG
                cost += style["w_elev"] * ((elev - style["elev"]) / 20.0) ** 2
            if pref_dir is not None:
                cost += (y3.angle(pref_dir) / DEG / 30.0) ** 2
            if pad_dir is not None:
                z3 = -math.sin(a3) * fwd + math.cos(a3) * zt
                cost += 0.6 * (z3.angle(pad_dir) / DEG / 40.0) ** 2
            if style["clear"]:
                dip_z = (p0 + math.cos(a1) * L1 * fwd + math.sin(a1) * L1 * zt + math.cos(a2) * L2 * fwd + math.sin(a2) * L2 * zt).z
                cost += (max(0.0, style["clear"] * MM - dip_z) / (3.0 * MM)) ** 2
            if best is None or cost < best["cost"]:
                best = dict(cost=cost, hard=hard, angles=(a1, a2, a3), fl=fl, phi=phi, twist=twist, fwd=fwd.copy(), z=zt.copy(), x=xax.copy())
    if best is None:   # out of reach: point the straight finger at the target and report the miss
        fwd = v.normalized() if dist > 1e-6 else y0.copy()
        zt = (z0 - z0.dot(fwd) * fwd).normalized()
        best = dict(cost=50.0 + (max(0.0, dist - (L1 + L2 + L3)) / MM) ** 2, hard=0.0, angles=(0.0, 0.0, 0.0), fl=(0.0, 0.0, 0.0),
                    phi=0.0, twist=0.0, fwd=fwd, z=zt, x=fwd.cross(zt).normalized())
    mats, p = {}, p0.copy()
    for j, name in enumerate(names):
        a = best["angles"][j]
        y = math.cos(a) * best["fwd"] + math.sin(a) * best["z"]
        z = -math.sin(a) * best["fwd"] + math.cos(a) * best["z"]
        mats[name] = frame_matrix(best["x"], y, z, p)
        p = p + model.length[name] * y
    best["mats"] = mats
    best["joints"] = [mats[n].translation.copy() for n in names] + [p]
    best["miss"] = (p - target).length
    return best


def curl_digit(model, digit, wrist, flex_deg, spread_deg=0.0):
    """Forward pose of one digit: absolute flexion of its three joints (degrees), measured from the bind direction."""
    names = model.chain(digit)
    base = wrist @ model.rel[names[0]]
    r3 = base.to_3x3()
    y0, z0 = r3.col[1].normalized(), r3.col[2].normalized()
    fwd = rotate_about(y0, z0, spread_deg * DEG)
    xax = fwd.cross(z0).normalized()
    mats, p, a = {}, base.translation.copy(), 0.0
    for j, name in enumerate(names):
        a += flex_deg[j] * DEG
        y = math.cos(a) * fwd + math.sin(a) * z0
        z = -math.sin(a) * fwd + math.cos(a) * z0
        mats[name] = frame_matrix(xax, y, z, p)
        p = p + model.length[name] * y
    return mats, p


def nelder_mead(f, x0, steps, iterations=260):
    n = len(x0)
    simplex = [list(x0)]
    for i in range(n):
        x = list(x0)
        x[i] += steps[i]
        simplex.append(x)
    values = [f(x) for x in simplex]
    for _ in range(iterations):
        order = sorted(range(n + 1), key=lambda i: values[i])
        simplex = [simplex[i] for i in order]
        values = [values[i] for i in order]
        centre = [sum(simplex[i][k] for i in range(n)) / n for k in range(n)]
        worst = simplex[-1]
        refl = [centre[k] + (centre[k] - worst[k]) for k in range(n)]
        fr = f(refl)
        if fr < values[0]:
            exp = [centre[k] + 2.0 * (centre[k] - worst[k]) for k in range(n)]
            fe = f(exp)
            simplex[-1], values[-1] = (exp, fe) if fe < fr else (refl, fr)
        elif fr < values[-2]:
            simplex[-1], values[-1] = refl, fr
        else:
            con = [centre[k] + 0.5 * (worst[k] - centre[k]) for k in range(n)]
            fc = f(con)
            if fc < values[-1]:
                simplex[-1], values[-1] = con, fc
            else:
                for i in range(1, n + 1):
                    simplex[i] = [simplex[0][k] + 0.5 * (simplex[i][k] - simplex[0][k]) for k in range(n)]
                    values[i] = f(simplex[i])
    i = min(range(n + 1), key=lambda k: values[k])
    return simplex[i], values[i]


# ----------------------------------------------------------------------------------------------------------------------
# the fretting hand (the player's left hand)
# ----------------------------------------------------------------------------------------------------------------------
def fretting_wrist(model, params, base_rot, base_k):
    rot = Quaternion(Vector(params[0:3]).normalized(), Vector(params[0:3]).length).to_matrix() if Vector(params[0:3]).length > 1e-9 else Matrix.Identity(3)
    r = rot @ base_rot
    k = base_k + Vector(params[3:6])
    m = r.to_4x4()
    m.translation = k - r @ model.rel["middle_proximal"].translation
    return m


def thumb_goal(model, neck, wrist):
    """Where the thumb rests: its tip on the back of the neck, a little toward the nut from the index knuckle."""
    index_knuckle = (wrist @ model.rel["index_proximal"]).translation
    x = max(8.0 * MM, index_knuckle.x - 4.0 * MM)
    pad = 8.6 * MM * model.scale
    target = Vector((x, -3.0 * MM, -(neck.depth + 0.9 * pad)))
    return target, Vector((-0.12, -0.99, 0.05)).normalized(), Vector((0.0, 0.0, 1.0))


def solve_fretting(model, neck, name, targets):
    """targets: {finger name: Vector} for the fingers that fret. Returns (mats for all 16 bones, report dict)."""
    unit = MM * model.scale
    lane = {"index": -19.0 * unit, "middle": 0.0, "ring": 18.5 * unit, "pinky": 36.0 * unit}
    hover = name == "rest"
    if hover:   # the palm sits as for a chord over frets 1-3 on the middle strings, then backs off; no finger touches
        goals = {}
        for finger in ("index", "middle", "ring"):
            x = neck.target_x(HOME_FRET[finger])
            goals[finger] = Vector((x, neck.string_y(3, x), neck.string_height))
    else:
        goals = dict(targets)
    style = FINGER_STYLE
    beta = 62.0 * DEG   # the palm faces up and a little back toward the player; the knuckles are in front of the fretboard
    f = Vector((0.0, -math.cos(beta), math.sin(beta)))
    p = Vector((0.0, -math.sin(beta), -math.cos(beta)))
    base_rot = Matrix((f.cross(p), f, p)).transposed()
    base_rot = Matrix.Rotation(14.0 * DEG, 3, "Z") @ base_rot    # the knuckle line drops a little toward the bridge
    kx = sum(goals[fg].x - lane[fg] for fg in goals) / len(goals)
    reach_y = max(goals[fg].y for fg in goals)
    base_k = Vector((kx, max(neck.half_width(kx) + 26.0 * MM, reach_y + 34.0 * MM), 15.0 * MM))

    def evaluate(params, full=False):
        wrist = fretting_wrist(model, params, base_rot, base_k)
        cost, solved = 0.0, {}
        for fg, target in goals.items():
            res = solve_digit(model, fg, wrist, target, style)
            solved[fg] = res
            cost += res["cost"] + (res["miss"] / MM) ** 2 * 4.0
        tgt, pref, pad = thumb_goal(model, neck, wrist)
        thumb = solve_digit(model, "thumb", wrist, tgt, THUMB_STYLE, pref_dir=pref, pad_dir=pad)
        cost += 0.5 * thumb["cost"] + (thumb["miss"] / MM) ** 2 * 0.5
        balls = [((wrist @ model.rel[fg + "_proximal"]).translation, 9.5 * unit) for fg in rig.FINGERS]
        balls.append(((wrist @ model.rel["thumb_proximal"]).translation, 13.0 * unit))
        balls.append((wrist.translation, 21.0 * unit))
        balls.append((wrist @ Vector((0.0, 50.0 * unit, 0.0)), 14.0 * unit))
        for res in solved.values():
            balls.append((res["joints"][1], 8.5 * unit))
        for centre, radius in balls:
            gap = neck.clearance(centre, radius)
            cost += 0.25 * (max(0.0, 2.0 * MM - gap) / MM) ** 2
        cost += 0.6 * (Vector(params[0:3]).length / 0.45) ** 2 + 0.3 * (Vector(params[3:6]).length / (30.0 * MM)) ** 2
        return (cost, wrist, solved, thumb) if full else cost

    best, _value = nelder_mead(evaluate, [0.0] * 6, [0.12, 0.12, 0.12, 0.008, 0.008, 0.008], iterations=320)
    best, value = nelder_mead(evaluate, best, [0.05, 0.05, 0.05, 0.004, 0.004, 0.004], iterations=200)
    cost, wrist, solved, thumb = evaluate(best, full=True)
    loose = {"index": (18.0, 42.0, 24.0), "middle": (22.0, 48.0, 28.0), "ring": (25.0, 52.0, 30.0), "pinky": (28.0, 55.0, 32.0)}
    if hover:
        wrist = wrist.copy()
        wrist.translation = wrist.translation + Vector((0.0, 5.0 * MM, 14.0 * MM))
        solved = {}
        tgt, pref, pad = thumb_goal(model, neck, wrist)
        thumb = solve_digit(model, "thumb", wrist, tgt + Vector((0.0, 0.0, -6.0 * MM)), THUMB_STYLE, pref_dir=pref, pad_dir=pad)

    mats = {"wrist": wrist}
    report = dict(cost=cost, fingers={}, wrist=wrist)
    for fg, res in solved.items():
        mats.update(res["mats"])
        report["fingers"][fg] = res
    mats.update(thumb["mats"])
    report["fingers"]["thumb"] = thumb
    for fg in rig.FINGERS:      # unused fingers: a loose curl, lifted clear of the strings
        if fg in solved:
            continue
        curled, tip = curl_digit(model, fg, wrist, loose[fg])
        floor = neck.string_height + 13.0 * MM
        if tip.z < floor and abs(tip.y) < neck.half_width(tip.x) + 8.0 * MM:
            res = solve_digit(model, fg, wrist, Vector((tip.x, tip.y, floor + 3.0 * MM)), HOVER_STYLE)
            curled = res["mats"]
            report["fingers"][fg] = res
        mats.update(curled)
    return model.complete(mats), report


# ----------------------------------------------------------------------------------------------------------------------
# the strumming hand (the player's right hand)
# ----------------------------------------------------------------------------------------------------------------------
# name: (where the pick point is: across the strings, toward the nut, above the strings in mm; how far the fingers lean
# toward the neck in degrees; grip: 1 = thumb and index pinched as on a pick, 0 = loose)
STRUM_POSES = {
    "down-start": dict(across=("above", 6, 12.0), along=0.0, above=1.0, lean=38.0, grip=1.0),
    "down-end": dict(across=("below", 1, 12.0), along=4.0, above=1.0, lean=18.0, grip=1.0),
    "up-start": dict(across=("below", 1, 10.0), along=2.0, above=1.0, lean=14.0, grip=1.0),
    "up-end": dict(across=("above", 4, 5.0), along=-2.0, above=2.0, lean=34.0, grip=1.0),
    "pick-ready": dict(across=("above", 3, 3.5), along=0.0, above=5.0, lean=27.0, grip=1.0),
    "rest": dict(across=("below", 1, 16.0), along=6.0, above=20.0, lean=24.0, grip=0.0),
}
PICK_REACH_MM = 9.0     # the pick point is this far beyond the pinch of thumb and index, toward the strings
STRUM_CLEAR_MM = 6.0    # the other fingertips stay at least this far above the strings


def strum_line_x(neck):
    """Where the pick crosses the strings: between the end of the fretboard and the middle of the soundhole."""
    if neck.soundhole_x is None:
        raise PoseError("the mock-up has no 'Soundhole' marker: rebuild it with --nut-to-soundhole MM (the strumming hand needs it)")
    return neck.soundhole_x - 0.5 * neck.soundhole_radius


def solve_strumming(model, neck, name):
    spec = STRUM_POSES[name]
    unit = MM * model.scale
    xs = strum_line_x(neck)
    side, string_no, gap = spec["across"]
    y = neck.string_y(string_no, xs) + (gap if side == "below" else -gap) * MM
    pick = Vector((xs + spec["along"] * MM, y, neck.string_height + spec["above"] * MM))

    lean, tip_in, roll = spec["lean"] * DEG, 14.0 * DEG, -8.0 * DEG
    f = math.cos(tip_in) * Vector((-math.sin(lean), math.cos(lean), 0.0)) + math.sin(tip_in) * Vector((0.0, 0.0, -1.0))
    p = Vector((0.0, 0.0, -1.0))
    p = (p - p.dot(f) * f).normalized()
    p = rotate_about(p, f, roll)
    rot = Matrix((f.cross(p), f, p)).transposed()
    wrist = rot.to_4x4()

    grip = spec["grip"]
    if grip > 0.5:
        curls = {"index": (56.0, 76.0, 40.0), "middle": (50.0, 72.0, 40.0), "ring": (50.0, 70.0, 38.0), "pinky": (48.0, 66.0, 36.0)}
    else:       # at rest the hand hangs open
        curls = {"index": (26.0, 40.0, 24.0), "middle": (30.0, 46.0, 26.0), "ring": (33.0, 50.0, 28.0), "pinky": (36.0, 52.0, 30.0)}
    mats = {"wrist": wrist}
    tips = {}
    for fg in rig.FINGERS:
        m, tip = curl_digit(model, fg, wrist, curls[fg], spread_deg=0.0)
        mats.update(m)
        tips[fg] = tip
    radial = rot.col[0].normalized()          # the right hand's wrist bone +X points to the thumb side
    index_dip = mats["index_distal"].translation
    report = dict(fingers={})
    if grip > 0.5:   # the thumb's pad on the side of the index finger's last joint, as on a pick
        goal = tips["index"] + 0.45 * (index_dip - tips["index"]) + (10.5 * unit) * radial
        thumb = solve_digit(model, "thumb", wrist, goal, THUMB_STYLE, pad_dir=-radial)
        mats.update(thumb["mats"])
        report["fingers"]["thumb"] = thumb
        thumb_tip = thumb["joints"][3]
    else:
        m, thumb_tip = curl_digit(model, "thumb", wrist, (4.0, 10.0, 12.0))
        mats.update(m)
    model.complete(mats)
    toward_strings = rot.col[2].normalized()    # the palm side of the hand
    point = (0.5 * (tips["index"] + thumb_tip) if grip > 0.5 else tips["index"]) + (PICK_REACH_MM * unit) * toward_strings
    shift = pick - point
    for key in mats:
        m = mats[key].copy()
        m.translation = m.translation + shift
        mats[key] = m
    # no other fingertip may dip into the strings: curl it further until it clears them
    floor = neck.string_height + STRUM_CLEAR_MM * MM
    for fg in ("middle", "ring", "pinky") + (("index",) if grip < 0.5 else ()):
        curl = list(curls[fg])
        for _try in range(8):
            if model.tip(mats, fg).z >= floor:
                break
            curl[1] += 6.0
            curl[2] += 3.0
            m, _tip = curl_digit(model, fg, mats["wrist"], curl)
            mats.update(m)
    report.update(pick=pick, wrist=mats["wrist"], cost=0.0,
                  lowest_other_tip=min(model.tip(mats, fg).z for fg in ("middle", "ring", "pinky")) - neck.string_height)
    return mats, report


# ----------------------------------------------------------------------------------------------------------------------
# writing a pose into Blender: IK from the computed pose, bake, key
# ----------------------------------------------------------------------------------------------------------------------
def ensure_empty(name, collection, parent, size=0.004, kind="SPHERE"):
    ob = bpy.data.objects.get(name)
    if ob is None:
        ob = bpy.data.objects.new(name, None)
        collection.objects.link(ob)
        ob.empty_display_type = kind
        ob.empty_display_size = size
        ob.parent = parent
    return ob


def add_ik(arm, hand, collection, neck_obj):
    """One IK constraint per digit on its distal bone (chain 3, pole target), switched off until a pose is solved."""
    controls = {}
    for digit in rig.DIGITS:
        target = ensure_empty("ik_%s_%s" % (hand, digit), collection, neck_obj)
        pole = ensure_empty("pole_%s_%s" % (hand, digit), collection, neck_obj, size=0.003, kind="PLAIN_AXES")
        pb = arm.pose.bones[digit + "_distal"]
        con = pb.constraints.new("IK")
        con.name = "GhostIK"
        con.target, con.pole_target = target, pole
        con.pole_angle = -90.0 * DEG       # the knuckle side of every digit is its bones' -Z
        con.chain_count = 3
        con.use_tail, con.use_stretch, con.use_rotation = True, False, False
        con.iterations = 500
        con.enabled = False
        controls[digit] = (target, pole, con)
        for part in ("intermediate", "distal"):
            hinge = arm.pose.bones["%s_%s" % (digit, part)]
            if digit != "thumb":           # finger joints are hinges
                hinge.lock_ik_y = hinge.lock_ik_z = True
            hinge.ik_stretch = 0.0
        arm.pose.bones[digit + "_proximal"].ik_stiffness_y = 0.9
    return controls


def apply_mats(arm, model, mats):
    """Set every pose bone from armature-space matrices (parents first), as plain location/rotation values."""
    posed = {}
    for name in rig.BONE_NAMES:
        parent = model.parent[name]
        before = model.rest[name] if parent is None else posed[parent] @ model.rel[name]
        basis = before.inverted() @ mats[name]
        pb = arm.pose.bones[name]
        pb.rotation_mode = "QUATERNION"
        pb.rotation_quaternion = basis.to_quaternion()
        pb.location = basis.translation if parent is None else (0.0, 0.0, 0.0)
        pb.scale = (1.0, 1.0, 1.0)
        rot = basis.to_quaternion().to_matrix().to_4x4()
        if parent is None:
            rot.translation = basis.translation
        posed[name] = before @ rot
    return posed


def read_mats(arm):
    bpy.context.view_layer.update()
    ev = arm.evaluated_get(bpy.context.evaluated_depsgraph_get())
    return {name: ev.pose.bones[name].matrix.copy() for name in rig.BONE_NAMES}


_LAST_KEY = {}


def pose_and_key(arm, model, controls, mats, frame, exact, use_ik=True):
    """mats -> pose bones; IK pulls the digits listed in 'exact' ({digit: target}) onto their targets; bake; key.

    Returns {digit: (tip position, change IK made in mm)}.
    """
    apply_mats(arm, model, mats)
    stats = {}
    for digit in rig.DIGITS:
        target, pole, con = controls[digit]
        names = model.chain(digit)
        tip = model.tip(mats, digit)
        goal = exact.get(digit, tip)
        knee = mats[names[1]].translation
        dorsal = -(mats[names[0]].to_3x3().col[2] + mats[names[1]].to_3x3().col[2]).normalized()
        target.location = goal
        pole.location = knee + 0.08 * dorsal
        con.enabled = bool(use_ik)
    if use_ik:
        solved = read_mats(arm)
        for digit in rig.DIGITS:
            controls[digit][2].enabled = False
        twist = 0.0
        for name in rig.BONE_NAMES:
            twist = max(twist, (solved[name].to_3x3().normalized().to_quaternion().rotation_difference(mats[name].to_3x3().to_quaternion())).angle / DEG)
        if twist > 12.0:   # the IK left the computed pose: keep the computed one (it is exact by construction)
            print("    note: Blender IK moved a joint by %.1f deg; kept the computed pose instead" % twist)
        else:
            apply_mats(arm, model, solved)
    final = read_mats(arm)
    for digit in rig.DIGITS:
        tip = final[digit + "_distal"] @ Vector((0.0, model.length[digit + "_distal"], 0.0))
        stats[digit] = (tip, (tip - model.tip(mats, digit)).length / MM)
    for name in rig.BONE_NAMES:
        pb = arm.pose.bones[name]
        last = _LAST_KEY.get((arm.name, name))    # keep neighbouring keys on the same side, so in-betweens take the short way
        q = pb.rotation_quaternion.copy()
        if last is not None and last.dot(q) < 0.0:
            q.negate()
            pb.rotation_quaternion = q
        _LAST_KEY[(arm.name, name)] = q
        pb.keyframe_insert("rotation_quaternion", frame=frame)
        if name == "wrist":
            pb.keyframe_insert("location", frame=frame)
    for digit in rig.DIGITS:
        controls[digit][0].keyframe_insert("location", frame=frame)
        controls[digit][1].keyframe_insert("location", frame=frame)
    return stats


# ----------------------------------------------------------------------------------------------------------------------
# scene
# ----------------------------------------------------------------------------------------------------------------------
def dress_neck(neck):
    """Dim emissive materials for the mock-up (there are no lights: an unlit object would be black = invisible)."""
    looks = {"Fretboard": ((0.020, 0.015, 0.012), 1.0), "Nut": ((0.30, 0.29, 0.26), 1.0), "Fret_": ((0.16, 0.17, 0.19), 1.0),
             "String_": ((0.75, 0.62, 0.30), 1.0), "Guide_SoundholeRing": ((0.20, 0.18, 0.14), 1.0),
             "Guide_Top": ((0.0045, 0.0040, 0.0036), 1.0), "Guide_": ((0.010, 0.011, 0.013), 1.0)}
    mats = {key: ghost_look.make_flat_material("Mock_" + key.rstrip("_"), rgb, strength) for key, (rgb, strength) in looks.items()}
    for ob in bpy.data.objects:
        if ob.type != "MESH" or ob.parent is not neck.obj:
            continue
        for key, mat in mats.items():       # an exact name first, then the first prefix that fits
            if ob.name == key or (key.endswith("_") and ob.name.startswith(key)):
                ob.data.materials.clear()
                ob.data.materials.append(mat)
                break


def add_guides(neck, collection):
    """Things the mock-up does not have but the eye needs: the back of the neck (where the thumb rests) and,
    when the mock-up has a Soundhole marker, a solid soundhole ring and a patch of the guitar's top."""
    def mesh_object(name, verts, faces):
        me = bpy.data.meshes.new(name)
        me.from_pydata(verts, [], faces)
        me.update()
        ob = bpy.data.objects.new(name, me)
        collection.objects.link(ob)
        ob.parent = neck.obj
        ob.hide_select = True
        return ob

    x_end = neck.fret_x(5) + 20.0 * MM
    below = 8.0 * MM          # the mock-up's fretboard slab is this thick
    rings = []
    for x in (0.0, x_end):
        pts = [p for p in neck.profile(x) if p[1] < -below + 1e-9]
        hw = neck.half_width(x)
        rings.append([(x, -hw, -below)] + [(x, y, z) for (y, z) in reversed(pts)] + [(x, hw, -below)])
    n = len(rings[0])
    verts = rings[0] + rings[1]
    faces = [(i, (i + 1) % n, n + (i + 1) % n, n + i) for i in range(n)]
    faces += [tuple(range(n)), tuple(range(2 * n - 1, n - 1, -1))]
    ob = mesh_object("Guide_NeckBack", verts, faces)
    ob["note"] = "ASSUMED back of the neck (--neck-depth): the thumb rests on it. Not part of the mock-up; not exported."
    if neck.soundhole_x is None:
        return
    sx, r, top_z = neck.soundhole_x, neck.soundhole_radius, -7.0 * MM   # the top is about a fretboard below the frets
    seg, verts, faces = 48, [], []
    for k in range(seg):
        a = 2.0 * math.pi * k / seg
        for rad in (r, r + 3.0 * MM):
            verts.append((sx + rad * math.cos(a), rad * math.sin(a), top_z))
    for k in range(seg):
        a, b = 2 * k, 2 * ((k + 1) % seg)
        faces.append((a, a + 1, b + 1, b))
    ring = mesh_object("Guide_SoundholeRing", verts, faces)
    ring["note"] = "the soundhole edge at the ASSUMED/measured nut-to-soundhole distance, for the eye only"
    w = 0.19   # a patch of the guitar's top around the soundhole, with the hole cut out
    verts, faces = [], []
    for k in range(seg):
        a = 2.0 * math.pi * k / seg
        verts.append((sx + (r + 3.0 * MM) * math.cos(a), (r + 3.0 * MM) * math.sin(a), top_z))
        c, s = math.cos(a), math.sin(a)
        m = max(abs(c), abs(s))
        verts.append((sx + w * c / m, w * s / m, top_z))
    for k in range(seg):
        a, b = 2 * k, 2 * ((k + 1) % seg)
        faces.append((a, a + 1, b + 1, b))
    mesh_object("Guide_Top", verts, faces)


def debug_views(scene, folder, label, focus):
    cam = ghost_look.ensure_camera(scene, "DebugCam")
    views = {
        "front": ((focus.x + 0.10, focus.y - 0.16, 0.42), (focus.x, focus.y + 0.02, 0.0), (0.0, -1.0, 0.0), 60.0),
        "along": ((-0.26, focus.y + 0.05, 0.03), (focus.x, focus.y + 0.02, 0.0), (0.0, -1.0, 0.0), 60.0),
        "player": ((focus.x + 0.33, -0.42, 0.07), (focus.x, focus.y, 0.0), (0.0, -1.0, 0.0), 60.0),
    }
    for key, (eye, target, up, lens) in views.items():
        ghost_look.aim_camera(cam, eye, target, up=up, lens=lens)
        ghost_look.render_to(scene, cam, os.path.join(folder, "%s_%s.png" % (label, key)))


def parse_args(argv):
    ap = argparse.ArgumentParser(prog="pose_hands.py", description="Pose both ghost hands on the neck mock-up (see the file header).")
    ap.add_argument("--neck", required=True)
    ap.add_argument("--out", default=None)
    ap.add_argument("--hand-length", type=float, default=rig.REFERENCE_HAND_LENGTH_MM)
    ap.add_argument("--neck-depth", type=float, default=22.0)
    ap.add_argument("--only", default=None)
    ap.add_argument("--debug-renders", default=None)
    ap.add_argument("--no-ik", action="store_true")
    p = ap.parse_args(argv)
    p.neck = os.path.abspath(p.neck)
    if not os.path.isfile(p.neck):
        raise PoseError("no neck mock-up at %s: build it first with art/blender/build_neck_mockup.py" % p.neck)
    if p.out is None:
        p.out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ghost_hands.blend")
    p.out = os.path.abspath(p.out)
    p.only = [s.strip() for s in p.only.split(",") if s.strip()] if p.only else None
    return p


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = parse_args(argv)
    if not bpy.app.background:
        raise PoseError("run this script headless (see the usage at the top): it replaces the open scene")
    bpy.ops.wm.open_mainfile(filepath=p.neck)
    scene = bpy.context.scene
    neck_obj = bpy.data.objects.get("Neck")
    if neck_obj is None:
        raise PoseError("%s has no object named 'Neck': it is not a mock-up from build_neck_mockup.py" % p.neck)
    neck = Neck(neck_obj, p.neck_depth)
    hands_col = bpy.data.collections.new("GhostHands")
    scene.collection.children.link(hands_col)
    ik_col = bpy.data.collections.new("GhostHands_IK")
    scene.collection.children.link(ik_col)

    guide_col = bpy.data.collections.new("GhostHands_Guides")
    scene.collection.children.link(guide_col)
    ghost_look.setup_scene_look(scene)
    ghost = ghost_look.make_ghost_material()
    add_guides(neck, guide_col)
    dress_neck(neck)
    hands = {}
    for hand, side, obj_name in (("fretting", -1, "GhostHand_Fretting"), ("strumming", 1, "GhostHand_Strumming")):
        arm, mesh, info = rig.build_hand(side, obj_name, hands_col, p.hand_length)
        arm.parent = neck_obj
        mesh.data.materials.append(ghost)
        arm.hide_render = True
        hands[hand] = dict(arm=arm, mesh=mesh, model=RigModel(arm), controls=add_ik(arm, hand, ik_col, neck_obj), info=info)
        print("%s: %s hand, %d triangles" % (obj_name, info["side"], info["triangles"]))

    wanted = p.only or list(FRAMES)
    unknown = [w for w in wanted if w not in FRAMES]
    if unknown:
        raise PoseError("unknown pose name(s) %s; the poses are: %s" % (", ".join(unknown), ", ".join(FRAMES)))
    fret, strum = hands["fretting"], hands["strumming"]
    solved = {}                                   # label -> (matrices, report, fingertip targets)
    for pose in STRUMMING_POSES:
        label = "strumming-" + pose
        if label in wanted or pose == "rest":
            mats, report = solve_strumming(strum["model"], neck, pose)
            solved[label] = (mats, report, {})
    for pose in FRETTING_POSES:
        label = "fretting-" + pose
        if label not in wanted:
            continue
        targets = {}
        for ob in bpy.data.objects:
            if ob.name.startswith("tip_%s_f" % pose) and "finger" in ob.keys():
                targets[FINGER_OF[int(ob["finger"])]] = (neck_obj.matrix_world.inverted() @ ob.matrix_world).translation
        if pose != "rest" and not targets:
            raise PoseError("the mock-up has no targets tip_%s_f*: rebuild it with --chords including %s" % (pose, pose))
        mats, report = solve_fretting(fret["model"], neck, pose, targets)
        solved[label] = (mats, report, targets)

    # key in frame order. Each hand also holds a pose at the other hand's markers (the fretting hand holds G, the
    # strumming hand rests), so stepping through the markers never shows a half-way hand.
    hold = (solved.get("fretting-G") or solved.get("fretting-rest") or (None,))[0]
    idle = solved["strumming-rest"][0]
    failures = []
    for label in sorted((name for name in FRAMES if name in wanted), key=lambda name: FRAMES[name]):
        frame = FRAMES[label]
        mats, report, targets = solved[label]
        fretting = label.startswith("fretting-")
        hand, other, other_mats = (fret, strum, idle) if fretting else (strum, fret, hold)
        stats = pose_and_key(hand["arm"], hand["model"], hand["controls"], mats, frame, targets, use_ik=not p.no_ik)
        if other_mats is not None:
            pose_and_key(other["arm"], other["model"], other["controls"], other_mats, frame, {}, use_ik=False)
        scene.timeline_markers.new(label, frame=frame)
        if fretting:
            print("%s (frame %d): palm cost %.2f" % (label, frame, report["cost"]))
            for digit in rig.DIGITS:
                res = report["fingers"].get(digit)
                line = "    %-6s" % digit
                if res is not None:
                    line += " knuckle %5.1f  middle %5.1f  last %5.1f  spread %5.1f deg" % (res["fl"][0], res["fl"][1], res["fl"][2], res["phi"])
                if digit in targets:
                    err = (stats[digit][0] - targets[digit]).length / MM
                    line += "   tip %.3f mm from its dot" % err
                    if err > 1.0:
                        failures.append("%s %s %.2f mm" % (label, digit, err))
                print(line)
        else:
            pk, wr = report["pick"], report["wrist"].translation
            print("%s (frame %d): pick point x %.1f  y %.1f  z %.1f mm; wrist x %.1f  y %.1f  z %.1f mm; other fingertips %.1f mm or more above the strings; IK moved the tips by up to %.3f mm" % (
                label, frame, pk.x / MM, pk.y / MM, pk.z / MM, wr.x / MM, wr.y / MM, wr.z / MM, report["lowest_other_tip"] / MM,
                max(v[1] for v in stats.values())))
        if p.debug_renders:
            scene.frame_set(frame)
            other["mesh"].hide_render = True
            focus = Vector((0.06, 0.0, 0.0)) if fretting else Vector((strum_line_x(neck), 0.0, 0.0))
            debug_views(scene, os.path.abspath(p.debug_renders), label, focus)
            other["mesh"].hide_render = False

    scene.frame_start, scene.frame_end = 1, 120
    scene.frame_set(FRAMES["fretting-G"] if "fretting-G" in wanted else 1)
    os.makedirs(os.path.dirname(p.out), exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=p.out, check_existing=False)
    print("wrote %s" % p.out)
    if failures:
        raise PoseError("fingertips off their dots: " + "; ".join(failures))


if __name__ == "__main__":
    try:
        main()
    except (PoseError, rig.BuildError) as e:
        sys.stderr.write("pose_hands.py: ERROR: %s\n" % e)
        sys.exit(1)
