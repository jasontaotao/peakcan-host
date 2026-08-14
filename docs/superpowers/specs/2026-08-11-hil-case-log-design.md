# Design: WPF HIL 每 case 全量报文流式 log（.asc）

> Spec date: 2026-08-11
> Depends: 现有 `HILAssertionContext` / `PeakCanAssertionContext` 帧环形缓冲 + `FrameCaptureExporter`（CLI `--export-frames`）
> Scope: **WPF HIL 运行，每个 test case 生成一个独立的全量 CAN 报文 `.asc` 文件，流式写入，零额外内存**。CLI 行为不变。
> Status: APPROVED（2026-08-11 用户确认 4 个决策点：范围=steps 阶段 / 开关默认勾选 / 空文件生成 / 3 个阻塞点防御方案）
> Rev 2: 2026-08-12 复审修订，新增 P3~P12 决议（尾部帧窗口 / 目录创建 / helper 复用 / fixture setup 失败 / PeakCan 解码保护 / ConsumerLoop 测试 / BOM 编码 / logger 注入 / UI 反馈 / 测试注入），见 §4.2

---

## 1. Goals

当前 HIL 帧捕获只覆盖**失败步骤周边 ≤50 帧**（`CircularBuffer<CanFrame> capacity: 50`，`HILAssertionContext.cs:31`），且：
- CLI 侧 `--export-frames` 每失败 case 一个 `.asc`（`FrameCaptureExporter.cs:43`）
- **WPF 侧无独立导出**，只有 HTML 报告内嵌帧转储（`HtmlReportGenerator.cs:164-199`）

本设计目标：

**G1. 全量报文** — WPF 跑 HIL 时，每个 test case 的 **steps 执行期间** CAN 总线上**所有**帧都落盘（不含失败/通过过滤）。

> **范围界定**：帧记录范围是 case 的 steps 执行阶段（`SetupAsync` → `fixture.TeardownAsync` 之前），**不包括 Case Fixture 的 Setup 和 Teardown**。理由：fixture 的帧是基础设施行为（如重置 ECU 会话），不属于测试关注点。如果后续需要 fixture 帧，可提前挂载点。
> **Rev 2 边界说明（P3）**：consumer 经 bounded channel（10000, DropOldest）异步消费，case 结束瞬间 channel 内积压帧需要排空窗口。见 §4.2 P3 决议——排空期间（≤500ms）到达的帧（含 teardown 前奏）可能一并入账，属可接受的轻微越界。
**G2. 每 case 一文件** — 一个 case 一个 `.asc` 文件，以 case 名 + run 时间戳命名。
**G3. 流式写入零内存** — 帧到达即写盘（BufferedStream），不在内存攒全量列表。
**G4. 用户可关** — WPF 面板加 CheckBox 开关，默认勾选。
**G5. CLI 零影响** — CLI 不传 sink factory，行为完全不变。

---

## 2. Current State（证据）

| 项 | 证据 |
|----|------|
| 帧环形缓冲 | `HILAssertionContext.cs:31` / `PeakCanAssertionContext.cs:27` — `CircularBuffer<CanFrame> _recentFrames = new(capacity: 50)` |
| 帧消费循环 | `HILAssertionContext.ConsumerLoop`（`HILAssertionContext.cs:189-264`）— 每帧 `_recentFrames.Add(frame)`（`:198`），单 consumer 线程（`Channel.ReadAllAsync`） |
| 失败快照 | `TestSuiteEngine.cs:196-203` — 步骤失败时 `ctx is IHasRecentFrames` → `GetRecentFrames()` 存入 `StepResult.FramesAroundFailure` |
| case 执行边界 | `TestSuiteEngine.ExecuteCaseAsync`（`TestSuiteEngine.cs:101-255`）— `ExecuteAsync` 逐 case 调用（`:66-69`），`HilRunnerService` 只调一次 `ExecuteAsync`（`HilRunnerService.cs:52`） |
| WPF 运行入口 | `HilViewModel.RunAsync`（`HilViewModel.cs:277-290`）— 构造 `HilRunRequest` → `_runner.RunAsync` |
| `HilRunRequest` 字段 | `HilRunRequest.cs:3-21` — record，含 `Format`/`SelectedCaseNames` 等 |
| CLI 导出格式 | `FrameCaptureExporter.WriteAscFileAsync`（`FrameCaptureExporter.cs:52-87`）— PEAK ASCII；header 固定、`date` 行固定日期、逐帧 `{seconds:F6} 1 {id}x Rx d {dlc} {data}`；offset 基准 = `frames[0].Timestamp`（`:63-65`） |
| 文件名清洗 | `FrameCaptureExporter.SanitizeFileName`（`FrameCaptureExporter.cs:92-101`）— 非法字符替换 `_`，**可复用** |
| 报告目录 | `HilReportService.ReportDirectory` = `%LocalAppData%\PeakCanHost\hil-reports\`（`HilReportService.cs:18`） |

---

## 3. Design

### 3.1 数据流

```
HilViewModel (WPF, CaptureCaseLogs CheckBox)
  │  HilRunRequest{ CaptureCaseLogs, CaseLogDirectory? }
  ▼
