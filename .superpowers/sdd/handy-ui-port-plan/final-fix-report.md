# F1/F2 fix report — shared store instances (`feat/handy-ui-port`)

Fixes the two merge-blocking findings from `final-review.md` (§F1, §F2).
F3/F4 and the 17 deferred minors are untouched (accepted follow-ups / leave-as-is).

`dotnet` is unavailable on this host, so this was careful authoring only —
windows-latest CI (`dotnet test`) must be green before merge, same gate as the review.

## What was wrong

- **F1 (CRITICAL):** `App` and the section views each held a `SettingsStore`
  loaded from the same `settings.json`. Dictation reads `App._settings`, so
  in-UI Gemini edits were ignored until restart, and App's post-transcribe
  `KeyCursor` save persisted its stale `ApiKeys` over keys the user just saved
  (data loss).
- **F2 (IMPORTANT):** same divergence for `ClipboardStore` — the History view
  rendered a private snapshot and its pin/delete `Save()`s could clobber
  transcripts dictated after the window opened. `OpenHistory()` was dead code:
  the Task 7 tray restructure dropped the pre-branch `History…` row, leaving
  the plan's Task 5 line ("App opens the main window to the History section")
  with no live caller.

## The fix (one shared-instance change covering both)

Production (`src/VoiceIme/`):

- `App.xaml.cs`
  - `CreateMainWindow()` builds the shell with section views bound to the
    live stores: `GeneralSettingsView(_settings, registrar, …)`,
    `GeminiSettingsView(_settings)`, `HistorySettingsView(_clips)`.
    Static overload `CreateMainWindow(settings, clips, registrar,
    listMicrophones)` holds the construction so wiring tests can drive it
    without instantiating the singleton `Application`.
  - `OpenSettings()` / `OpenHistory()` both go through `CreateMainWindow()`
    + `AttachLiveHotkey()` + `RefreshSectionViews()` + `NavigateTo()` +
    `ShowMainWindow()`. `OpenHistory` navigates to `MainSection.History`
    (public `NavigateTo` supports it) and is now reachable from the tray —
    re-wire chosen over delete.
  - `RefreshSectionViews(window, settings, clips)` (static, explicit stores):
    History rebinds to the live clips, General/Gemini fields reload from the
    live settings. Idempotent; null-window is a no-op. Called on every open
    so transcripts dictated while hidden appear and any externally-reset
    fields repaint.
  - Tray tooltip: `TrayText(hotkey)` / `TruncateTrayText` (63-char WinForms
    cap) / `RefreshTrayText()` re-reading `_settings.Hotkey`. `GeneralSettingsView`
    raises `Saved` after every successful persist; `AttachLiveHotkey`
    subscribes via the `RefreshTrayTextFromSave` adapter (the event carries
    the store, the tooltip re-reads the live one). Attach is idempotent
    (`-=` before `+=`) and no longer registrar-gated, so tooltip refresh
    works even before the hotkey window exists.
  - `LiveSettings` / `LiveClips` internals name the live stores the shell
    must bind to; `TrayMenuAction.History` is translated to an
    `OpenHistory()` menu item.
- `TrayMenu.cs`: new `History` action + `HistoryLabel`; idle order
  `version | copy | history | settings | quit`, busy inserts Cancel after
  the header.
- `Views/GeneralSettingsView.xaml.cs`: `BoundSettings` identity seam,
  `Saved` event (raised only on successful save), `ReloadFromSettings()`.
- `Views/GeminiSettingsView.xaml.cs`: `BoundSettings` seam, `ReloadFromSettings()`.
- `Views/HistorySettingsView.xaml.cs`: `BoundStore` seam (`BindStore` unchanged).
- Parameterless view ctors are kept as standalone/test fallbacks; they are no
  longer used by any production path.

Tests (`tests/VoiceIme.Tests/`):

- New `AppWiringTests.cs`: shell binds live settings to General+Gemini
  (`Assert.Same`), live clips to History with pre-existing transcripts
  rendering; view save mutates the live settings instance and raises `Saved`;
  `RefreshSectionViews` pulls external hotkey/model/transcript changes into
  an already-built shell; null-window no-op is UI-free; `TrayText` format
  and 63-char truncation are OS-agnostic.
- `GeneralSettingsViewTests`: injected-store identity, `Saved` raised on
  success / not on validation failure, `ReloadFromSettings` refreshes the chip.
- `GeminiSettingsViewTests`: injected-store identity, `ReloadFromSettings`
  refreshes Base URL/model fields.
- `HistorySettingsViewTests`: `BoundStore` is the injected instance and its
  adds render (existing `BindStore_SwitchesToLiveStore` test kept).
- `TrayMenuTests`: idle/busy order arrays updated for the History row; new
  History-always-enabled test.
- All new WPF tests follow the existing headless-vacuous STA pattern
  (pass vacuously without a window station; full assertions on windows-latest CI).

## Divergence re-verification (F1/F2 grep)

Remaining `SettingsStore.Load` / `new SettingsStore()` / `new ClipboardStore()`
hits after the fix:

| Hit | Verdict |
|---|---|
| `App` field init (`= SettingsStore.Load()`) + `OnStartup` reload | Pre-existing double-load, benign per review non-findings |
| Parameterless view ctors (`General`/`Gemini` `: this(SettingsStore.Load())`, `History` `: this(new ClipboardStore())`) | Standalone/test fallback only; no production path uses them |
| `SettingsStore.Load()` body (`new SettingsStore()` — the loader itself) | Self-construction, not divergence |
| Gemini Reset defaults (`new SettingsStore()` for default Base URL/model/prompt) | In-memory defaults template, never stored |

No production path constructs a store the views or dictation diverge on:
`CreateMainWindow` is the single shell builder, and `RefreshSectionViews`
keeps shown views in sync.

## F2 live-code confirmation

The plan's Task 5 line now has live code behind it: `OpenHistory()` builds
(or reuses) the shared-store shell, re-syncs views, navigates to
`MainSection.History`, and shows the window — and it is reachable from the
tray `History…` row (`TrayMenuAction.History` → `TranslateMenuItem` →
`OpenHistory()`), restoring the pre-branch entry point Task 7 dropped.
No no-caller method remains.

## Follow-ups (not built)

- F3: wire microphone/mute selections into `AudioRecorder` (or label the group).
- F4: decide placeholders vs hidden nav for Advanced/About.
- Suggested extra test once CI runs: `AttachLiveHotkey` idempotency
  (double-attach → single `Saved` invocation) — needs an `App` instance, so
  it cannot run under the singleton-safe pattern used here.
