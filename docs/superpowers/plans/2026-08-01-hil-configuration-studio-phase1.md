# HIL Configuration Studio — Phase 1 (Studio 壳 + DBC Browser) 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新增非模态窗口 `HilStudioWindow`（三栏 Grid + 2 GridSplitter），Phase 1 实现第一栏 DBC Browser：加载/浏览 DBC 消息→信号→值表，搜索过滤，暴露 `SelectedMessage/SelectedSignal` 供 Phase 2/3 消费。

**Architecture:** 新增 `HilStudioWindow` 窗口 + `HilStudioViewModel`（CommunityToolkit.Mvvm）走 `ViewSwitcher.ShowWindow` 缓存模式。DBC 数据复用现有单例 `DbcService`（`Current` + `DbcLoaded/LoadFailed` 事件），投影为结构化行（`HilStudioDbcMessageRow`/`HilStudioDbcSignalRow`），Message→Signal 双层 DataGrid + 值表 VAL_ 二级展开。Phase 2/3 栏为占位 Border。现有 ECU Script Editor 不动。

**Tech Stack:** WPF net10.0-windows，CommunityToolkit.Mvvm 8.4.2，Microsoft.Extensions.Hosting DI，FluentAssertions + xUnit + NSubstitute（测试），无第三方 UI 控件。

## Global Constraints

- **阶段边界**：本计划只做 Phase 1（Studio 壳 + DBC Browser）。col2/col4 是占位 Border（"(Phase 2)"/"(Phase 3)"），不做 Phase 2/3 任何功能。
- **现有 ECU Script Editor 不动**（`EcuScriptEditorViewModel` / `EcuScriptEditorWindow` 保持原样）。
- **引擎零改动**：不触碰 `EcuScriptLoader` / `TestSuiteEngine` / `StatefulVirtualEcu` 等 runtime 代码。
- **DbcService 是共享单例**：`HilStudioViewModel` 注入同一个 `DbcService`，订阅 `DbcLoaded/LoadFailed` **永不退订**（单例随进程退出）；`OnLoaded` 幂等重建。
- **选中契约（约束 #5）**：`SelectedMessage/SelectedSignal` 在过滤重建或重新加载时会变 null；Phase 1 不保证跨过滤保留选中。
- **搜索语义漂移（约束 #6）**：结构化 `Signal.Name` 匹配 ≠ 现有 `DbcViewModel` 的格式化串匹配；不得声称"行为等价"。
- **依赖路径（约束 #8）**：`RunOnUi` = `src/PeakCan.Host.App/ViewModels/DispatcherExtensions.cs:61`；`IFileDialogService` = `src/PeakCan.Host.Core/IFileDialogService.cs:10`；`NullToVisibilityConverter` = App.xaml 全局注册。不得自行重写 dispatcher 编组。
- **命名消歧（约束 #9）**：`SignalCount`=单消息信号数；`TotalSignals`=全库信号数；投影行 `Source` 暴露 Core record（`Message`/`Signal`）。
- **值表容器（约束 #10）**：值表展开是信号级嵌套 DataGrid 的 `RowDetailsTemplate`，禁止三层嵌套 DataGrid。
- 所有新 ViewModel 继承 `ObservableObject`，用 `[ObservableProperty]`/`[RelayCommand]`；行投影类是 plain class + `init`（无 INPC）。

## File Structure

**新建（src/PeakCan.Host.App/）**
- `Windows/HilStudioWindow.xaml` + `.xaml.cs` — 三栏窗口壳 + DBC Browser UI
- `ViewModels/HilStudioViewModel.cs` — 主 VM（集合、状态、选择钩子、ctor 订阅）
- `ViewModels/HilStudioViewModel/DbcLoadingFlow.partial.cs` — OpenAsync + OnLoaded/OnLoadFailed + RefreshFromCurrent + Rebuild
- `ViewModels/HilStudioViewModel/DbcSearchFlow.partial.cs` — OnSearchTextChanged + ApplyFilter
- `ViewModels/HilStudioDbcMessageRow.cs` — Message 行投影
- `ViewModels/HilStudioDbcSignalRow.cs` — Signal 行投影 + 内嵌 `HilDbcValueTableEntryRow`

**修改（src/PeakCan.Host.App/）**
- `Composition/AppHostBuilder.cs` — 注册 `HilStudioViewModel` 单例 + AppShellViewModel factory 加参数
- `ViewModels/AppShellViewModel.cs` — ctor required 参数 + cache 字段
- `ViewModels/AppShellViewModel/ViewSwitchFlow.cs` — `ShowHilStudio` 命令
- `AppShell.xaml` — View 菜单 MenuItem

**测试（tests/PeakCan.Host.App.Tests/）**
- 新建 `ViewModels/HilStudioProjectionTests.cs`
- 新建 `ViewModels/HilStudioViewModelTests.cs`
- 修改 `ViewModels/AppShellViewModelTests.cs` + `ViewModels/AppShellViewModelMessageBoxPromptTests.cs`（补构造实参）

---

### Task 1: DBC 行投影模型

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/HilStudioDbcMessageRow.cs`
- Create: `src/PeakCan.Host.App/ViewModels/HilStudioDbcSignalRow.cs`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/HilStudioProjectionTests.cs`

**Interfaces:**
- Produces: `HilStudioDbcMessageRow.From(Message, IReadOnlyDictionary<string, ValueTable>)` → `HilStudioDbcMessageRow`（含 `.Source: Message`、`.Id: string`、`.Name`、`.Dlc`、`.Sender`、`.SignalCount: int`、`.Comment: string?`、`.Signals: IReadOnlyList<HilStudioDbcSignalRow>`）
- Produces: `HilStudioDbcSignalRow.From(Signal, IReadOnlyDictionary<string, ValueTable>)` → `HilStudioDbcSignalRow`（含 `.Source: Signal`、`.Name`、`.BitLayout`、`.FactorOffset`、`.MinMax`、`.Unit`、`.ValueTableName: string?`、`.ValueTableEntries: IReadOnlyList<HilDbcValueTableEntryRow>?`）
- Produces: `public sealed record HilDbcValueTableEntryRow(long Key, string Label)`

