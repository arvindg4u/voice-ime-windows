# Phase 3 — True Real-Time Mic Streaming: Final Report (§44)

- Date: 2026-09-20
- Branch: `worktree-phase3-realtime-streaming` (worktree `.claude/worktrees/phase3-realtime-streaming`), 5 commits `6971e51..b5df8eb` on top of Phase-2 head `3e30ab0`
- Base: `3e30ab0` (Phase 2 merge-record commit; itself not in `main` — Phases 1–3 all live on worktree branches)
- Merge status: NOT merged to `main`. Pushed to `origin/worktree-phase3-realtime-streaming`; windows-latest CI green on the pushed head.
- Diff vs base: 12 files, +1674/−8.

## Commits

| Hash | Task | Subject |
|------|------|---------|
| `6971e51` | 1 | streaming front-end on LiveSession |
| `dd67ef9` | 2 | bounded PCM pump for Live streaming |
| `28bfd22` | 3 | streaming transport path with interim/final events |
| `478e07f` | 4 | interim preview text on overlay |
| `b5df8c9` | 5 | App Live-streaming integration (this session, direct — subagents kept cancelling) |

## Files changed

| File | Change |
|------|--------|
| `src/VoiceIme/LiveSession.cs` | +267: `ConnectAndSetupAsync` / `SendPcmAsync` / `CompleteAndReadFinalAsync` streaming front-end; `RunAsync` one-shot untouched |
| `src/VoiceIme/LivePcmPump.cs` | NEW (91): bounded `Channel<byte[]>` pump, default 300 |
| `src/VoiceIme/LiveTranscriptionTransport.cs` | +63: `TranscribeStreamingAsync` single-key attempt |
| `src/VoiceIme/OverlayState.cs` | +22: `FormatPreview(finals, interim)` pure join |
| `src/VoiceIme/OverlayWindow.xaml(.cs)` | +4/+53: Recording-phase preview text via existing dispatcher pattern |
| `src/VoiceIme/App.xaml.cs` | +591/−0: Live branch in start/stop, `CommitTranscript` extraction (byte-verbatim move, verified by script), `_liveFailed` fail-closed latch |
| `tests/VoiceIme.Tests/LiveSessionStreamingTests.cs` | NEW (138) |
| `tests/VoiceIme.Tests/LivePcmPumpTests.cs` | NEW (62) |
| `tests/VoiceIme.Tests/LiveStreamingTransportTests.cs` | NEW (82) |
| `tests/VoiceIme.Tests/OverlayPreviewTests.cs` | +15 |
| `tests/VoiceIme.Tests/AppLiveStreamingTests.cs` | NEW (294): gate + integration-level contracts |

## Real-time architecture

`AudioRecorder.PcmChunkAvailable` (true-copy chunks, Phase 2 seam) → `LivePcmPump` (bounded channel; NAudio callback only `TryWrite`s, never blocks) → single-reader drain → `LiveSession.SendPcmAsync` → interim/final callbacks → Dispatcher-marshaled overlay preview. Stop: `pump.Complete()` → drain tail → `CompleteAndReadFinalAsync` → exactly-once commit through the untouched coordinator gates. REST/Interactions path byte-identical (verified: commit-block move is byte-verbatim).

## PCM ownership

Recorder fan-out copies each chunk before invoking (`ToArray` at the callback); pump moves immutable `byte[]`; `LiveSession` never zeroes streaming chunks (one-shot `RunAsync` still zeroes its private clone). WAV accumulates in the recorder harmlessly on the Live path and is `Array.Clear`ed at stop.

## Queue/backpressure

Bounded 300 (~30 s at 100 ms/chunk, Android pending-cap parity). Full → `TryEnqueue` false → `FailLiveSession` (typed error, silent cancel + balloon, no partial paste). Draining starts only after setup-ACK; Complete-then-drain ordering guarantees last-chunk-before-stop (§15).

## Live lifecycle

One `LiveSession` per dictation, created at record start on the cursor key. No reconnect/resumption/replay/retry (brief §37). Socket owned by session: `CompleteAndReadFinalAsync` closes; `DisposeAsync` is idempotent and safe from cancel/stop/exit paths.

## Setup gating

`ConnectAndSetupAsync` = connect → setup → ACK-wait under `SetupAckTimeout`, reusing the Phase-2 `WaitForSetupAckAsync` verbatim (no second ACK loop). False → `ReportStartResult` rollback (same path as capture start failure). Whole streaming session bounded by `LiveTimeout` from connect.

## Activity start

First `SendPcmAsync` sends `activityStart` exactly once before its chunk (`_activityStarted` flag); post-`Complete` sends are no-ops, never throws.

## PCM streaming

Chunks sent in call order as single audio messages (caller contract: 16 kHz mono 16-bit, not validated). Drain is the sole `SendPcmAsync` caller (single-reader channel + exactly-once `StartLiveDrain` claim shared by record/stop paths, both on the UI thread).

## Activity end

`CompleteAndReadFinalAsync` sends `activityEnd` + `audioStreamEnd` in order, then reuses the exact Phase-2 `ReadFinalAsync` (turnComplete-not-final ruling, guard checks, Rotate mapping).

