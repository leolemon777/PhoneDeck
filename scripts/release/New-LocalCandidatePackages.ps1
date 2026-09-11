[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$CandidateDirectory,
    [string]$OutputRoot = 'outputs\local-packages',
    [string]$Aapt2Path = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "Requires PowerShell 7+"
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path -LiteralPath (Join-Path $ScriptDir '..\..')).Path
. (Join-Path $ScriptDir 'CandidatePathHelpers.ps1')

$script:PhaseName = 'init'
$script:PhaseExitCode = 1
$script:FinalStatus = 'failed'
$runDir = $null
$repoParent = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot '..'))

function Resolve-OptionalToolPath {
    param([string]$Path, [string]$Label, [string]$ToolName = '')
    if ([string]::IsNullOrWhiteSpace($Path)) {
        if ([string]::IsNullOrWhiteSpace($ToolName)) { return '' }
        $cmd = Get-Command $ToolName -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd.Source }
        return ''
    }
    $full = if ([System.IO.Path]::IsPathRooted($Path)) { [System.IO.Path]::GetFullPath($Path) } else { [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path)) }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "$Label file not found." }
    if (Test-HasReparseOrLink -PathToCheck $full) { throw "$Label must not be a symlink/junction/reparse file." }
    return $full
}

function Invoke-CheckedPwshFile {
    param([string]$ScriptPath, [string]$StepName, [string[]]$ArgumentList = @())
    if (-not (Test-Path -LiteralPath $ScriptPath -PathType Leaf)) { throw "Missing script $StepName." }
    $args = @('-NoProfile', '-NonInteractive', '-File', $ScriptPath) + $ArgumentList
    & pwsh @args
    $code = $LASTEXITCODE
    if ($null -eq $code) { $code = 0 }
    if ($code -ne 0) { throw "${StepName} failed with exit code ${code}." }
}

