<#
.SYNOPSIS
    Isolated pure-data tests for Assert-ReleaseVersions.ps1.

.DESCRIPTION
    Builds unique temp fixtures and actually executes the assert script. Covers
    fractional/bool/string integer bypasses, SemVer/injection rejects, props field
    drift (including Version/AssemblyVersion), validate-only immutability, Sync
    repair, and missing fields. Fixtures are retained (no recursive delete).
    Does not run product builds.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "Test-ReleaseVersions.puredata.ps1 requires PowerShell 7+. Current: $($PSVersionTable.PSVersion)"
}

$here = $PSScriptRoot
$assertScript = Join-Path $here 'Assert-ReleaseVersions.ps1'
if (-not (Test-Path -LiteralPath $assertScript)) {
    throw "Missing assert script: $assertScript"
}

$failed = 0
$passed = 0
$fixtureRoots = New-Object System.Collections.Generic.List[string]

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Content
    )
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Content, $utf8)
}

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

function New-FixtureRoot {
    $root = Join-Path $env:TEMP ("PhoneDeck-B02-versioning-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    [void]$script:fixtureRoots.Add($root)
    Write-Host "FIXTURE: $root"
    return $root
}

function New-ValidDescriptorObject {
    return [ordered]@{
        schemaVersion = 1
        windows = [ordered]@{
            version = '1.6.0-dev.11'
            sequence = 23
        }
        android = [ordered]@{
            versionName = '1.6.0-dev.17'
            versionCode = 23
        }
        macos = [ordered]@{
            version = '2.0.0-dev.3'
            bundleShortVersion = '2.0.0'
            bundleVersion = '2'
            historicBundleMapping = [ordered]@{
                healthVersion = '2.0.0-dev.3'
                bundleShortVersion = '2.0.0'
                bundleVersion = '2'
                note = 'historic Mac mapping preserved for fixture'
            }
        }
        console = [ordered]@{
            follows = 'windows.version'
            informationalVersion = '1.6.0-dev.11'
        }
    }
}

function Write-Descriptor {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Object
    )
    $json = $Object | ConvertTo-Json -Depth 32
    Write-Utf8NoBom -Path $Path -Content $json
}

function Write-RawDescriptor {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Json
    )
    Write-Utf8NoBom -Path $Path -Content $Json
}