HilRunnerService.RunAsync
  │  CaptureCaseLogs=true → new AscFrameSinkFactory(dir, runTimestamp, baseDir)
  │  engine.ExecuteAsync(suite, ctx, config, progress, ct, sinkFactory)
  ▼
TestSuiteEngine.ExecuteCaseAsync
  │  case 开始 → sink = factory.Create(caseName, caseIndex) → ctx.SetFrameSink(sink)
  │  try { 执行 steps }
  │  finally { ctx.WaitForFrameDrainAsync(ct)  ← Rev 2 (P3): 有界排空在途帧
  │            ctx.SetFrameSink(null); sink?.Dispose() }   ← 异常/取消/StopCaseOnFailure 都保证关闭
  ▼
HILAssertionContext.ConsumerLoop
  │  每帧: _recentFrames.Add(frame)     ← 现有
  │        frameSink?.Write(frame)      ← 新增（Volatile.Read 读取）
```

### 3.2 新增接口（Core 层，纯接口零文件依赖）

```csharp
namespace PeakCan.HIL.Core.HIL.Contracts;

/// <summary>流式 CAN 帧记录器。Write 由 consumer 单线程调用；Dispose 后 Write 必须静默丢弃。</summary>
public interface IFrameSink : IDisposable
{
    void Write(CanFrame frame);
}

/// <summary>按 case 创建帧 sink。工厂由 HilRunnerService 一次性构造，跨 case 复用。</summary>
public interface IFrameSinkFactory
{
    /// <summary>为指定 case 创建 sink；返回 null = 该 case 不记录（预留 case 级跳过）。</summary>
    IFrameSink? Create(string caseName, int caseIndex);
}

/// <summary>IAssertionContext 的可选扩展：挂载/摘除帧 sink。</summary>
public interface IHasFrameSink
{
    void SetFrameSink(IFrameSink? sink);

    /// <summary>
    /// 有界等待 consumer 排空在途帧（channel 积压）。引擎线程在 case 结束、detach **之前**调用；
    /// 500ms 上限或 ct 取消时直接返回（放弃排空，残余帧丢弃但文件仍合法）。Rev 2 (P3)。
    /// </summary>
    Task WaitForFrameDrainAsync(CancellationToken ct = default);
}
```

### 3.3 `TestSuiteEngine` 变更

- `ExecuteAsync` 追加可选参数 `IFrameSinkFactory? sinkFactory = null`（**向后兼容，CLI 不传→零影响**）。
- `ExecuteCaseAsync` 在 case fixture **setup 成功之后**、steps 之前挂载 sink（Rev 2 P6：setup 失败则 steps 不执行，不创建 sink）；`finally` 按 **drain → detach → Dispose** 顺序收尾（Rev 2 P3，顺序不可颠倒——drain 必须在 detach 之前，否则积压帧因 sink 已摘除而丢失）：

```csharp
// ExecuteCaseAsync 内，case fixture setup 之后、steps 之前（failureReason 为 null 才挂载，P6）
IFrameSink? sink = null;
if (failureReason is null && ctx is IHasFrameSink hasSink && sinkFactory is not null)
{
    sink = sinkFactory.Create(testCase.Name, caseIndex);   // 可能返回 null（降级，见 A8/P4）
    hasSink.SetFrameSink(sink);
}
try
{
    // ... 现有 steps 执行逻辑（StopCaseOnFailure / 负测试 / 取消 均在此块内） ...
}
finally
{
    if (ctx is IHasFrameSink hasSink2 && sink is not null)
    {
        await hasSink2.WaitForFrameDrainAsync(ct);   // 1. 排空在途帧（sink 仍挂载）
        hasSink2.SetFrameSink(null);                 // 2. detach（此后新帧不再写入）
    }
    sink?.Dispose();                                 // 3. flush + close（幂等）
}
```

> 注意：现有 `ExecuteCaseAsync` 无 caseIndex 参数，需从 `ExecuteAsync` 的 `caseIndex` 传入（`ExecuteAsync` 循环里 `caseIndex` 已存在，`:65`）。

### 3.4 `IHasFrameSink` 实现（Infrastructure 层）

`HILAssertionContext` / `PeakCanAssertionContext` 各加：

```csharp
private IFrameSink? _frameSink;   // 跨线程：引擎线程写，consumer 线程读

public void SetFrameSink(IFrameSink? sink)
    => Volatile.Write(ref _frameSink, sink);

// Rev 2 (P3): 引擎线程在 case 结束、detach 之前调用；有界排空 channel 积压。
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