- [ ] **Step 1: 写失败测试**

`tests/PeakCan.Host.App.Tests/ViewModels/HilStudioProjectionTests.cs`:
```csharp
using FluentAssertions;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Tests.ViewModels;

public class HilStudioProjectionTests
{
    private static readonly ValueTable OffOn = new(
        "M1_SigA_Table",
        new Dictionary<long, string> { [1] = "On", [0] = "Off" });

    private static Signal NewSigA() => new(
        "SigA", 0, 8, ByteOrder.LittleEndian, ValueType.Unsigned,
        1, 0, 0, 255, "km/h", Array.Empty<string>(),
        ValueTableName: "M1_SigA_Table");

    private static Signal NewSigB() => new(
        "SigB", 8, 8, ByteOrder.BigEndian, ValueType.Signed,
        0.1, -5, -12.8, 12.7, "", Array.Empty<string>());

    [Fact]
    public void Message_Projection_Formats_Id_And_Counts()
    {
        var msg = new Message(0x100, "M1", 8, "ECU1",
            new List<Signal> { NewSigA(), NewSigB() },
            IsMultiplexed: false, MultiplexorSignalIndex: null, Comment: "engine msg");
        var tables = new Dictionary<string, ValueTable> { ["M1_SigA_Table"] = OffOn };

        var row = HilStudioDbcMessageRow.From(msg, tables);

        row.Id.Should().Be("0x100");
        row.Name.Should().Be("M1");
        row.Dlc.Should().Be("8");
        row.Sender.Should().Be("ECU1");
        row.SignalCount.Should().Be(2);
        row.Comment.Should().Be("engine msg");
        row.Signals.Should().HaveCount(2);
        row.Source.Should().BeSameAs(msg);
    }

    [Fact]
    public void Extended_Message_Id_Strips_Ide_Bit_And_Uses_X8()
    {
        var msg = new Message(0x80000123u, "M1", 8, "ECU1",
            new List<Signal>(), IsMultiplexed: false, MultiplexorSignalIndex: null);

        var row = HilStudioDbcMessageRow.From(msg, new Dictionary<string, ValueTable>());

        row.Id.Should().Be("0x00000123");
    }

    [Fact]
    public void Signal_Projection_Formats_BitLayout_Scale_Range()
    {
        var row = HilStudioDbcSignalRow.From(NewSigA(), new Dictionary<string, ValueTable>());

        row.Name.Should().Be("SigA");
        row.BitLayout.Should().Be("0|8@1+");      // LittleEndian -> '1', Unsigned -> '+'
        row.FactorOffset.Should().Be("(1,0)");
        row.MinMax.Should().Be("[0|255]");
        row.Unit.Should().Be("km/h");
    }

    [Fact]
    public void Signed_BigEndian_Signal_Uses_0_And_Minus()
    {
        var row = HilStudioDbcSignalRow.From(NewSigB(), new Dictionary<string, ValueTable>());

        row.BitLayout.Should().Be("8|8@0-");      // BigEndian -> '0', Signed -> '-'
    }

    [Fact]
    public void ValueTable_Entries_Expanded_Ordered_By_Key()
    {
        var tables = new Dictionary<string, ValueTable> { ["M1_SigA_Table"] = OffOn };
        var row = HilStudioDbcSignalRow.From(NewSigA(), tables);

        row.ValueTableName.Should().Be("M1_SigA_Table");
        row.ValueTableEntries.Should().HaveCount(2);
        row.ValueTableEntries![0].Key.Should().Be(0);   // 升序, 字典无序需显式排序
        row.ValueTableEntries![0].Label.Should().Be("Off");
        row.ValueTableEntries![1].Key.Should().Be(1);
        row.ValueTableEntries![1].Label.Should().Be("On");
    }

    [Fact]
    public void Signal_Without_ValueTable_Or_With_Dangling_Name_Has_Null_Entries()
    {
        var dangling = new Signal(
            "SigC", 0, 8, ByteOrder.LittleEndian, ValueType.Unsigned,
            1, 0, 0, 255, "", Array.Empty<string>(),
            ValueTableName: "NoSuchTable");

        HilStudioDbcSignalRow.From(NewSigB(), new Dictionary<string, ValueTable>())
            .ValueTableEntries.Should().BeNull();
        HilStudioDbcSignalRow.From(dangling, new Dictionary<string, ValueTable>())
            .ValueTableEntries.Should().BeNull();
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~HilStudioProjectionTests"`
Expected: FAIL — 编译错误 `HilStudioDbcMessageRow` / `HilStudioDbcSignalRow` 不存在。

- [ ] **Step 3: 实现投影类**

`src/PeakCan.Host.App/ViewModels/HilStudioDbcMessageRow.cs`:
```csharp
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// 一条 DBC 消息在 HIL Studio DBC Browser 的行投影。纯投影、无行为、无事件。
/// ID 格式化与 DbcMessageViewModel 一致: 标准 11-bit -> "0x123", 扩展 29-bit -> "0x00000123",
/// 去掉 bit31 的 IDE 合并位。
/// </summary>
public sealed class HilStudioDbcMessageRow
{
    /// <summary>原始 Core record, 供 Phase 2/3 结构化消费（约束 #9）。</summary>
    public Message Source { get; init; } = null!;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Dlc { get; init; } = "";
    public string Sender { get; init; } = "";
    public int SignalCount { get; init; }
    public string? Comment { get; init; }
    public IReadOnlyList<HilStudioDbcSignalRow> Signals { get; init; } = Array.Empty<HilStudioDbcSignalRow>();

    public static HilStudioDbcMessageRow From(Message m, IReadOnlyDictionary<string, ValueTable> tables)
    {
        var isExtended = (m.Id & 0x80000000u) != 0;
        var rawId = isExtended ? m.Id & 0x7FFFFFFFu : m.Id;
        var fmt = isExtended ? "X8" : "X3";
        var signals = new List<HilStudioDbcSignalRow>(m.Signals.Count);
        foreach (var s in m.Signals)
            signals.Add(HilStudioDbcSignalRow.From(s, tables));
        return new HilStudioDbcMessageRow
        {
            Source = m,
            Id = $"0x{rawId.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture)}",
            Name = m.Name,
            Dlc = m.Dlc.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Sender = m.Sender,
            SignalCount = m.Signals.Count,
            Comment = m.Comment,
            Signals = signals,
        };
    }
}
```

