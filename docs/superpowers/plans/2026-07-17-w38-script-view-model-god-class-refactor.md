# W38 Plan — ScriptViewModel god-class refactor (23rd overall)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.App/ViewModels/ScriptViewModel.cs` from 225 LoC to ~95 LoC by extracting 3 NEW partials (`LoggingFlow.partial.cs` + `OutputFlow.partial.cs` + `ExecutionFlow.partial.cs`) into a new `ScriptViewModel/` subdirectory, following the W3-W37 god-class refactor pattern.

**Architecture:** Sister of W22 RecentSessionsService + W34 DbcSendViewModel + W37 AscLocator subdirectory-partials pattern. Main stays with bindable state + ctor + Dispose; 3 NEW partials host the 4 distinct responsibilities (logging + output buffering + execution). No public API change; no test change.

**Tech Stack:** C# .NET 10 / WPF / CommunityToolkit.Mvvm (partial-class source-gen friendly)

## Global Constraints

1. **Public API 100% preserved** — `ScriptViewModel` class + `Dispose()` + `OnWebView2InitFailed(Exception, string)` + all 5 properties + `RunCommand` + `StopCommand` + `ClearOutputCommand` + `MaxOutputLines` const remain publicly callable from XAML / DI / tests.
2. **Tests must pass without modification** — full suite 1456 PASS / 0 FAIL / 5 SKIP maintained; `ScriptViewModelTests` continue passing without file edits.
3. **Partial keyword unchanged** — `public sealed partial class ScriptViewModel : ObservableObject` (line 21) — already partial from prior PATCH; do NOT edit.
4. **LoC formula W8.5 D7 32-locked** — delta must EXACTLY match range deletion counts; re-grep boundaries before each task.
5. **Re-extract verbatim from HEAD** — no fabricated code; use `git show HEAD:src/...cs | sed -n '<range>p'` for each partial's content.
6. **W20 + W23 LESSON APPLIED** — boundary verification + struct-ctor verification + verbatim re-extract.
7. **W19 R1 LESSON ENHANCED** — re-grep post-T(N-1) boundaries BEFORE running each deletion script.
8. **Build + filter tests after each task** — catch extraction errors immediately.

---

## File Structure

```
src/PeakCan.Host.App/ViewModels/
├── ScriptViewModel.cs                              (~95 LoC after W38; was 225)
└── ScriptViewModel/                                (NEW subdirectory)
    ├── LoggingFlow.partial.cs                      (NEW ~30 LoC; 5 [LoggerMessage] + OnWebView2InitFailed)
    ├── OutputFlow.partial.cs                       (NEW ~60 LoC; buffer + flush + clear + MaxOutputLines const)
    └── ExecutionFlow.partial.cs                    (NEW ~40 LoC; RunAsync + Stop + CanRun + CanStop)
```

Each partial hosts one responsibility. Main stays with bindable state + ctor + Dispose.

---

### Task 0: Branch + spec verify + plan commit

**Files:**
- Verify: `docs/superpowers/specs/2026-07-17-w38-script-view-model-god-class-refactor.md` committed at `e0328ee`
- Create: `docs/superpowers/plans/2026-07-17-w38-script-view-model-god-class-refactor.md`

- [ ] **Step 1: Verify spec is committed**

```bash
git log --oneline -5
```

Expected: `e0328ee W38 spec: ScriptViewModel god-class refactor (3 partials + 5-task roll-out, 23rd overall)` visible.

- [ ] **Step 2: Verify branch + baseline tests**

```bash
git status
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~ScriptViewModel" --logger "console;verbosity=minimal"
```

Expected: 0 errors, 0 warnings; ScriptViewModel tests PASS (existing baseline count — verify via test count in output, expected ≥10 tests).

- [ ] **Step 3: Capture exact pre-W38 line ranges**

```bash
grep -n "LogScriptCompleted\|LogScriptFailed\|LogScriptException\|LogScriptStopped\|LogWebView2InitFailed\|OnWebView2InitFailed\|public const int MaxOutputLines\|Queue<ScriptOutputLine>\|object _bufferLock\|ClearOutput\|OnOutputReceived\|FlushOutputBuffer\|RunAsync\|CanRun\|CanStop\|private void Stop" src/PeakCan.Host.App/ViewModels/ScriptViewModel.cs
```

