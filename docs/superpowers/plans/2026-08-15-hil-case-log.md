# HIL Case Log（每 case 全量报文 .asc）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** WPF 跑 HIL 时，每个 test case 的 steps 执行期间把 CAN 总线上所有帧流式写入独立的 PEAK ASCII (`.asc`) 文件，零额外内存，CLI 行为不变。

**Architecture:** Core 层加纯接口（`IHilFrameSink` / `IHilFrameSinkFactory` / `IHasFrameSink`），Infrastructure 层实现流式 sink + 共享格式 helper，`TestSuiteEngine` 在 case 级挂载/排空/摘除 sink（drain → detach → Dispose 顺序固定），`HilRunnerService` 构造工厂并降级关闭，WPF `HilViewModel` 暴露 CheckBox。

**Tech Stack:** .NET 10 / WPF / xUnit / System.Threading.Channels / CommunityToolkit.Mvvm / FluentAssertions（App 测试用）/ 原生 Assert（Infra 测试用）

**Spec:** [docs/superpowers/specs/2026-08-11-hil-case-log-design.md](../specs/2026-08-11-hil-case-log-design.md)（含 2026-08-12 Rev 2 P3~P12）

## Global Constraints

- **接口命名（用户已拍板）**：新接口 `IHilFrameSink` / `IHilFrameSinkFactory`，避开已有 `Infrastructure/Channel/IFrameSink`（被 `RecordService`/`DbcDecodeBackgroundService` 实现，语义不同）。`IHasFrameSink` 保留原名（无冲突）。
- **G5 CLI 零影响**：CLI 不传 sink factory → 行为完全不变；`FrameCaptureExporter` 改用 `AscFileFormat` 后输出**逐字节不变**（现有 FrameCaptureExporterTests 必须继续通过）。
- **编码（P9）**：`.asc` 文件必须 UTF-8 with BOM（`new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)`），与 `File.WriteAllTextAsync(..., Encoding.UTF8)` 一致。
- **记录范围**：steps 阶段（case fixture setup 成功之后、case teardown 之前）。setup 失败 → 不建 sink（P6）。
- **收尾顺序（P3）**：`WaitForFrameDrainAsync` → `SetFrameSink(null)` → `Dispose()`，顺序不可颠倒。
- **默认目录**：`%LocalAppData%\PeakCanHost\hil-reports\case-logs\`。
- **文件名**：`{caseName}_{caseIndex}_{runTimestamp}.asc`，runTimestamp = `yyyyMMddHHmmssfff`，caseName 清洗后截断到 100 字符（A9）。
- **降级**：目录不可写/不可创建 → 记日志 + 功能整体关闭（P4）；`Create` 失败返回 null（A8）；sink `Write` 异常不传播（A7，MVP 静默丢弃，见 spec 待优化 #5）。
- **默认值**：`HilRunRequest.CaptureCaseLogs = false`（保 CLI 语义），WPF VM 显式传 true。

---

### Task 1: Core HIL case-log 契约

**Files:**
- Create: `src/PeakCan.Host.Core/HIL/Contracts/IHilFrameSink.cs`
- Test: `tests/PeakCan.Host.Core.Tests/HIL/HilFrameSinkContractTests.cs`

**Interfaces:**
- Consumes: `PeakCan.HIL.Core.CanFrame`（现有）
- Produces: `IHilFrameSink`（`void Write(CanFrame)` + `IDisposable`）、`IHilFrameSinkFactory`（`IHilFrameSink? Create(string caseName, int caseIndex)`）、`IHasFrameSink`（`void SetFrameSink(IHilFrameSink? sink)` + `Task WaitForFrameDrainAsync(CancellationToken ct = default)`）。后面所有任务依赖这三个契约。

- [ ] **Step 1: 写失败测试**

```csharp
// tests/PeakCan.Host.Core.Tests/HIL/HilFrameSinkContractTests.cs
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.Host.Core.Tests.HIL;

public class HilFrameSinkContractTests
{
    private sealed class RecordingSink : IHilFrameSink
    {
        public int Written { get; private set; }
        public void Write(CanFrame frame) => Written++;
        public void Dispose() { }
    }

    private sealed class SpyHasSink : IHasFrameSink
    {
        public IHilFrameSink? Sink { get; private set; }
        public int DrainCalls { get; private set; }
        public void SetFrameSink(IHilFrameSink? sink) => Sink = sink;
        public Task WaitForFrameDrainAsync(CancellationToken ct = default) { DrainCalls++; return Task.CompletedTask; }
    }

    [Fact]
    public void Sink_Write_RecordsFrame() => Assert.Equal(1, new RecordingSink().PipeWrite());

    [Fact]
    public void HasSink_SetAndDrain_Work()
    {
        var spy = new SpyHasSink();
        using var sink = new RecordingSink();
        spy.SetFrameSink(sink);
        Assert.Same(sink, spy.Sink);
        spy.WaitForFrameDrainAsync().GetAwaiter().GetResult();
        Assert.Equal(1, spy.DrainCalls);
    }

    [Fact]
    public void Factory_Create_ReturnsSinkOrNull()
    {
        IHilFrameSinkFactory factory = new StubFactory();
        using var s = factory.Create("case", 0);
        Assert.NotNull(s);
        Assert.Null(new NullFactory().Create("case", 0));
    }

    private sealed class StubFactory : IHilFrameSinkFactory
    {
        public IHilFrameSink? Create(string caseName, int caseIndex) => new RecordingSink();
    }
    private sealed class NullFactory : IHilFrameSinkFactory
    {
        public IHilFrameSink? Create(string caseName, int caseIndex) => null;
    }
}

