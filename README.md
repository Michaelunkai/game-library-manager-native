# Game Library Manager Native

This project contains the native Windows WPF build of Game Library Manager.
It is the desktop executable lane, separate from the related web repository.

## Run the verified bundle

```powershell
F:\study\repos\game-library-manager-native\dist\GameLibrary.exe
```

The direct launch is the supported route: it selects the adjacent F-drive
`data\` profile automatically and keeps the window on the secondary display
when that display is available. The adjacent native WPF runtime DLLs and
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

`evidence\self-test-default-profile.json` records 64 passing diagnostic
checks with zero failures, and the fresh external WPF UI proof records 31
passing checks. The final source executable was copied into
`dist\GameLibrary.exe` and retains its SHA-256 identity:

`8D152C92E07276233F5BA3DF7B5B3A38576B2FE0470A5830928BD8770F7FC14A`

The final executable is 460,116,840 bytes and embeds the complete 2,343-file
catalog payload. The live verification located its window on the non-primary
display and confirmed the expected 1,260-game catalog.

The previous live bundle was preserved because it was running during
organization; this project contains a verified copy rather than deleting or
moving the live source bundle.
