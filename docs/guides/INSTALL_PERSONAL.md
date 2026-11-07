# 个人安装与验收清单（2026-11-06，提交 625a7bc）

作者自用：1 部 Samsung 手机 + Mac + 两台 Windows，同一个 Wi-Fi，只用共享麦克风 + Typeless。
这份清单对应 PR #59 的代码；所有结果在你实测前都只是“代码通过构建”。

## 0. 已备好的东西（都在被 Git 忽略的目录里）

| 内容 | 位置 | SHA-256 |
|---|---|---|
| Mac 接收端（Apple Silicon，原生精简版） | `work/phone-deck/dist/macos/osx-arm64/PhoneDeck Receiver.app` | 主程序 `e7171f83…c7bb`（完整值见下方命令） |
| Windows 接收端 + 托盘（x64） | `outputs/personal-slim-20261106/PhoneDeck-Windows.zip` | zip `60ba70e4…9947`，另有 `SHA256SUMS.txt` |
| 手机 APK | **这台 Mac 没有你手机的签名，不能在这里打** | 见第 3 节 |

版本号和你现在装的一样（Windows 1.6.0-dev.17、Mac 2.0.0-dev.4、手机 1.6.0-dev.22/28），**不能靠版本号分辨新旧**。
分辨新版的办法：

- Windows exe 属性 →“详细信息”→ 产品版本末尾是 `+625a7bc6…`。
- 任一接收端的 `http://127.0.0.1:8765/api/health` 的 `capabilities` 里有 `sharedPcm16kV1`，且**没有** `managedDictation`。
- Mac 主程序 SHA-256 以 `e7171f83` 开头（旧版是 `8bc17818`）。

安装顺序没有要求：新手机配旧接收端会照旧发 48 kHz；新接收端配旧手机也能用共享模式。
但**只有两端都升级后才会走 16 kHz**，所以建议一次全换。

## 1. Mac（这台）

先备份再替换，旧包压成 zip（不要留成同 Bundle ID 的 .app 目录，辅助功能列表会认错）：

```bash
cd ~/Library/Application\ Support/PhoneDeck/backups && ditto -c -k --keepParent ~/Applications/"PhoneDeck Receiver.app" receiver-app-8bc17818-20261106.zip
```

停服务、替换、重启：

```bash
launchctl bootout gui/$UID/com.phonedeck.receiver 2>/dev/null; rm -rf ~/Applications/"PhoneDeck Receiver.app" && ditto "/Volumes/ExternalSSD/00_资料库/05_开发项目/PhoneDeck/work/phone-deck/dist/macos/osx-arm64/PhoneDeck Receiver.app" ~/Applications/"PhoneDeck Receiver.app" && launchctl bootstrap gui/$UID ~/Library/LaunchAgents/com.phonedeck.receiver.plist 2>&1; sleep 2; launchctl kickstart -k gui/$UID/com.phonedeck.receiver
```

- 第一次启动可能报 `OS_REASON_CODESIGNING`，再执行一次 `kickstart -k` 即可。
- **换了 ad-hoc 签名，辅助功能会失效**：系统设置 → 隐私与安全性 → 辅助功能，用「−」删掉所有 PhoneDeck Receiver，再「+」→ ⌘⇧G 输入 `~/Applications` 选这个 App，然后再 `kickstart -k` 一次。
  不要反复重启，没授权时每次启动都会弹授权框。
- 验证：

```bash
shasum -a 256 ~/Applications/"PhoneDeck Receiver.app"/Contents/MacOS/PhoneDeck.Receiver
curl -s 127.0.0.1:8765/api/health | python3 -c 'import json,sys; d=json.load(sys.stdin); print(d["capabilities"]); print("accessibilityTrusted =", d["input"]["accessibilityTrusted"])'
```

- 回滚：`launchctl bootout …`，删掉新 App，`ditto -x -k` 解开备份的 zip 到 `~/Applications`，再按上面重启并重新授权辅助功能。

## 2. 两台 Windows（每台都做一遍）

