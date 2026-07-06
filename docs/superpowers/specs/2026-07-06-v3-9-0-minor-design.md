# v3.9.0 MINOR Design — Full Replay A/B Loop + Click-to-Jump + Production Diagnostics

**Date:** 2026-07-06
**Status:** Draft — pending user review
**Branch:** `feature/v3-9-0-minor` (TBD)
**Parent:** v3.8.8 PATCH on origin/main (`34d5a79f3423238a2d6fd401d858ab1e68da5d9a`)

## 1. Context

v3.8.0 MINOR shipped the first Replay cursor-walking UX (frame-step + bookmarks + loop regions) but partial-scoped two pieces:
- **A/B loop regions** — seek to start on activation but no auto-rewind at end (v3.9.0 carry-over)
- **Click-to-jump** — Slider exists but click does not seek

The v3.8.3–v3.8.8 PATCH chain (8 PATCHes in 1 day) closed 25 production defects (1228 + 5 SKIP / 0 fail) and left a clean slate for v3.9.0. Several Tier 2/3 deferred items from v3.8.7 + v3.8.8 release notes are also ready to ship.

**Goal:** Close the v3.8.0 UX half (full A/B loop + click-to-jump + slider visual markers + bookmark label edit) and add two production-diagnostics capabilities (Serilog `ReadFrom.Configuration` + IOdxImportService CancellationToken threading). One MINOR, 6 pieces, ~800–1200 LoC, +25–35 tests.

## 2. Goals & non-goals

### Goals (V1 — Tier 1 + Tier 2 from v3.8.8 plan)

1. **A/B loop rewind** — when `CurrentTimestamp ≥ loopRegion.End` during playback, atomically rewind to `loopRegion.Start`. Mirrors standard DAQ-player UX (Vector CANoe, Wireshark).
2. **Click-to-jump on Slider** — mouse click + drag seeks to proportional time. Mirrors YouTube/VLC scrubber UX.
3. **Slider visual markers** — triangle glyph for bookmarks + colored band for loop regions rendered ON the Slider (no new control). User sees positions at a glance.
4. **Bookmark label edit** — right-click bookmark row → context menu "Edit label" → inline TextBox → save round-trips through `.tmtrace` bundle.
5. **Serilog `ReadFrom.Configuration`** — add `Serilog.Settings.Configuration` NuGet + `ReadFrom.Configuration(builder.Configuration)`. Operator can now edit `appsettings.json` `Serilog:MinimumLevel:Default` to bump to `Debug` without recompile.
6. **IOdxImportService ImportAsync CancellationToken threading** — service accepts `CancellationToken` + `ParseAndIndexOneDocument` honors `ct.ThrowIfCancellationRequested()` per document. v3.8.8 F3 `CancelImport()` now actually stops the in-flight import (currently the orphan task runs to completion).

### Non-goals (deferred to v3.9.x PATCH or v3.10.0 MINOR)

- **Scrubber drag dedup** — Slider drag event-coalescing (perf, not user-visible). Defer to v3.9.x PATCH.
- **AppShellVM dispose unregister** — explicit cleanup pattern (defense, not defect). Defer.
- **RecentSessionsService file watcher** — live menu sync (rare UX, high complexity). Defer.
- **Auto-save STA safety** — STA thread guard on auto-save path (rare race, defensive). Defer.
- **Multi-loop-region rewind** — when overlapping loop regions exist, pick innermost. v3.9.0 ships single-region rewind; multi-region is v3.10.0.
- **Slider keyboard seek** — ←/→ already works; ↑/↓ for speed. Defer UI polish.

## 3. Architecture decisions

### D1 — A/B loop rewind hook placement

The rewind decision happens in the playback tick path. **Insert a new `OnLoopRewind(LoopRegionDto region)` virtual method on `IReplayService`** that `ReplayTimeline` invokes when the auto-rewind is about to fire. `ReplayViewModel` subscribes + logs to the `UdsLogLine`-style replay log (or a new `ReplayLog` ObservableCollection) + surfaces a transient status message ("Rewind: loop region X"). Why a hook not an event: keeps `ReplayTimeline` testable in isolation (it can verify the hook fires without a VM).

