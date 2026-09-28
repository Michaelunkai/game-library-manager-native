<p align="center">
  <img src="docs/images/icon.png" alt="Game Library Manager" width="96">
</p>

<h1 align="center">Game Library Manager</h1>

<p align="center">
  A native Windows app that turns your Docker-backed library into a clean,
  fast, playable collection.
</p>

<p align="center">
  <a href="https://github.com/Michaelunkai/game-library-manager-native/releases"><img src="https://img.shields.io/badge/platform-Windows%2010%2F11-2dd4a7?style=flat-square" alt="Windows 10/11"></a>
  <a href="https://dotnet.microsoft.com/download/dotnet/10.0"><img src="https://img.shields.io/badge/.NET-10-512bd4?style=flat-square" alt=".NET 10"></a>
  <a href="https://www.docker.com/products/docker-desktop/"><img src="https://img.shields.io/badge/docker-required-2496ed?style=flat-square" alt="Docker"></a>
</p>

<p align="center">
  <img src="docs/images/hero.png" alt="Game Library Manager" width="100%">
</p>

## Overview

Game Library Manager is a Windows 11 WPF desktop application for people who
keep their games in Docker images. It gives you a polished, searchable catalog,
installs and extracts titles into your own folders with live progress, tracks
playtime, and hands off to the Wand trainer with a single click.

It is a self-contained native build. There is no browser, no web service, and
no runtime to install: the bundle carries everything it needs.

## Features

<p align="center">
  <img src="docs/images/features.png" alt="Features" width="100%">
</p>

- **Durable installs.** Downloads and extracts with exact byte and percentage
  progress. An interrupted download resumes on its own, and a finished
  download is never abandoned while the image finishes extracting. Every game
  lands in its own digest-scoped folder, and several can install at once.
- **Play with Wand.** One click opens the Wand trainer app, finds the right
  game, and attaches its trainer. It works even while another game's trainer is
  already running, and re-clicks attach a running session.
- **Real metadata.** Artwork, completion hours, and disk requirements come from
  corroborated Steam, GOG, and HowLongToBeat records. Ambiguous titles and
  version-only tags are never mislabeled.
- **Save awareness.** Backups are written and checksummed when a game exits;
  restores round-trip against the exact saved format and leave unrelated files
  alone.
- **Fast by design.** The catalog is parsed once and cached, so searching,
  filtering, and syncing stay smooth even with thousands of titles.

## In action

<p align="center">
  <img src="docs/images/library-view.png" alt="Library view" width="49%">
  <img src="docs/images/search-view.png" alt="Search results" width="49%">
</p>

## Install

Download the latest release from the
[Releases](https://github.com/Michaelunkai/game-library-manager-native/releases)
page:

- **`GameLibrary-windows-x64.zip`** — extract it anywhere and run
  `GameLibrary.exe`. The .NET runtime, the WPF runtime DLLs, and the Wand
  bridge are all bundled, so nothing else is required.

On first launch the app creates a `data` folder beside the executable and loads
the bundled catalog. Useful flags:

| Flag | Purpose |
|---|---|
| `--offline` | Start without network access |
| `--main-monitor` | Place the window on the primary display |
| `--data-dir <path>` | Use a specific profile folder instead of `data` |

To install a game, open its card and choose **Install selected**, pick a library
folder, and let the durable terminal walk through download, extraction, and
verification while you watch the percentages climb.

## Building from source

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
and Docker Desktop.

```powershell
cd native
.\build.ps1
```

`build.ps1` bundles the catalog from `public\` and publishes a self-contained
x64 package into `native\dist`. Verify the exact executable with:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File native\verify-reliability.ps1 `
  -ExePath native\dist\GameLibrary.exe -EvidenceRoot evidence\verification -Phase Target
```

## Project layout

| Path | Purpose |
|---|---|
| `native\` | C# WPF source, build script, verification harness |
| `public\` | Catalog source bundled into the executable |
| `launcher\` | Compatibility launcher that forwards to the native build |
| `docs\` | Images and acceptance notes |
| `dist\` | Legacy launcher entry point (forwards to `native\dist`) |
| `data\` | Runtime profile: catalog cache, state, and install receipts |

## Requirements

- Windows 10 or 11 (x64)
- Docker Desktop, for installing and extracting games
- A Docker Hub namespace with game images, or a local catalog source

## Notes

- The self-test suite runs 150 checks covering identity, metadata, save
  backup/restore, durable installs, and Wand dispatch.
- Personal state lives in the local `data` profile and is never part of the
  repository.
- See `docs\acceptance-current.md` for the acceptance audit and known gaps.