# UI/UX research — Voice IME for Windows redesign

Source of truth for the redesign decisions in `docs/DESIGN.md`.

## 1. Windows 11 / Fluent Design (Microsoft)

[Windows UI guidelines — Fluent Design for WinUI 3, WPF, .NET MAUI](https://skills.rest/skill/windows-ui-guidelines) and [Microsoft's guidelines for app settings](https://github.com/MicrosoftDocs/windows-dev-docs/blob/docs/hub/apps/design/app-settings/guidelines-for-app-settings.md):

- **Follow standard controls, minimize custom UI, reduce visual noise.** Prefer platform-familiar patterns over invented ones so the app feels like it belongs on Windows 11.
- **Navigation:** left-side `NavigationView`-style rail is the canonical pattern for multi-section apps. The app's settings entry belongs at the bottom of the navigation list, pinned below a divider (Windows 11 Settings convention).
- **Settings pages:** open full window size, scrollable layout with a **constrained max width (~1000–1100 px)** so long lines stay readable on wide displays; single-column layout; group related settings under section headers (BodyStrong style); present content top-to-bottom.
- **Controls:** toggle switch for binary (on/off) settings; radio buttons for up to 5 mutually exclusive options; settings take effect immediately when changed.
- **Visual language:** Segoe UI Variable type system, Fluent color ramps with an accent color, layered surfaces (base + card elevations), 4–8 px corner radii, subtle elevation via strokes rather than heavy shadows.

## 2. Tray / notification area (Win32 UX guidance)

[Notifications and the Notification Area — Win32 apps](https://learn.microsoft.com/en-us/windows/win32/shell/notification-area) and [Notification Area design concepts](https://learn.microsoft.com/en-us/windows/win32/uxguide/winenv-notification):

- **One icon per component**, high-DPI aware (16×16 and 32×32 versions); provide a tooltip.
- **Leave users in control:** they decide whether the icon is always visible; the app cannot force promotion out of the overflow area. Provide an explicit way to show/hide the icon and an explicit Exit command.
- **Left-click must do something.** A tray icon that shows nothing on click feels broken — left double-click should open the app's main surface (here: the settings shell); right-click shows the context menu.
- **Status changes should be subtle.** Don't flash or rapidly animate the tray icon; brief subtle indicators are OK for transient activity. Tooltip text reflects current state (idle / recording / uploading / error).
- **Balloon tips/toasts are supplementary.** Never build a workflow that assumes a toast arrived; mandatory info must also be available on-screen (tray tooltip + in-window status + history).
- Close-with-× should **hide to tray**, not quit; Quit exists only on the tray menu. Single instance enforced via mutex; a second instance focuses the running one.

## 3. Modern WPF desktop patterns

- Single window + content swap (`ContentControl` section host) suits a settings shell; keep XAML thin, logic in code-behind/viewmodels.
- Theme awareness: light/dark palettes swapped at runtime through `DynamicResource` brushes; follow the OS theme by default.
- Accessibility: keyboard-navigable controls, visible focus indicators, `AutomationProperties.Name` on icon-only buttons, tooltips, scalable text (no hardcoded sizes where the system scales them).

## 5. Dark mode best practices (web research)

[Dark Mode Design Guide: Color, Type, A11y (2026)](https://mantlr.com/blog/dark-mode-design-guide-color-typography-accessibility),
[Dark Mode Color Palettes: Complete Guide for 2025](https://mypalettetool.com/blog/dark-mode-color-palettes),
[Dark Mode Color Schemes: Complete Design Guide 2026](https://colorpick.app/blog/dark-mode-color-schemes):

- **Never pure black (`#000000`) as a dark surface.** Causes eye strain and
  OLED "smearing" when scrolling. Use near-black dark gray (we use `#2C2B29`,
  matching Windows 11 app-dark background). Both Material Design and Apple HIG
  recommend this.
- **Never pure white (`#FFFFFF`) as dark text.** Causes halation/glare against
  dark backgrounds. Use off-white (we use `#E8E8E8`); retains high contrast
  (~14:1 on our base) while reducing strain.
- **Desaturate and lighten accent colors for dark mode.** Fully saturated
  brand colors that work on white "glow" or vibrate against dark surfaces.
  Reduce saturation 10–20%, increase lightness 5–10% (we shift brand pink
  `#DA5893` → `#E08FB8` on dark).
- **Surface elevation via value gaps, not shadows.** In light mode shadows
  create depth; on dark backgrounds shadows are ineffective. Surfaces layer
  with progressively lighter steps (Background → Surface → SurfaceElevated).
- **Text hierarchy:** primary ~87% white opacity, secondary ~60%, disabled
  ~38% on dark surfaces.
- **Contrast targets:** primary text ≥ 7:1 (ours ~14:1), secondary ≥ 4.5:1
  (ours ~7.6:1), disabled ≥ 3:1 (ours ~3.5:1), UI components ≥ 3:1.
- **Respect the OS theme first.** Read `AppsUseLightTheme` from the Windows
  registry at startup, follow it by default, and offer a manual light/dark/
  system override persisted in settings (`ThemeManager.ResolveTheme`).
- **Use `DynamicResource` (not `StaticResource`)** for all theme brushes so
  live theme switching updates the UI without restart. WPF reads the registry
  value once at read time; `DynamicResource` is what keeps the app reactive.

## 6. WPF-specific theming notes

[What's new in WPF for .NET 9 — Fluent theme](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net90):
- WPF .NET 9 includes a Fluent theme with integrated light/dark modes and
  system accent color support (`SystemColors.AccentColorBrushKey`).
- We target .NET 8 without the Fluent package, so we hand-author the token
  system. `SystemColors` via `DynamicResource` could be added later to track
  the user's Windows accent color at runtime.
- `DynamicResource` (not direct `SystemColors.XColor` reads) is required for
  runtime color tracking — direct reads bake in the value at read time.

## 7. Constraints specific to this repo

- Stack is locked: C# 12, .NET 8, WPF + WinForms `NotifyIcon`, NAudio, xUnit.
- **No new NuGet packages:** `nuget.org` is not reachable from this environment, so all UI is hand-authored WPF with the existing `HandyTheme.xaml` design tokens.
- Gemini backend, `settings.json` schema, and `clips.json` history schema are untouched; all logic (coordinator, recorder, LlmClient, tray) stays as-is — only the UI layer changes.
