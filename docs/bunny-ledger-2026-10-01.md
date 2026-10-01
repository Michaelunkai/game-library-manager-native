# bunny team ledger - 2026-10-01

Manager agent: `9e024615-cbd1-4340-a722-6a19c2f2a655`
Repo: `F:\study\repos\game-library-manager-native` (branch `codex/native-reliability-fluent-redesign`)

## Preflight

- Live catalog refreshed from `paseo_list_models(opencode)`: 33 models, 2 free.
  Free ids: `opencode-go-failover/longcat-2.5-preview-free`, `opencode-go-failover/space-bunny-free`
- `resolve_team.py --self-test` -> ok, 7 samples
- `preflight_team_launch.mjs` -> PREFLIGHT PASSED (9 checks)
- Parser: `bunny 8 {goal}` -> valid, requested 8, waves [[1,2,3,4],[5,6,7,8]]
- GOAL HASH: `826a9524595844e533602998dd386b8479ab80e4e81cea61dd1bbcdcc0e43d7b`

## Non-overlap contract

Every worker writes ONLY new dedicated files. All shared hot files are manager-owned:
`native\MainWindow.xaml.cs`, `native\MainWindow.xaml`, `native\SelfTests.cs`,
`native\Models.cs`, `native\Program.cs`, `native\LibraryStore.cs`,
`native\GameSaveOperations.cs`, `native\GameClassification.cs`,
`native\CategoryVisibility.cs`, `native\CompletionDuration.cs`,
`native\AhkGameControl.cs`, `native\GameCardProjection.cs`,
`native\DockerScripts.cs`, `native\InstalledStorage.cs`, the `.csproj`, `build.ps1`.

## Slots

