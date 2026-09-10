<#
.SYNOPSIS
    PhoneDeck 统一开发构建入口 (B01)
.DESCRIPTION
    每次在 OutputDir 下创建唯一 runId 目录；写 manifest.json / latest.json；
    失败即停并保留阶段退出码。不递归删除历史输出。
.PARAMETER Platform
    All | Windows | Android | MacOS
.PARAMETER Configuration
    Release（默认）或 Debug
.PARAMETER OutputDir
    产物根目录，默认 outputs/build-review；必须是仓库 outputs 下的子目录
.PARAMETER Clean
    兼容参数：仅警告新 run 天然隔离，不删除历史输出
.PARAMETER SkipTests
    跳过单元测试；最终 status=unverified
#>
[CmdletBinding()]
param(
    [ValidateSet('All', 'Windows', 'Android', 'MacOS')]
    [string]$Platform = 'All',

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$OutputDir = 'outputs/build-review',

    [switch]$Clean,

    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# 保留外部命令原始退出码；避免 native 失败在读取 LASTEXITCODE 前被转成终止异常
$PSNativeCommandUseErrorActionPreference = $false

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "PhoneDeck 构建脚本要求 PowerShell 7+ (pwsh)。当前版本: $($PSVersionTable.PSVersion)"
}

$script:PhaseExitCode = 1
$script:PhaseName = 'init'
$script:RunId = $null
$script:RunDir = $null
$script:RunDirRelative = $null
$script:OutFull = $null
$script:ManifestPath = $null
$script:LatestPath = $null
$script:Artifacts = [System.Collections.Generic.List[object]]::new()
$script:FinalStatus = 'failed'
$script:ManifestInitialized = $false

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path -LiteralPath (Join-Path $ScriptDir '..')).Path

function Get-RepoRelativePath {
    param([Parameter(Mandatory)][string]$FullPath)
    $rel = [System.IO.Path]::GetRelativePath($RepoRoot, $FullPath)
    return ($rel -replace '\\', '/')
}

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
            throw "[security] 目录链中不允许软链接/符号链接/Junction。找到: $current"
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
            throw "[security] 目录树包含软链接/符号链接/Junction，拒绝继续。找到: $($item.FullName)"
        }
    }
}

function Resolve-SafeOutputRoot {
    param([Parameter(Mandatory)][string]$RequestedOutputDir)

    $outFull = if ([System.IO.Path]::IsPathRooted($RequestedOutputDir)) {
        [System.IO.Path]::GetFullPath($RequestedOutputDir)
    } else {
        [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $RequestedOutputDir))
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
        throw "[security] 输出目录必须位于仓库根目录的 outputs 子目录内（不能是 outputs 本身）。请求的目录: $outFull"
    }

    # 宿主大小写：在大小写敏感文件系统上，要求相对路径各段与真实目录名大小写一致
    if ($comparison -eq [System.StringComparison]::Ordinal -and (Test-Path -LiteralPath $allowedOutputsDir)) {
        $rel = [System.IO.Path]::GetRelativePath($RepoRoot, $outFull) -replace '\\', '/'
        $expectedPrefix = 'outputs/'
        if (-not $rel.StartsWith($expectedPrefix, [System.StringComparison]::Ordinal)) {
            throw "[security] 输出目录相对路径大小写必须为 outputs/...，实际: $rel"
        }
        $cursor = $RepoRoot
        foreach ($segment in ($rel -split '/')) {
            if ([string]::IsNullOrWhiteSpace($segment)) { continue }
            $matched = Get-ChildItem -LiteralPath $cursor -Force -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -ceq $segment } |
                Select-Object -First 1
            if (-not $matched) {
                # 段尚不存在时，仅允许后续 CreateDirectory；已存在但不匹配大小写则拒绝
                $wrongCase = Get-ChildItem -LiteralPath $cursor -Force -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -ieq $segment -and $_.Name -cne $segment } |
                    Select-Object -First 1
                if ($wrongCase) {
                    throw "[security] 输出路径段大小写不匹配。期望 '$segment'，实际 '$($wrongCase.Name)'（于 $cursor）"
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

function New-ManifestObject {
    param(
        [Parameter(Mandatory)][string]$Status,
        [Parameter(Mandatory)][string]$Phase,
        [Parameter(Mandatory)][int]$ExitCode
    )
    $runDirectory = if ($script:RunDirRelative) { $script:RunDirRelative } else { '' }
    $runIdValue = if ($script:RunId) { $script:RunId } else { '' }
    return [ordered]@{
        schemaVersion     = 1
        runId             = $runIdValue
        runDirectory      = $runDirectory
        status            = $Status
        phase             = $Phase
        exitCode          = $ExitCode
        testsSkipped      = [bool]$SkipTests
        artifacts         = @($script:Artifacts)
        reportsDirectory  = 'reports'
    }
}

function Write-AtomicTextFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Content
    )
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $dir = [System.IO.Path]::GetDirectoryName($fullPath)
    if ([string]::IsNullOrWhiteSpace($dir)) { throw "无效写入路径: $Path" }
    [System.IO.Directory]::CreateDirectory($dir) | Out-Null

    # 随机临时名 + CreateNew：避免固定 .tmp 预置 symlink 被 WriteAllText 穿透
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
                # 删除链接节点本身，绝不跟随写到 symlink target
                [System.IO.File]::Delete($fullPath)
            }
        }
        [System.IO.File]::Move($tmp, $fullPath, $true)
    } catch {
        if (Test-Path -LiteralPath $tmp) {
            Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        }
        throw
    }
}

