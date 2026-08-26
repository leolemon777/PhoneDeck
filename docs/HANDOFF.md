# PhoneDeck 项目交接说明

更新时间：2026-08-26
当前分支：`agent/usb-audio-recovery-1.5.0`
当前源码：PhoneDeck 1.5.0 候选版
上一实机稳定基线：PhoneDeck 1.4.0
规格基线：v0.3
Android 配置版本：`schemaVersion=1`
通信协议：v2，并兼容 1.4.0 固定动作

## 给下一台电脑和下一位 Agent 的一句话

1.5.0 的源码、长期签名、Samsung 安装和一轮真实 ADB 服务断开/恢复已经完成；下一步
不是继续扩大功能，而是补齐快捷键编辑、真实输入、VB-CABLE、Typeless、连续断线和
蓝牙验收，修复实机问题后再发布。

## 本轮完成的代码

### USB/ADB、音频和 Typeless 稳定性

- Android 每次听写生成独立 `sessionId`，音频流通过 `X-PhoneDeck-Session` 传递。
- Android 每 2 秒健康检查 USB；断开时立即释放 `AudioRecord`、HTTP 连接和手机本地状态。
- 停止 Typeless 请求失败时，手机仍会强制停止录音，不再让 UI 卡在听写状态。
- Windows 音频桥记录当前会话，可按 `sessionId` 主动取消并释放 WASAPI 流锁。
- 新增幂等 `/api/dictation/start` 与 `/api/dictation/stop`。
- 音频异常断流时，Windows 对本会话执行一次尽力而为的 Typeless 复位。
- 服务器退出时也会尝试清理当前 PhoneDeck Typeless 会话。
- 点击说话模式已改为单主按钮交互：同一个大按钮在空闲、启动中和听写中分别显示“开始说话”、“取消启动”和“停止说话”；下方只保留“暂停/继续”。
- 暂停会立即停止 Android `AudioRecord`，同时按 PCM 实时速率发送静音维持同一音频和
  Typeless 会话；继续时恢复手机麦克风采集，不需要重新建立会话。
- 停止会先立即停止手机录音，再异步等待电脑端完成 Typeless 和文字收尾。

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
- 语音灵敏度改进版已用同一签名覆盖安装；原独立“停止”控制已合并到大号语音主按钮，“暂停/继续”仍为辅助控制。
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

### 尚未验证，不得写成 PASS

- 未在真实手机上验证动态网格、编辑页、长按不误触、拖动排序和字体放大。
- 因 1.4.0 原签名私钥遗失，本次只能一次性清除旧版数据，不能声称旧版配置迁移通过。
- 同签名重复安装已确认不重新安装包，但尚未用自定义配置证明文件级持久化。
- 暂停/继续已经通过真机加安全模拟接收器验证，但尚未连接真实 VB-CABLE，也未验证
  Typeless 对长时间静音保活、暂停后继续识别和最终文字的实际效果。
- 新增的 Typeless 真实录音状态确认已在当前机读取到 `capturing=false`，但因缺少 VB-CABLE，尚未完成真实手机音频下的“开始 → 停止 → capturing=false”端到端验收。
- 未进行连续 20 次开始/停止和 20 次 USB 拔插/切换。
- 未验证断线发生在“音频已连接但 Typeless 尚未确认”等竞态点。
- 未验证蓝牙 v2 `hello`、自定义快捷键和 ACK 的真实连接。
- 未测试 Windows UIPI、高权限目标软件、F1–F24 和媒体键的真实输入效果。
- 未执行 Windows 自包含 publish、正式发布包替换、Git 标签或 GitHub Release。

## 下一步严格顺序

1. 把 `E:\Desktop\PhoneDeck-Signing-Backup` 加密复制到另一个可靠介质，不上传 GitHub。
2. 在 FocusSink 或普通文本框验证 F1、Ctrl+C、Ctrl+Shift+S、Win+D、Alt+Tab。
3. 验证编辑、隐藏、排序、新增、删除、单按钮恢复、全部恢复和重启持久化。
4. 安装并确认 VB-CABLE，启动 Typeless 并选择正确的 `CABLE Output`。
5. 连续开始/停止听写 20 次。
6. 在听写的启动中、进行中和停止中分别断开 USB，确认三端都能复位。
7. 连续 USB 断开/恢复 20 次并保存日志、视频和失败步骤。
8. 验证 USB 与蓝牙发送同一自定义组合键。
9. 修复实机问题并重跑构建/lint。
10. 全部通过后再执行 Windows publish、发布包替换、打标签和 GitHub Release。

## 安全和范围边界

- 不加入任意 PowerShell、CMD、shell 或脚本执行。
- 不把当前 localhost 无鉴权入口开放到局域网。
- 不提交签名密钥、ADB 私钥、Typeless 个人配置、录音或发布缓存。
- 1.6.0 的多电脑发现/配对、1.7.0 多配置和 1.8.0 宏不进入本轮候选版。
- 构建通过不能替代真实手机、音频、Typeless、蓝牙和现场验收。
