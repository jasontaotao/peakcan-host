# Release Notes v3.44.0 — SequenceSendService god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.44.0
**Branch**: `feature/w30-sequence-send-service-god-class`
**Parent**: v3.43.5 PATCH (`44e0323` on main + W29.5 vault-only PATCH)

## Why this MINOR

`src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs` had grown to **266 LoC** as of v3.43.5 — at 33.3% of the 800 LoC Round-1 ceiling. Single `public sealed class SequenceSendService` (NOT partial — `partial` modifier added at L31 in T0-D2 per W26.5 3/3 CONFIRMED + W21 3/3 CONFIRMED sister). 3 readonly fields + 2 ctors (parameterless delegating + full) + 1 nested enum `Mode` + 1 nested record `Result` + 1 public async method `SendAsync` (**91 LoC LARGEST**) + 2 private helpers (`TryBuildRow` 76 LoC + `SendOneAsync` 16 LoC).

This is the **26th god-class refactor** in the project (W3-W30 series). **6th App/Services layer** (sister of W22 RecordService + W23 CyclicDbcSendService + W27 RecentSessionsService + W28 DbcService + W29 SendFrameLibrary). **1st App/Services/MultiFrame subdirectory**. **20th subdirectory-pattern deployment**.

## LoC trajectory (W8.5 D7 CONFIRMED formula — 32-locked)

Per task, `LoC_n = LoC_{n-1} - sum(deleted at task n)`. Both transitions **EXACT match**.

| Task | Flow | Range deleted | LoC out | Main after |
|---|---|---|---|---|
| T0-D2 | partial-keyword add (L31) | -- | 0 | 266 |
| T1 | SendFlow (SendAsync 91 LoC LARGEST method moves per W25 D5 deviation) | 75-165 (HEAD) | 91 | 175 |
| T2 | RowBuildFlow (TryBuildRow + SendOneAsync) | 83-175 (post-T1) | 93 | 82 |
| **Total** | -- | -- | **184** | **82** |

**Net**: 266 → 82 LoC main file (**-184 LoC, -69.2%**). Total project LoC across main + 2 partials ≈ 240 LoC (small +56 LoC overhead from per-file namespace + using directives + 2 xmldoc header comment blocks).

## What this MINOR does

### Refactor — SequenceSendService adds 2 NEW partials in `SequenceSendService/` subdirectory

1. **NEW `src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService/SendFlow.partial.cs` (~157 LoC)**:
   - Contains `public async Task<Result> SendAsync(IReadOnlyList<MultiFrameSequenceRow>, Mode, int, int, IProgress<int>?, CancellationToken)` method body + xmldoc verbatim from main HEAD L75-L165.
   - **W25 D5 deviation APPLIED**: 91 LoC LARGEST method MOVES per the sharp discrete flow boundary criterion (concurrent Task.WhenAll fan-out vs sequential Task.Delay loop = 2 distinct dispatching paths, NOT single central orchestration loop).
   - Sister of W25 OnChannelFrame 73 LoC + W26 OnFrame 62 LoC + W27 LoadAsync 60 LoC + W28 LoadAsync 79 LoC moves (all ≥ 60 LoC + discrete flow boundary).
   - Verbatim re-extraction via `git show HEAD:src/.../SequenceSendService.cs | sed -n '75,165p'` per W20 T2 R1 fabrication LESSON (35th application).
   - 1 using-directive fix per W19 R1 first-correction (`PeakCan.Host.App.Models` for `MultiFrameSequenceRow` type).

2. **NEW `src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService/RowBuildFlow.partial.cs` (~157 LoC)**:
   - Contains 2 private helpers verbatim from main HEAD L174-L266: `private bool TryBuildRow(MultiFrameSequenceRow, out CanFrame, out string?)` (76 LoC) + `private async Task<bool> SendOneAsync(CanFrame, CancellationToken)` (16 LoC).
   - **W23 STRUCT-FABRICATION LESSON APPLIED 13th time**: `CanId(raw, FrameFormat format)` 2-arg + `CanFrame(canId, payload, FrameFlags.None, ChannelId.None, default)` 5-arg signatures verified during verbatim re-extraction.
   - Verbatim re-extraction via `git show main:src/.../SequenceSendService.cs | sed -n '174,266p'` per W20 LESSON (36th application).
   - 1 using-directive fix per W19 (`PeakCan.Host.App.Models` for `MultiFrameSequenceRow` type).

