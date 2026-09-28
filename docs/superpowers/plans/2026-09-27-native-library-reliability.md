# Native Game Library reliability implementation and acceptance runbook

The user-approved plan in this task is the release contract. This file records the executable sequence, evidence boundaries, and current implementation map. The plan requires a passed result for every applicable current game; a build or fixture result alone cannot close live acceptance.

## Immutable scope

- Keep the existing .NET 10 WPF native application, F-drive profile, compatible launcher, Docker/WSL installation routes, Wand, AHK, ASS, Reass, and gameprogress helper.
- Preserve the dirty working tree, every untracked source file, installed games, saves, backup history, launch paths, personal edits, running games, and unrelated services. Never reset or clean the checkout.
- Keep normal-speed concurrent installations and gameplay. Serialize only work that touches the same installation or save target.
- Never create missing metadata, trainer attachment, or save progress evidence by guessing. Mark gaps Blocked or Not tested.
- Use Windows PowerShell 5.1 for launch and verification scripts. Keep file mutation, process launch, service control, and verification in separate invocations.

## Paths

- Repository: `F:\study\repos\game-library-manager-native`
- Native source: `F:\study\repos\game-library-manager-native\native`
- Deployed native executable: `F:\study\repos\game-library-manager-native\native\dist\GameLibrary.exe`
- Compatibility launcher: `F:\study\repos\game-library-manager-native\dist\GameLibrary.exe`
- Live profile: `F:\study\repos\game-library-manager-native\data`
- Web reference: `F:\study\repos\game-library-manager-web`
- AHK script: `F:\study\Platforms\windows\autohotkey\mymainahk\current.ahk`
- ASS: `F:\study\projects\SystemMonitor\PSProcLasso\Windows\Applications\Gaming\SaveData\AssLatestGameBackup`
- Reass: `F:\study\Platforms\windows\functions\Reass.ps1`
- Backups: `F:\backup\gamesaves`
- Gameprogress: `F:\study\projects\games\tools\gameprogress`
- SDK: `C:\Users\Admin\.codex\toolchains\gamelibrary-dotnet10\dotnet.exe`

## Evidence and state model

The evidence root for this run is `evidence\reliability-20260927T134422Z`. `baseline` contains Git, process, executable, profile, category, AHK, and save inventory evidence. `rollback` contains pre-edit source and binary snapshots. `fixtures`, `stage`, `target`, `live`, and `coverage` separate isolated tests, package tests, deployed tests, live scenarios, and current-game coverage. The acceptance ledger must record test name, timestamp, executable hash, path, result, and blocker. Use Passed, Failed, Blocked, and Not tested precisely. Keep code, deployment, current-library coverage, and complete requested acceptance as separate outcomes.

## Ordered implementation gates

