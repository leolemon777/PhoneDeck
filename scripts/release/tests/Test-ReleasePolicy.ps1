<#
.SYNOPSIS
    Pure-data tests for B03 S2 release-policy preflight.

.DESCRIPTION
    Uses temporary generated PUBLIC RSA PEM fixtures and stub apksigner scripts.
    Does not read existing signing materials or invent production approved fingerprints.
    Fixtures are retained (no recursive delete). No product build/restore.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "Test-ReleasePolicy.ps1 requires PowerShell 7+. Current: $($PSVersionTable.PSVersion)"
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ReleaseDir = (Resolve-Path -LiteralPath (Join-Path $ScriptDir '..')).Path
$RepoRoot = (Resolve-Path -LiteralPath (Join-Path $ReleaseDir '..\..')).Path
$EligibilityScript = Join-Path $ReleaseDir 'Test-ReleaseEligibility.ps1'
$ApkCertScript = Join-Path $ReleaseDir 'Get-ApkInstallCertSha256.ps1'
$ExamplePolicy = Join-Path $ReleaseDir 'release-policy.example.json'
$HelperLib = Join-Path $ReleaseDir 'ReleasePolicy.ps1'

. $HelperLib

$passed = 0
$failed = 0
$fixtureRoots = New-Object System.Collections.Generic.List[string]

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        Write-Host "FAIL: $Message" -ForegroundColor Red
        $script:failed++
    } else {
        Write-Host "PASS: $Message" -ForegroundColor Green
        $script:passed++
    }
}

function Write-Utf8NoBom {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Content)
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Content, $utf8)
}

