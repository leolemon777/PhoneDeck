# B03 S4a: internal release-history transaction foundation (dot-source library).
# Not a signing CLI. No private-key parameters. releasable remains false always.
# Commit is an internal state primitive only — S4b must verify signature/provenance
# before any caller invokes Commit. This library does not grant release approval.
# Reuses CandidatePathHelpers.ps1 + ReleasePolicy.ps1 (read-only; do not edit them).

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ReleaseTransactionDir = $PSScriptRoot
. (Join-Path $script:ReleaseTransactionDir 'CandidatePathHelpers.ps1')
. (Join-Path $script:ReleaseTransactionDir 'ReleasePolicy.ps1')

function Get-ReleaseTransactionLockPath {
    param([Parameter(Mandatory)][string]$HistoryPathFull)
    return ($HistoryPathFull + '.lock')
}

function Get-ReleaseTransactionJournalPath {
    param([Parameter(Mandatory)][string]$HistoryPathFull)
    return ($HistoryPathFull + '.journal')
}

function New-ReleaseTransactionResultObject {
    param(
        [Parameter(Mandatory)][string]$Status,
        [Parameter(Mandatory)][string]$TransactionId,
        [Parameter(Mandatory)][long]$Sequence,
        [Parameter(Mandatory)][string]$HistoryPath,
        [Parameter(Mandatory)][string]$JournalPath,
        [Parameter(Mandatory)][string]$LockPath,
        [string[]]$Reasons = @(),
        [string]$HistorySha256 = '',
        [string]$BaselineSha256 = '',
        [string]$CommittedUtc = ''
    )
    return ,([ordered]@{
            schemaVersion   = 1
            mode            = 'transaction-foundation'
            releasable      = $false
            status          = $Status
            transactionId   = $TransactionId
            sequence        = $Sequence
            historyPath     = $HistoryPath
            journalPath     = $JournalPath
            lockPath        = $LockPath
            historySha256   = $HistorySha256
            baselineSha256  = $BaselineSha256
            committedUtc    = $CommittedUtc
            reasons         = @($Reasons)
        })
}

function Resolve-SafeHistoryPathForTransaction {
    param(
        [Parameter(Mandatory)][string]$HistoryPath,
        [Parameter(Mandatory)][string]$RepoRoot,
        [switch]$AllowMissing
    )
    if ([string]::IsNullOrWhiteSpace($HistoryPath)) {
        throw 'HistoryPath path is empty.'
    }
    if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
        throw 'RepoRoot path is empty.'
    }

    $repoFull = [System.IO.Path]::GetFullPath($RepoRoot)
    if (-not (Test-Path -LiteralPath $repoFull -PathType Container)) {
        throw "RepoRoot not found: $RepoRoot"
    }

    $full = if ([System.IO.Path]::IsPathRooted($HistoryPath)) {
        [System.IO.Path]::GetFullPath($HistoryPath)
    } else {
        [System.IO.Path]::GetFullPath((Join-Path $repoFull $HistoryPath))
    }

    $allowedOutputsDir = [System.IO.Path]::GetFullPath((Join-Path $repoFull 'outputs'))
    $separator = [System.IO.Path]::DirectorySeparatorChar.ToString()
    $allowedPrefix = if ($allowedOutputsDir.EndsWith($separator)) { $allowedOutputsDir } else { $allowedOutputsDir + $separator }
    $comparison = if (Test-IsCaseSensitiveFileSystemLocal -ProbeDir $repoFull) {
        [System.StringComparison]::Ordinal
    } else {
        [System.StringComparison]::OrdinalIgnoreCase
    }

    $parent = [System.IO.Path]::GetDirectoryName($full)
    if ([string]::IsNullOrWhiteSpace($parent)) {
        throw "HistoryPath parent directory is invalid: $full"
    }
    if ($parent.Equals($allowedOutputsDir, $comparison) -or -not $parent.StartsWith($allowedPrefix, $comparison)) {
        throw 'HistoryPath must be under a subdirectory of repo outputs/ (path escape refused).'
    }
    if ($full.Equals($allowedOutputsDir, $comparison) -or -not $full.StartsWith($allowedPrefix, $comparison)) {
        throw 'HistoryPath must be under a subdirectory of repo outputs/ (path escape refused).'
    }

    $repoParent = [System.IO.Path]::GetFullPath((Join-Path $repoFull '..'))
    Assert-AncestorChainSafeLocal -TargetPath $parent -StopAtPath $repoParent

    if (Test-Path -LiteralPath $full) {
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            throw "HistoryPath exists but is not a file: $full"
        }
        if (Test-HasReparseOrLinkLocal -PathToCheck $full) {
            throw 'HistoryPath must not be a symlink/junction/reparse file.'
        }
    } elseif (-not $AllowMissing) {
        throw 'HistoryPath file is missing.'
    }

    return $full
}

