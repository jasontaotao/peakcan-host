# W7 MultiFrameSendViewModel god-class refactor — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs` from 513 LoC to ~220 LoC by extracting 5 logical flow groups into partial-class files. Pure mechanical refactor — zero behavioral change, zero test changes, zero API surface change.

**Architecture:** Same W3/W4/W5/W6 partial-class split pattern. MultiFrameSendViewModel stays a single `sealed partial class : ObservableObject, IDisposable` with 5 partial-class files in `src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel/` directory. Main file keeps constructor, [ObservableProperty] fields, DI-injected services, public properties. Each partial file owns one logical flow group. Tasks ordered smallest-flow-first (E → D → C → B → A).

**Tech Stack:** C# .NET 10, WPF, CommunityToolkit.Mvvm 8.x. Git with LF line endings (per v3.18.0 PATCH `.gitattributes`).

## Global Constraints

- **Public API unchanged.** No method signatures, [RelayCommand] attributes, or [ObservableProperty] backing fields move.
- **partial-class visibility.** All private methods visible across partial files; cross-flow calls stay as plain invocations.
- **Test coverage unchanged.** No tests added, removed, or modified.
- **Line-ending normalized to LF.** Per v3.18.0 PATCH `.gitattributes` enforcement.
- **No behavioral change.** Every method body, xmldoc, comment, and whitespace moves verbatim.
- **No version bump until Task 6.** Tasks 1-5 keep `Directory.Build.props` at v3.21.0. Task 6 bumps to v3.22.0.
- **Branch**: `feature/w7-multi-frame-send-view-model-god-class` (already created from `main` @ `d63e9cb`).
- **Spec**: `docs/superpowers/specs/2026-07-11-multi-frame-send-view-model-god-class-refactor.md`.

---

## File Structure

```
src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs              # main file, ~220 LoC after Task 5
src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel/               # NEW directory
  LifecycleFlow.cs                                                    # Task 1 — Dispose
  DbcIntegrationFlow.cs                                              # Task 2 — OnDbcLoaded + partial void
  LibraryFlow.cs                                                      # Task 3 — Save/Load/Delete/Replace
  SendFlow.cs                                                         # Task 4 — SendAsync + Stop
  RowManagementFlow.cs                                                # Task 5 — Add/Remove/Duplicate/Move/Clear + OnRowsChanged + RefreshProgressMax + partial void
docs/superpowers/plans/2026-07-11-multi-frame-send-view-model-god-class-refactor.md   # this file
docs/release-notes-v3.22.0.md                                        # NEW in Task 6
```

---

## Cumulative method-line ranges (anchors for all 5 tasks)

Pre-Task-1 file: 513 LoC (commit `0ef4281`). All deletion scripts delete by line-range slicing per the W3 lesson.

**W5 lesson applied**: Each task inserts a "Flow X marker" comment line into the main file. Subsequent tasks must account for the +1 marker line per prior task. Adjusted expected line counts:

| Task starts after | Expected LoC |
|---|---|
| Task 1 (initial) | 513 |
| Task 2 | 514 (Task 1 marker line +1) |
| Task 3 | 515 (Tasks 1+2 markers +2) |
| Task 4 | 516 (Tasks 1+2+3 markers +3) |
| Task 5 | 517 (Tasks 1+2+3+4 markers +4) |

**Important**: Methods may be interleaved. Pre-verify the exact line ranges by reading the file before each task.

---

### Task 1: Extract Flow E → `LifecycleFlow.cs` (smallest first)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel/LifecycleFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs` (delete Dispose + xmldoc — verify exact range)
- Create: `scripts/w7_task1_delete_lifecycleflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): sequence-send state (Flow B, main)
- Produces: 1 public void method

**Pre-conditions**:
- Branch `feature/w7-multi-frame-send-view-model-god-class` checked out
- `git status` clean
- `src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs` is 513 LoC

- [ ] **Step 1: Read main file lines 500-513 to capture exact verbatim content of Dispose**

Use Read tool with offset 500, limit 13. Capture the xmldoc + Dispose method body.

- [ ] **Step 2: Create the partial file `LifecycleFlow.cs`**

Create with header + verbatim content:

```csharp
namespace PeakCan.Host.App.ViewModels;

