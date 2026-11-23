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

只为作者自用、只用 Typeless（2026-11-05 决策）：原生 Android + Windows/macOS 接收端。Desktop 2.0 实验版、iPhone 网页、蓝牙通道、统一更新与开源发布流程已删除，不要再加回来。当前 .NET 项目使用 SDK 10（`global.json`）；真实权限、输入和音频必须实机验证。

真正的代码位于 `work/phone-deck`：

- Android：`work/phone-deck/android`
- Windows：`work/phone-deck/windows/PhoneDeck.Server`
- macOS：`work/phone-deck/macos/PhoneDeck.Receiver`
- 输入测试工具：`work/phone-deck/test/FocusSink`

不要修改 `outputs` 中的二进制作为源代码。需要发布时从源码重新构建。

## 当前基线

- 当前主线版本组（v1.6.0-beta.3）：Android 1.6.0-dev.22 / code 28；Windows 接收端与托盘 1.6.0-dev.17 / 发布序号 29；macOS 接收端 2.0.0-dev.4 / bundle 3。版本唯一来源为 `work/phone-deck/release-versions.json`，先修改描述，再用 `Assert-ReleaseVersions.ps1 -Sync` 同步。
- 作者的 Samsung、两台 Windows 与 Apple Silicon Mac 已完成附近连接、听写和切换的核心链路，详见 `docs/release/SUPPORT_MATRIX.md`。
- 手机 APK 沿用作者签名电脑上的调试签名；换签名需要卸载重装并重新配对，私钥不能入 Git。在其他电脑构建的 APK 不能覆盖安装到作者手机。
- 没有统一更新：各端在本机构建后手动安装（手机 `adb install -r`，不要卸载）。
- 当前规格版本：v0.6。规格顶部按日期记录的最新用户决策优先于第 0 章与历史章节；旧二维码首页、居中话筒和电脑 Agent 指令同步已被取代。四端总规划不等于全部已实现或已授权执行。
- 当前 Android 代码是 Java，不要在没有明确收益和迁移计划时整体改写 Kotlin。
- 当前 Windows 接收端是 .NET 10/C#，使用 ASP.NET Core、NAudio 和 SendInput；托盘为 WinForms。
- 当前 macOS 精简版是 .NET 10 Native AOT，使用 CGEvent、Core Audio / BlackHole。作者设备已验证核心听写与连接；Intel、翻译/问答、完整权限与升级矩阵仍待验收，不得声称 Mac 全场景已通过。
- `shared/Receiver` 已共用逐手机凭据、撤销、附近确认、mDNS 广播、会话租约和状态页。Android 无 USB 首次连接为同网段发现 + 四位校验码 + 电脑点允许；保留 USB 与旧协议兼容。

## 当前最高优先级

1. 在作者的手机与 3 台电脑上验证同一 Wi-Fi 共享供音：断续、断流、长时间使用、断线/停止/重启；协议自动化不能代替硬件结果。
2. 在作者设备上验证 Typeless 听写、翻译、问答（含 Wi-Fi 共享 16 kHz 下的识别准确率）。
3. Windows 高 DPI 与开机启动、Mac 权限升级验证。
4. 仅执行当前任务授权的阶段；总体规划不代表已授权一次性实现全部功能。

## 不可破坏的行为

- 底部语音操作区保持易触达。
- 只用共享麦克风（2026-11-05 决策）：点击 / 按住的受管听写已删除，不要再加回来；手机主按钮只开关共享。
- USB 音频维持 48 kHz / PCM16 / mono 基线，除非有测试证明应改变。
- Typeless 快捷键从 Typeless 自己的设置（或 shortcutOverrides）动态读取，不能写死当前机器的键位（如 LeftShift+Z）。
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

涉及真实语音、ADB、Typeless、VB-CABLE、Mac 权限或 USB 共享切换器时，自动化构建不能替代真实硬件验收。报告中必须明确区分“代码通过构建”和“已在真实设备验证”。

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
- USB 和局域网是可替换传输层，业务配置不能绑定单一传输。
- 优先小步提交、兼容迁移和可回退设计。
