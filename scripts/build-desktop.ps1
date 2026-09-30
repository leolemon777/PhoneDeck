# Standalone preview pipeline. Does not participate in legacy signed fleet updates.
[CmdletBinding()]
param(
    [ValidateSet('win-x64','linux-x64','linux-arm64','osx-x64','osx-arm64')][string]$Rid,
    [string]$WhisperSource,
    [string]$ModelPath,
    [switch]$IncludeModel,
    [switch]$VerifySpeech,
    [string]$OutputDirectory = 'outputs/desktop-release',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$version = '2.0.0-alpha.1'
$commit = '927cfce34f31707e17f2bff35c349632fb9e2c3a'
$modelHash = 'AE85E4A935D7A567BD102FE55AFC16BB595BDB618E11B2FC7591BC08120411BB'
if (!$Rid) { $Rid = if ($IsWindows) { 'win-x64' } elseif ($IsMacOS) { 'osx-' + [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant() } else { 'linux-' + [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant() } }
if (($Rid.StartsWith('win-') -and !$IsWindows) -or ($Rid.StartsWith('osx-') -and !$IsMacOS) -or ($Rid.StartsWith('linux-') -and !$IsLinux)) { throw 'Build native speech on the matching OS.' }
if (!$IsWindows -and !$Rid.EndsWith([Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant())) { throw 'Use a matching native architecture runner.' }
function Invoke-Checked([string]$Command, [string[]]$Arguments) { & $Command @Arguments; if ($LASTEXITCODE -ne 0) { throw "$Command failed: $LASTEXITCODE" } }
$root = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
# Packaging never copies a user data directory and never removes an existing output.
$run = Join-Path $root ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $Rid + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6))
$package = Join-Path $run "PhoneDeck-$version-$Rid"
New-Item -ItemType Directory -Path $package -Force | Out-Null
if (!$WhisperSource) {
    $WhisperSource = Join-Path $run 'whisper-source'
    Invoke-Checked git @('clone','--depth','1','--branch','v1.9.4','https://github.com/ggml-org/whisper.cpp.git',$WhisperSource)
}
$WhisperSource = [IO.Path]::GetFullPath($WhisperSource)
$actual = & git -C $WhisperSource rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $actual.Trim() -ne $commit) { throw 'whisper.cpp source must match the pinned commit.' }
$dirty = & git -C $WhisperSource status --porcelain
if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Refusing modified native source.' }
if (!$SkipTests) { Invoke-Checked dotnet @('test',(Join-Path $repo 'work/phone-deck/desktop/PhoneDeck.Desktop.Tests/PhoneDeck.Desktop.Tests.csproj'),'-c','Release') }
$native = Join-Path $run 'native-build'
$options = @('-S',(Join-Path $repo 'work/phone-deck/desktop/speech-runtime'),"-DWHISPER_SOURCE=$WhisperSource",'-B',$native,'-DCMAKE_BUILD_TYPE=Release','-DBUILD_SHARED_LIBS=OFF','-DGGML_NATIVE=OFF','-DGGML_AVX=OFF','-DGGML_AVX2=OFF','-DGGML_AVX512=OFF','-DGGML_FMA=OFF','-DGGML_F16C=OFF','-DGGML_BMI2=OFF','-DGGML_SSE42=OFF','-DGGML_OPENMP=OFF','-DGGML_METAL=OFF','-DGGML_BACKEND_DL=OFF','-DWHISPER_BUILD_TESTS=OFF','-DWHISPER_BUILD_SERVER=OFF','-DWHISPER_SDL2=OFF','-DWHISPER_BUILD_IS_DEV=OFF')
if ($IsWindows) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio C++ build tools are required.' }
    $installations = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json | ConvertFrom-Json)
    if (!$installations) { throw 'No Visual Studio installation with the C++ compiler was found.' }
    $major = ([Version]$installations[0].installationVersion).Major
    $generator = switch ($major) { 17 { 'Visual Studio 17 2022' }; 18 { 'Visual Studio 18 2026' }; default { throw "Unsupported Visual Studio version: $major" } }
    $options += @('-G',$generator,'-A','x64','-DCMAKE_POLICY_DEFAULT_CMP0091=NEW','-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded')
}
if ($IsMacOS) { $options += '-DCMAKE_OSX_DEPLOYMENT_TARGET=14.0' }
Invoke-Checked cmake $options
Invoke-Checked cmake @('--build',$native,'--config','Release','--target','whisper-cli','--parallel','4')
Invoke-Checked dotnet @('publish',(Join-Path $repo 'work/phone-deck/desktop/PhoneDeck.Desktop/PhoneDeck.Desktop.csproj'),'-c','Release','-r',$Rid,'--self-contained','true','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-o',$package)
$runtime = Join-Path $package 'speech-runtime'; New-Item -ItemType Directory -Path $runtime | Out-Null
$cli = if ($IsWindows) { Join-Path $native 'bin/Release/whisper-cli.exe' } else { Join-Path $native 'bin/whisper-cli' }
Copy-Item -LiteralPath $cli -Destination $runtime
Copy-Item -LiteralPath (Join-Path $WhisperSource 'LICENSE') -Destination (Join-Path $runtime 'LICENSE-whisper.cpp.txt')
Copy-Item -LiteralPath (Join-Path $repo 'docs/DESKTOP_QUICK_START.md') -Destination (Join-Path $package 'START-HERE.md')
Copy-Item -LiteralPath (Join-Path $repo 'docs/DESKTOP_THIRD_PARTY.md') -Destination (Join-Path $package 'THIRD-PARTY.md')
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $package 'LICENSE-PhoneDeck.txt')
Copy-Item -LiteralPath (Join-Path $repo 'licenses/desktop') -Destination (Join-Path $package 'licenses') -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'work/phone-deck/desktop/PhoneDeck.Desktop/packages.lock.json') -Destination (Join-Path $package 'dependencies.lock.json')
if ($IsWindows) { Copy-Item -LiteralPath (Join-Path $repo 'scripts/desktop/Start-PhoneDeck.vbs') -Destination $package }
if ($IncludeModel -and !$ModelPath) {
    $ModelPath = Join-Path $run 'ggml-small-q5_1.bin'
    Invoke-WebRequest -Uri 'https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-small-q5_1.bin' -OutFile $ModelPath
}
if ($ModelPath) {
    if ((Get-Item -LiteralPath $ModelPath).Length -ne 190085487 -or (Get-FileHash -LiteralPath $ModelPath -Algorithm SHA256).Hash -ne $modelHash) { throw 'Bundled model hash/size mismatch.' }
    New-Item -ItemType Directory -Path (Join-Path $package 'models') | Out-Null
    Copy-Item -LiteralPath $ModelPath -Destination (Join-Path $package 'models/ggml-small-q5_1.bin')
}
if ($VerifySpeech) {
    $validationModel = $ModelPath
    if (!$validationModel) {
        $validationModel = Join-Path $run 'speech-validation-model.bin'
        Invoke-WebRequest -Uri 'https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-small-q5_1.bin' -OutFile $validationModel
    }
    # Exercise native argv encoding and stdin/stdout on every OS, using only upstream public audio.
    # Keep the verification model outside the distributed package when first-run download is selected.
    $smoke = Join-Path $run '语音验收'
    New-Item -ItemType Directory -Path (Join-Path $smoke 'models'),(Join-Path $smoke 'speech-runtime') -Force | Out-Null
    Copy-Item -LiteralPath $validationModel -Destination (Join-Path $smoke 'models/ggml-small-q5_1.bin')
    Copy-Item -LiteralPath $cli -Destination (Join-Path $smoke 'speech-runtime')
    $python = if ($IsWindows) { 'python' } else { 'python3' }
    Invoke-Checked $python @((Join-Path $repo 'scripts/tests/Test-DesktopSpeech.py'),$smoke,(Join-Path $WhisperSource 'samples/jfk.wav'))
}
$manifest = [ordered]@{ version=$version; rid=$Rid; sourceCommit=(& git -C $repo rev-parse HEAD).Trim(); sourceDirty=[bool](& git -C $repo status --porcelain); whisperCommit=$commit; modelSha256=$modelHash.ToLowerInvariant(); modelBundled=[bool]$ModelPath; createdUtc=[DateTime]::UtcNow.ToString('O'); files=@() }
foreach ($file in Get-ChildItem -LiteralPath $package -File -Recurse) { $manifest.files += @{ path=[IO.Path]::GetRelativePath($package,$file.FullName).Replace('\','/'); sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); bytes=$file.Length } }
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $package 'build-manifest.json') -Encoding utf8NoBOM
if ($IsWindows) {
    $archive = Join-Path $run "PhoneDeck-$version-$Rid.zip"
    Compress-Archive -LiteralPath $package -DestinationPath $archive -CompressionLevel Optimal
    $iscc = Get-Command iscc -ErrorAction SilentlyContinue
    if ($iscc) { Invoke-Checked $iscc.Source @("/DSourceDir=$package","/DOutputDir=$run",(Join-Path $repo 'scripts/desktop/WindowsSetup.iss')) }
} elseif ($IsMacOS) {
    $bundle = Join-Path $run 'PhoneDeck.app'; $macos = Join-Path $bundle 'Contents/MacOS'; New-Item -ItemType Directory -Path $macos -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo 'scripts/desktop/Info.plist') -Destination (Join-Path $bundle 'Contents/Info.plist')
    Get-ChildItem -LiteralPath $package | Copy-Item -Destination $macos -Recurse
    # Ad-hoc preview only. Developer ID signing/notarization is a separate release gate.
    Invoke-Checked codesign @('--force','--deep','--sign','-',$bundle)
    $archive = Join-Path $run "PhoneDeck-$version-$Rid.tar.gz"; Invoke-Checked tar @('-czf',$archive,'-C',$run,'PhoneDeck.app')
} else {
    $archive = Join-Path $run "PhoneDeck-$version-$Rid.tar.gz"; Invoke-Checked tar @('-czf',$archive,'-C',$run,(Split-Path $package -Leaf))
    if (Get-Command dpkg-deb -ErrorAction SilentlyContinue) {
        $deb = Join-Path $run 'deb'; $opt = Join-Path $deb 'opt/phonedeck'; New-Item -ItemType Directory -Path $opt -Force | Out-Null
        Get-ChildItem -LiteralPath $package | Copy-Item -Destination $opt -Recurse
        New-Item -ItemType Directory -Path (Join-Path $deb 'DEBIAN'),(Join-Path $deb 'usr/share/applications') -Force | Out-Null
        $arch = if ($Rid.EndsWith('arm64')) { 'arm64' } else { 'amd64' }
        @("Package: phonedeck-desktop","Version: 2.0.0~alpha1","Architecture: $arch","Maintainer: PhoneDeck contributors <noreply@github.com>","Depends: libc6 (>= 2.35), libgcc-s1, libstdc++6, libssl3, zlib1g, libicu70 | libicu72 | libicu74 | libicu76 | libicu78, libx11-6, xdotool","Recommends: wtype","Description: PhoneDeck local voice and multi-computer text preview") | Set-Content -LiteralPath (Join-Path $deb 'DEBIAN/control') -Encoding utf8NoBOM
        Copy-Item -LiteralPath (Join-Path $repo 'scripts/desktop/phonedeck.desktop') -Destination (Join-Path $deb 'usr/share/applications')
        Invoke-Checked dpkg-deb @('--build','--root-owner-group',$deb,(Join-Path $run "PhoneDeck-$version-$Rid.deb"))
    }
}
Get-ChildItem -LiteralPath $run -File | Where-Object Extension -In '.zip','.gz','.exe','.deb' | ForEach-Object { "$( (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" } | Set-Content -LiteralPath (Join-Path $run 'checksums.sha256') -Encoding utf8NoBOM
Write-Host "Package directory: $run"
if ($env:GITHUB_OUTPUT) { "package_directory=$run" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8 }
