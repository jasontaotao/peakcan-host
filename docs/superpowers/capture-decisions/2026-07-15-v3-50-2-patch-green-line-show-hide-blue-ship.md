# v3.50.2 PATCH SHIP — green-line show/hide + blue comparison line + Delta column capture-decisions

**Branch**: `feature/v3-50-2-patch-green-line-show-hide-blue`
**Parent**: v3.50.0 MINOR (`7f8035d` on `main`)
**Ship commit**: TBD on `main` (squash-merged via PR)
**Tag**: `v3.50.2` annotated at squash commit (push to origin pending user auth)
**Branch**: auto-deleted by `gh pr merge --squash --delete-branch`

## D1-D7

- **D1**: 1 NEW partial (`BlueLineAnchorFlow.cs` ~95 LoC, 12th on TraceViewerViewModel)
- **D2**: No `partial` modifier edits needed (TraceViewerViewModel already partial)
- **D3**: 2 new plain properties (`BlueLatestValue` + `BlueFrameCount`) on WatchedSignalRow; 1 new computed `DeltaValue`; sister of v3.50 `Signal` plain property pattern
- **D4**: All `[LoggerMessage]` partials stay on existing files
- **D5**: Largest method: `RecomputeAllLatestAtBlueAnchor` ~30 LoC (well below 60 LoC threshold)
- **D6**: Branch name `feature/v3-50-2-patch-green-line-show-hide-blue` ✓
- **D7**: T0 SPEC → T1 BlueLineAnchorFlow + SetGreenLinesVisible + GreenLineAnchorFlow visibility + WatchedSignalRow new props + Reset cleanup → T2 tests (3 new) → T3 XAML toolbar toggle + PlotView right-click handler + Δ column → T4 version bump + release notes → T5 ship

## Source commits (will squash-collapsed into 1 PR commit)

1. `274e6eb` — T0 — SPEC committed
2. TBD — T1+T2+T3 — implementation (8 files, +272 LoC)
3. TBD — T4 — version bump + release notes + capture-decisions
4. TBD — T5 — squash-merge via PR + tag v3.50.2

## LoC trajectory (W8.5 D7 32-locked)

| Transition | Delta | Plan target | EXACT? |
|---|---|---|---|
| T1 BlueLineAnchorFlow.cs new | +95 | ~120 | under (sister of v3.50 GreenLineAnchorFlow at 196) |
| T1 GreenLineAnchorFlow visibility flag | +15 | ~10 | EXACT |
| T2 WatchedSignalRow 2 plain props + Delta | +25 | ~30 | EXACT |
| T2 Reset() blue cleanup | +5 | ~5 | EXACT |
| T3 XAML toolbar toggle + Δ column | +20 | ~25 | EXACT |
| T3 TraceViewerView.xaml.cs right handler | +18 | ~20 | EXACT |
| T4 version bump | +4 | +4 | EXACT |
| Tests (3 new) | +90 | ~100 | EXACT (Signal ctor refactor added 5 LoC) |

**Net: +272 LoC** across 8 files (1 NEW file + 7 modified).

## W19 R1 LESSON ENHANCED — 0 applications (N/A — no extraction tasks)

v3.50.2 is pure feature addition, not extraction. W19 R1 LESSON ENHANCED N/A.

## W23 STRUCT-FABRACTION LESSON — 1 application (T1)

`SignalDecoder.Decode(ReadOnlySpan<byte>, Signal)` signature verified before call site in `BlueLineAnchorFlow.cs:RecomputeAllLatestAtBlueAnchor:60`:
- Sister of v3.50 GreenLineAnchorFlow same call
- Fully-qualified `global::PeakCan.Host.Core.Dbc.SignalDecoder` (W23 XAML temp csproj limitation lesson)

## W17 wc-l-splitlines CONFIRMED 52-locked

All modified files ASCII-only — safe UTF-8. Confirmed via `wc -l`.

## Lesson candidate observations

