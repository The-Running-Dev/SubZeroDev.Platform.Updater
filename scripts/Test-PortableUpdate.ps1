[CmdletBinding()]
param([string]$BaselineDirectory = 'artifacts/probe', [string]$OutputDirectory = 'artifacts/portable-smoke', [string]$NuGetConfig)
$ErrorActionPreference = 'Stop'
if (Test-Path $OutputDirectory) { throw 'Use a fresh smoke output directory.' }
$root = [IO.Path]::GetFullPath($OutputDirectory)
$data = Join-Path $root 'preserved data'
$installation = Join-Path $root 'portable with spaces'
New-Item -ItemType Directory -Path $data -Force | Out-Null
Set-Content -LiteralPath (Join-Path $data 'config.json') -Value '{"sentinel":"preserve-config"}'
Set-Content -LiteralPath (Join-Path $data 'diagnostics.log') -Value 'preserve-log'
$baseline = [IO.Path]::GetFullPath($BaselineDirectory)
Expand-Archive -LiteralPath (Get-ChildItem "$baseline/assets/*-Portable.zip").FullName -DestinationPath $installation
# Each version is a real publish so the test proves the relaunched binary changed, not only its package metadata.
$packArguments = @{}
if ($NuGetConfig) { $packArguments.NuGetConfig = $NuGetConfig }
$previousControl = $env:UPDATER_PROBE_CONTROL
try {
    foreach ($version in @('1.0.1', '1.0.2')) {
        $next = Join-Path $root $version
        & "$PSScriptRoot/Pack-Application.ps1" -Project tests/UpdaterProbe/UpdaterProbe.csproj -AppId SubZeroDev.UpdaterProbe -MainExe UpdaterProbe.exe -Version $version -ReleaseNotes README.md -OutputDirectory $next @packArguments
        $control = @{ AppId = 'SubZeroDev.UpdaterProbe'; Repository = 'https://github.com/The-Running-Dev/SubZeroDev.UpdaterProbe.Releases'; Data = $data; Target = $version; Feed = (Join-Path $next 'assets') }
        $env:UPDATER_PROBE_CONTROL = Join-Path $data 'control.json'
        $control | ConvertTo-Json | Set-Content -LiteralPath $env:UPDATER_PROBE_CONTROL
        $process = Start-Process -FilePath (Join-Path $installation 'SubZeroDev.UpdaterProbe.exe') -WindowStyle Hidden -PassThru
        $deadline = [DateTime]::UtcNow.AddSeconds(60)
        do {
            Start-Sleep -Milliseconds 250
            $complete = Join-Path $data 'complete'
            $completedVersion = if (Test-Path $complete) { (Get-Content $complete -Raw).Trim() } else { '' }
        } while ($completedVersion -ne $version -and [DateTime]::UtcNow -lt $deadline)
        if ($completedVersion -ne $version) { throw "Update to $version did not relaunch successfully; inspect $data" }
        if (-not $process.WaitForExit(5000)) { throw 'Old process did not exit.' }
        if ((Get-Content "$data/config.json" -Raw) -notmatch 'preserve-config' -or (Get-Content "$data/diagnostics.log" -Raw) -notmatch 'preserve-log') { throw 'Application data was replaced.' }
        $prefs = Get-Content "$data/updater.json" -Raw | ConvertFrom-Json
        if ($prefs.consentMode -ne 'installAutomatically') { throw 'Consent was not retained.' }
        if ($null -ne $prefs.pendingInstallVersion) { throw "Update to $version was not confirmed by the relaunched process." }
    }
    $runs = @(Get-Content "$data/runs.log")
    $expected = @('1.0.0', '1.0.1', '1.0.1', '1.0.2')
    if ($runs.Count -ne $expected.Count) { throw "Expected one relaunch per update; runs.log has $($runs.Count) entries." }
    for ($i = 0; $i -lt $expected.Count; $i++) {
        $fields = $runs[$i].Split('|')
        if ($fields[0] -ne $expected[$i] -or $fields[1] -ne $expected[$i]) { throw "Run $($i + 1) expected package and binary $($expected[$i]); found $($fields[0]) / $($fields[1])." }
    }
    Write-Output "Portable A -> B -> C passed: $data"
} finally { $env:UPDATER_PROBE_CONTROL = $previousControl }
