# W19 Spec — TraceViewModel god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.App/ViewModels/TraceViewModel.cs` from 384 LoC to ~190 LoC by extracting 3 logical flow groups into partial-class files. Public API + 9 existing tests unchanged.

**Architecture:** Sister pattern to W6/W7/W8/W11/W14/W15/W18 (subdirectory + non-suffix `.cs` filenames). 8th App-layer god-class split (after W6 DbcParser + W6 SendViewModel + W7 MultiFrameSendViewModel + W14 ScriptEngine + W16 ReplayViewModel). The class is **already** `public sealed partial class TraceViewModel : ObservableObject` at line 53 — no CS0260 mitigation needed.

**Tech Stack:** C# .NET 10, WPF ViewModel with `CommunityToolkit.Mvvm` `[ObservableProperty]` + `[RelayCommand]` source-generators. App layer.

**Plan:** [`../plans/2026-07-12-trace-view-model-god-class-refactor.md`](../plans/2026-07-12-trace-view-model-god-class-refactor.md)
**Branch:** `feature/w19-trace-view-model-god-class` (created from `main` @ `2b8a2b8` v3.32.0 HEAD)

## Global Constraints

- **Public API unchanged.** 7 generated `[ObservableProperty]` public properties + 2 generated `[RelayCommand]` public properties + 1 `Entries` collection + 1 `PendingDecode` view + 2 `internal` test/worker helpers (`RegisterForTesting`, `TryCompletePending`) + 2 public methods (`AppendBatchAsync`, `GetMessageIdStats`) + 2 `internal static` helpers (`FormatHexWithSpaces`, `CsvEscape`) all preserved.
- **partial-class visibility.** All private methods + private fields visible across partial files. Each partial carries its own `using` block per W16 ReplayViewModel partial pattern.
- **Test coverage unchanged.** All 9 existing `TraceViewModelTests` (Default_Entries, Default_MaxRows, AppendBatch_With_Null_Application, AppendBatch_On_StaThread, GetMessageIdStats_*, Clear_Resets_All_Counters, PendingDecode_*) pass without modification.
- **Line-ending normalized to LF.**
- **No behavioral change.** Every method body, xmldoc, comment, and whitespace moves verbatim.
- **No version bump until Task 4.** Tasks 1-3 keep `src/Directory.Build.props` at v3.32.0. Task 4 bumps to v3.33.0.

## Current state (384 LoC)

`src/PeakCan.Host.App/ViewModels/TraceViewModel.cs` (v3.32.0 HEAD) has:
- 1 `public sealed record MessageIdStat(...)` (lines 11-15) — top-level type
- 1 `public sealed partial class TraceViewModel : ObservableObject` (line 53) — already partial
- 7 `[ObservableProperty]` backing fields (lines 76, 83, 87, 91, 98, 104, 111)
- 1 `Entries` `ObservableCollection<TraceEntry>` (line 60)
- 1 `PendingDecode` `IReadOnlyDictionary` view (line 129)
- 2 private fields: `_messageCounts` (Dictionary) + `_pendingDecode` (ConcurrentDictionary) — lines 114, 121
- 1 `OnHighlightTextChanged` source-gen callback (line 234)
- 1 `internal void RegisterForTesting(...)` (line 137)
- 1 `internal bool TryCompletePending(...)` (line 147)
- 1 `public Task AppendBatchAsync(...)` (line 155) — 60 LoC, largest method
- 1 `[RelayCommand] private void Clear()` (line 219)
- 1 `private void ApplyHighlight()` (line 236)
- 1 `public IReadOnlyList<MessageIdStat> GetMessageIdStats(int topN)` (line 259)
- 1 `internal static string FormatHexWithSpaces(...)` (line 283)
- 1 `[RelayCommand] private void ExportCsv()` (line 309) — 54 LoC
- 1 `internal static string CsvEscape(string)` (line 368)

Threshold per `automotive-coding-standards-file-size.md`: 800 LoC ceiling. TraceViewModel at **48.0%** of ceiling.

