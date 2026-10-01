using System.Net;
using Xunit;

namespace SubZeroDev.Platform.Updater.Tests;

public sealed class UpdaterTests
{
    [Fact] public async Task AtomicPreferencesSaveRecoversAfterTemporaryReadLock() {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            var store = new PreferencesStore(directory, null);
            await store.SaveAsync(new(), default);
            Task save;
            using (var locked = new FileStream(Path.Combine(directory, "updater.json"), FileMode.Open, FileAccess.Read, FileShare.Read)) {
                save = store.SaveAsync(new() { CheckAutomatically = false }, default);
                await Task.Delay(50);
                Assert.False(save.IsCompleted);
            }
            await save;
            Assert.False((await store.LoadAsync(default)).CheckAutomatically);
        } finally { Directory.Delete(directory, recursive: true); }
    }
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Store : IPreferencesStore
    {
        internal UpdaterPreferences Value = new();
        internal bool Fail;
        public Task<UpdaterPreferences> LoadAsync(CancellationToken token) => Task.FromResult(Value);
        public Task SaveAsync(UpdaterPreferences p, CancellationToken token) {
            if (Fail) throw new IOException("Disk full");
            Value = p; return Task.CompletedTask;
        }
    }
    private sealed class Engine : IUpdateEngine
    {
        public bool IsSupported { get; set; } = true;
        internal int Checks, Downloads, Applies;
        internal UpdateChannel Channel;
        internal UpdateCandidate? Candidate = CandidateFor("2.0.0");
        internal Exception? Error;
        internal TaskCompletionSource? Wait;
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<UpdateCandidate?> CheckAsync(UpdateChannel channel, CancellationToken token) {
            Checks++; Channel = channel; Entered.TrySetResult();
            if (Wait is not null) await Wait.Task.WaitAsync(token);
            if (Error is not null) throw Error;
            return Candidate;
        }
        public Task DownloadAsync(UpdateCandidate candidate, Action<int> progress, CancellationToken token) {
            Downloads++; progress(30); token.ThrowIfCancellationRequested();
            if (Error is not null) throw Error;
            return Task.CompletedTask;
        }
        public void Apply(UpdateCandidate candidate) => Applies++;
        public void Dispose() { }
    }
    private sealed class Restart : IUpdateRestartCoordinator
    {
        internal RestartDecision Decision;
        public Task<RestartDecision> RequestRestartAsync(CancellationToken token) => Task.FromResult(Decision);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Clock Clock = new();
        internal readonly Store Store = new();
        internal readonly Engine Engine = new();
        internal readonly Restart Restart = new();
        internal readonly UpdaterClient Client;
        internal Fixture(UpdaterPreferences? preferences = null) {
            Store.Value = preferences ?? new();
            Client = new(new("Example", new("https://github.com/example/app"), "unused"), Engine, Store, Restart, Store.Value, Clock);
        }
        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }
    private static UpdateCandidate CandidateFor(string version) => new("1.0.0", version, UpdateChannel.Stable, null, null, null, new object());

    [Fact] public async Task DefaultsRequireConsentAndPersistAttemptThrottle() {
        await using var f = new Fixture();
        Assert.True(f.Client.Preferences.CheckAutomatically);
        Assert.Equal(ConsentMode.ConfirmEachUpdate, f.Client.Preferences.ConsentMode);
        Assert.True((await f.Client.StartAutomaticCheckAsync()).ShouldPrompt);
        Assert.Equal(0, f.Engine.Downloads);
        Assert.Equal(f.Clock.Now, f.Store.Value.LastAutomaticNetworkCheckUtc);
        Assert.Equal(CheckOutcomeKind.RecentlyChecked, (await f.Client.StartAutomaticCheckAsync()).Kind);
        f.Clock.Now += TimeSpan.FromMinutes(15);
        await f.Client.StartAutomaticCheckAsync();
        Assert.Equal(2, f.Engine.Checks);
    }
    [Fact] public async Task ManualBypassesDisabledAndRecentChecks() {
        await using var f = new Fixture(new() { CheckAutomatically = false, LastAutomaticNetworkCheckUtc = DateTimeOffset.MaxValue });
        Assert.Equal(CheckOutcomeKind.AutomaticCheckDisabled, (await f.Client.StartAutomaticCheckAsync()).Kind);
        await f.Client.CheckAsync(CheckOrigin.Manual);
        await f.Client.CheckAsync(CheckOrigin.Manual);
        Assert.Equal(2, f.Engine.Checks);
    }
    [Fact] public async Task ToggleAndChannelPersistWithoutGrantingConsent() {
        await using var f = new Fixture();
        await f.Client.SavePreferencesAsync(f.Client.Preferences with { CheckAutomatically = false, Channel = UpdateChannel.Preview });
        Assert.Equal(UpdateChannel.Preview, f.Store.Value.Channel);
        await f.Client.CheckAsync(CheckOrigin.Manual);
        Assert.Equal(UpdateChannel.Preview, f.Engine.Channel);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Client.SavePreferencesAsync(f.Client.Preferences with { ConsentMode = ConsentMode.InstallAutomatically }));
    }
    [Fact] public async Task InstallConsentAndDeferredRestartUseExactCandidate() {
        await using var f = new Fixture();
        var candidate = (await f.Client.CheckAsync(CheckOrigin.Manual)).Candidate!;
        await f.Client.InstallAsync(candidate, true);
        Assert.Equal(ConsentMode.InstallAutomatically, f.Store.Value.ConsentMode);
        Assert.Equal(UpdateStage.AwaitingRestart, f.Client.State.Stage);
        Assert.Equal(0, f.Engine.Applies);
        f.Restart.Decision = RestartDecision.Ready;
        await f.Client.RetryPendingRestartAsync();
        Assert.Equal(1, f.Engine.Applies);
        Assert.Equal(1, f.Engine.Downloads);
        await f.Client.SavePreferencesAsync(f.Client.Preferences with { ConsentMode = ConsentMode.ConfirmEachUpdate });
        Assert.Equal(ConsentMode.ConfirmEachUpdate, f.Store.Value.ConsentMode);
    }
    [Fact] public async Task LaterSuppressesOnlySameVersionAutomaticPrompt() {
        await using var f = new Fixture();
        var candidate = (await f.Client.CheckAsync(CheckOrigin.Manual)).Candidate!;
        await f.Client.DeferAsync(candidate);
        Assert.False((await f.Client.StartAutomaticCheckAsync()).ShouldPrompt);
        Assert.True((await f.Client.CheckAsync(CheckOrigin.Manual)).ShouldPrompt);
        f.Clock.Now += TimeSpan.FromMinutes(16);
        f.Engine.Candidate = CandidateFor("2.1.0");
        Assert.True((await f.Client.StartAutomaticCheckAsync()).ShouldPrompt);
        Assert.Equal(ConsentMode.ConfirmEachUpdate, f.Store.Value.ConsentMode);
    }
    [Fact] public async Task AutomaticConsentUsesNormalInstallPath() {
        await using var f = new Fixture(new() { ConsentMode = ConsentMode.InstallAutomatically });
        Assert.False((await f.Client.StartAutomaticCheckAsync()).ShouldPrompt);
        Assert.Equal(1, f.Engine.Downloads);
        Assert.Equal(UpdateStage.AwaitingRestart, f.Client.State.Stage);
    }
    [Fact] public async Task RepeatedInstallRequestsApplyCandidateOnlyOnce() {
        await using var f = new Fixture();
        f.Restart.Decision = RestartDecision.Ready;
        var candidate = (await f.Client.CheckAsync(CheckOrigin.Manual)).Candidate!;
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => f.Client.InstallAsync(candidate)));
        await f.Client.InstallAsync(candidate);
        Assert.Equal(1, f.Engine.Downloads);
        Assert.Equal(1, f.Engine.Applies);
    }
    [Fact] public async Task OverlappingChecksShareNetworkAndCancelledWaiterDoesNotCancelOthers() {
        await using var f = new Fixture();
        f.Engine.Wait = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var automatic = f.Client.StartAutomaticCheckAsync();
        await f.Engine.Entered.Task;
        using var cancellation = new CancellationTokenSource();
        var cancelled = f.Client.CheckAsync(CheckOrigin.Manual, cancellation.Token);
        var manual = f.Client.CheckAsync(CheckOrigin.Manual);
        cancellation.Cancel();
        Assert.Equal(CheckOutcomeKind.Cancelled, (await cancelled).Kind);
        f.Engine.Wait.SetResult();
        Assert.True((await manual).ShouldPrompt);
        await automatic;
        Assert.Equal(1, f.Engine.Checks);
    }
    [Fact] public async Task DisposalCancelsUnderlyingCheck() {
        var f = new Fixture();
        f.Engine.Wait = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = f.Client.CheckAsync(CheckOrigin.Manual);
        await f.Engine.Entered.Task;
        await f.DisposeAsync();
        Assert.Equal(CheckOutcomeKind.Cancelled, (await task).Kind);
    }
    [Theory]
    [InlineData(403, CheckOutcomeKind.RateLimited)]
    [InlineData(429, CheckOutcomeKind.RateLimited)]
    [InlineData(503, CheckOutcomeKind.NetworkUnavailable)]
    public async Task HttpFailuresAreVisibleAndAutomaticFailuresAreThrottled(int status, CheckOutcomeKind expected) {
        await using var f = new Fixture();
        f.Engine.Error = new HttpRequestException("Network", null, (HttpStatusCode)status);
        Assert.Equal(expected, (await f.Client.StartAutomaticCheckAsync()).Kind);
        Assert.Equal(CheckOutcomeKind.RecentlyChecked, (await f.Client.StartAutomaticCheckAsync()).Kind);
        Assert.Null(f.Store.Value.LastSuccessfulCheckUtc);
        Assert.Equal(expected, (await f.Client.CheckAsync(CheckOrigin.Manual)).Kind);
    }
    [Fact] public async Task InvalidFeedCannotInstall() {
        await using var f = new Fixture();
        f.Engine.Error = new InvalidDataException("Bad hash");
        Assert.Equal(CheckOutcomeKind.InvalidRelease, (await f.Client.CheckAsync(CheckOrigin.Manual)).Kind);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Client.InstallAsync(CandidateFor("9.0.0")));
        Assert.Equal(0, f.Engine.Applies);
    }
    [Fact] public async Task FailedPreferenceWritePreservesLastGoodValue() {
        await using var f = new Fixture(); f.Store.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => f.Client.SavePreferencesAsync(f.Client.Preferences with { CheckAutomatically = false }));
        Assert.True(f.Client.Preferences.CheckAutomatically);
    }
    [Fact] public async Task DownloadFailureNeverRestarts() {
        await using var f = new Fixture();
        var candidate = (await f.Client.CheckAsync(CheckOrigin.Manual)).Candidate!;
        f.Engine.Error = new IOException("File locked");
        await Assert.ThrowsAsync<IOException>(() => f.Client.InstallAsync(candidate));
        Assert.Equal(UpdateStage.Failed, f.Client.State.Stage);
        Assert.Equal(0, f.Engine.Applies);
    }
    [Fact] public async Task UnsupportedInstallDoesNotAccessNetwork() {
        await using var f = new Fixture(); f.Engine.IsSupported = false;
        Assert.Equal(CheckOutcomeKind.UnsupportedInstallation, (await f.Client.CheckAsync(CheckOrigin.Manual)).Kind);
        Assert.Equal(0, f.Engine.Checks);
    }
    [Fact] public async Task NoUpdateIsSilentForAutomaticChecks() {
        await using var f = new Fixture(); f.Engine.Candidate = null;
        var result = await f.Client.StartAutomaticCheckAsync();
        Assert.Equal(CheckOutcomeKind.UpToDate, result.Kind); Assert.False(result.ShouldPrompt);
    }
    [Fact] public async Task CorruptAndLockedPreferencesRecoverSafely() {
        string directory = Path.Combine(Path.GetTempPath(), "updater-test-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "updater.json");
        try {
            await File.WriteAllTextAsync(path, "bad json");
            var diagnostics = new List<string>();
            var store = new PreferencesStore(directory, diagnostics.Add);
            Assert.Equal(new UpdaterPreferences(), await store.LoadAsync(default));
            Assert.Single(diagnostics);
            await store.SaveAsync(new() { CheckAutomatically = false }, default);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(new(), default));
            Assert.False((await store.LoadAsync(default)).CheckAutomatically);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        } finally { Directory.Delete(directory, true); }
    }
}
