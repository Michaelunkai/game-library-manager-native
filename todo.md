# Windows Game Library acceptance checklist

## Screenshot: shared-sync HTTP 503 and incomplete Docker namespace

☑ Capture the failing shared-sync request and each failed Docker repository/page from current logs and Sync details; preserve the exact causes separately. Shared endpoint:503usage_exceeded. Codex's redirected credential context is empty; the user's native Windows login script confirms valid saved credentials. Public namespace1324repositories; anonymous pagination beyond1000returns403. Evidence:criteria-sync-root-causes-20260922.json.
☑ Diagnose why the Windows client still depends on an unavailable shared service and identify a supported working synchronization route without deploying to Netlify. Shared categories use the quota-exhausted endpoint; direct Docker sync can reuse Invoke-DockerHubLogin.ps1's existing current-user encrypted credential. Native sign-in is already present.
☐ Repair shared synchronization or its Windows fallback while preserving every local edit and accurately reporting whether remote data is current.
☐ Diagnose Docker namespace failures separately: authentication, pagination, throttling, timeouts, malformed responses, and individual unavailable repositories.
☑ Repair namespace retrieval so one failed repository cannot block successful repositories; resume failed work without losing previously verified games. r38b exposes independently verified repository updates during partial scans; restart/repeated-outage/recovery and priority-cache precedence tests pass. Exact target115regression/64WPF checks; live1325repository scan complete; personal data preserved.
☐ Verify retries recover automatically after service/network restoration, respect retry limits, and survive application restart.
☐ Test unavailable and recovering sources, partial pagination, expired authentication, and repeated outages without data loss or false success messages.
☐ Verify the repaired synchronization in the exact Windows executable and compare the resulting namespace with authoritative Docker data.
☑ Add regression coverage and actionable diagnostics for these exact failures; do not accept merely hiding the error messages as a repair. r37 staged regression suite:114 passed; WPF suite passed. Shared quota and Docker authentication errors remain distinct.
☑ Verify encrypted-credential integration from the same redirected context, including authenticated pagination beyond 1000 results. Both formerly blocked pages return200; authenticated totals1325repositories and1277backup tags. Evidence:criteria-r37-authenticated-pagination.json.
☑ Build and test the credential integration in the exact Windows executable, preserving personal data. r37 exact target:114regression/64WPF checks;10profile-preservation assertions; live Installed view007first/Action/Played12.1h/Recently Played survives refresh. Namespace1325repositories and1277backup tags matches independent authenticated pagination.

## Personal-state repair verification

☑ Trace and recover existing playtime, last-played dates, and personal edits from authoritative profiles/backups without resetting current changes.
☑ Make Installed default visibly sort by most recently played, including after restart and with other filters enabled.
☑ Restore 007 First Light to Action and verify category changes survive refresh, scans, synchronization, and restart.
☑ Fix the cause of lost/reversed personal edits and test preservation across profile loading and repeated releases.
☑ Verify restored playtime and ordering in the exact Windows executable; keep unrecoverable data explicitly unresolved.

Target: F:\study\repos\game-library-manager-native\native\dist\GameLibrary.exe
Scope: Windows only. No Netlify deployment. Cleanup requires every acceptance item below to be verified.
Evidence register: docs/criteria-audit-20260922.md. Prior scoped tests do not prove universal behavior.

## Current metadata identity fix

☑ Re-read the original attached requirements.
☑ Reproduce cross-game metadata collisions against the existing assembly.
☑ Restrict title matching to equivalent titles and explicit curated aliases.
☑ Add regression cases for sequels, spinoffs, numeric collisions and punctuation.
☑ Build the r17 Windows package.
☑ Run the staged package regression checks.
☑ Run the staged package WPF UI checks.
☑ Preserve the current executable and replace it with the verified package.
☑ Run regression checks against the exact target executable.
☑ Run WPF UI checks against the exact target executable.
☑ Reopen the normal app and verify that it responds.
☑ Record the release hash and verification evidence.

## Wand included count correction

☑ Reproduce the fixed 39 count and compare it with the current Wand library.
☑ Identify why the filter or displayed count is stale or incorrect.
☑ Fix the Wand included filter and count using current Wand registrations.
☑ Verify the count changes correctly when Wand library membership changes.
☑ Run the corrected package regression suite.
☑ Compare the corrected package's Wand audit with the live database.
☑ Deploy the corrected executable and its live-reader helper.
☑ Verify the exact target executable after deployment.
☑ Verify the normal app reports the current Wand registration count.
☑ Record the Wand count fix and its evidence.

## False staging-folder installation

