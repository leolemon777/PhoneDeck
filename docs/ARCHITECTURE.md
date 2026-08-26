# PhoneDeck 架构说明

## 当前 1.5.0 候选版数据流

```text
Android MainActivity
  ├─ schemaVersion=1 动态按钮配置 ─┐
  ├─ protocol v2 keyChord JSON ────┤
  └─ AudioRecord PCM 48k mono ─────┤
                                │ ADB reverse 127.0.0.1:8765
Windows PhoneDeck.Server        │
  ├─ /api/input ◀──────────────────┘
  │    ├─ 旧固定动作兼容
  │    └─ v2 信封/目标/键位白名单 → SendInput → 当前前台窗口
  ├─ /api/audio/stream
  │    └─ sessionId → NAudio/WASAPI → CABLE Input → CABLE Output → Typeless
  ├─ /api/dictation/start|stop
  │    └─ 幂等会话 → Typeless；断流时尽力复位
  └─ BluetoothReceiver
       └─ RFCOMM 快捷键 + ACK；无音频
```

### Android 主要组件

- `MainActivity.java`：动态主界面、开始/取消/暂停/继续/停止语音状态、连接选择、动作发送和反馈。
- `SettingsActivity.java`：设置入口和点击/按住语音模式；点击模式使用同一主按钮开始/停止，另保留暂停/继续辅助控制。
- `ShortcutConfigRepository.java`：原子保存、版本检查、损坏备份和默认配置。
- `ShortcutSettingsActivity.java` / `ShortcutEditActivity.java`：列表、排序、编辑、测试和恢复。
- `KeyPickerActivity.java` / `KeyCatalog.java`：受控键位选择、规范化和显示。
- `AudioStreamer.java`：AudioRecord、PCM 音量计算和 HTTP chunked 音频流；暂停时停止
  AudioRecord，并按实时速率发送 PCM 静音保持同一 HTTP/Typeless 会话，继续时恢复采集。
- `BluetoothTransport.java`：手机作为 RFCOMM 服务端，当前只保存一个电脑连接。

### Windows 主要组件

- `Program.cs`：Kestrel、本地 API、Typeless 快捷键读取、SendInput 和请求去重。
- `InputCommandProcessor.cs`：协议 v2 信封、目标电脑和动作验证。
- `ReceiverIdentity.cs`：首次启动生成并持久化稳定电脑 ID。
- `PhoneAudioBridge.cs` / `DictationSessionManager.cs`：WASAPI 音频与 Typeless 会话所有权；只有虚拟音频输出真正启动后才公布会话。
- `TypelessStateProbe.cs`：枚举 Windows 采集端的 Core Audio 会话，核对 Typeless 进程是否真正处于录音状态，不再只依赖服务内部布尔值。
- `BluetoothReceiver.cs`：发现已配对手机、RFCOMM 连接、执行动作和返回 ACK。

## 当前单电脑限制

1. Android USB 服务器地址仍为 `http://127.0.0.1:8765`。
2. ADB reverse 只能指向当前 USB 主机。
3. Android 蓝牙传输只保存一个 socket。
4. 已有单机稳定 `computerId` 和请求目标字段，但尚无设备列表、发现或配对模型。
5. Windows 服务没有配对鉴权，因为 localhost USB 隧道不需要局域网暴露。
6. 音频/Typeless 已有显式会话清理，但真实 USB 切换和外部 Typeless 状态仍待硬件验收。

## 已实现的协议 v2 基础与目标分层

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
