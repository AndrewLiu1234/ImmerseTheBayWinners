"""
build_neck_mockup.py - build a 1:1 guitar-neck mock-up in Blender, headless, and save art/blender/neck_mockup.blend.

WHY: the ghost-hand poses (docs/GHOST_HANDS.md section 5 and 7) are authored on a mock-up that has the real guitar's
scale length, nut width and string spread, so every fingertip target sits where the real fingertip dot will be.

USAGE (run it from the repo root; options come after the "--"; millimetres everywhere):

  /Applications/Blender.app/Contents/MacOS/Blender --factory-startup -b --python-exit-code 1 \
      --python art/blender/build_neck_mockup.py -- [options]

  --scale-length MM     nut-to-saddle distance L                       default 648    (25.5 in; Appendix B4)
  --nut-width MM        fretboard width at the nut                      default 43
  --nut-spread MM       string 1 to string 6 centre distance at nut     default 35
  --saddle-spread MM    the same at the saddle                          default 53
  --frets N             frets to build, 1..12                           default 5
  --string-height MM    string centre line above the fretboard          default 1.5
  --tip-fraction F      fingertip target, fraction of the way from fret n-1 to fret n   default 0.75
  --chords LIST         comma list from G,C,D,Em,Am,E,A,Dm             default G,C,D,Em,Am
  --nut-to-soundhole MM optional: adds a 'Soundhole' ring marker for the strumming hand
  --soundhole-diameter MM                                               default 100
  --out PATH            output .blend                                   default art/blender/neck_mockup.blend

It must run headless: it starts from an empty factory scene, which would wipe an open GUI session. Re-running replaces
the output file (Blender keeps the previous one as neck_mockup.blend1), so do your hand work in a copy:
open the result and use File > Save As... (for example hand_poses.blend) before you add the hand.

THE NECK FRAME (Blender axes; the object 'Neck' is its origin; 1 Blender unit = 1 m, the scene shows mm):
  origin  centre line of the neck, at the nut face, on the fretboard's playing surface
  +X      along the neck, from the nut toward the bridge: fret n is at X = L * (1 - 2^(-n/12))
  +Y      across the neck, toward string 1 (the high E). From above (Top view, nut on the left) string 1 is at the top.
  +Z      up out of the fretboard (toward the player's hand)
Unity gets the same frame as (x, y, z)_unity = (X, Z, Y)_blender; docs in unity/Assets/StreamingAssets/poses/README.md.

WHAT IT BUILDS: Neck (empty, the frame), Fretboard, Nut, Fret_01.., String_1..String_6 (thin cylinders running straight
from the nut spacing to the saddle spacing) and one named empty per fingertip target: tip_<chord>_f<finger>, at
tip-fraction of the way from fret n-1 to fret n, on the right string, at the string's height (Appendix B2 and B4).
Targets are grouped in one collection per chord (Targets_G, ...): hide the others while you pose. Each empty carries
custom properties chord, finger, string, fret. Finger 1 = index ... 4 = pinky.
"""

import argparse
import math
import os
import sys

import bmesh
import bpy
from mathutils import Vector

SCRIPT_VERSION = "1.0.0"
MM = 0.001  # Blender unit = 1 m; every length below is written in millimetres and multiplied by MM when it is used

# docs/MASTER_PLAN.md Appendix B2, exactly as printed: strings listed 6 -> 1; x = not played, 0 = open, - = no finger.
# Artist reference data (art/ may hold it; Runtime code may not). If Appendix B2 changes, change these rows with it.
B2 = {
    "Em": ("0 2 2 0 0 0", "- 2 3 - - -"),
    "G": ("3 2 0 0 0 3", "2 1 - - - 3"),
    "C": ("x 3 2 0 1 0", "x 3 2 - 1 -"),
    "D": ("x x 0 2 3 2", "x x - 1 3 2"),
    "Am": ("x 0 2 2 1 0", "x - 2 3 1 -"),
    "E": ("0 2 2 1 0 0", "- 2 3 1 - -"),
    "A": ("x 0 2 2 2 0", "x - 1 2 3 -"),
    "Dm": ("x x 0 2 3 1", "x x - 2 3 1"),
}
# String radius in mm for strings 1..6 (a light acoustic set, a little thicker than real so it stays visible)
STRING_RADIUS_MM = {1: 0.20, 2: 0.25, 3: 0.33, 4: 0.40, 5: 0.50, 6: 0.60}
FINGER_NAMES = {1: "index", 2: "middle", 3: "ring", 4: "pinky"}


