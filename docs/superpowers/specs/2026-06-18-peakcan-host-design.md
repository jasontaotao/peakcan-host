# PeakCan Host — Design Spec

- **Date**: 2026-06-18
- **Author**: Claude (brainstorming session)
- **Status**: Approved (brainstorm gate cleared, awaiting user review of written spec)
- **Target**: Windows-only desktop application — generic CAN bus monitor + DBC parser, built on PEAK PCAN-USB FD / Pro FD hardware
- **Repository root**: `D:/claude_proj2/peakcan-host/`

---

## 1. Goals & Non-Goals

### 1.1 Goals (MVP)

Provide a Windows desktop application that:

1. Enumerates all installed PEAK CAN channels.
2. Connects / disconnects to a selected channel with configurable baudrate and CAN FD toggle.
3. Streams incoming frames into a real-time, virtualized Trace view without UI jank at 1k+ fps.
4. Loads `.dbc` files and decodes frames into human-readable signal names + physical values.
5. Allows manually crafting and sending individual frames (standard / extended ID; CAN 2.0 / FD).
6. Reports bus statistics: frame rate, total bytes, error frames, bus load estimate.

### 1.2 Non-Goals (deferred)

- Recording & replay (ASC / CSV / BLF) → **v1.1**
- Cyclic / scheduled transmission → **v1.1**
- Frame / signal filters (ID range, signal-value predicates) → **v1.1**
- Script automation (CSX / Roslyn) → **v1.2**
- Real-time signal charting → **v1.2**
- UDS / OBD-II / DoIP diagnostic stack → **v1.3**
- J1939 / CANopen protocol layers → **v2.0**
- Linux / macOS cross-platform (Avalonia port) → **v2.0**

### 1.3 Success Criteria (Definition of Done)

- [ ] App launches and enumerates all installed PCAN channels.
- [ ] User can connect / disconnect with baudrate + FD toggle; status reflects connection state.
- [ ] Live frames appear in Trace within 100 ms of arrival; no dropped frames under 1000 fps steady load.
- [ ] Loading a DBC file automatically decodes known frames in Trace.
- [ ] Manual send round-trips: a sent frame appears in the Trace of the same channel.
- [ ] Bus statistics match a known reference (e.g. PCAN-View) within 1% for fps / errors.
- [ ] `dotnet test` is green; Core layer ≥ 95% branch coverage; total ≥ 80% line coverage.
- [ ] NetArchTest architecture rules are green.
- [ ] `dotnet publish` produces a single-file `win-x64` executable that launches with no separate runtime install.

---

## 2. Hardware & Runtime

| Item | Decision |
|---|---|
| Hardware | PEAK **PCAN-USB FD** / **PCAN-USB Pro FD** (CAN 2.0A/B + CAN FD with BRS) |
| Driver | Official PEAK PCAN driver (user-installed); first-run detection with friendly download link |
| OS | Windows 10 1809+ / Windows 11 |
| UI framework | WPF on .NET 8 (`net8.0-windows10.0.19041.0` TFM) |
| Distribution | Single-file self-contained `win-x64` exe via `dotnet publish` |
| DPI | PerMonitorV2 awareness (manifest) |

**Why this hardware set**: PCAN-USB FD and Pro FD are the most common models; the official `Peak.Can.Basic` managed wrapper covers both. Single-platform Windows keeps the SDK call surface small and matches the official PEAK samples.

---

## 3. Tech Stack

| Concern | Choice | Rationale |
|---|---|---|
| Language | C# 12 / .NET 8 | WPF support, primary PEAK-supported stack |
| UI | WPF (no MAUI / no Avalonia) | Windows-only MVP; native PEAK-CAN SDK targets WPF |
| MVVM | CommunityToolkit.Mvvm 8.x (`[ObservableProperty]`) | Cuts boilerplate ~60% vs hand-written MVVM |
| DI / Hosting | `Microsoft.Extensions.Hosting` 8.x | Standard .NET 8 patterns, BackgroundService integration |
| Logging | `Serilog` + `Serilog.Extensions.Hosting` + `Serilog.Sinks.File` | Sinks to DebugView + rolling local file |
| Charts | `LiveChartsCore.SkiaSharpView.WPF` | Modern, SkiaSharp-backed, free for commercial |
| Tests | xUnit + FluentAssertions + NSubstitute + AutoFixture | Industry standard |
| Architecture rules | `NetArchTest.Rules` | Enforces layering at test time |
| Coverage | `coverlet.collector` | Line + branch coverage |
| DBC parser | **Self-implemented** (hand-written tokenizer + recursive-descent parser) | Zero deps, clean license, full control over multiplexed signals + VAL_ tables |

