# W35 SPEC — PeakCanChannel write-flow god-class refactor (31st overall, 3rd Infrastructure-layer)

**Date**: 2026-07-14
**Target class**: `src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs` (244 LoC)
**Target version**: v3.48.1 PATCH (per W31 + W32 + W33 + W34 "PATCH for single-flow extraction" precedent; or MINOR if 2-flow extraction chosen — see D1 alternative)
**Sister pattern**: W18 PeakCanChannel initial god-class refactor (2 partials: NativeBindings + ReadLoopFlow) + W25 ChannelRouter (3 partials: Channels + FrameRouting + Sinks). **31st god-class refactor** in W3-W34 series. **3rd Infrastructure-layer god-class** after W18 + W25.

## Context

`PeakCanChannel.cs` (244 LoC) is the **3rd Infrastructure-layer god-class** in the W3-W34 series (30 god-class refactors shipped; W18 was the first partial-extraction of THIS class, W25 was ChannelRouter). This SPEC proposes a **second-cycle god-class refactor** of PeakCanChannel — extracting the remaining write-flow + connect-flow to take the main further down.

**Class shape** (already verified via direct read):

- `public sealed partial class PeakCanChannel : ICanChannel` (L65) — **already partial** (W18 sister; no D2 needed)
- 4 fields (L80-L83): `_handle` + `_gate` + `_logger` + `_reader`
- 4 events/properties (L85-L95): `Id` + `IsConnected` + `FrameReceived` + `ReadLoopError`
- 1 production ctor (L97-L107, 10 LoC)
- **LARGEST method**: `ConnectAsync` (L109-L158, **50 LoC**) — just at the boundary below 60 LoC threshold
- `DisconnectAsync` (L160-L172, 13 LoC)
- **Second-largest method**: `WriteAsync` (L174-L220, **47 LoC**) — classic + FD dual-path dispatch
- `DisposeAsync` (L222-L227, 6 LoC)
- 3 partial declarations (L229-L241): `LogReadLoopException` + `LogReadLoopGivingUp` + `LogReadLoopSubscriberThrew` + 1 implicit `static partial void LogReadLoopSubscriberThrew` — **STAY ON MAIN per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34 sister precedent (CS8795 mitigation)**

**Existing W18 partials (sister of W35)**:
- `PeakCanChannel/NativeBindings.cs` (72 LoC) — `EmitClassic` + `EmitFd` + `MakeError` + `ResolveClassicCode`
- `PeakCanChannel/ReadLoopFlow.cs` (126 LoC) — `ReadLoopAsync` 75 LoC + `SafeEmitReadLoopError` 16 LoC
- **Subdirectory total post-W18**: 198 LoC across 2 partials

**LARGEST method analysis** per W25 D5 deviation:

- `ConnectAsync` 50 LoC < **60 LoC threshold** ✗ (just below)
- Shape: **connect orchestration loop** (gate enter → classic/FD dispatch → start read loop → set up read-loop task)
- Per **W25 D5 deviation criteria #3** ("method is NOT a single central orchestration loop"): `ConnectAsync` IS the central connect orchestration loop → would fail criteria → but **LoC is below 60 LoC threshold** so orchestration-loop stay pattern does NOT apply at W35
- **Conclusion**: `ConnectAsync` 50 LoC **MOVE OK** if discrete boundary; or **KEEP INLINE** as a sister-cycle candidate for Flow B extraction (Connect-flow)
- `WriteAsync` 47 LoC < 60 LoC threshold ✓ — clear discrete boundary (classic + FD dispatch)
- Per W12 + W14 + W18 + W19 + W20 + W21 + W22 + W23 + W24 + W25 + W26 + W27 + W28 + W29 + W30 + W31 + W32 + W33 + W34 D5 default: `WriteAsync` **MOVES** to WriteFlow.partial.cs (47 LoC ≤ 50 LoC threshold; clear classic-vs-FD dispatch boundary)

**W25 D5 deviation NOT applicable at W35** — both candidate methods are below the 60 LoC threshold. The W22 + W23 + W34 orchestration-loop stay pattern is sister-relevant but doesn't trigger.

