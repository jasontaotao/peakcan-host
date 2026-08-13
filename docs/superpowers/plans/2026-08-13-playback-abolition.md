# Playback 废除 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 删除 `TraceViewerViewModel` 侧全部播放残留（`Play/Pause/Stop/SeekTo` 命令、`Loop/Speed/ScrubberValue` 属性、`On*Changed` hooks、帧泵订阅 `AttachAllServiceHandlers`/`DetachAllServiceHandlers`、`Propagate*`、`SeekAllToProportionalTime`），简化 `SetMaster`/`OnRegistrySourcesChanged`/`RebindMasterServiceIfChanged`/`Dispose`，`BuildSnapshotAsync` 播放信封写默认值 + viewports 停止持久化（与 `TraceSessionService.BuildSnapshot` 对齐），并删除图表播放 cursor 死代码。

**Architecture:** 播放功能在 v3.50.4 已从 XAML 移除，本次只清 VM 端残留 + 关联测试。Core 层播放原语、Replay tab、`IChatToolContext.Seek`（Chat 工具 `SeekToTimeTool`）、`BundlePlaybackDto` 字段定义全部保留。删除是原子操作（属性/命令/hooks/帧泵相互引用），按「先删测试 → 再删生产」顺序保持每次 commit 编译通过且测试全绿。

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm, xUnit + NSubstitute + FluentAssertions。

## Global Constraints

- **不动 `PeakCan.Host.Core` / `PeakCan.Host.Infrastructure` 层**（NetArchTest 边界）——`ReplayTimeline` / `ReplayService` / `TraceViewerService` 的 Play/Pause/Stop/Seek/SetSpeed 原语保留。
- **不动 Replay tab**（`ReplayViewModel` 播放功能 + `ReplayViewModelTests` / `ReplayTimelineTests` / `IReplayServiceTests` 全部保留）。
- **不动 `TraceSessionService`**（`BuildSnapshot` 已写默认值、`OpenSessionAsync` 不触碰 playback 标量）。
- **不动 `BundlePlaybackDto` / `BundleViewportDto` / `LoopRegionDto` 字段定义**（Replay 共享）。
- **保留**：`_totalDuration` / `TotalDuration`、`ChartViewModel.SetTotalDuration`、`IChatToolContext.Seek`（`ChatToolContextFlow.cs:100-105`）、`ITraceViewerService.Seek`、`ClearCanIdFilter`、`RebindMasterFromRegistry`、`DetachAllSourcePropertyHandlers`、`OnAnySourcePropertyChanged`、`CaptureViewports` / `ApplyViewports`（TraceChartViewModel 内部机制，spec §5 明确保留）。
- 每次 commit 后对应测试必须全绿；Task 完成时全量 App 测试全绿。
- 生产代码注释：面向用户/业务逻辑用中文，技术 API/接口用英文。
- 提交信息用 conventional commits（`refactor:` / `test:` / `chore:`），不加 Co-Authored-By（全局已禁用 attribution）。
- **不要**把 `docs/superpowers/specs/2026-08-11-hil-case-log-design.md`（工作树中未提交）纳入任何 commit。

## 执行者须知（本计划自包含，无需任何外部文档）

执行前先读这 6 个文件，对照下方「当前代码」定位要改的位置：

1. `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs`
   - 类 doc comment（line 35-40）提及 `PlayCommand/PauseCommand/StopCommand/SeekToCommand`
   - `[ObservableProperty] private double _scrubberValue;`（line 108-109）
   - `[ObservableProperty] private bool _loop = false;`（line 125-126）
   - `[ObservableProperty] private double _speed = 1.0;`（line 129-130）
   - `_allServices` 字段注释（line 97）：「Play/Pause/Stop/Seek iterate this dict」
   - `Dispose()`（line 392-420）内 `DetachAllServiceHandlers();`（line 406）
2. `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/TransportFlow.cs`（整文件删除）
   - `Play()`（21）/`Pause()`（36）/`Stop()`（43）/`SeekTo(double)`（51）四个 `[RelayCommand]`
   - `OnScrubberValueChanged`（63）/`OnLoopChanged`（89）/`OnSpeedChanged`（94）三个 partial hooks
3. `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/LifecycleFlow.cs`（整文件删除）
   - `AttachAllServiceHandlers`（16）/`DetachAllServiceHandlers`（28）/`OnMasterPlaybackEnded`（41）/`OnAnyFrameEmitted`（59）
