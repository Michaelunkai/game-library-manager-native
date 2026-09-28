# Current Windows acceptance — 24 September 2026

**r60 is deployed and verified. Full acceptance is not achieved; the cleanup gate remains closed.**

The continuation began at **13:27:41 UTC**, with a **15:27:41 UTC** deadline. This report preserves the original requirements rather than equating finite passing tests with universal guarantees. Work stayed Windows-only. No website deployment, real game launch, live save restore, billing change, or remote repository write occurred.

Final evidence consolidation completed at **107.17 minutes**, within the requested two hours, with25referenced receipts present and all required passing receipt flags verified. The consolidated result is `evidence/criteria-r60-final-acceptance.json`; its fullAcceptance value remains false.

## Delivered executable and verification

- Target: `F:\study\repos\game-library-manager-native\native\dist\GameLibrary.exe`
- SHA-256: `5DA177CC3D53B189C575CD85ED94CF7D530923485E2021CF09826CFE0A15102C`
- Matching DLL SHA-256: `4B8B33F826DA210E7770406E2D3B0B2827D47AAB880B6F608B7337AA189649A5`
- **127 regression checks and 82 WPF checks** pass in staging and on the exact target.
- **90 progress-helper tests** pass after the final helper patch.
- **5 controlled native pause/recovery checks** pass on the exact target.
- Rollback: `evidence/pre-r60-release-20260924-174913`.
- The compatibility launcher activates the same current native process successfully.

## Repairs delivered

Installed platform identity now comes from bounded files associated with the selected executable: root/ancestor configuration, its own Unity plugins, and bounded Unreal Steamworks directories. Supported formats include Steam AppId files and section-specific TENOKE IDs. Conflicting IDs, unrelated executable trees, traversal and reparse paths are rejected. TENOKE's achievement-heavy files have a 256 KiB full-read bound; other identity files retain their 64 KiB bound. This fixes the observed Astral Ascent and Skul regression without truncating late conflicting IDs.

Steam's confirmed app identity supplies the canonical lookup title. GOG identity accepts the selected primary game task, or an exact hidden game task linked to a valid primary launcher/game executable. Custom names, ratings, categories and launch paths remain intact. Real source proofs cover Rogue Trader, Nocturnal, Fury Unleashed, Tales of the Shire, Kaiju Cracking Corporation, Campfire with Cat, Late Shift and Under the Witch.

Duration search is case-insensitive at the provider-candidate stage while retaining strict title, edition and platform guards. Multiple exact-title PC candidates can be disambiguated only by a unique independently verified Steam release year, with valid candidate years. Late Shift resolves to the 2017 game's HLTB record 44239; the chosen year is retained as provenance.

Failed provider requests now create a durable 30-minute duration cooldown, shared by helper processes using that cache root. A successful empty result does not create a cooldown. Fresh and stale valid sources remain usable. Native automatic selection respects the duration barrier while allowing independent missing artwork/platform-identity work. Old installed-game lookup failures retry once after the duration contract revision, then obey normal backoff. Hidden installed games receive background enrichment without unhiding categories.

The backup detector's false ownership of shared Windows Temp was reproduced and repaired. Backup/restore reject broad shared filesystem roots, and restore rejects broad registry roots. Isolated copied-format restores pass. Missing progress output no longer asks for a manual percentage. The deployment helper now waits for exclusive access after normal app shutdown before replacing the executable.

## Requirement-by-requirement result

