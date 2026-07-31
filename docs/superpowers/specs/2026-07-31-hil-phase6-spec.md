# HIL Phase 6: Wiring Completion + ODX State-Chart + Resilience + Credential Unification

> Depends: Phase 5 (complete — commit `9b25d04`)
> Spec date: 2026-07-31
> Integration: merges Phase 5 spec §7 deferred items + Phase 5 未接线代码 gap 分析

---

## 1. Goals

Phase 5 交付了 6 大能力（ODX 适配器、Generator 插件、HTML 报告、WPF 面板、独立模拟器、LLM 分析），但存在两类遗留：

**A. Phase 5 spec 明确定义的 deferred 项（§7 Out of Scope）：**
1. ODX STATE-CHART 解析
2. Polly retry
3. Routine POS-RESPONSE 解析
4. Credential Store 统一

**B. Phase 5 实现但未接线的代码（gap）：**
5. CLI 报告格式未接线 — `HtmlReportGenerator`/`TrendTracker`/`FrameCaptureExporter` 已实现但 `Program.cs` 不支持 `--format html`/`--export-frames`
6. LLM 分析未接线 — `HilAnalysisService` 已实现但 `HilViewModel.AnalyzeAsync` 是 stub，`IHilAnalysisService` 未在 DI 注册

---

## 2. Current State

### 2.1 Phase 5 Delivered (commit `0ddadc8`)

| Component | Status | File |
|-----------|--------|------|
| `OdxToEcuScriptAdapter` | ✅ 完整 | `Infrastructure/HIL/Odx/OdxToEcuScriptAdapter.cs` |
| `GeneratorPluginLoader` | ✅ 完整 | `Infrastructure/HIL/Generators/GeneratorPluginLoader.cs` |
| `DidReadoutGenerator` | ✅ 完整 | `Infrastructure/HIL/Generators/DidReadoutGenerator.cs` |
| `DidWriteGenerator` | ✅ 完整 | `Infrastructure/HIL/Generators/DidWriteGenerator.cs` |
| `HtmlReportGenerator` | ✅ 完整但未接线 | `Infrastructure/Cli/Reporting/HtmlReportGenerator.cs` |
| `TrendTracker` | ✅ 完整但未接线 | `Infrastructure/Cli/Reporting/TrendTracker.cs` |
| `ConsoleSummaryFormatter` | ✅ 完整但未接线 | `Infrastructure/Cli/Reporting/ConsoleSummaryFormatter.cs` |
| `FrameCaptureExporter` | ✅ 完整但未接线 | `Infrastructure/Cli/Reporting/FrameCaptureExporter.cs` |
| `EcuSimulatorHost` | ✅ 完整 | `Infrastructure/HIL/EcuSimulatorHost.cs` |
| `HilAnalysisService` | ✅ 完整但未接线 | `Infrastructure/HIL/Analysis/HilAnalysisService.cs` |
| `SimpleCredentialStore` | ✅ 完整 | `Infrastructure/HIL/Analysis/SimpleCredentialStore.cs` |
| `HilPromptBuilder` | ✅ 完整 | `Infrastructure/HIL/Analysis/HilPromptBuilder.cs` |

### 2.2 未接线的代码（Phase 5 gap 分析）

| Gap | Evidence |
|-----|----------|
| CLI 不支持 `--format html` | `CliArgs.cs:134` PrintHelp 只列 `console/trx/junit`；`Program.cs:80-86` 只处理 `trx`/`junit` |
| 无 `--export-frames` | `CliArgs` 无 `ExportFramesDir` 字段；`CliArgsParser` 无此 flag |
| `HilViewModel.AnalyzeAsync` 是 stub | `HilViewModel.cs:109-113`：`_logger.LogInformation("AnalyzeCommand invoked (Sprint 14 stub)")` |
| `IHilAnalysisService` 未注册 | `HeadlessHostBuilder.cs` 无注册；`AppServicesFlow.cs` 无注册 |
| `HilRunRequest.EnableAnalyze` 硬编码 false | `HilViewModel.cs:139`：`EnableAnalyze: false` |

### 2.3 Existing Infrastructure

| 类型 | 位置 | 说明 |
|------|------|------|
| `ICredentialStore` | `Core/Analysis/ICredentialStore.cs` | `GetAsync/SetAsync/DeleteAsync` |
| `WindowsCredentialManagerStore` | `App/Services/CredentialStore/WindowsCredentialManagerStore.cs` | DPAPI 加密，advapi32 P/Invoke |
| `SimpleCredentialStore` | `Infrastructure/HIL/Analysis/SimpleCredentialStore.cs` | 内存→环境变量→`~/.hil/credentials` JSON |
| `RequestBasedMappers` | `Core/Uds/Odx/RequestBasedMappers.cs` | 已有 POS-RESPONSE chain walk（DID 专用） |
| `HilAnalysisService` | `Infrastructure/HIL/Analysis/HilAnalysisService.cs` | 构造函数 `(ICredentialStore, HttpClient? = null)` |
| `EcuStateMachine` | `Core/HIL/Contracts/EcuStateMachine.cs` | 初始状态 `"default"`（`L13`），`MatchesState` 接受 `FromState==null` 或 `==_currentState`（`L74-75`） |
| `EcuScript` | `Infrastructure/HIL/EcuScript.cs` | 当前无 `InitialState` 字段（Phase 6 新增） |
| `TrendTracker` | `Infrastructure/Cli/Reporting/TrendTracker.cs` | API: `Record(entry, path?, maxEntries?)` / `Load(path?)` |
| `FrameCaptureExporter` | `Infrastructure/Cli/Reporting/FrameCaptureExporter.cs` | API: `ExportAsync(result, directory, ct)` |
| `HtmlReportGenerator` | `Infrastructure/Cli/Reporting/HtmlReportGenerator.cs` | API: `GenerateHtml(result, trends?)` → string |
| `ConsoleSummaryFormatter` | `Infrastructure/Cli/Reporting/ConsoleSummaryFormatter.cs` | API: `Format(result)` → string |

**⚠️ 术语说明（T1-R1 修复）**：
- `"wildcard"` 是 JSON 序列化层占位符，对应运行时 `FromState = null`
- `OdxEcuScriptImporter` 分组时 `FromState ?? "wildcard"` → JSON 中出现 `"name": "wildcard"`
- `EcuScriptLoader` 解析时 `"wildcard"` → `null`
- 实现时**不要**在 `EcuStateMachine` 中写 `if (state == "wildcard")`，运行时不存在此状态名

### 2.4 Test Fixtures

- `tests/PeakCan.Host.Core.Tests/Fixtures/Odx/Demo_Cdd.odx-d` — 含 2 个 STATE-CHART（`Session` + `SecurityAccess`），共 **15** 个 STATE-TRANSITION（Session 6 个 + SecurityAccess 9 个）。**注意**：Demo_Cdd 中不存在 `<DIAG-COMM-REF>` 元素，STATE-TRANSITION 关联路径为 `DIAG-SERVICE → STATE-TRANSITION-REFS → STATE-TRANSITION-REF → STATE-TRANSITION`
- `tests/PeakCan.Host.Core.Tests/Fixtures/Odx/complete.odx` — 无 STATE-CHART（向后兼容测试用）

---

## 3. Design Decisions

### 3.1 CLI 报告格式接线

**问题**：`HtmlReportGenerator`/`TrendTracker`/`FrameCaptureExporter`/`ConsoleSummaryFormatter` 已实现且有测试，但 `Program.cs` 和 `CliArgs` 完全不支持这些格式。

**方案**：扩展 `CliArgs` + `CliArgsParser` + `Program.cs`。

**CliArgs 新增字段**：
```csharp
string? ExportFramesDir = null   // --export-frames <dir>
```

**CliArgsParser 新增 flag**：
```csharp
case "--export-frames": exportFramesDir = args[++i]; break;
```

**Format 扩展**：`Format` 字段已支持任意字符串，无需修改类型。`PrintHelp` 更新列出新值。

**Program.cs 修改**（L3-R5 修复：替换 `engine.ExecuteAsync` 之后的整个 if+return 块）：

```csharp
// 当前 Program.cs:77-88 的代码结构：
//   var result = await engine.ExecuteAsync(...);
//   if (cli.OutputPath is not null) { ... return result.AllPassed ? 0 : 1; }
//
// 修改为：删除 if 块，替换为以下 switch + return（return 在 switch 之后）
```

