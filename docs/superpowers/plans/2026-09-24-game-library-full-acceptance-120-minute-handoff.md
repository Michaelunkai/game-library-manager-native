# Game Library Full Acceptance and Safe Cleanup 120-Minute Execution Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:dispatching-parallel-agents` for the independent read-only audits, then `superpowers:executing-plans` for implementation and deployment. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close every requirement for the Windows executable at `F:\study\repos\game-library-manager-native\native\dist\GameLibrary.exe`, prove each requirement with current evidence, and perform maximum safe project cleanup only if every gate passes.

**Architecture:** Treat acceptance as a fail-closed evidence matrix. Run independent audits in parallel against the exact executable and isolated profiles, fix only reproduced failures, build one fresh staged release, verify it before and after deployment, and permit cleanup only after every matrix row is green. Preserve the real profile, game saves, and installed games throughout.

**Tech Stack:** .NET 10 Windows WPF, PowerShell 7 and Windows PowerShell 5.1 UI Automation, Python 3.14/pytest, Docker Hub read APIs, JSON evidence receipts.

---

## Trust and time boundary

This plan has a hard wall-clock ceiling of 120 minutes: 110 minutes for work and 10 minutes for contingency and reporting. It does **not** redefine the original requirements to make them easier.

The current evidence already proves that several literal requirements are not presently established:

- Shared synchronization has returned HTTP 503 `usage_exceeded`; local code cannot guarantee that an external service will never fail.
- A future game can have no readable save, achievement, campaign denominator, or trustworthy metadata identity. A correct percentage cannot be created from absent evidence.
- Kristala lacks a verified campaign denominator, and the available Outlaws backup lacks campaign save data.
- Only 7 of 136 previously audited unique launch paths had detected save sources; 127 had none, with two emulator/wrapper identities unresolved.
- A finite Docker snapshot cannot prove zero-latency synchronization for every future push and outage.
- A controlled pause/resume process tree cannot prove flawless behavior for every game engine and launcher without running those games.

Therefore the only trustworthy promise is: **within 120 minutes, the session must either produce current evidence that every gate passed and safely clean the project, or explicitly report the remaining red gates and perform no cleanup.** It must never claim universal completion from finite tests, fabricate progress, or delete evidence while a gate is red.

## Current authoritative baseline

- Repository: `F:\study\repos\game-library-manager-native`
- Current exact target SHA-256: `93BABDB5AF9936DAA5D3E3F0457A7162CB6EC9B153878884B9F0AE779CC4EE28`
- Current release: `r56c`
- Current exact-target evidence: 126 regression checks and 82 WPF checks pass.
- Live personal-state evidence: 007 First Light is first in Installed, category Action, Played 12.1 h, sort Recently Played.
- GOG proof: The Red Strings Club resolves from its primary executable manifest, reloads its title and artwork, displays about 4 hours, and displays measured installed size.
- Compatibility launcher: `F:\study\repos\game-library-manager-native\dist\GameLibrary.exe` activates the native target without rewriting the profile.
- Full acceptance remains open. No cleanup has been performed.

## Non-negotiable operating rules

- [ ] Work on Windows only. Do not deploy to Netlify or any website.
- [ ] Read the original requirements from `C:\Users\Admin\.codex\attachments\4a56fbd2-30ec-4e97-87be-b65f9e1b0967\pasted-text-1.txt` before changing code.
- [ ] Read `todo.md`, `docs/acceptance-current.md`, and the latest release receipts before changing code.
- [ ] Preserve the dirty worktree. Do not reset, checkout, clean, or overwrite unrelated changes.
- [ ] Do not ask the user for in-game checkpoint percentages. Some games do not expose them; the application exists to derive them when evidence permits.
- [ ] Do not launch Dragon Quest VII's `Launcher.exe`; its menu scripts can copy or delete emulator files.
- [ ] Do not perform a live save restore. Restore only isolated copies through the existing verification scripts.
- [ ] Do not launch real games merely to satisfy pause or exit tests. Use the packaged controlled-process proof unless the user explicitly authorizes a real launch.
- [ ] Do not change Docker billing or remote repositories. Docker checks are read-only.
- [ ] If Docker authentication is actually missing, run `F:\study\Platforms\windows\functions\Invoke-DockerHubLogin.ps1` in its own command. Never print credentials.
- [ ] Keep process launches, URL-bearing probes, cleanup, and verification in separate PowerShell invocations.
- [ ] Do not use forced recursive deletion. Resolve every cleanup target, reject reparse points, and use `Remove-Item -LiteralPath` without `-Force` only after the final gate passes.
- [ ] Keep at most one source-changing agent active at a time. Parallel agents may perform read-only audits only.