// ConsumerLoop 内，_recentFrames.Add(frame) 之后：
Volatile.Read(ref _frameSink)?.Write(frame);
```

> **Rev 2 (P7) 附带修复 — `PeakCanAssertionContext` 解码保护**：硬件模式的 ConsumerLoop 中 `SignalDecoder.Decode`（`PeakCanAssertionContext.cs:153`）**没有** try/catch 包裹（HILAssertionContext 有 FIND-004 修复，`:216-229`）。一旦解码抛异常（如 signal.Length > 64），consumer loop 死亡 → sink 后续帧全丢，G1"全量"承诺连带失效。随本任务一并修复：与 FIND-004 同款，逐 signal try/catch + 记日志跳过。此改动已有同类测试先例（FIND-004 对应用例），新增 PeakCanAssertionContext 用例见 §6。
>
> **Rev 2 (P10) — P7 前置依赖**：`PeakCanAssertionContext` 当前 ctor 为 `(ICanChannel, IDbcLookup)`，**无 logger 参数**（`HeadlessHostBuilder.cs:107` 直接 `new PeakCanAssertionContext(channel, dbc)`）。P7"记日志"需要先给 ctor 追加 `ILogger? logger = null`（照 `HILAssertionContext.cs:34` 模式），并更新 `HeadlessHostBuilder` 调用点传入容器 logger。File Inventory 相应补充。

### 3.5 `AscFrameSink`（Infrastructure 层，新建）

```csharp
/// <summary>
/// 流式 CAN 帧 → PEAK ASCII (.asc) 文件。BufferedStream 缓冲，Dispose 时 flush+close。
/// 首帧时间戳作为 offset 基准（与 FrameCaptureExporter 语义一致）。
/// 线程安全：Write 由 consumer 单线程调用；Dispose 与 Write 竞态由软关闭标志保护。
/// </summary>
internal sealed class AscFrameSink : IFrameSink
{
    private readonly FileStream _fs;
    private readonly BufferedStream _buffered;
    private readonly StreamWriter _writer;
    private int _disposed;                       // Interlocked 标志
    private double? _timestampOffsetUs;          // 首帧时间戳基准
    private int _frameCount;

    public AscFrameSink(string path) : this(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) { }

    // Rev 2 (P12): 测试注入 ctor —— A7 用例需模拟 IO 失败，string 路径无法注入故障流。
    internal AscFrameSink(Stream stream)
    {
        // 编码必须带 BOM（Rev 2 P9）：与 FrameCaptureExporter 的 File.WriteAllTextAsync(Encoding.UTF8) 输出一致，
        // 保证 T5"逐字节一致"成立、PEAK 工具行为一致。
        _buffered = new BufferedStream(stream);
        _writer = new StreamWriter(_buffered, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        AscFileFormat.WriteHeader(sb);   // 格式走 AscFileFormat（§3.10）
        _writer.Write(sb.ToString());
    }

    public void Write(CanFrame frame)
    {
        if (Volatile.Read(ref _disposed) != 0) return;   // 软关闭：Dispose 后丢弃
        _timestampOffsetUs ??= frame.Timestamp.TotalMicroseconds;
        // 复用 AscFileFormat.WriteFrameLine（与 FrameCaptureExporter 同源，T5 防漂移）
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;   // 幂等
        _buffered.Flush();
        _writer.Flush();
        _writer.Dispose();
        _buffered.Dispose();
        _fs.Dispose();
    }
}
```

> **P0 决议（并发竞态）**：`Write` 与 `Dispose` 竞态由 `_disposed` Interlocked 标志 + `Write` 前置检查解决。即使 consumer 线程已 `Volatile.Read` 拿到 sink 引用、引擎线程此刻 `Dispose`，`Write` 内部二次检查标志位后丢弃帧，不抛 `ObjectDisposedException`。与 `IAssertionContext` IDisposable 契约（volatile flag，`IAssertionContext.cs:8-11`）一致。
>
> **P1 决议（首帧基准）**：`_timestampOffsetUs ??= frame.Timestamp` —— 第一帧到达时记录基准，后续帧 `elapsedUs = frame.Timestamp - _timestampOffsetUs`，与 `FrameCaptureExporter.cs:63-65` 语义一致。
>
> **P2 决议（空文件）**：无帧 case 生成仅含 header 的空 `.asc`（约 200 字节）。MVP 接受，文档说明。

### 3.6 `AscFrameSinkFactory`（Infrastructure 层，新建）

```csharp
/// <summary>
/// 按 case 名 + run 时间戳 + case index 命名。目录默认 <reportDir>/case-logs/。
/// Rev 2 (P5): SanitizeFileName 复用 AscFileFormat（internal helper），含 100 字符截断（A9）。
/// </summary>
internal sealed class AscFrameSinkFactory : IFrameSinkFactory
{
    private readonly string _directory;
    private readonly string _runTimestamp;   // yyyyMMddHHmmssfff，与报告时间戳同源（见 3.7）

