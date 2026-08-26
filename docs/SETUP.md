# 新电脑开发、构建与运行

## 1. 克隆仓库

```powershell
git clone https://github.com/leolemon777/PhoneDeck.git
cd PhoneDeck
```

私有仓库需要新电脑上的 GitHub 账号具有访问权限，并使用 GitHub CLI、Git Credential Manager 或 SSH 登录。

## 2. 开发依赖

### Android

- JDK 17；
- Android SDK Platform 35；
- Android SDK Build Tools；
- Android SDK Platform Tools（ADB）；
- 可选 Android Studio。

仓库包含 Gradle Wrapper 8.9，不需要单独安装全局 Gradle。

确保 `JAVA_HOME` 指向 JDK 17，并设置 `ANDROID_HOME` 或在 `work/phone-deck/android/local.properties` 写入本机 SDK 路径。`local.properties` 不会提交。

### Windows 接收端

- Windows 10/11 x64；
- .NET 8 SDK；
- VB-CABLE（运行时外部依赖）；
- Typeless（运行时外部依赖）。

自包含发布后的 PhoneDeck.Server 不要求目标电脑预装 .NET，但开发和构建需要 SDK。

### macOS

当前没有 macOS 接收端。Android App 可以在 macOS 上构建，但 Windows 接收端依赖 NAudio、WASAPI、SendInput 和 Windows 蓝牙 API，不能直接作为 Mac 程序运行。

## 3. Android 构建

```powershell
cd work\phone-deck\android
.\gradlew.bat :app:assembleDebug
.\gradlew.bat :app:lintDebug
```

Debug APK：

```text
work/phone-deck/android/app/build/outputs/apk/debug/app-debug.apk
```

安装到已开启 USB 调试的手机：

```powershell
adb devices -l
adb install -r work\phone-deck\android\app\build\outputs\apk\debug\app-debug.apk
```

PhoneDeck 1.5.0 起使用固定的项目签名。仓库只包含
`work/phone-deck/signing.properties.example`，不包含私钥或真实密码。新电脑必须通过安全的
离线方式恢复以下两个文件：

```text
work/phone-deck/signing/phonedeck-release.jks
work/phone-deck/signing/signing.properties
```

本机备份当前位于 `E:\Desktop\PhoneDeck-Signing-Backup`。复制到新电脑后应放回上面的
Git 忽略目录；不要改名、重新生成或提交 GitHub。Gradle 检测到本地配置后，会让 Debug
和 Release 使用同一长期签名。当前证书 SHA-256 为：

```text
df32795309ee01996ccfb21804a37f558a8a095c901d850f991d0d52ea6b1d9f
```

如果缺少本地签名配置，Debug 会回退到该电脑自己的 Android Debug 证书，不能用于覆盖
手机中的长期签名版本；此时生成的 Release 也不能作为正式包交付。

## 4. Windows 构建

普通构建：

```powershell
dotnet build work\phone-deck\windows\PhoneDeck.Server\PhoneDeck.Server.csproj -c Release
```

单文件自包含发布：

```powershell
dotnet publish work\phone-deck\windows\PhoneDeck.Server\PhoneDeck.Server.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true
```

默认发布目录位于项目的 `bin/Release` 下。`bin` 和 `obj` 不提交 Git。

## 5. Windows 运行链路

1. 从 VB-Audio 官方渠道安装 VB-CABLE；必要时以管理员身份安装并重启。
2. 在 Windows 声音设备中确认 `CABLE Input` 和 `CABLE Output` 存在。
3. 安装并启动 Typeless。
4. 在 Typeless 中选择 `CABLE Output` 作为麦克风。
5. 启动 `PhoneDeck.Server.exe`。
6. 连接并授权 Android 手机。
7. 建立 ADB 反向转发：

```powershell
adb reverse tcp:8765 tcp:8765
adb shell am start -n com.codex.phonedeck/.MainActivity
```

