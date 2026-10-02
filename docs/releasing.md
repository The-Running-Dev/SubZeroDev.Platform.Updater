# Release and host integration

## Library

Run `scripts/Test-Package.ps1` on Windows with .NET 10. It runs tests and API/XML analyzers, creates the NuGet and symbols packages, validates metadata, and restores WPF and WinUI consumers using a new empty package cache. The package is UI-independent; samples take a PackageReference and receive Velopack transitively for the mandatory bootstrap call.

Choose the library license before publishing. Add LICENSE and package license metadata, increment Directory.Build.props and the samples' central package version together, move reviewed API entries to PublicAPI.Shipped.txt, and tag the tested commit `v0.1.0`. The Publish NuGet workflow checks the tag, tests the package, and requires the `nuget` environment's NUGET_API_KEY. A missing credential stops publication; it never substitutes a private package feed. SourceLink uses the SDK's GitHub provider; symbols are in the snupkg.

## Application distributions

Pin both Velopack runtime and vpk to **1.2.161**. `scripts/Pack-Application.ps1` accepts the project, application ID, main executable, version, notes, and a fresh output directory. It produces a self-contained win-x64 publish and calls the verified `vpk pack --msi --instLocation PerUser` flags. The portable archive contains the root launcher, Update.exe, `.portable`, and `current`; never redistribute only `current`. The stable root launcher is **AppId.exe**, which need not equal the main executable's filename.

Each new release has its own full package and feed. Stable is `vX.Y.Z` / `win-stable`; preview is `vX.Y.Z-preview.N` / `win-preview`, marked prerelease. Do not reuse old cumulative channel feeds under a new GitHub release: every entry must match the release tag and have its corresponding asset in that release. Preview clients query both streams and choose the greatest version above the installed one. Drafts and legacy releases without feeds are ignored. Clients read the feeds of at most the five newest releases per stream and use the newest one that passes validation; a corrupt newest release falls back to the previous one. Metadata requests and retries are bounded; 403/429 are actionable rate-limit outcomes.

`scripts/Publish-Release.ps1` checks local hashes and required assets, creates a draft, uploads all assets, checks every remote asset's name, size and SHA-256 digest (downloading any asset GitHub reports without a digest), then publishes. Initialize the binary repository with a README first; GitHub cannot publish tags in an empty repository. It refuses to replace existing releases, and stops unless GitHub definitely reports the tag as absent. An interrupted draft requires inspection, never blind overwrite. Use a repository-scoped token with Contents write for cross-repository workflow publication. Desktop clients have no tokens.

No signing credential is assumed. Supply `VELOPACK_SIGN_PARAMS` through a protected environment only when a signing identity is configured. Unsigned artifacts remain usable but may show Windows reputation warnings. Do not describe them as signed.

## Host responsibilities

Call `VelopackApp.Build().Run()` directly at the beginning of synchronous Main. After UI initialization create the client and observe its startup task. Render notes as text, marshal events to the UI, keep manual results visible, and preserve the exact candidate presented for Install. A channel change affects subsequent checks; it does not replace the displayed candidate.

The restart coordinator returns Defer while editors or critical operations are active. For Ready, stop work, dispose tray/windows and release the instance guard before returning. Exit normally after Completed; Velopack waits for the process to exit. Keep the UI dispatcher running until the coordinator has finished; never synchronously wait for it on that dispatcher. Dispose the client on ordinary quit to cancel outstanding work. A staged package may be applied by the next Velopack bootstrap after a user-approved download.

Settings belong outside `current`. Atomically persisted updater.json contains schema version, automatic-check flag, channel, consent, network timestamps, per-version deferral and the version a scheduled restart is expected to start. No app config schema migration is required. Defaults are automatic checks, Stable, and confirmation each time. Later defers only that version for 24 hours. Consent is saved only with explicit Install; it can be revoked independently. Offline corner operation is unaffected.

## Package exercises

`tests/UpdaterProbe` is a test-only executable. Its external control JSON selects a target, app ID, data directory and optional local feed. `UPDATER_PROBE_CONTROL` points to that JSON. It records package version, compiled binary version, process path and updater states, installs the selected version, exits gracefully, and records completion after real relaunch. A null feed uses the production public-GitHub provider, without a token. Use distinct install directories and retain data outside them.

For the actual HotCorners local-feed exercise only, build the library with `-p:UpdaterLocalValidation=true` (HotCorners keeps its own `UPDATER_LOCAL_VALIDATION` define): the library then reads UPDATER_VALIDATION_FEED and HotCorners requires HOTCORNERS_VALIDATION_DATA. The property stamps the package `X.Y.Z-local-validation`; the build fails if the constant is defined any other way or that suffix is overridden, and the Publish NuGet workflow refuses such packages. These branches are absent from normal release binaries. Keep validation packages in a separate feed/cache and never upload them as product releases. See validation.md for recorded results and remaining external gates.
