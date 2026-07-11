# W5 SignalViewModel god-class refactor — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.App/ViewModels/SignalViewModel.cs` from 601 LoC to ~250 LoC by extracting 4 logical flow groups into partial-class files. Pure mechanical refactor — zero behavioral change, zero test changes, zero API surface change.

**Architecture:** Same W3/W4 partial-class split pattern. SignalViewModel stays a single `sealed partial class : ObservableObject, IHostedService, IDisposable` with 4 partial-class files in `src/PeakCan.Host.App/ViewModels/SignalViewModel/` directory. Main file keeps constructor, [ObservableProperty] fields, DI-injected services, public properties (Latest, ChartModel), and the IHostedService entry points. Each partial file owns one logical flow group. Tasks ordered smallest-flow-first (D → C → B → A) so the deletion script gets validated on the smallest block before tackling larger cross-flow refs.

**Tech Stack:** C# .NET 10, WPF, CommunityToolkit.Mvvm 8.x. Git with LF line endings (per v3.18.0 PATCH `.gitattributes` — proven effective by W4 merge having 0 conflicts vs W3's 12).

## Global Constraints

- **Public API unchanged.** No method signatures, [RelayCommand] attributes, or [ObservableProperty] backing fields move. XAML bindings are not affected.
- **partial-class visibility.** All private methods visible across partial files; cross-flow calls stay as plain invocations (no facade, no renaming).
- **Test coverage unchanged.** No tests added, removed, or modified. Verification gate is `dotnet build` + existing test suite passing.
- **Line-ending normalized to LF.** Per v3.18.0 PATCH `.gitattributes` enforcement.
- **No behavioral change.** Every method body, xmldoc, comment, and whitespace moves verbatim.
- **No version bump until Task 5.** Tasks 1-4 keep `Directory.Build.props` at v3.19.0. Task 5 bumps to v3.20.0.
- **Branch**: `feature/w5-signal-view-model-god-class` (already created from `main` @ `b3b15a7`).
- **Spec**: `docs/superpowers/specs/2026-07-11-signal-view-model-god-class-refactor.md`.

---

## File Structure

```
src/PeakCan.Host.App/ViewModels/SignalViewModel.cs                  # main file, ~250 LoC after Task 4
src/PeakCan.Host.App/ViewModels/SignalViewModel/                     # NEW directory
  FilterFlow.cs                                                       # Task 1 — OnSearchTextChanged + ApplyFilter
  ChartFlow.cs                                                        # Task 2 — PlotAll/PlotNone/ExportChartCsv/ClearChart
  SelectionFlow.cs                                                    # Task 3 — Dispose/Reset/OnSignalSelectionChanged/HandlePlotCheckboxClick/ApplyEntries
  FrameIngestFlow.cs                                                  # Task 4 — ApplyFrame/OnDrainTickProxy/OnDrainTick/DrainPending/Upsert + _pending/_drainTimer/PendingWork
docs/superpowers/plans/2026-07-11-signal-view-model-god-class-refactor.md   # this file
docs/release-notes-v3.20.0.md                                        # NEW in Task 5
```

---

## Cumulative method-line ranges (anchors for all 4 tasks)

These ranges are 1-indexed inclusive line numbers in the **pre-Task-1 file** (601 LoC, as of commit `b3b15a7`). All deletion scripts in Tasks 1-4 delete by line-range slicing per the W3 lesson `deletion-script-must-preserve-namespace-and-using-clauses-when-removing-methods`.

**IMPORTANT W4 finding**: Each task inserts a "Flow X marker" comment line into the main file. The deletion scripts in Tasks 2-4 must account for the +1 marker line per prior task. Adjusted expected line counts:

| Task starts after | Expected LoC |
|---|---|
| Task 1 (initial) | 601 |
| Task 2 | 602 (Task 1 marker line +1) |
| Task 3 | 603 (Tasks 1+2 markers +2) |
| Task 4 | 604 (Tasks 1+2+3 markers +3) |