☑ Inspect scanner handling of app-owned staging directories.
☑ Exclude app-owned staging folders and remove their false installed markers.
☑ Verify partial downloads cannot appear as installed games.
☑ Run the scanner package UI checks.
☑ Deploy the verified scanner executable.
☑ Verify the exact target scanner executable.
☑ Verify the real saved staging registration is corrected without deleting files.
☑ Record scanner and metadata-audit evidence.

## Backup outcome handling

☑ Inspect handling of confirmed no-save outcomes and automatic retries.
☑ Correct no-save outcome handling without claiming a backup exists.
☑ Verify successful, no-save and failed helper outcomes independently.
☑ Deploy the backup-outcome correction.
☑ Verify the exact target backup-outcome build.
☑ Reopen the app and record backup coverage and release evidence.

## Live progress audit evidence

☑ Record latest-backup, hash, decoder and native-agreement evidence.

## Remaining full acceptance work

☐ Audit pause/resume coverage against the actual installed games and launcher chains.
☐ Resolve any pause/resume failures found by that audit.
☐ Verify resource behavior while games are paused and recovery when the library exits.
☑ Audit namespace/tag synchronization against current Docker Hub state. Exact r48 cold scan fetched all1325repositories with0reused caches,3958repository/tag entries and1277backup tags. Live namespace matches every exact identity, size, update time and digest; priority cache matches its stored projection. No remote writes. Evidence:criteria-r48-namespace-cold.json and criteria-r48-namespace-comparison.json. This finite check does not prove zero-latency synchronization through future outages.
☐ Verify additions, updates and retry recovery without losing catalog data or edits.
☑ Audit artwork identity and coverage for every catalog game. (Coverage audited; game identity remains unverified where source evidence is absent.)
☐ Correct missing or mismatched artwork using attributable game identity.
☑ Audit completion-time sources and coverage for every catalog game. Full r44provenance audit:3712game-classified rows;164recorded sources,859positive catalog estimates without recorded sources,2689without positive time. Installed141:99recorded sources,42catalog estimates;83recorded-source rows lack canonical provider title. Source presence alone does not prove identity/accuracy.
☑ Enable automatic revalidation of legacy duration records lacking canonical provider-title evidence and distinguish unverified catalog estimates in the native display without inventing replacement hours. r45 exact121regression/67WPF pass;17legacy records gained canonical titles automatically in live data. Remaining source coverage is still open; no replacement hours invented.
☐ Correct unsupported or inaccurate completion-time data.
☐ Verify local installation sizes and unresolved folder mappings against actual files.
☑ Audit backup helper selection and save coverage for installed games.
☑ Verify restore round trips against isolated copies of real supported save formats. Available007/Kristala/Outlaws payloads:30files,922190bytes and19directories restored exactly; prior sandbox state preserved and original backups unchanged. Real007registry export rebased only to a unique test key also round-trips exactly with prior-key preservation. No live saves or real game registry keys restored. Broader game/launcher coverage remains open.
☐ Verify automatic backup after successful exits across supported launcher chains.
☐ Establish exact game identity and save ownership for the two remaining emulator launcher chains before changing backup selection. Read-only audit: DQhistorical Eden log identifies0100A9D01C446000 but points to an older F:\Downloads installation; configured portable save roots contain0files. KingsBountyRyujinx menu supplies no ROM argument; save directory contains only.lock/ExtraData0/1. Current title/save ownership remains unproven; evidence/criteria-emulator-save-ownership.json. Do not treat all emulator saves as the selected game.
☑ Exclude verified support launcher binaries that prevent automatic executable selection for Dawnwalker and Onimusha; test real-profile discovery without launching games. r43b excludes EpicWebHelper/FitGirl-Launcher and recovers exact-folder local aliases after catalog discovery. Both real local identities now persist correct executables; existing choices/history retained. Exact target120regression/66WPF pass;007first/Action/12.1h verified after refresh. Helper detection identifies both; Dawnwalker has saved settings, Onimusha has no detected save data. No games launched.
☑ Audit the exact HOW_MUCH_LEFT.bat output against latest backup selection.
☑ Verify each supported progress decoder and document unsupported formats. Updated evidence:78helper tests pass;007has checkpoint-weighted estimates, Kristala and Outlaws still lack validated campaign percentages. This does not establish0.001percentage-point accuracy.
☐ Implement remaining progress coverage where authoritative save data permits it.
☑ Replace remaining user-visible unknown labels with precise data-availability explanations, while retaining missing values and never fabricating progress, size, or completion hours; verify native cards, details, and install summaries. r44 native120regression/67WPF and78progress-helper tests pass; exact batch007numeric estimate unchanged, real Kristala explicitly reports missing campaign measurement. Literal native display-label audit passes; data coverage remains unresolved. Personal-state preservation and007recent-first/Action/12.1h retained.
☑ Verify native progress and remaining-time output agrees with the batch tool. Updated r44evidence:00777.365%estimated/~3h39m batch agrees with native~3.6h rounding; missing campaign values remain absent for Kristala/Outlaws. Numerical accuracy remains unproven.
☐ Audit persistence of every editing action across restart and refresh. Details failures are repaired in r46; absent-category assignments in r47; premature startup editing in r48. The broader action/concurrency/failure audit remains open.
☑ Repair category removal for saved identities absent from the loaded catalog; verify the real confirmation UI, overrides, personal-data preservation and restart using the corrected ready-state harness. Exact r47passes12external checks,121regression/75WPF checks, all10original-profile checks and live007first/Action/12.1h after refresh. Compatibility entrypoint activates existing r47. Startup exposure remains unresolved separately.
☑ Prevent editing before profile initialization finishes and retain retry/full-backup recovery access. r48 deterministically reproduced premature-save data loss before the fix; all3 startup assertions now pass. Exact target121regression/78WPF,8external damaged-profile recovery checks,12category lifecycle checks,10original-profile preservation checks and live007first/Action/12.1h pass. Editing and edit shortcuts are guarded until ready; invalid imports preserve the damaged file, and a full backup restores editing with the original retained. Compatibility activation also passes.
☐ Resolve remaining durability failures found by that audit.
☑ Reproduce repeated Details category saves and invalid/failed multi-field saves in the real WPF dialog using an isolated profile. r46-red WPF proof reproduces4failures: move-back ignored, invalid-tag rating mutation, rejected rating leaked by later save, locked-file partial in-memory edits. No real profile touched.
☑ Make Details edits validate and persist atomically, then verify category reversal, validation rejection, storage failure, retry, refresh and reload. PersonalGameEdits stages a validated clone, persists once, then publishes committed fields while retaining the shared UserState identity. All8new WPF assertions pass, including all4previously reproduced failures. Staged r46passes121regression/75WPF checks.
☑ Build and verify the repaired Windows executable through both canonical and compatibility entrypoints; recheck the real personal library before continuing acceptance work. Exact r46target121regression/75WPF checks pass, unchanged root forwarder8profile/target checks pass with r46hash, real007first/Action/12.1h after refresh passes, and all10original-profile preservation assertions pass.1259categories/31tabs/7history records retained. No cleanup or website deployment.
☑ Identify the writer/cause that emptied localCatalog. Reproduced root dist/GameLibrary.exe (September14 build) stripping all1259categories plus installationFolders/pendingGameBackups/wandGames in an isolated profile; history remains. PowerShell GameL points to that older executable. Live loss recurred with old1280catalog/39Wand log signature while native target hash remained unchanged. Evidence:criteria-legacy-profile-loss.json.
☑ Replace the obsolete root dist entrypoint with a tested forwarding launcher to native/dist/GameLibrary.exe; preserve old binary for rollback and verify quoted arguments, exit codes, and actual target identity. Deployed forwarder passes argument/exit-code checks, exact-target121regression/67WPF checks, and8post-deployment profile/target checks. Both copies of the old data loss are retained as evidence; old executable is backed up. GameL now reaches the current executable without changing PowerShell profiles.
☑ Recover missing personal fields without overwriting current edits; verify categories, tabs, history, installed order and both launch paths after refresh/restart. Restored4fields stripped by the older executable, retaining every other current field and adding no old backup jobs. All10preservation checks pass;1259categories/31tabs/7history records retained. Both actual launch paths show007first,Action,Played12.1h,RecentlyPlayed after refresh; native restart preserves them. Old executable and pre-recovery profiles retained; no project cleanup.
☑ Run the final requirement-by-requirement acceptance audit with direct evidence. docs/acceptance-current.md records r44 full-scope audit: acceptance NOT achieved, cleanup prohibited. Fresh141installed rows have covers/sourced positive times;2712game-classified rows lack covers,2689lack positive times. Shared503persists. Backup136paths:7sources,127no saves,1rejected launcher,1wrapper;0missing launch paths.
☑ Fix restore selection so invalid backups with a known different identity cannot block the selected game; retain rejection of unknown-identity or corrupt newest selected-game backups. PS5 sandbox restore regressions pass; real latest 007/Kristala/Outlaws backups each pass VerifyOnly. Profile integrity and all profile hashes unchanged; no real save restored.
☑ Reconcile stale installed markers within an authoritative scan scope without erasing personal history or treating unavailable drives as uninstallations. r42 exact executable119regression/66WPF pass; real profile removes only echoesoftheend,torchlight2,wand:105524 absent-directory markers. All9release preservation checks and10original-profile checks pass; 007 remains first/Action/Played12.1h after refresh. Existing but unidentifiable folders are preserved for further diagnosis.

