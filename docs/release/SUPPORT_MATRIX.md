# 支持与验证矩阵

> 更新：2026-10-07，主线 v1.6.0-beta.3 技术预览。
> 「预览」表示在作者设备上验证了所列路径，不承诺全部组合；「实验」表示证据不足；「待验证」表示尚无对应真机结果。
> 本批重新构建产物的构建/版本/哈希检查，与之前设备的功能走查分别记录。尚未完成外部用户的干净安装认证。

## 客户端 × 接收端

| 组合 | 等级 | 已有证据与限制 |
|---|---|---|
| Android → Windows（USB） | 预览 | 历史 USB 配对/听写与 ADB 恢复记录；完整拔插/升级/失败矩阵仍待复测 |
| Android → Windows（Wi-Fi 附近确认） | 预览 | 两台 Windows 的四位校验码 + 点允许、听写/快捷键与切换走查；原生手机不再扫码 |
| Android → macOS（USB / Wi-Fi） | 预览 | Apple Silicon 原生精简版、BlackHole、辅助功能、附近确认、点击/按住听写与电脑停止同步关麦 |
| Android → Mac + 两台 Windows | 预览 | 同网段发现、三台目录、左右滑动确认切换、离线卡片；同时共享供音与长时间压力待验证 |
| 旧 Android → 迁移期共享令牌 | 兼容预览 | 旧令牌仍受支持，可在状态页关闭；不能据此声明逐手机授权/撤销已验证 |
| 原生 iPhone → 任意 | 未实现 | 没有 iOS 原生工程或 IPA |
| 手机网页 → Windows / Mac 完整版 | 实验 | 协议/PCM 自动化与浏览器台架；实际手机 TLS 信任、权限、锁屏等见 [TYPELESS_PWA](../guides/TYPELESS_PWA.md) |
| 新 Desktop 2.0 本地 Whisper | 独立实验路线 | 版本/构建/发布与主线分开，见 [CROSS_PLATFORM_EXECUTION](CROSS_PLATFORM_EXECUTION.md)；不据主线结果声明 Linux 或本地识别已验收 |

## Android

| 项 | 范围 |
|---|---|
| 构建下限 | minSdk 26（Android 8.0），targetSdk 35；没有逐系统版本认证 |
| 实测设备 | Samsung SM-G9880 / Android 12（SDK 31），目前唯一手机真机 |
| 已走查 | 点击/按住听写、停止联动、共享开关、深色待机/录音/共享、离线提示、横屏系统栏留白 |
| 待验证 | 其他品牌、后台/锁屏长时间采音、字体缩放/读屏、长时间共享与异常恢复 |
| beta 安装渠道 | `com.codex.phonedeck` 调试签名；beta.2 → beta.3 同签名升级。正式签名需卸载重装、重新配对 |

## Windows

| 项 | 范围 |
|---|---|
| 工具链 | .NET SDK 10.0.400（global.json）；self-contained win-x64，无需用户另装 .NET |
| 声明平台 | Windows 10/11 x64；Windows 10 未单独认证，ARM64 无主线分发包 |
| 实测机器 | 作者两台 Windows；核心听写、快捷键、附近允许与三电脑切换 |
| 输入法 | Typeless 核心听写预览；翻译/问答未逐项验证；豆包/微信/千问为实验档案 |
| 虚拟麦克风 | VB-CABLE，用户自行安装，不打包 |
| 待验证 | 高 DPI、开机启动、干净安装、完整升级/撤销/断线矩阵、实际资源占用 |
| 签名 | 尚无 Windows 代码签名 |

## macOS

| 项 | 范围 |
|---|---|
| 声明平台 | macOS 14.2+，公开主线包为 Apple Silicon arm64；Intel 可源码构建但未真机认证 |
| 运行时 | .NET 10 Native AOT 精简版；不含 iPhone 网页网关或旧二维码图片，完整版需单独构建 |
| 实测 | Apple Silicon：BlackHole 2ch、CGEvent/辅助功能、听写、状态页、USB/Wi-Fi 与附近允许 |
| 待验证 | 翻译/问答、Intel、完整睡眠/重启/升级权限矩阵、长时间共享 |
| 签名/公证 | ad-hoc 签名，尚无 Developer ID/苹果公证；更新需重新授权辅助功能 |

## 传输与维护

| 项 | 范围 |
|---|---|
| 同网段 LAN | UDP + mDNS 已实现，同网段自动发现已实测；mDNS 单独成功仍待第二设备隔离验证 |
| 手动地址 | 可绕开发现层，仍需私网可达、固定证书与本机确认；无法绕过 AP 隔离 |
| USB | ADB reverse 只指向当前 USB 主机 |
| 蓝牙 | 历史快捷键实验，不传音频 |
| IPv6-only / 跨网段 / VPN | 没有完整认证；广播/mDNS 不跨子网 |
| 第三方声明 | [THIRD_PARTY_NOTICES](THIRD_PARTY_NOTICES.md)；主线包附带许可证与声明 |
| 稳定支持 / SLA | 未承诺；只有所列技术预览范围 |
| 已知限制 | [KNOWN_ISSUES](KNOWN_ISSUES.md)，验证时间线见 [HANDOFF](../HANDOFF.md) |
