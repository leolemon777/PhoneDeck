# PhoneDeck 构建、打包与发布流程

核对日期：2026-09-10。依据源码、现有脚本与 PR #5 的 CI 结果整理。
产品范围和工作优先级由 [总计划第 0 章](../spec%20plan.markdown) 管理；本文负责构建操作和交付门槛。
本轮更新文档，不安装软件、不修改 CI、不升级依赖或发布正式版本。

## 1. 当前基线

| 组件 | 源码/工具链 | 当前产物与边界 |
|---|---|---|
| Android | Java 17；AGP 8.7.3；Gradle Wrapper 8.9；compile/target SDK 35；min SDK 26 | dev.17 / versionCode 23；debug APK 已在 Samsung Android 12 覆盖安装 |
| Windows 接收端 | .NET 8，win-x64；NAudio 2.2.1 | dev.11 / 更新序号 23；自包含 EXE，86 项接收端测试通过 |
| Windows 控制台 | .NET 8 WPF/Windows Forms，win-x64 | 独立 EXE；统一更新包需要单文件携带原生依赖；尚无独立产品版本字段 |
| macOS 接收端 | .NET 8；osx-arm64 / osx-x64 源码与脚本 | dev.3 预览；`.app` 使用 ad-hoc 签名，真实 Mac 输入/音频尚待验收 |
| iOS | 尚无客户端工程 | 先做音频/本地网络原型，再建立 Xcode 构建链；目前不产出 IPA |