## File map

### Core runtime

- `native/Program.cs` — diagnostic entrypoints, UI startup, exact profile mutex.
- `native/MainWindow.xaml` and `native/MainWindow.xaml.cs` — cards, Installed view, filters, persistence wiring, scan application.
- `native/Editors.cs` — Play, Wand, Backup, Restore, and pause/resume actions.
- `native/GamePause.cs` and `native/GamePauseProof.cs` — suspension guardian and controlled parent/child proof.
- `native/GameSaveOperations.cs` — exact `ass` and `reass` dispatch.
- `native/GameProgressClient.cs` — native reader for `HOW_MUCH_LEFT.bat` data.
- `native/DockerNamespaceClient.cs`, `native/DockerHubAccess.cs`, `native/SyncClient.cs`, and `native/NamespaceUpdates.cs` — authenticated namespace synchronization and durable merge behavior.
- `native/MetadataClient.cs`, `native/SteamMetadata.cs`, `native/CatalogIdentity.cs`, `native/GogInstalledIdentity.cs`, and `native/InstalledMetadataIdentity.cs` — title identity, artwork, completion time, and metadata scheduling.
- `native/InstalledGames.cs`, `native/InstalledStorage.cs`, and `native/LibraryStore.cs` — local discovery, installed size, state loading, and persistence.
- `native/WandIntegration.cs` and `native/WandLiveLibrary.cs` — current Wand membership and launcher matching.

### Existing regression coverage

- `native/SelfTests.cs`
- `native/GogInstalledIdentityTests.cs`
- `native/InstalledMetadataIdentityTests.cs`
- `native/LocalScanNameTests.cs`
- `native/InstalledStorageTests.cs`
- `native/SyncBackoffTests.cs`
- `native/SyncResilienceTests.cs`
- `native/PersonalEditingTests.cs`
- `native/ReadableMetadataTitleTests.cs`
- `native/SteamMetadataTests.cs`

### External progress implementation

