# RollerCAN 力反馈旋钮 Demo（M5Stack CoreS3）

## 硬件

- **RollerCAN Unit (SKU: U188)**：STM32G431 + FOC 驱动 3504 200KV 无刷电机，
  磁编码器反馈，支持电流 / 速度 / 位置三环控制，CAN 或 I2C 控制。
- **CoreS3**：通过 Grove **Port.A**（SDA=G2, SCL=G1, 400kHz）以 I2C 连接 RollerCAN（默认地址 0x64）。

> Grove 5V 供电扭矩仅 0.021 N·m，手感偏轻；要明显的力反馈请用 XT30 接口供 6-16V（可达 0.065 N·m）。
> 电压切勿超过 16V。

## 力反馈原理

力反馈 = **Current Mode（电流≈扭矩）+ 位置闭环软件建模**。
CoreS3 以 ~200Hz 读取 `getPosReadback()`（单位 0.01°，36000 = 一圈），
按虚拟物理模型计算扭矩后用 `setCurrent()` 输出：

| 模式 | 手感 | 模型 |
|---|---|---|
| DETENTS | 两级棘轮 | 5° 格点 / 10° 大档（吸附更宽），均为中小力度 |
| SPRING | 弹簧回中 | 扭矩 ∝ 偏离角度，松手回零 |
| ENDSTOP | 自由旋转 + ±180° 硬限位 | 越界施加反向"墙"扭矩 |
| FREE | 无阻尼，纯编码器输入 | 输出关闭 |

调参入口都在 `firmware/src/main.cpp` 顶部常量区（`CURRENT_MAX`、`KP_*`、
`TICK_*` / `CAP_*`（多级棘轮）、`WALL_*`）。电流指令范围 -120000 ~ 120000
（设备内部缩放，非真实 mA），先小后大逐步调。

## 协作约定

- **固件烧录一律由用户自己执行**（用桌面 RollerFlasher.app 的"构建并烧录"）。
  Agent 只负责：改固件代码 → `pio run` 验证编译 → 把 `firmware/src/` 同步到
  `~/Library/Application Support/RollerFlasher/firmware/`（App 实际构建的目录），
  然后交用户烧录。
- CoreS3 原生 USB 重枚举较快，若自动复位失败（`No serial data received`），
  重新插拔 USB 或重试一次即可。

## WiFi / OSC 输出（Unity 联动）

固件启动时连接 WiFi，以 ~100Hz 向 `TARGET_IP:8000` 发 OSC（UDP）：

| 地址 | 参数 | 含义 |
|---|---|---|
| `/rollercan/imu` | fff | roll pitch yaw（度，互补滤波；yaw 仅陀螺仪积分会缓慢漂移） |
| `/rollercan/motor` | ff | 电机角度（度）、电流指令 |

WiFi 配置在 `firmware/src/main.cpp` 顶部（`WIFI_SSID` / `WIFI_PASS` / `TARGET_IP`）。
注意 ESP32-S3 只支持 2.4GHz，路由器需开 2.4G/5G 双频合一或单独的 2.4G SSID。

Unity 侧：`RollerHapticUnity/Assets/Scripts/RollerHapticSync.cs` 自包含 OSC 解析，
挂场景物体上，拖入 `coreS3Root`（CoreS3 静止组）和 `motorRoot`（RollerCAN 转子），
Inspector 里的 `Invert*` / `*Offset` 用于轴向校正。

> OSC 因网段不一致暂搁置时的替代方案（USB 串口直连 Unity）见
> [docs/unity-serial-link-plan.md](docs/unity-serial-link-plan.md)；
> 钓鱼模拟器（Unity 张力 → 电机扭矩）总施工计划见
> [docs/fishing-simulator-plan.md](docs/fishing-simulator-plan.md)。

## 使用方式

- 触摸屏底部 4 个按钮直接切换模式；CoreS3 物理 **BtnA** 循环切换。
- 串口（115200）持续输出遥测：`HAPTIC,<mode>,<pos_0.01deg>,<cur_cmd>`。

## 关键 API（M5Unit-Roller 库，UnitRollerI2C）

```
begin(&Wire, 0x64, sda, scl, 400000)
setMode(ROLLER_MODE_CURRENT)      // 1速度 2位置 3电流 4编码器
setCurrent(int32)                 // 目标电流=扭矩指令
getPosReadback()                  // 实时位置，0.01° 单位
getCurrentReadback() getTemp() getErrorCode()
setOutput(0/1)                    // 使能/释放
setStallProtection(0)             // 力反馈务必关堵转锁定
saveConfigToFlash() / startAngleCal()  // 配置保存与编码器校准
```

参考：
- 驱动库 https://github.com/m5stack/M5Unit-Roller
- 文档 https://docs.m5stack.com/zh_CN/unit/Unit-RollerCAN
- 内部固件（开源，可研究 FOC 实现）https://github.com/m5stack/M5Unit-RollerCAN-Internal-FW

## 烧录与串口工具：RollerFlasher（macOS SwiftUI）

```
cd RollerFlasher && swift run          # 启动 App
```

两个页签：

1. **串口监视**：枚举 `/dev/cu.usb*` 设备，115200 连接 CoreS3，
   实时解析 HAPTIC 遥测并画出角度曲线。
2. **固件烧录**：选择 `firmware/` 工程目录 →
   - `构建`：`pio run -e m5stack-cores3`
   - `构建并烧录`：`pio run -t upload --upload-port <串口>`（自动进下载模式，推荐）
   - `烧录已编译 .bin`：直接调 esptool 写 0x0 整包

命令行等价操作：

```
cd firmware
pio run -e m5stack-cores3                               # 构建
pio run -e m5stack-cores3 -t upload --upload-port /dev/cu.usbmodemXXXX  # 烧录
pio device monitor -b 115200                            # 监视串口
```