**Zero `[LoggerMessage]` partials anywhere in TraceViewModel** — verified via `grep LoggerMessage src/PeakCan.Host.App/ViewModels/TraceViewModel.cs` returns 0 matches. **This avoids the CS8795 sister-risk observed in W18 PeakCanChannel.**

## Target state (~190 LoC main + 3 partials)

```
src/PeakCan.Host.App/ViewModels/TraceViewModel.cs                          # main file, ~190 LoC after Task 3
src/PeakCan.Host.App/ViewModels/TraceViewModel/                            # NEW directory
  ReceptionFlow.cs                                                          # Task 1 -- AppendBatchAsync + RegisterForTesting + TryCompletePending (~85 LoC)
  HighlightFilterFlow.cs                                                   # Task 2 -- OnHighlightTextChanged + ApplyHighlight + GetMessageIdStats + FormatHexWithSpaces (~80 LoC)
  ExportFlow.cs                                                             # Task 3 -- ExportCsv + CsvEscape (~70 LoC)
docs/superpowers/plans/2026-07-12-trace-view-model-god-class-refactor.md  # NEW in Task 0
docs/release-notes-v3.33.0.md                                               # NEW in Task 4
```

**Net reduction**: 384 → ~190 LoC main file (-194 LoC, -50.5%); total lines roughly preserved (~384 across main + 3 partials + ~25 LoC headers).

## Flow boundaries

### Flow — Main file (state + clear + 0 source-gen LoggerMessages)

**Stays in main**:
- `using` block (lines 1-6)
- Namespace + `MessageIdStat` record (lines 8-15)
- Class xmldoc + outer class declaration (lines 17-53)
- 7 `[ObservableProperty]` backing fields (lines 75-111) — source-gen partial scope rule
- 1 `Entries` collection (line 60)
- 1 `MaxRows` `_maxRows` field (lines 75-76)
- 2 private fields: `_messageCounts` + `_pendingDecode` (lines 114, 121)
- 1 `PendingDecode` public view (line 129)
- 1 `[RelayCommand] Clear()` (lines 218-228) — touches Entries + FilteredCount + TotalFrameCount + _messageCounts + _pendingDecode (cluster keeps together with state)

### Flow A — ReceptionFlow (~85 LoC, NEW)

**Methods**:
- `public Task AppendBatchAsync(IReadOnlyList<CanFrame> batch)` (lines 155-215) — 60 LoC, dispatcher-marshaled append + filter + FIFO trim + register pending-decode
- `internal void RegisterForTesting(TraceEntryKey, TraceEntry)` (lines 137-138) — test-only helper, cross-thread access to `_pendingDecode`
- `internal bool TryCompletePending(TraceEntryKey, out TraceEntry?)` (lines 147-148) — DbcDecodeBackgroundService worker atomic check-and-remove

**Depends on**:
- All 7 `[ObservableProperty]` fields (main, source-gen reads TotalFrameCount + FilterText + ShowErrorsOnly + IsPaused + MaxRows)
- `_messageCounts` Dictionary (main field)
- `_pendingDecode` ConcurrentDictionary (main field)
- `Entries` collection (main)
- `PendingDecode` view (main)
- `CanFrame` + `TraceEntry` + `TraceEntryKey` types (Core)

**Rationale for grouping**: All 3 methods touch the receive pipeline — dispatcher marshaling on `AppendBatchAsync` + the test/worker entry-points into `_pendingDecode` (which `AppendBatchAsync` populates via `RegisterForTesting` + worker drains via `TryCompletePending`). Sister of W16 ReplayViewModel.Loader.partial.cs.

### Flow B — HighlightFilterFlow (~80 LoC, NEW)

**Methods**:
- `partial void OnHighlightTextChanged(string value)` (line 234) — source-gen callback
- `private void ApplyHighlight()` (lines 236-253)
- `public IReadOnlyList<MessageIdStat> GetMessageIdStats(int topN = 20)` (lines 259-273)
- `internal static string FormatHexWithSpaces(ReadOnlySpan<byte> data)` (lines 283-299)

