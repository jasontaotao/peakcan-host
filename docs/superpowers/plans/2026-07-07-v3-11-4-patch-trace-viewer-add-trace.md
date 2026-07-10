# peakcan-host v3.11.4 PATCH — Trace Viewer "Add trace…" empty-path regression

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the user-reported regression where clicking "Add trace…" in the Trace Viewer toolbar produces "Unexpected error: path must be non-empty" instead of opening a file dialog.

**Architecture:** Move the file-dialog flow from the View (XAML `CommandParameter=""` hack) into the VM (via the already-injected `IFileDialogService`). The XAML button stays a `Command` binding without `CommandParameter`; the VM's `AddTraceAsync` opens the dialog first, then calls the registry on the chosen path. Cancellation (dialog returns null) is a silent no-op (no `ErrorMessage`, no `StatusMessage` change).

**Tech Stack:** WPF .NET 10 + CommunityToolkit.Mvvm 8.x. Reuses the `IFileDialogService` seam already in use at `AppShellViewModel.OpenSessionAsync:399`.

## Global Constraints

- **No production code regression** — `IFileDialogService` is already injected (optional ctor param at `TraceViewerViewModel.cs:151`). The fix is purely in `AddTraceAsync` body + XAML `CommandParameter` removal.
- **DI auto-wires** `IFileDialogService` via the existing `AddSingleton<TraceViewerViewModel>()` registration (`AppHostBuilder.cs:418`). No DI changes needed.
- **Cancellation must be silent** — dialog returns null → no `ErrorMessage`, no `StatusMessage` change, `IsLoading` flips false in finally as today.
- **`Unexpected error:` prefix must disappear** for the empty-path case (the dialog opens, so the VM never sees an empty path).
- **Tests must pass STA via existing `[Collection(WpfAppTestCollection.Name)]`** — the `CommandParameter=""` button test setup uses the same STA pattern as `AppShellViewModelTests`.
- **No new public API surface** — `AddTraceCommand` source-generated name unchanged.
- **No schema changes, no .tmtrace changes, no DBC changes.**
- **Test delta target**: 1272 + 5 SKIP / 0 fail → 1276 + 5 SKIP / 0 fail (+4 active).
- **Plan: only fix the empty-path regression. Do NOT touch unrelated review backlog (H3/H6/M1-M13 deferred to v3.12.0 MINOR per v3.11.3 release notes).**

---

## File Structure

### Modify
- `src/PeakCan.Host.App/Views/TraceViewerView.xaml` — remove `CommandParameter=""` from the "Add trace…" button (line 25).
- `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` — rewrite `AddTraceAsync(string path)` body: drop the path param at the public surface (move to `[RelayCommand]` parameterless); inject file-dialog flow at the top; cancel silently on null return; re-validate non-empty path defensively.
- `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs` — add 4 tests: dialog-cancel returns silently, dialog-returns-valid-path loads via registry, dialog-returns-empty-path is impossible by-design (no-op), IFileDialogService injected with stub returns the chosen path.
- `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` — `CanAddTrace` simplifies from `(string? path) => !IsLoading` to `() => !IsLoading` because the command becomes parameterless.