Expected: exact line numbers printed; capture them as `LOG_START..LOG_END`, `OUTPUT_START..OUTPUT_END`, `EXEC_START..EXEC_END` for T1/T2/T3 range computation. Save to `scripts/w38_ranges.txt`.

- [ ] **Step 4: Commit plan**

```bash
git add docs/superpowers/plans/2026-07-17-w38-script-view-model-god-class-refactor.md
git commit -m "W38 plan: ScriptViewModel god-class refactor (3 partials: LoggingFlow + OutputFlow + ExecutionFlow)"
```

---

### Task 1: LoggingFlow partial extraction (~30 LoC)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/ScriptViewModel/LoggingFlow.partial.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/ScriptViewModel.cs:196-224`

**Interfaces:**
- Consumes: `_logger` (ILogger<ScriptViewModel>) — from main ctor; `IsEditorReady` + `EditorError` properties — generated from main [ObservableProperty]
- Produces: 5 [LoggerMessage] partial methods + `OnWebView2InitFailed(Exception, string)` public API

- [ ] **Step 1: Re-grep boundaries BEFORE running script**

```bash
grep -n "LogScriptCompleted\|LogScriptFailed\|LogScriptException\|LogScriptStopped\|LogWebView2InitFailed\|OnWebView2InitFailed" src/PeakCan.Host.App/ViewModels/ScriptViewModel.cs
```

Expected: 5 [LoggerMessage] lines + 1 public OnWebView2InitFailed method; capture exact start/end line numbers. Re-verify against `scripts/w38_ranges.txt` from Task 0 Step 3.

- [ ] **Step 2: Extract verbatim content from HEAD**

```bash
git show HEAD:src/PeakCan.Host.App/ViewModels/ScriptViewModel.cs | sed -n '<LOG_START>,<LOG_END>p'
```

Expected: 5 [LoggerMessage] declarations + 1 OnWebView2InitFailed public method body. Save output to `scripts/w38_t1_logging_content.txt`.

- [ ] **Step 3: Create LoggingFlow.partial.cs**

Write `src/PeakCan.Host.App/ViewModels/ScriptViewModel/LoggingFlow.partial.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class ScriptViewModel
{
    // Flow: Logging helpers (v1.2.12 PATCH Item 7 + earlier).
    // Methods moved verbatim from ScriptViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - RunAsync -> LogScriptCompleted / LogScriptFailed / LogScriptException
    //   - Stop -> LogScriptStopped
    //   - OnWebView2InitFailed (public API for ScriptView.OnLoaded WebView2 init failures)

    [LoggerMessage(Level = LogLevel.Information, Message = "Script completed successfully")]
    private static partial void LogScriptCompleted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Script failed: {Error}")]
    private static partial void LogScriptFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Script execution exception")]
    private static partial void LogScriptException(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Script stopped by user")]
    private static partial void LogScriptStopped(ILogger logger);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Error, Message = "WebView2 init failed")]
    private static partial void LogWebView2InitFailed(ILogger logger, Exception ex);

    /// <summary>
    /// v1.2.12 PATCH Item 7 review I-2/I-3: invoked by ScriptView.OnLoaded
    /// when EnsureCoreWebView2Async or NavigateToString throws. Sets
    /// IsEditorReady = false + EditorError message AND logs the underlying
    /// exception via [LoggerMessage] LogWebView2InitFailed. Keeps the WPF
    /// view free of logger plumbing (DI-incompatible) while satisfying
    /// the spec's logging requirement.
    /// </summary>
    public void OnWebView2InitFailed(Exception ex, string message)
    {
        LogWebView2InitFailed(_logger, ex);
        IsEditorReady = false;
        EditorError = message;
    }
}
```

