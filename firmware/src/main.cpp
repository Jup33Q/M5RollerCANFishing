/*
 * RollerCAN 力反馈旋钮 (Haptic Knob) — M5Stack CoreS3
 *
 * 原理：RollerCAN 切到 Current Mode（电流模式 = 扭矩模式），
 * CoreS3 以 ~200Hz 读取编码器位置，根据“虚拟物理模型”计算目标电流：
 *   - DETENTS  两级棘轮：5° 格点 / 10° 大档（吸附更宽），均为中小力度
 *   - SPRING   弹簧回中：扭矩与偏离角度成正比，松手自动回零
 *   - ENDSTOP  自由旋转 + 两端硬限位（软件墙）
 *   - FREE     自由旋转：关闭输出，纯编码器读取
 *
 * 接线：RollerCAN Grove 口 → CoreS3 Port.A（红 5V / 黑 GND / 黄 SDA=G2 / 白 SCL=G1）
 * 注意：Grove 5V 供电扭矩较小（0.021 N·m），要更强手感请用 XT30 6-16V 供电。
 *
 * 重要：RollerCAN 必须在机身配置菜单中把通信模式设为 I2C Mode！
 *   长按底部 Button A → 通电 → 进入配置模式 → 旋转电机选 Communication → I2C Mode。
 *
 * 串口遥测格式（115200）：  HAPTIC,<mode>,<pos_0.01deg>,<cur_cmd>,<roll>,<pitch>,<yaw>
 *
 * 串口命令（Unity → CoreS3，ASCII 行协议）：
 *   TENSION,<0..1000>      线张力千分比 → 电流 = ratio × EXT_MAX_CURRENT × EXT_DIR
 *   SETCUR,<-60000..60000> 原始电流指令（调试，直接进 EXT 电流模式）
 *   MODE,EXT / MODE,LOCAL  外部驱动 / 回本地 DETENTS
 *   FISH,<0..6>            中鱼鱼种 id → EXT 界面切到对应像素鱼精灵
 *   安全：EXT 模式 200ms 无新命令 → 电流归零回 LOCAL（看门狗）
 *
 * UDP 命令通道（WiFi，与串口同一套 ASCII 行协议，进同一个 handleCommand）：
 *   CoreS3 监听 UDP :9000，一个数据包 = 一行命令（不带换行也可）。
 *   Unity 侧从 OSC 包的来源 IP 自动发现 CoreS3，无需配置设备 IP。
 *
 * 连接确认：Unity 每 0.5s 发 PING 心跳（不武装看门狗），固件经 UDP 回 PONG；
 *   钓鱼 HUD 状态行据此显示 UNITY LINKED / WAITING UNITY...。
 *
 * 界面：M5Canvas 离屏渲染一次性 pushSprite（消频闪）。
 *   本地 4 模式：工程风遥测界面（标题/按钮静态一次，数据区 canvas 100ms 刷新）。
 *   EXT：整屏游戏 HUD（深蓝渐变、像素鱼精灵、全宽张力条、红区闪烁预警），
 *   与正常模式一眼可辨；EXT 下触摸按钮区隐藏，模式仅串口 / BtnA 进出。
 *
 * OSC 输出（WiFi UDP，供 Unity/UniOSC 同步）：
 *   /rollercan/imu    fff   roll pitch yaw（度，四元数换算 legacy）
 *   /rollercan/quat   ffff  姿态四元数 x y z w（调平后，Unity 主用通道）
 *   /rollercan/motor  ff    电机角度（度）、电流指令
 *   /rollercan/acc    ff    线性加速度幅值（m/s²，调平世界系去重力）：
 *                           瞬时值 + 15Hz 低通（甩绳/甩竿手势检测用）
 *   目标：TARGET_IP:TARGET_PORT，~100Hz
 */

#include <M5Unified.h>
#include <M5GFX.h>
#include <WiFi.h>
#include <WiFiUdp.h>
#include "unit_rolleri2c.hpp"
#include "fish_sprites.h"

// ---------------- WiFi / OSC 配置 ----------------
// RollerFlasher.app 的「同步 WiFi」会在构建前生成 src/wifi_config.h 覆盖下列默认值，
// 不要手改 wifi_config.h（会被重新生成）；要改默认值改这里。
#if __has_include("wifi_config.h")
#  include "wifi_config.h"
#endif
#ifndef CFG_WIFI_SSID
#  define CFG_WIFI_SSID   "YOUR_WIFI_SSID"
#  define CFG_WIFI_PASS   "YOUR_WIFI_PASS"
#  define CFG_TARGET_IP   "192.168.1.100"
#endif
static const char* WIFI_SSID   = CFG_WIFI_SSID;
static const char* WIFI_PASS   = CFG_WIFI_PASS;
static const char* TARGET_IP   = CFG_TARGET_IP;     // 运行 Unity 的电脑 IP
static const uint16_t TARGET_PORT = 8000;
static const uint16_t OSC_RATE_HZ = 100;
static const uint16_t CMD_LISTEN_PORT = 9000;   // Unity → CoreS3 命令 UDP 监听端口

