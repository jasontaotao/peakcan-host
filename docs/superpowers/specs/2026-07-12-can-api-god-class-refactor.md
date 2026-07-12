# W26 SPEC — CanApi god-class refactor (22nd overall)

**Date**: 2026-07-12
**Status**: APPROVED (user delegation "W26 god-class" + auto-target selection)
**Target version**: v3.40.0 MINOR
**Branch**: `feature/w26-can-api-god-class`
**Sister pattern**: W14 ScriptEngine (App/Services/Scripting sister layer); W25 ChannelRouter (most recent god-class refactor, largest-method-can-move deviation precedent).

## Context

`src/PeakCan.Host.App/Services/Scripting/CanApi.cs` 347 LoC is the next god-class candidate in the W3-W25 series (21 refactors shipped + 1 vault-only PATCH = 22 cycles total). The class is **already `public sealed partial class CanApi : IFrameSink, IScriptCanApi`** at line 22 (`partial` pre-existed — sister of 22/23 prior cases).

`CanApi` is the JS-scripting adapter that exposes CAN bus operations to the V8 engine + acts as an `IFrameSink` consumer for `ChannelRouter` fanout. **Multi-interface** (`IFrameSink` + `IScriptCanApi`) — 1st time in W3-W26 series. 7 readonly fields + 1 ctor (with `_channelRouter.AttachSink(this)` subscription) + 9 public methods + 11 `[LoggerMessage]` partials.

The 9 methods partition cleanly into 3 flow boundaries:
- **`IFrameSink` lifecycle** (OnFrame(CanFrame) sink dispatcher + OnError + Dispose, ~80 LoC)
- **`IScriptCanApi` callback registry** (4 registry mutators OnFrame(Action) + OffFrame + OnMessage + OffMessage, ~80 LoC)
- **`IScriptCanApi` send + query** (Send + IsConnected + GetChannelId, ~50 LoC)

## Architecture

Sister pattern of W14 ScriptEngine + W25 ChannelRouter. 22nd god-class refactor overall. **7th App/Services layer** + **2nd App/Services/Scripting sublayer** (after W14 ScriptEngine) + **16th subdirectory-pattern deployment**.

### W26 D1-D7

- **D1**: 3 NEW partials (`SinkLifecycle` + `CallbackRegistry` + `SendAndQuery`) in `CanApi/` subdirectory.
- **D2**: No `partial` modifier edit needed (already partial at line 22, sister of 22/23 prior cases).
- **D3**: 7 readonly fields (`_logger` + `_sendService` + `_channelRouter` + 3 `ConcurrentDictionary` + `_callbackCounter`) + 1 ctor + 11 `[LoggerMessage]` partials + class xmldoc stay in main.
- **D4**: All 11 `[LoggerMessage]` partials stay on `CanApi` main partial declaration per W18 R1 + W22 D4 + W23 D4 + W25 D4 sister precedent (CS8795 mitigation: keeping `[LoggerMessage]` partials on same-class declaration avoids the "no logging method found in corresponding source generator declaration" warning).
- **D5**: `OnFrame(CanFrame frame)` 62 LoC LARGEST method **moves to `SinkLifecycle.partial.cs`** per W25 D5 deviation logic (frame-arrives → callback-fanout is sharp discrete dispatcher shape, sister of W25 OnChannelFrame 75 LoC which moved for fan-out-with-error-isolation). 1st `IFrameSink` partial-class scenario in W3-W26 series.
- **D6**: Branch name `feature/w26-can-api-god-class`.
- **D7**: Order largest-first per W12+W14+W18+W22+W23+W24+W25 D7 sister pattern: **A (SinkLifecycle, 80 LoC) → B (CallbackRegistry, 80 LoC) → C (SendAndQuery, 50 LoC)**.

### Flow boundaries (Phase 1 verified)

**Stays in main (~217 LoC, target)**:
- `using` block (1-6) + namespace + class xmldoc (10-21) + outer class declaration (22) — already partial
- 7 readonly fields: 3 service deps (`_logger` + `_sendService` + `_channelRouter`) + 3 `ConcurrentDictionary` state (`_frameCallbacks` + `_messageCallbacks` + `_prefixCallbacks`) + 1 counter (`_callbackCounter`)
- 1 ctor (L39-L54, **~16 LoC, contains `_channelRouter.AttachSink(this)` subscription which is essential Flow A wiring** — STAYS INLINE per W14+W18+W22+W23+W24+W25 D5 sister-principle)
- 11 `[LoggerMessage]` partial method declarations: `LogInvalidCanId` + `LogSendEmptyData` + `LogDataTooLong` + `LogSendFailed` + `LogFrameCallbackRegistered` + `LogFrameCallbackUnregistered` + `LogMessageCallbackRegistered` + `LogPrefixCallbackRegistered` + `LogFrameCallbackError` + `LogMessageCallbackError` + `LogPrefixCallbackError` + `LogSinkError` (L312-end, ~50 LoC)

