/*
 * FishingSim.cs — 钓鱼模拟器最小玩法（手感优先，美术后置）
 *
 * 循环：IDLE →(Space 或甩竿[加速度幅值超阈值] 抛竿)→ CAST → WAIT →(随机咬钩)→ BITE →(摇柄扬竿)→ FIGHT
 *       FIGHT → 收线尽=LANDED / 张力爆表=LINE_BROKEN / 超时松口=ESCAPED
 *
 * 手感链路：FIGHT 时发 MODE,EXT，每帧张力 → TENSION,0..1000 → 电机反向扭矩；
 * 摇柄编码器回传，收线/出线由真实转子角度驱动（不摇时鱼把摇柄拽反转 = 出线）。
 * 结束（LANDED/ESCAPED/LINE_BROKEN）发 MODE,LOCAL 回本地力反馈。
 * 固件侧 200ms 看门狗兜底：Stop Play / 断链自动释放。
 *
 * 链路：USB 串口（RollerHapticSerial）与 UDP/OSC（RollerHapticSync）双路并存，
 * 组件由 FindFirstObjectByType 自动发现；命令往所有活着的链路发，角度优先串口。
 * 推荐 OSC：不占串口，烧录/串口监视不用停 Play。
 *
 * 视觉从简：水面 Plane + 浮漂 Sphere + 鱼 Capsule + LineRenderer 鱼线。
 * UI 用 OnGUI（免 uGUI 搭建）：张力条 / 耐力条 / 距离 / 状态。
 */
using UnityEngine;

public class FishingSim : MonoBehaviour
{
    public enum State { IDLE, CAST, WAIT, BITE, FIGHT, LANDED, ESCAPED, LINE_BROKEN }

    [Header("链路（场景里自动 FindFirstObjectByType，不用手拖）")]
    public RollerHapticSerial serial;   // USB 串口链路（可选）
    public RollerHapticSync oscLink;    // UDP/OSC 链路（可选，优先推荐：不占串口）

    [Header("场景引用")]
    public Transform rodTip;        // 竿尖（挂 IMU_Rig 下，随姿态动）
    public Transform bobber;        // 浮漂
    public Transform fish;          // 鱼（胶囊体）
    public LineRenderer fishLine;   // 鱼线
    public Transform waterSurface;  // 水面（取 y 高度用）

    [Header("鱼参数")]
    [Range(0f, 1f)] public float fishBasePull = 0.45f;   // 基础拉力（占 EXT_MAX_CURRENT 比例；FIGHT 开始时被鱼种表覆盖）
    [Range(0f, 0.5f)] public float struggleAmp = 0.18f;  // 挣扎幅度
    public float struggleFreq = 1.6f;                    // 挣扎频率 Hz
    [Tooltip("每秒冲刺概率")] public float sprintProbPerSec = 0.25f;
    [Range(0f, 0.6f)] public float sprintBoost = 0.35f;
    public float sprintDuration = 0.7f;
    public float staminaMax = 100f;
    [Tooltip("高张力下耐力消耗/秒")] public float staminaDrain = 14f;
    [Tooltip("耐力耗尽后拉力衰减到")] [Range(0.1f, 1f)] public float exhaustPullScale = 0.35f;

    [System.Serializable] public class FishSpecies
    {
        public string name;
        public float basePull;
        public Color baseColor;
    }

    [Header("鱼种表（下标 = 固件协议 FISH,<id> = 精灵数组下标）")]
    public FishSpecies[] species = new FishSpecies[]
    {
        new FishSpecies { name = "鲫鱼",       basePull = 0.30f, baseColor = new Color(0.85f, 0.65f, 0.10f) }, // 0 金黄
        new FishSpecies { name = "锦鲤",       basePull = 0.45f, baseColor = new Color(0.95f, 0.35f, 0.15f) }, // 1 橙红
        new FishSpecies { name = "鲈鱼",       basePull = 0.60f, baseColor = new Color(0.25f, 0.70f, 0.25f) }, // 2 绿
        new FishSpecies { name = "鲶鱼",       basePull = 0.80f, baseColor = new Color(0.35f, 0.30f, 0.75f) }, // 3 蓝紫
        new FishSpecies { name = "小虾",       basePull = 0.15f, baseColor = new Color(0.95f, 0.45f, 0.20f) }, // 4 橙红
        new FishSpecies { name = "小螃蟹",     basePull = 0.25f, baseColor = new Color(0.85f, 0.15f, 0.10f) }, // 5 红
        new FishSpecies { name = "萨卡班甲鱼", basePull = 0.50f, baseColor = new Color(0.55f, 0.60f, 0.45f) }, // 6 灰绿（彩蛋）
    };

