# Unity 串口联动计划（替代 WiFi/OSC）

日期：2026-09-12 ｜ 状态：**已并入 [fishing-simulator-plan.md](fishing-simulator-plan.md)**
（串口收发层为该计划"改动二-1"，本文链路设计不变，仍有效）

## 背景

- WiFi/OSC 链路搁置：CoreS3 拿到 192.168.31.x，Mac 是 192.168.x.x，
  同名 WiFi（<WIFI_SSID>）实为两个网段，OSC 单播过不去，等网络捋顺再恢复。
- 改走 **USB 串口**：烧录/供电的同一根线即是数据链路，零网络依赖。
- 现有串口遥测 `HAPTIC,<mode>,<pos_0.01deg>,<cur_cmd>`（~200Hz, 115200），
  但**不含 IMU 姿态**，Unity 里 CoreS3 白块需要 roll/pitch/yaw，固件需小改。

## 改动一：固件（小改，编译验证即可，烧录用户自己执行）

`rollercan-haptic/firmware/src/main.cpp` 遥测行扩为 7 字段：

```
HAPTIC,<mode>,<pos_0.01deg>,<cur_cmd>,<roll>,<pitch>,<yaw>
```

- roll/pitch/yaw 用现有 `imuRoll/imuPitch/imuYaw`（度，保留 2 位小数）。
- 接收端按字段数区分新旧格式，旧 4 字段解析不受影响。
- 频率保持 ~200Hz；若 Unity 侧解析掉帧再降到 100Hz（`Serial.printf` 有开销，
  200Hz × ~50 字节 ≈ 10KB/s，115200 波特够用）。
- 改完 `pio run -e m5stack-cores3` 验证编译，同步 `src/main.cpp` 到
  `~/Library/Application Support/RollerFlasher/firmware/src/`（App 实际构建目录），
  交用户烧录。

## 改动二：Unity 新增 `RollerHapticSerial.cs`

位置：`RollerHapticUnity/Assets/Scripts/RollerHapticSerial.cs`。
与 `RollerHapticSync.cs`（OSC 版）并存，**同一时刻只启用一个**：
串口版挂同一场景物体或新物体，OSC 版组件取消勾选即可回退。

设计要点：

- `System.IO.Ports.SerialPort`（Unity Mono / .NET Standard 2.1 自带，无需装包），
  115200 8N1，`ReadLine` 后台线程 + `ConcurrentQueue` 投递主线程解析。
- **端口自动发现**：macOS 枚举 `/dev/cu.usbmodem*`（插拔后编号会变，如
  usbmodem2101，不要硬编码）；Inspector `portName` 留空 = 自动。
- 解析 7 字段（兼容 4 字段）：mode 仅作调试显示，pos/100 → 电机角度，
  roll/pitch/yaw → CoreS3 姿态。
- **复用同一套校正管线**：`smoothing` / `invertYaw` / `invertMotorAngle` /
  `yawOffset` / `motorAngleOffset`，绑定 `coreS3Root=Static_Group`、
  `motorRoot=RollerCAN_Rotor`（与现 OSC 组件一致）。
- 调试只读字段：`linesPerSec`、`parseErrors`、`lastMode`、`dbgRoll/Pitch/Yaw`、
  `dbgMotorAngle/Cur`。
- 掉线处理：读失败/端口消失 → 每秒重试重开，Console 只打一次警告不刷屏。

## 注意事项 / 坑

- **端口独占**：Unity 占用串口时，RollerFlasher 的串口监视必须断开
  （烧录不受影响——App 烧录前会自动断开监视，但 Unity 侧要先 Stop Play）。
- Editor 下 macOS 无沙箱限制；将来出 Player 构建才需要
  `com.apple.security.device.serial` entitlement。
- yaw 仍是陀螺仪积分，会缓慢漂移；若明显，后续加静止自动归零或
  BMM150 磁力计校正（原 OSC 计划的第 3 条，串口方案同样适用）。

## 验收清单

1. Play 后 `linesPerSec` ≈ 200，`parseErrors` 不增长。
2. 手转 RollerCAN 旋钮 → 红圆柱（RollerCAN_Rotor）实时同步，且手上有
   两级棘轮力反馈（5° 格点 / 10° 大档，中小力度，参数已调好勿动：
   `KP_MINOR=35 KP_MAJOR=40 KD_DAMPING=1.5`）。
3. 拿起 CoreS3 转动 → 白块+红方（Static_Group）随姿态转；轴向不对调
   Inspector 的 Invert/Offset。
4. 数值与 RollerFlasher 遥测页一致（不同时连接，端口独占）。
5. Stop Play 后串口释放，RollerFlasher 能重新连上。
