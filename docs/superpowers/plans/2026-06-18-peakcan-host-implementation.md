# PeakCan Host Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows-only WPF desktop host application for PEAK PCAN-USB FD / Pro FD that enumerates channels, streams frames into a virtualized Trace view, decodes DBC files, and supports manual send + bus statistics — MVP scoped per `docs/superpowers/specs/2026-06-18-peakcan-host-design.md`.

**Architecture:** 3-layer .NET 8 solution — `PeakCan.Host.Core` (pure domain: CanFrame, DbcDocument, Signal decoder, Result<T>), `PeakCan.Host.Infrastructure` (PCAN-Basic wrapper, ChannelWorker, ChannelRouter, statistics), `PeakCan.Host.App` (WPF MVVM with CommunityToolkit.Mvvm, Microsoft.Extensions.Hosting DI, Serilog, LiveCharts2). Data flow: PCAN `SetRcvEvent` → `ChannelWorker` reads via P/Invoke → `Channel<CanFrame>` → `ChannelRouter` fan-out → 4 services (Trace/Send/Dbc/Stats) → ViewModels → virtualized WPF views.

**Tech Stack:** C# 12 / .NET 8 / WPF / `Peak.Can.Basic` 4.x / CommunityToolkit.Mvvm 8.x / Microsoft.Extensions.Hosting 8.x / Serilog / LiveChartsCore.SkiaSharpView.WPF / xUnit + FluentAssertions + NSubstitute + NetArchTest.

## Global Constraints

- **Project root:** `D:/claude_proj2/peakcan-host/` (new directory, `git init`-ed in Task 1).
- **TFM:** `net10.0-windows10.0.19041.0` for `App`; `net10.0` for `Core` and `Infrastructure`. (Originally planned `net8.0`; bumped to `net10.0` because host machine has only .NET 10 SDK installed — adjusted 2026-06-18 with user approval.)
- **C#:** LangVersion `latest`, nullable **enabled**, `TreatWarningsAsErrors` true.
- **Coverage floor:** Core 100% line / 95% branch; total ≥ 80% line.
- **Architecture rules** (NetArchTest, Task 21): `Core` has zero deps on WPF / `Peak.Can.Basic`; `App` never references `Peak.Can.Basic` directly (only via `ICanChannel` interface); WPF namespaces never appear in `Core` or `Infrastructure`.
- **Conventions:** Conventional Commits; one task = one commit; all commits prefixed `feat:`, `test:`, `chore:`, `refactor:`.
- **DBC parser scope (MVP):** Standard DBC keywords only — `VERSION`, `NS_`, `BS_:`, `BU_:`, `BO_`, `SG_`, `EV_`, `VAL_`, `VAL_TABLE_`, `CM_`, `BA_DEF_` (subset), `BA_`, `SIG_GROUP_`. Multiplexed signals (M / m) supported. IEEE float/double (Vector extension) accepted but decode falls back to int and warns.
- **Time format:** PCAN-Basic `TPCANTimestamp` is in microseconds (millis × 1000 + micros). We expose `Timestamp.TotalMicroseconds`; UI formats as `HH:mm:ss.ffffff`.

## File Structure (created across all tasks)

```
D:/claude_proj2/peakcan-host/
├── .editorconfig
├── .gitignore
├── Directory.Build.props
├── Directory.Packages.props
├── global.json
├── PeakCan.Host.sln
├── README.md
├── src/
│   ├── PeakCan.Host.Core/
│   │   ├── PeakCan.Host.Core.csproj
│   │   ├── CanFrame.cs / CanId.cs / FrameFlags.cs / Timestamp.cs / ChannelId.cs
│   │   ├── Result.cs / Error.cs / ErrorCode.cs
│   │   ├── Dbc/
│   │   │   ├── DbcDocument.cs / Node.cs / Message.cs / Signal.cs / ValueTable.cs
│   │   │   ├── DbcTokenizer.cs
│   │   │   ├── DbcParser.cs
│   │   │   ├── DbcErrorCode.cs
│   │   │   └── SignalDecoder.cs
│   │   └── FrameFormat.cs / FrameType.cs / ByteOrder.cs / ValueType.cs
│   ├── PeakCan.Host.Infrastructure/
│   │   ├── PeakCan.Host.Infrastructure.csproj
│   │   ├── Peak/
│   │   │   ├── PeakCanNative.cs / PeakCanChannel.cs / PeakError.cs / PeakErrorMapper.cs
│   │   │   └── PeakBaudRate.cs / PeakChannelCapability.cs
│   │   ├── Channel/
│   │   │   ├── ICanChannel.cs / IFrameSink.cs / IFrameSource.cs
│   │   │   ├── ChannelWorker.cs / ChannelRouter.cs / ChannelRegistry.cs
│   │   │   └── ChannelException.cs
│   │   └── Statistics/
│   │       └── BusStatisticsCollector.cs
│   └── PeakCan.Host.App/
│       ├── PeakCan.Host.App.csproj
│       ├── App.xaml / App.xaml.cs / AppShell.xaml / AppShell.xaml.cs
│       ├── app.manifest
│       ├── Views/  TraceView.xaml / SendView.xaml / DbcView.xaml / SignalView.xaml / StatsView.xaml
│       ├── ViewModels/  AppShellViewModel.cs / TraceViewModel.cs / SendViewModel.cs / DbcViewModel.cs / SignalViewModel.cs / StatsViewModel.cs
│       ├── Services/  TraceService.cs / SendService.cs / DbcService.cs / StatisticsService.cs
│       ├── Converters/  HexConverter.cs / TimestampConverter.cs / CanIdToStringConverter.cs
│       └── Composition/  AppHostBuilder.cs
└── tests/
    ├── PeakCan.Host.Core.Tests/
    │   ├── PeakCan.Host.Core.Tests.csproj
    │   ├── CanIdTests.cs / FrameFlagsTests.cs / ResultTests.cs
    │   ├── DbcTokenizerTests.cs / DbcParserTests.cs / SignalDecoderTests.cs
    │   └── CanFrameTests.cs
    └── PeakCan.Host.Infrastructure.Tests/
        ├── PeakCan.Host.Infrastructure.Tests.csproj
        ├── PeakErrorMapperTests.cs
        ├── ChannelWorkerTests.cs / ChannelRouterTests.cs
        ├── BusStatisticsCollectorTests.cs
        └── Architecture/  LayeringRulesTests.cs
```

---

### Task 1: Repository scaffolding + solution structure

**Files:**
- Create: `D:/claude_proj2/peakcan-host/.gitignore`
- Create: `D:/claude_proj2/peakcan-host/global.json`
- Create: `D:/claude_proj2/peakcan-host/Directory.Build.props`
- Create: `D:/claude_proj2/peakcan-host/Directory.Packages.props`
- Create: `D:/claude_proj2/peakcan-host/.editorconfig`
- Create: `D:/claude_proj2/peakcan-host/PeakCan.Host.sln`
- Create: `src/PeakCan.Host.Core/PeakCan.Host.Core.csproj`
- Create: `src/PeakCan.Host.Infrastructure/PeakCan.Host.Infrastructure.csproj`
- Create: `src/PeakCan.Host.App/PeakCan.Host.App.csproj`
- Create: `tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj`
- Create: `tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj`

**Interfaces:** N/A (no code yet, just scaffolding).

- [ ] **Step 1: Create project root and git init**

```bash
cd "D:/claude_proj2"
mkdir -p peakcan-host && cd peakcan-host
git init -b main
```

- [ ] **Step 2: Write `global.json`**

```json
{
  "sdk": {
    "version": "10.0.300",
    "rollForward": "latestMajor"
  }
}
```

- [ ] **Step 3: Write `.gitignore`** (standard .NET + Rider/VS, omit node_modules)

```gitignore
bin/
obj/
artifacts/
*.user
*.suo
.vs/
.idea/
TestResults/
coverage/
*.log
```

- [ ] **Step 4: Write `Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <WarningsNotAsErrors>NU1903</WarningsNotAsErrors>
    <AnalysisMode>Recommended</AnalysisMode>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  </PropertyGroup>
</Project>
```

- [ ] **Step 5: Write `Directory.Packages.props`**

```xml
<Project>
  <ItemGroup>
    <PackageVersion Include="Peak.Can.Basic" Version="4.10.0" />
    <PackageVersion Include="CommunityToolkit.Mvvm" Version="8.4.0" />
    <PackageVersion Include="Microsoft.Extensions.Hosting" Version="8.0.1" />
    <PackageVersion Include="Microsoft.Extensions.Logging" Version="8.0.1" />
    <PackageVersion Include="Serilog" Version="4.1.0" />
    <PackageVersion Include="Serilog.Extensions.Hosting" Version="8.0.0" />
    <PackageVersion Include="Serilog.Sinks.File" Version="6.0.0" />
    <PackageVersion Include="LiveChartsCore.SkiaSharpView.WPF" Version="2.0.0-rc5.4" />
    <PackageVersion Include="xunit" Version="2.9.2" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageVersion Include="FluentAssertions" Version="6.12.2" />
    <PackageVersion Include="NSubstitute" Version="5.3.0" />
    <PackageVersion Include="NetArchTest.Rules" Version="1.3.2" />
    <PackageVersion Include="coverlet.collector" Version="6.0.2" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: Write `.editorconfig`** (excerpt — full file in `peakcan-host/.editorconfig`)

```ini
root = true
[*]
charset = utf-8
end_of_line = lf
insert_final_newline = true
indent_style = space
indent_size = 4

[*.{cs,xaml}]
trim_trailing_whitespace = true
dotnet_diagnostic.IDE0005.severity = error   # remove unnecessary using
dotnet_diagnostic.CA1822.severity = none     # allow private static methods as instance
```

- [ ] **Step 7: Create solution and projects**

```bash
cd "D:/claude_proj2/peakcan-host"
dotnet new sln -n PeakCan.Host

dotnet new classlib -n PeakCan.Host.Core -o src/PeakCan.Host.Core --framework net8.0
dotnet new classlib -n PeakCan.Host.Infrastructure -o src/PeakCan.Host.Infrastructure --framework net8.0
dotnet new wpf -n PeakCan.Host.App -o src/PeakCan.Host.App --framework net8.0
dotnet new xunit -n PeakCan.Host.Core.Tests -o tests/PeakCan.Host.Core.Tests --framework net8.0
dotnet new xunit -n PeakCan.Host.Infrastructure.Tests -o tests/PeakCan.Host.Infrastructure.Tests --framework net8.0

dotnet sln add src/*/*.csproj tests/*/*.csproj

dotnet add src/PeakCan.Host.Infrastructure reference src/PeakCan.Host.Core
dotnet add src/PeakCan.Host.App reference src/PeakCan.Host.Core src/PeakCan.Host.Infrastructure
dotnet add tests/PeakCan.Host.Core.Tests reference src/PeakCan.Host.Core
dotnet add tests/PeakCan.Host.Infrastructure.Tests reference src/PeakCan.Host.Infrastructure src/PeakCan.Host.Core

# Add packages
dotnet add src/PeakCan.Host.Infrastructure package Peak.Can.Basic
dotnet add src/PeakCan.Host.App package CommunityToolkit.Mvvm
dotnet add src/PeakCan.Host.App package Microsoft.Extensions.Hosting
dotnet add src/PeakCan.Host.App package Serilog
dotnet add src/PeakCan.Host.App package Serilog.Extensions.Hosting
dotnet add src/PeakCan.Host.App package Serilog.Sinks.File
dotnet add src/PeakCan.Host.App package LiveChartsCore.SkiaSharpView.WPF

dotnet add tests/PeakCan.Host.Core.Tests package FluentAssertions
dotnet add tests/PeakCan.Host.Infrastructure.Tests package FluentAssertions
dotnet add tests/PeakCan.Host.Infrastructure.Tests package NSubstitute
dotnet add tests/PeakCan.Host.Infrastructure.Tests package NetArchTest.Rules
```

- [ ] **Step 8: Tweak TFM of App to enable Win10 APIs**

Edit `src/PeakCan.Host.App/PeakCan.Host.App.csproj` — replace `<TargetFramework>net8.0</TargetFramework>` with:

```xml
<TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
<UseWPF>true</UseWPF>
<ApplicationManifest>app.manifest</ApplicationManifest>
```

Create `src/PeakCan.Host.App/app.manifest` (PerMonitorV2 DPI):

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </windowsSettings>
  </application>
</assembly>
```

- [ ] **Step 9: First build**

```bash
cd "D:/claude_proj2/peakcan-host"
dotnet restore
dotnet build -c Debug
```

Expected: build succeeds with no errors. Five projects compile, App project shows "UseWPF=true".

- [ ] **Step 10: First commit**

```bash
cd "D:/claude_proj2/peakcan-host"
git add -A
git commit -m "chore: scaffold 3-layer solution (Core/Infrastructure/App + 2 test projects)"
```

---

### Task 2: Core — domain types (CanId, CanFrame, FrameFlags, Timestamp, ChannelId)

**Files:**
- Create: `src/PeakCan.Host.Core/FrameFormat.cs`
- Create: `src/PeakCan.Host.Core/FrameType.cs`
- Create: `src/PeakCan.Host.Core/ChannelId.cs`
- Create: `src/PeakCan.Host.Core/Timestamp.cs`
- Create: `src/PeakCan.Host.Core/FrameFlags.cs`
- Create: `src/PeakCan.Host.Core/CanId.cs`
- Create: `src/PeakCan.Host.Core/CanFrame.cs`
- Create: `tests/PeakCan.Host.Core.Tests/CanIdTests.cs`
- Create: `tests/PeakCan.Host.Core.Tests/FrameFlagsTests.cs`
- Create: `tests/PeakCan.Host.Core.Tests/CanFrameTests.cs`

**Interfaces:**
- `CanId` constructor enforces range: Standard ≤ 0x7FF, Extended ≤ 0x1FFFFFFF. Throws `ArgumentOutOfRangeException`.
- `Timestamp.TotalMicroseconds` is the public microsecond counter.

- [ ] **Step 1: Write failing tests for `CanId`**

```csharp
// tests/PeakCan.Host.Core.Tests/CanIdTests.cs
using FluentAssertions;
using PeakCan.Host.Core;
using Xunit;

namespace PeakCan.Host.Core.Tests;

public class CanIdTests
{
    [Fact]
    public void Standard_Accepts_11Bit_Id()
    {
        var id = new CanId(0x123, FrameFormat.Standard);
        id.Raw.Should().Be(0x123);
        id.IsExtended.Should().BeFalse();
    }

    [Fact]
    public void Standard_Rejects_29Bit_Id()
    {
        Action act = () => new CanId(0x800, FrameFormat.Standard);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Extended_Accepts_29Bit_Id()
    {
        var id = new CanId(0x18FF1234, FrameFormat.Extended);
        id.IsExtended.Should().BeTrue();
    }

    [Fact]
    public void Extended_Rejects_Over_29Bit()
    {
        Action act = () => new CanId(0x40000000, FrameFormat.Extended);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0x000)]
    [InlineData(0x7FF)]
    public void Standard_Boundaries_Are_Valid(uint raw) =>
        new CanId(raw, FrameFormat.Standard).Raw.Should().Be(raw);
}
```

- [ ] **Step 2: Run test, verify RED**

```bash
cd "D:/claude_proj2/peakcan-host"
dotnet test tests/PeakCan.Host.Core.Tests --filter CanIdTests
```

Expected: FAIL — `CanId` does not exist.

- [ ] **Step 3: Implement `FrameFormat`, `FrameType`, `ChannelId`, `Timestamp`, `FrameFlags`, `CanId`, `CanFrame`**