```csharp
// Report generation (after engine.ExecuteAsync)
// 注意：console 模式下，engine.ExecuteAsync 运行期间已通过 ConsoleProgress 输出逐条进度（ProgressBar）
// 运行结束后追加 ConsoleSummaryFormatter 汇总。两者输出不冲突（进度是行内\r刷新，汇总是换行输出）
switch (cli.Format)
{
    case "html":
        var trends = TrendTracker.Load("./hil-trends.json");
        var html = HtmlReportGenerator.GenerateHtml(result, trends);
        var htmlPath = cli.OutputPath ?? $"hil-report-{DateTime.UtcNow:yyyyMMddHHmmss}.html";
        await File.WriteAllTextAsync(htmlPath, html);
        Console.WriteLine($"HTML report written to {htmlPath}");
        TrendTracker.Record(new TrendEntry(DateTime.UtcNow, result.SuiteName,
            result.TotalCases, result.PassedCases, result.FailedCases, (int)result.ElapsedMs));
        break;
    case "html+junit":
        // HTML + JUnit dual output
        var trends2 = TrendTracker.Load("./hil-trends.json");
        var html2 = HtmlReportGenerator.GenerateHtml(result, trends2);
        var htmlPath2 = Path.ChangeExtension(cli.OutputPath ?? "hil-report", ".html");
        await File.WriteAllTextAsync(htmlPath2, html2);
        var junitPath = Path.ChangeExtension(cli.OutputPath ?? "hil-report", ".xml");
        await JUnitWriter.WriteJunit(result, junitPath);
        TrendTracker.Record(new TrendEntry(DateTime.UtcNow, result.SuiteName,
            result.TotalCases, result.PassedCases, result.FailedCases, (int)result.ElapsedMs));
        break;
    case "junit":
        await JUnitWriter.WriteJunit(result, cli.OutputPath ?? "hil-report.xml");
        break;
    case "trx":
        await ResultWriter.WriteTrx(result, cli.OutputPath ?? "hil-report.trx");
        break;
    case "console":
    default:
        // console 模式：追加汇总到进度输出之后
        Console.WriteLine(ConsoleSummaryFormatter.Format(result));
        break;
}

// Frame export (independent of format)
if (cli.ExportFramesDir is not null)
{
    await FrameCaptureExporter.ExportAsync(result, cli.ExportFramesDir);
    Console.WriteLine($"Frame captures exported to {cli.ExportFramesDir}");
}
```

**PrintHelp 更新**：
```
--format <format>  Output format: console (default), trx, junit, html, html+junit
--export-frames <dir>  Export fault frames as .asc files (independent of format)
```

**向后兼容**：`Format` 默认值仍为 `"console"`，现有行为不变。

### 3.2 LLM 分析接线

**问题**：`HilAnalysisService` 已实现但：
1. 未在 `HeadlessHostBuilder` 注册
2. 未在 `AppHostBuilder` 注册
3. `HilViewModel.AnalyzeAsync` 是 stub
4. `HilRunRequest.EnableAnalyze` 硬编码 `false`

**方案**：

**⚠️ EnableAnalyze 语义澄清（L3-R1 修复）**：
`EnableAnalyze` 是 WPF 后置分析开关，**不经过 CLI 路径**。分析是用户在 WPF 中点击 "Analyze" 按钮后的独立动作，不需要在 `HilRunRequestExtensions.ToCliArgs` 中传播，也不需要 `CliArgs` 新增字段。`EnableAnalyze` 仅控制 WPF 中 "Analyze" 按钮的 `CanExecute` 逻辑（是否允许触发分析）。

**DI 注册（Sprint 19 统一用 AddHttpClient，此处不重复注册）**：

Sprint 16 **不**注册 `IHilAnalysisService`（避免与 Sprint 19 的 `AddHttpClient` 冲突，见 L5-R1）。注册统一在 Sprint 19 完成。Sprint 16 仅完成 ViewModel 接线和 UI 绑定。

**HilViewModel 接线**：

```csharp
// 新增字段
private readonly IHilAnalysisService _analysisService;
private TestSuiteResult? _lastResult;

// 构造函数新增参数（IHilAnalysisService 由 DI 注入，Sprint 19 注册）
public HilViewModel(IHilRunnerService runner, ILogger<HilViewModel> logger,
    IFileDialogService fileDialog, IHilAnalysisService analysisService)
{
    _runner = runner;
    _logger = logger;
    _fileDialog = fileDialog;
    _analysisService = analysisService;
}
```

**⚠️ 构造函数变更影响（T4-R1 修复）**：

`HilViewModel` 构造函数新增 `IHilAnalysisService` 参数，以下 **4 个测试文件共 9 处**调用需同步更新：

| 文件 | 调用数 | 修改内容 |
|------|--------|---------|
| `App.Tests/ViewModels/HilViewModelTests.cs` | 1 | 新增 `Mock<IHilAnalysisService>` 参数 |
| `App.Tests/ViewModels/AppShellViewModelTests.cs` | 6 | 新增 `Mock<IHilAnalysisService>` 参数 |
| `App.Tests/ViewModels/AppShellViewModelMessageBoxPromptTests.cs` | 1 | 新增 `Mock<IHilAnalysisService>` 参数 |
| `App.Tests/Windows/UdsWindowTests.cs` | 1 | 新增 `Mock<IHilAnalysisService>` 参数 |

// AnalyzeCommand 替换 stub
[ObservableProperty] private string _analysisResult = "";
[ObservableProperty] private bool _isAnalyzing = false;
[ObservableProperty] private bool _enableAnalyze = false;

[RelayCommand(CanExecute = nameof(CanAnalyze))]
private async Task AnalyzeAsync()
{
    if (_lastResult is null || _lastResult.AllPassed) return;

    IsAnalyzing = true;
    AnalysisResult = "Analyzing...";

    try
    {
        var analysis = await _analysisService.AnalyzeAsync(_lastResult, default);
        AnalysisResult = analysis is { IsUnavailable: false }
            ? analysis.Content
            : $"Analysis unavailable: {analysis?.UnavailableReason ?? "unknown"}";
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "LLM analysis failed");
        AnalysisResult = $"Error: {ex.Message}";
    }
    finally
    {
        IsAnalyzing = false;
    }
}

private bool CanAnalyze() => !IsAnalyzing && _lastResult is { AllPassed: false };
```

**RunAsync 中保存 `_lastResult`（L4-R4 修复）**：

```csharp
var result = await _runner.RunAsync(request, progress, default);
_lastResult = result;  // 保存供 AnalyzeAsync 使用
AnalyzeCommand.NotifyCanExecuteChanged();  // L4-R4：通知 UI 更新按钮状态
```

> **⚠️ L4-R4 修复**：`_lastResult` 是普通字段（非 `[ObservableProperty]`），变化不会自动触发 `[RelayCommand(CanExecute = ...)]` 重新评估。必须显式调用 `NotifyCanExecuteChanged()`，否则 "Analyze" 按钮保持禁用。

**HilRunRequest.EnableAnalyze 改为绑定**：

```csharp
var request = new HilRunRequest(
    ...,
    EnableAnalyze: EnableAnalyze);  // 由 UI CheckBox 控制
```

**HilView.xaml 新增**：

```xml
<!-- 在 Faults CheckBox 旁 -->
CheckBox Content="Analyze" IsChecked="{Binding EnableAnalyze}"
          VerticalAlignment="Center" Margin="0,0,12,0" />

<!-- 在 Results Grid 下方 -->
<TextBox Text="{Binding AnalysisResult}" IsReadOnly="True"
         AcceptsReturn="True" VerticalScrollBarVisibility="Auto"
         Height="120" Margin="0,8,0,0" />
```

**测试影响**：`HilViewModelTests` 构造函数需新增 `IHilAnalysisService` mock 参数。

### 3.3 Credential Store 统一

**问题**：WPF 用 `WindowsCredentialManagerStore`（advapi32.dll），CLI 用 `SimpleCredentialStore`（内存+环境变量+文件），两套实现互不可见。

**方案**：引入 `ChainedCredentialStore`（Infrastructure 层），按优先级链式查找。

```csharp
// Infrastructure/HIL/Analysis/ChainedCredentialStore.cs

public sealed class ChainedCredentialStore : ICredentialStore
{
    private readonly IReadOnlyList<ICredentialStore> _stores;

