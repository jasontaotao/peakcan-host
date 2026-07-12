# W18 Spec — PeakCanChannel god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs` from 389 LoC to ~250 LoC by extracting 2 logical flow groups into partial-class files.

**Architecture:** Sister pattern to W3-W17. The Infrastructure-layer `public sealed partial class PeakCanChannel : ICanChannel` (already partial at line 65, modifier pre-existed). Adds 2 NEW partial-class files in `src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel/`. Each partial owns one logical flow group. Main file keeps 3 readonly fields + 1 mutable `_gate` field + Id/IsConnected properties + ctor + ConnectAsync + DisconnectAsync + WriteAsync + DisposeAsync + 5 `[LoggerMessage]` partials.

**Tech Stack:** C# .NET 10, Infrastructure layer (raw PEAK PCANBasic DLL interop). The class wraps native PEAK SDK calls (`TPCANMsg`, `TPCANTimestamp`, `TPCANMsgFD`, `TPCANStatus`, `TPCANBaudrate`, `PCANBasic.Initialize` etc.) for read/write/connect/disconnect against the PEAK CAN hardware.

## Global Constraints

- **Public API unchanged.** `ICanChannel` implementation unchanged — callers (`ChannelRouter`, `ChannelSendSink`, `ReplayService`) see identical behavior.
- **partial-class visibility.** All private methods + private fields visible across partial files.
- **Test coverage unchanged.** No tests added, removed, or modified.
- **Line-ending normalized to LF.**
- **No behavioral change.** Every method body, xmldoc, comment, and whitespace moves verbatim.
- **No version bump until Task 3.** Tasks 1-2 keep `src/Directory.Build.props` at v3.31.1. Task 3 bumps to v3.32.0.
- **Branch**: `feature/w18-peak-can-channel-god-class` (created from `main` @ `54bb3e9` v3.31.1).

---

## Current state (389 LoC)

`src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs` (v3.31.1 HEAD) has:
- 1 `public sealed partial class PeakCanChannel : ICanChannel` with 16 methods:
  - Public API: `ConnectAsync` + `DisconnectAsync` + `WriteAsync` + `DisposeAsync` + 2 events + 2 properties
  - Private internals: `ReadLoopAsync` (75 LoC, largest) + `SafeEmitReadLoopError` + `EmitClassic` + `EmitFd` + `MakeError` + `ResolveClassicCode` + 5 `[LoggerMessage]` partials
- 3 readonly fields: `_handle` (ushort, native handle) + `_gate` (ChannelConnectGate) + `_logger` + `_reader`
- 1 `internal const MaxConsecutiveReadFailures = 100`
- 1 `private static readonly int[] ReadLoopBackoffMs = { 1, 10, 50 }` (backoff configuration)
- Implements `ICanChannel` + 2 events (`FrameReceived`, `ReadLoopError`)

Threshold per `automotive-coding-standards-file-size.md`: 800 LoC ceiling. PeakCanChannel at **48.6%** of ceiling.

**Native binding**: PEAK PCANBasic unmanaged DLL (`PeakCAN` reference). Sister of W14 ScriptEngine (V8 native binding). The class manages:
- `PCANBasic.Initialize(handle, baud_code)` + `PCANBasic.Uninitialize(handle)` lifecycle
- `PCANBasic.Read(handle, out msg, out ts)` + `PCANBasic.ReadFD(handle, out msgFD, out ts)` read loop with reconnection + backoff
- `PCANBasic.Write(handle, ref msg)` write
- Frame type marshalling (`TPCANMsg` classic vs `TPCANMsgFD` FD)

## Target state (~250 LoC main + 2 partials)

```
src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs                # main file, ~250 LoC after Task 2
src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel/                  # NEW directory
  ReadLoopFlow.cs                                                     # Task 1 -- ReadLoopAsync (largest, 75 LoC) + SafeEmitReadLoopError (~85 LoC)
  NativeBindings.cs                                                  # Task 2 -- EmitClassic + EmitFd + MakeError + ResolveClassicCode (~50 LoC)
docs/superpowers/plans/2026-07-12-peak-can-channel-god-class-refactor.md  # NEW in Task 0
docs/release-notes-v3.32.0.md                                         # NEW in Task 3
```

**Net reduction**: 389 → ~250 LoC main file (-139 LoC, -35.7%); total lines unchanged (~389 across main + 2 partials).

## Flow boundaries

### Flow — Main file (declaration + state + connect/write + 5 LoggerMessage partials)

