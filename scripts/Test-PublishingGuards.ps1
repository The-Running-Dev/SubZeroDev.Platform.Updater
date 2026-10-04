$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/PackageSignature.ps1"
# A mock prevents any external write even if a validation gate regresses.
function gh { throw 'github-command-reached' }
function Expect-Failure([scriptblock]$Action, [string]$Message) {
    try { & $Action } catch {
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
    foreach ($name in @('Example-Portable.zip', 'Example-Setup.exe', 'Example.msi', 'Example-full.nupkg', 'RELEASE-NOTES.md')) {
        [IO.File]::WriteAllText((Join-Path $directory $name), 'test')
    }
    $feed = @{ Assets = @(@{ PackageId = 'Example'; Version = '1.0.0'; Type = 'Full'; FileName = 'Example-full.nupkg'; Size = 4; SHA256 = (Get-FileHash (Join-Path $directory 'Example-full.nupkg')).Hash }) }
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
    Write-Output 'Publishing guards passed: missing key, unsigned, wrong signer, and valid signer.'
} finally {
    if ($testSigner) { $testSigner.Dispose() }; if ($testOtherSigner) { $testOtherSigner.Dispose() }
    if (Test-Path -LiteralPath $directory) {
        Get-ChildItem -LiteralPath $directory -File | Remove-Item -Force
        Remove-Item -LiteralPath $directory
    }
}



