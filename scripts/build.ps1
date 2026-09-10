<#
.SYNOPSIS
    PhoneDeck 统一开发构建入口 (B01)
.DESCRIPTION
    执行 Windows / Android / macOS 跨平台构建、测试、单文件打包与依赖检查。
    支持路径可移植、失败即停、单文件独立性校验与测试报告归档。
.PARAMETER Platform
    构建平台：'All'（默认，全量构建）、'Windows'、'Android'、'MacOS'。
.PARAMETER Configuration
    构建配置：'Release'（默认）或 'Debug'。
.PARAMETER OutputDir
    产物输出根目录，默认为 'outputs/build-review'。
.PARAMETER Clean
    若指定，先清理输出目录及历史构建中间产物。
.PARAMETER SkipTests
    若指定，跳过单元测试（开发调试用，默认包含测试）。
#>
[CmdletBinding()]
param(
    [ValidateSet('All', 'Windows', 'Android', 'MacOS')]
    [string]$Platform = 'All',

    [string]$Configuration = 'Release',

    [string]$OutputDir = 'outputs/build-review',

    [switch]$Clean,

    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# 确定仓库根目录（路径完全可移植，不依赖任何绝对路径）
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path (Join-Path $ScriptDir "..")).Path
$OutFull = if ([System.IO.Path]::IsPathRooted($OutputDir)) {
    [System.IO.Path]::GetFullPath($OutputDir)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $OutputDir))
}

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "PhoneDeck 统一开发构建入口 (B01)" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "仓库根目录: $RepoRoot"
Write-Host "目标平台:   $Platform"
Write-Host "构建配置:   $Configuration"
Write-Host "输出目录:   $OutFull"
Write-Host "运行环境:   $([System.Environment]::OSVersion.VersionString)"
Write-Host "=================================================="

function Assert-StepSuccess {
    param([string]$StepName)
    if ($LASTEXITCODE -ne 0) {
        Write-Error "步骤 [$StepName] 失败，退出码: $LASTEXITCODE。构建已终止。"
        exit $LASTEXITCODE
    }
}

function Ensure-JavaHome {
    if (-not [string]::IsNullOrWhiteSpace($env:JAVA_HOME) -and (Test-Path -LiteralPath $env:JAVA_HOME)) {
        return $env:JAVA_HOME
    }

    # 尝试从 PATH 中探测 java
    $javaCmd = Get-Command java -ErrorAction SilentlyContinue
    if ($javaCmd) {
        $javaBin = Split-Path -Parent $javaCmd.Source
        $candidateJavaHome = Split-Path -Parent $javaBin
        if (Test-Path -LiteralPath (Join-Path $candidateJavaHome "bin/javac.exe")) {
            $env:JAVA_HOME = $candidateJavaHome
            return $candidateJavaHome
        }
    }

    # 探测常见 JDK 安装位置
    $probePaths = @(
        "E:\Android\Jdk17\jdk-17.0.20.1+1",
        "E:\PhoneDeck-build\jdk-21.0.12.1+1",
        "C:\Program Files\Java\jdk-17",
        "C:\Program Files\Eclipse Adoptium\jdk-17*",
        "C:\Program Files\Microsoft\jdk-17*"
    )
    foreach ($probe in $probePaths) {
        $resolved = Get-Item -Path $probe -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($resolved -and (Test-Path (Join-Path $resolved.FullName "bin/javac.exe"))) {
            $env:JAVA_HOME = $resolved.FullName
            Write-Host "自动探测并使用 JDK: $($env:JAVA_HOME)" -ForegroundColor Yellow
            return $env:JAVA_HOME
        }
    }

    throw "未找到有效的 JAVA_HOME，请先配置 JAVA_HOME 环境变量指向 JDK 17。"
}

