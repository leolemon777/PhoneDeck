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

Release APK 需要项目所有者自己的签名。仓库不包含签名密钥；其他开发者应创建自己的测试密钥，不要覆盖正式签名身份。

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

8. 手机先显示 USB 已连接，再点击语音按钮。
9. 电脑上先把光标放到真正的文字输入框。

服务器健康检查：

```powershell
Invoke-RestMethod http://127.0.0.1:8765/api/health
```

应返回 `ok: true`，并显示找到 `CABLE Input (VB-Audio Virtual Cable)`。

## 6. USB 重连

USB 断开或 ADB transport 改变后，`adb reverse` 会丢失。发行包可将 `scripts/windows/AutoReconnectUsb.ps1` 复制到 `PhoneDeck.Server.exe` 同一目录，并在启动服务器时以隐藏 PowerShell 进程启动。

该脚本解决端口转发恢复，但当前版本仍需要继续加强“语音过程中断线”的会话状态清理，详见 [HANDOFF.md](./HANDOFF.md)。

## 7. 蓝牙

当前蓝牙只发送快捷键，不传输音频。Windows 与手机需要先在系统设置中人工确认配对码。不要绕过系统配对确认。

## 8. 新电脑常见问题

- `adb devices` 显示 unauthorized：解锁手机并允许该电脑的 USB 调试密钥。
- 手机显示连接失败：重新运行 `adb reverse tcp:8765 tcp:8765`。
- 手机音量条跳动但 Typeless 没文字：检查 Typeless 麦克风是否仍是 `CABLE Output`。
- 健康检查显示没有音频设备：重新检查 VB-CABLE 驱动和 `CABLE Input` 播放端。
- 快捷键对管理员程序无效：Windows UIPI 会阻止低权限程序向高权限窗口注入输入。
- Release APK 无法覆盖正式版：签名不同；不要卸载用户正式版，除非已经备份并明确接受应用数据丢失。

## 9. 不应复制到新电脑或 GitHub 的内容

- Android 正式签名密钥；
- ADB 私钥；
- GitHub/API 令牌；
- Typeless 个人配置；
- 手机录音和测试语音；
- `bin`、`obj`、`build`、`.gradle`、`artifacts`、`dist`；
- 第三方驱动安装包。
