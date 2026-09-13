# IMU 四元数轴映射修正计划（改动 0–2 已施工，待用户 Play 验收）

日期：2026-09-13 ｜ 状态：**代码完成（改动 0–2），待用户重开场景 + Play 验收（验证清单 1–6）** ｜ 前置：姿态估计四元数化已上线（见 water-shader-and-ux-plan.md 施工记录"姿态估计根治"）

## 施工记录（2026-09-13）

- 改动 1：`RollerHapticSync.cs` quat 分支映射 `(-x,-z,-y)` → `(-x, y, z)`，注释同步更新
  （真旋转映射 det=+1：roll→-X、pitch→+Z、yaw→+Y）。
- 改动 2：`Fishing.unity` RollerHapticSync 组件 `invertMotorAngle: 1 → 0`。
- 改动 0：新建 `Assets/Scripts/CameraAim.cs`（LateUpdate 只改 rotation：瞄准点 =
  `target.position + target.rotation * aimOffset` 局部偏移随 IMU_Rig 旋转，
  dampSpeed 指数阻尼，默认 target=IMU_Rig、aimOffset=(0,-0.3,0.4)、dampSpeed=4）；
  .meta guid `555f0c306cf3499180484c8a40b4471a` 预生成；Fishing.unity Main Camera
  追加 MonoBehaviour `&1088967290` 并接入 m_Component。
- 验证：Mono csc（`cli csc.exe` + `UnityReferenceAssemblies/unity-4.8-api`(+Facades)
  + `Managed/UnityEngine/UnityEngine.*.dll`）编译 Assets/Scripts 全部脚本 exit=0；
  场景 YAML 锚点查重无冲突，guid 全工程唯一。
- 固件本轮未动，无需 pio / RollerFlasher 同步。
- 待用户：重开 Fishing.unity（勿 Ctrl+S）→ Play → 按下方验证清单 1–6 验收；
  顺手检查项（invertReel / EXT_DIR 手感方向）Play 时定论。

## 施工记录（2026-09-13 第三轮：IMU_Rig 预制体化 + IMU 位移/加速度通道）

> **注意**：本轮的位移估计部分（/rollercan/pos、posSync、ZUPT 漏积分）经实机
> 考量后已于**第四轮整体移除**，只保留加速度幅值通道；预制体化部分仍有效。

- **IMU_Rig 预制体化**：新建 `Assets/Prefabs/IMU_Rig.prefab`
  （guid `f66b81ad53804504870f78342c8e27d0`）——根 IMU_Rig（默认 pos (0,0.12,0)）
  + RodTip + FBX 嵌套实例（修改项与场景原状一致：scale 1、pos 0、rot 恒等、
  命名 RollerHapticModel）；prefab 内另备 3 个 stripped Transform 供场景跨嵌套引用：
  FBX 根(800000003)、RollerCAN_Rotor(800000004)、Main.unity coreS3Root 原指
  内部节点(800000005, 源 fileID -4123692122157435511)。
- **Fishing.unity**：旧 IMU_Rig GO/Transform/FBX PrefabInstance/RodTip 六块删除，
  换 `&1700000001` 新 prefab 实例；引用重接：coreS3Root→1700000002（prefab 根）、
  motorRoot→1700000004（跨嵌套 RollerCAN_Rotor）、CameraAim.target→1700000002、
  rodTip→1700000003、SceneRoots→1700000001。位移/缩放未动（用户要求）。
- **Main.unity**：FBX 直挂实例换 `&1700000011` 新 prefab 实例，原 FBX 根的
  pos(0,0.001,0)/rot(-90°X) 烘到 IMU_Rig 根上（世界姿态不变）；coreS3Root→
  1700000012（跨嵌套原内部节点，行为不变）、motorRoot→1700000013、
  SceneRoots→1700000011。
- 两场景 YAML 校验：旧 fileID 无残留、锚点无重复。若手搓的跨嵌套 stripped
  引用在编辑器里解析失败，RollerHapticSync.Start 新增 coreS3Root 按名
  （"IMU_Rig"）兜底 + 原有 motorRoot 按名兜底会接管。
