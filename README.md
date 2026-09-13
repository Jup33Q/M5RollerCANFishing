# M5RollerCANFishing — RollerCAN Haptic-Knob Fishing Simulator

[![Platform](https://img.shields.io/badge/platform-macOS%20only-lightgrey)](README.md)
[![Unity](https://img.shields.io/badge/Unity-2022.3%2B%20Built--in%20RP-black?logo=unity)](RollerHapticUnity/)
[![PlatformIO](https://img.shields.io/badge/PlatformIO-ESP32--S3%20(CoreS3)-orange?logo=platformio)](firmware-fishing/)
[![Hardware](https://img.shields.io/badge/M5Stack-CoreS3%20%2B%20RollerCAN-blue)](README.md)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)
[![中文 README](https://img.shields.io/badge/lang-%E4%B8%AD%E6%96%87-red)](README_zh.md)

A haptic fishing simulator built on **M5Stack CoreS3 + RollerCAN** (I2C force-feedback
knob motor) + **Unity**: the RollerCAN knob is the fishing-reel handle, the CoreS3 IMU
is the rod attitude, and line tension becomes counter-torque on the motor — the gameplay
is in your hand. Whip-cast via accelerometer gesture, pixel-fish sprites on the device
screen, procedural water shader, catch counter and more.

> **macOS only** (Apple Silicon / Intel, developed and verified on macOS):
> the RollerFlasher uploader is a macOS SwiftUI app, the modeling script targets the
> macOS Steam build of Blender, and the Unity side is verified with
> Unity 2022.3.62f3c1 / 6000.x on macOS. The firmware itself (PlatformIO / ESP32-S3)
> is cross-platform, but the toolchain as a whole has no Windows/Linux support planned.

## Hardware

- M5Stack CoreS3 (built-in BMI270 IMU)
- M5Stack RollerCAN (I2C brushless force-feedback knob, addr 0x64, Port.A SDA=2/SCL=1)
- A Mac running Unity on the same WiFi network

## Repository layout

| Path | Contents |
|---|---|
| `firmware-fishing/` | **Dedicated fishing firmware** (clean build: boots straight into the fishing HUD, no local modes, BtnA = level) |
| `firmware/` | Standard haptic firmware (4 local modes: DETENTS/SPRING/ENDSTOP/FREE + serial/UDP EXT) |
| `RollerFlasher/` | macOS uploader (SwiftPM): WiFi config injection, firmware-variant picker, serial monitor |
| `RollerHapticUnity/` | Unity project (Built-in RP): Fishing scene, water shader, pixel fish |
| `package/com.rollerhaptic.fishing/` | Unity content as a UPM package (import into a new project via *Add package from disk*) |
| `blender/` | Parametric reel-model script `roller_model.py` + .blend + preview renders |
| `tools/` | `png_to_rgb565.py` (fish PNG → RGB565 C array for the firmware) |
| `assets/fish/` | 7 pixel-fish PNGs (background-removed RGBA, shared by firmware and Unity) |
| `docs/` | All build plans and debugging records (attitude estimation, axis mapping, protocol details) |
| `prompt-rebuild/` | **Pure-prompt rebuild kit**: activation prompts (EN/ZH) + assets to recreate the whole project from scratch |

## Quick start

1. Firmware: open RollerFlasher (or run `pio run -t upload` yourself); the default
   variant is the fishing firmware. Before building, the app writes your WiFi settings
   into `src/wifi_config.h` (the repo ships placeholder defaults `YOUR_WIFI_SSID` /
   `YOUR_WIFI_PASS` / `192.168.1.100` — edit as needed).
2. Unity: open `RollerHapticUnity/` in Unity Hub, open `Assets/Scenes/Fishing.unity`,
   press Play. The CoreS3 IP is auto-discovered from incoming OSC packets — zero config.
3. Controls: whip the device to cast; quick crank on bite = hook-set; crank to reel
   during FIGHT, watch the tension bar (0.3 s in the red = line breaks); after landing
   a fish, whip again for the next cast. The "IMU 调平" button (top right) re-zeroes
   the attitude.

## Protocol (one ASCII-line protocol over both serial 115200 and UDP :9000)

- Unity → CoreS3: `TENSION,<0..1000>` / `SETCUR,<n>` / `FISH,<0..6>` / `MODE,EXT` /
  `MODE,LOCAL` / `LEVEL` / `PING`
- CoreS3 → Unity (OSC, ~100 Hz → :8000): `/rollercan/imu` (euler, legacy),
  `/rollercan/quat` (quaternion, primary), `/rollercan/motor` (angle/current),
  `/rollercan/acc` (linear-acceleration magnitude for whip detection)

## License

MIT (see [LICENSE](LICENSE)). For learning and tinkering. The `_backup/` folder from
the development workspace (KriptoFX WaterSystem2, a commercial asset) is **not**
included in this repository.
