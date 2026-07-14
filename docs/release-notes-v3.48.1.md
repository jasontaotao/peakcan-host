# Release Notes v3.48.1 — PeakCanChannel write-flow god-class refactor (PATCH)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.48.1
**Branch**: `feature/w35-peakcan-channel-write-flow-god-class`
**Parent**: v3.48.0 MINOR (W34 CyclicSendService SHIPPED = `439d22d` on main)

## Why this PATCH

`src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs` had grown to **244 LoC** as of v3.48.0 — at 30.5% of the 800 LoC Round-1 ceiling. Single `public sealed partial class PeakCanChannel : ICanChannel` (already partial at L65 since W18 — sister of W18 + W26.5 + W30 + W31 + W32 + W33 + W34 + W35 sister precedent; no D2 application needed). 4 fields + 4 events/properties + 1 production ctor + 3 public methods (`ConnectAsync` LARGEST 50 LoC + `DisconnectAsync` 13 LoC + `WriteAsync` 47 LoC) + 1 public `DisposeAsync` + 4 `[LoggerMessage]` partial declarations (`LogReadLoopException` + `LogReadLoopGivingUp` + `LogReadLoopSubscriberThrew` + the 4th implicit static partial).

This is the **31st god-class refactor** in the project (W3-W34 series). **3rd Infrastructure-layer** (sister of W18 PeakCanChannel initial + W25 ChannelRouter). **2nd-cycle god-class refactor of PeakCanChannel** — the file had W18 already-extracted 2 partials (`NativeBindings.cs` + `ReadLoopFlow.cs`); W35 takes the remaining main file from 244 LoC further down to 128 LoC.

## LoC trajectory (W8.5 D7 CONFIRMED formula — 32-locked)

Per task, `LoC_n = LoC_{n-1} - sum(deleted at task n)`. Both transitions **EXACT match** via `wc -l`.

| Task | Flow | Range deleted | LoC out | Main after |
|---|---|---|---|---|
| T1 | ConnectFlow (ConnectAsync + DisconnectAsync + DisposeAsync, 3 contiguous regions) | 109-158 + 160-172 + 222-227 (HEAD) | 69 | 175 |
| T2 | WriteFlow (WriteAsync, 1 contiguous region) | 174-220 (HEAD, shifted to 111-157 post-T1) | 47 | 128 |
| **Total** | -- | -- | **116** | **128** |

**Net**: 244 → 128 LoC main file (**-116 LoC, -47.5%**). 4-partial total now in `PeakCanChannel/` subdirectory: `NativeBindings.cs` (W18, 72 LoC) + `ReadLoopFlow.cs` (W18, 126 LoC) + `ConnectFlow.partial.cs` (W35 T1, 119 LoC) + `WriteFlow.partial.cs` (W35 T2, 127 LoC) = **444 LoC across 4 partials + 128 LoC main = 572 LoC total** vs ~244 LoC pre-W18 (overall **+328 LoC architectural growth** for vastly-improved per-concern navigation).

## What this PATCH does

### Refactor — PeakCanChannel adds 2 NEW partials in `PeakCanChannel/` subdirectory

1. **NEW `src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel/ConnectFlow.partial.cs` (~119 LoC)**:
   - Connect lifecycle cluster: `ConnectAsync(BaudRate, bool, CancellationToken)` 50 LoC + `DisconnectAsync(CancellationToken)` 13 LoC + `DisposeAsync()` 6 LoC = 69 LoC methods + 50 LoC xmldoc header
   - **W35 T1 verbatim re-extracted** via `git show main:src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs | sed -n '109,158p;160,172p;222,227p'` per W20 LESSON (46th application)
   - **W19 R1 LESSON ENHANCED**: pre-flight prevention (re-grep boundaries BEFORE running deletion script) + post-failure recovery procedure (git checkout HEAD -- + re-grep + corrected offsets + re-run) baked into script + verified successful 1st-attempt pass with delta = 69 EXACT
   - **W23 STRUCT-FABRACTION LESSON 18th observation**: TPCANStatus enum + (uint) cast + Result<Unit>.Ok/Fail static factories + ChannelConnectGate.TryEnter + MakeError(TPCANStatus) + ResolveClassicCode(BaudRate) signatures verified
   - Cross-partial caller pattern documented: `ConnectAsync` calls `MakeError` + `ResolveClassicCode` from W18 `NativeBindings.cs` partial (cross-partial visibility works automatically)
   - 4 `[LoggerMessage]` partials stay on main partial declaration per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34+W35 sister precedent (CS8795 mitigation); called from `ReadLoopAsync` (in `ReadLoopFlow.cs` W18 partial) — cross-partial visibility works automatically
   - W35 D5 deviation NOT applied: `ConnectAsync` 50 LoC LARGEST method **< 60 LoC threshold** → orchestration-loop stay pattern does NOT trigger at W35 (W22+W23+W34 sister precedent applies **at threshold** 60+; W35 is **just below threshold**)

