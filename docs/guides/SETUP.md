# 新电脑开发、构建与运行

核对日期：2026-09-10。本文负责开发环境和运行接线；编译、测试、签名、打包、CI 与发布门槛统一见
[BUILD_PIPELINE.md](../BUILD_PIPELINE.md)。当前 Android/Windows 可开发运行，Mac 为待真机验收的预览，iOS 尚无工程。

## 1. 获取源码

```powershell
git clone https://github.com/leolemon777/PhoneDeck.git
cd PhoneDeck
git status
```

仓库当前为私有，需要账号访问权限。使用 GitHub CLI、Git Credential Manager 或 SSH 登录。
确认所需分支和 commit：当前 dev.17/dev.11 改动在 [PR #5](https://github.com/leolemon777/PhoneDeck/pull/5)，
核对时尚未合并；默认克隆 main 不代表取得最新候选。开始修改前按 AGENTS.md 建立独立分支。

## 2. 开发依赖

| 目标 | 编译需要 | 真机运行另需 |
|---|---|---|
| Android | JDK 17、SDK Platform 35、Build Tools 35.0.0、仓库 Gradle Wrapper 8.9；Android Studio 可选 | 手机录音权限；USB 路径需要 Platform Tools/ADB |
| Windows 接收端和控制台 | Windows x64、.NET 8 SDK | VB-CABLE、所选语音输入法；自包含发布不要求预装 .NET |
| macOS 接收端 | Mac、.NET SDK、zsh/codesign；使用独立 macOS 工程 | macOS 14.2+、BlackHole、输入法、辅助功能及相关权限；见 [MACOS_SETUP.md](./MACOS_SETUP.md) |
| 签名更新包 | PowerShell 7、已构建 EXE/APK、匹配渠道的签名材料 | 已接入更新能力的 Windows/Android |

Android 设置 `JAVA_HOME` 和 `ANDROID_HOME`，或在 `work/phone-deck/android/local.properties`
填写本机 SDK 路径。不要复制其他电脑的绝对路径；该文件不提交 Git。无需安装全局 Gradle。
Windows 接收端不能直接当作 Mac 程序运行；Mac 源码位于 `work/phone-deck/macos/PhoneDeck.Receiver`。
VB-CABLE、BlackHole 与输入法不属于编译依赖，也不随本仓库分发。

## 3. 构建与 Android 签名渠道

从仓库根目录执行 [构建手册第 4 节](../BUILD_PIPELINE.md#4-当前可执行的构建命令) 的命令。
Android 包含 assemble、单元测试与 lint；Windows 同时发布接收端和控制台。
统一更新只替换两个 EXE，控制台须带 `IncludeNativeLibrariesForSelfExtract=true` 发布参数。

仓库只包含 `work/phone-deck/signing.properties.example`。本机项目签名配置的位置为：

```text
work/phone-deck/signing/signing.properties
work/phone-deck/signing/phonedeck-release.jks
```

密钥文件名以实际配置为准。存在项目签名配置时，当前 Gradle 的 debug/release 共用该签名。
缺少配置时，debug 使用本机开发证书，release 没有发行签名。不同电脑生成的 debug 包不保证能互相覆盖。

当前 Samsung dev.17 使用开发证书渠道（指纹前缀 `653884d0…`）；历史长期证书
（`df327953…`）是另一渠道。不要把恢复历史密钥等同于能覆盖当前手机。
安装前用 SDK Build Tools 的 `apksigner verify --print-certs` 核对候选 APK 与目标安装渠道。
签名不匹配时停止覆盖，保留应用数据，先明确渠道迁移方案。

确认签名匹配后，开发者可从仓库根目录执行：

```powershell
adb devices -l
adb install -r work/phone-deck/android/app/build/outputs/apk/debug/app-debug.apk
```

这只是 USB 开发安装；产品自身更新必须另行验证下载、验签、系统安装确认与版本回报。
正式公开发行需要正式签名渠道和候选验收，不能直接发布临时 debug 包。

## 4. Windows 首次运行

> 语音延迟调优（可选）：接收端默认让 VB-CABLE 输出常开并在空闲时输出静音，会话开始不再初始化
> WASAPI；如需关闭设置环境变量 `PHONEDECK_WARM_AUDIO=0`。VB-CABLE 控制面板中的内部缓冲（Max Latency）
> 也会叠加延迟，可按实测调小。尾音静音默认 400 ms，可用 `PHONEDECK_OUTPUT_TAIL_MS`（100–1000）调整。
> 端到端延迟用 `scripts/performance/voice-loopback-delay.py` 实测，两端日志用
> `scripts/performance/voice-latency-report.py` 汇总。

1. 将同一候选构建的接收端与控制台放到独立运行目录，保留已有 `data`；不要直接覆盖正在运行的 EXE。
2. 自行安装 VB-CABLE，确认 `CABLE Input` 和 `CABLE Output` 存在，按驱动要求重启。
3. 安装并启动语音输入法，在输入法中选择 `CABLE Output` 作为麦克风。
4. 启动控制台/接收端；若控制台已启动接收端，不再额外启动第二个服务器进程。
5. 手机开启 USB 调试，解锁并批准该电脑的调试密钥；运行下面的开发连接命令。

```powershell
adb reverse tcp:8765 tcp:8765
adb shell am start -n com.codex.phonedeck/.MainActivity
```

6. 完成手机与该电脑的配对，确认电脑名称和目标；首次 USB 配对后再验证同一 Wi-Fi 下的连接。
7. 在真正的文字输入框放置光标，验证快捷键，以及开启共享后在电脑上按 Typeless 快捷键听写。

健康检查：

```powershell
Invoke-RestMethod http://127.0.0.1:8765/api/health
```

检查 `ok`、版本、`computerId`、能力及音频状态。LAN 使用 HTTPS 8766、配对令牌和证书固定；
不要为了连接方便将无鉴权 HTTP 8765 暴露到局域网。

Typeless 默认输入设备/选错设备可能导致语音启动被拒绝；确认其明确使用虚拟麦克风。
其他输入法按 [VOICE_ENGINES.md](./VOICE_ENGINES.md) 配置档案、快捷键并实际验证，实验档案不等于正式兼容。

## 5. 语音模式与多设备

- 共享麦克风：手机向已连接且具备音频能力的电脑供音；每台电脑通过自己的输入法快捷键控制转写。
- 现有桌面共享请求可由手机观察并联动，因此不能沿用“每次打开 App 必须手动开启”的旧说明。
  手机授权、用户停止、重启与持久请求的具体契约仍是总计划 T02 的验收项。
- 共享采音显示 Android 常驻通知；验证手机/通知停止后采音和连接实际结束。

每台电脑有独立身份和配对记录，不要复制另一台电脑的 `data` 来配置新电脑。
没有统一更新：每台电脑在本机构建或拷贝新接收端后手动替换，手机用 `adb install -r` 覆盖安装（不要卸载）。

## 6. 重连、配置与故障定位

USB 断开或 ADB transport 改变后可能需要重建 reverse。
`scripts/windows/AutoReconnectUsb.ps1` 用于恢复转发和唤醒 App；先检查控制台现有重连机制，
避免叠加多个看门狗。真实连续断线、锁屏、输入法与音频恢复仍需硬件验收。

快捷键布局保存在 Android 私有目录的 `shortcut-config.json`，普通文件管理器不可直接访问。
测试前使用应用支持的配置导出功能；不要用清除应用数据修复连接问题，否则会丢失配对和设置。
配置损坏时程序会保留损坏文件并恢复默认布局，迁移仍需验证编辑、排序、隐藏与重启持久化。

| 现象 | 检查方向 |
|---|---|
| ADB unauthorized | 解锁手机并批准本机调试密钥 |
| USB 无法连接 | ADB 设备在线、reverse 转发、服务器健康；不要绕过配对 |
| 音量条有变化却没有文字 | 输入法是否启动、麦克风是否为 CABLE Output、目标输入框与快捷键 |
| 音频设备不可用 | VB-CABLE 驱动与 CABLE Input 播放端是否启用 |
| 快捷键对管理员窗口无效 | Windows 权限隔离会限制低权限进程注入输入 |
| APK 无法覆盖安装 | 比较签名、versionCode 与系统安装结果，保留现有数据 |

## 7. 开发迁移与秘密材料

需要维护同一签名渠道时，通过安全离线方式单独迁移相应密钥与配置，放回 Git 忽略的
`work/phone-deck/signing/`。Android 安装签名与更新清单 RSA 私钥是两套材料，职责不同。
不要通过 GitHub、Issue、PR 或聊天传输私钥和密码；公开公钥/证书指纹可用于核验。

新电脑自行建立 ADB 信任和登录凭据，不迁移另一台机器的 ADB 私钥、访问令牌、个人输入法配置或录音。
`bin`、`obj`、`build`、`.gradle`、`artifacts`、`dist` 和第三方驱动安装包不提交 Git；从源码重建产物。
