$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/PackageSignature.ps1"
# A mock prevents any external write even if a validation gate regresses.
$script:githubCalls = 0
function gh { $script:githubCalls++; throw 'github-command-reached' }
function Expect-Failure([scriptblock]$Action, [string]$Message) {
    $before = $script:githubCalls
    try { & $Action } catch {
        if ($Message -ne 'github-command-reached' -and $script:githubCalls -ne $before) { throw 'Validation invoked GitHub before rejecting the release.' }
        if ($_.Exception.Message -like "*$Message*") { return }
        throw
    }
    throw "Expected failure: $Message"
}
$directory = Join-Path ([IO.Path]::GetTempPath()) "updater-publishing-test-$([Guid]::NewGuid())"
$testSigner = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
$testOtherSigner = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
try {
    $publish = @{ Repository = 'example/test'; Version = '1.0.0'; AssetsDirectory = $directory }
    Expect-Failure { & "$PSScriptRoot/Publish-Release.ps1" @publish } 'requires -PackageSigningKey'
    New-Item -ItemType Directory -Path $directory | Out-Null
    foreach ($name in @('Example-win-stable-Portable.zip', 'Example-win-stable-Setup.exe', 'Example-win-stable.msi', 'Example-1.0.0-win-stable-full.nupkg', 'RELEASE-NOTES.md')) {
        [IO.File]::WriteAllText((Join-Path $directory $name), 'test')
    }
    $feed = @{ Assets = @(@{ PackageId = 'Example'; Version = '1.0.0'; Type = 'Full'; FileName = 'Example-1.0.0-win-stable-full.nupkg'; Size = 4; SHA256 = (Get-FileHash (Join-Path $directory 'Example-1.0.0-win-stable-full.nupkg')).Hash }) }
    $feed | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $directory 'releases.win-stable.json')
    function Write-Hashes {
        $lines = Get-ChildItem -LiteralPath $directory -File | Where-Object Name -ne 'SHA256SUMS' | ForEach-Object { "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName).Hash, $_.Name }
        [IO.File]::WriteAllLines((Join-Path $directory 'SHA256SUMS'), [string[]]$lines)
    }
    Write-Hashes
    $publish.PackageSigningKey = [Convert]::ToBase64String($testSigner.ExportSubjectPublicKeyInfo())
    Expect-Failure { & "$PSScriptRoot/Publish-Release.ps1" @publish } 'Missing package signature'
    New-PackageSignatures $directory 'Example' 'win-stable' $testOtherSigner.ExportECPrivateKeyPem()
    Write-Hashes
    Expect-Failure { & "$PSScriptRoot/Publish-Release.ps1" @publish } 'does not match the trusted key'
    New-PackageSignatures $directory 'Example' 'win-stable' $testSigner.ExportECPrivateKeyPem()
    Write-Hashes
    Expect-Failure { & "$PSScriptRoot/Publish-Release.ps1" @publish } 'github-command-reached'
    $publish.PackageSigningKey = $testSigner.ExportSubjectPublicKeyInfoPem()
    Expect-Failure { & "$PSScriptRoot/Publish-Release.ps1" @publish } 'github-command-reached'
    foreach ($private in @($testSigner.ExportECPrivateKeyPem(), $testSigner.ExportPkcs8PrivateKeyPem(),
        $testSigner.ExportEncryptedPkcs8PrivateKeyPem('test', [Security.Cryptography.PbeParameters]::new([Security.Cryptography.PbeEncryptionAlgorithm]::Aes256Cbc, [Security.Cryptography.HashAlgorithmName]::SHA256, 1000)))) {
        $publish.PackageSigningKey = $private
        Expect-Failure { & "$PSScriptRoot/Publish-Release.ps1" @publish } 'Only PUBLIC KEY PEM'
    }
    $publish.PackageSigningKey = [Convert]::ToBase64String($testSigner.ExportSubjectPublicKeyInfo())
    $manifest = Join-Path $directory 'SHA256SUMS'
    $validHashes = [IO.File]::ReadAllLines($manifest)
    foreach ($unexpected in @('private-key.pem', 'Example-0.9.0-win-stable-full.nupkg', 'releases.win-preview.json', '.hidden-secret')) {
        $path = Join-Path $directory $unexpected
        [IO.File]::WriteAllText($path, 'unexpected')
        Write-Hashes # Even a correctly hashed extra file is rejected.
        Expect-Failure { & "$PSScriptRoot/Publish-Release.ps1" @publish } 'Unexpected release asset'
        Remove-Item -LiteralPath $path -Force
    }
    [IO.File]::WriteAllLines($manifest, [string[]]$validHashes[1..($validHashes.Count - 1)])
    Expect-Failure { & "$PSScriptRoot/Publish-Release.ps1" @publish } 'must match the upload set exactly'
    [IO.File]::WriteAllLines($manifest, [string[]]@($validHashes + $validHashes[0]))
    Expect-Failure { & "$PSScriptRoot/Publish-Release.ps1" @publish } 'duplicate SHA256SUMS entry'
    foreach ($entry in @('malformed', ('0' * 64 + '  ../outside'), ('0' * 64 + '  missing.zip'), ('0' * 64 + '  SHA256SUMS'))) {
        [IO.File]::WriteAllLines($manifest, [string[]]@($validHashes + $entry))
        Expect-Failure { & "$PSScriptRoot/Publish-Release.ps1" @publish } 'SHA256SUMS entry'
    }
    [IO.File]::WriteAllLines($manifest, $validHashes)
    [IO.File]::WriteAllText((Join-Path $directory 'RELEASE-NOTES.md'), 'tampered')
    Expect-Failure { & "$PSScriptRoot/Publish-Release.ps1" @publish } 'Hash mismatch'
    [IO.File]::WriteAllText((Join-Path $directory 'RELEASE-NOTES.md'), 'test')
    foreach ($name in @('assets.win-stable.json', 'RELEASES-win-stable')) { [IO.File]::WriteAllText((Join-Path $directory $name), 'metadata') }
    Write-Hashes
    Expect-Failure { & "$PSScriptRoot/Publish-Release.ps1" @publish } 'github-command-reached'
    Write-Output 'Publishing guards passed: public formats, private key rejection, signatures, exact asset sets and manifests; invalid inputs made no GitHub calls.'
} finally {
    if ($testSigner) { $testSigner.Dispose() }; if ($testOtherSigner) { $testOtherSigner.Dispose() }
    if (Test-Path -LiteralPath $directory) {
        Get-ChildItem -LiteralPath $directory -File | Remove-Item -Force
        Remove-Item -LiteralPath $directory
    }
}