function Ensure-AndroidSdk {
    $localProps = Join-Path $RepoRoot "work/phone-deck/android/local.properties"
    if (Test-Path -LiteralPath $localProps) {
        $content = Get-Content -LiteralPath $localProps -Raw
        if ($content -match "sdk\.dir\s*=\s*(.+)") {
            $sdkDir = $matches[1].Trim().Replace('\\', '\')
            if (Test-Path -LiteralPath $sdkDir) {
                return $sdkDir
            }
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($env:ANDROID_HOME) -and (Test-Path -LiteralPath $env:ANDROID_HOME)) {
        return $env:ANDROID_HOME
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ANDROID_SDK_ROOT) -and (Test-Path -LiteralPath $env:ANDROID_SDK_ROOT)) {
        return $env:ANDROID_SDK_ROOT
    }

    # 常见 SDK 路径
    $candidateSdk = "E:\Android\Sdk"
    if (Test-Path -LiteralPath $candidateSdk) {
        return $candidateSdk
    }

    throw "未找到 Android SDK 路径，请在 work/phone-deck/android/local.properties 设置 sdk.dir 或配置 ANDROID_HOME 环境变量。"
}

# 1. 清理
if ($Clean) {
    Write-Host "`n>>> [1/4] 清理历史产物..." -ForegroundColor Yellow
    if (Test-Path -LiteralPath $OutFull) {
        Remove-Item -LiteralPath $OutFull -Recurse -Force
    }
}
[System.IO.Directory]::CreateDirectory($OutFull) | Out-Null
$reportsDir = Join-Path $OutFull "reports"
[System.IO.Directory]::CreateDirectory($reportsDir) | Out-Null

$results = [ordered]@{}

# 2. Windows 平台构建与检查
if ($Platform -eq 'All' -or $Platform -eq 'Windows') {
    Write-Host "`n>>> 执行 Windows 平台构建与验证..." -ForegroundColor Green
    $winServerProj = Join-Path $RepoRoot "work/phone-deck/windows/PhoneDeck.Server/PhoneDeck.Server.csproj"
    $winServerTestsProj = Join-Path $RepoRoot "work/phone-deck/windows/PhoneDeck.Server.Tests/PhoneDeck.Server.Tests.csproj"
    $winConsoleProj = Join-Path $RepoRoot "work/phone-deck/windows/PhoneDeck.ControlCenter/PhoneDeck.ControlCenter.csproj"
    
    $winOutServer = Join-Path $OutFull "windows/server"
    $winOutConsole = Join-Path $OutFull "windows/console"
    $winTestResultsDir = Join-Path $reportsDir "windows-tests"
    [System.IO.Directory]::CreateDirectory($winTestResultsDir) | Out-Null

    # 2.1 单元测试
    if (-not $SkipTests) {
        Write-Host "  [Windows] 运行接收端单元测试..."
        dotnet test $winServerTestsProj -c $Configuration --logger "trx;LogFileName=PhoneDeck.Server.Tests.trx" --results-directory $winTestResultsDir
        Assert-StepSuccess "Windows Server 单元测试"
    } else {
        Write-Host "  [Windows] 跳过单元测试（-SkipTests）" -ForegroundColor DarkGray
    }

    # 2.2 构建
    Write-Host "  [Windows] 构建 PhoneDeck.Server ($Configuration)..."
    dotnet build $winServerProj -c $Configuration
    Assert-StepSuccess "Windows Server 编译"

    Write-Host "  [Windows] 构建 PhoneDeck.ControlCenter ($Configuration)..."
    dotnet build $winConsoleProj -c $Configuration
    Assert-StepSuccess "Windows ControlCenter 编译"

    # 2.3 自包含单文件发布
    Write-Host "  [Windows] 发布自包含单文件 PhoneDeck.Server..."
    if (Test-Path -LiteralPath $winOutServer) { Remove-Item -LiteralPath $winOutServer -Recurse -Force }
    dotnet publish $winServerProj -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -o $winOutServer
    Assert-StepSuccess "Windows Server 单文件发布"

    Write-Host "  [Windows] 发布自包含单文件 PhoneDeck.ControlCenter (含原生依赖)..."
    if (Test-Path -LiteralPath $winOutConsole) { Remove-Item -LiteralPath $winOutConsole -Recurse -Force }
    dotnet publish $winConsoleProj -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $winOutConsole
    Assert-StepSuccess "Windows ControlCenter 单文件发布"

    # 2.4 单文件与依赖严格检查
    Write-Host "  [Windows] 校验单文件产物与原生依赖嵌入..."
    $serverExe = Join-Path $winOutServer "PhoneDeck.Server.exe"
    $consoleExe = Join-Path $winOutConsole "PhoneDeck.ControlCenter.exe"

    if (-not (Test-Path -LiteralPath $serverExe)) {
        throw "单文件发布校验失败：未在输出目录找到 $serverExe"
    }
    if (-not (Test-Path -LiteralPath $consoleExe)) {
        throw "单文件发布校验失败：未在输出目录找到 $consoleExe"
    }

    # 检查控制台输出目录是否残留散落的原生 DLL
    $looseDlls = @(Get-ChildItem -LiteralPath $winOutConsole -Filter "*.dll" -File)
    if ($looseDlls.Count -gt 0) {
        $dllNames = ($looseDlls | ForEach-Object { $_.Name }) -join ", "
        throw "单文件依赖检查失败：控制台发布目录存在散落 DLL [$dllNames]，说明原生依赖未成功内嵌打包进单文件！"
    }

    $serverHash = (Get-FileHash -LiteralPath $serverExe -Algorithm SHA256).Hash.ToLowerInvariant()
    $serverSize = (Get-Item -LiteralPath $serverExe).Length
    $consoleHash = (Get-FileHash -LiteralPath $consoleExe -Algorithm SHA256).Hash.ToLowerInvariant()
    $consoleSize = (Get-Item -LiteralPath $consoleExe).Length

    Write-Host "    ✓ PhoneDeck.Server.exe ($([math]::Round($serverSize/1MB, 2)) MB, SHA256: $($serverHash.Substring(0,16))...)" -ForegroundColor Cyan
    Write-Host "    ✓ PhoneDeck.ControlCenter.exe (单文件内嵌原生库, $([math]::Round($consoleSize/1MB, 2)) MB, SHA256: $($consoleHash.Substring(0,16))...)" -ForegroundColor Cyan

    $results['Windows'] = @{
        Status = 'PASS'
        Server = $serverExe
        ServerSHA256 = $serverHash
        ControlCenter = $consoleExe
        ControlCenterSHA256 = $consoleHash
    }
}

# 3. Android 平台构建与检查
if ($Platform -eq 'All' -or $Platform -eq 'Android') {
    Write-Host "`n>>> 执行 Android 平台构建与验证..." -ForegroundColor Green
    Ensure-JavaHome | Out-Null
    Ensure-AndroidSdk | Out-Null
    
    $androidDir = Join-Path $RepoRoot "work/phone-deck/android"
    $androidOutDir = Join-Path $OutFull "android"
    $androidReportsDir = Join-Path $reportsDir "android"
    [System.IO.Directory]::CreateDirectory($androidOutDir) | Out-Null
    [System.IO.Directory]::CreateDirectory($androidReportsDir) | Out-Null

    $gradlewCmd = if ($IsWindows -or ($PSVersionTable.PSEdition -eq 'Desktop') -or ($env:OS -match 'Windows')) {
        Join-Path $androidDir "gradlew.bat"
    } else {
        Join-Path $androidDir "gradlew"
    }

    Push-Location $androidDir
    try {
        $tasks = @(':app:assembleDebug')
        if (-not $SkipTests) {
            $tasks += ':app:testDebugUnitTest'
        }
        $tasks += @(':app:lintDebug', ':app:assembleRelease')
        
        Write-Host "  [Android] 执行 Gradle 任务: $($tasks -join ' ')..."
        & $gradlewCmd @tasks --no-daemon --console=plain
        Assert-StepSuccess "Android Gradle 构建与测试"
    } finally {
        Pop-Location
    }

    # 校验产物
    $debugApkSrc = Join-Path $androidDir "app/build/outputs/apk/debug/app-debug.apk"
    if (-not (Test-Path -LiteralPath $debugApkSrc)) {
        throw "Android 构建产物缺失：未找到 $debugApkSrc"
    }

    $releaseApkDir = Join-Path $androidDir "app/build/outputs/apk/release"
    $releaseApks = @(Get-ChildItem -LiteralPath $releaseApkDir -Filter "*.apk" -File -ErrorAction SilentlyContinue)
    if ($releaseApks.Count -eq 0) {
        throw "Android Release APK 产物缺失：未在 $releaseApkDir 找到任何 APK"
    }

    # 拷贝产物到输出目录
    $debugApkDst = Join-Path $androidOutDir "PhoneDeck-debug.apk"
    Copy-Item -LiteralPath $debugApkSrc -Destination $debugApkDst -Force
    $debugHash = (Get-FileHash -LiteralPath $debugApkDst -Algorithm SHA256).Hash.ToLowerInvariant()
    $debugSize = (Get-Item -LiteralPath $debugApkDst).Length

    $primaryReleaseApk = $releaseApks[0]
    $releaseApkDst = Join-Path $androidOutDir $primaryReleaseApk.Name
    Copy-Item -LiteralPath $primaryReleaseApk.FullName -Destination $releaseApkDst -Force
    $releaseHash = (Get-FileHash -LiteralPath $releaseApkDst -Algorithm SHA256).Hash.ToLowerInvariant()
    $releaseSize = (Get-Item -LiteralPath $releaseApkDst).Length

    # 归档测试报告与 lint 报告
    $buildReportsDir = Join-Path $androidDir "app/build/reports"
    if (Test-Path -LiteralPath $buildReportsDir) {
        Get-ChildItem -LiteralPath $buildReportsDir | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $androidReportsDir -Recurse -Force
        }
    }

    Write-Host "    ✓ PhoneDeck Debug APK ($([math]::Round($debugSize/1MB, 2)) MB, SHA256: $($debugHash.Substring(0,16))...)" -ForegroundColor Cyan
    Write-Host "    ✓ PhoneDeck Release APK ($($primaryReleaseApk.Name), $([math]::Round($releaseSize/1MB, 2)) MB, SHA256: $($releaseHash.Substring(0,16))...)" -ForegroundColor Cyan
    Write-Host "    ✓ Android 测试与 Lint 报告已归档到 $androidReportsDir" -ForegroundColor Cyan

    $results['Android'] = @{
        Status = 'PASS'
        DebugApk = $debugApkDst
        DebugApkSHA256 = $debugHash
        ReleaseApk = $releaseApkDst
        ReleaseApkSHA256 = $releaseHash
        Reports = $androidReportsDir
    }
}

