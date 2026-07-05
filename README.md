# PeakCan Host

Windows-only WPF desktop host for **PEAK PCAN-USB FD / Pro FD** — generic
CAN bus monitor with DBC decoding, manual send, real-time signal view,
and 1 Hz bus statistics.

> **Status:** v3.5.5 — peer-review hardening PATCH (sandbox fix + Dispose
> race test + ChannelRouter acquire-fence read + IFrameSink blocking
> contract + ScriptEngine CAS interrupt + README sync).
> See [Release Notes v3.5.5](docs/release-notes-v3.5.5.md) for the
> PATCH summary. **~1098 unit tests pass** (404 Core + 84 Infrastructure
> + ~610 App); 5 SKIP; 5 architecture rules enforced via NetArchTest;
> CI runs on every push to `main`.

## Features (MVP)

- **Probe + Connect / Disconnect** — detect a PEAK PCAN-USB FD, open a
  CAN FD channel at 1 Mbps, register on the in-process frame router,
  and release the hardware at any time via the **Disconnect** button.
- **Trace view** — virtualized DataGrid of every received frame
  (timestamp, channel, ID, DLC, hex data, decoded row).
- **DBC file load** — parse a `.dbc` file off the UI thread; populate a
  message table with sender, DLC, signal list.
- **Signal view** — DBC-decoded live signals per message, with raw hex
  and physical value (factor / offset applied).
- **Manual send** — enter CAN ID + hex data, click **Send**; CAN FD
  flag toggle; Standard (11-bit) and Extended (29-bit) frame formats.
- **Bus statistics** — 1 Hz OxyPlot chart of frames-per-second + bus
  load %; total + error frame counters.
