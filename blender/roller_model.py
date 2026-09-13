# roller_model.py — Blender headless 建模：RollerCAN + CoreS3（游戏道具版）
# 结构：
#   Assembly (empty root)
#    ├─ Rig (armature)
# 　 │    ├─ Bone_Root  → Static_Group（静止件，骨骼父子）
#    │    └─ Bone_Rotor → RollerCAN_Rotor（可动件，绕 Z 轴摇柄旋转）
#    └─ Static_Group (empty)
#         ├─ RollerCAN_Square / Flange / CoreS3_Body / Face / Screen(plane, UV 满幅)
# 可动组：RollerCAN_Rotor 网格 = 转子 + Crank 四件（Hub/Arm/Axle/Grip，多材质槽）
#         + Rotor_Stripe / Rotor_RimDot（撞色旋转指示，挂在转子节点下）
# 尺寸：真实毫米 × GLOBAL_SCALE（10）→ 整机约 0.54m 宽。
# 运行: blender --background --python roller_model.py [-- --preview]
import bpy, math, os

# ---------- 场景清理 ----------
bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
scene.unit_settings.system = 'METRIC'
scene.unit_settings.scale_length = 1.0  # 1 BU = 1 m

OUT_DIR = os.path.dirname(os.path.abspath(__file__))

# ---------- 材质（按件拆细，一件一名，方便 Unity 侧独立调整） ----------
def make_mat(name, color, metallic=0.0, rough=0.4):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = m.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = (*color, 1)
    bsdf.inputs["Metallic"].default_value = metallic
    bsdf.inputs["Roughness"].default_value = rough
    return m

MAT_ROTOR  = make_mat("RollerCAN_Rotor_Red",      (0.58, 0.06, 0.03), metallic=0.85, rough=0.32)
MAT_SQUARE = make_mat("RollerCAN_Square_Red",     (0.50, 0.05, 0.03), metallic=0.80, rough=0.40)
MAT_FLANGE = make_mat("RollerCAN_Flange_RedDark", (0.35, 0.03, 0.02), metallic=0.80, rough=0.45)
MAT_HUB    = make_mat("CrankHub_RedDark",         (0.38, 0.04, 0.02), metallic=0.80, rough=0.42)
MAT_ARM    = make_mat("CrankArm_Metal",           (0.50, 0.50, 0.54), metallic=0.95, rough=0.22)
MAT_AXLE   = make_mat("CrankAxle_MetalDark",      (0.30, 0.30, 0.33), metallic=0.95, rough=0.30)
MAT_GRIP   = make_mat("CrankGrip_Dark",           (0.07, 0.07, 0.08), metallic=0.10, rough=0.50)
MAT_SHELL  = make_mat("CoreS3_Shell_White",       (0.82, 0.82, 0.80), metallic=0.10, rough=0.50)
MAT_FACE   = make_mat("CoreS3_Face_Dark",         (0.06, 0.06, 0.07), metallic=0.20, rough=0.35)
MAT_SCREEN = make_mat("CoreS3_Screen",            (0.02, 0.05, 0.08), metallic=0.30, rough=0.15)
MAT_ACCENT = make_mat("Rotor_AccentYellow",       (0.95, 0.72, 0.04), metallic=0.30, rough=0.35)

# ---------- 工具 ----------
def bevel(obj, width, segments=2):
    mod = obj.modifiers.new("Bevel", 'BEVEL')
    mod.width = width
    mod.segments = segments
    mod.limit_method = 'ANGLE'
    mod2 = obj.modifiers.new("WeightedNormal", 'WEIGHTED_NORMAL')
    mod2.keep_sharp = True
    return obj

def cube_obj(name, size, loc, mat):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc)
    o = bpy.context.object
    o.name = name
    o.scale = (size[0], size[1], size[2])
    bpy.ops.object.transform_apply(scale=True)
    if mat: o.data.materials.append(mat)
    return o

