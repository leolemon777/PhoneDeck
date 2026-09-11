<#
.SYNOPSIS
    Isolated pure-data tests for B03 S4a ReleaseTransaction foundation.

.DESCRIPTION
    All writes stay under repo outputs/release-transaction-tests/<guid>/.
    Fixtures are retained (no recursive cleanup). Child-process lock/termination
    tests only target processes this suite created. Does not read existing
    release history, signing materials, or product data.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "Test-ReleaseTransaction.ps1 requires PowerShell 7+. Current: $($PSVersionTable.PSVersion)"
}

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ReleaseDir = (Resolve-Path -LiteralPath (Join-Path $ScriptDir '..')).Path
$RepoRoot = (Resolve-Path -LiteralPath (Join-Path $ReleaseDir '..\..')).Path
$LibPath = Join-Path $ReleaseDir 'ReleaseTransaction.ps1'

. $LibPath

$passed = 0
$failed = 0
$fixtureRoots = New-Object System.Collections.Generic.List[string]
$ownedChildPids = New-Object System.Collections.Generic.List[int]

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        Write-Host "FAIL: $Message" -ForegroundColor Red
        $script:failed++
    } else {
        Write-Host "PASS: $Message" -ForegroundColor Green
        $script:passed++
    }
}

function Write-Utf8NoBom {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Content)
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Content, $utf8)
}

function Get-FileSha256LowerLocalTest {
    param([Parameter(Mandatory)][string]$LiteralPath)
    return (Get-FileHash -LiteralPath $LiteralPath -Algorithm SHA256).Hash.ToLowerInvariant()
}

function New-FixtureRoot {
    $root = Join-Path $RepoRoot ("outputs\release-transaction-tests\" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    [void]$script:fixtureRoots.Add($root)
    Write-Host "FIXTURE: $root"
    return $root
}

function New-HistoryFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [object[]]$Sequences = @(),
        [object[]]$Entries = $null
    )
    $releases = New-Object System.Collections.Generic.List[object]
    if ($null -ne $Entries) {
        foreach ($e in $Entries) {
            [void]$releases.Add($e)
        }
    } else {
        foreach ($s in @($Sequences)) {
            if ($null -eq $s) { continue }
            [void]$releases.Add([ordered]@{ sequence = [long]$s; note = 'fixture' })
        }
    }
    $body = [ordered]@{
        schemaVersion = 1
        releases      = @($releases.ToArray())
    }
    Write-Utf8NoBom -Path $Path -Content (ConvertTo-Json -InputObject $body -Depth 8)
}

function Read-JsonFile {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json -Depth 32)
}

function Stop-OwnedChildProcesses {
    foreach ($ownedPid in @($script:ownedChildPids)) {
        try {
            $p = Get-Process -Id $ownedPid -ErrorAction SilentlyContinue
            if ($null -ne $p) {
                Stop-Process -Id $ownedPid -Force -ErrorAction SilentlyContinue
            }
        } catch {
            # Only best-effort for suite-owned PIDs.
        }
    }
    $script:ownedChildPids.Clear()
}

function Start-OwnedPwsh {
    param(
        [Parameter(Mandatory)][string[]]$ArgumentList,
        [string]$WorkingDirectory = $RepoRoot
    )
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = (Get-Command pwsh).Source
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.WorkingDirectory = $WorkingDirectory
    foreach ($a in $ArgumentList) {
        [void]$psi.ArgumentList.Add($a)
    }
    $p = New-Object System.Diagnostics.Process
    $p.StartInfo = $psi
    [void]$p.Start()
    [void]$script:ownedChildPids.Add([int]$p.Id)
    return $p
}

# --- Static deny mirrors (supplement; behavioral tests below are authoritative) ---
$libText = Get-Content -LiteralPath $LibPath -Raw
Assert-True ($libText -notmatch '(?i)releasable\s*=\s*\$?true') 'static: library never sets releasable=true'
Assert-True ($libText -match 'transaction-foundation') 'static: library mentions transaction-foundation'
Assert-True ($libText -notmatch '(?i)SigningKey|ImportPkcs8|BEGIN\s+PRIVATE\s+KEY|privateKey') 'static: no private-key/signing CLI surface'
Assert-True ($libText -notmatch 'Remove-Item\s+-Recurse') 'static: no recursive cleanup'
Assert-True ($libText -match 'S4b') 'static: documents S4b signature/provenance gate before Commit'

$fx = New-FixtureRoot

