# RollerCAN Fishing Simulator — One-Shot Rebuild Activation Prompt (EN)

> How to use: create an empty directory as the workspace, copy the whole
> `prompt-rebuild/` folder into it, open Kimi Code, and paste the **entire content
> of this file** as the first message. The agent will rebuild the complete project
> from scratch per the spec below. This kit already ships everything that cannot be
> regenerated from a prompt: fish art (`assets/fish/`), the required skills
> (`skills/`), and all build records (`docs/`).
>
> 中文版本：[ACTIVATE_zh.md](ACTIVATE_zh.md)

---

## Mission

Rebuild the complete "RollerCAN haptic fishing simulator" project from scratch
(**macOS only**):

- **Hardware**: M5Stack CoreS3 (BMI270 IMU) + M5Stack RollerCAN (I2C force-feedback
  knob motor, address 0x64, Port.A SDA=2/SCL=1, 400 kHz). The RollerCAN knob is the
  fishing-reel handle; the CoreS3 is the rod attitude. Line tension becomes
  counter-torque on the motor — the gameplay is in your hand.
- **Components**: ① dedicated fishing firmware (PlatformIO) ② standard haptic
  firmware ③ RollerFlasher macOS uploader (SwiftPM / SwiftUI) ④ Unity fishing
  simulator (Built-in render pipeline) ⑤ Blender parametric reel model.
- Workspace layout (create in place):

```
<workspace>/
├── firmware-fishing/      # dedicated fishing firmware (clean build)
├── firmware/              # standard haptic firmware (4 local modes)
├── RollerFlasher/         # macOS uploader, SwiftPM package
├── RollerHapticUnity/     # Unity project
├── blender/roller_model.py# parametric reel-model script
├── tools/png_to_rgb565.py # fish PNG -> RGB565 C array
├── assets/fish/           # ALREADY PRESENT: fish_0..6.png (background-removed RGBA)
└── docs/                  # ALREADY PRESENT: 5 build plans (read first — all pitfalls)
```

## Read the docs first

The 5 plans under `docs/` are the full project history. Read them before building:
fishing-simulator-plan.md (main protocol / acceptance checklist),
unity-serial-link-plan.md (link layer), fish-sprites-gamification-plan.md
(pixel-fish HUD), water-shader-and-ux-plan.md (water shader + model build log),
imu-axis-mapping-fix-plan.md (attitude-estimation rewrite + axis-mapping
calibration + prefab-ization + whip detection — ten rounds of build records).

## Protocol (one ASCII-line protocol over both serial 115200 and UDP :9000)

- Unity → CoreS3: `TENSION,<0..1000>` (tension per-mille -> current = ratio ×
  EXT_MAX_CURRENT × EXT_DIR), `SETCUR,<-60000..60000>`, `FISH,<0..6>` (fish sprite),
  `MODE,EXT` / `MODE,LOCAL`, `LEVEL` (re-zero attitude), `PING` (heartbeat; firmware
  replies PONG; does NOT arm the watchdog).
- CoreS3 → Unity: serial `HAPTIC,EXT,<pos_0.01deg>,<cur>,<roll>,<pitch>,<yaw>`
  ~200 Hz; OSC/UDP ~100 Hz → :8000: `/rollercan/imu fff` (euler, legacy),
  `/rollercan/quat ffff` (x,y,z,w, leveled — **primary**), `/rollercan/motor ff`
  (angle/current), `/rollercan/acc ff` (linear-acceleration magnitude: instantaneous
  + 15 Hz low-pass, for whip-cast detection).
- The CoreS3 IP is auto-discovered by Unity from the source address of incoming OSC
  packets — zero configuration.
- OSC encoding pitfall: strings must be NUL-terminated FIRST and then padded to
  4-byte alignment (otherwise the receiver fails to parse every packet).

## ① Fishing firmware `firmware-fishing/` (PlatformIO, board m5stack-cores3)

