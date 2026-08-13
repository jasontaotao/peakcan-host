# Trace 会话状态剥离 + VM 生命周期 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 4 组会话级状态（master source id / 全局 filter / watch list / signal groups）从 `TraceViewerViewModel` 剥离到新的 `ITraceSessionService`，让 VM 改为窗口级生命周期（transient）、删除 `Reset()`。

**Architecture:** 新建 singleton `ITraceSessionService` 作为会话数据的唯一 home，同时承接「打开会话」操作。`TraceViewerViewModel` 用**属性转发**（同名属性 get/set 转发到 service）保持内部 40+ 处引用零改动，仅改属性定义。`AppShellViewModel` 打开会话走 service、保存会话走窗口引用、开窗用 `Func<TraceViewerViewModel>` 工厂。

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection, xUnit + NSubstitute + FluentAssertions。

## Global Constraints

- 不动 `PeakCan.Host.Core` / `PeakCan.Host.Infrastructure` 层（NetArchTest 边界）。
- 每个 task 结束时项目必须可编译、该 task 涉及测试可跑（`dotnet build` 不因后续 task 未做而失败）。
- 生产代码注释：面向用户/业务逻辑中文，技术 API/接口英文。
- 提交信息 conventional commits，不加 Co-Authored-By。
- 本 Phase 只处理 Trace 侧；`ReplayViewModel` / `ReplaySessionAutoSaver` 保持 singleton 不动（记录为后续项）。

## 执行者须知（本计划自包含，无需任何外部文档）

### 核心决策（先读懂再动手）

`TraceViewerViewModel` 被做成进程级单例，导致它同时背两类状态：

- **会话级状态**（关了窗口也须保留）：master 通道、全局 CAN 过滤、watch list、signal 分组。
- **窗口级状态**（关了就该丢）：播放进度、图表缩放、聊天、锚点线。

本计划把 4 组会话级状态搬到新的 singleton `ITraceSessionService`，让 VM 改 transient、删 `Reset()`。

**核心手法 = 属性转发**：VM 保留同名属性 `WatchedSignals` / `SignalGroups` / `MasterSourceId` / `CanIdFilter`，但 get/set 转发到 service。这样 VM 内部 40+ 处对这些属性的读写**一行都不用改**，只改属性定义本身（见 Task 3）。

### 改前必读文件 + 当前代码精确位置

| 字段/方法 | 当前位置 | 现状 |
|---|---|---|
| `_masterSourceId`（`[ObservableProperty]`） | `ViewModels/TraceViewerViewModel.cs:112` | `private string _masterSourceId = "";` |
| `_canIdFilter`（`[ObservableProperty]`） | `ViewModels/TraceViewerViewModel.cs:128` | `private string _canIdFilter = "";` |
| `Signals`（`TraceSignalRow`） | `ViewModels/TraceViewerViewModel.cs:149` | legacy 集合，**不迁移** |
| `WatchedSignals`（`WatchedSignalRow`） | `ViewModels/TraceViewerViewModel.cs:157` | `public ObservableCollection<WatchedSignalRow> WatchedSignals { get; } = new();` |
| `SignalGroups`（`WatchedSignalGroup`） | `ViewModels/TraceViewerViewModel/ChatToolContextFlow.cs:26` | `public ObservableCollection<WatchedSignalGroup> SignalGroups { get; } = new();`（注意在 ChatToolContextFlow.cs，不在主文件） |
| `Reset()` | `TraceViewerViewModel.cs:291-343` | 待删除 |
| `Dispose()` | `TraceViewerViewModel.cs:406-420` | 待扩展（补集合订阅反注册） |
| ctor 末段集合订阅 | `TraceViewerViewModel.cs:237、241` | `WatchedSignals.CollectionChanged += ...`（匿名 lambda + 方法组） |
| `OpenSessionAsync` / `ApplySnapshotAsync` | `ViewModels/TraceViewerViewModel/SessionFlow.cs:51-57、188-351` | 待迁到 service |
| `SaveSessionAsync` / `BuildSnapshotAsync` | `SessionFlow.cs:33-38、91-174` | **留在 VM** |
| `AppShellViewModel` ctor | `AppShellViewModel.cs:254-286`，第 19 参 line 273 | `TraceViewerViewModel traceViewerViewModel` |
| AppShell 3 命令 | `AppShellViewModel/SessionFlow.cs:43、76、94` | 调 `_traceViewerViewModel.OpenSessionAsync/SaveSessionAsync` |
| `ShowTraceViewer` | `AppShellViewModel/ViewSwitchFlow.cs:187-256` | `new TraceViewerView(_traceViewerViewModel)` line 207；`Closed += ... Reset()` line 218 |
| AppShellViewModel 工厂 | `Composition/AppHostBuilder.cs:327-356`，line 344 | `sp.GetRequiredService<TraceViewerViewModel>()` |
| VM 注册 | `Composition/AppHostBuilder/ViewModelsBatch2Flow.cs:80` | `AddSingleton<TraceViewerViewModel>()` |
| Trace autosaver | `Services/Trace/TraceSessionAutoSaver.cs:120` | `class TraceSessionAutoSaver : SessionAutoSaver<TraceViewerViewModel>` |
| provider 定义 | `Services/Trace/TraceSessionAutoSaver.cs:54-81` | `ITraceViewerViewModelProvider` / `ServiceProviderTraceViewerViewModelProvider` |
| auto-restore | `App.xaml.cs:121、140` | `GetRequiredService<TraceViewerViewModel>()` + `ApplyAutoSnapshotAsync(traceVm, ...)` |