**Depends on**:
- `_messageCounts` Dictionary (main)
- `TotalFrameCount` source-gen property (main)
- `HighlightText` source-gen property (main)
- `Entries` collection (main, for ApplyHighlight iteration)
- `MessageIdStat` record (main, top-level type)
- `TraceEntry` + `CanFrame` types (Core)

**Rationale for grouping**: 3 of 4 methods (ApplyHighlight + GetMessageIdStats + FormatHexWithSpaces) are pure derivation over state — no mutation. The 4th (`OnHighlightTextChanged`) is a source-gen callback that just dispatches to `ApplyHighlight`. Sister of W6 partial cluster (UI-bound formatting + filter derivation).

### Flow C — ExportFlow (~70 LoC, NEW)

**Methods**:
- `[RelayCommand] private void ExportCsv()` (lines 309-362) — 54 LoC, modal SaveFileDialog + Task.Run writer + try/catch
- `internal static string CsvEscape(string field)` (lines 368-383) — RFC 4180 escape

**Depends on**:
- `Entries` collection (main, for snapshot)
- `TraceEntry` type (Core)
- `[RelayCommand]` source-gen emits `ExportCsvCommand` property in main partial (per source-gen scope rule)

**Rationale for grouping**: `ExportCsv` calls `CsvEscape` per-field. `CsvEscape` is `internal static` (tested at line 109 of `TraceViewModelTests`). Both are RFC 4180 / SaveFileDialog cluster — sister of W8 SendViewModel export partial pattern.

## Architecture invariants (per W3-W18 patterns)

1. **Public API unchanged.** Same `[ObservableProperty]` generated properties + `[RelayCommand]` generated commands + `Entries` + `PendingDecode` + 2 `internal` test/worker helpers.
2. **partial-class visibility** works for `[ObservableProperty]` source-gen — backing fields stay in main, generated public properties emitted into main partial, all siblings can read them as ordinary class members.
3. **State ownership preserved**: 7 `[ObservableProperty]` backing fields + Entries + _messageCounts + _pendingDecode + Clear [RelayCommand] stay in main per W16 D3 sister-rule.
4. **Zero `[LoggerMessage]` partials** → no CS8795 sister-risk (W18 R1 lesson does not apply here).
5. **`OnHighlightTextChanged` callback moves to HighlightFilterFlow.cs** — the callback implementation body travels; the source-gen emits the call site in main. Sister of W16 `OnLoopChanged` + `OnCanIdFilterTextChanged` pattern.
6. **`AppendBatchAsync` largest-method stays inline** per W12 D7 + W14 D8 + W18 D5 sister-principle (single dispatcher-marshal flow, one continuous dispatcher.InvokeAsync body).
7. **`Clear` stays in main** — it touches state owned by 3 partials (Entries + _messageCounts + _pendingDecode); sister of W3 R3 + W14 D2 lifecycle-cluster principle.

## Verification

- `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore`: 0 errors, 0 warnings
- `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~TraceViewModel"`: 9/9 tests pass without modification
- `dotnet test --no-restore --nologo -c Debug`: full solution 0 new fails

## Risk notes

- **R1 (low)**: Missing `using` directives in new partial files — Phase 1 explore confirmed `CanFrame` lives in `PeakCan.Host.Core` (already imported in main). Apply W11 R1 lesson: pre-scan source types in each extracted method + add `using` to each partial file's top.
- **R2 (very low)**: LoC formula — per W8.5 D7 CONFIRMED 17-locked. Use W13 T1 2/3 loose-assertion pattern + wc-l-splitlines CONFIRMED lesson.
- **R3 (very low)**: xmldoc-grep test risk — Phase 1 explore confirmed `TraceEntry.cs` + `TraceEntryKey.cs` xmldoc cref refs `TraceViewModel.AppendBatchAsync` + `.PendingDecode` but the cref resolver follows the class type, not the file path. So splitting doesn't break cref links. Sister risk confirmed low.
- **R4 (very low)**: `[ObservableProperty]` source-gen partial scope — backing field + attribute MUST stay together. Confirmed via Phase 1 explore (W16 ReplayViewModel pattern, all 14 [ObservableProperty] fields stay in main).
- **R5 (none)**: CS8795 sister-risk from W18 PeakCanChannel — **N/A** for TraceViewModel (zero `[LoggerMessage]` partials).