    [Header("张力模型")]
    [Tooltip("收线速度对张力的加成")] public float tensionReelGain = 0.35f;
    [Tooltip("收线该速度(度/秒)时加成打满")] public float reelSpeedFull = 360f;
    [Tooltip("sqrt 曲线让小鱼也有细腻手感")] public bool tensionSqrtCurve = true;
    [Range(0.5f, 1f)] public float breakThreshold = 0.95f;
    public float breakHoldTime = 0.3f;

    [Header("线长")]
    public float startLineLen = 3f;       // 抛竿后出线长度（米）
    public float metersPerDeg = 0.0008f;  // 摇柄 1° 收线长度
    public bool invertReel = false;       // 收线方向反了勾选
    public float landedLineLen = 0.15f;   // 收到该长度算上岸
    public float maxLineLen = 6f;         // 线杯容量（被拖到上限=逃）

    [Header("甩竿检测（加速度计，OSC 链路 /rollercan/acc）")]
    [Tooltip("甩竿触发阈值 m/s²（线性加速度瞬时值 dbgAccRaw 超过即抛竿）")]
    public float whipThreshold = 5f;
    [Tooltip("触发后冷却秒数（防抖）")]
    public float whipCooldown = 1.5f;

    [Header("调试（只读）")]
    public State state = State.IDLE;
    public float tension01;
    public float lineLen;
    public float stamina;
    public float fishPull01;
    public int fishSpeciesIdx = -1;   // 当前中鱼鱼种（species 下标，-1 = 无）

    [Header("渔获统计（只读）")]
    public int landedCount;                          // 总上岸数
    public int[] landedPerSpecies = new int[7];      // 分鱼种计数

    [Header("鱼精灵（Resources/fish/fish_N.png，运行时装配 billboard）")]
    public float fishSpriteSize = 0.4f;              // 精灵世界尺寸（米）

    Transform fishQuad;       // 运行时建的 Quad（根级，避免 Fish 非均匀缩放扭曲）
    Renderer fishQuadRend;
    static Texture2D[] fishTex;
    Vector3 landedFrom;       // 上岸动画起点（鱼最后的水中位置）
    float fishSpin;           // 上岸跃起翻滚角（落地后侧躺 90°）

    float waitTimer, biteTimer, stateTimer, sprintTimer;
    float overBreakTimer;
    float lastMotorAngle;
    bool hasAngle;
    float sendTimer;
    float whipCooldownTimer;
    Vector3 castFrom, castTo;
    readonly System.Random rng = new System.Random();

    float WaterY => waterSurface != null ? waterSurface.position.y : 0f;

    // ---------------- 链路抽象（串口 / OSC 双路，自动发现组件） ----------------

    bool LinkOpen => (serial != null && serial.isOpen) || (oscLink != null && oscLink.isOpen);

    /// <summary>电机角度（度）：优先串口（延迟低），否则 OSC</summary>
    float LinkMotorAngle
    {
        get
        {
            if (serial != null && serial.isOpen) return serial.MotorAngleRaw;
            if (oscLink != null && oscLink.isOpen) return oscLink.MotorAngleRaw;
            return 0f;
        }
    }

    /// <summary>命令往所有活着的链路都发（幂等，固件同一 handleCommand 入口）</summary>
    void LinkSend(System.Action<RollerHapticSerial> viaSerial, System.Action<RollerHapticSync> viaOsc)
    {
        if (serial != null && serial.isOpen) viaSerial(serial);
        if (oscLink != null && oscLink.isOpen) viaOsc(oscLink);
    }

