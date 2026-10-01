using System.Text;
using System.Text.Json;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;
using Xunit;

namespace SubZeroDev.Platform.Updater.Tests;

public sealed class GithubFeedTests
{
    private sealed class Downloader : IFileDownloader
    {
        internal readonly Dictionary<string, string> Responses = [];
        internal readonly List<string> Requests = [];
        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            Assert.False(headers?.ContainsKey("Authorization") ?? false);
            Requests.Add(url);
            return Task.FromResult(Encoding.UTF8.GetBytes(Responses[url]));
        }
        public async Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30) => Encoding.UTF8.GetString(await DownloadBytes(url, headers, timeout));
        public Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default) => throw new NotImplementedException();
    }
    private static object Release(string tag, bool preview = false, bool draft = false, bool packagePresent = true) => new {
        tag_name = tag, prerelease = preview, draft, published_at = "2026-10-01T00:00:00Z",
        assets = (packagePresent ? new[] { "releases.win-stable.json", "releases.win-preview.json", "Example-full.nupkg" } : ["releases.win-stable.json"])
            .Select(name => new { name, browser_download_url = $"https://github.com/example/app/releases/download/{tag}/{name}" })
    };
    private static string Feed(string version, string id = "Example", string? hash = null, string file = "Example-full.nupkg") => JsonSerializer.Serialize(new { Assets = new[] {
        new { PackageId = id, Version = version, Type = "Full", FileName = file, SHA256 = hash ?? new string('A', 64), Size = 123 }
    } });
    private static ValidatedGithubSource Source(Downloader downloader, UpdateChannel channel = UpdateChannel.Stable) =>
        new(new("Example", new("https://github.com/example/app"), "unused"), channel, downloader);

    [Fact] public async Task StableIgnoresDraftAndPreviewAndNeverSendsAuthentication()
    {
        var d = new Downloader();
        d.Responses["https://api.github.com/repos/example/app/releases?per_page=100&page=1"] = JsonSerializer.Serialize(new[] { Release("v3.0.0", draft: true), Release("v2.0.0-preview.1", preview: true), Release("v1.1.0") });
        d.Responses["https://github.com/example/app/releases/download/v1.1.0/releases.win-stable.json"] = Feed("1.1.0");
        var feed = await Source(d).GetReleaseFeed(NullVelopackLogger.Instance, "Example", "win-stable");
        Assert.Equal("1.1.0", Assert.Single(feed.Assets).Version.ToString());
        Assert.Equal(2, d.Requests.Count);
    }

    [Theory]
    [InlineData("1.2.0", "Example", null, "Example-full.nupkg", true)]
    [InlineData("1.1.0", "OtherApp", null, "Example-full.nupkg", true)]
    [InlineData("1.1.0", "Example", "bad", "Example-full.nupkg", true)]
    [InlineData("1.1.0", "Example", null, "../Example-full.nupkg", true)]
    [InlineData("1.1.0", "Example", null, "Example-full.nupkg", false)]
    public async Task RejectsMismatchedOrMissingReleaseAssets(string version, string id, string? hash, string file, bool present)
    {
        var d = new Downloader();
        d.Responses["https://api.github.com/repos/example/app/releases?per_page=100&page=1"] = JsonSerializer.Serialize(new[] { Release("v1.1.0", packagePresent: present) });
        d.Responses["https://github.com/example/app/releases/download/v1.1.0/releases.win-stable.json"] = Feed(version, id, hash, file);
        await Assert.ThrowsAsync<InvalidDataException>(() => Source(d).GetReleaseFeed(NullVelopackLogger.Instance, "Example", "win-stable"));
    }

    [Fact] public async Task FindsStableAfterOneHundredPreviews()
    {
        var d = new Downloader();
        d.Responses["https://api.github.com/repos/example/app/releases?per_page=100&page=1"] = JsonSerializer.Serialize(Enumerable.Range(1, 100).Select(n => Release($"v2.0.0-preview.{n}", preview: true)));
        d.Responses["https://api.github.com/repos/example/app/releases?per_page=100&page=2"] = JsonSerializer.Serialize(new[] { Release("v1.1.0") });
        d.Responses["https://github.com/example/app/releases/download/v1.1.0/releases.win-stable.json"] = Feed("1.1.0");
        Assert.Single((await Source(d).GetReleaseFeed(NullVelopackLogger.Instance, "Example", "win-stable")).Assets);
        Assert.Equal(3, d.Requests.Count);
    }
}