## Cleanup gate and delivery

☐ Prove that every original criterion is satisfied; record any unmet criterion instead of treating partial tests as completion.
☐ Inventory project storage and identify reproducible disposable artifacts.
☐ Verify every proposed deletion is inside the intended project and unused by the app or build.
☐ Remove only verified disposable artifacts after the acceptance gate passes.
☐ Verify the executable, saved library and build workflow after cleanup.
☐ Report measured space reclaimed and final acceptance evidence.

















































































## Automatic 007 progress investigation

☑ Inspect actual 007 binary headers and the decoder implementation; current decoder only recognizes the layout, and no validated campaign decoder was found in this investigation.
☐ Implement and verify a decoder only when campaign fields and their meaning are established.
☐ Keep universal progress acceptance and cleanup open until verified.

## 007 binary decoder implementation

☑ Implement bounded XOR/zlib decoding and typed current-checkpoint extraction from actual slot saves.
☑ Test malformed data, truncation, duplicate checkpoints, and decompression limits: 54 tests passed.
☑ Verify extraction across all 32 existing 007 slot backups; every checkpoint decoded and every save hash remained unchanged.
☐ Establish campaign checkpoint ordering and completion semantics before calculating percentages.

## 007 campaign denominator investigation

☑ Locate missionconfig.json resource 01C295E29A912E2D in the maintained Bond index and inspect installed RPKG headers.
☑ Extract relevant JSON/ORES resources read-only; audit shows incomplete/prototype checkpoint lists, unsuitable as a full campaign denominator.
☐ Compare the complete ordering against decoded save checkpoints before computing percentages.

