# 第三方依赖与声明（B01/B02 维护项）

> 首次建立：2026-09-30。用途：规格 DOC-04/REL-11 的第三方声明基线——开源发布前
> 每次新增依赖必须同步本清单；SBOM 自动化（REL-11）未建前以本文件为人工基线。
> 许可结论：全部与项目 MIT 许可兼容（MIT/Apache-2.0 均允许再分发与商用，
> 需保留版权与许可声明——发布包中应附带本清单）。

## Windows 接收端（PhoneDeck.Server，.NET 10）

| 包 | 版本 | 许可 | 用途 | 引入 |
|---|---|---|---|---|
| NAudio | 2.2.1 | MIT | WASAPI 虚拟麦克风播放（VB-CABLE 供音） | 历史基线 |
| Makaretu.Dns | 2.0.1 | MIT | DNS 记录/服务发现协议模型（mDNS TXT/SRV） | M1-A A5（2026-09-30） |
| Makaretu.Dns.Multicast | 0.27.0 | MIT | 多播 mDNS 广播（_phonedeck._tcp 发布） | M1-A A5（2026-09-30） |

## Windows 托盘（PhoneDeck.ControlCenter，.NET 10 WinForms）

| 包 | 版本 | 许可 | 用途 | 引入 |
|---|---|---|---|---|
| QRCoder | 1.6.0 | MIT | 配对二维码 PNG 渲染（仅本机 UI） | M1-A A2（2026-09-29） |

## 测试依赖（不进发布包）

| 包 | 版本 | 许可 | 范围 |
|---|---|---|---|
| MSTest.TestAdapter/TestFramework | 4.3.3 | MIT | Server.Tests / Receiver.Tests |
| MSTest.TestAdapter/TestFramework（ControlCenter 测试） | — | MIT | 未建测试工程（历史） |
| junit | 4.13.2 | EPL-1.0 | Android 单测（testImplementation，不入 APK） |

EPL-1.0 仅用于测试类路径，不分发进任何产物，无传染性影响。

## Android（app）

| 依赖 | 版本 | 许可 | 用途 | 引入 |
|---|---|---|---|---|
| com.journeyapps:zxing-android-embedded | 4.3.0 | Apache-2.0 | 扫码配对（CaptureActivity + IntentIntegrator；包名注意：`com.google.zxing.integration.android.IntentIntegrator`） | M1-A A3（2026-09-29） |
| └ com.google.zxing:core（传递） | 3.4.1 | Apache-2.0 | 二维码解码核心 | 同上 |

## 平台自带（无第三方许可负担）

- Android：org.json、NsdManager、Keystore 等系统 API（系统许可覆盖）。
- Windows：System.Text.Json、SendInput/WinForms 等 BCL/OS API（.NET MIT 与 Windows EULA）。
- 自绘资产：DeckIconView/LinearIcon 系列为项目原创线性图形，无第三方字体/图片依赖
  （UI_REFRESH.md 约束维持）。

## 维护规则

1. 新增 PackageReference/implementation 时同步本清单（版本/许可/用途/引入批次）。
2. 许可必须与 MIT 兼容；GPL/AGPL 类需用户决策后引入。
3. 升级依赖时核对许可是否变更（尤其 Makaretu 系小版本）。
4. 发布打包（B03）时应把本清单随 Release 附带（DOC-04 的"第三方责任清楚"项）。
