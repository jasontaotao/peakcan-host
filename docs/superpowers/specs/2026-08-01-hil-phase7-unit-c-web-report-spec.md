# HIL Phase 7 (Unit C): Web 报告 UI — WPF 内嵌 WebView2 报告查看

> Spec date: 2026-08-01
> Depends: Phase 6 (commit `579b02e`) + 单元 A (`e427acf`) + 单元 B (`a1e1ede`)
> Scope: **WPF HilView 内嵌 WebView2 就地渲染 HIL 报告 + 自动落盘 + 一键浏览器打开**。单元 C 是 Phase 7 四个独立单元
> 的第三个（A=DeepSeekOptions 接线 ✅，B=Generator 热加载 ✅，C=Web 报告 UI，D=Multi-bus gateway 后续）。
>
> **Revision 2（2026-08-01）**：按 spec review 修正 —— L1 ctor 改动波及 8 处测试调用 + File Inventory 补测试文件；
> L2/T1 `HilReportService` 接口化 `IHilReportService`（NSubstitute 无法 mock 非虚成员）；L3 报告插入点精确到
> `StatusMessage`(:200) 之后、`AnalyzeAsync`(:205) 之前；B1/T2 给出 DockPanel 外层完整 XAML 方案（不引入无效的
> `Grid.Row`）；B2 WebView 改 `Navigate(fileUri)` 绕开 `NavigateToString` 2MB 限制；B3 `Unloaded` 退订 `PropertyChanged`；
> B4 文件名加毫秒精度；E1 WebView2 Runtime 假设修正（不声称 Win11 自带）；E2 TrendTracker Mutex 措辞诚实化；
> T3 LLM Analysis TextBox 决定"保留原位置"；T4 新增 `ShowReportError` 解决 fallback 初始空白；T5 命名空间改
> `Infrastructure/HIL/Reporting`。
>
> **Revision 3（2026-08-01）**：按 spec review 修正 R1-R4 —— R1 HTML Report tab 的 WebView2 用 Grid 叠放
> 保证填充剩余空间（DockPanel `LastChildFill` 默认让 last-child 填充，Border/WebView2 会坍缩）；R2 `HilViewModel.cs`
> 补 `using System.Diagnostics;`（`Process`/`ProcessStartInfo`）；R3 `HilReportService.cs` 补
> `using PeakCan.Host.Infrastructure.Cli.Reporting;`（`HtmlReportGenerator`/`TrendTracker`/`TrendEntry`）；
> R4 `DateTime.UtcNow` 单次捕获，文件名与趋势时间戳一致。

---

## 1. Goals

Phase 5/6 交付了 `HtmlReportGenerator`（单文件自包含 HTML 报告）并完成 CLI 接线（`--format html` /
`html+junit`，`Program.cs:92-99`），但 **WPF 面板看不到 HTML 报告**：

- `HilViewModel.RunAsync` 运行完成后只填充 `Results`（DataGrid）和 `ResultsTree`（TreeView），
  无 HTML 报告视图（`HilViewModel.cs:190-206`）。
- HTML 报告只在 CLI 场景通过 `--format html` 落盘到 CWD；WPF 场景无任何报告出口。

本单元目标：

**C1. WPF 内嵌查看** — HilView 加 WebView2 区域，HIL 运行完成后就地渲染
`HtmlReportGenerator.GenerateHtml` 的输出（复用现有报告生成逻辑，零新增生成代码）。