class UserError(Exception):
    """A mistake in the command line or the data: reported on one line, exit code 1."""


# ----------------------------------------------------------------------------------------------------------------------
# geometry (pure functions, millimetres)
# ----------------------------------------------------------------------------------------------------------------------
def fret_distance_mm(scale_length, n):
    """Appendix B4: distance from the nut to fret n, d(n) = L * (1 - 2^(-n/12))."""
    return scale_length * (1.0 - 2.0 ** (-n / 12.0))


def target_x_mm(scale_length, fret, fraction):
    """Fingertip target along the neck: fraction of the way from fret n-1 to fret n."""
    a = fret_distance_mm(scale_length, fret - 1)
    b = fret_distance_mm(scale_length, fret)
    return a + fraction * (b - a)


def string_y_mm(string_no, x_mm, p):
    """Across-neck position of string 1..6 at distance x from the nut (+ = toward string 1). Straight line nut -> saddle."""
    offset = 3.5 - string_no  # string 1 -> +2.5, string 6 -> -2.5
    at_nut = offset * p.nut_spread / 5.0
    at_saddle = offset * p.saddle_spread / 5.0
    return at_nut + (at_saddle - at_nut) * (x_mm / p.scale_length)


def board_width_mm(x_mm, p):
    """The fretboard widens at the same rate as the string spread (constant edge margin)."""
    return p.nut_width + (p.saddle_spread - p.nut_spread) * (x_mm / p.scale_length)


def parse_chord(name):
    """B2 row -> list of (string, fret, finger) for the fingered strings only."""
    frets, fingers = B2[name]
    frets, fingers = frets.split(), fingers.split()
    if len(frets) != 6 or len(fingers) != 6:
        raise UserError("chord table row for %s does not have six strings" % name)
    out = []
    for k in range(6):
        string_no = 6 - k
        if fingers[k].isdigit():
            fret = int(frets[k])
            if fret < 1:
                raise UserError("chord table: %s string %d has a finger on an open string" % (name, string_no))
            out.append((string_no, fret, int(fingers[k])))
        elif frets[k] not in ("x", "0"):
            raise UserError("chord table: %s string %d is fretted without a finger" % (name, string_no))
    return out


def compute_targets(p):
    """All fingertip targets: list of dicts with chord, finger, string, fret and x/y/z in mm (Blender neck frame)."""
    targets = []
    for chord in p.chord_list:
        for string_no, fret, finger in parse_chord(chord):
            x = target_x_mm(p.scale_length, fret, p.tip_fraction)
            targets.append({"chord": chord, "finger": finger, "string": string_no, "fret": fret,
                            "x": x, "y": string_y_mm(string_no, x, p), "z": p.string_height})
    return targets


# ----------------------------------------------------------------------------------------------------------------------
# Blender helpers
# ----------------------------------------------------------------------------------------------------------------------
def make_mesh(name, verts_mm, faces=(), edges=()):
    me = bpy.data.meshes.new(name)
    me.from_pydata([(x * MM, y * MM, z * MM) for (x, y, z) in verts_mm], list(edges), list(faces))
    me.update()
    if faces:
        bm = bmesh.new()
        bm.from_mesh(me)
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)  # outward normals whatever the winding above
        bm.to_mesh(me)
        bm.free()
    return me


def box_mesh(name, x0, x1, y0, y1, z0, z1):
    verts = [(x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0), (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)]
    faces = [(0, 1, 2, 3), (4, 7, 6, 5), (0, 4, 5, 1), (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)]
    return make_mesh(name, verts, faces)