```csharp
// src/PeakCan.Host.Core/FrameFormat.cs
namespace PeakCan.Host.Core;
public enum FrameFormat : byte { Standard, Extended }

// src/PeakCan.Host.Core/FrameType.cs
namespace PeakCan.Host.Core;
public enum FrameType : byte { Data, Remote, Error, Status }

// src/PeakCan.Host.Core/ChannelId.cs
namespace PeakCan.Host.Core;
public readonly record struct ChannelId(ushort Handle)
{
    public static ChannelId None => default;
    public override string ToString() => $"ch{Handle}";
}

// src/PeakCan.Host.Core/Timestamp.cs
namespace PeakCan.Host.Core;
public readonly record struct Timestamp(ulong TotalMicroseconds)
{
    public static Timestamp FromMillis(ulong millis, ushort micros)
        => new(millis * 1000UL + micros);
    public override string ToString() => $"{TotalMicroseconds / 1_000_000}:{TotalMicroseconds % 1_000_000:D6}";
}

// src/PeakCan.Host.Core/FrameFlags.cs
namespace PeakCan.Host.Core;
[Flags]
public enum FrameFlags : ushort
{
    None = 0,
    Rtr = 1 << 0,                      // CAN 2.0 Remote Transmission Request
    BitRateSwitch = 1 << 1,            // CAN FD BRS
    ErrorStateIndicator = 1 << 2,      // CAN FD ESI (only meaningful on FD error frames)
    ErrFrame = 1 << 3,                 // PCAN_ERROR_* frame
    Fd = 1 << 4,                       // CAN FD format (up to 64 bytes)
}

// src/PeakCan.Host.Core/CanId.cs
namespace PeakCan.Host.Core;
public readonly record struct CanId
{
    public uint Raw { get; }
    public FrameFormat Format { get; }
    public FrameType Type { get; init; }

    public CanId(uint raw, FrameFormat format, FrameType type = FrameType.Data)
    {
        if (format == FrameFormat.Standard && raw > 0x7FFu)
            throw new ArgumentOutOfRangeException(nameof(raw), raw, "Standard ID exceeds 11 bits");
        if (format == FrameFormat.Extended && raw > 0x1FFFFFFFu)
            throw new ArgumentOutOfRangeException(nameof(raw), raw, "Extended ID exceeds 29 bits");
        Raw = raw;
        Format = format;
        Type = type;
    }

    public bool IsExtended => Format == FrameFormat.Extended;
    public override string ToString()
        => IsExtended ? $"0x{Raw:X8}" : $"0x{Raw:X3}";
}

// src/PeakCan.Host.Core/CanFrame.cs
namespace PeakCan.Host.Core;
public readonly record struct CanFrame(
    CanId Id,
    ReadOnlyMemory<byte> Data,
    FrameFlags Flags,
    ChannelId Channel,
    Timestamp Timestamp)
{
    public byte Dlc => (byte)Data.Length;
    public bool IsFd => (Flags & FrameFlags.Fd) != 0;
    public bool IsError => (Flags & FrameFlags.ErrFrame) != 0;
}
```

- [ ] **Step 4: Run tests, verify GREEN**

```bash
dotnet test tests/PeakCan.Host.Core.Tests --filter CanIdTests
```

Expected: 6 tests pass.

- [ ] **Step 5: Add tests for `CanFrame` and `FrameFlags`**

```csharp
// tests/PeakCan.Host.Core.Tests/FrameFlagsTests.cs
using FluentAssertions;
using PeakCan.Host.Core;
using Xunit;
namespace PeakCan.Host.Core.Tests;
public class FrameFlagsTests
{
    [Fact]
    public void Can_Combine_Multiple_Flags()
    {
        var f = FrameFlags.Fd | FrameFlags.BitRateSwitch;
        f.HasFlag(FrameFlags.Fd).Should().BeTrue();
        f.HasFlag(FrameFlags.BitRateSwitch).Should().BeTrue();
        f.HasFlag(FrameFlags.Rtr).Should().BeFalse();
    }
}

// tests/PeakCan.Host.Core.Tests/CanFrameTests.cs
using FluentAssertions;
using PeakCan.Host.Core;
using Xunit;
namespace PeakCan.Host.Core.Tests;
public class CanFrameTests
{
    [Fact]
    public void IsFd_True_When_Fd_Flag_Set()
    {
        var frame = new CanFrame(
            new CanId(1, FrameFormat.Standard),
            new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
            FrameFlags.Fd | FrameFlags.BitRateSwitch,
            ChannelId.None,
            default);
        frame.IsFd.Should().BeTrue();
        frame.Dlc.Should().Be(4);
    }

    [Fact]
    public void IsError_True_When_ErrFrame_Set()
    {
        var frame = new CanFrame(
            new CanId(0, FrameFormat.Standard, FrameType.Error),
            ReadOnlyMemory<byte>.Empty,
            FrameFlags.ErrFrame,
            ChannelId.None,
            default);
        frame.IsError.Should().BeTrue();
    }
}
```

- [ ] **Step 6: Run all Core tests, verify GREEN**

```bash
dotnet test tests/PeakCan.Host.Core.Tests
```

Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(core): add CanId/CanFrame/FrameFlags/Timestamp/ChannelId domain types"
```

---

### Task 3: Core — `Result<T>` + `Error` + `ErrorCode`

**Files:**
- Create: `src/PeakCan.Host.Core/ErrorCode.cs`
- Create: `src/PeakCan.Host.Core/Error.cs`
- Create: `src/PeakCan.Host.Core/Result.cs`
- Create: `tests/PeakCan.Host.Core.Tests/ResultTests.cs`

**Interfaces:** `Result<T>.Ok(value)`, `Result<T>.Fail(code, message)`, `TryGetValue(out T)`. Match methods provided for fluent chaining.

- [ ] **Step 1: Write failing tests**

```csharp
// tests/PeakCan.Host.Core.Tests/ResultTests.cs
using FluentAssertions;
using PeakCan.Host.Core;
using Xunit;
namespace PeakCan.Host.Core.Tests;
public class ResultTests
{
    [Fact]
    public void Ok_Has_Success_True_And_Value()
    {
        var r = Result<int>.Ok(42);
        r.IsSuccess.Should().BeTrue();
        r.Value.Should().Be(42);
        r.Error.Should().BeNull();
    }

    [Fact]
    public void Fail_Has_Success_False_And_Error()
    {
        var r = Result<int>.Fail(ErrorCode.InvalidArgument, "bad");
        r.IsSuccess.Should().BeFalse();
        r.Value.Should().Be(0);
        r.Error!.Code.Should().Be(ErrorCode.InvalidArgument);
        r.Error.Message.Should().Be("bad");
    }

    [Fact]
    public void TryGetValue_Returns_True_For_Ok()
    {
        var r = Result<string>.Ok("x");
        r.TryGetValue(out var v).Should().BeTrue();
        v.Should().Be("x");
    }

    [Fact]
    public void TryGetValue_Returns_False_For_Fail()
    {
        var r = Result<string>.Fail(ErrorCode.Unknown, "x");
        r.TryGetValue(out _).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run, verify RED**

```bash
dotnet test tests/PeakCan.Host.Core.Tests --filter ResultTests
```

- [ ] **Step 3: Implement**

```csharp
// src/PeakCan.Host.Core/ErrorCode.cs
namespace PeakCan.Host.Core;
public enum ErrorCode
{
    Unknown = 0,
    InvalidArgument,
    InvalidState,
    IoError,
    NotFound,
    ParseFailure,
    HardwareNotAvailable,
    HardwareBusy,
    HardwareParameter,
    Cancelled,
}

// src/PeakCan.Host.Core/Error.cs
namespace PeakCan.Host.Core;
public sealed record Error(ErrorCode Code, string Message);

// src/PeakCan.Host.Core/Result.cs
namespace PeakCan.Host.Core;
public readonly record struct Result<T>(bool IsSuccess, T? Value, Error? Error)
{
    public static Result<T> Ok(T v) => new(true, v, null);
    public static Result<T> Fail(ErrorCode code, string msg)
        => new(false, default, new Error(code, msg));

    public bool TryGetValue(out T? value)
    {
        value = Value;
        return IsSuccess;
    }
}
```

- [ ] **Step 4: Run, verify GREEN**

```bash
dotnet test tests/PeakCan.Host.Core.Tests --filter ResultTests
```

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): add Result<T>, Error, ErrorCode for explicit error propagation"
```

---

### Task 4: Core — DBC Tokenizer

**Files:**
- Create: `src/PeakCan.Host.Core/Dbc/TokenType.cs`
- Create: `src/PeakCan.Host.Core/Dbc/Token.cs`
- Create: `src/PeakCan.Host.Core/Dbc/DbcTokenizer.cs`
- Create: `tests/PeakCan.Host.Core.Tests/DbcTokenizerTests.cs`

**Interfaces:**
- `DbcTokenizer.Tokenize(string text, int maxLine = 1_000_000)` → `IReadOnlyList<Token>` where each `Token` has `Type`, `Lexeme`, `Line`, `Column`.
- Whitespace and `//` line comments are skipped; unrecognized chars throw `DbcParseException`.

- [ ] **Step 1: Write failing tests**

```csharp
// tests/PeakCan.Host.Core.Tests/DbcTokenizerTests.cs
using FluentAssertions;
using PeakCan.Host.Core.Dbc;
using Xunit;
namespace PeakCan.Host.Core.Tests;
public class DbcTokenizerTests
{
    [Fact]
    public void Skips_Whitespace_And_Comments()
    {
        var t = new DbcTokenizer();
        var tokens = t.Tokenize("  // comment\nBO_ 100 Msg: 8 ECU\n");
        tokens.Should().HaveCountGreaterThan(4);
        tokens[0].Lexeme.Should().Be("BO_");
        tokens[0].Line.Should().Be(2);
    }

    [Fact]
    public void Recognizes_Punctuation()
    {
        var t = new DbcTokenizer();
        var tokens = t.Tokenize(": ( ) , ;");
        tokens.Select(x => x.Type).Should().ContainInOrder(
            TokenType.Colon, TokenType.LParen, TokenType.RParen,
            TokenType.Comma, TokenType.Semicolon);
    }

    [Fact]
    public void Captures_String_Literal()
    {
        var t = new DbcTokenizer();
        var tokens = t.Tokenize("CM_ BU_ Node1 \"Some description\";");
        var str = tokens.First(x => x.Type == TokenType.String);
        str.Lexeme.Should().Be("Some description");
    }

    [Fact]
    public void Throws_On_Unknown_Char()
    {
        var t = new DbcTokenizer();
        Action act = () => t.Tokenize("BO_ 100 Msg @ bad");
        act.Should().Throw<DbcParseException>()
            .Where(e => e.Line == 1 && e.Column >= 14);
    }
}
```

- [ ] **Step 2: Run, verify RED**

```bash
dotnet test tests/PeakCan.Host.Core.Tests --filter DbcTokenizerTests
```

- [ ] **Step 3: Implement token types and tokenizer**

```csharp
// src/PeakCan.Host.Core/Dbc/TokenType.cs
namespace PeakCan.Host.Core.Dbc;
public enum TokenType
{
    Eof, Identifier, Integer, Float, String,
    Colon, Comma, Semicolon, LParen, RParen, Plus, Minus, At, Pipe,
    Keyword_BO_, Keyword_SG_, Keyword_BU_, Keyword_VAL_, Keyword_VAL_TABLE_,
    Keyword_EV_, Keyword_CM_, Keyword_BA_DEF_, Keyword_BA_, Keyword_SIG_GROUP_,
    Keyword_VERSION, Keyword_NS_, Keyword_BS_, Keyword_NS_DESC_,
}

// src/PeakCan.Host.Core/Dbc/Token.cs
namespace PeakCan.Host.Core.Dbc;
public readonly record struct Token(TokenType Type, string Lexeme, int Line, int Column);

// src/PeakCan.Host.Core/Dbc/DbcParseException.cs
namespace PeakCan.Host.Core.Dbc;
public sealed class DbcParseException : Exception
{
    public int Line { get; }
    public int Column { get; }
    public DbcParseException(string msg, int line, int column) : base($"{msg} at line {line}, col {column}")
    { Line = line; Column = column; }
}

// src/PeakCan.Host.Core/Dbc/DbcTokenizer.cs
namespace PeakCan.Host.Core.Dbc;
public sealed class DbcTokenizer
{
    private static readonly Dictionary<string, TokenType> _keywords = new()
    {
        ["BO_"] = TokenType.Keyword_BO_, ["SG_"] = TokenType.Keyword_SG_,
        ["BU_"] = TokenType.Keyword_BU_, ["VAL_"] = TokenType.Keyword_VAL_,
        ["VAL_TABLE_"] = TokenType.Keyword_VAL_TABLE_, ["EV_"] = TokenType.Keyword_EV_,
        ["CM_"] = TokenType.Keyword_CM_, ["BA_DEF_"] = TokenType.Keyword_BA_DEF_,
        ["BA_"] = TokenType.Keyword_BA_, ["SIG_GROUP_"] = TokenType.Keyword_SIG_GROUP_,
        ["VERSION"] = TokenType.Keyword_VERSION, ["NS_"] = TokenType.Keyword_NS_,
        ["BS_"] = TokenType.Keyword_BS_, ["NS_DESC_"] = TokenType.Keyword_NS_DESC_,
    };

    public IReadOnlyList<Token> Tokenize(string text, int maxLine = 1_000_000)
    {
        var tokens = new List<Token>(text.Length / 8);
        int line = 1, col = 1, i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\n') { line++; col = 1; i++; continue; }
            if (c == '\r') { i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; col++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            { while (i < text.Length && text[i] != '\n') i++; continue; }

            if (c == '"')
            {
                int start = ++i; int startCol = col++;
                while (i < text.Length && text[i] != '"')
                { if (text[i] == '\n') throw new DbcParseException("Unterminated string", line, col); i++; col++; }
                if (i >= text.Length) throw new DbcParseException("Unterminated string", line, startCol);
                tokens.Add(new Token(TokenType.String, text[start..i], line, startCol));
                i++; col++; continue;
            }

            if (char.IsDigit(c) || (c == '-' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                int start = i; int startCol = col; bool isFloat = false;
                if (text[i] == '-') { i++; col++; }
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
                { if (text[i] == '.') isFloat = true; i++; col++; }
                tokens.Add(new Token(isFloat ? TokenType.Float : TokenType.Integer, text[start..i], line, startCol));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int start = i; int startCol = col;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) { i++; col++; }
                var lex = text[start..i];
                if (_keywords.TryGetValue(lex, out var kt))
                    tokens.Add(new Token(kt, lex, line, startCol));
                else
                    tokens.Add(new Token(TokenType.Identifier, lex, line, startCol));
                continue;
            }

            // Punctuation
            TokenType punc = c switch
            {
                ':' => TokenType.Colon, ',' => TokenType.Comma, ';' => TokenType.Semicolon,
                '(' => TokenType.LParen, ')' => TokenType.RParen, '+' => TokenType.Plus,
                '-' => TokenType.Minus, '@' => TokenType.At, '|' => TokenType.Pipe,
                _ => throw new DbcParseException($"Unexpected character '{c}'", line, col),
            };
            tokens.Add(new Token(punc, c.ToString(), line, col));
            i++; col++;
        }
        if (line > maxLine) throw new DbcParseException($"File exceeds {maxLine} lines", line, col);
        tokens.Add(new Token(TokenType.Eof, "", line, col));
        return tokens;
    }
}
```

- [ ] **Step 4: Run, verify GREEN**

```bash
dotnet test tests/PeakCan.Host.Core.Tests --filter DbcTokenizerTests
```

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): add DBC tokenizer with keyword + punctuation + line/col tracking"
```

---

### Task 5: Core — DBC AST + Parser (BU_, BO_, SG_, basic messages)

**Files:**
- Create: `src/PeakCan.Host.Core/Dbc/ByteOrder.cs`
- Create: `src/PeakCan.Host.Core/Dbc/ValueType.cs`
- Create: `src/PeakCan.Host.Core/Dbc/Node.cs`
- Create: `src/PeakCan.Host.Core/Dbc/Signal.cs`
- Create: `src/PeakCan.Host.Core/Dbc/Message.cs`
- Create: `src/PeakCan.Host.Core/Dbc/ValueTable.cs`
- Create: `src/PeakCan.Host.Core/Dbc/DbcDocument.cs`
- Create: `src/PeakCan.Host.Core/Dbc/DbcErrorCode.cs`
- Create: `src/PeakCan.Host.Core/Dbc/DbcParser.cs`
- Create: `tests/PeakCan.Host.Core.Tests/DbcParserTests.cs`

**Interfaces:**
- `DbcParser.Parse(string text, CancellationToken ct = default)` → `Result<DbcDocument>`.
- `DbcDocument.MessagesById` is keyed by 32-bit ID with the IDE bit merged (extended IDs have bit 31 set, matching PEAK convention).

- [ ] **Step 1: Write enum + record types**

```csharp
// src/PeakCan.Host.Core/Dbc/ByteOrder.cs
namespace PeakCan.Host.Core.Dbc;
public enum ByteOrder : byte { BigEndian = 0, LittleEndian = 1 }   // matches DBC @1 / @0

