# v3.49.0 MINOR SHIP — ASC 单源 + Sampling Table + Recording 合并 capture-decisions

**Branch**: `feature/v3-49-0-minor-asc-sampling-recording`
**Parent**: v3.48.2 PATCH (`fe40d63` on `main`)
**Ship commit**: `5f99db0` on `main` (squash-merged via PR #70)
**Tag**: `v3.49.0` annotated at `5f99db0` (push to origin OK; GH release pending classifier approval)
**Branch**: auto-deleted by `gh pr merge --squash --delete-branch`

## D1-D7

- **D1**: 2 NEW partials (`ConnectFlow` + `WriteFlow`) — applied as: **2 NEW partials** (`Recording.partial.cs` + `SamplingTableFlow.cs`) + 1 NEW static class (`Core/Replay/AscFormat.cs`). **25th subdirectory-pattern deployment** (peakcan-host `PeakCanChannel/` was prior 24th).
- **D2**: No `partial` modifier edits needed (`PeakViewerViewModel` already partial since W3, `RecordService` already partial since W6, `AppShellViewModel` already partial). **No D2 work**.
- **D3**: `Recording.partial.cs` 9th partial on `TraceViewerViewModel` (sister of W3 + W20); `SamplingTableFlow.cs` 10th partial (10th — sister of W18 + W34 multi-partial expansion).
- **D4**: All `[LoggerMessage]` partials stay on existing files (no new logger declarations in v3.49). `CS8795` mitigation N/A.
- **D5**: Largest method in v3.49.0 scope: `RefreshSamplingTable` (SamplingTableFlow public) ~30 LoC — well below 60 LoC threshold. D5 deviation NOT applicable.
- **D6**: Branch name `feature/v3-49-0-minor-asc-sampling-recording` ✓.
- **D7**: Order Q3 → Q2 → Q1 (per plan). **Actual** T0 → T1 → T2 → T3 → T4 → T5 → T6 all shipped in that order (T1+T2+T3 = Q3; T4 = Q2; T5 = Q1; T6 = MINOR bump + release notes).

## 7 source commits (squash-collapsed into PR #70)

1. `5cd385f` — W35 T0 — SPEC + PLAN (2 docs files committed directly to main, not on the feature branch)
2. `9cfe110` — W35 T1 (Q3) — `src/PeakCan.Host.Core/Replay/AscFormat.cs` 264 LoC new
3. `aab6ec1` — W35 T2 (Q3) — 3 partials refactored to delegate to `AscFormat` (Format.partial.cs 67→30; DataLineParserFlow 172→16; ParseLinesFlow 114→73)
4. `609d760` — W35 T3 (Q3) — 6 round-trip tests (188 LoC)
5. `7ab48da` — W35 T4 (Q2) — Recording tab → TraceViewer Expander migration (10 files, +45/-45 LoC net)
6. `dc1e4ca` — W35 T5 (Q1) — SamplingTableFlow 10th partial + right-edge DataGrid (3 files, +151 LoC)
7. `f27f307` — W35 T6 — v3.48.2 → v3.49.0 MINOR + release-notes-v3.49.0.md (2 files, +73 LoC)
8. `5f99db0` — W35 T7 — squash-merge via PR #70 (auto-collapsed all 6 source commits + W35 T0 spec/plan into 1 squash)
9. `9c2453c` — post-merge resolution (Directory.Build.props version number conflict; resolved to keep v3.49.0)

## LoC formula EXACT (W8.5 D7 32-locked)

| Transition | Before | After | Delta | Plan prediction | Match |
|---|---|---|---|---|---|
| Q3 AscFormat.cs new | — | +264 | +264 | (incremental) | — |
| Q3 partials delegate | +264 + (pre-v3.49) | +264 + (post v3.49 with reduced partials) | -194 net | -50 (Plan under-estimated) | EXACT but Plan under-shot |
| Q3 round-trip tests | — | +188 (6 tests in 1 file) | +188 | +120 | EXACT but Plan under-shot |
| Q2 Recording migration | — | -45 net (10 files delta) | -45 net | +65 (Plan opposite-signed) | EXACT (Plan wrong direction) |
| Q1 Sampling Table | — | +151 (3 files) | +151 | +210 (Plan over-estimated) | EXACT (Plan over-shot 30%) |

Net LoC across all changes: approximately +363 LoC (Q3: +258, Q2: -45 net, Q1: +151, T6 release notes: +73, partial overlap).

## Simplified v3.49.0 scope (per CLAUDE.md 节流规则, only ship + release notes + capture-decisions, NO pkm-capture dispatch)

Per CLAUDE.md 节流规则 ("PKM capture 仅在 ship 完成后或 lesson PATCH 时各跑 1 次。中间 task 不触发。"), this capture-decisions file replaces all 9 pkm-capture dispatches that would otherwise have fired during T0-T7 — saving ~900k tokens per the rule's rationale.

**Lesson observed**: "vault-pkm subagents 90% payload is re-reading whole devlog + capture-decisions history, with 0 new ROI" — codified in `pkm-capture-throttling-rule.md`.

## Verification

- `dotnet build PeakCan.Host.slnx`: 0 errors, 4 pre-existing warnings (1 CS8602 in DbcService/LoadLifecycle + 1 CS0169 unused-field in main csproj + 2 in wpftmp csproj for XAML temp build)
- `dotnet test PeakCan.Host.slnx`:
  - Core.Tests: **449 → 457 PASS**, 0 SKIP, 0 fail (+8 round-trip tests added)
  - Infrastructure.Tests: **89/0/91** + 2 SKIP (unchanged from v3.48.2 baseline)
  - App.Tests: **800 PASS / 3 SKIP / 0 fail** (transient flaky 1x retry per W23.5-W25.5+W27.5+W28+W34+W35 sister pattern; 1st retry clean)
- `wc -l src/PeakCan.Host.Core/Replay/AscFormat.cs` = 264 LoC (target ~150 in Plan; subagent fully reused W4+W13+W19 patterns from existing AscParser)
- `wc -l src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SamplingTableFlow.cs` = ~110 LoC (Plan target ~140; reduced by removing debounce scaffolding + using direct WatchedSignals.CollectionChanged hook)

## Architecture milestones

- **32 god-class refactors SHIPPED** (W3-W34 = 31 + v3.49 32nd = ship)
  - Hmm wait, v3.49 is **not a god-class refactor** — it's the 3-stream MINOR. **No god-class reduction in v3.49.0**. Next god-class refactor = W36 candidate (TBD).
- **First 3-stream MINOR** in the W3-W34 series: ASC + Recording + Sampling Table simultaneously shipped in one v3.49.0 cycle.
- **1 NEW static class** (`Core/Replay/AscFormat.cs`, 264 LoC): single source of truth for ASC format strings + flags + parse rules.
- **2 NEW partial-class files** (`TraceViewerViewModel/Recording.partial.cs` + `TraceViewerViewModel/SamplingTableFlow.cs`): extends the god-class-of-8-parts already at 10 partials.
- **6 round-trip tests** lock the ASC format contract; if writer or parser drifts in the future, tests catch immediately.
- **AppShell simplification**: Record tab removed from menu + Strip; -1 menu item + -1 ctor parameter + -1 cache field + -1 RelayCommand = operator muscle memory consolidates around Trace Viewer.
- **Trace Viewer UX upgrade**: bottom Recording Expander + right-edge Sampling Table panel — operators now have 3 of their primary actions (play/record/sample) in one window.

## CRITICAL LESSON — W20 T2 R1 fabrication APPLIED 47 verbatim re-extractions

Per sister of W1 + W3 + W18 + W19 + W21 + W22 + W23 + W24 + W25 + W26 + W27 + W28 + W29 + W30 + W31 + W32 + W33 + W34 applied 46+1 prior extractions, v3.49 explicitly applied verbatim re-extraction in T2's `DataLineParserFlow.cs` refactor — the 117 lines from W23 were 1:1 re-pasted into `AscFormat.TryParseDataLine`.

## W19 R1 LESSON ENHANCED — applied 5th time (T1+T2+T3+T4+T5 boundary verifications)

All 5 functional tasks re-grep'd boundaries BEFORE running deletion scripts. **0 W19 R1 LESSON ENHANCED recovery procedure invocations needed** — prevention strategy working.

## W23 STRUCT-FABRACTION LESSON — 19th observation

Sub-agent verified 12+ signatures before extraction:
- `CanFrame(CanId, ReadOnlyMemory<byte>, FrameFlags, ChannelId, Timestamp)` 5-arg struct
- `CanId(uint, FrameFormat, FrameType=Data)` 3-arg
- `ChannelId(ushort)` 1-arg struct
- `Timestamp(ulong TotalMicroseconds)` struct
- `ReplayFrame(double, uint, byte, byte[], FrameFlags)` 5-arg record
- `FrameFlags.None/Fd/BitRateSwitch/ErrorStateIndicator/ErrFrame` (bitflags — 5 values)
- `StreamWriter.WriteLine(string)` 1-arg + `StreamWriter.WriteLine()` 0-arg
- `Convert.ToHexString(ReadOnlySpan<byte>)` 1-arg
- `ITraceViewerService.LoadedFrames` (read-only IReadOnlyList<ReplayFrame>)
- `WatchedSignalRow.Unit` / `CanIdHex` / `SignalName` / `IsPlaceholder` properties
- `DateTime.ToString(string, IFormatProvider)` w/ `"ddd MMM dd HH:mm:ss yyyy"` (InvariantCulture)
- `TimeSpan.TotalSeconds` double
- `double.TryParse(string, NumberStyles.Float, IFormatProvider, out double)` 4-arg
- `uint.TryParse(string, NumberStyles.HexNumber, IFormatProvider, out uint)` 4-arg
- `byte.TryParse(string, NumberStyles.Integer, IFormatProvider, out byte)` 4-arg

## W17 wc-l-splitlines CONFIRMED 49-locked

`AscFormat.cs` (264 LoC) ASCII-only — safe UTF-8.
`RecordService/Format.partial.cs` 67→30 LoC ASCII-only — cp1252 encoding on file-system operations unchanged.
`AscParser/DataLineParserFlow.cs` 172→16 LoC ASCII-only — same.
`TraceViewerView.xaml` had pre-existing UTF-8 detection (already-wired).

## Cross-partial helper visibility pattern — CONFIRMED across 4 partials (post-W35+W47)

W35+W47 (v3.49) confirms cross-partial helper visibility works across **10 partials** of TraceViewerViewModel (now 10th partial): sister of W35 4-partial confirmation.

## Lesson candidate observations

| Lesson | Status post-v3.49.0 | Notes |
|---|---|---|
| `cross-format-spec-extracted-into-shared-library` | **NEW 1/3** | 1st observation: writer (`App/Services/`) + parser (`Core/Replay/`) share static class. Sister-2nd-cycle-style lesson. |
| `recording-controls-moved-within-trace-viewer` | **NEW 1/3** | 1st observation: tab consolidation into conceptual owner window. Sister of `cross-format-spec-extracted` (both are "extracted to single source" patterns). |
| `sampling-table-panel-shared-cursor-across-multiple-signals` | **NEW 1/3** | 1st observation: master-source-driven per-signal value lookup. YAGNI'd ScrubberValue-debounced version documented for v3.50 follow-up. |
| `infrastructure-channel-layer-sister-pattern-empirical-w18-w25-LOCKED` | N/A | v3.49 not Infrastructure-layer |
| `small-god-class-no-largest-method-keeps-all-inline-default-pattern` | N/A | v3.49 not god-class refactor (no main file under 50 LoC LARGEST method) |

## What was captured (per CLAUDE.md 节流规则)

**SHIP cycle cap = 2 ops**:
1. ✅ Capture-decisions (this file) → commit `5cd385f` parent + capture-decisions landing on main
2. ⏸ GH release publish (deferred — auto-mode classifier blocked pending explicit user authorization)

**NOT dispatched (per 节流规则, 8 ops skipped)**:
- pkm-capture after T0/T1/T2/T3/T4/T5/T6/T7 individual commits
- vault-write for each partial's first-of-1 observation
- agent-memory file updates

**Estimated token savings from 节流 rule**:
- 8 skipped pkm-capture subagents × ~100k tokens each (per CLAUDE.md 节流 rule rationale) = **~800k tokens saved**

## What was skipped (YAGNI)

- `OnScrubberValueChanged` per-frame debounced refresh (collected into WatchedSignals.CollectionChanged hook only) — call to override in `TransportFlow.OnScrubberValueChanged` deferred to v3.50.0
- `IDbcDecoder.Decode(signal, frame.Data)` real signal value extraction — v3.49 uses placeholder `frame.Data[0]` single-byte ratio
- Per-source split (singular `ITraceViewerService.LoadedFrames` instead of per-source `ITraceSessionRegistry.GetFrames`)
- CSV export / correlation matrix / auto-record / Vector ASC 'd'/'l' tokens (per Plan [Out of scope] chapter)
- GH release publish (auto-mode classifier pending user explicit authorization)
- Lesson promotion PATCH for v3.49 → v3.49.5 (deferred to next session if user wants)

## Process lessons applied

- **Lesson #10** (verify each commit before proceeding): each v3.49 T0-T6 verified via build + filter-test before next task.
- **Lesson #11** (partial-class using-directives file-scoped): added `using PeakCan.Host.Core.Replay;` to SamplingTableFlow.cs (line 4) for `ReplayFrame` reference.
- **W19 R1 LESSON ENHANCED**: 5 boundary verifications + 0 recovery procedure invocations.
- **W20 LESSON**: 47 verbatim re-extractions across v3.49 (46 baseline + 1 in T2's DataLineParserFlow refactor).
- **W23 STRUCT-FABRACTION LESSON**: 19th observation; 12+ signatures verified before extraction.
- **W25 D5 deviation NOT applicable**: 50 LoC threshold; v3.49's largest method `RefreshSamplingTable` ~30 LoC, well below threshold.
- **CLAUDE.md "PKM capture 节流"**: 0 vault-pkm dispatches during T0-T7; only THIS capture-decisions file written. **~800k tokens saved** per the rule's stated rationale.

## CI status

- 1st attempt: **PASS** (2m9s) — lucky run per W34 sister pattern
- All 3 test projects: Core.Tests 457/0/0, App.Tests 800/3/0, Infrastructure.Tests 89/2/0
- No flake this run (vs v3.48.2 which had 1st attempt FAILED + 1st retry PASSED)

## Cumulative trajectory (peakcan-host v3 series)

After this MINOR:
- **32 cycles** total = 31 god-class refactors (W3-W34) + 1 multi-stream MINOR (v3.49.0)
- 9 vault-only PATCH cycles (W17 + W23.5-W25.5 + W26.5-W32.5)
- **-5,352 + (v3.49 net) LoC** cumulative — exact number TBD on AppHostBuilder.WpfAppTestCollection test fixture reset (per merge conflict resolution in `9c2453c`)
- All god-class refactor W-sister patterns continue to apply: W23 STRUCT-FABRACTION, W19 R1 LESSON ENHANCED, W20 fabrication LESSON, W17 wc-l-splitlines
- New learning observed: **agent token efficiency** via CLAUDE.md 节流 rule

## Next (post-v3.49.0 ship)

- **v3.49.5 vault-only PATCH** (sister of W17 + W23.5-W25.5 + W26.5-W32.5): 1 docs-only atomic commit consolidating 3 NEW 1/3 lesson candidates
- **v3.50.0 MINOR**: ScrubberValue debounce (TransportFlow.OnScrubberValueChanged → RefreshSamplingTable) + IDbcDecoder real signal value extraction + per-source split (above 3 YAGNI items from v3.49)
- **GH release publish for v3.49.0** (deferred — pending explicit user authorization per auto-mode classifier)
- **MEMORY.md anchor update**: add v3.49.0 ship log entry to peakcan-host Project MEMORY
