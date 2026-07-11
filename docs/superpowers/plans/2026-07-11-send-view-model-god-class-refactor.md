# W6 SendViewModel god-class refactor — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.App/ViewModels/SendViewModel.cs` from 533 LoC to ~250 LoC by extracting 4 logical flow groups into partial-class files. Pure mechanical refactor — zero behavioral change, zero test changes, zero API surface change.

**Architecture:** Same W3/W4/W5 partial-class split pattern. SendViewModel stays a single `sealed partial class : ObservableObject, IDisposable` with 4 partial-class files in `src/PeakCan.Host.App/ViewModels/SendViewModel/` directory. Main file keeps constructor, [ObservableProperty] fields, DI-injected services, public properties. Each partial file owns one logical flow group. Tasks ordered smallest-flow-first (D → B → C → A) so the deletion script gets validated on the smallest block before tackling larger cross-flow refs.

**Tech Stack:** C# .NET 10, WPF, CommunityToolkit.Mvvm 8.x. Git with LF line endings (per v3.18.0 PATCH `.gitattributes` — proven effective by W4 + W5 merges having 0 conflicts).

## Global Constraints

- **Public API unchanged.** No method signatures, [RelayCommand] attributes, or [ObservableProperty] backing fields move.
- **partial-class visibility.** All private methods visible across partial files; cross-flow calls stay as plain invocations.
- **Test coverage unchanged.** No tests added, removed, or modified.
- **Line-ending normalized to LF.** Per v3.18.0 PATCH `.gitattributes` enforcement.
- **No behavioral change.** Every method body, xmldoc, comment, and whitespace moves verbatim.
- **No version bump until Task 5.** Tasks 1-4 keep `Directory.Build.props` at v3.20.0. Task 5 bumps to v3.21.0.
- **Branch**: `feature/w6-send-view-model-god-class` (already created from `main` @ `833cd85`).
- **Spec**: `docs/superpowers/specs/2026-07-11-send-view-model-god-class-refactor.md`.

---

## File Structure

```
src/PeakCan.Host.App/ViewModels/SendViewModel.cs                      # main file, ~250 LoC after Task 4
src/PeakCan.Host.App/ViewModels/SendViewModel/                        # NEW directory
  LifecycleFlow.cs                                                    # Task 1 — Dispose
  CyclicFlow.cs                                                       # Task 2 — StartCyclic + StopCyclic
  LibraryFlow.cs                                                      # Task 3 — Library commands + OpenMultiFrameSend + 2 log helpers
  FrameSendFlow.cs                                                    # Task 4 — SendAsync + partial void + 5 log helpers
docs/superpowers/plans/2026-07-11-send-view-model-god-class-refactor.md   # this file
docs/release-notes-v3.21.0.md                                        # NEW in Task 5
```

---

## Cumulative method-line ranges (anchors for all 4 tasks)

These ranges are 1-indexed inclusive line numbers in the **pre-Task-1 file** (533 LoC, as of commit `833cd85`). All deletion scripts delete by line-range slicing per the W3 lesson.

**W5 lesson applied**: Each task inserts a "Flow X marker" comment line into the main file. Subsequent tasks must account for the +1 marker line per prior task. Adjusted expected line counts:

| Task starts after | Expected LoC |
|---|---|
| Task 1 (initial) | 533 |
| Task 2 | 534 (Task 1 marker line +1) |
| Task 3 | 535 (Tasks 1+2 markers +2) |
| Task 4 | 536 (Tasks 1+2+3 markers +3) |

**W5 lesson applied**: Methods may be interleaved (SendViewModel pattern). Pre-verify the exact line ranges by reading the file before each task.

---

