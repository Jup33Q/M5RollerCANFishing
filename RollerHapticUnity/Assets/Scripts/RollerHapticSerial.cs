/*
 * RollerHapticSerial.cs — USB 串口双向链路（替代 WiFi/OSC 的 RollerHapticSync）
 *
 * CoreS3 → Unity（~200Hz ASCII 遥测，115200 8N1）：
 *   HAPTIC,<mode>,<pos_0.01deg>,<cur_cmd>,<roll>,<pitch>,<yaw>
 *   （兼容旧 4 字段格式，无 IMU 字段时姿态不更新）
 *
 * Unity → CoreS3（ASCII 行协议）：
 *   TENSION,<0..1000>      线张力千分比 → 电机反向扭矩
 *   SETCUR,<-60000..60000> 原始电流指令（调试）
 *   MODE,EXT / MODE,LOCAL  外部驱动 / 回本地力反馈
 *   固件侧有 200ms 看门狗：停止发送即自动释放扭矩。
 *
 * 用法：场景空物体挂本脚本，Inspector 拖入 coreS3Root / motorRoot。
 *   与 RollerHapticSync（OSC 版）同一时刻只启用一个（组件取消勾选即回退）。
 *   端口独占：RollerFlasher 监视/烧录前请先 Stop Play。
 */
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Ports;
using System.Threading;
using UnityEngine;

public class RollerHapticSerial : MonoBehaviour
{
    [Header("场景引用")]
    public Transform coreS3Root;   // CoreS3 整机（IMU 姿态节点）
    public Transform motorRoot;    // RollerCAN 旋转部分（转子/摇柄）

    [Header("串口")]
    [Tooltip("留空 = 自动发现 /dev/cu.usbmodem*")]
    public string portName = "";
    public int baudRate = 115200;

    [Header("轴向校正")]
    public bool invertRoll = false;
    public bool invertPitch = false;
    public bool invertYaw = false;
    public bool invertMotorAngle = false;
    public float rollOffset = 0f;
    public float pitchOffset = 0f;
    public float yawOffset = 0f;
    public float motorAngleOffset = 0f;
    [Tooltip("平滑系数，0=不平滑")]
    [Range(0f, 0.9f)] public float smoothing = 0.15f;

    [Header("调试（只读）")]
    public string lastMode = "";
    public float dbgRoll, dbgPitch, dbgYaw;
    public float dbgMotorAngle, dbgMotorCur;
    public int linesPerSec;
    public int parseErrors;
    public bool isOpen;

    // ---- 对外只读状态（FishingSim 用） ----
    public float MotorAngle => smMotor;      // 校正后电机角度（度）
    public float MotorAngleRaw => dbgMotorAngle;
    public float Roll => smRoll;
    public float Pitch => smPitch;
    public float Yaw => smYaw;
    public float LastCurCmd => dbgMotorCur;

    readonly ConcurrentQueue<string> queue = new ConcurrentQueue<string>();
    SerialPort port;
    Thread rxThread;
    volatile bool running;
    readonly object sendLock = new object();
    int lineCounter;
    float nextRetryTime;
    bool warnPrinted;
    string activePort = "";

    void Start()
    {
        InvokeRepeating(nameof(UpdateLps), 1f, 1f);
        TryOpen();
    }

    void UpdateLps() { linesPerSec = lineCounter; lineCounter = 0; }

    /// <summary>扫描可用串口：直接扫 /dev 文件系统（Mono 的 SerialPort.GetPortNames
    /// 在 macOS 上不可靠，经常漏掉 cu.usbmodem*，只作 fallback 补充）。</summary>
    public static List<string> ScanPorts()
    {
        var list = new List<string>();
        try
        {
            foreach (var pat in new[] { "cu.usbmodem*", "cu.usbserial*", "cu.wchusbserial*" })
                foreach (var p in System.IO.Directory.GetFiles("/dev", pat))
                    if (!list.Contains(p)) list.Add(p);
        }
        catch (Exception) { }
        try
        {
            foreach (var p in SerialPort.GetPortNames())
                if (p.StartsWith("/dev/cu.", StringComparison.Ordinal) && !list.Contains(p))
                    list.Add(p);
        }
        catch (Exception) { }
        return list;
    }

    string FindPort()
    {
        if (!string.IsNullOrEmpty(portName)) return portName;
        var list = ScanPorts();
        return list.Count > 0 ? list[0] : null;
    }

    /// <summary>关闭当前连接并立即重新扫描/打开（Inspector 按钮用，Play 中调用）。</summary>
    public void Rescan()
    {
        Cleanup();
        warnPrinted = false;
        TryOpen();
    }

