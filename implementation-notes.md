# implementation-notes — 2026-09-05 dev.8 软色纸卡 UI

## 需求

用户提供 8 屏参考图（soft-neo 风格：暖奶油/暖黑底、纯白/深咖大圆角卡片、
胶囊控件、单一蓝色主操作、橙绿点缀、柔投影），要求手机端 UI "一模一样"。

## 决策

- **风格映射而非照抄页面**：把参考图的设计语言（色板/圆角/胶囊/投影/深浅双调）
  应用到 PhoneDeck 现有信息架构（连接卡、快捷键网格、语音坞、目标芯片），
  不改动任何交互与行为（AGENTS.md 不可破坏行为全部保留）。
- **跟随系统深浅色**：参考图浅深两套各 4 屏。原主题系统是单 frost 浅色，
  现改为 `load(Context)` 按系统 `UI_MODE_NIGHT_MASK` 返回 ivory/espresso
  双调色板，单 id `soft`；旧 PREFS 里的主题 id 自动覆盖，旧入口
  `all()/byId()/save()` 保留签名做兼容。
- **深色主操作 = 白色胶囊**（非蓝）：参考图深色屏的 FAB 是白圆+黑加号，
  深色 `primary=#F1EEE6 / onPrimary=#191713`，浅色保持蓝 `#3D6BF3`。
- **快捷键卡片回归纸卡**：不再整体着色（shortcutColor → surface），
  预设色只出现在 chord 小胶囊上（shortcutAccent 换参考图色板），
  与参考图"白卡+彩色点缀"一致。
- **去玻璃化**：shape() 删除 frost 渐变分支；MainActivity 不再挂
  FrostedBackdropView（极光背景与纸卡风格互斥）；isFrost() 保留恒 false
  以免大改调用面（VoiceLevelView 自动走默认柱状波形）。
- **圆角/投影令牌**：chord 10、模式芯片 15/30dp 全胶囊、smallButton 19/38、
  语音退格回车 23/46、快捷键卡 20、目标芯片 20、连接卡 24、语音坞 28、
  主语音按钮 36/72 全胶囊；投影 4/6/14dp（参考图为柔投影无描边，
  连接卡/语音坞去掉 1dp stroke，目标芯片保留未选中描边以区分 online/alpha）。

## 权衡 / 偏差

- 参考图的底部 Tab 导航、搜索框、图表等组件不存在于 PhoneDeck，未引入；
  语音坞保持固定底部（行为不可破坏）。
- 深色下 `primary` 为米白，故深色模式中"点击说话模式"等 primary 强调文字
  呈米白而非蓝——与参考图深色屏"几乎无蓝、用白与橙绿点缀"一致。
- SettingsActivity 的 `themeOption()`/`selectTheme` 成为死代码但保留
  （surgical edit 原则；javac/lint 均不报错）。

## 签名与部署（重要）

- 手机已装 dev.7 的证书 SHA-256 `653884d0…`（外部机器签名），与 2号机
  备份密钥 `DF:32:79:53…`（PhoneDeck-Signing-Backup → work/phone-deck/signing/，
  已 gitignore）均不一致 → 无法覆盖安装。已 `adb uninstall` 后安装本机构建
  （jks 签名，versionCode 14），**一次性重置手机端数据**（配对槽位、
  权限、本地偏好）。Agent 快捷键由电脑端同步自动恢复；2号机配对在
  adb reverse + 启动 App 后自动完成（连接卡显示"往里走的COMPUTE · Wi-Fi"）。
  此前配过的 1号/3号（DESKTOP-74F6FT5，离线）需下次开插 USB 重新配对。
- 权限：RECORD_AUDIO、BLUETOOTH_CONNECT 已 adb 授予；POST_NOTIFICATIONS
  在该 ROM 报 Unknown permission（未声明于 targetSdk 授权路径），通知权限
  待用户在系统设置里允许（共享麦克风前台服务通知不受影响，仅隐藏通知展示）。

## 验证

- `:app:assembleDebug`、`:app:lintDebug` 通过（版本 versionCode 14 / 1.6.0-dev.8）。
- 真机 Samsung SM-G9880 截图验证浅色与深色（diagnostics/pd-light.png、
  pd-dark.png）：奶油底/白卡/蓝色胶囊主按钮 + 暖黑底/深咖卡/白色胶囊主按钮，
  与参考图逐项对照一致；USB 自动重配对生效。

## 风险

- 旧版升级到 dev.8 的其他设备都会因签名差异（vs 各自原签名）触发卸载重装，
  需重新插线配对一次。
- `PhoneDeckTheme.byId` 恒返 ivory，若有第三方代码路径直接 byId 渲染将失去
  深色（仓库内无此调用，仅防御性备注）。


# 2026-09-05 dev.9 追记：主题选择器全量恢复

用户反馈：原 9 套配色不能丢、参考图 4 配色也要做成可选。dev.8 的单主题
方向被推翻，改为：

- PhoneDeckTheme 变为主题仓库：`soft`（自动档，跟随系统，默认）+
  ivory/pearl/espresso/cocoa（参考图 4 配色）+ frost/paper/ocean/oled/
  inklight/inkdark/goldblue/goldamber/goldforest（历史 9 套，调色板与
  玻璃/单色/染色专属渲染从 git 0e15bd7 原样恢复）。
- load()：pref 无记录或 "soft" → 按昼夜解析并包装为 SOFT 身份
  （softAuto），保证设置页只标选一项；显式选择则存具体 id。
- 设置页恢复 14 项选择器（themeOption 原组件）；MainActivity onResume
  id 变化自动 recreate（既有逻辑）。