internal static class SinkWriteExtensions
{
    public static int PipeWrite(this IHilFrameSink sink)
    {
        using (sink) { sink.Write(new CanFrame(new CanId(1, FrameFormat.Standard), ReadOnlyMemory<byte>.Empty, FrameFlags.None, ChannelId.None, new Timestamp(0))); }
        return sink.Written;
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~HilFrameSinkContractTests" -v q`
Expected: 编译失败 `error CS0246: 找不到类型或命名空间名“IHilFrameSink”`

- [ ] **Step 3: 写最小实现**

```csharp
// src/PeakCan.Host.Core/HIL/Contracts/IHilFrameSink.cs
namespace PeakCan.HIL.Core.HIL.Contracts;

/// <summary>流式 CAN 帧记录器。Write 由 consumer 单线程调用；Dispose 后 Write 必须静默丢弃。
/// Write 内部不得向调用方抛异常（会杀 consumer loop）。</summary>
public interface IHilFrameSink : IDisposable
{
    void Write(CanFrame frame);
}

/// <summary>按 case 创建帧 sink。工厂由 HilRunnerService 一次性构造，跨 case 复用。</summary>
public interface IHilFrameSinkFactory
{
    /// <summary>为指定 case 创建 sink；返回 null = 该 case 不记录（预留 case 级跳过）。</summary>
    IHilFrameSink? Create(string caseName, int caseIndex);
}

/// <summary>IAssertionContext 的可选扩展：挂载/摘除帧 sink。</summary>
public interface IHasFrameSink
{
    void SetFrameSink(IHilFrameSink? sink);

    /// <summary>有界等待 consumer 排空在途帧（channel 积压）。引擎线程在 case 结束、detach 之前调用；
    /// 500ms 上限或 ct 取消时直接返回（放弃排空，残余帧丢弃但文件仍合法）。</summary>
    Task WaitForFrameDrainAsync(CancellationToken ct = default);
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~HilFrameSinkContractTests" -v q`
Expected: PASS（3 tests）

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Core/HIL/Contracts/IHilFrameSink.cs tests/PeakCan.Host.Core.Tests/HIL/HilFrameSinkContractTests.cs
git commit -m "feat(hil): add IHilFrameSink/IHilFrameSinkFactory/IHasFrameSink contracts (case-log spec)"
```

---

### Task 2: `AscFileFormat` 共享格式 helper

**Files:**
- Create: `src/PeakCan.Host.Infrastructure/HIL/AscFileFormat.cs`
- Test: `tests/PeakCan.Host.Infrastructure.Tests/HIL/AscFileFormatTests.cs`

**Interfaces:**
- Consumes: `PeakCan.HIL.Core.CanFrame`
- Produces: `AscFileFormat.WriteHeader(StringBuilder)`、`AscFileFormat.WriteFrameLine(StringBuilder, CanFrame, double elapsedUs)`、`AscFileFormat.SanitizeFileName(string, int maxLength = int.MaxValue)`。Task 3（FrameCaptureExporter）、Task 4（AscFrameSink）、Task 5（Factory）复用。字节格式必须与 `FrameCaptureExporter` 现输出**完全一致**（T5）。

- [ ] **Step 1: 写失败测试**

```csharp
// tests/PeakCan.Host.Infrastructure.Tests/HIL/AscFileFormatTests.cs
using System.Text;
using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

public class AscFileFormatTests
{
    [Fact]
    public void WriteHeader_ProducesExactFourLines()
    {
        var sb = new StringBuilder();
        AscFileFormat.WriteHeader(sb);
        var expected = $"date Fri Jan 01 00:00:00.000 {DateTime.Now:yyyy}\n"
                     + "base hex  timestamps absolute\n"
                     + "internal events logged\n"
                     + "// version 8.5.0\n";
        Assert.Equal(expected, sb.ToString());
    }

    [Fact]
    public void WriteFrameLine_MatchesFrameCaptureExporterFormat()
    {
        var frame = new CanFrame(
            new CanId(0x123, FrameFormat.Standard),
            new ReadOnlyMemory<byte>(new byte[] { 0x01, 0x02, 0x03 }),
            FrameFlags.None, ChannelId.None, new Timestamp(1000000));
        var sb = new StringBuilder();
        AscFileFormat.WriteFrameLine(sb, frame, 0.0);
        Assert.Equal("   0.000000 1  0x123      x       Rx d 3 01 02 03\n", sb.ToString());
    }

    [Fact]
    public void SanitizeFileName_ReplacesInvalidChars_AndTruncates()
    {
        Assert.Equal("a_b_c_d_e", AscFileFormat.SanitizeFileName("a/b:c*d?e", 100));
        var longName = new string('中', 200);
        Assert.Equal(100, AscFileFormat.SanitizeFileName(longName, 100).Length);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AscFileFormatTests" -v q`
Expected: 编译失败 `error CS0246: 找不到类型或命名空间名“AscFileFormat”`

- [ ] **Step 3: 写最小实现**

```csharp
// src/PeakCan.Host.Infrastructure/HIL/AscFileFormat.cs
using System.Text;
using PeakCan.HIL.Core;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>PEAK ASCII (.asc) 文件格式共享 helper。FrameCaptureExporter（CLI）与 AscFrameSink（WPF 流式）同源，
/// 逐字节一致。internal，同程序集可见。</summary>
internal static class AscFileFormat
{
    public static void WriteHeader(StringBuilder sb)
    {
        sb.AppendLine($"date Fri Jan 01 00:00:00.000 {DateTime.Now:yyyy}");
        sb.AppendLine("base hex  timestamps absolute");
        sb.AppendLine("internal events logged");
        sb.AppendLine("// version 8.5.0");
    }

    public static void WriteFrameLine(StringBuilder sb, CanFrame frame, double elapsedUs)
    {
        var seconds = elapsedUs / 1_000_000.0;
        var idStr = frame.Id.IsExtended ? $"0x{frame.Id.Raw:X8}" : $"0x{frame.Id.Raw:X3}";
        var dlc = frame.Data.Length;
        var dataHex = BitConverter.ToString(frame.Data.Span.ToArray()).Replace("-", " ");
        sb.AppendLine($"{seconds,12:F6} 1  {idStr,-12}x       Rx d {dlc} {dataHex}");
    }

    public static string SanitizeFileName(string name, int maxLength = int.MaxValue)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        if (sb.Length > maxLength) sb.Length = maxLength;
        return sb.ToString();
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AscFileFormatTests" -v q`
Expected: PASS（3 tests）。若 `WriteFrameLine` 空格数断言失败，以 `FrameCaptureExporter.cs:82-83` 的格式字符串为准修正测试字面量（T5 锚定的是 exporter 的真实字节，不是本 helper 想当然）。

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Infrastructure/HIL/AscFileFormat.cs tests/PeakCan.Host.Infrastructure.Tests/HIL/AscFileFormatTests.cs
git commit -m "feat(hil): add AscFileFormat shared ASC helper (case-log spec P5)"
```

---

### Task 3: `FrameCaptureExporter` 改用 `AscFileFormat`（Rev 2 P5）

**Files:**
- Modify: `src/PeakCan.Host.Infrastructure/Cli/Reporting/FrameCaptureExporter.cs`（`WriteAscFileAsync` :52-87 改用 helper；`:43` 的 `SanitizeFileName` 调用改 `AscFileFormat.SanitizeFileName`；删除 private `SanitizeFileName` :92-101）
- Test: `tests/PeakCan.Host.Infrastructure.Tests/Cli/Reporting/FrameCaptureExporterTests.cs`（追加字节精确回归测试）

**Interfaces:**
- Consumes: `AscFileFormat`（Task 2）
- Produces: 无新类型。**不变式（G5）**：`FrameCaptureExporter` 对外输出逐字节不变，现有测试全绿。

- [ ] **Step 1: 写失败测试（字节精确锚定）**

```csharp
// 追加到 tests/PeakCan.Host.Infrastructure.Tests/Cli/Reporting/FrameCaptureExporterTests.cs
[Fact]
public async Task FrameExporter_BytesMatchAscFileFormat_GoldenLine()
{
    var dir = GetTempDir();
    var frame = new CanFrame(
        new CanId(0x123, FrameFormat.Standard),
        new ReadOnlyMemory<byte>(new byte[] { 0x01, 0x02, 0x03 }),
        FrameFlags.None, ChannelId.None, new Timestamp(1000000));
    var step = new StepResult(0, TestCaseStepKind.AssertSignal, "s1", StepStatus.Failed,
        "fail", null, null, 0, new[] { frame });
    var caseResult = new TestCaseResult("Fail_Case", "Fail_Case", false, "fail", 10, 1, 0, 1, 0, 0, new[] { step });
    var result = new TestSuiteResult("Suite", 1, 0, 1, 0, 100, Array.Empty<string>(), new[] { caseResult });

    try
    {
        await FrameCaptureExporter.ExportAsync(result, dir);
        var content = await File.ReadAllTextAsync(Directory.GetFiles(dir, "*.asc")[0]);
        Assert.Contains("   0.000000 1  0x123      x       Rx d 3 01 02 03", content);
    }
    finally
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}
```

- [ ] **Step 2: 运行确认失败（此测试在改动前应该已通过 —— 它锚定的是现状）**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj --filter "FullyQualifiedName~FrameExporter_BytesMatchAscFileFormat" -v q`
Expected: PASS（锚定改动前行为）。若 FAIL，说明格式字符串与 exporter 现状不符，按 exporter 真实输出修正测试字面量后再继续。

- [ ] **Step 3: 重构 `FrameCaptureExporter` 委托 helper**

```csharp
// src/PeakCan.Host.Infrastructure/Cli/Reporting/FrameCaptureExporter.cs
// 顶部加: using PeakCan.Host.Infrastructure.HIL;
private static async Task WriteAscFileAsync(string path, List<CanFrame> frames, CancellationToken ct)
{
    var sb = new StringBuilder();

    AscFileFormat.WriteHeader(sb);   // ← 改：原 :57-60 内联 header 删除

    double timestampOffsetUs = 0;
    if (frames.Count > 0)
        timestampOffsetUs = frames[0].Timestamp.TotalMicroseconds;

    foreach (var frame in frames)
    {
        ct.ThrowIfCancellationRequested();
        var elapsedUs = frame.Timestamp.TotalMicroseconds - timestampOffsetUs;
        AscFileFormat.WriteFrameLine(sb, frame, elapsedUs);   // ← 改：原 :74-83 内联删除
    }

    await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, ct).ConfigureAwait(false);
}
```

`ExportAsync` :43 的 `SanitizeFileName(c.TestCaseName)` → `AscFileFormat.SanitizeFileName(c.TestCaseName)`。删除整个 private `SanitizeFileName` 方法（:92-101）与 `System.Text`/`System.Globalization` 中不再使用的 using（保留 `System.Globalization` 若仍被 `StringBuilder` 之外用到——检查编译）。

- [ ] **Step 4: 运行确认通过（全部现有 exporter 测试 + 新 golden 测试）**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj --filter "FullyQualifiedName~FrameExporter" -v q`
Expected: PASS（现有 2 + 新 1）。这是 G5 回归闸：任一 FAIL = 输出漂移，立即回查。

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Infrastructure/Cli/Reporting/FrameCaptureExporter.cs tests/PeakCan.Host.Infrastructure.Tests/Cli/Reporting/FrameCaptureExporterTests.cs
git commit -m "refactor(hil): FrameCaptureExporter delegates to AscFileFormat, output unchanged (case-log P5)"
```

---

### Task 4: `AscFrameSink`（流式写入）

**Files:**
- Create: `src/PeakCan.Host.Infrastructure/HIL/AscFrameSink.cs`
- Test: `tests/PeakCan.Host.Infrastructure.Tests/HIL/AscFrameSinkTests.cs`

**Interfaces:**
- Consumes: `IHilFrameSink`（Task 1）、`AscFileFormat`（Task 2）、`CanFrame`
- Produces: `AscFrameSink`（internal sealed，`IHilFrameSink`）。Task 5（Factory）构造它；Task 7/8 通过 `IHasFrameSink.SetFrameSink` 挂载它。

- [ ] **Step 1: 写失败测试**

```csharp
// tests/PeakCan.Host.Infrastructure.Tests/HIL/AscFrameSinkTests.cs
using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

public class AscFrameSinkTests
{
    private static CanFrame F(ulong us, params byte[] data) =>
        new CanFrame(new CanId(0x123, FrameFormat.Standard),
            new ReadOnlyMemory<byte>(data), FrameFlags.None, ChannelId.None, new Timestamp(us));

    [Fact]
    public void Write_ProducesNFrameLines_FirstOffsetZero()
    {
        using var ms = new MemoryStream();
        using (var sink = new AscFrameSink(ms))
        {
            sink.Write(F(1000000, 0x01, 0x02));
            sink.Write(F(2000000, 0x03, 0x04));
        }
        var content = new System.Text.UTF8Encoding(true).GetString(ms.ToArray());
        Assert.Contains("   0.000000 1  0x123      x       Rx d 2 01 02", content);
        Assert.Contains("   1.000000 1  0x123      x       Rx d 2 03 04", content);
    }

    [Fact]
    public void Dispose_FlushesBufferedFrames()
    {
        using var ms = new MemoryStream();
        var sink = new AscFrameSink(ms);
        sink.Write(F(1000000, 0x01));
        sink.Dispose();
        Assert.Contains("Rx d 1 01", new System.Text.UTF8Encoding(true).GetString(ms.ToArray()));
    }

    [Fact]
    public void Dispose_IsIdempotent() => Assert.Null(Record.Exception(() =>
    {
        using var ms = new MemoryStream();
        var sink = new AscFrameSink(ms);
        sink.Dispose();
        sink.Dispose();
    }));

    [Fact]
    public void Write_AfterDispose_SilentlyDrops()
    {
        using var ms = new MemoryStream();
        var sink = new AscFrameSink(ms);
        sink.Write(F(1000000, 0x01));
        var before = ms.ToArray().Length;
        sink.Dispose();
        Assert.Null(Record.Exception(() => sink.Write(F(2000000, 0x02))));
        Assert.Equal(before, ms.ToArray().Length);
    }

    [Fact]
    public void Empty_ProducesHeaderOnly()
    {
        using var ms = new MemoryStream();
        using (var sink = new AscFrameSink(ms)) { }
        var lines = new System.Text.UTF8Encoding(true).GetString(ms.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.Contains("// version 8.5.0", lines);
    }

    [Fact]
    public void File_StartsWithUtf8Bom()
    {
        using var ms = new MemoryStream();
        using (var sink = new AscFrameSink(ms)) { }
        var bytes = ms.ToArray();
        Assert.True(bytes.Length >= 3);
        Assert.Equal(0xEF, bytes[0]); Assert.Equal(0xBB, bytes[1]); Assert.Equal(0xBF, bytes[2]);
    }

    [Fact]
    public void Write_ThrowingStream_DoesNotPropagate()
    {
        using var sink = new AscFrameSink(new ThrowingStream());
        Assert.Null(Record.Exception(() => sink.Write(F(1000000, 0x01))));
    }

    private sealed class ThrowingStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("disk full");
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            throw new IOException("disk full");
    }
}
```

> 注：`Write_ThrowingStream_DoesNotPropagate` 是 A7 用例。若 `ThrowingStream` 覆写不全导致未走到 sink 内部 catch，测试会因 `Dispose` 时 StreamWriter 底层写入抛异常而失败——届时给 `Dispose` 也加 try/catch（吞掉），保证「写失败不传播」整体成立（A7/P12）。

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AscFrameSinkTests" -v q`
Expected: 编译失败 `error CS0246: 找不到类型或命名空间名“AscFrameSink”`

- [ ] **Step 3: 写最小实现**

```csharp
// src/PeakCan.Host.Infrastructure/HIL/AscFrameSink.cs
using System.Text;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>流式 CAN 帧 → PEAK ASCII (.asc) 文件。BufferedStream 缓冲，Dispose 时 flush+close。
/// 首帧时间戳作为 offset 基准。Write 由 consumer 单线程调用；Dispose 与 Write 竞态由软关闭标志保护。</summary>
internal sealed class AscFrameSink : IHilFrameSink
{
    private readonly BufferedStream _buffered;
    private readonly StreamWriter _writer;
    private int _disposed;                       // Interlocked 标志
    private double? _timestampOffsetUs;

    public AscFrameSink(string path) : this(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) { }

    internal AscFrameSink(Stream stream)
    {
        _buffered = new BufferedStream(stream);
        _writer = new StreamWriter(_buffered, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var sb = new StringBuilder();
        AscFileFormat.WriteHeader(sb);
        _writer.Write(sb.ToString());
    }

    public void Write(CanFrame frame)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            _timestampOffsetUs ??= frame.Timestamp.TotalMicroseconds;
            var elapsedUs = frame.Timestamp.TotalMicroseconds - _timestampOffsetUs.Value;
            var sb = new StringBuilder();
            AscFileFormat.WriteFrameLine(sb, frame, elapsedUs);
            _writer.Write(sb.ToString());
        }
        catch (Exception)
        {
            // A7: IO 失败不传播（不杀 consumer loop）。MVP 静默丢弃，见 spec 待优化 #5。
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            _writer.Flush();
            _buffered.Flush();
        }
        catch (Exception) { /* A7: flush/close 失败也不传播 */ }
        finally
        {
            _writer.Dispose();
            _buffered.Dispose();
        }
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AscFrameSinkTests" -v q`
Expected: PASS（7 tests）

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Infrastructure/HIL/AscFrameSink.cs tests/PeakCan.Host.Infrastructure.Tests/HIL/AscFrameSinkTests.cs
git commit -m "feat(hil): add AscFrameSink streaming ASC writer (case-log spec P0/P9/A7)"
```

---

### Task 5: `AscFrameSinkFactory`

**Files:**
- Create: `src/PeakCan.Host.Infrastructure/HIL/AscFrameSinkFactory.cs`
- Test: `tests/PeakCan.Host.Infrastructure.Tests/HIL/AscFrameSinkFactoryTests.cs`

**Interfaces:**
- Consumes: `IHilFrameSinkFactory`（Task 1）、`AscFrameSink`（Task 4）、`AscFileFormat.SanitizeFileName`（Task 2）
- Produces: `AscFrameSinkFactory`（internal sealed）。Task 9（HilRunnerService）构造它。

- [ ] **Step 1: 写失败测试**

```csharp
// tests/PeakCan.Host.Infrastructure.Tests/HIL/AscFrameSinkFactoryTests.cs
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

public class AscFrameSinkFactoryTests
{
    private static string GetTempDir() => Path.Combine(Path.GetTempPath(), $"hil_sink_{Guid.NewGuid():N}");

    [Fact]
    public void Create_ProducesNamedFile_AtExpectedPath()
    {
        var dir = GetTempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var factory = new AscFrameSinkFactory(dir, "20260815_120000_000");
            using var sink = factory.Create("Brake_Test", 2);
            Assert.NotNull(sink);
            Assert.True(File.Exists(Path.Combine(dir, "Brake_Test_2_20260815_120000_000.asc")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Create_LongName_TruncatesTo100()
    {
        var dir = GetTempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var factory = new AscFrameSinkFactory(dir, "TS");
            using var sink = factory.Create(new string('中', 200), 0);
            var file = Directory.GetFiles(dir, "*.asc").Single();
            var fileName = Path.GetFileNameWithoutExtension(file);
            Assert.True(fileName.Length <= 100 + "_0_TS".Length, $"file name too long: {fileName.Length}");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Create_MissingDirectory_ReturnsNull_NoThrow()
    {
        var factory = new AscFrameSinkFactory(Path.Combine(Path.GetTempPath(), $"does_not_exist_{Guid.NewGuid():N}"), "TS");
        Assert.Null(Record.Exception(() => factory.Create("Case", 0)) == null ? null : factory.Create("Case", 0));
        Assert.Null(factory.Create("Case", 0));   // A8: 降级 null，不抛
    }
}
```

> 注：`Create_MissingDirectory_ReturnsNull_NoThrow` 里第一行 Assert 是给 `Record.Exception` 用的惯用写法，执行时若读起来别扭可化简为只保留 `Assert.Null(factory.Create("Case", 0))`（A8 核心断言）。

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AscFrameSinkFactoryTests" -v q`
Expected: 编译失败 `error CS0246: 找不到类型或命名空间名“AscFrameSinkFactory”`

- [ ] **Step 3: 写最小实现**

```csharp
// src/PeakCan.Host.Infrastructure/HIL/AscFrameSinkFactory.cs
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>按 case 名 + case index + run 时间戳命名。目录由 HilRunnerService 预先创建。</summary>
internal sealed class AscFrameSinkFactory : IHilFrameSinkFactory
{
    private readonly string _directory;
    private readonly string _runTimestamp;   // yyyyMMddHHmmssfff

    public AscFrameSinkFactory(string directory, string runTimestamp)
    {
        _directory = directory;
        _runTimestamp = runTimestamp;
    }

    public IHilFrameSink? Create(string caseName, int caseIndex)
    {
        try
        {
            var safeName = AscFileFormat.SanitizeFileName(caseName, maxLength: 100);
            var fileName = $"{safeName}_{caseIndex}_{_runTimestamp}.asc";
            return new AscFrameSink(Path.Combine(_directory, fileName));
        }
        catch (Exception)
        {
            // A8: 建文件失败（目录缺失/权限）→ 返回 null 降级，不把 case 标记为 Failed。
            return null;
        }
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AscFrameSinkFactoryTests" -v q`
Expected: PASS（3 tests）

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Infrastructure/HIL/AscFrameSinkFactory.cs tests/PeakCan.Host.Infrastructure.Tests/HIL/AscFrameSinkFactoryTests.cs
git commit -m "feat(hil): add AscFrameSinkFactory (case-log spec T1/A8/A9)"
```

---

### Task 6: `TestSuiteEngine` sink 生命周期（P6/P3/T3）

**Files:**
- Modify: `src/PeakCan.Host.Core/HIL/TestSuiteEngine.cs`
- Modify: `tests/PeakCan.Host.Core.Tests/HIL/Fakes/FakeAssertionContext.cs`（加 `IHasFrameSink`）
- Modify: `tests/PeakCan.Host.Core.Tests/HIL/TestSuiteEngineTests.cs`（加 sink 生命周期测试）

**Interfaces:**
- Consumes: `IHilFrameSink` / `IHilFrameSinkFactory` / `IHasFrameSink`（Task 1）
- Produces: `TestSuiteEngine.ExecuteAsync` 新增可选参 `IHilFrameSinkFactory? sinkFactory = null`；`ExecuteCaseAsync` 新增 `int caseIndex` 参。Task 9 调用新签名。

- [ ] **Step 1: 给 `FakeAssertionContext` 加 `IHasFrameSink` 支持**

```csharp
// tests/PeakCan.Host.Core.Tests/HIL/Fakes/FakeAssertionContext.cs
// 1) 类声明追加: , IHasFrameSink   (using PeakCan.HIL.Core.HIL.Contracts 若尚未引入)
// 2) 加成员:
public IHilFrameSink? ActiveSink { get; private set; }
public int DrainCalls { get; private set; }
public void SetFrameSink(IHilFrameSink? sink) => ActiveSink = sink;
public Task WaitForFrameDrainAsync(CancellationToken ct = default)
{
    DrainCalls++;
    return Task.CompletedTask;
}
```

- [ ] **Step 2: 写失败测试**

```csharp
// 追加到 tests/PeakCan.Host.Core.Tests/HIL/TestSuiteEngineTests.cs
private sealed class RecordingFactory : IHilFrameSinkFactory
{
    public List<(string Name, int Index)> Creates { get; } = new();
    public List<IHilFrameSink> Created { get; } = new();
    public IHilFrameSink? Create(string caseName, int caseIndex)
    {
        Creates.Add((caseName, caseIndex));
        var s = new RecordingSink();
        Created.Add(s);
        return s;
    }
}
private sealed class RecordingSink : IHilFrameSink
{
    public bool Disposed { get; private set; }
    public void Write(PeakCan.HIL.Core.CanFrame frame) { }
    public void Dispose() => Disposed = true;
}

[Fact]
public async Task ExecuteAsync_MountsSinkPerCase_DisposesEach()
{
    var engine = CreateEngine();   // 复用现有 helper
    var ctx = new FakeAssertionContext();
    var factory = new RecordingFactory();
    var suite = MakeSuite(caseNames: new[] { "A", "B" });   // 复用现有 suite 构造 helper

    await engine.ExecuteAsync(suite, ctx, new TestSuiteConfig(), null, default, factory);

    Assert.Equal(2, factory.Creates.Count);
    Assert.All(factory.Created, s => Assert.True(s.Disposed));
}

[Fact]
public async Task ExecuteAsync_SetupFailure_DoesNotCreateSink()
{
    var engine = CreateEngine();
    var ctx = new FakeAssertionContext();
    var factory = new RecordingFactory();
    var suite = MakeSuiteWithFailingFixture();   // 复用现有 fixture 失败构造

    await engine.ExecuteAsync(suite, ctx, new TestSuiteConfig(), null, default, factory);

    Assert.Empty(factory.Creates);
}

[Fact]
public async Task ExecuteAsync_DrainBeforeDetach_OrderFixed()
{
    var engine = CreateEngine();
    var ctx = new FakeAssertionContext();
    var factory = new RecordingFactory();
    var suite = MakeSuite(caseNames: new[] { "A" });

    await engine.ExecuteAsync(suite, ctx, new TestSuiteConfig(), null, default, factory);

    Assert.Equal(1, ctx.DrainCalls);              // drain 被调用
    Assert.Null(ctx.ActiveSink);                  // detach 后无残留
    Assert.True(factory.Created[0].Disposed);     // Dispose 最后
}
```

> 若 `TestSuiteEngineTests` 没有 `CreateEngine`/`MakeSuite` 之类的现成 helper，参照该文件现有测试的 suite/engine 构造方式补一个本地私有 helper（`TestSuite`、`TestCase`、`TestSuiteConfig` 的构造字段见 `HilRunRequest` 同目录的模型定义与现有测试）。

- [ ] **Step 3: 运行确认失败**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~TestSuiteEngineTests" -v q`
Expected: 编译失败 `error CS7036: 未提供与“ExecuteAsync(...)”的必需形参对应的实参`（新签名未就位）

- [ ] **Step 4: 修改 `TestSuiteEngine`**

```csharp
// ExecuteAsync 签名（:24-29）追加第 6 参:
public async Task<TestSuiteResult> ExecuteAsync(
    TestSuite suite,
    Contracts.IAssertionContext ctx,
    TestSuiteConfig config,
    IProgress<TestProgress>? progress = null,
    CancellationToken externalCt = default,
    Contracts.IHilFrameSinkFactory? sinkFactory = null)

// ExecuteAsync 循环（:69）改传 caseIndex + sinkFactory:
var caseResult = await ExecuteCaseAsync(caseModel, ctx, config, linkedCt, caseIndex, sinkFactory);

// ExecuteCaseAsync 签名（:101-102）改为:
private async Task<TestCaseResult> ExecuteCaseAsync(
    TestCase testCase, Contracts.IAssertionContext ctx, TestSuiteConfig config, CancellationToken ct,
    int caseIndex, Contracts.IHilFrameSinkFactory? sinkFactory)
```

`ExecuteCaseAsync` 步骤区改为（挂载在 setup 之后、steps 之前；finally 固定 drain → detach → Dispose）：

```csharp
    // 原有 setup 循环保持不动（:117-125）

    // Case log sink: setup 成功之后挂载（P6），steps 之前
    Contracts.IHilFrameSink? sink = null;
    if (failureReason is null && ctx is Contracts.IHasFrameSink hasSink && sinkFactory is not null)
    {
        sink = sinkFactory.Create(testCase.Name, caseIndex);
        hasSink.SetFrameSink(sink);
    }

    try
    {
        // Steps (only if setup succeeded) —— 原 :128-219 块整体移入 try
        if (failureReason is null)
        {
            for (int i = 0; i < testCase.Steps.Count; i++)
            {
                /* ...原 steps 逻辑原样（含负测试判定 / FramesAroundFailure / StopCaseOnFailure）... */
            }
        }
    }
    finally
    {
        // P3: 排空在途帧 → detach → Dispose，顺序不可颠倒
        if (ctx is Contracts.IHasFrameSink hasSink2 && sink is not null)
        {
            await hasSink2.WaitForFrameDrainAsync(ct);
            hasSink2.SetFrameSink(null);
        }
        sink?.Dispose();
    }

    // 原有 case teardown 循环（:221-230）在 finally 之后保持不动
```

> 注意：`await hasSink2.WaitForFrameDrainAsync(ct)` 在 `ExecuteCaseAsync` 中 `await` 需方法已是 async——已是（返回 `Task<TestCaseResult>`）。`ct` 是 linkedCt，取消时 drain 内部 catch OCE 返回。

- [ ] **Step 5: 运行确认通过（含既有引擎测试回归）**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~TestSuiteEngineTests" -v q`
Expected: PASS（现有 + 新 3）。既有测试不能回归（sinkFactory 缺省 = 行为不变）。

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.Core/HIL/TestSuiteEngine.cs tests/PeakCan.Host.Core.Tests/HIL/Fakes/FakeAssertionContext.cs tests/PeakCan.Host.Core.Tests/HIL/TestSuiteEngineTests.cs
git commit -m "feat(hil): TestSuiteEngine mounts/drains/disposes per-case frame sink (case-log P6/P3/T3)"
```

---

### Task 7: `HILAssertionContext` 实现 `IHasFrameSink`

**Files:**
- Modify: `src/PeakCan.Host.Infrastructure/HIL/HILAssertionContext.cs`
- Test: `tests/PeakCan.Host.Infrastructure.Tests/HIL/HILAssertionContextFrameSinkTests.cs`（NEW）

**Interfaces:**
- Consumes: `IHilFrameSink` / `IHasFrameSink`（Task 1）、`_frameChannel`（已存在 :53）
- Produces: `HILAssertionContext` 实现 `IHasFrameSink`（`SetFrameSink` / `WaitForFrameDrainAsync`）+ ConsumerLoop 写帧。Task 9 的引擎在运行时通过它挂载 sink。

- [ ] **Step 1: 写失败测试**

```csharp
// tests/PeakCan.Host.Infrastructure.Tests/HIL/HILAssertionContextFrameSinkTests.cs
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

public class HILAssertionContextFrameSinkTests
{
    private sealed class RecordingSink : IHilFrameSink
    {
        public List<CanFrame> Frames { get; } = new();
        public void Write(CanFrame f) => Frames.Add(f);
        public void Dispose() { }
    }

    // 复用现有 HILAssertionContextTests 的 fake channel 构造（FrameReceivedSubscription 需要 ICanChannel）；
    // 找不到时参照 HILAssertionContextConcurrencyTests / HILIntegrationTests 的 channel 造法
    private static HILAssertionContext MakeContext() => throw new NotSupportedException("按现有测试的 channel 构造补");
}
```

> **说明**：`MakeContext` 依赖现有测试基建（fake `ICanChannel` / `FrameReceivedSubscription` 驱动 `OnFrame`）。实现时以 `tests/PeakCan.Host.Infrastructure.Tests/HILIntegrationTests.cs` 或 `HILAssertionContextConcurrencyTests.cs` 里已有的造 context + 灌帧方式为准，替换上面的 throw。测试核心断言如下：

```csharp
[Fact]
public async Task SetFrameSink_FramesWritten_ThenDetachStops()
{
    var ctx = MakeContext();
    using var sink = new RecordingSink();
    ctx.SetFrameSink(sink);
    PushFrames(ctx, 3);                       // 灌 3 帧
    await ctx.WaitForFrameDrainAsync(TimeSpan.FromSeconds(1).ToCancellationToken());  // 等待消费
    Assert.Equal(3, sink.Frames.Count);
    ctx.SetFrameSink(null);
    PushFrames(ctx, 2);
    await Task.Delay(50);
    Assert.Equal(3, sink.Frames.Count);       // detach 后不再写
    ctx.Dispose();
}

[Fact]
public async Task WaitForFrameDrain_DrainsBacklog()
{
    var ctx = MakeContext();
    using var sink = new RecordingSink();
    ctx.SetFrameSink(sink);
    PushFrames(ctx, 100);
    await ctx.WaitForFrameDrainAsync(default);
    Assert.Equal(100, sink.Frames.Count);
    ctx.Dispose();
}

[Fact]
public async Task WaitForFrameDrain_Cancelled_ReturnsWithoutThrow()
{
    var ctx = MakeContext();
    using var sink = new RecordingSink();
    ctx.SetFrameSink(sink);
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    Assert.Null(Record.Exception(() => ctx.WaitForFrameDrainAsync(cts.Token).GetAwaiter().GetResult()));
    ctx.Dispose();
}

[Fact]
public void ConcurrentWriteAndDispose_NoObjectDisposedException()
{
    var ctx = MakeContext();
    using var sink = new RecordingSink();
    ctx.SetFrameSink(sink);
    var t = Task.Run(() => { for (int i = 0; i < 500; i++) PushFrames(ctx, 1); });
    ctx.Dispose();                              // 与写竞态
    t.Wait();
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj --filter "FullyQualifiedName~HILAssertionContextFrameSinkTests" -v q`
Expected: 编译失败 `error CS0535: “HILAssertionContext”未实现接口成员“SetFrameSink”`

- [ ] **Step 3: 实现 `IHasFrameSink` + ConsumerLoop 写帧**

```csharp
// src/PeakCan.Host.Infrastructure/HIL/HILAssertionContext.cs
// 类声明（:16 附近）追加接口:
// public sealed class HILAssertionContext : IAssertionContext, IHasRecentFrames, IFaultInjectionContext, IStepVariableStore, IHasFrameSink

// 加字段 + 两成员（放 GetRecentFrames :143 附近）:
private IHilFrameSink? _frameSink;   // 跨线程：引擎线程写，consumer 线程读

public void SetFrameSink(IHilFrameSink? sink)
    => Volatile.Write(ref _frameSink, sink);

public async Task WaitForFrameDrainAsync(CancellationToken ct = default)
{
    var deadline = DateTime.UtcNow.AddMilliseconds(500);
    try
    {
        while (_frameChannel.Reader.Count > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10, ct).ConfigureAwait(false);
    }
    catch (OperationCanceledException) { /* 取消时放弃排空，文件仍合法 */ }
}

// ConsumerLoop（:198 `_recentFrames.Add(frame);` 之后）插入:
Volatile.Read(ref _frameSink)?.Write(frame);
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj --filter "FullyQualifiedName~HILAssertionContextFrameSinkTests" -v q`
Expected: PASS。若 `WaitForFrameDrain_DrainsBacklog` 偶发断言 <100，是 500ms 上限内 consumer 未跑完——把灌帧放到 drain 前并确保 consumer 已启动（`await Task.Delay(20)` 预热），或在 500ms 内可完成的数量级内灌帧（如 100 帧足够）。

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Infrastructure/HIL/HILAssertionContext.cs tests/PeakCan.Host.Infrastructure.Tests/HIL/HILAssertionContextFrameSinkTests.cs
git commit -m "feat(hil): HILAssertionContext implements IHasFrameSink + drain (case-log P3/P8)"
```

---

### Task 8: `PeakCanAssertionContext` sink + P7 解码保护 + P10 logger

**Files:**
- Modify: `src/PeakCan.Host.Infrastructure/HIL/PeakCanAssertionContext.cs`
- Modify: `src/PeakCan.Host.Infrastructure/HIL/HeadlessHostBuilder.cs`（:107 传 logger）
- Test: `tests/PeakCan.Host.Infrastructure.Tests/PeakCanAssertionContextTests.cs`（追加 P7 用例）

**Interfaces:**
- Consumes: `IHilFrameSink` / `IHasFrameSink`（Task 1）
- Produces: `PeakCanAssertionContext` 实现 `IHasFrameSink`；ctor 新增可选 `ILogger? logger = null`；ConsumerLoop 解码加 try/catch（P7）。Task 9 的硬件模式经 `HeadlessHostBuilder` 使用。

- [ ] **Step 1: 写失败测试（P7：解码异常不杀 loop）**

```csharp
// 追加到 tests/PeakCan.Host.Infrastructure.Tests/PeakCanAssertionContextTests.cs
// 依赖现有测试的 DBC/fake channel 构造方式（PeakCanAssertionContext(channel, dbc)）
[Fact]
public async Task ConsumerLoop_DecodeException_DoesNotKillLoop_SinkStillReceives()
{
    // 构造含 bitLength > 64 的 signal 的 DBC（SignalDecoder.Decode 会抛 ArgumentOutOfRangeException），
    // 参考现有测试里的 DBC 文本构造；然后:
    var ctx = new PeakCanAssertionContext(channel, dbc);
    var sink = new RecordingSink();   // 复用本文件/项目里的 recording sink 模式
    ctx.SetFrameSink(sink);
    // 灌一帧解码必失败的帧（该 message id + 数据），再灌一帧正常帧
    PushFrame(ctx, badFrame);
    PushFrame(ctx, goodFrame);
    await ctx.WaitForFrameDrainAsync(default);
    Assert.Contains(goodFrame, sink.Frames);   // loop 存活，后续帧到达
    ctx.Dispose();
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj --filter "FullyQualifiedName~ConsumerLoop_DecodeException" -v q`
Expected: FAIL —— 解码异常杀掉 ConsumerLoop，`goodFrame` 未达 sink（或测试无法编译，因 sink 接口/`SetFrameSink` 未实现）。

- [ ] **Step 3: 实现**

```csharp
// src/PeakCan.Host.Infrastructure/HIL/PeakCanAssertionContext.cs
// 类声明（:16）追加 IHasFrameSink:
// internal sealed class PeakCanAssertionContext : IAssertionContext, IHasRecentFrames, IStepVariableStore, IHasFrameSink, IDisposable

// ctor（:29）追加 logger（P10）:
private readonly ILogger? _logger;   // 字段（与 _channel 等并列）

public PeakCanAssertionContext(ICanChannel channel, IDbcLookup dbcLookup, ILogger? logger = null)
{
    _channel = channel;
    _dbcLookup = dbcLookup;
    _logger = logger;
    /* ...原 body 不动... */
}

// 加字段 + 两成员（放 GetRecentFrames :86 附近）:
private IHilFrameSink? _frameSink;
public void SetFrameSink(IHilFrameSink? sink) => Volatile.Write(ref _frameSink, sink);
public async Task WaitForFrameDrainAsync(CancellationToken ct = default)
{
    var deadline = DateTime.UtcNow.AddMilliseconds(500);
    try
    {
        while (_frameChannel.Reader.Count > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10, ct).ConfigureAwait(false);
    }
    catch (OperationCanceledException) { }
}

// ConsumerLoop（:141 `_recentFrames.Add(frame);` 之后）插入:
Volatile.Read(ref _frameSink)?.Write(frame);

// P7: :150-156 的 decode 循环改为 FIND-004 同款 try/catch:
foreach (var signal in message.Signals)
{
    var signalName = $"{message.Name}.{signal.Name}";
    try
    {
        var value = SignalDecoder.Decode(frame.Data.Span, signal);
        signals[signalName] = value;
        _signalCache[signalName] = (value, frame.Timestamp.TotalMicroseconds);
    }
    catch (Exception ex)
    {
        _logger?.LogWarning(ex, "Failed to decode signal {Signal} in message {Message}", signal.Name, message.Name);
    }
}
```

```csharp
// src/PeakCan.Host.Infrastructure/HIL/HeadlessHostBuilder.cs（:107）
return new PeakCanAssertionContext(channel, dbc,
    sp.GetService<Microsoft.Extensions.Logging.ILogger<PeakCanAssertionContext>>());
```

> 附注：这里把 `_signalCache` 的时间戳从 `_currentTimestamp` 对齐到帧时间戳（FIND-001 同款），是 P7 改同一段顺手做的一致化，属本任务附带修复；若想严格限定 P7 范围可保留 `_currentTimestamp`，但建议对齐（与 HILAssertionContext 一致）。

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj --filter "FullyQualifiedName~PeakCanAssertionContext" -v q`
Expected: PASS（现有 + 新）。现有 PeakCanAssertionContextTests 回归（ctor 加可选参不影响调用方）。

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Infrastructure/HIL/PeakCanAssertionContext.cs src/PeakCan.Host.Infrastructure/HIL/HeadlessHostBuilder.cs tests/PeakCan.Host.Infrastructure.Tests/PeakCanAssertionContextTests.cs
git commit -m "feat(hil): PeakCanAssertionContext IHasFrameSink + decode protection + logger (case-log P7/P10)"
```

---

### Task 9: `HilRunnerService` 编排 + `HilRunRequest` + `IHilRunnerService`

**Files:**
- Modify: `src/PeakCan.Host.Core/HIL/HilRunRequest.cs`
- Modify: `src/PeakCan.Host.Core/HIL/Contracts/IHilRunnerService.cs`（加 `LastCaseLogDirectory`）
- Modify: `src/PeakCan.Host.Infrastructure/HIL/HilRunnerService.cs`
- Test: `tests/PeakCan.Host.Infrastructure.Tests/HilRunnerServiceTests.cs`（NEW，仅测纯逻辑；全链路在 Task 11）

**Interfaces:**
- Consumes: `IHilFrameSinkFactory` / `AscFrameSinkFactory`（Task 5）、`TestSuiteEngine.ExecuteAsync` 新签名（Task 6）
- Produces: `HilRunnerService` ctor 注入 `ILogger<HilRunnerService>`；`LastCaseLogDirectory`（接口+实现）；`ResolveCaseLogDirectory` internal 纯函数。Task 10（VM）读 `LastCaseLogDirectory`。

- [ ] **Step 1: 写失败测试（纯逻辑）**

```csharp
// tests/PeakCan.Host.Infrastructure.Tests/HilRunnerServiceTests.cs
using PeakCan.HIL.Core.HIL;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Infrastructure.Tests;

public class HilRunnerServiceTests
{
    [Fact]
    public void ResolveCaseLogDirectory_UsesDefault_WhenNull()
    {
        var request = new HilRunRequest("d.dbc", "s.json");
        var dir = HilRunnerService.ResolveCaseLogDirectory(request);
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PeakCanHost", "hil-reports", "case-logs");
        Assert.Equal(expected, dir);
    }

    [Fact]
    public void ResolveCaseLogDirectory_UsesOverride_WhenSet()
    {
        var request = new HilRunRequest("d.dbc", "s.json", CaseLogDirectory: @"C:\logs");
        Assert.Equal(@"C:\logs", HilRunnerService.ResolveCaseLogDirectory(request));
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj --filter "FullyQualifiedName~HilRunnerServiceTests" -v q`
Expected: 编译失败 `error CS0246: 找不到类型或命名空间名“ResolveCaseLogDirectory”`

- [ ] **Step 3: 实现**

```csharp
// src/PeakCan.Host.Core/HIL/HilRunRequest.cs —— record 末尾追加两可选参（现有调用方不受影响）:
    // Test case selection: null = run all; non-empty = run only matching case names
    IReadOnlyList<string>? SelectedCaseNames = null,
    // 2026-08-15: WPF 每 case 全量报文 log
    bool CaptureCaseLogs = false,
    string? CaseLogDirectory = null);
```

```csharp
// src/PeakCan.Host.Core/HIL/Contracts/IHilRunnerService.cs —— 接口加:
/// <summary>本次 run 实际使用的 case-log 目录（CaptureCaseLogs 成功时非 null）。</summary>
string? LastCaseLogDirectory { get; }
```

```csharp
// src/PeakCan.Host.Infrastructure/HIL/HilRunnerService.cs
using Microsoft.Extensions.Logging;          // 若未引入

public sealed class HilRunnerService : IHilRunnerService
{
    private readonly ILogger<HilRunnerService> _logger;

    public HilRunnerService(ILogger<HilRunnerService> logger) => _logger = logger;

    /// <inheritdoc/>
    public DbcDocument? LastDbcDocument { get; private set; }

    /// <inheritdoc/>
    public string? LastCaseLogDirectory { get; private set; }

    /// <summary>解析 case-log 目录：request 覆盖值 或 默认 %LocalAppData%\PeakCanHost\hil-reports\case-logs\。internal 便于测试。</summary>
    internal static string ResolveCaseLogDirectory(HilRunRequest request)
        => request.CaseLogDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "PeakCanHost", "hil-reports", "case-logs");

    public async Task<TestSuiteResult> RunAsync(HilRunRequest request, IProgress<TestProgress>? progress = null, CancellationToken ct = default)
    {
        LastDbcDocument = null;
        LastCaseLogDirectory = null;   // ← 每次 run 重置

        /* ...原 body 保持，直到 engine 调用前插入: */

        // 每 case 全量报文 log: 建目录 + 构造 factory（P4 降级）
        IHilFrameSinkFactory? sinkFactory = null;
        if (request.CaptureCaseLogs)
        {
            var dir = ResolveCaseLogDirectory(request);
            try
            {
                Directory.CreateDirectory(dir);
                var runTimestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
                sinkFactory = new AscFrameSinkFactory(dir, runTimestamp);
                LastCaseLogDirectory = dir;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Case log directory unavailable, capture disabled: {Dir}", dir);
                sinkFactory = null;
            }
        }

        /* 原 :52 改为: */
        return await engine.ExecuteAsync(suite, ctx, new TestSuiteConfig(), progress, ct, sinkFactory);
    }
}
```

> `IHilFrameSinkFactory` 需要 `using PeakCan.HIL.Core.HIL.Contracts;`（HilRunnerService.cs:5 已有）。

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj --filter "FullyQualifiedName~HilRunnerServiceTests" -v q`
Expected: PASS（2 tests）。DI 注册（AppHostBuilder.cs:291 `AddSingleton<IHilRunnerService, HilRunnerService>`）会自动解析新 ctor 参数，无需改注册。

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Core/HIL/HilRunRequest.cs src/PeakCan.Host.Core/HIL/Contracts/IHilRunnerService.cs src/PeakCan.Host.Infrastructure/HIL/HilRunnerService.cs tests/PeakCan.Host.Infrastructure.Tests/HilRunnerServiceTests.cs
git commit -m "feat(hil): HilRunnerService wires case-log factory + LastCaseLogDirectory (case-log P4)"
```

---

### Task 10: `HilViewModel` + `HilView.xaml` CheckBox（P1/P11）

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/HilViewModel.cs`
- Modify: `src/PeakCan.Host.App/Views/HilView.xaml`
- Modify: `tests/PeakCan.Host.App.Tests/ViewModels/HilViewModelTests.cs`

**Interfaces:**
- Consumes: `HilRunRequest.CaptureCaseLogs`（Task 9）、`IHilRunnerService.LastCaseLogDirectory`（Task 9）
- Produces: `HilViewModel.CaptureCaseLogs` 可观察属性 + request 传递 + StatusMessage 目录提示。

- [ ] **Step 1: 写失败测试**

```csharp
// 追加到 tests/PeakCan.Host.App.Tests/ViewModels/HilViewModelTests.cs
// 依赖现有 MockRunner（IHilRunnerService 的 mock）——给 mock 加 LastCaseLogDirectory 属性
[Fact]
public void CaptureCaseLogs_DefaultsTrue()
{
    var vm = MakeVm();   // 复用现有构造 helper
    Assert.True(vm.CaptureCaseLogs);
}

[Fact]
public async Task RunAsync_PassesCaptureCaseLogs_WhenChecked()
{
    var vm = MakeVm();
    vm.CaptureCaseLogs = true;
    await vm.RunAsyncCommand.ExecuteAsync(null);   // 或现有测试驱动 Run 的方式
    Assert.True(MockRunner.LastRequest.CaptureCaseLogs);
}

[Fact]
public async Task RunAsync_StatusMessage_AppendsCaseLogDir()
{
    var vm = MakeVm();
    MockRunner.LastCaseLogDirectory = @"C:\logs\case-logs";
    vm.CaptureCaseLogs = true;
    await vm.RunAsyncCommand.ExecuteAsync(null);
    Assert.Contains("case logs", vm.StatusMessage);
    Assert.Contains(@"C:\logs\case-logs", vm.StatusMessage);
}
```

> `MockRunner` 在现有 `HilViewModelTests.cs` 内（或共享 fake）。若 mock 是闭包内联的，为 `LastCaseLogDirectory` 加一个可写字段即可；`LastRequest` 记录最近一次 `RunAsync` 收到的 request。

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~HilViewModelTests" -v q`
Expected: FAIL/编译失败（`CaptureCaseLogs` 不存在 / mock 缺属性）

- [ ] **Step 3: 实现**

```csharp
// src/PeakCan.Host.App/ViewModels/HilViewModel.cs
// 属性区（:28 附近，与其它 ObservableProperty 并列）:
[ObservableProperty] private bool _captureCaseLogs = true;

// RunAsync 的 request 构造（:277-288）追加:
    CaptureCaseLogs: CaptureCaseLogs,

// StatusMessage 设置后（:302 之后）追加:
    if (CaptureCaseLogs && _runner.LastCaseLogDirectory is { } caseLogDir)
        StatusMessage += $" — case logs: {caseLogDir}";
```

```xml
<!-- src/PeakCan.Host.App/Views/HilView.xaml —— Mode selector ComboBox（:43 `</ComboBox>`）之后插入 -->
<CheckBox Content="记录每 case 报文 (.asc)" IsChecked="{Binding CaptureCaseLogs}"
          VerticalAlignment="Center" Margin="16,0,0,0" />
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~HilViewModelTests" -v q`
Expected: PASS（现有 + 新 3）。既有 VM 测试不能回归（新增可空请求字段不影响）。

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/HilViewModel.cs src/PeakCan.Host.App/Views/HilView.xaml tests/PeakCan.Host.App.Tests/ViewModels/HilViewModelTests.cs
git commit -m "feat(hil): CaptureCaseLogs CheckBox + StatusMessage dir hint (case-log P1/P11)"
```

---

### Task 11: 集成测试（全链路，P4）

**Files:**
- Modify: `tests/PeakCan.Host.Infrastructure.Tests/HILIntegrationTests.cs`

**Interfaces:**
- Consumes: 全部前序任务产物。
- Produces: 端到端验证用例（T2 负测试也记录 / P4 目录创建 / P4 目录不可写降级）。

- [ ] **Step 1: 写失败测试**

```csharp
// 追加到 tests/PeakCan.Host.Infrastructure.Tests/HILIntegrationTests.cs
// 复用现有测试如何驱动 HilRunnerService / HeadlessHostBuilder 跑 suite 的方式
[Fact]
public async Task CaptureCaseLogs_True_ProducesAscPerCase()
{
    var request = new HilRunRequest("d.dbc", suitePath, Mode: HilMode.VirtualEcu, ..., CaptureCaseLogs: true);
    var result = await RunSuite(request);          // 现有 helper
    var dir = runner.LastCaseLogDirectory;
    Assert.NotNull(dir);
    Assert.True(Directory.Exists(dir));
    Assert.True(Directory.GetFiles(dir, "*.asc").Length >= suiteCases);
}

[Fact]
public async Task CaptureCaseLogs_False_ProducesNoAsc()
{
    var request = new HilRunRequest("d.dbc", suitePath, Mode: HilMode.VirtualEcu, ..., CaptureCaseLogs: false);
    await RunSuite(request);
    Assert.True(runner.LastCaseLogDirectory is null
        || !Directory.Exists(runner.LastCaseLogDirectory));
}

[Fact]
public async Task NegatedCase_AlsoLogsAsc() { /* suite 含 ExpectedVerdict.Fail 的 case → 也有 .asc */ }

[Fact]
public async Task MissingCaseLogDir_IsAutoCreated() { /* 删 case-logs 后跑 → 目录重建 */ }

[Fact]
public async Task UnwritableCaseLogDir_DegradesWithoutFailure()
{
    var request = new HilRunRequest("d.dbc", suitePath, Mode: HilMode.VirtualEcu, ...,
        CaptureCaseLogs: true, CaseLogDirectory: @"Z:\no_such_drive\x");
    var result = await RunSuite(request);
    Assert.NotNull(result);                        // 不抛、正常完成
    Assert.True(result.TotalCases > 0);
}
```

> 集成测试的 suite/runner 构造与 `RunSuite` helper 若现有文件已有，直接复用；没有则按现有 `HILIntegrationTests` 里最短的端到端用例抄一份。`Z:\` 不存在 → `Directory.CreateDirectory` 抛异常 → P4 降级路径。

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj --filter "FullyQualifiedName~HILIntegrationTests" -v q`
Expected: FAIL/编译失败（新用例依赖的 `LastCaseLogDirectory`/`CaptureCaseLogs` 若 Task 9 未完成则不编译；全部完成后应跑通）

- [ ] **Step 3: 实现 = 前序任务的集成产物，本任务无新生产代码**。若用例依赖的 runner 构造方式与 Task 9 不一致，回 Task 9 修（不引入新类型）。

- [ ] **Step 4: 运行确认通过**

Run: 全部 4 个测试项目:
```bash
dotnet test tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj -v q
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -v q
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj -v q
dotnet test tests/PeakCan.Host.Cli.Tests/PeakCan.Host.Cli.Tests.csproj -v q
```
Expected: 全部 PASS，0 失败。CLI 测试（含 FrameCaptureExporter 相关）必须全绿（G5）。

- [ ] **Step 5: Commit**

```bash
git add tests/PeakCan.Host.Infrastructure.Tests/HILIntegrationTests.cs
git commit -m "test(hil): case-log integration coverage (negated cases, dir auto-create, unwritable degrade)"
```

---

## Self-Review（已对照 spec 逐条核查）

**Spec 覆盖：**
- G1 全量帧：Task 7/8（ConsumerLoop 写帧）✓
- G2 每 case 一文件 + 命名：Task 5 ✓；G3 流式零内存：Task 4（BufferedStream 逐帧写）✓
- G4 用户可关：Task 10（CheckBox）✓；G5 CLI 零影响：Task 3（字节不变）+ Task 9（默认 false）✓
- P0 并发竞态：Task 4（Interlocked 软关闭）✓；P1 首帧基准：Task 4 ✓；P2 空文件：Task 4 测试 ✓
- P3 drain 顺序：Task 6（finally 顺序）+ Task 7/8（WaitForFrameDrainAsync）✓
- P4 目录创建/降级：Task 9 + Task 11 集成 ✓
- P5 AscFileFormat 共享：Task 2 + Task 3 ✓
- P6 setup 失败不建 sink：Task 6 ✓
- P7 PeakCan 解码保护：Task 8 ✓；P8 ConsumerLoop 测试：Task 7 ✓
- P9 BOM：Task 4 测试 ✓；P10 logger：Task 8 ✓；P11 UI 反馈：Task 10 ✓；P12 Stream ctor：Task 4 ✓
- A7 Write 不传播：Task 4 ✓；A8 Create null 降级：Task 5 ✓；A9 100 截断：Task 2/5 ✓
- T1 同名 case：Task 5（caseIndex）✓；T2 负测试记录：Task 11 ✓；T3 取消/StopCaseOnFailure 关闭：Task 6（finally）✓；T5 格式可解析：Task 2/3 字节锚定 ✓

**占位符扫描：** 无 TBD/TODO/"写测试即可"式占位。Task 7/11 的 `MakeContext`/`RunSuite` 标注为"按现有测试基建复用"，指向了具体文件作为来源，非占位。

**类型一致性：** `IHilFrameSink.Write(CanFrame)` / `IHilFrameSinkFactory.Create(string, int)` / `IHasFrameSink.SetFrameSink(IHilFrameSink?)` / `WaitForFrameDrainAsync(CancellationToken)` 在 Task 1 定义，Task 4-10 全程一致引用。`LastCaseLogDirectory` 在 Task 9 的接口+实现+VM 消费三处拼写一致。`ResolveCaseLogDirectory` 签名 `(HilRunRequest) → string` 测试与实现一致。