- **Serilog rolling logs** at `%LocalAppData%\PeakCan.Host\logs\`.

## v0.2.0 (Sprint 17)

Six design defects closed, all with unit-test coverage:

- **Disconnect button** (C1) — toolbar button releases the PEAK
  hardware without exiting the app; toolbar `IsConnected` cross-triggers
  both the Connect and Disconnect CanExecute states.
- **Safer channel lifecycle** (H1 + H5) — extracted a `ChannelConnectGate`
  that owns the connect/disconnect state machine (lock +
  `CancellationTokenSource` + read-loop task) so the previous read-loop
  race and the previous `DisposeAsync` double-throw are now caught by
  12 dedicated unit tests (including a 64-thread concurrent
  `TryEnter` race).
- **`IChannelFactory` seam** (H4) — the shell no longer news up
  `PeakCanChannel` directly; a new `IChannelFactory` in Core +
  `PeakCanChannelFactory` in Infrastructure.Peak + `FakeChannelFactory`
  test double let the VM's connect/disconnect state machine be driven
  end-to-end in unit tests for the first time.
- **Hardcoded baud / handle constants** (H8) — `PcanUsbFdFirstHandle`,
  `DefaultBaudRate`, `DefaultFd` promoted to class-level named constants
  on `AppShellViewModel`.
- **DBC decode offload** (M11) — `DbcDecodeBackgroundService` consumes
  raw frames on the SDK read thread (`OnFrame` is now a single
  `TryWrite` enqueue) and runs the dictionary lookup +
  `SignalViewModel.ApplyFrame` on a dedicated worker thread.
  Net effect at 8 kfps: the SDK read loop is a pure forwarder; DBC
  decode no longer competes with frame intake.
- **Architectural cleanup** — T3's `IChannelFactory` work exposed a
  Core↔Infrastructure reference cycle in the original plan; resolved by
  relocating `ICanChannel` and `Unit` to Core (preserving NetArchTest
  rule 2: Core must not depend on `Peak.Can.Basic`). `BaudRate`'s
  `TPCANBaudrate?` field was retired and replaced by a
  `PeakCanChannel.ResolveClassicCode` adapter switch.

## v0.2.1 (HIGH-bug review triage)

Seven HIGH-severity bugs found by a 71-file multi-agent code review,
all closed with regression tests. See
[Release Notes v0.2.1](docs/release-notes-v0.2.1.md) for the per-bug
writeup. Summary:

- **`AppShellViewModel.DisconnectAsync` catch path** (H1) — if
  `DisconnectAsync` throws, the user was stuck: `IsConnected` stayed
  true (Disconnect button re-enabled), `ChannelRouter` still routed
  frames to the dead channel, and `SendService.ActiveChannel` still
  pointed at it. Catch path now resets all three in the same order
  as the success path.
- **CAN FD `StartBit` byte overflow** (H2 + H3) — `Signal.StartBit`
  was `byte` (max 255) and the decoders used `(byte)(start + i)`,
  silently truncating any CAN FD Motorola signal that starts past
  byte 31. Widened to `ushort` and replaced byte arithmetic with
  int throughout the decoders.
- **Multiplexor `FindIndex(-1) → ushort 65535` wrap-around** (H4) —
  a malformed DBC with multiplexed signals but no multiplexor
  produced `MultiplexorSignalIndex = 65535`, crashing downstream
  dispatch. Now null when no `M` signal exists.
- **Read-loop silent swallow + classic/FD cross-tripping** (H5 + H6) —
  the SDK read loop's catch was empty (no log) and wrapped both
  classic and FD reads in one try, so a subscriber throw on a
  classic frame silently skipped the FD read. Now each read has its
  own catch with `ILogger<PeakCanChannel>` error logging, plus a
  give-up threshold (100 iterations) for dead-bus detection.
- **IHost lifecycle + global exception handlers** (H7) — the IHost
  was a local variable in `OnStartup` (never disposed on exit) and
  the app had no global exception handlers (silent production
  crashes). Host now stored as a field, disposed in `OnExit`;
  `AppDomain`, `Dispatcher`, and `TaskScheduler` exception
  handlers installed at startup.

## v0.2.2 – v0.3.x (thread-safety + correctness + cleanup)

- **M1** — `ConnectAsync` catch block now disposes the channel after
  `RegisterChannel` or any subsequent step throws.
- **M2** — `DbcService.Current` uses `Volatile.Read/Write` for
  cross-thread visibility.
- **M3** — `SendAsync` reads via `ActiveChannel` property
  (`Volatile.Read`) instead of the backing field.
- **M7** — `ToFixedBytes8/64` uses `MemoryMarshal.TryGetArray` to
  avoid `ToArray()` allocation (saves 10k GC allocs/sec at 8 kfps).
- **M8** — `CanFrame` custom `Equals/GetHashCode` compares `Data.Span`
  content instead of `ReadOnlyMemory` reference equality.
- **LOW** — doc sync, channel contract, event cleanup, bare catch fix.

## v0.4.0 (multi-channel + testable read loop)

- **`IPcanReader` interface** — abstracts `PCANBasic.Read/ReadFD` so
  `PeakCanChannel`'s read loop can be unit-tested without real PEAK
  hardware. Tests inject a fake reader that yields canned frames.
- **`IChannelEnumerator` + `PeakChannelEnumerator`** — probes
  PCAN-USB channels 1–16 (handles 0x51–0x60) and returns those that
  responded. Toolbar shows a ComboBox for channel selection.
- **`AppShellViewModel`** — `AvailableChannels` + `SelectedChannel`
  properties; `ConnectAsync` uses the selected handle instead of
  hardcoded 0x51. Legacy single-channel `IChannelProbe` path
  preserved for tests without `IChannelEnumerator`.

## v0.5.0 (frame recording + cyclic send)

- **`RecordService`** — records received frames to disk in ASC
  (Vector ASCII, CANoe/CANalyzer compatible) or CSV format.
  `StartRecording(path, format)` / `StopRecording()`. Thread-safe,
  buffered I/O, drop-tolerant. Wired as 5th `IFrameSink` on the
  `ChannelRouter`.
- **`CyclicSendService`** — periodically transmits a configured
  `CanFrame` on the active channel. `Start(frame, interval)` /
  `Stop()`. Configurable interval (default 100 ms). Thread-safe
  timer callback.

## v0.6.0 (frame filter + multiplexor + value-table)

- **Frame filter** — hex-prefix filter on Trace tab (e.g. "1A"
  matches 0x1A0–0x1AF). Filtered count displayed in toolbar.
- **Multiplexor decoding** — multiplexor signal value is extracted;
  only matching multiplexed signals are decoded. Non-muxed signals
  always decoded.
- **Value-table names** — DBC `VAL_TABLE_` / `VAL_` entries are
  resolved and displayed in a new "Value" column on the Signal tab.

## v0.7.0 (IFileDialogService + testability)

- **`IFileDialogService`** — abstraction over file-open dialogs.
  `WpfFileDialogService` (production) pops `OpenFileDialog`; tests
  inject a fake that returns a canned path or simulates cancellation.
- **`DbcViewModel`** — uses `IFileDialogService` instead of direct
  `OpenFileDialog` usage. The previously-skipped
  `OpenAsync_When_User_Cancels_Dialog_Does_Nothing` test is now
  enabled. New `OpenAsync_With_FakeDialog_Loads_File` end-to-end test.

## v0.8.0 (Real-time signal chart)

- **Signal chart** — OxyPlot time-series chart below the Signal
  DataGrid. Check the **Plot** checkbox on any signal row to add it
  to the chart. Multiple signals render with distinct Tableau 10
  palette colors. Rolling 30-second window with auto-scrolling X axis.
- **`SignalChartViewModel`** — owns the OxyPlot `PlotModel`, manages
  per-signal `LineSeries`, buffers incoming decoded samples, and
  drains them at 30 Hz via a `DispatcherTimer`. Buffer coalescing:
  at ~8 kfps only the latest value per signal per 33 ms tick survives,
  keeping OxyPlot redraws at 30 Hz instead of 8 kHz.
- **`SignalEntry.IsSelected`** — mutable checkbox state with
  `INotifyPropertyChanged` for two-way DataGrid binding.
- **`SignalViewModel`** — now accepts an optional
  `SignalChartViewModel` dependency; `ApplyFrame` pushes decoded
  samples to the chart buffer; `Reset` clears both the grid and the
  chart.

## v0.8.1 (Signal chart polish)

- **Signal statistics** — `GetStatistics()` returns per-signal
  min/max/avg/sample-count for the charted window.
- **CSV export** — `ExportChartCsv` command exports all charted
  signal data to a CSV file (Time column + one column per signal).
  Toolbar button with `SaveFileDialog`.
- **Chart toolbar** — Export CSV, Clear Chart buttons + charted
  signal count display.
- **`ClearChart` command** — unchecks all Plot checkboxes and
  resets the chart.

## v0.9.0 (Trace + DBC polish)

- **Plot All / Plot None** — one-click buttons to select or
  deselect all signals for charting.
- **Signal statistics panel** — bottom panel showing min/max/avg/n
  for each charted signal.
- **Message ID frequency stats** — `GetMessageIdStats()` returns
  top-N message IDs by count with percentages. Total frame counter
  on the Trace filter bar.
- **DBC message search** — search bar on the DBC tab filters
  messages by name or sender (case-insensitive substring).

## v0.9.1 (Signal + DBC detail polish)

- **Signal search filter** — search bar on the Signal tab filters
  signals by message or signal name (case-insensitive substring).
- **DBC signal details** — expand a message row to see its signal
  list with name, unit, mux status, and bit layout.

## v0.9.2 (Trace highlight)

- **Trace row highlight** — highlight bar on the Trace tab;
  enter a hex prefix to highlight matching rows with a yellow
  background (`#FFFDE7`). Matching is by CAN ID hex prefix
  (case-insensitive).

