# SubZeroDev.Platform.Updater — implementation specification

**Status:** implementation ready  
**Date:** 2026-10-01  
**Owner:** The-Running-Dev  
**First consumer:** SubZeroDev.HotCorners  
**Scope:** Windows desktop applications, .NET 10, x64 first

> **Instruction to Astra:** Implement this specification end to end in one pass. Create the updater repository and package, integrate HotCorners, update its release process and documentation, and exercise real packaged update paths. Follow each repository's `AGENTS.md` and existing design contracts. Make routine implementation decisions without stopping for questions. Report any unavailable external credential or repository permission precisely, with all code and local validation completed. Do not report success based only on a compiled library or a debug launch.

## 1. Decisions and desired result

| Area | Decision |
| --- | --- |
| Shared component | **New repository:** `The-Running-Dev/SubZeroDev.Platform.Updater`, public source, one reusable NuGet package named `SubZeroDev.Platform.Updater`. Do not place the shared implementation in HotCorners or Licensing. |
| First app | HotCorners consumes the package from the first release and is the reference tray integration. |
| Update engine | Velopack, pinned to one tested version across its runtime NuGet reference and `vpk` CLI. The shared package wraps its update lifecycle; consumers do not implement feed parsing or file replacement. |
| Release source | Public GitHub Releases for each app, with no GitHub credential in the desktop binary. For HotCorners create a public binary-only repository, `The-Running-Dev/SubZeroDev.HotCorners.Releases`, while keeping the existing source repository private. |
| Distribution | Velopack self-updating portable ZIP, per-user `Setup.exe`, and per-user MSI for `win-x64`. The update package and channel feed are also published; the ZIP and installer alone are insufficient for ongoing updates. |
| Channels | Stable is default. Preview is opt-in. Preview users consider both preview and stable releases and take the highest compatible version. Never downgrade automatically when leaving Preview. |
| Check behavior | Automatic checks enabled by default, initiated after every ordinary app startup without blocking startup. A persisted 15-minute minimum interval suppresses repeated automatic network requests after rapid restarts; manual checks always attempt a fresh request. |
| Consent | On the first available update, show **Install and restart**, **Later**, and a **Install future updates automatically** checkbox. Save automatic consent only after the user selects Install and restart with the box checked. Future eligible updates download and install when the host can safely restart. |
| UI | Package has no WPF, WinUI, WinForms, XAML, tray, or dialog dependency. It exposes state, results, and operations; each host presents them using its own UI. |
| Package feed | Publish the public library on **nuget.org** for straightforward use by other apps. Use a local NuGet feed during integration before the first publication. Publishing requires a nuget.org API key or trusted publishing setup supplied by the owner. Do not substitute a private authenticated feed as the only distribution method. |

The end state is a newly installed or newly extracted HotCorners release that checks on startup, offers a manual tray check, lets the user disable automatic checks and choose a channel, installs an update after consent, and preserves settings and startup behavior after relaunch. The old HotCorners 1.0.0 ZIP needs a one-time user migration because it contains no updater.

## 2. Current repository facts and constraints

These facts were inspected from the repositories on 2026-10-01; recheck the current branch before editing.

