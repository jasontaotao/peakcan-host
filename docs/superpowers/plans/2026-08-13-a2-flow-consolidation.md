# A2 Flow 结构收尾 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 外科手术式收尾 `TraceViewerViewModel` 的 flow partial 结构——删除名不副实的 `PlaybackFlow.cs`（M4），4 个残留方法归位到准确域名，master 重绑逻辑归拢到 `SourceFlow.cs`，清理残留的 "Flow X" 历史标签与误导性头注释。**0 逻辑变更**。

**Architecture:** 所有成员在同一个 partial class（`TraceViewerViewModel`）内跨文件移动——private 成员跨文件可见性不变，`[RelayCommand]` 源生成不受文件位置影响。纯文件结构重组 + 注释修正，无需任何行为改动。

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm。

## Global Constraints

- **0 逻辑变更**：方法**逐字移动**，不改签名、不改方法体逻辑、不改 `[RelayCommand]` 特性。只允许改注释。
- 移动方法时**检查目标文件是否已有必要 using**；缺了才补，已有则不动，**不要删任何现有 using**。
- **不动** `TraceViewerViewModel.cs` 主文件、Core/Infrastructure、Replay、`TraceSessionService`、`ChartFillEngine.cs`、`ProgressiveScatterSource.cs`。
- **不要**把工作树中未提交的 `docs/superpowers/specs/2026-08-11-hil-case-log-design.md` 纳入任何 commit——每个 commit 用显式 `git add <具体文件>`。
- 提交信息用 conventional commits（`refactor:` / `docs:`），不加 Co-Authored-By。
- 生产代码注释：面向用户/业务逻辑用中文，技术 API/接口用英文。

## 执行者须知（本计划自包含，无需任何外部文档）

执行前先读这 3 个文件：

1. `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/PlaybackFlow.cs`（57 行，即将删除）——含 4 个方法：`ClearCanIdFilter`（12）/`DetachAllSourcePropertyHandlers`（16）/`OnAnySourcePropertyChanged`（29）/`RebindMasterFromRegistry`（39）
2. `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SourceFlow.cs`（285 行）——`RebindMasterFromRegistry` 与 `RebindMasterServiceIfChanged` 的迁入目标
3. `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SessionFlow.cs`（187 行）——`RebindMasterServiceIfChanged`（~169-183）迁出

**硬约束（违反即失败）：**

- `ClearCanIdFilter` 的 `[RelayCommand]` 源生成命令 `ClearCanIdFilterCommand` 被 XAML 绑定——移动后命令名不变，XAML 不受影响。
- `RebindMasterFromRegistry` 的现有 doc comment 引用了已删除的 `AttachAllServiceHandlers`/`DetachAllServiceHandlers`/`FrameEmitted`/`PlaybackEnded`（播放废除时漏改）——**迁移时同步修正该注释**（见 Task 1 Step 3）。
- `RebindMasterServiceIfChanged` 的现有 doc comment 引用了 "重挂事件 handlers + loop/speed 传播"（已删）——**迁移时同步修正**（见 Task 1 Step 3）。

---

### Task 1: FilterFlow.cs 新建 + PlaybackFlow.cs 删除 + master 重绑归拢 SourceFlow.cs

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/FilterFlow.cs`
- Delete: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/PlaybackFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SourceFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SessionFlow.cs`

**Interfaces:**
- Consumes: `PlaybackFlow.cs` 的 4 个方法（Task 前提）
- Produces: `FilterFlow.cs`（过滤器域 3 方法）；`SourceFlow.cs`（master 重绑 2 方法）；`SessionFlow.cs`（移除 `RebindMasterServiceIfChanged`）

- [ ] **Step 1: 新建 `FilterFlow.cs`**

创建 `FilterFlow.cs`，内容如下（3 个方法体从 `PlaybackFlow.cs` **逐字复制**，仅文件头注释为新写）：

```csharp
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Services.Trace;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewerViewModel
{
    // 全局 CAN-ID 过滤器（Clear 按钮）+ 逐源 filter INPC 响应。
    // TraceSource 只把 CanIdFilter 暴露为 INPC——过滤器变更需同步刷新
    // 帧计数并移除孤儿 chart series。

    // v3.4.2 PATCH: XAML "Clear" button binding. Empty string → parser
    // returns null → unfiltered rebuild.
    [RelayCommand]
    private void ClearCanIdFilter() => CanIdFilter = "";

    // v3.4.3 PATCH: detach per-source INPC subscriptions. Idempotent --
    // subtracting an absent handler is a no-op.
    private void DetachAllSourcePropertyHandlers()
    {
        foreach (var src in _registry.Sources)
            src.PropertyChanged -= OnAnySourcePropertyChanged;
    }

    // v3.4.3 PATCH: react to TraceSource.CanIdFilter changes by
    // refreshing frame counts + removing orphan chart series
    // synchronously. The TraceSource instance only exposes CanIdFilter
    // as INPC today, so the filter guard is a safety net for future
    // fields. v3.14.3 PATCH: do NOT call RebuildSignalsCore -- user
    // opt-ins in the signal table must survive filter changes; only
    // the per-row FrameCount + LatestValue columns are refreshed.
    private void OnAnySourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TraceSource.CanIdFilter)) return;
        if (_dbcService.Current is null) return;
        RefreshFrameCounts();
        RemoveOrphanChartSeries();
        ChartViewModel.SyncYAxes();
    }
}
```

