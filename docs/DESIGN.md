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
`ThemeManager.ApplyTheme`.

| Token | Light | Dark | Use |
| --- | --- | --- | --- |
| `HandyText` | #0F0F0F | #FBFBFB | Body text |
| `HandyTextSecondary` | #5F5F5F | #A8A7A4 | Helper / secondary text |
| `HandyBackground` | #FBFBFB | #2C2B29 | Window base |
| `HandyCardBackground` | #FFFFFF | #373634 | Cards / surfaces |
| `HandyCardBorder` | #808080 @20% | #808080 @20% | Card hairlines |
| `HandyAccent` | #DA5893 | #DA5893 | Brand accent, primary actions |
| `HandyLogoPrimary` | #FAA2CA | #F28CBB | Active nav fill |
| `HandyHoverBackground` | #EFEFEF | #383735 | Hover states |
| `HandyActiveBackground` | #FDE9F0 | #3B2D32 | Active nav fill (tint) |
| `HandyDivider` | #E5E5E5 | #3A3937 | Hairline dividers |
| `HandyError` | #DC2626 | #F87171 | Error text / borders |
| `HandyWarning` | #D97706 | #FBBF24 | Warning text |
| `HandyFontFamily` | \multicolumn{3}{c}{Segoe UI, system fallback} |

### Type scale

- Page title: 20 px, SemiBold
- Section label: 13 px, SemiBold
- Body: 13 px
- Caption/helper: 12 px, Medium gray
- Small: 11 px
- Numbers/timers: Consolas (tabular)

### Shapes & motion

- Cards: 8 px corner radius, 1 px border, subtle drop-shadow on elevated surfaces.
- Buttons: 8 px radius, min height 36 px, hover opacity/scale, pressed state.
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