**Flow A — SinkLifecycle (~80 LoC) → `CanApi/SinkLifecycle.partial.cs`:**
- `public void OnFrame(CanFrame frame)` (L233-L294, 62 LoC, LARGEST single method) — **moves** per W25 D5 deviation
- `public void OnError(Exception ex)` (L296-L302, ~7 LoC)
- `public void Dispose()` (L304-L310, ~7 LoC)

Touches: 3 `ConcurrentDictionary` fields (read-only iteration in OnFrame + write-clear in Dispose) + 2 `[LoggerMessage]` partials called from OnFrame (`LogFrameCallbackError` + `LogMessageCallbackError` + `LogPrefixCallbackError`).

**Flow B — CallbackRegistry (~80 LoC) → `CanApi/CallbackRegistry.partial.cs`:**
- `public string OnFrame(Action<CanFrame> callback)` (L106-L118, ~13 LoC)
- `public void OffFrame(string callbackId)` (L120-L133, ~14 LoC)
- `public string OnMessage(object id, Action<CanFrame> callback)` (L135-L166, ~32 LoC)
- `public void OffMessage(object id, string callbackId)` (L168-L187, ~20 LoC)

Touches: 3 `ConcurrentDictionary` (mutate) + `_callbackCounter` (read + increment) + `AttachSink(this)` is in ctor (NOT in Flow B) + 6 `[LoggerMessage]` partials (`LogFrameCallbackRegistered` + `LogFrameCallbackUnregistered` + `LogMessageCallbackRegistered` + `LogPrefixCallbackRegistered`).

**Flow C — SendAndQuery (~50 LoC) → `CanApi/SendAndQuery.partial.cs`:**
- `public async Task<bool> Send(int id, byte[] data, bool fd = false, bool extended = false)` (L64-L104, ~41 LoC)
- `public bool IsConnected() => _sendService.ActiveChannel is { IsConnected: true };` (L189, 1 LoC)
- `public string? GetChannelId() => _sendService.ActiveChannel?.Id.Handle.ToString("X2", CultureInfo.InvariantCulture);` (L227, 1 LoC)
- ~7 LoC of helper code (around L210-L227 — `CanFrame`/`CanId` constructors sister to W23 + W25 verifying)

Touches: `_sendService` + `_logger` + 4 `[LoggerMessage]` partials (`LogInvalidCanId` + `LogSendEmptyData` + `LogDataTooLong` + `LogSendFailed`).

### Notes on cross-flow references

- **ctor wiring stays in main** because `_channelRouter.AttachSink(this)` in the ctor is the subscription hook for `IFrameSink` lifecycle (Flow A). Per W14 + W23 + W25 D5 sister-principle: ctor body = DI wiring + lifecycle hooks = STAYS INLINE.
- **3 `ConcurrentDictionary` state fields stay in main** because they're cross-flow state (read in Flow A's `OnFrame(CanFrame)` sink dispatch + mutated in Flow B's registry methods + never touched in Flow C).
- `OnFrame(CanFrame frame)` in Flow A iterates the 3 dicts + invokes callbacks via `try { } catch { logger }` pattern, **same fan-out-with-error-isolation shape as W25 `OnChannelFrame`** (sister-deviation-logic). Sister-of-W25 confirmed.
- `OnFrame(Action<CanFrame> callback)` in Flow B is a different signature (different parameter type) — NO name-collision risk (partial-class method-name resolution is by full signature).

## LoC trajectory

| Task | Flow | Range (1-indexed) | LoC deleted | Markers | LoC main after |
|---|---|---|---|---|---|
| T1 | A — SinkLifecycle | TBD per Phase 1 exact grep (around OnFrame(CanFrame) L233-L294 + OnError + Dispose L296-L310) | ~80 | 1 | ~267 |
| T2 | B — CallbackRegistry | TBD per Phase 1 exact grep (around 4 registry methods L106-L187) | ~80 | 1 | ~187 |
| T3 | C — SendAndQuery | TBD per Phase 1 exact grep (around Send + IsConnected + GetChannelId L64-L104 + helpers L189-L227) | ~50 | 1 | ~137 |
| T4 | v3.39.5 -> v3.40.0 | (no source) | 0 | 0 | ~137 |

