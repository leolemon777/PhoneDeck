# PhoneDeck macOS 接收端（2.0.0-dev.2）

## 当前阶段能做什么

macOS 2.0.0-dev.2 用于验证第三台 Mac 与两台 Windows 的混合控制和双语音模式：

- 支持 Apple Silicon 与 Intel Mac；
- 复用 PhoneDeck 协议 v2、稳定 `computerId`、USB 配对、HTTPS 8766、证书固定、访问令牌和 UDP 8767 发现；
- 通过 macOS `CGEvent` 注入受控快捷键与 Unicode 文字；
- 保留请求 ID 去重、目标电脑校验、组合键数量/时长限制，以及异常时释放修饰键；
- 当前 Android 默认布局在 Mac 上自动兼容：`Ctrl`/`Win`/`Alt` 分别按逻辑映射为 `Command`/`Command`/`Option`；
- `Win+Shift+S` 映射为 `Command+Shift+4`，`Alt+Tab` 映射为 `Command+Tab`，`Win+Space` 映射为 `Control+Space`。
- 接收 48 kHz / PCM16 / mono 手机音频，通过 AUHAL 按设备 UID 定向输出到
  `BlackHole 2ch`，不会修改系统默认输出设备；输出侧复制为左右双声道。
- 支持 `managed` 手机控制听写和 `shared` 共享麦克风；共享流只供音，不触发 Typeless。
- 自动读取 Typeless 的三种快捷键和所选麦克风，并通过 Core Audio 进程对象确认真实采集状态。

蓝牙、Developer ID 签名和公证尚未接入；真实 Mac 音频和三机压力测试也尚未完成。

## Mac 端准备

最低要求 macOS 14.2。构建机/运行机需要：

