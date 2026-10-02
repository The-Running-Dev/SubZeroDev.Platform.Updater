namespace SubZeroDev.Platform.Updater;

internal interface IUpdateEngine : IDisposable
{
    bool IsSupported { get; }
    Task<UpdateCandidate?> CheckAsync(UpdateChannel channel, CancellationToken token);
    Task DownloadAsync(UpdateCandidate candidate, Action<int> progress, CancellationToken token);
    void VerifyCanApply(UpdateCandidate candidate);
    void Apply(UpdateCandidate candidate);
}

/// <summary>Default updater implementation. Create one instance per application process.</summary>
public sealed class UpdaterClient : IUpdaterClient
{
    private readonly UpdaterOptions options;
    private readonly IUpdateEngine engine;
    private readonly IPreferencesStore store;
    private readonly IUpdateRestartCoordinator restart;
    private readonly TimeProvider clock;
    private readonly Action<string>? log;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly SemaphoreSlim preferencesGate = new(1, 1);
    private readonly object sync = new();
    private Task<CheckResult>? checking;
    private Task? installing;
    private UpdateCandidate? installingCandidate;
    private bool disposed;
    private readonly HashSet<UpdateCandidate> candidates = [];
    private UpdateCandidate? pending;
    private UpdateCandidate? applied;
    private UpdaterPreferences preferences;
    private UpdaterState state = new(UpdateStage.Idle);

    internal UpdaterClient(UpdaterOptions options, IUpdateEngine engine, IPreferencesStore store, IUpdateRestartCoordinator restart,
        UpdaterPreferences preferences, TimeProvider? clock = null, Action<string>? log = null)
        => (this.options, this.engine, this.store, this.restart, this.preferences, this.clock, this.log) =
            (options, engine, store, restart, preferences, clock ?? TimeProvider.System, log);

    /// <summary>Load preferences and create a public GitHub updater. Logging is optional and contains no credentials.</summary>
    public static async Task<UpdaterClient> CreateAsync(UpdaterOptions options, IUpdateRestartCoordinator restartCoordinator,
        Action<string>? diagnostic = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(restartCoordinator);
        if (string.IsNullOrWhiteSpace(options.AppId) || string.IsNullOrWhiteSpace(options.SettingsDirectory)) throw new ArgumentException("AppId and SettingsDirectory are required.");
        if (!options.PublicReleaseRepository.IsAbsoluteUri || options.PublicReleaseRepository.Scheme != "https" ||
            options.PublicReleaseRepository.Host != "github.com" || options.PublicReleaseRepository.AbsolutePath.Trim('/').Split('/').Length != 2 ||
            options.PublicReleaseRepository.UserInfo.Length != 0 || options.PublicReleaseRepository.Query.Length != 0 || options.PublicReleaseRepository.Fragment.Length != 0)
            throw new ArgumentException("Use a public https://github.com/owner/repository URL.");
        if (options.MinimumAutomaticCheckInterval < TimeSpan.FromMinutes(15) || options.NetworkTimeout <= TimeSpan.Zero || options.NetworkTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(options));
        var store = new PreferencesStore(options.SettingsDirectory, diagnostic);
        var preferences = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
#if UPDATER_LOCAL_VALIDATION
        // Compiled only into the local smoke-test package. Never enable for release builds.
        if (Environment.GetEnvironmentVariable("UPDATER_VALIDATION_FEED") is { Length: > 0 } feed)
            return new(options, new VelopackEngine(options, sourceFactory: _ => new Velopack.Sources.SimpleFileSource(new(feed)), log: diagnostic), store, restartCoordinator, preferences, log: diagnostic);
#endif
        return new(options, new VelopackEngine(options, log: diagnostic), store, restartCoordinator, preferences, log: diagnostic);
    }

    /// <inheritdoc />
    public UpdaterPreferences Preferences => Volatile.Read(ref preferences);
    /// <inheritdoc />
    public UpdaterState State => Volatile.Read(ref state);
    /// <inheritdoc />
    public event EventHandler<UpdaterState>? StateChanged;

    private void Publish(UpdaterState next)
    {
        Volatile.Write(ref state, next);
        Log($"stage={next.Stage} channel={next.Candidate?.Channel ?? Preferences.Channel} version={next.Candidate?.TargetVersion ?? "none"}");
        foreach (EventHandler<UpdaterState> handler in StateChanged?.GetInvocationList() ?? []) {
            try { handler(this, next); } catch (Exception ex) { Log($"category=subscriber-failure type={ex.GetType().Name}"); }
        }
    }

    private void Log(string message)
    {
        try { log?.Invoke($"{clock.GetUtcNow():O} {message}"); } catch { /* Diagnostics cannot fail an update. */ }
    }

    /// <inheritdoc />
    public Task<CheckResult> StartAutomaticCheckAsync(CancellationToken cancellationToken = default) => CheckAsync(CheckOrigin.Automatic, cancellationToken);