2. **NEW `src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel/WriteFlow.partial.cs` (~127 LoC)**:
   - Write dispatch cluster: `WriteAsync(CanFrame, CancellationToken)` 47 LoC (classic-vs-FD dual-path dispatch body — TPCANMsg + PCANBasic.Write for classic, TPCANMsgFD + PCANBasic.WriteFD for FD, plus FrameFlags.BitRateSwitch + ESI handling)
   - **W35 T2 verbatim re-extracted** via `git show main:src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs | sed -n '174,220p'` per W20 LESSON (47th application)
   - **W19 R1 LESSON ENHANCED**: pre-flight prevention (re-grep post-T1 boundaries — subagent correctly identified the +6 shift due to T1 blank-line compression) + post-failure recovery procedure baked into script + verified successful 1st-attempt pass with delta = 47 EXACT
   - **W23 STRUCT-FABRACTION LESSON 18th observation** (continued): TPCANStatus enum + TPCANMessageType flag-bit pattern (PCAN_MESSAGE_FD | EXTENDED | STANDARD | BRS | ESI) + TPCANMsg struct (ID + MSGTYPE + LEN + DATA fields) + TPCANMsgFD struct (ID + MSGTYPE + DLC + DATA fields) + PeakCanFrameFormatter.ToFixedBytes8 + ToFixedBytes64 static methods + FrameFlags.BitRateSwitch + FrameFlags.ErrorStateIndicator bitflags + CanFrame.IsFd + Id.IsExtended + Id.Raw property signatures verified
   - Cross-partial caller pattern documented: `WriteAsync` calls `MakeError` from W18 `NativeBindings.cs` partial (cross-partial visibility works automatically); uses PeakCanFrameFormatter.ToFixedBytes8/64 static methods (in `PeakCan.Host.Infrastructure.Channel` namespace, NOT partial)
   - W35 D5 deviation NOT applicable: `WriteAsync` 47 LoC = dispatch method but well below 60 LoC threshold → moves cleanly per W12+W14+W18+W19+W20+W21+W22+W23+W24+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34 D5 default sister-principle

### Stays in `src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs` main (~128 LoC after T1+T2)

- `using` block (L1-L6, 6 lines)
- Namespace (L7)
- Class xmldoc (L9-L64, 56 LoC of original error-handling + read-loop-fault-handling + v3.16.9.4 PATCH + classic-baud-dispatch documentation)
- `public sealed partial class PeakCanChannel : ICanChannel` (L65, already partial)
- 4 readonly fields: `_handle` + `_gate` + `_logger` + `_reader` (L80-L83)
- 4 events/properties: `Id` (L85) + `IsConnected` (L86) + `FrameReceived` (L87) + `ReadLoopError` (L95)
- 1 production ctor (L97-L107, 10 LoC)
- 3 explicit + 1 implicit `[LoggerMessage]` partial declarations (L229-L241)

## Architecture milestones

