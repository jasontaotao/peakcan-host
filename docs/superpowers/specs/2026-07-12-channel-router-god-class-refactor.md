# W25 SPEC — ChannelRouter god-class refactor (21st overall)

**Date**: 2026-07-12
**Status**: APPROVED (user deferred "你自己选" — design proceeds)
**Target version**: v3.39.0 MINOR
**Branch**: `feature/w25-channel-router-god-class`
**Sister pattern**: W18 PeakCanChannel (Infrastructure/Channel layer precedent, same namespace `PeakCan.Host.Infrastructure.Channel`).

## Context

`src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs` 305 LoC is the next god-class candidate in the W3-W24 series (20 refactors shipped). The class is **already `public sealed partial class ChannelRouter : IFrameSource`** at line 74 (modifier pre-existed — sister of 19/20 prior cases; W21 was 1st fresh-add).

`ChannelRouter` fans frames out from one or more `ICanChannel`s to one or more `IFrameSink`s, with per-sink exception isolation. 4 readonly fields + 1 ctor + 5 public/private methods + 3 `[LoggerMessage]` partial declarations. The 5 methods partition cleanly into 3 flow boundaries:
- **Channel-list registration** (RegisterChannel + UnregisterChannel, ~22 LoC)
- **Sink-list registration with Volatile.Write publish** (AttachSink + DetachSink, ~46 LoC)
- **Hot-path frame dispatcher with per-sink try/catch + auto-detach on secondary exception** (OnChannelFrame, ~75 LoC, LARGEST)

## Architecture

Sister pattern of W3-W24 series. 21st god-class refactor. **6th Infrastructure layer** refactor (after W4/W5/W10/W11/W18) + **15th subdirectory-pattern deployment**.

### W25 D1-D7

- **D1**: 3 NEW partials (`FrameRouting` + `Sinks` + `Channels`) in `ChannelRouter/` subdirectory.
- **D2**: No `partial` modifier edit needed (already partial at line 74).
- **D3**: 4 readonly fields (`_channels` + `EmptySinks` + `_sinks` + `_gate` + `_logger`) + 1 ctor + 3 `[LoggerMessage]` partials (`LogSinkOnError` + `LogChannelRouterSinkOnFrameFailed` + `LogDetachSinkFailed`) + class xmldoc stay in main.
- **D4**: All 3 `[LoggerMessage]` partials stay on `ChannelRouter` main partial declaration per W18 R1 + W22 D4 + W23 D4 sister precedent (CS8795 mitigation: keeping `[LoggerMessage]` partials on same-class declaration avoids the "no logging method found in corresponding source generator declaration" warning).
- **D5**: `OnChannelFrame` 75 LoC LARGEST method **moves to `FrameRouting.partial.cs`** per W22 D5 deviation logic (RecordService) + W23 D5 deviation logic (CyclicDbcSendService) — large methods can move when they map to a discrete flow boundary ("frame arrives → fan out + error isolation"), not as a single central orchestration method.
- **D6**: Branch name `feature/w25-channel-router-god-class`.
- **D7**: Order largest-first per W12+W14+W18+W22+W23+W24 D7 sister pattern: **C (FrameRouting, 75 LoC) → B (Sinks, 46 LoC) → A (Channels, 22 LoC)**.

### Flow boundaries (Phase 1 verified)

**Stays in main (~162 LoC, target)**:
- `using` block (1-4) + namespace + class xmldoc (8-73) + outer class declaration (74) — already partial
- 4 readonly fields + 1 static readonly empty sentinel: `_channels` + `_sinks` + `_gate` + `_logger` + `EmptySinks` (L76-91)
- 1 ctor (L98-101, **4 LoC, smallest method, stays inline**)
- 3 `[LoggerMessage]` partial method declarations: `LogSinkOnError` + `LogChannelRouterSinkOnFrameFailed` + `LogDetachSinkFailed` (L271-304 — D3 + D4 sister mitigation)

**Flow A — Channels (~22 LoC) → `ChannelRouter/Channels.cs`:**
- `public void RegisterChannel(ICanChannel channel)` (L112-121)
- `public void UnregisterChannel(ICanChannel channel)` (L123-134)

Touches: `_channels` + `_gate` + `OnChannelFrame` (string-name reference to dispatcher).

**Flow B — Sinks (~46 LoC) → `ChannelRouter/Sinks.cs`:**
- `public void AttachSink(IFrameSink sink)` (L137-155)
- `public void DetachSink(IFrameSink sink)` (L157-181)

