# Windows 发行辅助脚本

`AutoReconnectUsb.ps1` 是 1.4.0 运行包沿用到 1.5.0 候选版的 USB/ADB 自动恢复模板。

打包时将其复制到 `PhoneDeck.Server.exe` 同一目录，并确保该目录下存在 `tools/adb.exe`。启动服务器后再以隐藏 PowerShell 进程启动脚本：

```powershell
Start-Process powershell.exe -WindowStyle Hidden -ArgumentList @(
  '-NoProfile',
  '-WindowStyle', 'Hidden',
  '-ExecutionPolicy', 'Bypass',
  '-File', '.\AutoReconnectUsb.ps1'
)
```

此脚本只恢复 ADB reverse 和重新唤醒 Activity。1.5.0 候选源码已经加入显式音频会话、
幂等 Typeless 开始/停止和断流清理，但脚本本身不负责这些状态，也不能替代
`docs/HANDOFF.md` 要求的真实手机连续断线验收。
