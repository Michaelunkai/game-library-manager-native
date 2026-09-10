# Native acceptance record — 2026-09-08

## Recovered scope and source

Original task `01a07d8c-023c-7a02-b551-b4ec9ea37f75` ended after inventory. The other linked task, `01a07cb7-72dc-73a1-ae5c-910e17fa0e38`, is the separate music project. The original game-library goal requires native Windows operation, complete workflow parity, actual bidirectional browser synchronization, recovery/concurrency checks, and preservation of the existing site and backend.

Authoritative checkout: `F:\study\repos\fullstack\game-library-manager-web`. Native implementation is under `native/`. Nine maintained website/function/dependency/configuration paths were also reconciled with standalone production repository `Michaelunkai/game-library-manager-web`, commit `2fc2461955174d90a4542f707b878ef9f0b6cc26`, to prepare conditional synchronization. Catalog data and cover assets were not changed. This is prepared local source, not a deployment. The checkout's enclosing Git remote is a different monorepo; its push is not an established Netlify deployment route.

## Latest local revalidation — 2026-09-08

The current installed package is `0B3BE3BED0B258CEF57BAD9C2FCA8913CFEF87D8FFB89D6662D4EE34FD063DD5` at `C:\Users\micha\AppData\Local\Programs\GameLibraryManager\GameLibrary.exe`; the responding foreground process is PID `45356`. This build adds a Wand protocol handoff that waits for the actual game executable before tracking playtime, preserves searches entered while the bundled catalog is still loading, and applies dark themed ComboBox controls to the filtering surface. `self-test-theme.json` passed 43 checks, `external-ui.json` passed 28 checks, `scripts-native.json` passed 13 checks, and `kill-script-native.json` passed 18 checks for this package. The installed state still records `E:\games`, `Recently Added`, BAT, Native Linux, and zero pending shared edits.

The live site was rechecked after this revalidation: `/app.js` still lacks `ConditionalAdminSync` and the 15-second sync interval, so the reviewed conditional-sync source remains undeployed. Netlify authorization ticket `3f8f9d3a8e0daf03bb9b5dd15e284c83` remains pending.

## Current executable revalidation — 2026-09-08

The rebuilt dist and installed executable now match at SHA256 `197DDC1D0CC5B94E81BFDDCA57C5CFCE740A92D451376305529D7F853EB1398E`, size `400360527`. The installed copy at `C:\Users\micha\AppData\Local\Programs\GameLibraryManager\GameLibrary.exe` is open and responding as PID `19432`. The current build passed 51 self-tests, 31 external native UI checks, and 5 Wand checks; the Wand receipt includes a fresh `wizardwithagun.log` IPC and hook record. Earlier receipts retain the hash of the build they exercised.

## Current feature revalidation — 2026-09-08 18:23Z

The native source was rebuilt from the current checkout and installed at `C:\Users\micha\AppData\Local\Programs\GameLibraryManager\GameLibrary.exe`. Dist and installed copies match SHA256 `DC945C13378FB00E13DA4D35142EF6AE5E95FB4966864E8407787A19CE41AC44` (400,376,911 bytes); the installed window is responding as PID `49832` with no child game process. No game was launched during this revalidation, and the earlier Wand game launch receipt was not repeated.

The current build passed 54/54 core self-tests in `evidence/self-test-feature-current.json` and 28/28 offline WPF UI checks in `evidence/offline-ui-current.json`. These current checks cover exact per-game completion markers and freshness, immediate in-process completion notification before the batch exits, PowerShell/BAT/WSL2 shell marker isolation, category ordering boundary logic, and the existing Play/Wand/filter/install/category UI contracts. The current source also retains the fail-closed Wand handoff and exact executable resolution changes verified by the earlier Wand receipts.

The remaining external blockers below are unchanged: universal third-party Wand injection, all-game/network conditions, same-site Netlify deployment, approved Chrome UI parity, and unsigned/other-PC/reboot/long-play coverage remain unproven. This local revalidation updates the installed native build and its evidence without claiming those external gates.

## Recorded proof

