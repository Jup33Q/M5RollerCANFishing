import SwiftUI

final class AppModel: ObservableObject {
    @Published var ports: [SerialPort.PortInfo] = []
    @Published var selectedPort: String = ""
    @Published var connected = false
    @Published var serialLog = ""
    @Published var flashLog = ""
    @Published var projectDir = ""
    @Published var busy = false

    // WiFi / OSC 配置（写入固件 src/wifi_config.h，构建时生效）
    @Published var wifiSSID = UserDefaults.standard.string(forKey: "wifiSSID") ?? ""
    @Published var wifiPass = UserDefaults.standard.string(forKey: "wifiPass") ?? ""
    @Published var targetIP = UserDefaults.standard.string(forKey: "targetIP")
        ?? WifiConfigSync.primaryIPv4() ?? ""

    // 固件变体（钓鱼 = 独立 firmware-fishing 工程；标准 = firmware 工程）
    @Published var variant = FirmwareVariant(
        rawValue: UserDefaults.standard.string(forKey: "firmwareVariant") ?? "fishing") ?? .fishing

    // 串口监视页手动命令（TENSION/SETCUR/MODE，调鱼的手感不用开 Unity）
    @Published var manualCmd = ""

    // 解析 HAPTIC,mode,pos,cur[,roll,pitch,yaw] 遥测
    @Published var lastMode = "-"
    @Published var lastDeg: Double = 0
    @Published var lastCur: Int = 0
    @Published var lastRoll: Double = 0
    @Published var lastPitch: Double = 0
    @Published var history: [Double] = []   // 角度曲线（最近 300 点）

    let serial = SerialPort()
    let flasher = Flasher()

    init() {
        refreshPorts()
        serial.onReceive = { [weak self] s in self?.appendSerial(s) }
        serial.onClose = { [weak self] in self?.connected = false }
        flasher.onLog = { [weak self] s in
            guard let self else { return }
            self.flashLog += s
            if self.flashLog.count > 200_000 { self.flashLog = String(self.flashLog.suffix(150_000)) }
            if s.contains("退出码") { self.busy = false }
        }
        // 按变体定位固件工程：优先 App 包内 Resources/<工程名>（拷贝到可写目录再构建），
        // 其次源码工程相对路径
        projectDir = Self.resolveProjectDir(for: variant)
    }

    /// 变体 → 固件工程目录：bundle Resources 播种到 App Support 工作副本（可写），
    /// 其次源码树开发路径
    static func resolveProjectDir(for v: FirmwareVariant) -> String {
        let support = "\(NSHomeDirectory())/Library/Application Support/RollerFlasher/\(v.projectDirName)"
        if let bundled = Bundle.main.resourceURL?.appendingPathComponent(v.projectDirName).path,
           FileManager.default.fileExists(atPath: "\(bundled)/platformio.ini") {
            if !FileManager.default.fileExists(atPath: "\(support)/platformio.ini") {
                try? FileManager.default.createDirectory(
                    atPath: "\(NSHomeDirectory())/Library/Application Support/RollerFlasher",
                    withIntermediateDirectories: true)
                try? FileManager.default.copyItem(atPath: bundled, toPath: support)
            }
            if FileManager.default.fileExists(atPath: "\(support)/platformio.ini") {
                return support
            }
            return bundled
        }
        let dev = "\(NSHomeDirectory())/Documents/rollercan-haptic/\(v.projectDirName)"
        if FileManager.default.fileExists(atPath: "\(dev)/platformio.ini") {
            return dev
        }
        return ""
    }

    func refreshPorts() {
        ports = SerialPort.listPorts()
        if selectedPort.isEmpty || !ports.contains(where: { $0.path == selectedPort }) {
            selectedPort = ports.first?.path ?? ""
        }
    }

    func toggleConnect() {
        if connected {
            serial.close()
            connected = false
        } else if !selectedPort.isEmpty {
            connected = serial.open(path: selectedPort)
            if connected { serialLog += "[已连接 \(selectedPort) @115200]\n" }
        }
    }

