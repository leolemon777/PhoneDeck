# 手机连接与共享麦克风稳定性改造方案

## 2026-10-29 后续源码实施进展

按用户“继续优化，不需要测试”的最新要求，本轮实现普通健康失败时保留仍有音频进度的共享目标、无进度目标的有限恢复窗口、
单连接写入看门狗与异步断开、Windows 运行中输出故障传播、线程池饱和时不阻塞调用线程及多地址探测提前结束。
配对身份变化和明确拒绝仍优先于连接保留；全部无有效音频连接时保留自动停采约束。共享连接数只在首批 write/flush 成功后增加，
但仍没有远端 PCM/播放确认。手机与运行中的接收端未更新到本轮代码；本轮只做静态复核与编译，不运行测试。
具体当前状态见 [HANDOFF](../HANDOFF.md)。下文的最初审计与后续完整方案作为历史依据保留，不能把其中全部规划视为已实现。

2026 年 10 月 29 日。适用主线 Android 与 Windows/macOS 外部输入法接收端。

当前优先级是让连接状态可信、音频中断能恢复、失败能定位。用户反馈覆盖发现不到电脑、配对后断线、显示连接但没有声音、多电脑切换及锁屏后失效。源码中已有并行发现、证书固定、前台录音服务、独立音频队列和接收端 5 秒断流回收，但这些机制之间的状态与恢复规则还没有闭合。

本轮先修复可以通过自动化验证的基础缺陷。下文的会话分层、接收确认、诊断界面和硬件验收是后续实施方案；不把它们记为已经完成的功能。

## 当前运行情况

本机运行 `control-center-publish/PhoneDeck.Server.exe`，产品版本为 `1.6.0-dev.17+a5e1c61`，包含 10 月 9 日的半开音频流修复。源码 Android 基线为 dev.22/code28。外层文件夹仍叫 dev.2，且根目录还留有更旧的 Server EXE，目录名不能用于判断运行版本。

只读检查得到以下事实：

- Windows 已检测到 VB-CABLE，配置中的 Typeless 麦克风为 CABLE Output。
- 一组间隔约 2 秒的 6 次健康请求耗时 3–136 ms，但音频诊断样本年龄为 6.6–12.9 秒，全部标记 `stale=true`。这段时间 `audio.streaming=true`，后续检查确认为 shared。HTTP 可达与诊断新鲜度显然不是同一件事；过期的 `capturing=false` 不能证明当时没有录音。
- 10 月 9 日的本地历史错误日志出现过 5 次“音频连接冲突：已有手机麦克风正在传输”。这证明发生过冲突，不证明目前仍被旧会话占用。
- 当前接收端路径存在专用网络的 TCP/UDP 显式阻止规则；当前 WLAN 属于公用网络，因此这是切换网络类型后的部署隐患，不能据此认定本次正在发生的连接故障来自防火墙。
- 初始审计没有 ADB 手机；后续接入 Samsung SM-G9880，补充了下述现装版本基线。新补丁尚未安装到手机或替换正在运行的接收端。

手机接入后，现装 dev.22/code28 与 Windows dev.17+a5e1c61 的 USB 共享做了开启、短时后台、关闭对照：
关闭时 CABLE Output 为静音，开启约 3 秒后在 25 秒测量窗口内持续读到非零电平，切到手机桌面后仍有信号，关闭后回到静音并释放服务。
只保存电平统计，没有保存音频；这不是锁屏、听写识别、多电脑或长时间验收。
后续 Typeless 最新状态为自动选择系统默认麦克风，并在物理麦克风阵列上持有 active 会话；未修改其设置。
因此分段自检还必须展示实际听写输入源，不能把“虚拟声卡已有信号”写成“听写已使用手机麦克风”。

手机 Wi-Fi 在两次检查之间从 PC 所在 /24 子网切到了另一 /24 子网。此时 PC 的 8766 建连超时，USB 8765 成功，
实际供音时只观察到 USB 回环连接。手机却仍主要展示绿色“正在共享”和“1/3 台供音”，没有说明实际通道。
下一批状态改造应保留每个目标的 endpoint 信息，至少显示“USB 连接 / Wi-Fi 探测失败”，并区分本地采集、上传连接和接收端数据证据。
不同子网及超时本身不足以定位到防火墙或路由器；应该提示检查当前网络，并保留可验证的错误分类。

现装包还包含与 PR #55 一致的指令库和快捷键 UI，且签名与本机新构建不一致。部署前需集成现有 UI、核对原签名与产物来源，
保留配对和快捷键数据。完整事实、哈希及升级限制见 [本轮手机基线记录](../HANDOFF.md)。

