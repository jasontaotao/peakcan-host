---
topic: playback-abolition
created: 2026-08-13
status: approved
covers: Trace Viewer 播放功能残留废除 + I-2 Save/Open viewports 不对称
related: docs/superpowers/specs/2026-08-13-scripting-cycle-and-trace-session-state-design.md
---

# 设计文档：废除 Trace Viewer 播放功能

## 1. 背景与问题

peakcan-host 的 Trace Viewer 曾经有完整的播放控制（Play / Pause / Stop / Seek、循环 Loop、速度 Speed、进度条 Scrubber）。v3.50.4 已从 XAML 移除全部播放控件（`TraceViewerView.xaml` 内注释明确："Loop / Speed / ▶ / ⏸ / ⏹ / Scrubber 全部移除. Trace Viewer 不再含回放控制. 任何残余 PlayCommand/PauseCommand/StopCommand/Loop/Speed/ScrubberValue 在 VM 端保留"）——但 VM 端残留全部播放代码未被清理：

- `Play/Pause/Stop/SeekTo` 四个 `[RelayCommand]` + 生成的 `*Command` 属性
- `Loop` / `Speed` / `ScrubberValue` 三个 `[ObservableProperty]`
- `OnScrubberValueChanged` / `OnLoopChanged` / `OnSpeedChanged` 三个 partial hooks
- 帧泵订阅（`AttachAllServiceHandlers` / `DetachAllServiceHandlers` → `FrameEmitted`/`PlaybackEnded`）+ `OnAnyFrameEmitted` / `OnMasterPlaybackEnded`
- `PropagateLoopToAllServices` / `PropagateSpeedToAllServices` / `SeekAllToProportionalTime`

这批代码在 UI 上无任何入口（XAML 已删），纯死代码；且 `SetMaster` / `OnRegistrySourcesChanged` / `RebindMasterServiceIfChanged` 还背着 `Stop()` / `Play()` / propagate 调用负担。同时带出一个长期不对称问题（Phase 2 独立 review I-2）：**手动 Save 写 viewports，Open 从不恢复**。

## 2. 目标与非目标

### 目标

1. 删除 TraceViewerViewModel 侧全部播放残留（命令 / 属性 / hooks / 帧泵 / propagate）。
2. 简化 `SetMaster` / `OnRegistrySourcesChanged` / `RebindMasterServiceIfChanged` / `Dispose`。
3. `BuildSnapshotAsync`（手动 Save 路径）playback 信封写默认值、viewports 停止持久化 —— 与 `TraceSessionService.BuildSnapshot` 对齐。
4. 图表播放 cursor（`UpdatePlaybackCursor` / `PlaybackCursorX` / `InvalidatePlotCallCount`）作为死代码删除。

### 非目标

- 不动 Core / Infrastructure 层（NetArchTest 边界）—— `ReplayTimeline` / `ReplayService` / `TraceViewerService` 的 Play/Pause/Stop/Seek/SetSpeed 原语全部保留。
- 不动 Replay tab（`ReplayViewModel` 播放功能完整保留）。
- 不动 `TraceSessionService`（`BuildSnapshot` 已写默认值、`OpenSessionAsync` 不触碰 playback 标量，天然对齐）。
- 不动 `BundlePlaybackDto` 字段定义（Replay 共享，保留）。`BundleViewportDto` / `CaptureViewports` / `ApplyViewports` 代码也保留（TraceChartViewModel 内部机制 + 别处可能使用），只是 Trace Save 不再写 viewports。
- 不重构 `TraceViewerViewModel` 的 flow 拆分结构（那是 A2，后续单独做）。

## 3. 现状（问题定位）

### 3.1 VM 端播放残留

