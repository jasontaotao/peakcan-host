# 移动端 Trace Viewer P0+P1 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 Android（.NET MAUI）上做 peakcan-host 的离线 trace viewer，P1 阶段实现流式 ASC 回放核心闭环——打开文件即流式首帧、播放/暂停/倍速/Seek、帧表格（环形缓冲+跟随语义）、播放中 ID 过滤、后台总时长扫描。

**Architecture:** 流式回放为核心（CANoe 模型），SQLite 留到 P2 作回看缓存。新增 3 个项目：`PeakCan.Host.Mobile.Core`（net10.0 纯逻辑库，可单测，依赖 Host.Core/HIL.Core + CommunityToolkit.Mvvm）、`PeakCan.Host.Mobile`（net10.0-android MAUI app，薄 UI + Platform 实现）、`PeakCan.Host.Mobile.Core.Tests`。Core 解析/播放逻辑落在 `Host.Core/Replay/Streaming/`（additive，不动现有 14 个 `AscParser.ParseAsync` 调用点）。

> **架构偏差说明**：spec §5.2 把 VM/服务画进单一 MAUI app 项目 + multi-target `net10.0-android;net10.0`。实施时发现"MAUI app 双 target 纯 net10.0 供单测"有 rough edges（Platforms/ glob、OutputType=Exe、Essentials 类型仅平台 TFM 可见）。改为：VM/服务/纯逻辑进独立的 `Mobile.Core`（net10.0）类库，app 项目只留 Views + Platform impl + 接线。这更符合"可独立单测、边界清晰"原则，spec 的功能设计不变。

**Tech Stack:** .NET 10（SDK 10.0.400）、MAUI（maui-android workload）、SQLite（P2，`Microsoft.Data.Sqlite`）、CommunityToolkit.Mvvm 8.4.2、xunit 2.9.3 + FluentAssertions 8.10.0 + NSubstitute 5.3.0（中央包管理）。

**Spec:** `docs/superpowers/specs/2026-09-07-mobile-trace-viewer-design.md`

## Global Constraints

- 分支 `feature/mobile-trace-viewer`（已从 `main` 拉出）。每 task 一次 commit，conventional commits，无 attribution（仓库全局禁用）。
- `.NET` 10、`<Nullable>enable</Nullable>`、`<ImplicitUsings>enable</ImplicitUsings>`，不显式设 LangVersion（跟随仓库默认）。
- 中央包管理（`Directory.Packages.props`）：新 `PackageReference` 不带 `Version=`。
- **Additive only**：`Host.Core` 现有 `AscParser.ParseAsync` 等 14 个调用点不动；只新增 `Host.Core/Replay/Streaming/` 目录。
- 注释约定：业务逻辑/用户面向注释中文，类型与 API 的 xmldoc 英文（跟随仓库）。
- 测试：xunit + FluentAssertions，确定性假时钟（复用 `FakeReplayClock`），禁止 `Thread.Sleep`/真时钟等待。
- 既有可复用：`AscFormat.TryParseDataLine/TryParseDateHeader/LineIsSectionDelimiter`（public static）、`ReplayFrame`、`ReplayState{Stopped,Playing,Paused}`、`PlaybackEndedEventArgs(Exception?)`、`IReplayClock`/`FakeReplayClock`、`ReplayOptions`、`CanIdListParser.Parse(string?)→CanIdParseResult{AllowList,InvalidTokens}`、`WallClockReplayClock`。
- 真机：Android 12+，开发者模式 + USB 调试已开（用户已确认）。

---

## Task 1: P0 环境 + MAUI 空壳部署到真机

**Files:**
- Create: `src/PeakCan.Host.Mobile/PeakCan.Host.Mobile.csproj`
- Create: `src/PeakCan.Host.Mobile/App.xaml`(.cs)、`MainPage.xaml`(.cs)、`MauiProgram.cs`（`dotnet new maui` 模板生成）

**Interfaces:** 无（环境任务）

- [ ] **Step 1: 装 maui-android workload**

Run:
```bash
dotnet workload install maui-android
```
Expected: 列出已安装 workload 包含 `maui-android`。

- [ ] **Step 2: 用模板生成 MAUI app**

Run:
```bash
cd /d/claude_proj2/peakcan-host
dotnet new maui -n PeakCan.Host.Mobile -o src/PeakCan.Host.Mobile
```
Expected: `src/PeakCan.Host.Mobile/` 含 `.csproj` + `App.xaml` + `MainPage.xaml` + `Platforms/Android/`。

- [ ] **Step 3: csproj 收窄到单 Android 目标**

编辑 `src/PeakCan.Host.Mobile/PeakCan.Host.Mobile.csproj` 的首个 `<PropertyGroup>`：

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <TargetFrameworks>net10.0-android</TargetFrameworks>
  <!-- 原模板含 net10.0-ios;maccatalyst;windows —— 删除，只留 android -->
  <UseMaui>true</UseMaui>
  <SingleProject>true</SingleProject>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
  <ApplicationId>com.zhengtaotao.peakcan.mobile</ApplicationId>
  <ApplicationVersion>1</ApplicationVersion>
</PropertyGroup>
```

Expected: 保存成功。

- [ ] **Step 4: 安装 Android SDK + JDK（MAUI 自动目标）**

Run:
```bash
dotnet build src/PeakCan.Host.Mobile -f net10.0-android -t:InstallAndroidDependencies -p:AcceptAndroidSDKLicenses=true
```
Expected: 下载并接受 license 后构建成功；失败（网络/license）按提示重试。`dl.google.com` 国内一般可直连；如代理必走，导出 `HTTP(S)_PROXY` 后重试。

- [ ] **Step 5: 验证真机 + adb**

手机插 USB、确认"USB 调试"授权弹窗点允许。找 adb 路径（装 SDK 后在 `%LOCALAPPDATA%\Android\sdk\platform-tools\adb.exe`）：

Run:
```bash
ADB="$LOCALAPPDATA/Android/sdk/platform-tools/adb.exe"
"$ADB" devices
```
Expected: 列出一台设备 `xxxxxxxx device`。若 `unauthorized` → 在手机上点"允许 USB 调试"。

- [ ] **Step 6: 部署空壳到真机**

Run:
```bash
dotnet build src/PeakCan.Host.Mobile -t:Run -f net10.0-android
```
Expected: 手机电亮，显示 MAUI 模板默认的"Hello, .NET MAUI"页。

- [ ] **Step 7: 清理模板样例文字（留空壳）**

`src/PeakCan.Host.Mobile/MainPage.xaml` 替换为：
```xml
<?xml version="1.0" encoding="utf-8" ?>
<ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
             x:Class="PeakCan.Host.Mobile.MainPage">
    <ScrollView>
        <VerticalStackLayout Padding="30" Spacing="16" VerticalOptions="Center">
            <Label Text="PeakCan Mobile" FontSize="32" HorizontalOptions="Center" />
            <Label Text="P0 scaffold — ready" FontSize="16" HorizontalOptions="Center" />
        </VerticalStackLayout>
    </ScrollView>
</ContentPage>
```
`MainPage.xaml.cs` 内 `OnCounterClicked` 等模板方法删除，类只留空壳：
```csharp
namespace PeakCan.Host.Mobile;
public partial class MainPage : ContentPage
{
    public MainPage() { InitializeComponent(); }
}
```
`MauiProgram.cs` 删去 `Counter` 相关 `AddSingleton`，仅留 `UseMauiApp<App>` 骨架：
```csharp
using Microsoft.Maui.Controls.Hosting;
namespace PeakCan.Host.Mobile;
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
        => MauiApp.CreateBuilder()
            .UseMauiApp<App>()
            .Build();
}
```

- [ ] **Step 8: 再部署确认无编译错误**

Run:
```bash
dotnet build src/PeakCan.Host.Mobile -t:Run -f net10.0-android
```
Expected: 真机显示"PeakCan Mobile / P0 scaffold — ready"。

- [ ] **Step 9: Commit**

```bash
git add src/PeakCan.Host.Mobile
git commit -m "chore(mobile): scaffold MAUI android app (P0 env)"
```

---

## Task 2: Mobile.Core 库 + 测试项目 + slnx + FrameRingBuffer（TDD）

**Files:**
- Create: `src/PeakCan.Host.Mobile.Core/PeakCan.Host.Mobile.Core.csproj`
- Create: `src/PeakCan.Host.Mobile.Core/PeakCan.Host.Mobile.Core.props`（如需；否则直接写在 csproj）
- Create: `src/PeakCan.Host.Mobile.Core/Models/FrameRow.cs`
- Create: `src/PeakCan.Host.Mobile.Core/Models/FrameRingBuffer.cs`
- Create: `tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj`
- Create: `tests/PeakCan.Host.Mobile.Core.Tests/Models/FrameRingBufferTests.cs`
- Create: `PeakCan.Host.Mobile.slnx`
- Modify: `src/PeakCan.Host.Mobile/PeakCan.Host.Mobile.csproj`（加对 Mobile.Core 的 ProjectReference）

**Interfaces:**
- Consumes: `PeakCan.Host.Core.Replay.ReplayFrame`（record：`Timestamp double / Id uint / Dlc byte / Data byte[] / Flags FrameFlags / IsExtended bool / Channel ushort`）
- Produces: `FrameRow`（display projection）、`FrameRingBuffer(int capacity)`（`Add/Clear/Snapshot`）

- [ ] **Step 1: 建 Mobile.Core 类库项目**

Run:
```bash
dotnet new classlib -n PeakCan.Host.Mobile.Core -o src/PeakCan.Host.Mobile.Core -f net10.0
```
编辑 `src/PeakCan.Host.Mobile.Core/PeakCan.Host.Mobile.Core.csproj`：
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\PeakCan.Host.Core\PeakCan.Host.Core.csproj" />
    <PackageReference Include="CommunityToolkit.Mvvm" />
  </ItemGroup>
</Project>
```
删模板自带的 `Class1.cs`。

- [ ] **Step 2: 建测试项目**

Run:
```bash
dotnet new xunit -n PeakCan.Host.Mobile.Core.Tests -o tests/PeakCan.Host.Mobile.Core.Tests -f net10.0
```
编辑 `tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj`：
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="NSubstitute" />
    <PackageReference Include="coverlet.collector" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\PeakCan.Host.Mobile.Core\PeakCan.Host.Mobile.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: 建 Mobile.slnx 并加四个项目**

Run:
```bash
dotnet new sln -n PeakCan.Host.Mobile --format slnx
dotnet sln PeakCan.Host.Mobile.slnx add src/PeakCan.Host.Mobile/PeakCan.Host.Mobile.csproj
dotnet sln PeakCan.Host.Mobile.slnx add src/PeakCan.Host.Mobile.Core/PeakCan.Host.Mobile.Core.csproj
dotnet sln PeakCan.Host.Mobile.slnx add src/PeakCan.Host.Core/PeakCan.Host.Core.csproj
dotnet sln PeakCan.Host.Mobile.slnx add tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj
```
（`Host.Core` 本身 `ProjectReference` 到 `peakcan-hil-core` 兄弟 repo，条件引用已在 `Host.Core.csproj` 内处理。）

- [ ] **Step 4: 写 FrameRingBuffer 失败测试**

`tests/PeakCan.Host.Mobile.Core.Tests/Models/FrameRingBufferTests.cs`：
```csharp
using FluentAssertions;
using PeakCan.Host.Mobile.Core.Models;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.Models;

public class FrameRingBufferTests
{
    private static FrameRow Row(int i) => new(i, (uint)i, false, 0, Array.Empty<byte>());

    [Fact]
    public void Snapshot_ReturnsFramesInInsertionOrder_WhenUnderCapacity()
    {
        var buf = new FrameRingBuffer(8);
        buf.Add(Row(1)); buf.Add(Row(2)); buf.Add(Row(3));
        var snap = buf.Snapshot();
        snap.Should().HaveCount(3);
        snap.Select(r => r.Timestamp).Should().BeEquivalentTo(new[] { 1d, 2d, 3d });
    }

    [Fact]
    public void Add_OverwritesOldest_WhenAtCapacity()
    {
        var buf = new FrameRingBuffer(3);
        for (int i = 1; i <= 5; i++) buf.Add(Row(i)); // 容量 3 → 留 3,4,5
        var snap = buf.Snapshot();
        snap.Should().HaveCount(3);
        snap.Select(r => r.Timestamp).Should().BeEquivalentTo(new[] { 3d, 4d, 5d });
    }

    [Fact]
    public void Clear_ResetsBuffer()
    {
        var buf = new FrameRingBuffer(4);
        buf.Add(Row(1)); buf.Add(Row(2));
        buf.Clear();
        buf.Snapshot().Should().BeEmpty();
        buf.Count.Should().Be(0);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        Action act = () => new FrameRingBuffer(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
```

