# Release Notes v3.40.0 — CanApi god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.40.0
**Branch:** `feature/w26-can-api-god-class`
**Parent:** v3.39.5 PATCH (`faedaf2` on origin/main)

## Why this MINOR

`src/PeakCan.Host.App/Services/Scripting/CanApi.cs` had grown to **347 LoC** as of v3.39.5 — at 43.4% of the 800 LoC Round-1 ceiling. Single `public sealed partial class CanApi : IFrameSink, IScriptCanApi` (modifier pre-existed at line 22 — sister of 22/23 prior cases). 7 readonly fields + 1 ctor (with `_channelRouter.AttachSink(this)` subscription) + 9 public methods + 11 `[LoggerMessage]` partial declarations.

**1st multi-interface** god-class refactor in W3-W26 series (sister-of-W14 ScriptEngine + sister-deviation-logic-of-W25 ChannelRouter).

This is the **22nd god-class refactor** in the project (W3-W26 series). **2nd App/Services/Scripting sublayer** (sister of W14 ScriptEngine). **16th subdirectory-pattern deployment**.

## LoC trajectory (W8.5 D7 CONFIRMED formula — 37-locked)

Per task, `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`. All 3 transitions **EXACT match or within ±2 tolerance**.

| Task | Flow | Range deleted | LoC out | Main after |
|---|---|---|---|---|
| T1 | SinkLifecycle (OnFrame(CanFrame) LARGEST 62 LoC + OnError + Dispose) | 233-310 | 78 | 269 |
| T2 | CallbackRegistry (4 IScriptCanApi registry mutators) | 106-184 | 79 | 190 |
| T3 | SendAndQuery (Send+xml + IsConnected + GetChannelId, 3 non-contiguous ranges) | 56-98 + 110 + 148 | 45 | 145 |
| **Total** | -- | -- | **202** | **145** |

**Net**: 347 → 145 LoC main file (**-202 LoC, -58.2%**). Total project LoC across main + 3 partials ≈ 250 LoC (small +105 LoC overhead from per-file namespace + using directives + 3 xmldoc header comment blocks).

## What this MINOR does

### Refactor — CanApi adds 3 NEW partials in `CanApi/` subdirectory

1. **NEW `src/PeakCan.Host.App/Services/Scripting/CanApi/SinkLifecycle.partial.cs` (~135 LoC)**:
   - Contains `public void OnFrame(CanFrame frame)` (62 LoC LARGEST method — moves per W25 D5 deviation) + `public void OnError(Exception ex)` + `public void Dispose()` method bodies verbatim from HEAD L233-L310.
   - Verbatim re-extraction via `git show HEAD:src/.../CanApi.cs | sed -n '233,310p'` per W20 T2 R1 fabrication LESSON (24th application).
   - 1 using-directive fix per W19 (`using PeakCan.Host.Core;` for `CanFrame` type).

2. **NEW `src/PeakCan.Host.App/Services/Scripting/CanApi/CallbackRegistry.partial.cs` (~125 LoC)**:
   - Contains `public string OnFrame(Action<CanFrame> callback)` + `public void OffFrame(...)` + `public string OnMessage(...)` + `public void OffMessage(...)` method bodies verbatim from HEAD L106-L184.
   - Verbatim re-extraction via `git show HEAD:src/.../CanApi.cs | sed -n '106,184p'` per W20 T2 R1 fabrication LESSON (25th application).
   - 1 using-directive fix per W19 (`using PeakCan.Host.Core;` for `Action<CanFrame>` type).

3. **NEW `src/PeakCan.Host.App/Services/Scripting/CanApi/SendAndQuery.partial.cs` (~100 LoC)**:
   - Contains `public async Task<bool> Send(...)` (with xmldoc) + `public bool IsConnected()` (1 LoC) + `public string? GetChannelId()` (1 LoC) method bodies verbatim from HEAD L56-L98 + L110 + L148 (3 non-contiguous ranges).
   - Verbatim re-extraction via 3 separate `git show HEAD:` + `sed -n '<range>p'` commands per W20 LESSON (26th application).
   - 1 using-directive fix per W19 (`using System.Globalization;` for `CultureInfo.InvariantCulture`).

