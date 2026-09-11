<#
.SYNOPSIS
    Test-CandidateRun.ps1
.DESCRIPTION
    Validates New-CandidateRun.ps1 actual behavior.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Server,
    [Parameter(Mandatory)][string]$ControlCenter,
    [Parameter(Mandatory)][string]$Apk,
    [Parameter(Mandatory)][string]$Aapt2Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7 -or -not $IsWindows) {
    throw "Test-CandidateRun.ps1 requires PowerShell 7+ on Windows."
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path -LiteralPath (Join-Path $ScriptDir '..\..\..')).Path
$CoreScript = Join-Path $RepoRoot 'scripts\release\New-CandidateRun.ps1'

function Get-FileSha256Lower {
    param([Parameter(Mandatory)][string]$LiteralPath)
    $stream = [System.IO.File]::OpenRead($LiteralPath)
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        $hashBytes = $sha256.ComputeHash($stream)
        return [System.BitConverter]::ToString($hashBytes).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $stream.Dispose()
    }
}

function Invoke-CandidateRun {
    param(
        [string]$ServerArg,
        [string]$ControlCenterArg,
        [string]$ApkArg,
        [string]$Aapt2PathArg,
        [string]$OutputRootArg
    )
    $argsList = @('-NoProfile', '-NonInteractive', '-File', $CoreScript, '-Server', $ServerArg, '-ControlCenter', $ControlCenterArg, '-Apk', $ApkArg)
    if ($Aapt2PathArg) { $argsList += @('-Aapt2Path', $Aapt2PathArg) }
    if ($OutputRootArg) { $argsList += @('-OutputRoot', $OutputRootArg) }

    $pinfo = New-Object System.Diagnostics.ProcessStartInfo
    $pinfo.FileName = 'pwsh'
    $pinfo.UseShellExecute = $false
    $pinfo.RedirectStandardOutput = $true
    $pinfo.RedirectStandardError = $true
    $pinfo.CreateNoWindow = $true
    foreach ($arg in $argsList) { $pinfo.ArgumentList.Add($arg) }

    $p = New-Object System.Diagnostics.Process
    $p.StartInfo = $pinfo
    $p.Start() | Out-Null
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    $p.WaitForExit()
    $stdout = $outTask.Result
    $stderr = $errTask.Result
    $exitCode = $p.ExitCode
    $p.Dispose()
    return @{ ExitCode = $exitCode; Stdout = $stdout; Stderr = $stderr }
}

$TestGuid = [Guid]::NewGuid().ToString('N')
$TestOutputRoot = Join-Path $RepoRoot 'outputs\candidate-tests'
$CurrentTestRoot = Join-Path $TestOutputRoot $TestGuid
New-Item -ItemType Directory -Path $CurrentTestRoot -Force | Out-Null

$srcHashes = @{
    Server = Get-FileSha256Lower $Server
    ControlCenter = Get-FileSha256Lower $ControlCenter
    Apk = Get-FileSha256Lower $Apk
}
$srcSizes = @{
    Server = (Get-Item -LiteralPath $Server).Length
    ControlCenter = (Get-Item -LiteralPath $ControlCenter).Length
    Apk = (Get-Item -LiteralPath $Apk).Length
}

$TestsPassed = 0
$TestsFailed = 0
$TestsSkipped = 0

Write-Host "Running Positive Test 1..."
$beforeDirs = if (Test-Path -LiteralPath $CurrentTestRoot) { @(Get-ChildItem -LiteralPath $CurrentTestRoot -Directory | Select-Object -ExpandProperty FullName) } else { @() }
$res1 = Invoke-CandidateRun -ServerArg $Server -ControlCenterArg $ControlCenter -ApkArg $Apk -Aapt2PathArg $Aapt2Path -OutputRootArg "outputs/candidate-tests/$TestGuid"
if ($res1.ExitCode -ne 0) { throw "Positive run 1 failed:`n$($res1.Stderr)`n$($res1.Stdout)" }