    public IFrameSink? Create(string caseName, int caseIndex)
    {
        var safeName = AscFileFormat.SanitizeFileName(caseName, maxLength: 100);
        var fileName = $"{safeName}_{caseIndex}_{_runTimestamp}.asc";
        return new AscFrameSink(Path.Combine(_directory, fileName));
    }
}
```

> **P1 决议（同名 case）**：文件名含 `caseIndex`，suite 内重复 case 名不互相覆盖。
> 命名：`{caseName}_{caseIndex}_{runTimestamp}.asc`。
> **Rev 2 (P5)**：`SanitizeFileName` 不再"复用 FrameCaptureExporter 的 private 方法"（不可编译），统一走 `AscFileFormat`（§3.10）；CLI 侧不截断（`maxLength: int.MaxValue`），行为不变。

### 3.7 `HilRunnerService` 变更

```csharp
public async Task<TestSuiteResult> RunAsync(HilRunRequest request, ...)
{
    // 每 run 生成一次时间戳（与报告共用语义，见 P2 待优化）
    var runTimestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
    IFrameSinkFactory? sinkFactory = null;
    if (request.CaptureCaseLogs)
    {
        var dir = request.CaseLogDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "PeakCanHost", "hil-reports", "case-logs");
        try
        {
            Directory.CreateDirectory(dir);   // Rev 2 (P4): 首次运行目录不存在；先例 FrameCaptureExporter.cs:22
            sinkFactory = new AscFrameSinkFactory(dir, runTimestamp);
        }
        catch (Exception ex)
        {
            // P4: 建目录/不可写 → 记日志降级（不阻断测试），功能整体关闭而非逐 case 静默失败
            _logger.LogWarning(ex, "Case log directory unavailable, capture disabled: {Dir}", dir);
            sinkFactory = null;
        }
    }
    // ... channel connect, background frames ...
    return await engine.ExecuteAsync(suite, ctx, new TestSuiteConfig(), progress, ct, sinkFactory);
}
```

> **Rev 2 (P4)**：`HilRunnerService` 构造函数注入 `ILogger<HilRunnerService>`（当前无参 ctor，DI 注册不变）。若目录不可用，功能整体降级关闭 + 日志，避免 A8 的"逐 case 静默返回 null = 功能无声死亡"。

### 3.8 `HilRunRequest` 变更

```csharp
public sealed record HilRunRequest(
    ...
    IReadOnlyList<string>? SelectedCaseNames = null,
    // 2026-08-11: WPF 每 case 全量报文 log
    bool CaptureCaseLogs = false,          // 默认关（CLI 默认不记录；WPF 侧由 VM 显式传 true）
    string? CaseLogDirectory = null);      // null → %LocalAppData%\PeakCanHost\hil-reports\case-logs\
```

> **产品决议**：`HilRunRequest` 默认 `false`（保持 CLI 语义不变），WPF `HilViewModel` 显式传 `CaptureCaseLogs`（默认 true，CheckBox 绑定）。

### 3.9 `HilViewModel` + `HilView.xaml` 变更

```csharp
// HilViewModel
[ObservableProperty] private bool _captureCaseLogs = true;   // 默认勾选

// RunAsync 内 request 构造追加：
CaptureCaseLogs: CaptureCaseLogs,