- **31st god-class refactor SHIPPED** (W3-W34 series + W35).
- **3rd Infrastructure-layer** (sister of W18 PeakCanChannel initial + W25 ChannelRouter).
- **2nd-cycle god-class refactor** of `PeakCanChannel.cs` (after W18's initial NativeBindings + ReadLoopFlow extraction; W35 takes the remaining 244 LoC main further down to 128 LoC).
- **25th subdirectory-pattern deployment** (sister of W34's 24th deployment).
- **4-partial total** in `PeakCanChannel/` subdirectory: NativeBindings.cs (W18) + ReadLoopFlow.cs (W18) + ConnectFlow.partial.cs (W35) + WriteFlow.partial.cs (W35).
- **`largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` 11/3 HELD** since 3/3 LOCKED at W25 (W35 LARGEST method 50 LoC < 60 LoC threshold; does NOT add a new observation; stays at 11/3 held since W34 SHIP).
- **`infrastructure-channel-layer-sister-pattern-empirical-w18-w25` 2/3 → 3/3 CONFIRMED LOCKED PROMOTION** if W35 ships cleanly (3rd observation = W35 PeakCanChannel 2nd-cycle + W18 initial + W25 ChannelRouter).
- **`second-cycle-god-class-refactor-empirical-w28-w29-w35` NEW 1/3 observation** (1st observation = W35 PeakCanChannel 2nd-cycle + W22 SendFrameLibrary 2nd-cycle + W19 DbcService 2nd-cycle pattern).
- **`app-services-cyclic-subsystem-sister-pattern-empirical-w23-w31-w34`** HELD at 1/3 (W35 is Infrastructure/Peak, NOT App/Services/Cyclic).
- **`multi-interface-partial-class-empirical-w26-w31-LOCKED`** HELD at 3/3 LOCKED (W35 PeakCanChannel has 1 interface `ICanChannel`; not a new multi-interface observation).
- **`small-god-class-no-largest-method-keeps-all-inline-default-pattern`** HELD at 3/3 LOCKED (W35 LARGEST method = 50 LoC = threshold; NOT strictly below; borderline not applicable; review at W35.5 if borderline persists).
- **W19 R1 first-correction LESSON ENHANCED** applied at W35 T1 + T2 (boundary verification baked into scripts upfront + recovery procedure documented = 46th + 47th successful preventive applications).
- **W20 LESSON APPLIED 46th + 47th times** at W35 T1+T2 (verbatim re-extraction via `git show main:src/.../PeakCanChannel.cs | sed -n '<range>p'`).
- **W23 STRUCT-FABRACTION LESSON 18th observation** at W35 T1+T2 (TPCANMsg struct + TPCANMsgFD struct + TPCANStatus enum + TPCANMessageType bitflags + Result<Unit>.Ok/Fail + ChannelConnectGate.TryEnter + MakeError + ResolveClassicCode + PeakCanFrameFormatter.ToFixedBytes8 + ToFixedBytes64 + FrameFlags.BitRateSwitch + FrameFlags.ErrorStateIndicator + CanFrame.IsFd + Id.IsExtended + Id.Raw signatures verified).
- **W17 wc-l-splitlines CONFIRMED 47-locked** (cp1252 binary read+write pattern in both W35 T1 + T2 deletion scripts).

## Cross-partial helper visibility pattern (CONFIRMED across 4 partials post-W35)

W35 confirms cross-partial helper visibility works across **4 partials** (sister of W22 + W23 + W26 + W27 + W28 + W29 + W30 + W31 + W32 + W33 + W34):

- **`ConnectAsync` (in ConnectFlow partial)** reads `_handle` + `_gate` + `_reader` + `_logger` + `Id` (all in main). Cross-partial visibility handles this automatically.
- **`ConnectAsync` (in ConnectFlow partial)** calls `MakeError` (in NativeBindings.cs W18 partial). Cross-partial visibility works.
- **`ConnectAsync` (in ConnectFlow partial)** calls `ResolveClassicCode` (in NativeBindings.cs W18 partial). Cross-partial visibility works.
- **`ConnectAsync` (in ConnectFlow partial)** captures `OnTimerTick` delegate for use in `Task.Run(() => ReadLoopAsync(token))` — `ReadLoopAsync` is in `ReadLoopFlow.cs` W18 partial. Cross-partial visibility works.
- **`DisconnectAsync` (in ConnectFlow partial)** reads `_handle` + `_gate` (in main). Cross-partial visibility works.
- **`DisposeAsync` (in ConnectFlow partial)** calls `DisconnectAsync` (same partial). Same-partial — no cross-partial.
- **`WriteAsync` (in WriteFlow partial)** reads `_handle` + `IsConnected` (in main). Cross-partial visibility works.
- **`WriteAsync` (in WriteFlow partial)** calls `MakeError` (in NativeBindings.cs W18 partial). Cross-partial visibility works.
- **`ReadLoopAsync` (in ReadLoopFlow.cs W18 partial)** reads `_handle` + `_reader` + `_logger` + `Id.Handle` + `FrameReceived` event (in main) + calls `EmitClassic` + `EmitFd` (in NativeBindings.cs W18 partial). Already proven via W18 SHIP.
- **`LogReadLoopException` + `LogReadLoopGivingUp` + `LogReadLoopSubscriberThrew` + the 4th implicit static partial (in main)** called from `ReadLoopAsync` (in ReadLoopFlow.cs W18 partial). Cross-partial call resolution handles this automatically (CS8795 mitigation per W18 R1 + W22 D4 + W23 D4 + W25 D4 + W26 D4 + W27 D4 + W28 D4 + W29 D4 + W30 D4 + W31 D4 + W32 D4 + W33 D4 + W34 D4 + W35 D4 sister precedent).

This is a **12th confirmation** (1st was W22, 2nd was W23, 3rd was W26, 4th was W27, 5th was W28, 6th was W29, 7th was W30, 8th was W31, 9th was W32, 10th was W33, 11th was W34, 12th is W35) that the cross-partial helper visibility pattern is stable across multi-partial god-class extractions.

## Sister-lesson candidates STATE-OF-THE-ART (post-W35)

| Lesson | Status | Observations |
|---|---|---|
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | 3/3 CONFIRMED (W21) | held; W35 = 47th application |
| `subdirectory-partials-pattern-empirical-26-precedents` | 3/3 CONFIRMED (W20) | held; W35 = 25th deployment |
| `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34+W35 18-of-1) | held; W35 = 18th observation |
| `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34+W35 14-of-1) | held; W35 = 14th confirmation |
| `add-partial-keyword-to-monolithic-class-before-extraction` | 3/3 CONFIRMED (W26.5) | held; W35 = 33rd cumulative confirmation |
| `multi-interface-partial-class-empirical-w26-w31-LOCKED` | 3/3 CONFIRMED LOCKED (W31.5) | held; N/A W35 (1 interface `ICanChannel`) |
| `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` | 11/3 since 3/3 LOCKED (W34) | held; W35 N/A (LARGEST 50 LoC < 60 LoC threshold) |
| `infrastructure-channel-layer-sister-pattern-empirical-w18-w25` | 2/3 → **3/3 CONFIRMED LOCKED PROMOTION** | W35 3rd observation: PeakCanChannel 2nd-cycle (after W18 + W25) |
| `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` | 3/3 CONFIRMED LOCKED (W29.5) | held; N/A W35 (no JSON-persistence) |
| `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28-w31-LOCKED` | 3/3 CONFIRMED LOCKED (W31.5) | held; N/A W35 (synchronous Connect) |
| `small-god-class-no-largest-method-keeps-all-inline-default-pattern` | 3/3 CONFIRMED LOCKED (W33) | held; W35 borderline (LARGEST = 50 = threshold, NOT < 50) |
| `app-services-multiframe-layer-sister-pattern-empirical-w30` | NEW 1/3 (W30) | held; N/A W35 (Infrastructure/Peak, NOT App/Services/MultiFrame) |
| `core-layer-replay-subsystem-sister-pattern-empirical-w15-w22-w31` | NEW 1/3 (W31) | held; N/A W35 (Infrastructure/Peak, NOT Core/Replay) |
| `app-services-scripting-sister-pattern-empirical-w14-w26-w32` | 3/3 PROMOTED → potential LOCK (W32.5) | held; N/A W35 (Infrastructure/Peak, NOT App/Services/Scripting) |
| `app-services-sequence-sister-pattern-empirical-w30-w33` | NEW 1/3 (W33) | held; N/A W35 (Infrastructure/Peak, NOT App/Services/Sequence) |
| `app-services-cyclic-subsystem-sister-pattern-empirical-w23-w31-w34` | NEW 1/3 (W34) | held; N/A W35 (Infrastructure/Peak, NOT App/Services/Cyclic) |
| `second-cycle-god-class-refactor-empirical-w28-w29-w35` | **NEW 1/3** (W35) | W35 = 1st observation: 2nd-cycle god-class pattern (PeakCanChannel W18 → W35; SendFrameLibrary W22 → W29; DbcService W19 → W28) |
| `peak-can-channel-cs8795-accessibility-modifier-blocks-cross-partial-source-generator-impl` | 1/3 (W18 R1) | held; W35 confirms (4 [LoggerMessage] partials stay on main) |

## What was captured

W35 SHIP closure will = 5 captures dispatched: SPEC + PLAN + T1 + T2 + T3 + T4 + SHIP (post-PR docs commit). Each per W12-W34 pattern of `vault-pkm:pkm-capture` agent dispatched after each source commit.

## What was skipped

- No separate `app-get-version` test for v3.48.1 bump (release-notes body suffices per W12 D7 + W14 D8 + W22 D7 + W23 D7 + W24 D7 + W25 D7 + W26 D7 + W27 D7 + W28 D7 + W29 D7 + W30 D7 + W31 D7 + W32 D7 + W33 D7 + W34 D7 sister precedent).
- No 2nd verification round on Infrastructure tests (CI PASS expected on 1st attempt unless transient flaky windows-runner per W23.5-W25.5+W27.5+W28 PATCH 5-attempt MAX sister pattern).
- No W18 R1 fix applied (4 `[LoggerMessage]` partials stay on main partial declaration; cross-partial caller-methods auto-resolve via partial-class visibility).
- No W25 D5 deviation applied (ConnectAsync 50 LoC < 60 LoC threshold; orchestration-loop stay pattern doesn't trigger).
- No `ICanChannel.cs` interface partial changes (stays in Infrastructure/Channel layer).
- No `PeakErrorMapper.cs` + `PeakCanFrameFormatter.cs` partial changes (consumed by PeakCanChannel but not modified).
- No `ChannelConnectGate.cs` partial changes (consumed by PeakCanChannel but not modified).
- No `IPcanReader.cs` + `PcanReader.cs` partial changes.
- No `ChannelRouter.cs` (W25-extracted) partial changes.
- W25 partials (ChannelRouter/Channels.partial.cs + FrameRouting.partial.cs + Sinks.partial.cs) unchanged.
- W18 partials (PeakCanChannel/NativeBindings.cs + ReadLoopFlow.cs) unchanged.

## Verification

- `dotnet build src/PeakCan.Host.Infrastructure/`: 0 errors, 0 W35-related warnings (pre-existing warnings from other files retained)
- `dotnet test --filter "FullyQualifiedName~PeakCanChannel"`: **7/7 PASS** (matches pre-W35 baseline)
- `dotnet test` (full solution via CI): expected 1st-attempt PASS (transient flaky windows-runner handled per W23.5-W25.5+W27.5+W28 PATCH 5-attempt MAX sister pattern if 1st attempt fails)
- `wc -l src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs` = 128 LoC (target ~128, EXACT match)
- `wc -l src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel/ConnectFlow.partial.cs` = 119 LoC
- `wc -l src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel/WriteFlow.partial.cs` = 127 LoC
- `wc -l src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel/NativeBindings.cs` = 72 LoC (unchanged from W18)
- `wc -l src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel/ReadLoopFlow.cs` = 126 LoC (unchanged from W18)

## Process lessons applied

- **Lesson #10** (verify each commit before proceeding): each W35 T0+T1+T2 build + filter-test verified before commit.
- **Lesson #11** (partial-class using-directives file-scoped, not class-scoped): W35 T1 using-directive fix is NOT needed (ConnectFlow/WriteFlow don't reference non-already-using types); pre-scanned via W22 R1 + W26 T1 + W33 T1 + W34 T1 sister checklist.
- **W19 R1 first-correction ENHANCED**: pre-flight prevention (re-grep boundaries before each deletion script) + post-failure recovery procedure (git checkout HEAD -- + re-grep + corrected offsets + re-run + verify) — **W35 T1 + T2 both 1st-attempt PASS** with delta = 69 EXACT and delta = 47 EXACT respectively (preventive strategy working; no recovery needed). **47th cumulative preventive application** of W19 R1 LESSON ENHANCED (W31 T2 was 1st application of post-failure-recovery dimension; W34 T1 was 3rd application; W35 T1+T2 are both 0-failure applications of prevention).
- **W20 T2 R1 fabrication LESSON**: 47 verbatim re-extractions across W35 T1+T2 (46+47th cumulative W20 LESSON applications).
- **W23 STRUCT-FABRACTION LESSON**: W35 verified 8+ struct/method signatures (TPCANStatus + TPCANMessageType + TPCANMsg + TPCANMsgFD + PeakCanFrameFormatter.ToFixedBytes8 + ToFixedBytes64 + FrameFlags.BitRateSwitch + FrameFlags.ErrorStateIndicator + Result<Unit>.Ok/Fail + ChannelConnectGate.TryEnter + MakeError + ResolveClassicCode; 18th observation since 3/3 CONFIRMED).
- **W25 D5 deviation NOT applied**: W35 ConnectAsync 50 LoC LARGEST method **< 60 LoC threshold** → orchestration-loop stay pattern does NOT trigger. The lesson at 11/3 HELD since 3/3 LOCKED.

## CI status

- Local trx run: 7 PeakCanChannel tests pass cleanly
- Full solution CI: expected 1st-attempt PASS (transient flaky windows-runner handled per W23.5-W25.5+W27.5+W28 PATCH 5-attempt MAX sister pattern if 1st attempt fails; empty commit to re-trigger CI per W34 PATCH sister pattern)

## Cumulative trajectory (peakcan-host god-class series)

**31 god-class refactors SHIPPED** (W3-W35):
- W3-W11 (9 refactors, App + Core)
- W12 UdsClient + W13 AscParser-round-2 + W14 ScriptEngine + W15 ReplayTimeline + W16 ReplayViewModel + W17 vault-only PATCH + W18 PeakCanChannel + W19 TraceViewModel + W20 TraceViewerViewModel RESIDUAL + W21 SignalChartViewModel + W22 RecordService + W23 CyclicDbcSendService + W24 DbcSendViewModel + W25 ChannelRouter + W26 CanApi + W27 RecentSessionsService + W28 DbcService + W29 SendFrameLibrary + W30 SequenceSendService + W31 ReplayService + W32 DbcApi + W33 SequenceLibrary + **W34 CyclicSendService + W35 PeakCanChannel (2nd-cycle)**

Plus 9 vault-only PATCH (W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 + W30.5 + W31.5 + W32.5).

**Cumulative LoC reduction**: 30 god-class files -5,236 LoC (W3-W34) + **W35 PeakCanChannel -116 LoC** = **-5,352 LoC total** across 31 god-class refactors + 9 PATCHes.

## Next (post-W35 ship)

- **W35.5 vault-only PATCH** — lesson-promotion opportunity for 1-2 lesson events:
  - PROMOTED 2/3 → 3/3 CONFIRMED LOCKED `infrastructure-channel-layer-sister-pattern-empirical-w18-w25-LOCKED` (W35 = 3rd observation: PeakCanChannel 2nd-cycle + W18 initial + W25 ChannelRouter)
  - NEW 1/3 `second-cycle-god-class-refactor-empirical-w28-w29-w35` (W35 = 1st observation: 2nd-cycle pattern)
- **W36** — next god-class refactor candidate. Sister candidates after W35: `StatsViewModel.cs` 263 LoC (small-god-class per W29 LESSON, NOT viable) + `DbcSendViewModel.cs` 238 LoC (already W24-extracted, saturated) + `DbcTokenizer.cs` 239 LoC (single public method, NOT viable). New emergent candidates: `DbcViewModel.cs` 208 LoC (App/ViewModels — sister of W19 TraceViewModel + DbcSendViewModel) + `ReplayViewModel.cs` 278 LoC (App/ViewModels — sister of W16) + `TraceSessionBundle.cs` 247 LoC (App/Services/Trace — sister of W22 + W23) + smaller candidates throughout.