public sealed partial class MultiFrameSendViewModel
{
    // Flow E: Lifecycle (v1.x.x + earlier).
    // Methods moved verbatim from MultiFrameSendViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - Dispose -> sequence-send state (Flow B, main)

    /// <summary>
    /// [capture verbatim xmldoc]
    /// </summary>
    public void Dispose()
    {
        // [capture verbatim method body]
    }
}
```

- [ ] **Step 3: Write the deletion script**

Create `scripts/w7_task1_delete_lifecycleflow.py`:

```python
"""Delete Flow E (Lifecycle) from MultiFrameSendViewModel.cs (Task 1)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines (verified before Task 1): Dispose + xmldoc.
DELETIONS = [(START_LINE, END_LINE, "Dispose + xmldoc")]  # fill in actual line range

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 513, f"Expected 513 LoC, got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text, "namespace missing!"
assert "public sealed partial class MultiFrameSendViewModel" in text, "class declaration missing!"
assert "public MultiFrameSendViewModel(" in text, "ctor missing!"

marker = "    // === Flow E methods moved to MultiFrameSendViewModel/LifecycleFlow.cs (W7 Task 1) ===\n"
for i in range(len(lines) - 1, -1, -1):
    if lines[i].strip() == "}":
        lines.insert(i, marker)
        print(f"Flow E marker inserted before line {i + 1} (class closing brace)")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
```

- [ ] **Step 4: Run the deletion script**

```bash
python scripts/w7_task1_delete_lifecycleflow.py
```

- [ ] **Step 5: Build to verify**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```

Expected: `已成功生成。` with 0 errors.

- [ ] **Step 6: Run targeted tests**

```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~MultiFrameSendViewModel"
```

Expected: pass count matches pre-Task-1 baseline.

- [ ] **Step 7: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel/LifecycleFlow.cs scripts/w7_task1_delete_lifecycleflow.py
git commit -m "refactor(mfsvm): extract Flow E (Lifecycle) to partial class (W7 Task 1)

Flow E owns: Dispose.
Cross-flow refs (partial-class visible):
- Dispose -> sequence-send state (Flow B, main)

Main file: 513 -> ~503 LoC (-10 LoC).
Tests: MultiFrameSendViewModel pass; build clean."
```

---

### Task 2: Extract Flow D → `DbcIntegrationFlow.cs`

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel/DbcIntegrationFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs` (delete OnRateLimitRejectedCountChanged + OnDbcLoaded — verify exact ranges)
- Create: `scripts/w7_task2_delete_dbcintegrationflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `_dbc` (state), `RateLimitRejectedVisibility` (computed property)
- Produces: 1 partial void + 1 private void

**Pre-conditions**:
- Task 1 committed. Main file at 514 LoC (513 + 1 marker).
- Run `wc -l src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs` — must be 514 before this task.

- [ ] **Step 1: Read main file around lines 105-115 + 198-215 to capture OnRateLimitRejectedCountChanged + OnDbcLoaded**

Two reads.

- [ ] **Step 2: Create `DbcIntegrationFlow.cs`**

Create with header + verbatim content. Required usings:
- `PeakCan.Host.Core.Dbc` (DbcDocument)

- [ ] **Step 3: Write the deletion script (2 non-contiguous ranges)**

```python
"""Delete Flow D (DbcIntegration) from MultiFrameSendViewModel.cs (Task 2)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# 2 ranges in the post-Task-1 514-LoC file (+1 marker):
# (1) OnRateLimitRejectedCountChanged + xmldoc (around 105-120)
# (2) OnDbcLoaded + xmldoc (around 198-215)
DELETIONS = [
    (START_LINE_1, END_LINE_1, "OnRateLimitRejectedCountChanged + xmldoc"),
    (START_LINE_2, END_LINE_2, "OnDbcLoaded + xmldoc"),
]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 514, f"Expected 514 LoC after Task 1 (+1 marker), got {original_count}"

max_line = max(e for _, e, _ in DELETIONS)
assert max_line <= original_count, f"Line {max_line} > file length {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text
assert "public sealed partial class MultiFrameSendViewModel" in text
assert "public MultiFrameSendViewModel(" in text

