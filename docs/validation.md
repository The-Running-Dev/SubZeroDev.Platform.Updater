# Windows validation — 2026-10-01

Validated with .NET 10, Velopack runtime/CLI 1.2.161, Windows x64. Artifacts are unsigned.
Local evidence is retained under ignored `artifacts/`; CI repeats deterministic and portable gates.

| Gate | Evidence |
|---|---|
| Library | 32 deterministic tests: defaults/atomic storage, corrupt/locked preferences and bounded transient-lock recovery, manual override, throttling failures, cancellation/single-flight, exact-candidate consent, deferral, restart, unsupported layout, HTTP errors, feed identity, semantic channel selection, SHA-256 tampering. |
| Real NuGet consumers | WPF and WinUI 3 restore 0.1.0 from a freshly packed feed with an empty cache; both Release builds pass. Both executables ran a manual check and reported UnsupportedInstallation, correctly identifying their unpackaged layout. |
| Portable | Real packaged probe A=1.0.0 → B=1.0.1 → C=1.0.2, each version a separate publish, one process relaunch per update, root launcher in a path with spaces. Each run's compiled binary version must equal its package version, and the relaunched process must confirm the recorded pending install. External config/log sentinels and persisted consent survive. `scripts/Test-PortableUpdate.ps1` repeats this and runs in CI. |
| Setup | Silent per-user Setup installed A into a path with spaces. Actual update/relaunch A→B→C succeeded; external preferences and run/state logs survived. Evidence: setup-data. Setup test installation was uninstalled before MSI testing. |
| MSI | Per-user MSI installed A without elevation. Actual update/relaunch to B succeeded. HKCU uninstall entry MSI:SubZeroDev.UpdaterProbe reported 1.0.1 and the expected LocalAppData install location. Evidence: msi-install.log and msi-data. |
| Public GitHub | Public probe v1.0.1 contains ZIP, Setup, MSI, full nupkg, feed, SHA256SUMS and notes. Packaged A checked/downloaded/verified/applied/relaunched B through the production unauthenticated GitHub source. Evidence: github-data. |
| HotCorners | Actual packaged 1.1.0→1.1.1 local-feed update completed; `current/sq.version` became 1.1.1 and a new app process ran from the same stable root. Isolated updater preferences and preservation sentinel survived. Smoke-only build enables the local feed and isolated settings; production builds exclude those hooks. |
| HotCorners regression | 214 tests pass: 66 Core, 74 Platform, 74 App. Includes startup migration/quoting, native menu checked/radio/busy states, and safe restart waiting for an occupied action slot. |

Public probe: https://github.com/The-Running-Dev/SubZeroDev.UpdaterProbe.Releases/releases/tag/v1.0.1

## Reproduction

Run `scripts/Test-Package.ps1`. Pack probe A with `scripts/Pack-Application.ps1`, AppId
`SubZeroDev.UpdaterProbe`, MainExe `UpdaterProbe.exe`, version `1.0.0`, output `artifacts/probe`.
Run `scripts/Test-PortableUpdate.ps1`. Use fresh output directories on each run.

For Setup, run its generated installer with `--silent --installto <writable-path>`; for MSI use
`msiexec /i <generated-msi> /qn /norestart /l*v <log>`. Before launching either stable root EXE,
set UPDATER_PROBE_CONTROL to an external JSON file containing AppId, Repository, Data, Target
and Feed. Point Feed at B/C assets for local tests; use null for the public GitHub test. The
probe's `runs.log`, `states.log`, `complete`, and `updater.json` record the actual result.
Uninstall the same AppId's Setup before installing MSI. Preserve test evidence outside install roots.

Sample runtime checks: set UPDATER_SAMPLE_SMOKE to an absolute result-file path, run either
built sample EXE and wait for its normal exit. The UI dispatcher runs the manual check and
writes its outcome. Clear that variable for interactive use.

## Publication gates still requiring the owner

- Choose the updater library license, then add its package license metadata.
- Provide NuGet publication credentials (or configure trusted publishing) and publish 0.1.0.
- Configure HOTCORNERS_RELEASE_TOKEN for CI Contents write to the public binary repository.
- Configure a signing identity if signed distributions are required. These tests establish update
  mechanics and integrity validation; they do not establish Authenticode trust or SmartScreen reputation.

The first product release is v1.1.0. Its workflow requires the published NuGet package before
publishing binaries. The existing v1.0.0 release is untouched. Private-source CI can temporarily
restore an actual local NuGet built from a pinned public updater commit; that bootstrap does not
claim nuget.org publication. No paid package, external credential, or license choice is assumed.
