# PeakCan Host — v1.2.0 UDS Panel Orchestrator Design

> **Baseline**: `a73e1f6` (v1.1.0 SHIPPED on main, PR #1 squash-merged 2026-06-25)
> **Target**: v1.2.0
> **Status**: Draft for review

## 1. Overview

v1.1.0 closed two OEM-blocking gaps (`IKeyDerivationAlgorithm` + JSON-loadable
`DidDatabase` / `RoutineDatabase`) but explicitly deferred three follow-up
items to v1.2 because the existing monolithic `UdsViewModel` (279 lines, 8
`[RelayCommand]`s, free-text `TextBox` for DID/Routine IDs) was working and
the spec amendment carved them out to "protect the working `ReadDid` /
`WriteDid` / `ReadDTC` flow" (see v1.1.0 spec §9, items D1/D2/D3):

- **D1** §4.5 — `UdsViewModel` refactor into a 4-panel orchestrator
- **D2** §4.8 — `SessionPanelViewModel` / `DidPanelViewModel` /
  `RoutinePanelViewModel` / `DtcPanelViewModel` plus Row types
- **D3** §4.9 — `UdsView.xaml` rewrite to use `DidDatabase` /
  `RoutineDatabase` as `DataGrid.ItemsSource` instead of free-text `TextBox`
- **D4** §6 — error-handling entries that reference panel VMs (already
  covered by the monolith's per-command `try/catch`; **CLOSED by default**)

v1.2.0 closes D1 + D2 + D3 in a single MINOR ship. v1.1.0 already registered
`DidDatabase` and `RoutineDatabase` in `AppHostBuilder` (lines 154-155) but
no UI consumer exists yet; v1.2.0 is the wiring step that completes the
chain `JSON → Core DB → Panel VM → XAML DataGrid`.

## 2. Goals

### In Scope

- **G1**: Refactor `UdsViewModel` (currently `src/PeakCan.Host.App/ViewModels/UdsViewModel.cs`,
  279 lines, 8 commands) into a thin orchestrator holding 4 panel VMs plus a
  shared `OutputLog` and a `ClearOutputCommand`. The orchestrator is ≤80
  lines and has zero `UdsClient` interaction of its own.
- **G2**: Add four panel ViewModels under
  `src/PeakCan.Host.App/ViewModels/Uds/`:
  - `SessionPanelViewModel` — 5 RelayCommands
    (`SetDefaultSessionCommand`, `SetExtendedSessionCommand`,
    `SetProgrammingSessionCommand`, `ToggleTesterPresentCommand`,
    `SecurityAccessCommand`).
  - `DidPanelViewModel` — `ReadDidCommand`, `WriteDidCommand`.
  - `RoutinePanelViewModel` — `StartRoutineCommand`, `StopRoutineCommand`,
    `QueryRoutineCommand`.
  - `DtcPanelViewModel` — `ReadDtcsCommand`, `ClearDtcsCommand`.
  Each VM receives `UdsClient` (and the relevant DB where applicable) via
  constructor injection, exposes a `ObservableCollection<RowType>` for the
  XAML, and implements an internal `IUdsPanel.AttachLog(...)` interface so
  the orchestrator can wire the shared `OutputLog` once at construction.
- **G3**: Add Row types (`DidRow`, `RoutineRow`, `DtcRow`) — small
  mutable classes carrying live values for XAML binding.
- **G4**: Replace `UdsView.xaml` (currently 116 lines, `TextBox` for DID and
  Routine, `DataGrid` for DTC only) with the full spec §4.9 layout: top
  `SessionPanel` strip with Default / Extended / Programming / TesterPresent
  checkbox / Security Access button + status text, middle `TabControl` with
  three tabs (DIDs / Routines / DTCs) where each tab's primary view is a
  `DataGrid` bound to the corresponding panel VM's row collection, bottom
  `RichTextBox` output log with a `Clear` button and per-severity color.
- **G5**: Migrate `LogEntries` (`ObservableCollection<string>`) to
  `OutputLog` (`ObservableCollection<UdsLogLine>`) where `UdsLogLine` is a
  `record(string Timestamp, string Level, string Message)`. The XAML
  code-behind listens to `CollectionChanged` and appends a colored `<Run>`
  per entry into the `RichTextBox.FlowDocument`.
- **G6**: `AppHostBuilder` registers the four panel VMs as singletons
  (`SessionPanelViewModel` / `DidPanelViewModel` / `RoutinePanelViewModel`
  / `DtcPanelViewModel`) and re-registers `UdsViewModel` against the new
  4-arg constructor (which DI auto-resolves from the panel VM
  registrations). The old `UdsViewModel(ILogger, UdsClient)` 2-arg ctor
  is removed.
- **G7**: Version bump `Directory.Build.props` from `1.1.0` to `1.2.0`;
  release notes `docs/release-notes-v1.2.0.md`.
- **G8**: Unit-test coverage ≥80% for all new code (project default floor).
  Net new tests ≥15. Existing `UdsViewModelSecurityAccessTests` cases are
  migrated to `SessionPanelViewModelTests` (the SecurityAccess logic moves
  there).

### Non-Goals (YAGNI)

- **N1**: NOT implementing OEM-specific key algorithms. Same as v1.1.0 N1.
- **N2**: NOT changing the Core layer. `UdsClient`, `DidDatabase`,
  `RoutineDatabase`, `IKeyDerivationAlgorithm`, `PlaceholderKeyAlgorithm`
  are untouched.
- **N3**: NOT changing the project file (no new packages).
- **N4**: NOT changing the DBC parser, scripting engine, signal chart, or
  trace view.
- **N5**: NOT changing `AppShell.xaml` (UDS tab is already wired).
- **N6**: NOT adding a third-party UI library (no MaterialDesignThemes,
  no ReactiveUI). Stick with `CommunityToolkit.Mvvm` + WPF primitives.
- **N7**: NOT introducing INotifyPropertyChanged on Row types beyond the
  minimum required (IsReading busy flag on `DidRow` and `Status` on
  `RoutineRow` need to fire INotifyPropertyChanged for XAML updates).
- **N8**: NOT touching J1939 / CANopen (v2.0 backlog).
- **N9**: NOT touching Linux / SocketCAN cross-platform (v2.0 backlog).
- **N10**: NOT changing the persisted format of any user data (DBC files,
  ASC recordings, JSON configs).

## 3. Architecture

### 3.1 Layer impact

```
   PeakCan.Host.App            (WPF, MVVM, BackgroundService, DI composition)
            │
            │  MODIFIED
            │    + ViewModels/Uds/UdsViewModel.cs   (NEW orchestrator, ≤80 lines)
            │    + ViewModels/Uds/SessionPanelViewModel.cs   (NEW)
            │    + ViewModels/Uds/DidPanelViewModel.cs       (NEW)
            │    + ViewModels/Uds/RoutinePanelViewModel.cs   (NEW)
            │    + ViewModels/Uds/DtcPanelViewModel.cs       (NEW)
            │    + ViewModels/Uds/UdsLogLine.cs              (NEW)
            │    + ViewModels/Uds/IUdsPanel.cs               (NEW internal interface)
            │    + ViewModels/Uds/Rows/{DidRow,RoutineRow,DtcRow}.cs (NEW)
            │    + Views/UdsView.xaml                        (REWRITE per spec §4.9)
            │    + Views/UdsView.xaml.cs                     (log appender handler)
            │    + Composition/AppHostBuilder.cs             (+ 4 panel VM DI + UdsViewModel ctor)
            │
            │  DELETED
            │    - ViewModels/UdsViewModel.cs                (replaced by ViewModels/Uds/UdsViewModel.cs)
            │
            ▼  uses
   PeakCan.Host.Infrastructure  (PEAK SDK adapter, ChannelRouter, BusStatistics) — unchanged
            ▼  uses
   PeakCan.Host.Core           (CanFrame, DBC parser, SignalDecoder, Result, Uds*, Database/*)
                                — unchanged except as noted below
```

No Core / Infrastructure changes EXCEPT for the single testability hook
listed below. NetArchTest rule 2 (Core must not depend on `Peak.Can.Basic`)
is preserved.

**Core testability hook (added 2026-06-25 during v1.2.0 implementation,
discovered while building `SessionPanelViewModelTests` + `DidPanelViewModelTests` + `RoutinePanelViewModelTests` + `DtcPanelViewModelTests`):**

Six `UdsClient` async service methods gained the `virtual` keyword (none
had it before despite the two `SecurityAccessAsync` overloads being
`virtual` since v1.1.0):

- `src/PeakCan.Host.Core/Uds/UdsClient.cs` — `DiagnosticSessionControlAsync`,
  `ReadDataByIdentifierAsync`, `WriteDataByIdentifierAsync`,
  `RoutineControlAsync`, `ReadDtcInformationAsync`, and
  `ClearDiagnosticInformationAsync` are all `virtual`. Both
  `SecurityAccessAsync` overloads were already `virtual`. Brings the
  class to a consistent testability surface. Non-behavioral: callers see
  no API change.

Rationale: every panel `ViewModel` (Session/DID/Routine/DTC; §4.5–§4.8)
takes `UdsClient` directly via DI; their tests need a
`RecordingUdsClient : UdsClient` test double that overrides the relevant
async service method to record calls without going through the real
ISO-TP transport. Without `virtual` the test double cannot intercept the
call, and the panel-command tests cannot verify the right parameters
are sent to the bus. Each panel test double only overrides the methods
it actually exercises; the remaining methods stay non-overridden and
inherited.

### 3.2 Component diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│ PeakCan.Host.App / Views / UdsView.xaml                             │
│  ┌───────────────────────────────────────────────────────────────┐  │
│  │ SessionPanel                                                  │  │
│  │  [Default] [Extended] [Programming] [TP☐] [SecAccess]         │  │
│  │  Session: <CurrentSession>  Level: 0x<SecurityLevel>          │  │
│  ├───────────────────────────────────────────────────────────────┤  │
│  │ ┌─────────────┬────────────────┬─────────────────────────┐    │  │
│  │ │ DIDs         │ Routines        │ DTCs                    │    │  │
│  │ │ DataGrid     │ DataGrid        │ DataGrid                │    │  │
│  │ │ DidPanelVM   │ RoutinePanelVM  │ DtcPanelVM              │    │  │
│  │ ├─────────────┴────────────────┴─────────────────────────┤    │  │
│  │ │ Output Log (RichTextBox, color-coded by Level, [Clear]) │    │  │
│  │ └─────────────────────────────────────────────────────────┘    │  │
│  └───────────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────┘
                                │
                                ▼  DataContext
┌─────────────────────────────────────────────────────────────────────┐
│ UdsViewModel (orchestrator, ≤80 lines)                              │
│  ├── Session   : SessionPanelViewModel                              │
│  ├── Did       : DidPanelViewModel                                  │
│  ├── Routine   : RoutinePanelViewModel                              │
│  ├── Dtc       : DtcPanelViewModel                                  │
│  ├── OutputLog : ObservableCollection<UdsLogLine>                   │
│  └── ClearOutputCommand                                             │
│       (each panel.AttachLog(OutputLog) wired in ctor)               │
└─────────────────────────────────────────────────────────────────────┘
                                │
                                ▼  via DI
┌─────────────────────────────────────────────────────────────────────┐
│ PeakCan.Host.App / ViewModels / Uds / Panel VMs                     │
│  SessionPanelViewModel   ctor(UdsClient, ILogger)                   │
│  DidPanelViewModel       ctor(UdsClient, DidDatabase)               │
│  RoutinePanelViewModel   ctor(UdsClient, RoutineDatabase)           │
│  DtcPanelViewModel       ctor(UdsClient)                            │
│  (each implements IUdsPanel.AttachLog)                              │
└─────────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────────┐
│ PeakCan.Host.Core / Uds (unchanged from v1.1.0)                     │
│  UdsClient, IKeyDerivationAlgorithm / PlaceholderKeyAlgorithm,      │
│  DidDatabase, RoutineDatabase, IsoTpLayer, UdsTimer, ...             │
└─────────────────────────────────────────────────────────────────────┘
```

### 3.3 Namespace migration

The old `src/PeakCan.Host.App/ViewModels/UdsViewModel.cs` lived in
`PeakCan.Host.App.ViewModels`. The new orchestrator lives at
`src/PeakCan.Host.App/ViewModels/Uds/UdsViewModel.cs` in
`PeakCan.Host.App.ViewModels.Uds`. Affected files (verified against
v1.1.0 tree):

- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs:163` — `using`
  update needed for the type reference on the `AddSingleton<UdsViewModel>()`
  line; replace with fully-qualified `PeakCan.Host.App.ViewModels.Uds.UdsViewModel`
  to match the convention already used on lines 152 / 154 / 155 / 161.
- `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs` — currently
  declares `namespace PeakCan.Host.App.ViewModels` (line 11) and
  references `UdsViewModel` implicitly (lines 79, 186) via the same
  namespace. After the move, add `using PeakCan.Host.App.ViewModels.Uds;`
  at the top so the field (`_udsViewModel`, line 79) and the ctor
  parameter (`UdsViewModel udsViewModel`, line 186) still resolve.
- `tests/PeakCan.Host.App.Tests/...` — the existing
  `UdsViewModelSecurityAccessTests` references the old
  `UdsViewModel` directly. Those tests are deleted in this PR (cases
  migrated to `SessionPanelViewModelTests`), so no `using` update is
  required for the surviving tests.

The XAML namespace `xmlns:views="clr-namespace:PeakCan.Host.App.Views"`
in `AppShell.xaml` is unaffected: `UdsView` and `UdsView.xaml.cs` are
not moved (only their DataContext's *type* changes). WPF resolves the
DataContext via the runtime type, not via a compile-time XAML namespace
reference. `AppShellViewModel` constructs `_udsView = new UdsView {
DataContext = _udsViewModel }` (line 295) at runtime — the
`UdsViewModel` reference resolves through the new `using` directive.

## 4. Components

### 4.1 `UdsViewModel` (REPLACED, App)

**Location**: `src/PeakCan.Host.App/ViewModels/Uds/UdsViewModel.cs`

```csharp
namespace PeakCan.Host.App.ViewModels.Uds;

/// <summary>
/// Orchestrator for the UDS diagnostic tab. Holds four panel ViewModels
/// (Session / DID / Routine / DTC), a shared output log, and a clear-output
/// command. Owns no UdsClient interaction of its own — each panel owns its
/// own RelayCommands and talks to UdsClient directly.
/// </summary>
public sealed partial class UdsViewModel : ObservableObject
{
    public SessionPanelViewModel Session { get; }
    public DidPanelViewModel     Did     { get; }
    public RoutinePanelViewModel Routine { get; }
    public DtcPanelViewModel     Dtc     { get; }

    /// <summary>Shared UDS log; all panels append here.</summary>
    public ObservableCollection<UdsLogLine> OutputLog { get; } = new();

    public UdsViewModel(
        SessionPanelViewModel session,
        DidPanelViewModel     did,
        RoutinePanelViewModel routine,
        DtcPanelViewModel     dtc)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(did);
        ArgumentNullException.ThrowIfNull(routine);
        ArgumentNullException.ThrowIfNull(dtc);

        Session = session;
        Did     = did;
        Routine = routine;
        Dtc     = dtc;

        Session.AttachLog(OutputLog);
        Did.AttachLog(OutputLog);
        Routine.AttachLog(OutputLog);
        Dtc.AttachLog(OutputLog);
    }

    [RelayCommand]
    private void ClearOutput() => OutputLog.Clear();
}
```

### 4.2 `IUdsPanel` (NEW, App, internal)

**Location**: `src/PeakCan.Host.App/ViewModels/Uds/IUdsPanel.cs`

```csharp
namespace PeakCan.Host.App.ViewModels.Uds;

/// <summary>
/// Internal contract: a panel VM exposes a single AttachLog hook so the
/// orchestrator can wire a shared ObservableCollection at construction time
/// without forcing each panel ctor to take one more parameter.
/// </summary>
internal interface IUdsPanel
{
    void AttachLog(ObservableCollection<UdsLogLine> log);
}
```

### 4.3 `UdsLogLine` (NEW, App)

**Location**: `src/PeakCan.Host.App/ViewModels/Uds/UdsLogLine.cs`

```csharp
namespace PeakCan.Host.App.ViewModels.Uds;

/// <summary>
/// One line of UDS output log. Replaces the v1.1.0 LogEntries string with
/// a structured (Timestamp, Level, Message) so the XAML can color-code by
/// severity without re-parsing.
/// </summary>
public sealed record UdsLogLine(string Timestamp, string Level, string Message);
```

`Level` is one of `"Info"`, `"Warn"`, `"Error"`. Color mapping in the XAML
code-behind:

| Level | Brush |
|---|---|
| `"Info"` | (default text) |
| `"Warn"` | `#DCDCAA` (VS Code Warning Yellow) |
| `"Error"` | `#F48771` (VS Code Error Red) |

### 4.4 Row types (NEW, App)

**Location**: `src/PeakCan.Host.App/ViewModels/Uds/Rows/`

```csharp
// DidRow.cs
public sealed class DidRow : ObservableObject
{
    public ushort Id         { get; init; }
    public string Name       { get; init; } = "";
    public int    LengthBytes { get; init; }
    public bool   Writable   { get; init; }
    public string WritableDisplay => Writable ? "R/W" : "R/O";

    [ObservableProperty] private string? _readValue;
    [ObservableProperty] private bool    _isReading;
}

// RoutineRow.cs
public sealed class RoutineRow : ObservableObject
{
    public ushort Id   { get; init; }
    public string Name { get; init; } = "";

    [ObservableProperty] private string  _status     = "Idle";
    [ObservableProperty] private string? _lastResult;
}

// DtcRow.cs
public sealed class DtcRow
{
    public uint   Code        { get; init; }
    public byte   Status      { get; init; }
    public string Description { get; init; } = "";

    public string CodeDisplay   => $"0x{Code:X6}";
    public string StatusDisplay => $"0x{Status:X2}";
}
```

`DidRow` and `RoutineRow` derive from `ObservableObject` because their
`IsReading` / `Status` / `ReadValue` / `LastResult` properties mutate
during a command and XAML must react. `DtcRow` is a plain record-style
class because the panel replaces the entire `Dtcs` collection on each
`ReadDtcsCommand`.

### 4.5 `SessionPanelViewModel` (NEW, App)

**Location**: `src/PeakCan.Host.App/ViewModels/Uds/SessionPanelViewModel.cs`

```csharp
public sealed partial class SessionPanelViewModel : ObservableObject, IUdsPanel
{
    private readonly UdsClient _udsClient;
    private readonly ILogger<SessionPanelViewModel> _logger;
    private CancellationTokenSource? _testerPresentCts;
    private ObservableCollection<UdsLogLine>? _log;

    [ObservableProperty] private string  _currentSession  = "Default";
    [ObservableProperty] private byte?   _securityLevel;            // null = not authenticated
    [ObservableProperty] private bool    _testerPresentActive;

    public SessionPanelViewModel(UdsClient udsClient, ILogger<SessionPanelViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(udsClient);
        ArgumentNullException.ThrowIfNull(logger);
        _udsClient = udsClient;
        _logger    = logger;
    }

    public void AttachLog(ObservableCollection<UdsLogLine> log) => _log = log;

    [RelayCommand]
    private async Task SetDefaultSessionAsync()
        => await SetSessionAsync(0x01, "Default").ConfigureAwait(false);

    [RelayCommand]
    private async Task SetExtendedSessionAsync()
        => await SetSessionAsync(0x02, "Extended").ConfigureAwait(false);

    [RelayCommand]
    private async Task SetProgrammingSessionAsync()
        => await SetSessionAsync(0x03, "Programming").ConfigureAwait(false);

    [RelayCommand]
    private void ToggleTesterPresent() { /* start/stop background loop, see §5.1 */ }

    [RelayCommand]
    private async Task SecurityAccessAsync()
    {
        try
        {
            AppendLog("Info", "Requesting security access (level 0x01)...");
            var response = await _udsClient.SecurityAccessAsync((byte)0x01, CancellationToken.None)
                                          .ConfigureAwait(false);
            SecurityLevel = 0x01;
            AppendLog("Info", $"SecurityAccess 0x01 succeeded ({response.Length} bytes).");
        }
        catch (KeyAlgorithmNotConfiguredException ex)
        {
            _logger.LogWarning(ex, "SecurityAccess key algorithm not configured for level 0x{Level:X2}",
                ex.SecurityLevel);
            AppendLog("Warn", ex.Message);
            AppendLog("Info", "Hint: register an IKeyDerivationAlgorithm implementation in DI before invoking SecurityAccess.");
            SecurityLevel = null;
        }
        catch (UdsNegativeResponseException ex)
        {
            AppendLog("Warn", $"Security access failed: NRC 0x{ex.ResponseCode:X2}");
            SecurityLevel = null;
        }
        catch (InvalidOperationException ex)
        {
            AppendLog("Error", $"Security access error: {ex.Message}");
            SecurityLevel = null;
        }
        catch (Exception ex)
        {
            AppendLog("Error", $"Security access error: {ex.Message}");
            SecurityLevel = null;
        }
    }

    private async Task SetSessionAsync(byte subFunction, string label)
    {
        try
        {
            await _udsClient.DiagnosticSessionControlAsync(subFunction).ConfigureAwait(false);
            CurrentSession = label;
            AppendLog("Info", $"Session → {label}");
        }
        catch (Exception ex)
        {
            AppendLog("Error", $"Set session {label} failed: {ex.Message}");
        }
    }

    private void AppendLog(string level, string message)
        => _log?.Add(new UdsLogLine(DateTime.Now.ToString("HH:mm:ss.fff"), level, message));
}
```

Note: the existing v1.1.0 monolith `UdsViewModel.ConnectAsync` (`DiagnosticSessionControlAsync(0x02)` + SessionText = "Extended" + StatusText = "Connected") is
replaced by `SetExtendedSessionAsync` here. The "Connect" button on the new
top strip is **renamed to "Extended"** (and the orchestrator drops the
"Connected/Disconnected" `StatusText` field — the equivalent is now
`Session.CurrentSession == "Default"` vs `"Extended"` / `"Programming"`).
A `Connect` button that maps directly to "Extended session" is preserved
semantically; the existing UX is improved, not broken, because opening the
UDS tab in v1.1.0 already implied a channel connection (the
`ConnectAsync` failure was the channel-disconnected error path).

### 4.6 `DidPanelViewModel` (NEW, App)

**Location**: `src/PeakCan.Host.App/ViewModels/Uds/DidPanelViewModel.cs`

```csharp
public sealed partial class DidPanelViewModel : ObservableObject, IUdsPanel
{
    private readonly UdsClient _udsClient;
    private ObservableCollection<UdsLogLine>? _log;

    public ObservableCollection<DidRow> Dids { get; } = new();
    [ObservableProperty] private DidRow?  _selectedDid;
    [ObservableProperty] private string   _writeValue = "";
    [ObservableProperty] private string?  _lastResult;

    public DidPanelViewModel(UdsClient udsClient, DidDatabase didDb)
    {
        ArgumentNullException.ThrowIfNull(udsClient);
        ArgumentNullException.ThrowIfNull(didDb);
        _udsClient = udsClient;

        foreach (var d in didDb.All)
            Dids.Add(new DidRow { Id = d.Id, Name = d.Name, LengthBytes = d.LengthBytes, Writable = d.Writable });
        if (Dids.Count > 0) SelectedDid = Dids[0];
    }

    public void AttachLog(ObservableCollection<UdsLogLine> log) => _log = log;

    [RelayCommand(CanExecute = nameof(CanReadDid))]
    private async Task ReadDidAsync()
    {
        var row = SelectedDid;
        if (row is null) return;
        row.IsReading = true;
        ReadDidCommand.NotifyCanExecuteChanged();
        try
        {
            AppendLog("Info", $"Reading DID 0x{row.Id:X4}...");
            var data = await _udsClient.ReadDataByIdentifierAsync(row.Id).ConfigureAwait(false);
            row.ReadValue = BitConverter.ToString(data).Replace("-", " ");
            LastResult    = row.ReadValue;
            AppendLog("Info", $"DID 0x{row.Id:X4} = {row.ReadValue}");
        }
        catch (UdsNegativeResponseException ex)
        {
            AppendLog("Warn", $"Read DID 0x{row.Id:X4} failed: NRC 0x{ex.ResponseCode:X2}");
        }
        catch (Exception ex)
        {
            AppendLog("Error", $"Read DID 0x{row.Id:X4} error: {ex.Message}");
        }
        finally
        {
            row.IsReading = false;
            ReadDidCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanReadDid() => SelectedDid is { IsReading: false };

    [RelayCommand(CanExecute = nameof(CanReadDid))]
    private async Task WriteDidAsync()
    {
        var row = SelectedDid;
        // Note: Writable is NOT a local guard — the ECU is the authority
        // on writability and returns NRC 0x31 (requestOutOfRange) for
        // read-only DIDs. UI-level enforcement of Writable happens in
        // UdsView.xaml via IsEnabled="{Binding Did.SelectedDid.Writable}".
        // Clarified 2026-06-25 during v1.2.0 implementation: this matches
        // v1.1.0 monolith's WriteDidAsync behavior.
        if (row is null) return;
        try
        {
            var data = ParseHexString(WriteValue);
            AppendLog("Info", $"Writing DID 0x{row.Id:X4} ({data.Length} bytes)...");
            await _udsClient.WriteDataByIdentifierAsync(row.Id, data).ConfigureAwait(false);
            AppendLog("Info", $"DID 0x{row.Id:X4} written successfully");
        }
        catch (UdsNegativeResponseException ex)
        {
            AppendLog("Warn", $"Write DID 0x{row.Id:X4} failed: NRC 0x{ex.ResponseCode:X2}");
        }
        catch (FormatException ex)
        {
            AppendLog("Error", $"Write DID 0x{row.Id:X4}: invalid hex input — {ex.Message}");
        }
        catch (Exception ex)
        {
            AppendLog("Error", $"Write DID 0x{row.Id:X4} error: {ex.Message}");
        }
    }

    private static byte[] ParseHexString(string hex)
    {
        // Same shape as v1.1.0 monolith's helper (lines 245-257).
        hex = hex.Replace(" ", "").Replace("-", "");
        if (hex.Length % 2 != 0) hex = "0" + hex;
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    private void AppendLog(string level, string message)
        => _log?.Add(new UdsLogLine(DateTime.Now.ToString("HH:mm:ss.fff"), level, message));
}
```

### 4.7 `RoutinePanelViewModel` (NEW, App)

```csharp
public sealed partial class RoutinePanelViewModel : ObservableObject, IUdsPanel
{
    private readonly UdsClient _udsClient;
    private ObservableCollection<UdsLogLine>? _log;

    public ObservableCollection<RoutineRow> Routines { get; } = new();
    [ObservableProperty] private RoutineRow? _selectedRoutine;

    public RoutinePanelViewModel(UdsClient udsClient, RoutineDatabase routineDb)
    {
        ArgumentNullException.ThrowIfNull(udsClient);
        ArgumentNullException.ThrowIfNull(routineDb);
        _udsClient = udsClient;

        if (routineDb.All.Count == 0)
        {
            // Empty list is normal; do not log here. Orchestrator's DI
            // registration may run before OutputLog is attached, and the
            // empty-list condition is silent at startup. RoutineDatabase
            // already logs a Warning when JSON is malformed.
        }
        foreach (var r in routineDb.All)
            Routines.Add(new RoutineRow { Id = r.Id, Name = r.Name });
        if (Routines.Count > 0) SelectedRoutine = Routines[0];
    }

    public void AttachLog(ObservableCollection<UdsLogLine> log) => _log = log;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task StartRoutineAsync() => RunAsync(0x01);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task StopRoutineAsync()  => RunAsync(0x02);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task QueryRoutineAsync() => RunAsync(0x03);

    private bool CanRun() => SelectedRoutine is { Status: "Idle" or "Completed" or "Failed" or "Stopped" };

    private async Task RunAsync(byte subFunction)
    {
        var row = SelectedRoutine;
        if (row is null) return;
        var label = subFunction switch { 0x01 => "Start", 0x02 => "Stop", 0x03 => "Query", _ => $"subFn 0x{subFunction:X2}" };
        row.Status = "Running";
        StartRoutineCommand.NotifyCanExecuteChanged();
        StopRoutineCommand.NotifyCanExecuteChanged();
        QueryRoutineCommand.NotifyCanExecuteChanged();
        try
        {
            AppendLog("Info", $"{label} routine 0x{row.Id:X4}...");
            var result = await _udsClient.RoutineControlAsync(subFunction, row.Id).ConfigureAwait(false);
            row.LastResult = BitConverter.ToString(result).Replace("-", " ");
            row.Status     = "Completed";
            AppendLog("Info", $"{label} routine 0x{row.Id:X4} → {row.LastResult}");
        }
        catch (UdsNegativeResponseException ex)
        {
            row.Status = "Failed";
            AppendLog("Warn", $"{label} routine 0x{row.Id:X4} failed: NRC 0x{ex.ResponseCode:X2}");
        }
        catch (Exception ex)
        {
            row.Status = "Failed";
            AppendLog("Error", $"{label} routine 0x{row.Id:X4} error: {ex.Message}");
        }
        finally
        {
            StartRoutineCommand.NotifyCanExecuteChanged();
            StopRoutineCommand.NotifyCanExecuteChanged();
            QueryRoutineCommand.NotifyCanExecuteChanged();
        }
    }

    private void AppendLog(string level, string message)
        => _log?.Add(new UdsLogLine(DateTime.Now.ToString("HH:mm:ss.fff"), level, message));
}
```

### 4.8 `DtcPanelViewModel` (NEW, App)

```csharp
public sealed partial class DtcPanelViewModel : ObservableObject, IUdsPanel
{
    private readonly UdsClient _udsClient;
    private ObservableCollection<UdsLogLine>? _log;

    public ObservableCollection<DtcRow> Dtcs { get; } = new();

    public DtcPanelViewModel(UdsClient udsClient)
    {
        ArgumentNullException.ThrowIfNull(udsClient);
        _udsClient = udsClient;
    }

    public void AttachLog(ObservableCollection<UdsLogLine> log) => _log = log;

    [RelayCommand]
    private async Task ReadDtcsAsync()
    {
        try
        {
            AppendLog("Info", "Reading DTCs (reportByStatusMask=0xFF)...");
            var data = await _udsClient.ReadDtcInformationAsync(0x02, 0xFF).ConfigureAwait(false);

            Dtcs.Clear();
            for (int i = 0; i + 3 < data.Length; i += 4)
            {
                var code   = (uint)((data[i] << 16) | (data[i + 1] << 8) | data[i + 2]);
                var status = data[i + 3];
                Dtcs.Add(new DtcRow { Code = code, Status = status, Description = GetDtcDescription(code) });
            }

            AppendLog("Info", $"Found {Dtcs.Count} DTCs");
        }
        catch (UdsNegativeResponseException ex)
        {
            AppendLog("Warn", $"Read DTCs failed: NRC 0x{ex.ResponseCode:X2}");
        }
        catch (Exception ex)
        {
            AppendLog("Error", $"Read DTCs error: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ClearDtcsAsync()
    {
        try
        {
            AppendLog("Info", "Clearing all DTCs...");
            await _udsClient.ClearDiagnosticInformationAsync().ConfigureAwait(false);
            Dtcs.Clear();
            AppendLog("Info", "All DTCs cleared");
        }
        catch (UdsNegativeResponseException ex)
        {
            AppendLog("Warn", $"Clear DTCs failed: NRC 0x{ex.ResponseCode:X2}");
        }
        catch (Exception ex)
        {
            AppendLog("Error", $"Clear DTCs error: {ex.Message}");
        }
    }

    private static string GetDtcDescription(uint dtc) => dtc switch
    {
        <= 0x00FFFF => "Powertrain",
        <= 0x01FFFF => "Chassis",
        <= 0x02FFFF => "Body",
        <= 0x03FFFF => "Network",
        _           => "Unknown"
    };

    private void AppendLog(string level, string message)
        => _log?.Add(new UdsLogLine(DateTime.Now.ToString("HH:mm:ss.fff"), level, message));
}
```

### 4.9 `UdsView.xaml` (REWRITTEN, App)

**Location**: `src/PeakCan.Host.App/Views/UdsView.xaml`

Layout follows v1.1.0 spec §4.9 with one naming correction (the v1.1.0 spec
called the top control "SessionPanel" which conflicts with the new
`SessionPanelViewModel` name; here the top is just the layout header strip
and the XAML binds to `{Binding Session.CurrentSession}` etc.):

**Style note**: `App.xaml` has an empty `<Application.Resources>` block and
no static brushes or converters are defined in the project. The new XAML
uses inline brush literals (`#F0F0F0` for the session strip, `#1E1E1E` for
the log background, `#D4D4D4` for log foreground) and a computed
`WritableDisplay` property on `DidRow` (replacing the missing
`BoolToReadWriteConverter`). This matches the pattern already used in
`SignalView.xaml` / `TraceView.xaml` (`Background="#F8F8F8"` inline).

```xml
<UserControl x:Class="PeakCan.Host.App.Views.UdsView" ...>
  <DockPanel>
    <!-- Top: Session header strip -->
    <Border DockPanel.Dock="Top" Padding="8" Background="#F0F0F0">
      <StackPanel Orientation="Horizontal">
        <Button Content="Default"     Command="{Binding Session.SetDefaultSessionCommand}"/>
        <Button Content="Extended"    Command="{Binding Session.SetExtendedSessionCommand}"/>
        <Button Content="Programming" Command="{Binding Session.SetProgrammingSessionCommand}"/>
        <Separator/>
        <CheckBox Content="TesterPresent" IsChecked="{Binding Session.TesterPresentActive, Mode=OneWay}"
                  Command="{Binding Session.ToggleTesterPresentCommand}"/>
        <Separator/>
        <Button Content="SecurityAccess (Level 1)"
                Command="{Binding Session.SecurityAccessCommand}"/>
        <TextBlock Text="{Binding Session.CurrentSession, StringFormat='Session: {0}'}"
                   Margin="12,0,0,0" VerticalAlignment="Center"/>
        <TextBlock Text="{Binding Session.SecurityLevel, StringFormat='Level: 0x{0:X2}', TargetNullValue='(not authenticated)'}"
                   Margin="12,0,0,0" VerticalAlignment="Center"/>
      </StackPanel>
    </Border>

    <!-- Bottom: Output Log -->
    <Border DockPanel.Dock="Bottom" Padding="8" Background="#1E1E1E">
      <DockPanel>
        <Button DockPanel.Dock="Top" Content="Clear" HorizontalAlignment="Right"
                Command="{Binding ClearOutputCommand}" Margin="0,0,0,4"/>
        <RichTextBox x:Name="LogBox" IsReadOnly="True" IsDocumentEnabled="True"
                     Background="#1E1E1E" Foreground="#D4D4D4"
                     FontFamily="Consolas" FontSize="12"
                     VerticalScrollBarVisibility="Auto">
          <FlowDocument PageWidth="2400">
            <Paragraph x:Name="LogParagraph"/>
          </FlowDocument>
        </RichTextBox>
      </DockPanel>
    </Border>

    <!-- Middle: TabControl -->
    <TabControl>
      <TabItem Header="DIDs">
        <Grid>
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width="2*"/>
            <ColumnDefinition Width="3*"/>
          </Grid.ColumnDefinitions>
          <DataGrid ItemsSource="{Binding Did.Dids}" SelectedItem="{Binding Did.SelectedDid}"
                    AutoGenerateColumns="False" IsReadOnly="True">
            <DataGrid.Columns>
              <DataGridTextColumn Header="ID"     Binding="{Binding Id, StringFormat=0x{0:X4}}"/>
              <DataGridTextColumn Header="Name"   Binding="{Binding Name}"/>
              <DataGridTextColumn Header="Length" Binding="{Binding LengthBytes}"/>
              <DataGridTextColumn Header="R/W"    Binding="{Binding WritableDisplay}"/>
              <DataGridTextColumn Header="Value"  Binding="{Binding ReadValue}"/>
            </DataGrid.Columns>
          </DataGrid>
          <StackPanel Grid.Column="1" Margin="12">
            <TextBlock Text="{Binding Did.SelectedDid.Name, StringFormat='Selected DID: {0}', TargetNullValue='(none selected)'}"
                       FontWeight="Bold"/>
            <TextBlock Text="{Binding Did.SelectedDid.LengthBytes, StringFormat='Length: {0} bytes', TargetNullValue=''}"
                       Margin="0,4,0,0"/>
            <TextBlock Text="Write value (hex)" Margin="0,8,0,0"/>
            <TextBox  Text="{Binding Did.WriteValue, UpdateSourceTrigger=PropertyChanged}"
                      IsEnabled="{Binding Did.SelectedDid.Writable}"/>
            <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
              <Button Content="Read"  Command="{Binding Did.ReadDidCommand}"  Margin="0,0,8,0"/>
              <Button Content="Write" Command="{Binding Did.WriteDidCommand}"/>
            </StackPanel>
            <TextBlock Text="{Binding Did.LastResult, StringFormat='Last: {0}'}"
                       Margin="0,8,0,0" FontFamily="Consolas" TextWrapping="Wrap"/>
          </StackPanel>
        </Grid>
      </TabItem>

      <TabItem Header="Routines">
        <DockPanel>
          <DataGrid DockPanel.Dock="Top" Height="200"
                    ItemsSource="{Binding Routine.Routines}" SelectedItem="{Binding Routine.SelectedRoutine}"
                    AutoGenerateColumns="False" IsReadOnly="True">
            <DataGrid.Columns>
              <DataGridTextColumn Header="ID"     Binding="{Binding Id, StringFormat=0x{0:X4}}"/>
              <DataGridTextColumn Header="Name"   Binding="{Binding Name}"/>
              <DataGridTextColumn Header="Status" Binding="{Binding Status}"/>
              <DataGridTextColumn Header="Result" Binding="{Binding LastResult}"/>
            </DataGrid.Columns>
          </DataGrid>
          <StackPanel Orientation="Horizontal" Margin="8">
            <Button Content="Start" Command="{Binding Routine.StartRoutineCommand}"/>
            <Button Content="Stop"  Command="{Binding Routine.StopRoutineCommand}"  Margin="8,0,0,0"/>
            <Button Content="Query" Command="{Binding Routine.QueryRoutineCommand}" Margin="8,0,0,0"/>
            <TextBlock Text="{Binding Routine.SelectedRoutine.LastResult, StringFormat='Last: {0}'}"
                       Margin="12,0,0,0" VerticalAlignment="Center" FontFamily="Consolas"/>
          </StackPanel>
        </DockPanel>
      </TabItem>

      <TabItem Header="DTCs">
        <DockPanel>
          <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="8">
            <Button Content="Read DTCs"  Command="{Binding Dtc.ReadDtcsCommand}"/>
            <Button Content="Clear DTCs" Command="{Binding Dtc.ClearDtcsCommand}" Margin="8,0,0,0"/>
          </StackPanel>
          <DataGrid ItemsSource="{Binding Dtc.Dtcs}" AutoGenerateColumns="False" IsReadOnly="True">
            <DataGrid.Columns>
              <DataGridTextColumn Header="Code"        Binding="{Binding CodeDisplay}"/>
              <DataGridTextColumn Header="Status"      Binding="{Binding StatusDisplay}"/>
              <DataGridTextColumn Header="Description" Binding="{Binding Description}" Width="*"/>
            </DataGrid.Columns>
          </DataGrid>
        </DockPanel>
      </TabItem>
    </TabControl>
  </DockPanel>
</UserControl>
```

**`UdsView.xaml.cs`** adds:

```csharp
public partial class UdsView : UserControl
{
    private static readonly SolidColorBrush WarnBrush  = new(Color.FromRgb(0xDC, 0xDC, 0xAA));
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromRgb(0xF4, 0x87, 0x71));

    public UdsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => DetachLog();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachLog();
        if (e.NewValue is UdsViewModel vm)
        {
            vm.OutputLog.CollectionChanged += OnLogCollectionChanged;
        }
    }

    private void OnLogCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        foreach (UdsLogLine line in e.NewItems!)
        {
            var run = new Run($"[{line.Timestamp}] {line.Message}")
            {
                Foreground = line.Level switch
                {
                    "Warn"  => WarnBrush,
                    "Error" => ErrorBrush,
                    _       => null,  // default
                }
            };
            LogParagraph.Inlines.Add(run);
        }
        LogBox.ScrollToEnd();

        // Trim if over cap.
        if (LogParagraph.Inlines.Count > 500)
            LogParagraph.Inlines.Remove(LogParagraph.Inlines.FirstInline);
    }

    private void DetachLog()
    {
        if (DataContext is UdsViewModel oldVm)
            oldVm.OutputLog.CollectionChanged -= OnLogCollectionChanged;
    }
}
```

### 4.10 `AppHostBuilder` DI updates (MODIFY, App)

**Location**: `src/PeakCan.Host.App/Composition/AppHostBuilder.cs`

Replace `builder.Services.AddSingleton<UdsViewModel>();` with:

```csharp
builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.SessionPanelViewModel>();
builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.DidPanelViewModel>();
builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.RoutinePanelViewModel>();
builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.DtcPanelViewModel>();
builder.Services.AddSingleton<PeakCan.Host.App.ViewModels.Uds.UdsViewModel>();
```

The new `UdsViewModel` ctor is `(SessionPanelViewModel, DidPanelViewModel, RoutinePanelViewModel, DtcPanelViewModel)` — DI resolves all four panel VMs from the registrations above. Panel VMs' ctors are unchanged from §4.5–§4.8: each takes `UdsClient` (already registered as a singleton factory on lines 157-162 of the v1.1.0 AppHostBuilder) plus, for `DidPanelViewModel` / `RoutinePanelViewModel`, the corresponding `DidDatabase` / `RoutineDatabase` (already registered on lines 154-155).

### 4.11 Version bump (MODIFY)

**Location**: `Directory.Build.props`

`<Version>1.1.0</Version>` → `<Version>1.2.0</Version>`.

`<AssemblyVersion>1.1.0.0</AssemblyVersion>` → `<AssemblyVersion>1.2.0.0</AssemblyVersion>`.

`<FileVersion>1.1.0.0</FileVersion>` → `<FileVersion>1.2.0.0</FileVersion>`.

## 5. Data Flow

### 5.1 TesterPresent background loop

`SessionPanelViewModel.ToggleTesterPresent` flips `_testerPresentActive`:

```csharp
[RelayCommand]
private void ToggleTesterPresent()
{
    if (_testerPresentActive)
    {
        _testerPresentCts?.Cancel();
        _testerPresentCts?.Dispose();
        _testerPresentCts = null;
        TesterPresentActive = false;
        AppendLog("Info", "TesterPresent stopped");
        return;
    }
    _testerPresentCts = new CancellationTokenSource();
    var ct = _testerPresentCts.Token;
    TesterPresentActive = true;
    AppendLog("Info", "TesterPresent started (2s interval)");
    _ = Task.Run(async () =>
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _udsClient.TesterPresentAsync().ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* expected on stop */ }
        catch (Exception ex)
        {
            AppendLog("Error", $"TesterPresent loop error: {ex.Message}");
            TesterPresentActive = false;
        }
    }, ct);
}
```

This is a small functional improvement over v1.1.0's monolithic
`TesterPresentAsync` which sent exactly one frame on each click. The
checkbox semantics make more sense with a continuous background loop.

### 5.2 OutputLog lifecycle and cap

- Initial state: empty `ObservableCollection<UdsLogLine>`.
- Each panel `AppendLog(level, msg)` adds a single `UdsLogLine`.
- `UdsView.xaml.cs` listens to `CollectionChanged` and appends a
  `Run` to `LogParagraph.Inlines`. When `Inlines.Count > 500`, the
  earliest inline is removed. (Cap raised from v1.1.0's 100 to 500
  because structured UdsLogLine entries are smaller per-line than the
  v1.1.0 `string` and color-coded runs read better with more context.)
- `ClearOutputCommand` clears both `OutputLog` and `LogParagraph.Inlines`.

## 6. Error Handling

| Exception | Source | Caught at | UI behavior |
|---|---|---|---|
| `KeyAlgorithmNotConfiguredException` | `PlaceholderKeyAlgorithm.ComputeKey` | `SessionPanelViewModel.SecurityAccessCommand` | Log Warn via `ILogger` + 2 OutputLog lines (ex.Message + Hint); `SecurityLevel = null`; no crash |
| `InvalidOperationException` ("no IKeyDerivationAlgorithm wired") | `UdsClient.SecurityAccessAsync` 2-arg overload (throws when `_keyAlgorithm` is null because UdsClient was built with the legacy 2-arg ctor) | `SessionPanelViewModel.SecurityAccessCommand` | **Same as `KeyAlgorithmNotConfiguredException`**: OutputLog Warn + Hint + `SecurityLevel = null`. **Clarified 2026-06-25 during v1.2.0 implementation:** both exceptions mean the same root cause from the user's POV (no OEM key algorithm registered), so they get identical user-facing treatment. A generic `InvalidOperationException` from elsewhere in `UdsClient` (e.g. channel disconnected via `SendService`) still falls into the top-level `catch (Exception)` row below and logs Error. |
| `UdsNegativeResponseException(Nrc)` | `UdsClient` response parser | Each panel VM's `[RelayCommand]` catch block | OutputLog Warn (`"NRC 0xNN"`); release busy flag; no crash |
| `IsoTpTimeoutException` | `IsoTpLayer` | Top-level `catch (Exception)` per command | OutputLog Error; release busy flag; no crash |
| `TimeoutException` (P2/P2*) | `UdsTimer` | Top-level `catch (Exception)` | OutputLog Error; release busy flag; no crash |
| `OperationCanceledException` | TesterPresent loop cancellation | `Task.Run` body | OutputLog Info "TesterPresent stopped"; expected |
| `InvalidOperationException` ("channel disconnected") | `UdsClient.SendService` | Top-level `catch (Exception)` | OutputLog Error; do not mutate state |
| `JsonException` (config file) | `DidDatabase` / `RoutineDatabase` | (Startup only — v1.1.0 already handles) | n/a — UI layer never sees it |
| `IOException` (config file) | Same | Same | n/a |
| `FormatException` (invalid hex) | `DidPanelViewModel.ParseHexString` | `WriteDidCommand` catch | OutputLog Error; no crash |

**Design rule (inherited from v1.1.0 spec §6)**: no unhandled exception in
the UI thread ever crashes the app. Every `[RelayCommand]` body has both a
specific catch for known UDS exceptions (`UdsNegativeResponseException`)
and a trailing `catch (Exception ex)` that logs and resets busy flags.

## 7. Testing Strategy

Target: ≥80% line coverage for all new code. Net new tests: ~17.

### 7.1 Unit tests (`tests/PeakCan.Host.App.Tests/ViewModels/Uds/`)

`SessionPanelViewModelTests`:
- `Ctor_Defaults_CurrentSession_Default_SecurityLevel_Null_TesterPresentActive_False`
- `SetDefaultSessionCommand_Invokes_UdsClient_DiagnosticSessionControl_0x01`
- `SetExtendedSessionCommand_Invokes_UdsClient_DiagnosticSessionControl_0x02`
- `SetProgrammingSessionCommand_Invokes_UdsClient_DiagnosticSessionControl_0x03`
- `ToggleTesterPresentCommand_Flips_TesterPresentActive_And_Starts_BackgroundLoop`
- `ToggleTesterPresentCommand_When_AlreadyActive_Cancels_BackgroundLoop`
- `SecurityAccessCommand_With_Placeholder_Algorithm_Logs_HintMessage_DoesNotCrash`
- `SecurityAccessCommand_With_Fake_Algorithm_Sets_SecurityLevel_0x01`
- `SecurityAccessCommand_With_UdsNegativeResponse_Logs_Warn_And_Clears_SecurityLevel`
- `SecurityAccessCommand_With_InvalidOperationException_Logs_HintMessage_And_Clears_SecurityLevel` (renamed from the brief's `_Logs_Error_` version 2026-06-25 — InvalidOp in SecurityAccess context now matches the `KeyAlgorithmNotConfigured` behavior of Warn + Hint)
- `AttachLog_Null_DoesNotThrow`
- `SetSessionAsync_On_Exception_Logs_Error_Without_Changing_CurrentSession`

`DidPanelViewModelTests`:
- `Ctor_Populates_Dids_From_DidDatabase_All`
- `Ctor_Selects_First_Did_As_SelectedDid`
- `ReadDidCommand_Populates_SelectedDid_ReadValue_And_LastResult`
- `ReadDidCommand_With_UdsNegativeResponse_Logs_Warn_And_Clears_IsReading`
- `ReadDidCommand_Sets_IsReading_True_During_Execution`
- `WriteDidCommand_Validates_Hex_Input_And_Invokes_WriteDataByIdentifier`
- `WriteDidCommand_With_Invalid_Hex_Logs_FormatException_Without_Crash`
- `ReadDidCommand_CanExecute_False_When_No_Selection`
- `ReadDidCommand_CanExecute_False_When_IsReading_True`

`RoutinePanelViewModelTests`:
- `Ctor_Populates_Routines_From_RoutineDatabase_All`
- `Ctor_With_Empty_RoutineDatabase_Has_No_SelectedRoutine`
- `StartRoutineCommand_Updates_Status_Running_Then_Completed`
- `StopRoutineCommand_Invokes_RoutineControl_0x02`
- `QueryRoutineCommand_Invokes_RoutineControl_0x03`
- `StartRoutineCommand_With_UdsNegativeResponse_Sets_Status_Failed`
- `RoutineCommand_CanExecute_False_When_Status_Running`

`DtcPanelViewModelTests`:
- `ReadDtcsCommand_Parses_4Byte_Chunks_Into_DtcRows`
- `ReadDtcsCommand_With_Empty_Response_Clears_Dtcs`
- `ReadDtcsCommand_With_UdsNegativeResponse_Logs_Warn_And_Leaves_Dtcs_Unchanged`
- `ClearDtcsCommand_Invokes_ClearDiagnosticInformation_And_Clears_Collection`

`UdsViewModelOrchestratorTests`:
- `Ctor_Wires_All_Four_PanelVMs_To_Shared_OutputLog`
- `ClearOutputCommand_Clears_OutputLog`

### 7.2 Integration tests

`UdsPanelIntegrationTests` (App.Tests, cross panel + FakeUdsClient):
- `SessionPanel_SecurityAccess_With_FakeAlgorithm_Sets_SecurityLevel_And_Appends_Info_Log`
- `DidPanel_ReadDid_Updates_SelectedDid_ReadValue_And_Appends_Info_Log`
- `RoutinePanel_StartRoutine_Updates_Status_To_Completed_And_Appends_Info_Log`
- `DtcPanel_ReadDtcs_Populates_Dtcs_From_Fake_Response`

The existing v1.1.0 `UdsViewModelSecurityAccessTests` (5 tests) are
**deleted** in this PR and their cases migrated to
`SessionPanelViewModelTests.SecurityAccessCommand_*`. The delete is part
of the same commit because the type they test no longer exists.

### 7.3 Manual test checklist (release notes)

- [ ] Launch app with PCAN-USB FD hardware; open UDS tab.
- [ ] DIDs tab shows 5 built-in DIDs (VIN, SW/HW version, PartNumber,
      SupplierID) in DataGrid.
- [ ] Edit `%APPDATA%\PeakCan.Host\uds-dids.json`, add a custom DID,
      restart → custom DID appears.
- [ ] Select a DID in DataGrid, click Read → `LastResult` populated with
      hex string.
- [ ] Routines tab shows routines from JSON (empty list + no error if
      file missing).
- [ ] DTCs tab → Read DTCs → DataGrid populated.
- [ ] Top strip Default / Extended / Programming buttons flip
      `CurrentSession`.
- [ ] Top strip Security Access (Level 1) with no OEM algorithm
      registered → OutputLog shows Warn + Hint; no crash.
- [ ] Top strip TesterPresent checkbox toggles; log shows "started" /
      "stopped".
- [ ] Output Log: Info lines default-colored, Warn yellow (#DCDCAA),
      Error red (#F48771).
- [ ] Clear button empties OutputLog and RichTextBox.
- [ ] Disconnect hardware mid-session → UDS commands log Error, no crash.

## 8. File Inventory

### 8.1 New files

```
src/PeakCan.Host.App/ViewModels/Uds/UdsViewModel.cs              (~50 lines, orchestrator)
src/PeakCan.Host.App/ViewModels/Uds/IUdsPanel.cs                  (~10, internal interface)
src/PeakCan.Host.App/ViewModels/Uds/UdsLogLine.cs                 (~10)
src/PeakCan.Host.App/ViewModels/Uds/SessionPanelViewModel.cs      (~120)
src/PeakCan.Host.App/ViewModels/Uds/DidPanelViewModel.cs          (~110)
src/PeakCan.Host.App/ViewModels/Uds/RoutinePanelViewModel.cs      (~110)
src/PeakCan.Host.App/ViewModels/Uds/DtcPanelViewModel.cs          (~90)
src/PeakCan.Host.App/ViewModels/Uds/Rows/DidRow.cs                (~25)
src/PeakCan.Host.App/ViewModels/Uds/Rows/RoutineRow.cs            (~25)
src/PeakCan.Host.App/ViewModels/Uds/Rows/DtcRow.cs                (~20)

tests/PeakCan.Host.App.Tests/ViewModels/Uds/SessionPanelViewModelTests.cs
tests/PeakCan.Host.App.Tests/ViewModels/Uds/DidPanelViewModelTests.cs
tests/PeakCan.Host.App.Tests/ViewModels/Uds/RoutinePanelViewModelTests.cs
tests/PeakCan.Host.App.Tests/ViewModels/Uds/DtcPanelViewModelTests.cs
tests/PeakCan.Host.App.Tests/ViewModels/Uds/UdsViewModelOrchestratorTests.cs
tests/PeakCan.Host.App.Tests/ViewModels/Uds/UdsPanelIntegrationTests.cs

docs/release-notes-v1.2.0.md
```

### 8.2 Modified files

```
src/PeakCan.Host.App/Views/UdsView.xaml                          (full rewrite to spec §4.9)
src/PeakCan.Host.App/Views/UdsView.xaml.cs                       (+ log appender handler)
src/PeakCan.Host.App/Composition/AppHostBuilder.cs               (+ 4 panel VM DI registrations)
Directory.Build.props                                            (1.1.0 → 1.2.0)
README.md                                                         (+ v1.2.0 section)
```

### 8.3 Deleted files

```
src/PeakCan.Host.App/ViewModels/UdsViewModel.cs                  (old 279-line monolith)
tests/PeakCan.Host.App.Tests/ViewModels/UdsViewModelSecurityAccessTests.cs (migrated to SessionPanelViewModelTests)
```

### 8.4 Namespace-impacted files

```
src/PeakCan.Host.App/Composition/AppHostBuilder.cs               (using update)
src/PeakCan.Host.App/AppShellViewModel.cs                        (using update if it imports UdsViewModel)
tests/PeakCan.Host.App.Tests/...                                 (any using updates)
```

## 9. Open Questions

None. All design decisions resolved.

## 9.1 Discovered during v1.2.0 implementation (out of scope, deferred)

- **`DidDatabase` 2-arg ctor NRE on null `ILogger`** —
  `src/PeakCan.Host.Core/Uds/Database/DidDatabase.cs:57` calls
  `LogNoPathConfigured(logger!)` on a `ILogger<DidDatabase>?` parameter
  that the `[LoggerMessage]` source-gen does not null-check. The 1-arg
  ctor `DidDatabase(ILogger<DidDatabase>?)` is unusable in test setups
  that pass `null`. **Workaround in v1.2.0 tests:** always pass
  `NullLogger<DidDatabase>.Instance`. **Root cause fix (v1.2.x PATCH):**
  one of:
  - Guard the ctor: `_logger = logger;` then `_logger?.LogInformation(...)` (LoggerMessage source-gen can be made null-safe by changing the `[LoggerMessage]` signature to `partial void LogNoPathConfigured(LogLevel level)` and routing through `_logger?.IsEnabled(level) == true ? _logger.Log(level, ...) : NoOp`).
  - Make the ctor non-nullable: `DidDatabase(ILogger<DidDatabase> logger)` (breaks the 1-arg ctor public surface; minor).
  - **`RoutineDatabase` does NOT have this bug** (verified 2026-06-25 by Task 4 implementer): its nullable `_logger` + `logger!` at the partial-method call sites handles null logger correctly. No workaround needed for `RoutineDatabase` ctor.
  Discovered 2026-06-25 by Task 3 review; not in v1.2.0 ship.

## 10. References

- [v1.1.0 design spec](file:///D:/claude_proj2/peakcan-host/docs/superpowers/specs/2026-06-25-v1-1-0-uds-ui-and-key-provider-design.md) — predecessor that shipped the JSON DBs and the SecurityAccess KeyProvider; v1.2.0 completes D1/D2/D3 carved out in §9.
- [v1.1.0 release notes](file:///D:/claude_proj2/peakcan-host/docs/release-notes-v1.1.0.md) — Known Limitations section lists the 3 v1.2 items verbatim.
- [UDS diagnostic stack design](file:///D:/claude_proj2/peakcan-host/docs/superpowers/specs/2026-06-22-uds-diagnostic-stack-design.md) — Core/Infrastructure base.
- [v1.1.0 SHIPPED memory](file:///C:/Users/13777/.claude/projects/D--claude-proj2/memory/peakcan-host-v1-1-0-shipped.md) — confirms v1.2 backlog shape.
- [ISO 14229-1:2020](https://www.iso.org/standard/72442.html) — UDS application layer.
- [ISO 15765-2:2024](https://www.iso.org/standard/83899.html) — ISO-TP transport layer.