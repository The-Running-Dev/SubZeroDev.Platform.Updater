using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace SubZeroDev.Platform.Updater;

internal sealed class ValidatedGithubSource : GithubSource, IPackageSignatureSource
{
    private readonly UpdaterOptions options;
    private readonly UpdateChannel stream;
    private readonly Action<string>? log;
    private readonly Dictionary<GithubRelease, (string Tag, DateTimeOffset? Published)> releases = [];
    private int rejectedReleases;
    private const int MaximumReleasePages = 3;
    private const int ReleasesPerPage = 100;
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
        for (int page = 1; page <= MaximumReleasePages; page++) {
            string json;
            // Only the listing tells a missing repository apart; a missing asset stays a network failure.
            try { json = await Downloader.DownloadString($"https://api.github.com/repos{RepoUri.AbsolutePath}/releases?per_page={ReleasesPerPage}&page={page}").ConfigureAwait(false); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { throw new RepositoryNotFoundException(ex); }
            using var document = JsonDocument.Parse(json);
            foreach (var element in document.RootElement.EnumerateArray().Take(ReleasesPerPage)) {
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
            if (document.RootElement.GetArrayLength() < ReleasesPerPage) break;
        }
        if (result.Count == 0 && rejectedReleases > 0) throw new InvalidDataException("No release in this channel passed validation.");
        return result.ToArray();
    }

    public override async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        // GitBase downloads every release's feed and fails on the first bad one. Each release's feed lists only its own
        // packages, so walking newest version first and stopping at the first valid full package finds the latest
        // update with one download in the common case. The listing bounds worst-case work to 300 feeds.
        var newest = (await GetReleases(Prerelease).ConfigureAwait(false))
            .OrderByDescending(release => SemanticVersion.Parse(releases[release].Tag[1..]));
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
