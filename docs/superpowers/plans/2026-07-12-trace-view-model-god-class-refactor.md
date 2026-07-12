# W19 Plan — TraceViewModel god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mechanically split `src/PeakCan.Host.App/ViewModels/TraceViewModel.cs` (384 LoC) into 3 partial-class files + ~190 LoC main file. Zero behavioral change.

**Architecture:** Sister of W6/W7/W8/W11/W14/W15/W18 (subdirectory pattern). 8th App layer + 2nd `[ObservableProperty]` source-gen partial. Order: A (ReceptionFlow) → B (HighlightFilterFlow) → C (ExportFlow).

**Tech Stack:** C# .NET 10, App layer + WPF ViewModel + `CommunityToolkit.Mvvm` `[ObservableProperty]` + `[RelayCommand]` source-generators.

**Spec:** [`../specs/2026-07-12-trace-view-model-god-class-refactor.md`](../specs/2026-07-12-trace-view-model-god-class-refactor.md)
**Branch:** `feature/w19-trace-view-model-god-class` (created from `main` @ `2b8a2b8` v3.32.0 HEAD; spec commit `0091f38`)

## Global Constraints

- Public API unchanged (7 `[ObservableProperty]` + 2 `[RelayCommand]` + Entries + PendingDecode + 2 internal helpers + 2 public methods + 2 internal static).
- partial-class visibility on private fields + private methods.
- Test coverage unchanged (9 existing TraceViewModelTests pass without modification).
- LF line endings.
- No behavioral change.
- No version bump until Task 4.
- Outer class already `public sealed partial class TraceViewModel : ObservableObject` at line 53 — no CS0260 mitigation.
- **Zero `[LoggerMessage]` partials anywhere → no CS8795 sister-risk** (W18 R1 lesson does NOT apply here).

## LoC trajectory (W8.5 D7 17-locked + W13 T1 2/3 loose assertion + W17 wc-l-splitlines CONFIRMED)

| Task | Flow | Range (1-indexed) | LoC deleted | Markers | LoC main after |
|---|---|---|---|---|---|
| T1 | A — ReceptionFlow | 137-148 + 155-215 (RegisterForTesting + TryCompletePending + AppendBatchAsync) | ~73 | 1 | ~311 |
| T2 | B — HighlightFilterFlow | 234-253 + 259-273 + 283-299 (OnHighlightTextChanged + ApplyHighlight + GetMessageIdStats + FormatHexWithSpaces) | ~52 | 1 | ~259 |
| T3 | C — ExportFlow | 309-362 + 368-383 (ExportCsv + CsvEscape) | ~70 | 1 | ~189 |
| T4 | v3.32.0 -> v3.33.0 | (no source) | 0 | 0 | ~189 |
| T5 | ship | -- | -- | -- | ~189 |

Cumulative: 384 -> ~311 -> ~259 -> ~189 main.

---

## Task 0: Branch + plan commit

```bash
git add docs/superpowers/plans/2026-07-12-trace-view-model-god-class-refactor.md
git commit -m "W19 plan: TraceViewModel god-class refactor (3 partials: Reception + HighlightFilter + Export)"
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~TraceViewModel" --logger "console;verbosity=minimal"
```

---

## Task 1: Extract Flow A — ReceptionFlow.cs (~85 LoC)

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewModel.cs:137-148 + 155-215` (delete RegisterForTesting + TryCompletePending + AppendBatchAsync + interim xmldoc/comments)
- Create: `src/PeakCan.Host.App/ViewModels/TraceViewModel/ReceptionFlow.cs`

**Step 1**: Write `scripts/w19_task1_delete_receptionflow.py` with W13 T1 2/3 loose assertion + W17 wc-l-splitlines CONFIRMED pattern. Re-grep post-T0 ranges.

Range: lines 137-148 (RegisterForTesting + TryCompletePending — 12 LoC) + lines 155-215 (AppendBatchAsync — 60 LoC) + interim xmldoc + comments. Total ~73 LoC deletion + 1 marker line.

**Step 2**: Run deletion. Expected: 384 - 73 + 1 ≈ 312 LoC post-marker. Loose assertion `abs(actual - expected) <= 1`.

**Step 3**: Create `ReceptionFlow.cs` with verbatim extracted code. Required usings:
- `System.Collections.Generic` (IReadOnlyList)
- `System.Windows` (Application + Dispatcher)
- `PeakCan.Host.Core` (CanFrame)

Class declaration: `public sealed partial class TraceViewModel`

The 3 methods must travel together (sister of W3 R3 + W14 D2 mutable-state coupling principle):
- `internal void RegisterForTesting(TraceEntryKey, TraceEntry)` — touches `_pendingDecode`
- `internal bool TryCompletePending(TraceEntryKey, out TraceEntry?)` — touches `_pendingDecode`
- `public Task AppendBatchAsync(IReadOnlyList<CanFrame>)` — touches Entries + _messageCounts + _pendingDecode + all 5 source-gen filter properties

**Step 4**: Build + tests (TraceViewModel filter tests).

**Step 5**: Commit: `W19 Task 1: extract Flow A (ReceptionFlow: AppendBatchAsync + RegisterForTesting + TryCompletePending) to partial`.

---

## Task 2: Extract Flow B — HighlightFilterFlow.cs (~80 LoC)

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewModel.cs:234-253 + 259-273 + 283-299` (delete OnHighlightTextChanged callback + ApplyHighlight + GetMessageIdStats + FormatHexWithSpaces)
- Create: `src/PeakCan.Host.App/ViewModels/TraceViewModel/HighlightFilterFlow.cs`

