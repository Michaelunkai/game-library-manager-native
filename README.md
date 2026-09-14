# Game Library Manager Native

This project contains the native Windows WPF build of Game Library Manager.
It is the desktop executable lane, separate from the related web repository.

## Run the verified bundle

```powershell
F:\study\repos\game-library-manager-native\dist\GameLibrary.exe
```

The direct launch is the supported route: it selects the adjacent F-drive
`data\` profile automatically and opens on the monitor where the launch was
initiated. `--main-monitor` and `--second-monitor` remain available as explicit
overrides. Startup placement is normalized after WPF restores any stale Windows
placement. The adjacent native WPF runtime DLLs and
`dist\tools` directory are part of the self-contained package; no native
runtime extraction to the user's TEMP directory is required. `--offline`
remains available for a no-network session.

## Project layout

- `native\` — C# WPF source, build scripts, acceptance checks, and tests.
- `public\` — catalog source used by the native build.
- `dist\` — verified runnable Windows x64 bundle (kept outside Git history).
- `data\` — F-drive runtime catalog, cache, state, and job receipts (kept
  outside Git history).
- `evidence\` — small verification receipts from the completed acceptance
  run.

## Reliability and synchronization

Local preferences, play history, install measurements, and paths are written
atomically beside the bundle with a recoverable backup. Website-shared tabs,
visibility, and category edits enter a durable per-field outbox before network
I/O; they leave that outbox only after the production backend acknowledges the
conditional write and a read-back matches. Conflicts and offline failures keep
the local edit available for review.

Install jobs use unique staging folders, exact completion markers, and
cross-process reservations. A visible PowerShell host now records the complete
terminal transcript beside each durable job log, so the affected game and its
actual failing command remain inspectable instead of being reduced to a generic
exit code.

Installed games show recursively measured local bytes when available; Docker
download size remains a separate fallback. Artwork and approximate completion
time are loaded from the bundled catalog and incrementally refreshed through
the title-validated metadata service without replacing good cached data after a
provider mismatch or outage.

## Interface

The WPF shell uses custom Windows 11 caption controls, Mica where supported,
rounded semantic surfaces, light and dark palettes, visible keyboard focus,
comfortable control targets, and polite accessibility announcements. It
disables decorative backdrop effects in Windows high-contrast mode.

## Release

Version 1.1.0 is published at
https://github.com/Michaelunkai/game-library-manager-native/releases/tag/v1.1.0.
The complete ZIP is the fresh-install package; the separate EXE is the exact
binary for an existing complete bundle. Production synchronization is live at
https://game-library-michaelunkai.netlify.app and advertises atomic conditional
writes backed by Netlify Blobs.