`src/PeakCan.Host.App/ViewModels/HilStudioDbcSignalRow.cs`:
```csharp
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

/// <summary>一条信号在 HIL Studio DBC Browser 的行投影 + 可选值表展开。</summary>
public sealed class HilStudioDbcSignalRow
{
    public Signal Source { get; init; } = null!;
    public string Name { get; init; } = "";
    public string BitLayout { get; init; } = "";
    public string FactorOffset { get; init; } = "";
    public string MinMax { get; init; } = "";
    public string Unit { get; init; } = "";
    public string? ValueTableName { get; init; }
    /// <summary>值表条目, 按 key 升序。表缺失/悬空引用 -> null（约束 #10 由 UI 收拢）。</summary>
    public IReadOnlyList<HilDbcValueTableEntryRow>? ValueTableEntries { get; init; }

    public static HilStudioDbcSignalRow From(Signal s, IReadOnlyDictionary<string, ValueTable> tables)
    {
        IReadOnlyList<HilDbcValueTableEntryRow>? entries = null;
        if (s.ValueTableName is { } vtName && tables.TryGetValue(vtName, out var vt))
        {
            entries = vt.Entries
                .OrderBy(kv => kv.Key)
                .Select(kv => new HilDbcValueTableEntryRow(kv.Key, kv.Value))
                .ToList();
        }
        return new HilStudioDbcSignalRow
        {
            Source = s,
            Name = s.Name,
            BitLayout = $"{s.StartBit}|{s.Length}@{(s.Order == ByteOrder.LittleEndian ? '1' : '0')}{(s.ValueType == ValueType.Signed ? '-' : '+')}",
            FactorOffset = $"({s.Factor},{s.Offset})",
            MinMax = $"[{s.Min}|{s.Max}]",
            Unit = s.Unit,
            ValueTableName = s.ValueTableName,
            ValueTableEntries = entries,
        };
    }
}

/// <summary>值表里一个 key=label 对。</summary>
public sealed record HilDbcValueTableEntryRow(long Key, string Label);
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~HilStudioProjectionTests"`
Expected: PASS (6 tests)

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/HilStudioDbcMessageRow.cs src/PeakCan.Host.App/ViewModels/HilStudioDbcSignalRow.cs tests/PeakCan.Host.App.Tests/ViewModels/HilStudioProjectionTests.cs
git commit -m "feat(studio): DBC message/signal projection rows with value-table expansion"
```

---

### Task 2: HilStudioViewModel 加载流

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/HilStudioViewModel.cs`
- Create: `src/PeakCan.Host.App/ViewModels/HilStudioViewModel/DbcLoadingFlow.partial.cs`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/HilStudioViewModelTests.cs`

**Interfaces:**
- Consumes: `HilStudioDbcMessageRow.From` / `HilStudioDbcSignalRow.From`（Task 1）；`DbcService`（`Current`、`LoadAsync(string, CancellationToken)`、`DbcLoaded: Action<DbcDocument>?`、`LoadFailed: Action<Error>?`）；`DispatcherExtensions.RunOnUi`；`IFileDialogService.ShowOpenDialog(string) → string?`；`WpfFileDialogService`
- Produces: `HilStudioViewModel`（`ObservableCollection<HilStudioDbcMessageRow> Messages`、`FilteredMessages`、`[ObservableProperty] SearchText/Status/LoadedPath/TotalMessages/TotalSignals/SelectedMessage/SelectedSignal`、`[RelayCommand] OpenCommand`、`void RefreshFromCurrent()`、`partial void OnSelectedMessageChanged`）
- Produces: `void ApplyFilter()`（Task 3 增强；本任务先做"无过滤全量重建"）

- [ ] **Step 1: 写失败测试**

`tests/PeakCan.Host.App.Tests/ViewModels/HilStudioViewModelTests.cs`（文件头部；`EventRaiseExtensions` 已存在于 `DbcViewModelTests.cs` 同命名空间，直接复用）:
```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Tests.ViewModels;

public class HilStudioViewModelTests
{
    private sealed class FakeFileDialogService : IFileDialogService
    {
        public string? NextResult { get; set; }
        public string? ShowOpenDialog(string filter) => NextResult;
        public string? ShowSaveDialog(string filter, string? defaultExt, string? initialDirectory) => NextResult;
    }

    private static HilStudioViewModel NewVm(DbcService svc, IFileDialogService? fileDialog = null)
        => new(svc, NullLogger<HilStudioViewModel>.Instance, fileDialog);

    private static DbcDocument DocWith(params Message[] messages) => new(
        Version: "", Nodes: new List<Node>(),
        Messages: messages, MessagesById: new Dictionary<uint, Message>(),
        ValueTables: new Dictionary<string, ValueTable>(),
        SourcePath: @"C:\test\example.dbc");

    private static void RaiseLoaded(DbcService svc, DbcDocument doc)
        => svc.GetType().GetEvent(nameof(DbcService.DbcLoaded))!.RaiseMethod(svc, doc);

