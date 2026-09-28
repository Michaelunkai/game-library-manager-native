# Game Library Manager Native

A native Windows 11 WPF desktop application for managing your Docker-backed game
library. It tracks installed games, downloads and extracts them from Docker Hub
with live progress, organizes categories/tags/wishlist, and launches games
directly or through the WeMod/Wand trainer app.

## Install — download the release

Grab the latest release asset **`GameLibrary-windows-x64.zip`** from
[GitHub Releases](https://github.com/Michaelunkai/game-library-manager-native/releases)
and extract it anywhere (for example `C:\Games\GameLibrary`). Then run:

```powershell
C:\Games\GameLibrary\GameLibrary.exe
```

- The executable is **self-contained** — no .NET runtime or Node install is
  required. The adjacent WPF runtime DLLs and the `tools\` folder are part of
  the bundle, so keep the whole extracted folder together.
- On first launch it creates a `data\` folder next to the executable and loads
  the bundled 1,179-game catalog (covers included). No install wizard or
  admin rights are required.
- `--offline` starts a no-network session; `--main-monitor` forces the primary
  display.
- Download a game's image from Docker Hub to install it into your chosen
  library folder; the durable install terminal shows live byte progress and
  percentages for every command and finishes with extraction and verification.

## Build from source

Requirements: .NET 10 SDK and Docker Desktop.

```powershell
cd native
# publishes a self-contained x64 bundle into native\dist (GameLibrary.exe,
# runtime DLLs, and tools\)
.\build.ps1
```

The build also bundles `public\data` + `public\images` into
`native\Assets\catalog.zip` and copies the Wand bridge runtime. Run the full
verification against the exact executable with:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File native\verify-reliability.ps1 `
  -ExePath native\dist\GameLibrary.exe -EvidenceRoot evidence\verification -Phase Target
```

The legacy `dist\GameLibrary.exe` path (used by a `GameL` PowerShell alias) is a
compatibility launcher that forwards to `native\dist\GameLibrary.exe`.

## Project layout

- `native\` — C# WPF source, build scripts, acceptance checks, and self-tests.
- `public\` — catalog source (games, times, sizes, covers) bundled by the build.
- `native\dist\` — current runnable Windows x64 bundle (outside Git history).
- `dist\GameLibrary.exe` — compatibility launcher for existing commands.
- `launcher\` — forwarding-launcher source, build script and process tests.
- `data\` — runtime catalog, cache, state, and install-job receipts (outside Git
  history; created automatically beside the executable).
- `docs\` — acceptance audits and reliability notes.
- `evidence\` — small verification receipts (large local test output is ignored).

## Current build verification

- Deployed SHA-256:
  `DE9EEAC8ED5FFC7F649D215D3418CEA5BC2BD9FDC79D13E02B69A0F852E794BC`
- Core self-test: **150 / 150** (metadata identity, classification, save/restore,
  durable install concurrency, Wand dispatch, live progress parsing).
- Reliability target suite: **12 / 12** (package manifest, self-test, storage/sync,
  install-job, UI, native pause, Reass restore, progress helper, Wand Node fixtures).
- Live durable installs stream real byte progress (`x/y B (0.000%)`) straight
  from the Docker Engine API, so a percentage never sits flat while a layer
  downloads; stalled pulls restart automatically and a fully downloaded image is
  never killed during extraction.

### Recent reliability work

- **Durable installs**: every game installs into its own digest-scoped folder;
  many games install concurrently; an interrupted stage auto-resumes; a lost
  extraction container is recreated; the terminal shows continuous byte/percent
  progress and never freezes on a percentage.
- **Play with Wand**: every eligible game sends an exact-game `wemod://play`
  handoff whenever the CDP route does not confirm a dispatch — another game's
  busy trainer, an unreachable CDP endpoint, or an already-running game no longer
  blocks a launch, and re-clicks attach the running game's trainer.
- **Responsiveness**: the catalog JSON is parsed once and cached, file-existence
  checks are cached, and the identity projection is reused across pure view
  changes, so startup, every keystroke, and the periodic refresh stay smooth.
- **Metadata**: exact-title/Steam/GOG identity, sourced completion hours, and
  artwork come from corroborated sources; ambiguous numeric tags are never
  mislabeled as games.
- **Backup/restore**: save detection and restore reject shared/registry roots and
  verify payload hashes; no live saves are overwritten by automated checks.

## Acceptance status

Full acceptance is **not** treated as finished for every external guarantee
(universal availability of remote services, campaign-progress denominators for
every game, and all possible hardware/network conditions). Remaining gaps are
recorded in [the current acceptance audit](docs/acceptance-current.md). The
native application itself is rebuilt, deployed, and verified in this repository.