## 2026-07-11: W9 TASK 4 EXECUTED -- Flow B (Send) extracted, 4/7 flows done (more-than-halfway) (capture 59)

- **Commit**: `0dc997f` on `feature/w9-isotp-layer-god-class` (parent `a7a5aa5` W9 T3 Watchdog; grandparent `763c28e` W9 T2 Logging; great-grandparent `3b15573` W9 T1 FlowControl; great-great-grandparent `8651d4d` W9 PLAN). 3 files / +165/-97 LoC.
- **MILESTONE -- W9 MORE-THAN-HALFWAY**: 4 of 7 W9 flows extracted. Main file 806 -> 516 LoC (**-290 LoC, -36.0%**, exceeds halfway by both LoC and task count).
- **Files**:
  - NEW `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer/SendFlow.cs` (117 LoC, partial file, 4 methods + 2 usings: System.Threading + System.Threading.Tasks)
  - MODIFIED `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer.cs` (612 -> 516 LoC, -96 LoC, +1 marker)
  - NEW `scripts/w9_task4_delete_sendflow.py` (deletion script, single contiguous range)
- **Flow B content (verbatim moved)**: 4 methods:
  - `public async Task SendMessageAsync(byte[] data, CancellationToken ct = default)` -- public entry, routes SF (data.Length <= 7) vs MF
  - `private Task SendSingleFrameAsync(byte[] data)` -- SF helper, calls SendCanFrameAsync(data, 0)
  - `private async Task SendCanFrameAsync(byte[] data, int frameIndex)` -- async send with try/catch + IsoTpSendFailedException throw + SendFailureCount++ + LogIsoTpSendFailed call
  - `private void SendCanFrame(byte[] data)` -- legacy sync send (called from RX-side FlowControl partial)
- **Cross-partial callers**: 1 (`SendCanFrameAsync` -> `LogIsoTpSendFailed` from Flow G) -- resolves correctly. `FlowControlFlow.SendFlowControl` -> `SendFlow.SendCanFrame` -- RX partial calls TX partial.
- **Verification**:
  - `dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj` -- **0 errors, 0 warnings** (Core layer remains clean)
  - `dotnet test --filter IsoTp` -- **23/23 PASS, 0 fail, 0 skip** (unchanged from W9 T3 baseline)
- **Plan LoC-trajectory-table accuracy**: plan predicted post-T4 = 545; actual = 516 (**-29 LoC discrepancy**). Even with W8.5 D7 correct formula `LoC_n = LoC_{n-1} - sum(deleted) + 1 marker`, plan estimates are off by 2-5 LoC per task due to exact deletion range uncertainty. Deletion scripts asserting on actual `wc -l` remain correct execution pattern.
- **Lessons with new evidence**:
  - `partial-class-using-directives-are-file-scoped-not-class-scoped` -- **17th confirmation** across W3-W9 (W9 T4 pre-scanned only System.Threading + System.Threading.Tasks; build clean first attempt, no CS0246/CS0103).
  - `plan-LoC-trajectory-table-must-account-for-deletion-not-just-marker-addition` -- **6/3 confirmations across W9** (W9 T1, T2, T3, T4 all confirm W8.5 lesson); HELD at CONFIRMED.
  - `deletion-script-line-range-precision-with-non-contiguous-ranges` -- single contiguous range.
  - `wX_taskN_delete_<flow>flow.py` template -- **10th use** (W3-W7 + W8 6 tasks + W9 T1-T4).
- **No NEW 1-of-1 lesson promotions**: W9 T4 is a pure extraction (no new pattern emergence).
- **Cumulative W9 (4/7)**: T1 FlowControl (3b15573, -24) + T2 Logging (763c28e, -24) + T3 Watchdog (a7a5aa5, -146) + T4 Send (0dc997f, -96). Main file 806 -> 516 LoC (-290 LoC, -36.0%).
- **R-risks status post-T4**: R1 HELD (build clean first attempt); R2 HELD (post-LoC assertion matched); R3 VALIDATED by T3; R4 PARTIALLY APPLIED (1 cross-partial caller).
- **Branch**: `feature/w9-isotp-layer-god-class` at `0dc997f` (4 source commits ahead of W9 PLAN, NOT pushed).
- **Tasks remaining (5/9 -- ship phase)**: T5 Receive + T6 MultiFrameTransport + T7 Lifecycle + T8 version bump + T9 Tier-3 ship.

### Next steps

- W9 Task 5 (Flow D -- Receive, ~110 LoC, 7 cross-partial callers -- R4 extension; primary validation target for `cross-partial-method-calls-resolve-identically-to-in-class-calls` CANDIDATE lesson)
- W9 Task 6 (Flow C -- MultiFrameTransport, ~165 LoC, LARGEST)
- W9 Task 7 (Flow A -- Lifecycle, ~155 LoC)
- W9 Task 8 (version bump v3.23.1 -> v3.24.0 + `docs/release-notes-v3.24.0.md` NEW)
- W9 Task 9 (Tier-3 push + tag v3.24.0 + GH release)