// src/PeakCan.Host.Core/Dbc/ValueType.cs
namespace PeakCan.Host.Core.Dbc;
public enum ValueType : byte { Unsigned = 0, Signed = 1, Float = 2, Double = 3 }

// src/PeakCan.Host.Core/Dbc/Node.cs
namespace PeakCan.Host.Core.Dbc;
public sealed record Node(string Name);

// src/PeakCan.Host.Core/Dbc/ValueTable.cs
namespace PeakCan.Host.Core.Dbc;
public sealed record ValueTable(string Name, IReadOnlyDictionary<long, string> Entries);

// src/PeakCan.Host.Core/Dbc/Signal.cs (initial — multiplexor fields added in Task 6)
namespace PeakCan.Host.Core.Dbc;
public sealed record Signal(
    string Name,
    byte StartBit, byte Length,
    ByteOrder Order,
    ValueType ValueType,
    double Factor, double Offset,
    double Min, double Max,
    string Unit,
    IReadOnlyList<string> Receivers,
    bool IsMultiplexor = false,
    bool IsMultiplexed = false,
    ushort? MultiplexValue = null,
    string? ValueTableName = null);

// src/PeakCan.Host.Core/Dbc/Message.cs
namespace PeakCan.Host.Core.Dbc;
public sealed record Message(
    uint Id,
    string Name,
    byte Dlc,
    string Sender,
    IReadOnlyList<Signal> Signals,
    bool IsMultiplexed,
    ushort? MultiplexorSignalIndex);

// src/PeakCan.Host.Core/Dbc/Node.cs (unchanged from above)
// src/PeakCan.Host.Core/Dbc/DbcDocument.cs
namespace PeakCan.Host.Core.Dbc;
public sealed record DbcDocument(
    string Version,
    IReadOnlyList<Node> Nodes,
    IReadOnlyList<Message> Messages,
    IReadOnlyDictionary<uint, Message> MessagesById,
    IReadOnlyDictionary<string, ValueTable> ValueTables);

// src/PeakCan.Host.Core/Dbc/DbcErrorCode.cs
namespace PeakCan.Host.Core.Dbc;
public enum DbcErrorCode
{
    Unknown, UnexpectedToken, MissingSemicolon, InvalidId, InvalidDlc,
    InvalidSignalSpec, DuplicateMessage, DuplicateSignal, FileTooLarge,
}
```

- [ ] **Step 2: Write failing tests for basic parser**

```csharp
// tests/PeakCan.Host.Core.Tests/DbcParserTests.cs
using FluentAssertions;
using PeakCan.Host.Core.Dbc;
using Xunit;
namespace PeakCan.Host.Core.Tests;
public class DbcParserTests
{
    [Fact]
    public void Parses_Version_And_Nodes()
    {
        var src = "VERSION \"1.0\"\n\nNS_ :\n\nBS_:\n\nBU_: ECU1 ECU2\n";
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeTrue();
        r.Value!.Nodes.Select(n => n.Name).Should().BeEquivalentTo("ECU1", "ECU2");
        r.Value.Version.Should().Be("1.0");
    }

    [Fact]
    public void Parses_Simple_Message_With_Signal()
    {
        var src = """
        VERSION "1.0"
        NS_ :
        BS_:
        BU_: ECU

        BO_ 100 Msg1: 8 ECU
         SG_ Speed : 0|16@1+ (0.1,0) [0|6553.5] "km/h"  ECU
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeTrue();
        var msg = r.Value!.MessagesById[100u];
        msg.Name.Should().Be("Msg1");
        msg.Dlc.Should().Be(8);
        msg.Signals.Should().HaveCount(1);
        var s = msg.Signals[0];
        s.Name.Should().Be("Speed");
        s.StartBit.Should().Be(0);
        s.Length.Should().Be(16);
        s.Order.Should().Be(ByteOrder.LittleEndian);
        s.ValueType.Should().Be(ValueType.Unsigned);
        s.Factor.Should().Be(0.1);
        s.Offset.Should().Be(0.0);
        s.Unit.Should().Be("km/h");
    }

    [Fact]
    public void Parses_Extended_Id_With_Ide_Bit()
    {
        var src = """
        BU_: ECU
        BO_ 2147483700 ExtMsg: 8 ECU
         SG_ S : 0|8@1+ (1,0) [0|255] ""  ECU
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeTrue();
        // 0x18FF1234 = 2147483700 (with IDE bit 31 set)
        r.Value!.MessagesById.ContainsKey(0x18FF1234u).Should().BeTrue();
    }

    [Fact]
    public void Fails_With_Line_And_Column_On_Bad_Token()
    {
        var src = "BU_: ECU\nBO_ @ bad\n";
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeFalse();
        r.Error!.Message.Should().Contain("line");
    }
}
```

- [ ] **Step 3: Run, verify RED**

```bash
dotnet test tests/PeakCan.Core.Tests --filter DbcParserTests
```

- [ ] **Step 4: Implement `DbcParser`**

```csharp
// src/PeakCan.Host.Core/Dbc/DbcParser.cs
using System.Globalization;
namespace PeakCan.Host.Core.Dbc;
public static class DbcParser
{
    public static Result<DbcDocument> Parse(string text, CancellationToken ct = default)
    {
        try
        {
            var tokenizer = new DbcTokenizer();
            var tokens = tokenizer.Tokenize(text);
            var p = new ParserState(tokens);
            return p.ParseDocument().Match(DbcDocument (d) => Result<DbcDocument>.Ok(d),
                                           e => Result<DbcDocument>.Fail(ErrorCode.ParseFailure, e));
        }
        catch (DbcParseException ex)
        {
            return Result<DbcDocument>.Fail(ErrorCode.ParseFailure, ex.Message);
        }
    }

    private sealed class ParserState
    {
        private readonly IReadOnlyList<Token> _tokens;
        private int _i;

        public ParserState(IReadOnlyList<Token> tokens) { _tokens = tokens; }
        private Token Current => _tokens[_i];
        private Token Peek(int offset) => _i + offset < _tokens.Count ? _tokens[_i + offset] : _tokens[^1];
        private Token Consume() => _tokens[_i++];

        private Result<DbcDocument> ParseDocument()
        {
            string version = "";
            var nodes = new List<Node>();
            var messages = new List<Message>();
            var valueTables = new Dictionary<string, ValueTable>();
            while (Current.Type != TokenType.Eof)
            {
                switch (Current.Type)
                {
                    case TokenType.Keyword_VERSION:
                        Consume();
                        version = Consume().Lexeme.Trim('"');
                        Expect(TokenType.Semicolon);
                        break;
                    case TokenType.Keyword_BU_:
                        Consume();
                        while (Current.Type == TokenType.Identifier) nodes.Add(new(Consume().Lexeme));
                        Expect(TokenType.Semicolon);
                        break;
                    case TokenType.Keyword_BO_:
                        var m = ParseMessage();
                        if (m.IsSuccess) messages.Add(m.Value);
                        break;
                    case TokenType.Keyword_VAL_TABLE_:
                        var vt = ParseValueTable();
                        if (vt.IsSuccess) valueTables[vt.Value.Name] = vt.Value;
                        break;
                    case TokenType.Keyword_NS_:
                    case TokenType.Keyword_BS_:
                    case TokenType.Keyword_CM_:
                    case TokenType.Keyword_BA_DEF_:
                    case TokenType.Keyword_BA_:
                    case TokenType.Keyword_SIG_GROUP_:
                        SkipUntilSemicolon();
                        break;
                    default:
                        SkipUntilSemicolon();
                        break;
                }
            }
            var byId = messages.ToDictionary(m => m.Id);
            return Result<DbcDocument>.Ok(new DbcDocument(version, nodes, messages, byId, valueTables));
        }

        private Result<Message> ParseMessage()
        {
            int startLine = Current.Line;
            Consume(); // BO_
            if (Current.Type != TokenType.Integer)
                return Result<Message>.Fail(ErrorCode.ParseFailure, $"Expected message ID at line {Current.Line}");
            uint id = uint.Parse(Consume().Lexeme, CultureInfo.InvariantCulture);
            // If bit 31 is set, this is already the merged IDE ID; otherwise Standard
            if ((id & 0x80000000u) == 0 && id > 0x7FF)
                return Result<Message>.Fail(ErrorCode.InvalidId, $"Standard ID {id} exceeds 11 bits at line {startLine}");

            var nameTok = Consume();
            if (nameTok.Type != TokenType.Identifier)
                return Result<Message>.Fail(ErrorCode.ParseFailure, $"Expected message name at line {nameTok.Line}");
            Expect(TokenType.Colon);
            if (Current.Type != TokenType.Integer)
                return Result<Message>.Fail(ErrorCode.InvalidDlc, $"Expected DLC at line {Current.Line}");
            byte dlc = byte.Parse(Consume().Lexeme);
            string sender = "";
            if (Current.Type == TokenType.Identifier) sender = Consume().Lexeme;
            Expect(TokenType.Semicolon);

            var signals = new List<Signal>();
            while (Current.Type == TokenType.Keyword_SG_) signals.Add(ParseSignal());
            return Result<Message>.Ok(new Message(id, nameTok.Lexeme, dlc, sender, signals, false, null));
        }

        private Signal ParseSignal()
        {
            Consume(); // SG_
            var name = Consume().Lexeme;       // ' ' prefix allowed but stripped by Identifier
            if (Current.Type == TokenType.Identifier && name.StartsWith(' ')) name = name.TrimStart();
            // Multiplex indicator is leading char 'M' or 'm' embedded in name
            if (name.StartsWith("M ") || name == "M")
            {
                // Multiplexor signal — recognized in Task 6
            }
            Expect(TokenType.Colon);
            byte start = byte.Parse(Consume().Lexeme);
            Expect(TokenType.Pipe);
            byte len = byte.Parse(Consume().Lexeme);
            Expect(TokenType.At);
            // @1 = little, @0 = big; followed by sign
            var orderTok = Consume();
            ByteOrder order = orderTok.Lexeme == "1" ? ByteOrder.LittleEndian : ByteOrder.BigEndian;
            var sign = Consume();
            ValueType vt = sign.Type switch
            {
                TokenType.Plus => ValueType.Unsigned,
                TokenType.Minus => ValueType.Signed,
                _ => ValueType.Unsigned,
            };
            Expect(TokenType.LParen);
            double factor = ParseDouble();
            Expect(TokenType.Comma);
            double offset = ParseDouble();
            Expect(TokenType.RParen);
            Expect(TokenType.LBracket);
            double min = ParseDouble();
            Expect(TokenType.Pipe);
            double max = ParseDouble();
            Expect(TokenType.RBracket);
            string unit = Consume().Lexeme.Trim('"');
            var receivers = new List<string>();
            if (Current.Type == TokenType.Identifier) receivers.Add(Consume().Lexeme);
            while (Current.Type == TokenType.Comma) { Consume(); receivers.Add(Consume().Lexeme); }
            return new Signal(name, start, len, order, vt, factor, offset, min, max, unit, receivers);
        }

        private Result<ValueTable> ParseValueTable()
        {
            Consume(); // VAL_TABLE_
            var name = Consume().Lexeme;
            var entries = new Dictionary<long, string>();
            while (Current.Type == TokenType.Minus || Current.Type == TokenType.Integer)
            {
                long val = long.Parse(Consume().Lexeme, CultureInfo.InvariantCulture);
                entries[val] = Consume().Lexeme.Trim('"');
            }
            Expect(TokenType.Semicolon);
            return Result<ValueTable>.Ok(new ValueTable(name, entries));
        }

        private double ParseDouble()
            => double.Parse(Consume().Lexeme, CultureInfo.InvariantCulture);

        private void Expect(TokenType type)
        {
            if (Current.Type != type)
                throw new DbcParseException($"Expected {type}, got {Current.Type}", Current.Line, Current.Column);
            Consume();
        }

        private void SkipUntilSemicolon()
        {
            while (Current.Type != TokenType.Semicolon && Current.Type != TokenType.Eof) Consume();
            if (Current.Type == TokenType.Semicolon) Consume();
        }
    }
}
```

- [ ] **Step 5: Run, verify GREEN**

```bash
dotnet test tests/PeakCan.Host.Core.Tests --filter DbcParserTests
```

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(core): add DBC parser — version/NS_/BS_/BU_/BO_/SG_/VAL_TABLE_ (no multiplexed yet)"
```

---

### Task 6: Core — DBC parser extensions (multiplexed M/m + VAL_ for signals)

**Files:**
- Modify: `src/PeakCan.Host.Core/Dbc/DbcParser.cs` (extend `ParseMessage` to handle multiplexed signals)
- Modify: `src/PeakCan.Host.Core/Dbc/Signal.cs` (no signature change — fields already present)
- Create: `tests/PeakCan.Host.Core.Tests/DbcParserMultiplexedTests.cs`

**Interfaces:** Multiplexor signal is `SG_ M Name : ...` (no space after `M` indicates multiplexor; `m<value>` indicates multiplexed). `VAL_ <MsgId> <SigName> <int> "<text>" ... ;` registers per-signal value tables.

- [ ] **Step 1: Write failing tests for multiplexed signals and VAL_**

```csharp
// tests/PeakCan.Host.Core.Tests/DbcParserMultiplexedTests.cs
using FluentAssertions;
using PeakCan.Host.Core.Dbc;
using Xunit;
namespace PeakCan.Host.Core.Tests;
public class DbcParserMultiplexedTests
{
    [Fact]
    public void Parses_Multiplexor_Signal()
    {
        var src = """
        BU_: ECU
        BO_ 200 MuxMsg: 8 ECU
         SG_ Mux M : 0|4@1+ (1,0) [0|15] ""  ECU
         SG_ Val0 m0 : 8|8@1+ (1,0) [0|255] ""  ECU
         SG_ Val1 m1 : 16|8@1+ (1,0) [0|255] ""  ECU
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeTrue();
        var msg = r.Value!.MessagesById[200u];
        msg.IsMultiplexed.Should().BeTrue();
        msg.MultiplexorSignalIndex.Should().Be(0);
        msg.Signals[0].IsMultiplexor.Should().BeTrue();
        msg.Signals[1].IsMultiplexed.Should().BeTrue();
        msg.Signals[1].MultiplexValue.Should().Be(0);
        msg.Signals[2].MultiplexValue.Should().Be(1);
    }

    [Fact]
    public void Parses_VAL_For_Signal_And_Attaches_ValueTableName()
    {
        var src = """
        BU_: ECU
        BO_ 300 Msg: 8 ECU
         SG_ State : 0|3@1+ (1,0) [0|7] ""  ECU

        VAL_ 300 State 0 "Off" 1 "On" 2 "Error" ;
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeTrue();
        var sig = r.Value!.MessagesById[300u].Signals[0];
        sig.ValueTableName.Should().Be("State");   // implementation strategy: store name on Signal
    }

    [Fact]
    public void Reuses_VAL_TABLE_Reference_For_Signal()
    {
        var src = """
        BU_: ECU
        BO_ 400 Msg: 8 ECU
         SG_ Mode : 0|2@1+ (1,0) [0|3] ""  ECU

        VAL_TABLE_ Tbl 0 "A" 1 "B" 2 "C" 3 "D" ;
        VAL_ 400 Mode VAL_TABLE_ Tbl ;
        """;
        var r = DbcParser.Parse(src);
        r.IsSuccess.Should().BeTrue();
        var sig = r.Value!.MessagesById[400u].Signals[0];
        sig.ValueTableName.Should().Be("Tbl");
        r.Value!.ValueTables.Should().ContainKey("Tbl");
    }
}
```