    public ChainedCredentialStore(params ICredentialStore[] stores)
    {
        _stores = stores ?? throw new ArgumentNullException(nameof(stores));
        if (_stores.Count == 0) throw new ArgumentException("At least one store required", nameof(stores));
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        foreach (var store in _stores)
        {
            var value = await store.GetAsync(key, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(value))
                return value;
        }
        return null;
    }

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        // 写入第一个 store（主存储）
        return _stores[0].SetAsync(key, value, ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        // 从所有 store 删除（尽力而为，单个 store 失败不中断）
        foreach (var store in _stores)
        {
            try
            {
                await store.DeleteAsync(key, ct).ConfigureAwait(false);
            }
            catch (CredentialStoreException)
            {
                // 某个 store 删除失败（如 WCM 中不存在），继续清理其他 store
            }
        }
    }
}
```

**注册策略**：

| 场景 | 注册方式 | 回退链 |
|------|---------|--------|
| WPF (App) | `ChainedCredentialStore(WindowsCredentialManagerStore, SimpleCredentialStore)` | WCM → 环境变量 → 文件 |
| CLI (Infrastructure) | `SimpleCredentialStore` | 环境变量 → 文件 |

**注意**：`WindowsCredentialManagerStore` 在 App 层，`HeadlessHostBuilder` 在 Infrastructure 层，Infrastructure 不能引用 App。因此 CLI 路径仍仅用 `SimpleCredentialStore`。`ChainedCredentialStore` 让 WPF 路径可同时看到两套凭证。

**AppServicesFlow 修改**：

```csharp
// 替换现有 ICredentialStore 注册:
services.AddSingleton<Core.Analysis.ICredentialStore>(sp =>
{
    var winStore = new WindowsCredentialManagerStore(
        sp.GetRequiredService<ILogger<WindowsCredentialManagerStore>>());
    return new ChainedCredentialStore(winStore, new SimpleCredentialStore());
});
```

**向后兼容**：现有 `WindowsCredentialManagerStore` 和 `SimpleCredentialStore` 实现不变。`ChainedCredentialStore` 是新增抽象。

### 3.4 ODX STATE-CHART 导入

**问题**：Phase 5 的 `OdxToEcuScriptAdapter` 将所有 transition 的 `FromState` 设为 `null`（wildcard），全部挂到 `wildcard` 状态。真实 ODX STATE-CHART 定义了 ECU 的状态转换图。

**⚠️ 关键约束（L1-R1 + L2-R1 修复）**：

1. **Demo_Cdd.odx-d 中不存在 `<DIAG-COMM-REF>` 元素**。关联路径是：
   ```
   DIAG-SERVICE → STATE-TRANSITION-REFS → STATE-TRANSITION-REF (ID-REF) → STATE-TRANSITION 元素 → SOURCE-SNREF / TARGET-SNREF
   ```
   不能依赖 `DiagCommIds` 字段（恒为空）。

2. **EcuStateMachine 初始状态是 `"default"`**（`EcuStateMachine.cs:13`）。如果 `FromState` 被设为 `"Locked"`，初始状态不匹配 → transition 永远不会触发。解决方案：
   - `EcuScript` 新增 `InitialState` 字段（默认 `"default"`）
   - STATE-CHART 解析成功后，`InitialState` 设为 STATE-CHART 的 `START-STATE-SNREF`（如 `"Locked"`）
   - `EcuStateMachine` 构造时读取 `InitialState` 作为 `_currentState` 初始值

**STATE-CHART 结构**（Demo_Cdd.odx-d）：

```xml
<STATE-CHARTS>
  <STATE-CHART ID="_362">
    <SHORT-NAME>SecurityAccess</SHORT-NAME>
    <SEMANTIC>SECURITY</SEMANTIC>
    <STATE-TRANSITIONS>
      <STATE-TRANSITION ID="_639">
        <SOURCE-SNREF SHORT-NAME="Locked" />
        <TARGET-SNREF SHORT-NAME="UnlockedL1" />
      </STATE-TRANSITION>
      ...
    </STATE-TRANSITIONS>
    <START-STATE-SNREF SHORT-NAME="Locked" />
    <STATES>
      <STATE ID="_363"><SHORT-NAME>Locked</SHORT-NAME>...</STATE>
      <STATE ID="_364"><SHORT-NAME>UnlockedL1</SHORT-NAME>...</STATE>
      <STATE ID="_365"><SHORT-NAME>Unlocked_L2</SHORT-NAME>...</STATE>
    </STATES>
  </STATE-CHART>
</STATE-CHARTS>
```

**DIAG-SERVICE 关联**：

```xml
<DIAG-SERVICE ID="_415" SEMANTIC="SESSION">
  <STATE-TRANSITION-REFS>
    <STATE-TRANSITION-REF ID-REF="_417" />
    ...
  </STATE-TRANSITION-REFS>
  <REQUEST-REF ID-REF="_411" />
</DIAG-SERVICE>
```

**方案**：新增 `OdxStateChartExtractor`（Core/Uds/Odx/），解析 STATE-CHART 元素。

**新增记录类型**：

```csharp
// Core/Uds/Odx/OdxStateChartInfo.cs

public sealed record OdxStateChartInfo(
    string ChartName,
    string StartState,
    IReadOnlyList<string> StateNames,
    IReadOnlyList<StateChartTransition> Transitions);

/// <summary>
/// Single state transition from a STATE-CHART.
/// TransitionId is the XML ID attribute of the STATE-TRANSITION element,
/// used to match against DIAG-SERVICE's STATE-TRANSITION-REF entries.
/// </summary>
public sealed record StateChartTransition(
    string TransitionId,
    string SourceState,
    string TargetState);
```

**OdxStateChartExtractor**：

```csharp
// Core/Uds/Odx/OdxStateChartExtractor.cs

public static class OdxStateChartExtractor
{
    /// <summary>
    /// Extract the first SEMANTIC-matched STATE-CHART from ODX.
    /// Returns null if no STATE-CHART found.
    /// </summary>
    public static OdxStateChartInfo? TryExtract(XDocument xdoc, XNamespace ns, string? semantic = null)
    {
        var charts = xdoc.Descendants(ns + "STATE-CHART").ToList();
        if (charts.Count == 0) return null;

        // Prefer chart matching semantic (e.g., "SECURITY"), else take first
        var chart = semantic is not null
            ? charts.FirstOrDefault(c => (string?)c.Element(ns + "SEMANTIC") == semantic) ?? charts[0]
            : charts[0];

        var chartName = (string?)chart.Element(ns + "SHORT-NAME") ?? "StateChart";
        var startState = (string?)chart.Element(ns + "START-STATE-SNREF")?.Attribute("SHORT-NAME") ?? "Default";

        // Build state ID → name map
        var stateIdToName = new Dictionary<string, string>();
        foreach (var state in chart.Descendants(ns + "STATE"))
        {
            var id = (string?)state.Attribute("ID");
            var name = (string?)state.Element(ns + "SHORT-NAME");
            if (id is not null && name is not null)
                stateIdToName[id] = name;
        }

        var stateNames = stateIdToName.Values.ToList();

        // Extract transitions with their XML IDs
        var transitions = new List<StateChartTransition>();
        foreach (var st in chart.Descendants(ns + "STATE-TRANSITION"))
        {
            var transitionId = (string?)st.Attribute("ID");
            var sourceRef = (string?)st.Element(ns + "SOURCE-SNREF")?.Attribute("SHORT-NAME");
            var targetRef = (string?)st.Element(ns + "TARGET-SNREF")?.Attribute("SHORT-NAME");
            if (transitionId is null || sourceRef is null || targetRef is null) continue;

            transitions.Add(new StateChartTransition(transitionId, sourceRef, targetRef));
        }

        return new OdxStateChartInfo(chartName, startState, stateNames, transitions);
    }

    /// <summary>
    /// Build a map: DIAG-SERVICE XML ID → list of STATE-TRANSITION-REF IDs it references.
    /// This is the correct association path (no DIAG-COMM-REF in Demo_Cdd).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildDiagServiceTransitionMap(
        XDocument xdoc, XNamespace ns)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var svc in xdoc.Descendants(ns + "DIAG-SERVICE"))
        {
            var svcId = (string?)svc.Attribute("ID");
            if (svcId is null) continue;

            var transitionRefs = svc.Descendants(ns + "STATE-TRANSITION-REF")
                .Select(r => (string?)r.Attribute("ID-REF"))
                .Where(id => id is not null)
                .Select(id => id!)
                .ToList();

            if (transitionRefs.Count > 0)
                result[svcId] = transitionRefs;
        }
        return result;
    }

}  // OdxStateChartExtractor 类结束

/*
 * B1-R2 + L4-R3 修复：
 * 1. RequestBasedMappers.ReadServiceId/ReadSubfunctionParam/ParseByte 改为 internal（原为 private）
 * 2. OdxStateChartExtractor 不定义 BuildDiagServiceToRequestMap（死代码，L4-R3）
 *    OdxToEcuScriptAdapter 有自己的 private 版本，正确使用 RequestBasedMappers. 前缀
 * 3. ParseByte 正确读取 CODED-VALUE 子元素（非 param.Value）：
 *        var v = p.Element(ns + "CODED-VALUE"); // 正确：只取值
 *        // param.Value 会拼接 SHORT-NAME + LONG-NAME + BYTE-POSITION + CODED-VALUE → 解析失败
 */
