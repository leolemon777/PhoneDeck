# B03 S2: shared read-only release-policy helpers.
# Dotted by Get-ApkInstallCertSha256.ps1 / Test-ReleaseEligibility.ps1.
# Never writes candidate/history, never signs, never reads private keys.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-HasReparseOrLinkLocal {
    param([Parameter(Mandatory)][string]$PathToCheck)
    if (-not (Test-Path -LiteralPath $PathToCheck)) { return $false }
    $item = Get-Item -LiteralPath $PathToCheck -Force
    if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { return $true }
    if ($item.LinkType) { return $true }
    return $false
}

function Test-IsCaseSensitiveFileSystemLocal {
    param([Parameter(Mandatory)][string]$ProbeDir)
    if (-not (Test-Path -LiteralPath $ProbeDir)) { return -not $IsWindows }
    $name = [System.IO.Path]::GetFileName($ProbeDir.TrimEnd('\', '/'))
    if ([string]::IsNullOrWhiteSpace($name)) { return -not $IsWindows }
    $flipped = if ($name.ToLowerInvariant() -ceq $name) { $name.ToUpperInvariant() } else { $name.ToLowerInvariant() }
    if ($flipped -ceq $name) { return -not $IsWindows }
    $parent = Split-Path -Parent $ProbeDir
    $alt = Join-Path $parent $flipped
    return -not (Test-Path -LiteralPath $alt)
}

function Assert-AncestorChainSafeLocal {
    param(
        [Parameter(Mandatory)][string]$TargetPath,
        [Parameter(Mandatory)][string]$StopAtPath
    )
    $stop = [System.IO.Path]::GetFullPath($StopAtPath)
    $current = [System.IO.Path]::GetFullPath($TargetPath)
    while ($current -and $current.Length -ge $stop.Length) {
        if (Test-HasReparseOrLinkLocal -PathToCheck $current) {
            throw "Directory chain must not contain symlink/junction/reparse. Found: $current"
        }
        if ($current.Equals($stop, [System.StringComparison]::OrdinalIgnoreCase)) { break }
        $parent = [System.IO.Path]::GetDirectoryName($current)
        if (-not $parent -or $parent -eq $current) { break }
        $current = $parent
    }
}

function Get-FileSha256LowerLocal {
    param([Parameter(Mandatory)][string]$LiteralPath)
    return (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Test-StrictJsonIntegerTypeLocal {
    param([AllowNull()]$Value)
    if ($null -eq $Value) { return $false }
    if ($Value -is [bool]) { return $false }
    if ($Value -is [string]) { return $false }
    if ($Value -is [double] -or $Value -is [float] -or $Value -is [decimal]) { return $false }
    return (
        $Value -is [byte] -or $Value -is [sbyte] -or
        $Value -is [int16] -or $Value -is [uint16] -or
        $Value -is [int32] -or $Value -is [uint32] -or
        $Value -is [int64] -or $Value -is [uint64]
    )
}

function Assert-StrictJsonIntegerLocal {
    param(
        [AllowNull()]$Value,
        [Parameter(Mandatory)][string]$Name,
        [long]$Min = 1,
        [long]$Max = [long]::MaxValue
    )
    if ($null -eq $Value) {
        throw "Missing required field: $Name"
    }
    if (-not (Test-StrictJsonIntegerTypeLocal -Value $Value)) {
        $typeName = $Value.GetType().FullName
        throw "Field $Name must be a JSON integer type (not bool/string/fractional), got type=$typeName value=$Value"
    }
    $n = [long]$Value
    if ($n -lt $Min -or $n -gt $Max) {
        throw "Field $Name must be in [$Min, $Max], got: $n"
    }
    return $n
}

function Assert-FullSha256Hex {
    param(
        [AllowNull()]$Value,
        [Parameter(Mandatory)][string]$Name,
        [switch]$AllowEmpty
    )
    if ($null -eq $Value) {
        if ($AllowEmpty) { return '' }
        throw "Missing required field: $Name"
    }
    if ($Value -isnot [string]) {
        throw "Field $Name must be a string, got type=$($Value.GetType().FullName)"
    }
    $text = [string]$Value
    if ([string]::IsNullOrEmpty($text)) {
        if ($AllowEmpty) { return '' }
        throw "Field $Name is empty (unconfigured)."
    }
    if ($text -notmatch '^[0-9a-fA-F]{64}$') {
        throw "Field $Name must be a full 64-hex SHA256 (no colons/spaces), got length=$($text.Length)"
    }
    return $text.ToLowerInvariant()
}

function Normalize-ApkCertSha256Digest {
    param([Parameter(Mandatory)][string]$RawDigest)
    $compact = ($RawDigest -replace '[:\s]', '').Trim().ToLowerInvariant()
    if ($compact -notmatch '^[0-9a-f]{64}$') {
        throw "APK certificate SHA-256 digest is not a full 64-hex value after normalization: '$RawDigest'"
    }
    return $compact
}

function Resolve-ExistingFileLocal {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string]$RepoRoot
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
        throw "$Label file not found: $Path"
    }
    if (Test-HasReparseOrLinkLocal -PathToCheck $full) {
        throw "$Label must not be a symlink/junction/reparse file."
    }
    return $full
}

function Resolve-SafeCandidateDirectory {
    param(
        [Parameter(Mandatory)][string]$CandidateDirectory,
        [Parameter(Mandatory)][string]$RepoRoot
    )
    if ([string]::IsNullOrWhiteSpace($CandidateDirectory)) {
        throw "CandidateDirectory path is empty."
    }
    $full = if ([System.IO.Path]::IsPathRooted($CandidateDirectory)) {
        [System.IO.Path]::GetFullPath($CandidateDirectory)
    } else {
        [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $CandidateDirectory))
    }
    if (-not (Test-Path -LiteralPath $full -PathType Container)) {
        throw "CandidateDirectory not found or not a directory."
    }

    $allowedOutputsDir = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot 'outputs'))
    $separator = [System.IO.Path]::DirectorySeparatorChar.ToString()
    $allowedPrefix = if ($allowedOutputsDir.EndsWith($separator)) { $allowedOutputsDir } else { $allowedOutputsDir + $separator }
    $comparison = if (Test-IsCaseSensitiveFileSystemLocal -ProbeDir $RepoRoot) {
        [System.StringComparison]::Ordinal
    } else {
        [System.StringComparison]::OrdinalIgnoreCase
    }
    if ($full.Equals($allowedOutputsDir, $comparison) -or -not $full.StartsWith($allowedPrefix, $comparison)) {
        throw "CandidateDirectory must be under repo outputs/ (path escape refused)."
    }

    $repoParent = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot '..'))
    Assert-AncestorChainSafeLocal -TargetPath $full -StopAtPath $repoParent
    if (Test-HasReparseOrLinkLocal -PathToCheck $full) {
        throw "CandidateDirectory must not be a symlink/junction/reparse directory."
    }
    return $full
}

