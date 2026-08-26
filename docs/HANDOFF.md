# PhoneDeck 项目交接说明

更新时间：2026-08-26
当前分支：`agent/usb-audio-recovery-1.5.0`
当前源码：PhoneDeck 1.5.0 候选版
上一实机稳定基线：PhoneDeck 1.4.0
规格基线：v0.3
Android 配置版本：`schemaVersion=1`
通信协议：v2，并兼容 1.4.0 固定动作

## 给下一台电脑和下一位 Agent 的一句话

1.5.0 的源码、构建和协议边界验证已经完成；下一步不是继续扩大功能，而是使用真实
Samsung 手机、ADB、VB-CABLE、Typeless 和蓝牙完成候选版验收，修复实机问题后再发布。

## 本轮完成的代码

### USB/ADB、音频和 Typeless 稳定性

- Android 每次听写生成独立 `sessionId`，音频流通过 `X-PhoneDeck-Session` 传递。
- Android 每 2 秒健康检查 USB；断开时立即释放 `AudioRecord`、HTTP 连接和手机本地状态。
- 停止 Typeless 请求失败时，手机仍会强制停止录音，不再让 UI 卡在听写状态。
- Windows 音频桥记录当前会话，可按 `sessionId` 主动取消并释放 WASAPI 流锁。
- 新增幂等 `/api/dictation/start` 与 `/api/dictation/stop`。
- 音频异常断流时，Windows 对本会话执行一次尽力而为的 Typeless 复位。
- 服务器退出时也会尝试清理当前 PhoneDeck Typeless 会话。

对应提交：

- `eaa6291 Fix USB dictation session cleanup`

### PhoneDeck 1.5.0 可编程快捷键

- Android 应用显示名升级为“PhoneDeck 手机控制台”，版本为 `1.5.0` / `versionCode 6`。
- 主界面改为由手机本地配置动态渲染的 3 列快捷键网格；底部语音区保持固定。
- 支持编辑和新增按钮、隐藏内置按钮、删除自定义按钮。
- 支持长按拖动排序，并提供“上移/下移”无障碍替代操作。
- 支持修改名称、预设颜色、内置图标或 Emoji。
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

Windows：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build `
  work\phone-deck\windows\PhoneDeck.Server\PhoneDeck.Server.csproj -c Release
```

结果：成功，0 个警告，0 个错误。

Android：

```powershell
$env:JAVA_HOME = 'E:\Android\Jdk17\jdk-17.0.20.1+1'
$env:ANDROID_HOME = 'E:\Android\Sdk'
$env:GRADLE_USER_HOME = 'E:\Android\GradleCache'
$env:TEMP = 'E:\Android\Temp'
$env:TMP = 'E:\Android\Temp'
cd work\phone-deck\android
.\gradlew.bat :app:assembleDebug :app:lintDebug --no-daemon
```

结果：两项均成功；lint 为 0 error，仍有既有/非阻塞的方向锁定、硬编码界面文字和
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

### 尚未验证，不得写成 PASS

- 未把 1.5.0 APK 覆盖安装到项目所有者的 Samsung 手机。
- 未在真实手机上验证动态网格、编辑页、长按不误触、拖动排序和字体放大。
- 未验证 1.4.0 → 1.5.0 覆盖安装后麦克风权限与语音模式是否保留。
- 未连接真实 VB-CABLE，也未让 Typeless 使用手机音频。
- 未进行连续 20 次开始/停止和 20 次 USB 拔插/切换。
- 未验证断线发生在“音频已连接但 Typeless 尚未确认”等竞态点。
- 未验证蓝牙 v2 `hello`、自定义快捷键和 ACK 的真实连接。
- 未测试 Windows UIPI、高权限目标软件、F1–F24 和媒体键的真实输入效果。
- 未执行 Release APK 正式签名、Windows 自包含 publish 或发布包替换。

## 下一步严格顺序

1. 备份手机当前正式版与配置，不卸载 1.4.0。
2. 启动新 Windows 接收端，确认健康检查显示 1.5.0 / protocol v2。
3. 使用 Debug APK 覆盖安装，确认权限和语音模式保留。
4. 先在 FocusSink 或普通文本框验证 F1、Ctrl+C、Ctrl+Shift+S、Win+D、Alt+Tab。
5. 验证编辑、隐藏、排序、新增、删除、单按钮恢复和全部恢复。
6. 验证 USB 与蓝牙发送同一自定义组合键。
7. 接入 VB-CABLE 与 Typeless，连续开始/停止 20 次。
8. 在听写的启动中、进行中和停止中分别断开 USB，确认三端都能复位。
9. 连续 USB 断开/恢复 20 次并保存日志、视频和失败步骤。
10. 修复实机问题并重跑构建/lint；通过后再更新为正式 1.5.0、打标签和发布。

## 安全和范围边界

- 不加入任意 PowerShell、CMD、shell 或脚本执行。
- 不把当前 localhost 无鉴权入口开放到局域网。
- 不提交签名密钥、ADB 私钥、Typeless 个人配置、录音或发布缓存。
- 1.6.0 的多电脑发现/配对、1.7.0 多配置和 1.8.0 宏不进入本轮候选版。
- 构建通过不能替代真实手机、音频、Typeless、蓝牙和现场验收。
