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
| 5  DbcParser AST+basic  | complete | `9b2c197` feat + fix commit | 115/115 tests pass; 100% line / 98.3% branch coverage on Core; build 0 warn 0 err; code-reviewer WARN (1 HIGH exception-leak + 3 MEDIUM) — all HIGH/MEDIUM fixed: ParseUInt/ParseByte/ParseLong wrap OverflowException, HashSet seenIds replaces O(n²) duplicate check, dead Keyword_SG_ removed from IsStructuralKeyword, trailing newlines applied |
| 6  DBC parser multiplexed M/m + VAL_ | complete | `8eda017` feat + `028ef42` fix(review) | 125/125 tests pass (10 new in DbcParserMultiplexedTests); build 0 warn 0 err; code-reviewer APPROVE_WITH_NOTES (3 MEDIUM + 6 LOW) — MEDIUM 1 reverted Message.Signals to IReadOnlyList and used 'with' rebuild, MEDIUM 2 added DbcParseException for unknown msg id (including VAL_-before-BO_), MEDIUM 3 added 7 edge-case tests; LOW applied: Peek bounds, m<N> range/malformed error, FindSignalIndex + ReplaceSignalValueTableName helpers; LOW deferred: inline-VAL_ self-table naming (documented MVP shortcut, follow-up), LastOrDefault duplicate-name (rare in real DBC) |
| 7  Core SignalDecoder (LE/BE/signed/float/double) | complete | `cced679` feat + `ff79c74` fix(review) | 137/137 tests pass (12 total SignalDecoderTests: 3 plan + 3 added BE/signed/length>64 + 6 review MEDIUM tests for IEEE 754, len=64 boundary, BE non-zero start, LE byte boundary); build 0 warn 0 err; code-reviewer APPROVE_WITH_NOTES (2 MEDIUM + 5 LOW) — MEDIUM 1 documented silent short-frame truncation in XML doc, MEDIUM 2 added 6 tests covering Float/Double / signed-16 / BE-non-zero-start / LE-byte-boundary / length=64 boundary; LOW applied: classic-CAN comment fix, IEEE 754 host-endian-agnostic note, SignExtend len=64 comment; LOW deferred: vectorize 8 bits at once (follow-up if WPF trace view shows decoder hot), throw on invalid enum (low value, leave default arm) |
| 8  Infrastructure PeakError + Mapper | complete | `41cb0a1` feat + `?` fix(review) | 14/14 Infrastructure tests pass (PeakErrorMapperTests: 5 plan InlineData + 3 fact added OK msg / unknown hex / non-empty sweep + 6 review MEDIUM tests for IsOk Theory 4 cases / bitmasked composite / uint.MaxValue); build 0 warn 0 err; code-reviewer APPROVE_WITH_NOTES (2 MEDIUM + 3 LOW) — MEDIUM 1 documented MVP limitation that bit-flag composites (PCAN_ERROR_INITIALIZE\|BUSOFF) fall through to Unknown, MEDIUM 2 added IsOk helper to remove the OK-trap; LOW applied: 2 boundary tests (composite + max uint); LOW deferred: return type `Error` record (no callers yet, defer to call site materialization), split HardwareBusy into Transient vs Reset (UI message text already distinguishes them) |

## Resume Guide (next session)

To continue:
1. Read this ledger and `docs/superpowers/plans/2026-06-18-peakcan-host-implementation.md` once.
2. `cd D:/claude_proj2/peakcan-host && git log --oneline` to confirm baseline.
3. Continue at **Task 7** (Core — SignalDecoder, file-isolated from Task 6 work, no merge conflict). Recommended batch from here:
   - **Batch A** (4 parallel, all in `src/PeakCan.Host.Core/`, no shared files): Task 7 (SignalDecoder)
   - **Batch B** (after Task 7 merges): Task 8 (Infrastructure PeakError+Mapper), Task 9 (Infrastructure ICanChannel)
   - **Batch C**: Task 10 (ChannelRouter), Task 11 (BusStatisticsCollector)
   - **Serial from Task 12**: App layer (multiple files share AppShell.xaml).
4. Per the SDD skill, dispatch a fresh subagent per task, then a task-reviewer subagent; mark each complete + append to this ledger only after the review verdict is clean.
5. Plan §17 must use OxyPlot.Wpf, not LiveChartsCore (deviation). StatsViewModel/StatsView code blocks reference `LiveChartsCore` types — rewrite using `OxyPlot.PlotModel` + `LineSeries` before implementing Task 17.

## Session boundary

Last session reached the implementer-parallel decision and ran Task 1 manually (scaffolding + restore + build + commit). User opted to stop at Task 1 rather than risk mid-batch crash from context exhaustion.
| 2  Core domain types  | complete | `9ee928d` | 11/11 tests pass; 0 warn 0 err build |
| 3  Result<T>          | complete | `8ffdb29` | 32/32 tests pass; 100% coverage |
| 4  DBC Tokenizer      | complete | `f5d6a2f` + `8df3766` | 75/75 tests pass; 100% line |
| 5  DBC parser 基础    | complete | `9b2c197` + `cac575e` | 115/115 tests pass; 100% line / 98.3% branch |
| 6  DBC parser 高级    | complete | `8eda017` + `028ef42` | 125/125 tests pass (10 new); review APPROVE_WITH_NOTES → all MEDIUM fixed |
| 7  SignalDecoder      | complete | `cced679` + `ff79c74` | 137/137 tests pass; 100% line on SignalDecoder |
| 8  PeakError + Mapper | complete | `41cb0a1` + `?` | 14/14 tests pass |
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
