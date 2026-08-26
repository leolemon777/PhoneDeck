# PhoneDeck 源代码

当前版本：1.4.0。

目录：

- `android`：原生 Android Java App，最低 Android 8.0（API 26）
- `windows/PhoneDeck.Server`：.NET 8 Windows x64 接收端

电脑端的 `typeless` 白名单动作会读取当前用户的
`%APPDATA%\Typeless.exe\app-settings.json`，解析 `dictationMode` 快捷键后通过
Windows `SendInput` 触发；读取失败时退回 Typeless 官方 Windows 默认键 `RightAlt`。
Typeless 快捷键会保持按下约 55 毫秒以提高全局快捷键识别率。手机为每条指令生成唯一
`requestId`，电脑端在 USB 和蓝牙通道统一去重并返回确认。

USB 音频由 Android `AudioRecord` 以 48 kHz / PCM 16-bit / mono 采集，通过
`/api/audio/stream` 的 ADB 反向隧道持续发送。电脑端使用 NAudio/WASAPI 把音频写入
`CABLE Input (VB-Audio Virtual Cable)`；Typeless 从对应的 `CABLE Output` 录音端读取。
蓝牙目前只支持按键动作，不承载音频。

Android 主界面将语音操作区固定在屏幕底部。语音手势支持 `tap`（点击开始、再次
点击停止）和 `hold`（按下开始、松开停止）两种状态机；设置保存在应用私有的
`SharedPreferences` 中。长按模式对“音频尚在连接时已经松手”的情况做了延迟收尾，
避免 Typeless 或手机录音残留在启动状态。

Android 开发构建需要 JDK 17、Android SDK 35 和 Gradle 8.9：

```powershell
cd android
gradle assembleDebug
```

Windows 构建：

```powershell
dotnet publish windows/PhoneDeck.Server/PhoneDeck.Server.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

正式 APK 使用独立的个人签名生成；私钥没有包含在源代码压缩包中。自行构建时请使用自己的 Android 签名密钥。
