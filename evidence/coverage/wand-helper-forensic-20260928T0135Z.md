# Wand / DELTARUNE helper forensic note

**Scope:** read-only review of retained library activity, preflight status, trace-file presence, Windows Application/WER and Security events. No game, Wand process, UI, or profile state was controlled.

## DELTARUNE process trials

- Earlier trace name supplied by the Wand lane: `C:\Users\Admin\AppData\Local\Wand\logs\tophat\2026-09-27T23-08-21Z-3c82af66.traces.otlp` (PID 19296). The file is absent in the reported directory at audit time. The library activity log has no entries timestamped 2026-09-27 23:08–23:09Z. No exact PID creation/exit timestamps or exit code were available for PID 19296.
- Later process: PID 2212 (`DELTARUNE.exe`). Wand lane supplied creation FILETIME `0x1DD4EDFAC89C6D3`, which converts to **2026-09-28T00:24:07.0366931Z**. The retained preflight status says the helper session began at **00:24:17.988Z**, plugin loaded at **00:24:18.288Z**, and `trainer-command-success` was observed at **00:24:18.289Z** for PID 2212. At **01:13:50.229Z**, status was `connected=false`, `reason=matching-view-not-found`. Root's read-only CIM check around **01:34Z** found no PID 2212 and a full exact-path scan found no DELTARUNE process. This bounds the process as absent by that observation; it does not establish its exact exit time or exit code.
- The reported raw trace `C:\Users\Admin\AppData\Local\Wand\logs\tophat\2026-09-28T00-24-17Z-5106e4e4.traces.otlp` is also absent at audit time. The `tophat` directory contained no files when checked. Thus the retained status JSON supports a brief helper/plugin observation and successful command result, but the underlying OTLP events cannot be independently replayed or inspected.
- `data\activity.log` contains no DELTARUNE launch, PID, session-end, or Wand records for either 2026-09-27 23:08–23:09Z or 2026-09-28 00:24–00:25Z. It records `Clean shutdown` at **2026-09-28T00:15:32.9289999Z** and the next startup at **00:26:35.8826871Z**, so the library was not present to record the later helper trial.

## Windows event evidence

- Application log IDs 1000, 1001, 1002, and 1026 were checked around each trial (local time UTC+03:00): **02:03–02:15 on Sep 28** for the PID 19296 trace start, and **03:15–04:35 on Sep 28** for PID 2212. No DELTARUNE, Wand, or WeMod application-error/WER event was found. The later interval contained unrelated WER items for `llama-server.exe`, Windows servicing, PowerShell, DWM/RDP, and other processes.
- No DELTARUNE/Wand/WeMod report directory was present in WER ReportQueue or ReportArchive at audit time. Security log was enabled, but no process-audit event IDs 4688/4689 were returned for 03:15–04:35 local. These absences do not prove the game did not crash; they leave no independent crash or process-exit record for these trials.

## Prior registered-game launch samples

All timestamps below are UTC and refer to `data\activity.log` line numbers.

| Game / identity | Retained evidence | What it establishes |
|---|---|---|
| Megabonk, Steam `gameId=107171`, executable `E:\games\megabonk-f4ddaef0\Megabonk.exe` | Lines 421768–421790: exact mapping and Wand handoff; exact PID **46180** yielded at 2026-09-27 18:20:00.552Z. Lines 421819 and 421853: no fresh session within 30 seconds; `malformed-sidebar-view`; trainer attachment not confirmed. Lines 421902–421903: user requested Exit game + Wand at 18:21:57.143Z; session ended at 18:21:57.207Z after 117 seconds. | Trainer attach/view evidence was missing while the game was deliberately left running; recorded final exit was user-requested, not evidence of a game crash. |
| Ashen, Steam `gameId=27643`, executable `E:\games\Ashen\Ashen\Binaries\Win64\Ashen-Win64-Shipping.exe` | Lines 421921 and 421929: exact mapping/readiness. Line 421939: generic process `Win64` exited before tracking at 18:22:32.158Z, no error dialog. Line 421940: protocol reported PID **46352** at 18:22:32.221Z; line 421942 says the exact game process exited before it could be tracked. | A launch-to-tracker/process-identity timing failure. Logs do not identify whether game, launcher, or another component caused the short-lived process; no crash conclusion is supported. |
| South of Midnight, Steam `gameId=100324`, executable `E:\games\SouthofMidnight\Midnight\Binaries\Win64\SouthOfMidnight.exe` | Lines 373682, 373688–373689: exact mapping and protocol attempt at 2026-09-26 19:15:41–19:15:43Z. Line 373703: local CDP connection refused on `127.0.0.1:9222`; lines 373705–373706: no exact process yielded and retry. Lines 373723 and 373738: app reported exit before tracking and no fallback process was started/terminated. A later retry tracked PID **38836** at lines 373840–373841. | The first failure was in the local Wand/CDP handoff path; later tracking succeeded. The first attempt does not establish a game crash. |
| DELTARUNE, Steam `gameId=57393` | Lines 422045–422046: tracked PID **28112** at 2026-09-27 18:24:05.935Z. Lines 422066–422067: user requested Exit game + Wand at 18:24:27.183Z and session ended after 21 seconds. Lines 422069–422071: a subsequent protocol start lacked fresh trainer-session evidence; game exited before tracking, with no error dialog. | Prior app-mediated session ended by the user; the following attempt failed to prove trainer attachment and did not log a crash. This is separate from PID 2212's later, app-offline helper trace. |

## Assessment and evidence limits

- **Game crash:** unproven. No matching Application Error/WER or retained crash report was found. A process later being absent is not a crash record.
- **Launcher exit:** unproven for PID 19296 and PID 2212. There is no parent-process/exit-code chain in retained evidence. Historical Ashen/South of Midnight records show that protocol handoff and process tracking can fail independently of a proven game crash.
- **Trainer disconnect/attach loss:** the PID 2212 status changes from `trainer-command-success` to `matching-view-not-found`, which is evidence that the helper no longer found the matching view. Without the missing trace payload or a timestamped process-exit event, it cannot distinguish a helper disconnect from the game process ending.
- **Library evidence gap:** confirmed. The library activity log was shut down during the later trial, and the referenced OTLP files are missing. The available records do not provide a complete launcher → game PID → trainer attach → disconnect/exit timeline.

## Source fingerprints

- `F:\study\repos\game-library-manager-native\data\activity.log`: 45,331,658 bytes; SHA-256 `EFD3D7BCDF8E6182045E8ADB64359704CCBC160EE9B145520B79E4D37B2B46A8` (last write 2026-09-28T00:26:50.1409781Z).
- `F:\study\repos\game-library-manager-native\evidence\wand-preflight-20260928T0108Z\deltarune-2212-status.json`: 442 bytes; SHA-256 `52886E0CA42A59850036CC0C3BF5D28E4B026A38F61F065539A572A4C345EB52` (last write 2026-09-28T01:13:50.3003577Z).
- Raw trace presence checks and Windows event queries were performed read-only at approximately 2026-09-28T01:35Z. Trace-file absence is only a statement about the named path at check time; it does not establish whether the trace was never written, rotated, or stored elsewhere.
