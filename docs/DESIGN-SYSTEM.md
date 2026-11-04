# Voice IME — Design System

The single reference for colour, spacing, type, and interaction states in this app.
If a value is not in this document, it should not be in the UI.

---

## 1. Layers

Three layers, and they must not collapse into each other:

| Layer | Lives in | Example | Rule |
|---|---|---|---|
| **Primitive** | nowhere — it is a single hex value | brand pink `#DA5893` | Never referenced by a view. |
| **Semantic** | `src/VoiceIme/Theme/Palette.cs` → brush keys in `HandyTheme.xaml` | `AccentFill`, `TextMuted`, `FocusRing` | The only layer views may reference. |
| **Component** | control templates in `HandyTheme.xaml` | `PrimaryButton`, `InputComboBox` | May reference semantic tokens only. |

Naming follows the role/emphasis convention used by Fluent 2 and Material 3:
`{Role}{Emphasis}` — `SurfaceCard`, `TextSecondary`, `AccentFill`, `ErrorText`.
`On*` means "content drawn on top of that fill" (`OnAccent`).

---

## 2. Colour tokens

Every token is defined **in both palettes**. A key present in one and missing from the
other leaves stale colour on screen after a live theme switch — `ThemeContrastTests`
fails if the key sets differ.

### Surfaces (elevation ladder)

| Token | Light | Dark | Use |
|---|---|---|---|
| `SurfaceCanvas` | `#FAFAFA` | `#1B1A19` | Window background |
| `SurfaceCard` | `#FFFFFF` | `#232221` | Cards, panels |
| `SurfaceSunken` | `#F4F4F5` | `#151413` | Inputs, wells, secondary buttons |
| `SurfaceHover` | `#F1F1F3` | `#2E2D2B` | Row / nav hover |
| `SurfaceSelected` | `#FCE7F1` | `#3A2831` | Selected row, active nav |
| `SurfaceOverlay` | `#FFFFFF` | `#2A2927` | Floating dictation pill |

Dark mode is **not** an inversion: the canvas is a warm near-black (never `#000000`),
and elevation steps *up* in lightness. Pure black plus pure white text causes halation.

### Text

| Token | Light | Dark | Minimum |
|---|---|---|---|
| `TextPrimary` | `#1A1A1A` | `#F2F1EF` | AAA (7:1) on every surface |
| `TextSecondary` | `#5A5A5A` | `#BAB8B4` | AA (4.5:1) |
| `TextMuted` | `#68686A` | `#9A9894` | AA (4.5:1) |
| `TextDisabled` | `#868688` | `#807E7A` | 3:1 (held deliberately) |

Dark text is `#F2F1EF`, not `#FFFFFF` — full white on a dark surface blurs.

### Accent

| Token | Light | Dark |
|---|---|---|
| `AccentFill` | `#DA5893` | `#E08BB5` |
| `AccentFillHover` | `#CE4F89` | `#E89CC0` |
| `AccentFillPressed` | `#CD538A` | `#D4799F` |
| `OnAccent` | `#1A1114` | `#1A1114` |
| `AccentInk` | `#A63A6A` | `#F0A6C8` |
| `AccentSubtle` | `#FCE7F1` | `#3A2831` |

**The one surprising rule:** the brand pink carries **dark ink**, not white.
White on `#DA5893` measures **3.62:1** and fails WCAG AA; near-black ink measures
**5.11:1**. This is why `OnAccent` exists and why no template hardcodes `White`.
Dark mode lightens the accent to `#E08BB5` so the pink does not vibrate against a
dark surface.

### Lines and focus

| Token | Light | Dark | Notes |
|---|---|---|---|
| `BorderSubtle` | `#E6E6E8` | `#35332F` | Decorative only — never the sole boundary of an input |
| `BorderStrong` | `#86868B` | `#78766F` | Control boundaries — must reach 3:1 (WCAG 1.4.11) |
| `FocusRing` | `#A63A6A` | `#F0A6C8` | Keyboard focus — 2px, always visible |

`BorderSubtle` is intentionally *not* 3:1. It separates cards where the fill already
does the work. Anything whose boundary is the only affordance (a text field, a toggle
track) uses `BorderStrong`.

### Status

Each status has three tokens: text on neutral surfaces, a tinted container, and ink
for that container.

| | Light text / surface / on | Dark text / surface / on |
|---|---|---|
| Error | `#B3261E` / `#FCEBEA` / `#FFFFFF` | `#FF8A80` / `#3A2422` / `#2A0F0D` |
| Warning | `#8A5300` / `#FFF4E0` / `#FFFFFF` | `#FFC24D` / `#3A2E14` / `#2A2008` |
| Success | `#1E6E3C` / `#E6F4EA` / `#FFFFFF` | `#6FD08C` / `#1E3324` / `#0C2413` |

