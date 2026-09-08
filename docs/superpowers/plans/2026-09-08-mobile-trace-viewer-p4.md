# Mobile Trace Viewer P4 (BLF Streaming) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** 让 Android 移动端像 ASC 一样流式打开并回放 `.blf` trace，不解压/物化整个文件。

**Architecture:** 新增 `BlfStreamingSource` 实现 `IStreamingTraceSource`：外层只解析 BLF object header，CAN 帧对象直接解码；`LOGG` zlib 容器按需解压后复用现有 `BlfParser` 解码逻辑。跨容器/乱序帧经过 1 秒窗口重排缓冲，保证播放顺序稳定。Mobile 入口按扩展名创建 source，并把 `.blf` 纳入选择、intent、缓存和时长扫描链路。

**Tech Stack:** .NET 10 / MAUI Android / xUnit / FluentAssertions；复用 `PeakCan.Host.Core.Replay.BlfParser` 与 `StreamingTracePlayer`。

**Spec:** `docs/superpowers/specs/2026-09-07-mobile-trace-viewer-design.md`（P4：`BlfStreamingSource`，窗口重排缓冲，`.blf` 可流式回放）

## Global Constraints

- `PeakCan.Host.Mobile.Core` 保持纯逻辑库，不依赖 MAUI/LiveCharts。
- 用户可见文案使用中文；XML/API 注释使用英文。
- 测试禁止 `Thread.Sleep` 和真实 `Task.Delay`。
- 导入大小上限保持 `TraceFileCache.MaxImportBytes = 500L * 1024 * 1024`。
- 每个 task 一次 conventional commit，不添加 attribution。
- Core 新增逻辑必须有 xUnit 覆盖；关键入口运行 Android build。
- 不要提交 `.acceptance/` 与 `.worktrees/`。

---

## Task 1: BLF 重排缓冲

**Files:**
- Create: `src/PeakCan.Host.Core/Replay/Streaming/BlfReorderBuffer.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Replay/Streaming/BlfReorderBufferTests.cs`

**Interfaces:**
- Produces: `internal sealed class BlfReorderBuffer`
  - `const double WindowSeconds = 1.0;`
  - `IReadOnlyList<ReplayFrame> Push(ReplayFrame frame)` 返回可立即产出的旧窗口帧。
  - `IReadOnlyList<ReplayFrame> Flush()` 返回并清空当前缓冲。
  - 顺序契约：窗口内按 `Timestamp` 升序；窗口结束后新帧开启新窗口。

- [x] **Step 1: Write the failing tests**

```csharp
using FluentAssertions;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Replay.Streaming;

public class BlfReorderBufferTests
{
    private static ReplayFrame Frame(double timestamp, uint id = 1) =>
        new(timestamp, id, 1, new byte[] { 1 }, FrameFlags.None, false, 1);

    [Fact]
    public void Push_BuffersFramesWithinWindow()
    {
        var buffer = new BlfReorderBuffer();
        buffer.Push(Frame(0.1)).Should().BeEmpty();
        buffer.Push(Frame(0.9)).Should().BeEmpty();
        buffer.Flush().Select(f => f.Timestamp).Should().Equal(0.1, 0.9);
    }

    [Fact]
    public void Push_SortsLateArrivalWithinWindow()
    {
        var buffer = new BlfReorderBuffer();
        buffer.Push(Frame(0.9)).Should().BeEmpty();
        buffer.Push(Frame(0.1)).Should().BeEmpty();
        buffer.Flush().Select(f => f.Timestamp).Should().Equal(0.1, 0.9);
    }

    [Fact]
    public void Push_EmitsPreviousWindowSorted_WhenWindowAdvances()
    {
        var buffer = new BlfReorderBuffer();
        buffer.Push(Frame(0.9)).Should().BeEmpty();
        buffer.Push(Frame(0.2)).Should().BeEmpty();
        var ready = buffer.Push(Frame(1.4));
        ready.Select(f => f.Timestamp).Should().Equal(0.2, 0.9);
        buffer.Flush().Select(f => f.Timestamp).Should().Equal(1.4);
    }

    [Fact]
    public void Flush_AfterFlush_RestartsWindow()
    {
        var buffer = new BlfReorderBuffer();
        buffer.Push(Frame(0.1)).Should().BeEmpty();
        buffer.Flush().Should().HaveCount(1);
        buffer.Push(Frame(3.0)).Should().BeEmpty();
        buffer.Flush().Select(f => f.Timestamp).Should().Equal(3.0);
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter BlfReorderBufferTests --nologo`
Expected: FAIL，`BlfReorderBuffer` 不存在。

