# B02 实施约定

2026-09-10，基于 a954ab1。B01 的 PR #7 云端运行 34469445810：Windows、Android、macOS 和去重前置检查全部成功。Mac 仅为 CI 构建验证，不等于真机支持。

本批工作分支 agent/b02-version-dependencies。Codex 规划、审查和独立验证；Grok 与 agy 按下列文件分工编码，不修改用户主仓库。

## 版本接口与分工

Grok 负责新增 work/phone-deck/release-versions.json、scripts/versioning 下脚本与其独立测试，以及各端产品版本引用所需的源码/props/Gradle app 配置。JSON schemaVersion=1，windows.version=1.6.0-dev.11、windows.sequence=23；android.versionName=1.6.0-dev.17、android.versionCode=23；macos.version=2.0.0-dev.3、macos.bundleShortVersion=2.0.0、macos.bundleVersion=2。控制台跟随 Windows 版本。Mac 历史 bundleVersion=2 与 dev.3 分叉须显式记录为历史映射，本批不静默改号。描述文件不存工具链、私钥或个人路径。

默认只校验，显式 Sync 才生成源码版本与程序集属性；任一版本漂移都失败，不能用人工反复修改多个版本源。独立测试必须实际验证错误值/缺字段/漂移，禁止跳过断言或仅搜索脚本文本。本批先完成描述与源码/程序集一致性，后续小批接入真实 PE/APK 产物检查及打包入口；当前 build-update-package.ps1 不得提前声称完成验收。

agy 负责 global.json、独立 Directory.Build.targets（仅 NuGet 锁策略）、packages.lock.json、Gradle wrapper properties、独立依赖校验脚本；本轮先只读计划，待 Codex 核对再编辑。禁止修改 Grok 的版本文件、项目文件、app/build.gradle、Directory.Build.props。CI 与 scripts/build.ps1 留给下一轮指定唯一维护者，当前两人都不改。

## 工具链证据与策略

.NET SDK 固定 8.0.425（PR7 Windows/Mac 实际通过版本）；不在此批升级框架或改变依赖版本。官方元数据：https://raw.githubusercontent.com/dotnet/core/main/release-notes/8.0/releases.json 。SDK win-x64 ZIP SHA-512：f0b6f15bf6f1a0507205c0cb102ab99e1dee875c4682c8ed94665be1d580186a06b21455e83b3a01a0ff7f4cd887b67420f2e2fe09ed985534a4cea488ae1af9 。.NET 8 在 2026-11-10 结束支持，迁移另列后续任务。

Gradle 8.9 分发 SHA-256 已从 https://services.gradle.org/distributions/gradle-8.9-bin.zip.sha256 读取：d725d707bfabd4dfdc958c624003b3c80accc03f7037b5122c4b1d0ef15cecab 。保留 AGP 8.7.3/JDK17/SDK35，固定的值必须可验证，不能用 PENDING 假装锁定完成。

双方编码时不运行共享 dotnet/Gradle 构建、不提交、不切分支、不读取 signing/data。Codex 待编辑结束统一构建并验证，包内版本检查须使用实际二进制，不把 sidecar 的自报版本当作二进制证据。

## 第一轮独立审查（11:34Z）

版本默认校验当前源码通过，但把 windows.sequence 改成 JSON 数字 23.1 后仍退出0，说明整数转换导致错误输入被取整接受；要求严格 JSON 整数类型、版本格式和数字范围校验，并补全生成 props 的 Version/AssemblyVersion 校验。测试夹具改为保留，避免未验证路径的递归删除。Grok负责修正及真实失败测试。

agy 的三项配置已真实落盘，但 RestorePackagesWithLockFile 误受 CI 条件限制：普通 msbuild 属性读取为空。要求其无条件启用，只有 RestoreLockedMode 受 CI 条件限制。SDK8.0.425已从官方地址下载并通过SHA512，位于outputs/toolchains/dotnet-8.0.425/runtime；编辑结束后才生成依赖锁并统一构建。

## 修正后的独立验证（11:54Z）

Codex 回读依赖 targets，确认锁文件现已无条件启用。版本脚本独立执行60个断言通过，真实源码校验通过；第一次审查保存的 sequence=23.1 描述被正确非零拒绝。此结果只覆盖版本描述/源码/生成属性，不代表实际PE/APK、依赖锁还原、CI或B02整体通过。打包代码正在审查，尚未验收。

## 打包门槛审查（12:04Z）

agy只读任务产生了实际实现，Codex按未审代码处理。静态检查确认Server和ControlCenter共用身份白名单，会放过交换产物；APK字段未限定同一条package记录；源码描述校验尚未接入打包前置。Pester测试依赖未固定，使用个人输出SDK路径及未保护递归清理，暂不执行。要求下一轮限定scripts/packaging与build-update-package.ps1，修正身份按角色匹配、精确产品版本及哈希后缀、aapt2定位/返回码/单package记录解析、签名前源码严格校验；独立测试用真实PE夹具且不删除临时树。临时test.ps1/test2.ps1不纳入提交。

## 继续执行的独立验证（2026-09-10 20:35本地时间）

六项目锁定还原全部通过，包含Mac双架构依赖图。真实Windows统一构建使用SDK8.0.425，86测试通过，两个单文件EXE发布成功，run为outputs/b02-windows-validation/20260911T003332Z-dc9dcee6。静态读取PE资源确认两者FileVersion=1.6.0.23，ProductVersion=1.6.0-dev.11+a954ab1完整提交哈希，OriginalFilename分别是对应程序集DLL。

打包独立测试9/10：畸形APK记录被拒绝，但测试预期诊断不同；还发现SDK自动定位赋值给PowerShell只读变量IsWindows，以及CLI错误测试人为构造诊断、签名前漏源码校验。已交agy限定文件修正，未放行。Grok负责CI/core接入global.json、锁定还原、源码版本校验及回归测试，不与agy同写。