Cumulative: 347 → ~267 → ~187 → ~137 main. Re-grep + range verify after each task per W19 R1 first-correction + W17 wc-l-splitlines CONFIRMED + W20 fabrication LESSON APPLIED 23+ times in W25 + W23 STRUCT-FABRICATION LESSON.

## W20 + W23 LESSON APPLIED — verbatim re-extract + struct-ctor verification

Per W20 T2 R1 fabrication incident + W23 T2 3-fix-cycle + W25 T2 Array.Copy 5-arg verification:

1. **Re-grep boundaries BEFORE running each deletion script** (Phase 1 explore done; re-verify before each T1/T2/T3 script run).
2. **Re-extract original code from HEAD via `git show HEAD:src/...cs | sed -n '<range>p'`** for each partial's verbatim content.
3. **Verify `CanFrame` + `CanId` + `FrameFormat` struct-ctor signatures** in Flow C — sister of W23 + W24 + W25 LESSON (W23 1st applied, W24 2nd, W25 3rd + 4th).
4. **Verify `ConcurrentDictionary<TKey, TValue>` 7-arg constructor** signatures if used (e.g. `new ConcurrentDictionary<string, Action<CanFrame>>(StringComparer)`) — sister-extension of struct-ctor LESSON.
5. **Verify `Volatile.Read/Write` semantics** NOT USED here (different from W25 ChannelRouter; CanApi uses `ConcurrentDictionary` which has its own thread-safety).
6. **Build + run filter tests after each task** to catch any extraction errors immediately.

## Sister-lesson candidates to monitor

| Lesson | Status | What W26 might observe |
|---|---|---|
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | 3/3 CONFIRMED (W21) | W26 22nd god-class application (T1+T2+T3); 24th+25th+26th W20 LESSON application |
| `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` | 3/3 CONFIRMED (W23+W24+W25 4-of-1 → 5/5 since CONFIRMED) | W26 6th observation since CONFIRMED — verify `CanFrame` 5-arg + `CanId` 2-arg + `FrameFormat` enum + `ChannelId.None` |
| `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` | 3/3 CONFIRMED (W23) | W26 5th confirmation (11 [LoggerMessage] partials on main, called from 3 per-flow partials) |
| `backing-fields-and-relaycommand-attribute-methods-must-stay-in-main-partial-while-partial-void-hooks-can-move` | NEW 1/3 (W24) | W26: 3 ConcurrentDictionary state fields stay in main per W26 D3 (cross-flow state); no [ObservableProperty] backing fields nor [RelayCommand] methods in CanApi |
| `add-partial-keyword-to-monolithic-class-before-extraction` | W25 2/3 → W26 3/3 CONFIRMED | W26: class already partial at line 22 (sister of 22 prior cases); 23rd confirmation (3/3 CONFIRMED via W25 2/3 → W26 3/3) |
| `subdirectory-partials-pattern-empirical-26-precedents` | 3/3 CONFIRMED (W20) | W26 16th deployment, sister-of-W14 (same App/Services/Scripting sublayer) |
| `infrastructure-channel-layer-sister-pattern-empirical-w18-w25` | W25 2/3 → W26 held | W26 is App/Services/Scripting sister of W14, NOT Infrastructure/Channel — held |
| `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` | W25 3/3 CONFIRMED | W26 2nd observation CONFIRMED: `OnFrame(CanFrame)` 62 LoC moves to SinkLifecycle.partial.cs (frame-arrives → callback-fanout discrete flow, sister of W25 OnChannelFrame 75 LoC moved for fan-out-with-error-isolation) |
| `multi-interface-partial-class-iframesink-and-iscriptcanapi` | NEW W26 1/3 candidate | W26 1st observation: `CanApi : IFrameSink, IScriptCanApi` is 1st multi-interface partial in W3-W26 series. Both interface methods cleanly partition: `IFrameSink` (OnFrame+OnError+Dispose) → Flow A, `IScriptCanApi` (OnFrame(Action)+OffFrame+OnMessage+OffMessage+Send+IsConnected+GetChannelId) → Flow B+C. Multi-interface partial-class decomposition is a stable pattern. |

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings (after W26 T1+T2+T3 using-directive fixes per W19 LESSON)
- `dotnet test --filter "~CanApi"`: pass without modification (no public/internal API change)
- `dotnet test --filter "~Script"` or full App.Tests: 0 new fails (1 transient flaky AscParser per W13 T1 sister pattern retained)
- `wc -l src/PeakCan.Host.App/Services/Scripting/CanApi.cs` ≤ 150 LoC (target ~137)
- 3 NEW partial files in `CanApi/` directory
- 7 readonly fields + 1 ctor + 11 `[LoggerMessage]` partials remain in main
- 9 methods (OnFrame(CanFrame)+OnError+Dispose+OnFrame(Action)+OffFrame+OnMessage+OffMessage+Send+IsConnected+GetChannelId) move to per-flow partials (3+4+3 = 10 method moves; minor math correction noted)
- DI registration unchanged (`AddSingleton<CanApi>()` factory in AppServicesFlow.cs)
- `IFrameSink` + `IScriptCanApi` interfaces unchanged (auto-implemented across partials)
- V8 scripting binding unchanged (`can.send()` + `can.onFrame()` + `can.onMessage()` etc. still work)
- Tag v3.40.0 + GH release published
- Branch deleted post-merge

