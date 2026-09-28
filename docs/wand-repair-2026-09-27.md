# Play with Wand repair and remaining acceptance

The deployed native package is `native\dist\GameLibrary.exe`, SHA-256
`87B42C719D2F2AABB8B76198322B7BE1C7F4829B28AD5230A9AAC3C5C793D9DB`.
The previous complete bundle is in
`evidence\reliability-20260927T134422Z\rollback\native-dist-before-wand-final-20260927T1807Z`.

## Reproduced defects and repairs

1. The production button check used a packaged supported-games manifest while
   the launcher and card list used current LevelDB registrations. The manifest
   contained 39 rows; 22 of the 45 current exact executable paths were absent.
   The button and launcher now use the same live registrations. The current
   deployed audit resolves all 45 button and protocol candidates.
2. A game observed before trainer confirmation was marked as a Wand play. A
   later Play with Wand click could merely focus that game. Early tracking now
   remains unconfirmed, and repeat clicks attempt attachment to the existing
   exact process. A cached Connected state is rechecked against fresh trainer
   state before a click is reduced to window activation.
3. A canonical card could choose one installed version for normal Play while
   another linked source was registered in Wand. The one-card projection keeps
   Wand eligibility when any linked source is registered and routes the Wand
   action to that exact source. The identity fixture covers this case.
4. Eleven live registrations previously projected the generic `Win64` folder
   instead of their installation roots. The LevelDB reader now uses a matched
   version path, validates containment and file identity, and preserves the
   previous good snapshot during transient read failures. The focused fixture
   first reproduced and then passed this case.
5. Overlay IPC and hook markers were previously treated as trainer attachment.
   They are diagnostic only. A read-only CDP probe for the inspected Wand
   12.58.0 build requires one expected game view, a `playing` trainer state,
   the expected game/trainer identities, and the exact game PID. Native code
   checks PID creation before and after inspection, pins the installed
   `app.asar` hash, and demotes a stale Connected state. Missing CDP, a changed
   Wand build, or ambiguous state remains unconfirmed.
6. The old standalone Wand verifier could stop broad same-name processes and
   call overlay activity a connection. It now performs a passive audit and
   exits with an explicit Not tested trainer result.

Historical `data\activity.log` contains 26 no-exact-process outcomes, 12
protocol no-op fallbacks, and 61 game-running-without-fresh-connection-evidence
entries. These counts overlap and are not a count of distinct affected games.
REPLACED and Nocturnal were reported by the user as working; REPLACED's old
log entry still lacked authoritative trainer evidence, illustrating the old
reporting gap. South of Midnight is not among the current 45 LevelDB
registrations, though its historical protocol sequence eventually observed an
exact game PID after CDP recovery.

## Verification and limits

The final package passed nine isolated PS5 verification suites at stage and
after deployment. The deployed self-test passed 137 checks, native UI fixture
86, native pause proof 5, and install-job proof 7. All 74 packaged files match
the staged hashes. The deployed read-only audit reports 45/45 current
registrations, exact launchers, tracked executables, catalog candidates, and
button paths, with `launchVerified=false`.

The normal live library UI was deliberately not reopened after the repair.
No real trainer attachment to a game process was tested for this binary.
The installed Wand 12.58.0 source supports repeated `wemod://play` as its
protocol replay signal; it ignores arbitrary URI nonce parameters. An already
running Wand instance without its local CDP endpoint can still leave a
protocol no-op unconfirmed. No duplicate or unmodified game is launched as an
automatic fallback. Wand compatibility for future games/builds cannot be
established without their actual registrations, supported trainer builds, and
game-bound runtime proof.

See `evidence\reliability-20260927T134422Z\acceptance.md` and the matching
JSON ledger for the exact status of every original requirement.
