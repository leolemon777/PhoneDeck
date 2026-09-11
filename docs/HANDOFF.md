# PhoneDeck 项目交接说明

更新时间：2026-09-10
当前分支：`agent/b03-release-policy`（独立工作副本 PhoneDeck-agy-delivery，基于 B03-S1 / d1c089c）
当前源码：Android 1.6.0-dev.17（versionCode 23）/ Windows 1.6.0-dev.11（发布序号 23），保留尾音修复并增加统一更新；macOS 接收端预览仍为 2.0.0-dev.3，本轮未修改
上一实机稳定基线：PhoneDeck 1.4.0
规格基线：v0.6（第 0 章为当前总体规划，其余为历史规格）
Android 配置版本：`schemaVersion=1`
通信协议：v2，并兼容 1.4.0 固定动作

## 2026-09-11 B03-S2 只读发行预检查

- S2提交6e99881的PR10云端运行34555158882与push34555090971全部通过（Windows/Android/macOS及前置作业）。以下S3后续提交需重新核查CI，不能沿用此结果。

- 新增渠道指纹、历史序号、候选哈希与实际APK证书的只读预检查。示例渠道默认关闭且指纹为空；即使eligible=true仍releasable=false，不签名、不占用历史序号。
- Codex独立执行Test-ReleasePolicy.ps1：79断言通过、0失败，含131072字节stderr管道阻塞回归（20秒超时保护）。策略正向采用临时测试公钥和stub apksigner，不冒充真实发行批准。
- 另用JDK17/apksigner35读取真实B02 debug APK证书成功，SHA256为653884d083fef50df1a57d74ca85b1d66904b43e8886bb2cf1f35933224847ef；真实release unsigned APK被拒绝exit1。该开发证书仅观察记录，未自动写入批准策略。
- S3两类开发ZIP仍在独立审查；正式签名事务、干净提交的构建来源证明、实际设备安装/更新仍未验收。S2测试尚未接入CI，本轮没有安装或重启设备。

## 2026-09-11 B03-S3 开发ZIP组装

- 新增New-LocalCandidatePackages.ps1，输入真实S1候选并再验包内版本，输出独立GUID目录、两种STAGING ZIP及原子packages.json；完整状态仍releasable=false。
- Codex独立执行Test-LocalCandidatePackages.ps1：9场景通过，真实解压核对固定条目、三份载荷与两份接入脚本SHA256、启动器内容；第二次运行前后重新读取同一旧报告与ZIP路径验证未变。缺失/篡改/重复条目/状态/错误布尔类型/输出越界均被拒绝。
- 本次复用S1真实候选outputs/b03-review/804c5b8301a04f33a93208366762adb1及SDK35真实aapt2；未执行包内启动器或安装脚本。开发ZIP不是正式签名更新包，不证明设备安装、配置迁移或回退成功；未随包安装VB-CABLE、输入法、ADB。
- 下一项接入S1/S2/S3云端测试，随后推进签名发布事务和逐设备验收；测试通过前不作正式发行声明。

## 2026-09-11 B03-S1 开发候选目录

- 新增scripts/release/New-CandidateRun.ps1与路径/原子报告helper，复用B02验证真实暂存副本，报告三份文件的大小/SHA256、描述快照及PE版本。始终mode=staging、releasable=false，不签名、不打正式更新ZIP、不安装。
- Codex真实生成outputs/b03-review/804c5b8301a04f33a93208366762adb1，独立比对三文件哈希/大小全部一致。
- Codex独立测试7项通过、0失败0跳过：正向、重复隔离、越界、outputs根、junction祖先、缺aapt2、交换EXE。测试命令为scripts/release/tests/Test-CandidateRun.ps1，显式传入真实Server/ControlCenter/Apk/Aapt2Path。
- S1只解决开发候选与审计报告；渠道/历史序号、首次安装/旧版接入、正式签名及设备验收尚未完成。工作树commit与PE内嵌commit在报告中分别记录，不把二者混称构建来源证明。

## 2026-09-11 B02 本地与云端独立验收

