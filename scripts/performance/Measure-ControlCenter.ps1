param(
    [Parameter(Mandatory)][string]$InstallDirectory,
    [ValidateRange(2, 60)][int]$SampleCount = 12,
    [ValidateRange(100, 500)][int]$IntervalMilliseconds = 500,
    [string]$OutputPath
)
# Read-only sampler for the native receiver. No GC, working-set trimming,
# process restart, or real audio/input is performed.
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This sampler requires Windows.' }
$directory = (Resolve-Path -LiteralPath $InstallDirectory).Path
$samples = @()
for ($index = 0; $index -lt $SampleCount; $index++) {
    foreach ($name in @('PhoneDeck.ControlCenter', 'PhoneDeck.Server')) {
        $expected = Join-Path $directory ($name + '.exe')
        foreach ($process in (Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            try {
                if ($process.Path -ne $expected) { continue }
                $process.Refresh()
                $samples += [pscustomobject]@{
                    at = [DateTimeOffset]::UtcNow.ToString('O'); name = $name; pid = $process.Id
                    privateCommitMiB = [math]::Round($process.PrivateMemorySize64 / 1MB, 2)
                    workingSetMiB = [math]::Round($process.WorkingSet64 / 1MB, 2)
                    cpuMilliseconds = $process.TotalProcessorTime.TotalMilliseconds
                    handles = $process.HandleCount; windowVisible = $process.MainWindowHandle -ne 0
                }
            } finally { $process.Dispose() }
        }
    }
    if ($index -lt $SampleCount - 1) { Start-Sleep -Milliseconds $IntervalMilliseconds }
}
if ($samples.Count -eq 0) { throw 'No PhoneDeck processes found in this install directory.' }
$report = [ordered]@{
    note = 'Private commit and working set are distinct metrics. Compare equal workloads and warm-up periods.'
    samples = $samples
}
$json = $report | ConvertTo-Json -Depth 5
if ($OutputPath) { $json | Set-Content -LiteralPath $OutputPath -Encoding utf8 }
$json