### 硬约束（违反即失败）

- **Save 留 VM，Open 迁 service**：`SaveSessionAsync` / `BuildSnapshotAsync` 读 scrubber/viewports/chart（窗口级状态），**不迁**。只有 `OpenSessionAsync` / `ApplySnapshotAsync` 的会话数据部分迁到 service。
- **Replay 侧不动**：`ReplayViewModel` / `ReplaySessionAutoSaver` / `IReplayViewModelProvider` 保持原样。
- **不能丢日志**：迁移 `ApplySnapshotAsync` 时，保留 `LogRelocated` / `LogSourceMissing` / `LogBundleDbcLoadFailedInline` 的日志语义（这些 `[LoggerMessage]` partial 在 `SourceFlow.cs:196-219`）。迁移后若这些 helper 在 VM 里无人调用，把它们移到 service（连同 `[LoggerMessage]` partial）或保留在 VM——**两条路二选一，不要静默删除日志**。
- **NetArchTest 边界**：App 层不得引用 PEAK SDK。新 `TraceSessionService` 只依赖 `ITraceSessionRegistry` / `TraceSessionLibrary` / `DbcService` / `IAscLocator` / `IAscContentHasher`，均为 App/Core 已有抽象。
- 每个 Task 结束时 `dotnet build PeakCan.Host.slnx -c Debug` 必须通过。

### 测试改动模式（通用，Task 2/3/4 都适用）

测试栈：xUnit + NSubstitute + FluentAssertions。以下模式覆盖绝大多数测试改动：

1. **`TraceViewerViewModel` 测试构造**：新 ctor 第 1 个 required 参数是 `ITraceSessionService session`。现有 `new TraceViewerViewModel(registry, dbcService, logger, sessionLibrary, ...)` 全部改成 `new TraceViewerViewModel(Substitute.For<ITraceSessionService>(), registry, dbcService, logger, sessionLibrary, ...)`。对 `vm.WatchedSignals` / `vm.MasterSourceId` / `vm.CanIdFilter` / `vm.SignalGroups` 的断言，改为先给替身设置返回值再断言替身：
   ```csharp
   var session = Substitute.For<ITraceSessionService>();
   session.WatchedSignals.Returns(new ObservableCollection<WatchedSignalRow>());
   session.SignalGroups.Returns(new ObservableCollection<WatchedSignalGroup>());
   var vm = new TraceViewerViewModel(session, registry, dbc, logger, library, ...);
   // 之后断言 session.Received(1).MasterSourceId = ... 或操作 session.WatchedSignals
   ```