```

**OdxToEcuScriptAdapter 修改（L2-R2 修复）**：

`Load()` 方法签名**新增 `out string initialState` 参数**（L2-R5 修复：原文"保持不变"有误，实际新增 out 参数）。
`InitialState` 通过 **out 参数** 返回给调用方（`OdxEcuScriptImporter`）。

```csharp
// OdxToEcuScriptAdapter.cs — Load 方法签名变更:
public IReadOnlyList<EcuStateTransition> Load(string odxPath, out string initialState)
{
    // ... existing extraction logic ...

    initialState = "default";  // 默认值

    var stateChart = OdxStateChartExtractor.TryExtract(doc, ns, "SECURITY");
    if (stateChart is { } chart)
    {
        initialState = chart.StartState;  // 如 "Locked"

        // 构建 STATE-TRANSITION XML ID → (source, target) 映射
        var transitionMap = chart.Transitions.ToDictionary(t => t.TransitionId);

        // 构建 DIAG-SERVICE → [STATE-TRANSITION-REF IDs] 映射
        var diagSvcTransitions = OdxStateChartExtractor.BuildDiagServiceTransitionMap(doc, ns);

        // 构建 DIAG-SERVICE → (SID, subFunc) 映射（复用 RequestBasedMappers internal 方法）
        var diagSvcToRequest = BuildDiagServiceToRequestMap(doc, ns);

        // 建立 (SID, subFunc) → [(sourceState, targetState)] 映射
        // ⚠️ T3-R2 修复：使用 ServiceRequest 记录（含 SubFunction）避免 ?? 0 误匹配
        var stateTransitionsByService = new Dictionary<ServiceRequest, List<(string From, string To)>>();
        foreach (var (svcId, transitionRefs) in diagSvcTransitions)
        {
            if (!diagSvcToRequest.TryGetValue(svcId, out var req)) continue;
            foreach (var transitionRef in transitionRefs)
            {
                if (!transitionMap.TryGetValue(transitionRef, out var scTrans)) continue;
                var key = new ServiceRequest(req.Sid, req.Sub);
                if (!stateTransitionsByService.TryGetValue(key, out var list))
                    stateTransitionsByService[key] = list = new List<(string, string)>();
                list.Add((scTrans.SourceState, scTrans.TargetState));
            }
        }

        // 更新已有 transition 的 FromState/ToState
        for (int i = 0; i < transitions.Count; i++)
        {
            var t = transitions[i];
            if (t.SubFunction is { } sub &&
                stateTransitionsByService.TryGetValue(new ServiceRequest(t.ServiceId, sub), out var st))
            {
                var (fromState, toState) = st[0];
                transitions[i] = t with { FromState = fromState, ToState = toState };
            }
        }
    }

    return transitions;
}

// 辅助记录（文件内 private）
private readonly record struct ServiceRequest(byte Sid, byte Sub);

// BuildDiagServiceToRequestMap 复用 RequestBasedMappers internal 方法:
private static IReadOnlyDictionary<string, (byte Sid, byte Sub)> BuildDiagServiceToRequestMap(
    XDocument xdoc, XNamespace ns)
{
    var requestById = new Dictionary<string, XElement>();
    foreach (var req in xdoc.Descendants(ns + "REQUEST"))
    {
        var id = (string?)req.Attribute("ID");
        if (id is not null) requestById[id] = req;
    }

    var result = new Dictionary<string, (byte Sid, byte Sub)>();
    foreach (var svc in xdoc.Descendants(ns + "DIAG-SERVICE"))
    {
        var svcId = (string?)svc.Attribute("ID");
        var reqRefEl = svc.Element(ns + "REQUEST-REF");
        if (svcId is null || reqRefEl is null) continue;
        var reqRefId = (string?)reqRefEl.Attribute("ID-REF");
        if (reqRefId is null || !requestById.TryGetValue(reqRefId, out var req)) continue;

        // 复用 RequestBasedMappers internal 方法（B1-R2 修复）
        var sid = RequestBasedMappers.ReadServiceId(req, ns);
        var sub = RequestBasedMappers.ReadSubfunctionParam(req, ns);
        if (sid is not null)
            result[svcId] = (sid.Value, sub);  // sub 非 nullable（RequestBasedMappers 返回 byte）
    }
    return result;
}
```

**OdxEcuScriptImporter 修改（L4-R2 修复）**：

```csharp
public static string ImportToJson(string odxPath, string ecuName, uint requestId, uint responseId)
{
    var adapter = new OdxToEcuScriptAdapter();
    var transitions = adapter.Load(odxPath, out var initialState);  // 获取 initialState

    // ... existing grouping logic ...

    var script = new
    {
        name = ecuName,
        initialState,  // 新增字段
        canIds = new { requestId = $"0x{requestId:X3}", responseId = $"0x{responseId:X3}" },
        states
    };

    return JsonSerializer.Serialize(script, HILJsonOptions.Default);
}
```

**EcuScript 修改（T1-R2 修复：文件在 Infrastructure 层）**：

```csharp
// Infrastructure/HIL/EcuScript.cs 新增字段:
public string InitialState { get; init; } = "default";
```

**EcuStateMachine 修改（L1-R3 修复）**：

```csharp
// Core/HIL/Contracts/EcuStateMachine.cs:
// 新增字段:
private readonly string _initialState;

// 构造函数新增 initialState 参数，保持 generators 可选（L1-R3 修复）:
public EcuStateMachine(
    IEnumerable<EcuStateTransition> transitions,
    IEnumerable<IEcuResponseGenerator>? generators = null,  // 保持 ? = null（12+ 处单参数调用依赖）
    string initialState = "default")
{
    _transitions = transitions.ToList();
    _generators = generators?.ToDictionary(g => g.Name) ?? new();
    _initialState = initialState;  // 保存初始状态（L1-R4 修复）
    _currentState = initialState;  // 替代硬编码 "default"
}

// L1-R4 修复：Reset() 恢复到 InitialState 而非硬编码 "default"
public void Reset()
{
    _currentState = _initialState;  // 恢复为初始状态（可能是 "Locked"）
    _context.Clear();
}
```

**EcuScriptLoader 修改（L2-R2 + L3-R2 + L3-R3 + T1-R3 修复）**：

```csharp
// Infrastructure/HIL/EcuScriptLoader.cs:

// 1. ParseStateMachine 签名新增 initialState 参数（L2-R3 修复）:
private static EcuStateMachine ParseStateMachine(JsonElement statesEl,
    List<IEcuResponseGenerator> generators, string initialState)
{
    var allTransitions = new List<EcuStateTransition>();
    // ... existing parsing logic ...
    return new EcuStateMachine(allTransitions, generators, initialState);
}

// 2. ParseEcuScript 中读取 initialState（L3-R3 + T1-R3 修复）:
// 使用 TryGetProperty 兼容旧 JSON（无 initialState 字段）
var initialState = element.TryGetProperty("initialState", out var isEl)
    ? isEl.GetString() ?? "default"
    : "default";

// 3. 调用 ParseStateMachine 时传入 initialState:
stateMachine = ParseStateMachine(statesEl, mergedGenerators, initialState);

// 4. 构造 EcuScript 时设置 InitialState:
var script = new EcuScript(Name, CanIds, stateMachine, DidValues)
{
    InitialState = initialState
};
```

**降级策略**：如果 STATE-CHART 不存在或解析失败，`InitialState` 保持 `"default"`，所有 transition 保持 `FromState = null`（当前行为，向后兼容）。

**输出 JSON 变化**：

Phase 5（当前）：
```json
{ "name": "MyECU", "initialState": "default", "states": [
  { "name": "wildcard", "transitions": [
    { "serviceId": "0x27", "subFunction": "0x01", "toState": "seedSent" },
    { "serviceId": "0x27", "subFunction": "0x02", "toState": "unlocked" }
  ]}
]}
```

Phase 6（有 STATE-CHART）：
```json
{ "name": "MyECU", "initialState": "Locked", "states": [
  { "name": "Locked", "transitions": [
    { "serviceId": "0x27", "subFunction": "0x02", "toState": "UnlockedL1", ... }
  ]},
  { "name": "wildcard", "transitions": [
    { "serviceId": "0x27", "subFunction": "0x01", "toState": "seedSent" },
    { "serviceId": "0x22", ... }
  ]}
]}
```

### 3.5 Routine POS-RESPONSE 解析

**问题**：当前 0x31 Routine 响应硬编码为 `[0x71, subFunc]`，不从 ODX 提取真实响应字节。

**方案**：新增 `ExtractRoutineResponses` 方法到 `RequestBasedMappers`，复用已有的 POS-RESPONSE chain walk 逻辑。

```csharp
// RequestBasedMappers.cs 新增