function Open-ReleaseTransactionLockStream {
    param([Parameter(Mandatory)][string]$LockPath)
    $dir = [System.IO.Path]::GetDirectoryName($LockPath)
    if ([string]::IsNullOrWhiteSpace($dir)) {
        throw "Invalid lock path: $LockPath"
    }
    [System.IO.Directory]::CreateDirectory($dir) | Out-Null
    if (Test-HasReparseOrLinkLocal -PathToCheck $dir) {
        throw "Lock directory must not be a symlink/junction/reparse: $dir"
    }
    try {
        return [System.IO.File]::Open(
            $LockPath,
            [System.IO.FileMode]::OpenOrCreate,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None
        )
    } catch [System.IO.IOException] {
        throw "History lock is held by another process (FileShare.None). LockPath=$LockPath. $($_.Exception.Message)"
    } catch [System.UnauthorizedAccessException] {
        throw "History lock could not be acquired. LockPath=$LockPath. $($_.Exception.Message)"
    }
}

function Close-ReleaseTransactionLockStream {
    param($LockStream)
    if ($null -eq $LockStream) { return }
    try {
        $LockStream.Dispose()
    } catch {
        # Best-effort release on error paths.
    }
}

function Test-ReleaseTransactionLockStreamLive {
    param($LockStream)
    if ($null -eq $LockStream) { return $false }
    if ($LockStream -isnot [System.IO.Stream]) { return $false }
    try {
        if ($LockStream -is [System.IO.FileStream]) {
            $handle = $LockStream.SafeFileHandle
            if ($null -eq $handle -or $handle.IsClosed) { return $false }
        }
        # CanRead/CanWrite throw ObjectDisposedException on a disposed stream.
        return ($LockStream.CanRead -or $LockStream.CanWrite)
    } catch [System.ObjectDisposedException] {
        return $false
    } catch {
        return $false
    }
}

function Assert-ReleaseTransactionSessionLockHeld {
    param(
        [Parameter(Mandatory)]$Session,
        [Parameter(Mandatory)][string]$Operation
    )
    $stream = $Session['LockStream']
    if ($null -eq $stream) {
        throw "$Operation requires a live exclusive session lock stream; lock is missing (released, forged, closed, or never held). Use Recover-ReleaseTransaction for crash/stale-handle recovery — do not mutate another transaction journal."
    }
    if (-not (Test-ReleaseTransactionLockStreamLive -LockStream $stream)) {
        throw "$Operation requires a non-disposed exclusive session lock; lock stream is disposed/closed. Use Recover-ReleaseTransaction for crash/stale-handle recovery — do not mutate another transaction journal."
    }
}

function Assert-ReleaseTransactionJournalMatchesPendingSession {
    param(
        [Parameter(Mandatory)]$Session,
        [Parameter(Mandatory)][string]$Operation
    )
    $journalPath = [string]$Session['JournalPath']
    $transactionId = [string]$Session['TransactionId']
    $seq = [long]$Session['Sequence']
    $baseline = [string]$Session['BaselineSha256']

    $journal = Read-ReleaseTransactionJournal -JournalPath $journalPath
    if ($journal.status -cne 'pending') {
        throw "$Operation refused: journal status must be pending (got: $($journal.status)) for transactionId=$transactionId sequence=$seq."
    }
    if ($journal.transactionId -cne $transactionId) {
        throw "$Operation refused: journal transactionId mismatch (session=$transactionId journal=$($journal.transactionId)). Stale/forged session must not overwrite another transaction journal."
    }
    if ([long]$journal.sequence -ne $seq) {
        throw "$Operation refused: journal sequence mismatch (session=$seq journal=$($journal.sequence)). Stale/forged session must not overwrite another transaction journal."
    }
    if ($journal.historyBaselineSha256 -cne $baseline) {
        throw "$Operation refused: journal historyBaselineSha256 mismatch vs session baseline (session=$baseline journal=$($journal.historyBaselineSha256))."
    }
}