| 位置 | 内容 |
|---|---|
| `TraceViewerViewModel.cs:37-38` | 类 doc comment 提及 `PlayCommand/PauseCommand/StopCommand/SeekToCommand`（需更新） |
| `TraceViewerViewModel.cs:108-109` | `[ObservableProperty] private double _scrubberValue;` |
| `TraceViewerViewModel.cs:125-126` | `[ObservableProperty] private bool _loop = false;` |
| `TraceViewerViewModel.cs:129-130` | `[ObservableProperty] private double _speed = 1.0;` |
| `TraceViewerViewModel/TransportFlow.cs`（整文件） | `Play()`（21）/`Pause()`（36）/`Stop()`（43）/`SeekTo(double)`（51）四个 `[RelayCommand]` + `OnScrubberValueChanged`（63）/`OnLoopChanged`（89）/`OnSpeedChanged`（94）三个 partial hooks |
| `TraceViewerViewModel/PlaybackFlow.cs` | `PropagateLoopToAllServices`（15）/`PropagateSpeedToAllServices`（21）/`SeekAllToProportionalTime`（66） |
| `TraceViewerViewModel/LifecycleFlow.cs`（整文件） | `AttachAllServiceHandlers`（16，订阅 FrameEmitted + master 的 PlaybackEnded）/`DetachAllServiceHandlers`（28）/`OnMasterPlaybackEnded`（41，Loop 回绕）/`OnAnyFrameEmitted`（59，写 ScrubberValue + `UpdatePlaybackCursor`） |
| `TraceViewerViewModel.cs:406` | `Dispose()` 里 `DetachAllServiceHandlers()` |

调用面（废除后须同步清理）：

- `SourceFlow.cs:173-194` `SetMaster`：`wasPlaying`（178）/`Stop()`（179）/`DetachAllServiceHandlers()+AttachAllServiceHandlers()`（186-187）/`PropagateLoopToAllServices()+PropagateSpeedToAllServices()`（188-189）/`if (wasPlaying) Play()`（193）
- `SourceFlow.cs:241-243` `OnRegistrySourcesChanged`：`AttachAllServiceHandlers()` + 两个 Propagate
- `SessionFlow.cs:179-191` `RebindMasterServiceIfChanged`：`DetachAllServiceHandlers()+AttachAllServiceHandlers()`（187-188）+ 两个 Propagate（189-190）
- `SamplingTableFlow.cs:75` `RefreshSamplingTable`：`var targetTs = ScrubberValue;`
- `TraceChartViewModel/PlaybackFlow.cs` `UpdatePlaybackCursor`（21，仅被 `OnAnyFrameEmitted` 调用）+ `TraceChartViewModel.cs:35` `PlaybackCursorX` + `InvalidatePlotCallCount`

### 3.2 Save/Open viewports 不对称（I-2）

| 路径 | viewports 行为 |
|---|---|
| VM 手动 Save（`SessionFlow.cs:66-149` `BuildSnapshotAsync`） | 写真实 `ChartViewModel.CaptureViewports()`（127） |
| auto-save（`TraceSessionService.BuildSnapshot`，`TraceSessionService.cs:78-137`） | 写空列表（123，注释"窗口级 → 空列表"） |
| Open（`TraceSessionService.OpenSessionAsync`） | **不恢复 viewports**（`OpenSessionAsync` 无 viewport 恢复逻辑） |

结论：viewports 从未被恢复过，持久化无实际价值 → **停止持久化**（手动 Save 也写空列表）。

### 3.3 播放标量在 Save 信封里的现状

`TraceSessionService.BuildSnapshot` 已写默认值（`TraceSessionService.cs:117-122`：`Loop=false, Speed=1.0, ScrubberValue=0.0`），与废除后的目标一致。VM 手动 Save 路径（`SessionFlow.cs:118-126`）仍写 VM 属性真实值 —— 废除后改为默认值，两侧收敛。

## 4. 删除方案

### 4.1 VM 属性（`TraceViewerViewModel.cs`）

