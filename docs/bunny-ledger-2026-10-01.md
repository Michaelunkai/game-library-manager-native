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

## Manager integration backlog (after all 8 deliver)

1. Register 8 new test classes in `SelfTests.Run` via `Check(...)`.
2. `ApplyFilter` -> use `SearchPerformance` (slot 2).
3. Context-menu "Delete game and leftovers" -> `GameRemoval` (slot 1).
4. Card completion line -> `CompletionProgress` (slot 4).
5. Tag migration command -> `TagTaxonomy` (slot 3).
6. Backup/Restore buttons -> `SaveDataLocator` + `SaveRestoreCoordinator`, replacing
   `GameSaveOperations.EnsureGameNotRunning` with the Snapshot policy (slots 5, 6).
7. Speed bar + F1/F2/F3 -> `GameSpeedController` + `GameSpeedNative` (slots 7, 8).
8. De-duplicate the `ISpeedApplier` declaration (slot 7 vs slot 8).
9. `dotnet build`, then `--self-test`, then `verify-reliability.ps1`.
