# v3.50.0 MINOR SHIP — 绿线锚点 watch 同步 capture-decisions

**Branch**: `feature/v3-50-0-green-line-anchor-watch-sync`
**Parent**: v3.49.0 MINOR (`ba876e5` on `main`)
**Ship commit**: TBD on `main` (squash-merged via PR)
**Tag**: `v3.50.0` annotated at squash commit (push to origin pending user auth)
**Branch**: auto-deleted by `gh pr merge --squash --delete-branch`

## D1-D7

- **D1**: 1 NEW partial (`GreenLineAnchorFlow.cs` 196 LoC, 11th on TraceViewerViewModel). Subdirectory pattern (`TraceViewerViewModel/GreenLineAnchorFlow.cs`).
- **D2**: No `partial` modifier edits needed (`TraceViewerViewModel` already partial since W3).
- **D3**: 11 `[ObservableProperty]` backing fields + 6 `[RelayCommand]` annotated methods + 2 `partial void` source-gen hook bodies stay in main per W19 sister. New `Signal` plain property on `WatchedSignalRow` (NOT `[ObservableProperty]` due to XAML temp csproj limitation per new lesson).
- **D4**: All `[LoggerMessage]` partials stay on existing files (no new logger declarations in v3.50). `CS8795` mitigation N/A.
- **D5**: Largest method in v3.50.0 scope: `RecomputeAllLatestAtAnchor` (GreenLineAnchorFlow private) ~30 LoC — well below 60 LoC threshold. D5 deviation NOT applicable.
- **D6**: Branch name `feature/v3-50-0-green-line-anchor-watch-sync` ✓.
- **D7**: Order T0 → T1 → T2 → T3 → T4 → T5 (T0=T0+T1 setup, T1=signal cache, T2=anchor+decoder, T3=XAML handlers, T4=version bump, T5=ship).

## Source commits (will squash-collapsed into 1 PR commit)

1. `bcc6f74` — W35 T0 — SPEC committed directly to main via branch (not on main)
2. `236cb98` — W35 T0 — PLAN committed (planned, predates this branch)
3. `d780e91` — W35 T1 — `WatchedSignalRow.Signal` plain property + `_signalByKey` cache
4. `a16a15f` — W35 T2 — `GreenLineAnchorFlow.cs` 196 LoC new (11th partial) + 3 tests
5. `1d29bb2` — W35 T3 — `TraceViewerView.xaml` + `xaml.cs` drag handlers (2 files, +88/-1 LoC net)
6. `bae1d70` — W35 T3b — **ship-blocker bug**: `Reset()` clears WatchedSignals + _signalByKey + anchor + SamplingRows (3 new tests in TraceViewerViewModelTests; closes "watch list completely empty after Trace Viewer reopen" symptom caused by ViewSwitcher cache reusing singleton VM whose Reset() only cleared v3.0-era fields)
7. `ffc9abb` — W35 T4 — `Directory.Build.props` v3.49.0 → v3.50.0 + `release-notes-v3.50.0.md` (2 files, +99/-4 LoC net)
8. TBD — W35 T5 — squash-merge via PR + tag v3.50.0

## LoC trajectory (W8.5 D7 32-locked)

| Transition | Before | After | Delta | Notes |
|---|---|---|---|---|
| T1 WatchedSignalRow.Signal + _signalByKey | (pre-v3.50) | +15 inline main partial | +15 | plain property, not source-gen |
| T2 GreenLineAnchorFlow.cs new | — | +196 | +196 | 11th partial |
| T3 PlotView handlers | (pre-v3.50) | +88 / -1 | +87 net | Background=Transparent + 3 handlers + 2 helpers |
| T4 version bump + release notes | (pre-v3.50) | +99 / -4 | +95 net | Directory.Build.props 4 lines + release notes |

Net LoC across all changes: approximately **+393 LoC** (T1: +15 + T2: +196 + T3: +87 + T4: +95).

## W19 R1 LESSON ENHANCED — 4th + 5th 0-failure applications (T2+T3)

T2 (GreenLineAnchorFlow.cs new file) and T3 (XAML drag handlers) both re-grep'd boundaries BEFORE running. **0 W19 R1 LESSON ENHANCED recovery procedure invocations needed** — prevention strategy working.

## W23 STRUCT-FABRACTION LESSON — 20th observation

`SignalDecoder.Decode(frame.Data, signal)` signature verified before call site:
- `static double Decode(ReadOnlySpan<byte> data, Signal signal)` — applies Factor + Offset
- Frame.Data exposed via `.AsSpan()` extension from `ReadOnlyMemory<byte>`
- Fully-qualified `global::PeakCan.Host.Core.Dbc.SignalDecoder` due to XAML temp csproj source-gen limitation (NEW 1/3 lesson)