function Write-ManifestFiles {
    param(
        [Parameter(Mandatory)][string]$Status,
        [Parameter(Mandatory)][string]$Phase,
        [Parameter(Mandatory)][int]$ExitCode
    )
    $manifest = New-ManifestObject -Status $Status -Phase $Phase -ExitCode $ExitCode
    $json = ($manifest | ConvertTo-Json -Depth 8) + [Environment]::NewLine
    # run manifest 先写；latest 失败时仍抛错，由调用方非零退出（避免伪成功）
    if ($script:ManifestPath) {
        Write-AtomicTextFile -Path $script:ManifestPath -Content $json
    }
    if ($script:LatestPath) {
        Write-AtomicTextFile -Path $script:LatestPath -Content $json
    }
}

function Write-CiOutput {
    param($Manifest)
    if ([string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) { return }
    @(
        "run_id=$($Manifest.runId)"
        "run_directory=$($Manifest.runDirectory)"
        "status=$($Manifest.status)"
        "phase=$($Manifest.phase)"
        "exit_code=$($Manifest.exitCode)"
        "tests_skipped=$($Manifest.testsSkipped)"
    ) | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
}

function Write-PrecheckFailureLatest {
    param(
        [Parameter(Mandatory)][string]$Phase,
        [Parameter(Mandatory)][int]$ExitCode,
        [Parameter(Mandatory)][string]$Message
    )
    Write-Host $Message -ForegroundColor Red
    # OutputDir 已确认安全：写入/覆盖 latest.json，避免 CI 把旧成功结果当成本次
    $script:LatestPath = Join-Path $script:OutFull 'latest.json'
    [System.IO.Directory]::CreateDirectory($script:OutFull) | Out-Null
    $script:RunId = ''
    $script:RunDirRelative = ''
    $script:Artifacts.Clear()
    $manifest = New-ManifestObject -Status 'failed' -Phase $Phase -ExitCode $ExitCode
    $json = ($manifest | ConvertTo-Json -Depth 8) + [Environment]::NewLine
    Write-AtomicTextFile -Path $script:LatestPath -Content $json
    Write-CiOutput -Manifest $manifest
}

function Write-FinalCiOutput {
    param(
        [Parameter(Mandatory)][string]$Status,
        [Parameter(Mandatory)][string]$Phase,
        [Parameter(Mandatory)][int]$ExitCode
    )
    Write-CiOutput -Manifest (New-ManifestObject -Status $Status -Phase $Phase -ExitCode $ExitCode)
}

function Invoke-NativeStep {
    param(
        [Parameter(Mandatory)][string]$StepName,
        [Parameter(Mandatory)][scriptblock]$Action
    )
    $script:PhaseName = $StepName
    Write-Host "  → $StepName" -ForegroundColor DarkCyan
    & $Action
    $code = $LASTEXITCODE
    if ($null -eq $code) { $code = 0 }
    if ($code -ne 0) {
        $script:PhaseExitCode = [int]$code
        throw "步骤 [$StepName] 失败，退出码: $code"
    }
}

function Add-Artifact {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$FullPath
    )
    if (-not $script:RunDir) { return }
    $rel = [System.IO.Path]::GetRelativePath($script:RunDir, $FullPath) -replace '\\', '/'
    $script:Artifacts.Add([ordered]@{ name = $Name; path = $rel })
}