- [ ] **Step 4: Verify build before deletion**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```

Expected: 0 errors (both partials declare the same partial methods — that's the whole point of partial classes; build succeeds with duplicate declarations).

- [ ] **Step 5: Delete from main via range removal script**

Write `scripts/w38_task1_delete_loggingflow.py`:

```python
#!/usr/bin/env python3
"""Delete LoggingFlow range from ScriptViewModel.cs per W38 Task 1."""
import re
import sys
from pathlib import Path

path = Path("src/PeakCan.Host.App/ViewModels/ScriptViewModel.cs")
text = path.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

# Exact range from Task 0 Step 3 re-grep (placeholder; T1 implementer MUST re-grep)
START = <LOG_START>
END = <LOG_END>

# Convert to 0-indexed
deleted = lines[START - 1:END]
print(f"W38 T1: deleting {len(deleted)} lines ({START}..{END})")

# W19 R1 LESSON: 2/3 loose assertion — warn if delta off by ±3
EXPECTED_DELTA = 30
actual_delta = len(deleted)
if abs(actual_delta - EXPECTED_DELTA) > 3:
    print(f"WARNING: W19 R1 — expected ~{EXPECTED_DELTA} LoC, got {actual_delta}")

remaining = lines[:START - 1] + lines[END:]
path.write_text("".join(remaining), encoding="utf-8")
print(f"W38 T1: main LoC now {sum(1 for _ in path.read_text(encoding='utf-8').splitlines())}")
```

Run:

```bash
python scripts/w38_task1_delete_loggingflow.py
```

Expected: prints ~30 LoC deleted; main LoC count drops from 225 → ~195.

- [ ] **Step 6: Build + run ScriptViewModel tests**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~ScriptViewModel" --logger "console;verbosity=minimal"
```

Expected: 0 errors, 0 warnings; ALL ScriptViewModel tests PASS (same count as Task 0 Step 2 baseline).

- [ ] **Step 7: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/ScriptViewModel/LoggingFlow.partial.cs src/PeakCan.Host.App/ViewModels/ScriptViewModel.cs scripts/w38_task1_delete_loggingflow.py
git commit -m "W38 T1: ScriptViewModel LoggingFlow partial extraction (~30 LoC; 5 [LoggerMessage] + OnWebView2InitFailed)"
```

---

### Task 2: OutputFlow partial extraction (~60 LoC)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/ScriptViewModel/OutputFlow.partial.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/ScriptViewModel.cs` (post-T1 ranges for buffer fields + ClearOutput + OnOutputReceived + FlushOutputBuffer + MaxOutputLines const)

**Interfaces:**
- Consumes: `_outputBuffer` (Queue<ScriptOutputLine>) + `_bufferLock` (object) — moved from main; `OutputLines` ObservableCollection — stays in main
- Produces: `_outputBuffer` + `_bufferLock` field declarations + `MaxOutputLines` const + `ClearOutput` `[RelayCommand]` + `OnOutputReceived` + `FlushOutputBuffer`

- [ ] **Step 1: Re-grep post-T1 boundaries**

```bash
grep -n "Queue<ScriptOutputLine>\|object _bufferLock\|public const int MaxOutputLines\|ClearOutput\|OnOutputReceived\|FlushOutputBuffer" src/PeakCan.Host.App/ViewModels/ScriptViewModel.cs
```

Expected: capture exact start/end for buffer fields + const + ClearOutput + OnOutputReceived + FlushOutputBuffer. **CRITICAL**: re-verify because T1 changed line numbers.

- [ ] **Step 2: Extract verbatim content from HEAD (post-T1 commit)**

```bash
git show HEAD:src/PeakCan.Host.App/ViewModels/ScriptViewModel.cs | sed -n '<OUTPUT_START>,<OUTPUT_END>p'
```

Expected: `_outputBuffer` + `_bufferLock` field declarations + `MaxOutputLines` const + `ClearOutput` `[RelayCommand]` method + `OnOutputReceived` private method + `FlushOutputBuffer` private method body.

- [ ] **Step 3: Create OutputFlow.partial.cs**

