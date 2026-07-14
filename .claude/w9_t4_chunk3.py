with open(r'D:/claude_proj2/peakcan-host/.claude/w9_t4_capture_staging.md', 'a') as f:
    f.write('''## Lessons with new evidence

- `partial-class-using-directives-are-file-scoped-not-class-scoped` -- **17th confirmation** across W3-W9 (W9 T4 pre-scanned only `System.Threading` + `System.Threading.Tasks`; build clean first attempt, no CS0246/CS0103). The 4 partials done in W9 so far (FlowControlFlow / LoggingFlow / WatchdogFlow / SendFlow) have all required different usings, validating the file-scoped nature.
- `plan-LoC-trajectory-table-must-account-for-deletion-not-just-marker-addition` (CONFIRMED via W8.5 PATCH, 3/3 confirmations pre-W9) -- now at **6/3 confirmations across W9** (W9 T1, T2, T3, T4 all confirm the W8.5 lesson). Lesson is HELD at CONFIRMED. **New observation for W9 D7 application**: even with correct deletion-aware formula, plan estimates are off by 2-5 LoC per task due to exact deletion range uncertainty.
- `deletion-script-line-range-precision-with-non-contiguous-ranges` -- single contiguous range (W9 T4 maintains W3/W4/W7/W9-T1/T2/T3 single-range pattern).
- `wX_taskN_delete_<flow>flow.py` template -- **10th use** (W3-W7 + W8 6 tasks + W9 T1 + W9 T2 + W9