def cyl_obj(name, radius, depth, loc, mat, vertices=24):
    # 端盖用 TRIFAN（中心向外辐射布线），不用 NGON 大面
    bpy.ops.mesh.primitive_cylinder_add(vertices=vertices, radius=radius, depth=depth,
                                        end_fill_type='TRIFAN', location=loc)
    o = bpy.context.object
    o.name = name
    if mat: o.data.materials.append(mat)
    bpy.ops.object.shade_smooth()
    # 端盖扇面保持平直（不参与 smooth，否则圆盘明暗发花）
    for poly in o.data.polygons:
        if len(poly.vertices) == 3:
            poly.use_smooth = False
    return o

def plane_obj(name, size_xy, loc, mat):
    """单面片 plane（朝上），默认 UV 即 0-1 占满整幅"""
    bpy.ops.mesh.primitive_plane_add(size=1, location=loc)
    o = bpy.context.object
    o.name = name
    o.scale = (size_xy[0], size_xy[1], 1)
    bpy.ops.object.transform_apply(scale=True)
    if mat: o.data.materials.append(mat)
    return o

def empty(name, loc=(0, 0, 0)):
    o = bpy.data.objects.new(name, None)
    o.empty_display_size = 0.01
    o.location = loc
    scene.collection.objects.link(o)
    return o

def apply_all_modifiers(objs):
    for o in objs:
        if o.type != 'MESH':
            continue
        bpy.context.view_layer.objects.active = o
        for m in list(o.modifiers):
            bpy.ops.object.modifier_apply(modifier=m.name)

# ---------- 尺寸（真实毫米；导出前统一 × GLOBAL_SCALE） ----------
GLOBAL_SCALE = 10.0      # 整体放大倍数：真实毫米级太小，按游戏道具尺寸
# RollerCAN: 40x40x40mm 整机；圆柱段 Ø41x16mm（电机），方形段 40x40x24mm
ROTOR_R, ROTOR_H = 0.0205, 0.016
SQ_W, SQ_H       = 0.040, 0.024
# CoreS3: 54x54x16mm
CS3_W, CS3_H     = 0.054, 0.016

z_rotor = ROTOR_H / 2                    # 0.008
z_sq    = ROTOR_H + SQ_H / 2             # 0.028
z_cs3   = ROTOR_H + SQ_H + CS3_H / 2     # 0.048

# ---------- RollerCAN 旋转圆柱（可动；用户定稿：无 RotorRing / RotorCap） ----------
rotor = cyl_obj("RollerCAN_Rotor", ROTOR_R, ROTOR_H, (0, 0, z_rotor), MAT_ROTOR, vertices=32)
bevel(rotor, 0.0015)

# ---------- 渔轮摇柄（NURBS 参数化，最后合并进转子网格） ----------
# 转子自由端面是 z=0 底面（顶面被方形机身盖住），摇柄挂在底面外侧：
# 轴座从底面向 -Z 探出，摇臂沿 +X 伸出，握把轴 -Z（朝远离机身方向，像真渔轮）。
CRANK_ARM_LEN = 0.075    # 摇臂长（轴心→握把中心）
CRANK_ARM_Z   = -0.0045  # 摇臂中心高度（转子底面外侧 4.5mm）
CRANK_ARM_R0  = 0.0048   # 摇臂根部半径
CRANK_ARM_R1  = 0.0032   # 摇臂末端半径
CRANK_GRIP_LEN = 0.030   # 握把长
CRANK_GRIP_R   = 0.0068  # 握把最大半径

