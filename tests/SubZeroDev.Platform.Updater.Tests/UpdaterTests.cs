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
        public string? CurrentVersion { get; set; } = "1.0.0";
        public bool VerifiesPackageSignatures { get; set; } = true;
        internal int Checks, Downloads, Applies;
        internal Exception? ApplyError, VerifyError;
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
        public void VerifyCanApply(UpdateCandidate candidate) { if (VerifyError is not null) throw VerifyError; }
        public void Apply(UpdateCandidate candidate) { if (ApplyError is not null) throw ApplyError; Applies++; }
        public void Dispose() { }
    }
    private sealed class Restart : IUpdateRestartCoordinator
    {
        internal RestartDecision Decision;
        internal int Requests, Aborts;
        internal Func<CancellationToken, Task<RestartDecision>>? Respond;
        public Task<RestartDecision> RequestRestartAsync(CancellationToken token) { Requests++; return Respond?.Invoke(token) ?? Task.FromResult(Decision); }
        public Task RestartAbortedAsync() { Aborts++; return Task.CompletedTask; }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Clock Clock = new();
        internal readonly Store Store = new();
        internal readonly Engine Engine = new();
        internal readonly Restart Restart = new();
        internal readonly UpdaterClient Client;
        internal Fixture(UpdaterPreferences? preferences = null, TimeSpan? shutdown = null) {
            Store.Value = preferences ?? new();
            Client = new(new("Example", new("https://github.com/example/app"), "unused"), Engine, Store, Restart, Store.Value, Clock) {
                ShutdownTimeout = shutdown ?? TimeSpan.FromSeconds(10)
            };
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
    [Fact] public async Task AutomaticConsentPromptsWhenPackagesCannotBeAuthenticated() {
        await using var f = new Fixture(new() { ConsentMode = ConsentMode.InstallAutomatically });
        f.Engine.VerifiesPackageSignatures = false;
        var result = await f.Client.StartAutomaticCheckAsync();
        Assert.True(result.ShouldPrompt);
        Assert.Equal("2.0.0", result.Candidate?.TargetVersion);
        Assert.Equal(0, f.Engine.Downloads);
        Assert.Equal(ConsentMode.InstallAutomatically, f.Store.Value.ConsentMode);
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
    [Fact] public async Task AutomaticInstallFailureIsReportedNotThrown() {
        await using var f = new Fixture(new() { ConsentMode = ConsentMode.InstallAutomatically });
        f.Restart.Decision = RestartDecision.Ready;
        f.Engine.ApplyError = new IOException("Update.exe locked");
        var result = await f.Client.StartAutomaticCheckAsync();
        Assert.Equal(CheckOutcomeKind.UpdateAvailable, result.Kind);
        Assert.True(result.ShouldPrompt);
        Assert.DoesNotContain("Update.exe", result.Message);
        Assert.Equal(UpdateStage.Failed, f.Client.State.Stage);
    }
    [Fact] public async Task AutomaticCheckWithStagedUpdateDoesNotInstallAnother() {
        await using var f = new Fixture(new() { ConsentMode = ConsentMode.InstallAutomatically });
        Assert.False((await f.Client.StartAutomaticCheckAsync()).ShouldPrompt);
        Assert.Equal(UpdateStage.AwaitingRestart, f.Client.State.Stage);
        f.Clock.Now += TimeSpan.FromMinutes(16);
        f.Engine.Candidate = CandidateFor("2.1.0");
        var result = await f.Client.StartAutomaticCheckAsync();
        Assert.False(result.ShouldPrompt);
        Assert.Equal(1, f.Engine.Downloads);
    }
    [Fact] public async Task ApplyFailureAfterReadyRestoresHostAndKeepsRetry() {
        await using var f = new Fixture();
        f.Restart.Decision = RestartDecision.Ready;
        f.Engine.ApplyError = new IOException("Update.exe locked");
        var candidate = (await f.Client.CheckAsync(CheckOrigin.Manual)).Candidate!;
        await Assert.ThrowsAsync<IOException>(() => f.Client.InstallAsync(candidate));
        Assert.Equal(1, f.Restart.Aborts);
        Assert.Equal(UpdateStage.Failed, f.Client.State.Stage);
        f.Engine.ApplyError = null;
        await f.Client.RetryPendingRestartAsync();
        Assert.Equal(1, f.Engine.Applies);
        Assert.Equal(1, f.Engine.Downloads);
        Assert.Equal(UpdateStage.Completed, f.Client.State.Stage);
    }
    [Fact] public async Task CancellationAfterReadyStillApplies() {
        await using var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        f.Restart.Respond = _ => { cancellation.Cancel(); return Task.FromResult(RestartDecision.Ready); };
        var candidate = (await f.Client.CheckAsync(CheckOrigin.Manual)).Candidate!;
        await f.Client.InstallAsync(candidate, cancellationToken: cancellation.Token);
        Assert.Equal(1, f.Engine.Applies);
        Assert.Equal(0, f.Restart.Aborts);
    }
    [Fact] public async Task FailingRestartRequestRestoresHost() {
        await using var f = new Fixture();
        f.Restart.Respond = _ => throw new InvalidOperationException("Tray dispose failed");
        var candidate = (await f.Client.CheckAsync(CheckOrigin.Manual)).Candidate!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Client.InstallAsync(candidate));
        Assert.Equal(1, f.Restart.Aborts);
        Assert.Equal(0, f.Engine.Applies);
    }
    [Fact] public async Task UnapplicableStagedPackageNeverQuiescesHost() {
        await using var f = new Fixture();
        f.Restart.Decision = RestartDecision.Ready;
        f.Engine.VerifyError = new FileNotFoundException("The staged update package is missing.");
        var candidate = (await f.Client.CheckAsync(CheckOrigin.Manual)).Candidate!;
        await Assert.ThrowsAsync<FileNotFoundException>(() => f.Client.InstallAsync(candidate));
        Assert.Equal(0, f.Restart.Requests);
        f.Engine.VerifyError = null;
        await f.Client.InstallAsync((await f.Client.CheckAsync(CheckOrigin.Manual)).Candidate!);
        Assert.Equal(2, f.Engine.Downloads);
        Assert.Equal(1, f.Engine.Applies);
    }
    [Fact] public async Task BookkeepingSaveFailureKeepsFoundUpdateAndThrottles() {
        await using var f = new Fixture(); f.Store.Fail = true;
        var result = await f.Client.StartAutomaticCheckAsync();
        Assert.Equal(CheckOutcomeKind.UpdateAvailable, result.Kind);
        Assert.True(result.ShouldPrompt);
        Assert.Equal(CheckOutcomeKind.RecentlyChecked, (await f.Client.StartAutomaticCheckAsync()).Kind);
        Assert.Equal(1, f.Engine.Checks);
    }
    [Fact] public async Task CheckDuringInstallReportsProgressInsteadOfWaiting() {
        await using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<RestartDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Restart.Respond = _ => { entered.TrySetResult(); return release.Task; };
        var candidate = (await f.Client.CheckAsync(CheckOrigin.Manual)).Candidate!;
        var install = f.Client.InstallAsync(candidate);
        await entered.Task;
        var result = await f.Client.CheckAsync(CheckOrigin.Manual).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CheckOutcomeKind.UpdateInProgress, result.Kind);
        Assert.Same(candidate, result.Candidate);
        Assert.False(result.ShouldPrompt);
        Assert.Equal(1, f.Engine.Checks);
        release.SetResult(RestartDecision.Defer);
        Assert.Equal(InstallOutcome.Deferred, await install);
    }
    [Fact] public async Task StagedUpdateIsNotReofferedAsAvailable() {
        await using var f = new Fixture();
        var candidate = (await f.Client.CheckAsync(CheckOrigin.Manual)).Candidate!;
        Assert.Equal(InstallOutcome.Deferred, await f.Client.InstallAsync(candidate));
        f.Engine.Candidate = CandidateFor("2.1.0");
        var manual = await f.Client.CheckAsync(CheckOrigin.Manual);
        Assert.Equal(CheckOutcomeKind.UpdateInProgress, manual.Kind);
        Assert.Same(candidate, manual.Candidate);
        Assert.True(manual.ShouldPrompt);
        Assert.False((await f.Client.StartAutomaticCheckAsync()).ShouldPrompt);
        Assert.Equal(1, f.Engine.Checks);
        Assert.Equal(UpdateStage.AwaitingRestart, f.Client.State.Stage);
        f.Restart.Decision = RestartDecision.Ready;
        Assert.Equal(InstallOutcome.RestartScheduled, await f.Client.InstallAsync(manual.Candidate!));
    }
    [Fact] public async Task InstallAndRetryReportWhatHappened() {
        await using var f = new Fixture();
        Assert.Equal(InstallOutcome.NoPendingUpdate, await f.Client.RetryPendingRestartAsync());
        var candidate = (await f.Client.CheckAsync(CheckOrigin.Manual)).Candidate!;
        Assert.Equal(InstallOutcome.Deferred, await f.Client.InstallAsync(candidate));
        Assert.Equal(InstallOutcome.Deferred, await f.Client.RetryPendingRestartAsync());
        f.Restart.Decision = RestartDecision.Ready;
        Assert.Equal(InstallOutcome.RestartScheduled, await f.Client.RetryPendingRestartAsync());
        Assert.Equal(InstallOutcome.RestartScheduled, await f.Client.RetryPendingRestartAsync());
        Assert.Equal(1, f.Engine.Applies);
    }
    [Fact] public async Task ScheduledInstallIsConfirmedByNextStart() {
        await using (var first = new Fixture()) {
            first.Restart.Decision = RestartDecision.Ready;
            await first.Client.InstallAsync((await first.Client.CheckAsync(CheckOrigin.Manual)).Candidate!);
            Assert.Equal("2.0.0", first.Store.Value.PendingInstallVersion);
        }
        await using var f = new Fixture(new() { PendingInstallVersion = "2.0.0" });
        f.Engine.CurrentVersion = "2.0.0";
        await f.Client.VerifyLastInstallAsync();
        Assert.Null(f.Store.Value.PendingInstallVersion);
        Assert.Equal(UpdateStage.Idle, f.Client.State.Stage);
    }
    [Fact] public async Task UpdateThatDidNotApplyIsReportedAndNotRetriedAutomatically() {
        await using var f = new Fixture(new() { PendingInstallVersion = "2.0.0", ConsentMode = ConsentMode.InstallAutomatically });
        await f.Client.VerifyLastInstallAsync();
        Assert.Equal(UpdateStage.Failed, f.Client.State.Stage);
        Assert.Null(f.Store.Value.PendingInstallVersion);
        var result = await f.Client.StartAutomaticCheckAsync();
        Assert.False(result.ShouldPrompt);
        Assert.Equal(0, f.Engine.Downloads);
        Assert.True((await f.Client.CheckAsync(CheckOrigin.Manual)).ShouldPrompt);
    }
    [Fact] public async Task DisposalDoesNotHangOnUnresponsiveHost() {
        var f = new Fixture(shutdown: TimeSpan.FromMilliseconds(200));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Restart.Respond = _ => { entered.TrySetResult(); return new TaskCompletionSource<RestartDecision>().Task; };
        var candidate = (await f.Client.CheckAsync(CheckOrigin.Manual)).Candidate!;
        _ = f.Client.InstallAsync(candidate);
        await entered.Task;
        await f.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<ObjectDisposedException>(() => { _ = f.Client.InstallAsync(candidate); });
        Assert.Throws<ObjectDisposedException>(() => { _ = f.Client.RetryPendingRestartAsync(); });
    }
    [Fact] public async Task UsersOnlySeeFixedMessagesNeverExceptionText() {
        await using var f = new Fixture();
        f.Engine.Error = new IOException(@"C:\Users\someone\secret\updater.json is locked");
        var check = await f.Client.CheckAsync(CheckOrigin.Manual);
        Assert.DoesNotContain("secret", check.Message);
        Assert.DoesNotContain("secret", f.Client.State.Message);
        f.Engine.Error = null;
        var candidate = (await f.Client.CheckAsync(CheckOrigin.Manual)).Candidate!;
        f.Engine.ApplyError = new IOException(@"C:\Users\someone\secret\Update.exe");
        f.Restart.Decision = RestartDecision.Ready;
        await Assert.ThrowsAsync<IOException>(() => f.Client.InstallAsync(candidate));
        Assert.Equal(UpdateStage.Failed, f.Client.State.Stage);
        Assert.DoesNotContain("secret", f.Client.State.Message);
    }
    [Fact] public async Task MissingRepositoryIsReportedAsNotFound() {
        await using var f = new Fixture();
        f.Engine.Error = new HttpRequestException("Not found", null, HttpStatusCode.NotFound);
        var result = await f.Client.CheckAsync(CheckOrigin.Manual);
        Assert.Equal(CheckOutcomeKind.NetworkUnavailable, result.Kind);
        Assert.Contains("not found", result.Message);
    }
    [Fact] public async Task RateLimitBacksOffAutomaticChecksForRetryAfterButNotManualOnes() {
        await using var f = new Fixture();
        f.Engine.Error = new RateLimitedException("limited", HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(45));
        Assert.Equal(CheckOutcomeKind.RateLimited, (await f.Client.StartAutomaticCheckAsync()).Kind);
        f.Clock.Now += TimeSpan.FromMinutes(16);
        Assert.Equal(CheckOutcomeKind.RecentlyChecked, (await f.Client.StartAutomaticCheckAsync()).Kind);
        Assert.Equal(1, f.Engine.Checks);
        Assert.Equal(CheckOutcomeKind.RateLimited, (await f.Client.CheckAsync(CheckOrigin.Manual)).Kind);
        f.Engine.Error = null;
        f.Clock.Now += TimeSpan.FromMinutes(46);
        Assert.Equal(CheckOutcomeKind.UpdateAvailable, (await f.Client.StartAutomaticCheckAsync()).Kind);
    }
    [Fact] public async Task RateLimitWithoutRetryAfterStillWaitsTheNormalInterval() {
        await using var f = new Fixture();
        f.Engine.Error = new RateLimitedException("limited", HttpStatusCode.Forbidden, null);
        await f.Client.StartAutomaticCheckAsync();
        f.Clock.Now += TimeSpan.FromMinutes(14);
        Assert.Equal(CheckOutcomeKind.RecentlyChecked, (await f.Client.StartAutomaticCheckAsync()).Kind);
        f.Clock.Now += TimeSpan.FromMinutes(2);
        f.Engine.Error = null;
        Assert.Equal(CheckOutcomeKind.UpdateAvailable, (await f.Client.StartAutomaticCheckAsync()).Kind);
    }
    [Fact] public async Task OnlyRecentOffersStayInstallable() {
        await using var f = new Fixture();
        var first = (await f.Client.CheckAsync(CheckOrigin.Manual)).Candidate!;
        for (int i = 0; i < 4; i++) { f.Engine.Candidate = CandidateFor("2.0." + (i + 1)); await f.Client.CheckAsync(CheckOrigin.Manual); }
        await Assert.ThrowsAsync<ArgumentException>(() => f.Client.DeferAsync(first));
        await f.Client.DeferAsync(f.Engine.Candidate!);
    }
    [Fact] public async Task GitSuffixedRepositoryIsRejected() {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<ArgumentException>(() => UpdaterClient.CreateAsync(new("Example", new("https://github.com/example/app.git"), directory), new Restart()));
        Assert.False(Directory.Exists(directory));
    }
    [Fact] public async Task NewerPreferencesFileIsReadLeniently() {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "updater.json");
        try {
            await File.WriteAllTextAsync(path, """{"schemaVersion":2,"checkAutomatically":false,"channel":"preview","consentMode":"someFutureMode","futureField":{"a":1}}""");
            var store = new PreferencesStore(directory, null);
            var loaded = await store.LoadAsync(default);
            Assert.Equal(2, loaded.SchemaVersion);
            Assert.False(loaded.CheckAutomatically);
            Assert.Equal(UpdateChannel.Preview, loaded.Channel);
            Assert.Equal(ConsentMode.ConfirmEachUpdate, loaded.ConsentMode);
            await store.SaveAsync(loaded with { Channel = UpdateChannel.Stable }, default);
            var saved = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            Assert.Equal(2, (int)saved["schemaVersion"]!);
            Assert.Equal(1, (int)saved["futureField"]!["a"]!);
            Assert.Equal("stable", (string)saved["channel"]!);
        } finally { Directory.Delete(directory, true); }
    }
    [Fact] public async Task UnknownFieldsInCurrentSchemaSurviveSaves() {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "updater.json");
        try {
            await File.WriteAllTextAsync(path, """{"schemaVersion":1,"extra":"keep"}""");
            var store = new PreferencesStore(directory, null);
            await store.SaveAsync(await store.LoadAsync(default) with { CheckAutomatically = false }, default);
            Assert.Contains("\"extra\"", await File.ReadAllTextAsync(path));
        } finally { Directory.Delete(directory, true); }
    }
}
