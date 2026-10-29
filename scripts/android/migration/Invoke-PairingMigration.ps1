[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Export', 'Verify', 'Import')]
    [string]$Mode,

    [Parameter(Mandatory = $true)]
    [string]$Adb,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Serial,

    [Parameter(Mandatory = $true)]
    [string]$JavaHome,

    [Parameter(Mandatory = $true)]
    [string]$AndroidSdk,

    [Parameter(Mandatory = $true)]
    [string]$BackupFile,

    [switch]$TestEmulator
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# This is deliberately a one-shot migration tool.  The helper is app-UID
# scoped; the PowerShell side never prints or writes the decrypted payload.
$PackageName = 'com.codex.phonedeck'
$DataDirectory = '/data/user/0/com.codex.phonedeck'
$AppHelperRelativePath = 'files/.phonedeck-pairing-migration.jar'
$AppHelperAbsolutePath = '/data/user/0/com.codex.phonedeck/files/.phonedeck-pairing-migration.jar'
$HelperClass = 'com.codex.phonedeck.migration.PairingMigration'
$FrameStart = 'PHONEDECK_PAIRING_MIGRATION_V1'
$FrameEnd = 'PHONEDECK_PAIRING_MIGRATION_END'
$MaximumPayloadBytes = 4 * 1024 * 1024

# Import is the only operation that can create a real app preference file.  A
# physical serial must never reach that path, even if a caller accidentally
# supplies -TestEmulator.
if ($Mode -eq 'Import') {
    if (-not $TestEmulator) {
        throw 'Import requires explicit -TestEmulator; no physical device may be modified.'
    }
    if ($Serial -notmatch '^emulator-[0-9]+$') {
        throw 'Import requires an emulator serial matching emulator-<number>.'
    }
}

$script:AdbPath = $null
$script:RemoteTempPath = $null
$script:DeviceTouched = $false
$script:BuildRoot = $null
$script:BuildRootCreated = $false

function Throw-Safe {
    param([Parameter(Mandatory = $true)][string]$Message)

    # Do not concatenate native stderr/stdout or exception messages here.  ADB
    # and JSON/crypto providers can echo credentials or input fragments.
    throw [System.InvalidOperationException]::new($Message)
}

function Resolve-ExecutablePath {
    param([Parameter(Mandatory = $true)][string]$Value)

    if (Test-Path -LiteralPath $Value -PathType Leaf) {
        return [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Value).Path)
    }

    try {
        $command = Get-Command -Name $Value -CommandType Application -ErrorAction Stop
        return $command.Source
    } catch {
        Throw-Safe "Executable not found: $Value"
    }
}

function Resolve-DirectoryPath {
    param([Parameter(Mandatory = $true)][string]$Value)

    if (-not (Test-Path -LiteralPath $Value -PathType Container)) {
        Throw-Safe "Directory not found: $Value"
    }
    return [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Value).Path)
}

function Stop-CapturedProcess {
    param([Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process)

    try {
        if (-not $Process.HasExited) {
            # Kill only this child.  Do not use Kill(entireProcessTree), since
            # adb may have unrelated sibling processes owned by the caller.
            $Process.Kill()
        }
    } catch {
    }
    try {
        [void]$Process.WaitForExit(5000)
    } catch {
    }
}

function Wait-CapturedTask {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)][System.Threading.Tasks.Task]$Task,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    try {
        if (-not $Task.Wait([System.TimeSpan]::FromSeconds($TimeoutSeconds))) {
            Stop-CapturedProcess $Process
            Throw-Safe $FailureMessage
        }
        return $Task.GetAwaiter().GetResult()
    } catch {
        if ($_.Exception -is [System.InvalidOperationException]) {
            throw
        }
        Stop-CapturedProcess $Process
        Throw-Safe $FailureMessage
    }
}

