# PeakCan Host — v1.1.0 UDS UI + SecurityAccess KeyProvider Design

> **Baseline**: `10eb498` (latest on `fix/uds-8-critical`, post-v0.10.1 + post-UDS feature merge)
> **Target**: v1.1.0
> **Status**: Draft for review

## 1. Overview

v0.10.1 + the merged-but-untagged UDS diagnostic stack (`feat: add UDS diagnostic stack (ISO 14229 + ISO 15765-2)`) shipped the Core/Infrastructure side of UDS — ISO-TP transport, session/timer management, all mandatory services (DiagnosticSessionControl, ECUReset, ReadDataByIdentifier, WriteDataByIdentifier, SecurityAccess, TesterPresent, RoutineControl, ReadDTCInformation, ClearDiagnosticInformation, RequestDownload/TransferData/RequestTransferExit), and a functional App-layer UI: `UdsViewModel` with 8 RelayCommands, `UdsView.xaml` (TabControl with DIDs / Routines / DTCs + Log Panel), `AppShellViewModel.ShowUdsCommand`, and `AppHostBuilder` registrations for `IsoTpLayer` / `UdsTimer` / `UdsClient` / `UdsViewModel`.

Two gaps remain that block v1.1.0 ship:

1. `UdsViewModel.SecurityAccessAsync` (`src/PeakCan.Host.App/ViewModels/UdsViewModel.cs:132`) still throws `NotImplementedException` because no configurable SecurityAccess key derivation algorithm exists. OEMs need a DI seam to plug in their seed→key computation without recompiling the host.
2. DID and Routine definitions are hard-coded in the existing `UdsViewModel` (free-text DID / Routine ID input boxes in `UdsView.xaml`). Users need JSON-loadable databases (`%APPDATA%\PeakCan.Host\uds-dids.json`, `uds-routines.json`) so they can extend the DID/Routine lists without code changes.

This spec closes those two gaps:

1. **`IKeyDerivationAlgorithm` abstraction** — replaces the `NotImplementedException` in `UdsViewModel.SecurityAccessAsync` (`src/PeakCan.Host.App/ViewModels/UdsViewModel.cs:132`) with a DI-injected OEM-pluggable algorithm. A `PlaceholderKeyAlgorithm` ships by default; OEM-specific implementations are wired by the OEM at deploy time.
2. **UDS UI Phase E** — full `UdsView.xaml` plus four panel ViewModels (Session, DID, Routine, DTC), backed by JSON-loadable DID/Routine databases.

Both ship together as **v1.1.0**.

## 2. Goals

### In Scope

- **G1**: Define `IKeyDerivationAlgorithm` in `PeakCan.Host.Core.Uds` with `byte[] ComputeKey(byte[] seed, byte securityLevel)`.
- **G2**: Ship `PlaceholderKeyAlgorithm` (default) that throws `KeyAlgorithmNotConfiguredException(level)` with an actionable message.
- **G3**: `UdsClient` gains a new constructor parameter `IKeyDerivationAlgorithm` and a new public method `SecurityAccessAsync(byte requestLevel, byte[]? seedOverride = null)` that uses the injected algorithm (falling back to the explicit seed if `seedOverride` is non-null — kept for tests and deterministic ECU simulators).
- **G4**: `UdsViewModel.SecurityAccessAsync` removes the `NotImplementedException`, calls the new `UdsClient` overload, and surfaces `KeyAlgorithmNotConfiguredException` cleanly in the Output Log (no crash, no red dialog).
- **G5**: Define `DidDefinition` + `DidDatabase` and `RoutineDefinition` + `RoutineDatabase` in `PeakCan.Host.Core.Uds.Database`. Each loads from `%APPDATA%\PeakCan.Host\uds-dids.json` (or `uds-routines.json`), merging with built-in defaults.
- **G6**: Built-in DID defaults: VIN (0xF190), ECU SW version (0xF184), ECU HW version (0xF191), Part Number (0xF187), Supplier ID (0xF18A) — all 17 bytes, read-only.
- **G7**: Refactor `UdsViewModel` into an orchestrator holding four panel ViewModels: `SessionPanelViewModel`, `DidPanelViewModel`, `RoutinePanelViewModel`, `DtcPanelViewModel`.
- **G8**: New `UdsView.xaml` with three tabs (DIDs / Routines / DTCs) plus a SessionPanel header and an Output Log footer.
- **G9**: AppShell adds a "UDS" tab; DI registration wires all new services in `AppHostBuilder`.
- **G10**: Unit-test coverage ≥80% for all new code; integration tests for `UdsClient` SecurityAccess + `UdsViewModel` panel orchestration; manual test checklist against real ECU or UDS simulator.

### Non-Goals (YAGNI)

- **N1**: NOT implementing any OEM-specific key derivation algorithm. Placeholder + DI seam is the entire deliverable.
- **N2**: NOT adding new UDS services beyond what `feat: add UDS diagnostic stack (ISO 14229 + ISO 15765-2)` already provided.
- **N3**: NOT introducing a third-party JSON library; use `System.Text.Json` (already in .NET 10 BCL).
- **N4**: NOT introducing MVVM frameworks beyond what the project already uses (`CommunityToolkit.Mvvm` `RelayCommand` / `ObservableObject`).
- **N5**: NOT changing the DBC parser scope.
- **N6**: NOT modifying the existing `feat: add JavaScript scripting engine` (`e5de8e2`) — `can.*` API does not need to expose UDS yet (future `v1.2` backlog).
- **N7**: NOT adding Linux/SocketCAN or J1939/CANopen (`v2.0` backlog).
- **N8**: NOT changing the persisted format of any existing user data (e.g. DBC files, ASC recordings).

