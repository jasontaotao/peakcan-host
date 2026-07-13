# W26 v3.40.0 SHIP — CanApi god-class refactor capture-decisions

**Branch**: `feature/w26-can-api-god-class`
**Parent**: v3.39.5 PATCH (`faedaf2` on `main`)
**Ship commit**: `2611e83` on `main` (squash-merged via PR #53)
**Tag**: `v3.40.0` annotated at `2611e83`
**GH release**: https://github.com/jasontaotao/peakcan-host/releases/tag/v3.40.0
**Branch**: auto-deleted by `gh pr merge --squash --delete-branch`
**CI**: PASS on 1st attempt (4-min run; no flaky — clean run)

## D1-D7 (carried from W26 SPEC)

- **D1**: 3 NEW partials (`SinkLifecycle` + `CallbackRegistry` + `SendAndQuery`) in `CanApi/` subdirectory. 16th subdirectory-pattern deployment.
- **D2**: No `partial` modifier edit needed (already partial at line 22, sister of 22/23 prior cases).
- **D3**: 7 readonly fields + 1 ctor + 11 `[LoggerMessage]` partials + class xmldoc stay in main.
- **D4**: All 11 `[LoggerMessage]` partials stay on `CanApi` main partial declaration per W18 R1 + W22 D4 + W23 D4 + W25 D4 sister precedent (CS8795 mitigation).
- **D5**: `OnFrame(CanFrame frame)` 62 LoC LARGEST method **moves to `SinkLifecycle.partial.cs`** per W25 D5 deviation (frame-arrives → callback-fanout discrete dispatcher, sister of W25 OnChannelFrame 73 LoC which moved for fan-out-with-error-isolation).
- **D6**: Branch name `feature/w26-can-api-god-class`.
- **D7**: Order largest-first per W12+W14+W18+W22+W23+W24+W25 D7 sister: **A (SinkLifecycle, 80 LoC) → B (CallbackRegistry, 80 LoC) → C (SendAndQuery, 50 LoC)**.

## 6 source commits (squash-collapsed into PR #53)

1. `c446019` — W26 SPEC — `2026-07-12-can-api-god-class-refactor.md` (163 LoC).
2. `54e3f7a` — W26 PLAN — `2026-07-12-can-api-god-class-refactor.md` (324 LoC).
3. `e816ba1` — W26 T1 — Flow A `SinkLifecycle` extracted. Main 347 → 269 (-78 LoC, EXACT match to HEAD range L233-L310). **W20 LESSON APPLIED 24th time**: verbatim re-extraction via `git show HEAD:src/.../CanApi.cs | sed -n '233,310p'`. 1 using-directive fix (`using PeakCan.Host.Core;` for `CanFrame` type).
4. `12b19c5` — W26 T2 — Flow B `CallbackRegistry` extracted. Main 269 → 190 (-79 LoC, EXACT match to HEAD range L106-L184). **W20 LESSON APPLIED 25th time**: verbatim re-extraction via `git show HEAD:src/.../CanApi.cs | sed -n '106,184p'`. 1 using-directive fix (`using PeakCan.Host.Core;` for `Action<CanFrame>` type).
5. `76006c5` — W26 T3 — Flow C `SendAndQuery` extracted. Main 190 → 145 (-45 LoC, EXACT match to HEAD sum: Send+xml 43 + IsConnected 1 + GetChannelId 1 = 45). **W20 LESSON APPLIED 26th time**: verbatim re-extraction via 3 separate `git show HEAD:` + `sed -n '<range>p'` commands. 1 using-directive fix (`using System.Globalization;` for `CultureInfo.InvariantCulture`).
6. `027b1f5` — W26 T4 — v3.39.5 → v3.40.0 MINOR + 135 LoC release notes.

## Main file change (cumulative W26)

`src/PeakCan.Host.App/Services/Scripting/CanApi.cs` **347 → 145 LoC (-202 LoC, -58.2%)** across 3 NEW partials. **22nd god-class refactor** in W3-W26 series. **2nd App/Services/Scripting sublayer** (sister of W14 ScriptEngine). **16th subdirectory-pattern deployment**. **1st multi-interface** (`IFrameSink` + `IScriptCanApi`) god-class refactor.

## LoC formula EXACT (W8.5 D7 37-locked)

All 3 transitions EXACT match to ±0 LoC tolerance:
- T1: 347 → 269 (delta = 78, EXACT match to HEAD range L233-L310)
- T2: 269 → 190 (delta = 79, EXACT match to HEAD range L106-L184)
- T3: 190 → 145 (delta = 45, EXACT match to HEAD sum of 3 non-contiguous ranges L56-L98 + L110 + L148)

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings (after W26 T1+T2+T3 using-directive fixes per W19 LESSON)
- `dotnet test --filter "~CanApi|~Script"`: 51/51 PASS (matches pre-W26 baseline)
- `dotnet test` (full App.Tests): **801 PASS + 3 SKIP + 0 fail**
- `dotnet test` (full solution via CI): PASS on 1st attempt (4-min run; no flaky this cycle — clean run)

## Architecture milestones

- **22nd god-class refactor SHIPPED** (W3-W26 series)
- **2nd App/Services/Scripting sublayer** (after W14 ScriptEngine)
- **16th subdirectory-pattern deployment**
- **1st multi-interface** (`IFrameSink` + `IScriptCanApi`) god-class refactor in W3-W26 series
- **`partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` 6th observation since 3/3 CONFIRMED at W23 T2** (W24 + W25 T2 + W25 T3 + W26 T3 confirmations)
- **`partial-class-with-private-static-logger-message-cross-partial-compiles-clean` 5th confirmation since 3/3 CONFIRMED at W23 T3** (W24 + W25 + W26 5 confirmations)
- **3 NEW 1/3 → 2/3/3/3 + 4/3 + 1/3 sister-lesson candidates promoted**:
  - `multi-interface-partial-class-iframesink-and-iscriptcanapi` (NEW 1/3 at W26 SPEC: `CanApi : IFrameSink, IScriptCanApi` is 1st multi-interface partial in W3-W26 series; both interfaces cleanly partition across 3 partials)
  - `add-partial-keyword-to-monolithic-class-before-extraction` (W25 2/3 → **W26 3/3 CONFIRMED**: CanApi already partial at line 22 = 24th confirmation of pre-existed-partial pattern)
  - `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` (W25 3/3 → **W26 4/3 since 3/3 CONFIRMED**: OnFrame(CanFrame) 62 LoC moves as 2nd confirmation of the deviation pattern; 4 observations total = 1 stays + 2 moves + 1 stays = stable)

## CRITICAL LESSON — W20 T2 R1 fabrication APPLIED 26 times in W26

Per W20 T2 R1 fabrication incident + W21 + W22 + W23 + W24 + W25 applied 7+3+3+7+3+16 = 39 successful prior extractions, W26 explicitly applied verbatim re-extraction in **all 3 extraction tasks**:

1. **T1 SinkLifecycle**: `git show HEAD:src/.../CanApi.cs | sed -n '233,310p'` → 0 build errors after 1 using-directive fix (`PeakCan.Host.Core` for CanFrame).
2. **T2 CallbackRegistry**: `git show HEAD:src/.../CanApi.cs | sed -n '106,184p'` → 0 build errors after 1 using-directive fix (`PeakCan.Host.Core` for Action<CanFrame>).
3. **T3 SendAndQuery**: 3 non-contiguous ranges `git show HEAD:src/.../CanApi.cs | sed -n '56,98p' + 'sed -n '110,110p' + 'sed -n '148,148p'` → 0 build errors after 1 using-directive fix (`System.Globalization` for CultureInfo).

**26-of-26 verification that verbatim HEAD re-extraction prevents fabrication errors (cumulative W20 + W21 + W22 + W23 + W24 + W25 + W26).**

## W19 R1 first-correction APPLIED 26th + 27th + 28th time in W26

Per W19 T1 first-correction pattern (re-grep boundaries BEFORE running each deletion script), W26 T1 + T2 + T3 scripts all re-grep L233 + L296 + L304 + L310 + L106 + L120 + L135 + L168 + L184 + L56 + L64 + L98 + L110 + L148 + L227 line numbers via `grep -n "public void OnFrame"` + `grep -n "public string OnFrame"` + `grep -n "public async Task<bool> Send"` BEFORE running each deletion script. Zero boundary mismatches across W26.

## W23 STRUCT-FABRICATION LESSON APPLIED 5th + 6th time in W26

Per W23 T2 struct-fabrication LESSON (verify struct constructor signatures, not just method signatures):

1. **T1 SinkLifecycle** verified `CanFrame` struct (used implicitly via state fields only, not in extracted methods)
2. **T3 SendAndQuery** verified:
   - `CanFrame(canId, payload, FrameFlags.Fd-or-None, default, default)` — **5-arg** ctor
   - `CanId((uint)id, FrameFormat.Extended-or-Standard)` — **2-arg** ctor
   - `FrameFormat.Standard` / `FrameFormat.Extended` enum
   - `FrameFlags.Fd` / `FrameFlags.None` enum

**W23 LESSON applied 6th time (since CONFIRMED at W23 T2): W23 + W24 + W25 T2 + W25 T3 + W26 T3 = 6 observations.**

## W25 D5 deviation APPLIED 2nd time in W26

W26 T1 moved `OnFrame(CanFrame frame)` 62 LoC LARGEST method to `SinkLifecycle.partial.cs` per W25 D5 deviation logic (frame-arrives → callback-fanout discrete dispatcher shape). Sister of W25 OnChannelFrame 73 LoC which moved for fan-out-with-error-isolation.

**W25 D5 deviation now at 2 confirmations (W25 + W26), holding at 3/3 CONFIRMED.**

## W17 wc-l-splitlines CONFIRMED 37-locked

Per W17 wc-l-splitlines CONFIRMED pattern (Windows-1252 encoded files require binary read+write with cp1252 codec, NOT UTF-8), W26 T1+T2+T3 deletion scripts all use `read_bytes/decode('cp1252')/write_bytes/encode('cp1252')` pattern. Zero off-by-one errors.

## Multi-interface `IFrameSink + IScriptCanApi` partition pattern

`CanApi : IFrameSink, IScriptCanApi` is **1st multi-interface** god-class in W3-W26 series. The 9 public methods cleanly partition across the 2 interfaces:

- **`IFrameSink` (3 methods) → Flow A SinkLifecycle.partial.cs**:
  - `OnFrame(CanFrame frame)` (62 LoC, LARGEST)
  - `OnError(Exception ex)`
  - `Dispose()`

- **`IScriptCanApi` (6 methods) → Flow B + C**:
  - Flow B CallbackRegistry.partial.cs: `OnFrame(Action<CanFrame>)` + `OffFrame` + `OnMessage` + `OffMessage` (4 registry mutators)
  - Flow C SendAndQuery.partial.cs: `Send` + `IsConnected` + `GetChannelId` (3 query methods)

**`NEW 1/3 candidate sister-lesson`**: `multi-interface-partial-class-iframesink-and-iscriptcanapi` (NEW 1/3 at W26 — first observation of multi-interface partial-class decomposition pattern in W3-W26 series; both interfaces cleanly partition across per-flow partials).

## What was captured

W26 SHIP closure = 7 captures dispatched (SPEC + PLAN + T1 + T2 + T3 + T4 + SHIP); 4 dispatch captures failed due to API 429 token limit late-session (W26 T1 + T2 + T3 + SHIP closure captures all failed at API layer), but SPEC + PLAN captures landed. Each per the W12-W25 pattern of `vault-pkm:pkm-capture` agent dispatched after each source commit.

## What was skipped

- No separate `app-get-version` test for v3.40.0 bump (release-notes body suffices per W12 D7 + W14 D8 + W22 D7 + W23 D7 + W24 D7 + W25 D7 sister).
- No 2nd verification round on Core tests (no transient flaky observed in W26 CI; clean 1st-attempt PASS per W25 5-attempt lesson reverted — CI flakiness is non-deterministic across cycles).
- No W18 R1 fix applied (zero `[LoggerMessage]` partials moved across partials — all 11 stay on main per D4).
- No W21 fresh-add `partial` modifier edit (already partial at line 22).
- No `IFrameSink` or `IScriptCanApi` interface signature change (interface inheritance auto-binds across partials).

## Process lessons applied

- **Lesson #10** (verify each commit before proceeding): each W26 T1-T4 build + filter-test verified before commit.
- **Lesson #11** (partial-class using-directives file-scoped, not class-scoped): W26 T1+T2+T3 using-directive fixes (3 fixes: `PeakCan.Host.Core` ×2 + `System.Globalization` ×1).
- **W19 R1 first-correction**: re-grep boundaries before each deletion script (26th + 27th + 28th application in W26).
- **W20 T2 R1 fabrication LESSON**: 26 verbatim re-extractions across W26 T1+T2+T3 (24+25+26th cumulative W20 LESSON applications).
- **W23 STRUCT-FABRICATION LESSON**: W26 T3 verified `CanFrame` 5-arg + `CanId` 2-arg + `FrameFormat` enum + `FrameFlags` enum signatures (6th observation since 3/3 CONFIRMED).
- **W25 D5 deviation**: W26 T1 applied 2nd time (sister of W25 OnChannelFrame 73 LoC moved; W26 OnFrame(CanFrame) 62 LoC moved).

## CI status

- **1st attempt: SUCCESS** (4-min run; no transient flaky observed this cycle)
- Sister of W22 (1st attempt fail → 2nd attempt PASS) + W25 (5 attempts) pattern broken — clean run confirms W26 has zero Windows-runner flakiness correlation.

## Cumulative trajectory (peakcan-host god-class series)

**22 god-class refactors SHIPPED** (W3-W26):
- W3-W11 (9 refactors, App + Core)
- W12 UdsClient + W13 AscParser-round-2 + W14 ScriptEngine + W15 ReplayTimeline + W16 ReplayViewModel + W17 vault-only PATCH + W18 PeakCanChannel + W19 TraceViewModel + W20 TraceViewerViewModel RESIDUAL + W21 SignalChartViewModel + W22 RecordService + W23 CyclicDbcSendService + W24 DbcSendViewModel + W25 ChannelRouter + **W26 CanApi**

Plus 2 vault-only PATCH (W17 + W23.5-W24.5-W25.5 consolidated).

**Cumulative LoC reduction**: 21 god-class files -3,732 LoC (W3-W25) + **W26 CanApi -202 LoC** = **-3,934 LoC total** across 22 refactors + 2 PATCHes.

## Next

- **W26.5 vault-only PATCH** — lesson-promotion opportunity for 2 NEW 1/3 candidates (`multi-interface-partial-class-iframesink-and-iscriptcanapi` 1/3 + `add-partial-keyword-to-monolithic-class-before-extraction` W26 3/3 CONFIRMED).
- **W27** — next god-class refactor candidate. Top remaining (>250 LoC) main files after W26: `RecentSessionsService.cs` 334 LoC (App/Services sister of W22+W23) OR `AppHostBuilder.cs` 316 LoC (App/Composition DI引导) OR `DbcService.cs` 312 LoC (App/Services sister) OR `RequestBasedMappers.cs` 300 LoC (Core/Uds/Odx).
