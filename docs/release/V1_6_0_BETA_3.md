## 中文

言渡把现有 Android 手机变成电脑麦克风和快捷键面板，节省另买麦克风或带麦耳机的硬件费用。语音识别由你自选的电脑语音软件完成，软件费用单独计算。

本批是 **技术预览（Pre-release）**，包含 Android APK、Windows x64 接收端与托盘、Apple Silicon Mac 精简接收端。

### 这次更新

- 合入横屏系统栏/挖孔留白修复，避免 Samsung 横屏标题被状态栏遮挡。
- 统一中英文 README、支持矩阵、架构/构建说明与引擎设置指南，补上 LINUX DO 社区链接。
- 删除 Windows 托盘已无调用的二维码库，补齐第三方与运行时许可声明。
- 从固定源码重新构建三个平台，提供 `BUILD_PROVENANCE.json` 和 `SHA256SUMS.txt`，保留原 beta.2 字节。

### 下载与安装

| 文件 | 平台/内部版本 |
|---|---|
| `Yandu-v1.6.0-beta.3-android.apk` | Android 8.0+，1.6.0-dev.22 / code28 |
| `Yandu-v1.6.0-beta.3-windows-x64.zip` | Windows 10/11 x64；Server 与托盘 1.6.0-dev.17 / sequence29 |
| `Yandu-v1.6.0-beta.3-macos-arm64.zip` | macOS 14.2+，Apple Silicon；2.0.0-dev.4 / bundle3，Native AOT lite |