function Ensure-JavaHome {
    $javacName = if ($IsWindows) { 'javac.exe' } else { 'javac' }
    $javaName = if ($IsWindows) { 'java.exe' } else { 'java' }

    function Test-IsJdk17 {
        param([string]$HomePath)
        if ([string]::IsNullOrWhiteSpace($HomePath)) { return $false }
        $javacPath = Join-Path $HomePath "bin/$javacName"
        if (-not (Test-Path -LiteralPath $javacPath)) { return $false }
        $javaCmd = Join-Path $HomePath "bin/$javaName"
        $versionInfo = & $javaCmd -version 2>&1 | Out-String
        return [bool]($versionInfo -match 'version "17(\.|$)')
    }

    if (-not [string]::IsNullOrWhiteSpace($env:JAVA_HOME)) {
        if (Test-IsJdk17 $env:JAVA_HOME) { return $env:JAVA_HOME }
        throw "JAVA_HOME 存在但不是有效的 JDK 17（或缺 javac）。值: $($env:JAVA_HOME)"
    }

    $javaCmd = Get-Command java -ErrorAction SilentlyContinue
    if ($javaCmd) {
        $javaBin = Split-Path -Parent $javaCmd.Source
        $candidate = Split-Path -Parent $javaBin
        if (Test-IsJdk17 $candidate) {
            $env:JAVA_HOME = $candidate
            Write-Host "从 PATH 探测 JDK 17: $($env:JAVA_HOME)" -ForegroundColor Yellow
            return $candidate
        }
    }

    throw '未找到有效的 JDK 17。请设置 JAVA_HOME，或将 JDK 17 的 java/javac 放入 PATH。'
}

function Ensure-AndroidSdk {
    $sdkDir = $null
    $localProps = Join-Path $RepoRoot 'work/phone-deck/android/local.properties'
    if (Test-Path -LiteralPath $localProps) {
        foreach ($line in Get-Content -LiteralPath $localProps) {
            if ($line -match '^\s*sdk\.dir\s*=\s*(.+)\s*$') {
                # Gradle local.properties 常见转义：C\:\\Users\\...
                $propDir = $matches[1].Trim().Trim('"')
                $propDir = $propDir -replace '\\:', ':' -replace '\\\\', '\'
                if (Test-Path -LiteralPath $propDir) {
                    $sdkDir = $propDir
                    break
                }
            }
        }
    }

    if (-not $sdkDir -and -not [string]::IsNullOrWhiteSpace($env:ANDROID_HOME) -and (Test-Path -LiteralPath $env:ANDROID_HOME)) {
        $sdkDir = $env:ANDROID_HOME
    }
    if (-not $sdkDir -and -not [string]::IsNullOrWhiteSpace($env:ANDROID_SDK_ROOT) -and (Test-Path -LiteralPath $env:ANDROID_SDK_ROOT)) {
        $sdkDir = $env:ANDROID_SDK_ROOT
    }
    if (-not $sdkDir) {
        $candidates = @()
        if ($IsWindows -and $env:LOCALAPPDATA) {
            $candidates += (Join-Path $env:LOCALAPPDATA 'Android\Sdk')
        }
        if ($env:HOME) {
            $candidates += (Join-Path $env:HOME 'Library/Android/sdk')
            $candidates += (Join-Path $env:HOME 'Android/Sdk')
        }
        foreach ($candidate in $candidates) {
            if (Test-Path -LiteralPath $candidate) {
                $sdkDir = $candidate
                break
            }
        }
    }

    if (-not $sdkDir) {
        throw '未找到 Android SDK。请在 work/phone-deck/android/local.properties 设置 sdk.dir，或配置 ANDROID_HOME / ANDROID_SDK_ROOT。'
    }

    $env:ANDROID_HOME = $sdkDir
    if ([string]::IsNullOrWhiteSpace($env:ANDROID_SDK_ROOT)) {
        $env:ANDROID_SDK_ROOT = $sdkDir
    }
    return $sdkDir
}