| Criterion | Observed result | Evidence under `evidence/` |
|---|---|---|
| Self-contained native WPF EXE, bundled 1,179 games and 2,028 covers | Passed, no installed .NET runtime or browser engine required | `self-test-final.json`, packaged `dist/GameLibrary.exe` |
| Storage/recovery, exact-case tags, queues/conflicts/failure/reconnect, script safety, local-game discovery, scanner exclusions, metadata attribution/cache, rejecting offline transport, import during delayed sync and cancellation | 39 checks passed on the final installed build | `self-test-final.json` |
| Packaged live WPF controls, global search/privacy, installed-only filter, statistics, sorts, selection, themes, tray/hide/restore, process status/error | 23 checks passed | `live-ui.json` |
| Offline native admin sign-in with existing website password; category create, rename, visibility and durable queue; rejecting offline transport; other UI checks | 27 checks passed | `offline-ui.json` |
| External Windows UI Automation: search, details, settings, full backup export/import, imported navigation, website-format import, selection restoration, minimize, second launch, close/restart | 25 checks passed | `external-ui.json` |
| Real category create/move/removal confirmation and queued cleanup | 12 checks passed; offline transport guarded | `admin-external-native.json` |
| Actual native tray menu Open/Refresh/Exit via registered icon callback | 8 checks passed; no physical tray click, refresh guarded offline | `tray-external-native.json` |
| PowerShell/BAT/shell install and PowerShell stop Save dialogs | 13 checks passed; saved payload/syntax verified, scripts not executed by this lane | `scripts-native.json` |
| Windows website Kill All parity: independent PS1/BAT exports, scope review, cancellation, zero-selection saving and payload integrity | 18 checks passed; scripts saved only, no container deletion | `kill-script-native.json` |
| Actual exported Kill All PowerShell logic under Windows PowerShell 5 | 10 mock-Docker scenarios passed: cancellation, empty/error/malformed listing, pinned target, environment priority and removal failures; unchanged payload, no real Docker execution | `killall-behavior.json` |
| Complete fresh Docker Hub snapshot | 1,260 tags, 13 pages; authenticated pagination through existing Docker Desktop credential helper | `final-live-catalog/cache/docker-tags.json`, `live-ui.json` |
| Automatic catalog interval in untouched normal native window | 9 checks passed; second check after 59.2 seconds, fresh first-page check and original full snapshot retained | `automatic-catalog-native.json` |
| Real native Docker download/extraction via existing VMM and automatic installed/launcher persistence | Pepper Grinder passed 6 checks with 54 files; no manual scan or picker before the automatic-completion assertions. Earlier Inmot passed 4 process checks but its support-only payload was not a playable game | `docker-native-peppergrinder.json`, `docker-native-inmot.json` |
| Unknown local-game folder, native scan/picker, persistent record, proper local Details, actual Play and restart | 13 checks passed using a copy of the real Pepper Grinder fixture | `local-game-native.json` |
| Real native scan, Windows EXE picker, Play | Five checks passed after clearing the saved fixture launcher; real Pepper Grinder responding window | `play-native.json` |
| Real native metadata control and offline cache | Three checks passed; cover cached and existing playtime preserved against rough genre estimate | `metadata-native.json` |
| Automatic artwork, matched title, time and category for a Docker-only game | Saviorless enriched without manual refresh in 8.78 seconds; actual offline restart retained the title | `automatic-metadata.json` |
| Native to production API and independent website API to native, restart, restoration | Seven checks passed, original category restored; browser UI not exercised | `live-sync.json`, `live-sync-state/rollback-restored-*.json` |
| Candidate backend conditional writes and error handling | Ten fixture checks passed | `backend-cas.json` |
| Actual pinned Netlify Blobs 10.7.13 SDK transport | Six wire/error checks passed; not live-provider proof | `backend-sdk.json` |
| Browser queue, conflict, interrupted acknowledgement and legacy-host behavior | Ten logic checks passed; not browser UI proof | `browser-sync-logic.json` |
| Existing website regression suite after source reconciliation | 121 tests passed, zero failed, in isolated fixture | `web-regression/test.log` |
| Website/function syntax and build | Ten JavaScript syntax checks and npm build passed; scoped git diff check passed | Recorded task terminal checks |
| Source preservation/rollback | All nine candidate hashes verified; private rollback rehearsal passed without modifying current source | `maintained-source.json`, `source-rollback.json` |
| Concrete same-site deployment source packaging | 2,048 file hashes verified, including 2,028 images and explicitly included pinned lockfile; zero extra files. No deployment | `deployment-candidate.json`, `deployment-candidate-verification.json` |
| Actual local Netlify function bundling | 15 checks passed with the installed official bundler; four ZIPs explicitly target `nodejs24.x`, include exact fallback data, and retain the actual SDK 10.7.13/runtime/fetch guard | `deployment-bundle.json` |
| Installed session on preceding D40DC418 build | Responding after five automatic catalog checks over 315 seconds; no recorded UI/startup/sync failures, zero pending shared edits | `installed-session-health.json` |
| Install/uninstall ownership and unrelated-file preservation | Isolated install rollback passed | `install-rollback.json` |
| Latest installed EXE, user-state preservation, second launch, clean close/reopen | Passed; installed app left running | `installed-final.json` |

