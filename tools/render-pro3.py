"""Original Pro 3 illustration. Run with Blender in background mode.

blender -b -t 4 --python tools/render-pro3.py -- output_directory [--sample]
"""
import math
from pathlib import Path
import sys

import bpy
from mathutils import Vector

args = sys.argv[sys.argv.index("--") + 1:]
out = Path(args[0]).resolve()
out.mkdir(parents=True, exist_ok=True)
bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene


def material(name, color, roughness, metallic=0, coat=0):
    mat = bpy.data.materials.new(name)
    mat.diffuse_color = (*color, 1)
    mat.use_nodes = True
    shader = mat.node_tree.nodes.get("Principled BSDF")
    shader.inputs["Base Color"].default_value = (*color, 1)
    shader.inputs["Roughness"].default_value = roughness
    shader.inputs["Metallic"].default_value = metallic
    shader.inputs["Coat Weight"].default_value = coat
    return mat


white = material("Polished white shell", (.87, .88, .91), .19, coat=.40)
trim = material("Molded inner tray", (.61, .64, .69), .32, coat=.14)
tip = material("Soft silicone", (.91, .91, .91), .49)
black = material("Microphone mesh", (.012, .014, .017), .63)
inset = material("Socket interior", (.081, .086, .095), .39)
steel = material("Brushed stainless steel", (.46, .5, .54), .27, .78)
led = material("Status LED", (.19, .72, .32), .34)
shader = led.node_tree.nodes.get("Principled BSDF")
shader.inputs["Emission Color"].default_value = (.22, .76, .38, 1)
shader.inputs["Emission Strength"].default_value = .42


def mesh_object(name, vertices, faces, mat):
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    scene.collection.objects.link(obj)
    if mat:
        obj.data.materials.append(mat)
    return obj


def shell(name, sections, mat):
    """Loft a rounded rectangle along vertically changing sections."""
    vertices = []
    quarter_steps = 20
    count = quarter_steps * 4
    for z, w, d, r in sections:
        for corner, (sx, sy) in enumerate(((1, 1), (-1, 1), (-1, -1), (1, -1))):
            for j in range(quarter_steps):
                a = math.pi * (corner / 2 + j / (2 * (quarter_steps - 1)))
                vertices.append((sx * (w / 2 - r) + r * math.cos(a),
                                 sy * (d / 2 - r) + r * math.sin(a), z))
    faces = [tuple(reversed(range(count)))]
    for i in range(len(sections) - 1):
        for j in range(count):
            k = (j + 1) % count
            faces.append((i * count + j, i * count + k,
                          (i + 1) * count + k, (i + 1) * count + j))
    faces.append(tuple(range(len(vertices) - count, len(vertices))))
    obj = mesh_object(name, vertices, faces, mat)
    for face in obj.data.polygons[1:-1]:
        face.use_smooth = True
    return obj


def ellipsoid(name, loc, scale, mat=None, rot=(0, 0, 0)):
    bpy.ops.mesh.primitive_uv_sphere_add(segments=48, ring_count=24, location=loc)
    obj = bpy.context.object
    obj.name = name
    obj.scale = scale
    obj.rotation_euler = rot
    if mat:
        obj.data.materials.append(mat)
    for face in obj.data.polygons:
        face.use_smooth = True
    return obj


def carve(obj, tool):
    bpy.context.view_layer.objects.active = obj
    modifier = obj.modifiers.new("Formed recess", "BOOLEAN")
    modifier.operation = "DIFFERENCE"
    modifier.object = tool
    bpy.ops.object.modifier_apply(modifier=modifier.name)
    bpy.data.objects.remove(tool, do_unlink=True)


def pivot(name, loc=(0, 0, 0)):
    obj = bpy.data.objects.new(name, None)
    scene.collection.objects.link(obj)
    obj.location = loc
    return obj


def attach(obj, parent):
    bpy.context.view_layer.update()
    matrix = obj.matrix_world.copy()
    obj.parent = parent
    obj.matrix_world = matrix


