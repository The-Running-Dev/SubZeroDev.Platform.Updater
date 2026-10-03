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
. "$PSScriptRoot/PackageSignature.ps1"
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-preview\.[1-9]\d*)?$') { throw 'Version must be X.Y.Z or X.Y.Z-preview.N.' }
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
# Prefer a mechanism that keeps secrets off the command line: Azure Trusted Signing reads a metadata file and
# authenticates through the environment, and a custom command receives only a template.
if ($env:VELOPACK_AZURE_SIGN_FILE) { $arguments += @('--azureTrustedSignFile', [IO.Path]::GetFullPath($env:VELOPACK_AZURE_SIGN_FILE)) }
elseif ($env:VELOPACK_SIGN_TEMPLATE) { $arguments += @('--signTemplate', $env:VELOPACK_SIGN_TEMPLATE) }
elseif ($env:VELOPACK_SIGN_PARAMS) {
    Write-Warning 'VELOPACK_SIGN_PARAMS puts signing arguments, possibly including a certificate password, on the signtool command line where other local processes can read them. Prefer VELOPACK_AZURE_SIGN_FILE or VELOPACK_SIGN_TEMPLATE.'
    $arguments += @('--signParams', $env:VELOPACK_SIGN_PARAMS)
}
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
# Publisher signatures let clients with a pinned PackageSigningKey reject packages from anyone else who can write releases.
if ($env:UPDATER_PACKAGE_SIGNING_KEY) {
    New-PackageSignatures $assets $AppId $channel $env:UPDATER_PACKAGE_SIGNING_KEY
    $files = @(Get-ChildItem -LiteralPath $assets -File | Where-Object Name -ne 'SHA256SUMS')
} else { Write-Warning 'UPDATER_PACKAGE_SIGNING_KEY is not set; clients that pin a package signing key will reject this release.' }
$hashes = $files | Sort-Object Name | ForEach-Object { "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant(), $_.Name }
[IO.File]::WriteAllLines((Join-Path $assets 'SHA256SUMS'), $hashes)
Write-Output "Validated $AppId $Version ($channel): $assets"