1. 把 `PhoneDeck-Windows.zip` 拷到电脑上解压（Mac 上做的包没有经过 Windows 打包脚本，下面第 4 步会核对）。
2. 托盘图标 → 退出，确认任务管理器里没有 `PhoneDeck.Server.exe` / `PhoneDeck.ControlCenter.exe`。
3. **只替换两个 exe**。配对身份、证书、设置都在 `%LOCALAPPDATA%\PhoneDeck\`，不要动它；
   旧文档说的“数据在 exe 旁边的 data 目录”已过时。
   - 先把旧的两个 exe 复制一份改名备份（如 `*.old`）。
   - 新的覆盖到原来的位置（开机启动指向的就是原路径，保持不变）。
   - 防火墙脚本 `Enable-PhoneDeckLan.ps1` 之前跑过就不用再跑。
4. 双击 `PhoneDeck.ControlCenter.exe`，托盘出现图标。核对：
   - exe 属性 → 详细信息 → 产品版本末尾 `+625a7bc6…`。
   - PowerShell：`(Invoke-RestMethod http://127.0.0.1:8765/api/health).capabilities`，应含 `sharedPcm16kV1`、不含 `managedDictation`。
   - 日志在 `%LOCALAPPDATA%\PhoneDeck\logs\receiver.log`。
5. 如果 `voice-engine-settings.json` 里 `activeEngine` 不是 `typeless`，日志会提示回退到 Typeless，不用处理。
6. 回滚：退出托盘，把 `*.old` 改回原名。

## 3. 手机（在持有你手机签名的那台电脑上做）

这台 Mac 打出的 APK 证书是 `ba44191d…`，手机上现在装的是 `df327953…`，**不能覆盖安装**；
不要为此卸载（会丢掉全部配对）。

在那台电脑上：

```powershell
git pull
cd work\phone-deck\android
.\gradlew.bat :app:assembleDebug :app:testDebugUnitTest
```

装之前先比对证书（两条输出的 SHA-256 必须一样，不一样就停，不要装）：

```powershell
adb shell pm path com.codex.phonedeck            # 取出路径后：
adb pull <上面的路径> installed.apk
apksigner verify --print-certs installed.apk
apksigner verify --print-certs app\build\outputs\apk\debug\app-debug.apk
```

一致后覆盖安装（保留数据，不卸载）：

```powershell
adb install -r app\build\outputs\apk\debug\app-debug.apk
```

装完核对：设置页多了“打开言渡时屏幕常亮”开关；首页只有一个“开启共享麦克风”按钮；
`adb shell dumpsys package com.codex.phonedeck | findstr lastUpdateTime` 时间是刚才。
配对记录应都在；若有电脑显示“需要重新配对”，长按电脑卡片选“重新配对”。
回滚：保留旧 APK（`installed.apk`），`adb install -r -d installed.apk` 同签名可降回。

## 4. 实测方案（3 台电脑同一 Wi-Fi，约 40 分钟）

1. 三台电脑都加入共享组，开启共享麦克风；手机先亮屏 10 分钟正常使用，再**熄屏 10 分钟**（验证熄屏 Wi-Fi 锁），再亮屏。
2. 过程中每台电脑都按 Typeless 快捷键口述几句：包括小声的句子和句尾。
3. 结束后收数据：
   - Mac：`grep '结束 mode=shared' ~/Library/Logs/PhoneDeck/receiver.log | tail`（看“断续=次数/毫秒”“断流”）。
   - Windows：`%LOCALAPPDATA%\PhoneDeck\logs\receiver.log` 同样的行。
   - 手机：`adb logcat -d -s PhoneDeckNet`（开始供音、看门狗卡住、连接中断、等待重连，附 Wi-Fi 信号）。
4. 合格标准：
   - Wi-Fi 断续 ≤ 10 次/分钟（改坏前 2–10，改坏后 23–135）。
   - “5000ms 未收到手机音频”次数明显少于 11-04/05。
   - 手机上的默认电脑全程不自己变。
   - 熄屏阶段断续不比亮屏差。
   - Typeless 听写准确率与改动前没有明显差别（16 kHz）。
5. CPU：对比接收端占用，原记录 Mac 共享约 1.2–3.6%、Windows 约 4.5%。
   Mac：`top -pid $(pgrep -f PhoneDeck.Receiver) -l 5 -stats cpu,mem`；Windows：任务管理器看 `PhoneDeck.Server.exe`。
6. 16 kHz 若让识别变差：目前没有单独的关闭开关，告诉我，会加到每台电脑的设置里。

## 5. 已知没做

- Windows 原生编译（AOT）：只能在 Windows 上做，见 HANDOFF 2026-11-05 第一条。
- Windows 自动化测试：PR #59 的 CI（Windows 接收端与测试、Android 构建与检查、macOS 接收端与测试）在提交 625a7bc 上全部通过。
  这只说明代码通过构建与测试，不代表在你的电脑上验证过。