Touches: `_sinks` + `_gate` + `EmptySinks` + `Array.Copy` + `Volatile.Write`.

**Flow C — FrameRouting (~75 LoC) → `ChannelRouter/FrameRouting.cs`:**
- `private void OnChannelFrame(CanFrame frame)` (L183-255)

Touches: `_sinks` + `EmptySinks` + `Volatile.Read` + `DetachSink` (string-name reference to Flow B method) + 3 `[LoggerMessage]` partial hook callsites (`LogChannelRouterSinkOnFrameFailed` + `LogSinkOnError` + `LogDetachSinkFailed`).

### Notes on cross-flow references

- `OnChannelFrame` calls `DetachSink(s)` (Flow B method) — partial-class visibility makes this automatic.
- `LogChannelRouterSinkOnFrameFailed` + `LogSinkOnError` + `LogDetachSinkFailed` are declared in main (D4) and called from FrameRouting.partial.cs — partial-class visibility makes this automatic.

## LoC trajectory

| Task | Flow | Range (1-indexed) | LoC deleted | Markers | LoC main after |
|---|---|---|---|---|---|
| T1 | C — FrameRouting | TBD per Phase 1 exact grep (around OnChannelFrame ~L183-255) | ~75 | 1 | ~230 |
| T2 | B — Sinks | TBD per Phase 1 exact grep (around AttachSink + DetachSink ~L137-181) | ~46 | 1 | ~184 |
| T3 | A — Channels | TBD per Phase 1 exact grep (around RegisterChannel + UnregisterChannel ~L112-134) | ~22 | 1 | ~162 |
| T4 | v3.38.0 -> v3.39.0 MINOR | (no source) | 0 | 0 | ~162 |

Cumulative: 305 → ~230 → ~184 → ~162 main. Re-grep + range verify after each task per W19 R1 first-correction + W17 wc-l-splitlines CONFIRMED + W20 fabrication LESSON APPLIED 20+ times in W24.

## W20 + W23 LESSON APPLIED — verbatim re-extract + struct-ctor verification

Per W20 T2 R1 fabrication incident + W23 T2 3-fix-cycle CanFrame/CanId struct-constructor fabrication:

1. **Re-grep boundaries BEFORE running each deletion script** (Phase 1 explore done in read; re-verify before each T1/T2/T3 script run).
2. **Re-extract original code from HEAD via `git show HEAD:src/...cs | sed -n '<range>p'`** for each partial's verbatim content.
3. **Verify `Volatile.Read(ref _sinks)` and `Volatile.Write(ref _sinks, ...)` signatures** stay exact — `ChannelRouter` partial visibility means Flow C (FrameRouting) reads `_sinks` and Flow B (Sinks) writes `_sinks` (zero-allocation immutable snapshot pattern documented at L28-42).
4. **Verify `Array.Copy(source, sourceIndex, destination, destinationIndex, length)`** 5-arg signature per W23 STRUCT-FABRICATION LESSON.
5. **Build + run filter tests after each task** to catch any extraction errors immediately.

## Sister-lesson candidates to monitor

| Lesson | Status | What W25 might observe |
|---|---|---|
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | 3/3 CONFIRMED (W21) | W25 21st god-class application (T1+T2+T3) |
| `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` | 3/3 CONFIRMED (W24) | W25 confirmed (Array.Copy 5-arg + Volatile.Read/Write signature verification applied) — additional confirmation |
| `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` | 3/3 CONFIRMED (W23) | W25 4th confirmation (3 [LoggerMessage] partials on main partial declared, called from FrameRouting.partial.cs) |
| `backing-fields-and-relaycommand-attribute-methods-must-stay-in-main-partial-while-partial-void-hooks-can-move` | NEW 1/3 (W24) | Held — ChannelRouter has no [RelayCommand] methods; observation N/A for W25 |
| `add-partial-keyword-to-monolithic-class-before-extraction` | 3/3 CONFIRMED (W21) | Held — already partial |
| `subdirectory-partials-pattern-empirical-20-precedents` | 3/3 CONFIRMED (W20) | W25 21st deployment, sister-of-W18 |
| `infrastructure-channel-layer-sister-pattern-empirical-w18-w25` | NEW W25 1/3 candidate | W25 + W18 = 2 confirmations of Infrastructure/Channel layer god-class refactor pattern |
| `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` | NEW W25 1/3 candidate | W25 OnChannelFrame 75 LoC moves to FrameRouting.partial.cs (frame-arrives → fan-out+error-isolation discrete flow); W22 RecordBatchAsync 100 LoC STAYS in main (single central orchestration); W23 OnTimerTick 151 LoC moves to Cycling.partial.cs (cyclic-tick → per-tick state machine discrete flow) |