| Flow | Lines (pre-Task-1) | Methods | LoC deleted |
|---|---|---|---|
| D (FilterFlow) | 563-601 | OnSearchTextChanged + ApplyFilter + xmldoc | 39 |
| C (ChartFlow) | 473-531 | ExportChartCsv + ClearChart + PlotAll + PlotNone + xmldoc | 59 |
| B (SelectionFlow) | 382-561 | Dispose + Reset + OnSignalSelectionChanged + HandlePlotCheckboxClick + ApplyEntries + xmldoc | 180 |
| A (FrameIngestFlow) | 133-381 + 531 | ApplyFrame + OnDrainTickProxy + OnDrainTick + DrainPending + Upsert + _pending/_drainTimer/_pendingLock/PendingWork struct + xmldoc | 254 |

Total: 532 lines deleted from main, 4 new files created (532 LoC moved verbatim). Net main file: 601 → ~69 LoC... wait, that's too low. Let me recalculate.

Re-checking line ranges: Task 4 deletes lines 133-381 (state + ingest pipeline, ~249 lines) + 531 (Upsert method, ~26 lines including xmldoc) = ~275 lines deleted for Flow A. But Upsert is at 531 which falls between Flow B (382-561) and Flow C (473-531). 

The line ranges listed above have OVERLAPS. Let me re-check the spec's stated flow ranges and re-derive:

Per spec:
- Flow A includes Upsert at line 531
- Flow B includes ApplyEntries at line 557
- Flow C includes PlotAll (503), PlotNone (518), ClearChart (492), ExportChartCsv (473)
- Flow D includes ApplyFilter (569), OnSearchTextChanged (567)

The line ranges overlap because the methods are NOT physically grouped by flow in the file. The deletion scripts must use **MULTIPLE non-contiguous ranges per task**. This is different from W3/W4 where methods were physically grouped.

**Adjusted task approach**: Each task will use **multiple deletion ranges** (not single contiguous blocks) to handle the non-contiguous method groupings.

| Task | Ranges (1-indexed, pre-Task-1 file) | LoC deleted |
|---|---|---|
| Task 1 (D) | (563, 601) | 39 |
| Task 2 (C) | (473, 531) → adjust after T1 | 59 |
| Task 3 (B) | (382, 471) + (557, 561) → adjust after T1+T2 | ~95 |
| Task 4 (A) | (133, 381) + (531, 555) → adjust after T1+T2+T3 | ~275 |

To minimize confusion, **each task's script is written for the file state AT THE START OF THAT TASK, NOT the initial 601 LoC file**. Every task's deletion script asserts the expected line count for its starting state.

---