    void TryOpen()
    {
        string p = FindPort();
        if (p == null)
        {
            if (!warnPrinted) { Debug.LogWarning("[RollerHapticSerial] 未找到 /dev/cu.usbmodem*，每秒重试"); warnPrinted = true; }
            nextRetryTime = Time.time + 1f;
            return;
        }
        try
        {
            port = new SerialPort(p, baudRate, Parity.None, 8, StopBits.One);
            port.ReadTimeout = 500;     // 让接收线程能周期性检查 running 标志
            port.NewLine = "\n";
            port.Open();
            // 拉高 DTR/RTS 声明占线：ESP32-S3 USB CDC 默认把 DTR/RTS 特定
            // 跳变序列当 esptool 复位信号（进下载模式），拉高后不再命中序列
            try { port.DtrEnable = true; port.RtsEnable = true; } catch (Exception) { }
            activePort = p;
            isOpen = true;
            running = true;
            rxThread = new Thread(RxLoop) { IsBackground = true };
            rxThread.Start();
            warnPrinted = false;
            Debug.Log($"[RollerHapticSerial] opened {p} @ {baudRate}");
        }
        catch (Exception e)
        {
            if (!warnPrinted) { Debug.LogWarning($"[RollerHapticSerial] 打开 {p} 失败：{e.Message}，每秒重试"); warnPrinted = true; }
            Cleanup();
            nextRetryTime = Time.time + 1f;
        }
    }

    void RxLoop()
    {
        while (running)
        {
            try
            {
                string line = port.ReadLine();
                if (!string.IsNullOrEmpty(line)) { queue.Enqueue(line.Trim()); lineCounter++; }
            }
            catch (TimeoutException) { }
            catch (Exception)
            {
                if (running) queue.Enqueue("__DISCONNECTED__");
                break;
            }
        }
    }

    void Cleanup()
    {
        running = false;
        isOpen = false;
        // 先等接收线程退出（ReadTimeout=500ms 保证它很快返回），再 Close——
        // 否则 Mono 的 SerialPort.Close 可能卡住/吞异常，fd 泄漏导致 Stop Play 后端口仍被占用
        try { if (rxThread != null && rxThread.IsAlive) rxThread.Join(800); } catch (Exception) { }
        rxThread = null;
        try { port?.Close(); } catch (Exception) { }
        port = null;
        activePort = "";
    }

    float smRoll, smPitch, smYaw, smMotor;
    float nextPingAt;

    void Update()
    {
        while (queue.TryDequeue(out var line))
        {
            if (line == "__DISCONNECTED__") { Cleanup(); nextRetryTime = Time.time + 1f; continue; }
            ParseLine(line);
        }

        if (!isOpen && Time.time >= nextRetryTime) TryOpen();

        // 连接确认心跳：在线时每 0.5s 发 PING（固件标记 Unity 在线，不武装看门狗）
        if (isOpen && Time.time >= nextPingAt)
        {
            nextPingAt = Time.time + 0.5f;
            SendRaw("PING");
        }

        // 平滑 + 校正（与 RollerHapticSync 同一管线）
        float k = 1f - smoothing;
        smRoll  += (dbgRoll - smRoll) * k;
        smPitch += (dbgPitch - smPitch) * k;
        smYaw   += (dbgYaw - smYaw) * k;
        smMotor += (dbgMotorAngle - smMotor) * k;

        if (coreS3Root != null)
        {
            float r = (invertRoll ? -smRoll : smRoll) + rollOffset;
            float p = (invertPitch ? -smPitch : smPitch) + pitchOffset;
            float y = (invertYaw ? -smYaw : smYaw) + yawOffset;
            coreS3Root.localRotation = Quaternion.Euler(r, y, p);
        }
        if (motorRoot != null)
        {
            float ma = (invertMotorAngle ? -smMotor : smMotor) + motorAngleOffset;
            motorRoot.localRotation = Quaternion.Euler(0f, ma, 0f);
        }
    }

    void ParseLine(string line)
    {
        // HAPTIC,<mode>,<pos_0.01deg>,<cur_cmd>[,<roll>,<pitch>,<yaw>]
        var f = line.Split(',');
        if (f.Length < 4 || f[0] != "HAPTIC") return;
        try
        {
            lastMode = f[1];
            dbgMotorAngle = int.Parse(f[2], CultureInfo.InvariantCulture) / 100f;
            dbgMotorCur = int.Parse(f[3], CultureInfo.InvariantCulture);
            if (f.Length >= 7)
            {
                dbgRoll  = float.Parse(f[4], CultureInfo.InvariantCulture);
                dbgPitch = float.Parse(f[5], CultureInfo.InvariantCulture);
                dbgYaw   = float.Parse(f[6], CultureInfo.InvariantCulture);
            }
        }
        catch (Exception) { parseErrors++; }
    }

    // ---------------- 发送（主线程调用，线程安全） ----------------

    public void SendRaw(string line)
    {
        lock (sendLock)
        {
            if (!isOpen || port == null) return;
            try { port.WriteLine(line); }
            catch (Exception) { Cleanup(); nextRetryTime = Time.time + 1f; }
        }
    }

    /// <summary>线张力 0..1 → TENSION,0..1000</summary>
    public void SendTension(float t01)
    {
        int v = Mathf.Clamp(Mathf.RoundToInt(t01 * 1000f), 0, 1000);
        SendRaw($"TENSION,{v}");
    }

    public void SendModeExt()   => SendRaw("MODE,EXT");
    public void SendModeLocal() => SendRaw("MODE,LOCAL");

    void OnDestroy()
    {
        Cleanup();
        CancelInvoke();
    }
}
