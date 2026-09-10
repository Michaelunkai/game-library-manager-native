# Game Library Manager Native

This project contains the native Windows WPF build of Game Library Manager.
It is the desktop executable lane, separate from the related web repository.

## Run the verified bundle

```powershell
F:\study\repos\game-library-manager-native\dist\GameLibrary.exe
```

The direct launch is the supported route: it selects the adjacent F-drive
`data\` profile automatically and keeps the window on the secondary display
when that display is available; startup placement is normalized after WPF
restores any stale Windows placement. The adjacent native WPF runtime DLLs and
`dist\tools` directory are part of the self-contained package; no native
runtime extraction to the user's TEMP directory is required. `--offline`
remains available for a no-network session, and `--main-monitor` is available
when the primary display is explicitly wanted.

## Project layout

- `native\` — C# WPF source, build scripts, acceptance checks, and tests.
- `public\` — catalog source used by the native build.
- `dist\` — verified runnable Windows x64 bundle (kept outside Git history).
- `data\` — F-drive runtime catalog, cache, state, and job receipts (kept
  outside Git history).
- `evidence\` — small verification receipts from the completed acceptance
  run.

## Verification receipt

`evidence\self-test-final-20260910.json` records 69 passing diagnostic
checks with zero failures. The fresh external WPF UI proof records 31
passing checks, and the WPF pause proof records 29 passing checks including
an actual AHK-format pause/resume fixture. The final source executable was
copied into `dist\GameLibrary.exe` and retains its SHA-256 identity:

`4DAF00CF10CBABA8EB572E15EE43534C5DC091E14D3393847794C42F50DAD992`

The final executable is 460,153,704 bytes and embeds the complete catalog
payload: 1,179 games and 2,028 cached covers. The pause proof recorded
`activeBeforePause=1.41`, `pausedDelta=0.00`, and `resumedDelta=1.64` seconds
against `F:\study\Platforms\windows\autohotkey\mymainahk\frozen-processes.ini`.
The live verification located its window on the non-primary display. Install
requests are exact game/destination reservations with cross-process lock files,
so repeated requests do not write the same game folder concurrently while
independent destinations remain parallel.

The root bundle and the build-output bundle were synchronized by checksum
after the final tests. The app was then launched from the root bundle and
verified responding on the non-primary display.
