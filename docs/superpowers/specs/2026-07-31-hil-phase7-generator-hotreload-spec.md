# HIL Phase 7 (Unit B): Generator Hot-Reload

> Spec date: 2026-07-31
> Depends: Phase 6 (commit `579b02e`) + 单元 A (commit `e427acf`)
> Scope: **接线外部 generator 插件目录 + 长运行模式热加载**。单元 B 是 Phase 7 第二个独立单元
> （A=DeepSeekOptions 接线已完成，C=Web 报告 UI，D=Multi-bus gateway 后续）。
>
> **Revision 2（2026-07-31）**：按 code-review 10 项（P1-P2 / H1-H3 / M1-M3 / L1-L2）全部修正。
> 核心变更：P1 host 生命周期矛盾 → 分两条路径（HIL 测试=仅接线，--simulate=热加载）；
> P2 --simulate 未覆盖 → Program.cs 创建 manager 接管热加载；H3 并发模型具体化；
> M1 ALC 不改 GeneratorPluginLoader（封装在 manager）；M2 Unload 异步测试；L2 失败重试。

---

## 1. Goals

Phase 5 交付了 `GeneratorPluginLoader`（Sprint 10，扫描目录 DLL 加载 `IEcuResponseGenerator`），
但存在两个问题：

**B1. 外部 generator 插件从未接线** — `LoadFromDirectory`（`GeneratorPluginLoader.cs:16`）无任何
生产调用方；`externalGenerators` 参数在 `EcuScriptLoader.Load/Parse`
（`EcuScriptLoader.cs:18,24,30`）存在，但 `HeadlessHostBuilder.cs:49`、`MatrixConfigLoader.cs:47`、
`Program.cs:46` 三个生产调用点均未传入。所有 ECU 模拟只用内置 5 个 generator
（`GetBuiltInGenerators()`，`EcuScriptLoader.cs:240-250`）。

**B2. 无运行时热加载** — 即使接线，也是启动时一次性加载。插件开发者改代码后必须重启工具才能
看到效果。

---

## 2. Current State

### 2.1 证据

| 项 | 证据 |
|----|------|
| `LoadFromDirectory` 无生产调用方 | 全库 grep 仅测试引用；`GeneratorPluginLoader.cs:16` 实现用 `Assembly.LoadFrom`（无法卸载） |
| `MergeGenerators` 调用点 | `EcuScriptLoader.cs:46`：`MergeGenerators(GetBuiltInGenerators(), externalGenerators)`；external 覆盖 built-in（`GeneratorPluginLoader.cs:63-71`） |
| 虚拟 ECU 加载（不传 external） | `HeadlessHostBuilder.cs:49`：`EcuScriptLoader.Load(args.EcuScriptPath!)` |
| Matrix 加载（不传 external） | `MatrixConfigLoader.cs:47`/`:52`：`EcuScriptLoader.Load/ParseEcuScript` |
| --simulate 加载（不传 external） | `Program.cs:46`：`EcuScriptLoader.Load(cli.EcuScriptPath!)` |
| **--simulate 长运行** | `Program.cs:44-56`：`await host.RunAsync(cts.Token)` 阻塞直到 Ctrl+C（`EcuSimulatorHost.cs:44` `Task.Delay(Infinite)`） |
| **HIL 测试单次 host** | `HilRunnerService.cs:18`：`using var host = HeadlessHostBuilder.Build(...)` 每次 Run 建 host 即 dispose |
| WPF 复用同一路径 | `HilRunnerService.cs:18` 调 `HeadlessHostBuilder.Build`；`AppHostBuilder.cs:302` 注册 `HilRunnerService` |
| `EcuStateMachine` generators 锁定 | `EcuStateMachine.cs:11`：`private readonly Dictionary<string, IEcuResponseGenerator> _generators`；构造时 `ToDictionary`（`:32`） |
| `ProcessRequest` 读 generators | `EcuStateMachine.cs:58`：`_generators.TryGetValue(d.GeneratorName, out var gen)` |
| `StatefulVirtualEcu` 持有状态机 | `StatefulVirtualEcu.cs:16`：`_stateMachine`；`EcuSimulatorHost.cs:32` 创建 ECU |
| 接口 | `IEcuResponseGenerator.cs:8-21`：`Name` + `Generate(request, currentState, context)` |
| CliArgs 无 generator-dir | 需新增（见 §3.6） |