### D1-D7 sister-pattern decisions (carried from W30 SPEC)

- **D1**: 2 NEW partials (`SendFlow` + `RowBuildFlow`) in `SequenceSendService/` subdirectory. **20th subdirectory-pattern deployment**.
- **D2**: **Add `partial` modifier** at L31 (`public sealed class SequenceSendService` → `public sealed partial class SequenceSendService`) per W26.5 3/3 CONFIRMED + W21 3/3 CONFIRMED sister.
- **D3**: 3 readonly fields (`_sendService` + `_dbcEncodeService?` + `_dbcService?`) + 2 ctors (parameterless delegating + full) + 1 nested enum `Mode` + 1 nested record `Result` + class xmldoc stay in main.
- **D4**: N/A — zero `[LoggerMessage]` partials (verified). No CS8795 risk.
- **D5**: **APPLIES** — `SendAsync` 91 LoC LARGEST method MOVES to SendFlow.partial.cs per W25 D5 deviation (sister of W25 + W26 + W27 + W28 moves). **7th observation of `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator`** (6/3 LOCKED at W28.5 → 7/3 at W30).
- **D6**: Branch name `feature/w30-sequence-send-service-god-class`.
- **D7**: Order largest-first per W12+W14+W18+W22+W23+W24+W25+W26+W27+W28+W29 D7 sister + W25 D5 deviation: **A (SendFlow, 91 LoC, LARGEST + W25 D5 deviation applied) → B (RowBuildFlow, 93 LoC, helpers cluster)**.

## What this MINOR does NOT change (YAGNI + sister-pattern invariants)

- No public/internal API change.
- No test changes (16 SequenceSendService tests pass without modification).
- No facade pattern (W3-W29 CONFIRMED direct partial-class visibility).
- No xmldoc-grep risk (verified — tests do not path-grep main file content).
- No CS8795 risk (D4: zero `[LoggerMessage]` partials in W30).
- No `MultiFrameSequenceRow` or `MultiFrameSequenceRow.Kind` inner-enum changes (stays in `Models` namespace).
- No `SendService.SendAsync` or `DbcEncodeService.Encode` cross-service API changes.
- No `SendViewModel` or `MultiFrameSendViewModel` partial changes (W6 sister precedent unchanged).
- No D5 default sister-principle change (W29 1/3 NEW `small-god-class-no-largest-method-keeps-all-inline-default-pattern` correctly NOT applied here since SendAsync 91 LoC ≥ 60 LoC threshold).

## Architecture milestones

- **26th god-class refactor SHIPPED** (W3-W30 series).
- **6th App/Services layer** (after W22 + W23 + W27 + W28 + W29).
- **1st App/Services/MultiFrame subdirectory** (new layer discovered; W22-W29 sisters were Trace/DBC/JSON-persistence layers).
- **20th subdirectory-pattern deployment**.
- **W20 LESSON APPLIED 35th + 36th times** across W30 T1+T2 (verbatim re-extraction via `git show main:src/.../SequenceSendService.cs | sed -n '<range>p'`).
- **W23 STRUCT-FABRICATION LESSON APPLIED 13th time since 3/3 CONFIRMED at W23 T2** (W30 verified `CanId(raw, FrameFormat format)` 2-arg + `CanFrame(canId, payload, FrameFlags.None, ChannelId.None, default)` 5-arg signatures in RowBuildFlow.partial.cs L137-L139).
- **W25 D5 deviation APPLIED 5th time** (W25 OnChannelFrame + W26 OnFrame + W27 LoadAsync + W28 LoadAsync + **W30 SendAsync** = 5 moves since 3/3 LOCKED at W25).
- **W26.5 + W21 partial-keyword add APPLIED 28th time** (W30 T0-D2 added `partial` modifier to `SequenceSendService` before extraction; sister of W26.5 3/3 CONFIRMED + W21 3/3 CONFIRMED).
- **W19 R1 first-correction APPLIED 35th + 36th times** at W30 T1+T2 (2 using-directive fixes for `PeakCan.Host.App.Models`).
- **W17 wc-l-splitlines CONFIRMED 41-locked** (cp1252 binary read+write).

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings (after W30 T1+T2 using-directive fixes per W19; 2 pre-existing CS8602 warnings from `DbcService/LoadLifecycle.partial.cs:88` are NOT W30-related, retained across cycles).
- `dotnet test --filter "FullyQualifiedName~SequenceSendService"`: **16/16 PASS** (matches pre-W30 baseline).
- `dotnet test` (full solution via trx): 0 new fails (full App.Tests run via local trx).

