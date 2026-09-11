<#
.SYNOPSIS
    Test-LocalCandidatePackages.ps1
.DESCRIPTION
    Validates New-LocalCandidatePackages.ps1 actual behavior.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$CandidateDirectory,
    [Parameter(Mandatory=$true)][string]$Aapt2Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7 -or -not $IsWindows) {
    throw "Requires PowerShell 7+ on Windows."
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path -LiteralPath (Join-Path $ScriptDir '..\..\..')).Path
$CoreScript = Join-Path $RepoRoot 'scripts\release\New-LocalCandidatePackages.ps1'

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

function Invoke-PackageRun {
    param(
        [string]$CandidateArg,
        [string]$Aapt2PathArg,
        [string]$OutputRootArg
    )
    $argsList = @('-NoProfile', '-NonInteractive', '-File', $CoreScript, '-CandidateDirectory', $CandidateArg, '-Aapt2Path', $Aapt2PathArg)
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
$TestOutputRoot = Join-Path $RepoRoot 'outputs\local-package-tests'
$CurrentTestRoot = Join-Path $TestOutputRoot $TestGuid
New-Item -ItemType Directory -Path $CurrentTestRoot -Force | Out-Null

$TestsPassed = 0
$TestsFailed = 0

Write-Host "Running Positive Test..."
$beforeDirs = if (Test-Path -LiteralPath $CurrentTestRoot) { @(Get-ChildItem -LiteralPath $CurrentTestRoot -Directory | Select-Object -ExpandProperty FullName) } else { @() }
$res1 = Invoke-PackageRun -CandidateArg $CandidateDirectory -Aapt2PathArg $Aapt2Path -OutputRootArg "outputs/local-package-tests/$TestGuid"
if ($res1.ExitCode -ne 0) { throw "Positive run failed:`n$($res1.Stderr)`n$($res1.Stdout)" }

$afterDirs1 = @(Get-ChildItem -LiteralPath $CurrentTestRoot -Directory | Select-Object -ExpandProperty FullName)
$newDirs1 = @($afterDirs1 | Where-Object { $_ -notin $beforeDirs })
if ($newDirs1.Count -ne 1) { throw "Expected exactly 1 new run directory, found $($newDirs1.Count)" }

$runDir1 = $newDirs1[0]
$packagesJsonPath = Join-Path $runDir1 'packages.json'
if (-not (Test-Path -LiteralPath $packagesJsonPath)) { throw "packages.json missing" }
$packagesJson = Get-Content -Raw -LiteralPath $packagesJsonPath | ConvertFrom-Json
if ($packagesJson.mode -ne 'staging') { throw "mode not staging" }
if ($packagesJson.releasable -ne $false) { throw "releasable not false" }
if ($packagesJson.packages.Count -ne 2) { throw "Expected 2 packages" }
if ($packagesJson.status -ne 'complete') { throw "Expected status complete, found $($packagesJson.status)" }

$portableZip = Join-Path $runDir1 'PhoneDeck-Portable-STAGING.zip'
$enrollZip = Join-Path $runDir1 'PhoneDeck-Enrollment-STAGING.zip'
if (-not (Test-Path -LiteralPath $portableZip)) { throw "Missing portable ZIP" }
if (-not (Test-Path -LiteralPath $enrollZip)) { throw "Missing enrollment ZIP" }

foreach ($pkg in $packagesJson.packages) {
    $zipPath = Join-Path $runDir1 $pkg.name
    if (-not (Test-Path -LiteralPath $zipPath)) { throw "ZIP mentioned in report missing" }
    $actualHash = Get-FileSha256Lower $zipPath
    $actualSize = (Get-Item -LiteralPath $zipPath).Length
    if ($actualHash -ne $pkg.sha256) { throw "Hash mismatch for $($pkg.name)" }
    if ($actualSize -ne $pkg.size) { throw "Size mismatch for $($pkg.name)" }
}

# Inspect actual ZIP entries/content hashes
$portableExpectedEntries = @('PhoneDeck.Server.exe', 'PhoneDeck.ControlCenter.exe', 'PhoneDeck.apk', 'Start-ControlCenter.cmd', 'README.txt', 'data/') | Sort-Object
$enrollExpectedEntries = @('PhoneDeck.Server.exe', 'PhoneDeck.ControlCenter.exe', 'PhoneDeck.apk', 'Install-UpdateSupport.ps1', 'Install-UpdateSupport.cmd', 'README.txt') | Sort-Object

$candidateJson = Get-Content -Raw -LiteralPath (Join-Path $CandidateDirectory 'candidate.json') | ConvertFrom-Json
$candidateHashes = @{}
foreach ($f in $candidateJson.files) { $candidateHashes[$f.name] = $f.sha256 }

$installSupportPs1Path = Join-Path $RepoRoot 'work\phone-deck\Install-UpdateSupport.ps1'
$installSupportCmdPath = Join-Path $RepoRoot 'work\phone-deck\Install-UpdateSupport.cmd'
$installSupportPs1RepoHash = Get-FileSha256Lower $installSupportPs1Path
$installSupportCmdRepoHash = Get-FileSha256Lower $installSupportCmdPath

Add-Type -AssemblyName System.IO.Compression.FileSystem
function Get-ZipEntrySha256Lower {
    param([System.IO.Compression.ZipArchiveEntry]$Entry)
    $stream = $Entry.Open()
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hashBytes = $sha256.ComputeHash($stream)
            return [System.BitConverter]::ToString($hashBytes).Replace('-', '').ToLowerInvariant()
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-ZipEntryText {
    param([System.IO.Compression.ZipArchiveEntry]$Entry)
    $stream = $Entry.Open()
    try {
        $reader = New-Object System.IO.StreamReader($stream)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Test-ZipArchive {
    param([string]$ZipPath, [string[]]$ExpectedEntries, [bool]$IsEnrollment)
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $actualEntries = @()
        foreach ($entry in $archive.Entries) {
            $actualEntries += $entry.FullName -replace '\\', '/'
            if ($candidateHashes.ContainsKey($entry.Name)) {
                $actualHash = Get-ZipEntrySha256Lower -Entry $entry
                if ($actualHash -ne $candidateHashes[$entry.Name]) { throw "Payload hash mismatch in ZIP for $($entry.Name)" }
            } elseif ($entry.Name -eq 'Install-UpdateSupport.ps1') {
                $actualHash = Get-ZipEntrySha256Lower -Entry $entry
                if ($actualHash -ne $installSupportPs1RepoHash) { throw "Install-UpdateSupport.ps1 ZIP stream hash mismatch vs repo file" }
            } elseif ($entry.Name -eq 'Install-UpdateSupport.cmd') {
                $actualHash = Get-ZipEntrySha256Lower -Entry $entry
                if ($actualHash -ne $installSupportCmdRepoHash) { throw "Install-UpdateSupport.cmd ZIP stream hash mismatch vs repo file" }
            } elseif ($entry.Name -eq 'Start-ControlCenter.cmd') {
                $content = Get-ZipEntryText -Entry $entry
                if ($content -notmatch '(?im)^\s*setlocal\s*$') { throw "Start-ControlCenter.cmd missing setlocal" }
                if ($content -notmatch 'set\s+"PHONEDECK_DATA_DIR=%~dp0data"') { throw "Start-ControlCenter.cmd missing quoted PHONEDECK_DATA_DIR" }
                if ($content -notmatch 'PhoneDeck\.ControlCenter\.exe') { throw "Start-ControlCenter.cmd missing local ControlCenter launch" }
                if ($content -match '(?i)Install-UpdateSupport') { throw "Start-ControlCenter.cmd must not invoke install support" }
            } elseif ($entry.Name -eq 'README.txt') {
                $content = Get-ZipEntryText -Entry $entry
                if ($content -notmatch 'not a public release|STAGING') { throw "README must state staging / not public release" }
                if ($content -notmatch 'VB-CABLE') { throw "README missing VB-CABLE prerequisite note" }
                if ($content -notmatch 'IME|input method') { throw "README missing IME prerequisite note" }
                if ($content -notmatch 'ADB') { throw "README missing ADB prerequisite note" }
                if ($content -notmatch 'APK signing channel is unconfirmed|APK channel') { throw "README missing APK channel unconfirmed note" }
                if ($IsEnrollment) {
                    if ($content -notmatch 'Install-UpdateSupport\.cmd\s+-InstallDirectory\s+"C:\\path\\existing"\s+-DataDirectory\s+"C:\\path\\existing\\data"') {
                        throw "Enrollment README missing explicit Install-UpdateSupport.cmd -InstallDirectory/-DataDirectory example"
                    }
                    if ($content -notmatch 'SEPARATE folder|separate folder') { throw "Enrollment README missing extract-separate-folder instruction" }
                    if ($content -notmatch 'identity|computer-id|Back up|backup') { throw "Enrollment README missing identity/backup guidance" }
                    if ($content -notmatch 'microphone idle|Microphone is busy|dictation') { throw "Enrollment README missing mic idle guidance" }
                    if ($content -match '更新支持服务的安装') { throw "Enrollment README must not claim service installation" }
                    if ($content -notmatch '(?i)not Windows service') { throw "Enrollment README must clarify it is not Windows service install" }
                    if ($content -notmatch 'replaces local executables|local executable') { throw "Enrollment README must describe local executable replacement" }
                } else {
                    if ($content -notmatch 'Start-ControlCenter\.cmd') { throw "Portable README missing Start-ControlCenter.cmd" }
                    if ($content -notmatch 'PHONEDECK_DATA_DIR') { throw "Portable README missing PHONEDECK_DATA_DIR note" }
                    if ($content -notmatch 'does not install a Windows service|not.*Windows service') { throw "Portable README must clarify it is not service install" }
                }
            }
        }

        $sortedActual = $actualEntries | Sort-Object
        if ($ExpectedEntries.Length -ne $sortedActual.Length) { throw "ZIP entry count mismatch. Expected $($ExpectedEntries.Length), got $($sortedActual.Length)" }
        for ($i = 0; $i -lt $ExpectedEntries.Length; $i++) {
            if ($ExpectedEntries[$i] -ne $sortedActual[$i]) { throw "ZIP entry mismatch: expected $($ExpectedEntries[$i]), found $($sortedActual[$i])" }
        }
    }
    finally {
        $archive.Dispose()
    }
}

Test-ZipArchive -ZipPath $portableZip -ExpectedEntries $portableExpectedEntries -IsEnrollment $false
Test-ZipArchive -ZipPath $enrollZip -ExpectedEntries $enrollExpectedEntries -IsEnrollment $true

$TestsPassed++

Write-Host "Running Positive Test 2: Repeat old zip / report unchanged"
# Snapshot run1 artifacts BEFORE invoking res2; later re-read the SAME paths.
$run1PackagesJsonHashBefore = Get-FileSha256Lower $packagesJsonPath
$run1PortableZipHashBefore = Get-FileSha256Lower $portableZip
$run1EnrollZipHashBefore = Get-FileSha256Lower $enrollZip

$res2 = Invoke-PackageRun -CandidateArg $CandidateDirectory -Aapt2PathArg $Aapt2Path -OutputRootArg "outputs/local-package-tests/$TestGuid"
if ($res2.ExitCode -ne 0) { throw "Repeat run failed" }
$afterDirs2 = @(Get-ChildItem -LiteralPath $CurrentTestRoot -Directory | Select-Object -ExpandProperty FullName)
$newDirs2 = @($afterDirs2 | Where-Object { $_ -notin $afterDirs1 })
if ($newDirs2.Count -ne 1) { throw "Expected exactly 1 new run directory for repeat, found $($newDirs2.Count)" }
$runDir2 = $newDirs2[0]

# Assert run1 SAME paths were not mutated by the second packaging run.
if ((Get-FileSha256Lower $packagesJsonPath) -ne $run1PackagesJsonHashBefore) { throw "run1 packages.json mutated after res2" }
if ((Get-FileSha256Lower $portableZip) -ne $run1PortableZipHashBefore) { throw "run1 portable ZIP mutated after res2" }
if ((Get-FileSha256Lower $enrollZip) -ne $run1EnrollZipHashBefore) { throw "run1 enrollment ZIP mutated after res2" }

$packagesJsonPath2 = Join-Path $runDir2 'packages.json'
if (-not (Test-Path -LiteralPath $packagesJsonPath2)) { throw "packages.json missing in run 2" }
$packagesJson2 = Get-Content -Raw -LiteralPath $packagesJsonPath2 | ConvertFrom-Json

if ($packagesJson.packages.Count -ne $packagesJson2.packages.Count) { throw "Package count mismatch between runs" }
for ($i = 0; $i -lt $packagesJson.packages.Count; $i++) {
    if ($packagesJson.packages[$i].sha256 -ne $packagesJson2.packages[$i].sha256) {
        throw "Hash mismatch for $($packagesJson.packages[$i].name) between runs"
    }
}

$portableZip2 = Join-Path $runDir2 'PhoneDeck-Portable-STAGING.zip'
$enrollZip2 = Join-Path $runDir2 'PhoneDeck-Enrollment-STAGING.zip'
if ($run1PortableZipHashBefore -ne (Get-FileSha256Lower $portableZip2)) { throw "Portable ZIP hash mismatch between run1 snapshot and run2" }
if ($run1EnrollZipHashBefore -ne (Get-FileSha256Lower $enrollZip2)) { throw "Enrollment ZIP hash mismatch between run1 snapshot and run2" }

$TestsPassed++

# Missing
Write-Host "Running Negative Test: Missing payload file..."
$missingFileCandidate = Join-Path $CurrentTestRoot 'missing-file-candidate'
Copy-Item -LiteralPath $CandidateDirectory -Destination $missingFileCandidate -Recurse -Force
Remove-Item -LiteralPath (Join-Path $missingFileCandidate 'payload/PhoneDeck.apk') -Force
$resMissing = Invoke-PackageRun -CandidateArg $missingFileCandidate -Aapt2PathArg $Aapt2Path -OutputRootArg "outputs/local-package-tests/$TestGuid"
if ($resMissing.ExitCode -eq 0) { throw "Expected failure for missing file" }
if ($resMissing.Stdout -notmatch "Missing payload file") { throw "Expected diagnostic for missing file, got: $($resMissing.Stdout)" }
$TestsPassed++

# Tampered candidate
Write-Host "Running Negative Test: Tampered candidate..."
$tamperedCandidate = Join-Path $CurrentTestRoot 'tampered-candidate'
Copy-Item -LiteralPath $CandidateDirectory -Destination $tamperedCandidate -Recurse -Force
$tamperedJsonPath = Join-Path $tamperedCandidate 'candidate.json'
$tamperedJson = Get-Content -Raw -LiteralPath $tamperedJsonPath | ConvertFrom-Json
$tamperedJson.files[0].sha256 = '0000000000000000000000000000000000000000000000000000000000000000'
$tamperedJsonContent = $tamperedJson | ConvertTo-Json -Depth 10
Set-Content -Path $tamperedJsonPath -Value $tamperedJsonContent

$resTampered = Invoke-PackageRun -CandidateArg $tamperedCandidate -Aapt2PathArg $Aapt2Path -OutputRootArg "outputs/local-package-tests/$TestGuid"
if ($resTampered.ExitCode -eq 0) { throw "Expected failure for tampered candidate" }
if ($resTampered.Stdout -notmatch "Payload hash mismatch") { throw "Expected real diagnostic for tampered candidate, got: $($resTampered.Stdout)" }
$TestsPassed++

# Duplicate entries
Write-Host "Running Negative Test: Duplicate entries..."
$dupCandidate = Join-Path $CurrentTestRoot 'duplicate-candidate'
Copy-Item -LiteralPath $CandidateDirectory -Destination $dupCandidate -Recurse -Force
$dupJsonPath = Join-Path $dupCandidate 'candidate.json'
$dupJson = Get-Content -Raw -LiteralPath $dupJsonPath | ConvertFrom-Json
$dupJson.files += $dupJson.files[0]
$dupJsonContent = $dupJson | ConvertTo-Json -Depth 10
Set-Content -Path $dupJsonPath -Value $dupJsonContent

$resDup = Invoke-PackageRun -CandidateArg $dupCandidate -Aapt2PathArg $Aapt2Path -OutputRootArg "outputs/local-package-tests/$TestGuid"
if ($resDup.ExitCode -eq 0) { throw "Expected failure for duplicate entries" }
if ($resDup.Stdout -notmatch "Duplicate file entry") { throw "Expected diagnostic for duplicate entries, got: $($resDup.Stdout)" }
$TestsPassed++

# Failed candidate
Write-Host "Running Negative Test: Failed candidate file status..."
$failCandidate = Join-Path $CurrentTestRoot 'failed-candidate'
Copy-Item -LiteralPath $CandidateDirectory -Destination $failCandidate -Recurse -Force
$failJsonPath = Join-Path $failCandidate 'candidate.json'
$failJson = Get-Content -Raw -LiteralPath $failJsonPath | ConvertFrom-Json
$failJson.files[0] | Add-Member -MemberType NoteProperty -Name 'status' -Value 'failed' -Force
$failJsonContent = $failJson | ConvertTo-Json -Depth 10
Set-Content -Path $failJsonPath -Value $failJsonContent

$resFail = Invoke-PackageRun -CandidateArg $failCandidate -Aapt2PathArg $Aapt2Path -OutputRootArg "outputs/local-package-tests/$TestGuid"
if ($resFail.ExitCode -eq 0) { throw "Expected failure for failed candidate file status" }
if ($resFail.Stdout -notmatch "status is not complete") { throw "Expected diagnostic for failed candidate, got: $($resFail.Stdout)" }
$TestsPassed++

# Invalid Top-Level Status
Write-Host "Running Negative Test: Invalid Top-Level Status..."
$invalidStatusCandidate = Join-Path $CurrentTestRoot 'invalid-status-candidate'
Copy-Item -LiteralPath $CandidateDirectory -Destination $invalidStatusCandidate -Recurse -Force
$invalidStatusJsonPath = Join-Path $invalidStatusCandidate 'candidate.json'
$invalidStatusJson = Get-Content -Raw -LiteralPath $invalidStatusJsonPath | ConvertFrom-Json
$invalidStatusJson.status = 'running'
Set-Content -Path $invalidStatusJsonPath -Value ($invalidStatusJson | ConvertTo-Json -Depth 10)

$resInvalidStatus = Invoke-PackageRun -CandidateArg $invalidStatusCandidate -Aapt2PathArg $Aapt2Path -OutputRootArg "outputs/local-package-tests/$TestGuid"
if ($resInvalidStatus.ExitCode -eq 0) { throw "Expected failure for invalid top-level status" }
if ($resInvalidStatus.Stdout -notmatch "Invalid candidate.json schemaVersion, mode, status, or releasable flag") { throw "Expected diagnostic for invalid status, got: $($resInvalidStatus.Stdout)" }
$TestsPassed++

# Invalid Releasable Type
Write-Host "Running Negative Test: Invalid Releasable Type..."
$invalidReleasableCandidate = Join-Path $CurrentTestRoot 'invalid-releasable-candidate'
Copy-Item -LiteralPath $CandidateDirectory -Destination $invalidReleasableCandidate -Recurse -Force
$invalidReleasableJsonPath = Join-Path $invalidReleasableCandidate 'candidate.json'
$invalidReleasableJson = Get-Content -Raw -LiteralPath $invalidReleasableJsonPath | ConvertFrom-Json
$invalidReleasableJson.releasable = 'false'
Set-Content -Path $invalidReleasableJsonPath -Value ($invalidReleasableJson | ConvertTo-Json -Depth 10)

$resInvalidReleasable = Invoke-PackageRun -CandidateArg $invalidReleasableCandidate -Aapt2PathArg $Aapt2Path -OutputRootArg "outputs/local-package-tests/$TestGuid"
if ($resInvalidReleasable.ExitCode -eq 0) { throw "Expected failure for invalid releasable type" }
if ($resInvalidReleasable.Stdout -notmatch "Invalid candidate.json schemaVersion, mode, status, or releasable flag") { throw "Expected diagnostic for invalid releasable, got: $($resInvalidReleasable.Stdout)" }
$TestsPassed++


# Check output escape
Write-Host "Running Negative Test: Output Escape..."
$resEscape = Invoke-PackageRun -CandidateArg $CandidateDirectory -Aapt2PathArg $Aapt2Path -OutputRootArg "../escape-test"
if ($resEscape.ExitCode -eq 0) { throw "Expected failure for output escape" }
$TestsPassed++

Write-Host "Tests Passed: $TestsPassed, Failed: $TestsFailed"
if ($TestsFailed -eq 0) {
    Write-Host "All tests passed successfully."
    exit 0
} else {
    Write-Host "Some tests failed."
    exit 1
}
