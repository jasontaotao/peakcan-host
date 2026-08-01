# HIL View 增强: Browse 占位符 + Mode 图标 + 独立 ECU 编辑器

> Spec date: 2026-08-01
> Scope: **HIL view（`HilView.xaml`）三个 UX 增强**：① 5 个 Browse 字段中文占位符；② Mode 选择器随 `SelectedMode` 切换的 emoji 图标 + ToolTip 功能说明 + 下拉框图标；③ 把内嵌 60px ECU 脚本 JSON 编辑器**独立成窗口编辑器工具**（HIL view 移除内嵌、加 "Open ECU Editor" 按钮、保存回填 `EcuScriptPath`）。
> Constraints: 全部**零第三方依赖**；`TreatWarningsAsErrors` + `EnforceCodeStyleInBuild` 必须零警告；**裸 `catch (Exception)` 是项目惯例（CA1031 未启用，证据 §2.1）**。
>
> **Revision 2（2026-08-01）**：按 R1 review 修正 19 点 —— L1/B4 Reset 清 EditorText+FilePath（关窗=会话结束）；L2/L4 CanRun 已检查 EcuScriptPath（:288）；L3 编辑器 FilePath 锚定 + 标题显示；L5 SaveAs 取消无操作；B1 initialDirectory null 保护；B2/T2 LoadInitialPath 用 IsNullOrEmpty；B3 TextBox UpdateSourceTrigger=PropertyChanged；B5 窗口设 Owner；D1 Format 用 doc.RootElement；D2 catch 4 类（源码核实）；D3/D4 ShowWindow(factory)/initialDirectory 可空确认；T1 转换器只注册 App.xaml；T3 转换器 null-safe unboxing；T4 窗口 XAML 骨架；T5 ctor 插位。
>
> **Revision 3（2026-08-01）**：按 R2 review 修正 11 点 ——
> **L1-R2**：回填加 `IsValidEcuScript` 门禁——`EcuScriptPath` **只在 `IsValidEcuScript==true` 时回填**（Open 加载非法 JSON → 内容仍显示 + ErrorMessage 警告，但**不回填**，堵住"Open 旁路不经校验写 EcuScriptPath"）。
> **L2-R2**：脏跟踪 `_savedText` + `HasUnsavedChanges`；`Open` 与 `LoadExternal` 前若有未保存修改 → `IMessageBoxPrompt.ShowAsync` 确认（cancelled → 无操作），消除"Open 覆盖未保存编辑无确认"。
> **L3-R2**：`BrowseEcu` 设 `EcuScriptPath` 后 raise `EcuScriptPathSetExternally` 事件 → AppShell → 编辑器 `LoadExternalAsync`（复用脏确认）→ HIL TextBox / 编辑器 FilePath / WindowTitle 三方一致；"最后写入胜"由 HIL `EcuScriptPath` TextBox 实时绑定作为可见反馈。
> **B1-R2**：证伪（CA1031 未启用，裸 `catch (Exception)` 是项目惯例，证据 `Program.cs:206`/`App.xaml.cs:98`/`HilView.xaml.cs:49` 等）；但仍统一 Open/LoadInitialPath 读文件到共享 `TryReadFile`。
> **B2-R2**：`TryValidate` 加**防御兜底** `catch (Exception ex)`（`EcuScriptLoader.Parse` 是 public API，未来新增异常不逃逸）。
> **D1-R2**：`Open` 与 `LoadInitialPath` 统一走共享 `TryReadFile`，消除捕获范围不一致。
> **T1-R2**：`xmlns:conv` 表述修正——必加（功能 2 局部注册 `HilMode` 转换器需要 `conv:` 前缀）。
> **T2-R2**：`WindowTitle` 用 `partial void OnFilePathChanged` 自动派生，删 4 个手动同步点（不可遗漏）。
>
> **Revision 4（2026-08-01）**：按 R3 review 修正 5 点 ——
> **L1-R3**：`ApplyLoadedContent` 第一行加 `EditorText = content;`（此前漏设——三条路径读文件但编辑器不显示内容，必现 bug）。
> **L2-R3**：`_savedText` 初始 `""`（与 `EditorText` 默认一致）——否则首次打开 `HasUnsavedChanges=true` 误弹确认框。
> **B1-R3/D1-R3/T1-R3**：`TryReadFile` 签名统一为 `bool TryReadFile(string path, [NotNullWhen(true)] out string? content, out string? error)`（原 string? 返回 + out content 冗余双通道、调用点参数数不一致会编译错、`[NotNullWhen]` 用在非 bool 返回上语义错）；所有调用点统一 `if (!TryReadFile(path, out var content, out var readError)) { ErrorMessage = readError; return; }`。
> **B2-R3**：`LoadExternalAsync` 加 `_loadExternalInProgress` guard（模态 `IMessageBoxPrompt` 前提下快速双击不可达，防御并发覆盖竞态）。
>
> **Revision 5（2026-08-01）**：按 R4 review 修正 2 点 ——
> **L1-R4**：**证伪（源码证据）**——`WpfMessageBoxPrompt.ShowAsync`（`WpfMessageBoxPrompt.cs:34,45-58`）经 `Dispatcher.InvokeAsync` 调度、内部为**同步模态 `MessageBox.Show`**，启动模态循环阻塞**整个 WPF UI 线程**；确认框打开期间用户无法操作 HIL tab（BrowseEcu 不可达），guard 的"跳过"不可达，三方不一致崩溃路径不成立。spec §2.1 补证据。
> **B1-R4**：`LoadExternalAsync` 开头加 `if (string.IsNullOrEmpty(path)) return;`（与 `LoadInitialPath` 空检查一致）；`BrowseEcu` 改动明确只在 `if (path is not null)` 块内 Invoke 事件（取消时不设路径也不 Invoke——现状 `HilViewModel.cs:93-94` 已是 early-return）。

---

## 1. Goals

**G1. Browse 字段空态提示** — 5 个 Browse 路径字段（DBC / Suite / Trace / ECU script / Matrix）为空时，文本框内显示灰色中文斜体提示，说明该字段用途。用户已确认行为：**始终显示**（聚焦空态也显示），输入第一个字符即消失。

