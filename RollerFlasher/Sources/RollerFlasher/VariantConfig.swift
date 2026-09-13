import Foundation

/// 固件变体：钓鱼模拟器 = 独立干净固件工程 firmware-fishing（上电即钓鱼 HUD，
/// 无本地模式/按键切模式）；标准力反馈 = firmware 工程（本地 4 模式 + 串口 EXT）。
/// 旧机制（variant_config.h + FISHING_DEFAULT_EXT）已废弃：两个工程各自独立，
/// 构建前只需写 wifi_config.h。
enum FirmwareVariant: String, CaseIterable, Identifiable {
    case fishing    // 钓鱼模拟器（默认）：firmware-fishing 工程
    case standard   // 标准力反馈：firmware 工程

    var id: String { rawValue }
    var title: String {
        switch self {
        case .fishing:  return "钓鱼模拟器（默认）"
        case .standard: return "标准力反馈"
        }
    }
    /// bundle Resources / App Support 工作副本 / 源码树里的工程目录名
    var projectDirName: String {
        switch self {
        case .fishing:  return "firmware-fishing"
        case .standard: return "firmware"
        }
    }
}

enum VariantConfigSync {

    /// 清理旧机制残留：标准工程 src/variant_config.h（存在则说明是旧版钓鱼变体
    /// 写的，会让标准固件上电进 EXT——新版两工程独立，这个头文件一律删掉）。
    @discardableResult
    static func cleanLegacyHeader(projectDir: String) -> String {
        let path = (projectDir as NSString)
            .appendingPathComponent("src/variant_config.h")
        if FileManager.default.fileExists(atPath: path) {
            try? FileManager.default.removeItem(atPath: path)
            return "已清理旧版 variant_config.h 残留"
        }
        return "无旧版 variant_config.h 残留"
    }
}