$afterDirs1 = @(Get-ChildItem -LiteralPath $CurrentTestRoot -Directory | Select-Object -ExpandProperty FullName)
$newDirs1 = @($afterDirs1 | Where-Object { $_ -notin $beforeDirs })
if ($newDirs1.Count -ne 1) { throw "Expected exactly 1 new run directory, found $($newDirs1.Count)" }

$runDir1 = $newDirs1[0]
$report1Path = Join-Path $runDir1 'candidate.json'
$report1Raw = Get-Content -Raw -LiteralPath $report1Path
$report1Sha = Get-FileSha256Lower $report1Path
$report1 = $report1Raw | ConvertFrom-Json

if ($report1.status -ne 'complete') { throw "status not complete" }
if ($report1.mode -ne 'staging') { throw "mode not staging" }
if ($report1.releasable -ne $false) { throw "releasable not false" }
if ($report1.files.Count -ne 3) { throw "Expected 3 files in report" }

$seenNames = @()
foreach ($file in $report1.files) {
    if ($file.name -in $seenNames) { throw "Duplicate filename in report: $($file.name)" }
    $seenNames += $file.name

    $srcKey = $null
    if ($file.name -eq 'PhoneDeck.Server.exe') { $srcKey = 'Server'; if ($file.path -ne 'payload/PhoneDeck.Server.exe') { throw "Wrong path" } }
    elseif ($file.name -eq 'PhoneDeck.ControlCenter.exe') { $srcKey = 'ControlCenter'; if ($file.path -ne 'payload/PhoneDeck.ControlCenter.exe') { throw "Wrong path" } }
    elseif ($file.name -eq 'PhoneDeck.apk') { $srcKey = 'Apk'; if ($file.path -ne 'payload/PhoneDeck.apk') { throw "Wrong path" } }
    else { throw "Unexpected file in report: $($file.name)" }

    if ($file.size -ne $srcSizes[$srcKey]) { throw "Size mismatch in report for $srcKey" }
    if ($file.sha256 -ne $srcHashes[$srcKey]) { throw "Hash mismatch in report for $srcKey" }

    $stagedPath = Join-Path $runDir1 $file.path
    $stagedHash = Get-FileSha256Lower $stagedPath
    $stagedSize = (Get-Item -LiteralPath $stagedPath).Length

    if ($stagedSize -ne $srcSizes[$srcKey]) { throw "Staged size mismatch for $srcKey" }
    if ($stagedHash -ne $srcHashes[$srcKey]) { throw "Staged hash mismatch for $srcKey" }
}
$TestsPassed++

Write-Host "Running Positive Test 2 (Repeated)..."
$beforeDirs2 = @(Get-ChildItem -LiteralPath $CurrentTestRoot -Directory | Select-Object -ExpandProperty FullName)
$res2 = Invoke-CandidateRun -ServerArg $Server -ControlCenterArg $ControlCenter -ApkArg $Apk -Aapt2PathArg $Aapt2Path -OutputRootArg "outputs/candidate-tests/$TestGuid"
if ($res2.ExitCode -ne 0) { throw "Positive run 2 failed:`n$($res2.Stderr)`n$($res2.Stdout)" }

$afterDirs2 = @(Get-ChildItem -LiteralPath $CurrentTestRoot -Directory | Select-Object -ExpandProperty FullName)
$newDirs2 = @($afterDirs2 | Where-Object { $_ -notin $beforeDirs2 })
if ($newDirs2.Count -ne 1) { throw "Expected exactly 1 new run directory in run 2, found $($newDirs2.Count)" }

$runDir2 = $newDirs2[0]
if ($runDir1 -eq $runDir2) { throw "Run directories must be distinct" }

$report1ShaAfterRun2 = Get-FileSha256Lower $report1Path
if ($report1Sha -ne $report1ShaAfterRun2) { throw "Old report was modified" }

foreach ($file in $report1.files) {
    $stagedPath = Join-Path $runDir1 $file.path
    $stagedHash = Get-FileSha256Lower $stagedPath
    $srcKey = if ($file.name -eq 'PhoneDeck.Server.exe') { 'Server' } elseif ($file.name -eq 'PhoneDeck.ControlCenter.exe') { 'ControlCenter' } else { 'Apk' }
    if ($stagedHash -ne $srcHashes[$srcKey]) { throw "Old payload $srcKey was modified" }
}
$TestsPassed++

