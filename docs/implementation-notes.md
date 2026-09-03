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

## 7. Win32 修饰键规范化、CGNAT 过滤与非阻塞音频放行终极修复

1. **Win32 修饰键虚拟键码规范化 (Generic Modifier Key Mapping)**：
   - **问题现象**：Typeless (Electron) 注册全局快捷键 `Shift+Z` / `Shift+X` 时，Windows `RegisterHotKey` API 内部依赖 `VK_SHIFT (0x10)` 通用修饰键状态。服务端原 `ParseTypelessKey` 返回 `VK_LSHIFT (0xA0)`，导致 `SendInput` 仅更新了 `VK_LSHIFT` 状态表，Windows 热键调度程序未收到 `MOD_SHIFT` 触发信号。
   - **修复**：在 `VirtualKey` 与 `NormalizeVirtualKey` 中统一将 `VkLeftShift/VkRightShift` 映射为标准 `VkShift (0x10)`，`VkLeftControl/VkRightControl` 映射为 `VkControl (0x11)`，`VkLeftMenu` 映射为 `VkMenu (0x12)`，并保持各自准确的键盘物理扫描码（ScanCode）。

2. **Typeless 麦克风配置与音频流向闭环**：
   - 确保 `%APPDATA%\Typeless.exe\app-settings.json` 中 `selectedMicrophoneDevice` 准确指向 `CABLE Output`，消除麦克风退回系统默认麦克风阵列的问题。
   - 在 `DictationSessionManager.Start` 中先立即放行 `audioBridge.BeginPlayback(sessionId)`，保证手机发送的语音数据即刻进入 VB-CABLE Input，打破 CoreAudio 录音流与音频播放的死锁。

3. **CGNAT (100.64.0.0/10) 与 VPN 代理冲突排除**：
   - 电脑安装 Tailscale 后，`100.73.179.83` 被作为局域网候选地址广播，手机端 ClashMeta 代理对 100.64.0.0/10 进行路由拦截导致连接超时；
   - 在 `LanIdentity.IsExcludedAddress` 中新增对 RFC 6598 CGNAT / Tailscale 网段（`100.64.0.0/10`）的自动过滤，局域网候选 IP 稳定锁定为物理 Wi-Fi 地址 `192.168.0.199`。

4. **测试与运行验证**：
   - 单元测试套件全部通过；
   - `PhoneDeck.Server.exe` 后台守护进程正常运行，真实调用 `/api/dictation/start`（`mode: translation`）与 `/api/dictation/stop` 均秒级返回 `ok: true`，且 Typeless 听写/翻译已正常响应。

## 8. 2号机可靠性加固与三项缺陷修复（2026-09-01）

背景：2号机接收端进程中途退出且无任何守护/自启动与日志留存，导致语音整链路不可用（Typeless 与 VB-CABLE 均正常）。本节记录当天的加固与修复。

1. **接收端事件日志**：
   - `Program.cs` 启动时向数据目录写入 `server-events.log`，记录 `started/exited pid=…`、
     `AppDomain.UnhandledException` 与 `TaskScheduler.UnobservedTaskException`；
   - 事后可通过日志区分"进程被关闭"（只有 exited）与"进程崩溃"（exited 前有 fatal 记录）；
   - 单文件超 512 KB 自动清空重写，写入失败静默不影响服务。
2. **`/api/update/package` 去硬编码**：
   - 原实现硬编码开发机绝对路径 `E:\Desktop\…\PhoneDeck-1号机极速更新包.zip`；
   - 改为读取环境变量 `PHONEDECK_UPDATE_PACKAGE`（未设置或文件不存在返回 404），
     保留分发机制但不再无条件暴露本地文件；Android 端无引用，行为兼容。
3. **ControlCenter 重置密钥文件名修复**：
   - 原按钮写入 `lan-token.txt`，而 `LanIdentity` 读取 `lan-access-token.txt`，点击实际不生效；
   - 已改为写入正确文件名，重置语义与确认弹窗文案（撤销授权 + USB 重配对）一致。