- **固件位移估计**（main.cpp）：imuUpdate 末尾新增——体轴加速度经调平后姿态
  q_out 旋到世界系去重力 → 线性加速度（m/s²）；ZUPT（角速度和<8°/s 且
  |a|-1g<0.06g）时速度清零、位置按 2/s 漏回零；运动时漏积分（速度泄漏 3/s，
  速度限 ±1.5m/s、位置限 ±0.6m）。LEVEL 时位移/速度归零。线性加速度幅值
  15Hz 低通 accMagLp（甩绳手势检测预留）。
- **OSC 新通道** `/rollercan/pos ffff`：位移 x,y,z（米）+ accMagLp，~100Hz。
- **Unity RollerHapticSync**：解析 /rollercan/pos，轴映射与四元数同一换基
  (x,-y,-z)；posSyncEnabled/posScale/invertPos/posSmoothing 四个 Inspector
  参数；驱动 coreS3Root.localPosition = 初始位置 + 位移×posScale；
  dbgPos/dbgAccMag 只读；IMU 录制 CSV 新增 posX,posY,posZ,accMag 四列。
- 验证：pio run SUCCESS（flash 16.4%）；固件已同步 RollerFlasher 两目录
  （bundle /tmp 重签，codesign -v 通过）；Mono csc exit=0。
- 待用户：RollerFlasher「构建并烧录」新固件 → 重开场景 Play：拿起设备
  平移看模型跟随（Inspector dbgPos），快速甩动看 dbgAccMag 峰值（甩绳阈
  值后续定）；轴向/力度不对调 invertPos/posScale。

## 施工记录（2026-09-13 第二轮：三轴全反修正）

- 用户实机反馈：第一版 (-x, y, z) 三个旋转轴方向全反、手柄方向也反。
- `RollerHapticSync.cs` quat 映射 (-x, y, z) → **(x, -y, -z)**（上一版取向量部分共轭，
  三轴旋转方向全部翻转：roll→+X、pitch→-Z、yaw→-Y）。
- `Fishing.unity` `invertMotorAngle` 0 → **1**（手柄方向翻回）。
- IMU_Rig 及其子层级的位移/缩放未动（用户要求保持现状）。
- Mono csc 编译 exit=0。待用户重开场景 Play 复验单轴跟随与手柄方向。

## 背景

固件改四元数积分 + `/rollercan/quat` 通道后实机测试：翻转/反向甩动已消除，
但实机对比发现 Unity 模型的轴向映射与实际设备不一致。当时的映射是推导值
（镜像置换 `(x,y,z)→(-x,-z,-y)`），未经实机校准。

## 实机测出的三处错误（用户确认）

| # | 现象 | 现状 | 应改为 |
|---|---|---|---|
| 1 | 模型 Y/Z 轴与真实世界颠倒 | 设备 Y→模型 Z、设备 Z→模型 Y | 设备 Y→模型 Y、设备 Z→模型 Z（蓝绿轴对调） |
| 2 | X 轴旋转方向反 | roll 绕 +X | roll 绕 **-X**（红轴方向取反） |
| 3 | 手柄（曲柄）转动方向反 | invertMotorAngle = 1 | 翻回 **0** |

## 改动点

### 0. Play 模式相机朝向鱼竿（新增）

现状：Main Camera 固定 (0, 1.6, -2.4)、俯角 30°，不跟踪。拿起 CoreS3 当鱼竿
转动 IMU_Rig 后，鱼竿/渔轮会转出画面。

做法（下次实现）：新脚本 `Assets/Scripts/CameraAim.cs`，LateUpdate 里保持相机
位置不动、朝向目标——`transform.rotation = Quaternion.LookRotation(target - pos)`，
目标挂 IMU_Rig（或 RollerHapticModel），可加 aimOffset（往水面方向偏一点保住
浮漂视野）与平滑阻尼。相机 Transform 已在 YAML 就位，脚本只改 rotation。
场景 YAML 把组件挂到 Main Camera 上（m_Component 追加 MonoBehaviour，
脚本 .meta guid 预生成）。

### 1. `RollerHapticUnity/Assets/Scripts/RollerHapticSync.cs` — `/rollercan/quat` 解析分支

