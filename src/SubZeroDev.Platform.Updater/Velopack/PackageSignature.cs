using System.Security.Cryptography;
using System.Text;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace SubZeroDev.Platform.Updater;

/// <summary>
/// Publisher signatures over a full package's identity and SHA-256. The key is pinned in the application, so a party
/// that can only write GitHub releases cannot produce a package that clients accept.
/// </summary>
internal static class PackageSignature
{
    /// <summary>The release asset next to each full package that holds its base64 signature.</summary>
    internal const string Extension = ".sig";

    // scripts/PackageSignature.ps1 builds the same message; change both together.
    internal static byte[] Message(string appId, VelopackAsset asset) => Encoding.UTF8.GetBytes(
        $"SubZeroDev.Platform.Updater package signature v1\n{appId}\n{asset.Version}\n{asset.FileName}\n{asset.SHA256?.ToUpperInvariant()}\n{asset.Size}\n");

    internal static ECDsa ImportPublicKey(string key)
    {
        var ecdsa = ECDsa.Create();
        try {
            var encoded = key.Trim();
            if (encoded.Contains("-----BEGIN", StringComparison.Ordinal)) {
                const string begin = "-----BEGIN PUBLIC KEY-----", end = "-----END PUBLIC KEY-----";
                if (!encoded.StartsWith(begin, StringComparison.Ordinal) || !encoded.EndsWith(end, StringComparison.Ordinal))
                    throw new ArgumentException("Only PUBLIC KEY PEM is accepted; private keys must not be configured.", nameof(key));
                encoded = encoded[begin.Length..^end.Length];
            }
            var bytes = Convert.FromBase64String(encoded);
            ecdsa.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read != bytes.Length) throw new ArgumentException("The public key contains trailing data.", nameof(key));
            var parameters = ecdsa.ExportParameters(includePrivateParameters: false);
            if (parameters.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                throw new ArgumentException("The package signing key must be an ECDSA P-256 public key.", nameof(key));
            return ecdsa;
        } catch (Exception ex) when (ex is FormatException or CryptographicException) {
            ecdsa.Dispose();
            throw new ArgumentException("The package signing key must be an ECDSA P-256 public key in PEM or base64 SubjectPublicKeyInfo form.", nameof(key), ex);
        } catch {
            ecdsa.Dispose();
            throw;
        }
    }

    internal static string Validate(string key)
    {
        using var _ = ImportPublicKey(key);
        return key;
    }

    internal static bool Verify(ECDsa key, string appId, VelopackAsset asset, byte[] signatureFile)
    {
        byte[] signature;
        try { signature = Convert.FromBase64String(Encoding.UTF8.GetString(signatureFile).Trim().TrimStart('﻿')); }
        catch (FormatException) { return false; }
        if (asset.Type != VelopackAssetType.Full || string.IsNullOrEmpty(asset.SHA256)) return false;
        return key.VerifyData(Message(appId, asset), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}

/// <summary>A source that can supply the publisher signature for a full package it listed.</summary>
internal interface IPackageSignatureSource
{
    Task<byte[]?> GetSignatureAsync(VelopackAsset asset);
}

/// <summary>A local directory feed whose signatures sit next to the packages; used by test and validation builds.</summary>
internal sealed class SignedFileSource(DirectoryInfo directory) : IUpdateSource, IPackageSignatureSource
{
    private readonly SimpleFileSource inner = new(directory);

    public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        => inner.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease);

    public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
        => inner.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken);

    public async Task<byte[]?> GetSignatureAsync(VelopackAsset asset)
    {
        var path = Path.Combine(directory.FullName, Path.GetFileName(asset.FileName) + PackageSignature.Extension);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path).ConfigureAwait(false) : null;
    }
}