def nurbs_path(name, pts, radii, mat, bevel_res=2, resolution=8):
    """NURBS 空间曲线 + 逐点半径锥化圆截面 → 摇臂"""
    cu = bpy.data.curves.new(name, 'CURVE')
    cu.dimensions = '3D'
    cu.resolution_u = resolution
    cu.bevel_depth = 1.0          # 实际半径用 point.radius 逐点给
    cu.bevel_resolution = bevel_res
    cu.use_fill_caps = False      # 端盖不交给曲线，转网格后手工做中心扇
    sp = cu.splines.new('NURBS')
    sp.points.add(len(pts) - 1)
    for i, (p, r) in enumerate(zip(pts, radii)):
        sp.points[i].co = (*p, 1.0)
        sp.points[i].radius = r
    sp.order_u = min(3, len(pts))
    sp.use_endpoint_u = True
    o = bpy.data.objects.new(name, cu)
    scene.collection.objects.link(o)
    if mat: o.data.materials.append(mat)
    return o

def nurbs_lathe(name, profile_rz, loc, mat, steps=32):
    """NURBS 母线（r,z 平面）求值成折线 → 手工 360° 车削成回转体网格。
    （Blender 5.2 的 Screw 修改器不再作用于曲线，故转折线后车削。）"""
    cu = bpy.data.curves.new(name + "_profile", 'CURVE')
    cu.dimensions = '3D'   # 必须 3D：2D 曲线会压平 Z
    cu.resolution_u = 8
    sp = cu.splines.new('NURBS')
    sp.points.add(len(profile_rz) - 1)
    for i, (r, z) in enumerate(profile_rz):
        sp.points[i].co = (r, 0.0, z, 1.0)
    sp.order_u = min(3, len(profile_rz))
    sp.use_endpoint_u = True
    tmp = bpy.data.objects.new(name + "_profile", cu)
    scene.collection.objects.link(tmp)
    # NURBS 求值成折线顶点（顺序沿母线）
    dg = bpy.context.evaluated_depsgraph_get()
    me = tmp.evaluated_get(dg).to_mesh()
    pts = sorted(((v.co.x, v.co.z) for v in me.vertices), key=lambda p: p[1])
    tmp.evaluated_get(dg).to_mesh_clear()
    bpy.data.objects.remove(tmp)
    bpy.data.curves.remove(cu)
    # 手工车削：pts 绕 Z 轴旋转 steps 份
    n = len(pts)
    verts, faces = [], []
    for i in range(steps):
        a = 2.0 * math.pi * i / steps
        ca, sa = math.cos(a), math.sin(a)
        for (r, z) in pts:
            verts.append((r * ca, r * sa, z))
    for i in range(steps):
        ni = (i + 1) % steps
        for j in range(n - 1):
            a, b = i * n + j, ni * n + j
            faces.append((a, b, b + 1, a + 1))
    # 两端三角扇封口（母线端点半径 >0，留孔会漏）
    for end_i, sign in ((0, -1), (n - 1, 1)):
        ci = len(verts)
        verts.append((0.0, 0.0, pts[end_i][1]))
        for i in range(steps):
            ni = (i + 1) % steps
            if sign < 0:
                faces.append((ci, ni * n + end_i, i * n + end_i))
            else:
                faces.append((ci, i * n + end_i, ni * n + end_i))
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    mesh.update()
    o = bpy.data.objects.new(name, mesh)
    o.location = loc
    scene.collection.objects.link(o)
    if mat: o.data.materials.append(mat)
    for poly in mesh.polygons:
        poly.use_smooth = True
    return o

# 中央轴座（嵌进转子底面，向外 -Z 探出托住摇臂）
hub = cyl_obj("CrankHub", 0.0062, 0.016, (0, 0, 0.003), MAT_HUB, vertices=24)
bevel(hub, 0.0008, 2)

# 摇臂：根部粗末端细，略向下垂（-Z）避让机身
arm_pts = [
    (0.000, 0.0, CRANK_ARM_Z + 0.0020),
    (CRANK_ARM_LEN * 0.35, 0.0, CRANK_ARM_Z + 0.0008),
    (CRANK_ARM_LEN * 0.70, 0.0, CRANK_ARM_Z - 0.0006),
    (CRANK_ARM_LEN * 0.92, 0.0, CRANK_ARM_Z - 0.0016),
    (CRANK_ARM_LEN, 0.0, CRANK_ARM_Z - 0.0020),
]
arm_radii = [CRANK_ARM_R0, CRANK_ARM_R0 * 0.92, CRANK_ARM_R0 * 0.80,
             CRANK_ARM_R1 * 1.1, CRANK_ARM_R1]