### D1-D7 sister-pattern decisions (carried from W26 SPEC)

- **D1**: 3 NEW partials (`SinkLifecycle` + `CallbackRegistry` + `SendAndQuery`) in `CanApi/` subdirectory. 16th subdirectory-pattern deployment.
- **D2**: No `partial` modifier edit needed (already partial at line 22).
- **D3**: 7 readonly fields (`_logger` + `_sendService` + `_channelRouter` + 3 `ConcurrentDictionary` + `_callbackCounter`) + 1 ctor + 11 `[LoggerMessage]` partials + class xmldoc stay in main.
- **D4**: All 11 `[LoggerMessage]` partials stay on `CanApi` main partial declaration per W18 R1 + W22 D4 + W23 D4 + W25 D4 sister precedent (CS8795 mitigation).
- **D5**: `OnFrame(CanFrame frame)` 62 LoC LARGEST method **moves to `SinkLifecycle.partial.cs`** per W25 D5 deviation logic (frame-arrives → callback-fanout discrete dispatcher, sister of W25 OnChannelFrame 75 LoC which moved).
- **D6**: Branch name `feature/w26-can-api-god-class`.
- **D7**: Order largest-first per W12+W14+W18+W22+W23+W24+W25 D7 sister: **A (SinkLifecycle, 80 LoC) → B (CallbackRegistry, 80 LoC) → C (SendAndQuery, 50 LoC)**.

## What this MINOR does NOT change (YAGNI + sister-pattern invariants)

