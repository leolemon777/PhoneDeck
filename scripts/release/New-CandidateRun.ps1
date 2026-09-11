<#
.SYNOPSIS
    B03 S1: stage a non-releasable development candidate directory with an auditable report.

.DESCRIPTION
    Copies Server / ControlCenter / APK into a new GUID run under OutputRoot (default
    outputs/candidates), validates with B02 Assert-ReleaseVersions + Assert-PackageVersions
    via checked child pwsh, verifies SHA256 source↔staged match, and writes candidate.json.

    Mode is staging only. releasable is always false. No signing, ZIP, install, device,
    remote, build, restore, or private-key access.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Server,
    [Parameter(Mandatory)][string]$ControlCenter,
    [Parameter(Mandatory)][string]$Apk,
    [string]$OutputRoot = 'outputs/candidates',
    [string]$Aapt2Path = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "New-CandidateRun.ps1 requires PowerShell 7+. Current: $($PSVersionTable.PSVersion)"
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path -LiteralPath (Join-Path $ScriptDir '..\..')).Path
. (Join-Path $ScriptDir 'CandidatePathHelpers.ps1')

$script:PhaseName = 'init'
$script:PhaseExitCode = 1
$script:RunId = $null
$script:RunDir = $null
$script:CandidateJsonPath = $null
$script:ReportInitialized = $false
$script:CreatedUtc = $null
$script:SourceCommit = $null
$script:SourceDirty = $null
$script:VersionsSnapshot = $null
$script:FinalStatus = 'failed'
$script:ErrorPhase = $null
$script:ErrorMessage = $null
$script:RedactPaths = [System.Collections.Generic.List[string]]::new()
$script:ReportFiles = $null
$script:PayloadShaMatched = $false
$script:ReleaseVersionsOk = $false
$script:PackageVersionsOk = $false
$script:ProductVersions = $null
$script:FileVersions = $null
$script:OriginalFilenames = $null
$script:RepoParent = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot '..'))
$script:DescriptorSha256 = $null

function Get-RepoRelativePath {
    param([Parameter(Mandatory)][string]$FullPath)
    $rel = [System.IO.Path]::GetRelativePath($RepoRoot, $FullPath)
    return ($rel -replace '\\', '/')
}

function Resolve-ExistingFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Label
    )
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Label path is empty."
    }
    $full = if ([System.IO.Path]::IsPathRooted($Path)) {
        [System.IO.Path]::GetFullPath($Path)
    } else {
        [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
    }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "$Label file not found."
    }
    if (Test-HasReparseOrLink -PathToCheck $full) {
        throw "$Label must not be a symlink/junction/reparse file."
    }
    return $full
}

function Get-GitWorktreeProvenance {
    $commitOut = & git -C $RepoRoot rev-parse HEAD 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to read sourceCommit from git worktree."
    }
    $commit = [string]$commitOut
    if ([string]::IsNullOrWhiteSpace($commit)) {
        throw "git rev-parse HEAD returned empty sourceCommit."
    }

    $statusOut = & git -C $RepoRoot status --porcelain 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to read sourceDirty from git status."
    }
    # Clean `git status --porcelain` emits no pipeline objects (AutomationNull).
    # [string]$null is $null, so (.Trim()) throws; normalize before Trim.
    $statusText = if ($null -eq $statusOut) { '' } else { [string]$statusOut }
    $dirty = -not [string]::IsNullOrWhiteSpace($statusText.Trim())
    return [ordered]@{
        sourceCommit = $commit.Trim()
        sourceDirty  = [bool]$dirty
    }
}

function Get-VersionsSnapshot {
    param([Parameter(Mandatory)][string]$DescriptorPath)
    $raw = Get-Content -Raw -LiteralPath $DescriptorPath
    $obj = $raw | ConvertFrom-Json
    # Explicit ordered snapshot (descriptor SSOT). Not binary build evidence.
    $macos = [ordered]@{
        version            = [string]$obj.macos.version
        bundleShortVersion = [string]$obj.macos.bundleShortVersion
        bundleVersion      = [string]$obj.macos.bundleVersion
    }
    if ($null -ne $obj.macos.historicBundleMapping) {
        $h = $obj.macos.historicBundleMapping
        $macos['historicBundleMapping'] = [ordered]@{
            healthVersion      = [string]$h.healthVersion
            bundleShortVersion = [string]$h.bundleShortVersion
            bundleVersion      = [string]$h.bundleVersion
            note               = [string]$h.note
        }
    }
    return [ordered]@{
        schemaVersion = [int]$obj.schemaVersion
        windows       = [ordered]@{
            version  = [string]$obj.windows.version
            sequence = [long]$obj.windows.sequence
        }
        android       = [ordered]@{
            versionName = [string]$obj.android.versionName
            versionCode = [long]$obj.android.versionCode
        }
        macos         = $macos
        console       = [ordered]@{
            follows              = [string]$obj.console.follows
            informationalVersion = [string]$obj.console.informationalVersion
        }
    }
}