- [ ] **Step 5: 跑测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.Mobile.Core.Tests --filter "FullyQualifiedName~FrameRingBufferTests"
```
Expected: 编译失败（`FrameRingBuffer` 不存在）。

- [ ] **Step 6: 实现 FrameRow + FrameRingBuffer**

`src/PeakCan.Host.Mobile.Core/Models/FrameRow.cs`：
```csharp
using System.Globalization;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Mobile.Core.Models;

/// <summary>Display projection of one <see cref="ReplayFrame"/> for the table cell.</summary>
public sealed record FrameRow(double Timestamp, uint Id, bool IsExtended, byte Dlc, byte[] Data)
{
    public string TimeText => Timestamp.ToString("F6", CultureInfo.InvariantCulture);
    public string IdText => IsExtended ? Id.ToString("X8", CultureInfo.InvariantCulture)
                                       : Id.ToString("X3", CultureInfo.InvariantCulture);

    public string DataText
    {
        get
        {
            int n = Math.Min(Dlc, Data.Length);
            if (n == 0) return string.Empty;
            var chars = new char[n * 3 - 1];
            for (int i = 0; i < n; i++)
            {
                byte b = Data[i];
                chars[i * 3] = HexChar(b >> 4);
                chars[i * 3 + 1] = HexChar(b & 0xF);
                if (i < n - 1) chars[i * 3 + 2] = ' ';
            }
            return new string(chars);
        }
    }

    /// <summary>Project a parsed <see cref="ReplayFrame"/> into a display row.</summary>
    public static FrameRow FromReplayFrame(ReplayFrame f) => new(f.Timestamp, f.Id, f.IsExtended, f.Dlc, f.Data);

    private static char HexChar(int v) => (char)(v < 10 ? '0' + v : 'A' + v - 10);
}
```

`src/PeakCan.Host.Mobile.Core/Models/FrameRingBuffer.cs`：
```csharp
namespace PeakCan.Host.Mobile.Core.Models;

/// <summary>
/// 环形缓冲：容量固定，写满后覆盖最旧的一帧。表格数据源用它保持最近 N 帧。
/// Snapshot 返回当前内容的**按插入序**只读拷贝（最旧在前）。
/// 非线程安全——调用方（VM drain）保证单线程访问。
/// </summary>
public sealed class FrameRingBuffer
{
    private readonly FrameRow[] _buffer;
    private int _head;   // 下一次写入位置
    private int _count;

    public FrameRingBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new FrameRow[capacity];
    }

    public int Capacity => _buffer.Length;
    public int Count => _count;

    public void Add(FrameRow row)
    {
        _buffer[_head] = row;
        _head = (_head + 1) % _buffer.Length;
        if (_count < _buffer.Length) _count++;
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
        Array.Clear(_buffer);
    }

    /// <summary>当前内容的按插入序只读拷贝（最旧在前）。</summary>
    public IReadOnlyList<FrameRow> Snapshot()
    {
        var result = new FrameRow[_count];
        int start = _count == _buffer.Length ? _head : 0;
        for (int i = 0; i < _count; i++)
            result[i] = _buffer[(start + i) % _buffer.Length];
        return result;
    }
}
```

- [ ] **Step 7: 跑测试确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.Mobile.Core.Tests --filter "FullyQualifiedName~FrameRingBufferTests"
```
Expected: 4 passed。

- [ ] **Step 8: app 项目引用 Mobile.Core**

`src/PeakCan.Host.Mobile/PeakCan.Host.Mobile.csproj` 加：
```xml
<ItemGroup>
  <ProjectReference Include="..\PeakCan.Host.Mobile.Core\PeakCan.Host.Mobile.Core.csproj" />
</ItemGroup>
```

- [ ] **Step 9: Commit**

```bash
git add PeakCan.Host.Mobile.slnx src/PeakCan.Host.Mobile.Core tests/PeakCan.Host.Mobile.Core.Tests src/PeakCan.Host.Mobile/PeakCan.Host.Mobile.csproj
git commit -m "feat(mobile): add Mobile.Core lib + tests + FrameRingBuffer (TDD)"
```

---

## Task 3: Core 流式解析类型 + AscStreamingSource（TDD 对拍）

**Files:**
- Create: `src/PeakCan.Host.Core/Replay/Streaming/StreamingParseStats.cs`
- Create: `src/PeakCan.Host.Core/Replay/Streaming/StreamingTraceOpenResult.cs`
- Create: `src/PeakCan.Host.Core/Replay/Streaming/IStreamingTraceSource.cs`
- Create: `src/PeakCan.Host.Core/Replay/Streaming/AscStreamingSource.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Replay/Streaming/AscStreamingSourceTests.cs`

**Interfaces:**
- Consumes: `AscFormat.TryParseDataLine(string, out ReplayFrame, out string reason)`、`AscFormat.TryParseDateHeader(string)`、`AscFormat.LineIsSectionDelimiter(string)`、`ReplayOptions`、`ReplayFrame`、`ReplayFormatException`
- Produces: `IStreamingTraceSource.OpenAsync → StreamingTraceOpenResult{WallClockOrigin,TimestampsAreAbsolute,Frames:IAsyncEnumerable<ReplayFrame>,SourceLengthBytes:long?,Stats:StreamingParseStats}`、`StreamingParseStats.SkippedLines/BytesRead`（枚举期间增长）

- [ ] **Step 1: 写流式类型 + 接口**

`src/PeakCan.Host.Core/Replay/Streaming/StreamingParseStats.cs`：
```csharp
namespace PeakCan.Host.Core.Replay;

/// <summary>
/// Streaming parse counters, live during enumeration. Thread-safe reads
/// (Interlocked) because the consumer reads from a different thread than
/// the producer (parser on a background task, UI/player reading SkippedLines).
/// </summary>
public sealed class StreamingParseStats
{
    private long _skippedLines;
    private long _bytesRead;
    public long SkippedLines => Interlocked.Read(ref _skippedLines);
    public long BytesRead => Interlocked.Read(ref _bytesRead);
    internal void IncSkipped() => Interlocked.Increment(ref _skippedLines);
    internal void AddBytes(long n) { if (n > 0) Interlocked.Add(ref _bytesRead, n); }
}
```

`src/PeakCan.Host.Core/Replay/Streaming/StreamingTraceOpenResult.cs`：
```csharp
namespace PeakCan.Host.Core.Replay;

/// <summary>
/// One opened streaming parse session. Header is populated eagerly during
/// <see cref="IStreamingTraceSource.OpenAsync"/>; <see cref="Frames"/> is
/// lazily enumerated. Re-open (e.g. for <c>StreamingTracePlayer.Seek</c>) by
/// calling <c>OpenAsync</c> again.
/// </summary>
public sealed record StreamingTraceOpenResult(
    DateTime? WallClockOrigin,
    bool TimestampsAreAbsolute,
    IAsyncEnumerable<ReplayFrame> Frames,
    long? SourceLengthBytes,
    StreamingParseStats Stats);
```

`src/PeakCan.Host.Core/Replay/Streaming/IStreamingTraceSource.cs`：
```csharp
namespace PeakCan.Host.Core.Replay;

/// <summary>
/// Re-openable source of a streaming frame enumeration. Each
/// <see cref="OpenAsync"/> returns a fresh <see cref="StreamingTraceOpenResult"/>
/// whose <c>Frames</c> starts at the beginning of the source (or where the
/// concrete impl positions it). Used by <see cref="StreamingTracePlayer"/>
/// for fast-forward Seek.
/// </summary>
public interface IStreamingTraceSource
{
    Task<StreamingTraceOpenResult> OpenAsync(CancellationToken ct = default);
}
```

- [ ] **Step 2: 写 AscStreamingSource 失败测试**

`tests/PeakCan.Host.Core.Tests/Replay/Streaming/AscStreamingSourceTests.cs`：
```csharp
using System.Text;
using FluentAssertions;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Replay.Streaming;

public class AscStreamingSourceTests
{
    private static MemoryStream AscStream(string content)
        => new(Encoding.UTF8.GetBytes(content));

    // 复用 AscParserTests 的 fixture 格式
    private const string ThreeFrames = @"
date Wed Jun 28 10:00:00.000 2026
base 0x7e0 500k timestamps absolute
internal events logged

 0.000000 51  100  8  11 22 33 44 55 66 77 88
 0.500000 51  200  4  AA BB CC DD
 1.000000 51  100  2  01 02
";

    private static async Task<List<ReplayFrame>> ConsumeAll(IAsyncEnumerable<ReplayFrame> frames)
    {
        var list = new List<ReplayFrame>();
        await foreach (var f in frames) list.Add(f);
        return list;
    }

    private static (double Ts, uint Id, byte[] Data, bool IsExt) Of(ReplayFrame f)
        => (f.Timestamp, f.Id, f.Data, f.IsExtended);

    [Fact]
    public async Task Frames_Match_Batch_Parser_ForSameFixture()
    {
        using var batchStream = AscStream(ThreeFrames);
        var batch = await AscParser.ParseAsyncWithHeaderAsync(batchStream);
        using var streamStream = AscStream(ThreeFrames);
        var source = new AscStreamingSource(() => AscStream(ThreeFrames));
        var result = await source.OpenAsync();
        var streamed = await ConsumeAll(result.Frames);

        streamed.Should().HaveCount(batch.Frames.Count);
        for (int i = 0; i < batch.Frames.Count; i++)
            Of(streamed[i]).Should().BeEquivalentTo(Of(batch.Frames[i]));
    }

    [Fact]
    public async Task Header_Populated_FromDateAndBaseLines()
    {
        var source = new AscStreamingSource(() => AscStream(ThreeFrames));
        var result = await source.OpenAsync();
        result.WallClockOrigin.Should().NotBeNull();
        result.TimestampsAreAbsolute.Should().BeTrue();
    }

    [Fact]
    public async Task MalformedLine_Skipped_AndCounted()
    {
        const string asc = @"
 0.000000 51  100  8  11 22 33 44 55 66 77 88
 this is not a frame
 0.500000 51  200  4  AA BB CC DD
";
        var source = new AscStreamingSource(() => AscStream(asc));
        var result = await source.OpenAsync();
        var frames = await ConsumeAll(result.Frames);
        frames.Should().HaveCount(2);
        result.Stats.SkippedLines.Should().Be(1);
    }

    [Fact]
    public async Task NoParseableFrames_ThrowsReplayFormatException_AtEnumerationEnd()
    {
        const string asc = "date Wed Jun 28 10:00:00.000 2026\nbase 0x7e0 500k\n";
        var source = new AscStreamingSource(() => AscStream(asc));
        var result = await source.OpenAsync();
        var act = () => ConsumeAll(result.Frames);
        await act.Should().ThrowAsync<ReplayFormatException>();
    }

    [Fact]
    public async Task OverHalfMalformed_ThrowsReplayFormatException()
    {
        const string asc = @"
 0.000000 51  100  8  11 22 33 44 55 66 77 88
 garbage 1
 garbage 2
 garbage 3
";
        var source = new AscStreamingSource(() => AscStream(asc));
        var result = await source.OpenAsync();
        var act = () => ConsumeAll(result.Frames);
        await act.Should().ThrowAsync<ReplayFormatException>();
    }
}
```

- [ ] **Step 3: 跑测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests --filter "FullyQualifiedName~AscStreamingSourceTests"
```
Expected: 编译失败（`AscStreamingSource` 不存在）。

- [ ] **Step 4: 实现 AscStreamingSource**

`src/PeakCan.Host.Core/Replay/Streaming/AscStreamingSource.cs`：
```csharp
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.Core.Replay;