try {
    $script:PhaseName = 'resolve-inputs'
    $candidateFull = if ([System.IO.Path]::IsPathRooted($CandidateDirectory)) {
        [System.IO.Path]::GetFullPath($CandidateDirectory)
    } else {
        [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $CandidateDirectory))
    }
    if (-not (Test-Path -LiteralPath $candidateFull -PathType Container)) {
        throw "Candidate directory not found."
    }
    Assert-TreeHasNoReparse -RootPath $candidateFull

    $jsonPath = Join-Path $candidateFull 'candidate.json'
    if (-not (Test-Path -LiteralPath $jsonPath -PathType Leaf)) {
        throw "candidate.json not found in candidate directory."
    }
    $candidateJson = Get-Content -Raw -LiteralPath $jsonPath | ConvertFrom-Json

    if (($candidateJson.schemaVersion -isnot [int] -and $candidateJson.schemaVersion -isnot [long]) -or $candidateJson.schemaVersion -ne 1 -or $candidateJson.mode -ne 'staging' -or $candidateJson.status -ne 'complete' -or $candidateJson.releasable -isnot [bool] -or $candidateJson.releasable -ne $false) {
        throw "Invalid candidate.json schemaVersion, mode, status, or releasable flag."
    }

    $staged = [ordered]@{
        Server = Join-Path $candidateFull 'payload\PhoneDeck.Server.exe'
        ControlCenter = Join-Path $candidateFull 'payload\PhoneDeck.ControlCenter.exe'
        Apk = Join-Path $candidateFull 'payload\PhoneDeck.apk'
    }

    $script:PhaseName = 'validate-candidate-payload'
    $candidateFiles = @{}
    $expectedNamesAndPaths = @{
        'PhoneDeck.Server.exe' = 'payload/PhoneDeck.Server.exe'
        'PhoneDeck.ControlCenter.exe' = 'payload/PhoneDeck.ControlCenter.exe'
        'PhoneDeck.apk' = 'payload/PhoneDeck.apk'
    }

    foreach ($f in $candidateJson.files) {
        if ($candidateFiles.ContainsKey($f.name)) { throw "Duplicate file entry in candidate.json: $($f.name)" }

        if (-not $expectedNamesAndPaths.ContainsKey($f.name)) { throw "Unexpected file name in candidate.json: $($f.name)" }
        $expectedPath = $expectedNamesAndPaths[$f.name]
        $actualPath = $f.path -replace '\\', '/'
        if ($actualPath -ne $expectedPath) { throw "Invalid path for $($f.name)" }

        if ($f.size -isnot [int] -and $f.size -isnot [long]) { throw "File $($f.name) size is not a strict integer." }
        if ($null -ne $f.psobject.Properties['status'] -and $f.status -ne 'complete') { throw "File $($f.name) status is not complete." }
        $candidateFiles[$f.name] = $f
    }

    if ($candidateFiles.Count -ne 3 -or $candidateJson.files.Count -ne 3) { throw "Expected exactly 3 files in candidate.json." }

    foreach ($k in $staged.Keys) {
        if (-not (Test-Path -LiteralPath $staged[$k] -PathType Leaf)) {
            throw "Missing payload file: $k"
        }
        $actualSize = [long](Get-Item -LiteralPath $staged[$k]).Length
        $actualHash = Get-FileSha256Lower -LiteralPath $staged[$k]

        $filename = [System.IO.Path]::GetFileName($staged[$k])
        if (-not $candidateFiles.ContainsKey($filename)) {
            throw "Payload file $filename not found in candidate.json."
        }
        $expected = $candidateFiles[$filename]

        if ($actualSize -ne [long]$expected.size) {
            throw "Payload size mismatch for $filename"
        }
        if ($actualHash -ne $expected.sha256) {
            throw "Payload hash mismatch for $filename"
        }
    }

    $descriptorPath = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot 'work\phone-deck\release-versions.json'))

    $script:PhaseName = 'package-version-validate'
    $assertPackage = Join-Path $RepoRoot 'scripts\packaging\Assert-PackageVersions.ps1'
    $pkgArgs = @(
        '-Server', $staged.Server,
        '-ControlCenter', $staged.ControlCenter,
        '-Apk', $staged.Apk,
        '-DescriptorPath', $descriptorPath
    )
    $resolvedAapt2 = Resolve-OptionalToolPath -Path $Aapt2Path -Label 'Aapt2Path' -ToolName 'aapt2.exe'
    if ($resolvedAapt2) { $pkgArgs += @('-Aapt2Path', $resolvedAapt2) }
    Invoke-CheckedPwshFile -ScriptPath $assertPackage -StepName 'Assert-PackageVersions' -ArgumentList $pkgArgs

    $script:PhaseName = 'output-safety'
    $outFull = Resolve-SafeCandidateOutputRoot -RequestedOutputRoot $OutputRoot -RepoRoot $RepoRoot

    $script:PhaseName = 'create-run'
    $runId = [Guid]::NewGuid().ToString('N')
    $runDir = Join-Path $outFull $runId
    if (Test-Path -LiteralPath $runDir) { throw "Output directory already exists." }
    [System.IO.Directory]::CreateDirectory($runDir) | Out-Null
    Assert-AncestorChainSafe -TargetPath $runDir -StopAtPath $repoParent

    $packagesJsonPath = Join-Path $runDir 'packages.json'

    function Write-PackagesReport {
        param([string]$Status, [string]$Phase, [string]$ErrorMsg, [array]$Packages)
        $report = [ordered]@{
            schemaVersion = 1
            mode = 'staging'
            releasable = $false
            status = $Status
            candidate = [ordered]@{
                runId = [string]$candidateJson.runId
                sourceCommit = [string]$candidateJson.sourceCommit
            }
            createdUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        }
        if ($Phase) { $report.phase = $Phase }
        if ($ErrorMsg) { $report.error = $ErrorMsg }
        if ($Packages) { $report.packages = $Packages }

        $json = ($report | ConvertTo-Json -Depth 10) + [Environment]::NewLine
        Assert-AncestorChainSafe -TargetPath $runDir -StopAtPath $repoParent
        Write-AtomicTextFile -Path $packagesJsonPath -Content $json -SafeAncestorStopAt $repoParent
    }

    Write-PackagesReport -Status 'running' -Phase $script:PhaseName

    $script:PhaseName = 'create-portable-zip'
    $portableTmp = Join-Path $runDir 'tmp-portable'
    [System.IO.Directory]::CreateDirectory($portableTmp) | Out-Null
    [System.IO.Directory]::CreateDirectory((Join-Path $portableTmp 'data')) | Out-Null
    [System.IO.File]::Copy($staged.Server, (Join-Path $portableTmp 'PhoneDeck.Server.exe'))
    [System.IO.File]::Copy($staged.ControlCenter, (Join-Path $portableTmp 'PhoneDeck.ControlCenter.exe'))
    [System.IO.File]::Copy($staged.Apk, (Join-Path $portableTmp 'PhoneDeck.apk'))

    $cmdContent = "@echo off`r`nsetlocal`r`nset `"PHONEDECK_DATA_DIR=%~dp0data`"`r`nstart """" `"%~dp0PhoneDeck.ControlCenter.exe`""
    [System.IO.File]::WriteAllText((Join-Path $portableTmp 'Start-ControlCenter.cmd'), $cmdContent)

    $readmeContent = @"