function Get-ApkInstallCertSha256FromApksigner {
    param(
        [Parameter(Mandatory)][string]$ApkPath,
        [Parameter(Mandatory)][string]$ApkSignerPath
    )
    if (-not (Test-Path -LiteralPath $ApkPath -PathType Leaf)) {
        throw "APK file not found."
    }
    if (-not (Test-Path -LiteralPath $ApkSignerPath -PathType Leaf)) {
        throw "ApkSignerPath file not found."
    }
    if (Test-HasReparseOrLinkLocal -PathToCheck $ApkPath) {
        throw "APK must not be a symlink/junction/reparse file."
    }
    if (Test-HasReparseOrLinkLocal -PathToCheck $ApkSignerPath) {
        throw "ApkSignerPath must not be a symlink/junction/reparse file."
    }

    $pinfo = New-Object System.Diagnostics.ProcessStartInfo
    $pinfo.FileName = $ApkSignerPath
    $pinfo.UseShellExecute = $false
    $pinfo.RedirectStandardOutput = $true
    $pinfo.RedirectStandardError = $true
    $pinfo.CreateNoWindow = $true
    $pinfo.ArgumentList.Add('verify') | Out-Null
    $pinfo.ArgumentList.Add('--print-certs') | Out-Null
    $pinfo.ArgumentList.Add($ApkPath) | Out-Null

    $p = New-Object System.Diagnostics.Process
    $p.StartInfo = $pinfo
    [void]$p.Start()
    # Drain stdout and stderr concurrently before WaitForExit. Sequential ReadToEnd
    # deadlocks when the child fills the stderr pipe while stdout remains open.
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    $p.WaitForExit()
    $stdout = $outTask.Result
    $stderr = $errTask.Result
    $code = $p.ExitCode
    $p.Dispose()

    $combined = (($stdout + [Environment]::NewLine + $stderr) -replace "`r`n", "`n")
    if ($code -ne 0) {
        $hint = ($combined.Trim() -split "`n" | Select-Object -First 4) -join ' | '
        throw "apksigner verify --print-certs failed with exit code $code. $hint"
    }

    $matches = [regex]::Matches(
        $combined,
        'Signer\s+#(\d+)\s+certificate\s+SHA-256\s+digest:\s*([0-9A-Fa-f:]+)',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    )
    if ($matches.Count -eq 0) {
        throw "apksigner output missing Signer certificate SHA-256 digest."
    }
    if ($matches.Count -ne 1) {
        throw "APK must have exactly one signer certificate SHA-256 digest; found $($matches.Count)."
    }
    $signerNum = [int]$matches[0].Groups[1].Value
    if ($signerNum -ne 1) {
        throw "Expected Signer #1 certificate SHA-256 digest; found Signer #$signerNum."
    }

    # Reject additional signer blocks even if only one SHA-256 line matched.
    $signerHeaders = [regex]::Matches($combined, 'Signer\s+#(\d+)\s+certificate\s+DN:', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($signerHeaders.Count -gt 1) {
        throw "APK must have a single signer; found $($signerHeaders.Count) certificate DN blocks."
    }

    return (Normalize-ApkCertSha256Digest -RawDigest $matches[0].Groups[2].Value)
}

function Get-PublisherPublicKeySha256FromPem {
    param([Parameter(Mandatory)][string]$PemPath)
    if (-not (Test-Path -LiteralPath $PemPath -PathType Leaf)) {
        throw "PublisherPublicKeyPath file not found."
    }
    if (Test-HasReparseOrLinkLocal -PathToCheck $PemPath) {
        throw "PublisherPublicKeyPath must not be a symlink/junction/reparse file."
    }

    $pem = [System.IO.File]::ReadAllText($PemPath)
    if ([string]::IsNullOrWhiteSpace($pem)) {
        throw "Publisher public key PEM is empty."
    }
    if ($pem -match '(?im)^\s*-+\s*BEGIN\s+(RSA\s+)?PRIVATE\s+KEY\s*-+' -or
        $pem -match '(?im)^\s*-+\s*BEGIN\s+ENCRYPTED\s+PRIVATE\s+KEY\s*-+') {
        throw "Private key material refused; PublisherPublicKeyPath must be a public RSA PEM only."
    }
    if ($pem -notmatch '(?im)^\s*-+\s*BEGIN\s+PUBLIC\s+KEY\s*-+') {
        throw "PublisherPublicKeyPath must contain a BEGIN PUBLIC KEY PEM block."
    }

    $rsa = [System.Security.Cryptography.RSA]::Create()
    try {
        try {
            # ImportFromPem binds ReadOnlySpan<char>; ToCharArray is the portable PS7 form.
            $rsa.ImportFromPem($pem.ToCharArray())
        } catch {
            throw "Failed to import publisher public RSA PEM: $($_.Exception.Message)"
        }
        # ExportSubjectPublicKeyInfo is the canonical SPKI DER encoding.
        $spki = $rsa.ExportSubjectPublicKeyInfo()
        if ($null -eq $spki -or $spki.Length -lt 1) {
            throw "ExportSubjectPublicKeyInfo returned empty SPKI bytes."
        }
        $hash = [System.Security.Cryptography.SHA256]::HashData($spki)
        return [System.BitConverter]::ToString($hash).Replace('-', '').ToLowerInvariant()
    } finally {
        $rsa.Dispose()
    }
}

function Read-JsonObjectFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Label
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label file not found."
    }
    if (Test-HasReparseOrLinkLocal -PathToCheck $Path) {
        throw "$Label must not be a symlink/junction/reparse file."
    }
    $raw = [System.IO.File]::ReadAllText($Path)
    if ([string]::IsNullOrWhiteSpace($raw)) {
        throw "$Label is empty."
    }
    try {
        # Unary comma: prevent pipeline enumeration of the root object.
        return ,($raw | ConvertFrom-Json -Depth 32)
    } catch {
        throw "$Label is malformed JSON: $($_.Exception.Message)"
    }
}

