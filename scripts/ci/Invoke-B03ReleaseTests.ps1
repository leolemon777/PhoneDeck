<#
.SYNOPSIS
    CI orchestration for B03 S1/S2/S3 staging release tests (no product EXE/install execution).

.DESCRIPTION
    Resolves same-run Windows/Android artifact basenames, runs S2 puredata, creates one dedicated
    S1 staging candidate under a unique OutputRoot, then runs S1 and S3 suites. Child pwsh exits
    are checked immediately. Logs + bounded report staging are always retained. Never uploads or
    stages TEMP S2 private-key fixture folders, existing signing/, or data/.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WindowsArtifactRoot,
    [Parameter(Mandatory)][string]$AndroidArtifactRoot,
    [Parameter(Mandatory)][string]$Aapt2Path,
    [string]$RepoRoot = '',
    [string]$ReportRoot = 'outputs/ci-b03-reports'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "Invoke-B03ReleaseTests.ps1 requires PowerShell 7+. Current: $($PSVersionTable.PSVersion)"
}
if (-not $IsWindows) {
    throw "Invoke-B03ReleaseTests.ps1 requires Windows."
}

if (-not $RepoRoot) {
    $RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
} else {
    $RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
}

. (Join-Path $RepoRoot 'scripts/release/CandidatePathHelpers.ps1')

function Resolve-UniqueBasename {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$FileName
    )
    if (-not (Test-Path -LiteralPath $Root)) {
        throw "Artifact root missing for '$FileName': $Root"
    }
    $matches = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Filter $FileName -ErrorAction Stop)
    if ($matches.Count -eq 0) {
        throw "Required artifact basename not found: $FileName under $Root"
    }
    if ($matches.Count -gt 1) {
        $listed = ($matches | ForEach-Object { $_.FullName }) -join '; '
        throw "Duplicate artifact basename '$FileName' ($($matches.Count) matches): $listed"
    }
    return $matches[0].FullName
}

function Resolve-RealSdk35Aapt2 {
    param([Parameter(Mandatory)][string]$Requested)
    if ([string]::IsNullOrWhiteSpace($Requested)) {
        throw 'Aapt2Path is empty.'
    }
    $full = if ([System.IO.Path]::IsPathRooted($Requested)) {
        [System.IO.Path]::GetFullPath($Requested)
    } else {
        [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Requested))
    }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "aapt2 not found at: $full"
    }
    $leaf = [System.IO.Path]::GetFileName($full)
    if ($leaf -ne 'aapt2.exe' -and $leaf -ne 'aapt2') {
        throw "Aapt2Path must point at aapt2 binary, got: $leaf"
    }
    # Prefer explicit SDK 35.0.0 path; still accept a real aapt2.exe when the parent folder is 35.0.0.
    $parentName = Split-Path -Leaf (Split-Path -Parent $full)
    if ($parentName -ne '35.0.0') {
        Write-Host "WARNING: aapt2 parent folder is '$parentName' (expected build-tools 35.0.0)."
    }
    # Reject obvious stub/fixture scripts.
    $ext = [System.IO.Path]::GetExtension($full).ToLowerInvariant()
    if ($ext -in @('.cmd', '.bat', '.ps1')) {
        throw "Aapt2Path must be real SDK aapt2, not a script stub: $full"
    }
    return $full
}

function New-DirectorySafe {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
    return (Resolve-Path -LiteralPath $Path).Path
}

function New-UniqueGuidDirectory {
    param(
        [Parameter(Mandatory)][string]$ParentFull,
        [Parameter(Mandatory)][string]$Label
    )
    $guid = [guid]::NewGuid().ToString('N')
    $full = Join-Path $ParentFull $guid
    if (Test-Path -LiteralPath $full) {
        throw "$Label directory already exists (no overwrite/deletion): $full"
    }
    New-Item -ItemType Directory -Path $full -Force:$false | Out-Null
    if (-not (Test-Path -LiteralPath $full -PathType Container)) {
        throw "Failed to create $Label directory: $full"
    }
    return [ordered]@{
        Guid = $guid
        Full = (Resolve-Path -LiteralPath $full).Path
    }
}