    [Fact]
    public void Default_Status_Is_No_Dbc_Loaded()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var vm = NewVm(svc);

        vm.Status.Should().Be("No DBC loaded");
        vm.LoadedPath.Should().BeEmpty();
        vm.TotalMessages.Should().Be(0);
    }

    [Fact]
    public void DbcLoaded_Event_Populates_Messages_And_Counts()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var vm = NewVm(svc);
        var doc = DocWith(
            new Message(0x100, "M1", 8, "ECU1",
                new List<Signal> { new("S1", 0, 8, ByteOrder.LittleEndian, ValueType.Unsigned, 1, 0, 0, 255, "", Array.Empty<string>()) },
                IsMultiplexed: false, MultiplexorSignalIndex: null),
            new Message(0x200, "M2", 4, "ECU2", new List<Signal>(), false, null));

        RaiseLoaded(svc, doc);

        vm.Messages.Should().HaveCount(2);
        vm.FilteredMessages.Should().HaveCount(2);
        vm.TotalMessages.Should().Be(2);
        vm.TotalSignals.Should().Be(1);
        vm.LoadedPath.Should().Be(@"C:\test\example.dbc");
        vm.Status.Should().Contain("Loaded 2 messages");
    }

    [Fact]
    public void LoadFailed_Event_Sets_Status_To_FAIL()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var vm = NewVm(svc);

        svc.GetType().GetEvent(nameof(DbcService.LoadFailed))!
            .RaiseMethod(svc, new Error(ErrorCode.IoError, "missing file"));

        vm.Status.Should().StartWith("FAIL:");
        vm.Status.Should().Contain("missing file");
    }

    [Fact]
    public void Reload_Clears_And_Repopulates()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var vm = NewVm(svc);
        RaiseLoaded(svc, DocWith(new Message(0x100, "M1", 8, "ECU1", new List<Signal>(), false, null)));

        RaiseLoaded(svc, DocWith());

        vm.Messages.Should().BeEmpty();
        vm.TotalMessages.Should().Be(0);
    }

    [Fact]
    public void RefreshFromCurrent_Seeds_From_Service_Without_Event()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var doc = DocWith(new Message(0x100, "M1", 8, "ECU1", new List<Signal>(), false, null));
        svc.SetCurrentForTests(doc);
        var vm = NewVm(svc);

        vm.RefreshFromCurrent();

        vm.Messages.Should().HaveCount(1);
        vm.Messages[0].Name.Should().Be("M1");
    }

    [Fact]
    public async Task OpenAsync_When_User_Cancels_Does_Nothing()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var dialog = new FakeFileDialogService { NextResult = null };
        var vm = NewVm(svc, dialog);

        await vm.OpenCommand.ExecuteAsync(null);

        vm.Status.Should().Be("No DBC loaded");
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~HilStudioViewModelTests"`
Expected: FAIL — `HilStudioViewModel` 不存在。

- [ ] **Step 3: 实现主 VM + 加载流**

`src/PeakCan.Host.App/ViewModels/HilStudioViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// HIL Configuration Studio 主 VM。Phase 1 只实现 DBC Browser 栏；
/// Phase 2/3 消费 <see cref="SelectedMessage"/> / <see cref="SelectedSignal"/>。
/// 共享单例 DbcService, 事件永不退订（随进程退出）, OnLoaded 幂等重建。
/// </summary>
public sealed partial class HilStudioViewModel : ObservableObject
{
    private readonly DbcService _svc;
    private readonly IFileDialogService _fileDialog;
    private readonly ILogger<HilStudioViewModel> _logger;
    private readonly List<HilStudioDbcMessageRow> _allMessages = new();

    public ObservableCollection<HilStudioDbcMessageRow> Messages { get; } = new();
    public ObservableCollection<HilStudioDbcMessageRow> FilteredMessages { get; } = new();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _status = "No DBC loaded";
    [ObservableProperty] private string _loadedPath = "";
    [ObservableProperty] private int _totalMessages;
    [ObservableProperty] private int _totalSignals;
    [ObservableProperty] private HilStudioDbcMessageRow? _selectedMessage;
    [ObservableProperty] private HilStudioDbcSignalRow? _selectedSignal;

    public HilStudioViewModel(
        DbcService svc,
        ILogger<HilStudioViewModel> logger,
        IFileDialogService? fileDialog = null)
    {
        _svc = svc ?? throw new ArgumentNullException(nameof(svc));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _fileDialog = fileDialog ?? new WpfFileDialogService();
        _svc.DbcLoaded += OnLoaded;
        _svc.LoadFailed += OnLoadFailed;
    }

    /// <summary>切消息时清掉残留的信号选择（防御嵌套 DataGrid 重载写回 null）。</summary>
    partial void OnSelectedMessageChanged(HilStudioDbcMessageRow? value) => SelectedSignal = null;
}
```

`src/PeakCan.Host.App/ViewModels/HilStudioViewModel/DbcLoadingFlow.partial.cs`:
```csharp
using System.IO;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class HilStudioViewModel
{
    [RelayCommand]
    private async Task OpenAsync()
    {
        var path = _fileDialog.ShowOpenDialog("DBC files (*.dbc)|*.dbc|All files|*.*");
        if (path is null) return;
        LoadedPath = path;
        Status = "Parsing...";
        await _svc.LoadAsync(path, CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>种子加载: 窗口打开时若主窗已加载 DBC 则直接显示（镜像 EcuScriptEditorViewModel.LoadInitialPath）。</summary>
    public void RefreshFromCurrent()
    {
        if (_svc.Current is { } doc)
            Rebuild(doc);
    }

    private void OnLoaded(DbcDocument doc) => ((Action)(() => Rebuild(doc))).RunOnUi();

    private void OnLoadFailed(Error error) => ((Action)(() => Status = $"FAIL: {error.Code} {error.Message}")).RunOnUi();

    private void Rebuild(DbcDocument doc)
    {
        Messages.Clear();
        FilteredMessages.Clear();
        _allMessages.Clear();
        foreach (var m in doc.Messages)
        {
            var row = HilStudioDbcMessageRow.From(m, doc.ValueTables);
            Messages.Add(row);
            _allMessages.Add(row);
        }
        TotalMessages = doc.Messages.Count;
        TotalSignals = _allMessages.Sum(r => r.SignalCount);
        LoadedPath = doc.SourcePath ?? LoadedPath;
        Status = $"Loaded {TotalMessages} messages, {TotalSignals} signals from {Path.GetFileName(LoadedPath)}";
        SelectedMessage = null;
        SelectedSignal = null;
        ApplyFilter();
    }
}
```