* [HotCorners source](https://github.com/The-Running-Dev/SubZeroDev.HotCorners) is private, targets .NET 10 Windows, and currently ships a plain `win-x64` ZIP from `.github/workflows/release.yml`. Its current `Directory.Build.props` declares version `1.0.0`.
* `src/SubZeroDev.HotCorners.App/Program.cs` owns startup, creates a single-instance guard before initializing the tray, and then enters a blocking detection loop. Velopack bootstrap must run **before** the single-instance guard, dispatcher, tray icon, or detection loop, because its update hooks run through the same executable.
* `src/SubZeroDev.HotCorners.Platform/Messaging/HiddenMessageWindow.cs` creates the Win32 tray menu on a separate STA dispatcher thread. Menu changes and update notifications must be marshalled onto that thread. Preserve existing Settings, Pause/Resume, and Quit actions.
* `src/SubZeroDev.HotCorners.Platform/Startup/Win32StartupRegistration.cs` stores a per-user HKCU Run entry. Velopack gives Windows a stable root launcher and a replaceable `current` executable; the Run entry must point to the stable launcher.
* HotCorners settings and logs live beneath `%LOCALAPPDATA%\SubZeroDev.HotCorners`. Keep them there, outside Velopack's replaceable `current` directory. Put updater preferences in a separate file to avoid changing the existing application configuration schema.
* [SubZeroDev.Licensing](https://github.com/The-Running-Dev/SubZeroDev.Licensing) is the architectural precedent for a standalone reusable package: small public API, clear result types, tests, samples, central versions, and public API tracking. Reuse those engineering conventions, not licensing domain code.
* HotCorners' README and design files currently describe no networking, installer, or auto-update. Update those contracts and privacy wording as part of integration. Respect its `AGENTS.md` precedence and contribution rules. Do not assume pending licensing PR #155 has merged.

## 3. Goals, limits, and supported environment

### Required in version 1

1. A UI-independent updater package for unpackaged .NET Windows apps, with a documented host adapter pattern for WPF and WinUI 3. A small WinForms/console example is useful but does not replace the WPF and WinUI demonstrations.
2. Public GitHub Releases as the first source, configurable per app without a hard-coded HotCorners repository. Settings and release lookup are per user and per app.
3. Self-updating Velopack portable and per-user installed distributions. Test both. Include MSI as an installer output and smoke-test its update path as available in the Windows CI/test environment.
4. Stable/Preview selection, startup and manual checks, persisted automatic-check opt-out, first-install confirmation, optional future auto-install, progress and actionable errors.
5. Safe application shutdown and relaunch coordinated with the host. Never silently close a window or terminate a process before the host declares restart safe.
6. The HotCorners first-consumer integration, release automation, migration notes, and real package testing.

### Outside version 1

* Private GitHub release authentication in a distributed client, arbitrary installer technologies, MSIX installation control, background Windows service, forced updates, silent downgrade, Linux/macOS packaging, admin-level installation, and telemetry.
* A generic dialog implementation. Host apps control wording, focus, accessibility, tray behavior, and visual design.
* Automatic migration of the existing plain ZIP's files into Velopack's portable layout. Old 1.0.0 binaries cannot self-update; their users download/extract a new portable ZIP or run Setup once.
* A promise of automatic rollback on launch failure. Velopack's actual behavior must be tested and documented; do not claim a recovery feature the package does not implement.

Target `net10.0` for the library if all dependencies support it, keeping platform UI references out; use Windows runtime checks and Windows-focused docs. The examples and HotCorners target `net10.0-windows`. If a Windows-specific TFM is required by the chosen Velopack APIs, use `net10.0-windows` and explain it in the README. Build x64 artifacts first; architecture must be a deliberate release identity, not inferred from asset filename substrings.

## 4. Repository and project layout

Create the public repository `The-Running-Dev/SubZeroDev.Platform.Updater` with a focused solution:

```text
SubZeroDev.Platform.Updater/
  AGENTS.md                         # only if the repository convention calls for one
  README.md
  CHANGELOG.md
  LICENSE
  Directory.Build.props
  Directory.Packages.props           # if using central package management
  SubZeroDev.Platform.Updater.slnx
  src/SubZeroDev.Platform.Updater/
    Abstractions/                     # public API
    GitHub/                           # source construction and release policy
    Velopack/                         # engine adapter and package detection
    Persistence/                      # atomic user preferences
    UpdaterClient.cs
  tests/SubZeroDev.Platform.Updater.Tests/
  samples/WpfTrayHost/
  samples/WinUIHost/
  docs/consumer-guide.md
  docs/release-guide.md
  .github/workflows/ci.yml
  .github/workflows/publish.yml
```

Use one shipping NuGet package initially. Keep internals behind interfaces where tests need deterministic fakes, but avoid a multi-package framework. Include XML documentation for public members, nullable reference types, analyzers, source-link/symbol package, package icon/README as appropriate, deterministic builds, and the same public-API tracking discipline used by Licensing. Pin the exact Velopack runtime and CLI versions; the pack CLI version should match the runtime per [Velopack's guidance](https://docs.velopack.io/getting-started/csharp).

Create `The-Running-Dev/SubZeroDev.HotCorners.Releases` as a **public binary release repository** with a short README explaining that source and issues live in the private source repository only where access permits, a visible privacy/update notice, and no source mirror. Its release assets are produced by the private source workflow. Never embed a GitHub token in an app, package, portable ZIP, or installer.

## 5. Package consumer contract

The public API should be as small as possible while covering this behavior. Exact names can change for C# consistency, but all capabilities and semantics below are required and documented. Do not expose Velopack model types in the package's ordinary consumer surface unless unavoidable; the wrapper should keep application code stable when Velopack changes.

```csharp
public sealed record UpdaterOptions(
    string AppId,
    Uri PublicReleaseRepository,
    string SettingsDirectory,
    TimeSpan MinimumAutomaticCheckInterval,
    TimeSpan NetworkTimeout);

public enum UpdateChannel { Stable, Preview }
public enum UpdateConsentMode { ConfirmEachUpdate, InstallAutomatically }
public sealed record UpdaterPreferences(
    bool CheckAutomatically,
    UpdateChannel Channel,
    UpdateConsentMode ConsentMode);

public enum CheckOrigin { Startup, Manual }
public enum CheckOutcomeKind {
    UpdateAvailable, UpToDate, AutomaticCheckDisabled, RecentlyChecked,
    UnsupportedInstallation, NetworkUnavailable, RateLimited,
    InvalidRelease, Cancelled
}
public sealed record CheckOutcome(
    CheckOutcomeKind Kind,
    UpdateCandidate? Candidate,
    string? UserSafeMessage);

public interface IUpdaterClient : IAsyncDisposable {
    UpdaterPreferences Preferences { get; }
    UpdaterState State { get; }
    event EventHandler<UpdaterStateChangedEventArgs> StateChanged;
    Task<CheckOutcome> CheckAsync(CheckOrigin origin, CancellationToken cancellationToken);
    Task SavePreferencesAsync(UpdaterPreferences preferences, CancellationToken cancellationToken);
    Task<InstallOutcome> InstallAsync(UpdateCandidate candidate,
        bool rememberAutomaticConsent, CancellationToken cancellationToken);
}

public interface IUpdateRestartCoordinator {
    Task<RestartDecision> RequestRestartAsync(UpdateCandidate candidate,
        CancellationToken cancellationToken);
}
```

The API sketch establishes behavior, not an instruction to copy it unchanged. In particular:

* `CheckAsync(Startup)` is called once by the host after basic startup. The package enforces automatic-check preference and the minimum interval, returning `AutomaticCheckDisabled` or `RecentlyChecked` when no request is made. `CheckAsync(Manual)` bypasses both and returns a distinct visible result even when automatic checks are disabled.
* The package provides a convenience `StartAutomaticCheck` or equivalent that starts work without blocking the UI and makes errors observable through a returned task/state. No unobserved fire-and-forget task or `async void` handler.
* `StateChanged` carries `Idle`, `Checking`, `UpdateAvailable`, `Downloading` with percentage where known, `AwaitingRestart`, `Applying`, `Completed`, `Failed`, and `UnsupportedInstallation`. Events have **no UI-thread guarantee**. Consumer samples explicitly dispatch them to the WPF Dispatcher, WinUI DispatcherQueue, or HotCorners' message window.
* An available candidate contains the current and target semantic versions, channel, published date, release notes URL/text if safely available, and stable identity needed to install the same candidate checked. It must not contain executable instructions from release text.
* `InstallAsync` downloads/stages the exact selected candidate, reports progress, asks the restart coordinator whether it is safe to close, and invokes Velopack's apply/restart mechanism. If restart is deferred, retain a staged update if Velopack supports it and expose `AwaitingRestart`; otherwise retry later. Persist `InstallAutomatically` only after a user-confirmed call sets `rememberAutomaticConsent=true`; a background check, checkbox toggle without confirmation, or deferred offer must not grant consent.
* Automatic mode calls the same installation path, with no confirmation dialog, and requests safe restart from the host. A host may defer while a settings dialog, unsaved document, recording, or other critical activity is active. On deferral, surface a pending state and retry at an appropriate idle point; never loop aggressively.
* Concurrent automatic/manual checks and installs are single-flight. A manual check during an in-flight startup check joins or supersedes it and produces one manual-visible result. Cancellation cannot leave preferences or package state half-written.
* Accept optional logging and persistence abstractions internally for tests. The public default logger uses host-provided `ILogger`/callback or no-op; do not introduce a mandatory logging framework dependency.

### Minimal documented host integration

```csharp
[STAThread]
static int Main(string[] args)
{
    VelopackApp.Build().Run(); // first operational line, before mutex/tray/UI
    return RunOrdinaryApplication(args);
}

// After ordinary app startup:
// 1. Construct the client with AppId, public release URL, settings directory,
//    and a host-specific restart coordinator.
// 2. Subscribe to state changes and dispatch to the UI thread.
// 3. Start the automatic check without blocking startup.
// 4. Bind host menu/settings actions to CheckAsync(Manual) and SavePreferencesAsync.
// 5. Dispose/cancel on ordinary application exit.
```

Keep the direct `VelopackApp.Build().Run()` line because [Velopack requires early bootstrap](https://docs.velopack.io/getting-started/csharp) and its packaging tool can inspect the app entry point. If a wrapper method is added, prove that packaged hooks still work and configure any CLI bootstrap check intentionally.

## 6. User preferences and exact behavior

Store a versioned JSON document per app/user at the caller-selected path, defaulting to `%LOCALAPPDATA%\<AppId>\updater.json`. HotCorners uses `%LOCALAPPDATA%\SubZeroDev.HotCorners\updater.json`. Use an atomic temporary-file/write/replace strategy, preserve the last good settings on write failure, and recover to safe defaults with a logged diagnostic if a file is corrupt. Lock or serialize writes within the process.

```json
{
  "schemaVersion": 1,
  "checkAutomatically": true,
  "channel": "stable",
  "consentMode": "confirmEachUpdate",
  "lastAutomaticNetworkCheckUtc": null,
  "lastSuccessfulCheckUtc": null,
  "lastOfferedVersion": null,
  "offerDeferredUntilUtc": null
}
```

Runtime bookkeeping may be stored separately from user preferences, but it must remain under the same per-user directory and must not turn a missing field into disabled checks. Settings changes take effect immediately, survive app upgrades, and have deterministic forward-compatible parsing of unknown fields.

| Trigger or setting | Required behavior |
| --- | --- |
| First launch | `CheckAutomatically=true`, `Channel=Stable`, `ConsentMode=ConfirmEachUpdate`. |
| Ordinary startup | Launch an asynchronous check after tray/basic UI readiness. Do not delay startup or show a modal dialog on a no-update result. Suppress a new network request if one succeeded or was attempted less than 15 minutes ago; a previously found candidate can still be offered according to deferral rules. |
| Tray: `Check for updates…` | Always make a fresh request, including when automatic checks are off; show Checking, Up to date, available update, or actionable error. |
| Tray: `Automatically check for updates` | Checked by default. Toggle persists immediately. Off suppresses automatic checks but does not prevent manual checks, downloads the user approved, or a pending apply. |
| Tray: channel | Stable/Preview radio choice. Save immediately and trigger a manual-visible check for the new channel. On switch from Preview to Stable at a higher preview version, explain that the app remains on its current build until a newer stable build exists. |
| Available update, confirm mode | Present version, channel, release notes link, `Install and restart`, `Later`, and `Install future updates automatically` checkbox, unchecked by default. `Later` defers repeat automatic prompting for 24 hours or until a newer target is found; manual check can present it at once. |
| Available update, automatic mode | Download and apply without a new prompt once the host authorizes a safe restart. Display nonblocking status where the host can do so. Disabling automatic checks does not silently erase already granted auto-install consent; the two settings are independent. |
| Network/offline/rate limit | Keep app functional. Use a timeout, no tight retries, and a bounded backoff after rate limiting. Manual action shows a clear message and a link to the public releases page. Automatic errors are logged and surfaced non-modally only when useful. |

Manual checking must not change the automatic-check setting. A dismissed offer must never count as “don't ask again.” The checkbox means **future updates may install automatically**; phrase it clearly and keep the setting discoverable so users can revert to confirmation. For future hosts, expose the consent mode independently of tray layout.

## 7. Release selection and channels

Use immutable app identity `SubZeroDev.HotCorners`, x64 architecture, and SemVer 2 versions. A tag `v1.2.0` is stable; a tag such as `v1.3.0-preview.1` is preview. Match tag version, application version, Velopack package version, and release metadata. Reject draft releases, malformed/mismatched feeds, unexpected pack ID/architecture, missing assets, and candidate versions that are not strictly newer. Never use a generic GitHub “latest” endpoint for Preview because GitHub's latest-release semantics exclude prereleases.

Pack stable artifacts with Velopack channel `win-stable`; pack preview artifacts with `win-preview`. Stable selection checks only the stable channel. Preview selection checks both channels and chooses the highest compatible SemVer candidate, so Preview users continue to receive stable releases that supersede a preview. Use `GithubSource` against the public binary repository with `accessToken: null`; use `prerelease: false` for stable and `prerelease: true` for preview, plus explicit channel options as needed. Validate the exact behavior of the pinned Velopack version in tests because GitHub's release filtering and Velopack channel filtering are distinct. If a preview candidate cannot be represented reliably with the built-in source, implement a narrow adapter around Velopack's documented `IUpdateSource`; do not replace its package application logic.

Never set `AllowVersionDowngrade=true` for automatic checks. Switching from Preview to Stable changes future selection only; it does not replace a newer preview build with an older stable one. Document an explicit manual reinstall path if a user urgently wants to leave Preview before stable catches up. This policy avoids silent data-schema rollback. [Velopack documents explicit channels and downgrade behavior](https://docs.velopack.io/integrating/switching-channels).

Every launch calls the startup-check path, with a persisted 15-minute automatic-network interval to limit unnecessary requests on rapid relaunch. The public GitHub source has an [unauthenticated API limit of 60 requests per hour per IP](https://docs.velopack.io/integrating/update-sources); do not depend on an embedded token. Treat GitHub 403/429 rate limiting distinctly from offline failure. Do not claim that an unauthenticated conditional request is free of the primary API limit.

## 8. Download, install, restart, and recovery

1. Package a full update `.nupkg` and channel index with every public release. Deltas can be enabled after full-package updates are proven; lack of a delta must not prevent an update. Upload the portable ZIP, Setup.exe, MSI, full package, index, release notes, and checksums/metadata required by the chosen Velopack publication flow.
2. Use Velopack's `UpdateManager` to check, download, and apply. Verify its package/manifest integrity behavior for the pinned version and add an explicit trusted SHA-256 check if the engine does not provide sufficient verification. Reject a tampered package before apply. Pin to the configured GitHub repository and HTTPS; release text is display data only.
3. Download without blocking the app's normal interaction. Report progress. Support cancellation before apply, resume or clean partial downloads according to Velopack's documented behavior, and do not mark a candidate installed until the relaunched app reports the expected version.
4. Before applying, ask the host's restart coordinator. The host closes settings or defers if it cannot safely stop. For HotCorners, pause detection, dispose the tray/message window cleanly, release the single-instance guard, then let Velopack apply and relaunch. Coordinate from the appropriate thread to avoid deadlock with the blocking detection loop or the separate tray dispatcher.
5. Persist app data outside Velopack `current`. The [Windows layout has a stable root launcher and replaceable `current` directory](https://docs.velopack.io/packaging/operating-systems/windows). Neither updater nor consumer stores settings, logs, or generated data under `current`.
6. On apply error, leave the old app usable when Velopack can do so; show/log a useful failure and keep manual retry available. Test file lock, interrupted download, bad feed, and restart rejection. Record what recovery the pinned engine actually provides. Do not build a custom in-place ZIP overwrite path.
7. For portable deployment, extract the complete Velopack ZIP and launch the root executable. Do not copy only `current\HotCorners.exe`. Test update/restart from a normal user-writable folder, including a path with spaces. Do not promise updating from a read-only location.
8. For Setup/MSI deployment, point shortcuts and auto-start registration to the stable launcher, not the executable inside `current`. Default MSI to **PerUser** for parity with Setup and no elevation. A future per-machine MSI requires a separate privilege/IT policy.

Velopack's [packaging output includes a self-updating portable release and update packages](https://docs.velopack.io/packaging/overview), and its [Windows installer documentation](https://docs.velopack.io/packaging/installer) describes Setup/MSI behavior. Verify the exact CLI flags against the pinned `vpk pack -H` output and commit an executable release script rather than relying on this prose as a shell command.

## 9. HotCorners integration work

### Startup and lifecycle

* Add the updater package reference to `src/SubZeroDev.HotCorners.App`. Put `VelopackApp.Build().Run()` at the first operational line of `Program.Main`, before the single-instance guard. Ensure Velopack hook invocations exit quickly without creating a tray icon or starting detection.
* Build an `UpdaterClient` using `AppId=SubZeroDev.HotCorners`, public release URL `https://github.com/The-Running-Dev/SubZeroDev.HotCorners.Releases`, and the existing `%LOCALAPPDATA%\SubZeroDev.HotCorners` data root. Initialize after basic tray readiness; retain the client for application lifetime; observe startup task exceptions and cancel/dispose on Quit.
* Adapt the blocking 25 ms detection loop so an update can request a graceful stop, release resources, and exit promptly. Do not apply while the settings dialog is active or while the application cannot guarantee a clean restart. Make the completion path explicit rather than relying on process termination.
* Handle first-run/updated hooks only for quick migration work that Velopack permits there. Avoid network calls, UI, or other long-running work in hooks.

### Tray and user experience

Extend `HiddenMessageWindow`'s menu with these items, preserving existing behavior and keyboard accessibility:

```text
Settings…
Pause detection / Resume detection
────────────
Check for updates…
Automatically check for updates        [checked by default]
Update channel  >  Stable             [selected by default]
                   Preview
────────────
Quit
```

Use unique command IDs and radio/check marks. Disable duplicate check/install actions while one is running. A manual check shows a lightweight status or dialog that states “You're up to date,” candidate details, or the error; an automatic no-update check stays silent. A found update presents the consent UI described in section 6, using the host's current UI technology. A generic Windows dialog is acceptable for HotCorners v1 if it meets the required choices and checkbox. Dispatch state changes onto the tray window's STA dispatcher using its established posting pattern; never mutate Win32 menu state from a network callback. Make update status discoverable in the tray after an automatic download or deferred restart.

### Startup registration and data

`Win32StartupRegistration` currently writes `Environment.ProcessPath` into HKCU Run. In Velopack mode, derive the stable root launcher path from the installed/portable layout or supported locator API and use that for new registrations. On first upgraded launch, if the Run entry is already present and belongs to HotCorners, replace the old executable path with the new stable root path; if no entry exists, leave startup disabled. Do not rewrite an unrelated Run value. Verify the Run entry after two consecutive updates, from Setup, MSI, and portable modes. Keep existing HotCorners `config.json` unchanged and place updater prefs in `updater.json` beside it.

### Product documentation and migration

Update README, setup guide, privacy/network disclosure, screenshots if affected, and design contracts (`design/00-brief.md`, `10-design.md`, `20-contract.md`, `30-slices.md`, `90-decisions.md` as applicable). The old “no network/no installer/no auto-update” statements become inaccurate. Describe the default startup check, manual check, toggle, channels, public GitHub request, no telemetry, and fallback download route.

The existing plain 1.0.0 ZIP has no update mechanism. Publish a clear “one-time migration” note: users may extract the new Velopack portable ZIP into a **new clean folder** and run its root executable, or install Setup. Their `%LOCALAPPDATA%` configuration remains in place. Do not tell users to unzip over an active old folder. If they have a startup Run entry, launch the new app once and let the verified migration rewrite the path. Do not imply the old binary will update itself.

## 10. CI, publication, and credentials

### Updater repository

* CI on push/PR: restore, build, analyzers, unit tests, pack, validate NuGet metadata, and compile WPF/WinUI consumer examples on Windows. Include a clean consumer restore/build against the locally produced package, rather than project references only.
* Release workflow on `v*.*.*` tags: verify tag/package version match and CI gates, create package and symbols, publish to nuget.org once, and create source-repo release notes. Use an owner-provided `NUGET_API_KEY` or trusted publishing configuration with least privilege. Never print it. If credentials are unavailable, leave a valid local `.nupkg`, passing tests, and an exact blocked publication step; do not claim public package publication.
* Generate/maintain public API compatibility files, a short WPF sample, a short WinUI sample, and a consumer guide with the early bootstrap call and release-pipeline contract.

### HotCorners repository

Replace `.github/workflows/release.yml`'s plain `Compress-Archive` release route with a versioned, reproducible Windows release script and workflow. Use a pinned `vpk` local tool manifest or equivalent pinned invocation. Pipeline: checkout; validate tag and `Directory.Build.props` version; run existing tests; publish self-contained `win-x64`; `vpk pack` with fixed pack ID, main EXE, channel, per-user MSI, title/icon; verify required artifacts/feed and bootstrapping entry point; compute checksums; publish a **draft** GitHub release in the public distribution repository, upload all assets, then publish only after every asset passes verification. Mark preview tags as prereleases. Keep the existing private source repository's release notes or link to the public binary release as appropriate, but the in-app source always uses the public release repository.

The private source workflow's default `GITHUB_TOKEN` does not automatically authorize writes to a different public repository. Provision a narrowly scoped GitHub App installation token or fine-grained PAT with Contents write on `SubZeroDev.HotCorners.Releases`; store it only as a GitHub Actions secret in the private source repository. Name it clearly, for example `HOTCORNERS_RELEASE_TOKEN`. Use ephemeral permissions, avoid writing the token to artifacts/logs, and fail before publication if unavailable. Provision the nuget.org publish credential separately. Do not add release tokens to desktop code. Prefer [GitHub immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases) where available; publish only fully assembled draft releases and never silently replace an already public update artifact.

Signing is strongly recommended for Windows trust and SmartScreen reputation. If a signing certificate/service is available, sign the app and installer in the release script and verify signatures before publication. If none is available today, finish the package/update implementation, mark artifacts unsigned in the release notes, and report that limitation plainly; do not invent a certificate or block local correctness tests. The release workflow must be ready to accept signing credentials later without changing updater APIs.

### Versioning and first public release

Do **not** republish the existing version `1.0.0` with a different package format under the same tag. Bump HotCorners to the next appropriate stable version (normally `1.1.0` for the distribution change unless repository history requires another version), then create a later patch/test version to prove updating. Reserve tags only after versions and public distribution repository are ready. A preview release uses SemVer prerelease form, e.g. `v1.2.0-preview.1`, and is marked as a GitHub prerelease. Require exact match among tag, assembly informational version, Velopack package version, and release title.

## 11. Security, privacy, and observability

* The configured HTTPS GitHub repository is the only source for release metadata/packages by default. Validate pack ID, channel, target version, architecture, feed shape, and package integrity. Keep GitHub release notes as untrusted text: escape/render safely; never execute links or commands supplied by release notes.
* No embedded personal access tokens, private release credentials, telemetry, machine identifiers, analytics, or automatic crash upload. A public GitHub check sends normal network metadata such as IP address and user agent to GitHub; disclose this in the app README/privacy text.
* Log timestamp, check origin, selected channel, version, stage, duration, and category of failure; do not log secrets, arbitrary release content, full user paths unnecessarily, or network response bodies. HotCorners' existing local diagnostic sink may receive updater diagnostics.
* Never update from a release whose identity differs from the configured app. Do not accept arbitrary download URLs from settings or command-line arguments. Package source configuration is an app-author decision, not a user-editable URL.
* Installer/package signing and checksums complement the source validation. Explicitly test tampered feed/package rejection and malformed releases. GitHub's [release asset API includes a digest field](https://docs.github.com/en/rest/releases/assets); use reliable Velopack metadata and documented verification for the pinned version instead of trusting only an asset filename.

## 12. Test plan and acceptance gates

Unit tests use fake clock, preferences store, release source, download/apply engine, and restart coordinator. Integration tests use a local Velopack file feed where practical; at least one manual end-to-end Windows test uses the actual public GitHub release source. Tests must cover the behavior, not mirror implementation methods.

| Gate | Pass condition |
| --- | --- |
| Package independence | A fresh WPF sample and a fresh WinUI 3 sample restore the built NuGet package, compile, bootstrap Velopack early, and perform a manual check without copying HotCorners code. |
| Defaults/persistence | First run enables checks, selects Stable, and requires confirmation. Tray toggle and channel survive restart; corrupt preferences recover safely. |
| Startup performance | HotCorners tray/detection become usable before network completion, including offline/slow GitHub; no unobserved task exception. |
| Manual path | Manual check works with automatic checks off, bypasses the 15-minute interval, and gives a visible up-to-date/error/candidate result. |
| Consent | Later does not change consent. Install with checked box installs current release and persists future auto-install; unchecked installs only current release. Reverting to confirm mode works. |
| Channels | Stable ignores previews; Preview selects the highest compatible stable/preview build; leaving Preview never auto-downgrades; malformed tag/asset is rejected. |
| Single-flight/errors | Repeated menu clicks cause one download/apply; cancellation, offline, 403/429, invalid feed, disk failure, and locked files leave app and preferences usable. |
| Portable update | Extract complete Velopack portable ZIP to a user-writable folder, run root EXE, update from version A to B, observe one clean relaunch at B, preserve `config.json`, updater settings, logs, tray behavior, and startup registration. Repeat B to C. |
| Setup update | Install per-user Setup A, update to B, confirm no elevation, stable shortcut/Run target, settings preservation, and clean relaunch. Repeat B to C. |
| MSI update | Install per-user MSI A, update to B, confirm Windows installer registration and the same stable launcher/update behavior. Record any Velopack limitation uncovered by the pinned version rather than silently dropping MSI. |
| Existing-user migration | Plain ZIP 1.0.0 data remains readable after launching new Velopack package; an existing HotCorners Run entry is migrated only when present; old plain ZIP is not represented as self-updating. |
| Release integrity | Published public GitHub release has complete feed/packages/ZIP/Setup/MSI, correct stable/prerelease flags, matching versions, and a real client can see and install it. Tampered or missing package refuses apply. |
| Privacy/docs | No embedded token or telemetry; README/design documents accurately describe network access, default check, controls, migration, and download fallback. |

For the update exercise, publish a small throwaway test app through the same updater package and packaging pipeline if real HotCorners release tags cannot be safely minted solely for a test. Still perform at least one HotCorners packaged local-feed A→B test. Do not use a debug executable as proof of update support: Velopack's `UpdateManager` expects a Velopack package layout.

## 13. One-pass implementation order and definition of done

1. Inspect current branches, repository instructions, and release/version files. Confirm repository names are available and identify any existing workflow credentials without exposing secret values.
2. Create the updater source repository and public HotCorners binary-release repository. Scaffold package, tests, samples, CI, and docs. Pin Velopack and make package build/pack pass.
3. Implement preferences, GitHub source/channel policy, update state machine, single-flight checks, consent, install/restart coordination, diagnostics, and failure results. Exercise fake-source tests and local Velopack feed.
4. Integrate HotCorners startup/tray/settings/lifecycle and startup Run migration. Update design contracts and product documentation. Use the local built NuGet package to validate integration before public publication.
5. Replace HotCorners release workflow with pinned Velopack packaging and cross-repo public release publication. Produce all three user distributions and update assets. Perform packaged portable/Setup/MSI tests and a real GitHub release check/install if credentials permit.
6. Publish the updater package on nuget.org, switch HotCorners to a released package version, run a clean restore/build/test, and publish the first HotCorners updater-enabled release. Do not use a source project reference in the final consumer.
7. Report repository/PR/release/package links, pinned versions, test evidence for each distribution, any unresolved external credential/signing limitation, and the exact one-time migration instruction for 1.0.0 users.

**Done means:** the new package is consumable through NuGet; HotCorners uses it without shared updater source copied into the app; a public release provides working updater feeds; manual and startup checks work; user consent and tray controls persist; portable and installed update paths apply and relaunch while preserving data; and documentation matches behavior. If an owner-only credential blocks publication, code and local packaged validation should still be complete, with the blocked gate named explicitly. A library-only PR or a release containing only a ZIP and Setup.exe is incomplete.

## 14. Alternatives and future extension points

* **Static HTTPS feed** (`SimpleWebSource` over Azure Blob, S3, or a static site) is the preferred second source if GitHub API rate limiting or organization policy becomes problematic. Keep source creation behind an internal provider interface so this can be added without changing host UI code. Velopack [supports HTTP and local file sources](https://docs.velopack.io/integrating/update-sources).
* **Velopack Flow** can reduce custom release hosting work, but adds a hosted dependency and should be considered only after the public GitHub path works.
* **MSIX/App Installer** is suitable for packaged WinUI apps and can use Windows-managed updates, but it is a different installation authority. Add a separate provider later; never let both MSIX and Velopack attempt to apply updates to the same installation. See [Microsoft's App Installer update documentation](https://learn.microsoft.com/en-us/windows/msix/app-installer/auto-update-and-repair--overview).
* **Microsoft Store** deployments use Store update policy. The common package may expose settings/status where appropriate, but should report that in-app Velopack install is unsupported for Store-managed builds.
* **Delta packages, staged overnight installs, enterprise policy, per-machine MSI, and signed release provenance** are useful next increments after first-consumer reliability is demonstrated.

## 15. Source references

* [Velopack C# bootstrap and update manager](https://docs.velopack.io/getting-started/csharp)
* [Velopack package outputs and version format](https://docs.velopack.io/packaging/overview)
* [Velopack Windows layout, updating, and installer scope](https://docs.velopack.io/packaging/operating-systems/windows)
* [Velopack Setup and MSI](https://docs.velopack.io/packaging/installer)
* [Velopack update sources and GitHub rate limits](https://docs.velopack.io/integrating/update-sources)
* [Velopack channel switching](https://docs.velopack.io/integrating/switching-channels)
* [GitHub release API semantics](https://docs.github.com/en/rest/releases/releases)
* [GitHub REST API rate limits](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api)
* [GitHub immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases)