4. `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/PlaybackFlow.cs`
   - 保留：`ClearCanIdFilter`（11）/`DetachAllSourcePropertyHandlers`（33）/`OnAnySourcePropertyChanged`（46）/`RebindMasterFromRegistry`（92）
   - 删除：`PropagateLoopToAllServices`（15）/`PropagateSpeedToAllServices`（21）/`SeekAllToProportionalTime`（66）
5. `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SourceFlow.cs`
   - `SetMaster`（line 173-194）：`wasPlaying`（178）/`Stop()`（179）/`DetachAllServiceHandlers()+AttachAllServiceHandlers()`（186-187）/`PropagateLoopToAllServices()+PropagateSpeedToAllServices()`（188-189）/`if (wasPlaying) Play()`（193）
   - `OnRegistrySourcesChanged`（line 241-243）：`AttachAllServiceHandlers()` + 两个 Propagate
6. `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SessionFlow.cs`
   - `RebindMasterServiceIfChanged`（line 179-191）：`DetachAllServiceHandlers()+AttachAllServiceHandlers()`（187-188）+ 两个 Propagate（189-190）
   - `BuildSnapshotAsync`（line 66-149）：scaffold 的 `CurrentTimestamp: ScrubberValue`（70）/`Speed: Speed`（71）/`Loop: Loop`（72）；Playback 信封（118-126）写 VM 真实值；`dto.Viewports = new List<BundleViewportDto>(ChartViewModel.CaptureViewports());`（127）
7. `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SamplingTableFlow.cs`
   - `RefreshSamplingTable`：`var targetTs = ScrubberValue;`（line 75）
8. `src/PeakCan.Host.App/ViewModels/TraceChartViewModel/PlaybackFlow.cs` 与 `TraceChartViewModel.cs`
   - `UpdatePlaybackCursor`（PlaybackFlow.cs:21-44）+ 节流字段（13-16）+ `[ObservableProperty] _invalidatePlotCallCount`（18-19）
   - `SetTotalDuration`（46）**保留**
   - `TraceChartViewModel.cs:35` `PlaybackCursorX` 属性
9. `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs`（约 1885 行）
10. `tests/PeakCan.Host.App.Tests/ViewModels/TraceChartViewModelTests.cs`

**硬约束（违反即失败）：**

- **`PlayCommand/PauseCommand/StopCommand/SeekToCommand` 仅在 TraceViewerViewModel 自身文件被引用**（已 grep 确认：`ReplayView.xaml.cs:147` 的 `SeekToCommand` 是 `ReplayViewModel` 的、`MultiFrameSendViewModel`/`FlashPanelViewModel` 的 `StopCommand` 是各自 VM 的——**都别碰**）。
- **`PlaybackCursorX` 无任何 XAML 绑定**（已 grep 全 src `*.xaml` 无匹配）——可安全删除。
- Replay / Core 测试里的 `.Loop` / `.Speed` / `PlayCommand`（`ReplayViewModelTests.cs`、`ReplayTimelineTests.cs`、`IReplayServiceTests.cs`）**全是 Replay 侧，保留**。
- `TraceSessionService.BuildSnapshot`（`TraceSessionService.cs:78-137`）已经是「窗口级 → 默认值」的写法（`Loop=false, Speed=1.0, ScrubberValue=0.0`、`Viewports=空`）——**不要改它**，它是本 Task 2 里 `BuildSnapshotAsync` 的目标形态。
- `OnSourcesChanged_ClearsNonMasterStartEndTimestamps_InMultiTraceMode`（TraceViewerViewModelTests.cs:992）保留——`OnRegistrySourcesChanged` 的 per-source `StartTimestamp/EndTimestamp = null` 循环不删。

---

### Task 1: 删除 TraceViewerViewModel 播放残留 + 调用点清理 + 测试

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs`
- Delete: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/TransportFlow.cs`
- Delete: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/LifecycleFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/PlaybackFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SourceFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SessionFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SamplingTableFlow.cs`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs`

