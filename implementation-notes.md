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