/// <summary>
/// Streaming ASC source: opens the stream, eagerly reads header lines
/// (<c>date</c>/<c>base</c>) up to the first data line, then lazily yields
/// <see cref="ReplayFrame"/>s line-by-line — never materializing the whole
/// file. <c>date</c>/<c>base</c> lines appearing AFTER the first data line
/// are ignored (batch parser catches them anywhere; pathological for real
/// files — documented divergence). Malformed lines are skipped and counted
/// in <see cref="StreamingParseStats.SkippedLines"/>. The ">50% malformed"
/// and "no parseable frames" guards throw <see cref="ReplayFormatException"/>
/// at enumeration end (timing-equivalent to batch, which also reads all
/// lines before throwing).
/// </summary>
public sealed class AscStreamingSource : IStreamingTraceSource
{
    private readonly Func<Stream> _streamFactory;
    private readonly ILogger _logger;

    public AscStreamingSource(Func<Stream> streamFactory, ILogger? logger = null)
    {
        _streamFactory = streamFactory ?? throw new ArgumentNullException(nameof(streamFactory));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public async Task<StreamingTraceOpenResult> OpenAsync(CancellationToken ct = default)
    {
        Stream stream = _streamFactory();
        long? length = stream.CanSeek ? stream.Length : null;
        var stats = new StreamingParseStats();
        var reader = new StreamReader(stream, leaveOpen: true);

        DateTime? origin = null;
        bool absolute = false;
        string? firstDataLine = null;

        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            AddBytes(stats, line);
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith("//", StringComparison.Ordinal)) continue;
            if (t.StartsWith("date ", StringComparison.Ordinal)) { origin ??= AscFormat.TryParseDateHeader(t); continue; }
            if (t.StartsWith("base ", StringComparison.Ordinal)) { absolute = t.Contains("absolute", StringComparison.OrdinalIgnoreCase); continue; }
            if (t.StartsWith("internal events", StringComparison.Ordinal)) continue;
            if (AscFormat.LineIsSectionDelimiter(t)) continue;
            firstDataLine = t; // 第一个候选数据行（也可能畸形）
            break;
        }

        var frames = Enumerate(reader, firstDataLine, stats, ct);
        return new StreamingTraceOpenResult(origin, absolute, frames, length, stats);
    }

    private async IAsyncEnumerable<ReplayFrame> Enumerate(
        StreamReader reader, string? firstDataLine, StreamingParseStats stats,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        long emitted = 0, malformed = 0, dataLines = 0;
        try
        {
            // 先处理 OpenAsync 已读到的首个候选数据行
            if (firstDataLine is not null)
            {
                if (AscFormat.TryParseDataLine(firstDataLine, out var f0, out _)) { emitted++; yield return f0; }
                else { malformed++; dataLines++; stats.IncSkipped(); }
            }

            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                AddBytes(stats, line);
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith("//", StringComparison.Ordinal)) continue;
                if (t.StartsWith("date ", StringComparison.Ordinal)) continue;
                if (t.StartsWith("base ", StringComparison.Ordinal)) continue;
                if (t.StartsWith("internal events", StringComparison.Ordinal)) continue;
                if (AscFormat.LineIsSectionDelimiter(t)) continue;

                dataLines++;
                if (AscFormat.TryParseDataLine(t, out var frame, out _)) { emitted++; yield return frame; }
                else { malformed++; stats.IncSkipped(); }
            }
        }
        finally
        {
            reader.Dispose();
        }

        // 与批量解析器等价的尾部校验
        if (emitted == 0)
            throw new ReplayFormatException(
                $"ASC file has no parseable frames (saw {dataLines} data lines, all malformed).");
        if (dataLines > 0 && (double)malformed / dataLines > 0.5)
            throw new ReplayFormatException(
                $"ASC file appears corrupted ({malformed}/{dataLines} = {100.0 * malformed / dataLines:F0}% malformed).");
    }

    private static void AddBytes(StreamingParseStats stats, string line)
    {
        // UTF-8 字节近似：line.Length 是 UTF-16 char 数；多数 ASC 为 ASCII → 1B/char。
        // 只用于进度估算，不要求精确。
        stats.AddBytes(line.Length + 1);
    }
}
```

- [ ] **Step 5: 跑测试确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests --filter "FullyQualifiedName~AscStreamingSourceTests"
```
Expected: 5 passed。

- [ ] **Step 6: 跑 Core 全量回归确认无回归**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests
```
Expected: 全绿（additive，不应影响既有测试）。

- [ ] **Step 7: Commit**

```bash
git add src/PeakCan.Host.Core/Replay/Streaming tests/PeakCan.Host.Core.Tests/Replay/Streaming
git commit -m "feat(core): add streaming ASC source (additive, TDD parity with batch parser)"
```

---

## Task 4: StreamingTracePlayer（TDD 假时钟）

**Files:**
- Create: `src/PeakCan.Host.Core/Replay/Streaming/IStreamingTracePlayer.cs`
- Create: `src/PeakCan.Host.Core/Replay/Streaming/StreamingTracePlayer.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Replay/Streaming/StreamingTracePlayerTests.cs`
- Test helper: `tests/PeakCan.Host.Core.Tests/Replay/Streaming/RecordingReplayClock.cs`

**Interfaces:**
- Consumes: `IStreamingTraceSource`（Task 3）、`IReplayClock`（Core）、`ReplayState{Stopped,Playing,Paused}`、`PlaybackEndedEventArgs(Exception?)`、`ReplayFrame`
- Produces: `IStreamingTracePlayer`（`PlayAsync/Pause/Resume/SetSpeed/SeekAsync/Stop`，事件 `FrameEmitted/PlaybackEnded/SeekProgress`，属性 `State/CurrentTimestamp/Speed`）

- [ ] **Step 1: 写 IStreamingTracePlayer 接口**

`src/PeakCan.Host.Core/Replay/Streaming/IStreamingTracePlayer.cs`：
```csharp
namespace PeakCan.Host.Core.Replay;

/// <summary>
/// Streaming playback engine: pulls frames from an <see cref="IStreamingTraceSource"/>,
/// pacing emission by each frame's timestamp × <see cref="Speed"/>. Read-only
/// (no bus writes). <see cref="SeekAsync"/> re-opens the source and fast-forwards
/// to the target timestamp without emitting skipped frames.
/// </summary>
public interface IStreamingTracePlayer : IDisposable
{
    ReplayState State { get; }
    double CurrentTimestamp { get; }
    double Speed { get; }

    event Action<ReplayFrame>? FrameEmitted;
    event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded;
    event Action<double>? SeekProgress;

    /// <summary>Start or resume playback. Completes when playback reaches EOF, is stopped, or fails.</summary>
    Task PlayAsync(CancellationToken ct = default);
    void Pause();
    void Resume();
    void SetSpeed(double multiplier);
    /// <summary>Re-open the source and resume from <paramref name="timestamp"/>. Returns to the pre-seek play state (Playing→续播, Paused→停在该帧).</summary>
    Task SeekAsync(double timestamp, CancellationToken ct = default);
    void Stop();
}
```

- [ ] **Step 2: 写 RecordingReplayClock 测试助手**

`tests/PeakCan.Host.Core.Tests/Replay/Streaming/RecordingReplayClock.cs`：
```csharp
using System.Collections.Concurrent;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Tests.Replay.Streaming;

/// <summary>
/// IReplayClock that records every Delay duration requested and advances
/// Now synchronously. Lets tests assert pacing math (delay sizes) without
/// wall-clock waits. Pause semantics: <see cref="Delay"/> always completes
/// synchronously.
/// </summary>
public sealed class RecordingReplayClock : IReplayClock
{
    private DateTime _now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public DateTime Now => _now;
    public IReadOnlyList<TimeSpan> RecordedDelays => _delays;
    private readonly List<TimeSpan> _delays = new();

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        _delays.Add(delay > TimeSpan.Zero ? delay : TimeSpan.Zero);
        _now += delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        return Task.CompletedTask;
    }

    public IDisposable CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => throw new NotSupportedException("StreamingTracePlayer does not use timers — clock only needs Now + Delay.");
}
```

- [ ] **Step 3: 写失败测试**

`tests/PeakCan.Host.Core.Tests/Replay/Streaming/StreamingTracePlayerTests.cs`：
```csharp
using FluentAssertions;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Replay.Streaming;

public class StreamingTracePlayerTests
{
    // 三帧：0.0 / 0.5 / 1.0 秒
    private static IStreamingTraceSource MakeSource(params (double Ts, uint Id)[] frames)
    {
        var asc = string.Join('\n', frames.Select(f => $" {f.Ts:F6} 51  {f.Id:X3}  2  01 02"));
        return new FuncSource(() => AscStream(asc));
    }
    private static MemoryStream AscStream(string content)
        => new(System.Text.Encoding.UTF8.GetBytes(content));

    private static List<ReplayFrame> Capture(IStreamingTracePlayer p)
    {
        var list = new List<ReplayFrame>();
        p.FrameEmitted += f => list.Add(f);
        return list;
    }

    [Fact]
    public async Task PlayAsync_EmitsAllFramesInOrder_ThenEof()
    {
        var src = MakeSource((0, 0x100), (0.5, 0x200), (1.0, 0x300));
        using var player = new StreamingTracePlayer(src, new RecordingReplayClock());
        var captured = Capture(player);
        PlaybackEndedEventArgs? ended = null;
        player.PlaybackEnded += (_, e) => ended = e;

        await player.PlayAsync();

        captured.Should().HaveCount(3);
        captured.Select(f => f.Timestamp).Should().BeEquivalentTo(new[] { 0d, 0.5, 1.0 });
        ended.Should().NotBeNull();
        ended!.Error.Should().BeNull();
        player.State.Should().Be(ReplayState.Stopped);
    }

