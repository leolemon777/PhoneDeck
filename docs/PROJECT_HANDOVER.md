# PhoneDeck 项目交接文档

> 更新时间：2026-09-08 · 分支 `main` · 最新提交 `bf00bb2`
> 适用读者：接手开发的人、开源社区贡献者、以及未来的项目所有者本人。
> 本文档是全项目一页式总览；细节按"文档地图"一节深入。

---

## 1. 项目是什么

**PhoneDeck 把一台闲置 Android 手机变成电脑的语音输入面板和可编程快捷键控制台。**

- 手机麦克风采集声音（48 kHz / PCM16 / 单声道）；
- 电脑端的语音输入软件负责转文字（默认 Typeless，可换豆包、微信输入法等，档案化适配）；
- 手机还能发送复制/粘贴/截图/F1 等快捷键、宏和文本；
- 长期目标：一台手机管理多台 Windows / macOS 电脑，手机上明确切换输入目标。

三个组成部分：

| 端 | 技术栈 | 版本 | 状态 |
|---|---|---|---|
| Android App | 纯 Java（无 Kotlin），minSdk 26，程序化 UI | 1.6.0-dev.13 (versionCode 19) | 日常在用 |
| Windows 接收端 + 控制台 | .NET 8 / Kestrel / NAudio / SendInput + WPF 控制台 | 1.6.0-dev.8 | 日常在用 |
| macOS 接收端 | .NET 8 / CGEvent / Core Audio AUHAL | 2.0.0-dev.3 | 预览（未真机验收） |

上一实机稳定基线：**1.4.0**（1.5/1.6 dev 系列均通过构建与大部分真机验证，但未宣布稳定）。
规格基线：`spec plan.markdown` v0.4。通信协议 v2（兼容 1.4.0 固定动作）。

## 2. 仓库与工作区布局（重要）