☑ Inspect level definitions: extracted the explicit 297-node unbranched campaign route from Clover_Intro to MainMenu.

☑ Implement a clearly labeled checkpoint-based 007 estimate from the extracted campaign route.
☑ Test route integrity, endpoint behavior, and current-checkpoint calculations: 55 tests passed.
☑ Run the exact batch and native progress reader: both derive 77.365% and approximately 3.65 main-story hours from the latest save.

## Kristala automatic save decoding

☑ Decode Kristala GVAS with uesave v0.7.1; verify last checkpoint and main-quest step states.
☐ Identify a validated campaign-completion metric before calculating percentages.

☐ Extract Kristala quest definitions: retoc v0.1.5 reports an encrypted directory index requiring the game archive key; campaign denominator remains unverified.

## Outlaws save-source coverage

☐ Locate Outlaws campaign saves: no .save files found in the installation, current-user Local/Roaming app data, or Public Documents Steam; standard Ubisoft/uplay folders absent.
☐ Update and test source detection if a missing location is confirmed.

## Live Windows progress verification

☑ Launch the exact target and inspect its visible 007 progress label: UI verification passed.
☑ Record criteria-007-live-ui.json and restore the prior search filter.

## Kristala archive access investigation

☐ Check local game metadata and supported read-only archive tooling for quest extraction.
☐ Validate any extracted quest sequence against the decoded save.

## Required default metadata for every existing and future game

User requirement: automatic addition and synchronization must include correct game artwork, total completion hours, local storage requirements, and all displayed metadata. Applies by default to existing games and future additions in the Windows executable. This is an acceptance gate, not a claim of current completion.

☐ Verify future additions receive metadata automatically without manually pressing refresh or editing a title.
☐ Verify game updates and DLC trigger a fresh measured installed-size calculation without replacing it with a published requirement or download size.
☐ Verify an interrupted lookup resumes after restart and fills missing fields when the source recovers, preserving already verified data.
☐ Resolve ambiguous titles using corroborated platform identifiers, including Wand's Ashen game27643 → Steam649950; verify artwork and duration refer to that exact game and edition.
☐ Validate installation-marker identities for build-suffixed folders such as Hellboy before requesting canonical-title metadata; never blindly remove title numbers.

☐ Reproduce the screenshot's Onimusha - Way of the Sword placeholder artwork, missing duration, missing storage figures, and HTTP 503 in the exact Windows executable.
☑ Trace game-entry paths: catalog/Docker and installed scans reload through ApplyFilter/ScheduleMetadata; Wand-only rows were transient and could not retain enrichment across reloads.

## Wand discovery persistence