PhoneDeck Portable STAGING package (not a public release).

What this is:
- Local development/staging zip only. releasable=false. Do not treat as a finished public installer.
- Contains PhoneDeck.Server.exe, PhoneDeck.ControlCenter.exe, PhoneDeck.apk, empty data/, Start-ControlCenter.cmd, and this README.
- Start-ControlCenter.cmd only sets PHONEDECK_DATA_DIR to this folder's data\ for the launched process and starts the local PhoneDeck.ControlCenter.exe. It does not install a Windows service.

How to use:
1. Extract this zip to its own folder.
2. Optional: back up any existing PhoneDeck identity/data you care about before testing.
3. Prefer microphone idle / finish dictation before starting.
4. Run Start-ControlCenter.cmd from the extracted folder. Do not launch PhoneDeck.Server.exe directly for this portable flow (Server defaults to LocalAppData unless PHONEDECK_DATA_DIR is set).
5. An existing PHONEDECK_DATA_DIR in your environment still affects where config/data live; the empty data\ folder alone does not guarantee isolation.

Prerequisites NOT included in this package:
- VB-CABLE (or equivalent virtual audio)
- Selected IME / input method setup
- ADB and USB debugging tooling
- Install those separately if your test needs them.

APK note:
- APK signing channel is unconfirmed for release. Do not assert this APK is unsigned, or that it matches/differs from any production channel.
"@
    [System.IO.File]::WriteAllText((Join-Path $portableTmp 'README.txt'), $readmeContent.Replace("`n", "`r`n"))

    foreach ($k in $staged.Keys) {
        $actualHashAfter = Get-FileSha256Lower -LiteralPath $staged[$k]
        $filename = [System.IO.Path]::GetFileName($staged[$k])
        if ($actualHashAfter -ne $candidateFiles[$filename].sha256) {
            throw "Source file $filename mutated during copy."
        }
    }

    $deterministicTime = [datetimeoffset]$candidateJson.createdUtc

    Get-ChildItem -Path $portableTmp -Recurse | ForEach-Object { $_.CreationTimeUtc = $deterministicTime.UtcDateTime; $_.LastWriteTimeUtc = $deterministicTime.UtcDateTime }
    $portableZipPath = Join-Path $runDir 'PhoneDeck-Portable-STAGING.zip'
    Compress-Archive -Path "$portableTmp\*" -DestinationPath $portableZipPath

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::Open($portableZipPath, [System.IO.Compression.ZipArchiveMode]::Update)
    $dataEntry = $archive.CreateEntry('data/')
    $dataEntry.LastWriteTime = $deterministicTime
    $archive.Dispose()

    $script:PhaseName = 'create-enrollment-zip'
    $enrollTmp = Join-Path $runDir 'tmp-enroll'
    [System.IO.Directory]::CreateDirectory($enrollTmp) | Out-Null
    [System.IO.File]::Copy($staged.Server, (Join-Path $enrollTmp 'PhoneDeck.Server.exe'))
    [System.IO.File]::Copy($staged.ControlCenter, (Join-Path $enrollTmp 'PhoneDeck.ControlCenter.exe'))
    [System.IO.File]::Copy($staged.Apk, (Join-Path $enrollTmp 'PhoneDeck.apk'))

    $installSupportPs1 = Join-Path $RepoRoot 'work\phone-deck\Install-UpdateSupport.ps1'
    $installSupportCmd = Join-Path $RepoRoot 'work\phone-deck\Install-UpdateSupport.cmd'

    $installSupportPs1ExpectedHash = Get-FileSha256Lower -LiteralPath $installSupportPs1
    $installSupportCmdExpectedHash = Get-FileSha256Lower -LiteralPath $installSupportCmd

    [System.IO.File]::Copy($installSupportPs1, (Join-Path $enrollTmp 'Install-UpdateSupport.ps1'))
    [System.IO.File]::Copy($installSupportCmd, (Join-Path $enrollTmp 'Install-UpdateSupport.cmd'))
    $enrollReadmeContent = @"