**G2. Mode 图标 + 功能说明** — Mode ComboBox 旁加随 `SelectedMode` 切换的 emoji 图标，悬停 ToolTip 显示该模式中文功能说明；下拉框每项显示 "emoji + 模式名"。用户已确认：emoji 风格、仅 ToolTip 解释、下拉框加图标。

**G3. 独立 ECU 脚本 JSON 编辑器** — 内嵌 60px 编辑器独立成**非模态窗口工具**（仿 `UdsWindow`）：打开/保存/另存为 .json、保存前用 `EcuScriptLoader.Parse` 校验（非法阻止保存并显示具体错误）、Format 美化；HIL view 移除内嵌编辑器 + `SaveEcu`，加 "Open ECU Editor" 按钮；**编辑器保存/打开的**（`IsValidEcuScript==true` 时）文件路径回填 `HilViewModel.EcuScriptPath`。**Run 语义**：VirtualEcu 模式 Run 用 `EcuScriptPath` 指向的文件；`EcuScriptPath` 为空时 **`CanRun` 已返回 false → Run 按钮禁用**（`HilViewModel.cs:288`，现有逻辑，无需新增）。

## 2. Current State

### 2.1 证据

| 项 | 证据 |
|----|------|
| HIL 布局 | `HilView.xaml` 两个横向 StackPanel（`Row1` :16-25 = Mode + DBC + Suite；`Row2` :28-48 = Trace + HardwareChannel + EcuScript + Matrix + Faults/Analyze）；ECU 编辑器 DockPanel :51-60；中部 TabControl（Results / HTML Report）:69-135；外层 `<DockPanel Margin="16">` :14 |
| 5 个 Browse 字段 | `DbcPath`/`SuitePath`/`TracePath`/`EcuScriptPath`/`MatrixPath`，裸 `TextBox + Browse 按钮`，各有英文 ToolTip，空态纯白框（`HilView.xaml:21-42`） |
| VM 属性 | `HilViewModel.cs` `[ObservableProperty]`，默认 `""`：`_dbcPath`:23 `_suitePath`:24 `_tracePath`:25 `_ecuScriptPath`:27 `_matrixPath`:28 |
| HilMode enum | `Core/HIL/HilMode.cs:6-19` 仅 4 值：`TraceReplay`/`Hardware`/`VirtualEcu`/`Matrix`；`SelectedMode` 默认 `TraceReplay`（`HilViewModel.cs:33`）；ComboBox `ItemsSource`=ObjectDataProvider `HilModeValues`（`HilView.xaml:7-12,18-20`） |
| **CanRun 现状** | `HilViewModel.cs:279-292` `CanRun()`：`IsRunning`→false；`SuitePath`/`DbcPath` 空→false；`switch(SelectedMode)` 中 **`VirtualEcu => !string.IsNullOrEmpty(EcuScriptPath)`（:288）**。**不引用 `EcuEditorJson`** → 删它不影响 CanRun；空 `EcuScriptPath` 时 Run 已禁用 |
| 内嵌 ECU 编辑器 | `HilView.xaml:51-60` 固定 `Height="60"` TextBox + Save ECU/Run/Analyze；`HilViewModel.cs` `_ecuEditorJson`:47 `_currentEcuTempPath`:50 `SaveEcu()`:107-120（无校验写临时文件）`CanSaveEcu()`:122 `BrowseEcu`:90-95（只设 `EcuScriptPath`，不反向读文件） |
| 校验器 | `EcuScriptLoader.Parse(string json)`（`Infrastructure/HIL/EcuScriptLoader.cs:24-28`）public static。**已核实异常类型**：`JsonException`（:26,44,59,149,150）、`KeyNotFoundException`（:32 等缺字段）、`FormatException`（hex/base64：:70,189,197,208）、`InvalidOperationException`（值类型不匹配：:71,129,144,158）。**public API——未来可能新增异常**（→ TryValidate 需防御兜底） |
| Format 陷阱 | `EcuScript.cs:10-15` 是**解析后的状态机**（不含原始 `rules`/`states`）——用 `HILJsonOptions.Default` 序列化 CLR `EcuScript` 会把 `rules` 改写为 `states`；Format 必须 `JsonDocument` 往返 + `WriteIndented` |
| 文件对话框 | `Core/IFileDialogService.cs:10-28` `string? ShowOpenDialog(string filter)` / `string? ShowSaveDialog(string filter, string? defaultExt, string? initialDirectory)`（initialDirectory 为 `string?`，:27）；实现 `Services/WpfFileDialogService.cs`；DI Singleton `AppHostBuilder.cs:133` |
| **确认框** | `IMessageBoxPrompt`（`Services/Trace/TraceSessionAutoSaver.cs:89`）：`Task<MessageBoxResult> ShowAsync(string title, string message, Window? owner)`；实现 `Services/Trace/WpfMessageBoxPrompt.cs`——`ShowAsync` 经 `Dispatcher.InvokeAsync` 调度 `ShowInternal`（:34），内部为**同步模态 `MessageBox.Show`**（:45-58）→ **阻塞整个 WPF UI 线程直到用户关闭**，确认框期间无法操作 HIL tab（L1-R4 证据，guard 假设成立）；AppShellViewModel 已注入（:279）；测试用 `Substitute.For<IMessageBoxPrompt>()`（`AppShellViewModelMessageBoxPromptTests.cs` 先例） |
| **CA1031 未启用** | `.editorconfig` 未配置 CA1031；`AnalysisMode=Recommended` + `TreatWarningsAsErrors` 下现有代码有约 40 处**裸 `catch (Exception ex)` 编译通过**（`Program.cs:206`、`App.xaml.cs:98,143,152,276,291,308`、`PeakChannelProbe.cs:53`、`HilView.xaml.cs:49`、`ScriptView.xaml.cs:90`、`UdsSession.cs:123` 等）→ 裸 catch 是项目惯例 |
| 独立窗口接入先例 | `ViewSwitcher.ShowWindow<TWindow>(Func<TWindow> factory, ref TWindow? cache)`（`Composition/ViewSwitcher.cs:108-174`，接受 factory；缓存 + Closed 清缓存 CacheHolder:199-208）；`UdsWindow`（`ViewSwitchFlow.cs:95-132`，含 Owner 赋值）；`TraceViewerView`（:186-256，Closed→Reset hook 先例 :218 但每次重复订阅）；`MultiFrameSendWindow` ctor 收 VM |
| AppShellViewModel ctor | `AppShellViewModel.cs:270-283`：必填参到 `HilViewModel`（:281），**可选参只在尾部** `IChannelEnumerator? = null`（:282）+ `IConfiguration? = null`（:283）→ 新必填参数插 :281/:282 之间 |
| ScriptView 不可照抄 | `ScriptView` 的 WebView2 编辑器**端到端是坏的**（Open/Save 绑不存在的命令、JS↔C# 桥未通、`ScriptText` 恒空）→ 新编辑器**不用 WebView2**，用纯 WPF TextBox |
| 图标方案 | 项目无图标字体/矢量 Path/可用图片资源；现有惯例 = **emoji 直接写按钮 Content**（`ScriptView.xaml:28,32` 📂💾、`TraceViewerView.xaml:112,115` 🔒🤖、`MultiFrameSendWindow.xaml:109` 💾 等 8 处） |
| 转换器惯例 | `Composition/Converters/InverseNullToVisibilityConverter.cs`；app-wide 可复用转换器注册 `App.xaml:17-26`，view 专属注册局部资源（`SendView.xaml:11`） |
| 窗口不注册 DI | `AppHostBuilderTests.cs:400` 负向断言钉死"TraceViewerView 不注册 DI" → 新窗口遵循同样决策 |

