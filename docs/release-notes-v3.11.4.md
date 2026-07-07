# Release Notes v3.11.4 — Trace Viewer "Add trace…" regression fix (PATCH)

**Released:** 2026-07-07
**Parent:** v3.11.3 PATCH (`e7b72f21`)
**Tag:** v3.11.4
**Branch:** `feature/v3-11-1-patch`

## Highlights

This PATCH fixes the user-reported regression where clicking **"Add trace…"** in the Trace Viewer toolbar produced **"Unexpected error: path must be non-empty"** instead of opening a file dialog.

| Commit | Fix | Tests |
|--------|-----|-------|
| `a22f99f` | Move file-dialog flow from XAML `CommandParameter=""` hack into the VM via `IFileDialogService` | +4 |

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
| CANoe Vector ASC v1.3 parser gap (user-reported same day) | Separate v3.11.5 PATCH |

## Upgrade notes

No breaking changes:
- `AddTraceCommand` source-generated name preserved (XAML binding unchanged).
- `IFileDialogService` is optional in the `TraceViewerViewModel` ctor (was added in v3.6.0 MINOR T3 — `TraceViewerViewModel.cs:151`). Test fixtures that build the VM without DI continue to work; the new code path surfaces a defensive error message instead of crashing.
- DI registration unchanged (`AddSingleton<TraceViewerViewModel>()` at `AppHostBuilder.cs:418` auto-wires the new dependency).

## NEXT

- v3.11.5 PATCH — CANoe Vector ASC v1.3 parser compatibility (user-reported same day)
- v3.12.0 MINOR — C2 ReplayViewModel god class split + H3/H6/M1-M13 review backlog closure