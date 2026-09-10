<#
.SYNOPSIS
    PhoneDeck 统一开发构建便捷入口
.DESCRIPTION
    转发所有参数至 scripts/build.ps1，透传非零退出码。
#>
[CmdletBinding()]
param(
    [ValidateSet('All', 'Windows', 'Android', 'MacOS')]
    [string]$Platform = 'All',

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$OutputDir = 'outputs/build-review',

    [switch]$Clean,

    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$targetScript = Join-Path $PSScriptRoot 'scripts/build.ps1'
try {
    & $targetScript @PSBoundParameters
    $code = $LASTEXITCODE
    if ($null -eq $code) { $code = 0 }
    exit $code
} catch {
    Write-Error $_
    exit 1
}