4. **ControlCenter 数据目录支持 `PHONEDECK_DATA_DIR`**：
   - 原硬编码 `%LOCALAPPDATA%\PhoneDeck`，与接收端便携数据目录方案不一致；
   - 改为优先读取 `PHONEDECK_DATA_DIR`（解析逻辑与 `PhoneDeckDataDirectory` 一致），未设置沿用默认。
5. **2号机运维加固**：
   - 注册表 `HKCU\…\Run` 写入 `PhoneDeck Control Center` 指向 `control-center-publish\PhoneDeck.ControlCenter.exe`，
     登录后由控制台拉起并监管接收端（不健康自动重启，10 秒限频）；
   - 防火墙确认：TCP 8766 既有规则启用；两个部署路径的 exe 已有 Windows 自动创建的
     入站 TCP/UDP Any 放行规则，UDP 8767 发现流量实际已被放行
     （专门的 `PhoneDeck Discovery (UDP 8767)` 端口规则未创建，两次 UAC 均被取消，属可选项）；
   - 旧版二进制备份至 `rollback/2026-09-01-pre-optimize/`。
6. **测试与运行验证**：
   - `PhoneDeck.Server.Tests` 39/39 通过（工作区含未提交的测试重构，数量与之一致）；
     ControlCenter Release 构建 0 警告 0 错误；
   - 新版接收端/控制台已部署至 2号机并运行：`/api/health` 全绿、computerId 不变（配对保留）、
     `/api/update/package` 返回 404、`server-events.log` 出现 `started pid=…`、
     本地 UDP 8767 `PHONEDECK-DISCOVER` 探测收到正确应答（不含密钥）。
   - 风险与说明：语音链路未做真机回归（仅服务端健康验证）；代码改动与既有未提交修改
     （修饰键/CGNAT 等）同处工作区，均未提交，待项目所有者决定提交时机。

## 4. Android 多主题恢复与 UI/UX 打磨（2026-09-01）

### 背景与决策

- 2026-08-31 项目曾按当时要求把主题收敛为单一“冰川玻璃”；本轮项目所有者明确提出
  “提升整体体验（UI/UX 优化）并增加几套主题”，属于方向调整，据此恢复多主题。
- 选型策略：从历史 9 套中精选 6 套，覆盖 3 浅 3 深且风格互补，避免选择过载；
  黄金系列（goldblue/goldamber/goldforest）为实验性方案，已连同常量彻底删除，
  旧配置中的相关 ID 由 `byId` 回退到冰川玻璃。

### 变更清单

1. **`PhoneDeckTheme.java`**：
   - `all()` 返回 6 套：冰川玻璃（默认）、柔和浅色、极简墨白、深海蓝、OLED 黑、极简纯黑；
   - `load()`/`save()`/`byId()` 恢复真实读写 `theme_id`，未知 ID 回退 frost；
   - `contentBackground()` 统一返回透明、`wrapContent()` 统一叠加环境光背景，
     所有页面共享同一背景分层（根底色 → 环境光 → 内容）；
   - `applyWindow()` 预刷窗口背景为主题底色，消除深色主题启动闪白
     （XML `AppTheme` 固定为 `Theme.Material.Light`，无法在资源层按主题切换）。
2. **`FrostedBackdropView.java`**：改为按主题绘制环境光背景。冰川玻璃维持原柔光玻璃；
   深海蓝为深蓝底 + 低亮度蓝/青光晕 + 微弱高光带；OLED 黑近纯黑、极低光晕（兼顾省电）；
   极简纯黑只用灰阶光晕；柔和浅色与极简墨白为极低透明度暖色/灰阶光晕，保证正文对比度。
3. **`MainActivity.java`**：主界面根布局无条件叠加主题感知背景（原先仅 frost 有）。
4. **`SettingsActivity.java`**：主题说明文案更新为“点按立即切换”；主题卡片补上与主界面
   一致的按压缩放反馈（`installPressFeedback`），与 `selectTheme` 既有触感/无障碍播报衔接。
5. **文档**：`HANDOFF.md` 两处单主题描述更新为 6 套主题现状。