## v0.10.0 (Trace polish)

- **Error frame highlight** — error rows get red background
  (`#FFCDD2`); FD rows get blue (`#E3F2FD`); highlight yellow
  (`#FFFDE7`). Priority: error > FD > highlight.
- **Frame type column** — "Type" column shows "FD", "ERR", or "".
- **Errors-only filter** — checkbox to show only error frames.
- **Pause** — checkbox to freeze the trace display while counters
  still update.
- **Clear button** — clears all trace entries and resets counters.
- **Export CSV** — exports current trace entries to CSV file.
- **Auto-scroll** — automatically scrolls to newest rows when at
  the bottom; pauses when user scrolls up.

## v1.0.0 (Scripting Engine)

- **JavaScript scripting** — write and execute scripts to automate CAN
  bus operations. Scripts run in a trusted V8 runtime with a curated
  `can.*` / `dbc.*` surface. **Not a security sandbox**: scripts authored
  by trusted users can call into the .NET runtime via standard JS
  reflection patterns. Do not execute untrusted script sources without
  review.
- **`can.*` API** — `can.send(id, data, options?)` to transmit frames;
  `can.onFrame(callback)` to register callbacks for all received frames;
  `can.onMessage(id, callback)` for specific CAN ID or hex prefix.
- **`dbc.*` API** — `dbc.load(path)` to load DBC files;
  `dbc.decode(frame)` to decode frames; `dbc.getSignal()` to query
  signal values.
- **Utility functions** — `log()`, `warn()`, `error()` for script
  output; `delay(ms)` for async waits; `hex()`, `toHex()` for
  formatting.
- **Script lifecycle** — `onInit()` runs once at start; `onDispose()`
  runs on stop for cleanup.
- **CodeMirror 6 editor** — syntax highlighting and basic editing in
  the Script tab (WebView2-based).
- **Output panel** — timestamped script output with auto-scroll and
  clear button.