/// <summary>
/// Extract positive response byte patterns for routines (0x31).
/// Walks DIAG-SERVICE -> REQUEST-REF (0x31) -> POS-RESPONSE-REF -> POS-RESPONSE
/// -> PARAM SEMANTIC="DATA" -> collect byte pattern.
/// Returns dictionary: routineId -> byte[] (full response including [0x71, subFunc, ...data]).
/// </summary>
public static IReadOnlyDictionary<ushort, byte[]> ExtractRoutineResponses(
    XDocument xdoc, XNamespace ns)
{
    ArgumentNullException.ThrowIfNull(xdoc);

    // Reuse the same DIAG-SERVICE walk as ExtractRoutines
    var byRequestId = new Dictionary<string, (ushort Id, byte Sub)>();
    foreach (var req in xdoc.Descendants(ns + "REQUEST"))
    {
        var sid = ReadServiceId(req, ns);
        if (sid != ServiceId_RoutineControl) continue;
        var id = ReadIdParam(req, ns);
        if (id is null) continue;
        var reqId = (string?)req.Attribute("ID");
        if (reqId is null) continue;
        var sub = ReadSubfunctionParam(req, ns);
        byRequestId[reqId] = (id.Value, sub);
    }

    // Index POS-RESPONSE id -> element
    var posById = new Dictionary<string, XElement>();
    foreach (var pos in xdoc.Descendants(ns + "POS-RESPONSE"))
    {
        var id = (string?)pos.Attribute("ID");
        if (id is not null) posById[id] = pos;
    }

    var result = new Dictionary<ushort, byte[]>();
    foreach (var svc in xdoc.Descendants(ns + "DIAG-SERVICE"))
    {
        var reqRefEl = svc.Element(ns + "REQUEST-REF");
        if (reqRefEl is null) continue;
        var reqRefId = (string?)reqRefEl.Attribute("ID-REF");
        if (reqRefId is null || !byRequestId.TryGetValue(reqRefId, out var info))
            continue;

        foreach (var posRef in svc.Elements(ns + "POS-RESPONSE-REFS")
                                  .Elements(ns + "POS-RESPONSE-REF"))
        {
            var posId = (string?)posRef.Attribute("ID-REF");
            if (posId is null || !posById.TryGetValue(posId, out var pos))
                continue;

            var dataBytes = ExtractResponseBytes(pos, ns);
            // Full response: [0x71, subFunc, ...data]
            var fullResponse = new byte[dataBytes.Length + 2];
            fullResponse[0] = 0x71;
            fullResponse[1] = info.Sub;
            Array.Copy(dataBytes, 0, fullResponse, 2, dataBytes.Length);
            result[info.Id] = fullResponse;
            break;  // Take first POS-RESPONSE only
        }
    }

    return result;
}

/// <summary>
/// Extract raw byte values from PARAM SEMANTIC="DATA" elements in a POS-RESPONSE.
/// </summary>
private static byte[] ExtractResponseBytes(XElement pos, XNamespace ns)
{
    var bytes = new List<byte>();
    foreach (var param in pos.Descendants(ns + "PARAM"))
    {
        if ((string?)param.Attribute("SEMANTIC") != "DATA") continue;

        // Try CODED-VALUE (decimal integer — ODX schema uses decimal for CODED-VALUE)
        var codedValue = param.Descendants(ns + "CODED-VALUE").FirstOrDefault();
        if (codedValue is not null && byte.TryParse(codedValue.Value,
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
        {
            bytes.Add(b);
            continue;
        }

        // Try PHYSICAL-VALUE (integer)
        var physValue = param.Descendants(ns + "PHYSICAL-VALUE").FirstOrDefault();
        if (physValue is not null && byte.TryParse(physValue.Value,
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var b2))
        {
            bytes.Add(b2);
        }
    }
    return bytes.ToArray();
}
```

**OdxToEcuScriptAdapter 修改**：

```csharp
var routineResponses = RequestBasedMappers.ExtractRoutineResponses(doc, ns);

// 在 routine 循环中:
foreach (var routine in routines)
{
    byte[] startResponse;
    if (routineResponses.TryGetValue(routine.Id, out var respBytes) && respBytes.Length >= 2)
    {
        startResponse = respBytes;
        startResponse[1] = 0x01; // ensure subFunc matches
    }
    else
    {
        startResponse = new byte[] { 0x71, 0x01 }; // fallback
    }
    // ... 使用 startResponse 替代硬编码
}
```

**降级策略**：无 POS-RESPONSE 或解析失败 → 回退到 `[0x71, subFunc]`。

### 3.6 HttpClient 工厂化 + Polly Retry

**问题**：`HilAnalysisService` 使用 `new HttpClient()`，无重试策略，无生命周期管理。

**方案**：使用 `Microsoft.Extensions.Http.Polly` + `IHttpClientFactory`。

**NuGet 依赖**：Infrastructure 项目添加 `Microsoft.Extensions.Http.Polly`（自动传递 `Microsoft.Extensions.Http`）。

**using 依赖（E1-R1 修复）**：
- `HeadlessHostBuilder.cs` 需添加 `using Microsoft.Extensions.DependencyInjection;`（已有）和 `using Polly;`
- `AppServicesFlow.cs` 已有 `using Polly;`

**GetRetryPolicy 共享（E2-R1 修复）**：

`GetRetryPolicy()` 在 `HeadlessHostBuilder`（Infrastructure）和 `AppServicesFlow`（App）中都需要。两个类在不同项目/命名空间，无法直接共享。方案：**各自定义 private static 方法**（代码重复 < 10 行，可接受）。

```csharp
// HeadlessHostBuilder.cs 中:
private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
    => HttpPolicyExtensions
        .HandleTransientHttpError()
        .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        .WaitAndRetryAsync(3, retryAttempt =>
            TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

// AppServicesFlow.cs 中（同样定义）:
private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
    => HttpPolicyExtensions
        .HandleTransientHttpError()
        .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        .WaitAndRetryAsync(3, retryAttempt =>
            TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));
```

**HeadlessHostBuilder 修改**：

```csharp
// HeadlessHostBuilder.Build 中新增:
builder.Services.AddHttpClient<Core.HIL.Analysis.IHilAnalysisService, HilAnalysisService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(150);
    // 注意：HilAnalysisService 使用绝对 URI 发请求，不设 BaseAddress（见 E3-R1）
    client.DefaultRequestHeaders.Add("User-Agent", "peakcan-host/hil-analyze");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
})
.AddPolicyHandler(GetRetryPolicy());
```

**HilAnalysisService 构造函数修改**：

```csharp
// 改为接收 HttpClient（由 IHttpClientFactory 注入）:
// L1-R5 修复：保留 _ownsHttpClient 字段但始终赋 false（由 factory 管理生命周期）
public HilAnalysisService(HttpClient httpClient, ICredentialStore credentialStore)
{
    _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    _credentialStore = credentialStore;
    _ownsHttpClient = false;  // typed client 由 IHttpClientFactory 管理，不拥有 HttpClient
}

// Dispose 不再释放 HttpClient:
public void Dispose()
{
    // _ownsHttpClient 始终为 false，无需释放。保留字段以避免破坏其他调用点（如有）。
}
```

**E3-R1 修复（ApiEndpoint vs BaseAddress）**：

`HilAnalysisService` 当前使用绝对 URI `https://api.deepseek.com/chat/completions` 发请求。typed client 的 `BaseAddress` 与绝对 URI 同时存在时，绝对 URI 胜出。为保持一致性：
- **不设** `BaseAddress`（避免混淆）
- 保持 `ApiEndpoint` 绝对 URI 不变
- Polly policy 在 handler 级别生效，与 URI 方案无关

**AppServicesFlow 修改**（WPF 路径）：

```csharp
services.AddHttpClient<Core.HIL.Analysis.IHilAnalysisService, HilAnalysisService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(150);
    client.DefaultRequestHeaders.Add("User-Agent", "peakcan-host/hil-analyze");
})
.AddPolicyHandler(GetRetryPolicy());
```

**向后兼容**：
- 现有 `HilAnalysisService(ICredentialStore, HttpClient?)` 构造函数签名变更 → 需更新测试
- 测试中传入 mock HttpClient 即可
- `_ownsHttpClient` 逻辑简化（始终 false）

---

## 4. Sprint Breakdown

### Sprint 15: CLI 报告格式接线

| # | 文件 | 操作 | 说明 |
|---|------|------|------|
| 1 | `Infrastructure/Cli/CliArgs.cs` | MODIFY | 新增 `ExportFramesDir` 字段 + `--export-frames` 解析 + PrintHelp 更新 |
| 2 | `PeakCan.Host.Cli/Program.cs` | MODIFY | html/html+junit/console 报告分支 + `--export-frames` 帧导出 |
| 3 | `Infrastructure.Tests/Cli/Reporting/CliReportIntegrationTests.cs` | NEW | 6 个测试：html/html+junit/console 输出、帧导出、格式验证 |

### Sprint 16: LLM 分析接线