---

## 4. Architecture

### 4.1 Project Layout

```
PeakCan.Host.sln
├── Directory.Build.props                  # Nullable enable, LangVersion latest, TreatWarningsAsErrors
├── Directory.Packages.props               # Central package management
├── .editorconfig                          # Style enforcement
├── global.json                            # .NET 8 SDK pin
├── README.md
├── src/
│   ├── PeakCan.Host.Core/                 # Domain layer: zero external deps (except BCL)
│   │   ├── CanFrame.cs                    # readonly record struct + ReadOnlyMemory<byte>
│   │   ├── CanId.cs                       # Strongly-typed ID with format + range guards
│   │   ├── FrameFlags.cs                  # [Flags] enum: Rtr, BRS, ESI, ErrFrame, Fd
│   │   ├── Timestamp.cs                   # 100ns precision (PCAN native)
│   │   ├── ChannelId.cs                   # uint handle, primary key
│   │   ├── Dbc/
│   │   │   ├── DbcDocument.cs             # Immutable AST
│   │   │   ├── Message.cs / Signal.cs / Node.cs / ValueTable.cs
│   │   │   ├── DbcTokenizer.cs            # Hand-written scanner
│   │   │   ├── DbcParser.cs               # Recursive-descent, returns Result<DbcDocument>
│   │   │   └── DbcErrorCode.cs
│   │   └── Errors/
│   │       ├── Result.cs                  # Result<T> discriminated union
│   │       └── Error.cs                   # { Code, Message }
│   │
│   ├── PeakCan.Host.Infrastructure/       # PCAN-Basic + Channel plumbing
│   │   ├── Peak/
│   │   │   ├── PeakCanNative.cs           # P/Invoke (managed wrapper over Peak.Can.Basic)
│   │   │   ├── PeakCanChannel.cs          # Single-channel adapter
│   │   │   └── PeakError.cs               # PCAN-Basic status → typed enum
│   │   ├── Channel/
│   │   │   ├── ICanChannel.cs             # Async-friendly channel interface
│   │   │   ├── ChannelWorker.cs           # BackgroundService: SetRcvEvent → ReadFD loop → Channel<T>
│   │   │   ├── ChannelRouter.cs           # Fan-out to N IFrameSink
│   │   │   ├── IFrameSink.cs / IFrameSource.cs
│   │   │   └── ChannelHandle.cs           # Owns/disposes native handle
│   │   └── Statistics/
│   │       └── BusStatisticsCollector.cs   # Interlocked counters + load calc
│   │
│   └── PeakCan.Host.App/                  # WPF MVVM
│       ├── App.xaml / AppShell.xaml       # Main window, menu, status bar
│       ├── Views/
│       │   ├── TraceView.xaml             # DataGrid with VirtualizingStackPanel
│       │   ├── SendView.xaml              # Form: ID + DLC + Data + Frame Type
│       │   ├── DbcView.xaml               # DBC load + message/signal tree
│       │   ├── SignalView.xaml            # Decoded live signals
│       │   └── StatsView.xaml             # LiveCharts2 charts
│       ├── ViewModels/
│       │   ├── AppShellViewModel.cs
│       │   ├── TraceViewModel.cs
│       │   ├── SendViewModel.cs
│       │   ├── DbcViewModel.cs
│       │   ├── SignalViewModel.cs
│       │   └── StatsViewModel.cs
│       ├── Services/
│       │   ├── TraceService.cs            # Batches frames → TraceViewModel
│       │   ├── SendService.cs             # ICanChannel.SendAsync
│       │   ├── DbcService.cs              # Loads + parses + caches DBC
│       │   └── StatisticsService.cs       # Periodic read-out to StatsVM
│       ├── Converters/                    # Hex, time-format converters
│       └── Composition/
│           ├── HostBuilderExtensions.cs   # Wires DI registrations
│           └── AppServices.cs             # Resolver helpers
│
└── tests/
    ├── PeakCan.Host.Core.Tests/
    │   ├── CanFrameTests.cs
    │   ├── CanIdTests.cs
    │   ├── DbcParserTests.cs              # Standard / multiplexed / value tables
    │   ├── SignalDecoderTests.cs          # Little/Big, signed/unsigned, factor/offset
    │   └── ResultTests.cs
    └── PeakCan.Host.Infrastructure.Tests/
        ├── ChannelWorkerTests.cs          # Mocked ICanChannel
        ├── ChannelRouterTests.cs
        ├── BusStatisticsCollectorTests.cs
        └── Architecture/                  # NetArchTest rules
            ├── LayeringRules.cs
            └── ApiSurfaceRules.cs
```