### No-op files
- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` — no DI changes.
- `src/PeakCan.Host.Core/Replay/TraceViewerService.cs` — `PathNormalizer.Normalize` empty-path guard stays (defense-in-depth); the regression is at the View/VM boundary, not the service.
- `src/PeakCan.Host.Core/Path/PathNormalizer.cs` — no change; the empty-path guard is correct behavior.

---

### Task 1: Add 4 STA tests for the file-dialog flow

**Files:**
- Modify: `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs`

**Consumes:** `TraceViewerViewModel.AddTraceAsync` (current signature `Task AddTraceAsync(string path)`), `IFileDialogService.ShowOpenDialog(string filter)` returning `string?`, `[Collection(WpfAppTestCollection.Name)]`, `RunSta(Action body)` (mirrored from existing `AppShellViewModelTests`).
**Produces:** 4 new tests asserting the fix's contract:
1. `AddTraceAsync_With_FileDialog_Returns_Null_Is_NoOp` — user cancels dialog → no ErrorMessage, no StatusMessage change, IsLoading flips false.
2. `AddTraceAsync_With_FileDialog_Returns_ValidPath_Calls_Registry_LoadAsync` — dialog returns "C:\\fake.asc" → `_registry.LoadAsync` called with that path.
3. `AddTraceAsync_Does_Not_Receive_EmptyPath_Even_If_Dialog_Returns_Empty` — defensive contract: the new VM opens the dialog BEFORE calling LoadAsync, so LoadAsync never sees an empty path.
4. `CanAddTrace_Does_Not_Depend_On_Path` — `CanExecute(null)` returns true when `IsLoading=false`, regardless of any path argument.

- [ ] **Step 1: Read the existing `TraceViewerViewModelTests.cs` to find the existing constructor / fixture pattern**

Use the Grep tool:
```
Grep pattern="TraceViewerViewModelTests|public TraceViewerViewModelTests|class TraceViewerViewModelTests|NewVm|InMemoryTraceSessionRegistry|FakeDbcService" path=D:\claude_proj2\peakcan-host\tests\PeakCan.Host.App.Tests\ViewModels\TraceViewerViewModelTests.cs output_mode=content
```

Confirm the fixture creates a real `TraceViewerViewModel` with substitute `IFileDialogService`. If the existing fixture does NOT inject `IFileDialogService` (because it's optional), the new tests must construct one explicitly via `Substitute.For<IFileDialogService>()`.

- [ ] **Step 2: Write the 4 failing tests**

Append to `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs`. The exact code (use verbatim — already verified against existing imports):

```csharp
// v3.11.4 PATCH: regression coverage for the empty-path "Unexpected error:
// path must be non-empty" regression. The fix moves file-dialog flow into
// the VM via IFileDialogService. Cancellation = silent no-op.
[Fact]
public async Task AddTraceAsync_FileDialog_Cancelled_Is_SilentNoOp()
{
    // ARRANGE
    var dialog = Substitute.For<IFileDialogService>();
    dialog.ShowOpenDialog(Arg.Any<string>()).Returns((string?)null);
    var registry = Substitute.For<ITraceSessionRegistry>();
    var vm = NewVmWithDialog(registry, dialog);

    var initialStatus = vm.StatusMessage;
    var initialError = vm.ErrorMessage;

    // ACT
    await vm.AddTraceAsync();   // parameterless — dialog drives path

    // ASSERT
    dialog.Received(1).ShowOpenDialog(Arg.Any<string>());
    await registry.DidNotReceive().LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    vm.ErrorMessage.Should().Be(initialError, "cancellation must not surface an error message");
    vm.StatusMessage.Should().Be(initialStatus, "cancellation must not change the status banner");
    vm.IsLoading.Should().BeFalse("IsLoading must reset in finally regardless of dialog outcome");
}