function Assert-StrictJsonArrayLocal {
    param(
        [AllowNull()]$Value,
        [Parameter(Mandatory)][string]$Name,
        [switch]$AllowEmpty
    )
    if ($null -eq $Value) {
        throw "$Name is missing or null (must be a JSON array)."
    }
    if ($Value -is [string]) {
        throw "$Name must be a JSON array, not a string."
    }
    # JSON object → PSCustomObject / dictionary. Do not accept via @() coercion.
    if ($Value -is [System.Management.Automation.PSCustomObject] -or $Value -is [System.Collections.IDictionary]) {
        throw "$Name must be a JSON array, not an object."
    }
    if ($Value -isnot [System.Collections.IList]) {
        throw "$Name must be a JSON array, got type=$($Value.GetType().FullName)"
    }
    if (-not $AllowEmpty -and $Value.Count -lt 1) {
        throw "$Name must contain at least one element."
    }
    # Unary comma keeps a single-element array from unwrapping on return.
    return ,$Value
}

function Get-PolicyChannel {
    param(
        [Parameter(Mandatory)]$Policy,
        [Parameter(Mandatory)][string]$ChannelId
    )
    $schema = Assert-StrictJsonIntegerLocal -Value $Policy.schemaVersion -Name 'policy.schemaVersion' -Min 1 -Max 1
    if ($null -eq $Policy.PSObject.Properties['channels']) {
        throw "policy.channels is missing."
    }
    $channels = Assert-StrictJsonArrayLocal -Value $Policy.channels -Name 'policy.channels'

    $found = New-Object System.Collections.Generic.List[object]
    foreach ($ch in $channels) {
        if ($null -eq $ch) { continue }
        if ($null -eq $ch.id -or $ch.id -isnot [string]) {
            throw "policy.channels[].id must be a string."
        }
        if ([string]$ch.id -ceq $ChannelId) {
            [void]$found.Add($ch)
        }
    }
    if ($found.Count -eq 0) {
        throw "Channel '$ChannelId' not found in policy."
    }
    if ($found.Count -gt 1) {
        throw "Channel '$ChannelId' is duplicated in policy."
    }

    $channelRow = $found[0]
    if ($null -eq $channelRow.enabled -or $channelRow.enabled -isnot [bool]) {
        throw "policy.channels[id=$ChannelId].enabled must be a JSON boolean."
    }

    $apkFp = Assert-FullSha256Hex -Value $channelRow.apkInstallCertSha256 -Name "policy.channels[id=$ChannelId].apkInstallCertSha256" -AllowEmpty
    $pubFp = Assert-FullSha256Hex -Value $channelRow.publisherPublicKeySha256 -Name "policy.channels[id=$ChannelId].publisherPublicKeySha256" -AllowEmpty

    # Unary comma: emit ONLY the intended object (no DictionaryEntry enumeration).
    return ,([ordered]@{
        schemaVersion              = $schema
        id                         = [string]$channelRow.id
        enabled                    = [bool]$channelRow.enabled
        apkInstallCertSha256       = $apkFp
        publisherPublicKeySha256   = $pubFp
    })
}

