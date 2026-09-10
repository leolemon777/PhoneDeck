<#
.SYNOPSIS
    PhoneDeck Build Pipeline Regression Tests
.DESCRIPTION
    Tests for portable isolated PowerShell 7 control flow regression.
#>
$ErrorActionPreference = 'Stop'
$global:FailedTests = 0
$global:PassedTests = 0
$global:SkippedTests = 0

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "This test requires PowerShell 7+ (pwsh). Current version: $($PSVersionTable.PSVersion)"
}
if (-not $IsWindows) {
    Write-Host "Skip: This test requires Windows." -ForegroundColor Yellow
    exit 0
}

function Describe ($Name, $ScriptBlock) { Write-Host "Describe: $Name" -ForegroundColor Cyan; & $ScriptBlock }
function Skip-Test ($Message) { throw [System.Management.Automation.RuntimeException]::new("SKIP_TEST: $Message") }
function It ($Name, $ScriptBlock) {
    try {
        & $ScriptBlock
        Write-Host "  [+] $Name" -ForegroundColor Green
        $global:PassedTests++
    } catch {
        if ($_.Exception.Message -match "^SKIP_TEST: (.*)") {
            Write-Host "  [?] $Name (Skipped: $($matches[1]))" -ForegroundColor Yellow
            $global:SkippedTests++
        } else {
            Write-Host "  [-] $Name" -ForegroundColor Red
            Write-Host "      $($_.Exception.Message)" -ForegroundColor Red
            $global:FailedTests++
        }
    }
}
function Assert-True ($Condition, $Message = "") { if (-not $Condition) { throw "Assertion Failed: $Message" } }
function Assert-Equal ($Actual, $Expected, $Message = "") { if ($Actual -ne $Expected) { throw "Assertion Failed: $Message. Expected '$Expected', got '$Actual'" } }

function Setup-Fixture {
    $FixtureRoot = Join-Path $env:TEMP "PhoneDeck Fixture 中文 空格 $([guid]::NewGuid())"
    Write-Host "    [Fixture] $FixtureRoot" -ForegroundColor DarkGray
    New-Item -ItemType Directory -Path $FixtureRoot -Force | Out-Null
    $ScriptsDir = Join-Path $FixtureRoot "scripts"
    New-Item -ItemType Directory -Path $ScriptsDir -Force | Out-Null

    $RepoRoot = Resolve-Path "$PSScriptRoot\..\.." | Select-Object -ExpandProperty Path
    if (Test-Path (Join-Path $RepoRoot "build.ps1")) { Copy-Item -Path (Join-Path $RepoRoot "build.ps1") -Destination (Join-Path $FixtureRoot "build.ps1") -Force }
    if (Test-Path (Join-Path $RepoRoot "scripts\build.ps1")) { Copy-Item -Path (Join-Path $RepoRoot "scripts\build.ps1") -Destination (Join-Path $ScriptsDir "build.ps1") -Force }

    $workWindowsPath = Join-Path $FixtureRoot "work\phone-deck\windows"
    New-Item -ItemType Directory -Path (Join-Path $workWindowsPath "PhoneDeck.Server") -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $workWindowsPath "PhoneDeck.Server\PhoneDeck.Server.csproj") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $workWindowsPath "PhoneDeck.Server.Tests") -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $workWindowsPath "PhoneDeck.Server.Tests\PhoneDeck.Server.Tests.csproj") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $workWindowsPath "PhoneDeck.ControlCenter") -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $workWindowsPath "PhoneDeck.ControlCenter\PhoneDeck.ControlCenter.csproj") -Force | Out-Null

    $MockBin = Join-Path $FixtureRoot "mock_bin"
    New-Item -ItemType Directory -Path $MockBin -Force | Out-Null

    $MockDotnetPs1 = Join-Path $MockBin "dotnet-mock.ps1"
    Set-Content -Path $MockDotnetPs1 -Value @'
param()
$ArgsList = $args
$LogPath = $env:MOCK_DOTNET_LOG
$LogEntry = @{ Args = $ArgsList } | ConvertTo-Json -Compress
if ($LogPath) { Add-Content -Path $LogPath -Value $LogEntry }