1. **Window placement.** Capture the foreground window monitor before WPF activation, fall back to pointer then primary, preserve explicit flags, apply DPI-aware placement once, debounce size/position persistence, and never move an accessible restored window. Prove with real multi-monitor launch, movement, tray, second instance, mixed DPI, and disconnect checks.
2. **Game identity.** Retain source IDs. Group only evidence-confirmed versions by verified mapping, product ID, exact installation, digest, or corroborated aliases. Select the newest published version by authoritative push time while preserving the chosen playable installation and every personal record. Prove duplicate-card behavior across refresh, restart, import/export, and mixed Docker/local/Wand sources.
3. **Categories.** Merge local, shared, bundled, and assigned IDs without resurrecting deletion tombstones. Expose hidden and empty definitions. Persist independent `HideTab` and `HideGamesFromAll` flags; migrate old hidden entries to both true. Prove offline edits, all switch combinations, restart, import/export, and every current definition.
4. **2D classification.** Classify canonical identities with identity-matched publisher/store evidence. Include 2.5D plane-based gameplay. Preserve Finished, meh, HyperV, and non-games. Require an undo receipt and respect newer personal edits. Review ambiguous games; never guess.
5. **AHK control.** Preserve Ctrl+H and Alt+H. Use same-user target-specific Pause, Resume, Status requests bound to PID, creation stamp, window, and session. Card actions affect only the selected game; Alt+H still resumes all. Prove cross-control, repeated/rapid requests, PID reuse, full-screen recovery, played-time exclusion, and runtime reload without interrupting a paused game.
6. **Wand.** Keep registrations and valid bootstrap contexts. Track resolving, game start, attachment, connection, failure, and late disconnect separately. Require fresh trainer evidence for the exact game/process. Reconcile an already-running game and limit attempts. Prove fixtures and every current valid registration on real game windows without interrupting a user-owned session.
7. **Installation jobs.** Persist operation ID, canonical/source ID, pinned digest, target, stage, owned processes, checkpoints, activity, failures, and marker. For supported Docker targets, run a worker before WPF; preserve legacy WSL route until it has equivalent safe ownership. Pull, extract to staging, verify, wait for use conflict, promote, verify, then mark complete. Bound inactive subprocesses, report stderr and layer progress, and test crash/restart/hang/duplicate/concurrency cases.
8. **Job control.** Pause download by ending only its pull client; resume the same digest. Pause extraction at verified file checkpoints. Stop only owned work and preserve reconciliation data. Never publish a partial installation marker.
9. **Operation coordination.** Detect external and library-launched processes by exact executable and creation identity. Allow unrelated work; defer only same-installation promotion or same-game restore. Recheck immediately before mutation; queue one backup after final game exit.
10. **Saves.** ASS and Reass return atomic structured results naming the selected executable, outcome, manifest, integrity, bytes, and rollback. Accept success only with exit code, matching structured result, and physical verified receipt. Verify restore before target mutation and preserve pre-restore rollback. Test fixtures, corruption, wrong game, no-save, and rollback without overwriting arbitrary live saves.
11. **Metadata.** Store independent artwork, duration, and size evidence with source identity, scope, timestamp, validation, and retry. Enrich hidden and new games in bounded background work. Keep installed logical GB, download GB, and published requirement distinct. Do not count placeholders or loosely matched titles as correct.
12. **Progress.** Schema v3 separates main story from overall completion. Decode stable, identity-matched save snapshots; document denominators and uncertainty. Legacy v2 can support only main-story evidence. Unknown is unavailable, never zero or copied between scopes. Track every played current game in coverage.
13. **Website parity.** Inventory maintained website controls and reachable deployment. Implement maximum download size, random visible selection, select category, and safe selected-command copying. Exercise all existing native equivalents and record source versus live verification separately.

## Build and release sequence

1. Build and run isolated tests against source before packaging. Preserve all failures. Do not update expectations merely to turn a failure green.
2. Publish to a new, absent `stage\package` directory with `native\build.ps1 -DotnetPath <SDK> -DistributionRoot <stage package> -BuildOutputRoot <stage build output>`. Never invoke its default destructive distribution path.
3. Hash every staged file and confirm the Node runtime, LevelDB addon, Wand helpers, catalog assets, executable, and companion DLLs. Test from the staged location with `native\verify-reliability.ps1 -ExePath <staged exe> -EvidenceRoot <root> -Phase Stage`.
4. Run real-game and real-Docker scenarios separately. Preserve user-owned sessions. A fixture pass does not become a real-game pass.
5. Record running games/jobs/paused identities, let workers reach safe points, gracefully close only the native app, and snapshot the full old bundle. Deploy the complete verified stage package, verify deployed hashes, retain the compatibility route, and start through the real launch path.
6. Run `-Phase Target` against the deployed executable. Compare profile/personal data against the baseline with only intentional migrations and legitimate play/metadata/job updates allowed. Verify UI, AHK, Wand, saves, concurrent installation, and persistence on the deployed binary.
7. If target verification fails, preserve the post-update profile, stop only the new app after reconciling jobs, restore the old complete bundle, reverse only defective migration fields from their receipts, and verify rollback hashes. Never overwrite new live saves or installations with a stale profile snapshot.
8. Observe idle refresh, metadata, categories, play, install, pause, exit backup, and restart for a bounded period. Record responsiveness, memory, handles, workers, logs, and exceptions.

## Final report gate

Report deployed path and SHA-256, release evidence and verified rollback locations, implemented changes, actual deployed test results, per-game metadata/progress/Wand/backup coverage, and every Failed/Blocked/Not tested criterion. Complete requested acceptance passes only if all applicable real-current-game and deployment criteria have evidence. Do not translate an isolated suite count into universal coverage.
