# Game Library Manager Native

This project contains the native Windows WPF build of Game Library Manager.
It is the desktop executable lane, separate from the related web repository.

## Run the verified bundle

```powershell
F:\study\repos\game-library-manager-native\dist\GameLibrary.exe --offline --second-monitor --data-dir F:\study\repos\game-library-manager-native\data
```

The adjacent `dist\tools` directory is part of the self-contained runtime.
The application and its copied catalog, cache, and state are all located on
the `F:` drive. The `--second-monitor` option keeps the window on the
secondary display when that display is available.

## Project layout

- `native\` — C# WPF source, build scripts, acceptance checks, and tests.
- `public\` — catalog source used by the native build.
- `dist\` — verified runnable Windows x64 bundle (kept outside Git history).
- `data\` — F-drive runtime catalog, cache, state, and job receipts (kept
  outside Git history).
- `evidence\` — small verification receipts from the completed acceptance
  run.

## Verification receipt

`evidence\self-test-monitor.json` records 63 passing diagnostic checks with
zero failures, and the monitor-aware isolated UI proof records 28 passing
checks. The final source executable was copied into `dist\GameLibrary.exe`
and retains its SHA-256 identity:

`E605C2C92B5E38A8C222A59B1FBD7CBFF71C62B17C88BC9DDBD3CA09587E8516`

The final executable is 468,333,647 bytes and embeds the complete 2,343-file
catalog payload. The live verification located its window on the non-primary
display and confirmed the expected 1,260-game catalog.

The previous live bundle was preserved because it was running during
organization; this project contains a verified copy rather than deleting or
moving the live source bundle.
