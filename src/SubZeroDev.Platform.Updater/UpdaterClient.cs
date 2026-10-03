namespace SubZeroDev.Platform.Updater;

internal interface IUpdateEngine : IDisposable
{
    bool IsSupported { get; }
    string? CurrentVersion { get; }
    bool VerifiesPackageSignatures { get; }
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
    private Task<InstallOutcome>? installing;
    private UpdateCandidate? installingCandidate;
    private bool disposed;
    private DateTimeOffset? lastAutomaticAttempt;
    // Only recent offers stay valid, so a long-lived host does not keep every checked release alive.
    private const int MaximumRememberedCandidates = 4;
    private readonly List<UpdateCandidate> candidates = [];
    private DateTimeOffset? automaticBackoffUntil;
    private UpdateCandidate? pending;
    private UpdateCandidate? applied;
    private UpdaterPreferences preferences;
    private UpdaterState state = new(UpdateStage.Idle);

    internal UpdaterClient(UpdaterOptions options, IUpdateEngine engine, IPreferencesStore store, IUpdateRestartCoordinator restart,
        UpdaterPreferences preferences, TimeProvider? clock = null, Action<string>? log = null)
        => (this.options, this.engine, this.store, this.restart, this.preferences, this.clock, this.log) =
            (options, engine, store, restart, preferences, clock ?? TimeProvider.System, log);

    /// <summary>The longest disposal waits for outstanding work before abandoning it.</summary>
    internal TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Load preferences and create a public GitHub updater. Logging is optional and contains no credentials.</summary>
    public static async Task<UpdaterClient> CreateAsync(UpdaterOptions options, IUpdateRestartCoordinator restartCoordinator,
        Action<string>? diagnostic = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(restartCoordinator);
        if (string.IsNullOrWhiteSpace(options.AppId) || string.IsNullOrWhiteSpace(options.SettingsDirectory)) throw new ArgumentException("AppId and SettingsDirectory are required.");
        if (!options.PublicReleaseRepository.IsAbsoluteUri || options.PublicReleaseRepository.Scheme != "https" ||
            options.PublicReleaseRepository.Host != "github.com" || options.PublicReleaseRepository.AbsolutePath.Trim('/').Split('/').Length != 2 ||
            options.PublicReleaseRepository.UserInfo.Length != 0 ||
            options.PublicReleaseRepository.AbsolutePath.TrimEnd('/').EndsWith(".git", StringComparison.OrdinalIgnoreCase) || options.PublicReleaseRepository.Query.Length != 0 || options.PublicReleaseRepository.Fragment.Length != 0)
            throw new ArgumentException("Use a public https://github.com/owner/repository URL.");
        if (options.MinimumAutomaticCheckInterval < TimeSpan.FromMinutes(15) || options.NetworkTimeout <= TimeSpan.Zero || options.NetworkTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(options));
        var store = new PreferencesStore(options.SettingsDirectory, diagnostic);
        var preferences = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        IUpdateEngine? engine = null;
#if UPDATER_LOCAL_VALIDATION
        // Compiled only into the local smoke-test package. Never enable for release builds.
        if (Environment.GetEnvironmentVariable("UPDATER_VALIDATION_FEED") is { Length: > 0 } feed)
            engine = new VelopackEngine(options, sourceFactory: _ => new SignedFileSource(new(feed)), log: diagnostic);
#endif
        engine ??= new VelopackEngine(options, log: diagnostic);
        var client = new UpdaterClient(options, engine, store, restartCoordinator, preferences, log: diagnostic);
        await client.VerifyLastInstallAsync().ConfigureAwait(false);
        return client;
    }

