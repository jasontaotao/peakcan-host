# W4 Spec — AppShellViewModel god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs` from 1019 LoC to ~400 LoC by extracting 4 logical flow groups into partial-class files. Pure mechanical refactor — zero behavioral change, zero test changes, zero API surface change.

**Architecture:** Same W3 partial-class split pattern. AppShellViewModel stays a single `sealed partial class` with 4 partial-class files in `src/PeakCan.Host.App/ViewModels/AppShellViewModel/` directory. Main file keeps constructor, [ObservableProperty] fields, DI-injected services, partial void change handlers, and Dispose. Each partial file owns one logical flow group.

**Tech Stack:** C# .NET 10, WPF, CommunityToolkit.Mvvm 8.x (partial-class pattern proven in W3 for TraceViewerViewModel).

## Global Constraints

- **Public API unchanged.** No method signatures, [RelayCommand] attributes, or [ObservableProperty] backing fields move. XAML bindings are not affected.
- **partial-class visibility.** All private methods visible across partial files; cross-flow calls stay as plain invocations (no facade, no renaming).
- **Test coverage unchanged.** No tests added, removed, or modified. Verification gate is `dotnet build` + existing test suite passing.
- **Line-ending normalized to LF.** Per v3.18.0 PATCH `.gitattributes` enforcement.
- **No behavioral change.** Every method body, xmldoc, comment, and whitespace moves verbatim.
- **No version bump.** This spec is for the design; v3.19.0 MINOR bump happens in the ship commit.

---

## Current state (1019 LoC)

`src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs` (v3.18.0 HEAD) has:
- 33 methods total
- 20+ DI fields (router, logger, view models for Trace/Dbc/Send/Signal/Stats/Script/Uds/Record/Replay/MultiFrameSend, services, dialogs)
- 11 `Show*` commands (View navigation)
- 4 channel lifecycle commands (EnumerateChannels, ConnectAsync, DisconnectAsync, OnReadLoopError)
- 6 session commands (OpenDbc, OpenSessionAsync, SaveSessionAsync, OpenRecentSessionAsync, ClearRecentSessions, RefreshRecentEntries)
- 11 LoggerMessage partial void helpers (LogReadLoopError, LogOpenDbcInvoked, LogProbeOk, LogProbeThrew, LogConnectOk, LogConnectFailed, LogConnectThrew, LogUnregisterFailed, LogDisconnectOk, LogDisconnectThrew + 1 prior)
- ctor (~150 lines), state properties, Dispose

Threshold per `automotive-coding-standards-file-size.md`: 800 LoC ceiling. AppShellViewModel is **+27% over the ceiling**.

## Target state (~400 LoC main + 4 partials)

```
src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs              (~400 LoC main)
src/PeakCan.Host.App/ViewModels/AppShellViewModel/
  ChannelFlow.cs                                                   (~280 LoC)  - Flow A
  ViewSwitchFlow.cs                                                (~200 LoC)  - Flow B
  SessionFlow.cs                                                   (~130 LoC)  - Flow C
  LogFlow.cs                                                       (~40 LoC)   - Flow D
```

**Net reduction**: 1019 → ~400 LoC main file (-60%); total lines unchanged (still ~1019 across main + partials).

## Flow boundaries

### Flow A — Channel lifecycle (~280 LoC)
Owns the channel enumeration, connect, disconnect, and runtime error handler. The "hardware control" surface.

**Methods**:
- `EnumerateChannels` (line 748) — calls `_channelEnumerator.EnumerateAsync()`, populates `ChannelList`
- `ConnectAsync` (line 820) — probe + register + register subscription; sets `StatusMessage`
- `DisconnectAsync` (line 895) — unregister + dispose; sets `StatusMessage`
- `OnReadLoopError(ReadLoopError err)` (line 960) — handles bus-off / driver-unload via `ReadLoopError` event (added in v3.16.9.4 PATCH)
- `OnIsFdChanged(bool value)` (line 246) — partial void; switches bit timing between classic and FD
- `OnSelectedChannelChanged(ChannelInfo? value)` (line 266) — partial void; auto-disconnect on channel change

**Depends on (cross-flow calls)**:
- `_channelEnumerator.EnumerateAsync()` (DI field, main file)
- `_channelProbe.ProbeAsync()` (DI field, main file)
- `_channelFactory.CreateChannel()` (DI field, main file)
- `_router.Register/Unregister()` (DI field, main file)
- `ShowTraceViewer` (Flow B) — called from ConnectAsync on success