`DbcSearchFlow.partial.cs`（本任务先放基础版，Task 3 增强过滤逻辑）:
```csharp
namespace PeakCan.Host.App.ViewModels;

public sealed partial class HilStudioViewModel
{
    /// <summary>全量重建过滤集合。Task 3 增强为按 SearchText 过滤；本任务先全显示。</summary>
    private void ApplyFilter()
    {
        FilteredMessages.Clear();
        foreach (var m in _allMessages)
            FilteredMessages.Add(m);
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~HilStudioViewModelTests"`
Expected: PASS (6 tests)

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/HilStudioViewModel.cs src/PeakCan.Host.App/ViewModels/HilStudioViewModel/DbcLoadingFlow.partial.cs src/PeakCan.Host.App/ViewModels/HilStudioViewModel/DbcSearchFlow.partial.cs tests/PeakCan.Host.App.Tests/ViewModels/HilStudioViewModelTests.cs
git commit -m "feat(studio): HilStudioViewModel DBC loading flow (shared DbcService singleton)"
```

---

### Task 3: HilStudioViewModel 搜索 + 选择流

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/HilStudioViewModel/DbcSearchFlow.partial.cs`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/HilStudioViewModelTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `HilStudioViewModel` 全部成员
- Produces: 增强 `ApplyFilter()`（按 `SearchText` 匹配 Message.Name / Sender / Signal.Name，OrdinalIgnoreCase）+ `partial void OnSearchTextChanged`

- [ ] **Step 1: 加失败测试**（追加到 `HilStudioViewModelTests.cs`）

```csharp
    [Fact]
    public void Search_Filters_By_Message_Name_CaseInsensitive()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var vm = NewVm(svc);
        RaiseLoaded(svc, DocWith(
            new Message(0x100, "M1", 8, "ECU1", new List<Signal>(), false, null),
            new Message(0x200, "M2", 4, "ECU2", new List<Signal>(), false, null)));

        vm.SearchText = "m1";

        vm.FilteredMessages.Should().HaveCount(1);
        vm.FilteredMessages[0].Name.Should().Be("M1");
    }

    [Fact]
    public void Search_Filters_By_Sender_And_By_Signal_Name()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var vm = NewVm(svc);
        RaiseLoaded(svc, DocWith(
            new Message(0x100, "M1", 8, "ECU1",
                new List<Signal> { new("Speed", 0, 16, ByteOrder.LittleEndian, ValueType.Unsigned, 1, 0, 0, 6553.5, "", Array.Empty<string>()) },
                IsMultiplexed: false, MultiplexorSignalIndex: null)));

        vm.SearchText = "ECU1";
        vm.FilteredMessages.Should().HaveCount(1);

        vm.SearchText = "speed"; // Signal.Name 匹配（结构化，约束 #6）
        vm.FilteredMessages.Should().HaveCount(1);

        vm.SearchText = "zzz";
        vm.FilteredMessages.Should().BeEmpty();
    }

    [Fact]
    public void Clearing_Search_Restores_All()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var vm = NewVm(svc);
        RaiseLoaded(svc, DocWith(
            new Message(0x100, "M1", 8, "ECU1", new List<Signal>(), false, null),
            new Message(0x200, "M2", 4, "ECU2", new List<Signal>(), false, null)));

        vm.SearchText = "m1";
        vm.FilteredMessages.Should().HaveCount(1);

        vm.SearchText = "";
        vm.FilteredMessages.Should().HaveCount(2);
    }

    [Fact]
    public void Changing_SelectedMessage_Clears_SelectedSignal()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var vm = NewVm(svc);
        RaiseLoaded(svc, DocWith(
            new Message(0x100, "M1", 8, "ECU1",
                new List<Signal> { new("Speed", 0, 16, ByteOrder.LittleEndian, ValueType.Unsigned, 1, 0, 0, 6553.5, "", Array.Empty<string>()) },
                IsMultiplexed: false, MultiplexorSignalIndex: null)));
        vm.SelectedMessage = vm.Messages[0];
        vm.SelectedSignal = vm.Messages[0].Signals[0];
        vm.SelectedSignal.Should().NotBeNull();

        vm.SelectedMessage = null;

        vm.SelectedSignal.Should().BeNull();
    }
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test ... --filter "FullyQualifiedName~HilStudioViewModelTests"`
Expected: FAIL — `SearchText = "m1"` 后 `FilteredMessages` 仍为 2（基础版不过滤）。

- [ ] **Step 3: 实现搜索过滤**（替换 Task 2 的 `DbcSearchFlow.partial.cs` 全部内容）