function New-FixtureRoot {
    $root = Join-Path $env:TEMP ("PhoneDeck-B03-S2-policy-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    [void]$script:fixtureRoots.Add($root)
    Write-Host "FIXTURE: $root"
    return $root
}

function New-RepoCandidateRoot {
    $root = Join-Path $RepoRoot ("outputs\release-policy-tests\" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    [void]$script:fixtureRoots.Add($root)
    return $root
}

function Get-FileSha256Lower {
    param([Parameter(Mandatory)][string]$LiteralPath)
    return (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256).Hash.ToLowerInvariant()
}

function New-TempPublicRsaPem {
    param([Parameter(Mandatory)][string]$Directory)
    $rsa = [System.Security.Cryptography.RSA]::Create(2048)
    try {
        $spki = $rsa.ExportSubjectPublicKeyInfo()
        $sha = [System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::HashData($spki)).Replace('-', '').ToLowerInvariant()
        $pem = $rsa.ExportSubjectPublicKeyInfoPem()
        $privPem = $rsa.ExportPkcs8PrivateKeyPem()
        $pubPath = Join-Path $Directory 'publisher-public.pem'
        $privPath = Join-Path $Directory 'publisher-private.pem'
        Write-Utf8NoBom -Path $pubPath -Content $pem
        Write-Utf8NoBom -Path $privPath -Content $privPem
        return [ordered]@{
            PublicPath  = $pubPath
            PrivatePath = $privPath
            SpkiSha256  = $sha
        }
    } finally {
        $rsa.Dispose()
    }
}

function New-StubApkSigner {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$CertSha256Hex,
        [int]$SignerCount = 1,
        [int]$ExitCode = 0
    )
    $colon = ($CertSha256Hex.ToUpperInvariant() -replace '(.{2})', '$1:').TrimEnd(':')
    $lines = New-Object System.Collections.Generic.List[string]
    for ($i = 1; $i -le $SignerCount; $i++) {
        [void]$lines.Add("Signer #$i certificate DN: CN=Fixture$i")
        [void]$lines.Add("Signer #$i certificate SHA-256 digest: $colon")
        [void]$lines.Add("Signer #$i certificate SHA-1 digest: AA:BB:CC:DD")
    }
    $body = ($lines -join "`r`n") + "`r`n"
    $cmdPath = Join-Path $Directory 'apksigner-stub.cmd'
    # cmd.exe echo can mangle some chars; write via PowerShell wrapper bat that prints fixed text.
    $ps1Path = Join-Path $Directory 'apksigner-stub-impl.ps1'
    $impl = @"
param([Parameter(ValueFromRemainingArguments=`$true)]`$Rest)
Write-Output @'
$body
'@
exit $ExitCode
"@
    Write-Utf8NoBom -Path $ps1Path -Content $impl
    $cmd = @"
@echo off
pwsh -NoProfile -NonInteractive -File "%~dp0apksigner-stub-impl.ps1" %*
exit /b %ERRORLEVEL%
"@
    Write-Utf8NoBom -Path $cmdPath -Content $cmd
    return $cmdPath
}

function New-StubApkSignerStderrFlood {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [int]$ExitCode = 2,
        [int]$StderrBytes = 131072
    )
    # Isolated executable stub: flood stderr past the OS pipe buffer while leaving
    # stdout open until process exit. Reproduces parent sequential ReadToEnd deadlock.
    $cmdPath = Join-Path $Directory 'apksigner-stub.cmd'
    $ps1Path = Join-Path $Directory 'apksigner-stub-impl.ps1'
    $impl = @"
param([Parameter(ValueFromRemainingArguments=`$true)]`$Rest)
`$chunk = 'E' * 4096
`$written = 0
while (`$written -lt $StderrBytes) {
    [Console]::Error.Write(`$chunk)
    `$written += `$chunk.Length
}
exit $ExitCode
"@
    Write-Utf8NoBom -Path $ps1Path -Content $impl
    $cmd = @"
@echo off
pwsh -NoProfile -NonInteractive -File "%~dp0apksigner-stub-impl.ps1" %*
exit /b %ERRORLEVEL%
"@
    Write-Utf8NoBom -Path $cmdPath -Content $cmd
    return $cmdPath
}

function New-CandidateFixture {
    param(
        [Parameter(Mandatory)][string]$RunDir,
        [long]$Sequence = 24,
        [bool]$SourceDirty = $false,
        [string]$Status = 'complete',
        [string]$Mode = 'staging',
        [bool]$Releasable = $false,
        [switch]$TamperApkHash
    )
    $payload = Join-Path $RunDir 'payload'
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    $server = Join-Path $payload 'PhoneDeck.Server.exe'
    $cc = Join-Path $payload 'PhoneDeck.ControlCenter.exe'
    $apk = Join-Path $payload 'PhoneDeck.apk'
    [System.IO.File]::WriteAllBytes($server, [byte[]](1..32))
    [System.IO.File]::WriteAllBytes($cc, [byte[]](33..64))
    [System.IO.File]::WriteAllBytes($apk, [byte[]](65..120))

    $files = @(
        [ordered]@{
            name = 'PhoneDeck.Server.exe'
            path = 'payload/PhoneDeck.Server.exe'
            size = [long](Get-Item -LiteralPath $server).Length
            sha256 = Get-FileSha256Lower $server
        },
        [ordered]@{
            name = 'PhoneDeck.ControlCenter.exe'
            path = 'payload/PhoneDeck.ControlCenter.exe'
            size = [long](Get-Item -LiteralPath $cc).Length
            sha256 = Get-FileSha256Lower $cc
        },
        [ordered]@{
            name = 'PhoneDeck.apk'
            path = 'payload/PhoneDeck.apk'
            size = [long](Get-Item -LiteralPath $apk).Length
            sha256 = Get-FileSha256Lower $apk
        }
    )
    if ($TamperApkHash) {
        $files[2].sha256 = ('0' * 64)
    }

    $report = [ordered]@{
        schemaVersion = 1
        mode          = $Mode
        releasable    = $Releasable
        status        = $Status
        runId         = [guid]::NewGuid().ToString('N')
        createdUtc    = '2026-09-11T00:00:00Z'
        sourceCommit  = 'd1c089c2e1c8a0cf0565fffa7319ba059b95b348'
        sourceDirty   = $SourceDirty
        versions      = [ordered]@{
            schemaVersion = 1
            windows       = [ordered]@{ version = '1.6.0-dev.11'; sequence = $Sequence }
            android       = [ordered]@{ versionName = '1.6.0-dev.17'; versionCode = $Sequence }
            macos         = [ordered]@{ version = '2.0.0-dev.3'; bundleShortVersion = '2.0.0'; bundleVersion = '2' }
            console       = [ordered]@{ follows = 'windows.version'; informationalVersion = '1.6.0-dev.11' }
        }
        files         = $files
        validation    = [ordered]@{ note = 'fixture' }
    }
    $reportPath = Join-Path $RunDir 'candidate.json'
    Write-Utf8NoBom -Path $reportPath -Content (($report | ConvertTo-Json -Depth 10) + "`n")
    return [ordered]@{
        RunDir     = $RunDir
        ReportPath = $reportPath
        ApkPath    = $apk
        ReportSha  = Get-FileSha256Lower $reportPath
    }
}

function New-PolicyFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ChannelId,
        [bool]$Enabled = $true,
        [string]$ApkCertSha256 = '',
        [string]$PublisherSha256 = ''
    )
    $obj = [ordered]@{
        schemaVersion = 1
        channels      = @(
            [ordered]@{
                id                         = $ChannelId
                enabled                    = $Enabled
                apkInstallCertSha256       = $ApkCertSha256
                publisherPublicKeySha256   = $PublisherSha256
            }
        )
    }
    Write-Utf8NoBom -Path $Path -Content (($obj | ConvertTo-Json -Depth 8) + "`n")
}

function New-HistoryFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [object[]]$Sequences = @(),
        [string]$RawJson = $null
    )
    # [string]$RawJson = $null is coerced to "" by PowerShell; treat blank as unset.
    if (-not [string]::IsNullOrWhiteSpace($RawJson)) {
        Write-Utf8NoBom -Path $Path -Content $RawJson
        return
    }
    if ($null -eq $Sequences -or @($Sequences).Count -eq 0) {
        $emptyHistory = @'
{
  "schemaVersion": 1,
  "releases": []
}
'@
        Write-Utf8NoBom -Path $Path -Content ($emptyHistory + "`n")
        return
    }
    $releases = @()
    foreach ($s in @($Sequences)) {
        $releases += [ordered]@{ sequence = [int]$s; note = 'fixture' }
    }
    $obj = [ordered]@{
        schemaVersion = 1
        releases      = @($releases)
    }
    Write-Utf8NoBom -Path $Path -Content (($obj | ConvertTo-Json -Depth 8) + "`n")
}

