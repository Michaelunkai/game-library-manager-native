# Current Windows acceptance — r61b, 24 September 2026

**r61b is deployed and verified. Full acceptance remains unmet; cleanup is prohibited by the original gate.** This follow-through repaired two reproduced failures and established the hosting quota and reboot evidence. It does not claim permanent external availability, mathematical progress precision, or compatibility with every game.

## Delivered release

- Executable: `F:\study\repos\game-library-manager-native\native\dist\GameLibrary.exe`
- SHA256: `9E19C2D465777926FC117CD7A4EE79B9434D84FBEAA3A277F5818D490E595733`
- Matching DLL SHA256: `C06E899468139A7F347AE0ED7F0836F671882EA1F73A9CF71DF261E14C02282B`
- Staged and exact-target checks: **128 regression,82 WPF,5 controlled pause/recovery**. Independent storage/sync suite:22 storage,23 backoff,34 resilience,12 fallback/cadence. Do not add overlapping suites as independent coverage.
- External progress helper: **92 tests passed** after final changes. Exact batch/native agreement:9 checks passed against the same latest007 backup and unchanged12payload hashes.
- Rollback: `evidence/pre-r61b-release-20260924-184532`. No project cleanup; **0bytes reclaimed**.

## Repairs and newly established causes

The authenticated Netlify dashboard confirms a Functions quota suspension: **187594 requests against125000**,62594over, for September1–October1. All team projects are paused. Its offered remedies are next-period restoration or a paid upgrade; no payment method is saved, and no billing change was made. The native app previously read shared configuration every2seconds even when healthy, potentially43200requests/day per continuously running client. This is a verified contributing pattern, not proof that the client caused every billed invocation.

Healthy shared reads now use a60second interval. Explicit refresh and actionable authenticated edit queues can refresh immediately; conflicted or mixed queues obey the interval. Docker refresh remains independent. Failed initial shared reads can load the backend's exact public GitHub configuration through a credential-free, redirect-disabled, bounded2MiB read with a20second deadline and schema checks. The fallback cannot perform writes, confirm write readback, clear pending edits or advance successful-sync state. Status remains explicitly read-only. Live exact-target proof loaded1259categories/31tabs and verified1277direct Docker backup tags during the real503 outage.

Windows evidence establishes99.7335%commit exhaustion at15:18:22UTC followed by an unexpected reboot at15:20:28UTC. That explains why the prior app and tracker processes disappeared. Kernel event records bugcheck0x3B; dump creation failed. Responsible workload and exact crash cause remain unidentified. Current Windows configuration has an automatically managed13GiB pagefile; prior configuration is not proven. No global memory setting was changed. The helper's observed scan MemoryError now exits nonzero with a concise resource-unavailable message and no partial progress. Registry hashing uses64KiB reads while still validating the entire payload. Twelve observations found both reopened processes alive. The tracker responded throughout; the app had one nonresponsive sample during the UI-verification interval and responded on the next sample. This is finite evidence, not a claim of uninterrupted responsiveness.

## Original requirements and twelve-step follow-through

The subsequent six-sample idle observation found both processes alive and responsive throughout (`evidence/criteria-r61b-idle-observation.json`). Final evidence and executable-hash consolidation is recorded in `evidence/criteria-r61b-final-acceptance.json`.