**Interfaces:**
- Produces: `TraceViewerViewModel` 不再有 `ScrubberValue/Loop/Speed` 属性、`PlayCommand/PauseCommand/StopCommand/SeekToCommand` 命令、`AttachAllServiceHandlers/DetachAllServiceHandlers` 方法
- Produces: `SetMaster(string)` 不再调用 Stop/Play/Propagate；`BuildSnapshotAsync` 播放信封写默认值、`Viewports` 为空列表

**删除顺序说明**：删除成员后测试无法编译（播放测试引用被删成员），故**先删测试（Commit A）再删生产（Commit B）**，每个 commit 均编译通过且测试全绿。新的 BuildSnapshot 契约钉住测试（Commit C）锁定删除后的行为。

- [ ] **Step 1: 删除 14 个废弃播放测试 + 重命名 1 个（Commit A）**

在 `TraceViewerViewModelTests.cs` 中按**方法名**删除以下 `[Fact]`（行号是删除前位置，供定位；以方法名匹配为准）：

```
1.  PlayCommand_InvokesServicePlay                        (~256)
2.  PauseCommand_InvokesServicePause                      (~273)
3.  StopCommand_InvokesServiceStop                        (~285)
4.  FrameEmitted_DuringPlay_DoesNotTriggerSeek_ReverseTriggerGuard   (~480)
5.  UserDrag_ScrubberValue_TriggersSeek_WhenMasterPaused  (~507)
6.  SeekTo_ProportionalMapping_NonMasterAt30pctOf60s_IsAt15pctOf30s  (~664)
7.  SeekTo_NegativeTimestamp_ClampsToZero                 (~707)
8.  SeekTo_TimestampBeyondTotalDuration_ClampsToMax       (~748)
9.  SeekTo_InRangeTimestamp_PassesThroughUnchanged        (~774)
10. SetSpeed_AppliesToAllServices                         (~794)
11. Loop_PropagatesToAllServices_OnChange                 (~817)
12. OnMasterPlaybackEnded_LoopTrue_RewindsAllServicesToZero  (~840)
13. SetMaster_MidPlayback_StopsAll_RestartsFromZero       (~1024)
14. SetMaster_ReattachesPlaybackEndedToNewMaster          (~1056)
```

同时：

- **重命名** `SetMaster_ChangesMasterSourceId_RebindsFrameEmitted`（~871）→ `SetMaster_ChangesMasterSourceId`（方法体只断言 `sut.MasterSourceId.Should().Be("b")`，无播放引用，重命名后内容不变）。
- 若 `using PeakCan.HIL.Core.Replay;` 在删除后无剩余引用（`ReplayState` / `PlaybackEndedEventArgs` / `ReplayFrame` 不再使用），删除该 using；若有剩余引用（如 LoadAsync 测试用 `ReplayFrame`）则保留。以编译为准。

- [ ] **Step 2: 跑 TraceViewerViewModelTests 确认全绿（生产未动）**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~TraceViewerViewModelTests"`
Expected: PASS（剩余测试全绿；生产代码未改，仅删了针对已废除行为的测试）。

- [ ] **Step 3: Commit A**

```bash
git add tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs
git commit -m "test(trace): remove playback tests from TraceViewerViewModelTests"
```

- [ ] **Step 4: 删除 VM 播放属性 + 两个 flow 文件（Commit B 第 1 块）**

`TraceViewerViewModel.cs`：

```csharp
// 删除（连同 [ObservableProperty] 特性）：
[ObservableProperty]
private double _scrubberValue;

[ObservableProperty]
private bool _loop = false;

[ObservableProperty]
private double _speed = 1.0;
```

更新类 doc comment（line 35-40 附近）——删除提及 `PlayCommand/PauseCommand/StopCommand/SeekToCommand` 的句子。更新 `_allServices` 字段注释（line 97 附近）——删除「Play/Pause/Stop/Seek iterate this dict」。

删除文件：`TraceViewerViewModel/TransportFlow.cs`、`TraceViewerViewModel/LifecycleFlow.cs`（用 `git rm`）。

- [ ] **Step 5: 清理 PlaybackFlow.cs + 调用点（Commit B 第 2 块）**

`PlaybackFlow.cs`：删除 `PropagateLoopToAllServices`（15-19）、`PropagateSpeedToAllServices`（21-25）、`SeekAllToProportionalTime`（66-90）。保留 `ClearCanIdFilter` / `DetachAllSourcePropertyHandlers` / `OnAnySourcePropertyChanged` / `RebindMasterFromRegistry`。

