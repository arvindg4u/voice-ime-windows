# AGENTS.md

Tray-first Windows voice dictation: global hotkey → NAudio capture → Gemini
transcription → clipboard + Ctrl+V into the focused window. C# 12 / .NET 8,
WPF shell + WinForms `NotifyIcon`, single-file `win-x64` EXE. Two projects:
`src/VoiceIme` (WinExe) and `tests/VoiceIme.Tests` (xUnit, references src).

## Build & test

- **No `dotnet` is installed on this host** — you cannot build or test locally.
  `windows-latest` CI is the only gate: `dotnet test VoiceIme.Windows.sln -c Release`
  (`.github/workflows/windows.yml`).
- Even with an SDK, `dotnet test` cannot run on Linux: the suite targets
  `net8.0-windows` and the test host needs the Windows-only
  `Microsoft.WindowsDesktop.App` runtime. On Linux only `dotnet build` compiles
  (via `EnableWindowsTargeting`).
- Publish: `dotnet publish src/VoiceIme/VoiceIme.csproj -c Release -r win-x64
  --self-contained /p:PublishSingleFile=true
  /p:IncludeNativeLibrariesForSelfExtract=true -o publish`
- Installer (needs Inno Setup 6): `ISCC.exe installer\VoiceIme.iss` →
  `installer/Output/VoiceIme-Setup-<version>.exe`.

## Versioning & release

- Bump the version in **both** `src/VoiceIme/VoiceIme.csproj` (`<Version>`) and
  `installer/VoiceIme.iss` (`#define MyAppVersion`); they must match.
- Pushing a `v*` tag runs `release.yml` (test + publish + installer → GitHub
  Release). Conventional Commits; the Handy UI-port plan expects one commit per task.

## Architecture (not obvious from filenames)

- `App.xaml.cs` is orchestration glue: it executes effects (capture, upload,
  overlay, tray, paste). `DictationCoordinator.cs` is the **pure, UI-free state
  machine** (Idle → Recording → Uploading → Error) that decides. Keep dictation
  logic out of App and UI handles out of the coordinator; the coordinator takes
  an injectable clock so tests drive time synthetically.
- Transport is data-driven: `ModelRouter.RouteModel` maps the model name to
  `Rest`, `Live`, or `Interactions`. `LlmClient` is the entry point; each
  transport implements `ITranscriptionTransport` (`TranscriptionContracts.cs`).
  Live names (`-live`, `native-audio`, `-dialog`) stream over WebSocket
  (`LiveSession`/`LivePcmPump`); REST posts inline base64 audio to
  `{baseUrl}/models/{model}:generateContent`.
- Persistent state under `%AppData%\VoiceIme\`: `settings.json` (API keys
  DPAPI-encrypted), `clips.json` (transcript history — **plaintext**),
  `logs\voiceime.log` (rotating).
- UI: `MainWindow` shell + `MainNav` sidebar (General / History / Speech /
  Advanced / About), each a `Views/*SettingsView`. The "Speech" sidebar label is
  `MainSection.Gemini`.
- **One shared instance per store.** There is exactly one `SettingsStore` and one
  `ClipboardStore`; views bind the live instances through
  `CreateMainWindow`/`RefreshSectionViews`. Do **not** `new`/`Load()` a second
  store inside a view — that re-introduces a merge-blocking bug (in-UI edits
  ignored; cursor save clobbering just-saved keys). See `AppWiringTests`.

## Settings contract

- Adding a settings field is additive: give it a default and coerce bad values
  instead of throwing (the `Coerce*`/`Clamp*` helpers in `SettingsStore.cs`).
  Additive fields do **not** bump `CurrentSchemaVersion`.
- Tests touching the process-global `SettingsStore.SettingsPathOverride` /
  `Logger.FilePathOverride` must reset them in a `finally` and live in the
  `"SettingsFile"` collection; env-var tests use `LoggerEnvCollection`.

## Design system (enforced by tests)

- All colours live in `src/VoiceIme/Theme/Palette.cs`; views reference
  **semantic** tokens and use `DynamicResource` for every colour (a live theme
  switch repaints `DynamicResource` only). Reference: `docs/DESIGN-SYSTEM.md`.
- Never write a literal hex or named colour (`White`, `Black`, …) in `.xaml` —
  `ThemeTokenAuditTests` fails on it. Only `Palette.cs`, `ThemeManager.cs`,
  `HandyTheme.xaml` may name colours.
- Both palettes must define the identical key set (`ThemeContrastTests`).
- When adding a visual state, add its foreground/background pair to
  `ContrastContract.Build()` (`Theme/Contrast.cs`); AA (4.5:1) is the floor,
  `TextPrimary` holds AAA (7:1). Non-colour scales (`DesignTokens.cs`) are
  guarded by `DesignTokenTests` — don't invent values outside the ramps.
  Legacy `Handy*` token aliases only ever shrink.

## Privacy invariants (do not regress)

- Never log API keys or transcript text — log lengths/fingerprints via
  `Logger.LogTranscriptReceived` / `LogKeyUsed`; `Logger.RedactFree` scrubs
  `AIza…` as a last resort.
- Audio stays in RAM and is zeroed after the request; never written to disk.
- No-goes (from CONTRIBUTING): no background services, no audio persistence, no
  unencrypted keys, no new network endpoints, no new install-time components.

## Testing quirks

- Tests that construct WPF controls belong to the `"WpfSta"` collection
  (serialized — `Application.LoadComponent` is not thread-safe) and run through
  `StaTestHelper.TryRunOnSta`. Headless/Linux runners have no window station, so
  these pass **vacuously**; full assertions run only on windows-latest.
- HTTP is mocked with `RichardSzalay.MockHttp` (rotation / 429 / 401 paths); the
  Live WebSocket seam is injected via `ILiveSocketFactory`.

## Reference

- `README.md` is user-facing; its "Architecture" file map predates the
  Live/Interactions transports, Theme system, and Views — trust the source tree.
- Design spec and plan/phase artifacts live in `docs/superpowers/` and
  `.superpowers/sdd/`.
- A read-only Handy reference clone (`handy-reference/`, gitignored) is
  inspiration only if present; never modify or commit it.