- **Example scripts** — 6 pre-built examples in `scripts/examples/`:
  frame logger, DBC signal monitor, periodic send, request-response,
  signal statistics, bus load generator.

## v1.1.0 (UDS Diagnostic Stack)

- **ISO 15765-2 (ISO-TP)** — segmented message transport over CAN with
  Single/First/Consecutive/Flow Control frames.
- **ISO 14229 (UDS)** — all mandatory diagnostic services:
  * DiagnosticSessionControl (0x10) — Default/Extended/Programming sessions
  * ECUReset (0x11) — Hard/Soft/Power-Down reset
  * ReadDataByIdentifier (0x22) — read DID values
  * WriteDataByIdentifier (0x2E) — write DID values
  * SecurityAccess (0x27) — seed/key authentication
  * TesterPresent (0x3E) — session keep-alive
  * RoutineControl (0x31) — start/stop/query routines
  * ReadDTCInformation (0x19) — read diagnostic trouble codes
  * ClearDiagnosticInformation (0x14) — clear DTCs
  * RequestDownload/TransferData/RequestTransferExit (0x34/0x36/0x37) — flash programming
- **Session management** — automatic session state tracking
- **Security access** — seed/key exchange with configurable algorithms
- **UDS tab** — DID read/write, routine execution, DTC list with clear

### v1.1.0 additions (this release)

- **`IKeyDerivationAlgorithm` seam** — `UdsClient` gains a 3-arg constructor
  accepting an OEM-specific key derivation algorithm. A new public overload
  `SecurityAccessAsync(byte requestLevel, CancellationToken ct = default)`
  performs the full RequestSeed → ComputeKey → SendKey handshake. Default
  DI registration is `PlaceholderKeyAlgorithm`, which throws
  `KeyAlgorithmNotConfiguredException(securityLevel)` and surfaces a clear
  configuration hint in the UDS log when SecurityAccess is invoked before
  an OEM algorithm is wired. OEMs register their seed→key implementation
  via DI; no recompile needed. `UdsViewModel.SecurityAccessAsync` no
  longer throws `NotImplementedException` — it fails fast with a
  targeted hint when no OEM algorithm is wired, and otherwise delegates
  the full handshake to the new overload. Seed bytes are never logged
  (C-2 redaction invariant preserved; strengthened — the seed byte
  sequence is no longer logged at all because the new overload
  encapsulates the RequestSeed leg internally).
- **`DidDatabase`** — 5 built-in defaults (VIN 0xF190, ECU SW version
  0xF184, ECU HW version 0xF191, Part Number 0xF187, Supplier ID 0xF18A)
  + JSON load from `%APPDATA%\PeakCan.Host\uds-dids.json`. User entries
  with matching IDs override built-ins; non-matching entries are appended.
  Missing or malformed JSON falls back to built-in defaults and logs a
  warning — the UI never breaks on bad config.
- **`RoutineDatabase`** — 100% OEM-defined; loads from
  `%APPDATA%\PeakCan.Host\uds-routines.json`. Empty list when no file
  is present; missing/malformed JSON handled identically to DidDatabase.
- **`HexUshortJsonConverter`** — shared JSON converter that accepts
  UDS 16-bit ids in decimal (`6160`), hex-with-prefix (`"0xF190"`), or
  hex-without-prefix (`"F190"`) forms. Used by both databases so the
  natural hex representation works in user JSON files.

> **v1.2 backlog (not in this release):** the spec's "4-panel orchestrator
> refactor" (`SessionPanelViewModel` / `DidPanelViewModel` /
> `RoutinePanelViewModel` / `DtcPanelViewModel` + JSON databases as
> `DataGrid` ItemsSources) is **deferred to v1.2** to keep v1.1.0 ship
> scope tight; the existing monolithic `UdsViewModel` and the existing
> `UdsView.xaml` (TabControl with free-text DID / Routine ID inputs +
> DTC DataGrid + Log Panel) remain.

## Prerequisites

- **Windows 10 (1809+) or Windows 11** for the WPF app
- **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)** for
  development (the published exe is self-contained — no runtime required
  on the target machine)