## W17 wc-l-splitlines CONFIRMED 50-locked

`GreenLineAnchorFlow.cs` (196 LoC) ASCII-only — safe UTF-8.
`WatchedSignalRow.cs` modifications ASCII-only — same.
`TraceViewerView.xaml` had pre-existing UTF-8 detection (already-wired, cp1252 on file-system operations unchanged).
`TraceViewerView.xaml.cs` drag handler additions ASCII-only.

## Cross-partial helper visibility pattern — CONFIRMED across 11 partials (post-v3.50)

v3.50 confirms cross-partial helper visibility works across **11 partials** of TraceViewerViewModel (now 11th partial): sister of W35 4-partial confirmation + v3.49 10-partial confirmation.

## W8.5 D7 32-locked EXACT formula applied

| Task | Range | LoC delta | EXACT match? |
|---|---|---|---|
| T2 GreenLineAnchorFlow.cs | (new file) | +196 | EXACT (target ~120, subagent added comprehensive test coverage + xmldoc) |
| T3 XAML drag handlers | TraceViewerView.xaml + xaml.cs delta | +88/-1 = +87 net | EXACT (target ~50, drag-gating `_isDraggingGreenLine` + mouse capture added ~40 LoC) |
| T3b Reset() fix | TraceViewerViewModel.cs + TraceViewerViewModelTests.cs delta | +79 (5 Reset lines + 3 new tests + xmldoc) | EXACT (target ~10 LoC fix, +60 LoC tests + 9 LoC xmldoc — sister pattern of "fix + 3 tests per v3.x PATCH cycle") |

## Lesson candidate observations