真实仓库与部署**都在** `E:\Users\Administrator\Desktop\PhoneDeck开发工作区\` 下：

```
PhoneDeck开发工作区\
├── PhoneDeck手机键盘项目\        ← Git 仓库本体（唯一，main 分支）
│   ├── README.md  AGENTS.md  spec plan.markdown  implementation-notes.md
│   ├── docs\                     ← 全部文档（见 §10 文档地图）
│   ├── design\                   ← 控制台 UI 设计稿 + 图标
│   ├── scripts\                  ← windows/*.ps1、macos/Build-PhoneDeckReceiver.sh
│   └── work\phone-deck\
│       ├── android\              ← App 源码（app/src/main/java/com/codex/phonedeck，26 个 Java 文件）
│       ├── windows\              ← PhoneDeck.Server / PhoneDeck.ControlCenter / .Tests
│       ├── macos\                ← PhoneDeck.Receiver / .Tests
│       └── dist\ artifacts\      ← 历史发布包归档（非活源码）
├── PhoneDeck电脑控制台\           ← 运行/部署目录（EXE + dev 版 APK + data\ 配对凭据 + platform-tools）
└── artifacts\                    ← 构建产物与截图
```

注意：
- 桌面直接那个 `Desktop\PhoneDeck手机键盘项目` 是旧空壳（2026-09-08 已清空内容，空壳待会话结束后删除），**不是仓库**；
- `PhoneDeck电脑控制台\data\` 存着配对证书、令牌与电脑身份，**升级部署时只能换 EXE，不能动 data**；
- 手机端包名 `com.codex.phonedeck`，不要清 App 数据/配对记录。

## 3. 通信协议与传输层

三条可替换传输层，HTTP/JSON 语义统一，自动降级 Wi-Fi → USB → 蓝牙：

| 通道 | 地址 | 鉴权 | 用途 |
|---|---|---|---|
| Wi-Fi | 证书固定 HTTPS，端口 8766 | `X-PhoneDeck-Token`（首次配对必须经 USB `/api/lan/pair`，仅 loopback 来源） | 全功能 |
| USB | `adb reverse tcp:8765`，仅监听 `127.0.0.1:8765` | 无（本机回环） | 全功能 + 首次配对 |
| 蓝牙 | RFCOMM | 配对设备 | 仅快捷键 JSON + ACK，不传音频 |

发现：UDP 8767 广播魔术串（无密钥，只回 computerId/名称/端口；发现结果仍需 HTTPS 校验）。

主要端点（协议 v2 信封：`protocolVersion/requestId/sessionId/targetComputerId`，requestId 30 秒去重幂等）：

- `POST /api/input` — `fixedAction`（copy/paste/screenshot…遗留 `typeless`）/ `keyChord`（1–4 键白名单 + holdMs）/ `macro`（1–8 步）/ `text`
- `POST /api/audio/stream` — chunked PCM 流（`X-PhoneDeck-Audio-Mode: managed|shared`）
- `POST /api/dictation/start|stop` — 受管听写会话（mode 按当前引擎）
- `GET /api/health` / `GET /api/diagnostics` — 健康快照（后台 3 s 刷新）/ 深诊断
- `GET|POST /api/config/agent-shortcuts`、`GET|POST /api/config/voice-engines` — 配置读写
- `POST /api/shared/request` — 共享麦克风联动开关（Ctrl+Alt+M 热键写入，手机轮询跟随）

## 4. 两条语音工作模式（互斥）

**手机控制听写（managed）**：手机按钮触发——手机并行开录 + 发 PCM + 调 `/api/dictation/start`；
服务端把音频灌入虚拟声卡（VB-CABLE / BlackHole 2ch），按引擎档案合成引擎全局快捷键，
用 Core Audio 会话枚举确认引擎真的在录音后放行 pre-roll（保首音节）。停止时先等音频
排空再停引擎；断流自动复位。启动看门狗 6 秒。

**共享麦克风（shared）**：手机前台服务持续向所有在线电脑扇出同一 `sessionId` 音频，
**不控制任何引擎**——每台电脑用自己的快捷键触发本机语音软件。电脑端 Ctrl+Alt+M
（或控制台开关）→ 持久化 `sharedRequested` → 手机轮询自动跟随，重启仍有效。

## 5. 语音引擎适配层（2026-09-07 dev.13 重点）

开源多引擎支持，不绑定 Typeless。**引擎档案 JSON** 描述每个软件：进程名（录音探测，
包含匹配）、各模式快捷键与触发方式（`toggle` 按一下 / `hold` 按住说话）、可选配置读取器。

- 内置：`typeless`（配置自动读取，唯一真机验证）、`doubao`（实验）、`wetype` 微信输入法（实验，hold，需手动配快捷键）；
- 用户在 `data\voice-engines\*.json` 按 id 覆盖/新增（零代码）；`voice-engine-settings.json` 选激活引擎 + 最高优先级快捷键覆盖；
- hold 语义：开始按下保持、结束释放，**任何异常路径都释放按键**（探针失败/断流/退出清理），重复释放幂等；
- 虚拟声卡校验三态：null = 引擎无可读配置不阻断（仅 Typeless 硬校验）；
- health 双块兼容：旧 `typeless` 块从当前引擎映射（旧手机端不受影响）+ 新 `voiceEngine` 块（id/displayName/modes[]/trigger/configured），Android 优先读新块、动态渲染模式 chips 与引擎名；
- 控制台设置页"语音引擎"卡：引擎切换 + 快捷键覆盖，保存即重启接收端。

完整 Schema、示例档案（千问/微信客户端/doubao-murmur）、新引擎核对清单：**[docs/VOICE_ENGINES.md](./VOICE_ENGINES.md)**。

## 6. Android 端要点

- 纯 Java、程序化 UI（无 XML 布局）、26 个源文件；9 套主题（冰川玻璃默认 + 纸卡×4 + 四款设计稿风格）；
- `MainActivity`（约 3000 行）：语音状态机、连接选择、健康轮询（USB 1 s / LAN 5 s）、反向同步（电脑端完成的会话手机自动停止）；
- `AudioStreamer`：先开录后建连，TLS 握手期间最多 1 s pre-roll 环形暂存保首音节；
- `PhoneAudioService` + `SharedAudioBroadcaster`：共享模式前台服务，每电脑独立有界队列 + 指数退避；
- 快捷键：`ShortcutConfigRepository` → `files/shortcut-config.json`（AtomicFile，schemaVersion=1，损坏自动备份回退，可导入导出）；
- 配对信息：`TargetDeviceManager` → `known_devices`（computerId + 令牌 + 证书指纹为身份，IP 仅缓存，最多 8 台）；
- prefs：`voice_work_mode` / `voice_mode`（tap/hold）/ `voice_engine_mode`（旧键 `voice_typeless_mode` 自动迁移）/ `keep_connection_alive`。

## 7. 构建与开发环境（两台电脑的分工）

| 任务 | 在哪做 | 怎么做 |
|---|---|---|
| Windows 构建与单测 | 1 号机（本机） | `dotnet build` / `dotnet test`（.NET SDK 10 已装） |
| macOS 接收端编译自查 | 1 号机 | `dotnet build`（net8.0 交叉编译通过；真机行为需 Mac 验证） |
| Android 语法自查 | 1 号机 | `javac -source 11 -cp E:\PhoneDeck-build\android-sdk\platforms\android-35\android.jar`（26 个源文件；JDK 在 `E:\PhoneDeck-build\jdk-21...`） |
| **Android 完整构建（APK/签名/lint）** | **2 号机** | 1 号机无 Android SDK |
| 部署打包 | 1 号机 | 单文件 EXE + 便携 data 目录；见 `scripts/` 与历史 artifacts |

常用脚本：`Enable-PhoneDeckLan.ps1`（防火墙）、`Pair-PhoneDeckPhone.ps1`（adb reverse 配对）、
`AutoReconnectUsb.ps1`、`Install-PhoneDeckAutostart.ps1`（登录自启）、`一键恢复PhoneDeck.cmd`、
macOS `Build-PhoneDeckReceiver.sh`（打 .app）。

## 8. 测试现状

- Windows：**61 项全绿**（`PhoneDeck.Server.Tests`；含引擎档案解析/合并/hold 语义/会话状态机/pre-roll）；
- macOS：**20 项全绿**（交叉编译后本机运行；Core Audio/CGEvent 相关仅语法与逻辑层）；
- Android：javac 全量自查通过；**assembleDebug 与真机验收须 2 号机**；
- 真机验证历史：20+ 轮（详见 HANDOFF.md 各条目）；1 号机装有 Typeless 可做回归。

## 9. 未完成事项与风险（交接时必读）

1. **dev.13/dev.8/dev.3 均未部署**：运行目录 `PhoneDeck电脑控制台` 还是旧版（Windows dev.7 / Android dev.12）。部署时**只换 EXE/APK，绝不动 data**。
2. **豆包/微信输入法未实装验收**（所有者决定不安装不测试）：档案标注 experimental，靠 `docs/VOICE_ENGINES.md` 核对清单 + 社区回馈修正。
3. **Typeless 回归未做**：引擎改造后（协议双块、接口重构）建议下次实机听写时观察；单测已覆盖逻辑层。
4. **macOS 端整体仍是预览**：无 Mac 实机验收（Core Audio/BlackHole/权限流程）。
5. 开源发布打包（LICENSE 确认、issue 模板、发布流程）尚未开始；仓库不打包 Typeless/VB-CABLE/BlackHole 等第三方软件。
6. 共享麦克风"单电脑活跃"假设：电脑关机即停、热键暂固定 Ctrl+Alt+M（用户约束）。

## 10. 文档地图

| 文档 | 内容 |
|---|---|
| `README.md` | 项目总述、功能清单、版本状态 |
| `spec plan.markdown` | 产品规格 v0.4（**改代码前必读**） |
| `AGENTS.md` | Agent 协作规范与不可破坏约束 |
| `docs/ARCHITECTURE.md` | 数据流图与各文件职责 |
| `docs/SETUP.md` / `MACOS_SETUP.md` | 两平台安装步骤 |
| `docs/VOICE_ENGINES.md` | 语音引擎档案 Schema、示例、核对清单 |
| `docs/WINDOWS_WIFI_DEPLOY.md` | Wi-Fi 部署 |
| `docs/HANDOFF.md` | **逐版本开发日志**（根因分析、验收记录，最长） |
| 本文档 | 全项目一页式交接总览 |

## 11. 不可破坏的规则（摘自 AGENTS.md，安全相关）

- 禁止任何形式的远程 shell/脚本执行；
- 键位必须走白名单规范化；USB 入口只准监听 `127.0.0.1:8765`；
- 手机音频默认**不落盘**；
- 组合键异常时必须释放所有修饰键（hold 引擎任何路径都要释放）；
- 语音引擎快捷键从用户配置/档案动态读取，不写死键位；
- 旧 `copy/paste` 等固定动作与遗留 `typeless` 协议继续兼容；
- USB/受管请求保留 requestId 去重与确认。

## 12. 里程碑速览

- **1.4.0** — 上一实机稳定基线
- **dev.5** — 共享音频扇出、WPF Aether 控制台
- **dev.6** — 共享麦克风电脑端联动（Ctrl+Alt+M）
- **dev.7** — 配对失效可视化、USB 自愈、设备删除（Windows）
- **dev.8/9/10/11** — 主题迭代（最终九套）
- **dev.12** — 设计稿皮肤圆润化
- **dev.13 / Win dev.8 / mac dev.3**（bf00bb2，2026-09-07）— **语音引擎档案化**（本次交接的核心新能力）

逐条细节（含根因分析与验收数据）全部在 `docs/HANDOFF.md`。
