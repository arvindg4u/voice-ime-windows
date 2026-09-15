# Phase 2 Report — Gemini Live WebSocket Engine

## Branch

`worktree-phase2-live-engine` (base `2cab9b1`), pushed to origin. CI: Windows workflow `34936029792` — **success**.

## Commits

- `0c30a34` feat: add Live wire-protocol builders, parsers, and WAV strip
- `ceb6f2a` feat: add Live session guard and overlap-aware final accumulator
- `461a720` feat: add Live WebSocket seam with ClientWebSocket implementation
- `47f28a7` feat: add Live session orchestration with setup gating and bounded finalize
- `0860757` fix: honor key-rotate signal on Live setup and stream sends
- `9569820` feat: implement Live transcription transport with key rotation
- `e307e28` feat: expose PCM chunk fan-out event on AudioRecorder

## Diff

`git diff 2cab9b1..HEAD --stat`: 17 files, +1609/−20. 8 src files, 8 test files, 1 routing-test edit.

## Files changed

Src: `LiveProtocol.cs` (new), `LiveSessionGuard.cs` (new), `FinalAccumulator.cs` (new),
`LiveSocket.cs` (new), `LiveSession.cs` (new), `LiveTranscriptionTransport.cs` (new),
`LlmClient.cs` (+18: Live routing arm + `LiveSocketsForTests` seam), `AudioRecorder.cs` (+9: `PcmChunkAvailable` event).
Tests: `LiveProtocolTests`, `LiveSessionGuardTests`, `FinalAccumulatorTests`, `LiveSocketTests`,
`LiveSessionTests`, `LiveTransportTests`, `AudioRecorderLiveSeamTests` (all new),
`LlmClientRoutingTests` (+39: WebSocket/no-HTTP + REST-400→Live assertions),
`TranscriptionContractsTests` (+22). No App/paste changes in the Phase-2 delta (`2cab9b1..HEAD`); the whole-branch diff vs main does include Phase 1's one-line SmartMode threading fix in App.xaml.cs (no paste logic).

## Live architecture

`LlmClient.TranscribeRoutedAsync` routes `*-live` / `IsLiveCandidate` models to
`LiveTranscriptionTransport` (REST 400 `IsLiveOnlyError` falls back REST→Live one hop, same key;
fallback targets never re-fallback, so loops are impossible). Transport strips WAV→PCM first
(no socket on typed audio errors), then one `LiveSession` per key with rotation on rate-limit
signals; cursor (`StartKeyIndex`) untouched by rotation. `FinalAccumulator` (from Phase 1
task numbering — guard + accumulator commit) provides overlap-aware final accumulation.

## WebSocket implementation

`ILiveSocket` / `ILiveSocketFactory` seam; `ClientWebSocketLiveSocket` is the thin production
wrapper (rationale in `LiveSocket.cs` docs: built-in, no new dependency). Socket disposal is
owned by the session: abort-registration calls `DisposeAsync` on prompt cancel, `finally`
closes (6 s grace) then disposes on every path.

## Setup protocol

`BuildLiveSetupJson` (top-level `inputAudioTranscription`, SMART/VERBATIM, TEXT, manual VAD),
`BuildLiveUri` (`wss://…/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key=…`),
setup-ACK gating with 20 s `SetupAckTimeout`.

## Audio streaming

`WavToMono16kPcm` validates 16 kHz mono 16-bit PCM; `StreamAudioAsync` sends 3200 B chunks as
`realtimeInput` base64 frames, then VAD `activityEnd`. Prompt-abort registration releases stuck
transports; every socket call observes the LiveTimeout-linked token.

## Interim behavior

Interim hypotheses are parsed (`TryParseInterim`) but never reach the accumulator and never
commit — `ReadFinalAsync` replaces/displays only; only non-empty final/`inputTranscription`
text becomes `Done`.

## Final accumulation

`FinalAccumulator` appends only non-overlapping suffixes of authoritative finals;
`LiveSession` returns `Done` only for non-empty final text; empty transcript is a typed error.

## Session safety

`LiveSessionGuard.Next()` captured at session start; every received message checks staleness
first — a stale session ignores everything and never streams (§16). Exactly-once via linear
flow: no TCS/Interlocked (no concurrent callbacks by construction; documented in `LiveSession`
docs, §23).