2. **删除 `Reset()` 测试**：`grep -rn "\.Reset()" tests/PeakCan.Host.App.Tests`，删除所有断言 Reset 行为的用例（方法已删，测试必然编译失败）。

3. **`OpenSessionAsync` 测试迁移**：`grep -rn "OpenSessionAsync" tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs`——这些用例要么删掉（功能由 `TraceSessionServiceTests` 覆盖），要么改为断言 `session.Received(1).OpenSessionAsync(path)`。

4. **`AppShellViewModel` 测试构造**：现有 `new AppShellViewModel(...)` 约 20 个参数。把原来的 `traceViewerViewModel` 实参位置换成 `Substitute.For<ITraceSessionService>()`，紧随其后加 `() => Substitute.For<TraceViewerViewModel>()`（`Func<TraceViewerViewModel>` 工厂）。Open/OpenRecent 命令断言改为 `session.Received(1).OpenSessionAsync(path)`；Save 命令断言改为「窗口 DataContext 为 VM 时调 `vm.SaveSessionAsync`，无窗口时不调」。

5. **逐 task 验证**：每改完一个测试文件，跑 `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~<类名>"`。全量验证在 Task 5 做 `dotnet test PeakCan.Host.slnx -c Debug`。

---

### Task 1: 新建 `ITraceSessionService` + `TraceSessionService`

**Files:**
- Create: `src/PeakCan.Host.App/Services/Trace/ITraceSessionService.cs`
- Create: `src/PeakCan.Host.App/Services/Trace/TraceSessionService.cs`
- Test: `tests/PeakCan.Host.App.Tests/Services/Trace/TraceSessionServiceTests.cs`（新建）

**Interfaces:**
- Consumes: `ITraceSessionRegistry`（`Sources` / `LoadAsync` / `UnloadAsync` / `GetService`）、`TraceSessionLibrary`（`Load` / `Save`）、`DbcService`（`LoadAsync` / `Current`）、`IAscLocator`（`LocateAsync`）、`IAscContentHasher`（`ComputeAsync`）—— 均已是 DI 单例。
- Produces: `ITraceSessionService`（下签名），供 Task 2/3/4 使用。

接口（与 spec §5.1 一致）：

```csharp
public interface ITraceSessionService
{
    ObservableCollection<WatchedSignalRow> WatchedSignals { get; }
    ObservableCollection<WatchedSignalGroup> SignalGroups { get; }
    string? MasterSourceId { get; set; }
    string GlobalCanIdFilter { get; set; }
    Task<IReadOnlyList<string>> OpenSessionAsync(string path);
    TraceSessionBundleDto BuildSnapshot();
    bool HasContent { get; }
}
```

实现要点（`TraceSessionService : ObservableObject, ITraceSessionService`）：

- 4 组状态：`WatchedSignals` / `SignalGroups` 是 `ObservableCollection`（get-only）；`MasterSourceId` / `GlobalCanIdFilter` 用 `[ObservableProperty]` 生成 INPC。
- 依赖 `ITraceSessionRegistry _registry`、`TraceSessionLibrary _library`、`DbcService _dbcService`、`IAscLocator _locator`、`IAscContentHasher _hasher`、`ILogger<TraceSessionService> _logger`。
- `HasContent => _registry.Sources.Count > 0`。
- `BuildSnapshot()`：完整实现如下（照搬 `SessionFlow.cs:91-174` 的字段填充，替换窗口级字段为默认值）。类型 `TraceSessionSnapshotBuilder.Scaffold` / `BundleSourceDto` / `BundlePlaybackDto` / `BundleWatchedSignalDto` / `BundleGroupDto` 均已存在，字段名与下面代码一致：