// RollerFlasher.app 的「固件变体」会在构建前生成 src/variant_config.h
// （钓鱼模拟器变体定义 FISHING_DEFAULT_EXT），与 wifi_config.h 同机制，勿手改。
#if __has_include("variant_config.h")
#  include "variant_config.h"
#endif

// ---------------- 硬件配置 ----------------
static constexpr uint8_t  ROLLER_I2C_ADDR = 0x64;   // 默认 I2C 地址
static constexpr int      PIN_SDA = 2;              // CoreS3 Port.A SDA
static constexpr int      PIN_SCL = 1;              // CoreS3 Port.A SCL
static constexpr uint32_t I2C_HZ  = 400000;

// ---------------- 力反馈参数（按需调） ----------------
// 电流单位与范围见库说明：-120000 ~ 120000（设备内部缩放）
static constexpr int32_t  CURRENT_MAX   = 60000;    // 最大扭矩上限
// DETENTS 两级棘轮：5° 格点（中）/ 10° 大档（重）；大档吸附范围更宽、力度更大
static constexpr int32_t  TICK_MINOR = 500;         // 5° 格点（36000=一圈）
static constexpr int32_t  TICK_MAJOR = 1000;        // 10° 大档
static constexpr int32_t  CAP_MAJOR  = 250;         // 大档吸附范围 ±2.50°
static constexpr float    KP_MINOR   = 35.0f;       // 5° 格点力度（中小）
static constexpr float    KP_MAJOR   = 40.0f;       // 10° 档力度（中小；靠宽吸附保持存在感，防震荡）
static constexpr float    KP_SPRING  = 15.0f;
static constexpr float    KD_SPRING  = 4.0f;        // SPRING 专用速度阻尼（回中不振荡为准，嫌肉减小）
static constexpr float    KP_WALL    = 80.0f;       // 限位墙，硬一点
static constexpr float    KD_DAMPING = 1.5f;        // 速度阻尼：每 0.01RPM 的反向电流，抑制过冲/震荡
static constexpr float    VEL_SMOOTH = 0.3f;        // 速度估计 EMA 系数（0~1，越小越平滑但延迟越大）
static constexpr int32_t  WALL_MIN = -18000;        // 限位 -180°
static constexpr int32_t  WALL_MAX =  18000;        // 限位 +180°
static constexpr int32_t  WALL_SOFT = 1500;         // 软边界过渡区 15°

// ---------------- 外部驱动（钓鱼模拟器 EXT 模式） ----------------
static constexpr int32_t  EXT_MAX_CURRENT = 40000;  // 鱼拉手感上限（TENSION 满量程）
static constexpr int      EXT_DIR = 1;              // 拉力方向（±1，方向反了改这个）
static constexpr uint32_t EXT_WATCHDOG_MS = 200;    // EXT 命令看门狗：超时归零回 LOCAL

enum HapticMode { MODE_DETENTS = 0, MODE_SPRING, MODE_ENDSTOP, MODE_FREE, MODE_EXT, MODE_COUNT };
static const char* MODE_NAMES[MODE_COUNT] = {"DETENTS", "SPRING", "ENDSTOP", "FREE", "EXT"};
static constexpr int LOCAL_MODE_COUNT = 4;  // 触摸按钮区只放本地 4 模式，EXT 仅串口进入

UnitRollerI2C roller;
HapticMode mode    = MODE_DETENTS;
bool rollerOk      = false;
int32_t lastPos    = 0;
int32_t lastCurCmd = 0;
uint32_t lastLoopUs = 0;
float    velEst    = 0;     // 位置差分速度估计（0.01 RPM）
String scanResult = "scanning...";

// ---------------- EXT 外部驱动状态 ----------------
int32_t  extCurCmd = 0;             // 最近一条 TENSION/SETCUR 映射的电流指令
uint32_t lastExtCmdMs = 0;
bool     extWatchdogArmed = false;  // 收到过串口命令才武装看门狗（上电默认 EXT 不变看门狗误触发）
int      extFishId = -1;            // FISH,<id> 指定的鱼种（-1 = 未指定，问号占位）
float    extTension01 = 0;          // 最近张力 0..1（HUD 显示用）
uint32_t lastPingMs = 0;            // 最近 Unity PING 心跳（连接确认，不影响看门狗）

// Unity 在线 = 1s 内有心跳，或看门狗武装且 1s 内有实质命令
static inline bool unityLinked()
{
    uint32_t now = millis();
    return (now - lastPingMs < 1000) || (extWatchdogArmed && now - lastExtCmdMs < 1000);
}

// 鱼种名表，下标与 FISH,<id> / fish_sprites.h 一致
static const char* FISH_NAMES[FISH_SPRITE_COUNT] = {
    "CRUCIAN", "KOI", "BASS", "CATFISH", "SHRIMP", "CRAB", "SACABAMBASPIS"
};

// ---------------- 离屏 canvas（消频闪：合成后一次性 pushSprite） ----------------
M5Canvas canvasMain(&M5.Display);   // 本地模式数据区（y=40 起，避开标题与按钮区）
M5Canvas canvasFish(&M5.Display);   // 钓鱼 HUD 整屏

