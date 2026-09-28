# Native app rebuild and verification — 2026-09-28

## Delivered executable

- Path: `F:\study\repos\game-library-manager-native\native\dist\GameLibrary.exe`
- SHA-256: `B13810C8738097338E421989363957F8245EA387284876152D3EA6C87A1D8BA5`
- Size: 461,407,040 bytes, 74 files in the bundle.
- Rollback (previous deployed build): `evidence\native-dist-rollback-20260928T1335Z`
  - Previous SHA-256: `87B42C719D2F2AABB8B76198322B7BE1C7F4829B28AD5230A9AAC3C5C793D9DB`

## Change

`native\MetadataEntryPathTests.cs` (untracked source in the worktree) simulated a
"verified" HowLongToBeat duration using only `timeTitle`, `timeUrl` and
`timeFetchedAt`. The current source-acceptance contract
(`CompletionDuration.HasSource`) also requires `timeSamples > 0` and a matching
`timeQuery`/`matchedTitle`. Without those fields the reloaded duration was not
treated as sourced, so `RunAutomaticMetadata` re-queried all five fixture rows
instead of only the row whose artwork was damaged, failing the WPF check
*"Automatic metadata repairs a damaged shared cover after reload without manual
refresh"* (`fixture.Order` grew by 5 instead of 1).

Fix: add `timeQuery` and `timeSamples = 50` to the simulated verified record so
the fixture matches the real metadata the app writes. No product code changed.

## Verification

All runs used isolated profiles; no live profile or game was touched.

| Suite | Command | Result |
|---|---|---|
| Core self-test | `native\dist\GameLibrary.exe --self-test` | 146 passed / 0 failed |
| Storage + sync proof | `--storage-sync-proof` | passed (22/23/34/12) |
| Install-job proof | `--install-job-proof` | passed (8 checks) |
| Native pause proof | `--native-pause-proof` | passed (5 checks) |
| WPF UI proof | `--data-dir <isolated> --offline --ui-test` | 86 passed / 0 failed |
| Reliability Stage (staged build) | `verify-reliability.ps1 -Phase Stage` | 12/12 passed |
| Reliability Target (deployed build) | `verify-reliability.ps1 -Phase Target` | 12/12 passed |
| Wand Node fixtures | `node --test wand-*.test.cjs` | 4/4 passed |
| Reass restore fixture | `Test-ReassRestore.ps1` | `REASS_SELFTEST_OK` |
| ASS detector self-test | `AssLatestGameBackup.exe --self-test` | `ASS_DETECTOR_SELFTEST_OK` |
| Progress helper | `pytest games\tools\gameprogress\tests` | 98 passed |
| Metadata-focused harness | `MetadataFocused.csproj` | 15 evidence + 18 classification + steam passed |

Target verification receipt:
`evidence\reliability-target-20260928T133558Z\target\d22b2cab759f470faab116cc1f7ecca2\verification-summary.json`

## Remaining criteria that are NOT satisfiable from the native app / local machine

These are unchanged by this rebuild and remain explicitly unverified:

- Shared-sync writes: hosted Netlify Functions quota suspended (HTTP 503
  `usage_exceeded`); account is read-only until the next billing period or an
  authorized plan change.
- Live Docker durable installation: Docker Desktop internal metadata store
  returns `read-only file system`; no real install can complete.
- Live Wand trainer attachment: requires launching Wand 12.58.0 and a real game;
  CDP endpoint `127.0.0.1:9222` is absent. Audit remains `launchVerified=false`.
- Save ownership for the Dragon Quest VII (Eden) and King's Bounty (Ryujinx)
  emulator chains: no verifiable campaign save payload is present.
- AHK pause window-placement restore (`evidence\ahk-pause-parity-20260928\run8`):
  the geometry lives in the external production script
  `F:\study\Platforms\windows\autohotkey\mymainahk\current.ahk`, not in this
  repository; the native `--native-pause-proof` (process suspension/resume)
  passes.
- Website/browser parity and deployment: out of scope for the native app.
