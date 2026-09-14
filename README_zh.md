# M5RollerCANFishing — RollerCAN 旋钮力反馈钓鱼模拟器

[![Platform](https://img.shields.io/badge/platform-%E4%BB%85%20macOS-lightgrey)](README_zh.md)
[![Unity](https://img.shields.io/badge/Unity-6.x%20%7C%202022.3%20LTS%20Built--in%20RP-black?logo=unity)](RollerHapticUnity/)
[![PlatformIO](https://img.shields.io/badge/PlatformIO-ESP32--S3%20(CoreS3)-orange?logo=platformio)](firmware-fishing/)
[![Hardware](https://img.shields.io/badge/M5Stack-CoreS3%20%2B%20RollerCAN-blue)](README_zh.md)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)
[![English README](https://img.shields.io/badge/lang-English-blue)](README.md)

M5Stack CoreS3 + RollerCAN（I2C 力反馈电机旋钮）+ Unity 的钓鱼模拟器：
RollerCAN 旋钮 = 鱼线轮摇柄，CoreS3 IMU = 鱼竿姿态，鱼线张力实时变成电机反向扭矩。
甩竿抛投（加速度计手势）、像素鱼精灵、水面 shader、渔获计数一应俱全。

> **仅适用于 macOS**（Apple Silicon / Intel 均可，仅在 macOS 上开发与验证）：
> RollerFlasher 烧录器是 macOS SwiftUI app，Blender 建模脚本走 macOS Steam 版 Blender，
> Unity 侧目标版本为 **Unity 6.x**（工程版本 6000.5.7f1，Built-in 管线）——脚本同时
> 通过 **2022.3 LTS** 引用集编译验证，2022.3.x 亦可使用。固件（PlatformIO/ESP32-S3）
> 本身跨平台，但整条工具链没有 Windows/Linux 支持计划。

## 硬件

- M5Stack CoreS3（内置 BMI270 IMU）
- M5Stack RollerCAN（I2C 无刷力反馈旋钮，地址 0x64，Port.A SDA=2/SCL=1）
- 同一 WiFi 下的 Mac（Unity 端）

## 仓库结构

| 目录 | 内容 |
|---|---|
| `firmware-fishing/` | **钓鱼专用固件**（干净版：上电即钓鱼 HUD，无本地模式，BtnA=调平） |
| `firmware/` | 标准力反馈固件（本地 4 模式 DETENTS/SPRING/ENDSTOP/FREE + 串口/UDP EXT） |
| `RollerFlasher/` | macOS 烧录器（SwiftPM）：WiFi 配置写入、变体选择、串口监视 |
| `RollerHapticUnity/` | Unity 工程（Built-in 管线）：Fishing 场景、水面 shader、像素鱼 |
| `package/com.rollerhaptic.fishing/` | Unity 内容的 UPM 打包版（可 Add package from disk 导入新工程） |
| `blender/` | 渔轮模型参数化脚本 `roller_model.py` + .blend + 预览图 |
| `tools/` | `png_to_rgb565.py`（鱼图 → 固件 RGB565 C 数组） |
| `assets/fish/` | 7 张像素鱼 PNG（去背 RGBA，固件与 Unity 共用同款素材） |
| `docs/` | 全部施工计划与排障记录（含姿态估计/轴映射/协议细节） |
| `prompt-rebuild/` | **纯提示词重建项目**：一份激活提示词 + 必要素材，从零复刻整个项目 |

## 快速开始

1. 固件：打开 RollerFlasher（或自行 `pio run -t upload`），默认变体 = 钓鱼模拟器；
   烧录前 App 会把 WiFi 配置写进 `src/wifi_config.h`（仓库内固件默认值为占位符
   `YOUR_WIFI_SSID` / `YOUR_WIFI_PASS` / `192.168.1.100`，请按需修改）。
2. Unity：用 Unity Hub 打开 `RollerHapticUnity/`，打开 `Assets/Scenes/Fishing.unity`，
   Play。CoreS3 IP 自动发现（OSC 包来源），无需配置。
3. 操作：甩竿 = 抛投；咬钩后快摇手柄 = 扬竿；FIGHT 中摇柄收线，张力红区 0.3s 断线；
   鱼上岸后甩竿再来一竿。右上角「IMU 调平」按钮校准姿态零点。

## 通信协议（同一套 ASCII 行，串口 115200 或 UDP :9000）

- Unity → CoreS3：`TENSION,<0..1000>` / `SETCUR,<n>` / `FISH,<0..6>` / `MODE,EXT` /
  `MODE,LOCAL` / `LEVEL` / `PING`
- CoreS3 → Unity（OSC，~100Hz → :8000）：`/rollercan/imu`（欧拉角）、
  `/rollercan/quat`（四元数，主用）、`/rollercan/motor`（角度/电流）、
  `/rollercan/acc`（线性加速度幅值，甩竿检测）

## 许可

MIT（见 [LICENSE](LICENSE)）。开发工作区 `_backup/` 中的 KriptoFX WaterSystem2
为商业资产，不包含在本仓库内。
