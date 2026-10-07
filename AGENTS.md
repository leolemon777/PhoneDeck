# PhoneDeck Agent 工作说明

本文件适用于在 GitHub 或本地继续开发 PhoneDeck 的所有 Codex、Claude Code、Cursor、ZCode 或其他 Agent。

## 开始工作前

1. 完整阅读仓库根目录的 `README.md`。
2. 完整阅读 `spec plan.markdown`，它是产品范围和决策的唯一长期来源。
3. 阅读 `docs/HANDOFF.md`、`docs/ARCHITECTURE.md` 和相关源代码。
   涉及构建、版本或发布时同时阅读 `docs/BUILD_PIPELINE.md`，按规格 0.21 的依赖执行。
4. 执行 `git status`，确认没有覆盖其他 Agent 或用户的未提交修改。
5. 明确本次工作属于哪个版本，不把后续版本功能偷偷塞进当前里程碑。

## 源码根目录

主线是原生 Android + Windows/macOS 外部输入法接收端。`work/phone-deck/desktop/PhoneDeck.Desktop` 为独立 `2.0.0-alpha.1` 本地 Whisper 实验路线，构建入口 `scripts/build-desktop.ps1`，具体状态见 `docs/release/CROSS_PLATFORM_EXECUTION.md`。新 Desktop、Mac 与网页客户端不能混入旧固定三产物更新清单。当前 .NET 项目使用 SDK 10（`global.json`）；真实权限、输入和音频必须实机验证。

真正的代码位于 `work/phone-deck`：

- Android：`work/phone-deck/android`
- Windows：`work/phone-deck/windows/PhoneDeck.Server`
- macOS：`work/phone-deck/macos/PhoneDeck.Receiver`
- 输入测试工具：`work/phone-deck/test/FocusSink`

不要修改 `outputs` 中的二进制作为源代码。需要发布时从源码重新构建。

## 当前基线

- 当前主线版本组（v1.6.0-beta.3）：Android 1.6.0-dev.22 / code 28；Windows 接收端与托盘 1.6.0-dev.17 / 发布序号 29；macOS 接收端 2.0.0-dev.4 / bundle 3。版本唯一来源为 `work/phone-deck/release-versions.json`，先修改描述，再用 `Assert-ReleaseVersions.ps1 -Sync` 同步。
- 主线为技术预览。作者的 Samsung、两台 Windows 与 Apple Silicon Mac 已完成附近连接、听写和切换的核心链路，详见 `docs/release/SUPPORT_MATRIX.md`。beta.3 为重新构建的分发包，不能把旧设备走查当成该批字节的新安装验收。尚无外部用户干净安装、其他手机与全部输入法矩阵验收。
- beta APK 沿用开发机调试签名，不能当作长期正式签名或进入签名设备更新包；不要更换已发布 beta 的字节。切换正式签名需要卸载重装并重新配对。实验 `.desktoppreview` 保持独立包名与签名。
- Windows/Android 统一更新已加入，协议与发布要求见 docs/guides/FLEET_UPDATES.md。签名包只允许固定三个产物，禁止引入远程命令/脚本或跳过发布者验证。Android APK 签名和更新清单签名独立，私钥不能入 Git。
- 当前规格版本：v0.6。规格顶部按日期记录的最新用户决策优先于第 0 章与历史章节；旧二维码首页、居中话筒和电脑 Agent 指令同步已被取代。四端总规划不等于全部已实现或已授权执行。
- 当前 Android 代码是 Java，不要在没有明确收益和迁移计划时整体改写 Kotlin。
- 当前 Windows 接收端是 .NET 10/C#，使用 ASP.NET Core、NAudio、SendInput 和原生蓝牙套接字；托盘为 WinForms。
- 当前 macOS 精简版是 .NET 10 Native AOT，使用 CGEvent、Core Audio / BlackHole。作者设备已验证核心听写与连接；Intel、翻译/问答、完整权限与升级矩阵仍待验收，不得声称 Mac 全场景已通过。
- `shared/Receiver` 已共用逐手机凭据、撤销、附近确认、mDNS 广播、会话租约和状态页。Android 无 USB 首次连接为同网段发现 + 四位校验码 + 电脑点允许；保留 USB 与旧协议兼容。

## 当前最高优先级

