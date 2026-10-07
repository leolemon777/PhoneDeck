# 主线第三方依赖与声明

> 更新：2026-10-07（v1.6.0-beta.3）。版本依据项目引用与 packages.lock.json；许可证依据 NuGet 元数据和上游许可文本。
> 本清单覆盖主线 Android、Windows 和 macOS。独立本地 Whisper Desktop 的依赖见 [DESKTOP_THIRD_PARTY](DESKTOP_THIRD_PARTY.md)。
> 许可文本保存在 [licenses](licenses)，来源与哈希见 [sources.json](licenses/sources.json)。发布包附带本清单及许可文本；自包含 .NET 包另外附带运行时原有许可/第三方声明。

## 接收端直接依赖

| 包 | 版本 | 许可 | 分发范围与用途 |
|---|---|---|---|
| NAudio | 2.2.1 | MIT | Windows WASAPI / VB-CABLE 音频输出；包含 NAudio.Core、Wasapi、WinMM、Midi、Asio 同版本组件 |
| Makaretu.Dns | 2.0.1 | MIT | Windows/Mac，共用 DNS 记录与发现模型 |
| Makaretu.Dns.Multicast | 0.27.0 | MIT | Windows/Mac，共用 mDNS 广播 |
| QRCoder | 1.6.0 | MIT | Windows Server 与 Mac 完整版的实验网页/旧配对图片；Mac lite 排除。托盘扫码 UI 已删除，beta.3 同步移除其引用 |

## 接收端传递依赖

| 包 | 版本 | 许可 | 来源 |
|---|---|---|---|
| Common.Logging / Common.Logging.Core | 3.4.1 | Apache-2.0 | Makaretu 系依赖；[许可](licenses/Common.Logging.txt) |
| IPNetwork2 | 2.1.2 | BSD-2-Clause | Makaretu 系依赖；[许可](licenses/IPNetwork2.txt) |
| SimpleBase | 1.3.1 | Apache-2.0 | DNS 编码依赖；[许可](licenses/SimpleBase.txt) |
| Tmds.LibC | 0.2.0 | MIT | mDNS 平台依赖；[许可](licenses/Tmds.LibC.txt) |
| NETStandard.Library / Microsoft / System 运行时组件 | 版本以各项目锁与选定 SDK 为准 | 各组件许可 | 自包含分发保留 .NET Core、ASP.NET Core、Windows Desktop 的原始 LICENSE / THIRD-PARTY-NOTICES；不把系统或传递组件一律称为本项目 MIT |

## Android 与测试

Android 主线当前没有第三方运行时 implementation 依赖。扫码客户端与 zxing 已在 2026-10-06 删除。
org.json、NsdManager、Keystore 等为系统 API；系统运行库不会作为本项目独立打包依赖。

| 测试包 | 版本 | 许可 | 范围 |
|---|---|---|---|
| MSTest.TestAdapter / TestFramework | 4.3.3 | MIT | Server / Receiver 测试，不进入用户包 |
| junit | 4.13.2 | EPL-1.0 | Android testImplementation，不进入 APK |

## 自绘资产与外部软件

- DeckIconView、LinearIcon、话筒图标和界面主题为项目自绘资产，没有第三方字体/图片依赖。
- Typeless、豆包、微信、千问、VB-CABLE 与 BlackHole 由用户另行安装，不随言渡打包；其商标、许可与隐私条款由各自所有者管理。

## 维护

新增或升级引用时，同时审查锁、许可版本、用途及发布包中的声明。不要只列直接依赖或把开发工具/测试库误写成 APK 运行时依赖。
本文件是人工盘点；完整自动 SBOM 与正式发行签名仍待独立完成。