# =============================================================================
# 1) Normal commit on explicit empty history
# =============================================================================
$histEmpty = Join-Path $fx 'history-empty.json'
New-HistoryFile -Path $histEmpty -Sequences @()
$beforeEmpty = Get-FileSha256LowerLocalTest $histEmpty
$session = Begin-ReleaseTransaction -HistoryPath $histEmpty -Sequence 1 -RepoRoot $RepoRoot
Assert-True ($session.Status -eq 'pending') 'begin empty history => pending'
Assert-True ($session.Result.mode -eq 'transaction-foundation') 'begin mode=transaction-foundation'
Assert-True ($session.Result.releasable -eq $false) 'begin releasable=false'
Assert-True ($null -ne $session.LockStream) 'begin holds lock stream'
$commit = Commit-ReleaseTransaction -Session $session
Assert-True ($commit.status -eq 'committed') 'commit empty history => committed'
Assert-True ($commit.mode -eq 'transaction-foundation') 'commit mode=transaction-foundation'
Assert-True ($commit.releasable -eq $false) 'commit releasable=false'
Assert-True ($commit.sequence -eq 1) 'commit sequence=1'
Assert-True ($null -eq $session.LockStream) 'commit releases lock stream'
$after = Read-JsonFile $histEmpty
Assert-True (@($after.releases).Count -eq 1) 'history has one release after commit'
Assert-True ($after.releases[0].sequence -eq 1) 'committed sequence=1'
Assert-True ($after.releases[0].transactionId -ceq $commit.transactionId) 'committed transactionId correlates'
Assert-True (-not [string]::IsNullOrWhiteSpace([string]$after.releases[0].committedUtc)) 'committedUtc present'
$journal = Read-JsonFile ($histEmpty + '.journal')
Assert-True ($journal.status -eq 'committed') 'journal status=committed'
Assert-True ($beforeEmpty -cne (Get-FileSha256LowerLocalTest $histEmpty)) 'history bytes changed on commit'

# =============================================================================
# 2) Missing history refused; empty[] allowed already covered
# =============================================================================
$missing = Join-Path $fx ("missing-history-" + [guid]::NewGuid().ToString('N') + '.json')
$missingThrew = $false
$missingMsg = ''
try {
    [void](Begin-ReleaseTransaction -HistoryPath $missing -Sequence 1 -RepoRoot $RepoRoot)
} catch {
    $missingThrew = $true
    $missingMsg = [string]$_.Exception.Message
}
Assert-True $missingThrew 'missing history throws'
Assert-True ($missingMsg -match 'HistoryPath file is missing') "missing history reason: $missingMsg"

# =============================================================================
# 3) Malformed history / non-array releases / bad schema
# =============================================================================
$malformed = Join-Path $fx 'history-malformed.json'
Write-Utf8NoBom -Path $malformed -Content '{ not-json '
$malThrew = $false
try { [void](Begin-ReleaseTransaction -HistoryPath $malformed -Sequence 1 -RepoRoot $RepoRoot) } catch { $malThrew = $true }
Assert-True $malThrew 'malformed JSON history refused'

$relObj = Join-Path $fx 'history-releases-object.json'
Write-Utf8NoBom -Path $relObj -Content '{"schemaVersion":1,"releases":{"sequence":1}}'
$objThrew = $false
try { [void](Begin-ReleaseTransaction -HistoryPath $relObj -Sequence 2 -RepoRoot $RepoRoot) } catch { $objThrew = $true }
Assert-True $objThrew 'releases object (non-array) refused'

# =============================================================================
# 4) Duplicate / non-monotonic history refused; sequence regress refused
# =============================================================================
$dup = Join-Path $fx 'history-dup.json'
Write-Utf8NoBom -Path $dup -Content '{"schemaVersion":1,"releases":[{"sequence":2},{"sequence":2}]}'
$dupThrew = $false
try { [void](Begin-ReleaseTransaction -HistoryPath $dup -Sequence 3 -RepoRoot $RepoRoot) } catch { $dupThrew = $true }
Assert-True $dupThrew 'duplicate sequences in history refused'

$nonmono = Join-Path $fx 'history-nonmono.json'
Write-Utf8NoBom -Path $nonmono -Content '{"schemaVersion":1,"releases":[{"sequence":5},{"sequence":4}]}'
$nmThrew = $false
try { [void](Begin-ReleaseTransaction -HistoryPath $nonmono -Sequence 6 -RepoRoot $RepoRoot) } catch { $nmThrew = $true }
Assert-True $nmThrew 'non-monotonic history refused'

$prior = Join-Path $fx 'history-prior.json'
New-HistoryFile -Path $prior -Sequences @(10, 20, 23)
$regThrew = $false
$regMsg = ''
try { [void](Begin-ReleaseTransaction -HistoryPath $prior -Sequence 23 -RepoRoot $RepoRoot) } catch {
    $regThrew = $true
    $regMsg = [string]$_.Exception.Message
}
Assert-True $regThrew 'sequence equal to max refused'
Assert-True ($regMsg -match 'strictly greater|maxSequence') "regress reason: $regMsg"
$reg2Threw = $false
try { [void](Begin-ReleaseTransaction -HistoryPath $prior -Sequence 22 -RepoRoot $RepoRoot) } catch { $reg2Threw = $true }
Assert-True $reg2Threw 'sequence below max refused'

# =============================================================================
# 5) Abort leaves history unchanged; does not decrement committed sequences
# =============================================================================
$histAbort = Join-Path $fx 'history-abort.json'
New-HistoryFile -Path $histAbort -Sequences @(1, 2)
$hashAbortBefore = Get-FileSha256LowerLocalTest $histAbort
$sessAbort = Begin-ReleaseTransaction -HistoryPath $histAbort -Sequence 3 -RepoRoot $RepoRoot
$abortRes = Abort-ReleaseTransaction -Session $sessAbort
Assert-True ($abortRes.status -eq 'aborted') 'abort => aborted'
Assert-True ($abortRes.releasable -eq $false) 'abort releasable=false'
Assert-True ((Get-FileSha256LowerLocalTest $histAbort) -ceq $hashAbortBefore) 'abort does not change history bytes'
$abortJournal = Read-JsonFile ($histAbort + '.journal')
Assert-True ($abortJournal.status -eq 'aborted') 'journal aborted'
$afterAbortHist = Read-JsonFile $histAbort
Assert-True (@($afterAbortHist.releases).Count -eq 2) 'abort did not remove committed sequences'
Assert-True ($null -eq $sessAbort.LockStream) 'abort releases lock'

