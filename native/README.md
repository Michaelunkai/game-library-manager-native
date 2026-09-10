# Game Library for Windows

A native WPF application with a self-contained Windows x64 executable. It uses the authoritative project's catalog and the production Netlify admin-config endpoint. No browser engine, localhost web server, Node installation, or installed .NET runtime is required to run the EXE.

## Run

Installed executable:

`C:\Users\micha\AppData\Local\Programs\GameLibraryManager\GameLibrary.exe`

Packaged executable:

`F:\study\repos\fullstack\game-library-manager-web\native\dist\GameLibrary.exe`

Keep the adjacent `dist\tools` directory with the executable. It includes the
bundled Node.js runtime and the native Wand LevelDB bridge, so Play with Wand
does not depend on a separately installed Node.js runtime.

The Start menu entry is **Game Library**. Close exits; minimize hides to the system tray by default. A second launch restores the existing instance. The tray menu supports Open, Refresh, Open download folder, and Exit.

## Using the library

- Search across categories by game name, exact Docker tag, category, Docker URL, or installation/launcher path. The Installed view stays limited to installed games. Ctrl+K focuses search; F5 refreshes.
- Filter by category, wishlist, installed status, personal tag, or rating. Sort by name, date, time, size, category, or rating.
- Details contains descriptions, a YouTube trailer-search link, personal ratings/tags, installed markers, wishlist, executable selection, Play, installation folder, and Docker script/link copying for Docker catalog entries.
- Refresh cover & completion time uses the existing website metadata service and caches validated covers offline. It requires a matching game title and preserves an existing playtime against rough genre estimates. Refresh selected applies this workflow to a selection with progress and cancellation.
- Missing artwork and time are also filled automatically for visible games and newly discovered Docker tags. Matching first-party curated aliases are accepted; generic mismatched titles are rejected. Retry times and unavailable reasons survive restart. Unknown completion time remains unknown until usable metadata arrives.
- Select visible games with Select all or Ctrl+A. Clear or Esc resets selection. Selection is separate from the currently highlighted row.
- Install selected shows a review of games, destination, and known/unknown sizes before starting a native progress window. Errors and exit codes are recorded under `jobs` in the app's data folder.
- Export script supports PowerShell 5, BAT, and shell. BAT uses a fixed-size loader, so a large selection does not exceed Windows' command-line limit.
- Export script also offers separate PowerShell/BAT **Kill All** exports without selecting games. Saving only writes a file. Running it lists every container in the active Docker target, including unrelated containers, and requires `DELETE ALL` before force-stopping and removing those containers and their writable layers. Images, volumes, and downloaded game folders are preserved. The existing stop-selected script remains available.
- Download folders include a stable tag hash so `AeternaNoctis` and `aeternanoctis` cannot collide on case-insensitive Windows filesystems. Existing folders with the original game name are still recognized by Scan folder.
- Scan folder searches game directories to depth six, skips reparse points and support/runtime directories, avoids installers, and does not silently choose between ambiguous executables. Unknown games become local library records with stable folder identities. Choose an executable in Details when needed.
- A successful download automatically scans its selected destination and saves its installed marker and launcher when an actual game executable is present. Support-only payloads do not count as installed games.
- Admin sign-in uses the same password as the website, without saving that password. Shared category edits and category creation/rename/removal/visibility require sign-in.
- Settings contains theme, cover size, time/category visibility, repository, destination, tray behavior, complete backup export, and native/website settings import.

## Synchronization and persistence

Production endpoint: `https://game-library-michaelunkai.netlify.app/api/admin-config`.

Shared categories, hidden tabs, and category definitions synchronize with that endpoint. Personal wishlist, ratings, tags, installed markers, launch paths, and preferences are local, matching the website's existing browser-local preference boundary. Export/import transfers settings and selection to or from the website; the native complete backup also includes personal state.

The native app polls shared configuration every 15 seconds without rebuilding an unchanged list, and checks the catalog once a minute. Refresh catalog downloads current data files and the first-party Docker tag feed. If that feed is degraded, the native app paginates Docker Hub directly. A complete snapshot less than 15 minutes old can be reused only after a fresh first-page identity/size/date and total-count comparison for the same repository. Existing Docker Desktop sign-in can supply the authentication required beyond anonymous pagination limits; credentials are used only in memory for the relevant Docker hosts. Completeness checks reject partial snapshots, and an older fallback cannot replace a complete snapshot. The recorded live check retrieved 1,260 tags across 13 pages.

User data is stored in `%LOCALAPPDATA%\GameLibraryManager`. Writes use a flushed temporary file and atomic replacement with a previous-version backup. Corrupt user state is preserved before recovery. Bundled assets have a content-addressed revision; upgrading the EXE does not reuse an outdated bundled catalog.