### 2.2 现状痛点（ECU 编辑器）

- 固定 60px 只够约 3 行，ECU 脚本轻松几十行
- 纯裸 TextBox：无高亮/行号/校验/格式化/等宽字体
- `SaveEcu` 不校验 JSON：非法内容静默写临时文件，Run 时才在 StatusMessage 报错（`HilViewModel.cs:267-271`）
- 保存到随机 GUID 临时文件，用户不知道内容落在哪；内容不持久化，重启即丢
- `BrowseEcu` 不把文件内容读进编辑器，两条路径脱节

## 3. Design

### 3.1 功能 1：Browse 字段中文占位符

**新转换器** `Composition/Converters/EmptyStringToVisibilityConverter.cs`（仿 `InverseNullToVisibilityConverter`，带 XML doc + nullable）：
```csharp
[ValueConversion(typeof(string), typeof(Visibility))]
public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null or string { Length: 0 } ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("EmptyStringToVisibilityConverter is one-way.");
}
```

**注册（只一次，App.xaml）**：`App.xaml` :17-26 加 `<conv:EmptyStringToVisibilityConverter x:Key="EmptyStringToVisibilityConverter" />`。HilView **不重复注册**——局部 XAML 用 `{StaticResource EmptyStringToVisibilityConverter}`（StaticResource 向上查 `Application.Resources`）。转换器无状态，一个全局实例足够（T1）。

**HilView.xaml**：**根必加 `xmlns:conv="clr-namespace:PeakCan.Host.App.Composition.Converters"`**（T1-R2：功能 2 局部注册 `<conv:HilModeToIconConverter x:Key="HilModeIcon" />` 必须用 `conv:` 前缀；功能 1 的 EmptyString 转换器用全局 `{StaticResource}` 不需要 `conv:` 前缀，但根命名空间仍要加）。`UserControl.Resources` 加共享 `HintTextStyle`（Gray / Italic / IsHitTestVisible=False / VerticalAlignment=Center / HorizontalAlignment=Stretch / TextTrimming=CharacterEllipsis / Margin=4,0,0,0）。**5 个 Browse TextBox** 每个 Grid 包裹 + overlay 提示（示例 DBC）：
```xml
<Grid Width="180" Margin="0,0,4,0">
    <TextBox Text="{Binding DbcPath}" ToolTip="DBC file path" />
    <TextBlock Text="选择 DBC 文件..." Style="{StaticResource HintTextStyle}"
               Visibility="{Binding Text, RelativeSource={RelativeSource AncestorType=TextBox},
                            Converter={StaticResource EmptyStringToVisibilityConverter}}" />
</Grid>
```
设计点：TextBlock 是 Grid 最后 child（盖在 TextBox 白底上）；`IsHitTestVisible=False`（点击穿透聚焦）；Visibility 绑 **`TextBox.Text` DP**（RelativeSource）→ 每键击即时通知，输入首字符立即消失，不依赖 VM 的 LostFocus 更新；外层 Grid 承接原 Width+Margin，布局零变化。**只改 5 个 Browse 字段**，`HardwareChannel`（默认 "USB1"、无 Browse）不动。

**提示文字**（中文；纯 XAML 属性，日后改回英文只改 5 处）：DBC(180)="选择 DBC 文件..."、Suite(180)="选择测试套件 JSON..."、Trace(260)="选择 Trace 文件 (.asc/.blf)..."、EcuScript(160)="选择 ECU 脚本..."、Matrix(160)="选择矩阵配置 JSON..."。

### 3.2 功能 2：Mode 图标 + ToolTip + 下拉框图标

**两个新转换器**（局部注册到 `HilView.Resources`，HIL 专属）：
- `HilModeToIconConverter.cs`（**null-safe unboxing，T3**）：
```csharp
[ValueConversion(typeof(HilMode), typeof(string))]
public sealed class HilModeToIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is HilMode mode ? mode switch
        {
            HilMode.TraceReplay => "📼",   // 回放录制文件
            HilMode.Hardware    => "🔌",   // 真实 PCAN 硬件
            HilMode.VirtualEcu  => "💻",   // 本机模拟器
            HilMode.Matrix      => "🔗",   // 多 ECU 互联
            _ => "❓",
        } : "❓";
    public object ConvertBack(...) => throw new NotSupportedException(...);
}
```
  **禁止** `(HilMode)value` 直接 cast（value 为 null 时 InvalidCastException）。映射依据 `HilMode.cs:8-18` XML doc + `docs/user-manual-hil.html:882-904`。