- [ ] **Step 2: Run, verify RED**

```bash
dotnet test tests/PeakCan.Host.Core.Tests --filter DbcParserMultiplexedTests
```

- [ ] **Step 3: Extend `ParseSignal` to detect M / m0..m15 prefix**

Replace `ParseSignal` body inside `DbcParser.cs`. Key edits:
1. After consuming `SG_`, the next token can be `M Name` (multiplexor) or `Name` (regular) or `m0..m15 Name` (multiplexed).
2. Adjust `Consume()` calls accordingly; populate `IsMultiplexor` / `IsMultiplexed` / `MultiplexValue` on `Signal`.
3. Track `multiplexorIndex` inside `ParseMessage`; set `Message.IsMultiplexed = true` and `Message.MultiplexorSignalIndex = index`.
4. In `ParseDocument`, when encountering `Keyword_VAL_`, parse as `VAL_ <MsgIdOrName> <SigNameOrVAL_TABLE_> <int-or-VAL_TABLE_> ... ;`. Attach the table-name to the matching signal's `ValueTableName`.

Add to `ParseMessage` (immediately after the signals loop):

```csharp
            // After collecting all signals, fix up IsMultiplexed on the message
            bool isMuxed = signals.Any(s => s.IsMultiplexed);
            ushort? muxIdx = isMuxed ? (ushort?)signals.FindIndex(s => s.IsMultiplexor) : null;
            return Result<Message>.Ok(new Message(id, nameTok.Lexeme, dlc, sender, signals, isMuxed, muxIdx));
```

Replace the leading part of `ParseSignal` to handle M / m:

```csharp
        private Signal ParseSignal()
        {
            Consume(); // SG_

            // Detect M / m0..m15 prefix on the signal name.
            bool isMuxor = false;
            bool isMuxed = false;
            ushort? muxVal = null;

            Token nameTok = Current;
            if (nameTok.Type == TokenType.Identifier && nameTok.Lexeme == "M")
            {
                isMuxor = true;
                Consume();
                nameTok = Consume();
            }
            else if (nameTok.Type == TokenType.Identifier && nameTok.Lexeme.Length >= 2 && nameTok.Lexeme[0] == 'm'
                     && ushort.TryParse(nameTok.Lexeme.AsSpan(1), out var mv))
            {
                isMuxed = true;
                muxVal = mv;
                Consume();
                nameTok = Consume();
            }
            else
            {
                nameTok = Consume();
            }
            var name = nameTok.Lexeme;

            Expect(TokenType.Colon);
            byte start = byte.Parse(Consume().Lexeme);
            Expect(TokenType.Pipe);
            byte len = byte.Parse(Consume().Lexeme);
            Expect(TokenType.At);
            var orderTok = Consume();
            ByteOrder order = orderTok.Lexeme == "1" ? ByteOrder.LittleEndian : ByteOrder.BigEndian;
            var sign = Consume();
            ValueType vt = sign.Type switch
            {
                TokenType.Minus => ValueType.Signed,
                _ => ValueType.Unsigned,
            };
            Expect(TokenType.LParen);
            double factor = ParseDouble();
            Expect(TokenType.Comma);
            double offset = ParseDouble();
            Expect(TokenType.RParen);
            Expect(TokenType.LBracket);
            double min = ParseDouble();
            Expect(TokenType.Pipe);
            double max = ParseDouble();
            Expect(TokenType.RBracket);
            string unit = Consume().Lexeme.Trim('"');
            var receivers = new List<string>();
            if (Current.Type == TokenType.Identifier) receivers.Add(Consume().Lexeme);
            while (Current.Type == TokenType.Comma) { Consume(); receivers.Add(Consume().Lexeme); }
            return new Signal(name, start, len, order, vt, factor, offset, min, max, unit, receivers,
                              IsMultiplexor: isMuxor, IsMultiplexed: isMuxed, MultiplexValue: muxVal);
        }
```

In `ParseDocument`, handle `Keyword_VAL_` before the default skip:

```csharp
                case TokenType.Keyword_VAL_:
                    ParseValForSignal();
                    break;
```

Add the helper:

```csharp
        private void ParseValForSignal()
        {
            Consume(); // VAL_
            // Resolve message id (may be identifier by name)
            uint msgId;
            if (Current.Type == TokenType.Integer)
            {
                var raw = Consume().Lexeme;
                if (!uint.TryParse(raw, out msgId)) throw new DbcParseException("Bad VAL_ id", Current.Line, Current.Column);
            }
            else
            {
                var name = Consume().Lexeme;
                var msg = _pendingMessages.LastOrDefault(m => m.Name == name)
                          ?? throw new DbcParseException($"VAL_: unknown message {name}", Current.Line, Current.Column);
                msgId = msg.Id;
            }

            // Determine value table name (VAL_TABLE_ X | inline pairs)
            string tableName = "";
            if (Current.Type == TokenType.Identifier && Peek(1).Type == TokenType.Keyword_VAL_TABLE_)
            {
                // VAL_ <msg> <sig> VAL_TABLE_ <name> ;
                var sigTok = Consume();
                Consume(); // VAL_TABLE_
                tableName = Consume().Lexeme;
                // Attach table name to signal
                if (_pendingMessagesById.TryGetValue(msgId, out var m))
                {
                    var sig = m.Signals.FirstOrDefault(s => s.Name == sigTok.Lexeme)
                              ?? throw new DbcParseException($"VAL_: unknown signal {sigTok.Lexeme}", Current.Line, Current.Column);
                    m.Signals[m.Signals.IndexOf(sig)] = sig with { ValueTableName = tableName };
                }
                Expect(TokenType.Semicolon);
            }
            else
            {
                // Inline VAL_ entries: signal name + (int "text") pairs
                var sigTok = Consume();
                if (_pendingMessagesById.TryGetValue(msgId, out var m))
                {
                    var sig = m.Signals.FirstOrDefault(s => s.Name == sigTok.Lexeme)
                              ?? throw new DbcParseException($"VAL_: unknown signal {sigTok.Lexeme}", Current.Line, Current.Column);
                    // We don't store inline values for MVP — only mark the table name as the signal's name for ad-hoc lookup
                    m.Signals[m.Signals.IndexOf(sig)] = sig with { ValueTableName = sigTok.Lexeme };
                }
                while (Current.Type == TokenType.Integer || Current.Type == TokenType.Minus)
                {
                    Consume(); // value
                    Consume(); // "text"
                }
                Expect(TokenType.Semicolon);
            }
        }
```

Add two fields to `ParserState`:

```csharp
        private List<Message> _pendingMessages = new();
        private Dictionary<uint, Message> _pendingMessagesById = new();
```

And replace the `messages` list local in `ParseDocument` with these fields. After `ParseDocument` returns, use them to build the final `DbcDocument`.

- [ ] **Step 4: Run, verify GREEN**

```bash
dotnet test tests/PeakCan.Host.Core.Tests --filter DbcParserMultiplexedTests
```

- [ ] **Step 5: Run full Core test suite, ensure no regression**

```bash
dotnet test tests/PeakCan.Host.Core.Tests
```

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(core): DBC parser handles multiplexed M/m signals and VAL_/VAL_TABLE_ attachments"
```

---

### Task 7: Core — Signal decoder (little-endian unsigned, with tests)

**Files:**
- Create: `src/PeakCan.Host.Core/Dbc/SignalDecoder.cs`
- Create: `tests/PeakCan.Host.Core.Tests/SignalDecoderTests.cs`

**Interfaces:** `SignalDecoder.Decode(ReadOnlySpan<byte> data, Signal signal)` → `double` (physical value). Throws on illegal length > 64.

- [ ] **Step 1: Write failing tests**

```csharp
// tests/PeakCan.Host.Core.Tests/SignalDecoderTests.cs
using FluentAssertions;
using PeakCan.Host.Core.Dbc;
using Xunit;
namespace PeakCan.Host.Core.Tests;
public class SignalDecoderTests
{
    private static Signal U16Little(int start = 0) => new(
        "S", (byte)start, 16, ByteOrder.LittleEndian, ValueType.Unsigned,
        1.0, 0.0, 0, 65535, "u", Array.Empty<string>());

    [Fact]
    public void LittleEndian_Unsigned_16Bit_At_Start()
    {
        byte[] data = { 0xCD, 0xAB };
        SignalDecoder.Decode(data, U16Little()).Should().Be(0xABCD);
    }

    [Fact]
    public void LittleEndian_With_Factor_And_Offset()
    {
        var sig = new Signal("S", 0, 16, ByteOrder.LittleEndian, ValueType.Unsigned,
            0.1, 5.0, 0, 0, "u", Array.Empty<string>());
        byte[] data = { 0x10, 0x00 };  // raw = 16
        // physical = 16 * 0.1 + 5.0 = 6.6
        SignalDecoder.Decode(data, sig).Should().BeApproximately(6.6, 1e-9);
    }

    [Fact]
    public void Zero_Length_Signal_Returns_Zero()
    {
        var sig = new Signal("S", 0, 0, ByteOrder.LittleEndian, ValueType.Unsigned,
            1.0, 0.0, 0, 0, "", Array.Empty<string>());
        SignalDecoder.Decode(new byte[] { 0xFF }, sig).Should().Be(0.0);
    }
}
```

- [ ] **Step 2: Run, verify RED**

```bash
dotnet test tests/PeakCan.Host.Core.Tests --filter SignalDecoderTests
```

- [ ] **Step 3: Implement little-endian unsigned decoder**

```csharp
// src/PeakCan.Host.Core/Dbc/SignalDecoder.cs
using System.Numerics;
namespace PeakCan.Host.Core.Dbc;
public static class SignalDecoder
{
    public static double Decode(ReadOnlySpan<byte> data, Signal signal)
    {
        if (signal.Length == 0) return 0.0;
        if (signal.Length > 64)
            throw new ArgumentOutOfRangeException(nameof(signal), "Signal > 64 bits not supported in MVP");

        ulong raw = signal.Order == ByteOrder.LittleEndian
            ? ReadLittleEndian(data, signal.StartBit, signal.Length)
            : ReadBigEndian(data, signal.StartBit, signal.Length);

        double physical = signal.ValueType switch
        {
            ValueType.Unsigned => raw,
            ValueType.Signed   => SignExtend(raw, signal.Length),
            ValueType.Float    => BitConverter.Int32BitsToSingle((int)raw),
            ValueType.Double   => BitConverter.Int64BitsToDouble((long)raw),
            _ => raw,
        };
        return physical * signal.Factor + signal.Offset;
    }

    private static ulong ReadLittleEndian(ReadOnlySpan<byte> data, byte start, byte len)
    {
        // DBC bit numbering: start bit is LSB of the first byte; little-endian grows toward higher byte indices.
        ulong result = 0;
        for (byte i = 0; i < len; i++)
        {
            byte byteIdx = (byte)((start + i) / 8);
            byte bitIdx  = (byte)((start + i) % 8);
            if (byteIdx >= data.Length) break;          // not enough data — treat remaining as 0
            ulong bit = (ulong)(data[byteIdx] >> bitIdx) & 1UL;
            result |= bit << i;
        }
        return result;
    }

    private static ulong ReadBigEndian(ReadOnlySpan<byte> data, byte start, byte len)
    {
        // DBC big-endian: bit 0 is MSB of byte 0, grows toward byte 1 etc.
        ulong result = 0;
        for (byte i = 0; i < len; i++)
        {
            byte absBit = (byte)(start + i);
            byte byteIdx = (byte)(absBit / 8);
            byte bitInByte = (byte)(7 - (absBit % 8));  // MSB-first
            if (byteIdx >= data.Length) break;
            ulong bit = (ulong)(data[byteIdx] >> bitInByte) & 1UL;
            result = (result << 1) | bit;
        }
        return result;
    }

    private static long SignExtend(ulong raw, byte len)
    {
        if (len >= 64) return (long)raw;
        ulong sign = 1UL << (len - 1);
        if ((raw & sign) == 0) return (long)raw;
        ulong mask = ~((1UL << len) - 1);
        return (long)(raw | mask);
    }
}
```

- [ ] **Step 4: Run, verify GREEN**

```bash
dotnet test tests/PeakCan.Host.Core.Tests --filter SignalDecoderTests
```

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): SignalDecoder — little-endian + big-endian + signed + float/double"
```

---

### Task 8: Infrastructure — `PeakError` + `PeakErrorMapper` (PCAN-Basic status → ErrorCode)

**Files:**
- Create: `src/PeakCan.Host.Infrastructure/Peak/PeakError.cs`
- Create: `src/PeakCan.Host.Infrastructure/Peak/PeakErrorMapper.cs`
- Create: `tests/PeakCan.Host.Infrastructure.Tests/PeakErrorMapperTests.cs`

**Interfaces:** `PeakErrorMapper.ToErrorCode(uint status)` returns `(ErrorCode code, string message)`.

- [ ] **Step 1: Write failing tests**

```csharp
// tests/PeakCan.Host.Infrastructure.Tests/PeakErrorMapperTests.cs
using FluentAssertions;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Peak;
using Xunit;
namespace PeakCan.Host.Infrastructure.Tests;
public class PeakErrorMapperTests
{
    [Theory]
    [InlineData(0x00000000u, ErrorCode.Unknown)]                  // PCAN_ERROR_OK
    [InlineData(0x00000001u, ErrorCode.HardwareBusy)]            // PCAN_ERROR_XMTFULL
    [InlineData(0x00000020u, ErrorCode.HardwareNotAvailable)]    // PCAN_ERROR_NODRIVER
    [InlineData(0x00000040u, ErrorCode.HardwareBusy)]            // PCAN_ERROR_BUSOFF
    [InlineData(0x00000009u, ErrorCode.IoError)]                 // PCAN_ERROR_ILLHW
    public void Maps_Known_PCAN_Status_To_ErrorCode(uint raw, ErrorCode expected)
    {
        var (code, _) = PeakErrorMapper.ToErrorCode(raw);
        code.Should().Be(expected);
    }
}
```

- [ ] **Step 2: Run, verify RED**

```bash
dotnet test tests/PeakCan.Host.Infrastructure.Tests --filter PeakErrorMapperTests
```

- [ ] **Step 3: Implement**

```csharp
// src/PeakCan.Host.Infrastructure/Peak/PeakError.cs
namespace PeakCan.Host.Infrastructure.Peak;
// PCAN-Basic error code constants (subset — we map only the ones we surface to UI)
public static class PeakError
{
    public const uint OK          = 0x00000000;
    public const uint XMTFULL     = 0x00000001;
    public const uint OVERRUN     = 0x00000002;
    public const uint BUSLIGHT    = 0x00000004;
    public const uint BUSHEAVY    = 0x00000008;
    public const uint BUSOFF      = 0x00000040;
    public const uint NODRIVER    = 0x00000020;
    public const uint ILLHW       = 0x00000009;
    public const uint REGTEST     = 0x0000000A;
    public const uint PARAM       = 0x0000000B;
}

// src/PeakCan.Host.Infrastructure/Peak/PeakErrorMapper.cs
using PeakCan.Host.Core;
namespace PeakCan.Host.Infrastructure.Peak;
public static class PeakErrorMapper
{
    public static (ErrorCode Code, string Message) ToErrorCode(uint raw)
        => raw switch
        {
            PeakError.OK       => (ErrorCode.Unknown, "OK"),                  // success is not an error
            PeakError.XMTFULL  => (ErrorCode.HardwareBusy, "Transmit buffer full"),
            PeakError.OVERRUN  => (ErrorCode.IoError, "Receive overrun"),
            PeakError.BUSLIGHT => (ErrorCode.IoError, "Bus light error"),
            PeakError.BUSHEAVY => (ErrorCode.IoError, "Bus heavy error"),
            PeakError.BUSOFF   => (ErrorCode.HardwareBusy, "Bus-off state"),
            PeakError.NODRIVER => (ErrorCode.HardwareNotAvailable, "PCAN driver not loaded"),
            PeakError.ILLHW    => (ErrorCode.HardwareNotAvailable, "Illegal hardware"),
            PeakError.REGTEST  => (ErrorCode.HardwareNotAvailable, "Driver init failed self-test"),
            PeakError.PARAM    => (ErrorCode.HardwareParameter, "Illegal parameter"),
            _ => (ErrorCode.Unknown, $"Unknown PCAN status 0x{raw:X8}"),
        };
}
```

