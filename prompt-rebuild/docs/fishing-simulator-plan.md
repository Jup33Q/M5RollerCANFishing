# 钓鱼模拟器施工计划（Unity ↔ CoreS3 ↔ RollerCAN）

日期：2026-09-12 ｜ 状态：待实施 ｜ 前置：[unity-serial-link-plan.md](unity-serial-link-plan.md)（串口链路）

## 概念

RollerCAN 旋钮 = **鱼线轮摇柄**。Unity 钓鱼模拟器实时计算鱼线张力，
经 USB 串口发给 CoreS3 变成电机反向扭矩（手上有鱼在拉）；
摇柄编码器回传 Unity 当收线输入。手感即玩法。

最小可玩循环：抛竿 → 等咬钩 → 中鱼（拉力+挣扎）→ 摇柄遛鱼收线 →
张力爆表断线 / 鱼上岸。

## 通信协议（双通道：USB 串口 115200 或 WiFi UDP，同一套 ASCII 行协议）

**CoreS3 → Unity**：
- 串口遥测（~200Hz）：`HAPTIC,<mode>,<pos_0.01deg>,<cur_cmd>,<roll>,<pitch>,<yaw>`
- 或 OSC/UDP（~100Hz，目标 `TARGET_IP:8000`）：`/rollercan/imu fff` roll/pitch/yaw；
  `/rollercan/motor ff` 电机角度/电流指令

- `pos`：摇柄编码器 → **收线输入**（角速度 = 收线速度）。
- `roll/pitch/yaw`：CoreS3 IMU 姿态 → **渔轮/鱼竿模型姿态**（整机拿起
  当鱼竿用）；Unity 侧沿用 RollerHapticSync 的 smoothing / Invert / Offset
  校正管线。yaw 仅陀螺仪积分会漂，钓鱼场景以 roll/pitch 为主，
  明显漂移时再加静止自动归零。

**Unity → CoreS3**（串口 RX 或 UDP :9000，一个 UDP 包 = 一行命令，
CoreS3 IP 由 Unity 从 OSC 包来源自动发现，免配置）：

| 命令 | 含义 |
|---|---|
| `TENSION,<0..1000>` | 线张力千分比 → 电流 = ratio × `EXT_MAX_CURRENT`，方向 `EXT_DIR`（拉住摇柄） |
| `SETCUR,<-60000..60000>` | 原始电流指令（调试用，直接进 CURRENT 模式） |
| `MODE,EXT` / `MODE,LOCAL` | 外部驱动 / 回本地力反馈模式 |
| `FISH,<0..6>` | 中鱼鱼种 id → EXT 钓鱼 HUD 切对应像素鱼精灵 |
| `PING` | 连接确认心跳（Unity 每 0.5s 发，**不武装看门狗**；UDP 通道回 `PONG`） |

安全约束：
- **看门狗**：EXT 模式下 200ms 收不到新命令 → 电流归零并回 LOCAL
  （防 Unity 卡死/拔线时电机锁扭矩）。
- 上电默认 LOCAL DETENTS，收到 `MODE,EXT` 才进外部驱动。
- 所有电流指令过 `clampCur` + `CURRENT_MAX` 上限，与本地模式同一安全路径。

## 改动一：固件（编译验证即可，烧录用户自己执行）

`rollercan-haptic/firmware/src/main.cpp`：

1. `loop()` 加串口 RX：`Serial` 按行缓冲解析上表三条命令。
2. 新增模式 `MODE_EXT`：CURRENT 模式，电流 = 最近一条 `TENSION/SETCUR`
   映射值 + 可选基础阻尼（收线顺滑感，复用位置差分 `velEst`）。
3. 看门狗计时器（`lastExtCmdMs`）。
4. 新常量：`EXT_MAX_CURRENT`（默认 40000，鱼拉手感上限）、
   `EXT_DIR`（±1，拉力方向）、`EXT_WATCHDOG_MS=200`。
5. 触摸按钮区第 5 格显示 EXT（或保持 4 格、EXT 仅串口进——施工时定）。
6. 变体支持：`variant_config.h`（见改动三）`#define FISHING_DEFAULT_EXT 1`
   时上电默认 EXT。用 `__has_include` 引入，与 `wifi_config.h` 同机制。
7. 改完 `pio run` 验证 + 同步到 `~/Library/Application Support/RollerFlasher/firmware/src/`，
   交用户烧录。

## 改动二：Unity（RollerHapticUnity/，6000.5.7f1）

