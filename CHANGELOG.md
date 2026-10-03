# Changelog

## 0.1.0 (unreleased)

First release: UI-independent Windows x64 updates for .NET 10 on Velopack 1.2.161 and public GitHub Releases.

- `UpdaterClient` state machine with manual and automatic checks, explicit consent, deferral, pending-restart retry and restart verification.
- Per-release feeds with strict `vX.Y.Z` / `vX.Y.Z-preview.N` tags (no leading zeros), identity, hash and asset validation, and fallback to the previous valid release.
- Optional ECDSA P-256 package signatures (`UpdaterOptions.PackageSigningKey`, `<package>.sig`).
- Atomic, forward-compatible `updater.json`: unknown fields and newer schema versions survive saves.
- Downloads accept only GitHub HTTPS hosts across at most five redirects, are capped at the size the release declares, and identify as `SubZeroDev.Platform.Updater/<version>`.
- 403/429 are reported as `RateLimited`; a `Retry-After` (or exhausted rate-limit window) pauses automatic checks for up to an hour, while manual checks always run.
- Users only see fixed messages. Exception text, which can carry local paths, goes to diagnostics only. A missing or private repository (404 on the release listing) is reported as `RepositoryNotFound`.
- At most four offered candidates stay installable; `.git`-suffixed repository URLs are rejected.
- Pipeline: `Pack-Application.ps1`, `Publish-Release.ps1` (release title is the tag), `New-SigningKey.ps1`, `Test-Package.ps1`, `Test-PortableUpdate.ps1`. Signing secrets are read from the environment, not passed on the command line, and workflow actions are pinned to commit SHAs.
- MIT license. NuGet publication uses trusted publishing (OIDC, no stored API key).
- Source is organised as the spec lays out: `Abstractions/`, `GitHub/`, `Velopack/`, `Persistence/`.
- Samples: `WpfTrayHost` (tray icon, no main window) and `WinUIHost`.