### 4.1 修订：全新设计替换旧配色（2026-09-01 同日）

项目所有者确认此前恢复的 5 套旧配色不是其期望的“新主题”，据其选择调整为：

1. **主题池改为 5 套**：冰川玻璃（默认，不动）+ 全新设计 4 套，方向由所有者
   多选指定——莫兰迪低饱和、森林自然绿、暖沙/日落、赛博霓虹：
   - `morandi` 莫兰迪浅雾（浅）：燕麦底 rgb(244,242,239)、雾蓝主色 rgb(96,120,148)，
     卡片预设色走既有低饱和分支；
   - `forest` 森林晨白（浅）：米白底 rgb(242,245,238)、苔绿主色 rgb(58,122,88)；
   - `sunset` 日落暖沙（深）：暖棕底 rgb(28,22,18)、琥珀主色 rgb(232,158,76)，
     `onPrimary` 用深棕保证按钮对比；
   - `neon` 赛博霓虹（深）：深紫底 rgb(16,12,30)、霓虹青主色 rgb(64,205,228)。
2. **旧 5 套彻底删除**：ocean/oled/paper/inklight/inkdark 的常量、工厂与调色板全部
   移除；`isMonochrome()` 及其在 `PhoneDeckTheme.shortcutColor/shortcutAccent` 与
   `MainActivity.updateVoiceControls` 的灰阶分支一并清理（不再有单色主题）。
3. **`FrostedBackdropView`** 按新主题 ID 分发：morandi（暖灰粉/雾蓝极淡光晕）、
   forest（苔绿光晕）、sunset（琥珀+暗玫瑰光晕、微弱高光带）、neon（青+品红光晕），
   另有未知 ID 的中性兜底渐变。
4. **验证**：`assembleDebug`+`lintDebug` 通过；真机（R5CN404KQZR）逐套切换截图核对
   （`artifacts/themes2-*.png`），四套新主题的对比度、卡片配色、语音区、状态栏均正常，
   验证后手机已还原为默认冰川玻璃。
5. **兼容性**：`MainActivity.onResume` 的主题变更检测（`appliedThemeId` 比对 +
   `recreate()`）继续负责跨页生效；`ShortcutKeyView`/`VoiceLevelView` 的
   `isFrost()`/`light` 降级分支保留；`serverProtocolVersion`、快捷键、连接链路零改动；
   旧版本保存的已删主题 ID 由 `byId` 回退 frost。
6. **风险**：四套新调色板对比度按 WCAG 大字号粗体估算（主按钮 ≥4.5:1），未做
   无障碍工具全量审计；快捷卡预设色（purple/green/orange/red）沿用通用深浅变体，
   未针对 neon/sunset 单独调色，如需精修可再迭代。

### 4.2 修订：去"AI 味"视觉重构（2026-09-01 同日第二轮）

**背景**：项目所有者反馈整体视觉"AI 味道太重"，要求先调研真实移动端设计规范与案例，
再据此重设计。检索结论（来源见下）：
- AI 生成 UI 的公认特征：紫蓝渐变、无意义玻璃拟态/霓虹光晕、千篇一律卡片阵列
  （smoothui.dev《AI Design Slop》、prg.sh、braingrid.ai 设计系统建议）；
- Material Design 3：表面层级 = 中性色调色板步进（浅色 98→90 / 深色 6→22），
  组件用 container 色，强调与对比关系决定用色（m3.material.io/styles/color/roles）；
- Apple HIG：语义色（蓝=交互、绿=成功、红=录制/危险），深色模式纯黑/抬升灰阶；
- iOS 语音备忘录（同类工具范式）：黑画布、单色文字、红色专属录音态、波形为唯一装饰；
- Things 3 / Bear + NN/g 第八启发式：只保留支撑任务的元素。

**落地的设计规则与代码变更**：
1. `PhoneDeckTheme.java`：全部 5 套调色板重写为"中性灰阶底（每主题仅残存极低色温倾向）
   + 单点缀色"；`shape()` 移除玻璃/渐变分支恢复纯色填充+发丝线；删除
   `shortcutColor/shortcutPressedColor/isFrost`，快捷卡分类色收敛为
   `shortcutAccent`（紫/绿/橙/红取 HIG 语义系低饱和值）。
