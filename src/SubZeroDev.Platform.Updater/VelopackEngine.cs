using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
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
        downloader = new(options.NetworkTimeout, handler);
        this.sourceFactory = sourceFactory;
        this.log = log;
        signingKey = options.PackageSigningKey is { } key ? PackageSignature.ImportPublicKey(key) : null;
    }

    public bool IsSupported => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
        locator.CurrentlyInstalledVersion is not null && locator.AppId == options.AppId;

    public string? CurrentVersion => locator.CurrentlyInstalledVersion?.ToString();

    public bool VerifiesPackageSignatures => signingKey is not null;

    public async Task<UpdateCandidate?> CheckAsync(UpdateChannel channel, CancellationToken token)
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

internal sealed class ValidatedGithubSource : GithubSource, IPackageSignatureSource
{
    private readonly UpdaterOptions options;
    private readonly UpdateChannel stream;
    private readonly Action<string>? log;
    private readonly Dictionary<GithubRelease, (string Tag, DateTimeOffset? Published)> releases = [];
    private int rejectedReleases;
    private const int MaximumFeedDownloads = 5;
    internal ValidatedGithubSource(UpdaterOptions options, UpdateChannel stream, IFileDownloader downloader, Action<string>? log = null)
        : base(options.PublicReleaseRepository.ToString(), accessToken: null, prerelease: stream == UpdateChannel.Preview, downloader)
        => (this.options, this.stream, this.log) = (options, stream, log);

    private void Reject(string reason)
    {
        // Release text is untrusted, so only the category is logged.
        try { log?.Invoke($"category=rejected-release stream={stream} reason={reason}"); } catch { /* Diagnostics cannot fail an update. */ }
    }

    protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
    {
        releases.Clear();
        rejectedReleases = 0;
        var result = new List<GithubRelease>();
        // Bound work, but scan beyond GithubSource's default ten releases so busy preview streams don't hide stable releases.
        for (int page = 1; page <= 3; page++) {
            var json = await Downloader.DownloadString($"https://api.github.com/repos{RepoUri.AbsolutePath}/releases?per_page=100&page={page}").ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            foreach (var element in document.RootElement.EnumerateArray()) {
                if (element.GetProperty("draft").GetBoolean()) continue;
                bool preview = element.GetProperty("prerelease").GetBoolean();
                if (preview != (stream == UpdateChannel.Preview)) continue;
                var tag = element.GetProperty("tag_name").GetString() ?? "";
                string pattern = preview ? @"^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)-preview\.[1-9]\d*$" : @"^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$";
                // Legacy releases without a Velopack channel do not participate in this feed.
                var release = JsonSerializer.Deserialize<GithubRelease>(element.GetRawText())!;
                string feed = stream == UpdateChannel.Preview ? "releases.win-preview.json" : "releases.win-stable.json";
                if (!release.Assets.Any(a => a.Name == feed)) continue;
                // One malformed release must not block updates from the valid ones, so it is skipped rather than fatal.
                if (!Regex.IsMatch(tag, pattern) || !SemanticVersion.TryParse(tag[1..], out _)) { rejectedReleases++; Reject("tag"); continue; }
                result.Add(release);
                releases.Add(release, (tag, release.PublishedAt is { } date ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)) : null));
            }
            if (document.RootElement.GetArrayLength() < 100) break;
        }
        if (result.Count == 0 && rejectedReleases > 0) throw new InvalidDataException("No release in this channel passed validation.");
        return result.ToArray();
    }

    public override async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        // GitBase downloads every release's feed and fails on the first bad one. Each release's feed lists only its own
        // packages, so walking newest version first and stopping at the first valid full package finds the latest
        // update with one download in the common case, and never more than a few.
        var newest = (await GetReleases(Prerelease).ConfigureAwait(false))
            .OrderByDescending(release => SemanticVersion.Parse(releases[release].Tag[1..])).Take(MaximumFeedDownloads);
        bool attempted = rejectedReleases > 0;
        foreach (var release in newest) {
            attempted = true;
            VelopackAssetFeed? feed;
            try {
                var bytes = await Downloader.DownloadBytes(GetAssetUrlFromName(release, $"releases.{channel}.json"), GetRequestHeaders("application/octet-stream")).ConfigureAwait(false);
                feed = VelopackAssetFeed.FromJson(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('﻿'));
            } catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException or ArgumentException) { Reject("feed"); continue; }
            var version = SemanticVersion.Parse(releases[release].Tag[1..]);
            var valid = (feed?.Assets ?? []).Where(asset => {
                if (asset.PackageId == options.AppId && asset.Size > 0 && asset.FileName == Path.GetFileName(asset.FileName) &&
                    Regex.IsMatch(asset.SHA256 ?? "", "^[A-F0-9]{64}$") && asset.Version == version &&
                    release.Assets.Any(a => a.Name == asset.FileName)) return true;
                Reject("asset");
                return false;
            }).Select(asset => (VelopackAsset)new GitBaseAsset(asset, release)).ToArray();
            if (valid.Any(asset => asset.Type == VelopackAssetType.Full)) return new VelopackAssetFeed { Assets = valid };
        }
        // A channel with nothing valid left is reported as invalid rather than silently up to date.
        if (attempted) throw new InvalidDataException("Release feed identity, version, hash, or asset is invalid.");
        return new VelopackAssetFeed();
    }

    public override async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken)
    {
        // The feed declares the package size, so a response larger than that is cut off instead of filling the disk.
        if (Downloader is ISizeLimitedDownloader limited) limited.MaximumDownloadBytes = releaseEntry.Size;
        try { await base.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken).ConfigureAwait(false); }
        finally { if (Downloader is ISizeLimitedDownloader reset) reset.MaximumDownloadBytes = null; }
        // Validate the manifest before UpdateManager renames its incomplete download into a staged package.
        using var package = ZipFile.OpenRead(localFile);
        using var manifest = package.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)).Open();
        var document = XDocument.Load(manifest);
        string? Field(string name) => document.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
        if (Field("id") != options.AppId || !string.Equals(Field("machineArchitecture"), "x64", StringComparison.OrdinalIgnoreCase) ||
            !SemanticVersion.TryParse(Field("version"), out var version) || version != releaseEntry.Version)
            throw new InvalidDataException("Downloaded package identity, architecture, or version does not match the selected release.");
    }

    public async Task<byte[]?> GetSignatureAsync(VelopackAsset asset)
    {
        string name = asset.FileName + PackageSignature.Extension;
        if (asset is not GitBaseAsset { Release: var release } || !release.Assets.Any(a => a.Name == name)) return null;
        return await Downloader.DownloadBytes(GetAssetUrlFromName(release, name), GetRequestHeaders("application/octet-stream")).ConfigureAwait(false);
    }

    internal (DateTimeOffset? Published, Uri Url) Metadata(VelopackAsset asset)
    {
        var release = releases[((GitBaseAsset)asset).Release];
        return (release.Published, new Uri($"{RepoUri}/releases/tag/{Uri.EscapeDataString(release.Tag)}"));
    }
}

/// <summary>A downloader that can refuse responses larger than a known size.</summary>
internal interface ISizeLimitedDownloader
{
    long? MaximumDownloadBytes { set; }
}

/// <summary>GitHub asked the client to slow down; RetryAfter is its requested wait, when it said.</summary>
internal sealed class RateLimitedException(string message, System.Net.HttpStatusCode status, TimeSpan? retryAfter) : HttpRequestException(message, null, status)
{
    internal TimeSpan? RetryAfter { get; } = retryAfter;
}

