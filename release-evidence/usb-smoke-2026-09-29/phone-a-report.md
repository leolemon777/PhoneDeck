# USB 真机只读冒烟报告 — PHONE-A

- 日期：2026-09-29
- 执行方式：`E:\Android\Sdk\platform-tools\adb.exe`（完整路径，不在 PATH）
- 约束：全程只读。未执行 install / uninstall / pm clear / am start 或任何改变设备状态的操作；未读取联系人、短信、存储等个人数据。
- 匿名化：设备序列号以 **PHONE-A** 代称，原始序列号不出现在本报告及任何产物中。

## 1. 设备与连接状态

| 项 | 结果 |
|---|---|
| 匿名编号 | PHONE-A |
| 型号（getprop ro.product.model） | SM-G9880 |
| 系统版本（ro.build.version.release） | Android 12 |
| SDK（ro.build.version.sdk） | 31 |
| adb 状态 | `device`（在线且已授权），USB transport，transport_id:1 |

命令与输出（序列号已替换为 PHONE-A）：

```
> adb devices -l
List of devices attached
PHONE-A               device product:z3qzcx model:SM_G9880 device:z3q transport_id:1   [exit=0]

> adb shell getprop ro.product.model
SM-G9880

> adb shell getprop ro.build.version.release
12

> adb shell getprop ro.build.version.sdk
31
```

## 2. 已安装 PhoneDeck 相关包

| 包名 | 版本 | 证据 |
|---|---|---|
| `com.codex.phonedeck.preview` | versionName=1.6.0-dev.18-ui-preview，versionCode=24（minSdk=26，targetSdk=35） | `dumpsys package com.codex.phonedeck.preview` |
| `com.codex.phonedeck`（正式包） | **未安装** | `dumpsys package com.codex.phonedeck` → `Unable to find package: com.codex.phonedeck` |
| 含 `luma` 的包 | **无匹配** | 全量 `pm list packages` 过滤无结果 |

命令与输出：

```
> adb shell pm list packages codex.phonedeck
package:com.codex.phonedeck.preview

> adb shell pm list packages luma
（无输出，无匹配）

> adb shell pm list packages | findstr phonedeck        # 全量交叉验证
package:com.codex.phonedeck.preview

> adb shell pm list packages | findstr luma             # 全量交叉验证
（无输出，无匹配）

> adb shell dumpsys package com.codex.phonedeck | findstr Unable
  Unable to find package: com.codex.phonedeck

> adb shell dumpsys package com.codex.phonedeck.preview | findstr versionName
    versionName=1.6.0-dev.18-ui-preview

> adb shell dumpsys package com.codex.phonedeck.preview | findstr versionCode
    versionCode=24 minSdk=26 targetSdk=35
```

说明：任务指定对 `com.codex.phonedeck` 查版本；因该正式包未安装，对实际存在的同族包 `com.codex.phonedeck.preview` 补做同类只读 `dumpsys package` 查询以取得版本，属同类只读操作。

版本对照：设备上的预览包 1.6.0-dev.18-ui-preview（versionCode 24）落后于仓库当前源码基线 Android 1.6.0-dev.21（见 AGENTS.md「当前基线」；其同时载明 Samsung 新版因预览签名不同、安装通道待用户选择，见 docs/PHONE_MANAGED_DESKTOP.md）。本次按只读约束未做任何更新。

## 3. adb reverse 通道

```
> adb reverse --list
UsbFfs tcp:8765 tcp:8765
```

存在一条反向通道：设备侧 tcp:8765 → 电脑侧 tcp:8765，与 PhoneDeck USB 传输使用的本机 `127.0.0.1:8765` 端口一致（AGENTS.md 安全约束：无鉴权 HTTP 入口只监听 127.0.0.1:8765）。这表明 USB 反向链路当前已配置；但本次未获授权做端到端请求验证，不据此宣称链路可用。

## 4. 操作清单与结果汇总

| # | 操作（只读） | 结果 | exit |
|---|---|---|---|
| 1 | `adb devices -l` | 1 台设备在线（PHONE-A，state=device） | 0 |
| 2 | `shell getprop ro.product.model` | SM-G9880 | 0 |
| 3 | `shell getprop ro.build.version.release` | 12 | 0 |
| 4 | `shell getprop ro.build.version.sdk` | 31 | 0 |
| 5 | `shell pm list packages`（过滤 codex.phonedeck / luma） | 仅 `com.codex.phonedeck.preview`；无 luma 匹配 | 0 |
| 6 | `shell dumpsys package com.codex.phonedeck` | 未安装（Unable to find package） | 0 |
| 7 | `shell dumpsys package com.codex.phonedeck.preview` | 1.6.0-dev.18-ui-preview / versionCode 24 | 0 |
| 8 | `adb reverse --list` | 一条 tcp:8765→tcp:8765 反向通道 | 0 |

## 5. 未执行项（NOT_RUN）

以下均超出本次「连通性 + 版本核对」授权范围，一律未执行：

| 项 | 状态 | 所需条件 |
|---|---|---|
| USB 音频实测（48 kHz / PCM16 / mono 基线） | NOT_RUN | 需发起真实语音会话并监听电脑端播放，涉及交互与音频采集 |
| 输入注入 / 快捷键真实输入（如 FocusSink 验收） | NOT_RUN | 需在电脑端运行接收工具并从手机触发输入 |
| USB 配对 / 授权 / 撤销流程 | NOT_RUN | 属改变设备状态的操作，超出只读约束 |
| 安装 / 更新到 1.6.0-dev.21 或切换安装通道 | NOT_RUN | 会改变设备状态；且渠道迁移决策（D05）未定，规格要求未决时不动现有手机 |
| 手机端 app 运行状态（进程/前台 Activity） | NOT_RUN | `pidof` / `dumpsys activity` 不在授权命令清单内；仅以 reverse --list 作间接证据 |
| 蓝牙、局域网 HTTPS 8766、mDNS | NOT_RUN | 需额外传输层与网络环境验证 |
| Typeless、微信/豆包/千问输入法兼容 | NOT_RUN | 需真机安装对应输入法并人工交互 |
| 屏幕锁定 / TLS / iOS 采音 | NOT_RUN | 需对应硬件与场景 |

## 6. 结论

PHONE-A（SM-G9880，Android 12 / SDK 31）经 USB 与 adb 正常连接且已授权；机上仅有预览签名包 `com.codex.phonedeck.preview` 1.6.0-dev.18-ui-preview（versionCode 24），正式包 `com.codex.phonedeck` 未安装，无 `luma` 相关包；USB 反向通道 tcp:8765 已配置。连通性与版本核对全部通过，其余功能项按上表记 NOT_RUN。
