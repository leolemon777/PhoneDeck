<#
.SYNOPSIS
    CI 用：查询同仓库 open PR，决定 push 是否跳过。API 失败 fail-open（不跳过）。
.DESCRIPTION
    写入 GITHUB_OUTPUT: skip_push=true|false
    需要环境变量: GITHUB_REPOSITORY, GITHUB_REF（或 -Branch）, GH_TOKEN/GITHUB_TOKEN
#>
[CmdletBinding()]
param(
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$Branch = '',
    [string]$GitHubOutput = $env:GITHUB_OUTPUT
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-SkipOutput {
    param([bool]$Skip, [string]$Reason)
    Write-Host $Reason
    if (-not [string]::IsNullOrWhiteSpace($GitHubOutput)) {
        "skip_push=$($Skip.ToString().ToLowerInvariant())" | Add-Content -LiteralPath $GitHubOutput -Encoding utf8
    }
}

if ([string]::IsNullOrWhiteSpace($Repository)) {
    Write-SkipOutput -Skip $false -Reason 'GITHUB_REPOSITORY 为空：fail-open，继续运行全部检查。'
    exit 0
}

if ([string]::IsNullOrWhiteSpace($Branch)) {
    $ref = [string]$env:GITHUB_REF
    if ($ref -like 'refs/heads/*') {
        $Branch = $ref.Substring('refs/heads/'.Length)
    }
}
if ([string]::IsNullOrWhiteSpace($Branch)) {
    Write-SkipOutput -Skip $false -Reason '无法解析分支名：fail-open，继续运行全部检查。'
    exit 0
}

$token = $env:GH_TOKEN
if ([string]::IsNullOrWhiteSpace($token)) { $token = $env:GITHUB_TOKEN }
if ([string]::IsNullOrWhiteSpace($token)) {
    Write-SkipOutput -Skip $false -Reason '无 GitHub token：fail-open，继续运行全部检查。'
    exit 0
}

$decisionScript = Join-Path $PSScriptRoot 'Get-PushSkipDecision.ps1'
$prs = @()
try {
    # 使用 gh api 并正确编码查询参数；拉取 open PR 后在本地按同仓库 head 过滤
    # （head=owner:branch 对 fork/叠加不够稳健，且编码易错）
    $page = 1
    $all = [System.Collections.Generic.List[object]]::new()
    while ($page -le 5) {
        $json = & gh api --method GET "repos/$Repository/pulls" -f state=open -f per_page=100 -f page=$page 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "gh api failed (exit $LASTEXITCODE): $json"
        }
        $batch = $json | ConvertFrom-Json
        if ($null -eq $batch) { break }
        $arr = @($batch)
        if ($arr.Count -eq 0) { break }
        foreach ($item in $arr) { $all.Add($item) | Out-Null }
        if ($arr.Count -lt 100) { break }
        $page++
    }
    $prs = @($all)
}
catch {
    Write-SkipOutput -Skip $false -Reason "GitHub API 查询失败（fail-open，继续运行全部检查）: $_"
    exit 0
}

$decision = & $decisionScript -Repository $Repository -Branch $Branch -PullRequests $prs
Write-SkipOutput -Skip ([bool]$decision.Skip) -Reason ([string]$decision.Reason)
exit 0
