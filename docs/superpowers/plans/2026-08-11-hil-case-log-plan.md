# WPF 每 case 全量报文流式 log 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** WPF 跑 HIL 时，每个 test case 生成一个独立的全量 CAN 报文 `.asc` 文件，流式写入，零内存增长。

**Architecture:** `IFrameSink` 接口（Core 层）+ `IFrameSinkFactory` 注入 `TestSuiteEngine`，在 case 生命周期边界挂载/摘除 `AscFrameSink`（Infrastructure 层）。`HILAssertionContext.ConsumerLoop` 每帧通过 `Volatile.Read` 写入当前 sink。WPF 面板 CheckBox 绑定 `CaptureCaseLogs` 传入 `HilRunRequest`。

**Tech Stack:** .NET 10, C#, PEAK ASCII (.asc) 格式, xUnit, NSubstitute, BufferedStream

## Global Constraints

- 所有新增接口放 `Core/HIL/Contracts/` 命名空间 `PeakCan.HIL.Core.HIL.Contracts`
- 所有 Infrastructure 实现放 `Infrastructure/HIL/` 命名空间 `PeakCan.Host.Infrastructure.HIL`
- `AscFrameSink.Write` 内部 try/catch 所有异常，不向 consumer loop 传播（A7）
- `AscFrameSinkFactory.Create` 内部 try/catch，失败返回 null 降级（A8）
- `SanitizeFileName` 截断到 100 字符（A9）
- `.asc` 格式与 `FrameCaptureExporter.cs:52-87` 一致（header + 帧行格式）
- `_frameSink` 跨线程访问：引擎线程 `Volatile.Write`，consumer 线程 `Volatile.Read`
- sink 内部 `_disposed` Interlocked 标志 + `Write` 前置检查软关闭（A1）
- 首帧时间基准 `_timestampOffsetUs ??= frame.Timestamp`（A2）
- CLI 不传 factory → 行为零影响（`sinkFactory=null` 可选参数）

---

### Task 1: Core 接口 — IFrameSink / IFrameSinkFactory / IHasFrameSink

**Files:**
- Create: `src/PeakCan.Host.Core/HIL/Contracts/IFrameSink.cs`

**Interfaces:**
- Consumes: 无（最底层接口）
- Produces: `IFrameSink`、`IFrameSinkFactory`、`IHasFrameSink`（供 Task 2/3/4/5 消费）

- [ ] **Step 1: 写接口定义**

```csharp
namespace PeakCan.HIL.Core.HIL.Contracts;

/// <summary>
/// 流式 CAN 帧记录器。Write 由 consumer 单线程调用；Dispose 后 Write 必须静默丢弃。
/// Write 内部必须 try/catch 所有异常，不向调用方传播。
/// </summary>
public interface IFrameSink : IDisposable
{
    void Write(CanFrame frame);
}

/// <summary>
/// 按 case 创建帧 sink。工厂由 HilRunnerService 一次性构造，跨 case 复用。
/// Create 内部必须 try/catch，失败返回 null 降级（不阻断测试）。
/// </summary>
public interface IFrameSinkFactory
{
    IFrameSink? Create(string caseName, int caseIndex);
}

/// <summary>
/// IAssertionContext 的可选扩展：挂载/摘除帧 sink。
/// 实现方必须用 Volatile 语义读写 _frameSink 字段（跨线程：引擎写，consumer 读）。
/// </summary>
public interface IHasFrameSink
{
    void SetFrameSink(IFrameSink? sink);
}
```

- [ ] **Step 2: 编译验证**

```bash
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -v q --nologo
```
Expected: build 成功

- [ ] **Step 3: Commit**

```bash
git add src/PeakCan.Host.Core/HIL/Contracts/IFrameSink.cs
git commit -m "feat: add IFrameSink/IFrameSinkFactory/IHasFrameSink interfaces"
```

---

### Task 2: AscFrameSink 流式 .asc 写入器

**Files:**
- Create: `src/PeakCan.Host.Infrastructure/HIL/AscFrameSink.cs`
- Test: `tests/PeakCan.Host.Infrastructure.Tests/HIL/AscFrameSinkTests.cs`

**Interfaces:**
- Consumes: `IFrameSink`（Task 1）
- Produces: `AscFrameSink : IFrameSink`（供 Task 3 消费）

- [ ] **Step 1: 写 AscFrameSinkTests 测试**