if ($ArgsList -contains '--version') { Write-Output '8.0.100'; exit 0 }
if ($ArgsList -contains '--info') { Write-Output 'mock info'; exit 0 }
if ($ArgsList -contains '--list-sdks') { Write-Output '8.0.100 [C:\Program Files\dotnet\sdk]'; exit 0 }
if ($ArgsList -contains '--list-runtimes') { Write-Output 'Microsoft.NETCore.App 8.0.0 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]'; exit 0 }
if ($env:MOCK_DOTNET_FAIL_PHASE -and ($ArgsList -contains $env:MOCK_DOTNET_FAIL_PHASE)) { exit [int]$env:MOCK_DOTNET_FAIL_CODE }

if ($ArgsList -contains 'publish') {
    $outPath = 'bin/publish'
    $csprojName = 'PhoneDeck.Unknown.exe'
    for ($i=0; $i -lt $ArgsList.Count; $i++) {
        if ($ArgsList[$i] -eq '-o' -or $ArgsList[$i] -eq '--output') { $outPath = $ArgsList[$i+1] }
        if ($ArgsList[$i] -match '([^\\/]+)\.csproj$') {
            $baseName = $matches[1]
            if (-not ($baseName -match '^PhoneDeck\.')) { $baseName = "PhoneDeck.$baseName" }
            $csprojName = $baseName + '.exe'
        }
    }
    if (-not (Test-Path $outPath)) { New-Item -ItemType Directory -Path $outPath -Force | Out-Null }
    Set-Content -Path (Join-Path $outPath $csprojName) -Value 'MZ...'
}
if ($ArgsList -contains 'test') {
    $trxDir = 'TestResults'
    for ($i=0; $i -lt $ArgsList.Count; $i++) {
        if ($ArgsList[$i] -eq '--results-directory') { $trxDir = $ArgsList[$i+1] }
    }
    if (-not (Test-Path $trxDir)) { New-Item -ItemType Directory -Path $trxDir -Force | Out-Null }
    Set-Content -Path (Join-Path $trxDir 'mock.trx') -Value '<TestRun></TestRun>'
}
exit 0
'@

    $PwshPath = (Get-Command pwsh.exe -ErrorAction SilentlyContinue).Source
    if (-not $PwshPath) { $PwshPath = "pwsh.exe" }
    $MockDotnet = Join-Path $MockBin "dotnet.cmd"
    $cmdContent = "@echo off`r`n`"$PwshPath`" -NoProfile -ExecutionPolicy Bypass -File `"%~dp0dotnet-mock.ps1`" %*"
    [System.IO.File]::WriteAllText($MockDotnet, $cmdContent, [System.Text.Encoding]::ASCII)

    $MockGradle = Join-Path $MockBin "gradle.bat"
    [System.IO.File]::WriteAllText($MockGradle, "@echo off`r`nexit 0", [System.Text.Encoding]::ASCII)
    return $FixtureRoot, $MockBin
}

function Invoke-Wrapper {
    param([string]$FixtureRoot, [string[]]$ArgsList, [hashtable]$EnvVars, [string]$Cwd)
    $Psi = [System.Diagnostics.ProcessStartInfo]::new()
    $Psi.FileName = (Get-Command pwsh.exe -ErrorAction SilentlyContinue).Source
    if (-not $Psi.FileName) { $Psi.FileName = "pwsh.exe" }

    $Psi.ArgumentList.Add("-NoProfile")
    $Psi.ArgumentList.Add("-ExecutionPolicy")
    $Psi.ArgumentList.Add("Bypass")
    $Psi.ArgumentList.Add("-File")
    $Psi.ArgumentList.Add((Join-Path $FixtureRoot "build.ps1"))

    $Psi.ArgumentList.Add("-Platform")
    $Psi.ArgumentList.Add("Windows")

    if ($ArgsList) { foreach ($arg in $ArgsList) { $Psi.ArgumentList.Add($arg) } }
    $Psi.UseShellExecute = $false
    $Psi.RedirectStandardOutput = $true
    $Psi.RedirectStandardError = $true
    $Psi.WorkingDirectory = if ($Cwd) { $Cwd } else { $FixtureRoot }
    if ($EnvVars) { foreach ($key in $EnvVars.Keys) { $Psi.EnvironmentVariables[$key] = $EnvVars[$key] } }

    $Process = [System.Diagnostics.Process]::Start($Psi)
    $OutputTask = $Process.StandardOutput.ReadToEndAsync()
    $ErrorTask = $Process.StandardError.ReadToEndAsync()

    if (-not $Process.WaitForExit(30000)) {
        $Process.Kill($true)
        $Process.Dispose()
        throw "Process timed out after 30 seconds"
    }

    $ExitCode = $Process.ExitCode
    $Process.Dispose()
    return @{ ExitCode = $ExitCode; Output = $OutputTask.Result; Error = $ErrorTask.Result }
}