function Invoke-Eligibility {
    param(
        [Parameter(Mandatory)][string]$CandidateDirectory,
        [Parameter(Mandatory)][string]$PolicyPath,
        [Parameter(Mandatory)][string]$HistoryPath,
        [Parameter(Mandatory)][string]$Channel,
        [Parameter(Mandatory)][string]$PublisherPublicKeyPath,
        [Parameter(Mandatory)][string]$ApkSignerPath
    )
    $argsList = @(
        '-NoProfile', '-NonInteractive', '-File', $EligibilityScript,
        '-CandidateDirectory', $CandidateDirectory,
        '-PolicyPath', $PolicyPath,
        '-HistoryPath', $HistoryPath,
        '-Channel', $Channel,
        '-PublisherPublicKeyPath', $PublisherPublicKeyPath,
        '-ApkSignerPath', $ApkSignerPath
    )
    $pinfo = New-Object System.Diagnostics.ProcessStartInfo
    $pinfo.FileName = 'pwsh'
    $pinfo.UseShellExecute = $false
    $pinfo.RedirectStandardOutput = $true
    $pinfo.RedirectStandardError = $true
    $pinfo.CreateNoWindow = $true
    foreach ($a in $argsList) { [void]$pinfo.ArgumentList.Add($a) }
    $p = New-Object System.Diagnostics.Process
    $p.StartInfo = $pinfo
    [void]$p.Start()
    $stdout = $p.StandardOutput.ReadToEnd()
    $stderr = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    $code = $p.ExitCode
    $p.Dispose()

    $jsonObj = $null
    try {
        # Eligibility JSON is written to stdout; take the last JSON object-looking chunk.
        $trimmed = $stdout.Trim()
        if ($trimmed.StartsWith('{')) {
            $jsonObj = $trimmed | ConvertFrom-Json
        } else {
            $idx = $trimmed.LastIndexOf('{')
            if ($idx -ge 0) {
                $jsonObj = $trimmed.Substring($idx) | ConvertFrom-Json
            }
        }
    } catch {
        $jsonObj = $null
    }

    return [ordered]@{
        ExitCode = $code
        Stdout   = $stdout
        Stderr   = $stderr
        Json     = $jsonObj
    }
}

function Invoke-ApkCertScript {
    param(
        [Parameter(Mandatory)][string]$Apk,
        [Parameter(Mandatory)][string]$ApkSignerPath
    )
    $argsList = @(
        '-NoProfile', '-NonInteractive', '-File', $ApkCertScript,
        '-Apk', $Apk,
        '-ApkSignerPath', $ApkSignerPath
    )
    $pinfo = New-Object System.Diagnostics.ProcessStartInfo
    $pinfo.FileName = 'pwsh'
    $pinfo.UseShellExecute = $false
    $pinfo.RedirectStandardOutput = $true
    $pinfo.RedirectStandardError = $true
    $pinfo.CreateNoWindow = $true
    foreach ($a in $argsList) { [void]$pinfo.ArgumentList.Add($a) }
    $p = New-Object System.Diagnostics.Process
    $p.StartInfo = $pinfo
    [void]$p.Start()
    $stdout = $p.StandardOutput.ReadToEnd()
    $stderr = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    $code = $p.ExitCode
    $p.Dispose()
    return [ordered]@{ ExitCode = $code; Stdout = $stdout.Trim(); Stderr = $stderr }
}