    [Fact]
    public async Task Pacing_DelaysMatchTimestamps_At1x()
    {
        var src = MakeSource((0, 0x100), (0.5, 0x200), (1.0, 0x300));
        var clock = new RecordingReplayClock();
        using var player = new StreamingTracePlayer(src, clock);
        Capture(player);

        await player.PlayAsync();

        // 帧间 delta 0.5/0.5 秒，1x → delays 0.5/0.5（首帧 due=now → 不 delay）
        // RecordingReplayClock 对 <=0 也记一条 zero（取决于实现）；断言存在 0.5s 两条
        clock.RecordedDelays.Where(d => d == TimeSpan.FromSeconds(0.5))
            .Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task SetSpeed2x_HalvesDelays()
    {
        var src = MakeSource((0, 0x100), (1.0, 0x200));
        var clock = new RecordingReplayClock();
        using var player = new StreamingTracePlayer(src, clock);
        player.SetSpeed(2.0);
        Capture(player);

        await player.PlayAsync();

        // delta 1.0s / speed 2 → delay 0.5s
        clock.RecordedDelays.Should().Contain(TimeSpan.FromSeconds(0.5));
    }

    [Fact]
    public async Task PauseBeforePlay_BlocksEmission_ThenResumeReleases()
    {
        var src = MakeSource((0, 0x100), (0.5, 0x200));
        using var player = new StreamingTracePlayer(src, new RecordingReplayClock());
        var captured = Capture(player);
        player.Pause();

        var playTask = player.PlayAsync();
        await Task.Delay(50); // 给 run loop 进到 pause gate

        captured.Should().BeEmpty(); // 被 pause gate 挡住，没 emit

        player.Resume();
        await playTask;

        captured.Should().HaveCount(2);
    }

    [Fact]
    public async Task SeekAsync_FastForwardsToTarget_EmitsFromThere()
    {
        var src = MakeSource((0, 0x100), (0.5, 0x200), (1.0, 0x300), (1.5, 0x400));
        using var player = new StreamingTracePlayer(src, new RecordingReplayClock());
        var captured = Capture(player);

        var playTask = player.PlayAsync();
        await Task.Delay(50);
        await player.SeekAsync(1.0);
        await playTask;

        // 跳到 1.0 → 只 emit >= 1.0 的帧（1.0, 1.5），跳过 0.0/0.5
        captured.Select(f => f.Timestamp).Should().BeEquivalentTo(new[] { 1.0, 1.5 });
    }

    [Fact]
    public async Task SourceThrows_ReportsErrorViaPlaybackEnded()
    {
        var src = new ThrowingSource(() => throw new InvalidOperationException("boom"));
        using var player = new StreamingTracePlayer(src, new RecordingReplayClock());
        PlaybackEndedEventArgs? ended = null;
        player.PlaybackEnded += (_, e) => ended = e;

        await player.PlayAsync();

        ended.Should().NotBeNull();
        ended!.Error.Should().BeOfType<InvalidOperationException>();
    }

    // —— 测试用 fake source ——
    private sealed class FuncSource : IStreamingTraceSource
    {
        private readonly Func<Stream> _factory;
        public FuncSource(Func<Stream> factory) => _factory = factory;
        public Task<StreamingTraceOpenResult> OpenAsync(CancellationToken ct = default)
        {
            var s = new AscStreamingSource(_factory);
            return s.OpenAsync(ct);
        }
    }
    private sealed class ThrowingSource : IStreamingTraceSource
    {
        private readonly Func<Stream> _factory;
        public ThrowingSource(Func<Stream> factory) => _factory = factory;
        public Task<StreamingTraceOpenResult> OpenAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
    }
}
```

- [ ] **Step 4: 跑测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests --filter "FullyQualifiedName~StreamingTracePlayerTests"
```
Expected: 编译失败（`StreamingTracePlayer` 不存在）。

- [ ] **Step 5: 实现 StreamingTracePlayer**

`src/PeakCan.Host.Core/Replay/Streaming/StreamingTracePlayer.cs`：
```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeakCan.Host.Core.Replay;

/// <summary>
/// Streaming playback engine. Pulls frames from <see cref="IStreamingTraceSource"/>,
/// paces each by its timestamp × <see cref="Speed"/> using <see cref="IReplayClock"/>.
/// Backpressure-free vs UI: emits as fast as pacing allows; the consumer
/// (VM) is responsible for batching UI updates. <see cref="Pause"/> blocks the
/// run loop on a semaphore gate; <see cref="Resume"/> releases and re-anchors
/// the clock so resumed playback does not burst-emit to catch up.
/// </summary>
public sealed class StreamingTracePlayer : IStreamingTracePlayer
{
    private readonly IStreamingTraceSource _source;
    private readonly IReplayClock _clock;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _pauseGate = new(1, 1);
    private readonly object _lifecycleLock = new();

    private CancellationTokenSource? _runCts;
    private double _speed = 1.0;
    private double _currentTimestamp;
    private double? _seekTarget;     // pending seek position
    private bool _reanchorRequested = true;
    private ReplayState _state = ReplayState.Stopped;
    private bool _disposed;

    public StreamingTracePlayer(IStreamingTraceSource source, IReplayClock? clock = null, ILogger? logger = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _clock = clock ?? new WallClockReplayClock();
        _logger = logger ?? NullLogger.Instance;
    }

    public ReplayState State { get { lock (_lifecycleLock) return _state; } }
    public double CurrentTimestamp { get { lock (_lifecycleLock) return _currentTimestamp; } }
    public double Speed { get { lock (_lifecycleLock) return _speed; } }

    public event Action<ReplayFrame>? FrameEmitted;
    public event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded;
    public event Action<double>? SeekProgress;

    public async Task PlayAsync(CancellationToken ct = default)
    {
        lock (_lifecycleLock)
        {
            if (_state == ReplayState.Playing) return;
            if (_state == ReplayState.Paused) { ResumeImpl(); return; }
            _state = ReplayState.Playing;
            _reanchorRequested = true;
        }

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                double startFrom;
                lock (_lifecycleLock)
                {
                    _runCts = runCts;
                    startFrom = _seekTarget ?? _currentTimestamp;
                    _seekTarget = null;
                }

                var outcome = await RunLoopAsync(startFrom, runCts.Token).ConfigureAwait(false);

                lock (_lifecycleLock) _runCts = null;

                switch (outcome.Kind)
                {
                    case RunOutcome.Eof:
                    case RunOutcome.Stopped:
                        _state = ReplayState.Stopped;
                        if (outcome.Kind == RunOutcome.Stopped) _currentTimestamp = 0;
                        PlaybackEnded?.Invoke(this, new PlaybackEndedEventArgs());
                        return;
                    case RunOutcome.Failed:
                        _state = ReplayState.Stopped;
                        PlaybackEnded?.Invoke(this, new PlaybackEndedEventArgs(outcome.Error));
                        return;
                    case RunOutcome.SeekRequested:
                        continue; // 续：startFrom 在下一轮从 _seekTarget 取
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { lock (_lifecycleLock) { if (_state == ReplayState.Playing) _state = ReplayState.Stopped; } }
    }

    public void Pause()
    {
        bool wasPlaying;
        lock (_lifecycleLock) { wasPlaying = _state == ReplayState.Playing; if (wasPlaying) _state = ReplayState.Paused; }
        if (wasPlaying) _pauseGate.Wait(); // 取走 gate，run loop 将在下一帧阻塞
    }

    public void Resume()
    {
        bool wasPaused;
        lock (_lifecycleLock) { wasPaused = _state == ReplayState.Paused; if (wasPaused) { _state = ReplayState.Playing; _reanchorRequested = true; } }
        if (wasPaused) _pauseGate.Release();
    }
    private void ResumeImpl() { _state = ReplayState.Playing; _reanchorRequested = true; _pauseGate.Release(); }

    public void SetSpeed(double multiplier)
    {
        if (multiplier <= 0) throw new ArgumentOutOfRangeException(nameof(multiplier));
        lock (_lifecycleLock) { _speed = multiplier; _reanchorRequested = true; }
    }

    public Task SeekAsync(double timestamp, CancellationToken ct = default)
    {
        lock (_lifecycleLock)
        {
            if (_state == ReplayState.Stopped)
            {
                _currentTimestamp = Math.Max(0, timestamp);
                _seekTarget = null;
                return Task.CompletedTask;
            }
            _seekTarget = Math.Max(0, timestamp);
        }
        _runCts?.Cancel();
        return Task.CompletedTask;
    }

    public void Stop()
    {
        lock (_lifecycleLock) { if (_state == ReplayState.Stopped) return; _state = ReplayState.Stopped; _seekTarget = null; }
        _runCts?.Cancel();
        try { _pauseGate.Release(); } catch (SemaphoreFullException) { }
    }

    private async Task<RunOutcome> RunLoopAsync(double startFrom, CancellationToken ct)
    {
        StreamingTraceOpenResult session;
        try { session = await _source.OpenAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return RunOutcome.SeekOrCancel(_seekTarget); }
        catch (Exception ex) { return RunOutcome.Failed(ex); }

        DateTime anchorClock = default;
        double anchorTs = 0;
        bool anchored = false;
        bool fastForwarding = startFrom > 0;

        try
        {
            await foreach (var frame in session.Frames.WithCancellation(ct).ConfigureAwait(false))
            {
                if (fastForwarding)
                {
                    if (frame.Timestamp < startFrom)
                    {
                        if (session.SourceLengthBytes is > 0)
                            SeekProgress?.Invoke((double)session.Stats.BytesRead / session.SourceLengthBytes.Value);
                        continue;
                    }
                    fastForwarding = false;
                    _reanchorRequested = true; // seek 后重新锚定
                }

                // Pause gate：paused 时阻塞在此
                await _pauseGate.WaitAsync(ct).ConfigureAwait(false);
                _pauseGate.Release();

                lock (_lifecycleLock)
                {
                    if (_reanchorRequested || !anchored)
                    {
                        anchorClock = _clock.Now;
                        anchorTs = frame.Timestamp;
                        anchored = true;
                        _reanchorRequested = false;
                    }
                }

                var due = anchorClock + TimeSpan.FromSeconds((frame.Timestamp - anchorTs) / Speed);
                var remaining = due - _clock.Now;
                if (remaining > TimeSpan.Zero)
                    await _clock.Delay(remaining, ct).ConfigureAwait(false);

                lock (_lifecycleLock) _currentTimestamp = frame.Timestamp;
                FrameEmitted?.Invoke(frame);
            }
            return RunOutcome.Eof;
        }
        catch (OperationCanceledException)
        {
            lock (_lifecycleLock)
            {
                if (_seekTarget.HasValue) return RunOutcome.SeekOrCancel(_seekTarget);
            }
            return RunOutcome.Stopped;
        }
        catch (Exception ex) { return RunOutcome.Failed(ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _pauseGate.Dispose();
    }

    private readonly record struct RunOutcome(RunOutcomeKind Kind, Exception? Error, double? Seek)
    {
        public static readonly RunOutcome Eof = new(RunOutcomeKind.Eof, null, null);
        public static readonly RunOutcome Stopped = new(RunOutcomeKind.Stopped, null, null);
        public static RunOutcome Failed(Exception ex) => new(RunOutcomeKind.Failed, ex, null);
        public static RunOutcome SeekOrCancel(double? seek) => new(RunOutcomeKind.SeekRequested, null, seek);
    }
    private enum RunOutcomeKind { Eof, Stopped, Failed, SeekRequested }
}
```

- [ ] **Step 6: 跑测试确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests --filter "FullyQualifiedName~StreamingTracePlayerTests"
```
Expected: 6 passed。若 `PauseBeforePlay`/`SeekAsync` 因异步时序 flaky，在 `await Task.Delay(50)` 处加注释说明或用 `await Task.Yield()` 重试；确定性优先于消除"给 run loop 一点时间"。

- [ ] **Step 7: 跑 Core 全量回归**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests
```
Expected: 全绿。

- [ ] **Step 8: Commit**

```bash
git add src/PeakCan.Host.Core/Replay/Streaming tests/PeakCan.Host.Core.Tests/Replay/Streaming
git commit -m "feat(core): add StreamingTracePlayer (TDD, fake clock)"
```

---

## Task 5: DurationScanner（Mobile.Core，TDD）

**Files:**
- Create: `src/PeakCan.Host.Mobile.Core/Services/DurationScanResult.cs`
- Create: `src/PeakCan.Host.Mobile.Core/Services/DurationScanner.cs`
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/Services/DurationScannerTests.cs`

**Interfaces:**
- Consumes: `AscFormat.TryParseDataLine/TryParseDateHeader`（Host.Core，public）
- Produces: `DurationScanResult(double DurationSeconds, long FrameCount, DateTime? WallClockOrigin)`、`DurationScanner.ScanAsync(Stream, IProgress<double>?, CancellationToken)`

- [ ] **Step 1: 写失败测试**

`tests/PeakCan.Host.Mobile.Core.Tests/Services/DurationScannerTests.cs`：
```csharp
using System.Text;
using FluentAssertions;
using PeakCan.Host.Mobile.Core.Services;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.Services;

public class DurationScannerTests
{
    private static Stream Asc(string content) => new MemoryStream(Encoding.UTF8.GetBytes(content));

    [Fact]
    public async Task ScanAsync_ReportsDurationAndFrameCount()
    {
        const string asc = @"
date Wed Jun 28 10:00:00.000 2026
 0.000000 51  100  2  01 02
 0.500000 51  200  2  03 04
 2.000000 51  100  2  05 06
";
        var result = await DurationScanner.ScanAsync(Asc(asc));
        result.FrameCount.Should().Be(3);
        result.DurationSeconds.Should().Be(2.0);
        result.WallClockOrigin.Should().NotBeNull();
    }