删除 `_scrubberValue` / `_loop` / `_speed` 三个 `[ObservableProperty]` 及其源生成属性 `ScrubberValue` / `Loop` / `Speed`。

**保留**：`_totalDuration`（chart X 轴，非播放）、`MasterSourceId`（转发 `_session.MasterSourceId`）、`CanIdFilter`（转发 `_session.GlobalCanIdFilter`）、`_isLoading` / `_errorMessage` / `_statusMessage`。

更新类 doc comment（37-38 行附近）移除对 `PlayCommand` 等的引用。

### 4.2 `TransportFlow.cs`（整文件删除）

`Play` / `Pause` / `Stop` / `SeekTo` 命令 + `OnScrubberValueChanged` / `OnLoopChanged` / `OnSpeedChanged` hooks 全部删除，文件无剩余内容 → 删除文件。

### 4.3 `PlaybackFlow.cs`（保留非播放方法）

删除 `PropagateLoopToAllServices` / `PropagateSpeedToAllServices` / `SeekAllToProportionalTime`（唯一调用方是已删的 `OnScrubberValueChanged`）。

**保留**：`ClearCanIdFilter`、`DetachAllSourcePropertyHandlers`、`OnAnySourcePropertyChanged`、`RebindMasterFromRegistry`。

### 4.4 `LifecycleFlow.cs`（整文件删除）

`AttachAllServiceHandlers` / `DetachAllServiceHandlers`（FrameEmitted/PlaybackEnded 订阅）、`OnMasterPlaybackEnded`、`OnAnyFrameEmitted` 全部删除。废除后无任何路径调用 `svc.Play()`，FrameEmitted 永不触发 → 订阅与 handler 均为死代码。文件无剩余内容 → 删除文件。

### 4.5 调用点清理

- **`SetMaster`（`SourceFlow.cs:173-194`）**：删除 `wasPlaying` 计算、`Stop()`、`DetachAllServiceHandlers()+AttachAllServiceHandlers()`、两个 Propagate、`if (wasPlaying) Play()`。保留：guard、`MasterSourceId = sourceId`、`_masterService = newMaster`、`TotalDuration = ...`、`ChartViewModel.SetTotalDuration(...)`、`_ = RebuildSignalsAsync()`。
- **`OnRegistrySourcesChanged`（`SourceFlow.cs:241-243`）**：删除 `AttachAllServiceHandlers()` + 两个 Propagate。保留 per-source `StartTimestamp/EndTimestamp = null` 循环（服务层 range 配置，不属本 Scope）与 `RebindMasterFromRegistry()`。
- **`RebindMasterServiceIfChanged`（`SessionFlow.cs:179-191`）**：删除 `DetachAllServiceHandlers()+AttachAllServiceHandlers()` + 两个 Propagate。保留 master 重绑 + `TotalDuration` + `ChartViewModel.SetTotalDuration`。
- **`Dispose`（`TraceViewerViewModel.cs:392+`）**：删除 `DetachAllServiceHandlers()`（406）。其余反注册（DbcLoaded / CollectionChanged / SessionRestored / `DetachAllSourcePropertyHandlers` / SourcesChanged / chat CTS）保留。

### 4.6 `BuildSnapshotAsync`（`SessionFlow.cs:66-149`）

- Scaffold 的 `CurrentTimestamp` / `Speed` / `Loop` 改写默认值（`0.0` / `1.0` / `false`）—— 与 `TraceSessionService.BuildSnapshot`（`TraceSessionService.cs:80-88`）一致。
- `dto.Playback` 写默认值（`Loop=false, Speed=1.0, ScrubberValue=0.0`），`MasterSourceId` 保留。
- **`dto.Viewports` 改为空列表**（I-2 决策：停止持久化）。

### 4.7 `SamplingTableFlow.RefreshSamplingTable`（`SamplingTableFlow.cs:75`）

`var targetTs = ScrubberValue;` → `var targetTs = _masterService.CurrentTimestamp;`（service 权威位置；废除后无播放写回，语义更准）。