function New-CandidateReportObject {
    param(
        [Parameter(Mandatory)][string]$Status,
        [object]$Files = $null,
        [object]$Validation = $null,
        [string]$ErrorPhase = $null,
        [string]$ErrorMessage = $null
    )

    $report = [ordered]@{
        schemaVersion = 1
        mode          = 'staging'
        releasable    = $false
        status        = $Status
        runId         = $(if ($script:RunId) { $script:RunId } else { '' })
        createdUtc    = $(if ($script:CreatedUtc) { $script:CreatedUtc } else { '' })
        sourceCommit  = $(if ($null -ne $script:SourceCommit) { $script:SourceCommit } else { '' })
        sourceDirty   = $(if ($null -ne $script:SourceDirty) { [bool]$script:SourceDirty } else { $false })
        versions      = $(if ($null -ne $script:VersionsSnapshot) { $script:VersionsSnapshot } else { [ordered]@{} })
        files         = @()
        validation    = [ordered]@{}
    }

    if ($null -ne $Files) {
        $report.files = @($Files)
    }
    if ($null -ne $Validation) {
        $report.validation = $Validation
    }
    if ($Status -eq 'failed') {
        $report['error'] = [ordered]@{
            phase   = $(if ($ErrorPhase) { $ErrorPhase } else { $script:PhaseName })
            message = $(if ($ErrorMessage) { $ErrorMessage } else { 'failed' })
        }
    }
    return $report
}

function Write-CandidateReport {
    param(
        [Parameter(Mandatory)][string]$Status,
        [object]$Files = $null,
        [object]$Validation = $null,
        [string]$ErrorPhase = $null,
        [string]$ErrorMessage = $null
    )
    if (-not $script:CandidateJsonPath) { return }
    $report = New-CandidateReportObject -Status $Status -Files $Files -Validation $Validation -ErrorPhase $ErrorPhase -ErrorMessage $ErrorMessage
    $json = ($report | ConvertTo-Json -Depth 10) + [Environment]::NewLine
    Write-AtomicTextFile -Path $script:CandidateJsonPath -Content $json -SafeAncestorStopAt $script:RepoParent
    $script:ReportInitialized = $true
}

function Invoke-CheckedPwshFile {
    param(
        [Parameter(Mandatory)][string]$ScriptPath,
        [Parameter(Mandatory)][string]$StepName,
        [Parameter()][string[]]$ArgumentList = @()
    )
    if (-not (Test-Path -LiteralPath $ScriptPath -PathType Leaf)) {
        throw "Missing validator script for ${StepName}."
    }
    $args = @('-NoProfile', '-NonInteractive', '-File', $ScriptPath) + $ArgumentList
    & pwsh @args
    $code = $LASTEXITCODE
    if ($null -eq $code) { $code = 0 }
    if ($code -ne 0) {
        $script:PhaseExitCode = [int]$code
        throw "${StepName} failed with exit code ${code}."
    }
}

function Get-PeVersionFields {
    param([Parameter(Mandatory)][string]$ExePath)
    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($ExePath)
    return [ordered]@{
        productVersion   = [string]$info.ProductVersion
        fileVersion      = [string]$info.FileVersion
        originalFilename = [string]$info.OriginalFilename
    }
}

function Add-RedactPath {
    param([string]$Path)
    if (-not [string]::IsNullOrWhiteSpace($Path)) {
        $script:RedactPaths.Add([System.IO.Path]::GetFullPath($Path))
    }
}