## Out of scope (YAGNI)

- No public/internal API change.
- No test changes (CanApi tests + Script tests pass without modification).
- No facade pattern (W3-W25 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk (verified — tests do not path-grep main file content).
- No CS8795 risk (D4 keeps all 11 [LoggerMessage] partials on main partial declaration).
- No `AttachSink(this)` relocation (stays in ctor per D3 + D5 sister).
- No V8 scripting API surface change.
- No `IScriptCanApi` interface signature change.

## Sister-pattern progress

| W | Layer | Subdirectory | Main LoC | Partial LoC | Total |
|---|---|---|---|---|---|
| W14 | App/Services/Scripting | Scripting/ScriptEngine/ | -187 | +3 partials | 12th god-class |
| W22 | App/Services | RecordService/ | -193 | +3 partials | 18th god-class |
| W23 | App/Services | CyclicDbcSendService/ | -288 | +3 partials | 19th god-class |
| W24 | App/ViewModels | DbcSendViewModel/ | -146 | +3 partials | 20th god-class |
| W25 | Infrastructure/Channel | ChannelRouter/ | -141 | +3 partials | 21st god-class |
| **W26** | **App/Services/Scripting** | **CanApi/** | **-210 (target)** | **+3 partials** | **22nd god-class** |

W26 cross-layer balance: 8 App/Services refactors total when shipped (W14+W22+W23+W26 = +sister sister of W24 DbcSendViewModel which is App/ViewModels not App/Services).

## Files to touch

- NEW: `src/PeakCan.Host.App/Services/Scripting/CanApi/SinkLifecycle.partial.cs` (~80 LoC)
- NEW: `src/PeakCan.Host.App/Services/Scripting/CanApi/CallbackRegistry.partial.cs` (~80 LoC)
- NEW: `src/PeakCan.Host.App/Services/Scripting/CanApi/SendAndQuery.partial.cs` (~50 LoC)
- MODIFY: `src/PeakCan.Host.App/Services/Scripting/CanApi.cs` (347 → ~137 LoC)
- MODIFY: `src/Directory.Build.props` (v3.39.5 → v3.40.0)
- NEW: `docs/release-notes-v3.40.0.md`
- NEW: `scripts/w26_task1_delete_sinklifecycle.py`
- NEW: `scripts/w26_task2_delete_callbackregistry.py`
- NEW: `scripts/w26_task3_delete_sendandquery.py`
- NEW: `docs/superpowers/capture-decisions/2026-07-12-w26-can-api-god-class-ship.md` (post-PR docs commit)

## Next after W26

- **W26.5 vault-only PATCH** — lesson-promotion opportunity for 2 NEW 1/3 candidates (`multi-interface-partial-class-iframesink-and-iscriptcanapi` 1/3 + `add-partial-keyword-to-monolithic-class-before-extraction` W26 3/3 CONFIRMED).
- **W27** — next god-class refactor candidate. Top remaining (>250 LoC) main files after W26: `RecentSessionsService.cs` 334 LoC (App/Services sister of W22+W23) OR `AppHostBuilder.cs` 316 LoC (App/Composition DI引导) OR `DbcService.cs` 312 LoC (App/Services sister).