Write `src/PeakCan.Host.App/ViewModels/ScriptViewModel/OutputFlow.partial.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class ScriptViewModel
{
    // Flow: Output buffering + flush (script engine output → UI ObservableCollection).
    // Methods + fields moved verbatim from ScriptViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - Ctor -> _outputBuffer + _bufferLock initializers (moved here)
    //   - Ctor -> _flushTimer.Tick += (_, _) => FlushOutputBuffer (intra-flow)
    //   - OnOutputReceived <- _engine.OutputReceived (ctor subscription, main)
    //   - FlushOutputBuffer -> OutputLines.Add / .RemoveAt (main [ObservableProperty] collection)
    //   - ClearOutput -> OutputLines.Clear + _outputBuffer.Clear (intra-flow)

    // Buffer for output lines from the script engine.
    private readonly Queue<ScriptOutputLine> _outputBuffer = new();
    private readonly object _bufferLock = new();

    /// <summary>Maximum output lines to keep in the UI.</summary>
    public const int MaxOutputLines = 1000;

    /// <summary>Clear the output panel.</summary>
    [RelayCommand]
    private void ClearOutput()
    {
        OutputLines.Clear();
        lock (_bufferLock)
        {
            _outputBuffer.Clear();
        }
    }

    /// <summary>
    /// Called by the script engine when output is produced.
    /// Queues the line for UI flush.
    /// </summary>
    private void OnOutputReceived(ScriptOutputLine line)
    {
        lock (_bufferLock)
        {
            _outputBuffer.Enqueue(line);
        }
    }

    /// <summary>
    /// Flush buffered output lines to the UI collection.
    /// Called at 30 Hz by the dispatcher timer.
    /// </summary>
    private void FlushOutputBuffer()
    {
        ScriptOutputLine[] lines;
        lock (_bufferLock)
        {
            if (_outputBuffer.Count == 0) return;
            lines = [.. _outputBuffer];
            _outputBuffer.Clear();
        }

        foreach (var line in lines)
        {
            var prefix = line.Level switch
            {
                ScriptOutputLevel.Warning => "⚠ ",
                ScriptOutputLevel.Error => "❌ ",
                _ => ""
            };

            var formatted = $"[{line.Timestamp:HH:mm:ss}] {prefix}{line.Message}";
            OutputLines.Add(formatted);

            // Trim old lines if we exceed the limit.
            while (OutputLines.Count > MaxOutputLines)
            {
                OutputLines.RemoveAt(0);
            }
        }
    }
}
```

- [ ] **Step 4: Verify build before deletion**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```

Expected: 0 errors. If duplicate field declarations appear, the partial-class design is broken — verify file is in `ScriptViewModel/` subdirectory with correct namespace.

- [ ] **Step 5: Delete from main via range removal script**

Write `scripts/w38_task2_delete_outputflow.py`:

```python
#!/usr/bin/env python3
"""Delete OutputFlow range from ScriptViewModel.cs per W38 Task 2."""
import sys
from pathlib import Path

path = Path("src/PeakCan.Host.App/ViewModels/ScriptViewModel.cs")
text = path.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

START = <OUTPUT_START>
END = <OUTPUT_END>

deleted = lines[START - 1:END]
print(f"W38 T2: deleting {len(deleted)} lines ({START}..{END})")

EXPECTED_DELTA = 60
actual_delta = len(deleted)
if abs(actual_delta - EXPECTED_DELTA) > 3:
    print(f"WARNING: W19 R1 — expected ~{EXPECTED_DELTA} LoC, got {actual_delta}")

remaining = lines[:START - 1] + lines[END:]
path.write_text("".join(remaining), encoding="utf-8")
print(f"W38 T2: main LoC now {sum(1 for _ in path.read_text(encoding='utf-8').splitlines())}")
```

Run:

```bash
python scripts/w38_task2_delete_outputflow.py
```

Expected: prints ~60 LoC deleted; main LoC count drops from ~195 → ~135.

- [ ] **Step 6: Build + run ScriptViewModel tests**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~ScriptViewModel" --logger "console;verbosity=minimal"
```

Expected: 0 errors, 0 warnings; ALL ScriptViewModel tests PASS (same count as baseline).