    [Fact]
    public async Task ScanAsync_EmptyStream_ReturnsZero()
    {
        var result = await DurationScanner.ScanAsync(Asc(""));
        result.FrameCount.Should().Be(0);
        result.DurationSeconds.Should().Be(0);
    }
}
```

- [ ] **Step 2: 跑确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.Mobile.Core.Tests --filter "FullyQualifiedName~DurationScannerTests"
```
Expected: 编译失败。

- [ ] **Step 3: 实现**

`src/PeakCan.Host.Mobile.Core/Services/DurationScanResult.cs`：
```csharp
namespace PeakCan.Host.Mobile.Core.Services;

public sealed record DurationScanResult(double DurationSeconds, long FrameCount, DateTime? WallClockOrigin);
```

`src/PeakCan.Host.Mobile.Core/Services/DurationScanner.cs`：
```csharp
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>
/// 只读扫描 ASC 文件确定总时长 + 帧数 + wall-clock origin，**不保留帧对象**
/// （transient ReplayFrame 经 GC 回收）。用于流式回放下让时间轴 slider 获得量程。
/// 帧序假设时间有序（ASC 天然如此）→ Duration = last - first；乱序文件为近似值，
/// 对 slider 量程足够。实现复用 <see cref="AscFormat.TryParseDataLine"/>
/// 保持单源语义。
/// </summary>
public static class DurationScanner
{
    public static async Task<DurationScanResult> ScanAsync(
        Stream stream, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        long total = stream.CanSeek ? stream.Length : 0;
        long bytesRead = 0;
        long count = 0;
        double first = double.NaN, last = 0;
        DateTime? origin = null;

        using var reader = new StreamReader(stream, leaveOpen: true);
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            bytesRead += line.Length + 1;
            var t = line.Trim();
            if (t.StartsWith("date ", StringComparison.Ordinal))
                origin ??= AscFormat.TryParseDateHeader(t);
            if (AscFormat.TryParseDataLine(t, out var frame, out _))
            {
                if (count == 0) first = frame.Timestamp;
                last = frame.Timestamp;
                count++;
            }
            if (progress is not null && total > 0 && (count & 0x3FFF) == 0 && count > 0)
                progress.Report((double)bytesRead / total);
        }
        if (progress is not null && total > 0) progress.Report(1.0);

        double duration = count > 0 && !double.IsNaN(first) ? last - first : 0;
        return new DurationScanResult(duration, count, origin);
    }
}
```

- [ ] **Step 4: 跑确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.Mobile.Core.Tests --filter "FullyQualifiedName~DurationScannerTests"
```
Expected: 2 passed。

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Mobile.Core/Services tests/PeakCan.Host.Mobile.Core.Tests/Services
git commit -m "feat(mobile): add DurationScanner (streaming total-duration scan, TDD)"
```

---

## Task 6: 平台抽象 + TraceFileCache（TDD）

**Files:**
- Create: `src/PeakCan.Host.Mobile.Core/Platform/IUiDispatcher.cs`
- Create: `src/PeakCan.Host.Mobile.Core/Platform/IFilePickerGateway.cs`
- Create: `src/PeakCan.Host.Mobile.Core/Services/TraceFileCache.cs`
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/Services/TraceFileCacheTests.cs`

**Interfaces:**
- Produces: `IUiDispatcher{Post,StartTimer}`、`IFilePickerGateway{CacheDirectory,PickTraceFileAsync}`、`PickedTraceFile`、`TraceFileCache(string cacheDir)`（`FindCached/ImportAsync`）

- [ ] **Step 1: 写抽象接口**

`src/PeakCan.Host.Mobile.Core/Platform/IUiDispatcher.cs`：
```csharp
namespace PeakCan.Host.Mobile.Core.Platform;

/// <summary>UI-thread marshalling + recurring UI-thread timer abstraction.
/// Implemented per-platform (MAUI on Android). VM depends on this interface,
/// not MAUI Essentials, so it is unit-testable under plain net10.0.</summary>
public interface IUiDispatcher
{
    /// <summary>Run <paramref name="action"/> on the UI thread (inline if already on it).</summary>
    void Post(Action action);
    /// <summary>Start a recurring UI-thread timer firing <paramref name="tick"/> every <paramref name="period"/>. Dispose to stop.</summary>
    IDisposable StartTimer(TimeSpan period, Action tick);
}
```

`src/PeakCan.Host.Mobile.Core/Platform/IFilePickerGateway.cs`：
```csharp
namespace PeakCan.Host.Mobile.Core.Platform;

public sealed record PickedTraceFile(string DisplayName, long SizeBytes, Func<Stream> OpenRead);

/// <summary>File picking + cache-directory provider. Abstracted so VM/tests
/// don't depend on MAUI FilePicker/FileSystem APIs directly.</summary>
public interface IFilePickerGateway
{
    string CacheDirectory { get; }
    /// <summary>Open the system file picker for trace files. Returns null if the user cancelled.</summary>
    Task<PickedTraceFile?> PickTraceFileAsync(CancellationToken ct = default);
}
```

- [ ] **Step 2: 写 TraceFileCache 失败测试**

`tests/PeakCan.Host.Mobile.Core.Tests/Services/TraceFileCacheTests.cs`：
```csharp
using FluentAssertions;
using PeakCan.Host.Mobile.Core.Platform;
using PeakCan.Host.Mobile.Core.Services;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.Services;

public class TraceFileCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "peakcan-cache-test-" + Guid.NewGuid().ToString("N"));
    public TraceFileCacheTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static PickedTraceFile Pick(string name, byte[] content)
        => new(name, content.Length, () => new MemoryStream(content));

    [Fact]
    public async Task ImportAsync_WritesFile_AndReturnsPath()
    {
        var cache = new TraceFileCache(_dir);
        var path = await cache.ImportAsync(Pick("foo.asc", new byte[] { 1, 2, 3 }));
        File.Exists(path).Should().BeTrue();
        await File.ReadAllBytesAsync(path).Should().Equal(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task ImportAsync_SameNameAndSize_ReusesWithoutCopying()
    {
        var cache = new TraceFileCache(_dir);
        bool opened = false;
        var pick = new PickedTraceFile("foo.asc", 3, () => { opened = true; return new MemoryStream(new byte[] { 1, 2, 3 }); });

        var p1 = await cache.ImportAsync(pick);
        opened.Should().BeTrue("first import must copy");
        opened = false;
        var p2 = await cache.ImportAsync(pick);
        p2.Should().Be(p1);
        opened.Should().BeFalse("second import should not re-open the stream (cache hit)");
    }

    [Fact]
    public async Task FindCached_ReturnsNull_WhenAbsent()
    {
        var cache = new TraceFileCache(_dir);
        cache.FindCached("missing.asc", 123).Should().BeNull();
    }
}
```

- [ ] **Step 3: 跑确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.Mobile.Core.Tests --filter "FullyQualifiedName~TraceFileCacheTests"
```
Expected: 编译失败。

- [ ] **Step 4: 实现 TraceFileCache**

`src/PeakCan.Host.Mobile.Core/Services/TraceFileCache.cs`：
```csharp
using PeakCan.Host.Mobile.Core.Platform;

namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>
/// Copies picked trace files into a private cache directory keyed by
/// (displayName, sizeBytes). Same name+size → cache hit, stream not
/// re-opened (saves the 100MB copy). Matches spec §5 "同文件重开秒开".
/// </summary>
public sealed class TraceFileCache
{
    private readonly string _cacheDir;
    public TraceFileCache(string cacheDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheDir);
        _cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
    }

    public string? FindCached(string displayName, long sizeBytes)
    {
        var path = PathOf(displayName, sizeBytes);
        return File.Exists(path) ? path : null;
    }

    public async Task<string> ImportAsync(PickedTraceFile file, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var path = PathOf(file.DisplayName, file.SizeBytes);
        if (File.Exists(path)) return path; // cache hit — 不再读流

        var tmp = path + ".partial";
        await using (var src = file.OpenRead())
        await using (var dst = File.Create(tmp))
        {
            var buffer = new byte[256 * 1024];
            long copied = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                copied += n;
                if (file.SizeBytes > 0) progress?.Report((double)copied / file.SizeBytes);
            }
        }
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    private string PathOf(string name, long size) =>
        Path.Combine(_cacheDir, $"{Sanitize(name)}.{size}.asc");

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString();
    }
}
```

- [ ] **Step 5: 跑确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.Mobile.Core.Tests --filter "FullyQualifiedName~TraceFileCacheTests"
```
Expected: 3 passed。

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.Mobile.Core/Platform src/PeakCan.Host.Mobile.Core/Services tests/PeakCan.Host.Mobile.Core.Tests/Services
git commit -m "feat(mobile): add IUiDispatcher/IFilePickerGateway abstractions + TraceFileCache (TDD)"
```

---

## Task 7: TraceSessionViewModel（TDD）

**Files:**
- Create: `src/PeakCan.Host.Mobile.Core/Platform/IStreamingSourceFactory.cs`（可选委托类型；或直接用 `Func<string, IStreamingTraceSource>`）
- Create: `src/PeakCan.Host.Mobile.Core/ViewModels/TraceSessionViewModel.cs`
- Create: `src/PeakCan.Host.Mobile.Core/ViewModels/SessionState.cs`
- Create: `tests/PeakCan.Host.Mobile.Core.Tests/ViewModels/TraceSessionViewModelTests.cs`
- Test helpers: `tests/PeakCan.Host.Mobile.Core.Tests/Fakes/FakeUiDispatcher.cs`、`Fakes/FakeStreamingTracePlayer.cs`、`Fakes/FakeSourceFactory.cs`

**Interfaces:**
- Consumes: `IStreamingTracePlayer`（Task 4）、`IStreamingTraceSource`（Task 3）、`IUiDispatcher`（Task 6）、`DurationScanner`（Task 5）、`FrameRingBuffer`/`FrameRow`（Task 2）、`CanIdListParser.Parse`（Core）
- Produces: `TraceSessionViewModel`（`OpenAsync(path)`、`TogglePlay`、`SeekTo(double)`、`SetSpeed(double)`、`SetIdFilter(string)`、`VisibleRows`、`State`、`CurrentTimeText`、`DurationText`、`Progress01`、`IsSeekBusy`、`Stop`）

- [ ] **Step 1: 写 SessionState + fakes 骨架**

`src/PeakCan.Host.Mobile.Core/ViewModels/SessionState.cs`：
```csharp
namespace PeakCan.Host.Mobile.Core.ViewModels;

public enum SessionState { Empty, Ready, Playing, Paused, Seeking, Ended, Failed }
```

`tests/PeakCan.Host.Mobile.Core.Tests/Fakes/FakeUiDispatcher.cs`：
```csharp
using PeakCan.Host.Mobile.Core.Platform;

namespace PeakCan.Host.Mobile.Core.Tests.Fakes;

/// <summary>Inline dispatcher + controllable timer. Post runs immediately; StartTimer fires tick only on Tick().</summary>
public sealed class FakeUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
    public IDisposable StartTimer(TimeSpan period, Action tick) => new FakeTimer(tick);
    public sealed class FakeTimer : IDisposable
    {
        private readonly Action _tick; private bool _disposed;
        public FakeTimer(Action tick) => _tick = tick;
        public void Tick() { if (!_disposed) _tick(); }
        public void Dispose() => _disposed = true;
    }
}
```

`tests/PeakCan.Host.Mobile.Core.Tests/Fakes/FakeStreamingTracePlayer.cs`：
```csharp
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Mobile.Core.Tests.Fakes;

/// <summary>Records calls + lets tests drive emission/events. Implements the contract loosely enough for VM tests.</summary>
public sealed class FakeStreamingTracePlayer : IStreamingTracePlayer
{
    public ReplayState State { get; set; } = ReplayState.Stopped;
    public double CurrentTimestamp { get; set; }
    public double Speed { get; set; } = 1.0;
    public event Action<ReplayFrame>? FrameEmitted;
    public event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded;
    public event Action<double>? SeekProgress;
    public int PlayCount, PauseCount, ResumeCount, StopCount;
    public List<double> Seeks { get; } = new();

    public Task PlayAsync(CancellationToken ct = default) { PlayCount++; State = ReplayState.Playing; return Task.CompletedTask; }
    public void Pause() { PauseCount++; State = ReplayState.Paused; }
    public void Resume() { ResumeCount++; State = ReplayState.Playing; }
    public void SetSpeed(double m) => Speed = m;
    public Task SeekAsync(double t, CancellationToken ct = default) { Seeks.Add(t); return Task.CompletedTask; }
    public void Stop() { StopCount++; State = ReplayState.Stopped; }

    // 测试驱动器：模拟真实播放器 emit 一帧
    public void Emit(ReplayFrame f) { CurrentTimestamp = f.Timestamp; FrameEmitted?.Invoke(f); }
    public void EmitEof() => PlaybackEnded?.Invoke(this, new PlaybackEndedEventArgs());

    public void Dispose() { }
}
```

- [ ] **Step 2: 写失败测试**

`tests/PeakCan.Host.Mobile.Core.Tests/ViewModels/TraceSessionViewModelTests.cs`：
```csharp
using FluentAssertions;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Mobile.Core.Tests.Fakes;
using PeakCan.Host.Mobile.Core.ViewModels;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.ViewModels;

public class TraceSessionViewModelTests
{
    private static ReplayFrame F(double t, uint id) =>
        new(t, id, 2, new byte[] { 1, 2 }, default, false);

    private sealed class FakeSourceFactory : IStreamingSourceFactory
    {
        public IStreamingTraceSource LastSource = null!;
        public IStreamingTraceSource Create(string path)
            => LastSource = Substitute.For<IStreamingTraceSource>();
    }

    private sealed class Env
    {
        public readonly FakeUiDispatcher Ui = new();
        public readonly FakeStreamingTracePlayer Player = new();
        public readonly FakeSourceFactory SourceFactory = new();
        public readonly TraceSessionViewModel Vm;
        public FakeUiDispatcher.FakeTimer DrainTimer = null!;
        public Env()
        {
            // VM 接受注入：ui、sourceFactory、playerFactory（→ 固定返回 Player）
            Vm = new TraceSessionViewModel(Ui, SourceFactory, _ => Player);
            // 抓 drain timer
            var field = typeof(TraceSessionViewModel)
                .GetField("_drainTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            DrainTimer = (FakeUiDispatcher.FakeTimer)field!.GetValue(Vm)!;
        }
    }

    [Fact]
    public void InitialState_IsEmpty()
    {
        var env = new Env();
        env.Vm.State.Should().Be(SessionState.Empty);
        env.Vm.VisibleRows.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenAsync_PrefetchesFirstScreen_GoesReady()
    {
        var env = new Env();
        // fake source.OpenAsync → 返回头 + 惰性 2 帧
        var frames = new AsyncFrameSeq(F(0, 1), F(0.5, 2));
        env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));

        await env.Vm.OpenAsync("foo.asc");

        env.Vm.State.Should().Be(SessionState.Ready);
        env.Vm.VisibleRows.Should().HaveCount(2); // 第一屏已预读
    }

    [Fact]
    public void TogglePlay_CallsPlayer_AndTransitionsToPlaying()
    {
        var env = new Env();
        env.Vm.TogglePlayCommand.Execute(null);
        env.Player.PlayCount.Should().Be(1);
        env.Vm.State.Should().Be(SessionState.Playing);
    }

    [Fact]
    public void FrameEmitted_AccumulatesInPending_NotVisibleUntilDrainTick()
    {
        var env = new Env();
        env.Vm.MarkReadyForEmit(); // 测试 helper：置为 Playing + 清 ring
        env.Player.Emit(F(0.0, 0x100));
        env.Vm.VisibleRows.Should().BeEmpty(); // 还没 drain
        env.DrainTimer.Tick();
        env.Vm.VisibleRows.Should().HaveCount(1);
    }

    [Fact]
    public void RingBuffer_KeepsLatestN_FramesOnly()
    {
        var env = new Env();
        env.Vm.MarkReadyForEmit();
        for (int i = 0; i < 5500; i++) env.Player.Emit(F(i * 0.01, (uint)i));
        env.DrainTimer.Tick();
        env.Vm.VisibleRows.Should().HaveCount(5000); // 容量上限
    }

    [Fact]
    public void IdFilter_ExcludesNonMatchingFrames()
    {
        var env = new Env();
        env.Vm.MarkReadyForEmit();
        env.Vm.SetIdFilter("100");
        env.Player.Emit(F(0.0, 0x100)); // 匹配
        env.Player.Emit(F(0.1, 0x200)); // 不匹配 → 丢弃
        env.DrainTimer.Tick();
        env.Vm.VisibleRows.Should().HaveCount(1);
        env.Vm.VisibleRows[0].Id.Should().Be(0x100u);
    }

    [Fact]
    public void PlaybackEnded_Eof_TransitionsToEnded()
    {
        var env = new Env();
        env.Vm.TogglePlayCommand.Execute(null);
        env.Player.EmitEof();
        env.Vm.State.Should().Be(SessionState.Ended);
    }

    [Fact]
    public void SetSpeed_ForwardsToPlayer()
    {
        var env = new Env();
        env.Vm.SetSpeed(4.0);
        env.Player.Speed.Should().Be(4.0);
    }
}

// 测试用：可控的惰性帧流
internal sealed class AsyncFrameSeq
{
    private readonly ReplayFrame[] _frames;
    public AsyncFrameSeq(params ReplayFrame[] frames) => _frames = frames;
    public StreamingTraceOpenResult OpenResult =>
        new(null, false, Yield(), null, new StreamingParseStats());
    private async IAsyncEnumerable<ReplayFrame> Yield(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var f in _frames)
        {
            ct.ThrowIfCancellationRequested();
            yield return f;
        }
    }
}
```
注：`IStreamingSourceFactory`、`TraceSessionViewModel` 的构造、`MarkReadyForEmit`/`SetIdFilter`/`TogglePlayCommand`/`VisibleRows`/`State` 在 Step 3 实现。`Substitute.For`（NSubstitute）+ `ReturnsForAnyArgs` 已在测试项目引用。

- [ ] **Step 3: 跑确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.Mobile.Core.Tests --filter "FullyQualifiedName~TraceSessionViewModelTests"
```
Expected: 编译失败。

- [ ] **Step 4: 实现 TraceSessionViewModel**

`src/PeakCan.Host.Mobile.Core/Platform/IStreamingSourceFactory.cs`：
```csharp
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Mobile.Core.Platform;

/// <summary>Factory of <see cref="IStreamingTraceSource"/> for a file path. Abstracted for testability.</summary>
public interface IStreamingSourceFactory
{
    IStreamingTraceSource Create(string cachedFilePath);
}
```

`src/PeakCan.Host.Mobile.Core/ViewModels/TraceSessionViewModel.cs`：
```csharp
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Mobile.Core.Models;
using PeakCan.Host.Mobile.Core.Platform;
using PeakCan.Host.Mobile.Core.Services;

namespace PeakCan.Host.Mobile.Core.ViewModels;

/// <summary>
/// 单 trace session 的 UI 状态机。持有一个 <see cref="IStreamingTracePlayer"/>，
/// 在 FrameEmitted（播放器线程）上收集到 pending 队列，由 50ms UI 定时器 drain
/// 到环形缓冲并通知 VisibleRows 刷新（禁止逐帧刷 UI）。ID 过滤在 ingest 谓词处
/// 生效（播放中过滤，spec P1）。DurationText/Progress01 在 DurationScanner
/// 后台扫完后填充（打开文件时并行）。
/// </summary>
public sealed partial class TraceSessionViewModel : ObservableObject, IDisposable
{
    private readonly IUiDispatcher _ui;
    private readonly IStreamingSourceFactory _sourceFactory;
    private readonly Func<IStreamingTraceSource, IStreamingTracePlayer> _playerFactory;
    private readonly ILogger _logger;

    private readonly object _emitGate = new();
    private readonly List<ReplayFrame> _pending = new();
    private readonly FrameRingBuffer _ring = new(5000);

    private IStreamingTracePlayer? _player;
    private IReadOnlySet<uint>? _idFilter;
    private double _duration;
    private bool _durationKnown;
    private IDisposable? _drainTimer;
    private SessionState _state = SessionState.Empty;

    public TraceSessionViewModel(IUiDispatcher ui, IStreamingSourceFactory sourceFactory,
        Func<IStreamingTraceSource, IStreamingTracePlayer> playerFactory, ILogger? logger = null)
    {
        _ui = ui; _sourceFactory = sourceFactory; _playerFactory = playerFactory;
        _logger = logger ?? NullLogger.Instance;
    }

    public SessionState State { get => _state; private set { if (_state != value) { _state = value; OnPropertyChanged(); } } }

    [ObservableProperty] private string _currentTimeText = "00:00:00";
    [ObservableProperty] private string _durationText = "??:??";
    [ObservableProperty] private double _progress01;
    [ObservableProperty] private bool _isSeekBusy;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _idFilterText;

    public IReadOnlyList<FrameRow> VisibleRows => _ring.Snapshot();

    /// <summary>Open a cached file. Prefetch first screen (~200 frames) so the
    /// table shows data immediately in Ready state; kick DurationScanner in the
    /// background to populate the time-axis range.</summary>
    public async Task OpenAsync(string cachedFilePath, CancellationToken ct = default)
    {
        var source = _sourceFactory.Create(cachedFilePath);
        var open = await source.OpenAsync(ct);
        int prefetched = 0;
        await foreach (var f in open.Frames.WithCancellation(ct))
        {
            _ring.Add(FrameRow.FromReplayFrame(f));
            if (++prefetched >= 200) break;
        }
        State = SessionState.Ready;
        RaiseRowsChanged();
        _player = _playerFactory(source);
        _player.FrameEmitted += OnFrameEmitted;
        _player.PlaybackEnded += OnPlaybackEnded;
        _player.SeekProgress += OnSeekProgress;

        _ = Task.Run(async () =>
        {
            try
            {
                await using var fs = new FileStream(cachedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var scan = await DurationScanner.ScanAsync(fs, null, ct);
                _duration = scan.DurationSeconds;
                _durationKnown = true;
                _ui.Post(() => DurationText = FormatTime(_duration));
            }
            catch (Exception ex) { _logger.LogWarning(ex, "duration scan failed"); }
        }, ct);
    }

    private void OnFrameEmitted(ReplayFrame f)
    {
        lock (_emitGate) { if (PassesFilter(f)) _pending.Add(f); }
    }

    private bool PassesFilter(ReplayFrame f) => _idFilter is null || _idFilter.Contains(f.Id);

    private void Drain()
    {
        List<ReplayFrame> batch;
        lock (_emitGate) { if (_pending.Count == 0) return; batch = new List<ReplayFrame>(_pending); _pending.Clear(); }
        if (batch.Count == 0) return;
        foreach (var f in batch) _ring.Add(FrameRow.FromReplayFrame(f));
        if (batch.Count > 0) CurrentTimeText = FormatTime(batch[^1].Timestamp);
        if (_durationKnown && _duration > 0) Progress01 = batch[^1].Timestamp / _duration;
        RaiseRowsChanged();
    }

    private void RaiseRowsChanged() => OnPropertyChanged(nameof(VisibleRows));

    private void OnPlaybackEnded(object? sender, PlaybackEndedEventArgs e)
    {
        _ui.Post(() =>
        {
            if (e.Error is null) State = SessionState.Ended;
            else { State = SessionState.Failed; ErrorMessage = e.Error.Message; }
            _drainTimer?.Dispose(); _drainTimer = null;
            Drain();
        });
    }

    private void OnSeekProgress(double p) => _ui.Post(() => { IsSeekBusy = p < 1.0; Progress01 = p; });

    [RelayCommand]
    private void TogglePlay()
    {
        if (_player is null) return;
        if (State == SessionState.Playing) { _player.Pause(); State = SessionState.Paused; return; }
        _player.PlayAsync();
        State = SessionState.Playing;
        _drainTimer ??= _ui.StartTimer(TimeSpan.FromMilliseconds(50), Drain);
    }

    [RelayCommand]
    private void Stop()
    {
        _player?.Stop();
        _drainTimer?.Dispose(); _drainTimer = null;
        State = SessionState.Ended;
        Drain();
    }

    [RelayCommand]
    private void SeekTo(double timestamp)
    {
        if (_player is null) return;
        IsSeekBusy = true;
        _player.SeekAsync(timestamp);
    }

    partial void OnIdFilterTextChanged(string? value)
    {
        var parsed = CanIdListParser.Parse(value);
        _idFilter = parsed.AllowList;
        // 过滤变更后立即对现有 ring 重筛（P2 SQLite 路径在 P2 加；P1 仅 ring 内重筛不可能，
        // ring 只保留最近 5000 帧 → 这里只影响后续 emit；接受此限制）
    }

    /// <summary>设置 ID 过滤（十六进制，逗号分隔；空串=清除）。</summary>
    public void SetIdFilter(string text) { IdFilterText = string.IsNullOrWhiteSpace(text) ? null : text; }

    // —— 测试 helper（internal，InternalsVisibleTo 测试项目）——
    internal void MarkReadyForEmit()
    {
        State = SessionState.Playing;
        _ring.Clear();
        _drainTimer ??= _ui.StartTimer(TimeSpan.FromMilliseconds(50), Drain);
    }

    private static string FormatTime(double seconds) =>
        seconds <= 0 ? "00:00:00" : TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");

    public void Dispose()
    {
        _drainTimer?.Dispose();
        if (_player is not null)
        {
            _player.FrameEmitted -= OnFrameEmitted;
            _player.PlaybackEnded -= OnPlaybackEnded;
            _player.SeekProgress -= OnSeekProgress;
            _player.Dispose();
        }
    }
}
```

在 `Mobile.Core.csproj` 加 InternalsVisibleTo（让测试访问 `MarkReadyForEmit`）：
```xml
<ItemGroup>
  <InternalsVisibleTo Include="PeakCan.Host.Mobile.Core.Tests" />
</ItemGroup>
```

- [ ] **Step 5: 跑确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.Mobile.Core.Tests --filter "FullyQualifiedName~TraceSessionViewModelTests"
```
Expected: 8 passed。若 `FrameEmitted_AccumulatesInPending_NotVisibleUntilDrainTick` 依赖 ring 含内容判定——`MarkReadyForEmit` 已清 ring 并置 Playing，emit 后 drain 应见 1 行。

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.Mobile.Core/ViewModels src/PeakCan.Host.Mobile.Core/Platform/IStreamingSourceFactory.cs src/PeakCan.Host.Mobile.Core/PeakCan.Host.Mobile.Core.csproj tests/PeakCan.Host.Mobile.Core.Tests/ViewModels tests/PeakCan.Host.Mobile.Core.Tests/Fakes
git commit -m "feat(mobile): add TraceSessionViewModel (state machine + ring + throttle + id filter, TDD)"
```

---

## Task 8: MAUI UI（Views + Platform 实现 + 接线）

**Files:**
- Create: `src/PeakCan.Host.Mobile/Platform/PlatformUiDispatcher.cs`
- Create: `src/PeakCan.Host.Mobile/Platform/MauiFilePickerGateway.cs`
- Create: `src/PeakCan.Host.Mobile/Platform/AscStreamingSourceFactory.cs`
- Create: `src/PeakCan.Host.Mobile/Views/FilesPage.xaml`(.cs)
- Create: `src/PeakCan.Host.Mobile/Views/TracePage.xaml`(.cs)
- Create: `src/PeakCan.Host.Mobile/Views/FrameDetailSheet.xaml`(.cs)（P1：仅原始字节，无 DBC）
- Modify: `src/PeakCan.Host.Mobile/App.xaml.cs`（AppShell 接线 + OnSleep 暂停）
- Modify: `src/PeakCan.Host.Mobile/MauiProgram.cs`（DI 注册）

**Interfaces:**
- Consumes: `TraceSessionViewModel`（Task 7）、`IUiDispatcher`/`IFilePickerGateway`/`IStreamingSourceFactory`（Task 6/7）、`TraceFileCache`（Task 6）、`AscStreamingSource`（Task 3）
- Produces: 可运行的 Android UI：文件页 → 选 .asc → trace 页（控制条 + 帧表格 + 过滤 + 跟随语义 + 帧详情）

- [ ] **Step 1: PlatformUiDispatcher（MAUI MainThread）**

`src/PeakCan.Host.Mobile/Platform/PlatformUiDispatcher.cs`：
```csharp
using PeakCan.Host.Mobile.Core.Platform;

namespace PeakCan.Host.Mobile.Platform;

public sealed class PlatformUiDispatcher : IUiDispatcher
{
    public void Post(Action action)
        => MainThread.BeginInvokeOnMainThread(action);

    public IDisposable StartTimer(TimeSpan period, Action tick)
    {
        var timer = Application.Current!.Dispatcher.CreateTimer();
        timer.Interval = period;
        timer.Tick += (_, _) => tick();
        timer.Start();
        return timer;
    }
}
```

- [ ] **Step 2: MauiFilePickerGateway**

`src/PeakCan.Host.Mobile/Platform/MauiFilePickerGateway.cs`：
```csharp
using PeakCan.Host.Mobile.Core.Platform;

namespace PeakCan.Host.Mobile.Platform;

public sealed class MauiFilePickerGateway : IFilePickerGateway
{
    public string CacheDirectory => FileSystem.CacheDirectory;

    public async Task<PickedTraceFile?> PickTraceFileAsync(CancellationToken ct = default)
    {
        var custom = new FilePickerFileType(
            new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                [DevicePlatform.Android] = new[] { ".asc", ".blf" }
            });
        var result = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "选择 trace 文件",
            FileTypes = custom,
        });
        if (result is null) return null;

        long size = 0;
        await using (var s = await result.OpenReadAsync())
            size = s.Length;
        var name = result.FileName;
        return new PickedTraceFile(name, size, () => result.OpenReadAsync().GetAwaiter().GetResult());
    }
}
```

- [ ] **Step 3: AscStreamingSourceFactory**

`src/PeakCan.Host.Mobile/Platform/AscStreamingSourceFactory.cs`：
```csharp
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Mobile.Core.Platform;