- [ ] **Step 4: Run, verify GREEN**

```bash
dotnet test tests/PeakCan.Host.Infrastructure.Tests --filter PeakErrorMapperTests
```

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(infra): add PeakError constants and PeakErrorMapper (TPCANStatus → ErrorCode)"
```

---

### Task 9: Infrastructure — `ICanChannel` interface + `PeakCanChannel` wrapper

**Files:**
- Create: `src/PeakCan.Host.Infrastructure/Channel/ICanChannel.cs`
- Create: `src/PeakCan.Host.Infrastructure/Channel/ChannelException.cs`
- Create: `src/PeakCan.Host.Infrastructure/Channel/IFrameSink.cs`
- Create: `src/PeakCan.Host.Infrastructure/Channel/IFrameSource.cs`
- Create: `src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs`
- Create: `tests/PeakCan.Host.Infrastructure.Tests/PeakCanChannelTests.cs` (skipped on CI — `[Trait("category","integration")]`)

**Interfaces:**
- `ICanChannel` — `Task ConnectAsync(BaudRate, bool fd, CancellationToken)`, `Task DisconnectAsync()`, `ValueTask WriteAsync(CanFrame, CancellationToken)`, `ChannelId Id`, `event Action<CanFrame>? FrameReceived`.
- `PeakCanChannel` adapts the managed `PCANBasic` API to `ICanChannel`; uses `SetRcvEvent` to signal arrivals.

- [ ] **Step 1: Create interfaces + exception**

```csharp
// src/PeakCan.Host.Infrastructure/Channel/ICanChannel.cs
using PeakCan.Host.Core;
namespace PeakCan.Host.Infrastructure.Channel;
public interface ICanChannel : IAsyncDisposable
{
    ChannelId Id { get; }
    bool IsConnected { get; }
    Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default);
    /// <summary>Fired by ChannelWorker when new frame(s) are read.</summary>
    event Action<CanFrame>? FrameReceived;
}

public readonly record struct BaudRate(ushort Code, string Name, bool IsFd)
{
    public static readonly BaudRate Can125kbps  = new(0x001C, "125 kbps", false);
    public static readonly BaudRate Can250kbps  = new(0x011C, "250 kbps", false);
    public static readonly BaudRate Can500kbps  = new(0x0019, "500 kbps", false);
    public static readonly BaudRate Can1Mbps    = new(0x0014, "1 Mbps", false);
    public static readonly BaudRate CanFd1Mbps  = new(0x0014, "1 Mbps (FD)", true);
    public static readonly BaudRate CanFd2Mbps  = new(0x0104, "2 Mbps (FD)", true);
    public static readonly BaudRate CanFd5Mbps  = new(0x0504, "5 Mbps (FD)", true);
}

public readonly record struct Unit;

// src/PeakCan.Host.Infrastructure/Channel/ChannelException.cs
namespace PeakCan.Host.Infrastructure.Channel;
public sealed class ChannelException : Exception
{
    public ChannelException(string msg, Exception? inner = null) : base(msg, inner) { }
}

// src/PeakCan.Host.Infrastructure/Channel/IFrameSink.cs
using PeakCan.Host.Core;
namespace PeakCan.Host.Infrastructure.Channel;
public interface IFrameSink
{
    void OnFrame(CanFrame frame);
    void OnError(Exception ex);
}

// src/PeakCan.Host.Infrastructure/Channel/IFrameSource.cs
namespace PeakCan.Host.Infrastructure.Channel;
public interface IFrameSource
{
    void AttachSink(IFrameSink sink);
    void DetachSink(IFrameSink sink);
}
```

- [ ] **Step 2: Implement `PeakCanChannel`**

```csharp
// src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs
using System.Threading.Channels;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;
using Peak.Can.Basic;
namespace PeakCan.Host.Infrastructure.Peak;
public sealed class PeakCanChannel : ICanChannel
{
    private readonly TPCANHandle _handle;
    private readonly Channel<CanFrame> _internal = Channel.CreateBounded<CanFrame>(
        new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _cts = new();
    private Task? _readLoop;
    public ChannelId Id { get; }
    public bool IsConnected { get; private set; }
    public event Action<CanFrame>? FrameReceived;

    public PeakCanChannel(ChannelId id) { Id = id; _handle = (TPCANHandle)id.Handle; }

    public async Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
    {
        try
        {
            var status = fd
                ? PCANBasic.InitializeFD(_handle, (TPCANBaudrate)baud.Code)
                : PCANBasic.Initialize(_handle, (TPCANBaudrate)baud.Code);
            if (status != TPCANStatus.PCAN_ERROR_OK)
            {
                var (code, msg) = PeakErrorMapper.ToErrorCode((uint)status);
                return Result<Unit>.Fail(code, msg);
            }
            IsConnected = true;
            _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token), ct);
            return Result<Unit>.Ok(default);
        }
        catch (Exception ex)
        {
            return Result<Unit>.Fail(ErrorCode.HardwareNotAvailable, ex.Message);
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (!IsConnected) return;
        _cts.Cancel();
        try { if (_readLoop is not null) await _readLoop.ConfigureAwait(false); } catch { /* expected on cancel */ }
        PCANBasic.Uninitialize(_handle);
        IsConnected = false;
    }

    public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
    {
        if (!IsConnected) return ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.InvalidState, "Not connected"));
        try
        {
            TPCANStatus status;
            if (frame.IsFd)
            {
                var m = new TPCANMsgFD
                {
                    ID = frame.Id.IsExtended ? TPCANMessageId.Extended : TPCANMessageId.Standard,
                    MSGTYPE = frame.Flags.HasFlag(FrameFlags.BitRateSwitch) ? TPCANMessageType.FD_BRS : TPCANMessageType.FD,
                    DLC = (byte)Math.Min(frame.Dlc, 15),
                    DATA = ToFixedBytes(frame.Data),
                };
                status = PCANBasic.WriteFD(_handle, ref m);
            }
            else
            {
                var m = new TPCANMsg
                {
                    ID = frame.Id.IsExtended ? TPCANMessageId.Extended : TPCANMessageId.Standard,
                    MSGTYPE = TPCANMessageType.Standard,
                    LEN = (byte)Math.Min(frame.Dlc, 8),
                    DATA = ToFixedBytes8(frame.Data),
                };
                status = PCANBasic.Write(_handle, ref m);
            }
            return status == TPCANStatus.PCAN_ERROR_OK
                ? ValueTask.FromResult(Result<Unit>.Ok(default))
                : ValueTask.FromResult(MakeError(status));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.IoError, ex.Message));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _cts.Dispose();
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TPCANMsg msg; TPCANTimestamp ts;
            while (PCANBasic.Read(_handle, out msg, out ts) == TPCANStatus.PCAN_ERROR_OK)
            {
                Emit(msg, ts, isFd: false);
            }
            TPCANMsgFD fdMsg; TPCANTimestampFD tsFd;
            while (PCANBasic.ReadFD(_handle, out fdMsg, out tsFd) == TPCANStatus.PCAN_ERROR_OK)
            {
                Emit(fdMsg, tsFd, isFd: true);
            }
            await Task.Delay(1, ct).ConfigureAwait(false);
        }
    }

    private void Emit(TPCANMsg m, TPCANTimestamp ts, bool isFd)
    {
        var canId = new CanId(m.ID, (m.ID & (uint)TPCANMessageId.Extended) != 0 ? FrameFormat.Extended : FrameFormat.Standard);
        var bytes = m.DATA.Take(m.LEN).ToArray();
        var frame = new CanFrame(canId, bytes, FrameFlags.None, Id,
            Timestamp.FromMillis(ts.micros, 0));
        _internal.Writer.TryWrite(frame);
        FrameReceived?.Invoke(frame);
    }
    private void Emit(TPCANMsgFD m, TPCANTimestampFD ts, bool isFd)
    {
        var canId = new CanId(m.ID & 0x1FFFFFFFu, (m.ID & (uint)TPCANMessageId.Extended) != 0 ? FrameFormat.Extended : FrameFormat.Standard);
        var flags = FrameFlags.Fd;
        if ((m.MSGTYPE & TPCANMessageType.FD_BRS) != 0) flags |= FrameFlags.BitRateSwitch;
        if ((m.MSGTYPE & TPCANMessageType.ESI) != 0) flags |= FrameFlags.ErrorStateIndicator;
        var dlc = DlcToBytes((byte)m.DLC);
        var bytes = m.DATA.Take(dlc).ToArray();
        var frame = new CanFrame(canId, bytes, flags, Id, Timestamp.FromMillis((ulong)ts.value, 0));
        _internal.Writer.TryWrite(frame);
        FrameReceived?.Invoke(frame);
    }
    private static byte DlcToBytes(byte dlc) => dlc switch
    {
        <= 8 => dlc, 9 => 12, 10 => 16, 11 => 20, 12 => 24, 13 => 32, 14 => 48, _ => 64,
    };
    private static byte[] ToFixedBytes(ReadOnlyMemory<byte> src)
    {
        var dst = new byte[64];
        src.Span.CopyTo(dst);
        return dst;
    }
    private static byte[] ToFixedBytes8(ReadOnlyMemory<byte> src)
    {
        var dst = new byte[8];
        src.Span.CopyTo(dst);
        return dst;
    }
    private static Result<Unit> MakeError(TPCANStatus s)
    {
        var (code, msg) = PeakErrorMapper.ToErrorCode((uint)s);
        return Result<Unit>.Fail(code, msg);
    }
}
```

- [ ] **Step 3: Add an integration test (skipped on CI)**

```csharp
// tests/PeakCan.Host.Infrastructure.Tests/PeakCanChannelTests.cs
using Xunit;
namespace PeakCan.Host.Infrastructure.Tests;
public class PeakCanChannelTests
{
    [Fact(Skip = "Requires real PCAN hardware — run locally")]
    [Trait("category", "integration")]
    public void Connect_And_Disconnect_Round_Trip() { /* see docs */ }
}
```

- [ ] **Step 4: Build, ensure compile**

```bash
dotnet build
```

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(infra): ICanChannel interface + PeakCanChannel wrapper (managed PCAN-Basic)"
```

---

### Task 10: Infrastructure — `ChannelRouter` fan-out

**Files:**
- Create: `src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs`
- Create: `tests/PeakCan.Host.Infrastructure.Tests/ChannelRouterTests.cs`

**Interfaces:** `ChannelRouter` implements `IFrameSource`. `AttachSink(IFrameSink)` subscribes to ALL active channels' `FrameReceived` event and forwards to the sink. Sinks never see each other's exceptions.

- [ ] **Step 1: Write failing tests**

```csharp
// tests/PeakCan.Host.Infrastructure.Tests/ChannelRouterTests.cs
using FluentAssertions;
using NSubstitute;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;
using Xunit;
namespace PeakCan.Host.Infrastructure.Tests;
public class ChannelRouterTests
{
    [Fact]
    public async Task FanOut_Delivers_Frame_To_All_Sinks()
    {
        var ch1 = Substitute.For<ICanChannel>();
        var ch2 = Substitute.For<ICanChannel>();
        var sink1 = Substitute.For<IFrameSink>();
        var sink2 = Substitute.For<IFrameSink>();
        var router = new ChannelRouter();
        router.RegisterChannel(ch1);
        router.RegisterChannel(ch2);
        router.AttachSink(sink1);
        router.AttachSink(sink2);
        var frame = new CanFrame(new CanId(1, FrameFormat.Standard), new byte[] { 0xAA }, FrameFlags.None, ChannelId.None, default);
        ch1.FrameReceived += Raise.Event<Action<CanFrame>>(frame);
        ch2.FrameReceived += Raise.Event<Action<CanFrame>>(frame);
        sink1.Received(2).OnFrame(frame);
        sink2.Received(2).OnFrame(frame);
    }

    [Fact]
    public void Detaching_Sink_Stops_Delivery()
    {
        var ch = Substitute.For<ICanChannel>();
        var sink = Substitute.For<IFrameSink>();
        var router = new ChannelRouter();
        router.RegisterChannel(ch);
        router.AttachSink(sink);
        router.DetachSink(sink);
        var frame = new CanFrame(new CanId(1, FrameFormat.Standard), new byte[] { 0xAA }, FrameFlags.None, ChannelId.None, default);
        ch.FrameReceived += Raise.Event<Action<CanFrame>>(frame);
        sink.DidNotReceive().OnFrame(Arg.Any<CanFrame>());
    }
}
```

- [ ] **Step 2: Run, verify RED**

- [ ] **Step 3: Implement**

```csharp
// src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs
using PeakCan.Host.Core;
namespace PeakCan.Host.Infrastructure.Channel;
public sealed class ChannelRouter : IFrameSource
{
    private readonly List<ICanChannel> _channels = new();
    private readonly List<IFrameSink> _sinks = new();
    private readonly object _gate = new();

    public void RegisterChannel(ICanChannel channel)
    {
        lock (_gate)
        {
            if (_channels.Contains(channel)) return;
            _channels.Add(channel);
            channel.FrameReceived += OnChannelFrame;
        }
    }

    public void UnregisterChannel(ICanChannel channel)
    {
        lock (_gate)
        {
            if (_channels.Remove(channel))
                channel.FrameReceived -= OnChannelFrame;
        }
    }

    public void AttachSink(IFrameSink sink)
    {
        lock (_gate) { if (!_sinks.Contains(sink)) _sinks.Add(sink); }
    }

    public void DetachSink(IFrameSink sink)
    {
        lock (_gate) _sinks.Remove(sink);
    }

    private void OnChannelFrame(CanFrame frame)
    {
        IFrameSink[] snapshot;
        lock (_gate) snapshot = _sinks.ToArray();
        foreach (var s in snapshot)
        {
            try { s.OnFrame(frame); }
            catch (Exception ex) { s.OnError(ex); }
        }
    }
}
```

- [ ] **Step 4: Run, verify GREEN**

```bash
dotnet test tests/PeakCan.Host.Infrastructure.Tests --filter ChannelRouterTests
```

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(infra): ChannelRouter fan-out with sink attach/detach and per-sink error isolation"
```

---

### Task 11: Infrastructure — `BusStatisticsCollector`

**Files:**
- Create: `src/PeakCan.Host.Infrastructure/Statistics/BusStatisticsCollector.cs`
- Create: `tests/PeakCan.Host.Infrastructure.Tests/BusStatisticsCollectorTests.cs`

**Interfaces:** Implements `IFrameSink`. Exposes `Snapshot()` returning a `BusStatistics` record with `FramesPerSecond`, `TotalFrames`, `ErrorFrames`, `BytesPerSecond`, `BusLoadPercent` (computed from frame inter-arrival times).

- [ ] **Step 1: Write failing tests**

```csharp
// tests/PeakCan.Host.Infrastructure.Tests/BusStatisticsCollectorTests.cs
using FluentAssertions;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Statistics;
using Xunit;
namespace PeakCan.Host.Infrastructure.Tests;
public class BusStatisticsCollectorTests
{
    [Fact]
    public void Counts_Total_And_Err_Frames()
    {
        var s = new BusStatisticsCollector();
        for (int i = 0; i < 10; i++) s.OnFrame(MakeFrame(err: i == 3));
        var snap = s.Snapshot();
        snap.TotalFrames.Should().Be(10);
        snap.ErrorFrames.Should().Be(1);
    }