| Requirement / step | Verified result | Remaining limit |
|---|---|---|
| Installed-only Play controls | Exact-target WPF/direct-launch guards pass. | Finite tested installation changes. |
| Pause/resume and minimum resources (7) | Five controlled process-tree cases pass on exact target. | Every actual game, engine, emulator and launcher was not exercised; no universal or minimum-resource guarantee. |
| Shared-service repair (1) | Quota cause verified in account UI;60second healthy cadence and read-only fallback deployed. | Hosted writes remain503 until quota reset or an authorized account change. |
| Every Docker repository/tag synchronized | Current direct backup snapshot1277tags verified; preceding full enumeration1326repositories/3959tag instances retained. Namespace code unchanged. | Earlier full snapshot is timestamped historical evidence, not a new exhaustive scan or future real-time guarantee. |
| Seven installed duration gaps (2) | Fresh15:52UTC snapshot134/141source-guard-accepted durations;7unverified estimates. | Kristala,Ash & Rust,Burden of Command,Kaiju,Campfire,Dragon Quest VII,Under the Witch remain unsupported. No provider absence inference. |
| Artwork/catalog coverage (3) | All141installed covers decode. Catalog source-accepted duration count498. |2711missing covers include2667unresolved qualified Docker identities and44bare tags; those44include4game candidates,21fixture names,11recognizable utilities,8unresolved. These are classifications, not payload proofs. Four normal isolated native refreshes produced no accepted cover/time updates. Steam returned inconsistent app-ID envelopes for two titles; ambiguous titles and inconsistent sources remain rejected. |
| Local GB | Retained133complete root measurements cover141installed rows. Code unchanged. | Logical size differs from allocated storage; no exact future/uninstalled size guarantee. |
| Save detection (4) | All576expected paths for128unresolved games checked absent; bounded alternative-root audit found no missed active campaign payload. |6/136launch paths have detected saves;128unresolved,1launcher,1wrapper. Emulator directories contain no proven campaign payload. Retained uninstalled Dying Light2save preserved. |
| Backup/restore and exit backup (5) | Packaged queue/retry checks pass; fresh ASS detector self-test and Reass integrity verification pass. Prior isolated file/registry restore proofs retained. | Every game's successful exit and live restore untested. No live save restored or real game launched in this follow-through. |
| Exact batch/progress/native agreement (6) | Same latest007backup:98.649%,0.21784h/about13minutes remaining; native0.2h;9agreement checks pass. | Equal-checkpoint estimate is not calibrated to0.001percentage-point accuracy. Kristala denominator and Outlaws campaign payload remain missing. |
| Missing-data wording | Explicit unavailable/estimated states; no request for manual percentages. | Wording cannot manufacture missing measurements. |
| Resource diagnosis (8) | Commit exhaustion, unclean reboot and dump failure established; helper fails without partial progress; app/tracker recovered. | Responsible workload, bugcheck cause and permanent prevention unproven. |
| Preservation and recovery (9) | All10original-profile checks pass:1259categories,31tabs,7history records. Live007first/Action/16.1h; Wand49; compatibility activates same process. New fallback tests preserve edits and reject malformed/oversized responses. | Finite tests cannot prove permanence against all external writes or hardware failure. |
| Final exact-target acceptance and Windows scope (10) | Final source rebuilt, stage and exact-target suites passed; live fallback, UI and progress verified. No website deployment. | Full original acceptance remains false. |
| Cleanup (11) | No cleanup; rollback, evidence, saves and dirty worktree preserved. | Gate remains closed. |
| Maintenance (12) | New regression tests plus six-hour heartbeat `game-library-service-health`; quiet for unchanged/non-actionable state, notifications on meaningful changes. | Monitoring detects changes; it cannot guarantee uptime or supply absent data. |

## Evidence

Paths are relative to this repository:

- `evidence/criteria-release-r61b-20260924.json`
- `evidence/criteria-r61b-target-selftest.json`, `evidence/criteria-r61b-target-ui.json`, `evidence/criteria-r61b-target-pause.json`, `evidence/criteria-r61b-target-storage-sync.json`
- `evidence/criteria-r61-final-helper-tests.log`, `evidence/criteria-r61-memory-helper-red.log`
- `evidence/criteria-r61b-live-read-fallback.json`, `evidence/r61-hosting-root-cause.json`
- `evidence/criteria-r61b-native-batch-agreement.json`, `evidence/criteria-r61-exact-batch-latest.json`
- `evidence/criteria-r61b-current-personal-ui.json`, `evidence/criteria-r33-profile-preservation.json`, `evidence/criteria-r61b-compatibility-activation.json`
- `evidence/criteria-r61b-metadata-audit.json`, `evidence/criteria-r61b-data-coverage.json`, `evidence/r61-metadata-classification.json`, `evidence/r61-metadata-four-refresh-findings.md`, `evidence/r61-metadata-steam-final-route-probe.json`
- `evidence/r61-save-audit-findings.md`, `evidence/r61-save-audit-resource-reboot.json`, `evidence/criteria-r61b-running-observation.json`
- `evidence/criteria-r61-ass-selftest.log`, `evidence/criteria-r61-reass-selftest.log`, `evidence/service-monitor-state.json`

Earlier requirements and detailed r60 proofs remain in `docs/acceptance-r60-before-r61b.md`. Jev was explicitly invoked but returned **balance_exhausted HTTP402**; no repair judgment is attributed to it.