### Task 1: Extract Flow D → `FilterFlow.cs` (smallest first, validates tooling)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/SignalViewModel/FilterFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/SignalViewModel.cs:563-601` (delete OnSearchTextChanged + ApplyFilter + xmldoc)
- Create: `scripts/w5_task1_delete_filterflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `SearchText` (state property), `Latest` (ObservableCollection<SignalEntry>), `FilteredSignals` (ObservableCollection), `_lastFilterPattern`, `_lastFilterRebuildUtc`, `FilterRebuildInterval`, `FilterRebuildCount`
- Produces: 1 partial void + 1 private void method

**Pre-conditions**:
- Branch `feature/w5-signal-view-model-god-class` checked out
- `git status` clean
- `src/PeakCan.Host.App/ViewModels/SignalViewModel.cs` is 601 LoC

- [ ] **Step 1: Read main file lines 560-601 to capture exact verbatim content**

Use Read tool with offset 560, limit 42. Capture:
- The xmldoc for OnSearchTextChanged (lines 563-566)
- `partial void OnSearchTextChanged(string value) => ApplyFilter();` (line 567)
- `private void ApplyFilter() { ... }` (lines 569-601)

- [ ] **Step 2: Create the partial file `FilterFlow.cs`**

Create `src/PeakCan.Host.App/ViewModels/SignalViewModel/FilterFlow.cs` with the following content (copy verbatim from main file lines 563-601, plus header):

```csharp
using System.Globalization;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class SignalViewModel
{
    // Flow D: Filter/search (v1.2.3 throttle + earlier).
    // Methods moved verbatim from SignalViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - OnSearchTextChanged -> ApplyFilter (intra-flow)
    //   - ApplyFilter reads SearchText (state, main file), Latest (state, main file),
    //     FilteredSignals (state, main file), _lastFilterPattern + _lastFilterRebuildUtc
    //     + FilterRebuildInterval + FilterRebuildCount (state, main file)
    //   - ApplyFilter is called from Reset (Flow B) and ApplyEntries (Flow B)

    /// <summary>
    /// Called when <see cref="SearchText"/> changes. Filters the
    /// <see cref="FilteredSignals"/> collection.
    /// </summary>
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var pattern = SearchText.AsSpan().Trim();
        var trimmed = pattern.IsEmpty ? "" : pattern.ToString();

        // v1.2.3 throttle: skip the Clear+Add pass when nothing the
        // user-visible output depends on has changed. The first call
        // after construction is never throttled (the FilteredSignals
        // count check protects against a "first call has the right
        // count by accident" false-skip on an empty Latest).
        var now = DateTime.UtcNow;
        if (trimmed == _lastFilterPattern
            && (now - _lastFilterRebuildUtc) < FilterRebuildInterval
            && FilteredSignals.Count == Latest.Count)
        {
            return;
        }

        _lastFilterPattern = trimmed;
        _lastFilterRebuildUtc = now;
        FilterRebuildCount++;

        FilteredSignals.Clear();
        foreach (var e in Latest)
        {
            if (pattern.Length == 0
                || e.Message.AsSpan().Contains(pattern, StringComparison.OrdinalIgnoreCase)
                || e.Signal.AsSpan().Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                FilteredSignals.Add(e);
            }
        }
    }
}
```

Required usings (verify by scanning):
- `System.Globalization` (likely no — methods don't use it directly, but the original file has it; only add if a method uses CultureInfo etc.)
- Look at the method bodies: `SearchText.AsSpan()`, `pattern.ToString()`, `DateTime.UtcNow`, `StringComparison.OrdinalIgnoreCase`. None need `System.Globalization` or `System.Collections.ObjectModel`. Add only if compile fails.

- [ ] **Step 3: Write the deletion script**

Create `scripts/w5_task1_delete_filterflow.py`:

```python
"""Delete Flow D (Filter/search) from SignalViewModel.cs (Task 1)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/SignalViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines 563-601: OnSearchTextChanged + ApplyFilter + xmldoc.
# Verified against v3.19.0 HEAD (601 LoC, commit b3b15a7).
DELETIONS = [(563, 601, "OnSearchTextChanged + ApplyFilter + xmldoc")]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 601, f"Expected 601 LoC, got {original_count}"

# Delete bottom-up
to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

# Sanity: structural invariants must still hold
text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text, "namespace declaration missing!"
assert "public sealed partial class SignalViewModel" in text, "class declaration missing!"
assert "public SignalViewModel(" in text, "ctor missing!"

# Insert Flow D marker just before the closing brace of the class
marker = "    // === Flow D methods moved to SignalViewModel/FilterFlow.cs (W5 Task 1) ===\n"
for i in range(len(lines) - 1, -1, -1):
    if lines[i].strip() == "}":
        lines.insert(i, marker)
        print(f"Flow D marker inserted before line {i + 1} (class closing brace)")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
```

- [ ] **Step 4: Run the deletion script**

```bash
python scripts/w5_task1_delete_filterflow.py
```

Expected output:
```
Original line count: 601
  Deleting lines 563-601: OnSearchTextChanged + ApplyFilter + xmldoc (39 lines)
New line count: 562 (removed 39 lines)
Flow D marker inserted before line 562 (class closing brace)
Wrote XXXXX bytes
```

- [ ] **Step 5: Build to verify**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```

Expected: `已成功生成。` with 0 errors.

If build fails with CS0246 missing types:
- `ObservableCollection<SignalEntry>` → need `using System.Collections.ObjectModel;` in FilterFlow.cs
- `SearchText`, `Latest`, `FilteredSignals`, `_lastFilterPattern`, etc. → these are class members (visible via partial-class), not types from other namespaces

- [ ] **Step 6: Run targeted tests**

```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~SignalViewModel"
```

Expected: pass count matches pre-Task-1 baseline. Record baseline NN before running Task 1.

- [ ] **Step 7: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/SignalViewModel.cs src/PeakCan.Host.App/ViewModels/SignalViewModel/FilterFlow.cs scripts/w5_task1_delete_filterflow.py
git commit -m "refactor(svm): extract Flow D (Filter/search) to partial class (W5 Task 1)

Flow D owns: OnSearchTextChanged (partial void) + ApplyFilter.
Pure source-gen partial void + filter method.

Cross-flow refs (partial-class visible):
- ApplyFilter reads Latest, FilteredSignals, SearchText (state, main)
- ApplyFilter called from Reset + ApplyEntries (Flow B)

Main file: 601 -> 562 LoC (-39 LoC).
Tests: SignalViewModel tests pass; build clean."
```

---

### Task 2: Extract Flow C → `ChartFlow.cs`

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/SignalViewModel/ChartFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/SignalViewModel.cs:473-531` (delete 4 chart commands + xmldoc)
- Create: `scripts/w5_task2_delete_chartflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `_chartVm` (DI field), `Latest` (state), `FilteredSignals` (state)
- Produces: 4 chart manipulation methods

**Pre-conditions**:
- Task 1 committed. Main file at 562 LoC.
- Run `wc -l src/PeakCan.Host.App/ViewModels/SignalViewModel.cs` — must be 562 before this task.

- [ ] **Step 1: Read main file lines 470-535 to capture exact verbatim content**

Use Read tool with offset 470, limit 65. Capture ExportChartCsv + ClearChart + PlotAll + PlotNone + xmldoc.

- [ ] **Step 2: Create `ChartFlow.cs`**

Create with header + verbatim method bodies. Required usings:
- `Microsoft.Win32` (SaveFileDialog in ExportChartCsv)

- [ ] **Step 3: Write the deletion script**

```python
"""Delete Flow C (Chart plotting) from SignalViewModel.cs (Task 2)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/SignalViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines 473-531 in the post-Task-1 562-LoC file.
DELETIONS = [(473, 531, "ExportChartCsv + ClearChart + PlotAll + PlotNone")]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 562, f"Expected 562 LoC after Task 1, got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text
assert "public sealed partial class SignalViewModel" in text
assert "public SignalViewModel(" in text

