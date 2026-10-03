using System.Security.Cryptography;
using System.Text;
using Velopack;
using Xunit;

namespace SubZeroDev.Platform.Updater.Tests;

public sealed class PackageSignatureTests
{
    internal static VelopackAsset Asset(string version = "1.1.0", string file = "Example-1.1.0-full.nupkg", string? hash = null, long size = 4) => new() {
        PackageId = "Example", Version = SemanticVersion.Parse(version), Type = VelopackAssetType.Full, FileName = file, Size = size,
        SHA256 = hash ?? Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3, 4 }))
    };
    internal static byte[] Sign(ECDsa key, VelopackAsset asset, string appId = "Example") => Encoding.UTF8.GetBytes(Convert.ToBase64String(
        key.SignData(PackageSignature.Message(appId, asset), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) + "\r\n");

    [Fact] public void SignatureRoundTripsThroughPemAndBase64Keys()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = Sign(signer, Asset());
        foreach (var text in new[] { signer.ExportSubjectPublicKeyInfoPem(), Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()) }) {
            using var key = PackageSignature.ImportPublicKey(text);
            Assert.True(PackageSignature.Verify(key, "Example", Asset(), signature));
        }
    }

    [Theory]
    [InlineData("OtherApp", "1.1.0", "Example-1.1.0-full.nupkg", null, 4L)]
    [InlineData("Example", "1.2.0", "Example-1.1.0-full.nupkg", null, 4L)]
    [InlineData("Example", "1.1.0", "Example-1.2.0-full.nupkg", null, 4L)]
    [InlineData("Example", "1.1.0", "Example-1.1.0-full.nupkg", "AAAA", 4L)]
    [InlineData("Example", "1.1.0", "Example-1.1.0-full.nupkg", null, 5L)]
    public void SignatureDoesNotCoverAnyOtherPackage(string appId, string version, string file, string? hash, long size)
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var key = PackageSignature.ImportPublicKey(signer.ExportSubjectPublicKeyInfoPem());
        Assert.False(PackageSignature.Verify(key, appId, Asset(version, file, hash, size), Sign(signer, Asset())));
    }

    [Fact] public void OtherKeysAndMalformedSignaturesAreRejected()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var key = PackageSignature.ImportPublicKey(signer.ExportSubjectPublicKeyInfoPem());
        Assert.False(PackageSignature.Verify(key, "Example", Asset(), Sign(attacker, Asset())));
        Assert.False(PackageSignature.Verify(key, "Example", Asset(), Encoding.UTF8.GetBytes("not base64!")));
        Assert.False(PackageSignature.Verify(key, "Example", Asset(), Encoding.UTF8.GetBytes(Convert.ToBase64String(new byte[10]))));
        var delta = Asset();
        delta.Type = VelopackAssetType.Delta;
        Assert.False(PackageSignature.Verify(key, "Example", delta, Sign(signer, delta)));
    }

    [Fact] public void OnlyP256PublicKeysAreAccepted()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var rsa = RSA.Create();
        foreach (var text in new[] { "", "not a key", p384.ExportSubjectPublicKeyInfoPem(), rsa.ExportSubjectPublicKeyInfoPem() })
            Assert.Throws<ArgumentException>(() => new UpdaterOptions("Example", new("https://github.com/example/app"), "unused") { PackageSigningKey = text });
        Assert.Null(new UpdaterOptions("Example", new("https://github.com/example/app"), "unused").PackageSigningKey);
    }
}
