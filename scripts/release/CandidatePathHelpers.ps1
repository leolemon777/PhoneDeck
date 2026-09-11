# Internal helpers for New-CandidateRun.ps1 (B03 S1). Not a public entrypoint.
# Path safety and atomic writes adapted from scripts/build.ps1 patterns.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-IsCaseSensitiveFileSystem {
    param([Parameter(Mandatory)][string]$ProbeDir)
    if (-not (Test-Path -LiteralPath $ProbeDir)) { return -not $IsWindows }
    $name = [System.IO.Path]::GetFileName($ProbeDir.TrimEnd('\', '/'))
    if ([string]::IsNullOrWhiteSpace($name)) { return -not $IsWindows }
    $flipped = if ($name.ToLowerInvariant() -ceq $name) { $name.ToUpperInvariant() } else { $name.ToLowerInvariant() }
    if ($flipped -ceq $name) { return -not $IsWindows }
    $parent = Split-Path -Parent $ProbeDir
    $alt = Join-Path $parent $flipped
    return -not (Test-Path -LiteralPath $alt)
}

function Test-HasReparseOrLink {
    param([Parameter(Mandatory)][string]$PathToCheck)
    if (-not (Test-Path -LiteralPath $PathToCheck)) { return $false }
    $item = Get-Item -LiteralPath $PathToCheck -Force
    if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { return $true }
    if ($item.LinkType) { return $true }
    return $false
}

function Assert-AncestorChainSafe {
    param(
        [Parameter(Mandatory)][string]$TargetPath,
        [Parameter(Mandatory)][string]$StopAtPath
    )
    $stop = [System.IO.Path]::GetFullPath($StopAtPath)
    $current = [System.IO.Path]::GetFullPath($TargetPath)
    while ($current -and $current.Length -ge $stop.Length) {
        if (Test-HasReparseOrLink -PathToCheck $current) {
            throw "[security] Directory chain must not contain symlink/junction/reparse. Found: $current"
        }
        if ($current.Equals($stop, [System.StringComparison]::OrdinalIgnoreCase)) { break }
        $parent = [System.IO.Path]::GetDirectoryName($current)
        if (-not $parent -or $parent -eq $current) { break }
        $current = $parent
    }
}

function Assert-TreeHasNoReparse {
    param([Parameter(Mandatory)][string]$RootPath)
    if (-not (Test-Path -LiteralPath $RootPath)) { return }
    Assert-AncestorChainSafe -TargetPath $RootPath -StopAtPath $RootPath
    $items = Get-ChildItem -LiteralPath $RootPath -Recurse -Force -ErrorAction Stop
    foreach ($item in $items) {
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -or $item.LinkType) {
            throw "[security] Tree contains symlink/junction/reparse. Found: $($item.FullName)"
        }
    }
}

function Resolve-SafeCandidateOutputRoot {
    param(
        [Parameter(Mandatory)][string]$RequestedOutputRoot,
        [Parameter(Mandatory)][string]$RepoRoot
    )

    $outFull = if ([System.IO.Path]::IsPathRooted($RequestedOutputRoot)) {
        [System.IO.Path]::GetFullPath($RequestedOutputRoot)
    } else {
        [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $RequestedOutputRoot))
    }

    $allowedOutputsDir = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot 'outputs'))
    $separator = [System.IO.Path]::DirectorySeparatorChar.ToString()
    $allowedPrefix = if ($allowedOutputsDir.EndsWith($separator)) { $allowedOutputsDir } else { $allowedOutputsDir + $separator }

    $comparison = if (Test-IsCaseSensitiveFileSystem -ProbeDir $RepoRoot) {
        [System.StringComparison]::Ordinal
    } else {
        [System.StringComparison]::OrdinalIgnoreCase
    }

    if ($outFull.Equals($allowedOutputsDir, $comparison) -or -not $outFull.StartsWith($allowedPrefix, $comparison)) {
        throw "[security] OutputRoot must be a subdirectory of repo outputs/ (not outputs itself). Requested: $outFull"
    }

    if ($comparison -eq [System.StringComparison]::Ordinal -and (Test-Path -LiteralPath $allowedOutputsDir)) {
        $rel = [System.IO.Path]::GetRelativePath($RepoRoot, $outFull) -replace '\\', '/'
        if (-not $rel.StartsWith('outputs/', [System.StringComparison]::Ordinal)) {
            throw "[security] OutputRoot relative path casing must be outputs/..., got: $rel"
        }
        $cursor = $RepoRoot
        foreach ($segment in ($rel -split '/')) {
            if ([string]::IsNullOrWhiteSpace($segment)) { continue }
            $matched = Get-ChildItem -LiteralPath $cursor -Force -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -ceq $segment } |
                Select-Object -First 1
            if (-not $matched) {
                $wrongCase = Get-ChildItem -LiteralPath $cursor -Force -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -ieq $segment -and $_.Name -cne $segment } |
                    Select-Object -First 1
                if ($wrongCase) {
                    throw "[security] Output path segment casing mismatch. Expected '$segment', found '$($wrongCase.Name)' under $cursor"
                }
                break
            }
            $cursor = $matched.FullName
        }
    }

    $repoParent = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot '..'))
    Assert-AncestorChainSafe -TargetPath $outFull -StopAtPath $repoParent
    return $outFull
}

function Write-AtomicTextFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Content,
        [string]$SafeAncestorStopAt = ''
    )
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $dir = [System.IO.Path]::GetDirectoryName($fullPath)
    if ([string]::IsNullOrWhiteSpace($dir)) { throw "Invalid write path: $Path" }

    if (-not [string]::IsNullOrWhiteSpace($SafeAncestorStopAt)) {
        Assert-AncestorChainSafe -TargetPath $dir -StopAtPath $SafeAncestorStopAt
    }

    [System.IO.Directory]::CreateDirectory($dir) | Out-Null

    if (-not [string]::IsNullOrWhiteSpace($SafeAncestorStopAt)) {
        Assert-AncestorChainSafe -TargetPath $dir -StopAtPath $SafeAncestorStopAt
    }

    $tmp = Join-Path $dir ('.phonedeck-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $encoding = [System.Text.UTF8Encoding]::new($false)
    $fs = $null
    try {
        $fs = [System.IO.File]::Open(
            $tmp,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None
        )
        $bytes = $encoding.GetBytes($Content)
        $fs.Write($bytes, 0, $bytes.Length)
        $fs.Flush($true)
    } finally {
        if ($null -ne $fs) { $fs.Dispose() }
    }

    try {
        if (Test-Path -LiteralPath $fullPath) {
            if (Test-HasReparseOrLink -PathToCheck $fullPath) {
                [System.IO.File]::Delete($fullPath)
            }
        }
        [System.IO.File]::Move($tmp, $fullPath, $true)
        if (Test-HasReparseOrLink -PathToCheck $fullPath) {
            throw "[security] Atomic write landed on a reparse/symlink node: $fullPath"
        }
    } catch {
        if (Test-Path -LiteralPath $tmp) {
            Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        }
        throw
    }
}

function Get-FileSha256Lower {
    param([Parameter(Mandatory)][string]$LiteralPath)
    return (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
