"""
render_previews.py - render every keyed pose of ghost_hands.blend in the ghost look, plus one contact sheet. Headless.

USAGE (Blender 5.2; options after the "--"; --python-exit-code must come before --python):

  /Applications/Blender.app/Contents/MacOS/Blender --factory-startup -b art/blender/hand/ghost_hands.blend \
      --python-exit-code 1 --python art/blender/hand/render_previews.py -- [--out-dir DIR] [--only LIST]

  --out-dir DIR   default: renders/ next to this script
  --only LIST     comma list of marker names, e.g. fretting-G,fretting-Em (default: every pose marker in the file)
  --width / --height   default 960 x 540

It renders what is in the .blend, so it also shows poses you changed by hand. Nothing is saved back into the .blend.
One PNG per marker, <hand>-<pose>.png, from the player's side of the guitar (headstock on the left, as the wearer of
the glasses sees it): the fretting hand close up at the first frets, the strumming hand close up at the soundhole.
contact-sheet.png puts all of them on one page. Black is "not drawn" on the glasses: the neck is shown dim on purpose.
"""

import argparse
import os
import sys

import bpy
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ghost_look  # noqa: E402

HANDS = {"fretting": "GhostHand_Fretting", "strumming": "GhostHand_Strumming"}


def camera_for(hand, soundhole_x):
    """(eye, target, up, lens): the player's side, leaning in enough to see the strings."""
    if hand == "fretting":
        target = Vector((0.068, 0.012, 0.004))
        return target + Vector((0.20, -0.35, 0.235)), target, (0.0, 0.0, 1.0), 62.0
    target = Vector((soundhole_x - 0.030, 0.018, 0.025))
    return target + Vector((0.17, -0.37, 0.27)), target, (0.0, 0.0, 1.0), 62.0


def contact_sheet(paths, out_path, columns=4, pad=6):
    """Paste the PNGs into one grid with numpy (no extra packages needed)."""
    import numpy as np
    tiles = []
    for path in paths:
        img = bpy.data.images.load(path, check_existing=False)
        w, h = img.size
        px = np.empty(w * h * 4, dtype=np.float32)
        img.pixels.foreach_get(px)
        tiles.append(px.reshape(h, w, 4)[::2, ::2, :])   # half size
        bpy.data.images.remove(img)
    th, tw = tiles[0].shape[0], tiles[0].shape[1]
    rows = (len(tiles) + columns - 1) // columns
    sheet = np.zeros((rows * th + (rows + 1) * pad, columns * tw + (columns + 1) * pad, 4), dtype=np.float32)
    sheet[:, :, 3] = 1.0
    sheet[:, :, 0:3] = 0.035
    for i, tile in enumerate(tiles):
        r, c = divmod(i, columns)
        y0 = sheet.shape[0] - (r + 1) * (th + pad)       # image rows run bottom-up
        x0 = pad + c * (tw + pad)
        sheet[y0:y0 + th, x0:x0 + tw, :] = tile
    out = bpy.data.images.new("contact", width=sheet.shape[1], height=sheet.shape[0], alpha=False)
    out.pixels.foreach_set(sheet.ravel())
    out.filepath_raw = out_path
    out.file_format = "PNG"
    out.save()
    bpy.data.images.remove(out)
    return out_path


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    ap = argparse.ArgumentParser(prog="render_previews.py")
    ap.add_argument("--out-dir", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "renders"))
    ap.add_argument("--only", default=None)
    ap.add_argument("--width", type=int, default=960)
    ap.add_argument("--height", type=int, default=540)
    ap.add_argument("--no-sheet", action="store_true")
    p = ap.parse_args(argv)
    out_dir = os.path.abspath(p.out_dir)
    scene = bpy.context.scene
    if not bpy.data.filepath:
        sys.stderr.write("render_previews.py: ERROR: open ghost_hands.blend first (see the usage at the top)\n")
        sys.exit(1)
    only = [s.strip() for s in p.only.split(",") if s.strip()] if p.only else None
    ghost_look.setup_scene_look(scene, p.width, p.height)
    sound = bpy.data.objects.get("Soundhole")
    soundhole_x = sound.location.x if sound is not None else 0.44
    for ob in bpy.data.objects:                      # helpers never show up in a render
        if ob.type in ("EMPTY", "ARMATURE"):
            ob.hide_render = True
    cam = ghost_look.ensure_camera(scene)
    markers = sorted(scene.timeline_markers, key=lambda m: m.frame)
    written = []
    for m in markers:
        hand = m.name.split("-", 1)[0]
        if hand not in HANDS or (only and m.name not in only):
            continue
        scene.frame_set(m.frame)
        for h, name in HANDS.items():                # one hand per picture
            for ob in bpy.data.objects:
                if ob.name == name + "_Mesh":
                    ob.hide_render = (h != hand)
        eye, target, up, lens = camera_for(hand, soundhole_x)
        ghost_look.aim_camera(cam, eye, target, up=up, lens=lens)
        path = os.path.join(out_dir, m.name + ".png")
        ghost_look.render_to(scene, cam, path)
        written.append(path)
        print("wrote %s (%d KB)" % (path, os.path.getsize(path) // 1024))
    if not written:
        sys.stderr.write("render_previews.py: ERROR: no pose markers (fretting-*, strumming-*) in %s\n" % bpy.data.filepath)
        sys.exit(1)
    if not p.no_sheet and not only:
        sheet = contact_sheet(written, os.path.join(out_dir, "contact-sheet.png"))
        print("wrote %s (%d KB)" % (sheet, os.path.getsize(sheet) // 1024))


if __name__ == "__main__":
    main()