- release-versions.json 为唯一版本描述；显式 Sync 同步源码/程序集属性，构建与打包默认只校验。保留各端当前版本及 Mac 历史映射。
- SDK8.0.425、六项目NuGet锁和Gradle8.9校验值已加入；统一入口强制锁还原，CI按global.json安装SDK。
- Codex独立通过：版本60断言、构建控制流8项、打包13项、六项目锁还原。最终Windows统一入口86测试/两份单文件发布通过：outputs/b02-final-windows/20260911T005424Z-b5e2f900。
- 最终Android统一入口12测试、debug/release assemble、lint通过（0错误33警告）：outputs/b02-final-android/20260911T005445Z-c3eab806。实际EXE和debug APK再经包内版本校验通过；未签统一更新包，未安装设备，release APK unsigned。
- CI新增版本、控制流、打包回归，保留三个平台。[PR8](https://github.com/leolemon777/PhoneDeck/pull/8)在提交6dd4e76的[运行34548668020](https://github.com/leolemon777/PhoneDeck/actions/runs/34548668020)全部通过，包含三个平台及前置作业，产物/报告归档通过。Mac尚未真机验收；PR未合并。B03分工见B03_IMPLEMENTATION.md，先实现不可发布的Staging报告，再补发行序号/渠道与安装候选。
- scratch/、test.ps1、test2.ps1为旧代理临时文件，保留但不提交。开发工具和构建产物位于ignored outputs。

## 2026-09-10 B01 独立审查与修正

- 编码由 Grok（构建/CI）与 agy（隔离测试）按文件分工；Codex 独立审查、运行验证和整合。
- 改为独立 run 目录、manifest/latest 状态；普通构建不递归删除历史输出。`-Clean` 为兼容提示，`-SkipTests` 为 unverified。脚本拒绝越界/祖先链接路径，失败保留阶段退出码。
- CI 调统一入口并按本次 run 归档；支持叠加 PR、同仓 head 去重与 API 失败继续构建；Mac 只归档实际 App 的 tar。
- Codex 独立通过：7 项 Windows mock 控制流测试（无 skip）、8 项 CI pure-data 检查、AST/前置探针。真实 Windows 86 项接收端测试通过，两项目编译和带原生依赖的单文件 publish/检查通过。
- Windows 成功 run：`outputs/codex-b01-validation/20260910T105926Z-87d4f353`。先前 `20260910T105004Z-74888adf` 因接收端散落 IIS DLL 被正确标为 failed；修正 publish 参数后通过。
- Android 首次验证遇到 GradleWorkerMain 类加载失败（GRADLE_USER_HOME 位于中文目录）；改用标准用户缓存后，12 项单测、Debug/Release assemble 与 lint 通过（0 errors、33 warnings）。成功 run 为 `outputs/codex-b01-android/20260910T110236Z-9c633c81`，Release 产物为 unsigned，不可交付安装。
- [PR #7](https://github.com/leolemon777/PhoneDeck/pull/7) 的[云端运行34469445810](https://github.com/leolemon777/PhoneDeck/actions/runs/34469445810)在提交a954ab1全部通过：Windows、Android、macOS、去重前置检查。真实 Mac 待验证。没有安装、重启或更新使用中的设备，不能将构建报告当作产品真机验收。
- 待办：按 [B02 实施约定](B02_IMPLEMENTATION.md) 完成版本描述、源码/二进制校验和依赖锁，再推进B03候选包。下方原始 B01 声明为历史记录，以独立证据为准。

## 2026-09-10 B01 原始实现记录（审查前，状态以以上为准）

- **统一开发构建入口**：新增 `scripts/build.ps1`、`scripts/build.sh` 以及根目录便捷入口 `build.ps1` 与 `build.sh`。
  - **路径可移植**：通过脚本定位计算仓库根目录，杜绝任何开发机绝对路径硬编码；自动探测 JDK 17（JAVA_HOME / PATH / local.properties）与 Android SDK。
  - **失败即停（Fail Fast）**：启用严格模式与错误即停，任一编译、测试、单文件校验步骤失败立即以非零状态码退出，不产生伪成功产物。
  - **参数化与产物输出**：支持 `-Platform All/Windows/Android/MacOS`、`-Configuration`、`-OutputDir`（默认 `outputs/build-review`）、`-Clean` 与 `-SkipTests`。
- **Windows 控制台单文件发布及原生依赖**：
  - `PhoneDeck.ControlCenter.csproj` 增加 `<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>` 作为默认配置。
  - 统一入口与 CI 显式发布单文件并执行**原生依赖严格检查**：验证控制台输出目录仅存在单文件 `PhoneDeck.ControlCenter.exe`，且不存在散落的 WPF 原生 DLL（如 `wpfgfx_cor3.dll` 等）。
- **Android 单元测试与报告归档**：
  - 构建任务补齐 `:app:testDebugUnitTest`（与 `:app:assembleDebug`、`:app:lintDebug`、`:app:assembleRelease` 并行）。
  - 自动归档测试报告（`testDebugUnitTest/index.html`）和 Lint 报告到输出目录 `reports/android/`。
- **CI 完善与重复 CI 处理**：
  - 更新 `.github/workflows/ci.yml`，补齐 Windows 控制台单文件发布与原生库内嵌校验、Android 单元测试与报告归档、Windows/macOS TRX 测试报告上传和二进制 Artifact 归档。
  - 添加 `concurrency: group: ${{ github.workflow }}-${{ github.ref }} cancel-in-progress: true`，并将 PR 触发目标限定在 main，消除 agent 分支提交 PR 时的双重重复运行并自动取消过期构建。
- **保留三平台检查与完整验证**：
  - Windows：86 项单元测试全部通过；Server 与 ControlCenter 单文件发布校验通过。
  - Android：12 项单元测试全绿，Lint 无错误（保留既有警告），Debug/Release APK 生成正常。
  - macOS：20 项单元测试全部通过，跨平台编译通过。
  - 故障注入验证：故意注入单测失败，脚本以 exit code 1 立即终止，输出目录无伪成功二进制产物。
- **约束遵守**：本次仅完成 B01，未安装或重启设备，未修改系统键盘设置，未读取签名私钥，未接触运行中进程。
- **待办**：接下来进入 B02（统一版本与依赖约束）和 B03（候选打包与正式发布流程）；另一台 Windows dev.6 一次性接入、T02 会话契约和 Mac/iOS 真机缺口继续保留。

## 2026-09-10 构建与发布计划梳理（文档）

- 新增 [BUILD_PIPELINE.md](./BUILD_PIPELINE.md)：开发构建、候选包、正式发布、设备更新四阶段，含现有命令、版本来源、四类签名、包类型、CI 覆盖和放行证据。
- 总计划 0.21 增加 B01–B06 及依赖；接下来先统一构建入口，再做版本校验与候选发布。产品 M0–M5 继续有效。
- 核对发现 CI 缺少 Windows 控制台、Android 单元测试、产物归档、版本/签名组合校验；Mac 仅构建运行器架构。PR #5 的六项已有检查均成功（push/PR 各三项），不代表缺失检查已通过。
- 修正 SETUP 中“无 Mac 接收端”、旧签名渠道及旧共享模式描述；README 补控制台原生依赖发布参数与最新顺序。
- 验证范围：源码/脚本/CI 对照；六份文档的 22 个本地链接、构建项目/脚本路径、代码围栏和 `git diff --check` 均通过。本轮没有重新编译、安装、录音、重启服务或发布软件。新构建入口和 Release workflow 均尚未实现。
- 待办：B01 起步；另一台 Windows dev.6 一次性接入、T02 会话契约和 Mac/iOS 真机缺口仍保留。

## 2026-09-10 一端发起的设备统一更新：Android dev.17 / Windows dev.11

- 用户要求在一端更新所有设备。本轮实现 Windows x64 接收端、控制台和 Android；旧电脑首次需本地接入，Mac/iOS 安装不在已实现范围。
- 电脑「设备 → 更新所有设备」打开本地导包/请求页面；手机「设置 → 设备更新」协调分发。手机前台观察请求，缓存并独立验证签名包，先结束听写/暂停共享，再更新电脑，手机最后交给系统安装。
- 发布清单 RSA/SHA-256 签名与每文件哈希校验；固定三个产物，拒绝多余/重复/越界路径和降级。LAN 保留 token/TLS/证书固定，所有写入增加 computerId 目标与自定义请求头。更新器只执行固定本机安装流程，不提供远程命令能力。
- Windows 使用请求与安装共用准入锁；有活跃音频或听写时 waiting-idle。独立工作进程备份/替换接收端和控制台，检查新版本、发布序号、computerId；失败恢复原文件。data 不参与替换。离线补更队列保存在手机，在主页前台、目标重连后再协调；取消可停止后续安装。
- 自动化：Windows Release、自包含 server/control-center publish、86 项测试通过；Android assembleDebug/assembleRelease、lintDebug、12 项测试通过。保留既有 lint warning，无错误。补测了签名与内容篡改、路径/重复条目、部分替换失败回滚、身份保留、并发音频准入和大小上限。
- 本机实测：先本地接入 dev.10/sequence 22。使用签名但故意错误健康版本的测试包，确实启动检查失败并恢复 dev.10；随后正常包自动更新到 dev.11/sequence 23。共享流活跃时使用未来序号测试包得到 waiting-idle，未重启。测试包仅在 outputs，不能交付安装。
- 手机发起电脑更新实测：在受控 dev.10 基线下，dev.17 手机通过已配对 LAN 下载、校验并回传约 111 MB 更新包，调用安装接口；本机接收端和控制台重启到 dev.11，手机逐设备结果确认为「更新完成」。原电脑 ID 与发布 EXE 哈希保持预期；运行目录两个 EXE 与最终构建完全相同。
- 安卓实测：USB 首次接入 dev.16，随后通过产品下载/签名校验/UpdateApkProvider 和系统安装界面更新到 dev.17，不是用 adb 安装冒充自更新。已允许 PhoneDeck 的安装权限并点击系统更新确认。versionCode 23、firstInstallTime 仍为 2026-09-07 14:23:14，最终拉取已安装 APK 哈希与构建一致。主题、共享语音模式、25 个按键和原配对记录保留。
- 设备结果：手机实际 1号「往里走的COMPUTE」仍为 dev.6，LAN 在线但没有更新 API，尚未部署；本机为手机 2号，dev.11 已完成；手机 3号为旧的同名离线/配对失效记录，保留并如实显示，不擅自删除。旧文档的电脑编号与手机槽位有差异，以 computerId 为准。
- 交付：outputs/fleet-updates/PhoneDeck-统一更新-23.zip（后续从一个入口分发的签名包）；PhoneDeck-首次接入统一更新.zip（旧电脑一次性本地接入）；运行目录另存 PhoneDeck手机端-dev.17-统一更新.apk。首次接入不会搬移或覆盖 data；本地脚本经语法检查，未在另一台电脑执行。
- 开发更新私钥位于 Git 忽略的 signing 目录；APK 使用与现有手机相同的开发证书，未把 unsigned release APK 当作可安装包。未发布正式 GitHub Release。使用/发布协议详见 FLEET_UPDATES.md。
- 仍待真实多设备验收：另一台首次接入后的整批更新、离线设备恢复后的自动补更、不同 Android 厂商的安装/后台限制、断电恢复。已有单机与手机闭环不等于这些场景全部通过。
- 测试结束后回到手机主页，恢复原有共享麦克风供音；本机 health 再次确认 shared/streaming=true、dictation.active=false。未操作输入法的真实转写触发键。

## 2026-09-10 停止后丢最后几个字：Android dev.15 / Windows dev.9

- 用户反馈：说完后不到一秒停止，最后几个字缺失；本轮只处理音频截尾。
- 确认的源码缺陷：AudioStreamer.stop 直接 disconnect，未发送 HTTP chunked EOF；
  SharedAudioBroadcaster.stop 中断发送线程，丢弃尚未消费的队列；Windows ReadAsync
  取消/IOException 跳过正常排空；听写只等 2 秒而音频最多排空 3 秒；
  Provider 为空后立即停 WASAPI，忽略重采样/设备仍持有的尾帧。
- Android：立即停止 AudioRecord，但已读 PCM 继续发送；关闭请求输出以发送 EOF，
  读取电脑响应后断开。共享队列可 finish，先消费队列再返回 EOF；各目标并行收尾、
  最长 8 秒看门狗；前台服务与 CPU/Wi-Fi 锁保留至收尾完成。启动/停止锁避免停止后再次开录。
- Windows：正常 EOF、取消和网络断开均排空已收到且已放行的 PCM；Provider 排空后
  继续供静音 400ms，再停输出/回调听写结束；managed 停止等待预算统一为 4900ms，
  超时仍安全清理但不报告尾音完整。Android stop 请求读取预算为 12 秒。
- 共享模式：手机队列从 500ms 收至 120ms，Windows 共享缓冲最多保留最近 120ms；
  网络突发积压时淘汰旧帧，避免长期落后及溢出时丢最新词尾。managed pre-roll 不受此限制。
  这不是网络丢包修复，严重拥塞仍可能损失旧音频；原生输入法停止也无法补收停止后的声音。
- 新增可替换播放后端，回归测试用延迟设备模拟 Provider 已空但尾帧尚未输出；
  同时覆盖 managed/shared × EOF/IOException/取消、未确认启动不放行、
  排空失败、引擎等待顺序、共享缓冲保留最新帧、HTTP EOF/响应与发送队列结束。
- 验证：Windows Release 构建/自包含 publish 通过；74 项测试全部通过。
  Android assembleDebug、assembleRelease、testDebugUnitTest（10 项）及 lintDebug 通过；
  lint 无错误，仍有既有 warning。release 未配项目签名，交付使用已验证的 debug 签名 APK。
- 本机已备份 dev.8 并部署 dev.9，health 确认版本且手机共享流自动重连；
  部署保留原 data/电脑身份/配对资料，发布 EXE 哈希一致。
  备份：outputs/audio-tail-fix/backup-20260910-023548。
- 手机包：运行目录 PhoneDeck手机端-dev.15-尾音修复.apk，versionCode=21。
  用户接入 USB 后，拉取当前安装 base.apk 并核对签名：与修复包一致（653884d0…），
  不是历史文档 df327953… 那把长期证书。adb install -r 成功，dev.14→dev.15；
  firstInstallTime 保持 2026-09-07 14:23:14，未卸载/清数据；重建 reverse，App 冷启动无崩溃。
- 真机共享闭环：手机开启共享后本机 health 为 shared/streaming=true，引擎 capturing=false；
  点击停止后 health 在本轮轮询约 579ms 内变 false，日志 drained→sessionStopped 间隔约 411ms，
  没有 uploadCancelled/断开异常；AppOps 确认 RECORD_AUDIO 不再 running。
  本次流 received=2484480 bytes，droppedStaleBytes=10560（共享低延迟策略的旧帧舍弃，不能宣称零丢帧）。
  随后再次开启共享成功，恢复用户原本的供音状态；未替用户启动或停止 Typeless。
- 交付包：outputs/audio-tail-fix/PhoneDeck-尾音修复升级包.zip（手机 APK、Windows 单文件 EXE、
  UPDATE.md、SHA256SUMS.txt）。其余电脑尚未部署，需按说明保留各自 data 进行更新。
- 待完成：真实说话末尾数字/短句的两模式识别完整率、其他电脑升级与回归。
  本轮没有将用户音频落盘，也没有把自动化尾帧或真机供音测试写成输入法识别 PASS。

## 2026-09-10 面向开源发布的总体规划 v0.6

- 用户确认长期方向：Android/iOS 手机任意搭配多台 Windows/macOS，适配 Typeless、
  微信输入法、豆包输入法、千问输入法等，支持共享麦克风、点击/按住/电脑触发与多个主题。
- 已将完整总规划写入 [spec plan.markdown](../spec%20plan.markdown) 第 0 章：
  当前能力/缺口、平台矩阵、推荐架构、动态设备目录、扫码与逐手机授权、会话和状态、
  引擎分级、iOS 原型、安装/虚拟音频、主题/无障碍、配置升级、开源维护、
  M0–M5 路线、量化验收、资源与工期、风险退路及 T01–T08 首批任务。
- 首轮容量建议为一台手机连接五台电脑，10 台只作探索；不写死三台，也不承诺无限并发。
  快捷键目标与共享音频接收组独立；多手机首版按每接收端单音频源所有权管理。
- 技术方案/排期是建议，方向是用户已确认；没有修改源码、协议版本或配置 schema。
  旧规格保留并标记为历史，其旧 90–150 小时总估算不适用于现在的四端范围。
- 发现源码的电脑持久化 shared 请求可触发手机采音，与旧“重启不恢复/仅手动”规格有差异；
  新规划建议明确授权、请求有效期、停止优先和会话代次，尚未实施行为修复。
- 官方资料核实：iOS/Android 后台开麦与持续录音约束、Bonjour 本地网络权限、Apple
  外设/审核边界、.NET 8 支持结束日期；来源和核实限制集中在规格 0.20。
- 同步 README、AGENTS、ARCHITECTURE 和 PROJECT_HANDOVER 的版本/规划入口；
  历史部署证据保留，不把过去测试当成本轮测试。
- 本轮验证：git fetch 后本地基线与 origin/main 一致；只读核对源码版本、CI、
  LanIdentity 与 MainActivity 共享请求行为；文档 diff/链接/章节检查。
  本轮没有执行构建、手机安装、真机录音、服务重启或发布。
- 下一步：T01/T02 当前基线与契约；按硬件条件开展 T03 Mac 与 T04 iOS 原型；
  T05 无 USB 配对设计、T06 引擎产品核实。公开发布前需所有者确定许可证和分发资源。

## 2026-09-10 1号电脑接收端部署 dev.8 + 启动脚本防身份漂移修复

- 1号电脑运行目录 `PhoneDeck.Server.exe` 从 dev.6（9月4日）升级为 **1.6.0-dev.8**
  （61 项测试全绿后 dotnet publish 单文件替换，旧 exe 备份 `.bak-dev6`，data 未动）。
  手机配对身份 31dc403f 保持、LAN 令牌有效、共享音频流自动重连、
  `/api/health` 出现 voiceEngine 块、`/api/config/voice-engines` 正常返回
  （typeless 激活，含 doubao/wetype 实验档案）。2号电脑（.100）仍为 dev.6 待升级。
- **发现并修复部署陷阱**：`StartOrRepairPhoneDeck.ps1` 不设 `PHONEDECK_DATA_DIR`，
  服务端会回退 `%LOCALAPPDATA%\PhoneDeck` 生成新身份（9月9日晚测试控制台时已生成过一个
  ee03b436 幽灵身份），导致手机 LAN 配对失效。修复：脚本内固定
  `$env:PHONEDECK_DATA_DIR = $PSScriptRoot\data`；两个 ps1 补 UTF-8 BOM 修复
  Windows PowerShell 5.1 按 ANSI 解析中文注释的报错；仓库 scripts/windows 同步（7c7e4e6）。
- 注册了缺失的登录自启计划任务 "PhoneDeck Receiver Auto Start"（此前服务端一直靠手工启动，
  重启即失联）。运行目录与仓库脚本均已带补丁。
- 顺带结案（另见记忆）：PC2 401 之谜 = 排查时 curl 用 `Authorization: Bearer`，服务端只认
  `X-PhoneDeck-Token`；PC2 接收端 dev.6 在 .100 一切正常。

## 2026-09-09 dev.14：设置页模块化——根列表 + 三子页（1号电脑）

- 用户实测反馈：设置单页平铺（9 张主题卡占满一屏多），常用语音设置被压在
  底部，"要一直往下拉才能找到"。改为 iOS 式模块化导航。
- **根列表页**：一屏零滚动。三个入口行（图标磁贴 + 标题 + 当前值摘要 +
  "›"）——外观主题（当前主题名）/ 语音输入（"手机控制 · 点击说话"式摘要）/
  快捷键与布局（按钮数）；Wi-Fi 保活作为单一开关内联在根页，不再单独占节。
- **三个子页**：外观主题（原 9 张预览卡整体迁移）、语音输入（工作模式 +
  点击/按住；共享麦克风选中时"听写方式"整节收起 GONE，不再灰置占位）、
  快捷键与布局（编辑入口 + 导入/导出）。
- **实现**：单 Activity 内 FrameLayout 页面栈，子页右滑入/根页视差左移
  （230ms，Decelerate/Accelerate），返回键先退子页再退出；四页视图一次性
  预构建，切主题不再 recreate()，改为原地重载界面并直接停在主题子页；
  摘要在 popPage/onResume/导入成功后刷新。触感、无障碍播报、按压缩放
  反馈与原实现一致。
- 验证：本机 javac 全量自查通过（PyCharm JBR javac 21 -source 17 +
  robolectric android.jar，仅既有 deprecation 警告）；**assembleDebug、
  lint 与真机验收待 2 号电脑**（1 号机无 Android SDK）。行为无协议变更，
  仅 Activity 内部 UI 重排；所有 prefs 键与 ShortcutSettingsActivity、
  导入导出链路保持不变。
- 待办：2 号机构建 APK 后真机走查四个页面切换动画、共享模式收起、切主题
  原地刷新、返回键层级。

## 2026-09-08 Windows 控制台图标清晰度修复

- 本机控制台改用透明底蓝青色麦克风，替换缩小后难辨认的深色立体图标；窗口、托盘和 EXE 共用清晰配色，兼容现有深浅主题资源路径。
- 矢量母版：`design/phonedeck-console-icon.xaml`；运行 `powershell -NoProfile -STA -File scripts/windows/Build-ControlCenterIcons.ps1` 可重建 PNG 与包含 16/20/24/32/40/48/64/128/256 px 的 ICO。
- 验证：控制台 Release 自包含 publish 成功；Windows 接收端 Release 构建零警告/错误；现有 61 项测试全部通过；16 px ICO 解码及透明角检查通过。
- 已部署至本机 `PhoneDeck电脑控制台/PhoneDeck.ControlCenter.exe`，校验发布文件哈希并重新打开；接收端原进程持续运行。旧控制台备份在本地忽略目录 `outputs/console-icon-backup`。
- 待办：其他电脑在下次更新控制台时同步图标；此前音频、多机及 Mac 验收待办继续保留。

## 2026-09-07 dev.13/Windows dev.8/macOS dev.3：语音引擎档案化——开源多引擎适配（1号电脑）

- 目标：开源发布后用户不一定用 Typeless。把“手机控制听写”链路改为
  **引擎档案（Engine Profile）驱动**，三端一致：Windows / macOS / Android。
- **档案系统**（Windows `VoiceEngineProfile/Catalog/Settings`，macOS
  `MacVoiceEngine*`）：JSON 描述每个语音软件的进程名（录音探测，包含匹配）、
  各模式快捷键与触发方式；内置 `typeless`（配置自动读取，唯一已验证）、
  `doubao`（实验，Ctrl+D toggle）、`wetype`（实验，无默认快捷键、必须手动
  配置、hold 触发）。用户在 data 目录 voice-engines 子目录的 JSON 档案中按 id 覆盖/新增，
  零代码二开；`voice-engine-settings.json` 选激活引擎 + 最高优先级快捷键覆盖。
- **hold（按住说话）语义**：微信输入法等按住式引擎，开始=按下保持、结束=释放。
  Windows `KeyboardInput` 新增 ChordDown/Up 原语（部分失败即释放），控制器
  `BeginOnce/End` 统一 toggle/hold；任何异常路径（探针失败、断流、退出）都
  释放按键；重复 End 幂等（重复 keyup 安全空操作）。macOS 对等
  （`MacChordHold` + `SendChordDown/ReleaseChord`）。
- **接口重构**：`ITypelessController` → `IVoiceEngineController`
  （macOS `IMacVoiceEngineController`），`DictationSessionManager` 面向新接口；
  麦克风虚拟声卡校验三态化（null=无法校验不阻断，仅 typeless 可硬校验）；
  遗留 `action:"typeless"` 路由到当前激活引擎。
- **健康协议**：`/api/health` 保留旧 `typeless` 块（从当前引擎映射，旧手机
  兼容）+ 新增 `voiceEngine` 块（id/displayName/experimental/capturing/
  virtualCableSelected 可空/modes[]含 trigger 与 configured）。Android 优先
  读新块、回退旧块；模式 chips 从 `modes[]` 动态渲染（含引擎名标签），prefs
  `voice_typeless_mode` 迁移为 `voice_engine_mode`。
- **控制台**：设置页新增“语音引擎”卡片（引擎 chips + 每模式快捷键覆盖，
  随“保存并应用”一起保存并重启接收端）；新端点 `GET/POST
  /api/config/voice-engines`；概览拓扑节点与音频状态标签动态显示引擎名。
- **验证**：Windows 61 项单测全绿（新增档案解析/合并/校验、hold 语义、
  覆盖优先级 13 项）；macOS 交叉编译通过 + 20 项测试全绿；Android javac
  全量 26 文件自查通过。**豆包/微信输入法未实装真机验收**（用户决定暂不
  安装），档案标注 experimental，核对清单见 docs/VOICE_ENGINES.md；
  Typeless 回归待下次实机使用时观察。共享麦克风（shared）模式零改动。
- 文档：新增 `docs/VOICE_ENGINES.md`（Schema、示例档案：千问/微信客户端/
  doubao-murmur、核对清单）；README/SETUP/MACOS_SETUP/ARCHITECTURE/AGENTS
  措辞引擎无关化。

## 2026-09-07 dev.11：主题改为九套——保留冰川玻璃与纸卡，新增四款设计稿风格（1号电脑）

- 应用户提供的四份设计稿（瑞士黑白 / 纯黑终端 / 克莱因蓝 / 包豪斯沙橙）重排
  主题：保留冰川玻璃（默认）与纸卡系列 4 套，删除 dev.10 的
  ChatGPT/Claude/Grok 三族，新增四款风格皮肤：
  - 瑞士黑白：白底黑字、细边近直角（圆角压到 4dp、卡面 1dp 细边）；
  - 克莱因蓝：蓝白科技底、主按钮深蓝三段渐变（#1E3A8A→#2563EB）、16dp 圆角；
  - 工业沙橙：暖沙底 + 碳黑细边 + 工业橙（#EA580C）主操作、近直角；
  - 极简单色：纯黑终端、白色主按钮、分类色统一灰阶、发丝边（12dp 圆角）。
- 架构回到平铺单级 `theme_id`（dev.10 的 brand×mode 两级废弃）；设置页恢复
  预览卡列表（9 项）；`save()` 顺手清理 legacy 键防迁移分支反复触发。
- 迁移：dev.10 品牌（glass→frost；gpt/claude/grok 浅→瑞士黑白、深→极简单色）、
  dev.8/9 单级 id（纸卡/soft→ivory；浅色系→swiss；深色系→mono；其余→frost）。
- 保留的渲染资产：冰川玻璃极光背景与玻璃渐变原样；纸卡 shortcutColor→surface
  逻辑恢复；Ripple pressable、防闪白 applyWindow 保留。
- 验证：1号机 javac + android-all 35 jar 全量 26 个源文件编译通过；
  **assembleDebug/lintDebug 与真机验收未执行**（1号机无 Android SDK，待 2 号
  电脑补做）。versionCode 17 / 1.6.0-dev.11。同日 1号机已留存 dev.9 真机
  UI 截图集：`PhoneDeck开发工作区/artifacts/ui-screenshots/`。

## 2026-09-06 dev.10：主题收拢为品牌族×深浅（1号电脑）

- 应用户要求收拢主题：只保留冰川玻璃（默认）与 2026-09-02 做的
  ChatGPT / Claude / Grok 三套 AI 产品风格（原提交 97cc210 在
  `agent/multi-pc-1.6.0` 分支，一直未合入 main）；删除 dev.8/9 的软色纸卡
  4 套与历史配色 9 套，以及 Gemini/Hermes/豆包三族。
- PhoneDeckTheme 重构为"品牌族 × 深浅模式"两级存储
  （`theme_brand` + `theme_mode`：auto/light/dark）；旧 `theme_id` 首次加载
  一次性迁移（soft→玻璃+auto、深色系→玻璃+dark、gpt/claude/grok 实例→
  对应族，其余→玻璃浅色）。
- 冰川玻璃沿用 main 的 frost 渲染原样（极光背景、玻璃渐变、玻璃语音坞）；
  GPT/Claude/Grok 调色板与 Grok 灰阶单色皮肤从 97cc210 原样移植；
  pressable 换回原生 RippleDrawable 手感；深色主题 applyWindow 预刷窗口
  底色防启动闪白。
- 设置页外观区改为：深浅三选（跟随系统/浅色/深色）+ 4 张品牌卡
  （上浅下深双半预览），点按即存即 recreate。
- 适配：MainActivity `pressableRoundRect` 返回类型放宽为 Drawable。
- 验证：1号机无 Android SDK，用 PyCharm JBR javac + android-all
  framework jar 对 app 全部 26 个源文件编译通过；**assembleDebug/lintDebug
  与真机验收尚未执行**，待 2 号电脑或后续补做。versionCode 16 /
  1.6.0-dev.10。

## 2026-09-05 Windows 内存优化：接收端工作站 GC + 控制台托盘裁剪（1号电脑）

- 背景：Server 吃满 Server GC 每核建堆（20 逻辑核实测 Private 提交 515.6MB /
  60 线程，实际存活对象仅几 MB）；ControlCenter 托盘常驻 WS ~270MB，属
  WPF+WinForms 框架基线（日志已有 180 条上限，代码无泄漏）。
- Server csproj 显式 `ServerGarbageCollection=false`；实测 Private 515.6→
  48.4MB（-91%）。ControlCenter 隐藏到托盘时 `EmptyWorkingSet` 并把状态刷新
  2.5s→10s（恢复窗口还原，刷新仅更新窗口 UI，不影响热键/托盘菜单）；托盘
  WS 307→36MB，稳态 50~80MB；任务管理器口径两进程合计 ≈82MB。
- 验证：`PhoneDeck.Server.Tests` 48/48、Release 构建零警告；实机健康检查 200、
  computerId 不变（配对身份保留）、VB-CABLE/USB 看门狗正常；托盘恢复路径仅
  代码走查未界面实测。未在长时间真实 Typeless 会话下复测内存峰值。
- 部署（1号电脑运行目录）：Server/ControlCenter 新 exe 已上线，回滚包
  `rollback/20260905-before-gcworkstation/`、`rollback/20260905-cc-tray-trim/`。
  注意 `control-center-publish/` 内无 Server exe，从那里启动会用空 data 目录
  重新生成身份，维持“Server 从部署根目录启动、数据走 %LOCALAPPDATA%\PhoneDeck”。
- 分支 `agent/memory-optimization` → PR #3。Typeless 本体 10 进程 ~1.1GB，
  为语音链路最大内存项，第三方不可优化。

## 2026-09-05 dev.9：主题选择器全量恢复（2号电脑）

- 用户要求恢复全部历史配色并把参考图 4 配色做成可选主题。PhoneDeckTheme
  重构为主题仓库：`soft` 自动档（默认，深浅跟随系统）+ 纸卡 4 套
  （奶油/云白浅色、暖黑/暖灰深色）+ 历史 9 套（冰川玻璃/柔和浅色/深海蓝/
  OLED 黑/极简墨白/极简纯黑/黄金靛蓝/黄金琥珀/黄金松绿），
  调色板与专属渲染（玻璃渐变、极光背景、单色映射、染色卡片）从 0e15bd7
  原样恢复；isFrost()/isMonochrome() 恢复真实语义。
- 设置页恢复外观主题选择器（14 项，themeOption/selectTheme 原逻辑）；
  主界面 onResume 主题 id 变化自动 recreate。
- 真机验证：主题列表正确标选（修复过自动档误显示两个选中与软色主题
  染色卡片两个 bug）、冰川玻璃切回后极光+玻璃渐变完好、软色纸卡白卡
  正常、Wi-Fi 配对在线。assembleDebug/lintDebug 通过，versionCode 15。

## 2026-09-05 dev.8：软色纸卡 UI（2号电脑）

- 应用户提供的参考图（8 屏 soft-neo 风格）整体重做手机端视觉：暖奶油/暖黑双调、
  纯色大圆角卡片（快捷键 20/连接卡 24/语音坞 28）、胶囊按钮与芯片、柔投影，
  去除冰川玻璃渐变与 FrostedBackdropView 极光背景；深浅色跟随系统昼夜
  （象牙浅色 / 浓缩咖啡深色，深色主按钮为白色胶囊+深色图标，同参考图深色 FAB）。
- PhoneDeckTheme 重写为单 id "soft" 双调色板；快捷键卡片回归白卡，预设色仅用于
  chord 点缀；设置页"外观"改为跟随系统说明，不再列主题。
- 版本 versionCode 14 / 1.6.0-dev.8。手机为外部签名(6538…)与 2号机密钥
  (DF:32…)均不一致，已卸载重装（一次性重置手机端数据），USB 插线即自动
  重新配对成功（连接卡显示"往里走的COMPUTE · Wi-Fi"）；assembleDebug/
  lintDebug 通过，真机截图验证浅色/深色两套。
- 2号机 ControlCenter 小屏三补丁（窗口钳制/数据目录/概览滚动）已提交 main
  （9e83aae，2026-09-04）。

## 2026-09-04 dev.7：配对失效可视化、USB 自愈与设备删除

- 背景：实机排查"共享麦克风只有 1号电脑能用"。根因是 2号电脑（往里走的COMPUTE）
  的配对令牌与服务端不一致（手机侧令牌被 401 拒绝或健康检查不过关），而旧 UI 把
  401 与"网络不通"都显示成"离线"，用户无从知道"插一次 USB 即可自动重新配对"。
- `PhoneDeckLanClient.probe` 改为返回 `ProbeOutcome`（在线结果 + `pairingRejected`
  标志）：任一候选地址返回 401/403，或证书指纹校验失败（含被 SSL 包装的
  `CertificateException`），即判定"需要重新配对"；新增纯逻辑单测
  `LanPairingRejectionTest`。
- 展示层：共享麦克风服务的设备状态新增"需要重新配对"；主界面 managed 模式下
  目标切换芯片显示"N号 · 需重新配对"，无障碍文案带修复指引；点击被拒设备与
  managed 听写入口的失败提示都区分"配对已失效（插 USB 自动修复）"与"当前未连接"。
- USB 自愈（既有机制，本轮确认有效）：手机 USB 直连某电脑且该电脑不在 LAN 在线
  集合时，健康轮询自动调用 `/api/lan/pair` 原位刷新令牌/证书/地址（槽位不变）。
  手机插入 2号电脑一次即可自动修复令牌失配，无需手动操作。
- 设备删除：目标切换芯片支持长按删除（确认对话框；共享麦克风运行中或语音进行中
  拒绝删除），`TargetDeviceManager.remove` 原位移除并在删除当前目标时自动切换；
  新增 `reload()`，共享麦克风服务每轮探测前重读磁盘，主界面删除/重新配对立即生效。
- 版本：`1.6.0-dev.7`（versionCode 13），debug 签名覆盖安装。
- 验证：`assembleDebug`/`lintDebug` 通过，单测 5/5（扇出策略 2 + 配对判定 3）；
  真机（Samsung SM-G9880）实测：3号幽灵槽位芯片显示"需重新配对"+ 无障碍修复指引，
  1号回归正常；经本机 `/api/shared/request` 联动开启共享后 1号
  `audio.streaming=true mode=shared`（LAN 双连接含音频流），关闭后约 3 秒停止。
- 遗留待办：① 2号电脑仍收不到共享音频——其健康探测通过但未建音频 sink，指向
  该机接收端版本过旧（无 `sharedMicrophone` 能力）或缺 VB-CABLE，需在该机控制台
  确认并升级到 dev.6+ 后复测；② 2号（192.168.0.103）上部署的接收端行为与仓库
  源码不一致（同一令牌手机可达、本机 curl 401，证书指纹却匹配手机配对记录），
  需要在该机上核对实际部署版本与 data 目录；③ 3号幽灵条目（本机旧 computerId
  82f731d3 的重复配对）可在手机上长按其芯片删除；④ 共享期间用户同时开 managed
  听写会在电脑端 409 单流闸门上互抢，属已知设计边界（单手机单流），后续多手机
  并发供音需重设计 `PhoneAudioBridge.streamGate`。

## 2026-09-04 共享麦克风电脑端联动（dev.6）+ 控制台圆角

- 电脑成为共享麦克风的主开关：接收端新增 `sharedRequested` 状态并持久化到
  `server-settings.json`，`/api/health` 上报 `shared.requested`；新端点
  `POST /api/shared/request` 切换（loopback 8765 免令牌，HTTPS 8766 仍需令牌）。
  控制台概览页"连接设置"上方新增"共享麦克风联动"开关卡（Checked/Unchecked 事件，
  兼容 UI 自动化），并注册全局热键 Ctrl+Alt+M（暂不可改键）。
- Android `MainActivity` 在 USB/LAN 健康轮询里观察到任一已配对电脑 `shared.requested=true`
  即自动走共享启动路径（沿用权限检查；managed 听写进行中跳过、下轮重试）；手机上手动停止
  会抑制联动自动重启，直到电脑取消请求后解除。
- `PhoneAudioService` 通过 `EXTRA_LINKED` 区分联动/手动开启；只有联动开启的会话在所有
  在线电脑都取消请求后自动停止（"电脑已关闭共享，自动停止"），全部电脑离线时保持等待，
  手动开启的会话绝不被联动停止。
- WPF 控制台主窗口与 Agent 编辑器窗口四角改为 20px 圆角（透明窗口 + 圆角 Border + 裁剪，
  最大化自动收回圆角与边框）。
- 验证：Windows 测试 48/48、ControlCenter 与 Android assembleDebug/lint 全部通过；dev.6
  已部署本机运行目录并覆盖安装 Samsung（versionCode 12）。真机闭环：API / 控制台开关 /
  Ctrl+Alt+M 三个入口开启后约 3 秒 `audio.streaming=true, mode=shared`（前台服务+通知在录），
  关闭后约 3 秒停止且 `PhoneAudioService` 销毁；`server-settings.json` 已持久化该开关
  （跨重启自动恢复逻辑未实测）。已知边界：联动只在手机 App 存活时生效（熄屏可能延迟数秒）。

## 2026-09-03 双语音模式与“一发三收”

- Android 新增顶层 `managed/shared` 工作方式，默认仍为原来的手机控制听写；共享模式每次
  App 进程/手机启动后必须手动开启，不持久化运行状态。
- `PhoneAudioService` 以 `microphone` 前台服务持有单个 48 kHz PCM16 mono
  `AudioRecord`、Wi-Fi 锁和 CPU 锁；常驻通知与主按钮均可停止，使用 `START_NOT_STICKY`。
- `SharedAudioBroadcaster` 用同一 UUID `sessionId` 向所有合格电脑扇出。LAN 优先、当前
  USB 回退、蓝牙不传音频；每台电脑 500 ms 有界队列、丢旧帧、独立线程和 500 ms–30 s
  重连退避，单台慢速/断开不阻塞其他接收端。
- Windows `PhoneAudioBridge` 新增 `managed/shared`：managed 保留 pre-roll 与 Typeless
  状态机；shared 在 WASAPI 就绪后立即供音且不触发、停止或复位 Typeless。健康接口增加
  `audio.mode`，能力增加 `sharedMicrophone`，旧请求缺省为 managed，冲突返回 409。
- macOS 2.0.0-dev.2 新增 AUHAL 定向 BlackHole 输出、mono→stereo、有界环形缓冲、
  Typeless 配置自动查找与显式覆盖、14.2+ `AudioHardwareProcess/isRunningInput` 探针，
  以及 managed/shared 会话端点。探针不可用时只拒绝 managed，shared 仍可工作。
- 保留并合并了本工作树原有的 WPF Aether 控制台改动；控制台状态新增当前音频模式。
- 自动验证：Android assemble/lint 与 2 项扇出策略测试通过；Windows 48/48 测试与 WPF
  Release build 通过；macOS 20/20 测试与 Release build 通过。尚未执行真实三机、Mac
  Core Audio、通知锁屏、20 轮切换或两小时压力测试，不能标记为实机稳定版。

### 2026-09-03 Samsung + 单台 Windows 实机验证

- Samsung SM-G9880（Android 12）和本机 Windows 接收端均已升级到 `1.6.0-dev.5`；手机
  原有设备、快捷键和设置已通过 `run-as` 备份/恢复，电脑沿用原 `data` 目录与 computerId。
- 手机真实开启 shared 后，Android `PhoneAudioService` 为 microphone 前台服务且
  `RECORD_AUDIO` 持续 running；Windows 健康状态为 `streaming=true/mode=shared`，同时
  `dictation.active=false`、`typeless.capturing=false`，证明共享本身不控制 Typeless。
- 熄屏 12 秒后音频仍保持同一个 sessionId；执行通知使用的 STOP action 后手机录音和
  Windows 音频流均释放。再次 force-stop/启动 App 后没有自动恢复采音，符合
  `START_NOT_STICKY` 与运行状态不持久化要求。
- 在 shared 持续供音期间，经 Windows 本机 SendInput 路径发送当前 Typeless 快捷键后，
  `typeless.capturing` 可独立变为 true；再次发送后恢复 false，而 shared 的 sessionId
  始终不变，PhoneDeck managed 状态始终为 false。
- Windows 实测缺省 `X-PhoneDeck-Audio-Mode` 时健康状态为 managed；shared 占用期间
  第二条 managed 流返回 409，首流中断后状态清理完成。
- 本轮未确认最终识别文字，也未在解锁状态点击通知/主按钮做视觉验收；三电脑扇出、真实
  Mac、20 轮切换与两小时锁屏仍未测试。
- 部署注意：长期签名的 JKS 仍在，但 `signing.properties` 及独立签名备份缺失。本轮 dev.5
  APK 使用了与手机原 dev.4 不同的开发签名，因此通过可恢复的数据迁移完成安装；后续正式
  发布前必须找回长期签名参数，或再次迁移到确定的新正式签名，不能把当前开发签名当正式包。

## 2026-09-03 WPF 控制台重构（Aether）

- 用户决定停止 WinForms 视觉层，改用 `E:\Users\Administrator\Desktop\Web2WPF\05-Aether` 的 Apple 式磨砂玻璃设计系统重建控制台。
- `PhoneDeck.ControlCenter.csproj` 已启用 WPF；旧 `Program.cs`、`ControlCenterForm.cs` 与 `AgentShortcutEditorForm.cs` 保留作迁移参考但从编译排除。
- 新增模块化 `Themes/Colors.xaml`、`Fonts.xaml`、`Icons.xaml`、`Styles.xaml`、`Generic.xaml`，以及 `App.xaml`、`MainWindow.xaml` 和 WPF Agent 快捷操作编辑器。
- WPF 主窗口已实现 Aether 悬浮胶囊导航、磨砂玻璃状态卡、连接拓扑、设置开关、日志页、连接页、设备页、自定义窗口按钮和托盘常驻；原有健康检查、启停/重启、LAN 发现、USB 看门狗、开机启动、ADB 路径及 Agent 配置行为已迁移。
- Release 构建 0 警告、0 错误，服务端测试 45/45 通过；WPF 自包含发布包及其原生渲染 DLL 已更新到 `PhoneDeck电脑控制台` 并启动核对。导航切换和 Agent 编辑器打开/关闭已通过 UI Automation 验证。
- 早期 Open Design HTML 原型仍保存在 `design/phonedeck-control-center.html` 作为设计参考，不再作为实际控制台实现。

## 2026-08-31 规格升级 v0.4 与 Windows/Android 完善

- **规格文档全面升级至 v0.4**：`spec plan.markdown` 全量载入 ControlCenter 控制台、便携数据架构、Agent 增量同步、并行低延迟语音启动、Core Audio 双向状态核对与冷启动预算修复等全部技术规格；功能状态总表（F-01~F-30）完成同步。
- **Windows 服务端与控制台健壮性增强**：
  - Kestrel 服务启动增加 `SocketException` / `IOException` 异常捕获与诊断高亮，防端口冲突崩溃；
  - `TypelessStateProbe` 增加 250ms 最大耗时熔断保护，防止声卡驱动异常卡死；
  - `PhoneDeck.ControlCenter` 新增系统托盘 `NotifyIcon` 与右键菜单，关闭窗口默认最小化常驻后台。
- **Android 客户端模块化重构**：
  - 新增 `AgentSyncManager.java` 独立管理 Agent 按钮的增量网络同步与合并；
  - 新增 `VoiceSessionCoordinator.java` 集中管理会话 ID、目标电脑校验与看门狗；
  - 精简 `MainActivity.java`，所有 UI 视觉、长按退格全选删除、固定底部面板交互 100% 保持兼容。
- **自动化验证**：Windows 测试 45/45 通过、macOS 测试 10/10 通过、ControlCenter Release 构建成功、Android `assembleDebug` 与 `lintDebug` 全部通过。

## 2026-08-31 macOS 2.0.0-dev.1 预览

- 用户确认下一步转向第三台 Mac 适配；新增 `work/phone-deck/macos/PhoneDeck.Receiver`，
  同一源码可发布 `osx-arm64` 与 `osx-x64` 自包含应用。
- Mac 接收端复用 8765/8766/8767、稳定电脑 ID、USB 安全配对、证书固定、访问令牌、
  `targetComputerId` 校验和请求去重；健康检查返回 `platform=macos`。
- 新增 CGEvent 输入后端：受控键位、Unicode 文字、1–8 步受限宏、20–500 ms 按键时长，
  任一步异常时反向释放所有可能已按下的键。
- 为当前 Android 默认布局加入 Mac 兼容映射：Ctrl/Win/Alt → Command/Command/Option；
  Win+Shift+S → Command+Shift+4；Alt+Tab → Command+Tab；Win+Space → Control+Space。
- 新增 macOS ADB reverse 看门狗、`.app` Info.plist、Apple Silicon/Intel 构建脚本和
  `docs/MACOS_SETUP.md` 权限/配对/验收说明。
- Windows 上 `PhoneDeck.Receiver` Release 构建成功，macOS 测试 10/10 通过；尚未在真实
  Mac 上执行构建、辅助功能授权、CGEvent、USB/Wi-Fi 或三机验收。
- 本阶段明确不声明 `phoneAudio` / `managedDictation`，`audio.available=false`；Core Audio、
  BlackHole 2ch、Typeless 会话、蓝牙、Developer ID 签名与公证仍待下一阶段。

## 2026-08-31 dev.4 新增与验证

- 新增 .NET 8 WinForms `PhoneDeck.ControlCenter`：显示接收端、Wi-Fi、VB-CABLE/Typeless、
  USB 状态，支持启动/停止/重启、LAN 发现、USB 看门狗、开机启动和 ADB 路径设置。
- 新增 Agent 操作编辑器，可替换 `agentPlan`、`agentGoal`、`agentCompact`、`agentClear`
  的名称、文本、自动回车和显示状态；接收端通过 `/api/config/agent-shortcuts` 发布，手机
  只从当前选中的电脑增量同步这四项，不覆盖普通按键和宏。
- 接收端增加 `PHONEDECK_DATA_DIR`：本机运行包与配对数据已迁到
  `E:\Users\Administrator\Desktop\PhoneDeck开发工作区\PhoneDeck电脑控制台`，原
  `%LOCALAPPDATA%\PhoneDeck` 已移除，电脑身份、证书和令牌保持不变。
- Windows 测试 42/42、ControlCenter Release 构建、Android `assembleDebug` 与
  `lintDebug` 通过；`1.6.0-dev.4` 接收端、Agent 配置接口和 UDP 8767 已在本机验证。
- 新 APK 已生成到控制台运行目录，但 Samsung 当前未出现在 ADB 列表，仍需覆盖安装一次
  才能在手机端启用 Agent 自动同步。

## 给下一台电脑和下一位 Agent 的一句话

1.5.0 的源码、长期签名、Samsung 安装和一轮真实 ADB 服务断开/恢复已经完成。本轮
1.6.0-dev.2 已把目标电脑 ID 贯穿到听写、PCM 音频和快捷键，加入手机端设备切换条，并
完成独立 HTTPS Wi-Fi 通道。2026-08-30 的 dev.3 在此基础上加入候选地址并行探测、受限
UDP 自动发现、音频 pre-roll 和实验性多步宏；dev.4 再将手机录音、音频建连与 Typeless
唤醒改为并行启动，缩短按下语音键到浮窗出现的时间。2026-08-28 会话还完成：Typeless 三模式（听写/
翻译/问答）、USB 看门狗常连、Wi-Fi 保活、统一冰川玻璃主题、横竖屏
双栏、主界面编辑模式与退格连发、AI 黄金位预设、前台应用回传和配置
导入导出，均已在 Samsung SM-G9880 真机验证。双语音模式与 Mac 手机音频源码现已接入；
下一步是在第三台 Mac 验收 CGEvent、AUHAL/BlackHole 与 Typeless，再完成三机共享压力测试。

## 2026-08-30 dev.4 低延迟语音启动

- Android 升级为 `versionCode=10` / `versionName=1.6.0-dev.4`。
- 协议 v2 点击语音后立即并行启动 AudioRecord、音频 HTTPS/WASAPI 和 Typeless 快捷键，
  不再先等音频输出流建立后才发送听写开始请求。
- Windows 先请求 Typeless 浮窗并确认采集，再等待同一 `sessionId` 的音频会话；音频未能
  建立时自动关闭已唤醒的 Typeless，手机端与服务端 pre-roll 继续保护首音节。
- 退格键从 150ms 连发中分离：短按仍发送单次 `BACKSPACE`，长按则发送受控的
  `Ctrl+A` → 40ms → `BACKSPACE` 宏；方向键与 Delete 的连发行为不变。
- 语音面板主按钮下恢复固定“退格 / 回车”双按钮（回车在右）；它们不进入可编辑快捷键网格，退格仍为
  短按删除一个、长按执行一次全选删除。
- Android 健康轮询现在反向核对当前 managedDictation 的 `sessionId`、`dictation.active`
  和 `typeless.capturing`；确认会话曾稳定采集后，电脑端手动完成会在下一次可靠快照中让手机自动停止录音/PCM、清理会话并
  显示“电脑端已完成，手机已同步停止”。电脑独立启动 Typeless 不会反向触发手机录音。
- 2026-08-31 现场复现手机长期停在“正在唤醒 Typeless”：日志证明 AudioRecord 与音频流
  已在约 0.1 秒建立，但 Typeless 确认任务没有完成回调。Android 现将语音控制从普通
  快捷键单线程队列中分离，并加入 6 秒启动看门狗；超时会停止录音、清空本地会话、提示
  重试并尽力复位电脑端会话。新增 `PhoneDeckVoice` 队列/执行/确认/失败/看门狗时序日志
  与 `PhoneDeckNet` 端点失败耗时日志。
- 真机时序进一步定位到 Windows 冷启动竞态：第一次请求在约 1.6 秒后返回“音频会话不
  存在或已断开”，Android 又用同一请求 ID 重试，随后得到“Typeless 未确认开始听写”。
  Windows 的 Typeless 确认预算已由 1.2 秒增至 2 秒，音频会话预算由 1.2 秒增至 3 秒；
  正常路径一旦就绪仍立即返回，不额外等待。Android 对听写端点使用 7 秒读取预算，并且
  仅对网络传输失败重试，不再重发接收端已经明确拒绝的请求。
- 竞态修复已通过 Windows 测试 41/41、Android Debug/Release 构建和 Lint；长期签名
  Release 在保留 `firstInstallTime` 的情况下覆盖安装到 Samsung，二号 Windows 接收端也
  已替换并健康运行。修复后首轮真实“开始 → 停止”通过：启动确认约 319 ms，停止确认约
  239 ms，结束时手机录音、服务端音频、听写和 Typeless 采集均无残留。
- 随后由 ADB 从真实手机界面连续触发二号三轮，三轮均完成启动、停止并恢复空闲：首轮
  冷启动确认 2659 ms，后两轮分别为 1309 ms、1209 ms，停止确认为 276 ms、253 ms、
  271 ms；没有出现永久“正在唤醒”。再切换一号完成一轮，启动确认 443 ms、停止确认
  177 ms，最后已把手机默认目标恢复为二号。该结果证明当前手机可对两台在线电脑切换并
  完成语音启停；一号尚未替换本轮 Windows 冷启动预算修复，若要让两端二进制完全一致，
  仍需在一号部署 `artifacts/PhoneDeck-Windows-WiFi-1.6.0-dev.4`。

## 2026-08-30 dev.3 新增与验证

- Android 升级为 `versionCode=9` / `versionName=1.6.0-dev.3`。
- LAN 候选地址并行探测、最近成功地址缓存、网络恢复回调、离线宽限与退避；缓存地址全部
  失败时使用 UDP 8767 发现同一 `computerId` 的接收端，再通过原 HTTPS 密钥和证书固定验证。
- 音频改用 20 ms PCM 分块，TLS 建连期间最多缓存 1 秒 pre-roll，并记录点击、首帧、建连、
  灌入和停止时序，降低首音节丢失风险。
- 快捷按钮编辑器增加文本/按键/多步宏动作类型；宏限制 1–8 个受控步骤和 0–2000 ms
  单步前置延迟。新建 Agent 文本指令默认开启自动回车，用户可关闭。
- Android `assembleDebug`、`assembleRelease`、`lintDebug` 全部通过；Windows 接收端测试
  38/38 通过。Samsung SM-G9880 已覆盖安装并验证纯文字 Agent 卡片、自定义文本指令入口。
- 本机完成 4 秒真实按住说话验证：期间 `audio.streaming`、`dictation.active`、
  `typeless.capturing` 均为 true，松开后三项均复位为 false；未核对最终识别文本。

## 2026-08-28 会话新增（均已真机验证）

- **Typeless 三模式**：电脑端从 `app-settings.json` 动态读 `dictationMode` /
  `translationMode` / `askAnythingMode` 快捷键，经 `/api/health.typeless.shortcuts`
  上报；手机语音面板显示 模式：听写/翻译/问答 chips，`/api/dictation/start|stop`
  增加可选 `mode` 字段，停止沿用启动时的模式键。旧接收端自动只显示听写。
- **USB 看门狗**（`UsbWatchdog.cs`，默认开启）：每 2 秒检测 adb reverse，丢失自动
  重建并唤醒 App；`%LOCALAPPDATA%\PhoneDeck\server-settings.json` 可配 `usbWatchdog`
  与 `adbPath`；health 上报 `usbWatchdog` 状态。真实验证 `adb kill-server` 后自动恢复。
- **手机 Wi-Fi 保活**：设置新增开关（默认开），持 `WIFI_MODE_FULL_LOW_LATENCY` 锁。
- **主题系统**：历史版本曾提供 9 套主题；2026-08-31 按项目所有者要求收敛为唯一的
  “冰川玻璃”。设置页只显示这一项，旧配置保存的任何其他主题 ID 都会自动回退为 frost。
- **横竖屏**：主界面横屏双栏（网格 6 列 + 右侧语音面板），旋转不重建 Activity，
  听写中旋转会话不断；编辑页/键位选择器旋转保留草稿与选择。
- **主界面编辑模式**：右上角 编辑/完成 切换；编辑模式点击改按钮、长按拖动换位
  （持久化），平时长按退格/Delete/方向键连续发送（约 150ms/次），单击发一次。
- **说完自动回车（已下线）**：曾作为设置开关（默认开）在停止听写约 0.8 秒后
  补发 Enter，2026-08-28 按用户要求整体移除（`scheduleAutoEnter`、
  `auto_enter_after_dictation` 及设置项 UI 均已删除），听写停止后不再自动回车，
  需要回车时用网格里的 回车 按钮。
- **AI 黄金位预设**：合并新增 打断(Esc)、新会话(/clear+回车)、接受全部(Ctrl+Enter)、
  拒绝全部(Ctrl+Backspace)。
- **前台应用回传**：health 新增 `foregroundApp`（仅进程名，不读窗口标题），手机
  连接卡显示目标电脑当前前台应用。
- **配置导入导出**：设置页 SAF 导出/导入 `phonedeck-shortcuts.json`；导入前完整
  校验，失败不改现有配置。真机完成导出→导入闭环。
- **FocusSink**：无操作自动关闭延长到 10 分钟，便于人工验证。

## 待办（下一步严格顺序）

1. 在真实 Mac 运行 `scripts/macos/Build-PhoneDeckReceiver.sh`，固定安装到 `/Applications`，
   授予辅助功能和本地网络权限。
2. 在 TextEdit 验证文字、Command+C/V/Z、Command+Shift+4、Command+Tab、Control+Space，
   并确认没有修饰键残留。
3. 用 Samsung 与 Mac 做 USB 初配、拔线 Wi-Fi 输入、IP 变化恢复和 20 轮目标切换。
4. 与两台 Windows 做混合三机目标隔离：手机只把文字/快捷键发到当前电脑。
5. 在三台电脑开启共享后锁屏手机，分别/同时触发本机 Typeless，并验证断网、睡眠、重启自动恢复。
6. 连续切换 managed/shared 20 轮并完成至少两小时锁屏共享，检查会话、线程和缓冲无增长。
7. 回归 Windows 拔 USB 后真实 Wi-Fi 语音文字、快捷键编辑/隐藏/排序/重启持久化和蓝牙。
8. 标准 mDNS/Bonjour、凭据撤销/重配；自定义 UDP 地址发现已完成。
9. 调研报告 `outputs/PhoneDeck开发工具调研-2026-08-28.md`：第一档四项已完成；
   第二档中的受限多步宏已进入实验实现；焦点保障、按前台应用自动切配置、分页仍待完成。

## 构建环境注意（本机）

`E:\Android\...` 路径在本机不存在；构建用项目自带工具链：
`work\tools\java\jdk-17.0.20.1+1`、`work\tools\android-sdk`、`work\tools\gradle-cache`，
TEMP 指向 `work\tools\temp`。本机 adb 偶发卡死时 `taskkill /IM adb.exe /F` 后重启即可。


## Wi-Fi MVP 使用方式

1. 手机和电脑连接同一个 Wi-Fi；音频只在局域网内传输，不消耗手机流量。
2. 每台 Windows 以管理员身份运行一次 `scripts/windows/Enable-PhoneDeckLan.ps1`。
3. 每台电脑运行匹配的 1.6.0-dev.5（Mac 为 2.0.0-dev.2）接收端，首次用 USB 连接手机并建立 `adb reverse tcp:8765`。
4. App 自动读取该电脑的证书指纹、随机密钥和 LAN 地址；顶部出现“Wi-Fi 在线”后可移除
   ADB reverse 或拔掉 USB。
5. 对第二、第三台电脑重复一次；之后三台接收端同时运行，手机切换目标即可。

控制台运行包通过 `PHONEDECK_DATA_DIR` 把当前配对凭据保存在相邻 `data` 文件夹；未设置
该环境变量的传统接收端仍回退 `%LOCALAPPDATA%\PhoneDeck`。凭据不得提交到 Git。电脑
IP 变化后，手机会先使用受限 UDP 发现刷新候选地址；受限网络禁用广播时，重新连接 USB
仍可刷新无线配对资料。后续标准 mDNS/Bonjour 可作为补充发现方式。

## 本轮方案审核结论

- USB 只能连当前一台主机；普通 Hub/Y 线不可能提供三台主机并联。
- 当前蓝牙只保持一个 RFCOMM 电脑连接，且不传语音，所以不能承诺蓝牙三机语音。
- 手机端设备列表现在只把健康检查或蓝牙 `hello` 确认过的电脑记为已知设备；离线设备不可
  误选，蓝牙-only 目标会明确提示语音需要 USB 或后续局域网通道。
- 局域网必须使用独立端口、配对、消息认证和心跳，不能把 localhost 无鉴权 API 直接暴露。

## 1.6.0-dev.1 本轮代码变更

- `TargetEnvelopeValidator.cs` 统一校验 protocol v2 的 request/session/target，快捷键和
  听写入口共用同一规则；PCM 流通过 `X-PhoneDeck-Protocol` 与
  `X-PhoneDeck-Computer-Id` 请求头校验。
- Android `AudioStreamer`、听写 start/stop 均携带会话目标 ID；同一听写会话不会因目标
  状态变化而把停止命令发给另一台电脑。
- `TargetDeviceManager.java` 持久化已确认电脑的编号、名称、平台和最近在线时间；主界面
  增加目标切换条。切换只允许在线 USB/蓝牙设备，语音对蓝牙-only 目标会明确提示尚需
  USB 或局域网通道。
- 蓝牙 hello 现在回传电脑显示名，便于手机建立可读的设备卡片。

验证：Windows Release build、11 项单元测试、Android `assembleDebug` 和 `lintDebug`
均通过；尚未进行真实多电脑或局域网验收。

## 本轮完成的代码

### UI、UX 与多主题

- 历史阶段曾新增冰川玻璃、深海蓝、OLED 黑和柔和浅色四套持久化主题；当前版本已按
  项目所有者要求删除其他主题入口，仅保留冰川玻璃。
- 冰川玻璃使用原生 Canvas 绘制静态柔光背景，并以半透明渐变、白色描边和轻量阴影构成玻璃卡片；不依赖在线资源或实时模糊。
- 主界面连接卡片增加当前电脑、USB/蓝牙状态、重新检测提示和设置入口。
- 快捷卡片已改为“名称 + 组合键/文本指令”纯文字布局，移除图标块与 Emoji，保留发送中、成功和失败反馈。
- 默认第一排新增“规划 `/plan`、目标 `/goal`、压缩上下文 `/compact`”，使用已鉴权 `text` 动作并默认回车执行。
- 底部语音区加入原生麦克风图形、渐变主按钮和连续波形；输入目标编号改到面板右下方。
- 设置、快捷键管理、按钮编辑和组合键选择页统一使用主题背景与玻璃表面。
- 组合键选择器改为分组键盘网格，主动限制最多 3 个修饰键加 1 个基础键。
- 颜色选择触控面积提高到 48 dp 并加入内容描述；排序图标点击可选择上移/下移，继续保留长按拖动。
- 修正已移除箭头后仍提示“使用上下箭头”的旧文案。

本轮真机预览使用独立 Debug 包名与正式版并排安装，未卸载或覆盖正式 PhoneDeck；源码交付前已移除临时包名后缀。

### USB/ADB、音频和 Typeless 稳定性

- Android 每次听写生成独立 `sessionId`，音频流通过 `X-PhoneDeck-Session` 传递。
- Android 每 2 秒健康检查 USB；断开时立即释放 `AudioRecord`、HTTP 连接和手机本地状态。
- 停止 Typeless 请求失败时，手机仍会强制停止录音，不再让 UI 卡在听写状态。
- Windows 音频桥记录当前会话，可按 `sessionId` 主动取消并释放 WASAPI 流锁。
- 新增幂等 `/api/dictation/start` 与 `/api/dictation/stop`。
- 音频异常断流时，Windows 对本会话执行一次尽力而为的 Typeless 复位。
- 服务器退出时也会尝试清理当前 PhoneDeck Typeless 会话。
- 点击说话模式已改为单主按钮交互：同一个大按钮在空闲、启动中和听写中分别显示“开始说话”、“取消启动”和“停止说话”；用户确认移除独立“暂停/继续”。
- 停止会先立即停止手机录音，再异步等待电脑端完成 Typeless 和文字收尾。

对应提交：

- `eaa6291 Fix USB dictation session cleanup`

### PR 复审后的会话与测试通道修复

- `/dictation/start` 的重复 `requestId` 仍必须重新核对真实录音状态，不能跳过确认后假报成功。
- Typeless 状态探针不可用时启动会明确失败；停止会尽力发送一次切换，但不会在未确认时返回成功。
- 停止或断流清理失败后仍释放服务内部会话所有权，避免陈旧 `sessionId` 永久阻塞后续听写。
- 第二次停止切换前立即重读 Core Audio 状态，避免 Typeless 刚停止又被切换回开启。
- Android 音频 POST 与 `/dictation/start` 并发时，Windows 会在 1.2 秒内等待真实 WASAPI 会话登记，避免把正常的几十毫秒竞态误报成 USB 断线。
- 旧音频流先释放设备与流闸门，再触发 `AudioEnded` 回调，避免快速“停止 → 重新开始”时形成锁等待。
- 快捷键编辑页“发送测试”现在先用 USB，USB 不可用时复用主界面的蓝牙连接；USB 请求可能已执行时，只允许向相同 `computerId` 的蓝牙连接补收确认。
- USB 听写中断后重新连接时，Android 会把红色旧提示替换为“USB 已恢复，可以继续使用”，并把麦克风状态复位为已停止。
- 新增 6 个 Windows 状态机单元测试和 GitHub Actions，持续构建 Android、Windows、发布包并运行测试。

### PhoneDeck 1.5.0 可编程快捷键

- Android 应用显示名升级为“PhoneDeck 手机控制台”，版本为 `1.5.0` / `versionCode 6`。
- 主界面改为由手机本地配置动态渲染的 3 列快捷键网格；底部语音区保持固定。
- 支持编辑和新增按钮、隐藏内置按钮、删除自定义按钮。
- 支持长按拖动排序，并提供“上移/下移”无障碍替代操作。
- 支持修改名称、预设颜色、组合键或 Agent 文本指令；快捷卡片不再提供图标编辑入口。
- 支持 A–Z、0–9、F1–F24、修饰键、导航键、系统键和媒体键。
- 组合键最多 4 键，且只能包含 1 个普通键；测试动作不会自动保存。
- 支持单按钮恢复和二次确认后的全部恢复；全部恢复不修改语音模式。
- 离开编辑页时提示保存、放弃或继续编辑。
- 配置保存在应用私有 `shortcut-config.json`，使用 `AtomicFile` 原子写入。
- 配置损坏或未来版本不兼容时先保留 `.corrupt-<timestamp>.json`，再加载默认布局并提示。
- Windows 健康检查返回稳定 `computerId`、平台、架构、协议版本和能力。
- 协议 v2 `keyChord` 必须携带有效 `requestId`、`sessionId` 和匹配的 `targetComputerId`。
- 电脑端使用受控键位白名单，拒绝任意脚本、命令行、未知键、重复键和超界 `holdMs`。
- 组合键异常时逐键尽力释放已按下的键。
- 蓝牙连接新增 v2 `hello`，返回电脑 ID 与能力；旧固定动作仍兼容。

对应提交：

- `a859825 Implement PhoneDeck 1.5 programmable shortcuts`

## 本轮实际执行的验证

### 已验证

1.6.0-dev.2 安全 Wi-Fi MVP：

- Windows Release 构建成功，0 个警告、0 个错误；单元测试 14/14 通过，
  包括 LAN 密钥缺失、错误和正确三种情况。
- Android `assembleDebug`、`assembleRelease` 和 `lintDebug` 全部成功；当前 Release
  因修复工作树无 `signing.properties` 而未签名。
- Samsung `SM-G9880` 已安装 `com.codex.phonedeck` 1.6.0-dev.2 Debug。
- 底部目标切换改版的 `assembleDebug` 与 `lintDebug` 成功，已覆盖安装并完成截图核对：“1号/2号”位于语音面板右下方，顶部旧目标区与暂停按钮均已移除。
- Agent 快捷操作改版的 `assembleDebug` 与 `lintDebug` 成功，已覆盖安装并完成真机截图核对：已有 18 个按键自动迁移为纯文字，三个 Agent 指令置顶，配置中非空 `icon` 数量为 0。
- 已通过 USB loopback 自动配对；移除 `adb reverse tcp:8765` 后，手机界面仍显示
  `DESKTOP-74F6FT5 · Wi-Fi 在线`。
- 实际 HTTPS 健康检查已确认：不带配对密钥返回 401，携带正确密钥返回
  1.6.0-dev.2 和稳定电脑 ID。
- 本机防火墙已启用仅限 `LocalSubnet` 的 TCP 8766 入站规则，当前接收端
  同时监听 loopback HTTP 8765 与 HTTPS 8766。

UI 主题（Samsung SM-G9880 / Android 12，独立 Preview 包）：

- `assembleDebug` 与 `lintDebug` 成功；
- 冰川玻璃主界面、主题选择页和快捷键管理页完成截图核对；
- 主界面空闲状态与一次真实“开始听写 → 显示手机麦克风波形 → 停止”完成现场预览，未出现崩溃；
- 冰川主题下连接卡、3 列快捷键、固定语音区、浅色系统栏和设置页文字可读；
- 正式包未因本轮视觉验收被卸载或覆盖，长期签名包仍需完成最终覆盖验收。

Windows：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build `
  work\phone-deck\windows\PhoneDeck.Server\PhoneDeck.Server.csproj -c Release
```

结果：成功，0 个警告，0 个错误。

PR 复审新增状态机测试：

```powershell
dotnet test work\phone-deck\windows\PhoneDeck.Server.Tests\PhoneDeck.Server.Tests.csproj -c Release
```

结果：6/6 通过，覆盖失败启动的同请求重试、音频登记并发等待、探针不可用、停止失败释放所有权、断流失败释放所有权和第二次切换前重读状态。Windows 自包含 publish 也已成功。

Android（长期签名已接入 Debug/Release）：

```powershell
$env:JAVA_HOME = 'E:\Android\Jdk17\jdk-17.0.20.1+1'
$env:ANDROID_HOME = 'E:\Android\Sdk'
$env:GRADLE_USER_HOME = 'E:\Android\GradleCache'
$env:TEMP = 'E:\Android\Temp'
$env:TMP = 'E:\Android\Temp'
cd work\phone-deck\android
.\gradlew.bat clean :app:assembleDebug :app:assembleRelease :app:lintDebug --no-daemon
```

结果：三项均成功；lint 为 0 error，仍有既有/非阻塞的方向锁定、硬编码界面文字和
触摸可访问性 warning。

本机 PATH 先命中 `C:\Program Files (x86)\dotnet\dotnet.exe`，该宿主没有 SDK，因此
Windows 构建必须显式调用上面的 x64 `dotnet.exe`。本机 Java/Gradle 若继承默认临时目录
会出现 `Unable to establish loopback connection`，将本轮 `TEMP/TMP` 指向
`E:\Android\Temp` 后构建正常；没有修改系统全局环境变量。

APK 元数据已用 `aapt2 dump badging` 验证：

- 包名：`com.codex.phonedeck`；
- `versionCode=6`；
- `versionName=1.5.0`；
- `minSdk=26`；
- `targetSdk=35`；
- 应用名：`PhoneDeck 手机控制台`。

长期签名：

- 主密钥：`work/phone-deck/signing/phonedeck-release.jks`，已被 Git 忽略；
- 本机独立备份：`E:\Desktop\PhoneDeck-Signing-Backup`；
- Debug 与 Release APK 的证书 SHA-256 均为
  `df32795309ee01996ccfb21804a37f558a8a095c901d850f991d0d52ea6b1d9f`；
- 两个 APK 均通过 APK Signature Scheme v2 验证；
- 密钥和密码没有提交到 Git，也不得写入 Issue、PR 或聊天。

本地服务器接口验证未发送任何真实快捷键，确认：

- 健康检查返回 1.5.0 / protocol v2 / 稳定电脑 ID；
- 缺协议的 `keyChord` 返回 400；
- 缺少或错误 `targetComputerId` 返回 400；
- 未知键、重复键、仅修饰键返回 400；
- `holdMs=19` 返回 400；
- `protocolVersion=3` 返回 400；
- 大于 64 KB 的普通 JSON 请求返回 413；
- 无音频会话时启动 Typeless 返回 409；
- 重复停止听写为幂等 200；
- 旧协议未知固定动作仍返回 400。

Samsung 真机：

- 设备：Samsung `SM-G9880`，Android 12，ADB 已授权；
- 手机原有 1.4.0 使用已经遗失的 `PhoneDeck Local` 私钥，无法无损覆盖；
- 经项目所有者明确同意，执行一次性卸载 1.4.0，并安装长期签名的 1.5.0 Release；
- 1.5.0 冷启动成功，主界面显示 18 个默认快捷键、语音区和连接状态；
- 建立 `adb reverse tcp:8765 tcp:8765` 后，手机显示绿色“USB 已连接”；
- 执行一次真实 `adb kill-server` 后，手机自动回到等待连接状态；重启 ADB 并恢复
  reverse 后，手机自动回到“USB 已连接”；
- 用同一长期签名再次执行 `adb install -r` 成功，`firstInstallTime` 保持不变，证明
  后续同签名 APK 可以覆盖升级。
- 历史版本曾把原独立“停止”控制合并到大号语音主按钮，并保留“暂停/继续”；当前版已按用户决定移除该辅助入口。
- 单主按钮版的 `assembleDebug`、`assembleRelease` 和 `lintDebug` 全部成功，并已用同一长期签名 Release 覆盖安装到该 Samsung 手机。
- 上述“开始 → 停止”真机试验只证明 Android 录音已停和服务内部标志已清，不能证明 Typeless 真实停止。项目所有者随后实测发现 Typeless 仍在电脑端录音，该结论已撤回。
- 根因已确认：当前 Windows 没有 VB-CABLE，Typeless 还选择 `Auto-detect (麦克风阵列)`；旧服务在 WASAPI 初始化完成前提前公布会话，导致 Typeless 启动后又立即收到停止切换键，Electron 可能漏处理第二次按键。
- 修复后，WASAPI 成功启动前不再公布音频会话；启动前强制检查 VB-CABLE 和 Typeless 选中麦克风；停止后通过 Windows Core Audio 会话核对 Typeless 进程是否仍在录音，仅在确认仍为 Active 时重试一次停止键。
- 修复版 Windows Release 构建 0 警告/0 错误；Android Debug、Release 和 Lint 成功，同签名 Release 已覆盖安装。当前环境点击语音后手机直接显示“缺少 VB-CABLE，未启动 Typeless”；Android AppOps 未出现新录音，Typeless `Recordings` 目录没有新文件，健康状态为 `capturing=false`。
- 项目所有者提供的 `ChatGPT Image 2026年8月26日 11_11_07.png` 已作为 Android 应用图标；原图未重绘，1024 px 母版保存为 `android/artwork/phonedeck-app-icon-1024.png`，并生成 mdpi、hdpi、xhdpi、xxhdpi、xxxhdpi 五档 `mipmap` PNG。
- 图标版 `assembleDebug`、`assembleRelease` 和 `lintDebug` 全部成功；APK 资源清单确认 160–640 dpi 图标均已打包，长期签名 SHA-256 仍为 `df32795309ee01996ccfb21804a37f558a8a095c901d850f991d0d52ea6b1d9f`，同签名 Release 覆盖安装成功，Samsung “应用程序信息”页已显示新的蓝紫麦克风图标。
- 在没有键盘注入能力的本地安全模拟接收器上完成“开始 → 暂停 → 继续 → 停止”：
  开始后 Android AppOps 显示麦克风 `running`；暂停后不再 `running`、HTTP 会话保持；
  继续后重新 `running`；停止后模拟端音频和听写状态均为 false。
- 真机执行“开始后约 180 ms 立即取消”，Android 记录采集约 233 ms 后停止，正式
  Windows 健康检查确认 `audio.streaming=false`、`dictation.active=false`，应用无崩溃。
- 当前 Samsung + VB-CABLE + Typeless 真实链路已完成“开始 → 暂停 → 继续 → 停止”：开始时服务端三项状态均为 true；暂停时手机 AudioRecord 为 inactive、电脑音频和 Typeless 会话保持；继续后 AudioRecord 恢复 active；停止后三项状态均为 false。
- 连续 20 轮真实开始/停止全部通过；每轮均确认音频、听写与 Typeless 状态，结束后无残留会话。
- 听写中执行真实 `adb kill-server` 后，Windows 自动复位 Typeless 并清除音频/听写状态；ADB 与 `tcp:8765` reverse 已恢复，手机顶部重新显示绿色“USB 已连接”。
- 压力测试发现并修复两个额外竞态：音频登记晚于 start 请求，以及旧流回调阻塞下一条流闸门。
- 本轮 Android 源码与 APK 构建通过，但当前修复工作树没有签名属性文件，因此没有卸载或覆盖手机上已有的 1.5.0；新增的“USB 已恢复”反馈仍需用长期签名包做一次屏幕验收。

### 尚未验证，不得写成 PASS

- 尚未在拔掉 USB 后执行真实 Wi-Fi 语音启动/停止并核对 Typeless 文字。
- 尚未在第二、第三台 Windows 电脑进行同时在线与语音目标切换验收。
- 已实现受限 UDP 地址发现；尚未实现标准 mDNS/Bonjour、手动撤销/重配凭据和 macOS 接收端。
- 未在真实手机上验证动态网格、编辑页、长按不误触、拖动排序和字体放大。
- 因 1.4.0 原签名私钥遗失，本次只能一次性清除旧版数据，不能声称旧版配置迁移通过。
- 同签名重复安装已确认不重新安装包，但尚未用自定义配置证明文件级持久化。
- 当前电脑已经安装并启用 VB-CABLE，Typeless 也已选中 `CABLE Output`；历史版的暂停/继续状态曾通过验证，但尚未由用户对着手机说一段固定文本并核对最终识别文字。
- 未进行连续 20 次 USB 拔插/切换；本轮只完成 1 次听写中 ADB 通道中断与恢复。
- 未验证断线发生在“音频已连接但 Typeless 尚未确认”等竞态点。
- 未验证蓝牙 v2 `hello`、自定义快捷键和 ACK 的真实连接。
- 未测试 Windows UIPI、高权限目标软件、F1–F24 和媒体键的真实输入效果。
- Windows 自包含 publish 已验证；尚未替换正式发布包、创建 Git 标签或 GitHub Release。

## 下一步严格顺序

1. 拔掉 USB，由用户说一段固定文本，验收 Wi-Fi 音频、单键开始/停止和最终 Typeless 文字。
2. 在听写启动中、正在听写和停止中分别断开 Wi-Fi，确认三端都能复位。
3. 在第二、第三台 Windows 重复安装、防火墙开启和 USB 自动配对。
4. 验收三台接收端同时在线、手机切换和音频只进入当前目标。
5. 增加标准 mDNS/Bonjour、凭据撤销/重配和设备删除交互。
6. 回归快捷键编辑、隐藏、排序、新增、删除和重启持久化。
7. 在 FocusSink 验证 F1、Ctrl+C、Ctrl+Shift+S、Win+D、Alt+Tab。
8. 使用长期签名属性构建并覆盖安装正式候选版。

## 安全和范围边界

- 不加入任意 PowerShell、CMD、shell 或脚本执行。
- 不把当前 localhost 无鉴权入口开放到局域网。
- 不提交签名密钥、ADB 私钥、Typeless 个人配置、录音或发布缓存。
- 标准 mDNS/Bonjour、macOS 和 1.7.0 多配置仍不进入本轮候选版；dev.3 的受限实验宏不得
  在完成焦点、失败处理和真机输入验收前标记为稳定。
- 构建通过不能替代真实手机、音频、Typeless、蓝牙和现场验收。
