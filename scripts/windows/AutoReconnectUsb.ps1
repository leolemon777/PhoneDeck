$ErrorActionPreference = 'SilentlyContinue'

$createdNew = $false
$mutex = [System.Threading.Mutex]::new(
    $true,
    'Local\PhoneDeckUsbReconnect',
    [ref]$createdNew)

if (-not $createdNew) {
    exit 0
}

try {
    $basePath = Split-Path -Parent $MyInvocation.MyCommand.Path
    $adbPath = Join-Path $basePath 'tools\adb.exe'

    while ($true) {
        if (-not (Get-Process -Name 'PhoneDeck.Server' -ErrorAction SilentlyContinue)) {
            break
        }

        if (Test-Path -LiteralPath $adbPath) {
            $deviceState = (& $adbPath get-state 2>$null)
            if ($deviceState -eq 'device') {
                $reverseList = (& $adbPath reverse --list 2>$null) -join "`n"
                if ($reverseList -notmatch 'tcp:8765\s+tcp:8765') {
                    & $adbPath reverse tcp:8765 tcp:8765 *> $null
                    if ($LASTEXITCODE -eq 0) {
                        & $adbPath shell am start `
                            -n com.codex.phonedeck/.MainActivity *> $null
                    }
                }
            }
        }

        Start-Sleep -Seconds 2
    }
}
finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
