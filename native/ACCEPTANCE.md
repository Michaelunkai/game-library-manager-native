# Native acceptance record — 2026-09-14

This record covers release 1.1.0 of the native WPF application and its reviewed same-site synchronization source. Historical hashes and machine-specific installed paths have intentionally been removed; the final release receipt and GitHub assets are the source of truth for artifact identity.

## Implemented acceptance criteria

- Direct launch chooses the monitor containing the pointer. Explicit primary/secondary monitor overrides still work, and dimensions are safe on small high-DPI work areas.
- Packaged metadata and cover assets work offline. Cache overlays are accepted only after game-identity matching. Approximate completion hours are bounded and do not replace a good known value with an invalid result.
- Installed size is recursively measured from local files, timestamped, and preferred over Docker download size. Inaccessible or incomplete scans fail closed instead of reporting partial bytes as exact.
- Install operations validate their identifier before path construction, use unique staging and destination reservations, restore an interrupted prior install, and clean only exact stale artifacts owned by that game. The visible terminal transcript, structured job log, command context, and exit code persist on failure.
- Shared edits enter a durable per-field outbox before transport. Conditional writes, conflict retention, interrupted-acknowledgement reconciliation, and read-back verification implement bidirectional native/website synchronization without silently discarding local intent.
- The Windows 11 Fluent shell includes custom caption controls, Mica where supported, semantic light/dark/high-contrast palettes, keyboard focus, accessible targets, reduced popup motion, and status announcements.
- The executable and application bundle use the new multi-resolution Fluent game-library icon.

## Fresh verification before release

| Gate | Result |
|---|---|
| Native diagnostic self-test | 92 passed, 0 failed |
| Packaged offline WPF proof | 39 passed, including a harmless real terminal process that exited 7 and left a non-empty transcript |
| Backend conditional-write fixtures | 10 passed |
| Actual Netlify Blobs SDK wire/error checks | 6 passed |
| Browser queue/integration logic | 10 passed |
| Official Netlify function bundling | 15 passed; four ZIPs, Node 24, Blobs SDK 10.7.13, exact fallback data, safe-fetch guard |
| Staged deployment source | 2,048 files hashed, including 2,028 images and the explicit package lock |

The final clean build is re-run after all source commits. Its SHA-256, size, exact root path, release tag, GitHub download, and live production result are reported in the release handoff.

## Evidence boundaries

The old cited installation record contained only a waiting message and a generic exit code. It cannot be reconstructed retrospectively. The new end-to-end failure fixture proves that future terminal output is durable; success of every third-party Docker image still depends on that image, Docker VMM availability, storage, and network conditions.

The executable remains unsigned. Verification on this Windows installation does not prove behavior on every other PC, every game, every network condition, or every future provider version.
