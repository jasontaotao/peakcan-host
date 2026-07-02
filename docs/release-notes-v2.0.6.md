# v2.0.6 PATCH — 3 bug fixes + 1 UI tidy (2026-07-02)

## Summary

Four user-reported issues closed in a single PATCH:

| # | Severity | Symptom | Root cause | Fix |
|---|----------|---------|------------|-----|
| 1 | CRITICAL | 点击 DID Read，peakcan 没接 ECU 时程序卡死闪退 | WPF VM 的 `await ... .ConfigureAwait(false)` 把 catch + finally 拖到线程池；接着写 `row.IsReading`、`ObservableCollection.Add(...)`、`RelayCommand.NotifyCanExecuteChanged()` → 跨线程 binding 访问 | 移除 5 个 UDS VM 的 `ConfigureAwait(false)`，让 WPF SynchronizationContext 保持 UI dispatcher 上的续延 |
| 2 | HIGH | 加载 ODX-D 后 DIDs / Routines 表格无变化 | `DidPanelViewModel` / `RoutinePanelViewModel` 只在 ctor 里把 `didDb.All` 抄进 ObservableCollection；`UdsViewModel.LoadOdxAsync` 只调了 `Dtc.RefreshFromDatabase()`，注释还撒谎说 Did/Routine 自己会刷新 | 两个 panel 加 `RefreshFromDatabase()`，`LoadOdxAsync` 三个 panel 一起刷新 |
| 3 | HIGH | 加载 DBC 后 Send view 的 DBC mode ComboBox 无 message 可选 | `DbcSendViewModel` **根本没注册到 DI**；`SendViewModel.DbcSend` 拿到 null；XAML `DataContext="{Binding DbcSend}"` 全面板绑到 null | `AppHostBuilder` 加 `AddSingleton<DbcSendViewModel>()` |
| 4 | LOW (UI) | Routines 的 Start / Stop / Query 按钮巨大、形状丑 | `<DockPanel>` 里按钮 StackPanel 没指定 `DockPanel.Dock="Top"`，是 DockPanel 的最后一个子元素 → 默认 fill（横向剩余空间）→ Button 在 fill 横向 StackPanel 里 `HorizontalAlignment=Stretch` → 拉满整个底部 | 重排：按钮 StackPanel 改用 `DockPanel.Dock="Top"`，DataGrid 改为 fill（与 DTC tab 一致）；按钮 `MinWidth="72"` + `Padding="8,3"` 让形状正常 |

**附带修复**：v2.0.4 PATCH 重构 `DtcDop` 后，`Core.Tests/Uds/Odx/MapperTests.cs` 的 `DtcDop.TryMap` 测试成为 stale code（编译不过）。v2.0.6 同步更新到 `Enumerate + IndexInlineDtcs` 两段式 API，把 `SHORT-NAME` 从 DTC-DOP 移到 DTC 元素（与 `DtcDop.TryMapSingle` 一致）。

## Bug-1 详解：WPF VM ConfigureAwait(false) crash

```csharp
// PRE-v2.0.6 (DidPanelViewModel.ReadDidAsync):
var data = await _udsClient.ReadDataByIdentifierAsync(row.Id).ConfigureAwait(false);
//      ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
//      这一行让 await 之后的代码（包括 catch 和 finally）跑在 threadpool 上
row.ReadValue = ...;                                  // 跨线程写 ObservableProperty
...
finally {
    row.IsReading = false;                            // 跨线程写 ObservableProperty
    ReadDidCommand.NotifyCanExecuteChanged();         // 跨线程 RelayCommand
}

// POST-v2.0.6:
var data = await _udsClient.ReadDataByIdentifierAsync(row.Id);
//      WPF SynchronizationContext 把续延 keep 在 UI dispatcher 线程
```

`UdsClient` 的方法体（Core 层）**仍然**用 `ConfigureAwait(false)`，这是对的——Core 层不依赖 UI。但 VM 层 await 的时候 WPF 的 SynchronizationContext 会自动捕获，**VM 不应该再用 `ConfigureAwait(false)` 把它丢掉**。v2.0.6 在 5 个 VM 文件里（Did / Routine / Dtc / Session）的 RelayCommand 入口移除 `ConfigureAwait(false)`。

**保留** `ConfigureAwait(false)` 的位置：`SessionPanelViewModel.ToggleTesterPresent` 里的 `Task.Run(async () => { ... })` lambda——这是故意跑在后台线程的循环（TesterPresent 2 秒间隔），不是 RelayCommand 入口。

## Bug-2 详解：ODX import 刷新链路