- `HilModeToDescriptionConverter.cs`（ToolTip 用，同样 null-safe pattern）：TraceReplay="离线回放：从 ASC/BLF 录制文件回放 CAN 帧，无需硬件（只读）"、Hardware="硬件在环：通过 PCAN-USB 连接真实 ECU，发送真实 CAN 帧并验证响应"、VirtualEcu="虚拟 ECU：本机运行 ECU 脚本 JSON 模拟单个 ECU，无需真实硬件"、Matrix="多 ECU 矩阵：矩阵配置 JSON 驱动多个虚拟 ECU，模拟多 ECU 总线交互"，未知/null→空串。

**HilView.xaml**：`UserControl.Resources` 加 `<conv:HilModeToIconConverter x:Key="HilModeIcon" />` + `<conv:HilModeToDescriptionConverter x:Key="HilModeDesc" />`。ComboBox :18-20 加 ItemTemplate（enum 值作 DataContext，`{Binding}`=ToString）：
```xml
<ComboBox.ItemTemplate>
    <DataTemplate>
        <StackPanel Orientation="Horizontal">
            <TextBlock Text="{Binding Converter={StaticResource HilModeIcon}}" Margin="0,0,6,0" />
            <TextBlock Text="{Binding}" VerticalAlignment="Center" />
        </StackPanel>
    </DataTemplate>
</ComboBox.ItemTemplate>
```
ComboBox 之后加图标（16px + ToolTip）：
```xml
<TextBlock Text="{Binding SelectedMode, Converter={StaticResource HilModeIcon}}"
           ToolTip="{Binding SelectedMode, Converter={StaticResource HilModeDesc}}"
           VerticalAlignment="Center" Margin="0,0,8,0" FontSize="16" />
```

### 3.3 功能 3：独立 ECU 脚本 JSON 编辑器窗口

#### 3.3.1 新文件与窗口 XAML

| 文件 | 职责 |
|---|---|
| `src/PeakCan.Host.App/Windows/EcuScriptEditorWindow.xaml` + `.xaml.cs` | 独立非模态窗口；ctor 收 `EcuScriptEditorViewModel` 设 DataContext（仿 `MultiFrameSendWindow.xaml.cs:15-20`）；`WindowStartupLocation="CenterOwner"`、约 900x600 |
| `src/PeakCan.Host.App/ViewModels/EcuScriptEditorViewModel.cs` | 全部编辑器逻辑 |
| `tests/PeakCan.Host.App.Tests/ViewModels/EcuScriptEditorViewModelTests.cs` | 单元测试（NSubstitute fake `IFileDialogService` + `IMessageBoxPrompt`） |

**窗口 XAML 骨架**（T4）：
```xml
<Window x:Class="PeakCan.Host.App.Windows.EcuScriptEditorWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        Title="{Binding WindowTitle}" Width="900" Height="600"
        WindowStartupLocation="CenterOwner">
  <DockPanel>
    <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="4">
      <Button Content="📂 Open" Command="{Binding OpenCommand}" Margin="0,0,4,0" Padding="8,3" />
      <Button Content="💾 Save" Command="{Binding SaveCommand}" Margin="0,0,4,0" Padding="8,3" />
      <Button Content="Save As..." Command="{Binding SaveAsCommand}" Margin="0,0,4,0" Padding="8,3" />
      <Button Content="✨ Format" Command="{Binding FormatCommand}" Margin="0,0,4,0" Padding="8,3" />
      <TextBlock Text="{Binding StatusMessage}" VerticalAlignment="Center" Margin="12,0,0,0" />
    </StackPanel>
    <Border DockPanel.Dock="Bottom" BorderBrush="#CCCCCC" BorderThickness="1" Margin="4">
      <TextBlock Text="{Binding ErrorMessage}" Foreground="Red" TextWrapping="Wrap" />
    </Border>
    <TextBox Text="{Binding EditorText, UpdateSourceTrigger=PropertyChanged}"
             AcceptsReturn="True" TextWrapping="NoWrap"
             FontFamily="Cascadia Mono, Consolas"
             VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Auto"
             Padding="4" Margin="4" />
  </DockPanel>
</Window>
```
- **`UpdateSourceTrigger=PropertyChanged` 必设**（B3）：否则默认 LostFocus 下"输入后直接点 Save"读到旧值
- code-behind 仅 `InitializeComponent()` + ctor 收 VM 设 DataContext，无业务逻辑

#### 3.3.2 ViewModel 设计（`EcuScriptEditorViewModel : ObservableObject`）

- ctor：`(IFileDialogService fileDialog, IMessageBoxPrompt messageBox, ILogger<EcuScriptEditorViewModel> logger)`
- 属性：
  - `[ObservableProperty] string _editorText = "";`
  - `[ObservableProperty] string? _filePath;`（当前文件路径；回填输出端）
  - `[ObservableProperty] string _statusMessage = "Ready";`
  - `[ObservableProperty] string? _errorMessage;`（XAML 红字）
  - `[ObservableProperty] string _windowTitle = "ECU Script Editor";`
  - `[ObservableProperty] bool _isValidEcuScript;`（**回填门禁**，L1-R2）
  - 私有 `string? _savedText = "";`（脏跟踪基线 = 最近一次 Load/Save 的内容；**初始 `""` 与 EditorText 默认一致，L2-R3**）
  - 私有 `bool _loadExternalInProgress;`（LoadExternalAsync 并发 guard，B2-R3）
  - 派生 `bool HasUnsavedChanges => !string.Equals(EditorText, _savedText, StringComparison.Ordinal);`
