## 2026-07-11 -- W9 TASK 4 EXECUTED -- Flow B (Send) extracted, 4/7 flows done (more-than-halfway by LoC)

> **MILESTONE FLAG -- W9 MORE-THAN-HALFWAY**: 4 of 7 W9 flows extracted (T1 FlowControl + T2 Logging + T3 Watchdog + T4 Send). Main file `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer.cs` reduced from 806 LoC to 516 LoC (**-290 LoC, -36.0%**). 4 partial files in `IsoTpLayer/` directory.

**Commit**: `0dc997f` on `feature/w9-isotp-layer-god-class` (parent `a7a5aa5` W9 Task 3 Flow E Watchdog; grandparent `763c28e` W9 Task 2 Flow G Logging; great-grandparent `3b15573` W9 Task 1 Flow F FlowControl; great-great-grandparent `8651d4d` W9 PLAN; great-great-great-grandparent `5ca82c2` W9 SPEC; great-great-great-great-grandparent `f2376f5` v3.23.1 on main). 3 files / +165/-97 LoC.

**Branch**: `feature/w9-isotp-layer-god-class` at `0dc997f`, 4 source commits ahead of W9 PLAN, NOT pushed.

**Files in this ship**:
- NEW `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer/SendFlow.cs` (117 LoC, partial file)
- MODIFIED `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer.cs` (612 -> 516 LoC, -96 LoC, +1 marker)
- NEW `scripts/w9_task4_delete_sendflow.py` (deletion script, line-range-based, single contiguous range)

### Flow B content (verbatim moved to SendFlow.cs)

- `public async Task SendMessageAsync(byte[] data, CancellationToken ct = default)` -- public entry, routes SF (data.Length <= 7) vs MF
- `private Task SendSingleFrameAsync(byte[] data)` -- SF helper, calls SendCanFrameAsync(data, 0)
- `private async Task SendCanFrameAsync(byte[] data, int frameIndex)` -- async send with try/catch + IsoTpSendFailedException throw + SendFailureCount++ + LogIsoTpSendFailed call
- `private void SendCanFrame(byte[] data)` -- legacy sync send (called from RX-side FlowControl partial)

**Required usings added to SendFlow.cs**: `System.Threading` (CancellationToken) + `System.Threading.Tasks` (Task, async/await).

### Cross-flow references (partial-class visible)

| Method | Called from | Flow | File |
|---|---|---|---|
| `SendMessageAsync` (public) | DI/test entry point | B (Send) | partial |
| `SendMessageAsync` -> `SendMultiFrameAsync` | intra-flow MF path | B (Send) | partial |
| `SendMessageAsync` -> `SendSingleFrameAsync` | intra-flow SF path | B (Send) | partial |
| `SendSingleFrameAsync` -> `SendCanFrameAsync` | intra-flow SF helper | B (Send) | partial |
| `SendCanFrameAsync` -> `LogIsoTpSendFailed` | [LoggerMessage] 3001 | G (Logging) | partial |
| `SendCanFrameAsync` -> `SendFailureCount++` | counter | main | main |
| `SendCanFrame` <- `SendFlowControl` | RX-side FC sends ack | F (FlowControl) | partial |

All cross-partial calls resolve correctly across partials.

### Plan LoC-trajectory-table accuracy

