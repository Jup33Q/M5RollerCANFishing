# 固件像素鱼游戏化界面 修改计划（固件精灵图 + Unity 鱼种系统）

日期：2026-09-12 ｜ 状态：**代码完成（步骤 1–4），待用户烧录 + Play 联调验收（步骤 5）** ｜ 前置：[fishing-simulator-plan.md](fishing-simulator-plan.md)（主线 1–4 已完成，待联调）

## 目标

1. 钓鱼模拟器固件（EXT 模式）的 CoreS3 屏幕界面游戏化：像素鱼精灵图 + 张力条 +
   鱼名，替代原来的纯文字遥测界面。
2. 鱼图用本地 FLUX.2-klein 生成（像素风、侧视图、品红纯色背景）→ 去背景 →
   转 RGB565 C 数组烧进固件。
3. **每一种鱼与 Unity 中的鱼种一一对应**：Unity 中鱼时随机选种，串口下发
   `FISH,<id>`，固件屏幕显示对应精灵；Unity 侧鱼胶囊体颜色在基色上做 HSV 随机抖动
   （同种鱼每条颜色略有差异）。

## 鱼种表（7 种，id 即协议 `FISH,<id>` 与精灵数组下标）

| id | 鱼种 | basePull（手感，0..1） | Unity 基色 | 备注 |
|---|---|---|---|---|
| 0 | 鲫鱼 crucian | 0.30 | 金黄 | 已出图 ✅ |
| 1 | 锦鲤 koi | 0.45 | 橙红白 | 已出图 ✅ |
| 2 | 鲈鱼 bass | 0.60 | 绿 | 已出图 ✅ |
| 3 | 鲶鱼 catfish | 0.80 | 蓝紫 | 已出图 ✅ |
| 4 | 小虾 shrimp | 0.15 | 橙红 | **待出图** |
| 5 | 小螃蟹 crab | 0.25 | 红 | **待出图** |
| 6 | 萨卡班甲鱼 sacabambaspis | 0.50 | 灰绿 | **待出图**，彩蛋鱼，那张呆萌脸 |

## 当前状态

- 已生成（`rollercan-haptic/assets/fish/fish_{0..3}_raw.png`，512×512，品红背景，
  像素风侧视图朝左）：鲫鱼金、锦鲤橙红白、鲈鱼绿、鲶鱼蓝紫，质量均可直接用。
- **2026-09-12 补齐**：小虾（seed 1831431115）/ 小螃蟹（seed 692368144，正面视角但可用）/
  萨卡班甲鱼（seed 1492947821）已出图；7 张全部 birefnet 去背景 → `fish_N.png`（RGBA）。
- `tools/png_to_rgb565.py`（Pillow 装在 `.venv-pio`）已生成 `firmware/src/fish_sprites.h`
  （7×96×96 RGB565 + FISH_SPRITES 索引表，526KB 源文件 / flash 占用 16.3% 编译通过）；
  `--check` 回渲 `assets/fish/fish_0_check.png` 已抽查颜色/透明键正确。
- 固件 `main.cpp` 已改完并 `pio run` 通过、同步到 RollerFlasher 固件目录：
  canvas 消频闪（canvasMain 数据区 + canvasFish 整屏）、钓鱼 HUD、`FISH,<id>` 协议。
- Unity `FishingSim.cs` 鱼种系统已加，Mono csc + unity-4.8-api 引用集独立编译通过。
- **未做**：步骤 5 验收（烧录 + 串口手动命令 + Unity Play，均由用户执行）。

## 剩余施工步骤

### 1) 补齐出图 + 去背景

- 3 张新图用 flux-klein `generate_image`，prompt 模板沿用已验证的（512×512，
  `solid pure magenta background` 方便抠图）：
  - `16-bit retro pixel art game sprite of a single small shrimp, side view facing left, orange-red body, chunky visible pixels, ...`
  - `... small crab, side view facing left, red body with two claws, ...`
  - `... sacabambaspis (ancient jawless fish, derpy round face, big eyes), grey-green body, ...`
- 7 张全部过 `sam_edit`（engine=birefnet，op=remove_background）→ `fish_N.png`（RGBA）。

### 2) 转 RGB565 C 数组

- 脚本 `rollercan-haptic/tools/png_to_rgb565.py`（Pillow，装在工作区 `.venv-pio` 或复用）：
  - 输入 RGBA PNG → 缩放到 **96×96**（LANCZOS）；alpha < 128 的像素输出透明键
    **0xF81F（品红）**（鱼图黑描边，不能用黑色当透明键）；其余转 RGB565。
  - 输出 `firmware/src/fish_sprites.h`：
    `const uint16_t fish_N[96*96] PROGMEM` × 7 +
    `const uint16_t* FISH_SPRITES[7]` 索引表。
  - 体积：7 × 96 × 96 × 2B ≈ 129KB，flash 够用。
- 转换后抽查一张数组回渲 PNG 确认颜色/透明键没错。

### 3) 固件改动（`firmware/src/main.cpp`）