```csharp
public TraceSessionBundleDto BuildSnapshot()
{
    var scaffold = new TraceSessionSnapshotBuilder.Scaffold(
        LoadedFilePath: null,
        CurrentTimestamp: 0.0,     // 窗口级 → 默认
        Speed: 1.0,                // 窗口级 → 默认
        Loop: false,               // 窗口级 → 默认
        StartTimestamp: 0.0,
        EndTimestamp: 0.0,
        CanIdFilterText: GlobalCanIdFilter,
        DbcPath: _dbcService.Current?.SourcePath ?? "");
    var dto = _builder.BuildAsync(scaffold, CancellationToken.None).GetAwaiter().GetResult();

    dto.Sources = new List<BundleSourceDto>(_registry.Sources.Count);
    foreach (var src in _registry.Sources)
    {
        var hash = "";
        if (!string.IsNullOrEmpty(src.Path) && File.Exists(src.Path))
        {
            try { hash = _hasher.ComputeAsync(src.Path, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            { hash = ""; }
        }
        dto.Sources.Add(new BundleSourceDto
        {
            SourceId = src.SourceId,
            DisplayName = src.DisplayName,
            Path = src.Path,
            ColorA = src.Color.A, ColorR = src.Color.R, ColorG = src.Color.G, ColorB = src.Color.B,
            StrokeStyle = src.StrokeStyle.ToString(),
            CanIdFilter = src.CanIdFilter ?? "",
            ContentHash = hash,
        });
    }
    dto.Playback = new BundlePlaybackDto
    {
        MasterSourceId = MasterSourceId ?? "",
        Loop = false, Speed = 1.0, ScrubberValue = 0.0,
        StartTimestamp = null, EndTimestamp = null,
    };
    dto.Viewports = new List<BundleViewportDto>();   // 窗口级 → 空列表
    dto.WatchedSignals = WatchedSignals
        .Where(r => !r.IsPlaceholder)
        .Select(r => new BundleWatchedSignalDto
        {
            CanIdHex = r.CanIdHex, MessageName = r.MessageName, SignalName = r.SignalName,
            Unit = r.Unit, SourceId = r.SourceId, Alias = r.Alias,
        }).ToList();
    dto.Groups = SignalGroups
        .Select(g => new BundleGroupDto
        {
            Id = g.Id, Name = g.Name, Notes = g.Notes, SignalKeys = g.SignalKeys.ToList(),
        }).ToList();
    return dto;
}
```

（`_builder` 是 `TraceSessionSnapshotBuilder`，在 ctor 注入。）

**`OpenSessionAsync` 迁移**：把 `TraceViewerViewModel.ApplySnapshotAsync`（`SessionFlow.cs:188-351`）的**会话数据部分**搬进来：

```csharp
public async Task<IReadOnlyList<string>> OpenSessionAsync(string path)
{
    var dto = await Task.Run(() => _library.Load(path)).ConfigureAwait(false);
    if (dto is null) return Array.Empty<string>();

    var missing = new List<string>();
    foreach (var src in _registry.Sources.ToList())
        await _registry.UnloadAsync(src.SourceId).ConfigureAwait(false);

    var nameBySourceId = dto.Sources.ToDictionary(s => s.SourceId, s => s.DisplayName, StringComparer.Ordinal);

    foreach (var bs in dto.Sources)
    {
        var loadPath = bs.Path;
        if (!string.IsNullOrEmpty(bs.Path) && !File.Exists(bs.Path) && !string.IsNullOrEmpty(bs.ContentHash))
        {
            var relocated = await _locator.LocateAsync(bs.ContentHash).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(relocated) && File.Exists(relocated))
                loadPath = relocated;
        }
        try
        {
            var loaded = await _registry.LoadAsync(loadPath).ConfigureAwait(false);
            loaded.CanIdFilter = bs.CanIdFilter;
            if (!string.IsNullOrEmpty(bs.DisplayName) &&
                bs.DisplayName != Path.GetFileNameWithoutExtension(bs.Path))
                loaded.DisplayName = bs.DisplayName;
            if (!(bs.ColorA == 0 && bs.ColorR == 0 && bs.ColorG == 0 && bs.ColorB == 0))
                loaded.Color = new Color(bs.ColorR, bs.ColorG, bs.ColorB, bs.ColorA);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or ReplayException)
        {
            missing.Add(bs.Path);
        }
    }

    if (!string.IsNullOrEmpty(dto.DbcPath) && File.Exists(dto.DbcPath))
    {
        try { await _dbcService.LoadAsync(dto.DbcPath).ConfigureAwait(false); }
        catch { /* best-effort, log */ }
    }

    GlobalCanIdFilter = dto.GlobalCanIdFilter ?? "";
    if (dto.Playback is { } pb && !string.IsNullOrEmpty(pb.MasterSourceId))
    {
        var newMaster = _registry.Sources.FirstOrDefault(s =>
            string.Equals(s.DisplayName, nameBySourceId.GetValueOrDefault(pb.MasterSourceId, ""), StringComparison.Ordinal));
        if (newMaster is not null) MasterSourceId = newMaster.SourceId;
    }
    return missing;
}
```