function Assert-PlatformHostSupport {
    $isMac = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::OSX)

    switch ($Platform) {
        'Windows' {
            if (-not $IsWindows) {
                throw '[precheck] 当前宿主不支持 Windows 完整产物（WPF/WinForms 需要 Windows）。'
            }
        }
        'MacOS' {
            if (-not $isMac) {
                throw '[precheck] 当前宿主不支持 macOS .app 完整产物（需要 macOS）。'
            }
        }
        'All' {
            if ($IsWindows) {
                # All = 宿主目标 + Android
            } elseif ($isMac) {
                # All = macOS + Android
            } else {
                Write-Host '当前为非 Windows/macOS 宿主：Platform=All 仅执行 Android 目标。' -ForegroundColor Yellow
            }
        }
    }
}

function Assert-ToolPrechecks {
    $isMac = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::OSX)
    $needWindows = ($Platform -eq 'Windows') -or ($Platform -eq 'All' -and $IsWindows)
    $needMac = ($Platform -eq 'MacOS') -or ($Platform -eq 'All' -and $isMac)
    $needAndroid = ($Platform -eq 'Android') -or ($Platform -eq 'All')

    if ($needWindows -or $needMac) {
        $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
        if (-not $dotnet) { throw '未找到 dotnet，请安装 .NET SDK 并确保在 PATH 中。' }
    }
    if ($needAndroid) {
        Ensure-JavaHome | Out-Null
        Ensure-AndroidSdk | Out-Null
        $androidDir = Join-Path $RepoRoot 'work/phone-deck/android'
        $gradlew = if ($IsWindows) {
            Join-Path $androidDir 'gradlew.bat'
        } else {
            Join-Path $androidDir 'gradlew'
        }
        if (-not (Test-Path -LiteralPath $gradlew)) {
            throw "未找到 Gradle Wrapper: $gradlew"
        }
    }
}

function Get-MacHostRid {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    switch ($arch) {
        'Arm64' { return 'osx-arm64' }
        'X64' { return 'osx-x64' }
        default {
            $uname = (& uname -m 2>$null)
            if ($uname -eq 'arm64') { return 'osx-arm64' }
            if ($uname -eq 'x86_64') { return 'osx-x64' }
            throw "无法确定本机 macOS RID（架构: $arch / uname: $uname）"
        }
    }
}

