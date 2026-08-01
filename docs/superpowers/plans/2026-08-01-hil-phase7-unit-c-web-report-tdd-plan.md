# HIL Phase 7 Unit C TDD Plan: Web 报告 UI

> Spec: `docs/superpowers/specs/2026-08-01-hil-phase7-unit-c-web-report-spec.md` (Rev 3, 0 CRITICAL)
> Created: 2026-08-01
> Sprints: 2 | Increments: 6 | Tests: 10

---

## Pre-checks (verify before coding)

| # | Check | Command | Expected |
|---|-------|---------|----------|
| 0 | Build passes | `dotnet build` | 0 errors |
| 1 | HIL tests green | `dotnet test --filter "FullyQualifiedName~HIL"` | 0 new failures |
| 2 | `HilViewModel` ctor has 4 params | grep `public HilViewModel` in `HilViewModel.cs` | runner, logger, fileDialog, analysisService |
| 3 | 10 `new HilViewModel(` calls in tests | grep -rn `new HilViewModel(` tests/ | 10 matches |
| 4 | `HilView.xaml` outer is DockPanel | grep `<DockPanel` in `HilView.xaml` | line 13 |
| 5 | `HilView.xaml.cs` only has InitializeComponent | grep `InitializeComponent` in `HilView.xaml.cs` | 1 match, no other logic |
| 6 | `IHilReportService` does not exist | grep `IHilReportService` in src/ | 0 matches |
| 7 | `HilReportService` does not exist | grep `HilReportService` in src/ | 0 matches |
| 8 | `HtmlReportGenerator.GenerateHtml` returns string | grep `public static string GenerateHtml` in `HtmlReportGenerator.cs` | line 22 |
| 9 | `TrendTracker.Load` accepts path param | grep `public static.*Load` in `TrendTracker.cs` | line 61, `string? path = null` |
| 10 | WebView2 xmlns in ScriptView | grep `xmlns:wv2` in `ScriptView.xaml` | line 6 |

---

## Sprint 1: 服务层 + VM 逻辑 (7 tests)

### Inc 1: `IHilReportService` + `HilReportService` + 测试

**Files**: `Infrastructure/HIL/Reporting/IHilReportService.cs` (NEW), `Infrastructure/HIL/Reporting/HilReportService.cs` (NEW), `Infrastructure.Tests/HIL/Reporting/HilReportServiceTests.cs` (NEW)

| Test | Description |
|------|-------------|
| `Generate_ReturnsHtmlAndFilePath` | `Generate(固定 TestSuiteResult)` -> `Html` 非空且含 `<div class="summary`、`FilePath` 指向报告目录 |
| `Generate_WritesFileToDisk` | `Generate` 后 `File.Exists(FilePath)`；文件含 `<!DOCTYPE html>` |
| `Generate_CreatesDirectoryIfMissing` | 报告目录不存在时 `Generate` 自动 `Directory.CreateDirectory` |
| `Generate_ConsecutiveCalls_ProduceUniqueFilePaths` | 连续两次 `Generate` -> 两个 `FilePath` 不同（毫秒精度，B4） |
| `Generate_RecordsTrendEntry` | `Generate` 后 `TrendTracker.Load(trendsPath)` 比之前多一条、字段匹配 result |
| `Generate_CustomDirectory_WritesToTempDir` | 构造传临时目录 -> 文件写到该目录（不污染 `%LocalAppData%`） |