- **`WindowTitle` 自动派生（T2-R2）**：`partial void OnFilePathChanged(string? value) => WindowTitle = string.IsNullOrEmpty(value) ? "ECU Script Editor" : $"ECU Script Editor - {Path.GetFileName(value)}";`（CommunityToolkit 生成 partial；删所有手动同步点，不可能遗漏）
- 命令：
  - **`Open`（async，脏确认 L2-R2）**：
    ```csharp
    [RelayCommand]
    private async Task Open()
    {
        if (HasUnsavedChanges)
        {
            var r = await _messageBox.ShowAsync("Discard changes?",
                "Opening a file will discard unsaved changes. Continue?", null);
            if (r != MessageBoxResult.OK) return;
        }
        var path = _fileDialog.ShowOpenDialog("ECU Script JSON|*.json|All Files|*.*");
        if (path is null) return;                       // 取消 → 无操作
        if (!TryReadFile(path, out var content, out var readError)) { ErrorMessage = readError; return; }
        ApplyLoadedContent(path, content);
    }
    ```
  - **`Save`**：`FilePath` 空→`SaveAs`；否则 `TrySaveTo(FilePath)`
  - **`SaveAs`**：`var dir = FilePath is null ? null : Path.GetDirectoryName(FilePath);`（B1 null 保护，`GetDirectoryName("")` 返回 null 安全）；`ShowSaveDialog("ECU Script JSON|*.json", ".json", dir)`；**返回 null（取消）→ 无操作**（不写文件/不改 FilePath/不改 StatusMessage，L5）；否则 `TrySaveTo(chosen)`
  - **`Format`**（D1）：`using var doc = JsonDocument.Parse(EditorText); EditorText = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });`（**`doc.RootElement`** 而非 `doc`）；catch `JsonException` → ErrorMessage；**禁用** `HILJsonOptions.Default` 序列化 CLR `EcuScript`
- **共享读文件 helper（D1-R2，Open 与 LoadInitialPath/LoadExternal 统一）**：
  ```csharp
  private bool TryReadFile(string path, [NotNullWhen(true)] out string? content, out string? error)
  {
      try { content = File.ReadAllText(path); error = null; return true; }
      catch (Exception ex) { content = null; error = ex.Message; return false; }   // 裸 catch 是项目惯例（§2.1 CA1031 未启用）
  }
  ```
  `File.ReadAllText` 的所有异常（IO/UnauthorizedAccess/PathTooLong/Argument/NotSupported…）统一落 `ErrorMessage`。**签名统一为 bool + out content + out error（B1-R3/D1-R3/T1-R3）**：`[NotNullWhen(true)]` 标注在 bool 返回方法上是其正确用法；避免"返回值=内容 + out 内容"的冗余双通道。**所有调用点统一**：`if (!TryReadFile(path, out var content, out var readError)) { ErrorMessage = readError; return; }`
- **`ApplyLoadedContent(path, content)`**（Open/LoadInitialPath/LoadExternal 共用；**属性设置顺序关键**）：
  ```csharp
  private void ApplyLoadedContent(string path, string content)
  {
      EditorText = content;                                 // 必须设——否则内容读进 out 但编辑器不显示（L1-R3）
      IsValidEcuScript = TryValidate(content, out var validateError);
      ErrorMessage = IsValidEcuScript ? null : validateError;   // 非法：仍显示内容供查看/修改，但警告
      _savedText = content;
      FilePath = path;                                          // 最后设 → 回填 handler 读到已更新的 IsValidEcuScript
      StatusMessage = IsValidEcuScript ? $"Opened {path}" : $"Opened {path} (not a valid ECU script)";
  }
  ```
- **`TryValidate`（Save/SaveAs/ApplyLoadedContent 共用；B2-R2 防御兜底）**：
  ```csharp
  private bool TryValidate(string json, [NotNullWhen(false)] out string? error)
  {
      try { _ = EcuScriptLoader.Parse(json); error = null; return true; }
      catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
      { error = ex.Message; return false; }                    // 4 类源码核实（§2.1 校验器）
      catch (Exception ex) { error = ex.Message; return false; } // 防御兜底：public API 未来新增异常不逃逸
  }
  ```
- **`TrySaveTo(path)`**：`TryValidate` 失败 → `ErrorMessage` + `StatusMessage="Save blocked: invalid JSON."`，**不写文件不改 FilePath**；成功 → `File.WriteAllText(path, EditorText)`（catch Exception → ErrorMessage）→ `IsValidEcuScript=true` → `_savedText=EditorText` → `FilePath=path`（**仅 SaveAs 时值变**；Save 同路径 SetProperty 值相等不通知）→ `StatusMessage="Saved {path}"`
- **`LoadInitialPath(path)`**（种子）：**"空"= `string.IsNullOrEmpty(path)`（B2）**：空 → 仅清 `ErrorMessage`；`File.Exists(path)` false → `ErrorMessage=$"File not found: {path}"`；否则 `if (!TryReadFile(path, out var content, out var readError)) { ErrorMessage = readError; return; } ApplyLoadedContent(path, content);`
- **`LoadExternalAsync(path)`**（BrowseEcu→编辑器同步，L3-R2）：
  ```csharp
  public async Task LoadExternalAsync(string path)
  {
      if (string.IsNullOrEmpty(path)) return;   // B1-R4：与 LoadInitialPath 空检查一致（BrowseEcu 取消时不 Invoke，防御未来误调）
      if (_loadExternalInProgress) return;   // B2-R3：并发 guard（模态 MessageBox 阻塞 UI 前提下快速双击不可达，§2.1 证据）
      _loadExternalInProgress = true;
      try
      {
          if (HasUnsavedChanges)
          {
              var r = await _messageBox.ShowAsync("Discard changes?",
                  $"Loading {path} will discard unsaved changes. Continue?", null);
              if (r != MessageBoxResult.OK) return;   // 保持当前文件；EcuScriptPath 已是 BrowseEcu 所设，用户知情
          }
          if (!TryReadFile(path, out var content, out var readError)) { ErrorMessage = readError; return; }
          ApplyLoadedContent(path, content);
      }
      finally { _loadExternalInProgress = false; }
  }
  ```
  AppShell 事件 handler 用 fire-and-forget `_ = LoadExternalAsync(path);`