### 2.2 现状结论

- 外部插件体系是"只实现未接线"。
- `Assembly.LoadFrom` 加载的程序集**无法卸载**，替换同名 DLL 会返回旧程序集——真正的热加载必须用
  `AssemblyLoadContext`（可卸载）。
- 热加载的实际价值集中在**长运行场景**（--simulate），HIL 测试单次 host（几秒）无热加载价值。

---

## 3. Design

### 3.1 两条路径（P1 生命周期决策）

按 host 生命周期拆分，避免"热加载窗口=测试执行时间"的无意义设计：

| | **路径 1：HIL 测试模式** | **路径 2：--simulate 模式** |
|---|---|---|
| 生命周期 | 单次 Run（`using var host`，`HilRunnerService.cs:18`） | 长运行直到 Ctrl+C（`Program.cs:54`） |
| 热加载价值 | 低（窗口几秒，下次 Run 自然重新加载） | **高**（ECU 持续运行，期间换插件） |
| 方案 | **仅接线**：一次性 `LoadFromDirectory(dir)`，无 watcher、无 ALC | **接线 + 热加载**：`GeneratorPluginManager`（ALC + FileSystemWatcher） |
| manager 注册位置 | 不在任何 host 内，直接调用静态 `LoadFromDirectory` | `Program.cs` 创建（应用级生命周期，`using` 保证 dispose） |

### 3.2 路径 1：HIL 测试模式接线（B1）

**不引入 watcher/ALC**——每次 Run 建新 host，从目录一次性加载即可：

```csharp
// HeadlessHostBuilder.cs:49 虚拟 ECU 模式：
var external = GeneratorPluginLoader.LoadFromDirectory(args.GeneratorDir); // 新增
var ecuScript = EcuScriptLoader.Load(args.EcuScriptPath!, external);

// MatrixConfigLoader.cs:47 Matrix 模式：
var external = GeneratorPluginLoader.LoadFromDirectory(dir);
ecus.Add(EcuScriptLoader.Load(fullPath, external));
```

- `GeneratorPluginLoader.LoadFromDirectory` **保持现有签名**（`M1`：不改 ALC，`Assembly.LoadFrom`
  对一次性加载足够）。
- `MergeGenerators`（`EcuScriptLoader.cs:46`）自动合并 built-in + external，external 覆盖同名。
- 覆盖 `HilRunnerService`（WPF 面板）——它复用 `HeadlessHostBuilder`（`HilRunnerService.cs:18`），
  WPF 每次 Run 自动受益，无需额外 UI。

### 3.3 路径 2：--simulate 热加载（P2）

**`GeneratorPluginManager`（新类，`Infrastructure/HIL/Generators/`）**——唯一含 ALC + watcher 的组件：

```csharp
// Program.cs:44-56 --simulate 分支改造：
using var manager = new GeneratorPluginManager(cli.GeneratorDir);
var ecuScript = EcuScriptLoader.Load(cli.EcuScriptPath!, manager.Current);
var handle = HeadlessHostBuilder.ParseChannelHandle(cli.HardwareChannel!);
var channel = new PeakCanChannel(new ChannelId(handle), null);
manager.GeneratorsChanged += () => ecuScript.StateMachine.ReplaceGenerators(manager.Current);
var host = new EcuSimulatorHost(channel, ecuScript.CanIds, ecuScript.StateMachine, null);
// ... Console.CancelKeyPress 等原有逻辑 ...
await host.RunAsync(cts.Token);   // Ctrl+C → using 块 dispose manager
```

