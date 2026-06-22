# PeakCan Host

Windows-only WPF desktop host for **PEAK PCAN-USB FD / Pro FD** — generic
CAN bus monitor with DBC decoding, manual send, real-time signal view,
and 1 Hz bus statistics.

> **Status:** MVP v0.6.0 — see [Spec](docs/superpowers/specs/2026-06-18-peakcan-host-design.md)
> for the design and [Sprint 17 Plan](docs/superpowers/plans/2026-06-19-sprint-17-v0-2-0.md)
> for the previous v0.2.0 defect-fix plan, plus
> [Release Notes](docs/release-notes-v0.2.1.md) for the v0.2.1 high-bug
> review triage. 371 unit tests pass (155 Core + 141 App
> + 74 Infrastructure); 5 architecture rules enforced via NetArchTest;
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

Output: **371 pass + 2 SKIP** across Core (155) / Infrastructure (74) /
App (141 + 2 SKIP — `TraceServiceTests.ExecuteAsync_Periodically_Flushes_Channel_Into_VM_Batch`
+ `OpenAsync_When_User_Cancels_Dialog_Does_Nothing`).
With `dotnet test --collect:"XPlat Code Coverage"` a per-test-project
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
- **Multiplexed signals (M / m)** — parsed and stored, but not yet
  decoded in the signal view (v1.1).
- **IEEE float / double** (Vector extension) — accepted; decoder falls
  back to int if the keyword is unrecognized.
- **Value tables** — fully supported; signal view will display the
  decoded value-name pair (v1.1).
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
| **No DDD-level IFileDialogService seam** | The `OpenFileDialog.ShowDialog` in `DbcViewModel.OpenAsync` is currently called inline; the test for the user-cancel path is `[Fact(Skip=...)]`. A service extraction is a v1.1 refactor. |
| **VMs are not `IDisposable`** | All ViewModels are DI singletons that live for the whole process. Disposing would unsubscribe from singleton services that are never disposed themselves — a latent footgun. Both VM and service die together at process exit. |

## Roadmap

- **v0.7.0** — IFileDialogService extraction, additional polish.
- **v1.0** — Real-time signal charts, scripting automation
  (CodeMirror 6 + sandboxed script engine).
- **v1.1** — UDS diagnostic stack.
- **v2.0** — J1939 / CANopen, cross-platform (Linux + SocketCAN).

## License

Project-internal. PCAN-Basic SDK is used per PEAK-System terms.