def cylinder_mesh(name, a_mm, b_mm, radius_mm, sides=10):
    a, b = Vector(a_mm), Vector(b_mm)
    axis = (b - a).normalized()
    ref = Vector((0.0, 0.0, 1.0)) if abs(axis.z) < 0.9 else Vector((1.0, 0.0, 0.0))
    u = axis.cross(ref).normalized()
    v = axis.cross(u).normalized()
    verts = []
    for centre in (a, b):
        for k in range(sides):
            ang = 2.0 * math.pi * k / sides
            q = centre + radius_mm * (math.cos(ang) * u + math.sin(ang) * v)
            verts.append((q.x, q.y, q.z))
    faces = [(k, (k + 1) % sides, sides + (k + 1) % sides, sides + k) for k in range(sides)]
    faces.append(tuple(range(sides)))
    faces.append(tuple(range(2 * sides - 1, sides - 1, -1)))
    return make_mesh(name, verts, faces)


def ring_mesh(name, radius_mm, segments=64):
    verts = [(radius_mm * math.cos(2 * math.pi * k / segments), radius_mm * math.sin(2 * math.pi * k / segments), 0.0)
             for k in range(segments)]
    edges = [(k, (k + 1) % segments) for k in range(segments)]
    return make_mesh(name, verts, (), edges)


def material(name, rgb):
    mat = bpy.data.materials.new(name)
    mat.diffuse_color = (rgb[0], rgb[1], rgb[2], 1.0)  # viewport colour (solid shading, "Material" colour mode)
    return mat


def add_object(name, data, collection, parent, mat=None):
    obj = bpy.data.objects.new(name, data)
    collection.objects.link(obj)
    obj.parent = parent
    if mat is not None and data is not None:
        data.materials.append(mat)
    return obj