☑ Persist previously uncatalogued Wand game rows with stable local identities and cached metadata.
☑ Verify Wand-only rows, metadata, and local edits survive reload while current membership counts remain live: 59 WPF checks passed, including three new persistence checks.
☑ Build and validate r23 in the exact Windows executable: 104 regression/59 WPF checks; live Wand filter displays 45, matching the current registration snapshot.
☑ Verify automatic metadata loop for catalog, Docker priority tags, Docker namespace tags, installed scans, and Wand additions with controlled source responses: all enriched fields survived reload; an unavailable title did not block others.
☑ Prioritize off-screen installed games and recent additions ahead of older repository entries; integration test confirms installed entries receive lookup first. Staged checks: 104 regression and 64 WPF.
☑ Deploy and verify r24b in the exact Windows executable: 104 regression and 64 WPF checks passed; normal profile reopened.
☑ Resolve installed-entry missing artwork/duration fields and audit duplicate launchers: all137markedinstalled entries now have decoded artwork and positive hours;9groups share exact existing executable paths. This verifies presence, not universal metadata accuracy.
☑ Resolve Ashen metadata by Wand's exact Steam identity and PC-scoped duration matching; staged105 regression/64WPF and75helper tests pass, live sourced artwork/15GB/~16.4h verified.
☑ Deploy r27b to the exact Windows executable:105 regression/64WPF checks pass; real Ashen UI displays~16.4h and15GB, artwork/source persisted. Coverage now has one installed entry missing artwork/hours.
☑ Deploy r28 installer-marker identity resolution:106 regression/64WPF checks pass on the target; live Hellboy displays correct Steam artwork/~9h/3GB published requirement, preserving local identity and custom labels.
☑ Deploy r29 scanner folder-evidence recovery:107regression/64WPF checks pass; normal startup persists7missing visible installation roots and live Ashen displays8.812GB installed files. DragonQuest custom-launcher mapping remains unresolved.
☑ Deploy r30 custom-launcher root recovery:108regression/64WPF checks pass; startup saves DragonQuest's exact folder, live UI shows5.799GB/596files. All137visible installed entries now have existing mapped roots.
☑ Correct false registry-only rejection: accept consistent registry manifests only after size/path/list/SHA256 validation; retain latest-backup ordering without inventing campaign progress.
☑ Test registry-only integrity and latest-backup selection: 72 helper tests passed; exact HOW_MUCH_LEFT.bat has no skipped-backup warnings and retains latest007estimate.
☑ Fix partial metadata success delaying missing fields for 24 hours; r25 retries incomplete successes after five minutes, including old cache records.
☑ Normalize Steam search punctuation while preserving exact identity validation; live folder-style Onimusha lookup passed.
☑ Deploy r25 and verify 104 regression plus 64 WPF checks in the exact executable.
☑ Investigate Soul Reaver/Defiance: installed AppId2521380 and extraction marker identify Soul Reaver; official Steam identifies Defiance as3747730.
☑ Correct the two catalog identities and the positively verified wrong launcher mapping while preserving personal edits.
☑ Test identity correction, cache rejection, persistence, and Wand reassociation guards: 105 regression and 64 WPF checks passed in r26b.
☑ Deploy r26b: 105 regression and 64 WPF checks; real UI shows distinct correct titles, Soul Reaver playable, Defiance play hidden. Personal data and SRX.exe hash unchanged.
☐ Resolve the exact game and edition before assigning artwork or other metadata; reject sequel, remake, and similarly named title mismatches.
☐ Automatically obtain, cache, display, and verify artwork related to the exact game; replace placeholders when valid artwork becomes available.
☐ Obtain and display sourced main-story completion hours and supported alternative completion styles with their proper estimate labels.
☐ Measure actual installed game-folder size, including updates and DLC where applicable, and refresh it after installation or file changes.
☐ For uninstalled games, obtain a supported local disk-space requirement or estimate for the relevant build; distinguish it from compressed download size and measured installed size.
☐ Recover metadata synchronization after HTTP 503 and other transient failures, retaining valid existing data and retrying unfinished games automatically.
☐ Ensure one unavailable metadata source does not prevent Docker discovery or updates from other available sources.
☐ Persist completed metadata and unfinished enrichment work across app restarts; refresh stale fields automatically.
☐ Test adding a previously absent game through each supported entry path and verify automatic discovery, correct metadata, persistence, and later refresh.
☑ Audit current artwork, duration and installed storage coverage with r48.3967rows/3712game-classified:2712missing decoded covers,2689no positive time,859unsourced estimates,134native-source-guard accepted,30recorded-but-rejected sources. All141visible installed entries have decoded artwork;99accepted durations/42unsourced estimates. All141map to133unique measured folders;133complete/0incomplete scans,1381859793606logical bytes. All10personal-state checks pass. Receipts:criteria-r48-data-coverage.json,criteria-r48-installed-storage-audit.json,criteria-r48-storage-summary.json. File decoding/source guards do not establish independent identity or universal future coverage.
☑ Reproduce and fix automatic duration updates failing to re-sort the active completion-time view. r49-red reproduces both incorrect orders. The fix uses the actual Time to Beat labels; all4WPF assertions pass for both directions and preservation of search/selection/personal state. Staged and exact r49pass121regression/82WPF checks. Live007first/Action/12.1h, all10original-profile checks and compatibility activation pass. No cleanup or website deployment.
☑ Audit generic Docker tag titles against publisher evidence. Five sampled image manifests/configs passed SHA256 validation; four old version-tag images had no identity labels, while Onimusha's GMenu labels matched repository/tag/folder. No layers downloaded or containers started. The catalog has2533numeric/version-qualified entries, including23positive durations incorrectly attributed from title-only matches for games named2or25. Receipts:criteria-r49-publisher-label-audit.json,criteria-r49-generic-title-contamination.json. Repository names/version strings alone do not establish game identity.
☑ Prevent numeric/version-only qualified Docker tags from impersonating game titles. Five pre-fix failures reproduced; all7identity assertions now pass. r50b exact target122regression/82WPF, rendered live source-identity check,10personal-state checks and compatibility activation pass. All2533ambiguous identities are retained;23unsupported times no longer display, with original cache values preserved. Curated numeric game titles and executable-based progress remain supported. No cleanup or website deployment.
☐ Add corroborated publisher-title resolution for ambiguous image versions where the publisher exposes sufficient evidence; retain explicit unresolved identity when original images contain no identifying metadata. Do not infer a game solely from a numeric tag, repository name or optional generic image title.
☑ Resolve GMenu build-tag titles using the verified publisher naming contract; test identity guards and automatic metadata lookup in the Windows executable. r39b preserves114identities and leaves0generated-tag queries; live Onimusha automatically receives decoded Steam2638890artwork,50GB published requirement,HLTB160598~21.4h without manual refresh. Exact target116regression/64WPF checks pass; personal-state checks pass.
☑ Audit136installed lookup titles:89have sourced main-story averages,47remain unresolved,47entry values materially differ from bundled hours. Deploy r32source/freshness verification;110regression/64WPF pass, real KAKU automatically changes22hours to10.7hours with attribution.
☐ Resolve remaining installed-title duration mismatches or absent main-story samples without substituting extras/completionist values or inventing hours.

  r34 evidence: exact provider aliases and trademark normalization recover The Vagrant, Tales of the Shire, and Catherine Classic;44of the prior47unresolved titles remain.78helper tests and111regression/64WPF target checks pass. Live The Vagrant displays~9.1h after a selected time-only refresh. Restored007Action/12.1h/recent-first and all1259category assignments/31tabs/history remain verified after this release. Source recheck: evidence/criteria-r34-duration-summary.json. Immediate automatic retry after a helper update remains unverified because existing retry cooldowns persist.