function Read-ReleaseHistoryValidated {
    param([Parameter(Mandatory)][string]$HistoryPathFull)
    $history = Read-JsonObjectFile -Path $HistoryPathFull -Label 'history'
    $state = Get-HistorySequenceState -History $history
    return ,([ordered]@{
            History = $history
            State   = $state
        })
}

function ConvertTo-ReleaseHistoryEntries {
    param([Parameter(Mandatory)]$History)
    $releases = Assert-StrictJsonArrayLocal -Value $History.releases -Name 'history.releases' -AllowEmpty
    $entries = New-Object System.Collections.Generic.List[object]
    $index = 0
    foreach ($rel in $releases) {
        if ($null -eq $rel) {
            throw "history.releases[$index] is null."
        }
        $seq = Assert-StrictJsonIntegerLocal -Value $rel.sequence -Name "history.releases[$index].sequence" -Min 1
        $row = [ordered]@{ sequence = $seq }
        if ($null -ne $rel.PSObject.Properties['transactionId'] -and $null -ne $rel.transactionId) {
            if ($rel.transactionId -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$rel.transactionId)) {
                throw "history.releases[$index].transactionId must be a non-empty string when present."
            }
            $row.transactionId = [string]$rel.transactionId
        }
        if ($null -ne $rel.PSObject.Properties['committedUtc'] -and $null -ne $rel.committedUtc) {
            # ConvertFrom-Json may promote ISO-8601 values to DateTime; normalize to string.
            if ($rel.committedUtc -is [datetime]) {
                $row.committedUtc = ([datetime]$rel.committedUtc).ToUniversalTime().ToString('o')
            } elseif ($rel.committedUtc -is [string]) {
                $row.committedUtc = [string]$rel.committedUtc
            } else {
                throw "history.releases[$index].committedUtc must be a string or DateTime when present."
            }
        }
        # Preserve unknown optional fields as strings/values when present (bounded).
        foreach ($prop in $rel.PSObject.Properties) {
            if ($prop.Name -in @('sequence', 'transactionId', 'committedUtc')) { continue }
            if ($null -eq $prop.Value) { continue }
            if ($prop.Value -is [string] -or (Test-StrictJsonIntegerTypeLocal -Value $prop.Value) -or $prop.Value -is [bool]) {
                $row[$prop.Name] = $prop.Value
            }
        }
        [void]$entries.Add($row)
        $index++
    }
    return ,$entries
}

function Write-ReleaseHistoryFile {
    param(
        [Parameter(Mandatory)][string]$HistoryPathFull,
        [Parameter(Mandatory)]$Entries,
        [Parameter(Mandatory)][string]$SafeAncestorStopAt
    )
    # Avoid @($list) around OrderedDictionary rows — that operator throws
    # "Argument types do not match" for List[object] of OrderedDictionary.
    if ($Entries -is [System.Collections.ICollection] -and $Entries -isnot [string]) {
        $releaseArray = [object[]]::new($Entries.Count)
        $i = 0
        foreach ($row in $Entries) {
            $releaseArray[$i] = $row
            $i++
        }
    } else {
        $releaseArray = [object[]]@($Entries)
    }
    $body = [ordered]@{
        schemaVersion = 1
        releases      = $releaseArray
    }
    $json = ConvertTo-Json -InputObject $body -Depth 8
    Write-AtomicTextFile -Path $HistoryPathFull -Content $json -SafeAncestorStopAt $SafeAncestorStopAt
}