- [ ] **Step 7: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/ScriptViewModel/OutputFlow.partial.cs src/PeakCan.Host.App/ViewModels/ScriptViewModel.cs scripts/w38_task2_delete_outputflow.py
git commit -m "W38 T2: ScriptViewModel OutputFlow partial extraction (~60 LoC; buffer + flush + clear)"
```

---

### Task 3: ExecutionFlow partial extraction (~40 LoC)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/ScriptViewModel/ExecutionFlow.partial.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/ScriptViewModel.cs` (post-T2 ranges for RunAsync + Stop + CanRun + CanStop)

**Interfaces:**
- Consumes: `_engine` (ScriptEngine) + `_logger` (ILogger<ScriptViewModel>) — both stay in main; `ScriptText` + `IsRunning` + `StatusText` — generated from main [ObservableProperty]
- Produces: `RunAsync` `[RelayCommand]` + `Stop` `[RelayCommand]` + `CanRun()` + `CanStop()` private methods

- [ ] **Step 1: Re-grep post-T2 boundaries**

```bash
grep -n "RunAsync\|CanRun\|private void Stop\|CanStop" src/PeakCan.Host.App/ViewModels/ScriptViewModel.cs
```

Expected: 4 matches — RunAsync, CanRun, Stop, CanStop; capture exact start/end. **CRITICAL**: re-verify because T2 changed line numbers.

- [ ] **Step 2: Extract verbatim content from HEAD (post-T2 commit)**

```bash
git show HEAD:src/PeakCan.Host.App/ViewModels/ScriptViewModel.cs | sed -n '<EXEC_START>,<EXEC_END>p'
```

Expected: `RunAsync` `[RelayCommand]` method (~34 LoC, LARGEST) + `CanRun()` + `Stop` `[RelayCommand]` method + `CanStop()`.

- [ ] **Step 3: Create ExecutionFlow.partial.cs**

Write `src/PeakCan.Host.App/ViewModels/ScriptViewModel/ExecutionFlow.partial.cs`:

```csharp
using CommunityToolkit.Mvvm.Input;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class ScriptViewModel
{
    // Flow: Script execution (Run + Stop commands).
    // Methods moved verbatim from ScriptViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - RunAsync -> _engine.RunAsync (main field)
    //   - RunAsync -> LogScriptCompleted / LogScriptFailed / LogScriptException (Flow: Logging)
    //   - Stop -> _engine.Stop (main field)
    //   - Stop -> LogScriptStopped (Flow: Logging)
    //   - CanRun / CanStop -> IsRunning (main [ObservableProperty] property)
    //
    // [RelayCommand] attributes MUST travel with their methods.

    /// <summary>Run the current script.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(ScriptText)) return;

        IsRunning = true;
        StatusText = "Running...";
        OutputLines.Clear();

        try
        {
            var result = await _engine.RunAsync(ScriptText).ConfigureAwait(true);

            if (result.Success)
            {
                StatusText = "Completed";
                LogScriptCompleted(_logger);
            }
            else
            {
                StatusText = $"Error: {result.Error}";
                LogScriptFailed(_logger, result.Error ?? "Unknown error");
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            LogScriptException(_logger, ex);
        }
        finally
        {
            IsRunning = false;
        }
    }

    private bool CanRun() => !IsRunning;

    /// <summary>Stop the running script.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        _engine.Stop();
        StatusText = "Stopped";
        IsRunning = false;
        LogScriptStopped(_logger);
    }

    private bool CanStop() => IsRunning;
}
```

- [ ] **Step 4: Verify build before deletion**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```

Expected: 0 errors.

- [ ] **Step 5: Delete from main via range removal script**

Write `scripts/w38_task3_delete_executionflow.py`:

```python
#!/usr/bin/env python3
"""Delete ExecutionFlow range from ScriptViewModel.cs per W38 Task 3."""
from pathlib import Path

path = Path("src/PeakCan.Host.App/ViewModels/ScriptViewModel.cs")
text = path.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

START = <EXEC_START>
END = <EXEC_END>

deleted = lines[START - 1:END]
print(f"W38 T3: deleting {len(deleted)} lines ({START}..{END})")