1. **串口层**：按串口计划实现 `RollerHapticSerial.cs`（System.IO.Ports，
   端口自动发现 `/dev/cu.usbmodem*`，后台线程收 + ConcurrentQueue 主线程解析），
   并加**发送**：`SendTension(float01)` / `SendRaw(string)`，发送线程安全。
   注意端口独占：RollerFlasher 监视/烧录前要 Stop Play。
2. **玩法** `FishingSim.cs`（新场景 Fishing.unity，保留 Main.unity 不动）：
   - **模型改造**：给 `RollerCAN_Rotor` 加一根**摇柄手把**（渔轮摇臂）——
     转子下程序化生成：摇臂（细长 Cube/Cylinder，沿径向伸出）+ 握把
     （短 Cylinder，垂直于摇臂末端），随转子做圆周运动，收线动作可视化；
     程序化生成优先（参数可调），美术化 Blender 重做后置。
     渔轮整体（Static_Group + 手把）挂 IMU 姿态节点，拿起 CoreS3 即"持竿"。
   - 状态机：IDLE → CAST → WAIT → BITE → FIGHT → LANDED / ESCAPED / LINE_BROKEN
   - FishModel：拉力 = 鱼种基础拉力 × (1 + 挣扎 sin + 噪声) + 随机冲刺脉冲；
     鱼有耐力值，持续高张力消耗耐力，耐力尽则拉力衰减
   - 收线：摇柄角速度 → 线收回速度；张力 = f(鱼瞬时拉力, 收线速度, 剩余线长)；
     张力 > 断线阈值持续 0.3s → LINE_BROKEN
   - 视觉从简：3D 水面平面 + 浮漂 + 线（LineRenderer）+ 鱼用胶囊体即可，
     手感优先，美术后置。UI：张力条、鱼距离、耐力条、状态文字
   - 中鱼时才发 `MODE,EXT`；LANDED/ESCAPED/LINE_BROKEN 后发 `MODE,LOCAL`
3. 调参面板（Inspector）：鱼重/拉力系数/挣扎频率/冲刺概率/断线阈值/
   `EXT_MAX_CURRENT` 映射曲线（线性或 sqrt，让小鱼也有细腻手感）。

## 改动三：RollerFlasher App（给固件选项）

「固件烧录」页签加**固件变体选择器**（与 WiFi 同步同一组机制）：

| 选项 | 效果 |
|---|---|
| 标准力反馈（默认） | 不写 variant_config.h，上电 LOCAL DETENTS |
| 钓鱼模拟器 | 生成 `src/variant_config.h`：`#define FISHING_DEFAULT_EXT 1`，上电即 EXT 等命令 |

- 实现：`VariantConfigSync.writeHeader(projectDir:variant:)`，
  构建/烧录前与 `syncWifiConfig()` 一起自动执行；选择持久化到 UserDefaults。
- 「仅写入配置」按钮同时写 wifi_config.h + variant_config.h。
- App 串口监视页可顺手加 TENSION 手动发送框（调鱼的手感不用开 Unity）——可选项。

## 施工顺序

1. 固件 RX + EXT + 看门狗 → 用串口助手发 `SETCUR,20000` 手验证扭矩方向
2. Unity 串口层（收发回环：摇柄角度动 UI，按钮发 TENSION 感受拉力）
3. Unity FishingSim 最小玩法（先手感后美术）
4. App 变体选项
5. 联调验收

## 验收清单

- 拿起 CoreS3 转动 → Unity 渔轮/鱼竿姿态实时跟随（roll/pitch 为主）；
  摇柄转动 → 手把绕轴做圆周运动，收线速度跟手
- 中鱼瞬间手上有"被拽"感；挣扎期有抖动；冲刺时扭矩饱和但不振（`EXT_MAX_CURRENT` 封顶）
- 摇柄收线：阻尼顺滑；不摇时鱼把线拖走（pos 反转，Unity 距离增加）
- 张力条到红区 0.3s → 断线，电机立刻松扭矩回 LOCAL
- Stop Play / 拔线 200ms 内电机自动释放（看门狗）
- 变体烧录：钓鱼固件上电直接进 EXT；标准固件上电 DETENTS
- 力反馈本地参数不动：DETENTS 两级棘轮 `KP_MINOR=35 KP_MAJOR=40 KD_DAMPING=1.5`、
  SPRING `KD_SPRING=4.0`、速度一律用位置差分 `velEst`（readback 寄存器延迟助振，勿回退）