function Read-ReleaseTransactionJournal {
    param([Parameter(Mandatory)][string]$JournalPath)
    if (-not (Test-Path -LiteralPath $JournalPath -PathType Leaf)) {
        throw 'Transaction journal file is missing.'
    }
    if (Test-HasReparseOrLinkLocal -PathToCheck $JournalPath) {
        throw 'Transaction journal must not be a symlink/junction/reparse file.'
    }
    $journal = Read-JsonObjectFile -Path $JournalPath -Label 'transaction journal'
    [void](Assert-StrictJsonIntegerLocal -Value $journal.schemaVersion -Name 'journal.schemaVersion' -Min 1 -Max 1)
    if ($null -eq $journal.transactionId -or $journal.transactionId -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$journal.transactionId)) {
        throw 'journal.transactionId must be a non-empty string.'
    }
    $seq = Assert-StrictJsonIntegerLocal -Value $journal.sequence -Name 'journal.sequence' -Min 1
    if ($null -eq $journal.status -or $journal.status -isnot [string]) {
        throw 'journal.status must be a string.'
    }
    $status = [string]$journal.status
    if ($status -notin @('pending', 'committed', 'aborted')) {
        throw "journal.status must be pending|committed|aborted, got: $status"
    }
    $baseline = Assert-FullSha256Hex -Value $journal.historyBaselineSha256 -Name 'journal.historyBaselineSha256'
    return ,([ordered]@{
            schemaVersion         = 1
            transactionId         = [string]$journal.transactionId
            sequence              = $seq
            status                = $status
            historyBaselineSha256 = $baseline
            updatedUtc            = $(if ($null -ne $journal.PSObject.Properties['updatedUtc'] -and $journal.updatedUtc -is [string]) { [string]$journal.updatedUtc } else { '' })
        })
}

function Write-ReleaseTransactionJournal {
    param(
        [Parameter(Mandatory)][string]$JournalPath,
        [Parameter(Mandatory)][string]$TransactionId,
        [Parameter(Mandatory)][long]$Sequence,
        [Parameter(Mandatory)][ValidateSet('pending', 'committed', 'aborted')][string]$Status,
        [Parameter(Mandatory)][string]$HistoryBaselineSha256,
        [Parameter(Mandatory)][string]$SafeAncestorStopAt
    )
    $body = [ordered]@{
        schemaVersion         = 1
        transactionId         = $TransactionId
        sequence              = $Sequence
        status                = $Status
        historyBaselineSha256 = $HistoryBaselineSha256.ToLowerInvariant()
        updatedUtc            = ([datetime]::UtcNow.ToString('o'))
    }
    $json = ConvertTo-Json -InputObject $body -Depth 6
    Write-AtomicTextFile -Path $JournalPath -Content $json -SafeAncestorStopAt $SafeAncestorStopAt
}

function Find-ReleaseHistoryTransactionMatch {
    param(
        [Parameter(Mandatory)]$Entries,
        [Parameter(Mandatory)][string]$TransactionId,
        [Parameter(Mandatory)][long]$Sequence
    )
    $exact = $false
    $sequenceOtherTxn = $false
    $txnOtherSequence = $false
    foreach ($rel in $Entries) {
        $seq = [long]$rel.sequence
        $tid = ''
        if ($rel -is [System.Collections.IDictionary]) {
            if ($rel.Contains('transactionId')) { $tid = [string]$rel['transactionId'] }
        } elseif ($null -ne $rel.PSObject.Properties['transactionId'] -and $null -ne $rel.transactionId) {
            $tid = [string]$rel.transactionId
        }

        if ($seq -eq $Sequence -and $tid -ceq $TransactionId) {
            $exact = $true
        } elseif ($seq -eq $Sequence -and -not [string]::IsNullOrWhiteSpace($tid) -and $tid -cne $TransactionId) {
            $sequenceOtherTxn = $true
        } elseif ($tid -ceq $TransactionId -and $seq -ne $Sequence) {
            $txnOtherSequence = $true
        } elseif ($seq -eq $Sequence -and [string]::IsNullOrWhiteSpace($tid)) {
            # Same sequence without transactionId cannot prove this transaction.
            $sequenceOtherTxn = $true
        }
    }
    return ,([ordered]@{
            ExactMatch         = $exact
            SequenceConflict   = $sequenceOtherTxn
            TransactionConflict = $txnOtherSequence
        })
}

