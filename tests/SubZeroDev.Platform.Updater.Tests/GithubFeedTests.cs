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
    private static object Release(string tag, bool preview = false, bool draft = false, bool packagePresent = true, bool signed = false) => new {
        tag_name = tag, prerelease = preview, draft, published_at = "2026-10-01T00:00:00Z",
        assets = (packagePresent ? new[] { "releases.win-stable.json", "releases.win-preview.json", "Example-full.nupkg" } : ["releases.win-stable.json"])
            .Concat(signed ? ["Example-full.nupkg.sig"] : Array.Empty<string>())
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
    [Fact] public async Task MalformedReleasesAreSkippedWhenValidReleasesRemain()
    {
        var d = new Downloader();
        d.Responses["https://api.github.com/repos/example/app/releases?per_page=100&page=1"] = JsonSerializer.Serialize(new[] { Release("v1.3"), Release("v1.2.0"), Release("v1.1.0") });
        d.Responses["https://github.com/example/app/releases/download/v1.2.0/releases.win-stable.json"] = Feed("1.2.0", hash: "bad");
        d.Responses["https://github.com/example/app/releases/download/v1.1.0/releases.win-stable.json"] = Feed("1.1.0");
        var log = new List<string>();
        var feed = await new ValidatedGithubSource(new("Example", new("https://github.com/example/app"), "unused"), UpdateChannel.Stable, d, log.Add)
            .GetReleaseFeed(NullVelopackLogger.Instance, "Example", "win-stable");
        Assert.Equal("1.1.0", Assert.Single(feed.Assets).Version.ToString());
        Assert.Equal(2, log.Count(m => m.Contains("rejected-release")));
        Assert.DoesNotContain(log, m => m.Contains("v1.3"));
    }

    [Fact] public async Task ChannelWithOnlyMalformedReleasesIsInvalid()
    {
        var d = new Downloader();
        d.Responses["https://api.github.com/repos/example/app/releases?per_page=100&page=1"] = JsonSerializer.Serialize(new[] { Release("v1.2"), Release("release-1.1.0") });
        await Assert.ThrowsAsync<InvalidDataException>(() => Source(d).GetReleaseFeed(NullVelopackLogger.Instance, "Example", "win-stable"));
    }

    [Fact] public async Task OnlyTheNewestValidReleaseFeedIsDownloaded()
    {
        var d = new Downloader();
        // Listed out of version order: the walk follows the tag version, not the API order.
        d.Responses["https://api.github.com/repos/example/app/releases?per_page=100&page=1"] = JsonSerializer.Serialize(new[] { Release("v1.1.0"), Release("v1.3.0"), Release("v1.2.0") });
        d.Responses["https://github.com/example/app/releases/download/v1.3.0/releases.win-stable.json"] = Feed("1.3.0");
        var feed = await Source(d).GetReleaseFeed(NullVelopackLogger.Instance, "Example", "win-stable");
        Assert.Equal("1.3.0", Assert.Single(feed.Assets).Version.ToString());
        Assert.Equal(2, d.Requests.Count);
    }

    [Fact] public async Task CorruptNewestFeedFallsBackToPreviousRelease()
    {
        var d = new Downloader();
        d.Responses["https://api.github.com/repos/example/app/releases?per_page=100&page=1"] = JsonSerializer.Serialize(new[] { Release("v1.2.0"), Release("v1.1.0") });
        d.Responses["https://github.com/example/app/releases/download/v1.2.0/releases.win-stable.json"] = "{ not json";
        d.Responses["https://github.com/example/app/releases/download/v1.1.0/releases.win-stable.json"] = Feed("1.1.0");
        Assert.Equal("1.1.0", Assert.Single((await Source(d).GetReleaseFeed(NullVelopackLogger.Instance, "Example", "win-stable")).Assets).Version.ToString());
    }

    [Fact] public async Task FeedDownloadsAreBoundedWhenNothingIsValid()
    {
        var d = new Downloader();
        var tags = Enumerable.Range(1, 8).Select(n => $"v1.{n}.0").ToArray();
        d.Responses["https://api.github.com/repos/example/app/releases?per_page=100&page=1"] = JsonSerializer.Serialize(tags.Select(t => Release(t)));
        foreach (var tag in tags) d.Responses[$"https://github.com/example/app/releases/download/{tag}/releases.win-stable.json"] = Feed(tag[1..], hash: "bad");
        await Assert.ThrowsAsync<InvalidDataException>(() => Source(d).GetReleaseFeed(NullVelopackLogger.Instance, "Example", "win-stable"));
        Assert.Equal(6, d.Requests.Count);
    }

    [Fact] public async Task SignatureIsReadFromTheSameReleaseAndAbsentSignatureIsNull()
    {
        var d = new Downloader();
        d.Responses["https://api.github.com/repos/example/app/releases?per_page=100&page=1"] = JsonSerializer.Serialize(new[] { Release("v1.2.0", signed: true) });
        d.Responses["https://github.com/example/app/releases/download/v1.2.0/releases.win-stable.json"] = Feed("1.2.0");
        d.Responses["https://github.com/example/app/releases/download/v1.2.0/Example-full.nupkg.sig"] = "c2lnbmF0dXJl";
        var source = Source(d);
        var asset = Assert.Single((await source.GetReleaseFeed(NullVelopackLogger.Instance, "Example", "win-stable")).Assets);
        Assert.Equal("c2lnbmF0dXJl", Encoding.UTF8.GetString((await source.GetSignatureAsync(asset))!));

        d.Responses["https://api.github.com/repos/example/app/releases?per_page=100&page=1"] = JsonSerializer.Serialize(new[] { Release("v1.2.0") });
        source = Source(d);
        Assert.Null(await source.GetSignatureAsync(Assert.Single((await source.GetReleaseFeed(NullVelopackLogger.Instance, "Example", "win-stable")).Assets)));
    }

    private sealed class Handler(Func<Stream> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(body()) });
    }
    private sealed class TrickleStream(int chunks, TimeSpan delay) : Stream
    {
        private int remaining = chunks;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (remaining == 0) return 0;
            await Task.Delay(delay, cancellationToken);
            remaining--;
            buffer.Span[0] = 1;
            return 1;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact] public async Task PackageDownloadOutlastsTimeoutWhileDataKeepsArriving()
    {
        string file = Path.Combine(Path.GetTempPath(), "updater-download-" + Guid.NewGuid());
        try {
            using var downloader = new PublicDownloader(TimeSpan.FromMilliseconds(400), new Handler(() => new TrickleStream(8, TimeSpan.FromMilliseconds(100))));
            await downloader.DownloadFile("https://github.com/example/app/releases/download/v1.1.0/Example-full.nupkg", file, _ => { });
            Assert.Equal(8, new FileInfo(file).Length);
        } finally { File.Delete(file); }
    }

    [Fact] public async Task StalledPackageDownloadTimesOut()
    {
        string file = Path.Combine(Path.GetTempPath(), "updater-download-" + Guid.NewGuid());
        try {
            using var downloader = new PublicDownloader(TimeSpan.FromMilliseconds(200), new Handler(() => new TrickleStream(2, TimeSpan.FromSeconds(5))));
            await Assert.ThrowsAsync<TimeoutException>(() => downloader.DownloadFile("https://github.com/example/app/releases/download/v1.1.0/Example-full.nupkg", file, _ => { }));
        } finally { File.Delete(file); }
    }

    private sealed class Responder(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal readonly List<Uri> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }
    private static HttpResponseMessage Redirect(string location) => new(System.Net.HttpStatusCode.Redirect) { Headers = { Location = new(location) } };
    private const string FeedUrl = "https://github.com/example/app/releases/download/v1.1.0/releases.win-stable.json";
    private const string PackageUrl = "https://github.com/example/app/releases/download/v1.1.0/Example-full.nupkg";

    [Fact] public async Task RedirectsStayOnGithubHosts()
    {
        var handler = new Responder(r => r.RequestUri!.Host == "github.com"
            ? Redirect("https://objects.githubusercontent.com/package")
            : new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        using var downloader = new PublicDownloader(TimeSpan.FromSeconds(5), handler);
        Assert.Equal(3, (await downloader.DownloadBytes(FeedUrl)).Length);
        Assert.Equal(2, handler.Requests.Count);

        var hostile = new Responder(_ => Redirect("https://evil.example.com/package"));
        using var strict = new PublicDownloader(TimeSpan.FromSeconds(5), hostile);
        await Assert.ThrowsAsync<InvalidDataException>(() => strict.DownloadBytes(FeedUrl));
        Assert.Single(hostile.Requests);

        using var downgrade = new PublicDownloader(TimeSpan.FromSeconds(5), new Responder(_ => Redirect("http://objects.githubusercontent.com/package")));
        await Assert.ThrowsAsync<InvalidDataException>(() => downgrade.DownloadBytes(FeedUrl));
    }

    [Fact] public async Task EndlessRedirectsAreStopped()
    {
        using var downloader = new PublicDownloader(TimeSpan.FromSeconds(5), new Responder(_ => Redirect("https://github.com/loop")));
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadBytes(FeedUrl));
    }

    [Theory]
    [InlineData(429, "120", 120)]
    [InlineData(403, null, null)]
    public async Task RateLimitCarriesRetryAfter(int status, string? header, int? seconds)
    {
        using var downloader = new PublicDownloader(TimeSpan.FromSeconds(5), new Responder(_ => {
            var response = new HttpResponseMessage((System.Net.HttpStatusCode)status);
            if (header is not null) response.Headers.TryAddWithoutValidation("Retry-After", header);
            return response;
        }));
        var error = await Assert.ThrowsAsync<RateLimitedException>(() => downloader.DownloadBytes(FeedUrl));
        Assert.Equal(seconds is null ? null : TimeSpan.FromSeconds(seconds.Value), error.RetryAfter);
        Assert.Equal((System.Net.HttpStatusCode)status, error.StatusCode);
    }

    [Fact] public async Task MissingRepositoryListingIsRepositoryNotFound()
    {
        var source = new ValidatedGithubSource(new("Example", new("https://github.com/example/app"), "unused"), UpdateChannel.Stable,
            new PublicDownloader(TimeSpan.FromSeconds(5), new Responder(_ => new(System.Net.HttpStatusCode.NotFound))));
        await Assert.ThrowsAsync<RepositoryNotFoundException>(() => source.GetReleaseFeed(NullVelopackLogger.Instance, "Example", "win-stable"));
    }

    [Fact] public async Task NotFoundIsNotARateLimit()
    {
        using var downloader = new PublicDownloader(TimeSpan.FromSeconds(5), new Responder(_ => new(System.Net.HttpStatusCode.NotFound)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadBytes(FeedUrl));
        Assert.IsNotType<RateLimitedException>(error);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, error.StatusCode);
    }

    [Fact] public async Task DownloadsLargerThanDeclaredAreRefused()
    {
        string file = Path.Combine(Path.GetTempPath(), "updater-download-" + Guid.NewGuid());
        try {
            using var streamed = new PublicDownloader(TimeSpan.FromSeconds(5), new Handler(() => new TrickleStream(8, TimeSpan.Zero))) { MaximumDownloadBytes = 4 };
            await Assert.ThrowsAsync<InvalidDataException>(() => streamed.DownloadFile(PackageUrl, file, _ => { }));

            using var declared = new PublicDownloader(TimeSpan.FromSeconds(5), new Responder(_ => new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[64]) })) { MaximumDownloadBytes = 8 };
            await Assert.ThrowsAsync<InvalidDataException>(() => declared.DownloadFile(PackageUrl, file, _ => { }));

            using var exact = new PublicDownloader(TimeSpan.FromSeconds(5), new Handler(() => new TrickleStream(8, TimeSpan.Zero))) { MaximumDownloadBytes = 8 };
            await exact.DownloadFile(PackageUrl, file, _ => { });
            Assert.Equal(8, new FileInfo(file).Length);
        } finally { File.Delete(file); }
    }

    [Fact] public async Task UserAgentCarriesTheLibraryVersion()
    {
        string? agent = null;
        using var downloader = new PublicDownloader(TimeSpan.FromSeconds(5), new Responder(r => {
            agent = r.Headers.UserAgent.ToString();
            return new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
        }));
        await downloader.DownloadBytes(FeedUrl);
        Assert.StartsWith("SubZeroDev.Platform.Updater/", agent);
        Assert.NotEqual("SubZeroDev.Platform.Updater/0", agent);
    }

    [Fact] public async Task TagsWithLeadingZerosAreSkipped()
    {
        var d = new Downloader();
        d.Responses["https://api.github.com/repos/example/app/releases?per_page=100&page=1"] = JsonSerializer.Serialize(new[] { Release("v01.2.0"), Release("v1.02.0"), Release("v1.1.0") });
        d.Responses["https://github.com/example/app/releases/download/v1.1.0/releases.win-stable.json"] = Feed("1.1.0");
        Assert.Equal("1.1.0", Assert.Single((await Source(d).GetReleaseFeed(NullVelopackLogger.Instance, "Example", "win-stable")).Assets).Version.ToString());
    }
}