arm = nurbs_path("CrankArm", arm_pts, arm_radii, MAT_ARM)

# 握把轴销（摇臂末端 → 握把中段，沿 -Z）
grip_z0 = CRANK_ARM_Z - 0.002 - 0.002   # 握把近端（连臂端）高度
axle = cyl_obj("CrankAxle", 0.0026, CRANK_GRIP_LEN * 0.8,
               (CRANK_ARM_LEN, 0, grip_z0 - CRANK_GRIP_LEN * 0.4), MAT_AXLE, vertices=16)

# 握把：鼓形回转体（NURBS 母线求值 + 手工车削），向 -Z 悬垂
grip_profile = [
    (0.0008, -CRANK_GRIP_LEN),                    # 外端（悬垂端）
    (0.0042, -CRANK_GRIP_LEN + 0.0015),
    (CRANK_GRIP_R * 0.97, -CRANK_GRIP_LEN * 0.65),
    (CRANK_GRIP_R, -CRANK_GRIP_LEN * 0.30),
    (0.0042, -0.0012),
    (0.0008, 0.000),                              # 近端（连摇臂）
]
grip = nurbs_lathe("CrankGrip", grip_profile, (CRANK_ARM_LEN, 0, grip_z0), MAT_GRIP)

# 摇臂曲线转网格（FBX 只导网格；握把已是网格）
bpy.ops.object.select_all(action='DESELECT')
arm.select_set(True)
bpy.context.view_layer.objects.active = arm
bpy.ops.object.convert(target='MESH')
# 两端开口 → 手工中心向外辐射封口：每个边界环加一个中心点 + 三角扇
import bmesh
from mathutils import Vector as _V, Matrix as _M
bm = bmesh.new()
bm.from_mesh(arm.data)
bnd = set(e for e in bm.edges if e.is_boundary)
loops = []
while bnd:
    e0 = bnd.pop()
    loop_vs = list(e0.verts)
    loop_es = [e0]
    frontier = list(e0.verts)
    while frontier:
        v = frontier.pop()
        for e in v.link_edges:
            if e in bnd:
                bnd.discard(e)
                loop_es.append(e)
                for vv in e.verts:
                    if vv not in loop_vs:
                        loop_vs.append(vv)
                        frontier.append(vv)
    loops.append((loop_vs, loop_es))
assert len(loops) == 2, f"摇臂应有 2 个开口环，实际 {len(loops)}"
for loop_vs, loop_es in loops:
    vc = bm.verts.new(sum((v.co for v in loop_vs), _V()) / len(loop_vs))
    for e in loop_es:
        bm.faces.new((e.verts[0], e.verts[1], vc))
bm.to_mesh(arm.data)
bm.free()
bpy.ops.object.shade_smooth()
for poly in arm.data.polygons:      # 端面三角扇保持平直
    if len(poly.vertices) == 3:
        poly.use_smooth = False

# ---------- 转子撞色细节（旋转可视化，挂转子节点下随转） ----------
# 亮黄与红机身撞色。注意：转子顶面被方形机身盖住，指示必须做在圆柱侧壁上。
# 侧壁竖直指示条（近摇臂侧，切向贴在圆柱面上，略微凸出）
_a = -0.35
stripe = cube_obj("Rotor_Stripe", (0.0009, 0.004, ROTOR_H * 0.72),
                  (ROTOR_R * math.cos(_a), ROTOR_R * math.sin(_a), ROTOR_H * 0.5), MAT_ACCENT)