**Implementation**:
- `IHilReportService.cs`：接口 `Generate(TestSuiteResult) -> HilReportResult` + `HilReportResult(string Html, string FilePath)` record
- `HilReportService.cs`：
  - `using PeakCan.Host.Infrastructure.Cli.Reporting;`（R3）
  - `ReportDirectory` 属性，默认 `%LocalAppData%\PeakCanHost\hil-reports\`
  - `Generate`：`CreateDirectory` -> `TrendTracker.Load(trendsPath)` -> `GenerateHtml` -> `var now = DateTime.UtcNow` -> 文件名 `yyyyMMddHHmmssfff` -> `File.WriteAllText` -> `TrendTracker.Record(now, ...)` -> 返回 `(html, filePath)`

### Inc 2: `HilViewModel` ctor 扩展 + 报告属性 + RunAsync 插入 + OpenReport

**Files**: `App/ViewModels/HilViewModel.cs` (MODIFY), `App.Tests/ViewModels/HilViewModelReportTests.cs` (NEW)

| Test | Description |
|------|-------------|
| `RunAsync_Success_FillsReportPath` | mock `IHilReportService` 返回固定 `HilReportResult` -> RunAsync 后 `LatestReportPath` 非空、`ShowReportError=false`、`ReportError=""` |
| `RunAsync_ReportServiceThrows_DegradesGracefully` | mock service `Generate` 抛异常 -> `ShowReportError=true`、`ReportError` 非空、`Results` 仍填充、不抛 |
| `OpenReport_NoPath_NoOp` | `LatestReportPath=""` -> `OpenReportCommand` 可执行但不抛 |
| `ctor_AcceptsFiveParams` | `new HilViewModel(r, log, fd, a, mockReportService)` 构造成功 |

**Implementation**:
- 新增 `using System.Diagnostics;`（R2）
- ctor 追加第 5 参 `IHilReportService reportService`
- 新增 `[ObservableProperty]`：`LatestReportPath` (string, 初始 `""`)、`ShowReportError` (bool, 初始 `false`)、`ReportError` (string, 初始 `""`)
- `RunAsync` 插入点：`StatusMessage`（`:200`）之后、`AnalyzeAsync`（`:205`）之前
  - try: `var report = _reportService.Generate(result); LatestReportPath = report.FilePath; ShowReportError = false; ReportError = "";`
  - catch: `_logger.LogError; ReportError = ex.Message; ShowReportError = true;`
- `[RelayCommand] OpenReport`：检查 `LatestReportPath` 非空且 `File.Exists` -> `Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })`

### Inc 3: 现有测试 ctor 调用点修复（10 处）

**Files**: 5 个测试文件 MODIFY

| Test | Description |
|------|-------------|
| (编译期验证) | `dotnet build` 测试项目 0 errors -- 10 处 `new HilViewModel(...)` 全部加 `Substitute.For<IHilReportService>()` |

**10 处调用点**（spec §4 L1 关键约束）:
1. `AppShellViewModelTests.cs:147`
2. `AppShellViewModelTests.cs:476`
3. `AppShellViewModelTests.cs:573`
4. `AppShellViewModelTests.cs:711`
5. `AppShellViewModelTests.cs:1012`
6. `AppShellViewModelTests.cs:1112`
7. `AppShellViewModelMessageBoxPromptTests.cs:176`
8. `UdsWindowTests.cs:105`
9. `HilViewModelTests.cs:30` (helper `CreateViewModel`)
10. `HilViewModelAnalysisTests.cs:27` (helper `CreateViewModel`)

**Key constraint**: 每处加 `Substitute.For<IHilReportService>()` 作为第 5 参数。helper 方法（#9, #10）加可选参数 `IHilReportService? reportService = null` + 默认 mock，减少调用方改动。

---

## Sprint 2: UI 层 + DI (3 checks)

### Inc 4: `HilView.xaml` TabControl + WebView2 + fallback

**Files**: `App/Views/HilView.xaml` (MODIFY)

**Implementation**:
- XAML 顶部加 `xmlns:wv2="clr-namespace:Microsoft.Web.WebView2.Wpf;assembly=Microsoft.Web.WebView2.Wpf"`
- 替换中部 `<Grid>`（`:67-102`）为 `<TabControl>`
  - TabItem "Results"：原 DataGrid + TreeView 原样搬入
  - TabItem "HTML Report"：DockPanel -> StackPanel(顶部工具栏) + Grid(叠放 Border/WebView2 + fallback TextBlock)
- LLM Analysis TextBox 保留原位置（`DockPanel.Dock="Bottom"`）

**Key constraint (R1)**: HTML Report tab 的 DockPanel 中，Grid（含 WebView2 + fallback）是 last-child 填充剩余空间。fallback TextBlock 在 Grid 内用 DataTrigger 控制 Visibility，不能当 DockPanel last-child（会坍缩 WebView2）。

### Inc 5: `HilView.xaml.cs` code-behind

**Files**: `App/Views/HilView.xaml.cs` (MODIFY)

**Implementation**:
- `using System.ComponentModel;` + `using Microsoft.Web.WebView2.Core;`
- `Loaded += OnLoaded` + `Unloaded += OnUnloaded`
- `OnLoaded`：`_vm = DataContext as HilViewModel` -> 订阅 `PropertyChanged` -> `await EnsureCoreWebView2Async()` -> try `NavigateToReport(LatestReportPath)` / catch 设 `ReportError` + `ShowReportError=true`
- `OnUnloaded`：`_isLoaded = false` + `_vm.PropertyChanged -= OnVmPropertyChanged`（B3 防累积）
- `OnVmPropertyChanged`：`LatestReportPath` 变化 -> `NavigateToReport`
- `NavigateToReport`：`CoreWebView2.Navigate(new Uri(filePath).AbsoluteUri)`（B2 绕开 NavigateToString 2MB 限制）
- 不 dispose WebView2（ScriptView v2.0.7 教训）

### Inc 6: DI 注册 + 端到端编译验证

**Files**: `App/Composition/AppHostBuilder.cs` (MODIFY)

**Implementation**:
- `:302` 附近追加 `builder.Services.AddSingleton<Infrastructure.HIL.Reporting.IHilReportService, Infrastructure.HIL.Reporting.HilReportService>();`

**Verification**:
| Check | Command | Expected |
|-------|---------|----------|
| Build passes | `dotnet build` | 0 errors |
| All new tests green | `dotnet test --filter "FullyQualifiedName~HilReportService\|FullyQualifiedName~HilViewModelReport"` | 0 failed |
| Existing HIL tests green | `dotnet test --filter "FullyQualifiedName~HIL"` | 0 new failures |
| `IHilReportService` registered | grep `IHilReportService` in `AppHostBuilder.cs` | 1 match |
| `HilViewModel` ctor has 5 params | grep `public HilViewModel` in `HilViewModel.cs` | 5 params |
| 0 `new HilViewModel(` with 4 params | grep -rn `new HilViewModel(` tests/ + check 5th param | all 10 have 5th param |

---

## Post-checks (verify after coding)

| # | Check | Command | Expected |
|---|-------|---------|----------|
| 0 | Build passes | `dotnet build` | 0 errors |
| 1 | All new tests green | `dotnet test --filter "FullyQualifiedName~HilReportService\|FullyQualifiedName~HilViewModelReport"` | 0 failed |
| 2 | Existing HIL tests green | `dotnet test --filter "FullyQualifiedName~HIL"` | 0 new failures (4 既有 TraceViewer 失败除外) |
| 3 | `IHilReportService` exists | grep `IHilReportService` in src/ | interface + impl + DI registration |
| 4 | `HilViewModel` ctor has 5 params | grep `public HilViewModel` in `HilViewModel.cs` | 5 params including `IHilReportService` |
| 5 | 10 `new HilViewModel(` all have 5th param | grep -rn `new HilViewModel(` tests/ | all 10 have mock reportService |
| 6 | `HilView.xaml` has TabControl | grep `TabControl` in `HilView.xaml` | 1 match |
| 7 | `HilView.xaml` has WebView2 | grep `WebView2` in `HilView.xaml` | 1 match |
| 8 | `HilView.xaml.cs` has OnUnloaded unsubscribe | grep `PropertyChanged -=` in `HilView.xaml.cs` | 1 match |
| 9 | `HilView.xaml.cs` uses Navigate not NavigateToString | grep `NavigateToString` in `HilView.xaml.cs` | 0 matches |
| 10 | `AppHostBuilder` registers IHilReportService | grep `IHilReportService` in `AppHostBuilder.cs` | 1 match |

---

## Risk Notes

- **WebView2 初始化竞态**：`EnsureCoreWebView2Async` 是异步的，`LatestReportPath` 可能在初始化完成前已设置 -- `OnLoaded` 初始化完成后立即导航一次。
- **同步 IO 阻塞 UI**：`HilReportService.Generate` 是同步方法（HTML 生成 + 落盘）。对于大报告（几百 KB），可能阻塞 UI 线程几十毫秒。与 CLI 一致，本单元不改 async。
- **`HilReportResult.Html` 冗余**：VM 只用 `FilePath`，`Html` 供测试断言。生产中 `report.Html` 是不被 VM 使用的大字符串，但 `Generate` 必须返回 HTML 以写入文件，复用返回值给测试是自然的。
- **tab 切换重复订阅**：`OnUnloaded` 退订 `PropertyChanged`（B3），`OnLoaded` 重新订阅 -- 幂等。