# 4. macOS 平台构建与检查
if ($Platform -eq 'All' -or $Platform -eq 'MacOS') {
    Write-Host "`n>>> 执行 macOS 平台构建与验证..." -ForegroundColor Green
    $macReceiverProj = Join-Path $RepoRoot "work/phone-deck/macos/PhoneDeck.Receiver/PhoneDeck.Receiver.csproj"
    $macReceiverTestsProj = Join-Path $RepoRoot "work/phone-deck/macos/PhoneDeck.Receiver.Tests/PhoneDeck.Receiver.Tests.csproj"
    $macTestResultsDir = Join-Path $reportsDir "macos-tests"
    [System.IO.Directory]::CreateDirectory($macTestResultsDir) | Out-Null

    # 4.1 单元测试（跨平台执行）
    if (-not $SkipTests) {
        Write-Host "  [macOS] 运行接收端单元测试..."
        dotnet test $macReceiverTestsProj -c $Configuration --logger "trx;LogFileName=PhoneDeck.Receiver.Tests.trx" --results-directory $macTestResultsDir
        Assert-StepSuccess "macOS Receiver 单元测试"
    }

    # 4.2 编译检查（跨平台执行）
    Write-Host "  [macOS] 构建 PhoneDeck.Receiver ($Configuration)..."
    dotnet build $macReceiverProj -c $Configuration
    Assert-StepSuccess "macOS Receiver 编译"

    # 4.3 如果在 macOS 环境且有 zsh，则执行 App Bundle 构建
    $isMacPlatform = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)
    if ($isMacPlatform) {
        Write-Host "  [macOS] 检测到当前系统为 macOS，构建 .app bundle..."
        $macBuildScript = Join-Path $RepoRoot "scripts/macos/Build-PhoneDeckReceiver.sh"
        zsh $macBuildScript
        Assert-StepSuccess "macOS App Bundle 构建"
    } else {
        Write-Host "  [macOS] 非 macOS 环境，已完成跨平台编译与逻辑单测验证（.app 打包在 macOS 运行）。" -ForegroundColor DarkGray
    }

    $results['MacOS'] = @{
        Status = 'PASS'
        Project = $macReceiverProj
    }
}

Write-Host "`n==================================================" -ForegroundColor Green
Write-Host "PhoneDeck 开发构建与检查全部通过！" -ForegroundColor Green
Write-Host "==================================================" -ForegroundColor Green
foreach ($key in $results.Keys) {
    Write-Host "[$key] $($results[$key].Status)" -ForegroundColor Green
}
Write-Host "输出目录: $OutFull"
Write-Host "报告目录: $reportsDir"
Write-Host "=================================================="