### v1.1.0 Ship Scope (v1.2 Backlog Items)

The merged-but-untagged UDS work already shipped `UdsViewModel` (monolithic, 8 RelayCommands), `UdsView.xaml` (TabControl with free-text DID / Routine ID inputs + DTC DataGrid + Log Panel), `AppShellViewModel.ShowUdsCommand`, and DI registrations for `IsoTpLayer` / `UdsTimer` / `UdsClient` / `UdsViewModel`. The following spec items are therefore **deferred to v1.2** and NOT part of v1.1.0 ship:

- **D1**: §4.5 (`UdsViewModel` refactor to orchestrator holding 4 panel VMs). The existing monolithic `UdsViewModel` is functional — refactoring risks regression in the working `ReadDid` / `WriteDid` / `ReadDTC` commands.
- **D2**: §4.8 (Session / DID / Routine / DTC panel ViewModels + Row types). Same reason.
- **D3**: §4.9 (`UdsView.xaml` rewrite to use DataGrid tree views with `DidDatabase` + `RoutineDatabase` as ItemsSource). The existing free-text input boxes remain. Once `DidDatabase` / `RoutineDatabase` are in Core (§4.6 / §4.7), wiring them into the XAML is a v1.2 task.
- **D4**: §6 error-handling table entries that reference panel VMs (`InvalidOperationException` from disconnected channel). Existing `UdsViewModel` already has `try/catch (Exception)` in every command.

The v1.1.0 ship scope is therefore:
- §4.1 / §4.2 / §4.3 — `IKeyDerivationAlgorithm` + `PlaceholderKeyAlgorithm` + `KeyAlgorithmNotConfiguredException` (Core).
- §4.4 — `UdsClient` ctor + new `SecurityAccessAsync(byte, CancellationToken)` overload (Core).
- §4.6 / §4.7 — `DidDefinition` + `DidDatabase` (with 5 built-in defaults + JSON load) and `RoutineDefinition` + `RoutineDatabase` (Core).
- §4.10 (subset) — `AppHostBuilder` DI registration of `IKeyDerivationAlgorithm`, `DidDatabase`, `RoutineDatabase`, and the `UdsClient` factory using the 3-arg ctor (App).
- `UdsViewModel.SecurityAccessAsync` modification: remove the `NotImplementedException` (line 132) and call the new `UdsClient.SecurityAccessAsync(0x01)` overload. Existing `try/catch` shape is preserved.

## 3. Architecture

### 3.1 Layer impact

```
   PeakCan.Host.App            (WPF, MVVM, BackgroundService, DI composition)
            │  +UdsView.xaml, +4 panel VMs, +UdsViewModel orchestrator refactor
            │  +IKeyDerivationAlgorithm DI registration (Placeholder default)
            ▼  uses
   PeakCan.Host.Infrastructure  (PEAK SDK adapter, ChannelRouter, BusStatistics) — unchanged
            ▼  uses
   PeakCan.Host.Core           (CanFrame, DBC parser, SignalDecoder, Result)
            │  +IKeyDerivationAlgorithm, +PlaceholderKeyAlgorithm, +DidDefinition/RoutineDefinition/Databases
            │  +UdsClient ctor injection of IKeyDerivationAlgorithm
            │  +UdsClient.SecurityAccessAsync(byte, byte[]?) overload
```

No new layer. NetArchTest rule 2 (Core must not depend on `Peak.Can.Basic`) is preserved — `IKeyDerivationAlgorithm` has zero hardware dependency.

### 3.2 Component diagram

```
┌─────────────────────────────────────────────────────────────────┐
│ PeakCan.Host.App / Views / UdsView.xaml                         │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │ SessionPanel                                              │  │
│  │  [Default] [Extended] [Programming] [TesterPresent] [Sec] │  │
│  │  Current: <Session>  SecurityLevel: <level>              │  │
│  ├───────────────────────────────────────────────────────────┤  │
│  │ ┌─────────────┬───────────────┬───────────────────────┐   │  │
│  │ │ DIDs         │ Routines       │ DTCs                 │   │  │
│  │ │ TreeView     │ DataGrid       │ DataGrid             │   │  │
│  │ │ (DidDatabase │ (Routine-      │ (DtcPanelVM)         │   │  │
│  │ │  driven)     │  Database)     │                      │   │  │
│  │ ├─────────────┴───────────────┴───────────────────────┤   │  │
│  │ │ Output Log (RichTextBox, auto-scroll, clear button)  │   │  │
│  │ └──────────────────────────────────────────────────────┘   │  │
│  └───────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
                                │
                                ▼  DataContext
┌─────────────────────────────────────────────────────────────────┐
│ UdsViewModel (orchestrator)                                     │
│  ├── SessionPanelViewModel   (current session, TesterPresent)   │
│  ├── DidPanelViewModel       (read/write DID list)              │
│  ├── RoutinePanelViewModel   (start/stop/query routines)        │
│  ├── DtcPanelViewModel       (read/clear DTCs)                  │
│  └── OutputLog               (shared ObservableCollection)      │
└─────────────────────────────────────────────────────────────────┘
                                │  via DI
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│ PeakCan.Host.App / Services / Uds                               │
│  └── (existing UdsClient wired via DI)                          │
└─────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│ PeakCan.Host.Core / Uds                                         │
│  ├── UdsClient              (NEW ctor: +IKeyDerivationAlgorithm) │
│  │                                                              │
│  ├── IKeyDerivationAlgorithm interface                          │
│  │   └── PlaceholderKeyAlgorithm (default DI registration)      │
│  │                                                              │
│  ├── IsoTp / Services / ...  (unchanged)                        │
│  │                                                              │
│  └── Database/                                                  │
│      ├── DidDefinition       record                             │
│      ├── DidDatabase         service (JSON + built-in defaults)  │
│      ├── RoutineDefinition   record                             │
│      └── RoutineDatabase     service (JSON + built-in defaults)  │
└─────────────────────────────────────────────────────────────────┘
```