body = shell("Case lower shell", [
    (0, 4.45, 1.37, .51), (.04, 4.83, 1.54, .57), (.12, 5.18, 1.74, .64),
    (.23, 5.53, 1.91, .70), (.39, 5.82, 2.05, .75), (.60, 6.04, 2.15, .78),
    (.84, 6.17, 2.20, .83), (1.15, 6.22, 2.21, .85), (2.67, 6.22, 2.21, .85),
    (2.89, 6.21, 2.20, .83), (3.01, 6.17, 2.17, .80),
    (3.08, 6.11, 2.10, .77), (3.105, 6.01, 2.01, .73),
], white)

tray = shell("Case insert", [
    (2.91, 5.95, 1.91, .70), (3.088, 5.98, 1.98, .71),
    (3.129, 5.91, 1.91, .69), (3.138, 5.78, 1.81, .66),
], trim)

lid = pivot("Lid hinge", (0, .92, 3.12))
lid_shell = shell("Lid outer shell", [
    (3.16, 5.96, 2.00, .73), (3.18, 6.12, 2.12, .77),
    (3.24, 6.19, 2.18, .81), (3.36, 6.22, 2.21, .83), (3.83, 6.20, 2.19, .81),
    (4.10, 6.10, 2.13, .77), (4.31, 5.89, 2.02, .72), (4.49, 5.60, 1.84, .64),
    (4.62, 5.21, 1.62, .57), (4.68, 4.85, 1.44, .49), (4.70, 4.59, 1.34, .46),
], white)
lid_insert = shell("Lid insert", [
    (3.13, 5.63, 1.73, .62), (3.139, 5.86, 1.89, .69), (3.17, 5.98, 1.99, .73),
    (3.25, 6.01, 2.02, .74),
], trim)

for side in (-1, 1):
    x = side * 1.49
    carve(body, ellipsoid("Tray tool", (x, -.01, 3.20), (1.23, .73, .90)))
    carve(tray, ellipsoid("Tray tool", (x, -.01, 3.20), (1.25, .756, .90)))
    carve(lid_shell, ellipsoid("Lid tool", (x, -.02, 3.04), (1.25, .76, .78)))
    carve(lid_insert, ellipsoid("Lid tool", (x, -.02, 3.04), (1.26, .77, .79)))
    ellipsoid("Recess contact", (x, -.14, 2.32), (.21, .10, .035), steel)

attach(lid_shell, lid)
attach(lid_insert, lid)
ellipsoid("Hinge", (0, .955, 3.13), (1.05, .17, .125), steel)
ellipsoid("Front indicator", (0, -1.105, 2.49), (.065, .023, .065), led)
for z in (1.98, 2.31):
    ellipsoid("Lanyard loop rim", (3.103, .2, z), (.035, .135, .147), steel)
    ellipsoid("Lanyard loop opening", (3.129, .2, z), (.021, .084, .094), inset)


def vent_surface(earpiece, side, parent):
    """Lay an angled acoustic mesh over the curvature of the shell."""
    vertices = []
    angle = side * .68
    rotation = earpiece.rotation_euler.to_matrix()
    for i in range(37):
        a = math.tau * max(0, i - 1) / 36
        u, v = (0, 0) if i == 0 else (.106 * math.cos(a), .25 * math.sin(a))
        x = side * .145 + u * math.cos(angle) - v * math.sin(angle)
        z = .23 + u * math.sin(angle) + v * math.cos(angle)
        sx, sy, sz = earpiece.scale
        y = -sy * math.sqrt(max(0, 1 - (x / sx)**2 - (z / sz)**2)) - .013
        vertices.append(earpiece.location + rotation @ Vector((x, y, z)))
    faces = [(0, 1 + (i + 1) % 36, i + 1) for i in range(36)]
    attach(mesh_object("Acoustic vent", vertices, faces, black), parent)


