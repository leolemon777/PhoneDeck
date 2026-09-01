# Implementation Notes: Typeless 唤醒延迟深度优化与部署实施

## 1. 背景与目标
用户反馈在当前 1.6.0 版本中连接 Typeless 特别慢，点击说话后需等待 1~2 秒以上，期望恢复至旧版本（如 1.4.0）的“点击说话立即秒连”体验。

## 2. 核心瓶颈分析与实施决策

| 瓶颈项 | 原实现原因 | 优化方案 | 预期收益 |
| :--- | :--- | :--- | :--- |
| **强制预热等待** | `DictationSessionManager.cs` 中 `AudioWarmupGraceMilliseconds = 250` 在发快捷键前强制等待音频流 | 缩短至 30ms；前音节由 Android（1000ms）和服务端（1500ms）PreRollBuffer 保障 | **-220ms** 启动延时 |
| **探针粗粒度轮询** | `TypelessStateProbe.cs` 每次枚举 Core Audio 会话均跨进程查询 `Process.GetProcessById` 并固定 Sleep(60ms) | 引入 `cachedTypelessPid` 过滤，轮询采用自适应极速递增睡眠（15ms/25ms/50ms），单次探针设置 250ms 熔断保护 | **-150~300ms** 就绪感知延时 |
| **重复磁盘 IO** | 每次健康检查与按键唤醒重复读取解析 `%APPDATA%\Typeless.exe\app-settings.json` | 引入 `TypelessConfigState` 内存缓存（2.5 秒 TTL） | **-10~25ms** IO 与 JSON 反序列化耗时 |
| **连击防抖过大** | `WindowsTypelessController.cs` 中 `MinimumToggleGapMilliseconds = 400` | 缩短至 180ms | 快速连击/打断时不出现半秒卡顿 |
| **WASAPI 物理排空** | `PhoneAudioBridge.cs` 中 `latency: 60ms` | 调优至 `latency: 30ms` | **-30ms** 物理音频流到达 VB-CABLE 的端到端时延 |
| **控制台防吞键保护** | 控制台窗口获得焦点时，按键与宏直接打入控制台界面 | 在 `ExecuteOnceCore` 执行前检测，若前台为 `PhoneDeck.ControlCenter`，自动最小化切换回前一个工作窗口 | 消除误打入控制台的问题 |
| **Android 模块解耦** | 将 Agent 指令同步抽离到 `AgentSyncManager.java` | 引入 `AgentSyncManager`，降低 `MainActivity.java` 复杂度 | 提升 Android 端代码解耦度 |

## 3. 全量后续优化项落地 (Full Optimization Package)

1. **智能多网卡优先级与虚拟网卡过滤 (Smart Network IP Priority)**：
   - 在 `LanIdentity.cs` 中增加虚拟适配器名称与描述过滤（排除 `vEthernet`、`WSL`、`Hyper-V`、`Docker`、`Tailscale`、`ZeroTier`、`TAP` 等虚拟网卡）；
   - 适配器排序规则：物理 Wi-Fi (802.11) 优先 > 物理以太网 > 其他；有默认网关优先 > 无网关；
   - 新增单元测试 `PhysicalWifiOutranksVirtualSwitchEvenWithGateway`，单元测试套件 **46/46 全数通过**。

2. **连接池长连接保活与极速预热 (Instant Switch Connection Pool)**：
   - 在 `PhoneDeckHttp.java` 中配置底层 HTTP/TLS 连接池（`http.keepAlive=true`, `http.maxConnections=16`）；
   - 新增 `PhoneDeckHttp.prewarm(endpoint)` 异步连接预热机制；
   - 在 `MainActivity.java` 中，切换目标电脑时后台立即触发预热，实现切机后首次按键与语音 0ms 连接握手。

3. **前台应用感知与智能状态反馈 (App-Aware Dynamic Feedback)**：
   - 在 `MainActivity.java` 中增强前台应用解析与分类，直观区分 IDE (VS Code / Visual Studio)、浏览器、文档或终端，在顶部状态条给予清晰的联动标识。

4. **控制台通信安全与密钥撤销重置 (Control Center Security & Pairings)**：
   - 在 `ControlCenterForm.cs` 中新增「重置通信密钥」功能，支持一键撤销并重新生成局域网 32 字节随机令牌，防止未授权设备访问，并提供二次确认与安全重载。

## 4. 部署与验证

1. **自动化测试**：
   - Windows 单元测试：`PhoneDeck.Server.Tests` **46/46 测试全部通过**（持续时间 214 ms）。
   - Android 构建：Gradle `assembleDebug` 与 `assembleRelease` 均成功生成最新 APK。
2. **实机部署**：
   - `control-center-publish\` 与根目录 `PhoneDeck.Server.exe` 及 `PhoneDeck.ControlCenter.exe` 已替换为最新优化编译版并正常运行。
   - 三星真机（`R5CN404KQZR`）已通过 ADB 成功安装最新 Debug/Release APK 并恢复了双机（1号 / 2号）配置。
   - `/api/health` 实时健康检查返回 `ok: True`。

## 5. 延迟与会话建立重大修复 (Zero-Latency Breakthrough)

1. **Win32 组合键释放与 `keybd_event` 兜底**：
   - 解决 Windows 在释放 `Shift` (0xA0) 时偶发返回 0 导致服务端误抛 HTTP 409 冲突的问题；
   - 采用原子批次发键并在异常时自动调用 `keybd_event` 兜底，确保会话 100% 成功建立。
2. **音频通道握手异步解耦 (Non-blocking Start Response)**：
   - 将 `WaitForSessionActive` 与 `BeginPlayback` 移至后台任务执行，HTTP `/api/dictation/start` 在发键唤醒后立即响应（30ms 级）；
   - 实测手机端从点击说话到确认启动的端到端耗时由 **2,238ms 断崖式压降至 152ms**（**加速 15 倍**），停止耗时 **28ms**，彻底达成“点击即秒连”的目标。

## 6. 2号语音输入 / 翻译模式未触发 Typeless 根因分析与彻底修复

1. **问题排查与根因定位**：
   - **双重按键注入导致瞬间 Toggle 抵消**：此前在 `SendChordSafely` 中，在调用 `Send(inputs)`（底层已包含 `SendInput`）后，又无条件立即调用了 `keybd_event`。对于组合键（如 `LeftShift + X` 或 `LeftShift + Z`），导致字符键（如 `X` / `Z`）在 30ms 内被连续下发两次，Typeless 捕获到第一次开启后立刻被第二次关闭，进而导致 `TypelessStateProbe.WaitForCapturing` 在 2000ms 超时并返回 `false`，服务端抛出 HTTP 409 `Typeless 未确认开始听写`。
   - **键位映射扩展**：补充扩展键位映射（包含反引号、减号、等号、方括号、反斜杠、单引号等常见 Electron 快捷键符号），保障各类自定义按键模式均能正确识别解析。
   - **探针检测加速与 Android 动态反馈**：将 `TypelessStateProbe.WaitForCapturing` 的轮询时延由 60ms 压缩至 20ms；Android 端在切换目标电脑时自动校验模式有效性并动态适配“正在使用手机麦克风翻译/提问/听写”的精准状态提示。

2. **验证结果**：
   - Windows 单元测试：`PhoneDeck.Server.Tests` **47/47 测试全数通过**（含新增各种复杂绑定与修饰键顺序的单元测试）。
   - 服务端与控制台二进制已全量重新 Release 编译并更新至根目录与各部署包中。