### D2 — Click-to-jump on Slider uses `Thumb.DragCompleted` + `Slider.MouseLeftButtonUp`

WPF `Slider` has no native click-seek. Two options:
- (a) Subclass `Slider` → `SeekableSlider` with a `ClickToSeek` attached property
- (b) Use `PreviewMouseLeftButtonDown` + `MouseLeftButtonUp` code-behind on `ReplayView.xaml`

**Choose (b)**: lower-blast-radius (no new control class), the click-to-seek math is a few lines, and the project already has WPF code-behind precedent in `OdxImportWindow.xaml.cs` (v3.8.8 F3 future wiring). Document the click-vs-drag discrimination (small mouse movement between down and up = click; large = drag → defer to Slider's native drag).

### D3 — Slider visual markers via custom `Slider.Template`

Replace `Slider.Template` with a `ControlTemplate` that overlays a `Canvas` containing:
- 1 × `Polygon` per bookmark (filled triangle, 6 px wide, position proportional to `bookmark.Timestamp / TotalDuration`)
- 1 × `Rectangle` per loop region (semi-transparent fill, `Start / TotalDuration` to `End / TotalDuration`)
- Visual markers update via `MultiBinding` to `Bookmarks` + `LoopRegions` + `TotalDuration`. `INotifyCollectionChanged` is already implemented by the existing `ObservableCollection<>` wrappers (`BookmarkVm` / `LoopRegionVm`).

**Trade-off:** Custom template loses Slider's default theme (touch + keyboard). Mitigation: preserve `IsMoveToPointEnabled="True"` + the existing KeyBindings. Style the template to match the existing dark-theme Slider.

### D4 — Bookmark label edit via `DataGrid` + `DataGridTemplateColumn`

Use a `DataGrid` (not a `ListBox`) for the bookmarks panel so each row has a built-in edit affordance. `DataGridTemplateColumn.CellTemplate` shows the label as TextBlock; `CellEditingTemplate` shows an inline TextBox bound TwoWay to `BookmarkVm.Label`. Save on `RowEditEnding` (commit on Enter / focus-lost, NOT on every keystroke — avoids per-keystroke bundle save).

### D5 — Serilog `ReadFrom.Configuration` shape

Replace hardcoded `MinimumLevel.Information()` in `AppHostBuilder.Build` (line 103-116) with:
```csharp
var loggerConfig = new LoggerConfiguration().ReadFrom.Configuration(builder.Configuration);
```
The `appsettings.json` `Serilog` section is read at startup:
```json
"Serilog": {
  "MinimumLevel": {
    "Default": "Information",
    "Override": {
      "Microsoft": "Warning",
      "PeakCan.Host": "Debug"
    }
  },
  "WriteTo": [
    { "Name": "File", "Args": { "path": "...", "rollingInterval": "Day" } }
  ]
}
```
The hardcoded `WriteTo.File` line is REMOVED (configuration owns it now). Existing test `Build_WiresSerilogGlobalLogger_NotSilentDefaultLogger` (v3.8.7) is updated to assert `Log.Logger.GetType().FullName == "Serilog.Core.Logger"` after `Build()`.

### D6 — IOdxImportService ImportAsync CancellationToken threading

Change signature: `Task<OdxImportResult> ImportAsync(string odxPath, CancellationToken ct = default)`. Inside the per-document foreach:
```csharp
ct.ThrowIfCancellationRequested();  // already at top of loop
try { ParseAndIndexOneDocument(xdoc, didDefs, dtcDefs, routineDefs, warnings, ct); }
catch (OperationCanceledException) { throw; }  // propagate, not warn
catch (Exception ex) { warnings.Add($"ODX parse error in document: {ex.Message}"); }
```
`ParseAndIndexOneDocument` accepts `ct` and calls `ct.ThrowIfCancellationRequested()` between major parse steps (not inside tight DOP loops — overhead). `OdxImportViewModel.CancelImport` (v3.8.8 F3) threads the VM-owned CTS into the call: `_service.ImportAsync(path, _importCts.Token)`. The VM's `_importCts?.Cancel()` in `CancelImport` actually stops the parse.

## 4. File layout

### New files (4)

| Path | Purpose |
|---|---|
| `src/PeakCan.Host.App/Views/SeekableSlider.cs` | (only if D2 (a) chosen — we chose (b), so NOT new) |
| `tests/PeakCan.Host.App.Tests/ViewModels/ReplayLoopRewindTests.cs` | A/B loop rewind test (1 file, ~150 LoC) |
| `tests/PeakCan.Host.App.Tests/ViewModels/ReplayClickToJumpTests.cs` | Click-to-jump test (1 file, ~80 LoC) |
| `tests/PeakCan.Host.App.Tests/ViewModels/ReplayBookmarkEditTests.cs` | Bookmark label edit test (1 file, ~120 LoC) |
| `tests/PeakCan.Host.App.Tests/Composition/AppHostBuilderSerilogTests.cs` | Serilog config wiring test (1 file, ~80 LoC) |
| `tests/PeakCan.Host.App.Tests/Services/Odx/ImportAsyncCancellationTests.cs` | CT threading test (1 file, ~100 LoC) |

### Modified files (~10)

| Path | Piece |
|---|---|
| `src/PeakCan.Host.Core/Replay/IReplayService.cs` | D1: add `OnLoopRewind` event/hook |
| `src/PeakCan.Host.Core/Replay/ReplayTimeline.cs` | D1: invoke hook on `CurrentTimestamp ≥ LoopRegion.End` |
| `src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs` | D1 subscribe + D2 slider click handler + D4 bookmark edit save |
| `src/PeakCan.Host.App/Views/ReplayView.xaml` | D2 + D3 Slider template + D4 DataGrid bookmark panel |
| `src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs` (loop-region rewind status) | D1 status message |
| `src/PeakCan.Host.Core/Uds/Odx/IOdxImportService.cs` | D6: signature `ImportAsync(string, CancellationToken)` |
| `src/PeakCan.Host.Core/Uds/Odx/OdxImportService.cs` | D6: thread CT through `ParseAndIndexOneDocument` |
| `src/PeakCan.Host.App/ViewModels/Uds/OdxImportViewModel.cs` | D6: own `_importCts`, pass to service, cancel in `CancelImport` |
| `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` | D5: `ReadFrom.Configuration` + remove hardcoded `WriteTo.File` |
| `src/PeakCan.Host.App/appsettings.json` | D5: add `Serilog` section |
| `tests/PeakCan.Host.App.Tests/Composition/AppHostBuilderTests.cs` | D5: update existing Serilog test |

### LoC estimate

| Category | LoC |
|---|---|
| Production code | ~400 |
| Test code | ~530 |
| Spec / docs / appsettings | ~70 |
| **Total** | **~1000** |

## 5. Phases (implementation order)

| Phase | Pieces | Days | Risk |
|---|---|---|---|
| **P1: A/B loop rewind** | D1 + 1 new test file | 1 | LOW (hook is additive) |
| **P2: Click-to-jump** | D2 + 1 new test file | 0.5 | LOW (code-behind, isolated) |
| **P3: Slider visual markers** | D3 + UI polish | 1 | MEDIUM (template override; touch + keyboard preservation) |
| **P4: Bookmark label edit** | D4 + 1 new test file + DataGrid | 1 | MEDIUM (DataGrid edit-mode save timing) |
| **P5: Serilog ReadFrom.Configuration** | D5 + NuGet add + 1 new test file + appsettings.json | 0.5 | MEDIUM (NuGet add may break build; hardcoded `WriteTo.File` removal is a behavioral change) |
| **P6: IOdxImportService CT threading** | D6 + 1 new test file + 1 signature change | 0.5 | LOW (additive param) |
| **P7: Tier 3 ship** | README + release notes + Tier 3 + PKM | 0.5 | LOW (pattern established) |

**Total: 5 days (1 working week).** Could compress to 3 days if Tier 3 ship reused v3.8.x PKM capture template as-is.

## 6. Risk register

| Risk | Severity | Mitigation |
|---|---|---|
| D1 `OnLoopRewind` hook added to `IReplayService` interface — breaking change for any external test double | LOW | Only `IReplayService` consumer is `ReplayViewModel` (DI singleton). Update NSubstitute mocks in 1 test file. |
| D3 custom Slider template may lose touch + keyboard | MEDIUM | Template keeps `IsMoveToPointEnabled="True"` + existing `KeyBinding` arrows. Add unit test for keyboard up/down. |
| D5 NuGet add (`Serilog.Settings.Configuration`) may conflict with existing Serilog version | MEDIUM | Run `dotnet restore` after NuGet add; if conflict, upgrade Serilog to compatible version. v3.8.x uses Serilog 3.x — should be fine. |
| D5 hardcoded `WriteTo.File` removal may silently change log path | LOW | Default `appsettings.json` `Serilog.WriteTo[0].Args.path` mirrors the hardcoded path. Document migration in release notes. |
| D6 ODX `ImportAsync(string, CancellationToken)` signature change | LOW | Default param `= default` keeps all existing callers source-compat. v3.8.x tests use `ct` already. |
| D6 VM `_importCts` lifecycle: if not disposed, leaks | LOW | `OdxImportViewModel` already has `Dispose` pattern; add `_importCts?.Dispose()` in the `finally` of `ImportAsync`. |
| P5 may need a v3.9.1 PATCH if NuGet breaks build | MEDIUM | P5 commit message: "v3.9.0 MINOR: ... if Serilog.Settings.Configuration fails to restore, ship v3.9.1 PATCH with the fix". |

## 7. Verification

```bash
# Per phase:
dotnet test --filter "FullyQualifiedName~ReplayLoopRewindTests" --nologo  # P1
dotnet test --filter "FullyQualifiedName~ReplayClickToJumpTests" --nologo  # P2
dotnet test --filter "FullyQualifiedName~ReplayBookmarkEditTests" --nologo # P4
dotnet test --filter "FullyQualifiedName~AppHostBuilderSerilogTests" --nologo # P5
dotnet test --filter "FullyQualifiedName~ImportAsyncCancellationTests" --nologo # P6

# Full suite (per phase + after P7):
dotnet test PeakCan.Host.slnx --nologo
# Expect: 1228 + 5 SKIP -> 1253-1263 + 5 SKIP / 0 fail (+25-35 active)

# Manual smoke:
# 1. Replay tab, load .asc, add a loop region [10, 20], start playback at t=5
# 2. Watch playback hit t=20, verify it auto-rewinds to t=10 (status bar shows "Rewind: ...")
# 3. Click at the midpoint of the Slider, verify it seeks to TotalDuration/2
# 4. Verify bookmark triangles + loop region bands are visible on the Slider
# 5. Right-click a bookmark, edit the label, save bundle, reload bundle, verify label persists
# 6. Edit appsettings.json to set Serilog:MinimumLevel:Default = Debug, restart, verify Debug logs appear
# 7. ODX Import tab, load a large ODX, click Cancel during parse, verify IsBusy=false AND parse task actually stops
```

## 8. Out of scope (explicitly deferred)

- Multi-loop-region rewind (overlapping regions, pick innermost) — v3.10.0
- Slider keyboard seek for ↑/↓ (speed) — v3.10.0
- Scrubber drag dedup (Slider drag event-coalescing) — v3.9.x PATCH
- AppShellVM dispose unregister (defensive) — v3.9.x PATCH
- RecentSessionsService file watcher (rare UX) — v3.9.x PATCH
- Auto-save STA safety (defensive) — v3.9.x PATCH
- v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete — 76th deferred, still

## 9. Ship summary

- **Tag**: v3.9.0 (MINOR, semi-breaking — Serilog config shape, ODX service signature)
- **Parent**: v3.8.8 PATCH on origin/main (`34d5a79f3423238a2d6fd401d858ab1e68da5d9a`)
- **Files**: 14 overlay (4 production + 5 test + 5 spec/docs/config)
- **Tests**: +25-35 active
- **IPC surface**: zero change (WPF app, no IPC)
- **Semver**: MINOR bump (Serilog config migration is operator-facing; ODX service has default-param so source-compat preserved)
