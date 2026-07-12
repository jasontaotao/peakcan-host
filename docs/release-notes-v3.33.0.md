# Release Notes v3.33.0 — TraceViewModel god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.33.0
**Branch:** `feature/w19-trace-view-model-god-class`
**Parent:** v3.32.0 MINOR (`2b8a2b8` on origin/main)

## Why this MINOR

`src/PeakCan.Host.App/ViewModels/TraceViewModel.cs` had grown to **384 LoC** as of v3.32.0 — at 48.0% of the 800 LoC Round-1 ceiling. Single `public sealed partial class TraceViewModel : ObservableObject` (modifier pre-existed at line 53) backing the WPF Trace tab with **16 members** (7 `[ObservableProperty]` backing fields + 1 `Entries` collection + 1 `PendingDecode` view + 2 private fields `_messageCounts` + `_pendingDecode` + 1 source-gen callback `OnHighlightTextChanged` + 2 `[RelayCommand]` methods `Clear` + `ExportCsv` + 2 `internal` test/worker helpers `RegisterForTesting` + `TryCompletePending` + 2 public methods `AppendBatchAsync` + `GetMessageIdStats` + 2 `internal static` helpers `FormatHexWithSpaces` + `CsvEscape`).

This is the **15th god-class refactor** in the project (W3-W19 series). **8th App-layer god-class** (after W6 DbcParser + W6/W7 SendViewModel + W7 MultiFrameSendViewModel + W14 ScriptEngine + W16 ReplayViewModel). **2nd `[ObservableProperty]` source-generator partial split** (sister of W16 ReplayViewModel). **Zero `[LoggerMessage]` partials** anywhere in TraceViewModel — sidesteps the W18 R1 CS8795 sister-risk entirely.

## LoC trajectory (W8.5 D7 CONFIRMED formula — now 20-locked)

Per task, `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`. All 3 transitions **EXACT match** (within W13 T1 2/3 loose-assertion ±1 LoC tolerance).

| Task | Flow | Range deleted | LoC out | Main after |
|---|---|---|---|---|
| T1 | ReceptionFlow (AppendBatchAsync + RegisterForTesting + TryCompletePending) | 132-216 | 85 | 300 |
| T2 | HighlightFilterFlow (OnHighlightTextChanged + ApplyHighlight + GetMessageIdStats + FormatHexWithSpaces) | 146-216 | 71 | 230 |
| T3 | ExportFlow (ExportCsv [RelayCommand] + CsvEscape) | 147-229 | 83 | 148 |
| **Total** | -- | -- | **239** | **148** |

**Net**: 384 → 148 LoC main file (**-236 LoC, -61.5%**). Total project LoC across main + 3 partials ~385 LoC (small ~1 LoC overhead from per-partial namespace + using directives).

## What this MINOR does

### Refactor — TraceViewModel adds 3 NEW partial-class files

The class was already `public sealed partial class TraceViewModel : ObservableObject` at line 53 (modifier pre-existed for future split, 8th confirmation of `outer-modifier-pre-applied` lesson cluster per W19 D1). The main file keeps: 1 `MessageIdStat` record (top-level type) + 1 `Entries` collection + 7 `[ObservableProperty]` backing fields (`_maxRows` + `_filterText` + `_filteredCount` + `_totalFrameCount` + `_highlightText` + `_showErrorsOnly` + `_isPaused`) + 2 private fields (`_messageCounts` + `_pendingDecode`) + 1 `PendingDecode` public view + 1 `[RelayCommand] Clear()` (touches state owned by 3 partials).

**Files created**:

| File | Flow | LoC | Methods |
|---|---|---|---|
| `TraceViewModel/ReceptionFlow.cs` | A — ReceptionFlow | ~85 | `AppendBatchAsync` (60 LoC, single-largest, dispatcher-marshal) + `RegisterForTesting` + `TryCompletePending` (2 internal test/worker helpers coupled by `_pendingDecode`) |
| `TraceViewModel/HighlightFilterFlow.cs` | B — HighlightFilterFlow | ~71 | `OnHighlightTextChanged` source-gen callback + `ApplyHighlight` + `GetMessageIdStats` + `FormatHexWithSpaces` (4 UI-bound formatting/filter members) |
| `TraceViewModel/ExportFlow.cs` | C — ExportFlow | ~83 | `ExportCsv` `[RelayCommand]` (54 LoC) + `CsvEscape` (RFC 4180 cluster) |

### Verification