### 4.2 Dependency Rules (enforced by NetArchTest)

- `Core` → no references other than BCL (`netstandard2.1` or `net8.0`).
- `Infrastructure` → references `Core` only.
- `App` → references `Core` + `Infrastructure`, never `Peak.Can.Basic` (the official managed wrapper) directly — only via the `ICanChannel` interface defined in `Infrastructure`.
- WPF namespaces (`System.Windows.*`) never appear in `Core` or `Infrastructure`.

### 4.3 Data Flow

```
┌────────────────────────────────────────────────────────────────────────┐
│                  Peak.Can.Basic (managed wrapper)                      │
│  Initialize / ReadFD / WriteFD / GetStatus / SetRcvEvent               │
└────────────────────────────┬───────────────────────────────────────────┘
                             │ SetRcvEvent fires native hEvent
                             ▼
┌────────────────────────────────────────────────────────────────────────┐
│  ChannelWorker (BackgroundService, one per channel)                    │
│  ─────────────────────────────────────────────────────────────────    │
│  while (!stoppingToken) {                                              │
│      WaitForSingleObject(hEvent, INFINITE);                            │
│      while (PeakCanNative.ReadFD(handle, out msg, out ts)) {           │
│          var frame = ToCanFrame(msg, ts, channelId);                   │
│          _frameWriter.TryWrite(frame);   // Channel<CanFrame>          │
│      }                                                                 │
│  }                                                                     │
└────────────────────────────┬───────────────────────────────────────────┘
                             │ System.Threading.Channels Channel<CanFrame>
                             ▼
┌────────────────────────────────────────────────────────────────────────┐
│  ChannelRouter (fan-out)                                               │
│  IFrameSink[] sinks = [TraceService, StatisticsService, DbcResolver]   │
│  Subscribes to ChannelReader.AllAsync; each sink receives every frame. │
│  Sink exceptions are caught + logged; they don't affect siblings.      │
└────────────────────────────┬───────────────────────────────────────────┘
                             │
        ┌────────────────────┼─────────────────────┬─────────────────┐
        ▼                    ▼                     ▼                 ▼
  ┌──────────┐         ┌──────────┐          ┌──────────┐       ┌──────────┐
  │  Trace   │         │   Send   │          │   DBC    │       │   Stats  │
  │ Service  │         │ Service  │          │ Resolver │       │ Service  │
  └────┬─────┘         └────┬─────┘          └────┬─────┘       └────┬─────┘
       │ batched              │ write              │ decode           │ counters
       ▼                      ▼                    ▼                 ▼
  TraceVM               SendVM              SignalVM            StatsVM
   .Entries              .Queue               .Live              .Counters
       │                      │                    │                 │
       └──── ObservableCollection (UI-thread safe via Dispatcher) ───┘
                                     │
                                     ▼
                           WPF Views (virtualized)
```

**Key code pattern — batched UI flush:**

