# Consumer guide

## Install

```xml
<PackageReference Include="SubZeroDev.Platform.Updater" Version="0.1.0" />
```

Velopack arrives transitively. The target is Windows x64; an executable that was not installed by Velopack reports `UnsupportedInstallation`.

## Bootstrap

`Velopack.VelopackApp.Build().Run()` must be the first operation in `Main`, before any single-instance guard or UI. Velopack runs install, update and uninstall hooks through your executable.

```csharp
[STAThread]
static void Main()
{
    Velopack.VelopackApp.Build().Run();
    // single-instance guard, then UI
}
```

## Create the client

```csharp
var updater = await UpdaterClient.CreateAsync(
    new UpdaterOptions("Contoso.App", new Uri("https://github.com/contoso/App.Releases"), settingsDirectory)
    {
        PackageSigningKey = publisherPublicKey,   // required for production distributions
    },
    restartCoordinator);
updater.StateChanged += (_, state) => dispatcher.BeginInvoke(() => Render(state));
Show(await updater.StartAutomaticCheckAsync());
```

- `PublicReleaseRepository` is the public binary repository, `https://github.com/<owner>/<repo>` with no `.git` suffix. No token is accepted.
- `SettingsDirectory` must be outside the installation (`current` is replaced on update).
- `StateChanged` has no thread guarantee; marshal it to your UI thread.
- Show `CheckResult.Message` as text. Release notes are untrusted and never HTML.
- Users only get fixed messages. Use diagnostics (the optional `CreateAsync` log callback) for detail.

## Checks and installs

| Call | Behavior |
| --- | --- |
| `StartAutomaticCheckAsync()` | Honors the preferences, the 15-minute attempt interval and any `Retry-After` pause. Never throws for installs; failures come back as results. |
| `CheckAsync(CheckOrigin.Manual)` | Ignores preferences and throttling, and always returns a visible result. |
| `InstallAsync(candidate, rememberAutomaticConsent)` | Downloads, verifies, asks the restart coordinator, then schedules the update. Consent is recorded only here. |
| `DeferAsync(candidate)` | Suppresses automatic prompts for that version for 24 hours. |
| `RetryPendingRestartAsync()` | Retries a staged update after a `Defer`. |

Install the exact candidate you displayed. Only the four most recent offered candidates stay installable.

## Restart coordinator

Implement `IUpdateRestartCoordinator`:

- `RequestRestartAsync` returns `Defer` while settings or critical work are open. For `Ready`, stop work, dispose the tray and release the instance guard.
- `RestartAbortedAsync` runs if scheduling fails after `Ready`: restore work, tray and instance guard. It may follow a partial quiesce, so make it idempotent.
- After `Completed`, exit normally; Velopack applies the package after exit. Never wait for updater shutdown on the UI thread the coordinator needs.

## Preferences and consent

Defaults: automatic checks on, Stable channel, confirmation for every install. `SavePreferencesAsync` changes them and can revoke automatic consent. Without a `PackageSigningKey`, `InstallAutomatically` still prompts.

The preferences file is forward compatible: unknown fields and a newer `schemaVersion` are read leniently and written back unchanged.

## Signing

1. `scripts/New-SigningKey.ps1 -PrivateKeyPath <outside the repository>` creates an ECDSA P-256 key and prints the public key.
2. Embed the public key as `UpdaterOptions.PackageSigningKey`.
3. Set `UPDATER_PACKAGE_SIGNING_KEY` to the private PEM when running `Pack-Application.ps1`; it writes `<package>.sig`.

Keep the private key apart from the token that writes releases. Rotation steps are in [release-guide.md](release-guide.md).

## Release contract

- Stable: tag `vX.Y.Z`, Velopack channel `win-stable`. Preview: `vX.Y.Z-preview.N`, channel `win-preview`, marked prerelease. No leading zeros.
- Each release has its own full package and `releases.<channel>.json`, plus the `.sig` file when signing. Every feed entry must match the tag and have its asset in that release.
- The release title is the tag. Drafts and releases without a feed are ignored.
- Clients read at most the five newest releases per stream and use the newest valid one.

`scripts/Pack-Application.ps1` and `scripts/Publish-Release.ps1` implement this; see [release-guide.md](release-guide.md).

## Network behavior

HTTPS to `api.github.com`, `github.com` and `*.githubusercontent.com` only, with at most five redirects. Packages larger than their feed declares are refused. A 403/429 is `RateLimited`; a `Retry-After` pauses automatic checks (up to one hour). A 404 on the release listing is `RepositoryNotFound` (renamed or made private). GitHub sees normal request metadata including IP address; there is no application telemetry.

## Samples

- [`samples/WpfTrayHost`](../samples/WpfTrayHost): WPF with a tray icon and no main window.
- [`samples/WinUIHost`](../samples/WinUIHost): WinUI 3 window.

Both restore the package from `artifacts/packages` or nuget.org and are built by `scripts/Test-Package.ps1`. They are not in the solution file, which lists only `src` and `tests`.

`NetworkTimeout` bounds response waits and package stalls (30 seconds by default). `PackageDownloadTimeout` separately bounds the entire package transfer, including redirects and retries (30 minutes by default, configurable up to 24 hours). Incoming data resets only the stall timer.
