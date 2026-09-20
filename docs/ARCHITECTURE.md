# PhoneDeck 架构说明

## 当前实现与目标架构的边界

源码基线：Android 1.6.0-dev.17 / Windows 接收端 1.6.0-dev.11 /
macOS 预览 2.0.0-dev.3；当前协议仍为 v2。
面向 Android/iOS × Windows/macOS 的目标架构、动态多设备模型、逐手机授权、
会话状态、引擎适配和阶段依赖见 [长期规格 v0.6 第 0 章](../spec%20plan.markdown)。
这些是后续规划，iOS、无线扫码配对与 Receiver.Core 公共库尚不能当作当前实现。

目前共享请求可由电脑持久化并被手机轮询跟随，授权/重启语义仍需按规格 0.8 迁移；
当前电脑 LAN 令牌也不能当作已实现逐手机凭据与撤销。
下文旧版本标记描述各能力引入时的结构，不替代当前支持矩阵或真机验收记录。

## 当前 Android/Windows 数据流

统一更新使用独立路径：电脑本地更新页 → 签名包/批量请求 → 手机 FleetUpdateActivity 缓存与分发 →
各接收端 FleetUpdates 校验与空闲准入 → 独立 FleetUpdateWorker 固定文件替换、健康校验与异常回退。
Android 通过非导出的只读 UpdateApkProvider 向系统安装器临时授予 APK 读取权；自身包名、签名与版本需一致。
手机的离线补更队列持久化，主页在前台且目标恢复连接时再次协调，不承诺后台静默分发。
Mac/iOS 不参与当前安装协议。详见 [统一更新协议](./FLEET_UPDATES.md)。

```text
Android MainActivity
  ├─ schemaVersion=1 动态按钮配置 ─┐
  ├─ protocol v2 keyChord/text/macro JSON ─┤
  └─ AudioRecord PCM 48k mono ─────┤
                                │ ADB reverse 127.0.0.1:8765
                                │ 或证书固定 HTTPS Wi-Fi :8766
Windows PhoneDeck.Server        │
  ├─ /api/input ◀──────────────────┘
  │    ├─ 旧固定动作兼容
  │    └─ v2 信封/目标/键位白名单 → SendInput → 当前前台窗口
  ├─ /api/audio/stream
  │    └─ sessionId + targetComputerId header → NAudio/WASAPI → CABLE Input → CABLE Output → 语音引擎（默认 Typeless，档案可换）
  ├─ /api/dictation/start|stop
  │    └─ 幂等会话 → 当前语音引擎（toggle/hold 两种触发）；断流时尽力复位
  └─ BluetoothReceiver
       └─ RFCOMM 快捷键 + ACK；无音频
```

### Android 主要组件

- `MainActivity.java`：动态主界面、开始/取消/停止语音状态、连接选择、动作发送和反馈。
- `SettingsActivity.java`：设置入口和点击/按住语音模式；点击模式使用同一主按钮开始/停止。
- `ShortcutConfigRepository.java`：原子保存、版本检查、损坏备份和默认配置。
- `ShortcutSettingsActivity.java` / `ShortcutEditActivity.java`：列表、排序、编辑、测试和恢复。
- `KeyPickerActivity.java` / `KeyCatalog.java`：受控键位选择、规范化和显示。
- `AudioStreamer.java`：AudioRecord、20 ms PCM 分块、音量计算和 HTTP chunked 音频流；
  TLS 建连期间最多缓存 1 秒 pre-roll，降低首音节丢失概率。
- `PhoneAudioService.java` / `SharedAudioBroadcaster.java`：共享麦克风的前台服务所有权、
  Wi-Fi/CPU 锁、在线接收端探测，以及单次 `AudioRecord` 到多台电脑的独立有界队列扇出；
  单台断流按 500 ms–30 s 退避重连，不阻塞采音和其他接收端。
- `BluetoothTransport.java`：手机作为 RFCOMM 服务端，当前只保存一个电脑连接。
- `TargetDeviceManager.java`：保存已确认电脑的 ID、显示名、平台、手机端编号、候选 LAN
  地址、最近成功地址、访问密钥和证书指纹；IP 只作为可替换缓存。
- `PhoneDeckHttp.java` / `PhoneDeckLanClient.java` / `LanDiscoveryClient.java`：并行探测候选
  地址并通过受限 UDP 广播发现接收端；任何发现结果仍需 HTTPS、密钥、证书固定与目标 ID
  校验后才可用于控制和音频。
- `android/artwork/phonedeck-app-icon-1024.png` 与 `res/mipmap-*`：Android 图标母版及 mdpi–xxxhdpi 确定性切图，清单的普通与圆形图标共用该资源。

### Windows 主要组件

- `Program.cs`：Kestrel、本地 API、Typeless 快捷键读取、SendInput 和请求去重。
- `InputCommandProcessor.cs`：协议 v2 信封、目标电脑和动作验证。
- `ReceiverIdentity.cs`：首次启动生成并持久化稳定电脑 ID。
- `LanIdentity.cs`：生成并持久化局域网 TLS 证书和随机访问密钥；配对资料只允许从 USB
  loopback 端口读取。Wi-Fi 端口独立监听 8766，未携带正确密钥返回 401。
- `LanDiscoveryResponder.cs`：在 UDP 8767 回应不含密钥的最小身份信息，供已配对手机更新
  候选 IP；不会绕过 HTTPS 鉴权。
- `PhoneAudioBridge.cs` / `DictationSessionManager.cs`：WASAPI 音频与 Typeless 会话所有权；
  `managed` 保留 pre-roll 与状态机，`shared` 立即持续写入虚拟声卡且绝不操作 Typeless。