function Get-FileSha256 {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function New-ProductTree {
    param(
        [Parameter(Mandatory)][string]$Root,
        [string]$WindowsVersion = '1.6.0-dev.11',
        [long]$WindowsSequence = 23,
        [string]$AndroidVersionName = '1.6.0-dev.17',
        [long]$AndroidVersionCode = 23,
        [string]$MacVersion = '2.0.0-dev.3',
        [string]$MacBundleShort = '2.0.0',
        [string]$MacBundleVersion = '2',
        [switch]$WithGeneratedProps
    )

    $fleet = @"
internal sealed class FleetUpdates
{
    internal const string Version = "$WindowsVersion";
    internal const long Sequence = $WindowsSequence;
}
"@
    Write-Utf8NoBom -Path (Join-Path $Root 'windows/PhoneDeck.Server/FleetUpdates.cs') -Content $fleet

    $bt = @"
private static void SendHello(IntPtr socket, string computerId, string displayName)
{
    var json = JsonSerializer.Serialize(new
    {
        type = "hello",
        name = "PhoneDeck",
        version = "$WindowsVersion",
        protocolVersion = 2
    });
}
"@
    Write-Utf8NoBom -Path (Join-Path $Root 'windows/PhoneDeck.Server/BluetoothReceiver.cs') -Content $bt

    $gradle = @"
android {
    defaultConfig {
        versionCode $AndroidVersionCode
        versionName "$AndroidVersionName"
    }
}
"@
    Write-Utf8NoBom -Path (Join-Path $Root 'android/app/build.gradle') -Content $gradle

    $macProgram = @"
    return Results.Ok(new
    {
        ok = true,
        name = "PhoneDeck",
        version = "$MacVersion",
        protocolVersion = 2
    });
"@
    Write-Utf8NoBom -Path (Join-Path $Root 'macos/PhoneDeck.Receiver/Program.cs') -Content $macProgram

    $plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<plist version="1.0">
<dict>
    <key>CFBundleShortVersionString</key>
    <string>$MacBundleShort</string>
    <key>CFBundleVersion</key>
    <string>$MacBundleVersion</string>
</dict>
</plist>
"@
    Write-Utf8NoBom -Path (Join-Path $Root 'macos/PhoneDeck.Receiver/Packaging/Info.plist') -Content $plist

    if ($WithGeneratedProps) {
        $winProps = @"
<!--
  GENERATED FILE — do not edit by hand.
  Source of truth: work/phone-deck/release-versions.json
  Regenerated by: pwsh -File scripts/versioning/Assert-ReleaseVersions.ps1 -Sync
-->
<Project>
  <PropertyGroup>
    <Version>1.6.0</Version>
    <AssemblyVersion>1.6.0.0</AssemblyVersion>
    <FileVersion>1.6.0.$WindowsSequence</FileVersion>
    <InformationalVersion>$WindowsVersion</InformationalVersion>
    <PhoneDeckReleaseSequence>$WindowsSequence</PhoneDeckReleaseSequence>
  </PropertyGroup>
</Project>
"@
        Write-Utf8NoBom -Path (Join-Path $Root 'windows/Directory.Build.props') -Content $winProps

        $macProps = @"
<!--
  GENERATED FILE — do not edit by hand.
  Source of truth: work/phone-deck/release-versions.json
  Regenerated by: pwsh -File scripts/versioning/Assert-ReleaseVersions.ps1 -Sync
-->
<Project>
  <PropertyGroup>
    <Version>$MacBundleShort</Version>
    <AssemblyVersion>$MacBundleShort.0</AssemblyVersion>
    <FileVersion>2.0.0.$MacBundleVersion</FileVersion>
    <InformationalVersion>$MacVersion</InformationalVersion>
    <PhoneDeckBundleVersion>$MacBundleVersion</PhoneDeckBundleVersion>
  </PropertyGroup>
</Project>
"@
        Write-Utf8NoBom -Path (Join-Path $Root 'macos/Directory.Build.props') -Content $macProps
    }
}

function Invoke-Assert {
    param(
        [Parameter(Mandatory)][string]$PhoneDeckRoot,
        [switch]$Sync
    )
    $argList = @('-NoProfile', '-File', $assertScript, '-PhoneDeckRoot', $PhoneDeckRoot)
    if ($Sync) { $argList += '-Sync' }
    $output = & pwsh @argList 2>&1
    $code = $LASTEXITCODE
    if ($null -eq $code) { $code = -1 }
    return [pscustomobject]@{
        ExitCode = [int]$code
        Output = ($output | Out-String)
    }
}

# --- 1) invalid schemaVersion integer out of range ---
$fx1 = New-FixtureRoot
New-ProductTree -Root $fx1 -WithGeneratedProps
$badSchema = New-ValidDescriptorObject
$badSchema.schemaVersion = 99
Write-Descriptor -Path (Join-Path $fx1 'release-versions.json') -Object $badSchema
$r1 = Invoke-Assert -PhoneDeckRoot $fx1
Assert-True -Condition ($r1.ExitCode -ne 0) -Message 'invalid schemaVersion exits nonzero'
Assert-True -Condition ($r1.Output -match 'schemaVersion') -Message 'invalid schemaVersion mentions schemaVersion'

# --- 2) missing required field ---
$fx2 = New-FixtureRoot
New-ProductTree -Root $fx2 -WithGeneratedProps
$missing = New-ValidDescriptorObject
$missing.windows.Remove('version')
Write-Descriptor -Path (Join-Path $fx2 'release-versions.json') -Object $missing
$r2 = Invoke-Assert -PhoneDeckRoot $fx2
Assert-True -Condition ($r2.ExitCode -ne 0) -Message 'missing windows.version exits nonzero'
Assert-True -Condition ($r2.Output -match 'windows\.version|Missing required') -Message 'missing field error is explicit'

# --- 3) missing historicBundleMapping ---
$fx3 = New-FixtureRoot
New-ProductTree -Root $fx3 -WithGeneratedProps
$noMap = New-ValidDescriptorObject
$noMap.macos.Remove('historicBundleMapping')
Write-Descriptor -Path (Join-Path $fx3 'release-versions.json') -Object $noMap
$r3 = Invoke-Assert -PhoneDeckRoot $fx3
Assert-True -Condition ($r3.ExitCode -ne 0) -Message 'missing historicBundleMapping exits nonzero'
Assert-True -Condition ($r3.Output -match 'historicBundleMapping') -Message 'missing historic mapping named in error'

# --- 4) source drift (validate-only, no Sync) + read-only no mutation ---
$fx4 = New-FixtureRoot
New-ProductTree -Root $fx4 -WindowsVersion '9.9.9-dev.1' -WithGeneratedProps
Write-Descriptor -Path (Join-Path $fx4 'release-versions.json') -Object (New-ValidDescriptorObject)
$fleetPath = Join-Path $fx4 'windows/PhoneDeck.Server/FleetUpdates.cs'
$btPath = Join-Path $fx4 'windows/PhoneDeck.Server/BluetoothReceiver.cs'
$gradlePath = Join-Path $fx4 'android/app/build.gradle'
$winPropsPath = Join-Path $fx4 'windows/Directory.Build.props'
$hashBefore = @{
    fleet = Get-FileSha256 $fleetPath
    bt = Get-FileSha256 $btPath
    gradle = Get-FileSha256 $gradlePath
    winProps = Get-FileSha256 $winPropsPath
}
$r4 = Invoke-Assert -PhoneDeckRoot $fx4
Assert-True -Condition ($r4.ExitCode -ne 0) -Message 'source drift exits nonzero in validate-only mode'
Assert-True -Condition ($r4.Output -match 'drift|!=') -Message 'source drift reports mismatch details'
Assert-True -Condition ($r4.Output -match 'Sync') -Message 'validate-only drift hints at -Sync'
Assert-True -Condition ((Get-FileSha256 $fleetPath) -eq $hashBefore.fleet) -Message 'validate-only does not mutate FleetUpdates.cs'
Assert-True -Condition ((Get-FileSha256 $btPath) -eq $hashBefore.bt) -Message 'validate-only does not mutate BluetoothReceiver.cs'
Assert-True -Condition ((Get-FileSha256 $gradlePath) -eq $hashBefore.gradle) -Message 'validate-only does not mutate build.gradle'
Assert-True -Condition ((Get-FileSha256 $winPropsPath) -eq $hashBefore.winProps) -Message 'validate-only does not mutate windows Directory.Build.props'

# --- 5) matching tree + generated props passes ---
$fx5 = New-FixtureRoot
New-ProductTree -Root $fx5 -WithGeneratedProps
Write-Descriptor -Path (Join-Path $fx5 'release-versions.json') -Object (New-ValidDescriptorObject)
$r5 = Invoke-Assert -PhoneDeckRoot $fx5
Assert-True -Condition ($r5.ExitCode -eq 0) -Message 'matching fixture validates successfully'
Assert-True -Condition ($r5.Output -match 'OK: release versions consistent') -Message 'matching fixture prints OK'

# --- 6) Sync repairs drift then validate passes ---
$fx6 = New-FixtureRoot
New-ProductTree -Root $fx6 -WindowsVersion '0.0.0-dev.0' -WindowsSequence 1 -AndroidVersionCode 1 -MacBundleVersion '9'
Write-Descriptor -Path (Join-Path $fx6 'release-versions.json') -Object (New-ValidDescriptorObject)
$r6a = Invoke-Assert -PhoneDeckRoot $fx6
Assert-True -Condition ($r6a.ExitCode -ne 0) -Message 'pre-Sync drifted fixture fails validate'
$r6b = Invoke-Assert -PhoneDeckRoot $fx6 -Sync
Assert-True -Condition ($r6b.ExitCode -eq 0) -Message 'Sync repairs drift and exits 0'
$r6c = Invoke-Assert -PhoneDeckRoot $fx6
Assert-True -Condition ($r6c.ExitCode -eq 0) -Message 'post-Sync validate-only still passes'

$fleetText = [System.IO.File]::ReadAllText((Join-Path $fx6 'windows/PhoneDeck.Server/FleetUpdates.cs'))
Assert-True -Condition ($fleetText -match 'Version = "1\.6\.0-dev\.11"') -Message 'Sync wrote FleetUpdates windows.version'
Assert-True -Condition ($fleetText -match 'Sequence = 23') -Message 'Sync wrote FleetUpdates windows.sequence'

$winProps = [System.IO.File]::ReadAllText((Join-Path $fx6 'windows/Directory.Build.props'))
Assert-True -Condition ($winProps -match 'GENERATED FILE') -Message 'Sync marked windows props as GENERATED'
Assert-True -Condition ($winProps -match '<Version>1\.6\.0</Version>') -Message 'Sync wrote windows Version'
Assert-True -Condition ($winProps -match '<AssemblyVersion>1\.6\.0\.0</AssemblyVersion>') -Message 'Sync wrote windows AssemblyVersion'
Assert-True -Condition ($winProps -match '<FileVersion>1\.6\.0\.23</FileVersion>') -Message 'Sync wrote windows FileVersion'
Assert-True -Condition ($winProps -match '<InformationalVersion>1\.6\.0-dev\.11</InformationalVersion>') -Message 'Sync wrote windows InformationalVersion'

$macProps = [System.IO.File]::ReadAllText((Join-Path $fx6 'macos/Directory.Build.props'))
Assert-True -Condition ($macProps -match '<Version>2\.0\.0</Version>') -Message 'Sync wrote macos Version'
Assert-True -Condition ($macProps -match '<AssemblyVersion>2\.0\.0\.0</AssemblyVersion>') -Message 'Sync wrote macos AssemblyVersion'
Assert-True -Condition ($macProps -match '<InformationalVersion>2\.0\.0-dev\.3</InformationalVersion>') -Message 'Sync wrote macos InformationalVersion'
Assert-True -Condition ($macProps -match '<PhoneDeckBundleVersion>2</PhoneDeckBundleVersion>') -Message 'Sync preserved historic Mac bundleVersion=2'

# --- 7) fractional sequence 23.1 must NOT round via [long] ---
$fx7 = New-FixtureRoot
New-ProductTree -Root $fx7 -WithGeneratedProps
$frac = New-ValidDescriptorObject
$frac.windows.sequence = 23.1
Write-Descriptor -Path (Join-Path $fx7 'release-versions.json') -Object $frac
$raw7 = [System.IO.File]::ReadAllText((Join-Path $fx7 'release-versions.json'))
Assert-True -Condition ($raw7 -match '23\.1') -Message 'fixture JSON retains fractional sequence 23.1'
$r7 = Invoke-Assert -PhoneDeckRoot $fx7
Assert-True -Condition ($r7.ExitCode -ne 0) -Message 'fractional windows.sequence=23.1 exits nonzero (no [long] rounding bypass)'
Assert-True -Condition ($r7.Output -match 'windows\.sequence|integer|fractional|Double|type=') -Message 'fractional sequence error names type/integer issue'

# --- 8) bool schemaVersion must not coerce to 1 ---
$fx8 = New-FixtureRoot
New-ProductTree -Root $fx8 -WithGeneratedProps
$boolSchema = New-ValidDescriptorObject
$boolSchema.schemaVersion = $true
Write-Descriptor -Path (Join-Path $fx8 'release-versions.json') -Object $boolSchema
$r8 = Invoke-Assert -PhoneDeckRoot $fx8
Assert-True -Condition ($r8.ExitCode -ne 0) -Message 'bool schemaVersion exits nonzero'
Assert-True -Condition ($r8.Output -match 'schemaVersion|bool|Boolean|integer') -Message 'bool schemaVersion error is explicit'

# --- 9) string-typed sequence must not coerce ---
$fx9 = New-FixtureRoot
New-ProductTree -Root $fx9 -WithGeneratedProps
$strSeq = New-ValidDescriptorObject
$strSeq.windows.sequence = '23'
Write-Descriptor -Path (Join-Path $fx9 'release-versions.json') -Object $strSeq
$r9 = Invoke-Assert -PhoneDeckRoot $fx9
Assert-True -Condition ($r9.ExitCode -ne 0) -Message 'string windows.sequence exits nonzero'
Assert-True -Condition ($r9.Output -match 'windows\.sequence|String|integer') -Message 'string sequence error names type issue'

# --- 10) bool versionCode / string versionCode ---
$fx10 = New-FixtureRoot
New-ProductTree -Root $fx10 -WithGeneratedProps
$boolCode = New-ValidDescriptorObject
$boolCode.android.versionCode = $true
Write-Descriptor -Path (Join-Path $fx10 'release-versions.json') -Object $boolCode
$r10 = Invoke-Assert -PhoneDeckRoot $fx10
Assert-True -Condition ($r10.ExitCode -ne 0) -Message 'bool android.versionCode exits nonzero'
Assert-True -Condition ($r10.Output -match 'android\.versionCode|bool|Boolean|integer') -Message 'bool android.versionCode error names type issue'

$fx10b = New-FixtureRoot
New-ProductTree -Root $fx10b -WithGeneratedProps
$strCode = New-ValidDescriptorObject
$strCode.android.versionCode = '23'
Write-Descriptor -Path (Join-Path $fx10b 'release-versions.json') -Object $strCode
$r10b = Invoke-Assert -PhoneDeckRoot $fx10b
Assert-True -Condition ($r10b.ExitCode -ne 0) -Message 'string android.versionCode exits nonzero'
Assert-True -Condition ($r10b.Output -match 'android\.versionCode|String|integer') -Message 'string android.versionCode error names type issue'

# --- 11) android.versionCode above Play limit ---
$fx11 = New-FixtureRoot
New-ProductTree -Root $fx11 -WithGeneratedProps
$bigAndroid = New-ValidDescriptorObject
$bigAndroid.android.versionCode = 2100000001
Write-Descriptor -Path (Join-Path $fx11 'release-versions.json') -Object $bigAndroid
$r11 = Invoke-Assert -PhoneDeckRoot $fx11
Assert-True -Condition ($r11.ExitCode -ne 0) -Message 'android.versionCode > 2100000000 exits nonzero'
Assert-True -Condition ($r11.Output -match '2100000000|android\.versionCode') -Message 'android versionCode limit mentioned'

# --- 12) sequence / bundleVersion above FileVersion WORD limit ---
$fx12 = New-FixtureRoot
New-ProductTree -Root $fx12 -WithGeneratedProps
$bigSeq = New-ValidDescriptorObject
$bigSeq.windows.sequence = 65536
Write-Descriptor -Path (Join-Path $fx12 'release-versions.json') -Object $bigSeq
$r12 = Invoke-Assert -PhoneDeckRoot $fx12
Assert-True -Condition ($r12.ExitCode -ne 0) -Message 'windows.sequence > 65535 exits nonzero'
Assert-True -Condition ($r12.Output -match '65535|windows\.sequence|FileVersion|WORD') -Message 'FileVersion WORD limit explained for sequence'

$fx12b = New-FixtureRoot
New-ProductTree -Root $fx12b -WithGeneratedProps
$bigBundle = New-ValidDescriptorObject
$bigBundle.macos.bundleVersion = '65536'
$bigBundle.macos.historicBundleMapping.bundleVersion = '65536'
Write-Descriptor -Path (Join-Path $fx12b 'release-versions.json') -Object $bigBundle
$r12b = Invoke-Assert -PhoneDeckRoot $fx12b
Assert-True -Condition ($r12b.ExitCode -ne 0) -Message 'macos.bundleVersion > 65535 exits nonzero'
Assert-True -Condition ($r12b.Output -match '65535|bundleVersion|FileVersion|WORD') -Message 'FileVersion WORD limit explained for bundleVersion'

# --- 13) unsafe SemVer / XML injection in version string ---
$fx13 = New-FixtureRoot
New-ProductTree -Root $fx13 -WithGeneratedProps
$unsafe = New-ValidDescriptorObject
$unsafe.windows.version = '1.6.0-dev.11</InformationalVersion><Hack>'
$unsafe.console.informationalVersion = '1.6.0-dev.11</InformationalVersion><Hack>'
Write-Descriptor -Path (Join-Path $fx13 'release-versions.json') -Object $unsafe
$r13 = Invoke-Assert -PhoneDeckRoot $fx13
Assert-True -Condition ($r13.ExitCode -ne 0) -Message 'XML-injection version text exits nonzero'
Assert-True -Condition ($r13.Output -match 'unsafe|SemVer|windows\.version') -Message 'unsafe version error mentions SemVer/unsafe'

$fx13b = New-FixtureRoot
New-ProductTree -Root $fx13b -WithGeneratedProps
$notSemVer = New-ValidDescriptorObject
$notSemVer.windows.version = 'not a version'
$notSemVer.console.informationalVersion = 'not a version'
Write-Descriptor -Path (Join-Path $fx13b 'release-versions.json') -Object $notSemVer
$r13b = Invoke-Assert -PhoneDeckRoot $fx13b
Assert-True -Condition ($r13b.ExitCode -ne 0) -Message 'non-SemVer windows.version exits nonzero'
Assert-True -Condition ($r13b.Output -match 'SemVer|windows\.version') -Message 'non-SemVer error mentions SemVer'

# --- 14) props Version/AssemblyVersion drift ignored previously ---
$fx14 = New-FixtureRoot
New-ProductTree -Root $fx14 -WithGeneratedProps
Write-Descriptor -Path (Join-Path $fx14 'release-versions.json') -Object (New-ValidDescriptorObject)
$driftProps = @"
<!--
  GENERATED FILE — do not edit by hand.
  Source of truth: work/phone-deck/release-versions.json
  Regenerated by: pwsh -File scripts/versioning/Assert-ReleaseVersions.ps1 -Sync
-->
<Project>
  <PropertyGroup>
    <Version>9.9.9</Version>
    <AssemblyVersion>8.8.8.0</AssemblyVersion>
    <FileVersion>1.6.0.23</FileVersion>
    <InformationalVersion>1.6.0-dev.11</InformationalVersion>
    <PhoneDeckReleaseSequence>23</PhoneDeckReleaseSequence>
  </PropertyGroup>
</Project>
"@
Write-Utf8NoBom -Path (Join-Path $fx14 'windows/Directory.Build.props') -Content $driftProps
$r14 = Invoke-Assert -PhoneDeckRoot $fx14
Assert-True -Condition ($r14.ExitCode -ne 0) -Message 'Version/AssemblyVersion props drift exits nonzero'
Assert-True -Condition ($r14.Output -match 'Version|AssemblyVersion') -Message 'props drift names Version or AssemblyVersion'

# --- 15) duplicate Version element in props ---
$fx15 = New-FixtureRoot
New-ProductTree -Root $fx15 -WithGeneratedProps
Write-Descriptor -Path (Join-Path $fx15 'release-versions.json') -Object (New-ValidDescriptorObject)
$dupProps = @"
<!--
  GENERATED FILE — do not edit by hand.
  Source of truth: work/phone-deck/release-versions.json
  Regenerated by: pwsh -File scripts/versioning/Assert-ReleaseVersions.ps1 -Sync
-->
<Project>
  <PropertyGroup>
    <Version>1.6.0</Version>
    <Version>1.6.0</Version>
    <AssemblyVersion>1.6.0.0</AssemblyVersion>
    <FileVersion>1.6.0.23</FileVersion>
    <InformationalVersion>1.6.0-dev.11</InformationalVersion>
    <PhoneDeckReleaseSequence>23</PhoneDeckReleaseSequence>
  </PropertyGroup>
</Project>
"@
Write-Utf8NoBom -Path (Join-Path $fx15 'windows/Directory.Build.props') -Content $dupProps
$r15 = Invoke-Assert -PhoneDeckRoot $fx15
Assert-True -Condition ($r15.ExitCode -ne 0) -Message 'duplicate Version in props exits nonzero'
Assert-True -Condition ($r15.Output -match 'duplicate.*Version|Version.*duplicate') -Message 'duplicate Version reported'

# --- 16) missing FileVersion in props ---
$fx16 = New-FixtureRoot
New-ProductTree -Root $fx16 -WithGeneratedProps
Write-Descriptor -Path (Join-Path $fx16 'release-versions.json') -Object (New-ValidDescriptorObject)
$missingFv = @"
<!--
  GENERATED FILE — do not edit by hand.
  Source of truth: work/phone-deck/release-versions.json
  Regenerated by: pwsh -File scripts/versioning/Assert-ReleaseVersions.ps1 -Sync
-->
<Project>
  <PropertyGroup>
    <Version>1.6.0</Version>
    <AssemblyVersion>1.6.0.0</AssemblyVersion>
    <InformationalVersion>1.6.0-dev.11</InformationalVersion>
    <PhoneDeckReleaseSequence>23</PhoneDeckReleaseSequence>
  </PropertyGroup>
</Project>
"@
Write-Utf8NoBom -Path (Join-Path $fx16 'windows/Directory.Build.props') -Content $missingFv
$r16 = Invoke-Assert -PhoneDeckRoot $fx16
Assert-True -Condition ($r16.ExitCode -ne 0) -Message 'missing FileVersion in props exits nonzero'
Assert-True -Condition ($r16.Output -match 'FileVersion') -Message 'missing FileVersion named in error'

# --- 17) invalid props XML ---
$fx17 = New-FixtureRoot
New-ProductTree -Root $fx17 -WithGeneratedProps
Write-Descriptor -Path (Join-Path $fx17 'release-versions.json') -Object (New-ValidDescriptorObject)
Write-Utf8NoBom -Path (Join-Path $fx17 'windows/Directory.Build.props') -Content "<!-- GENERATED FILE --><Project><PropertyGroup><Version>1.6.0</Version>"
$r17 = Invoke-Assert -PhoneDeckRoot $fx17
Assert-True -Condition ($r17.ExitCode -ne 0) -Message 'invalid props XML exits nonzero'
Assert-True -Condition ($r17.Output -match 'XML|invalid|unsafe') -Message 'invalid props XML named in error'

# --- 18) raw JSON fractional sequence against matching sources (reviewer bypass case) ---
$fx18 = New-FixtureRoot
New-ProductTree -Root $fx18 -WithGeneratedProps
$rawFrac = @'
{
  "schemaVersion": 1,
  "windows": {
    "version": "1.6.0-dev.11",
    "sequence": 23.1
  },
  "android": {
    "versionName": "1.6.0-dev.17",
    "versionCode": 23
  },
  "macos": {
    "version": "2.0.0-dev.3",
    "bundleShortVersion": "2.0.0",
    "bundleVersion": "2",
    "historicBundleMapping": {
      "healthVersion": "2.0.0-dev.3",
      "bundleShortVersion": "2.0.0",
      "bundleVersion": "2",
      "note": "historic Mac mapping preserved for fixture"
    }
  },
  "console": {
    "follows": "windows.version",
    "informationalVersion": "1.6.0-dev.11"
  }
}
'@
Write-RawDescriptor -Path (Join-Path $fx18 'release-versions.json') -Json $rawFrac
$r18 = Invoke-Assert -PhoneDeckRoot $fx18
Assert-True -Condition ($r18.ExitCode -ne 0) -Message 'raw JSON sequence 23.1 vs source 23 exits nonzero'
Assert-True -Condition ($r18.Output -notmatch 'OK: release versions consistent') -Message 'raw JSON 23.1 does not print OK'

Write-Host ""
Write-Host "Passed: $passed  Failed: $failed"
Write-Host "Retained fixtures ($($fixtureRoots.Count)):"
foreach ($root in $fixtureRoots) {
    Write-Host "  $root"
}
if ($failed -gt 0) {
    exit 1
}
exit 0