function Assert-SequenceAvailableForBegin {
    param(
        [Parameter(Mandatory)]$HistoryState,
        [Parameter(Mandatory)][long]$Sequence
    )
    if ($Sequence -lt 1) {
        throw "Sequence must be a positive integer >= 1, got: $Sequence"
    }
    $maxSeq = [long]$HistoryState.maxSequence
    if ($Sequence -le $maxSeq) {
        throw "Sequence must be strictly greater than history maxSequence=$maxSeq; refused sequence=$Sequence (duplicate or non-monotonic)."
    }
    foreach ($existing in @($HistoryState.sequences)) {
        if ([long]$existing -eq $Sequence) {
            throw "Sequence $Sequence already exists in history.releases (not globally unique)."
        }
    }
}

function Assert-NoBlockingPendingJournal {
    param(
        [Parameter(Mandatory)][string]$JournalPath
    )
    if (-not (Test-Path -LiteralPath $JournalPath -PathType Leaf)) {
        return
    }
    if (Test-HasReparseOrLinkLocal -PathToCheck $JournalPath) {
        throw 'Existing transaction journal must not be a symlink/junction/reparse file.'
    }
    $existing = Read-ReleaseTransactionJournal -JournalPath $JournalPath
    if ($existing.status -eq 'pending') {
        throw "Conflicting pending journal exists (transactionId=$($existing.transactionId) sequence=$($existing.sequence)). Recover or abort before Begin."
    }
}

<#
.SYNOPSIS
    Begin a release-history transaction under an exclusive FileShare.None lock.
.DESCRIPTION
    Validates schemaVersion=1 history, refuses missing history, allows releases:[],
    writes a pending journal with history baseline hash, and keeps the lock held
    until Commit-ReleaseTransaction or Abort-ReleaseTransaction.
#>
function Begin-ReleaseTransaction {
    param(
        [Parameter(Mandatory)][string]$HistoryPath,
        [Parameter(Mandatory)]$Sequence,
        [Parameter(Mandatory)][string]$RepoRoot
    )

    $seq = Assert-StrictJsonIntegerLocal -Value $Sequence -Name 'Sequence' -Min 1

    $historyFull = Resolve-SafeHistoryPathForTransaction -HistoryPath $HistoryPath -RepoRoot $RepoRoot
    $lockPath = Get-ReleaseTransactionLockPath -HistoryPathFull $historyFull
    $journalPath = Get-ReleaseTransactionJournalPath -HistoryPathFull $historyFull
    $safeStop = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot '..'))

    $lockStream = $null
    try {
        $lockStream = Open-ReleaseTransactionLockStream -LockPath $lockPath

        # Re-resolve after lock: refuse if history vanished or became a link.
        $historyFull = Resolve-SafeHistoryPathForTransaction -HistoryPath $historyFull -RepoRoot $RepoRoot
        $parsed = Read-ReleaseHistoryValidated -HistoryPathFull $historyFull
        Assert-SequenceAvailableForBegin -HistoryState $parsed.State -Sequence $seq
        Assert-NoBlockingPendingJournal -JournalPath $journalPath

        $baseline = Get-FileSha256Lower -LiteralPath $historyFull
        $transactionId = [guid]::NewGuid().ToString('N')

        Write-ReleaseTransactionJournal `
            -JournalPath $journalPath `
            -TransactionId $transactionId `
            -Sequence $seq `
            -Status pending `
            -HistoryBaselineSha256 $baseline `
            -SafeAncestorStopAt $safeStop

        $result = New-ReleaseTransactionResultObject `
            -Status pending `
            -TransactionId $transactionId `
            -Sequence $seq `
            -HistoryPath $historyFull `
            -JournalPath $journalPath `
            -LockPath $lockPath `
            -HistorySha256 $baseline `
            -BaselineSha256 $baseline

        $session = [ordered]@{
            HistoryPath    = $historyFull
            JournalPath    = $journalPath
            LockPath       = $lockPath
            RepoRoot       = [System.IO.Path]::GetFullPath($RepoRoot)
            TransactionId  = $transactionId
            Sequence       = $seq
            BaselineSha256 = $baseline
            Status         = 'pending'
            LockStream     = $lockStream
            Result         = $result
        }
        # Transfer lock ownership to session; caller must Commit/Abort (or dispose).
        $lockStream = $null
        return ,$session
    } catch {
        Close-ReleaseTransactionLockStream -LockStream $lockStream
        throw
    }
}

