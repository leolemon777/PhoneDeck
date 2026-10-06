# 语音引擎适配指南（Voice Engines）

PhoneDeck 的"手机控制听写"不绑定某一家语音转文字软件。电脑端接收器通过
**引擎档案（Engine Profile）** 描述每个语音软件的触发快捷键、触发方式与录音状态
探测进程，同一套机制在 Windows 与 macOS 上工作。本文说明内置引擎、档案格式，
以及如何为零代码适配新引擎。

## 工作原理回顾

手机按住/点击语音按钮后：

1. 手机开始录音，把 48 kHz PCM 流发给电脑端接收器；
2. 接收器把音频写入虚拟声卡（Windows: VB-CABLE / macOS: BlackHole 2ch）；
3. 接收器按当前引擎档案合成引擎的全局快捷键（Windows `SendInput` / macOS `CGEvent`）；
4. 引擎从虚拟声卡读音频并转文字，输入到当前焦点窗口；
5. 接收器用 Core Audio 音频会话枚举探测引擎进程是否真的在录音（`/api/health` 的
   `voiceEngine.capturing`），手机据此反向同步。

因此，一个语音软件只要满足三点就能被适配：

- 支持自定义（或固定且可注入的）全局快捷键；
- 可以把输入设备选成虚拟声卡；
- 录音时在其进程中出现 WASAPI/Core Audio 采集会话。

## 内置引擎

| id | 显示名 | 快捷键来源 | 触发方式 | 状态 |
|---|---|---|---|---|
| `typeless` | Typeless | 自动读取其 `app-settings.json`（Windows 读取失败回退 RightAlt，macOS 回退 Fn） | 切换（toggle） | 已真机验证 |
| `doubao` | 豆包 | 档案默认 `Ctrl+D`（Windows） | 切换（toggle） | **实验性** |
| `wetype` | 微信输入法 | 无默认，必须手动配置 | 按住（hold） | **实验性** |

实验性档案的快捷键与进程名来自公开资料，未经本仓库真机核实，且可能随第三方
版本变动。失效时按下面的方法覆盖修正即可，欢迎提 PR 回馈。

> 千问输入法（按住右 Alt）未内置，但可直接复制本文"示例档案"一节中的
> 现成 JSON 使用。

## 配置文件位置

数据目录（`PHONEDECK_DATA_DIR` 环境变量，默认接收器 EXE 旁 `data\`）：

```
data\
├── voice-engine-settings.json    # 当前引擎选择 + 手动快捷键覆盖
└── voice-engines\                # 扩展/覆盖档案（每个 JSON 一个引擎）
    └── qianwen.json