```csharp
public sealed class TraceService : BackgroundService, IFrameSink
{
    private readonly TraceViewModel _vm;
    private readonly Channel<CanFrame> _batch = Channel.CreateBounded<CanFrame>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest });

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var buffer = new List<CanFrame>(256);
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(50, ct);                          // 20 Hz UI tick
            buffer.Clear();
            while (_batch.Reader.TryRead(out var f)) buffer.Add(f);
            if (buffer.Count > 0)
                await _vm.AppendBatchAsync(buffer);            // Dispatcher.InvokeAsync once
        }
    }

    public void OnFrame(CanFrame f) => _batch.Writer.TryWrite(f);
}
```

**Send path:**

```csharp
public Task<Result<Unit>> SendAsync(CanFrame frame, CancellationToken ct)
    => _channel.WriteAsync(frame, ct);
```

### 4.4 DI Lifetime Map

| Service | Lifetime | Why |
|---|---|---|
| `PeakCanNative` | Singleton | Stateless P/Invoke wrapper |
| `PeakCanChannel` (per channel) | Singleton (keyed by `ChannelId`) | One channel ↔ one connection |
| `ChannelWorker` | Singleton (keyed) | Owns the receive loop |
| `ChannelRouter` | Singleton | Cross-channel fan-out |
| `TraceService` / `StatisticsService` / `DbcService` | Singleton | Maintain rolling state |
| `*ViewModel` | Singleton | MVP runs single-window; trivially switchable to Transient |
| WPF `Window` instances | Transient (factory) | Standard WPF |

---

## 5. Core Data Model

```csharp
// CanId.cs
public readonly record struct CanId(uint Raw, FrameFormat Format)
{
    public bool IsExtended => Format == FrameFormat.Extended;
    public FrameType Type { get; init; }
    // Constructor enforces: Standard ≤ 0x7FF, Extended ≤ 0x1FFFFFFF
}

// CanFrame.cs
public readonly record struct CanFrame(
    CanId Id,
    ReadOnlyMemory<byte> Data,         // 0..64 bytes (CAN FD max)
    FrameFlags Flags,                  // Rtr, BRS, ESI, ErrFrame, Fd
    ChannelId Channel,
    Timestamp Timestamp                // 100ns precision
);

// FrameFlags.cs
[Flags]
public enum FrameFlags : ushort
{
    None = 0,
    Rtr = 1 << 0,                      // CAN 2.0 Remote Transmission Request
    BitRateSwitch = 1 << 1,            // CAN FD BRS — data phase runs at higher baudrate
    ErrorStateIndicator = 1 << 2,      // CAN FD ESI — only meaningful on FD error frames
    ErrFrame = 1 << 3,                 // PCAN_ERROR_* frame (controller reported a bus error)
    Fd = 1 << 4,                       // Frame uses CAN FD format (up to 64 bytes)
}

// DbcDocument.cs — immutable AST
public sealed record DbcDocument(
    string Version,
    IReadOnlyList<Node> Nodes,
    IReadOnlyList<Message> Messages,
    IReadOnlyDictionary<uint, Message> MessagesById,        // O(1) lookup
    IReadOnlyDictionary<string, IReadOnlyList<string>> ValueTables);

public sealed record Message(
    uint Id,
    string Name,
    byte Dlc,
    string Sender,
    IReadOnlyList<Signal> Signals,
    bool IsMultiplexed,
    ushort? MultiplexorSignalIndex);

public sealed record Signal(
    string Name,
    byte StartBit, byte Length,
    ByteOrder Order,                 // 1 = Intel (little-endian), 0 = Motorola (big-endian)
    ValueType ValueType,             // '+' (unsigned int) / '-' (signed int, two's complement)
                                     // Standard DBC supports only + / -; IEEE 32/64-bit float
                                     // is a Vector CANdb++ extension and may be ignored by other tools.
    double Factor, double Offset,
    double Min, double Max,
    string Unit,
    IReadOnlyList<string>? Receivers);

// Result.cs — error-as-value, no exceptions for expected failures
public readonly record struct Result<T>(bool IsSuccess, T? Value, Error? Error)
{
    public static Result<T> Ok(T v) => new(true, v, null);
    public static Result<T> Fail(ErrorCode code, string msg) => new(false, default, new(code, msg));
}
```

