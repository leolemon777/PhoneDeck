# Requires PowerShell 7. This rehearsal helper never writes to physical phones.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Adb,
    [Parameter(Mandatory)][ValidatePattern('^emulator-[0-9]+$')][string]$Serial,
    [Parameter(Mandatory)][string]$SourceDirectory
)
$ErrorActionPreference = 'Stop'
function Invoke-Adb([string[]]$Arguments, [AllowNull()][object]$InputLine = $null) {
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Adb
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.RedirectStandardInput = ($null -ne $InputLine)
    if ($null -ne $InputLine) { $info.StandardInputEncoding = [System.Text.UTF8Encoding]::new($false) }
    foreach ($value in (@('-s', $Serial) + $Arguments)) { $info.ArgumentList.Add($value) }
    $process = [System.Diagnostics.Process]::Start($info)
    $outputTask = $process.StandardOutput.ReadToEndAsync()
    $errorTask = $process.StandardError.ReadToEndAsync()
    if ($null -ne $InputLine) {
        $inputTask = $process.StandardInput.WriteLineAsync($InputLine)
        if (-not $inputTask.Wait(30000)) { $process.Kill(); throw 'ADB input timed out' }
        $process.StandardInput.Close()
    }
    if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'ADB operation timed out' }
    $output = $outputTask.GetAwaiter().GetResult()
    $discardedError = $errorTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) { throw 'ADB settings rehearsal failed; raw output suppressed' }
    return $output.Trim()
}
if ((Invoke-Adb @('shell','getprop','ro.kernel.qemu')) -ne '1') { throw 'Not an Android emulator' }
$package = 'com.codex.phonedeck'
$null = Invoke-Adb @('shell','am','force-stop',$package)
$null = Invoke-Adb @('shell','run-as',$package,'mkdir','-p','files','shared_prefs')
$reports = @()
foreach ($relative in @('files/shortcut-config.json','files/rList','shared_prefs/PhoneDeckSettings.xml')) {
    $source = Join-Path $SourceDirectory $relative
    $bytes = [System.IO.File]::ReadAllBytes($source)
    $hash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    $temporary = 'files/.settings-migration-' + [Guid]::NewGuid().ToString('N')
    try {
        $command = "run-as $package sh -c 'base64 -d > $temporary'"
        $null = Invoke-Adb @('shell', '-T', '-e', 'none', $command) ([Convert]::ToBase64String($bytes))
        $stagedHash = (Invoke-Adb @('shell','run-as',$package,'sha256sum',$temporary)).Split(' ')[0]
        if ($stagedHash -ne $hash) { throw 'Transferred settings hash mismatch' }
        $null = Invoke-Adb @('shell','run-as',$package,'chmod','600',$temporary)
        # mv -n refuses replacement. A final hash check also rejects a preexisting different file.
        $null = Invoke-Adb @('shell','run-as',$package,'mv','-n',$temporary,$relative)
        $restoredHash = (Invoke-Adb @('shell','run-as',$package,'sha256sum',$relative)).Split(' ')[0]
        if ($restoredHash -ne $hash) { throw 'Existing destination differs; no overwrite performed' }
        $reports += [pscustomobject]@{ File=$relative; Bytes=$bytes.Length; HashMatched=$true }
    } finally {
        $null = Invoke-Adb @('shell','run-as',$package,'rm','-f',$temporary)
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}
$reports | ConvertTo-Json
