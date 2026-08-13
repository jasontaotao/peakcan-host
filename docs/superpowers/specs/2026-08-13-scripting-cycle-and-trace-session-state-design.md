---
topic: scripting-cycle-and-trace-session-state-extraction
created: 2026-08-13
status: approved
covers: A1 脚本循环依赖破环 + A4/C2 Trace 会话状态剥离 + VM 生命周期
related: docs/superpowers/session-anchors/2026-07-10-v3-5-to-v3-16-status-anchor.md
---

# 设计文档：脚本循环依赖破环 + Trace 会话状态剥离

## 1. 背景与问题

peakcan-host 经过 v3.5 → v3.62 的快速增长，暴露出两类根因级架构债，本次重构分两个 Phase 依次修复：

- **A1 脚本循环依赖**：`ScriptEngine` ↔ `ScriptUtilities` 存在构造期双向依赖，当前用反射 hack 绕过（`AppHostBuilder.cs:154-174`），脆弱且静默失败。
- **A4/C2 会话状态混淆**：`TraceViewerViewModel` 同时承载「会话级可持久化状态」和「窗口级 UI 状态」，被注册成进程级 singleton 只为给 `AppShellViewModel` 的菜单命令当会话操作入口。由此产生 `Reset()` 和一系列 "close + reopen 显示陈旧状态" 的 bug。

## 2. 目标与非目标

### 目标

1. 消除 `ScriptEngine` ↔ `ScriptUtilities` 循环依赖，删除反射 hack。
2. 把 4 组会话级状态（master source id、全局 CAN-ID filter、watch list、signal groups）从 `TraceViewerViewModel` 剥离到独立的 `ITraceSessionService`。
3. `TraceViewerViewModel` 改为窗口级生命周期（transient），删除 `Reset()`。
4. `AppShellViewModel` 不再依赖 `TraceViewerViewModel` 实例（Open 走 service，Save 走窗口引用）。

### 非目标

- 不拆 `TraceViewerViewModel` 的 15 个 flow partial 文件（那是 A2，后续单独做）。
- 不引入消息总线 / `WeakReferenceMessenger`（A6，后续单独做）。
- 不改 UI 层（converter 合并、XAML 拆分，B/C 系列后续做）。
- 不动 Core / Infrastructure 层（NetArchTest 边界保持不变）。

## 3. 现状（问题定位）

### 3.1 A1 循环依赖

```
ScriptEngine        → ScriptUtilities   （CreateEngineFlow.cs:93-100 把 log/warn/error/delay/hex/toHex 暴露给 JS）
ScriptUtilities     → ScriptEngine.EmitOutput()  （ScriptUtilities.cs:38/48/58 输出路由）
ScriptViewModel     → ScriptEngine.OutputReceived  （ScriptViewModel.cs:62 订阅输出）
ScriptConsole       → ScriptEngine（static CurrentEngine，CreateEngineFlow.cs:47 设置）
```

- `EmitOutput` = `OutputReceived?.Invoke(line)`（`ScriptHelpersFlow.cs:15-18`）。
- `CanApi` / `DbcApi` 不依赖脚本引擎（已验证），不在循环内。
- 反射 hack 见 `AppHostBuilder.cs:154-174`：`GetField("_utilities", NonPublic).SetValue(engine, utilities)`，字段改名/加 sealed 即静默崩。

### 3.2 A4 会话状态混淆

`TraceViewerViewModel` 被注册为 singleton（`ViewModelsBatch2Flow.cs:80`），注释明确原因："singleton so AppShellViewModel constructs with the same instance, preserving loaded trace + signal list + chart scrubber position across menu round-trips"。

真实原因分解：

| 状态 | 性质 | 现状 home | 应归 |
|---|---|---|---|
| sources 集合 | 会话数据 | ✅ `ITraceSessionRegistry` | 不变 |
| DBC path | 会话数据 | ✅ `DbcService` | 不变 |
| master source id | 会话级 | VM（`[ObservableProperty] _masterSourceId`） | service |
| 全局 CAN-ID filter | 会话级 | VM（`_canIdFilter`） | service |
| watch list（`WatchedSignals`） | 会话级 | VM | service |
| signal groups（`SignalGroups`） | 会话级 | VM | service |
| scrubber / viewports / chart / chat / anchor | 纯窗口级 | VM | VM（不变） |