**测试**：`TraceSessionServiceTests` 用 NSubstitute 替身 registry/library/dbcService/locator/hasher，断言：`OpenSessionAsync` 的 unload/load 序列、missing 收集、DisplayName/Color 重戳、master 映射。`BuildSnapshot` 断言 sources 数量 + master/filter/watch/groups 字段。

- [ ] **Step 1**: 写 `TraceSessionServiceTests`（先写 `OpenSessionAsync_CollectsMissingPaths` + `BuildSnapshot_IncludesSessionState` 两个核心用例）
- [ ] **Step 2**: 跑测试确认失败（`TraceSessionService` 不存在）
- [ ] **Step 3**: 实现 `ITraceSessionService` + `TraceSessionService`
- [ ] **Step 4**: 跑 `--filter "FullyQualifiedName~TraceSessionServiceTests"` 确认通过
- [ ] **Step 5**: Commit（`feat(trace): add ITraceSessionService`）

---

### Task 2: `AppShellViewModel` 走 service + DI 注册

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs`（ctor 字段 + 参数）
- Modify: `src/PeakCan.Host.App/ViewModels/AppShellViewModel/SessionFlow.cs`（3 命令）
- Modify: `src/PeakCan.Host.App/ViewModels/AppShellViewModel/ViewSwitchFlow.cs`（`ShowTraceViewer`）
- Modify: `src/PeakCan.Host.App/Composition/AppHostBuilder.cs`（`AppShellViewModel` 工厂，约 line 327-356）
- Modify: `src/PeakCan.Host.App/Composition/AppHostBuilder/ViewModelsBatch2Flow.cs`（注册 service）
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/AppShellViewModelTests.cs`

**Interfaces:**
- Consumes: `ITraceSessionService`（Task 1）、`Func<TraceViewerViewModel>`
- Produces: `AppShellViewModel` 新 ctor 签名（Task 3 依赖其不再注入 `TraceViewerViewModel`）

改造点：

1. **ctor**（`AppShellViewModel.cs:254-286`）：删除 `TraceViewerViewModel traceViewerViewModel` 参数，新增 `ITraceSessionService traceSessionService` + `Func<TraceViewerViewModel> traceViewerFactory`。字段 `_traceViewerViewModel` 删除，新增 `_traceSessionService`、`_traceViewerFactory`。

2. **SessionFlow**（`SessionFlow.cs`）：
   - `OpenSessionAsync`（line 43）：`_traceViewerViewModel.OpenSessionAsync(path)` → `_traceSessionService.OpenSessionAsync(path)`。
   - `OpenRecentSessionAsync`（line 94）：同上。
   - `SaveSessionAsync`（line 76）：改为从缓存窗口拿 VM：
     ```csharp
     var vm = _traceViewerView?.DataContext as TraceViewerViewModel;
     if (vm is null) { /* 提示「请先打开 Trace Viewer」，return */ }
     await vm.SaveSessionAsync(path).ConfigureAwait(true);
     ```

3. **ViewSwitchFlow**（`ViewSwitchFlow.cs:206-218`）：
   - `factory: () => new TraceViewerView(_traceViewerViewModel)` → `() => new TraceViewerView(_traceViewerFactory())`。
   - 删除 `_traceViewerView.Closed += (_, _) => _traceViewerViewModel.Reset();`（line 218）。

4. **AppHostBuilder 工厂**（`AppHostBuilder.cs:344`）：`sp.GetRequiredService<TraceViewerViewModel>()` → `sp.GetRequiredService<ITraceSessionService>()`（新 ctor 的对应参数）+ `() => sp.GetRequiredService<TraceViewerViewModel>()`（工厂参数）。

