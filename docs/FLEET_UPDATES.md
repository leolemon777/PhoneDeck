# 设备统一更新

本次实现覆盖已有 Windows x64 接收端、Windows 控制台和 Android 手机。保留麦克风尾音修复。macOS 预览端和未实现的 iOS 客户端不支持此安装协议，不能宣称四端已支持统一安装。

## 使用方式

1. 首次给旧电脑装入更新能力，手机安装包含「设置 → 设备更新」的新版本。旧版没有下载/安装接口，无法凭空远程升级。
2. 手机打开「设置 → 设备更新 → 从手机导入签名更新包」，选择发布者签名 ZIP。也可使用旧电脑已缓存的包。
3. 手机点击「检查并更新所有设备」。旧版电脑的本地更新页仍兼容，但新版桌面托盘不再提供设置入口。
4. 手机缓存并校验更新包，等待听写结束，暂停共享麦克风，再向已配对电脑依次发送；保存更新包的电脑最后重启。电脑端更新接收端和控制台，手机最后弹出系统安装界面。
5. 保持更新页打开。逐台查看成功、旧版需首次安装、离线、回退或失败。离线设备留在手机补更列表，恢复连接并打开手机主页后自动重试；安装失败需查看原因并手动重试。后台或锁屏期间不保证分发。停止后续更新会取消自动补更；已进入安装阶段的电脑继续自行完成或回退。
6. 完成后在主页重新开启共享麦克风。安卓首次需允许本应用安装更新；系统确认/用户取消不能显示为安装成功。再次检查以实际安装版本为准。

不必同时给所有设备更新：本轮失败不阻止其他可达设备。手机缓存可能被系统清理，届时从电脑重新取得签名包。首次版本、失败重试和系统权限不是静默更新。

## 旧电脑首次接入

将首次接入 ZIP 解压到单独目录，保留原控制台目录及其 data。结束听写并关闭共享麦克风后，双击 `Install-UpdateSupport.cmd`。
它在桌面及其下一层目录查找唯一的现有控制台；找不到或存在多个时，需要输入原控制台文件夹路径。也可在本机显式运行：

```powershell
.\Install-UpdateSupport.ps1 -InstallDirectory '原来的 PhoneDeck电脑控制台目录'
# 使用其他数据目录时，额外传 -DataDirectory '原数据目录'
```

脚本仅在本机运行，先备份两个 EXE，保留 computer-id、配对密钥、证书、引擎和个性化配置。无需重新配对或卸载手机。若原安装签名与 APK 不同，不能覆盖安装，应使用同一签名渠道的包，不应通过卸载清数据规避。

## 协议与信任边界

- `health.updates` 和 `fleetUpdatesV1` 公布能力、当前发布序号与批量请求 ID。旧客户端仍可使用原协议。
- `GET /api/updates`：已导入清单、本机状态、版本、忙碌状态；`GET /api/updates/bundle`：下载已验证包。
- `POST /api/updates/bundle`：上传 ZIP；`POST /api/updates/request`：请求手机协调；`POST /api/updates/apply`：安装此电脑。
- 所有更新写入需 `X-PhoneDeck-Update: 1` 和 `X-PhoneDeck-Target: computerId`。LAN 额外保留原有 token、HTTPS 和证书固定。localhost 更新写入验证 Host，使用自定义请求头阻止跨站简单请求；不开放 CORS。
- 清单 schema=1，包含 sequence、windowsVersion、androidVersionCode、三个文件的名称、字节数与 SHA-256。ZIP **只能**包含 `manifest.json`、`manifest.sig`、`PhoneDeck.Server.exe`、`PhoneDeck.ControlCenter.exe`、`PhoneDeck.apk`。禁止额外路径、重复条目或脚本。
- `manifest.sig` 是原始 UTF-8 清单字节的 RSA/SHA-256 PKCS#1 签名。内置发布者公钥与 LAN 配对身份相互独立：已配对电脑也不能提供未经签名的程序。
- Windows 与 Android 独立验证签名、大小、每个文件哈希。上传上限 384 MiB；每文件 192 MiB；解压总量 512 MiB。不允许自动降级。Android 还核对自身包名、版本号和已安装 APK 的签名。
- 接收端的使用请求与安装切换共用准入锁，避免检查为空闲后又开始新音频。活跃请求、音频、受控听写或探测到引擎采集中返回 waiting-idle，稍后重试。
- 只调用已安装接收端复制出的固定更新工作进程，安装位置来自本机配置，不接受网络提供的路径、URL 或命令。更新器等待原进程退出、备份固定文件、替换并重新启动。只有版本、发布序号、computerId 检查都通过才记为完成；异常则恢复原文件。
- 旧文件保存在原数据目录的 `updates/backup`。断电/进程强杀等未能执行异常处理的情况不承诺自动回退，应使用首次接入包恢复。控制台的更新保护标记五分钟后过期，以便本地修复。

## 发布者打包

本机生成的开发发布私钥保存在 Git 忽略的 signing 目录，没有提交或传给其他设备。正式开源发布需将该信任根纳入受控发布流程，安全备份私钥；fork 项目自行生成密钥并同步替换 Windows 和 Android 公钥。没有私钥时不能签出原渠道认可的更新。Android 的 APK 签名与更新清单签名是两套独立密钥。

```powershell
# PowerShell 7；先发布 self-contained/single-file 的两个 Windows EXE。
# 控制台须 -p:IncludeNativeLibrariesForSelfExtract=true。
.\work\phone-deck\build-update-package.ps1 `
  -Server '发布目录\PhoneDeck.Server.exe' `
  -ControlCenter '发布目录\PhoneDeck.ControlCenter.exe' `
  -Apk '同签名渠道的 PhoneDeck.apk' `
  -Sequence 23 -WindowsVersion '1.6.0-dev.11' -AndroidVersionCode 23 `
  -SigningKey '安全保存的 PKCS8 DER 私钥路径' -Output '新发布包.zip'
```

发布序号必须与接收端内置序号一致；版本号须与两个平台实际构建一致。本次部署使用与现有手机相同的开发 APK 签名，不能当作已完成正式商店签名发布。未自动创建 GitHub Release。