**Why these choices:**

- `readonly record struct` for `CanFrame`: avoids heap allocation per frame; PCAN-Basic can return 2–4 frames per `SetRcvEvent` signal.
- `ReadOnlyMemory<byte>` for payload: supports zero-copy slicing into the underlying P/Invoke buffer.
- `CanId` constructor validates range: impossible to construct an illegal ID.
- `DbcDocument` is immutable with pre-built index maps: parse once, decode many.
- `Result<T>` instead of exceptions: DBC files vary wildly in quality; errors are expected and must be propagated explicitly.

---

## 6. Error Handling

### 6.1 Classification & Propagation

| Error source | Type | Propagation | User-facing |
|---|---|---|---|
| PCAN driver not installed | `PeakError.DriverNotLoaded` | `PeakCanNative.Initialize` → `Result.Fail` → startup barrier | Modal dialog at launch + Connect button disabled + log file path shown |
| Device busy | `PeakError.ChannelInUse` | Same | Channel list marks 🔒 + tooltip |
| Baudrate unsupported | `PeakError.IllegalParameter` | Baudrate list filtered at enumerate time | Dropdown hides illegal values |

> Note: `PeakError.*` are friendly .NET names mapped from PCAN-Basic's `TPCANStatus` constants (e.g. `PCAN_ERROR_NODRIVER` → `PeakError.DriverNotLoaded`). The mapping lives in `PeakCanHost.Infrastructure.Peak.PeakErrorMapper`.
| DBC parse failure | `DbcParseError{Line, Column, Reason}` | `DbcParser.ParseAsync` → `Result.Fail` | Toast + double-click row → opens file at line |
| Bus error frame | `FrameFlags.ErrFrame` | `ChannelWorker` flags it; `StatisticsService` increments counter | Trace row turns red; Stats panel increments |
| Receive overrun | `PeakError.Overrun` | `ReadFD` returns overrun; `Logger.Warn` + status bar | Status bar `⚠ Overrun ×N` |
| File IO (DBC load) | `IOException` | `DbcService` catches → `Result.Fail` | Toast + UI stays responsive |
| User cancels long parse | `OperationCanceledException` | `CancellationToken` plumbed end-to-end | Progress bar vanishes, no side effects |

### 6.2 Core Principles

- **Never silently swallow errors.** Every `catch` logs and re-throws as `Result.Fail` or surfaces to UI.
- **Hardware errors ≠ business errors.** `PeakErrorCode` and `DbcErrorCode` are separate enums.
- **Fail fast, but never crash.** Unhandled exceptions land in a friendly dialog with a "Copy log path" button.
- **`Channel<CanFrame>` never throws.** Bounded + `DropOldest`; producers never block; dropped frames only log.

### 6.3 Logging

- **Serilog** sinks: `DebugView` (dev) + `%LocalAppData%\PeakCan.Host\logs\peak-{date}.log` (rolling, 14-day retention).
- Startup at `Information`; user can flip to `Debug` from a hidden menu.
- Source context tagged by `ChannelId` for easy filtering.

---

## 7. Testing Strategy

### 7.1 Pyramid

```
                              E2E smoke (manual for MVP)
                             ────────────────────────────
                    ViewModel behavior tests (FlaUI + xUnit, MVP-optional)
                   ──────────────────────────────────────────────────
              Infrastructure (mocked ICanChannel + ChannelRouter behavior)
             ───────────────────────────────────────────────────────────
        Core unit tests (DBC parser, CanFrame, Signal decoder, Result)
       ────────────────────────────────────────────────────────────────
```

### 7.2 Per-Layer Targets

| Layer | Test type | Tools | Target |
|---|---|---|---|
| `Core` | Unit | xUnit + FluentAssertions + AutoFixture | **100% line / 95% branch** |
| `Infrastructure` | Behavior + (optional) integration with real PCAN | xUnit + NSubstitute | 85% line |
| `App` | ViewModel tests + manual UI smoke | xUnit + FluentAssertions | 70% line |
| **Total** | — | — | **≥ 80% line** (project minimum) |

