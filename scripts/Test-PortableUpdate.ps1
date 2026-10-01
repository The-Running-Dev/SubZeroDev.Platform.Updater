[CmdletBinding()]
param([string]$BaselineDirectory = 'artifacts/probe', [string]$OutputDirectory = 'artifacts/portable-smoke')
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
$previousControl = $env:UPDATER_PROBE_CONTROL
try {
    foreach ($version in @('1.0.1', '1.0.2')) {
        $next = Join-Path $root $version
        & "$PSScriptRoot/Pack-Application.ps1" -Project tests/UpdaterProbe/UpdaterProbe.csproj -AppId SubZeroDev.UpdaterProbe -MainExe UpdaterProbe.exe -Version $version -ReleaseNotes README.md -OutputDirectory $next -PublishDirectory "$baseline/application" -SkipPublish
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
    }
    $runs = @(Get-Content "$data/runs.log")
    if ($runs.Count -ne 4 -or $runs[0] -notlike '1.0.0|*' -or $runs[1] -notlike '1.0.1|*' -or $runs[2] -notlike '1.0.1|*' -or $runs[3] -notlike '1.0.2|*') { throw 'Expected one relaunch per update.' }
    Write-Output "Portable A -> B -> C passed: $data"
} finally { $env:UPDATER_PROBE_CONTROL = $previousControl }