## 已定位的问题

| 问题 | 代码证据与影响 | 本轮处理 |
|---|---|---|
| 旧探测拖住新网络 | `ConnectionMonitor` 原先超时后保留 in-flight Future，reset 只重置退避；切网后可能继续等待旧请求 | 取消不再使用的超时探测，增加网络代次，隔离旧结果 |
| 自动切网后漏刷新 | 首页只监听可用/丢失，1 秒内事件被丢弃；共享服务没有独立 Wi-Fi 监听 | 新增共用链路观察组件，覆盖 IP/路由变化，首页合并后补发、服务后台重探测 |
| 旧会话回调污染新状态 | 只检查正在运行不能区分停止后重开的会话，音频 worker 可直接发布状态 | 探测与通知校验实际会话身份；状态在主线程应用，连接清理保留后台 |
| 短暂建连使退避归零 | `SharedAudioBroadcaster.TargetSink` 原先取得 OutputStream 就 reset；接收端反复拒绝时持续从 500 ms 重试 | 实际持续写入满 5 秒才复位；写入中断重新计时 |
| 共享数量误导 | `PhoneAudioService.knownTotal` 原先统计全部已配对电脑 | 改为显式共享组中的现存电脑数 |
| 诊断超时后发生并发读取 | `DiagnosticsMonitor.RefreshAsync` 原先等待超时就释放 gate，真正的 COM reader 尚未退出 | gate 持有到实际读取结束，补充退出竞态保护 |
| 探测不完整被当成停止录音 | `VoiceEngineStateProbe` 原先超预算后 break 并返回 false | 不完整或超预算返回 unknown，避免作为停止证据 |
| 控制探测失败拆掉音频流 | `PhoneAudioService.probeReceivers` 把本轮没有结果的电脑排除，`updateTargets` 随即删除 sink | 进入下一批会话恢复改造 |
| 供音状态缺少接收确认 | `TargetSink` 取得 OutputStream 即标记 connected；未证明接收端接纳、输出设备播放或输入法采集 | 进入下一批状态与确认改造 |
| 手机侧写入可能停滞 | shared 发送循环没有独立写入进度看门狗；read timeout 不能证明 write/flush 有界 | 进入下一批数据通道恢复改造 |
| 重连窗口与自动停采相冲突 | 探测最长退避 15 秒，共享零目标停采也是 15 秒；恢复可能尚未被下一次探测观察到 | 联合调整恢复状态和时限，不单独延长录音 |
| 运行身份与部署入口不统一 | 托盘默认使用程序旁 data，直接运行 Server 默认使用 LocalAppData；还可能打开旧 EXE | 增加统一启动入口和身份诊断，迁移时保留已配对身份 |

接收端 5 秒无字节回收已经存在，不能再把“增加断流超时”当作完整解决方案。主动共享仍在发送时，WASAPI 输出设备故障也需要向上传播；只看到流存在，不能保证电脑有声音。

## 目标行为

手机只需要让用户完成三件事：选择电脑、开始说话、停止说话。系统负责判断是否可用、恢复连接并解释失败。

| 场景 | 应呈现的行为 |
|---|---|
| 打开 App | 展示已配对电脑的验证状态和附近电脑；发现到设备不等于已经获准控制 |
| 电脑已连接但缺虚拟声卡 | 卡片显示“电脑已连接，音频待配置”，给出具体配置入口 |
| 点按或按住说话 | 清楚区分“手机正在采音”“电脑正在准备”“电脑已接收”；开始失败归还麦克风并显示原因 |
| 开启共享 | 只向明确选中的共享组送音，逐台显示等待、接收中、重连中、被占用或需重新配对 |
| 一台电脑断开 | 只恢复这一台，其余电脑继续供音；不会因为一次健康请求超时拆除健康音频流 |
| 短暂切网 | 当前用户已开启的 shared 会话在有界恢复窗口内尝试重新连接；超过窗口停止并说明原因 |
| App 被系统终止或用户停止 | 不自动重新开启麦克风；迟到响应和重试不能复活旧会话 |
| 电脑结束 shared 转写 | 只结束该电脑的段落，手机与其他电脑继续供音 |
| shared 与 managed 切换 | 手机先完成旧模式收尾，得到释放确认后再进入新模式；冲突明确说明占用来源 |

## 连接与音频分别管理

保留当前 Java、.NET、PCM 与 HTTPS 基线。Android 逐步提取以下职责，先迁移逻辑并回归，再调整 UI：