☑ Resolve the observed large-folder timeout: r31b adds bounded growing retries and folder-shared caching; Slave Zero X completes21,865files/6.418GB and exact UI shows a complete size. All130audited folders have complete measurements at recorded times;109regression/64WPF checks pass.
☐ Verify the final behavior in F:\study\repos\game-library-manager-native\native\dist\GameLibrary.exe before allowing cleanup.

## Metadata source diagnosis

☑ Verify hosted metadata returns HTTP503 while direct Steam lookup identifies Onimusha app2638890, its artwork, and50GB published disk requirement.
☑ Implement independent Steam artwork and disk-requirement fallback with strict title matching; duration fallback remains pending.
☑ Enable metadata for locally discovered games and test persistence.

## Independent Windows metadata implementation

☑ Detect unreadable or replaced artwork files, retry missing artwork independently of sourced hours, and verify image-cache recovery after file replacement. r41b validates decoding/cache hashes, atomically replaces damaged files, and invalidates stale rendered fallbacks. Exact target118regression/66WPF checks pass, including automatic repair after reload with sourced hours retained. Real artwork audit61hash-addressed files:0mismatches; personal data preserved.
☑ Preserve explicit utility/backup classification and prevent unrelated game titles, completion hours, and game metadata retries for those entries. r40:255declared non-game rows,15inapplicable durations suppressed;117regression/64WPF target checks pass. Isolated exact-executable UI confirms utility identity; normal-profile hidden categories remain untouched. All personal-state checks pass.

☑ Implement direct Steam artwork and published disk requirements with exact-title matching.
☑ Verify fallback identity rejection, persistence, and local-game scheduling; live Onimusha lookup recovered its correct artwork and 50 GB published requirement.
☑ Build and test r21b before replacing the target: 104 regression and 56 WPF checks passed.
☑ Verify the deployed executable: 104 regression and 56 WPF checks passed; real-profile automatic Onimusha update persisted its artwork and displayed 50 GB disk required. Search restored after verification.
☐ Add independently sourced completion hours and verify metadata refresh for games that already have artwork.

## Completion-hour source accuracy