| # | 文件 | 操作 | 说明 |
|---|------|------|------|
| 1 | `App/ViewModels/HilViewModel.cs` | MODIFY | AnalyzeAsync 接线、EnableAnalyze 绑定、_lastResult、IHilAnalysisService 字段 |
| 2 | `App/Views/HilView.xaml` | MODIFY | 分析结果 TextBox + EnableAnalyze CheckBox |
| 3 | `App.Tests/ViewModels/HilViewModelTests.cs` | MODIFY | 新增 mock IHilAnalysisService 参数（1 处） |
| 4 | `App.Tests/ViewModels/AppShellViewModelTests.cs` | MODIFY | 新增 mock IHilAnalysisService 参数（6 处） |
| 5 | `App.Tests/ViewModels/AppShellViewModelMessageBoxPromptTests.cs` | MODIFY | 新增 mock IHilAnalysisService 参数（1 处） |
| 6 | `App.Tests/Windows/UdsWindowTests.cs` | MODIFY | 新增 mock IHilAnalysisService 参数（1 处） |
| 7 | `App.Tests/ViewModels/HilViewModelAnalysisTests.cs` | NEW | 6 个测试：AnalyzeAsync 调用、unavailable 处理、CanAnalyze 逻辑 |

**注意**：Sprint 16 **不**注册 `IHilAnalysisService`（避免与 Sprint 19 的 `AddHttpClient` 冲突）。注册统一在 Sprint 19 完成。

### Sprint 17: Credential Store 统一

| # | 文件 | 操作 | 说明 |
|---|------|------|------|
| 1 | `Infrastructure/HIL/Analysis/ChainedCredentialStore.cs` | NEW | 回退链 Credential Store |
| 2 | `App/Composition/AppHostBuilder/AppServicesFlow.cs` | MODIFY | 注册 ChainedCredentialStore(WCM, Simple) |
| 3 | `Infrastructure.Tests/HIL/Analysis/ChainedCredentialStoreTests.cs` | NEW | 5 个测试：回退链、写入主存储、删除全部、空 store、单 store |

### Sprint 18: ODX STATE-CHART + Routine POS-RESPONSE

| # | 文件 | 操作 | 说明 |
|---|------|------|------|
| 1 | `Core/Uds/Odx/OdxStateChartInfo.cs` | NEW | OdxStateChartInfo + StateChartTransition 记录 |
| 2 | `Core/Uds/Odx/OdxStateChartExtractor.cs` | NEW | STATE-CHART 解析器 |
| 3 | `Core/Uds/Odx/RequestBasedMappers.cs` | MODIFY | ReadServiceId/ReadSubfunctionParam 改 internal + 新增 ExtractRoutineResponses + ExtractResponseBytes |
| 4 | `Infrastructure/HIL/EcuScript.cs` | MODIFY | 新增 InitialState 字段 |
| 5 | `Core/HIL/Contracts/EcuStateMachine.cs` | MODIFY | 构造函数新增 initialState + Reset() 恢复 _initialState |
| 6 | `Infrastructure/HIL/EcuScriptLoader.cs` | MODIFY | 读取 initialState JSON + 传入 EcuStateMachine |
| 7 | `Infrastructure/HIL/Odx/OdxToEcuScriptAdapter.cs` | MODIFY | Load 新增 out initialState + STATE-CHART 集成 + routine response 替换 |
| 8 | `Infrastructure/HIL/Odx/OdxEcuScriptImporter.cs` | MODIFY | 读取 out initialState + 输出 JSON |
| 9 | `Core.Tests/Uds/Odx/OdxStateChartExtractorTests.cs` | NEW | 5 个测试 |
| 10 | `Core.Tests/Uds/Odx/RequestBasedMappersRoutineResponseTests.cs` | NEW | 4 个测试 |
| 11 | `Infrastructure.Tests/HIL/Odx/OdxToEcuScriptAdapterStateChartTests.cs` | NEW | 4 个测试 |
| 12 | `Infrastructure.Tests/HIL/Odx/OdxToEcuScriptAdapterTests.cs` | MODIFY | 3 处 `Load` 加 `out _` |
| 13 | `Infrastructure.Tests/HIL/Odx/OdxToEcuScriptAdapterSecurityAccessTests.cs` | MODIFY | 3 处 |
| 14 | `Infrastructure.Tests/HIL/Odx/OdxToEcuScriptAdapterDidTests.cs` | MODIFY | 1 处 |
| 15 | `Infrastructure.Tests/HIL/Odx/OdxToEcuScriptAdapterRoutineTests.cs` | MODIFY | 2 处 |

### Sprint 19: HttpClient 工厂化 + Polly Retry

| # | 文件 | 操作 | 说明 |
|---|------|------|------|
| 1 | `Infrastructure/PeakCan.Host.Infrastructure.csproj` | MODIFY | 添加 `Microsoft.Extensions.Http.Polly`（自动传递 `Microsoft.Extensions.Http`） |
| 2 | `Infrastructure/HIL/Analysis/HilAnalysisService.cs` | MODIFY | 构造函数改为 `(HttpClient, ICredentialStore)`，`_ownsHttpClient` 始终 false（保留字段），Dispose 清空 |
| 3 | `Infrastructure/HIL/HeadlessHostBuilder.cs` | MODIFY | `AddHttpClient<IHilAnalysisService, HilAnalysisService>` + Polly policy + `using Polly;` + 注册 `ICredentialStore = new SimpleCredentialStore()` (**B2-R2 修复**) |
| 4 | `App/Composition/AppHostBuilder/AppServicesFlow.cs` | MODIFY | `AddHttpClient<IHilAnalysisService, HilAnalysisService>` + Polly policy（已有 `using Polly;`） |
| 5 | `Infrastructure.Tests/HIL/Analysis/HilAnalysisServiceRetryTests.cs` | NEW | 4 个测试：重试触发、重试次数、不重试取消、最终成功 |
| 6 | `Infrastructure.Tests/HIL/Analysis/Sprint14Tests.cs` | MODIFY | 更新构造函数调用（3 处，传入 mock HttpClient） |

**⚠️ 注册冲突避免（L5-R1 修复）**：Sprint 19 是 `IHilAnalysisService` 的**唯一**注册点。Sprint 16 不注册，避免 `AddSingleton` 覆盖 `AddHttpClient`。

---

## 5. Dependencies

```
Sprint 15 (CLI 报告) ──────────────────────> 独立
Sprint 16 (LLM 接线) ──> Sprint 19 (注册)    [ViewModel 构造函数需 IHilAnalysisService 在 DI 中存在]
Sprint 17 (Credential) ───────────────────> 独立
Sprint 18 (ODX 增强) ─────────────────────> 独立
Sprint 19 (HttpClient + Polly + 注册) ────> 独立
```

**关键约束**：Sprint 16（ViewModel 接线）依赖 Sprint 19（DI 注册），因为 `HilViewModel` 构造函数需要 `IHilAnalysisService` 在 DI 容器中可解析。

**推荐执行顺序**：15 → 17 → 18 → 19 → 16（先完成基础设施 + 注册，最后接线 LLM）

---

## 6. Risks

| Risk | Severity | Mitigation |
|------|----------|------------|
| Polly 引入新 NuGet 依赖 | LOW | `Microsoft.Extensions.Http.Polly` 是微软官方包，自动传递 `Microsoft.Extensions.Http` |
| STATE-CHART 解析格式差异 | MEDIUM | 降级到 `InitialState="default"` + `FromState=null`（向后兼容） |
| `ExtractResponseBytes` 可能解析不出字节 | LOW | 降级到 `[0x71, subFunc]` fallback |
| `HilAnalysisService` 构造函数变更破坏现有测试 | MEDIUM | Sprint14Tests 3 处 + 新增测试，全部传入 mock HttpClient |
| WPF `HilViewModel` 构造函数参数变更影响 9 处调用 | MEDIUM | 4 个测试文件已列于 Sprint 16 清单，DI 自动解析 |
| CLI 无法访问 WindowsCredentialManagerStore | MEDIUM | 文档说明限制，ChainedCredentialStore 只在 WPF 路径生效 |
| `TrendTracker.Load` 文件不存在时行为 | LOW | 确认 Load 返回空列表而非抛异常 |
| `GetRetryPolicy` 两处重复定义 | LOW | 代码 < 10 行，各自定义 private static，可接受 |
| console 模式进度+汇总输出交互 | LOW | 进度用 `\r` 行内刷新，汇总用 `\n` 换行，不冲突 |
| 单个 DIAG-SERVICE 关联多个 STATE-TRANSITION 只取第一个 | MEDIUM | 已知限制：SecurityAccess 在 UnlockedL1/Unlocked_L2 状态下 0x27 0x02 不匹配。可接受（Demo_Cdd 中 SecurityAccess chart 的 start state 是 Locked，首个 transition 覆盖 Locked→UnlockedL1） |

---

## 7. Out of Scope

- ODX 编辑/回写
- Multi-bus gateway
- Generator 热加载
- Web 报告 UI
- ECU 脚本语法高亮
- DeepSeekOptions 注入（hardcoded model 保持）
- ODX COMPU-METHOD 在模拟器中的完整物理值解码
- ECU 仿真模型形式化验证
- NEG-RESPONSE 解析