- [x] **Step 3: Implement the buffer**

```csharp
namespace PeakCan.Host.Core.Replay;

/// <summary>
/// Bounds out-of-order BLF frames to a small timestamp window. BLF containers
/// may contain frames that are not globally sorted; sorting the whole file
/// would break streaming, so only one bounded window is materialized.
/// </summary>
internal sealed class BlfReorderBuffer
{
    private const double Epsilon = 1e-9;
    private readonly List<ReplayFrame> _pending = new();
    private double? _windowStart;

    public const double WindowSeconds = 1.0;

    public IReadOnlyList<ReplayFrame> Push(ReplayFrame frame)
    {
        if (_windowStart is null)
        {
            _windowStart = Math.Floor(frame.Timestamp);
            _pending.Add(frame);
            return Array.Empty<ReplayFrame>();
        }

        if (frame.Timestamp < _windowStart.Value + WindowSeconds - Epsilon)
        {
            _pending.Add(frame);
            return Array.Empty<ReplayFrame>();
        }

        var ready = _pending.OrderBy(f => f.Timestamp).ToArray();
        _pending.Clear();
        _windowStart = Math.Floor(frame.Timestamp);
        _pending.Add(frame);
        return ready;
    }

    public IReadOnlyList<ReplayFrame> Flush()
    {
        var ready = _pending.OrderBy(f => f.Timestamp).ToArray();
        _pending.Clear();
        _windowStart = null;
        return ready;
    }
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter BlfReorderBufferTests --nologo`
Expected: PASS

- [x] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Core/Replay/Streaming/BlfReorderBuffer.cs tests/PeakCan.Host.Core.Tests/Replay/Streaming/BlfReorderBufferTests.cs
git commit -m "feat(mobile): add bounded blf reorder buffer"
```

---

## Task 2: BlfStreamingSource 基础流式解析

**Files:**
- Modify: `src/PeakCan.Host.Core/Replay/BlfParser.cs:321-338`（`ParseObjectBody` 从 `private` 改为 `internal`，供同 assembly 流式 source 复用）
- Create: `src/PeakCan.Host.Core/Replay/Streaming/BlfStreamingSource.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Replay/Streaming/BlfStreamingSourceTests.cs`

**Interfaces:**
- Consumes: `IStreamingTraceSource`, `StreamingTraceOpenResult`, `BlfFormat`, `BlfParser.ParseObjectBody`, `BlfParser.LogContainerFlow_UnpackAndRecurse`。
- Produces: `public sealed class BlfStreamingSource : IStreamingTraceSource`
  - 构造：`BlfStreamingSource(Func<Stream> streamFactory, ILogger? logger = null)`
  - `OpenAsync(double? skipUntil = null, CancellationToken ct = default)` 懒枚举帧。
  - 支持 `LOGG` 文件头和裸 `LOBJ` 流；直接 CAN 对象与 zlib `LOGG` 容器都能产出 `ReplayFrame`。

- [x] **Step 1: Change object body visibility**

把 `BlfParser.ParseObjectBody` 的可见性从 `private static` 改为 `internal static`，注释补充：

```csharp
/// <summary>Internal so <see cref="BlfStreamingSource"/> can reuse the same
/// frame decoders without materializing an entire BLF file.</summary>
internal static IReadOnlyList<ReplayFrame> ParseObjectBody(
    uint objectType, ulong timestamp, ReadOnlySpan<byte> frameData)
