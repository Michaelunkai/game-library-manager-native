# Game Library Native Reliability and Fluent Redesign

## Purpose and constraints

Upgrade the existing .NET 10 WPF executable in place. Preserve the current catalog, exact-case Docker identities, offline-first state, Wand integration, script exports, installed-game discovery, and self-contained packaging. Do not replace the native app with the related website or perform a framework migration that would discard tested behavior.

The product is a single-user Windows 11 game-library cockpit: calm, dark-first, information-rich, and optimized for mouse, keyboard, and mixed-DPI multi-monitor use. Its memorable interaction is a persistent sync/status rail that makes every local save, remote acknowledgement, install phase, and recoverable error visible without modal interruption.

## Architecture

### Window placement and shell

Startup monitor selection has three modes: `Auto`, `Primary`, and `Secondary`. `Auto` is the default and selects the monitor containing the pointer at process startup, falling back to the foreground window's monitor and then the primary monitor. Explicit command-line switches retain deterministic overrides. The window is centered and clamped inside that monitor's working area using device-pixel-aware placement.

The WPF window remains resizable and snap-capable. A custom caption row uses `WindowChrome`, native hit-test behavior for drag/maximize regions, standard system commands, and DWM attributes for rounded corners, dark caption integration, and Mica where the OS supports it. Unsupported systems fall back to opaque semantic surfaces.

### Design system

Theme resources define semantic roles rather than per-control literals: canvas, shell, surface, elevated surface, border, text, muted text, accent, accent-on, success, warning, danger, information, focus, and selection. Segoe UI Variable is preferred with Segoe UI fallback. Spacing follows 4/8/12/16/20/24/32, controls use at least 40-pixel pointer targets, cards use 12-16 pixel radii, and motion uses short 120-220 ms easing with a reduced-motion bypass.

The main window uses a compact navigation rail, a responsive command/search surface, stat tiles, rich game rows, a deliberate empty state, and a bottom status rail. Job windows share the same theme and expose phase, current game, retry count, captured output, final error summary, and recovery action. High contrast and keyboard focus remain functional even when decorative material is disabled.

### Metadata and size truth

Provider results are accepted only when exact identity checks pass. Curated aliases stay closed and test-covered; arbitrary fuzzy matches never overwrite a valid image or completion time. Missing or rejected data remains explicitly unknown and is retried with persisted backoff.

Docker Hub `full_size` is labeled as download size. After a verified installation marker exists, the app measures the actual promoted game folder and stores that byte count atomically; installed rows show measured local size with its observation time. Prediction and observation are never conflated.

### Install reliability and diagnostics

Each selected game remains independently transactional: unique operation id, exact destination-scoped reservation, staging folder, owned labeled container, bounded pull/extraction retries, completion marker validation, atomic promotion, and previous-install rollback. A failed game does not erase or mislabel successful games.

Visible-terminal installs also write a dedicated transcript. The progress window tails that transcript into the durable job log, records each game's terminal state, and displays a concise failure summary instead of only `exit code 1`. Cancellation is cooperative while queued, process-tree based while running, and never removes an unowned container or a previous valid install.

### Persistence and synchronization

Every mutation first performs an atomic local write with backup recovery. Shared changes then enter a durable outbox before network I/O. The sync loop uses conditional writes, read-back verification, idempotent acknowledgement, bounded retry with jitter, explicit conflict retention, and a visible state of Saved locally / Syncing / Synced / Needs attention.

Website/backend work may only claim cross-client completion after the existing Netlify site advertises conditional writes and the maintained web client is deployed and verified. Authentication, GitHub, Netlify, or network unavailability must never lose the local mutation; it remains queued and visible. Claims such as “always” mean durable local persistence plus eventual verified synchronization when the authorized remote is reachable, not a false promise that external services cannot fail.

## Error handling

Recoverable UI errors are non-modal and logged. Destructive cleanup is ownership-checked. All network responses are schema-validated and read back. Metadata mismatch preserves old values. State corruption restores the validated backup. Job logs retain the actual failing game and provider/process output. Shutdown flushes playtime and state but refuses to close while an owned install is active unless that operation is stopped.

## Verification

Use red-green tests for monitor selection, local-size measurement, transcript capture/failure summaries, and durable save/outbox semantics. Run the existing full self-test suite, XAML compilation, native UI proof, multi-monitor placement proof, install fixtures, backend CAS suites, build, checksum synchronization, cold relaunch, and post-relaunch state checks. Inspect the rendered dark and light interfaces and keyboard focus. Verify the exact final executable path and hash. Push source, tests, docs, and the downloadable release artifact only after all applicable gates pass.