def earbud(side):
    name = "Left" if side == -1 else "Right"
    root = pivot(name + " earbud")
    angle = (0, side * -.26, 0)
    earpiece = ellipsoid(name + " earpiece", (side*.10, 0, .13), (.68, .52, .61), white, angle)
    vent_surface(earpiece, side, root)
    attach(earpiece, root)
    stem = shell(name + " stem", [
        (-1.67, .25, .23, .102), (-1.63, .32, .29, .135), (-1.56, .36, .32, .145),
        (-1.28, .37, .34, .153), (-.54, .44, .39, .177), (-.27, .49, .42, .187),
        (-.04, .45, .41, .183),
    ], white)
    stem.location = (side * .28, .12, 0)
    stem.rotation_euler = (.14, -side * .22, 0)
    attach(stem, root)
    attach(ellipsoid(name + " stem grille", (side*.615, .34, -1.56), (.117, .106, .045), steel), root)
    attach(ellipsoid(name + " upper vent", (side*.42, .26, .59), (.13, .05, .13), black, (.38, 0, 0)), root)
    # Molded silicone tip around a recessed sound outlet.
    tip_center = (-side*.59, -.12, -.045)
    attach(ellipsoid(name + " silicone", tip_center, (.40, .375, .377), tip, (0, side * .21, 0)), root)
    attach(ellipsoid(name + " sound outlet", (-side*.91, -.167, -.055), (.087, .215, .21), inset, (0, side * .2, 0)), root)
    return root


buds = [earbud(-1), earbud(1)]


def place_bud(bud, side, seated):
    if seated:
        bud.location = (side * 1.51, .015, 3.235)
        bud.rotation_euler = (-.23, side * .09, side * .11)
    else:
        bud.location = (side * 4.12, -.75, 3.13)
        bud.rotation_euler = (-.08, -side * .12, -side * .21)


def point_at(obj, point):
    obj.rotation_euler = (Vector(point) - obj.location).to_track_quat("-Z", "Y").to_euler()


def area(name, location, power, color, size, size_y, aim=(0, 0, 2.4)):
    light = bpy.data.lights.new(name, "AREA")
    light.energy = power
    light.color = color
    light.shape = "RECTANGLE"
    light.size = size
    light.size_y = size_y
    obj = bpy.data.objects.new(name, light)
    scene.collection.objects.link(obj)
    obj.location = location
    point_at(obj, aim)


area("Key softbox", (-5, -7, 9), 1300, (.95, .97, 1), 6.4, 4.6)
area("Fill softbox", (5, -3, 6), 530, (1, .98, .94), 5, 5)
area("Rear strip", (1, 5, 7.5), 1100, (.86, .92, 1), 6.2, 2)
area("Low white bounce", (-1, -6, 1.4), 65, (.89, .94, 1), 6, 3)
area("Tall strip reflection", (-4.7, -5, 4.1), 210, (1, .98, .96), 1.1, 6)

world = bpy.data.worlds.new("Neutral studio")
scene.world = world
world.use_nodes = True
world.node_tree.nodes["Background"].inputs[0].default_value = (.4, .43, .49, 1)
world.node_tree.nodes["Background"].inputs[1].default_value = .33

camera = bpy.data.objects.new("Product camera", bpy.data.cameras.new("Product camera"))
scene.collection.objects.link(camera)
camera.location = (3.0, -16.0, 8.85)
point_at(camera, (0, 0, 2.62))
camera.data.type = "ORTHO"
camera.data.ortho_scale = 11.65
scene.camera = camera

scene.render.engine = "CYCLES"
scene.cycles.samples = 32
scene.cycles.use_denoising = True
cycles = bpy.context.preferences.addons["cycles"].preferences
try:
    cycles.compute_device_type = "OPTIX"
    cycles.get_devices()
    for device in cycles.devices:
        device.use = device.type == "OPTIX"
    if any(device.use for device in cycles.devices):
        scene.cycles.device = "GPU"
except (RuntimeError, TypeError, ValueError):
    pass
scene.render.film_transparent = True
scene.render.resolution_x = 384
scene.render.resolution_y = 212
scene.render.resolution_percentage = 100
scene.view_settings.view_transform = "AgX"
scene.view_settings.look = "AgX - Medium High Contrast"
scene.view_settings.exposure = .35
scene.render.image_settings.file_format = "PNG"
scene.render.image_settings.color_mode = "RGBA"

FRAME_COUNT = 20
sample = "--sample" in args
for mask in ((3, 0) if sample else range(4)):
    for index, bud in enumerate(buds):
        place_bud(bud, -1 if index == 0 else 1, bool(mask & (1 << index)))
    for frame in ((0, FRAME_COUNT - 1) if sample else range(FRAME_COUNT)):
        lid.rotation_euler.x = math.radians(-108 * frame / (FRAME_COUNT - 1))
        scene.render.filepath = str(out / f"{mask}-{frame:02}.png")
        bpy.ops.render.render(write_still=True)
