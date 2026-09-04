# PhoneDeck 第二台 Windows 电脑部署

## 前提

- Windows 10/11 x64；
- 电脑与手机连接同一 Wi-Fi；
- 手机已安装 PhoneDeck 1.6.0-dev.2；
- 语音输入需安装 VB-CABLE 和 Typeless，快捷键不需要这两项。
- 一发三收需部署 1.6.0-dev.5 或更高接收端；`/api/health.capabilities` 应包含
  `sharedMicrophone`，`audio.available` 应为 true。

## 首次部署

1. 把整个 `PhoneDeck-Windows-WiFi-1.6.0-dev.2` 文件夹复制到新电脑，
   不要只复制 EXE。
2. 右键 `Enable-PhoneDeckLan.ps1`，选择使用 PowerShell 以管理员身份运行。
3. 双击 `PhoneDeck.Server.exe`，保持接收端运行。
4. 如果需要语音，安装 VB-CABLE，并在 Typeless 中选择
   `CABLE Output (VB-Audio Virtual Cable)` 作为麦克风。
5. 手机开启开发者选项和 USB 调试，用 USB 连接这台新电脑。
6. 解锁手机并接受“允许 USB 调试”，然后右键
   `Pair-PhoneDeckPhone.ps1`，选择“使用 PowerShell 运行”。
7. 手机顶部出现“2号 · <电脑名>”且显示“Wi-Fi 在线”后，可拔掉 USB。

## 日常使用

1. 手机和多台电脑保持在同一 Wi-Fi。
2. 每台电脑启动各自的 `PhoneDeck.Server.exe`。
3. 手机打开 PhoneDeck，点击“1号、2号、3号”切换目标。
4. 语音、快捷键只会发送给当前选中且在线的电脑。

首次 USB 仅用于安全交换该电脑的证书指纹、访问密钥和 Wi-Fi 地址。
配对完成后的日常音频与快捷键传输使用局域网 HTTPS，不消耗手机流量。

## 常见问题

- PowerShell 禁止脚本：在该文件夹打开 PowerShell，运行
  `powershell -ExecutionPolicy Bypass -File .\Enable-PhoneDeckLan.ps1`。
- 手机不出现授权窗口：更换可传数据的 USB 线，并确认已开启 USB 调试。
- 只能用快捷键、不能语音：检查 VB-CABLE 和 Typeless 的麦克风选择。
- 共享模式显示“需要更新电脑端”：确认运行的不是旧接收端，并检查健康接口中的
  `sharedMicrophone` 能力。
- 共享已连接但本机不出字：这是正常的供音/触发分离；在该电脑按 Typeless 本机快捷键。
- 电脑换网络后离线：再插一次 USB，重新运行 `Pair-PhoneDeckPhone.ps1`。
- 公司/酒店 Wi-Fi 无法连接：该网络可能开启了客户端隔离，需使用允许设备互访的路由器。