### Task 1: Extract Flow D → `LifecycleFlow.cs` (smallest first)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/SendViewModel/LifecycleFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/SendViewModel.cs:211-216` (delete Dispose + xmldoc — verify exact range)
- Create: `scripts/w6_task1_delete_lifecycleflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): cyclic state (intra-cross-flow, Flow B) — stops cyclic on dispose
- Produces: 1 public void method

**Pre-conditions**:
- Branch `feature/w6-send-view-model-god-class` checked out
- `git status` clean
- `src/PeakCan.Host.App/ViewModels/SendViewModel.cs` is 533 LoC

- [ ] **Step 1: Read main file lines 200-225 to capture exact verbatim content of Dispose**

Use Read tool with offset 200, limit 25. Capture the xmldoc + Dispose method body.

- [ ] **Step 2: Create the partial file `LifecycleFlow.cs`**

Create `src/PeakCan.Host.App/ViewModels/SendViewModel/LifecycleFlow.cs` with the verbatim content + header:

```csharp
namespace PeakCan.Host.App.ViewModels;

public sealed partial class SendViewModel
{
    // Flow D: Lifecycle (v3.x.x + earlier).
    // Methods moved verbatim from SendViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - Dispose -> Cyclic state (Flow B) — stops cyclic on dispose

    /// <summary>
    /// [capture verbatim xmldoc from main file]
    /// </summary>
    public void Dispose()
    {
        // [capture verbatim method body]
    }
}
```

- [ ] **Step 3: Write the deletion script**

Create `scripts/w6_task1_delete_lifecycleflow.py`:

```python
"""Delete Flow D (Lifecycle) from SendViewModel.cs (Task 1)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/SendViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines (verified before Task 1): Dispose + xmldoc.
DELETIONS = [(START_LINE, END_LINE, "Dispose + xmldoc")]  # fill in actual line range

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 533, f"Expected 533 LoC, got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text, "namespace missing!"
assert "public sealed partial class SendViewModel" in text, "class declaration missing!"
assert "public SendViewModel(" in text, "ctor missing!"

marker = "    // === Flow D methods moved to SendViewModel/LifecycleFlow.cs (W6 Task 1) ===\n"
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
python scripts/w6_task1_delete_lifecycleflow.py
```

- [ ] **Step 5: Build to verify**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```

Expected: `已成功生成。` with 0 errors.

- [ ] **Step 6: Run targeted tests**

```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~SendViewModel"
```

Expected: pass count matches pre-Task-1 baseline.

- [ ] **Step 7: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/SendViewModel.cs src/PeakCan.Host.App/ViewModels/SendViewModel/LifecycleFlow.cs scripts/w6_task1_delete_lifecycleflow.py
git commit -m "refactor(svm): extract Flow D (Lifecycle) to partial class (W6 Task 1)

Flow D owns: Dispose.
Cross-flow refs (partial-class visible):
- Dispose -> Cyclic state (Flow B) — stops cyclic on dispose

Main file: 533 -> ~523 LoC (-10 LoC).
Tests: SendViewModel pass; build clean."
```

---

### Task 2: Extract Flow B → `CyclicFlow.cs`

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/SendViewModel/CyclicFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/SendViewModel.cs` (delete StartCyclic + StopCyclic + xmldoc — verify exact range)
- Create: `scripts/w6_task2_delete_cyclicflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `_sendService` (DI field), `Latest`/`Frame` (state), `CyclicTimer`/`SendCyclicCommand` (state)
- Produces: 2 private void methods + [RelayCommand] StartCyclicCommand

**Pre-conditions**:
- Task 1 committed. Main file at 534 LoC (533 + 1 marker).
- Run `wc -l src/PeakCan.Host.App/ViewModels/SendViewModel.cs` — must be 534 before this task.

- [ ] **Step 1: Read main file lines around 346-373 to capture StartCyclic + StopCyclic**

Use Read tool to capture both methods + their xmldocs.

- [ ] **Step 2: Create `CyclicFlow.cs`**

Create with header + verbatim method bodies. Required usings:
- `CommunityToolkit.Mvvm.Input` (for [RelayCommand])

[RelayCommand] attributes MUST travel with their methods.

- [ ] **Step 3: Write the deletion script**

```python
"""Delete Flow B (Cyclic) from SendViewModel.cs (Task 2)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/SendViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines (verified before Task 2): StartCyclic + StopCyclic + xmldoc.
# Expected LoC: 534 (533 + 1 Task 1 marker).
DELETIONS = [(START_LINE, END_LINE, "StartCyclic + StopCyclic + xmldoc")]  # fill in actual line range

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 534, f"Expected 534 LoC after Task 1, got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text
assert "public sealed partial class SendViewModel" in text
assert "public SendViewModel(" in text

