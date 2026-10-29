[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Test {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw "Pairing migration synthetic test failed: $Message"
    }
}

function Import-RequiredFunctionsFromSource {
    param([Parameter(Mandatory = $true)][string]$SourcePath)

    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        $SourcePath,
        [ref]$tokens,
        [ref]$parseErrors)
    Assert-Test ($parseErrors.Count -eq 0) 'main migration script has PowerShell parse errors.'

    # Do not dot-source the main script: its parameter block and top-level
    # workflow would resolve tools or contact ADB.  Load only the functions
    # used by this local DPAPI/ACL test.
    $required = @(
        'Throw-Safe',
        'Get-PayloadMetadata',
        'Get-CurrentUserSid',
        'Set-CurrentUserOnlyAcl',
        'Assert-CurrentUserOnlyAcl',
        'Write-ProtectedBackup',
        'Read-ProtectedBackup'
    )
    $blocks = @()
    foreach ($name in $required) {
        $matches = @($ast.Find({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq $name
        }, $true))
        Assert-Test ($matches.Count -eq 1) "could not isolate function $name from the main script."
        $blocks += [scriptblock]::Create($matches[0].Extent.Text)
    }
    return $blocks
}

function Invoke-PhysicalImportGuardTest {
    param([Parameter(Mandatory = $true)][string]$MainScript)

    $pwsh = Join-Path $PSHOME 'pwsh.exe'
    Assert-Test (Test-Path -LiteralPath $pwsh -PathType Leaf) 'pwsh executable is missing.'
    $arguments = @(
        '-NoProfile',
        '-NonInteractive',
        '-File', $MainScript,
        '-Mode', 'Import',
        '-Adb', 'C:\this-adb-must-never-be-resolved.exe',
        '-Serial', 'R5CN404KQZR',
        '-JavaHome', 'C:\this-java-must-never-be-resolved',
        '-AndroidSdk', 'C:\this-sdk-must-never-be-resolved',
        '-BackupFile', 'C:\this-backup-must-never-be-read.dpapi'
    )
    $output = @(& $pwsh @arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $text = ($output | Out-String)
    Assert-Test ($exitCode -ne 0) 'physical Import guard unexpectedly succeeded.'
    Assert-Test ($text -match '(?i)TestEmulator') 'physical Import guard did not run before ADB/tool resolution.'
}

$mainScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'Invoke-PairingMigration.ps1'))
$tokens = $null
$syntaxErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    $mainScript,
    [ref]$tokens,
    [ref]$syntaxErrors) | Out-Null
Assert-Test ($syntaxErrors.Count -eq 0) 'main migration script syntax check failed.'

# This variable is referenced by the extracted production functions.
$PackageName = 'com.codex.phonedeck'
$functionBlocks = Import-RequiredFunctionsFromSource -SourcePath $mainScript
foreach ($functionBlock in @($functionBlocks)) {
    . $functionBlock
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) `
    ('phonedeck-pairing-migration-test-' + [guid]::NewGuid().ToString('N'))
$tempRootFull = [System.IO.Path]::GetFullPath($tempRoot)
$tempRootCreated = $false
$backupPath = Join-Path $tempRootFull 'pairings.current-user.dpapi'
$corruptPath = Join-Path $tempRootFull 'pairings.corrupt.dpapi'
$payloadJson = '{"format":1,"package":"com.codex.phonedeck","uid":10851,"activeComputerId":"synthetic-computer","devices":[{"computerId":"synthetic-computer","displayName":"Synthetic Computer","platform":"Windows","slot":1,"lastSeenAt":0,"lanAddresses":["192.0.2.1"],"lanPort":8766,"lanToken":"synthetic-token","clientId":"synthetic-client","sharedGroup":true,"certificateSha256":"synthetic-certificate","lastGoodAddress":"192.0.2.1","nameLocked":false}]}'
$result = $null

try {
    [void][System.IO.Directory]::CreateDirectory($tempRootFull)
    $tempRootCreated = $true

    $written = Write-ProtectedBackup -Path $backupPath -PayloadJson $payloadJson
    Assert-Test (Test-Path -LiteralPath $backupPath -PathType Leaf) 'protected backup was not created.'
    Assert-CurrentUserOnlyAcl -Path $backupPath

    $read = Read-ProtectedBackup -Path $backupPath
    Assert-Test ($read.RoundtripVerified -eq $true) 'DPAPI roundtrip was not reported as verified.'
    Assert-Test ($read.DeviceCount -eq 1 -and $read.SharedGroupCount -eq 1) 'synthetic metadata mismatch.'
    Assert-Test ($read.ActiveComputerPresent -eq $true) 'synthetic active computer was not preserved.'
    Assert-Test ($read.Json -ceq $payloadJson) 'payload changed during DPAPI write/read.'

    $beforeHash = (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash
    $overwriteRejected = $false
    try {
        [void](Write-ProtectedBackup -Path $backupPath -PayloadJson $payloadJson)
    } catch {
        $overwriteRejected = $true
    }
    Assert-Test $overwriteRejected 'existing backup path was not rejected.'
    $afterHash = (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash
    Assert-Test ($beforeHash -ceq $afterHash) 'rejected overwrite changed the existing backup hash.'

    $originalBytes = [System.IO.File]::ReadAllBytes($backupPath)
    try {
        [System.IO.File]::WriteAllBytes($corruptPath, $originalBytes)
    } finally {
        [Array]::Clear($originalBytes, 0, $originalBytes.Length)
    }
    Set-CurrentUserOnlyAcl -Path $corruptPath
    $corruptBytes = [System.IO.File]::ReadAllBytes($corruptPath)
    try {
        $corruptBytes[$corruptBytes.Length - 1] = $corruptBytes[$corruptBytes.Length - 1] -bxor 0x01
        [System.IO.File]::WriteAllBytes($corruptPath, $corruptBytes)
    } finally {
        [Array]::Clear($corruptBytes, 0, $corruptBytes.Length)
    }
    $corruptRejected = $false
    try {
        [void](Read-ProtectedBackup -Path $corruptPath)
    } catch {
        $corruptRejected = $true
    }
    Assert-Test $corruptRejected 'corrupted DPAPI backup was accepted.'

    Invoke-PhysicalImportGuardTest -MainScript $mainScript

    $result = [pscustomobject]@{
        PowerShellSyntax = $true
        FunctionExtraction = $true
        DpapiWriteRead = $true
        AclCurrentUserOnly = $true
        ExistingBackupRejected = $overwriteRejected
        ExistingBackupHashUnchanged = ($beforeHash -ceq $afterHash)
        CorruptBackupRejected = $corruptRejected
        PhysicalImportGuard = $true
        AdbInvocations = 0
    }
} finally {
    # Only remove the exact freshly generated directory after validating that
    # it is under the OS temp root and has this test's unique name.
    $systemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $systemTempPrefix = $systemTemp.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $leaf = [System.IO.Path]::GetFileName($tempRootFull)
    $ownedName = $leaf -match '^phonedeck-pairing-migration-test-[0-9a-f]{32}$'
    if ($tempRootCreated -and $ownedName -and
        $tempRootFull.StartsWith($systemTempPrefix, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $tempRootFull -PathType Container)) {
        [System.IO.Directory]::Delete($tempRootFull, $true)
        if (Test-Path -LiteralPath $tempRootFull -PathType Container) {
            throw 'Pairing migration synthetic test cleanup left a temporary directory.'
        }
    } elseif ($tempRootCreated) {
        throw 'Pairing migration synthetic test refused to delete an unvalidated temporary path.'
    }
}

$result | ConvertTo-Json -Compress