    /// 烧录前断开串口，避免与 esptool 争抢端口导致写完后校验失败
    func prepareFlash() {
        if connected {
            serial.close()
            connected = false
            flashLog += "[已自动断开串口监视，避免端口占用]\n"
        }
        busy = true
    }

    /// 读取 Mac 当前 WiFi 名称（macOS 可能因隐私限制返回空，此时手填）
    func detectWiFi() {
        if let ssid = WifiConfigSync.currentWiFiSSID() {
            wifiSSID = ssid
            flashLog += "[已读取本机 WiFi：\(ssid)]\n"
        } else {
            flashLog += "[读不到本机 WiFi 名称（macOS 隐私限制），请手动输入 SSID]\n"
        }
    }

    /// 目标 IP 设为本机主用 IPv4
    func useLocalIP() {
        if let ip = WifiConfigSync.primaryIPv4() {
            targetIP = ip
            flashLog += "[目标 IP 设为本机：\(ip)]\n"
        } else {
            flashLog += "[未找到本机 IPv4 地址]\n"
        }
    }

    /// 把 WiFi 配置写进固件工程 src/wifi_config.h；构建/烧录前自动调用
    func syncWifiConfig() {
        let ud = UserDefaults.standard
        ud.set(wifiSSID, forKey: "wifiSSID")
        ud.set(wifiPass, forKey: "wifiPass")
        ud.set(targetIP, forKey: "targetIP")
        guard !projectDir.isEmpty else { return }
        do {
            let path = try WifiConfigSync.writeHeader(
                projectDir: projectDir, ssid: wifiSSID, pass: wifiPass, targetIP: targetIP)
            flashLog += "[WiFi 配置 → \(path)：SSID=\(wifiSSID)  目标=\(targetIP):8000]\n"
        } catch {
            flashLog += "[WiFi 配置写入失败：\(error.localizedDescription)]\n"
        }
    }

    /// 变体切换/烧录前：切到变体对应的固件工程，清理旧 variant_config.h 残留
    func syncVariantConfig() {
        UserDefaults.standard.set(variant.rawValue, forKey: "firmwareVariant")
        let dir = Self.resolveProjectDir(for: variant)
        if !dir.isEmpty && dir != projectDir {
            projectDir = dir
            flashLog += "[固件变体：\(variant.title) — 工程 \(variant.projectDirName)]\n"
        }
        let msg = VariantConfigSync.cleanLegacyHeader(projectDir: projectDir)
        if msg.hasPrefix("已清理") { flashLog += "[\(msg)]\n" }
    }

    /// 「仅写入配置」：WiFi + 变体一次写齐
    func syncAllConfigs() {
        syncWifiConfig()
        syncVariantConfig()
    }

    /// 串口监视页手动发送命令（自动补换行）
    func sendManual() {
        let cmd = manualCmd.trimmingCharacters(in: .whitespacesAndNewlines)
        guard connected, !cmd.isEmpty else { return }
        serial.send(cmd + "\n")
        serialLog += "> \(cmd)\n"
    }

    private var lineBuf = ""
    private var pendingSerial = ""
    private var flushScheduled = false

    private func appendSerial(_ s: String) {
        // 逐行解析遥测（轻量，每条都处理）
        lineBuf += s
        while let r = lineBuf.firstRange(of: "\n") {
            let line = String(lineBuf[lineBuf.startIndex..<r.lowerBound])
            lineBuf.removeSubrange(lineBuf.startIndex...r.lowerBound)
            parse(line)
        }
        // 日志显示节流合并：200Hz 遥测直接 += 会把主线程刷爆（大字符串 O(n²) 拷贝）
        pendingSerial += s
        guard !flushScheduled else { return }
        flushScheduled = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { [weak self] in
            guard let self else { return }
            self.flushScheduled = false
            guard !self.pendingSerial.isEmpty else { return }
            self.serialLog += self.pendingSerial
            self.pendingSerial = ""
            if self.serialLog.count > 60_000 { self.serialLog = String(self.serialLog.suffix(40_000)) }
        }
    }