## Finalization

Bounded by `FinalizeGrace`; empty/whitespace final → typed error (`Got empty transcript`), persisted as empty-text history row like REST failures. `Done` carries authoritative final text only.

## Interim UX

`InterimReceived` = latest-only replace; `FinalReceived` = per-turn deltas appended. App joins via `OverlayState.FormatPreview`; `SetPreview` renders during Recording only, empty clears. Listener exceptions swallowed (reader never throws).

## Final accumulation

`FinalAccumulator` (Phase 2, untouched) fed only by final frames; interim never reaches it, so no final is fabricated from hypotheses. `Done` short-circuits on no-advance final with prior text (server echo of cumulative input counts as final signal).

## App integration

`StartRecordingAsync`: recorder first, pump buffers pre-ACK, then setup; Live branch predicated on `ModelRouter.RouteModel == Live` at start AND stop (mid-record model switch falls back to WAV). `StopAndTranscribeAsync` branches to `StopLiveAndCommitAsync` only when the recording actually started a session. Key index captured at start; cursor advances `(used+1)%n` on Done only.

## Exactly-once commit/paste

Single `CommitTranscript` helper (extracted, shared by both paths) + `ShouldCommitLiveTranscript` gate (token alive, generation current, still Uploading) checked after drain, after finalize, and inside `CommitTranscript` via `ReportUploadSucceeded`. `_liveFailed` latch: a failed session never finalizes even if stop wins the race with the failure marshal.

## Cancellation

Coordinator `SessionToken` threads through setup/drain/finalize; every cancel path funnels to `NotifyUploadCancelled` (silent, no paste, preview cleared). OCE propagates unwrapped; drain wrapper swallows only its own background OCE (cancel path owns UI) — never converted to a typed error. `CancelRecording`/`OnExit` detach, complete, and best-effort dispose.

## Session safety

Stale generations ignored at every gate (setup continuation, drain claim, finalize, commit). Cross-session interference impossible: single pipeline, fields snapshotted to locals at stop, cleared on every exit path.

## Cleanup

Stop: detach → clear → dispose (idempotent `DisposeAsync`, verified on `ClientWebSocketLiveSocket`). Cancel/exit: same trio, fire-and-forget dispose (never throws). Pump `Complete`+`Dispose` idempotent. No CTS/socket/timer leaks (session CTS torn down in all paths).

## Security

No `Logger.` calls in new Live/streaming src (grep-verified); keys travel only in the internally-built `wss://` URI query, never stored as fields, never in messages; transcripts/chunks/URLs/bodies never logged (only existing char-count/fingerprint metrics in the moved commit block). Fixed user-safe strings throughout; socket messages never surface. `LiveSessionGuard` gates every received frame.

## Tests added

10 App-level integration tests (`AppLiveStreamingTests`: Done→commit, stale/cancelled/wrong-state gate rejects, pump-full no-commit, cancel-during-drain OCE, last-chunk ordering with 7-frame assertion, setup-false rollback, interim/final join, routing, preview-clear) + Task 1–3 suites (streaming front-end, pump bounds/ordering, transport path) + Task 4 preview cases. Fake sockets only, TCS/`Channel`-controlled, no `Task.Delay` (grep-verified). App singleton untestable → internal static `ShouldCommitLiveTranscript` seam + coordinator/transport/pump integration level (documented in file header).

## Build

`dotnet build VoiceIme.Windows.sln -c Release`: **0 warnings, 0 errors** (Linux, via `EnableWindowsTargeting`).

## Tests

windows-latest CI (run `35488232292`, head `b5df8c9`): **685 passed, 0 failed, 0 skipped** — full suite incl. all Phase 1/2/3 suites. Publish + EXE artifact upload also green.

## New failures

Zero. First-push CI green with no triage needed.

## Manual smoke

NOT RUN (no Windows host in this session; Linux container cannot execute the testhost either). Needs: Live-model dictate into Notepad (interim preview visible, single paste), setup-timeout airplane-mode, pump-full on very long recording, cancel mid-stream silence, mid-record model switch fallback.

## Known limitations

- Single attempt on cursor key: streaming cannot rotate on 429 mid-stream (no replay by design) — Rotate fails the attempt with the rate-limited message.
- Pump bound 300 (~30 s unsent backlog): sustained backpressure fails safely rather than degrading.
- `StopLiveAndCommitAsync` (~180 lines) and `StartLiveSessionAsync` (~90) exceed the old <50-line convention; Phase-3 plan sets no size limit — left unsplit to avoid refactor risk without local test execution.
- `CommitTranscript` named without `Async` suffix (synchronous method; plan suggested `CommitTranscriptAsync`).

## Deferred Phase-4 (do NOT start)

Per brief STOP condition: multi-key streaming rotation strategy, backpressure UX (warning before fail), Live+Interactions model-matrix expansion, streaming latency metrics, any settings UI for streaming.

## Recommendation

Merge `worktree-phase3-realtime-streaming` → `main` only together with (or after) Phase 1 + Phase 2 branches, in order, each with its CI green — Phase 3's base is Phase 2's head, which is not in `main`. Then run the manual smoke list above on a real Windows host with a Live model before any `v*` release.