```csharp
// PRE-v2.0.6 (UdsViewModel.LoadOdxAsync):
await OdxImport.ImportAsync(dialog.FileName);
Dtc.RefreshFromDatabase();
// Did / Routine 的 ObservableCollection 在 ctor 里 populate 完后就没人管了
// 注释还撒谎："Did/Routine panels refresh themselves via their existing
// Load commands; future DI wiring exposes explicit refresh." —— 根本不存在

// POST-v2.0.6:
await OdxImport.ImportAsync(dialog.FileName);
Did.RefreshFromDatabase();      // 新方法
Routine.RefreshFromDatabase();   // 新方法
Dtc.RefreshFromDatabase();      // 已存在
```

`DidPanelViewModel.RefreshFromDatabase` / `RoutinePanelViewModel.RefreshFromDatabase` mirror `DtcPanelViewModel.RefreshFromDatabase`：从 `_didDb.All` / `_routineDb.All` 重新填 ObservableCollection。`DidDatabase.AddRange` 是 last-wins on duplicate id，所以 ODX 重复导入同文件不会出现重复行。

## Bug-3 详解：DI 漏注册

```csharp
// PRE-v2.0.6 (AppHostBuilder.cs):
builder.Services.AddSingleton<CyclicDbcSendService>();
builder.Services.AddSingleton<ICyclicDbcSendService>(sp =>
    sp.GetRequiredService<CyclicDbcSendService>());
// ← 这里漏了 DbcSendViewModel 的注册
// ↓
// SendViewModel 构造: DbcSendViewModel? dbcSend = null
// SendViewModel.DbcSend = null
// ↓
// SendView.xaml:
//   <StackPanel DataContext="{Binding DbcSend}">  ← 绑到 null
//     <ComboBox ItemsSource="{Binding DbcMessages}" />  ← 空
//     <DataGrid  ItemsSource="{Binding SignalRows}"  />  ← 空
//     <Button    Command="{Binding SendCommand}"    />  ← 无效

// POST-v2.0.6:
builder.Services.AddSingleton<DbcSendViewModel>();
```

`DbcService.DbcLoaded` event handler 一直就在 `DbcSendViewModel` ctor 里订阅，所以 DBC 一加载 `DbcMessages` 就被填——只是因为 VM 是 null，这条路径根本没机会运行。

## Bug-4 详解：DockPanel 布局

PRE-v2.0.6 的 Routines tab XAML：

```xml
<DockPanel>
    <DataGrid DockPanel.Dock="Top" Height="200" .../>      <!-- top dock -->
    <StackPanel Orientation="Horizontal" Margin="8">       <!-- LAST CHILD → fill -->
        <Button Content="Start" .../>
        <Button Content="Stop"  .../>
        <Button Content="Query" .../>
    </StackPanel>
</DockPanel>
```

StackPanel 是 DockPanel 最后一个子元素，没指定 `DockPanel.Dock`，**默认 fill**——占据 DataGrid 下方所有剩余空间（横向拉满）。Button 在 fill 横向 StackPanel 里 `HorizontalAlignment=Stretch` 默认值——三个按钮被拉成等宽填满整个底部，看起来又胖又丑。

POST-v2.0.6：把 StackPanel 移到 DataGrid 之前并加上 `DockPanel.Dock="Top"`；DataGrid 改为 fill（去掉固定 Height=200）；按钮加 `MinWidth="72"` + `Padding="8,3"` 让形状对齐。

## Test counts

| Suite | v2.0.5 | v2.0.6 | Δ |
|-------|--------|--------|---|
| Core  | 384    | 384    | 0 (重写 MapperTests 用 v2.0.4 API，无新增) |
| App   | 445    | 456    | +11（11 个新测试，0 fail） |
| Infra | 84     | 84     | 0 |
| **Total** | **913 + 6 SKIP** | **924 + 6 SKIP** | +11 |

### 新增测试清单