- `DeviceDirectory` 保存稳定 computerId、配对身份、候选地址、共享组。
- `ConnectionCoordinator` 统一网络事件、发现、验证、探测、退避与缓存代次。
- `AudioSessionController` 管理用户意图、managed/shared 互斥、sessionId、启动与停止、前台服务所有权。
- 每台电脑的 `PeerAudioSession` 管理发送队列、写入进度、接收确认、重连次数和该电脑的错误。
- `MainActivity` 渲染状态、转发用户操作；后台连接和共享恢复由相应长期存活组件持有。

连接状态和音频状态需要独立保存，避免把“在线”当成万能状态：

| 层次 | 需要证明什么 | 不能推导什么 |
|---|---|---|
| 发现 | 某个地址声称提供接收端 | 身份可信、凭据有效 |
| 已验证连接 | TLS pin、凭据、computerId 验证通过 | 虚拟声卡可用、音频正在播放 |
| 音频就绪 | 当前音频设备与配置检查通过 | 当前 session 已被接纳 |
| 接收中 | 同一 owner/session 的接收端持续收到 PCM | 第三方输入法一定在采集 |
| 正在转写 | 输入法有效探针确认采集 | 第三方一定已生成或提交文字 |

所有异步结果携带网络代次、sessionId、computerId 和原始采样时刻。未知或过期状态既不作为成功，也不作为电脑已经停止的证据。保留诊断开始时刻；增加完成时刻和耗时，不能把慢探针改成“刚刚采样”来掩盖过期。

## 共享麦克风恢复规则

共享组、健康探测和实际音频流分别记录。一次健康请求失败先把该电脑标为“状态待确认”，让已存在的数据流按自己的进度与接收确认继续工作。只有用户移出共享组、明确撤销凭据、确认不支持或恢复窗口耗尽时，才执行相应关闭。

逐台确认建议先兼容现有 health 的 owner/session/mode 字段，再增加能力协商后的流状态：接收代次、收到的帧/字节数、最后收包年龄、输出设备是否运行。UI 的“正在接收”必须依据接收端证据。输入法未采集在 shared 中是正常待命，不应触发全组停止。

写入进度看门狗必须独立于 read timeout。若 write/flush 长时间无进度，关闭该 socket 并进入独立退避；队列继续有界，恢复后不重播过期积压。socket 的关闭、旧 worker 退出和新连接创建必须有数量上限，不能通过不断创建线程来绕过阻塞。

零目标停采按“全组没有确认接收且恢复窗口已到期”计算。到期前需要完成一次有界的新鲜探测；不能让 15 秒退避先于最后一次恢复尝试结束整个共享。明确区分用户已开启会话的短暂恢复与进程重启后的重新开麦。

接收端保持每个音频槽的所有者、sessionId、流代次、最后收包时刻和输出状态。WASAPI/BlackHole 故障要结束或重建对应输出并返回具体原因。旧代次的 finally、stop 和失败回调不能清除新代次会话。

## 网络和部署改造

网络变化由统一组件处理，覆盖 Wi-Fi 可用性、链路地址变化和恢复事件。发现与已配对流使用正确的本地网络，不让 VPN、蜂窝默认路由或错误的固定 /24 广播地址决定 LAN 目标。USB 保持独立通道；网络变化不清除配对身份。

当前源码已补充共用 Wi-Fi 链路监听、IP/路由变化去重、旧事件隔离和前后台探测补发；没有改变系统选网，也尚未将发现/HTTPS socket 绑定到指定 Wi-Fi Network。
用户确认这次并非主动切网，系统日志支持“断开后自动选中另一已保存网络”。自动切网应作为常规恢复场景，不能依赖用户始终保持同一个接入点。