5. **ViewModelsBatch2Flow**：新增 `services.AddSingleton<ITraceSessionService, TraceSessionService>();`（VM 的 transient 化在 Task 3 做）。

**测试**：`AppShellViewModelTests` 更新 ctor 构造（传 `Substitute.For<ITraceSessionService>()` + `Func<TraceViewerViewModel>`）。`OpenSessionAsync` 断言改走 service（`_session.Received(1).OpenSessionAsync(...)`）。`SaveSessionAsync` 断言窗口 VM 路径（无窗口时提示）。

- [ ] **Step 1**: 改 `AppShellViewModel` ctor + SessionFlow + ViewSwitchFlow
- [ ] **Step 2**: 改 `AppHostBuilder` 工厂 + `ViewModelsBatch2Flow` 注册 service
- [ ] **Step 3**: 改 `AppShellViewModelTests`（ctor + 命令断言），跑 `--filter "FullyQualifiedName~AppShellViewModel"` 确认通过
- [ ] **Step 4**: Commit（`refactor(trace): route AppShell session commands through ITraceSessionService`）

---

### Task 3: `TraceViewerViewModel` 状态透传 + transient 化

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs`（属性定义 + ctor + `Dispose`）
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SessionFlow.cs`（删 `OpenSessionAsync`/`ApplySnapshotAsync`；`SaveSessionAsync`/`BuildSnapshotAsync` 保留）
- Modify: `src/PeakCan.Host.App/Composition/AppHostBuilder/ViewModelsBatch2Flow.cs`（`AddSingleton<TraceViewerViewModel>` → `AddTransient<TraceViewerViewModel>`）
- Modify: `src/PeakCan.Host.App/Views/TraceViewerView.xaml.cs`（窗口 `Closed` → dispose VM）
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs`

**Interfaces:**
- Consumes: `ITraceSessionService`（Task 1）
- Produces: `TraceViewerViewModel`（transient，状态转发 service）

改造点：

1. **属性转发**（`TraceViewerViewModel.cs:99-157`）：

```csharp
// WatchedSignals / SignalGroups：get-only 转发（ObservableCollection 引用不变，内容变更由集合自身通知）
public ObservableCollection<WatchedSignalRow> WatchedSignals => _session.WatchedSignals;
public ObservableCollection<WatchedSignalGroup> SignalGroups => _session.SignalGroups;

