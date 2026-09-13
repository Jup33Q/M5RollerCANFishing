# RollerCAN 钓鱼模拟器 · 一键从头重建激活提示词

> 用法：新建一个空目录作为工作区，把 `prompt-rebuild/` 整个拷进去，打开 Kimi Code，
> 把本文件**全文**粘贴为第一条消息。Agent 会按下面的规格从零重建整个项目。
> 本目录已带齐：鱼图素材（assets/fish/）、必要 skills（skills/）、施工文档（docs/）。

---

## 任务总述

从零重建「RollerCAN 力反馈钓鱼模拟器」完整项目（仅 macOS）：

- **硬件**：M5Stack CoreS3（BMI270 IMU）+ M5Stack RollerCAN（I2C 力反馈旋钮电机，
  地址 0x64，Port.A SDA=2/SCL=1，400kHz）。RollerCAN 旋钮 = 鱼线轮摇柄，
  CoreS3 = 鱼竿姿态。鱼线张力 → 电机反向扭矩（手感即玩法）。
- **组成**：① 钓鱼专用固件（PlatformIO）② 标准力反馈固件 ③ RollerFlasher
  macOS 烧录器（SwiftPM/SwiftUI）④ Unity 钓鱼模拟器（Built-in 管线）
  ⑤ Blender 参数化渔轮模型。
- 工作区布局（就地创建）：

```
<工作区>/
├── firmware-fishing/      # 钓鱼专用固件（干净版）
├── firmware/              # 标准力反馈固件（本地 4 模式）
├── RollerFlasher/         # macOS 烧录器 SwiftPM 工程
├── RollerHapticUnity/     # Unity 工程
├── blender/roller_model.py# 渔轮参数化建模脚本
├── tools/png_to_rgb565.py # 鱼图 → RGB565 C 数组
├── assets/fish/           # 已有：fish_0..6.png（去背 RGBA 像素鱼）
└── docs/                  # 已有：5 份施工计划（先通读，含全部踩坑记录）
```

## 先读文档

`docs/` 下 5 份计划是前世今生全记录，施工前先通读：
fishing-simulator-plan.md（主线协议/验收清单）、unity-serial-link-plan.md（链路）、
fish-sprites-gamification-plan.md（像素鱼 HUD）、water-shader-and-ux-plan.md
（水面 shader + 模型施工记录）、imu-axis-mapping-fix-plan.md（姿态估计根治 +
轴映射校准 + prefab 化 + 甩竿检测，共十轮施工记录）。

## 通信协议（一套 ASCII 行，串口 115200 与 UDP :9000 双通道同式）

- Unity → CoreS3：`TENSION,<0..1000>`（张力千分比 → 电流 = ratio × EXT_MAX_CURRENT
  × EXT_DIR）、`SETCUR,<-60000..60000>`、`FISH,<0..6>`（切鱼精灵）、`MODE,EXT` /
  `MODE,LOCAL`、`LEVEL`（调平）、`PING`（心跳，固件回 PONG，不武装看门狗）。
- CoreS3 → Unity：串口 `HAPTIC,EXT,<pos_0.01deg>,<cur>,<roll>,<pitch>,<yaw>` ~200Hz；
  OSC/UDP ~100Hz → :8000：`/rollercan/imu fff`（欧拉角 legacy）、`/rollercan/quat ffff`
  （x,y,z,w 调平后，**主用**）、`/rollercan/motor ff`（角度/电流）、`/rollercan/acc ff`
  （线性加速度幅值：瞬时 + 15Hz 低通，甩竿检测用）。
- CoreS3 IP 由 Unity 从 OSC 包来源自动发现，免配置。
- OSC 编码注意：字符串必须先 \0 终止再 4 字节对齐补齐（否则接收方解析全丢）。

## ① 钓鱼固件 firmware-fishing（PlatformIO，board m5stack-cores3）

