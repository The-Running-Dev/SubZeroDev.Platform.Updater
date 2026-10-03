using System.Security.Cryptography;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;
using Xunit;

namespace SubZeroDev.Platform.Updater.Tests;

public sealed class EngineTests
{
    private sealed class Source(params string[] versions) : IUpdateSource
    {
        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
            => Task.FromResult(new VelopackAssetFeed { Assets = versions.Select(v => new VelopackAsset {
                PackageId = "Example", Version = SemanticVersion.Parse(v), Type = VelopackAssetType.Full,
                FileName = $"Example-{v}-full.nupkg", Size = 4, SHA256 = Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3, 4 }))
            }).ToArray() });
        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
            => File.WriteAllBytesAsync(localFile, new byte[] { 4, 3, 2, 1 }, cancelToken);
    }

    [Theory]
    [InlineData("1.0.0", UpdateChannel.Stable, "1.1.0")]
    [InlineData("1.0.0", UpdateChannel.Preview, "1.2.0-preview.2")]
    [InlineData("1.2.0-preview.3", UpdateChannel.Stable, null)]
    [InlineData("1.2.0-preview.3", UpdateChannel.Preview, null)]
    public async Task SelectsHighestCompatibleWithoutDowngrade(string current, UpdateChannel channel, string? expected)
    {
        using var engine = new VelopackEngine(new("Example", new("https://github.com/example/app"), "unused"),
            new TestVelopackLocator("Example", current, Path.GetTempPath()),
            stream => stream == UpdateChannel.Stable ? new Source("1.1.0") : new Source("1.2.0-preview.1", "1.2.0-preview.2"));
        Assert.Equal(expected, (await engine.CheckAsync(channel, default))?.TargetVersion);
    }

    [Fact] public async Task PreviewCanAdvanceToHigherStable()
    {
        using var engine = new VelopackEngine(new("Example", new("https://github.com/example/app"), "unused"),
            new TestVelopackLocator("Example", "1.2.0-preview.3", Path.GetTempPath()),
            stream => stream == UpdateChannel.Stable ? new Source("1.2.0") : new Source("1.2.0-preview.3"));
        var candidate = await engine.CheckAsync(UpdateChannel.Preview, default);
        Assert.Equal("1.2.0", candidate?.TargetVersion);
        Assert.Equal(UpdateChannel.Stable, candidate?.Channel);
    }

    private sealed class InvalidSource : IUpdateSource
    {
        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
            => throw new InvalidDataException("Release feed identity, version, hash, or asset is invalid.");
        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
            => throw new NotSupportedException();
    }

    [Fact] public async Task InvalidStreamDoesNotHideValidStream()
    {
        using var engine = new VelopackEngine(new("Example", new("https://github.com/example/app"), "unused"),
            new TestVelopackLocator("Example", "1.0.0", Path.GetTempPath()),
            stream => stream == UpdateChannel.Stable ? new Source("1.1.0") : new InvalidSource());
        Assert.Equal("1.1.0", (await engine.CheckAsync(UpdateChannel.Preview, default))?.TargetVersion);
    }

    [Fact] public async Task InvalidStreamIsReportedWhenNothingIsAvailable()
    {
        using var engine = new VelopackEngine(new("Example", new("https://github.com/example/app"), "unused"),
            new TestVelopackLocator("Example", "1.0.0", Path.GetTempPath()), _ => new InvalidSource());
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.CheckAsync(UpdateChannel.Stable, default));
    }

    [Fact] public async Task TamperedDownloadIsNotStaged()
    {
        var directory = Path.Combine(Path.GetTempPath(), "updater-integrity-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try {
            using var engine = new VelopackEngine(new("Example", new("https://github.com/example/app"), directory),
                new TestVelopackLocator("Example", "1.0.0", directory), _ => new Source("1.1.0"));
            var candidate = await engine.CheckAsync(UpdateChannel.Stable, default);
            Assert.NotNull(candidate);
            var error = await Assert.ThrowsAsync<Velopack.Exceptions.ChecksumFailedException>(() => engine.DownloadAsync(candidate, _ => { }, default));
            Assert.Contains("SHA256", error.Message);
            Assert.Empty(Directory.GetFiles(directory, "*-full.nupkg"));
        } finally { Directory.Delete(directory, true); }
    }

    private sealed class SignedSource(Func<VelopackAsset, byte[]?> signature, params string[] versions) : IUpdateSource, IPackageSignatureSource
    {
        private readonly Source inner = new(versions);
        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
            => inner.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease);
        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
            => inner.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken);
        public Task<byte[]?> GetSignatureAsync(VelopackAsset asset) => Task.FromResult(signature(asset));
    }
    private static VelopackEngine SigningEngine(ECDsa publisher, Func<UpdateChannel, IUpdateSource> sources, List<string>? log = null) =>
        new(new("Example", new("https://github.com/example/app"), "unused") { PackageSigningKey = publisher.ExportSubjectPublicKeyInfoPem() },
            new TestVelopackLocator("Example", "1.0.0", Path.GetTempPath()), sources, log is null ? null : log.Add);

    [Fact] public async Task SignedPackageIsOffered()
    {
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var engine = SigningEngine(publisher, _ => new SignedSource(a => PackageSignatureTests.Sign(publisher, a), "1.1.0"));
        Assert.True(engine.VerifiesPackageSignatures);
        Assert.Equal("1.1.0", (await engine.CheckAsync(UpdateChannel.Stable, default))?.TargetVersion);
    }

    [Fact] public async Task UnsignedOrForeignSignedPackagesAreNeverOffered()
    {
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Func<UpdateChannel, IUpdateSource>[] cases = [
            _ => new SignedSource(_ => null, "1.1.0"),
            _ => new SignedSource(a => PackageSignatureTests.Sign(attacker, a), "1.1.0"),
            _ => new Source("1.1.0"),
        ];
        foreach (var sources in cases) {
            var log = new List<string>();
            using var engine = SigningEngine(publisher, sources, log);
            await Assert.ThrowsAsync<InvalidDataException>(() => engine.CheckAsync(UpdateChannel.Stable, default));
            Assert.Contains(log, m => m.Contains("reason=signature"));
        }
    }

    [Fact] public async Task ForgedStreamDoesNotHideSignedStream()
    {
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var engine = SigningEngine(publisher, stream => stream == UpdateChannel.Stable
            ? new SignedSource(a => PackageSignatureTests.Sign(publisher, a), "1.1.0") : new SignedSource(_ => null, "1.2.0-preview.1"));
        Assert.Equal("1.1.0", (await engine.CheckAsync(UpdateChannel.Preview, default))?.TargetVersion);
    }

    [Fact] public void EngineWithoutKeyDoesNotClaimSignatureVerification()
    {
        using var engine = new VelopackEngine(new("Example", new("https://github.com/example/app"), "unused"),
            new TestVelopackLocator("Example", "1.0.0", Path.GetTempPath()), _ => new Source("1.1.0"));
        Assert.False(engine.VerifiesPackageSignatures);
    }

    [Theory]
    [InlineData("win-preview", "1.2.0-preview.3", UpdateChannel.Stable, "1.1.0", "1.2.0-preview.3")]
    [InlineData("win-preview", "1.2.0-preview.3", UpdateChannel.Preview, "1.1.0", "1.2.0-preview.3")]
    [InlineData("win-stable", "1.1.0", UpdateChannel.Preview, "1.1.0", "1.1.0")]
    [InlineData("win-preview", "1.2.0", UpdateChannel.Stable, "1.2.0", "1.2.0-preview.3")]
    public async Task InstalledChannelNeverCausesDowngradeOrReinstall(string installed, string current, UpdateChannel channel, string stable, string preview)
    {
        using var engine = new VelopackEngine(new("Example", new("https://github.com/example/app"), "unused"),
            new TestVelopackLocator("Example", current, Path.GetTempPath(), null!, null!, null!, installed, null!, null!, null!),
            stream => stream == UpdateChannel.Stable ? new Source(stable) : new Source(preview));
        Assert.Equal(current, engine.CurrentVersion);
        Assert.Null(await engine.CheckAsync(channel, default));
    }
}
