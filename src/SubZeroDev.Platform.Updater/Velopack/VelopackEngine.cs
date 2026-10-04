using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Xml.Linq;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace SubZeroDev.Platform.Updater;

internal sealed class VelopackEngine : IUpdateEngine
{
    private readonly UpdaterOptions options;
    private readonly IVelopackLocator locator;
    private readonly PublicDownloader downloader;
    private readonly Func<UpdateChannel, IUpdateSource>? sourceFactory;
    private readonly Action<string>? log;
    private readonly ECDsa? signingKey;
    private sealed record Selection(UpdateManager Manager, UpdateInfo Update);

    internal VelopackEngine(UpdaterOptions options, IVelopackLocator? locator = null, Func<UpdateChannel, IUpdateSource>? sourceFactory = null, Action<string>? log = null, HttpMessageHandler? handler = null)
    {
        this.options = options;
        this.locator = locator ?? (VelopackLocator.IsCurrentSet ? VelopackLocator.Current : VelopackLocator.CreateDefaultForPlatform());
        downloader = new(options.NetworkTimeout, handler, options.PackageDownloadTimeout);
        this.sourceFactory = sourceFactory;
        this.log = log;
        signingKey = options.PackageSigningKey is { } key ? PackageSignature.ImportPublicKey(key) : null;
        // A slow request with its one retry must still fit, so a long NetworkTimeout widens the budget.
        CheckTimeout = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(2).Ticks, options.NetworkTimeout.Ticks * 2));
    }

    public bool IsSupported => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
        locator.CurrentlyInstalledVersion is not null && locator.AppId == options.AppId;

    public string? CurrentVersion => locator.CurrentlyInstalledVersion?.ToString();

    public bool VerifiesPackageSignatures => signingKey is not null;

    // Covers both channels, listing pages, feeds and signature lookup as one bounded operation.
    internal TimeSpan CheckTimeout { get; init; }

    public async Task<UpdateCandidate?> CheckAsync(UpdateChannel channel, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(CheckTimeout);
        try { return await CheckCoreAsync(channel, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) {
            throw new TimeoutException("The release search exceeded its time budget.");
        }
    }

    private async Task<UpdateCandidate?> CheckCoreAsync(UpdateChannel channel, CancellationToken token)
    {
        downloader.OperationToken = token;
        UpdateCandidate? best = null;
        InvalidDataException? invalid = null;
        foreach (var stream in channel == UpdateChannel.Preview ? new[] { UpdateChannel.Stable, UpdateChannel.Preview } : [UpdateChannel.Stable]) {
            var source = sourceFactory?.Invoke(stream) ?? new ValidatedGithubSource(options, stream, downloader, log);
            var manager = new UpdateManager(source, new() { ExplicitChannel = stream == UpdateChannel.Stable ? "win-stable" : "win-preview", AllowVersionDowngrade = false, MaximumDeltasBeforeFallback = -1 }, locator);
            UpdateInfo? update;
            // An invalid stream must not hide a valid update in the other stream.
            try { update = await manager.CheckForUpdatesAsync().ConfigureAwait(false); }
            catch (InvalidDataException ex) { invalid = ex; continue; }
            token.ThrowIfCancellationRequested();
            // A non-default installed channel lets Velopack offer same-version or lower releases; never accept them.
            if (update is null || manager.CurrentVersion is not { } current || update.TargetFullRelease.Version <= current) continue;
            if (signingKey is not null && !await HasPublisherSignatureAsync(source, update.TargetFullRelease).ConfigureAwait(false)) {
                // A release writer without the publisher key cannot get a package accepted; report it rather than fall back silently.
                Log($"category=rejected-release stream={stream} reason=signature");
                invalid = new InvalidDataException("The release package is not signed by the trusted publisher key.");
                continue;
            }
            // Full packages keep selection and integrity checking independent of delta history.
            update = new UpdateInfo(update.TargetFullRelease, false);
            var metadata = (source as ValidatedGithubSource)?.Metadata(update.TargetFullRelease);
            var candidate = new UpdateCandidate(current.ToString(), update.TargetFullRelease.Version.ToString(), stream,
                metadata?.Published, metadata?.Url, update.TargetFullRelease.NotesMarkdown, new Selection(manager, update));
            if (best is null || update.TargetFullRelease.Version > SemanticVersion.Parse(best.TargetVersion)) best = candidate;
        }
        if (best is null && invalid is not null) throw invalid;
        return best;
    }

    private async Task<bool> HasPublisherSignatureAsync(IUpdateSource source, VelopackAsset asset)
    {
        if (source is not IPackageSignatureSource signatures || await signatures.GetSignatureAsync(asset).ConfigureAwait(false) is not { } signature) return false;
        return PackageSignature.Verify(signingKey!, options.AppId, asset, signature);
    }

    private void Log(string message)
    {
        try { log?.Invoke(message); } catch { /* Diagnostics cannot fail an update. */ }
    }

    public async Task DownloadAsync(UpdateCandidate candidate, Action<int> progress, CancellationToken token)
    {
        var selected = (Selection)candidate.Identity;
        downloader.OperationToken = token;
        await selected.Manager.DownloadUpdatesAsync(selected.Update, progress, token).ConfigureAwait(false);
        var asset = selected.Update.TargetFullRelease;
        var packagePath = Path.Combine(locator.PackagesDir!, asset.FileName);
        try {
            // The feed SHA-256 is what the publisher signed, so check the staged bytes against it directly.
            using (var file = File.OpenRead(packagePath))
                if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(file).ConfigureAwait(false)), asset.SHA256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Downloaded package does not match the selected release hash.");
            using var package = ZipFile.OpenRead(packagePath);
            var manifest = package.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            using var stream = manifest.Open();
            var document = XDocument.Load(stream);
            string? Field(string name) => document.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
            if (Field("id") != options.AppId || !string.Equals(Field("machineArchitecture"), "x64", StringComparison.OrdinalIgnoreCase) ||
                !SemanticVersion.TryParse(Field("version"), out var version) || version != asset.Version)
                throw new InvalidDataException("Downloaded package identity, architecture, or version does not match the selected release.");
        } catch {
            // Prevent a rejected staged package from being applied by a later bootstrap.
            File.Delete(packagePath);
            throw;
        }
    }

    public void VerifyCanApply(UpdateCandidate candidate)
    {
        // Checked before the host releases its tray and instance guard, so the common apply failures leave it untouched.
        var selected = (Selection)candidate.Identity;
        if (locator.UpdateExePath is not { } updater || !File.Exists(updater))
            throw new FileNotFoundException("The Velopack updater executable is missing.");
        if (locator.PackagesDir is not { } packages || !File.Exists(Path.Combine(packages, selected.Update.TargetFullRelease.FileName)))
            throw new FileNotFoundException("The staged update package is missing.");
        // A deferred restart may happen much later; do not trust the package merely because it was valid at download time.
        VerifyStagedPackageHash(Path.Combine(packages, selected.Update.TargetFullRelease.FileName), selected.Update.TargetFullRelease.SHA256);
    }

    internal static void VerifyStagedPackageHash(string path, string expectedHash)
    {
        using var file = File.OpenRead(path);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(file)), expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The staged update package no longer matches the selected release hash.");
    }

    public void Apply(UpdateCandidate candidate)
    {
        var selected = (Selection)candidate.Identity;
        selected.Manager.WaitExitThenApplyUpdates(selected.Update.TargetFullRelease, silent: true, restart: true);
    }

    public void Dispose()
    {
        downloader.Dispose();
        signingKey?.Dispose();
    }
}