# ----------------------------------------------------------------------------------------------------------------------
# the build
# ----------------------------------------------------------------------------------------------------------------------
def build(p):
    bpy.ops.wm.read_factory_settings(use_empty=True)  # headless only (checked in main): this empties the session
    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.length_unit = "MILLIMETERS"  # shown in mm; 1 Blender unit stays 1 m so FBX export and Unity see metres
    scene.unit_settings.scale_length = 1.0

    root_col = bpy.data.collections.new("GuitarNeck")
    scene.collection.children.link(root_col)
    targets_col = bpy.data.collections.new("Targets")
    scene.collection.children.link(targets_col)

    neck = bpy.data.objects.new("Neck", None)
    neck.empty_display_type = "ARROWS"
    neck.empty_display_size = 0.03
    root_col.objects.link(neck)
    for key, value in (("scale_length_mm", p.scale_length), ("nut_width_mm", p.nut_width), ("nut_spread_mm", p.nut_spread),
                       ("saddle_spread_mm", p.saddle_spread), ("string_height_mm", p.string_height),
                       ("frets", p.frets), ("tip_fraction", p.tip_fraction), ("builder", "build_neck_mockup.py " + SCRIPT_VERSION)):
        neck[key] = value

    m_board = material("neck_board", (0.20, 0.11, 0.06))
    m_nut = material("neck_nut", (0.90, 0.88, 0.80))
    m_fret = material("neck_fret", (0.75, 0.77, 0.80))
    m_string = material("neck_string", (0.95, 0.80, 0.35))

    x_end = fret_distance_mm(p.scale_length, p.frets) + 20.0
    t = 8.0  # fretboard thickness below the playing surface
    w0, w1 = board_width_mm(0.0, p), board_width_mm(x_end, p)
    board_verts = [(0, -w0 / 2, -t), (0, w0 / 2, -t), (x_end, w1 / 2, -t), (x_end, -w1 / 2, -t),
                   (0, -w0 / 2, 0), (0, w0 / 2, 0), (x_end, w1 / 2, 0), (x_end, -w1 / 2, 0)]
    board_faces = [(0, 1, 2, 3), (4, 7, 6, 5), (0, 4, 5, 1), (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)]
    add_object("Fretboard", make_mesh("Fretboard", board_verts, board_faces), root_col, neck, m_board)

    add_object("Nut", box_mesh("Nut", -3.0, 0.0, -w0 / 2, w0 / 2, -t, 2.5), root_col, neck, m_nut)  # nut face at X = 0

    for n in range(1, p.frets + 1):
        x = fret_distance_mm(p.scale_length, n)
        w = board_width_mm(x, p)
        obj = add_object("Fret_%02d" % n, box_mesh("Fret_%02d" % n, x - 1.0, x + 1.0, -w / 2, w / 2, 0.0, 1.0), root_col, neck, m_fret)
        obj["fret"] = n
        obj["distance_mm"] = x

    for s in range(1, 7):
        a = (0.0, string_y_mm(s, 0.0, p), p.string_height)
        b = (p.scale_length, string_y_mm(s, p.scale_length, p), p.string_height)
        obj = add_object("String_%d" % s, cylinder_mesh("String_%d" % s, a, b, STRING_RADIUS_MM[s]), root_col, neck, m_string)
        obj["string"] = s

    chord_cols = {}
    targets = compute_targets(p)
    for tg in targets:
        col = chord_cols.get(tg["chord"])
        if col is None:
            col = bpy.data.collections.new("Targets_%s" % tg["chord"])
            targets_col.children.link(col)
            chord_cols[tg["chord"]] = col
        name = "tip_%s_f%d" % (tg["chord"], tg["finger"])
        if name in bpy.data.objects:
            name += "_s%d" % tg["string"]  # a barre chord would put one finger on several strings
        obj = bpy.data.objects.new(name, None)
        obj.empty_display_type = "SPHERE"
        obj.empty_display_size = 1.5 * MM
        obj.show_name = True
        obj.show_in_front = True
        col.objects.link(obj)
        obj.parent = neck
        obj.location = (tg["x"] * MM, tg["y"] * MM, tg["z"] * MM)
        obj["chord"], obj["finger"], obj["string"], obj["fret"] = tg["chord"], tg["finger"], tg["string"], tg["fret"]
        obj["finger_name"] = FINGER_NAMES[tg["finger"]]

    if p.nut_to_soundhole:
        obj = add_object("Soundhole", ring_mesh("Soundhole", p.soundhole_diameter / 2.0), root_col, neck)
        obj.location = (p.nut_to_soundhole * MM, 0.0, 0.0)
        obj["note"] = "centre of the soundhole at the measured nut-to-soundhole distance; strum zone = end of fretboard to here"

    doc = bpy.data.texts.new("NECK_FRAME")
    doc.use_fake_user = True
    doc.write(
        "Neck frame (Blender axes). Origin: nut face, neck centre line, fretboard surface.\n"
        "+X along the neck toward the bridge (fret n at L*(1-2^(-n/12))), +Y toward string 1 (high E), +Z up out of the fretboard.\n"
        "Unity (x, y, z) = (X, Z, Y). Pose JSON spec: unity/Assets/StreamingAssets/poses/README.md\n"
        "Fingertip targets: objects tip_<chord>_f<finger>, grouped in collections Targets_<chord>.\n"
        "Built by build_neck_mockup.py %s: L=%.3f nut=%.1f spread %.1f -> %.1f frets=%d\n"
        % (SCRIPT_VERSION, p.scale_length, p.nut_width, p.nut_spread, p.saddle_spread, p.frets))

    for screen in bpy.data.screens:  # millimetre-sized objects must not be clipped when you zoom in
        for area in screen.areas:
            if area.type == "VIEW_3D":
                for space in area.spaces:
                    if space.type == "VIEW_3D":
                        try:
                            space.clip_start = 0.0005
                            space.clip_end = 20.0
                        except Exception:
                            pass

    bpy.context.view_layer.objects.active = neck
    return targets


def check_against_appendix_b4(p, targets):
    """With the default guitar the numbers printed in docs/MASTER_PLAN.md Appendix B4 must come out (to 0.05 mm)."""
    if not (abs(p.scale_length - 648.0) < 1e-9 and abs(p.tip_fraction - 0.75) < 1e-9):
        return
    for n, expected in {1: 36.4, 2: 70.7, 3: 103.1, 4: 133.7, 5: 162.5}.items():
        if n <= p.frets and abs(fret_distance_mm(p.scale_length, n) - expected) > 0.05:
            raise UserError("fret %d distance %.3f mm differs from Appendix B4 (%.1f)" % (n, fret_distance_mm(p.scale_length, n), expected))
    for n, expected in {1: 27.3, 2: 62.1, 3: 95.0}.items():
        if abs(target_x_mm(p.scale_length, n, p.tip_fraction) - expected) > 0.05:
            raise UserError("fingertip target on fret %d at %.3f mm differs from Appendix B4 (%.1f)" % (n, target_x_mm(p.scale_length, n, p.tip_fraction), expected))
    print("check: matches the numbers printed in Appendix B4 (frets 1-5, fingertip targets on frets 1-3)")