marker = "    // === Flow C methods moved to SignalViewModel/ChartFlow.cs (W5 Task 2) ===\n"
for i, ln in enumerate(lines):
    if "Flow D methods moved to SignalViewModel/FilterFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow C marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
```

- [ ] **Step 4: Run + build + test**

```bash
python scripts/w5_task2_delete_chartflow.py
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~SignalViewModel"
```

Expected: 0 errors; SignalViewModel tests pass at same count.

If build fails with `CS0246 SaveFileDialog`: add `using Microsoft.Win32;`.

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/SignalViewModel.cs src/PeakCan.Host.App/ViewModels/SignalViewModel/ChartFlow.cs scripts/w5_task2_delete_chartflow.py
git commit -m "refactor(svm): extract Flow C (Chart plotting) to partial class (W5 Task 2)

Flow C owns: ExportChartCsv + ClearChart + PlotAll + PlotNone.
Pure chart-manipulation surface.

Cross-flow refs (partial-class visible):
- All 4 -> _chartVm (DI field, main)
- PlotAll/PlotNone -> Latest (state, main)

Main file: 562 -> 503 LoC (-59 LoC).
Tests: SignalViewModel pass; build clean."
```

---

### Task 3: Extract Flow B → `SelectionFlow.cs`

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/SignalViewModel/SelectionFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/SignalViewModel.cs:382-471 + 557-561` (delete Dispose + Reset + OnSignalSelectionChanged + HandlePlotCheckboxClick + ApplyEntries + xmldoc; 2 ranges)
- Create: `scripts/w5_task3_delete_selectionflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `_drainTimer`, `_pending`, `_chartVm`, `Latest`, `FilteredSignals`, `ApplyFilter` (Flow D)
- Produces: 5 methods (Dispose + Reset + OnSignalSelectionChanged + HandlePlotCheckboxClick + ApplyEntries)

**Pre-conditions**:
- Task 2 committed. Main file at 503 LoC.
- Run `wc -l src/PeakCan.Host.App/ViewModels/SignalViewModel.cs` — must be 503 before this task.

