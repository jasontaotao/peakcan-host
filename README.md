# PeakCan Host

Windows-only WPF desktop host for **PEAK PCAN-USB FD / Pro FD** — generic
CAN bus monitor with DBC decoding, manual send, real-time signal view,
and 1 Hz bus statistics.

> **Status:** MVP v0.2.0 — see [Spec](docs/superpowers/specs/2026-06-18-peakcan-host-design.md)
> for the design and [Sprint 17 Plan](docs/superpowers/plans/2026-06-19-sprint-17-v0-2-0.md)
> for the v0.2.0 defect-fix plan. 305 unit tests pass (137 Core + 96 App
> + 72 Infrastructure); 5 architecture rules enforced via NetArchTest;
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

Output: **285 pass + 7 SKIP** across Core (137) / Infrastructure (60 +
2 hardware SKIP) / App (90 + 5 SKIP — 3 hardware + 1
`TraceServiceTests.ExecuteAsync_Periodically_Flushes_Channel_Into_VM_Batch`
+ 1 pre-existing `OpenAsync_When_User_Cancels_Dialog_Does_Nothing`).
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
| **Single hardcoded channel (`0x51`)** | The MVP probes + connects the PCAN-USB FD first handle. Multi-channel enumeration is v1.1 (spec §"v1.1 scope"). |
| **No DDD-level IFileDialogService seam** | The `OpenFileDialog.ShowDialog` in `DbcViewModel.OpenAsync` is currently called inline; the test for the user-cancel path is `[Fact(Skip=...)]`. A service extraction is a v1.1 refactor. |
| **VMs are not `IDisposable`** | All ViewModels are DI singletons that live for the whole process. Disposing would unsubscribe from singleton services that are never disposed themselves — a latent footgun. Both VM and service die together at process exit. |

## Roadmap

- **v1.1** — Multi-channel enumeration, frame recording (ASC / CSV),
  frame filters, cyclic transmission, multiplexor decoding, value-table
  decoded names, IFileDialogService extraction.
- **v1.2** — Real-time signal charts, scripting automation
  (CodeMirror 6 + sandboxed script engine).
- **v1.3** — UDS diagnostic stack.
- **v2.0** — J1939 / CANopen, cross-platform (Linux + SocketCAN).

## License

Project-internal. PCAN-Basic SDK is used per PEAK-System terms.