| Original requirement | Current proof | Remaining limit / result |
|---|---|---|
| Play controls only for installed games | Exact-target WPF suite covers hidden controls and direct-launch rejection. | Tested paths pass; future installation changes still require scanning. |
| Pause/resume every game and children, minimum resources, flawless recovery | Controlled real process tree verifies suspension, explicit resume, stale-identity rejection, disconnect recovery and abrupt guardian death. | Every real engine/emulator/launcher was not launched. Minimum resource use and universal compatibility are unproven. |
| Permanent error-free synchronization | Independent Docker/metadata routes and storage/backoff/resilience checks pass. | Shared service continues returning **503 usage_exceeded**; the live log recorded another503 at15:00 UTC. Permanent external availability cannot be guaranteed locally. |
| Every Docker repository/tag, especially backup, current | Final live cache at15:06 UTC matches retained full cold enumeration: **1326 repositories, 3959 tag instances, 1277 backup tags**, zero differences. All cold repositories were fetched without reuse/errors. A new repository arrival was observed automatically. | Finite timestamps are recorded. This is not proof of zero-latency synchronization through every future outage. |
| Per-game ass/reass backup and restore | Detector checks:9 shared-root,27 selected-game,13 Unreal. Restore guards:9 file-root,18 registry-target. Actual-format copied file and isolated rebased registry round-trips pass. | Of136 unique launch paths: **6 detected save sources,128 no detected save data,1 rejected launcher identity,1 batch wrapper**. Universal coverage remains incomplete. |
| Immediate backup after successful exit | Packaged tests cover success/failure, queue persistence, retries and truthful no-save outcomes. | Every actual game's exit and unresolved emulator/wrapper paths were not exercised. |
| Correct artwork for every existing/future game | All **141 installed catalog rows have decoded covers**. Corrected canonical cards and GOG display pass live checks. | Decoding alone does not prove identity. Across3713 game-classified rows,2711 lack decoded artwork, often with unresolved publisher identity. |
| Correct completion hours for every game | Final15:07 snapshot: **134/141 installed durations pass native source guards;7 remain explicitly unverified estimates**. Fresh independent audit verified47 installed sources with zero HLTB-ID mismatches or changes over20%. | Kristala, Ash & Rust and Burden of Command had no main-story samples. Kaiju, Campfire, Dragon Quest VII launcher identity and Under the Witch still lack supported attribution. Provider429/403 stopped the broad audit; empty/unqueried rows are not proof of absence. |
| Accurate local GB for all games | Fresh finite traversal completed **133/133 unique roots covering141 installed rows**. Logical bytes are distinguished from downloads and published requirements. | Logical traversal is not allocated disk usage, and cannot establish an uninstalled/future build's exact size. |
| Exact batch, latest backup, percentage/hours and native agreement | Exact `HOW_MUCH_LEFT.bat` and final native assembly pass **9 agreement checks**: same latest007 backup, **98.649%**, **0.21784 hours (about13 minutes)** remaining; native rounds to0.2h. Manifest and12 payload hashes unchanged. | Equal-checkpoint weighting is not calibrated to0.001 percentage-point accuracy. Kristala's campaign denominator is unverified; available Outlaws backup lacks campaign payload. |
| Never display unknown | User-facing missing-data wording is explicit; batch does not request a manual percentage. | Wording cannot manufacture missing percentages, time or storage. Internal missing states remain truthful. |
| Permanently preserve edits, categories, tabs and played time | All10 original-profile checks pass: **1259 categories,31 tabs,7 history records**. Live007 remains first/Action/Recently Played/**16.1h**. Wand shows **49** registrations and retains its total for an empty search. Compatibility activation passes. | Finite tests cannot prove permanence against every external writer or storage failure. |
| Windows-only scope and existing entrypoint | Native executable and compatibility launcher verified; no website deployment. | Satisfied for this continuation. |
| Diagnose failures without breaking working software | Historical Windows events establish99.734% commit at the earlier crash, but attribution failed. Current app/profile recovery and bounded resource observations pass. | Two prior app/watcher processes were found absent across continuation boundaries without matching recorded application exceptions. Cause and permanent prevention are not established. |
| Maximum safe cleanup only after every criterion passes | Gate remains false. **No project cleanup;0 bytes reclaimed.** Dirty worktree, saves, rollback copies and evidence retained. | Cleanup is intentionally blocked by unmet original requirements. |

The other catalog duration categories at15:07 were483 native-source-guard accepted,519 estimates without source,1 recorded source rejected, and2710 without positive time. Seven uppercase-title UI checks pass with search/settings restored. Tales of the Shire and Oblivion enriched while their categories stayed hidden. Sonic Mania's initial automatic response was empty; a later **explicit time-only refresh through the native MetadataClient** recovered its source, verifying byte-identical personal state and unchanged other metadata. No provider result was manually assigned to the cache.

## Final evidence

All paths below are in `evidence/` unless otherwise stated.

- `criteria-release-r60-20260922.json`, `criteria-r60-target-selftest.json`, `criteria-r60-target-ui.json`, `criteria-r60-target-pause.json`.
- `criteria-r60-final-helper-tests.log`, `criteria-r60-cooldown-source-summary.md`, and the r60 red/green cooldown/revision/hidden-category receipts.
- `criteria-r60-data-coverage.json`, `criteria-r60-metadata-audit.json`, `criteria-r60-uppercase-natural-final-readback.json`, `criteria-r60-canonical-live-ui.json`.
- `criteria-r60-sonic-native-refresh.json`, `criteria-r60-remaining-canonical-sources.json`, `criteria-r59-final-lateshift-year-live.json`.
- `criteria-r60-gog-profile-ui.json`; its retained provider fixture is explicitly linked to the r59 source receipt, not represented as a fresh network request.
- `criteria-r60-final-dockerlane-consolidated.json`, `criteria-r60-final-storage-sync-dockerlane.json`, `criteria-r57c-installed-storage-measured.json`.
- `criteria-r60-current-personal-ui.json`, `criteria-r33-profile-preservation.json`, `criteria-r60-compatibility-activation.json`.
- `criteria-r60-final-exact-batch-latest.json`, `criteria-r60-final-native-batch-agreement.json`, `criteria-r59-fresh-provider-audit.json`.
- `criteria-r57-pre-progress-backups-summary.md`, `criteria-backup-coverage-r57-post-shared-root-fix.json`, and the r57 isolated restore receipts.

Deployed ASS SHA256: `B619B6073E46DD5302B31E0B538F647955DADD1B8B7B8A32EACCC15B5BAAF046`. Deployed Reass SHA256: `C8EC26B1FF42CE56FBA06A104DB2BEFBEB2517EDD968F279736FD46DAF6A641E`. Their rollback copies remain retained. Passing checks above do not waive the unresolved rows or authorize cleanup.
