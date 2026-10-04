using System.Net;
using Xunit;

namespace SubZeroDev.Platform.Updater.Tests;

public sealed class NetworkPolicyTests
{
    private const string Url = "https://github.com/example/app/releases/download/v1/package.nupkg";
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Engine : IUpdateEngine
    {
        public bool IsSupported => true;
        public string? CurrentVersion => "1.0.0";
        public bool VerifiesPackageSignatures => true;
        internal int Checks;
        internal Exception? Error;
        internal int[] Progress = [];
        public Task<UpdateCandidate?> CheckAsync(UpdateChannel channel, CancellationToken token) {
            Checks++;
            if (Error is not null) throw Error;
            return Task.FromResult<UpdateCandidate?>(new("1.0.0", "2.0.0", channel, null, null, null, new object()));
        }
        public Task DownloadAsync(UpdateCandidate candidate, Action<int> progress, CancellationToken token) {
            foreach (int value in Progress) progress(value);
            return Task.CompletedTask;
        }
        public void VerifyCanApply(UpdateCandidate candidate) { }
        public void Apply(UpdateCandidate candidate) { }
        public void Dispose() { }
    }
    private sealed class Restart : IUpdateRestartCoordinator
    {
        public Task<RestartDecision> RequestRestartAsync(CancellationToken token) => Task.FromResult(RestartDecision.Defer);
        public Task RestartAbortedAsync() => Task.CompletedTask;
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        internal readonly Clock Clock = new();
        internal readonly Engine Engine = new();
        internal PreferencesStore Store => new(DirectoryPath, null);
        internal async Task<UpdaterClient> Create(Action<string>? log = null) {
            var store = Store;
            return new(new("Example", new("https://github.com/example/app"), DirectoryPath), Engine, store,
                new Restart(), await store.LoadAsync(default), Clock, log);
        }
        public void Dispose() { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); }
    }

    [Fact] public async Task FortyFiveMinuteBackoffSurvivesRestartAndExpiresAtOriginalDeadline() {
        using var f = new Fixture();
        var deadline = f.Clock.Now.AddMinutes(45);
        f.Engine.Error = new RateLimitedException("limited", HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(45));
        await using (var first = await f.Create()) {
            Assert.Equal(CheckOutcomeKind.RateLimited, (await first.StartAutomaticCheckAsync()).Kind);
            Assert.Equal(deadline, first.Preferences.AutomaticBackoffUntilUtc);
        }
        f.Clock.Now += TimeSpan.FromMinutes(16);
        await using (var restarted = await f.Create()) {
            Assert.Equal(CheckOutcomeKind.RecentlyChecked, (await restarted.StartAutomaticCheckAsync()).Kind);
            Assert.Equal(1, f.Engine.Checks);
            // A manual failure is available during backoff and does not clear the saved deadline.
            f.Engine.Error = new HttpRequestException("offline");
            Assert.Equal(CheckOutcomeKind.NetworkUnavailable, (await restarted.CheckAsync(CheckOrigin.Manual)).Kind);
            Assert.Equal(2, f.Engine.Checks);
        }
        f.Clock.Now = deadline.AddTicks(-1);
        await using var third = await f.Create();
        Assert.Equal(CheckOutcomeKind.RecentlyChecked, (await third.StartAutomaticCheckAsync()).Kind);
        f.Clock.Now = deadline;
        f.Engine.Error = null;
        Assert.Equal(CheckOutcomeKind.UpdateAvailable, (await third.StartAutomaticCheckAsync()).Kind);
        Assert.Null((await f.Store.LoadAsync(default)).AutomaticBackoffUntilUtc);
    }

    [Fact] public async Task SuccessfulManualCheckClearsPersistedBackoff() {
        using var f = new Fixture();
        await f.Store.SaveAsync(new() { AutomaticBackoffUntilUtc = f.Clock.Now.AddMinutes(45) }, default);
        await using var client = await f.Create();
        Assert.Equal(CheckOutcomeKind.UpdateAvailable, (await client.CheckAsync(CheckOrigin.Manual)).Kind);
        Assert.Null((await f.Store.LoadAsync(default)).AutomaticBackoffUntilUtc);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RestoredBackoffIsBoundedAndReadsNewerSchemas(int schema) {
        using var f = new Fixture();
        await f.Store.SaveAsync(new() { SchemaVersion = schema, AutomaticBackoffUntilUtc = f.Clock.Now.AddYears(10) }, default);
        await using var client = await f.Create();
        Assert.Equal(CheckOutcomeKind.RecentlyChecked, (await client.StartAutomaticCheckAsync()).Kind);
        f.Clock.Now += TimeSpan.FromHours(1);
        Assert.Equal(CheckOutcomeKind.UpdateAvailable, (await client.StartAutomaticCheckAsync()).Kind);
    }

    [Fact] public async Task ExcessiveRetryAfterPersistsOnlyOneHour() {
        using var f = new Fixture();
        f.Engine.Error = new RateLimitedException("limited", HttpStatusCode.TooManyRequests, TimeSpan.FromDays(10));
        await using var client = await f.Create();
        await client.StartAutomaticCheckAsync();
        Assert.Equal(f.Clock.Now.AddHours(1), (await f.Store.LoadAsync(default)).AutomaticBackoffUntilUtc);
    }

    [Fact] public async Task ClientPublishesAndLogsOnlyIncreasingPercentagesAndAlwaysCompletes() {
        using var f = new Fixture();
        f.Engine.Progress = [0, 0, -5, 1, 1, 2, 1, 50, 49, 50, 99];
        var logs = new List<string>();
        await using var client = await f.Create(logs.Add);
        var progress = new List<int>();
        client.StateChanged += (_, state) => { if (state.Stage == UpdateStage.Downloading) progress.Add(state.DownloadPercent!.Value); };
        var candidate = (await client.CheckAsync(CheckOrigin.Manual)).Candidate!;
        await client.InstallAsync(candidate);
        Assert.Equal(new[] { 0, 1, 2, 50, 99, 100 }, progress);
        Assert.Equal(progress.Count, logs.Count(x => x.Contains("stage=Downloading")));
    }

    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => response(token);
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("string")]
    [InlineData("file")]
    public async Task CallerTimeoutBoundsSlowResponse(string method) {
        using var f = new Fixture();
        Directory.CreateDirectory(f.DirectoryPath);
        using var downloader = new PublicDownloader(TimeSpan.FromSeconds(5), new Handler(async token => {
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
        }));
        Task Download() => method switch {
            "bytes" => downloader.DownloadBytes(Url, timeout: .002),
            "string" => downloader.DownloadString(Url, timeout: .002),
            _ => downloader.DownloadFile(Url, Path.Combine(f.DirectoryPath, "package"), _ => { }, timeout: .002)
        };
        await Assert.ThrowsAsync<TimeoutException>(Download);
    }

    // Produces a large body without retaining it in memory; can delay each read to model stalls or trickles.
    private sealed class GeneratedStream(long length, TimeSpan delay) : Stream
    {
        private long remaining = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) {
            if (remaining == 0) return 0;
            await Task.Delay(delay, token);
            int count = (int)Math.Min(remaining, buffer.Length);
            buffer.Span[..count].Clear();
            remaining -= count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private static Handler StreamHandler(long length, TimeSpan delay, bool knownLength = true) => new(_ => {
        var content = new StreamContent(new GeneratedStream(length, delay));
        if (knownLength) content.Headers.ContentLength = length;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    });

    [Fact] public async Task CallerTimeoutBoundsStalledTransfer() {
        using var f = new Fixture();
        Directory.CreateDirectory(f.DirectoryPath);
        using var downloader = new PublicDownloader(TimeSpan.FromSeconds(5), StreamHandler(1, TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAsync<TimeoutException>(() => downloader.DownloadFile(Url, Path.Combine(f.DirectoryPath, "package"), _ => { }, timeout: .002));
    }

    [Fact] public async Task CallerDeadlineStillBoundsTransferReceivingData() {
        using var f = new Fixture();
        Directory.CreateDirectory(f.DirectoryPath);
        using var downloader = new PublicDownloader(TimeSpan.FromSeconds(5), StreamHandler(81920 * 20, TimeSpan.FromMilliseconds(80)));
        await Assert.ThrowsAsync<TimeoutException>(() => downloader.DownloadFile(Url, Path.Combine(f.DirectoryPath, "package"), _ => { }, timeout: .004));
    }

    [Fact] public async Task TimeoutIsInMinutesAndPackageDeadlineIsIndependent() {
        using var f = new Fixture();
        Directory.CreateDirectory(f.DirectoryPath);
        using var metadata = new PublicDownloader(TimeSpan.FromSeconds(5), StreamHandler(1, TimeSpan.FromMilliseconds(100)));
        Assert.Single(await metadata.DownloadBytes(Url, timeout: .05));
        using var package = new PublicDownloader(TimeSpan.FromSeconds(5), StreamHandler(81920 * 20, TimeSpan.FromMilliseconds(80)), TimeSpan.FromMilliseconds(240));
        await Assert.ThrowsAsync<TimeoutException>(() => package.DownloadFile(Url, Path.Combine(f.DirectoryPath, "package"), _ => { }, timeout: .05));
    }

    [Theory]
    [InlineData(true, 100 * 1024 * 1024)]
    [InlineData(false, 100 * 1024 * 1024)]
    [InlineData(true, 0)]
    public async Task LargeStreamReportsBoundedMonotonicProgressAndSingleCompletion(bool knownLength, int length) {
        using var f = new Fixture();
        Directory.CreateDirectory(f.DirectoryPath);
        string file = Path.Combine(f.DirectoryPath, "package");
        using var downloader = new PublicDownloader(TimeSpan.FromSeconds(5), StreamHandler(length, TimeSpan.Zero, knownLength));
        var reports = new List<int>();
        await downloader.DownloadFile(Url, file, reports.Add);
        Assert.Equal(length, new FileInfo(file).Length);
        Assert.Equal(100, reports[^1]);
        Assert.Equal(1, reports.Count(x => x == 100));
        Assert.InRange(reports.Count, 1, 101);
        Assert.All(reports.Zip(reports.Skip(1)), pair => Assert.True(pair.First < pair.Second));
        if (knownLength && length > 0) Assert.InRange(reports.Count, 90, 101);
        else Assert.Single(reports);
    }
}