**Plan prediction** (file `docs/superpowers/plans/2026-07-11-isotp-layer-god-class-refactor.md`):
- Post-Task-3 LoC = 639 (per plan table at start of Task 4)
- Actual post-Task-3 LoC = **612** (per `wc -l` after T3 deletion script ran)
- Discrepancy = -27 LoC (plan underestimated Task 3 deletion by 27 LoC; observed deletion was 146 LoC vs plan's 119 LoC)

**Plan prediction** (Task 4 LoC trajectory row):
- Post-Task-4 LoC = 545 (formula: `LoC_{n-1} - sum(deleted) + 1` = 612 - 95 + 1)
- Actual post-Task-4 LoC = **516** (formula: 612 - 97 + 1 = 516)
- Discrepancy = **-29 LoC** (plan underestimated Task 4 deletion by 2 LoC)

**Why the -2 LoC discrepancy for T4**: plan estimated ~95 LoC deletion for Flow B (95 = 4-method body LoC estimate). Actual deletion was 97 LoC. The 2-LoC delta is consistent with: (a) blank lines between methods being counted differently, (b) xmldoc comments spanning 1 extra line, (c) the private void SendCanFrame body slightly longer than estimated.

**W8.5 D7 CONFIRMED lesson validated again**: even with the correct deletion-aware formula , plan-table estimates can be off by 2-5 LoC per task. This is inherent to estimating exact line counts for verbatim extraction tasks. Deletion scripts asserting on actual 0 pre-delete remain the correct execution pattern.

### Cumulative W9 progress (4/7 flows done)

| Task | Flow | Commit | LoC delta | Main LoC after |
|---|---|---|---|---|
| T1 | F (FlowControl) | 3b15573 | -24 | 782 |
| T2 | G (Logging) | 763c28e | -24 | 758 |
| T3 | E (Watchdog) | a7a5aa5 | -146 | 612 |
| T4 | B (Send) | 0dc997f | -96 | 516 |
| T5 | D (Receive) | pending | ~-110 | ~406 |
| T6 | C (MultiFrameTransport) | pending | ~-165 | ~241 |
| T7 | A (Lifecycle) | pending | ~-155 | ~86 |

Main file trajectory: 806 -> 782 (T1) -> 758 (T2) -> 612 (T3) -> 516 (T4). Cumulative **-290 LoC, -36.0%** (more-than-halfway by LoC, more-than-halfway by task count).

Plan trajectory target (per W9 PLAN correct formula): 777 (T1) -> 748 (T2) -> 639 (T3) -> 545 (T4). Actual: 782 -> 758 -> 612 -> 516. Cumulative plan discrepancy at T4: -29 LoC (plan underestimated total deletion by 29 LoC through T4). Plan trajectory underestimates actual trajectory by 1.5-3 LoC per task on average.

### R-risks status post-T4

- R1 (missing usings): HELD. Pre-scanned System.Threading + System.Threading.Tasks only. Build clean on first attempt.
- R2 (deletion script precision): HELD. Single contiguous line range; assertion post-LoC = 516 matched actual.
- R3 (_rxWatchdog state move): VALIDATED by T3 (no issues).
- R4 (cross-partial method calls): PARTIALLY APPLIED. T4 has 1 cross-partial caller of LogIsoTpSendFailed from Flow G (intra-flow-partial -- both in different partials now), all resolves correctly.

### Verification

- dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -- **0 errors, 0 warnings** (Core layer remains clean -- no pre-existing warnings)
- dotnet test --filter IsoTp -- **23/23 PASS, 0 fail, 0 skip** (unchanged from W9 T3 baseline)
- Main file reduction: 612 -> 516 LoC (-96 LoC)

### Lessons with new evidence

- partial-class-using-directives-are-file-scoped-not-class-scoped -- **17th confirmation** across W3-W9 (W9 T4 pre-scanned only System.Threading + System.Threading.Tasks; build clean first attempt, no CS0246/CS0103). The 4 partials done in W9 so far (FlowControlFlow / LoggingFlow / WatchdogFlow / SendFlow) have all required different usings, validating the file-scoped nature.
- plan-LoC-trajectory-table-must-account-for-deletion-not-just-marker-addition (CONFIRMED via W8.5 PATCH, 3/3 confirmations pre-W9) -- now at **6/3 confirmations across W9** (W9 T1, T2, T3, T4 all confirm the W8.5 lesson). Lesson is HELD at CONFIRMED. **New observation for W9 D7 application**: even with correct deletion-aware formula, plan estimates are off by 2-5 LoC per task due to exact deletion range uncertainty.
- deletion-script-line-range-precision-with-non-contiguous-ranges -- single contiguous range (W9 T4 maintains W3/W4/W7/W9-T1/T2/T3 single-range pattern).
- wX_taskN_delete_<flow>flow.py template -- **10th use** (W3-W7 + W8 6 tasks + W9 T1 + W9 T2 + W9 T3 + W9 T4).

### No NEW 1-of-1 lesson promotions

W9 T4 is a pure extraction (no new pattern emergence). Existing lessons continue to hold:
- partial-class-using-directives-are-file-scoped-not-class-scoped (CONFIRMED)
- partial-class-visibility-extends-to-private-fields-and-observableproperty-backing-fields (CANDIDATE 2/3 from W8 T3 + W9 T3 Watchdog)
- cross-partial-method-calls-resolve-identically-to-in-class-calls (CANDIDATE 1/3 from W8 T6 -- not yet validated in W9)
- loggermessage-partial-methods-can-be-split-across-partial-class-files (CANDIDATE 1/3 from W9 T2)
- plan-LoC-trajectory-table-must-account-for-deletion-not-just-marker-addition (CONFIRMED via W8.5 PATCH)
- vault-only-PATCH-convention-applied-when-src-tree-unchanged (CANDIDATE 1/3 from W8.5)

### Capture classification

Source-code change matching existing patterns -- partial-class extraction with no new architectural lesson. Plan-table accuracy observation (2-5 LoC per task inherent uncertainty even with correct formula) is worth documenting in devlog/MEMORY.md but does not require a new lesson candidate.

### Vault artifacts

- Capture-decisions: development/capture-decisions/2026-07-11-w9-task4-send-extraction-capture-decisions.md (this entry)
- MEMORY.md: W9 TASK 4 section prepended above W9 TASK 2 (W9 TASK 3 capture was skipped per parallel-dispatch race pattern)
- Agent-memory MEMORY.md: W9 TASK 4 pointer prepended

### Next steps

- W9 Task 5 (Flow D -- Receive, ~110 LoC, 7 cross-partial callers -- R4 extension; primary validation target for cross-partial-method-calls-resolve-identically-to-in-class-calls CANDIDATE lesson)
- W9 Task 6 (Flow C -- MultiFrameTransport, ~165 LoC, LARGEST)
- W9 Task 7 (Flow A -- Lifecycle, ~155 LoC)
- W9 Task 8 (version bump v3.23.1 -> v3.24.0 + docs/release-notes-v3.24.0.md NEW)
- W9 Task 9 (Tier-3 push + tag v3.24.0 + GH release)