| Slot | Model | Agent id | Assignment | Exclusive write scope |
|---|---|---|---|---|
| 1 | longcat-2.5-preview-free | `809cfa84-3f3c-4af6-b191-c903382b8b8c` | Goal 1 core: game removal engine | `native\GameRemoval.cs`, `native\GameRemovalTests.cs` |
| 2 | space-bunny-free | `fa6ce313-95bb-4071-95c8-54c0441fdc58` | Goal 2 core: zero-lag search/category index | `native\SearchPerformance.cs`, `native\SearchPerformanceTests.cs` |
| 3 | longcat-2.5-preview-free | `4f2a149d-3ebb-44d7-b434-1497ae9845dd` | Goal 3: non-game tag taxonomy | `native\TagTaxonomy.cs`, `native\TagTaxonomyTests.cs` |
| 4 | space-bunny-free | `fd52dc69-e0ca-4888-a799-4128da59e48c` | Goal 4: completion % + hours left | `native\CompletionProgress.cs`, `native\CompletionProgressTests.cs` |
| 5 | longcat-2.5-preview-free | `b32fe818-2abe-471f-8b03-adfa9ca56e23` | Goal 5a: save-data path locator | `native\SaveDataLocator.cs`, `native\SaveDataLocatorTests.cs` |
| 6 | space-bunny-free | `43cacc82-dfd2-4ce2-944e-8abd7f0beb61` | Goal 5b: live backup + safe restore | `native\SaveRestoreCoordinator.cs`, `native\SaveRestoreCoordinatorTests.cs` |
| 7 | longcat-2.5-preview-free | `1ec971e1-0093-4360-8025-3b55acae6967` | Goal 6a: speed model + F1/F2/F3 | `native\GameSpeedController.cs`, `native\GameSpeedControllerTests.cs` |
| 8 | space-bunny-free | `14146227-d7b1-40b6-8764-2aefd25ba192` | Goal 6b: native clock hook + injector | `native\GameSpeedNative.cs`, `native\GameSpeedNativeTests.cs`, `native\tools\gamespeed\` |

## Manager integration backlog (all done)

1. Registered all 8 new test classes in `SelfTests.Run` via `Check(...)`.
2. `ApplyFilter` -> `SearchPerformance.GameMatchPredicate`, with the index rebuilt
   on catalog change, on `Save()`, and whenever the searched state collections
   change size. Without the last two the index went stale on in-place state
   mutation and hid a newly added launch path - caught by the pre-existing
   `Installed launcher path is searchable` UI check.
3. `Game actions` context menu on every card (Play / Back up / Restore / Speed /
   **Delete from all drives** / export submenu) wired to `GameRemoval`.
4. `SpeedByGame` and `SaveDataOverrides` added to `UserState` in `Models.cs`.
5. F1/F2/F3 window-level `PreviewKeyDown` handler, fail-closed on no running game.
6. `RunGameSave` now snapshots the discovered save roots before running the
   existing helper, so real progress is captured while the helper's receipts,
   logs and verification are unchanged.
7. `GameSaveOperations`: added `InspectRunningGame` + `RunningGameState`;
   `EnsureGameNotRunning` is now restore-only and its message says backups are
   unaffected. Added `SaveRootProvider` bridging `SaveDataLocator` ->
   `SaveRestoreCoordinator`, plus `BackupSaveRootsAsync` / `RestoreSaveRootsAsync`.
8. De-duplicated `ISpeedApplier` (slot 7's copy is the survivor).

## Host defect found and repaired

The .NET 10.0.401 SDK at `C:\Program Files\dotnet\sdk\10.0.401\Sdks\` had been
stripped, so every `dotnet build` failed instantly with `MSB4236`. This blocked
all eight workers before any code was written. Repaired by reinstalling
`Microsoft.DotNet.SDK.10` 10.0.401 via winget. Separately, `global.json` is
resolved from the *working directory*, so builds must run from `native\` with the
relative csproj name; that was broadcast to all eight slots.

## Defects the new tests exposed (all fixed, all manager-verified)

| Defect | Where | Fix |
|---|---|---|
| Clock re-seeded from the anchor, so no speed factor ever applied | `gamespeed.c`, `ScaledClock.SetFactor` | always integrate; 1.0x is the identity |
| Clock could rewind when slowed (negative frame delta) | `ScaledClock` | `_lastVirtualRaw` monotonic lift on change |
| Anchored vs unanchored value space mixed | `ScaledClock.Publish` | both now anchor-relative |
| Non-running process reported 0.0x | `NativeSpeedApplier.Inspect` | explicit `NormalFactor` state |
| Elevation text demanded admin for a lower target | `GameSpeedNative.ElevationReason` | made total |
| Backup never located real save roots | `GameSaveOperations` / `RunGameSave` | locator + snapshot wired in |
| Search index stale on in-place state mutation | `MainWindow` | rebuild on `Save()` and on shape change |

Two of the failing assertions were genuine **test** bugs, not product bugs: a
drift expectation that double-counted a 12.35 s anchor, and a shared `now` that
accumulated across loop iterations. Both were corrected and independently
confirmed by reflection (drift exactly 0.000000 ms at 0.5/1.5/2.0/3.0/4.5).

## Final state

- `dotnet build` - 0 errors, 0 warnings.
- `--self-test` - **159 checks, 0 failures** (9 new).
- `--ui-test` - **88 checks, 0 failures**.
- `verify-reliability.ps1 -Phase Target` - **`passed: true`**, 9/9 cases.
- Speed hook measured from an uninjected parent clock: **1.0000x / 2.0042x /
  0.5010x / 4.0039x**.
  An earlier "ratio 1.0002" reading was a measurement artifact - `Stopwatch` uses
  QPC, which the hook patches, so both sides were scaled.
- Release `v1.3.0` with `GameLibrary-windows-x64.zip`.

## Follow-up work after the team completed

### Scroll stutter (fixed)

Scrolling periodically froze. Root cause was a latency cliff, not slow drawing:
both artwork caches called `Clear()` at 2,500 entries, evicting every cover on
screen at once, so the next scroll re-decoded each on the UI thread - and a cache
miss on a content-addressed `.img` SHA256-hashes up to 8 MB. `ArtworkFile.Replaced`
had the same cliff via an epoch bump. Both now evict LRU only. The progress line
stopped wrapping (uniform card heights stop the virtualizing panel re-measuring),
and scroll-driven metadata work is debounced on a 220 ms idle timer.
New check pins the LRU retention.

### 32-bit speed hook (investigated, deliberately not shipped)

The WinLibs MinGW-w64 is `--disable-multilib`, so LLVM-MinGW `UCRT` was installed
to provide an i686 sysroot. `gamespeed32.dll` builds with no diagnostics and is a
genuine `coff-i386` / `PE32` DLL with all four exports. In a real x86 child it
installs all five hooks and applies the factor, then faults `0xC0000005`; the
diagnostic trace stops at `applied factorFp=131072 scaled=1` with no `HOOK` line,
so the fault is inside `GameSpeed_Set` after installation.

Shipping it would crash 32-bit games, so it is excluded from the package via
`<None Remove="tools\gamespeed\gamespeed32.dll" />` and 32-bit is refused. The
suite asserts both the absence of the file and the refusal, so enabling it later
fails loudly. Full trace and next steps are in
`native\tools\gamespeed\README.md`.