当前（错误）：
```csharp
float qx = -m.vals[0], qy = -m.vals[2], qz = -m.vals[1], qw = m.vals[3];
```
改为：
```csharp
float qx = -m.vals[0], qy = m.vals[1], qz = m.vals[2], qw = m.vals[3];
```
即最终映射 = 固件四元数向量部分 `(x,y,z)` → 模型 `(-x, y, z)`。

同步更新该分支上方的注释（轴映射说明：设备 roll→模型 **-X**、pitch→模型 **+Z**、
yaw→模型 **+Y**；不再是镜像置换，det 恢复 +1，是真旋转映射）。

### 2. `Fishing.unity` YAML — RollerHapticSync 组件

- `invertMotorAngle: 1` → `0`（手柄运动方向反）。
- 改完让用户重开场景（勿 Ctrl+S）；或用户直接在 Inspector 取消勾选 Invert Motor Angle 后保存。

### 3. 顺手检查（改动后验证时再定）

- `FishingSim.invertReel`：手柄显示方向翻正后，游戏内收线方向可能也要跟着翻，
  Play 时正摇手柄看线是收还是放，不对再勾。
- FishingSim 鱼游动方向/张力方向手感：EXT_DIR 拉力方向与新的显示方向是否一致，
  中鱼时手感"被拽"应与鱼逃窜方向匹配。

## 验证

1. 烧录/编译不变（固件不动），改完 Play。
2. 单轴测试：设备分别绕 X/Y/Z 轴转 90°，模型应同轴同向跟随（红=roll -X、绿=yaw +Y、蓝=pitch +Z）。
3. 摇手柄：曲柄转动方向与真实 RollerCAN 转子一致。
4. 相机：拿起设备转动，鱼竿始终保持在画面内，水面/浮漂不被完全顶出画面。
5. 开 IMU 录制转一遍各轴，CSV（含 quat 列）可离线复核映射。
6. 翻头顶测试不回归（四元数积分无翻转）。

## 施工记录（2026-09-13 第四轮：位移砍掉，加速度计甩竿判断）

- **用户决定**：不做位移同步（双积分漂移硬伤），只留加速度计做甩绳/甩竿判断。
- **固件**：移除 pos/vel 状态、ZUPT、漏积分（第三轮代码整体回退）；保留
  「体轴加速度→调平世界系→去重力→线性加速度幅值」计算，accMagRaw（瞬时）
  + accMagLp（15Hz 低通）。OSC 通道 `/rollercan/pos ffff` 改为
  **`/rollercan/acc ff`**（瞬时值, 低通值，m/s²，~100Hz）。文件头 OSC 注释同步。
- **RollerHapticSync.cs**：posSync/posScale/invertPos/posSmoothing、rawPos/smPos/
  basePos、/rollercan/pos 解析、位置驱动全部移除；新增 /rollercan/acc 解析 →
  `dbgAccRaw`（瞬时）/ `dbgAccMag`（低通）；coreS3Root 按名 "IMU_Rig" 兜底保留
  （prefab 化保险）；CSV 列改 accRaw,accLp。
- **FishingSim.cs**：IDLE 新增甩竿触发——OSC 在线且 `dbgAccRaw ≥ whipThreshold`
  （默认 5 m/s²，Inspector 可调）即 StartCast()，冷却 whipCooldown 1.5s 防抖；
  状态文案「SPACE 或甩竿抛投」；OnGUI 左下角实时显示 acc 瞬时/低通值与当前阈值，
  供实机调参。
- 验证：pio run SUCCESS；固件同步 RollerFlasher 两目录（bundle /tmp 重签
  codesign -v 通过）；Mono csc exit=0。
- 待用户：烧录（需新固件才有 /rollercan/acc）→ 重开场景 Play → 左下角看
  acc 数值，甩几次定阈值（误触发就调高 whipThreshold，不灵就调低）。

## 施工记录（2026-09-13 第五轮：相机不自转 + 烧录变体失效排查）
- **CameraAim 不自转**：aimOffset 从「目标局部空间（随 IMU 旋转）」改为**世界空间**
  固定偏移——之前相机随设备姿态原地转动（用户感知为"自转"）；target 从 IMU_Rig
  改指 **RollerHapticModel**（Fishing.unity 新增 stripped Transform &1700000005 →
  prefab 内 800000003 = FBX 根）。csc exit=0，场景锚点查重通过。