    void Start()
    {
        if (serial == null) serial = FindAnyObjectByType<RollerHapticSerial>();
        if (oscLink == null) oscLink = FindAnyObjectByType<RollerHapticSync>();
        if (fish != null) fish.gameObject.SetActive(false);
        if (bobber != null) bobber.gameObject.SetActive(false);
        if (fishLine != null) fishLine.gameObject.SetActive(false);
        EnterIdle();
    }

    void EnterIdle()
    {
        state = State.IDLE;
        tension01 = 0f;
        if (bobber != null) bobber.gameObject.SetActive(false);
        if (fish != null) fish.gameObject.SetActive(false);
        if (fishQuad != null) fishQuad.gameObject.SetActive(false);
        if (fishLine != null) fishLine.gameObject.SetActive(false);
    }

    // ---------------- 鱼精灵（运行时装配，免场景改动） ----------------

    void EnsureFishQuad()
    {
        if (fishQuad != null) return;
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = "FishSprite";
        Destroy(go.GetComponent<Collider>());
        fishQuad = go.transform;
        fishQuad.localScale = Vector3.one * fishSpriteSize;
        fishQuadRend = go.GetComponent<Renderer>();
        fishQuadRend.sharedMaterial = new Material(Shader.Find("Unlit/Transparent"));
        go.SetActive(false);
    }

    static Texture2D LoadFishTex(int idx)
    {
        if (fishTex == null) fishTex = new Texture2D[7];
        if (fishTex[idx] == null) fishTex[idx] = Resources.Load<Texture2D>($"fish/fish_{idx}");
        return fishTex[idx];
    }

    void LateUpdate()
    {
        // 精灵跟随鱼锚点 + 朝相机 billboard（空中跃起时叠加翻滚）
        if (fishQuad != null && fishQuad.gameObject.activeSelf && fish != null)
        {
            fishQuad.position = fish.position;
            var cam = Camera.main;
            if (cam != null)
                fishQuad.rotation =
                    Quaternion.LookRotation(fishQuad.position - cam.transform.position)
                    * Quaternion.Euler(0f, 0f, fishSpin);
        }
    }

    void Update()
    {
        float dt = Time.deltaTime;

        // 摇柄角速度（度/秒），正 = 收线方向
        float reelDps = 0f;
        if (LinkOpen)
        {
            float a = LinkMotorAngle;
            if (hasAngle && dt > 0f)
            {
                float d = a - lastMotorAngle;
                if (invertReel) d = -d;
                reelDps = d / dt;
            }
            lastMotorAngle = a;
            hasAngle = true;
        }

        whipCooldownTimer -= dt;
        // 甩竿检测：OSC 链路在线且加速度瞬时幅值超阈值（甩动/急停的冲量）。
        // IDLE 与三个终局状态都可触发（终局需先亮结果 1.5s），甩竿 = 直接抛下一竿。
        bool whip = whipCooldownTimer <= 0f
            && oscLink != null && oscLink.isOpen && oscLink.dbgAccRaw >= whipThreshold;

        switch (state)
        {
            case State.IDLE:
                if (Input.GetKeyDown(KeyCode.Space) || whip)
                {
                    whipCooldownTimer = whipCooldown;
                    if (whip) Debug.Log($"[FishingSim] 甩竿触发抛竿（acc {oscLink.dbgAccRaw:0.0} m/s²）");
                    StartCast();
                }
                break;

            case State.CAST:
                stateTimer += dt;
                float k = Mathf.Clamp01(stateTimer / 0.6f);
                if (bobber != null)
                    bobber.position = Vector3.Lerp(castFrom, castTo, k)
                        + Vector3.up * (Mathf.Sin(k * Mathf.PI) * 0.4f);
                if (k >= 1f) { state = State.WAIT; waitTimer = Mathf.Lerp(2f, 6f, (float)rng.NextDouble()); }
                break;

            case State.WAIT:
                BobberIdle(dt);
                waitTimer -= dt;
                if (waitTimer <= 0f) { state = State.BITE; biteTimer = 1.2f; }
                break;

            case State.BITE:
                BobberIdle(dt, 0.03f);  // 咬钩：浮漂猛沉
                biteTimer -= dt;
                // 扬竿刺鱼：快速摇柄或 Space
                if (reelDps > 90f || Input.GetKeyDown(KeyCode.Space)) StartFight();
                else if (biteTimer <= 0f) EndFight(State.ESCAPED, "鱼跑了（没扬竿）");
                break;

            case State.FIGHT:
                TickFight(dt, reelDps);
                break;

            case State.LANDED:
                stateTimer += dt;
                // 上岸动画：鱼从水中抛物线跃到竿旁，空中翻滚，落地侧躺
                if (fish != null && fish.gameObject.activeSelf)
                {
                    float lk = Mathf.Clamp01(stateTimer / 0.8f);
                    Vector3 shore = (rodTip != null ? rodTip.position : transform.position)
                        + Vector3.up * 0.2f + Vector3.back * 0.25f;
                    fish.position = Vector3.Lerp(landedFrom, shore, lk)
                        + Vector3.up * (Mathf.Sin(lk * Mathf.PI) * 0.45f);
                    fishSpin = lk < 1f ? stateTimer * 540f : 90f;
                    UpdateLine(fish.position);
                }
                if (stateTimer > 1.5f && (Input.GetKeyDown(KeyCode.Space) || whip))
                {
                    whipCooldownTimer = whipCooldown;
                    StartCast();    // 甩竿/空格直接抛下一竿
                }
                break;

            case State.ESCAPED:
            case State.LINE_BROKEN:
                stateTimer += dt;
                if (stateTimer > 1.5f && (Input.GetKeyDown(KeyCode.Space) || whip))
                {
                    whipCooldownTimer = whipCooldown;
                    StartCast();    // 甩竿/空格直接抛下一竿（不必先回 IDLE）
                }
                break;
        }
    }