- `F:\study\projects\games\tools\gameprogress\HOW_MUCH_LEFT.bat`
- `F:\study\projects\games\tools\gameprogress\src\gameprogress\`
- `F:\study\projects\games\tools\gameprogress\tests\`

### Build and acceptance helpers

- `native/build.ps1`
- `evidence/deploy-criteria-build.ps1`
- `evidence/audit-backup-coverage.ps1`
- `evidence/audit-r48-data-coverage.py`
- `evidence/verify-real-backup-roundtrips.py`
- `evidence/verify-r33-personal-state.ps1`
- `evidence/verify-r33-profile-preservation.py`
- `evidence/verify-gog-profile-ui.ps1`
- `evidence/compare-r48-namespace.py`

### Fixed tool paths

- .NET SDK: `F:\study\temp\glm-native-build\dotnet-sdk-10.0.400\dotnet.exe`
- Python: `C:\Users\Admin\AppData\Local\Python\pythoncore-3.14-64\python.exe`
- Windows PowerShell 5.1: `C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe`
- Metadata audit harness: `F:\study\temp\glm-native-build\duration-input-audit\bin\Debug\net10.0-windows\Audit.dll`
- GOG live harness: `F:\study\temp\glm-native-build\gog-live-proof\bin\Debug\net10.0-windows\Audit.dll`

## 120-minute execution schedule

### Task 1: Freeze the baseline and start the clock — minute 0 to 5

**Files:** read only.

- [ ] **Step 1 (1 minute): Record the start time and exact target identity.**

Run from the repository root:

```powershell
Get-Date -AsUTC
Get-FileHash -Algorithm SHA256 -LiteralPath 'F:\study\repos\game-library-manager-native\native\dist\GameLibrary.exe'
Get-Process GameLibrary -ErrorAction SilentlyContinue | Select-Object Id,Path,StartTime
```

Expected baseline hash: `93BABDB5AF9936DAA5D3E3F0457A7162CB6EC9B153878884B9F0AE779CC4EE28`. If it differs, treat the current file as authoritative and do not reuse r56c receipts as proof for it.

- [ ] **Step 2 (2 minutes): Read the requirements and current acceptance record.**

```powershell
Get-Content -LiteralPath 'C:\Users\Admin\.codex\attachments\4a56fbd2-30ec-4e97-87be-b65f9e1b0967\pasted-text-1.txt' -Raw
Get-Content -LiteralPath 'F:\study\repos\game-library-manager-native\docs\acceptance-current.md' -Raw
```

- [ ] **Step 3 (2 minutes): Capture worktree state without modifying it.**

```powershell
git status --short
git diff --stat
```

Do not attempt to make the worktree clean. Existing modifications are part of the current application state.

### Task 2: Dispatch six independent read-only audits — minute 5 to 25

Use `superpowers:dispatching-parallel-agents`. Give every agent one lane, forbid source edits, and require a concise JSON-or-text receipt. All lanes may run concurrently.

#### Lane A: exact executable and UI baseline

- [ ] **Step A1: Run exact-target regression checks.**

```powershell
$taskTarget='F:\study\repos\game-library-manager-native\native\dist\GameLibrary.exe'
$taskReport='F:\study\repos\game-library-manager-native\evidence\criteria-r57-pre-selftest.json'
$taskProcess=Start-Process -FilePath $taskTarget -ArgumentList @('--self-test',$taskReport) -WindowStyle Hidden -PassThru -Wait
if($taskProcess.ExitCode -ne 0){throw 'Exact-target self-test failed'}
Get-Content -LiteralPath $taskReport -Raw | ConvertFrom-Json | Select-Object passed,tests,failures,executable
```

Expected: `passed=True`, `tests=126`, `failures=0`, exact executable path.

- [ ] **Step A2: Run WPF checks in an isolated profile.**

```powershell
$taskTarget='F:\study\repos\game-library-manager-native\native\dist\GameLibrary.exe'
$taskProfile='F:\study\repos\game-library-manager-native\evidence\criteria-ui-r57-pre'
$taskReport='F:\study\repos\game-library-manager-native\evidence\criteria-r57-pre-ui.json'
$taskProcess=Start-Process -FilePath $taskTarget -ArgumentList @('--data-dir',$taskProfile,'--offline','--ui-test',$taskReport) -WindowStyle Hidden -PassThru -Wait
if($taskProcess.ExitCode -ne 0){throw 'Exact-target UI test failed'}
```

Expected: report `passed=True` with 82 checks.

#### Lane B: Docker namespace and retry behavior

- [ ] **Step B1: Run the packaged storage/backoff/resilience proof.**

```powershell
$taskTarget='F:\study\repos\game-library-manager-native\native\dist\GameLibrary.exe'
$taskReport='F:\study\repos\game-library-manager-native\evidence\criteria-r57-pre-storage-sync.json'
$taskProcess=Start-Process -FilePath $taskTarget -ArgumentList @('--storage-sync-proof',$taskReport) -WindowStyle Hidden -PassThru -Wait
if($taskProcess.ExitCode -ne 0){throw 'Storage/sync proof failed'}
Get-Content -LiteralPath $taskReport -Raw
```

Expected: `passed=true`, storage 22, backoff 23, resilience 34.

- [ ] **Step B2: Run an authenticated read-only namespace proof in a new isolated directory.**

Create a new empty directory under `evidence` with `New-Item`. Then run:

```powershell
$taskTarget='F:\study\repos\game-library-manager-native\native\dist\GameLibrary.exe'
$taskProfile='F:\study\repos\game-library-manager-native\evidence\namespace-r57-cold'
$taskReport='F:\study\repos\game-library-manager-native\evidence\criteria-r57-namespace-cold.json'
$taskProcess=Start-Process -FilePath $taskTarget -ArgumentList @('--namespace-read-proof',$taskReport,'--data-dir',$taskProfile) -WindowStyle Hidden -PassThru -Wait
if($taskProcess.ExitCode -ne 0){throw 'Namespace proof failed'}
```

Expected: no remote writes, no errors, a complete namespace result, and repository count at least 1325. A changed count is valid only when the snapshot is complete.

- [ ] **Step B3: Record the external limit honestly.**

Even if the snapshot is perfect, mark “every future push through every future outage with zero delay” as unproven. Do not translate a finite successful read into a permanent guarantee.

#### Lane C: metadata, covers, hours, and installed size

- [ ] **Step C1: Run the read-only metadata harness against the current build assembly.**

Use the assembly matching the deployed r56c build:

```powershell
& 'F:\study\temp\glm-native-build\dotnet-sdk-10.0.400\dotnet.exe' `
  'F:\study\temp\glm-native-build\duration-input-audit\bin\Debug\net10.0-windows\Audit.dll' `
  'F:\study\temp\glm-native-build\output-r11\Release\net10.0-windows\win-x64\GameLibrary.dll' `
  'F:\study\repos\game-library-manager-native\data' `
  'F:\study\repos\game-library-manager-native\evidence\criteria-r57-pre-metadata-audit.json'
```

- [ ] **Step C2: Summarize current coverage.**

```powershell
& 'C:\Users\Admin\AppData\Local\Python\pythoncore-3.14-64\python.exe' `
  'F:\study\repos\game-library-manager-native\evidence\audit-r48-data-coverage.py' --release r57
```

