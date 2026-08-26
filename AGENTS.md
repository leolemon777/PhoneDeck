# PhoneDeck Agent 工作说明

本文件适用于在 GitHub 或本地继续开发 PhoneDeck 的所有 Codex、Claude Code、Cursor、ZCode 或其他 Agent。

## 开始工作前

1. 完整阅读仓库根目录的 `README.md`。
2. 完整阅读 `spec plan.markdown`，它是产品范围和决策的唯一长期来源。
3. 阅读 `docs/HANDOFF.md`、`docs/ARCHITECTURE.md` 和相关源代码。
4. 执行 `git status`，确认没有覆盖其他 Agent 或用户的未提交修改。
5. 明确本次工作属于哪个版本，不把后续版本功能偷偷塞进当前里程碑。

## 源码根目录

真正的代码位于 `work/phone-deck`：

- Android：`work/phone-deck/android`
- Windows：`work/phone-deck/windows/PhoneDeck.Server`
- 输入测试工具：`work/phone-deck/test/FocusSink`

不要修改 `outputs` 中的二进制作为源代码。需要发布时从源码重新构建。

## 当前基线

- 当前正式功能版本：1.4.0。
- 当前规格版本：v0.3。
- 1.5.0 尚未开始实现。
- 当前 Android 代码是 Java，不要在没有明确收益和迁移计划时整体改写 Kotlin。
- 当前 Windows 接收端是 .NET 8/C#，使用 ASP.NET Core、NAudio、SendInput 和原生蓝牙套接字。
- 当前 macOS 接收端不存在，不要声称已经支持 Mac。

## 当前最高优先级

1. 稳定性修复：USB/ADB 断开时清理手机音频、电脑音频流和 Typeless 状态。
2. 1.5.0：安全的可编程快捷键与配置模型。
3. 协议 v2：兼容旧协议，并预留多电脑身份与平台能力。
4. 1.6.0：多电脑设备管理和目标切换。

## 不可破坏的行为

- 底部语音操作区保持易触达。
- 点击与按住两种语音模式继续工作。
- USB 音频维持 48 kHz / PCM16 / mono 基线，除非有测试证明应改变。
- Typeless 快捷键从用户配置动态读取，不能写死当前机器的 LeftShift+Z。
- 旧的 `copy`、`paste`、`screenshot` 等动作继续兼容。
- USB 请求保留 requestId 去重和确认。
- 组合键发生异常时尽力释放所有修饰键。
- 手机音频默认不落盘。

## 安全约束

- 禁止实现任意 shell、PowerShell、CMD 或脚本远程执行。
- 自定义按键只能来自受控映射表，并限制按键数量与按住时间。
- 程序/文件夹启动必须使用电脑端批准目标 ID。
- 当前无鉴权 HTTP 入口只能监听 `127.0.0.1`。
- 未来局域网入口必须与 USB 入口隔离，并具有配对、密钥、目标 ID 和消息认证。
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
