# Design: WPF HIL 每 case 全量报文流式 log（.asc）

> Spec date: 2026-08-11
> Depends: 现有 `HILAssertionContext` / `PeakCanAssertionContext` 帧环形缓冲 + `FrameCaptureExporter`（CLI `--export-frames`）
> Scope: **WPF HIL 运行，每个 test case 生成一个独立的全量 CAN 报文 `.asc` 文件，流式写入，零额外内存**。CLI 行为不变。
> Status: APPROVED（2026-08-11 用户确认 4 个决策点：范围=steps 阶段 / 开关默认勾选 / 空文件生成 / 3 个阻塞点防御方案）

---

## 1. Goals

当前 HIL 帧捕获只覆盖**失败步骤周边 ≤50 帧**（`CircularBuffer<CanFrame> capacity: 50`，`HILAssertionContext.cs:31`），且：
- CLI 侧 `--export-frames` 每失败 case 一个 `.asc`（`FrameCaptureExporter.cs:43`）
- **WPF 侧无独立导出**，只有 HTML 报告内嵌帧转储（`HtmlReportGenerator.cs:164-199`）

本设计目标：

**G1. 全量报文** — WPF 跑 HIL 时，每个 test case 的 **steps 执行期间** CAN 总线上**所有**帧都落盘（不含失败/通过过滤）。

> **范围界定**：帧记录范围是 case 的 steps 执行阶段（`SetupAsync` → `fixture.TeardownAsync` 之前），**不包括 Case Fixture 的 Setup 和 Teardown**。理由：fixture 的帧是基础设施行为（如重置 ECU 会话），不属于测试关注点。如果后续需要 fixture 帧，可提前挂载点。
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
  │  finally { ctx.SetFrameSink(null); sink?.Dispose() }   ← 异常/取消/StopCaseOnFailure 都保证关闭
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
}
```

### 3.3 `TestSuiteEngine` 变更

- `ExecuteAsync` 追加可选参数 `IFrameSinkFactory? sinkFactory = null`（**向后兼容，CLI 不传→零影响**）。
- `ExecuteCaseAsync` 在 case fixture 之后、steps 之前挂载 sink；`finally` 摘除 + Dispose：

```csharp
// ExecuteCaseAsync 内，steps 执行前后
IFrameSink? sink = null;
if (ctx is IHasFrameSink hasSink && sinkFactory is not null)
{
    sink = sinkFactory.Create(testCase.Name, caseIndex);
    hasSink.SetFrameSink(sink);
}
try
{
    // ... 现有 steps 执行逻辑 ...
}
finally
{
    if (ctx is IHasFrameSink hasSink2) hasSink2.SetFrameSink(null);
    sink?.Dispose();
}
```

> 注意：现有 `ExecuteCaseAsync` 无 caseIndex 参数，需从 `ExecuteAsync` 的 `caseIndex` 传入（`ExecuteAsync` 循环里 `caseIndex` 已存在，`:65`）。

### 3.4 `IHasFrameSink` 实现（Infrastructure 层）

`HILAssertionContext` / `PeakCanAssertionContext` 各加：

```csharp
private IFrameSink? _frameSink;   // 跨线程：引擎线程写，consumer 线程读

public void SetFrameSink(IFrameSink? sink)
    => Volatile.Write(ref _frameSink, sink);

// ConsumerLoop 内，_recentFrames.Add(frame) 之后：
Volatile.Read(ref _frameSink)?.Write(frame);
```

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

    public AscFrameSink(string path) { /* 写 header，打开 BufferedStream */ }

    public void Write(CanFrame frame)
    {
        if (Volatile.Read(ref _disposed) != 0) return;   // 软关闭：Dispose 后丢弃
        _timestampOffsetUs ??= frame.Timestamp.TotalMicroseconds;
        // 写一行: {seconds:F6} 1 {id}x Rx d {dlc} {datahex}（复用 FrameCaptureExporter 格式）
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
/// </summary>
internal sealed class AscFrameSinkFactory : IFrameSinkFactory
{
    private readonly string _directory;
    private readonly string _runTimestamp;   // yyyyMMddHHmmssfff，与报告时间戳同源（见 3.7）

    public IFrameSink? Create(string caseName, int caseIndex)
    {
        var safeName = SanitizeFileName(caseName);   // 复用 FrameCaptureExporter.SanitizeFileName
        var fileName = $"{safeName}_{caseIndex}_{_runTimestamp}.asc";
        return new AscFrameSink(Path.Combine(_directory, fileName));
    }
}
```

