namespace SubZeroDev.Platform.Updater;

/// <summary>The release stream selected by the user.</summary>
public enum UpdateChannel { /// <summary>Stable releases only.</summary>
    Stable, /// <summary>Stable and preview releases, choosing the newest.</summary>
    Preview }
/// <summary>The user's installation preference.</summary>
public enum ConsentMode { /// <summary>Ask before each installation.</summary>
    ConfirmEachUpdate, /// <summary>Install when the host can restart safely.</summary>
    InstallAutomatically }
/// <summary>Why a check was requested.</summary>
public enum CheckOrigin { /// <summary>A startup or scheduled check.</summary>
    Automatic, /// <summary>A user requested check.</summary>
    Manual }
/// <summary>The outcome of an update check.</summary>
public enum CheckOutcomeKind {
    /// <summary>A newer compatible update is available.</summary>
    UpdateAvailable,
    /// <summary>No newer compatible update is available.</summary>
    UpToDate,
    /// <summary>The user disabled automatic checks.</summary>
    AutomaticCheckDisabled,
    /// <summary>An automatic check was recently attempted.</summary>
    RecentlyChecked,
    /// <summary>This executable is not a supported Velopack installation.</summary>
    UnsupportedInstallation,
    /// <summary>The network request failed or timed out.</summary>
    NetworkUnavailable,
    /// <summary>GitHub refused the request because of throttling or access restrictions.</summary>
    RateLimited,
    /// <summary>A release failed validation.</summary>
    InvalidRelease,
    /// <summary>The operation was cancelled.</summary>
    Cancelled,
    /// <summary>An update is already being installed, staged, or scheduled; Candidate is that update. Nothing was checked.</summary>
    UpdateInProgress,
    /// <summary>The release repository does not exist or is not public; it may have been renamed or made private.</summary>
    RepositoryNotFound }
/// <summary>The current stage of the updater.</summary>
public enum UpdateStage {
    /// <summary>No operation is running.</summary>
    Idle, /// <summary>Looking for a release.</summary>
    Checking, /// <summary>A candidate can be offered.</summary>
    UpdateAvailable, /// <summary>Downloading and verifying a package.</summary>
    Downloading, /// <summary>A verified package is staged.</summary>
    AwaitingRestart, /// <summary>The host has approved restart.</summary>
    Applying, /// <summary>The update has been scheduled for application after process exit.</summary>
    Completed, /// <summary>An operation failed.</summary>
    Failed, /// <summary>This installation cannot update itself.</summary>
    UnsupportedInstallation }
/// <summary>What an install or restart retry did.</summary>
public enum InstallOutcome {
    /// <summary>The update is scheduled; exit the application to apply it.</summary>
    RestartScheduled,
    /// <summary>The host deferred the restart; the verified update stays staged for RetryPendingRestartAsync.</summary>
    Deferred,
    /// <summary>No update was staged, so nothing was done.</summary>
    NoPendingUpdate }
/// <summary>The host's response to a safe restart request.</summary>
public enum RestartDecision { /// <summary>Keep the staged update for a later retry.</summary>
    Defer, /// <summary>The host is quiescent and will exit after the updater returns.</summary>
    Ready }