Write-Host "Running Negative Test: Outside outputs rejected..."
$fixtureOutside = Join-Path ([System.IO.Path]::GetTempPath()) "fixture-outside-$TestGuid"
New-Item -ItemType Directory -Path $fixtureOutside -Force | Out-Null
$sentinelOutside = Join-Path $fixtureOutside 'sentinel.txt'
Set-Content -Path $sentinelOutside -Value 'sentinel'
$resOutside = Invoke-CandidateRun -ServerArg $Server -ControlCenterArg $ControlCenter -ApkArg $Apk -Aapt2PathArg $Aapt2Path -OutputRootArg $fixtureOutside
if ($resOutside.ExitCode -eq 0) { throw "Expected failure for outside outputs" }
if ($resOutside.Stdout -notmatch "OutputRoot must be a subdirectory of repo outputs") { throw "Expected real diagnostic for outside, got: $($resOutside.Stdout)" }
if (-not (Test-Path -LiteralPath $sentinelOutside) -or (Get-Content -LiteralPath $sentinelOutside) -ne 'sentinel') { throw "Sentinel changed in outside outputs!" }
$TestsPassed++

Write-Host "Running Negative Test: Outputs-root rejected..."
$fixtureOutputs = Join-Path $RepoRoot "outputs\fixture-outputs-$TestGuid"
New-Item -ItemType Directory -Path $fixtureOutputs -Force | Out-Null
$sentinelOutputs = Join-Path $fixtureOutputs 'sentinel.txt'
Set-Content -Path $sentinelOutputs -Value 'sentinel'
$resOutputsRoot = Invoke-CandidateRun -ServerArg $Server -ControlCenterArg $ControlCenter -ApkArg $Apk -Aapt2PathArg $Aapt2Path -OutputRootArg "outputs"
if ($resOutputsRoot.ExitCode -eq 0) { throw "Expected failure for outputs root" }
if ($resOutputsRoot.Stdout -notmatch "OutputRoot must be a subdirectory of repo outputs/ \(not outputs itself\)") { throw "Expected real diagnostic for outputs root, got: $($resOutputsRoot.Stdout)" }
if (-not (Test-Path -LiteralPath $sentinelOutputs) -or (Get-Content -LiteralPath $sentinelOutputs) -ne 'sentinel') { throw "Sentinel changed in outputs root!" }
$TestsPassed++

Write-Host "Running Negative Test: Junction ancestor pointing outside..."
$junctionFixture = Join-Path $CurrentTestRoot 'junction-fixture'
New-Item -ItemType Directory -Path $junctionFixture -Force | Out-Null
$junctionOutsideTarget = Join-Path $TestOutputRoot "outside-target-$TestGuid"
New-Item -ItemType Directory -Path $junctionOutsideTarget -Force | Out-Null
$sentinelJunction = Join-Path $junctionOutsideTarget 'sentinel.txt'
Set-Content -Path $sentinelJunction -Value 'sentinel'

$junctionCreated = $false
try {
    New-Item -ItemType Junction -Path (Join-Path $junctionFixture 'link') -Target $junctionOutsideTarget -ErrorAction Stop | Out-Null
    $junctionCreated = $true
} catch {
    Write-Host "Link creation not permitted, explicit skip junction test."
    $TestsSkipped++
}

if ($junctionCreated) {
    $relJunction = "outputs/candidate-tests/$TestGuid/junction-fixture/link/nested"
    $resJunction = Invoke-CandidateRun -ServerArg $Server -ControlCenterArg $ControlCenter -ApkArg $Apk -Aapt2PathArg $Aapt2Path -OutputRootArg $relJunction
    if ($resJunction.ExitCode -eq 0) { throw "Expected failure for junction output" }
    if ($resJunction.Stdout -notmatch "Directory chain must not contain symlink/junction/reparse") { throw "Expected real diagnostic for junction, got: $($resJunction.Stdout)" }
    if (-not (Test-Path -LiteralPath $sentinelJunction) -or (Get-Content -LiteralPath $sentinelJunction) -ne 'sentinel') { throw "Sentinel changed in junction target!" }
    $TestsPassed++
}

