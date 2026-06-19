# PeakCan Host — SDD Progress Ledger

This ledger is the recovery map for subagent-driven development. Tasks listed here as `complete` are DONE — do not re-dispatch.

## Plan reference
- Spec: `D:/claude_proj2/docs/superpowers/specs/2026-06-18-peakcan-host-design.md`
- Plan: `D:/claude_proj2/docs/superpowers/plans/2026-06-18-peakcan-host-implementation.md`
- Repo: `D:/claude_proj2/peakcan-host/` (main branch, single-developer)
- Ledger updated: 2026-06-18

## Status

| Task | Status | Commits | Reviewer verdict |
|---|---|---|---|
| 1  scaffolding        | complete | feat: chore commit `scaffold 3-layer solution` | self-validated by `dotnet build` 0 warn 0 err |
| 1a plan deviations    | complete | inline in scaffolding | TFM net8→net10, LiveCharts2→OxyPlot.Wpf, Peak.Can.Basic→Peak.PCANBasic.NET |
| 2  Core domain types  | complete | `9ee928d` feat + `bd157e1` test(coverage) | 24/24 tests pass; 100% line / 100% branch coverage on Core; build 0 warn 0 err; code-reviewer APPROVE_WITH_NOTES (1 MEDIUM coverage gap fixed) |
| 3  Result/Error/ErrorCode | complete | `8ffdb29` feat + polish commit | 32/32 tests pass; 100% line / 100% branch coverage on Core; build 0 warn 0 err; code-reviewer APPROVE_WITH_NOTES (4 LOW docs/test polish applied) |
| 4  DbcTokenizer         | complete | `f5d6a2f` feat + `8df3766` fix(review) | 75/75 tests pass; 100% line coverage; build 0 warn 0 err; code-reviewer APPROVE_WITH_NOTES (4 MEDIUM + 6 LOW; MEDIUM 1+2+4 applied, MEDIUM 3 hex/binary deferred as YAGNI per MVP; LOW identifier test + column doc applied; CRITICAL infinite-loop bug caught by new exponent test and fixed in the same commit) |
| 5  DbcParser AST+basic  | complete | `9b2c197` feat | 110/110 tests pass; 100% line / 98.4% branch coverage on Core; build 0 warn 0 err; 9 AST source files (ByteOrder/ValueType/Node/ValueTable/Signal/Message/DbcDocument/DbcErrorCode/DbcParser) + 32 parser tests |

## Resume Guide (next session)

To continue:
1. Read this ledger and `docs/superpowers/plans/2026-06-18-peakcan-host-implementation.md` once.
2. `cd D:/claude_proj2/peakcan-host && git log --oneline` to confirm baseline.
3. Start at Task 2 (Core 域类型). Recommended parallel batches (file-isolated, no merge conflicts):
   - **Batch 1** (4 parallel, all in `src/PeakCan.Host.Core/`, no shared files): Task 2 (CanFrame), Task 3 (Result<T>), Task 4 (DbcTokenizer), Task 7 (SignalDecoder)
   - **Batch 2** (after Batch 1 merges): Task 5 (DbcParser basic), Task 6 (DbcParser multiplexed), Task 8 (PeakError+Mapper), Task 9 (ICanChannel)
   - **Batch 3**: Task 10 (ChannelRouter), Task 11 (BusStatisticsCollector)
   - **Serial from Task 12**: App layer (multiple files share AppShell.xaml).
4. Per the SDD skill, dispatch a fresh subagent per task, then a task-reviewer subagent; mark each complete + append to this ledger only after the review verdict is clean.
5. Plan §17 must use OxyPlot.Wpf, not LiveChartsCore (deviation). StatsViewModel/StatsView code blocks reference `LiveChartsCore` types — rewrite using `OxyPlot.PlotModel` + `LineSeries` before implementing Task 17.

## Session boundary

Last session reached the implementer-parallel decision and ran Task 1 manually (scaffolding + restore + build + commit). User opted to stop at Task 1 rather than risk mid-batch crash from context exhaustion.
| 2  Core domain types  | complete | `9ee928d` | 11/11 tests pass; 0 warn 0 err build |
| 3  Result<T>          | pending | — | — |
| 4  DBC Tokenizer      | pending | — | — |
| 5  DBC parser 基础    | pending | — | — |
| 6  DBC parser 高级    | pending | — | — |
| 7  SignalDecoder      | pending | — | — |
| 8  PeakError + Mapper | pending | — | — |
| 9  ICanChannel        | pending | — | — |
| 10 ChannelRouter      | pending | — | — |
| 11 BusStatisticsCollector | pending | — | — |
| 12 AppShell + Connect UI | pending | — | — |
| 13 Trace              | pending | — | — |
| 14 Send               | pending | — | — |
| 15 DBC view + shell nav | pending | — | — |
| 16 Signal view        | pending | — | — |
| 17 Stats view         | pending | — | — |
| 18 NetArchTest        | pending | — | — |
| 19 CI + coverage      | pending | — | — |
| 20 Final smoke + publish | pending | — | — |
| 21 README             | pending | — | — |

## Conventions
- One task = one commit (or small commit chain within a single task).
- Implementer dispatches use `claude` general-purpose subagent.
- Reviewer dispatches use `claude` general-purpose subagent.
- Model: implementer + reviewer use `claude` (session default) for now; can be downgraded per task to save cost.
- Tests required to pass before commit; coverage thresholds enforced at Task 19.

## Notes
- Wave 1 must complete before Wave 2; Wave 2 before Wave 3; Wave 3 before Wave 4; Wave 4 before Wave 5; Wave 5/6/7 in any order after.
- Hardware-dependent verification (Task 9 `PeakCanChannel` integration test) is `[Trait("category","integration")]` and skipped in CI.
- LiveCharts2 2.0.0-rc5.4 is a pre-release; if it breaks, fall back to OxyPlot.Wpf.