☑ Requery all39installed unsourced entries and22alternative readable-title queries. Original and candidate results are retained in criteria-r53-duration-results.jsonl/criteria-r53b-duration-results.jsonl;21alternative queries have positive main-story averages. criteria-r53-duration-diagnosis.json separates compact-equivalent queries from spelling/identity changes. No candidate metadata has been assigned to the application.
☑ Implement15identity-equivalent readable search formats with consistent cached/Wand handling and one-time old-query retry. Pre-fix45failures reproduced; all96focused assertions and124regression/82WPF checks pass in staged r54c. Existing ambiguous-format protection remains tested with an uncurated title.
☑ Corroborate six remaining title identities using Unity app.info, Steam AppIds and a GOG launch manifest plus official store identities. Receipt:criteria-r55-installed-identities.json. DragonQuest's launcher/edition remains separate unresolved work.
☑ Implement six corroborated identity corrections. Pre-fix24failures reproduced; all63catalog identity assertions now pass, with124regression/82WPF checks passing in staged r55. Source identity corrections preserve IDs, personal fields and launcher mappings.
☑ Deploy r55 and verify124regression/82WPF/63catalog-identity assertions. All6automatic source refreshes persist; live AHighlandSong/Mia/RedStringsClub titles and hours pass. Installed coverage123accepted/18unsourced;141covers decode.007first/Action/12.1h,10personal-state checks,45Wandregistrations and compatibility activation pass. A transient file lock stopped the first copy with the original hash intact; exclusive access was verified before successful retry. No cleanup.
☑ Implement bounded GOG manifest title discovery tied to the selected primary executable. Five failures reproduced;15focused checks and125regression/82WPF staged r56b checks pass. A real GOG local identity resolves without a catalog override, fetches artwork/~4.01h and reloads persisted state in an isolated profile. Live proof exposed and fixed package-tag precedence; unrelated manifests retain the installer fallback.
☐ Add platform-ID-confirmed Unity title discovery: app.info can contain internal project names (Tianding uses Zebra), so title-only Steam matching is insufficient. Preserve explicit overrides and reject conflicting or unrelated platform records.
→ Verify automatic manifest-based discovery through local scan/load, metadata scheduling, persistence, exact-target suites and live readback.
☑ Deploy r54c to the exact Windows executable:124regression/82WPF and96focused query assertions pass; all15corrected installed queries automatically persist sourced main-story data. Real BladeChimera/LittleKitty/Lorelei displays pass; filters restored,007first/Action/12.1h and10personal-state assertions pass, compatibility activation passes. Fresh installed coverage117accepted/24unsourced; all141covers decode. No cleanup.

☑ Audit the42installed unsourced entries and prior lookup receipts: three corroborated wrong catalog titles identified using local installation evidence and official Steam identities. Repairs and current source rechecks are tracked separately below.

☑ Verify regression tests reproduce the three corroborated installed-title substitutions: r51-red fails the new identity assertion; per-title proof records correction, stale-cache rejection, app-ID rejection and retry failures.
☑ Correct the three catalog identities and verify metadata rejection and retry behavior: all21focused assertions and123regression checks pass in r51.
☑ Build and verify the staged Windows executable: r51passes123regression and82WPF checks.
☑ Deploy r51b and verify the exact target:123regression/82WPF pass; live007first/Action/12.1h/RecentlyPlayed and all10original-profile assertions pass. Earlier r51target artwork failure retained as red evidence.

☑ Verify same-length artwork replacement invalidates decoded-image and rendered-fallback caches even when original timestamps are retained. Strengthened test passes in full staged and exact-target r51b suites; pre-fix failure and correct repaired file hash retained.
☑ Correlate the10:05UTC crash with Windows events: system commit100282327040/100549758976bytes (99.734%,255MiBremaining), PowerShell memory failures and app thread-creation failure coincide. Windows attribution failed. Nine samples over2minutes show the unchanged r51b process alive, private bytes522010624to398123008,28–35threads,1539–1588handles; all10personal-state checks pass. Evidence:criteria-r52-resource-exhaustion-xml.json,criteria-r52-memory-events.json,criteria-r52-memory-observation.json. This verifies the observed system shortage and current recovery, not responsible-process attribution or permanent prevention.
☐ Establish the process responsible for the system commit exhaustion if historical attribution becomes available; current Windows events confirm99.734%commit usage but report failed attribution. Do not claim that a short healthy run proves permanent crash prevention.
☑ Recheck live sources and automatic rendering: Dune: Imperium8.8389h/Steam1689500, Planet of Lana4.625h/Steam1608230, Ghost Trick12.0733h/Steam1967430; all three get matching cached artwork and pass real-window checks without manual refresh. Fresh r51b audit:102sourced/39unsourced installed durations,141decoded installed covers. All10personal-state checks still pass. Full acceptance and cleanup remain unmet.