## 4. Components

### 4.1 `IKeyDerivationAlgorithm` (NEW, Core)

**Location**: `src/PeakCan.Host.Core/Uds/IKeyDerivationAlgorithm.cs`

```csharp
namespace PeakCan.Host.Core.Uds;

/// <summary>
/// OEM-specific key derivation algorithm for UDS SecurityAccess (0x27).
/// Implementations are typically OEM-confidential and may call into
/// native libraries, network services, or hardware security modules.
/// </summary>
public interface IKeyDerivationAlgorithm
{
    /// <summary>
    /// Computes the response key for the given seed and security level.
    /// </summary>
    /// <param name="seed">Bytes returned by SecurityAccess requestSeed
    ///   (the seed argument to SecurityAccessAsync(byte)).</param>
    /// <param name="securityLevel">Sub-function byte (0x01, 0x03, ...).</param>
    /// <returns>Computed key bytes. Length is OEM-specific.</returns>
    /// <exception cref="ArgumentNullException">seed is null.</exception>
    /// <exception cref="KeyAlgorithmNotConfiguredException">
    ///   Thrown by placeholder implementations when no OEM algorithm
    ///   is registered. OEM implementations should throw other exceptions
    ///   (e.g. <see cref="InvalidOperationException"/>) on algorithm failure.
    /// </exception>
    byte[] ComputeKey(byte[] seed, byte securityLevel);
}
```

### 4.2 `PlaceholderKeyAlgorithm` (NEW, Core)

**Location**: `src/PeakCan.Host.Core/Uds/PlaceholderKeyAlgorithm.cs`

```csharp
namespace PeakCan.Host.Core.Uds;

/// <summary>
/// Default <see cref="IKeyDerivationAlgorithm"/> implementation. Throws
/// <see cref="KeyAlgorithmNotConfiguredException"/> until an OEM-specific
/// implementation is registered in DI. Ship this so the build, tests, and
/// startup are all green without an OEM-supplied algorithm.
/// </summary>
public sealed class PlaceholderKeyAlgorithm : IKeyDerivationAlgorithm
{
    public byte[] ComputeKey(byte[] seed, byte securityLevel)
    {
        ArgumentNullException.ThrowIfNull(seed);
        throw new KeyAlgorithmNotConfiguredException(securityLevel);
    }
}
```

### 4.3 `KeyAlgorithmNotConfiguredException` (NEW, Core)

**Location**: `src/PeakCan.Host.Core/Uds/KeyAlgorithmNotConfiguredException.cs`

```csharp
namespace PeakCan.Host.Core.Uds;

/// <summary>
/// Thrown by <see cref="IKeyDerivationAlgorithm"/> implementations that
/// have not been configured with OEM-specific parameters. Distinct from
/// generic <see cref="InvalidOperationException"/> so the UI layer can
/// surface a targeted configuration hint instead of a generic error.
/// </summary>
public sealed class KeyAlgorithmNotConfiguredException : Exception
{
    public byte SecurityLevel { get; }

    public KeyAlgorithmNotConfiguredException(byte securityLevel)
        : base($"UDS SecurityAccess key algorithm for level 0x{securityLevel:X2} " +
               "is not configured. Register an IKeyDerivationAlgorithm implementation " +
               "in DI before calling SecurityAccessAsync.")
        => SecurityLevel = securityLevel;
}
```

### 4.4 `UdsClient` changes (MODIFY, Core)

**Location**: `src/PeakCan.Host.Core/Uds/UdsClient.cs`

The existing `SecurityAccessAsync(byte level, byte[]? key = null, CancellationToken ct = default)` is a single virtual method that already handles both legs of the SecurityAccess handshake: when `key is null` it does RequestSeed and returns the seed bytes; when `key is not null` it does SendKey and returns the success response. We do NOT split this into private `RequestSeedAsync` / `SendKeyAsync` helpers — that would be a refactor outside the scope of v1.1.0 (non-goal N4-ish).

- Add `IKeyDerivationAlgorithm? _keyAlgorithm` field (nullable; the existing parameterless-of-key-algo ctor leaves it null so existing tests keep working).
- Add new constructor:
  ```csharp
  public UdsClient(IsoTpLayer isoTp, IKeyDerivationAlgorithm keyAlgorithm, UdsTimer? timer = null)
  {
      ArgumentNullException.ThrowIfNull(isoTp);
      ArgumentNullException.ThrowIfNull(keyAlgorithm);

      _isoTp = isoTp;
      _keyAlgorithm = keyAlgorithm;
      _timer = timer ?? new UdsTimer();
      Security = new UdsSecurity();
      _isoTp.MessageReceived += OnMessageReceived;
  }
  ```
