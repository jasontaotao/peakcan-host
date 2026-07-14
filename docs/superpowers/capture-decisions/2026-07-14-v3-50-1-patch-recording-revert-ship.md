# v3.50.1 PATCH SHIP — Recording revert capture-decisions

**Branch**: `feature/v3-50-1-patch-recording-revert`
**Parent**: v3.50.0 MINOR (`7f8035d` on `main`)
**Ship commit**: TBD on `main` (squash-merged via PR)
**Tag**: `v3.50.1` annotated at squash commit (push to origin pending user auth)
**Branch**: auto-deleted by `gh pr merge --squash --delete-branch`
**Reverts**: v3.49.0 MINOR Q2 (`7ab48da`)

## D1-D7

- **D1**: 0 NEW partials (this PATCH is a revert, deletes 1 partial). v3.49 had 9 partials on TraceViewerViewModel; v3.50 had 11; v3.50.1 has 10.
- **D2**: No `partial` modifier edits needed.
- **D3**: TraceViewerViewModel ctor signature change (removed `RecordViewModel? recordingViewModel` param); AppShellViewModel ctor signature change (added `RecordViewModel recordViewModel` param).
- **D4**: All `[LoggerMessage]` partials stay on existing files.
- **D5**: Largest method in v3.50.1 scope: `ShowRecord` 13 LoC — well below 60 LoC threshold.
- **D6**: Branch name `feature/v3-50-1-patch-recording-revert` ✓.
- **D7**: Order T1 → T2 → T3 → T4 (T0 was SPEC at branch start). T1-T3 all source-code revert (mirror of v3.49 Q2 commit `7ab48da`).

## Source commits (will squash-collapsed into 1 PR commit)

1. `53479a2` — T0 — SPEC committed
2. `cd4baf0` — T1+T2+T3 — Recording revert (10 files, +82/-65 LoC net + 1 file deleted)
3. TBD — T4 — version bump + release notes
4. TBD — T5 — squash-merge via PR + tag v3.50.1

## LoC trajectory (W8.5 D7 32-locked)

| Transition | Before | After | Delta | Notes |
|---|---|---|---|---|
| TraceViewerView.xaml Expander + xmlns removal | (pre-v3.50.1) | -12 LoC | -12 | mirror of v3.49 Q2 +12 |
| Recording.partial.cs delete | -19 LoC | 0 | -19 | file deletion, 10 partials → 9 |
| TraceViewerViewModel.cs ctor param removal | (pre-v3.50.1) | -14 LoC | -14 | mirror of v3.49 Q2 +14 |
| AppShell.xaml Record menu re-add | (pre-v3.50.1) | +6 LoC | +6 | mirror of v3.49 Q2 -1 (with comment) |
| AppShellViewModel.cs ctor + field re-add | (pre-v3.50.1) | +9 LoC | +9 | mirror of v3.49 Q2 -4 (with comment) |
| ViewSwitchFlow.cs ShowRecord re-add | (pre-v3.50.1) | +15 LoC | +15 | mirror of v3.49 Q2 -13 (with comment) |
| AppHostBuilder.cs RecordViewModel wire re-add | (pre-v3.50.1) | +4 LoC | +4 | mirror of v3.49 Q2 -1 |
| AppShellViewModelTests RecordViewModel args re-add + ShowRecordCommand test | (pre-v3.50.1) | +24 LoC | +24 | mirror of v3.49 Q2 -23 |
| AppShellViewModelMessageBoxPromptTests RecordViewModel arg re-add | (pre-v3.50.1) | +3 LoC | +3 | mirror of v3.49 Q2 -1 |
| UdsWindowTests RecordViewModel arg re-add | (pre-v3.50.1) | +3 LoC | +3 | mirror of v3.49 Q2 -1 |

**Net LoC across all changes: +19 LoC** (mostly test infrastructure restore + comments).

## W19 R1 LESSON ENHANCED — 0 applications (N/A — no extraction tasks in revert PATCH)

