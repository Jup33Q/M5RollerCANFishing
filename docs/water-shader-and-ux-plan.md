# 钓鱼模拟器改进计划：水面 Shader + 视野遮挡修复 + Game 界面调平按钮

日期：2026-09-12 ｜ 状态：**代码完成（任务 1–3），待用户重开场景 + Play 截图验收** ｜ 前置：[fishing-simulator-plan.md](fishing-simulator-plan.md)、[fish-sprites-gamification-plan.md](fish-sprites-gamification-plan.md)（代码均已完成，烧录+联调进行中）

## 施工记录（2026-09-12）

- 任务 1：新建 `Assets/Shaders/WaterSurface.shader`（guid `ad3d5e0c15ab4fadbc42612d3a8a1a19`，Surface + GrabPass `_WaterGrab`，正弦波+fbm 法线/折射扭曲/菲涅尔/闪鳞高光，参数全暴露）；`Water.mat` 的 m_Shader 已指向它并写入默认属性。Shader 编译需 Unity 刷新后看 Console（本机无桥可预验）。
- 任务 2：Fishing.unity PrefabInstance `&249099961`（RollerHapticModel）加 m_LocalScale 0.5³ 覆盖，m_LocalPosition 改 (0, -0.05, 0.06)；RodTip 是 IMU_Rig 直接子节点不受影响。
- 任务 3：FishingSim.cs OnGUI 右上角「IMU 调平」按钮 → 双链路 `SendRaw("LEVEL")`；Mono csc（2022.3.62f3c1 UnityEngine 模块 + 4.8-api 引用集）独立编译通过。

## 施工记录（2026-09-12 第二轮：水面性能 + NURBS 摇柄）

- **水面编辑模式性能**：shader 法线改 4 层正弦**解析导数**（一次 sin+cos 同出高度与梯度），
  fbm 细节/闪鳞用 `_Detail` 门控；新增 `WaterQuality.cs`（[ExecuteAlways]，挂 Water 物体，
  用 MaterialPropertyBlock 在非 Play 时把 `_Detail` 降到 0.15、Play 恢复 1，不弄脏资产）。
- **摇柄重做（Blender NURBS 参数化）**：`rollercan-haptic/blender/roller_model.py` 新增
  摇柄段——轴座 + NURBS 路径锥化摇臂（根 9.6mm→梢 6.4mm）+ NURBS 母线手工车削鼓形握把，
  挂在**转子自由端面（z=0 底面）外侧**并 parent 到 RollerCAN_Rotor（随转）；
  尺寸全面放大：臂长 75mm、握把 30mm。注意 Blender 5.2 的 Screw 修改器不再作用于曲线，
  车削走「NURBS 求值折线 → 手工 lathe」。FBX 同路径导出（节点名不变，fileID 稳定），
  已拷贝覆盖 `Assets/Models/RollerHaptic.fbx`，prefab 自动获得 CrankHub/CrankArm/
  CrankAxle/CrankGrip 子节点，无需场景接线。
- **转子撞色旋转指示**：侧壁亮黄竖条（Rotor_Stripe，近摇臂侧）+ 对侧定位点
  （Rotor_RimDot），随转子转。注意转子顶面被方形机身完全盖住，指示只能做在侧壁。
- **场景清理**：Fishing.unity 删除程序化 CrankArm/CrankGrip（10 个 YAML 块）+
  ReelHandle 组件及 PrefabInstance 的 m_AddedGameObjects/m_AddedComponents 条目；
  ReelHandle.cs 文件保留备用（不再挂场景）。
- 预览图 `blender/preview_crank.png`（blender --background --python roller_model.py -- --preview）。
- **减面（2026-09-13）**：柱体统一 24 边（主转子 32、轴销/定位点 16）、端盖改
  `TRIFAN` 中心辐射布线（扇面强制 flat 防明暗发花）、bevel 段数 3→2、
  NURBS 分辨率 12→8、车削 48→32；全模型 1704 顶点（原握把一件就 2354）。
  桌面备份 `~/Desktop/roller_haptic.blend` 已同步到此版本。
- **定稿同步（2026-09-13）**：按用户手改移除 RotorCap（中心轴帽）/RotorRing（底部装饰环）；
  摇臂两端封口改为确定性手工中心扇（曲线 `use_fill_caps=False` → bmesh 找 2 个边界环 →
  各加中心点 + 三角扇，与柱体 TRIFAN 同式；曲线自带封口/poke 实测拓扑不可靠已弃用）。
  摇臂拓扑：248 四边面 + 2×8 三角扇，无 ngon 无开放边。桌面备份已再同步。