```csharp
using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

public sealed class AscFrameSinkTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "AscFrameSinkTests_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Write_MultipleFrames_FileContainsFrameLines()
    {
        // Arrange
        var path = Path.Combine(_tempDir, "test.asc");
        using var sink = new AscFrameSink(path);
        var frames = new[]
        {
            new CanFrame(new CanId(0x123, false), 0, new byte[] { 0x01, 0x02 }),
            new CanFrame(new CanId(0x456, false), 1000, new byte[] { 0xAA, 0xBB }),
        };

        // Act
        foreach (var f in frames) sink.Write(f);
        sink.Dispose();

        // Assert
        var text = File.ReadAllText(path);
        Assert.Contains("base hex  timestamps absolute", text);
        Assert.Contains("1  0x123x       Rx d 2 01 02", text);
        Assert.Contains("1  0x456x       Rx d 2 AA BB", text);
    }

    [Fact]
    public void FirstFrameTimestamp_IsZeroOffset()
    {
        var path = Path.Combine(_tempDir, "offset.asc");
        using var sink = new AscFrameSink(path);
        var t0 = TimeSpan.FromSeconds(1000); // 任意大基准
        var t1 = TimeSpan.FromSeconds(1001.5);
        sink.Write(new CanFrame(default, t0, Array.Empty<byte>()));
        sink.Write(new CanFrame(default, t1, Array.Empty<byte>()));
        sink.Dispose();

        var text = File.ReadAllText(path);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // 第一帧 seconds=0，第二帧 seconds=1.5
        Assert.Contains(" 0.000000", lines[^2]);
        Assert.Contains(" 1.500000", lines[^1]);
    }

    [Fact]
    public void Dispose_Idempotent_DoesNotThrow()
    {
        var path = Path.Combine(_tempDir, "idempotent.asc");
        var sink = new AscFrameSink(path);
        sink.Dispose();
        sink.Dispose(); // 第二次不抛
    }

    [Fact]
    public void Write_AfterDispose_SilentlyDrops()
    {
        var path = Path.Combine(_tempDir, "after.asc");
        var sink = new AscFrameSink(path);
        sink.Write(new CanFrame(default, TimeSpan.Zero, new byte[] { 0x01 }));
        sink.Dispose();
        sink.Write(new CanFrame(default, TimeSpan.Zero, new byte[] { 0x02 })); // 不抛
        var text = File.ReadAllText(path);
        // 只有一帧（第一帧），第二帧被丢弃
        var frameLines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(l => l.Contains("x       Rx"));
        Assert.Equal(1, frameLines);
    }

    [Fact]
    public void NoFrames_FileHasOnlyHeader()
    {
        var path = Path.Combine(_tempDir, "empty.asc");
        using var sink = new AscFrameSink(path);
        // 一帧不写，直接 Dispose
        var text = File.ReadAllText(path);
        Assert.Contains("base hex  timestamps absolute", text);
        Assert.DoesNotContain("x       Rx", text);
    }

    [Fact]
    public void Write_WhenBufferedStreamThrows_DoesNotPropagate()  // A7
    {
        // 模拟写失败：传入一个不可写的路径（如只读目录）
        // 实际测试用 mock 更稳定，但 AscFrameSink 内部 try/catch 是防御性的，
        // 这里验证 Write 不抛即可
        var path = Path.Combine(_tempDir, "throw.asc");
        var sink = new AscFrameSink(path);
        // 先关掉底层流模拟失败场景
        sink.Dispose();
        // Write 不应抛
        var ex = Record.Exception(() =>
            sink.Write(new CanFrame(default, TimeSpan.Zero, new byte[] { 0x01 })));
        Assert.Null(ex);
    }

    public void Dispose() { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
}
```

- [ ] **Step 2: 运行测试 -> 预期失败（类还没有）**

```bash
dotnet test tests/PeakCan.Host.Infrastructure.Tests/ --filter "AscFrameSink" --no-restore -v q --nologo 2>&1 | head -5
```

- [ ] **Step 3: 写 AscFrameSink 实现**

```csharp
using System.Globalization;
using System.Text;
using PeakCan.HIL.Core;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// 流式 CAN 帧 → PEAK ASCII (.asc) 文件。BufferedStream 缓冲，Dispose 时 flush+close。
/// 首帧时间戳作为 offset 基准。Write 内部 try/catch 所有异常，不向 consumer loop 传播。
/// 线程安全：Write 由 consumer 单线程调用；Dispose 与 Write 竞态由 Interlocked 软关闭标志保护。
/// </summary>
internal sealed class AscFrameSink : IFrameSink
{
    private readonly FileStream _fs;
    private readonly BufferedStream _buffered;
    private readonly StreamWriter _writer;
    private int _disposed;                      // Interlocked 标志，0=活跃，1=已关闭
    private double? _timestampOffsetUs;         // 首帧时间戳基准（A2）
    private int _frameCount;

    public AscFrameSink(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _buffered = new BufferedStream(_fs);
        _writer = new StreamWriter(_buffered, Encoding.UTF8);
        WriteHeader();
    }

    public void Write(CanFrame frame)
    {
        if (Volatile.Read(ref _disposed) != 0) return;  // 软关闭：Dispose 后丢弃
        try
        {
            _timestampOffsetUs ??= frame.Timestamp.TotalMicroseconds;
            var elapsedUs = frame.Timestamp.TotalMicroseconds - _timestampOffsetUs.Value;
            var seconds = elapsedUs / 1_000_000.0;

            var idStr = frame.Id.IsExtended
                ? $"0x{frame.Id.Raw:X8}"
                : $"0x{frame.Id.Raw:X3}";
            var dlc = frame.Data.Length;
            var dataHex = BitConverter.ToString(frame.Data.Span.ToArray()).Replace("-", " ");

            _writer.WriteLine(
                $"{seconds,12:F6} 1  {idStr,-12}x       Rx d {dlc} {dataHex}");
            _frameCount++;
        }
        catch (Exception)
        {
            // A7: IO 异常（磁盘满、权限等）静默降级，不向 consumer loop 传播
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;  // 幂等
        try
        {
            _buffered.Flush();
            _writer.Flush();
            _writer.Dispose();
            _buffered.Dispose();
            _fs.Dispose();
        }
        catch (Exception)
        {
            // Dispose 阶段异常不抛
        }
    }

    private void WriteHeader()
    {
        // 复用 FrameCaptureExporter 格式
        _writer.WriteLine($"date Fri Jan 01 00:00:00.000 {DateTime.Now:yyyy}");
        _writer.WriteLine("base hex  timestamps absolute");
        _writer.WriteLine("internal events logged");
        _writer.WriteLine("// version 8.5.0");
    }
}
```