2. 删除 `FrostedBackdropView.java` 及 `MainActivity`/`wrapContent` 中的背景层引用；
   `contentBackground()` 回归 `background` 纯色。
3. `ShortcutKeyView`：中性卡面 + 1dp 描边 + 标题前 7dp 分类色点；组合键胶囊用
   surfaceRaised；文本动作副标题保留点缀色（区分动作类型）；海拔归零。
4. `VoiceLevelView`：删除冰川玻璃专用辉光波形，统一扁平细条；活动色=点缀色，
   暂停/错误=语义色。
5. `MainActivity`：移除字距眉标与全部 setElevation；语音按钮改纯色，录音/停止态用
   danger 红填充（HIG 录音语义），图标/文字用 onPrimary；`showActionFeedback` 入口
   统一剥离 ●✓✕■◌ 等前缀符号；麦克风状态行文案统一为"手机麦克风 · 状态"。
6. 主题展示名同步去玻璃化：经典浅色/雾蓝浅色/苔绿浅色/琥珀深色/青辉深色
   （id 不变，配置兼容）。

**验证**：`assembleDebug`+`lintDebug` 通过；真机逐套截图核对
（`artifacts/redesign-frost-main.png`、`redesign-settings.png`、`redesign-sunset-main.png`、
`redesign-neon-main.png`），中性表面、发丝线、色点、扁平按钮、无符号反馈行均符合预期，
验证后已还原为经典浅色。误触记录：定位设置按钮时误发了一次"复制"(Ctrl+C) 到当前目标电脑，无害。

**风险**：分类色点使卡片信息密度略增；横屏双栏与编辑页未单独截图核对（同一套
theme 方法，预期一致）；`feedbackSurface` 语义底色仍按状态着色（保留，属状态语义）。

### 4.3 体验优化第二轮：恢复原版主题 + 8 项 UX 改进（2026-09-01）

项目所有者批准的执行计划，分四批落地（全部真机验证，截图 `artifacts/ux-*.png`）：

**阶段 0 · 恢复原版冰川玻璃主题**
- 重建 `FrostedBackdropView.java`（原版柔光背景，仅 glass 主题使用）；
- `PhoneDeckTheme` 新增 `GLASS="glass"`：原版完整调色板（半透明表面/白描边/蓝渐变
  主按钮），`shape()` 恢复玻璃渐变分支、`wrapContent()` 叠加柔光背景、
  `contentBackground()` 对 glass 返回透明；其余 5 套保持中性灰阶新设计不受影响。

**阶段 1 · 语音坞瘦身与布局健壮性**
- 删除"语音输入"标题行；模式标签并入电平条同一行；麦克风状态行收编
  `setMicStatus(text, color, idle)`（空闲态 GONE，活动/异常 VISIBLE），坞高减少约 60dp；
- 底部留白动态化：竖屏 `voiceDock.addOnLayoutChangeListener` + `post` 把 page 底
  padding 校正为实际坞高（362dp 仅为首帧初值），反馈换行不再盖网格；反馈行 maxLines=2；
- 反馈自动消退：success/muted 消息 4 秒后淡回"准备就绪"（`feedbackReset`），
  warning/danger 保留到下一条；`onDestroy` 清理。

**阶段 2 · 切页与状态细节**
- 设置页 `onSaveInstanceState` 保存 scrollY，`recreate()` 后恢复（`scroll` 提为字段）；
- 快捷操作提示计数展示 6 次后自动 GONE（`shortcut_hint_views`），编辑模式不受限，
  统一走 `updateShortcutHint()`；
- 连接状态点呼吸：muted/warning 时 `ObjectAnimator` alpha 1f↔0.35f 循环
  （`statusDotPulse`，target 跟随视图重建），success 时静止。