# Commit then refuse Abort-on-committed
$histCommitAbort = Join-Path $fx 'history-commit-then-abort.json'
New-HistoryFile -Path $histCommitAbort -Sequences @()
$sessCA = Begin-ReleaseTransaction -HistoryPath $histCommitAbort -Sequence 1 -RepoRoot $RepoRoot
[void](Commit-ReleaseTransaction -Session $sessCA)
# Re-begin is needed for a new session; craft a fake committed session to ensure Abort refuses
$fakeCommitted = [ordered]@{
    HistoryPath    = $histCommitAbort
    JournalPath    = $histCommitAbort + '.journal'
    LockPath       = $histCommitAbort + '.lock'
    RepoRoot       = $RepoRoot
    TransactionId  = 'x'
    Sequence       = 1
    BaselineSha256 = 'a' * 64
    Status         = 'committed'
    LockStream     = $null
}
$caThrew = $false
try { [void](Abort-ReleaseTransaction -Session $fakeCommitted) } catch { $caThrew = $true }
Assert-True $caThrew 'abort refused after committed status'
$hashAfterCommit = Get-FileSha256LowerLocalTest $histCommitAbort
Assert-True ((Read-JsonFile $histCommitAbort).releases[0].sequence -eq 1) 'committed sequence still present'
Assert-True ((Get-FileSha256LowerLocalTest $histCommitAbort) -ceq $hashAfterCommit) 'history unchanged by refused abort'

# =============================================================================
# 6) Baseline tamper between Begin and Commit refused; history unchanged failure path
# =============================================================================
$histTamper = Join-Path $fx 'history-tamper.json'
New-HistoryFile -Path $histTamper -Sequences @(5)
$sessT = Begin-ReleaseTransaction -HistoryPath $histTamper -Sequence 6 -RepoRoot $RepoRoot
# External non-collaborative write while lock is held by this process (same process can still rewrite history file).
New-HistoryFile -Path $histTamper -Sequences @(5, 99)
$tamperHash = Get-FileSha256LowerLocalTest $histTamper
$tampThrew = $false
$tampMsg = ''
try {
    [void](Commit-ReleaseTransaction -Session $sessT)
} catch {
    $tampThrew = $true
    $tampMsg = [string]$_.Exception.Message
}
Assert-True $tampThrew 'commit refuses baseline tamper'
Assert-True ($tampMsg -match 'baseline hash mismatch|non-collaborative') "tamper reason: $tampMsg"
Assert-True ($null -eq $sessT.LockStream) 'failed commit still releases lock'
Assert-True ((Get-FileSha256LowerLocalTest $histTamper) -ceq $tamperHash) 'tampered history not overwritten by failed commit'
# Journal may still be pending; Recover should conservatively abort (no exact txn row for our id).
$recT = Recover-ReleaseTransaction -HistoryPath $histTamper -RepoRoot $RepoRoot
Assert-True ($recT.status -eq 'recovered-aborted') 'recover after tamper/failed commit => recovered-aborted'
Assert-True ((Get-FileSha256LowerLocalTest $histTamper) -ceq $tamperHash) 'recover abort keeps tampered history bytes (no decrement API)'

