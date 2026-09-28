# Game Library for Windows

Game Library is a native Windows 11 WPF application distributed as a self-contained Windows x64 bundle. It does not require a browser engine, localhost server, Node installation, or installed .NET runtime. Keep `GameLibrary.exe`, its adjacent runtime files, `Assets`, and `tools` together.

## Run

The supported user-facing executable is:

`F:\study\repos\game-library-manager-native\dist\GameLibrary.exe`

Direct launch uses the adjacent `data` directory and opens on the monitor containing the pointer at launch time. `--main-monitor` and `--second-monitor` are explicit overrides. A second launch restores the existing instance; Close exits and Minimize hides to the tray by default.

## Library data

- The packaged catalog supplies titles, artwork, and approximate completion time offline. Online metadata refresh accepts only title-matched results and never replaces good cached data with a mismatch.
- Installed size is measured recursively from the selected local folder and timestamped. If measurement cannot complete, the UI labels the Docker download size as an estimate instead of presenting partial bytes as exact.
- Personal wishlist, ratings, tags, launchers, installed markers, and preferences remain local. Shared categories, tabs, and visibility synchronize with `https://game-library-michaelunkai.netlify.app/api/admin-config`.
- Local state and the per-field shared-edit outbox use flushed atomic replacement and recoverable backups. Conditional writes, conflict retention, interrupted-acknowledgement reconciliation, and read-back verification prevent a generic last-writer-wins overwrite.

## Installation reliability

Install jobs use a unique operation ID, exact game/destination reservation, cross-process lock, isolated staging directory, and atomic promotion. On startup or cancellation, cleanup is restricted to stale artifacts for that exact game under `.gamelibrarymanager-staging`; an intact prior installation is restored if promotion was interrupted.

The visible terminal's complete stdout/stderr transcript is persisted next to the durable structured job log. A failed command records its game, operation, exit code, command context, and terminal output. Historical generic logs cannot recover output that was never captured, but every new install now preserves it.

## Interface and accessibility

The shell uses a custom Windows 11 title bar, Mica where supported, rounded semantic surfaces, light/dark palettes, keyboard focus indicators, accessible control targets, and live status announcements. High-contrast mode switches to Windows system colors and disables decorative backdrop effects. Window dimensions are safely clamped even on small or high-DPI work areas.

## Build and verify

```powershell
.\build.ps1
.\dist\GameLibrary.exe --self-test .\evidence\self-test.json
.\dist\GameLibrary.exe --data-dir .\evidence\offline-ui --offline --ui-test .\evidence\offline-ui.json
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\verify-native.ps1
```

`build.ps1` accepts `-DotnetPath` for an explicit .NET 10 SDK and isolated output roots. `--self-test` uses temporary fixtures and simulated failures; `--ui-test` runs the packaged WPF window against an explicit data directory and exits. Neither mode mutates production data.

The deployment helpers under `backend` prepare the maintained website source, hash every staged file, run conditional-sync tests, and build all four Netlify function ZIPs with the official bundler. The reviewed candidate is live on the existing Netlify site. Production advertises conditional writes, rejects a stale competing revision, and was read back successfully by both the native client and the visible website.

See [ACCEPTANCE.md](ACCEPTANCE.md) for the current release record. The executable is not code-signed.