function Get-HistorySequenceState {
    param([Parameter(Mandatory)]$History)
    $schema = Assert-StrictJsonIntegerLocal -Value $History.schemaVersion -Name 'history.schemaVersion' -Min 1 -Max 1
    if ($null -eq $History.PSObject.Properties['releases']) {
        throw "history.releases is missing."
    }
    # Explicit empty array is the only valid "new channel" initializer; object/string/null refused.
    $releases = Assert-StrictJsonArrayLocal -Value $History.releases -Name 'history.releases' -AllowEmpty
    $seen = New-Object 'System.Collections.Generic.HashSet[long]'
    $maxSeq = [long]0
    $previous = [long]0
    $index = 0
    foreach ($rel in $releases) {
        if ($null -eq $rel) {
            throw "history.releases[$index] is null."
        }
        $seq = Assert-StrictJsonIntegerLocal -Value $rel.sequence -Name "history.releases[$index].sequence" -Min 1
        if (-not $seen.Add($seq)) {
            throw "history.releases sequence is not globally unique; duplicate sequence=$seq."
        }
        if ($index -gt 0 -and $seq -le $previous) {
            throw "history.releases sequence must be strictly monotonic ascending; releases[$index].sequence=$seq is not greater than previous=$previous."
        }
        if ($seq -gt $maxSeq) { $maxSeq = $seq }
        $previous = $seq
        $index++
    }

    return ,([ordered]@{
        schemaVersion = $schema
        count         = [int]$releases.Count
        maxSequence   = $maxSeq
        sequences     = @($seen)
    })
}