    /// <inheritdoc />
    public async Task<CheckResult> CheckAsync(CheckOrigin origin, CancellationToken cancellationToken = default)
    {
        Task<CheckResult> task;
        lock (sync) {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (checking is { IsCompleted: false }) task = checking;
            else {
                if (origin == CheckOrigin.Automatic) {
                    if (!Preferences.CheckAutomatically) return new(CheckOutcomeKind.AutomaticCheckDisabled);
                    if (Preferences.LastAutomaticNetworkCheckUtc is { } last && clock.GetUtcNow() - last < options.MinimumAutomaticCheckInterval)
                        return new(CheckOutcomeKind.RecentlyChecked);
                }
                task = checking = Task.Run(() => CheckCoreAsync(origin, lifetime.Token), CancellationToken.None);
            }
        }
        try {
            var result = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            bool prompt = result.Candidate is { } c && (origin == CheckOrigin.Manual || Preferences.LastOfferedVersion != c.TargetVersion || Preferences.OfferDeferredUntilUtc <= clock.GetUtcNow() || Preferences.OfferDeferredUntilUtc is null);
            if (origin == CheckOrigin.Automatic && result.Candidate is { } candidate && prompt && Preferences.ConsentMode == ConsentMode.InstallAutomatically) {
                // A staged or applied update finishes first; a newer candidate is offered again after restart.
                if (Volatile.Read(ref applied) is not null || Volatile.Read(ref pending) is not null) prompt = false;
                else {
                    // Automatic installs run from host startup; failures are reported in the result, never thrown to the host.
                    try {
                        await InstallAsync(candidate, cancellationToken: cancellationToken).ConfigureAwait(false);
                        prompt = false;
                    } catch (Exception ex) when (ex is not OperationCanceledException) {
                        Log($"category=automatic-install-failure type={ex.GetType().Name}");
                        return result with { ShouldPrompt = true, Message = "The update could not be installed automatically. Try installing it manually." };
                    }
                }
            }
            return result with { ShouldPrompt = prompt };
        } catch (OperationCanceledException) { return new(CheckOutcomeKind.Cancelled); }
    }