- [ ] **Step 4: 运行测试 -> 全部通过**

```bash
dotnet test tests/PeakCan.Host.Infrastructure.Tests/ --filter "AscFrameSink" -v q --nologo
```

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Infrastructure/HIL/AscFrameSink.cs tests/PeakCan.Host.Infrastructure.Tests/HIL/AscFrameSinkTests.cs
git commit -m "feat: add AscFrameSink streaming .asc writer"
```

---

### Task 3: AscFrameSinkFactory 命名 + 目录

**Files:**
- Create: `src/PeakCan.Host.Infrastructure/HIL/AscFrameSinkFactory.cs`
- Test: `tests/PeakCan.Host.Infrastructure.Tests/HIL/AscFrameSinkFactoryTests.cs`

**Interfaces:**
- Consumes: `IFrameSinkFactory`（Task 1）、`AscFrameSink`（Task 2）
- Produces: `AscFrameSinkFactory : IFrameSinkFactory`（供 Task 7 消费）

- [ ] **Step 1: 写 AscFrameSinkFactoryTests**

```csharp
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

public sealed class AscFrameSinkFactoryTests
{
    [Fact]
    public void Create_NormalPath_ReturnsSink()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sinkfactory_" + Guid.NewGuid().ToString("N"));
        var factory = new AscFrameSinkFactory(dir, "20260811120000000");
        using var sink = factory.Create("MyCase", 0);
        Assert.NotNull(sink);
        // 文件已创建（header 已写）
        var files = Directory.GetFiles(dir, "*.asc");
        Assert.Single(files);
        Assert.Contains("MyCase_0_20260811120000000", files[0]);
    }

    [Fact]
    public void Create_DirectoryNotWritable_ReturnsNull()  // A8
    {
        // 在只读目录或不存在路径测试；由于跨平台只读目录不好模拟，
        // 用不存在父目录的路径（Create 会尝试创建目录，失败返回 null）
        var dir = Path.Combine("Z:\\invalid_should_not_exist_xyz", "case-logs");
        var factory = new AscFrameSinkFactory(dir, "ts");
        var sink = factory.Create("AnyCase", 0);
        Assert.Null(sink);
    }

    [Fact]
    public void Create_LongCaseName_TruncatesTo100Chars()  // A9
    {
        var dir = Path.Combine(Path.GetTempPath(), "trunc_" + Guid.NewGuid().ToString("N"));
        var longName = new string('A', 200);
        var factory = new AscFrameSinkFactory(dir, "ts");
        using var sink = factory.Create(longName, 0);
        Assert.NotNull(sink);
        var files = Directory.GetFiles(dir, "*.asc");
        var fileName = Path.GetFileNameWithoutExtension(files[0]);
        // 截断后应为 100_0_ts
        Assert.Equal(100 + 1 + 1 + 1 + 2, fileName.Length);  // 100 + '_' + index + '_' + ts
        Assert.StartsWith(new string('A', 100) + "_0_ts", fileName);
    }

    [Fact]
    public void Create_SameCaseNameDifferentIndex_UniqueNames()
    {
        var dir = Path.Combine(Path.GetTempPath(), "unique_" + Guid.NewGuid().ToString("N"));
        var factory = new AscFrameSinkFactory(dir, "ts");
        using var s1 = factory.Create("SameCase", 0);
        using var s2 = factory.Create("SameCase", 1);
        Assert.NotNull(s1);
        Assert.NotNull(s2);
        var files = Directory.GetFiles(dir, "*.asc").OrderBy(f => f).ToArray();
        Assert.Equal(2, files.Length);
        Assert.NotEqual(files[0], files[1]);
    }
}
```

- [ ] **Step 2: 运行测试 -> 预期失败**

```bash
dotnet test tests/PeakCan.Host.Infrastructure.Tests/ --filter "AscFrameSinkFactory" --no-restore -v q --nologo 2>&1 | head -5
```

- [ ] **Step 3: 写 AscFrameSinkFactory 实现**

```csharp
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// 按 case 名 + run 时间戳 + case index 命名。目录默认 <reportDir>/case-logs/。
/// </summary>
internal sealed class AscFrameSinkFactory : IFrameSinkFactory
{
    private readonly string _directory;
    private readonly string _runTimestamp;