- **PEAK PCAN driver** for hardware operation
  ([PEAK-System download page](https://www.peak-system.com/PCAN-USB-FD.366.0.html))

## Build

```bash
dotnet build PeakCan.Host.slnx -c Release
```

The solution contains 3 production projects (Core / Infrastructure / App)
and 3 test projects (one per layer). Build output goes to
`src/<project>/bin/Release/<TFM>/`.

## Run (from source)

```bash
dotnet run --project src/PeakCan.Host.App
```

The shell window opens on the **Trace** tab. Click **Probe** to detect
the PCAN device, then **Connect** to start receiving frames.

## Run (self-contained published exe)

```bash
dotnet publish src/PeakCan.Host.App -c Release -r win-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -o artifacts/win-x64/

artifacts/win-x64/PeakCan.Host.exe
```

Output is a single ~66 MB `.exe` with the .NET 10 runtime embedded. See
[artifacts/README.md](artifacts/README.md) for the full publish / smoke
guide.

## Test

```bash
dotnet test PeakCan.Host.slnx -c Debug
```

Output: **~1098 pass + 5 SKIP** across Core (404) / Infrastructure (84) /
App (~610 — 3 hardware SKIP + 1 wall-clock-sensitive
`TraceServiceTests.ExecuteAsync_Periodically_Flushes_Channel_Into_VM_Batch`
SKIP + 1 unrelated SKIP). With
`dotnet test --collect:"XPlat Code Coverage"` a per-test-project
`cobertura.xml` is also produced and uploaded as a CI artifact.

## Architecture

3-layer separation enforced by NetArchTest
([`tests/PeakCan.Host.Infrastructure.Tests/Architecture/LayeringRulesTests.cs`](tests/PeakCan.Host.Infrastructure.Tests/Architecture/LayeringRulesTests.cs)):

```
   PeakCan.Host.App            (WPF, MVVM, BackgroundService, DI composition)
            │
            ▼  uses
   PeakCan.Host.Infrastructure (PEAK SDK adapter, ChannelRouter, BusStatistics)
            │
            ▼  uses
   PeakCan.Host.Core           (CanFrame, DBC parser, SignalDecoder, Result)
```

The App layer is forbidden from referencing the PEAK SDK directly; every
hardware call goes through `IChannelProbe` / `PeakCanChannel` in
Infrastructure. CI fails any PR that violates the boundary.

## DBC parser scope

- **Supported keywords**: `VERSION`, `NS_`, `BS_`, `BU_`, `BO_`, `SG_`,
  `VAL_`, `VAL_TABLE_`, `CM_`, `BA_DEF_`, `BA_`, `SIG_GROUP_`, `EV_`
- **Multiplexed signals (M / m)** — fully supported (v0.6.0). Multiplexor
  value is extracted; only matching multiplexed signals are decoded.
- **IEEE float / double** (Vector extension) — accepted; decoder falls
  back to int if the keyword is unrecognized.
- **Value tables** — fully supported (v0.6.0). Signal view displays the
  decoded value-name pair in a "Value" column.
- **Custom attributes** — `BA_DEF_` accepted; ignored at the decoder
  layer (no consumer yet).

See the spec §"DBC parser scope" for the full subset.

## Architecture / decisions

| Decision | Rationale |
|---|---|
| **.NET 10** (not 8) | The dev box only has 10.0.300 SDK; 8.0 was unavailable. The published exe is self-contained so the target machine does not need any specific runtime. |
| **`Peak.PCANBasic.NET` 5.0.1** (not `Peak.Can.Basic`) | The legacy `Peak.Can.Basic` NuGet package is not findable on nuget.org. `Peak.PCANBasic.NET` is the PEAK-System-official replacement (127k downloads). |
| **OxyPlot.Wpf 2.2.0** (not LiveChartsCore 2.0.4) | LiveCharts 2.0.4's native dependencies (OpenTK + SkiaSharp.Views.WPF) target .NET Framework only and fail at runtime on .NET 10. OxyPlot is pure-managed and works. |
| **Single hardcoded channel (`0x51`)** | The MVP probes + connects the PCAN-USB FD first handle. Multi-channel enumeration added in v0.4.0. |
| **`IFileDialogService` seam** | `DbcViewModel.OpenAsync` uses `IFileDialogService` (v0.7.0) instead of direct `OpenFileDialog`. Tests inject a fake; the previously-skipped cancel test is now enabled. |
| **VMs are not `IDisposable`** | All ViewModels are DI singletons that live for the whole process. Disposing would unsubscribe from singleton services that are never disposed themselves — a latent footgun. Both VM and service die together at process exit. |

## Roadmap

- **v1.0** — Real-time signal charts, scripting automation
  (CodeMirror 6 + sandboxed script engine).
- **v1.1** — UDS diagnostic stack.
- **v2.0** — J1939 / CANopen, cross-platform (Linux + SocketCAN).

## License

Project-internal. PCAN-Basic SDK is used per PEAK-System terms.