```

macOS 端数据目录默认是 `~/Library/Application Support/PhoneDeck/`。
所有改动**重启接收器后生效**；Windows 端也可以在控制台（设置 → 语音引擎）
界面修改，保存时自动重启。

### voice-engine-settings.json

```json
{
  "activeEngine": "wetype",
  "shortcutOverrides": {
    "wetype": { "dictation": "Ctrl+Shift+V" }
  }
}
```

- `activeEngine`：当前引擎 id；
- `shortcutOverrides`：手动指定某引擎某模式的快捷键，**优先级最高**
  （引擎配置文件 > 档案默认值）。无配置可读的引擎（如微信输入法）
  必须在这里填写后受管听写才可用。

## 引擎档案格式

`voice-engines\*.json` 每个文件描述一个引擎；id 与内置档案相同时**覆盖**内置，
不同则新增。字段：

```json
{
  "id": "qianwen",
  "displayName": "千问输入法",
  "experimental": true,
  "processNames": ["Qianwen", "qwen"],
  "settingsReader": null,
  "requiresVirtualCable": true,
  "modes": [
    {
      "id": "dictation",
      "label": "语音输入",
      "keys": "RightAlt",
      "macKeys": null,
      "trigger": "hold"
    }
  ]
}
```

| 字段 | 说明 |
|---|---|
| `id` | 引擎标识（小写）。`voice-engine-settings.json` 与健康接口引用它。 |
| `displayName` | 手机与控制台显示名。 |
| `experimental` | 可选；true 时启动横幅与控制台会标注"实验性"。 |
| `processNames` | 录音状态探测匹配的进程名（忽略大小写，相等或包含即命中）。语音在子进程的软件填主进程名即可。 |
| `settingsReader` | 可选；目前仅 `"typeless"`（自动读其配置文件）。其余引擎留 null，快捷键用 `shortcutOverrides` 或档案默认值。 |
| `requiresVirtualCable` | 受管听写是否要求引擎输入设备选虚拟声卡（默认 true）。 |
| `modes[]` | 引擎的工作模式（Typeless 有听写/翻译/问答三个；多数软件只有一个）。 |
| `modes[].id` | 模式标识，手机端 `/api/dictation` 的 `mode` 参数。 |
| `modes[].label` | 手机端模式 chips 的显示名。 |
| `modes[].keys` | Windows 默认快捷键（如 `"Ctrl+D"`、`"RightAlt"`）。null = 无默认。 |
| `modes[].macKeys` | macOS 默认快捷键。null = macOS 无默认。 |
| `modes[].trigger` | `toggle`（按一下开始、再按一下结束）或 `hold`（按住说话、松开结束）。 |

`trigger` 是适配的关键差异：Typeless 这类软件按一下切换；微信输入法、千问输入法
这类"按住说话"软件，接收器会在会话开始时按下快捷键并保持、结束时释放，并保证
任何异常路径（断流、进程退出）都释放按键，不悬挂修饰键。

## 示例档案（可直接复制）

### 千问输入法（Windows，按住右 Alt 说话）

```json
{
  "id": "qianwen",
  "displayName": "千问输入法",
  "experimental": true,
  "processNames": ["Qianwen", "qwen"],
  "modes": [
    { "id": "dictation", "label": "语音输入", "keys": "RightAlt", "trigger": "hold" }
  ]
}
```

前提：在千问设置 → 桌面工具中开启语音输入法，并把输入设备选为 CABLE Output。
双击右 Alt 的"智能语音指令"模式可再加一项（trigger 同为 hold，快捷键需要能在
千问中改造成可注入组合后再覆盖）。

### 微信电脑客户端（按住 Ctrl+Win 说话）

```json
{
  "id": "wechat",
  "displayName": "微信",
  "experimental": true,
  "processNames": ["WeChat", "Weixin"],
  "modes": [
    { "id": "dictation", "label": "按住说话", "keys": "Ctrl+Win", "trigger": "hold" },
    { "id": "continuous", "label": "持续输入", "keys": "Ctrl+Win+Shift", "trigger": "toggle" }
  ]
}
```

### doubao-murmur 等社区工具（右 Alt 切换）

```json
{
  "id": "murmur",
  "displayName": "doubao-murmur",
  "experimental": true,
  "processNames": ["murmur"],
  "modes": [
    { "id": "dictation", "label": "语音输入", "keys": "RightAlt", "trigger": "toggle" }
  ]
}
```

### 闪电说（Windows 按住 Ctrl+Win；macOS 需先改键）

```json
{
  "id": "shandian",
  "displayName": "闪电说",
  "experimental": true,
  "processNames": ["Shandian"],
  "modes": [
    { "id": "dictation", "label": "按住说话", "keys": "Ctrl+Win", "macKeys": null, "trigger": "hold" }
  ]
}
```

适配注意：

- `processNames` 为占位示例，首次适配时按下方核对清单第 4 步，用任务管理器 /
  活动监视器核实真实进程名后替换，再用 `/api/diagnostics` 验证 `capturing` 跟随录音变化；
- Windows 默认"按住 左Ctrl+左Win 说话"，可注入；若在软件内改过快捷键，用
  `shortcutOverrides` 覆盖；
- macOS 默认触发键 Fn 是固件键、无法程序注入——需在闪电说设置中改为右 Command
  或 Ctrl 组合等可注入按键，并把组合写入 `shortcutOverrides`；
- 输入设备须选 `CABLE Output`（Windows）/ `BlackHole 2ch`（macOS）。

## 实验性档案核对清单

为一个新引擎写档案时，按顺序核对：

1. **快捷键**：在目标软件设置里找到/设置全局语音快捷键。优先可注入组合
   （Ctrl/Alt/Shift 开头）。Fn、专用麦克风键等固件键无法程序注入。
   Windows 快捷键写法：`"Ctrl+D"`、`"Ctrl+Shift+V"`、`"RightAlt"`、`"F6"`。
2. **触发方式**：按一下开始再按一下结束 → `toggle`；按住才说话 → `hold`。
3. **输入设备**：把该软件的麦克风/输入设备选成 `CABLE Output`
   （Windows）或 `BlackHole 2ch`（macOS）。
4. **进程名**：开始一次语音输入后，打开任务管理器（Windows）/活动监视器
   （macOS）找到占用麦克风的进程名，填入 `processNames`。可用
   `GET /api/diagnostics`（loopback 8765）观察 `voiceEngine.capturing` 是否
   随实际录音变化来验证。
5. **手机端**：手机健康轮询会自动发现引擎与模式；模式多于一个时手机显示
   模式 chips，只有一个模式时自动隐藏。

## 对健康接口的影响

`/api/health` 新增 `voiceEngine` 块（新手机端优先读取）：

```json
"voiceEngine": {
  "id": "wetype",
  "displayName": "微信输入法",
  "experimental": true,
  "capturing": false,
  "virtualCableSelected": null,
  "microphone": null,
  "modes": [
    { "id": "dictation", "label": "语音输入", "trigger": "hold",
      "configured": true, "keys": ["CTRL", "SHIFT", "V"] }
  ]
}
```

`virtualCableSelected` 为 `null` 表示该引擎无可读配置、无法校验（手机端不据此
阻断）。旧 `typeless` 块继续保留（从当前引擎映射），未升级的旧手机端不受影响。

## 与共享麦克风模式的关系

共享麦克风（shared）模式下手机只供音、**不控制任何引擎**，由每台电脑自己的
快捷键触发本机语音软件，与引擎档案无关。