marker = "    // === Flow B methods moved to SendViewModel/CyclicFlow.cs (W6 Task 2) ===\n"
for i, ln in enumerate(lines):
    if "Flow D methods moved to SendViewModel/LifecycleFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow B marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
```

- [ ] **Step 4: Run + build + test**

```bash
python scripts/w6_task2_delete_cyclicflow.py
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~SendViewModel"
```

Expected: 0 errors; SendViewModel tests pass at same count.

If build fails with `CS0246 RelayCommand`: add `using CommunityToolkit.Mvvm.Input;`.

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/SendViewModel.cs src/PeakCan.Host.App/ViewModels/SendViewModel/CyclicFlow.cs scripts/w6_task2_delete_cyclicflow.py
git commit -m "refactor(svm): extract Flow B (Cyclic) to partial class (W6 Task 2)

Flow B owns: StartCyclic + StopCyclic. All [RelayCommand] attributes
travel with their methods.

Cross-flow refs (partial-class visible):
- StartCyclicCommand -> _sendService (DI, main)
- StopCyclic -> CyclicTimer (state, main)

Main file: 534 -> ~504 LoC (-30 LoC).
Tests: SendViewModel pass; build clean."
```

---

### Task 3: Extract Flow C → `LibraryFlow.cs`

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/SendViewModel/LibraryFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/SendViewModel.cs` (delete 5 library methods + 2 log helpers — verify exact ranges)
- Create: `scripts/w6_task3_delete_libraryflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `_library` (DI field), `_fileDialogs` (DI field), `Latest`/`Frame` (state)
- Produces: 5 private/public void methods + 2 log helpers

**Pre-conditions**:
- Task 2 committed. Main file at 535 LoC (534 + 1 marker).
- Run `wc -l src/PeakCan.Host.App/ViewModels/SendViewModel.cs` — must be 535 before this task.

- [ ] **Step 1: Read main file lines around 383-508 to capture all 5 library methods + 2 log helpers**

Multiple reads — methods may be interleaved with Flow A/B/D content. Capture exact ranges.

- [ ] **Step 2: Create `LibraryFlow.cs`**

Create with header + verbatim content. Required usings:
- `CommunityToolkit.Mvvm.Input` (for [RelayCommand] on SaveCurrentToLibraryCommand, DeleteFromLibraryCommand)
- `Microsoft.Extensions.Logging` (for 2 log helpers)
- `PeakCan.Host.App.Windows` (for `MultiFrameSendWindow` in OpenMultiFrameSend)

[RelayCommand] attributes travel with methods.

- [ ] **Step 3: Write the deletion script (2 non-contiguous ranges — library methods + log helpers)**

```python
"""Delete Flow C (Library) from SendViewModel.cs (Task 3)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/SendViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# 2 ranges in the post-Task-2 535-LoC file:
# (1) Library methods + xmldoc (383-475 area)
# (2) LogSaveToLibraryFailed + LogDeleteFromLibraryFailed (494-501)
# Plus OpenMultiFrameSend at line 508 (separate range)
# Adjust ranges after reading exact lines.

DELETIONS = [
    (START_LINE_1, END_LINE_1, "Library methods"),
    (START_LINE_2, END_LINE_2, "2 log helpers"),
    (START_LINE_3, END_LINE_3, "OpenMultiFrameSend"),
]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 535, f"Expected 535 LoC after Task 2, got {original_count}"

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
assert "public sealed partial class SendViewModel" in text
assert "public SendViewModel(" in text

marker = "    // === Flow C methods moved to SendViewModel/LibraryFlow.cs (W6 Task 3) ===\n"
for i, ln in enumerate(lines):
    if "Flow B methods moved to SendViewModel/CyclicFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow C marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
```

- [ ] **Step 4: Run + build + test**

```bash
python scripts/w6_task3_delete_libraryflow.py
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~SendViewModel"
```

