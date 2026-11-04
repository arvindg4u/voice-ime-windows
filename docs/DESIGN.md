# Voice IME for Windows — UI/UX redesign spec

Status: proposed design for the settings shell, overlay, and tray surface.
Backend (dictation pipeline, coordinator, tray lifecycle) is intentionally untouched.

## 1. Goals

- A modern, native-feeling Windows 11 app: Fluent-inspired surfaces, Segoe UI type scale,
  light/dark themes that follow the OS, subtle elevation, smooth micro-interactions.
- Tray-first UX preserved: app starts hidden in the tray; the settings window opens on
  demand; × hides to tray; Quit only on the tray menu.
- One settings shell with a home dashboard: users see state (idle/recording/uploading/error),
  their hotkey, and their last transcript at a glance, then drill into sections.
- Accessible by default: keyboard navigation, visible focus, tooltips, screen-reader names
  on icon buttons, high-contrast-friendly tokens.
- Zero new dependencies: pure WPF + WinForms, extending the existing `HandyTheme.xaml`
  design-token contract.

## 2. Visual language (design tokens)

All colors are `DynamicResource` keys in `Theme/HandyTheme.xaml`, swapped live by
`ThemeManager.ApplyTheme`. The system is a **single semantic token map** applied
to two palettes (light / dark) — not two independent stylesheets.

### Color palette

| Token | Light | Dark | Contrast (dark) | Use |
| --- | --- | --- | --- | --- |
| `HandyBackground` | #FBFBFB | #2C2B29 | — | Window/page base canvas |
| `HandySurface` | #FFFFFF | #3A3937 | — | Cards, elevated containers |
| `HandySurfaceElevated` | #F8F8F8 | #454442 | — | Modals, dropdowns, flyouts |
| `HandyText` | #0F0F0F | #E8E8E8 | ~14:1 | Primary body text |
| `HandyTextSecondary` | #5F5F5F | #A8A7A4 | ~7.6:1 | Helper / caption text |
| `HandyTextDisabled` | #C7C7C7 | #6E6D6B | ~3.5:1 | Disabled text |
| `HandyAccent` | #DA5893 | #E08FB8 | — | Brand accent, primary actions |
| `HandyAccentHover` | #C44E7F | #D67AA8 | — | Accent under pointer |
| `HandyAccentActive` | #AE4372 | #C86A94 | — | Accent when pressed |
| `HandyAccentContainer` | #FDE9F0 | #3B2D32 | — | Tinted fill behind accent |
| `HandyBorder` | #E5E5E5 | #3A3937 | — | Input borders, card borders |
| `HandyDivider` | #E5E5E5 | #3A3937 | — | Hairline section dividers |
| `HandyError` | #DC2626 | #F87171 | — | Error text / borders |
| `HandyWarning` | #D97706 | #FBBF24 | — | Warning text / borders |
| `HandyFocusBorder` | #DA5893 | #E08FB8 | — | Keyboard focus ring |
| `HandyInkDisabled` | #C7C7C7 | #6E6D6B | — | Disabled icons / button glyphs |
| `HandyHoverBackground` | #EFEFEF | #383735 | — | Subtle hover fill |
| `HandyActiveBackground` | #FDE9F0 | #3B2D32 | — | Active / selected fill |
| `HandyLogoPrimary` | #FAA2CA | #F28CBB | — | Active nav rail fill |
| `HandyFontFamily` | \multicolumn{4}{c}{Segoe UI, system fallback} |

### Dark-mode design rules (applied)

- **No pure black surfaces:** dark base is `#2C2B29` (dark gray, not `#000000`)
  — avoids eye strain and OLED smearing.
- **No pure white text:** dark text is `#E8E8E8` (off-white) — reduces glare
  while retaining ~14:1 contrast on the dark base.
- **Accent adjusted for dark:** brand pink `#DA5893` is desaturated and
  lightened to `#E08FB8` on dark so it reads as a calm tint, not neon.
- **Surface elevation via value gaps:** `Background → Surface → SurfaceElevated`
  get progressively lighter on dark, so depth reads without shadows.
- **Contrast targets:** primary text ≥ 7:1, secondary ≥ 4.5:1, disabled ≥ 3:1,
  UI components ≥ 3:1 against their background. All dark pairings exceed these.

### Type scale