```csharp
namespace PeakCan.Host.App.ViewModels;

public sealed partial class HilStudioViewModel
{
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>
    /// 全量重建过滤集合。匹配 Message.Name / Sender / Signal.Name（结构化, OrdinalIgnoreCase）。
    /// 注意（约束 #6）：与 DbcViewModel 不同, 这里匹配的是结构化 Signal.Name,
    /// 不是含 bit/scale 的格式化串 —— 行为不等价, 是有意改进。
    /// </summary>
    private void ApplyFilter()
    {
        FilteredMessages.Clear();
        var pattern = SearchText.Trim();
        foreach (var m in _allMessages)
        {
            if (pattern.Length == 0
                || m.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                || m.Sender.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                || m.Signals.Any(s => s.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            {
                FilteredMessages.Add(m);
            }
        }
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test ... --filter "FullyQualifiedName~HilStudioViewModelTests"`
Expected: PASS（10 tests）

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/HilStudioViewModel/DbcSearchFlow.partial.cs tests/PeakCan.Host.App.Tests/ViewModels/HilStudioViewModelTests.cs
git commit -m "feat(studio): HilStudioViewModel search filter + selection-clearing hook"
```

---

### Task 4: HilStudioWindow 壳 + DBC Browser UI

**Files:**
- Create: `src/PeakCan.Host.App/Windows/HilStudioWindow.xaml`
- Create: `src/PeakCan.Host.App/Windows/HilStudioWindow.xaml.cs`

**Interfaces:**
- Consumes: `HilStudioViewModel`（Task 2/3 全部绑定成员）
- Produces: `HilStudioWindow(HilStudioViewModel)` ctor（Task 5 接线用）

- [ ] **Step 1: 写窗口 XAML**（`Windows/HilStudioWindow.xaml`）

```xml
<Window x:Class="PeakCan.Host.App.Windows.HilStudioWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="HIL Configuration Studio" Width="1400" Height="800"
        WindowStartupLocation="CenterOwner">
  <Grid>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width="*" MinWidth="320"/>
      <ColumnDefinition Width="Auto"/>
      <ColumnDefinition Width="*" MinWidth="240"/>
      <ColumnDefinition Width="Auto"/>
      <ColumnDefinition Width="*" MinWidth="240"/>
    </Grid.ColumnDefinitions>

    <!-- ===== col 0: DBC Browser（Phase 1） ===== -->
    <Grid Grid.Column="0" Margin="8">
      <Grid.RowDefinitions>
        <RowDefinition Height="Auto"/>
        <RowDefinition Height="Auto"/>
        <RowDefinition Height="*"/>
      </Grid.RowDefinitions>
      <StackPanel Orientation="Horizontal">
        <Button Content="Open DBC..." Command="{Binding OpenCommand}" Padding="8,2"/>
        <TextBlock Text="{Binding Status}" Margin="8,3,0,0" VerticalAlignment="Center"/>
      </StackPanel>
      <StackPanel Orientation="Horizontal" Grid.Row="1" Margin="0,6,0,4">
        <TextBlock Text="Search:" VerticalAlignment="Center"/>
        <TextBox Text="{Binding SearchText, UpdateSourceTrigger=PropertyChanged}" Width="200" Margin="4,0"/>
        <TextBlock Text="{Binding FilteredMessages.Count, StringFormat='{}{0} shown'}"
                   VerticalAlignment="Center" Margin="4,0,0,0" Foreground="Gray"/>
      </StackPanel>
      <DataGrid Grid.Row="2" ItemsSource="{Binding FilteredMessages}"
                IsReadOnly="True" EnableRowVirtualization="True" RowHeight="20"
                AlternatingRowBackground="#F8F8F8"
                RowDetailsVisibilityMode="VisibleWhenSelected"
                SelectedItem="{Binding SelectedMessage, Mode=TwoWay}"
                AutoGenerateColumns="False">
        <DataGrid.Columns>
          <DataGridTextColumn Header="ID" Binding="{Binding Id}" Width="110"/>
          <DataGridTextColumn Header="Name" Binding="{Binding Name}" Width="*"/>
          <DataGridTextColumn Header="DLC" Binding="{Binding Dlc}" Width="50"/>
          <DataGridTextColumn Header="Sender" Binding="{Binding Sender}" Width="110"/>
          <DataGridTextColumn Header="Signals" Binding="{Binding SignalCount}" Width="70"/>
          <DataGridTextColumn Header="Comment" Binding="{Binding Comment}" Width="*">
            <DataGridTextColumn.ElementStyle>
              <Style TargetType="TextBlock">
                <Setter Property="Foreground" Value="Gray"/>
                <Setter Property="FontStyle" Value="Italic"/>
              </Style>
            </DataGridTextColumn.ElementStyle>
          </DataGridTextColumn>
        </DataGrid.Columns>
        <DataGrid.RowDetailsTemplate>
          <DataTemplate>
            <DataGrid ItemsSource="{Binding Signals}" IsReadOnly="True"
                      EnableRowVirtualization="True" RowHeight="20"
                      RowDetailsVisibilityMode="VisibleWhenSelected"
                      SelectedItem="{Binding DataContext.SelectedSignal,
                                    RelativeSource={RelativeSource AncestorType=Window}, Mode=TwoWay}"
                      AutoGenerateColumns="False">
              <DataGrid.Columns>
                <DataGridTextColumn Header="Name" Binding="{Binding Name}" Width="150"/>
                <DataGridTextColumn Header="Bits" Binding="{Binding BitLayout}" Width="110"/>
                <DataGridTextColumn Header="Scale" Binding="{Binding FactorOffset}" Width="90"/>
                <DataGridTextColumn Header="Range" Binding="{Binding MinMax}" Width="110"/>
                <DataGridTextColumn Header="Unit" Binding="{Binding Unit}" Width="70"/>
                <DataGridTextColumn Header="Value Table" Binding="{Binding ValueTableName}" Width="110"/>
                <DataGridTextColumn Header="Comment" Binding="{Binding Comment}" Width="*">
                  <DataGridTextColumn.ElementStyle>
                    <Style TargetType="TextBlock">
                      <Setter Property="Foreground" Value="Gray"/>
                      <Setter Property="FontStyle" Value="Italic"/>
                    </Style>
                  </DataGridTextColumn.ElementStyle>
                </DataGridTextColumn>
              </DataGrid.Columns>
              <!-- 值表展开：信号级嵌套 DataGrid 的 RowDetailsTemplate（约束 #10） -->
              <DataGrid.RowDetailsTemplate>
                <DataTemplate>
                  <Border Visibility="{Binding ValueTableEntries,
                              Converter={StaticResource NullToVisibilityConverter}}"
                          Margin="16,4,4,4">
                    <StackPanel>
                      <TextBlock Text="{Binding ValueTableName, StringFormat='Value Table: {0}'}"
                                 FontWeight="SemiBold" FontSize="11"/>
                      <ItemsControl ItemsSource="{Binding ValueTableEntries}">
                        <ItemsControl.ItemTemplate>
                          <DataTemplate>
                            <StackPanel Orientation="Horizontal" Margin="0,1">
                              <TextBlock Text="{Binding Key}" FontFamily="Consolas" Foreground="Gray" Width="48"/>
                              <TextBlock Text=" = "/>
                              <TextBlock Text="{Binding Label}"/>
                            </StackPanel>
                          </DataTemplate>
                        </ItemsControl.ItemTemplate>
                      </ItemsControl>
                    </StackPanel>
                  </Border>
                </DataTemplate>
              </DataGrid.RowDetailsTemplate>
            </DataGrid>
          </DataTemplate>
        </DataGrid.RowDetailsTemplate>
      </DataGrid>
    </Grid>

    <GridSplitter Grid.Column="1" Width="5" HorizontalAlignment="Stretch"
                  VerticalAlignment="Stretch" Background="#CCCCCC"
                  ResizeBehavior="PreviousAndNext"/>

    <!-- ===== col 2: Test Suite Builder 占位 ===== -->
    <Border Grid.Column="2" Background="#FAFAFA" BorderBrush="#DDDDDD"
            BorderThickness="1" Margin="4">
      <StackPanel VerticalAlignment="Center" HorizontalAlignment="Center">
        <TextBlock Text="Test Suite Builder" FontSize="16" FontWeight="SemiBold"/>
        <TextBlock Text="(Phase 2)" Foreground="Gray"/>
      </StackPanel>
    </Border>

    <GridSplitter Grid.Column="3" Width="5" HorizontalAlignment="Stretch"
                  VerticalAlignment="Stretch" Background="#CCCCCC"
                  ResizeBehavior="PreviousAndNext"/>

    <!-- ===== col 4: ECU Simulator 占位 ===== -->
    <Border Grid.Column="4" Background="#FAFAFA" BorderBrush="#DDDDDD"
            BorderThickness="1" Margin="4">
      <StackPanel VerticalAlignment="Center" HorizontalAlignment="Center">
        <TextBlock Text="ECU Simulator" FontSize="16" FontWeight="SemiBold"/>
        <TextBlock Text="(Phase 3)" Foreground="Gray"/>
      </StackPanel>
    </Border>
  </Grid>