- The existing `UdsClient(IsoTpLayer isoTp, UdsTimer? timer = null)` ctor remains untouched and sets `_keyAlgorithm = null`. Existing tests using it (e.g. `FakeCanChannel`-based integration tests) continue to compile.
- Add a new overload that uses the injected algorithm:
  ```csharp
  public virtual async Task<byte[]> SecurityAccessAsync(byte requestLevel, CancellationToken ct = default)
  {
      if (_keyAlgorithm is null)
          throw new InvalidOperationException(
              "UdsClient was constructed without an IKeyDerivationAlgorithm. " +
              "Use the (IsoTpLayer, IKeyDerivationAlgorithm, UdsTimer?) constructor " +
              "or call SecurityAccessAsync(byte level, byte[] key, CancellationToken) directly.");

      // RequestSeed leg: existing method with key=null returns seed bytes.
      byte[] seed = await SecurityAccessAsync(requestLevel, key: null, ct).ConfigureAwait(false);

      // SECURITY: never log seed bytes — see commit a9fe443 (C-2 fix).
      byte[] key = _keyAlgorithm.ComputeKey(seed, requestLevel);

      // SendKey leg: existing method with key=non-null returns success response.
      return await SecurityAccessAsync(requestLevel, key, ct).ConfigureAwait(false);
  }
  ```
- The existing `SecurityAccessAsync(byte level, byte[]? key, CancellationToken ct)` is the path `UdsViewModel` already calls with `level: 0x01, key: null` (RequestSeed). After this task lands, `UdsViewModel` switches to the new overload `SecurityAccessAsync(0x01)`.

**Refactor strategy**: pure additive — new ctor + new overload; existing ctor and existing 3-arg `SecurityAccessAsync` are unchanged. No test that uses the old ctor + old method breaks.

### 4.5 `UdsViewModel` changes (MODIFY, App)

**Location**: `src/PeakCan.Host.App/ViewModels/UdsViewModel.cs`

- Delete the `NotImplementedException` throw at line 132.
- Call `_udsClient.SecurityAccessAsync(0x01)`.
- Add a `catch (KeyAlgorithmNotConfiguredException ex)` branch that logs the exception's message (already user-friendly) and additionally emits a `Log("→ Register your IKeyDerivationAlgorithm implementation in AppHostBuilder.cs.")` hint.
- Keep the existing `catch (UdsNegativeResponseException)` and `catch (Exception)` branches.
- Existing `try/catch` shape is preserved; only the body changes.

### 4.6 `DidDefinition` + `DidDatabase` (NEW, Core)

**Location**: `src/PeakCan.Host.Core/Uds/Database/`

```csharp
public sealed record DidDefinition(
    ushort Id,
    string Name,
    string Description,
    int LengthBytes,
    bool Writable);

public interface IDidDatabase
{
    IReadOnlyList<DidDefinition> All { get; }
    DidDefinition? Find(ushort id);
}

/// <summary>
/// Resolves the default user-JSON path: <c>%APPDATA%\PeakCan.Host\uds-dids.json</c>.
/// Equivalent to <c>Path.Combine(Environment.GetFolderPath(SpecialFolder.LocalApplicationData), "PeakCan.Host", "uds-dids.json")</c>.
/// </summary>
public static class DidDatabaseDefaults
{
    public static string DefaultJsonPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PeakCan.Host", "uds-dids.json");
}

public sealed class DidDatabase : IDidDatabase
{
    private readonly ILogger<DidDatabase>? _logger; // Serilog ILogger; null in unit tests.

    public IReadOnlyList<DidDefinition> All { get; }
    public DidDefinition? Find(ushort id)
        => All.FirstOrDefault(d => d.Id == id);

    public DidDatabase(ILogger<DidDatabase>? logger = null)
        : this(DidDatabaseDefaults.DefaultJsonPath, logger) { }

    public DidDatabase(string? userJsonPath, ILogger<DidDatabase>? logger = null)
    {
        _logger = logger;
        var builtIn = BuiltInDefaults();
        var user = LoadUserFile(userJsonPath);
        All = Merge(builtIn, user).ToList();
    }

    private static IEnumerable<DidDefinition> BuiltInDefaults() { ... 5 DIDs ... }
    private IEnumerable<DidDefinition>? LoadUserFile(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            _logger?.LogInformation("No DID user JSON at {Path}; using built-in defaults.", path ?? "(null)");
            return null;
        }
        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<DidFileDto>(json);
            return dto?.Dids ?? Enumerable.Empty<DidDefinition>();
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Malformed DID JSON at {Path}; using built-in defaults.", path);
            return null;
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "IO error reading DID JSON at {Path}; using built-in defaults.", path);
            return null;
        }
    }
    private static IEnumerable<DidDefinition> Merge(
        IEnumerable<DidDefinition> builtIn,
        IEnumerable<DidDefinition>? user) { ... }

    private sealed class DidFileDto
    {
        [JsonPropertyName("dids")]
        public List<DidDefinition> Dids { get; set; } = new();
    }
}
```

JSON shape (`%APPDATA%\PeakCan.Host\uds-dids.json`):
```json
{
  "dids": [
    { "id": "0xF190", "name": "VIN",            "description": "Vehicle Identification Number", "lengthBytes": 17, "writable": false },
    { "id": "0xF187", "name": "PartNumber",     "description": "ECU Part Number",               "lengthBytes": 10, "writable": false },
    { "id": "0xF18A", "name": "SupplierID",     "description": "ECU Supplier ID",               "lengthBytes":  4, "writable": false },
    { "id": "0xF191", "name": "HardwareVersion","description": "ECU Hardware Version",          "lengthBytes":  3, "writable": false },
    { "id": "0xF184", "name": "SoftwareVersion","description": "ECU Software Version",          "lengthBytes":  9, "writable": false }
  ]
}
```

User-JSON behavior:
- File missing → log `Information`, return null, fall back to built-in only.
- File present but malformed JSON → log `Warning`, return null, fall back to built-in only. **Decision: do not throw** — UI must remain usable even with a broken config file.
- User IDs override built-in defaults with the same `id`. Other IDs are appended.

### 4.7 `RoutineDefinition` + `RoutineDatabase` (NEW, Core)

