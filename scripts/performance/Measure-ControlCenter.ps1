param(
    [string]$SourceRoot = (Join-Path $PSScriptRoot '../..'),
    [string]$DotNetPath = 'dotnet',
    [switch]$Verify
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This probe requires a Windows desktop session.' }
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$run = Join-Path $SourceRoot ('outputs/control-center-probe/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$source = [System.Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'ControlCenterProbe.cs'))
$targets = Join-Path $run 'probe.targets'
"<Project><ItemGroup><Compile Include=`"$source`" /></ItemGroup></Project>" | Set-Content -LiteralPath $targets
$project = Join-Path $SourceRoot 'work/phone-deck/windows/PhoneDeck.ControlCenter/PhoneDeck.ControlCenter.csproj'
$publish = Join-Path $run 'instrumented-only'
Push-Location $SourceRoot
try {
    # Isolated intermediates prevent an instrumented entry point from being
    # reused by an ordinary build. These binaries are never deployment packages.
    & $DotNetPath publish $project -c Release -p:RestoreLockedMode=true `
        -p:StartupObject=ControlCenterProbe "-p:CustomAfterMicrosoftCommonTargets=$targets" `
        "-p:IntermediateOutputPath=$run/obj/" "-p:OutputPath=$run/bin/" -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Probe build failed.' }
    $arguments = '"' + $run + '"'
    if ($Verify) { $arguments += ' --verify' }
    $process = Start-Process -FilePath (Join-Path $publish 'PhoneDeck.ControlCenter.exe') `
        -ArgumentList $arguments -WindowStyle Hidden -PassThru
    try {
        if (-not $process.WaitForExit(60000)) {
            $process.Kill()
            throw 'Probe exceeded its 60-second deadline.'
        }
        if ($process.ExitCode -ne 0) { throw "Probe failed; inspect $run/metrics.json" }
    } finally { $process.Dispose() }
    $report = Get-Content -LiteralPath (Join-Path $run 'metrics.json') -Raw | ConvertFrom-Json
    if ($report.error) { throw $report.error }
    if ($Verify -and -not $report.verified) { throw 'Behavior checks did not complete.' }
    Write-Output $run
} finally { Pop-Location }
