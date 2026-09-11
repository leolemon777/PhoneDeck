param(
    [Parameter(Mandatory)][string]$Server,
    [Parameter(Mandatory)][string]$ControlCenter,
    [Parameter(Mandatory)][string]$Apk,
    [Parameter(Mandatory)][long]$Sequence,
    [Parameter(Mandatory)][string]$WindowsVersion,
    [Parameter(Mandatory)][int]$AndroidVersionCode,
    [Parameter(Mandatory)][string]$SigningKey,
    [Parameter(Mandatory)][string]$Output
)
# Run with PowerShell 7. The private key is PKCS#8 DER, stored outside Git.
$ErrorActionPreference = 'Stop'
$taskFiles = @(
    @{ name = 'PhoneDeck.Server.exe'; source = (Resolve-Path -LiteralPath $Server).Path },
    @{ name = 'PhoneDeck.ControlCenter.exe'; source = (Resolve-Path -LiteralPath $ControlCenter).Path },
    @{ name = 'PhoneDeck.apk'; source = (Resolve-Path -LiteralPath $Apk).Path }
)

$descriptorPath = Join-Path $PSScriptRoot "release-versions.json"
$descriptor = Get-Content -Raw -LiteralPath $descriptorPath | ConvertFrom-Json

if ($WindowsVersion -ne $descriptor.windows.version -or
    $Sequence -ne $descriptor.windows.sequence -or
    $AndroidVersionCode -ne $descriptor.android.versionCode) {
    throw "CLI values do not match descriptor base"
}

$assertReleaseScript = Join-Path $PSScriptRoot "..\..\scripts\versioning\Assert-ReleaseVersions.ps1"
& pwsh -NoProfile -NonInteractive -File $assertReleaseScript -DescriptorPath $descriptorPath
if ($LASTEXITCODE -ne 0) {
    throw "Packaging aborted: Assert-ReleaseVersions.ps1 validation failed with exit code $LASTEXITCODE."
}

$assertScript = Join-Path $PSScriptRoot "..\..\scripts\packaging\Assert-PackageVersions.ps1"
& pwsh -NoProfile -NonInteractive -File $assertScript -Server $taskFiles[0].source -ControlCenter $taskFiles[1].source -Apk $taskFiles[2].source -DescriptorPath $descriptorPath
if ($LASTEXITCODE -ne 0) {
    throw "Packaging aborted: Assert-PackageVersions.ps1 validation failed with exit code $LASTEXITCODE."
}

$taskManifest = [ordered]@{
    schema = 1; sequence = $Sequence; windowsVersion = $WindowsVersion; androidVersionCode = $AndroidVersionCode
    files = @($taskFiles | ForEach-Object { [ordered]@{ name = $_.name; size = (Get-Item -LiteralPath $_.source).Length; sha256 = (Get-FileHash -LiteralPath $_.source -Algorithm SHA256).Hash.ToLowerInvariant() } })
}
$taskManifestBytes = [Text.Encoding]::UTF8.GetBytes(($taskManifest | ConvertTo-Json -Depth 5 -Compress))
$taskRsa = [Security.Cryptography.RSA]::Create()
try {
    $taskBytesRead = 0
    $taskRsa.ImportPkcs8PrivateKey([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $SigningKey)), [ref]$taskBytesRead)
    $taskSignature = $taskRsa.SignData($taskManifestBytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
} finally { $taskRsa.Dispose() }
$taskOutput = [IO.Path]::GetFullPath($Output)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskOutput)) | Out-Null
if (Test-Path -LiteralPath $taskOutput) { throw 'Output already exists. Choose a new release filename.' }
$taskZip = [IO.Compression.ZipFile]::Open($taskOutput, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($taskFile in $taskFiles) { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskZip, $taskFile.source, $taskFile.name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null }
    foreach ($taskEntry in @(@{name='manifest.json';bytes=$taskManifestBytes}, @{name='manifest.sig';bytes=$taskSignature})) {
        $taskStream = $taskZip.CreateEntry($taskEntry.name).Open()
        try { $taskStream.Write($taskEntry.bytes) } finally { $taskStream.Dispose() }
    }
} finally { $taskZip.Dispose() }
Get-FileHash -LiteralPath $taskOutput -Algorithm SHA256
