# SubZeroDev.Platform.Updater

UI-independent Windows x64 desktop updates for .NET 10, backed by Velopack 1.2.161 and public GitHub Releases.

Call `Velopack.VelopackApp.Build().Run();` as the first operation in your executable's `Main`, before any single-instance guard or UI. Create one `UpdaterClient` after startup, observe `StartAutomaticCheckAsync`, and marshal `StateChanged` to your UI dispatcher. Unpackaged development executables report `UnsupportedInstallation`.

The host implements `IUpdateRestartCoordinator`. Return `Defer` while settings or critical work are open. For `Ready`, stop detection/work, dispose the tray and release the instance guard. If the update cannot be scheduled after that, the updater calls `RestartAbortedAsync`: restore detection, tray and instance guard so the current version keeps running (it can follow a partial quiesce, so make it idempotent). The staged update stays available to `RetryPendingRestartAsync`. Automatic installs never throw from `StartAutomaticCheckAsync`; a failure comes back as a result with `ShouldPrompt` set. After the updater reaches `Completed`, exit the process normally: Velopack waits for exit before applying the verified package and restarting. Never wait for updater shutdown from the UI thread that the coordinator needs.

Preferences are stored atomically in the caller's settings directory. Automatic checks default on, Stable, with explicit consent for every installation and a persisted fifteen-minute attempt interval. Manual checks bypass those preferences and receive their own visible result. A cancelled waiter does not cancel a shared check; disposal cancels the underlying operation. Download cancellation leaves the running application intact.

`InstallAsync(candidate, rememberAutomaticConsent: true)` records consent only as part of an explicit install action. `SavePreferencesAsync` can revoke it. `DeferAsync` suppresses automatic prompts for the same version for 24 hours. `RetryPendingRestartAsync` retries a staged update when the host becomes idle.

Stable releases use `vX.Y.Z` and `win-stable`; previews use `vX.Y.Z-preview.N` and `win-preview`. Preview clients examine both channels and never downgrade. Releases must contain matching full packages and SHA-256 feed entries. Release notes are untrusted text, never active HTML. No access token is accepted or embedded in clients. GitHub sees normal HTTPS request metadata, including IP address; there is no application telemetry.

## Build

Use the .NET 10 SDK on Windows. `dotnet test` runs deterministic tests. `dotnet pack src/SubZeroDev.Platform.Updater -c Release -o artifacts/packages` creates the library and symbols packages. Samples restore the actual package from that local feed or nuget.org.

The initial package is not published until the owner's NuGet publication credential is available. See `docs/releasing.md` for release and validation instructions.
