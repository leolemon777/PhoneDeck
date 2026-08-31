# PhoneDeck macOS 接收端（2.0.0-dev.1）

## 当前阶段能做什么

macOS 第一阶段用于尽快验证第三台 Mac 与两台 Windows 的混合切换：

- 支持 Apple Silicon 与 Intel Mac；
- 复用 PhoneDeck 协议 v2、稳定 `computerId`、USB 配对、HTTPS 8766、证书固定、访问令牌和 UDP 8767 发现；
- 通过 macOS `CGEvent` 注入受控快捷键与 Unicode 文字；
- 保留请求 ID 去重、目标电脑校验、组合键数量/时长限制，以及异常时释放修饰键；
- 当前 Android 默认布局在 Mac 上自动兼容：`Ctrl`/`Win`/`Alt` 分别按逻辑映射为 `Command`/`Command`/`Option`；
- `Win+Shift+S` 映射为 `Command+Shift+4`，`Alt+Tab` 映射为 `Command+Tab`，`Win+Space` 映射为 `Control+Space`。

本阶段尚未接入 Core Audio、BlackHole、Typeless 受控会话和蓝牙。健康检查会明确返回 `audio.available=false`，手机不会把尚未实现的 Mac 语音链路显示成可用。

## Mac 端准备

建议 macOS 12 或更高版本。构建机需要：

1. .NET 8 SDK；
2. Android SDK Platform Tools（需要 USB 自动配对时安装）；
3. 已安装的 Typeless（第一阶段只可通过固定 Typeless 动作触发，手机麦克风音频尚不可用）。

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
    "available": false
  }
}
```

如果 macOS 弹出“本地网络”提示，请允许；PhoneDeck 只监听本机 USB 回环端口 8765，以及局域网 HTTPS 8766/UDP 8767，不会开放到公网。

## 手机配对与第一轮验收

1. 在 Mac 启动接收端。
2. 用 USB 连接 Android 手机，并在手机上确认这台 Mac 的 USB 调试授权。
3. 如果已安装 `adb`，USB 看门狗会自动建立 `adb reverse tcp:8765 tcp:8765`；也可手动执行同一命令。
4. 手机连接卡应显示这台 Mac，设备平台保存为 `macos`。
5. 打开 TextEdit，新建空白文档，将光标留在文档内。
6. 依次验证：输入 `/plan`、复制、粘贴、撤销、截图、切换窗口、切换输入法。
7. 拔掉 USB，在同一 Wi-Fi 下确认手机仍显示 Mac 在线，并再次验证文字与快捷键。
8. 在两台 Windows 与这台 Mac 之间切换，确认动作只进入手机当前显示的目标电脑。

## 已知限制

- 尚未在真实 Mac 上完成构建、权限、CGEvent、USB、Wi-Fi 和混合三机验收；Windows 上的编译与单元测试不能替代这些步骤。
- 当前 Android 按键编辑器仍使用 Windows 风格标签。Mac 接收端对默认组合做兼容转换；后续会把编辑器升级为明确的 `主键（Ctrl/Command）`、`Control`、`Option` 和 `Command` 语义。
- macOS 没有标准 F21–F24 虚拟键码，本阶段支持 F1–F20；媒体播放/上一首/下一首暂不执行并返回明确错误。
- 语音阶段计划采用 Core Audio 输出到 `BlackHole 2ch`，再由 Typeless 选择 BlackHole 作为麦克风。BlackHole 和 Typeless 均由用户单独安装，不进入 PhoneDeck 安装包。
- 当前构建仅做 ad-hoc 本地签名；正式分发仍需要 Apple Developer ID 签名、公证和安装包。