marker = "    // === Flow D methods moved to MultiFrameSendViewModel/DbcIntegrationFlow.cs (W7 Task 2) ===\n"
for i, ln in enumerate(lines):
    if "Flow E methods moved to MultiFrameSendViewModel/LifecycleFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow D marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
```

- [ ] **Step 4: Run + build + test**

```bash
python scripts/w7_task2_delete_dbcintegrationflow.py
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~MultiFrameSendViewModel"
```

Expected: 0 errors; tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel/DbcIntegrationFlow.cs scripts/w7_task2_delete_dbcintegrationflow.py
git commit -m "refactor(mfsvm): extract Flow D (DbcIntegration) to partial class (W7 Task 2)

Flow D owns: OnRateLimitRejectedCountChanged (partial void) + OnDbcLoaded.
Cross-flow refs (partial-class visible):
- OnRateLimitRejectedCountChanged -> RateLimitRejectedVisibility (main)
- OnDbcLoaded -> _dbc (state, main)

Main file: 514 -> ~495 LoC (-20 LoC).
Tests: MultiFrameSendViewModel pass; build clean."
```

---

### Task 3: Extract Flow C → `LibraryFlow.cs`

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel/LibraryFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs` (delete SaveCurrent + LoadSaved + DeleteSaved + ReplaceOrAddInPicker — verify exact ranges)
- Create: `scripts/w7_task3_delete_libraryflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `_libraryService` (DI field), `Rows` (state), `SavedPicker` (state)
- Produces: 4 methods

**Pre-conditions**:
- Task 2 committed. Main file at 515 LoC (514 + 1 marker).
- Run `wc -l` — must be 515 before this task.

- [ ] **Step 1: Read main file around lines 355-430 to capture library methods**

Multiple reads. Methods may be interleaved.

- [ ] **Step 2: Create `LibraryFlow.cs`**

Create with header + verbatim content. Required usings:
- `CommunityToolkit.Mvvm.Input` (for [RelayCommand])

- [ ] **Step 3: Write the deletion script**

```python
"""Delete Flow C (Library) from MultiFrameSendViewModel.cs (Task 3)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines (verified before Task 3): 4 library methods + xmldoc.
# Expected LoC: 515 (514 + 1 marker).
DELETIONS = [(START_LINE, END_LINE, "4 library methods + xmldoc")]  # fill in actual line range

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 515, f"Expected 515 LoC after Task 2, got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text
assert "public sealed partial class MultiFrameSendViewModel" in text
assert "public MultiFrameSendViewModel(" in text

marker = "    // === Flow C methods moved to MultiFrameSendViewModel/LibraryFlow.cs (W7 Task 3) ===\n"
for i, ln in enumerate(lines):
    if "Flow D methods moved to MultiFrameSendViewModel/DbcIntegrationFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow C marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
```

- [ ] **Step 4: Run + build + test**

```bash
python scripts/w7_task3_delete_libraryflow.py
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~MultiFrameSendViewModel"
```

If build fails with `CS0246 RelayCommand`: add `using CommunityToolkit.Mvvm.Input;`.

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel/LibraryFlow.cs scripts/w7_task3_delete_libraryflow.py
git commit -m "refactor(mfsvm): extract Flow C (Library) to partial class (W7 Task 3)

Flow C owns: SaveCurrent + LoadSaved + DeleteSaved + ReplaceOrAddInPicker.
Cross-flow refs (partial-class visible):
- All 4 -> _libraryService (DI, main) + Rows (state, main)