<#
.SYNOPSIS
    Internal commit primitive: append history then mark journal committed.
.DESCRIPTION
    Does NOT verify signatures or build provenance. S4b wrappers must do that
    before calling this function. Always returns releasable=false.
#>
function Commit-ReleaseTransaction {
    param(
        [Parameter(Mandatory)]$Session
    )
    if ($null -eq $Session) { throw 'Session is required.' }
    if ($Session -isnot [System.Collections.IDictionary]) { throw 'Session must be a dictionary-like transaction handle.' }
    if ($Session['Status'] -cne 'pending') {
        throw "Commit requires pending session status, got: $($Session['Status'])"
    }

    $historyFull = [string]$Session['HistoryPath']
    $journalPath = [string]$Session['JournalPath']
    $lockPath = [string]$Session['LockPath']
    $repoRoot = [string]$Session['RepoRoot']
    $transactionId = [string]$Session['TransactionId']
    $seq = [long]$Session['Sequence']
    $baseline = [string]$Session['BaselineSha256']
    $safeStop = [System.IO.Path]::GetFullPath((Join-Path $repoRoot '..'))
    $committedUtc = ''

    try {
        # Pending Commit must hold the original live lock; never re-acquire here.
        Assert-ReleaseTransactionSessionLockHeld -Session $Session -Operation 'Commit'
        # Re-read journal under the held lock; refuse stale/forged sessions.
        Assert-ReleaseTransactionJournalMatchesPendingSession -Session $Session -Operation 'Commit'

        # Baseline recheck before mutating history — refuse non-collaborative writes.
        $currentHash = Get-FileSha256Lower -LiteralPath $historyFull
        if ($currentHash -cne $baseline) {
            throw "History baseline hash mismatch before commit; non-collaborative write refused. expected=$baseline actual=$currentHash"
        }

        $parsed = Read-ReleaseHistoryValidated -HistoryPathFull $historyFull
        Assert-SequenceAvailableForBegin -HistoryState $parsed.State -Sequence $seq
        $entries = ConvertTo-ReleaseHistoryEntries -History $parsed.History

        $committedUtc = [datetime]::UtcNow.ToString('o')
        [void]$entries.Add([ordered]@{
                sequence      = $seq
                transactionId = $transactionId
                committedUtc  = $committedUtc
            })

        Write-ReleaseHistoryFile -HistoryPathFull $historyFull -Entries $entries -SafeAncestorStopAt $safeStop
        $newHash = Get-FileSha256Lower -LiteralPath $historyFull

        Write-ReleaseTransactionJournal `
            -JournalPath $journalPath `
            -TransactionId $transactionId `
            -Sequence $seq `
            -Status committed `
            -HistoryBaselineSha256 $newHash `
            -SafeAncestorStopAt $safeStop

        $Session['Status'] = 'committed'
        $Session['BaselineSha256'] = $newHash

        return (New-ReleaseTransactionResultObject `
                -Status committed `
                -TransactionId $transactionId `
                -Sequence $seq `
                -HistoryPath $historyFull `
                -JournalPath $journalPath `
                -LockPath $lockPath `
                -HistorySha256 $newHash `
                -BaselineSha256 $baseline `
                -CommittedUtc $committedUtc)
    } finally {
        Close-ReleaseTransactionLockStream -LockStream $Session['LockStream']
        $Session['LockStream'] = $null
    }
}

<#
.SYNOPSIS
    Abort a pending transaction. Never deletes or decrements committed history sequences.
#>
function Abort-ReleaseTransaction {
    param(
        [Parameter(Mandatory)]$Session
    )
    if ($null -eq $Session) { throw 'Session is required.' }
    if ($Session -isnot [System.Collections.IDictionary]) { throw 'Session must be a dictionary-like transaction handle.' }
    if ($Session['Status'] -ceq 'committed') {
        throw 'Abort refused: transaction already committed; committed sequences are never decremented.'
    }

    $historyFull = [string]$Session['HistoryPath']
    $journalPath = [string]$Session['JournalPath']
    $lockPath = [string]$Session['LockPath']
    $repoRoot = [string]$Session['RepoRoot']
    $transactionId = [string]$Session['TransactionId']
    $seq = [long]$Session['Sequence']
    $baseline = [string]$Session['BaselineSha256']
    $safeStop = [System.IO.Path]::GetFullPath((Join-Path $repoRoot '..'))

    try {
        if ($Session['Status'] -ceq 'aborted') {
            return (New-ReleaseTransactionResultObject `
                    -Status aborted `
                    -TransactionId $transactionId `
                    -Sequence $seq `
                    -HistoryPath $historyFull `
                    -JournalPath $journalPath `
                    -LockPath $lockPath `
                    -HistorySha256 $(if (Test-Path -LiteralPath $historyFull -PathType Leaf) { Get-FileSha256Lower -LiteralPath $historyFull } else { '' }) `
                    -BaselineSha256 $baseline `
                    -Reasons @('already-aborted'))
        }

        if ($Session['Status'] -cne 'pending') {
            throw "Abort requires pending session status, got: $($Session['Status'])"
        }

        # Pending Abort must hold the original live lock; never re-acquire here.
        Assert-ReleaseTransactionSessionLockHeld -Session $Session -Operation 'Abort'
        Assert-ReleaseTransactionJournalMatchesPendingSession -Session $Session -Operation 'Abort'

        $historyHash = Get-FileSha256Lower -LiteralPath $historyFull
        if ($historyHash -cne $baseline) {
            # Failure after history append (journal still pending): Abort must not mark aborted.
            $parsed = Read-ReleaseHistoryValidated -HistoryPathFull $historyFull
            $entries = ConvertTo-ReleaseHistoryEntries -History $parsed.History
            $match = Find-ReleaseHistoryTransactionMatch -Entries $entries -TransactionId $transactionId -Sequence $seq
            if ($match.ExactMatch) {
                throw "Abort refused: history already contains exact committed transactionId/sequence (txn=$transactionId seq=$seq) while journal is still pending. Call Recover-ReleaseTransaction to reconcile; Abort must not mark journal aborted."
            }
            throw "Abort refused: history baseline hash mismatch under session lock; unexpected mutation. expected=$baseline actual=$historyHash"
        }

        # History byte-stable: safe to mark journal aborted.
        Write-ReleaseTransactionJournal `
            -JournalPath $journalPath `
            -TransactionId $transactionId `
            -Sequence $seq `
            -Status aborted `
            -HistoryBaselineSha256 $baseline `
            -SafeAncestorStopAt $safeStop

        $Session['Status'] = 'aborted'

        return (New-ReleaseTransactionResultObject `
                -Status aborted `
                -TransactionId $transactionId `
                -Sequence $seq `
                -HistoryPath $historyFull `
                -JournalPath $journalPath `
                -LockPath $lockPath `
                -HistorySha256 $historyHash `
                -BaselineSha256 $baseline)
    } finally {
        Close-ReleaseTransactionLockStream -LockStream $Session['LockStream']
        $Session['LockStream'] = $null
    }
}