| File | Test | 覆盖 bug |
|------|------|---------|
| `UdsViewModelCrashRegressionTests.cs` (new) | `ReadDidCommand_With_NoEcu_Exception_DoesNotCrash_AndClearsIsReading` | #1 |
|  | `WriteDidCommand_With_NoEcu_Exception_DoesNotCrash` | #1 |
|  | `ReadDtcsCommand_With_NoEcu_Exception_DoesNotCrash` | #1 |
|  | `ClearDtcsCommand_With_NoEcu_Exception_DoesNotCrash` | #1 |
|  | `StartRoutineCommand_With_NoEcu_Exception_DoesNotCrash_AndSetsStatusFailed` | #1 |
|  | `SetDefaultSessionCommand_With_NoEcu_Exception_DoesNotCrash` | #1 |
|  | `SecurityAccessCommand_With_NoEcu_Exception_DoesNotCrash_AndClearsSecurityLevel` | #1 |
| `OdxPanelRefreshTests.cs` (new) | `DidPanelViewModel_RefreshFromDatabase_AddsNewRowsFromDatabase` | #2 |
|  | `DidPanelViewModel_RefreshFromDatabase_OverridesExistingRows_LastWins` | #2 |
|  | `RoutinePanelViewModel_RefreshFromDatabase_AddsNewRowsFromDatabase` | #2 |
| `DbcSendViewModelRegistrationTests.cs` (new) | `DbcSendViewModel_IsRegisteredInDi_AndWiredIntoSendViewModel` | #3 |

## Files changed (10)

### 生产代码（5）
- `src/PeakCan.Host.App/ViewModels/Uds/DidPanelViewModel.cs` (M: +30 lines — store `_didDb` + RefreshFromDatabase)
- `src/PeakCan.Host.App/ViewModels/Uds/RoutinePanelViewModel.cs` (M: +22 lines — store `_routineDb` + RefreshFromDatabase)
- `src/PeakCan.Host.App/ViewModels/Uds/DtcPanelViewModel.cs` (M: 4 lines changed — remove ConfigureAwait(false))
- `src/PeakCan.Host.App/ViewModels/Uds/SessionPanelViewModel.cs` (M: 4 lines changed — remove ConfigureAwait(false), keep Task.Run lambda)
- `src/PeakCan.Host.App/ViewModels/Uds/DidPanelViewModel.cs` (M: 2 lines changed — remove ConfigureAwait(false))
- `src/PeakCan.Host.App/ViewModels/Uds/RoutinePanelViewModel.cs` (M: 1 line changed — remove ConfigureAwait(false))
- `src/PeakCan.Host.App/ViewModels/Uds/UdsViewModel.cs` (M: 8 lines changed — LoadOdxAsync calls all 3 refreshes)
- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` (M: +12 lines — AddSingleton<DbcSendViewModel>)
- `src/PeakCan.Host.App/Views/UdsView.xaml` (M: reorder Routines tab DockPanel, MinWidth/Padding)

### 测试代码（3）
- `tests/PeakCan.Host.App.Tests/ViewModels/Uds/UdsViewModelCrashRegressionTests.cs` (+ ~180 lines, 7 tests)
- `tests/PeakCan.Host.App.Tests/ViewModels/Uds/OdxPanelRefreshTests.cs` (+ ~90 lines, 3 tests)
- `tests/PeakCan.Host.App.Tests/ViewModels/DbcSendViewModelRegistrationTests.cs` (+ ~40 lines, 1 test)
- `tests/PeakCan.Host.Core.Tests/Uds/Odx/MapperTests.cs` (M: rewrite DtcDopMappingTests for v2.0.4 API)

### 文档
- `docs/release-notes-v2.0.6.md` (+)
- `docs/user-manual.html` §11.x: 待 v2.0.6 ship 后增补一句"ODX import 后 DIDs/Routines 立即刷新"

## Pre-ship review

- **0C / 0H / 0M / 0L**
- self-review 重点：
  - 5 个 ConfigureAwait(false) 删除位置都是 RelayCommand 入口（async Task 方法体），不是 Task.Run lambda 内部——`SessionPanelViewModel.ToggleTesterPresent` 的 `Task.Run(async () => { ... })` lambda 故意保留 ConfigureAwait(false)，因为整个 lambda 本来就跑在后台线程
  - DidDatabase.AddRange 是 last-wins on duplicate id，所以重复导入同 ODX 文件不会出现重复行（被 DidPanelViewModel_RefreshFromDatabase_OverridesExistingRows_LastWins 测试锁定）
  - DbcSendViewModel 的 DbcService.DbcLoaded event 订阅在 ctor 里，DI singleton 生命周期与 DbcService 一致，不会泄漏
  - DockPanel 重排不影响 DataGrid 行为（只是从固定 Height=200 改为 fill，响应式更好）
- v2.0.6 派 code-reviewer：**YES**——4 个 bug 涉及 WPF threading + DI 生命周期 + XAML 布局，比 v2.0.5 的 1 个 ODX 算法更杂，应该走完整 review

## Ship method

延续 Tier 3 fallback（github.com:443 不通）；如无新网络恢复迹象，预计 10-call pipeline。