function Invoke-ApkCertScriptBounded {
    param(
        [Parameter(Mandatory)][string]$Apk,
        [Parameter(Mandatory)][string]$ApkSignerPath,
        [int]$TimeoutMs = 20000
    )
    $argsList = @(
        '-NoProfile', '-NonInteractive', '-File', $ApkCertScript,
        '-Apk', $Apk,
        '-ApkSignerPath', $ApkSignerPath
    )
    $pinfo = New-Object System.Diagnostics.ProcessStartInfo
    $pinfo.FileName = 'pwsh'
    $pinfo.UseShellExecute = $false
    $pinfo.RedirectStandardOutput = $true
    $pinfo.RedirectStandardError = $true
    $pinfo.CreateNoWindow = $true
    foreach ($a in $argsList) { [void]$pinfo.ArgumentList.Add($a) }
    $p = New-Object System.Diagnostics.Process
    $p.StartInfo = $pinfo
    [void]$p.Start()
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    $finished = $p.WaitForExit($TimeoutMs)
    if (-not $finished) {
        try { $p.Kill($true) } catch { }
        try { [void]$outTask.Wait(1000) } catch { }
        try { [void]$errTask.Wait(1000) } catch { }
        $p.Dispose()
        return [ordered]@{ TimedOut = $true; ExitCode = -1; Stdout = ''; Stderr = '' }
    }
    $stdout = $outTask.Result
    $stderr = $errTask.Result
    $code = $p.ExitCode
    $p.Dispose()
    return [ordered]@{ TimedOut = $false; ExitCode = $code; Stdout = $stdout.Trim(); Stderr = $stderr }
}

# --- static example policy ---
Write-Host "`n=== example policy contract ==="
Assert-True (Test-Path -LiteralPath $ExamplePolicy -PathType Leaf) 'release-policy.example.json exists'
$example = Get-Content -Raw -LiteralPath $ExamplePolicy | ConvertFrom-Json
Assert-True ($example.schemaVersion -eq 1) 'example schemaVersion=1'
Assert-True (@($example.channels).Count -ge 1) 'example has channels'
$exCh = @($example.channels)[0]
Assert-True ($exCh.enabled -eq $false) 'example channel enabled=false'
Assert-True ([string]::IsNullOrEmpty([string]$exCh.apkInstallCertSha256)) 'example apkInstallCertSha256 empty (no invented fingerprint)'
Assert-True ([string]::IsNullOrEmpty([string]$exCh.publisherPublicKeySha256)) 'example publisherPublicKeySha256 empty (no invented fingerprint)'

# --- shared materials for dynamic tests ---
$fx = New-FixtureRoot
$key = New-TempPublicRsaPem -Directory $fx
$fixtureCert = 'aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899'
$stubOk = New-StubApkSigner -Directory $fx -CertSha256Hex $fixtureCert -SignerCount 1 -ExitCode 0
New-Item -ItemType Directory -Path (Join-Path $fx 'multi') -Force | Out-Null
$stubMulti = New-StubApkSigner -Directory (Join-Path $fx 'multi') -CertSha256Hex $fixtureCert -SignerCount 2 -ExitCode 0
New-Item -ItemType Directory -Path (Join-Path $fx 'fail') -Force | Out-Null
$stubFail = New-StubApkSigner -Directory (Join-Path $fx 'fail') -CertSha256Hex $fixtureCert -SignerCount 1 -ExitCode 2
New-Item -ItemType Directory -Path (Join-Path $fx 'other-key') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $fx 'other-cert') -Force | Out-Null

# --- Get-ApkInstallCertSha256 ---
Write-Host "`n=== Get-ApkInstallCertSha256 ==="
$candRoot = New-RepoCandidateRoot
$cand = New-CandidateFixture -RunDir (Join-Path $candRoot 'run-cert') -Sequence 24
$certRes = Invoke-ApkCertScript -Apk $cand.ApkPath -ApkSignerPath $stubOk
Assert-True ($certRes.ExitCode -eq 0) "apksigner stub cert read exit 0 (got $($certRes.ExitCode))"
Assert-True ($certRes.Stdout -ceq $fixtureCert) "normalized full SHA256 matches fixture (got '$($certRes.Stdout)')"

$certMulti = Invoke-ApkCertScript -Apk $cand.ApkPath -ApkSignerPath $stubMulti
Assert-True ($certMulti.ExitCode -ne 0) 'multi-signer stub refused nonzero'
Assert-True ($certMulti.Stderr -match 'single signer|exactly one signer') "multi-signer reason present: $($certMulti.Stderr)"

$certFail = Invoke-ApkCertScript -Apk $cand.ApkPath -ApkSignerPath $stubFail
Assert-True ($certFail.ExitCode -ne 0) 'apksigner failure exit nonzero'