// ---------------- OSC / IMU 状态 ----------------
WiFiUDP udp;
bool wifiOk = false;
IPAddress targetIp;
// ---------------- 姿态估计（四元数 + Mahony 加速度校正） ----------------
// 旧实现是欧拉角直接积分陀螺仪 + 朴素互补滤波：roll/pitch 过 ±90° 时欧拉积分
// 发散翻转、互补滤波跨 ±180° 回绕拉反方向（Unity 模型翻转/反向旋转的根源）。
// 四元数积分无奇点；加速度校正走重力方向最短弧（Mahony 风格），无回绕问题。
float qW = 1, qX = 0, qY = 0, qZ = 0;          // 姿态四元数（机体系→世界系）
float lvW = 1, lvX = 0, lvY = 0, lvZ = 0;      // 调平零点（四元数）
float imuRoll = 0, imuPitch = 0, imuYaw = 0;   // 度（由四元数换算，仅供显示/旧协议）
float lastAccRoll = 0, lastAccPitch = 0;        // 加速度参考姿态（自动调平判定用）
bool leveled = false;                           // 已调平标志
uint32_t stillSinceMs = 0;                      // 静止计时（自动调平）
uint32_t lastImuUs = 0;
uint32_t lastOscMs = 0;

// 线性加速度幅值（m/s²，调平世界系去重力）：甩绳/甩竿手势检测用。
// raw = 瞬时幅值（抓甩动峰值）；lp = 15Hz 低通（平滑参考）。
float accMagRaw = 0, accMagLp = 0;

// 调平：以当前姿态四元数为零点
void doLevel()
{
    lvW = qW; lvX = qX; lvY = qY; lvZ = qZ;
    leveled = true;
    Serial.println("LEVEL,OK");
}

// 调平后的输出姿态四元数：lv⁻¹ ⊗ q
static void outQuat(float* w, float* x, float* y, float* z)
{
    *w = lvW*qW + lvX*qX + lvY*qY + lvZ*qZ;
    *x = lvW*qX - lvX*qW - lvY*qZ + lvZ*qY;
    *y = lvW*qY + lvX*qZ - lvY*qW - lvZ*qX;
    *z = lvW*qZ - lvX*qY + lvY*qX - lvZ*qW;
}

// 四元数 → legacy 欧拉角（度，aerospace：roll X / pitch Y / yaw Z）
static void quatToEuler(float w, float x, float y, float z,
                        float* roll, float* pitch, float* yaw)
{
    *roll  = atan2f(2.0f * (w * x + y * z), 1.0f - 2.0f * (x * x + y * y)) * 180.0f / PI;
    float sp = 2.0f * (w * y - z * x);
    *pitch = asinf(sp > 1.0f ? 1.0f : (sp < -1.0f ? -1.0f : sp)) * 180.0f / PI;
    *yaw   = atan2f(2.0f * (w * z + x * y), 1.0f - 2.0f * (y * y + z * z)) * 180.0f / PI;
}

// 调平后的输出欧拉角（遥测/OSC/界面统一用；从输出四元数换算，零点严格为 0）
static inline float outRoll()  { float w,x,y,z,r,p,ya; outQuat(&w,&x,&y,&z); quatToEuler(w,x,y,z,&r,&p,&ya); return r; }
static inline float outPitch() { float w,x,y,z,r,p,ya; outQuat(&w,&x,&y,&z); quatToEuler(w,x,y,z,&r,&p,&ya); return p; }
static inline float outYaw()   { float w,x,y,z,r,p,ya; outQuat(&w,&x,&y,&z); quatToEuler(w,x,y,z,&r,&p,&ya); return ya; }

// 极简 OSC 编码器：单条消息、仅 float 参数
// OSC: address\0(4对齐) + ",fff.."\0(4对齐) + 大端 float32
// 注意：字符串必须先加终止 \0 再补齐（",fff" 恰好 4 字节时也要补——否则接收方
// 找不到 tag 终止符会解析失败，曾导致 /rollercan/imu 包 100% 丢解析）
static void oscPad(String &s) { s += '\0'; while (s.length() % 4) s += '\0'; }

void sendOSC(const char* address, const float* vals, int n)
{
    if (!wifiOk) return;
    String addr(address); oscPad(addr);
    String tags = ",";
    for (int i = 0; i < n; i++) tags += 'f';
    oscPad(tags);
    udp.beginPacket(targetIp, TARGET_PORT);
    udp.write((const uint8_t*)addr.c_str(), addr.length());
    udp.write((const uint8_t*)tags.c_str(), tags.length());
    for (int i = 0; i < n; i++) {
        uint32_t u;
        memcpy(&u, &vals[i], 4);
        uint8_t be[4] = {(uint8_t)(u >> 24), (uint8_t)(u >> 16), (uint8_t)(u >> 8), (uint8_t)u};
        udp.write(be, 4);
    }
    udp.endPacket();
}

