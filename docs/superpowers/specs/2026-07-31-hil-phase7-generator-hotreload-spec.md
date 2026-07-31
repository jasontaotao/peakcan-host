# HIL Phase 7 (Unit B): Generator Hot-Reload

> Spec date: 2026-07-31
> Depends: Phase 6 (commit `579b02e`) + 单元 A (commit `e427acf`)
> Scope: **接线外部 generator 插件目录 + 长运行模式热加载**。单元 B 是 Phase 7 第二个独立单元
> （A=DeepSeekOptions 接线已完成，C=Web 报告 UI，D=Multi-bus gateway 后续）。
>
> **Revision 3（2026-07-31）**：按 code-review 24 项（Rev1: P1-P2/H1-H3/M1-M3/L1-L2 +
> Rev2: L1-L4/B1-B4/E1-E3/T1-T4）全部修正。Rev3 核心变更：**统一 `manager.Current` 语义为
> "仅外部 generators"**（消除双重合并），提取 `BuiltInGenerators` 单一来源（L1/L2/T1），
> `--simulate` 顺带修复 host 泄漏（L3），ALC 子类代码给出（E1），`volatile` 内存模型（B3），
> Matrix 三层签名透传（B2/L4/T3），`GeneratorPluginLoader` 不改（E2），null 边界（B1），
> 重试异常集扩展（B4），事件类型声明（E3），`Dispose` 语义明确（T4），record 参数位置（T2）。

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
| `LoadFromDirectory` 无生产调用方 | 全库 grep 仅测试引用；`GeneratorPluginLoader.cs:16` 用 `Assembly.LoadFrom`（无法卸载） |
| `MergeGenerators` | `GeneratorPluginLoader.cs:63-71`：external 覆盖 built-in 同名 |
| `EcuScriptLoader` 合并点 | `EcuScriptLoader.cs:46`：`MergeGenerators(GetBuiltInGenerators(), externalGenerators)` |
| **built-in 私有** | `EcuScriptLoader.cs:240`：`private static GetBuiltInGenerators()`（5 个实例 `:242-249`） |
| 虚拟 ECU 加载（不传 external） | `HeadlessHostBuilder.cs:49`：`EcuScriptLoader.Load(args.EcuScriptPath!)` |
| Matrix 加载（不传 external） | `MatrixConfigLoader.cs:47`/`:52`：`Load`/`ParseEcuScript`；三层方法 `Parse:16`/`LoadFromFile:63`/`Load:73` |
| --simulate 加载（不传 external） | `Program.cs:46`：`EcuScriptLoader.Load(cli.EcuScriptPath!)` |
| **--simulate 长运行** | `Program.cs:44-56`：`await host.RunAsync(cts.Token)` 阻塞直到 Ctrl+C（`EcuSimulatorHost.cs:44`） |
| **--simulate host 未 dispose** | `Program.cs:49`：`var host = new EcuSimulatorHost(...)` 无 `using`；`EcuSimulatorHost.cs:13` 实现 `IDisposable` |
| **HIL 测试单次 host** | `HilRunnerService.cs:18`：`using var host = HeadlessHostBuilder.Build(...)` 每次 Run 即 dispose |
| WPF 复用同一路径 | `HilRunnerService.cs:18` 调 `HeadlessHostBuilder.Build`；`AppHostBuilder.cs:302` 注册 |
| `EcuStateMachine` generators 锁定 | `EcuStateMachine.cs:11`：`private readonly Dictionary`；构造 `ToDictionary`（`:32`） |
| `ProcessRequest` 读 generators | `EcuStateMachine.cs:58`：`_generators.TryGetValue` |
| 接口 | `IEcuResponseGenerator.cs:8-21`：`Name` + `Generate` |
| `ToCliArgs` | `HilRunRequestExtensions.cs:21-32`：positional 构造 `CliArgs`，末尾 `matrixPath` |
| `HilRunRequest` | `HilRunRequest.cs:3-17`：positional record，末尾 `bool EnableAnalyze = false` |

### 2.2 现状结论

- 外部插件体系是"只实现未接线"。
- `Assembly.LoadFrom` 无法卸载，替换同名 DLL 返回旧程序集——热加载必须用 `AssemblyLoadContext`。
- 热加载实际价值集中在**长运行场景**（--simulate）；HIL 测试单次 host（几秒）无热加载价值。

---

## 3. Design

### 3.1 两条路径（P1 生命周期决策）

按 host 生命周期拆分，避免"热加载窗口=测试执行时间"的无意义设计：