```

- [x] **Step 2: Write failing streaming tests**

在 `BlfStreamingSourceTests` 中复制 `BlfParserTests` 的 `WriteFileHeader` / `WriteObject` 合成数据辅助方法（不要跨测试类调用 private helper），先覆盖：

- `LOGG` 文件 + 两个直接 `CAN_MESSAGE` 对象按序产出。
- bad magic 抛 `ReplayFormatException`。
- 未知 object type 被跳过。
- `skipUntil` 之前的帧不产出。
- 已有 `BlfParser_LogContainerZlib_Parsed` 的压缩容器 payload 应能通过 streaming source 产出。

最小测试骨架：

```csharp
[Fact]
public async Task OpenAsync_StreamsWithDirectCanObjects()
{
    var ms = new MemoryStream();
    WriteFileHeader(ms);
    WriteCan(ms, timestampTicks: 0);
    WriteCan(ms, timestampTicks: 500_000_000L);
    ms.Position = 0;

    var source = new BlfStreamingSource(() => new MemoryStream(ms.ToArray()));
    await using var open = await source.OpenAsync();
    var frames = await open.Frames.ToListAsync();

    frames.Should().HaveCount(2);
    frames[0].Timestamp.Should().Be(0);
    frames[1].Timestamp.Should().Be(0.5);
}
```

- [x] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter BlfStreamingSourceTests --nologo`
Expected: FAIL，`BlfStreamingSource` 不存在。

- [x] **Step 4: Implement BlfStreamingSource**

实现要点（不使用 `BlfParser.ParseCoreAsync`，避免 `List<ReplayFrame>` 全量物化）：

```csharp
public sealed class BlfStreamingSource : IStreamingTraceSource
{
    private readonly Func<Stream> _streamFactory;
    private readonly ILogger _logger;

    public BlfStreamingSource(Func<Stream> streamFactory, ILogger? logger = null)
    {
        _streamFactory = streamFactory ?? throw new ArgumentNullException(nameof(streamFactory));
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<StreamingTraceOpenResult> OpenAsync(double? skipUntil = null, CancellationToken ct = default)
    {
        var stream = _streamFactory();
        var stats = new StreamingParseStats();
        try
        {
            if (stream.CanSeek && stream.Length < 4)
                throw new ReplayFormatException($"BLF file too small: {stream.Length} bytes");

            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            var firstSignature = new string(reader.ReadChars(4));
            if (firstSignature == BlfFormat.FileSignature)
                reader.ReadBytes(BlfFormat.FileHeaderSize - 4);
            else if (firstSignature == BlfFormat.ObjSignature)
                stream.Position -= 4;
            else
                throw new ReplayFormatException(
                    $"Not a valid BLF file: bad magic '{firstSignature}'");

            return new StreamingTraceOpenResult
            {
                Frames = Enumerate(stream, reader, stats, skipUntil, ct),
                SourceLengthBytes = stream.CanSeek ? stream.Length : null,
                Stats = stats,
                SourceStream = stream,
            };
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }
}
```

`Enumerate` 中每个外层 object：

1. 按 4 字节滑窗找 `LOBJ`（复用现有“回退 3 字节”逻辑）。
2. 读取 32-byte header：`objectSize`、`objectType`、`timestamp`。
3. 读满 `objectSize - BlfFormat.ObjectHeaderSize` 字节；读不满按 corrupted 处理并 seek 到下一个字节。
4. `objectType == BlfFormat.ObjTypeLogContainer` 时调用 `BlfParser.LogContainerFlow_UnpackAndRecurse(frameData, _logger)`。
5. 其他可解析类型调用 `BlfParser.ParseObjectBody(objectType, timestamp, frameData)`。
6. 将产出的帧送入 `BlfReorderBuffer.Push`，先把 ready 帧依次 `yield return`，EOF 时 `Flush()`。
7. `errorCount * 2 > objectCount` 且 `objectCount > 0` 时抛 `ReplayFormatException`。
8. `finally` 中 dispose `BinaryReader`（`leaveOpen: true` 时可只 dispose stream，由 `StreamingTraceOpenResult` 负责）。