void wifiConnect()
{
    WiFi.mode(WIFI_STA);
    WiFi.begin(WIFI_SSID, WIFI_PASS);
    Serial.printf("WiFi connecting to %s", WIFI_SSID);
    uint32_t t0 = millis();
    while (WiFi.status() != WL_CONNECTED && millis() - t0 < 10000) {
        delay(250);
        Serial.print(".");
    }
    wifiOk = (WiFi.status() == WL_CONNECTED);
    if (wifiOk) {
        targetIp.fromString(TARGET_IP);
        udp.begin(CMD_LISTEN_PORT);     // 同一 socket 兼收命令（发送用 beginPacket 不受影响）
        Serial.printf("\nWiFi OK: %s -> OSC to %s:%d, cmd listen :%d\n",
                      WiFi.localIP().toString().c_str(), TARGET_IP, TARGET_PORT, CMD_LISTEN_PORT);
    } else {
        Serial.println("\nWiFi FAILED (OSC disabled)");
    }
}

// 姿态估计：陀螺仪四元数积分（无奇点）+ Mahony 加速度 tilt 校正（最短弧，无回绕）。
// yaw 无加速度计参考，仅陀螺仪积分（相对量，缓慢漂移，靠调平归零）。
void imuUpdate()
{
    uint32_t now = micros();
    float dt = (now - lastImuUs) / 1e6f;
    if (lastImuUs == 0 || dt <= 0 || dt > 0.1f) { lastImuUs = now; return; }
    lastImuUs = now;

    float gx, gy, gz, ax, ay, az;
    M5.Imu.getGyro(&gx, &gy, &gz);    // deg/s
    M5.Imu.getAccel(&ax, &ay, &az);   // g

    // Mahony P 校正：体轴下世界"上"方向估计 vs 加速度计实测方向，
    // 误差 = 实测 × 估计（最短弧旋转轴），按增益叠进角速度
    float wx = gx * DEG_TO_RAD, wy = gy * DEG_TO_RAD, wz = gz * DEG_TO_RAD;
    float an = sqrtf(ax * ax + ay * ay + az * az);
    if (an > 1e-4f) {
        float ux = ax / an, uy = ay / an, uz = az / an;      // 实测"上"（体轴）
        // 估计"上"：world up (0,0,1) 经 q 逆变换到体轴 = R(q) 第三行
        float vx = 2.0f * (qX * qZ - qW * qY);
        float vy = 2.0f * (qY * qZ + qW * qX);
        float vz = 1.0f - 2.0f * (qX * qX + qY * qY);
        constexpr float KC = 1.0f;    // 校正增益 /s（≈原互补滤波响应速度）
        wx += KC * (uy * vz - uz * vy);
        wy += KC * (uz * vx - ux * vz);
        wz += KC * (ux * vy - uy * vx);
    }

    // 四元数一阶积分：q += 0.5·dt·(q ⊗ ω_body)
    float nw = qW + 0.5f * dt * (-qX * wx - qY * wy - qZ * wz);
    float nx = qX + 0.5f * dt * ( qW * wx + qY * wz - qZ * wy);
    float ny = qY + 0.5f * dt * ( qW * wy + qZ * wx - qX * wz);
    float nz = qZ + 0.5f * dt * ( qW * wz + qX * wy - qY * wx);
    float n = sqrtf(nw * nw + nx * nx + ny * ny + nz * nz);
    if (n > 1e-6f) { qW = nw / n; qX = nx / n; qY = ny / n; qZ = nz / n; }

    // legacy 欧拉角（显示 / 旧协议用）
    quatToEuler(qW, qX, qY, qZ, &imuRoll, &imuPitch, &imuYaw);
    lastAccRoll  = atan2f(ay, az) * 180.0f / PI;
    lastAccPitch = atan2f(-ax, sqrtf(ay * ay + az * az)) * 180.0f / PI;

    // 自动调平：静止（角速度三轴和 < 6°/s）且接近水平放置（±30° 内）持续 3 秒，
    // 自动把当前姿态记为零点（只自动调一次；之后要重调用 LEVEL 命令——
    // 避免钓鱼中途静置片刻被误调平）
    float gyroMag = fabsf(gx) + fabsf(gy) + fabsf(gz);
    bool nearLevel = fabsf(lastAccRoll) < 30.0f && fabsf(lastAccPitch) < 30.0f;
    if (!leveled && nearLevel && gyroMag < 6.0f) {
        if (stillSinceMs == 0) stillSinceMs = millis();
        else if (millis() - stillSinceMs > 3000) doLevel();
    } else {
        stillSinceMs = 0;
    }

    // ---- 线性加速度幅值（甩绳/甩竿手势检测用）----
    // 体轴加速度(g)经调平后姿态 q_out 旋到世界系，去重力（世界 z 向上，静止≈+1g）
    float ow, ox, oy, oz;
    outQuat(&ow, &ox, &oy, &oz);
    float txx = 2.0f * (oy * az - oz * ay);
    float tyy = 2.0f * (oz * ax - ox * az);
    float tzz = 2.0f * (ox * ay - oy * ax);
    float wax = ax + ow * txx + (oy * tzz - oz * tyy);   // R(q_out)·a_body
    float way = ay + ow * tyy + (oz * txx - ox * tzz);
    float waz = az + ow * tzz + (ox * tyy - oy * txx);
    float lx = wax * 9.81f, ly = way * 9.81f, lz = (waz - 1.0f) * 9.81f;  // m/s²
    accMagRaw = sqrtf(lx * lx + ly * ly + lz * lz);
    accMagLp += (accMagRaw - accMagLp) * (1.0f - expf(-15.0f * dt));   // ~15Hz 低通
}