---

## 8. File Inventory

### Sprint 15 (CLI 报告接线)
| File | Action |
|------|--------|
| `Infrastructure/Cli/CliArgs.cs` | MODIFY |
| `PeakCan.Host.Cli/Program.cs` | MODIFY |
| `Infrastructure.Tests/Cli/Reporting/CliReportIntegrationTests.cs` | NEW |

### Sprint 16 (LLM 分析接线)
| File | Action |
|------|--------|
| `App/ViewModels/HilViewModel.cs` | MODIFY |
| `App/Views/HilView.xaml` | MODIFY |
| `App.Tests/ViewModels/HilViewModelTests.cs` | MODIFY (1 处) |
| `App.Tests/ViewModels/AppShellViewModelTests.cs` | MODIFY (6 处) |
| `App.Tests/ViewModels/AppShellViewModelMessageBoxPromptTests.cs` | MODIFY (1 处) |
| `App.Tests/Windows/UdsWindowTests.cs` | MODIFY (1 处) |
| `App.Tests/ViewModels/HilViewModelAnalysisTests.cs` | NEW |

**E1-R2 修复**：全部 4 个测试文件已列出（共 9 处 `new HilViewModel(...)` 调用）。

### Sprint 17 (Credential 统一)
| File | Action |
|------|--------|
| `Infrastructure/HIL/Analysis/ChainedCredentialStore.cs` | NEW |
| `App/Composition/AppHostBuilder/AppServicesFlow.cs` | MODIFY |
| `Infrastructure.Tests/HIL/Analysis/ChainedCredentialStoreTests.cs` | NEW |

### Sprint 18 (ODX 增强)
| File | Action |
|------|--------|
| `Core/Uds/Odx/OdxStateChartInfo.cs` | NEW |
| `Core/Uds/Odx/OdxStateChartExtractor.cs` | NEW |
| `Core/Uds/Odx/RequestBasedMappers.cs` | MODIFY (ReadServiceId/ReadSubfunctionParam 改 internal + 新增 ExtractRoutineResponses + ExtractResponseBytes) |
| `Infrastructure/HIL/EcuScript.cs` | MODIFY (新增 InitialState 字段) — **T1-R2 修复** |
| `Core/HIL/Contracts/EcuStateMachine.cs` | MODIFY (构造函数接受 initialState 参数) |
| `Infrastructure/HIL/EcuScriptLoader.cs` | MODIFY (读取 initialState JSON 字段 + 传入 EcuStateMachine) — **L3-R2 修复** |
| `Infrastructure/HIL/Odx/OdxToEcuScriptAdapter.cs` | MODIFY (Load 新增 out initialState + STATE-CHART 集成 + routine response 替换) |
| `Infrastructure/HIL/Odx/OdxEcuScriptImporter.cs` | MODIFY (读取 out initialState + 输出 JSON) — **L4-R2 修复** |
| `Core.Tests/Uds/Odx/OdxStateChartExtractorTests.cs` | NEW |
| `Core.Tests/Uds/Odx/RequestBasedMappersRoutineResponseTests.cs` | NEW |
| `Infrastructure.Tests/HIL/Odx/OdxToEcuScriptAdapterStateChartTests.cs` | NEW |
| `Infrastructure.Tests/HIL/Odx/OdxToEcuScriptAdapterTests.cs` | MODIFY（3 处 `Load` 加 `out _`）(**L2-R4**) |
| `Infrastructure.Tests/HIL/Odx/OdxToEcuScriptAdapterSecurityAccessTests.cs` | MODIFY（3 处）(**L2-R4**) |
| `Infrastructure.Tests/HIL/Odx/OdxToEcuScriptAdapterDidTests.cs` | MODIFY（1 处）(**L2-R4**) |
| `Infrastructure.Tests/HIL/Odx/OdxToEcuScriptAdapterRoutineTests.cs` | MODIFY（2 处）(**L2-R4**) |

### Sprint 19 (HttpClient + Polly)
| File | Action |
|------|--------|
| `Infrastructure/PeakCan.Host.Infrastructure.csproj` | MODIFY |
| `Infrastructure/HIL/Analysis/HilAnalysisService.cs` | MODIFY |
| `Infrastructure/HIL/HeadlessHostBuilder.cs` | MODIFY |
| `App/Composition/AppHostBuilder/AppServicesFlow.cs` | MODIFY |
| `Infrastructure.Tests/HIL/Analysis/HilAnalysisServiceRetryTests.cs` | NEW |
| `Infrastructure.Tests/HIL/Analysis/Sprint14Tests.cs` | MODIFY（3 处参数重排）(**L3-R4**) |

**L3-R4 修复：Sprint14Tests 调用代码变更**

```csharp
// 旧（当前）:
var service = new HilAnalysisService(credentialStore, httpClient);  // :96, :123
var service = new HilAnalysisService(credentialStore);             // :108

// 新（Sprint 19）:
var service = new HilAnalysisService(httpClient, credentialStore);  // 参数顺序反转
var service = new HilAnalysisService(httpClient, new SimpleCredentialStore());  // 需 2 个必需参数
```

---

## 9. Test Summary

| Sprint | Count | Notes |
|--------|-------|-------|
| 15 (CLI 报告) | 6 | CliReportIntegrationTests |
| 16 (LLM 接线) | 6 | HilViewModelAnalysisTests (新建) |
| 17 (Credential) | 5 | ChainedCredentialStoreTests |
| 18 (ODX 增强) | 13 | OdxStateChartExtractorTests (5) + RequestBasedMappersRoutineResponseTests (4) + OdxToEcuScriptAdapterStateChartTests (4) |
| 19 (HttpClient) | 4 | HilAnalysisServiceRetryTests |
| **Total** | **34** | |

---

## 10. Definition of Done

- [ ] `--format html` / `--format html+junit` / `--export-frames <dir>` 在 CLI 可用
- [ ] `ConsoleSummaryFormatter` 用于 console 格式输出
- [ ] `HilViewModel.AnalyzeAsync` 调用 `IHilAnalysisService` 并在 UI 显示结果
- [ ] `IHilAnalysisService` 通过 `AddHttpClient` 在 HeadlessHostBuilder 和 AppServicesFlow 中注册（Sprint 19 唯一注册点）
- [ ] `EnableAnalyze` 由 WPF UI CheckBox 控制（WPF 后置分析开关，不经过 CLI 路径）
- [ ] `ChainedCredentialStore` 让 WPF 路径可同时访问 Windows Credential Manager 和文件凭证
- [ ] OdxStateChartExtractor 正确解析 Demo_Cdd.odx-d 的 STATE-CHART
- [ ] 有 STATE-CHART 的 ODX 生成带 FromState/ToState 的 transition
- [ ] 无 STATE-CHART 的 ODX 保持全部 wildcard（向后兼容）
- [ ] Routine 响应从 ODX POS-RESPONSE chain 提取真实字节
- [ ] 无 POS-RESPONSE 时回退到 `[0x71, subFunc]`
- [ ] `HilAnalysisService` 使用 `IHttpClientFactory` + Polly retry（3 次指数退避）
- [ ] 用户取消（OperationCanceledException）不触发重试
- [ ] ~34 新测试通过
- [ ] 现有 HIL 测试全部通过（112+）

---

## 11. Review Traceability

### Phase 5 Deferred / Gap → Phase 6 Sprint

| Source Item | Sprint | Fix Location |
|-------------|--------|-------------|
| Phase 5 §7: ODX STATE-CHART | 18 | §3.4 - OdxStateChartExtractor + OdxToEcuScriptAdapter 修改 |
| Phase 5 §7: Polly retry | 19 | §3.6 - AddHttpClient + Polly policy |
| Phase 5 §7: Routine POS-RESPONSE | 18 | §3.5 - ExtractRoutineResponses + ExtractResponseBytes |
| Phase 5 §7: Credential Store 统一 | 17 | §3.3 - ChainedCredentialStore |
| Phase 5 gap: CLI 报告未接线 | 15 | §3.1 - CliArgs + Program.cs html/export-frames |
| Phase 5 gap: LLM 分析未接线 | 16+19 | §3.2 - HilViewModel 接线 (16) + AddHttpClient 注册 (19) |

### Adversarial Review Findings → Spec Fixes