function Invoke-WindowsBuild {
    $winServerProj = Join-Path $RepoRoot 'work/phone-deck/windows/PhoneDeck.Server/PhoneDeck.Server.csproj'
    $winServerTestsProj = Join-Path $RepoRoot 'work/phone-deck/windows/PhoneDeck.Server.Tests/PhoneDeck.Server.Tests.csproj'
    $winConsoleProj = Join-Path $RepoRoot 'work/phone-deck/windows/PhoneDeck.ControlCenter/PhoneDeck.ControlCenter.csproj'
    $winOutServer = Join-Path $script:RunDir 'windows/server'
    $winOutConsole = Join-Path $script:RunDir 'windows/console'
    $winTestResultsDir = Join-Path $script:RunDir 'reports/windows-tests'
    [System.IO.Directory]::CreateDirectory($winOutServer) | Out-Null
    [System.IO.Directory]::CreateDirectory($winOutConsole) | Out-Null
    [System.IO.Directory]::CreateDirectory($winTestResultsDir) | Out-Null

    if (-not $SkipTests) {
        Invoke-NativeStep 'windows-test' {
            Write-Host '  [Windows] 运行接收端单元测试...'
            dotnet test $winServerTestsProj -c $Configuration --logger 'trx;LogFileName=PhoneDeck.Server.Tests.trx' --results-directory $winTestResultsDir
        }
    } else {
        Write-Host '  [Windows] 跳过单元测试（-SkipTests）' -ForegroundColor DarkGray
    }

    Invoke-NativeStep 'windows-build-server' {
        Write-Host "  [Windows] 构建 PhoneDeck.Server ($Configuration)..."
        dotnet build $winServerProj -c $Configuration
    }
    Invoke-NativeStep 'windows-build-console' {
        Write-Host "  [Windows] 构建 PhoneDeck.ControlCenter ($Configuration)..."
        dotnet build $winConsoleProj -c $Configuration
    }

    Invoke-NativeStep 'windows-publish-server' {
        Write-Host '  [Windows] 发布自包含单文件 PhoneDeck.Server（含原生依赖）...'
        dotnet publish $winServerProj -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $winOutServer
    }
    Invoke-NativeStep 'windows-publish-console' {
        Write-Host '  [Windows] 发布自包含单文件 PhoneDeck.ControlCenter（含原生依赖）...'
        dotnet publish $winConsoleProj -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $winOutConsole
    }

    Invoke-NativeStep 'windows-validate' {
        $serverExe = Join-Path $winOutServer 'PhoneDeck.Server.exe'
        $consoleExe = Join-Path $winOutConsole 'PhoneDeck.ControlCenter.exe'
        if (-not (Test-Path -LiteralPath $serverExe)) { throw "单文件发布校验失败：未找到 $serverExe" }
        if (-not (Test-Path -LiteralPath $consoleExe)) { throw "单文件发布校验失败：未找到 $consoleExe" }

        foreach ($dir in @($winOutServer, $winOutConsole)) {
            $looseDlls = @(Get-ChildItem -LiteralPath $dir -Filter '*.dll' -File -ErrorAction SilentlyContinue)
            if ($looseDlls.Count -gt 0) {
                $dllNames = ($looseDlls | ForEach-Object { $_.Name }) -join ', '
                throw "单文件依赖检查失败：目录 $dir 存在散落 DLL [$dllNames]"
            }
        }

        Add-Artifact -Name 'windows-server-exe' -FullPath $serverExe
        Add-Artifact -Name 'windows-console-exe' -FullPath $consoleExe
        Write-Host "    ✓ Windows EXE 已写入本次 run" -ForegroundColor Cyan
    }
}