# Isolated regression: stderr > pipe buffer while stdout remains open must not deadlock.
Write-Host "`n=== apksigner stderr-pipe deadlock regression ==="
New-Item -ItemType Directory -Path (Join-Path $fx 'stderr-flood') -Force | Out-Null
$stubFlood = New-StubApkSignerStderrFlood -Directory (Join-Path $fx 'stderr-flood') -ExitCode 2 -StderrBytes 131072
$floodRes = Invoke-ApkCertScriptBounded -Apk $cand.ApkPath -ApkSignerPath $stubFlood -TimeoutMs 20000
Assert-True (-not $floodRes.TimedOut) 'stderr-flood stub completes within bounded timeout (no deadlock)'
Assert-True ($floodRes.ExitCode -ne 0) "stderr-flood stub nonzero refusal (got $($floodRes.ExitCode))"
Assert-True (($floodRes.Stderr + $floodRes.Stdout) -match 'exit code\s*2|failed with exit code') "stderr-flood retains exit diagnostics: $($floodRes.Stderr)"

# --- eligible happy path ---
Write-Host "`n=== eligible preflight (releasable forced false) ==="
$happyDir = New-RepoCandidateRoot
$happyCand = New-CandidateFixture -RunDir (Join-Path $happyDir 'run-ok') -Sequence 24
$policyPath = Join-Path $fx 'policy-ok.json'
$historyPath = Join-Path $fx 'history-empty.json'
New-PolicyFile -Path $policyPath -ChannelId 'dev' -Enabled $true -ApkCertSha256 $fixtureCert -PublisherSha256 $key.SpkiSha256
New-HistoryFile -Path $historyPath -Sequences @()
$histBefore = Get-FileSha256Lower $historyPath
$candBefore = Get-FileSha256Lower $happyCand.ReportPath

$ok = Invoke-Eligibility -CandidateDirectory $happyCand.RunDir -PolicyPath $policyPath -HistoryPath $historyPath -Channel 'dev' -PublisherPublicKeyPath $key.PublicPath -ApkSignerPath $stubOk
Assert-True ($ok.ExitCode -eq 0) "eligible exit 0 (got $($ok.ExitCode); stderr=$($ok.Stderr))"
Assert-True ($null -ne $ok.Json) 'eligible emits JSON'
Assert-True ($ok.Json.eligible -eq $true) 'eligible=true'
Assert-True ($ok.Json.releasable -eq $false) 'releasable=false even when eligible'
# StrictMode-safe nested reads: still assert real verified fields (no skip / invented diagnostics).
$okVerified = $null
if ($null -ne $ok.Json -and $null -ne $ok.Json.PSObject.Properties['verified']) {
    $okVerified = $ok.Json.verified
}
Assert-True ($null -ne $okVerified) 'eligible JSON includes verified'
Assert-True ($null -ne $okVerified -and $okVerified.candidateSequence -eq 24) 'verified candidateSequence=24'
Assert-True ($null -ne $okVerified -and $okVerified.historyMaxSequence -eq 0) 'verified historyMaxSequence=0 for empty releases'
Assert-True ($null -ne $okVerified -and $okVerified.apkInstallCertSha256 -ceq $fixtureCert) 'verified apk cert'
Assert-True ($null -ne $okVerified -and $okVerified.publisherPublicKeySha256 -ceq $key.SpkiSha256) 'verified publisher SPKI sha'
Assert-True ((Get-FileSha256Lower $historyPath) -ceq $histBefore) 'history bytes unchanged after eligible'
Assert-True ((Get-FileSha256Lower $happyCand.ReportPath) -ceq $candBefore) 'candidate.json bytes unchanged after eligible'

# history with prior sequences
$historyPrior = Join-Path $fx 'history-prior.json'
New-HistoryFile -Path $historyPrior -Sequences @(10, 20, 23)
$ok2 = Invoke-Eligibility -CandidateDirectory $happyCand.RunDir -PolicyPath $policyPath -HistoryPath $historyPrior -Channel 'dev' -PublisherPublicKeyPath $key.PublicPath -ApkSignerPath $stubOk
Assert-True ($ok2.ExitCode -eq 0) 'eligible with prior history max=23'
$ok2Verified = $null
if ($null -ne $ok2.Json -and $null -ne $ok2.Json.PSObject.Properties['verified']) {
    $ok2Verified = $ok2.Json.verified
}
Assert-True ($null -ne $ok2Verified -and $ok2Verified.historyMaxSequence -eq 23) 'historyMaxSequence=23'

# --- refusal cases ---
Write-Host "`n=== refusal cases ==="

