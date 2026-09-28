# Native durable install-terminal repair — 2026-09-28

## Delivered executable

- Path: `F:\study\repos\game-library-manager-native\native\dist\GameLibrary.exe`
- SHA-256: `5BD549C408B5D7DDD1AA7C134C8DDDCED52988ECC00B9D2CD76FD439EF09EE8A`
- Rollbacks: `evidence\native-dist-rollback-20260928T1335Z` (87B42C…), `evidence\native-dist-rollback-20260928T1405Z` (B13810C…)

## Reproduced defect

Selecting a game and clicking **Install selected → Start download** opened the durable
install terminal, which printed real progress and then terminated with:

```
Install terminal failed: The install-job manifest was not found.
The durable install job was left intact. Press any key to close this terminal.
```

The manifest existed and the worker kept pulling, so the terminal (and the parent
window / recovery controller) had failed to *read* it.

## Root cause

`InstallJobStore.SaveCore` wrote `job.json` through `LibraryStore.AtomicWrite`,
which uses `File.Replace`. `File.Replace` is a two-step rename (destination →
backup, then replacement → destination) and transiently exposes a missing
destination. The worker rewrites the manifest on every progress event, so a
concurrent reader (`InstallJobTerminalHost`, the parent monitor, the controller)
could observe the file absent and throw `FileNotFoundException`.

A second effect: concurrent reads also made `AtomicWrite` throw sharing
violations, contributing to the worker being restarted and the visible
`RetryableFailure · The previous worker ended while this stage was running`
notice.

## Fixes

1. `native\InstallJob.cs`
   - `SaveCore` now replaces the manifest with a single overwriting move
     (`File.Move(temp, path, overwrite: true)`), which never exposes an absent
     manifest, with a bounded retry on `IOException`/`UnauthorizedAccessException`.
   - `Load` and `ReadEvents` read through a `FileShare.ReadWrite | FileShare.Delete`
     handle with a bounded retry, tolerating a concurrent replacement.
   - `AppendEventCore` shares the event log with `ReadWrite | Delete`.
2. `native\InstallJobTerminalHost.cs` — the terminal loop retries a transient
   store read instead of terminating the console.
3. `native\InstallJobWorkerHost.cs` — `EnsureWorkerStarted` probes the exclusive
   worker lease and does not launch a duplicate worker while one owns the job,
   removing the spurious recovery notice.
4. `native\MainWindow.xaml.cs` — install review text now names both routes
   explicitly: "Windows BAT / default terminal route (.BAT)" and "WSL2 Ubuntu (.SH)".
5. `native\MetadataEntryPathTests.cs` — simulated verified duration now includes
   `timeQuery`/`timeSamples` required by `CompletionDuration.HasSource`.
6. `native\InstallJobConcurrencyTests.cs` (new) — regression test: a writer saving
   and recording activity while a reader loads the manifest and reads events must
   never throw (invoked from the self-test suite).
7. `native\verify-terminal.ps1` — reworked for the durable terminal route
   (console window + manifest + live event growth); kills the owned Docker child.

## Verification (exact target, isolated profiles)

| Suite | Result |
|---|---|
| Core self-test | 147 / 0 failures (3 consecutive runs) |
| Reliability Stage (staged build) | 12 / 12 |
| Reliability Target (deployed build) | 12 / 12 |
| Durable install-terminal proof | 8 / 8, status running, stages Preflight→ResolveDigest→Pull, 0 `retryable-failure`, 0 `previous worker ended` |

Terminal receipt: `native\evidence\terminal-native-current.json`.