**Stays in main**:
- `using` block (lines 1-15)
- Namespace + class xmldoc (lines 60-64)
- Outer class declaration (line 65) — already `public sealed partial`, no change
- Static config (lines 67-78): `ReadLoopBackoffMs` array + `MaxConsecutiveReadFailures` const
- 4 readonly fields (lines 80-83): `_handle` + `_gate` + `_logger` + `_reader`
- 2 properties (lines 85-86): `Id` + `IsConnected`
- 2 events (lines 87-95): `FrameReceived` + `ReadLoopError`
- 1 ctor (lines 97-107)
- 4 lifecycle methods: `ConnectAsync` (109-158) + `DisconnectAsync` (160-172) + `WriteAsync` (174-220) + `DisposeAsync` (222-227)
- 5 `[LoggerMessage]` partial declarations (lines 305-334) — sister of W10+W11+W12+W13+W14+W15 source-generator scope

### Flow A — ReadLoopFlow (~85 LoC, NEW)

**Methods**:
- `private async Task ReadLoopAsync(CancellationToken ct)` (lines 229-303) — **75 LoC, largest method** with read-loop + backoff + reconnection + disposal-tailing
- `private void SafeEmitReadLoopError(ReadLoopError err)` (lines 317-332) — error emission to subscribers

**Depends on**:
- 4 readonly fields + 1 mutable `_gate` (main fields, partial-class visible)
- `FrameReceived` event (main)
- `ReadLoopError` event (main)
- `_reader` (main, used by ReadLoopAsync's underlying reads)
- 3 `[LoggerMessage]` partials: `LogReadLoopException` + `LogReadLoopGivingUp` + `LogReadLoopSubscriberThrew` (main)
- `IPcanReader` interface (sibling file, partial-class visible via using directive on Infrastructure/Peak)

**Rationale for grouping**: `ReadLoopAsync` (75 LoC) + `SafeEmitReadLoopError` (~15 LoC) share the read-loop's event-emission + error-handling logic. `ReadLoopAsync` calls `SafeEmitReadLoopError` to surface read failures to subscribers. Single-purpose cluster (state machine: connect → read loop → backoff on failure → retry → emit → dispose). Per W14 D8 sister-principle: a single 75 LoC method stays inline (helper-extraction would require changing the method shape).

### Flow B — NativeBindings (~50 LoC, NEW)

**Methods**:
- `private void EmitClassic(TPCANMsg m, TPCANTimestamp ts)` (lines 336-349) — marshals PEAK classic CAN frame to `CanFrame` + timestamp conversion + event emit
- `private void EmitFd(TPCANMsgFD m, ulong tsMicroseconds)` (lines 351-365) — FD variant
- `private static Result<Unit> MakeError(TPCANStatus s)` (lines 367-379) — convert PEAK status code to `Result<Unit>`
- `private static TPCANBaudrate? ResolveClassicCode(BaudRate baud)` (lines 381-389) — name → enum mapping

**Depends on**:
- `_reader` (main field, reads via `IPcanReader`)
- `FrameReceived` event (main)
- All `TPCAN*` types from `PeakCAN` P/Invoke (sister of W14's ClearScript V8 interop)
- `CanFrame` + `BaudRate` from `PeakCan.Host.Core` (sister file)

**Rationale for grouping**: All 4 helpers are static (3 of 4) or instance-but-stateless (Emit*); all touch PEAK SDK native-binding types (`TPCAN*`); cluster isolates the interop knowledge to one partial. Sister of W14's `CreateEngineFlow.cs` (V8 + host-object wiring isolation).

## Architecture invariants (per W3-W17 patterns)

1. **Public API unchanged.** `ICanChannel` implementation unchanged — same public method/property/event signatures.
2. **partial-class visibility** works for P/Invoke static imports — sibling partial files declare `using PeakCan.*;` for cross-namespace type access.
3. **State ownership preserved**: 4 readonly fields + 1 mutable `_gate` + `_handle` stay in main per W11 D5 + W12 D5 + W14 D5 sister.
4. **5 LoggerMessage partials stay in main** per W10+W11+W12+W13+W14+W15+W17 sister.
5. **Native binding lifecycle**: `_reader.Dispose()` + `_handle` cleanup preserved verbatim (read-loop's `finally` block + `DisposeAsync`). Verbatim extraction preserves interop sequence.
6. **PEAK SDK interop isolation**: all `TPCAN*`-touching helpers in NativeBindings partial — sister of W14's `CreateEngineFlow.cs` (V8 isolation).
7. **ReadLoopAsync largest-method stays inline** per W12 D7 + W14 D8 sister-principle (one-method-one-purpose = single read-loop; body is one continuous try/catch/finally block).

## Verification

- `dotnet build src/PeakCan.Host.Infrastructure/PeakCan.Host.Infrastructure.csproj -c Debug --no-restore`: 0 errors
- `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~Channel"`: all tests pass without modification (ChannelRouter tests + ICanChannel contract tests)
- `dotnet test --no-restore --nologo -c Debug`: full solution builds clean, no regressions

## Risk notes

- **R1 (medium)**: Missing `using` directives — **First time working with PEAK SDK namespace** in partial-extraction (W14 was ClearScript; this is PEAK PCANBasic P/Invoke). Likely need `using Peak.Can.Basic;` (the PEAK C# wrapper) in BOTH Flow A (ReadLoop uses `_reader.Read`) and Flow B (`EmitClassic`/`EmitFd`/`MakeError` use `TPCAN*` types). Apply W11 R1 lesson (#11 19th+ confirmation): pre-scan all source types + add `using` to each partial file's top per need.
- **R2 (low)**: LoC formula — per W8.5 D7 CONFIRMED 15-locked. Use W13 T1 2/3 loose-assertion pattern + wc-l-splitlines CONFIRMED lesson.
- **R3 (low)**: PEAK SDK native binding lifecycle — `_reader.Dispose()` + handle cleanup preserved verbatim (read-loop `finally` block). Mirrors W14 R5 sister (V8 binding survived partial split).
- **R4 (very low)**: `internal const MaxConsecutiveReadFailures = 100` + `private static readonly int[] ReadLoopBackoffMs` static config — stay in main per W12 R3 sister (static-init-pattern + const stays with class declaration).

## Out of scope (explicit YAGNI)

- **No facade pattern**: W3-W17 CONFIRMED direct partial-class visibility is sufficient.
- **No test changes**: All existing tests stay unmodified.
- **No public/internal API surface change**.
- **No ChannelRouter refactor**: W18 scoped to PeakCanChannel only. ChannelRouter (305 LoC) is the next candidate but not in W18 scope.

## Tasks remaining (preview for the plan)

1. **Task 1**: Extract Flow A — `ReadLoopFlow` (ReadLoopAsync + SafeEmitReadLoopError, ~85 LoC, largest method).
2. **Task 2**: Extract Flow B — `NativeBindings` (Emit* + MakeError + ResolveClassicCode, ~50 LoC, native-interop isolation).
3. **Task 3**: Bump version v3.31.1 → v3.32.0 + write release notes (MINOR ship commit).
4. **Task 4**: Tier-3 push + tag + GH release.

Total: 4 tasks, ~3 source commits (T1 + T2 + ship).

## Decision log

- **D1**: 2 partials (`ReadLoopFlow` + `NativeBindings`). Subdirectory pattern (NOT sibling-file like W16 — W14/W15 sister-directory-pattern preferred for new layer exploration).
- **D2**: 5 LoggerMessage partials stay in main per W10+W11+W12+W13+W14+W15+W17 sister.
- **D3**: Branch name `feature/w18-peak-can-channel-god-class`.
- **D4**: Order tasks: **A (ReadLoopFlow, largest 85 LoC) → B (NativeBindings, ~50 LoC)** — A first to validate the read-loop + disposal-tailing + emitter-coupling shape; B second (native-interop isolation).
- **D5**: `ReadLoopAsync` 75 LoC stays inline per W12 D7 + W14 D8 sister-principle (single read-loop, one continuous try/catch/finally).
- **D6**: Plan LoC-trajectory-table formula explicitly applied per W8.5 D7 CONFIRMED (16-locked across W12-W16 + W17 vault-only PATCH).
- **D7**: 6 sister-lesson-candidates to monitor for 2/3→3/3 promotion during W18:
  - `xmldoc-grep-test-breaks-when-partial-class-split-moves-the-overloaded-method-xmldoc-into-different-file` (1/3, W12 T4)
  - `execution-lifecycle-cluster-must-not-be-split-across-partials` (2/3, W3 + W14)
  - `internal-sealed-partial-class-modifier-does-not-constrain-partial-extraction-mechanics` (1/3, W15)
  - `replay-view-model-manual-properties-with-partial-class-visibility-into-service-field` (1/3, W16 T1)
  - `sibling-file-pattern-vs-subdirectory-is-correct-when-predecessors-set-precedent` (1/3, W16 T2)
  - `peak-can-channel-infrastructure-layer-native-binding-survives-partial-extraction` (NEW 1/3 — W18 T1)

## Closing milestone context

This is the **14th god-class refactor** in the project (W3-W18 series). PeakCanChannel is the **1st Infrastructure layer** god-class (all prior 13 W3-W17 were App or Core). Specifically it's `Infrastructure/Peak/PeakCanChannel.cs` — raw PEAK PCANBasic P/Invoke wrapper at Infrastructure layer. Sister of W14 ScriptEngine (native binding lifecycle) but at a different layer + different runtime. Validates the partial-class split pattern works for: Infrastructure-layer native-binding class + `[LoggerMessage]`-decorated private methods + event-emission coupling.

If W18 ships + tests pass + lesson confirmations hold, next steps are W18.5 vault-only PATCH (lesson promotion if 6 candidates get 1 more observation) OR W19 (next candidate: ChannelRouter 305 LoC, also Infrastructure/Channel layer).