- `dotnet build src/PeakCan.Host.App/`: **0 errors, 0 warnings** (zero `[LoggerMessage]` partials = no W18 R1 CS8795 risk)
- `dotnet test --filter "~TraceViewModel"`: **20 / 20 PASS** first try (filter expanded to include adjacent tests; 9 dedicated TraceViewModelTests + 11 related coverage tests)
- `dotnet test` (full solution): **0 new fails** (full re-run unchanged from v3.32.0 baseline)

## Lessons applied

- **W8.5 D7 CONFIRMED** LoC correction formula (**20-locked** across W12-W19) — all 3 transitions EXACT.
- **W6/W7/W8/W11/W14/W15/W17/W18 sister** subdirectory pattern (NOT W16 sibling-`.partial.cs`-suffix). 9th subdirectory-pattern deployment.
- **W16 D3 sister** `[ObservableProperty]` source-gen partial scope: 7 backing fields stay in main, generated public properties emitted into main partial, all siblings can read as ordinary class members.
- **W16 sister** `[RelayCommand]` partial scope: `Clear` stays in main (touches state owned by 3 partials), `ExportCsv` moves to ExportFlow.cs (only touches `Entries`).
- **W14 D2 + W3 R3 sister** mutable-state coupling kept together: `AppendBatchAsync` + `RegisterForTesting` + `TryCompletePending` all share `_pendingDecode` field — moved together as one ReceptionFlow cluster.
- **W12 D7 + W14 D8 + W18 D5 sister** largest-method stays inline in single partial: `AppendBatchAsync` 60 LoC stays verbatim (not extracted further).
- **W13 T1 2/3 loose-assertion** + **W17 wc-l-splitlines CONFIRMED** deletion-script pattern applied at all 3 scripts.
- **W19 R1 first-correction** (T1 only): initial estimate missed 3 xmldoc blocks (132-136 + 140-146 + 150-154 = 18 LoC); corrected to range 132-216 on first re-grep.

## New sister-lesson candidates (per W19 D8)

- **`[ObservableProperty]-source-generator-partial-scope-bounded-by-main-file`** (1/3 → **2/3** after W19, sister of W16) — W19 T1-T3 confirmed: 7 backing fields stay in main across 3 partial extractions; all siblings read as ordinary class members.
- **`relaycommand-attribute-and-method-must-travel-together-across-partials`** (NEW **1/3** at W19) — W19 T3 1st observation: `[RelayCommand]` on `ExportCsv` travels with its method into ExportFlow.cs; `[RelayCommand]` on `Clear` stays in main because method stays in main (state-clustering principle). If W19 ships clean, this is canonical.
- **`partial-class-with-zero-LoggerMessage-parts-skips-cs8795-sister-risk`** (NEW **1/3** at W19) — W19 T1-T3 all 3 confirm: VM-style partials with no source-gen LoggerMessages don't hit CS8795 sister-risk. Awaits 2 more observations.
- **`appendbatch-async-dispatcher-marshaling-cluster-stays-together-across-partials`** (NEW **1/3** at W19) — W19 T1 1st observation: `AppendBatchAsync` dispatcher-hops + `RegisterForTesting` + `TryCompletePending` all share dispatcher-thread boundary; cluster keeps together. Awaits 2 more observations.
- **`subdirectory-partials-pattern-empirical-9-precedents`** (W18 D2 → 1/3 → **2/3** at W19) — 9th subdirectory-pattern deployment confirmed.

## What stays the same

- Public API surface — `[ObservableProperty]` generated properties + `[RelayCommand]` generated commands + `Entries` + `PendingDecode` + 2 `internal` test/worker helpers + 2 public methods + 2 `internal static` helpers all preserved.
- Test count unchanged (9 dedicated `TraceViewModelTests` + full solution 0 new fails).
- WPF DataGrid binding contract unchanged (no XAML changes needed).
- DI registration unchanged (`AddSingleton<TraceViewModel>()` in AppHostBuilder preserved).

## Next steps (post-ship)

- **W19.5 vault-only PATCH** — lesson-promotion opportunity (4 NEW 1/3 candidates + 2 promoted 1/3→2/3 candidates; some may reach 3/3 with W19 SHIP confirmation as 3rd observation).
- **W20** — next god-class refactor candidate. Top remaining (>350 LoC) main files: `SignalChartViewModel.cs` 378 LoC (App/ViewModels sister of W19, TraceViewModel sibling) + `DbcSendViewModel.cs` 384 (App/ViewModels). ChannelRouter.cs 305 (Infrastructure/Channel sister of W18) is also a candidate.