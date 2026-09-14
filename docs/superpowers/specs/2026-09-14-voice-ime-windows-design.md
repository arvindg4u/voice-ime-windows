# Voice IME for Windows — Design

- **Date:** 2026-09-14
- **Path:** architectural (new repo, new platform)
- **Stack:** C# 12 · .NET 8 · WPF + WinForms tray · NAudio · xUnit
- **Repo:** https://github.com/arvindg4u/voice-ime-windows
- **Status:** approved by owner ("yes"), implemented as initial commit

## 1. Goal

A Windows app that works **universally everywhere in Windows**: press a global
hotkey in any app, dictate, and have the transcript pasted into the focused
window. EXEs are built **only by GitHub CI/CD** — nothing local. The Android
repo (`voice-ime-android`, v1.4.1) is the reference: replicate its core
architecture in a different language, changing only the platform edges.

## 2. Decisions (all owner-approved)

| # | Question | Decision |
| - | -------- | -------- |
| 1 | Tech stack | **C# .NET 8 WPF** — native Win32 access (RegisterHotKey, SendInput, NAudio), single-file EXE via `dotnet publish`, simplest `windows-latest` CI. Rejected: Tauri 2 (WebView buys nothing for a tray utility, adds JS/TS), Python+PyInstaller (heavy EXE, AV false-positives, weak tray). |
| 2 | Trigger | **Global hotkey Ctrl+Shift+Space** + tray Dictate fallback. |
| 3 | Delivery | **Clipboard + synthesized Ctrl+V** (restores prior clipboard). Rejected: per-character SendInput (slower, focus-sensitive; only needed for RDP/Citrix niches). |

## 3. Architecture

Same pipeline and state machine as Android; only edges change:

```text
Hotkey (RegisterHotKey) → AudioRecorder (NAudio WASAPI, 16kHz mono → WAV)
→ LlmClient (HttpClient, Gemini generateContent, round-robin keys, 429 failover)
→ ClipboardStore (history) → NativeInput (clipboard + Ctrl+V via SendInput)
```

Module map: `App` (tray lifecycle/orchestration), `HotkeyWindow`,
`AudioRecorder`, `LlmClient`, `NativeInput`, `SettingsStore` (DPAPI),
`ClipboardStore`, `SettingsWindow` (+Test button), `HistoryWindow`.
Settings schema (`%AppData%/VoiceIme/settings.json`): `baseUrl`, `model`,
`customPrompt` (strings), `keyCursor` (int), `apiKeysProtected` (base64 DPAPI
blob). History (`clips.json`): array of `{Id: guid, Text, CreatedUtc: ISO-8601
UTC, Pinned: bool}`, cap 100, pinned survive eviction.

## 4. CI/CD (nothing local)

- `windows.yml` (push/PR): `setup-dotnet` → `dotnet test` → `dotnet publish
  -r win-x64 --self-contained /p:PublishSingleFile=true` → upload `VoiceIme.exe`.
- `release.yml` (`v*` tags): test + publish + attach EXE via
  `softprops/action-gh-release` with generated notes.
- No code signing yet (unsigned EXE shows SmartScreen prompt — documented);
  no ARM64 leg yet.

## 5. Testing

xUnit + MockHttp: WAV header/tone/magnitude/silence; rotation success/429/401;
ordering/eviction/pins; DPAPI round-trip (Windows-guarded). Waveform deferred
(v1 uses tray state, not a custom canvas). Manual: Test button, Notepad paste,
error strings, auto-stop, elevated-app limitation.

## 6. Risks & limits

- Elevated targets unreachable without elevation (history preserves transcript).
- Single hotkey, not configurable yet (v1.1: settings field + conflict warning).
- No live-streaming transport in v1 (REST only; Android's LiveSocket equivalent
  is future work).
- Unsigned EXE → SmartScreen; optional later via Azure Trusted Signing.

## 7. Self-review

No TBDs — all sections concrete. Architecture matches implementation file for
file. Scope is one repo, one release unit. "Universally" is explicitly bounded
by the elevation caveat. Spec approved; implementation is the initial commit.