// ---------------- I2C 扫描（诊断用） ----------------
String i2cScan()
{
    String found;
    for (uint8_t addr = 1; addr < 127; addr++) {
        Wire.beginTransmission(addr);
        if (Wire.endTransmission() == 0) {
            char buf[8];
            snprintf(buf, sizeof(buf), "0x%02X ", addr);
            found += buf;
        }
    }
    if (found.isEmpty()) found = "NONE";
    Serial.printf("I2C scan: %s\n", found.c_str());
    return found;
}

static inline int32_t clampCur(float c)
{
    if (c >  CURRENT_MAX) return  CURRENT_MAX;
    if (c < -CURRENT_MAX) return -CURRENT_MAX;
    return (int32_t)c;
}

// 最近倍数（四舍五入，正确处理负数）
static inline int32_t nearestMultiple(int32_t pos, int32_t spacing)
{
    int32_t r = pos % spacing;
    int32_t base = pos - r;
    if (r >  spacing / 2) base += spacing;
    if (r < -spacing / 2) base -= spacing;
    return base;
}

int32_t computeTorque(int32_t pos)
{
    switch (mode) {
        case MODE_DETENTS: {
            // 两级棘轮：优先吸附 10° 大档（重），否则吸附 5° 格点（中）
            int32_t major = nearestMultiple(pos, TICK_MAJOR);
            if (abs(pos - major) <= CAP_MAJOR)
                return clampCur(KP_MAJOR * (float)(major - pos));
            int32_t minor = nearestMultiple(pos, TICK_MINOR);
            return clampCur(KP_MINOR * (float)(minor - pos));
        }
        case MODE_SPRING:
            return clampCur(KP_SPRING * (float)(0 - pos));
        case MODE_ENDSTOP:
            if (pos > WALL_MAX - WALL_SOFT)
                return clampCur(KP_WALL * (float)((WALL_MAX - WALL_SOFT) - pos));
            if (pos < WALL_MIN + WALL_SOFT)
                return clampCur(KP_WALL * (float)((WALL_MIN + WALL_SOFT) - pos));
            return 0;
        case MODE_FREE:
        default:
            return 0;
    }
}

void applyMode()
{
    roller.setOutput(0);
    delay(20);
    if (mode == MODE_FREE) {
        roller.setMode(ROLLER_MODE_ENCODER);
        return;
    }
    roller.setMode(ROLLER_MODE_CURRENT);
    roller.setCurrent(0);
    roller.setOutput(1);
}

// ---------------- 界面（静态一次 + 局部刷新，避免整屏重绘频闪） ----------------
void drawStaticUI()
{
    auto &d = M5.Display;
    d.fillScreen(BLACK);
    d.setFont(&fonts::FreeSans12pt7b);
    d.setTextColor(WHITE, BLACK);
    d.setCursor(10, 8);
    d.printf("RollerCAN Haptic");
    d.setFont(&fonts::Font2);
    d.setCursor(10, 34);
    d.setTextColor(wifiOk ? GREEN : RED, BLACK);
    d.printf("%s", wifiOk ? WiFi.localIP().toString().c_str() : "WiFi offline (OSC off)");

    int w = d.width() / 4;
    for (int i = 0; i < LOCAL_MODE_COUNT; i++) {
        uint32_t bg = (i == (int)mode) ? 0x001F : 0x4208;
        d.fillRect(i * w + 2, d.height() - 46, w - 4, 42, bg);
        d.setFont(&fonts::Font2);
        d.setTextColor(WHITE, bg);
        d.setCursor(i * w + 8, d.height() - 30);
        d.printf("%s", MODE_NAMES[i]);
    }
}

// 数据区 canvas 尺寸：y=40 起到按钮区上方（按钮区在 height-46）
static constexpr int CANVAS_MAIN_H = 155;
void drawFishingUI();   // 前置声明（定义在 updateDynamicUI 之后）

void updateDynamicUI()
{
    if (mode == MODE_EXT) { drawFishingUI(); return; }
    auto &c = canvasMain;
    c.fillSprite(BLACK);
    // 模式名
    c.setFont(&fonts::FreeSans24pt7b);
    c.setCursor(10, 5);
    if (!rollerOk) {
        c.setTextColor(RED);
        c.printf("NO MOTOR");
    } else {
        c.setTextColor(CYAN);
        c.printf("%s", MODE_NAMES[mode]);
    }
    // 角度
    c.setFont(&fonts::FreeSans18pt7b);
    c.setCursor(10, 60);
    c.setTextColor(GREEN);
    c.printf("%7.2f deg", lastPos / 100.0f);
    // 状态行
    c.setFont(&fonts::FreeSans9pt7b);
    c.setCursor(10, 110);
    c.setTextColor(LIGHTGREY);
    if (!rollerOk) {
        c.printf("I2C found: %s", scanResult.c_str());
        c.setCursor(10, 132);
        c.printf("need 0x64 | RollerCAN set I2C Mode!");
    } else {
        c.printf("cur cmd: %6ld", (long)lastCurCmd);
        // IMU 三轴显示（轴向测试：转动 CoreS3 看对应轴变化；调平后 [LVL] 标记）
        c.setFont(&fonts::Font2);
        c.setCursor(10, 132);
        c.setTextColor(leveled ? GREEN : 0x7BEF);  // 已调平绿 / 未调平灰
        c.printf("R%+.1f P%+.1f Y%+.1f%s", outRoll(), outPitch(), outYaw(),
                 leveled ? " [LVL]" : "");
    }
    c.pushSprite(0, 40);
}