Same shape as 4.6 (with the same `ILogger<RoutineDatabase>? logger` parameter, default path `%APPDATA%\PeakCan.Host\uds-routines.json`, and graceful fallback on missing/malformed JSON), but the record is:

```csharp
public sealed record RoutineDefinition(
    ushort Id,
    string Name,
    string Description,
    bool Startable,
    bool Stoppable);

public static class RoutineDatabaseDefaults
{
    public static string DefaultJsonPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PeakCan.Host", "uds-routines.json");
}

public sealed class RoutineDatabase
{
    // Same constructor shape as DidDatabase:
    //   RoutineDatabase(ILogger<RoutineDatabase>? logger = null)
    //   RoutineDatabase(string? userJsonPath, ILogger<RoutineDatabase>? logger = null)
    // Same load/malformed/IO error handling.
    // No built-in defaults (routines are 100% OEM-defined).
    public IReadOnlyList<RoutineDefinition> All { get; }
    public RoutineDefinition? Find(ushort id) => All.FirstOrDefault(r => r.Id == id);
}
```

JSON shape (`%APPDATA%\PeakCan.Host\uds-routines.json`):
```json
{
  "routines": [
    { "id": "0xFF00", "name": "EraseMemory",  "description": "Erase ECU flash memory",         "startable": true, "stoppable": true  },
    { "id": "0xFF01", "name": "CheckIntegrity","description": "Run flash integrity check",     "startable": true, "stoppable": false },
    { "id": "0x0202", "name": "DtcClearCheck", "description": "Check DTC clear prerequisites",  "startable": true, "stoppable": false }
  ]
}
```

No built-in defaults — routines are 100% OEM-defined. Empty list when no user file.

### 4.8 Panel ViewModels (NEW, App)

**Location**: `src/PeakCan.Host.App/ViewModels/Uds/`

| ViewModel | Properties | Commands |
|---|---|---|
| `SessionPanelViewModel` | `CurrentSession` (string), `SecurityLevel` (byte?), `TesterPresentActive` (bool) | `SetDefaultSessionCommand`, `SetExtendedSessionCommand`, `SetProgrammingSessionCommand`, `ToggleTesterPresentCommand` |
| `DidPanelViewModel` | `Dids` (ObservableCollection<DidRow>), `SelectedDid`, `ReadResult`, `WriteValue` (hex string) | `ReadDidCommand`, `WriteDidCommand` |
| `RoutinePanelViewModel` | `Routines` (ObservableCollection<RoutineRow>), `SelectedRoutine`, `QueryResult` | `StartRoutineCommand`, `StopRoutineCommand`, `QueryRoutineCommand` |
| `DtcPanelViewModel` | `Dtcs` (ObservableCollection<DtcRow>), `SelectedDtc` | `ReadDtcsCommand`, `ClearDtcsCommand` |

**Row types** are small records/classes carrying the live values for the UI:
- `DidRow(ushort Id, string Name, int LengthBytes, bool Writable, string? ReadValue, bool IsReading, bool IsWriting)`
- `RoutineRow(ushort Id, string Name, string Status, string? LastResult)`
- `DtcRow(uint Code, byte Status, string? Description)`

All commands are `[RelayCommand]` (CommunityToolkit.Mvvm) with `CanExecute` driven by `UdsClient.IsSessionActive` + busy flags.

All panel VMs receive `UdsClient` via constructor injection. They write log lines to a shared `ObservableCollection<UdsLogLine>` exposed by `UdsViewModel`.

### 4.9 `UdsView.xaml` (NEW, App)

**Location**: `src/PeakCan.Host.App/Views/UdsView.xaml` + `.xaml.cs`

Layout (no MaterialDesignThemes, no third-party UI deps):