## Process lessons applied (W20 + W23 + W25 + W26.5 + W19)

- **Lesson #10** (verify each commit before proceeding): each W30 T0+T1+T2 build + filter-test verified before commit.
- **Lesson #11** (partial-class using-directives file-scoped, not class-scoped): W30 T1+T2 using-directive fixes (2 fixes: `PeakCan.Host.App.Models` ×2 for `MultiFrameSequenceRow` type).
- **W19 R1 first-correction**: re-grep boundaries before each deletion script (35th + 36th application in W30).
- **W20 T2 R1 fabrication LESSON**: 36 verbatim re-extractions across W30 T1+T2 (35+36th cumulative W20 LESSON applications).
- **W23 STRUCT-FABRICATION LESSON**: W30 verified `CanId` 2-arg + `CanFrame` 5-arg struct-ctor signatures (13th observation since 3/3 CONFIRMED).
- **W25 D5 deviation APPLIED**: W30 SendAsync 91 LoC LARGEST method MOVES (7th observation since 3/3 LOCKED at W25; W30 = 5th move confirming W25 + W26 + W27 + W28 + W30 move pattern).
- **W26.5 + W21 partial-keyword add**: W30 T0-D2 added `partial` modifier to `SequenceSendService` before extraction (28th cumulative application).

## Sister-pattern cumulative trajectory (god-class series, W3-W30)

| W | Layer | Subdirectory | Main LoC | Prior + W30 |
|---|---|---|---|---|
| W22 | App/Services | RecordService/ | -193 | 18th god-class |
| W23 | App/Services | CyclicDbcSendService/ | -288 | 19th god-class |
| W27 | App/Services/Trace | RecentSessionsService/ | -213 | 23rd god-class |
| W28 | App/Services | DbcService/ | -195 | 24th god-class |
| W29 | App/Services | SendFrameLibrary/ | -162 | 25th god-class |
| **W30** | **App/Services/MultiFrame** | **SequenceSendService/** | **-184** | **26th god-class** |

**Cumulative LoC reduction (W3-W30)**: 25 god-class files -4,504 LoC (W3-W29) + **W30 SequenceSendService -184 LoC** = **-4,688 LoC total** across 26 god-class refactors + 6 vault-only PATCHes (W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5).

## What was captured

W30 SHIP closure = 5 captures dispatched: SPEC + PLAN + T1 + T2 + T3 (T4 ship captures via `vault-pkm:pkm-capture` background-dispatched post-T4 squash-merge + tag + GH release). Each per the W12-W29 pattern of `vault-pkm:pkm-capture` agent dispatched after each source commit.

## Next (post-ship)

- **W30.5 vault-only PATCH** — lesson-promotion opportunity for 1 NEW 1/3 + 1 7/3 observation + 1 HELD 6/3 LOCKED candidates:
  - `app-services-multiframe-layer-sister-pattern-empirical-w30` NEW 1/3 (SequenceSendService MultiFrame decomposition = SendFlow + RowBuildFlow; 1st observation of App/Services/MultiFrame sister pattern)
  - `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` 7/3 observation held (W30 SendAsync 91 LoC ≥ 60 LoC + concurrent-vs-sequential discrete flow boundary = MOVES; 5th move in 7 observations, sister of W25+W26+W27+W28)
  - `small-god-class-no-largest-method-keeps-all-inline-default-pattern` 1/3 → 2/3 awaiting (W30 has LARGEST method 91 LoC ≥ 60 LoC → W25 D5 deviation applied correctly, NOT default D5 — confirms W29 1/3 observation that default D5 applies to small god-classes only)
- **W31** — next god-class refactor candidate. Top remaining (>250 LoC) main files after W30: `ReplayService.cs` 265 LoC (Core/Replay) OR `StatsViewModel.cs` 263 LoC (App/ViewModels) OR `DbcSendViewModel.cs` 384 LoC (App/ViewModels — sister of W24, but already partial since W24; likely needs further splitting).