// 钓鱼模拟器整屏游戏 HUD（EXT 模式）：深蓝水底渐变 + 像素鱼精灵 + 全宽张力条
void drawFishingUI()
{
    auto &c = canvasFish;
    const int W = c.width(), H = c.height();
    c.fillGradientRect(0, 0, W, H, (uint32_t)0x0841, (uint32_t)0x0001, m5gfx::VLINEAR);  // 深靛 → 近黑蓝

    // 顶部大标题
    c.setFont(&fonts::FreeSans18pt7b);
    c.setTextColor(CYAN);
    c.setCursor(12, 10);
    c.printf("FISHING!");

    // 像素鱼精灵（居中偏左）；未收到 FISH 命令时问号占位
    if (extFishId >= 0) {
        c.pushImage(24, 62, FISH_SPRITE_SIZE, FISH_SPRITE_SIZE,
                    FISH_SPRITES[extFishId], (uint16_t)FISH_TRANSPARENT_KEY);
        c.setFont(&fonts::Font2);
        c.setTextColor(WHITE);
        c.setCursor(24, 166);
        c.printf("%s", FISH_NAMES[extFishId]);
    } else {
        c.setFont(&fonts::FreeSans24pt7b);
        c.setTextColor(0x4208);
        c.setCursor(56, 92);
        c.printf("?");
    }

    // 右侧大号张力百分比（绿 → 黄 → 红）
    uint16_t tc = extTension01 < 0.5f ? GREEN : (extTension01 < 0.8f ? YELLOW : RED);
    c.setFont(&fonts::FreeSans24pt7b);
    c.setTextColor(tc);
    c.setCursor(150, 80);
    c.printf("%3.0f%%", extTension01 * 100.0f);

    // 右侧状态行：Unity 连接确认（PING/PONG 心跳）
    bool linked = unityLinked();
    c.setFont(&fonts::Font2);
    c.setCursor(150, 152);
    if (!linked) {
        c.setTextColor(0xFD20);  // 橙
        c.printf("WAITING UNITY...");
    } else {
        c.setTextColor(GREEN);
        c.printf("UNITY LINKED");
        c.setCursor(150, 170);
        c.setTextColor(LIGHTGREY);
        c.printf("cur %6ld", (long)lastCurCmd);
    }

    // 底部全宽张力条（绿→红渐变条，未填充段盖暗色）
    const int barX = 8, barY = H - 34, barW = W - 16, barH = 24;
    c.fillGradientRect(barX, barY, barW, barH, (uint32_t)0x07E0, (uint32_t)0xF800, m5gfx::HLINEAR);
    int fillW = (int)(barW * extTension01);
    if (fillW < barW) c.fillRect(barX + fillW, barY, barW - fillW, barH, 0x1082);
    c.drawRect(barX - 1, barY - 1, barW + 2, barH + 2, WHITE);

    // 断线预警：张力 ≥95% 整屏红框闪烁
    if (extTension01 >= 0.95f && ((millis() / 250) & 1)) {
        for (int i = 0; i < 3; i++) c.drawRect(i, i, W - 2 * i, H - 2 * i, RED);
    }

    c.pushSprite(0, 0);
}

void refreshButtons()
{
    auto &d = M5.Display;
    int w = d.width() / 4;
    for (int i = 0; i < LOCAL_MODE_COUNT; i++) {
        uint32_t bg = (i == (int)mode) ? 0x001F : 0x4208;
        d.fillRect(i * w + 2, d.height() - 46, w - 4, 42, bg);
        d.setFont(&fonts::Font2);
        d.setTextColor(WHITE, bg);
        d.setCursor(i * w + 8, d.height() - 30);
        d.printf("%s", MODE_NAMES[i]);
    }
}

// ---------------- 串口 RX（Unity → CoreS3 命令） ----------------
String rxLine;

void enterLocal()
{
    if (mode == MODE_EXT) {
        mode = MODE_DETENTS;
        extCurCmd = 0;
        extTension01 = 0;
        extFishId = -1;
        if (rollerOk) applyMode();
        drawStaticUI();      // 钓鱼 HUD 覆盖了整屏，回本地需重画静态区
        updateDynamicUI();
    }
}

void enterExt()
{
    if (mode != MODE_EXT) {
        mode = MODE_EXT;
        if (rollerOk) applyMode();
        updateDynamicUI();   // 分流到 drawFishingUI 整屏覆盖（含按钮区）
    }
}