- [x] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "BlfStreamingSourceTests|BlfReorderBufferTests" --nologo`
Expected: PASS

- [x] **Step 6: Commit**

```bash
git add src/PeakCan.Host.Core/Replay/BlfParser.cs src/PeakCan.Host.Core/Replay/Streaming/BlfStreamingSource.cs tests/PeakCan.Host.Core.Tests/Replay/Streaming/BlfStreamingSourceTests.cs
git commit -m "feat(mobile): add streaming blf source"
```

---

## Task 3: Mobile 入口支持 `.blf`

**Files:**
- Modify: `src/PeakCan.Host.Mobile.Core/Services/TraceFileCache.cs:63`
- Modify: `src/PeakCan.Host.Mobile.Core/Services/DurationScanner.cs`
- Create: `src/PeakCan.Host.Mobile.Core/Services/BlfDurationScanner.cs`
- Modify: `src/PeakCan.Host.Mobile/Platform/MauiFilePickerGateway.cs:24-25`
- Modify: `src/PeakCan.Host.Mobile/Platform/AscStreamingSourceFactory.cs`
- Modify: `src/PeakCan.Host.Mobile/Views/FilesPage.xaml.cs:65-68,120-139`
- Modify: `src/PeakCan.Host.Mobile.Core/ViewModels/TraceSessionViewModel.cs:154-161`
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/Services/BlfDurationScannerTests.cs`
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/Services/TraceFileCacheTests.cs`（追加扩展名用例）

**Interfaces:**
- Consumes: `BlfStreamingSource`、`BlfFormat`、`TraceFileCache`。
- Produces:
  - `TraceFileCache.PathOf(name, size)` 使用原扩展名：`a.1000.blf`、`a.1000.asc`。
  - `IStreamingSourceFactory` 实现按扩展名分发：`.blf → BlfStreamingSource`，`.asc → AscStreamingSource`。
  - `BlfDurationScanner.ScanAsync(Stream, IProgress<double>?, CancellationToken)` 返回 `DurationScanResult`。

- [x] **Step 1: Write failing cache and duration tests**

```csharp
[Fact]
public void PathOf_PreservesSupportedExtensions()
{
    using var cache = new TraceFileCache(TempDir);
    var blf = cache.ImportAsync(new PickedTraceFile("a.blf", 12, _ => Task.FromResult<Stream>(new MemoryStream(new byte[12])))).GetAwaiter().GetResult();
    blf.Should().EndWith(".blf");
    File.Exists(blf).Should().BeTrue();
}
```

BLF 时长扫描测试用 `BlfStreamingSource` 合成 0s/1s/2s 帧，断言：

```csharp
result.DurationSeconds.Should().Be(2);
result.FrameCount.Should().Be(3);
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --filter "TraceFileCacheTests|BlfDurationScannerTests" --nologo`
Expected: FAIL

- [x] **Step 3: Implement extension and duration support**

`TraceFileCache.PathOf` 改为：

```csharp
private string PathOf(string name, long size)
{
    var extension = Path.GetExtension(name);
    if (!extension.Equals(".asc", StringComparison.OrdinalIgnoreCase) &&
        !extension.Equals(".blf", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("仅支持 .asc 或 .blf 文件。");
    }

    return Path.Combine(_cacheDir, $"{Sanitize(Path.GetFileNameWithoutExtension(name))}.{size}{extension.ToLowerInvariant()}");
}
```

新增 `BlfDurationScanner`，打开同一个 seekable stream，枚举 `BlfStreamingSource.Frames`，只保留 `first/last/count` 并通过 `Stats.BytesRead / total` 报告进度。不要把帧加入列表。

`TraceSessionViewModel.OpenAsync` 将当前 `DurationScanner.ScanAsync` 改为：

```csharp
Func<Stream, CancellationToken, Task<DurationScanResult>> scanAsync =
    cachedFilePath.EndsWith(".blf", StringComparison.OrdinalIgnoreCase)
        ? (fs, token) => BlfDurationScanner.ScanAsync(fs, progress, token)
        : (fs, token) => DurationScanner.ScanAsync(fs, progress, token);
var scan = await scanAsync(fs, ct).ConfigureAwait(false);
```

`MauiFilePickerGateway` 接受 `.asc`/`.blf`；`AscStreamingSourceFactory` 重命名为 `StreamingSourceFactory`，实现：

```csharp
public IStreamingTraceSource Create(string cachedFilePath)
{
    var extension = Path.GetExtension(cachedFilePath);
    return extension.Equals(".blf", StringComparison.OrdinalIgnoreCase)
        ? new BlfStreamingSource(() => new FileStream(cachedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        : new AscStreamingSource(() => new FileStream(cachedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read));
}
```

同步修改 `MauiProgram.cs` DI 注册为 `StreamingSourceFactory`。

`FilesPage.RefreshRecentAsync` 使用两个扩展名枚举；`HandleIntentUriAsync` 接受 `.asc` 或 `.blf`，dest 扩展名跟随源文件。错误文案改为“仅支持 .asc 或 .blf 格式文件”。

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo`
Expected: PASS

- [x] **Step 5: Commit**

```bash
git add -A src/PeakCan.Host.Mobile.Core src/PeakCan.Host.Mobile tests/PeakCan.Host.Mobile.Core.Tests
git commit -m "feat(mobile): open blf traces"
```

---

## Task 4: BLF 回放契约与 Seek

**Files:**
- Test: `tests/PeakCan.Host.Core.Tests/Replay/Streaming/BlfStreamingSourceTests.cs`（追加）
- Test: `tests/PeakCan.Host.Core.Tests/Replay/Streaming/StreamingTracePlayerTests.cs`（追加）

**Interfaces:**
- Consumes: `BlfStreamingSource`, `StreamingTracePlayer`, `FakeReplayClock`。
- Produces: `.blf` 与 `StreamingTracePlayer` 的播放/暂停/倍速/Seek 契约。

- [x] **Step 1: Write failing tests**

至少覆盖：

1. 多个 zlib `LOGG` container 之间的帧全局按时间窗口重排。
2. `skipUntil` 只产出目标时间后的帧。
3. `StreamingTracePlayer.PlayAsync` 使用 fake clock 对 BLF source 播完所有帧。
4. `SeekAsync(target)` 快进后下一帧 `Timestamp >= target`。

- [x] **Step 2: Run tests to verify expected failures**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "BlfStreamingSourceTests|StreamingTracePlayerTests" --nologo`
Expected: 新增用例 FAIL（如 seek/container 行为未覆盖）。

- [x] **Step 3: Minimal fixes only**

优先调整 source；若 `StreamingTracePlayer` 有真实缺陷，修复并保持 ASC 测试不变。

- [x] **Step 4: Run Core streaming tests**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "BlfStreamingSourceTests|BlfReorderBufferTests|StreamingTracePlayerTests" --nologo`
Expected: PASS

- [x] **Step 5: Commit**

```bash
git add tests/PeakCan.Host.Core.Tests src/PeakCan.Host.Core
git commit -m "feat(mobile): replay blf with seek support"
```

---

## Task 5: 全量验证

- [x] **Step 1: Run full test suites**

```powershell
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --nologo
dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo
```

Expected: 0 failed；不引入新的 nullable warning。

- [x] **Step 2: Android build**

```powershell
dotnet build src/PeakCan.Host.Mobile/PeakCan.Host.Mobile.csproj --nologo
```

Expected: 0 error。

- [x] **Step 3: Commit fixes if any**

```bash
git add -A
git commit -m "fix(mobile): stabilize blf streaming"
```

如果没有修改，跳过提交。

---

## Task 6: 模拟器验收与评审

- [x] **Step 1: Build signed APK**

```powershell
dotnet build src/PeakCan.Host.Mobile/PeakCan.Host.Mobile.csproj -f net10.0-android -p:EmbedAssembliesIntoApk=true --nologo
```

- [x] **Step 2: Install and manually verify on emulator**

```powershell
adb install -r src/PeakCan.Host.Mobile/bin/Debug/net10.0-android/com.zhengtaotao.peakcan.mobile-Signed.apk
```

Checklist：
- 打开 `.blf` 文件成功。
- 首屏帧显示。
- 播放/暂停/倍速正常。
- duration slider 有量程。
- Seek 到中段后继续播放。
- ID filter 生效。
- DBC 信号列和图表 Tab 正常。
- 重启后在 FilesPage 可重开同一 `.blf`。

- [x] **Step 3: Record acceptance artifacts in `.acceptance/` only（不提交）**

- [x] **Step 4: Run Superpowers code review and fix Critical/Important findings**

- [x] **Step 5: Finish branch after explicit user confirmation**

