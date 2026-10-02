# Dot-source this file. Signatures must match src/SubZeroDev.Platform.Updater/PackageSignature.cs; change both together.
using namespace System.Security.Cryptography

function Get-PackageSignatureMessage([string]$AppId, $Asset) {
    [Text.Encoding]::UTF8.GetBytes("SubZeroDev.Platform.Updater package signature v1`n$AppId`n$($Asset.Version)`n$($Asset.FileName)`n$("$($Asset.SHA256)".ToUpperInvariant())`n$($Asset.Size)`n")
}

function Assert-P256([ECDsa]$Key, [string]$Kind) {
    if ($Key.ExportParameters($false).Curve.Oid.Value -ne '1.2.840.10045.3.1.7') { throw "The package signing key must be an ECDSA P-256 $Kind key." }
}

function Get-FullPackageAssets([string]$AssetsDirectory, [string]$Channel) {
    $feed = Get-Content -LiteralPath (Join-Path $AssetsDirectory "releases.$Channel.json") -Raw | ConvertFrom-Json
    $full = @($feed.Assets | Where-Object Type -eq 'Full')
    if (-not $full) { throw "releases.$Channel.json has no full package." }
    $full
}

# Writes {package}.sig next to each full package. The private key is PEM text and is never logged.
function New-PackageSignatures([string]$AssetsDirectory, [string]$AppId, [string]$Channel, [string]$PrivateKeyPem) {
    $key = [ECDsa]::Create()
    try {
        $key.ImportFromPem($PrivateKeyPem)
        Assert-P256 $key 'private'
        foreach ($asset in Get-FullPackageAssets $AssetsDirectory $Channel) {
            $signature = $key.SignData((Get-PackageSignatureMessage $AppId $asset), [HashAlgorithmName]::SHA256, [DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
            [IO.File]::WriteAllText((Join-Path $AssetsDirectory "$($asset.FileName).sig"), [Convert]::ToBase64String($signature))
        }
    } finally { $key.Dispose() }
}

# Throws unless every full package has a signature from the given public key (PEM or base64 SubjectPublicKeyInfo).
function Assert-PackageSignatures([string]$AssetsDirectory, [string]$AppId, [string]$Channel, [string]$PublicKey) {
    $key = [ECDsa]::Create()
    try {
        if ($PublicKey.Contains('-----BEGIN')) { $key.ImportFromPem($PublicKey) } else { $read = 0; $key.ImportSubjectPublicKeyInfo([Convert]::FromBase64String($PublicKey.Trim()), [ref]$read) }
        Assert-P256 $key 'public'
        foreach ($asset in Get-FullPackageAssets $AssetsDirectory $Channel) {
            $path = Join-Path $AssetsDirectory "$($asset.FileName).sig"
            if (-not (Test-Path -LiteralPath $path)) { throw "Missing package signature: $($asset.FileName).sig" }
            $signature = [Convert]::FromBase64String((Get-Content -LiteralPath $path -Raw).Trim())
            if (-not $key.VerifyData((Get-PackageSignatureMessage $AppId $asset), $signature, [HashAlgorithmName]::SHA256, [DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) {
                throw "Package signature does not match the trusted key: $($asset.FileName)"
            }
        }
    } finally { $key.Dispose() }
}