void handleCommand(const String& cmd)
{
    if (cmd == "PING") {            // Unity 心跳：只标记在线，不武装看门狗、不切模式
        lastPingMs = millis();
        return;
    }
    if (cmd == "LEVEL") {           // 手动调平（同 PING：不武装看门狗、不切模式）
        doLevel();
        return;
    }
    lastExtCmdMs = millis();
    extWatchdogArmed = true;
    if (cmd.startsWith("TENSION,")) {
        long t = cmd.substring(8).toInt();
        if (t < 0) t = 0;
        if (t > 1000) t = 1000;
        extTension01 = t / 1000.0f;
        extCurCmd = clampCur(EXT_DIR * extTension01 * EXT_MAX_CURRENT);
        enterExt();
    } else if (cmd.startsWith("SETCUR,")) {
        extCurCmd = clampCur((float)cmd.substring(7).toInt());
        extTension01 = fabsf((float)extCurCmd) / EXT_MAX_CURRENT;
        if (extTension01 > 1.0f) extTension01 = 1.0f;
        enterExt();
    } else if (cmd.startsWith("FISH,")) {
        int id = cmd.substring(5).toInt();
        if (id >= 0 && id < FISH_SPRITE_COUNT) extFishId = id;
    } else if (cmd == "MODE,EXT") {
        enterExt();
    } else if (cmd == "MODE,LOCAL") {
        enterLocal();
    }
}

void serialRxPoll()
{
    while (Serial.available() > 0) {
        char ch = (char)Serial.read();
        if (ch == '\n' || ch == '\r') {
            if (rxLine.length() > 0) {
                handleCommand(rxLine);
                rxLine = "";
            }
        } else if (rxLine.length() < 64) {
            rxLine += ch;
        } else {
            rxLine = "";    // 超长行丢弃，等待下一行重同步
        }
    }
}

// UDP 命令通道（WiFi）：一个数据包 = 一行 ASCII 命令，复用 handleCommand
void udpRxPoll()
{
    if (!wifiOk) return;
    int n = udp.parsePacket();
    if (n <= 0) return;
    char buf[65];
    int len = udp.read(buf, sizeof(buf) - 1);
    if (len <= 0) return;
    buf[len] = '\0';
    String cmd(buf);
    cmd.trim();
    if (cmd.isEmpty()) return;
    if (cmd == "PING") {
        // Unity 连接确认心跳：标记在线并回 PONG（回 Unity 发包的源地址端口 = 其 OSC 监听口）
        lastPingMs = millis();
        udp.beginPacket(udp.remoteIP(), udp.remotePort());
        udp.write((const uint8_t*)"PONG\n", 5);
        udp.endPacket();
        return;
    }
    handleCommand(cmd);
}

void setup()
{
    M5.begin();
    Serial.begin(115200);
    M5.Display.setRotation(1);
    M5.Imu.begin();               // CoreS3 内置 BMI270

    // 离屏 canvas（PSRAM）：数据区 + 钓鱼整屏 HUD
    canvasMain.setColorDepth(16);
    canvasFish.setColorDepth(16);
    if (!canvasMain.createSprite(M5.Display.width(), CANVAS_MAIN_H))
        Serial.println("canvasMain alloc FAILED");
    if (!canvasFish.createSprite(M5.Display.width(), M5.Display.height()))
        Serial.println("canvasFish alloc FAILED");

    wifiConnect();

    // Roller 库内部会调用 Wire.begin(SDA=2, SCL=1)
    rollerOk = roller.begin(&Wire, ROLLER_I2C_ADDR, PIN_SDA, PIN_SCL, I2C_HZ);
    scanResult = i2cScan();
    Serial.printf("RollerCAN at 0x%02X: %s\n", ROLLER_I2C_ADDR, rollerOk ? "OK" : "NOT FOUND");

    if (rollerOk) {
        roller.setRGBMode(ROLLER_RGB_MODE_USER_DEFINED);
        roller.setRGB(0x07E0);          // 绿色
        roller.setRGBBrightness(60);
        roller.setStallProtection(0);   // 力反馈场景关闭堵转锁定
        roller.setDialCounter(0);
        applyMode();
    }
#ifdef FISHING_DEFAULT_EXT
    // 钓鱼模拟器变体（variant_config.h）：上电直接进 EXT 等串口命令，
    // 看门狗在收到首条命令后才武装，不会因为没接 Unity 而掉回 LOCAL
    mode = MODE_EXT;
    if (rollerOk) applyMode();
#endif
    if (mode != MODE_EXT) drawStaticUI();   // 钓鱼变体上电直接进 HUD，跳过工程风静态区
    updateDynamicUI();
}

