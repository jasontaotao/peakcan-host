# W9 Spec — IsoTpLayer god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer.cs` from 806 LoC to ~150 LoC by extracting 7 logical flow groups into partial-class files. Pure mechanical refactor — zero behavioral change, zero test changes, zero API surface change.

**Architecture:** Same W3-W8 partial-class split pattern, applied to Core layer. IsoTpLayer stays a single `sealed partial class : IDisposable` with 7 partial-class files in `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer/` directory. Main file keeps state fields, public properties, constants, nested types (`WatchdogHandle`, `CanIdConfig`), DI readonly fields, constructors, and `Dispose`. Each partial file owns one logical flow group.

**Tech Stack:** C# .NET 10, Microsoft.Extensions.Logging.Abstractions. No WPF/MVVM dependency (Core layer). Git with LF line endings (per v3.18.0 PATCH `.gitattributes`).

## Global Constraints

- **Public API unchanged.** No method signatures, properties, or nested types move.
- **partial-class visibility.** All private methods + private fields visible across partial files; cross-flow calls stay as plain invocations.
- **Test coverage unchanged.** No tests added, removed, or modified.
- **Line-ending normalized to LF.** Per v3.18.0 PATCH `.gitattributes` enforcement.
- **No behavioral change.** Every method body, xmldoc, comment, and whitespace moves verbatim.
- **No version bump until Task 8.** Tasks 1-7 keep `src/Directory.Build.props` at v3.23.1. Task 8 bumps to v3.24.0.
- **Branch**: `feature/w9-isotp-layer-god-class` (already created from `main` @ `f2376f5` v3.23.1).
- **Spec**: this file.

---

## Current state (806 LoC)

`src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer.cs` (v3.23.1 HEAD) has:
- 14 methods total + 1 nested class (`WatchdogHandle`)
- Implements: `IDisposable`
- Public API: 2 ctors + `SendMessageAsync` + `ProcessFrame` + `Reset` + `Dispose` + 2 properties + `MessageReceived` event
- 8 distinct responsibilities stacked end-to-end

Threshold per `automotive-coding-standards-file-size.md`: 800 LoC ceiling. IsoTpLayer is **AT the ceiling** (100.75%).

## Target state (~150 LoC main + 7 partials)

```
src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer.cs                              # main file, ~150 LoC after Task 7
src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer/                                 # NEW directory
  FlowControlFlow.cs                                                         # Task 1 — HandleFlowControl + SendFlowControl (~30 LoC, smallest)
  LoggingFlow.cs                                                             # Task 2 — 3 [LoggerMessage] partial methods (~30 LoC)
  WatchdogFlow.cs                                                            # Task 3 — StartReceiveWatchdog + CancelReceiveWatchdog + WatchdogHandle nested class (~110 LoC)
  SendFlow.cs                                                                # Task 4 — SendMessageAsync + SendSingleFrameAsync + SendCanFrameAsync + SendCanFrame (~95 LoC)
  ReceiveFlow.cs                                                             # Task 5 — ProcessFrame + HandleSingleFrame + HandleFirstFrame + HandleConsecutiveFrame + HandleConsecutiveFrameLocked (~110 LoC)
  MultiFrameTransportFlow.cs                                                 # Task 6 — SendMultiFrameAsync + StMinToTimeSpan + WaitForFlowControlAsync (~165 LoC, largest)
  LifecycleFlow.cs                                                           # Task 7 — ctors + Dispose + Reset + constants + public properties + event + CanIdConfig record (~155 LoC, includes lots of xmldoc)
docs/superpowers/plans/2026-07-11-isotp-layer-god-class-refactor.md   # NEW in Task 0 (plan written alongside spec)
docs/release-notes-v3.24.0.md                                              # NEW in Task 8
```

**Net reduction**: 806 → ~150 LoC main file (-656 LoC, -81.4%); total lines unchanged (still ~806 across main + partials).

## Flow boundaries

### Flow A — Lifecycle (~155 LoC, includes xmldoc-heavy CanIdConfig record)
Owns object construction, disposal, reset, public properties, and the nested `CanIdConfig` record.

**Methods**:
- `IsoTpLayer(CanIdConfig, Action<CanFrame>)` (line 195) — sync ctor
- `IsoTpLayer(CanIdConfig, Func<CanFrame, Task>, ILogger<IsoTpLayer>?)` (line 215) — async ctor
- `Dispose()` (line 305)
- `Reset()` (line 285)