function Invoke-CapturedProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [AllowNull()][object]$StandardInputText,
        [switch]$BinaryOutput
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.RedirectStandardInput = $null -ne $StandardInputText
    if ($null -ne $StandardInputText) {
        # The helper protocol is strict ASCII Base64.  Explicit ASCII avoids a
        # platform-default UTF-8 preamble and keeps stdin a single text line.
        $startInfo.StandardInputEncoding = [System.Text.Encoding]::ASCII
    }
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add([string]$argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stdoutMemory = $null
    try {
        try {
            if (-not $process.Start()) {
                Throw-Safe "Could not start process: $FilePath"
            }
        } catch {
            if ($_.Exception -is [System.InvalidOperationException]) {
                throw
            }
            Throw-Safe "Could not start process: $FilePath"
        }

        if ($BinaryOutput) {
            $stdoutMemory = [System.IO.MemoryStream]::new()
            $stdoutTask = $process.StandardOutput.BaseStream.CopyToAsync($stdoutMemory)
        } else {
            $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        }
        $stderrTask = $process.StandardError.ReadToEndAsync()

        if ($null -ne $StandardInputText) {
            $inputTask = $process.StandardInput.WriteAsync($StandardInputText)
            try {
                [void](Wait-CapturedTask -Process $process -Task $inputTask -TimeoutSeconds 30 `
                    -FailureMessage 'Process stdin write timed out.')
            } finally {
                try {
                    $process.StandardInput.Close()
                } catch {
                }
            }
        }

        $exitTask = $process.WaitForExitAsync()
        [void](Wait-CapturedTask -Process $process -Task $exitTask -TimeoutSeconds 60 `
            -FailureMessage 'Process execution timed out.')
        [void](Wait-CapturedTask -Process $process -Task $stdoutTask -TimeoutSeconds 30 `
            -FailureMessage 'Process stdout capture timed out.')
        $stderr = Wait-CapturedTask -Process $process -Task $stderrTask -TimeoutSeconds 30 `
            -FailureMessage 'Process stderr capture timed out.'
        if ($BinaryOutput) {
            $stdout = $stdoutMemory.ToArray()
        } else {
            $stdout = [string](Wait-CapturedTask -Process $process -Task $stdoutTask `
                -TimeoutSeconds 1 -FailureMessage 'Process stdout capture failed.')
        }

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Stdout = $stdout
            Stderr = $stderr
        }
    } finally {
        if ($stdoutMemory) {
            $stdoutMemory.Dispose()
        }
        $process.Dispose()
    }
}

function Invoke-Adb {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [AllowNull()][object]$StandardInputText,
        [switch]$BinaryOutput
    )

    return Invoke-CapturedProcess -FilePath $script:AdbPath -Arguments $Arguments `
        -StandardInputText $StandardInputText -BinaryOutput:$BinaryOutput
}

function Get-AndroidJar {
    param([Parameter(Mandatory = $true)][string]$SdkRoot)

    $platformRoot = Join-Path $SdkRoot 'platforms'
    if (-not (Test-Path -LiteralPath $platformRoot -PathType Container)) {
        Throw-Safe 'Android SDK platforms directory not found.'
    }

    $platform = Get-ChildItem -LiteralPath $platformRoot -Directory -ErrorAction Stop |
        Where-Object { $_.Name -match '^android-([0-9]+)$' } |
        ForEach-Object {
            [pscustomobject]@{
                Api = [int]$Matches[1]
                Path = Join-Path $_.FullName 'android.jar'
            }
        } |
        Where-Object { Test-Path -LiteralPath $_.Path -PathType Leaf } |
        Sort-Object Api -Descending |
        Select-Object -First 1

    if ($null -eq $platform) {
        Throw-Safe 'No Android platform android.jar was found in the SDK.'
    }
    return $platform.Path
}

function New-HelperBuild {
    param(
        [Parameter(Mandatory = $true)][string]$JavaRoot,
        [Parameter(Mandatory = $true)][string]$SdkRoot
    )

    $javac = Resolve-ExecutablePath (Join-Path $JavaRoot 'bin\javac.exe')
    $java = Resolve-ExecutablePath (Join-Path $JavaRoot 'bin\java.exe')
    $jarTool = Resolve-ExecutablePath (Join-Path $JavaRoot 'bin\jar.exe')
    $androidJar = Get-AndroidJar $SdkRoot
    $buildToolsRoot = Join-Path $SdkRoot 'build-tools'
    if (-not (Test-Path -LiteralPath $buildToolsRoot -PathType Container)) {
        Throw-Safe 'Android SDK build-tools directory not found.'
    }
    $d8 = Get-ChildItem -LiteralPath $buildToolsRoot -Directory -ErrorAction Stop |
        Where-Object { $_.Name -match '^([0-9]+)(\.[0-9]+){1,3}$' } |
        ForEach-Object {
            $d8Path = Join-Path $_.FullName 'lib\d8.jar'
            if (Test-Path -LiteralPath $d8Path -PathType Leaf) {
                [pscustomobject]@{
                    Version = [version]$_.Name
                    Path = $d8Path
                }
            }
        } |
        Sort-Object Version -Descending |
        Select-Object -First 1
    if ($null -eq $d8) {
        Throw-Safe 'No Android build-tools d8.jar was found in the SDK.'
    }
    $source = Join-Path $PSScriptRoot 'PairingMigration.java'
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        Throw-Safe 'PairingMigration.java is missing beside the migration script.'
    }

    $buildRoot = Join-Path ([System.IO.Path]::GetTempPath()) `
        ('phonedeck-pairing-migration-' + [guid]::NewGuid().ToString('N'))
    $classes = Join-Path $buildRoot 'classes'
    $dex = Join-Path $buildRoot 'dex'
    $jarPath = Join-Path $buildRoot 'PairingMigration.jar'
    if (Test-Path -LiteralPath $buildRoot) {
        Throw-Safe 'The generated helper build directory already exists.'
    }
    [void][System.IO.Directory]::CreateDirectory($classes)
    $script:BuildRoot = $buildRoot
    $script:BuildRootCreated = $true

    $compile = Invoke-CapturedProcess -FilePath $javac -Arguments @(
        '-encoding', 'UTF-8',
        '-source', '8',
        '-target', '8',
        '-Xlint:none',
        '-proc:none',
        '-cp', $androidJar,
        '-d', $classes,
        $source
    )
    if ($compile.ExitCode -ne 0) {
        Throw-Safe "Java helper compilation failed (exit $($compile.ExitCode))."
    }

    $classFiles = @(Get-ChildItem -LiteralPath $classes -Recurse -File -Filter '*.class' |
        ForEach-Object { $_.FullName })
    if ($classFiles.Count -eq 0) {
        Throw-Safe 'Java helper compilation produced no class files.'
    }
    [void][System.IO.Directory]::CreateDirectory($dex)
    $d8Arguments = @(
        '-cp', $d8.Path,
        'com.android.tools.r8.D8',
        '--min-api', '26',
        '--lib', $androidJar,
        '--output', $dex
    ) + $classFiles
    $dexResult = Invoke-CapturedProcess -FilePath $java -Arguments $d8Arguments
    $dexPath = Join-Path $dex 'classes.dex'
    if ($dexResult.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $dexPath -PathType Leaf)) {
        Throw-Safe "Android helper D8 conversion failed (exit $($dexResult.ExitCode))."
    }

    # app_process loads a DEX entry from the helper JAR.  Do not ship the
    # ordinary JVM class files or a generated manifest into the staged bytes.
    $pack = Invoke-CapturedProcess -FilePath $jarTool -Arguments @(
        'cMf', $jarPath,
        '-C', $dex,
        'classes.dex'
    )
    if ($pack.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $jarPath -PathType Leaf)) {
        Throw-Safe "Java helper packaging failed (exit $($pack.ExitCode))."
    }

    $listing = Invoke-CapturedProcess -FilePath $jarTool -Arguments @('tf', $jarPath)
    $entries = @($listing.Stdout -split "`r?`n" |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_.Length -ne 0 })
    if ($listing.ExitCode -ne 0 -or $entries.Count -ne 1 -or $entries[0] -cne 'classes.dex') {
        Throw-Safe 'The staged helper JAR must contain exactly classes.dex.'
    }

    $hash = (Get-FileHash -LiteralPath $jarPath -Algorithm SHA256).Hash.ToLowerInvariant()
    return [pscustomobject]@{
        Path = $jarPath
        Sha256 = $hash
    }
}

function Get-RemoteSha256 {
    param(
        [Parameter(Mandatory = $true)][string[]]$CommandArguments,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    $result = Invoke-Adb -Arguments $CommandArguments
    if ($result.ExitCode -ne 0) {
        Throw-Safe $FailureMessage
    }
    $match = [System.Text.RegularExpressions.Regex]::Match(
        [string]$result.Stdout,
        '(?im)(?<![0-9a-f])[0-9a-f]{64}(?![0-9a-f])')
    if (-not $match.Success) {
        Throw-Safe $FailureMessage
    }
    return $match.Value.ToLowerInvariant()
}

function Stage-Helper {
    param([Parameter(Mandatory = $true)][pscustomobject]$Helper)

    $remoteName = 'phonedeck-pairing-migration-' + [guid]::NewGuid().ToString('N') + '.jar'
    $remoteTemp = '/data/local/tmp/' + $remoteName
    $script:RemoteTempPath = $remoteTemp
    $script:DeviceTouched = $true

    $pushed = Invoke-Adb -Arguments @('-s', $Serial, 'push', $Helper.Path, $remoteTemp)
    if ($pushed.ExitCode -ne 0) {
        Throw-Safe 'Could not stage the migration helper on the device.'
    }

    $remoteHash = Get-RemoteSha256 -CommandArguments @(
        '-s', $Serial, 'shell', 'sha256sum', $remoteTemp
    ) -FailureMessage 'Could not verify the pushed helper hash.'
    if ($remoteHash -ne $Helper.Sha256) {
        Throw-Safe 'The pushed migration helper hash does not match the local build.'
    }

    # The pipe is evaluated on the device.  Both paths are constants except
    # for the generated alphanumeric temporary name, so no caller input is
    # interpreted as a remote shell fragment.
    $stageCommand = "cat $remoteTemp | run-as $PackageName sh -c `"cat > $AppHelperRelativePath`""
    $staged = Invoke-Adb -Arguments @('-s', $Serial, 'shell', $stageCommand)
    if ($staged.ExitCode -ne 0) {
        Throw-Safe 'Could not copy the helper into the app UID sandbox.'
    }

    $appHash = Get-RemoteSha256 -CommandArguments @(
        '-s', $Serial, 'shell', 'run-as', $PackageName, 'sha256sum', $AppHelperRelativePath
    ) -FailureMessage 'Could not verify the app-sandbox helper hash.'
    if ($appHash -ne $Helper.Sha256) {
        Throw-Safe 'The app-sandbox helper hash does not match the local build.'
    }

    return [pscustomobject]@{
        RemoteHash = $remoteHash
        AppHash = $appHash
        Verified = $true
    }
}

function Invoke-DeviceHelper {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('export', 'verify', 'import')][string]$HelperMode,
        [AllowNull()][object]$PayloadInput
    )

    # A no-PTY shell with escape handling disabled forwards Base64 stdin and
    # keeps the helper's diagnostic channel separate from its export frame.
    # Shell v2 keeps stdout/stderr separate for the strict export frame and
    # forwards Base64 stdin. Nullable object parameters preserve actual null;
    # PowerShell [string] parameters would silently turn null into empty text.
    $transport = @('shell', '-T', '-e', 'none')
    $arguments = @('-s', $Serial) + $transport + @(
        'run-as', $PackageName,
        'env',
        "CLASSPATH=$AppHelperAbsolutePath",
        'app_process', '/system/bin', $HelperClass,
        $HelperMode, $DataDirectory
    )
    if ($null -ne $PayloadInput) {
        $arguments += '--stdin-base64'
    }

    $result = Invoke-Adb -Arguments $arguments -StandardInputText $PayloadInput -BinaryOutput
    $stderr = [string]$result.Stderr
    # Some platform-tools revisions keep the remote stderr channel separate,
    # while older shell transports merge it into stdout.  The helper summary
    # contains metadata only, so it is safe to search both captured streams;
    # neither stream is ever emitted to the caller.
    $stdoutText = [System.Text.Encoding]::UTF8.GetString([byte[]]$result.Stdout)
    $diagnosticText = $stderr + "`n" + $stdoutText
    $summary = $null
    $diagnosticLines = @($diagnosticText -split "`r?`n")
    for ($lineIndex = $diagnosticLines.Count - 1; $lineIndex -ge 0; $lineIndex--) {
        $candidate = $diagnosticLines[$lineIndex].Trim()
        if ($candidate.Length -eq 0 -or -not $candidate.StartsWith('{')) {
            continue
        }
        try {
            $parsed = $candidate | ConvertFrom-Json -Depth 10 -ErrorAction Stop
            if ($null -ne $parsed.ok) {
                $summary = $parsed
                break
            }
        } catch {
            # Diagnostics are intentionally not surfaced; try another line.
        }
    }
    if ($result.ExitCode -ne 0 -or $null -eq $summary -or $summary.ok -ne $true) {
        $safeCode = 'no_validated_error_code'
        if ($null -ne $summary -and $null -ne $summary.PSObject.Properties['error'] -and
            [string]$summary.error -cmatch '^[a-zA-Z_]{1,160}$') {
            $safeCode = [string]$summary.error
        }
        Throw-Safe "Device migration helper failed in $HelperMode mode ($safeCode)."
    }
    return [pscustomobject]@{
        StdoutBytes = [byte[]]$result.Stdout
        Summary = $summary
    }
}

function Get-ExportPayload {
    param([Parameter(Mandatory = $true)][byte[]]$StdoutBytes)

    if ($StdoutBytes.Length -gt $MaximumPayloadBytes + 512) {
        Throw-Safe 'The device export payload is too large.'
    }
    try {
        # Windows ADB may translate line terminators even without a PTY.
        # Normalize only CRLF; framing remains exact and bare CR is rejected.
        $raw = [System.Text.Encoding]::UTF8.GetString($StdoutBytes).Replace("`r`n", "`n")
        $prefix = $FrameStart + "`n"
        $suffix = "`n" + $FrameEnd + "`n"
        if (-not $raw.StartsWith($prefix, [System.StringComparison]::Ordinal) -or
            -not $raw.EndsWith($suffix, [System.StringComparison]::Ordinal)) {
            $startOffset = $raw.IndexOf($FrameStart, [System.StringComparison]::Ordinal)
            $endOffset = $raw.LastIndexOf($FrameEnd, [System.StringComparison]::Ordinal)
            $crCount = ($raw.ToCharArray() | Where-Object { $_ -eq "`r" } | Measure-Object).Count
            Throw-Safe "The device export frame is not exact (length=$($raw.Length), start=$startOffset, end=$endOffset, CR=$crCount)."
        }
        $json = $raw.Substring($prefix.Length, $raw.Length - $prefix.Length - $suffix.Length)
        if ($json.IndexOf("`r") -ge 0 -or $json.IndexOf("`n") -ge 0) {
            Throw-Safe 'The device export frame contains unexpected line breaks.'
        }
        if ($json.Length -eq 0 -or $json.Length -gt $MaximumPayloadBytes) {
            Throw-Safe 'The device export payload is empty or too large.'
        }
        $payload = $json | ConvertFrom-Json -Depth 30 -ErrorAction Stop
        return [pscustomobject]@{
            Json = $json
            Payload = $payload
        }
    } catch {
        if ($_.Exception -is [System.InvalidOperationException]) {
            throw
        }
        Throw-Safe 'The device export payload is not valid JSON.'
    }
}

function Get-PayloadMetadata {
    param([Parameter(Mandatory = $true)]$Payload)

    try {
        if ($Payload.format -ne 1 -or $Payload.package -ne $PackageName) {
            Throw-Safe 'Pairing payload identity or format is invalid.'
        }
        $devices = @($Payload.devices)
        if ($devices.Count -gt 10) {
            Throw-Safe 'Pairing payload contains too many devices.'
        }
        $sharedCount = @($devices | Where-Object { $_.sharedGroup -eq $true }).Count
        $activePresent = $false
        if ($null -ne $Payload.activeComputerId) {
            $activePresent = @($devices | Where-Object {
                $_.computerId -eq [string]$Payload.activeComputerId
            }).Count -eq 1
        }
        return [pscustomobject]@{
            DeviceCount = $devices.Count
            SharedGroupCount = $sharedCount
            ActiveComputerPresent = $activePresent
        }
    } catch {
        if ($_.Exception -is [System.InvalidOperationException]) {
            throw
        }
        Throw-Safe 'Pairing payload metadata is invalid.'
    }
}

function Get-CurrentUserSid {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    if ($null -eq $identity.User) {
        Throw-Safe 'Could not resolve the current Windows user SID.'
    }
    return $identity.User.Value
}

function Set-CurrentUserOnlyAcl {
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        $sid = [System.Security.Principal.SecurityIdentifier]::new((Get-CurrentUserSid))
        # Start from the file's existing security descriptor.  Constructing a
        # blank FileSecurity object makes Set-Acl attempt to write an owner or
        # other descriptor fields the current user does not own, which can
        # raise PrivilegeNotHeldException.  Only replace the DACL below.
        $security = Get-Acl -LiteralPath $Path -ErrorAction Stop
        $security.SetAccessRuleProtection($true, $false)
        $existingRules = @($security.GetAccessRules(
            $true,
            $true,
            [System.Security.Principal.SecurityIdentifier]))
        foreach ($existingRule in $existingRules) {
            [void]$security.RemoveAccessRuleSpecific($existingRule)
        }
        $accessRule = [System.Security.AccessControl.FileSystemAccessRule]::new(
            $sid,
            [System.Security.AccessControl.FileSystemRights]::FullControl,
            [System.Security.AccessControl.InheritanceFlags]::None,
            [System.Security.AccessControl.PropagationFlags]::None,
            [System.Security.AccessControl.AccessControlType]::Allow)
        [void]$security.AddAccessRule($accessRule)
        Set-Acl -LiteralPath $Path -AclObject $security -ErrorAction Stop
    } catch {
        Throw-Safe 'Could not restrict the backup ACL to the current user.'
    }
}

function Assert-CurrentUserOnlyAcl {
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        $sid = Get-CurrentUserSid
        $security = Get-Acl -LiteralPath $Path -ErrorAction Stop
        $rules = @($security.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]))
        if ($rules.Count -ne 1 -or -not $security.AreAccessRulesProtected) {
            Throw-Safe 'The backup ACL is not restricted to the current user.'
        }
        $unexpected = @($rules | Where-Object {
            $_.IdentityReference.Value -ne $sid
        })
        if ($unexpected.Count -ne 0 -or
            $rules[0].AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) {
            Throw-Safe 'The backup ACL is not restricted to the current user.'
        }
    } catch {
        if ($_.Exception -is [System.InvalidOperationException]) {
            throw
        }
        Throw-Safe 'Could not verify the backup ACL.'
    }
}

