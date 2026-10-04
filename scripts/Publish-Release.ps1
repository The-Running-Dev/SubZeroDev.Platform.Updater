[CmdletBinding()]
param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][string]$Version,
      [Parameter(Mandatory)][string]$AssetsDirectory, [string]$PackageSigningKey)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/PackageSignature.ps1"
if ([string]::IsNullOrWhiteSpace($PackageSigningKey)) { throw 'Production publication requires -PackageSigningKey matching the public key pinned in the application.' }
$tag = "v$Version"
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-preview\.[1-9]\d*)?$') { throw 'Invalid release version.' }
$channel = if ($Version.Contains('-preview.')) { 'win-preview' } else { 'win-stable' }
$feed = Get-Content -LiteralPath (Join-Path $AssetsDirectory "releases.$channel.json") -Raw | ConvertFrom-Json
$appId = $feed.Assets[0].PackageId
if ($appId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { throw 'Invalid release application ID.' }
$required = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($name in @("$appId-$channel-Portable.zip", "$appId-$channel-Setup.exe", "$appId-$channel.msi", "releases.$channel.json", 'SHA256SUMS', 'RELEASE-NOTES.md')) {
    [void]$required.Add($name)
}
$packages = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($asset in $feed.Assets) {
    if ($asset.PackageId -cne $appId -or $asset.Version -cne $Version -or $asset.Type -cnotin @('Full', 'Delta')) { throw 'Feed identity/version/type mismatch.' }
    $name = "$($asset.FileName)"
    $suffix = "$($asset.Type)".ToLowerInvariant()
    if ($name -cne "$appId-$Version-$channel-$suffix.nupkg" -or -not $packages.Add($name)) { throw 'Invalid or duplicate feed package name.' }
    [void]$required.Add($name)
    if ($asset.Type -ceq 'Full') { [void]$required.Add("$name.sig") }
    $path = Join-Path $AssetsDirectory $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $asset.Size -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $asset.SHA256) { throw "Feed package mismatch: $name" }
}
$allowed = [Collections.Generic.HashSet[string]]::new($required, [StringComparer]::Ordinal)
# vpk also emits these channel-specific metadata files.
[void]$allowed.Add("assets.$channel.json")
[void]$allowed.Add("RELEASES-$channel")
$files = @(Get-ChildItem -LiteralPath $AssetsDirectory -Force)
foreach ($file in $files) {
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or -not $allowed.Contains($file.Name)) { throw "Unexpected release asset: $($file.Name)" }
}
foreach ($name in $required) {
    if ($files.Name -cnotcontains $name) {
        if ($name.EndsWith('.sig')) { throw "Missing package signature: $name" }
        throw "Missing release asset: $name"
    }
}
$hashNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($line in Get-Content -LiteralPath (Join-Path $AssetsDirectory 'SHA256SUMS')) {
    if ($line -cnotmatch '^([a-fA-F0-9]{64})  ([^\\/:]+)$') { throw 'Invalid SHA256SUMS entry.' }
    $hash = $Matches[1]; $name = $Matches[2]
    if ($name -ceq 'SHA256SUMS' -or $files.Name -cnotcontains $name -or -not $hashNames.Add($name)) { throw "Unexpected or duplicate SHA256SUMS entry: $name" }
    if ((Get-FileHash -LiteralPath (Join-Path $AssetsDirectory $name) -Algorithm SHA256).Hash -ne $hash) { throw "Hash mismatch: $name" }
}
if (-not $hashNames.SetEquals([string[]]@($files.Name | Where-Object { $_ -cne 'SHA256SUMS' }))) { throw 'SHA256SUMS must match the upload set exactly (excluding SHA256SUMS itself).' }
# The public key (PEM, base64, or a file holding either) is the one embedded in the application.
if (Test-Path -LiteralPath $PackageSigningKey -PathType Leaf) { $PackageSigningKey = Get-Content -LiteralPath $PackageSigningKey -Raw }
Assert-PackageSignatures $AssetsDirectory $appId $channel $PackageSigningKey
# Refuse to replace an existing release, including an incomplete draft; inspect it manually.
# Only a definite "not found" allows creation; auth, network and rate-limit failures stop here.
$existing = gh release view $tag --repo $Repository 2>&1
if ($LASTEXITCODE -eq 0) { throw "Release $tag already exists." }
if ("$existing" -notmatch 'release not found') { throw "Could not confirm that $tag is absent: $existing" }
$argsList = @('release', 'create', $tag, '--repo', $Repository, '--draft', '--title', $tag, '--notes-file', (Join-Path $AssetsDirectory 'RELEASE-NOTES.md'))
if ($channel -eq 'win-preview') { $argsList += '--prerelease' }
gh @argsList
if ($LASTEXITCODE) { throw 'Could not create draft release.' }
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