| | **路径 1：HIL 测试模式** | **路径 2：--simulate 模式** |
|---|---|---|
| 生命周期 | 单次 Run（`using var host`，`HilRunnerService.cs:18`） | 长运行直到 Ctrl+C（`Program.cs:54`） |
| 热加载价值 | 低（窗口几秒，下次 Run 自然重新加载） | **高**（ECU 持续运行，期间换插件） |
| 方案 | **仅接线**：一次性 `LoadFromDirectory(dir)`，无 watcher、无 ALC | **接线 + 热加载**：`GeneratorPluginManager`（ALC + FileSystemWatcher） |
| manager 位置 | 不在任何 host，直接静态 `LoadFromDirectory` | `Program.cs` 创建（`using` 保证 dispose） |

### 3.2 统一语义：`BuiltInGenerators` 单一来源（L1/L2/T1）

**新建 `BuiltInGenerators` 静态类**（`Infrastructure/HIL/Generators/BuiltInGenerators.cs`）：

```csharp
public static class BuiltInGenerators
{
    public static IReadOnlyList<IEcuResponseGenerator> CreateAll() => new[]
    {
        new SecurityAccessSeedGenerator(),
        new SecurityAccessVerifyKeyGenerator(),
        new ClearDtcGenerator(),
        new DidReadoutGenerator(),
        new DidWriteGenerator(),
    };
}
```

- `EcuScriptLoader.GetBuiltInGenerators()`（`:240`）**删除**，`ParseEcuScript`（`:46`）改调
  `BuiltInGenerators.CreateAll()`——built-in 列表**单一来源**，未来新增内置不会漏。
- **语义约定（贯穿全文）**：`GeneratorPluginManager.Current` = **仅外部 generators**（不含 built-in）。
  外部插件管理器的职责就是管理外部插件；built-in 合并统一由
  `GeneratorPluginLoader.MergeGenerators(BuiltInGenerators.CreateAll(), external)` 完成。
- `EcuScriptLoader.Load/Parse` 的 `externalGenerators` 参数接收的就是 external 列表（含 `manager.Current`），
  内部合并 built-in——**无双重合并**。

### 3.3 路径 1：HIL 测试模式接线（B1/L4/T3）

三个生产调用点全部接线（`HeadlessHostBuilder` 两个分支 + `MatrixConfigLoader`）：

```csharp
// HeadlessHostBuilder.cs:49 虚拟 ECU 分支：
var external = GeneratorPluginLoader.LoadFromDirectory(args.GeneratorDir);
var ecuScript = EcuScriptLoader.Load(args.EcuScriptPath!, external);

// HeadlessHostBuilder.cs:56-64 Matrix 分支（L4/T3）：
var external = GeneratorPluginLoader.LoadFromDirectory(args.GeneratorDir);
var config = MatrixConfigLoader.Load(args.MatrixPath!, external);

// Hardware（:34-43）与 TraceReplay（:66-76）分支不涉及 ECU 脚本，不需要 generators。
```

- `GeneratorPluginLoader.LoadFromDirectory` **签名与实现均不改**（E2）——`Assembly.LoadFrom` 对
  一次性加载足够，ALC 只在 `GeneratorPluginManager` 内部。
- `HilRunnerService`（WPF 面板）复用 `HeadlessHostBuilder`（`HilRunnerService.cs:18`），自动受益。

### 3.4 路径 2：--simulate 热加载（P2/L3）

**`GeneratorPluginManager`（新类，`Infrastructure/HIL/Generators/`）**：

```csharp
// Program.cs:44-56 --simulate 分支改造：
using var manager = new GeneratorPluginManager(cli.GeneratorDir);   // null 安全（B1）
var ecuScript = EcuScriptLoader.Load(cli.EcuScriptPath!, manager.Current);
var handle = HeadlessHostBuilder.ParseChannelHandle(cli.HardwareChannel!);
var channel = new PeakCanChannel(new ChannelId(handle), null);
// 热替换：合并 built-in + 当前外部（BuiltInGenerators 单一来源，L1/L2）
manager.GeneratorsChanged += () => ecuScript.StateMachine.ReplaceGenerators(
    GeneratorPluginLoader.MergeGenerators(BuiltInGenerators.CreateAll(), manager.Current));
using var host = new EcuSimulatorHost(channel, ecuScript.CanIds, ecuScript.StateMachine, null);  // L3 修复既有泄漏
// ... Console.CancelKeyPress 等原有逻辑 ...
await host.RunAsync(cts.Token);   // Ctrl+C → using 块 dispose host + manager
```

- `EcuSimulatorHost`（`EcuSimulatorHost.cs`）**不改**——`Program.cs` 直接持 `ecuScript.StateMachine`
  订阅替换。