**File**: `src/PeakCan.Host.App/ViewModels/AppShellViewModel/ChannelFlow.cs`
**Required usings**: `Microsoft.Extensions.Logging`, `PeakCan.Host.Core`, `PeakCan.Host.Infrastructure.Channel`

### Flow B — View navigation (~200 LoC)
Owns the 11 `Show*` commands and the multi-frame window opener. The "user navigates between tabs" surface.

**Methods**:
- `ShowTrace` (line 508) — uses `ViewSwitcher.Show` with `_traceViewModel`
- `ShowDbc` (line 524) — `CurrentView = GetOrCreateDbcView()` (helper stays in main)
- `ShowSend` (line 527)
- `ShowSignals` (line 538)
- `ShowStats` (line 552)
- `ShowScript` (line 568)
- `ShowUds` (line 581)
- `ShowRecord` (line 620)
- `ShowReplay` (line 633)
- `OpenMultiFrame` (line 650) — opens MultiFrameSendWindow
- `ShowTraceViewer` (line 674) — opens TraceViewerWindow as separate window

**Depends on**:
- 9 view-model fields (DI, main file): `_traceViewModel`, `_dbcViewModel`, etc.
- 9 view caches (private fields, main file): `_traceView`, `_dbcView`, etc.
- `GetOrCreateXxxView()` helpers (private methods, main file)
- `ViewSwitcher.Show` (already-extracted helper in `Composition/ViewSwitcher.cs`)

**File**: `src/PeakCan.Host.App/ViewModels/AppShellViewModel/ViewSwitchFlow.cs`
**Required usings**: `PeakCan.Host.App.Views`, `PeakCan.Host.App.Views.Uds`, `PeakCan.Host.App.Windows`, `PeakCan.Host.App.ViewModels.Uds`

### Flow C — Session open/save (~130 LoC)
Owns the DBC open, session bundle save/open, and recent-sessions list refresh. The "user opens a saved state" surface.

**Methods**:
- `OpenDbc` (line 380) — opens DBC file dialog → loads DbcService
- `OpenSessionAsync` (line 398) — opens .tmtrace bundle via TraceViewerViewModel
- `SaveSessionAsync` (line 429) — saves current Trace Viewer state
- `OpenRecentSessionAsync(string? path)` (line 451)
- `ClearRecentSessions` (line 477) — clears `_recentSessions`
- `RefreshRecentEntries` (line 494) — refreshes `RecentSessions` property

**Depends on**:
- `_recentSessions` (DI field, main file)
- `_traceViewerViewModel` (DI field, main file) — for `OpenSessionAsync`/`SaveSessionAsync`
- `_dbcService` (DI field, main file) — for `OpenDbc`
- `_fileDialogs` (DI field, main file)
- `_messageBoxPrompt` (DI field, main file)
- `TraceViewerViewModel.OpenSessionAsync/SaveSessionAsync` (cross-class)
- `ShowTraceViewer` (Flow B) — called after successful OpenSessionAsync

**File**: `src/PeakCan.Host.App/ViewModels/AppShellViewModel/SessionFlow.cs`
**Required usings**: `Microsoft.Extensions.Logging`

### Flow D — Log helpers (~40 LoC)
All 11 `[LoggerMessage]` partial void helpers. Pure source-gen declarations; no logic.

**Methods** (11 total):
- `LogReadLoopError`, `LogOpenDbcInvoked`, `LogProbeOk`, `LogProbeThrew`, `LogConnectOk`, `LogConnectFailed`, `LogConnectThrew`, `LogUnregisterFailed`, `LogDisconnectOk`, `LogDisconnectThrew` (+ 1 prior)

**Depends on**: nothing — pure declarations.

**File**: `src/PeakCan.Host.App/ViewModels/AppShellViewModel/LogFlow.cs`
**Required usings**: `Microsoft.Extensions.Logging`

### Main file — ctor + state + Dispose (~400 LoC)
Stays as-is, minus the methods moved to partials.