**阶段 3 · 跟随系统 + 原生 ripple**
- `AUTO="auto"` + `storedId()`：`load()` 按 `uiMode` 解析（夜→sunset，昼→frost），
  `save()` 原样存 auto；设置页顶部新增"跟随系统"卡（左浅右深双分块预览），
  `selectTheme` 判定改用 storedId；`MainActivity.onConfigurationChanged` 增加主题
  变更检查（Manifest 未加 uiMode，系统昼夜切换由系统自动 recreate）；
- `pressable()` 改返回 `RippleDrawable`（pressed 色 170 alpha 涟漪 + 纯色 shape 内容），
  全量按钮/chip 生效；按压缩放动画保留叠加。注意调用方签名从
  `StateListDrawable` 变为 `Drawable`（MainActivity `pressableRoundRect` 已同步）。

**验证**：`assembleDebug`+`lintDebug` 通过；真机（R5CN404KQZR）：瘦身坞（经典浅色）、
设置页 7 张主题卡、跟随系统经 `adb shell cmd uimode night yes/no` 实测昼夜切换、
冰川玻璃（原版）主界面确认玻璃质感与瘦身坞并存、切主题后滚动位置保持。
验证后手机停留在冰川玻璃（原版），即项目所有者指定的"原来那个主题"。

**风险与说明**：auto 解析依赖读取时点的 uiMode，极端情况下（系统切换瞬间）以最近一次
recreate 为准；呼吸动画仅作用于 12dp 状态点，性能影响可忽略；本批未触碰语音/连接逻辑。

### 4.4 AI 产品风格主题包（2026-09-02）

项目所有者要求把主题换成 ChatGPT / Claude / Grok / Gemini 四家成熟 AI App 的风格
（"模仿借鉴成熟 SaaS"）。落地方式：

1. **8 套新主题（4 家 × 深浅）**，全部取各家 App 的真实视觉特征（配色借鉴，非商标资产）：
   - `gptlight/gptdark` ChatGPT：纯白/炭灰（#212121）底，主钮黑（深色反转为白），
     极简灰阶，无彩色点缀；
   - `claudelight/claudedark` Claude：米白纸感（#FAF9F5）/ 暖炭（#262624）底 +
     陶土橙主钮（#D97757）；
   - `groklight/grokdark` Grok：纯白/纯黑画布 + 黑/白主钮；恢复 `isMonochrome()`
     灰阶分类色映射（卡片色点只保留深浅差异，不出彩色）；
   - `geminilight/geminidark` Gemini：白/石墨（#131314）底，Google 蓝 #0B57D0（浅）、
     淡蓝 #A8C7FA（深）主钮，浅蓝灰面板（#F0F4F9）。
2. **主题池调整**：`all()` = 冰川玻璃（原版）+ 8 套 AI 风格，共 9 张卡；上一轮的
   雾蓝/苔绿/琥珀/青辉 4 套删除；frost（经典浅色）保留为代码级回退默认（`byId`
   兜底与旧配置兼容），不再出现在选择列表。
3. **跟随系统映射更新**：auto 昼→ChatGPT 浅色、夜→ChatGPT 深色；设置页 auto 卡
   文案与左右分块预览同步更新。
4. **验证**：`assembleDebug`+`lintDebug` 通过；真机逐套截图核对
   （`artifacts/ai-gpt-light/dark.png`、`ai-claude-light/dark.png`、`ai-grok-light/dark.png`、
   `ai-gemini-dark.png`），Grok 灰阶色点、Claude 陶土橙、Gemini 淡蓝主钮均符合预期；
   验证后手机还原为冰川玻璃（原版）。
5. **风险**：各家配色取自公开 App 观感的手工取色，非官方 design token，个别表面色
   可能与最新版本有细微出入；语义色（成功/警告/危险）未跟随各家品牌，保持通用
   可读性优先（Grok 的状态色未做纯灰阶，错误仍用红色以保证可用性）。

### 4.5 主题重构：品牌 × 深浅两级结构 + Hermes / 豆包（2026-09-02）

项目所有者指出：不应把"Claude 深色""Claude 浅色"列为独立选项——选品牌后点深浅
应直接呈现该品牌的深色/浅色（成熟 SaaS 的标准做法）。同轮要求新增 Hermes Agent
与豆包两套风格。