电脑 ZIP 内有 `INSTALL.txt`、许可证和第三方声明。Windows 自行安装 [VB-CABLE](https://vb-audio.com/Cable/)，Mac 自行安装 [BlackHole 2ch](https://github.com/ExistentialAudio/BlackHole)，在语音软件中选好虚拟麦克风。手机点“附近 · 点按连接”，核对四位校验码，电脑点允许。

APK 沿用 beta.2 开发机调试证书，可以覆盖同渠道 beta.2；将来的长期正式签名 APK 需要卸载重装并重新配对。证书 SHA-256：`ba44191dea3478d824a98ba3b1e0d3cf4e18a10090f5d33704dd8d4f9637061f`。

Windows 尚无代码签名，Mac 为 ad-hoc 签名、尚无苹果公证；Mac 更新需重新授权辅助功能。Mac ZIP 使用原生 ditto 保留签名元数据，并在解压后重新验证签名。Mac lite 不含实验 iPhone 网页网关。

### 验证与范围

[主线 CI](https://github.com/leolemon777/PhoneDeck/actions/runs/37605012066) 与 [网页兼容 CI](https://github.com/leolemon777/PhoneDeck/actions/runs/37605011947) 均通过：Windows 181 项、Mac 48 项接收端测试，Android 46 项单测/build/lint、60 项版本检查、协议契约与发布 staging/事务检查。分发包另外核对 APK 签名/版本、两个 EXE 的内置版本/源码提交、Mac bundle/架构及解压后的签名。

作者的 Samsung、两台 Windows、Apple Silicon Mac 已有核心听写、附近连接和三电脑切换记录；**本批没有自动安装到这些设备，也没有完成外部用户的干净安装认证**。多电脑同时共享、翻译/问答、其他机型、Intel 和长时间异常/升级矩阵仍待验证。见 [支持矩阵](https://github.com/leolemon777/PhoneDeck/blob/v1.6.0-beta.3/docs/release/SUPPORT_MATRIX.md) 与 [已知问题](https://github.com/leolemon777/PhoneDeck/blob/v1.6.0-beta.3/docs/release/KNOWN_ISSUES.md)。

源码：[5565aa8](https://github.com/leolemon777/PhoneDeck/tree/5565aa8c9258b2a5c1e145fe2036327b9b1be7eb)，经 [PR #50](https://github.com/leolemon777/PhoneDeck/pull/50) 合入 main。本批独立于签名三产物统一更新渠道，不发布本地 Whisper Desktop 草稿。

---

## English

Yandu turns an Android phone you already own into a computer microphone and shortcut pad, helping you avoid buying an extra microphone or headset just for voice input. Speech recognition is handled by your chosen dictation app on the computer; any software costs are separate.

This is a **technical preview (Pre-release)** with an Android APK, a Windows x64 receiver and tray app, and a slim receiver for Apple Silicon Macs.

### What's new

- Added landscape spacing for system bars and display cutouts, preventing the title from being covered by the status bar on Samsung phones.
- Updated the Chinese and English READMEs, support matrix, architecture and build documentation, and voice engine setup guide; added a link to the LINUX DO community.
- Removed the unused QR-code library from the Windows tray app and completed third-party and runtime license notices.
- Rebuilt all three platforms from a fixed source commit, with `BUILD_PROVENANCE.json` and `SHA256SUMS.txt`. The original beta.2 assets remain unchanged.

### Downloads and installation

| File | Platform / internal version |
|---|---|
| `Yandu-v1.6.0-beta.3-android.apk` | Android 8.0+; 1.6.0-dev.22 / versionCode 28 |
| `Yandu-v1.6.0-beta.3-windows-x64.zip` | Windows 10/11 x64; Server and tray app 1.6.0-dev.17 / release sequence 29 |
| `Yandu-v1.6.0-beta.3-macos-arm64.zip` | macOS 14.2+, Apple Silicon; 2.0.0-dev.4 / bundle version 3; Native AOT lite |

The computer ZIPs include `INSTALL.txt`, licenses, and third-party notices. Install [VB-CABLE](https://vb-audio.com/Cable/) on Windows or [BlackHole 2ch](https://github.com/ExistentialAudio/BlackHole) on Mac, then select the virtual microphone in your dictation app. On the phone, tap “附近 · 点按连接” (Nearby · tap to connect), check that the four-digit codes match, and click Allow on the computer.

The APK uses the same development-machine debug certificate as beta.2, so it can update beta.2 installations from that signing channel. A future APK with the long-term release signature will require uninstalling, reinstalling, and pairing again. Certificate SHA-256: `ba44191dea3478d824a98ba3b1e0d3cf4e18a10090f5d33704dd8d4f9637061f`.

The Windows apps are not code-signed. The Mac app is ad-hoc signed and is not notarized by Apple; re-grant Accessibility permission after updating. The Mac ZIP was created with native `ditto` to preserve signing metadata, and its signature was verified again after extraction. Mac lite does not include the experimental iPhone web gateway.

### Verification and scope

The [main CI](https://github.com/leolemon777/PhoneDeck/actions/runs/37605012066) and [web compatibility CI](https://github.com/leolemon777/PhoneDeck/actions/runs/37605011947) passed: 181 Windows receiver tests, 48 Mac receiver tests, 46 Android unit tests plus build and lint, 60 version checks, protocol contracts, and release staging/transaction checks. Distribution checks also covered the APK signature and version, embedded versions and source commits in both Windows EXEs, and the Mac bundle, architecture, and signature after extraction.

Previous device checks cover core dictation, nearby pairing, and switching between three computers with the author's Samsung phone, two Windows PCs, and an Apple Silicon Mac. **This batch was not automatically installed on those devices and has not completed clean-install validation by outside users.** Simultaneous microphone sharing across computers, translate/ask modes, other phones, Intel Macs, and extended failure-recovery/upgrade scenarios still need verification. See the [support matrix](https://github.com/leolemon777/PhoneDeck/blob/v1.6.0-beta.3/docs/release/SUPPORT_MATRIX.md) and [known issues](https://github.com/leolemon777/PhoneDeck/blob/v1.6.0-beta.3/docs/release/KNOWN_ISSUES.md).

Source: [5565aa8](https://github.com/leolemon777/PhoneDeck/tree/5565aa8c9258b2a5c1e145fe2036327b9b1be7eb), merged into main through [PR #50](https://github.com/leolemon777/PhoneDeck/pull/50). This batch is separate from the signed, three-artifact unified update channel. The local Whisper Desktop draft is not part of this release.