    public AscFrameSinkFactory(string directory, string runTimestamp)
    {
        _directory = directory;
        _runTimestamp = runTimestamp;
    }

    public IFrameSink? Create(string caseName, int caseIndex)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var safeName = SanitizeFileName(caseName, maxLength: 100);  // A9: 截断到 100 字符
            var fileName = $"{safeName}_{caseIndex}_{_runTimestamp}.asc";
            return new AscFrameSink(Path.Combine(_directory, fileName));
        }
        catch (Exception)
        {
            // A8: 目录不可写/权限不足 → 返回 null 降级，不阻断测试
            return null;
        }
    }

    /// <summary>
    /// 清洗文件名：非法字符替换为 _，并截断到 maxLength。
    /// </summary>
    private static string SanitizeFileName(string name, int maxLength = 100)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        }
        if (sb.Length > maxLength)
            sb.Length = maxLength;
        return sb.ToString();
    }
}
```

- [ ] **Step 4: 运行测试 -> 全部通过**

```bash
dotnet test tests/PeakCan.Host.Infrastructure.Tests/ --filter "AscFrameSinkFactory" -v q --nologo
```

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Infrastructure/HIL/AscFrameSinkFactory.cs tests/PeakCan.Host.Infrastructure.Tests/HIL/AscFrameSinkFactoryTests.cs
git commit -m "feat: add AscFrameSinkFactory with path truncation and error resilience"
```

---

### Task 4: TestSuiteEngine — sink 生命周期注入

**Files:**
- Modify: `src/PeakCan.Host.Core/HIL/TestSuiteEngine.cs`
- Modify: `tests/PeakCan.Host.Core.Tests/HIL/TestSuiteEngineTests.cs`

**Interfaces:**
- Consumes: `IFrameSinkFactory`（Task 1）、`IHasFrameSink`（Task 1）、`IFrameSink`（Task 1）
- Produces: 修改后的 `ExecuteAsync` 签名（可选参数 `IFrameSinkFactory?`），供 Task 7 调用

- [ ] **Step 1: 修改 TestSuiteEngine.ExecuteAsync → 加可选参数**

```csharp
// 原签名（:24-29）
public async Task<TestSuiteResult> ExecuteAsync(
    TestSuite suite,
    Contracts.IAssertionContext ctx,
    TestSuiteConfig config,
    IProgress<TestProgress>? progress = null,
    CancellationToken externalCt = default,
    IFrameSinkFactory? sinkFactory = null)     // ← 新增
```

- [ ] **Step 2: 修改 ExecuteCaseAsync → 在 case 边界挂载/摘除 sink**

在 `ExecuteCaseAsync` 方法中，`var stepResults = new List<StepResult>();` 之后、`var globalFixtures = ...` 之前，添加：

```csharp
// 2026-08-11: 全量报文 log — 在 case 生命周期的 clean 边界挂载 sink
IFrameSink? caseSink = null;
if (ctx is IHasFrameSink hasSink && sinkFactory is not null)
    hasSink.SetFrameSink(caseSink = sinkFactory.Create(testCase.Name, caseIndex));
```

在 `caseStopwatch.Stop();` 之后、`// Aggregate` 之前，添加 finally 块包裹现有逻辑。注意现有的 `ExecuteCaseAsync` 没有 try-finally 包裹 steps 部分。需要把 fixtures setup → steps → teardown 整个包进 try-finally，或者只在 steps 部分包。更精确的做法：在 case 开始处 try-finally，确保 sink 一定被清理。

但因为 `ExecuteCaseAsync` 内部有多个 return 路径（setup 失败、steps 完成、teardown 失败），最安全的方式是：

```csharp
// 在 steps 执行前后（:128-218 的 if (failureReason is null) 块）包 try-finally：
if (failureReason is null)
{
    try
    {
        // ... 现有 steps 循环 ...
    }
    finally
    {
        if (ctx is IHasFrameSink hasSink2) hasSink2.SetFrameSink(null);
        caseSink?.Dispose();
    }
}
```

但注意：fixture teardown 在 steps 之后执行（`:222-230`），sink 应该在 teardown 之后才关闭（如果希望记录 teardown 帧）。但 spec 说范围是 steps 阶段，所以在 steps 结束后关闭 sink 是正确的。teardown 的帧不记录。