8. 手机先显示 USB 已连接，再点击大号“开始说话”主按钮。启动阶段同一按钮会变为“取消启动”。
9. 听写中同一主按钮会变为“停止说话”，点击它完成本次文字；下方“暂停”会停止手机麦克风采集但保持会话，“继续”恢复采集。

如果手机显示“USB 已连接 · 缺少 VB-CABLE”，语音按钮不会启动 Typeless。安装并启用 VB-CABLE 后，还必须在 Typeless 设置中把麦克风选为 `CABLE Output (VB-Audio Virtual Cable)`；保持“Auto-detect / 系统默认麦克风”会被 PhoneDeck 拒绝，以防误录电脑自带麦克风。
10. 电脑上先把光标放到真正的文字输入框。

服务器健康检查：

```powershell
Invoke-RestMethod http://127.0.0.1:8765/api/health
```

应返回 `ok: true`，并显示找到 `CABLE Input (VB-Audio Virtual Cable)`。
1.5.0 候选版还会返回 `protocolVersion: 2`、稳定 `computerId`、平台、架构、能力列表，
以及当前音频/Typeless 会话状态。

## 6. USB 重连

USB 断开或 ADB transport 改变后，`adb reverse` 会丢失。发行包可将 `scripts/windows/AutoReconnectUsb.ps1` 复制到 `PhoneDeck.Server.exe` 同一目录，并在启动服务器时以隐藏 PowerShell 进程启动。

该脚本只负责恢复端口转发和唤醒 App。1.5.0 候选源码已经加入显式音频会话、
幂等 Typeless 开始/停止和断流清理，但仍必须按 [HANDOFF.md](./HANDOFF.md) 使用真实
手机完成连续断线验收，不能只凭构建结果视为稳定。

## 6.1 1.5.0 快捷键配置

快捷键布局保存在 Android 应用私有目录中的 `shortcut-config.json`，普通文件管理器不会
直接看到。首次从 1.4.0 覆盖安装时会按原布局生成默认配置，原有语音模式继续保存在
独立 `SharedPreferences` 中。不要为了测试清除 App 数据，否则会同时清除配置与语音模式。

配置损坏时 App 会保留 `shortcut-config.corrupt-<timestamp>.json` 并恢复默认布局。
1.5.0 正式验收必须覆盖：编辑、隐藏、排序、新增、删除、单个恢复、全部恢复和重启持久化。

## 7. 蓝牙

当前蓝牙只发送快捷键，不传输音频。Windows 与手机需要先在系统设置中人工确认配对码。不要绕过系统配对确认。

## 8. 新电脑常见问题

- `adb devices` 显示 unauthorized：解锁手机并允许该电脑的 USB 调试密钥。
- 手机显示连接失败：重新运行 `adb reverse tcp:8765 tcp:8765`。
- 手机音量条跳动但 Typeless 没文字：检查 Typeless 麦克风是否仍是 `CABLE Output`。
- 健康检查显示没有音频设备：重新检查 VB-CABLE 驱动和 `CABLE Input` 播放端。
- 快捷键对管理员程序无效：Windows UIPI 会阻止低权限程序向高权限窗口注入输入。
- Release APK 无法覆盖正式版：签名不同；不要卸载用户正式版，除非已经备份并明确接受应用数据丢失。

## 9. 新电脑迁移与秘密材料

必须通过加密离线介质单独迁移 PhoneDeck 长期签名密钥和对应
`signing.properties`，然后放入 Git 忽略的 `work/phone-deck/signing/`。不要通过 GitHub、
Issue、PR、聊天或普通网盘传输它们。

以下内容不应复制到新电脑或提交 GitHub：

- ADB 私钥；
- GitHub/API 令牌；
- Typeless 个人配置；
- 手机录音和测试语音；
- `bin`、`obj`、`build`、`.gradle`、`artifacts`、`dist`；
- 第三方驱动安装包。