- **道具版重构（2026-09-13）**：整体 ×10（`GLOBAL_SCALE=10.0`，真实毫米级太小，整机约 0.54m 宽；
  缩放走 `data.transform` + 位置 ×10，不走 ops 防父子双重缩放）；材质按件拆细 11 种
  （Rotor/Square/Flange/Hub/Arm/Axle/Grip/Shell/Face/Screen/AccentYellow 各自独立命名）；
  Crank 四件 `join` 进 RollerCAN_Rotor 网格（多材质槽保留）；屏幕改单面片 plane、UV 0-1 满幅；
  新建运动绑定 Rig：Bone_Root（Static_Group 骨骼父子）+ Bone_Rotor（转子网格骨骼父子，
  绕 Z 轴 = 电机轴），FBX 导出含 ARMATURE。预览渲染时 Bone_Rotor 摆 35° 已验证随动正确。
  注意：Unity 侧模型变大 10 倍，RodTip 位置与 0.5 实例缩放可能要重新调。
- **FBX 剥离骨骼（2026-09-13）**：骨骼版 FBX 在 Unity 导入后翻车了——skinned-mesh 重复渲染
  （白色大残影）、骨骼父子补偿导致转子轴向/缩放错乱、motorRoot 驱动失效。修法：
  `.blend` 保留 Rig（Blender 侧动画用），导出 FBX 前把 rotor/grp_st 从骨骼父子解回
  Assembly，`object_types` 不含 ARMATURE——节点名/层级与旧版一致，场景 fileID 稳定，
  RollerHapticSync 直接驱动 RollerCAN_Rotor 节点恢复工作。
- **Unity 链路修复（2026-09-13）**：RollerHapticSync——motorRoot 引用失效时按名字递归找回
  （FBX 重导入 fileID 会漂）；电机旋转轴改 `motorAxis` 字段可调（本次导入柱轴落本地 +Z，
  非旧版 +Y）；`invertMotorAngle` 置 1（方向反）；IMU 平滑从逐轴 lerp 改四元数 Slerp——
  固件欧拉角跳等价表示（roll/yaw/pitch 同翻 180°）时逐轴 lerp 反向绕远路，Slerp 免疫。
- **姿态估计根治（2026-09-13）**：录制分析（imu_record_20260913_104817.csv）定位真根因——
  固件欧拉角直接积分陀螺仪，roll/pitch 过 ±90° 时积分发散，发出单帧 ~180° 毛刺并陷入
  持续镜像态（模型"反向旋转"）；Unity 侧任何滤波都无法区分镜像态与真实姿态。
  固件改**四元数积分 + Mahony 加速度 tilt 校正**（最短弧，无回绕无奇点），LEVEL 改存
  四元数零点，legacy 欧拉角由四元数换算（显示/旧协议不变），新增 `/rollercan/quat`
  通道（x,y,z,w，调平后）。Unity 优先用 quat 通道：轴映射 roll→X、pitch→Z、yaw→Y
  （镜像置换 det=-1 → 向量部分 (x,y,z)→(-x,-z,-y)），Slerp 平滑；欧拉路径保留兜底
  （reject+resync 防毛刺）。固件 pio 通过，已同步 RollerFlasher 两目录（bundle 已重签）。
  注意：估计"上"向量是 R(q) 第三行（非第三列，Madgwick 原文四元数约定相反，勿照搬）。

## 背景状态（2026-09-12 傍晚）

- 链路已从 USB 串口切换为 **UDP/OSC 双通道**（`RollerHapticSync.cs` 收发一体，
  CoreS3 IP 从 OSC 包来源自动发现；PING/PONG 连接确认；串口链路保留备用）。
- 固件 OSC 编码 bug 已修（oscPad 终止符），IMU 数据能进 Unity。
- 固件已加**自动调平**（静止近水平 3s 自动归零）+ `LEVEL` 命令 + 本地界面 IMU 三轴显示。
- Unity 工程：**Built-in 管线**；`com.unity.render-pipelines.core` 与 `shadergraph` 已从
  manifest 移除（编辑器 6000.5.7f1 安装不完整、PathTracing 类型缺失导致包编译失败）；
  KWS WaterSystem2 已移出工程备份在 `_backup/KriptoFX/`。**不要再引入 URP/ShaderGraph 依赖，
  除非先在 Hub 里重装修复 6000.5.7f1**。