实际上更准确的位置：在最后 `caseStopwatch.Stop();` 之后、`// Aggregate` 之前，把 sink 摘除+Dispose。因为 case 的整个执行（含 teardown）已经结束。

让我重新设计：

```csharp
// 在 ExecuteCaseAsync 开头、fixture 之前：
IFrameSink? caseSink = null;
if (ctx is IHasFrameSink hasSink && sinkFactory is not null)
    hasSink.SetFrameSink(caseSink = sinkFactory.Create(testCase.Name, caseIndex));

try
{
    // ... 全部现有逻辑（fixture setup → steps → fixture teardown）...
}
finally
{
    if (ctx is IHasFrameSink hasSink2) hasSink2.SetFrameSink(null);
    caseSink?.Dispose();
}
return caseResult;
```

这样无论哪个路径（异常/取消/停止），sink 都保证关闭。但需要把 `return new TestCaseResult(...)` 移出 try 块，用变量存结果。

更简洁：把整个 `ExecuteCaseAsync` 方法体包进 try-finally：

```csharp
private async Task<TestCaseResult> ExecuteCaseAsync(
    TestCase testCase, Contracts.IAssertionContext ctx, TestSuiteConfig config, CancellationToken ct,
    IFrameSinkFactory? sinkFactory, int caseIndex)  // 新增参数
{
    IFrameSink? caseSink = null;
    if (ctx is IHasFrameSink hasSink && sinkFactory is not null)
        hasSink.SetFrameSink(caseSink = sinkFactory.Create(testCase.Name, caseIndex));
    try
    {
        // ... 全部现有逻辑 ...
        return new TestCaseResult(...);
    }
    finally
    {
        if (ctx is IHasFrameSink hasSink2) hasSink2.SetFrameSink(null);
        caseSink?.Dispose();
    }
}
```

这样最干净，不需要迁移 return 语句，try-finally 保证所有路径。

- [ ] **Step 3: 修改 `ExecuteAsync` 循环传递 `caseIndex` 和 `sinkFactory`**

```csharp
// ExecuteAsync 内（:66-69）
int caseIndex = 0;
foreach (var caseModel in suite.Cases)
{
    linkedCt.ThrowIfCancellationRequested();
    var caseResult = await ExecuteCaseAsync(caseModel, ctx, config, linkedCt,
        sinkFactory, caseIndex);  // ← 传新参数
    caseResults.Add(caseResult);
    // ...
    caseIndex++;
}
```

- [ ] **Step 4: 写 TestSuiteEngine 生命周期测试**

在 `TestSuiteEngineTests.cs` 中添加：

```csharp
[Fact]
public async Task ExecuteAsync_WithSinkFactory_CreatesAndDisposesSinkPerCase()
{
    // Arrange
    var sinkMock = new Mock<IFrameSink>();
    var factoryMock = new Mock<IFrameSinkFactory>();
    factoryMock.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<int>()))
        .Returns(sinkMock.Object);
    var ctx = new FakeAssertionContextWithSink();  // 实现 IHasFrameSink
    var engine = CreateEngine(simpleExecutor: true);

    // Act
    var result = await engine.ExecuteAsync(
        MakeSuite(2), ctx, DefaultConfig, sinkFactory: factoryMock.Object);

    // Assert
    factoryMock.Verify(f => f.Create(It.IsAny<string>(), 0), Times.Once);
    factoryMock.Verify(f => f.Create(It.IsAny<string>(), 1), Times.Once);
    sinkMock.Verify(s => s.Dispose(), Times.Exactly(2));
}

[Fact]
public async Task ExecuteAsync_StepExecutorThrows_SinkStillDisposed()
{
    var sinkMock = new Mock<IFrameSink>();
    var factoryMock = new Mock<IFrameSinkFactory>();
    factoryMock.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<int>()))
        .Returns(sinkMock.Object);
    var ctx = new FakeAssertionContextWithSink();
    var engine = CreateEngine(executorThatThrows: true);

    var result = await engine.ExecuteAsync(
        MakeSuite(1), ctx, DefaultConfig, sinkFactory: factoryMock.Object);

    sinkMock.Verify(s => s.Dispose(), Times.Once);
}

[Fact]
public async Task ExecuteAsync_NoSinkFactory_BehavesSameAsBefore()
{
    // 回归：不传 factory → 行为与改动前完全一致
    var engine = CreateEngine(simpleExecutor: true);
    var ctx = new FakeAssertionContextWithSink();
    var result = await engine.ExecuteAsync(MakeSuite(1), ctx, DefaultConfig);
    // 不抛，不空
    Assert.NotNull(result);
}

[Fact]
public async Task ExecuteAsync_FactoryReturnsNull_NoError()
{
    var factoryMock = new Mock<IFrameSinkFactory>();
    factoryMock.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<int>()))
        .Returns((IFrameSink?)null);
    var ctx = new FakeAssertionContextWithSink();
    var engine = CreateEngine(simpleExecutor: true);

    var result = await engine.ExecuteAsync(
        MakeSuite(1), ctx, DefaultConfig, sinkFactory: factoryMock.Object);

    Assert.NotNull(result);
    Assert.True(result.Passed);
}

// 辅助：FakeAssertionContextWithSink
private sealed class FakeAssertionContextWithSink : FakeAssertionContext, IHasFrameSink
{
    private IFrameSink? _sink;
    public void SetFrameSink(IFrameSink? sink) => Volatile.Write(ref _sink, sink);
}
```

