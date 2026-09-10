param(
    [Parameter(Mandatory=$true)][string]$InstallDirectory,
    [string]$DataDirectory
)
# One-time LOCAL enrollment for receivers that predate the update protocol.
# Extract the bootstrap ZIP to a separate directory, then run this script there.
$ErrorActionPreference = 'Stop'
$taskInstall = (Resolve-Path -LiteralPath $InstallDirectory).Path
if (!$DataDirectory) { $DataDirectory = Join-Path $taskInstall 'data' }
$taskData = (Resolve-Path -LiteralPath $DataDirectory).Path
$taskIdentityPath = Join-Path $taskData 'computer-id.txt'
if (!(Test-Path -LiteralPath $taskIdentityPath)) { throw 'Cannot find existing computer-id.txt. Supply the original -DataDirectory; do not create a new identity.' }
$taskIdentity = [IO.File]::ReadAllText($taskIdentityPath).Trim()
$taskNames = @('PhoneDeck.Server.exe', 'PhoneDeck.ControlCenter.exe')
foreach ($taskName in $taskNames) {
    if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot $taskName))) { throw "Missing bootstrap file: $taskName" }
}
if ($taskInstall.Equals($PSScriptRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Extract the bootstrap package to a separate folder first.' }
try { $taskHealth = Invoke-RestMethod 'http://127.0.0.1:8765/api/health' -TimeoutSec 3 } catch { $taskHealth = $null }
if ($taskHealth -and ($taskHealth.audio.streaming -or $taskHealth.dictation.active -or $taskHealth.voiceEngine.capturing)) {
    throw 'Microphone is busy. Finish dictation and turn off the shared microphone before first enrollment.'
}
if ($taskHealth -and $taskHealth.computerId -ne $taskIdentity) { throw 'The running receiver uses a different data directory.' }
$taskBackup = Join-Path $taskData ('updates/bootstrap-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $taskBackup -Force | Out-Null
$taskChanged = @()
$taskConsoleWasRunning = $false
function Start-PhoneDeckLocal([string]$name) {
    $env:PHONEDECK_DATA_DIR = $taskData
    Start-Process -FilePath (Join-Path $taskInstall $name) -WorkingDirectory $taskInstall -WindowStyle Hidden
}
try {
    foreach ($taskName in $taskNames) {
        $taskTarget = Join-Path $taskInstall $taskName
        if (Test-Path -LiteralPath $taskTarget) { Copy-Item -LiteralPath $taskTarget -Destination (Join-Path $taskBackup $taskName) }
        foreach ($taskProcess in @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($taskName)) -ErrorAction SilentlyContinue)) {
            if ($taskProcess.Path -eq $taskTarget) {
                if ($taskName -eq 'PhoneDeck.ControlCenter.exe') { $taskConsoleWasRunning = $true }
                Stop-Process -Id $taskProcess.Id -Force
            }
        }
    }
    foreach ($taskName in $taskNames) {
        $taskTarget = Join-Path $taskInstall $taskName
        for ($taskAttempt=0; ; $taskAttempt++) {
            try { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskName) -Destination $taskTarget -Force; break }
            catch { if ($taskAttempt -ge 20) { throw }; Start-Sleep -Milliseconds 250 }
        }
        $taskChanged += $taskName
    }
    Start-PhoneDeckLocal 'PhoneDeck.Server.exe'
    $taskGood = $false
    for ($taskAttempt=0; $taskAttempt -lt 30; $taskAttempt++) {
        Start-Sleep -Milliseconds 500
        try {
            $taskHealth = Invoke-RestMethod 'http://127.0.0.1:8765/api/health' -TimeoutSec 2
            if ($taskHealth.computerId -eq $taskIdentity -and $taskHealth.updates.supported) { $taskGood = $true; break }
        } catch { }
    }
    if (!$taskGood) { throw 'New receiver failed identity/update capability validation.' }
    if ($taskConsoleWasRunning) { Start-PhoneDeckLocal 'PhoneDeck.ControlCenter.exe' }
    Write-Output 'Update support installed. Future updates can be started from any enrolled PC and coordinated by the paired Android phone.'
} catch {
    $taskFailure = $_
    foreach ($taskProcess in @(Get-Process -Name PhoneDeck.Server -ErrorAction SilentlyContinue)) {
        if ($taskProcess.Path -eq (Join-Path $taskInstall 'PhoneDeck.Server.exe')) { Stop-Process -Id $taskProcess.Id -Force }
    }
    Start-Sleep -Milliseconds 1000
    foreach ($taskName in $taskChanged) {
        $taskOld = Join-Path $taskBackup $taskName
        if (Test-Path -LiteralPath $taskOld) { Copy-Item -LiteralPath $taskOld -Destination (Join-Path $taskInstall $taskName) -Force }
    }
    Start-PhoneDeckLocal 'PhoneDeck.Server.exe'
    if ($taskConsoleWasRunning) { Start-PhoneDeckLocal 'PhoneDeck.ControlCenter.exe' }
    throw $taskFailure
}