**Constants + properties** (stay in main):
- `MaxSingleFramePayload` (line 21), `MaxMessageLength` (line 24), `DefaultFlowControlTimeout` (line 27), `DefaultReceiveTimeout` (line 30)
- `FlowControlTimeout` (line 172), `ReceiveTimeout` (line 183)
- `MessageReceived` event (line 190)

**DI readonly fields** (stay in main):
- `_config`, `_sendFrame`, `_sendFrameAsync`, `_logger` (lines 32-35)

**Nested type stays in main**: `CanIdConfig` record (lines 793-806) — public type in API surface, like W8's `TraceChartStatistics`.

**File**: `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer/LifecycleFlow.cs`
**Required usings**: minimal (no project-internal types beyond what main has)

### Flow B — Send (~95 LoC)
Owns message-send dispatch (SF vs MF routing) + the sync/async send-frame callback paths.

**Methods**:
- `SendMessageAsync(byte[] data, CancellationToken ct = default)` (line 230) — public entry, routes SF vs MF
- `SendSingleFrameAsync(byte[] data)` (line 310) — private SF helper
- `SendCanFrameAsync(byte[] data, int frameIndex)` (line 335) — async send with try/catch + IsoTpSendFailedException throw
- `SendCanFrame(byte[] data)` (line 749) — legacy sync send

**Depends on**:
- `_config` (DI, main)
- `_sendFrame` / `_sendFrameAsync` / `_logger` (DI, main)
- `SendFailureCount` (test counter, main)
- `SendMultiFrameAsync` (Flow C, intra-flow cross-partial)
- `IsoTpSendFailedException`, `IsoTpFrame`, `IsoTpFrameType`, `CanFrame`, `CanId`, `FrameFormat`, `FrameFlags` (Core types, partial-class visible from any Core namespace)

**File**: `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer/SendFlow.cs`
**Required usings**: `System.Threading`, `System.Threading.Tasks` (Task/TaskCompletionSource-like types), `Microsoft.Extensions.Logging`

### Flow C — MultiFrameTransport (~165 LoC, largest)
Owns the multi-frame transport: FF/CF emission, FC wait, BS gate, STmin pacing.

**Methods**:
- `SendMultiFrameAsync(byte[] data, CancellationToken ct)` (line 369) — main MF transport
- `StMinToTimeSpan(int stMinRaw)` (line 474) — static helper
- `WaitForFlowControlAsync(CancellationToken ct)` (line 486) — private FC wait

**Depends on**:
- `_sendGate` (state, main)
- `_txLock` + `_txWaitingForFc` + `_txBlockSize` + `_txStMin` (state, main)
- `_flowControlTimeout` (state, main)
- `SendCanFrameAsync` (Flow B, cross-partial)
- `StMinToTimeSpan` (intra-flow)

**File**: `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer/MultiFrameTransportFlow.cs`
**Required usings**: `System.Threading`

### Flow D — Receive (~110 LoC)
Owns incoming frame processing + reassembly state mutation.

**Methods**:
- `ProcessFrame(CanFrame frame)` (line 256) — public entry, decode + dispatch
- `HandleSingleFrame(IsoTpFrame frame)` (line 516) — SF complete, fire MessageReceived
- `HandleFirstFrame(IsoTpFrame frame)` (line 522) — FF handler with length validation
- `HandleConsecutiveFrame(IsoTpFrame frame)` (line 649) — CF handler (lock-protected half + lock-released MessageReceived fire)
- `HandleConsecutiveFrameLocked(IsoTpFrame frame)` (line 683) — internal lock-protected half

**Depends on**:
- `_config` (DI, main)
- `_rxLock` + `_rxInProgress` + `_rxBuffer` + `_rxExpectedLength` + `_rxReceivedLength` + `_rxExpectedSequence` (state, main)
- `_logger` (DI, main)
- `LogIsoTpFfLengthTooLarge` (Flow G, cross-partial)
- `LogIsoTpHandlerFailed` (Flow G, cross-partial)
- `CancelReceiveWatchdog` (Flow E, cross-partial)
- `StartReceiveWatchdog` (Flow E, cross-partial)
- `SendFlowControl` (Flow F, cross-partial)
- `MessageReceived` event (main)

**File**: `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer/ReceiveFlow.cs`
**Required usings**: minimal

### Flow E — Watchdog (~110 LoC)
Owns reassembly timeout mechanism — the v1.2.13 PATCH Item 1 machinery (WatchdogHandle nested class + StartReceiveWatchdog + CancelReceiveWatchdog).

**Methods**:
- `StartReceiveWatchdog(int expectedGeneration)` (line 574)
- `CancelReceiveWatchdog()` (line 623)