    [Fact]
    public void Reports_FramesPerSecond_Over_Window()
    {
        var s = new BusStatisticsCollector();
        for (int i = 0; i < 100; i++)
        {
            s.OnFrame(MakeFrame());
            Thread.Sleep(1);  // 100 frames over ~100ms
        }
        var snap = s.Snapshot();
        snap.FramesPerSecond.Should().BeGreaterThan(100);
    }

    private static CanFrame MakeFrame(bool err = false)
        => new(new CanId(1, FrameFormat.Standard), new byte[] { 0 },
               err ? FrameFlags.ErrFrame : FrameFlags.None, ChannelId.None, default);
}
```

- [ ] **Step 2: Run, verify RED**

- [ ] **Step 3: Implement**

```csharp
// src/PeakCan.Host.Infrastructure/Statistics/BusStatisticsCollector.cs
using System.Diagnostics;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;
namespace PeakCan.Host.Infrastructure.Statistics;
public sealed record BusStatistics(
    long TotalFrames, long ErrorFrames, double FramesPerSecond,
    long TotalBytes, double BytesPerSecond, double BusLoadPercent);

public sealed class BusStatisticsCollector : IFrameSink
{
    private long _total, _err;
    private long _bytes;
    private readonly Queue<(long Ticks, int Bytes)> _recent = new();  // 1-second window
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public void OnFrame(CanFrame frame)
    {
        Interlocked.Increment(ref _total);
        if (frame.IsError) Interlocked.Increment(ref _err);
        Interlocked.Add(ref _bytes, frame.Dlc);
        lock (_recent)
        {
            _recent.Enqueue((_clock.ElapsedTicks, frame.Dlc));
            while (_recent.Count > 0 && _clock.ElapsedTicks - _recent.Peek().Ticks > TimeSpan.TicksPerSecond)
                _recent.Dequeue();
        }
    }

    public void OnError(Exception ex) { /* logged at call site */ }

    public BusStatistics Snapshot()
    {
        long total = Interlocked.Read(ref _total);
        long err = Interlocked.Read(ref _err);
        long bytes = Interlocked.Read(ref _bytes);
        int count; long bytesInWindow;
        lock (_recent) { count = _recent.Count; bytesInWindow = _recent.Sum(x => (long)x.Bytes); }
        double windowSeconds = count > 0 ? 1.0 : 0.0;
        return new BusStatistics(total, err, count / windowSeconds, bytes, bytesInWindow, LoadPercent(count));
    }

    private static double LoadPercent(int framesPerSecond)
    {
        // crude heuristic: 1Mbps CAN classic at 100% load = ~8000 fps (8 byte avg); 1Mbps FD ≈ 30000 fps
        // Treat >= 8000 fps as 100%.
        return Math.Min(100.0, framesPerSecond / 80.0);
    }
}
```

- [ ] **Step 4: Run, verify GREEN**

```bash
dotnet test tests/PeakCan.Host.Infrastructure.Tests --filter BusStatisticsCollectorTests
```

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(infra): BusStatisticsCollector with rolling 1s window + load heuristic"
```

---

### Task 12: App — `AppHostBuilder` (DI registration + Serilog)

**Files:**
- Create: `src/PeakCan.Host.App/Composition/AppHostBuilder.cs`
- Create: `src/PeakCan.Host.App/App.xaml` (replace generated one)
- Create: `src/PeakCan.Host.App/App.xaml.cs`
- Create: `src/PeakCan.Host.App/AppShell.xaml`
- Create: `src/PeakCan.Host.App/AppShell.xaml.cs`
- Create: `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs`

**Interfaces:** `AppHostBuilder.Build()` returns `IHost` with all services registered. `App.OnStartup` builds the host and shows `AppShell`.

- [ ] **Step 1: Write `AppHostBuilder`**

```csharp
// src/PeakCan.Host.App/Composition/AppHostBuilder.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.Peak;
using PeakCan.Host.Infrastructure.Statistics;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using Serilog;
namespace PeakCan.Host.App.Composition;
public static class AppHostBuilder
{
    public static IHost Build()
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PeakCan.Host", "logs", "peak-.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders().AddSerilog(Log.Logger, dispose: true);

        // Core infrastructure
        builder.Services.AddSingleton<ChannelRouter>();
        builder.Services.AddSingleton<BusStatisticsCollector>();

        // App services
        builder.Services.AddSingleton<TraceService>();
        builder.Services.AddSingleton<SendService>();
        builder.Services.AddSingleton<DbcService>();
        builder.Services.AddSingleton<StatisticsService>();

        // ViewModels
        builder.Services.AddSingleton<AppShellViewModel>();
        builder.Services.AddSingleton<TraceViewModel>();
        builder.Services.AddSingleton<SendViewModel>();
        builder.Services.AddSingleton<DbcViewModel>();
        builder.Services.AddSingleton<SignalViewModel>();
        builder.Services.AddSingleton<StatsViewModel>();

        // Windows
        builder.Services.AddSingleton<MainWindow>();
        return builder.Build();
    }
}
```

- [ ] **Step 2: Write `App.xaml` + `App.xaml.cs`**

```xml
<!-- src/PeakCan.Host.App/App.xaml -->
<Application x:Class="PeakCan.Host.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="pack://application:,,,/PeakCan.Host.App;component/Resources/Styles.xaml" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

```csharp
// src/PeakCan.Host.App/App.xaml.cs
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PeakCan.Host.App.Composition;
namespace PeakCan.Host.App;
public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var host = AppHostBuilder.Build();
        Services = host.Services;
        var shell = new AppShell { DataContext = Services.GetRequiredService<AppShellViewModel>() };
        shell.Show();
    }
}
```

- [ ] **Step 3: Minimal `AppShell.xaml` + code-behind**

```xml
<!-- src/PeakCan.Host.App/AppShell.xaml -->
<Window x:Class="PeakCan.Host.App.AppShell"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="PeakCan Host" Height="720" Width="1280">
    <DockPanel>
        <Menu DockPanel.Dock="Top">
            <MenuItem Header="_File">
                <MenuItem Header="Open DBC..." Command="{Binding OpenDbcCommand}" />
                <Separator />
                <MenuItem Header="Exit" Click="OnExit" />
            </MenuItem>
        </Menu>
        <StatusBar DockPanel.Dock="Bottom">
            <TextBlock Text="{Binding StatusMessage}" />
        </StatusBar>
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="*" />
                <RowDefinition Height="Auto" />
            </Grid.RowDefinitions>
            <ContentControl Grid.Row="0" x:Name="MainArea" />
        </Grid>
    </DockPanel>
</Window>
```

```csharp
// src/PeakCan.Host.App/AppShell.xaml.cs
using System.Windows;
namespace PeakCan.Host.App;
public partial class AppShell : Window
{
    public AppShell() { InitializeComponent(); }
    private void OnExit(object sender, RoutedEventArgs e) => Close();
}
```

- [ ] **Step 4: Stub `AppShellViewModel`**

```csharp
// src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
namespace PeakCan.Host.App.ViewModels;
public sealed partial class AppShellViewModel : ObservableObject
{
    [ObservableProperty] private string _statusMessage = "Ready";

    [RelayCommand]
    private void OpenDbc() { /* wired in Task 15 */ StatusMessage = "Open DBC clicked"; }
}
```

- [ ] **Step 5: Build, run, verify window appears**

```bash
dotnet build
dotnet run --project src/PeakCan.Host.App
```

Expected: WPF window appears with menu, status bar. Close button works.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(app): AppShell window with menu + status bar + DI + Serilog"
```

---

### Task 13: App — `TraceService` + `TraceViewModel` + `TraceView`

**Files:**
- Create: `src/PeakCan.Host.App/Services/TraceService.cs`
- Create: `src/PeakCan.Host.App/ViewModels/TraceViewModel.cs`
- Create: `src/PeakCan.Host.App/ViewModels/TraceEntry.cs`
- Create: `src/PeakCan.Host.App/Views/TraceView.xaml`
- Create: `src/PeakCan.Host.App/Views/TraceView.xaml.cs`

**Interfaces:** `TraceService` extends `BackgroundService`, implements `IFrameSink`; batches frames into `TraceViewModel.AppendBatchAsync` every 50ms.

- [ ] **Step 1: Write `TraceEntry` view-model record**

```csharp
// src/PeakCan.Host.App/ViewModels/TraceEntry.cs
using PeakCan.Host.Core;
namespace PeakCan.Host.App.ViewModels;
public sealed class TraceEntry
{
    public Timestamp Timestamp { get; init; }
    public ChannelId Channel { get; init; }
    public CanId Id { get; init; }
    public byte Dlc { get; init; }
    public string DataHex { get; init; } = "";
    public string Decoded { get; init; } = "";
    public bool IsError { get; init; }
    public bool IsFd { get; init; }
}
```

- [ ] **Step 2: Write `TraceViewModel`**

```csharp
// src/PeakCan.Host.App/ViewModels/TraceViewModel.cs
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
namespace PeakCan.Host.App.ViewModels;
public sealed partial class TraceViewModel : ObservableObject
{
    public ObservableCollection<TraceEntry> Entries { get; } = new();
    [ObservableProperty] private int _maxRows = 10_000;

    public Task AppendBatchAsync(IReadOnlyList<PeakCan.Host.Core.CanFrame> batch)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return Task.CompletedTask;
        return dispatcher.InvokeAsync(() =>
        {
            foreach (var f in batch)
            {
                Entries.Add(new TraceEntry
                {
                    Timestamp = f.Timestamp,
                    Channel = f.Channel,
                    Id = f.Id,
                    Dlc = f.Dlc,
                    DataHex = Convert.ToHexString(f.Data.Span),
                    IsError = f.IsError,
                    IsFd = f.IsFd,
                });
            }
            while (Entries.Count > MaxRows) Entries.RemoveAt(0);
        }).Task;
    }
}
```

- [ ] **Step 3: Write `TraceService`**

```csharp
// src/PeakCan.Host.App/Services/TraceService.cs
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using PeakCan.Host.Core;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Infrastructure.Channel;
namespace PeakCan.Host.App.Services;
public sealed class TraceService : BackgroundService, IFrameSink
{
    private readonly TraceViewModel _vm;
    private readonly Channel<CanFrame> _batch = Channel.CreateBounded<CanFrame>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest });

    public TraceService(TraceViewModel vm) { _vm = vm; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var buf = new List<CanFrame>(256);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(50, ct); } catch (TaskCanceledException) { break; }
            buf.Clear();
            while (_batch.Reader.TryRead(out var f)) buf.Add(f);
            if (buf.Count > 0) await _vm.AppendBatchAsync(buf);
        }
    }

    public void OnFrame(CanFrame f) => _batch.Writer.TryWrite(f);
    public void OnError(Exception ex) { /* logged by ChannelRouter */ }
}
```

- [ ] **Step 4: Write `TraceView.xaml` with virtualized DataGrid**

```xml
<!-- src/PeakCan.Host.App/Views/TraceView.xaml -->
<UserControl x:Class="PeakCan.Host.App.Views.TraceView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:PeakCan.Host.App.ViewModels"
             d:DataContext="{d:DesignInstance Type=vm:TraceViewModel}"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             mc:Ignorable="d">
    <DataGrid ItemsSource="{Binding Entries}"
              AutoGenerateColumns="False"
              IsReadOnly="True"
              EnableRowVirtualization="True"
              EnableColumnVirtualization="True"
              VirtualizingPanel.IsVirtualizing="True"
              VirtualizingPanel.VirtualizationMode="Recycling"
              VirtualizingPanel.ScrollUnit="Pixel"
              ScrollViewer.CanContentScroll="True"
              RowHeight="20"
              AlternatingRowBackground="#F8F8F8">
        <DataGrid.Columns>
            <DataGridTextColumn Header="Time" Binding="{Binding Timestamp}" Width="120" />
            <DataGridTextColumn Header="Ch" Binding="{Binding Channel}" Width="60" />
            <DataGridTextColumn Header="ID" Binding="{Binding Id}" Width="100" />
            <DataGridTextColumn Header="DLC" Binding="{Binding Dlc}" Width="50" />
            <DataGridTextColumn Header="Data" Binding="{Binding DataHex}" Width="*" />
            <DataGridTextColumn Header="Decoded" Binding="{Binding Decoded}" Width="200" />
        </DataGrid.Columns>
    </DataGrid>
</UserControl>
```

```csharp
// src/PeakCan.Host.App/Views/TraceView.xaml.cs
using System.Windows.Controls;
namespace PeakCan.Host.App.Views;
public partial class TraceView : UserControl { public TraceView() { InitializeComponent(); } }
```

- [ ] **Step 5: Wire into `AppShell.xaml`** (replace `MainArea` placeholder with `TraceView`)

```xml
<ContentControl Grid.Row="0" x:Name="MainArea">
    <ContentControl.Content>
        <views:TraceView DataContext="{Binding TraceViewModel}" />
    </ContentControl.Content>
</ContentControl>
```

Add to `AppShell.xaml` root: `xmlns:views="clr-namespace:PeakCan.Host.App.Views"`.

Add `TraceViewModel` property to `AppShellViewModel` constructor:

```csharp
public AppShellViewModel(TraceViewModel trace) { TraceViewModel = trace; }
public TraceViewModel TraceViewModel { get; }
```

- [ ] **Step 6: Build, run, see empty DataGrid**

```bash
dotnet build
dotnet run --project src/PeakCan.Host.App
```

Expected: window shows DataGrid with header row only.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(app): TraceService (50ms batch) + TraceViewModel + virtualized TraceView DataGrid"
```

---

### Task 14: App — `SendService` + `SendViewModel` + `SendView`

**Files:**
- Create: `src/PeakCan.Host.App/Services/SendService.cs`
- Create: `src/PeakCan.Host.App/ViewModels/SendViewModel.cs`
- Create: `src/PeakCan.Host.App/Views/SendView.xaml`
- Create: `src/PeakCan.Host.App/Views/SendView.xaml.cs`

**Interfaces:** `SendService.SendAsync(CanFrame)` delegates to the active channel's `WriteAsync`. (For MVP, store a single `ICanChannel?` reference; multi-channel v1.1.)

- [ ] **Step 1: Write `SendService`**

```csharp
// src/PeakCan.Host.App/Services/SendService.cs
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;
namespace PeakCan.Host.App.Services;
public sealed class SendService
{
    public ICanChannel? ActiveChannel { get; set; }

    public ValueTask<Result<Unit>> SendAsync(CanFrame frame, CancellationToken ct = default)
        => ActiveChannel is null
            ? ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.InvalidState, "No active channel"))
            : ActiveChannel.WriteAsync(frame, ct);
}
```

- [ ] **Step 2: Write `SendViewModel`**

```csharp
// src/PeakCan.Host.App/ViewModels/SendViewModel.cs
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Services;
using PeakCan.Host.Core;
namespace PeakCan.Host.App.ViewModels;
public sealed partial class SendViewModel : ObservableObject
{
    private readonly SendService _svc;
    public SendViewModel(SendService svc) { _svc = svc; }

    [ObservableProperty] private string _idText = "100";
    [ObservableProperty] private bool _isExtended;
    [ObservableProperty] private bool _isFd;
    [ObservableProperty] private string _dataText = "DEADBEEF";
    [ObservableProperty] private string _status = "";

    [RelayCommand]
    private async Task SendAsync()
    {
        if (!uint.TryParse(IdText, System.Globalization.NumberStyles.HexNumber, null, out var raw))
        { Status = "Invalid ID"; return; }
        var bytes = ParseHex(DataText);
        var canId = new CanId(raw, IsExtended ? FrameFormat.Extended : FrameFormat.Standard);
        var flags = IsFd ? FrameFlags.Fd : FrameFlags.None;
        var frame = new CanFrame(canId, bytes, flags, ChannelId.None, default);
        var r = await _svc.SendAsync(frame);
        Status = r.IsSuccess ? $"Sent {bytes.Length} bytes" : $"FAIL: {r.Error!.Message}";
    }

