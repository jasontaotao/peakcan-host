# W39 Plan — DbcViewModel god-class refactor (24th overall)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.App/ViewModels/DbcViewModel.cs` from 208 LoC to ~105 LoC by extracting 3 NEW partials (`LoadingFlow.partial.cs` + `SearchFlow.partial.cs` + `ExportFlow.partial.cs`) into a new `DbcViewModel/` subdirectory, following the W3-W38 god-class refactor pattern.

**Architecture:** Sister of W22 RecentSessionsService + W34 DbcSendViewModel + W37 AscLocator + W38 ScriptViewModel subdirectory-partials pattern. Main stays with bindable state + ctor + LogOpenInvoked; 3 NEW partials host the 4 distinct responsibilities (loading + search + export + logging already in main). No public API change; no test change.

**Tech Stack:** C# .NET 10 / WPF / CommunityToolkit.Mvvm (partial-class source-gen friendly)

## Global Constraints

1. **Public API 100% preserved** — `DbcViewModel` class + `OpenCommand` + `ExportCsvCommand` (source-gen from [RelayCommand]) + `Messages` + `FilteredMessages` + `LoadedPath` + `Status` + `SearchText` + `TotalMessages` + `TotalSignals` properties remain publicly callable from XAML / DI / tests.
2. **Tests must pass without modification** — full suite 1456 PASS / 0 FAIL / 5 SKIP maintained; `DbcViewModelTests` continue passing without file edits.
3. **Partial keyword unchanged** — `public sealed partial class DbcViewModel : ObservableObject` (line 45) — already partial from prior PATCH; do NOT edit.
4. **LoC formula W8.5 D7 32-locked** — delta must EXACTLY match range deletion counts; re-grep boundaries before each task.
5. **Re-extract verbatim from HEAD** — no fabricated code; use `git show HEAD:src/...cs | sed -n '<range>p'` for each partial's content.
6. **W20 + W23 LESSON APPLIED** — boundary verification + struct-ctor verification + verbatim re-extract.
7. **W19 R1 LESSON ENHANCED** — re-grep post-T(N-1) boundaries BEFORE running each deletion script.
8. **W38 T2 LESSON** — be alert to non-contiguous-block deletions (Loading spans OpenAsync + OnLoaded + OnLoadFailed, with ctor subscription at L97-98 between ctor and methods). May need reverse-order 2-block deletion.
9. **Build + filter tests after each task** — catch extraction errors immediately.

---

## File Structure

```
src/PeakCan.Host.App/ViewModels/
├── DbcViewModel.cs                                  (~105 LoC after W39; was 208)
└── DbcViewModel/                                    (NEW subdirectory)
    ├── LoadingFlow.partial.cs                       (NEW ~54 LoC; OpenAsync + OnLoaded + OnLoadFailed)
    ├── SearchFlow.partial.cs                        (NEW ~20 LoC; OnSearchTextChanged + ApplyFilter)
    └── ExportFlow.partial.cs                        (NEW ~29 LoC; ExportCsv)
```

Each partial hosts one responsibility. Main stays with bindable state + ctor + logging helper.

---

### Task 0: Branch + spec verify + plan commit

**Files:**
- Verify: `docs/superpowers/specs/2026-07-17-w39-dbc-view-model-god-class-refactor.md` committed at `573e18d`
- Create: `docs/superpowers/plans/2026-07-17-w39-dbc-view-model-god-class-refactor.md`

- [ ] **Step 1: Verify spec is committed**

```bash
git log --oneline -5
```

Expected: `573e18d W39 spec: DbcViewModel god-class refactor (3 partials + 5-task roll-out, 24th overall)` visible.

- [ ] **Step 2: Verify branch + baseline tests**

```bash
git status
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~DbcViewModel" --logger "console;verbosity=minimal"
```

Expected: 0 errors, 0 warnings; DbcViewModel tests PASS (existing baseline count — verify via test count in output, expected ≥10 tests).

- [ ] **Step 3: Capture exact pre-W39 line ranges**