- **`Current` 语义**（T1 澄清）：仅外部 generators 列表；`EcuScriptLoader.Load` 内部合并 built-in；
  热替换由 `MergeGenerators(CreateAll(), Current)` 显式合并——三处语义一致，无双重合并。

### 3.5 `GeneratorPluginManager` 内部设计（E1/B1/B4/E3/T4）

```
构造(dir)
  ├─ dir 为 null 或空 → Current = empty，不启动 watcher（B1）
  ├─ 否则：ALC 加载目录 DLL → external 列表 → Current
  ├─ FileSystemWatcher(dir, "*.dll") 监听 Created/Changed/Renamed/Deleted
  └─ debounce Timer ~300ms（合并 DLL burst 事件）
     └─ Reload():
          ├─ 新 GeneratorLoadContext 加载目录 → 新 external 列表
          ├─ Interlocked.Exchange(ref _current, newList)
          ├─ 触发 GeneratorsChanged
          └─ 旧 ALC.Unload()（标记，GC 回收 —— M2）

Dispose()（T4 明确）
  ├─ watcher.Dispose()
  ├─ 取消 debounce Timer
  ├─ GeneratorsChanged = null（清事件，防泄漏 —— H2）
  └─ 不调 ALC.Unload()——generator 实例可能仍被 EcuStateMachine 引用（ProcessRequest 栈帧），
     Dispose 时机（Ctrl+C）后无新请求，ALC 由 GC 回收；Unload 在 Dispose 中无意义且有破坏风险
```

**ALC 加载（E1，给出代码）**：

```csharp
internal sealed class GeneratorLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    public GeneratorLoadContext(string mainAssemblyPath)
        : base(name: $"generator-{Guid.NewGuid():N}", isCollectible: true)
        => _resolver = new AssemblyDependencyResolver(mainAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}
```

- 插件依赖（`PeakCan.Host.Core` 等）经 `AssemblyDependencyResolver` fallback 到默认 ALC，保证插件里的
  `IEcuResponseGenerator` 与 Core 接口是**同一类型**。
- **失败重试（B4，异常集对齐 `GeneratorPluginLoader.cs:39-54`）**：加载失败捕获
  `BadImageFormatException` / `IOException`（文件锁）/ `FileLoadException` / `FileNotFoundException`
  （文件已删）→ 延迟 ~200ms 重试最多 3 次；耗尽仍失败 → 保留旧 `Current`，记日志，不中断。

**事件声明（E3）**：`public event Action? GeneratorsChanged;`（订阅者读 `Current` 获取最新列表，
无需事件参数）。

### 3.6 `EcuStateMachine.ReplaceGenerators` 并发模型（H3/B3）

**变更 `EcuStateMachine`**（`Core/HIL/Contracts/EcuStateMachine.cs`）：

- `_generators` 由 `readonly`（`:11`）改为 **`volatile`** 字段（B3——`Interlocked.Exchange` 写有全栅栏，
  但普通读在 ARM 弱内存模型可能读旧引用；`volatile` 保证读的获取语义，x86/x64 上语义同样正确）：
  `private volatile Dictionary<string, IEcuResponseGenerator> _generators;`
- 新增方法：

```csharp
public void ReplaceGenerators(IEnumerable<IEcuResponseGenerator> generators)
{
    var newDict = generators.ToDictionary(g => g.Name);
    Interlocked.Exchange(ref _generators, newDict);   // 原子替换引用
}
```

- `ProcessRequest`（`:58`）`_generators.TryGetValue` 读局部引用（`volatile` 保证读到最新）；
  栈帧持有旧字典，旧字典替换后**不再被修改**（不可变），安全。
- `StatefulVirtualEcu.OnUdsRequestReceived`（ISO-TP 接收线程）与替换并发安全。
- 保留 `_currentState`/`_context`（不重建状态机，运行状态不丢）。

### 3.7 接线清单（L1/T2）

| 文件 | 新增 |
|------|------|
| `CliArgs` | `string? GeneratorDir = null` 字段（positional **末尾**，现有调用不受影响） |
| `CliArgsParser` | `--generator-dir <path>` 解析 + `PrintHelp`（同文件 `CliArgs.cs:35`） |
| `HilRunRequest` | `string? GeneratorDir = null`（positional record **末尾**，`EnableAnalyze` 之后，T2） |
| `HilRunRequestExtensions.ToCliArgs` | `GeneratorDir` 传递（positional 末尾） |
| `HeadlessHostBuilder` | 虚拟 ECU + Matrix 两个分支读 `args.GeneratorDir` → `LoadFromDirectory`（T3） |
| `MatrixConfigLoader` | `Parse`/`LoadFromFile`/`Load` 三层加 `externalGenerators` 参数透传（B2） |

---

## 4. File Inventory