void loop()
{
    M5.update();
    imuUpdate();
    serialRxPoll();
    udpRxPoll();

    // EXT 看门狗：200ms 无新命令 → 电流归零并回 LOCAL DETENTS
    // （防 Unity 卡死/拔线时电机锁扭矩）
    if (mode == MODE_EXT && extWatchdogArmed && millis() - lastExtCmdMs > EXT_WATCHDOG_MS) {
        extWatchdogArmed = false;
        enterLocal();
        Serial.println("WATCHDOG,EXT timeout -> LOCAL");
    }

    // OSC 发送：IMU 姿态 + 电机角度
    if (wifiOk && millis() - lastOscMs >= 1000 / OSC_RATE_HZ) {
        lastOscMs = millis();
        float imuVals[3] = {outRoll(), outPitch(), outYaw()};
        sendOSC("/rollercan/imu", imuVals, 3);
        // 四元数姿态（x,y,z,w，调平后）：Unity 侧免欧拉角，根治奇点/回绕
        float qw2, qx2, qy2, qz2;
        outQuat(&qw2, &qx2, &qy2, &qz2);
        float quatVals[4] = {qx2, qy2, qz2, qw2};
        sendOSC("/rollercan/quat", quatVals, 4);
        float motorVals[2] = {lastPos / 100.0f, (float)lastCurCmd};
        sendOSC("/rollercan/motor", motorVals, 2);
        // 线性加速度幅值（m/s²）：瞬时值 + 15Hz 低通（甩绳/甩竿手势检测用）
        float accVals[2] = {accMagRaw, accMagLp};
        sendOSC("/rollercan/acc", accVals, 2);
    }

    // 电机掉线时每 2 秒重试一次
    static uint32_t lastRetry = 0;
    if (!rollerOk && millis() - lastRetry > 2000) {
        lastRetry = millis();
        rollerOk = roller.begin(&Wire, ROLLER_I2C_ADDR, PIN_SDA, PIN_SCL, I2C_HZ);
        scanResult = i2cScan();
        if (rollerOk) {
            roller.setRGBMode(ROLLER_RGB_MODE_USER_DEFINED);
            roller.setRGB(0x07E0);
            roller.setRGBBrightness(60);
            roller.setStallProtection(0);
            applyMode();
        }
        updateDynamicUI();
    }

    // 物理按键 A 循环切换模式（EXT 切出时需重画被钓鱼 HUD 覆盖的静态区）
    if (M5.BtnA.wasClicked()) {
        HapticMode prev = mode;
        mode = (HapticMode)(((int)mode + 1) % MODE_COUNT);
        if (rollerOk) applyMode();
        if (prev == MODE_EXT) {
            extCurCmd = 0;
            extTension01 = 0;
            extFishId = -1;
            drawStaticUI();
        } else {
            refreshButtons();
        }
        updateDynamicUI();
    }

    // 触摸屏：底部按钮区直接选模式（仅本地 4 模式，EXT 下按钮区隐藏，仅串口/BtnA 进出）
    auto t = M5.Touch.getDetail();
    if (mode != MODE_EXT && t.wasClicked() && t.y > M5.Display.height() - 50) {
        int idx = t.x / (M5.Display.width() / 4);
        if (idx >= 0 && idx < LOCAL_MODE_COUNT && idx != (int)mode) {
            mode = (HapticMode)idx;
            if (rollerOk) applyMode();
            refreshButtons();
            updateDynamicUI();
        }
    }

    // ~200Hz 力反馈环
    uint32_t now = micros();
    if (rollerOk && now - lastLoopUs >= 5000) {
        uint32_t dtUs = now - lastLoopUs;
        lastLoopUs = now;
        int32_t pos = roller.getPosReadback();
        // 位置差分估计速度（0.01 RPM，EMA 平滑）：
        // speed readback 寄存器经电机内部滤波延迟大，做阻尼会助振，弃用
        if (dtUs > 0) {
            float v = (float)(pos - lastPos) * (1e6f / (float)dtUs);  // 0.01°/s
            velEst += VEL_SMOOTH * (v * (60.0f / 36000.0f) - velEst); // → 0.01 RPM
        }
        lastPos = pos;
        if (mode == MODE_EXT) {
            // 外部驱动：串口指令电流 + 基础阻尼（收线顺滑感）
            lastCurCmd = extCurCmd - (int32_t)(KD_DAMPING * velEst);
            if (lastCurCmd >  CURRENT_MAX) lastCurCmd =  CURRENT_MAX;
            if (lastCurCmd < -CURRENT_MAX) lastCurCmd = -CURRENT_MAX;
            roller.setCurrent(lastCurCmd);
        } else if (mode != MODE_FREE) {
            float kd = (mode == MODE_SPRING) ? KD_SPRING : KD_DAMPING;
            lastCurCmd = computeTorque(pos) - (int32_t)(kd * velEst);
            if (lastCurCmd >  CURRENT_MAX) lastCurCmd =  CURRENT_MAX;
            if (lastCurCmd < -CURRENT_MAX) lastCurCmd = -CURRENT_MAX;
            roller.setCurrent(lastCurCmd);
        } else {
            lastCurCmd = 0;
        }
        Serial.printf("HAPTIC,%s,%ld,%ld,%.2f,%.2f,%.2f\n",
                      MODE_NAMES[mode], (long)lastPos, (long)lastCurCmd,
                      outRoll(), outPitch(), outYaw());
    }

    // 界面数据局部刷新（低频）
    static uint32_t lastUi = 0;
    if (millis() - lastUi > 100) {
        lastUi = millis();
        updateDynamicUI();
    }
}
