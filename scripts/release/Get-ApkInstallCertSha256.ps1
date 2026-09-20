<#
.SYNOPSIS
    Read the install-certificate SHA-256 of an APK via checked apksigner verify --print-certs.

.DESCRIPTION
    B03 S2 helper. Requires a real apksigner binary path. Enforces a single signer and
    normalizes the certificate SHA-256 digest to a full 64-hex lowercase string.
    Read-only: does not sign, install, or write release history.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Apk,
    [Parameter(Mandatory)][string]$ApkSignerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "Get-ApkInstallCertSha256.ps1 requires PowerShell 7+. Current: $($PSVersionTable.PSVersion)"
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path -LiteralPath (Join-Path $ScriptDir '..\..')).Path
. (Join-Path $ScriptDir 'ReleasePolicy.ps1')

try {
    $apkFull = Resolve-ExistingFileLocal -Path $Apk -Label 'Apk' -RepoRoot $RepoRoot
    $signerFull = Resolve-ExistingFileLocal -Path $ApkSignerPath -Label 'ApkSignerPath' -RepoRoot $RepoRoot
    $sha = Get-ApkInstallCertSha256FromApksigner -ApkPath $apkFull -ApkSignerPath $signerFull
    Write-Output $sha
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