`SourceFlow.cs` `SetMaster`（173-194）改为：

```csharp
[RelayCommand]
public void SetMaster(string sourceId)
{
    if (sourceId == MasterSourceId) return;
    if (!_allServices.TryGetValue(sourceId, out var newMaster)) return;
    MasterSourceId = sourceId;
    _masterService = newMaster;
    TotalDuration = _masterService.TotalDuration;
    ChartViewModel.SetTotalDuration(TotalDuration);
    // Master swap can change which signal rows have data (different
    // frame set); rebuild off-thread to avoid blocking the UI.
    _ = RebuildSignalsAsync();
}
```

`SourceFlow.cs` `OnRegistrySourcesChanged`（241-243 附近）：删除 `AttachAllServiceHandlers();` 与 `PropagateLoopToAllServices();` / `PropagateSpeedToAllServices();`。**保留** `RebindMasterFromRegistry();`（240）与上方 per-source `StartTimestamp/EndTimestamp = null` 循环（232-238）。顺带删除该文件中提及 "Loop/Speed to every newly registered service" 的过时注释。

`SessionFlow.cs` `RebindMasterServiceIfChanged`（179-191）改为：

```csharp
private void RebindMasterServiceIfChanged()
{
    if (string.IsNullOrEmpty(MasterSourceId)) return;
    var desired = _allServices.TryGetValue(MasterSourceId, out var svc) ? svc : null;
    if (ReferenceEquals(desired, _masterService)) return;
    _masterService = desired;
    TotalDuration = _masterService?.TotalDuration ?? 0.0;
    ChartViewModel.SetTotalDuration(TotalDuration);
}
```

`TraceViewerViewModel.cs` `Dispose()`（392-420）：删除 `DetachAllServiceHandlers();`（line 406）。其余反注册（`DbcLoaded` / `WatchedSignals.CollectionChanged` ×2 / `_session.PropertyChanged` / `_session.SessionRestored` / `DetachAllSourcePropertyHandlers` / `SourcesChanged` / chat CTS）全部保留。

- [ ] **Step 6: BuildSnapshotAsync 默认值 + viewports 空（Commit B 第 3 块）**

`SessionFlow.cs` `BuildSnapshotAsync`（66-149）：

scaffold（68-76）改三个窗口级字段：

```csharp
var scaffold = new TraceSessionSnapshotBuilder.Scaffold(
    LoadedFilePath: null,    // Trace iterates N sources — the builder's single-source path is unused
    CurrentTimestamp: 0.0,   // 播放已废除 → 窗口级默认
    Speed: 1.0,              // 播放已废除 → 窗口级默认
    Loop: false,             // 播放已废除 → 窗口级默认
    StartTimestamp: 0.0,
    EndTimestamp: 0.0,
    CanIdFilterText: CanIdFilter ?? "",
    DbcPath: LoadedDbcPath ?? "");
```

Playback 信封（118-126）：

```csharp
dto.Playback = new BundlePlaybackDto
{
    MasterSourceId = MasterSourceId ?? "",
    Loop = false, Speed = 1.0, ScrubberValue = 0.0,
    StartTimestamp = null, EndTimestamp = null,
};
```

viewports（127）：

```csharp
dto.Viewports = new List<BundleViewportDto>();
```

更新 `BuildSnapshotAsync` 的 doc comment（line 36-41）——删除「playback state is captured verbatim (master, loop, speed, scrubber)」的说法，改为「播放标量写默认值（窗口级已废除）」。

`SamplingTableFlow.cs`（75）：`var targetTs = ScrubberValue;` → `var targetTs = _masterService.CurrentTimestamp;`（`_masterService` 在上方 line 62 已判空，安全）。

- [ ] **Step 7: 构建 + 全量 App 测试（Commit B 收尾）**

Run: `dotnet build PeakCan.Host.slnx`
Expected: 0 errors。

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj`
Expected: 全绿。若编译器因残留 `using PeakCan.HIL.Core.Replay;`（`ReplayState` / `PlaybackEndedEventArgs` / `ReplayFrame` 已无引用）报警，删除对应文件里已无引用的 using（`SourceFlow.cs` / `SessionFlow.cs` / `PlaybackFlow.cs` 等逐一检查）。

- [ ] **Step 8: Commit B**

```bash
git add src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs \
        src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/TransportFlow.cs \
        src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/LifecycleFlow.cs \
        src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/PlaybackFlow.cs \
        src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SourceFlow.cs \
        src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SessionFlow.cs \
        src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SamplingTableFlow.cs