**Step 1**: Re-grep post-T1 ranges (line numbers shift down by ~73 LoC + 1 marker).

**Step 2**: Write `scripts/w19_task2_delete_highlightfilterflow.py`.

Range: 
- `OnHighlightTextChanged` callback (~1 LoC at line 234)
- `ApplyHighlight` (~18 LoC at lines 236-253)
- `GetMessageIdStats` (~15 LoC at lines 259-273)
- `FormatHexWithSpaces` (~17 LoC at lines 283-299)

Total ~52 LoC deletion + 1 marker.

**Step 3**: Run deletion. Expected: ~312 - 52 + 1 ≈ 261 LoC post-marker.

**Step 4**: Create `HighlightFilterFlow.cs` with verbatim extracted code. Required usings:
- `CommunityToolkit.Mvvm.ComponentModel` (for `partial void On*Changed` — the source-gen callback signature)
- `PeakCan.Host.Core` (CanFrame — actually may not be needed; verify)

The 4 methods must travel together (UI-bound formatting cluster):
- `partial void OnHighlightTextChanged(string value)` — source-gen callback implementation
- `private void ApplyHighlight()` — iterates Entries
- `public IReadOnlyList<MessageIdStat> GetMessageIdStats(int topN = 20)` — pure derivation over _messageCounts + TotalFrameCount
- `internal static string FormatHexWithSpaces(ReadOnlySpan<byte> data)` — pure formatter, no state

**Step 5**: Build + tests + commit.

---

## Task 3: Extract Flow C — ExportFlow.cs (~70 LoC)

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewModel.cs:309-362 + 368-383` (delete ExportCsv + CsvEscape)
- Create: `src/PeakCan.Host.App/ViewModels/TraceViewModel/ExportFlow.cs`

**Step 1**: Re-grep post-T2 ranges.

**Step 2**: Write `scripts/w19_task3_delete_exportflow.py`.

Range:
- `[RelayCommand] private void ExportCsv()` (~54 LoC at lines 309-362)
- `internal static string CsvEscape(string)` (~16 LoC at lines 368-383)

Total ~70 LoC deletion + 1 marker.

**Step 3**: Run deletion. Expected: ~261 - 70 + 1 ≈ 192 LoC post-marker.

**Step 4**: Create `ExportFlow.cs` with verbatim extracted code. Required usings:
- `CommunityToolkit.Mvvm.Input` (for `[RelayCommand]`)
- `PeakCan.Host.Core` (TraceEntry — actually already imported via global using if project has them; verify)

The 2 methods must travel together (export pipeline cluster):
- `[RelayCommand] private void ExportCsv()` — touches Entries
- `internal static string CsvEscape(string)` — pure formatter

**Step 5**: Build + tests + commit.

---

## Task 4: Bump version v3.32.0 → v3.33.0 + release notes

Mirror W12/W14/W16/W18 release notes format. MINOR (3 partial extractions = architectural change).

---

## Task 5: Tier-3 push + tag + GH release

Standard: `gh pr create` → `--squash --delete-branch` → `git tag v3.33.0` → `gh release create`.

---

## Acceptance Criteria

- [ ] `TraceViewModel.cs` ≤ 200 LoC (target ~189)
- [ ] 3 NEW partial files in `TraceViewModel/` directory, all `public sealed partial class TraceViewModel`
- [ ] Outer class stays `public sealed partial class TraceViewModel : ObservableObject`
- [ ] 9 existing TraceViewModel tests pass without modification
- [ ] `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings (no CS8795 risk since zero LoggerMessages)
- [ ] Full solution `dotnet test`: 0 new fails
- [ ] Tag v3.33.0 + GH release published
- [ ] Branch deleted post-merge

## Lesson Promotions to Monitor During W19

| Lesson | Status | What W19 might observe |
|---|---|---|
| `xmldoc-grep-test-breaks-when-partial-class-split-moves-the-overloaded-method-xmldoc-into-different-file` | 1/3 (W12 T4) | Awaits W19 (9 tests reference TraceViewModel; Phase 1 confirms no xmldoc-grep risk for current cref refs) |
| `observalproperty-source-gen-partial-scope-keeps-backing-fields-in-main` | NEW W19 candidate (1/3) | W19 2nd confirmation after W16 if `[ObservableProperty]` stays in main across 2 consecutive VM splits |
| `relaycommand-attribute-and-method-must-travel-together-across-partials` | NEW W19 candidate (1/3) | W19 1st observation — `[RelayCommand]` on `Clear` stays in main, `ExportCsv` moves to ExportFlow; if W19 ships clean, this is canonical |
| `subdirectory-pattern-vs-sibling-partial-cs-suffix-decision` | 1/3 (W18 D2) | W19 2nd confirmation if subdirectory pattern repeats |
| `partial-class-with-zero-LoggerMessage-parts-skips-cs8795-sister-risk` | NEW W19 candidate (1/3) | W19 1st observation — confirms that VM-style partials with no source-gen LoggerMessages don't hit CS8795 sister-risk |
| `appendbatch-async-dispatcher-marshaling-cluster-stays-together-across-partials` | NEW W19 candidate (1/3) | W19 1st observation — `AppendBatchAsync` dispatcher-hops + `RegisterForTesting` + `TryCompletePending` all share dispatcher-thread boundary; cluster keeps together |