    private func parse(_ line: String) {
        let f = line.split(separator: ",")
        guard f.count >= 4, f[0] == "HAPTIC",
              let pos = Double(f[2]), let cur = Int(f[3]) else { return }
        lastMode = String(f[1])
        lastDeg = pos / 100.0
        lastCur = cur
        if f.count >= 7, let r = Double(f[4]), let p = Double(f[5]) {
            lastRoll = r
            lastPitch = p
        }
        history.append(lastDeg)
        if history.count > 300 { history.removeFirst(history.count - 300) }
    }
}

struct ContentView: View {
    @StateObject private var model = AppModel()

    var body: some View {
        TabView {
            monitorTab.tabItem { Label("串口监视", systemImage: "terminal") }
            flashTab.tabItem { Label("固件烧录", systemImage: "arrow.down.doc") }
        }
        .padding(12)
    }

    // MARK: 串口监视
    var monitorTab: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Picker("串口", selection: $model.selectedPort) {
                    ForEach(model.ports) { Text($0.name).tag($0.path) }
                }
                .frame(maxWidth: 380)
                Button("刷新") { model.refreshPorts() }
                Button(model.connected ? "断开" : "连接") { model.toggleConnect() }
                    .buttonStyle(.borderedProminent)
                    .tint(model.connected ? .red : .accentColor)
                    .disabled(model.selectedPort.isEmpty)
                Circle().fill(model.connected ? .green : .gray).frame(width: 10, height: 10)
            }

            // 力反馈实时状态
            GroupBox("力反馈遥测 (HAPTIC,mode,pos,cur,roll,pitch,yaw)") {
                HStack(spacing: 24) {
                    VStack { Text("模式").font(.caption).foregroundStyle(.secondary); Text(model.lastMode).font(.title2).bold() }
                    VStack { Text("角度").font(.caption).foregroundStyle(.secondary); Text(String(format: "%.2f°", model.lastDeg)).font(.title2).monospacedDigit() }
                    VStack { Text("电流指令").font(.caption).foregroundStyle(.secondary); Text("\(model.lastCur)").font(.title2).monospacedDigit() }
                    VStack { Text("Roll").font(.caption).foregroundStyle(.secondary); Text(String(format: "%.1f°", model.lastRoll)).font(.title2).monospacedDigit() }
                    VStack { Text("Pitch").font(.caption).foregroundStyle(.secondary); Text(String(format: "%.1f°", model.lastPitch)).font(.title2).monospacedDigit() }
                    Spacer()
                    AngleChart(values: model.history)
                        .frame(height: 60)
                }
                .padding(6)
            }

            HStack {
                TextField("手动命令（TENSION,500 / SETCUR,20000 / MODE,EXT / MODE,LOCAL）", text: $model.manualCmd)
                    .textFieldStyle(.roundedBorder)
                    .onSubmit { model.sendManual() }
                Button("发送") { model.sendManual() }
                    .disabled(!model.connected)
            }

            LogView(text: $model.serialLog, placeholder: "串口输出…")
            HStack {
                Button("清空") { model.serialLog = "" }
                Spacer()
                Text("波特率 115200").font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    // MARK: 烧录
    var flashTab: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                TextField("固件工程目录（含 platformio.ini）", text: $model.projectDir)
                Button("选择…") { pickFolder() }
            }
            HStack {
                Picker("烧录串口", selection: $model.selectedPort) {
                    ForEach(model.ports) { Text($0.name).tag($0.path) }
                }
                .frame(maxWidth: 380)
                Button("刷新") { model.refreshPorts() }
            }
            GroupBox("固件变体（钓鱼 = 独立干净工程，上电即钓鱼 HUD）") {
                Picker("变体", selection: $model.variant) {
                    ForEach(FirmwareVariant.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
                .padding(6)
                .onChange(of: model.variant) { _ in model.syncVariantConfig() }
            }
            GroupBox("WiFi / OSC 同步（构建前自动写入固件）") {
                VStack(alignment: .leading, spacing: 8) {
                    HStack {
                        Text("SSID").frame(width: 52, alignment: .trailing)
                        TextField("WiFi 名称", text: $model.wifiSSID)
                        Button("读本机 WiFi") { model.detectWiFi() }
                    }
                    HStack {
                        Text("密码").frame(width: 52, alignment: .trailing)
                        TextField("WiFi 密码", text: $model.wifiPass)
                    }
                    HStack {
                        Text("目标 IP").frame(width: 52, alignment: .trailing)
                        TextField("运行 Unity 的电脑 IP", text: $model.targetIP)
                        Button("用本机 IP") { model.useLocalIP() }
                        Button("仅写入配置") { model.syncAllConfigs() }
                    }
                }
                .padding(6)
            }
            HStack(spacing: 12) {
                Button("构建 (pio run)") {
                    model.syncAllConfigs()
                    model.prepareFlash()
                    model.flasher.build(projectDir: model.projectDir)
                }
                Button("构建并烧录 (pio -t upload)") {
                    model.syncAllConfigs()
                    model.prepareFlash()
                    model.flasher.buildAndUpload(projectDir: model.projectDir, port: model.selectedPort)
                }
                .buttonStyle(.borderedProminent)
                .disabled(model.projectDir.isEmpty || model.selectedPort.isEmpty)
                Button("烧录已编译 .bin…") { pickBin() }
                Spacer()
                if model.busy { ProgressView().scaleEffect(0.7) }
            }
            .disabled(model.busy)

            LogView(text: $model.flashLog, placeholder: "构建 / 烧录日志…")
            HStack {
                Button("清空") { model.flashLog = "" }
                Spacer()
                Text("需要 PlatformIO：python3 -m pip install platformio").font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    private func pickFolder() {
        let p = NSOpenPanel()
        p.canChooseDirectories = true; p.canChooseFiles = false
        p.showsHiddenFiles = true   // Library 等不可见目录可选（也可 Cmd+Shift+G 直接输路径）
        if !model.projectDir.isEmpty { p.directoryURL = URL(fileURLWithPath: model.projectDir) }
        if p.runModal() == .OK, let url = p.url { model.projectDir = url.path }
    }

    private func pickBin() {
        let p = NSOpenPanel()
        p.allowedFileTypes = ["bin"]
        if p.runModal() == .OK, let url = p.url {
            model.prepareFlash()
            model.flasher.flashBin(port: model.selectedPort, binPath: url.path)
        }
    }
}

/// 滚动日志视图
struct LogView: View {
    @Binding var text: String
    let placeholder: String

    var body: some View {
        ScrollViewReader { proxy in
            ScrollView {
                Text(text.isEmpty ? placeholder : text)
                    .font(.system(size: 11, design: .monospaced))
                    .foregroundStyle(text.isEmpty ? .secondary : .primary)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .textSelection(.enabled)
                    .padding(8)
                    .id("bottom")
            }
            .background(Color(nsColor: .textBackgroundColor))
            .clipShape(RoundedRectangle(cornerRadius: 6))
            .overlay(RoundedRectangle(cornerRadius: 6).stroke(.quaternary))
            .onChange(of: text) { _ in proxy.scrollTo("bottom") }
        }
    }
}

/// 简易角度曲线（无 Charts 依赖，兼容 macOS 13）
struct AngleChart: View {
    let values: [Double]
    var body: some View {
        GeometryReader { geo in
            let lo = values.min() ?? -1, hi = values.max() ?? 1
            let span = max(hi - lo, 0.001)
            Path { p in
                guard values.count > 1 else { return }
                for (i, v) in values.enumerated() {
                    let x = geo.size.width * CGFloat(i) / CGFloat(values.count - 1)
                    let y = geo.size.height * (1 - CGFloat((v - lo) / span))
                    i == 0 ? p.move(to: CGPoint(x: x, y: y)) : p.addLine(to: CGPoint(x: x, y: y))
                }
            }
            .stroke(.cyan, lineWidth: 1.5)
        }
        .background(.black.opacity(0.05))
        .clipShape(RoundedRectangle(cornerRadius: 4))
    }
}