- [ ] **Step 5: 编译 + 运行测试**

```bash
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -v q --nologo
dotnet test tests/PeakCan.Host.Core.Tests/ --filter "TestSuiteEngine" -v q --nologo
```

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.Core/HIL/TestSuiteEngine.cs tests/PeakCan.Host.Core.Tests/HIL/TestSuiteEngineTests.cs
git commit -m "feat: TestSuiteEngine IFrameSinkFactory lifecycle per case"
```

---

### Task 5: HILAssertionContext + PeakCanAssertionContext — IHasFrameSink 实现

**Files:**
- Modify: `src/PeakCan.Host.Infrastructure/HIL/HILAssertionContext.cs`
- Modify: `src/PeakCan.Host.Infrastructure/HIL/PeakCanAssertionContext.cs`
- Modify: `tests/PeakCan.Host.Infrastructure.Tests/HIL/HILAssertionContextConcurrencyTests.cs`（或新建）

**Interfaces:**
- Consumes: `IHasFrameSink`（Task 1）、`IFrameSink`（Task 1）
- Produces: 修改后的两 context（在 ConsumerLoop 内写帧）

- [ ] **Step 1: 修改 HILAssertionContext**

在 `_recentFrames` 字段附近加：
```csharp
private IFrameSink? _frameSink;
```

加方法：
```csharp
void IHasFrameSink.SetFrameSink(IFrameSink? sink)
    => Volatile.Write(ref _frameSink, sink);