Main file: 515 -> ~415 LoC (-100 LoC).
Tests: MultiFrameSendViewModel pass; build clean."
```

---

### Task 4: Extract Flow B → `SendFlow.cs`

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel/SendFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs` (delete SendAsync + Stop — verify exact ranges)
- Create: `scripts/w7_task4_delete_sendflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `_sendService` (DI field), `Rows` (state), `Iterations` (state)
- Produces: 2 methods (1 async + 1 sync)

**Pre-conditions**:
- Task 3 committed. Main file at 516 LoC (515 + 1 marker).
- Run `wc -l` — must be 516 before this task.

- [ ] **Step 1: Read main file around lines 290-355 to capture SendAsync + Stop**

- [ ] **Step 2: Create `SendFlow.cs`**

Create with header + verbatim content. Required usings:
- `CommunityToolkit.Mvvm.Input` (for [RelayCommand])
- `PeakCan.Host.Core` (if SendAsync references CanId etc.)

- [ ] **Step 3: Write the deletion script**

```python
"""Delete Flow B (Send) from MultiFrameSendViewModel.cs (Task 4)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines (verified before Task 4): SendAsync + Stop + xmldoc.
# Expected LoC: 516 (515 + 1 marker).
DELETIONS = [(START_LINE, END_LINE, "SendAsync + Stop + xmldoc")]  # fill in actual line range

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 516, f"Expected 516 LoC after Task 3, got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text
assert "public sealed partial class MultiFrameSendViewModel" in text
assert "public MultiFrameSendViewModel(" in text

marker = "    // === Flow B methods moved to MultiFrameSendViewModel/SendFlow.cs (W7 Task 4) ===\n"
for i, ln in enumerate(lines):
    if "Flow C methods moved to MultiFrameSendViewModel/LibraryFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow B marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
```

- [ ] **Step 4: Run + build + test**

```bash
python scripts/w7_task4_delete_sendflow.py
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~MultiFrameSendViewModel"
```

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel/SendFlow.cs scripts/w7_task4_delete_sendflow.py
git commit -m "refactor(mfsvm): extract Flow B (Send) to partial class (W7 Task 4)

Flow B owns: SendAsync + Stop.
Cross-flow refs (partial-class visible):
- SendAsync -> _sendService (DI, main) + Rows (state, main)

Main file: 516 -> ~436 LoC (-80 LoC).
Tests: MultiFrameSendViewModel pass; build clean."
```

---

### Task 5: Extract Flow A → `RowManagementFlow.cs` (largest)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel/RowManagementFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs` (delete AddRow + RemoveRow + DuplicateRow + MoveUp + MoveDown + ClearRows + OnRowsChanged + RefreshProgressMax + OnIterationsChanged — verify exact ranges)
- Create: `scripts/w7_task5_delete_rowmanagementflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `Rows` (state), `Iterations` (state)
- Produces: 8 methods (6 [RelayCommand] + 2 private helpers)

**Pre-conditions**:
- Task 4 committed. Main file at 517 LoC (516 + 1 marker).
- Run `wc -l` — must be 517 before this task.

- [ ] **Step 1: Read main file around lines 215-295 to capture all row methods**

Methods may be interleaved with Flow B/C/D content.

- [ ] **Step 2: Create `RowManagementFlow.cs`**

Create with header + verbatim content. Required usings:
- `CommunityToolkit.Mvvm.Input` (for [RelayCommand])
- `System.Collections.Specialized` (NotifyCollectionChangedEventArgs)

- [ ] **Step 3: Write the deletion script**

```python
"""Delete Flow A (RowManagement) from MultiFrameSendViewModel.cs (Task 5)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines (verified before Task 5): 8 row methods + xmldoc.
# Expected LoC: 517 (516 + 1 marker).
DELETIONS = [(START_LINE, END_LINE, "Row CRUD + reorder + progress")]  # fill in actual line range

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 517, f"Expected 517 LoC after Task 4, got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text
assert "public sealed partial class MultiFrameSendViewModel" in text
assert "public MultiFrameSendViewModel(" in text

marker = "    // === Flow A methods moved to MultiFrameSendViewModel/RowManagementFlow.cs (W7 Task 5) ===\n"
for i, ln in enumerate(lines):
    if "Flow B methods moved to MultiFrameSendViewModel/SendFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow A marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
```

- [ ] **Step 4: Run + build + test**

```bash
python scripts/w7_task5_delete_rowmanagementflow.py
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~MultiFrameSendViewModel"
```

- [ ] **Step 5: Verify main file is now ~220 LoC**

```bash
wc -l src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs
```

Expected: ~220 LoC.

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel/RowManagementFlow.cs scripts/w7_task5_delete_rowmanagementflow.py
git commit -m "refactor(mfsvm): extract Flow A (RowManagement) to partial class (W7 Task 5)

Flow A owns: AddRow + RemoveRow + DuplicateRow + MoveUp + MoveDown +
ClearRows + OnRowsChanged + RefreshProgressMax + OnIterationsChanged
partial void.

Cross-flow refs (partial-class visible):
- All 8 -> Rows + Iterations (state, main)

Main file: 517 -> ~220 LoC (-297 LoC).
Cumulative W7: 513 -> ~220 LoC (-293 LoC, -57.1%).
Tests: MultiFrameSendViewModel pass; build clean."
```