- Page title: 20 px, SemiBold
- Section label: 13 px, SemiBold
- Body: 13 px
- Caption/helper: 12 px, Medium gray
- Small: 11 px
- Numbers/timers: Consolas (tabular)

### Shapes & motion

- Cards: 8 px corner radius, 1 px border, subtle elevation via surface layering.
- Buttons: 8 px radius, min height 36 px, hover background + accent border,
  pressed state (accent → accent-active).
- Toggle: 40 × 22 px Windows-style toggle switch with knob translate animation.
- Nav rail items: 44 px height, 10 px radius; active item = `HandyLogoPrimary` @80%,
  hover = `HandyHoverBackground`.
- Transitions: 200–350 ms fades/slides; no layout-thrashing animations.

## 3. Shell (`MainWindow`)

- **Window:** 900 × 700, min 700 × 560, center screen, resizable, default chrome.
- **Left rail (240 px):** app mark ("🎙️ Voice IME" + tiny tagline), nav items with icon +
  label, divider, then **About pinned at the bottom** (Windows 11 Settings convention).
- **Content:** single scrollable column, max-width 1080 px centered for readability,
  section cards, 24 px padding.
- **Footer:** full-width hairline; left = live tray status; right = version + update note.
- **First-run hint:** dismissible info bar (accent left border, "Open settings" / "Dismiss")
  shown until `SeenHint`; unchanged behavior.
- Nav sections (order): **Dashboard → General → Gemini → History → Advanced** | divider | **About**.
- Default landing: **Dashboard**.

## 4. Screens

### Dashboard (home)

- Page title "Voice IME" + tagline "Press <hotkey> to dictate — in any app."
- **Status card:** status dot chip + plain-language state ("Idle — ready to dictate"),
  hotkey hint, mic/device line.
- **Quick tiles:** General (hotkey & sound), Gemini (API keys, model, prompt), History
  (transcripts), Advanced (behavior), About — one tap to each section.
- **Recent transcript card:** latest non-empty transcript with time, copy button, and a
  "re-dictate" hint when the last entry failed.

### General, Gemini, History, Advanced, About

- Keep all existing behavior and settings fields (hotkey capture, activation mode,
  devices, toggles, API keys, model, prompt library + Test, history list, autostart,
  tray icon, overlay modes, paste method, theme, language).
- Restyled: page title, grouped cards, `HandyToggleSwitch`, inline validation messages,
  primary/success button states, copy feedback ("Copied ✓"), char counters.
- Immediate apply: toggles/dropdowns write through to the shared store on change (existing
  behavior preserved).

### Overlay (`OverlayWindow`)

- Contract unchanged (`Show`, `Hide`, `SetLevel`, `SetPreview`, `WorkAreaProvider`).
- 256 × 56 pill, bottom-center above taskbar, 40 px offset, topmost, no taskbar entry,
  non-focusable.
- Recording: pink pulsing dot, live waveform bars driven by mic level, elapsed timer.
- Uploading: spinner + "Sending…", timer keeps running.
- Error: red dot + message, auto-hide after 3 s.
- Preview: live transcription text slides into the center while recording.

## 5. Interaction & accessibility

- Every setting that changes takes effect immediately; saves are silent (status line
  "Saved ✓" where useful).
- Hotkey capture: chip click → capture mode, commit on key-up, click-away cancels,
  roll back + inline error on registration failure (unchanged logic).
- Keyboard: Tab walks the rail and all controls; `Esc` cancels capture/dialogs;
  icon-only buttons carry `AutomationProperties.Name`.
- Focus: visible accent ring on focused interactive elements (`HandyFocusBorder`).
- No color-only information: status uses dot color **and** text; errors use icon color
  **and** a message.

## 6. What is NOT changing

- `LlmClient`, `DictationCoordinator`, `AudioRecorder`, `NativeInput`, `SettingsStore`,
  `ClipboardStore`, `TrayMenu`, `Notifier`, hotkey/tray logic.
- Settings schema field names (`baseUrl`, `model`, `customPrompt`, `keyCursor`,
  `apiKeysProtected`, `hotkey`, `activationMode`, …).
- Tray menu order and icons.
- All public/internal seams used by xUnit tests (view `x:Name` fields, internal
  properties/methods, `MainWindow.FindName` targets `SectionHost`,
  `FirstRunHintText/Buttons`, `App.CreateMainWindow` overloads).