git commit -m "refactor(trace): remove playback feature from TraceViewerViewModel"
```

- [ ] **Step 9: 追加 BuildSnapshot 契约钉住测试（Commit C）**

在 `TraceViewerViewModelTests.cs` 的 BuildSnapshot 测试区（`BuildSnapshot_StampsInformationalVersion` ~1097 附近）追加：

```csharp
[Fact]
public void BuildSnapshot_WritesDefaultPlaybackEnvelope_AndNoViewports()
{
    // 播放已废除：Trace 的 BundlePlaybackDto 信封只写默认值，
    // viewports（chart 缩放/平移，窗口级状态）不再持久化（I-2 决策）。
    var library = NewTestLibrary(out var libPath);
    var vm = NewVm(library);

    var dto = vm.BuildSnapshot();

    dto.Playback.Should().NotBeNull();
    dto.Playback!.MasterSourceId.Should().BeNullOrEmpty();
    dto.Playback.Loop.Should().BeFalse();
    dto.Playback.Speed.Should().Be(1.0);
    dto.Playback.ScrubberValue.Should().Be(0.0);
    dto.Viewports.Should().BeEmpty();

    try { if (File.Exists(libPath)) File.Delete(libPath); } catch { /* best effort */ }
}
```

（`NewTestLibrary` / `NewVm` 是文件内已有的测试辅助方法，`BuildSnapshot_StampsInformationalVersion` 就在用。）

- [ ] **Step 10: 跑过滤测试 + 全量 App 测试确认绿**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~BuildSnapshot_WritesDefaultPlaybackEnvelope_AndNoViewports"`
Expected: PASS。

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj`
Expected: 全绿。

- [ ] **Step 11: Commit C**

```bash
git add tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs
git commit -m "test(trace): pin playback-free BuildSnapshot contract"
```

- [ ] **Step 12: 残留核查**

在 TraceViewerViewModel 相关文件（`src/PeakCan.Host.App/ViewModels/TraceViewerViewModel*.cs` 与 `TraceViewerViewModel/` 目录）grep：

Run: `git grep -nE "ScrubberValue|PropagateLoop|PropagateSpeed|SeekAllToProportionalTime|OnAnyFrameEmitted|OnMasterPlaybackEnded|AttachAllServiceHandlers|DetachAllServiceHandlers|OnScrubberValueChanged|OnLoopChanged|OnSpeedChanged" -- src/PeakCan.Host.App/ViewModels/`
Expected: 无匹配。

**注意**：测试里 `dto.Playback.Loop` / `dto.Playback.Speed` 是 `BundlePlaybackDto` 字段（Replay 共享），不是被删的 VM 属性——这是合法的、保留的。上述 grep 限定 `src/`，不含测试，故不受影响。

**Task 1 完成标准**：Commit A/B/C 均绿；残留 grep 无匹配；`SetMaster` / `RebindMasterServiceIfChanged` 无 Stop/Play/Propagate 调用。

---

### Task 2: 删除图表播放 cursor 死代码

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/TraceChartViewModel/PlaybackFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/TraceChartViewModelTests.cs`

**Interfaces:**
- Consumes: Task 1 已删 `OnAnyFrameEmitted`（`UpdatePlaybackCursor` 的唯一生产调用方）
- Produces: `TraceChartViewModel` 不再有 `UpdatePlaybackCursor` / `PlaybackCursorX` / `InvalidatePlotCallCount`

- [ ] **Step 1: 写失败测试前置核查**

`UpdatePlaybackCursor` 唯一生产调用方是 Task 1 已删的 `OnAnyFrameEmitted`。删除方法本身无新行为可测——先删测试（Step 3），再删生产（Step 4），与 Task 1 相同顺序。

- [ ] **Step 2: 改/删 TraceChartViewModelTests 的 cursor 测试**

`TraceChartViewModelTests.cs`：

