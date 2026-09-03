param(
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$serverPath = Join-Path $PSScriptRoot 'PhoneDeck.Server.exe'
$adbPath = Join-Path $PSScriptRoot 'platform-tools\adb.exe'

function Show-PhoneDeckResult([string]$message, [bool]$success) {
    if ($Quiet) {
        return
    }
    $shell = New-Object -ComObject WScript.Shell
    $icon = if ($success) { 64 } else { 16 }
    [void]$shell.Popup($message, 8, 'PhoneDeck 一键恢复', $icon)
}

try {
    if (-not (Test-Path -LiteralPath $serverPath)) {
        throw "找不到电脑端程序：$serverPath"
    }

    $serverResolved = (Resolve-Path -LiteralPath $serverPath).Path
    $servers = @(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -eq 'PhoneDeck.Server.exe'
    })
    $correctServer = $servers | Where-Object {
        $_.ExecutablePath -and
        $_.ExecutablePath.Equals($serverResolved, [System.StringComparison]::OrdinalIgnoreCase)
    } | Select-Object -First 1

    if (-not $correctServer) {
        # 旧版本会占用相同端口；仅停止 PhoneDeck.Server，不触碰其他程序。
        $servers | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
        Start-Sleep -Milliseconds 500
        Start-Process -FilePath $serverResolved `
            -WorkingDirectory $PSScriptRoot -WindowStyle Hidden | Out-Null
    }

    $health = $null
    for ($attempt = 0; $attempt -lt 20 -and $null -eq $health; $attempt++) {
        try {
            $health = Invoke-RestMethod `
                -Uri 'http://127.0.0.1:8765/api/health' -TimeoutSec 2
        } catch {
            Start-Sleep -Milliseconds 250
        }
    }
    if ($null -eq $health -or -not $health.ok) {
        throw '电脑端未能正常启动，请检查防火墙或端口占用。'
    }

    $usbStatus = 'USB 未连接（不影响 Wi-Fi 使用）'
    if (Test-Path -LiteralPath $adbPath) {
        & $adbPath start-server *> $null
        $deviceLines = @(& $adbPath devices)
        $serial = $null
        foreach ($line in $deviceLines) {
            if ($line -match '^([^\s]+)\s+device$') {
                $serial = $Matches[1]
                break
            }
        }
        if ($serial) {
            & $adbPath -s $serial reverse tcp:8765 tcp:8765 *> $null
            $usbStatus = "USB 已连接：$serial"
        }
    }

    Show-PhoneDeckResult `
        "PhoneDeck 已就绪。`n电脑：$($health.displayName)`n版本：$($health.version)`nWi-Fi 自动发现已启动。`n$usbStatus" `
        $true
    exit 0
} catch {
    Show-PhoneDeckResult $_.Exception.Message $false
    Write-Error $_
    exit 1
}