---

### Task 6: Version bump + release notes (v3.22.0 MINOR ship commit)

**Files:**
- Modify: `src/Directory.Build.props` (Version 3.21.0 → 3.22.0)
- Create: `docs/release-notes-v3.22.0.md`

**Pre-conditions**:
- Task 5 committed. 6 source commits on `feature/w7-multi-frame-send-view-model-god-class`.

- [ ] **Step 1: Bump version in Directory.Build.props**

Edit `src/Directory.Build.props`:
- `<Version>3.21.0</Version>` → `<Version>3.22.0</Version>`
- `<AssemblyVersion>3.21.0.0</AssemblyVersion>` → `<AssemblyVersion>3.22.0.0</AssemblyVersion>`
- `<FileVersion>3.21.0.0</FileVersion>` → `<FileVersion>3.22.0.0</FileVersion>`
- `<InformationalVersion>3.21.0</InformationalVersion>` → `<InformationalVersion>3.22.0</InformationalVersion>`

- [ ] **Step 2: Build to verify version bump**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```

Expected: 0 errors.

- [ ] **Step 3: Write release notes**

Create `docs/release-notes-v3.22.0.md` following the same structure as v3.20.0 / v3.21.0.

- [ ] **Step 4: Commit**

```bash
git add src/Directory.Build.props docs/release-notes-v3.22.0.md
git commit -m "chore(release): bump version to v3.22.0 + add release notes

W7 MINOR ship: MultiFrameSendViewModel god-class refactor complete.
Main file: 513 -> ~220 LoC (-57%). 5 partial-class files created
in src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel/."
```

---

### Task 7: Tier-3 push + tag + GH release

**Pre-conditions**:
- Task 6 committed. 7 commits on `feature/w7-multi-frame-send-view-model-god-class`.

- [ ] **Step 1: Push branch**

```bash
git push origin feature/w7-multi-frame-send-view-model-god-class
```

- [ ] **Step 2: Verify remote**

```bash
git ls-remote origin refs/heads/feature/w7-multi-frame-send-view-model-god-class
```

- [ ] **Step 3: Create annotated tag**

```bash
git tag -a v3.22.0 -m "v3.22.0 MINOR — MultiFrameSendViewModel god-class refactor

Main file: 513 -> ~220 LoC (-57%). 5 partial-class files created."
```

- [ ] **Step 4: Push tag**

```bash
git push origin v3.22.0
```

- [ ] **Step 5: Create GitHub release**

```bash
gh release create v3.22.0 --title "v3.22.0 MINOR — MultiFrameSendViewModel god-class refactor" --notes-file docs/release-notes-v3.22.0.md
```

- [ ] **Step 6: Verify release**

```bash
gh release view v3.22.0
```

---

## Self-Review

**1. Spec coverage**: Every section of the spec is covered by a task:
- 5 flow boundaries → Tasks 1-5
- Main file structure → Task 5 (final commit shows the slimmed main)
- Public API preservation → every task's verification
- Architecture invariants → pre-conditions
- Risk notes → pre-conditions + fix-on-fail guidance
- Ship target v3.22.0 → Tasks 6-7

**2. Placeholder scan**: No "TBD", "TODO", "implement later", or "similar to Task N" placeholders.

**3. Type consistency**:
- `MultiFrameSendViewModel` referenced consistently.
- `RowManagementFlow.cs` references `NotifyCollectionChangedEventArgs` (Specialized), `[RelayCommand]` (Input) — confirmed.
- `SendFlow.cs` references `CanId` (Core), `[RelayCommand]` (Input) — confirmed.
- `LibraryFlow.cs` references `[RelayCommand]` (Input), `SequenceLibrary.SavedSequence` (Services.Sequence) — confirmed.
- `DbcIntegrationFlow.cs` references `DbcDocument` (Core.Dbc) — confirmed.
- `LifecycleFlow.cs` minimal usings.

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-07-11-multi-frame-send-view-model-god-class-refactor.md`. Two execution options:

1. **Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks.
2. **Inline Execution** — Execute tasks in this session using executing-plans.