- Fishing.unity 场景：`RollerHapticSync` 物体（OSC 链路组件，coreS3Root=IMU_Rig、
  motorRoot=RollerCAN_Rotor），FishingSim.oscLink 已指向它。
- 场景构建惯例：GUI 编辑器开着时不能 batch（工程锁）；改场景优先直接编辑
  `Assets/Scenes/Fishing.unity` YAML（改完让用户重开场景、不要 Ctrl+S 覆盖）。

## 任务 1：水面 Shader（Water 物体）

要求：透明、有折射光影，效果达到 Shadertoy 平均水平。**Built-in 管线**。

- 新文件 `Assets/Shaders/WaterSurface.shader`（Surface Shader + `GrabPass`）：
  - 程序化波动法线：多层不同方向/频率正弦波叠加（Gerstner-lite 取法线）+
    2~3 层 value-noise fbm 细节，纯程序无贴图
  - 折射：GrabPass 屏幕采样 + 法线扰动扭曲（`_Distort`）
  - 菲涅尔：视角越掠射越反射天空色（`_SkyTint` 渐变近似，Built-in 无反射探针）
  - 太阳高光：Blinn-Phong，方向光方向 + 高频噪声调制出闪鳞（glitter）
  - 颜色：`_ShallowColor`/`_DeepColor` 随 fresnel/视角混合；`alpha:fade`，
    Queue=Transparent
  - 参数全部暴露 Properties（Inspector 可调）
- 应用：改 `Assets/Materials/Water.mat` 的 `m_Shader` 引用到新 shader
  （shader 主对象 fileID=4800000，取新 .shader 的 .meta guid）。
- 验证：Unity Play 截图看 Game 视图（水面透明折射水下/背景、太阳高光、波纹滚动）。
  场景里的鱼/浮漂在水面上方，不受 GrabPass 影响（Queue 顺序注意：
  水面 Transparent，鱼/浮漂 Opaque 先渲，会被折射扭曲——这是正确的水面效果）。

## 任务 2：渔轮摇柄模型 Game 视图太大挡视野

现象：Game 视图里 RollerHapticModel（IMU_Rig 下，渔轮+摇柄手把）占比过大，
挡住水面/浮漂视野。

- 位置：Fishing.unity 中 `IMU_Rig/RollerHapticModel`（含 RollerCAN_Rotor、
  RotorCap、RotorRing、Static_Group、RodTip）。
- 修法（任选，以截图验收为准）：缩小 RollerHapticModel 的 localScale（如 0.4~0.6），
  并/或把它下移到画面下缘（localPosition 下移/前移），或 Main Camera 拉远/抬高。
  注意 FishingSim.rodTip 引用 RodTip Transform，抛竿落点从 rodTip 算——
  挪模型后确认鱼线起点仍合理。
- 直接编辑场景 YAML 的 Transform 值即可（m_LocalScale / m_LocalPosition）。
- 验收：Game 视图里水面和浮漂无遮挡，渔轮在画面下方不抢眼。

## 任务 3：陀螺仪调平按钮 → Game 界面

- `FishingSim.cs` OnGUI 右上角加按钮「IMU 调平」：
  点击 → `LinkSend(s => s.SendRaw("LEVEL"), o => o.SendRaw("LEVEL"))`
  （固件已有 LEVEL 命令：立即以当前姿态为零点，不切模式不武装看门狗）。
- 按钮旁小字显示调平状态更好（可选）：固件调平后打印 `LEVEL,OK`（串口通道），
  OSC 通道无回执——可只做发射后不管。
- 验收：Play 中点按钮，CoreS3 屏幕本地界面 IMU 行出现 [LVL] 绿标，
  Unity 模型姿态归零。

## 验收清单

- 水面：透明、折射扭曲、太阳高光闪鳞、波纹持续滚动；截图确认效果达标
- 渔轮模型不再遮挡视野；抛竿/中鱼流程视觉正常
- Game 界面调平按钮可用，CoreS3 [LVL] 出现，Unity 姿态归零
- Console 无红错；主线验收清单（fishing-simulator-plan.md）不回归