# missing history
$missingHist = Join-Path $fx ("missing-history-" + [guid]::NewGuid().ToString('N') + '.json')
$r = Invoke-Eligibility -CandidateDirectory $happyCand.RunDir -PolicyPath $policyPath -HistoryPath $missingHist -Channel 'dev' -PublisherPublicKeyPath $key.PublicPath -ApkSignerPath $stubOk
Assert-True ($r.ExitCode -ne 0) 'missing history nonzero'
Assert-True ($r.Json.eligible -eq $false) 'missing history eligible=false'
Assert-True ($r.Json.releasable -eq $false) 'missing history releasable=false'
Assert-True (($r.Json.reasons -join ' ') -match 'HistoryPath file is missing') 'missing history reason'
Assert-True ($null -ne $r.Json.PSObject.Properties['verified']) 'failure JSON includes stable verified field'
Assert-True ($null -ne $r.Json.PSObject.Properties['candidate']) 'failure JSON includes stable candidate field'

# disabled channel
$polDisabled = Join-Path $fx 'policy-disabled.json'
New-PolicyFile -Path $polDisabled -ChannelId 'dev' -Enabled $false -ApkCertSha256 $fixtureCert -PublisherSha256 $key.SpkiSha256
$r = Invoke-Eligibility -CandidateDirectory $happyCand.RunDir -PolicyPath $polDisabled -HistoryPath $historyPath -Channel 'dev' -PublisherPublicKeyPath $key.PublicPath -ApkSignerPath $stubOk
Assert-True ($r.ExitCode -ne 0) 'disabled channel nonzero'
Assert-True (($r.Json.reasons -join ' ') -match 'disabled') 'disabled reason'

# unconfigured fingerprints
$polEmpty = Join-Path $fx 'policy-empty.json'
New-PolicyFile -Path $polEmpty -ChannelId 'dev' -Enabled $true -ApkCertSha256 '' -PublisherSha256 $key.SpkiSha256
$r = Invoke-Eligibility -CandidateDirectory $happyCand.RunDir -PolicyPath $polEmpty -HistoryPath $historyPath -Channel 'dev' -PublisherPublicKeyPath $key.PublicPath -ApkSignerPath $stubOk
Assert-True ($r.ExitCode -ne 0) 'unconfigured apk fingerprint nonzero'
Assert-True (($r.Json.reasons -join ' ') -match 'unconfigured') 'unconfigured reason'

# dirty candidate
$dirtyRoot = New-RepoCandidateRoot
$dirtyCand = New-CandidateFixture -RunDir (Join-Path $dirtyRoot 'run-dirty') -Sequence 24 -SourceDirty $true
$dirtyBefore = Get-FileSha256Lower $dirtyCand.ReportPath
$r = Invoke-Eligibility -CandidateDirectory $dirtyCand.RunDir -PolicyPath $policyPath -HistoryPath $historyPath -Channel 'dev' -PublisherPublicKeyPath $key.PublicPath -ApkSignerPath $stubOk
Assert-True ($r.ExitCode -ne 0) 'sourceDirty nonzero'
Assert-True (($r.Json.reasons -join ' ') -match 'sourceDirty') 'sourceDirty reason'
Assert-True ((Get-FileSha256Lower $dirtyCand.ReportPath) -ceq $dirtyBefore) 'dirty candidate bytes unchanged'

# hash mismatch / tampered report
$tamperRoot = New-RepoCandidateRoot
$tamperCand = New-CandidateFixture -RunDir (Join-Path $tamperRoot 'run-tamper') -Sequence 24 -TamperApkHash
$r = Invoke-Eligibility -CandidateDirectory $tamperCand.RunDir -PolicyPath $policyPath -HistoryPath $historyPath -Channel 'dev' -PublisherPublicKeyPath $key.PublicPath -ApkSignerPath $stubOk
Assert-True ($r.ExitCode -ne 0) 'tampered apk hash nonzero'
Assert-True (($r.Json.reasons -join ' ') -match 'sha256 mismatch') 'tampered hash reason'

# path escape outside outputs
$escapeDir = Join-Path $fx 'escape-candidate'
New-Item -ItemType Directory -Path $escapeDir -Force | Out-Null
$escapeCand = New-CandidateFixture -RunDir $escapeDir -Sequence 24
$r = Invoke-Eligibility -CandidateDirectory $escapeCand.RunDir -PolicyPath $policyPath -HistoryPath $historyPath -Channel 'dev' -PublisherPublicKeyPath $key.PublicPath -ApkSignerPath $stubOk
Assert-True ($r.ExitCode -ne 0) 'path escape nonzero'
Assert-True (($r.Json.reasons -join ' ') -match 'outputs|path escape') 'path escape reason'

# private key refused
$r = Invoke-Eligibility -CandidateDirectory $happyCand.RunDir -PolicyPath $policyPath -HistoryPath $historyPath -Channel 'dev' -PublisherPublicKeyPath $key.PrivatePath -ApkSignerPath $stubOk
Assert-True ($r.ExitCode -ne 0) 'private key PEM nonzero'
Assert-True (($r.Json.reasons -join ' ') -match 'Private key') 'private key reason'