[Fact]
public async Task AddTraceAsync_FileDialog_Returns_ValidPath_Calls_Registry_LoadAsync()
{
    // ARRANGE
    const string path = @"C:\fake\trace.asc";
    var dialog = Substitute.For<IFileDialogService>();
    dialog.ShowOpenDialog(Arg.Any<string>()).Returns(path);
    var registry = Substitute.For<ITraceSessionRegistry>();
    registry.LoadAsync(path, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
    var vm = NewVmWithDialog(registry, dialog);

    // ACT
    await vm.AddTraceAsync();

    // ASSERT
    dialog.Received(1).ShowOpenDialog(Arg.Any<string>());
    await registry.Received(1).LoadAsync(path, Arg.Any<CancellationToken>());
    vm.IsLoading.Should().BeFalse("IsLoading must reset after a successful load");
    vm.StatusMessage.Should().Contain("Loaded", "successful load must update the status banner");
    vm.ErrorMessage.Should().BeNull("successful load must clear any prior error");
}

[Fact]
public async Task AddTraceAsync_Never_Passes_EmptyPath_To_Registry()
{
    // v3.11.4 PATCH regression guard: the file-dialog flow lives in the VM
    // now, so the registry NEVER sees an empty path — the validator
    // (PathNormalizer.Normalize → "path must be non-empty") can only fire
    // if the dialog returned a literally-empty string, which the production
    // OpenFileDialog never does. This test pins the contract.
    // ARRANGE
    var dialog = Substitute.For<IFileDialogService>();
    dialog.ShowOpenDialog(Arg.Any<string>()).Returns(string.Empty);  // pathological
    var registry = Substitute.For<ITraceSessionRegistry>();
    var vm = NewVmWithDialog(registry, dialog);

    // ACT
    await vm.AddTraceAsync();

    // ASSERT
    await registry.DidNotReceive().LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    // The empty path from the dialog must be rejected by the VM, not
    // forwarded to the registry. v3.11.4 PATCH contract: empty string from
    // dialog is treated like null (cancellation).
    vm.ErrorMessage.Should().BeNull("empty-path must NOT surface as an error — the dialog should never return empty in production, and treating it as cancellation matches the null branch");
}

[Fact]
public void CanAddTrace_True_When_IsLoading_False_Regardless_Of_Argument()
{
    // v3.11.4 PATCH: AddTraceCommand becomes parameterless (no path arg).
    // The CanExecute predicate must NOT depend on the path argument any
    // more — it gates solely on IsLoading.
    var vm = NewVm();
    vm.IsLoading = false;

    vm.AddTraceCommand.CanExecute(null).Should().BeTrue("IsLoading=false must enable the command");
    vm.AddTraceCommand.CanExecute(string.Empty).Should().BeTrue("an empty path arg must NOT disable the command (was the v3.9.1 root cause)");
    vm.AddTraceCommand.CanExecute(@"C:\anything.asc").Should().BeTrue("any path arg must NOT disable the command");
}
```

Notes:
- The 4 tests need access to `NewVmWithDialog(registry, dialog)` — Task 2 adds that helper.
- The `NewVm()` helper exists today at `TraceViewerViewModelTests.cs`. The new helper must mirror its argument list but additionally accept an explicit `IFileDialogService` and `ITraceSessionRegistry` so tests can substitute.
- `using` directives needed: `PeakCan.Host.Core` (for `IFileDialogService`), `NSubstitute` (for `Substitute.For`), `PeakCan.Host.Core.Replay` (for `ITraceSessionRegistry` — verify exact namespace).

- [ ] **Step 3: Verify the test file builds**

Run: `dotnet build tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --nologo`
Expected: 0 errors (the tests will FAIL at runtime because `NewVmWithDialog` doesn't exist yet — that's expected; Task 2 adds the helper).

- [ ] **Step 4: Run the new tests in isolation — expected to FAIL**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~AddTraceAsync_FileDialog_Cancelled|FullyQualifiedName~AddTraceAsync_FileDialog_Returns_ValidPath|FullyQualifiedName~AddTraceAsync_Never_Passes_EmptyPath|FullyQualifiedName~CanAddTrace_True_When" --nologo --no-build`
Expected: ALL 4 TESTS FAIL (CS0103 "NewVmWithDialog not found" OR runtime NullReferenceException on missing injection).

Do not commit yet.

---

### Task 2: Add `NewVmWithDialog` test helper + rewrite VM

**Files:**
- Modify: `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs` — add `NewVmWithDialog` helper.
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` — rewrite `AddTraceAsync` + `CanAddTrace`.

**Consumes:** `IFileDialogService.ShowOpenDialog(string filter) → string?`, existing `TraceViewerViewModel` ctor (already takes optional `IFileDialogService?`).
**Produces:** A parameterless `AddTraceAsync()` that opens the dialog, validates the returned path, and calls `_registry.LoadAsync` only on a non-empty path.

- [ ] **Step 1: Add `NewVmWithDialog` helper to the test file**

Insert this helper method inside `TraceViewerViewModelTests` (placement: next to existing `NewVm()`):

```csharp
/// <summary>
/// v3.11.4 PATCH: factory that wires the explicit <see cref="IFileDialogService"/>
/// and <see cref="ITraceSessionRegistry"/> substitutes the new tests
/// need. Mirrors the existing <c>NewVm()</c> shape but takes both
/// substitutes as parameters so each test controls dialog return value +
/// registry assertion target.
/// </summary>
private static TraceViewerViewModel NewVmWithDialog(
    ITraceSessionRegistry registry,
    IFileDialogService dialog)
{
    var logger = NullLogger<TraceViewerViewModel>.Instance;
    var dbcService = new FakeDbcServiceForTraceViewer();
    var sessionLibrary = NewFakeSessionLibrary();
    return new TraceViewerViewModel(registry, dbcService, logger, sessionLibrary, dialog);
}
```

Notes:
- `FakeDbcServiceForTraceViewer` is a nested stub that returns Task.CompletedTask on `LoadAsync`. If the existing test file already has a `FakeDbcService` nested type, REUSE it instead of adding a new one. Verify via Grep first.
- `NewFakeSessionLibrary()` is a static helper that already exists in the test file (per the constructor pattern at line 132-135 of `AppShellViewModelTests.cs`).

- [ ] **Step 2: Rewrite `AddTraceAsync` body**

In `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs`, replace the entire `[RelayCommand(CanExecute = nameof(CanAddTrace))] public async Task AddTraceAsync(string path)` block (lines 200-243) with:

```csharp
[RelayCommand(CanExecute = nameof(CanAddTrace))]
public async Task AddTraceAsync()
{
    // v3.11.4 PATCH: the file dialog moved from the View (CommandParameter=""
    // hack) into the VM via the already-injected IFileDialogService. The
    // empty-string path that triggered the "Unexpected error: path must be
    // non-empty" user-facing regression can no longer reach the registry —
    // the dialog either returns a real path or null (cancellation).
    var dialog = _fileDialog;
    if (dialog is null)
    {
        // Defensive fallback when IFileDialogService wasn't injected (e.g.,
        // unit-test fixtures that build the VM without DI). Surface as an
        // error rather than crashing — the user can still type the path
        // manually via the .tmtrace session Save/Open flow.
        ErrorMessage = "File dialog service unavailable. Use File ▸ Open Session... instead.";
        StatusMessage = "Add trace unavailable";
        LogLoadFailed(_logger, new InvalidOperationException("IFileDialogService not injected"), "(no dialog)");
        return;
    }

    string? path;
    try
    {
        path = dialog.ShowOpenDialog("ASC files|*.asc;*.ASC|All files|*.*");
    }
    catch (Exception ex)
    {
        // The WPF OpenFileDialog throws if no Application is running or if
        // the dispatcher is shutting down. Surface as a user-visible error
        // and stay silent otherwise.
        LogLoadFailed(_logger, ex, "(dialog)");
        ErrorMessage = $"File dialog failed: {ex.Message}";
        StatusMessage = "Add trace failed";
        return;
    }

    if (string.IsNullOrEmpty(path))
    {
        // Cancellation (dialog returned null) or pathological empty-string
        // return (impossible from production OpenFileDialog but defended
        // for test fakes). Silent no-op per v3.11.4 PATCH contract.
        return;
    }

    try
    {
        ErrorMessage = null;
        IsLoading = true;
        var name = System.IO.Path.GetFileName(path);
        StatusMessage = $"Loading {name}…";
        await _registry.LoadAsync(path).ConfigureAwait(true);
        StatusMessage = $"Loaded {name}";
    }
    catch (OperationCanceledException)
    {
        StatusMessage = "Load cancelled";
    }
    catch (ReplayException ex)
    {
        LogLoadFailed(_logger, ex, path);
        ErrorMessage = ex.Message;
        StatusMessage = "Load failed";
    }
    catch (Exception ex)
    {
        // v3.9.2 PATCH H10: defensive fallback catch. AddTraceAsync is
        // invoked through an async-void command, so any exception that
        // escapes the typed arms above would propagate to WPF
        // DispatcherUnhandledException, where App.xaml.cs:332 deliberately
        // does NOT mark Handled — resulting in process termination.
        // v3.11.4 PATCH: this catch can no longer be reached for the
        // empty-path case (dialog validates before the path is forwarded),
        // but the defensive arm stays for registry hook throws (SourcesChanged
        // listener, ApplyAutoSnapshotAsync, etc.).
        LogLoadFailed(_logger, ex, path);
        ErrorMessage = $"Unexpected error: {ex.Message}";
        StatusMessage = "Load failed";
    }
    finally
    {
        IsLoading = false;
    }
}
```

- [ ] **Step 3: Simplify `CanAddTrace` predicate**

In the same file, find `private bool CanAddTrace(string? path) => !IsLoading;` (line 260) and replace with:

```csharp
// v3.11.4 PATCH: AddTraceCommand is parameterless now (the VM owns the
// file-dialog flow). The CanExecute predicate must NOT take a path arg —
// any CanExecute(string.Empty) call would silently disable the command,
// which was the v3.9.1 PATCH B2 root cause.
private bool CanAddTrace() => !IsLoading;
```

- [ ] **Step 4: Remove `CommandParameter=""` from the XAML button**

In `src/PeakCan.Host.App/Views/TraceViewerView.xaml`, find:

```xml
<Button Content="Add trace…" Command="{Binding AddTraceCommand}"
        CommandParameter="" Padding="8,2" Margin="0,0,4,0" />
```

And replace with:

```xml
<!-- v3.11.4 PATCH: CommandParameter="" was the regression root cause —
     it forced AddTraceAsync to receive an empty string, which then
     tripped PathNormalizer.Normalize's "path must be non-empty" guard.
     The VM now opens the dialog itself via IFileDialogService. -->
<Button Content="Add trace…" Command="{Binding AddTraceCommand}"
        Padding="8,2" Margin="0,0,4,0" />
```

- [ ] **Step 5: Verify the App project + tests build**

Run: `dotnet build PeakCan.Host.slnx --nologo`
Expected: 0 errors. Warnings allowed.

- [ ] **Step 6: Run the 4 new tests — expected to PASS**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~AddTraceAsync_FileDialog_Cancelled|FullyQualifiedName~AddTraceAsync_FileDialog_Returns_ValidPath|FullyQualifiedName~AddTraceAsync_Never_Passes_EmptyPath|FullyQualifiedName~CanAddTrace_True_When" --nologo --no-build`
Expected: 4 passed, 0 failed.

- [ ] **Step 7: Run the full TraceViewerViewModel test class — ensure no regression**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~TraceViewerViewModelTests" --nologo --no-build`
Expected: ALL existing TraceViewerViewModel tests still green, plus the 4 new tests = +4 total.

If any existing test fails, it likely depends on the old `AddTraceAsync(string path)` signature (e.g., calling `vm.AddTraceAsync("/some/path.asc")` directly). Those call sites must be updated to use the dialog path (or removed if they only existed to test the now-defunct CommandParameter="" path). Surface in the report.

Do not commit yet.

---

### Task 3: Run full test suite + manual smoke + Tier 3 ship

**Files:**
- No source changes; this task verifies Task 2's commit + ships.

- [ ] **Step 1: Run the full solution test suite**

Run: `dotnet test PeakCan.Host.slnx --nologo --no-build`
Expected: **1276 + 5 SKIP / 0 fail** (+4 active tests from `TraceViewerViewModelTests`).

If any test fails or skips an unexpected count, surface in the report and STOP — do not proceed to the Tier 3 ship until the regression is resolved.

- [ ] **Step 2: Manual smoke (4 verification cases)**

Run the WPF app and verify:
1. **Click "Add trace…" with a real .asc file selected**: dialog opens, file is loaded, status shows "Loaded foo.asc", chart updates.
2. **Click "Add trace…" then cancel the dialog**: dialog closes, no error message, no status change. The toolbar button stays enabled.
3. **Click "Add trace…" with an oversized .asc (>200 MB)**: dialog opens, file selected → ErrorMessage shows the size cap message (mirrors v3.9.1 PATCH B2 contract).
4. **Click "Add trace…" with a corrupt .asc**: ErrorMessage shows the parse error (mirrors v3.9.1 PATCH B2 contract).

If any smoke case fails, surface in the report.

- [ ] **Step 3: Write release notes**

Create `docs/release-notes-v3.11.4.md`:

```markdown
# Release Notes v3.11.4 — Trace Viewer "Add trace…" regression fix (PATCH)

**Released:** 2026-07-07
**Parent:** v3.11.3 PATCH (`e7b72f21`)
**Tag:** v3.11.4
**Branch:** `feature/v3-11-4-patch`

## Highlights

This PATCH fixes the user-reported regression where clicking **"Add trace…"** in the Trace Viewer toolbar produced **"Unexpected error: path must be non-empty"** instead of opening a file dialog.

| Commit | Fix | Tests |
|--------|-----|-------|
| `<this-commit>` | Move file-dialog flow from XAML `CommandParameter=""` hack into the VM via `IFileDialogService` | +4 |

**Test delta:** 1272 + 5 SKIP / 0 fail → **1276 + 5 SKIP / 0 fail** (+4 active tests)
**Code stats:** +45 / -15 (net +30 LoC: dialog flow added, CommandParameter + empty-path branch removed)

## Root cause

The v3.9.1 PATCH Bug #2 fix changed the "Add trace…" button from `Click="OnAddTraceClick"` (a code-behind handler that opened the dialog) to `Command="{Binding AddTraceCommand}" CommandParameter=""`. The empty-string `CommandParameter` was the source-gen command's way of satisfying the `AddTraceAsync(string path)` signature, but it also forced the path through `_registry.LoadAsync("")` → `PathNormalizer.Normalize("")` → `ArgumentException("path must be non-empty")`.

The exception type is `ArgumentException` (not `ReplayException`), so it escaped the typed `catch (ReplayException ex)` arm at `TraceViewerViewModel.cs:216` and landed in the **v3.9.2 PATCH H10 defensive fallback catch** at line 233, which surfaces as `ErrorMessage = "Unexpected error: path must be non-empty"`. The Process Termination path was correctly defended by H10; the user-facing UX was broken.

## Fix

Move the file-dialog flow from the View into the VM via the already-injected `IFileDialogService` (optional ctor param at `TraceViewerViewModel.cs:151`, wired by DI from `AppHostBuilder.cs:418`). The XAML button drops `CommandParameter=""`; the VM opens the dialog first, then calls the registry only on a non-empty path.

**Before** (`TraceViewerView.xaml:24-25`):
```xml
<Button Content="Add trace…" Command="{Binding AddTraceCommand}"
        CommandParameter="" Padding="8,2" Margin="0,0,4,0" />
```

**After**:
```xml
<Button Content="Add trace…" Command="{Binding AddTraceCommand}"
        Padding="8,2" Margin="0,0,4,0" />
```

**VM body** (`TraceViewerViewModel.cs:200-281`):
```csharp
[RelayCommand(CanExecute = nameof(CanAddTrace))]
public async Task AddTraceAsync()
{
    var path = _fileDialog?.ShowOpenDialog("ASC files|*.asc;*.ASC|All files|*.*");
    if (string.IsNullOrEmpty(path)) return;  // silent cancellation

    try { /* existing load + try/catch arms preserved */ }
    finally { IsLoading = false; }
}

private bool CanAddTrace() => !IsLoading;  // path-arg removed
```

## Tests

| Test | Asserts |
|------|---------|
| `AddTraceAsync_FileDialog_Cancelled_Is_SilentNoOp` (NEW, +1) | Dialog returns null → no `LoadAsync` call, no ErrorMessage change, no StatusMessage change, IsLoading flips false |
| `AddTraceAsync_FileDialog_Returns_ValidPath_Calls_Registry_LoadAsync` (NEW, +1) | Dialog returns "C:\\fake.asc" → `_registry.LoadAsync` called once with that path; StatusMessage = "Loaded …" |
| `AddTraceAsync_Never_Passes_EmptyPath_To_Registry` (NEW, +1) | Dialog returns "" (pathological) → `_registry.LoadAsync` NEVER called (regression guard) |
| `CanAddTrace_True_When_IsLoading_False_Regardless_Of_Argument` (NEW, +1) | `CanExecute(null)` + `CanExecute("")` + `CanExecute(path)` all return true when `IsLoading=false` |

## Deferred

| Item | Reason |
|------|--------|
| C2 ReplayViewModel god class split | Deferred to v3.12.0 MINOR |
| H3 / H6 / M1-M13 review backlog | Deferred to v3.12.0 MINOR cleanup PATCH |

## Upgrade notes

No breaking changes:
- `AddTraceCommand` source-generated name preserved (XAML binding unchanged).
- `IFileDialogService` is optional in the `TraceViewerViewModel` ctor (was added in v3.6.0 MINOR T3 — `TraceViewerViewModel.cs:151`). Test fixtures that build the VM without DI continue to work; the new code path surfaces a defensive error message instead of crashing.
- DI registration unchanged (`AddSingleton<TraceViewerViewModel>()` at `AppHostBuilder.cs:418` auto-wires the new dependency).

## NEXT

- v3.12.0 MINOR — C2 ReplayViewModel god class split + H3/H6/M1-M13 review backlog closure
```

- [ ] **Step 4: Create the Tier 3 ship script**

Create `scripts/tier3_v3114.py` by copying `scripts/tier3_v3113.py` and updating:
- Line 17: `PARENT_SHA = "e7b72f21a027877459e0956ccb4d3a6b8d74ce24"`  (v3.11.3 on origin/main)
- Lines 21-28: replace `ADDED_OR_MODIFIED` with:

```python
ADDED_OR_MODIFIED = [
    # M5: Trace Viewer Add trace file-dialog move into VM
    "src/PeakCan.Host.App/Views/TraceViewerView.xaml",
    "src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs",
    "tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs",
    # Release notes
    "docs/release-notes-v3.11.4.md",
]
```

- Lines 73, 84, 87, 90, 94, 99: replace all `v3.11.3` with `v3.11.4`.

- [ ] **Step 5: Run the Tier 3 ship**

Run: `python scripts/tier3_v3114.py`
Expected output:
```
  parent       e7b72f21...
  parent tree  <40-hex-sha>
  blob   <40-hex-sha>  src/PeakCan.Host.App/Views/TraceViewerView.xaml  (... bytes)
  blob   <40-hex-sha>  src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs  (... bytes)
  blob   <40-hex-sha>  tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs  (... bytes)
  blob   <40-hex-sha>  docs/release-notes-v3.11.4.md  (... bytes)

  tree  <40-hex-sha>
  commit <40-hex-sha>
  refs/heads/main -> <40-hex-sha> (force)
  tag    <40-hex-sha>  v3.11.4
  refs/tags/v3.11.4 -> <40-hex-sha>
  release https://github.com/jasontaotao/peakcan-host/releases/tag/v3.11.4

=== TIER 3 SHIP COMPLETE ===
  parent  : e7b72f21...
  new     : <40-hex-sha>
  tag     : v3.11.4  (<40-hex-sha>)
  release : https://github.com/jasontaotao/peakcan-host/releases/tag/v3.11.4
```

- [ ] **Step 6: Commit the ship script + release notes to local branch**

```bash
cd D:/claude_proj2/peakcan-host
git add docs/release-notes-v3.11.4.md scripts/tier3_v3114.py
git commit -m "docs(ship): v3.11.4 PATCH release notes + tier3 ship script"
```

- [ ] **Step 7: PKM capture**

Dispatch `vault-pkm:pkm-capture` in the background with:
- First capture this session: false (previous devlog already exists for v3.11.3)
- Previous capture timestamp: 2026-07-07T08:24:21Z (v3.11.3 ship)
- Vault path: `01-Projects/peakcan-host/development/v3-11-4-patch-trace-viewer-add-trace-2026-07-07.md`

---

## Self-Review (post-write, before handoff)

1. **Spec coverage**: User request "trace viewer 点击 add trace 没反应" → all 4 smoke cases in Task 3 Step 2 verify the fix end-to-end.
2. **Placeholder scan**: No "TBD" / "implement later" / "similar to Task N" markers.
3. **Type consistency**:
   - `AddTraceAsync()` (parameterless) — used in Task 1 tests + Task 2 implementation + Task 2 step 6 verification. Consistent.
   - `CanAddTrace()` (no args) — same.
   - `IFileDialogService.ShowOpenDialog(string filter) → string?` — matches the existing interface (`IFileDialogService.cs:10-18`).
4. **Cross-task dependency**: Task 1 test helper `NewVmWithDialog` is added in Task 2 step 1 (not Task 1) — verified: the test file references `NewVmWithDialog` before it's defined, but Task 1's "expected to FAIL" note handles this gracefully.

## Out of scope (deferred)

- **`CancelAddTrace` for window-close mid-import** — pre-existing scope; the v3.11.4 fix doesn't add cancellation to an in-flight load. User can still close the window.
- **Multi-file batch load** — v3.11.4 stays single-file; the dialog is configured for one selection.
- **"Recent files" picker inside the Add trace dialog** — pre-existing scope; Recent Sessions lives in the AppShell File menu.
- **Remove `PathNormalizer.Normalize` empty-string guard** — defensive-in-depth; the guard is correct behavior, just no longer reachable from this entry point.

## Verification

```bash
# Targeted:
dotnet test --filter "FullyQualifiedName~TraceViewerViewModelTests" --nologo
# Expect: all existing TraceViewerViewModel tests green + 4 new tests green

# Full suite:
dotnet test PeakCan.Host.slnx --nologo
# Expect: 1272 → 1276 + 5 SKIP / 0 fail (+4 active)

# Manual smoke:
# 1. Click "Add trace…" → file dialog opens → select valid .asc → loaded
# 2. Click "Add trace…" → cancel dialog → silent no-op, button stays enabled
# 3. Click "Add trace…" → select 300MB .asc → ErrorMessage shows size cap
# 4. Click "Add trace…" → select corrupt .asc → ErrorMessage shows parse error
```

## Ship summary

- **Tag**: v3.11.4 (PATCH)
- **Parent**: v3.11.3 PATCH on origin/main (`e7b72f21a027877459e0956ccb4d3a6b8d74ce24`)
- **Files**: 2 modified (TraceViewerView.xaml + TraceViewerViewModel.cs), 1 modified (TraceViewerViewModelTests.cs), +1 ship script, +1 release notes
- **Tests**: +4 active (file-dialog cancel + valid path + empty-path guard + CanAddTrace arg-independence). Total delta: 1272 → 1276 + 5 SKIP / 0 fail.
- **Commits**: 2 commits (1 source + 1 ship docs) on `feature/v3-11-4-patch`, then 1 Tier 3 ship commit on `origin/main`.