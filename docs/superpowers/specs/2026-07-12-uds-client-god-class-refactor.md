# W12 Spec — UdsClient god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.Core/Uds/UdsClient.cs` from 704 LoC to ~145 LoC by extracting 5 logical flow groups into partial-class files. Pure mechanical refactor — zero behavioral change, zero test changes, zero API surface change.

**Architecture:** Same W3-W11 partial-class split pattern, applied to a Core layer **instance** class implementing `IDisposable`. The outer `UdsClient` class becomes `partial`. Each partial file owns one logical flow group of methods. Main file keeps the class declaration, fields, properties, 3 ctors (state-ownership), `PublicOnMessageReceivedForTesting` xmldoc (test-seam comment block), and the `DiagnosticSessionResponse` record at end-of-file (per W11 D5 sister-lesson: type declarations that the inner state references stay with their only constructor's class declaration).

**Tech Stack:** C# .NET 10, Core layer (no WPF/MVVM). Git with LF line endings (per v3.18.0 PATCH `.gitattributes`).

## Global Constraints

- **Public API unchanged.** No method signatures, properties, exceptions, or nested types move.
- **partial-class visibility.** All private methods + private fields visible across partial files; cross-flow calls stay as plain invocations.
- **Test coverage unchanged.** No tests added, removed, or modified. The `internal Action? OnP2TimeoutFiredForTesting` test seam + `internal void PublicOnMessageReceivedForTesting` both stay (move with their consumer to TransportFlow).
- **Line-ending normalized to LF.** Per v3.18.0 PATCH `.gitattributes` enforcement.
- **No behavioral change.** Every method body, xmldoc, comment, and whitespace moves verbatim.
- **No version bump until Task 6.** Tasks 1-5 keep `src/Directory.Build.props` at v3.26.0. Task 6 bumps to v3.27.0.
- **Branch**: `feature/w12-uds-client-god-class` (created from `main` @ `17d7011` v3.26.0).
- **Spec**: this file.

---

## Current state (704 LoC)

`src/PeakCan.Host.Core/Uds/UdsClient.cs` (v3.26.0 HEAD) has:
- 1 instance `public class UdsClient : IDisposable` with **25 methods** (3 ctors + 7 wire/transport + 13 UDS services + 2 lifecycle façades) + 1 record
- 6 private readonly fields + 2 private volatile-simulation fields + 2 properties + 1 internal hook
- Total: ~760 LoC accounting for xmldoc + blank lines (raw 704 LoC; ASCII LoC counter computes to 696 LoC for `wc -l`)

Threshold per `automotive-coding-standards-file-size.md`: 800 LoC ceiling. UdsClient is at **88.0%** of ceiling (87.9% by `wc -l`). Major god-class refactor candidate per W8 closeout scan.

## Target state (~145 LoC main + 5 partials)

```
src/PeakCan.Host.Core/Uds/UdsClient.cs                                       # main file, ~145 LoC after Task 5
src/PeakCan.Host.Core/Uds/UdsClient/                                          # NEW directory
  TransportFlow.cs                                                             # Task 1 — wire layer + Rx + Dispose + test seam (~165 LoC, largest)
  SessionFlow.cs                                                               # Task 2 — 0x10 (DiagnosticSessionControl) + 0x11 (EcuReset ×2) + S3 keepalive façades (~80 LoC)
  DataIOFlow.cs                                                                # Task 3 — 0x22 + 0x2E + 0x19 + 0x14 (~80 LoC)
  SecurityFlow.cs                                                              # Task 4 — 0x27 × 2 overloads (~145 LoC)
  TransferFlow.cs                                                              # Task 5 — 0x3E + 0x31 + 0x34 + 0x36 + 0x37 (~145 LoC)
docs/superpowers/plans/2026-07-12-uds-client-god-class-refactor.md   # NEW in Task 0 (plan written alongside spec)
docs/release-notes-v3.27.0.md                                              # NEW in Task 6
```

**Net reduction**: 704 → ~145 LoC main file (-559 LoC, -79.4%); total lines unchanged (still ~704 across main + partials).

## Flow boundaries

All flows are instance methods on `UdsClient`. Each partial-class file declares `public partial class UdsClient { ... }` and adds the flow's methods + any private helpers (none expected — UdsClient is leaf-like, all helpers are one-line in-method).

### Flow — Transport (wire + Rx + lifecycle + test seam) (~165 LoC)

**Methods** (all require raw wire/Rx/disposal coupling — must move together):
- `public virtual async Task<byte[]> SendRequestAsync(byte, byte[]?, CancellationToken)` — line 152 — wire-level entry point. Locks `_requestLock` then delegates to `SendRequestInternalAsync`. **Public API surface** (virtual per v1.2.14 PATCH Item 4).
- `private async Task<byte[]> SendRequestInternalAsync(byte[], CancellationToken)` — line 578 — ISO-TP send + TCS await + P2 timeout registration + disposal ordering.
- `private void OnP2TimeoutFired()` — line 629 — P2 timeout callback wired into `_responseCts.Token.Register`.
- `private void OnMessageReceived(byte[])` — line 635 — ISO-TP `MessageReceived` event handler; C-8 SID echo validation + 0x7F NRC dispatch + 0x78 NRC P2* extension. Rx path.
- `internal void PublicOnMessageReceivedForTesting(byte[])` — line 695 — test seam for `OnMessageReceived`.
- `public void Dispose()` — line 569 — unhook `_isoTp.MessageReceived` + dispose `_requestLock` + dispose `_responseCts` + `Session.Dispose()`.

**Rationale for grouping**: SendRequest / SendRequestInternal / OnP2TimeoutFired / OnMessageReceived / PublicOnMessageReceivedForTesting share the `_responseTcs` + `_responseCts` + `_pendingRequestSid` + `_requestLock` volatile-simulation state. Splitting these across partial files would scatter tightly-coupled transport primitives across 3+ partials. The `Dispose` method closes over `_isoTp.MessageReceived -= OnMessageReceived` — must stay with the subscription it undoes.

**Depends on**:
- `_isoTp` (main field)
- `_timer` (main field)
- `_requestLock` (main field)
- `_responseTcs` + `_responseCts` + `_pendingRequestSid` (main fields, volatile-accessed)
- `OnP2TimeoutFiredForTesting` (main internal hook)
- `Session.Dispose()` (UdsSession reference)
- `_isoTp.SendMessageAsync` (IsoTpLayer, partial-class namespace visible — same `PeakCan.Host.Core.Uds.IsoTp` usings already at top of main)

**Cross-flow callers** (all UDS services — stay as plain calls via partial-class visibility):
- All 13 UDS service methods (Flow B-G) call `SendRequestAsync`.

**File**: `src/PeakCan.Host.Core/Uds/UdsClient/TransportFlow.cs`
**Required usings**: `using System.Threading.Tasks;` (already in file via implicit global usings in .NET 10), `using System;` (Array.Copy — implicit), no new usings expected.

### Flow — SessionControl (0x10 + 0x11 + S3 keepalive façade) (~80 LoC)

**Methods**:
- `public virtual async Task<DiagnosticSessionResponse> DiagnosticSessionControlAsync(byte, CancellationToken)` — line 184 — 0x10. Parses `[sessionType, P2high, P2low, P2*high, P2*low]`. Sets `Session` + propagates negotiated P2/P2* to `_timer` (C-3 fix).
- `public virtual async Task<byte> EcuResetAsync(byte, CancellationToken)` — line 223 — 0x11 (byte overload).
- `public Task<byte> EcuResetAsync(UdsResetType, CancellationToken)` — line 235 — 0x11 (enum overload → delegates to byte overload). v1.3.0 MINOR Item 2/4.
- `public void StartTesterPresent(TimeSpan?)` — line 558 — delegates to `Session.StartS3KeepAlive(this, interval)`.
- `public void StopTesterPresent()` — line 564 — delegates to `Session.StopS3KeepAlive()`.

**Depends on**:
- `SendRequestAsync` (Flow A, cross-partial)
- `Session.SetSession` + `Session.ResetS3Timer` (UdsSession reference)
- `_timer.P2Timeout` + `_timer.P2StarTimeout` setprop (UdsTimer reference)
- `UdsResetType` enum (Core type)
- `DiagnosticSessionResponse` (own-file record, stays at end of main)

**Rationale for grouping**: `StartTesterPresent` / `StopTesterPresent` are 2-line façades that just delegate to `Session.StartS3KeepAlive(this, ...)` — the closest conceptual pair is TesterPresent (0x3E), which lives in TransferFlow. **However**, TesterPresent (0x3E) is a UDS *service* that hits the wire. The Start/Stop methods are *application-level façades* that schedule periodic TesterPresent calls inside `UdsSession`. Putting them with `DiagnosticSessionControlAsync` (which **sets session state**, propagates to `_timer`) keeps **state-mutating session operations together** rather than mixing wire-emit (0x3E) with non-wire façades. Decision recorded as **D2** below.

Cross-flow note: `StartTesterPresent` → `Session.StartS3KeepAlive(this, interval)` — `this` is the `UdsClient` instance because `UdsSession` calls back into the client's `TesterPresentAsync`. The callback references `TesterPresentAsync` in TransferFlow. **Acceptable** (partial-class visibility).

**File**: `src/PeakCan.Host.Core/Uds/UdsClient/SessionFlow.cs`
**Required usings**: minimal (existing imports cover this).

### Flow — DataIO + DTC + RoutineControl (0x22 + 0x2E + 0x19 + 0x14 + 0x31) — REJECTED

**Rejected grouping**: I considered merging 0x22/0x2E (DataIO) with 0x19/0x14 (DTC) with 0x31 (RoutineControl). Total ~190 LoC. **Rejected** because:
1. ISO 14229 service groupings are semantically distinct (data is not routine control).
2. 0x31 RoutineControl has its own `routineControlType` (Start/Stop/QueryResult) sub-function protocol that diverges from DataIO's simple `(did, data)` round-trips.
3. Better to keep Flow C small (DataIO+DTC) and Flow E (Transfer) which includes TesterPresent + RoutineControl + 3 transfer-data methods.

### Flow — DataIO + DTC (0x22 + 0x2E + 0x19 + 0x14) (~80 LoC)

**Methods**:
- `public virtual async Task<byte[]> ReadDataByIdentifierAsync(ushort, CancellationToken)` — line 244 — 0x22. Parses `[DIDhigh, DIDlow, data...]`.
- `public virtual async Task WriteDataByIdentifierAsync(ushort, byte[], CancellationToken)` — line 262 — 0x2E.
- `public virtual async Task<byte[]> ReadDtcInformationAsync(byte, byte, CancellationToken)` — line 536 — 0x19.
- `public virtual async Task ClearDiagnosticInformationAsync(uint, CancellationToken)` — line 547 — 0x14.

**Depends on**:
- `SendRequestAsync` (Flow A, cross-partial)

**File**: `src/PeakCan.Host.Core/Uds/UdsClient/DataIOFlow.cs`
**Required usings**: minimal.

### Flow — SecurityAccess (0x27 × 2 overloads) (~145 LoC)

**Methods**:
- `public virtual async Task<byte[]> SecurityAccessAsync(byte, byte[]?, CancellationToken)` — line 279 — 0x27 3-arg overload (RequestSeed → SendKey). Lockout check (v1.3.0 MINOR Item 1) + key=RequestSeed branch + key=SendKey branch + UdsNegativeResponseException catch with `RecordFailedAttempt` (v1.3.1 PATCH Item 1).
- `public virtual async Task<byte[]> SecurityAccessAsync(byte, CancellationToken)` — line 381 — 0x27 2-arg overload (full handshake via `IKeyDerivationAlgorithm`). v1.3.1 PATCH Item 2 fail-fast pre-check. Calls 3-arg overload twice (RequestSeed leg + SendKey leg).

**Depends on**:
- `SendRequestAsync` (Flow A, cross-partial)
- `_keyAlgorithm` (main field, nullable)
- `Security.IsLocked` + `Security.SetSeed` + `Security.SetAuthenticated` + `Security.ResetLockout` + `Security.RecordFailedAttempt` + `Security.RemainingLockoutDelay` (UdsSecurity reference)
- `IKeyDerivationAlgorithm.ComputeKey` (Core type)
- `UdsSecurityLockedException` (Core type, in same namespace)
- `KeyAlgorithmNotConfiguredException` (Core type)

**File**: `src/PeakCan.Host.Core/Uds/UdsClient/SecurityFlow.cs`
**Required usings**: minimal.

### Flow — TesterPresent + RoutineControl + Transfer (0x3E + 0x31 + 0x34 + 0x36 + 0x37) (~145 LoC)

**Methods**:
- `public virtual async Task TesterPresentAsync(CancellationToken)` — line 421 — 0x3E.
- `public virtual async Task<byte[]> RoutineControlAsync(byte, ushort, byte[]?, CancellationToken)` — line 435 — 0x31 (byte overload).
- `public Task<byte[]> RoutineControlAsync(RoutineControlType, ushort, byte[]?, CancellationToken)` — line 461 — 0x31 (enum overload → delegates to byte overload). v1.3.0 MINOR Item 3/4.
- `public async Task<int> RequestDownloadAsync(uint, uint, CancellationToken)` — line 473 — 0x34. C-7 fix response parsing.
- `public async Task TransferDataAsync(byte, byte[], CancellationToken)` — line 511 — 0x36.
- `public async Task RequestTransferExitAsync(CancellationToken)` — line 524 — 0x37.

**Depends on**:
- `SendRequestAsync` (Flow A, cross-partial)
- `Session.ResetS3Timer` (UdsSession reference)
- `RoutineControlType` enum (Core type)

**File**: `src/PeakCan.Host.Core/Uds/UdsClient/TransferFlow.cs`
**Required usings**: minimal.

### Main file — declaration + fields + properties + 3 ctors + sentinel (~145 LoC)

**Stays in main**:
- `using System.Collections.Concurrent;` (line 1, SemaphoreSlim concurrent semaphore)
- `using Microsoft.Extensions.Logging;` (line 2)
- `using PeakCan.Host.Core.Uds.IsoTp;` (line 3)
- Namespace declaration (line 5)
- Outer class xmldoc (lines 7-14)
- Outer class declaration `public class UdsClient : IDisposable` → `public partial class UdsClient : IDisposable` (line 15)
- 6 readonly fields + 2 volatile-simulation fields:
  - `_isoTp` (line 17)
  - `_timer` (line 18)
  - `_requestLock` (line 19)
  - `_responseTcs` + `_responseCts` (lines 31-32)
  - `_pendingRequestSid` (line 45)
  - `_keyAlgorithm` (line 51)
- `OnP2TimeoutFiredForTesting` internal hook (line 40)
- `Session` + `Security` properties (lines 54-57)
- 3 ctors (lines 68-136) — all 3 stay in main. State-ownership and DI seam are concentrated here (per W9 D6 + W11 D5 sister-lesson).
- `DiagnosticSessionResponse` record (lines 698-704) — type declaration at end of file stays.

**Moves to partials** (via Tasks 1-5):
- Flow A methods → `TransportFlow.cs`
- Flow B methods → `SessionFlow.cs`
- Flow C methods → `DataIOFlow.cs`
- Flow D methods → `SecurityFlow.cs`
- Flow E methods → `TransferFlow.cs`

## Architecture invariants (per W3-W11 patterns)

1. **Public API unchanged**: `SendRequestAsync` + 13 UDS service methods + 3 ctors + `StartTesterPresent` + `StopTesterPresent` + `Dispose` + `DiagnosticSessionResponse` record stay callable from the public surface. Their fully-qualified type identity (`UdsClient`) does not change.
2. **partial-class visibility**: private methods + private fields visible across partial files. `SendRequestAsync` (Flow A) callable from any flow's UDS service method via plain method invocation.
3. **State stays close to its owner**: `_isoTp` + `_timer` + `_requestLock` + `_responseTcs` + `_responseCts` + `_pendingRequestSid` + `_keyAlgorithm` + `OnP2TimeoutFiredForTesting` are all owned by `UdsClient`. They stay in main. Transport methods that need them get them via partial-class visibility.
4. **Ctor + test seam grouping**: All 3 ctors stay together in main (state initialization), mirroring W9 D6 (nested-class-declaration-with-ctor) and W11 D5 (DI composition root). `PublicOnMessageReceivedForTesting` moves to TransportFlow (its single consumer is the OnMessageReceived handler that it delegates to).
5. **No new files outside the established directory**: `UdsClient/` is a sibling to `UdsClient.cs` (matches `DbcParser/` precedent).
6. **Instance class pattern**: Unlike W10 DbcParser (static + nested `ParserState`), UdsClient is `instance sealed/instance IDisposable`. The partial mechanism works identically for instance classes (compiler merges all partial declarations into one class).

## Verification

- `dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore`: 0 errors
- `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~UdsClient"`: all tests pass without modification
- `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~Uds"`: all tests pass without modification
- `dotnet test --no-restore --nologo -c Debug`: full solution builds clean, no regressions

## Risk notes

- **R1 (low)**: Missing `using` directives — per W3-W11 CONFIRMED lesson (19+ confirmations). Pre-scan method bodies for type references not in `PeakCan.Host.Core.Uds` or `Microsoft.Extensions.Logging`. Likely no new usings because all flow methods only use ISO 14229 standard types within the namespace.
- **R2 (low)**: Deletion script line-count assertion — per W8.5 PATCH D7 CONFIRMED. Apply correct formula `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`.
- **R3 (low)**: `StartTesterPresent` cross-flow callback. `StartTesterPresent` (Flow B main, SessionFlow) → `Session.StartS3KeepAlive(this, interval)` → `this.TesterPresentAsync` (Flow A Transport or Flow E Transfer). Acceptable via partial-class visibility.
- **R4 (low)**: `SendRequestAsync` virtual override — test doubles may `override` it across partials. Confirmed valid C# (partial class methods inherit as one class; `override` works identically).

## Out of scope (explicit YAGNI)

- **No facade pattern**: W3-W11 CONFIRMED direct partial-class visibility is sufficient.
- **No sub-class creation**: UdsClient stays a single class.
- **No test changes**: All existing tests stay unmodified.
- **No public/internal API surface change**: No new public methods, no removed methods, no renamed members.
- **No extraction of `DiagnosticSessionResponse` record**: Stays at end of main file as a sibling public type (sister to the DbcParser `Result<T>` pattern).

## Tasks remaining (preview for the plan)

1. **Task 1**: Extract Flow A — Transport (wire + Rx + Dispose + test seam) — largest, has the volatile-simulation state access + disposal ordering. **Run first** to validate that the partial-modifier + cross-class visibility works for an instance class (W10 was static + nested class; W3-W9 were instance but no IDisposable).
2. **Task 2**: Extract Flow B — SessionFlow (0x10 + 0x11 + S3 keepalive façades).
3. **Task 3**: Extract Flow C — DataIOFlow (0x22 + 0x2E + 0x19 + 0x14).
4. **Task 4**: Extract Flow D — SecurityFlow (0x27 × 2 overloads).
5. **Task 5**: Extract Flow E — TransferFlow (0x3E + 0x31 + 0x34 + 0x36 + 0x37).
6. **Task 6**: Bump version v3.26.0 → v3.27.0 + write release notes (MINOR ship commit).
7. **Task 7**: Tier-3 push + tag + GH release.

Total: 7 tasks, ~6 source commits (1 per flow + 1 ship).

## Decision log

- **D1**: 5 partials with descriptive ISO 14229 service-grouping names (Transport/Session/DataIO/Security/Transfer).
- **D2**: Same W3-W11 pattern (no facade, no sub-classes). StartTesterPresent/StopTesterPresent S3 keepalive façades stay with SessionFlow (Flow B) rather than TransferFlow (Flow E) — rationale: state-mutating session operations grouped together, not mixed with wire-emit.
- **D3**: Branch name `feature/w12-uds-client-god-class`.
- **D4**: Order tasks: A (Transport, largest, validates instance-class pattern) → B (Session, small) → C (DataIO, small) → D (Security, 145 LoC) → E (Transfer, 145 LoC). A first to extract the tightest-coupled methods (volatile simulation + disposal ordering); B+C next (state-mutating + simple round-trips); D+E last (the two fat flows).
- **D5**: All 3 ctors stay in main file (state-ownership + DI seam). `PublicOnMessageReceivedForTesting` moves to TransportFlow. `DiagnosticSessionResponse` record stays at end of main.
- **D6**: Plan LoC-trajectory-table formula explicitly applied per W8.5 PATCH CONFIRMED: `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`.
- **D7**: Treat W11's helper-extract pattern as **available but not required**: if any Transport method's body exceeds 50 LoC after extraction and it has a clear private helper split, apply W11 R3 mitigation (helper extracted to TransportFlow). Foreseen candidates: `SendRequestInternalAsync` (47 LoC raw, OK as-is) + `OnMessageReceived` (51 LoC raw, borderline). Decision deferred to Plan phase; if `OnMessageReceived` warrants split, do it via W11 R3 pattern in Task 1.

## Closing milestone context

This is the **9th god-class refactor** in the project. UdsClient is the **3rd Core layer** god-class (W9 IsoTpLayer + W10 DbcParser + W12 UdsClient). UdsClient is the **first Core-layer instance class** with IDisposable + virtual + internal-test-seam patterns. Validates the partial-class split pattern works for: instance class with IDisposable + virtual methods + internal test seams + nested types (sister of W3-W8 ViewModels, but on Core layer).

If W12 ships + tests pass + lesson confirmations hold, W12.5 vault-only PATCH (lesson-promotion) and W13 (next candidate: ScriptEngine.cs 548 LoC / AscParser.cs 513 LoC / ReplayTimeline.cs 469 LoC) become natural next steps.