- No public/internal API change.
- No test changes (51 CanApi + Script tests pass without modification).
- No facade pattern (W3-W25 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk (verified — tests do not path-grep main file content).
- No CS8795 risk (D4 keeps all 11 `[LoggerMessage]` partials on main partial declaration).
- No `IFrameSink` interface signature change (auto-implemented across partials).
- No `IScriptCanApi` interface signature change (auto-implemented across partials).
- No V8 scripting API surface change (`can.send()` + `can.onFrame()` + `can.onMessage()` etc. still work).
- No `_channelRouter.AttachSink(this)` relocation (stays in ctor per D3 + D5 sister).
- No `_sendService` or DI registration path change.

## Architecture milestones

- **22nd god-class refactor SHIPPED** (W3-W26 series).
- **2nd App/Services/Scripting sublayer** (sister of W14 ScriptEngine).
- **16th subdirectory-pattern deployment**.
- **W20 LESSON APPLIED 26 times total** across W26 T1+T2+T3 (verbatim re-extraction).
- **W23 STRUCT-FABRICATION LESSON APPLIED 6th time since 3/3 CONFIRMED** at W26 T3 (verified `CanFrame` 5-arg ctor + `CanId` 2-arg ctor + `FrameFormat` enum + `FrameFlags` enum).
- **W25 D5 deviation APPLIED 2nd time** at W26 T1 (OnFrame(CanFrame) 62 LoC moves to SinkLifecycle.partial.cs because frame-arrives → callback-fanout is sharp discrete flow).
- **W19 R1 first-correction APPLIED 26th+27th+28th time** at W26 T1+T2+T3 (3 using-directive fixes).
- **W17 wc-l-splitlines CONFIRMED 37-locked** (cp1252 binary read+write to handle Windows-1252 non-ASCII bytes in xmldoc + comments).
- **3 NEW 1/3 → 2/3/3/3 + 4/5 sister-lesson candidates promoted**:
  - `multi-interface-partial-class-iframesink-and-iscriptcanapi` (NEW 1/3 at W26 — `CanApi : IFrameSink, IScriptCanApi` is 1st multi-interface partial in W3-W26 series; both interface methods cleanly partition).
  - `add-partial-keyword-to-monolithic-class-before-extraction` (W25 2/3 → **W26 3/3 CONFIRMED**: CanApi already partial at line 22 = 24th confirmation of pre-existed-partial pattern).
  - `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` (W25 3/3 → **W26 4/3 CONFIRMED since W25 3/3**; OnFrame(CanFrame) 62 LoC moves as 2nd confirmation).

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings (after W26 T1+T2+T3 using-directive fixes per W19).
- `dotnet test --filter "~CanApi|~Script"`: 51/51 PASS (matches pre-W26 baseline).
- `dotnet test` (full solution): 0 new fails (1 transient flaky AscParser pre-existing per W13 T1 + W14 + W15 + W16 + W17 + W18 + W19 + W20 + W22 + W23 + W24 + W25 sister pattern retained).

## Process lessons applied (W20 + W22 + W23 + W24 + W25 + W19)

- **Lesson #10** (verify each commit before proceeding): each W26 T1-T3 build + filter-test verified before commit.
- **Lesson #11** (partial-class using-directives file-scoped, not class-scoped): W26 T1+T2+T3 using-directive fixes (`PeakCan.Host.Core` + `System.Globalization`).
- **W19 R1 first-correction**: re-grep boundaries before each deletion script (26th + 27th + 28th application in W26).
- **W20 T2 R1 fabrication LESSON**: 26 verbatim re-extractions across W26 T1+T2+T3 (24+25+26th cumulative W20 LESSON applications).
- **W23 STRUCT-FABRICATION LESSON**: W26 T3 verified `CanFrame` 5-arg + `CanId` 2-arg + `FrameFormat` + `FrameFlags` enum signatures (6th observation since 3/3 CONFIRMED).
- **W25 D5 deviation**: W26 T1 applied 2nd time (sister of W25 OnChannelFrame 73 LoC moved; W26 OnFrame(CanFrame) 62 LoC moved).

## Sister-pattern cumulative trajectory (god-class series, W3-W26)

| W | Layer | Subdirectory | Main LoC | 22 prior + W26 |
|---|---|---|---|---|
| W3-W11 | App + Core | various | -1,400 | 9 god-classes |
| W12 UdsClient | Core | UdsClient/ | -89 | 10th |
| W13 AscParser | Core | AscParser/ | -150 | 11th |
| W14 ScriptEngine | App/Services/Scripting | ScriptEngine/ | -187 | 12th |
| W15 ReplayTimeline | Core | ReplayTimeline/ | -85 | 13th |
| W16 ReplayViewModel | App/ViewModels | ReplayViewModel/ | -180 | 14th |
| W17 vault-only PATCH | (meta) | -- | 0 | (no source) |
| W18 PeakCanChannel | Infrastructure/Channel | PeakCanChannel/ | -228 | 15th |
| W19 TraceViewModel | App/ViewModels | TraceViewModel/ | -130 | 16th |
| W20 TraceViewerViewModel | App/ViewModels | TraceViewerViewModel/ | -91 | 17th |
| W21 SignalChartViewModel | App/ViewModels | SignalChartViewModel/ | -232 | 18th |
| W22 RecordService | App/Services | RecordService/ | -193 | 19th |
| W23 CyclicDbcSendService | App/Services | CyclicDbcSendService/ | -288 | 20th |
| W24 DbcSendViewModel | App/ViewModels | DbcSendViewModel/ | -146 | 21st |
| W25 ChannelRouter | Infrastructure/Channel | ChannelRouter/ | -141 | -- |
| **W26 CanApi** | **App/Services/Scripting** | **CanApi/** | **-202** | **22nd** |

**Cumulative LoC reduction (W3-W26)**: 21 god-class files -3,732 LoC (W3-W25) + **W26 CanApi -202 LoC** = **-3,934 LoC total** across 22 god-class refactors + 2 vault-only PATCHes.

## What was captured

W26 SHIP closure = 7 captures dispatched: SPEC + PLAN + T1 + T2 + T3 + T4 + SHIP. Each per the W12-W25 pattern of `vault-pkm:pkm-capture` agent dispatched after each source commit.

## Next (post-ship)

- **W26.5 vault-only PATCH** — lesson-promotion opportunity for 2 NEW 1/3 candidates (`multi-interface-partial-class-iframesink-and-iscriptcanapi` 1/3 + `add-partial-keyword-to-monolithic-class-before-extraction` W26 3/3 CONFIRMED).
- **W27** — next god-class refactor candidate. Top remaining (>250 LoC) main files after W26: `RecentSessionsService.cs` 334 LoC (App/Services sister of W22+W23) OR `AppHostBuilder.cs` 316 LoC (App/Composition DI引导) OR `DbcService.cs` 312 LoC (App/Services sister).