function Assert-CandidateReportForEligibility {
    param(
        [Parameter(Mandatory)][string]$CandidateDirectory
    )

    $reportPath = Join-Path $CandidateDirectory 'candidate.json'
    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
        throw "candidate.json missing under CandidateDirectory."
    }
    if (Test-HasReparseOrLinkLocal -PathToCheck $reportPath) {
        throw "candidate.json must not be a symlink/junction/reparse file."
    }

    $report = Read-JsonObjectFile -Path $reportPath -Label 'candidate.json'
    [void](Assert-StrictJsonIntegerLocal -Value $report.schemaVersion -Name 'candidate.schemaVersion' -Min 1 -Max 1)

    if ($null -eq $report.mode -or $report.mode -isnot [string] -or [string]$report.mode -cne 'staging') {
        throw "candidate.mode must be exactly 'staging'."
    }
    if ($null -eq $report.status -or $report.status -isnot [string] -or [string]$report.status -cne 'complete') {
        throw "candidate.status must be exactly 'complete' (got: $($report.status))."
    }
    if ($null -eq $report.releasable -or $report.releasable -isnot [bool] -or $report.releasable -ne $false) {
        throw "candidate.releasable must be JSON boolean false."
    }
    if ($null -eq $report.sourceDirty -or $report.sourceDirty -isnot [bool]) {
        throw "candidate.sourceDirty must be a JSON boolean."
    }
    if ([bool]$report.sourceDirty) {
        throw "candidate.sourceDirty=true refused (dirty worktree provenance)."
    }
    if ($null -eq $report.sourceCommit -or $report.sourceCommit -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$report.sourceCommit)) {
        throw "candidate.sourceCommit must be a non-empty string (worktree provenance only)."
    }

    if ($null -eq $report.versions) {
        throw "candidate.versions is missing."
    }
    [void](Assert-StrictJsonIntegerLocal -Value $report.versions.schemaVersion -Name 'candidate.versions.schemaVersion' -Min 1 -Max 1)
    if ($null -eq $report.versions.windows) {
        throw "candidate.versions.windows is missing."
    }
    $sequence = Assert-StrictJsonIntegerLocal -Value $report.versions.windows.sequence -Name 'candidate.versions.windows.sequence' -Min 1
    if ($null -eq $report.versions.android) {
        throw "candidate.versions.android is missing."
    }
    [void](Assert-StrictJsonIntegerLocal -Value $report.versions.android.versionCode -Name 'candidate.versions.android.versionCode' -Min 1)

    if ($null -eq $report.files) {
        throw "candidate.files is missing."
    }
    $files = @($report.files)
    $expected = @(
        @{ name = 'PhoneDeck.Server.exe'; path = 'payload/PhoneDeck.Server.exe' },
        @{ name = 'PhoneDeck.ControlCenter.exe'; path = 'payload/PhoneDeck.ControlCenter.exe' },
        @{ name = 'PhoneDeck.apk'; path = 'payload/PhoneDeck.apk' }
    )
    if ($files.Count -ne $expected.Count) {
        throw "candidate.files must contain exactly $($expected.Count) entries; found $($files.Count)."
    }

    $byName = @{}
    foreach ($f in $files) {
        if ($null -eq $f.name -or $f.name -isnot [string]) {
            throw "candidate.files[].name must be a string."
        }
        if ($null -eq $f.path -or $f.path -isnot [string]) {
            throw "candidate.files[].path must be a string."
        }
        $name = [string]$f.name
        if ($byName.ContainsKey($name)) {
            throw "candidate.files contains duplicate name: $name"
        }
        $size = Assert-StrictJsonIntegerLocal -Value $f.size -Name "candidate.files[$name].size" -Min 1
        $sha = Assert-FullSha256Hex -Value $f.sha256 -Name "candidate.files[$name].sha256"
        $byName[$name] = [ordered]@{
            name   = $name
            path   = [string]$f.path
            size   = $size
            sha256 = $sha
        }
    }

    foreach ($exp in $expected) {
        if (-not $byName.ContainsKey($exp.name)) {
            throw "candidate.files missing required name: $($exp.name)"
        }
        $entry = $byName[$exp.name]
        if ($entry.path -cne $exp.path) {
            throw "candidate.files[$($exp.name)].path must be '$($exp.path)' (got '$($entry.path)')."
        }
        if ($entry.path.Contains('..') -or $entry.path.StartsWith('/') -or $entry.path.StartsWith('\') -or $entry.path -match '^[A-Za-z]:') {
            throw "candidate.files[$($exp.name)].path refuses absolute/escape path."
        }

        $payloadPath = Join-Path $CandidateDirectory (($entry.path -replace '/', [System.IO.Path]::DirectorySeparatorChar))
        $payloadFull = [System.IO.Path]::GetFullPath($payloadPath)
        $candidateFull = [System.IO.Path]::GetFullPath($CandidateDirectory)
        $sep = [System.IO.Path]::DirectorySeparatorChar.ToString()
        $prefix = if ($candidateFull.EndsWith($sep)) { $candidateFull } else { $candidateFull + $sep }
        if (-not $payloadFull.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "candidate payload path escapes CandidateDirectory: $($entry.path)"
        }
        if (-not (Test-Path -LiteralPath $payloadFull -PathType Leaf)) {
            throw "candidate payload file missing: $($entry.path)"
        }
        if (Test-HasReparseOrLinkLocal -PathToCheck $payloadFull) {
            throw "candidate payload must not be a symlink/junction/reparse file: $($entry.path)"
        }

        $actualSize = [long](Get-Item -LiteralPath $payloadFull).Length
        $actualSha = Get-FileSha256LowerLocal -LiteralPath $payloadFull
        if ($actualSize -ne $entry.size) {
            throw "candidate payload size mismatch for $($entry.name): report=$($entry.size) actual=$actualSize"
        }
        if ($actualSha -cne $entry.sha256) {
            throw "candidate payload sha256 mismatch for $($entry.name) (report vs bytes)."
        }
    }

    $apkPath = Join-Path $CandidateDirectory 'payload\PhoneDeck.apk'

    return ,([ordered]@{
        reportPath    = $reportPath
        runId         = $(if ($report.runId -is [string]) { [string]$report.runId } else { '' })
        sourceCommit  = [string]$report.sourceCommit
        sourceDirty   = [bool]$report.sourceDirty
        sequence      = $sequence
        apkPath       = [System.IO.Path]::GetFullPath($apkPath)
        files         = @($byName.Values)
    })
}

function New-EligibilityResultObject {
    param(
        [Parameter(Mandatory)][bool]$Eligible,
        [Parameter(Mandatory)][string]$Channel,
        [string[]]$Reasons = @(),
        [hashtable]$Candidate = $null,
        [hashtable]$Verified = $null,
        [string]$Note = 'Read-only preflight only; releasable remains false; history/candidate bytes are not modified; signing/release is out of scope.'
    )

    # Stable fields on success and failure (StrictMode consumers can always read verified/candidate).
    $verifiedOut = if ($null -ne $Verified) { $Verified } else { [ordered]@{} }
    $obj = [ordered]@{
        schemaVersion = 1
        eligible      = [bool]$Eligible
        releasable    = $false
        channel       = $Channel
        reasons       = @($Reasons)
        note          = $Note
        candidate     = $Candidate
        verified      = $verifiedOut
    }
    return ,$obj
}
