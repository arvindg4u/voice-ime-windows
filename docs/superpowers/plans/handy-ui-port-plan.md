# Handy UI/UX Port — Implementation Plan

## Context

`voice-ime-windows` is a C# 12 / .NET 8 WPF tray app (single-file `win-x64` EXE,
CI-built). It records via hotkey, transcribes with cloud Gemini, pastes via
clipboard + Ctrl+V. The owner wants the frontend redesigned to follow
[cjpais/Handy](https://github.com/cjpais/Handy) (MIT licensed) UI/UX **exactly**,
plus its stability patterns — but **keeping our Gemini cloud backend** (Handy's
local Whisper/Parakeet backend is explicitly out of scope; no model downloads,
no Model screen, no GPU/acceleration UI).

A read-only reference clone lives at
`/teamspace/studios/this_studio/voice-ime-windows/handy-reference`
(git-ignored, never committed). Implementers may READ it; never modify it.

## Global Constraints

- Stack stays: C# 12, .NET 8, WPF + WinForms `NotifyIcon`, NAudio, xUnit.
  No new packages without a stated reason (NAudio, ProtectedData,
  MockHttp already referenced).
- Gemini backend untouched: `LlmClient` REST contract, key-rotation semantics,
  `settings.json` field names (`baseUrl`, `model`, `customPrompt`,
  `keyCursor`, `apiKeysProtected`), `clips.json` history schema. Settings UI
  may ADD fields (hotkey, activation mode, paste method) but must never break
  existing files — old files load with defaults.
- Visual language follows Handy exactly:
  - Accent solid button: `#DA5893`; logo pink light `#FAA2CA` / dark `#F28CBB`.
  - Light: text `#0F0F0F` on `#FBFBFB`. Dark: text `#FBFBFB` on `#2C2B29`.
  - Error `#DC2626` / `#F87171`, warning `#D97706` / `#FBBF24` (light/dark).
  - Muted gray `#808080`. System font stack only (Segoe UI on Windows).
  - Cards `8px` radius, `1px` `#808080 @20%` border. Buttons `8px` radius.
  - Sidebar `160px` wide, items `8px` padding + radius, active item
    pink `@80%` background.
- Follow the existing repo's C# conventions (file-scoped namespaces, nullable
  enabled, explicit usings in every file, no `!!`-style null-forgiving abuse).
- Every logic task ships xUnit coverage for new pure logic; WPF views
  themselves are not unit-tested (framework-bound) — extract testable logic
  into plain classes like the existing `ImeLayoutPolicy`-equivalent pattern.
- `dotnet test` must stay green after every task. Commit per task.

## Task 1 — Design tokens + shared WPF controls

Create `src/VoiceIme/Theme/` with:
- `HandyTheme.xaml` (`ResourceDictionary`): light + dark brushes for all
  palette tokens above (use `DynamicResource` so theme can switch at runtime;
  default to system theme at startup). Brush keys: `HandyText`,
  `HandyBackground`, `HandyAccent` (`#DA5893`), `HandyLogoPrimary`,
  `HandyError`, `HandyWarning`, `HandyMidGray`, `HandyCardBorder`,
  `HandyCardBackground`.
- Shared styles in the same dictionary: `HandyPrimaryButton`
  (white text on `HandyAccent`, 8px radius, hover 80% opacity),
  `HandySecondaryButton` (gray-tinted, hover accent border),
  `HandyDangerButton`, `HandySettingCard` (8px radius, 1px border),
  `HandySettingRow` (horizontal label + control, min height 48),
  `HandyGroupHeader` (12px, gray, uppercase, wide tracking),
  `HandyTextBox`, `HandyToggle` (44x24 pill, accent when on),
  `HandySlider`.
- `ThemeManager.cs` (plain static class, unit-testable mapping function):
  `ResolveTheme("system"|"light"|"dark")` → actual theme, following OS
  setting for `system`.
- Wire the dictionary into `App.xaml` resources.
- Tests: `ThemeManagerTests` (system/light/dark resolution incl. invalid
  string → system fallback).

## Task 2 — Main window shell (sidebar + footer + close-to-tray)

Replace the launch behavior: app starts tray-only; settings opens in a
`MainWindow` (680x570, min 680x570, resizable) that mirrors Handy's shell:
- Left sidebar 160px: logo text "🎙️ Voice IME" + nav items (General,
  History, Gemini, Advanced, About) with 24px emoji/text icons, active item
  pink @80% background, inactive hover gray @20%.
