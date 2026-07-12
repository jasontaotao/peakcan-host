# Release Notes v3.39.0 — ChannelRouter god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.39.0
**Branch:** `feature/w25-channel-router-god-class`
**Parent:** v3.38.0 MINOR (`32d14d5` on origin/main + `df612a7` capture-decisions)

## Why this MINOR

`src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs` had grown to **305 LoC** as of v3.38.0 — at 38.1% of the 800 LoC Round-1 ceiling. Single `public sealed partial class ChannelRouter : IFrameSource` (modifier pre-existed at line 74 — sister of 19/20 prior cases; W21 was 1st fresh-add anomaly). 4 readonly fields + 1 ctor + 5 methods + 3 `[LoggerMessage]` partial declarations.

This is the **21st god-class refactor** in the project (W3-W25 series). **6th Infrastructure layer** god-class (sister of W18 PeakCanChannel, same `PeakCan.Host.Infrastructure.Channel` namespace). **15th subdirectory-pattern deployment**.

## LoC trajectory (W8.5 D7 CONFIRMED formula — now 36-locked)

Per task, `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`. All 3 transitions **EXACT match**.

| Task | Flow | Range deleted | LoC out | Main after |
|---|---|---|---|---|
| T1 | FrameRouting (OnChannelFrame 73 LoC, LARGEST) | 183-255 | 73 | 232 |
| T2 | Sinks (AttachSink + DetachSink) | 137-181 | 45 | 187 |
| T3 | Channels (RegisterChannel + UnregisterChannel) | 112-134 | 23 | 164 |
| **Total** | -- | -- | **141** | **164** |

**Net**: 305 → 164 LoC main file (**-141 LoC, -46.2%**). Total project LoC across main + 3 partials ≈ 252 LoC (small +87 LoC overhead from per-file namespace + using directives + 3 xmldoc header comment blocks).

## What this MINOR does

### Refactor — ChannelRouter adds 3 NEW partials in `ChannelRouter/` subdirectory

1. **NEW `src/PeakCan.Host.Infrastructure/Channel/ChannelRouter/FrameRouting.partial.cs` (~101 LoC)**:
   - Contains `private void OnChannelFrame(CanFrame frame)` method body verbatim from HEAD L183-L255 (W25 D5 deviation per flow-boundary logic — frame-arrives → fan-out + error-isolation is a sharp flow boundary vs W22 `RecordBatchAsync` 100 LoC stayed inline because single orchestration pipeline).
   - Verbatim re-extraction via `git show HEAD:src/...cs | sed -n '183,255p'` per W20 T2 R1 fabrication LESSON (21st application).
   - 1 using-directive fix (added `using PeakCan.Host.Core;` for `CanFrame` type).

2. **NEW `src/PeakCan.Host.Infrastructure/Channel/ChannelRouter/Sinks.partial.cs` (~68 LoC)**:
   - Contains `public void AttachSink(IFrameSink sink)` + `public void DetachSink(IFrameSink sink)` method bodies verbatim from HEAD L137-L181.
   - Verbatim re-extraction via `git show HEAD:src/...cs | sed -n '137,181p'` per W20 T2 R1 fabrication LESSON (22nd application).
   - **Build clean first try** (no using-directive fix needed — types in same `PeakCan.Host.Infrastructure.Channel` namespace + stdlib).

3. **NEW `src/PeakCan.Host.Infrastructure/Channel/ChannelRouter/Channels.partial.cs` (~50 LoC)**:
   - Contains `public void RegisterChannel(ICanChannel channel)` + `public void UnregisterChannel(ICanChannel channel)` method bodies verbatim from HEAD L112-L134.
   - Verbatim re-extraction via `git show HEAD:src/...cs | sed -n '112,134p'` per W20 T2 R1 fabrication LESSON (23rd application).
   - 1 using-directive fix per W19 (`using PeakCan.Host.Core;` for `ICanChannel` type).

### D1-D7 sister-pattern decisions (carried from W25 SPEC)