| Finding | Severity | Fix Location |
|---------|----------|-------------|
| L1-R1: STATE-CHART DiagCommIds 恒为空 | CRITICAL | §3.4 — 重设计为 `DIAG-SERVICE → STATE-TRANSITION-REFS → STATE-TRANSITION → SOURCE/TARGET` 路径 |
| L2-R1: FromState="Locked" 不匹配初始状态 "default" | CRITICAL | §3.4 — EcuScript 新增 `InitialState`，EcuStateMachine 构造函数接受初始状态 |
| L3-R1: EnableAnalyze 在 ToCliArgs 中丢弃 | 降级 MEDIUM | §3.2 — 澄清 EnableAnalyze 是 WPF 后置动作，不经过 CLI |
| L4-R1: 构造函数参数顺序反转未覆盖所有调用点 | HIGH | Sprint 16 — 列出 4 文件 9 处调用 |
| L5-R1: AddSingleton 与 AddHttpClient 冲突 | HIGH | §5 + Sprint 16/19 — Sprint 16 不注册，Sprint 19 唯一注册点 |
| L6-R1: console 模式行为变更未说明 | MEDIUM | §3.1 — 注释说明 ConsoleProgress + ConsoleSummaryFormatter 输出交互 |
| B1-R1: ChainedCredentialStore.DeleteAsync 行为 | MEDIUM | §3.3 — 添加 try-catch 清理策略 |
| B2-R1: IsNullOrEmpty 判断空字符串 | LOW | §3.3 — 保持设计（合理行为） |
| B3-R1: Descendants 跨 STATE-CHART 边界 | LOW | §3.4 — Demo_Cdd 安全，风险低 |
| E1-R1: 缺少 Polly 包 + using | MEDIUM | §3.6 — 列出 using Polly; 依赖 |
| E2-R1: GetRetryPolicy 跨项目不可访问 | HIGH | §3.6 — 两处各自定义 private static |
| E3-R1: ApiEndpoint 绝对 URI 与 BaseAddress 冲突 | MEDIUM | §3.6 — 不设 BaseAddress，保持绝对 URI |
| E4-R1: App 层已有 Polly retry | MEDIUM | 接受 — 两套策略面向不同入口（TraceViewer vs HIL） |
| T1-R1: "wildcard" 术语混淆 | HIGH | §2.3 — 添加术语说明框 |
| T2-R1: CODED-VALUE 解析用 HexNumber | MEDIUM | §3.5 — 改为 NumberStyles.Integer |
| T3-R1: STATE-CHART 数量 16→15 | MEDIUM | §2.4 — 修正为 15（Session 6 + SecurityAccess 9） |
| T4-R1: HilViewModel 构造函数 9 处调用 | MEDIUM | Sprint 16 — 列出 4 文件 |
| T5-R1: 第一个 chart 名 "SessionControl"→"Session" | LOW | §2.4 — 修正为 "Session" |

### Adversarial Review Round 2 → Spec Fixes

| Finding | Severity | Fix Location |
|---------|----------|-------------|
| L1-R2: ReadServiceId 用 param.Value 非 CODED-VALUE 子元素 | CRITICAL | §3.4 — 删除 OdxStateChartExtractor 中的重复 ReadServiceId/ReadSubfunctionParam，改为调用 RequestBasedMappers internal 方法（B1-R2 修复） |
| L2-R2: Load() 返回类型与 spec 代码矛盾（IReadOnlyList vs EcuScript） | CRITICAL | §3.4 — Load 保持返回 `IReadOnlyList<EcuStateTransition>`，`initialState` 通过 `out` 参数返回 |
| L3-R2: EcuScriptLoader 未修改，InitialState 无法从 JSON 加载 | HIGH | §3.4 — 添加 EcuScriptLoader 修改说明（读取 initialState JSON 字段 + 传入 EcuStateMachine） |
| L4-R2: OdxEcuScriptImporter 需要输出 initialState 但无修改代码 | HIGH | §3.4 — 添加 OdxEcuScriptImporter 修改代码（读取 out initialState + 输出 JSON） |
| L5-R2: ReadSubfunctionParam 返回类型 byte? vs byte 不一致 | MEDIUM | §3.4 — 删除 spec 中的 ReadSubfunctionParam 定义，复用 RequestBasedMappers（返回 byte） |
| B1-R2: ReadServiceId/ReadSubfunctionParam 代码重复 | HIGH | §3.4 — RequestBasedMappers 方法改为 internal，OdxStateChartExtractor 直接调用 |
| B2-R2: AddHttpClient 的 ICredentialStore 依赖未注册 | MEDIUM | Sprint 19 — HeadlessHostBuilder 注册 `ICredentialStore = new SimpleCredentialStore()` |
| E1-R2: §8 Sprint 16 File Inventory 遗漏 3 个测试文件 | HIGH | §8 — 补全 4 个测试文件（共 9 处调用） |
| E2-R2: AddHttpClient 需要 Microsoft.Extensions.Http 包 | LOW | Sprint 19 — `Microsoft.Extensions.Http.Polly` 自动传递 `Microsoft.Extensions.Http` |
| T1-R2: EcuScript.cs 路径错误（Core→Infrastructure） | HIGH | §3.4 + §8 — 修正为 `Infrastructure/HIL/EcuScript.cs` |
| T2-R2: StateChartTransition vs OdxStateTransition 命名不一致 | MEDIUM | §4 — 统一为 StateChartTransition |
| T3-R2: `t.SubFunction ?? 0` 可能误匹配 | LOW | §3.4 — 使用 ServiceRequest 记录 + 仅匹配有 SubFunction 的 transition |

### Adversarial Review Round 3 → Spec Fixes

| Finding | Severity | Fix Location |
|---------|----------|-------------|
| L1-R3: EcuStateMachine 构造函数移除 `? = null` 破坏 12+ 处调用 | CRITICAL | §3.4 — 保持 `generators` 可选：`IEnumerable<IEcuResponseGenerator>? generators = null` |
| L2-R3: ParseStateMachine 签名未修改但内部引用 initialState | HIGH | §3.4 — ParseStateMachine 新增 `string initialState` 参数 |
| L3-R3: `GetProperty("initialState")` 对旧 JSON 抛 KeyNotFoundException | HIGH | §3.4 — 改用 `TryGetProperty` 兼容旧 JSON |
| L4-R3: OdxStateChartExtractor.BuildDiagServiceToRequestMap 是死代码 | HIGH | §3.4 — 删除此方法，OdxToEcuScriptAdapter 的 private 版本负责关联 |
| L5-R3: 单个 DIAG-SERVICE 关联多个 STATE-TRANSITION 只取第一个 | MEDIUM | §6 Risks — 标注此限制（SecurityAccess 在 UnlockedL1/Unlocked_L2 状态行为受限） |
| B1-R3: SetAsync/DeleteAsync 不对称 | LOW | §3.3 — 文档说明（可接受设计） |
| E1-R3: GetRetryPolicy 在 partial 类中可能冲突 | LOW | 当前无冲突，风险低 |
| T1-R3: spec 用 `root` 实际变量名是 `element` | MEDIUM | §3.4 — 修正为 `element.TryGetProperty(...)` |
| T2-R3: `t.SubFunction is { } sub` pattern matching 正确 | LOW | 已验证：byte? 匹配 is { } 时 sub 为 byte，正确 |

### Adversarial Review Round 4 → Spec Fixes

| Finding | Severity | Fix Location |
|---------|----------|-------------|
| L1-R4: `Reset()` 硬编码 "default" 不使用 InitialState | CRITICAL | §3.4 — 新增 `_initialState` 字段，`Reset()` 恢复到 `_initialState` |
| L2-R4: `Load` 签名变更破坏 9 处测试调用 | HIGH | §8 Sprint 18 — 列出 4 个测试文件共 9 处 `Load` 需加 `out _` |
| L3-R4: HilAnalysisService 构造函数参数顺序反转破坏 Sprint14Tests 3 处调用 | HIGH | Sprint 19 — 展示修改后的调用代码 |
| L4-R4: `_lastResult` 变化后 AnalyzeCommand.CanExecute 未通知 UI | MEDIUM | §3.2 — 赋值后调用 `AnalyzeCommand.NotifyCanExecuteChanged()` |
| B1-R4: SetAsync 只写 stores[0]，WCM 写入失败不回退 | LOW | §6 Risks — 可接受（异常传播给用户） |
| E1-R4: SimpleCredentialStore 实例内存非线程安全 | LOW | 当前 HIL 分析不并发，风险低 |

### Adversarial Review Round 5 → Spec Fixes

| Finding | Severity | Fix Location |
|---------|----------|-------------|
| L1-R5: Sprint 19 说"移除 _ownsHttpClient"但代码保留 | HIGH | §3.6 + Sprint 19 — 统一为"保留字段，始终赋 false" |
| L2-R5: §3.4 文字说"签名保持不变"但代码显示签名已变 | MEDIUM | §3.4 — 修正文字为"新增 out 参数" |
| L3-R5: Program.cs switch 插入位置和 return 未说明 | MEDIUM | §3.1 — 注释说明替换 if 块、return 在 switch 之后 |
| L4-R5: Sprint 18 表格遗漏 8 个 MODIFY 项 | MEDIUM | §4 Sprint 18 — 补全 15 项完整清单 |