    // ---------------- 抛竿/咬钩 ----------------

    void StartCast()
    {
        state = State.CAST;
        stateTimer = 0f;
        if (fish != null) fish.gameObject.SetActive(false);        // 上一局的鱼清场
        if (fishQuad != null) fishQuad.gameObject.SetActive(false);
        castFrom = rodTip != null ? rodTip.position : transform.position;
        castTo = castFrom + Vector3.forward * startLineLen;
        castTo.y = WaterY;
        if (bobber != null) { bobber.gameObject.SetActive(true); bobber.position = castFrom; }
        if (fishLine != null) fishLine.gameObject.SetActive(true);
    }

    void BobberIdle(float dt, float dipAmp = 0.008f)
    {
        if (bobber == null) return;
        var p = bobber.position;
        p.y = WaterY + Mathf.Sin(Time.time * 2.2f) * 0.006f
            - (state == State.BITE ? dipAmp * Mathf.Abs(Mathf.Sin(Time.time * 14f)) : 0f);
        bobber.position = p;
        UpdateLine(bobber.position);
    }

    // ---------------- 遛鱼 ----------------

    void StartFight()
    {
        state = State.FIGHT;
        stateTimer = 0f;
        stamina = staminaMax;
        overBreakTimer = 0f;
        sprintTimer = 0f;
        lineLen = startLineLen;

        // 随机鱼种：拉力手感 + 固件精灵（FISH,<id>）+ 胶囊体颜色 HSV 抖动
        fishSpeciesIdx = rng.Next(species.Length);
        var sp = species[fishSpeciesIdx];
        fishBasePull = sp.basePull;
        Color.RGBToHSV(sp.baseColor, out float h, out float s, out float v);
        h = (h + (float)(rng.NextDouble() - 0.5) * 0.06f + 1f) % 1f;   // hue ±0.03
        s = Mathf.Clamp01(s + (float)(rng.NextDouble() - 0.5) * 0.3f); // sat ±0.15
        v = Mathf.Clamp01(v + (float)(rng.NextDouble() - 0.5) * 0.3f); // val ±0.15

        if (bobber != null) bobber.gameObject.SetActive(false);
        fishSpin = 0f;
        if (fish != null)
        {
            fish.gameObject.SetActive(true);
            fish.position = (rodTip != null ? rodTip.position : Vector3.zero)
                + Vector3.forward * lineLen + Vector3.down * 0.35f;
            var rend = fish.GetComponent<Renderer>();
            if (rend != null)
            {
                rend.enabled = true;   // 无精灵图时回退胶囊体
                var mpb = new MaterialPropertyBlock();
                mpb.SetColor("_Color", Color.HSVToRGB(h, s, v));
                rend.SetPropertyBlock(mpb);
            }
            // 像素鱼精灵（billboard quad，纹理 = 固件同款 fish_N.png，颜色同样 HSV 抖动）
            EnsureFishQuad();
            var tex = LoadFishTex(fishSpeciesIdx);
            if (tex != null)
            {
                fishQuadRend.sharedMaterial.mainTexture = tex;
                fishQuadRend.sharedMaterial.color = Color.HSVToRGB(h, s, v);
                fishQuad.position = fish.position;
                fishQuad.gameObject.SetActive(true);
                if (rend != null) rend.enabled = false;   // 有精灵图就藏胶囊体
            }
        }
        LinkSend(s => s.SendRaw($"FISH,{fishSpeciesIdx}"), o => o.SendRaw($"FISH,{fishSpeciesIdx}"));  // 固件屏幕切对应像素鱼
        LinkSend(s => s.SendModeExt(), o => o.SendModeExt());                                          // 中鱼才进外部驱动
    }