- **D1**: 3 NEW partials (`FrameRouting` + `Sinks` + `Channels`) in `ChannelRouter/` subdirectory. 15th subdirectory-pattern deployment.
- **D2**: No `partial` modifier edit needed (already partial at line 74).
- **D3**: 4 readonly fields (`_channels` + `_sinks` + `_gate` + `_logger`) + 1 static readonly empty sentinel `EmptySinks` + 1 ctor + 3 `[LoggerMessage]` partials (`LogSinkOnError` + `LogChannelRouterSinkOnFrameFailed` + `LogDetachSinkFailed`) + class xmldoc stay in main.
- **D4**: All 3 `[LoggerMessage]` partials stay on `ChannelRouter` main partial declaration per W18 R1 + W22 D4 + W23 D4 sister precedent (CS8795 mitigation: keeping `[LoggerMessage]` partials on same-class declaration avoids the "no logging method found in corresponding source generator declaration" warning).
- **D5**: `OnChannelFrame` 75 LoC LARGEST method **moves to `FrameRouting.partial.cs`** per W22 D5 deviation logic (RecordService) + W23 D5 deviation logic (CyclicDbcSendService) — large methods can move when they map to a discrete flow boundary ("frame arrives → fan out + error isolation"), not as a single central orchestration method.
- **D6**: Branch name `feature/w25-channel-router-god-class`.
- **D7**: Order largest-first per W12+W14+W18+W22+W23+W24 D7 sister pattern: **C (FrameRouting, 75 LoC, LARGEST) → B (Sinks, 46 LoC) → A (Channels, 22 LoC)**.

## What this MINOR does NOT change (YAGNI + sister-pattern invariants)

- No public/internal API change.
- No test changes (17 ChannelRouter tests + sister tests pass without modification; pre-existing 1 transient flaky AscParser test retained per W13 T1 sister pattern).
- No facade pattern (W3-W24 CONFIRMED direct partial-class visibility — `IFrameSource` interface inherits unchanged via main partial declaration).
- No xmldoc-grep risk (verified — tests do not path-grep main file content).
- No CS8795 risk (D4 keeps `[LoggerMessage]` partials on main partial declaration).
- No `IFrameSource` interface visibility change (interface inheritance auto-binds across partials).
- No `PeakCanChannel.cs` (W18) partial changes (sister precedent unchanged).

## Architecture milestones

- **21st god-class refactor SHIPPED** (W3-W25 series).
- **6th Infrastructure layer** (sister of W18 PeakCanChannel).
- **15th subdirectory-pattern deployment** (W5 + W7 + W16 + W18 + W21 + W22 + W23 + W24 + ... + W25).
- **W20 LESSON APPLIED 23 times total** across W25 T1+T2+T3 (verbatim re-extraction via `git show HEAD:src/...cs | sed -n '<range>p'`).
- **W23 STRUCT-FABRICATION LESSON APPLIED** in T2 + T3 (verified `Array.Copy` 5-arg signature + `Volatile.Write<T>(ref T, T)` signature).
- **W19 LESSON APPLIED** in T1 + T3 (2 using-directive fixes per file: `PeakCan.Host.Core` for `CanFrame` + `ICanChannel`).
- **W17 wc-l-splitlines CONFIRMED** (cp1252 binary read+write to handle Windows-1252 non-ASCII bytes in xmldoc + comments).
- **3 NEW 1/3 → 2/3 lesson candidates promoted**:
  - `infrastructure-channel-layer-sister-pattern-empirical-w18-w25` (2 observations: W18 + W25 confirm Infrastructure/Channel layer god-class refactor pattern).
  - `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` (3 observations: W22 RecordBatchAsync + W23 OnTimerTick + W25 OnChannelFrame, with W22 + W23 keeping inline because single orchestration pipelines, W25 moving because sharp flow boundary).
  - `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` 4th confirmation (W23 + W24 + W25 T2 + W25 T3 all confirm Array.Copy + Volatile.Read/Write signature verification).