- lib_deps：M5Unified@^0.2.5、M5GFX@^0.2.7、github.com/m5stack/M5Unit-Roller.git
- 上电即钓鱼 HUD（canvas 离屏渲染消频闪）+ 电流模式待命；**无本地模式、
  无触摸切换；BtnA = 调平**（甩竿误触无副作用）。
- 姿态估计：陀螺仪**四元数积分 + Mahony 加速度 tilt 校正**（最短弧、无奇点无回绕；
  估计"上"向量取 R(q) 第三行，Madgwick 原文四元数约定相反勿照搬）；静止近水平
  3s 自动调平；LEVEL 存四元数零点，输出 = lv⁻¹ ⊗ q。
- 线性加速度：体轴加速度经调平姿态旋到世界系去重力 → 幅值 raw + 15Hz 低通。
- 看门狗：200ms 无实质命令 → `releaseTorque()`（电流归零、解除武装），
  界面永远留钓鱼 HUD。所有电流过 clampCur(±CURRENT_MAX=60000)。
- 手感参数（勿动）：EXT_MAX_CURRENT=40000、**EXT_DIR=-1**（鱼拽与收线反向）、
  KD_DAMPING=1.5、velEst 位置差分（readback 寄存器延迟助振，勿回退）。
- 钓鱼 HUD：深靛渐变底 + "FISHING!" 标题 + 96×96 像素鱼精灵（透明键 0xF81F，
  `canvas.pushImage(x,y,96,96,FISH_SPRITES[id],0xF81F)`）+ 大张力% + 全宽
  绿→红张力条 + ≥0.95 红框闪烁 + WAITING UNITY / UNITY LINKED + IMU 三轴 [LVL]。
- 鱼精灵表（fish_sprites.h 由 tools/png_to_rgb565.py 生成，7×96×96 RGB565）：
  0 鲫鱼 / 1 锦鲤 / 2 鲈鱼 / 3 鲶鱼 / 4 小虾 / 5 小螃蟹 / 6 萨卡班甲鱼。
  M5GFX `pushImage` 按 host order 读 uint16（ESP32 小端原生），不要转 big-endian；
  渐变用 `fillGradientRect(..., m5gfx::VLINEAR/HLINEAR)`（没有 fillGradientV/H）。
- WiFi 配置：`#if __has_include("wifi_config.h")` 引入，占位默认值
  YOUR_WIFI_SSID/YOUR_WIFI_PASS/192.168.1.100；RollerFlasher 构建前自动生成覆盖。

## ② 标准固件 firmware

同一 codebase 多本地 4 模式（DETENTS 两级棘轮 KP_MINOR=35/KP_MAJOR=40/
TICK_MINOR=500/TICK_MAJOR=1000/CAP_MAJOR=250、SPRING KP=15/KD_SPRING=4.0、
ENDSTOP 墙 ±18000、FREE），触摸按钮区 + BtnA 循环切模式，EXT 仅命令进出，
看门狗超时回 LOCAL DETENTS。姿态/OSC/命令协议与钓鱼固件全同。

## ③ RollerFlasher（macOS SwiftPM executable，swift build -c release）

- 两页签：串口监视（HAPTIC 解析 + 角度曲线 + 手动命令框）/ 固件烧录
  （变体选择、串口选择、WiFi 配置、构建/构建并烧录/烧录已编译 bin）。
- 变体 = 独立工程：fishing → firmware-fishing、standard → firmware；
  bundle Resources 各带一份，首次运行播种到 `~/Library/Application Support/
  RollerFlasher/<工程名>` 工作副本再构建；构建前自动写 wifi_config.h。
- pio/esptool 查找路径：/opt/homebrew/bin、~/.platformio/penv/bin 等；
  烧录前自动断开串口监视防端口争抢。
- 打包 .app：Contents/MacOS 放 release 二进制 + Resources 放两个固件工程，
  `codesign --force --deep --sign -` 签名（先 `xattr -cr` 清扩展属性）。