## Out of scope (explicit YAGNI)

- **No facade pattern**: W3-W18 CONFIRMED direct partial-class visibility is sufficient.
- **No test changes**: All existing tests stay unmodified.
- **No public/internal API surface change**.
- **No SignalChartViewModel refactor**: W19 scoped to TraceViewModel only. SignalChartViewModel (378 LoC) is a sister candidate but not in W19 scope.
- **No DbcSendViewModel refactor**: W19 scoped to TraceViewModel only. DbcSendViewModel (384 LoC) is a sister candidate but not in W19 scope.

## Tasks remaining (preview for the plan)

1. **Task 1**: Extract Flow A — `ReceptionFlow` (AppendBatchAsync + RegisterForTesting + TryCompletePending, ~85 LoC, largest method).
2. **Task 2**: Extract Flow B — `HighlightFilterFlow` (OnHighlightTextChanged callback + ApplyHighlight + GetMessageIdStats + FormatHexWithSpaces, ~80 LoC).
3. **Task 3**: Extract Flow C — `ExportFlow` (ExportCsv + CsvEscape, ~70 LoC, export pipeline cluster).
4. **Task 4**: Bump version v3.32.0 → v3.33.0 + write release notes (MINOR ship commit).
5. **Task 5**: Tier-3 push + tag + GH release.

Total: 5 tasks, ~4 source commits (T1 + T2 + T3 + ship).

## Decision log

- **D1**: 3 partials (`ReceptionFlow` + `HighlightFilterFlow` + `ExportFlow`). Subdirectory pattern (NOT sibling-with-`.partial.cs`-suffix like W16 — W6/W7/W8/W11/W14/W15/W18 sister-directory-pattern preferred).
- **D2**: 7 `[ObservableProperty]` backing fields stay in main per W16 D3 sister-rule (source-gen partial scope).
- **D3**: Branch name `feature/w19-trace-view-model-god-class`.
- **D4**: Order tasks: **A (ReceptionFlow, largest 85 LoC) → B (HighlightFilterFlow, ~80 LoC) → C (ExportFlow, ~70 LoC)** — A first to validate the dispatcher-marshal + cross-thread cluster; B second (UI-bound formatting cluster); C last (export pipeline cluster).
- **D5**: `AppendBatchAsync` 60 LoC stays inline per W12 D7 + W14 D8 + W18 D5 sister-principle (single dispatcher-marshal flow).
- **D6**: `Clear` `[RelayCommand]` stays in main per W3 R3 + W14 D2 lifecycle-cluster principle (touches state owned by 3 partials).
- **D7**: Plan LoC-trajectory-table formula explicitly applied per W8.5 D7 CONFIRMED (17-locked across W12-W18).
- **D8**: 6 sister-lesson-candidates to monitor for 1/3→2/3 or NEW 1/3 promotion during W19 (see plan §"Sister-lesson candidates to monitor").

## Closing milestone context

This is the **15th god-class refactor** in the project (W3-W19 series). TraceViewModel is the **8th App-layer god-class** (after W6/W7/W8/W11/W14/W16) and the **2nd ViewModel with `[ObservableProperty]` source-generator partial split** (sister of W16 ReplayViewModel). Specifically it's `App/ViewModels/TraceViewModel.cs` — WPF ViewModel for the Trace tab in the host UI.

If W19 ships + 9 tests pass + lesson confirmations hold, next steps are W19.5 vault-only PATCH (lesson promotion if any candidates reach 3/3) OR W20 (next candidate: SignalChartViewModel 378 LoC, also App/ViewModels sister of W19).