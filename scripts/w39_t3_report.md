# W39 T3 Report -- ExportFlow partial extraction

## Commit
- HEAD: c1ddd57
- Base: 80e41fb (W39 T2)

## Step-by-step results

- **Step 1 -- Re-grep post-T2 boundaries**: `grep -n ExportCsv` returned L109 (method declaration). Read L100-L133 to capture full method block. Confirmed actual EXPORT_START=105 (doc-comment), EXPORT_END=133 (closing brace) = 29 LoC. Plan template said 180..208 -- T2 deletion had shifted line numbers as the T2 reviewer warning predicted.
- **Step 2 -- Extract verbatim from HEAD**: `git show HEAD:... | sed -n '105,133p'` produced 29 lines matching plan template byte-for-byte. Saved to `scripts/w39_t3_export_content.txt`.
- **Step 3 -- Create ExportFlow.partial.cs**: Wrote 48 LoC file (19-line cross-flow header + 29 LoC method body) per plan template, with [RelayCommand] preserved on ExportCsv.
- **Step 4 -- Pre-deletion build**: 2 errors as expected (duplicate ExportCsv across partials + main). Will be resolved by deletion.
- **Step 5 -- Delete via range script**: `scripts/w39_task3_delete_exportflow.py` (START=105, END=133, EXPECTED_DELTA=29). Output: `deleting 29 lines (105..133)`, `delta 29 within W19 R1 +/- 3 LoC tolerance of 29`, `main LoC now 105`. **EXACT match -- 0 W19 R1 failure**.
- **Step 6 -- Post-deletion build + tests**: `dotnet build` 0 errors (6 pre-existing warnings from unrelated files: CS0169 / CS8602 / CS8603 in TraceViewerViewModel + DbcService + EnumTrackerLineSeries -- NOT from W39 T3). `dotnet test --filter DbcViewModel` **7/7 PASS / 0 FAIL** in 55ms -- baseline maintained.
- **Step 7 -- Final LoC distribution** (logical lines via `splitlines()`):
  - DbcViewModel.cs: 105 LoC (target ~105 -- EXACT)
  - LoadingFlow.partial.cs: 78 LoC (54 method + 24 header)
  - SearchFlow.partial.cs: 33 LoC (20 method + 13 header)
  - ExportFlow.partial.cs: 48 LoC (29 method + 19 header)
  - Total 264 LoC across 4 files (no net logic change; +56 LoC from 3 cross-flow header blocks per W38 T3 reviewer LESSON)
- **Step 8 -- Commit**: c1ddd57 with 3 files changed, 79 insertions, 29 deletions. Untouched: pre-existing untracked files (.claude/worktrees/, scripts/w38_*, scripts/w39_t1_*/t2_* files, modified 4 docs).

## Lessons applied / observed

- **W19 R1 LESSON ENHANCED (3rd 0-failure application)**: Re-grep BEFORE script + use actual post-T2 range (105..133, NOT plan template 180..208). Script's `EXPECTED_DELTA=29` exactly matched actual deletion count. Recovery procedure not needed -- first-attempt PASS.
- **W20 LESSON**: Boundary verification + verbatim re-extract from HEAD -- both verbatim content and `git show | sed` extract matched the plan template byte-for-byte.
- **W23 LESSON (18th struct-ctor confirmation)**: No new struct constructors in T3 (ExportCsv uses no new struct); only Microsoft.Win32.SaveFileDialog + StringBuilder + File.WriteAllText. 3 API boundaries verified.
- **W22-W37 partial-extraction pattern**: 4th partial deployed in DbcViewModel/ subdirectory. Public API 100% preserved -- `ExportCsvCommand` source-gen still resolves via [RelayCommand] on the partial method.

## New lesson candidate observations

- **`wpf-savefiledialog-usage-keeps-exportflow-tightly-coupled-to-app-layer`** (NEW 1/3 at W39 T3): ExportFlow stays in App layer because of `Microsoft.Win32.SaveFileDialog` -- could NOT move to Core. Sister of T1 LoadingFlow (also App-layer due to `WpfFileDialogService`).
- **`partial-extraction-must-use-original-code-from-head-not-fabricated-api`** (cumulative 6th application at W39 T3): verbatim 29-line re-extract from HEAD, 0 fabrication.

## Next

- W39 T4: v3.57.0 MINOR version bump + release notes (208 LoC main reduction, 3 NEW partials, ~+56 cross-flow header overhead).
- W39 T5: Tier-3 ship (push + PR + squash + tag v3.57.0 + GH release). This is the TIER-3 ship boundary where PKM capture will fire.