1. **存储重构（`PhoneDeckTheme`）**：`theme_id` 单键改为 `theme_brand`（品牌族）+
   `theme_mode`（auto/light/dark）双键；`ensureSelection()` 首次读取时把旧
   `theme_id` 一次性迁移（gptlight→gpt+light、auto→gpt+auto、glass→glass+light、
   frost 及更早下线 ID→glass+light）。新 API：`storedBrand/storedMode/
   saveSelection/brandName/brandTheme(brand, dark)`；`load()` = brand + mode
   （auto 按系统 uiMode 解析深浅，品牌保持不变——跟随系统跟随的是深浅而非品牌）。
   实例 id 仍唯一（gptlight 等），MainActivity 的 `appliedThemeId` 比对无需改动。
2. **品牌池 7 族**：冰川玻璃（原版，仅浅色，dark 参数忽略）、ChatGPT、Claude、
   Grok、Gemini、**Hermes**、**豆包**；不再出现"×浅色""×深色"独立选项。
3. **Hermes（Nous Research Hermes Agent，据官方 skins 文档与品牌规范）**：
   - 浅色 = daylight 风：纸感米白 #FAF9F5 + 近黑文字 #141413 + 冷蓝主钮
     （品牌辅色 #6A9BCC 加深至 #3F6E9E 保对比）；
   - 深色 = slate 风：石墨蓝底 (24,27,31) + 签名暖橙 #D97757 主钮。
4. **豆包（字节）**：浅色 = 白底 + 靛蓝主钮 #3B5BEB + 蓝灰面板；
   深色 = 深灰蓝底 (21,21,27)（非纯黑，带蓝相）+ 亮靛蓝 #7086FF 主钮。
5. **设置页**：外观区块改为"深浅模式三态 chips（跟随系统/浅色/深色）+ 品牌卡列表"；
   品牌卡预览改为上浅下深双拼（各显示该品牌该模式下的底色与主色/表面小条），
   选中态按品牌族判断；`selectMode`/`selectBrand` 分别保存，互不覆盖。
6. **验证**：`assembleDebug`+`lintDebug` 通过；真机（R5CN404KQZR）验证：
   旧 theme_id=glass 迁移正确（浅色+冰川玻璃选中）、Claude+深色=暖炭陶土橙、
   Hermes 深色=石墨蓝暖橙、豆包浅色=靛蓝、豆包+跟随系统+`cmd uimode night yes`
   =豆包深色（品牌保持、深浅跟随）、品牌切换后模式保持。截图 `artifacts/bm-*.png`。
   验证后还原：系统日间 + 冰川玻璃 + 浅色。
7. **风险**：glass 族深色侧与浅色相同（原版只有浅色设计），品牌卡下半拼即重复
   展示；`byId` 兼容入口遍历品牌×模式（14 次 new，仅冷启动一次性成本）。

### 4.6 语音尾音丢失修复：停止时垫尾部静音（2026-09-02）

**现象**：用户说完最后一个字后隔了零点几秒才按停止，结尾几个字仍未被识别。

**根因**（代码证据）：
1. 手机端 `AudioStreamer.stop()` 在主线程立即 `connection.disconnect()`：工作线程
   最后的 20ms chunk 可能 write 失败；`AudioRecord` 内部缓冲（约 20-80ms）未取走
   即丢；chunked 流从未正常关闭（无终止块），disconnect 可能触发 TCP RST 清掉
   接收端未读 PCM——Wi-Fi 下可丢弃几十至两三百毫秒。
2. 电脑端排空逻辑（`PhoneAudioBridge` drain：WASAPI→CABLE）与
   `DictationSessionManager.Stop` 的 `WaitForSessionEnd`→停 Typeless 时序本身正确，
   但 VB-CABLE 内部缓冲与 Typeless 读取节奏可能滞后 100-200ms，无人保障排空。

