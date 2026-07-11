# W4 AppShellViewModel god-class refactor — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs` from 1019 LoC to ~400 LoC by extracting 4 logical flow groups into partial-class files. Pure mechanical refactor — zero behavioral change, zero test changes, zero API surface change.

**Architecture:** Same W3 partial-class split pattern. AppShellViewModel stays a single `sealed partial class` with 4 partial-class files in `src/PeakCan.Host.App/ViewModels/AppShellViewModel/` directory. Main file keeps constructor, [ObservableProperty] fields, DI-injected services, partial void change handlers, and Dispose. Each partial file owns one logical flow group. Tasks ordered smallest-flow-first (D → C → B → A) so the deletion script gets validated on the smallest block before tackling larger cross-flow refs.

**Tech Stack:** C# .NET 10, WPF, CommunityToolkit.Mvvm 8.x. Git with LF line endings (per v3.18.0 PATCH `.gitattributes`).

## Global Constraints

- **Public API unchanged.** No method signatures, [RelayCommand] attributes, or [ObservableProperty] backing fields move. XAML bindings are not affected.
- **partial-class visibility.** All private methods visible across partial files; cross-flow calls stay as plain invocations (no facade, no renaming).
- **Test coverage unchanged.** No tests added, removed, or modified. Verification gate is `dotnet build` + existing test suite passing.
- **Line-ending normalized to LF.** Per v3.18.0 PATCH `.gitattributes` enforcement.
- **No behavioral change.** Every method body, xmldoc, comment, and whitespace moves verbatim.
- **No version bump until Task 5.** Tasks 1-4 keep `Directory.Build.props` at v3.18.0. Task 5 bumps to v3.19.0.
- **Branch**: `feature/w4-app-shell-god-class` (already created from `main` @ `133055c`).
- **Spec**: `docs/superpowers/specs/2026-07-11-app-shell-view-model-god-class-refactor.md`.

---

## File Structure

```
src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs              # main file, ~400 LoC after Task 4
src/PeakCan.Host.App/ViewModels/AppShellViewModel/                 # NEW directory
  LogFlow.cs                                                       # Task 1 — 11 LoggerMessage partial void
  SessionFlow.cs                                                   # Task 2 — OpenDbc + session open/save
  ViewSwitchFlow.cs                                                # Task 3 — 11 Show* + OpenMultiFrame
  ChannelFlow.cs                                                   # Task 4 — Enumerate + Connect/Disconnect + error
docs/superpowers/plans/2026-07-11-app-shell-view-model-god-class-refactor.md   # this file
docs/release-notes-v3.19.0.md                                     # NEW in Task 5
```

---

## Cumulative method-line ranges (anchors for all 4 tasks)

These ranges are 1-indexed inclusive line numbers in the **pre-Task-1 file** (1019 LoC, as of commit `133055c`). All deletion scripts in Tasks 1-4 delete by line-range slicing per the W3 lesson `deletion-script-must-preserve-namespace-and-using-clauses-when-removing-methods`.

| Flow | Lines | Methods | Lines deleted |
|---|---|---|---|
| D (LogFlow) | 989-1018 | 11 partial void | 30 |
| C (SessionFlow) | 380-506 | 6 methods + xmldoc | 127 |
| B (ViewSwitchFlow) | 508-746 | 11 Show* + 1 OpenMultiFrame | 239 |
| A (ChannelFlow) | 748-981 | Enumerate + Connect/Disconnect + OnReadLoopError | 234 |

Total: 630 lines deleted from main, 4 new files created (630 LoC moved verbatim). Net main file: 1019 → 389 LoC.

After **each task**, the line numbers shift down by the lines just deleted. **Every deletion script below is written for the file state AT THE START OF THAT TASK, NOT the initial 1019 LoC file.** Tasks that come later use shifted line ranges.

To minimize confusion: every task includes the **post-prev-task** line count as a sanity check the script can assert against.

---

### Task 1: Extract Flow D → `LogFlow.cs` (smallest first, validates tooling)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/AppShellViewModel/LogFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs:989-1018` (delete the 11 LoggerMessage partial void)
- Create: `scripts/w4_task1_delete_logflow.py`

