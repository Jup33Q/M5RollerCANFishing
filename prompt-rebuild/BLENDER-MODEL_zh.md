# RollerCAN 渔轮模型 · Blender 建模提示词（Kimi K3 专用）

>
> English version: [BLENDER-MODEL.md](BLENDER-MODEL.md)
> 用法：把本文件全文粘贴给 Kimi K3，工作区里需先有 `assets/model-ref/` 四张参考图。
> 目标产出：`blender/roller_model.py`（参数化建模脚本）+ `roller_haptic.blend` +
> 预览渲染图 + 供 Unity 用的 `RollerHaptic.fbx`。

---

## 任务

用 Blender（macOS Steam 版 5.2.1，headless：`blender --background --python roller_model.py`）
参数化重建 RollerCAN 渔轮整机模型。**先调取参考图目验再动手**：

- `assets/model-ref/preview.png` —— 整机基座+转子初版
- `assets/model-ref/preview_crank.png` —— 含摇柄的定稿版（最终形态以此为准）
- `assets/model-ref/axis_check.png` —— 轴向核对参考
- `assets/model-ref/fbx_reimport_check.png` —— FBX 重导入检查参考

用 ReadMediaFile 逐张查看，建模时对照。

## 模型规格

整机按 `GLOBAL_SCALE = 10.0` 放大（真实毫米级太小；缩放走 `data.transform` +
位置 ×10，**不走 ops**，防父子双重缩放）。整机约 0.54m 宽。

零部件（真实尺寸×10）：

1. **RollerCAN_Rotor**（电机转子，圆柱，主转子 32 边）：绕 Z 轴旋转（电机轴）。
   - 侧壁亮黄竖条 `Rotor_Stripe`（近摇臂侧）+ 对侧定位点 `Rotor_RimDot`（16 边），
     随转子转 = 旋转指示。**注意转子顶面被方形机身完全盖住，指示只能做在侧壁**。
   - 摇柄四件（CrankHub 轴座 + CrankArm 摇臂 + CrankAxle + CrankGrip 鼓形握把）
     挂在**转子自由端面（z=0 底面）外侧**，建模后 **join 进 RollerCAN_Rotor 网格**
     （多材质槽保留）。
2. **Static_Group**（静态部分）：方形机身（Square）、法兰（Flange）、Hub、
   外壳（Shell）、面板（Face）、屏幕（Screen = 单面片 plane，UV 0-1 满幅）。
3. 层级：`Assembly`（空节点根）→ `RollerCAN_Rotor`、`Static_Group`。

材质：按件拆细 11 种独立命名材质
（Rotor/Square/Flange/Hub/Arm/Axle/Grip/Shell/Face/Screen/AccentYellow）。

## 摇柄建模（关键难点，必须照做）

- CrankArm：**NURBS 路径锥化**——NURBS 曲线做路径，半径从根 9.6mm 锥化到梢 6.4mm，
  臂长 75mm。NURBS 分辨率 8。
- CrankGrip：**NURBS 母线手工车削**鼓形握把（30mm）——
  **Blender 5.2 的 Screw 修改器不再作用于曲线**，车削必须走
  「NURBS 求值折线 → 手工 lathe」（32 段）。
- 摇臂两端封口：**确定性手工中心扇**——曲线 `use_fill_caps=False`，转 mesh 后用
  bmesh 找 2 个边界环，各加中心点 + 三角扇（与柱体 TRIFAN 同式）。
  **曲线自带封口/poke 实测拓扑不可靠，勿用**。
- 验收拓扑：摇臂 248 四边面 + 2×8 三角扇，无 ngon 无开放边。

## 减面规范（全模型目标 ≈1700 顶点）

- 柱体统一 24 边（主转子 32、轴销/定位点 16）
- 端盖 `TRIFAN` 中心辐射布线，扇面强制 flat 防明暗发花
- bevel 段数 2；NURBS 分辨率 8；车削 32 段

## 运动绑定与导出（重要）

- `.blend` 内可保留 Rig：Bone_Root（Static_Group 骨骼父子）+ Bone_Rotor
  （转子网格骨骼父子，绕 Z 轴）——仅供 Blender 侧动画。
- **FBX 导出必须剥离骨骼**：导出前把 rotor/grp_st 从骨骼父子解回 Assembly，
  `object_types` 不含 ARMATURE。骨骼版导入 Unity 会 skinned-mesh 重复渲染
  （白色大残影）+ 轴向/缩放错乱，绝对勿导出 ARMATURE。
- FBX 节点名/层级须与上述一致（Unity 场景靠名字稳定引用 fileID）。

## 预览与验收（每步必做，保证成功）

1. `blender --background --python roller_model.py -- --preview`
   渲 `blender/preview_crank.png`（Bone_Rotor 摆 35° 验证随动）。
2. **ReadMediaFile 目验预览图**：对照 `assets/model-ref/preview_crank.png`，
   整机比例、摇柄形态、黄条位置、屏幕满幅。
3. 脚本内打印顶点/面数，核对 ≈1700 顶点、摇臂拓扑无 ngon/开放边。
4. 失败处理：读报错改脚本重跑，直到预览图与参考图形态一致为止——
   不要交付未目验的模型。

## 已验证的坑（不要再踩）

- Blender 5.2 Screw 不作用于曲线 → 手工 lathe
- 曲线自带封口拓扑不可靠 → bmesh 手工中心扇
- 整体缩放用 data.transform + 位置 ×10，不用 ops（父子双重缩放）
- 扇面不强制 flat 会明暗发花
- FBX 带 ARMATURE 进 Unity 直接翻车
