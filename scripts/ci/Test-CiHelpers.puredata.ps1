<#
.SYNOPSIS
    临时纯数据用例：验证 CI helper 决策与 manifest 校验（不改 scripts/tests，不跑全量 build）。
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $here '../..')).Path
$decisionScript = Join-Path $here 'Get-PushSkipDecision.ps1'
$readScript = Join-Path $here 'Read-BuildManifest.ps1'
$failed = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        Write-Host "FAIL: $Message" -ForegroundColor Red
        $script:failed++
    } else {
        Write-Host "PASS: $Message" -ForegroundColor Green
    }
}

# --- dedup decisions ---
$repo = 'leolemon777/PhoneDeck'
$prsSame = @(
    [pscustomobject]@{
        number = 12
        head   = [pscustomobject]@{
            ref  = 'agent/b01-build-pipeline'
            repo = [pscustomobject]@{ full_name = 'leolemon777/PhoneDeck' }
        }
    }
)
$d1 = & $decisionScript -Repository $repo -Branch 'agent/b01-build-pipeline' -PullRequests $prsSame
Assert-True -Condition ($d1.Skip -eq $true) -Message 'same-repo head open PR → skip'

$prsFork = @(
    [pscustomobject]@{
        number = 99
        head   = [pscustomobject]@{
            ref  = 'agent/b01-build-pipeline'
            repo = [pscustomobject]@{ full_name = 'someoneelse/PhoneDeck' }
        }
    }
)
$d2 = & $decisionScript -Repository $repo -Branch 'agent/b01-build-pipeline' -PullRequests $prsFork
Assert-True -Condition ($d2.Skip -eq $false) -Message 'fork same branch name → do not skip'

$prsOtherBranch = @(
    [pscustomobject]@{
        number = 3
        head   = [pscustomobject]@{
            ref  = 'agent/other'
            repo = [pscustomobject]@{ full_name = 'leolemon777/PhoneDeck' }
        }
    }
)
$d3 = & $decisionScript -Repository $repo -Branch 'agent/b01-build-pipeline' -PullRequests $prsOtherBranch
Assert-True -Condition ($d3.Skip -eq $false) -Message 'same repo different branch → do not skip'

$d4 = & $decisionScript -Repository $repo -Branch 'agent/b01-build-pipeline' -PullRequests @()
Assert-True -Condition ($d4.Skip -eq $false) -Message 'empty PR list → do not skip'

# owner 同名但 full_name 不同（叠加/镜像）不应仅因 owner 匹配而跳过
$prsOwnerOnly = @(
    [pscustomobject]@{
        number = 7
        head   = [pscustomobject]@{
            ref  = 'agent/b01-build-pipeline'
            repo = [pscustomobject]@{ full_name = 'leolemon777/PhoneDeck-forkish' }
        }
    }
)
$d5 = & $decisionScript -Repository $repo -Branch 'agent/b01-build-pipeline' -PullRequests $prsOwnerOnly
Assert-True -Condition ($d5.Skip -eq $false) -Message 'same owner different repo full_name → do not skip'

# --- manifest schema ---
$tmp = Join-Path $env:TEMP ("phonedeck-manifest-test-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $runDir = Join-Path $tmp 'outputs/build-review/run1'
    New-Item -ItemType Directory -Path (Join-Path $runDir 'reports') -Force | Out-Null
    $manifestPath = Join-Path $runDir 'manifest.json'
    $manifest = [ordered]@{
        schemaVersion    = 1
        runId            = 'run1'
        runDirectory     = 'outputs/build-review/run1'
        status           = 'success'
        phase            = 'complete'
        exitCode         = 0
        testsSkipped     = $false
        artifacts        = @(@{ name = 'x'; path = 'windows/server/a.exe' })
        reportsDirectory = 'reports'
    }
    ($manifest | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $manifestPath -Encoding utf8

    # 把临时目录当作伪仓库根：复制相对路径结构
    $fakeRepo = $tmp
    $info = & $readScript -Path $manifestPath -RepoRoot $fakeRepo -RequireRunDirectory
    Assert-True -Condition ($info.Manifest.status -eq 'success') -Message 'manifest schema accepts success'

    $bad = $manifest.PSObject.Copy()
    # 重新写非法 status
    $badPath = Join-Path $runDir 'bad.json'
    $badObj = [ordered]@{
        schemaVersion    = 1
        runId            = 'run1'
        runDirectory     = 'outputs/build-review/run1'
        status           = 'nope'
        phase            = 'complete'
        exitCode         = 0
        testsSkipped     = $false
        artifacts        = @()
        reportsDirectory = 'reports'
    }
    ($badObj | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $badPath -Encoding utf8
    $threw = $false
    try {
        & $readScript -Path $badPath -RepoRoot $fakeRepo | Out-Null
    } catch {
        $threw = $true
    }
    Assert-True -Condition $threw -Message 'illegal status rejected'

    $precheckPath = Join-Path $tmp 'latest-precheck.json'
    $precheck = [ordered]@{
        schemaVersion    = 1
        runId            = ''
        runDirectory     = ''
        status           = 'failed'
        phase            = 'precheck'
        exitCode         = 1
        testsSkipped     = $false
        artifacts        = @()
        reportsDirectory = 'reports'
    }
    ($precheck | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $precheckPath -Encoding utf8
    $threw2 = $false
    try {
        & $readScript -Path $precheckPath -RepoRoot $fakeRepo -RequireRunDirectory | Out-Null
    } catch {
        $threw2 = $true
    }
    Assert-True -Condition $threw2 -Message 'precheck latest without runDirectory rejected by RequireRunDirectory'
}
finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failed -gt 0) {
    Write-Host "`n$failed assertion(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host "`nAll pure-data CI helper checks passed." -ForegroundColor Green
exit 0