Backup imports wait for active synchronization before replacing state, then recheck queued edits. Closing the import dialog while it waits cancels the import. The prior library is backed up before a successful import.

Offline admin edits are durable per-field changes. Before publishing, the app fetches current state, detects same-field conflicts, merges unrelated website changes, and verifies the result with a fresh read. Interrupted acknowledgments are reconciled before retrying. Conflicts remain queued for explicit resolution in Sync details.

`--offline` blocks shared synchronization at both the action and HTTP transport boundaries. Admin edits and tray refresh cannot send queued changes in that mode. Deliberately requested manual metadata refresh remains a separate online action; automatic metadata requests are disabled offline.

**The current production server does not advertise atomic conditional writes.** Preflight checks and read-back reduce stale-state risk but cannot eliminate a simultaneous-write race. The client is ready to send `If-Match` when the backend advertises conditional-write support. The complete original goal remains open until that server guarantee and browser UI proof are addressed.

The same-site backend and website changes are now prepared in the maintained source. They pin Netlify Blobs 10.7.13, protect conditional writes against false acknowledgments, and add a durable browser queue. They have not been deployed. See [backend/README.md](backend/README.md) for the exact site, local verification, private rollback, and secure deployment prerequisite.

## Build and verify

```powershell
.\build.ps1
.\dist\GameLibrary.exe --self-test .\evidence\self-test.json
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\verify-native.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\verify-native.ps1 -KillScriptProof
.\dist\GameLibrary.exe --data-dir .\evidence\offline-ui --offline --ui-test .\evidence\offline-ui.json
.\dist\GameLibrary.exe --data-dir .\evidence\online-ui --ui-test .\evidence\online-ui.json
.\install.ps1
```

The Kill All export proof saves and parses both formats without running them. `verify-killall-scripts.ps1 -ScriptPath <exported.ps1>` separately tests the unchanged PowerShell payload with a strict fake Docker command in isolated PowerShell 5 child processes; it never invokes real Docker.

Build uses a stable .NET 10 SDK. `-DotnetPath` selects an explicit SDK host. This machine's original stable SDK directories were incomplete, so a separate Microsoft SDK 10.0.400 was downloaded to `%USERPROFILE%\.codex\toolchains\gamelibrary-dotnet10` and verified against Microsoft's SHA512 manifest. Global SDK directories and PATH were not changed.

The build redirects temporary files, NuGet packages, and the .NET CLI home to a
`study\temp\glm-native-build` folder on the drive containing this native tree;
the publish step refuses to leave a stale or incomplete Wand tool tree in the
distribution.

`--self-test` uses temporary fixture data and simulated HTTP failures; it does not mutate production. `--ui-test` runs the packaged WPF UI against an explicitly selected data directory and exits. The external PowerShell verifier exercises actual Windows UI Automation controls and process lifecycle.

The optional `-MetadataProof` verifier exercises live cover/time refresh. `-DockerProof -DockerTag peppergrinder` performs a real download into an isolated evidence folder using the existing Docker VMM engine and checks automatic installed/launcher persistence before manual scanning. `-PlayProof` uses its receipt, clears the fixture launcher before scanning, chooses the actual EXE through the Windows dialog, and launches it. `-LocalGameProof` copies that fixture into an unknown folder and exercises local discovery, launch and restart. The recorded game reached a responding title-menu window; this does not establish gameplay or all-game compatibility.

`-AdminProof`, `-TrayProof`, and `-ScriptProof` cover category removal confirmation, the actual tray menu through its registered callback, and all script-format Save dialogs. These use isolated app data; admin and tray shared refresh are guarded offline.

`--live-sync-proof` is a separate opt-in diagnostic requiring `GLM_ENABLE_LIVE_SYNC_PROOF=1` and `GLM_PROOF_ADMIN_TOKEN`. It temporarily changes one existing game's category through the native and independent website API clients, then restores the original value. It writes a rollback receipt before mutation and refuses to run again while an unresolved receipt exists. Never run this as part of normal startup or routine offline tests.

## Remove or roll back

Run `%LOCALAPPDATA%\Programs\GameLibraryManager\uninstall.ps1`. It validates the install receipt and executable checksum, removes only its managed EXE and matching shortcuts, and leaves all user data, game files, backups, and unrelated files intact. There is no recursive deletion.

Reinstalling backs up an existing managed executable with a timestamp. For rollback, close the app and restore the desired backup EXE; keep the current executable as another backup. The uninstaller intentionally refuses to delete an EXE whose checksum no longer matches its receipt.

See [ACCEPTANCE.md](ACCEPTANCE.md) for observed test results and remaining proof gaps. This build is not code-signed.
