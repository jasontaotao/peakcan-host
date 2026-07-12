# Release Notes v3.32.0 — PeakCanChannel god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.32.0
**Branch:** `feature/w18-peak-can-channel-god-class`
**Parent:** v3.31.1 PATCH (`54bb3e9` on origin/main)

## Why this MINOR

`src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs` had grown to **389 LoC** as of v3.31.1 — at 48.6% of the 800 LoC Round-1 ceiling. Single `public sealed partial class PeakCanChannel : ICanChannel` wrapping PEAK PCANBasic unmanaged DLL interop with **16 methods** (`ConnectAsync` + `DisconnectAsync` + `WriteAsync` + `DisposeAsync` + `ReadLoopAsync` + `EmitClassic` + `EmitFd` + `SafeEmitReadLoopError` + `MakeError` + `ResolveClassicCode` + 5 `[LoggerMessage]` partials) + 2 events + 2 properties + 4 readonly fields + `_gate` mutable field + 5-strict LoggerMessage partials.

This is the **14th god-class refactor** in the project (W3-W18 series). **1st Infrastructure layer** god-class (all prior 13 W3-W17 were App or Core layer). Specifically `src/PeakCan.Host.Infrastructure/Peak/` — raw PEAK PCANBasic P/Invoke wrapper. **2nd native-binding partial split** (sister of W14 ScriptEngine V8 binding). Validates the partial-class split pattern works for: Infrastructure-layer instance class implementing `ICanChannel` + native-binding lifecycle + `[LoggerMessage]` source-generator scope + per-subscriber try/catch isolation + Timer-driven read loop + bus-dead heuristic.

## LoC trajectory (W8.5 D7 CONFIRMED formula — now 17-locked)

Per task, `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`. Both transitions **EXACT match** (within W13 T1 2/3 loose-assertion ±1 LoC tolerance).

| Task | Flow | Range deleted | LoC out | Main after |
|---|---|---|---|---|
| T1 | ReadLoopFlow (ReadLoopAsync + SafeEmitReadLoopError) | 229-304, 317-333 | 93 | 297 |
| T2 | NativeBindings (EmitClassic + EmitFd + MakeError + ResolveClassicCode) | 243-296 | 54 | 245 |
| **Total** | -- | -- | **147** | **245** |

**Net**: 389 → 245 LoC main file (**-144 LoC, -37.0%**). Total project LoC unchanged (~389 across main + 2 partials).

## What this MINOR does

### Refactor — PeakCanChannel adds 2 NEW partial-class files

The class was already `public sealed partial class PeakCanChannel : ICanChannel` at line 65 (modifier pre-existed for future split, 7th confirmation of `outer-modifier-pre-applied` lesson cluster). The main file keeps: 2 readonly constants (backoff schedule array + max-failures int) + 4 readonly fields (`_handle` + `_gate` + `_logger` + `_reader`) + 2 properties (`Id` + `IsConnected`) + 2 events (`FrameReceived` + `ReadLoopError`) + 1 ctor + 4 lifecycle methods (`ConnectAsync` + `DisconnectAsync` + `WriteAsync` + `DisposeAsync`) + **3 `[LoggerMessage]` partials** (`LogReadLoopException` + `LogReadLoopGivingUp` + `LogReadLoopSubscriberThrew` — accessibility modifier `private` dropped per W18 T1 R1 mitigation).

**Files created**:

| File | Flow | LoC | Methods |
|---|---|---|---|
| `PeakCanChannel/ReadLoopFlow.cs` | A — ReadLoopFlow | ~95 | `ReadLoopAsync` (75 LoC, single-largest) + `SafeEmitReadLoopError` (~16 LoC, per-subscriber isolation pattern) |
| `PeakCanChannel/NativeBindings.cs` | B — NativeBindings | ~55 | `EmitClassic` + `EmitFd` + `MakeError` + `ResolveClassicCode` (4 PEAK SDK interop helpers) |

### Verification

- `dotnet build src/PeakCan.Host.Infrastructure/`: **0 errors, 0 warnings** (after W18 T1 R1 fix + W18 T2 R1 fix — both `using` directive additions)
- `dotnet test --filter "~Channel"`: **5 / 5 PASS** first try
- `dotnet test` (full solution, re-run after 1 transient flaky fail): **1339 PASS, 5 SKIP, 0 FAIL** — 89 Infrastructure + 801 App + 449 Core

## Lessons applied

- **W8.5 D7 CONFIRMED** LoC correction formula (17-locked across W12-W18) — both transitions EXACT.
- **W10 D5 + W11 D5 + W12 D5 + W13 D5 + W14 D5 + W15 D5 + W17** sister: 5 `[LoggerMessage]` partial declarations (originally 3) stay in main — but **W18 T1 R1 fix dropped `private` accessibility modifier** from 2 of them (LogReadLoopException + LogReadLoopSubscriberThrew) to allow cross-partial source-generator implementation. The 3rd `LogReadLoopGivingUp` retained `private` because it's not called from a different partial file.
- **W14 D2** lifecycle-cluster kept together in Flow A (ReadLoopAsync + SafeEmitReadLoopError share `FrameReceived`/`ReadLoopError` event-emission semantics).
- **W14 R5** sister: NativeBindings isolates PEAK SDK interop (`TPCAN*` types) to one partial — sister of W14's CreateEngineFlow V8 isolation pattern.
- **W13 T1 2/3 loose-assertion** pattern applied at all script-assertion sites.
- **W17 wc-l-splitlines CONFIRMED** lesson applied at all deletion scripts.
- **W18 T1 R1 new mitigation** = `peak-can-channel-cs8795-accessibility-modifier-blocks-cross-partial-source-generator-impl` (1/3 candidate observed).

## New sister-lesson candidates (per W18 R1 mitigation)

- **`peak-can-channel-cs8795-accessibility-modifier-blocks-cross-partial-source-generator-impl`** (1/3) — W18 T1 observation: when a `[LoggerMessage]` partial method declaration is called from a different partial file (cross-partial boundary), the explicit `private` accessibility modifier prevents the source generator from emitting the implementation into the consuming file's generator output. Mitigation: drop `private` modifier (default implicit-private). W18 D7 captured this R1 mitigation. Awaits 1 more observation (W19+) for promotion.

## What stays the same

- Public API surface — `ICanChannel` implementation unchanged. Same public methods/properties/events callable.
- Test count unchanged (5 Channel tests pre + post; 1339 full solution 0 fails on re-run).
- PEAK SDK native binding lifecycle preserved verbatim (`_reader.Dispose()` + handle cleanup in `finally` blocks).
- DI registration unchanged (ChannelRouter still injects `IPcanReader` for tests).

## Next steps (post-ship)

- **W18.5 vault-only PATCH** — lesson-promotion opportunity (W18 R1 mitigation 1/3 needs 2 more observations).
- **W19** — next god-class refactor candidate. Top 6 candidates (`>~375 LoC` main files) per the W18 candidate scan: `TraceViewModel.cs` 384 LoC + `DbcSendViewModel.cs` 384 + `CyclicDbcSendService.cs` 383 + `SignalChartViewModel.cs` 378 + `RecordService.cs` 375 (all App layer). ChannelRouter.cs 305 (Infrastructure/Channel sister of W18) is also a candidate.