`Open/OpenRecent` 是纯会话操作（load bundle → 应用 registry + DbcService），不依赖窗口级状态；`Save` 必须读 scrubber/viewports/chart 等窗口级状态。

## 4. Phase 1 — A1 脚本循环依赖破环

### 4.1 新接口

```csharp
// Services/Scripting/IScriptOutputSink.cs
public interface IScriptOutputSink
{
    void EmitOutput(ScriptOutputLine line);
}
```

### 4.2 改造点

1. `ScriptEngine` 实现 `IScriptOutputSink`（`EmitOutput` 已是 internal，改为接口实现）。
2. `ScriptUtilities` 依赖 `IScriptOutputSink`（`_engine` 字段 → `_sink`），`ScriptUtilities.cs:38/48/58` 改调 `_sink.EmitOutput(...)`。
3. `ScriptEngine` 的 `ScriptUtilities? _utilities` 字段改为 `Lazy<ScriptUtilities>? _utilities`，用延迟注入打破 ctor 双向依赖。`CreateEngineFlow.cs:93-100` 的 `if (_utilities is not null)` 改为先 `var utils = _utilities.Value` 再暴露。
4. `ScriptConsole.CurrentEngine`（`ScriptConsole.cs:19`）保持 `ScriptEngine?` 不变 —— 它是 static setter（由 `ScriptEngine.CreateEngine()` 设置），不参与 DI 循环。

### 4.3 DI 注册

替换 `AppHostBuilder.cs:154-183` 的反射 hack：

```csharp
builder.Services.AddSingleton<ScriptEngine>();           // ctor 注入 Lazy<ScriptUtilities>
builder.Services.AddSingleton<IScriptOutputSink>(sp => sp.GetRequiredService<ScriptEngine>());
builder.Services.AddSingleton<ScriptUtilities>();        // ctor 注入 IScriptOutputSink
```

解析顺序：解析 `ScriptUtilities` → 需要 `IScriptOutputSink` → forward 到 `ScriptEngine` → 其 ctor 需要 `Lazy<ScriptUtilities>`（不立即解析）→ `ScriptEngine` 构造完成 → `ScriptUtilities` 构造完成。`Lazy` 正确打破循环。

## 5. Phase 2 — A4/C2 会话状态剥离 + VM 生命周期

### 5.1 新服务 `ITraceSessionService`

```csharp
// Services/Trace/ITraceSessionService.cs
public interface ITraceSessionService
{
    ObservableCollection<WatchedSignalRow> WatchedSignals { get; }   // 从 VM 迁入
    ObservableCollection<WatchedSignalGroup> SignalGroups { get; }   // 从 VM 迁入
    string? MasterSourceId { get; set; }    // INPC（从 VM 的 _masterSourceId 迁入）
    string GlobalCanIdFilter { get; set; }  // INPC（从 VM 的 _canIdFilter 迁入）
    Task<IReadOnlyList<string>> OpenSessionAsync(string path);  // 从 VM 的 SessionFlow 迁入
    TraceSessionBundleDto BuildSnapshot();   // 供 auto-saver（纯会话数据，无 scrubber/viewports）
    bool HasContent { get; }                 // _registry.Sources.Count > 0
}
```

实现 `TraceSessionService`（singleton）依赖：`ITraceSessionRegistry`、`TraceSessionLibrary`、`DbcService`、`IAscLocator`、`IAscContentHasher`、`ILogger<TraceSessionService>`。

`OpenSessionAsync` 承接 `TraceViewerViewModel.ApplySnapshotAsync`（`SessionFlow.cs:188-351`）中**纯会话数据**部分：

- unload 现有 sources（`registry.UnloadAsync` 逐个）
- 对每个 bundle source：hash 重定位（`_locator.LocateAsync`）→ `registry.LoadAsync` → 重戳 DisplayName/Color/CanIdFilter
- 加载 DBC（`dbcService.LoadAsync`，best-effort）
- 记录 `MasterSourceId`（按 DisplayName 映射新 SourceId）
- 返回 missing `.asc` 路径列表

**不做**：不碰 `RebuildSignalsCore`、`ChartViewModel.ApplyViewports`、`WatchedSignals` 恢复、anchor 重算（这些是窗口级，由 VM 经 `SourcesChanged` 事件重建）。

### 5.2 `TraceViewerViewModel` 改造