# publisher mismatch
$otherKey = New-TempPublicRsaPem -Directory (Join-Path $fx 'other-key')
$r = Invoke-Eligibility -CandidateDirectory $happyCand.RunDir -PolicyPath $policyPath -HistoryPath $historyPath -Channel 'dev' -PublisherPublicKeyPath $otherKey.PublicPath -ApkSignerPath $stubOk
Assert-True ($r.ExitCode -ne 0) 'publisher mismatch nonzero'
Assert-True (($r.Json.reasons -join ' ') -match 'Publisher public key SPKI SHA256 mismatch') 'publisher mismatch reason'

# apk cert mismatch via different stub digest
$otherCert = 'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff'
$stubOther = New-StubApkSigner -Directory (Join-Path $fx 'other-cert') -CertSha256Hex $otherCert
$r = Invoke-Eligibility -CandidateDirectory $happyCand.RunDir -PolicyPath $policyPath -HistoryPath $historyPath -Channel 'dev' -PublisherPublicKeyPath $key.PublicPath -ApkSignerPath $stubOther
Assert-True ($r.ExitCode -ne 0) 'apk cert mismatch nonzero'
Assert-True (($r.Json.reasons -join ' ') -match 'APK install cert SHA256 mismatch') 'apk cert mismatch reason'

# sequence not greater than max
$lowRoot = New-RepoCandidateRoot
$lowCand = New-CandidateFixture -RunDir (Join-Path $lowRoot 'run-low') -Sequence 23
$r = Invoke-Eligibility -CandidateDirectory $lowCand.RunDir -PolicyPath $policyPath -HistoryPath $historyPrior -Channel 'dev' -PublisherPublicKeyPath $key.PublicPath -ApkSignerPath $stubOk
Assert-True ($r.ExitCode -ne 0) 'non-monotonic candidate sequence nonzero'
Assert-True (($r.Json.reasons -join ' ') -match 'must be greater than history max') 'non-monotonic reason'