**C2. 报告自动落盘** — 每次运行自动把 HTML 报告保存到固定报告目录
（`%LocalAppData%\PeakCanHost\hil-reports\`），供留存/分享/历史追溯。

**C3. 一键浏览器打开** — 面板提供 "Open in Browser" 命令，用系统浏览器打开最新报告文件。

---

## 2. Current State

### 2.1 证据

| 项 | 证据 |
|----|------|
| HTML 报告生成 | `HtmlReportGenerator.GenerateHtml(TestSuiteResult, IReadOnlyList<TrendEntry>?)` → string，单文件内嵌 CSS+JS（`Infrastructure/Cli/Reporting/HtmlReportGenerator.cs:22`） |
| 趋势追踪 | `TrendTracker.Record(entry, path?, maxEntries)` / `Load(path?)`，`path` 参数可传绝对路径脱离 CWD（`Infrastructure/Cli/Reporting/TrendTracker.cs:25,61`）；**Mutex 名固定** `Global\hil-trends-mutex`（`TrendTracker.cs:18`，不随 path 变化） |
| CLI 报告接线模式 | `Program.cs:92-99`：`TrendTracker.Load → GenerateHtml → File.WriteAllText → TrendTracker.Record` |
| WPF 运行入口 | `HilViewModel.RunAsync`（`HilViewModel.cs:165-218`），`_lastResult = result` 在 `:190`，`Results`/`ResultsTree` 填充在 `:193-196`，`StatusMessage` 在 `:198-200`，Phase 7 A 的 `if (EnableAnalyze && FailedCases>0) await AnalyzeAsync()` 在 `:205-206` |
| WPF HIL 面板布局 | `HilView.xaml` **外层是 `<DockPanel Margin="16">`（`:13`）不是 Grid**；中部 `<Grid>`（`:67-102`）= DataGrid(`:73`) + TreeView(`:82`)；底部 LLM Analysis TextBox 是 `DockPanel.Dock="Bottom"`（`:105-108`） |
| HilView code-behind | `HilView.xaml.cs` 仅 `InitializeComponent()`，无逻辑 |
| WebView2 依赖 | `Microsoft.Web.WebView2` 已在 `PeakCan.Host.App.csproj`（用于 ScriptView CodeMirror）；ScriptView 有完整集成 + try/catch fallback + 不 dispose 的泄漏防护模式（`ScriptView.xaml.cs`） |
| service 接口惯例 | `IHilAnalysisService` 在 `Core/HIL/Analysis/IHilAnalysisService.cs:7`；`HilViewModel` ctor 注入 `IHilRunnerService` / `ILogger` / `IFileDialogService` / `IHilAnalysisService`（`HilViewModel.cs:50`），**全部是接口** |
| HilViewModel 测试调用点 | 8 处 `new HilViewModel(...)`：`AppShellViewModelTests.cs:147,476,573,711,1012,1112`、`AppShellViewModelMessageBoxPromptTests.cs:176`、`UdsWindowTests.cs:105`；另有 helper `HilViewModelTests.cs:30`、`HilViewModelAnalysisTests.cs:27` |
| DI 注册 | `AppHostBuilder.cs:302` `HilRunnerService`、`:303` `HilViewModel`（Transient）、`:351` 使用 |

### 2.2 现状结论

- 报告生成逻辑完备（`HtmlReportGenerator` + `TrendTracker`），WPF 侧缺失的只是**消费出口**。
- WebView2 集成模式已由 ScriptView 生产验证（`EnsureCoreWebView2Async` + `NavigateToString` + try/catch
  fallback + 不 dispose）。**注意：项目 target 是 `net10.0-windows`，不限定 Win11；Windows 10 可能缺 WebView2
  Evergreen Runtime** —— code-behind 的 try/catch + fallback + "Open in Browser" 按钮是必要的兜底，
  不依赖"Win11 自带"假设。
- `TrendTracker` 默认 `./hil-trends.json` 是 CWD 语义（CLI 适用）；WPF 运行目录是 `bin/`，必须传绝对路径到报告目录。
- **`HilViewModel` ctor 加参必然打破 8 处测试调用**，File Inventory 必须包含这些测试文件的 MODIFY。

---

## 3. Design

### 3.1 报告目录策略

WPF 应用运行目录是 `bin/Debug/net10.0-windows/`，写 CWD 不合理（会被构建清理、路径不稳定）。
统一用 **`%LocalAppData%\PeakCanHost\hil-reports\`**：

```
%LocalAppData%\PeakCanHost\hil-reports\
├── hil-report-{yyyyMMddHHmmssfff}.html   # 每次运行一份（自动落盘，C2；毫秒精度防同秒覆盖，B4）
├── hil-trends.json                       # 趋势历史（TrendTracker 的 path 参数指向这里）
```

- `Directory.CreateDirectory` 幂等。
- 报告文件带 UTC 时间戳 + **毫秒**（`yyyyMMddHHmmssfff`），避免同秒内多次 Run 覆盖（B4）。
- `hil-trends.json` 一个固定文件。**趋势 Mutex 说明（E2）**：`TrendTracker` 的 Mutex 名是全局固定的
  `Global\hil-trends-mutex`（`TrendTracker.cs:18`），与 path 无关 —— CLI（CWD）与 WPF（报告目录）写**不同**
  趋势文件但争同一把锁，最多 `MutexTimeout`(5s) 串行等待，不损坏数据，但并非"互不干扰"。可接受：
  两个场景并发概率低，且锁超时后照常读写。不在本单元改 `TrendTracker`（避免 scope 膨胀）。

### 3.2 `IHilReportService` + `HilReportService`（新建，`Infrastructure/HIL/Reporting/`）

**接口化（L2/T1）**：`HilViewModel` 的 service 依赖全部是接口（§2.1 证据），且 NSubstitute 无法 mock
非虚成员 —— 定义接口，VM 注入接口，DI 注册接口，测试 mock 接口：

```csharp
namespace PeakCan.Host.Infrastructure.HIL.Reporting;