internal sealed class PublicDownloader : IFileDownloader, ISizeLimitedDownloader, IDisposable
{
    private const int MaximumRedirects = 5;
    private static readonly string UserAgent = "SubZeroDev.Platform.Updater/" +
        (typeof(PublicDownloader).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0");
    private readonly HttpClient client;
    private readonly TimeSpan timeout;

    internal PublicDownloader(TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        this.timeout = timeout;
        // Redirects are followed by SendAsync so every hop is checked against the GitHub allowlist.
        client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal CancellationToken OperationToken { get; set; }
    public long? MaximumDownloadBytes { get; set; }

    private static Uri CheckedUri(Uri uri)
    {
        if (uri.Scheme != "https" || (uri.Host != "github.com" && uri.Host != "api.github.com" && !uri.Host.EndsWith(".githubusercontent.com", StringComparison.Ordinal)))
            throw new InvalidDataException("Release downloads must use GitHub HTTPS URLs.");
        return uri;
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken token)
    {
        var uri = CheckedUri(new Uri(url));
        // One bounded retry for transient server errors. 403/429 are surfaced immediately.
        for (int attempt = 0, redirects = 0; ; ) {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308) {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || ++redirects > MaximumRedirects) throw new InvalidDataException("Release download redirected too many times.");
                uri = CheckedUri(location.IsAbsoluteUri ? location : new Uri(uri, location));
                continue;
            }
            if ((int)response.StatusCode >= 500 && attempt++ == 0) {
                response.Dispose();
                await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
                continue;
            }
            if (!response.IsSuccessStatusCode) {
                var status = response.StatusCode;
                var retryAfter = RetryAfter(response);
                response.Dispose();
                if (status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) throw new RateLimitedException($"GitHub returned HTTP {(int)status}.", status, retryAfter);
                throw new HttpRequestException($"GitHub returned HTTP {(int)status}.", null, status);
            }
            return response;
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } retry) return retry.Delta ?? (retry.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        // A primary rate limit reports when its window resets instead.
        if (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0" &&
            response.Headers.TryGetValues("x-ratelimit-reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var seconds))
            return DateTimeOffset.FromUnixTimeSeconds(seconds) - DateTimeOffset.UtcNow;
        return null;
    }

    // The timeout bounds the wait for response headers and, through the restart callback, each stall while reading the body.
    private async Task<T> RequestAsync<T>(string url, CancellationToken extra, Func<HttpResponseMessage, CancellationToken, Action, Task<T>> consume)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(OperationToken, extra);
        linked.CancelAfter(timeout);
        try {
            using var response = await SendAsync(url, linked.Token).ConfigureAwait(false);
            return await consume(response, linked.Token, () => linked.CancelAfter(timeout)).ConfigureAwait(false);
        } catch (OperationCanceledException) when (!OperationToken.IsCancellationRequested && !extra.IsCancellationRequested) {
            throw new TimeoutException("The update request timed out.");
        }
    }

    public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
        // Metadata is small, so one deadline covers the whole response.
        RequestAsync(url, default, async (r, token, _) => {
            if (r.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new InvalidDataException("Release metadata is too large.");
            await using var input = await r.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var output = new MemoryStream();
            byte[] buffer = new byte[8192]; int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0) {
                if (output.Length + count > 8 * 1024 * 1024) throw new InvalidDataException("Release metadata is too large.");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        });

    public async Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
        System.Text.Encoding.UTF8.GetString(await DownloadBytes(url, headers, timeout).ConfigureAwait(false));

    public Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default) =>
        // Packages can be large, so the deadline restarts whenever data arrives and only a stalled transfer times out.
        RequestAsync(url, cancelToken, async (r, token, progressed) => {
            await using var input = await r.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var output = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            long total = r.Content.Headers.ContentLength ?? 0;
            long? limit = MaximumDownloadBytes;
            if (limit is { } declared && total > declared) throw new InvalidDataException("The package is larger than its release declares.");
            byte[] buffer = new byte[81920]; int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0) {
                if (limit is { } maximum && output.Length + count > maximum) throw new InvalidDataException("The package is larger than its release declares.");
                progressed();
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                if (total > 0) progress((int)Math.Min(100, output.Length * 100 / total));
            }
            progress(100);
            return true;
        });

    public void Dispose() => client.Dispose();
}
