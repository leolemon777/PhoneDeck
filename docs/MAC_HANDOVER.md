# 带到 Mac 继续开发

本项目当前可验证的重点为 Windows x64、Android 与 macOS 预览接收端；iOS 尚无客户端工程。
2026-09-14 已在一台 Apple Silicon Mac 与 Samsung 上完成 Wi-Fi 输入和 Typeless 听写最小闭环，
但这不自动补齐其他 Windows/Mac、连续断线、三机共享、Intel 或 iOS 的实机验收。

## 搬哪些文件

使用交付目录中的源码 ZIP，或从 GitHub 检出交付说明指定的分支/提交。不要直接复制整个开发工作区：其中的 outputs、工具链、运行中的电脑控制台和个人配置不属于源码。

源码包不含签名私钥、APK 签名库、个人 data、ADB 密钥、构建缓存、旧二进制。不要删除或公开原机器上的这些运行数据；如需延续原签名渠道，签名材料必须单独安全管理。

## Mac 开发入口

1. 阅读根目录 README.md、AGENTS.md、spec plan.markdown 和 docs/HANDOFF.md。
2. 按 global.json 安装对应 .NET SDK，准备 PowerShell 7；Android 按 docs/BUILD_PIPELINE.md 准备 JDK 17、SDK 35。
3. 在仓库根目录执行 `pwsh -NoProfile -File ./build.ps1 -Platform MacOS` 构建 Mac 接收端；执行 `pwsh -NoProfile -File ./build.ps1 -Platform Android` 构建安卓。
4. Windows 的 WPF/原生功能通过 Windows CI 构建与测试；实际输入、共享麦克风和安装更新仍需 Windows 设备。不要把 Mac 上编辑 C# 等同于 Windows 真机通过。
5. Mac 真机阶段先用已有安卓客户端，验证辅助功能权限、BlackHole 音频路径、语音引擎与连接恢复。iPhone 需另外开发原生客户端。

## 当前发行边界

STAGING ZIP 是可检查的开发候选，不是已有设备认可的签名更新 ZIP。旧电脑首次接入与正式更新的区别见 docs/FLEET_UPDATES.md。安卓覆盖安装必须使用与已安装应用相同的签名渠道，不能通过卸载清数据掩盖不兼容。

需要继续完成：可信构建来源和正式签名事务接入、现有两台 Windows 与安卓的逐设备更新/回退/尾音验收、Mac 翻译/问答/连续断线与三机压力验收。项目中的长远四端规划保持有效，未实现项不得标记已支持。