The harness output for this step must therefore be named `evidence\criteria-r57-metadata-audit.json`. The final r57 audit later replaces this preliminary receipt with evidence from the matching r57 assembly. Do not weaken the source guards.

- [ ] **Step C3: Classify every installed row.**

Green requires all installed rows to have:

1. exact attributable identity;
2. decodable matching artwork;
3. positive completion hours with accepted source title and URL;
4. an existing installation folder;
5. a complete measured local logical-byte count;
6. no conflicting platform or catalog identity.

Any missing or ambiguous row keeps the metadata gate red. Whole-catalog and future-game universality remain red unless the identity resolver is source-driven rather than a finite correction table.

#### Lane D: backups and successful-exit dispatch

- [ ] **Step D1: Run detect-only coverage against the real profile.**

```powershell
& 'F:\study\repos\game-library-manager-native\evidence\audit-backup-coverage.ps1' `
  -Profile 'F:\study\repos\game-library-manager-native\data' -Suffix 'r57-pre'
```

- [ ] **Step D2: Re-run isolated real-format round trips.**

```powershell
& 'C:\Users\Admin\AppData\Local\Python\pythoncore-3.14-64\python.exe' `
  'F:\study\repos\game-library-manager-native\evidence\verify-real-backup-roundtrips.py'
```

Expected: sources and backups unchanged; isolated restored bytes exactly match.

- [ ] **Step D3: Keep unsupported ownership red.**

Do not map Dragon Quest VII or King’s Bounty emulator saves unless the selected executable, title ID, ROM, and save root all corroborate one another. A wrapper with no ROM argument is not ownership proof.

#### Lane E: progress and native agreement

- [ ] **Step E1: Run all helper tests.**

```powershell
Set-Location -LiteralPath 'F:\study\projects\games\tools\gameprogress'
& 'C:\Users\Admin\AppData\Local\Python\pythoncore-3.14-64\python.exe' -m pytest -q
```

Expected: all tests pass; no network prompt and no save writes.

- [ ] **Step E2: Run the exact batch file offline.**

```powershell
& 'F:\study\projects\games\tools\gameprogress\HOW_MUCH_LEFT.bat' --no-network
```

Require latest valid backup selection, three decimal display, and no literal `unknown`. Three decimals are formatting and do not prove 0.001-percentage-point accuracy.

- [ ] **Step E3: Re-run exact native 007 UI agreement.**

```powershell
& 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -NoProfile `
  -File 'F:\study\repos\game-library-manager-native\evidence\verify-007-live-progress.ps1'
```

The batch and native value must derive from the same latest backup. Kristala and Outlaws remain red unless this run discovers and validates authoritative campaign semantics; do not infer percentages from quest-step counts or playtime.

#### Lane F: persistence, Wand count, Installed order, and compatibility

- [ ] **Step F1: Start the exact target normally only if no matching process is running.**

Run the process launch in its own command, with no URL text. Record its PID in `evidence/criteria-r57-pre-process.txt`.

- [ ] **Step F2: Verify live personal state and Wand count.**

```powershell
& 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -NoProfile `
  -File 'F:\study\repos\game-library-manager-native\evidence\verify-r33-personal-state.ps1' `
  -Release r57-pre -Suffix r57-pre-installed -SelectInstalled
```

Require 007 First Light first, Action, Played 12.1 h, Recently Played; the included count must be read from current Wand membership, never compared to a hardcoded 39 or 45.

- [ ] **Step F3: Verify persisted fields after refresh.**

```powershell
& 'C:\Users\Admin\AppData\Local\Python\pythoncore-3.14-64\python.exe' `
  'F:\study\repos\game-library-manager-native\evidence\verify-r33-profile-preservation.py'
```

Require all ten preservation checks to pass.

### Task 3: Build the acceptance matrix — minute 25 to 30

**File to create with `apply_patch`:** `evidence/criteria-r57-pre-acceptance.md`

- [ ] **Step 1: Create one row for every original and later requirement.**

Use exactly these rows:

1. Play controls appear only for installed games.
2. Pause/resume suspends the game and descendants and recovers from library/helper exit.
3. Shared/Docker synchronization retains edits during failure and refreshes every current repository/tag.
4. New future Docker entries become visible through polling without hardcoded repository names.
5. Backup and Restore dispatch the selected game to the exact verified helpers.
6. Successful game exit queues backup; failed or cancelled launch does not.
7. Every installed/current/future game gets attributable title, image, hours, and storage values.
8. `HOW_MUCH_LEFT.bat` selects the latest valid backup and reports evidence-backed percentage and hours.
9. Native progress agrees with the batch source.
10. No user-visible literal `unknown` remains.
11. Every edit, category, tab, rating, tag, wishlist, playtime, and folder move survives refresh/restart/failure.
12. Installed defaults to Recently Played and most recently played game first.
13. Included count comes from current provider membership.
14. Exact target, compatibility launcher, and build workflow pass.
15. Project cleanup occurs only after rows 1–14 are green.