EXPECTED_DELTA = 40
actual_delta = len(deleted)
if abs(actual_delta - EXPECTED_DELTA) > 3:
    print(f"WARNING: W19 R1 — expected ~{EXPECTED_DELTA} LoC, got {actual_delta}")

remaining = lines[:START - 1] + lines[END:]
path.write_text("".join(remaining), encoding="utf-8")
print(f"W38 T3: main LoC now {sum(1 for _ in path.read_text(encoding='utf-8').splitlines())}")
```

Run:

```bash
python scripts/w38_task3_delete_executionflow.py
```

Expected: prints ~40 LoC deleted; main LoC count drops from ~135 → ~95.

- [ ] **Step 6: Build + run ScriptViewModel tests**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~ScriptViewModel" --logger "console;verbosity=minimal"
```

Expected: 0 errors, 0 warnings; ALL ScriptViewModel tests PASS (same count as baseline).

- [ ] **Step 7: Verify final LoC distribution**

```bash
wc -l src/PeakCan.Host.App/ViewModels/ScriptViewModel.cs src/PeakCan.Host.App/ViewModels/ScriptViewModel/*.cs
```

Expected: `ScriptViewModel.cs` ~95 LoC; `LoggingFlow.partial.cs` ~30; `OutputFlow.partial.cs` ~60; `ExecutionFlow.partial.cs` ~40. Total ~225 LoC (no net change).

