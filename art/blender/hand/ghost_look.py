"""
ghost_look.py - the "ghost" look for previews (docs/GHOST_HANDS.md section 3) and small render helpers.

Shared by build_hand_rig.py, pose_hands.py and render_previews.py. Blender 5.2, EEVEE, headless.

The look: additive, glowing translucent blue over black, the way the XREAL glasses show it (black = invisible):
  fill  #4FC3F7 at low strength, rim #B3E5FC (facing-ratio rim, the silhouette carries the shape), bright fingertips.
The fingertip mask is read from the mesh's UV map 'GhostMask' (u = fingertip glow, v = forearm fade), the same data the
FBX carries for the Unity shader (README.md, "Vertex data").
"""

import math
import os

import bpy

FILL_HEX = "#4FC3F7"
RIM_HEX = "#B3E5FC"


def srgb_to_linear(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def hex_to_linear(hex_text):
    h = hex_text.lstrip("#")
    return tuple(srgb_to_linear(int(h[i:i + 2], 16) / 255.0) for i in (0, 2, 4))


def _new_material(name):
    old = bpy.data.materials.get(name)
    if old is not None:
        bpy.data.materials.remove(old)
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    mat.node_tree.nodes.clear()
    return mat


def make_ghost_material(name="GhostHand", fill=0.07, rim=1.7, tips=2.2, rim_power=2.4):
    """Additive emission: fill + facing-ratio rim + fingertip glow, faded toward the forearm stub."""
    mat = _new_material(name)
    nt = mat.node_tree
    n, link = nt.nodes, nt.links

    out = n.new("ShaderNodeOutputMaterial")
    add = n.new("ShaderNodeAddShader")
    transparent = n.new("ShaderNodeBsdfTransparent")
    emission = n.new("ShaderNodeEmission")
    link.new(add.outputs[0], out.inputs["Surface"])
    link.new(emission.outputs[0], add.inputs[0])
    link.new(transparent.outputs[0], add.inputs[1])

    layer = n.new("ShaderNodeLayerWeight")
    layer.inputs["Blend"].default_value = 0.5
    rim_pow = n.new("ShaderNodeMath")
    rim_pow.operation = "POWER"
    rim_pow.inputs[1].default_value = rim_power
    link.new(layer.outputs["Facing"], rim_pow.inputs[0])

    uv = n.new("ShaderNodeUVMap")
    uv.uv_map = "GhostMask"
    sep = n.new("ShaderNodeSeparateXYZ")
    link.new(uv.outputs["UV"], sep.inputs[0])

    colour = n.new("ShaderNodeMixRGB")
    colour.blend_type = "MIX"
    colour.inputs["Color1"].default_value = hex_to_linear(FILL_HEX) + (1.0,)
    colour.inputs["Color2"].default_value = hex_to_linear(RIM_HEX) + (1.0,)
    rim_or_tip = n.new("ShaderNodeMath")
    rim_or_tip.operation = "MAXIMUM"
    link.new(rim_pow.outputs[0], rim_or_tip.inputs[0])
    link.new(sep.outputs["X"], rim_or_tip.inputs[1])
    link.new(rim_or_tip.outputs[0], colour.inputs["Fac"])
    link.new(colour.outputs[0], emission.inputs["Color"])

    rim_term = n.new("ShaderNodeMath")
    rim_term.operation = "MULTIPLY_ADD"       # rim^p * rim + fill
    rim_term.inputs[1].default_value = rim
    rim_term.inputs[2].default_value = fill
    link.new(rim_pow.outputs[0], rim_term.inputs[0])
    tip_term = n.new("ShaderNodeMath")
    tip_term.operation = "MULTIPLY_ADD"       # tip mask * tips + (rim term)
    tip_term.inputs[1].default_value = tips
    link.new(sep.outputs["X"], tip_term.inputs[0])
    link.new(rim_term.outputs[0], tip_term.inputs[2])
    fade = n.new("ShaderNodeMath")
    fade.operation = "MULTIPLY"               # forearm fade (v): 1 on the hand, 0 at the end of the stub
    link.new(tip_term.outputs[0], fade.inputs[0])
    link.new(sep.outputs["Y"], fade.inputs[1])
    link.new(fade.outputs[0], emission.inputs["Strength"])

    mat.surface_render_method = "BLENDED"
    mat.use_backface_culling = False
    mat.use_transparency_overlap = True
    mat.diffuse_color = hex_to_linear(FILL_HEX) + (0.45,)  # solid-viewport colour
    return mat


def make_flat_material(name, rgb, strength=1.0, additive=False):
    """A plain emissive colour for the neck mock-up and guides (no lights in the scene: black is 'not there')."""
    mat = _new_material(name)
    nt = mat.node_tree
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    emission = nt.nodes.new("ShaderNodeEmission")
    emission.inputs["Color"].default_value = (rgb[0], rgb[1], rgb[2], 1.0)
    emission.inputs["Strength"].default_value = strength
    if additive:
        add = nt.nodes.new("ShaderNodeAddShader")
        transparent = nt.nodes.new("ShaderNodeBsdfTransparent")
        nt.links.new(emission.outputs[0], add.inputs[0])
        nt.links.new(transparent.outputs[0], add.inputs[1])
        nt.links.new(add.outputs[0], out.inputs["Surface"])
        mat.surface_render_method = "BLENDED"
        mat.use_transparency_overlap = True
    else:
        nt.links.new(emission.outputs[0], out.inputs["Surface"])
    mat.diffuse_color = (rgb[0], rgb[1], rgb[2], 1.0)
    return mat


def setup_scene_look(scene, width=960, height=540, samples=32):
    """Black world, EEVEE, Standard view transform (emissive colours are shown as authored), PNG output."""
    scene.render.engine = "BLENDER_EEVEE"
    scene.render.resolution_x = width
    scene.render.resolution_y = height
    scene.render.resolution_percentage = 100
    scene.render.film_transparent = False
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGB"
    scene.render.image_settings.compression = 90
    try:
        scene.eevee.taa_render_samples = samples
    except Exception:
        pass
    try:
        scene.view_settings.view_transform = "Standard"
        scene.view_settings.look = "None"
    except Exception:
        pass
    world = scene.world
    if world is None:
        world = bpy.data.worlds.new("GhostBlack")
        scene.world = world
    world.use_nodes = True
    nt = world.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputWorld")
    bg = nt.nodes.new("ShaderNodeBackground")
    bg.inputs["Color"].default_value = (0.0, 0.0, 0.0, 1.0)
    bg.inputs["Strength"].default_value = 0.0
    nt.links.new(bg.outputs[0], out.inputs["Surface"])


def ensure_camera(scene, name="PreviewCam"):
    cam = bpy.data.objects.get(name)
    if cam is None or cam.type != "CAMERA":
        cam = bpy.data.objects.new(name, bpy.data.cameras.new(name))
        scene.collection.objects.link(cam)
    cam.data.clip_start = 0.01
    cam.data.clip_end = 20.0
    return cam


def aim_camera(cam, eye, target, up=(0.0, 0.0, 1.0), lens=50.0):
    """Place the camera at 'eye' looking at 'target' (metres), with 'up' as the screen-up hint."""
    from mathutils import Matrix, Vector
    eye, target, up = Vector(eye), Vector(target), Vector(up)
    back = (eye - target).normalized()          # camera looks along its -Z
    right = up.cross(back)
    if right.length < 1e-6:
        right = Vector((1.0, 0.0, 0.0))
    right.normalize()
    true_up = back.cross(right)
    m = Matrix.Identity(4)
    for i in range(3):
        m[i][0], m[i][1], m[i][2], m[i][3] = right[i], true_up[i], back[i], eye[i]
    cam.matrix_world = m
    cam.data.lens = lens


def render_to(scene, cam, path):
    scene.camera = cam
    scene.render.filepath = path
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.render.render(write_still=True)
    if not os.path.isfile(path):
        raise RuntimeError("render did not write %s" % path)
    return path