1. .NET 8 SDK；
2. Android SDK Platform Tools（需要 USB 自动配对时安装）；
3. 已安装的 [BlackHole 2ch](https://github.com/ExistentialAudio/BlackHole)，并在“音频 MIDI 设置”中设为 48,000 Hz；
4. 已安装 Typeless，并把麦克风选为 `BlackHole 2ch`，三种模式按需设置本机快捷键。

在仓库根目录执行：

```zsh
# 当前 Mac 架构（自动识别 Apple Silicon / Intel）
zsh scripts/macos/Build-PhoneDeckReceiver.sh

# 也可以显式构建
zsh scripts/macos/Build-PhoneDeckReceiver.sh arm64
zsh scripts/macos/Build-PhoneDeckReceiver.sh x64
```

输出位于：

```text
work/phone-deck/dist/macos/osx-arm64/PhoneDeck Receiver.app
# 或
work/phone-deck/dist/macos/osx-x64/PhoneDeck Receiver.app
```

把 `PhoneDeck Receiver.app` 移到 `/Applications`。正式授权前不要反复移动 App；macOS 的辅助功能授权与 App 身份、签名和位置有关。

## 首次启动与权限

第一次建议从“终端”运行，便于看到诊断：

```zsh
"/Applications/PhoneDeck Receiver.app/Contents/MacOS/PhoneDeck.Receiver"
```

然后进入：

```text
系统设置 → 隐私与安全性 → 辅助功能
```

添加并启用 `PhoneDeck Receiver.app`，完全退出接收端后重新启动。执行下面的命令检查状态：

Typeless 还需要它自己的麦克风权限：进入“系统设置 → 隐私与安全性 → 麦克风”，确认
Typeless 已启用。PhoneDeck Receiver 只向 BlackHole 输出手机 PCM，不读取 Mac 的物理
麦克风，因此不应要求 PhoneDeck 自身取得麦克风权限。

```zsh
curl -s http://127.0.0.1:8765/api/health | python3 -m json.tool
```

应看到：

```json
{
  "platform": "macos",
  "input": {
    "backend": "CGEvent",
    "accessibilityTrusted": true
  },
  "audio": {
    "available": true,
    "device": "BlackHole 2ch",
    "streaming": false,
    "sessionId": null,
    "mode": null
  },
  "typeless": {
    "virtualCableSelected": true,
    "capturing": false
  }
}
```

如果 `audio.available=false`，先检查 BlackHole 是否安装、名称是否为 `BlackHole 2ch`，
以及 `server-settings.json` 中可选的 `audioDeviceUid` 是否仍有效。PhoneDeck 不会为了修复
配置而更改系统默认设备。

## Typeless 与设备覆盖

接收端默认在 `~/Library/Application Support` 下的常见 Typeless 目录查找
`app-settings.json`。需要显式指定时，编辑：

```text
~/Library/Application Support/PhoneDeck/server-settings.json
```

可用字段：

```json
{
  "audioDeviceUid": null,
  "typelessSettingsPath": null,
  "typelessShortcuts": {
    "dictation": null,
    "translation": null,
    "ask": null
  }
}
```

空值表示自动读取 Typeless 配置。只有自动读取不适配当前 Typeless 版本时才填写覆盖值；
以上字段仅作用于 Typeless 引擎——切换其他引擎或新增档案见 `voice-engine-settings.json` 与
[VOICE_ENGINES.md](./VOICE_ENGINES.md)；
不要假设所有 Mac 都使用 Fn。`managed` 模式要求状态探针、BlackHole 选择和对应快捷键均
可用；`shared` 模式不操作 Typeless，因此状态探针不可用时仍可持续供音。

如果 macOS 弹出“本地网络”提示，请允许；PhoneDeck 只监听本机 USB 回环端口 8765，以及局域网 HTTPS 8766/UDP 8767，不会开放到公网。

## 手机配对与双模式验收

1. 在 Mac 启动接收端。
2. 用 USB 连接 Android 手机，并在手机上确认这台 Mac 的 USB 调试授权。
3. 如果已安装 `adb`，USB 看门狗会自动建立 `adb reverse tcp:8765 tcp:8765`；也可手动执行同一命令。
4. 手机连接卡应显示这台 Mac，设备平台保存为 `macos`。
5. 打开 TextEdit，新建空白文档，将光标留在文档内。
6. 依次验证：输入 `/plan`、复制、粘贴、撤销、截图、切换窗口、切换输入法。
7. 拔掉 USB，在同一 Wi-Fi 下确认手机仍显示 Mac 在线，并再次验证文字与快捷键。
8. 在两台 Windows 与这台 Mac 之间切换，确认动作只进入手机当前显示的目标电脑。
9. 选择“手机控制听写”，验证点击、按住以及已配置的听写/翻译/问答；停止和断流后
   `audio.streaming`、`dictation.active`、`typeless.capturing` 都恢复为 false。
10. 选择“共享麦克风”并手动开启；在 Mac 按本机 Typeless 快捷键，确认只有 Mac 开始
    转写，而共享流本身不切换 Typeless。
11. 与两台 Windows 同时在线，分别和同时按三台电脑的本机快捷键，完成一发三收验收。

## 已知限制

- 尚未在真实 Mac 上完成构建、权限、CGEvent、USB、Wi-Fi 和混合三机验收；Windows 上的编译与单元测试不能替代这些步骤。
- 当前 Android 按键编辑器仍使用 Windows 风格标签。Mac 接收端对默认组合做兼容转换；后续会把编辑器升级为明确的 `主键（Ctrl/Command）`、`Control`、`Option` 和 `Command` 语义。
- macOS 没有标准 F21–F24 虚拟键码，本阶段支持 F1–F20；媒体播放/上一首/下一首暂不执行并返回明确错误。
- BlackHole 和 Typeless 均由用户单独安装，不进入 PhoneDeck 安装包；设备 UID 和 Typeless
  配置结构仍需在实际安装版本上核对。
- 当前构建仅做 ad-hoc 本地签名；正式分发仍需要 Apple Developer ID 签名、公证和安装包。