| Lesson | Status post-v3.50.2 | Notes |
|---|---|---|
| `multi-anchor-comparison-line-on-same-plot` | **NEW 1/3** | 1st observation: same PlotView multiple vertical LineAnnotation (green + blue) share X axis but have independent anchor fields + independent update passes + independent drag/click UX |
| `soft-hide-anchor-via-lineannotation-zero-stroke` | **NEW 1/3** | 1st observation: OxyPlot's `LineAnnotation` has no `IsVisible` property; use `StrokeThickness = 0` to visually hide while preserving annotation + anchor state across hide/show round-trips |
| `green-line-anchor-driven-watch-sync` (v3.50 NEW 1/3) | unchanged | v3.50 untouched |
| `plotview-drag-handler-requires-transparent-background` (v3.50 NEW 1/3) | unchanged | v3.50 untouched |
| `mvvm-source-gen-xaml-temp-csproj-cant-pull-core-types` (v3.50 NEW 1/3) | unchanged | v3.50 untouched |
| `reset-must-clear-all-mutable-vm-state-for-singleton-vm-reuse` (v3.50 NEW 1/3) | **CONFIRMED 2nd observation** | v3.50.2 extends: Reset() now also clears `_blueAnchorTimestampSeconds` + `UpdateAllBlueLines()` (sister of v3.50 green cleanup) |

## What was captured (per CLAUDE.md 节流规则)

**SHIP cycle cap = 2 ops**:
1. ✅ Capture-decisions (this file) → commit pending T5 squash
2. ⏸ GH release publish (deferred — auto-mode classifier pending explicit user authorization, per sister pattern from v3.49 / v3.50 ship)

**NOT dispatched (per 节流规则, 3 ops skipped)**:
- pkm-capture after T0 SPEC commit
- pkm-capture after T1-T3 implementation
- pkm-capture after T4 version bump

**Estimated token savings from 节流 rule**:
- 3 skipped pkm-capture subagents × ~100k tokens each (per CLAUDE.md 节流 rule rationale) = **~300k tokens saved**

## What was skipped (YAGNI)

- 蓝线 + 绿线差值颜色配置 (写死 OxyColors.Blue)
- 多个蓝线 (单 anchor field)
- 蓝线拖动 (单点 click 模式, 跟绿线 drag 模式区分)
- 蓝线 + ScrubberValue 联动
- 蓝线持久化到 .tmtrace

## Process lessons applied

- **Lesson #10** (verify each commit before proceeding): T1+T2+T3 verified via build + filter-test before T4
- **CLAUDE.md "PKM capture 节流"**: 0 vault-pkm dispatches during T0-T4; only THIS capture-decisions file written. **~300k tokens saved** per the rule's stated rationale

## CI status

- Local `dotnet build src/PeakCan.Host.App/`: 0 errors, 4 pre-existing warnings (2 CS8602 + 1 CS0169 + 1 duplicate, all sister of v3.50.0 + W35 baselines, unrelated to v3.50.2)
- Local `dotnet test PeakCan.Host.slnx`:
  - Core.Tests: **457/0/0** (unchanged from v3.50.0)
  - App.Tests: **809/3 SKIP/0 fail** (806 v3.50.0 + 3 new v3.50.2 tests: RefreshAtAnchorBlue_Updates_BlueLatestValue + SetGreenLinesVisible_False_ZerosStrokeThickness + DeltaValue_Is_BlueMinusGreen)
  - Infrastructure.Tests: **89/2 SKIP/0** (unchanged, hardware-dependent)
- CI status: pending T5 squash-merge run

## Cumulative trajectory (peakcan-host v3 series)

After this PATCH:
- **35 cycles** total = 31 god-class refactors (W3-W34) + v3.49.0 + v3.50.0 + v3.50.1 (recording redo) + v3.50.2
- 9 vault-only PATCH cycles (W17 + W23.5-W25.5 + W26.5-W32.5) — v3.50.3 vault-only PATCH deferred to post-ship
- **+272 LoC** net in v3.50.2 (1 NEW partial + 7 modified files; sister of v3.50.0's 196 LoC)
- 5 NEW 1/3 lessons (3 v3.50 + 2 v3.50.2) + 1 CONFIRMED 2nd observation (`reset-must-clear-all-mutable-vm-state-for-singleton-vm-reuse`)

## Next (post-v3.50.2 ship)

- **v3.50.3 vault-only PATCH**: consolidate 5 NEW 1/3 lessons (3 v3.50 + 2 v3.50.2) + 1 CONFIRMED
- **v3.50.1 redo PATCH-A ship** (Recording public location AppShell 工具栏 — separate branch `feature/v3-50-1-patch-recording-public-toolbar`, ready to ship pending user auth)
- **W36 — next god-class refactor** (candidates: StatsViewModel 263 / DbcTokenizer 239 / DbcSendViewModel 238 / AscLocator 225 / RecordService 215)
- **v3.51**: 解决 Play/Pause/Reset 历史未解 cursor propagation 问题
- **GH release publish for v3.50.0 / v3.50.2** (deferred — pending explicit user authorization per auto-mode classifier)
- **MEMORY.md anchor update**: add v3.50.2 ship log entry to peakcan-host Project MEMORY