```bash
grep -n "OpenAsync\|OnLoaded\|OnLoadFailed\|OnSearchTextChanged\|ApplyFilter\|ExportCsv" src/PeakCan.Host.App/ViewModels/DbcViewModel.cs
```

Expected: exact line numbers printed; capture them as `LOAD_START..LOAD_END`, `SEARCH_START..SEARCH_END`, `EXPORT_START..EXPORT_END` for T1/T2/T3 range computation. Save to `scripts/w39_ranges.txt`.

- [ ] **Step 4: Commit plan**

```bash
git add docs/superpowers/plans/2026-07-17-w39-dbc-view-model-god-class-refactor.md
git commit -m "W39 plan: DbcViewModel god-class refactor (3 partials: LoadingFlow + SearchFlow + ExportFlow)"
```

---

### Task 1: LoadingFlow partial extraction (~54 LoC)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/DbcViewModel/LoadingFlow.partial.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/DbcViewModel.cs:101-154`

**Interfaces:**
- Consumes: `_svc` (DbcService) + `_signals` (SignalViewModel) + `_fileDialog` (IFileDialogService) — all stay in main; `LoadedPath` + `Status` + `Messages` + `FilteredMessages` + `_allMessages` + `TotalMessages` + `TotalSignals` properties — generated from main [ObservableProperty] / fields
- Produces: `OpenAsync` `[RelayCommand]` method + `OnLoaded(DbcDocument)` private method + `OnLoadFailed(PeakCan.Host.Core.Error)` private method

**SPECIAL NOTE — non-contiguous-block deletion**: The Loading range is L101-110 (OpenAsync) + L112-147 (OnLoaded) + L149-154 (OnLoadFailed). These 3 methods are contiguous (no interleaving code), so this is a SIMPLE single-block deletion (NOT non-contiguous like W38 T2). But ctor subscription at L97-98 is BETWEEN ctor and OpenAsync — that subscription STAYS in main ctor; only OpenAsync body moves.

- [ ] **Step 1: Re-grep boundaries BEFORE running script**

```bash
grep -n "OpenAsync\|OnLoaded\|OnLoadFailed" src/PeakCan.Host.App/ViewModels/DbcViewModel.cs
```

Expected: 3 matches — OpenAsync, OnLoaded, OnLoadFailed; capture exact start/end. Re-verify against `scripts/w39_ranges.txt` from Task 0 Step 3.

- [ ] **Step 2: Extract verbatim content from HEAD**

```bash
git show HEAD:src/PeakCan.Host.App/ViewModels/DbcViewModel.cs | sed -n '<LOAD_START>,<LOAD_END>p'
```

Expected: OpenAsync `[RelayCommand]` method + OnLoaded private method + OnLoadFailed private method. Save output to `scripts/w39_t1_loading_content.txt`.

- [ ] **Step 3: Create LoadingFlow.partial.cs**

Write `src/PeakCan.Host.App/ViewModels/DbcViewModel/LoadingFlow.partial.cs`:

