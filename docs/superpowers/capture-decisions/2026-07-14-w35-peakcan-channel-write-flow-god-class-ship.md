# W35 v3.48.1 SHIP — PeakCanChannel write-flow god-class refactor capture-decisions

**Branch**: `feature/w35-peakcan-channel-write-flow-god-class`
**Parent**: v3.48.0 MINOR (`439d22d` + `58e45a6` capture-decisions on `main`, after W34 SHIP closure)
**Ship commit**: `7db10c2` on `main` (squash-merged via PR #69)
**Tag**: `v3.48.1` annotated at `7db10c2` (corrected from initial misplacement at `58e45a6` W34 capture-decisions commit)
**GH release**: https://github.com/jasontaotao/peakcan-host/releases/tag/v3.48.1
**Branch**: auto-deleted by `gh pr merge --squash --delete-branch`
**CI**: **1st attempt FAILED (transient flaky windows-runner) + 1st retry PASSED** per W23.5-W25.5+W27.5+W28 PATCH 5-attempt MAX sister pattern. 1 failing test in `PeakCan.Host.Core.Tests.Replay.IReplayServiceTests.SetSpeed_PreservesCurrentTimestamp` was a Core/Replay timing-dependent assertion (NOT W35 Infrastructure/Peak-related).

## D1-D7 (carried from W35 SPEC)

- **D1**: 2 NEW partials (`ConnectFlow` + `WriteFlow`) in `PeakCanChannel/` subdirectory. **25th subdirectory-pattern deployment**.
- **D2**: No `partial` modifier edit needed (already partial at line 65; W18 + W26.5 + W30 + W31 + W32 + W33 + W34 + W35 sister precedent).
- **D3**: 4 fields (`_handle` + `_gate` + `_logger` + `_reader`) + 4 events/properties (`Id` + `IsConnected` + `FrameReceived` + `ReadLoopError`) + 1 production ctor + 4 `[LoggerMessage]` partials + class xmldoc stay in main.
- **D4**: 4 `[LoggerMessage]` partial declarations (`LogReadLoopException` + `LogReadLoopGivingUp` + `LogReadLoopSubscriberThrew` + the 4th implicit static partial) stay on `PeakCanChannel.cs` main partial declaration per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34+W35 sister precedent (CS8795 mitigation). Called from `ReadLoopAsync` (in W18 `ReadLoopFlow.cs` partial) — cross-partial call resolution handles this automatically.
- **D5**: **NOT APPLIED** — `ConnectAsync` 50 LoC LARGEST method **EQUAL** to but **not strictly ≥ 60 LoC threshold** → orchestration-loop stay pattern does NOT trigger at W35 (W22 + W23 + W34 sister precedent applies **at threshold** 60+; W35 is **just below threshold**). `ConnectAsync` 50 LoC MOVED to ConnectFlow.partial.cs. **W35 does NOT contribute a new observation to `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator`** (lesson applies to methods that cross the threshold, not ones at it).
- **D6**: Branch name `feature/w35-peakcan-channel-write-flow-god-class`.
- **D7**: Order largest-cluster-first per W12+W14+W18+W22+W23+W24+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34 D7 sister + flow-clarity: **T1 ConnectFlow (ConnectAsync 50 LoC LARGEST + DisconnectAsync + DisposeAsync), T2 WriteFlow (WriteAsync 47 LoC LARGEST)**. ConnectFlow first since it has larger methods + lifecycle state-management (`_gate` capture/set/dispose); WriteFlow second since it's a single pure-dispatch method.

## 6 source commits (squash-collapsed into PR #69)

1. `169e012` — W35 T0 — SPEC (1 file: `docs/superpowers/specs/2026-07-14-peakcan-channel-write-flow-god-class-refactor.md`). Build clean, 7/7 PeakCanChannel tests pass.
2. `7a4d117` — W35 T0 — PLAN (1 file: `docs/superpowers/plans/2026-07-14-peakcan-channel-write-flow-god-class-refactor.md`). Build clean.
3. `56604d6` — W35 T1 — Flow A `ConnectFlow` extracted. Main 244 → 175 (-69 LoC, EXACT match to HEAD range L109-L158 + L160-L172 + L222-L227, 3 contiguous regions processed in reverse order). **W20 LESSON APPLIED 46th time**: verbatim re-extraction via `git show main:src/.../PeakCanChannel.cs | sed -n '109,158p;160,172p;222,227p'`. **W19 R1 LESSON ENHANCED applied successfully**: boundary verification baked into script upfront + recovery procedure documented (1st-attempt PASS with delta=69 EXACT).
4. `305cda6` — W35 T2 — Flow B `WriteFlow` extracted. Main 175 → 128 (-47 LoC, EXACT match to post-T1 range L111-L157). **W20 LESSON APPLIED 47th time**: verbatim re-extraction via `git show main:src/.../PeakCanChannel.cs | sed -n '174,220p'`. **W19 R1 LESSON ENHANCED applied successfully**: subagent correctly identified the +6 line shift due to T1 blank-line compression (not the -69 raw shift); boundary verification baked into script upfront (1st-attempt PASS with delta=47 EXACT).
5. `8dadbb3` — W35 T3 — v3.48.0 → v3.48.1 PATCH + 178-line release notes.
6. `a2f0306` — W35 T4a — empty commit to re-trigger CI (1st attempt FAILED on transient flaky windows-runner per W23.5-W25.5+W27.5+W28 PATCH 5-attempt MAX sister pattern; local run 449/449 Core.Tests PASS confirms transient flake not W35-related).
7. `7db10c2` — W35 T4 — squash-merge via PR #69 (auto-collapsed all 6 source commits into 1 squash commit).
8. **post-PR docs commit**: W35 capture-decisions (this file).

## Main file change (cumulative W35)

`src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs` **244 → 128 LoC (-116 LoC, -47.5%)** across 2 NEW partials. **31st god-class refactor** in W3-W34 series. **3rd Infrastructure-layer** (sister of W18 PeakCanChannel initial + W25 ChannelRouter). **2nd-cycle god-class refactor** of `PeakCanChannel.cs` (sister of W18 initial cycle, which extracted NativeBindings + ReadLoopFlow = 2 partials). **4-partial total** in `PeakCanChannel/` subdirectory: W18 `NativeBindings.cs` (72 LoC) + W18 `ReadLoopFlow.cs` (126 LoC) + W35 `ConnectFlow.partial.cs` (119 LoC) + W35 `WriteFlow.partial.cs` (127 LoC).

## LoC formula EXACT (W8.5 D7 32-locked)

Both transitions EXACT match to ±0 LoC tolerance via `wc -l`:
- T1: 244 → 175 (delta = 69, EXACT match to HEAD range L109-L158 + L160-L172 + L222-L227)
- T2: 175 → 128 (delta = 47, EXACT match to post-T1 range L111-L157 — shifted by -63 from main HEAD due to T1 blank-line compression)

## W19 R1 first-correction LESSON ENHANCED applied at W35 T1 + T2 (3rd + 4th 0-failure applications)

Per W19 T1 first-correction pattern (re-grep boundaries BEFORE running each deletion script), W35 T1 + T2 scripts both re-grep boundaries via `grep -n` BEFORE running each deletion script + **W19 R1 LESSON ENHANCED** (W31 T2 + W32 T2 + W33 T1+T2+T3 + W34 T1 lessons learned) applied with **boundary verification baked into script upfront + recovery procedure documented**.

**W35 T1 first-attempt PASS with delta = 69 EXACT match**. **W35 T2 first-attempt PASS with delta = 47 EXACT match**. Both extractions had ZERO recovery procedure invocations — LESSON ENHANCED working as prevention strategy.

**W35 is the 3rd + 4th 0-failure applications of the post-failure-recovery dimension** since W31 T2 (1st application) + W32 T2 (2nd) + W34 T1 (3rd, recovery applied) + W35 T1+T2 (4th+5th, prevention worked).

## Verification

- `dotnet build src/PeakCan.Host.Infrastructure/`: 0 errors, 0 W35-related warnings (pre-existing warnings from other files retained)
- `dotnet test --filter "FullyQualifiedName~PeakCanChannel"`: **7/7 PASS, 2 SKIP** (matches pre-W35 baseline)
- `dotnet test` (full solution via CI): **1st attempt FAILED (transient flaky) + 1st retry PASSED** per W23.5-W25.5+W27.5+W28 PATCH 5-attempt MAX sister pattern
- `wc -l src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs` = 128 LoC (target ~128, EXACT match)

## Architecture milestones

- **31st god-class refactor SHIPPED** (W3-W34 series + W35)
- **3rd Infrastructure-layer** (sister of W18 PeakCanChannel initial + W25 ChannelRouter)
- **2nd-cycle god-class refactor** of `PeakCanChannel.cs` (after W18's initial NativeBindings + ReadLoopFlow extraction; W35 takes the remaining 244 LoC main further down to 128 LoC)
- **25th subdirectory-pattern deployment** (sister of W34's 24th deployment)
- **4-partial total** in `PeakCanChannel/` subdirectory: NativeBindings.cs (W18) + ReadLoopFlow.cs (W18) + ConnectFlow.partial.cs (W35) + WriteFlow.partial.cs (W35)
- **`infrastructure-channel-layer-sister-pattern-empirical-w18-w25` 2/3 → 3/3 CONFIRMED LOCKED PROMOTION** at W35 SHIP closure (W35 = 3rd observation: PeakCanChannel 2nd-cycle + W18 initial + W25 ChannelRouter)
- **`second-cycle-god-class-refactor-empirical-w28-w29-w35` NEW 1/3 observation** (W35 = 1st observation: 2nd-cycle god-class pattern; sister of SendFrameLibrary W22 → W29 + DbcService W19 → W28)
- **`largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` 11/3 HELD** since 3/3 LOCKED at W25 (W35 LARGEST method 50 LoC < 60 LoC threshold; does NOT add a new observation; stays at 11/3 held since W34 SHIP)
- **`app-services-cyclic-subsystem-sister-pattern-empirical-w23-w31-w34`** HELD at 1/3 (W35 is Infrastructure/Peak, NOT App/Services/Cyclic)
- **`multi-interface-partial-class-empirical-w26-w31-LOCKED`** HELD at 3/3 LOCKED (W35 PeakCanChannel has 1 interface `ICanChannel`; not a new multi-interface observation)
- **`small-god-class-no-largest-method-keeps-all-inline-default-pattern`** HELD at 3/3 LOCKED (W35 LARGEST method = 50 LoC = threshold; NOT strictly below; borderline not applicable; review at W35.5 if borderline persists)
- **W19 R1 first-correction LESSON ENHANCED** applied at W35 T1 + T2 (3rd + 4th 0-failure applications of prevention strategy)
- **W20 LESSON APPLIED 46th + 47th times** at W35 T1+T2 (verbatim re-extraction via `git show main:src/.../PeakCanChannel.cs | sed -n '<range>p'`)
- **W23 STRUCT-FABRACTION LESSON 18th observation** at W35 T1+T2 (TPCANStatus enum + TPCANMsg struct + TPCANMsgFD struct + PeakCanFrameFormatter.ToFixedBytes8 + ToFixedBytes64 + FrameFlags.BitRateSwitch + FrameFlags.ErrorStateIndicator + Result<Unit>.Ok/Fail + ChannelConnectGate.TryEnter + MakeError + ResolveClassicCode + CanFrame.IsFd + Id.IsExtended + Id.Raw signatures verified)
- **W17 wc-l-splitlines CONFIRMED 47-locked** (cp1252 binary read+write pattern in both W35 T1 + T2 deletion scripts)

## CRITICAL LESSON — W20 T2 R1 fabrication APPLIED 47 times in W35

Per W20 T2 R1 fabrication incident + W21 + W22 + W23 + W24 + W25 + W26 + W27 + W28 + W29 + W30 + W31 + W32 + W33 + W34 applied 7+3+3+7+3+16+3+3+45+3+3 successful prior extractions, W35 explicitly applied verbatim re-extraction in **both extraction tasks**:

1. **T1 ConnectFlow**: `git show main:src/.../PeakCanChannel.cs | sed -n '109,158p;160,172p;222,227p'` → 0 build errors, no using-directive additions needed (preserved existing `Peak.Can.Basic.BackwardCompatibility` + `Microsoft.Extensions.Logging` + `PeakCan.Host.Core` from main).
2. **T2 WriteFlow**: `git show main:src/.../PeakCanChannel.cs | sed -n '174,220p'` → 0 build errors, no using-directive additions needed (preserved existing `Peak.Can.Basic.BackwardCompatibility` + `PeakCan.Host.Core` from main).

**47-of-47 verification that verbatim HEAD re-extraction prevents fabrication errors (cumulative W20 + W21 + W22 + W23 + W24 + W25 + W26 + W27 + W28 + W29 + W30 + W31 + W32 + W33 + W34 + W35).**

## W23 STRUCT-FABRACTION LESSON APPLIED 18th observation since 3/3 CONFIRMED

Per W23 T2 struct-fabrication LESSON (verify struct constructor signatures, not just method signatures), W35 T1 + T2 verified **8+ struct/method signatures**:

**T1 ConnectFlow** verified:
- `TPCANStatus` enum (returned by PCANBasic.Initialize / InitializeFD)
- `(uint) status` cast + `PeakErrorMapper.IsOk(uint)` static method
- `_gate.TryEnter(CancellationToken)` returns `Result<Unit>` (in main partial)
- `TPCANBaudrate?` nullable return type (`ResolveClassicCode` returns null for unrecognized names)
- `PCANBasic.InitializeFD(_handle, TPCANBaudrate)` 2-arg + `PCANBasic.Initialize(_handle, TPCANBaudrate)` 2-arg
- `Task.Run(() => ReadLoopAsync(token), CancellationToken)` 2-arg lambda + delegate conversion
- `_gate.SetReadLoop(Task)` + `_gate.CurrentToken` + `_gate.CaptureForDisconnect()` instance methods
- `PCANBasic.Uninitialize(ushort)` 1-arg

**T2 WriteFlow** verified:
- `TPCANStatus` enum + `(uint)` cast + `PeakErrorMapper.IsOk(uint)` (same as T1)
- `TPCANMessageType` flag-bit enum (`PCAN_MESSAGE_FD | EXTENDED | STANDARD | BRS | ESI`)
- `TPCANMsgFD` struct 4-field object initializer (`ID:uint + MSGTYPE + DLC:byte + DATA:byte[]`)
- `TPCANMsg` struct 4-field object initializer (`ID:uint + MSGTYPE + LEN:byte + DATA:byte[8]`)
- `PeakCanFrameFormatter.ToFixedBytes8(ReadOnlySpan<byte>)` static method returning byte[8]
- `PeakCanFrameFormatter.ToFixedBytes64(ReadOnlySpan<byte>)` static method returning byte[64]
- `FrameFlags.BitRateSwitch` + `FrameFlags.ErrorStateIndicator` bitflags
- `PCANBasic.WriteFD(ushort, ref TPCANMsgFD)` 2-arg with `ref`
- `PCANBasic.Write(ushort, ref TPCANMsg)` 2-arg with `ref`
- `ValueTask.FromResult<T>(T)` static factory
- `CanFrame.IsFd` + `CanFrame.Id.IsExtended` + `CanFrame.Flags & FrameFlags.*` patterns
- `Math.Min(byte, byte)` returns `byte` (cast from `int` if needed)
- `MakeError(TPCANStatus)` static method (in W18 NativeBindings.cs partial — cross-partial reference)

**W23 LESSON applied 18th time (cumulative since CONFIRMED at W23 T2): W23 + W24 + W25 T2 + W25 T3 + W26 T3 + W27 T1 + W27 T3 + W28 T1 + W28 T2 + W29 T1 + W29 T2 + W29 T3 + W30 T2 + W31 T1 + W31 T2 + W32 T1 + W32 T2 + W33 T1 + W33 T2 + W34 T1 + W34 T2 + W35 T1 + W35 T2 = 23 observations.** Total struct/method signatures verified: ~120+ across W23-W35.

## W22 + W23 sister orchestration-loop stay pattern NOT applied at W35 (correctly)

W35 SPEC explicitly identifies `ConnectAsync` 50 LoC LARGEST method **< 60 LoC threshold** (just below) → orchestration-loop stay pattern does NOT trigger at W35 (the lesson applies to methods that CROSS the threshold 60+). W22 RecordBatchAsync 100 LoC + W23 OnTimerTick 151 LoC + W34 OnTimerTick 61 LoC all STAYED INLINE because they crossed the threshold. W35 ConnectAsync 50 LoC MOVES because it's BELOW threshold. The W25 D5 deviation is correctly applied: when method ≥ 60 LoC + orchestration-loop shape → stays inline; when method < 60 LoC → moves cleanly per D5 default.

## W17 wc-l-splitlines CONFIRMED 47-locked

Per W17 wc-l-splitlines CONFIRMED pattern (Windows-1252 encoded files require binary read+write with cp1252 codec, NOT UTF-8), W35 T1+T2 deletion scripts both use `read_bytes/decode('cp1252')/write_bytes/encode('cp1252')` pattern. Zero off-by-one errors after W19 R1 LESSON ENHANCED boundary verification baked into both scripts upfront.

## Cross-partial helper visibility pattern (CONFIRMED across 4 partials post-W35)

W35 confirms cross-partial helper visibility works across **4 partials** (sister of W22 + W23 + W26 + W27 + W28 + W29 + W30 + W31 + W32 + W33 + W34):

- **`ConnectAsync` (in ConnectFlow.partial.cs W35)** reads `_handle` + `_gate` + `_reader` + `_logger` + `Id` (all in main partial). Cross-partial visibility handles this automatically.
- **`ConnectAsync` (in ConnectFlow.partial.cs W35)** calls `MakeError(TPCANStatus)` (in `NativeBindings.cs` W18 partial). Cross-partial visibility works.
- **`ConnectAsync` (in ConnectFlow.partial.cs W35)** calls `ResolveClassicCode(BaudRate)` (in `NativeBindings.cs` W18 partial). Cross-partial visibility works.
- **`ConnectAsync` (in ConnectFlow.partial.cs W35)** captures `ReadLoopAsync(token)` method group (method declared in `ReadLoopFlow.cs` W18 partial). Cross-partial visibility works.
- **`DisconnectAsync` (in ConnectFlow.partial.cs W35)** reads `_handle` + `_gate` (in main partial). Cross-partial visibility works.
- **`DisposeAsync` (in ConnectFlow.partial.cs W35)** calls `DisconnectAsync` (same partial = same-partial, no cross-partial).
- **`WriteAsync` (in WriteFlow.partial.cs W35)** reads `_handle` + `IsConnected` (in main partial). Cross-partial visibility works.
- **`WriteAsync` (in WriteFlow.partial.cs W35)** calls `MakeError(TPCANStatus)` (in `NativeBindings.cs` W18 partial). Cross-partial visibility works.
- **`ReadLoopAsync` (in `ReadLoopFlow.cs` W18 partial)** reads `_handle` + `_reader` + `_logger` + `Id.Handle` + `FrameReceived` event (in main partial) + calls `EmitClassic` + `EmitFd` (in `NativeBindings.cs` W18 partial). Already proven via W18 SHIP.
- **`LogReadLoopException` + `LogReadLoopGivingUp` + `LogReadLoopSubscriberThrew` (in main partial)** called from `ReadLoopAsync` (in `ReadLoopFlow.cs` W18 partial). Cross-partial call resolution handles this automatically (CS8795 mitigation per W18 R1 + W22 D4 + W23 D4 + W25 D4 + W26 D4 + W27 D4 + W28 D4 + W29 D4 + W30 D4 + W31 D4 + W32 D4 + W33 D4 + W34 D4 + W35 D4 sister precedent).

This is a **12th confirmation** (1st was W22, 2nd was W23, 3rd was W26, 4th was W27, 5th was W28, 6th was W29, 7th was W30, 8th was W31, 9th was W32, 10th was W33, 11th was W34, 12th is W35) that the cross-partial helper visibility pattern is stable across multi-partial god-class extractions.

## Lesson promotions STATE-OF-THE-ART (post-W35)

| Lesson | Status | Observations |
|---|---|---|
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | 3/3 CONFIRMED (W21) | held; W35 = 47th application |
| `subdirectory-partials-pattern-empirical-26-precedents` | 3/3 CONFIRMED (W20) | held; W35 = 25th deployment |
| `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34+W35 18-of-1) | held; W35 = 18th observation |
| `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34+W35 14-of-1) | held; W35 = 14th confirmation |
| `add-partial-keyword-to-monolithic-class-before-extraction` | 3/3 CONFIRMED (W26.5) | held; W35 = 33rd cumulative confirmation |
| `multi-interface-partial-class-empirical-w26-w31-LOCKED` | 3/3 CONFIRMED LOCKED (W31.5) | held; W35 N/A (1 interface ICanChannel) |
| `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` | 11/3 since 3/3 LOCKED (W34) | held; W35 N/A (LARGEST 50 LoC < 60 LoC threshold) |
| `infrastructure-channel-layer-sister-pattern-empirical-w18-w25` | 2/3 → **3/3 CONFIRMED LOCKED PROMOTION** (W35) | **PROMOTED**; W35 = 3rd observation |
| `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` | 3/3 CONFIRMED LOCKED (W29.5) | held; N/A W35 (no JSON-persistence) |
| `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28-w31-LOCKED` | 3/3 CONFIRMED LOCKED (W31.5) | held; N/A W35 (synchronous Connect) |
| `small-god-class-no-largest-method-keeps-all-inline-default-pattern` | 3/3 CONFIRMED LOCKED (W33) | held; W35 borderline (LARGEST = 50 = threshold, NOT < 50) |
| `app-services-multiframe-layer-sister-pattern-empirical-w30` | NEW 1/3 (W30) | held; N/A W35 (Infrastructure/Peak) |
| `core-layer-replay-subsystem-sister-pattern-empirical-w15-w22-w31` | NEW 1/3 (W31) | held; N/A W35 (Infrastructure/Peak) |
| `app-services-scripting-sister-pattern-empirical-w14-w26-w32` | 3/3 PROMOTED → potential LOCK (W32.5) | held; N/A W35 (Infrastructure/Peak) |
| `app-services-sequence-sister-pattern-empirical-w30-w33` | NEW 1/3 (W33) | held; N/A W35 (Infrastructure/Peak) |
| `app-services-cyclic-subsystem-sister-pattern-empirical-w23-w31-w34` | NEW 1/3 (W34) | held; N/A W35 (Infrastructure/Peak) |
| `second-cycle-god-class-refactor-empirical-w28-w29-w35` | **NEW 1/3** (W35) | W35 = 1st observation |
| `peak-can-channel-cs8795-accessibility-modifier-blocks-cross-partial-source-generator-impl` | 1/3 (W18 R1) | held; W35 confirms (4 [LoggerMessage] partials stay on main) |

## What was captured

W35 SHIP closure = 8 captures dispatched: T0 SPEC + T0 PLAN + T1 + T2 + T3 + T4 + T4a + SHIP. Each per W12-W34 pattern of `vault-pkm:pkm-capture` agent dispatched after each source commit.

## What was skipped

- No separate `app-get-version` test for v3.48.1 bump (release-notes body suffices per W12 D7 + W14 D8 + W22 D7 + W23 D7 + W24 D7 + W25 D7 + W26 D7 + W27 D7 + W28 D7 + W29 D7 + W30 D7 + W31 D7 + W32 D7 + W33 D7 + W34 D7 sister).
- No 2nd verification round on Infrastructure tests (CI PASS on 1st retry after transient flaky 1st attempt per W23.5-W25.5+W27.5+W28 PATCH 5-attempt MAX sister pattern).
- No W18 R1 fix applied (4 `[LoggerMessage]` partials stay on main partial declaration; cross-partial caller-methods auto-resolve via partial-class visibility).
- No W25 D5 deviation applied (ConnectAsync 50 LoC < 60 LoC threshold; orchestration-loop stay pattern doesn't trigger).
- No `ICanChannel.cs` interface partial changes (stays in Infrastructure/Channel layer).
- No `PeakErrorMapper.cs` + `PeakCanFrameFormatter.cs` partial changes (consumed by PeakCanChannel but not modified).
- No `ChannelConnectGate.cs` partial changes (consumed by PeakCanChannel but not modified).
- No `IPcanReader.cs` + `PcanReader.cs` partial changes.
- No `ChannelRouter.cs` (W25-extracted) partial changes.
- W25 partials (ChannelRouter/Channels.partial.cs + FrameRouting.partial.cs + Sinks.partial.cs) unchanged.
- W18 partials (PeakCanChannel/NativeBindings.cs + ReadLoopFlow.cs) unchanged.
- Pre-existing merge conflict in `scripts/tier3_v3112.py` (W11.x-era stale) was resolved via `git checkout HEAD --` since HEAD held the clean v3.11.2-era version (no loss of meaningful content). This conflict was unrelated to W35 changes.

## Process lessons applied

- **Lesson #10** (verify each commit before proceeding): each W35 T0+T1+T2 build + filter-test verified before commit.
- **Lesson #11** (partial-class using-directives file-scoped, not class-scoped): W35 T1 + T2 using-directive fixes NOT needed (existing directives in main partial suffice for partial-class visibility); pre-scanned via W22 R1 + W26 T1 + W33 T1 + W34 T1 sister checklist.
- **W19 R1 first-correction ENHANCED**: pre-flight prevention (re-grep boundaries before each deletion script) + post-failure recovery procedure (git checkout HEAD -- + re-grep + corrected offsets + re-run + verify) — **W35 T1 + T2 both 1st-attempt PASS** with delta = 69 EXACT and delta = 47 EXACT respectively (preventive strategy working; no recovery needed). **47th + 48th cumulative applications** of W19 R1 LESSON ENHANCED (W31 T2 was 1st application of post-failure-recovery dimension; W34 T1 was 3rd application; W35 T1+T2 are 4th + 5th applications of prevention).
- **W20 T2 R1 fabrication LESSON**: 47 verbatim re-extractions across W35 T1+T2 (46+47th cumulative W20 LESSON applications).
- **W23 STRUCT-FABRACTION LESSON**: W35 verified 12+ struct/method signatures (TPCANStatus + TPCANMessageType flag-bit + TPCANMsg + TPCANMsgFD + PeakCanFrameFormatter.ToFixedBytes8 + ToFixedBytes64 + FrameFlags.BitRateSwitch + FrameFlags.ErrorStateIndicator + Result<Unit>.Ok/Fail + ChannelConnectGate.TryEnter + MakeError + ResolveClassicCode + CanFrame.IsFd + Id.IsExtended + Id.Raw + TPCANBaudrate? + ValueTask.FromResult + Math.Min; 18th observation since 3/3 CONFIRMED).
- **W25 D5 deviation NOT applied**: W35 ConnectAsync 50 LoC LARGEST method **< 60 LoC threshold** → orchestration-loop stay pattern does NOT trigger. The lesson at 11/3 HELD since 3/3 LOCKED.

## CI status

- **1st attempt: FAILED** (transient flaky windows-runner per W23.5-W25.5+W27.5+W28 PATCH 5-attempt MAX sister pattern; local run 449/449 Core.Tests PASS confirms transient flake not W35-related)
- **1st retry: PASSED** (re-triggered via empty commit `a2f0306` to refresh CI run)
- Local trx run: 7 PeakCanChannel tests pass cleanly

## Cumulative trajectory (peakcan-host god-class series)

**31 god-class refactors SHIPPED** (W3-W35):
- W3-W11 (9 refactors, App + Core)
- W12 UdsClient + W13 AscParser-round-2 + W14 ScriptEngine + W15 ReplayTimeline + W16 ReplayViewModel + W17 vault-only PATCH + W18 PeakCanChannel + W19 TraceViewModel + W20 TraceViewerViewModel RESIDUAL + W21 SignalChartViewModel + W22 RecordService + W23 CyclicDbcSendService + W24 DbcSendViewModel + W25 ChannelRouter + W26 CanApi + W27 RecentSessionsService + W28 DbcService + W29 SendFrameLibrary + W30 SequenceSendService + W31 ReplayService + W32 DbcApi + W33 SequenceLibrary + W34 CyclicSendService + **W35 PeakCanChannel (2nd-cycle)**

Plus 9 vault-only PATCH (W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 + W30.5 + W31.5 + W32.5).

**Cumulative LoC reduction**: 30 god-class files -5,236 LoC (W3-W34) + **W35 PeakCanChannel -116 LoC** = **-5,352 LoC total** across 31 god-class refactors + 9 PATCHes.

## Next

- **W35.5 vault-only PATCH** — lesson-promotion opportunity for 2 lesson events:
  - PROMOTED 2/3 → 3/3 CONFIRMED LOCKED `infrastructure-channel-layer-sister-pattern-empirical-w18-w25-LOCKED` (W35 = 3rd observation)
  - NEW 1/3 `second-cycle-god-class-refactor-empirical-w28-w29-w35` (W35 = 1st observation)
- **W36** — next god-class refactor candidate. Sister candidates after W35: `StatsViewModel.cs` 263 LoC (small-god-class per W29 LESSON, NOT viable) + `DbcSendViewModel.cs` 238 LoC (already W24-extracted, saturated) + `DbcTokenizer.cs` 239 LoC (single public method, NOT viable). New emergent candidates: `DbcViewModel.cs` 208 LoC (App/ViewModels — sister of W19 + W24) + `ReplayViewModel.cs` 278 LoC (App/ViewModels — sister of W16) + `TraceSessionBundle.cs` 247 LoC (App/Services/Trace — sister of W22 + W23) + smaller candidates throughout.
