[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$AppId,
    [Parameter(Mandatory)][string]$MainExe,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$ReleaseNotes,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$PublishDirectory,
    [string]$NuGetConfig,
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(-preview\.[1-9]\d*)?$') { throw 'Version must be X.Y.Z or X.Y.Z-preview.N.' }
$channel = if ($Version.Contains('-preview.')) { 'win-preview' } else { 'win-stable' }
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $OutputDirectory 'application' }
if (Test-Path $OutputDirectory) { throw "Use a new, empty output directory: $OutputDirectory" }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
if (-not $SkipPublish) {
    [string[]]$restoreArguments = @()
    if ($NuGetConfig) { $restoreArguments += "-p:RestoreConfigFile=$([IO.Path]::GetFullPath($NuGetConfig))" }
    dotnet publish $Project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false "-p:Version=$Version" -o $PublishDirectory @restoreArguments
    if ($LASTEXITCODE) { throw 'Application publish failed.' }
}
dotnet tool restore
if ($LASTEXITCODE) { throw 'Pinned vpk restore failed.' }
$assets = Join-Path $OutputDirectory 'assets'
$arguments = @('tool', 'run', 'vpk', '--', 'pack', '--packId', $AppId, '--packVersion', $Version,
    '--packDir', $PublishDirectory, '--mainExe', $MainExe, '--runtime', 'win-x64', '--channel', $channel,
    '--outputDir', $assets, '--releaseNotes', $ReleaseNotes, '--packAuthors', 'SubZeroDev', '--msi', '--instLocation', 'PerUser')
# Optional signing is performed by vpk for binaries and installers. Never log secret contents.
if ($env:VELOPACK_SIGN_PARAMS) { $arguments += @('--signParams', $env:VELOPACK_SIGN_PARAMS) }
& dotnet @arguments
if ($LASTEXITCODE) { throw 'Velopack packaging failed.' }
Copy-Item -LiteralPath $ReleaseNotes -Destination (Join-Path $assets 'RELEASE-NOTES.md')
$files = @(Get-ChildItem -LiteralPath $assets -File | Where-Object Name -ne 'SHA256SUMS')
$required = @('*-Portable.zip', '*-Setup.exe', '*.msi', '*-full.nupkg', "releases.$channel.json")
foreach ($pattern in $required) { if (-not ($files.Name -like $pattern)) { throw "Missing required asset: $pattern" } }
$feed = Get-Content -LiteralPath (Join-Path $assets "releases.$channel.json") -Raw | ConvertFrom-Json
foreach ($asset in $feed.Assets) {
    if ($asset.PackageId -ne $AppId -or $asset.Version -ne $Version) { throw 'Feed identity/version mismatch.' }
    $file = Join-Path $assets $asset.FileName
    if (-not (Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file).Hash -ne $asset.SHA256) { throw 'Feed hash mismatch.' }
}
$hashes = $files | Sort-Object Name | ForEach-Object { "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant(), $_.Name }
[IO.File]::WriteAllLines((Join-Path $assets 'SHA256SUMS'), $hashes)
Write-Output "Validated $AppId $Version ($channel): $assets"