/// <summary>一次 HIL 运行生成的报告产物。</summary>
public sealed record HilReportResult(string Html, string FilePath);

/// <summary>HIL HTML 报告生成服务（WPF 面板消费出口）。</summary>
public interface IHilReportService
{
    /// <summary>生成 HTML 报告并落盘到报告目录，返回 HTML 内容 + 文件路径。</summary>
    HilReportResult Generate(TestSuiteResult result);
}
```

```csharp
// 文件顶部 using（R3）：HtmlReportGenerator / TrendTracker / TrendEntry 在 Cli/Reporting 命名空间
using PeakCan.Host.Infrastructure.Cli.Reporting;

/// <summary>
/// 为 WPF 面板生成并落盘 HIL HTML 报告。复用 HtmlReportGenerator + TrendTracker，
/// 报告目录固定为 %LocalAppData%\PeakCanHost\hil-reports\（脱离 CLI 的 CWD 语义）。
/// </summary>
public sealed class HilReportService : IHilReportService
{
    public string ReportDirectory { get; }

    public HilReportService(string? reportDirectory = null)
    {
        ReportDirectory = reportDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "PeakCanHost", "hil-reports");
    }

    public HilReportResult Generate(TestSuiteResult result)
    {
        Directory.CreateDirectory(ReportDirectory);
        var trendsPath = Path.Combine(ReportDirectory, "hil-trends.json");
        var trends = TrendTracker.Load(trendsPath);
        var html = HtmlReportGenerator.GenerateHtml(result, trends);
        var now = DateTime.UtcNow;  // R4：单次捕获，文件名与趋势时间戳一致
        var filePath = Path.Combine(ReportDirectory, $"hil-report-{now:yyyyMMddHHmmssfff}.html");  // B4
        File.WriteAllText(filePath, html);
        TrendTracker.Record(
            new TrendEntry(now, result.SuiteName,
                result.TotalCases, result.PassedCases, result.FailedCases, (int)result.ElapsedMs),
            trendsPath);
        return new HilReportResult(html, filePath);
    }
}
```

- 命名空间 `PeakCan.Host.Infrastructure.HIL.Reporting`（T5）：`HtmlReportGenerator`/`TrendTracker` 留在
  `Cli/Reporting`（CLI 报告格式），本服务是 WPF 消费出口，放 HIL 域下语义正确。跨命名空间 `using` 即可。
- 构造参数 `reportDirectory` 可注入（测试传临时目录，不污染 `%LocalAppData%`），生产走默认路径。

### 3.3 `HilViewModel` 扩展（`App/ViewModels/HilViewModel.cs`）

**ctor（L1）**：追加 `IHilReportService reportService` 参数（第 5 个，`:50`）。文件顶部需新增
`using System.Diagnostics;`（R2，`Process`/`ProcessStartInfo`）。

```csharp
public HilViewModel(IHilRunnerService runner, ILogger<HilViewModel> logger,
    IFileDialogService fileDialog, IHilAnalysisService analysisService,
    IHilReportService reportService)
