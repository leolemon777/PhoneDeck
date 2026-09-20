<#
.SYNOPSIS
    根据 open PR 列表判断 push 事件是否应跳过（同仓库 head 已有 open PR 时跳过）。
.DESCRIPTION
    纯数据决策，便于本地用假 JSON 验证。fail-open 由调用方在 API 失败时处理：
    本函数只在成功拿到 PR 列表后做过滤。
.PARAMETER Repository
    当前仓库 full_name，例如 owner/repo
.PARAMETER Branch
    push 的分支名（不含 refs/heads/）
.PARAMETER PullRequests
    GitHub /pulls API 返回的对象数组（或已 ConvertFrom-Json 的列表）
.OUTPUTS
    PSCustomObject: Skip (bool), Reason (string), MatchedPrNumbers (int[])
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Repository,

    [Parameter(Mandatory)]
    [string]$Branch,

    [Parameter(Mandatory)]
    [AllowEmptyCollection()]
    [object[]]$PullRequests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$matched = [System.Collections.Generic.List[int]]::new()
foreach ($pr in $PullRequests) {
    if ($null -eq $pr) { continue }
    $headRef = $null
    $headRepo = $null
    try { $headRef = [string]$pr.head.ref } catch { $headRef = $null }
    try { $headRepo = [string]$pr.head.repo.full_name } catch { $headRepo = $null }

    if ([string]::IsNullOrWhiteSpace($headRef) -or [string]::IsNullOrWhiteSpace($headRepo)) {
        continue
    }

    # 同仓库 head（不是仅同 owner），兼容 fork/叠加 PR；分支名按 Git ref 语义大小写敏感
    $repoMatch = $headRepo.Equals($Repository, [System.StringComparison]::OrdinalIgnoreCase)
    $refMatch = $headRef.Equals($Branch, [System.StringComparison]::Ordinal)
    if ($repoMatch -and $refMatch) {
        $num = 0
        try { $num = [int]$pr.number } catch { $num = 0 }
        if ($num -gt 0) { $matched.Add($num) | Out-Null }
    }
}

if ($matched.Count -gt 0) {
    [pscustomobject]@{
        Skip             = $true
        Reason           = "open PR(s) for same-repo head ${Repository}:${Branch}: $($matched -join ', ')"
        MatchedPrNumbers = @($matched)
    }
} else {
    [pscustomobject]@{
        Skip             = $false
        Reason           = "no open same-repo PR for head ${Repository}:${Branch}"
        MatchedPrNumbers = @()
    }
}