- **`Reset()`**（L1/B4）：`EditorText=""`、`_savedText=""`、`FilePath=null`、`IsValidEcuScript=false`、`ErrorMessage=null`、`StatusMessage="Ready"`（`WindowTitle` 由 OnFilePathChanged 自动回基值）。语义：关窗=会话结束，未保存编辑丢弃；重开=新会话重新种子

#### 3.3.3 接线定案

**A. HIL "Open ECU Editor" 按钮 → 窗口**（ctor 事件订阅；`_hilViewModel` 无 public 访问器已确认 `AppShellViewModel.cs:113`，HilViewModel 全生命周期唯一实例）：
- `HilViewModel.cs`：加 `public event Action? OpenEcuEditorRequested;` + `[RelayCommand] private void OpenEcuEditor() => OpenEcuEditorRequested?.Invoke();`（不改 ctor → 测试构造点不动）
- `AppShellViewModel.cs` ctor（:310 后）：`_hilViewModel.OpenEcuEditorRequested += OnOpenEcuEditorRequested;`
- `ViewSwitchFlow.cs`：`private void OnOpenEcuEditorRequested() => ShowEcuScriptEditorCommand.Execute(null);`
- `HilView.xaml:52-56`：行 53 Save ECU 按钮 → `<Button Content="Open ECU Editor" Command="{Binding OpenEcuEditorCommand}" .../>`；删行 57-59 内嵌 TextBox

**B. EcuScriptPath 回填 + 三方一致（L1-R2 / L3-R2）**：
- **回填（编辑器→HIL）**：`AppShellViewModel.cs` ctor 加 `_ecuScriptEditorViewModel.PropertyChanged += OnEcuScriptEditorPropertyChanged;`：
  ```csharp
  private void OnEcuScriptEditorPropertyChanged(object? s, PropertyChangedEventArgs e)
  {
      if (e.PropertyName is nameof(EcuScriptEditorViewModel.FilePath) or nameof(EcuScriptEditorViewModel.IsValidEcuScript))
          SyncEcuScriptPath();
  }
  private void SyncEcuScriptPath()
  {
      var fp = _ecuScriptEditorViewModel.FilePath;
      if (!string.IsNullOrEmpty(fp) && _ecuScriptEditorViewModel.IsValidEcuScript)   // 门禁：只回填合法 ECU 脚本
          _hilViewModel.EcuScriptPath = fp;
  }
  ```
  （订阅两个属性使 handler 与 Open 的设置顺序无关；`ViewSwitchFlow.cs` 加 `using System.ComponentModel;`。**`Open` 加载非法 JSON → `IsValidEcuScript=false` → 不回填**，堵住 L1-R2 旁路）
- **HIL→编辑器（BrowseEcu 同步，L3-R2）**：
  - `HilViewModel.BrowseEcu`（:90-95）：在 **`if (path is not null)` 块内**（`EcuScriptPath = path;` 之后）加 `EcuScriptPathSetExternally?.Invoke(path);`（新事件 `public event Action<string>? EcuScriptPathSetExternally;`）——**取消时（path null）不设 `EcuScriptPath` 也不 Invoke**（B1-R4，现有 `HilViewModel.cs:93-94` 已是 early-return）
  - `AppShellViewModel.cs` ctor 加 `_hilViewModel.EcuScriptPathSetExternally += OnEcuScriptPathSetExternally;`，handler：若编辑器窗口已打开 → `_ = _ecuScriptEditorViewModel.LoadExternalAsync(path);`（脏确认后加载，保持 HIL TextBox / 编辑器 FilePath / WindowTitle 一致）
- **种子（HIL→编辑器首次）**：`ShowEcuScriptEditorCommand` 把 `LoadInitialPath(_hilViewModel.EcuScriptPath)` 放 `ViewSwitcher.ShowWindow` 的 **factory**（仅首次打开执行，缓存重显不重新种子）；`win.Closed += (_,_) => _ecuScriptEditorViewModel.Reset();` 也放 factory（每次窗口实例只订阅一次，改进 ViewSwitchFlow:218 重复订阅）
- **语义定案**："最后写入胜"是预期行为——两个写入方都是显式用户操作（BrowseEcu / 编辑器 Save/SaveAs），且每次写入后 HIL `EcuScriptPath` TextBox 实时绑定更新 = 可见反馈；编辑器标题显示当前 FilePath。无静默旁路（Open 非合法脚本不回填）

**C. 现有测试破坏**（grep 已确认，`HilViewModelReportTests` 不破）：
- `HilViewModelTests.cs:215-228` `EcuEditor_SaveAndRun_WritesTempFile_SetsEcuScriptPath` → **删除**（职责移入新 VM 测试）
- `HilViewModelTests.cs:230-241` `EcuEditor_EmptyJson_RunButtonDisabled` → 删 :237 `vm.EcuEditorJson = "";`，改名 `EcuScriptPath_Empty_RunButtonDisabled`。**CanRun 逻辑不改**（L2）
- **8 处 `new AppShellViewModel(`**（`AppShellViewModelTests.cs:107/436/533/671/972/1072`、`UdsWindowTests.cs:67`、`AppShellViewModelMessageBoxPromptTests.cs:128`）→ 加必填参数 `new EcuScriptEditorViewModel(Substitute.For<IFileDialogService>(), Substitute.For<IMessageBoxPrompt>(), NullLogger<EcuScriptEditorViewModel>.Instance)`

**D. DI + 生命周期**：
- `EcuScriptEditorViewModel` 注册 **`AddSingleton`**（`AppHostBuilder.cs:303` 后，仿 `TraceViewerViewModel` `ViewModelsBatch2Flow.cs:82`：单例 + Closed 时 Reset + 每次打开 LoadInitialPath 重新种子）
- **窗口不注册 DI**（被 `AppHostBuilderTests.cs:400` 负向断言钉死）；`ShowEcuScriptEditorCommand` 直接 `new EcuScriptEditorWindow(_ecuScriptEditorViewModel)`
- `AppShellViewModel.cs`：字段 `_ecuScriptEditorViewModel`(:113 附近) + `_ecuScriptEditorWindow`(:158 附近) + ctor **必填参数插 :281 `HilViewModel` 之后、:282 `IChannelEnumerator?` 之前**（T5）+ 赋值(:310 后)
- `AppHostBuilder.cs:328-356` factory：:354 `GetRequiredService<HilViewModel>(),` 后加 `GetRequiredService<EcuScriptEditorViewModel>(),`