```

新增属性（CommunityToolkit `[ObservableProperty]`）：

| 属性 | 初始值 | 用途 |
|------|--------|------|
| `LatestReportPath` (string) | `""` | 最新报告文件路径（WebView 导航源 + Open in Browser）（C1/C3） |
| `ShowReportError` (bool) | `false` | fallback TextBlock 可见性（仅"生成过且失败"才显示，T4） |
| `ReportError` (string) | `""` | 报告生成失败信息（生成异常 / WebView2 runtime 缺失） |

**不暴露 `ReportHtml`**：WebView 直接导航到落盘文件（`Navigate(fileUri)`，见 §3.4 B2），HTML 内容由
`IHilReportService.Generate` 返回给测试断言，VM 只持文件路径。

**`RunAsync` 插入点（L3，精确位置）**：在 `StatusMessage` 赋值（`:200`）之后、Phase 7 A 的
`if (EnableAnalyze && result.FailedCases > 0) await AnalyzeAsync();`（`:205`）**之前**。

理由：报告生成是秒级本地 IO，先落盘让 UI 立即有报告；`AnalyzeAsync` 是 LLM 网络调用（最长 ~150s 超时，
`HilViewModel.cs:142`），若报告在 Analyze 之后生成会被 LLM 阻塞。当前 `RunAsync` 的 `:188-206` 代码：

```csharp
var result = await _runner.RunAsync(request, progress, default);   // :188
_lastResult = result;                                              // :190
AnalyzeCommand.NotifyCanExecuteChanged();                          // :191
foreach (var cr in result.CaseResults) Results.Add(...);           // :193-194
BuildResultsTree(result);                                          // :196
StatusMessage = result.AllPassed                                   // :198-200
    ? $"All {result.TotalCases} cases passed"
    : $"{result.FailedCases}/{result.TotalCases} cases failed";
// ↓ 新插入：报告生成（L3：在 :200 后、:205 前）
try
{
    var report = _reportService.Generate(result);
    LatestReportPath = report.FilePath;
    ShowReportError = false;
    ReportError = "";
}
catch (Exception ex)
{
    _logger.LogError(ex, "HIL report generation failed");
    ReportError = ex.Message;
    ShowReportError = true;
}
// ↑ 插入结束
if (EnableAnalyze && result.FailedCases > 0)                       // :205
    await AnalyzeAsync();                                          // :206
