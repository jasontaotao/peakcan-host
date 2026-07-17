# v3.57.0 MINOR — DbcViewModel god-class refactor

> Status: W39 SHIP — 24th god-class overall, 1st DbcViewModel MINOR refactor of W39
>
> Date: 2026-07-17
>
> Sister of W22 RecentSessionsService + W34 DbcSendViewModel + W37 AscLocator + W38 ScriptViewModel.

## Headline

**DbcViewModel.cs** 208 → 104 LoC (**-104 LoC, -50.0%**), matches plan's -50% target exactly. Extracted 3 NEW partials (`LoadingFlow.partial.cs` 77 + `SearchFlow.partial.cs` 33 + `ExportFlow.partial.cs` 48) into new `DbcViewModel/` subdirectory.

## Architecture milestones

- **24th god-class SHIPPED** (W3-W39 series)
- **15th App/ViewModels** god-class refactor (sister of W5+W7+W11+W16+W21+W24+W30+W31+W33+W36+W38)
- **28th subdirectory-pattern deployment** (1st of W39)
- **4th 3-partial subdirectory deployment** (W34+W37+W38+W39)
- **5th cycle** where W19 R1 LESSON ENHANCED prevented code loss (T2/T3 lessons-applied)
- **6 NEW 1/3 lesson candidates** confirmed

## LoC formula EXACT (W8.5 D7 32-locked)

| Task | Flow | Range | LoC deleted | Main after | Marker |
|---|---|---|---|---|---|
| T1 | Loading | L101-154 | 54 | 154 | 1 |
| T2 | Search | L106-126 (actual post-T1; not plan template 17 lines) | 21 | 133 | 1 |
| T3 | Export | L105-133 (actual post-T2; not plan template 29 lines) | 29 | 104 | 1 |
| **Total** | **3 partials** | **contiguous + post-T shift** | **104** | **104** | **3** |

