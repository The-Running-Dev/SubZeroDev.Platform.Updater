[CmdletBinding()]
param([Parameter(Mandatory)][string]$PrivateKeyPath)
# Creates the publisher's ECDSA P-256 package signing key. Keep the private key outside any repository and outside the
# credentials that can write GitHub releases; embed only the printed public key in the application.
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $PrivateKeyPath) { throw "Refusing to overwrite an existing key: $PrivateKeyPath" }
$key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
try {
    $stream = [IO.File]::Open($PrivateKeyPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $writer = [IO.StreamWriter]::new($stream)
        $writer.Write($key.ExportPkcs8PrivateKeyPem())
        $writer.Dispose()
    } finally { $stream.Dispose() }
    Write-Output $key.ExportSubjectPublicKeyInfoPem()
} finally { $key.Dispose() }
