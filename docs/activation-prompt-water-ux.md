# 钓鱼模拟器 · 激活提示词（水面 Shader + 视野修复 + 调平按钮）

> 用法：新会话里整段粘贴。计划文档是自洽的，细节以文档为准。

```
继续 RollerCAN 钓鱼模拟器项目。工作区：<WORKDIR>

先读三份计划：
- rollercan-haptic/docs/fishing-simulator-plan.md（主线：协议/变体/验收清单）
- rollercan-haptic/docs/fish-sprites-gamification-plan.md（像素鱼 HUD，代码已完成）
- rollercan-haptic/docs/water-shader-and-ux-plan.md（本轮任务：水面 shader、渔轮遮挡修复、Game 界面调平按钮，按其任务 1→3 执行）

当前状态：
- 链路已切 UDP/OSC 双通道：RollerHapticSync.cs 收发一体（CoreS3 IP 从 OSC 来源自动发现，
  PING/PONG 连接确认）；RollerHapticSerial.cs 串口链路保留备用；FishingSim.cs 双链路抽象
  （LinkOpen/LinkMotorAngle/LinkSend），组件 FindAnyObjectByType 自动发现
- 固件 main.cpp：UDP :9000 命令通道、PING/PONG、自动调平（静止近水平 3s）、LEVEL 命令、
  本地界面 IMU 三轴行 [LVL]、钓鱼 HUD（canvas 离屏无频闪、FISH,<id> 精灵、张力条、
  UNITY LINKED 状态行）；pio 编译已验证
- Unity 工程 RollerHapticUnity（6000.5.7f1，Built-in 管线，.NET Framework）：
  render-pipelines.core/shadergraph 已从 manifest 移除（编辑器安装不完整缺 PathTracing，
  勿再引入 URP/ShaderGraph）；KWS 已移出到 _backup/KriptoFX/
- 代码改动验证：固件用 rollercan-haptic/.venv-pio/bin/pio run；
  Unity 脚本用 Mono csc + unity-4.8-api 引用集独立编译（命令模式见会话惯例）；
  场景改动直接编辑 Fishing.unity YAML（Unity GUI 开着不能 batch，改完让用户重开场景、勿 Ctrl+S）
- 固件改动后同步 ~/Library/Application Support/RollerFlasher/firmware/src/ 与
  ~/Desktop/RollerFlasher.app/Contents/Resources/firmware/src/（bundle 在 /tmp 重签名再 rsync 回）

协作约定：
- 本地力反馈参数（KP_MINOR=35 KP_MAJOR=40 KD_DAMPING=1.5 KD_SPRING=4.0、velEst 位置差分）勿动
- 固件烧录、Unity Play、场景重开均由用户执行；视觉改动用截图验收
- RollerFlasher 烧录走「构建并烧录」（自动写 wifi_config.h + variant_config.h）
```

## 本轮任务速览（细节在 water-shader-and-ux-plan.md）

1. `Assets/Shaders/WaterSurface.shader`：Built-in Surface Shader + GrabPass 折射 +
   程序噪声波动法线 + 菲涅尔天空反射 + 太阳高光闪鳞，透明；改 `Water.mat` 的
   m_Shader 引用应用
2. 渔轮模型（IMU_Rig/RollerHapticModel）Game 视图太大挡视野：改场景 YAML 缩小
   localScale / 下移，截图验收
3. FishingSim.OnGUI 加「IMU 调平」按钮发 LEVEL 命令（双链路）