- `PhoneDeckRuntimeAbstractions.cs` / `PhoneDeck.Server.Tests`：隔离真实音频与 Typeless 控制，回归验证失败重试、状态探针不可用和断流恢复。
- `TypelessStateProbe.cs`：枚举 Windows 采集端的 Core Audio 会话，核对 Typeless 进程是否真正处于录音状态，不再只依赖服务内部布尔值。
- `BluetoothReceiver.cs`：发现已配对手机、RFCOMM 连接、执行动作和返回 ACK。
- `PhoneDeck.ControlCenter`：.NET 8 WPF 控制台，采用 Web2WPF Aether 主题资源；通过本地健康接口展示接收端、Wi-Fi、音频和 USB 状态，并管理进程、便携设置、Agent 快捷操作与系统托盘。

### macOS 2.0.0-dev.2 预览组件

- `macos/PhoneDeck.Receiver/Program.cs`：Kestrel 本地 HTTP 8765、安全 HTTPS 8766、健康检查、USB 配对和协议 v2 输入入口。
- `MacKeyboardInput.cs`：CGEvent 输入后端、macOS 虚拟键码白名单、Unicode 文字、请求去重、宏限制和异常按键释放。
- `ReceiverIdentity.cs` / `LanIdentity.cs`：在 `~/Library/Application Support/PhoneDeck` 持久化稳定电脑 ID、证书与随机访问令牌；敏感文件限制为当前用户读写。
- `LanDiscoveryResponder.cs`：复用 UDP 8767 最小发现应答，仍需经过手机已有的证书固定、令牌和 `computerId` 校验。
- `UsbWatchdog.cs`：在 Mac 检测 `adb` 并恢复 `adb reverse tcp:8765 tcp:8765`。
- `CoreAudioHalOutput.cs` / `MacPhoneAudioBridge.cs`：AUHAL 定向绑定 BlackHole 设备 UID，
  mono PCM16 复制为 stereo，并以有界实时环形缓冲处理欠载、溢出和断流。
- `MacTypelessConfiguration.cs` / `MacTypeless.cs`：查找 Typeless 配置、读取或覆盖三种
  快捷键与麦克风，并通过 macOS 14.2+ Core Audio 进程对象的 `isRunningInput` 核对采集。
- `MacDictationSessionManager.cs`：复用受控听写的启动、停止、失败复位和断流清理语义。
- `PhoneDeck.Receiver.Tests`：不调用原生框架即可验证平台按键映射、配置解析、mono→stereo、
  环形缓冲、managed pre-roll、shared 立即输出、冲突和会话复位。

Mac 现在声明 `phoneAudio/sharedMicrophone/managedDictation`；`audio.available` 仍以实际找到
BlackHole 为准。源码已跨平台编译并通过测试，但尚未在真实 Mac 上验证 AUHAL、CGEvent、
TCC 权限、Kestrel TLS、ADB 或局域网防火墙行为。

## 当前单电脑/传输限制

1. Android USB 服务器地址仍为 `http://127.0.0.1:8765`。
2. ADB reverse 只能指向当前 USB 主机。
3. Android 蓝牙传输只保存一个 socket。
4. 已有 USB 首次配对、HTTPS 鉴权和自定义 UDP 发现，但尚无二维码/验证码配对、凭据撤销或标准 mDNS 浏览。
5. 多步宏已进入实验实现，仍缺完整真机输入、焦点保障和失败策略验收。
6. Windows 音频/Typeless 已有显式会话清理，但三台电脑联合切换和异常网络场景仍待硬件验收。
7. macOS 双语音模式已有预览源码；真实 BlackHole/Typeless、蓝牙和正式签名仍待验收或接入。

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
 SendInput/WASAPI    CGEvent + AUHAL/BlackHole
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

听写会话也必须绑定同一目标。JSON 端点使用 `protocolVersion`、`sessionId`、
`targetComputerId`；PCM 流使用 `X-PhoneDeck-Protocol: 2`、
`X-PhoneDeck-Session`、`X-PhoneDeck-Computer-Id` 和可选
`X-PhoneDeck-Audio-Mode: managed|shared` 请求头；缺省模式为 `managed`。服务端在进入音频或
Typeless 状态机前校验目标，不匹配直接返回 400。

`PRIMARY` 在 Windows 映射为 Ctrl，在 macOS 映射为 Command。当前 Android 编辑器仍保存
Windows 风格的 `CTRL/WIN/ALT`；2.0.0-dev.2 Mac 后端为默认布局提供兼容映射，并对截图、
窗口切换和输入法切换做专用转换。后续 Android 编辑器需要显式区分跨平台“主键”与真实
Control / Option / Command，消除自定义组合的歧义。

## 多电脑原则

- 每个接收端生成稳定电脑 ID 和配对身份。
- IP 地址、USB transport ID 和蓝牙地址是连接信息，不是产品身份。
- 一台手机可以保持多个已配对电脑状态；当前电脑只控制快捷键、文字、宏与 managed 听写目标。
- `managed` 是单目标受控会话；`shared` 例外地把同一个 `sessionId` 的手机麦克风流扇出到
  所有合格接收端，接收端只供音，由各电脑本机 Typeless 快捷键决定是否转写。
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

当前 `1.6.0-dev.5` 已完成 Windows 安全 Wi-Fi 入口、USB 自动配对、无线心跳、受限 UDP
自动发现、单目标听写和共享麦克风扇出。macOS `2.0.0-dev.2` 已新增兼容相同安全传输的
CGEvent、AUHAL/BlackHole 和 Typeless 会话预览。仍缺凭据撤销/重配、标准 mDNS/Bonjour、
真实 Mac 权限/音频验收和 Windows/macOS 三机联合压力测试。

完整字段、UX、安全和验收要求以根目录 `spec plan.markdown` 为准。