1. **注册**：`AddSingleton` → `AddTransient`（`ViewModelsBatch2Flow.cs:80`）。
2. **删除 `Reset()`**（`TraceViewerViewModel.cs:291-343`）。
3. **`Dispose()` 保留**（`TraceViewerViewModel.cs:406-420`），改为窗口 `Closed` 时调用（见 5.4）。
4. **状态透传**：4 组会话级状态改为 service 引用：
   - `WatchedSignals` → `_session.WatchedSignals`（get-only 转发属性，`ObservableCollection` 本身即可绑定）
   - `SignalGroups` → `_session.SignalGroups`
   - `MasterSourceId` → service 侧 `[ObservableProperty]`；VM 提供 get-only 转发属性，并在 ctor 订阅 `_session.PropertyChanged` 把 `MasterSourceId` / `GlobalCanIdFilter` 的 INPC 转发到自身（XAML 绑定 VM）
   - `CanIdFilter` → 转发 `_session.GlobalCanIdFilter`（同上）
5. **Open 移除**：`OpenSessionAsync` / `ApplySnapshotAsync`（`SessionFlow.cs:51-351`）迁入 service 并删除。
6. **Save 保留**：`SaveSessionAsync` / `BuildSnapshotAsync`（`SessionFlow.cs:33-174`）留 VM —— snapshot 含 scrubber/viewports/watch list/groups，其中 watch list/groups 读 `_session`，其余读 VM 窗口级状态。

### 5.3 `AppShellViewModel` 改造

1. **ctor**（`AppShellViewModel.cs:254-286`）：移除 `TraceViewerViewModel traceViewerViewModel` 参数，新增 `ITraceSessionService traceSessionService` + `Func<TraceViewerViewModel> traceViewerFactory`。
2. **`OpenSessionAsync` / `OpenRecentSessionAsync`**（`SessionFlow.cs:38-108`）：改调 `_traceSessionService.OpenSessionAsync(path)`，保留 missing `.asc` 的 MessageBox 逻辑。
3. **`SaveSessionAsync`**（`SessionFlow.cs:69-79`）：从缓存窗口拿 VM —— `_traceViewerView?.DataContext as TraceViewerViewModel`，null 则提示「请先打开 Trace Viewer」（不 auto-open，保持现状语义）。
4. **`ShowTraceViewer`**（`ViewSwitchFlow.cs:187-256`）：`new TraceViewerView(_traceViewerViewModel)` 改为 `new TraceViewerView(_traceViewerFactory())`，删除 `_traceViewerView.Closed += (_, _) => _traceViewerViewModel.Reset()`（`ViewSwitchFlow.cs:218`）。

### 5.4 窗口 → VM 生命周期绑定

`TraceViewerView` 的 `Closed` 事件触发 VM 释放（`TraceViewerView.xaml.cs:15-20` 已有 `Closed += ...` 订阅，扩展为 `(DataContext as IDisposable)?.Dispose()`）。窗口关闭 → VM dispose → 事件订阅清理，无需 `Reset()`。

### 5.5 Auto-saver 依赖修正

`TraceSessionAutoSaver`（`Services/Trace/TraceSessionAutoSaver.cs`）+ 基类 `SessionAutoSaver<TVm>` 当前经 `ITraceViewerViewModelProvider.GetCurrent()` 拿 VM 并调 `vm.BuildSnapshotAsync()` / `vm.OpenSessionAsync()` / `vm.Sources`。VM transient 后：

- **会话数据 snapshot**（sources / master / filter / watch list / groups）改从 `ITraceSessionService` 构建，不再需要 VM。
- **视图状态**（scrubber / viewports）在 auto-save 场景下非关键，窗口未开则跳过。

`ReplaySessionAutoSaver` 若同样依赖 VM（`IReplayViewModelProvider`），需一并评估 —— 本 Phase 只处理 Trace 侧，Replay 侧若解耦成本高可记录为后续项。

## 6. 数据流

### 6.1 Open Session（Phase 2 后）

```
AppShell 菜单 Open Session
  → ITraceSessionService.OpenSessionAsync(path)
      → TraceSessionLibrary.Load(path) → dto
      → registry.UnloadAsync(现有) + LoadAsync(每个 source，含 hash 重定位 + 重戳属性)
      → DbcService.LoadAsync(dto.DbcPath)
      → 记录 MasterSourceId / GlobalCanIdFilter
      → registry.SourcesChanged 触发
          → 若 TraceViewerViewModel 窗口开着：OnRegistrySourcesChanged() 重建视图（已订阅）
  → 返回 missing 列表 → AppShell MessageBox
```

