import Foundation

/// 封装外部命令：`pio`(PlatformIO) 构建、直接调用 pio 上传，或 esptool 烧录已有 .bin。
/// 依赖：python3 -m pip install platformio  （pip 会同时装 esptool）
enum FlashError: Error, LocalizedError {
    case toolNotFound(String)
    var errorDescription: String? {
        switch self {
        case .toolNotFound(let t): return "找不到工具：\(t)，请先 `python3 -m pip install platformio`"
        }
    }
}

final class Flasher {
    var onLog: ((String) -> Void)?

    /// 在 PATH 常见位置查找可执行文件
    static func which(_ name: String) -> String? {
        let candidates = [
            "/opt/homebrew/bin/\(name)",
            "/usr/local/bin/\(name)",
            "\(NSHomeDirectory())/.local/bin/\(name)",
            "\(NSHomeDirectory())/.platformio/penv/bin/\(name)",
            // 本机 Kimi Work 内置 Python 环境（pip install platformio 的目标）
            "\(NSHomeDirectory())/Library/Application Support/kimi-desktop/daimon-share/daimon/runtime/python/.venv/bin/\(name)",
        ]
        for c in candidates where FileManager.default.isExecutableFile(atPath: c) { return c }
        // 兜底：env PATH
        if let path = ProcessInfo.processInfo.environment["PATH"] {
            for dir in path.split(separator: ":") {
                let p = "\(dir)/\(name)"
                if FileManager.default.isExecutableFile(atPath: p) { return p }
            }
        }
        return nil
    }

    /// 构建 + 上传固件（PlatformIO 一条命令搞定烧录）
    func buildAndUpload(projectDir: String, port: String, env: String = "m5stack-cores3") {
        run(stream: true, launch: { [weak self] () throws -> Process in
            guard let pio = Flasher.which("pio") ?? Flasher.which("platformio") else {
                throw FlashError.toolNotFound("pio")
            }
            let p = Process()
            p.executableURL = URL(fileURLWithPath: pio)
            p.arguments = ["run", "-d", projectDir, "-e", env, "-t", "upload", "--upload-port", port]
            self?.onLog?("$ pio run -e \(env) -t upload --upload-port \(port)\n")
            return p
        })
    }

    /// 仅构建
    func build(projectDir: String, env: String = "m5stack-cores3") {
        run(stream: true, launch: { [weak self] () throws -> Process in
            guard let pio = Flasher.which("pio") ?? Flasher.which("platformio") else {
                throw FlashError.toolNotFound("pio")
            }
            let p = Process()
            p.executableURL = URL(fileURLWithPath: pio)
            p.arguments = ["run", "-d", projectDir, "-e", env]
            self?.onLog?("$ pio run -e \(env)\n")
            return p
        })
    }

    /// 用 esptool 烧录已编译的 .bin（完整镜像，含 bootloader 时需配合 pio；此处用于 0x0 整包）
    func flashBin(port: String, binPath: String, offset: String = "0x0") {
        run(stream: true, launch: { [weak self] () throws -> Process in
            guard let esp = Flasher.which("esptool.py") ?? Flasher.which("esptool") else {
                throw FlashError.toolNotFound("esptool")
            }
            let p = Process()
            p.executableURL = URL(fileURLWithPath: esp)
            p.arguments = ["--chip", "esp32s3", "--port", port, "--baud", "1500000",
                           "write_flash", "-z", offset, binPath]
            self?.onLog?("$ esptool --chip esp32s3 -p \(port) write_flash \(offset) \(binPath)\n")
            return p
        })
    }

    private func run(stream: Bool, launch: @escaping () throws -> Process) {
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            do {
                let proc = try launch()
                let pipe = Pipe()
                proc.standardOutput = pipe
                proc.standardError = pipe
                pipe.fileHandleForReading.readabilityHandler = { h in
                    let data = h.availableData
                    guard !data.isEmpty, let s = String(data: data, encoding: .utf8) else { return }
                    DispatchQueue.main.async { self?.onLog?(s) }
                }
                try proc.run()
                proc.waitUntilExit()
                pipe.fileHandleForReading.readabilityHandler = nil
                DispatchQueue.main.async {
                    self?.onLog?("\n[退出码 \(proc.terminationStatus)]\(proc.terminationStatus == 0 ? " ✅ 完成" : " ❌ 失败")\n")
                }
            } catch {
                DispatchQueue.main.async { self?.onLog?("\n❌ \(error.localizedDescription)\n") }
            }
        }
    }
}