- `EcuSimulatorHost`（`EcuSimulatorHost.cs`）**不改**——`Program.cs` 直接持 `ecuScript.StateMachine`
  引用订阅替换。
- `manager.Current`：`IReadOnlyList<IEcuResponseGenerator>`（built-in + external 合并，external 覆盖）。

### 3.4 `GeneratorPluginManager` 内部设计

```
构造(dir)
  ├─ 初始：ALC 加载目录 DLL → MergeGenerators(builtIn, external) → Current
  ├─ FileSystemWatcher(dir, "*.dll") 监听 Created/Changed/Renamed/Deleted
  └─ debounce Timer ~300ms（合并 DLL burst 事件）
     └─ Reload():
          ├─ 新 ALC 加载目录 → MergeGenerators → 新 Current
          ├─ 替换引用（Interlocked.Exchange(ref _current, newCurrent)）
          ├─ 触发 GeneratorsChanged
          └─ 旧 ALC.Unload()（标记，GC 回收 —— M2）

Dispose()
  ├─ watcher.Dispose()
  ├─ 取消 debounce Timer
  ├─ GeneratorsChanged = null（清事件，防泄漏 —— H2）
  └─ 释放当前 ALC
```

**ALC 加载（M1 封装）**：
- 每次 Reload 创建新 `AssemblyLoadContext`（可卸载）。
- 用 `AssemblyDependencyResolver` + `Resolving` 事件把插件依赖（`PeakCan.Host.Core` 等）fallback
  到默认 ALC，保证插件里的 `IEcuResponseGenerator` 与 Core 的接口是**同一类型**。
- **失败重试（L2）**：`LoadFromDirectory` 捕获 `BadImageFormatException`（DLL 写入中）→
  延迟 ~200ms 重试最多 3 次；重试耗尽仍失败 → 保留旧 `Current`，记日志，不中断。
- 返回类型包含 ALC 引用（`(IReadOnlyList<IEcuResponseGenerator>, AssemblyLoadContext)` 内部持有，
  不暴露给调用方）。

### 3.5 `EcuStateMachine.ReplaceGenerators` 并发模型（H3）

**变更 `EcuStateMachine`**（`Core/HIL/Contracts/EcuStateMachine.cs`）：

- `_generators` 由 `readonly`（`:11`）改为普通字段，保留构造函数 `ToDictionary`（`:32`）。
- 新增方法：

```csharp
public void ReplaceGenerators(IEnumerable<IEcuResponseGenerator> generators)
{
    var newDict = generators.ToDictionary(g => g.Name);
    Interlocked.Exchange(ref _generators, newDict);   // 原子替换引用
}
```

- `ProcessRequest`（`:58`）的 `_generators.TryGetValue` **无需加锁**：读取时栈帧持有旧字典引用，
  旧字典替换后**不再被修改**（不可变），安全。新请求读到新字典，原子切换。
- `StatefulVirtualEcu.OnUdsRequestReceived`（ISO-TP 接收线程，`StatefulVirtualEcu.cs:56`）与
  替换并发安全——替换只换引用，不修改旧字典。
- 保留 `_currentState`/`_context`（不重建状态机，运行状态不丢）。

### 3.6 接线清单（L1）

| 文件 | 新增 |
|------|------|
| `CliArgs` | `string? GeneratorDir = null` 字段 |
| `CliArgsParser` | `--generator-dir <path>` 解析 + `PrintHelp` |
| `HilRunRequest` | `string? GeneratorDir = null` 参数 |
| `HilRunRequestExtensions.ToCliArgs` | `GeneratorDir` 传递 |
| `HeadlessHostBuilder` | 虚拟 ECU/Matrix 模式读 `args.GeneratorDir` → `LoadFromDirectory` |
| `MatrixConfigLoader` | 接受 `externalGenerators` 参数透传 |

---

## 4. File Inventory