对照 `PlaybackFlow.cs` 逐行核对：3 个方法体必须逐字一致（`ClearCanIdFilter` / `DetachAllSourcePropertyHandlers` / `OnAnySourcePropertyChanged`）。`using` 三行从 `PlaybackFlow.cs` 原样带过来（`System.ComponentModel` / `CommunityToolkit.Mvvm.Input` / `PeakCan.Host.App.Services.Trace`）。

- [ ] **Step 2: 删除 `PlaybackFlow.cs`**

Run: `git rm src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/PlaybackFlow.cs`

- [ ] **Step 3: 迁入 `SourceFlow.cs`**

从 `PlaybackFlow.cs` 把 `RebindMasterFromRegistry` 方法体（**逐字**）移到 `SourceFlow.cs`，并**重写其 doc comment**（原注释引用已删除的 `AttachAllServiceHandlers`/`FrameEmitted`/`PlaybackEnded`，属 stale）：

```csharp
    private void RebindMasterFromRegistry()
    {
        // Master 解析：优先保留当前 MasterSourceId（仍在 Sources 中时），
        // 否则回退 Sources[0]。幂等——OnRegistrySourcesChanged 调用。
        if (_registry.Sources.Count == 0)
        {
            _masterService = null;
            MasterSourceId = "";
            return;
        }
        // Master invariant: prefer current MasterSourceId if still in Sources;
        // else fall back to Sources[0] (deterministic default).
        var newMaster = _registry.Sources.FirstOrDefault(
            s => s.SourceId == MasterSourceId) ?? _registry.Sources[0];
        MasterSourceId = newMaster.SourceId;
        _masterService = _allServices.TryGetValue(newMaster.SourceId, out var svc) ? svc : null;
    }
```

从 `SessionFlow.cs` 把 `RebindMasterServiceIfChanged` 方法体（**逐字**）移到 `SourceFlow.cs`，并**重写其 doc comment**（原注释引用 "重挂事件 handlers + loop/speed 传播"，均已在播放废除时删除）：

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

在 `SourceFlow.cs` 中把 `RebindMasterFromRegistry` 放在 `SetMaster` 之后（同域），`RebindMasterServiceIfChanged` 紧随其后。`SourceFlow.cs` 现有的 `using System.ComponentModel;` / `using PeakCan.HIL.Core.Replay;` 已覆盖这两个方法（方法内只用 `_registry`/`_allServices`/`MasterSourceId`/`TotalDuration`/`ChartViewModel`，无新类型）——**不要新增 using**。

`SessionFlow.cs`：删除 `RebindMasterServiceIfChanged` 方法及其 doc comment，**保留** `OnSessionRestored`（它仍调用 `RebindMasterServiceIfChanged`，跨文件 partial-class 可见性照常）与 `SaveSessionAsync`/`BuildSnapshotAsync`/`LogHashFailed`。

- [ ] **Step 4: 构建 + 全量 App 测试**

Run: `dotnet build PeakCan.Host.slnx`
Expected: 0 errors（若 `FilterFlow.cs` 或 `SourceFlow.cs` 缺 using 会编译错——Step 1 已带齐，Step 3 不应新增）。

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj`
Expected: 全绿（App 1107 通过——纯移动不改逻辑）。

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/FilterFlow.cs \
        src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/PlaybackFlow.cs \
        src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SourceFlow.cs \
        src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SessionFlow.cs
git commit -m "refactor(trace): consolidate filter flow and master rebind in TraceViewerViewModel"
```

**Task 1 完成标准**：`PlaybackFlow.cs` 已删；`FilterFlow.cs` 存在且 3 方法逐字一致；`SourceFlow.cs` 含 `RebindMasterFromRegistry` + `RebindMasterServiceIfChanged`（注释已去 stale 播放引用）；`SessionFlow.cs` 不再含 `RebindMasterServiceIfChanged`；构建 0 错误、App 测试全绿。

---