1. 完成外部用户干净安装、升级与卸载验收，并把公开下载包的来源、版本、签名与哈希逐批记录。
2. 验证多台电脑同时共享供音、长时间使用、断线/撤销/停止/重启；协议自动化不能代替硬件结果。
3. 按产品/系统/版本补齐听写、翻译、问答及 Typeless、微信、豆包、千问兼容矩阵。
4. 补齐其他 Android 手机、字体缩放/读屏、Windows 高 DPI 与开机启动、Mac Intel 与权限升级验证。
5. 正式签名、Mac 公证、Windows 精简编译与 iOS 原型按独立授权推进；iPhone 网页保持实验标记。
6. 仅执行当前任务授权的阶段；总体规划不代表已授权一次性实现或发布全部功能。

## 不可破坏的行为

- 底部语音操作区保持易触达。
- 点击与按住两种语音模式继续工作。
- USB 音频维持 48 kHz / PCM16 / mono 基线，除非有测试证明应改变。
- 语音引擎快捷键从用户配置/引擎档案动态读取，不能写死当前机器的键位（如 LeftShift+Z）。新增引擎走档案 JSON，不改代码。
- 旧的 `copy`、`paste`、`screenshot` 等动作继续兼容。
- USB 请求保留 requestId 去重和确认。
- 组合键发生异常时尽力释放所有修饰键。
- 手机音频默认不落盘。

## 安全约束

- 禁止实现任意 shell、PowerShell、CMD 或脚本远程执行。
- 自定义按键只能来自受控映射表，并限制按键数量与按住时间。
- 程序/文件夹启动必须使用电脑端批准目标 ID。
- 无鉴权 HTTP 入口只能监听 `127.0.0.1:8765`。
- 局域网入口必须继续独立使用 HTTPS 8766、逐手机凭据（保留 USB 配对的旧密钥兼容）、证书固定与目标 ID 校验；首次附近配对必须在电脑本机确认。
- 不提交 `signing`、`*.jks`、`*.keystore`、ADB 密钥、访问令牌、录音、个人配置或发布缓存。

## 验证要求

按改动范围至少执行：

```powershell
# Android
cd work\phone-deck\android
.\gradlew.bat :app:assembleDebug
.\gradlew.bat :app:lintDebug

# Windows
dotnet build work\phone-deck\windows\PhoneDeck.Server\PhoneDeck.Server.csproj -c Release
dotnet test work\phone-deck\windows\PhoneDeck.Server.Tests\PhoneDeck.Server.Tests.csproj -c Release

# macOS（编译/测试可跨平台执行；CGEvent、权限、ADB、网络必须在真实 Mac 验收）
dotnet build work\phone-deck\macos\PhoneDeck.Receiver\PhoneDeck.Receiver.csproj -c Release
dotnet test work\phone-deck\macos\PhoneDeck.Receiver.Tests\PhoneDeck.Receiver.Tests.csproj -c Release
```

涉及真实语音、ADB、Typeless、VB-CABLE、蓝牙、Mac 权限或 USB 共享切换器时，自动化构建不能替代真实硬件验收。报告中必须明确区分“代码通过构建”和“已在真实设备验证”。

## Git 与多 Agent 协作

1. 开始前拉取最新 `main`。
2. 每个独立任务使用独立分支，例如 `agent/usb-audio-recovery`。
3. 一次提交只解决一个清晰问题，提交信息说明结果而不是过程。
4. 不使用 `git reset --hard`、强推或覆盖其他 Agent 的提交。
5. 修改协议、配置格式、版本路线或产品范围时同步更新 `spec plan.markdown`。
6. 完成工作后更新 `docs/HANDOFF.md` 的“最新状态”和“待办”，并记录执行过的验证。
7. 推送分支并创建 Pull Request；PR 中写明风险、测试和未验证项。
8. 多个 Agent 不应同时修改同一源文件；先用 Issue/PR 或交接文档划分任务。

## 设计原则

- 手机按钮描述逻辑动作，平台后端负责 Windows/macOS 映射。
- 多电脑功能使用稳定 `computerId`，不能把会变化的 IP 当身份。
- 同一时刻只允许一个默认语音目标。
- 目标切换必须得到新电脑确认，不能只改变手机本地标签。
- USB、局域网和蓝牙是可替换传输层，业务配置不能绑定单一传输。
- 优先小步提交、兼容迁移和可回退设计。