function Get-ImmediateChildDirNames {
    param([Parameter(Mandatory)][string]$Root)
    if (-not (Test-Path -LiteralPath $Root)) {
        return [string[]]@()
    }
    return [string[]]@(
        Get-ChildItem -LiteralPath $Root -Directory -ErrorAction Stop |
            ForEach-Object { $_.Name }
    )
}

function Get-NewImmediateChildDirs {
    param(
        [Parameter(Mandatory)][string]$Root,
        [AllowEmptyCollection()][string[]]$BeforeNames
    )
    if (-not (Test-Path -LiteralPath $Root)) {
        return [string[]]@()
    }
    $beforeSet = [System.Collections.Generic.HashSet[string]]::new(
        [string[]]@($BeforeNames),
        [System.StringComparer]::OrdinalIgnoreCase
    )
    $newDirs = [System.Collections.Generic.List[string]]::new()
    foreach ($d in @(Get-ChildItem -LiteralPath $Root -Directory -ErrorAction Stop)) {
        if (-not $beforeSet.Contains($d.Name)) {
            [void]$newDirs.Add($d.FullName)
        }
    }
    return [string[]]@($newDirs.ToArray())
}

function Invoke-CheckedPwshFile {
    param(
        [Parameter(Mandatory)][string]$ScriptPath,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$ArgumentList,
        [Parameter(Mandatory)][string]$LogPath,
        [Parameter(Mandatory)][string]$Label
    )
    if (-not (Test-Path -LiteralPath $ScriptPath -PathType Leaf)) {
        throw "Script not found for ${Label}: $ScriptPath"
    }

    $argLine = if ($null -eq $ArgumentList -or $ArgumentList.Count -eq 0) {
        ''
    } else {
        ($ArgumentList | ForEach-Object {
                if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
            }) -join ' '
    }
    Write-Host "=== $Label ==="
    Write-Host "pwsh -NoProfile -NonInteractive -File `"$ScriptPath`" $argLine"

    $pinfo = New-Object System.Diagnostics.ProcessStartInfo
    $pinfo.FileName = 'pwsh'
    $pinfo.UseShellExecute = $false
    $pinfo.RedirectStandardOutput = $true
    $pinfo.RedirectStandardError = $true
    $pinfo.CreateNoWindow = $true
    $pinfo.WorkingDirectory = $RepoRoot
    [void]$pinfo.ArgumentList.Add('-NoProfile')
    [void]$pinfo.ArgumentList.Add('-NonInteractive')
    [void]$pinfo.ArgumentList.Add('-File')
    [void]$pinfo.ArgumentList.Add($ScriptPath)
    if ($null -ne $ArgumentList) {
        foreach ($a in $ArgumentList) {
            [void]$pinfo.ArgumentList.Add($a)
        }
    }

    $p = New-Object System.Diagnostics.Process
    $p.StartInfo = $pinfo
    [void]$p.Start()
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    $p.WaitForExit()
    $stdout = [string]$outTask.Result
    $stderr = [string]$errTask.Result
    $code = [int]$p.ExitCode
    $p.Dispose()

    $logDir = Split-Path -Parent $LogPath
    New-DirectorySafe -Path $logDir | Out-Null
    $banner = @(
        "label=$Label"
        "script=$ScriptPath"
        "exitCode=$code"
        "utc=$((Get-Date).ToUniversalTime().ToString('o'))"
        '----- STDOUT -----'
        $stdout
        '----- STDERR -----'
        $stderr
        "----- END exitCode=$code -----"
    ) -join [Environment]::NewLine
    [System.IO.File]::WriteAllText($LogPath, $banner + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

    Write-Host $stdout
    if (-not [string]::IsNullOrWhiteSpace($stderr)) {
        Write-Host $stderr -ForegroundColor Yellow
    }
    Write-Host "=== $Label exitCode=$code (log: $LogPath) ==="

    if ($code -ne 0) {
        throw "$Label failed immediately with exitCode=$code. See log: $LogPath"
    }

    return [ordered]@{
        Label    = $Label
        ExitCode = $code
        LogPath  = $LogPath
        Stdout   = $stdout
        Stderr   = $stderr
    }
}

function Resolve-CompleteCandidateFromRoot {
    param([Parameter(Mandatory)][string]$OutputRootFull)
    if (-not (Test-Path -LiteralPath $OutputRootFull)) {
        throw "Dedicated candidate OutputRoot missing: $OutputRootFull"
    }
    $jsonFiles = @(Get-ChildItem -LiteralPath $OutputRootFull -Recurse -File -Filter 'candidate.json' -ErrorAction Stop)
    $complete = @()
    foreach ($jf in $jsonFiles) {
        try {
            $obj = Get-Content -LiteralPath $jf.FullName -Raw -Encoding utf8 | ConvertFrom-Json
        } catch {
            continue
        }
        if ($null -eq $obj) { continue }
        $statusOk = ($obj.status -eq 'complete')
        $modeOk = ($obj.mode -eq 'staging')
        $relOk = ($obj.releasable -is [bool] -and $obj.releasable -eq $false)
        if ($statusOk -and $modeOk -and $relOk) {
            $complete += [ordered]@{
                JsonPath = $jf.FullName
                RunDir   = $jf.Directory.FullName
                RunId    = [string]$obj.runId
            }
        }
    }
    if ($complete.Count -eq 0) {
        throw "No complete staging candidate.json (releasable=false) under dedicated OutputRoot: $OutputRootFull"
    }
    if ($complete.Count -gt 1) {
        $listed = ($complete | ForEach-Object { $_.JsonPath }) -join '; '
        throw "Expected exactly one complete candidate.json under dedicated OutputRoot, found $($complete.Count): $listed"
    }
    return $complete[0]
}

function Copy-BoundedJsonTree {
    param(
        [AllowEmptyCollection()][string[]]$SourceRoots,
        [Parameter(Mandatory)][string]$DestRoot,
        [string[]]$NamePatterns = @('candidate.json', 'packages.json', 'policy.json', 'history.json', '*.json')
    )
    $copied = 0
    if ($null -eq $SourceRoots -or $SourceRoots.Count -eq 0) {
        return $copied
    }
    foreach ($SourceRoot in $SourceRoots) {
        if ([string]::IsNullOrWhiteSpace($SourceRoot) -or -not (Test-Path -LiteralPath $SourceRoot)) {
            continue
        }
        $rootLeaf = Split-Path -Leaf $SourceRoot
        $files = @(Get-ChildItem -LiteralPath $SourceRoot -Recurse -File -ErrorAction SilentlyContinue)
        foreach ($f in $files) {
            $name = $f.Name
            # Hard excludes: keys, binaries, payloads, existing signing/data names.
            if ($name -match '(?i)private\.pem$|private-key|BEGIN RSA PRIVATE|BEGIN PRIVATE') { continue }
            if ($name -match '(?i)\.(exe|apk|aab|dll|pdb|zip|pem|key|keystore|jks)$') {
                # Staging ZIPs handled separately; skip other binaries/keys here.
                if ($name -notmatch '(?i)^PhoneDeck-(Portable|Enrollment)-STAGING\.zip$') {
                    continue
                }
            }
            $rel = [System.IO.Path]::GetRelativePath($SourceRoot, $f.FullName)
            if ($rel -match '(?i)(^|[/\\])(payload|signing|data)([/\\]|$)') { continue }
            if ($rel -match '(?i)publisher-private') { continue }

            $allow = $false
            foreach ($pat in $NamePatterns) {
                if ($name -like $pat) { $allow = $true; break }
            }
            if (-not $allow) { continue }

            $dest = Join-Path $DestRoot (Join-Path $rootLeaf $rel)
            $destDir = Split-Path -Parent $dest
            New-DirectorySafe -Path $destDir | Out-Null
            Copy-Item -LiteralPath $f.FullName -Destination $dest -Force
            $copied++
        }
    }
    return $copied
}

function Copy-IntendedStagingOutputs {
    param(
        [AllowEmptyCollection()][string[]]$InvocationRoots,
        [Parameter(Mandatory)][string]$DestRoot,
        [Parameter(Mandatory)][string]$CandidateRunId
    )
    $copied = 0
    if ($null -eq $InvocationRoots -or $InvocationRoots.Count -eq 0) {
        return $copied
    }
    if ([string]::IsNullOrWhiteSpace($CandidateRunId)) {
        return $copied
    }

    $matches = [System.Collections.Generic.List[object]]::new()
    foreach ($root in $InvocationRoots) {
        if ([string]::IsNullOrWhiteSpace($root) -or -not (Test-Path -LiteralPath $root)) {
            continue
        }
        $pkgJsons = @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter 'packages.json' -ErrorAction SilentlyContinue |
                Sort-Object -Property FullName)
        foreach ($pj in $pkgJsons) {
            try {
                $obj = Get-Content -LiteralPath $pj.FullName -Raw -Encoding utf8 | ConvertFrom-Json
            } catch {
                continue
            }
            if ($null -eq $obj) { continue }
            if ($obj.status -ne 'complete' -or $obj.mode -ne 'staging') { continue }
            if (-not ($obj.releasable -is [bool] -and $obj.releasable -eq $false)) { continue }

            $pkgCandRunId = $null
            try {
                if ($null -ne $obj.candidate) {
                    $pkgCandRunId = [string]$obj.candidate.runId
                }
            } catch {
                $pkgCandRunId = $null
            }
            if ([string]::IsNullOrWhiteSpace($pkgCandRunId)) { continue }
            if (-not $pkgCandRunId.Equals($CandidateRunId, [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $runDir = $pj.Directory.FullName
            $portable = Join-Path $runDir 'PhoneDeck-Portable-STAGING.zip'
            $enroll = Join-Path $runDir 'PhoneDeck-Enrollment-STAGING.zip'
            if (-not ((Test-Path -LiteralPath $portable) -and (Test-Path -LiteralPath $enroll))) {
                continue
            }
            [void]$matches.Add([ordered]@{
                    PackagesJson = $pj.FullName
                    RunDir       = $runDir
                    DirName      = $pj.Directory.Name
                    Portable     = $portable
                    Enroll       = $enroll
                })
        }
    }

    if ($matches.Count -eq 0) {
        return 0
    }

    # One intended complete staging package set matched to the dedicated candidate runId.
    $chosen = $matches[0]
    $destRun = Join-Path $DestRoot $chosen.DirName
    New-DirectorySafe -Path $destRun | Out-Null
    Copy-Item -LiteralPath $chosen.PackagesJson -Destination (Join-Path $destRun 'packages.json') -Force
    Copy-Item -LiteralPath $chosen.Portable -Destination (Join-Path $destRun 'PhoneDeck-Portable-STAGING.zip') -Force
    Copy-Item -LiteralPath $chosen.Enroll -Destination (Join-Path $destRun 'PhoneDeck-Enrollment-STAGING.zip') -Force
    return 3
}

function Invoke-BoundedReportStaging {
    param(
        [Parameter(Mandatory)][string]$ReportFull,
        [AllowEmptyCollection()][string[]]$S2Roots,
        [AllowEmptyCollection()][string[]]$S1Roots,
        [AllowEmptyCollection()][string[]]$S3Roots,
        [string]$CandidateRunId = ''
    )
    $nS2 = Copy-BoundedJsonTree -SourceRoots $S2Roots -DestRoot (Join-Path $ReportFull 's2-json') -NamePatterns @('candidate.json', 'policy.json', 'history.json', '*.json')
    $nS1 = Copy-BoundedJsonTree -SourceRoots $S1Roots -DestRoot (Join-Path $ReportFull 's1-json') -NamePatterns @('candidate.json')
    $nS3json = Copy-BoundedJsonTree -SourceRoots $S3Roots -DestRoot (Join-Path $ReportFull 's3-json') -NamePatterns @('packages.json', 'candidate.json')
    $nStaging = 0
    if (-not [string]::IsNullOrWhiteSpace($CandidateRunId)) {
        $nStaging = Copy-IntendedStagingOutputs -InvocationRoots $S3Roots -DestRoot (Join-Path $ReportFull 's3-staging-outputs') -CandidateRunId $CandidateRunId
    }
    return [ordered]@{
        s2JsonFiles            = $nS2
        s1JsonFiles            = $nS1
        s3JsonFiles            = $nS3json
        s3StagingOutputFiles   = $nStaging
        s2Roots                = @($S2Roots)
        s1Roots                = @($S1Roots)
        s3Roots                = @($S3Roots)
        matchedCandidateRunId  = $CandidateRunId
        excludedTempS2Fixtures = 'TEMP/PhoneDeck-B03-S2-policy-* (private key fixtures) not copied'
        excludedPayloadBins    = 'payload/*.exe|*.apk and repeated fixture binaries not copied'
        excludedHistorical     = 'Only immediate-child dirs created by this invocation are staged'
    }
}

# --- prepare report/log roots (always retained; validated under outputs/; GUID never overwrites) ---
$reportBaseFull = Resolve-SafeCandidateOutputRoot -RequestedOutputRoot $ReportRoot -RepoRoot $RepoRoot
New-DirectorySafe -Path $reportBaseFull | Out-Null
$reportDir = New-UniqueGuidDirectory -ParentFull $reportBaseFull -Label 'ReportRoot'
$runStamp = [string]$reportDir.Guid
$reportFull = [string]$reportDir.Full
$logsDir = Join-Path $reportFull 'logs'
New-DirectorySafe -Path $logsDir | Out-Null

$summary = [ordered]@{
    schemaVersion = 1
    mode          = 'staging'
    releasable    = $false
    runStamp      = $runStamp
    startedUtc    = (Get-Date).ToUniversalTime().ToString('o')
    steps         = [ordered]@{}
    artifacts     = [ordered]@{}
    candidate     = $null
    ok            = $false
    error         = $null
}

$summaryPath = Join-Path $reportFull 'summary.json'
$failed = $false
$failMessage = $null
$stagingError = $null

$ciCandidateParentRel = 'outputs/ci-b03-candidates'
$ciCandidateParentFull = Resolve-SafeCandidateOutputRoot -RequestedOutputRoot $ciCandidateParentRel -RepoRoot $RepoRoot
New-DirectorySafe -Path $ciCandidateParentFull | Out-Null
$ciCandidateDir = New-UniqueGuidDirectory -ParentFull $ciCandidateParentFull -Label 'ci-b03-candidates'
$ciCandidateOutputRootRel = "$ciCandidateParentRel/$($ciCandidateDir.Guid)"
$ciCandidateOutputRootFull = [string]$ciCandidateDir.Full

$trackedS2Roots = [string[]]@()
$trackedS1Roots = [string[]]@()
$trackedS3Roots = [string[]]@()
$dedicatedCandidateRunId = ''

$s2SuiteRoot = Join-Path $RepoRoot 'outputs/release-policy-tests'
$s1SuiteRoot = Join-Path $RepoRoot 'outputs/candidate-tests'
$s3SuiteRoot = Join-Path $RepoRoot 'outputs/local-package-tests'

try {
    $winRoot = (Resolve-Path -LiteralPath $WindowsArtifactRoot).Path
    $andRoot = (Resolve-Path -LiteralPath $AndroidArtifactRoot).Path
    $aapt2 = Resolve-RealSdk35Aapt2 -Requested $Aapt2Path

    $server = Resolve-UniqueBasename -Root $winRoot -FileName 'PhoneDeck.Server.exe'
    $console = Resolve-UniqueBasename -Root $winRoot -FileName 'PhoneDeck.ControlCenter.exe'
    $apk = Resolve-UniqueBasename -Root $andRoot -FileName 'PhoneDeck-debug.apk'

    $summary.artifacts['server'] = $server
    $summary.artifacts['controlCenter'] = $console
    $summary.artifacts['apk'] = $apk
    $summary.artifacts['aapt2'] = $aapt2
    Write-Host "Resolved Server:         $server"
    Write-Host "Resolved ControlCenter:  $console"
    Write-Host "Resolved APK:            $apk"
    Write-Host "Resolved aapt2:          $aapt2"

    # 1) S2 puredata (no product binaries; ArgumentList intentionally empty)
    $s2Before = Get-ImmediateChildDirNames -Root $s2SuiteRoot
    $s2Script = Join-Path $RepoRoot 'scripts/release/tests/Test-ReleasePolicy.ps1'
    try {
        $s2 = Invoke-CheckedPwshFile -ScriptPath $s2Script -ArgumentList @() -LogPath (Join-Path $logsDir 's2-release-policy.log') -Label 'S2 Test-ReleasePolicy'
    }
    finally {
        $trackedS2Roots = @(Get-NewImmediateChildDirs -Root $s2SuiteRoot -BeforeNames $s2Before)
    }
    $summary.steps['s2'] = [ordered]@{
        exitCode     = $s2.ExitCode
        log          = 'logs/s2-release-policy.log'
        newChildDirs = @($trackedS2Roots)
    }
    Write-Host ("S2 new immediate child dirs ({0}): {1}" -f $trackedS2Roots.Count, (($trackedS2Roots | ForEach-Object { Split-Path -Leaf $_ }) -join ', '))

    # 2) Dedicated S1 staging candidate for S3 input
    $newCandScript = Join-Path $RepoRoot 'scripts/release/New-CandidateRun.ps1'
    $newArgs = @(
        '-Server', $server,
        '-ControlCenter', $console,
        '-Apk', $apk,
        '-Aapt2Path', $aapt2,
        '-OutputRoot', $ciCandidateOutputRootRel
    )
    $newCand = Invoke-CheckedPwshFile -ScriptPath $newCandScript -ArgumentList $newArgs -LogPath (Join-Path $logsDir 'new-candidate-run.log') -Label 'New-CandidateRun (dedicated S1 staging)'
    $summary.steps['newCandidate'] = [ordered]@{ exitCode = $newCand.ExitCode; log = 'logs/new-candidate-run.log'; outputRoot = $ciCandidateOutputRootRel }

    $candidate = Resolve-CompleteCandidateFromRoot -OutputRootFull $ciCandidateOutputRootFull
    $dedicatedCandidateRunId = [string]$candidate.RunId
    $summary.candidate = [ordered]@{
        runId      = $candidate.RunId
        runDir     = $candidate.RunDir
        jsonPath   = $candidate.JsonPath
        mode       = 'staging'
        releasable = $false
        status     = 'complete'
    }
    Write-Host "Dedicated complete candidate: $($candidate.RunDir)"

    # Stage candidate.json only (not repeated payload binaries already present as CI artifacts).
    $candReportDir = Join-Path $reportFull 'ci-candidate'
    New-DirectorySafe -Path $candReportDir | Out-Null
    Copy-Item -LiteralPath $candidate.JsonPath -Destination (Join-Path $candReportDir 'candidate.json') -Force

    # 3) S1 control-flow suite (7 cases)
    $s1Before = Get-ImmediateChildDirNames -Root $s1SuiteRoot
    $s1Script = Join-Path $RepoRoot 'scripts/release/tests/Test-CandidateRun.ps1'
    $s1Args = @(
        '-Server', $server,
        '-ControlCenter', $console,
        '-Apk', $apk,
        '-Aapt2Path', $aapt2
    )
    try {
        $s1 = Invoke-CheckedPwshFile -ScriptPath $s1Script -ArgumentList $s1Args -LogPath (Join-Path $logsDir 's1-candidate-run.log') -Label 'S1 Test-CandidateRun'
    }
    finally {
        $trackedS1Roots = @(Get-NewImmediateChildDirs -Root $s1SuiteRoot -BeforeNames $s1Before)
    }
    $summary.steps['s1'] = [ordered]@{
        exitCode     = $s1.ExitCode
        log          = 'logs/s1-candidate-run.log'
        newChildDirs = @($trackedS1Roots)
    }
    Write-Host ("S1 new immediate child dirs ({0}): {1}" -f $trackedS1Roots.Count, (($trackedS1Roots | ForEach-Object { Split-Path -Leaf $_ }) -join ', '))

    # 4) S3 suite against dedicated complete candidate (9 cases)
    $s3Before = Get-ImmediateChildDirNames -Root $s3SuiteRoot
    $s3Script = Join-Path $RepoRoot 'scripts/release/tests/Test-LocalCandidatePackages.ps1'
    $s3Args = @(
        '-CandidateDirectory', $candidate.RunDir,
        '-Aapt2Path', $aapt2
    )
    try {
        $s3 = Invoke-CheckedPwshFile -ScriptPath $s3Script -ArgumentList $s3Args -LogPath (Join-Path $logsDir 's3-local-packages.log') -Label 'S3 Test-LocalCandidatePackages'
    }
    finally {
        $trackedS3Roots = @(Get-NewImmediateChildDirs -Root $s3SuiteRoot -BeforeNames $s3Before)
    }
    $summary.steps['s3'] = [ordered]@{
        exitCode     = $s3.ExitCode
        log          = 'logs/s3-local-packages.log'
        newChildDirs = @($trackedS3Roots)
    }
    Write-Host ("S3 new immediate child dirs ({0}): {1}" -f $trackedS3Roots.Count, (($trackedS3Roots | ForEach-Object { Split-Path -Leaf $_ }) -join ', '))

    $summary.ok = $true
}
catch {
    $failed = $true
    $failMessage = $_.Exception.Message
    $summary.ok = $false
    $summary.error = $failMessage
    Write-Host "B03 release tests orchestration failed: $failMessage" -ForegroundColor Red
}
finally {
    # Always stage this invocation's JSON/reports (including after child failure). Do not mask the original error.
    try {
        $staging = Invoke-BoundedReportStaging `
            -ReportFull $reportFull `
            -S2Roots $trackedS2Roots `
            -S1Roots $trackedS1Roots `
            -S3Roots $trackedS3Roots `
            -CandidateRunId $dedicatedCandidateRunId
        $summary.steps['reportStaging'] = $staging
    }
    catch {
        $stagingError = $_.Exception.Message
        Write-Host "Report staging secondary failure (original error preserved): $stagingError" -ForegroundColor Yellow
        $summary.steps['reportStaging'] = [ordered]@{
            ok            = $false
            stagingError  = $stagingError
            s2Roots       = @($trackedS2Roots)
            s1Roots       = @($trackedS1Roots)
            s3Roots       = @($trackedS3Roots)
        }
        if (-not $failed) {
            $failed = $true
            $failMessage = "Report staging failed: $stagingError"
            $summary.ok = $false
            $summary.error = $failMessage
        }
        else {
            $summary['stagingError'] = $stagingError
        }
    }

    $summary['finishedUtc'] = (Get-Date).ToUniversalTime().ToString('o')
    $summaryJson = ($summary | ConvertTo-Json -Depth 8)
    [System.IO.File]::WriteAllText($summaryPath, $summaryJson + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
    Write-Host "Report root: $reportFull"
    Write-Host "Summary:     $summaryPath"

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
        "report_directory=$reportFull" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
        "ci_candidate_output_root=$ciCandidateOutputRootFull" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
        if ($null -ne $summary.candidate -and -not [string]::IsNullOrWhiteSpace([string]$summary.candidate['runDir'])) {
            "candidate_directory=$($summary.candidate['runDir'])" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
        }
    }
}

if ($failed) {
    Write-Host "EXIT DIAGNOSTIC: failed=true message=$failMessage report=$reportFull" -ForegroundColor Red
    exit 1
}

Write-Host "B03 S1/S2/S3 staging release tests completed (releasable=false)."
exit 0