```csharp
using System.IO;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Services;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class DbcViewModel
{
    // Flow: DBC loading (OpenAsync + event handlers).
    // Methods moved verbatim from DbcViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - Ctor -> _svc.DbcLoaded += OnLoaded; _svc.LoadFailed += OnLoadFailed (main ctor subscriptions)
    //   - OpenAsync -> _fileDialog.ShowOpenDialog (main field) + LogOpenInvoked (main [LoggerMessage])
    //   - OnLoaded -> Messages.Clear + FilteredMessages.Clear + _allMessages (main fields) + ApplyFilter (Flow B)
    //   - OnLoaded -> _signals.Reset + _signals.SetDbcService (main field)
    //   - OnLoaded -> TotalMessages + TotalSignals + Status (main [ObservableProperty])
    //   - OnLoadFailed -> Status (main [ObservableProperty])
    //
    // [RelayCommand] attribute MUST travel with OpenAsync method.

    [RelayCommand]
    private async Task OpenAsync()
    {
        var path = _fileDialog.ShowOpenDialog("DBC files (*.dbc)|*.dbc|All files|*.*");
        if (path is null) return;
        LoadedPath = path;
        Status = "Parsing...";
        LogOpenInvoked(_logger, path);
        await _svc.LoadAsync(path).ConfigureAwait(true);
    }

    private void OnLoaded(DbcDocument doc)
    {
        // DbcService.LoadAsync raises this event on its worker thread.
        // ObservableCollection<T>.CollectionChanged must fire on the UI
        // dispatcher when the collection is bound to an ItemsControl
        // (DataGrid, ListBox, etc.) — cross-thread mutation throws
        // NotSupportedException ("This type of CollectionView does not
        // support changes to its SourceCollection from a thread different
        // from the Dispatcher thread"). The marshal chokepoint lives in
        // DispatcherExtensions.RunOnUi; the previous Task 19 guard
        // (`appDispatcher == callingDispatcher`) was inverted and silently
        // skipped the hop in production. See
        // DispatcherExtensions.cs class doc-comment for the regression.
        ((Action)(() =>
        {
            Messages.Clear();
            _allMessages.Clear();
            FilteredMessages.Clear();
            foreach (var m in doc.Messages)
            {
                var vm = DbcMessageViewModel.From(m);
                Messages.Add(vm);
                _allMessages.Add(vm);
            }
            ApplyFilter();
            // Task 16: clear the decoded-signal table so stale entries
            // from a previous parse do not linger against a new DBC load.
            _signals.Reset();
            // v0.6.0: wire DBC service for value-table lookups.
            _signals.SetDbcService(_svc);
            TotalMessages = doc.Messages.Count;
            TotalSignals = doc.Messages.Sum(m => m.Signals.Count);
            var fileName = string.IsNullOrEmpty(LoadedPath) ? "(memory)" : Path.GetFileName(LoadedPath);
            Status = $"Loaded {TotalMessages} messages, {TotalSignals} signals from {fileName}";
        })).RunOnUi();
    }

    private void OnLoadFailed(PeakCan.Host.Core.Error error)
    {
        // Same dispatcher marshaling rationale as OnLoaded. Status is
        // bound to the UI; marshal via the same chokepoint.
        ((Action)(() => Status = $"FAIL: {error.Code} {error.Message}")).RunOnUi();
    }
}
```

- [ ] **Step 4: Verify build before deletion**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```

Expected: 0 errors (both partials declare the same partial methods — that's the whole point of partial classes; build succeeds with duplicate declarations).

- [ ] **Step 5: Delete from main via range removal script**

Write `scripts/w39_task1_delete_loadingflow.py`:

```python
#!/usr/bin/env python3
"""Delete LoadingFlow range from DbcViewModel.cs per W39 Task 1."""
from pathlib import Path

path = Path("src/PeakCan.Host.App/ViewModels/DbcViewModel.cs")
text = path.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

# Exact range from Task 0 Step 3 re-grep (placeholder; T1 implementer MUST re-grep)
START = <LOAD_START>
END = <LOAD_END>

# Convert to 0-indexed
deleted = lines[START - 1:END]
print(f"W39 T1: deleting {len(deleted)} lines ({START}..{END})")

# W19 R1 LESSON: 2/3 loose assertion — warn if delta off by ±3
EXPECTED_DELTA = 54
actual_delta = len(deleted)
if abs(actual_delta - EXPECTED_DELTA) > 3:
    print(f"WARNING: W19 R1 — expected ~{EXPECTED_DELTA} LoC, got {actual_delta}")

remaining = lines[:START - 1] + lines[END:]
path.write_text("".join(remaining), encoding="utf-8")
print(f"W39 T1: main LoC now {sum(1 for _ in path.read_text(encoding='utf-8').splitlines())}")
```

Run:

```bash
python scripts/w39_task1_delete_loadingflow.py
```

Expected: prints ~54 LoC deleted; main LoC count drops from 208 → ~154.

- [ ] **Step 6: Build + run DbcViewModel tests**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~DbcViewModel" --logger "console;verbosity=minimal"
```