**NOT applicable to W29 NEW `small-god-class-no-largest-method-keeps-all-inline-default-pattern` 3/3 LOCKED**: W35 LARGEST method 50 LoC = 50 LoC threshold (borderline; LARGEST 50 vs threshold <50; LOCKED requires **strictly below 50**). At exactly 50, the lesson is ambiguous. Per LOCKED lesson: "LARGEST method < 50 LoC → D5 default applies = keep all inline". Since W35 LARGEST method = 50 (equal to threshold, not below), the lesson is **strictly inapplicable**. Could be a borderline case for re-evaluation at W35.5 or W36.5.

## W35 D1-D7

- **D1**: 2 NEW partials (`ConnectFlow` + `WriteFlow`) in `PeakCanChannel/` subdirectory. **25th subdirectory-pattern deployment** (24 prior: W3-W34). Sister of W34's 2-partial dual-cluster pattern (Lifecycle + TimerTick) + W18's original 2-partial NativeBindings + ReadLoopFlow cycle.

  **Locked decision (Option B)**: 2-partial extraction. ConnectFlow (~85 LoC) + WriteFlow (~60 LoC). Sister of W34 2-partial pattern.

- **D2**: No `partial` modifier edit needed (already partial at line 65; W18 + W26.5 + W30 + W31 + W32 + W33 + W34 sister precedent).

- **D3**: 4 fields + 4 events/properties + 1 production ctor + 4 `[LoggerMessage]` partials + class xmldoc stay in main.

- **D4**: 4 `[LoggerMessage]` partial declarations (`LogReadLoopException` + `LogReadLoopGivingUp` + `LogReadLoopSubscriberThrew` + the 4th implicit static partial) stay on `PeakCanChannel` main partial declaration per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34 sister precedent (CS8795 mitigation). Called from `ReadLoopAsync` (in `PeakCanChannel/ReadLoopFlow.cs`) — cross-partial call resolution handles this automatically.

- **D5**: **NOT APPLIED** — `ConnectAsync` 50 LoC LARGEST method **EQUAL** to but **not strictly ≥ 60 LoC** → orchestration-loop stay pattern does NOT trigger (W22+W23+W34 sister precedent applies **at threshold** 60+; W35 is **just below threshold**). W35 does NOT contribute a new observation to `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` (the lesson applies to methods that cross the threshold, not ones at it). `ConnectAsync` 50 LoC moves to ConnectFlow.partial.cs if Option B chosen.

- **D6**: Branch name `feature/w35-peakcan-channel-write-flow-god-class` (consistent with W18 D6 sister precedent of `feature/w18-peakcan-channel-god-class`).

- **D7**: Order largest-cluster-first per W12+W14+W18+W22+W23+W24+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34 D7 sister + flow-clarity: **T1 ConnectFlow (ConnectAsync 50 LoC LARGEST + DisconnectAsync + DisposeAsync), T2 WriteFlow (WriteAsync 47 LoC LARGEST)**. ConnectFlow first since it has larger methods + lifecycle state-management (`_gate` capture/set/dispose); WriteFlow second since it's a single pure-dispatch method.

## Architecture

Sister pattern of W18 (PeakCanChannel initial) + W25 (ChannelRouter). 31st god-class refactor. **3rd Infrastructure-layer** (after W18 + W25) + **25th subdirectory-pattern deployment**. **Second-cycle god-class refactor of PeakCanChannel** (3rd file from the same class after W18; sister of W29 SendFrameLibrary's becoming 3-partial + W28 DbcService's becoming 2-partial second-cycles).

### Flow boundaries (Phase 1 verified)

**Stays in main (~128 LoC after T1+T2)**:
- `using` block (L1-L6) + namespace (L7) + class xmldoc (L9-L64, **56 LoC**)
- `public sealed partial class PeakCanChannel : ICanChannel` (L65, already partial)
- 4 fields (L80-L83)
- 4 events/properties (L85-L95)
- 1 production ctor (L97-L107)
- 3 explicit + 1 implicit `[LoggerMessage]` partial declarations (L229-L241)

**Flow A — ConnectFlow (~72 LoC, T1) → `PeakCanChannel/ConnectFlow.partial.cs`**:

- 1 public method `ConnectAsync(BaudRate, bool, CancellationToken)` (L109-L158, 50 LoC) — gate → classic/FD Initialize → start read loop
- 1 public method `DisconnectAsync(CancellationToken)` (L160-L172, 13 LoC)
- 1 public method `DisposeAsync()` (L222-L227, 6 LoC)

Plus `MakeError` + `ResolveClassicCode` stay in `NativeBindings.cs` per W18 sister (they were already partially-extracted in W18's `NativeBindings` partial, not re-extracting).

**Flow B — WriteFlow (~60 LoC, T2) → `PeakCanChannel/WriteFlow.partial.cs`**:

- 1 public method `WriteAsync(CanFrame, CancellationToken)` (L174-L220, **47 LoC**) — classic-vs-FD dual-path dispatch (TPCANMsg vs TPCANMsgFD formation + Write vs WriteFD call)

**Cross-partial caller pattern**:
- `ConnectAsync` (in ConnectFlow partial) reads `_handle` + `_gate` + `_reader` + `_logger` + `Id` (all in main). Partial-class cross-partial visibility handles this automatically (sister of W22+W23+W24+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34 cross-partial helper pattern).
- `WriteAsync` (in WriteFlow partial) reads `_handle` + `IsConnected` + `_logger` (all in main). Partial-class cross-partial visibility handles this automatically.
- `DisconnectAsync` (in ConnectFlow partial) reads `_handle` + `_gate` (in main). Cross-partial visibility handles this.
- `ConnectAsync` calls `MakeError` (in NativeBindings partial) — cross-partial visibility handles this.
- `ConnectAsync` calls `ResolveClassicCode` (in NativeBindings partial) — cross-partial visibility handles this.

**Cross-partial with W18 partials**:
- `ReadLoopAsync` (in `ReadLoopFlow.cs`) calls `EmitClassic` + `EmitFd` (in `NativeBindings.cs`) — already proven cross-partial in W18 SHIP.
- `ReadLoopAsync` calls `LogReadLoopException` + `LogReadLoopGivingUp` + `LogReadLoopSubscriberThrew` (in main partial) — cross-partial visibility handles this automatically (CS8795 mitigation per W18 R1 + W22 D4 + W23 D4 + W25 D4 + W26 D4 + W27 D4 + W28 D4 + W29 D4 + W30 D4 + W31 D4 + W32 D4 + W33 D4 + W34 D4 sister).

## LoC trajectory (W8.5 D7 32-locked + W19 R1 first-correction ENHANCED + W17 wc-l-splitlines CONFIRMED + W20 fabrication LESSON APPLIED 44+ times in W34 + W23 STRUCT-FABRACTION LESSON APPLIED 17 times)

| Task | Flow | Range (1-indexed) | LoC deleted | Markers | LoC main after |
|---|---|---|---|---|---|
| T1 | A — ConnectFlow | L109-L158 + L160-L172 + L222-L227 = 69 LoC (3 contiguous regions, processed in REVERSE order) | 69 | 1 | 175 |
| T2 | B — WriteFlow | L174-L220 = 47 LoC (1 contiguous region) | 47 | 1 | 128 |
| T3 | v3.48.0 -> v3.48.1 PATCH + release notes | (no source) | 0 | 0 | 128 |
| T4 | ship | -- | -- | -- | 128 |

Cumulative: 244 -> 175 -> 128 main. **Re-grep + range verify after each task per W19 R1 ENHANCED (pre-flight prevention + post-failure recovery)**.

## W20 + W23 LESSON APPLIED — verbatim re-extract + struct-ctor verification

Per W20 T2 R1 fabrication incident + W23 T2 3-fix-cycle struct-constructor fabrication, W35 will:

1. **Re-grep boundaries BEFORE running each deletion script** (Phase 1 done above; re-verify before T1 + T2 script runs).
2. **Re-extract original code from main HEAD via `git show main:src/.../PeakCanChannel.cs | sed -n '<range>p'`** for each partial's verbatim content.
3. **CRITICAL: Verify struct constructor signatures**: `TPCANMsgFD` struct (L189-L195) has fields `ID` (uint) + `MSGTYPE` (TPCANMessageType) + `DLC` (byte) + `DATA` (byte[]). `TPCANMsg` struct (L203-L209) has fields `ID` + `MSGTYPE` + `LEN` (byte) + `DATA`. `CanFrame.IsFd` property is boolean, `CanFrame.Id.IsExtended` is boolean, `CanFrame.Flags & FrameFlags.BitRateSwitch` + `FrameFlags.ErrorStateIndicator` are bitflags. `PeakCanFrameFormatter.ToFixedBytes64` + `ToFixedBytes8` are static methods returning byte[].
4. **Verify `[LoggerMessage]` attribute signatures for 3+1 declarations** — D4 sister-pattern.
5. **Build + run filter tests after each task** to catch any extraction errors immediately.

## Tasks

### T0: Branch + SPEC + PLAN commits

```bash
git checkout -b feature/w35-peakcan-channel-write-flow-god-class main
dotnet build src/PeakCan.Host.Infrastructure/PeakCan.Host.Infrastructure.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~PeakCanChannel" --logger "console;verbosity=minimal"
git add docs/superpowers/specs/2026-07-14-peakcan-channel-write-flow-god-class-refactor.md
git commit -m "W35 spec: PeakCanChannel write-flow god-class refactor (2 partials + 5-task roll-out, 31st overall, 3rd Infrastructure-layer, 25th subdirectory-pattern deployment, 2nd-cycle god-class refactor of PeakCanChannel after W18)"
git add docs/superpowers/plans/2026-07-14-peakcan-channel-write-flow-god-class-refactor.md
git commit -m "W35 plan: PeakCanChannel write-flow god-class refactor (2 partials: ConnectFlow + WriteFlow) — Option B per D7 sister"
```

### T1: ConnectFlow partial (~69 LoC)

Write `scripts/w35_task1_delete_connectflow.py` with W19 T1 2/3 loose assertion + W17 wc-l-splitlines CONFIRMED pattern + **W19 R1 LESSON ENHANCED boundary verification upfront + recovery procedure documented** (per W31 T2 + W32 T2 + W33 T1+T2+T3 + W34 T1 lessons learned). 3 contiguous regions: `DisposeAsync` L222-L227 + `DisconnectAsync` L160-L172 + `ConnectAsync` L109-L158, processed in REVERSE order. Expected: 244 - 69 + 1 ≈ 176 LoC. Build + tests, commit.

### T2: WriteFlow partial (~47 LoC)

Re-grep post-T1 ranges. Write `scripts/w35_task2_delete_writeflow.py`. 1 contiguous region: `WriteAsync` L174-L220 (post-T1 line numbers). Expected: 176 - 47 + 1 = 130 LoC. Build + tests, commit.

### T3: v3.48.0 → v3.48.1 PATCH + release notes

Mirror W34 release notes format. PATCH or MINOR depending on semantic-versioning impact assessment at T3 (recommended: PATCH per W31 + W32 + W33 + W34 sister precedent of second-cycle god-class refactor being a PATCH not a MINOR).

### T4: Tier-3 ship

`gh pr create` → `--squash --delete-branch` → `git tag v3.48.1` → `gh release create`.

## Sister-lesson candidates to monitor

| Lesson | Status | What W35 might observe |
|---|---|---|
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | 3/3 CONFIRMED (W21) | W35 10th god-class application (T1+T2) — 46th application total |
| `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34 17-of-1) | W35 18th observation (`TPCANMsgFD` struct + `TPCANMsg` struct + `CanFrame.IsFd` + `FrameFlags.BitRateSwitch` + `FrameFlags.ErrorStateIndicator` + `PeakCanFrameFormatter.ToFixedBytes64` + `ToFixedBytes8` static method signatures verified) |
| `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` | 3/3 CONFIRMED (W23+W24+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34 13-of-1) | W35 14th confirmation (4 `[LoggerMessage]` partials on main + called from `ReadLoopAsync` in `ReadLoopFlow.cs` partial which is ALREADY cross-partial via W18; + W35 adds ConnectFlow partial that does NOT touch the logger partials, so this is a positive confirmation by absence) |
| `add-partial-keyword-to-monolithic-class-before-extraction` | 3/3 CONFIRMED (W26.5) | W35 already partial (33rd cumulative confirmation) |
| `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` | 11/3 since 3/3 LOCKED (W34) | **N/A** — W35 LARGEST method 50 LoC < 60 LoC threshold (NOT a stay/observation candidate). ConnectAsync 50 LoC is BELOW threshold → moves if Option B chosen. |
| `subdirectory-partials-pattern-empirical-26-precedents` | 3/3 CONFIRMED (W20) | W35 25th deployment, sister-of-W34 |
| `infrastructure-channel-layer-sister-pattern-empirical-w18-w25` | 2/3 (W25) | **W35 likely 3rd observation**: 2nd-cycle god-class refactor of PeakCanChannel + sister of W18 initial + sister of W25 ChannelRouter. **Promotes `infrastructure-channel-layer-sister-pattern-empirical-w18-w25` to 3/3 CONFIRMED LOCKED if W35 confirms**. |
| `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` | 3/3 CONFIRMED LOCKED (W29.5) | N/A — W35 has no JSON-persistence |
| `small-god-class-no-largest-method-keeps-all-inline-default-pattern` | 3/3 CONFIRMED LOCKED (W33) | **Borderline**: W35 LARGEST method 50 LoC, threshold < 50 LoC. W35 is AT 50 (not below) — NOT applicable strictly per LESSON. Borderline candidate for re-evaluation at W35.5. |
| `multi-interface-partial-class-empirical-w26-w31-LOCKED` | 3/3 CONFIRMED LOCKED (W31.5) | N/A — W35 PeakCanChannel has 1 interface `ICanChannel` (no new multi-interface observation) |
| `app-services-cyclic-subsystem-sister-pattern-empirical-w23-w31-w34` | NEW 1/3 (W34) | N/A — W35 is Infrastructure/Peak, NOT App/Services/Cyclic |
| `second-cycle-god-class-refactor-empirical-w28-w29-w35-LOCKED` | NEW W35 1/3? | W35 may observe 2nd-cycle god-class refactor pattern (PeakCanChannel W18 → W35; SendFrameLibrary W22 → W29 2nd cycle; DbcService W19 → W28 2nd cycle). Sister-2nd-cycle pattern. If W35 ships, this is NEW 1/3 observation. |

## Verification

- `dotnet build src/PeakCan.Host.Infrastructure/`: 0 errors, 0 warnings
- `dotnet test --filter "FullyQualifiedName~PeakCanChannel"`: 7/7 tests pass without modification
- `dotnet test` (full solution): 0 new fails
- `wc -l src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs` ≤ 130 LoC (target ~128)
- 2 NEW partial files in `PeakCanChannel/` directory
- 4 fields + 4 events/properties + 1 ctor + 4 `[LoggerMessage]` partials remain in main
- 2 EXISTING W18 partials (`NativeBindings.cs` + `ReadLoopFlow.cs`) unchanged
- Cross-partial call resolution works for `ConnectAsync → MakeError` + `ConnectAsync → ResolveClassicCode` (both in NativeBindings partial) + `ReadLoopAsync → Log*` (in main partial)
- DI registration unchanged (`AddSingleton<ICanChannel, PeakCanChannel>(...)` factory)
- Public API unchanged (`ICanChannel` interface)
- Tag v3.48.1 + GH release published
- Branch deleted post-merge

## Out of scope (YAGNI)

- No public/internal API change.
- No test changes (7 PeakCanChannelTests pass without modification).
- No facade pattern (W3-W34 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk (verified — tests do not path-grep main file content).
- No W18 R1 mitigation needed (D4 keeps all 4 `[LoggerMessage]` partials on main partial declaration).
- No `ICanChannel.cs` interface partial changes (stays in Infrastructure/Channel layer).
- No `PeakErrorMapper.cs` + `PeakCanFrameFormatter.cs` partial changes (consumed by PeakCanChannel but not modified).
- No `ChannelConnectGate.cs` partial changes (consumed by PeakCanChannel but not modified).
- No `IPcanReader.cs` + `PcanReader.cs` partial changes.
- No `ChannelRouter.cs` (W25-extracted) partial changes.
- W25 partials (ChannelRouter/Channels.partial.cs + FrameRouting.partial.cs + Sinks.partial.cs) unchanged.
- W18 partials (PeakCanChannel/NativeBindings.cs + ReadLoopFlow.cs) unchanged.