- [ ] **Step 2: Give each row one status: `PASS`, `FAIL`, or `UNPROVEN`.**

For every `PASS`, cite a current receipt generated in this run. Historical receipts may explain behavior but cannot alone establish current acceptance. `UNPROVEN` is not a pass.

- [ ] **Step 3: Apply the fail-closed decision.**

If any row is `FAIL` or `UNPROVEN`, cleanup is forbidden. Continue with finite repairs until minute 80, but do not lower the gate.

### Task 4: Implement only reproduced finite failures — minute 30 to 65

Keep one source-changing agent. Use test-driven development: add a failing test, run it red, make the smallest source change, run the focused test green, then run the full self-test.

#### Repair A: platform-ID-confirmed local identity

**Files:**

- Create: `native/InstalledPlatformIdentity.cs`
- Create: `native/InstalledPlatformIdentityTests.cs`
- Modify: `native/InstalledMetadataIdentity.cs`
- Modify: `native/Models.cs`
- Modify: `native/SteamMetadata.cs`
- Modify: `native/MetadataClient.cs`
- Modify: `native/LibraryStore.cs`
- Modify: `native/SelfTests.cs`

- [ ] **Step A1: Add red tests for exact platform identity.**

Tests must cover:

- Unity `app.info` title plus corroborating numeric Steam App ID resolves the public title.
- Unity internal project name such as `Zebra` is never used as the public title by itself.
- conflicting App IDs reject automatic resolution;
- an App ID outside the selected executable’s regular installation root is ignored;
- absolute/traversal/reparse paths are rejected;
- file count is bounded and each identity file is size-bounded;
- a custom user display name is preserved while `MetadataLookupTitle` receives the corroborated title;
- category, AddedUtc, rating, launcher, and history remain unchanged.

- [ ] **Step A2: Implement the bounded local resolver with this contract.**

```csharp
internal sealed record InstalledPlatformIdentity(int SteamAppId, string LocalTitle, string EvidencePath);

internal static InstalledPlatformIdentity? Resolve(string folder, string? executable)
```

The resolver must accept only a fully qualified existing selected executable below a regular non-reparse root. It may read bounded known identity files such as `steam_appid.txt`, `steam_emu.ini`, and `flt.ini`. It must require exactly one nonzero App ID. It must return `null` on absence, conflict, malformed data, excessive size/count, or I/O denial. `app.info` may provide a local title hint but may not establish a public identity alone. Do not search parent drives or unrelated library folders.

- [ ] **Step A3: Carry the exact App ID through metadata lookup.**

Add this non-persisted property to `Game`:

```csharp
[JsonIgnore] public int MetadataSteamAppId { get; set; }
```

`LibraryStore.LoadGames` must populate it from `InstalledPlatformIdentity.Resolve`. When cached metadata has the same positive `steamAppId`, a valid `matchedTitle`, and direct Steam attribution, use that title as `MetadataLookupTitle`. A mismatched cached App ID must be rejected.

Add a direct-ID branch to `SteamMetadata.Read`: when `game.MetadataSteamAppId > 0`, skip title search, request appdetails for that exact ID, require `success=true`, matching `steam_appid`, and `type=game`, then return the canonical Steam title, image, and disk requirement. `MetadataClient.MatchesGame` may accept a differing folder label only when the returned positive `steamAppId` exactly equals `game.MetadataSteamAppId`; ordinary title-based rows retain the existing exact-title rules.

- [ ] **Step A4: Integrate without overwriting personal labels.**

Resolution order:

1. exact primary-executable GOG manifest;
2. cached canonical title tied to the exact local platform ID;
3. deterministic installer marker;
4. existing personal/default folder label.

On the first load with only an App ID, schedule metadata immediately. After the exact-ID response is cached, the next in-memory refresh/reload must use its canonical title. Only the metadata lookup title and a default folder-derived display label may be enriched automatically. Never replace a custom `LocalGame.Name` during scans. Preserve category, AddedUtc, rating, launcher, installed membership, playtime, and last-played time.

#### Repair B: synchronization correctness under finite failures

**Files:**