Expected: 0 errors, 0 warnings; ALL DbcViewModel tests PASS (same count as Task 0 Step 2 baseline).

- [ ] **Step 7: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/DbcViewModel/LoadingFlow.partial.cs src/PeakCan.Host.App/ViewModels/DbcViewModel.cs scripts/w39_task1_delete_loadingflow.py
git commit -m "W39 T1: DbcViewModel LoadingFlow partial extraction (~54 LoC; OpenAsync + OnLoaded + OnLoadFailed)"
```

---

### Task 2: SearchFlow partial extraction (~20 LoC)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/DbcViewModel/SearchFlow.partial.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/DbcViewModel.cs` (post-T1 ranges for OnSearchTextChanged partial + ApplyFilter)

**Interfaces:**
- Consumes: `_allMessages` (List<DbcMessageViewModel>) + `FilteredMessages` ObservableCollection + `SearchText` property — all stay in main
- Produces: `OnSearchTextChanged(string)` partial void source-gen hook body + `ApplyFilter()` private method

- [ ] **Step 1: Re-grep post-T1 boundaries**

```bash
grep -n "OnSearchTextChanged\|ApplyFilter" src/PeakCan.Host.App/ViewModels/DbcViewModel.cs
```

Expected: capture exact start/end for OnSearchTextChanged + ApplyFilter. **CRITICAL**: re-verify because T1 changed line numbers.

- [ ] **Step 2: Extract verbatim content from HEAD (post-T1 commit)**

```bash
git show HEAD:src/PeakCan.Host.App/ViewModels/DbcViewModel.cs | sed -n '<SEARCH_START>,<SEARCH_END>p'
```

Expected: `OnSearchTextChanged` partial method body + `ApplyFilter()` private method.

- [ ] **Step 3: Create SearchFlow.partial.cs**

Write `src/PeakCan.Host.App/ViewModels/DbcViewModel/SearchFlow.partial.cs`:

```csharp
namespace PeakCan.Host.App.ViewModels;

public sealed partial class DbcViewModel
{
    // Flow: Search/filter (SearchText-driven FilteredMessages rebuild).
    // Methods moved verbatim from DbcViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - OnSearchTextChanged -> ApplyFilter (intra-flow)
    //   - OnLoaded (Flow A) -> ApplyFilter (cross-flow post-load rebuild)
    //   - ApplyFilter -> _allMessages + FilteredMessages (main fields) + SearchText (main [ObservableProperty])

    /// <summary>
    /// Called when <see cref="SearchText"/> changes. Filters the
    /// <see cref="FilteredMessages"/> collection.
    /// </summary>
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredMessages.Clear();
        var pattern = SearchText.AsSpan().Trim();
        foreach (var m in _allMessages)
        {
            if (pattern.Length == 0
                || m.Name.AsSpan().Contains(pattern, StringComparison.OrdinalIgnoreCase)
                || m.Sender.AsSpan().Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                FilteredMessages.Add(m);
            }
        }
    }
}
```

- [ ] **Step 4: Verify build before deletion**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```

Expected: 0 errors. If duplicate partial void declaration appears, the partial-class design is broken — verify file is in `DbcViewModel/` subdirectory with correct namespace.

- [ ] **Step 5: Delete from main via range removal script**

Write `scripts/w39_task2_delete_searchflow.py`:

```python
#!/usr/bin/env python3
"""Delete SearchFlow range from DbcViewModel.cs per W39 Task 2."""
from pathlib import Path

path = Path("src/PeakCan.Host.App/ViewModels/DbcViewModel.cs")
text = path.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

START = <SEARCH_START>
END = <SEARCH_END>

deleted = lines[START - 1:END]
print(f"W39 T2: deleting {len(deleted)} lines ({START}..{END})")