    private async Task<CheckResult> CheckCoreAsync(CheckOrigin origin, CancellationToken token)
    {
        await operations.WaitAsync(token).ConfigureAwait(false);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Log($"check origin={origin} channel={Preferences.Channel}");
        try {
            if (!engine.IsSupported) {
                Publish(new(UpdateStage.UnsupportedInstallation));
                return new(CheckOutcomeKind.UnsupportedInstallation, Message: "Install or extract a Velopack distribution to enable updates.");
            }
            Publish(new(UpdateStage.Checking));
            if (origin == CheckOrigin.Automatic) await ChangePreferencesAsync(p => p with { LastAutomaticNetworkCheckUtc = clock.GetUtcNow() }, token).ConfigureAwait(false);
            var candidate = await engine.CheckAsync(Preferences.Channel, token).ConfigureAwait(false);
            await ChangePreferencesAsync(p => p with { LastSuccessfulCheckUtc = clock.GetUtcNow() }, token).ConfigureAwait(false);
            if (candidate is not null) lock (sync) candidates.Add(candidate);
            Publish(pending is not null ? new(UpdateStage.AwaitingRestart, pending) : new(candidate is null ? UpdateStage.Idle : UpdateStage.UpdateAvailable, candidate));
            return new(candidate is null ? CheckOutcomeKind.UpToDate : CheckOutcomeKind.UpdateAvailable, candidate);
        } catch (OperationCanceledException) { Publish(new(pending is null ? UpdateStage.Idle : UpdateStage.AwaitingRestart, pending)); return new(CheckOutcomeKind.Cancelled); }
        catch (Exception ex) {
            var kind = ex switch {
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests } => CheckOutcomeKind.RateLimited,
                HttpRequestException or TimeoutException => CheckOutcomeKind.NetworkUnavailable,
                _ => CheckOutcomeKind.InvalidRelease
            };
            Publish(new(UpdateStage.Failed, Message: ex.Message));
            Log($"category={kind} type={ex.GetType().Name}");
            return new(kind, Message: ex.Message);
        } finally { Log($"check-finished origin={origin} durationMs={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}"); operations.Release(); }
    }

    /// <inheritdoc />
    public Task SavePreferencesAsync(UpdaterPreferences value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (sync) ObjectDisposedException.ThrowIf(disposed, this);
        if (!Enum.IsDefined(value.Channel) || !Enum.IsDefined(value.ConsentMode)) throw new ArgumentException("Invalid preferences.");
        return ChangePreferencesAsync(p => {
            if (value.ConsentMode == ConsentMode.InstallAutomatically && p.ConsentMode != ConsentMode.InstallAutomatically)
                throw new InvalidOperationException("Automatic installation consent must accompany an explicit install.");
            return p with { CheckAutomatically = value.CheckAutomatically, Channel = value.Channel, ConsentMode = value.ConsentMode };
        }, cancellationToken);
    }

    private async Task ChangePreferencesAsync(Func<UpdaterPreferences, UpdaterPreferences> change, CancellationToken token)
    {
        await preferencesGate.WaitAsync(token).ConfigureAwait(false);
        try {
            var next = change(Preferences);
            await store.SaveAsync(next, token).ConfigureAwait(false);
            Volatile.Write(ref preferences, next);
        } finally { preferencesGate.Release(); }
    }

    /// <inheritdoc />
    public Task DeferAsync(UpdateCandidate candidate, CancellationToken cancellationToken = default)
    {
        ValidateCandidate(candidate);
        return ChangePreferencesAsync(p => p with { LastOfferedVersion = candidate.TargetVersion, OfferDeferredUntilUtc = clock.GetUtcNow().AddHours(24) }, cancellationToken);
    }

    private void ValidateCandidate(UpdateCandidate candidate)
    {
        lock (sync) {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!candidates.Contains(candidate)) throw new ArgumentException("Candidate was not offered by this client.", nameof(candidate));
        }
    }

    /// <inheritdoc />
    public Task InstallAsync(UpdateCandidate candidate, bool rememberAutomaticConsent = false, CancellationToken cancellationToken = default)
    {
        ValidateCandidate(candidate);
        lock (sync) {
            if (installing is { IsCompleted: false }) {
                if (!ReferenceEquals(candidate, installingCandidate)) throw new InvalidOperationException("A different candidate is already being installed.");
                return installing.WaitAsync(cancellationToken);
            }
            installingCandidate = candidate;
            installing = Task.Run(() => InstallCoreAsync(candidate, rememberAutomaticConsent, cancellationToken), CancellationToken.None);
            return installing;
        }
    }

    private async Task InstallCoreAsync(UpdateCandidate candidate, bool remember, CancellationToken caller)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, lifetime.Token);
        var token = linked.Token;
        await operations.WaitAsync(token).ConfigureAwait(false);
        try {
            if (ReferenceEquals(candidate, applied)) return;
            if (applied is not null) throw new InvalidOperationException("Exit the application to finish applying the update.");
            if (pending is not null) {
                if (!ReferenceEquals(candidate, pending)) throw new InvalidOperationException("Finish the staged update before installing another candidate.");
                await RestartCoreAsync(token).ConfigureAwait(false);
                return;
            }
            if (remember) await ChangePreferencesAsync(p => p with { ConsentMode = ConsentMode.InstallAutomatically }, token).ConfigureAwait(false);
            Publish(new(UpdateStage.Downloading, candidate, 0));
            await engine.DownloadAsync(candidate, p => Publish(new(UpdateStage.Downloading, candidate, Math.Clamp(p, 0, 100))), token).ConfigureAwait(false);
            pending = candidate;
            Publish(new(UpdateStage.AwaitingRestart, candidate));
            await RestartCoreAsync(token).ConfigureAwait(false);
        } catch (OperationCanceledException) { Publish(new(pending is null ? UpdateStage.Idle : UpdateStage.AwaitingRestart, pending)); throw; }
        catch (Exception ex) { Publish(new(UpdateStage.Failed, candidate, Message: ex.Message)); throw; }
        finally { operations.Release(); }
    }

    /// <inheritdoc />
    public async Task RetryPendingRestartAsync(CancellationToken cancellationToken = default)
    {
        lock (sync) ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await operations.WaitAsync(linked.Token).ConfigureAwait(false);
        try { await RestartCoreAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Publish(new(UpdateStage.Failed, pending, Message: ex.Message)); throw; }
        finally { operations.Release(); }
    }

    private async Task RestartCoreAsync(CancellationToken token)
    {
        if (pending is not { } candidate) return;
        try { engine.VerifyCanApply(candidate); }
        catch {
            // A staged package that cannot be applied is dropped so a later install downloads it again.
            pending = null;
            throw;
        }
        RestartDecision decision;
        try { decision = await restart.RequestRestartAsync(token).ConfigureAwait(false); }
        catch { await RestoreHostAsync().ConfigureAwait(false); throw; }
        if (decision == RestartDecision.Defer) return;
        // The host has released its resources, so cancellation no longer applies: apply, or give the host back its resources.
        Publish(new(UpdateStage.Applying, candidate));
        try { engine.Apply(candidate); }
        catch { await RestoreHostAsync().ConfigureAwait(false); throw; }
        applied = candidate;
        pending = null;
        Publish(new(UpdateStage.Completed, candidate));
    }

    private async Task RestoreHostAsync()
    {
        try { await restart.RestartAbortedAsync().ConfigureAwait(false); }
        catch (Exception ex) { Log($"category=host-restore-failure type={ex.GetType().Name}"); }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task? check; Task? install;
        lock (sync) { if (disposed) return; disposed = true; check = checking; install = installing; }
        await lifetime.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(check ?? Task.CompletedTask, install ?? Task.CompletedTask).ConfigureAwait(false); }
        catch (Exception ex) { Log($"category=shutdown type={ex.GetType().Name}"); }
        // Also join a separately requested pending restart before disposing the engine.
        await operations.WaitAsync().ConfigureAwait(false);
        engine.Dispose();
        operations.Release();
        lifetime.Dispose();
    }
}