</Window>
```

- [ ] **Step 2: 写 code-behind**（`Windows/HilStudioWindow.xaml.cs`）

```csharp
using System.Windows;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Windows;

public partial class HilStudioWindow : Window
{
    public HilStudioWindow(HilStudioViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
```

- [ ] **Step 3: 构建确认编译**

Run: `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 4: Commit**

```bash
git add src/PeakCan.Host.App/Windows/HilStudioWindow.xaml src/PeakCan.Host.App/Windows/HilStudioWindow.xaml.cs
git commit -m "feat(studio): HilStudioWindow 3-column shell + DBC Browser UI (Phase 1)"
```

---

### Task 5: DI 注册 + AppShell 接线 + 测试调用点修复

**Files:**
- Modify: `src/PeakCan.Host.App/Composition/AppHostBuilder.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/AppShellViewModel/ViewSwitchFlow.cs`
- Modify: `src/PeakCan.Host.App/AppShell.xaml`
- Modify: `tests/PeakCan.Host.App.Tests/ViewModels/AppShellViewModelTests.cs` + `ViewModels/AppShellViewModelMessageBoxPromptTests.cs`

**Interfaces:**
- Consumes: `HilStudioViewModel`（Task 2）、`HilStudioWindow`（Task 4）、`ViewSwitcher.ShowWindow<TWindow>(factory, ref cache)`
- Produces: AppShell View 菜单 "HIL Configuration Studio" → `ShowHilStudioCommand`

- [ ] **Step 1: AppHostBuilder 注册 VM**

`Composition/AppHostBuilder.cs`:
- 在 line 304 `builder.Services.AddSingleton<ViewModels.EcuScriptEditorViewModel>();` 后加：
```csharp
        builder.Services.AddSingleton<ViewModels.HilStudioViewModel>();
```
- 在 line 356 `sp.GetRequiredService<ViewModels.EcuScriptEditorViewModel>(),` 后加：
```csharp
            sp.GetRequiredService<ViewModels.HilStudioViewModel>(),
```

- [ ] **Step 2: AppShellViewModel ctor + cache 字段**

`ViewModels/AppShellViewModel.cs`:
- 在 line 284 `EcuScriptEditorViewModel ecuScriptEditorViewModel,`（**必选参数段末尾**, 可选参数 `IChannelEnumerator?`/`IConfiguration?` 之前）后加：
```csharp
        HilStudioViewModel hilStudioViewModel,
```
- 在字段区（`_ecuScriptEditorViewModel` 声明旁）加：
```csharp
    private readonly HilStudioViewModel _hilStudioViewModel;
```
- 在 line 314 `_ecuScriptEditorViewModel = ecuScriptEditorViewModel ?? ...` 后加：
```csharp
        _hilStudioViewModel = hilStudioViewModel ?? throw new ArgumentNullException(nameof(hilStudioViewModel));
```
- 在窗口 cache 字段区（`_ecuScriptEditorWindow` 旁）加：
```csharp
    private HilStudioWindow? _hilStudioWindow;
```
（若 `_ecuScriptEditorWindow` cache 字段不在本文件而在 `ViewSwitchFlow.cs`，则放在同文件的 ShowHilStudio 命令所在 partial 的字段区。）

- [ ] **Step 3: ViewSwitchFlow 加 ShowHilStudio 命令**

`ViewModels/AppShellViewModel/ViewSwitchFlow.cs`（镜像同文件 `ShowEcuScriptEditorWindow` 骨架）：
```csharp
    [RelayCommand]
    private void ShowHilStudio()
    {
        var app = System.Windows.Application.Current;
        ViewSwitcher.ShowWindow(
            factory: () =>
            {
                _hilStudioViewModel.RefreshFromCurrent();
                return new HilStudioWindow(_hilStudioViewModel);
            },
            cache: ref _hilStudioWindow);
        if (_hilStudioWindow is not null)
        {
            _hilStudioWindow.Owner = app?.MainWindow;
            if (!_hilStudioWindow.IsVisible) _hilStudioWindow.Show();
            else _hilStudioWindow.Activate();
        }
    }
```

- [ ] **Step 4: AppShell.xaml 菜单**

在 View 菜单 "ECU Script Editor" MenuItem 后加：
```xml
        <MenuItem Header="HIL Configuration Studio" Command="{Binding ShowHilStudioCommand}" />
```

- [ ] **Step 5: 修复测试构造调用点**

Run: `grep -rn "new AppShellViewModel(" tests/PeakCan.Host.App.Tests/`
（当前命中：`AppShellViewModelTests.cs` 的 107/437/535/674/976/1077 行 + `AppShellViewModelMessageBoxPromptTests.cs` 的 128 行；以 grep 实际结果为准。）
在每个 `new AppShellViewModel(...)` 调用的 `EcuScriptEditorViewModel` 实参之后，插入：
```csharp
new HilStudioViewModel(new FakeDbcService(), NullLogger<HilStudioViewModel>.Instance),
```
（`FakeDbcService` 嵌套类已存在于各测试文件；`NullLogger<HilStudioViewModel>` 需加 `using Microsoft.Extensions.Logging.Abstractions;` —— 若该文件已引用则直接使用。）

- [ ] **Step 6: 构建 + 全测试**

Run: `dotnet build D:\claude_proj2\peakcan-host\PeakCan.Host.slnx`
Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj`
Expected: BUILD SUCCEEDED + 全部测试 PASS（AppShellViewModelTests / MessageboxPromptTests 编译通过）

- [ ] **Step 7: Commit**

```bash
git add src/PeakCan.Host.App/Composition/AppHostBuilder.cs src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs src/PeakCan.Host.App/ViewModels/AppShellViewModel/ViewSwitchFlow.cs src/PeakCan.Host.App/AppShell.xaml tests/PeakCan.Host.App.Tests/ViewModels/AppShellViewModelTests.cs tests/PeakCan.Host.App.Tests/ViewModels/AppShellViewModelMessageBoxPromptTests.cs
git commit -m "feat(studio): wire HIL Configuration Studio into AppShell (DI + menu + cache)"
```

---

### Task 6: 端到端验证

**Files:**
- 无代码变更；运行验证

- [ ] **Step 1: 全量测试**

Run: `dotnet test D:\claude_proj2\peakcan-host\PeakCan.Host.slnx`
Expected: 全绿

- [ ] **Step 2: 手动验收清单**（启动 `PeakCan.Host.App`）
  1. View 菜单 → "HIL Configuration Studio" 打开非模态窗口；再点 → 复用缓存并 Activate；关闭重开 → 新实例
  2. 主窗 DBC 选项卡先加载一个 .dbc → 打开 Studio → DBC Browser 自动显示（`RefreshFromCurrent`）
  3. Studio 内 "Open DBC..." 加载 → Status 显示 `Loaded N messages, M signals from <file>`
  4. 主窗 DBC 重新加载 → Studio 自动刷新（共享单例）
  5. 选消息行 → RowDetails 展开信号表（Name/Bits/Scale/Range/Unit/Comment）
  6. 选带 VAL_ 的信号 → 二级展开显示 `Value Table: <name>` + key 升序 key = label；无值表信号展开区收拢
  7. 搜索消息名/发送方/信号名 substring 实时过滤 + "N shown" 计数；清空恢复
  8. col2/col4 显示 "(Phase 2)"/"(Phase 3)"；拖两个 GridSplitter 正常
  9. 损坏 DBC → Status 显示 `FAIL: <Code> <Message>`，不崩溃
- [ ] **Step 3: 若有 3/4 项失败** → 定位修复并重跑；全过则 Phase 1 完成。

---

## Self-Review（写完即查）

- **Spec 覆盖**：Phase 1 全部 spec 条目（7 新建文件 + 5 生产修改 + 2 测试修改 + VM 要点 + DataGrid 列 + 接线顺序 + 约束 #5-10）→ 已映射到 Task 1-6。约束 #1-4 是 Phase 3 项，本计划不涉及。
- **Placeholder 扫描**：无 TBD/TODO；所有测试与实现代码为完整可编译块。唯一"以 grep 实际结果为准"是 8 处调用点数量——这是现有代码的事实性未知（需实现时确认），不是占位。
- **类型一致性**：`HilStudioDbcMessageRow.From(Message, IReadOnlyDictionary<string,ValueTable>)` / `HilStudioDbcSignalRow.From` / `HilDbcValueTableEntryRow(long, string)` 在 Task 1 定义、Task 2 Rebuild 消费，签名一致。`ApplyFilter()` Task 2 基础版 → Task 3 增强，同一签名。`HilStudioWindow(HilStudioViewModel)` Task 4 定义、Task 5 使用一致。