**Never convey status by colour alone** (WCAG 1.4.1). Every status surface pairs its
tone with a glyph — `StatusSeverity` in `MainWindow` drives colour *and* icon.

---

## 3. Contrast contract

`ThemeContrastTests` enforces 51 foreground/background rules per theme against
WCAG 2.2. All pass in both themes. The rules are enumerated in
`ContrastContract.Build()` — add a rule there when you add a visual state.

Thresholds used:

| Requirement | Ratio |
|---|---|
| Body text (AA) | 4.5:1 |
| Body text (AAA, applied to `TextPrimary`) | 7:1 |
| Large text (AA) | 3:1 |
| UI component / focus indicator (1.4.11) | 3:1 |

Measured values are computed on the **composited** result
(`Contrast.Composite`), because tokens with alpha are what the user actually sees.
The old 20%-alpha border measured 1.25:1 against its own card for exactly this reason.

---

## 4. Non-colour tokens

Defined in `DesignTokens.cs`, following the Windows 11 / Fluent baseline.

**Spacing** (4px base): `4, 8, 12, 16, 24, 32`.

**Radii** (Windows 11 uses exactly these three): `4` inputs and chips, `8` cards and
buttons, `12` dialogs and the floating pill.

**Metrics:** control height `32`; primary action `40`; minimum pointer target `44`;
settings row `56`; sidebar `208`; content max width `720`.

**Type ramp:** caption `12`, body `13`, body-strong `14`, title `20`, display `28`.
Font is Segoe UI Variable with a Segoe UI fallback. Emphasis is **Semibold**, never
italic (readability) and never Bold (not in the Windows ramp).

---

## 5. Interaction states

Every focusable control defines: rest, hover, pressed, focus, disabled.

- **Focus** is a 2px `FocusRing` ring outside the control (`FocusVisualRing`). It is
  never `{x:Null}` except on inputs, which draw their own 2px focus border.
- **Hover** changes background (`SurfaceHover`) *and* foreground where needed — never
  opacity alone.
- **Pressed** uses the pressed token or a darker surface.
- **Disabled** uses 50% opacity plus `TextDisabled` where text is involved.

---

## 6. Theme switching

`ThemeManager.ApplyTheme` swaps a `ResourceDictionary` in
`Application.Resources.MergedDictionaries` — appended last so it wins lookup, with the
previous dictionary removed. Every consumer uses `DynamicResource`; `StaticResource`
resolves once and will not repaint.

Two rules follow from that:

1. **Colour → `DynamicResource`. Always.**
2. **Metrics, radii, font sizes → may be `StaticResource` or literals from
   `DesignTokens`** — they do not change with theme.

**High contrast.** When a Windows contrast theme is active, `ThemeManager` installs a
palette built entirely from `SystemColors` and stops painting its own colours. Status
tokens collapse to `WindowTextColor`, because inventing a red in high contrast defeats
the point of the contrast theme. Detail is carried by text and icons instead.

---

## 7. Guards

| Test | Prevents |
|---|---|
| `ThemeContrastTests` | Any token or pair dropping below WCAG AA |
| `BothPalettes_DefineExactlyTheSameKeys` | Stale colours after a live theme switch |
| `NoHardcodedHexColoursInXaml` | Literal colours outside the palette |
| `NoHardcodedNamedColoursInXaml` | `White`/`Black` creeping back into templates |
| `LegacyAliasCountOnlyShrinks` | New code depending on the old `Handy*` keys |

`Palette.LegacyAliases` still maps the ten original `Handy*` keys onto semantic tokens
so nothing is left unthemeable during migration. That list only shrinks.

---

## 8. Migrating a view

1. Replace `HandyText` → `TextPrimary`, `HandyMidGray` → `TextMuted`,
   `HandyCardBackground` → `SurfaceCard`, `HandyCardBorder` → `BorderSubtle`,
   `HandyAccent` → `AccentFill`, `HandyBackground` → `SurfaceCanvas`.
2. Replace control styles: `HandyPrimaryButton` → `PrimaryButton`,
   `HandySecondaryButton` → `SecondaryButton`, `HandyTextBox` → `InputTextBox`,
   `HandyToggle` → `ToggleSwitch`, `HandySlider` → `AccentSlider`.
3. Every `ComboBox` gets `Style="{DynamicResource InputComboBox}"` — the default WPF
   template is light-only and stays white in dark mode.
4. Add `AutomationProperties.Name` to anything without a visible text label.