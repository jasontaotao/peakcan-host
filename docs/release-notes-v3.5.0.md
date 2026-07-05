# peakcan-host v3.5.0 MINOR — `.tmtrace` Trace Viewer session bundle

## Summary

v3.5.0 MINOR ships a new file format `.tmtrace` that captures a Trace
Viewer multi-trace session so users can save "what they're looking at"
and restore it next morning. Replaces the v3.4.x release-notes
deferred big-item "session save/restore" with a concrete file format
modeled on `SequenceLibrary`.

## What's new

### `.tmtrace` file format

A versioned JSON envelope (`tmtrace/v1`) that round-trips:

- Sources: `sourceId`, `displayName`, recorded `.asc` path, ARGB color
  (per-channel byte fields), `strokeStyle` enum, per-source
  `canIdFilter` string.
- DBC path: stored as path-reference only (no DBC content embedded).
- Global CAN-ID filter string.
- Playback: `masterSourceId`, `loop`, `speed`, `scrubberValue`.
- Viewports: per-series X-axis min/max + `IsFocused` / `IsCollapsed`,
  keyed by `EffectiveKey` (SourceId.SignalKey) so multi-trace sessions
  disambiguate same-signal series.
- Envelope metadata: `version`, `schema`, `savedAt`, `appVersion`.

### Toolbar buttons

- **Save session…** — pops a `SaveFileDialog` (`*.tmtrace` filter,
  `DefaultExt = ".tmtrace"`, overwrite-prompt on) and writes the bundle
  atomically (tmp + `File.Move(overwrite: true)`).
- **Open session…** — pops an `OpenFileDialog`, loads the bundle,
  unloads any currently-loaded sources, reloads each `.asc` via the
  registry, applies playback (always to a paused cursor — never
  auto-resumes), restores the global filter + DBC path, and replays
  the saved chart viewports AFTER `SyncYAxes` runs (otherwise they get
  overwritten).

### UX behavior

- **Atomic write**: tmp file + `File.Move(overwrite: true)` with
  UTF-8 BOM. A crash mid-write leaves the previous good copy intact.
- **Corrupt-file recovery**: `Load()` returns `null` + logs Error.
  The View surfaces a `MessageBox` and the user keeps editing the
  current session.
- **Path-reference only**: recordings are NOT embedded. Typical `.asc`
  is 10 MB–1 GB; bundle size is ~1–10 KB regardless of recording size.
- **Color fidelity**: `OxyColor` round-trips losslessly via a
  per-channel JSON object (`{a,r,g,b}`); `StrokeStyle` enum round-trips
  via `JsonStringEnumConverter`.
- **Always restored to paused**: never auto-resumes playback on app
  restart — the user clicks Play explicitly.

### Missing-source UX

If a recorded `.asc` path no longer resolves (file moved, deleted, or
on an unmounted drive), the Open command collects the missing paths
into a list and the View surfaces a `MessageBox`:

> The following recordings could not be located:
>
> C:/recordings/highway_cruise.asc
>
> The session was restored with the remaining sources.

The session still loads with whatever sources resolved — partial
recovery beats all-or-nothing UX.

## Architecture

### New files

- `src/PeakCan.Host.App/Services/Trace/TraceSessionBundle.cs`
  (~120 LOC): DTOs (`TraceSessionBundleDto`, `BundleSourceDto`,
  `BundlePlaybackDto`, `BundleViewportDto`) with `[JsonPropertyName]`
  attributes. Plain POCOs — NOT VM-typed — so the bundle format is
  stable across VM refactors.
- `src/PeakCan.Host.App/Services/Trace/TraceSessionLibrary.cs`
  (~140 LOC): `Save(path, snapshot)` + `Load(path)` + atomic write +
  corrupt-recovery. Test ctor with custom path mirrors the
  `SequenceLibrary` pattern.
- `src/PeakCan.Host.App/Services/Trace/OxyColorJsonConverter.cs`
  (~60 LOC): `JsonConverter<OxyColor>` — 4-property object
  (`{a,r,g,b}`) for human readability.

### Modified files

- `src/PeakCan.Host.Core/Services/IFileDialogService.cs` — extended
  with `ShowSaveDialog(filter, defaultExt, initialDirectory)`. The
  test-mock surface was updated in `DbcViewModelTests.FakeFileDialogService`.
- `src/PeakCan.Host.App/Services/WpfFileDialogService.cs` — implements
  `ShowSaveDialog` via `Microsoft.Win32.SaveFileDialog`.
- `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` —
  `BuildSnapshot()` / `ApplySnapshotAsync(dto)` private helpers +
  `[RelayCommand] SaveSessionCommand` / `OpenSessionCommand`. Always
  restored to a paused cursor.
- `src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs` —
  `CaptureViewports()` / `ApplyViewports()` for per-series X-axis
  min/max + focus/collapse round-trip.
- `src/PeakCan.Host.App/Views/TraceViewerView.xaml` (+ .xaml.cs) — 2
  new toolbar buttons between "Load DBC…" and the legend strip, with
  click handlers that pop the dialogs and forward paths to the VM.
- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` —
  `AddSingleton<TraceSessionLibrary>()` in the Trace section.

### Tests added (+12 net)

- `tests/PeakCan.Host.App.Tests/Services/Trace/OxyColorJsonConverterTests.cs`
  (~4 tests): ARGB round-trip, four-property JSON shape, opaque-object
  deserialize, fully-transparent alpha preservation.
- `tests/PeakCan.Host.App.Tests/Services/Trace/TraceSessionLibraryTests.cs`
  (~8 tests per plan §H.8): sources round-trip, playback round-trip,
  viewports round-trip, corrupt-JSON returns null + logs, missing file
  returns null, overwrite existing, atomic-rename tmp cleanup,
  path-reference-not-embed.

## Deferred (post-v3.5.0)

- **Auto-save on app close** — YAGNI; user-triggered Save/Open only.
- **Bundle migration v1 → v2** — YAGNI until v3.5.x ships a breaking
  change. Schema bump is a one-line edit + version constant bump.
- **Hash-based `.asc` relocation** — out of scope.
- **Replay tab session save** — out of scope (separate feature, no
  multi-source concept).
- **`.tmtrace` AppShell File menu** — toolbar buttons are sufficient.

## Known issues

None known at ship time.

## Critical files

| File | LOC | Status |
|---|---|---|
| `src/PeakCan.Host.App/Services/Trace/TraceSessionBundle.cs` | ~120 | NEW |
| `src/PeakCan.Host.App/Services/Trace/TraceSessionLibrary.cs` | ~140 | NEW |
| `src/PeakCan.Host.App/Services/Trace/OxyColorJsonConverter.cs` | ~60 | NEW |
| `src/PeakCan.Host.Core/Services/IFileDialogService.cs` | +10 | MODIFIED |
| `src/PeakCan.Host.App/Services/WpfFileDialogService.cs` | +20 | MODIFIED |
| `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` | +110 | MODIFIED |
| `src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs` | +60 | MODIFIED |
| `src/PeakCan.Host.App/Views/TraceViewerView.xaml` | +6 | MODIFIED |
| `src/PeakCan.Host.App/Views/TraceViewerView.xaml.cs` | +50 | MODIFIED |
| `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` | +3 | MODIFIED |
| `tests/.../Services/Trace/OxyColorJsonConverterTests.cs` | ~80 | NEW |
| `tests/.../Services/Trace/TraceSessionLibraryTests.cs` | ~190 | NEW |

**No new third-party dependencies.** No NuGet additions.