namespace PeakCan.Host.Mobile.Platform;

public sealed class AscStreamingSourceFactory : IStreamingSourceFactory
{
    public IStreamingTraceSource Create(string cachedFilePath)
        => new AscStreamingSource(() => new FileStream(cachedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read));
}
```

- [ ] **Step 4: FilesPage**

`src/PeakCan.Host.Mobile/Views/FilesPage.xaml`：
```xml
<?xml version="1.0" encoding="utf-8" ?>
<ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
             x:Class="PeakCan.Host.Mobile.Views.FilesPage"
             Title="PeakCan Mobile">
    <Grid RowDefinitions="*,Auto" Padding="16">
        <ScrollView Grid.Row="0">
            <VerticalStackLayout Spacing="8">
                <Label Text="最近文件" FontSize="20" Margin="0,0,0,8" />
                <CollectionView x:Name="RecentList" SelectionMode="Single">
                    <CollectionView.ItemTemplate>
                        <DataTemplate>
                            <Frame Padding="12" Margin="0,2">
                                <VerticalStackLayout Spacing="2">
                                    <Label Text="{Binding DisplayName}" FontAttributes="Bold" />
                                    <Label Text="{Binding Subtitle}" FontSize="12" TextColor="Gray" />
                                </VerticalStackLayout>
                            </Frame>
                        </DataTemplate>
                    </CollectionView.ItemTemplate>
                </CollectionView>
            </VerticalStackLayout>
        </ScrollView>
        <Button Grid.Row="1" Text="打开文件" Margin="0,8,0,0"
                Clicked="OnOpenClicked" />
    </Grid>