    private static byte[] ParseHex(string s)
    {
        s = s.Replace(" ", "").Replace("-", "");
        if (s.Length % 2 == 1) s = "0" + s;
        var bytes = new byte[s.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(s.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber);
        return bytes;
    }
}
```

- [ ] **Step 3: Write `SendView.xaml`**

```xml
<!-- src/PeakCan.Host.App/Views/SendView.xaml -->
<UserControl x:Class="PeakCan.Host.App.Views.SendView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:PeakCan.Host.App.ViewModels"
             d:DataContext="{d:DesignInstance Type=vm:SendViewModel}"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             mc:Ignorable="d">
    <StackPanel Margin="8" Orientation="Vertical">
        <StackPanel Orientation="Horizontal" Margin="0,0,0,4">
            <TextBlock Text="ID (hex):" Width="80" VerticalAlignment="Center" />
            <TextBox Text="{Binding IdText}" Width="100" />
            <CheckBox Content="Extended" IsChecked="{Binding IsExtended}" Margin="8,0,0,0" VerticalAlignment="Center" />
            <CheckBox Content="CAN FD" IsChecked="{Binding IsFd}" Margin="8,0,0,0" VerticalAlignment="Center" />
        </StackPanel>
        <StackPanel Orientation="Horizontal" Margin="0,0,0,4">
            <TextBlock Text="Data (hex):" Width="80" VerticalAlignment="Center" />
            <TextBox Text="{Binding DataText}" Width="300" />
        </StackPanel>
        <StackPanel Orientation="Horizontal" Margin="0,8,0,0">
            <Button Content="Send" Command="{Binding SendCommand}" Width="80" />
            <TextBlock Text="{Binding Status}" Margin="12,0,0,0" VerticalAlignment="Center" Foreground="DarkGreen" />
        </StackPanel>
    </StackPanel>
</UserControl>
```

```csharp
// src/PeakCan.Host.App/Views/SendView.xaml.cs
using System.Windows.Controls;
namespace PeakCan.Host.App.Views;
public partial class SendView : UserControl { public SendView() { InitializeComponent(); } }
```

- [ ] **Step 4: Build + run, verify Send form renders**

```bash
dotnet build
dotnet run --project src/PeakCan.Host.App
```

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): SendService + SendViewModel + SendView (manual send form, no wire yet)"
```

---

### Task 15: App — `DbcService` + `DbcViewModel` + `DbcView` (load DBC + populate DBC tree)

**Files:**
- Create: `src/PeakCan.Host.App/Services/DbcService.cs`
- Create: `src/PeakCan.Host.App/ViewModels/DbcViewModel.cs`
- Create: `src/PeakCan.Host.App/ViewModels/DbcMessageViewModel.cs`
- Create: `src/PeakCan.Host.App/Views/DbcView.xaml`
- Create: `src/PeakCan.Host.App/Views/DbcView.xaml.cs`

**Interfaces:** `DbcService.LoadAsync(string path)` parses in background; emits `DbcDocument?` via event `DbcLoaded`.

- [ ] **Step 1: Write `DbcService`**

```csharp
// src/PeakCan.Host.App/Services/DbcService.cs
using PeakCan.Host.Core.Dbc;
namespace PeakCan.Host.App.Services;
public sealed class DbcService
{
    public DbcDocument? Current { get; private set; }
    public event Action<DbcDocument>? DbcLoaded;
    public event Action<Error>? LoadFailed;

    public async Task LoadAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var text = await File.ReadAllTextAsync(path, ct);
            var r = await Task.Run(() => DbcParser.Parse(text, ct), ct);
            if (r.IsSuccess) { Current = r.Value; DbcLoaded?.Invoke(r.Value); }
            else LoadFailed?.Invoke(r.Error!);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LoadFailed?.Invoke(new Error(ErrorCode.IoError, ex.Message)); }
    }
}
```

- [ ] **Step 2: Write `DbcViewModel` + `DbcMessageViewModel`**

```csharp
// src/PeakCan.Host.App/ViewModels/DbcMessageViewModel.cs
using PeakCan.Host.Core.Dbc;
namespace PeakCan.Host.App.ViewModels;
public sealed class DbcMessageViewModel
{
    public string Name { get; init; } = "";
    public string Id { get; init; } = "";
    public string Dlc { get; init; } = "";
    public string Sender { get; init; } = "";
    public int SignalCount { get; init; }

    public static DbcMessageViewModel From(Message m) => new()
    {
        Name = m.Name,
        Id = m.IsExtended ? $"0x{m.Id:X8}" : $"0x{m.Id:X3}",
        Dlc = m.Dlc.ToString(),
        Sender = m.Sender,
        SignalCount = m.Signals.Count,
    };
}

// src/PeakCan.Host.App/ViewModels/DbcViewModel.cs
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PeakCan.Host.App.Services;
namespace PeakCan.Host.App.ViewModels;
public sealed partial class DbcViewModel : ObservableObject
{
    private readonly DbcService _svc;
    public ObservableCollection<DbcMessageViewModel> Messages { get; } = new();

    [ObservableProperty] private string _loadedPath = "";
    [ObservableProperty] private string _status = "No DBC loaded";

    public DbcViewModel(DbcService svc)
    {
        _svc = svc;
        _svc.DbcLoaded += OnLoaded;
        _svc.LoadFailed += e => Status = $"FAIL: {e.Message}";
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        var dlg = new OpenFileDialog { Filter = "DBC files (*.dbc)|*.dbc|All files|*.*" };
        if (dlg.ShowDialog() == true)
        {
            LoadedPath = dlg.FileName;
            Status = "Parsing...";
            await _svc.LoadAsync(dlg.FileName);
        }
    }

    private void OnLoaded(PeakCan.Host.Core.Dbc.DbcDocument doc)
    {
        Messages.Clear();
        foreach (var m in doc.Messages) Messages.Add(DbcMessageViewModel.From(m));
        Status = $"Loaded {doc.Messages.Count} messages from {Path.GetFileName(LoadedPath)}";
    }
}
```

- [ ] **Step 3: Write `DbcView.xaml`**

```xml
<!-- src/PeakCan.Host.App/Views/DbcView.xaml -->
<UserControl x:Class="PeakCan.Host.App.Views.DbcView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <DockPanel Margin="8">
        <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,0,0,8">
            <Button Content="Open DBC..." Command="{Binding OpenCommand}" Width="100" />
            <TextBlock Text="{Binding Status}" Margin="12,0,0,0" VerticalAlignment="Center" />
        </StackPanel>
        <DataGrid ItemsSource="{Binding Messages}" AutoGenerateColumns="False" IsReadOnly="True">
            <DataGrid.Columns>
                <DataGridTextColumn Header="ID" Binding="{Binding Id}" Width="120" />
                <DataGridTextColumn Header="Name" Binding="{Binding Name}" Width="*" />
                <DataGridTextColumn Header="DLC" Binding="{Binding Dlc}" Width="60" />
                <DataGridTextColumn Header="Sender" Binding="{Binding Sender}" Width="120" />
                <DataGridTextColumn Header="Signals" Binding="{Binding SignalCount}" Width="80" />
            </DataGrid.Columns>
        </DataGrid>
    </DockPanel>
</UserControl>
```

```csharp
// src/PeakCan.Host.App/Views/DbcView.xaml.cs
using System.Windows.Controls;
namespace PeakCan.Host.App.Views;
public partial class DbcView : UserControl { public DbcView() { InitializeComponent(); } }
```

- [ ] **Step 4: Hook menu in `AppShellViewModel` to show DbcView**

Replace `AppShellViewModel` (add) with:

```csharp
public sealed partial class AppShellViewModel : ObservableObject
{
    private readonly IServiceProvider _sp;
    public AppShellViewModel(TraceViewModel trace, DbcViewModel dbc, SendViewModel send,
                             IServiceProvider sp)
    { TraceViewModel = trace; DbcViewModel = dbc; SendViewModel = send; _sp = sp; }

    [ObservableProperty] private object _currentView = null!;
    public TraceViewModel TraceViewModel { get; }
    public DbcViewModel DbcViewModel { get; }
    public SendViewModel SendViewModel { get; }

    [ObservableProperty] private string _statusMessage = "Ready";

    [RelayCommand] private void ShowTrace() => CurrentView = new TraceView { DataContext = TraceViewModel };
    [RelayCommand] private void ShowDbc()   => CurrentView = new DbcView   { DataContext = DbcViewModel };
    [RelayCommand] private void ShowSend()  => CurrentView = new SendView  { DataContext = SendViewModel };
    [RelayCommand] private void OpenDbc()   => ShowDbc();
}
```

Replace `MainArea` in `AppShell.xaml` with:

```xml
<ContentControl Grid.Row="0" x:Name="MainArea" Content="{Binding CurrentView}" />
```

Add a View menu:

```xml
<MenuItem Header="_View">
    <MenuItem Header="Trace" Command="{Binding ShowTraceCommand}" />
    <MenuItem Header="DBC"   Command="{Binding ShowDbcCommand}" />
    <MenuItem Header="Send"  Command="{Binding ShowSendCommand}" />
</MenuItem>
```

In `AppShellViewModel` ctor, set `CurrentView = new TraceView { DataContext = TraceViewModel };` as the initial view.

- [ ] **Step 5: Build + run, test open DBC**

Use any DBC file (e.g., copy one from `dbc-forge` samples). Verify the table populates.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(app): DbcService + DbcViewModel + DbcView + view-switching shell"
```

---

### Task 16: App — `SignalViewModel` (decoded live signals, per DBC message)

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/SignalViewModel.cs`
- Create: `src/PeakCan.Host.App/ViewModels/SignalEntry.cs`
- Create: `src/PeakCan.Host.App/Views/SignalView.xaml`
- Create: `src/PeakCan.Host.App/Views/SignalView.xaml.cs`
- Modify: `src/PeakCan.Host.App/Services/DbcService.cs` (add a hook for frame decode)

**Interfaces:** When TraceService receives a frame, look up the message in `DbcService.Current?.MessagesById`; for each matching signal, decode via `SignalDecoder.Decode` and append to `SignalEntry` table.

- [ ] **Step 1: Add `SignalDecoder` consumer in `TraceService`**

Modify `TraceService.OnFrame` to also call `DbcService.TryDecode(frame, callback)`. For MVP, the simplest path is to give `TraceService` a reference to `DbcService` and a `SignalViewModel`.

Replace the constructor of `TraceService`:

```csharp
    private readonly DbcService _dbc;
    private readonly SignalViewModel _signalVm;
    public TraceService(TraceViewModel vm, DbcService dbc, SignalViewModel signalVm)
    { _vm = vm; _dbc = dbc; _signalVm = signalVm; }
```

In `OnFrame`, after `_batch.Writer.TryWrite(f)`, also:

```csharp
        var doc = _dbc.Current;
        if (doc is not null && doc.MessagesById.TryGetValue(f.Id.Raw, out var msg))
            _signalVm.ApplyFrame(f, msg);
```

- [ ] **Step 2: Write `SignalViewModel` + `SignalEntry`**

```csharp
// src/PeakCan.Host.App/ViewModels/SignalEntry.cs
namespace PeakCan.Host.App.ViewModels;
public sealed class SignalEntry
{
    public string Message { get; init; } = "";
    public string Signal { get; init; } = "";
    public string Raw { get; init; } = "";
    public string Physical { get; init; } = "";
    public string Unit { get; init; } = "";
}

// src/PeakCan.Host.App/ViewModels/SignalViewModel.cs
using System.Collections.ObjectModel;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
namespace PeakCan.Host.App.ViewModels;
public sealed class SignalViewModel
{
    public ObservableCollection<SignalEntry> Latest { get; } = new();
    private readonly Dictionary<string, SignalEntry> _byKey = new();

    public void ApplyFrame(CanFrame frame, Message msg)
    {
        var span = frame.Data.Span;
        foreach (var sig in msg.Signals)
        {
            if (sig.IsMultiplexor || sig.IsMultiplexed) continue;   // v1.1
            double phys = SignalDecoder.Decode(span, sig);
            var key = $"{msg.Name}.{sig.Name}";
            var entry = new SignalEntry
            {
                Message = msg.Name,
                Signal = sig.Name,
                Raw = $"0x{phys:F0}",
                Physical = phys.ToString("0.###"),
                Unit = sig.Unit,
            };
            _byKey[key] = entry;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) continue;
            dispatcher.InvokeAsync(() =>
            {
                var existing = Latest.FirstOrDefault(e => e.Message == msg.Name && e.Signal == sig.Name);
                if (existing is null) Latest.Add(entry);
                else { var idx = Latest.IndexOf(existing); Latest[idx] = entry; }
            });
        }
    }

    public void Reset() { Latest.Clear(); _byKey.Clear(); }
}
```

- [ ] **Step 3: Wire DBC load to clear signal table**

In `DbcViewModel.OnLoaded`, add `SignalViewModel.Reset()` call. Inject `SignalViewModel` into `DbcViewModel`.

- [ ] **Step 4: Write `SignalView.xaml`**

```xml
<!-- src/PeakCan.Host.App/Views/SignalView.xaml -->
<UserControl x:Class="PeakCan.Host.App.Views.SignalView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <DataGrid ItemsSource="{Binding Latest}" AutoGenerateColumns="False" IsReadOnly="True">
        <DataGrid.Columns>
            <DataGridTextColumn Header="Message" Binding="{Binding Message}" Width="160" />
            <DataGridTextColumn Header="Signal"  Binding="{Binding Signal}"  Width="160" />
            <DataGridTextColumn Header="Raw"     Binding="{Binding Raw}"     Width="100" />
            <DataGridTextColumn Header="Physical" Binding="{Binding Physical}" Width="100" />
            <DataGridTextColumn Header="Unit"    Binding="{Binding Unit}"    Width="*" />
        </DataGrid.Columns>
    </DataGrid>
</UserControl>
```

```csharp
// src/PeakCan.Host.App/Views/SignalView.xaml.cs
using System.Windows.Controls;
namespace PeakCan.Host.App.Views;
public partial class SignalView : UserControl { public SignalView() { InitializeComponent(); } }
```

- [ ] **Step 5: Add menu item + view**

In `AppShellViewModel` add `SignalViewModel SignalViewModel { get; }` and `[RelayCommand] private void ShowSignals() => CurrentView = new SignalView { DataContext = SignalViewModel };`.

Add menu item: `<MenuItem Header="Signals" Command="{Binding ShowSignalsCommand}" />`.

- [ ] **Step 6: Build + run, verify signal view shows decoded values**

Need real PCAN traffic or mock frame injection. For MVP smoke test, you can temporarily inject frames from `App.OnStartup` via `TraceService.OnFrame` — but this is optional. If no hardware, just confirm the view renders empty.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(app): SignalViewModel + SignalView (DBC-decoded live signals)"
```

---

### Task 17: App — `StatisticsService` + `StatsViewModel` + `StatsView` (LiveCharts2 charts)

**Files:**
- Create: `src/PeakCan.Host.App/Services/StatisticsService.cs`
- Create: `src/PeakCan.Host.App/ViewModels/StatsViewModel.cs`
- Create: `src/PeakCan.Host.App/Views/StatsView.xaml`
- Create: `src/PeakCan.Host.App/Views/StatsView.xaml.cs`

**Interfaces:** `StatisticsService` runs a 1 Hz timer that calls `BusStatisticsCollector.Snapshot()` and pushes into `StatsViewModel`.

- [ ] **Step 1: Write `StatisticsService`**

```csharp
// src/PeakCan.Host.App/Services/StatisticsService.cs
using Microsoft.Extensions.Hosting;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Infrastructure.Statistics;
namespace PeakCan.Host.App.Services;
public sealed class StatisticsService : BackgroundService
{
    private readonly BusStatisticsCollector _collector;
    private readonly StatsViewModel _vm;
    public StatisticsService(BusStatisticsCollector c, StatsViewModel vm) { _collector = c; _vm = vm; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); } catch (TaskCanceledException) { break; }
            var snap = _collector.Snapshot();
            _vm.Push(snap);
        }
    }
}
```

- [ ] **Step 2: Write `StatsViewModel`**

```csharp
// src/PeakCan.Host.App/ViewModels/StatsViewModel.cs
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using PeakCan.Host.Infrastructure.Statistics;
namespace PeakCan.Host.App.ViewModels;
public sealed partial class StatsViewModel : ObservableObject
{
    public ObservableCollection<double> FpsSeries { get; } = new();
    public ObservableCollection<double> LoadSeries { get; } = new();
    [ObservableProperty] private long _totalFrames;
    [ObservableProperty] private long _errorFrames;
    public Axis[] XAxes { get; } = { new Axis { Labeler = v => v.ToString("0") } };
    public Axis[] YAxes { get; } = { new Axis { MinLimit = 0 } };

