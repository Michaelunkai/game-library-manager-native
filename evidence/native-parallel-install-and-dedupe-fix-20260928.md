# Native install handoff, live progress, parallel installs, and name dedupe — 2026-09-28

## Delivered executable

- Path: `F:\study\repos\game-library-manager-native\native\dist\GameLibrary.exe`
- SHA-256: `F9BF4361C932B4F10B5C90FC967602FA79E9A87C2F51671E1E8660DBF8C2CE07`
- Rollbacks: `evidence\native-dist-rollback-20260928T1405Z` (B13810C…), `…T1405Z`/`…T1440Z` (5BD549C…), `…T1443Z` (DEE8E364…)

## Reproduced defect (worker handoff)

The terminal showed, mid-run:

```
[job] Preflight · RetryableFailure · digest resolving · The previous worker ended while this stage was running; verify process ownership and retry the same pinned job.
```

This is written to `job.json` (not the event log) by `InstallJobWorkerHost` when a fresh
worker starts and sees a leftover `Running` status from a worker that ended. It required
a manual retry for an install that was otherwise fine.

## Fixes

1. `InstallJobWorkerHost.cs`
   - `EnsureWorkerStarted` probes the exclusive worker lease so a second worker is never
     launched while one owns the job.
   - A worker that finds a reconciled `Running` job now **auto-resumes** the same pinned
     stage (emitting a non-error `recovering` event) instead of marking `RetryableFailure`.
     Only a held user control request (pause/resume/stop) still surfaces a retryable state.
2. `InstallProcessRunner.cs` — parses Docker layer byte ratios (`x/y MB`) into a
   per-layer byte aggregate, exposed as `CompletedBytes`/`TotalBytes` on every command
   event and on the stage result.
3. `InstallJobDockerDriver.cs` — forwards the command byte totals into progress events and
   sets the exact staged-payload byte total during extraction.
4. `InstallJobTerminalHost.cs` — renders a live percentage for byte and file progress
   (`50.0%`).
5. `GameIdentityIndex.cs` + `GameCardProjection.cs` + `MainWindow.xaml.cs` — optional
   display-name edge (case-insensitive, whitespace-normalized, never for numeric/version
   tags) collapses duplicate names to one card; the representative is the most recently
   pushed tag (authoritative Docker Hub `last_updated`, else the durable per-tag push time).
6. Install review text names both routes explicitly (`.BAT` default terminal / `.SH`).

## New deterministic tests (self-test count 137 → 150)

- `InstallJobConcurrencyTests` — concurrent manifest write/read never throws.
- `InstallProgressTests` — layer byte-ratio parsing, aggregate byte percent, extraction
  file percent, and terminal percentage rendering.
- `ParallelInstallTests` — six games resolve to six distinct staging + digest-scoped
  installation folders; six downloads and six promotions run concurrently; the same
  installation path serializes only for its replacement window.
- `NameDedupTests` — duplicate names collapse to one card and the newest pushed tag wins,
  while verified identity grouping stays unchanged.

## Verification (exact target)

| Suite | Result |
|---|---|
| Core self-test | 150 / 0 failures |
| Reliability Target | 12 / 12 |
| Durable install terminal | 8 / 8, byte progress observed, 0 `retryable-failure`, 0 `previous worker ended`, 0 `recovering` |
| Process teardown | no stray GameLibrary or Docker pull processes |

Terminal receipt: `native\evidence\terminal-native-current.json`.