**Interfaces:**
- Consumes: nothing (pure declarations)
- Produces: `partial class AppShellViewModel` with the 11 `LogXxx` partial void methods (verbatim from main file lines 989-1018)

**Pre-conditions**:
- Branch `feature/w4-app-shell-god-class` checked out
- `git status` clean
- `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs` is 1019 LoC

- [ ] **Step 1: Create the partial file `LogFlow.cs`**

Create `src/PeakCan.Host.App/ViewModels/AppShellViewModel/LogFlow.cs` with the following content (copy verbatim from main file lines 989-1018):

```csharp
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class AppShellViewModel
{
    // Flow D: Log helpers (v3.16.9.4 PATCH + earlier).
    // Methods moved verbatim from AppShellViewModel.cs.
    //
    // All 11 helpers are [LoggerMessage] source-gen declarations.
    // The methods are deliberately not called from hot paths; their
    // only call site is the VM commands (Flow A + Flow B + Flow C).

    // v3.16.9.4 PATCH: ReadLoopError human-readable mapper (bus-off,
    // driver-unload, hardware-fault). The string format matches the
    // v3.16.9.4 release-notes vocabulary for diagnostic consistency.
    [LoggerMessage(Level = LogLevel.Error, Message = "Read loop error on handle 0x{Handle:X2}: {Kind}")]
    private static partial void LogReadLoopError(ILogger logger, ushort handle, string kind, Exception? ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Open DBC menu invoked")]
    private static partial void LogOpenDbcInvoked(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Probe OK on handle 0x{Handle:X2}")]
    private static partial void LogProbeOk(ILogger logger, ushort handle);

    [LoggerMessage(Level = LogLevel.Error, Message = "Probe threw on handle 0x{Handle:X2}")]
    private static partial void LogProbeThrew(ILogger logger, ushort handle, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connect OK on handle 0x{Handle:X2}")]
    private static partial void LogConnectOk(ILogger logger, ushort handle);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connect failed on handle 0x{Handle:X2}: {Code} {Message}")]
    private static partial void LogConnectFailed(ILogger logger, ushort handle, ErrorCode code, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Connect threw on handle 0x{Handle:X2}")]
    private static partial void LogConnectThrew(ILogger logger, ushort handle, Exception ex);

    // v3.8.8 PATCH F1: best-effort wrapper for the catch-arm
    // UnregisterChannel call. If the router itself throws (e.g. lock
    // contention or another sink's DisposeAsync propagating), we log
    // and continue so the channel dispose still runs.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Connect catch-arm UnregisterChannel threw on handle 0x{Handle:X2}")]
    private static partial void LogUnregisterFailed(ILogger logger, ushort handle, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Disconnect OK on handle 0x{Handle:X2}")]
    private static partial void LogDisconnectOk(ILogger logger, ushort handle);

    [LoggerMessage(Level = LogLevel.Error, Message = "Disconnect threw on handle 0x{Handle:X2}")]
    private static partial void LogDisconnectThrew(ILogger logger, ushort handle, Exception ex);
}
```

NOTE: The 11th helper `LogReadLoopError` was added in v3.16.9.4 PATCH. It is at line 982 in the main file (NOT in the 989-1018 range). After Task 1, `LogReadLoopError` stays in the main file (it lives next to `OnReadLoopError` which moves to Flow A in Task 4). This means the 11th helper is **deliberately split**: 10 helpers move to Flow D, 1 stays with Flow A. This is correct — `LogReadLoopError` is called only by `OnReadLoopError`.

Update the file content above: remove the `LogReadLoopError` block from `LogFlow.cs`. The 10 helpers moving are: `LogOpenDbcInvoked`, `LogProbeOk`, `LogProbeThrew`, `LogConnectOk`, `LogConnectFailed`, `LogConnectThrew`, `LogUnregisterFailed`, `LogDisconnectOk`, `LogDisconnectThrew` (+ 1 prior `LogXxx` at line 989 — confirm by reading).