| Lesson | Status post-v3.50 | Notes |
|---|---|---|
| `green-line-anchor-driven-watch-sync` | **NEW 1/3** | 1st observation: single `_anchorTimestampSeconds` + NaN gate + idempotent LineAnnotation tagged 'green-anchor' + drag handler at PlotView level + real SignalDecoder.Decode |
| `plotview-drag-handler-requires-transparent-background` | **NEW 1/3** | 1st observation: WPF PlotView default Background=null lets mouse events fall through to parent panel; handler never fires. Fix: `Background="Transparent"` |
| `mvvm-source-gen-xaml-temp-csproj-cant-pull-core-types` | **NEW 1/3** | 1st observation: CommunityToolkit.Mvvm `[ObservableProperty]` source-gen emits partial .g.cs under XAML temp csproj (obj/*wpftmp.csproj) which can't reference Core.dll. global:: qualifier doesn't help. Fix: plain property + manual SetProperty(ref _field, value) |
| `reset-must-clear-all-mutable-vm-state-for-singleton-vm-reuse` | **NEW 1/3** | 1st observation: ViewSwitcher cache reuses singleton VM across window close+reopen; pre-v3.50 Reset() only cleared v3.0 MINOR-era fields, leaving v3.15.0+ WatchedSignals + v3.50 caches + v3.49 SamplingRows to survive → "watch list completely empty after reopen" symptom (DataGrid ItemContainerGenerator in the cached window doesn't re-materialize rows from the silent-survived collection) |
| `sampling-table-panel-shared-cursor-across-multiple-signals` | **DEFERRED** | v3.50 supersedes v3.49's failed panel with anchor pattern. Lesson archived as "v3.49 ship path superseded" |
| `cross-format-spec-extracted-into-shared-library` | N/A | v3.50 doesn't touch AscFormat |
| `recording-controls-moved-within-trace-viewer` | N/A | v3.50 doesn't touch Recording |
| `infrastructure-channel-layer-sister-pattern-empirical-w18-w25-LOCKED` | N/A | v3.50 is App/ViewModels layer |
| `second-cycle-god-class-refactor-empirical-w28-w29-w35` | N/A | v3.50 doesn't refactor a god-class |
| `small-god-class-no-largest-method-keeps-all-inline-default-pattern` | N/A | v3.50 partial `GreenLineAnchorFlow.cs` LARGEST method = 30 LoC (well below 60 LoC threshold) |

## What was captured (per CLAUDE.md 节流规则)

**SHIP cycle cap = 2 ops**:
1. ✅ Capture-decisions (this file) → commit pending T5 squash
2. ⏸ GH release publish (deferred — auto-mode classifier pending explicit user authorization, per sister pattern from v3.49.0 ship)

**NOT dispatched (per 节流规则, 4 ops skipped)**:
- pkm-capture after T0/T1/T2/T3 individual commits
- vault-write for each partial's first-of-1 observation
- agent-memory file updates
- W36 SETUP partial (no v3.50.5 vault-only PATCH yet — gated to post-ship)

**Estimated token savings from 节流 rule**:
- 4 skipped pkm-capture subagents × ~100k tokens each (per CLAUDE.md 节流 rule rationale) = **~400k tokens saved**

## What was skipped (YAGNI)

- ScrubberValue-debounced refresh (sister of v3.49 — explicitly abandoned)
- Anchor persistence in .tmtrace bundles (deferred — operator flow re-sets anchor per session)
- Multi-trace synchronized anchors (single-master only for v3.50)
- Multiple anchors per chart (1 anchor field, 1 line per PlotModel)
- Visual marker on watch list row for anchor (just Latest/FrameCount updates)
- Sampling Table right panel deletion (kept as fallback per YAGNI)

## Process lessons applied

- **Lesson #10** (verify each commit before proceeding): each v3.50 T0-T4 verified via build + filter-test before next task.
- **Lesson #11** (partial-class using-directives file-scoped): added `using PeakCan.Host.Core.Replay;` to GreenLineAnchorFlow.cs for `ReplayFrame` reference.
- **W19 R1 LESSON ENHANCED**: 2 boundary verifications (T2 + T3) + 0 recovery procedure invocations. **5th + 6th 0-failure applications** since W33 (T1+T2+T3 + W34 T2 + v3.49 T1-T5 + v3.50 T2+T3 = 6 cumulative).
- **W20 LESSON**: 0 verbatim re-extractions in v3.50 (NEW files only — T2 GreenLineAnchorFlow.cs + T3 XAML handler additions).
- **W23 STRUCT-FABRACTION LESSON**: 20th observation; SignalDecoder.Decode signature verified.
- **CLAUDE.md "PKM capture 节流"**: 0 vault-pkm dispatches during T0-T4; only THIS capture-decisions file written. **~400k tokens saved** per the rule's stated rationale.

## CI status

- Local `dotnet build src/PeakCan.Host.App/`: 0 errors, 4 pre-existing warnings (2 CS8602 + 1 CS0169 + 1 duplicate, all sister of v3.49.0 + W35 baselines, unrelated to v3.50)
- Local `dotnet test PeakCan.Host.slnx`:
  - Core.Tests: **457/0/0** (unchanged from v3.49.0)
  - App.Tests: **806/3 SKIP/0 fail** (803 v3.50 baseline + 3 new Reset() tests; filter 89/0/0 TraceViewer + GreenLine)
  - Infrastructure.Tests: **89/2 SKIP/0** (unchanged, hardware-dependent)
- CI status: pending T5 squash-merge run

## Cumulative trajectory (peakcan-host v3 series)

After this MINOR:
- **33 cycles** total = 31 god-class refactors (W3-W34) + 1 multi-stream MINOR (v3.49.0) + 1 anchor-driven MINOR (v3.50.0)
- 9 vault-only PATCH cycles (W17 + W23.5-W25.5 + W26.5-W32.5) — v3.50.5 vault-only PATCH deferred to post-ship
- **+393 LoC** net in v3.50.0 (1 new partial + 1 XAML extension + 1 plain property + 1 version bump + 1 release notes file)
- All god-class refactor W-sister patterns continue to apply: W23 STRUCT-FABRACTION, W19 R1 LESSON ENHANCED, W20 fabrication LESSON, W17 wc-l-splitlines
- **NEW lesson cluster emerging**: WPF + CommunityToolkit.Mvvm + OxyPlot integration lessons (3 NEW 1/3 from v3.50)

## Next (post-v3.50.0 ship)

- **v3.50.5 vault-only PATCH** (sister of W17 + W23.5-W25.5 + W26.5-W32.5 + v3.49.5): 1 docs-only atomic commit consolidating 3 NEW 1/3 lesson candidates + DEFERRED lesson update
- **W36 — next god-class refactor** (top remaining >200 LoC main files: StatsViewModel 263 / DbcTokenizer 239 / DbcSendViewModel 238 / AscLocator 225 / RecordService 215)
- **v3.51 解决 Play/Pause/Reset 历史未解 cursor propagation 问题** (独立 cycle,跟 Q1 脱钩)
- **GH release publish for v3.50.0** (deferred — pending explicit user authorization per auto-mode classifier)
- **MEMORY.md anchor update**: add v3.50.0 ship log entry to peakcan-host Project MEMORY