- [ ] **Step 8: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/ScriptViewModel/ExecutionFlow.partial.cs src/PeakCan.Host.App/ViewModels/ScriptViewModel.cs scripts/w38_task3_delete_executionflow.py
git commit -m "W38 T3: ScriptViewModel ExecutionFlow partial extraction (~40 LoC; RunAsync + Stop + CanExecute guards)"
```

---

### Task 4: v3.56.0 MINOR + release notes

**Files:**
- Create: `docs/release-notes-v3-56-0-minor.md`
- Modify: `src/Directory.Build.props` (bump Version + AssemblyVersion + FileVersion + InformationalVersion from 3.55.0 → 3.56.0)

- [ ] **Step 1: Write release notes**

Create `docs/release-notes-v3-56-0-minor.md` mirroring W37 release-notes format. Include:
- **Architecture milestones**: 23rd god-class SHIPPED; 26th subdirectory-pattern deployment; 13th App/ViewModels.
- **Main file change**: 225 → ~95 LoC (-130 LoC, -58%); sister of W37 (-58.2%) exceeds plan's -55% target.
- **Subdirectory**: 3 NEW partials + main = ~225 LoC distributed across 4 files.
- **Cumulative LoC reduction (W3-W38)**: 30 god-classes -3,787 LoC (W3-W37) + W38 ScriptViewModel -130 LoC = **-3,917 LoC total**.
- **What stays the same**: public API surface (ScriptViewModelTests unchanged); 5 [ObservableProperty] properties unchanged; 3 [RelayCommand] commands unchanged; DI registration unchanged.

- [ ] **Step 2: Bump version**

Modify `src/Directory.Build.props`:

```xml
<Version>3.56.0</Version>
<AssemblyVersion>3.56.0.0</AssemblyVersion>
<FileVersion>3.56.0.0</FileVersion>
<InformationalVersion>3.56.0</InformationalVersion>
```

- [ ] **Step 3: Full CI + release notes commit**

```bash
dotnet build src/  # 0 errors, 0 warnings across all 3 src projects
dotnet test --no-restore --nologo -c Debug --logger "console;verbosity=minimal"  # full suite; 1456 PASS / 0 FAIL / 5 SKIP maintained
git add docs/release-notes-v3-56-0-minor.md src/Directory.Build.props
git commit -m "W38 release notes + version bump to v3.56.0 MINOR (ScriptViewModel god-class refactor)"
```

---

### Task 5: Tier-3 ship

- [ ] **Step 1: Push branch + create PR**

```bash
git push -u origin feature/w38-script-view-model-god-class
gh pr create --title "v3.56.0 MINOR: ScriptViewModel god-class refactor (23rd overall; 3 NEW partials; main -58% LoC)" --body "23rd god-class refactor per W38 plan. Sister pattern of W22+W34+W37. Main 225→95 LoC across 3 NEW partials (LoggingFlow + OutputFlow + ExecutionFlow) in ScriptViewModel/ subdirectory. Public API unchanged; tests unchanged."
```

- [ ] **Step 2: Squash merge + delete branch**

```bash
gh pr merge --squash --delete-branch
```

- [ ] **Step 3: Force-correct + tag + release**

```bash
git fetch origin main
git merge origin/main --no-edit
git reset --hard <squash-commit>
git tag v3.56.0
git push origin v3.56.0
gh release create v3.56.0 --title "v3.56.0 MINOR: ScriptViewModel god-class refactor" --notes-file docs/release-notes-v3-56-0-minor.md
```

---

## Sister-lesson candidates to monitor

| Lesson | Status | What W38 might observe |
|---|---|---|
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | 3/3 CONFIRMED (W21) | W38 5th god-class application (T1+T2+T3) — 23rd application total |
| `add-partial-keyword-to-monolithic-class-before-extraction` | 3/3 CONFIRMED (W21) | Held (W38 class already partial) |
| `subdirectory-partials-pattern-empirical-26-precedents` | 3/3 CONFIRMED (W20) | W38 26th deployment, sister-of-W37 |
| `output-buffer-with-dispatcher-timer-must-stay-coupled-in-same-partial` | NEW 1/3 (W38) | W38 1st observation: buffer + lock + flush + clear 同 partial |
| `largest-method-can-move-when-flow-is-orchestrator-with-async-await-chain` | NEW 1/3 (W38) | W38 1st observation: RunAsync (~34 LoC) async-await chain moved |
| `canexecute-guards-can-move-to-same-partial-as-their-relaycommand-methods` | NEW 1/3 (W38) | W38 1st observation: CanRun + CanStop 同 partial with Run + Stop |
| `webview2-init-failure-handler-must-stay-coupled-with-logger-message-partials-in-same-partial` | NEW 1/3 (W38) | W38 1st observation: OnWebView2InitFailed + 5 [LoggerMessage] 同 partial |
| `const-without-modifier-can-move-to-flow-partial-when-class-has-2-plus-flows-using-it` | NEW 1/3 (W38) | W38 1st observation: MaxOutputLines const moved |
| `3-partial-subdirectory-pattern-empirical-w34-w37-w38` | NEW 1/3 (W38) | W38 = 3rd 3-partial subdirectory deployment |

---

## Verification

- `dotnet build src/`: 0 errors, 0 warnings
- `dotnet test --filter "FullyQualifiedName~ScriptViewModel"`: same count PASS as baseline (≥10 tests)
- `dotnet test` (full solution): 1456 PASS / 0 FAIL / 5 SKIP maintained
- `wc -l src/PeakCan.Host.App/ViewModels/ScriptViewModel.cs` ≤ 100 LoC (target ~95)
- 3 NEW partial files in `ScriptViewModel/` directory
- 5 `[ObservableProperty]` backing fields remain in main (W19+W22+W23+W34+W37 sister)
- 3 `[RelayCommand]` annotated methods remain in main attribute; methods move to ExecutionFlow (sister of W19)
- DI registration unchanged (`AddSingleton<ScriptViewModel>()` factory in AppServicesFlow.cs)
- XAML bindings unchanged (5 properties + RunCommand + StopCommand + ClearOutputCommand all remain valid)
- Tag v3.56.0 + GH release published
- Branch deleted post-merge

## Out of scope (YAGNI)

- No public/internal API change.
- No test changes (ScriptViewModelTests + sister tests pass without modification).
- No facade pattern (W3-W37 CONFIRMED direct partial-class visibility).
- No `[LoggerMessage]` partial duplication risk (5 distinct EventId values; safe).
- No `ScriptEngine` / `ScriptEnvironment` refactor (separate concern).
- No `ScriptVariableFlow` extraction (no such code yet; YAGNI).
- No `ConsoleFlow` extraction (FlushOutputBuffer + ClearOutput cover it; YAGNI).