- lib_deps: `m5stack/M5Unified@^0.2.5`, `m5stack/M5GFX@^0.2.7`,
  `https://github.com/m5stack/M5Unit-Roller.git`
- Boots straight into the fishing HUD (offscreen-canvas rendering, no flicker) with
  the motor in current mode on standby. **No local modes, no touch-mode switching;
  BtnA = LEVEL** (a firm whip grip cannot knock it out of the HUD).
- Attitude estimation: gyro **quaternion integration + Mahony accelerometer tilt
  correction** (shortest arc, no singularities, no wrap-around; the estimated "up"
  vector is the THIRD ROW of R(q) — Madgwick's paper uses the opposite quaternion
  convention, do not copy it blindly). Auto-level after 3 s lying still near
  horizontal; LEVEL stores a quaternion zero; output = lv⁻¹ ⊗ q.
- Linear acceleration: rotate body-frame accel into the leveled world frame, remove
  gravity, take magnitude → instantaneous + 15 Hz low-pass.
- Watchdog: 200 ms without a substantive command → `releaseTorque()` (current zero,
  disarm); the UI always stays on the fishing HUD. All current commands pass through
  clampCur(±CURRENT_MAX=60000).
- Feel parameters (do not change): EXT_MAX_CURRENT=40000, **EXT_DIR=-1** (fish drag
  opposes the reeling direction), KD_DAMPING=1.5, velocity estimated by position
  differencing (the speed readback register lags and excites oscillation — never
  revert to it).
- Fishing HUD: deep-indigo gradient + "FISHING!" title + 96×96 pixel-fish sprite
  (transparent key 0xF81F, `canvas.pushImage(x,y,96,96,FISH_SPRITES[id],0xF81F)`) +
  large tension % + full-width green→red tension bar + red flashing frame ≥0.95 +
  WAITING UNITY / UNITY LINKED + IMU axes with [LVL] mark.
- Fish sprite table (fish_sprites.h generated by tools/png_to_rgb565.py,
  7×96×96 RGB565): 0 crucian / 1 koi / 2 bass / 3 catfish / 4 shrimp / 5 crab /
  6 sacabambaspis.
  M5GFX `pushImage` reads uint16 in host order (ESP32 is little-endian native) — do
  NOT byte-swap; gradients use `fillGradientRect(..., m5gfx::VLINEAR/HLINEAR)`
  (there is no fillGradientV/H).
- WiFi config: `#if __has_include("wifi_config.h")` with placeholder defaults
  YOUR_WIFI_SSID / YOUR_WIFI_PASS / 192.168.1.100; RollerFlasher generates the real
  header before every build.

## ② Standard firmware `firmware/`

Same codebase plus 4 local modes (DETENTS two-tier ratchet KP_MINOR=35/KP_MAJOR=40/
TICK_MINOR=500/TICK_MAJOR=1000/CAP_MAJOR=250, SPRING KP=15/KD_SPRING=4.0, ENDSTOP
walls ±18000, FREE), touch button bar + BtnA mode cycling, EXT entered/exited only
via commands, watchdog timeout returns to LOCAL DETENTS. Attitude / OSC / command
protocol identical to the fishing firmware.

## ③ RollerFlasher (macOS SwiftPM executable, `swift build -c release`)

- Two tabs: serial monitor (HAPTIC parsing + angle chart + manual command box) /
  firmware flashing (variant picker, port picker, WiFi config, build / build+upload /
  flash existing .bin).
- Variant = separate project: fishing → `firmware-fishing`, standard → `firmware`.
  The .app bundle carries one copy of each under Resources; on first run each is
  seeded to a writable working copy at
  `~/Library/Application Support/RollerFlasher/<project>`; wifi_config.h is written
  before every build.
- pio/esptool lookup paths: /opt/homebrew/bin, ~/.platformio/penv/bin, etc.;
  disconnect the serial monitor automatically before flashing to avoid port
  contention.