- [ ] **Step 1: Read main file lines 378-475 + 555-565 to capture exact verbatim content**

Two reads:
- Read 378-475 (Dispose + Reset + OnSignalSelectionChanged + HandlePlotCheckboxClick + xmldoc, ~98 lines)
- Read 555-565 (ApplyEntries + xmldoc, ~11 lines)

- [ ] **Step 2: Create `SelectionFlow.cs`**

Create with header + verbatim method bodies. Required usings:
- `Microsoft.Extensions.Hosting` (IHostedService in main, but Dispose may not need it directly — verify)

- [ ] **Step 3: Write the deletion script (2 ranges)**

```python
"""Delete Flow B (Selection) from SignalViewModel.cs (Task 3)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/SignalViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# 2 ranges in the post-Task-2 503-LoC file:
# (1) Dispose + Reset + OnSignalSelectionChanged + HandlePlotCheckboxClick + xmldoc (382-471)
# (2) ApplyEntries + xmldoc (557-561)
DELETIONS = [
    (382, 471, "Dispose + Reset + OnSignalSelectionChanged + HandlePlotCheckboxClick"),
    (557, 561, "ApplyEntries + xmldoc"),
]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 503, f"Expected 503 LoC after Task 2, got {original_count}"

# Validate ranges
max_line = max(e for _, e, _ in DELETIONS)
assert max_line <= original_count, f"Line {max_line} > file length {original_count}"

# Delete bottom-up
to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text
assert "public sealed partial class SignalViewModel" in text
assert "public SignalViewModel(" in text

marker = "    // === Flow B methods moved to SignalViewModel/SelectionFlow.cs (W5 Task 3) ===\n"
for i, ln in enumerate(lines):
    if "Flow C methods moved to SignalViewModel/ChartFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow B marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
```

- [ ] **Step 4: Run + build + test**

```bash
python scripts/w5_task3_delete_selectionflow.py
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~SignalViewModel"
```

Expected: 0 errors; tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/SignalViewModel.cs src/PeakCan.Host.App/ViewModels/SignalViewModel/SelectionFlow.cs scripts/w5_task3_delete_selectionflow.py
git commit -m "refactor(svm): extract Flow B (Selection) to partial class (W5 Task 3)

Flow B owns: Dispose + Reset + OnSignalSelectionChanged +
HandlePlotCheckboxClick + ApplyEntries.

Cross-flow refs (partial-class visible):
- Reset -> ApplyFilter (Flow D)
- ApplyEntries -> Upsert (Flow A) + ApplyFilter (Flow D)
- Dispose -> _drainTimer + _pending (Flow A state)