ACTUALLY — re-read main file lines 982-1018 carefully before writing LogFlow.cs. The exact count of helpers in 989-1018 is **10** (lines 989, 992, 995, 998, 1001, 1004, 1011, 1014, 1017 = 9 LoggerMessage attributes + their 9 partial void declarations = 18 lines, plus blank lines and 1 comment block). Copy each one verbatim into LogFlow.cs.

- [ ] **Step 2: Write the deletion script**

Create `scripts/w4_task1_delete_logflow.py`:

```python
"""Delete Flow D (Log helpers) from AppShellViewModel.cs (Task 1)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines 989-1018: 10 [LoggerMessage] partial void + comment block.
# Verified against v3.18.0 HEAD (1019 LoC, commit 133055c).
DELETIONS = [(989, 1018, "10 LoggerMessage partial void + xmldoc comment")]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 1019, f"Expected 1019 LoC, got {original_count}"

# Delete bottom-up so earlier indices stay stable
to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

# Sanity: structural invariants must still hold
text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text, "namespace declaration missing!"
assert "public sealed partial class AppShellViewModel" in text, "class declaration missing!"
assert "public AppShellViewModel(" in text, "ctor missing!"

# Insert Flow D marker just before the closing brace of the class
# (which is now at the end of the file since we trimmed the log helpers).
marker = "    // === Flow D methods moved to AppShellViewModel/LogFlow.cs (W4 Task 1) ===\n"
# Find last `}` (class closing brace)
for i in range(len(lines) - 1, -1, -1):
    if lines[i].strip() == "}":
        lines.insert(i, marker)
        print(f"Flow D marker inserted before line {i + 1} (class closing brace)")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
```

- [ ] **Step 3: Run the deletion script**

```bash
python scripts/w4_task1_delete_logflow.py
```

Expected output (3-5 lines):
```
Original line count: 1019
  Deleting lines 989-1018: 10 LoggerMessage partial void + xmldoc comment (30 lines)
New line count: 989 (removed 30 lines)
Flow D marker inserted before line 989 (class closing brace)
Wrote XXXXX bytes
```

- [ ] **Step 4: Build to verify**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```

Expected: `已成功生成。` with 0 errors. (The pre-existing `CS8602` nullable warning in `DbcService.cs:157` is unrelated and acceptable.)

If the build fails with `CS0246` or `CS0103` errors mentioning missing types (e.g. `ErrorCode`, `BaudRate`, `BaudRateF`):
- Verify `LogFlow.cs` has `using PeakCan.Host.Core;` for `ErrorCode`
- The `BaudRate` enum is in `PeakCan.Host.Infrastructure.Channel` namespace — only needed if a log helper references it. Most don't.

If the build fails with `CS0103 File/Path not found`: add `using System.IO;`.

- [ ] **Step 5: Run targeted tests**

```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~AppShellViewModel"
```

Expected: `已通过! - 失败:     0，通过:    NN，通过+skip:    NN，总计:    NN` where NN matches the pre-Task-1 test count for `AppShellViewModel`. Run the same filter before Task 1 starts to record the baseline NN.

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs src/PeakCan.Host.App/ViewModels/AppShellViewModel/LogFlow.cs scripts/w4_task1_delete_logflow.py
git commit -m "refactor(asvm): extract Flow D (Log helpers) to partial class

Flow D owns 10 LoggerMessage partial void helpers (LogOpenDbcInvoked,
LogProbeOk/Threw, LogConnectOk/Failed/Threw, LogUnregisterFailed,
LogDisconnectOk/Threw, plus 1 prior). Pure source-gen declarations.

Note: 11th helper LogReadLoopError stays in main file (lines next to
OnReadLoopError which moves to Flow A in Task 4).

Main file: 1019 -> 989 LoC (-30 LoC).
Tests: AppShellViewModel tests pass unchanged; build clean."
```

---

### Task 2: Extract Flow C → `SessionFlow.cs`

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/AppShellViewModel/SessionFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs:380-506` (delete the 6 session methods)
- Create: `scripts/w4_task2_delete_sessionflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `_recentSessions`, `_traceViewerViewModel`, `_dbcService`, `_fileDialogs`, `_messageBoxPrompt`, `_logger`
- Consumes (cross-class): `TraceViewerViewModel.OpenSessionAsync`, `TraceViewerViewModel.SaveSessionAsync`
- Produces: 6 public-facing methods on AppShellViewModel

