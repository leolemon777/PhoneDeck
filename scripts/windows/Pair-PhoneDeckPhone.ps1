$ErrorActionPreference = 'Stop'

$bundledAdb = Join-Path $PSScriptRoot 'platform-tools\adb.exe'
if (Test-Path -LiteralPath $bundledAdb) {
    $adb = $bundledAdb
} else {
    $adbCommand = Get-Command adb.exe -ErrorAction SilentlyContinue
    if ($null -eq $adbCommand) {
        throw '未找到 adb.exe。请使用完整 PhoneDeck 部署包，或先安装 Android Platform Tools。'
    }
    $adb = $adbCommand.Source
}

& $adb start-server | Out-Null
$deviceLines = @(& $adb devices | Select-Object -Skip 1 |
    Where-Object { $_ -match "\tdevice$" })

if ($deviceLines.Count -eq 0) {
    throw '没有找到已授权的 Android 手机。请解锁手机、开启 USB 调试并接受授权提示后重试。'
}
if ($deviceLines.Count -gt 1) {
    throw '检测到多台 Android 设备，请只保留需要配对的这台手机。'
}

& $adb reverse tcp:8765 tcp:8765
if ($LASTEXITCODE -ne 0) {
    throw '建立 PhoneDeck USB 配对通道失败。'
}

& $adb shell am start -n com.codex.phonedeck/.MainActivity | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw '无法启动 PhoneDeck 手机端，请先确认已安装手机 App。'
}

Write-Host ''
Write-Host 'PhoneDeck USB 配对通道已建立。'
Write-Host '请等待手机显示“Wi-Fi 在线”，然后即可拔掉 USB。'
Write-Host '日后电脑 IP 变化导致无法连接时，重新运行本脚本即可刷新地址。'
