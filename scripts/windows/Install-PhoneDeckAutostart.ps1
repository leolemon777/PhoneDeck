param(
    [string]$TaskName = 'PhoneDeck Receiver Auto Start'
)

$ErrorActionPreference = 'Stop'
$repairScript = Join-Path $PSScriptRoot 'StartOrRepairPhoneDeck.ps1'
$serverPath = Join-Path $PSScriptRoot 'PhoneDeck.Server.exe'

if (-not (Test-Path -LiteralPath $repairScript) -or
    -not (Test-Path -LiteralPath $serverPath)) {
    throw '请从完整的 PhoneDeck 电脑端文件夹运行本脚本。'
}

$arguments = '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File ' +
    '"' + $repairScript + '" -Quiet'
$action = New-ScheduledTaskAction `
    -Execute 'powershell.exe' `
    -Argument $arguments `
    -WorkingDirectory $PSScriptRoot
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 2)

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Description '登录 Windows 后自动启动 PhoneDeck Wi-Fi/USB 接收端。' `
    -Force | Out-Null

Start-ScheduledTask -TaskName $TaskName
Write-Host "PhoneDeck 已设置为登录后自动启动：$TaskName"