**E. 主窗口关闭（B5）**：`ShowEcuScriptEditorCommand` 设 `win.Owner = Application.Current?.MainWindow`（仿 `ViewSwitchFlow.cs:95-132`）。WPF owned 窗口随 owner 关闭 → Closed → `Reset()` + 缓存清理。无孤儿窗口。

#### 3.3.4 HilViewModel / HilView / AppShell 改动

- `HilViewModel.cs`：删 `_ecuEditorJson`(:47)、`_currentEcuTempPath`(:50)、`SaveEcu`+`CanSaveEcu`(:104-122)；**保留 `using System.IO;`**（`OpenReport` :177 仍用 `File.Exists`）；`CanRun` 不改；`BrowseEcu` 末尾加 `EcuScriptPathSetExternally?.Invoke(path);` + 新事件；加 `OpenEcuEditorRequested` + `OpenEcuEditor` 命令
- `HilView.xaml`：:51-60 DockPanel 简化为按钮 StackPanel（Open ECU Editor / Run / Analyze）
- `AppShell.xaml`：:41 "Trace Viewer…" 后加 `<MenuItem Header="ECU Script Editor" Command="{Binding ShowEcuScriptEditorCommand}" />`

## 4. 单元测试

**新增** `tests/PeakCan.Host.App.Tests/Composition/Converters/`（仿 `HexConverterTests.cs`，非 STA）：
- `EmptyStringToVisibilityConverterTests`：null→Visible、""→Visible、非空→Collapsed、ConvertBack→NotSupportedException
- `HilModeToIconConverterTests`：4 mode→对应 emoji、null→"❓"、未知 enum→"❓"（验证 null-safe unboxing）
- `HilModeToDescriptionConverterTests`：4 mode→非空中文、null→空串

**新增** `tests/PeakCan.Host.App.Tests/ViewModels/EcuScriptEditorViewModelTests.cs`（NSubstitute fake `IFileDialogService` + `IMessageBoxPrompt`，临时文件 `Path.GetTempPath()`+Guid + try-finally 清理）：
- Open 文件存在→加载内容+FilePath+WindowTitle+IsValidEcuScript / Open 取消→不变
- **Open 有未保存修改 + 确认取消 → 不调 ShowOpenDialog、内容不变（L2-R2）** / 确认 OK → 正常加载
- **Open 合法 ECU 脚本 → IsValidEcuScript=true / Open 非法 JSON（如 `{"a":1}`）→ 内容仍加载、IsValidEcuScript=false、ErrorMessage 非空（L1-R2）**
- SaveAs 合法→写文件+Saved+FilePath 更新+IsValidEcuScript=true / SaveAs 取消→不写文件、FilePath/StatusMessage 不变（L5）
- SaveAs 首次使用（FilePath=null）→ 不抛 ArgumentNullException（B1）
- SaveAs 非法 `{"name":"Test"}`（缺 canIds，KeyNotFoundException）/ `{"name":1}`（name 非字符串，InvalidOperationException）/ `"[invalid json"`（JsonException）→ 4 类都 catch、文件未创建（D2）
- Save 已有路径→直接写不弹框（ShowSaveDialog 未被调用）
- Format 美化→含换行且 EcuScriptLoader.Parse 通过 / Format 非法→ErrorMessage+内容不变
- LoadInitialPath 缺文件→ErrorMessage / 空串→仅清错误不报错（B2）/ null→同空串
- **LoadExternalAsync 有未保存修改 + 确认取消 → 内容不变（L3-R2）** / 确认 OK → 加载
- **LoadExternalAsync 进行中 → 并发调用被 guard 跳过（B2-R3）**
- **LoadExternalAsync 空路径 → NoOp（不弹确认、不改内容）（B1-R4）**
- **首次创建实例（`_savedText=""` 初始）→ `HasUnsavedChanges=false`，Open 不弹确认框（L2-R3）**
- **Open 文件 → EditorText 被设为文件内容（L1-R3）** / LoadInitialPath / LoadExternalAsync 同理
- **Reset→EditorText=""、FilePath=null、IsValidEcuScript=false、ErrorMessage=null、StatusMessage="Ready"、WindowTitle 回基值（L1/B4/T2-R2）**
- **WindowTitle 随 FilePath 自动派生（T2-R2）**：Open 设 FilePath → 标题含文件名；Reset → 基值

**扩展** `ConverterSmokeTests.AllConverters()`：EmptyString 用 `UIElement.VisibilityProperty`+OneWay；两个 HilMode 转换器用 `TextBlock.TextProperty`+OneWay。

**AppShellViewModelTests 补一条回填门禁**：editor FilePath 非空 + IsValidEcuScript=true → `_hilViewModel.EcuScriptPath` 更新；IsValidEcuScript=false → 不回填（L1-R2）。

## 5. 验证