    void TickFight(float dt, float reelDps)
    {
        stateTimer += dt;

        // 鱼瞬时拉力：基础 × (1 + 挣扎 sin + 噪声) + 冲刺脉冲
        float struggle = struggleAmp * Mathf.Sin(stateTimer * 2f * Mathf.PI * struggleFreq);
        float noise = Mathf.PerlinNoise(stateTimer * 0.7f, 3.7f) * 0.1f - 0.05f;
        if (sprintTimer <= 0f && Random.value < sprintProbPerSec * dt) sprintTimer = sprintDuration;
        float sprint = 0f;
        if (sprintTimer > 0f) { sprint = sprintBoost; sprintTimer -= dt; }
        float pullScale = stamina > 0f ? 1f : exhaustPullScale;
        fishPull01 = Mathf.Clamp01((fishBasePull * (1f + struggle + noise) + sprint) * pullScale);

        // 收线/出线：真实摇柄角度驱动（不摇时鱼扭矩拽反转子 → reelDps<0 → 出线）
        lineLen = Mathf.Clamp(lineLen - reelDps * metersPerDeg * dt, 0f, maxLineLen);

        // 张力：鱼拉力为底，收线越快张力越高
        float reelNorm = Mathf.Clamp01(Mathf.Max(0f, reelDps) / reelSpeedFull);
        tension01 = Mathf.Clamp01(fishPull01 * 0.85f + reelNorm * tensionReelGain);

        // 耐力：高张力持续消耗
        if (tension01 > 0.6f) stamina = Mathf.Max(0f, stamina - staminaDrain * dt);

        // 断线：红区持续 breakHoldTime
        if (tension01 >= breakThreshold)
        {
            overBreakTimer += dt;
            if (overBreakTimer >= breakHoldTime) { EndFight(State.LINE_BROKEN, "断线！"); return; }
        }
        else overBreakTimer = 0f;

        // 胜负
        if (lineLen <= landedLineLen) { EndFight(State.LANDED, "上岸！"); return; }
        if (lineLen >= maxLineLen)    { EndFight(State.ESCAPED, "线被拖光，鱼跑了"); return; }

        // 下发扭矩（≥30Hz，喂饱固件 200ms 看门狗）
        sendTimer -= dt;
        if (sendTimer <= 0f)
        {
            sendTimer = 0.03f;
            float t = tensionSqrtCurve ? Mathf.Sqrt(tension01) : tension01;
            LinkSend(s => s.SendTension(t), o => o.SendTension(t));
        }

        // 视觉：鱼在水下游窜
        if (fish != null && rodTip != null)
        {
            Vector3 dir = (Vector3.forward + Vector3.right * Mathf.Sin(stateTimer * 0.9f) * 0.3f).normalized;
            fish.position = rodTip.position + dir * lineLen + Vector3.down * (0.35f - fishPull01 * 0.1f);
            fish.rotation = Quaternion.LookRotation(-dir);
            UpdateLine(fish.position);
        }
    }