- **烧录变体失效根因**：设备跑的仍是 17:56 的标准固件。App Support 工作副本
  证据：18:01 那次构建在重编译 main.cpp.o 后触发整个 FrameworkArduino 重编
  （耗时数分钟），进程中断（.sconsign 与 .o 停在 18:01，firmware.elf/.bin 停在
  17:56）→ 钓鱼变体固件从未烧进设备。已手动在工作副本补跑 pio run（SUCCESS，
  当前 .pio/firmware.bin = 最新固件 + 钓鱼变体 + 用户 WiFi 配置）。
- **给用户**：App 里确认变体选「钓鱼模拟器」，再点一次「构建并烧录」并等日志
  出现 SUCCESS/退出码（框架缓存已热，这次很快）；或直接「烧录已编译 .bin…」选
  `~/Library/Application Support/RollerFlasher/firmware/.pio/build/m5stack-cores3/firmware.bin`。

## 施工记录（2026-09-13 第六轮：甩竿跳界面根治 = 钓鱼专用干净固件工程）

- **根因**：旧固件里 BtnA 循环切模式（含切出 EXT），甩竿紧握时极易误触 →
  跳回工程风界面；触摸区/看门狗 enterLocal 也都是退出路径。
- **新工程 `rollercan-haptic/firmware-fishing/`**（pio SUCCESS）：钓鱼专用干净固件——
  - 上电即钓鱼 HUD + 电流模式待命；无本地 4 模式、无触摸按钮区
  - **BtnA = 调平**（不切模式，误触无副作用）；触摸屏不参与任何切换
  - 看门狗超时 / MODE,LOCAL 只 `releaseTorque()`（电流归零+解除武装），
    界面永远留在钓鱼 HUD；MODE,EXT 仅作在线记录（协议兼容）
  - 保留全部已验证件：四元数+Mahony 姿态、自动调平、LEVEL、OSC 四通道
    （imu/quat/motor/acc）、acc raw+lp 甩竿检测数据、像素鱼 HUD、
    EXT 电流+KD_DAMPING 阻尼 200Hz 环、HAPTIC 串口遥测
  - HUD 底部加 IMU 三轴 + [LVL] 行（原工程风界面的调平可视性保留）
- **RollerFlasher 改造**（swift build release 通过，bundle 已更新重签）：
  - 变体 = 独立工程选择：fishing → `firmware-fishing`、standard → `firmware`，
    bundle Resources 各带一份，App Support 工作副本按变体分别播种
  - **默认变体改为钓鱼模拟器**；旧 variant_config.h 机制废弃
    （VariantConfigSync.writeHeader → cleanLegacyHeader，自动清理残留）
  - 变体 Picker 切换即切工程目录并写日志
- 已预置 `~/Library/Application Support/RollerFlasher/firmware-fishing/` 并
  预热 pio 缓存（SUCCESS，14s）；标准工程的旧 variant_config.h 残留已删除。
- **给用户**：打开新版 RollerFlasher（默认已是钓鱼模拟器）→「构建并烧录」→
  上电即钓鱼 HUD，随便甩不会跳界面。Unity 侧协议完全不变（同一套命令/OSC）。

## 施工记录（2026-09-13 第七轮：甩竿重复触发 + 固定相机）

- **甩竿只触发第一次的根因**：whip 检测只在 IDLE 状态跑，一局结束
  （LANDED/ESCAPED/LINE_BROKEN）后只能靠 Space 回 IDLE——甩竿无响应。
  修法：whip 检测提到 switch 外每帧计算；三个终局状态过 1.5s 结果展示后，
  甩竿/空格**直接 StartCast 抛下一竿**（不再绕道 IDLE）；冷却计时也移出
  switch 每帧递减。终局文案改「甩竿/SPACE 再来」。
- **取消相机跟随**：Fishing.unity 摘掉 Main Camera 上的 CameraAim 组件
  （MonoBehaviour &1088967290 块 + m_Component 条目删除），相机固定回
  (0,1.6,-2.4) 俯角 30°。CameraAim.cs 文件保留备用（未挂场景）。
