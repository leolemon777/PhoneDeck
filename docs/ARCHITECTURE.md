# PhoneDeck 架构说明

## 当前 1.4.0 数据流

```text
Android MainActivity
  ├─ 快捷键 JSON ───────────────┐
  └─ AudioRecord PCM 48k mono ──┤
                                │ ADB reverse 127.0.0.1:8765
Windows PhoneDeck.Server        │
  ├─ /api/input ◀───────────────┘
  │    └─ 白名单动作 → SendInput → 当前前台窗口
  ├─ /api/audio/stream
  │    └─ NAudio/WASAPI → CABLE Input → CABLE Output → Typeless
  └─ BluetoothReceiver
       └─ RFCOMM 快捷键 + ACK；无音频
```

### Android 主要组件

- `MainActivity.java`：界面、语音手势、连接选择、动作发送和反馈。
- `SettingsActivity.java`：点击/按住语音模式设置。
- `AudioStreamer.java`：AudioRecord、PCM 音量计算和 HTTP chunked 音频流。
- `BluetoothTransport.java`：手机作为 RFCOMM 服务端，当前只保存一个电脑连接。

### Windows 主要组件

- `Program.cs`：Kestrel、本地 API、Typeless 快捷键读取、SendInput 和请求去重。
- `PhoneAudioBridge.cs`：选择 VB-CABLE 播放端并使用 WASAPI 输出 PCM。
- `BluetoothReceiver.cs`：发现已配对手机、RFCOMM 连接、执行动作和返回 ACK。

## 当前单电脑限制

1. Android 服务器地址写死为 `http://127.0.0.1:8765`。
2. ADB reverse 只能指向当前 USB 主机。
3. Android 蓝牙传输只保存一个 socket。
4. 没有稳定 `computerId`、设备列表或当前目标模型。
5. Windows 服务没有配对鉴权，因为 localhost USB 隧道不需要局域网暴露。
6. 音频状态主要由手机本地布尔值推断，断线时可能和 Typeless 实际状态不同。

## 目标协议分层

```text
Android UI / Profiles / Actions
             │
       Target Device Manager
             │
   Authenticated Protocol v2
      ┌──────┼──────────┐
 USB/ADB   Local LAN   Bluetooth fallback
      └──────┼──────────┘
       Receiver Core
      ┌──────┴──────────┐
 Windows backend     macOS backend
 SendInput/WASAPI    CGEvent/Core Audio
```

业务动作只描述语义：

```json
{
  "protocolVersion": 2,
  "requestId": "UUID",
  "sessionId": "UUID",
  "targetComputerId": "UUID",
  "action": "keyChord",
  "keys": ["PRIMARY", "C"]
}
```

`PRIMARY` 在 Windows 映射为 Ctrl，在 macOS 映射为 Command。

## 多电脑原则

- 每个接收端生成稳定电脑 ID 和配对身份。
- IP 地址、USB transport ID 和蓝牙地址是连接信息，不是产品身份。
- 一台手机可以保持多个已配对电脑状态，但默认只有一个当前动作/语音目标。
- 语音切换是受控会话交接，不是把同一麦克风流广播三份。
- USB 共享切换器模式中只有当前物理端口在线；手机根据新主机健康响应自动更新目标。
- 局域网模式必须使用独立的已鉴权入口，不能把当前 localhost API 原样开放。

## 版本演进边界

### 1.5.0 可以做

- 引入配置 repository 和 schemaVersion；
- 动态按键模型；
- 安全键位映射；
- 协议 v2 基础字段；
- 旧动作兼容。

### 1.5.0 不直接做

- 完整 mDNS；
- 多电脑安全配对；
- macOS 接收端；
- 任意命令执行；
- 云端中继。

### 1.6.0 再做

- 设备发现和配对；
- Windows 多接收端；
- 手机目标选择；
- 语音交接；
- USB 共享切换器验证。

完整字段、UX、安全和验收要求以根目录 `spec plan.markdown` 为准。
