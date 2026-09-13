// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "RollerFlasher",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(name: "RollerFlasher")
    ]
)