EXPECTED_DELTA = 20
actual_delta = len(deleted)
if abs(actual_delta - EXPECTED_DELTA) > 3:
    print(f"WARNING: W19 R1 — expected ~{EXPECTED_DELTA} LoC, got {actual_delta}")

remaining = lines[:START - 1] + lines[END:]
path.write_text("".join(remaining), encoding="utf-8")
print(f"W39 T2: main LoC now {sum(1 for _ in path.read_text(encoding='utf-8').splitlines())}")
```

Run:

```bash
python scripts/w39_task2_delete_searchflow.py
```

Expected: prints ~20 LoC deleted; main LoC count drops from ~154 → ~134.

- [ ] **Step 6: Build + run DbcViewModel tests**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~DbcViewModel" --logger "console;verbosity=minimal"
```

Expected: 0 errors, 0 warnings; ALL DbcViewModel tests PASS (same count as baseline).

- [ ] **Step 7: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/DbcViewModel/SearchFlow.partial.cs src/PeakCan.Host.App/ViewModels/DbcViewModel.cs scripts/w39_task2_delete_searchflow.py
git commit -m "W39 T2: DbcViewModel SearchFlow partial extraction (~20 LoC; OnSearchTextChanged + ApplyFilter)"
```

---

### Task 3: ExportFlow partial extraction (~29 LoC)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/DbcViewModel/ExportFlow.partial.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/DbcViewModel.cs` (post-T2 ranges for ExportCsv)

**Interfaces:**
- Consumes: `_allMessages` (List<DbcMessageViewModel>) — stays in main
- Produces: `ExportCsv()` `[RelayCommand]` method

- [ ] **Step 1: Re-grep post-T2 boundaries**

```bash
grep -n "ExportCsv" src/PeakCan.Host.App/ViewModels/DbcViewModel.cs
```

Expected: 1 match — ExportCsv method; capture exact start/end. **CRITICAL**: re-verify because T2 changed line numbers.

- [ ] **Step 2: Extract verbatim content from HEAD (post-T2 commit)**

```bash
git show HEAD:src/PeakCan.Host.App/ViewModels/DbcViewModel.cs | sed -n '<EXPORT_START>,<EXPORT_END>p'
```

Expected: `ExportCsv` `[RelayCommand]` method (~25 LoC).

- [ ] **Step 3: Create ExportFlow.partial.cs**

Write `src/PeakCan.Host.App/ViewModels/DbcViewModel/ExportFlow.partial.cs`:

```csharp
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.Input;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class DbcViewModel
{
    // Flow: CSV export of DBC messages.
    // Method moved verbatim from DbcViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - ExportCsv -> _allMessages (main field)
    //
    // [RelayCommand] attribute MUST travel with ExportCsv method.
    // Microsoft.Win32.SaveFileDialog is WPF-specific; this partial stays
    // in the App layer (not Core).

    /// <summary>
    /// Export DBC messages to a CSV file.
    /// </summary>
    [RelayCommand]
    private void ExportCsv()
    {
        if (_allMessages.Count == 0) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = "dbc-messages.csv",
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("ID,Name,DLC,Sender,Signals");
        foreach (var m in _allMessages)
        {
            sb.AppendLine(string.Join(',',
                m.Id,
                m.Name,
                m.Dlc,
                m.Sender,
                m.SignalCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), System.Text.Encoding.UTF8);
    }
}
```

- [ ] **Step 4: Verify build before deletion**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```

Expected: 0 errors.

- [ ] **Step 5: Delete from main via range removal script**

Write `scripts/w39_task3_delete_exportflow.py`:

```python
#!/usr/bin/env python3
"""Delete ExportFlow range from DbcViewModel.cs per W39 Task 3."""
from pathlib import Path

path = Path("src/PeakCan.Host.App/ViewModels/DbcViewModel.cs")
text = path.read_text(encoding="utf-8")
lines = text.splitlines(keepends=True)

START = <EXPORT_START>
END = <EXPORT_END>

deleted = lines[START - 1:END]
print(f"W39 T3: deleting {len(deleted)} lines ({START}..{END})")