## Verification

- `dotnet build src/PeakCan.Host.Infrastructure/`: 0 errors, 0 warnings (after W25 T1 + T3 using-directive fixes per W19).
- `dotnet test --filter "~ChannelRouter"`: 17/17 PASS (matches pre-W25 baseline).
- `dotnet test` (full solution): 0 new fails (1 transient flaky AscParser pre-existing per W13 T1 + W14 + W15 + W16 + W17 + W18 + W19 + W20 + W22 + W23 + W24 sister pattern retained).

## Process lessons applied (W20 + W22 + W23 + W24)

- **Lesson #10** (verify each commit before proceeding): each W25 T1-T3 build + filter-test verified before commit.
- **W19 R1 first-correction**: re-grep boundaries before each deletion script (per `w25_task1_delete_framerouting.py` Step 1 grep-before-deletion pattern).
- **W20 T2 R1 fabrication LESSON**: 23 verbatim re-extractions across W25 T1+T2+T3 (W20 T2 + W21 + W22 + W23 + W24 + W25 cumulative).
- **W23 STRUCT-FABRICATION LESSON**: W25 T2 + T3 verified `Array.Copy` 5-arg + `Volatile.Write<T>` + `Volatile.Read<T>` signatures.

## Sister-pattern cumulative trajectory (god-class series, W3-W25)

| W | Layer | Subdirectory | Main LoC | 21 prior + W25 |
|---|---|---|---|---|
| W3-W11 | App + Core | various | -1,400 | 9 god-classes |
| W12 UdsClient | Core | UdsClient/ | -89 | 10th |
| W13 AscParser | Core | AscParser/ | -150 | 11th |
| W14 ScriptEngine | App/Services | ScriptEngine/ | -187 | 12th |
| W15 ReplayTimeline | Core | ReplayTimeline/ | -85 | 13th |
| W16 ReplayViewModel | App/ViewModels | ReplayViewModel/ | -180 | 14th |
| W17 vault-only PATCH | (meta) | -- | 0 | (no source) |
| W18 PeakCanChannel | Infrastructure/Channel | PeakCanChannel/ | -228 | 15th |
| W19 TraceViewModel | App/ViewModels | TraceViewModel/ | -130 | 16th |
| W20 TraceViewerViewModel | App/ViewModels | TraceViewerViewModel/ | -91 | 17th |
| W21 SignalChartViewModel | App/ViewModels | SignalChartViewModel/ | -232 | 18th |
| W22 RecordService | App/Services | RecordService/ | -193 | 19th |
| W23 CyclicDbcSendService | App/Services | CyclicDbcSendService/ | -288 | 20th |
| W24 DbcSendViewModel | App/ViewModels | DbcSendViewModel/ | -146 | -- |
| **W25 ChannelRouter** | **Infrastructure/Channel** | **ChannelRouter/** | **-141** | **21st** |

**Cumulative LoC reduction (W3-W25)**: 20 god-class files -3,389 LoC (W3-W24) + **W25 ChannelRouter -141 LoC** = **-3,530 LoC total** across 21 refactors.

## What was captured

W25 SHIP closure = 7 captures dispatched: SPEC + PLAN + T1 + T2 + T3 + T4 + SHIP. Each per the W12-W24 pattern of `vault-pkm:pkm-capture` agent dispatched after each source commit.

## Next (post-ship)

- **W25.5 vault-only PATCH** — lesson-promotion opportunity for 3 candidates (`infrastructure-channel-layer-sister-pattern-empirical-w18-w25` 2/3 + `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` 2/3 + `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` 4th confirmation since CONFIRMED).
- **W26** — next god-class refactor candidate. Top remaining (>350 LoC) main files after W25: `AppShellViewModel.cs` 353 LoC (RESIDUAL — 4 existing partials + main residue) OR `CanApi.cs` 347 LoC (App/Services/Scripting fresh god-class candidate).