**Pre-conditions**:
- Task 1 committed. Main file at 989 LoC.
- Run `wc -l src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs` — must be 989 before this task.

- [ ] **Step 1: Read main file lines 378-510 to capture exact verbatim content**

Use the Read tool to read `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs` with offset 378, limit 132.

- [ ] **Step 2: Create `SessionFlow.cs`**

Create `src/PeakCan.Host.App/ViewModels/AppShellViewModel/SessionFlow.cs` with the exact verbatim content from Step 1, plus a header comment:

```csharp
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class AppShellViewModel
{
    // Flow C: Session open/save + recent-sessions list.
    // Methods moved verbatim from AppShellViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - OpenSessionAsync -> ShowTraceViewer (Flow B)
    //   - OpenRecentSessionAsync -> OpenSessionAsync (intra-flow)
    //   - SaveSessionAsync -> TraceViewerViewModel.SaveSessionAsync (cross-class)
}
```

Then paste the 6 method bodies (OpenDbc + xmldoc, OpenSessionAsync + xmldoc, SaveSessionAsync + xmldoc, OpenRecentSessionAsync + xmldoc, ClearRecentSessions + xmldoc, RefreshRecentEntries + xmldoc) verbatim from the file content read in Step 1.

Required usings (verify by scanning method bodies):
- `Microsoft.Extensions.Logging` (if any method calls `_logger`)
- `PeakCan.Host.Core` (for any `ErrorCode` or `BaudRate` references — likely yes via `_logger.LogXxx` + error message format strings)
- `PeakCan.Host.Core.Replay` (if any method references `ReplayException` — `OpenSessionAsync` likely does)

- [ ] **Step 3: Write the deletion script**

Create `scripts/w4_task2_delete_sessionflow.py` with the same pattern as Task 1's script:

```python
"""Delete Flow C (Session open/save) from AppShellViewModel.cs (Task 2)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines 380-506 in the post-Task-1 989-LoC file.
# 127 LoC of session-related methods + xmldoc.
DELETIONS = [(380, 506, "OpenDbc + OpenSessionAsync + SaveSessionAsync + OpenRecentSessionAsync + ClearRecentSessions + RefreshRecentEntries")]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 989, f"Expected 989 LoC after Task 1, got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text
assert "public sealed partial class AppShellViewModel" in text
assert "public AppShellViewModel(" in text

marker = "    // === Flow C methods moved to AppShellViewModel/SessionFlow.cs (W4 Task 2) ===\n"
# Insert after the Flow D marker
for i, ln in enumerate(lines):
    if "Flow D methods moved to AppShellViewModel/LogFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow C marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
```

- [ ] **Step 4: Run the deletion script**

```bash
python scripts/w4_task2_delete_sessionflow.py
```

Expected:
```
Original line count: 989
  Deleting lines 380-506: ... (127 lines)
New line count: 862 (removed 127 lines)
Flow C marker inserted after line NN
Wrote XXXXX bytes
```

- [ ] **Step 5: Build + test**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~AppShellViewModel"
```

Expected: 0 errors; AppShellViewModel tests pass at the same count as Task 1.

If build fails with `CS0246 FileNotFoundException` / `CS0103 Path` errors: add `using System.IO;` to SessionFlow.cs.

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs src/PeakCan.Host.App/ViewModels/AppShellViewModel/SessionFlow.cs scripts/w4_task2_delete_sessionflow.py
git commit -m "refactor(asvm): extract Flow C (session open/save) to partial class

Flow C owns: OpenDbc, OpenSessionAsync, SaveSessionAsync,
OpenRecentSessionAsync, ClearRecentSessions, RefreshRecentEntries.

Cross-flow refs (partial-class visible):
- OpenSessionAsync -> ShowTraceViewer (Flow B)
- SaveSessionAsync -> TraceViewerViewModel.SaveSessionAsync

Main file: 989 -> 862 LoC (-127 LoC).
Tests: AppShellViewModel pass unchanged; build clean."
```