### Task 2: stale 注释清理

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SignalFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SourceFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/WatchFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SessionFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SamplingTableFlow.cs`

**Interfaces:**
- Consumes: Task 1 已把 `RebindMasterServiceIfChanged` 迁走（`SessionFlow.cs` 头部注释可能仍提及）

- [ ] **Step 1: 清理 4 个 flow 文件头部**

逐文件读头部注释块，做以下替换（**只改注释，不改任何代码**）：

| 文件 | 现状 | 改为 |
|---|---|---|
| `SignalFlow.cs` | 头部 `// Flow C: Signal table + filter (v3.15.0 MINOR + earlier patches).` + `// Methods moved verbatim from TraceViewerViewModel.cs.` + 整段 `// Cross-flow callers (must be Flow[X]_<Verb> with internal visibility after Tasks 3+5+6 land):`（含下一行 `// - Flow A: FlowA_OnRegistrySourcesChanged calls RefreshFrameCounts (here)`） | 删除 `Flow C:` 标签与整段 cross-flow 注释，替换为一句话：`// 信号表重建与帧计数（Signal table rebuild + frame counts）。` 若方法体上方另有内联注释，保留 |
| `SourceFlow.cs` | 头部 `// Flow A: Source management (registry add/remove + master swap + DBC load).` | 删除 `Flow A:` 前缀 → `// Source management (registry add/remove + master swap + DBC load).`。**保留**其后的 `// Cross-flow callers (all stay as plain calls because partial-class visibility...)`（这句是准确的） |
| `WatchFlow.cs` | 头部 `// Flow D: Watch list + chart plotting (v3.15.0 MINOR + earlier patches).` | 删除 `Flow D:` 前缀 → `// Watch list + chart plotting (v3.15.0 MINOR + earlier patches).`。**保留**其后的 cross-flow refs 注释（准确） |
| `SessionFlow.cs` | 头部 `// Flow E: Session save (v3.5.0 MINOR + later patches).` | 删除 `Flow E:` 前缀 → `// Session save (v3.5.0 MINOR + later patches).`。**保留**其后的 OpenSessionAsync 已删除说明（准确） |

- [ ] **Step 2: 修正 `SamplingTableFlow.cs` 头部假声明**

读 `SamplingTableFlow.cs` 头部注释块，修正两处：

1. 把 `// master CurrentTimestamp 变化时 debounce 50ms 刷新一次。` 改为真实行为：
   `// 由 WatchedSignals CollectionChanged 触发（RefreshSamplingTable）；master CurrentTimestamp 变化不触发（debounce 未接线）。`
2. `实现选择` 块里的 `// - debounce 用 Task.Delay(50) + CancellationToken 而不是 DispatcherTimer。` 若仍存在，改为：
   `// - debounce 未接线（v3.49.0 范围内 RefreshSamplingTable 只由 CollectionChanged 触发）。`

   （`W23 LESSON` 段若只描述已验证的类型属性，保留。）

- [ ] **Step 3: 构建 + 全量 App 测试**

Run: `dotnet build PeakCan.Host.slnx` → 0 errors。
Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj` → 全绿。

- [ ] **Step 4: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SignalFlow.cs \
        src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SourceFlow.cs \
        src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/WatchFlow.cs \
        src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SessionFlow.cs \
        src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SamplingTableFlow.cs
git commit -m "docs(trace): clean stale Flow-X headers and SamplingTableFlow claim"
```

**Task 2 完成标准**：`git grep -nE "Flow [A-E]:|Flow\[X\]|Tasks 3\+5\+6" -- src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/` 无匹配；`SamplingTableFlow.cs` 头部无 "debounce" 假声明；构建 0 错误、App 测试全绿。

---

### Task 3: 全量收尾验证

**Files:** 无（纯验证）。

- [ ] **Step 1: 全解决方案测试**

Run: `dotnet test PeakCan.Host.slnx -c Debug`
Expected: 全绿（App 1107 + Infra 391；Core 785 中 `AscParserTests` 是已知 pre-existing timing-flaky，与本改动无关——它偶尔失败属正常）。

- [ ] **Step 2: 无未提交残留**

Run: `git status --short`
Expected: 仅 `docs/superpowers/specs/2026-08-11-hil-case-log-design.md` 未提交（故意保留，**不要**加入任何 commit）。

**Task 3 完成标准**：全解决方案测试通过；工作树无意外残留。

---

## Self-Review 记录

- **Spec 覆盖**：§4.1（FilterFlow + RebindMasterFromRegistry 归位）→ Task 1 Step 1-3；§4.2（RebindMasterServiceIfChanged 迁移）→ Task 1 Step 3；§4.3（stale 注释）→ Task 2；§6（测试零改动）→ Task 1 Step 4 / Task 2 Step 3 / Task 3。
- **Placeholder 扫描**：无 TBD/TODO；Task 1 给出 `FilterFlow.cs` 完整内容 + 两个迁移方法的完整方法体；Task 2 给出逐文件替换表。
- **Type 一致性**：迁移方法不改签名；`RebindMasterFromRegistry`/`RebindMasterServiceIfChanged` 均为 `private void`，partial-class 跨文件可见性不变；`[RelayCommand]` 源生成命令名 `ClearCanIdFilterCommand` 不受文件位置影响。
- **0 行为保证**：所有移动逐字；唯一允许的改动是注释；构建 + 全量测试作为硬验证。