function Protect-ReportMessage {
    param([Parameter(Mandatory)][string]$Message)
    $text = $Message
    # Longest paths first so nested prefixes redact cleanly (case-insensitive on Windows).
    $paths = @($script:RedactPaths | Sort-Object { $_.Length } -Descending)
    foreach ($p in $paths) {
        if ([string]::IsNullOrWhiteSpace($p)) { continue }
        $variants = @(
            $p,
            ($p -replace '\\', '/'),
            ($p -replace '/', '\'),
            ($p -replace '^\\\\\?\\', '')
        ) | Select-Object -Unique
        foreach ($variant in $variants) {
            if ([string]::IsNullOrWhiteSpace($variant)) { continue }
            $text = [regex]::Replace($text, [regex]::Escape($variant), '<path>', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        }
    }
    # Never embed signing/data tree hints from accidental messages.
    $text = [regex]::Replace($text, '(?i)\bdata[\\/]+signing\b', '<redacted>')
    return $text
}

function Resolve-OptionalToolPath {
    param(
        [string]$Path,
        [Parameter(Mandatory)][string]$Label
    )
    if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
    $full = if ([System.IO.Path]::IsPathRooted($Path)) {
        [System.IO.Path]::GetFullPath($Path)
    } else {
        [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
    }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "$Label file not found."
    }
    if (Test-HasReparseOrLink -PathToCheck $full) {
        throw "$Label must not be a symlink/junction/reparse file."
    }
    return $full
}

try {
    Add-RedactPath -Path $RepoRoot

    $script:PhaseName = 'resolve-inputs'
    $serverFull = Resolve-ExistingFile -Path $Server -Label 'Server'
    $ccFull = Resolve-ExistingFile -Path $ControlCenter -Label 'ControlCenter'
    $apkFull = Resolve-ExistingFile -Path $Apk -Label 'Apk'
    Add-RedactPath -Path $serverFull
    Add-RedactPath -Path $ccFull
    Add-RedactPath -Path $apkFull

    $script:PhaseName = 'output-safety'
    $outFull = Resolve-SafeCandidateOutputRoot -RequestedOutputRoot $OutputRoot -RepoRoot $RepoRoot
    Add-RedactPath -Path $outFull

    $descriptorPath = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot 'work\phone-deck\release-versions.json'))
    Add-RedactPath -Path $descriptorPath
    if (-not (Test-Path -LiteralPath $descriptorPath -PathType Leaf)) {
        throw "release-versions.json descriptor not found."
    }

    $script:PhaseName = 'source-version-validate'
    $assertRelease = Join-Path $RepoRoot 'scripts\versioning\Assert-ReleaseVersions.ps1'
    Invoke-CheckedPwshFile -ScriptPath $assertRelease -StepName 'Assert-ReleaseVersions' -ArgumentList @(
        '-DescriptorPath', $descriptorPath
    )
    $script:ReleaseVersionsOk = $true

    $script:PhaseName = 'provenance'
    $prov = Get-GitWorktreeProvenance
    $script:SourceCommit = $prov.sourceCommit
    $script:SourceDirty = $prov.sourceDirty
    $script:VersionsSnapshot = Get-VersionsSnapshot -DescriptorPath $descriptorPath
    $script:DescriptorSha256 = Get-FileSha256Lower -LiteralPath $descriptorPath
    $script:CreatedUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')

    $script:PhaseName = 'hash-sources'
    $sourceHashes = [ordered]@{
        Server         = Get-FileSha256Lower -LiteralPath $serverFull
        ControlCenter  = Get-FileSha256Lower -LiteralPath $ccFull
        Apk            = Get-FileSha256Lower -LiteralPath $apkFull
    }
    $sourceSizes = [ordered]@{
        Server         = [long](Get-Item -LiteralPath $serverFull).Length
        ControlCenter  = [long](Get-Item -LiteralPath $ccFull).Length
        Apk            = [long](Get-Item -LiteralPath $apkFull).Length
    }

    $script:PhaseName = 'create-run'
    $script:RunId = [Guid]::NewGuid().ToString('N')
    $script:RunDir = Join-Path $outFull $script:RunId
    if (Test-Path -LiteralPath $script:RunDir) {
        throw "Candidate run directory already exists; refusing overwrite."
    }
    $payloadDir = Join-Path $script:RunDir 'payload'
    [System.IO.Directory]::CreateDirectory($payloadDir) | Out-Null
    Add-RedactPath -Path $script:RunDir
    Assert-AncestorChainSafe -TargetPath $script:RunDir -StopAtPath ([System.IO.Path]::GetFullPath((Join-Path $RepoRoot '..')))
    $script:CandidateJsonPath = Join-Path $script:RunDir 'candidate.json'
    Write-CandidateReport -Status 'running' -Validation ([ordered]@{
        releaseVersions = 'ok'
        packageVersions = 'pending'
        payloadSha256MatchedSource = $false
        note = 'sourceCommit is worktree provenance only; not binary build evidence. See productVersions after staging.'
    })
    Write-Host "Candidate runId: $($script:RunId)"
    Write-Host "Candidate dir:   $(Get-RepoRelativePath -FullPath $script:RunDir)"

    $staged = [ordered]@{
        Server        = Join-Path $payloadDir 'PhoneDeck.Server.exe'
        ControlCenter = Join-Path $payloadDir 'PhoneDeck.ControlCenter.exe'
        Apk           = Join-Path $payloadDir 'PhoneDeck.apk'
    }

    $script:PhaseName = 'copy-payload'
    foreach ($key in @('Server', 'ControlCenter', 'Apk')) {
        $dst = $staged[$key]
        if (Test-Path -LiteralPath $dst) {
            throw "Refusing to overwrite existing staged file: payload/$([System.IO.Path]::GetFileName($dst))"
        }
    }
    Assert-AncestorChainSafe -TargetPath $payloadDir -StopAtPath $script:RepoParent
    [System.IO.File]::Copy($serverFull, $staged.Server, $false)
    [System.IO.File]::Copy($ccFull, $staged.ControlCenter, $false)
    [System.IO.File]::Copy($apkFull, $staged.Apk, $false)
    Assert-TreeHasNoReparse -RootPath $script:RunDir

    $script:PhaseName = 'hash-staged'
    $stagedHashes = [ordered]@{
        Server        = Get-FileSha256Lower -LiteralPath $staged.Server
        ControlCenter = Get-FileSha256Lower -LiteralPath $staged.ControlCenter
        Apk           = Get-FileSha256Lower -LiteralPath $staged.Apk
    }
    $stagedSizes = [ordered]@{
        Server        = [long](Get-Item -LiteralPath $staged.Server).Length
        ControlCenter = [long](Get-Item -LiteralPath $staged.ControlCenter).Length
        Apk           = [long](Get-Item -LiteralPath $staged.Apk).Length
    }

    foreach ($key in @('Server', 'ControlCenter', 'Apk')) {
        if ($stagedHashes[$key] -ne $sourceHashes[$key]) {
            throw "Staged $key SHA256 does not match source hash recorded before copy."
        }
        if ($stagedSizes[$key] -ne $sourceSizes[$key]) {
            throw "Staged $key size does not match source size recorded before copy."
        }
    }

    # Upstream mutation after pre-copy hash must fail.
    $rehashSources = [ordered]@{
        Server        = Get-FileSha256Lower -LiteralPath $serverFull
        ControlCenter = Get-FileSha256Lower -LiteralPath $ccFull
        Apk           = Get-FileSha256Lower -LiteralPath $apkFull
    }
    foreach ($key in @('Server', 'ControlCenter', 'Apk')) {
        if ($rehashSources[$key] -ne $sourceHashes[$key]) {
            throw "Source $key changed during staging (upstream mutation)."
        }
    }

    $script:PayloadShaMatched = $true
    $script:ReportFiles = @(
        [ordered]@{
            name   = 'PhoneDeck.Server.exe'
            path   = 'payload/PhoneDeck.Server.exe'
            size   = $stagedSizes.Server
            sha256 = $stagedHashes.Server
        },
        [ordered]@{
            name   = 'PhoneDeck.ControlCenter.exe'
            path   = 'payload/PhoneDeck.ControlCenter.exe'
            size   = $stagedSizes.ControlCenter
            sha256 = $stagedHashes.ControlCenter
        },
        [ordered]@{
            name   = 'PhoneDeck.apk'
            path   = 'payload/PhoneDeck.apk'
            size   = $stagedSizes.Apk
            sha256 = $stagedHashes.Apk
        }
    )

    $script:PhaseName = 'package-version-validate'
    $descriptorShaNow = Get-FileSha256Lower -LiteralPath $descriptorPath
    if ($descriptorShaNow -ne $script:DescriptorSha256) {
        throw "release-versions.json changed during staging (descriptor mutation)."
    }
    $assertPackage = Join-Path $RepoRoot 'scripts\packaging\Assert-PackageVersions.ps1'
    $pkgArgs = @(
        '-Server', $staged.Server,
        '-ControlCenter', $staged.ControlCenter,
        '-Apk', $staged.Apk,
        '-DescriptorPath', $descriptorPath
    )
    $resolvedAapt2 = Resolve-OptionalToolPath -Path $Aapt2Path -Label 'Aapt2Path'
    if (-not [string]::IsNullOrWhiteSpace($resolvedAapt2)) {
        Add-RedactPath -Path $resolvedAapt2
        $pkgArgs += @('-Aapt2Path', $resolvedAapt2)
    }
    Assert-AncestorChainSafe -TargetPath $script:RunDir -StopAtPath $script:RepoParent
    Invoke-CheckedPwshFile -ScriptPath $assertPackage -StepName 'Assert-PackageVersions' -ArgumentList $pkgArgs
    $script:PackageVersionsOk = $true

    $script:PhaseName = 'read-pe-versions'
    $serverPe = Get-PeVersionFields -ExePath $staged.Server
    $ccPe = Get-PeVersionFields -ExePath $staged.ControlCenter
    $script:ProductVersions = [ordered]@{
        server        = $serverPe.productVersion
        controlCenter = $ccPe.productVersion
    }
    $script:FileVersions = [ordered]@{
        server        = $serverPe.fileVersion
        controlCenter = $ccPe.fileVersion
    }
    $script:OriginalFilenames = [ordered]@{
        server        = $serverPe.originalFilename
        controlCenter = $ccPe.originalFilename
    }

    $validation = [ordered]@{
        releaseVersions            = 'ok'
        packageVersions            = 'ok'
        payloadSha256MatchedSource = $true
        # PE ProductVersion is recorded separately from sourceCommit (worktree only).
        productVersions            = $script:ProductVersions
        fileVersions               = $script:FileVersions
        originalFilenames          = $script:OriginalFilenames
        note = 'sourceCommit/sourceDirty describe the staging worktree only; they are not proof the binaries were built from that commit. Use productVersions for embedded PE identity.'
    }

    $script:PhaseName = 'complete'
    Write-CandidateReport -Status 'complete' -Files $script:ReportFiles -Validation $validation
    $script:FinalStatus = 'complete'
    $script:PhaseExitCode = 0
    Write-Host "Candidate staging complete (releasable=false)."
}
catch {
    if ($script:PhaseExitCode -eq 0) { $script:PhaseExitCode = 1 }
    $script:FinalStatus = 'failed'
    $script:ErrorPhase = $script:PhaseName
    $script:ErrorMessage = Protect-ReportMessage -Message $_.Exception.Message
    Write-Host "Candidate staging failed [$($script:PhaseName)]: $($_.Exception.Message)" -ForegroundColor Red
    if ($script:CandidateJsonPath -and $script:RunDir -and (Test-Path -LiteralPath $script:RunDir)) {
        try {
            $failValidation = [ordered]@{
                releaseVersions            = $(if ($script:ReleaseVersionsOk) { 'ok' } else { 'failed-or-skipped' })
                packageVersions            = $(if ($script:PackageVersionsOk) { 'ok' } else { 'failed-or-skipped' })
                payloadSha256MatchedSource = [bool]$script:PayloadShaMatched
                note                       = 'sourceCommit is worktree provenance only; not binary build evidence.'
            }
            if ($null -ne $script:ProductVersions) {
                $failValidation['productVersions'] = $script:ProductVersions
            }
            if ($null -ne $script:FileVersions) {
                $failValidation['fileVersions'] = $script:FileVersions
            }
            if ($null -ne $script:OriginalFilenames) {
                $failValidation['originalFilenames'] = $script:OriginalFilenames
            }
            Write-CandidateReport -Status 'failed' -Files $script:ReportFiles -Validation $failValidation -ErrorPhase $script:ErrorPhase -ErrorMessage $script:ErrorMessage
        } catch {
            Write-Host "Failed to write candidate.json failure report: $($_.Exception.Message)" -ForegroundColor Red
            if ($script:PhaseExitCode -eq 0) { $script:PhaseExitCode = 1 }
        }
    }
}
finally {
    # Never delete candidate runs or other outputs. Failed dirs are retained for inspection.
}

if ($script:FinalStatus -ne 'complete' -or $script:PhaseExitCode -ne 0) {
    $code = if ($script:PhaseExitCode -ne 0) { [int]$script:PhaseExitCode } else { 1 }
    exit $code
}

exit 0