| 文件 | 动作 |
|------|------|
| `src/PeakCan.Host.Infrastructure/HIL/Generators/GeneratorPluginManager.cs` | NEW — ALC + watcher + debounce + 重试 + 事件 |
| `src/PeakCan.Host.Infrastructure/HIL/Generators/GeneratorPluginLoader.cs` | MODIFY — 暴露内部 ALC 加载 helper（`LoadFromDirectory` 保持兼容） |
| `src/PeakCan.Host.Core/HIL/Contracts/EcuStateMachine.cs` | MODIFY — `_generators` 去 readonly + `ReplaceGenerators`（Interlocked） |
| `src/PeakCan.Host.Infrastructure/HIL/HeadlessHostBuilder.cs` | MODIFY — 虚拟 ECU/Matrix 模式传 `LoadFromDirectory(args.GeneratorDir)` |
| `src/PeakCan.Host.Infrastructure/HIL/MatrixConfigLoader.cs` | MODIFY — 接受并透传 `externalGenerators` |
| `src/PeakCan.Host.Infrastructure/Cli/CliArgs.cs` | MODIFY — `GeneratorDir` 字段 + `--generator-dir` 解析（`CliArgsParser` 同文件 `:35`）+ help |
| `src/PeakCan.Host.Cli/Program.cs` | MODIFY — `--simulate` 分支用 `GeneratorPluginManager` + 热替换订阅 |
| `src/PeakCan.Host.Core/HIL/HilRunRequest.cs` | MODIFY — `GeneratorDir` 参数 |
| `src/PeakCan.Host.Infrastructure/HIL/HilRunRequestExtensions.cs` | MODIFY — `ToCliArgs` 传递 |
| `tests/PeakCan.Host.Infrastructure.Tests/HIL/Generators/GeneratorPluginManagerTests.cs` | NEW |
| `tests/PeakCan.Host.Infrastructure.Tests/HIL/Generators/GeneratorPluginLoaderTests.cs` | MODIFY — 兼容性保持（LoadFrom 行为不变） |
| `tests/PeakCan.Host.Core.Tests/HIL/Contracts/EcuStateMachineReplaceGeneratorsTests.cs` | NEW |
| `tests/PeakCan.Host.Infrastructure.Tests/HIL/Generators/GeneratorHotReloadIntegrationTests.cs` | NEW — ALC 替换同名 DLL 端到端 |

---

## 5. Testing (TDD)

| 用例 | 断言 |
|------|------|
| Manager 初始加载 | 目录含插件 → `Current` 含 external（覆盖 built-in 同名） |
| DLL 变化 → 事件 | 写入 dll → debounce 后 `GeneratorsChanged` 触发、`Current` 更新 |
| ALC 替换同名 DLL | 覆盖同名 dll → `GC.Collect()+WaitForPendingFinalizers` 后新版本生效（证明非 LoadFrom 缓存，M2） |
| 失败保留旧 | 写坏 dll → `Current` 不变、不抛 |
| 失败重试 | 写入中坏 dll → 重试后加载成功（L2） |
| `ReplaceGenerators` 原子替换 | 替换后 `ProcessRequest` 用新 generator 响应；`_currentState`/`_context` 保留 |
| Manager.Dispose | Dispose 后事件清空、watcher 释放（H2） |
| HIL 测试接线 | `--generator-dir` → `LoadFromDirectory` 一次性加载，外部插件响应生效 |
| Matrix 接线 | 多 ECU 各自脚本，external 生效 |
| ToCliArgs | `GeneratorDir` 从 `HilRunRequest` → `CliArgs` 传递 |

---

## 6. Out of Scope

- **WPF 面板热加载 UI**（状态显示/错误提示）——HIL 测试模式单次 host 无热加载需求；--simulate 是
  CLI 场景，无 UI
- **Generator 卸载后的内存回收保证**——ALC.Unload 是异步 GC 语义，本 spec 只保证"替换生效"，
  不承诺即时释放（M2）
- **Web 报告 UI（Phase 7 单元 C）**
- **Multi-bus gateway（Phase 7 单元 D）**
