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

## 4. Constraints specific to this repo

- Stack is locked: C# 12, .NET 8, WPF + WinForms `NotifyIcon`, NAudio, xUnit.
- **No new NuGet packages:** `nuget.org` is not reachable from this environment, so all UI is hand-authored WPF with the existing `HandyTheme.xaml` design tokens.
- Gemini backend, `settings.json` schema, and `clips.json` history schema are untouched; all logic (coordinator, recorder, LlmClient, tray) stays as-is — only the UI layer changes.