☑ Reject fuzzy mismatches and invalid duration units in the progress helper before reusing its source.
☑ Test correct identity, cache rejection, seconds conversion, and missing campaign durations: 63 helper tests passed.
☑ Probe the corrected live source: exact matches for Onimusha, 007, Kristala and Outlaws; Kristala has no main-story average in this response.
☑ Integrate bounded independent duration lookup in native metadata refresh and persist attribution.
☑ Verify native duration fallback during hosted outages without requiring artwork refresh; staged checks and live Onimusha source lookup passed.
☑ Build, test, and deploy r22: 104 regression/56 WPF checks on the target; live UI shows Onimusha ~21.4 h and 50 GB disk required, with source attribution persisted. Corrected folder-punctuation search query; 64 helper tests pass.

## 24 September continuation — verified r57c repairs

☑ Deploy bounded platform-ID discovery tied to selected executable ancestry, Unity plugin directories and Unreal Steamworks directories; reject conflicting IDs, unrelated plugins, traversal and reparse paths. ANSI/BOM encodings are covered. Exact Steam identity establishes canonical lookup while preserving personal names. Final r57c target passes127regression/82WPF and5controlled pause/recovery checks.
☑ Prove real automatic canonical repair for Rogue Trader, Nocturnal and Fury Unleashed; verify Tales of the Shire internal Unity identity and GOG primary-task title/artwork/hours/storage. Retain source attribution and rejection guards.
☑ Fix case-sensitive provider prefilter while retaining title/edition/platform guards. Helper82tests pass; live isolated exact-query proof recovers7valid sources. Running-app retry/readback remains scheduled according to durable backoff.
☑ Reject shared Temp/profile/Windows roots in backup detection and restore; reject broad registry roots in restore.9detector-root/27selected-game/13Unreal and9file-root/18registry checks pass; isolated actual-format restores pass. Current coverage6detected/128no detected saves/1rejected identity/1wrapper across136launchpaths. No live saves restored.
☑ Verify final current-helper exact batch/native agreement:9checks pass,00798.649%,about13minutes remaining,same latest backup and unchanged manifest/12payloadhashes. This remains an equal-checkpoint estimate, not calibrated0.001percentage-point accuracy.
☑ Reverify current personal state:007firstInstalled/Action/RecentlyPlayed/Played16.1h,49Wand registrations and stable total under empty search; all10original-profile preservation checks pass. Old12.1h/45counts above are historical.
☑ Verify Docker cold/live snapshot1326repositories/3959taginstances/1277backuptags,0errors,allfresh; observed new repository automatically. Shared API still503usage_exceeded. Current logical storage133complete roots covers141installed rows.
☑ Finish platform-format recovery and final running-app duration readback in r60: TENOKE bounded full reads, secondary GOG game tasks, release-year disambiguation, durable provider cooldown and hidden-installed enrichment pass. Seven uppercase titles recover automatically; Sonic Mania passes an explicit native time-only refresh. Final installed coverage134/141source-accepted,7unverified estimates;141covers decode. Current acceptance report records every remaining gate.
☐ Resolve remaining source/campaign/launcher gaps only from corroborated evidence. Universal availability, future compatibility and mathematical progress precision remain unproven.
☑ Evaluate cleanup gate: false. No cleanup performed,0bytes reclaimed; rollback/evidence/dirty worktree retained.

## r61b follow-through

☑ Verify account-level503cause: Netlify Functions187594/125000requests, team paused, periodSeptember1–October1. No billing changes.
☑ Repair healthy shared polling and conflicted-queue polling; verify12fallback/cadence cases with safe read-only authoritative GitHub fallback, pending-edit preservation and primary recovery.
☑ Reproduce helper MemoryError behavior with failing tests, implement fatal no-partial-result handling and64KiBregistryhash reads; all92helpertests pass.
☑ Build and deploy r61b with rollback;128regression/82WPF/5controlledpause exact-target checks pass, plus22storage/23backoff/34resilience/12fallback cases.
☑ Verify live GitHubfallback1259categories/31tabs and directDocker1277backuptags; preserve503/sharedwrite limitation explicitly.
☑ Reaudit576absentexpected savepaths, emulatorpayload gaps, metadataidentity classification and four genuine title candidates; preserve sourceguards and unresolved gates.
☑ Establish15:18commitexhaustion and15:20unexpectedreboot; restore app/tracker and record finite observations without claiming permanent prevention.
☑ Reverify10originalprofilechecks,00716.1h/Wand49, compatibilityactivation, exactbatch/native9checks and unchangedlatestbackup payloads.
☑ Create and verify six-hour quiet service monitor game-library-service-health.
☐ Full original acceptance remains blocked by hosted-write quota, missing source/campaign evidence and unproven universal guarantees. No cleanup.