### 6.2 Save Session（Phase 2 后）

```
AppShell 菜单 Save Session
  → 从缓存 _traceViewerView.DataContext 拿 VM（窗口必须开）
  → VM.SaveSessionAsync(path)
      → BuildSnapshotAsync()：会话数据读 _session（sources/master/filter/watch/groups）+ 视图状态读 VM（scrubber/viewports）
      → TraceSessionLibrary.Save(dto, path)
```

### 6.3 窗口生命周期

```
AppShell ShowTraceViewer
  → new TraceViewerView(_traceViewerFactory())   // 新 transient VM
  → 窗口 Closed → (DataContext as IDisposable)?.Dispose()   // 释放 VM + 事件订阅
  → 会话级状态（watch list / master / filter）存于 service，窗口重开时自动恢复
```

## 7. 错误处理

- `ITraceSessionService.OpenSessionAsync` 保留 `ApplySnapshotAsync` 现有的宽容语义：missing `.asc` 记入返回列表不抛；`FileNotFoundException`/`DirectoryNotFoundException`/`ReplayException` 记 missing；DBC load 失败 best-effort（日志 + 不抛）。
- `ScriptEngine` 的 `Lazy<ScriptUtilities>.Value` 若解析失败（理论上不可能，`ScriptUtilities` 依赖的 `IScriptOutputSink` 已注册），异常在首次访问时抛出 —— 用 DI 的 `IServiceScope` 校验可提前暴露。

## 8. 测试策略

### Phase 1

- `ScriptUtilitiesTests`：构造改为注入 fake `IScriptOutputSink`，断言 `Log/Warn/Error` 调 `sink.EmitOutput`。
- `ScriptEngineTests`：构造改传 `Lazy<ScriptUtilities>`，其余不变。
- 新增 `ScriptEngine` 实现 `IScriptOutputSink` 的契约测试。

### Phase 2

- 新增 `TraceSessionServiceTests`：`OpenSessionAsync` 的 registry 交互（unload + load + 重戳 + missing 返回）、master 映射、DBC best-effort。
- `TraceViewerViewModelTests`（~1885 行）大改：状态经 `_session` 透传的断言、`Reset()` 移除、`OpenSessionAsync` 移除。
- `AppShellViewModelTests`（~1430 行）大改：ctor 新签名、Open/OpenRecent 走 service、Save 走窗口引用。
- `TraceSessionAutoSaver` 相关测试：provider → service 的断言调整。
- 架构测试（`NetArchTest`）确认 App 层仍不引用 PEAK SDK（本 Phase 不改边界）。

## 9. 迁移与风险

| 风险 | 缓解 |
|---|---|
| watch list 跨窗口保留回归 | 移到 `ITraceSessionService`（singleton），窗口关闭不丢 —— 这是本 Phase 的核心收益之一 |
| 窗口关闭时 VM dispose 遗漏订阅 | `TraceViewerView.Closed` 统一 `Dispose()`，`Dispose()` 已有幂等 `_disposed` 守卫 |
| Auto-saver 在窗口未开时无法拿视图状态 | 会话数据改从 service 构建；视图状态（scrubber）auto-save 场景非关键，跳过 |
| 测试改动量大（~3300 行） | 分 Phase 提交；先 Phase 1（小、独立）验证节奏，再 Phase 2 |
| `Func<TraceViewerViewModel>` 工厂注入的 transient 依赖正确性 | VM 的 ctor 依赖均为 DI 已注册服务（registry / dbcService / sessionLibrary / hasher / locator / builder / credentialStore 等，多为 singleton），`Func<T>` 逐次解析 transient VM 及其依赖，DI 可自动装配 |

## 10. 实施顺序

1. Phase 1（A1）：`IScriptOutputSink` + `Lazy<ScriptUtilities>` + 删反射 hack + 测试。独立提交。
2. Phase 2（A4/C2）：`ITraceSessionService` + VM 状态透传 + transient 化 + AppShell 改造 + auto-saver 修正 + 测试。分小步提交。

> 详细步骤与任务分解见 writing-plans 产出的实现计划。