- Right content area: per-section `UserControl`, 16px padding, scrollable.
- Footer bar: left = status text (tray state), right = `v{version}` + update
  placeholder text.
- Close button hides to tray (`Hide()`, cancel close) instead of quitting;
  Quit only via tray menu. Reopening via tray Settings… activates existing
  window.
- Remove old `SettingsWindow` awareness from `App.xaml.cs` StartupUri flow:
  app is tray-first (`ShutdownMode.OnExplicitShutdown`), `MainWindow`
  created on demand (singleton).
- Delete `SettingsWindow.xaml(.cs)` only after its content is rehomed
  (Task 3/4 create the new screens; this task keeps the old window working
  until then — do NOT break the build: keep old files compiling).

## Task 3 — General settings screen

New `Views/GeneralSettingsView.xaml(.cs)` + viewmodel-less code-behind bound
to `SettingsStore` (extend the store — see schema rule below):
- Hotkey capture control (`ShortcutInput` port): idle chip showing current
  binding (default `Ctrl+Shift+Space`), click → recording mode (accent
  border, "press keys…" placeholder) capturing keydown, commit on all-keys-up,
  click-outside cancels, Reset button restores default. While capturing,
  unregister the live hotkey; on failure to register the new chord, roll back
  to the old binding and show the error inline (Handy `restore_registration`
  pattern).
- Activation mode dropdown: `Hold or toggle` / `Push to talk` / `Toggle`
  (persist; v1 behavior = toggle — other modes take effect in Task 8's
  coordinator; UI first).
- Sound group: microphone device dropdown (NAudio `WaveIn` device list +
  system default), mute-while-recording toggle (persist only for now),
  volume/test affordance minimal.
- All writes validate-then-commit: invalid values coerce to defaults with a
  warning, never throw (Handy settings pattern).
- Schema addition (additive only): `hotkey` (string, default
  `"Ctrl+Shift+Space"`), `activationMode` (string, default `"toggle"`),
  `microphone` (string device name, default `""` = system default),
  `muteWhileRecording` (bool, default false).
- Tests: hotkey parse/format helpers (`"Ctrl+Shift+Space"` ⇄ modifiers+key),
  invalid chord rejected, store round-trip with new fields, old file
  (without new fields) loads with defaults.

## Task 4 — Gemini settings screen (Handy post-processing API group port)

New `Views/GeminiSettingsView.xaml(.cs)` mirroring Handy's post-processing
API group layout, replacing provider/model-select with our fixed Gemini fields:
- Stacked group: Base URL field (wide), API keys box (mono font, one per
  line), Model field, Reset-to-defaults button.
- Custom prompt group (stacked): multi-line editor (min height 100),
  `Update`/`Save` happens via window Save, char-count hint (max 2000),
  tip text.
- Test button runs the 440 Hz tone through `LlmClient` and shows inline
  result (keep existing behavior, restyle to Handy primary button).
- Rehome and then delete old `SettingsWindow.xaml(.cs)`.
- Tests: prompt length validation (>2000 rejected with message), key
  splitting (blank lines/whitespace trimmed).

## Task 5 — History screen (Handy history port)

Replace `HistoryWindow` with `Views/HistorySettingsView.xaml(.cs)` embedded
in the main window + deletable standalone file:
- Header row: small uppercase gray title + `Open folder` secondary button
  (opens `%AppData%/VoiceIme` in Explorer).
- Entries newest-first: timestamp (medium, 14px), transcript italic
  14px word-wrapped selectable, icon row (Copy→check feedback, Pin/Star
  filled when pinned, Retry for failed/empty entries, Delete).