**Nested class** (moves WITH the watchdog methods):
- `WatchdogHandle` (line 128) — sealed nested class with `Cts` + `Generation` + `RefCount`

**Depends on**:
- `_rxLock` + `_rxInProgress` + `_rxBuffer` (state, main)
- `_receiveTimeout` (state, main)
- `_watchdogDisposalDeferredCount` (test counter, main)

**State fields that co-locate per W8 R3 lesson** (read/written only here):
- `_rxWatchdog` (line 111) — moved to Flow E

**File**: `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer/WatchdogFlow.cs`
**Required usings**: `System.Threading`

### Flow F — FlowControl (~30 LoC, smallest)
Owns TX-side FC parsing + RX-side FC sending.

**Methods**:
- `HandleFlowControl(IsoTpFrame frame)` (line 723) — TX-side: parse incoming FC, update _txBlockSize/_txStMin/_txWaitingForFc
- `SendFlowControl()` (line 736) — RX-side: send FC frame (BS=0, STmin=0)

**Depends on**:
- `_txLock` + `_txWaitingForFc` + `_txBlockSize` + `_txStMin` (state, main)
- `_config` (DI, main)
- `SendCanFrame` (Flow B, cross-partial)

**File**: `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer/FlowControlFlow.cs`
**Required usings**: minimal

### Flow G — Logging (~30 LoC)
Owns the 3 `[LoggerMessage]` source-gen partial methods (single source of truth for event ids 3001/3002/3003).

**Methods** (move verbatim — including attribute decorations):
- `LogIsoTpSendFailed(ILogger, Exception, uint)` (line 771) — event id 3001
- `LogIsoTpHandlerFailed(ILogger, Exception, int)` (line 779) — event id 3002
- `LogIsoTpFfLengthTooLarge(ILogger, int, int)` (line 786) — event id 3003

**Depends on**: nothing (pure static logging helpers)

**File**: `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer/LoggingFlow.cs`
**Required usings**: `Microsoft.Extensions.Logging`

### Main file — fields + state + ctor + public properties + nested types (~150 LoC)

**Stays in main**:
- All `using` directives (lines 1-4)
- Namespace + class declaration with xmldoc (lines 6-19)
- Constants: `MaxSingleFramePayload`, `MaxMessageLength`, `DefaultFlowControlTimeout`, `DefaultReceiveTimeout` (lines 20-30)
- DI readonly fields: `_config`, `_sendFrame`, `_sendFrameAsync`, `_logger` (lines 32-35)
- Test counters: `SendFailureCount`, `TxWaitingForFcForTesting` property, `_watchdogDisposalDeferredCount` (lines 43-58, 119)
- TX state: `_sendGate`, `_cfCounter`, `_txLock`, `_txBlockSize`, `_txStMin`, `_txWaitingForFc` (lines 66-71, 160-163)
- RX state: `_rxLock`, `_rxBuffer`, `_rxExpectedLength`, `_rxReceivedLength`, `_rxExpectedSequence`, `_rxInProgress` (lines 74-79)
- Timeout fields: `_flowControlTimeout`, `_receiveTimeout` (lines 165-166)
- Public properties: `FlowControlTimeout`, `ReceiveTimeout` (lines 172-187)
- `MessageReceived` event (line 190)
- Sync ctor (line 195)
- Async ctor (line 215)
- `Dispose()` (line 305)
- `Reset()` (line 285)
- Nested `CanIdConfig` record (lines 793-806) — public type
- Namespace closing brace (line 807+)

**Moves to partials** (via Tasks 1-7):
- Flow F methods → `FlowControlFlow.cs`
- Flow G methods → `LoggingFlow.cs`
- Flow E methods + nested `WatchdogHandle` class + `_rxWatchdog` field → `WatchdogFlow.cs`
- Flow B methods → `SendFlow.cs`
- Flow D methods → `ReceiveFlow.cs`
- Flow C methods → `MultiFrameTransportFlow.cs`
- Flow A methods → `LifecycleFlow.cs`

## Architecture invariants (per W3-W8 patterns)

