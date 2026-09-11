<#
.SYNOPSIS
    B03 S2 read-only release eligibility preflight.

.DESCRIPTION
    Checks candidate report + payload hashes/sizes, channel policy fingerprints,
    APK install cert (apksigner verify --print-certs, single signer), publisher
    public RSA PEM SPKI SHA-256, and history sequence uniqueness/monotonicity.

    Never writes candidate or history, never signs/releases, never reads private keys.
    eligible=true only means materials precheck passed; releasable is always false.
    History sequence consumption is deferred to the real signed release transaction.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [Parameter(Mandatory)][string]$PolicyPath,
    [Parameter(Mandatory)][string]$HistoryPath,
    [Parameter(Mandatory)][string]$Channel,
    [Parameter(Mandatory)][string]$PublisherPublicKeyPath,
    [Parameter(Mandatory)][string]$ApkSignerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "Test-ReleaseEligibility.ps1 requires PowerShell 7+. Current: $($PSVersionTable.PSVersion)"
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path -LiteralPath (Join-Path $ScriptDir '..\..')).Path
. (Join-Path $ScriptDir 'ReleasePolicy.ps1')

function Write-EligibilityJsonAndExit {
    param(
        [Parameter(Mandatory)]$Result,
        [Parameter(Mandatory)][int]$ExitCode
    )
    $json = ($Result | ConvertTo-Json -Depth 10)
    Write-Output $json
    exit $ExitCode
}

$channelId = [string]$Channel
$reasons = New-Object System.Collections.Generic.List[string]
$candidateInfo = $null
$verified = [ordered]@{}
$historyPathFull = $null
$policyPathFull = $null
$candidateDirFull = $null
$pubKeyPathFull = $null
$signerPathFull = $null
$historyBeforeSha = $null
$candidateBeforeSha = $null