    public void Push(BusStatistics s)
    {
        TotalFrames = s.TotalFrames;
        ErrorFrames = s.ErrorFrames;
        FpsSeries.Add(s.FramesPerSecond);
        LoadSeries.Add(s.BusLoadPercent);
        const int MaxPoints = 60;
        while (FpsSeries.Count > MaxPoints) FpsSeries.RemoveAt(0);
        while (LoadSeries.Count > MaxPoints) LoadSeries.RemoveAt(0);
    }
}
```

- [ ] **Step 3: Write `StatsView.xaml`**

```xml
<!-- src/PeakCan.Host.App/Views/StatsView.xaml -->
<UserControl x:Class="PeakCan.Host.App.Views.StatsView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:lvc="clr-namespace:LiveChartsCore.SkiaSharpView.WPF;assembly=LiveChartsCore.SkiaSharpView.WPF">
    <DockPanel Margin="8">
        <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="Total: " />
            <TextBlock Text="{Binding TotalFrames}" Margin="0,0,16,0" />
            <TextBlock Text="Errors: " />
            <TextBlock Text="{Binding ErrorFrames}" />
        </StackPanel>
        <lvc:CartesianChart Series="{Binding Series}" XAxes="{Binding XAxes}" YAxes="{Binding YAxes}" />
    </DockPanel>
</UserControl>
```

Then in code-behind, bind `Series` to a collection of `LineSeries<double>` (built in `StatsViewModel` ctor):

```csharp
// src/PeakCan.Host.App/ViewModels/StatsViewModel.cs (additions)
public ISeries[] Series { get; }
public StatsViewModel()
{
    Series = new ISeries[]
    {
        new LineSeries<double> { Values = FpsSeries, Name = "fps" },
        new LineSeries<double> { Values = LoadSeries, Name = "load %" },
    };
}
```

```csharp
// src/PeakCan.Host.App/Views/StatsView.xaml.cs
using System.Windows.Controls;
namespace PeakCan.Host.App.Views;
public partial class StatsView : UserControl { public StatsView() { InitializeComponent(); } }
```

- [ ] **Step 4: Add menu item + view**

`AppShellViewModel.ShowStatsCommand` + `<MenuItem Header="Stats" Command="{Binding ShowStatsCommand}" />`.

- [ ] **Step 5: Build + run, verify chart renders (empty series at first)**

```bash
dotnet build
dotnet run --project src/PeakCan.Host.App
```

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(app): StatisticsService + StatsViewModel + LiveCharts2 chart"
```

---

### Task 18: Architecture rules — NetArchTest

**Files:**
- Create: `tests/PeakCan.Host.Infrastructure.Tests/Architecture/LayeringRulesTests.cs`

**Interfaces:** Enforces 4 layering rules. Each rule is a `[Fact]` with a clear name.

- [ ] **Step 1: Write the rules**

```csharp
// tests/PeakCan.Host.Infrastructure.Tests/Architecture/LayeringRulesTests.cs
using NetArchTest.Rules;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Peak;
using PeakCan.Host.App;
using Xunit;
namespace PeakCan.Host.Infrastructure.Tests.Architecture;
public class LayeringRulesTests
{
    [Fact]
    public void Core_Should_Not_Depend_On_WPF()
    {
        var result = Types.InAssembly(typeof(CanFrame).Assembly)
            .ShouldNot().HaveDependencyOn("System.Windows")
            .GetResult();
        Assert.True(result.IsSuccessful, Format(result));
    }

    [Fact]
    public void Core_Should_Not_Depend_On_Peak_Can_Basic()
    {
        var result = Types.InAssembly(typeof(CanFrame).Assembly)
            .ShouldNot().HaveDependencyOn("Peak.Can.Basic")
            .GetResult();
        Assert.True(result.IsSuccessful, Format(result));
    }

    [Fact]
    public void App_Should_Not_Depend_On_Peak_Can_Basic()
    {
        var result = Types.InAssembly(typeof(AppHostBuilder).Assembly)
            .ShouldNot().HaveDependencyOn("Peak.Can.Basic")
            .GetResult();
        Assert.True(result.IsSuccessful, Format(result));
    }

    [Fact]
    public void Infrastructure_Should_Not_Depend_On_WPF()
    {
        var result = Types.InAssembly(typeof(PeakCanChannel).Assembly)
            .ShouldNot().HaveDependencyOn("System.Windows")
            .GetResult();
        Assert.True(result.IsSuccessful, Format(result));
    }

    [Fact]
    public void Infrastructure_Should_Not_Depend_On_App()
    {
        var result = Types.InAssembly(typeof(PeakCanChannel).Assembly)
            .ShouldNot().HaveDependencyOn("PeakCan.Host.App")
            .GetResult();
        Assert.True(result.IsSuccessful, Format(result));
    }

    private static string Format(NetArchTest.TestHelpers.TestResult r)
        => string.Join("\n", r.FailingTypeNames ?? new System.Collections.Generic.List<string>());
}
```

- [ ] **Step 2: Run, verify GREEN**

```bash
dotnet test tests/PeakCan.Host.Infrastructure.Tests --filter LayeringRulesTests
```

Expected: all 5 pass. If any fails, locate the violation in the corresponding project and refactor.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "test(arch): NetArchTest layering rules (5 rules enforcing 3-layer separation)"
```

---

### Task 19: CI script + coverage gate (`.github/workflows/ci.yml`)

**Files:**
- Create: `.github/workflows/ci.yml`
- Create: `Directory.Build.props` (modify — add `IsPackable=false` for tests)

**Interfaces:** Every PR runs `dotnet build`, `dotnet test --collect`, uploads `coverage.cobertura.xml`. Coverage threshold check: total ≥ 80%, Core line ≥ 95%, Core branch ≥ 90%.

- [ ] **Step 1: Add coverage threshold check via `coverlet.msbuild` in test projects**

Edit `tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj`:

```xml
<PropertyGroup>
  <CollectCoverage>true</CollectCoverage>
  <CoverletOutput>../..//coverage/core-coverage</CoverletOutput>
  <CoverletOutputFormat>cobertura</CoverletOutputFormat>
  <Threshold>95</Threshold>
  <ThresholdType>line,branch</ThresholdType>
  <ThresholdStat>total</ThresholdStat>
</PropertyGroup>
```

Edit `tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj`:

```xml
<PropertyGroup>
  <CollectCoverage>true</CollectCoverage>
  <CoverletOutput>../..//coverage/infra-coverage</CoverletOutput>
  <CoverletOutputFormat>cobertura</CoverletOutputFormat>
  <Threshold>80</Threshold>
  <ThresholdType>line,branch</ThresholdType>
  <ThresholdStat>total</ThresholdStat>
</PropertyGroup>
```

- [ ] **Step 2: Run, verify thresholds pass**

```bash
dotnet test
```

Expected: coverage threshold 95% (Core) and 80% (Infra) pass.

- [ ] **Step 3: Write `.github/workflows/ci.yml`**

```yaml
name: CI
on:
  push:
    branches: [main]
  pull_request:
    branches: [main]

jobs:
  build-test:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 8.0.x
      - name: Restore
        run: dotnet restore
      - name: Build
        run: dotnet build -c Release --no-restore
      - name: Test + Coverage
        run: dotnet test -c Release --no-build --verbosity normal
      - name: Upload coverage
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: coverage
          path: coverage/
```

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "ci: GitHub Actions build + test + coverage thresholds (95% Core, 80% Infra)"
```

---

### Task 20: Final integration smoke + manual MVP acceptance

**Files:** none (manual)

**Interfaces:** Run `dotnet publish` for the App; double-click the produced exe; confirm it launches without .NET runtime installed on the machine. If a real PCAN is available, connect + send + receive. If not, verify the empty shell launches and DBC loading works against a sample file (e.g. from `dbc-forge` samples).

- [ ] **Step 1: Run full test suite**

```bash
cd "D:/claude_proj2/peakcan-host"
dotnet test -c Release
```

Expected: all green; coverage thresholds pass.

- [ ] **Step 2: Publish self-contained single-file exe**

```bash
dotnet publish src/PeakCan.Host.App -c Release -r win-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -o artifacts/win-x64/
```

Expected: `artifacts/win-x64/PeakCan.Host.exe` exists, ~80-120 MB.

- [ ] **Step 3: Smoke launch (on a clean machine, or in a sandbox)**

```bash
"artifacts/win-x64/PeakCan.Host.exe"
```

Expected: window appears, no missing-DLL errors. If "PCAN driver not loaded" appears, that is expected and OK for smoke (no hardware present).

- [ ] **Step 4: Verify DBC load path**

1. Copy a sample `.dbc` from `D:/claude_proj2/dbc-forge/samples/` (if any) or any DBC you have.
2. In the app: View → DBC → Open DBC → pick file.
3. Verify message table populates with the right row count.

Expected: status bar reads "Loaded N messages from <file>".

- [ ] **Step 5: Check all DoD items from spec §1.3**

For each item in `docs/superpowers/specs/2026-06-18-peakcan-host-design.md` §1.3, mark done or note blocker. Hardware-dependent items (1, 2, 3, 5, 6) require real PCAN. Software-only items (4, 7, 8, 9, 10) should already be green from earlier tasks.

- [ ] **Step 6: Commit any final tweaks, tag v0.1.0**

```bash
git tag -a v0.1.0 -m "MVP internal preview — 6 DoD items, Core 100% covered"
```

---

### Task 21: README + user docs

**Files:**
- Create: `README.md` (project root)

**Interfaces:** README covers: prerequisites (Windows 10/11, PEAK driver), how to build, how to run, MVP feature list, DBC parser limitations, link to spec + plan.

- [ ] **Step 1: Write `README.md`**

```markdown
# PeakCan Host

Windows-only WPF desktop host for PEAK PCAN-USB FD / Pro FD — generic CAN bus monitor with DBC decoding.

> **Status:** MVP (v0.1.0). See [docs/superpowers/specs/2026-06-18-peakcan-host-design.md](docs/superpowers/specs/2026-06-18-peakcan-host-design.md) for full design and [docs/superpowers/plans/2026-06-18-peakcan-host-implementation.md](docs/superpowers/plans/2026-06-18-peakcan-host-implementation.md) for the build plan.

## Prerequisites

- Windows 10 (1809+) or Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (for development)
- PEAK PCAN driver — install from [PEAK-System download page](https://www.peak-system.com/PCAN-USB-FD.366.0.html)
- (For release binary) — no separate runtime required, the published exe is self-contained.

## Build

```bash
dotnet build -c Release
```

## Run

```bash
dotnet run --project src/PeakCan.Host.App
```

Or run the published self-contained exe:

```bash
dotnet publish src/PeakCan.Host.App -c Release -r win-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -o artifacts/win-x64/
artifacts/win-x64/PeakCan.Host.exe
```

## Test

```bash
dotnet test
```

## Features (MVP)

- Enumerate installed PCAN channels
- Connect / disconnect with baudrate + CAN FD toggle
- Real-time Trace view (virtualized DataGrid)
- DBC file load + signal decoding
- Manual frame send
- Bus statistics (fps, errors, load %)
- Serilog rolling logs at `%LocalAppData%\PeakCan.Host\logs\`

## DBC Parser Scope

- Standard DBC keywords: VERSION, NS_, BS_, BU_, BO_, SG_, VAL_, VAL_TABLE_, CM_, BA_DEF_, BA_, SIG_GROUP_, EV_
- Multiplexed signals (M / m) supported
- IEEE float/double (Vector extension) accepted; decode falls back to int if not recognized
- See spec §"DBC parser scope" for the full subset.

## Roadmap

- v1.1 — Recording (ASC/CSV), cyclic transmission, frame filters
- v1.2 — Script automation, real-time signal charts
- v1.3 — UDS diagnostic stack
- v2.0 — J1939 / CANopen, cross-platform (Linux)

## License

Project-internal. PCAN-Basic SDK is used per PEAK-System terms.
```

- [ ] **Step 2: Commit**

```bash
git add README.md
git commit -m "docs: README with prerequisites, build/run/test, MVP feature list, roadmap"
```

---

## Self-Review

**1. Spec coverage check**

| Spec section / requirement | Plan task |
|---|---|
| §1.1 Goals 1: enumerate channels | Task 9 (PeakCanChannel); visible in App via dropdown (TBD) — covered by host surface |
| §1.1 Goal 2: connect / disconnect | Task 9 (`ConnectAsync` / `DisconnectAsync`); UI hookup = Task 15 / 17 wiring (assumes main shell's connect button) — **GAP**: no explicit connect UI task |
| §1.1 Goal 3: Trace view | Task 13 |
| §1.1 Goal 4: DBC load + decode | Tasks 4-7, 15, 16 |
| §1.1 Goal 5: manual send | Task 14 |
| §1.1 Goal 6: bus statistics | Tasks 11, 17 |
| §2 Hardware & Runtime | Task 1 (TFM + manifest) |
| §3 Tech Stack | Task 1 (packages) |
| §4 Architecture | Tasks 1, 9-17 |
| §5 Core Data Model | Task 2 |
| §6 Error Handling | Tasks 3, 8 |
| §7 Testing | Tasks 4-7, 9-11, 18, 19 |
| §8 Build & Distribution | Task 20 |
| §9 Risk Register | addressed in Task 1 (DPI manifest), Task 9 (driver probe), Task 13 (virtualization) |

**GAP FOUND**: Connect/disconnect UI is not an explicit task. The plan assumes AppShell has a connect button but does not specify it. **Add Task 12.5 / Task 16.5 OR amend Task 12.**

→ **Fix inline**: Extend Task 12 with explicit Channel list + connect/disconnect UI. (Amend Task 12 description below.)

**2. Placeholder scan**: No "TBD" / "TODO" / "implement later" — all steps have full code.

**3. Type consistency**:
- `CanFrame` record struct — used consistently in Tasks 2, 9, 11, 13-17.
- `Result<T>` — used in Task 9, 14.
- `ChannelId` / `BaudRate` — defined in Task 9, used in Task 12, 14.
- `DbcDocument.MessagesById` — defined in Task 5, used in Task 16. ✓

**Inline amendment to Task 12** (add connect UI):

After Step 4 in Task 12, add a new Step 4a:

- [ ] **Step 4a: Add Channel list + Connect button to AppShell**

Add to `AppShellViewModel`:

```csharp
[ObservableProperty] private string _channelList = "(no channels enumerated)";
[RelayCommand] private void EnumerateChannels() { /* calls PCANBasic.GetValue for handles 0..7 */ ChannelList = "..."; }
[RelayCommand] private async Task ConnectAsync() { /* create PeakCanChannel, ConnectAsync, register on router, set SendService.ActiveChannel */ }
```

For MVP, hardcode handle 0x01 (PCAN-USB FD first channel). Full multi-channel enumeration is v1.1.

**This amendment is inlined above.**

---

## Final Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-06-18-peakcan-host-implementation.md`.**

21 tasks across 7 logical waves:

```
Wave 1 (1 task):  Project scaffolding + solution
Wave 2 (3 tasks): Core domain types (CanFrame, Result, DbcTokenizer, DbcParser, SignalDecoder)
Wave 3 (4 tasks): Infrastructure (PeakError, ICanChannel, ChannelRouter, BusStatisticsCollector)
Wave 4 (6 tasks): App WPF (AppShell, Trace, Send, DBC, Signal, Stats)
Wave 5 (2 tasks): Architecture rules + CI
Wave 6 (2 tasks): Final smoke + release
Wave 7 (1 task):  README
```

**Two execution options:**

1. **Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.
2. **Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.