Android 的网络回调适合触发重新验证，但每种 callback 的意义不同，不能把任意网络 onLost 当作整机无网络。参见 [Android 网络状态文档](https://developer.android.com/develop/connectivity/network-ops/reading-network-state)。

锁屏共享采用用户在可见界面发起的 microphone 前台服务，通知始终显示共享对象并可立即停止。系统回收或权限失效后停止会话，返回界面解释原因；不能依靠后台偷偷重启录音恢复。参见 [Android 前台麦克风服务限制](https://developer.android.com/develop/background-work/services/fgs/restrictions-bg-start)。

Windows 安装检查应同时检查端口放行、当前网络类型、针对当前 EXE 的显式阻止规则和局域网发现规则。显式阻止优先于冲突的允许规则，只重复添加 TCP 8766 allow 不能处理此类冲突，见 [Microsoft 防火墙规则优先级](https://learn.microsoft.com/en-us/windows/security/operating-system-security/network-security/windows-firewall/rules)。修复入口只处理本程序对应规则并保留 LocalSubnet 范围。

固定一个默认启动入口；诊断显示运行版本、构建提交、数据目录与电脑身份。已有 data 目录的选择和迁移要明确保留身份及凭据，避免启动方式变化导致用户反复配对。发行包同时标明 APK、Windows、Mac 的兼容版本组。

## 分批实施和验收

| 批次 | 实施内容 | 完成条件 |
|---|---|---|
| 第一批 基础修复 | 本轮探测取消/代次、shared 退避/计数、Windows 诊断互斥/unknown | Android build、unit、lint 和 Windows build、unit 通过；未替换运行版本 |
| 第二批 可信状态与恢复 | 会话控制器、保留健康音频流、逐台接收确认、写入看门狗、停止与重连竞态 | 假网络/假输出故障测试覆盖下表，真机确认没有假供音和幽灵会话 |
| 第三批 自检与部署 | 分段自检、错误码、运行身份、防火墙检查、版本组与单入口 | 新机器按提示完成首次连接；错误能定位到具体环节 |
| 第四批 长时间与跨平台 | 锁屏、Wi-Fi 漫游、睡眠、设备故障、多电脑和其他 Android 机型 | 每个承诺场景都有真实设备记录，之后再扩大支持范围 |

建议验收场景如下。时间指标是新一轮测试目标；上述现装版本短时 USB 基线不能替代新补丁及完整矩阵的硬件结果：

| 场景 | 必须观察到的结果 |
|---|---|
| 连续 20 次 managed 开始/停止 | 无旧会话占用、无提前关麦、首尾文字核对通过 |
| shared 连续开启/关闭 20 次 | 手机立即停采；接收端正确收尾；下一次可以重新开始 |
| 1、2、3 台电脑同时共享 | 每台有接收证据，断开一台不影响其他电脑的连续音频 |
| HTTPS 健康探测失败但音频仍通 | 正常数据流继续；状态显示待确认而非强制断开 |
| Wi-Fi 短断、IP 改变、AP 切换 | 新代次及时验证；旧结果不能覆盖；恢复窗口内独立重连 |
| 接收端重启、电脑睡眠与唤醒 | 旧会话释放；恢复失败有具体原因；不无限显示正在供音 |
| 注入输出故障或移除虚拟声卡 | 手机看到音频不可用；持续上传不被误报成可听见 |
| 401、409、503 与拒绝授权 | 各自显示重新配对、占用、暂不可用；没有高频重连风暴 |
| 停止后迟到 start/health/worker 回调 | 不复活录音，不清除新会话 |
| 锁屏共享 2 小时 | 接收连续性、温度、耗电、队列与重连记录齐全 |
| 三电脑连续运行 8 小时 | 内存、线程数与延迟无持续增长；中间故障不影响其他电脑 |

记录只包含必要的错误分类、相对时刻、队列/收包统计与匿名会话标识；不写音频、令牌或识别文本。延迟使用受控回环测量 p50/p95，并单独记录首字/尾音；在确认瓶颈后再评估编码或传输协议变更。

## 代码入口

- Android：[WifiLinkObserver](../../work/phone-deck/android/app/src/main/java/com/codex/phonedeck/WifiLinkObserver.java)、[WifiLinkPolicy](../../work/phone-deck/android/app/src/main/java/com/codex/phonedeck/WifiLinkPolicy.java)、[ConnectionMonitor](../../work/phone-deck/android/app/src/main/java/com/codex/phonedeck/ConnectionMonitor.java)、[PhoneAudioService](../../work/phone-deck/android/app/src/main/java/com/codex/phonedeck/PhoneAudioService.java)、[SharedAudioBroadcaster](../../work/phone-deck/android/app/src/main/java/com/codex/phonedeck/SharedAudioBroadcaster.java)、[SharedAudioPolicies](../../work/phone-deck/android/app/src/main/java/com/codex/phonedeck/SharedAudioPolicies.java)。
- Windows：[DiagnosticsMonitor](../../work/phone-deck/windows/PhoneDeck.Server/DiagnosticsMonitor.cs)、[VoiceEngineStateProbe](../../work/phone-deck/windows/PhoneDeck.Server/VoiceEngineStateProbe.cs)、[PhoneAudioBridge](../../work/phone-deck/windows/PhoneDeck.Server/PhoneAudioBridge.cs)、[Program](../../work/phone-deck/windows/PhoneDeck.Server/Program.cs)。
- 现行产品边界：[长期规格](../../spec%20plan.markdown)、[交接与验证记录](../HANDOFF.md)、[支持矩阵](../release/SUPPORT_MATRIX.md)。
