# PhoneDeck 项目交接说明

更新时间：2026-08-26
代码基线：PhoneDeck 1.4.0
规格基线：v0.3

## 给下一台电脑和下一位 Agent 的一句话

先保持现有 USB 手机麦克风 + Typeless 能稳定使用，再实现 1.5.0 的可编程快捷键；协议 v2 同时预留多电脑身份，但不要一次性直接开发完整 1.6/2.0。

## 项目来源与用户目标

项目由一台闲置 Samsung Android 手机开始。项目所有者希望把手机长期插在电脑上或无线连接，用手机麦克风驱动电脑端 Typeless，并用大按钮执行电脑快捷键。经过多轮实测，手机麦克风音质令人满意，因此后续功能围绕“语音优先、快捷控制、多电脑切换”展开。

项目所有者进一步提出：

- 所有快捷键都能在设置中替换；
- 支持 Codex、Claude Code、ZCode、Cursor 等工具的命令按钮；
- 项目可搬到其他 Windows/Mac 继续开发；
- 一台手机可以管理三台左右电脑，并明确切换语音输入目标；
- 可以研究四电脑 USB 共享切换器，但不采用普通 USB Hub/Y 线。

## 已完成状态

1. Android App 和 Windows 接收端 1.4.0 已构建并在真实手机/电脑上使用过。
2. 手机通过 ADB reverse 连接 Windows 本地服务。
3. 手机音频通过 VB-CABLE 到达 Typeless。
4. Typeless 当前用户配置会被动态读取，代码没有写死用户的具体快捷键。
5. 点击说话和按住说话已经实现。
6. 手机端底部语音区、设置页、音量反馈、震动和请求确认已经实现。
7. 蓝牙快捷键备用通道已经实现；蓝牙音频没有实现。
8. 运行包曾加入 PowerShell USB 自动恢复脚本；模板已放入 `scripts/windows/AutoReconnectUsb.ps1`。
9. 完整产品规格已经升级到 v0.3，包含 D-01 至 D-18、跨平台和多电脑路线。
10. Android Gradle Wrapper 已补齐，便于新电脑构建。

## 已观察到的可靠性问题

USB 短暂断开时，ADB transport 会变化且 `adb reverse` 会被清空。后台脚本可以恢复端口转发，但如果断线发生在语音进行中，手机、服务器和 Typeless 可能留下不一致状态。曾通过重启手机 App、PhoneDeck.Server 和 Typeless 恢复。

建议在进入 1.5.0 大功能前先完成：

1. Android 周期性健康检查和连接状态机；
2. 音频流中断时明确停止并复位 `dictationActive`；
3. 电脑端记录当前音频 sessionId，并在断线后释放流锁；
4. 必要时增加安全的“停止当前 PhoneDeck 听写会话”接口；
5. USB watcher 恢复 reverse 后触发两端状态同步，而不只是重新打开 Activity；
6. 连续开始/停止 20 次和 USB 切换 20 次回归测试。

## 接下来计划

### 第一批：稳定性修复

- 解决上面的 USB 音频残留问题。
- 补充诊断状态：手机录音、HTTP 音频流、VB-CABLE、Typeless。
- 保持 1.4.0 行为与安装包兼容。

### PhoneDeck 1.5.0

- 动态按钮配置；
- 单键与组合键选择器；
- 名称、图标、颜色、显示/隐藏和排序；
- 测试动作与恢复默认；
- Android 本地 JSON 配置、schemaVersion 和迁移；
- 协议 v2 与旧动作兼容；
- AI 命令先作为安全文本按钮，不执行任意 shell。

协议 v2 必须预留：

- `computerId`；
- `targetComputerId`；
- `sessionId`；
- `platform` / `architecture`；
- `capabilities`；
- 逻辑修饰键 `PRIMARY`。

### PhoneDeck 1.6.0

- Windows 多电脑设备列表；
- 本地网络或手机热点；
- 验证码/二维码配对；
- 一键切换当前目标；
- 语音受控交接；
- USB 共享切换器样机验证。

### PhoneDeck 2.0

- macOS Apple Silicon/Intel 接收端；
- CGEvent 输入；
- Core Audio 到虚拟音频设备；
- 辅助功能、麦克风权限、签名和公证；
- Windows/macOS 混合多电脑切换。

## 尚未最终确定的产品选择

- 多电脑默认使用局域网，还是仅在添加第二台电脑时引导开启；
- 第一台 USB 共享切换器的具体型号；
- 是否接受硬件模式必须按实体按钮；
- 语音进行中切换是否需要短时音频缓冲；
- AI 工具命令预设放入 1.5.x 还是 1.8.0；
- Intel Mac 是否与 Apple Silicon 第一版同时发布。

不要自行替项目所有者决定这些问题；先依据规格中的推荐方案制作可比较的原型或提出明确取舍。

## 接手检查清单

```powershell
git status
git log --oneline -5
dotnet build work\phone-deck\windows\PhoneDeck.Server\PhoneDeck.Server.csproj -c Release
cd work\phone-deck\android
.\gradlew.bat :app:assembleDebug
.\gradlew.bat :app:lintDebug
```

真实运行还需要阅读 [SETUP.md](./SETUP.md)。完成任何一批工作后，请更新本文件，避免下一位 Agent 重新猜测上下文。