# =============================================================================
# 7) Crash point A: pending journal, no history row => Recover aborts, history unchanged
# =============================================================================
$histCrashA = Join-Path $fx 'history-crash-a.json'
New-HistoryFile -Path $histCrashA -Sequences @(1)
$hashCrashA = Get-FileSha256LowerLocalTest $histCrashA
$readyA = Join-Path $fx 'crash-a-ready.txt'
$holdScriptA = Join-Path $fx 'crash-a-hold.ps1'
Write-Utf8NoBom -Path $holdScriptA -Content @'
param(
    [string]$LibPath,
    [string]$HistoryPath,
    [string]$RepoRoot,
    [long]$Sequence,
    [string]$ReadyFile
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. $LibPath
$session = Begin-ReleaseTransaction -HistoryPath $HistoryPath -Sequence $Sequence -RepoRoot $RepoRoot
[System.IO.File]::WriteAllText($ReadyFile, $session.TransactionId)
# Hold lock until killed (simulates crash while pending).
while ($true) { Start-Sleep -Seconds 30 }
'@
$childA = Start-OwnedPwsh -ArgumentList @(
    '-NoProfile', '-File', $holdScriptA,
    '-LibPath', $LibPath,
    '-HistoryPath', $histCrashA,
    '-RepoRoot', $RepoRoot,
    '-Sequence', '2',
    '-ReadyFile', $readyA
)
$deadlineA = [datetime]::UtcNow.AddSeconds(30)
while (-not (Test-Path -LiteralPath $readyA) -and [datetime]::UtcNow -lt $deadlineA) {
    Start-Sleep -Milliseconds 100
}
Assert-True (Test-Path -LiteralPath $readyA) 'crash-A child reached pending ready'
$txnA = (Get-Content -LiteralPath $readyA -Raw).Trim()

# Concurrent Begin must fail while child holds FileShare.None lock.
$lockContendThrew = $false
$lockContendMsg = ''
try {
    [void](Begin-ReleaseTransaction -HistoryPath $histCrashA -Sequence 3 -RepoRoot $RepoRoot)
} catch {
    $lockContendThrew = $true
    $lockContendMsg = [string]$_.Exception.Message
}
Assert-True $lockContendThrew 'real child-process lock contention refused second Begin'
Assert-True ($lockContendMsg -match 'lock is held|FileShare\.None|another process') "lock contention reason: $lockContendMsg"

# Terminate ONLY the owned fixture child.
Stop-Process -Id $childA.Id -Force -ErrorAction SilentlyContinue
$childA.WaitForExit(10000) | Out-Null
[void]$script:ownedChildPids.Remove([int]$childA.Id)

$recA = Recover-ReleaseTransaction -HistoryPath $histCrashA -RepoRoot $RepoRoot
Assert-True ($recA.status -eq 'recovered-aborted') 'crash-A recover => recovered-aborted'
Assert-True ($recA.transactionId -ceq $txnA) 'crash-A recover correlates exact transactionId'
Assert-True ((Get-FileSha256LowerLocalTest $histCrashA) -ceq $hashCrashA) 'crash-A history unchanged'
Assert-True ((Read-JsonFile ($histCrashA + '.journal')).status -eq 'aborted') 'crash-A journal aborted'

# =============================================================================
# 8) Crash point B: history has exact txn+seq, journal still pending => reconcile committed
# =============================================================================
$histCrashB = Join-Path $fx 'history-crash-b.json'
New-HistoryFile -Path $histCrashB -Sequences @(1)
$sessB = Begin-ReleaseTransaction -HistoryPath $histCrashB -Sequence 2 -RepoRoot $RepoRoot
$txnB = [string]$sessB.TransactionId
$baselineB = [string]$sessB.BaselineSha256
# Simulate commit crash after history append, before journal flip: write history manually, leave journal pending, drop lock.
$entriesB = @(
    [ordered]@{ sequence = 1; note = 'fixture' },
    [ordered]@{ sequence = 2; transactionId = $txnB; committedUtc = ([datetime]::UtcNow.ToString('o')) }
)
Write-ReleaseHistoryFile -HistoryPathFull $histCrashB -Entries $entriesB -SafeAncestorStopAt ([System.IO.Path]::GetFullPath((Join-Path $RepoRoot '..')))
# Force journal to remain pending with original baseline (crash before journal update).
Write-ReleaseTransactionJournal -JournalPath ($histCrashB + '.journal') -TransactionId $txnB -Sequence 2 -Status pending -HistoryBaselineSha256 $baselineB -SafeAncestorStopAt ([System.IO.Path]::GetFullPath((Join-Path $RepoRoot '..')))
Close-ReleaseTransactionLockStream -LockStream $sessB.LockStream
$sessB.LockStream = $null
$hashCrashB = Get-FileSha256LowerLocalTest $histCrashB

$recB = Recover-ReleaseTransaction -HistoryPath $histCrashB -RepoRoot $RepoRoot
Assert-True ($recB.status -eq 'recovered-committed') 'crash-B recover => recovered-committed'
Assert-True ($recB.transactionId -ceq $txnB) 'crash-B exact transactionId'
Assert-True ((Read-JsonFile ($histCrashB + '.journal')).status -eq 'committed') 'crash-B journal repaired committed'
Assert-True ((Get-FileSha256LowerLocalTest $histCrashB) -ceq $hashCrashB) 'crash-B history bytes unchanged by reconcile'
Assert-True ((Read-JsonFile $histCrashB).releases.Count -eq 2) 'crash-B retained committed sequences'

# =============================================================================
# 9) Sequence-only match must NOT reconcile as this transaction (conflict)
# =============================================================================
$histSeqOnly = Join-Path $fx 'history-seq-only-conflict.json'
New-HistoryFile -Path $histSeqOnly -Sequences @()
# Craft pending journal for txn AAA seq 7, but history has seq 7 under different txn BBB.
Write-Utf8NoBom -Path $histSeqOnly -Content (@'
{"schemaVersion":1,"releases":[{"sequence":7,"transactionId":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","committedUtc":"2026-01-01T00:00:00.0000000Z"}]}
'@)
$baselineSO = Get-FileSha256LowerLocalTest $histSeqOnly
Write-ReleaseTransactionJournal -JournalPath ($histSeqOnly + '.journal') -TransactionId 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' -Sequence 7 -Status pending -HistoryBaselineSha256 $baselineSO -SafeAncestorStopAt ([System.IO.Path]::GetFullPath((Join-Path $RepoRoot '..')))
# Touch lock file existence without holding it (Recover will acquire).
[System.IO.File]::WriteAllText(($histSeqOnly + '.lock'), '')
$soThrew = $false
$soMsg = ''
try {
    [void](Recover-ReleaseTransaction -HistoryPath $histSeqOnly -RepoRoot $RepoRoot)
} catch {
    $soThrew = $true
    $soMsg = [string]$_.Exception.Message
}
Assert-True $soThrew 'sequence-only / foreign txn conflict rejected'
Assert-True ($soMsg -match 'conflict|correlation|Sequence-only') "seq-only conflict reason: $soMsg"
Assert-True ((Get-FileSha256LowerLocalTest $histSeqOnly) -ceq $baselineSO) 'conflict recover leaves history unchanged'

# =============================================================================
# 10) Same-sequence contention after successful commit
# =============================================================================
$histSame = Join-Path $fx 'history-same-seq.json'
New-HistoryFile -Path $histSame -Sequences @()
$sessS1 = Begin-ReleaseTransaction -HistoryPath $histSame -Sequence 10 -RepoRoot $RepoRoot
[void](Commit-ReleaseTransaction -Session $sessS1)
$sameThrew = $false
try { [void](Begin-ReleaseTransaction -HistoryPath $histSame -Sequence 10 -RepoRoot $RepoRoot) } catch { $sameThrew = $true }
Assert-True $sameThrew 'same sequence after commit refused at Begin'

# Concurrent same-sequence while lock held (second child attempt via in-process after first Begin)
$histSame2 = Join-Path $fx 'history-same-seq-lock.json'
New-HistoryFile -Path $histSame2 -Sequences @()
$readyS = Join-Path $fx 'same-seq-ready.txt'
$holdScriptS = Join-Path $fx 'same-seq-hold.ps1'
Write-Utf8NoBom -Path $holdScriptS -Content @'
param(
    [string]$LibPath,
    [string]$HistoryPath,
    [string]$RepoRoot,
    [long]$Sequence,
    [string]$ReadyFile
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. $LibPath
$session = Begin-ReleaseTransaction -HistoryPath $HistoryPath -Sequence $Sequence -RepoRoot $RepoRoot
[System.IO.File]::WriteAllText($ReadyFile, $session.TransactionId)
while ($true) { Start-Sleep -Seconds 30 }
'@
$childS = Start-OwnedPwsh -ArgumentList @(
    '-NoProfile', '-File', $holdScriptS,
    '-LibPath', $LibPath,
    '-HistoryPath', $histSame2,
    '-RepoRoot', $RepoRoot,
    '-Sequence', '11',
    '-ReadyFile', $readyS
)
$deadlineS = [datetime]::UtcNow.AddSeconds(30)
while (-not (Test-Path -LiteralPath $readyS) -and [datetime]::UtcNow -lt $deadlineS) {
    Start-Sleep -Milliseconds 100
}
Assert-True (Test-Path -LiteralPath $readyS) 'same-seq child pending ready'
$sameLockThrew = $false
try { [void](Begin-ReleaseTransaction -HistoryPath $histSame2 -Sequence 11 -RepoRoot $RepoRoot) } catch { $sameLockThrew = $true }
Assert-True $sameLockThrew 'same sequence concurrent Begin refused under lock'
Stop-Process -Id $childS.Id -Force -ErrorAction SilentlyContinue
$childS.WaitForExit(10000) | Out-Null
[void]$script:ownedChildPids.Remove([int]$childS.Id)
[void](Recover-ReleaseTransaction -HistoryPath $histSame2 -RepoRoot $RepoRoot)

# =============================================================================
# 11) Path escape refused (outside outputs)
# =============================================================================
$escapePath = Join-Path $RepoRoot 'scratch\release-txn-escape-history.json'
$escapeDir = Split-Path -Parent $escapePath
if (-not (Test-Path -LiteralPath $escapeDir)) {
    New-Item -ItemType Directory -Path $escapeDir -Force | Out-Null
}
Write-Utf8NoBom -Path $escapePath -Content '{"schemaVersion":1,"releases":[]}'
$escThrew = $false
$escMsg = ''
try {
    [void](Begin-ReleaseTransaction -HistoryPath $escapePath -Sequence 1 -RepoRoot $RepoRoot)
} catch {
    $escThrew = $true
    $escMsg = [string]$_.Exception.Message
}
Assert-True $escThrew 'path escape outside outputs refused'
Assert-True ($escMsg -match 'outputs|path escape') "escape reason: $escMsg"

# =============================================================================
# 12) Pending journal blocks new Begin until Recover
# =============================================================================
$histPend = Join-Path $fx 'history-pending-block.json'
New-HistoryFile -Path $histPend -Sequences @()
$sessP = Begin-ReleaseTransaction -HistoryPath $histPend -Sequence 1 -RepoRoot $RepoRoot
Close-ReleaseTransactionLockStream -LockStream $sessP.LockStream
$sessP.LockStream = $null
$pendBlockThrew = $false
try { [void](Begin-ReleaseTransaction -HistoryPath $histPend -Sequence 2 -RepoRoot $RepoRoot) } catch { $pendBlockThrew = $true }
Assert-True $pendBlockThrew 'pending journal blocks another Begin'
$recP = Recover-ReleaseTransaction -HistoryPath $histPend -RepoRoot $RepoRoot
Assert-True ($recP.status -eq 'recovered-aborted') 'recover clears abandoned pending via abort'
$sessionAfter = Begin-ReleaseTransaction -HistoryPath $histPend -Sequence 1 -RepoRoot $RepoRoot
[void](Abort-ReleaseTransaction -Session $sessionAfter)

# =============================================================================
# 13) Lock file exists does NOT mean lock is held
# =============================================================================
$histStaleLock = Join-Path $fx 'history-stale-lock.json'
New-HistoryFile -Path $histStaleLock -Sequences @()
[System.IO.File]::WriteAllText(($histStaleLock + '.lock'), 'stale')
$sessStale = Begin-ReleaseTransaction -HistoryPath $histStaleLock -Sequence 1 -RepoRoot $RepoRoot
Assert-True ($sessStale.Status -eq 'pending') 'stale lock file alone does not block Begin'
[void](Abort-ReleaseTransaction -Session $sessStale)

# =============================================================================
# 14) Failed-commit stale handle must not Abort a newer transaction journal
# =============================================================================
$histStaleAbort = Join-Path $fx 'history-stale-abort-newer.json'
New-HistoryFile -Path $histStaleAbort -Sequences @()
$sessOld = Begin-ReleaseTransaction -HistoryPath $histStaleAbort -Sequence 1 -RepoRoot $RepoRoot
$oldTxn = [string]$sessOld.TransactionId
# Force Commit failure (baseline tamper) => lock released, Status remains pending.
Write-Utf8NoBom -Path $histStaleAbort -Content '{"schemaVersion":1,"releases":[],"tampered":true}'
$oldCommitThrew = $false
try { [void](Commit-ReleaseTransaction -Session $sessOld) } catch { $oldCommitThrew = $true }
Assert-True $oldCommitThrew 'stale-abort setup: commit failed after tamper'
Assert-True ($sessOld.Status -eq 'pending') 'stale-abort setup: session Status still pending after failed commit'
Assert-True ($null -eq $sessOld.LockStream) 'stale-abort setup: failed commit released lock'
# Recover clears abandoned pending so a newer Begin can proceed.
$recOld = Recover-ReleaseTransaction -HistoryPath $histStaleAbort -RepoRoot $RepoRoot
Assert-True ($recOld.status -eq 'recovered-aborted') 'stale-abort setup: recover aborted old pending'
$sessNew = Begin-ReleaseTransaction -HistoryPath $histStaleAbort -Sequence 1 -RepoRoot $RepoRoot
$newTxn = [string]$sessNew.TransactionId
Assert-True ($newTxn -cne $oldTxn) 'stale-abort setup: newer transactionId differs'
$journalBeforeStaleAbort = Get-Content -LiteralPath ($histStaleAbort + '.journal') -Raw -Encoding utf8
$staleAbortThrew = $false
$staleAbortMsg = ''
try { [void](Abort-ReleaseTransaction -Session $sessOld) } catch { $staleAbortThrew = $true; $staleAbortMsg = $_.Exception.Message }
Assert-True $staleAbortThrew 'stale failed-commit Abort refused (no live lock)'
Assert-True ($staleAbortMsg -match 'live exclusive session lock|lock is missing|Recover-ReleaseTransaction') "stale Abort reason: $staleAbortMsg"
$journalAfterStaleAbort = Get-Content -LiteralPath ($histStaleAbort + '.journal') -Raw -Encoding utf8
Assert-True ($journalAfterStaleAbort -ceq $journalBeforeStaleAbort) 'stale Abort did not rewrite newer journal bytes'
$jNewer = Read-JsonFile ($histStaleAbort + '.journal')
Assert-True ($jNewer.status -eq 'pending') 'newer journal remains pending'
Assert-True ($jNewer.transactionId -ceq $newTxn) 'newer journal transactionId preserved'
Assert-True ($jNewer.transactionId -cne $oldTxn) 'newer journal not overwritten with old transactionId'
Assert-True ($sessNew.Status -eq 'pending') 'newer session still pending'
Assert-True ($null -ne $sessNew.LockStream) 'newer session still holds lock'
[void](Abort-ReleaseTransaction -Session $sessNew)

# =============================================================================
# 15) Disposed lock stream refuses Commit/Abort; journal untouched
# =============================================================================
$histDisposed = Join-Path $fx 'history-disposed-lock.json'
New-HistoryFile -Path $histDisposed -Sequences @()
$sessDisp = Begin-ReleaseTransaction -HistoryPath $histDisposed -Sequence 1 -RepoRoot $RepoRoot
$sessDisp.LockStream.Dispose()
# Leave non-null disposed reference — must still refuse.
Assert-True ($null -ne $sessDisp.LockStream) 'disposed setup: LockStream reference still non-null'
$dispCommitThrew = $false
$dispCommitMsg = ''
try { [void](Commit-ReleaseTransaction -Session $sessDisp) } catch { $dispCommitThrew = $true; $dispCommitMsg = $_.Exception.Message }
Assert-True $dispCommitThrew 'Commit refused on disposed lock'
Assert-True ($dispCommitMsg -match 'non-disposed|disposed|closed|live exclusive') "disposed Commit reason: $dispCommitMsg"
# Commit finally nulls LockStream; recover then cover Abort-on-disposed separately.
Assert-True ($null -eq $sessDisp.LockStream) 'failed Commit cleared disposed LockStream in finally'
Assert-True ((Read-JsonFile ($histDisposed + '.journal')).status -eq 'pending') 'disposed Commit left journal pending'
$recDisp = Recover-ReleaseTransaction -HistoryPath $histDisposed -RepoRoot $RepoRoot
Assert-True ($recDisp.status -eq 'recovered-aborted') 'recover after disposed-commit attempt'
$sessDisp2 = Begin-ReleaseTransaction -HistoryPath $histDisposed -Sequence 1 -RepoRoot $RepoRoot
$disp2Txn = [string]$sessDisp2.TransactionId
$disp2JournalBefore = Get-Content -LiteralPath ($histDisposed + '.journal') -Raw -Encoding utf8
$sessDisp2.LockStream.Dispose()
$dispAbortThrew = $false
$dispAbortMsg = ''
try { [void](Abort-ReleaseTransaction -Session $sessDisp2) } catch { $dispAbortThrew = $true; $dispAbortMsg = $_.Exception.Message }
Assert-True $dispAbortThrew 'Abort refused on disposed lock'
Assert-True ($dispAbortMsg -match 'non-disposed|disposed|closed|live exclusive') "disposed Abort reason: $dispAbortMsg"
$disp2JournalAfter = Get-Content -LiteralPath ($histDisposed + '.journal') -Raw -Encoding utf8
Assert-True ($disp2JournalAfter -ceq $disp2JournalBefore) 'disposed Abort did not rewrite journal'
$jDisp2 = Read-JsonFile ($histDisposed + '.journal')
Assert-True ($jDisp2.status -eq 'pending') 'disposed Abort left journal pending'
Assert-True ($jDisp2.transactionId -ceq $disp2Txn) 'disposed Abort left same transactionId'
[void](Recover-ReleaseTransaction -HistoryPath $histDisposed -RepoRoot $RepoRoot)

# =============================================================================
# 16) Mismatched journal correlation refuses Commit/Abort under held lock
# =============================================================================
$histMismatch = Join-Path $fx 'history-journal-mismatch.json'
New-HistoryFile -Path $histMismatch -Sequences @()
$sessMis = Begin-ReleaseTransaction -HistoryPath $histMismatch -Sequence 1 -RepoRoot $RepoRoot
$misBaseline = [string]$sessMis.BaselineSha256
$foreignTxn = 'ffffffffffffffffffffffffffffffff'
$safeStopMis = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot '..'))
Write-ReleaseTransactionJournal `
    -JournalPath ($histMismatch + '.journal') `
    -TransactionId $foreignTxn `
    -Sequence 1 `
    -Status pending `
    -HistoryBaselineSha256 $misBaseline `
    -SafeAncestorStopAt $safeStopMis
$misCommitThrew = $false
$misCommitMsg = ''
try { [void](Commit-ReleaseTransaction -Session $sessMis) } catch { $misCommitThrew = $true; $misCommitMsg = $_.Exception.Message }
Assert-True $misCommitThrew 'Commit refused on journal transactionId mismatch'
Assert-True ($misCommitMsg -match 'transactionId mismatch|another transaction') "mismatch Commit reason: $misCommitMsg"
Assert-True ((Read-JsonFile ($histMismatch + '.journal')).transactionId -ceq $foreignTxn) 'mismatch Commit did not overwrite foreign journal'
Assert-True (@((Read-JsonFile $histMismatch).releases).Count -eq 0) 'mismatch Commit did not append history'
# Fresh session for Abort mismatch (prior Commit finally released lock).
$recMis = Recover-ReleaseTransaction -HistoryPath $histMismatch -RepoRoot $RepoRoot
Assert-True ($recMis.status -eq 'recovered-aborted') 'recover foreign pending after mismatch commit refuse'
$sessMis2 = Begin-ReleaseTransaction -HistoryPath $histMismatch -Sequence 1 -RepoRoot $RepoRoot
$mis2Txn = [string]$sessMis2.TransactionId
$mis2Baseline = [string]$sessMis2.BaselineSha256
Write-ReleaseTransactionJournal `
    -JournalPath ($histMismatch + '.journal') `
    -TransactionId $foreignTxn `
    -Sequence 1 `
    -Status pending `
    -HistoryBaselineSha256 $mis2Baseline `
    -SafeAncestorStopAt $safeStopMis
$misJournalBeforeAbort = Get-Content -LiteralPath ($histMismatch + '.journal') -Raw -Encoding utf8
$misAbortThrew = $false
$misAbortMsg = ''
try { [void](Abort-ReleaseTransaction -Session $sessMis2) } catch { $misAbortThrew = $true; $misAbortMsg = $_.Exception.Message }
Assert-True $misAbortThrew 'Abort refused on journal transactionId mismatch'
Assert-True ($misAbortMsg -match 'transactionId mismatch|another transaction') "mismatch Abort reason: $misAbortMsg"
Assert-True ((Get-Content -LiteralPath ($histMismatch + '.journal') -Raw -Encoding utf8) -ceq $misJournalBeforeAbort) 'mismatch Abort left foreign journal bytes unchanged'
Assert-True ((Read-JsonFile ($histMismatch + '.journal')).transactionId -ceq $foreignTxn) 'mismatch Abort did not write session transactionId'
Assert-True ($mis2Txn -cne $foreignTxn) 'mismatch setup: session txn differs from foreign'
[void](Recover-ReleaseTransaction -HistoryPath $histMismatch -RepoRoot $RepoRoot)

# =============================================================================
# 17) History appended before journal update: Abort must require Recover
# =============================================================================
$histAppendAbort = Join-Path $fx 'history-appended-before-journal.json'
New-HistoryFile -Path $histAppendAbort -Sequences @()
$sessAA = Begin-ReleaseTransaction -HistoryPath $histAppendAbort -Sequence 1 -RepoRoot $RepoRoot
$aaTxn = [string]$sessAA.TransactionId
$aaSeq = [long]$sessAA.Sequence
$aaBaseline = [string]$sessAA.BaselineSha256
$safeStopAA = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot '..'))
# Simulate Commit success through history append, crash before journal mark.
$aaEntries = New-Object System.Collections.Generic.List[object]
[void]$aaEntries.Add([ordered]@{
        sequence      = $aaSeq
        transactionId = $aaTxn
        committedUtc  = ([datetime]::UtcNow.ToString('o'))
    })
Write-ReleaseHistoryFile -HistoryPathFull $histAppendAbort -Entries $aaEntries -SafeAncestorStopAt $safeStopAA
$aaHistHash = Get-FileSha256LowerLocalTest $histAppendAbort
Assert-True ($aaHistHash -cne $aaBaseline) 'append-before-journal setup: history hash moved'
$aaJournalBefore = Get-Content -LiteralPath ($histAppendAbort + '.journal') -Raw -Encoding utf8
$aaAbortThrew = $false
$aaAbortMsg = ''
try { [void](Abort-ReleaseTransaction -Session $sessAA) } catch { $aaAbortThrew = $true; $aaAbortMsg = $_.Exception.Message }
Assert-True $aaAbortThrew 'Abort refused when history already has exact committed txn'
Assert-True ($aaAbortMsg -match 'already contains exact committed|Recover-ReleaseTransaction|must not mark journal aborted') "append-before-journal Abort reason: $aaAbortMsg"
$aaJournalAfter = Get-Content -LiteralPath ($histAppendAbort + '.journal') -Raw -Encoding utf8
Assert-True ($aaJournalAfter -ceq $aaJournalBefore) 'Abort did not mark journal aborted after history append'
$jAA = Read-JsonFile ($histAppendAbort + '.journal')
Assert-True ($jAA.status -eq 'pending') 'journal remains pending evidence for Recover'
Assert-True ($jAA.transactionId -ceq $aaTxn) 'pending journal transactionId preserved'
$histAA = Read-JsonFile $histAppendAbort
Assert-True ($histAA.releases.Count -eq 1) 'history committed row preserved'
Assert-True ($histAA.releases[0].transactionId -ceq $aaTxn) 'history exact transactionId preserved'
Assert-True ((Get-FileSha256LowerLocalTest $histAppendAbort) -ceq $aaHistHash) 'history bytes unchanged by refused Abort'
$recAA = Recover-ReleaseTransaction -HistoryPath $histAppendAbort -RepoRoot $RepoRoot
Assert-True ($recAA.status -eq 'recovered-committed') 'Recover reconciles history-appended pending => committed'
Assert-True ((Read-JsonFile ($histAppendAbort + '.journal')).status -eq 'committed') 'Recover marked journal committed'
Assert-True ((Get-FileSha256LowerLocalTest $histAppendAbort) -ceq $aaHistHash) 'Recover preserved history bytes'

# Cleanup owned children only (fixtures retained).
foreach ($invalidSequence in @(1.5, '2', $true, 0, -1)) {
    $invalidHistory = Join-Path $fx ('invalid-sequence-' + [guid]::NewGuid().ToString('N') + '.json')
    New-HistoryFile -Path $invalidHistory -Sequences @()
    $beforeInvalid = Get-FileSha256LowerLocalTest $invalidHistory
    $invalidSession = $null
    $refused = $false
    try {
        $invalidSession = Begin-ReleaseTransaction -HistoryPath $invalidHistory -Sequence $invalidSequence -RepoRoot $RepoRoot
    } catch {
        $refused = $true
    } finally {
        if ($null -ne $invalidSession) { [void](Abort-ReleaseTransaction -Session $invalidSession) }
    }
    Assert-True $refused "invalid sequence refused without coercion: $invalidSequence"
    Assert-True ((Get-FileSha256LowerLocalTest $invalidHistory) -ceq $beforeInvalid) 'invalid sequence leaves history unchanged'
    Assert-True (-not (Test-Path -LiteralPath ($invalidHistory + '.journal'))) 'invalid sequence creates no journal'
}

Stop-OwnedChildProcesses

Write-Host ''
Write-Host "FIXTURE_ROOTS_RETAINED: $($fixtureRoots.Count)"
foreach ($r in $fixtureRoots) { Write-Host "  $r" }
Write-Host "Passed: $passed / Failed: $failed"
if ($failed -gt 0) {
    exit 1
}
exit 0