```xml
<UserControl ...>
  <DockPanel>
    <!-- Top: SessionPanel -->
    <Border DockPanel.Dock="Top" Padding="8" Background="{StaticResource PanelBrush}">
      <StackPanel Orientation="Horizontal">
        <Button Content="Default session"      Command="{Binding Session.SetDefaultSessionCommand}"/>
        <Button Content="Extended session"     Command="{Binding Session.SetExtendedSessionCommand}"/>
        <Button Content="Programming session"  Command="{Binding Session.SetProgrammingSessionCommand}"/>
        <Separator/>
        <CheckBox Content="TesterPresent"      IsChecked="{Binding Session.TesterPresentActive, Mode=OneWay}"
                  Command="{Binding Session.ToggleTesterPresentCommand}"/>
        <Separator/>
        <Button Content="SecurityAccess (Level 1)"
                Command="{Binding Session.SecurityAccessCommand}"/>
        <TextBlock Text="{Binding Session.CurrentSession, StringFormat='Session: {0}'}"
                   Margin="12,0,0,0" VerticalAlignment="Center"/>
        <TextBlock Text="{Binding Session.SecurityLevel, StringFormat='Level: 0x{0:X2}'}"
                   Margin="12,0,0,0" VerticalAlignment="Center"/>
      </StackPanel>
    </Border>

    <!-- Bottom: Output Log -->
    <Border DockPanel.Dock="Bottom" Height="160" Padding="8" Background="{StaticResource OutputBrush}">
      <DockPanel>
        <Button DockPanel.Dock="Top" Content="Clear" HorizontalAlignment="Right"
                Command="{Binding ClearOutputCommand}" Margin="0,0,0,4"/>
        <RichTextBox IsReadOnly="True" x:Name="LogBox" VerticalScrollBarVisibility="Auto">
          <FlowDocument PageWidth="2000">
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
              <DataGridTextColumn Header="ID"     Binding="{Binding IdDisplay}"/>
              <DataGridTextColumn Header="Name"   Binding="{Binding Name}"/>
              <DataGridTextColumn Header="Length" Binding="{Binding LengthBytes}"/>
              <DataGridTextColumn Header="R/W"    Binding="{Binding WritableDisplay}"/>
              <DataGridTextColumn Header="Value"  Binding="{Binding ReadValue}"/>
            </DataGrid.Columns>
          </DataGrid>
          <StackPanel Grid.Column="1" Margin="12">
            <TextBlock Text="Selected DID details" FontWeight="Bold"/>
            <TextBlock Text="{Binding Did.SelectedDid.Description}" TextWrapping="Wrap"/>
            <TextBlock Text="Write value (hex)" Margin="0,8,0,0"/>
            <TextBox  Text="{Binding Did.WriteValue, UpdateSourceTrigger=PropertyChanged}"/>
            <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
              <Button Content="Read"  Command="{Binding Did.ReadDidCommand}"   Margin="0,0,8,0"/>
              <Button Content="Write" Command="{Binding Did.WriteDidCommand}"/>
            </StackPanel>
            <TextBlock Text="{Binding Did.LastResult}" Margin="0,8,0,0" FontFamily="Consolas"/>
          </StackPanel>
        </Grid>
      </TabItem>
      <TabItem Header="Routines">
        <DataGrid ItemsSource="{Binding Routine.Routines}" SelectedItem="{Binding Routine.SelectedRoutine}"
                  AutoGenerateColumns="False" IsReadOnly="True">
          <DataGrid.Columns>
            <DataGridTextColumn Header="ID"      Binding="{Binding IdDisplay}"/>
            <DataGridTextColumn Header="Name"    Binding="{Binding Name}"/>
            <DataGridTextColumn Header="Status"  Binding="{Binding Status}"/>
            <DataGridTextColumn Header="Result"  Binding="{Binding LastResult}"/>
          </DataGrid.Columns>
        </DataGrid>
        <StackPanel Orientation="Horizontal" Margin="8">
          <Button Content="Start" Command="{Binding Routine.StartRoutineCommand}"/>
          <Button Content="Stop"  Command="{Binding Routine.StopRoutineCommand}"  Margin="8,0,0,0"/>
          <Button Content="Query" Command="{Binding Routine.QueryRoutineCommand}" Margin="8,0,0,0"/>
        </StackPanel>
      </TabItem>
      <TabItem Header="DTCs">
        <DockPanel>
          <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="8">
            <Button Content="Read DTCs" Command="{Binding Dtc.ReadDtcsCommand}"/>
            <Button Content="Clear DTCs" Command="{Binding Dtc.ClearDtcsCommand}" Margin="8,0,0,0"/>
          </StackPanel>
          <DataGrid ItemsSource="{Binding Dtc.Dtcs}"
                    AutoGenerateColumns="False" IsReadOnly="True">
            <DataGrid.Columns>
              <DataGridTextColumn Header="Code"        Binding="{Binding CodeDisplay}"/>
              <DataGridTextColumn Header="Status"      Binding="{Binding StatusDisplay}"/>
              <DataGridTextColumn Header="Description" Binding="{Binding Description}"/>
            </DataGrid.Columns>
          </DataGrid>
        </DockPanel>
      </TabItem>
    </TabControl>
  </DockPanel>
</UserControl>
```

The orchestrator `UdsViewModel` exposes `Session`, `Did`, `Routine`, `Dtc` sub-VMs as properties; XAML uses `{Binding Session.CurrentSession}` etc. Log appending is done in code-behind by listening to `OutputLog.CollectionChanged` and inserting a `Run` into the `LogParagraph`.

### 4.10 `AppShell` wiring (MODIFY, App)

**Location**: `src/PeakCan.Host.App/AppShell.xaml` + `AppShellViewModel.cs`

- Add `UdsView` as a new `TabItem` between **Trace** and **Script** (visual order: Probe → Trace → **UDS** → Script → DBC → Signal → Chart).
- `AppShellViewModel` gains a `UdsViewModel` property (existing pattern: TraceViewModel, DbcViewModel, SignalViewModel, etc.).
- `AppHostBuilder.cs` registers:
  - `IKeyDerivationAlgorithm` → `PlaceholderKeyAlgorithm` (singleton).
  - `DidDatabase` and `RoutineDatabase` as singletons.
  - `UdsViewModel` + 4 panel VMs as singletons.
  - `UdsClient` is already registered (verify in `AppHostBuilder.cs`); update its ctor to use the new `IKeyDerivationAlgorithm` constructor.

## 5. Data Flow

### 5.1 SecurityAccess flow (KeyProvider path)

```
User clicks "SecurityAccess (Level 1)" on UDS tab
    ↓
SessionPanelViewModel.SecurityAccessCommand
    ↓
UdsClient.SecurityAccessAsync(0x01)
    ↓ (seedOverride == null)
UdsClient.RequestSeedAsync(0x01)
    ↓ (ISO-TP if multi-frame)
SendService.SendAsync + ICanChannel.OnFrame
    ↓
ECU returns seed bytes
    ↓ (never logged — see commit a9fe443)
_keyAlgorithm.ComputeKey(seed, 0x01)
    ↓
[OEM-supplied algorithm: e.g. HMAC-SHA256(seed, secret)]
    ↓
key bytes
    ↓
UdsClient.SendKeyAsync(0x01, key)
    ↓
ECU authenticates; returns positive response
    ↓
UdsClient raises SessionSecurityLevelChanged(0x01) event
    ↓
UdsViewModel / SessionPanelViewModel updates SecurityLevel property
```

### 5.2 DID Read flow