```

- 报告生成失败**不阻断**测试结果展示（try/catch 隔离，`ShowReportError=true` → UI 显示错误而非崩溃）。
- 测试结果 `Results`/`ResultsTree`/`StatusMessage` 先填，报告后生成 —— 报告失败不影响主视图。

新命令：

```csharp
[RelayCommand]
private void OpenReport()
{
    if (string.IsNullOrEmpty(LatestReportPath) || !File.Exists(LatestReportPath)) return;
    Process.Start(new ProcessStartInfo(LatestReportPath) { UseShellExecute = true });
}
```

### 3.4 `HilView.xaml` + `HilView.xaml.cs` 改造

**XAML（B1/T2 完整方案）**：外层保持 `<DockPanel Margin="16">`（`:13`）**不变** —— 顶层 `DockPanel.Dock`
区域（Mode selector / Paths / ECU editor / ProgressBar / StatusMessage / LLM Analysis TextBox）全部原样保留。
**只替换中部填充区的 `<Grid>`（`:67-102`）为 `<TabControl>`**（TabControl 作为 DockPanel 的 last-child 占满剩余空间）：

```xml
<!-- 替换原有 :67-102 的 <Grid>（DataGrid + TreeView） -->
<TabControl>
    <TabItem Header="Results">
        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="2*" />
            </Grid.ColumnDefinitions>
            <!-- 原 DataGrid（:73-80）原样 -->
            <DataGrid Grid.Column="0" ItemsSource="{Binding Results}" ... />
            <!-- 原 TreeView（:82-101）原样 -->
            <TreeView Grid.Column="1" ItemsSource="{Binding ResultsTree}">...</TreeView>
        </Grid>
    </TabItem>

    <TabItem Header="HTML Report">
        <DockPanel>
            <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,0,0,4">
                <TextBlock Text="{Binding LatestReportPath}" VerticalAlignment="Center"
                           Foreground="Gray" Margin="0,0,8,0" TextTrimming="CharacterEllipsis" />
                <Button Content="Open in Browser" Command="{Binding OpenReportCommand}"
                        HorizontalAlignment="Right" Padding="8,2" />
            </StackPanel>
            <!-- R1：WebView2 与 fallback 用 Grid 叠放 —— Grid 是 DockPanel last-child，占满剩余空间；
                 Border(WebView2) 填满 Grid，fallback TextBlock 覆盖其上（默认 ZIndex 更高，仅
                 ShowReportError=true 时可见）。不能把 TextBlock 放 Grid 外当 last-child（DockPanel
                 LastChildFill 会让它填充，Border 坍缩）。 -->
            <Grid>
                <Border BorderBrush="Gray" BorderThickness="1" CornerRadius="4">
                    <wv2:WebView2 x:Name="ReportWebView" />
                </Border>
                <!-- T4 fallback：仅 ShowReportError=true 时可见（避免初始空白） -->
                <TextBlock Text="{Binding ReportError}" Foreground="DarkRed" Padding="12" TextWrapping="Wrap"
                           VerticalAlignment="Center" HorizontalAlignment="Center">
                    <TextBlock.Style>
                        <Style TargetType="TextBlock">
                            <Setter Property="Visibility" Value="Collapsed" />
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding ShowReportError}" Value="True">
                                    <Setter Property="Visibility" Value="Visible" />
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </TextBlock.Style>
                </TextBlock>
            </Grid>
        </DockPanel>
    </TabItem>
</TabControl>
```

- **LLM Analysis TextBox（T3 决策）**：**保留原位置**（`DockPanel.Dock="Bottom"`，`:105-108`），不做并入 Results tab。
  理由：最小改动，不改变现有 dock 结构；Results tab 只承载测试结果树。
- XAML 顶部加 `xmlns:wv2="clr-namespace:Microsoft.Web.WebView2.Wpf;assembly=Microsoft.Web.WebView2.Wpf"`
  （同 `ScriptView.xaml:6`）。

**code-behind（B2/B3/E1）**：

```csharp
using System.ComponentModel;
using Microsoft.Web.WebView2.Core;

public partial class HilView : UserControl
{
    private HilViewModel? _vm;
    private bool _isLoaded;

    public HilView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm = DataContext as HilViewModel;
        if (_vm is null) return;
        _isLoaded = true;

        // B3：退订在 OnUnloaded，避免 tab 切换累积重复订阅
        _vm.PropertyChanged += OnVmPropertyChanged;