function Invoke-AndroidBuild {
    $androidDir = Join-Path $RepoRoot 'work/phone-deck/android'
    $androidOutDir = Join-Path $script:RunDir 'android'
    $androidReportsDir = Join-Path $script:RunDir 'reports/android'
    [System.IO.Directory]::CreateDirectory($androidOutDir) | Out-Null
    [System.IO.Directory]::CreateDirectory($androidReportsDir) | Out-Null

    $gradlewCmd = if ($IsWindows) {
        Join-Path $androidDir 'gradlew.bat'
    } else {
        Join-Path $androidDir 'gradlew'
    }

    $projectBuildDir = Join-Path $androidDir 'build'
    $appBuildDir = Join-Path $androidDir 'app/build'
    Assert-AncestorChainSafe -TargetPath $projectBuildDir -StopAtPath $androidDir
    Assert-AncestorChainSafe -TargetPath $appBuildDir -StopAtPath $androidDir
    Assert-TreeHasNoReparse -RootPath $projectBuildDir
    Assert-TreeHasNoReparse -RootPath $appBuildDir

    Push-Location $androidDir
    try {
        Invoke-NativeStep 'android-clean' {
            Write-Host '  [Android] Gradle clean...'
            & $gradlewCmd clean --no-daemon --console=plain
        }

        $tasks = [System.Collections.Generic.List[string]]::new()
        $tasks.Add(':app:assembleDebug') | Out-Null
        if (-not $SkipTests) { $tasks.Add(':app:testDebugUnitTest') | Out-Null }
        $tasks.Add(':app:lintDebug') | Out-Null
        $tasks.Add(':app:assembleRelease') | Out-Null

        Invoke-NativeStep 'android-build' {
            Write-Host "  [Android] Gradle: $($tasks -join ' ')..."
            & $gradlewCmd @($tasks.ToArray()) --no-daemon --console=plain
        }
    } finally {
        # 失败也尽量归档本次报告
        $buildReportsDir = Join-Path $androidDir 'app/build/reports'
        if (Test-Path -LiteralPath $buildReportsDir) {
            try {
                Assert-TreeHasNoReparse -RootPath $buildReportsDir
                Get-ChildItem -LiteralPath $buildReportsDir -Force | ForEach-Object {
                    if (($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -or $_.LinkType) {
                        throw "[security] 跳过含链接的报告项: $($_.FullName)"
                    }
                    Copy-Item -LiteralPath $_.FullName -Destination $androidReportsDir -Recurse -Force -ErrorAction SilentlyContinue
                }
            } catch {
                Write-Host "  [Android] 报告归档警告: $_" -ForegroundColor Yellow
            }
        }
        Pop-Location
    }

    $debugApkSrc = Join-Path $androidDir 'app/build/outputs/apk/debug/app-debug.apk'
    if (-not (Test-Path -LiteralPath $debugApkSrc)) {
        throw "Android 构建产物缺失：未找到 $debugApkSrc"
    }
    $releaseApkDir = Join-Path $androidDir 'app/build/outputs/apk/release'
    $releaseApks = @(Get-ChildItem -LiteralPath $releaseApkDir -Filter '*.apk' -File -ErrorAction SilentlyContinue)
    if ($releaseApks.Count -eq 0) {
        throw "Android Release APK 产物缺失：未在 $releaseApkDir 找到任何 APK"
    }

    $debugApkDst = Join-Path $androidOutDir 'PhoneDeck-debug.apk'
    Copy-Item -LiteralPath $debugApkSrc -Destination $debugApkDst -Force
    $primaryReleaseApk = $releaseApks[0]
    $releaseApkDst = Join-Path $androidOutDir $primaryReleaseApk.Name
    Copy-Item -LiteralPath $primaryReleaseApk.FullName -Destination $releaseApkDst -Force

    Add-Artifact -Name 'android-debug-apk' -FullPath $debugApkDst
    Add-Artifact -Name 'android-release-apk' -FullPath $releaseApkDst
    Write-Host '    ✓ Android APK 与报告已写入本次 run' -ForegroundColor Cyan
}

function Invoke-MacBuild {
    $macReceiverProj = Join-Path $RepoRoot 'work/phone-deck/macos/PhoneDeck.Receiver/PhoneDeck.Receiver.csproj'
    $macReceiverTestsProj = Join-Path $RepoRoot 'work/phone-deck/macos/PhoneDeck.Receiver.Tests/PhoneDeck.Receiver.Tests.csproj'
    $macTestResultsDir = Join-Path $script:RunDir 'reports/macos-tests'
    $macOutDir = Join-Path $script:RunDir 'macos'
    [System.IO.Directory]::CreateDirectory($macTestResultsDir) | Out-Null
    [System.IO.Directory]::CreateDirectory($macOutDir) | Out-Null

    if (-not $SkipTests) {
        Invoke-NativeStep 'macos-test' {
            Write-Host '  [macOS] 运行接收端单元测试...'
            dotnet test $macReceiverTestsProj -c $Configuration --logger 'trx;LogFileName=PhoneDeck.Receiver.Tests.trx' --results-directory $macTestResultsDir
        }
    } else {
        Write-Host '  [macOS] 跳过单元测试（-SkipTests）' -ForegroundColor DarkGray
    }

    Invoke-NativeStep 'macos-build' {
        Write-Host "  [macOS] 构建 PhoneDeck.Receiver ($Configuration)..."
        dotnet build $macReceiverProj -c $Configuration
    }

    $rid = Get-MacHostRid
    $archArg = if ($rid -eq 'osx-arm64') { 'arm64' } else { 'x64' }
    $macBuildScript = Join-Path $RepoRoot 'scripts/macos/Build-PhoneDeckReceiver.sh'
    Invoke-NativeStep 'macos-app' {
        Write-Host "  [macOS] 构建 .app bundle ($rid)..."
        if (Get-Command zsh -ErrorAction SilentlyContinue) {
            & zsh $macBuildScript $archArg
        } else {
            & bash $macBuildScript $archArg
        }
    }

    $appSrc = Join-Path $RepoRoot "work/phone-deck/dist/macos/$rid/PhoneDeck Receiver.app"
    if (-not (Test-Path -LiteralPath $appSrc)) {
        throw "macOS App Bundle 产物缺失：未找到 $appSrc"
    }

    $appDst = Join-Path $macOutDir 'PhoneDeck Receiver.app'
    if (Test-Path -LiteralPath $appDst) {
        throw "macOS run 目标已存在（新 run 目录不应预存 .app）: $appDst"
    }
    # 复制实际 .app，保留权限；不把 publish 目录带入 run
    Invoke-NativeStep 'macos-copy-app' {
        Write-Host '  [macOS] 复制 .app 到 run 目录...'
        & cp -R -p -- "$appSrc" "$appDst"
    }

    $tarName = 'PhoneDeck-Receiver.app.tar'
    $tarPath = Join-Path $macOutDir $tarName
    Invoke-NativeStep 'macos-tar-app' {
        Write-Host '  [macOS] 归档 .app（仅 app，保留权限）...'
        & tar -cf $tarPath -C $macOutDir 'PhoneDeck Receiver.app'
    }

    Add-Artifact -Name 'macos-app' -FullPath $appDst
    Add-Artifact -Name 'macos-app-tar' -FullPath $tarPath
    Write-Host "    ✓ macOS .app 与 tar 已写入本次 run（RID=$rid）" -ForegroundColor Cyan
}

# ---------------------------------------------------------------------------
# 主流程：先校验 OutputDir → 前置检查 → 再创建 run
# ---------------------------------------------------------------------------
try {
    $script:PhaseName = 'output-safety'
    $script:OutFull = Resolve-SafeOutputRoot -RequestedOutputDir $OutputDir
    $script:LatestPath = Join-Path $script:OutFull 'latest.json'

    Write-Host '==================================================' -ForegroundColor Cyan
    Write-Host 'PhoneDeck 统一开发构建入口 (B01)' -ForegroundColor Cyan
    Write-Host '==================================================' -ForegroundColor Cyan
    Write-Host "仓库根目录: $RepoRoot"
    Write-Host "目标平台:   $Platform"
    Write-Host "构建配置:   $Configuration"
    Write-Host "输出根目录: $($script:OutFull)"
    Write-Host "运行环境:   $([System.Environment]::OSVersion.VersionString)"
    Write-Host '=================================================='

    if ($Clean) {
        Write-Host '>>> -Clean：新 run 目录天然隔离历史输出，不删除既有产物。' -ForegroundColor Yellow
    }

    $script:PhaseName = 'precheck'
    Assert-PlatformHostSupport
    Assert-ToolPrechecks

    $script:PhaseName = 'create-run'
    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
    $suffix = [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $script:RunId = "$stamp-$suffix"
    $script:RunDir = Join-Path $script:OutFull $script:RunId
    $script:RunDirRelative = Get-RepoRelativePath -FullPath $script:RunDir
    [System.IO.Directory]::CreateDirectory((Join-Path $script:RunDir 'reports')) | Out-Null
    $script:ManifestPath = Join-Path $script:RunDir 'manifest.json'
    $script:PhaseExitCode = 0
    Write-ManifestFiles -Status 'running' -Phase 'create-run' -ExitCode 0
    $script:ManifestInitialized = $true
    Write-Host "本次 runId: $($script:RunId)" -ForegroundColor Cyan
    Write-Host "本次目录:   $($script:RunDirRelative)"

    $isMac = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::OSX)

    if (($Platform -eq 'Windows') -or ($Platform -eq 'All' -and $IsWindows)) {
        $script:PhaseName = 'windows'
        Write-Host "`n>>> Windows 平台构建与验证..." -ForegroundColor Green
        Invoke-WindowsBuild
    } elseif ($Platform -eq 'All' -and -not $IsWindows) {
        Write-Host "`n>>> [Windows] 非 Windows 宿主，跳过 WPF/WinForms 产物。" -ForegroundColor DarkGray
    }

    if (($Platform -eq 'Android') -or ($Platform -eq 'All')) {
        $script:PhaseName = 'android'
        Write-Host "`n>>> Android 平台构建与验证..." -ForegroundColor Green
        Invoke-AndroidBuild
    }

    if (($Platform -eq 'MacOS') -or ($Platform -eq 'All' -and $isMac)) {
        $script:PhaseName = 'macos'
        Write-Host "`n>>> macOS 平台构建与验证..." -ForegroundColor Green
        Invoke-MacBuild
    } elseif ($Platform -eq 'All' -and -not $isMac) {
        Write-Host "`n>>> [macOS] 非 macOS 宿主，跳过 .app 产物。" -ForegroundColor DarkGray
    }

    $script:PhaseName = 'complete'
    $script:PhaseExitCode = 0
    $script:FinalStatus = if ($SkipTests) { 'unverified' } else { 'success' }
}
catch {
    if ($script:PhaseExitCode -eq 0) { $script:PhaseExitCode = 1 }
    $script:FinalStatus = 'failed'
    $message = $_.Exception.Message
    if (-not $script:ManifestInitialized -and $script:OutFull) {
        # 前置失败：不创建 run，但覆盖 latest，避免旧成功结果被当成本次
        try {
            Write-PrecheckFailureLatest -Phase $script:PhaseName -ExitCode $script:PhaseExitCode -Message $message
        } catch {
            Write-Host "写入 precheck latest 失败: $_" -ForegroundColor Yellow
            Write-Host $message -ForegroundColor Red
        }
    } else {
        Write-Host $message -ForegroundColor Red
    }
}
finally {
    if ($script:ManifestInitialized) {
        $manifestWriteOk = $false
        try {
            Write-ManifestFiles -Status $script:FinalStatus -Phase $script:PhaseName -ExitCode $script:PhaseExitCode
            $manifestWriteOk = $true
            Write-FinalCiOutput -Status $script:FinalStatus -Phase $script:PhaseName -ExitCode $script:PhaseExitCode
        } catch {
            Write-Host "写入最终 manifest/latest 失败: $_" -ForegroundColor Red
            if ($script:PhaseExitCode -eq 0) { $script:PhaseExitCode = 1 }
            $script:FinalStatus = 'failed'
            # run manifest 已写成功但 latest 失败：仍非零退出，且不得再写成功态 CI 输出
            if (-not $manifestWriteOk) {
                try {
                    Write-FinalCiOutput -Status 'failed' -Phase $script:PhaseName -ExitCode $script:PhaseExitCode
                } catch {
                    Write-Host "写入失败态 CI 输出也失败: $_" -ForegroundColor Yellow
                }
            }
        }
    }

    Write-Host "`n==================================================" -ForegroundColor Cyan
    Write-Host "status=$($script:FinalStatus) phase=$($script:PhaseName) exitCode=$($script:PhaseExitCode)" -ForegroundColor Cyan
    if ($script:RunDirRelative) {
        Write-Host "runDirectory=$($script:RunDirRelative)"
    }
    Write-Host '=================================================='
}

exit $script:PhaseExitCode