</ContentPage>
```
`Views/FilesPage.xaml.cs`：
```csharp
using PeakCan.Host.Mobile.Core.Platform;
using PeakCan.Host.Mobile.Core.Services;

namespace PeakCan.Host.Mobile.Views;

public partial class FilesPage : ContentPage
{
    private readonly IFilePickerGateway _picker;
    private readonly TraceFileCache _cache;

    public record RecentItem(string DisplayName, string Subtitle, string CachedPath);

    public FilesPage(IFilePickerGateway picker, TraceFileCache cache)
    {
        InitializeComponent();
        _picker = picker;
        _cache = cache;
        // P1：最近文件列表暂用 cache 目录扫描填充（简单实现）
        RefreshRecent();
    }

    private void RefreshRecent()
    {
        var items = Directory.GetFiles(_cache.CacheDirectory, "*.asc")
            .Select(p => new RecentItem(Path.GetFileNameWithoutExtension(p), $"{new FileInfo(p).Length / 1024} KB", p))
            .ToList();
        RecentList.ItemsSource = items;
    }

    private async void OnOpenClicked(object? sender, EventArgs e)
    {
        var picked = await _picker.PickTraceFileAsync();
        if (picked is null) return;
        var path = await _cache.ImportAsync(picked);
        await Navigation.PushAsync(new TracePage(path));
    }
}
```

- [ ] **Step 5: TracePage（核心 UI）**

`src/PeakCan.Host.Mobile/Views/TracePage.xaml`：
```xml
<?xml version="1.0" encoding="utf-8" ?>
<ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
             xmlns:vm="clr-namespace:PeakCan.Host.Mobile.Core.ViewModels;assembly=PeakCan.Host.Mobile.Core"
             x:Class="PeakCan.Host.Mobile.Views.TracePage"
             Title="Trace">
    <Grid RowDefinitions="Auto,Auto,*,Auto" Padding="8">
        <!-- 控制条 -->
        <HorizontalStackLayout Grid.Row="0" Spacing="8" VerticalOptions="Center">
            <Button Text="{Binding PlayPauseLabel}" Clicked="OnTogglePlay" WidthRequest="60" />
            <Picker x:Name="SpeedPicker" SelectedIndexChanged="OnSpeedChanged" WidthRequest="80" />
            <Label Text="{Binding CurrentTimeText}" VerticalOptions="Center" />
            <Label Text="/" VerticalOptions="Center" />
            <Label Text="{Binding DurationText}" VerticalOptions="Center" />
        </HorizontalStackLayout>

        <Slider Grid.Row="1" Minimum="0" Maximum="1" Value="{Binding Progress01}"
                DragCompleted="OnSeekCompleted" IsEnabled="{Binding DurationKnown}" />

        <!-- 帧表格 -->
        <CollectionView Grid.Row="2" x:Name="Frames" ItemsSource="{Binding VisibleRows}">
            <CollectionView.ItemTemplate>
                <DataTemplate>
                    <Grid ColumnDefinitions="2*,1.2*,1*,2.6*">
                        <Label Grid.Column="0" Text="{Binding TimeText}" FontFamily="Mono" FontSize="12" />
                        <Label Grid.Column="1" Text="{Binding IdText}" FontFamily="Mono" FontSize="12" />
                        <Label Grid.Column="2" Text="{Binding Dlc}" FontFamily="Mono" FontSize="12" />
                        <Label Grid.Column="3" Text="{Binding DataText}" FontFamily="Mono" FontSize="12" LineBreakMode="TailTruncation" />
                    </Grid>
                </DataTemplate>
            </CollectionView.ItemTemplate>
        </CollectionView>

        <!-- 过滤栏 + 跟随 -->
        <HorizontalStackLayout Grid.Row="3" Spacing="8">
            <Entry x:Name="FilterEntry" Placeholder="ID 过滤 (hex, 逗号分隔)" WidthRequest="200"
                   Completed="OnFilterCompleted" />
            <Button Text="↓ 最新" Clicked="OnJumpLatest" />
        </HorizontalStackLayout>
    </Grid>
</ContentPage>
```
`Views/TracePage.xaml.cs`：
```csharp
using PeakCan.Host.Mobile.Core.Platform;
using PeakCan.Host.Mobile.Core.ViewModels;

namespace PeakCan.Host.Mobile.Views;

public partial class TracePage : ContentPage
{
    private readonly TraceSessionViewModel _vm;
    private bool _following = true;