## ④ Unity 工程 RollerHapticUnity（Built-in 管线，勿引 URP/ShaderGraph）

- 场景 Fishing.unity：水面（WaterSurface.shader：Surface + GrabPass 折射扭曲 +
  正弦解析导数法线 + fbm 细节 _Detail 门控 + 菲涅尔 + 闪鳞高光；WaterQuality.cs
  编辑模式降质）、RollerHapticSync（OSC 链路）、FishingSim（玩法 + OnGUI UI）、
  IMU_Rig（prefab：渔轮模型 + RodTip）、固定相机 (0,1.6,-2.4) 俯角 30°。
- RollerHapticSync.cs：UDP :8000 收 OSC（自包含极简解码器，大端 float）；
  quat 通道优先，轴映射 `(x,y,z)→(x,-y,-z)`（实机校准，真旋转映射）；
  Slerp 平滑；欧拉路径兜底含毛刺 reject+resync；motorRoot 失效按名递归找回
  "RollerCAN_Rotor"、coreS3Root 失效 GameObject.Find("IMU_Rig")；motorAxis=+Z、
  invertMotorAngle=1；UDP :9000 发命令，PING 0.5s 心跳；IMU 录制 CSV 含
  quat/accRaw/accLp 列。
- FishingSim.cs：状态机 IDLE→CAST→WAIT→BITE→FIGHT→LANDED/ESCAPED/LINE_BROKEN；
  甩竿检测（dbgAccRaw ≥ whipThreshold 5 m/s²，冷却 1.5s，IDLE 与终局都可触发，
  终局甩竿直接抛下一竿）；7 鱼种表（basePull 手感 + 基色 HSV 抖动）；张力 =
  鱼拉力×(1+挣扎 sin+噪声)+冲刺脉冲，收线加成；红区 0.3s 断线；耐力消耗/力竭衰减；
  鱼精灵 = Resources/fish/fish_N.png billboard quad（根级跟随，勿挂非均匀缩放
  父级下）；LANDED 抛物线上岸动画 + easeOutBack 横幅 + 渔获计数面板；
  左下姿态摇杆（IMU yaw/pitch 实时镜像）；收线进度条。
- IMU_Rig.prefab：嵌套 FBX 模型实例 + RodTip；场景跨嵌套引用用 stripped
  Transform 链。**FBX 勿含 ARMATURE**（skinned-mesh 重复渲染坑），扁平节点保
  fileID 稳定。

## ⑤ Blender 模型（blender/roller_model.py）

- 参数化建模 RollerCAN 整机 ×GLOBAL_SCALE 10；材质按件拆细；屏幕 plane UV 满幅；
  转子侧壁亮黄撞色旋转指示；NURBS 摇柄 join 进 RollerCAN_Rotor 网格；
  柱体 24 边、端盖 TRIFAN 中心辐射（扇面强制 flat）；FBX 导出剥离骨骼。
- 预览：`blender --background --python roller_model.py -- --preview`。

## 协作与验证约定

- 固件验证：`pio run`（PlatformIO 自行安装）；Unity 脚本无编辑器时可用 Mono csc
  + Unity 安装目录 UnityReferenceAssemblies/unity-4.8-api(+Facades) +
  Managed/UnityEngine/UnityEngine.*.dll 独立编译验证。
- 场景改动可直接编辑 .unity YAML（GUI 开着时）；改完重开场景勿 Ctrl+S。
- 固件烧录、Unity Play、实机验收由用户执行；视觉改动截图验收。
- 先通读 docs/ 再动手；每轮改动把施工记录追加到对应 plan 文档。

## 完成判据

- pio 两固件编译通过；RollerFlasher swift build 通过；Unity csc 编译通过。
- 烧录钓鱼固件上电即钓鱼 HUD；Unity Play 后拿起 CoreS3 模型姿态跟随、
  甩竿抛投、中鱼手上被拽、摇柄收线、断线松扭矩、上岸动画+计数。