PhoneDeck Enrollment STAGING package (not a public release).

What this is:
- Local development/staging zip only. releasable=false. Do not treat as a finished public installer.
- Contains PhoneDeck.Server.exe, PhoneDeck.ControlCenter.exe, PhoneDeck.apk, Install-UpdateSupport.ps1, Install-UpdateSupport.cmd, and this README.
- Install-UpdateSupport replaces local executables in an EXISTING PhoneDeck install directory. It is not Windows service installation.

How to use:
1. Extract this zip to a SEPARATE folder (not the live install folder).
2. Keep the existing computer identity and data directory. Back up first if needed.
3. Prefer microphone idle: finish dictation and turn off the shared microphone before enrollment.
4. From the extracted folder, run explicitly, for example:
   Install-UpdateSupport.cmd -InstallDirectory "C:\path\existing" -DataDirectory "C:\path\existing\data"
5. Do not double-click blindly; pass the real existing install/data paths. The script refuses to run against its own extract folder and refuses when computer-id.txt is missing.

Prerequisites NOT included in this package:
- VB-CABLE (or equivalent virtual audio)
- Selected IME / input method setup
- ADB and USB debugging tooling
- Install those separately if your test needs them.

APK note:
- APK signing channel is unconfirmed for release. Do not assert this APK is unsigned, or that it matches/differs from any production channel.
"@
    [System.IO.File]::WriteAllText((Join-Path $enrollTmp 'README.txt'), $enrollReadmeContent.Replace("`n", "`r`n"))

    foreach ($k in $staged.Keys) {
        $actualHashAfter = Get-FileSha256Lower -LiteralPath $staged[$k]
        $filename = [System.IO.Path]::GetFileName($staged[$k])
        if ($actualHashAfter -ne $candidateFiles[$filename].sha256) {
            throw "Source file $filename mutated during copy."
        }
    }
    if ((Get-FileSha256Lower -LiteralPath $installSupportPs1) -ne $installSupportPs1ExpectedHash) { throw "Install script mutated." }
    if ((Get-FileSha256Lower -LiteralPath $installSupportCmd) -ne $installSupportCmdExpectedHash) { throw "Install script mutated." }

    Get-ChildItem -Path $enrollTmp -Recurse | ForEach-Object { $_.CreationTimeUtc = $deterministicTime.UtcDateTime; $_.LastWriteTimeUtc = $deterministicTime.UtcDateTime }
    $enrollZipPath = Join-Path $runDir 'PhoneDeck-Enrollment-STAGING.zip'
    Compress-Archive -Path "$enrollTmp\*" -DestinationPath $enrollZipPath

    $script:PhaseName = 'verify-zips'
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    function Verify-Zip {
        param([string]$ZipPath, [string[]]$ExpectedEntries, [hashtable]$ExpectedPayloadFiles, [string]$ZipType)

        $zipSize = [long](Get-Item -LiteralPath $ZipPath).Length
        $zipHash = Get-FileSha256Lower -LiteralPath $ZipPath

        $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
        try {
            $actualEntries = @()
            foreach ($entry in $archive.Entries) {
                $entryName = $entry.FullName -replace '\\', '/'
                $actualEntries += $entryName

                $filename = $entry.Name
                if ($ExpectedPayloadFiles.ContainsKey($filename)) {
                    $expectedFile = $ExpectedPayloadFiles[$filename]
                    if ($entry.Length -ne $expectedFile.size) { throw "Decompressed payload size mismatch for $filename in $ZipType" }

                    $stream = $entry.Open()
                    $sha256 = [System.Security.Cryptography.SHA256]::Create()
                    $hashBytes = $sha256.ComputeHash($stream)
                    $actualHash = [System.BitConverter]::ToString($hashBytes).Replace('-', '').ToLowerInvariant()
                    $stream.Dispose()

                    if ($actualHash -ne $expectedFile.sha256) { throw "Decompressed payload hash mismatch for $filename in $ZipType" }
                } elseif ($filename -match '^Install-UpdateSupport\.(ps1|cmd)$') {
                    $stream = $entry.Open()
                    $sha256 = [System.Security.Cryptography.SHA256]::Create()
                    $hashBytes = $sha256.ComputeHash($stream)
                    $actualHash = [System.BitConverter]::ToString($hashBytes).Replace('-', '').ToLowerInvariant()
                    $stream.Dispose()

                    if ($filename -eq 'Install-UpdateSupport.ps1' -and $actualHash -ne $installSupportPs1ExpectedHash) { throw "Install-UpdateSupport.ps1 hash mismatch in $ZipType" }
                    if ($filename -eq 'Install-UpdateSupport.cmd' -and $actualHash -ne $installSupportCmdExpectedHash) { throw "Install-UpdateSupport.cmd hash mismatch in $ZipType" }
                }
            }
            $sortedExpected = $ExpectedEntries | Sort-Object
            $sortedActual = $actualEntries | Sort-Object

            if ($sortedExpected.Length -ne $sortedActual.Length) { throw "Zip entry count mismatch in $ZipType." }
            for ($i = 0; $i -lt $sortedExpected.Length; $i++) {
                if ($sortedExpected[$i] -ne $sortedActual[$i]) { throw "Zip entry mismatch in $($ZipType): expected '$($sortedExpected[$i])', found '$($sortedActual[$i])'." }
            }
        } finally {
            $archive.Dispose()
        }
        return @{ size = $zipSize; sha256 = $zipHash }
    }

    $portableEntries = @('PhoneDeck.Server.exe', 'PhoneDeck.ControlCenter.exe', 'PhoneDeck.apk', 'Start-ControlCenter.cmd', 'README.txt', 'data/')
    $enrollEntries = @('PhoneDeck.Server.exe', 'PhoneDeck.ControlCenter.exe', 'PhoneDeck.apk', 'Install-UpdateSupport.ps1', 'Install-UpdateSupport.cmd', 'README.txt')

    $portableStats = Verify-Zip -ZipPath $portableZipPath -ExpectedEntries $portableEntries -ExpectedPayloadFiles $candidateFiles -ZipType 'Portable'
    $enrollStats = Verify-Zip -ZipPath $enrollZipPath -ExpectedEntries $enrollEntries -ExpectedPayloadFiles $candidateFiles -ZipType 'Enrollment'

    $script:PhaseName = 'write-report'
    $packages = @(
        [ordered]@{
            name = 'PhoneDeck-Portable-STAGING.zip'
            size = $portableStats.size
            sha256 = $portableStats.sha256
        },
        [ordered]@{
            name = 'PhoneDeck-Enrollment-STAGING.zip'
            size = $enrollStats.size
            sha256 = $enrollStats.sha256
        }
    )
    Write-PackagesReport -Status 'complete' -Phase '' -ErrorMsg '' -Packages $packages

    $script:FinalStatus = 'complete'
    $script:PhaseExitCode = 0
    Write-Host "Local candidate packages created."
}
catch {
    if ($script:PhaseExitCode -eq 0) { $script:PhaseExitCode = 1 }
    $script:FinalStatus = 'failed'
    $errMsg = $_.Exception.Message
    Write-Host "Packaging failed [$($script:PhaseName)]: $errMsg" -ForegroundColor Red
    if ($null -ne $runDir -and (Test-Path -LiteralPath $runDir)) {
        Write-PackagesReport -Status 'failed' -Phase $script:PhaseName -ErrorMsg $errMsg
    }
}

if ($script:FinalStatus -ne 'complete' -or $script:PhaseExitCode -ne 0) {
    $code = if ($script:PhaseExitCode -ne 0) { [int]$script:PhaseExitCode } else { 1 }
    exit $code
}
exit 0