    void EndFight(State s, string msg)
    {
        state = s;
        stateTimer = 0f;
        tension01 = 0f;
        LinkSend(x => x.SendModeLocal(), o => o.SendModeLocal());  // 立刻松扭矩回本地
        if (s == State.LANDED)
        {
            landedCount++;
            if (fishSpeciesIdx >= 0 && fishSpeciesIdx < landedPerSpecies.Length)
                landedPerSpecies[fishSpeciesIdx]++;
            landedFrom = fish != null ? fish.position : Vector3.zero;  // 跃出水动画起点
        }
        else
        {
            if (fish != null) fish.gameObject.SetActive(false);        // 鱼跑了/断线：鱼消失
            if (fishQuad != null) fishQuad.gameObject.SetActive(false);
        }
        Debug.Log($"[FishingSim] {s}: {msg}（累计上岸 {landedCount}）");
    }

    void UpdateLine(Vector3 to)
    {
        if (fishLine == null || rodTip == null) return;
        fishLine.SetPosition(0, rodTip.position);
        fishLine.SetPosition(1, to);
    }

    // ---------------- UI（OnGUI 从简） ----------------

    void OnGUI()
    {
        GUI.Label(new Rect(16, 12, 560, 26), $"<b><size=18>{StateText()}</size></b>", Rich());

        // 右上角：IMU 调平（固件 LEVEL 命令：以当前姿态为零点，不切模式、不武装看门狗）
        if (GUI.Button(new Rect(Screen.width - 126, 12, 110, 28), "IMU 调平"))
            LinkSend(s => s.SendRaw("LEVEL"), o => o.SendRaw("LEVEL"));

        // 右上：渔获计数面板（总数 + 分鱼种）
        int lines = 0;
        for (int i = 0; i < species.Length && i < landedPerSpecies.Length; i++)
            if (landedPerSpecies[i] > 0) lines++;
        GUI.color = new Color(0f, 0f, 0f, 0.5f);
        GUI.DrawTexture(new Rect(Screen.width - 152, 46, 136, 24 + 18 * lines), Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.Label(new Rect(Screen.width - 146, 50, 130, 20), $"<b>渔获 {landedCount} 条</b>", Rich());
        float ly = 70;
        for (int i = 0; i < species.Length && i < landedPerSpecies.Length; i++)
            if (landedPerSpecies[i] > 0)
            {
                GUI.Label(new Rect(Screen.width - 146, ly, 130, 18),
                    $"{species[i].name} ×{landedPerSpecies[i]}", Rich());
                ly += 18;
            }

        // 上岸横幅：easeOutBack 弹入
        if (state == State.LANDED)
        {
            float t = Mathf.Clamp01(stateTimer / 0.45f);
            const float c1 = 1.70158f, c3 = c1 + 1f;
            float pop = 1f + c3 * Mathf.Pow(t - 1f, 3f) + c1 * Mathf.Pow(t - 1f, 2f);
            string spName2 = fishSpeciesIdx >= 0 && fishSpeciesIdx < species.Length
                ? species[fishSpeciesIdx].name : "鱼";
            var mtx = GUI.matrix;
            var pivot = new Vector2(Screen.width / 2f, Screen.height * 0.3f);
            GUIUtility.ScaleAroundPivot(Vector2.one * pop, pivot);
            GUI.color = new Color(1f, 0.9f, 0.3f);
            GUI.Label(new Rect(pivot.x - 250, pivot.y - 30, 500, 60),
                $"<b><size=42>钓到了 {spName2}！</size></b>", Centered());
            GUI.color = Color.white;
            GUI.matrix = mtx;
        }

        // 张力条
        DrawBar(new Rect(16, 46, 300, 18), tension01,
            Color.Lerp(Color.green, Color.red, tension01), "张力");
        if (state == State.FIGHT)
        {
            DrawBar(new Rect(16, 70, 300, 14), stamina / staminaMax, Color.cyan, "鱼耐力");
            // 收线进度条：lineLen 从满到 0 = 收线完成（鱼跑线会回退）
            DrawBar(new Rect(16, 90, 300, 12), 1f - lineLen / maxLineLen,
                new Color(0.2f, 0.8f, 1f), "收线进度");
            string spName = fishSpeciesIdx >= 0 && fishSpeciesIdx < species.Length
                ? species[fishSpeciesIdx].name : "?";
            GUI.Label(new Rect(16, 108, 300, 22), $"{spName} · 鱼距离 {lineLen:0.00} m", Rich());
        }
        if (!LinkOpen)
            GUI.Label(new Rect(16, 134, 400, 22), "<color=red>链路未连接（手感不可用）</color>", Rich());

        // 左下角：姿态摇杆（与真实 IMU 数据一致：X=yaw 左右，Y=pitch 上下，±90° 满偏）
        DrawJoystick(new Rect(16, Screen.height - 152, 120, 120));

        // 摇杆右侧：加速度幅值实时显示（甩竿阈值调参用）
        if (oscLink != null && oscLink.isOpen)
            GUI.Label(new Rect(146, Screen.height - 44, 400, 22),
                $"acc 瞬时 {oscLink.dbgAccRaw:0.0} / 低通 {oscLink.dbgAccMag:0.0} m/s²（甩竿阈值 {whipThreshold:0.0}）", Rich());
    }

    string StateText()
    {
        switch (state)
        {
            case State.IDLE:        return "SPACE 或甩竿抛投";
            case State.CAST:        return "抛竿…";
            case State.WAIT:        return "等鱼咬钩…";
            case State.BITE:        return "咬钩！快摇柄扬竿！";
            case State.FIGHT:       return "中鱼！摇柄收线，注意张力";
            case State.LANDED:      return "鱼上岸了！甩竿/SPACE 再来";
            case State.ESCAPED:     return "鱼跑了… 甩竿/SPACE 再来";
            case State.LINE_BROKEN: return "断线！甩竿/SPACE 再来";
            default: return "";
        }
    }

    GUIStyle rich;
    GUIStyle Rich() => rich ?? (rich = new GUIStyle(GUI.skin.label) { richText = true });
    GUIStyle centered;
    GUIStyle Centered() => centered ?? (centered = new GUIStyle(GUI.skin.label)
        { richText = true, alignment = TextAnchor.MiddleCenter });

    void DrawBar(Rect r, float v, Color c, string label)
    {
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = c;
        GUI.DrawTexture(new Rect(r.x + 1, r.y + 1, (r.width - 2) * Mathf.Clamp01(v), r.height - 2),
            Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.Label(new Rect(r.x + 6, r.y - 1, 200, r.height + 4), label, Rich());
    }

    // 姿态摇杆：只读可视化，摇杆球位置与真实 IMU 数据一致
    // （X = dbgYaw 左右偏，Y = dbgPitch 前后俯仰，±90° 满偏；球是方块，OnGUI 画不了圆）
    void DrawJoystick(Rect r)
    {
        GUI.color = new Color(0f, 0f, 0f, 0.5f);
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        // 十字准线
        GUI.color = new Color(1f, 1f, 1f, 0.25f);
        GUI.DrawTexture(new Rect(r.x + r.width / 2 - 1, r.y + 4, 2, r.height - 8), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.x + 4, r.y + r.height / 2 - 1, r.width - 8, 2), Texture2D.whiteTexture);
        // 摇杆球
        float yaw   = oscLink != null ? oscLink.dbgYaw   : 0f;
        float pitch = oscLink != null ? oscLink.dbgPitch : 0f;
        float kx = Mathf.Clamp(yaw / 90f, -1f, 1f);
        float ky = Mathf.Clamp(pitch / 90f, -1f, 1f);
        const float knob = 20f;
        float travel = (r.width - knob) / 2f - 4f;
        var ball = new Rect(r.x + r.width / 2 + kx * travel - knob / 2,
                            r.y + r.height / 2 - ky * travel - knob / 2,
                            knob, knob);
        GUI.color = LinkOpen ? new Color(0.2f, 0.8f, 1f, 0.95f) : new Color(0.5f, 0.5f, 0.5f, 0.8f);
        GUI.DrawTexture(ball, Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.Label(new Rect(r.x, r.y - 18, r.width + 80, 18), "竿姿态 yaw/pitch", Rich());
    }
}