EXPECTED_DELTA = 29
actual_delta = len(deleted)
if abs(actual_delta - EXPECTED_DELTA) > 3:
    print(f"WARNING: W19 R1 — expected ~{EXPECTED_DELTA} LoC, got {actual_delta}")

remaining = lines[:START - 1] + lines[END:]
path.write_text("".join(remaining), encoding="utf-8")
print(f"W39 T3: main LoC now {sum(1 for _ in path.read_text(encoding='utf-8').splitlines())}")
```

Run:

```bash
python scripts/w39_task3_delete_exportflow.py
```

Expected: prints ~29 LoC deleted; main LoC count drops from ~134 → ~105.

- [ ] **Step 6: Build + run DbcViewModel tests**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~DbcViewModel" --logger "console;verbosity=minimal"
```

Expected: 0 errors, 0 warnings; ALL DbcViewModel tests PASS (same count as baseline).

- [ ] **Step 7: Verify final LoC distribution**

```bash
wc -l src/PeakCan.Host.App/ViewModels/DbcViewModel.cs src/PeakCan.Host.App/ViewModels/DbcViewModel/*.cs
```

Expected: `DbcViewModel.cs` ~105 LoC; `LoadingFlow.partial.cs` ~54; `SearchFlow.partial.cs` ~20; `ExportFlow.partial.cs` ~29. Total ~208 LoC (no net change).