```
User selects DID row + clicks Read
    ↓
DidPanelViewModel.ReadDidCommand.CanExecute → Did.IsReading=true → button disabled
    ↓
UdsClient.ReadDataByIdentifierAsync(didId)
    ↓
ISO-TP if multi-frame
    ↓
ECU returns data
    ↓
DidPanelViewModel updates SelectedDid.ReadValue (e.g. "17 42 31 4D ...")
    ↓
Did.IsReading=false
```

### 5.3 DTC Read flow

```
User clicks "Read DTCs"
    ↓
DtcPanelViewModel.ReadDtcsCommand
    ↓
UdsClient.ReadDtcInformationAsync(0x02)   // reportByStatusMask
    ↓
ECU returns DTC list
    ↓
DtcPanelViewModel.Dtcs cleared + re-populated
```

### 5.4 Output Log appending

```
Any panel VM calls UdsViewModel.Log("...")
    ↓
UdsViewModel.OutputLog.Add(new UdsLogLine(timestamp, level, message))
    ↓
UdsView.xaml.cs handles CollectionChanged → appends <Run> to LogParagraph
    ↓
RichTextBox auto-scrolls to end
```

## 6. Error Handling

| Exception | Source | UI behavior |
|---|---|---|
| `KeyAlgorithmNotConfiguredException` | `PlaceholderKeyAlgorithm.ComputeKey` | Log exception's `Message` + hint "→ Register your IKeyDerivationAlgorithm implementation in AppHostBuilder.cs." No crash. |
| `UdsNegativeResponseException(Nrc)` | `UdsClient` (response parser) | Log "ECU rejected: NRC 0x{NRC:X2} ({NRC name})". No crash. |
| `IsoTpTimeoutException` | `IsoTpLayer` (N_Bs / N_Cr) | Log "ISO-TP timeout waiting for {CF/FC}". No crash. |
| `TimeoutException` (P2/P2*) | `UdsClient.UdsTimer` | Log "ECU did not respond within {P2}ms". No crash. |
| `OperationCanceledException` | TesterPresent cancellation on disconnect | Log "TesterPresent stopped (disconnected)". No crash. |
| `InvalidOperationException` ("channel disconnected") | `UdsClient.SendService` | Log "UDS requires an open CAN channel". Disable all UDS commands. |
| `JsonException` (config file) | `DidDatabase`/`RoutineDatabase` JSON parse | Log Warning at startup. Use built-in defaults only. UI still works. |
| `IOException` (config file) | `DidDatabase`/`RoutineDatabase` file read | Log Warning. Use built-in defaults only. UI still works. |

**Design rule**: no unhandled exception in the UI thread ever crashes the app. All panel commands wrap their body in `try/catch (Exception)` and route to `UdsViewModel.Log(...)`.

## 7. Testing Strategy

### 7.1 Unit tests (`tests/PeakCan.Host.Core.Tests/Uds/` and `tests/PeakCan.Host.App.Tests/ViewModels/Uds/`)

Target: ≥80% line coverage for all new code. The project already enforces 80% as a default floor; this spec matches that.

- `PlaceholderKeyAlgorithmTests`:
  - `ComputeKey_With_AnySeed_Throws_KeyAlgorithmNotConfiguredException`
  - `ComputeKey_With_NullSeed_Throws_ArgumentNullException`
  - Exception's `SecurityLevel` matches the input
- `DidDatabaseTests`:
  - `Constructor_NoUserFile_UsesBuiltInDefaults` (5 entries)
  - `Constructor_WithUserFile_OverridesBuiltInForMatchingId`
  - `Constructor_WithUserFile_AppendsNonOverlappingEntries`
  - `Constructor_WithMalformedJson_LogsWarningAndFallsBackToBuiltIn`
  - `Constructor_WithMissingFile_LogsInformationAndFallsBackToBuiltIn`
  - `Find_ExistingId_ReturnsDefinition`
  - `Find_MissingId_ReturnsNull`
- `RoutineDatabaseTests`:
  - `Constructor_NoUserFile_ReturnsEmptyList`
  - `Constructor_WithUserFile_PopulatesList`
  - `Constructor_WithMalformedJson_LogsWarningAndReturnsEmpty`
- `UdsClientSecurityAccessTests`:
  - `SecurityAccessAsync_With_PlaceholderAlgorithm_Throws_KeyAlgorithmNotConfiguredException`
  - `SecurityAccessAsync_With_FakeAlgorithm_Uses_Provided_Key`
  - `SecurityAccessAsync_With_SeedOverride_Bypasses_RequestSeed`
  - `SecurityAccessAsync_With_NullSeedOverride_FallsThrough_To_RequestSeed`
- `UdsViewModelSecurityAccessTests`:
  - `SecurityAccessAsync_With_PlaceholderAlgorithm_Logs_HintMessage_DoesNotCrash`
  - `SecurityAccessAsync_With_FakeAlgorithm_Logs_Success`
- `SessionPanelViewModelTests`:
  - `SetDefaultSessionCommand_Invokes_UdsClient`
  - `ToggleTesterPresentCommand_Toggles_Property`
- `DidPanelViewModelTests`:
  - `ReadDidCommand_Populates_ReadValue`
  - `WriteDidCommand_Validates_Hex_Input`
  - `ReadDidCommand_When_Disconnected_Does_Nothing`
- `RoutinePanelViewModelTests`:
  - `StartRoutineCommand_Updates_Status`
  - `QueryRoutineCommand_Populates_LastResult`
- `DtcPanelViewModelTests`:
  - `ReadDtcsCommand_Populates_Dtcs_Collection`
  - `ClearDtcsCommand_Invokes_UdsClient`