// RunAsync 内 StatusMessage 之后追加（Rev 2 P11，功能完整性）：
if (CaptureCaseLogs)
{
    StatusMessage += $" — case logs: {CaseLogDirectory ?? 默认目录}";
    // 注：VM 需在 request 构造处保留实际目录值；或由 runner 回传（P1 待优化）。
}
```

```xml
<!-- HilView.xaml：现有控件区（Mode selector 附近）加 -->
<CheckBox Content="记录每 case 报文 (.asc)" IsChecked="{Binding CaptureCaseLogs}" />
```

> **Rev 2 (P11) 决策**：
> - **UI 反馈**：跑完后 StatusMessage 追加 case-logs 目录提示（用户能找得到产物）。MVP 取 `CaseLogDirectory ?? %LocalAppData%\PeakCanHost\hil-reports\case-logs`，与 runner 默认保持一致。
> - **TraceReplay 语义**：回放模式帧源就是 trace 文件本身，录 .asc 属冗余。**MVP 决策：照录**（链路一致、无需分支逻辑，回放帧经同一 ConsumerLoop 自然落盘）；`CaseLogDirectory` 仅 API 级字段，UI 不暴露目录选择。

### 3.10 `AscFileFormat`（Infrastructure 层，新建，Rev 2 P5）

> **动机**：`FrameCaptureExporter` 的 `WriteAscFileAsync` 与 `SanitizeFileName` 都是 **private**（`FrameCaptureExporter.cs:52`、`:92`），原计划"复用"不可编译。抽共享 helper 同时解决：格式防漂移（T5 断言两处输出一致）+ `SanitizeFileName` 复用 + A9 截断统一。

```csharp
/// <summary>
/// PEAK ASCII (.asc) 文件格式共享 helper。FrameCaptureExporter（CLI）与 AscFrameSink（WPF 流式）同源，
/// 逐字节一致。internal，同程序集可见；命名空间归属实现时定（建议 Infrastructure/HIL/ 或 Cli/Reporting/）。
/// </summary>
internal static class AscFileFormat
{
    public static void WriteHeader(StringBuilder sb) { /* date/base hex/…，与 FrameCaptureExporter.cs:56-60 一致 */ }
    public static void WriteFrameLine(StringBuilder sb, CanFrame frame, double elapsedUs) { /* 与 :74-83 一致 */ }
    public static string SanitizeFileName(string name, int maxLength = int.MaxValue)
    { /* 非法字符替换 _ + 超长截断（A9）；FrameCaptureExporter 调用不截断，行为不变 */ }
}
```

> **Rev 2 (P5) 决议**：`FrameCaptureExporter` EDIT 改用 helper，输出逐字节不变（现有 CLI 导出测试兜底）；CLI 行为零变化（G5 保持）。

---

## 4. 多视角评审记录（2026-08-11）

### 架构工程师视角

| # | 发现 | 严重度 | 决议 |
|---|------|--------|------|
| A1 | `_frameSink` 跨线程（引擎写 / consumer 读）+ Dispose 与 Write 竞态 → use-after-dispose | **P0** | `Volatile` 读写 + sink 内部 Interlocked 软关闭标志（§3.5） |
| A2 | 流式写入需首帧时间基准，否则 `timestamps absolute` 时间戳错乱 | **P1** | `_timestampOffsetUs` 首帧惰性记录（§3.5） |
| A3 | `ExecuteAsync` 参数膨胀（5→6 参） | P2 | 可选参数，向后兼容；peakcan 风格即参数列表，不引入 options 对象 |
| A4 | `AscFrameSinkFactory` 依赖 request 级目录，不能 DI 单例 | — | `RunAsync` 内局部构造，正确 |
| A5 | `IHasFrameSink` 命名 | P2 | 接受；接口语义清晰（挂载帧 sink） |
| A6 | **run timestamp 与 HTML 报告各自生成，难以关联**（`HilReportService.cs:29` 独立 `DateTime.UtcNow`） | P2 待优化 | 后续 `HilRunnerService` 暴露 run timestamp 共享；MVP 接受轻微不一致 |
| **A7** | **`AscFrameSink.Write` 内部不 try/catch → consumer loop 崩溃**（consumer loop 只在 `:260` 捕获 `OperationCanceledException`，IO 异常会传播出去导致后续帧丢失） | **P0** | `Write` 内部 try/catch 所有异常，记日志降级；`IFrameSink` XML doc 注明此契约 |
| **A8** | **`AscFrameSinkFactory.Create` 目录不可写/权限不足时抛异常 → case 被标记为 Failed**（异常会跑到 `TestSuiteEngine.cs:161-164` 被 `catch (Exception ex)` 捕获变成步骤失败） | **P1** | `Create` 内部 try/catch，失败返回 null 降级；`HilRunnerService` 构造 factory 时可先验证目录可写性 |
| **A9** | **Windows 路径超限**：超长 case 名（如 200 字符中文）→ `PathTooLongException` | **P1** | `SanitizeFileName` 截断到 100 字符（`AscFrameSinkFactory` 内实现） |

### 测试工程师视角

| # | 发现 | 严重度 | 决议 |
|---|------|--------|------|
| T1 | 同名 case 覆盖（`SelectedCaseNames` 按 name 匹配，`HilRunnerService.cs:42`） | **P1** | 文件名加 `caseIndex`（§3.6） |
| T2 | 负测试 case（`WasNegatedTest` 提升 Status）——全量 log 与步骤状态无关，负测试 case 也必须记录 | — | 引擎在 case 级挂 sink，与步骤状态解耦，天然满足；加测试覆盖 |
| T3 | 取消 / StopCaseOnFailure / 步骤抛异常 → sink 必须关闭 | **P0** | `finally` 保证（§3.3）；测试覆盖三种路径 |
| T4 | 空 case / 无帧 case → 空 .asc | P2 | 接受（§3.5）；测试断言 header 存在 |
| T5 | `.asc` 格式可被 PEAK 工具解析 | — | 测试断言 header + 帧行格式（复用 `FrameCaptureExporter` 格式） |

### 产品经理视角

| # | 发现 | 严重度 | 决议 |
|---|------|--------|------|
| P1 | 开关默认值 | — | WPF CheckBox 默认勾选（用户主动要此功能）；`HilRunRequest` 默认 false 保 CLI 语义 |
| P2 | 磁盘增长无清理机制 | P2 | MVP 接受；doc 说明 `.asc` 文件累积，后续加保留策略 |
| P3 | 全量 log 与 HTML 报告帧转储（≤50 帧）不冲突——报告保持现状，log 是补充 | — | 不做"报告链接到 log"（P1 优化，MVP 外） |
| P4 | `.asc` 是 PEAK 标准格式，用户可用 CANalyzer/PCAN-View 打开，无需新工具 | — | 产品价值点，写进 doc |
| P5 | 误用风险：用户可能误以为含 DBC 解码 | — | doc 说明：`.asc` 是原始报文，解码需 DBC + 工具 |

### 待优化（不阻塞 MVP）

1. run timestamp 共享（HTML 报告 ↔ case log 关联）
2. HTML 报告帧转储区加"打开完整 `.asc`"链接
3. 磁盘清理策略（保留 N 天 / 手动清理）
4. case 级跳过（`IFrameSinkFactory.Create` 返回 null 已预留）
5. sink 写失败（磁盘满）降级：记日志 + 不阻断测试

---

### 4.2 复审评审记录（2026-08-12，Rev 2）

> 复审方式：对照实际代码（CodeGraph 索引逐条核对行号与结构）。前 3 项（P3~P5）为复审新发现；P6~P8 为边界补强。

| # | 发现 | 严重度 | 决议 |
|---|------|--------|------|
| **P3** | **尾部帧窗口**：帧经 bounded channel（`Channel<CanFrame>` capacity 10000, DropOldest）异步到达 consumer。case 结束引擎线程 detach+Dispose 时，channel 内最多 ~10000 帧未消费 → 直接丢失，G1"全量"不成立 | **P1** | `IHasFrameSink` 加 `WaitForFrameDrainAsync`：**detach 之前**有界排空（500ms，`Reader.Count` 轮询，ct 取消即放弃）；finally 顺序固定 **drain → detach → Dispose**（§3.3/§3.4）。残余窗口：排空期间到达的帧（≤500ms + 未排干残量）可能入账或丢失，文档化接受 |
| **P4** | **目录创建缺失**：`case-logs\` 首次不存在 → `FileStream` 抛 `DirectoryNotFoundException` → `Create` 返回 null → 功能无声死亡（无任何提示） | **P1** | `HilRunnerService` 显式 `Directory.CreateDirectory(dir)` + try/catch → 失败记日志、整体降级关闭（§3.7）；先例 `FrameCaptureExporter.cs:22`；`HilRunnerService` 注入 `ILogger` |
| **P5** | **"复用"不可编译**：`SanitizeFileName`/`WriteAscFileAsync` 均为 **private**（`FrameCaptureExporter.cs:92`、`:52`），File Inventory 未列其改动 | **P1** | 抽 `AscFileFormat` internal helper（§3.10）：格式行 + SanitizeFileName（含 A9 截断）同源；`FrameCaptureExporter` EDIT 改用，输出逐字节不变（CLI 测试兜底，G5 保持） |
| P6 | fixture setup 失败 → steps 不执行，原 §3.3 伪代码"case 开始即 Create"语义歧义（可能把 setup 帧录进去） | P2 | 挂载条件 `failureReason is null`（§3.3）；测试补"setup 失败 → `factory.Create` 不调用" |
| P7 | `PeakCanAssertionContext` ConsumerLoop 的 `SignalDecoder.Decode`（`:153`）无 try/catch（HILAssertionContext 有 FIND-004 修复）→ 解码异常杀 loop → sink 后续帧全丢，"全量"连带失效 | P2 | 附带修复：FIND-004 同款逐 signal try/catch + 记日志（§3.4）；测试补"解码异常不杀 loop，sink 仍收后续帧" |
| P8 | `ConsumerLoop` 无既有覆盖测试（CodeGraph：no covering tests），§6 "MODIFY HILAssertionContextTests" 实为**新增**首个 consumer 帧流测试 | 工作量 | 测试标注改 NEW；灌帧走 fake channel（`OnFrame_WritesToFrameChannel` 先例存在）；新增 drain 超时/取消路径用例 |
| **P9** | **BOM 编码不一致**：CLI 侧 `File.WriteAllTextAsync(..., Encoding.UTF8)` 带 BOM（`FrameCaptureExporter.cs:86`）；`new StreamWriter(path)` 默认无 BOM → T5"逐字节一致"必挂，PEAK 工具行为不一致 | **P1** | `AscFrameSink` 显式 `new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)`（§3.5），与 CLI 输出同源 |
| **P10** | **P7 前置缺失**：`PeakCanAssertionContext` ctor 无 logger（`HeadlessHostBuilder.cs:107`）→ P7"记日志"无处可记 | P2 | ctor 追加 `ILogger? logger = null`（照 `HILAssertionContext.cs:34`），更新 `HeadlessHostBuilder` 调用点（§3.4） |
| P11 | 功能完整性：跑完无任何 UI 反馈（log 生成与否、目录在哪）；TraceReplay 模式记录冗余语义未决 | P2 | StatusMessage 追加目录提示；TraceReplay 照录（链路一致）；`CaseLogDirectory` 仅 API 级（§3.9） |
| P12 | A7 测试需模拟 IO 失败，`AscFrameSink(string path)` 无法注入故障流 | P2 | internal ctor `AscFrameSink(Stream)`，string ctor 委托（§3.5） |

---

## 5. File Inventory

| 文件 | 动作 | 内容 |
|------|------|------|
| `Core/HIL/Contracts/IFrameSink.cs` | NEW | `IFrameSink` + `IFrameSinkFactory` + `IHasFrameSink`（§3.2，可拆 3 文件或 1 文件，按项目惯例） |
| `Core/HIL/TestSuiteEngine.cs` | EDIT | `ExecuteAsync` 加 `IFrameSinkFactory?` 参数；`ExecuteCaseAsync` 挂/摘 sink + finally Dispose（§3.3） |
| `Infrastructure/HIL/AscFrameSink.cs` | NEW | 流式 `.asc` 写入（§3.5） |
| `Infrastructure/HIL/AscFrameSinkFactory.cs` | NEW | 命名 + 目录（§3.6） |
| `Infrastructure/HIL/AscFileFormat.cs` | NEW（Rev 2 P5） | 共享格式 helper：WriteHeader / WriteFrameLine / SanitizeFileName（§3.10） |
| `Infrastructure/Cli/Reporting/FrameCaptureExporter.cs` | EDIT（Rev 2 P5） | 改用 `AscFileFormat`，输出逐字节不变（G5 保持） |
| `Infrastructure/HIL/HILAssertionContext.cs` | EDIT | 实现 `IHasFrameSink`（含 `WaitForFrameDrainAsync`），ConsumerLoop 写帧（§3.4） |
| `Infrastructure/HIL/PeakCanAssertionContext.cs` | EDIT | 同上 + FIND-004 同款解码 try/catch（Rev 2 P7）+ ctor 追加 `ILogger?`（Rev 2 P10） |
| `Infrastructure/HIL/HeadlessHostBuilder.cs` | EDIT（Rev 2 P10） | `PeakCanAssertionContext` 注册处传容器 logger |
| `Infrastructure/HIL/HilRunnerService.cs` | EDIT | 构造 factory + `Directory.CreateDirectory` + 注入 `ILogger` + 传 engine + run timestamp（§3.7） |
| `Core/HIL/HilRunRequest.cs` | EDIT | 加 `CaptureCaseLogs` + `CaseLogDirectory`（§3.8） |
| `App/ViewModels/HilViewModel.cs` | EDIT | `CaptureCaseLogs` 属性 + request 传递（§3.9） |
| `App/Views/HilView.xaml` | EDIT | CheckBox（§3.9） |
| 测试 | NEW/MODIFY | 见 §6 |

---

## 6. Testing (TDD)

### `AscFrameSinkTests`（NEW）

| 用例 | 断言 |
|------|------|
| 多帧写入 | 写入 N 帧 → 文件含 N 帧行，格式 `{seconds:F6} 1 {id}x Rx d {dlc} {datahex}` |
| 首帧时间基准 | 首帧 timestamp=T → 首帧 `seconds=0`，第二帧 `seconds=(T2-T)/1e6` |
| Dispose flush | Dispose 后文件已 flush（File.ReadAllText 可见全部帧） |
| Dispose 幂等 | 两次 Dispose 不抛 |
| **Dispose 后 Write 静默丢弃**（P0） | Dispose 后调用 Write → 不抛、文件不再增长 |
| 空帧 | 无帧 Dispose → 文件只有 header（~200 字节） |
| **BOM 一致**（P9） | 文件头 3 字节为 EF BB BF，与 `FrameCaptureExporter` 输出一致 |
| **Write 抛异常不传播**（A7/P12） | 注入故障 Stream（internal ctor）→ `Write` 内部 catch，不抛到外部 |
| **文件名超长截断**（A9） | 传入 200 字符 case 名 → 文件名 ≤ 100 字符 + 后缀 + 时间戳，不抛 `PathTooLongException` |

### `AscFrameSinkFactoryTests`（NEW）

| 用例 | 断言 |
|------|------|
| `Create` 目录不可写 → 返回 null（A8） | 模拟目录权限拒绝 → `Create` 不抛，返回 null |
| `Create` 正常路径 | 返回 `AscFrameSink` 实例，文件路径正确 |
| 超长 case 名截断（A9） | 200 字符 case 名 → 文件名 `≤100_{index}_{timestamp}.asc` |

### `AscFileFormatTests`（NEW，Rev 2 P5）

| 用例 | 断言 |
|------|------|
| `SanitizeFileName` 截断 | 200 字符名 → 返回 ≤100 字符；非法字符替换 `_` |
| `WriteFrameLine` 格式 | 与 `FrameCaptureExporter` 历史输出逐字节一致（防漂移，T5 兜底） |

### `TestSuiteEngine` sink 生命周期测试（MODIFY `TestSuiteEngineTests.cs`）

| 用例 | 断言 |
|------|------|
| 每 case 创建 + Dispose | 2 个 case → factory.Create 调 2 次、每个 sink Dispose 1 次 |
| 步骤抛异常 → finally Dispose | executor 抛异常 → sink 仍 Dispose |
| StopCaseOnFailure 提前 break | 失败后跳过剩余步骤 → sink 仍 Dispose |
| factory 返回 null | `Create` 返回 null → 无文件写入、不抛 |
| **ctx 不实现 IHasFrameSink** | 传 factory 但 ctx 不支持 → 静默跳过，不抛 |
| 不传 factory | `sinkFactory=null` → 行为与现状完全一致（回归） |
| **fixture setup 失败 → 不建 sink**（P6） | case fixture `SetupAsync` 抛异常 → `factory.Create` 不被调用，无 sink |
| **生命周期顺序**（P3） | 正常完成 → `WaitForFrameDrainAsync` 先于 detach、Dispose 最后 |

### `HILAssertionContext` sink 测试（MODIFY `HILAssertionContextTests.cs`，ConsumerLoop 覆盖为 NEW，P8）

| 用例 | 断言 |
|------|------|
| 挂载后帧写入 | `SetFrameSink(sink)` → 收到帧 → sink 收到 N 帧 |
| 摘除后不再写 | `SetFrameSink(null)` → 后续帧 sink 不收到 |
| **并发竞态**（P0） | consumer 线程持续 Write + 引擎线程 Dispose 交替 → 不抛 `ObjectDisposedException` |
| **积压排空**（P3） | fake channel 灌入 N 帧后调 `WaitForFrameDrainAsync` → 返回时 sink 已收到全部 N 帧 |
| **排空超时**（P3） | consumer 被阻塞（subscriber 慢）→ 500ms 返回不抛，文件仍合法 |
| **排空取消**（P3） | ct 已取消 → 立即返回不抛 |

### `PeakCanAssertionContext` 解码保护测试（MODIFY `PeakCanAssertionContextTests.cs`，Rev 2 P7）

| 用例 | 断言 |
|------|------|
| 解码异常不杀 loop | 帧含超长 signal（>64 位）→ 解码抛异常被 catch → **后续帧仍被 sink 收到**，loop 存活 |

### `HilViewModel` 开关测试（MODIFY `HilViewModelTests.cs`）

| 用例 | 断言 |
|------|------|
| 默认勾选 | `CaptureCaseLogs == true` |
| request 传递 | `RunAsync` mock 收到 `CaptureCaseLogs=true`（CheckBox 勾选时） |
| 去勾不传 | `CaptureCaseLogs=false` → mock 收到 `CaptureCaseLogs=false` |

### 集成测试（MODIFY `HilRunnerService` / `HILIntegrationTests`）

| 用例 | 断言 |
|------|------|
| `CaptureCaseLogs=true` 全链路 | 跑一个 suite → `case-logs\` 目录存在且含 `.asc` 文件，文件含 case 期间帧 |
| `CaptureCaseLogs=false` | 跑 suite → 无 `.asc` 生成 |
| 负测试 case 也记录 | suite 含负测试 case（`WasNegatedTest`）→ 该 case 也有 `.asc` |
| **目录首次不存在 → 自动创建**（P4） | 删除 `case-logs\` 后跑 → 目录被重建，测试不失败 |
| **目录不可写 → 降级不阻断**（P4） | 目录指向只读路径 → run 正常完成、无 `.asc`、无异常 |

---

## 7. Out of Scope

- **CLI 侧**：本次不改 CLI 入口。CLI 不传 sink factory → 行为不变。`--export-frames` 保持现状（失败周边帧）。
- **报告链接到 log**：HTML 报告不加"打开 `.asc`"链接（P1 优化）。
- **run timestamp 共享**：报告与 case log 时间戳各自生成（P2 待优化）。
- **磁盘清理**：无保留策略（MVP 接受累积）。
- **case 级跳过**：`IFrameSinkFactory.Create` 返回 null 已预留接口，本次不做 case 级 UI。
- **DBC 解码进 log**：`.asc` 只含原始报文，解码是消费端（CANalyzer/脚本）的事。

---

## 8. Acceptance

- [ ] WPF 跑 HIL，每个 case 生成 `{caseName}_{caseIndex}_{runTimestamp}.asc` 于 `hil-reports\case-logs\`
- [ ] 文件是合法 PEAK ASCII 格式（header + 帧行），含 case 期间全部帧（非 50 帧 cap）；BOM 与 CLI 导出一致（P9）
- [ ] 流式写入：case 跑 5 分钟内存不增长（无全量帧列表）
- [ ] CheckBox 默认勾选；去勾 → 不生成任何 `.asc`，行为 = 现状
- [ ] case 步骤异常 / 取消 / StopCaseOnFailure → sink 仍关闭，已写帧完整
- [ ] case 结束时尾部积压帧不丢（高帧率 run 后 .asc 帧数 ≈ 总线帧数，P3 排空生效）
- [ ] `case-logs\` 首次不存在 → 自动创建；目录不可写 → 记日志降级、测试正常跑完（P4）
- [ ] CLI 跑同一个 suite → 行为与改动前完全一致（无 `.asc` 生成、无异常）
- [ ] 同名 case 不互相覆盖（caseIndex 区分）
- [ ] 负测试 case 也生成 log