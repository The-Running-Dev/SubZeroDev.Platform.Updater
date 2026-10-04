[CmdletBinding()]
param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][string]$Version,
      [Parameter(Mandatory)][string]$AssetsDirectory, [string]$PackageSigningKey)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/PackageSignature.ps1"
if ([string]::IsNullOrWhiteSpace($PackageSigningKey)) { throw 'Production publication requires -PackageSigningKey matching the public key pinned in the application.' }
$tag = "v$Version"
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-preview\.[1-9]\d*)?$') { throw 'Invalid release version.' }
$channel = if ($Version.Contains('-preview.')) { 'win-preview' } else { 'win-stable' }
foreach ($pattern in @('*-Portable.zip', '*-Setup.exe', '*.msi', '*-full.nupkg', "releases.$channel.json", 'SHA256SUMS', 'RELEASE-NOTES.md')) {
    if (-not (Get-ChildItem -LiteralPath $AssetsDirectory -File | Where-Object Name -like $pattern)) { throw "Missing $pattern" }
}
foreach ($line in Get-Content -LiteralPath (Join-Path $AssetsDirectory 'SHA256SUMS')) {
    $hash, $name = $line -split '  ', 2
    if ((Get-FileHash -LiteralPath (Join-Path $AssetsDirectory $name)).Hash -ne $hash) { throw "Hash mismatch: $name" }
}
# The public key (PEM, base64, or a file holding either) is the one embedded in the application.
if ($PackageSigningKey) {
    if (Test-Path -LiteralPath $PackageSigningKey -PathType Leaf) { $PackageSigningKey = Get-Content -LiteralPath $PackageSigningKey -Raw }
    $appId = (Get-Content -LiteralPath (Join-Path $AssetsDirectory "releases.$channel.json") -Raw | ConvertFrom-Json).Assets[0].PackageId
    Assert-PackageSignatures $AssetsDirectory $appId $channel $PackageSigningKey
}
# Refuse to replace an existing release, including an incomplete draft; inspect it manually.
# Only a definite "not found" allows creation; auth, network and rate-limit failures stop here.
$existing = gh release view $tag --repo $Repository 2>&1
if ($LASTEXITCODE -eq 0) { throw "Release $tag already exists." }
if ("$existing" -notmatch 'release not found') { throw "Could not confirm that $tag is absent: $existing" }
$argsList = @('release', 'create', $tag, '--repo', $Repository, '--draft', '--title', $tag, '--notes-file', (Join-Path $AssetsDirectory 'RELEASE-NOTES.md'))
if ($channel -eq 'win-preview') { $argsList += '--prerelease' }
gh @argsList
if ($LASTEXITCODE) { throw 'Could not create draft release.' }
$files = @(Get-ChildItem -LiteralPath $AssetsDirectory -File)
gh release upload $tag --repo $Repository @($files.FullName)
if ($LASTEXITCODE) { throw 'Upload failed; release remains a draft.' }
# Compare remote content, not just names and sizes. Drafts are only reachable by release ID.
$id = gh release view $tag --repo $Repository --json databaseId --jq .databaseId
if ($LASTEXITCODE -or -not $id) { throw 'Could not read the draft release. Release remains a draft.' }
$remote = gh api "repos/$Repository/releases/$id" | ConvertFrom-Json
if ($LASTEXITCODE) { throw 'Could not read uploaded assets. Release remains a draft.' }
$verify = Join-Path ([IO.Path]::GetTempPath()) "release-verify-$([Guid]::NewGuid())"
try {
    foreach ($file in $files) {
        $asset = @($remote.assets | Where-Object name -eq $file.Name)
        if ($asset.Count -ne 1 -or $asset[0].size -ne $file.Length) { throw "Uploaded asset mismatch: $($file.Name). Release remains a draft." }
        $expected = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($asset[0].digest) {
            $actual = "$($asset[0].digest)"
        } else {
            # Older API responses omit digests; hash the downloaded asset instead.
            gh release download $tag --repo $Repository --pattern $file.Name --dir $verify --clobber
            if ($LASTEXITCODE) { throw "Could not download $($file.Name) for verification. Release remains a draft." }
            $actual = 'sha256:' + (Get-FileHash -LiteralPath (Join-Path $verify $file.Name) -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        if ($actual -ne "sha256:$expected") { throw "Uploaded asset content mismatch: $($file.Name). Release remains a draft." }
    }
} finally { if (Test-Path $verify) { Remove-Item -LiteralPath $verify -Recurse -Force } }
gh release edit $tag --repo $Repository --draft=false
if ($LASTEXITCODE) { throw 'Publishing failed; inspect the draft.' }