### 7.3 Required Test Cases (Core)

```csharp
// DbcParserTests
[Fact] public void Parses_Standard_Message_Header();
[Fact] public void Parses_Multiplexed_Signal_With_M_And_m();
[Fact] public void Parses_ValueTable_VAL_For_Signal();
[Fact] public void Rejects_Invalid_Hex_With_Line_And_Column();
[Fact] public void RoundTrips_Famous_Demo_File();          // dbc-forge sample

// CanIdTests
[Fact] public void CanId_Rejects_Standard_Over_Range();
[Fact] public void CanId_Rejects_Extended_Over_Range();

// SignalDecoderTests
[Theory] public void Signal_Decoding_Little_Endian_Unsigned(double raw, double expected);
[Theory] public void Signal_Decoding_Little_Endian_Signed(double raw, double expected);
[Theory] public void Signal_Decoding_BigEndian_Unsigned_With_Offset();
[Fact] public void CanFd_BitRateSwitch_Flag_Preserved_Through_Parser();

// ChannelWorkerTests
[Fact] public async Task Forwards_All_Frames_In_Order();
[Fact] public async Task Drops_Oldest_When_Bounded_Channel_Full();
[Fact] public async Task Stops_Cleanly_On_Cancellation();
```

### 7.4 Architecture Rules (NetArchTest)

```csharp
[Fact] public void Core_Should_Not_Depend_On_WPF();
[Fact] public void Core_Should_Not_Depend_On_Peak_Can_Basic();       // no managed wrapper in Core
[Fact] public void App_Should_Not_Depend_On_Peak_Can_Basic();        // App talks only via ICanChannel
[Fact] public void Infrastructure_Should_Not_Depend_On_WPF();
```

### 7.5 CI Gate

- `dotnet build -c Release` — green.
- `dotnet test --collect:"XPlat Code Coverage"` — green + total coverage ≥ 80%.
- `dotnet format --verify-no-changes` — green.
- NetArchTest rules — green.

Integration tests (real PCAN) tagged `[Trait("category","integration")]`, **skipped in CI**, runnable locally on demand.

---

## 8. Build & Distribution

```bash
dotnet publish src/PeakCan.Host.App -c Release -r win-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -o artifacts/win-x64/
```

Output: `artifacts/win-x64/PeakCan.Host.exe` — single file, no separate runtime install.

Versioning: **SemVer**:
- `0.1.0` — internal MVP preview
- `0.5.0` — feature-complete MVP
- `1.0.0` — public release

---

## 9. Risk Register

| Risk | Impact | Mitigation |
|---|---|---|
| `Peak.Can.Basic` NuGet API changes | M | Pin version in `Directory.Packages.props`; lockfile; CI catches breaks early |
| PCAN driver ↔ Windows version mismatch | M | Startup probe of all channels; clear error + PEAK download link |
| Multi-channel enumeration order unstable | L | Never assume order; key by `ChannelId` (uint handle) |
| Large DBC file (>10MB) blocks UI | M | Background parse via `Task.Run` + progress + `CancellationToken` |
| DataGrid rendering bottleneck under load | M | `VirtualizingStackPanel` + fixed row height + `TraceService` batched flush |
| Hardware model variation across users | M | Explicit support matrix published in README; FD / Classic / Pro all covered |
| First-run no driver | L | Friendly error, not a .NET exception dialog |
| Win11 high-DPI blur | L | `app.manifest` declares PerMonitorV2 awareness |

---

## 10. Open Questions

None at design-approval time. Future-version questions (e.g. Linux port UI tech, scripting sandbox) are intentionally deferred.

---

## 11. References

- PEAK-System PCAN-Basic API: https://www.peak-system.com/PCAN-Basic.242.0.html
- CommunityToolkit.Mvvm: https://learn.microsoft.com/dotnet/communitytoolkit/mvvm
- .NET Channels: https://learn.microsoft.com/dotnet/core/extensions/channels
- LiveCharts2: https://livecharts.dev/
- NetArchTest: https://github.com/BenMorris/NetArchTest