- Modify only if a new red test exists: `native/DockerNamespaceClient.cs`, `native/SyncClient.cs`, `native/NamespaceUpdates.cs`
- Test: `native/DockerNamespaceTests.cs`, `native/SyncBackoffTests.cs`, `native/SyncResilienceTests.cs`

- [ ] **Step B1: Add red fixtures only for observed failures.**

Fixtures must explicitly cover 401 token refresh, 429 retry-after, 500/502/503/504 bounded retry, one malformed repository, one unavailable repository, pagination beyond 1000 repositories, unchanged cache, and a newly appearing repository/tag. Personal edits and the previous complete catalog must survive every failed attempt.

- [ ] **Step B2: Preserve last complete state and merge successful independent pages.**

Do not clear a previous complete snapshot when one source is unavailable. Persist attempt status separately from the last complete catalog. Never write remote data during proof modes.

#### Repair C: persistence failure found by Lane F

**Files:** `native/PersonalGameEdits.cs`, `native/LocalCatalogEdits.cs`, `native/LibraryStore.cs`, corresponding tests.

- [ ] **Step C1: Reproduce the exact mutation sequence in an isolated profile.**

The test must apply the same UI operation twice, refresh, restart, and inject a write failure. It must compare all untouched fields byte-for-byte or structurally as appropriate.

- [ ] **Step C2: Commit state atomically.**

Validate a clone, write one atomic file, then publish the committed state. On failure retain the prior in-memory and on-disk state. Do not add broad migrations unrelated to the reproduced failure.

#### Repair D: progress only when semantics are authoritative

**Files:** external `gameprogress` source/tests and `native/GameProgressClient.cs` only if new evidence exists.

- [ ] **Step D1: Require a named numerator and denominator source.**

A numerical percentage needs both:

- a decoded current campaign position with documented meaning; and
- a validated ordered campaign total for the same edition.

Do not turn elapsed playtime, two completed quest steps, filename order, arbitrary checkpoint counts, or HLTB averages into “correct progress.” If either source is missing, leave this gate `UNPROVEN`; changing the wording does not close it.

### Task 5: Freeze source changes and build one fresh release — minute 65 to 80

Choose the first unused release matching `r57`, `r57a`, `r57b`, and so on. Never reuse or delete an existing stage directory.

- [ ] **Step 1: Run helper tests before the native build.**

```powershell
Set-Location -LiteralPath 'F:\study\projects\games\tools\gameprogress'
& 'C:\Users\Admin\AppData\Local\Python\pythoncore-3.14-64\python.exe' -m pytest -q
```

- [ ] **Step 2: Build a fresh self-contained stage.**

For release `r57`:

```powershell
Set-Location -LiteralPath 'F:\study\repos\game-library-manager-native'
& 'native\build.ps1' -SkipAssets `
  -DotnetPath 'F:\study\temp\glm-native-build\dotnet-sdk-10.0.400\dotnet.exe' `
  -DistributionRoot 'F:\study\repos\game-library-manager-native\native\dist-criteria-20260922-r57' `
  -BuildOutputRoot 'F:\study\temp\glm-native-build\output-r57' `
  *> 'evidence\criteria-r57-build.log'
```

Expected: exit 0, staged `GameLibrary.exe` exists, SHA-256 recorded.

- [ ] **Step 3: Run staged self-test and WPF test.**

```powershell
$taskStage='F:\study\repos\game-library-manager-native\native\dist-criteria-20260922-r57\GameLibrary.exe'
$taskSelf='F:\study\repos\game-library-manager-native\evidence\criteria-r57-selftest.json'
$taskProcess=Start-Process -FilePath $taskStage -ArgumentList @('--self-test',$taskSelf) -WindowStyle Hidden -PassThru -Wait
if($taskProcess.ExitCode -ne 0){throw 'Staged self-test failed'}
```

Then, in a separate invocation:

```powershell
$taskStage='F:\study\repos\game-library-manager-native\native\dist-criteria-20260922-r57\GameLibrary.exe'
$taskProfile='F:\study\repos\game-library-manager-native\evidence\criteria-ui-r57-fresh'
$taskUi='F:\study\repos\game-library-manager-native\evidence\criteria-r57-ui.json'
$taskProcess=Start-Process -FilePath $taskStage -ArgumentList @('--data-dir',$taskProfile,'--offline','--ui-test',$taskUi) -WindowStyle Hidden -PassThru -Wait
if($taskProcess.ExitCode -ne 0){throw 'Staged WPF test failed'}
```

Require every check to pass and each report’s executable path to equal the fresh stage.

- [ ] **Step 4: Re-run focused live-source harnesses against the matching new DLL.**

Run the metadata harness:

```powershell
& 'F:\study\temp\glm-native-build\dotnet-sdk-10.0.400\dotnet.exe' `
  'F:\study\temp\glm-native-build\duration-input-audit\bin\Debug\net10.0-windows\Audit.dll' `
  'F:\study\temp\glm-native-build\output-r57\Release\net10.0-windows\win-x64\GameLibrary.dll' `
  'F:\study\repos\game-library-manager-native\data' `
  'F:\study\repos\game-library-manager-native\evidence\criteria-r57-metadata-audit.json'
```

Run its coverage summary:

```powershell
& 'C:\Users\Admin\AppData\Local\Python\pythoncore-3.14-64\python.exe' `
  'F:\study\repos\game-library-manager-native\evidence\audit-r48-data-coverage.py' --release r57
```

Run the GOG live harness against an isolated profile:

```powershell
& 'F:\study\temp\glm-native-build\dotnet-sdk-10.0.400\dotnet.exe' `
  'F:\study\temp\glm-native-build\gog-live-proof\bin\Debug\net10.0-windows\Audit.dll' `
  'F:\study\temp\glm-native-build\output-r57\Release\net10.0-windows\win-x64\GameLibrary.dll' `
  'F:\study\repos\game-library-manager-native\evidence' `
  'F:\study\repos\game-library-manager-native\evidence\criteria-r57-gog-live-source.json'
```

Do not use the r56c DLL to certify r57. Require both harness receipts to report success before deployment.

### Task 6: Deploy and prove the exact target — minute 80 to 100

- [ ] **Step 1: Deploy only after staged reports are green.**

```powershell
Set-Location -LiteralPath 'F:\study\repos\game-library-manager-native'
& 'evidence\deploy-criteria-build.ps1' -Release r57 `
  -UiProfile 'F:\study\repos\game-library-manager-native\evidence\criteria-ui-r57-fresh'
```

The helper must close normally, back up the prior executable and both profile state locations, copy by exact path, compare hashes, run exact-target self/UI checks, and reopen the normal app. If Windows retains a transient lock, verify no matching process remains, verify the old hash is unchanged, obtain exclusive read access, and retry once. Do not force-kill or blindly repeat.

- [ ] **Step 2: Re-run live personal-state and profile-preservation checks.**

Require the exact target path, 007 first/Action/12.1 h/Recently Played, dynamic Wand count, and all ten preservation checks.

- [ ] **Step 3: Verify compatibility activation.**

Record the native PID and start time, run `dist\GameLibrary.exe`, require exit code 0, and require the same native PID/start time afterward. Save `evidence/criteria-r57-compatibility-activation.json`.

- [ ] **Step 4: Verify the GOG isolated profile through the exact target.**

```powershell
& 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -NoProfile `
  -File 'F:\study\repos\game-library-manager-native\evidence\verify-gog-profile-ui.ps1' -Release r57