- Failed transcriptions persist as retryable entries (empty text + error):
  Retry re-sends stored audio? No — audio is memory-only, so Retry
  re-opens the entry with a "re-dictate" hint. (Ruling: no audio persistence;
  privacy constraint wins over Handy's audio-retry.)
- Infinite list is simple `ListBox` (cap 100 already); keep pin/delete/
  clear-unpinned semantics from `ClipboardStore` unchanged.
- Delete old `HistoryWindow.xaml(.cs)` after rehoming; `App` opens the main
  window to the History section instead.
- Tests: entry display-label formatting (pin prefix, time format,
  80-char truncation) as a pure helper.

## Task 6 — Recording overlay (Handy compact pill port)

New topmost transparent overlay `OverlayWindow.xaml(.cs)`:
- Compact pill 256x50, no decorations, always-on-top, skipped in taskbar,
  non-focusable: pink 7px status dot (pulsing while recording), 9 mini
  waveform bars (4px wide, 3px gap, accent fill) driven by
  `AudioRecorder.LevelChanged`, timer text (tabular numerals, `m:ss`),
  cancel ✕ button (22px circle, hover accent).
- Position: bottom-center above taskbar (Win32 work-area based), 40px bottom
  offset.
- Shows on recording start, hides on stop/cancel/error. Uploading state:
  dot turns tertiary + spinner + "Sending…" label (pill widens to ~216px).
  Error state: red dot + message, auto-hide after 3 s.
- Overlay visibility cached in a `Volatile` flag so the ~30 Hz level callback
  does one read, not a dispatcher call (Handy `OVERLAY_ENABLED` pattern).
- Tests: `FormatElapsed` already exists — add `M:ss` cases; overlay
  viewmodel/state (`OverlayState` record: phase + level) pure transitions.

## Task 7 — Tray menu restructure + typed errors → toasts

- Tray menu order matches Handy idle: `Voice IME v{version}` (disabled) |
  separator | `Copy Last Transcript` | separator | `Settings… (Ctrl+,)` |
  separator | `Quit`. Busy (recording/uploading): version | separator |
  `Cancel` | separator | Copy Last Transcript | separator | Settings… |
  separator | Quit. Icons per state (use existing system icons; no binary
  assets in v1).
- Typed backend error events: `RecordingError` (mic denied / no device /
  unknown), `TranscriptionError` (message), `PasteError` — raised from the
  pipeline, handled in one place showing a balloon tip + inline overlay
  error, never leaving the overlay stuck (Handy atomic-revert pattern).
- Single toast/balloon host: all user-visible messages route through one
  `Notifier` class (balloon tip; WPF has no sonner — keep native).
- Tests: `Notifier` message formatting (user-safe strings only — assert no
  key material: feed a fake key, assert output never contains it).

## Task 8 — Coordinator state machine + pipeline guards

New pure `DictationCoordinator` class (no UI handles) owning
IDLE→RECORDING→UPLOADING→ERROR, porting Handy's coordinator essentials:
- 30 ms debounce on hotkey events; hold-vs-toggle classification per
  `activationMode` (hold = key-down starts, key-up stops; toggle = each
  press flips; 50 ms release-grace so a fast tap in hold mode still counts).
- Optimistic start + rollback: recording begins immediately, start failure
  reverts to Idle with typed error.
- Generation counters for stop/cancel so a late callback from a previous
  session can't clobber the new one.
- One `CancelCurrentOperation()` entry point: stops capture, cancels upload
  (`CancellationToken`), hides overlay, returns tray to Idle.
- Per-stage cancellation checks (stop → transcribe → paste) with 25 ms
  poll granularity on the token.
- `App.xaml.cs` wires the coordinator (thin glue only — no logic in App).
- Tests: ~synthetic-clock driven tests — debounce coalescing, hold vs
  toggle, release-grace, generation staleness, cancel mid-upload drains to
  Idle. (No real hotkeys/audio/HTTP in tests.)

## Task 9 — Settings salvage/migration + file logging

- `SettingsStore`: additive `schemaVersion` (current 1); load never throws —
  whole-parse failure falls back to per-field salvage over defaults (bad
  enum string → default + in-memory warning flag, never wipes the file);
  custom converter accepts legacy shapes; idempotent `Migrate()` for future
  versions. Frozen-fixture test: a v0 file (only baseUrl/model/keys) loads
  with all new defaults.
- Logging: lightweight `Logger` (no new packages — `System.Diagnostics.Trace`
  + file): `%AppData%/VoiceIme/logs/voiceime.log`, 500 KB rotation keeping
  1 spare, runtime level via `VOICEIME_LOG_LEVEL` env (default Info),
  redaction — secret values and transcript bodies never logged (log lengths
  and key fingerprints only). Per-stage latency lines (hotkey→capture,
  capture→response, response→paste).
- Single-instance: named mutex + IPC-ish focus (if second instance starts,
  it shows the running one's settings window via window-message broadcast
  and exits).
- Tests: salvage (corrupt JSON → defaults), fixture migration, redaction
  assertions, rotation (write >500 KB across two files → oldest trimmed).

## Explicit non-goals (do not build)

- Local transcription, model downloads/cards, GPU/acceleration selectors.
- Auto-update checker, donation buttons, onboarding wizard, debug log viewer.
- ARM64 publish leg, code signing, installer/MSIX.