- [ ] **Step 8: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/DbcViewModel/ExportFlow.partial.cs src/PeakCan.Host.App/ViewModels/DbcViewModel.cs scripts/w39_task3_delete_exportflow.py
git commit -m "W39 T3: DbcViewModel ExportFlow partial extraction (~29 LoC; ExportCsv)"
```

---

### Task 4: v3.57.0 MINOR + release notes

**Files:**
- Create: `docs/release-notes-v3-57-0-minor.md`
- Modify: `src/Directory.Build.props` (bump Version + AssemblyVersion + FileVersion + InformationalVersion from 3.56.0 → 3.57.0)

- [ ] **Step 1: Write release notes**

Create `docs/release-notes-v3-57-0-minor.md` mirroring W38 release-notes format. Include:
- **Architecture milestones**: 24th god-class SHIPPED; 28th subdirectory-pattern deployment; 15th App/ViewModels.
- **Main file change**: 208 → ~105 LoC (-103 LoC, -50%); 3 NEW partials (LoadingFlow 54 + SearchFlow 20 + ExportFlow 29 = 103 LoC).
- **Subdirectory**: 3 NEW partials + main = ~208 LoC distributed across 4 files.
- **Cumulative LoC reduction (W3-W39)**: 32 god-classes ~-4,210 LoC + W39 DbcViewModel -103 LoC = **~ -4,313 LoC total**.
- **What stays the same**: public API surface (DbcViewModelTests unchanged); 5 [ObservableProperty] properties unchanged; 2 [RelayCommand] commands unchanged; DI registration unchanged.

- [ ] **Step 2: Bump version**

Modify `src/Directory.Build.props`:

```xml
<Version>3.57.0</Version>
<AssemblyVersion>3.57.0.0</AssemblyVersion>
<FileVersion>3.57.0.0</FileVersion>
<InformationalVersion>3.57.0</InformationalVersion>
```

- [ ] **Step 3: Full CI + release notes commit**

```bash
dotnet build src/PeakCan.Host.App/  # 0 errors, 0 warnings
dotnet test --no-restore --nologo -c Debug --logger "console;verbosity=minimal" -- xUnit.MaxParallelThreads=1  # full suite; 1456 PASS / 0 FAIL / 5 SKIP maintained
git add docs/release-notes-v3-57-0-minor.md src/Directory.Build.props
git commit -m "W39 release notes + version bump to v3.57.0 MINOR (DbcViewModel god-class refactor)"
```

---

### Task 5: Tier-3 ship

- [ ] **Step 1: Push branch + create PR**

```bash
git push -u origin feature/w39-dbc-view-model-god-class
gh pr create --title "v3.57.0 MINOR: DbcViewModel god-class refactor (24th overall; 3 NEW partials; main -50% LoC)" --body "24th god-class refactor per W39 plan. Sister pattern of W22+W34+W37+W38. Main 208→105 LoC across 3 NEW partials (LoadingFlow + SearchFlow + ExportFlow) in DbcViewModel/ subdirectory. Public API unchanged; tests unchanged."
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
git tag v3.57.0
git push origin v3.57.0
gh release create v3.57.0 --title "v3.57.0 MINOR: DbcViewModel god-class refactor" --notes-file docs/release-notes-v3-57-0-minor.md
```

---

## Sister-lesson candidates to monitor

| Lesson | Status | What W39 might observe |
|---|---|---|
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | 3/3 CONFIRMED (W21) | W39 6th god-class application (T1+T2+T3) — 25th application total |
| `add-partial-keyword-to-monolithic-class-before-extraction` | 3/3 CONFIRMED (W21) | Held (W39 class already partial) |
| `subdirectory-partials-pattern-empirical-27-precedents` | 3/3 CONFIRMED (W20) | W39 28th deployment, sister-of-W38 |
| `dbc-load-and-csv-export-are-separate-user-facing-actions-must-not-share-partial` | NEW 1/3 (W39) | W39 1st observation: ExportCsv 独立 partial |
| `dispatcher-marshal-pattern-must-stay-coupled-with-event-handler-in-same-partial` | NEW 1/3 (W39) | W39 1st observation: RunOnUi + OnLoaded + OnLoadFailed 同 partial |
| `search-filter-must-stay-coupled-with-observableproperty-hook-in-same-partial` | NEW 1/3 (W39) | W39 1st observation: OnSearchTextChanged partial void + ApplyFilter 同 partial |
| `2-non-contiguous-block-deletion-for-load-flow-with-ctor-subscription-between-methods` | NEW 1/3 (W39) | W39 1st observation: OpenAsync + OnLoaded + OnLoadFailed 之间夹有 ctor subscription |
| `wpf-savefiledialog-usage-keeps-exportflow-tightly-coupled-to-app-layer` | NEW 1/3 (W39) | W39 1st observation: ExportFlow 留在 App 层因为 WPF-specific API |
| `3-partial-subdirectory-pattern-empirical-w34-w37-w38-w39` | NEW 1/3 (W39) | W39 = 4th 3-partial subdirectory deployment |

---

## Verification

- `dotnet build src/`: 0 errors, 0 warnings
- `dotnet test --filter "FullyQualifiedName~DbcViewModel"`: same count PASS as baseline (≥10 tests)
- `dotnet test` (full solution single-threaded retry): 1456 PASS / 0 FAIL / 5 SKIP maintained
- `wc -l src/PeakCan.Host.App/ViewModels/DbcViewModel.cs` ≤ 110 LoC (target ~105)
- 3 NEW partial files in `DbcViewModel/` directory
- 5 `[ObservableProperty]` backing fields remain in main (W19+W22+W23+W34+W37+W38 sister)
- 2 `[RelayCommand]` annotated methods travel with their attributes (W19 sister)
- DI registration unchanged (`AddSingleton<DbcViewModel>()` factory in AppServicesFlow.cs)
- XAML bindings unchanged (all properties + 2 commands remain valid)
- Tag v3.57.0 + GH release published
- Branch deleted post-merge

## Out of scope (YAGNI)

- No public/internal API change.
- No test changes (DbcViewModelTests + sister tests pass without modification).
- No facade pattern (W3-W38 CONFIRMED direct partial-class visibility).
- No `[LoggerMessage]` partial duplication risk (1 LogOpenInvoked stays in main; safe).
- No `DbcService` refactor (separate concern; v3.4 god-class refactor already shipped).
- No `SignalViewModel` refactor (separate concern; already 1 partial FrameIngestFlow).
- No `DbcMessageViewModel.From()` refactor (helper factory; not god-class).