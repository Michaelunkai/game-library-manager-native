# Follow-through on the twelve remaining steps

This work resumes after r60 at the user's request. It does not change the original acceptance criteria or authorize cleanup while they fail.

**Final status:** r61b is deployed; every numbered item below was investigated, but not every original requirement is satisfiable from available evidence. The final result superseding the in-progress notes is `docs/acceptance-current.md`, with consolidated evidence in `evidence/criteria-r61b-final-acceptance.json`. Exact-target128regression/82WPF/5pause,12fallback/cadence and92helper checks pass. A six-hour service monitor is active. Jev was attempted and returned balance_exhausted HTTP402. No billing changes, live save restore, game launch or cleanup occurred.

1. Shared service: fresh GETs still return503 usage_exceeded at both API and root. Existing GitHub sign-in successfully opened Netlify: Free Legacy team is paused for exceeding Functions limits,218KSeptember requests displayed. Dashboard offers next-month restoration or paid upgrade; no payment method saved. No billing changes. Native healthy shared reads currently poll every2seconds, a plausible quota contributor. Implementing a60second healthy shared-read cadence plus credential-free canonical GitHub read-only fallback; Docker cadence remains independent and queued writes must remain queued. Receipt: evidence/r61-hosting-root-cause.json.
2. Seven installed durations: independent source/identity audit in progress; no invented values.
3. Catalog artwork and duration gaps: independent classification audit in progress.
4. Save detection: all576expected paths across128unresolved games are absent; bounded alternative-root search found no missed active campaign payload. Emulator directories are empty or contain metadata only. A retained Dying Light2save belongs to an uninstalled game and remains preserved. Evidence/r61-save-audit-findings.md records boundaries. Empty roots are not successful save coverage.
5. Backup/restore: retain existing isolated round-trip proofs; investigate concrete newly discovered payloads before live-state changes.
6. Campaign inputs: examine available evidence for Kristala/Outlaws; exact precision remains unsupported without a validated denominator/payload.
7. Pause/recovery: retain five controlled process-tree checks; no claim of testing every actual game.
8. Resource failures: take fresh finite process/commit observations; do not blame current high-memory consumers for an unattributed historical event.
9. Preservation/sync: new fallback tests must preserve pending edits, reject malformed responses and permit recovery without treating degraded reads as successful writes.
10. Final acceptance: stage, test and deploy only after the final source changes, then verify exact target and live behavior.
11. Cleanup: gate closed; preserve rollback and evidence.
12. Maintenance: add regression coverage for reproduced defects; do not represent finite tests as permanent guarantees.