```

Require exact local ID, “The Red Strings Club,” sourced hours, and installed size.

### Task 7: Final requirement audit — minute 100 to 110

**Files:**

- Modify: `docs/acceptance-current.md`
- Modify: `docs/criteria-audit-20260922.md`
- Modify: `README.md`
- Modify: `todo.md`

- [ ] **Step 1: Rebuild the matrix using only final-release receipts.**

Every row must include the final executable hash and direct evidence. A pass requires evidence matching the scope of the claim.

- [ ] **Step 2: Apply the universal-claim rules.**

The following remain red unless independently proven in this run:

- no external service can ever return an error;
- every future game will expose identity, artwork, hours, size, saves, and campaign progress;
- every real game/launcher can be paused and resumed flawlessly;
- every future Docker push appears with zero delay;
- progress is accurate to 0.001 percentage points for every game.

A fallback label, retry loop, cached value, or finite sample cannot prove these statements.

- [ ] **Step 3: Decide cleanup eligibility.**

Cleanup is eligible only if rows 1–14 are all `PASS` and there are zero `FAIL`/`UNPROVEN` rows. Otherwise document the exact remaining rows, leave cleanup untouched, and stop by minute 120.

### Task 8: Conditional safe cleanup — minute 110 to 120 only if every gate passed

**Do not execute this task while any gate is red.**

- [ ] **Step 1: Inventory exact candidates and sizes without deletion.**

Candidates may include old `native\dist-criteria-20260922-*` stage directories, isolated `evidence\test-data-*` fixtures, isolated `evidence\gog-live-*` profiles, obsolete `pre-*-release-*` rollbacks, old root `dist-*` bundles, and superseded build outputs. Exclude all reparse points.

- [ ] **Step 2: Preserve required artifacts.**

Never remove:

- `native\dist\GameLibrary.exe` or its adjacent runtime/tools;
- `dist\GameLibrary.exe` compatibility launcher;
- `data\` or `native\data\`;
- source, project, build, documentation, or final verification scripts;
- current final receipts and one immediate rollback;
- external game backups or installed game folders;
- the exact batch progress tool and its tests.

- [ ] **Step 3: Write a cleanup manifest before deletion.**

The manifest must contain every absolute candidate path, resolved path, bytes, reparse status, reason reproducible, and retained replacement evidence. Reject any path outside the repository or any path equal to a workspace root.

- [ ] **Step 4: Delete one validated candidate at a time.**

Use `Remove-Item -LiteralPath <exact-resolved-path> -Recurse` without `-Force`. Never enumerate in one shell and pass constructed strings to another shell. Stop on the first unexpected path or access error.

- [ ] **Step 5: Re-run exact-target verification after cleanup.**

Require final self-test, WPF test, personal-state verification, compatibility activation, and a fresh build from retained sources. Compare the final executable hash with the pre-cleanup hash.

- [ ] **Step 6: Report reclaimed space.**

Calculate bytes before and after from the exact manifest. Report deleted paths, retained rollback, reclaimed bytes/GiB, final hash, and all final receipts.

## Acceptance decision table

| Gate | Required evidence | Cleanup if absent? |
|---|---|---|
| Exact target | Final self-test and WPF report match final path/hash | Forbidden |
| Installed-only Play | UI hidden plus direct-call rejection tests | Forbidden |
| Pause/resume | controlled tree proof plus all intended launcher-chain evidence | Forbidden |
| Docker current snapshot | complete authenticated namespace, no errors, exact comparison | Forbidden |
| Docker permanent/future claim | architecture and observation sufficient for the exact wording | Forbidden |
| Backup/restore | detect-only coverage and isolated round trips for every claimed path | Forbidden |
| Exit backup | successful/failed exit tests for every claimed launcher class | Forbidden |
| Metadata | exact identity, cover, sourced hours, measured size for every claimed game | Forbidden |
| Progress | authoritative numerator/denominator and native/batch agreement | Forbidden |
| Persistence | edit matrix survives refresh/restart/write failure | Forbidden |
| Installed ordering | live Recently Played/007 first receipt | Forbidden |
| Wand count | dynamic live membership tests, no constant | Forbidden |
| Compatibility | old entrypoint activates exact target without profile write | Forbidden |
| Cleanup safety | exact manifest, no reparse/out-of-root target, post-cleanup rebuild | Forbidden |

## Required final report from the new session

The final response must contain:

1. elapsed wall-clock minutes;
2. final exact executable path and SHA-256;
3. regression and WPF check counts;
4. a compact requirement matrix with `PASS`, `FAIL`, or `UNPROVEN`;
5. Docker repository/tag/backup counts and whether the snapshot was complete;
6. installed metadata and backup-coverage counts;
7. progress outputs and explicit source limitations;
8. personal-state and compatibility results;
9. whether cleanup was authorized by the gate;
10. exact bytes reclaimed if cleanup ran;
11. links to final receipts;
12. no claim that future external behavior is guaranteed unless it was actually proven.

## Copy-paste bootstrap prompt for a new session

```text
Work only in F:\study\repos\game-library-manager-native and only on the Windows executable. Do not deploy to Netlify. Read and execute docs\superpowers\plans\2026-09-24-game-library-full-acceptance-120-minute-handoff.md exactly. Use superpowers:dispatching-parallel-agents for its read-only audit lanes and superpowers:executing-plans for implementation. Start a 120-minute wall-clock timer. Preserve the dirty worktree and all personal data. Never fabricate progress or metadata, never ask me for in-game checkpoint percentages, never perform a live restore, and never launch Dragon Quest VII's Launcher.exe. The acceptance gate is fail-closed: if any requirement is FAIL or UNPROVEN, do not clean anything and do not claim completion. If every gate passes, perform only the validated cleanup in Task 8 and then rerun all exact-target checks. Report the full matrix, final hash, elapsed minutes, and reclaimed bytes.
```

## Self-review completed

- Every original requirement and later correction maps to an acceptance row and a current verification lane.
- The plan distinguishes current finite proof from universal/future guarantees.
- Cleanup is unreachable while any requirement is failed or unproven.
- Commands use exact paths and separate launches, probes, mutations, and cleanup.
- No Netlify action, billing change, real restore, checkpoint question, or fabricated value is permitted.
- The 120-minute bound applies to the execution session; it is not represented as proof that unavailable external facts can be manufactured within that time.