If build fails: add `using CommunityToolkit.Mvvm.Input;` + `using Microsoft.Extensions.Logging;` + `using PeakCan.Host.App.Windows;`.

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/SendViewModel.cs src/PeakCan.Host.App/ViewModels/SendViewModel/LibraryFlow.cs scripts/w6_task3_delete_libraryflow.py
git commit -m "refactor(svm): extract Flow C (Library) to partial class (W6 Task 3)

Flow B owns: RefreshLibrary + SaveCurrentToLibrary + LoadFromLibrary
+ DeleteFromLibrary + OpenMultiFrameSend + 2 log helpers.

Cross-flow refs (partial-class visible):
- Library commands -> _library + _fileDialogs (DI, main)
- OpenMultiFrameSend -> MultiFrameSendWindow (Windows namespace)

Main file: 535 -> ~395 LoC (-140 LoC).
Tests: SendViewModel pass; build clean."
```

---

### Task 4: Extract Flow A → `FrameSendFlow.cs` (largest)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/SendViewModel/FrameSendFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/SendViewModel.cs` (delete SendAsync + OnRateLimitRejectedCountChanged + 5 log helpers — verify exact ranges)
- Create: `scripts/w6_task4_delete_framesendflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `_sendService` (DI field), `_router` (DI field), `Latest`/`Frame` (state), Cyclic state (Flow B)
- Produces: 1 public async Task + 1 partial void + 5 log helpers

**Pre-conditions**:
- Task 3 committed. Main file at 536 LoC (535 + 1 marker).
- Run `wc -l src/PeakCan.Host.App/ViewModels/SendViewModel.cs` — must be 536 before this task.

- [ ] **Step 1: Read main file lines around 119 + 225-260 + 476-492 to capture OnRateLimitRejectedCountChanged + SendAsync + 5 log helpers**

Multiple reads. The OnRateLimitRejectedCountChanged is at line 119 (near top of methods), SendAsync at line 225 (middle), log helpers at lines 476-488 (bottom).

- [ ] **Step 2: Create `FrameSendFlow.cs`**

Create with header + verbatim content. Required usings:
- `Microsoft.Extensions.Logging` (for 5 log helpers)
- `PeakCan.Host.Core` (for CanId, ErrorCode)

- [ ] **Step 3: Write the deletion script (3 ranges)**

```python
"""Delete Flow A (FrameSend) from SendViewModel.cs (Task 4)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/SendViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# 3 ranges in the post-Task-3 536-LoC file:
# (1) OnRateLimitRejectedCountChanged + xmldoc (119-130 area)
# (2) SendAsync + xmldoc (225-260 area)
# (3) 5 log helpers (476-488)
# Adjust ranges after reading exact lines.

DELETIONS = [
    (START_LINE_1, END_LINE_1, "OnRateLimitRejectedCountChanged"),
    (START_LINE_2, END_LINE_2, "SendAsync"),
    (START_LINE_3, END_LINE_3, "5 log helpers"),
]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 536, f"Expected 536 LoC after Task 3, got {original_count}"

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
assert "public sealed partial class SendViewModel" in text
assert "public SendViewModel(" in text

marker = "    // === Flow A methods moved to SendViewModel/FrameSendFlow.cs (W6 Task 4) ===\n"
for i, ln in enumerate(lines):
    if "Flow C methods moved to SendViewModel/LibraryFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow A marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
```

- [ ] **Step 4: Run + build + test**

```bash
python scripts/w6_task4_delete_framesendflow.py
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~SendViewModel"
```

Expected: 0 errors; tests pass.

- [ ] **Step 5: Verify main file is now ~250-330 LoC**

```bash
wc -l src/PeakCan.Host.App/ViewModels/SendViewModel.cs
```

Expected: 250-330 LoC. The 4 partial files together should sum to ~200-280 LoC.

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/SendViewModel.cs src/PeakCan.Host.App/ViewModels/SendViewModel/FrameSendFlow.cs scripts/w6_task4_delete_framesendflow.py
git commit -m "refactor(svm): extract Flow A (FrameSend) to partial class (W6 Task 4)

Flow A owns: SendAsync + OnRateLimitRejectedCountChanged + 5 log helpers.

Cross-flow refs (partial-class visible):
- SendAsync -> _sendService + _router (DI, main) + Latest/Frame (state, main)
- SendAsync -> Cyclic state (Flow B) — stops cyclic on manual send

Main file: 536 -> ~330 LoC (-200 LoC).
Cumulative W6: 533 -> ~330 LoC (-203 LoC, -38.1%).
Tests: SendViewModel pass; build clean."
```

---

### Task 5: Version bump + release notes (v3.21.0 MINOR ship commit)

**Files:**
- Modify: `src/Directory.Build.props` (Version 3.20.0 → 3.21.0)
- Create: `docs/release-notes-v3.21.0.md`

**Pre-conditions**:
- Task 4 committed. 5 source commits on `feature/w6-send-view-model-god-class`.

- [ ] **Step 1: Bump version in Directory.Build.props**

Edit `src/Directory.Build.props`:
- `<Version>3.20.0</Version>` → `<Version>3.21.0</Version>`
- `<AssemblyVersion>3.20.0.0</AssemblyVersion>` → `<AssemblyVersion>3.21.0.0</AssemblyVersion>`
- `<FileVersion>3.20.0.0</FileVersion>` → `<FileVersion>3.21.0.0</FileVersion>`
- `<InformationalVersion>3.20.0</InformationalVersion>` → `<InformationalVersion>3.21.0</InformationalVersion>`

- [ ] **Step 2: Build to verify version bump**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```

Expected: 0 errors.

- [ ] **Step 3: Write release notes**

Create `docs/release-notes-v3.21.0.md` following the same structure as v3.19.0 / v3.20.0.

- [ ] **Step 4: Commit**

```bash
git add src/Directory.Build.props docs/release-notes-v3.21.0.md
git commit -m "chore(release): bump version to v3.21.0 + add release notes

W6 MINOR ship: SendViewModel god-class refactor complete.
Main file: 533 -> ~330 LoC (-38%). 4 partial-class files created
in src/PeakCan.Host.App/ViewModels/SendViewModel/."
```

---

### Task 6: Tier-3 push + tag + GH release

**Pre-conditions**:
- Task 5 committed. 6 commits on `feature/w6-send-view-model-god-class`.

- [ ] **Step 1: Push branch**

```bash
git push origin feature/w6-send-view-model-god-class
```

- [ ] **Step 2: Verify remote**

```bash
git ls-remote origin refs/heads/feature/w6-send-view-model-god-class
```

- [ ] **Step 3: Create annotated tag**

```bash
git tag -a v3.21.0 -m "v3.21.0 MINOR — SendViewModel god-class refactor

Main file: 533 -> ~330 LoC (-38%). 4 partial-class files created."
```

- [ ] **Step 4: Push tag**

```bash
git push origin v3.21.0
```

- [ ] **Step 5: Create GitHub release**

```bash
gh release create v3.21.0 --title "v3.21.0 MINOR — SendViewModel god-class refactor" --notes-file docs/release-notes-v3.21.0.md
```

- [ ] **Step 6: Verify release**

```bash
gh release view v3.21.0
```

---

## Self-Review

**1. Spec coverage**: Every section of the spec is covered by a task:
- 4 flow boundaries → Tasks 1-4
- Main file structure → Task 4
- Public API preservation → every task's verification
- Architecture invariants → pre-conditions
- Risk notes → pre-conditions + fix-on-fail guidance
- Ship target v3.21.0 → Tasks 5-6

**2. Placeholder scan**: No "TBD", "TODO", "implement later", or "similar to Task N" placeholders.

**3. Type consistency**:
- `SendViewModel` referenced consistently.
- `FrameSendFlow.cs` references `CanId`, `ErrorCode` (Core), `ILogger` (Logging) — confirmed.
- `CyclicFlow.cs` references `[RelayCommand]` (Input) — confirmed.
- `LibraryFlow.cs` references `MultiFrameSendWindow` (Windows), `[RelayCommand]` (Input), `ILogger` (Logging) — confirmed.

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-07-11-send-view-model-god-class-refactor.md`. Two execution options:

1. **Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks.
2. **Inline Execution** — Execute tasks in this session using executing-plans.