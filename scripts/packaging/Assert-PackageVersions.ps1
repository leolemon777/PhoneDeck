#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Server,
    [Parameter(Mandatory)][string]$ControlCenter,
    [Parameter(Mandatory)][string]$Apk,
    [Parameter(Mandatory)][string]$DescriptorPath,
    [string]$Aapt2Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 1. Parse descriptor file
$json = Get-Content -Raw -LiteralPath $DescriptorPath | ConvertFrom-Json

# 2. EXE static validation
$roles = [ordered]@{
    'Server' = $Server
    'ControlCenter' = $ControlCenter
}

$isWinOs = $IsWindows -or ($PSVersionTable.PSEdition -ne 'Core' -and [System.Environment]::OSVersion.Platform -eq 'Win32NT')

if (-not $isWinOs) {
    throw "Unsupported host: Windows PE static validation requires Windows OS."
}

foreach ($role in $roles.Keys) {
    $exe = $roles[$role]
    if (-not (Test-Path -LiteralPath $exe)) { throw "EXE not found: $exe" }
    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe)

    # Verify Identity
    $expectedOrig = "PhoneDeck.${role}.exe"
    $expectedOrigDll = "PhoneDeck.${role}.dll"
    if ($info.OriginalFilename -ne $expectedOrig -and $info.OriginalFilename -ne $expectedOrigDll) {
        throw "Invalid OriginalFilename in $exe : $($info.OriginalFilename). Expected $expectedOrig or $expectedOrigDll"
    }

    # ProductVersion supports exact descriptor or +commit suffix
    if ([string]::IsNullOrEmpty($info.ProductVersion)) { throw "ProductVersion is empty in $exe" }
    $expectedBase = $json.windows.version
    $escapedBase = [regex]::Escape($expectedBase)
    $hasMetadata = $expectedBase.Contains('+')
    $separator = if ($hasMetadata) { '\.' } else { '\+' }
    $pattern = "^${escapedBase}${separator}[0-9a-fA-F]+$"

    if ($info.ProductVersion -ne $expectedBase -and $info.ProductVersion -notmatch $pattern) {
        throw "ProductVersion mismatch in $exe. Expected $expectedBase (or with hex hash suffix), got $($info.ProductVersion)"
    }

    # FileVersion sequence validation (3 component prefix + sequence)
    if ($json.windows.version -match "^(\d+\.\d+\.\d+)") {
        $prefix = $matches[1]
    } else {
        throw "Cannot parse 3 component prefix from $($json.windows.version)"
    }
    $expectedFileVer = "$prefix.$($json.windows.sequence)"
    if ($info.FileVersion -ne $expectedFileVer) {
        throw "FileVersion mismatch in $exe. Expected $expectedFileVer, got $($info.FileVersion)"
    }
}

# 3. APK aapt2 validation
if (-not (Test-Path -LiteralPath $Apk)) { throw "APK not found: $Apk" }

if ([string]::IsNullOrWhiteSpace($Aapt2Path)) {
    $sdkPaths = @()
    if ($env:ANDROID_HOME) { $sdkPaths += $env:ANDROID_HOME }
    if ($env:ANDROID_SDK_ROOT) { $sdkPaths += $env:ANDROID_SDK_ROOT }

    if (-not $sdkPaths) { throw "ANDROID_HOME/ANDROID_SDK_ROOT environment variables are not set and Aapt2Path is not provided." }

    $aapt2Name = if ($isWinOs) { "aapt2.exe" } else { "aapt2" }

    $aapt2Candidates = @()
    foreach ($sdkPath in $sdkPaths) {
        $aapt2Candidates += @(Resolve-Path "$sdkPath/build-tools/*/$aapt2Name" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Path)
    }

    if (-not $aapt2Candidates) { throw "aapt2 not found in build-tools" }
    $Aapt2Path = ($aapt2Candidates | Sort-Object | Select-Object -Last 1)
}

if (-not (Test-Path -LiteralPath $Aapt2Path)) { throw "aapt2 not found at: $Aapt2Path" }

$badging = & $Aapt2Path dump badging $Apk 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "aapt2 dump badging failed with exit code $LASTEXITCODE : $badging"
}

$badgingText = $badging -join "`n"

$escapedVersionName = [regex]::Escape($json.android.versionName)
$pattern = "^package: name='com\.codex\.phonedeck' versionCode='$($json.android.versionCode)' versionName='$escapedVersionName'"

$pkgMatches = [regex]::Matches($badgingText, "(?m)^package: name=.*$")
if ($pkgMatches.Count -ne 1) {
    throw "APK package record missing, malformed, or duplicate. Expected exactly 1 match, found $($pkgMatches.Count)"
}

$pkgLine = $pkgMatches[0].Value
if ($pkgLine -notmatch "versionCode=" -or $pkgLine -notmatch "versionName=") {
    throw "APK package record missing, malformed, or duplicate. Expected exactly 1 match, found 0 valid records"
}

if ($pkgLine -notmatch $pattern) {
    throw "APK package record mismatch. Expected all 3 values together, got: $pkgLine"
}