- Packaging the .app: Contents/MacOS = release binary, Contents/Resources = both
  firmware projects; sign with `codesign --force --deep --sign -`
  (run `xattr -cr` first to strip extended attributes).

## ④ Unity project `RollerHapticUnity/` (Built-in RP — do NOT add URP/ShaderGraph)

- Scene Fishing.unity: water surface (WaterSurface.shader: Surface + GrabPass
  refraction distortion + analytic-derivative sine normals + fbm detail gated by
  `_Detail` + fresnel + glitter specular; WaterQuality.cs lowers quality in edit
  mode), RollerHapticSync (OSC link), FishingSim (gameplay + OnGUI UI), IMU_Rig
  (prefab: reel model + RodTip), fixed camera at (0,1.6,-2.4) pitched down 30°.
- RollerHapticSync.cs: UDP :8000 OSC receive (self-contained minimal decoder,
  big-endian floats); quat channel takes priority, axis mapping
  `(x,y,z)→(x,-y,-z)` (calibrated on the real device — a true rotation map);
  Slerp smoothing; euler fallback with glitch reject+resync; motorRoot refound by
  name ("RollerCAN_Rotor") when the reference breaks, coreS3Root refound via
  GameObject.Find("IMU_Rig"); motorAxis=+Z, invertMotorAngle=1; commands over UDP
  :9000, PING heartbeat every 0.5 s; IMU CSV recording with quat/accRaw/accLp
  columns.
- FishingSim.cs: state machine IDLE→CAST→WAIT→BITE→FIGHT→LANDED/ESCAPED/
  LINE_BROKEN; whip detection (dbgAccRaw ≥ whipThreshold 5 m/s², 1.5 s cooldown,
  armed in IDLE and in all three end states — a whip in an end state casts again
  directly); 7-species table (basePull feel + base color with HSV jitter); tension =
  fish pull × (1 + struggle sine + noise) + sprint bursts, plus reel-speed bonus;
  0.3 s in the red zone = line break; stamina drain / exhausted-fish fade;
  fish sprite = Resources/fish/fish_N.png on a billboard quad (root level, follows
  the fish anchor — never parent it under the non-uniformly scaled capsule);
  LANDED parabolic leap animation + easeOutBack banner + catch counter panel;
  bottom-left attitude joystick (live mirror of IMU yaw/pitch); reel progress bar.
- IMU_Rig.prefab: nested FBX model instance + RodTip; scenes reference
  across nesting via stripped-Transform chains. **Never export the FBX with an
  ARMATURE** (skinned-mesh double-rendering bug); flat nodes keep fileIDs stable.

## ⑤ Blender model (`blender/roller_model.py`)

> **There is a dedicated prompt for the model**: `prompt-rebuild/BLENDER-MODEL.md`
> (English; 中文： BLENDER-MODEL_zh.md), paired with four reference renders under
> `assets/model-ref/`. Use it for the modeling subtask — it is more detailed.

## Collaboration & verification conventions

- Firmware verification: `pio run` (install PlatformIO yourself). Unity scripts can
  be compile-checked without the editor using Mono's csc plus the Unity install's
  UnityReferenceAssemblies/unity-4.8-api(+Facades) and
  Managed/UnityEngine/UnityEngine.*.dll references.
- Scene changes may be made by editing the .unity YAML directly (while the GUI is
  open); after editing, reopen the scene and do NOT press Ctrl+S.
- Firmware flashing, Unity Play, and on-device acceptance are performed by the
  user; visual changes are accepted via screenshots.
- Read docs/ before touching anything; append a build record to the corresponding
  plan document after every round of changes.

## Definition of done

- Both firmwares compile with pio; RollerFlasher builds with swift build; Unity
  scripts compile with csc.
- Flashed fishing firmware boots straight into the fishing HUD; in Unity Play the
  model follows the CoreS3 attitude, whip-casting works, a hooked fish pulls against
  your hand, the crank reels in, a line break releases torque, and landing a fish
  plays the leap animation and increments the counter.