stripe.rotation_euler = (0, 0, _a)  # 局部 x 朝径向
bevel(stripe, 0.0003, 2)
# 对侧定位点（凸出圆柱面的小圆点，转到背面也有参照）
_b = 2.75
dot = cyl_obj("Rotor_RimDot", 0.0018, 0.0009,
              (ROTOR_R * math.cos(_b), ROTOR_R * math.sin(_b), ROTOR_H * 0.5),
              MAT_ACCENT, vertices=16)
dot.rotation_euler = (0, math.radians(90), _b)  # 轴向朝外

# ---------- RollerCAN 方形机身（静止） ----------
square = cube_obj("RollerCAN_Square", (SQ_W, SQ_W, SQ_H), (0, 0, z_sq), MAT_SQUARE)
bevel(square, 0.002)
# 顶部固定法兰盘（静止，连接 CoreS3）
flange = cyl_obj("RollerCAN_Flange", 0.018, 0.003, (0, 0, ROTOR_H + SQ_H + 0.0015), MAT_FLANGE, vertices=24)
bevel(flange, 0.0008, 2)

# ---------- CoreS3 主机（静止，屏幕朝上） ----------
cs3 = cube_obj("CoreS3_Body", (CS3_W, CS3_W, CS3_H), (0, 0, z_cs3), MAT_SHELL)
bevel(cs3, 0.002)
# 深色面板
face = cube_obj("CoreS3_Face", (CS3_W - 0.004, CS3_W - 0.004, 0.0016),
                (0, 0, z_cs3 + CS3_H / 2 + 0.0008), MAT_FACE)
bevel(face, 0.0015)
# 屏幕（2.0寸 320x240，约 41x31mm）：面片 plane，UV 0-1 占满整幅（贴屏显纹理用）
screen = plane_obj("CoreS3_Screen", (0.041, 0.031),
                   (0, 0, z_cs3 + CS3_H / 2 + 0.0017), MAT_SCREEN)

# ---------- 修改器全部应用（合并/缩放前烘焙） ----------
apply_all_modifiers([rotor, hub, arm, axle, grip, stripe, dot, square, flange, cs3, face])

# ---------- Crank 合并进转子（一个可动网格，多材质槽保留拆件材质） ----------
bpy.ops.object.select_all(action='DESELECT')
for o in (hub, arm, axle, grip):
    o.select_set(True)
rotor.select_set(True)
bpy.context.view_layer.objects.active = rotor
bpy.ops.object.join()          # hub/arm/axle/grip → RollerCAN_Rotor

# ---------- 层级（骨骼父子前先把世界位置关系摆好） ----------
root   = empty("Assembly")
grp_st = empty("Static_Group")
for o in (square, flange, cs3, face, screen):
    o.parent = grp_st
# 撞色细节挂转子节点下（随转）
for o in (stripe, dot):
    mw = o.matrix_world.copy()
    o.parent = rotor
    o.matrix_world = mw

# ---------- 全局放大 ----------
S = _M.Scale(GLOBAL_SCALE, 4)
for o in bpy.data.objects:
    if o.type == 'MESH':
        o.data.transform(S)      # 顶点 ×10（绕各自原点，原点都在装配轴线上或就地）
    o.location = S @ o.location  # 本地位置 ×10（父子层级同比例放大）

# ---------- 运动绑定（Armature） ----------
# Bone_Root：静止件（Static_Group 整组）
# Bone_Rotor：可动件（转子网格，绕 Z 轴旋转 = 摇柄/电机轴）
arm_data = bpy.data.armatures.new("Rig")
arm_obj = bpy.data.objects.new("Rig", arm_data)
scene.collection.objects.link(arm_obj)
bpy.context.view_layer.objects.active = arm_obj
bpy.ops.object.mode_set(mode='EDIT')
eb = arm_data.edit_bones
# 骨骼沿 +Y 建（Y 骨 + roll 0 时骨骼局部系 = 世界系，骨骼父子不引入 -90°X 补偿；
# 若沿 +Z 建，子网格会被带成 rot(-90°X)，导出 Unity 后转子轴偏 90°）
b_root = eb.new("Bone_Root")
b_root.head = (0, 0, 0)
b_root.tail = (0, 0.10, 0)
b_rotor = eb.new("Bone_Rotor")
b_rotor.head = (0, 0, 0)
b_rotor.tail = (0, 0.16, 0)
b_rotor.parent = b_root
bpy.ops.object.mode_set(mode='OBJECT')