Write-Host "Running Negative Test: Bad aapt2 path..."
$beforeDirsBadAapt = @(Get-ChildItem -LiteralPath $CurrentTestRoot -Directory | Select-Object -ExpandProperty FullName)
$resBadAapt = Invoke-CandidateRun -ServerArg $Server -ControlCenterArg $ControlCenter -ApkArg $Apk -Aapt2PathArg 'C:\invalid\path\to\aapt2.exe' -OutputRootArg "outputs/candidate-tests/$TestGuid"
if ($resBadAapt.ExitCode -eq 0) { throw "Expected failure for bad aapt2 path" }
if ($resBadAapt.Stdout -notmatch "Aapt2Path file not found") { throw "Expected real diagnostic for bad aapt2, got: $($resBadAapt.Stdout)" }

$afterDirsBadAapt = @(Get-ChildItem -LiteralPath $CurrentTestRoot -Directory | Select-Object -ExpandProperty FullName)
$newDirsBad = @($afterDirsBadAapt | Where-Object { $_ -notin $beforeDirsBadAapt })
if ($newDirsBad.Count -eq 1) {
    $failReportPath = Join-Path $newDirsBad[0] 'candidate.json'
    if (Test-Path -LiteralPath $failReportPath) {
        $failReport = Get-Content -Raw -LiteralPath $failReportPath | ConvertFrom-Json
        if ($failReport.status -ne 'failed') { throw "Expected failed status in report for bad aapt2 path" }
    } else {
        throw "Failed to find candidate.json in bad aapt2 path failed run dir"
    }
} else {
    throw "Expected exactly 1 new failed run directory for bad aapt2, found $($newDirsBad.Count)"
}
$TestsPassed++

Write-Host "Running Negative Test: Swapped valid EXEs..."
$beforeDirsSwap = @(Get-ChildItem -LiteralPath $CurrentTestRoot -Directory | Select-Object -ExpandProperty FullName)
$resSwap = Invoke-CandidateRun -ServerArg $ControlCenter -ControlCenterArg $Server -ApkArg $Apk -Aapt2PathArg $Aapt2Path -OutputRootArg "outputs/candidate-tests/$TestGuid"
if ($resSwap.ExitCode -eq 0) { throw "Expected failure for swapped EXEs" }
if ($resSwap.Stdout -notmatch "Assert-PackageVersions failed" -and $resSwap.Stderr -notmatch "Invalid OriginalFilename") { throw "Expected real diagnostic for swapped EXEs, got stdout: $($resSwap.Stdout) stderr: $($resSwap.Stderr)" }

$afterDirsSwap = @(Get-ChildItem -LiteralPath $CurrentTestRoot -Directory | Select-Object -ExpandProperty FullName)
$newDirsSwap = @($afterDirsSwap | Where-Object { $_ -notin $beforeDirsSwap })
if ($newDirsSwap.Count -eq 1) {
    $failReportPath = Join-Path $newDirsSwap[0] 'candidate.json'
    if (Test-Path -LiteralPath $failReportPath) {
        $failReport = Get-Content -Raw -LiteralPath $failReportPath | ConvertFrom-Json
        if ($failReport.status -ne 'failed') { throw "Expected failed status in report for swapped EXEs" }
    } else {
        throw "Failed to find candidate.json in swapped EXEs failed run dir"
    }
} else {
    throw "Expected exactly 1 new failed run directory for swapped EXEs, found $($newDirsSwap.Count)"
}
$TestsPassed++

Write-Host "Tests Passed: $TestsPassed, Failed: $TestsFailed, Skipped: $TestsSkipped"
if ($TestsFailed -eq 0) {
    Write-Host "All tests passed successfully."
    exit 0
} else {
    Write-Host "Some tests failed."
    exit 1
}
