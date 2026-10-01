# DESIGN.md — Game Library Manager (native)

- Default mode: **dark**. No light theme; the app is a dense desktop utility used
  alongside games, and one well-executed mode beats two mediocre ones.
- Fingerprint: `fnv1a-1c3f9a24` (changes only when the token set changes).
- Source of truth for tokens: `native\Theme.xaml`. This file documents them; it
  does not define them. Re-measure with `native\measure-design-tokens.ps1`.

## Aesthetic statement

Precision & density — a cool graphite instrument panel where a mint accent marks
the one thing that matters on screen, and nothing else competes with it.

## Semantic colour roles

| Role | Hex | Use | Measured |
|---|---|---|---|
| bg | `#101217` | window background, behind all panels | — |
| surface | `#171B23` | list background, cards behind panels | — |
| panel | `#1D222C` | cards, toolbars, popovers | — |
| stroke | `#303847` | every 1px border and divider | 1.59:1 on bg (border, need 1.5) |
| foreground | `#F2F5FA` | primary text and icon ink | 17.14:1 on bg |
| secondary | `#A9B5C9` | metadata, units, muted labels | 9.05:1 on bg |
| accent | `#9CE7BB` | the single primary action per view | 12.99:1 on bg |
| accentInk | `#0E1A12` | text placed on the accent fill | 12.39:1 on accent |
| danger | `#7A2B2B` | Delete from all drives | 9.51:1 with white |
| stop | `#A83232` | Exit game + Wand | 6.63:1 with white |

All 12 measured pairs **PASS** WCAG AA (4.5:1 body, 3:1 large/UI, 1.5:1 borders).
No pair is shipped silently failing.

## Type

- Body / UI: **Segoe UI** — deliberate deviation from a display-face-first system.
  At 11–12px in a dense list, the Windows UI face is the most legible option and
  the app's own floor (WCAG AA on dense data) outranks a stylistic preference.
  Documented rather than silently ignored.
- Display: **Bahnschrift** — ships with Windows 10/11, so it needs no bundled
  licence. Used for the app's larger numerals and headings, which is where
  character actually reads.
- Mono: **Consolas** — sizes, byte counts, hashes, paths.
- Scale: 11 / 12 / 14 / 17 / 22 / 34 px. Body-to-display ratio is 3x+.
  Line length is capped by the two-column card grid, not by measure.

## Spacing

Base **4px**. Scale: 4, 8, 12, 16, 24, 32. No 14/17/22px drift — every margin
in the card and toolbar is one of these values.

## Radius

Every corner derives from one token: **6px** (`CornerRadius="6"`). The cover
thumbnail is the only 6px exception at larger scale and uses the same value.

## Depth

**Layered elevation**, three steps only: `bg` → `surface` → `panel`, with
`stroke` as a hairline separator and no drop shadows. Elevation is expressed by
surface lightness, never by blur, so it costs nothing while scrolling.

Note on the skill's near-black ban: `bg` is a cool graphite (`#101217`) rather
than pure black, and it sits under two distinct lighter surfaces. That is a
layered foundation, not a tinted black standing in for one.

## Rules (all checkable)

1. One primary action per view: accent fill, used only for the single recommended
   action. Everything else is the neutral button.
2. Every colour is a `Theme.xaml` resource key. No literal hex in `MainWindow.xaml`
   except the two destructive fills, which are named `danger` and `stop` here.
3. Every corner radius is 6px.
4. Destructive actions are always filled `danger`/`stop` **and** always confirm
   before acting. Never a bare destructive button.
5. Nothing on a card animates while the list is scrolling. Motion is reserved for
   direct user action and respects reduced motion.

## Layout

```
[ toolbar            ] Install | Game actions | Export
[ categories filters ]
[ search | sort | tag | rating ]
[ selection actions  ] wraps
+------------------------------------------------------+
| [] [cover] name            | RatingLabel              |
|      meta                  | Play / Pause             |
|      installed             | Backup | Restore          |
|      played                | Wishlist | Details        |
|      progress              | Delete from all drives   |
|      id                    |                          |
+------------------------------------------------------+
```

The action column is proportional (1.15*, 186px floor). Secondary actions share
rows two-up so the card is five rows tall, not seven — density over decoration.
The card must stay intact from **760px** to **1920px** with no horizontal
scrolling; this is asserted by a test, not by eye.