# malformed history: releases as object (must not accept via @() coercion)
$histObjReleases = Join-Path $fx 'history-object-releases.json'
New-HistoryFile -Path $histObjReleases -RawJson (@'
{
  "schemaVersion": 1,
  "releases": { "sequence": 1, "note": "not-array" }
}
'@)
$r = Invoke-Eligibility -CandidateDirectory $happyCand.RunDir -PolicyPath $policyPath -HistoryPath $histObjReleases -Channel 'dev' -PublisherPublicKeyPath $key.PublicPath -ApkSignerPath $stubOk
Assert-True ($r.ExitCode -ne 0) 'history releases object nonzero'
Assert-True (($r.Json.reasons -join ' ') -match 'JSON array, not an object') 'history releases object reason'

# malformed history: releases as string
$histStrReleases = Join-Path $fx 'history-string-releases.json'
New-HistoryFile -Path $histStrReleases -RawJson (@'
{
  "schemaVersion": 1,
  "releases": "[]"
}
'@)
$r = Invoke-Eligibility -CandidateDirectory $happyCand.RunDir -PolicyPath $policyPath -HistoryPath $histStrReleases -Channel 'dev' -PublisherPublicKeyPath $key.PublicPath -ApkSignerPath $stubOk
Assert-True ($r.ExitCode -ne 0) 'history releases string nonzero'
Assert-True (($r.Json.reasons -join ' ') -match 'JSON array, not a string') 'history releases string reason'

# malformed policy: channels as object (must not accept via @() coercion)
$polObjChannels = Join-Path $fx 'policy-object-channels.json'
Write-Utf8NoBom -Path $polObjChannels -Content (@'
{
  "schemaVersion": 1,
  "channels": {
    "id": "dev",
    "enabled": true,
    "apkInstallCertSha256": "aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899",
    "publisherPublicKeySha256": "aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899"
  }
}
'@ + "`n")
$r = Invoke-Eligibility -CandidateDirectory $happyCand.RunDir -PolicyPath $polObjChannels -HistoryPath $historyPath -Channel 'dev' -PublisherPublicKeyPath $key.PublicPath -ApkSignerPath $stubOk
Assert-True ($r.ExitCode -ne 0) 'policy channels object nonzero'
Assert-True (($r.Json.reasons -join ' ') -match 'JSON array, not an object') 'policy channels object reason'

# malformed history: coerced string sequence
$histBad = Join-Path $fx 'history-string-seq.json'
New-HistoryFile -Path $histBad -RawJson (@'
{
  "schemaVersion": 1,
  "releases": [ { "sequence": "23", "note": "bad" } ]
}
'@)
$r = Invoke-Eligibility -CandidateDirectory $happyCand.RunDir -PolicyPath $policyPath -HistoryPath $histBad -Channel 'dev' -PublisherPublicKeyPath $key.PublicPath -ApkSignerPath $stubOk
Assert-True ($r.ExitCode -ne 0) 'string sequence nonzero'
Assert-True (($r.Json.reasons -join ' ') -match 'JSON integer type') 'string sequence noncoercing reason'

# malformed history: fractional sequence
$histFrac = Join-Path $fx 'history-frac-seq.json'
New-HistoryFile -Path $histFrac -RawJson (@'
{
  "schemaVersion": 1,
  "releases": [ { "sequence": 23.1, "note": "bad" } ]
}
'@)
$r = Invoke-Eligibility -CandidateDirectory $happyCand.RunDir -PolicyPath $policyPath -HistoryPath $histFrac -Channel 'dev' -PublisherPublicKeyPath $key.PublicPath -ApkSignerPath $stubOk
Assert-True ($r.ExitCode -ne 0) 'fractional sequence nonzero'
Assert-True (($r.Json.reasons -join ' ') -match 'JSON integer type|Double') 'fractional sequence reason'

# duplicate sequence
$histDup = Join-Path $fx 'history-dup.json'
New-HistoryFile -Path $histDup -Sequences @(10, 20, 20)
$r = Invoke-Eligibility -CandidateDirectory $happyCand.RunDir -PolicyPath $policyPath -HistoryPath $histDup -Channel 'dev' -PublisherPublicKeyPath $key.PublicPath -ApkSignerPath $stubOk
Assert-True ($r.ExitCode -ne 0) 'duplicate sequence nonzero'
Assert-True (($r.Json.reasons -join ' ') -match 'not globally unique') 'duplicate sequence reason'

# non-monotonic history ordering
$histNonMono = Join-Path $fx 'history-nonmono.json'
New-HistoryFile -Path $histNonMono -Sequences @(10, 30, 20)
$r = Invoke-Eligibility -CandidateDirectory $happyCand.RunDir -PolicyPath $policyPath -HistoryPath $histNonMono -Channel 'dev' -PublisherPublicKeyPath $key.PublicPath -ApkSignerPath $stubOk
Assert-True ($r.ExitCode -ne 0) 'non-monotonic history nonzero'
Assert-True (($r.Json.reasons -join ' ') -match 'monotonic') 'non-monotonic history reason'

# incomplete candidate status
$failRoot = New-RepoCandidateRoot
$failCand = New-CandidateFixture -RunDir (Join-Path $failRoot 'run-failed') -Sequence 24 -Status 'failed'
$r = Invoke-Eligibility -CandidateDirectory $failCand.RunDir -PolicyPath $policyPath -HistoryPath $historyPath -Channel 'dev' -PublisherPublicKeyPath $key.PublicPath -ApkSignerPath $stubOk
Assert-True ($r.ExitCode -ne 0) 'failed status nonzero'
Assert-True (($r.Json.reasons -join ' ') -match "status must be exactly 'complete'") 'failed status reason'

# confirm history unchanged across a refusal
$histPriorBefore = Get-FileSha256Lower $historyPrior
$r = Invoke-Eligibility -CandidateDirectory $lowCand.RunDir -PolicyPath $policyPath -HistoryPath $historyPrior -Channel 'dev' -PublisherPublicKeyPath $key.PublicPath -ApkSignerPath $stubOk
Assert-True ($r.ExitCode -ne 0) 'refusal still nonzero'
Assert-True ((Get-FileSha256Lower $historyPrior) -ceq $histPriorBefore) 'history bytes unchanged after refusal'

# static deny: owned scripts must not write history / sign / read private import APIs casually
Write-Host "`n=== static deny markers ==="
$owned = @(
    (Join-Path $ReleaseDir 'ReleasePolicy.ps1'),
    (Join-Path $ReleaseDir 'Get-ApkInstallCertSha256.ps1'),
    (Join-Path $ReleaseDir 'Test-ReleaseEligibility.ps1')
)
foreach ($f in $owned) {
    $text = Get-Content -Raw -LiteralPath $f
    Assert-True ($text -notmatch 'ImportPkcs8PrivateKey|ImportRSAPrivateKey|ExportPkcs8PrivateKey') "no private-key import/export API in $(Split-Path $f -Leaf)"
    Assert-True ($text -notmatch 'Write-AtomicTextFile|File::WriteAllText\(\$history|Set-Content\s+-.*History') "no history write helper usage in $(Split-Path $f -Leaf)"
    Assert-True ($text -notmatch 'releasable\s*=\s*\$true') "releasable never set true in $(Split-Path $f -Leaf)"
}

Write-Host ""
Write-Host "Fixtures retained under:"
foreach ($root in $fixtureRoots) { Write-Host "  $root" }
Write-Host ""
Write-Host "Passed: $passed  Failed: $failed"
if ($failed -gt 0) {
    exit 1
}
exit 0
