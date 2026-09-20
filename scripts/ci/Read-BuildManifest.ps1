<#
.SYNOPSIS
    读取并校验 B01 build manifest / latest.json（schemaVersion=1）。
.PARAMETER Path
    manifest.json 或 latest.json 路径
.PARAMETER RequireRunDirectory
    若指定，runDirectory 必须非空且对应目录存在（相对仓库根或已是绝对路径）
.PARAMETER RepoRoot
    仓库根；默认从本脚本位置推断
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Path,

    [switch]$RequireRunDirectory,

    [string]$RepoRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    $RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
}

if (-not (Test-Path -LiteralPath $Path)) {
    throw "manifest 不存在: $Path"
}

$raw = Get-Content -LiteralPath $Path -Raw -Encoding utf8
$obj = $raw | ConvertFrom-Json

$required = @('schemaVersion', 'runId', 'runDirectory', 'status', 'phase', 'exitCode', 'testsSkipped', 'artifacts', 'reportsDirectory')
foreach ($name in $required) {
    if (-not ($obj.PSObject.Properties.Name -contains $name)) {
        throw "manifest 缺少字段: $name ($Path)"
    }
}

if ([int]$obj.schemaVersion -ne 1) {
    throw "不支持的 schemaVersion=$($obj.schemaVersion) ($Path)"
}

$allowedStatus = @('running', 'success', 'failed', 'unverified')
if ($allowedStatus -notcontains [string]$obj.status) {
    throw "非法 status=$($obj.status) ($Path)"
}

if ([string]$obj.reportsDirectory -ne 'reports') {
    throw "reportsDirectory 必须为 'reports'，实际: $($obj.reportsDirectory)"
}

if ($null -eq $obj.artifacts) {
    throw "artifacts 不能为 null"
}

$runFull = $null
if (-not [string]::IsNullOrWhiteSpace([string]$obj.runDirectory)) {
    $rd = [string]$obj.runDirectory
    $runFull = if ([System.IO.Path]::IsPathRooted($rd)) {
        [System.IO.Path]::GetFullPath($rd)
    } else {
        [System.IO.Path]::GetFullPath((Join-Path $RepoRoot ($rd -replace '/', [System.IO.Path]::DirectorySeparatorChar)))
    }

    $allowedOutputsDir = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot 'outputs'))
    $separator = [System.IO.Path]::DirectorySeparatorChar.ToString()
    $allowedPrefix = if ($allowedOutputsDir.EndsWith($separator)) { $allowedOutputsDir } else { $allowedOutputsDir + $separator }
    if ($runFull.Equals($allowedOutputsDir, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $runFull.StartsWith($allowedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "runDirectory 必须位于仓库 outputs/ 子目录内: $runFull"
    }
}

if ($RequireRunDirectory) {
    if ([string]::IsNullOrWhiteSpace([string]$obj.runDirectory)) {
        throw "RequireRunDirectory：runDirectory 为空（可能是 precheck 失败，不能当成本次成功产物）"
    }
    if (-not (Test-Path -LiteralPath $runFull)) {
        throw "runDirectory 不存在: $runFull"
    }
}

[pscustomobject]@{
    Manifest     = $obj
    Path         = $Path
    RunDirectory = $runFull
    RepoRoot     = $RepoRoot
}