| 文件 | 动作 |
|------|------|
| `src/PeakCan.Host.Infrastructure/HIL/Generators/GeneratorPluginManager.cs` | NEW — ALC + watcher + debounce + 重试 + 事件 |
| `src/PeakCan.Host.Infrastructure/HIL/Generators/BuiltInGenerators.cs` | NEW — built-in 单一来源（`CreateAll()`） |
| `src/PeakCan.Host.Core/HIL/Contracts/EcuStateMachine.cs` | MODIFY — `_generators` volatile + `ReplaceGenerators`（Interlocked） |
| `src/PeakCan.Host.Infrastructure/HIL/EcuScriptLoader.cs` | MODIFY — 删 `GetBuiltInGenerators`（:240），`ParseEcuScript` 改调 `BuiltInGenerators.CreateAll()` |
| `src/PeakCan.Host.Infrastructure/HIL/HeadlessHostBuilder.cs` | MODIFY — 虚拟 ECU/Matrix 分支传 `LoadFromDirectory(args.GeneratorDir)` |
| `src/PeakCan.Host.Infrastructure/HIL/MatrixConfigLoader.cs` | MODIFY — 三层签名加 `externalGenerators` 透传 |
| `src/PeakCan.Host.Infrastructure/Cli/CliArgs.cs` | MODIFY — `GeneratorDir` 字段 + `--generator-dir` 解析 + help |
| `src/PeakCan.Host.Cli/Program.cs` | MODIFY — `--simulate` 分支 `GeneratorPluginManager` + 热替换订阅 + `using var host` |
| `src/PeakCan.Host.Core/HIL/HilRunRequest.cs` | MODIFY — `GeneratorDir` 参数（record 末尾） |
| `src/PeakCan.Host.Infrastructure/HIL/HilRunRequestExtensions.cs` | MODIFY — `ToCliArgs` 传 `GeneratorDir` |
| `tests/PeakCan.Host.Infrastructure.Tests/HIL/Generators/GeneratorPluginManagerTests.cs` | NEW |
| `tests/PeakCan.Host.Core.Tests/HIL/Contracts/EcuStateMachineReplaceGeneratorsTests.cs` | NEW |
| `tests/PeakCan.Host.Infrastructure.Tests/HIL/Generators/GeneratorHotReloadIntegrationTests.cs` | NEW — ALC 替换同名 DLL 端到端 |

> **E2 明确**：`GeneratorPluginLoader.cs` **不在 File Inventory**——签名与实现不改，
> `LoadFromDirectory`/`MergeGenerators` 原样复用。ALC 逻辑全在 `GeneratorPluginManager` 内部。

---

## 5. Testing (TDD)

| 用例 | 断言 |
|------|------|
| Manager 初始加载 | 目录含插件 → `Current` 含 external（仅外部，不含 built-in） |
| Manager null 目录 | `GeneratorDir=null` → `Current` empty、不启动 watcher、不抛（B1） |
| DLL 变化 → 事件 | 写入 dll → debounce 后 `GeneratorsChanged` 触发、`Current` 更新 |
| ALC 替换同名 DLL | 覆盖同名 dll → `GC.Collect()+WaitForPendingFinalizers` 后新版本生效（M2） |
| 失败保留旧 | 写坏 dll → `Current` 不变、不抛（含 IOException 文件锁，B4） |
| 失败重试 | 写入中坏 dll → 重试后加载成功 |
| `ReplaceGenerators` | 替换后 `ProcessRequest` 用新 generator 响应；`_currentState`/`_context` 保留 |
| `ReplaceGenerators` 并发 | 替换与 `ProcessRequest` 并发 → 不抛、无竞态（volatile 读） |
| Manager.Dispose | Dispose 后事件清空、watcher 释放（H2/T4） |
| HIL 测试接线 | `--generator-dir` → 一次性加载，外部插件响应生效 |
| Matrix 接线 | 多 ECU 各自脚本，external 生效（三层签名透传） |
| ToCliArgs | `GeneratorDir` 从 `HilRunRequest` → `CliArgs` 传递 |
| BuiltInGenerators 单一来源 | `CreateAll()` 返回 5 个；`EcuScriptLoader` 无重复定义 |

---

## 6. Out of Scope

- **WPF 面板热加载 UI**（状态显示/错误提示）——HIL 测试模式单次 host 无热加载需求；--simulate 是
  CLI 场景，无 UI
- **Generator 卸载后的内存回收保证**——ALC.Unload 是异步 GC 语义，本 spec 只保证"替换生效"，
  不承诺即时释放（M2）
- **Web 报告 UI（Phase 7 单元 C）**
- **Multi-bus gateway（Phase 7 单元 D）**