### 7.2 Integration tests

- `UdsClientSecurityAccessIntegrationTests` (Core):
  - `SecurityAccessAsync_FakeCanChannel_FullRoundTrip_Succeeds_With_Fake_KeyAlgorithm`
- `UdsViewModelIntegrationTests` (App):
  - `UdsViewModel_DidPanel_ReadDid_Updates_SelectedDid_ReadValue`

### 7.3 Manual test checklist (to ship with release notes)

- [ ] Launch app, connect to PCAN-USB FD hardware
- [ ] Open UDS tab, switch to Extended session
- [ ] Switch back to Default session
- [ ] Click SecurityAccess (Level 1) **without** an OEM `IKeyDerivationAlgorithm` registered → expect clear hint message, no crash
- [ ] Register a fake `IKeyDerivationAlgorithm` (test hook in `AppHostBuilder.cs` gated by `Debug`), restart app
- [ ] Click SecurityAccess → expect seed request → fake algorithm returns key → ECU accepts (or NRC if no real ECU)
- [ ] Read DID 0xF190 (VIN) → expect 17 bytes back
- [ ] Edit `uds-dids.json` with a custom DID → restart app → custom DID appears in tree
- [ ] Read DTCs against a real ECU or simulator
- [ ] Disconnect hardware mid-session → expect UDS tab commands disabled, no crash
- [ ] Reconnect hardware → commands re-enabled

## 8. File Inventory

### 8.1 New files

```
src/PeakCan.Host.Core/Uds/IKeyDerivationAlgorithm.cs
src/PeakCan.Host.Core/Uds/PlaceholderKeyAlgorithm.cs
src/PeakCan.Host.Core/Uds/KeyAlgorithmNotConfiguredException.cs
src/PeakCan.Host.Core/Uds/Database/DidDefinition.cs
src/PeakCan.Host.Core/Uds/Database/DidDatabase.cs
src/PeakCan.Host.Core/Uds/Database/RoutineDefinition.cs
src/PeakCan.Host.Core/Uds/Database/RoutineDatabase.cs

src/PeakCan.Host.App/ViewModels/Uds/SessionPanelViewModel.cs
src/PeakCan.Host.App/ViewModels/Uds/DidPanelViewModel.cs
src/PeakCan.Host.App/ViewModels/Uds/RoutinePanelViewModel.cs
src/PeakCan.Host.App/ViewModels/Uds/DtcPanelViewModel.cs
src/PeakCan.Host.App/ViewModels/Uds/UdsLogLine.cs
src/PeakCan.Host.App/Views/UdsView.xaml
src/PeakCan.Host.App/Views/UdsView.xaml.cs

tests/PeakCan.Host.Core.Tests/Uds/PlaceholderKeyAlgorithmTests.cs
tests/PeakCan.Host.Core.Tests/Uds/Database/DidDatabaseTests.cs
tests/PeakCan.Host.Core.Tests/Uds/Database/RoutineDatabaseTests.cs
tests/PeakCan.Host.Core.Tests/Uds/UdsClientSecurityAccessTests.cs
tests/PeakCan.Host.App.Tests/ViewModels/Uds/UdsViewModelSecurityAccessTests.cs
tests/PeakCan.Host.App.Tests/ViewModels/Uds/SessionPanelViewModelTests.cs
tests/PeakCan.Host.App.Tests/ViewModels/Uds/DidPanelViewModelTests.cs
tests/PeakCan.Host.App.Tests/ViewModels/Uds/RoutinePanelViewModelTests.cs
tests/PeakCan.Host.App.Tests/ViewModels/Uds/DtcPanelViewModelTests.cs
tests/PeakCan.Host.App.Tests/ViewModels/Uds/UdsViewModelIntegrationTests.cs
```

### 8.2 Modified files

```
src/PeakCan.Host.Core/Uds/UdsClient.cs                              (+ ctor + overload)
src/PeakCan.Host.App/ViewModels/UdsViewModel.cs                      (- NotImplementedException, + KeyProvider error handling, refactor to orchestrator)
src/PeakCan.Host.App/AppHostBuilder.cs                              (+ DI registrations)
src/PeakCan.Host.App/AppShell.xaml                                   (+ UDS TabItem)
src/PeakCan.Host.App/AppShellViewModel.cs                           (+ UdsViewModel property)
README.md                                                            (+ v1.1.0 section)
docs/release-notes-v1.1.0.md                                        (NEW)
src/PeakCan.Host.App/PeakCan.Host.App.csproj                        (already references all needed packages)
tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj        (no new packages needed)
tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj          (no new packages needed)
```

## 9. Open Questions

None. All design decisions resolved in this spec. OEM-specific algorithm delivery is explicitly out of scope (see N1); it is a downstream OEM task and does not block v1.1.0 ship.

## 10. References

- [v0.10.1 release notes](file:///D:/claude_proj2/peakcan-host/docs/release-notes-v0.10.1.md)
- [UDS diagnostic stack design](file:///D:/claude_proj2/peakcan-host/docs/superpowers/specs/2026-06-22-uds-diagnostic-stack-design.md) — original spec for the Core/Infrastructure side
- [UDS diagnostic stack plan](file:///D:/claude_proj2/peakcan-host/docs/superpowers/plans/2026-06-22-uds-diagnostic-stack-plan.md) — plan that this spec completes (Phase E)
- [Project README](file:///D:/claude_proj2/peakcan-host/README.md) — architecture, layering rules, test counts
- [ISO 14229-1:2020](https://www.iso.org/standard/72442.html) — UDS application layer
- [ISO 15765-2:2024](https://www.iso.org/standard/83899.html) — ISO-TP transport layer