def bone_parent(o, bone):
    """骨骼父子（保持世界位置；bone parent 默认对齐 tail，用 matrix_world 修正）"""
    mw = o.matrix_world.copy()
    o.parent = arm_obj
    o.parent_type = 'BONE'
    o.parent_bone = bone
    o.matrix_world = mw

bone_parent(grp_st, "Bone_Root")
bone_parent(rotor, "Bone_Rotor")
arm_obj.parent = root

# ---------- 保存 .blend（含运动绑定，Blender 侧动画用） ----------
blend_path = os.path.join(OUT_DIR, "roller_haptic.blend")
bpy.ops.wm.save_as_mainfile(filepath=blend_path)

# ---------- 导出 FBX（给 Unity：剥离骨骼，恢复扁平节点） ----------
# Unity 侧旋转由 RollerHapticSync 代码驱动 RollerCAN_Rotor 节点，骨骼在 Unity 里
# 只会引入 skinned-mesh 重复渲染/轴向/缩放错乱。因此 FBX 不导 ARMATURE，
# 并把骨骼父子的对象挂回 Assembly（节点名/层级与旧版一致，场景 fileID 保持稳定）。
for o in (rotor, grp_st):
    mw = o.matrix_world.copy()
    o.parent = root
    o.parent_type = 'OBJECT'
    o.parent_bone = ''
    o.matrix_world = mw

fbx_path = os.path.join(OUT_DIR, "..", "..", "unity", "RollerHaptic.fbx")
os.makedirs(os.path.dirname(fbx_path), exist_ok=True)
bpy.ops.export_scene.fbx(
    filepath=fbx_path,
    use_selection=False,
    apply_scale_options='FBX_SCALE_NONE',
    object_types={'EMPTY', 'MESH'},
    mesh_smooth_type='FACE',
    use_mesh_modifiers=True,
    add_leaf_bones=False,
    bake_anim=False,
)
print("EXPORTED:", fbx_path)

# ---------- 预览渲染（--preview 时；Bone_Rotor 摆 35° 验证绑定） ----------
import sys
if "--preview" in sys.argv:
    import mathutils
    arm_obj.pose.bones["Bone_Rotor"].rotation_mode = 'XYZ'
    arm_obj.pose.bones["Bone_Rotor"].rotation_euler.z = math.radians(35)
    # 相机
    bpy.ops.object.camera_add(location=(1.6, -1.5, 0.6))
    cam = bpy.context.object
    def track(o, pt):
        o.rotation_euler = (mathutils.Vector(pt) - o.location).to_track_quat('-Z', 'Y').to_euler()
    track(cam, (0.15, 0.0, 0.0))
    scene.camera = cam
    # 灯光
    bpy.ops.object.light_add(type='SUN', location=(3, -2, 4))
    sun = bpy.context.object
    sun.data.energy = 3.5
    sun.rotation_euler = (math.radians(50), math.radians(-15), math.radians(30))
    bpy.ops.object.light_add(type='AREA', location=(-1.5, 1, 2))
    area = bpy.context.object
    area.data.energy = 300
    area.data.size = 2.0
    scene.render.engine = 'BLENDER_EEVEE'
    scene.render.resolution_x = 900
    scene.render.resolution_y = 700
    scene.render.filepath = os.path.join(OUT_DIR, "preview_crank.png")
    scene.world = bpy.data.worlds.new("W")
    scene.world.use_nodes = True
    scene.world.node_tree.nodes["Background"].inputs[0].default_value = (0.9, 0.92, 0.95, 1)
    bpy.ops.render.render(write_still=True)
    print("PREVIEW:", scene.render.filepath)