        try
        {
            await ReportWebView.EnsureCoreWebView2Async();
            if (!_isLoaded || _vm is null) return;
            // B2：Navigate 到落盘文件（file:/// URI），绕开 NavigateToString 的 ~2MB 上限；
            // 报告已由 HilReportService 落盘（§3.2），文件路径是唯一事实源。
            if (!string.IsNullOrEmpty(_vm.LatestReportPath))
                NavigateToReport(_vm.LatestReportPath);
        }
        catch (Exception ex)
        {
            if (!_isLoaded) return;
            _vm.ReportError = $"WebView2 runtime 未安装或损坏: {ex.Message}. 请安装 WebView2 Evergreen Runtime.";
            _vm.ShowReportError = true;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;   // B3：防累积订阅
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HilViewModel.LatestReportPath)
            && !string.IsNullOrEmpty(_vm?.LatestReportPath))
            NavigateToReport(_vm.LatestReportPath);
    }

    private void NavigateToReport(string filePath)
    {
        if (ReportWebView.CoreWebView2 is null) return;
        // file:///C:/... 形式（Path 转 URI）
        ReportWebView.CoreWebView2.Navigate(new Uri(filePath).AbsoluteUri);
    }
}
```

- **不 dispose WebView2**（`ScriptView.xaml.cs:33-54` v2.0.7 教训：tab 切换 dispose 会破坏进程级
  `CoreWebView2Environment` 缓存，二次创建报 "runtime not installed or corrupted"）。只退订事件。
- **B2 依据**：`CoreWebView2.NavigateToString` 文档上限 ~2MB（自包含 HTML + 步骤表 + 帧转储容易超）。
  报告已落盘，`Navigate(fileUri)` 无大小限制且与落盘文件单一事实源一致。
- **E1 兜底**：`EnsureCoreWebView2Async` try/catch → `ShowReportError` 提示安装 Runtime；
  即便 WebView 不可用，"Open in Browser" 按钮仍可打开报告文件（系统浏览器，不依赖 WebView2）。

### 3.5 DI 注册（`App/Composition/AppHostBuilder.cs`）

`:302` 附近追加：

```csharp
builder.Services.AddSingleton<Infrastructure.HIL.Reporting.IHilReportService,
    Infrastructure.HIL.Reporting.HilReportService>();
