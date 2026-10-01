[CmdletBinding()]
param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][string]$Version,
      [Parameter(Mandatory)][string]$AssetsDirectory)
$ErrorActionPreference = 'Stop'
$tag = "v$Version"
if ($Version -notmatch '^\d+\.\d+\.\d+(-preview\.[1-9]\d*)?$') { throw 'Invalid release version.' }
$channel = if ($Version.Contains('-preview.')) { 'win-preview' } else { 'win-stable' }
foreach ($pattern in @('*-Portable.zip', '*-Setup.exe', '*.msi', '*-full.nupkg', "releases.$channel.json", 'SHA256SUMS', 'RELEASE-NOTES.md')) {
    if (-not (Get-ChildItem -LiteralPath $AssetsDirectory -File | Where-Object Name -like $pattern)) { throw "Missing $pattern" }
}
foreach ($line in Get-Content -LiteralPath (Join-Path $AssetsDirectory 'SHA256SUMS')) {
    $hash, $name = $line -split '  ', 2
    if ((Get-FileHash -LiteralPath (Join-Path $AssetsDirectory $name)).Hash -ne $hash) { throw "Hash mismatch: $name" }
}
# Refuse to replace an existing release, including an incomplete draft; inspect it manually.
gh release view $tag --repo $Repository *> $null
if ($LASTEXITCODE -eq 0) { throw "Release $tag already exists." }
$argsList = @('release', 'create', $tag, '--repo', $Repository, '--draft', '--title', "$Repository $tag", '--notes-file', (Join-Path $AssetsDirectory 'RELEASE-NOTES.md'))
if ($channel -eq 'win-preview') { $argsList += '--prerelease' }
gh @argsList
if ($LASTEXITCODE) { throw 'Could not create draft release.' }
$files = @(Get-ChildItem -LiteralPath $AssetsDirectory -File)
gh release upload $tag --repo $Repository @($files.FullName)
if ($LASTEXITCODE) { throw 'Upload failed; release remains a draft.' }
$remote = gh release view $tag --repo $Repository --json assets | ConvertFrom-Json
foreach ($file in $files) {
    $asset = @($remote.assets | Where-Object name -eq $file.Name)
    if ($asset.Count -ne 1 -or $asset[0].size -ne $file.Length) { throw "Uploaded asset mismatch: $($file.Name). Release remains a draft." }
}
gh release edit $tag --repo $Repository --draft=false
if ($LASTEXITCODE) { throw 'Publishing failed; inspect the draft.' }