function Write-ProtectedBackup {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$PayloadJson
    )

    $payloadBytes = $null
    $sealedBytes = $null
    $tempPath = $null
    $roundtripBytes = $null
    $readbackBytes = $null
    try {
        Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
        $payloadBytes = [System.Text.Encoding]::UTF8.GetBytes($PayloadJson)
        $sealedBytes = [System.Security.Cryptography.ProtectedData]::Protect(
            $payloadBytes,
            $null,
            [System.Security.Cryptography.DataProtectionScope]::CurrentUser)

        $fullPath = [System.IO.Path]::GetFullPath($Path)
        $parent = [System.IO.Path]::GetDirectoryName($fullPath)
        if ([string]::IsNullOrWhiteSpace($parent)) {
            Throw-Safe 'Backup path has no parent directory.'
        }
        [void][System.IO.Directory]::CreateDirectory($parent)
        if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
            Throw-Safe 'The protected pairing backup already exists; refusing to overwrite it.'
        }
        $tempPath = Join-Path $parent ('.' + [System.IO.Path]::GetFileName($fullPath) + '.' +
            [guid]::NewGuid().ToString('N') + '.tmp')
        $stream = [System.IO.FileStream]::new(
            $tempPath,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        try {
            $stream.Write($sealedBytes, 0, $sealedBytes.Length)
            $stream.Flush($true)
        } finally {
            $stream.Dispose()
        }
        Set-CurrentUserOnlyAcl $tempPath

        [System.IO.File]::Move($tempPath, $fullPath)
        $tempPath = $null
        # A same-directory rename retains the restricted ACL. Re-read it;
        # do not rewrite the security descriptor after publication.
        Assert-CurrentUserOnlyAcl $fullPath
        $readbackBytes = [System.IO.File]::ReadAllBytes($fullPath)
        $roundtripBytes = [System.Security.Cryptography.ProtectedData]::Unprotect(
            $readbackBytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        if ([Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($roundtripBytes)) -cne
            [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($payloadBytes))) {
            Throw-Safe 'Protected backup readback did not match the exported payload.'
        }
        return [pscustomobject]@{
            Path = $fullPath
            PayloadBytes = $payloadBytes.Length
            EncryptedBytes = $sealedBytes.Length
        }
    } catch {
        if ($_.Exception -is [System.InvalidOperationException]) {
            throw
        }
        Throw-Safe 'Could not atomically write the protected pairing backup.'
    } finally {
        if ($tempPath -and (Test-Path -LiteralPath $tempPath -PathType Leaf)) {
            Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
        }
        if ($payloadBytes) {
            [Array]::Clear($payloadBytes, 0, $payloadBytes.Length)
        }
        if ($sealedBytes) {
            [Array]::Clear($sealedBytes, 0, $sealedBytes.Length)
        }
        if ($roundtripBytes) { [Array]::Clear($roundtripBytes, 0, $roundtripBytes.Length) }
        if ($readbackBytes) { [Array]::Clear($readbackBytes, 0, $readbackBytes.Length) }
    }
}