<#
.SYNOPSIS
    Re-acquire the history lock and reconcile a journal after crash/interruption.
.DESCRIPTION
    Pending with no exact transactionId+sequence history row => conservative aborted.
    Pending with exact committed history row => journal repaired to committed.
    Sequence-only matches and conflicts are rejected. Never decrements committed history.
#>
function Recover-ReleaseTransaction {
    param(
        [Parameter(Mandatory)][string]$HistoryPath,
        [Parameter(Mandatory)][string]$RepoRoot
    )

    $historyFull = Resolve-SafeHistoryPathForTransaction -HistoryPath $HistoryPath -RepoRoot $RepoRoot
    $lockPath = Get-ReleaseTransactionLockPath -HistoryPathFull $historyFull
    $journalPath = Get-ReleaseTransactionJournalPath -HistoryPathFull $historyFull
    $safeStop = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot '..'))

    $lockStream = $null
    try {
        $lockStream = Open-ReleaseTransactionLockStream -LockPath $lockPath
        $historyFull = Resolve-SafeHistoryPathForTransaction -HistoryPath $historyFull -RepoRoot $RepoRoot

        if (-not (Test-Path -LiteralPath $journalPath -PathType Leaf)) {
            throw 'Transaction journal file is missing; Recover requires an existing journal.'
        }

        $journal = Read-ReleaseTransactionJournal -JournalPath $journalPath
        $parsed = Read-ReleaseHistoryValidated -HistoryPathFull $historyFull
        $entries = ConvertTo-ReleaseHistoryEntries -History $parsed.History
        $match = Find-ReleaseHistoryTransactionMatch -Entries $entries -TransactionId $journal.transactionId -Sequence $journal.sequence
        $historyHash = Get-FileSha256Lower -LiteralPath $historyFull

        if ($match.SequenceConflict -or $match.TransactionConflict) {
            throw "Journal/history conflict during Recover: sequence/transactionId correlation failed (journal txn=$($journal.transactionId) seq=$($journal.sequence)). Sequence-only matches are not accepted."
        }

        if ($journal.status -eq 'pending') {
            if ($match.ExactMatch) {
                Write-ReleaseTransactionJournal `
                    -JournalPath $journalPath `
                    -TransactionId $journal.transactionId `
                    -Sequence $journal.sequence `
                    -Status committed `
                    -HistoryBaselineSha256 $historyHash `
                    -SafeAncestorStopAt $safeStop
                return (New-ReleaseTransactionResultObject `
                        -Status 'recovered-committed' `
                        -TransactionId $journal.transactionId `
                        -Sequence $journal.sequence `
                        -HistoryPath $historyFull `
                        -JournalPath $journalPath `
                        -LockPath $lockPath `
                        -HistorySha256 $historyHash `
                        -BaselineSha256 $journal.historyBaselineSha256 `
                        -Reasons @('pending-journal-reconciled-to-committed'))
            }

            # Conservative: pending with no exact history correlation => aborted; history unchanged.
            Write-ReleaseTransactionJournal `
                -JournalPath $journalPath `
                -TransactionId $journal.transactionId `
                -Sequence $journal.sequence `
                -Status aborted `
                -HistoryBaselineSha256 $journal.historyBaselineSha256 `
                -SafeAncestorStopAt $safeStop
            $afterHash = Get-FileSha256Lower -LiteralPath $historyFull
            if ($afterHash -cne $historyHash) {
                throw 'Recover abort path unexpectedly mutated history bytes.'
            }
            return (New-ReleaseTransactionResultObject `
                    -Status 'recovered-aborted' `
                    -TransactionId $journal.transactionId `
                    -Sequence $journal.sequence `
                    -HistoryPath $historyFull `
                    -JournalPath $journalPath `
                    -LockPath $lockPath `
                    -HistorySha256 $historyHash `
                    -BaselineSha256 $journal.historyBaselineSha256 `
                    -Reasons @('pending-without-commit-conservatively-aborted'))
        }

        if ($journal.status -eq 'committed') {
            if (-not $match.ExactMatch) {
                throw "Journal status=committed but history lacks exact transactionId+sequence correlation (txn=$($journal.transactionId) seq=$($journal.sequence))."
            }
            return (New-ReleaseTransactionResultObject `
                    -Status committed `
                    -TransactionId $journal.transactionId `
                    -Sequence $journal.sequence `
                    -HistoryPath $historyFull `
                    -JournalPath $journalPath `
                    -LockPath $lockPath `
                    -HistorySha256 $historyHash `
                    -BaselineSha256 $journal.historyBaselineSha256 `
                    -Reasons @('already-committed'))
        }

        # aborted
        if ($match.ExactMatch) {
            throw "Journal status=aborted but history contains exact transactionId+sequence row (txn=$($journal.transactionId) seq=$($journal.sequence))."
        }
        return (New-ReleaseTransactionResultObject `
                -Status aborted `
                -TransactionId $journal.transactionId `
                -Sequence $journal.sequence `
                -HistoryPath $historyFull `
                -JournalPath $journalPath `
                -LockPath $lockPath `
                -HistorySha256 $historyHash `
                -BaselineSha256 $journal.historyBaselineSha256 `
                -Reasons @('already-aborted'))
    } finally {
        Close-ReleaseTransactionLockStream -LockStream $lockStream
    }
}
