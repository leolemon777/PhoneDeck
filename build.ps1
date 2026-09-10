<#
.SYNOPSIS
    PhoneDeck 统一开发构建便捷入口
.DESCRIPTION
    转发所有参数至 scripts/build.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('All', 'Windows', 'Android', 'MacOS')]
    [string]$Platform = 'All',

    [string]$Configuration = 'Release',

    [string]$OutputDir = 'outputs/build-review',

    [switch]$Clean,

    [switch]$SkipTests
)

$targetScript = Join-Path $PSScriptRoot "scripts/build.ps1"
& $targetScript @PSBoundParameters
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