The revert is mirror-image re-application of original code from v3.49 Q2 commit `7ab48da`. No deletions use W19 R1 LESSON ENHANCED recovery procedure. Sister of v3.17-W22 PATCHes that are pure-revert (no W19 R1 boundary verification needed because we're re-pasting the original code).

## W23 STRUCT-FABRACTION LESSON — N/A (no struct constructor refactor)

This PATCH only adds/removes method-level code; no new struct constructor signatures introduced. Sister of v3.50 T1+ v3.49 T2+ most revert PATCHes.

## W17 wc-l-splitlines CONFIRMED 51-locked

All modified files ASCII-only — safe UTF-8. Confirmed via `wc -l` (each file shows LoC consistent with character count).

## Cross-partial helper visibility pattern — N/A (Recording.partial.cs deleted)

Pattern unchanged for the remaining 10 partials (sister of v3.50 11-partial confirmation). Recording partial removal does NOT affect cross-partial visibility.

## Lesson candidate observations

| Lesson | Status post-v3.50.1 | Notes |
|---|---|---|
| `recording-and-playback-must-not-share-window` | **NEW 1/3** | 1st observation: Trace Viewer (offline .asc playback) and Recording (live bus capture) have different data sources + lifecycle + DI consumers; sharing a window conflates the two concerns. Revert to separate AppShell menu + Trace Viewer window |
| `recording-controls-moved-within-trace-viewer` (v3.49 NEW 1/3) | **DEFERRED** | v3.49 Q2 design reversed in v3.50.1; lesson archived as "v3.49 ship design superseded by v3.50.1 revert" |
| `green-line-anchor-driven-watch-sync` (v3.50 NEW 1/3) | unchanged | v3.50 untouched by v3.50.1 |
| `plotview-drag-handler-requires-transparent-background` (v3.50 NEW 1/3) | unchanged | v3.50 untouched |
| `mvvm-source-gen-xaml-temp-csproj-cant-pull-core-types` (v3.50 NEW 1/3) | unchanged | v3.50 untouched |
| `reset-must-clear-all-mutable-vm-state-for-singleton-vm-reuse` (v3.50 NEW 1/3) | unchanged | v3.50 untouched |

## What was captured (per CLAUDE.md 节流规则)

**SHIP cycle cap = 2 ops**:
1. ✅ Capture-decisions (this file) → commit pending T5 squash
2. ⏸ GH release publish (deferred — auto-mode classifier pending explicit user authorization, per sister pattern from v3.49 / v3.50 ship)

**NOT dispatched (per 节流规则, 3 ops skipped)**:
- pkm-capture after T1+T2+T3 commit (despite hook auto-prompts)
- pkm-capture after T4 version bump
- vault-write for the 1 NEW 1/3 lesson observation

**Estimated token savings from 节流 rule**:
- 3 skipped pkm-capture subagents × ~100k tokens each (per CLAUDE.md 节流 rule rationale) = **~300k tokens saved**

## What was skipped (YAGNI)

- Recording 控件本身的任何 UI 改动 (默认值, 默认文件名, 录制时长等)
- RecordService / RecordViewModel / RecordView 代码 (只重新绑位置)
- Trace Viewer 窗口任何改动 (绿线锚点, watch list, chart subplots, Reset bug fix)
- RecordingService 接口或 DI lifetime 调整

## Process lessons applied

- **Lesson #10** (verify each commit before proceeding): each v3.50.1 T1-T3 verified via build + filter-test before next task.
- **CLAUDE.md "PKM capture 节流"**: 0 vault-pkm dispatches during T0-T4; only THIS capture-decisions file written. **~300k tokens saved** per the rule's stated rationale.

## CI status

- Local `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings
- Local `dotnet test PeakCan.Host.slnx`:
  - Core.Tests: **456/0/0** (1 transient flaky on AscParserTests.Parse_MalformedLines_LogsEachWithLineNumberAndReason — pre-existing in main, sister of W23.5-W25.5+W27.5+W28+W34+W35 PATCH pattern; 5x isolated runs all PASS)
  - App.Tests: **807/3 SKIP/0 fail** (806 v3.50.0 + 1 restored ShowRecordCommand_Is_Not_Null_And_Can_Execute test)
  - Infrastructure.Tests: **89/2 SKIP/0** (unchanged, hardware-dependent)
- CI status: pending T5 squash-merge run

## Cumulative trajectory (peakcan-host v3 series)

After this PATCH:
- **34 cycles** total = 31 god-class refactors (W3-W34) + v3.49.0 + v3.50.0 + v3.50.1
- 9 vault-only PATCH cycles (W17 + W23.5-W25.5 + W26.5-W32.5) — v3.50.5 vault-only PATCH deferred to post-ship
- **+19 LoC** net in v3.50.1 (10 files + 1 file deleted; mostly test infrastructure restore + comments)
- All god-class refactor W-sister patterns continue to apply: W23 STRUCT-FABRACTION, W19 R1 LESSON ENHANCED, W20 fabrication LESSON, W17 wc-l-splitlines
- **NEW lesson cluster emerging**: v3.50 + v3.50.1 together yield 5 NEW 1/3 lessons (3 WPF/Mvvm integration + 1 anchor pattern + 1 playback/recording separation)

## Next (post-v3.50.1 ship)

- **v3.50.2 PATCH-B**: 绿线 show/hide 按钮 + 蓝色差值线 + watch list Delta 列 (独立 cycle, 不绑 Recording revert)
- **v3.50.5 vault-only PATCH**: consolidate 5 NEW 1/3 lessons from v3.50 + v3.50.1
- **W36 — next god-class refactor** (top remaining >200 LoC main files: StatsViewModel 263 / DbcTokenizer 239 / DbcSendViewModel 238 / AscLocator 225 / RecordService 215)
- **v3.51 解决 Play/Pause/Reset 历史未解 cursor propagation 问题** (独立 cycle)
- **GH release publish for v3.50.1** (deferred — pending explicit user authorization per auto-mode classifier)
- **MEMORY.md anchor update**: add v3.50.0 + v3.50.1 ship log entries to peakcan-host Project MEMORY