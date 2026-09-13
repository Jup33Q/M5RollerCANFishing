/*
 * RollerHapticSync.cs — 接收 CoreS3 发来的 OSC (UDP) 并同步场景
 *
 * 用法：
 *   1. 把本文件放进 Unity 工程的 Assets/Scripts/
 *   2. 场景里建一个空物体挂本脚本
 *   3. Inspector 拖入：Core S3 Root = 白色方块（CoreS3），Motor Root = 红色 RollerCAN 旋转部分
 *   4. 无需 UniOSC（自包含解析）。若坚持用 UniOSC：监听地址
 *      /rollercan/imu (fff) 和 /rollercan/motor (ff)，把值转给本脚本的 SetImu/SetMotor。
 *
 * 固件消息：
 *   /rollercan/imu    fff  roll pitch yaw（度）
 *   /rollercan/motor  ff   电机角度（度）、电流指令
 *
 * 发送（Unity → CoreS3，钓鱼模拟器命令通道）：
 *   UDP 纯文本行（与串口同一套 ASCII 协议），目标 = 最近 OSC 包来源 IP : cmdPort。
 *   CoreS3 的 IP 无需配置，从它发来的 OSC 包自动发现。
 *   SendTension / SendModeExt / SendModeLocal / SendRaw 与 RollerHapticSerial 同接口。
 *
 * 轴向约定（截图布局：CoreS3 屏幕朝上 = 绕竖直轴 yaw）：
 *   CoreS3:  yaw → Y 轴，roll → X 轴，pitch → Z 轴（如方向反了勾选 Invert 对应项）
 *   电机:    angle → motorAxis（motorRoot 本地空间方向；新导出的 FBX 柱轴为本地 +Z，
 *            旧版为 +Y——以 Inspector 里转子本地轴向为准，可在面板直接改）
 */
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class RollerHapticSync : MonoBehaviour
{
    [Header("场景引用")]
    public Transform coreS3Root;   // 白色：CoreS3（屏幕向上）
    public Transform motorRoot;    // 红色：RollerCAN

    [Header("网络")]
    public int listenPort = 8000;
    [Tooltip("CoreS3 命令监听端口（固件 CMD_LISTEN_PORT）")]
    public int cmdPort = 9000;

    [Header("链路状态（只读）")]
    [Tooltip("最近 1 秒内收到 OSC 包即为在线")]
    public bool isOpen;
    [Tooltip("双向确认：收到固件 PONG 回包")]
    public bool linkConfirmed;
    [Tooltip("自动发现的 CoreS3 IP:端口")]
    public string cmdTargetStr = "";

    /// <summary>电机角度（度，未平滑原始值，与 RollerHapticSerial.MotorAngleRaw 同义）</summary>
    public float MotorAngleRaw => dbgMotorAngle;

    [Header("轴向校正")]
    public bool invertRoll = false;
    public bool invertPitch = false;
    public bool invertYaw = false;
    public bool invertMotorAngle = false;
    [Tooltip("电机轴在 motorRoot 本地空间的方向（当前 FBX 为 +Z；若导入约定变化改这里）")]
    public Vector3 motorAxis = Vector3.forward;
    public float rollOffset = 0f;
    public float pitchOffset = 0f;
    public float yawOffset = 0f;
    public float motorAngleOffset = 0f;
    [Tooltip("平滑系数，0=不平滑")]
    [Range(0f, 0.9f)] public float smoothing = 0.15f;
    [Tooltip("IMU 单帧姿态跳变超过该角度判定为奇点毛刺并丢弃（>6000°/s 物理不可能）")]
    [Range(10f, 120f)] public float glitchRejectDeg = 60f;
    [Tooltip("连续毛刺包超过该数量判定为真实大动作，强制重新对齐")]
    [Range(2, 50)] public int glitchResyncPackets = 10;

    [Header("调试（只读）")]
    public float dbgRoll, dbgPitch, dbgYaw;
    public float dbgMotorAngle, dbgMotorCur;
    public int packetsPerSec;
    [Tooltip("线性加速度幅值（m/s²，去重力）：raw 瞬时值（甩竿峰值）/ lp 15Hz 低通")]
    public float dbgAccRaw;
    public float dbgAccMag;

    [Header("IMU 录制（调试）")]
    [Tooltip("Play 中勾选=开始录制，取消=停止并写 CSV 到工程根目录 imu_record_*.csv")]
    public bool recordImu = false;
    readonly System.Text.StringBuilder recBuf = new System.Text.StringBuilder();
    float recStartTime;
    bool recActive;

    struct OscMsg { public string addr; public float[] vals; }
    readonly ConcurrentQueue<OscMsg> queue = new ConcurrentQueue<OscMsg>();
    UdpClient udp;
    Thread rxThread;
    volatile bool running;
    int pktCounter;

    // 命令回发目标：最近 OSC 包的来源 IP（= CoreS3，自动发现免配置）
    volatile IPAddress lastSenderIp;
    long lastPacketTicks;   // DateTime.UtcNow.Ticks（线程安全起见不用 Time.time）
    long lastPongTicks;     // 最近收到固件 PONG 的时间
    float nextPingAt;

    void Start()
    {
        // FBX 重导入后 prefab 内部 fileID 可能变化导致 motorRoot 引用失效，
        // 引用缺失时按节点名在 coreS3Root 下递归找回 "RollerCAN_Rotor"。
        if (coreS3Root == null)
        {
            var go = GameObject.Find("IMU_Rig");
            if (go != null)
            {
                coreS3Root = go.transform;
                Debug.Log("[RollerHapticSync] coreS3Root 引用失效，已按名称找回 IMU_Rig");
            }
        }
        if (motorRoot == null && coreS3Root != null)
        {
            motorRoot = FindDeep(coreS3Root, "RollerCAN_Rotor");
            if (motorRoot != null)
                Debug.Log("[RollerHapticSync] motorRoot 引用失效，已按名称找回 RollerCAN_Rotor");
        }

        udp = new UdpClient(listenPort);
        running = true;
        rxThread = new Thread(RxLoop) { IsBackground = true };
        rxThread.Start();
        InvokeRepeating(nameof(UpdatePps), 1f, 1f);
        Debug.Log($"[RollerHapticSync] listening UDP :{listenPort}");
    }

    static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            var t = FindDeep(root.GetChild(i), name);
            if (t != null) return t;
        }
        return null;
    }

    void UpdatePps() { packetsPerSec = pktCounter; pktCounter = 0; }

    void RxLoop()
    {
        var ep = new IPEndPoint(IPAddress.Any, listenPort);
        while (running)
        {
            try
            {
                var data = udp.Receive(ref ep);
                lastSenderIp = ep.Address;
                lastPacketTicks = DateTime.UtcNow.Ticks;
                if (TryParseOsc(data, out var msg)) { queue.Enqueue(msg); pktCounter++; }
                else if (Encoding.ASCII.GetString(data).Trim() == "PONG")
                    lastPongTicks = DateTime.UtcNow.Ticks;   // 固件连接确认回包
            }
            catch (Exception) { /* socket closed on quit */ }
        }
    }

    // ---------------- 发送（主线程调用，UDP 纯文本行 → 固件 handleCommand） ----------------

    void UpdateLinkStatus()
    {
        isOpen = (DateTime.UtcNow.Ticks - lastPacketTicks) < TimeSpan.TicksPerSecond;
        linkConfirmed = isOpen &&
            (DateTime.UtcNow.Ticks - lastPongTicks) < 2 * TimeSpan.TicksPerSecond;
        cmdTargetStr = lastSenderIp != null ? $"{lastSenderIp}:{cmdPort}" : "";

        // 连接确认心跳：发现设备后每 0.5s 发 PING（固件回 PONG，不武装看门狗）
        if (isOpen && Time.time >= nextPingAt)
        {
            nextPingAt = Time.time + 0.5f;
            SendRaw("PING");
        }
    }

    public void SendRaw(string line)
    {
        var ip = lastSenderIp;
        if (ip == null || udp == null) return;
        try
        {
            var bytes = Encoding.ASCII.GetBytes(line);
            udp.Send(bytes, bytes.Length, new IPEndPoint(ip, cmdPort));
        }
        catch (Exception) { }
    }

    /// <summary>线张力 0..1 → TENSION,0..1000</summary>
    public void SendTension(float t01)
    {
        int v = Mathf.Clamp(Mathf.RoundToInt(t01 * 1000f), 0, 1000);
        SendRaw($"TENSION,{v}");
    }

    public void SendModeExt()   => SendRaw("MODE,EXT");
    public void SendModeLocal() => SendRaw("MODE,LOCAL");

    // 极简 OSC 解码：单消息、仅 float 参数
    static bool TryParseOsc(byte[] d, out OscMsg msg)
    {
        msg = default;
        int i = 0;
        int addrEnd = Array.IndexOf<byte>(d, 0, i);
        if (addrEnd < 0) return false;
        string addr = Encoding.ASCII.GetString(d, 0, addrEnd);
        i = (addrEnd + 4) & ~3;
        if (i >= d.Length || d[i] != (byte)',') return false;
        int tagEnd = Array.IndexOf<byte>(d, 0, i);
        if (tagEnd < 0) return false;   // tag 串无 \0 终止（坏包）直接丢弃
        string tags = Encoding.ASCII.GetString(d, i + 1, tagEnd - i - 1);
        i = (tagEnd + 4) & ~3;
        var vals = new float[tags.Length];
        for (int k = 0; k < tags.Length; k++)
        {
            if (tags[k] != 'f' || i + 4 > d.Length) return false;
            var b = new byte[] { d[i + 3], d[i + 2], d[i + 1], d[i] }; // 大端转小端
            vals[k] = BitConverter.ToSingle(b, 0);
            i += 4;
        }
        msg = new OscMsg { addr = addr, vals = vals };
        return true;
    }

    float smMotor;
    Quaternion smImu = Quaternion.identity;
    bool imuInitialized;
    Quaternion lastImuRaw;
    bool hasLastImuRaw;
    int glitchRun;
    Quaternion rawQuat;     // 固件 /rollercan/quat 最新值（映射到模型轴后）
    bool hasQuat;           // 收到过四元数通道 = 新固件，优先于欧拉角路径

    void Update()
    {
        UpdateLinkStatus();
        // 积压保护：卡顿后丢弃陈旧遥测只追最新（约 3 秒量），防止恢复时追帧抖动
        if (queue.Count > 600)
            while (queue.TryDequeue(out _)) { }
        while (queue.TryDequeue(out var m))
        {
            if (m.addr == "/rollercan/imu" && m.vals.Length >= 3)
            {
                // 固件在欧拉角空间做陀螺积分，pitch≈±90° 奇点附近会发出
                // 单帧 ~180° 的毛刺姿态（物理上不可能，>6000°/s）——丢弃；
                // 但连续超过 glitchResyncPackets 个包仍超限 = 真实大动作/已稳定，
                // 重新对齐（否则一旦拒包就永久冻结）。
                Quaternion qNew = Quaternion.Euler(m.vals[0], m.vals[2], m.vals[1]);
                if (hasLastImuRaw && Quaternion.Angle(lastImuRaw, qNew) > glitchRejectDeg
                    && ++glitchRun < glitchResyncPackets)
                {
                    // 奇点毛刺包，保持上一帧姿态
                }
                else
                {
                    dbgRoll = m.vals[0]; dbgPitch = m.vals[1]; dbgYaw = m.vals[2];
                    lastImuRaw = qNew; hasLastImuRaw = true;
                    glitchRun = 0;
                }
            }
            else if (m.addr == "/rollercan/quat" && m.vals.Length >= 4)
            {
                // 固件四元数姿态（x,y,z,w，调平后）。轴映射（实机校准 2026-09-13 第二轮）：
                // 设备 roll→模型 +X、pitch→模型 -Z、yaw→模型 -Y（三轴全反 = 上一版取向量共轭），
                // 四元数向量部分 (x,y,z) → (x, -y, -z)。
                hasQuat = true;
                float qx = m.vals[0], qy = -m.vals[1], qz = -m.vals[2], qw = m.vals[3];
                if (invertRoll)  qx = -qx;
                if (invertYaw)   qy = -qy;
                if (invertPitch) qz = -qz;
                rawQuat = new Quaternion(qx, qy, qz, qw);
            }
            else if (m.addr == "/rollercan/motor" && m.vals.Length >= 2)
            {
                dbgMotorAngle = m.vals[0]; dbgMotorCur = m.vals[1];
            }
            else if (m.addr == "/rollercan/acc" && m.vals.Length >= 2)
            {
                // 线性加速度幅值（m/s²，去重力）：vals[0] 瞬时值（甩竿峰值）、
                // vals[1] 15Hz 低通。甩绳/甩竿手势检测用（FishingSim 读 dbgAccRaw）。
                dbgAccRaw = m.vals[0];
                dbgAccMag = m.vals[1];
            }
        }

        // 平滑：IMU 姿态用四元数 Slerp——固件欧拉角会跳等价表示
        // （roll/yaw/pitch 同翻 180°），逐轴 lerp 会反向绕远路；
        // 四元数下等价表示=同值，Slerp 恒走最短路径。电机角单轴，DeltaAngle 即可。
        float k = 1f - smoothing;
        smMotor += Mathf.DeltaAngle(smMotor, dbgMotorAngle) * k;

        if (coreS3Root != null)
        {
            // 优先四元数通道（新固件，全程无欧拉角）；否则欧拉角兜底
            Quaternion qTarget = hasQuat
                ? Quaternion.Normalize(rawQuat)
                : Quaternion.Euler(
                    (invertRoll ? -dbgRoll : dbgRoll) + rollOffset,
                    (invertYaw ? -dbgYaw : dbgYaw) + yawOffset,
                    (invertPitch ? -dbgPitch : dbgPitch) + pitchOffset);
            if (!imuInitialized) { smImu = qTarget; imuInitialized = true; }
            else smImu = Quaternion.Slerp(smImu, qTarget, k);
            coreS3Root.localRotation = smImu;
        }

        float ma = (invertMotorAngle ? -smMotor : smMotor) + motorAngleOffset;
        if (motorRoot != null)
            motorRoot.localRotation = Quaternion.AngleAxis(ma, motorAxis.normalized);

        // IMU 录制：勾选状态变化时启停；每帧记录原始欧拉角 + Slerp 后实际输出姿态
        if (recordImu != recActive)
        {
            if (recordImu)
            {
                recBuf.Length = 0;
                recBuf.AppendLine("t,roll,pitch,yaw,outX,outY,outZ,hasQ,qx,qy,qz,qw,accRaw,accLp");
                recStartTime = Time.realtimeSinceStartup;
                recActive = true;
                Debug.Log("[RollerHapticSync] IMU 录制开始");
            }
            else FlushRecord();
        }
        if (recActive)
        {
            Vector3 oe = coreS3Root != null ? coreS3Root.localEulerAngles : Vector3.zero;
            recBuf.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0:F4},{1:F3},{2:F3},{3:F3},{4:F3},{5:F3},{6:F3},{7},{8:F5},{9:F5},{10:F5},{11:F5},{12:F3},{13:F3}",
                Time.realtimeSinceStartup - recStartTime,
                dbgRoll, dbgPitch, dbgYaw, oe.x, oe.y, oe.z,
                hasQuat ? 1 : 0, rawQuat.x, rawQuat.y, rawQuat.z, rawQuat.w,
                dbgAccRaw, dbgAccMag));
        }
    }

    void FlushRecord()
    {
        recActive = false;
        if (recBuf.Length == 0) return;
        string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..",
            $"imu_record_{System.DateTime.Now:yyyyMMdd_HHmmss}.csv"));
        System.IO.File.WriteAllText(path, recBuf.ToString());
        Debug.Log($"[RollerHapticSync] IMU 录制已保存: {path}");
        recBuf.Length = 0;
    }

    void OnDestroy()
    {
        if (recActive) FlushRecord();
        running = false;
        udp?.Close();
        CancelInvoke();
    }
}
