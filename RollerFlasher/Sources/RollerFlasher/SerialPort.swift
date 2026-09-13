import Foundation

/// 极简 POSIX 串口读写（无第三方依赖）。
/// 枚举 /dev/cu.*，过滤常见 USB-Serial 芯片命名。
final class SerialPort {
    struct PortInfo: Hashable, Identifiable {
        var id: String { path }
        let path: String   // /dev/cu.xxx
        let name: String   // 展示名
    }

    static func listPorts() -> [PortInfo] {
        let prefixes = ["cu.usbmodem", "cu.usbserial", "cu.wchusbserial", "cu.SLAB_USBtoUART", "cu.debug-console"]
        guard let items = try? FileManager.default.contentsOfDirectory(atPath: "/dev") else { return [] }
        return items
            .filter { name in prefixes.contains(where: { name.hasPrefix($0) }) }
            .sorted()
            .map { PortInfo(path: "/dev/\($0)", name: $0) }
    }

    private var fd: Int32 = -1
    private var reader: Thread?

    var onReceive: ((String) -> Void)?
    var onClose: (() -> Void)?

    var isOpen: Bool { fd >= 0 }

    func open(path: String, baud: Int32 = 115200) -> Bool {
        close()
        fd = Darwin.open(path, O_RDWR | O_NOCTTY | O_NONBLOCK)
        guard fd >= 0 else { return false }

        var tio = termios()
        tcgetattr(fd, &tio)
        cfmakeraw(&tio)
        let speed: speed_t
        switch baud {
        case 9600: speed = speed_t(B9600)
        case 19200: speed = speed_t(B19200)
        case 57600: speed = speed_t(B57600)
        case 230400: speed = speed_t(B230400)
        default: speed = speed_t(B115200)
        }
        cfsetispeed(&tio, speed)
        cfsetospeed(&tio, speed)
        tio.c_cflag |= UInt(CLOCAL | CREAD)
        tio.c_cc.16 = 1   // VMIN
        tio.c_cc.17 = 0   // VTIME
        guard tcsetattr(fd, TCSANOW, &tio) == 0 else {
            Darwin.close(fd); fd = -1; return false
        }

        let reader = Thread { [weak self] in self?.readLoop() }
        reader.qualityOfService = .userInitiated
        self.reader = reader
        reader.start()
        return true
    }

    func send(_ text: String) {
        guard fd >= 0, let data = text.data(using: .utf8) else { return }
        data.withUnsafeBytes { ptr in
            _ = Darwin.write(fd, ptr.baseAddress, data.count)
        }
    }

    func close() {
        if fd >= 0 {
            Darwin.close(fd)
            fd = -1
        }
    }

    private func readLoop() {
        var buf = [UInt8](repeating: 0, count: 4096)
        while isOpen {
            let n = Darwin.read(fd, &buf, buf.count)
            if n > 0 {
                let str = String(decoding: buf[0..<n], as: UTF8.self)
                let cb = onReceive
                DispatchQueue.main.async { cb?(str) }
            } else {
                Thread.sleep(forTimeInterval: 0.02)
            }
        }
        let cb = onClose
        DispatchQueue.main.async { cb?() }
    }

    deinit { close() }
}