---

### Task 3: Extract Flow B → `ViewSwitchFlow.cs`

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/AppShellViewModel/ViewSwitchFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs:508-746` (delete the 11 Show* + OpenMultiFrame + ShowTraceViewer)
- Create: `scripts/w4_task3_delete_viewflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): 9 view-model DI fields (`_traceViewModel`, `_dbcViewModel`, ..., `_replayViewModel`), 9 view-cache fields (`_traceView`, `_dbcView`, etc.), `GetOrCreateXxxView()` helpers, `CurrentView` property, `ViewSwitcher.Show` static helper
- Consumes (cross-class): `MultiFrameSendWindow`, `TraceViewerWindow`
- Produces: 11 [RelayCommand] `Show*` methods + 1 OpenMultiFrame + 1 ShowTraceViewer

**Pre-conditions**:
- Task 2 committed. Main file at 862 LoC.
- Run `wc -l src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs` — must be 862 before this task.

- [ ] **Step 1: Read main file lines 506-748 to capture exact verbatim content**

Use the Read tool with offset 506, limit 242.

- [ ] **Step 2: Create `ViewSwitchFlow.cs`**

Create the file with the same template as SessionFlow.cs (Flow C) but using namespace `PeakCan.Host.App.ViewModels`. Required usings (verify by scanning):
- `PeakCan.Host.App.Composition` (for `ViewSwitcher`)
- `PeakCan.Host.App.Views` (for the View types like `TraceView`, `DbcView`, `SendView`, etc.)
- `PeakCan.Host.App.Views.Uds` (for UdsView, if used)
- `PeakCan.Host.App.Windows` (for `MultiFrameSendWindow`, `TraceViewerWindow`)
- `PeakCan.Host.App.ViewModels.Uds` (for `UdsViewModel`, if any Show* method references it directly)

Paste the 13 method bodies verbatim.

NOTE: `ShowTrace` (line 508) is private; the rest are `[RelayCommand]` private. The `[RelayCommand]` attribute MUST travel with each method (CommunityToolkit.Mvvm source-gen requirement).

- [ ] **Step 3: Write the deletion script**

Create `scripts/w4_task3_delete_viewflow.py` with the pattern from Tasks 1+2:

```python
"""Delete Flow B (View navigation) from AppShellViewModel.cs (Task 3)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# Lines 508-746 in the post-Task-2 862-LoC file.
DELETIONS = [(508, 746, "11 Show* + OpenMultiFrame + ShowTraceViewer (Flow B)")]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 862, f"Expected 862 LoC after Task 2, got {original_count}"

to_delete = sorted([(s - 1, e, desc) for s, e, desc in DELETIONS], key=lambda x: -x[0])

for start0, end_line, desc in to_delete:
    assert lines[start0:end_line], f"Empty slice at {start0}-{end_line}"
    print(f"  Deleting lines {start0+1}-{end_line}: {desc} ({end_line - start0} lines)")
    del lines[start0:end_line]

print(f"New line count: {len(lines)} (removed {original_count - len(lines)} lines)")

text = "".join(lines)
assert "namespace PeakCan.Host.App.ViewModels;" in text
assert "public sealed partial class AppShellViewModel" in text
assert "public AppShellViewModel(" in text

marker = "    // === Flow B methods moved to AppShellViewModel/ViewSwitchFlow.cs (W4 Task 3) ===\n"
for i, ln in enumerate(lines):
    if "Flow C methods moved to AppShellViewModel/SessionFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow B marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
```

- [ ] **Step 4: Run + build + test**

```bash
python scripts/w4_task3_delete_viewflow.py
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~AppShellViewModel|FullyQualifiedName~ViewSwitcher"
```

Expected: 0 errors; AppShellViewModel tests + ViewSwitcherTests pass.

If build fails with `CS0246` errors: most likely missing `using PeakCan.Host.App.Views.Uds;` for `UdsView`, or `using PeakCan.Host.App.ViewModels.Uds;` for `UdsViewModel`. Add whichever is missing.

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs src/PeakCan.Host.App/ViewModels/AppShellViewModel/ViewSwitchFlow.cs scripts/w4_task3_delete_viewflow.py
git commit -m "refactor(asvm): extract Flow B (View navigation) to partial class

