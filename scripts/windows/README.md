# Windows 发行辅助脚本

`AutoReconnectUsb.ps1` 是当前 1.4.0 运行包使用的 USB/ADB 自动恢复模板。

打包时将其复制到 `PhoneDeck.Server.exe` 同一目录，并确保该目录下存在 `tools/adb.exe`。启动服务器后再以隐藏 PowerShell 进程启动脚本：

```powershell
Start-Process powershell.exe -WindowStyle Hidden -ArgumentList @(
  '-NoProfile',
  '-WindowStyle', 'Hidden',
  '-ExecutionPolicy', 'Bypass',
  '-File', '.\AutoReconnectUsb.ps1'
)
```

此脚本只恢复 ADB reverse 和重新唤醒 Activity。它不代表已经解决“语音过程中 USB 断开”的全部状态同步问题；该问题仍列在 `docs/HANDOFF.md` 的最高优先级。
