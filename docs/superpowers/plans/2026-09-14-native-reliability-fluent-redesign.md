# Game Library Native Reliability and Fluent Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild the exact WPF executable with caller-monitor placement, truthful metadata/local size, diagnosable resilient installs, durable synchronization, and a cohesive Fluent 2 interface without regressing existing behavior.

**Architecture:** Preserve the .NET 10 WPF application and isolate new behavior behind pure monitor-selection, installed-size, job-transcript, and save/sync helpers. Extend semantic XAML resources and Windows DWM integration without replacing tested catalog, Wand, Docker, or offline-first flows.

**Tech Stack:** C#/.NET 10 WPF, PowerShell 5-compatible generated install scripts, Windows DWM/WindowChrome interop, JSON atomic storage, Netlify Functions/Blobs and GitHub-backed admin config, Node verification scripts.

---

### Task 1: Caller-monitor startup

**Files:**
- Create: `native/WindowPlacement.cs`
- Modify: `native/Program.cs`
- Modify: `native/MainWindow.xaml.cs`
- Test: `native/SelfTests.cs`

- [ ] Add a failing self-test that passes synthetic monitor work areas and cursor/foreground points to `WindowPlacement.Select` and expects the containing cursor monitor, foreground fallback, and primary fallback.
- [ ] Run a fresh source build and self-test; confirm the new check fails because `WindowPlacement` does not exist.
- [ ] Implement `StartupMonitorMode { Auto, Primary, Secondary }`, pure selection/clamping helpers, and Win32 capture of the startup cursor/foreground monitor.
- [ ] Change the default from forced secondary placement to `Auto`; retain `--main-monitor` and `--second-monitor` overrides.
- [ ] Run the focused and full self-tests and verify the window origin lies inside the selected work area.

### Task 2: Truthful installed-size metadata

**Files:**
- Modify: `native/Models.cs`
- Modify: `native/LibraryStore.cs`
- Modify: `native/InstalledGames.cs`
- Modify: `native/MetadataClient.cs`
- Modify: `native/MainWindow.xaml.cs`
- Test: `native/SelfTests.cs`

- [ ] Add failing tests for recursive installed-byte measurement, inaccessible/reparse-point safety, state round-trip, and UI labels that distinguish `download` from `installed locally`.
- [ ] Run the tests and confirm failure on the missing persisted size fields/helper.
- [ ] Add `InstalledBytes` and `InstalledSizeMeasuredUtc` state maps and a bounded `InstalledSize.Measure` implementation that skips reparse points and catches per-file access failures.
- [ ] Measure only after verified completion or configured installed scans, save atomically, and expose measured local size ahead of Docker download size.
- [ ] Keep exact metadata identity checks and unknown states; never synthesize an image, time, or byte count as provider fact.
- [ ] Run metadata, scanner, state recovery, and full self-tests.

### Task 3: Install transcripts and recovery

**Files:**
- Modify: `native/DockerScripts.cs`
- Modify: `native/JobWindow.cs`
- Modify: `native/InstallReservations.cs`
- Modify: `native/MainWindow.xaml.cs`
- Test: `native/SelfTests.cs`

- [ ] Add failing tests for a visible-terminal transcript path, per-game success/failure records, precise final summary, cancellation while queued, and stale/disposed reservation recovery.
- [ ] Run the tests and confirm they fail on the current exit-code-only log.
- [ ] Bind an operation-scoped transcript environment variable, tee the PowerShell payload output while preserving its real exit code, and tail the transcript into the WPF progress surface.
- [ ] Parse `GAME`, `Completed`, and `FAILED` records into per-game status; show the exact failed title/reason and retry guidance.
- [ ] Ensure the reservation scope releases on every exception/cancellation path and retain existing destination/container ownership checks and atomic promotion rollback.
- [ ] Run PowerShell/BAT syntax checks, mocked Docker failure/retry cases, concurrent reservation tests, cancellation tests, and full self-tests.

### Task 4: Fluent 2 shell and controls

**Files:**
- Modify: `native/Theme.xaml`
- Modify: `native/MainWindow.xaml`
- Modify: `native/MainWindow.xaml.cs`
- Modify: `native/Editors.cs`
- Modify: `native/JobWindow.cs`
- Create: `native/WindowEffects.cs`
- Test: `native/SelfTests.cs`

- [ ] Add failing UI assertions for semantic resources, 40-pixel targets, custom caption controls, focus visuals, high-contrast fallback, live status region, and nonblank empty/error states.
- [ ] Run the UI proof and confirm the new assertions fail against the current shell.
- [ ] Implement semantic light/dark brushes, typography, radii, elevations, scrollbars, menus, buttons, text boxes, combo boxes, checks, list rows, dialogs, and status surfaces.
- [ ] Add `WindowChrome`, Mica/rounded-corner DWM effects, caption commands, keyboard accelerators, and reduced-motion detection while preserving native snap/maximize semantics.
- [ ] Adapt layout at narrow widths and verify long labels, 300% scaling bounds, keyboard traversal, high contrast, dark/light themes, and reduced motion.

### Task 5: Durable mutation and remote sync state

**Files:**
- Modify: `native/LibraryStore.cs`
- Modify: `native/SyncClient.cs`
- Modify: `native/MainWindow.xaml.cs`
- Modify: `native/Editors.cs`
- Modify: `native/backend/admin-config.js`
- Modify: `native/backend/admin-sync.js`
- Modify: `native/backend/app.cas.js`
- Modify: `native/backend/web-integration.js`
- Test: `native/SelfTests.cs`
- Test: `native/backend/verify-cas.cjs`
- Test: `native/backend/verify-admin-sync.cjs`

- [ ] Add failing tests proving a mutation is on disk before HTTP, survives interruption/restart, is acknowledged only after remote read-back, and remains queued on unauthorized/network/conflict responses.
- [ ] Run native and Node sync tests and confirm the new assertions fail.
- [ ] Centralize mutation saves, debounce immediate background sync, expose `Saved locally`, `Syncing`, `Synced`, and `Needs attention`, and keep the durable outbox authoritative until read-back matches.
- [ ] Extend only the remote fields that both native and web clients can safely round-trip; schema-validate and preserve unknown fields so old/new clients do not erase each other.
- [ ] Run backend CAS/SDK/browser logic suites plus native import/offline/reconnect tests.

### Task 6: Icon, packaging, and end-to-end release

**Files:**
- Modify: `native/Assets/GameLibrary.ico`
- Modify: `native/GameLibrary.Native.csproj`
- Modify: `native/build.ps1`
- Modify: `native/install.ps1`
- Modify: `README.md`
- Modify: `native/README.md`

- [ ] Create a calm library/game glyph with vector-clean geometry, transparent padding, and Windows icon sizes from 16 through 256 pixels; verify every frame.
- [ ] Run `git diff --check`, source publish to a new empty distribution directory, and the full self-test from that exact published executable.
- [ ] Run native UI, monitor placement, dark/light, install transcript, script, Wand, and cold-relaunch persistence gates from the new binary.
- [ ] Synchronize the verified distribution to `dist`, install with checksum verification, relaunch, and verify process/window/hash/state persistence.
- [ ] Verify the maintained website/backend candidate; deploy only to the existing Netlify site when authenticated, then verify live conditional writes and browser/native round trips.
- [ ] Commit and push source/tests/docs and publish the verified executable as the GitHub downloadable artifact. Record commit, release URL, Netlify deploy id if deployed, SHA-256, and the exact final executable path.

