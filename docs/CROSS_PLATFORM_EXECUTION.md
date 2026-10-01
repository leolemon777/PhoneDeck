# 跨平台开源候选执行记录

2026-09-30：用户授权执行 Windows/macOS/Linux、多电脑、统一语音入口、最终文字同步及低配置安装。沿用浅深两主题。

## 实施与默认行为

- 独立工作树 `PhoneDeck-cross-platform`，基于 `ab60e1a`；原工作树的状态窗口修改不覆盖、不代提交。
- 新增跨平台 Desktop 接收端，复用当前 v2、逐手机凭据和扫码确认；旧 Windows/Mac 接收端继续保留。
- 用户已确定默认内置开源 Whisper：首次下载模型，免输入法和虚拟声卡。模型固定版本/大小/哈希；音频仅内存。另可构建带模型的离线包。既有 Typeless 路径保留。
- 点击与按住共用同一会话；共享供音时每台电脑可用本机入口开始/提交。只有用户在手机主动开始后才采音。
- 最终文字由触发电脑生成一次，经配对手机转发给用户选择的共享组。来源电脑仅在用户开启该段且输入焦点不变时填入；其他电脑只进临时历史。任何听写均不自动回车，不保存转写文件。
- 首次使用目标：下载、运行、手机扫码、电脑确认、按提示下载一次模型即可测试语音。系统权限和防火墙不能绕过，界面给出诊断。

## 工作清单（持续更新）

- [x] Desktop 身份、TLS、逐手机配对、发现、安全本机管理（网络发现实网仍待验收）
- [x] 固定开源识别依赖、校验模型、内存 PCM、取消/停止/断流
- [x] 三平台输入后端及桌面触发入口代码；Windows 原生快捷键已验证，Mac/Linux 桌面输入仍待实测
- [x] Android 转写结果中继、显式共享范围与临时结果界面
- [x] 首次运行控制页、模型下载、依赖与权限提示、浅深主题
- [x] 三平台自包含打包、来源/校验和/依赖声明、CI；Android 独立固定签名渠道
- [x] 既有 Windows/Mac/Android 回归、新会话与同步隔离测试
- [ ] 真 Windows/Mac/Linux、多机和干净安装验收
- [ ] 审核候选后发布（未发布）

## 验收边界

用户随后澄清：当前没有可接入的 Mac，回到 Mac 后自行操作。已提供 SSH 手册用于 Linux；本机 `ssh -G linux2` 仍是默认主机/用户，没有对应别名和密钥，未建立 SSH 连接。这是本机接入条件缺失，不判定远端离线，也没有修改网络、代理或全局 SSH 配置。

### 已完成的验证

| 范围 | 证据 | 实际证明的边界 |
|---|---|---|
| 新 Desktop | 20/20；真实 Kestrel HTTPS、凭据/权限范围、目标、撤销、同源限制、结果隔离、去重、30分钟期限、会话中断 | 协议和状态机；自动化中不向用户输入框注入 |
| 旧接收端 | Windows 144/144、Mac 23/23；既有 PhoneDeck CI 全绿 | 旧路径回归；不替代 Mac 真机 |
| Android | Debug/uiPreview 构建、lint、27项单元测试；独立 desktopPreview 构建/lint及APK签名核验 | Java实现与可安装候选；测试渠道和分发渠道分别保管签名 |
| 四种桌面包 | [CI 36752538026](https://github.com/leolemon777/PhoneDeck/actions/runs/36752538026)：Windows x64、Ubuntu22.04 x64、Mac Intel/Apple Silicon 全部通过20项测试、原生构建、真实公开样本识别、自包含打包、归档逐文件哈希/执行权限；两个Mac均通过原生热键事件循环初始化/存活 | 对应OS运行组件真实可执行；不证明桌面权限、焦点输入或真实麦克风表现；Linux arm64仅有原生构建入口 |
| Windows + Samsung | SM-G9880 经 adb 反向TLS连接两个隔离Windows接收端；公开JFK样本；6份结果双向同步，GUID/来源/会话/文字相同；重复轮询不重复、不回传导入结果 | 真实手机中继、内置识别及Windows协议；两个接收端在同一台物理电脑，未调用麦克风 |
| Windows入口 | 真实浏览器点击、指针按住、键盘按住；原生Ctrl+Alt+Space与Ctrl+Alt+V；所有结果经手机同步 | Windows入口共用真实识别链路；验收使用history-only保护用户输入焦点 |
| 控制页 | Chromium浅色/深色、390px窄屏无横向溢出、二维码显隐、结果HTML按纯文字呈现、无JS错误 | 桌面控制页；不替代真实手机相机扫码 |

本地诊断发现并修复了 Windows 中文模型路径的 UTF-8 argv 崩溃、stdin 默认不输出识别文字、Mac 不支持 EphemeralKeySet、新版 Windows runner 编译器选择、Android SDK obsolete tools 和预览渠道回环地址加载丢失。x64包同时携带通用与加速组件；CPU及OS支持全部所需指令才自动使用加速组件，旧CPU回退通用组件。

自动化和跨平台构建不冒充真实桌面验收。剩余逐项操作见 [真机验收步骤](DESKTOP_ACCEPTANCE.md)。Windows干净安装/焦点自动填入、实网发现与相机扫码、真实中文麦克风、不同物理电脑、Mac权限/输入、Linux X11/Wayland仍是正式版门槛。候选保持alpha，不创建正式Release。

Windows焦点输入尝试因执行会话没有获得专用测试窗口前台焦点而停止，未启动该次听写，没有向用户输入框注入；不能把测试窗口构建或热键模拟算作焦点自动填入验收。独立Android分发APK已在Samsung安装并检查干净启动：显示扫码指引及内置识别，不自动接入旧USB，也不弹蓝牙授权。

Mac原生快捷键helper在OS主线程运行应用事件循环，CI检查初始化及循环存活，不模拟用户键盘；真实快捷键和辅助功能权限仍按上述真机步骤验收。发布包的外置build.json记录签名后字节与来源commit，checksums.sha256同时覆盖归档和外置清单。

### 已准备的候选文件

本地 `outputs/preview-delivery/PhoneDeck-2.0.0-alpha.1-safe-voice-20260930` 汇总四种桌面轻量包、Windows完整模型安装包/ZIP、固定签名Android APK、快速开始/真机清单/许可说明和SHA256。维护者[发布草稿](https://github.com/leolemon777/PhoneDeck/releases/tag/untagged-15cdabfbbb1d478a8a43)用于回Mac下载验收，尚未公开发布。

候选评审源码为 `e46ca69769274db5b02fc96ddd72e026a5db7ac4`；本地Windows完整包与APK从该源码生成。PR CI实际使用临时合并提交 `23dfa4ab078346a1be31b98bdd9e357635e2076c`，两者Git源码树经GitHub API核对同为 `27587fc129257372fa2ec1447b39b9aa95701023`。保留各自真实build-manifest来源，不把合并提交伪写成分支head。下载后已重新验证各归档及清单，汇总 `verification.json` 记录范围。既有[回归CI 36752538028](https://github.com/leolemon777/PhoneDeck/actions/runs/36752538028)也全绿。后续交接文档提交不会改动这批固定来源工件。