    /// <summary>Confirms that an update scheduled by the previous process is now running.</summary>
    internal async Task VerifyLastInstallAsync()
    {
        if (Preferences.PendingInstallVersion is not { } expected || engine.CurrentVersion is not { } current) return;
        if (string.Equals(current, expected, StringComparison.OrdinalIgnoreCase)) {
            Log($"update-verified version={expected}");
            await RecordAsync(p => p with { PendingInstallVersion = null }, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        // The apply step failed after this process exited. Report it, and stop automatic installs retrying it in a loop.
        Log($"category=update-not-applied expected={expected} current={current}");
        Volatile.Write(ref state, new(UpdateStage.Failed, Message: "The last update could not be applied. The current version is still running."));
        await RecordAsync(p => p with { PendingInstallVersion = null, LastOfferedVersion = expected, OfferDeferredUntilUtc = clock.GetUtcNow().AddHours(24) },
            CancellationToken.None).ConfigureAwait(false);
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
                    // The in-memory attempt still throttles when the persisted one could not be saved.
                    var last = Preferences.LastAutomaticNetworkCheckUtc is { } saved && (lastAutomaticAttempt is null || saved > lastAutomaticAttempt) ? saved : lastAutomaticAttempt;
                    if (last is { } attempt && clock.GetUtcNow() - attempt < options.MinimumAutomaticCheckInterval)
                        return new(CheckOutcomeKind.RecentlyChecked);
                    // GitHub asked clients to wait; manual checks still go through.
                    if (automaticBackoffUntil is { } until && clock.GetUtcNow() < until) return new(CheckOutcomeKind.RecentlyChecked);
                }
                task = checking = Task.Run(() => CheckCoreAsync(origin, lifetime.Token), CancellationToken.None);
            }
        }
        try {
            var result = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            // A staged update the host deferred can be offered again by a manual check; one being installed cannot.
            if (result.Kind == CheckOutcomeKind.UpdateInProgress) return result with { ShouldPrompt = origin == CheckOrigin.Manual && result.ShouldPrompt };
            if (result.Kind != CheckOutcomeKind.UpdateAvailable || result.Candidate is not { } candidate) return result;
            bool prompt = origin == CheckOrigin.Manual || Preferences.LastOfferedVersion != candidate.TargetVersion || Preferences.OfferDeferredUntilUtc <= clock.GetUtcNow() || Preferences.OfferDeferredUntilUtc is null;
            if (origin == CheckOrigin.Automatic && prompt && Preferences.ConsentMode == ConsentMode.InstallAutomatically) {
                // A staged or applied update finishes first; a newer candidate is offered again after restart.
                if (Volatile.Read(ref applied) is not null || Volatile.Read(ref pending) is not null) prompt = false;
                // Without a pinned publisher key, anyone able to write releases could install silently; ask instead.
                else if (!engine.VerifiesPackageSignatures) Log("category=automatic-install-requires-signing-key");
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
        // Never queue behind an install or restart: report it instead of leaving the caller waiting on the host.
        if (!operations.Wait(0)) {
            UpdateCandidate? current;
            lock (sync) current = installingCandidate;
            return new(CheckOutcomeKind.UpdateInProgress, current, "An update is being installed.");
        }
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Log($"check origin={origin} channel={Preferences.Channel}");
        try {
            // A different update is never offered while one is staged or scheduled.
            if (applied is { } done) return new(CheckOutcomeKind.UpdateInProgress, done, "Exit the application to finish applying the update.");
            if (pending is { } staged) return new(CheckOutcomeKind.UpdateInProgress, staged, "An update is ready to install when the application restarts.", ShouldPrompt: true);
            if (!engine.IsSupported) {
                Publish(new(UpdateStage.UnsupportedInstallation));
                return new(CheckOutcomeKind.UnsupportedInstallation, Message: "Install or extract a Velopack distribution to enable updates.");
            }
            Publish(new(UpdateStage.Checking));
            if (origin == CheckOrigin.Automatic) {
                var now = clock.GetUtcNow();
                lock (sync) lastAutomaticAttempt = now;
                await RecordAsync(p => p with { LastAutomaticNetworkCheckUtc = now }, token).ConfigureAwait(false);
            }
            var candidate = await engine.CheckAsync(Preferences.Channel, token).ConfigureAwait(false);
            lock (sync) automaticBackoffUntil = null;
            await RecordAsync(p => p with { LastSuccessfulCheckUtc = clock.GetUtcNow() }, token).ConfigureAwait(false);
            if (candidate is not null) lock (sync) {
                candidates.Add(candidate);
                if (candidates.Count > MaximumRememberedCandidates) candidates.RemoveAt(0);
            }
            Publish(new(candidate is null ? UpdateStage.Idle : UpdateStage.UpdateAvailable, candidate));
            return new(candidate is null ? CheckOutcomeKind.UpToDate : CheckOutcomeKind.UpdateAvailable, candidate);
        } catch (OperationCanceledException) { Publish(new(UpdateStage.Idle)); return new(CheckOutcomeKind.Cancelled); }
        catch (Exception ex) {
            var kind = ex switch {
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests } => CheckOutcomeKind.RateLimited,
                RepositoryNotFoundException => CheckOutcomeKind.RepositoryNotFound,
                HttpRequestException or TimeoutException => CheckOutcomeKind.NetworkUnavailable,
                _ => CheckOutcomeKind.InvalidRelease
            };
            if (kind == CheckOutcomeKind.RateLimited) {
                // Honour Retry-After, but never wait less than the normal interval or more than an hour.
                var wait = TimeSpan.FromTicks(Math.Clamp((ex as RateLimitedException)?.RetryAfter?.Ticks ?? 0, options.MinimumAutomaticCheckInterval.Ticks, TimeSpan.FromHours(1).Ticks));
                lock (sync) automaticBackoffUntil = clock.GetUtcNow() + wait;
            }
            // Exception text can carry local paths and server detail, so users only see these fixed messages.
            var message = kind switch {
                CheckOutcomeKind.RateLimited => "GitHub is limiting requests right now. Try again later.",
                CheckOutcomeKind.RepositoryNotFound => "The release repository was not found. It may have been renamed or made private.",
                CheckOutcomeKind.NetworkUnavailable => "GitHub could not be reached. Check your connection and try again.",
                _ => "The latest release could not be verified, so it was not used."
            };
            Publish(new(UpdateStage.Failed, Message: message));
            Log($"category={kind} type={ex.GetType().Name}");
            return new(kind, Message: message);
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

    // Bookkeeping must never cost the user an update, so a failed save is logged rather than thrown.
    private async Task RecordAsync(Func<UpdaterPreferences, UpdaterPreferences> change, CancellationToken token)
    {
        try { await ChangePreferencesAsync(change, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log($"category=preferences-save-failure type={ex.GetType().Name}"); }
    }

    /// <inheritdoc />
    public Task DeferAsync(UpdateCandidate candidate, CancellationToken cancellationToken = default)
    {
        lock (sync) ValidateCandidate(candidate);
        return ChangePreferencesAsync(p => p with { LastOfferedVersion = candidate.TargetVersion, OfferDeferredUntilUtc = clock.GetUtcNow().AddHours(24) }, cancellationToken);
    }

    private void ValidateCandidate(UpdateCandidate candidate)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!candidates.Contains(candidate) && !ReferenceEquals(candidate, pending) && !ReferenceEquals(candidate, applied) && !ReferenceEquals(candidate, installingCandidate))
            throw new ArgumentException("Candidate was not offered by this client.", nameof(candidate));
    }

    /// <inheritdoc />
    public Task<InstallOutcome> InstallAsync(UpdateCandidate candidate, bool rememberAutomaticConsent = false, CancellationToken cancellationToken = default)
    {
        // One lock with the disposed check, so disposal always sees and joins the install it lets start.
        lock (sync) {
            ValidateCandidate(candidate);
            if (installing is { IsCompleted: false }) {
                if (!ReferenceEquals(candidate, installingCandidate)) throw new InvalidOperationException("A different candidate is already being installed.");
                return installing.WaitAsync(cancellationToken);
            }
            installingCandidate = candidate;
            return installing = Task.Run(() => InstallCoreAsync(candidate, rememberAutomaticConsent, cancellationToken), CancellationToken.None);
        }
    }

    private async Task<InstallOutcome> InstallCoreAsync(UpdateCandidate candidate, bool remember, CancellationToken caller)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, lifetime.Token);
        var token = linked.Token;
        await operations.WaitAsync(token).ConfigureAwait(false);
        try {
            if (ReferenceEquals(candidate, applied)) return InstallOutcome.RestartScheduled;
            if (applied is not null) throw new InvalidOperationException("Exit the application to finish applying the update.");
            if (pending is not null) {
                if (!ReferenceEquals(candidate, pending)) throw new InvalidOperationException("Finish the staged update before installing another candidate.");
                return await RestartCoreAsync(token).ConfigureAwait(false);
            }
            if (remember) await ChangePreferencesAsync(p => p with { ConsentMode = ConsentMode.InstallAutomatically }, token).ConfigureAwait(false);
            Publish(new(UpdateStage.Downloading, candidate, 0));
            await engine.DownloadAsync(candidate, p => Publish(new(UpdateStage.Downloading, candidate, Math.Clamp(p, 0, 100))), token).ConfigureAwait(false);
            pending = candidate;
            Publish(new(UpdateStage.AwaitingRestart, candidate));
            return await RestartCoreAsync(token).ConfigureAwait(false);
        } catch (OperationCanceledException) { Publish(new(pending is null ? UpdateStage.Idle : UpdateStage.AwaitingRestart, pending)); throw; }
        catch (Exception) { Publish(new(UpdateStage.Failed, candidate, Message: InstallFailure)); throw; }
        finally { operations.Release(); }
    }

    private const string InstallFailure = "The update could not be installed. The current version is still running.";

    /// <inheritdoc />
    public Task<InstallOutcome> RetryPendingRestartAsync(CancellationToken cancellationToken = default)
    {
        // Runs in the install slot so it joins an install in progress and disposal joins it.
        lock (sync) {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (installing is { IsCompleted: false }) return installing.WaitAsync(cancellationToken);
            installingCandidate = Volatile.Read(ref pending);
            return installing = Task.Run(() => RetryCoreAsync(cancellationToken), CancellationToken.None);
        }
    }

    private async Task<InstallOutcome> RetryCoreAsync(CancellationToken caller)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, lifetime.Token);
        await operations.WaitAsync(linked.Token).ConfigureAwait(false);
        try { return await RestartCoreAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { Publish(new(UpdateStage.Failed, pending, Message: InstallFailure)); throw; }
        finally { operations.Release(); }
    }

    private async Task<InstallOutcome> RestartCoreAsync(CancellationToken token)
    {
        if (pending is not { } candidate) return applied is null ? InstallOutcome.NoPendingUpdate : InstallOutcome.RestartScheduled;
        try { engine.VerifyCanApply(candidate); }
        catch {
            // A staged package that cannot be applied is dropped so a later install downloads it again.
            pending = null;
            throw;
        }
        RestartDecision decision;
        try { decision = await restart.RequestRestartAsync(token).ConfigureAwait(false); }
        catch { await RestoreHostAsync().ConfigureAwait(false); throw; }
        if (decision == RestartDecision.Defer) return InstallOutcome.Deferred;
        // The host has released its resources, so cancellation no longer applies: apply, or give the host back its resources.
        Publish(new(UpdateStage.Applying, candidate));
        try { engine.Apply(candidate); }
        catch { await RestoreHostAsync().ConfigureAwait(false); throw; }
        applied = candidate;
        pending = null;
        // Checked by the next process, so an apply that fails after this one exits is reported rather than lost.
        await RecordAsync(p => p with { PendingInstallVersion = candidate.TargetVersion }, CancellationToken.None).ConfigureAwait(false);
        Publish(new(UpdateStage.Completed, candidate));
        return InstallOutcome.RestartScheduled;
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
        // Bounded, because a host that never answers RequestRestartAsync must not hang its own shutdown.
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var work = Task.WhenAll(check ?? Task.CompletedTask, install ?? Task.CompletedTask);
        try { await work.WaitAsync(ShutdownTimeout).ConfigureAwait(false); }
        catch (TimeoutException) when (!work.IsCompleted) { Log("category=shutdown-timeout"); return; }
        catch (Exception ex) { Log($"category=shutdown type={ex.GetType().Name}"); }
        var remaining = ShutdownTimeout - System.Diagnostics.Stopwatch.GetElapsedTime(started);
        // Abandoned work keeps the engine, so it is disposed only once nothing can still be using it.
        if (!await operations.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero).ConfigureAwait(false)) { Log("category=shutdown-timeout"); return; }
        engine.Dispose();
        operations.Release();
        lifetime.Dispose();
    }
}