- 验证：Mono csc exit=0；场景无 1088967290 残留、锚点无重复。
- 待用户：重开场景 Play——每局结束甩竿即可连续抛投；相机静止。

## 施工记录（2026-09-13 第八轮：姿态摇杆 HUD + 收线进度条 + 拉力方向翻转）

- **左下角姿态摇杆**（FishingSim.OnGUI，只读可视化，与真实数据一致）：
  摇杆球 = IMU 实际姿态（X=dbgYaw 左右、Y=dbgPitch 俯仰，±90° 满偏），
  链路断开时球变灰；acc 瞬时/低通读数挪到摇杆右侧。
- **收线进度条**：FIGHT 中新增「收线进度」条（1 - lineLen/maxLineLen，
  鱼跑线会回退），鱼种名/距离行下移。
- **拉力方向与收线相反**：firmware-fishing `EXT_DIR` 1 → -1——鱼拽 = 顶着
  手柄反方向出线，玩家收线时手感为对抗力。若实机方向仍不对，改回 1 即可
  （firmware-fishing/src/main.cpp:61）。
- 验证：pio SUCCESS；csc exit=0；固件已同步 App Support 工作副本与
  Desktop bundle（重签通过）。需重新烧录（仅固件改动）。

## 施工记录（2026-09-13 第九轮：鱼跑方向与收线反向——符号链核查）

- **现象**：鱼上钩后 RollerCAN 被拽转的方向与玩家收线方向相同（收线进度
  在鱼跑时反而前进）。
- **符号链核查**（invertReel=0）：
  1. 玩家收线摇柄 → 编码器正方向（R=+1）→ reelDps>0 → lineLen 减 ✓
  2. 设备 18:38 烧的固件 EXT_DIR=+1：正电流 → 编码器正方向（C=+1）
     → 鱼拽与收线同向 ✗（用户实测）
  3. 修复：EXT_DIR=-1（第八轮已改未烧）→ 鱼拽 = 编码器负方向
     → reelDps<0 → lineLen 增、收线进度回退 ✓，手感为对抗力 ✓
  4. invertMotorAngle 只影响模型视觉，不进 reelDps 链路，无影响。
- **结论**：修复已在固件源码（firmware-fishing/src/main.cpp:61 `EXT_DIR=-1`），
  工作副本 bin 已用用户 WiFi 配置重出（19:25）。**用户需重新烧录**：
  「构建并烧录」或「烧录已编译 .bin…」选
  `~/Library/Application Support/RollerFlasher/firmware-fishing/.pio/build/m5stack-cores3/firmware.bin`。

## 施工记录（2026-09-13 第十轮：像素鱼游戏素材 + 上岸动画 + 渔获计数）

- **鱼素材进 Unity**：`rollercan-haptic/assets/fish/fish_{0..6}.png`（birefnet 去背
  RGBA 版）复制到 `RollerHapticUnity/Assets/Resources/fish/`。运行时
  `Resources.Load<Texture2D>("fish/fish_N")` 装配：CreatePrimitive(Quad) 根级
  billboard（LateUpdate 朝相机），Unlit/Transparent 材质，纹理与固件精灵同款，
  颜色沿用 HSV 抖动；有图则藏胶囊体，无图回退胶囊体。FishSprite 不挂 Fish 子级
  （Fish 胶囊是非均匀缩放 0.08/0.15/0.08，子级会被拉变形），只每帧同步位置。
- **上岸动画**（LANDED）：鱼从最后水中位置抛物线跃到竿旁（0.8s，顶点 +0.45m），
  空中翻滚 540°/s，落地侧躺 90°；横幅「钓到了 <鱼种>！」easeOutBack 弹入。
- **渔获计数**：`landedCount` + `landedPerSpecies[7]`，右上「渔获 N 条」面板
  分鱼种列明细；EndFight(LANDED) 计数，ESCAPED/LINE_BROKEN 鱼直接消失；
  StartCast/EnterIdle 清场（含精灵）。
- 验证：Mono csc exit=0。纯代码改动，无需烧录/改场景；用户切回 Unity 自动编译
  后 Play 即可验收（Console 无红 + 中鱼看精灵/上岸动画/计数）。