> **P1 决议（同名 case）**：文件名含 `caseIndex`，suite 内重复 case 名不互相覆盖。
> 命名：`{caseName}_{caseIndex}_{runTimestamp}.asc`。

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
        sinkFactory = new AscFrameSinkFactory(dir, runTimestamp);
    }
    // ... channel connect, background frames ...
    return await engine.ExecuteAsync(suite, ctx, new TestSuiteConfig(), progress, ct, sinkFactory);
}
```

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
```

```xml
<!-- HilView.xaml：现有控件区（Mode selector 附近）加 -->
<CheckBox Content="记录每 case 报文 (.asc)" IsChecked="{Binding CaptureCaseLogs}" />
```

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

## 5. File Inventory

| 文件 | 动作 | 内容 |
|------|------|------|
| `Core/HIL/Contracts/IFrameSink.cs` | NEW | `IFrameSink` + `IFrameSinkFactory` + `IHasFrameSink`（§3.2，可拆 3 文件或 1 文件，按项目惯例） |
| `Core/HIL/TestSuiteEngine.cs` | EDIT | `ExecuteAsync` 加 `IFrameSinkFactory?` 参数；`ExecuteCaseAsync` 挂/摘 sink + finally Dispose（§3.3） |
| `Infrastructure/HIL/AscFrameSink.cs` | NEW | 流式 `.asc` 写入（§3.5） |
| `Infrastructure/HIL/AscFrameSinkFactory.cs` | NEW | 命名 + 目录（§3.6） |
| `Infrastructure/HIL/HILAssertionContext.cs` | EDIT | 实现 `IHasFrameSink`，ConsumerLoop 写帧（§3.4） |
| `Infrastructure/HIL/PeakCanAssertionContext.cs` | EDIT | 同上 |
| `Infrastructure/HIL/HilRunnerService.cs` | EDIT | 构造 factory + 传 engine + run timestamp（§3.7） |
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
| **Write 抛异常不传播**（A7） | 模拟 `BufferedStream` 写失败 → `Write` 内部 catch，不抛到外部 |
| **文件名超长截断**（A9） | 传入 200 字符 case 名 → 文件名 ≤ 100 字符 + 后缀 + 时间戳，不抛 `PathTooLongException` |

### `AscFrameSinkFactoryTests`（NEW）

| 用例 | 断言 |
|------|------|
| `Create` 目录不可写 → 返回 null（A8） | 模拟目录权限拒绝 → `Create` 不抛，返回 null |
| `Create` 正常路径 | 返回 `AscFrameSink` 实例，文件路径正确 |
| 超长 case 名截断（A9） | 200 字符 case 名 → 文件名 `≤100_{index}_{timestamp}.asc` |

### `TestSuiteEngine` sink 生命周期测试（MODIFY `TestSuiteEngineTests.cs`）

| 用例 | 断言 |
|------|------|
| 每 case 创建 + Dispose | 2 个 case → factory.Create 调 2 次、每个 sink Dispose 1 次 |
| 步骤抛异常 → finally Dispose | executor 抛异常 → sink 仍 Dispose |
| StopCaseOnFailure 提前 break | 失败后跳过剩余步骤 → sink 仍 Dispose |
| factory 返回 null | `Create` 返回 null → 无文件写入、不抛 |
| **ctx 不实现 IHasFrameSink** | 传 factory 但 ctx 不支持 → 静默跳过，不抛 |
| 不传 factory | `sinkFactory=null` → 行为与现状完全一致（回归） |

### `HILAssertionContext` sink 测试（MODIFY `HILAssertionContextTests.cs`）

| 用例 | 断言 |
|------|------|
| 挂载后帧写入 | `SetFrameSink(sink)` → 收到帧 → sink 收到 N 帧 |
| 摘除后不再写 | `SetFrameSink(null)` → 后续帧 sink 不收到 |
| **并发竞态**（P0） | consumer 线程持续 Write + 引擎线程 Dispose 交替 → 不抛 `ObjectDisposedException` |

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
- [ ] 文件是合法 PEAK ASCII 格式（header + 帧行），含 case 期间全部帧（非 50 帧 cap）
- [ ] 流式写入：case 跑 5 分钟内存不增长（无全量帧列表）
- [ ] CheckBox 默认勾选；去勾 → 不生成任何 `.asc`，行为 = 现状
- [ ] case 步骤异常 / 取消 / StopCaseOnFailure → sink 仍关闭，已写帧完整
- [ ] CLI 跑同一个 suite → 行为与改动前完全一致（无 `.asc` 生成、无异常）
- [ ] 同名 case 不互相覆盖（caseIndex 区分）
- [ ] 负测试 case 也生成 log