## Cancellation

Caller `ct` cancellation surfaces as unwrapped `OperationCanceledException` at every phase
(connect/send/ack/stream/read); abort registration releases stuck sockets promptly.

## Timeouts

`LiveTimeout` 75 s (linked token covering all phases), `SetupAckTimeout` 20 s,
`FinalizeGrace` 8 s bounded finalize after `turnComplete`, `CloseGrace` 6 s best-effort close.

## Error handling

All failures are fixed user-safe strings (`Invalid audio`, `Network error`, `Timed out`,
`Rate limited on all keys`, `Transcription failed`); keys/URIs/bodies never in messages;
`no reconnect` — any socket failure fails the attempt clean; typed timeout-is-error, never
commits interim (§15 ruling).

## Security

- No `Logger.` calls in `Live*.cs` (grep gate empty).
- No key/URI/secret in any `throw` in Live files — all throws carry fixed strings; key
  touches URI only via `Uri.EscapeDataString` in `BuildLiveUri`, never in messages.
- `TransportFallbackException : Exception` (NOT `OperationCanceledException`), so the
  `catch (… ) when (ex is not OperationCanceledException)` filters in REST transport never
  swallow it (check b).
- `LiveOnlyBodyWithSecrets_FallbackErrorStaysClean` passes with the real Live transport in
  the path: 100-byte non-WAV fails in `WavToMono16kPcm` pre-socket → fixed "Invalid audio"
  message; secret body never surfaces (check c). Confirmed by 653/653 CI pass.
- Reviewer check (a): repo-wide `grep -rn "Phase 2" tests/` returns nothing — no routing/contracts
  test asserts on the string "Phase 2".

## Tests added

48 new test methods: LiveProtocol 18, LiveSessionGuard 3, LiveSession 7, LiveSocket 4,
LiveTransport 7, AudioRecorderLiveSeam 3, FinalAccumulator (counted in 48), plus routing/security
assertions. Fake-based determinism: no `Task.Delay` in Live tests (grep gate empty).

## Build PASS/FAIL

PASS — `dotnet build VoiceIme.Windows.sln -c Release`: 0 warnings, 0 errors (local + CI).

## Tests PASS/FAIL

PASS — CI `windows-latest`: `Passed: 653, Failed: 0, Skipped: 0, Total: 653`.
(Main baseline run `34930350877`: `Passed: 511, Failed: 0` — the "7 pre-existing failures"
referenced in the brief belong to another workflow's scope and do not exist on current main;
comparison: branch 653 pass / 0 fail vs main 511 pass / 0 fail → **zero new failures**, all
142 new Phase-2 tests pass.)

## Pre-existing baseline failures

None on current main (511/511 green). The 7 historical failures (prompt library, sound atoms,
settings views) are absent from both runs — no baseline comparison delta to explain.

## New failures

Zero.

## Manual Live smoke test

NOT RUN — no production credentials in this environment.

## Known limitations

- One-shot chunked send of a complete PCM buffer per session; true mic-streaming (live
  `PcmChunkAvailable` → socket frames) is deferred to Phase 3. `PcmChunkAvailable` seam is
  exposed and tested but not yet wired to `LiveSession`.
- `turnComplete` alone is not completion: without authoritative final text it finalizes to a
  typed timeout/error, never a fabricated transcript (diverges from Android's commit-interim
  by explicit ruling).

## Rulings (carried from review)

- §15 timeout/close-empty returns typed error, never commits interim.
- §23 linear loop needs no completion guard (no TCS/Interlocked).
- Task 3 `CloseAsync`-Abort non-issue. Task 5 ScriptFinal plan-text defect (fixed).
- Task 6 no-producer-guard non-issue.
- Deferred minors: Task 1 check-then-cast; Task 2 O(n²); Task 3 fake-gate;
  Task 4 `DisposeAsync` discard + pre-cancelled registration; Task 5 switch-no-default.

## Deferred Phase-3

Mic-streaming pipeline (recorder seam → socket), paste/output integration, manual credentialed
smoke test, deferred minors above.

## Recommendation

Merge `worktree-phase2-live-engine`. Phase 2 acceptance gates all hold; STOP — do not begin Phase 3.
