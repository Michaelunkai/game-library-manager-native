# interface-design/system.md

Direction: **Precision & Density**. WPF resource dictionaries are the component
layer; `Theme.xaml` is the token layer. No Tailwind, no shadcn, no shadcn-*
registry equivalents exist for XAML, so the WPF equivalents are used and named
here instead.

## Tokens

See `DESIGN.md` at the repo root. Canonical key names live in `native\Theme.xaml`.

## Components

| Component | Source | Key / name | Spec |
|---|---|---|---|
| Button | in-repo WPF style | `Theme.xaml` default `Button` | 6px radius, 4px vertical padding, 1px `stroke` border |
| Primary button | in-repo WPF style | `PrimaryButton` | accent fill, `accentInk` foreground. **At most one per view.** |
| Destructive button | in-repo WPF style | inline, `danger` fill | filled red, white ink, **confirmation required** |
| Card | in-repo WPF `Border` | the `DataTemplate` root | 6px radius, `panel` fill, 1px `stroke` |
| Cover thumbnail | in-repo WPF `Image` | `CoverConverter` | fixed box, `ClipToBounds`, cached and frozen |
| Toolbar | in-repo `WrapPanel` | row 3 | wraps rather than clips |
| Card action column | in-repo `UniformGrid` + `StackPanel` | per-card template | 1.15* width, 186px floor, secondary actions two-up |

Hand-built and why no registry equivalent exists: every component above. The
`front` skill's registry path (shadcn/ui, 21st.dev) emits React + Tailwind class
names and has no XAML/WPF target; installing it would produce a second, parallel
design system inside the same repo, which is the exact slop the skill exists to
prevent.

## Patterns

- **Density over decoration.** The card's five action rows are the budget. A
  control that does not earn its row is cut, not shrunk.
- **One memorable element.** The mint accent. Everything else is graphite.
- **No motion on scroll.** The list recycles; anything animating during a
  recycle is a frame-drop source, not a delight.
- **Motion answers actions only.** A confirm dialog fades, a press depresses.
  Nothing moves on its own.
- **Reduced motion** is respected by construction: there is no ambient
  animation, so `prefers-reduced-motion` has nothing to disable.

## Density rules

- Body text never below 11px; metadata 12px.
- Vertical rhythm only on 4/8.
- Card action buttons: 8px horizontal / 4px vertical padding minimum.