- **适配** `Ctor_Empty_HasZeroSeries`（~28）：删除方法体内 `sut.PlaybackCursorX.Should().Be(0.0);`（line 33），保留 `sut.Series.Should().BeEmpty();` 与 `sut.TotalDuration.Should().Be(0.0);`。
- **删除** `UpdatePlaybackCursor_SetsProperty`（~57）。
- **删除** `UpdatePlaybackCursor_RapidCallsWithin16ms_DoesNotInvalidate`（~80，含其上方 v3.16.9 PATCH 的整块注释）。

若 `using System.Diagnostics;` 无剩余引用则删除。

- [ ] **Step 3: 跑 TraceChartViewModelTests 确认绿**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~TraceChartViewModelTests"`
Expected: PASS。

- [ ] **Step 4: 删除生产死代码**

`TraceChartViewModel/PlaybackFlow.cs`：删除 `_lastCursorInvalidateTicks` / `_lastCursorX` / `CursorInvalidateIntervalMs` / `StopwatchTicksToMs`（13-16）、`[ObservableProperty] private int _invalidatePlotCallCount;`（18-19）、`UpdatePlaybackCursor`（21-44）。**保留** `SetTotalDuration`（46）。若 `using System.Diagnostics;` 无剩余引用则删除。

`TraceChartViewModel.cs`（~35）：删除 `PlaybackCursorX` 属性及其私有字段。

- [ ] **Step 5: 构建 + 全量测试**

Run: `dotnet build PeakCan.Host.slnx` → 0 errors。
Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj` → 全绿。

- [ ] **Step 6: 残留核查**

Run: `git grep -nE "UpdatePlaybackCursor|PlaybackCursorX|InvalidatePlotCallCount" -- src/PeakCan.Host.App/`
Expected: 无匹配（`StatsViewModel.InvalidatePlotCallCount` 是独立 VM 的计数器，`git grep` 若匹配到它属正常——只确认 TraceChartViewModel 无匹配即可）。

- [ ] **Step 7: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/TraceChartViewModel/PlaybackFlow.cs \
        src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs \
        tests/PeakCan.Host.App.Tests/ViewModels/TraceChartViewModelTests.cs
git commit -m "refactor(trace): remove chart playback cursor dead code"
```

**Task 2 完成标准**：TraceChartViewModel 无 `UpdatePlaybackCursor` / `PlaybackCursorX` / `InvalidatePlotCallCount`；全量 App 测试绿。

---

### Task 3: 全量收尾验证

**Files:** 无（纯验证）。

- [ ] **Step 1: 全解决方案测试**

Run: `dotnet test PeakCan.Host.slnx -c Debug`
Expected: 全绿（App + Core + Infra；Core/Infra 未被改动，应保持原通过数）。

- [ ] **Step 2: 无未提交残留**

Run: `git status --short`
Expected: 仅 `docs/superpowers/specs/2026-08-11-hil-case-log-design.md` 未提交（故意保留，**不要**加入任何 commit）。

**Task 3 完成标准**：全解决方案测试通过；工作树无意外残留。

---

## Self-Review 记录

- **Spec 覆盖**：§4.1（属性）→ Task 1 Step 4；§4.2（TransportFlow）→ Task 1 Step 4；§4.3（PlaybackFlow）→ Task 1 Step 5；§4.4（LifecycleFlow）→ Task 1 Step 4；§4.5（调用点）→ Task 1 Step 5；§4.6（BuildSnapshotAsync）→ Task 1 Step 6；§4.7（SamplingTableFlow）→ Task 1 Step 6；§4.8（图表 cursor）→ Task 2；§7（测试）→ Task 1 Step 1/9 + Task 2 Step 2。
- **Placeholder 扫描**：无 TBD/TODO；所有删除按方法名/行号列出；契约测试含完整代码。
- **Type 一致性**：`BundlePlaybackDto.Loop/Speed/ScrubberValue`（保留的字段）在契约测试中通过 `dto.Playback.*` 访问，与被删的 VM 属性 `Loop/Speed/ScrubberValue` 区分明确；`_masterService.CurrentTimestamp`（SamplingTableFlow 替换目标）在 service 上存在（`ITraceViewerService.CurrentTimestamp`，已由 `ChatToolContextFlow.cs:128` 引用确认）。
- **TDD 说明**：删除语义下「RED→GREEN」不适用于被删成员（测试无法引用被删类型）。采用「先删废弃测试（绿）→ 再删生产（绿）→ 契约钉住测试（绿）」的顺序，每 commit 可独立评审。