/// <summary>Immutable configuration. AppId must match the Velopack package identity.</summary>
public sealed record UpdaterOptions(string AppId, Uri PublicReleaseRepository, string SettingsDirectory)
{
    /// <summary>Minimum elapsed time between automatic network attempts; defaults to fifteen minutes.</summary>
    public TimeSpan MinimumAutomaticCheckInterval { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>Maximum wait for a response, and for further data while downloading a package; defaults to thirty seconds.
    /// A package download may take longer overall while it keeps receiving data.</summary>
    public TimeSpan NetworkTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Total package transfer deadline, including redirects and retries; defaults to thirty minutes.
    /// Must be positive and no greater than twenty-four hours. Receiving data does not extend it.</summary>
    public TimeSpan PackageDownloadTimeout { get; init; } = TimeSpan.FromMinutes(30);
    /// <summary>The publisher's ECDSA P-256 public key, as PEM or base64 SubjectPublicKeyInfo. When set, every full package
    /// must carry a valid signature from this key before it is offered. When null, packages are checked only against the
    /// SHA-256 in their own release, and InstallAutomatically consent still asks before each installation.</summary>
    /// <exception cref="ArgumentException">The value is not an ECDSA P-256 public key.</exception>
    public string? PackageSigningKey { get; init => field = value is null ? null : PackageSignature.Validate(value); }
}

/// <summary>Versioned, per-user preferences. Copies are immutable.</summary>
public sealed record UpdaterPreferences
{
    /// <summary>The persisted schema version.</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>Whether startup checks are enabled.</summary>
    public bool CheckAutomatically { get; init; } = true;
    /// <summary>The selected release channel.</summary>
    public UpdateChannel Channel { get; init; }
    /// <summary>The installation preference; automatic consent is granted only through InstallAsync.</summary>
    public ConsentMode ConsentMode { get; init; }
    /// <summary>The last automatic network attempt, including failures.</summary>
    public DateTimeOffset? LastAutomaticNetworkCheckUtc { get; init; }
    /// <summary>GitHub's automatic-check backoff deadline; restored with a maximum remaining wait of one hour.</summary>
    public DateTimeOffset? AutomaticBackoffUntilUtc { get; init; }
    /// <summary>The last completed successful check.</summary>
    public DateTimeOffset? LastSuccessfulCheckUtc { get; init; }
    /// <summary>The version most recently offered or deferred.</summary>
    public string? LastOfferedVersion { get; init; }
    /// <summary>Automatic prompts for that version are suppressed until this time.</summary>
    public DateTimeOffset? OfferDeferredUntilUtc { get; init; }
    /// <summary>The version the last apply was scheduled to install; cleared once the restarted application confirms it is running.</summary>
    public string? PendingInstallVersion { get; init; }
}

/// <summary>An immutable, client-owned release selection. Install uses exactly this selection.</summary>
public sealed class UpdateCandidate
{
    internal UpdateCandidate(string current, string target, UpdateChannel channel, DateTimeOffset? published, Uri? notesUrl, string? notes, object identity)
        => (CurrentVersion, TargetVersion, Channel, PublishedAtUtc, ReleaseNotesUrl, ReleaseNotes, Identity) = (current, target, channel, published, notesUrl, notes, identity);
    internal object Identity { get; }
    /// <summary>The running application version.</summary>
    public string CurrentVersion { get; }
    /// <summary>The offered semantic version.</summary>
    public string TargetVersion { get; }
    /// <summary>The channel containing the selected package.</summary>
    public UpdateChannel Channel { get; }
    /// <summary>The release's publication time, when available.</summary>
    public DateTimeOffset? PublishedAtUtc { get; }
    /// <summary>An HTTPS GitHub release page; open only after a user gesture.</summary>
    public Uri? ReleaseNotesUrl { get; }
    /// <summary>Untrusted plain text or Markdown. Never render as active HTML.</summary>
    public string? ReleaseNotes { get; }
}

/// <summary>A check result. Automatic deferrals can return a candidate with ShouldPrompt false.</summary>
public sealed record CheckResult(CheckOutcomeKind Kind, UpdateCandidate? Candidate = null, string? Message = null, bool ShouldPrompt = false);
/// <summary>A snapshot delivered on the calling or worker thread, never a guaranteed UI thread.</summary>
public sealed record UpdaterState(UpdateStage Stage, UpdateCandidate? Candidate = null, int? DownloadPercent = null, string? Message = null);
/// <summary>Coordinates graceful host shutdown. Ready means detection, tray, and instance guard have been released.</summary>
public interface IUpdateRestartCoordinator
{
    /// <summary>Ask the host to quiesce. Return Defer while settings or critical work are active.</summary>
    Task<RestartDecision> RequestRestartAsync(CancellationToken cancellationToken);
    /// <summary>The restart failed after RequestRestartAsync was called, including after Ready. Restore detection, tray, and
    /// instance guard so the running version stays usable; may be called after a partial quiesce, so make it idempotent.
    /// The staged update remains available to RetryPendingRestartAsync.</summary>
    Task RestartAbortedAsync();
}
/// <summary>UI-independent update operations. Dispose during ordinary shutdown.</summary>
public interface IUpdaterClient : IAsyncDisposable
{
    /// <summary>The last successfully persisted preference snapshot.</summary>
    UpdaterPreferences Preferences { get; }
    /// <summary>The current state.</summary>
    UpdaterState State { get; }
    /// <summary>State notifications; subscribers must marshal to their UI dispatcher.</summary>
    event EventHandler<UpdaterState>? StateChanged;
    /// <summary>Check for updates. Concurrent checks share one network operation; each caller gets its own result.</summary>
    Task<CheckResult> CheckAsync(CheckOrigin origin, CancellationToken cancellationToken = default);
    /// <summary>Start a nonblocking automatic check after the host is ready. Observe the returned task.</summary>
    Task<CheckResult> StartAutomaticCheckAsync(CancellationToken cancellationToken = default);
    /// <summary>Save editable preferences. Enabling automatic installation requires InstallAsync with consent.</summary>
    Task SavePreferencesAsync(UpdaterPreferences preferences, CancellationToken cancellationToken = default);
    /// <summary>Download the exact candidate and request a graceful restart. Explicit consent can be remembered.</summary>
    Task<InstallOutcome> InstallAsync(UpdateCandidate candidate, bool rememberAutomaticConsent = false, CancellationToken cancellationToken = default);
    /// <summary>Defer automatic prompts for this version for twenty-four hours.</summary>
    Task DeferAsync(UpdateCandidate candidate, CancellationToken cancellationToken = default);
    /// <summary>Retry the host restart decision for a staged, verified update.</summary>
    Task<InstallOutcome> RetryPendingRestartAsync(CancellationToken cancellationToken = default);
}