- 修复两个真机发现的问题：①自动档对象沿用 ivory id 导致列表双选中；
  ②isSoft() 未含 SOFT 导致自动档快捷键卡出现彩色染色。
- versionCode 15 / 1.6.0-dev.9；assembleDebug/lintDebug 过；真机截屏验证
  自动档白卡、冰川玻璃极光渲染、主题列表标选，Wi-Fi 配对在线。


# 2026-09-05 Server 端关闭 Server GC（内存优化）

用户反馈接收端内存占用过大，实测（20 逻辑核机器）旧进程
Private=515.6MB / 工作集=121MB / 60 线程：典型的 ASP.NET Core
Server GC 每核建堆特征，实际存活对象远小于提交量。决定先做配置级
修复，不重写 Rust/Go。

- PhoneDeck.Server.csproj 显式设置 `<ServerGarbageCollection>false</ServerGarbageCollection>`
  （SDK.Web 默认 true）。无其他代码改动。
- 验证：dotnet build 零警告；PhoneDeck.Server.Tests 48/48 通过；
  单文件发布产物内嵌 runtimeconfig 确认 `"System.GC.Server": false`
  （旧 exe 为 true）。
- 部署：旧 exe 备份至运行目录 rollback/20260905-before-gcworkstation/；
  替换部署根 PhoneDeck.Server.exe；数据目录仍走默认
  %LOCALAPPDATA%\PhoneDeck（computerId/LAN 证书不变），健康检查 200，
  computerId 一致，VB-CABLE 与 USB 看门狗正常。
- 实测（启动后约 90 秒）：Private 515.6MB → 26.6MB，工作集
  121MB → 82MB，线程 60 → 23。回归方式：rollback 目录换回旧 exe 即可。

风险与未验证项：

- 未在真实语音会话（Typeless + VB-CABLE 长时间转写）下复测内存峰值；
  工作站 GC 理论上高负载时 GC 暂停略多于 Server GC，本场景并发极低，
  预期无感。
- 运行目录布局备注：control-center-publish/ 内无 PhoneDeck.Server.exe，
  控制台从该目录启动接收端会失败，且若复制 exe 过去会以
  PHONEDECK_DATA_DIR=该目录 data（空）启动，导致重新生成身份、破坏
  手机配对——维持"接收端从部署根目录直接启动"的现状。


# 2026-09-05 ControlCenter 托盘裁剪工作集（内存优化续）

接上节。Server 已降至 Private 26MB 后，剩余大头是 ControlCenter
（WPF，托盘常驻时 WS ~270MB / Private ~180MB，WPF+WinForms 框架基线，
代码无泄漏：日志已有 180 条上限，刷新每 2.5s 一次且分配小）。

- MainWindow.xaml.cs 新增 EmptyWorkingSet P/Invoke；点关闭隐藏到托盘时
  （MainWindow_Closing）裁剪工作集并把刷新间隔从 2.5s 放慢到 10s；
  RestoreFromTray 恢复 2.5s。RefreshStatusAsync 仅更新界面 UI，隐藏期
  放慢无副作用；热键/托盘菜单不经刷新路径。
- 构建：Release 零警告零错误；发布产物与 control-center-publish 布局
  一致（仅 exe 变化）。旧 exe 备份 rollback/20260905-cc-tray-trim/。
- 实测（新进程，可见→托盘）：WS 307.5MB → 35.6MB，托盘停留 26 秒后
  48.8MB；Private 基本不变（~190MB，属框架已提交内存，非物理占用）。
  双击托盘恢复 2.5s 刷新的路径仅代码走查，未做界面实测。

风险与未验证项：

- EmptyWorkingSet 失败时静默跳过，不影响功能；恢复窗口瞬间会有软缺页
  （页面换回），首次点亮略慢属预期。
- Typeless 本体 10 进程合计约 1.1GB，为第三方应用，PhoneDeck 无法优化；
  这是语音链路里最大的内存项。


# 2026-09-06 dev.10 追记：主题收拢为品牌族×深浅（1号电脑）

用户反馈：只要 9 月 2 日做的 ChatGPT / Claude / Grok 风格加冰川玻璃，
其余（纸卡 4 套、历史配色 9 套、Gemini/Hermes/豆包）全部删除。

- 97cc210（multi-pc 分支，2026-09-02，未合入 main）是品牌族×深浅两级
  架构；本次以它为骨架合入 main：BRANDS={glass, gpt, claude, grok}，
  存储 `theme_brand`+`theme_mode`，旧 `theme_id` 一次性迁移
  （soft→玻璃+auto，深色系→玻璃+dark，gpt/claude/grok 实例→对应族）。
- 冰川玻璃保持 main 的 frost id 与渲染（MainActivity isFrost() 挂极光背景
  的路径零改动）；GPT/Claude/Grok 调色板、Grok 灰阶单色皮肤、Ripple
  pressable、防闪白 applyWindow 均从 97cc210 原样移植；shortcutColor/
  shortcutPressedColor 保留 main 版计算（去掉纸卡分支）。
- 设置页：深浅三选 chips + 4 张品牌卡（themeHalf 上浅下深预览），
  selectBrand/selectAppearanceMode 即存即 recreate。
- 唯一适配：MainActivity pressableRoundRect 返回类型 StateListDrawable
  → Drawable（pressable 现返回 RippleDrawable）。
- 验证：1号机无 Android SDK/gradle 环境，采用 PyCharm JBR javac 17 +
  robolectric android-all 35 framework jar 对 app 全部 26 个源文件
  编译通过（曾抓出上述 pressableRoundRect 类型错误）。
  assembleDebug/lintDebug 与真机验收待 2 号电脑补做。
  versionCode 16 / 1.6.0-dev.10。
