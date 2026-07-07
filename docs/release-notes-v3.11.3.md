# Release Notes v3.11.3 — UDS Window refactor (PATCH)

**Released:** 2026-07-07
**Parent:** v3.11.2 PATCH (`d9e77a82`)
**Tag:** v3.11.3
**Branch:** `feature/v3-11-3-patch`

## Highlights

This PATCH completes the user-requested UX refactor: the UDS diagnostic surface
is migrated from an in-place `UserControl` tab to a separate, non-modal
`Window` that opens from the AppShell View menu — mirroring the
`TraceViewerView` / `MultiFrameSendWindow` precedent.

| Commit | Refactor | Tests |
|--------|----------|-------|
| `5e3a45b` | `UdsView` (`UserControl`) → `UdsWindow` (`Window`) under `Windows/` namespace | +1 |

**Test delta:** 1266 + 5 SKIP / 0 fail → **1267 + 5 SKIP / 0 fail** (+1 active test)
**Code stats:** +220 / -218 (net +2 LoC: Window attributes added, UserControl attributes removed; body bytes identical)

## Refactor

### UDS — `UdsView` → `UdsWindow`

`src/PeakCan.Host.App/Views/UdsView.xaml(.cs)` is moved to
`src/PeakCan.Host.App/Windows/UdsWindow.xaml(.cs)`. The root type changes
from `<UserControl>` to `<Window>` with `Title="UDS Diagnostics"`,
`Width="1100"`, `Height="700"`, `WindowStartupLocation="CenterOwner"`,
`ShowInTaskbar="True"`.

**XAML body is byte-identical** — same `DockPanel`, same TabControl
(DIDs / Routines / DTCs), same RichTextBox log handler. Only the root
type + namespace moved. XAML bindings resolve identically because the
`DataContext` contract (`UdsViewModel`) is preserved.

**`UdsViewModel` is unchanged** — no new DI registration needed (already
a singleton at `AppHostBuilder.cs:615`).

### `AppShellViewModel.ShowUds` — switch to `ViewSwitcher.ShowWindow`

The `ShowUds` body switches from `ViewSwitcher.Show(...)` (in-place tab
swapper) to `ViewSwitcher.ShowWindow(...)` + Owner assignment + Show/Activate.
This mirrors the v3.9.1 PATCH B1 + v3.11.1 PATCH M3 secondary-window
precedent already in place for `ShowTraceViewer`:

```csharp
ViewSwitcher.ShowWindow(
    factory: () => new UdsWindow { DataContext = _udsViewModel },
    cache: ref _udsWindow);
if (_udsWindow is null) return; // defensive

if (Application.Current?.MainWindow is { } owner && owner != _udsWindow)
    _udsWindow.Owner = owner;

if (!_udsWindow.IsVisible) _udsWindow.Show();
else _udsWindow.Activate();
```

**Cache semantics preserved**:
- First Show creates the window from the factory.
- Second Show reuses the cached instance (window position + size +
  SelectedDid + tab selections all survive menu round-trips).
- Closing the window clears the cache so the next Show opens fresh.
- Closing AppShell cascade-closes the UDS window via the Owner
  assignment (mirrors Trace Viewer cascade-close from v3.9.1 PATCH B1).

**Field rename**: `_udsView : UdsView?` → `_udsWindow : UdsWindow?`.

**Menu binding preserved**: `AppShell.xaml` line `<MenuItem Header="UDS"
Command="{Binding ShowUdsCommand}" />` is unchanged — the source-
generated command name survives the refactor.

## Tests

| Test | Asserts |
|------|---------|
| `UdsWindowTests.ShowUdsCommand_Opens_Cached_UdsWindow` (NEW, +1) | First click populates `_udsWindow` cache; second click reuses the same instance; `CurrentView` remains null (UDS is no longer in-place) |

**Full suite result:** 1272 passed / 5 SKIP / 0 fail across all 3 test projects
(`PeakCan.Host.App.Tests`: 760 + 3 SKIP, `PeakCan.Host.Core.Tests`: 428, `PeakCan.Host.Infrastructure.Tests`: 84 + 2 SKIP).

## Deferred

| Item | Reason |
|------|--------|
| C2 ReplayViewModel god class split | Deferred to v3.12.0 MINOR |
| 38-finding backlog (H3/H6/M1-M13) | Deferred to v3.12.0 MINOR cleanup PATCH |

## Upgrade notes

No breaking changes:
- `ShowUdsCommand` source-generated name preserved (XAML binding unchanged).
- `UdsViewModel` constructor signature unchanged.
- DI registration in `AppHostBuilder` unchanged.
- File moved (not renamed): `Views/UdsView.xaml(.cs)` → `Windows/UdsWindow.xaml(.cs)`.
  No external consumers reference `UdsView` (only the `AppShellViewModel`
  field reference, updated in this PATCH).

## NEXT

- v3.11.4 PATCH — visual UI smoke testing if user-reported regressions emerge
- v3.12.0 MINOR — C2 ReplayViewModel god class split (1153 LoC → 3 VMs)