<#
.SYNOPSIS
    静态校验 B01 构建脚本 AST，并做不触发全量 build 的前置失败探测。
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
Set-Location $repoRoot

$files = @(
    'scripts/build.ps1',
    'build.ps1',
    'scripts/ci/Get-PushSkipDecision.ps1',
    'scripts/ci/Read-BuildManifest.ps1',
    'scripts/ci/Resolve-PushSkip.ps1',
    'scripts/ci/Test-CiHelpers.puredata.ps1'
)

foreach ($f in $files) {
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $f), [ref]$tokens, [ref]$errors)
    if ($errors -and $errors.Count -gt 0) {
        Write-Host "AST FAIL: $f"
        $errors | ForEach-Object { Write-Host $_ }
        exit 1
    }
    Write-Host "AST OK: $f"
}

$build = Join-Path $repoRoot 'scripts/build.ps1'
$probeOut = 'outputs/b01-static-probe'
$probeFull = Join-Path $repoRoot $probeOut
$probeLatest = Join-Path $probeFull 'latest.json'
$cases = @(
    @{ Name = 'outputs-itself'; Args = @('-Platform', 'Windows', '-OutputDir', 'outputs'); ExpectNonZero = $true; ExpectLatestPrecheck = $false },
    @{ Name = 'outside-outputs'; Args = @('-Platform', 'Windows', '-OutputDir', 'scratch/evil'); ExpectNonZero = $true; ExpectLatestPrecheck = $false },
    @{ Name = 'macos-on-windows'; Args = @('-Platform', 'MacOS', '-OutputDir', $probeOut); ExpectNonZero = $true; ExpectLatestPrecheck = $true }
)

try {
    foreach ($c in $cases) {
        Write-Host "=== CASE $($c.Name) ==="
        $before = $null
        if (Test-Path -LiteralPath $probeLatest) {
            $before = Get-Content -LiteralPath $probeLatest -Raw
        }

        & pwsh -NoProfile -File $build @($c.Args)
        $code = $LASTEXITCODE
        Write-Host "exit=$code"
        if ($c.ExpectNonZero -and $code -eq 0) {
            throw "CASE $($c.Name) expected non-zero exit"
        }

        if ($c.ExpectLatestPrecheck) {
            if (-not (Test-Path -LiteralPath $probeLatest)) {
                throw "CASE $($c.Name) expected latest.json after safe OutputDir precheck failure"
            }
            $latest = Get-Content -LiteralPath $probeLatest -Raw | ConvertFrom-Json
            if ($latest.status -ne 'failed' -or $latest.phase -ne 'precheck') {
                throw "CASE $($c.Name) expected failed/precheck latest, got status=$($latest.status) phase=$($latest.phase)"
            }
            if (-not [string]::IsNullOrWhiteSpace([string]$latest.runDirectory)) {
                throw "CASE $($c.Name) precheck latest should not point at a successful runDirectory"
            }
            Write-Host "latest precheck OK: status=$($latest.status) phase=$($latest.phase)"
        } else {
            # 不安全 OutputDir 不得改写探测目录 latest
            if ($null -ne $before) {
                $after = if (Test-Path -LiteralPath $probeLatest) { Get-Content -LiteralPath $probeLatest -Raw } else { $null }
                if ($after -ne $before) {
                    throw "CASE $($c.Name) must not modify existing latest.json when OutputDir is unsafe"
                }
            }
        }
    }
}
finally {
    if (Test-Path -LiteralPath $probeFull) {
        Remove-Item -LiteralPath $probeFull -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host 'Static + precheck probes passed.'
exit 0