try {
    if ([string]::IsNullOrWhiteSpace($channelId)) {
        throw "Channel id is empty."
    }

    $candidateDirFull = Resolve-SafeCandidateDirectory -CandidateDirectory $CandidateDirectory -RepoRoot $RepoRoot
    $policyPathFull = Resolve-ExistingFileLocal -Path $PolicyPath -Label 'PolicyPath' -RepoRoot $RepoRoot
    $pubKeyPathFull = Resolve-ExistingFileLocal -Path $PublisherPublicKeyPath -Label 'PublisherPublicKeyPath' -RepoRoot $RepoRoot
    $signerPathFull = Resolve-ExistingFileLocal -Path $ApkSignerPath -Label 'ApkSignerPath' -RepoRoot $RepoRoot

    # Missing history file is an explicit refusal (empty array file is the only new-channel init).
    if ([string]::IsNullOrWhiteSpace($HistoryPath)) {
        throw "HistoryPath is empty."
    }
    $historyPathFull = if ([System.IO.Path]::IsPathRooted($HistoryPath)) {
        [System.IO.Path]::GetFullPath($HistoryPath)
    } else {
        [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $HistoryPath))
    }
    if (-not (Test-Path -LiteralPath $historyPathFull -PathType Leaf)) {
        throw "HistoryPath file is missing (refuse). Create an explicit schemaVersion=1 document with releases=[] to initialize a channel."
    }
    if (Test-HasReparseOrLinkLocal -PathToCheck $historyPathFull) {
        throw "HistoryPath must not be a symlink/junction/reparse file."
    }

    $candidateJsonPath = Join-Path $candidateDirFull 'candidate.json'
    if (-not (Test-Path -LiteralPath $candidateJsonPath -PathType Leaf)) {
        throw "candidate.json missing under CandidateDirectory."
    }
    $candidateBeforeSha = Get-FileSha256LowerLocal -LiteralPath $candidateJsonPath
    $historyBeforeSha = Get-FileSha256LowerLocal -LiteralPath $historyPathFull

    $policyObj = Read-JsonObjectFile -Path $policyPathFull -Label 'PolicyPath'
    # Must not assign into [string]$Channel (case-insensitive); that coerces OrderedDictionary → string
    # and StrictMode then throws "property 'enabled' cannot be found".
    $channelInfo = Get-PolicyChannel -Policy $policyObj -ChannelId $channelId

    if (-not $channelInfo.enabled) {
        throw "Channel '$channelId' is disabled (enabled=false)."
    }
    if ([string]::IsNullOrEmpty($channelInfo.apkInstallCertSha256)) {
        throw "Channel '$channelId' apkInstallCertSha256 is unconfigured (empty)."
    }
    if ([string]::IsNullOrEmpty($channelInfo.publisherPublicKeySha256)) {
        throw "Channel '$channelId' publisherPublicKeySha256 is unconfigured (empty)."
    }

    $historyObj = Read-JsonObjectFile -Path $historyPathFull -Label 'HistoryPath'
    $historyState = Get-HistorySequenceState -History $historyObj

    $candidateInfo = Assert-CandidateReportForEligibility -CandidateDirectory $candidateDirFull
    if ($candidateInfo.sequence -le [long]$historyState.maxSequence) {
        throw "Candidate sequence $($candidateInfo.sequence) must be greater than history max sequence $($historyState.maxSequence)."
    }

    $apkCertSha = Get-ApkInstallCertSha256FromApksigner -ApkPath $candidateInfo.apkPath -ApkSignerPath $signerPathFull
    if ($apkCertSha -cne $channelInfo.apkInstallCertSha256) {
        throw "APK install cert SHA256 mismatch: actual=$apkCertSha policy=$($channelInfo.apkInstallCertSha256)"
    }

    $publisherSha = Get-PublisherPublicKeySha256FromPem -PemPath $pubKeyPathFull
    if ($publisherSha -cne $channelInfo.publisherPublicKeySha256) {
        throw "Publisher public key SPKI SHA256 mismatch: actual=$publisherSha policy=$($channelInfo.publisherPublicKeySha256)"
    }

    # Immutability check: preflight must not have mutated inputs.
    $candidateAfterSha = Get-FileSha256LowerLocal -LiteralPath $candidateInfo.reportPath
    $historyAfterSha = Get-FileSha256LowerLocal -LiteralPath $historyPathFull
    if ($candidateAfterSha -cne $candidateBeforeSha) {
        throw "Internal error: candidate.json bytes changed during read-only preflight."
    }
    if ($historyAfterSha -cne $historyBeforeSha) {
        throw "Internal error: history bytes changed during read-only preflight."
    }

    $verified = [ordered]@{
        apkInstallCertSha256     = $apkCertSha
        publisherPublicKeySha256 = $publisherSha
        historyMaxSequence       = [long]$historyState.maxSequence
        candidateSequence        = [long]$candidateInfo.sequence
        historyReleaseCount      = [int]$historyState.count
    }

    $result = New-EligibilityResultObject -Eligible $true -Channel $channelId -Reasons @() -Candidate ([ordered]@{
        runId        = $candidateInfo.runId
        sequence     = [long]$candidateInfo.sequence
        sourceCommit = $candidateInfo.sourceCommit
        sourceDirty  = [bool]$candidateInfo.sourceDirty
        note         = 'sourceCommit/sourceDirty are worktree provenance only; not binary build evidence.'
    }) -Verified $verified

    Write-EligibilityJsonAndExit -Result $result -ExitCode 0
}
catch {
    $msg = $_.Exception.Message
    if ([string]::IsNullOrWhiteSpace($msg)) { $msg = 'Release eligibility preflight failed.' }
    [void]$reasons.Add($msg)

    # Best-effort immutability confirmation on failure paths too.
    try {
        if ($candidateBeforeSha -and $candidateDirFull) {
            $cj = Join-Path $candidateDirFull 'candidate.json'
            if (Test-Path -LiteralPath $cj -PathType Leaf) {
                $after = Get-FileSha256LowerLocal -LiteralPath $cj
                if ($after -cne $candidateBeforeSha) {
                    [void]$reasons.Add('candidate.json bytes changed during failed preflight (unexpected).')
                }
            }
        }
        if ($historyBeforeSha -and $historyPathFull -and (Test-Path -LiteralPath $historyPathFull -PathType Leaf)) {
            $afterH = Get-FileSha256LowerLocal -LiteralPath $historyPathFull
            if ($afterH -cne $historyBeforeSha) {
                [void]$reasons.Add('history bytes changed during failed preflight (unexpected).')
            }
        }
    } catch {
        # ignore secondary errors
    }

    $failCandidate = $null
    if ($null -ne $candidateInfo) {
        $failCandidate = [ordered]@{
            runId        = $candidateInfo.runId
            sequence     = [long]$candidateInfo.sequence
            sourceCommit = $candidateInfo.sourceCommit
            sourceDirty  = [bool]$candidateInfo.sourceDirty
            note         = 'sourceCommit/sourceDirty are worktree provenance only; not binary build evidence.'
        }
    }

    # Always emit stable verified (empty object when preflight did not reach fingerprint checks).
    $verifiedOut = if ($verified -is [System.Collections.IDictionary]) { $verified } else { [ordered]@{} }
    $result = New-EligibilityResultObject -Eligible $false -Channel $channelId -Reasons @($reasons) -Candidate $failCandidate -Verified $verifiedOut
    [Console]::Error.WriteLine($msg)
    Write-EligibilityJsonAndExit -Result $result -ExitCode 1
}
