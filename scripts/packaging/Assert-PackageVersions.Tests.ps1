#requires -Version 7.0
param(
    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'

$isWinOs = $IsWindows -or ($PSVersionTable.PSEdition -ne 'Core' -and [System.Environment]::OSVersion.Platform -eq 'Win32NT')
if (-not $isWinOs) {
    throw "Unsupported host: Tests require Windows OS."
}

if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $DotnetPath = Get-Command "dotnet" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
}
if (-not $DotnetPath) { throw "DotnetPath must be specified or dotnet must be in PATH" }

$scriptDir = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $scriptDir "..\..")).Path
$tempDir = Join-Path $env:TEMP "PhoneDeck_PkgTest_$(New-Guid)"
New-Item -Type Directory -Path $tempDir | Out-Null

Write-Host "Creating fixtures in $tempDir"

$descriptorPath = Join-Path $repoRoot "work\phone-deck\release-versions.json"
$descriptor = Get-Content -Raw -LiteralPath $descriptorPath | ConvertFrom-Json
$baseVer = $descriptor.windows.version
$seq = $descriptor.windows.sequence
$prefix = if ($baseVer -match "^(\d+\.\d+\.\d+)") { $matches[1] } else { "1.0.0" }
$fileVer = "$prefix.$seq"
$androidCode = $descriptor.android.versionCode
$androidName = $descriptor.android.versionName

function New-DummyExe($Path, $OriginalName, $FileVersion, $InfoVersion) {
    $projDir = Join-Path $tempDir (New-Guid).ToString()
    & $DotnetPath new console -n $OriginalName -o $projDir | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet new failed" }
    $outDir = Join-Path $projDir "out"
    & $DotnetPath publish $projDir -o $outDir -p:Version=$FileVersion -p:InformationalVersion=$InfoVersion | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

    $builtExe = Join-Path $outDir "$OriginalName.exe"
    if (-not (Test-Path -LiteralPath $builtExe)) {
        $builtExe = Join-Path $outDir "$OriginalName.dll"
    }
    Copy-Item -Path $builtExe -Destination $Path
}

$validServer = Join-Path $tempDir "PhoneDeck.Server.exe"
$validControlCenter = Join-Path $tempDir "PhoneDeck.ControlCenter.exe"
New-DummyExe -Path $validServer -OriginalName "PhoneDeck.Server" -FileVersion $fileVer -InfoVersion "${baseVer}+abc"
New-DummyExe -Path $validControlCenter -OriginalName "PhoneDeck.ControlCenter" -FileVersion $fileVer -InfoVersion $baseVer

$validApk = Join-Path $tempDir "PhoneDeck.apk"
Set-Content -Path $validApk -Value "dummy"

function New-Aapt2Mock($Path, $Content, $ExitCode = 0) {
    $contentBat = "@echo off`n" + $Content + "`nexit /b $ExitCode"
    Set-Content -Path $Path -Value $contentBat
}

$mockAapt2Valid = Join-Path $tempDir "aapt2_valid.bat"
New-Aapt2Mock -Path $mockAapt2Valid -Content "echo package: name='com.codex.phonedeck' versionCode='$androidCode' versionName='$androidName'"

$mockAapt2BadName = Join-Path $tempDir "aapt2_badname.bat"
New-Aapt2Mock -Path $mockAapt2BadName -Content "echo package: name='com.other.app' versionCode='$androidCode' versionName='$androidName'"

$mockAapt2BadCode = Join-Path $tempDir "aapt2_badcode.bat"
New-Aapt2Mock -Path $mockAapt2BadCode -Content "echo package: name='com.codex.phonedeck' versionCode='999' versionName='$androidName'"

$mockAapt2BadVersionName = Join-Path $tempDir "aapt2_badversionname.bat"
New-Aapt2Mock -Path $mockAapt2BadVersionName -Content "echo package: name='com.codex.phonedeck' versionCode='$androidCode' versionName='9.9.9'"

$mockAapt2DuplicateRecord = Join-Path $tempDir "aapt2_duplicaterecord.bat"
New-Aapt2Mock -Path $mockAapt2DuplicateRecord -Content "echo package: name='com.codex.phonedeck' versionCode='$androidCode' versionName='$androidName'`necho package: name='com.codex.phonedeck' versionCode='$androidCode' versionName='$androidName'"