```

在 `ConsumerLoop` 的 `_recentFrames.Add(frame);`（`:198`）之后加：
```csharp
Volatile.Read(ref _frameSink)?.Write(frame);
```

- [ ] **Step 2: 修改 PeakCanAssertionContext**

同上，在 `_recentFrames.Add(frame);`（`:141`）之后加：
```csharp
Volatile.Read(ref _frameSink)?.Write(frame);
```

- [ ] **Step 3: 写并发竞态测试**

在 `HILAssertionContextConcurrencyTests.cs` 或新建 `AscFrameSinkConcurrencyTests.cs` 中添加：

```csharp
[Fact]
public async Task SetFrameSink_DisposeWhileConsumerWrites_NoException()  // P0 A1
{
    // 模拟 consumer 持续写帧 + 引擎线程交替挂载/摘除 sink
    var mockSink = new Mock<IFrameSink>();
    mockSink.Setup(s => s.Write(It.IsAny<CanFrame>()))
        .Callback(() => Thread.SpinWait(100)); // 模拟写延迟
    var ctx = CreateContext();
    var hasSink = (IHasFrameSink)ctx;

    // 启动 fake frame producer
    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    var producer = Task.Run(async () =>
    {
        while (!cts.IsCancellationRequested)
        {
            hasSink.SetFrameSink(mockSink.Object);
            await Task.Delay(1);
            hasSink.SetFrameSink(null);
            mockSink.Object.Dispose();  // 模拟引擎线程 Dispose
            mockSink = new Mock<IFrameSink>();
        }
    });

    // 同时写帧
    for (int i = 0; i < 100; i++)
    {
        ctx.GetType().GetMethod("OnFrame", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.Invoke(ctx, new object[] { new CanFrame(default, TimeSpan.Zero, Array.Empty<byte>()) });
        await Task.Delay(1);
    }

    await producer;
    // 不抛 ObjectDisposedException = 通过
}
```

- [ ] **Step 4: 编译 + 运行测试**

```bash
dotnet build src/PeakCan.Host.Infrastructure/PeakCan.Host.Infrastructure.csproj -v q --nologo
dotnet test tests/PeakCan.Host.Infrastructure.Tests/ --filter "HILAssertionContext" -v q --nologo
```

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Infrastructure/HIL/HILAssertionContext.cs src/PeakCan.Host.Infrastructure/HIL/PeakCanAssertionContext.cs tests/PeakCan.Host.Infrastructure.Tests/HIL/ -A
git commit -m "feat: implement IHasFrameSink in HILAssertionContext and PeakCanAssertionContext"
```

---

### Task 6: HilRunRequest — 加 CaptureCaseLogs 字段

**Files:**
- Modify: `src/PeakCan.Host.Core/HIL/HilRunRequest.cs`

**Interfaces:**
- Consumes: 无
- Produces: 修改后的 `HilRunRequest` record（新增 `CaptureCaseLogs` + `CaseLogDirectory`），供 Task 7/8 消费

- [ ] **Step 1: 修改 HilRunRequest record**

```csharp
public sealed record HilRunRequest(
    string DbcPath,
    string SuitePath,
    string? TracePath = null,
    string? HardwareChannel = null,
    string Format = "console",
    uint UdsRequestId = 0x7DF,
    uint UdsResponseId = 0x7E8,
    // Phase 3 additions:
    string? EcuScriptPath = null,
    string? MatrixPath = null,
    bool EnableFaultInjection = false,
    // Sprint 12 additions:
    HilMode Mode = HilMode.TraceReplay,
    bool EnableAnalyze = false,
    // Phase 7 Unit B: external generator plugin directory
    string? GeneratorDir = null,
    // Test case selection: null = run all; non-empty = run only matching case names
    IReadOnlyList<string>? SelectedCaseNames = null,
    // 2026-08-11: WPF 每 case 全量报文 log
    bool CaptureCaseLogs = false,
    string? CaseLogDirectory = null);
```

- [ ] **Step 2: 编译验证**

```bash
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -v q --nologo
```
Expected: 0 错误（所有现有调用方都用命名参数，追加默认参数不破坏）

- [ ] **Step 3: Commit**

```bash
git add src/PeakCan.Host.Core/HIL/HilRunRequest.cs
git commit -m "feat: add CaptureCaseLogs and CaseLogDirectory to HilRunRequest"
```

---

### Task 7: HilRunnerService — 构造 factory + 传 engine

**Files:**
- Modify: `src/PeakCan.Host.Infrastructure/HIL/HilRunnerService.cs`

**Interfaces:**
- Consumes: `AscFrameSinkFactory`（Task 3）、`HilRunRequest`（Task 6）、`TestSuiteEngine`（Task 4）
- Produces: 修改后的 `RunAsync`（`CaptureCaseLogs=true` 时构造 factory 并传 engine）

- [ ] **Step 1: 修改 HilRunnerService.RunAsync**

在 `using var host = ...` 之后、`var engine = ...` 之前，添加：

```csharp
// 2026-08-11: 每 case 全量报文 log
var runTimestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
IFrameSinkFactory? sinkFactory = null;
if (request.CaptureCaseLogs)
{
    var dir = request.CaseLogDirectory
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PeakCanHost", "hil-reports", "case-logs");
    sinkFactory = new AscFrameSinkFactory(dir, runTimestamp);
}
```

修改 `engine.ExecuteAsync(suite, ctx, new TestSuiteConfig(), progress, ct)` 为：

```csharp
return await engine.ExecuteAsync(suite, ctx, new TestSuiteConfig(), progress, ct, sinkFactory);
```

- [ ] **Step 2: 编译验证**

```bash
dotnet build src/PeakCan.Host.Infrastructure/PeakCan.Host.Infrastructure.csproj -v q --nologo
```

- [ ] **Step 3: Commit**

```bash
git add src/PeakCan.Host.Infrastructure/HIL/HilRunnerService.cs
git commit -m "feat: HilRunnerService constructs AscFrameSinkFactory on CaptureCaseLogs=true"
```

---

### Task 8: HilViewModel + HilView — WPF 面板 CheckBox

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/HilViewModel.cs`
- Modify: `src/PeakCan.Host.App/Views/HilView.xaml`
- Modify: `tests/PeakCan.Host.App.Tests/ViewModels/HilViewModelTests.cs`

**Interfaces:**
- Consumes: `HilRunRequest`（Task 6）
- Produces: WPF 面板功能（CheckBox + 绑定 + request 传递）

- [ ] **Step 1: 修改 HilViewModel.cs**

在现有 `[ObservableProperty]` 区域添加：
```csharp
[ObservableProperty] private bool _captureCaseLogs = true;
```

在 `RunAsync` 的 `HilRunRequest` 构造参数中追加：
```csharp
CaptureCaseLogs: CaptureCaseLogs,
```

- [ ] **Step 2: 修改 HilView.xaml**

在现有控件区域（Mode selector 附近，或 HIL 右侧面板空白处）加 CheckBox：

```xml
<CheckBox Content="记录每 case 报文 (.asc)"
          IsChecked="{Binding CaptureCaseLogs}"
          Margin="0,0,16,0"
          VerticalAlignment="Center" />
```

- [ ] **Step 3: 修改 HilViewModelTests.cs**

在现有测试 helper 中 mock 不受影响（不改 ctor）。新增测试：

```csharp
[Fact]
public void CaptureCaseLogs_DefaultIsTrue()
{
    var vm = CreateViewModel();
    Assert.True(vm.CaptureCaseLogs);
}

[Fact]
public async Task RunAsync_SendsCaptureCaseLogsInRequest()
{
    var vm = CreateViewModel();
    vm.CaptureCaseLogs = true;
    // RunAsync 会调用 _runner.RunAsync，验证 request.CaptureCaseLogs == true
    // 需要 mock 捕获 request 参数
    HilRunRequest? capturedRequest = null;
    _runner.RunAsync(Arg.Do<HilRunRequest>(r => capturedRequest = r), Arg.Any<IProgress<TestProgress>>(), Arg.Any<CancellationToken>())
        .Returns(FakeResult);
    await vm.RunAsync();
    Assert.NotNull(capturedRequest);
    Assert.True(capturedRequest.CaptureCaseLogs);
}
```

- [ ] **Step 4: 编译 + 运行测试**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -v q --nologo 2>&1 | tail -5
dotnet test tests/PeakCan.Host.App.Tests/ --filter "HilViewModel" -v q --nologo
```

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/HilViewModel.cs src/PeakCan.Host.App/Views/HilView.xaml tests/PeakCan.Host.App.Tests/ViewModels/HilViewModelTests.cs
git commit -m "feat: add WPF CaptureCaseLogs CheckBox in HilView"
```

---

### Task 9: 集成测试

**Files:**
- Modify: `tests/PeakCan.Host.Infrastructure.Tests/HILIntegrationTests.cs`（或新建）

- [ ] **Step 1: 写集成测试**

```csharp
[Fact]
public async Task RunAsync_CaptureCaseLogsTrue_CreatesAscFiles()
{
    //  Arrange: 准备 suite + DBC + request
    var suiteDir = Path.Combine(Path.GetTempPath(), "hil_int_tests_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(suiteDir);
    var suitePath = Path.Combine(suiteDir, "suite.json");
    File.WriteAllText(suitePath, SimpleSuiteJson(2));  // 2 个 case
    var request = new HilRunRequest(
        DbcPath: "dummy.dbc",
        SuitePath: suitePath,
        TracePath: "dummy.asc",
        Mode: HilMode.TraceReplay,
        CaptureCaseLogs: true,
        CaseLogDirectory: Path.Combine(suiteDir, "case-logs"));

    var service = new HilRunnerService();
    var result = await service.RunAsync(request);

    // Assert: case-logs 目录有 2 个 .asc 文件
    var logDir = Path.Combine(suiteDir, "case-logs");
    Assert.True(Directory.Exists(logDir));
    var files = Directory.GetFiles(logDir, "*.asc");
    Assert.Equal(2, files.Length);
    // 每个文件含 header
    foreach (var f in files)
        Assert.Contains("base hex  timestamps absolute", File.ReadAllText(f));
}

[Fact]
public async Task RunAsync_CaptureCaseLogsFalse_NoAscFiles()
{
    // 同上，但 CaptureCaseLogs=false
    // Assert: case-logs 目录不存在或为空
}

[Fact]
public async Task RunAsync_NegatedTestCase_AlsoLogsFrames()
{
    // 含负测试用例的 suite → 仍生成 .asc
}
```

- [ ] **Step 2: 运行集成测试**

```bash
dotnet test tests/PeakCan.Host.Infrastructure.Tests/ --filter "RunAsync_CaptureCaseLogs" -v q --nologo
```

- [ ] **Step 3: Commit**

```bash
git add tests/PeakCan.Host.Infrastructure.Tests/ -A
git commit -m "test: integration tests for case log capture"
```

---

### Task 10: 最终验证

- [ ] **Step 1: 全量编译**

```bash
dotnet build peakcan-host/src/PeakCan.Host.Cli/PeakCan.Host.Cli.csproj -v q --nologo
```

- [ ] **Step 2: 全量测试**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/
dotnet test tests/PeakCan.Host.Infrastructure.Tests/
dotnet test tests/PeakCan.Host.App.Tests/
```

- [ ] **Step 3: CLI 回归验证**（确认 CLI 不传 factory 时行为不变）

```bash
# 找个已有的 CLI 测试确认
dotnet test tests/PeakCan.Host.Cli.Tests/
```

- [ ] **Step 4: Acceptance 清单检查**

对照 spec §8 逐条确认：
- [ ] WPF 跑 HIL，每个 case 生成 `{caseName}_{caseIndex}_{runTimestamp}.asc` 于 `hil-reports\case-logs\`
- [ ] 文件是合法 PEAK ASCII 格式，含 case 期间全部帧（非 50 帧 cap）
- [ ] 流式写入：case 跑 5 分钟内存不增长
- [ ] CheckBox 默认勾选；去勾 → 不生成任何 `.asc`
- [ ] case 异常/取消/StopCaseOnFailure → sink 仍关闭
- [ ] CLI 跑同一个 suite → 行为不变
- [ ] 同名 case 不互相覆盖（caseIndex 区分）
- [ ] 负测试 case 也生成 log

- [ ] **Step 5: 最终 commit（如有修复）**

```bash
git commit -m "feat: WPF HIL case log with integration tests"
```