Describe "PhoneDeck Build Pipeline Regression Tests" {
    It "Windows成功exit0+两EXE+manifest success+test有调用" {
        $FixtureRoot, $MockBin = Setup-Fixture
        $LogPath = Join-Path $FixtureRoot "dotnet.log"
        $OutputDir = Join-Path $FixtureRoot "outputs\build-review"
        $Result = Invoke-Wrapper -FixtureRoot $FixtureRoot -ArgsList @("-OutputDir", $OutputDir) -EnvVars @{ "MOCK_DOTNET_LOG" = $LogPath; "PATH" = "$MockBin;$($env:PATH)" }

        if ($Result.ExitCode -ne 0) {
            Set-Content -Path (Join-Path $FixtureRoot "result.log") -Value "Stdout:`n$($Result.Output)`nStderr:`n$($Result.Error)"
        }
        Assert-Equal $Result.ExitCode 0 "ExitCode should be 0. Output: $($Result.Output). Error: $($Result.Error)"

        $ManifestPath = Join-Path $OutputDir "latest.json"
        Assert-True (Test-Path $ManifestPath) "Manifest must exist"
        $Manifest = Get-Content $ManifestPath | ConvertFrom-Json

        Assert-Equal $Manifest.schemaVersion 1
        Assert-Equal $Manifest.status "success"
        Assert-True ($Manifest.runId -ne $null)

        Assert-True (Test-Path $LogPath) "Log must exist"
        $LogLines = Get-Content $LogPath
        $TestCount = ($LogLines | Where-Object { $_ -match '"test"' }).Count
        Assert-True ($TestCount -gt 0) "test called"

        $RunDir = Join-Path $FixtureRoot $Manifest.runDirectory
        Assert-True (Test-Path (Join-Path $RunDir "windows\server\PhoneDeck.Server.exe")) "Server.exe must exist"
        Assert-True (Test-Path (Join-Path $RunDir "windows\console\PhoneDeck.ControlCenter.exe")) "ControlCenter.exe must exist"

        Assert-True ($Manifest.artifacts.path -contains "windows/server/PhoneDeck.Server.exe" -or $Manifest.artifacts.path -contains "windows\server\PhoneDeck.Server.exe") "Manifest artifacts must contain Server.exe"
        Assert-True ($Manifest.artifacts.path -contains "windows/console/PhoneDeck.ControlCenter.exe" -or $Manifest.artifacts.path -contains "windows\console\PhoneDeck.ControlCenter.exe") "Manifest artifacts must contain ControlCenter.exe"
    }

    It "指定dotnet build失败exit非零且报告phase含build、exitCode23、后续publish未调用" {
        $FixtureRoot, $MockBin = Setup-Fixture
        $LogPath = Join-Path $FixtureRoot "dotnet.log"
        $OutputDir = Join-Path $FixtureRoot "outputs\build-review"
        $Result = Invoke-Wrapper -FixtureRoot $FixtureRoot -ArgsList @("-OutputDir", $OutputDir) -EnvVars @{ "MOCK_DOTNET_LOG" = $LogPath; "PATH" = "$MockBin;$($env:PATH)"; "MOCK_DOTNET_FAIL_PHASE" = "build"; "MOCK_DOTNET_FAIL_CODE" = "23" }

        if ($Result.ExitCode -ne 23) {
            Set-Content -Path (Join-Path $FixtureRoot "result.log") -Value "Stdout:`n$($Result.Output)`nStderr:`n$($Result.Error)"
        }
        Assert-Equal $Result.ExitCode 23 "ExitCode should be 23. Output: $($Result.Output). Error: $($Result.Error)"

        $ManifestPath = Join-Path $OutputDir "latest.json"
        Assert-True (Test-Path $ManifestPath) "Manifest must exist"
        $Manifest = Get-Content $ManifestPath | ConvertFrom-Json

        Assert-True ($Manifest.phase -match "build") "Phase should contain build"
        Assert-Equal $Manifest.exitCode 23 "Manifest exitCode should be 23"

        Assert-True (Test-Path $LogPath) "Log must exist for failed build"
        $LogLines = Get-Content $LogPath
        $PublishCount = ($LogLines | Where-Object { $_ -match '"publish"' }).Count
        Assert-Equal $PublishCount 0 "publish should not be called"
    }

    It "同OutputDir先成功后失败runId不同旧run保留latest失败" {
        $FixtureRoot, $MockBin = Setup-Fixture
        $LogPath = Join-Path $FixtureRoot "dotnet.log"
        $OutputDir = Join-Path $FixtureRoot "outputs\build-review"

        $Result1 = Invoke-Wrapper -FixtureRoot $FixtureRoot -ArgsList @("-OutputDir", $OutputDir) -EnvVars @{ "MOCK_DOTNET_LOG" = $LogPath; "PATH" = "$MockBin;$($env:PATH)" }
        Assert-Equal $Result1.ExitCode 0 "First run should exit 0. Error: $($Result1.Error)"
        $ManifestPath = Join-Path $OutputDir "latest.json"
        Assert-True (Test-Path $ManifestPath) "First run manifest must exist"
        $Manifest1 = Get-Content $ManifestPath | ConvertFrom-Json

        $Result2 = Invoke-Wrapper -FixtureRoot $FixtureRoot -ArgsList @("-OutputDir", $OutputDir) -EnvVars @{ "MOCK_DOTNET_LOG" = $LogPath; "PATH" = "$MockBin;$($env:PATH)"; "MOCK_DOTNET_FAIL_PHASE" = "build"; "MOCK_DOTNET_FAIL_CODE" = "1" }
        Assert-True ($Result2.ExitCode -ne 0) "Second run should fail. Expected failure."
        Assert-True (Test-Path $ManifestPath) "Second run manifest must exist"
        $Manifest2 = Get-Content $ManifestPath | ConvertFrom-Json

        Assert-True ($Manifest1.runId -ne $Manifest2.runId) "runId should be different"
        Assert-Equal $Manifest2.status "failed" "Status should be failed"
        Assert-True (Test-Path (Join-Path $FixtureRoot $Manifest1.runDirectory)) "Old run directory must be kept"
    }

    It "SkipTests无test调用且unverified" {
        $FixtureRoot, $MockBin = Setup-Fixture
        $LogPath = Join-Path $FixtureRoot "dotnet.log"
        $OutputDir = Join-Path $FixtureRoot "outputs\build-review"
        $Result = Invoke-Wrapper -FixtureRoot $FixtureRoot -ArgsList @("-OutputDir", $OutputDir, "-SkipTests") -EnvVars @{ "MOCK_DOTNET_LOG" = $LogPath; "PATH" = "$MockBin;$($env:PATH)" }

        if ($Result.ExitCode -ne 0) {
            Set-Content -Path (Join-Path $FixtureRoot "result.log") -Value "Stdout:`n$($Result.Output)`nStderr:`n$($Result.Error)"
        }
        Assert-Equal $Result.ExitCode 0 "ExitCode should be 0. Error: $($Result.Error)"

        $ManifestPath = Join-Path $OutputDir "latest.json"
        Assert-True (Test-Path $ManifestPath) "Manifest must exist"
        $Manifest = Get-Content $ManifestPath | ConvertFrom-Json

        Assert-Equal $Manifest.status "unverified" "Status should be unverified"

        Assert-True (Test-Path $LogPath) "Log must exist for SkipTests"
        $LogLines = Get-Content $LogPath
        $TestCount = ($LogLines | Where-Object { $_ -match '"test"' }).Count
        Assert-Equal $TestCount 0 "test should not be called"
    }

    It "outputs-evil/outputs本身/..前置拒绝原因匹配且哨兵未变" {
        $FixtureRoot, $MockBin = Setup-Fixture
        $Sentinel = Join-Path $FixtureRoot "sentinel.txt"
        Set-Content -Path $Sentinel -Value "unchanged"
        $Env = @{ "PATH" = "$MockBin;$($env:PATH)" }

        foreach ($dir in @("outputs", "outputs-evil", "..")) {
            $Result = Invoke-Wrapper -FixtureRoot $FixtureRoot -ArgsList @("-OutputDir", $dir) -EnvVars $Env
            if ($Result.ExitCode -eq 0) {
                Set-Content -Path (Join-Path $FixtureRoot "result.log") -Value "Stdout:`n$($Result.Output)`nStderr:`n$($Result.Error)"
            }
            Assert-True ($Result.ExitCode -ne 0) "Should fail for $dir"
            Assert-True (($Result.Error -match "拒绝|安全|越界|安全限制|reject|security|invalid") -or ($Result.Output -match "拒绝|安全|越界|安全限制|reject|security|invalid")) "Should match reject reason. Output: $($Result.Output) Error: $($Result.Error)"
            Assert-Equal (Get-Content $Sentinel) "unchanged" "Sentinel should be unchanged"
        }
    }

    It "仅fixture内的祖先junction导致拒绝（无创建权限明确skip）" {
        $FixtureRoot, $MockBin = Setup-Fixture
        $OutputsDir = Join-Path $FixtureRoot "outputs"
        $JunctionTarget = Join-Path $FixtureRoot "target"
        $JunctionLink = Join-Path $OutputsDir "link"

        $Sentinel = Join-Path $JunctionTarget "sentinel.txt"
        New-Item -ItemType Directory -Path $JunctionTarget -Force | Out-Null
        Set-Content -Path $Sentinel -Value "unchanged"

        New-Item -ItemType Directory -Path $OutputsDir -Force | Out-Null

        try {
            New-Item -ItemType Junction -Path $JunctionLink -Target $JunctionTarget -ErrorAction Stop | Out-Null
            $HasPerm = ((Get-Item $JunctionLink).LinkType -eq 'Junction')
        } catch {
            $HasPerm = $false
        }

        if (-not $HasPerm) {
            Skip-Test "Cannot create Junction, missing permissions"
        }

        $Result = Invoke-Wrapper -FixtureRoot $FixtureRoot -ArgsList @("-OutputDir", "$JunctionLink\child") -EnvVars @{ "PATH" = "$MockBin;$($env:PATH)" }
        if ($Result.ExitCode -eq 0) {
            Set-Content -Path (Join-Path $FixtureRoot "result.log") -Value "Stdout:`n$($Result.Output)`nStderr:`n$($Result.Error)"
        }
        Assert-True ($Result.ExitCode -ne 0) "Should reject junction"
        Assert-True (($Result.Error -match "拒绝|安全|越界|安全限制|链接|符号|软链接|reject|security|invalid|junction|link") -or ($Result.Output -match "拒绝|安全|越界|安全限制|链接|符号|软链接|reject|security|invalid|junction|link")) "Should match junction reject reason"
        Assert-Equal (Get-Content $Sentinel) "unchanged" "Sentinel should be unchanged"
    }

    It "任意cwd/中文空格wrapper可调用" {
        $FixtureRoot, $MockBin = Setup-Fixture
        $LogPath = Join-Path $FixtureRoot "dotnet.log"
        $OutputDir = Join-Path $FixtureRoot "outputs\build-review"
        $RandomCwd = Join-Path $env:TEMP "RandomCwd_$([guid]::NewGuid())"
        New-Item -ItemType Directory -Path $RandomCwd -Force | Out-Null

        $Result = Invoke-Wrapper -FixtureRoot $FixtureRoot -ArgsList @("-OutputDir", $OutputDir) -EnvVars @{ "MOCK_DOTNET_LOG" = $LogPath; "PATH" = "$MockBin;$($env:PATH)" } -Cwd $RandomCwd
        if ($Result.ExitCode -ne 0) {
            Set-Content -Path (Join-Path $FixtureRoot "result.log") -Value "Stdout:`n$($Result.Output)`nStderr:`n$($Result.Error)"
        }
        Assert-Equal $Result.ExitCode 0 "ExitCode should be 0. Error: $($Result.Error)"

        $ManifestPath = Join-Path $OutputDir "latest.json"
        Assert-True (Test-Path $ManifestPath) "Manifest must exist"
    }
}

Write-Host "`nTest Summary:" -ForegroundColor Cyan
Write-Host "Passed:  $global:PassedTests" -ForegroundColor Green
Write-Host "Failed:  $global:FailedTests" -ForegroundColor Red
Write-Host "Skipped: $global:SkippedTests" -ForegroundColor Yellow

if ($global:FailedTests -gt 0) { exit 1 } else { exit 0 }