# ----------------------------------------------------------------------------------------------------------------------
# command line
# ----------------------------------------------------------------------------------------------------------------------
def parse_args(argv):
    ap = argparse.ArgumentParser(prog="build_neck_mockup.py", description="Build the 1:1 neck mock-up (see the file header).")
    ap.add_argument("--scale-length", type=float, default=648.0)
    ap.add_argument("--nut-width", type=float, default=43.0)
    ap.add_argument("--nut-spread", type=float, default=35.0)
    ap.add_argument("--saddle-spread", type=float, default=53.0)
    ap.add_argument("--frets", type=int, default=5)
    ap.add_argument("--string-height", type=float, default=1.5)
    ap.add_argument("--tip-fraction", type=float, default=0.75)
    ap.add_argument("--chords", default="G,C,D,Em,Am")
    ap.add_argument("--nut-to-soundhole", type=float, default=0.0)
    ap.add_argument("--soundhole-diameter", type=float, default=100.0)
    ap.add_argument("--out", default=None)
    p = ap.parse_args(argv)
    if not 100.0 <= p.scale_length <= 1500.0:
        raise UserError("--scale-length %.1f mm is not a guitar (expected 100..1500)" % p.scale_length)
    if p.nut_width <= 0 or p.nut_spread <= 0 or p.saddle_spread <= 0:
        raise UserError("--nut-width, --nut-spread and --saddle-spread must be positive")
    if p.nut_spread > p.nut_width:
        raise UserError("--nut-spread (%.1f) cannot be wider than --nut-width (%.1f)" % (p.nut_spread, p.nut_width))
    if not 1 <= p.frets <= 12:
        raise UserError("--frets must be 1..12")
    if not 0.0 < p.tip_fraction < 1.0:
        raise UserError("--tip-fraction must be between 0 and 1 (0.75 = 75% of the way)")
    if p.string_height < 0:
        raise UserError("--string-height cannot be negative")
    p.chord_list = [c.strip() for c in p.chords.split(",") if c.strip()]
    for c in p.chord_list:
        if c not in B2:
            raise UserError("unknown chord %r (known: %s)" % (c, ", ".join(sorted(B2))))
        for _s, fret, _f in parse_chord(c):
            if fret > p.frets:
                raise UserError("chord %s needs fret %d but only %d frets are built (--frets)" % (c, fret, p.frets))
    if p.out is None:
        here = os.path.dirname(os.path.abspath(__file__)) if "__file__" in globals() and os.path.exists(__file__) else os.getcwd()
        p.out = os.path.join(here, "neck_mockup.blend")
    p.out = os.path.abspath(p.out)
    return p


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = parse_args(argv)
    if not bpy.app.background:
        raise UserError("run this script headless (see the usage at the top): it starts from an empty scene and would wipe the open one")
    targets = build(p)
    check_against_appendix_b4(p, targets)
    os.makedirs(os.path.dirname(p.out), exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=p.out, check_existing=False)
    print("Fret distances from the nut (mm): " + "  ".join("%d: %.2f" % (n, fret_distance_mm(p.scale_length, n)) for n in range(1, p.frets + 1)))
    print("Fingertip targets (mm along the neck / across, + = toward string 1 / above the board):")
    for tg in targets:
        print("  %-3s finger %d (%-6s) string %d fret %d  x=%7.2f  y=%6.2f  z=%4.2f" % (
            tg["chord"], tg["finger"], FINGER_NAMES[tg["finger"]], tg["string"], tg["fret"], tg["x"], tg["y"], tg["z"]))
    print("wrote %s" % p.out)


if __name__ == "__main__":
    try:
        main()
    except UserError as e:
        sys.stderr.write("build_neck_mockup.py: ERROR: %s\n" % e)
        sys.exit(1)