Installed executable: `C:\Users\micha\AppData\Local\Programs\GameLibraryManager\GameLibrary.exe`.

SHA256: `4CA31728F68753CB3D6EC17B774792BAC5F55059DD7F34B6CE7E2A417A146FD5`.

This exact build passed 39 core, 27 offline UI, 23 live UI, 25 external UI, 13 existing script-export and 18 Kill All export checks. Ten additional mock-Docker scenarios verified the actual saved PowerShell payload, bringing these current checks to 155. Its installed copy passed minimize/second-launch restore, clean close, and reopen; saved settings, wishlist, installed markers, ratings, tags, launch paths and pending changes matched the preserved pre-upgrade state after restart. The installed app was left running as PID 37504. See `installed-final.png` for the actual installed window.

The automatic catalog interval receipt was recorded on preceding build `F581C643…`, whose polling code is unchanged in the final build. Admin/tray, Docker completion/local-game and automatic enrichment receipts were recorded on `409D4A815…`. Subsequent metadata/import/lowest-rating fixes were covered by the final core and UI suites; those historical game downloads were not repeated merely to change their build label.

The final local parity audit found that the website exported Windows Kill All scripts while native exposed only stop-selected. Both Windows exports are now present. Saving opens a scope review and writes a file only. When explicitly run, the script pins the effective Docker context/host, snapshots full container IDs, shows unrelated-container and writable-layer consequences, and requires the exact `DELETE ALL` response. It excludes containers created after the listing and preserves images, volumes and downloaded game folders. The existing stop-selected action remains separate.