```

（HilViewModel 是 Transient，`AppHostBuilder.cs:303`；报告服务无状态，可单例。）

---

## 4. File Inventory

| 文件 | 动作 |
|------|------|
| `src/PeakCan.Host.Infrastructure/HIL/Reporting/IHilReportService.cs` | NEW — 接口 + `HilReportResult` record（§3.2） |
| `src/PeakCan.Host.Infrastructure/HIL/Reporting/HilReportService.cs` | NEW — 实现；顶部 `using PeakCan.Host.Infrastructure.Cli.Reporting;`（§3.2，R3） |
| `src/PeakCan.Host.App/ViewModels/HilViewModel.cs` | MODIFY — ctor + 属性 + RunAsync 插入点 + OpenReportCommand；新增 `using System.Diagnostics;`（§3.3，R2） |
| `src/PeakCan.Host.App/Views/HilView.xaml` | MODIFY — TabControl(Results / HTML Report) + WebView2 + fallback（§3.4） |
| `src/PeakCan.Host.App/Views/HilView.xaml.cs` | MODIFY — WebView2 初始化 + PropertyChanged→Navigate + 退订（§3.4） |
| `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` | MODIFY — 注册 IHilReportService（§3.5） |
| `tests/PeakCan.Host.Infrastructure.Tests/HIL/Reporting/HilReportServiceTests.cs` | NEW |
| `tests/PeakCan.Host.App.Tests/ViewModels/HilViewModelReportTests.cs` | NEW |
| `tests/PeakCan.Host.App.Tests/ViewModels/HilViewModelTests.cs` | MODIFY — helper `:30` 加 mock `IHilReportService` 参数 |
| `tests/PeakCan.Host.App.Tests/ViewModels/HilViewModelAnalysisTests.cs` | MODIFY — helper `:27` 加 mock 参数 |
| `tests/PeakCan.Host.App.Tests/ViewModels/AppShellViewModelTests.cs` | MODIFY — `:147,476,573,711,1012,1112` 加 mock 参数 |
| `tests/PeakCan.Host.App.Tests/ViewModels/AppShellViewModelMessageBoxPromptTests.cs` | MODIFY — `:176` 加 mock 参数 |
| `tests/PeakCan.Host.App.Tests/Windows/UdsWindowTests.cs` | MODIFY — `:105` 加 mock 参数 |

> **L1 关键约束**：`HilViewModel` ctor 第 5 参是**必填** `IHilReportService`（无默认值，避免静默丢失 DI 依赖）。
> 8 处 `new HilViewModel(...)` + 2 处 helper 全部加 `Substitute.For<IHilReportService>()` mock，否则测试项目
> CS7036 编译失败。这是 ctor 签名变更的必改清单，缺失任一处 build 即红。

---

## 5. Testing (TDD)

**`HilReportService`（真实实现 + 临时目录注入）**：

| 用例 | 断言 |
|------|------|
| Service 生成 HTML | `Generate(固定 TestSuiteResult)` → `Html` 非空且含 summary 卡片标记、`FilePath` 指向报告目录 |
| Service 落盘 | `Generate` 后 `File.Exists(FilePath)`；文件含 `<!DOCTYPE html>` |
| Service 目录创建 | 报告目录不存在时 `Generate` 自动 `Directory.CreateDirectory` |
| Service 文件名毫秒唯一（B4） | 连续两次 `Generate` → 两个 `FilePath` 不同（毫秒精度） |
| Service 趋势记录 | `Generate` 后 `TrendTracker.Load(trendsPath)` 比之前多一条、字段匹配 result |
| Service 自定义目录 | 构造传临时目录 → 文件写到该目录（不污染 `%LocalAppData%`） |

**`HilViewModel`（mock `IHilReportService`，L2/T1：`Substitute.For<IHilReportService>()`）**：

| 用例 | 断言 |
|------|------|
| RunAsync 后报告填充 | mock service 返回固定 `HilReportResult` → RunAsync 后 `LatestReportPath` 非空、`ShowReportError=false`、`ReportError=""` |
| 生成失败降级 | mock service `Generate` 抛异常 → `ShowReportError=true`、`ReportError` 非空、`Results` 仍填充、不抛 |
| OpenReport 无路径 | `LatestReportPath=""` → `OpenReportCommand` 可执行但 no-op 不抛 |
| ctor 参数存在 | `new HilViewModel(r, log, fd, a, mockReportService)` 构造成功（编译期保证，8 处调用点验证） |

- WebView2 控件本身不单测（code-behind 薄层 + 依赖 Win 环境）；`HilViewModel` 报告逻辑用 mock 接口测。
- `HilReportService.Generate` 是同步方法（HTML 生成 + 落盘都是同步 IO），不需 async；`WebView2.Navigate` 是同步导航。
- 现有 8 处 `new HilViewModel(...)` 测试调用点的编译通过 = ctor 改动闭环验证。

---

## 6. Out of Scope

- **Multi-bus gateway（Phase 7 单元 D）** — 后续独立 spec
- **WPF 历史报告列表/多份报告切换** — 本轮只展示"最新一次"，历史文件留存于目录但 UI 不管理（后续可加）
- **报告 UI 主题/图表增强** — 复用 `HtmlReportGenerator` 现有样式，不改生成逻辑
- **CLI 报告路径改造** — CLI 保持 CWD 语义不变（`Program.cs` 不改）；本单元只新增 WPF 侧出口
- **`TrendTracker` Mutex 按 path 区分** — E2 只改措辞说明，不改 Mutex 名（避免影响 CLI 既有行为）