function Read-ProtectedBackup {
    param([Parameter(Mandatory = $true)][string]$Path)

    $sealedBytes = $null
    $payloadBytes = $null
    try {
        $fullPath = [System.IO.Path]::GetFullPath($Path)
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            Throw-Safe 'Protected pairing backup does not exist.'
        }
        Assert-CurrentUserOnlyAcl $fullPath
        Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
        $sealedBytes = [System.IO.File]::ReadAllBytes($fullPath)
        $payloadBytes = [System.Security.Cryptography.ProtectedData]::Unprotect(
            $sealedBytes,
            $null,
            [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        $json = [System.Text.Encoding]::UTF8.GetString($payloadBytes)
        $payload = $json | ConvertFrom-Json -Depth 30 -ErrorAction Stop
        $metadata = Get-PayloadMetadata $payload
        return [pscustomobject]@{
            Path = $fullPath
            Json = $json
            Payload = $payload
            DeviceCount = $metadata.DeviceCount
            SharedGroupCount = $metadata.SharedGroupCount
            ActiveComputerPresent = $metadata.ActiveComputerPresent
            PayloadBytes = $payloadBytes.Length
            EncryptedBytes = $sealedBytes.Length
            RoundtripVerified = $true
        }
    } catch {
        if ($_.Exception -is [System.InvalidOperationException]) {
            throw
        }
        Throw-Safe 'Could not decrypt or validate the protected pairing backup.'
    } finally {
        if ($sealedBytes) {
            [Array]::Clear($sealedBytes, 0, $sealedBytes.Length)
        }
        if ($payloadBytes) {
            [Array]::Clear($payloadBytes, 0, $payloadBytes.Length)
        }
    }
}

function New-DeviceInputFrame {
    param([Parameter(Mandatory = $true)][string]$PayloadJson)

    $frame = $FrameStart + "`n" + $PayloadJson + "`n" + $FrameEnd + "`n"
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($frame)
    try {
        return [Convert]::ToBase64String($bytes)
    } finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

function Assert-AppStopped {
    $pidResult = Invoke-Adb -Arguments @('-s', $Serial, 'shell', 'pidof', $PackageName)
    if ($pidResult.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace([string]$pidResult.Stdout)) {
        Throw-Safe 'The PhoneDeck app must be stopped before migration verification/import.'
    }
    if ($pidResult.ExitCode -ne 0) {
        # pidof returns exit 1 when there is no matching process.  Any other
        # failure is treated as unknown rather than assuming the app is off.
        $trimmed = ([string]$pidResult.Stdout).Trim()
        $diagnostic = ([string]$pidResult.Stderr).Trim()
        if ($trimmed.Length -ne 0 -or $pidResult.ExitCode -ne 1 -or $diagnostic.Length -ne 0) {
            Throw-Safe 'Could not determine whether the PhoneDeck app is stopped.'
        }
    }
}

function Assert-TestEmulator {
    if ($Serial -notmatch '^emulator-[0-9]+$') {
        Throw-Safe 'Import requires an emulator serial matching emulator-<number>.'
    }
    $qemu = Invoke-Adb -Arguments @('-s', $Serial, 'shell', 'getprop', 'ro.kernel.qemu')
    if ($qemu.ExitCode -ne 0 -or ([string]$qemu.Stdout).Trim() -ne '1') {
        Throw-Safe 'Import target is not verified as an Android emulator.'
    }
    Assert-AppStopped
}

function Remove-OwnedBuildRoot {
    if (-not $script:BuildRoot) {
        return
    }
    try {
        $candidate = [System.IO.Path]::GetFullPath($script:BuildRoot)
        $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        $tempRootPrefix = $tempRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        $leaf = [System.IO.Path]::GetFileName($candidate)
        $ownedName = $leaf -match '^phonedeck-pairing-migration-[0-9a-f]{32}$'
        if (-not $script:BuildRootCreated -or -not $ownedName -or
            -not $candidate.StartsWith($tempRootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            return
        }
        Remove-Item -LiteralPath $candidate -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $candidate -PathType Container) {
            Throw-Safe 'Local helper build cleanup failed or left a residual directory.'
        }
    } catch {
        if ($_.Exception -is [System.InvalidOperationException]) {
            throw
        }
        Throw-Safe 'Local helper build cleanup could not be verified.'
    }
}

function Remove-DeviceHelper {
    if (-not $script:DeviceTouched) {
        return [pscustomobject]@{ Verified = $true }
    }
    $failed = $false

    $appCleanup = @('-s', $Serial, 'shell', 'run-as', $PackageName, 'rm', '-f', $AppHelperRelativePath)
    try {
        $result = Invoke-Adb -Arguments $appCleanup
        if ($result.ExitCode -ne 0) {
            $failed = $true
        } else {
            $check = Invoke-Adb -Arguments @(
                '-s', $Serial, 'shell', 'run-as', $PackageName, 'test', '!', '-e',
                $AppHelperRelativePath
            )
            if ($check.ExitCode -ne 0) {
                $failed = $true
            }
        }
    } catch {
        $failed = $true
    }
    if ($script:RemoteTempPath) {
        try {
            $result = Invoke-Adb -Arguments @(
                '-s', $Serial, 'shell', 'rm', '-f', $script:RemoteTempPath
            )
            if ($result.ExitCode -ne 0) {
                $failed = $true
            } else {
                $check = Invoke-Adb -Arguments @(
                    '-s', $Serial, 'shell', 'test', '!', '-e',
                    $script:RemoteTempPath
                )
                if ($check.ExitCode -ne 0) {
                    $failed = $true
                }
            }
        } catch {
            $failed = $true
        }
    }
    if ($failed) {
        Throw-Safe 'Device helper cleanup failed or left a residual helper path.'
    }
    return [pscustomobject]@{ Verified = $true }
}

$helperBuild = $null
$stage = $null
$finalResult = $null
try {
    $script:AdbPath = Resolve-ExecutablePath $Adb
    $javaRoot = Resolve-DirectoryPath $JavaHome
    $sdkRoot = Resolve-DirectoryPath $AndroidSdk

    if ($Mode -eq 'Export') {
        Assert-AppStopped
        $helperBuild = New-HelperBuild -JavaRoot $javaRoot -SdkRoot $sdkRoot
        $stage = Stage-Helper $helperBuild
        $device = Invoke-DeviceHelper -HelperMode export
        $export = Get-ExportPayload $device.StdoutBytes
        $metadata = Get-PayloadMetadata $export.Payload
        $protected = Write-ProtectedBackup -Path $BackupFile -PayloadJson $export.Json
        $finalResult = [pscustomobject]@{
            Mode = $Mode
            Serial = $Serial
            BackupFile = $protected.Path
            DeviceCount = $metadata.DeviceCount
            SharedGroupCount = $metadata.SharedGroupCount
            ActiveComputerPresent = $metadata.ActiveComputerPresent
            PayloadBytes = $protected.PayloadBytes
            EncryptedBytes = $protected.EncryptedBytes
            HelperSha256 = $helperBuild.Sha256
            StagingVerified = $stage.Verified
            RoundtripVerified = $true
        }
    } elseif ($Mode -eq 'Verify') {
        $backup = Read-ProtectedBackup -Path $BackupFile
        Assert-AppStopped
        $helperBuild = New-HelperBuild -JavaRoot $javaRoot -SdkRoot $sdkRoot
        $stage = Stage-Helper $helperBuild
        $frameInput = New-DeviceInputFrame -PayloadJson $backup.Json
        $device = Invoke-DeviceHelper -HelperMode verify -PayloadInput $frameInput
        $finalResult = [pscustomobject]@{
            Mode = $Mode
            Serial = $Serial
            BackupFile = $backup.Path
            DeviceCount = $backup.DeviceCount
            SharedGroupCount = $backup.SharedGroupCount
            ActiveComputerPresent = $backup.ActiveComputerPresent
            PayloadBytes = $backup.PayloadBytes
            EncryptedBytes = $backup.EncryptedBytes
            HelperSha256 = $helperBuild.Sha256
            StagingVerified = $stage.Verified
            RoundtripVerified = $backup.RoundtripVerified
            DeviceVerified = $true
        }
    } else {
        $backup = Read-ProtectedBackup -Path $BackupFile
        Assert-TestEmulator
        $helperBuild = New-HelperBuild -JavaRoot $javaRoot -SdkRoot $sdkRoot
        $stage = Stage-Helper $helperBuild
        $frameInput = New-DeviceInputFrame -PayloadJson $backup.Json
        $device = Invoke-DeviceHelper -HelperMode import -PayloadInput $frameInput
        $finalResult = [pscustomobject]@{
            Mode = $Mode
            Serial = $Serial
            BackupFile = $backup.Path
            DeviceCount = $backup.DeviceCount
            SharedGroupCount = $backup.SharedGroupCount
            ActiveComputerPresent = $backup.ActiveComputerPresent
            PayloadBytes = $backup.PayloadBytes
            EncryptedBytes = $backup.EncryptedBytes
            HelperSha256 = $helperBuild.Sha256
            StagingVerified = $stage.Verified
            RoundtripVerified = $backup.RoundtripVerified
            DeviceImported = $true
            TestEmulator = $true
        }
    }
} finally {
    [void](Remove-DeviceHelper)
    Remove-OwnedBuildRoot
}

$finalResult | ConvertTo-Json -Compress