这些新改动在 `agent/fleet-updates` / [PR #5](https://github.com/leolemon777/PhoneDeck/pull/5)，核对时 PR 尚未合并。
新开发者应明确检出所需提交；克隆默认 main 不代表已经取得上述版本。
实际安装状态：本机与手机已更新，另一台 Windows 仍为 dev.6，需要一次性接入。

构建目录与运行目录分开。仓库只保存源码、公开公钥、示例和脚本；APK、EXE、ZIP、签名私钥、data 与缓存不进 Git。

## 2. 一次修改到用户安装的全流程

```text
确定需求与验收用例
  → 独立分支 / 固定 commit / 检查工作区
  → 检查工具链与版本字段
  → 编译 + 单元测试 + lint
  → PR 的普通 CI（无发布私钥）
  → 选定候选 commit，构建各平台产物
  → 核对平台安装签名、更新签名、版本和文件哈希
  → 形成首次安装包 / 统一更新包 / Mac 预览包
  → 干净安装 + 升级 + 失败恢复 + 多机真机验收
  → 生成发布记录，发布通过门槛的同一批产物
  → 用户一端发起更新，按设备确认实际版本
```

| 阶段 | 输入 | 输出 | 放行条件 |
|---|---|---|---|
| 开发构建 | 工作分支、测试用例 | 编译结果、测试报告、开发 APK/EXE | 相应平台检查通过；不能直接标成发行包 |
| 候选构建 | 选定 commit、版本组、目标平台 | 带哈希的候选产物、来源记录 | 必要组件齐全，签名渠道匹配，版本一致 |
| 发布 | 已通过真机验收的候选产物 | 发布说明、下载包、支持矩阵 | 提升已经验过的同一批字节；如重建，重新核对并验收 |
| 设备更新 | 已签名更新包、已配对设备 | 每台设备的实际安装结果 | 电脑健康/版本/身份确认；安卓系统安装完成 |

“GitHub CI 通过”“安装成功”“语音识别正确”“支持五台电脑”各有独立证据，不能相互替代。
产品的统一更新只负责最后阶段，不会自动编译 GitHub 源码，也不是已经接入 GitHub Release 自动检查。

## 3. 干净开发环境

| 构建目标 | 开发环境 | 运行时另需准备 |
|---|---|---|
| Android | JDK 17、SDK Platform 35、Build Tools 35.0.0、仓库 Gradle Wrapper；Windows/Linux/macOS 均可开发构建 | Android 手机；首次 USB 路径需 ADB；录音与安装权限 |
| Windows 接收端及控制台 | Windows x64、.NET SDK（当前 CI 为 8.0.x） | 虚拟音频驱动、用户选择的输入法、LAN 防火墙与配对 |
| macOS `.app` | macOS、.NET SDK、zsh 与系统 codesign；架构显式指定 | BlackHole、输入法、辅助功能/音频权限 |
| 更新 ZIP | PowerShell 7、已构建的两个 EXE 和同渠道 APK、更新发布私钥 | 接收端内置匹配的发布公钥 |

环境路径通过 `JAVA_HOME`、`ANDROID_HOME` 或本机 `local.properties` 配置。
不复制本开发机的 E 盘路径到 CI；不把驱动或输入法安装当作编译条件。
检查环境可用 `java -version`、`dotnet --info` 和 `./gradlew --version`。
目前没有 `global.json` 或依赖锁文件；Gradle Wrapper 已固定版本但没有配置分发包 SHA-256。
后续 B02 固定可复现工具链，同时按总计划的运行时生命周期要求制定迁移验证，不能把当前 .NET 8 配置无限沿用。

## 4. 当前可执行的构建命令

以下以仓库根目录为起点。`outputs/build-review` 是开发构建目录，正式候选包应改为独立的发布编号/commit 目录，防止混入旧文件。
每个命令失败即停止后续打包；当前尚无统一脚本替用户完成这种失败传递。

### Android

```powershell
Push-Location work/phone-deck/android
./gradlew.bat :app:assembleDebug :app:testDebugUnitTest :app:lintDebug --no-daemon --console=plain
./gradlew.bat :app:assembleRelease --no-daemon --console=plain
Pop-Location
```

Linux/macOS 在同一 Android 目录使用 `./gradlew`；必要时先 `chmod +x gradlew`。
主要输出：`work/phone-deck/android/app/build/outputs/apk/debug/app-debug.apk`、release 子目录和 `build/reports`。
没有项目签名配置时 release 可能是 `app-release-unsigned.apk`，不能按文件夹名称判断可安装性。
debug 在没有项目签名配置时使用本机开发证书；不同开发机/CI 的 debug APK 不保证能相互覆盖。
配置项目签名后，当前 Gradle 会让 debug/release 共用该签名；恢复签名材料前先确认要维护的安装渠道。

### Windows

```powershell
dotnet test work/phone-deck/windows/PhoneDeck.Server.Tests/PhoneDeck.Server.Tests.csproj -c Release
dotnet build work/phone-deck/windows/PhoneDeck.ControlCenter/PhoneDeck.ControlCenter.csproj -c Release

dotnet publish work/phone-deck/windows/PhoneDeck.Server/PhoneDeck.Server.csproj `
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
  -o outputs/build-review/windows/server

dotnet publish work/phone-deck/windows/PhoneDeck.ControlCenter/PhoneDeck.ControlCenter.csproj `
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o outputs/build-review/windows/console
```

统一更新只替换两个 EXE，因此控制台发布必须包含上述原生依赖参数。
当前 csproj 没有将它设为默认值；后续 B01 应把这个要求写进统一入口或项目默认配置，并检查单文件产物。
FocusSink 是可选输入验收工具，独立于用户安装包：`work/phone-deck/test/FocusSink/FocusSink.csproj`。

### macOS

在真实 Mac 的仓库根目录执行：

```zsh
dotnet test work/phone-deck/macos/PhoneDeck.Receiver.Tests/PhoneDeck.Receiver.Tests.csproj -c Release
zsh scripts/macos/Build-PhoneDeckReceiver.sh arm64
zsh scripts/macos/Build-PhoneDeckReceiver.sh x64
```

分别输出 `work/phone-deck/dist/macos/osx-arm64/PhoneDeck Receiver.app` 和 `osx-x64` 下的同名 App。
脚本会清理对应架构的生成目录；不要把个人数据放在 dist。
现有 CI 不传架构参数，只构建运行器本机架构；上面两条是要补齐的双架构验证操作，不能由一次 CI 成功推定两种架构都验过。
ad-hoc 签名仅用于开发预览，不等于 Developer ID 签名、公证或公开安装渠道完成。

## 5. 版本与签名

| 字段 | 现有位置 | 要核对的关系 |
|---|---|---|
| Android versionName / versionCode | `android/app/build.gradle` | APK 内元数据必须一致，覆盖升级时 code 递增 |
| Windows Version / Sequence | `windows/PhoneDeck.Server/FleetUpdates.cs` | 更新清单 windowsVersion/sequence 必须与新接收端健康结果一致 |
| Mac 版本 | `macos/PhoneDeck.Receiver/Program.cs` 与 `Packaging/Info.plist` | 健康信息、Bundle 版本和发布说明一起核对 |
| 更新清单 | `build-update-package.ps1` 命令参数 | 当前为人工传参；不能只填新数字而没有构建对应程序 |
| 协议/配置版本 | 各端协议与配置实现 | 不能随营销版本号盲目递增；兼容性单独验收 |

建议后续引入唯一发布版本描述文件，生成/校验各端字段及文档摘要；**本轮没有创建该机制**。
一组发布可以包含不同平台版本，但必须来自明确的同一提交或记录清楚的来源，发布后禁止复用相同发布序号替换成另一批字节。

四种签名职责分别处理：

| 签名 | 目的 | 当前状态 |
|---|---|---|
| Android APK 签名 | 系统确认应用身份、允许覆盖升级 | 本次手机是开发证书渠道；历史长期证书为另一渠道，不能混装 |
| 更新清单 RSA 签名 | 已接入设备确认发布者并验证三个产物的哈希 | 已实现；公开公钥在源码，私钥不入 Git |
| Windows 程序签名 | 面向公开分发的系统发布者身份 | 正式渠道待选定和接入，清单签名不能替代它 |
| Apple 签名/公证/分发 | Mac/iOS 平台安装与发行 | Mac 仅 ad-hoc；iOS 尚无工程 |

本次手机 APK 证书前缀 `653884d0…`，历史长期证书前缀 `df327953…`；完整事实见 HANDOFF 的部署记录。
构建前比较目标设备与候选 APK 的签名。`signing.properties.example` 是格式样例，不代表具备对应私钥。
普通 PR 和 fork CI 不需要私钥；后续正式签名进入受保护发布流程，缺少正确密钥就停止生成该渠道的可发布包。

## 6. 打包和设备更新

现有打包脚本把已经构建好的文件归档并签名，**不会编译项目，也不会完整证明参数与二进制版本相符**。
将两个 EXE、经验证的同渠道 APK 放到候选目录，确认真实版本后执行：

```powershell
# 示例为当前开发版本组合；下一次发布需要实际递增并构建对应版本。
./work/phone-deck/build-update-package.ps1 `
  -Server outputs/build-review/windows/server/PhoneDeck.Server.exe `
  -ControlCenter outputs/build-review/windows/console/PhoneDeck.ControlCenter.exe `
  -Apk work/phone-deck/android/app/build/outputs/apk/debug/app-debug.apk `
  -Sequence 23 -WindowsVersion '1.6.0-dev.11' -AndroidVersionCode 23 `
  -SigningKey '<本机安全保存的更新私钥路径>' `
  -Output outputs/build-review/PhoneDeck-update-23.zip
```

示例用 debug APK 只是延续当前开发安装渠道，不能直接用于正式公开发行。
正式候选须使用该发行渠道已验证签名的 APK，并重新验收。脚本拒绝覆盖已有输出文件，但不会阻止用另一个文件名重复一个发布序号；B03 要补发布级唯一性检查。

| 包 | 内容 | 适用对象 |
|---|---|---|
| 首次接入更新能力包 | 两个 EXE、APK、Install-UpdateSupport.ps1/.cmd、说明与校验和 | 已有 PhoneDeck 数据但缺少更新接口的旧电脑 |
| 签名统一更新 ZIP | manifest.json、manifest.sig、固定两个 EXE、PhoneDeck.apk | 已接入 Windows/Android；不包含 data、驱动、输入法或 Mac App |
| 全新用户安装包 | 安装程序/向导、平台依赖提示、首次信任和卸载说明 | **尚未形成完整统一流程**；首次接入脚本要求原 computer-id，不适合全新用户 |
| Mac 预览 `.app` | 接收端与 Info.plist、ad-hoc 签名 | Mac 开发/硬件验收，独立于当前更新 ZIP |

候选记录至少包含 commit、工具链、构建时间、版本组、签名公钥/证书指纹、文件大小和 SHA-256、测试报告、支持矩阵。
不得收录私钥、令牌、真实 data 或用户录音。大文件和发布包进入产物存储/Release，不进入源码历史。

用户流程：任一已接入电脑导入签名包 → 请求批量更新 → 打开手机主页 → 手机校验和分发 → 电脑等待空闲、安装并检查 → 手机系统安装确认。
离线设备在恢复连接且手机主页前台时补更。输入法/虚拟音频驱动的安装与更新仍由其渠道负责。
操作细节和回退边界见 [FLEET_UPDATES.md](./FLEET_UPDATES.md)。

## 7. 现有 CI 的真实覆盖与缺口

依据 [.github/workflows/ci.yml](../.github/workflows/ci.yml)，PR #5 核对时所有已有检查成功。

| 项目 | 当前 CI | 下一步 |
|---|---|---|
| Windows 接收端 | test + build + self-contained publish | 保留，归档报告 |
| Windows 控制台 | 没有构建步骤 | B01 增加 build/publish 与单文件检查 |
| Android | assembleDebug、assembleRelease、lintDebug | B01 增加 testDebugUnitTest 与报告 |
| Mac | test + 当前运行器架构的 `.app` | B04 明确 arm64/x64 矩阵及正式签名边界 |
| 包/版本/签名 | 未校验发行组合，也未生成统一更新包 | B02/B03 增加校验和失败用例 |
| 产物归档 | 没有 upload-artifact 步骤 | B01 归档报告与明确标记的测试产物 |
| 正式发布 | 没有专用 Release workflow | B03 设计受保护签名/发布，不给 PR 私钥 |
| iOS | 无 | M0 原型后在 B04 接入真实工程与 Mac 构建机 |

`agent/**` push 与 pull_request 都会触发，目前同一提交可能运行两套相同作业；后续 B01 评估去重及取消过期运行。
CI release APK 构建成功不代表有发行签名；Mac runner 编译成功也没有验证用户的麦克风、辅助功能或 BlackHole。

## 8. 实施顺序与验收门槛

以下是总计划 0.21 的执行细化，目前均为待实施，负责人角色用于分工而非表示已经派发任务。

| 任务 | 依赖/负责人角色 | 具体交付 | 完成证据 |
|---|---|---|---|
| B01 统一开发构建 | 当前源码；构建维护 | 一个入口调用现有工具、失败即停、路径可移植；补控制台/Android 测试/报告归档；评估重复 CI | 干净 Windows 与 CI 执行同一检查集合，故意编译失败时无成功产物；不接触运行进程 |
| B02 版本与依赖约束 | B01；构建维护 | 单一版本描述、SDK/依赖约束、Gradle 分发校验、产物版本检查 | 任一版本/code/sequence 不匹配都在安装前失败；重建能追溯来源 |
| B03 候选包与发布 | B02；发布维护 | 首次安装/旧版接入/统一更新各自打包；签名渠道分离；产物报告；受保护发布流程 | 缺密钥、混渠道、漏控制台、错误版本、错误哈希均不产生可发布候选 |
| B04 跨平台构建矩阵 | B01；Mac/iOS 工程角色 | Mac 双架构；iOS 原型产生工程后再接入 CI；各平台独立签名任务 | 可重复构建各已实现目标，未支持组合明确排除 |
| B05 多设备候选验收 | B03 + M0/T01/T02；测试角色 | 第二台旧电脑接入、两机升级、离线补更、取消/低磁盘/文件占用/断电恢复；音频与身份回归 | 逐设备版本/设置/身份及测试结果可追溯；未确认的安装不显示成功 |
| B06 开源发行准备 | B03–B05 + M1–M4；维护角色 | 全新用户安装/卸载、许可证/资产清单、支持矩阵、SBOM、发行渠道、维护流程 | 支持矩阵内完成干净安装和升级，才能按 M5 声明稳定支持 |

建议现在先做 B01，再做 B02/B03；另一台电脑接入与 T02 会话契约可按现有条件推进。
Mac/iOS 原型继续尽早开展；多输入法、五机能力、主题保持 M1–M4 的依赖顺序，不以构建计划替代产品验收。

每个实现任务完成后，先记录源码与测试证据，再形成候选包；安装当前使用中的电脑和手机是独立动作，不应成为普通 build 的隐含副作用。