$mockAapt2Malformed = Join-Path $tempDir "aapt2_malformed.bat"
New-Aapt2Mock -Path $mockAapt2Malformed -Content "echo package: name='com.codex.phonedeck'`necho versionCode='$androidCode'`necho versionName='$androidName'"

$mockAapt2NonZero = Join-Path $tempDir "aapt2_nonzero.bat"
New-Aapt2Mock -Path $mockAapt2NonZero -Content "echo error" -ExitCode 1

$assertScript = Join-Path $scriptDir "Assert-PackageVersions.ps1"
$buildPkgScript = Join-Path $repoRoot "work\phone-deck\build-update-package.ps1"

$testsRun = 0
$testsPassed = 0

function Run-Test($Name, $ScriptBlock) {
    $script:testsRun++
    Write-Host "Running: $Name"
    try {
        & $ScriptBlock
        Write-Host "  PASS" -ForegroundColor Green
        $script:testsPassed++
    } catch {
        Write-Host "  FAIL: $($_.Exception.Message)" -ForegroundColor Red
    }
}

function Assert-Throws($ScriptBlock, $ExpectedMessage) {
    $threw = $false
    try {
        & $ScriptBlock
    } catch {
        $threw = $true
        if ($_.Exception.Message -notmatch $ExpectedMessage) {
            throw "Expected exception matching '$ExpectedMessage', but got: $($_.Exception.Message)"
        }
    }
    if (-not $threw) {
        throw "Expected exception but none was thrown."
    }
}

Run-Test "Correct files pass" {
    & $assertScript -Server $validServer -ControlCenter $validControlCenter -Apk $validApk -DescriptorPath $descriptorPath -Aapt2Path $mockAapt2Valid
}

Run-Test "Swapped EXEs throw" {
    Assert-Throws {
        & $assertScript -Server $validControlCenter -ControlCenter $validServer -Apk $validApk -DescriptorPath $descriptorPath -Aapt2Path $mockAapt2Valid
    } "Invalid OriginalFilename"
}

Run-Test "Wrong FileVersion throws" {
    $wrongFileExe = Join-Path $tempDir "PhoneDeck.Server_WrongFile.exe"
    New-DummyExe -Path $wrongFileExe -OriginalName "PhoneDeck.Server" -FileVersion "1.0.0.999" -InfoVersion $baseVer
    Assert-Throws {
        & $assertScript -Server $wrongFileExe -ControlCenter $validControlCenter -Apk $validApk -DescriptorPath $descriptorPath -Aapt2Path $mockAapt2Valid
    } "FileVersion mismatch"
}

Run-Test "Wrong ProductVersion throws" {
    $wrongProdExe = Join-Path $tempDir "PhoneDeck.Server_WrongProd.exe"
    New-DummyExe -Path $wrongProdExe -OriginalName "PhoneDeck.Server" -FileVersion $fileVer -InfoVersion "9.9.9"
    Assert-Throws {
        & $assertScript -Server $wrongProdExe -ControlCenter $validControlCenter -Apk $validApk -DescriptorPath $descriptorPath -Aapt2Path $mockAapt2Valid
    } "ProductVersion mismatch"
}

Run-Test "Wrong APK ID throws" {
    Assert-Throws {
        & $assertScript -Server $validServer -ControlCenter $validControlCenter -Apk $validApk -DescriptorPath $descriptorPath -Aapt2Path $mockAapt2BadName
    } "APK package record mismatch"
}

Run-Test "Wrong APK Code throws" {
    Assert-Throws {
        & $assertScript -Server $validServer -ControlCenter $validControlCenter -Apk $validApk -DescriptorPath $descriptorPath -Aapt2Path $mockAapt2BadCode
    } "APK package record mismatch"
}

Run-Test "Wrong APK VersionName throws" {
    Assert-Throws {
        & $assertScript -Server $validServer -ControlCenter $validControlCenter -Apk $validApk -DescriptorPath $descriptorPath -Aapt2Path $mockAapt2BadVersionName
    } "APK package record mismatch"
}