### 4.8 图表播放 cursor 死代码（`TraceChartViewModel`）

删除 `UpdatePlaybackCursor`（`TraceChartViewModel/PlaybackFlow.cs:21-44`）、`PlaybackCursorX` 属性（`TraceChartViewModel.cs:35`）、`InvalidatePlotCallCount`（`PlaybackFlow.cs:18-19`）+ 节流字段（`_lastCursorInvalidateTicks` / `_lastCursorX` / `CursorInvalidateIntervalMs` / `StopwatchTicksToMs`）。**保留** `SetTotalDuration`（46）。已确认 XAML 无 `PlaybackCursorX` 绑定（grep 全 src XAML 无匹配）。

## 5. 保留清单

- Core 层 `ReplayTimeline.Play/Pause/Stop/Seek/SetSpeed`、`ReplayService`、`TraceViewerService`。
- `ITraceViewerService.Seek` + `IChatToolContext.Seek`（`ChatToolContextFlow.cs:100-105`：`_masterService.Seek(timestampSeconds)`，独立于已删的 `[RelayCommand] SeekTo`）。`SeekToTimeTool`（`Services/ChatTools/SeekToTimeTool.cs`）保留。
- ReplayViewModel 全部播放功能 + `BundlePlaybackDto` 字段（Replay 共享）。
- VM：`_totalDuration`、`MasterSourceId`、`CanIdFilter`、`ClearCanIdFilter`、`RebindMasterFromRegistry`、`DetachAllSourcePropertyHandlers`、`OnAnySourcePropertyChanged`、watch list / groups 转发属性。
- `TraceSessionService`（不改）。
- `BundleViewportDto` / `CaptureViewports` / `ApplyViewports`（TraceChartViewModel 内部机制 + 测试）。

## 6. 数据流变化

### 6.1 SetMaster（废除后）

```
SetMaster(sourceId)
  → guard（同 id 或未知 source 直接返回）
  → MasterSourceId = sourceId; _masterService = newMaster;
  → TotalDuration / ChartViewModel.SetTotalDuration
  → _ = RebuildSignalsAsync()      // 信号表按新 master 重建
```
不再 Stop/Play —— master 切换不重置时间线（会话打开时不改变播放状态，语义与 Phase 2 一致）。

### 6.2 Save（废除后）

```
VM.SaveSessionAsync(path)
  → BuildSnapshotAsync()
      → scaffold：CurrentTimestamp=0.0 / Speed=1.0 / Loop=false（窗口级 → 默认）
      → dto.Playback = { MasterSourceId, Loop=false, Speed=1.0, ScrubberValue=0.0 }
      → dto.Viewports = []          // I-2：停止持久化
      → sources / watch / groups / GlobalCanIdFilter / DbcPath（不变）
```
与 `TraceSessionService.BuildSnapshot` 对窗口级字段完全一致。

### 6.3 帧泵（废除后）

`AttachAllServiceHandlers` 删除 → VM 不再订阅 `FrameEmitted`/`PlaybackEnded` → `OnAnyFrameEmitted`（写 ScrubberValue + 图表 cursor）与 `OnMasterPlaybackEnded`（Loop 回绕）不复存在。master 的时间线仍存在（`_masterService`），但其 CurrentTimestamp 仅被 Chat 的 `IChatToolContext.Seek` / `GetTraceInfo` 读取。

## 7. 测试策略

### 7.1 `TraceViewerViewModelTests.cs`（删除播放区段）