    public TracePage(string cachedFilePath)
    {
        InitializeComponent();
        var ui = (PlatformUiDispatcher)Handler?.MauiContext?.Services.GetService(typeof(IUiDispatcher))!;
        var sourceFactory = (AscStreamingSourceFactory)Handler?.MauiContext?.Services.GetService(typeof(IStreamingSourceFactory))!;
        _vm = new TraceSessionViewModel(ui, sourceFactory, src =>
            new PeakCan.Host.Core.Replay.StreamingTracePlayer(src, clock: null));
        BindingContext = _vm;
        SpeedPicker.ItemsSource = new[] { "0.1x", "0.5x", "1x", "2x", "5x", "10x" };
        SpeedPicker.SelectedIndex = 2;
        Frames.Scrolled += OnFramesScrolled;
        _vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(TraceSessionViewModel.VisibleRows) && _following)
                Device.BeginInvokeOnMainThread(() => Frames.ScrollTo(_vm.VisibleRows.Count - 1, position: ScrollToPosition.End, animate: false));
        };
        _ = _vm.OpenAsync(cachedFilePath);
    }

    private void OnTogglePlay(object? sender, EventArgs e) => _vm.TogglePlayCommand.Execute(null);
    private void OnFilterCompleted(object? sender, EventArgs e) => _vm.SetIdFilter(FilterEntry.Text);
    private void OnSpeedChanged(object? sender, EventArgs e)
    {
        var sel = (string?)SpeedPicker.SelectedItem;
        if (sel is not null && double.TryParse(sel.TrimEnd('x'), out var m)) _vm.SetSpeed(m);
    }
    private void OnSeekCompleted(object? sender, EventArgs e) => _vm.SeekToCommand.Execute(_vm.Progress01);
    private void OnJumpLatest(object? sender, EventArgs e)
    {
        _following = true;
        Frames.ScrollTo(_vm.VisibleRows.Count - 1, position: ScrollToPosition.End, animate: false);
    }
    private void OnFramesScrolled(object? sender, ItemsViewScrolledEventArgs e)
    {
        // 用户上滑脱离底部 → 取消跟随；到底 → 恢复
        _following = e.BottomItemIndex >= _vm.VisibleRows.Count - 2;
    }
}
```
注：`TraceSessionViewModel.DurationKnown` 属性（ObservableProperty `bool DurationKnown`）—— 在 Task 7 的 OpenAsync 完成时 setter 设 `DurationKnown = true`（补一行：`_ui.Post(() => { DurationText = FormatTime(_duration); DurationKnown = true; })`）。`PlayPauseLabel` 同理需 ObservableProperty（按 State 切 "▶"/"⏸"）—— 在 Task 7 实现 `partial void OnStateChanged` 或在 TogglePlay 里同步设 `PlayPauseLabel`。**实现者补这两个 ObservableProperty**（`DurationKnown`、`PlayPauseLabel`），代码模式与 `CurrentTimeText` 一致；不另起 Task。

- [ ] **Step 6: FrameDetailSheet（P1：原始字节）**

`src/PeakCan.Host.Mobile/Views/FrameDetailSheet.xaml`：
```xml
<?xml version="1.0" encoding="utf-8" ?>
<ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
             x:Class="PeakCan.Host.Mobile.Views.FrameDetailSheet"
             Title="帧详情">
    <ScrollView Padding="16">
        <VerticalStackLayout Spacing="6">
            <Label Text="{Binding Header}" FontAttributes="Bold" />
            <Label Text="{Binding RawBytes}" FontFamily="Mono" />
        </VerticalStackLayout>
    </ScrollView>
</ContentPage>
```
`Views/FrameDetailSheet.xaml.cs`：
```csharp
namespace PeakCan.Host.Mobile.Views;

public partial class FrameDetailSheet : ContentPage
{
    public string Header { get; }
    public string RawBytes { get; }
    public FrameDetailSheet(string header, string rawBytes)
    {
        InitializeComponent();
        Header = header; RawBytes = rawBytes;
        BindingContext = this;
    }
}
```
在 TracePage 的 `Frames.ItemTemplate` 里给 `<Grid>` 加 `GestureRecognizers` → `TapGestureRecognizer Tapped="OnRowTapped"`，`OnRowTapped` 弹出 `FrameDetailSheet`（取行数据）。**实现者补**：`OnRowTapped` + XAML 手势注册（约 10 行）。

- [ ] **Step 7: AppShell + MauiProgram + OnSleep**

`App.xaml.cs` 改 Shell：
```csharp
using PeakCan.Host.Mobile.Core.Platform;
using PeakCan.Host.Mobile.Core.Services;
using PeakCan.Host.Mobile.Platform;
using PeakCan.Host.Mobile.Views;

namespace PeakCan.Host.Mobile;

public partial class App : Application
{
    public App(FilesPage filesPage)
    {
        InitializeComponent();
        MainPage = filesPage; // P1：单页 navigation（Push 到 TracePage）
    }

    protected override void OnSleep()
    {
        base.OnSleep();
        // 切后台 → 若当前在 TracePage，暂停播放（spec §7）
        if (MainPage is NavigationPage nav && nav.CurrentPage is TracePage tp)
            tp.PauseForBackground();
    }
}
```
`TracePage` 加 `internal void PauseForBackground() => _vm.PauseForBackground();`，VM 加 `internal void PauseForBackground()` → 若 Playing 则 `_player.Pause()` + State=Paused。

`MauiProgram.cs`：
```csharp
using Microsoft.Maui.Controls.Hosting;
using PeakCan.Host.Mobile.Core.Platform;
using PeakCan.Host.Mobile.Core.Services;
using PeakCan.Host.Mobile.Platform;
using PeakCan.Host.Mobile.Views;

namespace PeakCan.Host.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
        => MauiApp.CreateBuilder()
            .UseMauiApp<App>()
            .RegisterServices()
            .Build();

    private static MauiAppBuilder RegisterServices(this MauiAppBuilder b)
    {
        b.Services.AddSingleton<IUiDispatcher, PlatformUiDispatcher>();
        b.Services.AddSingleton<IFilePickerGateway, MauiFilePickerGateway>();
        b.Services.AddSingleton<IStreamingSourceFactory, AscStreamingSourceFactory>();
        b.Services.AddSingleton<TraceFileCache>(_ => new TraceFileCache(FileSystem.CacheDirectory));
        b.Services.AddTransient<FilesPage>();
        return b;
    }
}
```

- [ ] **Step 8: 构建 + 部署到真机**

Run:
```bash
dotnet build src/PeakCan.Host.Mobile -t:Run -f net10.0-android
```
Expected: 部署成功，文件页显示。

- [ ] **Step 9: Commit**

```bash
git add src/PeakCan.Host.Mobile
git commit -m "feat(mobile): wire MAUI UI — FilesPage + TracePage + platform impls (P1)"
```

---

## Task 9: intent-filter 直开（微信/文件管理器直接唤起）

**Files:**
- Modify: `src/PeakCan.Host.Mobile/Platforms/Android/MainActivity.cs`
- Modify: `src/PeakCan.Host.Mobile/App.xaml.cs`（接收外部文件 URI）
- Modify: `src/PeakCan.Host.Mobile/Views/FilesPage.xaml.cs`（处理 pending 文件）

**Interfaces:** 无新接口（接线）

- [ ] **Step 1: MainActivity 注册 intent-filter**

`Platforms/Android/MainActivity.cs`：
```csharp
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Application = Microsoft.Maui.Controls.Application;

namespace PeakCan.Host.Mobile;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataSchemes = new[] { "content", "file" },
    DataMimeType = "application/octet-stream")]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        PendingFileUri = Intent?.Data;
    }
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        PendingFileUri = intent?.Data;
    }
    public static Android.Net.Uri? PendingFileUri { get; private set; }
}
```
（`DataMimeType = application/octet-stream` 是微信 content:// URI 的常见 MIME，覆盖偏广但兼容性好；用户在"用其他应用打开"选择器里能选到本 app。）

- [ ] **Step 2: FilesPage 消费 pending URI**

`Views/FilesPage.xaml.cs` 在 `OnAppearing` 里检查 `MainActivity.PendingFileUri`：
```csharp
protected override async void OnAppearing()
{
    base.OnAppearing();
    var uri = Platform.App.Activity is MainActivity ma ? MainActivity.PendingFileUri : null;
    if (uri is null) return;
    MainActivity.PendingFileUri = null; // 消费一次

    // content:// URI → 通过 ContentResolver 拷贝到 cache 目录再打开
    var dest = Path.Combine(_cache.CacheDirectory, $"shared-{DateTime.Now:yyyyMMdd-HHmmss}.asc");
    using var src = Platform.CurrentActivity?.ContentResolver?.OpenInputStream(uri);
    if (src is null) return;
    using var dst = File.Create(dest);
    await src.CopyToAsync(dst);
    await Navigation.PushAsync(new TracePage(dest));
}
```
注：`Platform.App.Activity` 在 MAUI 下访问当前 activity；`Platform.CurrentActivity` 同义。`ContentResolver.OpenInputStream` 把微信的 content:// 流读成字节。

- [ ] **Step 3: 构建部署 + 手动验证**

Run:
```bash
dotnet build src/PeakCan.Host.Mobile -t:Run -f net10.0-android
```
手动：手机里（微信聊天/文件管理器）点一个 .asc → "用其他应用打开" → 选 PeakCan Mobile → 应直接进 TracePage。

- [ ] **Step 4: Commit**

```bash
git add src/PeakCan.Host.Mobile
git commit -m "feat(mobile): android intent-filter for .asc direct-open from WeChat/files"
```

---

## Task 10: 真机验收（P1 出口标准）

**Files:** 无源码改动；产出验收记录（写入计划文件末尾即可，不进 PKM）

- [ ] **Step 1: 生成 100MB 测试 ASC**

PowerShell 脚本 `tools/gen-large-asc.ps1`（新建）：
```powershell
$lines = @()
$lines += "date Wed Jun 28 10:00:00.000 2026"
$lines += "base 0x7e0 500k"
$lines += "internal events logged"
$ts = 0.0
$count = 0
# ~30 字节/行 × ~3.3M 行 ≈ 100MB；每帧间隔 0.001s → ~55 分钟
$target = 3_300_000
while ($count -lt $target) {
  $id = 0x100 + ($count % 8)
  $lines += (" {0:F6} 51  {1:X3}  8  01 02 03 04 05 06 07 08" -f $ts, $id)
  $ts += 0.001
  $count++
}
[System.IO.File]::WriteAllLines("$PWD\large-100mb.asc", $lines)
Write-Host "done: $count frames, $([math]::Round((Get-Item large-100mb.asc).Length/1MB,1)) MB"
```
Run:
```bash
powershell -File tools/gen-large-asc.ps1
```

- [ ] **Step 2: 推到手机**

Run:
```bash
ADB="$LOCALAPPDATA/Android/sdk/platform-tools/adb.exe"
"$ADB" push large-100mb.asc /sdcard/Documents/
```

- [ ] **Step 3: 验收 checklist（手动记录结果）**

打开 app → FilePicker 选 `Documents/large-100mb.asc`，记录：

| 指标 | 目标 | 实测 |
|---|---|---|
| 首帧渲染（点击文件 → 首行出现） | <2s | ____ |
| 拷贝耗时（100MB） | <5s | ____ |
| 总时长扫描完成（slider 从 ?? 变实数） | <30s | ____ |
| 1x 播放 5 分钟 | 不丢帧、不卡 | ____ |
| 内存稳态（`adb shell dumpsys meminfo <pkg>` 取 Java Heap + Native） | <300MB | ____ |
| Seek 到中点 | <5s + 进度反馈 | ____ |
| ID 过滤 `0x103` | 表格只留匹配帧 | ____ |
| 切后台再回前台 | 暂停→续播 | ____ |
| 微信直开 .asc | 进 app 并能播 | ____ |

- [ ] **Step 4: 若有指标不达标，回到对应 Task 修**

不达标即 Task 7/4/8 的对应实现有缺口，按 systematic-debugging 处理后重跑 checklist。**验收未全绿不视为 P1 完成。**

- [ ] **Step 5: Commit 工具脚本**

```bash
git add tools/gen-large-asc.ps1
git commit -m "test(mobile): add 100MB ASC generator for P1 acceptance"
```

---

## Self-Review（已自查）

- **Spec 覆盖**：§4 Core 流式 API（Task 3）、§4.3 StreamingTracePlayer（Task 4）、§5.1 UI 信息架构（Task 8）、§5.2 组件（Task 2/5/6/7）、§5 表格渲染策略 + 跟随语义 + 抽象层（Task 7/8）、§6 数据流（Task 7 OpenAsync 预读 + drain + DurationScanner 并行）、§7 错误处理（Task 4 PlaybackEnded Error / Task 6 cache 降级路径暂未写——P2）、§9 P1 全部条目（Task 1-10）、§10 验收（Task 10）。§7 "SQLite 写失败降级"属 P2 不在本计划。§4.4 BLF 流式 = P4 不在。
- **占位符**：无 TBD/TODO；TracePage 的 `DurationKnown`/`PlayPauseLabel`/`OnRowTapped` 三处明确标注"实现者补"，给了模式参照（同 `CurrentTimeText`），非空泛。
- **类型一致**：`IStreamingTracePlayer`/`StreamingTracePlayer`、`IStreamingTraceSource`/`AscStreamingSource`、`StreamingTraceOpenResult`、`StreamingParseStats`、`RunOutcome` 在 Task 3-4 定义并被 Task 7-8 消费，签名一致。`TraceSessionViewModel` 的 `OpenAsync/TogglePlay/SetIdFilter/SeekTo/VisibleRows/State/DurationText/Progress01/IsSeekBusy` 在 Task 7 定义、Task 8 XAML 绑定引用，名称一致。