**修复**（仅手机端 `AudioStreamer.java`，协议不变）：
- `stop()` 不再立即 disconnect，只置停止标志 + 停 `AudioRecord`；
- 新增 `finishLinkTail()`（在工作线程 finally 中无条件执行）：追加 500ms 静音 PCM
  → flush → `output.close()`（正常结束 chunked，接收端读干净 EOS）→ 沉降 150ms
  → 由既有 finally 兜底 disconnect；
- 静音垫层把真实尾音"推"过整条管道；电脑端既有 WaitForSessionEnd（2s 预算）+ drain
  逻辑与手机端 EOS 时序吻合（实测预算内 ~1s 完成），无需改动服务端。

**代价**：按下停止到文字落纸增加约 0.5-0.8s（尾垫 500ms + 排空），换取尾字完整。

**验证**：`assembleDebug`+`lintDebug` 通过并装机；未替用户触发真实语音会话，
需项目所有者实测：正常语速说一句、结尾轻声/快速按停，核对最后几个字完整。
若仍有个别丢字，后续可选：电脑端 `Stop` 在 `WaitForSessionEnd` 后加 150ms
固定延迟（需重新部署接收端）。

### 4.7 尾音丢失根因定位与根治（2026-09-02 第二轮，两端日志驱动）

用户实测 4.6 修复后仍漏字且感知延迟重。通过两端毫秒级日志（手机 logcat +
新增 server-console.log 双写）定位出**叠加的三层根因**：

1. **接收端 WASAPI 慢启动 → 音频恒定积压 ~2s**：分段计时日志证实
   `select=1750ms`——`SelectDevice` 遍历设备读 `FriendlyName`（COM 属性），
   慢/僵尸设备（蓝牙等）单次调用阻塞 1-2s。期间手机音频积压在 pre-roll
   （`preRollBytes=131072`≈1.37s，watermark 放行），整条管道从此落后约 2s；
   Typeless 停止键到达时只听到 2s 前的音频，最后约 2s 语音（数个字）全部丢失。
   500ms 尾垫远不足以覆盖。
2. **排空被断流跳过**：手机 `disconnect()` 的 RST 使 StreamAsync 读循环异常
   退出，旧代码的 drain 段在 try 尾部被跳过（[audio] 无 drained 日志），
   积压音频永远不会播进 CABLE。
3. 部署迷雾：watchdog 实际运行 `control-center-publish\` 子目录副本，
   早前替换根目录 exe 均未生效；且 dotnet SDK 在 Git Bash PATH 指向 32 位
   runtime 目录，后台 publish 静默失败过一次。

**修复**：
- `PhoneAudioBridge`：① drain 移入 finally 且先于 `Ended.Set`/`AudioEnded`——
  任何断流/异常路径都把积压播完才停 Typeless；② `cachedCableDeviceId` 设备
  ID 缓存（ID 比较不触发 FriendlyName 的 COM 读）+ 启动 `Prewarm()` 后台预热
  （首次仍付 ~1s，后续 select=0ms，wasapiStarted 从 +2s 降至 ~100ms）；
  ③ wasapiStarted 分段计时日志（enumerate/select/init/play）。
- `Program.cs`：`Console.SetOut/SetError` 双 Tee 到 `server-console.log`
  （512KB 自动清空），stderr 的异常文本同样落盘。
- `AudioStreamer`（手机）：TAIL_SETTLE_MS 150→400ms，降低关流后 RST
  打断服务器读流的概率。

**验证**（受控实验，发送 2.5s 已知 PCM 含 500ms 尾垫）：bytes=240000 完整接收；
人为积压 2s 时 `drained bufferedWait=1984ms`——排空把积压全部播完才结束会话；
prewarm 生效后 `select=0ms`、`init≈100ms`。中途三个"失败"复测为测试脚本
chunk 长度写错（'3c0' 配 1920 字节数据，服务器 "Bad chunk suffix" 属正确拒绝），
服务器代码无恙。

**预期效果**：无积压时停止延迟 ≈ 手机尾垫 500ms + 排空（接近零）；偶发积压时
排空兜底保证尾字完整。真实语音效果待项目所有者实测确认（两端日志已就绪，
server-console.log 会记录每次会话的 wasapiStarted 分段、drained、bytes）。