// MasterSourceId / CanIdFilter：get/set 转发到 service（去掉 [ObservableProperty] + 私有字段）
public string MasterSourceId
{
    get => _session.MasterSourceId ?? "";
    set => _session.MasterSourceId = value;
}
public string CanIdFilter
{
    get => _session.GlobalCanIdFilter;
    set => _session.GlobalCanIdFilter = value;
}
```

2. **ctor**（`TraceViewerViewModel.cs:179-242`）：注入 `ITraceSessionService session`，字段 `_session`。在 ctor 末尾用**具名方法**订阅 service INPC 转发（具名以便 Dispose 反注册，不能用匿名 lambda）：

```csharp
_session.PropertyChanged += OnSessionPropertyChanged;
```

新增具名 handler（放主文件任意位置）：

```csharp
private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
{
    if (e.PropertyName == nameof(ITraceSessionService.MasterSourceId))
        OnPropertyChanged(nameof(MasterSourceId));
    else if (e.PropertyName == nameof(ITraceSessionService.GlobalCanIdFilter))
        OnPropertyChanged(nameof(CanIdFilter));
}
```

3. **删除 `Reset()`**（`TraceViewerViewModel.cs:291-343`）。`Dispose()` 保留并扩展——VM 变 transient 后，**必须反注册所有对 singleton 对象（`_dbcService` / `_session` / `WatchedSignals` 集合）的订阅**，否则 singleton 强引用 VM handler 导致 VM 泄漏。

   (a) ctor 里 `WatchedSignals.CollectionChanged` 的两处订阅（现状 line 237 匿名 lambda、line 241 方法组）：把 line 237 的匿名 lambda 改成具名方法，才能反注册：

   ```csharp
   // ctor 里替换 line 237 的匿名 lambda
   WatchedSignals.CollectionChanged += OnWatchedSignalsCollectionChangedForSamplingTable;

   // 新增具名方法（替代原匿名 lambda，逻辑不变）
   private void OnWatchedSignalsCollectionChangedForSamplingTable(object? sender, NotifyCollectionChangedEventArgs e)
       => RefreshSamplingTable();
   ```

   （line 241 的 `OnWatchedSignalsCollectionChangedForSignalCache` 已是具名方法，不用改。）

   (b) `Dispose()` 里补 3 行反注册（现状已有 `_dbcService.DbcLoaded -= OnDbcLoaded` / `DetachAllServiceHandlers()` / `_registry.SourcesChanged -=` / `_chatCts` 清理）：

   ```csharp
   public void Dispose()
   {
       if (_disposed) return;
       _disposed = true;
       _dbcService.DbcLoaded -= OnDbcLoaded;
       WatchedSignals.CollectionChanged -= OnWatchedSignalsCollectionChangedForSamplingTable;   // 新增
       WatchedSignals.CollectionChanged -= OnWatchedSignalsCollectionChangedForSignalCache;     // 新增
       _session.PropertyChanged -= OnSessionPropertyChanged;                                    // 新增
       DetachAllServiceHandlers();
       _registry.SourcesChanged -= OnRegistrySourcesChanged;
       _chatCts?.Cancel();
       _chatCts?.Dispose();
       _chatCts = null;
       GC.SuppressFinalize(this);
   }
   ```

4. **SessionFlow**：删除 `OpenSessionAsync`（line 51-57）与 `ApplySnapshotAsync`（line 188-351）。`SaveSessionAsync`（line 33-38）与 `BuildSnapshotAsync`（line 91-174）保留——它们读 `WatchedSignals`/`SignalGroups`（现转发 service）与 VM 窗口级状态（`ScrubberValue`/`ChartViewModel`）。

5. **注册**（`ViewModelsBatch2Flow.cs:80`）：`AddSingleton<TraceViewerViewModel>()` → `AddTransient<TraceViewerViewModel>()`。

6. **窗口 dispose**（`TraceViewerView.xaml.cs:15-20` 已有 `Closed += ...`）：扩展为：
   ```csharp
   Closed += (_, _) =>
   {
       UnsubscribeAllCompletedHandlers();
       if (DataContext is IDisposable d) d.Dispose();
   };
   ```

**测试**：`TraceViewerViewModelTests` 大改——ctor 传 `Substitute.For<ITraceSessionService>()`；删 `Reset()` 相关用例；`OpenSessionAsync` 用例移走（或改为断言 `_session.OpenSessionAsync`）；`WatchedSignals`/`MasterSourceId` 断言改走 service 替身。

- [ ] **Step 1**: 改 `TraceViewerViewModel.cs` 属性转发 + ctor + Dispose + 删 Reset
- [ ] **Step 2**: 删 SessionFlow 的 `OpenSessionAsync`/`ApplySnapshotAsync`
- [ ] **Step 3**: 改 `ViewModelsBatch2Flow` 为 AddTransient + 改 `TraceViewerView.xaml.cs` dispose
- [ ] **Step 4**: 改 `TraceViewerViewModelTests`，跑 `--filter "FullyQualifiedName~TraceViewerViewModel"` 确认通过
- [ ] **Step 5**: Commit（`refactor(trace): extract session state to service, make TraceViewerViewModel transient`）

---

### Task 4: Auto-saver + `App.xaml.cs` 改造 + 删 provider

**Files:**
- Modify: `src/PeakCan.Host.App/Services/Trace/TraceSessionAutoSaver.cs`（改依赖 service）
- Modify: `src/PeakCan.Host.App/App.xaml.cs`（auto-restore 拿 service）
- Test: `tests/PeakCan.Host.App.Tests/Services/Trace/TraceSessionAutoSaverTests.cs`（若存在）或 `AppLifecycleShutdownTests.cs`

**Interfaces:**
- Consumes: `ITraceSessionService`（Task 1，提供 `BuildSnapshot`/`OpenSessionAsync`/`HasContent`）

改造点：

1. **`TraceSessionAutoSaver`**：继承 `SessionAutoSaver<TraceViewerViewModel>` 改为继承 `SessionAutoSaver<ITraceSessionService>`，`_vmProvider` 字段改为直接注入 `ITraceSessionService`。override 改：
   - `GetActiveVm()` → 返回 service（或保留 provider 语义：`_services.GetService<ITraceSessionService>()`）
   - `HasContentToSave(svc)` → `svc.HasContent`
   - `BuildSnapshot(svc)` → `svc.BuildSnapshot()`
   - `ApplySnapshotToVmAsync(svc, sourceFile)` → `svc.OpenSessionAsync(sourceFile)`

2. **删除** `ITraceViewerViewModelProvider` / `ServiceProviderTraceViewerViewModelProvider`（`TraceSessionAutoSaver.cs:54-81`，仅 Trace 侧在用）。`ReplaySessionAutoSaver` 的 `IReplayViewModelProvider` 保留不动。

3. **`App.xaml.cs`**（line 121、140）：`var traceVm = Services.GetRequiredService<TraceViewerViewModel>()` 改为 `var traceSession = Services.GetRequiredService<ITraceSessionService>()`；`autoSaver.ApplyAutoSnapshotAsync(traceVm, ...)` 改为 `autoSaver.ApplyAutoSnapshotAsync(traceSession, ...)`。

**测试**：auto-saver 相关测试改断言 service 替身被调（`_session.Received(1).BuildSnapshot()` / `OpenSessionAsync(...)`）。

- [ ] **Step 1**: 改 `TraceSessionAutoSaver` 泛型参数 + override，删 provider
- [ ] **Step 2**: 改 `App.xaml.cs` auto-restore 路径
- [ ] **Step 3**: 改相关测试，跑 `--filter "FullyQualifiedName~AutoSaver|AppLifecycle"` 确认通过
- [ ] **Step 4**: Commit（`refactor(trace): autosaver builds snapshot from service, drop VM provider`）

---

### Task 5: 全量测试修复 + 架构验证

**Files:** 无新增；修复剩余测试编译/断言。

**Interfaces:** 无新产出。

- [ ] **Step 1**: `dotnet build PeakCan.Host.slnx -c Debug` 确认全量编译通过
- [ ] **Step 2**: `dotnet test PeakCan.Host.slnx -c Debug` 全量跑，修复剩余失败（重点：`TraceViewerViewModelTests`、`AppShellViewModelTests`、`TraceSessionLibraryTests`、auto-saver 测试、`AppLifecycleShutdownTests`）
- [ ] **Step 3**: 跑架构测试 `--filter "FullyQualifiedName~LayeringRules"` 确认 NetArchTest 边界仍通过
- [ ] **Step 4**: Commit（`test(trace): fix remaining tests after session-state extraction`）

---

## Self-Review 记录

- **Spec 覆盖**：spec §5.1（service）→ Task 1；§5.3（AppShell）→ Task 2；§5.2（VM 透传/transient/删 Reset/Open 移除）→ Task 3；§5.5（auto-saver）→ Task 4；§8 测试 → Task 1/3/4/5 分散落实。
- **Type 一致性**：`ITraceSessionService` 的 6 个成员签名在 Task 1/2/3/4 一致；`Func<TraceViewerViewModel>` 在 Task 2/3 一致；`BuildSnapshot()`/`OpenSessionAsync(string)` 在 Task 1/4 一致。
- **Task 边界可编译性**：Task 1 新增 service（无消费方，可编译）；Task 2 改 AppShell+DI（VM 仍 singleton，Func 解析 singleton 仍正确）；Task 3 改 VM+transient（AppShell 已走 service，不再依赖 VM.OpenSessionAsync）；Task 4 改 auto-saver。每个边界点都保证 `dotnet build` 通过。
- **已知遗留**：`ReplayViewModel`/`ReplaySessionAutoSaver` 仍为 singleton（spec §5.5 记为后续项）；`SaveSessionAsync` 留在 VM（spec §2 非目标内）。
