# 钓鱼模拟器 · 激活提示词（IMU 轴映射修正 + 手柄方向 + 相机跟随）

> 用法：新会话里整段粘贴。计划文档是自洽的，细节以文档为准。

```
继续 RollerCAN 钓鱼模拟器项目。工作区：<WORKDIR>

先读四份计划：
- rollercan-haptic/docs/fishing-simulator-plan.md（主线：协议/变体/验收清单）
- rollercan-haptic/docs/fish-sprites-gamification-plan.md（像素鱼 HUD，代码已完成）
- rollercan-haptic/docs/water-shader-and-ux-plan.md（水面 shader/调平按钮已完成，文末有全部施工记录）
- rollercan-haptic/docs/imu-axis-mapping-fix-plan.md（本轮任务：按改动 0→2 执行 + 验证清单）

当前状态：
- 链路 UDP/OSC 双通道：RollerHapticSync.cs 收发一体（CoreS3 IP 自动发现，PING/PONG 确认）；
  FishingSim.cs 双链路抽象（LinkOpen/LinkMotorAngle/LinkSend）
- 固件（已烧录新版）：姿态估计 = 四元数积分 + Mahony 加速度校正（无奇点无回绕，根治了
  过 ±90° 翻转/镜像）；LEVEL 四元数零点；OSC 新增 /rollercan/quat（x,y,z,w 调平后）；
  UDP :9000 命令通道、自动调平、本地界面 [LVL]、钓鱼 HUD（canvas 消频闪、FISH,<id> 精灵）
- Unity RollerHapticSync.cs：优先走 quat 通道（当前轴映射为推导值 (-x,-z,-y)，**有错误待本轮
  修正**，欧拉路径兜底含毛刺 reject+resync）；motorRoot 引用失效按名字递归找回；
  motorAxis 字段（默认 +Z）；IMU 平滑四元数 Slerp；IMU 录制（勾选 recordImu → CSV 写工程根目录）
- Unity 场景 Fishing.unity：水面 WaterSurface.shader（Built-in + GrabPass，_Detail 门控）
  + WaterQuality.cs（编辑模式降质）；相机 (0,1.6,-2.4) 俯角 30°；程序化手柄已删除
- Blender 模型（Steam 版 Blender 5.2.1，脚本 rollercan-haptic/blender/roller_model.py，
  --preview 渲预览图）：整机 ×GLOBAL_SCALE 10、材质按件拆细、屏幕 plane UV 满幅、
  转子侧壁亮黄撞色旋转指示、NURBS 摇柄已 join 进 RollerCAN_Rotor 网格；
  .blend 内含运动绑定（Bone_Root/Bone_Rotor），**FBX 导出剥离骨骼**（扁平节点保 fileID 稳定，
  骨骼版导入 Unity 会 skinned-mesh 重复渲染——勿导出 ARMATURE）
- 已知待办（imu-axis-mapping-fix-plan.md）：① quat 轴映射改 (-x,y,z)（YZ 不再对调、X 取反）
  ② Fishing.unity invertMotorAngle 1→0（手柄方向）③ 新 CameraAim.cs（Play 中相机朝向 IMU_Rig，
  aimOffset 保浮漂视野+阻尼），组件挂 Main Camera（YAML 追加 MonoBehaviour + 预生成 .meta guid）
- 代码改动验证：固件 rollercan-haptic/.venv-pio/bin/pio run（在 firmware/ 目录跑）；
  Unity 脚本用 Mono csc + 2022.3.62f3c1 引用集（unity-4.8-api + UnityEngine.* 模块，命令见会话惯例）；
  场景改动直接编辑 Fishing.unity YAML（Unity GUI 开着不能 batch，改完让用户重开场景、勿 Ctrl+S）
- 固件改动后同步 ~/Library/Application Support/RollerFlasher/firmware/src/ 与
  ~/Desktop/RollerFlasher.app/Contents/Resources/firmware/src/（bundle 在 /tmp 重签名再 rsync 回）

协作约定：
- 本地力反馈参数（KP_MINOR=35 KP_MAJOR=40 KD_DAMPING=1.5 KD_SPRING=4.0、velEst 位置差分）勿动
- 固件烧录、Unity Play、场景重开均由用户执行；视觉改动用截图验收
- RollerFlasher 烧录走「构建并烧录」（自动写 wifi_config.h + variant_config.h）
- unityMCP 桥在本工程不可用（无实例）；Blender 预览图用 ReadMediaFile 目验
```

## 本轮任务速览（细节在 imu-axis-mapping-fix-plan.md）

1. `RollerHapticSync.cs` quat 解析分支：`(x,y,z)→(-x,-z,-y)` 改 **`(-x, y, z)`**
   （YZ 蓝绿轴归位、X 旋转取反），注释同步改
2. `Fishing.unity`：`invertMotorAngle: 1 → 0`（手柄运动方向反）
3. 新建 `CameraAim.cs`：LateUpdate 相机位置不动、LookRotation 朝 IMU_Rig +
   aimOffset + 阻尼，YAML 挂 Main Camera
4. 按文档验证清单验收：单轴 90°、曲柄方向、相机跟竿、录制 CSV 复核、翻头顶不回归
