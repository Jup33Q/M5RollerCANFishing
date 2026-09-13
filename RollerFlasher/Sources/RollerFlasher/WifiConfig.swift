import Foundation

/// WiFi / OSC 目标配置同步：把 SSID / 密码 / Unity 主机 IP 写成固件的
/// `src/wifi_config.h`（固件 main.cpp 用 `__has_include` 自动包含，缺省回退内置默认值）。
enum WifiConfigSync {

    /// 生成并写入 <projectDir>/src/wifi_config.h，返回写入的文件路径。
    @discardableResult
    static func writeHeader(projectDir: String, ssid: String, pass: String, targetIP: String) throws -> String {
        let src = (projectDir as NSString).appendingPathComponent("src")
        try FileManager.default.createDirectory(atPath: src, withIntermediateDirectories: true)
        let path = (src as NSString).appendingPathComponent("wifi_config.h")
        let body = """
        // 本文件由 RollerFlasher.app「同步 WiFi」自动生成，请勿手改（会被覆盖）。
        // 生成时间：\(Date())
        #pragma once
        #define CFG_WIFI_SSID "\(escape(ssid))"
        #define CFG_WIFI_PASS "\(escape(pass))"
        #define CFG_TARGET_IP "\(escape(targetIP))"

        """
        try body.write(toFile: path, atomically: true, encoding: .utf8)
        return path
    }

    /// C 字符串转义（引号、反斜杠、控制字符）
    private static func escape(_ s: String) -> String {
        var out = ""
        for ch in s {
            switch ch {
            case "\"": out += "\\\""
            case "\\": out += "\\\\"
            case "\n": out += "\\n"
            case "\r": out += "\\r"
            default: out.append(ch)
            }
        }
        return out
    }

    /// 读取 Mac 当前 WiFi 名称。macOS 14+ 对无定位权限的进程会返回 <redacted>，此时返回 nil。
    static func currentWiFiSSID() -> String? {
        for iface in ["en0", "en1"] {
            let p = Process()
            let pipe = Pipe()
            p.executableURL = URL(fileURLWithPath: "/usr/sbin/networksetup")
            p.arguments = ["-getairportnetwork", iface]
            p.standardOutput = pipe
            p.standardError = FileHandle.nullDevice
            guard let _ = try? p.run() else { continue }
            let data = pipe.fileHandleForReading.readDataToEndOfFile()
            p.waitUntilExit()
            guard let out = String(data: data, encoding: .utf8) else { continue }
            // 形如 "Current Wi-Fi Network: <WIFI_SSID>\n"
            if let range = out.range(of: "Current Wi-Fi Network: ") {
                let ssid = String(out[range.upperBound...]).trimmingCharacters(in: .whitespacesAndNewlines)
                if !ssid.isEmpty, !ssid.contains("redacted") { return ssid }
            }
        }
        return nil
    }

    /// Mac 主用 IPv4（en0 优先），作为 OSC 目标 IP 的默认猜测。
    static func primaryIPv4() -> String? {
        var addrList: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&addrList) == 0, let first = addrList else { return nil }
        defer { freeifaddrs(addrList) }
        var found: [String: String] = [:]
        for ifa in sequence(first: first, next: { $0.pointee.ifa_next }) {
            let flags = Int32(ifa.pointee.ifa_flags)
            guard (flags & IFF_UP) != 0, (flags & IFF_LOOPBACK) == 0,
                  let addr = ifa.pointee.ifa_addr, addr.pointee.sa_family == UInt8(AF_INET)
            else { continue }
            var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            let saLen = socklen_t(addr.pointee.sa_len)
            guard getnameinfo(addr, saLen, &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST) == 0
            else { continue }
            found[String(cString: ifa.pointee.ifa_name)] = String(cString: host)
        }
        return found["en0"] ?? found["en1"] ?? found.values.first
    }
}