**A. 频闪修复（优先做，正常模式也受益）**：现状 `updateDynamicUI()` 每 100ms
`fillRect` + 大字体重绘，屏幕闪烁严重。改为 **M5Canvas 离屏渲染**：
所有界面元素画进一张全屏（或中部数据区）`M5Canvas`，一次性 `pushSprite` 上屏，
替代逐块 fillRect。本地 4 模式界面和钓鱼界面都走这条路径；静态区（标题/按钮）
仍只在初始化画一次。

**B. 钓鱼变体界面与正常模式明确区分**（不能只是换个模式名）：
- 正常模式：保持现有黑底工程风遥测界面（只是改走 canvas 消频闪）。
- EXT 钓鱼界面：整屏游戏 HUD 风格——深蓝水底渐变/深底色背景、顶部大标题
  "FISHING!"（或鱼种名）、96×96 像素鱼精灵居中偏左、右侧大号张力百分比、
  底部贯穿整宽的绿→黄→红张力条、>0.95 红框闪烁断线预警、看门狗未武装显示
  "WAITING UNITY..."；触摸按钮区在 EXT 下隐藏或变灰（EXT 仅串口进出）。
- 钓鱼变体**上电即显示该界面**（FISHING_DEFAULT_EXT 默认 EXT，等 `FISH` 命令前
  显示占位图/问号精灵），一眼可区分烧的是哪个固件。

**C. 协议与逻辑**：
- `#include "fish_sprites.h"`；串口协议新增：
  `FISH,<0..6>` —— 记录 `extFishId`，EXT 界面切到对应精灵；`MODE,LOCAL` 时
  `extFishId = -1`。
- `updateDynamicUI()` 分流：`mode == MODE_EXT` 走 `drawFishingUI()`（canvas 版），
  本地 4 模式走原界面（canvas 版）。
- `pushImage` 用透明键重载：`pushImage(x, y, 96, 96, FISH_SPRITES[id], 0xF81F)`；
  canvas 版则 `canvas.drawPng`→ 不对，用 `canvas.pushImage(..., 0xF81F)` 合成后
  随 canvas 一起 `pushSprite`。
- 精灵只在 `FISH` 命令或模式切换时重绘；100ms 刷新只重画张力条/数字到 canvas
  再 push（canvas 方案下整区重画也无频闪）。

### 4) Unity 改动（`FishingSim.cs`，纯代码，不用重建场景）

- 新增鱼种表（代码内默认初始化，Inspector 可调）：
  ```csharp
  [System.Serializable] public class FishSpecies {
      public string name; public float basePull; public Color baseColor;
  }
  public FishSpecies[] species = { /* 7 行，同上表 */ };
  ```
- `StartFight()`：`idx = rng.Next(species.Length)` → `fishBasePull = sp.basePull` →
  `serial.SendRaw($"FISH,{idx}")` → 鱼胶囊上色：
  `MaterialPropertyBlock` 设 `_Color` = 基色做 HSV 抖动（hue ±0.03、sat/val ±0.15，
  `Color.HSVToRGB`）。
- OnGUI FIGHT 状态显示鱼种名。
- csc 独立编译验证（引用 UnityEngine.*.dll 全套 + UnityEditor.CoreModule，
  命令见会话记录；编辑器开着的话等用户切回 Unity 自动编译，看 Console 无红）。

### 5) 验收

- 串口助手/App 手动命令发 `FISH,2` + `MODE,EXT` → CoreS3 屏显示鲈鱼精灵 + 张力条；
  `TENSION,800` 条变红；`MODE,LOCAL` 回本地界面。
- **界面区分**：钓鱼变体上电即是游戏 HUD 界面，与正常模式工程风界面一眼可辨；
  本地 4 模式界面外观不变。
- **频闪**：正常模式与钓鱼界面在 100ms 遥测刷新下均无可见闪烁（canvas 离屏渲染）。
- Unity FIGHT 中鱼：屏幕精灵与 Unity 鱼种一致；同种鱼多次中鱼颜色有差异。
- 主线验收清单（fishing-simulator-plan.md）其他项不回归。

## 注意

- 鱼图朝左；固件如需朝右用 `pushImage` 前水平翻转数组或 M5GFX 的 `setSwapBytes`/翻转参数。
- **字节序（已修正）**：M5GFX/LovyanGFX 的 `pushImage(x,y,w,h,data,transparent)` 按
  host order 读 uint16 内存数组（ESP32 小端原生，0xF800=红），`setSwapBytes` 只影响
  SPI 传输与此无关——脚本直接存原生值，不要再转 big-endian。若实机色差再查。
- **渐变 API（已修正）**：M5GFX 没有 `fillGradientV/H`，用
  `fillGradientRect(x,y,w,h,start,end, m5gfx::VLINEAR / HLINEAR)`。
- 已生成的图 seed：小虾 1831431115、螃蟹 692368144、萨卡班甲鱼 1492947821（前 4 张
  seed 记在更早会话），重出可复现。