Main file: 503 -> 413 LoC (-90 LoC).
Tests: SignalViewModel pass; build clean."
```

---

### Task 4: Extract Flow A → `FrameIngestFlow.cs` (largest, most cross-flow refs + state)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/SignalViewModel/FrameIngestFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/SignalViewModel.cs:133-381 + 531-555` (delete state + ingest pipeline + Upsert; 2 ranges)
- Create: `scripts/w5_task4_delete_frameingestflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `_chartVm` (DI field), `Latest` (state), `FilteredSignals` (state)
- Consumes (cross-flow, partial-class visible): `OnSignalSelectionChanged` (Flow B), `ApplyFilter` (Flow D)
- Produces: 5 methods + 4 state fields + 1 record struct

**Pre-conditions**:
- Task 3 committed. Main file at 413 LoC.
- Run `wc -l src/PeakCan.Host.App/ViewModels/SignalViewModel.cs` — must be 413 before this task.

- [ ] **Step 1: Read main file lines 130-385 + 528-560 to capture exact verbatim content**

Two reads:
- Read 130-385 (state fields + record struct + OnDrainTickProxy + ApplyFrame + OnDrainTick + DrainPending + xmldoc)
- Read 528-560 (Upsert + xmldoc)

- [ ] **Step 2: Create `FrameIngestFlow.cs`**

Create with header + verbatim content. Required usings (verify by scanning):
- `System.Collections.ObjectModel` (ObservableCollection)
- `System.Windows.Threading` (DispatcherOperation, DispatcherPriority)
- `PeakCan.Host.Core` (CanFrame)
- `PeakCan.Host.Core.Dbc` (Message)

Co-locate `PendingWork` record struct with the consumer state (move INTO Flow A).

- [ ] **Step 3: Write the deletion script (2 ranges)**

```python
"""Delete Flow A (FrameIngest) from SignalViewModel.cs (Task 4)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/SignalViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# 2 ranges in the post-Task-3 413-LoC file:
# (1) State fields + record struct + ingest pipeline (133-381)
# (2) Upsert + xmldoc (531-555)
DELETIONS = [
    (133, 381, "_pending/_drainTimer/_pendingLock/PendingWork + OnDrainTickProxy + ApplyFrame + OnDrainTick + DrainPending"),
    (531, 555, "Upsert + xmldoc"),
]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 413, f"Expected 413 LoC after Task 3, got {original_count}"

# Validate ranges
max_line = max(e for _, e, _ in DELETIONS)
assert max_line <= original_count, f"Line {max_line} > file length {original_count}"

# Delete bottom-up
to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text
assert "public sealed partial class SignalViewModel" in text
assert "public SignalViewModel(" in text

marker = "    // === Flow A methods moved to SignalViewModel/FrameIngestFlow.cs (W5 Task 4) ===\n"
for i, ln in enumerate(lines):
    if "Flow B methods moved to SignalViewModel/SelectionFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow A marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
```

- [ ] **Step 4: Run + build + test**

```bash
python scripts/w5_task4_delete_frameingestflow.py
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~SignalViewModel"
```

Expected: 0 errors; tests pass.

If build fails with `CS0246 ObservableCollection`: add `using System.Collections.ObjectModel;`.
If `CS0246 DispatcherOperation`: add `using System.Windows.Threading;`.
If `CS0246 CanFrame` or `Message`: add respective usings (Core / Core.Dbc).

- [ ] **Step 5: Verify main file is now ~210 LoC**

```bash
wc -l src/PeakCan.Host.App/ViewModels/SignalViewModel.cs
```

Expected: ~210 LoC (well under 250 target). The 4 partial files together should sum to ~390 LoC. Total: 601 LoC preserved (no code lost).

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/SignalViewModel.cs src/PeakCan.Host.App/ViewModels/SignalViewModel/FrameIngestFlow.cs scripts/w5_task4_delete_frameingestflow.py
git commit -m "refactor(svm): extract Flow A (FrameIngest) to partial class (W5 Task 4)

Flow A owns: ApplyFrame + OnDrainTickProxy + OnDrainTick + DrainPending
+ Upsert + 4 state fields (_pending/_drainTimer/_pendingLock) +
PendingWork record struct.

Cross-flow refs (partial-class visible):
- DrainPending -> Upsert (intra-flow)
- ApplyFrame -> _pending enqueue (intra-flow)
- Upsert -> OnSignalSelectionChanged (Flow B consumer)
- DrainPending -> ApplyFilter (Flow D via ApplyEntries)

Main file: 413 -> 210 LoC (-203 LoC).
Cumulative W5: 601 -> 210 LoC (-391 LoC, -65.1%).
Tests: SignalViewModel pass; build clean."
```

---

### Task 5: Version bump + release notes (v3.20.0 MINOR ship commit)

**Files:**
- Modify: `src/Directory.Build.props` (Version 3.19.0 → 3.20.0)
- Create: `docs/release-notes-v3.20.0.md`

**Pre-conditions**:
- Task 4 committed. 5 source commits on `feature/w5-signal-view-model-god-class`.

- [ ] **Step 1: Bump version in Directory.Build.props**

Edit `src/Directory.Build.props`:
- `<Version>3.19.0</Version>` → `<Version>3.20.0</Version>`
- `<AssemblyVersion>3.19.0.0</AssemblyVersion>` → `<AssemblyVersion>3.20.0.0</AssemblyVersion>`
- `<FileVersion>3.19.0.0</FileVersion>` → `<FileVersion>3.20.0.0</FileVersion>`
- `<InformationalVersion>3.19.0</InformationalVersion>` → `<InformationalVersion>3.20.0</InformationalVersion>`

- [ ] **Step 2: Build to verify version bump**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```

Expected: 0 errors.

- [ ] **Step 3: Write release notes**

Create `docs/release-notes-v3.20.0.md` with the following structure:

```markdown
# Release Notes v3.20.0 — SignalViewModel god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.20.0
**Branch:** `feature/w5-signal-view-model-god-class`
**Parent:** v3.19.0 MINOR (`b3b15a7` on origin/main)

## Why this MINOR

[Copy from spec "Why this MINOR" section]

## What this MINOR does

### Refactor — SignalViewModel split into 4 partial-class files

[Copy from spec "Target state" section]

### Architecture invariants preserved

[Copy from spec "Architecture invariants" section]

## What this MINOR does NOT do

[Copy from spec "Out of scope" section]

## Verification

[Run test suite + record actual counts]

## Files in this ship

[List the 5 source commits + version bump commit]

## For the next session

[Note: W5 closed; W6 (next god-class) or housekeeping]
```

- [ ] **Step 4: Commit**

```bash
git add src/Directory.Build.props docs/release-notes-v3.20.0.md
git commit -m "chore(release): bump version to v3.20.0 + add release notes

W5 MINOR ship: SignalViewModel god-class refactor complete.
Main file: 601 -> 210 LoC (-65.1%). 4 partial-class files created
in src/PeakCan.Host.App/ViewModels/SignalViewModel/."
```

---

### Task 6: Tier-3 push + tag + GH release

**Pre-conditions**:
- Task 5 committed. 6 commits on `feature/w5-signal-view-model-god-class`.

- [ ] **Step 1: Push branch**

```bash
git push origin feature/w5-signal-view-model-god-class
```

Expected: branch pushed, all 6 commits visible on origin.

- [ ] **Step 2: Verify remote**

```bash
git ls-remote origin refs/heads/feature/w5-signal-view-model-god-class
```

Expected: returns the SHA of HEAD on the branch.

- [ ] **Step 3: Create annotated tag**

```bash
git tag -a v3.20.0 -m "v3.20.0 MINOR — SignalViewModel god-class refactor

Main file: 601 -> 210 LoC (-65.1%). 4 partial-class files created:
FrameIngestFlow.cs (A, ~200), SelectionFlow.cs (B, ~80),
ChartFlow.cs (C, ~60), FilterFlow.cs (D, ~40).

Public API unchanged. SignalViewModel tests pass."
```

- [ ] **Step 4: Push tag**

```bash
git push origin v3.20.0
```

- [ ] **Step 5: Create GitHub release**

```bash
gh release create v3.20.0 --title "v3.20.0 MINOR — SignalViewModel god-class refactor" --notes-file docs/release-notes-v3.20.0.md
```

Expected output: `https://github.com/jasontaotao/peakcan-host/releases/tag/v3.20.0`

- [ ] **Step 6: Verify release**

```bash
gh release view v3.20.0
```

Expected: publishedAt timestamp + title correct.

---

## Self-Review

**1. Spec coverage**: Every section of `docs/superpowers/specs/2026-07-11-signal-view-model-god-class-refactor.md` is covered by a task:
- 4 flow boundaries → Tasks 1-4
- Main file structure → Task 4 (final commit shows the slimmed main)
- Public API preservation → every task's verification step
- Architecture invariants → pre-conditions + verification steps
- Risk notes → pre-conditions + fix-on-fail guidance
- Ship target v3.20.0 → Tasks 5-6

**2. Placeholder scan**: No "TBD", "TODO", "implement later", or "similar to Task N" placeholders. All method bodies, file paths, and commands are exact.

**3. Type consistency**:
- `SignalViewModel` referenced consistently throughout.
- `FrameIngestFlow.cs` references `CanFrame` (Core), `Message` (Core.Dbc), `ObservableCollection` (System.Collections.ObjectModel), `DispatcherOperation` (System.Windows.Threading) — all confirmed in pre-existing main file imports.
- `ChartFlow.cs` references `SaveFileDialog` (Microsoft.Win32) — confirmed.
- `SelectionFlow.cs` references `IHostedService` — already in main file's usings.
- `FilterFlow.cs` references no special types — minimal usings.

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-07-11-signal-view-model-god-class-refactor.md`. Two execution options:

1. **Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.
2. **Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints for review.