The prepared Netlify build target was updated from Node 18 to Node 24 on 2026-09-08 after reviewing the current [Netlify runtime rules](https://docs.netlify.com/build/functions/configuration/?fn-language=js). This matches the installed Node 24.18.0 used for local verification. The previous candidate and its receipt were preserved privately, the nine-file review hash was updated, and the original source rollback rehearsal passed again. This is a local configuration change. Any hosted `AWS_LAMBDA_JS_RUNTIME` override remains to be inspected through the authenticated site before deployment.

An old installed build left an orphan process after logging clean shutdown and losing all application windows. It was closed during the upgrade after confirming its exact path and lack of application windows. The updated installed build independently passed second-launch and clean-close checks. This is not a claim that the orphan's underlying cause was identified.

The game screenshot `peppergrinder-window.png` shows its rendered Start/Exit title menu, but also a separate Windows dotnet Application Error modal. The corresponding global SDK System.CommandLine failure is being investigated by the coordinating task; its cause is not attributed to this game. All native builds here use the isolated verified SDK. No physical gameplay, global repair, or all-games guarantee is established.

## Remaining requirements and precise blockers

This goal is **not complete**.

1. **Same-site deployment and live atomic concurrency:** the current production endpoint does not advertise conditional writes. The local Netlify session returns HTTP 401 for read-only site access. The nine-file source change and private rollback are ready for review. Secure CLI reauthorization for the existing Netlify account/site is required before recording its previous deploy, creating a same-site draft, publishing, and proving real provider concurrency. See `backend/README.md`. No deployment was attempted, and no alternate host was substituted.
2. **Approved visible Chrome verification:** browser execution still fails before JavaScript with `failed to write kernel assets ... os error 3`, including one fresh reconciliation at 00:13 UTC after a repair notice. The coordinator subsequently confirmed that recurring repair notices cover static checks and do not establish runtime recovery. No additional bootstrap was attempted at 00:44 UTC; see `browser-connection-deferred-20260908004431.json`. No Chrome identity discovery or interaction occurred. Actual website UI parity, native-to-browser/browser-to-native synchronization, live conflict UI, reload and reconnect proof remain outstanding separately from Netlify authentication.
3. **Remaining browser dispatch coverage:** the native trailer-search action exists, but its external browser launch has not been exercised while approved Chrome control is unavailable. Local-game and Docker scan/Play evidence covers the isolated real Pepper Grinder fixtures, not every existing folder or game. Automatic enrichment is verified for Saviorless; unavailable metadata retains fallback cover initials and a persisted retry reason.
4. The EXE remains unsigned. Other PCs, reboot, long gameplay, every game, and every possible network condition are outside the evidence recorded here.

An older offline admin fixture unexpectedly published the unique unused category `native_removal_proof`. It was reconciled with fresh reads, removed using a tabs-only update, and verified restored with unrelated configuration preserved: `admin-offline-incident-2026-09-07T23-47-15-865Z/receipt.json`. Both the action-level offline guard and a rejecting HTTP transport are now present and tested. The earlier opt-in live synchronization category mutation was also restored. No production cleanup remains from these fixtures. Do not repeat a mutation while an unresolved rollback receipt exists. The remaining criteria are required and have not been waived; repeated external blockers may block the tracked goal but do not complete it.

## Native-only completion amendment — 2026-09-09

This amendment is the authoritative result for the native WPF EXE scope requested by the user. It does not claim website deployment, browser parity, or live website synchronization.

The rebuilt and installed executable is `C:\Users\micha\AppData\Local\Programs\GameLibraryManager\GameLibrary.exe`, size `400381007` bytes, with matching dist and installed SHA256 `CC5A4F83CD8F375156AD076F8840124EE733680B11503267C81BFA391D1CE243`. `build.ps1` completed successfully and `install.ps1` reported checksum verification.

The latest installed build passed `self-test-docker-inspect-catch-20260909.json` with 56/56 checks and zero failures, and the fresh native gates passed: offline WPF UI 28/28; automatic catalog refresh 9/9 with 1,260 Docker Hub tags and a 16.05-second observed interval; real Docker VMM extraction 7/7 with 55 files and automatic Pepper Grinder launcher persistence; local-only scan/Play/restart 15/15; downloaded-game Play 5/5; visible default-terminal handoff 6/6; script export 13/13; Kill All review/export 18/18; administrator confirmation 12/12; tray behavior 8/8; metadata/cache 3/3; and native Wand 5/5.

The native Wand proof now includes the exact Wizard with a Gun executable, a visible current Wand client, and fresh overlay evidence containing IPC and hook markers. The user protocol handlers were reconciled after an isolated version comparison and are restored to `C:\Users\micha\AppData\Local\Wand\app-12.53.0\Wand.exe "%1"`. No Wand, game, or Docker container processes remain after verification.

The durable source fixes are in `DockerScripts.cs`, `JobWindow.cs`, and `SelfTests.cs`: POSIX completion markers expand the destination variable safely; PowerShell 5 avoids quoted Go-template marshalling by parsing JSON labels; generated containers carry exact native-owner and game-ID labels; generated and direct cancellation paths inspect those labels and refuse mismatches; and 56 self-tests cover the new behavior. The recent BAT/PowerShell quoting repair remains intact.

Within the native EXE scope, all previously actionable acceptance gates are complete. The historical website/browser items below remain a separate, intentionally unperformed scope and must not be used to downgrade this native result.