| 测试 | 位置（约） | 处置 |
|---|---|---|
| `PlayCommand_InvokesServicePlay` / `PauseCommand_InvokesServicePause` / `StopCommand_InvokesServiceStop` | 256-296 | 删除 |
| `FrameEmitted_DuringPlay_DoesNotTriggerSeek_ReverseTriggerGuard` / `UserDrag_ScrubberValue_TriggersSeek_WhenMasterPaused` | 480-520 | 删除 |
| `SeekTo_ProportionalMapping_NonMasterAt30pctOf60s_IsAt15pctOf30s` / `SeekTo_NegativeTimestamp_ClampsToZero` / `SeekTo_TimestampBeyondTotalDuration_ClampsToMax` / `SeekTo_InRangeTimestamp_PassesThroughUnchanged` | 664-774 | 删除 |
| Speed/Loop 传播测试（`sut.Speed = 2.5` / `sut.Loop = true` 段） | 810-836 | 删除 |
| `OnMasterPlaybackEnded_LoopTrue_RewindsAllServicesToZero` | 840-861 | 删除 |
| `SetMaster_ChangesMasterSourceId_RebindsFrameEmitted` | 871 附近 | 改：断言新 master 绑定（去 FrameEmitted 断言） |
| `SetMaster_MidPlayback_StopsAll_RestartsFromZero` | 1024 | 删除 |
| `SetMaster_ReattachesPlaybackEndedToNewMaster` | 1056 | 删除 |
| `BuildSnapshot_*`（hashing） | 1143+ | 保留，追加断言：Playback 信封默认值 + Viewports 空 |

### 7.2 `TraceChartViewModelTests.cs`

删除 cursor 测试（`PlaybackCursorX` 默认值 / `UpdatePlaybackCursor_SetsProperty` / `UpdatePlaybackCursor_RapidCallsWithin16ms_DoesNotInvalidate`，约 33-108）。

### 7.3 保留

- `SetMaster_ToUnknownSourceId_IsNoOp` 等 master 切换测试（去播放断言）。
- `IChatToolContext.Seek` 相关测试（`SeekToTimeToolTests` 等）—— 不动。
- `TraceSessionServiceTests`（Open 恢复 watch/groups/master）—— 不动。
- 架构测试（NetArchTest）确认 App 层仍不引用 PEAK SDK。

## 8. 迁移与风险

| 风险 | 缓解 |
|---|---|
| 删除属性/命令后，非 VM 生产代码残留引用 | 已 grep 确认：`PlayCommand/PauseCommand/StopCommand/SeekToCommand` 仅 TraceViewerViewModel 自身文件引用（Replay/MultiFrame/FlashPanel 是各自 VM 的同名命令，不动）；`PlaybackCursorX` 无 XAML 绑定。实施时按 `git grep` 复核。 |
| 测试改动量大 | 播放区段集中，按 7.1 表格逐段删除；`BuildSnapshot` 断言是唯一追加点。 |
| `SamplingTableFlow` 换 `CurrentTimestamp` 后采样点漂移 | `CurrentTimestamp` 与旧 `ScrubberValue` 在无播放场景下同源（ScrubberValue 本就是 FrameEmitted 写回 master.CurrentTimestamp）；Chat seek 后 `CurrentTimestamp` 已更新，采样更准。 |
| 帧泵删除后 master `CurrentTimestamp` 不再经 VM 写回 | master service 自身维护 `CurrentTimestamp`（`ReplayTimeline`），`IChatToolContext.GetTraceInfo` 直接读 `_masterService.CurrentTimestamp` —— 不受影响。 |
| 误删 `SetTotalDuration` / `_totalDuration` | 明确保留（chart X 轴同步，非播放）。review 时重点核对。 |

## 9. 实施顺序

1. VM 属性 + TransportFlow + LifecycleFlow 删除。
2. PlaybackFlow / SourceFlow / SessionFlow / Dispose 调用点清理 + SetMaster 简化。
3. BuildSnapshotAsync 默认值 + Viewports 空。
4. SamplingTableFlow + 图表 cursor 死代码。
5. 测试区段删除 + BuildSnapshot 断言追加。
6. 全量测试（App + Core + Infra）。

> 详细步骤与任务分解见 writing-plans 产出的实现计划。