## Verification

- `dotnet build src/PeakCan.Host.Infrastructure/`: 0 errors, 0 warnings
- `dotnet test --filter "FullyQualifiedName~ChannelRouter"`: 14/14 tests pass without modification
- `dotnet test` (full solution): 0 new fails (1 transient flaky AscParser per W13 T1 sister pattern retained)
- `wc -l src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs` ≤ 170 LoC (target ~162)
- 3 NEW partial files in `ChannelRouter/` directory
- 4 readonly fields + 1 ctor + 3 `[LoggerMessage]` partials remain in main
- 5 methods (Register/Unregister/Attach/Detach/OnChannelFrame) move to per-flow partials
- DI registration unchanged (`AddSingleton<ChannelRouter>()` factory in AppHostBuilder.cs)
- `IFrameSource` interface unchanged (still implemented via main partial declaration)
- Tag v3.39.0 + GH release published
- Branch deleted post-merge

## Out of scope (YAGNI)

- No public/internal API change.
- No test changes (14 ChannelRouterTests + sister tests pass without modification).
- No facade pattern (W3-W24 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk (verified — tests do not path-grep main file content).
- No CS8795 risk (D4 keeps `[LoggerMessage]` partials on main partial declaration).
- No `IFrameSource` interface visibility change (interface inheritance auto-binds across partials).
- No `PeakCanChannel.cs` (W18) partial changes (sister precedent unchanged).
- No `Append-LoggerMessage` to Flow C (kept on main per D4 sister precedent).

## Sister-pattern progress

| W | Layer | Subdirectory | Main LoC | Partial LoC | Total |
|---|---|---|---|---|---|
| W18 | Infrastructure/Channel | PeakCanChannel/ | -228 | +3 partials | 21st god-class confirmed at W25 |
| W22 | App/Services | RecordService/ | -193 | +3 partials | 18th god-class |
| W23 | App/Services | CyclicDbcSendService/ | -288 | +3 partials | 19th god-class |
| W24 | App/ViewModels | DbcSendViewModel/ | -146 | +3 partials | 20th god-class |
| **W25** | **Infrastructure/Channel** | **ChannelRouter/** | **-143 (target)** | **+3 partials** | **21st god-class** |

W25 cross-layer balance: 7 Infra-layer refactors total when shipped (W4+W5+W10+W11+W18+W19+W21 prior + W25 = infra-layer count to confirm).

## Files to touch

- NEW: `src/PeakCan.Host.Infrastructure/Channel/ChannelRouter/FrameRouting.partial.cs` (~75 LoC)
- NEW: `src/PeakCan.Host.Infrastructure/Channel/ChannelRouter/Sinks.partial.cs` (~46 LoC)
- NEW: `src/PeakCan.Host.Infrastructure/Channel/ChannelRouter/Channels.partial.cs` (~22 LoC)
- MODIFY: `src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs` (305 → ~162 LoC)
- MODIFY: `src/Directory.Build.props` (v3.38.0 → v3.39.0)
- NEW: `docs/release-notes-v3.39.0.md`
- NEW: `scripts/w25_task1_delete_framerouting.py`
- NEW: `scripts/w25_task2_delete_sinks.py`
- NEW: `scripts/w25_task3_delete_channels.py`
- NEW: `docs/superpowers/capture-decisions/2026-07-12-w25-channel-router-god-class-ship.md` (post-PR)

## Next after W25

- **W25.5 vault-only PATCH** — lesson-promotion opportunity for 2 NEW 1/3 candidates (`infrastructure-channel-layer-sister-pattern-empirical-w18-w25` + `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator`) + 1 promotion candidate (`partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` 4th confirmation since CONFIRMED at W24).
- **W26** — next god-class refactor candidate. Top remaining (>350 LoC) main files after W25: `AppShellViewModel.cs` 353 LoC (RESIDUAL — 4 existing partials + main residue) OR `CanApi.cs` 347 LoC (App/Services/Scripting fresh).