1. **Public API unchanged**: All public methods (`SendMessageAsync`, `ProcessFrame`, `Reset`, `Dispose`), properties (`FlowControlTimeout`, `ReceiveTimeout`), event (`MessageReceived`), constants, nested types (`CanIdConfig` record) stay in their original locations.
2. **partial-class visibility**: private methods, private fields, internal static partial methods (`LogIsoTpSendFailed`, etc.) are visible across partial files.
3. **State stays close to its reader/writer**: `_rxWatchdog` (test counter moved with it — actually `_watchdogDisposalDeferredCount` stays in main since it's read by tests via `[InternalsVisibleTo]`) co-locates with `WatchdogFlow` (its only consumer) per W8 R3 lesson.
4. **No new files outside the established directory**: `IsoTpLayer/` is a sibling directory.
5. **Nested `CanIdConfig` record stays in main** — it's a public type in the API surface, like W8's `TraceChartStatistics`.

## Verification

- `dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore`: 0 errors
- `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~IsoTp"`: all tests pass without modification

## Risk notes

- **R1 (low)**: Missing `using` directives — per W3-W8 CONFIRMED lesson (14+ confirmations across W3-W8+W8.5). Pre-scan method bodies for type references. Hit 3 times during W8 (Tasks 2/3/5); expected to hit 1-3 times during W9.
- **R2 (low)**: Deletion script precision — per W3-W8+W8.5 CONFIRMED lessons. Apply correct formula `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker` (NOT `LoC_base + n markers` — per the new CONFIRMED plan-LoC-trajectory-table lesson from W8.5 PATCH).
- **R3 (low)**: `_rxWatchdog` field moves with `WatchdogFlow` per W8 R3 lesson extension. It is private, only read/written by `StartReceiveWatchdog` / `CancelReceiveWatchdog` / `WatchdogHandle.Cts` consumers, and visible to tests via `[InternalsVisibleTo("PeakCan.Host.Core.Tests")]`. partial-class visibility makes this transparent.
- **R4 (low)**: `WatchdogFlow` has multiple cross-partial callers (ReceiveFlow → StartReceiveWatchdog/CancelReceiveWatchdog) per W8 R4 lesson. Plain invocations via partial-class visibility.

## Out of scope (explicit YAGNI)

- **No facade pattern**: W3-W8+W8.5 confirmed direct partial-class visibility is sufficient.
- **No sub-class creation**: IsoTpLayer stays a single class.
- **No test changes**: All existing tests stay unmodified.
- **No XAML changes**: N/A (Core layer, no UI).
- **No public/internal API surface change**: No new public methods, no removed methods, no renamed members.

## Tasks remaining (preview for the plan)

1. **Task 1**: Extract Flow F (FlowControl) — smallest (30 LoC), validates tooling.
2. **Task 2**: Extract Flow G (Logging) — 3 static partial methods.
3. **Task 3**: Extract Flow E (Watchdog) — 2 methods + nested class + 1 private field.
4. **Task 4**: Extract Flow B (Send) — 4 methods.
5. **Task 5**: Extract Flow D (Receive) — 5 methods.
6. **Task 6**: Extract Flow C (MultiFrameTransport) — 3 methods, largest.
7. **Task 7**: Extract Flow A (Lifecycle) — 4 methods + CanIdConfig record.
8. **Task 8**: Bump version v3.23.1 → v3.24.0 + write release notes (MINOR ship commit).
9. **Task 9**: Tier-3 push + tag + GH release.

Total: 9 tasks, ~8 source commits (1 per flow + 1 ship).

## Decision log

- **D1**: 7 partials with descriptive names (Lifecycle/Send/MultiFrameTransport/Receive/FlowControl/Watchdog/Logging) — same W3-W8 naming pattern adapted to Core layer.
- **D2**: Same W3-W8 pattern (no facade, no sub-classes).
- **D3**: Branch name `feature/w9-isotp-layer-god-class`.
- **D4**: Order tasks smallest-first: F (30) → G (30) → E (110) → B (95) → D (110) → C (165) → A (155). F + G validate deletion-script pattern on the smallest slices first; C goes second-to-last (largest, depends on B's `SendCanFrameAsync` being in place); A goes last (lifecycle, the most xmldoc-heavy).
- **D5**: `_rxWatchdog` co-locates with `WatchdogFlow` per W8 R3 lesson extension.
- **D6**: Nested `CanIdConfig` record + nested `WatchdogHandle` class behavior: `CanIdConfig` stays in main (public API surface), `WatchdogHandle` moves with `WatchdogFlow` (only used by Watchdog methods).
- **D7**: Plan LoC-trajectory-table formula explicitly applied per W8.5 PATCH CONFIRMED lesson: `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`.

## Closing milestone context

This is the **7th god-class refactor** in the project, and the **first in Core layer** (W3-W8 were all App layer). It extends the partial-class split pattern to a non-MVVM class with nested types and `[LoggerMessage]` source-gen methods, providing additional evidence for the W8 R3 (partial-class-with-state-fields) and R4 (cross-partial-method-calls) CANDIDATE lessons.