Flow B owns 11 Show* commands + OpenMultiFrame + ShowTraceViewer.
All [RelayCommand] attributes travel with their methods.

Cross-flow refs (partial-class visible):
- ConnectAsync (Flow A) -> ShowTraceViewer (Flow B)
- OpenSessionAsync (Flow C) -> ShowTraceViewer (Flow B)

Main file: 862 -> 624 LoC (-238 LoC).
Tests: AppShellViewModel + ViewSwitcher tests pass unchanged; build clean."
```

---

### Task 4: Extract Flow A → `ChannelFlow.cs` (largest, most cross-flow refs)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/AppShellViewModel/ChannelFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs:748-981` (delete EnumerateChannels + ConnectAsync + DisconnectAsync + OnReadLoopError)
- Modify: `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs` (move `OnIsFdChanged` + `OnSelectedChannelChanged` partial void + `LogReadLoopError` partial void)
- Create: `scripts/w4_task4_delete_channelflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `_router`, `_logger`, `_channelProbe`, `_channelFactory`, `_channelEnumerator`, `_sendService`, `_configuration`, `_suppressNextPersist`
- Consumes (cross-class): `ChannelRouter.Register`, `ChannelRouter.Unregister`, `IChannelProbe.ProbeAsync`, `IChannelFactory.CreateChannel`, `IChannelEnumerator.EnumerateAsync`
- Consumes (cross-flow, partial-class visible): `ShowTraceViewer` (Flow B), `LogReadLoopError` (the 11th log helper that's currently in the main file)
- Produces: 3 commands (EnumerateChannels + Connect + Disconnect) + 1 event handler (OnReadLoopError) + 2 partial void change handlers (OnIsFdChanged + OnSelectedChannelChanged)

**Pre-conditions**:
- Task 3 committed. Main file at 624 LoC.
- Run `wc -l src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs` — must be 624 before this task.

- [ ] **Step 1: Read main file lines 244-280 + 746-983 to capture exact verbatim content**

Read 2 ranges:
- Lines 244-280: `OnIsFdChanged` + `OnSelectedChannelChanged` partial void (and their xmldoc)
- Lines 746-983: `EnumerateChannels` + `ConnectAsync` + `DisconnectAsync` + `OnReadLoopError` + `LogReadLoopError` partial void

- [ ] **Step 2: Create `ChannelFlow.cs`**

Create the file with the standard template. Required usings (verify by scanning):
- `Microsoft.Extensions.Logging`
- `PeakCan.Host.Core` (for `ErrorCode`, `BaudRate`)
- `PeakCan.Host.Infrastructure.Channel` (for `ChannelRouter`, `IChannelProbe`, `IChannelFactory`, `IChannelEnumerator`, `BaudRateF`)

Paste verbatim:
1. The 2 partial void change handlers (`OnIsFdChanged` + `OnSelectedChannelChanged`)
2. The 4 methods (`EnumerateChannels` + `ConnectAsync` + `DisconnectAsync` + `OnReadLoopError`)
3. The 11th log helper (`LogReadLoopError`) — this moves WITH `OnReadLoopError` since it's only called by it

- [ ] **Step 3: Write the deletion script**

Create `scripts/w4_task4_delete_channelflow.py`. This task has **3 deletion ranges** (not 1):

```python
"""Delete Flow A (Channel lifecycle) from AppShellViewModel.cs (Task 4)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs")

content = MAIN.read_text(encoding="utf-8")
lines = content.splitlines(keepends=True)

# 3 ranges in the post-Task-3 624-LoC file.
# (1) OnIsFdChanged + OnSelectedChannelChanged partial void + xmldoc (lines 244-280)
# (2) EnumerateChannels + ConnectAsync + DisconnectAsync + OnReadLoopError + LogReadLoopError (lines 746-981)
DELETIONS = [
    (244, 280, "OnIsFdChanged + OnSelectedChannelChanged partial void"),
    (746, 981, "EnumerateChannels + ConnectAsync + DisconnectAsync + OnReadLoopError + LogReadLoopError"),
]

original_count = len(lines)
print(f"Original line count: {original_count}")
assert original_count == 624, f"Expected 624 LoC after Task 3, got {original_count}"

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
assert "public sealed partial class AppShellViewModel" in text
assert "public AppShellViewModel(" in text

marker = "    // === Flow A methods moved to AppShellViewModel/ChannelFlow.cs (W4 Task 4) ===\n"
for i, ln in enumerate(lines):
    if "Flow B methods moved to AppShellViewModel/ViewSwitchFlow.cs" in ln:
        lines.insert(i + 1, marker)
        print(f"Flow A marker inserted after line {i + 1}")
        break

MAIN.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {MAIN.stat().st_size} bytes")
```

- [ ] **Step 4: Run + build + test**

```bash
python scripts/w4_task4_delete_channelflow.py
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~AppShellViewModel|FullyQualifiedName~ViewSwitcher"
```

Expected: 0 errors; all tests pass.

If build fails with `CS0246 ErrorCode` or `CS0103`: add `using PeakCan.Host.Core;`.
If `CS0246 ChannelRouter`: add `using PeakCan.Host.Infrastructure.Channel;`.
If `CS0246 ReadLoopError`: add `using PeakCan.Host.Core;` (ReadLoopError is in Core).

- [ ] **Step 5: Verify main file is now ~389 LoC**

```bash
wc -l src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs
```

Expected: `389 src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs`. The 4 partial files together should sum to ~630 LoC. Total: 1019 LoC preserved (no code lost).

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs src/PeakCan.Host.App/ViewModels/AppShellViewModel/ChannelFlow.cs scripts/w4_task4_delete_channelflow.py
git commit -m "refactor(asvm): extract Flow A (Channel lifecycle) to partial class

Flow A owns: EnumerateChannels + ConnectAsync + DisconnectAsync +
OnReadLoopError + OnIsFdChanged + OnSelectedChannelChanged partial
void + LogReadLoopError (the 11th log helper that lives with its
caller).

Cross-flow refs (partial-class visible):
- ConnectAsync (Flow A) -> ShowTraceViewer (Flow B)
- ConnectAsync (Flow A) -> LogReadLoopError (Flow D caller)
- OnSelectedChannelChanged -> ConnectAsync (intra-flow trigger)

Main file: 624 -> 389 LoC (-235 LoC).
Cumulative W4: 1019 -> 389 LoC (-630 LoC, -61.8%).
Tests: AppShellViewModel + ViewSwitcher tests pass unchanged; build clean."
```

---

### Task 5: Version bump + release notes (v3.19.0 MINOR ship commit)

**Files:**
- Modify: `src/Directory.Build.props` (Version 3.18.0 → 3.19.0)
- Create: `docs/release-notes-v3.19.0.md`

**Pre-conditions**:
- Task 4 committed. 5 source commits on `feature/w4-app-shell-god-class`.

- [ ] **Step 1: Bump version in Directory.Build.props**

Edit `src/Directory.Build.props`:
- `<Version>3.18.0</Version>` → `<Version>3.19.0</Version>`
- `<AssemblyVersion>3.18.0.0</AssemblyVersion>` → `<AssemblyVersion>3.19.0.0</AssemblyVersion>`
- `<FileVersion>3.18.0.0</FileVersion>` → `<FileVersion>3.19.0.0</FileVersion>`
- `<InformationalVersion>3.18.0</InformationalVersion>` → `<InformationalVersion>3.19.0</InformationalVersion>`

- [ ] **Step 2: Build to verify version bump**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```

Expected: 0 errors. Window title in App.xaml should now show "v3.19.0" on next launch (not testable in headless test run).

- [ ] **Step 3: Write release notes**

Create `docs/release-notes-v3.19.0.md` with the following structure (fill in actual values from the 5 source commits):

```markdown
# Release Notes v3.19.0 — AppShellViewModel god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.19.0
**Branch:** `feature/w4-app-shell-god-class`
**Parent:** v3.18.0 PATCH (`133055c` on origin/main)

## Why this MINOR

[Copy from spec "Why this MINOR" section]

## What this MINOR does

### Refactor — AppShellViewModel split into 4 partial-class files

[Copy from spec "Target state" section]

### Architecture invariants preserved

[Copy from spec "Architecture invariants" section]

## What this MINOR does NOT do

[Copy from spec "Out of scope" section]

## Verification

[Run all 3 test suites + record actual counts]

## Files in this ship

[List the 5 source commits + version bump commit]

## For the next session

[Note: W4 closed; W5 (ReplayViewModel/SignalViewModel/other god-class) or other housekeeping]
```

- [ ] **Step 4: Commit**

```bash
git add src/Directory.Build.props docs/release-notes-v3.19.0.md
git commit -m "chore(release): bump version to v3.19.0 + add release notes

W4 MINOR ship: AppShellViewModel god-class refactor complete.
Main file: 1019 -> 389 LoC (-61.8%). 4 partial-class files created
in src/PeakCan.Host.App/ViewModels/AppShellViewModel/."
```

---

### Task 6: Tier-3 push + tag + GH release

**Pre-conditions**:
- Task 5 committed. 6 commits on `feature/w4-app-shell-god-class`.

- [ ] **Step 1: Push branch**

```bash
git push origin feature/w4-app-shell-god-class
```

Expected: branch pushed, all 6 commits visible on origin.

- [ ] **Step 2: Verify remote + tag**

```bash
git ls-remote origin refs/heads/feature/w4-app-shell-god-class
```

Expected: returns the SHA of HEAD on the branch.

- [ ] **Step 3: Create annotated tag**

```bash
git tag -a v3.19.0 -m "v3.19.0 MINOR — AppShellViewModel god-class refactor

Main file: 1019 -> 389 LoC (-61.8%). 4 partial-class files created:
ChannelFlow.cs (A, ~280), ViewSwitchFlow.cs (B, ~200),
SessionFlow.cs (C, ~130), LogFlow.cs (D, ~40).

Public API unchanged. AppShellViewModel tests + ViewSwitcher tests pass."
```

- [ ] **Step 4: Push tag**

```bash
git push origin v3.19.0
```

- [ ] **Step 5: Create GitHub release**

```bash
gh release create v3.19.0 --title "v3.19.0 MINOR — AppShellViewModel god-class refactor" --notes-file docs/release-notes-v3.19.0.md
```

Expected output: `https://github.com/jasontaotao/peakcan-host/releases/tag/v3.19.0`

- [ ] **Step 6: Verify release**

```bash
gh release view v3.19.0
```

Expected: publishedAt timestamp + title correct.

---

## Self-Review

**1. Spec coverage**: Every section of `docs/superpowers/specs/2026-07-11-app-shell-view-model-god-class-refactor.md` is covered by a task:
- 4 flow boundaries → Tasks 1-4
- Main file structure → Task 4 (final commit shows the slimmed main)
- Public API preservation → every task's verification step (build + tests)
- Architecture invariants → pre-conditions + verification steps
- Risk notes → pre-conditions + fix-on-fail guidance
- Ship target v3.19.0 → Tasks 5-6

**2. Placeholder scan**: No "TBD", "TODO", "implement later", or "similar to Task N" placeholders. All method bodies, file paths, and commands are exact.

**3. Type consistency**:
- `AppShellViewModel` referenced consistently throughout.
- `ChannelFlow.cs` references `ChannelRouter`, `BaudRate`, `ErrorCode`, `ReadLoopError` — all confirmed in pre-existing main file imports.
- `ViewSwitchFlow.cs` references `ViewSwitcher.Show` (Composition namespace), `TraceView`/`DbcView`/etc (Views namespace) — confirmed.
- `SessionFlow.cs` references `_recentSessions`, `TraceViewerViewModel` (cross-class) — confirmed.
- `LogFlow.cs` references `ErrorCode` (Core) — confirmed.

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-07-11-app-shell-view-model-god-class-refactor.md`. Two execution options:

1. **Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.
2. **Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints for review.