1. `dotnet build PeakCan.Host.slnx` — 警告清零（TreatWarningsAsErrors）
2. `dotnet test tests/PeakCan.Host.App.Tests`（重点：EcuScriptEditorViewModelTests、HilViewModelTests、AppShellViewModelTests、AppHostBuilderTests）
3. 手动 `dotnet run --project src/PeakCan.Host.App`：
   - **G1**：5 个空 Browse 字段显示灰色中文斜体提示；输入首字符/点 Browse 立即消失；清空恢复；布局不变
   - **G2**：切换 Mode 图标随 SelectedMode 变（📼/🔌/💻/🔗）；悬停图标 ToolTip 显示功能；下拉框每项 "emoji + 模式名"
   - **G3**：
     - HIL tab → Open ECU Editor → 窗口弹出，标题 "ECU Script Editor"
     - 粘贴非法 JSON → Save 阻止 + 具体错误（红字）→ 修好 → Save As 到临时路径 → 标题 "ECU Script Editor - {file}"，HIL `EcuScriptPath` 回填 → VirtualEcu Run 用该文件
     - **Open 非法 JSON → 内容仍显示 + 红字警告，但 HIL `EcuScriptPath` 不回填（L1-R2）**
     - **编辑器有未保存修改 → 点 Open → 弹确认框；取消 → 内容保留（L2-R2）**
     - **首次打开编辑器（无编辑）→ 点 Open 不弹确认框（L2-R3）**
     - **BrowseEcu 选文件 C → 若编辑器已打开且有未保存修改 → 确认框；确认 → 编辑器加载 C，HIL TextBox / 编辑器 FilePath / 标题一致（L3-R2）**
     - Save As 取消 → 无操作（L5）；VirtualEcu 空 EcuScriptPath → Run 禁用（L4）
     - 关窗再开 → 从当前 EcuScriptPath 重新种子，旧编辑丢弃（L1）；窗口打开期间切 tab 再回 → 未保存编辑保留
     - Format 美化不改 `rules` 结构；关闭主窗口 → 编辑器窗口随之关闭（B5）

## 6. 实施顺序

1. 功能 1（转换器 → App.xaml → HilView 5 字段 + 测试）
2. 功能 2（2 转换器 → HilView Resources → ComboBox + 图标 + 测试）
3. 功能 3（HilViewModel 瘦身+事件 → 新 VM → 新窗口 → AppShell 接线 → ViewSwitchFlow → AppShell.xaml → HilView 按钮 → DI → 修测试 → 新测试）
4. build + test + 手动验证

## 7. 风险与边界

- **回填门禁**：`EcuScriptPath` 只在 `IsValidEcuScript` 时回填（Open 非法 JSON 不回填）；AppShell handler 订阅 FilePath+IsValidEcuScript 双属性，与设置顺序无关
- **脏确认**：`Open`/`LoadExternalAsync` 前 `HasUnsavedChanges` 且 `IMessageBoxPrompt` 返回非 OK → 无操作；关窗（Reset）不弹确认（会话结束，明确语义）。`_savedText` 初始 `""` 保证首次打开不误报脏（L2-R3）
- **ApplyLoadedContent 必须设 `EditorText = content;`**（L1-R3，三条加载路径共用，漏设则编辑器不显示内容）
- **TryReadFile bool+out 统一签名**（B1-R3/D1-R3/T1-R3）：`bool TryReadFile(string path, [NotNullWhen(true)] out string? content, out string? error)`，所有调用点同一写法
- **LoadExternalAsync 并发 guard**（B2-R3）：`_loadExternalInProgress` 防御性跳过并发调用——**假设已由源码证实**（`WpfMessageBoxPrompt.cs:34,45-58` 同步模态 `MessageBox.Show` 阻塞整个 WPF UI 线程，确认框期间无法操作 HIL tab，快速双击不可达）
- **LoadExternalAsync 空路径防御**（B1-R4）：开头 `if (string.IsNullOrEmpty(path)) return;`；`BrowseEcu` 仅在 `path is not null` 时 Invoke 事件（`HilViewModel.cs:93-94` early-return 现状）
- **校验 catch**：4 类 + 防御兜底（public API 未来异常不逃逸到 UI）
- **裸 catch 合法**：CA1031 未启用（证据 §2.1），但 `TryReadFile` 用裸 catch 统一 Open/LoadInitialPath 行为
- **Format 语义**：`JsonSerializer.Serialize(doc.RootElement, ...)`，禁用序列化 CLR `EcuScript`/`JsonDocument`
- **Reset 语义**：关窗=会话结束（清内容），缓存重显=保留编辑
- **订阅泄漏**：三个订阅（OpenEcuEditorRequested、EcuScriptPathSetExternally、FilePath/IsValidEcuScript 转发）均在单例 VM 之间，同生命周期无泄漏；Closed→Reset 放 factory 避免重复订阅
- **emoji 渲染**：依赖 Win11 Segoe UI Emoji fallback（项目 8 处 emoji 已证明）
- **空白字符串**：粘贴一个空格会隐藏占位符（与 NullToVisibilityConverter 惯例一致）
- **无样式冲突**：全项目无隐式 TextBox Style / 主题，Grid overlay 不冲突

## 8. File Inventory

| 文件 | 动作 |
|---|---|
| `src/PeakCan.Host.App/Views/HilView.xaml` | 改：占位符 + mode 图标 + 移除内嵌编辑器 |
| `src/PeakCan.Host.App/ViewModels/HilViewModel.cs` | 改：删 SaveEcu/EcuEditorJson/_currentEcuTempPath（CanRun 不改）；加 OpenEcuEditor 命令/事件 + EcuScriptPathSetExternally 事件（BrowseEcu 末尾 raise） |
| `src/PeakCan.Host.App/ViewModels/AppShellViewModel/ViewSwitchFlow.cs` | 改：ShowEcuScriptEditorCommand（Owner/种子/Closed 钩子）+ OnOpenEcuEditorRequested + OnEcuScriptPathSetExternally + SyncEcuScriptPath |
| `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs` | 改：字段 + ctor 必填参数（:281/:282 之间）+ 三条订阅 |
| `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` | 改：AddSingleton<EcuScriptEditorViewModel> + factory 补参 |
| `src/PeakCan.Host.App/AppShell.xaml` | 改：_View 菜单项 |
| `src/PeakCan.Host.App/App.xaml` | 改：注册 EmptyStringToVisibilityConverter（仅一次） |
| `src/PeakCan.Host.App/Composition/Converters/` | 新增 3 个转换器 |
| `src/PeakCan.Host.App/Windows/EcuScriptEditorWindow.xaml` + `.xaml.cs` | 新增（Owner 设 MainWindow） |
| `src/PeakCan.Host.App/ViewModels/EcuScriptEditorViewModel.cs` | 新增 |
| `tests/PeakCan.Host.App.Tests/` | 改：HilViewModelTests 2 处 + 8 处构造点 + ConverterSmokeTests + AppShellViewModelTests 回填门禁；新增：3 转换器测试 + EcuScriptEditorViewModelTests |