Run-Test "Duplicate package record throws" {
    Assert-Throws {
        & $assertScript -Server $validServer -ControlCenter $validControlCenter -Apk $validApk -DescriptorPath $descriptorPath -Aapt2Path $mockAapt2DuplicateRecord
    } "missing, malformed, or duplicate"
}

Run-Test "Non-zero aapt2 exit throws" {
    Assert-Throws {
        & $assertScript -Server $validServer -ControlCenter $validControlCenter -Apk $validApk -DescriptorPath $descriptorPath -Aapt2Path $mockAapt2NonZero
    } "failed with exit code"
}

Run-Test "Malformed aapt2 throws" {
    Assert-Throws {
        & $assertScript -Server $validServer -ControlCenter $validControlCenter -Apk $validApk -DescriptorPath $descriptorPath -Aapt2Path $mockAapt2Malformed
    } "missing, malformed, or duplicate"
}

Run-Test "Missing aapt2 tool throws" {
    $missingAapt2 = Join-Path $tempDir "does_not_exist.bat"
    Assert-Throws {
        & $assertScript -Server $validServer -ControlCenter $validControlCenter -Apk $validApk -DescriptorPath $descriptorPath -Aapt2Path $missingAapt2
    } "aapt2 not found"
}

Run-Test "Auto-detection SDK environment works" {
    $fakeSdk = Join-Path $tempDir "fake_sdk"
    $buildTools = Join-Path $fakeSdk "build-tools\30.0.0"
    New-Item -Type Directory -Path $buildTools -Force | Out-Null

    $isWinOs = $IsWindows -or ($PSVersionTable.PSEdition -ne 'Core' -and [System.Environment]::OSVersion.Platform -eq 'Win32NT')
    $aapt2Name = if ($isWinOs) { "aapt2.exe" } else { "aapt2" }
    $fakeAapt2 = Join-Path $buildTools $aapt2Name

    $cs = @"
using System;
class Program {
    static int Main() {
        Console.WriteLine("package: name='com.codex.phonedeck' versionCode='$androidCode' versionName='$androidName'");
        return 0;
    }
}
"@
    $projDir = Join-Path $tempDir (New-Guid).ToString()
    & $DotnetPath new console -n MockAapt2 -o $projDir | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet new failed with exit code $LASTEXITCODE" }
    Set-Content -Path (Join-Path $projDir "Program.cs") -Value $cs
    $outDir = Join-Path $projDir "out"
    & $DotnetPath publish $projDir -o $outDir | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    Copy-Item -Path "$outDir\*" -Destination $buildTools -Recurse -Force
    $copiedExe = Join-Path $buildTools "MockAapt2.exe"
    if (-not (Test-Path -LiteralPath $copiedExe)) { $copiedExe = Join-Path $buildTools "MockAapt2" }
    Rename-Item -Path $copiedExe -NewName $aapt2Name

    $oldHome = $env:ANDROID_HOME
    $env:ANDROID_HOME = $fakeSdk
    try {
        & $assertScript -Server $validServer -ControlCenter $validControlCenter -Apk $validApk -DescriptorPath $descriptorPath
    } finally {
        $env:ANDROID_HOME = $oldHome
    }
}

Run-Test "Mismatch package CLI failing BEFORE fake missing-key path access" {
    $fakeKey = Join-Path $tempDir "missing_key.der"
    $outPath = Join-Path $tempDir "out.zip"

    $output = & pwsh -NoProfile -NonInteractive -File $buildPkgScript -Server $validServer -ControlCenter $validControlCenter -Apk $validApk -Sequence 9999 -WindowsVersion $baseVer -AndroidVersionCode $androidCode -SigningKey $fakeKey -Output $outPath 2>&1

    if ($LASTEXITCODE -eq 0) { throw "Expected failure but process exited with 0" }

    $outputText = $output -join "`n"
    if ($outputText -notmatch "CLI values do not match descriptor base") {
        throw "Expected real message 'CLI values do not match descriptor base', got: $outputText"
    }
    if ($outputText -match "missing_key.der") {
        throw "Key was read, but should have failed before path access"
    }
    if (Test-Path -LiteralPath $outPath) {
        throw "Output was created, but should have failed before output creation"
    }
}

Write-Host "Tests complete. $testsPassed / $testsRun passed."
if ($testsPassed -ne $testsRun) {
    exit 1
}