**Per-file final LoC**:
- `DbcViewModel.cs` main = **104 LoC** (plan target ~105, achieved 104 = -1 better)
- `LoadingFlow.partial.cs` = **77 LoC**
- `SearchFlow.partial.cs` = **33 LoC**
- `ExportFlow.partial.cs` = **48 LoC**
- **Total = 262 LoC** (+54 over plan's ~208 estimate; +54 = 3 × ~18-LoC cross-flow header block which plan estimates omitted — sister of W38 T3 reviewer LESSON)

## Verification

- `dotnet build src/`: 0 errors, 6 pre-existing unrelated warnings
- `dotnet test --filter "FullyQualifiedName~DbcViewModel"`: 7/7 PASS
- `dotnet test` full suite (3 projects, single-threaded): **1456 PASS / 0 FAIL / 5 SKIP** (matches v3.56.0 baseline)
- LoC main `DbcViewModel.cs` ≤ 110 (achieved 104)
- 3 NEW partial files in `DbcViewModel/` subdirectory
- 5 `[ObservableProperty]` backing fields remain in main (W19+W22+W23+W34+W37+W38 sister)
- 2 `[RelayCommand]` annotated methods travel with their attributes (W19 sister)
- DI registration unchanged (`AddSingleton<DbcViewModel>()` factory in AppServicesFlow.cs)
- XAML bindings unchanged (all 5 properties + 2 commands remain valid)

## What stays the same

- Public API surface (`DbcViewModelTests` pass without modification)
- `LoadedPath` + `Status` + `SearchText` + `TotalMessages` + `TotalSignals` properties
- `OpenCommand` + `ExportCsvCommand` (source-gen from [RelayCommand])
- `Messages` + `FilteredMessages` ObservableCollection
- `_allMessages` backing list
- Ctor event subscriptions at L97-98 (DbcLoaded += OnLoaded; LoadFailed += OnLoadFailed) preserved verbatim

## Sister-lesson candidates confirmed at W39 SHIP

- `partial-extraction-must-use-original-code-from-head-not-fabricated-api` (3/3 LOCKED W21): W39 6th god-class application (T1+T2+T3) — 26th application total; verbatim byte-for-byte diff in T3
- `add-partial-keyword-to-monolithic-class-before-extraction` (3/3 LOCKED W21): held (W39 class already partial)
- `subdirectory-partials-pattern-empirical-27-precedents` (3/3 LOCKED W20): W39 28th deployment, sister-of-W38
- **NEW 1/3** `dbc-load-and-csv-export-are-separate-user-facing-actions-must-not-share-partial` (W39 1st obs): ExportCsv 独立 partial
- **NEW 1/3** `dispatcher-marshal-pattern-must-stay-coupled-with-event-handler-in-same-partial` (W39 1st obs): RunOnUi + OnLoaded + OnLoadFailed 同 partial
- **NEW 1/3** `search-filter-must-stay-coupled-with-observableproperty-hook-in-same-partial` (W39 1st obs): OnSearchTextChanged partial void + ApplyFilter 同 partial
- **NEW 1/3** `2-non-contiguous-block-deletion-for-load-flow-with-ctor-subscription-between-methods` (W39 1st obs): identified as potential concern but T1 range was actually contiguous (no ctor-subscription between methods)
- **NEW 1/3** `wpf-savefiledialog-usage-keeps-exportflow-tightly-coupled-to-app-layer` (W39 1st obs): ExportFlow 留在 App 层因为 WPF-specific API
- **NEW 1/3** `3-partial-subdirectory-pattern-empirical-w34-w37-w38-w39` (W39 1st obs): 4th 3-partial subdirectory deployment
- **NEW 1/3 (T2 reviewer observation)** `script-vs-actual-range-mismatch-can-silently-pass-w19-r1-tolerance-check`: T2 had 17→21 delta drift that ±3 tolerance silently accepted; T3 caught + corrected

## W19 R1 LESSON ENHANCED — 6th + 7th + 8th 0-failure applications (with caveat)

- **T1**: re-grep post-T0 → exact range (101..154) → first-attempt PASS with delta=54 EXACT
- **T2**: re-grep post-T1 → caught range drift (17-line script vs 21-line actual) → VERIFIED CONTENT CORRECT, but lesson violation flagged
- **T3**: re-grep post-T2 → detected plan template drift (plan said 180..208, actual post-T2 was 105..133) → first-attempt PASS with delta=29 EXACT

All 3 tasks = 0 build/test failures. T2's lesson violation (script-vs-actual drift) was caught by T2 reviewer and T3 implementer was warned — T3 avoided recurrence.

## Cumulative LoC reduction (W3-W39)

| Cycle | God-class | LoC reduction |
|---|---|---|
| W3-W34 (30 cycles) | various | -3,671 LoC |
| W35 | PeakCanChannel 2nd-cycle | -116 LoC |
| W36 | StatsViewModel | ~-150 LoC |
| W37 | AscLocator | -131 LoC (-58.2%) |
| W38 | ScriptViewModel | -142 LoC (-63.1%) |
| **W39** | **DbcViewModel** | **-104 LoC (-50.0%)** |
| **Total** | **33 god-classes** | **~ -4,314 LoC** |

## What does NOT change

- No new test cases added (refactor PATCH with zero test delta)
- No public/internal API change
- No facade pattern (W3-W38 CONFIRMED direct partial-class visibility)
- No `[LoggerMessage]` partial duplication risk (1 LogOpenInvoked stays in main)
- No `DbcService` refactor (separate concern)
- No `SignalViewModel` refactor (separate concern)

## Next steps

- **W40+ candidates**: TraceSessionAutoSaver (212 LoC, no partials) / TraceViewerService (217 LoC, no partials) / DidDatabase (202 LoC, no partials) / SignalDecoder (204 LoC, no partials)
- 9 NEW 1/3 lesson candidates need 2nd observation each before promotion to STANDALONE
- Real feature work -- user direction needed