**Keeps**:
- All `[ObservableProperty]` fields (CurrentView, StatusMessage, ErrorMessage, ChannelList, RecentSessions, IsFd, SelectedChannel, etc.)
- All DI readonly fields
- All view-cache fields (`_traceView`, `_dbcView`, etc.)
- All `GetOrCreateXxxView()` helpers (used by Flow B via partial-class visibility)
- Constructor (~150 lines, all DI initialization)
- `Dispose` (~20 lines)
- `OnPropertyChanged` calls for shared state

## Architecture invariants (per W3 patterns)

1. **Public API unchanged**: `[RelayCommand]` attributes stay with their methods. CommunityToolkit.Mvvm source-gen needs to see them on the same declaration.
2. **partial-class visibility**: private methods are visible across partial files. Cross-flow calls (e.g. `ConnectAsync` → `ShowTraceViewer`) stay as plain invocations.
3. **State stays in main file**: all mutable state (`CurrentView`, `StatusMessage`, etc.) and DI services stay in the main file. Partial files consume them transparently.
4. **No new files outside the established directory**: `AppShellViewModel/` is a sibling directory to the main file, matching the W3 pattern (`TraceViewerViewModel/`).

## Verification

- `dotnet build` (Debug, warn-as-error): 0 errors. 1 pre-existing unrelated `CS8602` nullable warning in `DbcService.cs:157` is acceptable.
- `dotnet test --filter AppShellViewModel`: all `AppShellViewModelTests` + `AppShellViewModelMessageBoxPromptTests` pass without modification.
- `dotnet test --filter ViewSwitcher`: `ViewSwitcherTests` pass without modification.
- **Pre-existing parallel-runner flakes** (per v3.16.9.0 release notes): unchanged. Out of scope.

## Risk notes

- **R1 (low)**: Missing `using` directives. Per W3 lesson `partial-class-using-directives-are-file-scoped-not-class-scoped`, every partial file MUST include the `using` directives its methods depend on, even if the main file already has them. Mitigation: pre-scan each extracted method's body for type references and add corresponding usings before first build.
- **R2 (low)**: Deletion script precision. Per W3 lesson `deletion-script-must-preserve-namespace-and-using-clauses-when-removing-methods`, use line-range slicing (read file as `list[str]`, delete by `(start, end)` tuples, assert structural invariants) instead of regex/string-replace.
- **R3 (very low)**: Cross-flow call dependencies change. Per W3 spec, cross-flow calls stay as plain invocations. The dependency `ConnectAsync → ShowTraceViewer` works because `ShowTraceViewer` is just a method on the same partial class. No change needed.
- **R4 (YAGNI applied)**: Each `Show*` method is only 1-5 lines (thin shim to `ViewSwitcher.Show`). Could be inlined into a single `ShowView(string viewName)` method. **Rejected** — keeping the [RelayCommand] per-view matches the established WPF XAML binding pattern (one command per menu item).

## Out of scope (explicit YAGNI)

- **No facade pattern**: W3 confirmed direct partial-class visibility is sufficient. No `IChannelController` or similar extraction.
- **No sub-VM creation**: AppShellViewModel stays a single class. No `ChannelController`, `ViewNavigator`, `SessionManager` extraction.
- **No test changes**: All 2 dedicated AppShell test files + 1 ViewSwitcher test file stay unmodified. Verification gate is `dotnet build + dotnet test` passing.
- **No XAML changes**: All [RelayCommand] bindings work identically.

## Tasks remaining (preview for the plan)

1. **Task 1**: Extract Flow D (Log helpers) — smallest, lowest risk, validates the line-range deletion script for AppShellViewModel.
2. **Task 2**: Extract Flow C (Session open/save).
3. **Task 3**: Extract Flow B (View navigation).
4. **Task 4**: Extract Flow A (Channel lifecycle) — largest, most cross-flow references.
5. **Task 5**: Bump version + write release notes (v3.19.0 MINOR ship commit).
6. **Task 6**: Tier-3 push + tag + GH release.

Total: 6 tasks, ~5 source commits (1 per flow + 1 ship).

## Decision log

- **D1**: 4 partials (not 3, not 6) — matches W3 spirit without over-fragmenting. Confirmed by user.
- **D2**: Same pattern as W3 (no facade, no sub-VMs). Confirmed by user.
- **D3**: Branch name `feature/w4-app-shell-god-class` — matches `feature/v3-12-0-minor` style + W4 identifier.
- **D4**: Order tasks by flow size (smallest first): D → C → B → A. Smallest first validates the tooling; largest last catches the most cross-flow references.