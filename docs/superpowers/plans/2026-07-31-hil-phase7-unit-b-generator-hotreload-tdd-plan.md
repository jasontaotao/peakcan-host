# HIL Phase 7 Unit B TDD Plan: Generator Hot-Reload

> Spec: `docs/superpowers/specs/2026-07-31-hil-phase7-generator-hotreload-spec.md` (Rev 4, 0 CRITICAL)
> Created: 2026-07-31
> Sprints: 3 | Increments: 9 | Tests: 13
>
> **Revision 2（2026-07-31）**：plan review 修正 2 缺口 — Inc 6/8 插件 DLL 用 Roslyn 运行时编译
> （新增 `TestPluginCompiler` helper + `Microsoft.CodeAnalysis.CSharp` 依赖）；Inc 7 新增
> `GeneratorPluginManager.ApplyTo(EcuStateMachine)` 可测方法；Inc 9 Program.cs 用 `ApplyTo`，
> 测试改测 `ApplyTo`（Infra.Tests，非阻塞 host）。

---

## Pre-checks (verify before coding)

| # | Check | Command | Expected |
|---|-------|---------|----------|
| 0 | Build passes | `dotnet build` | 0 errors |
| 1 | HIL tests green | `dotnet test --filter "FullyQualifiedName~HIL"` | 0 failed (4 既有 TraceViewer 失败除外) |
| 2 | `GetBuiltInGenerators` is private | grep `private static.*GetBuiltInGenerators` in `EcuScriptLoader.cs` | line ~240 |
| 3 | `EcuStateMachine._generators` is readonly | grep `readonly Dictionary` in `EcuStateMachine.cs` | line 11 |
| 4 | `CliArgs` has no `GeneratorDir` | grep `GeneratorDir` in `CliArgs.cs` | 0 matches |
| 5 | `HilRunRequest` has no `GeneratorDir` | grep `GeneratorDir` in `HilRunRequest.cs` | 0 matches |
| 6 | `HeadlessHostBuilder` virtual ECU no external | grep `EcuScriptLoader.Load(args.EcuScriptPath` in `HeadlessHostBuilder.cs` | line 49, no 2nd arg |
| 7 | `Program.cs` simulate no manager | grep `GeneratorPluginManager` in `Program.cs` | 0 matches |
| 8 | `Program.cs` host not disposed | grep `var host = new EcuSimulatorHost` in `Program.cs` | line 49, no `using` |
| 9 | `MatrixConfigLoader.Load` 1 param | grep `public static MatrixConfig Load` in `MatrixConfigLoader.cs` | line 73, only `string path` |
| 10 | `GeneratorPluginLoader` not in File Inventory | grep `GeneratorPluginLoader.cs` in spec File Inventory | "不在 File Inventory" |

---

## Sprint 1: Core 变更 + 接线参数 (4 tests)

### Inc 1: `BuiltInGenerators` 单一来源 + `EcuScriptLoader` 改调

**Files**: `Infrastructure/HIL/Generators/BuiltInGenerators.cs` (NEW), `Infrastructure/HIL/EcuScriptLoader.cs` (MODIFY), `Infrastructure.Tests/HIL/Generators/GeneratorPluginLoaderTests.cs` (MODIFY)

| Test | Description |
|------|-------------|
| `BuiltInGenerators_CreateAll_ReturnsFiveGenerators` | `CreateAll()` 返回 5 个实例，Name 分别为 `SecurityAccessSeed` / `SecurityAccessVerifyKey` / `ClearDtc` / `DidReadout` / `DidWrite` |
| `EcuScriptLoader_UsesBuiltInGenerators_NoPrivateMethod` | grep `GetBuiltInGenerators` in `EcuScriptLoader.cs` -> 0 matches；`ParseEcuScript` 调 `BuiltInGenerators.CreateAll()` |

**Implementation**:
- 新建 `BuiltInGenerators.cs`，`CreateAll()` 返回 5 个 built-in 实例（从 `EcuScriptLoader.GetBuiltInGenerators()` 搬移）
- `EcuScriptLoader.cs`：删除 `GetBuiltInGenerators()`（`:240-250`），`ParseEcuScript`（`:46`）改调 `BuiltInGenerators.CreateAll()`
- `EcuScriptLoader.cs:6` 已有 `using PeakCan.Host.Infrastructure.HIL.Generators;`，无需新增 using

### Inc 2: `EcuStateMachine.ReplaceGenerators` 并发模型

**Files**: `Core/HIL/Contracts/EcuStateMachine.cs` (MODIFY), `Core.Tests/HIL/Contracts/EcuStateMachineReplaceGeneratorsTests.cs` (NEW)

| Test | Description |
|------|-------------|
| `ReplaceGenerators_UpdatesGenerators_ProcessRequestUsesNew` | 替换后 `ProcessRequest` 用新 generator 响应；`_currentState`/`_context` 保留 |
| `ReplaceGenerators_ConcurrentWithProcessRequest_NoException` | 替换与 `ProcessRequest` 并发 -> 不抛、无竞态 |

**Implementation**:
- 新增 `using System.Threading;`
- `_generators` 由 `readonly` 改为 `volatile`（`:11`）
- 新增 `ReplaceGenerators(IEnumerable<IEcuResponseGenerator>)`：`ToDictionary` + `Interlocked.Exchange`
- `ProcessRequest`（`:58`）不改 -- `volatile` 读保证获取语义

### Inc 3: `CliArgs.GeneratorDir` + `HilRunRequest.GeneratorDir` + `ToCliArgs`

**Files**: `Infrastructure/Cli/CliArgs.cs` (MODIFY), `Core/HIL/HilRunRequest.cs` (MODIFY), `Infrastructure/HIL/HilRunRequestExtensions.cs` (MODIFY), `Infrastructure.Tests/HIL/Generators/GeneratorPluginLoaderTests.cs` or new test file

| Test | Description |
|------|-------------|
| `CliArgsParser_GeneratorDir_ParsesFlag` | `--generator-dir /tmp/gens` sets `GeneratorDir` |
| `ToCliArgs_PassesGeneratorDir` | `HilRunRequest.GeneratorDir` -> `CliArgs.GeneratorDir` |

**Implementation**:
- `CliArgs` record 末尾加 `string? GeneratorDir = null`（`ExportFramesDir` 之后）
- `CliArgsParser.Parse`：新增 `string? generatorDir = null;` + `case "--generator-dir": generatorDir = args[++i]; break;`
- **三处** `new CliArgs(...)` 构造调用点（`:87,100,117`）均加 `GeneratorDir: generatorDir` 命名参数
- `PrintHelp` 加 `--generator-dir <path>` 行
- `HilRunRequest` record 末尾加 `string? GeneratorDir = null`（`EnableAnalyze` 之后）
- `ToCliArgs`：末尾加 `GeneratorDir: r.GeneratorDir` 命名参数

**Key constraint**: `CliArgsParser.Parse` 有 3 处 `new CliArgs(...)` positional 构造（ODX import `:87`、simulate `:100`、normal `:117`），全部需加 `GeneratorDir: generatorDir`。只解析不传参会导致 `GeneratorDir` 恒 null。

---

## Sprint 2: 路径 1 接线 -- HIL 测试模式 (3 tests)

### Inc 4: `HeadlessHostBuilder` 虚拟 ECU + Matrix 分支接线

**Files**: `Infrastructure/HIL/HeadlessHostBuilder.cs` (MODIFY)

| Test | Description |
|------|-------------|
| `HeadlessHostBuilder_VirtualEcu_GeneratorDir_LoadsExternal` | `--generator-dir` -> 虚拟 ECU 模式加载外部 generators，`DynamicResponse` 用外部 generator 响应 |
| `HeadlessHostBuilder_Matrix_GeneratorDir_LoadsExternal` | `--generator-dir` -> Matrix 模式多 ECU 各自加载 external |

**Implementation**:
- 虚拟 ECU 分支（`:49`）：
  ```csharp
  var external = GeneratorPluginLoader.LoadFromDirectory(args.GeneratorDir);
  var ecuScript = EcuScriptLoader.Load(args.EcuScriptPath!, external);
  ```
- Matrix 分支（`:59`）：
  ```csharp
  var external = GeneratorPluginLoader.LoadFromDirectory(args.GeneratorDir);
  var config = MatrixConfigLoader.Load(args.MatrixPath!, external);
  ```
- Hardware（`:34-43`）与 TraceReplay（`:66-76`）分支不改

**Key constraint**: `LoadFromDirectory(null)` 返回空列表（`Directory.Exists(null)` = false），安全。

### Inc 5: `MatrixConfigLoader` 三层签名透传

**Files**: `Infrastructure/HIL/MatrixConfigLoader.cs` (MODIFY)

| Test | Description |
|------|-------------|
| `MatrixConfigLoader_Parse_ExternalGenerators_PassedToEcuScriptLoader` | `Parse(json, basePath, external)` -> ECU 脚本含外部 generator |

**Implementation**:
- `Parse(string json, string? basePath = null, IEnumerable<IEcuResponseGenerator>? externalGenerators = null)`
  - `:47` `EcuScriptLoader.Load(fullPath, externalGenerators)`
  - `:52` `EcuScriptLoader.ParseEcuScript(ecuEl, externalGenerators)`
- `LoadFromFile(string path, IEnumerable<IEcuResponseGenerator>? externalGenerators = null)`
  - 调 `Parse(json, basePath, externalGenerators)`
- `Load(string path, IEnumerable<IEcuResponseGenerator>? externalGenerators = null)`
  - 调 `LoadFromFile(path, externalGenerators)`

**Key constraint**: 默认值 `= null` 使现有调用方零改动。

### Inc 6: HIL 测试接线端到端验证

**Files**: `Infrastructure.Tests/HIL/Generators/GeneratorHotReloadIntegrationTests.cs` (NEW, partial)

| Test | Description |
|------|-------------|
| `HilTestMode_ExternalGenerator_ResponseFromPlugin` | 集成：编译测试插件 DLL 到临时目录 -> `--generator-dir` -> HIL 测试虚拟 ECU 响应来自外部插件 |

**Implementation**:
- 新建 `Infrastructure.Tests/HIL/Generators/TestPluginCompiler.cs`（helper）：用
  `Microsoft.CodeAnalysis.CSharp.CSharpCompilation` 把内联 C# 源码编译为 DLL，`MetadataReference`
  引用 `PeakCan.Host.Core`（`IEcuResponseGenerator`）+ `System.Runtime`（`typeof(object)` 程序集）
- `TestPluginCompiler.CompileAsync(name, sourceCode, outputDir)` → 返回 DLL 路径
- 用 helper 编译一个 `IEcuResponseGenerator` 实现到临时目录 DLL
- 用 `HeadlessHostBuilder.Build` 构建虚拟 ECU host，传 `GeneratorDir`
- 发送 UDS 请求，验证响应来自外部 generator

---

## Sprint 3: 路径 2 热加载 -- `GeneratorPluginManager` + `--simulate` (6 tests)

### Inc 7: `GeneratorPluginManager` + `GeneratorLoadContext`

**Files**: `Infrastructure/HIL/Generators/GeneratorPluginManager.cs` (NEW), `Infrastructure.Tests/HIL/Generators/GeneratorPluginManagerTests.cs` (NEW)

| Test | Description |
|------|-------------|
| `Manager_LoadsPlugins_CurrentContainsExternal` | 目录含插件 -> `Current` 含 external（仅外部，不含 built-in） |
| `Manager_NullDir_CurrentEmpty_NoWatcher` | `GeneratorDir=null` -> `Current` empty、不启动 watcher、不抛 |
| `Manager_DllChanged_RaisesEvent_UpdatesCurrent` | 写入 dll -> debounce 后 `GeneratorsChanged` 触发、`Current` 更新 |
| `Manager_BadDll_RetainsOld_DoesNotThrow` | 写坏 dll -> `Current` 不变、不抛（含 IOException 文件锁） |
| `Manager_Dispose_ClearsEvent_ReleasesWatcher` | Dispose 后事件清空、watcher 释放 |

**Implementation**:
- `GeneratorLoadContext`（internal sealed）：`isCollectible: true` + `AssemblyDependencyResolver` + `Load` 重写
- `GeneratorPluginManager`（sealed, IDisposable）：
  - `private volatile IReadOnlyList<IEcuResponseGenerator> _current;`
  - 构造：null/空 -> `Current = empty`，不启动 watcher；否则 ALC 加载 + `FileSystemWatcher` + debounce Timer
  - `Reload()`：每个 DLL 独立 `GeneratorLoadContext` -> 新 external 列表 -> `Interlocked.Exchange` -> 触发事件 -> 旧 ALC 逐个 Unload
  - 失败重试：`BadImageFormatException` / `IOException` / `FileLoadException` / `FileNotFoundException` -> 200ms x 3 次
  - 失败路径：所有新创建的 ALC 逐个 Unload（包括加载成功的），保留旧 `_current`
  - `Dispose()`：watcher.Dispose + Timer.Dispose(WaitHandle) + `GeneratorsChanged = null`；不调 ALC.Unload
  - `public event Action? GeneratorsChanged;`
  - `ApplyTo(EcuStateMachine stateMachine)`：立即 `ReplaceGenerators(MergeGenerators(BuiltInGenerators.CreateAll(), Current))`
    + 订阅 `GeneratorsChanged` 重复同一替换（可测，Inc 9 用；built-in 由 `ApplyTo` 内部合并，不丢失）

### Inc 8: ALC 替换同名 DLL 端到端

**Files**: `Infrastructure.Tests/HIL/Generators/GeneratorHotReloadIntegrationTests.cs` (NEW, continued)

| Test | Description |
|------|-------------|
| `ALC_ReplaceSameDll_NewVersionTakesEffect` | 覆盖同名 dll -> `GC.Collect()+WaitForPendingFinalizers` 后新版本生效（证明非 LoadFrom 缓存） |

**Implementation**:
- `TestPluginCompiler` 编译 v1 源码（响应字节 A）→ 临时目录 v1.dll
- manager 加载 → 验证 `Current` 含 v1 generator（响应 A）
- 覆盖同名 v1.dll 为 v2（响应字节 B，同 AssemblyName）→ debounce 后 `GeneratorsChanged`
  → `GC.Collect()+WaitForPendingFinalizers` → 验证 `Current` 含 v2 generator（响应 B）
- 验证 v1 generator 不再被调用（证明非 `Assembly.LoadFrom` 缓存）

### Inc 9: `ApplyTo` 接线 + `Program.cs` --simulate 薄胶水

**Files**: `PeakCan.Host.Cli/Program.cs` (MODIFY), `Infrastructure.Tests/HIL/Generators/GeneratorPluginManagerTests.cs` (MODIFY)

| Test | Description |
|------|-------------|
| `Manager_ApplyTo_ReplacesGenerators_OnEvent` | `ApplyTo` 后触发 `GeneratorsChanged` → `stateMachine.ProcessRequest` 用新 generator 响应；built-in 不丢失 |

**Implementation**:
- `GeneratorPluginManager.ApplyTo(EcuStateMachine)`（Inc 7 定义）——Program.cs 不再内联 lambda
- 新增 `using PeakCan.Host.Infrastructure.HIL.Generators;`
- `--simulate` 分支（`:44-56`）改造：
  ```csharp
  using var manager = new GeneratorPluginManager(cli.GeneratorDir);
  var ecuScript = EcuScriptLoader.Load(cli.EcuScriptPath!, manager.Current);
  manager.ApplyTo(ecuScript.StateMachine);   // 立即替换 + 订阅热替换（合并 built-in）
  var handle = HeadlessHostBuilder.ParseChannelHandle(cli.HardwareChannel!);
  var channel = new PeakCanChannel(new ChannelId(handle), null);
  using var host = new EcuSimulatorHost(channel, ecuScript.CanIds, ecuScript.StateMachine, null);  // L3 修复既有泄漏
  // ... Console.CancelKeyPress 等原有逻辑 ...
  await host.RunAsync(cts.Token);
  ```
- `using var host` 修复既有泄漏（L3）

**Key constraint**: `using` 声明逆序 dispose -- 先 `host` 再 `manager`。`manager.Dispose` 清事件后 `host` 仍可安全 dispose。`--simulate` 阻塞式 `RunAsync` 无法在单测跑通（plan review 修正 2），接线逻辑由 `ApplyTo` 测试覆盖。

---

## Post-checks (verify after coding)

| # | Check | Command | Expected |
|---|-------|---------|----------|
| 0 | Build passes | `dotnet build` | 0 errors |
| 1 | All new tests green | `dotnet test --filter "FullyQualifiedName~GeneratorPluginManager\|FullyQualifiedName~ReplaceGenerators\|FullyQualifiedName~GeneratorHotReload\|FullyQualifiedName~BuiltInGenerators\|FullyQualifiedName~CliArgsParser_GeneratorDir\|FullyQualifiedName~ToCliArgs_PassesGeneratorDir\|FullyQualifiedName~MatrixConfigLoader_Parse_External"` | 0 failed |
| 2 | Existing HIL tests green | `dotnet test --filter "FullyQualifiedName~HIL"` | 0 new failures (4 既有 TraceViewer 失败除外) |
| 3 | `GetBuiltInGenerators` deleted | grep `GetBuiltInGenerators` in `EcuScriptLoader.cs` | 0 matches |
| 4 | `_generators` is volatile | grep `volatile Dictionary` in `EcuStateMachine.cs` | 1 match |
| 5 | `GeneratorPluginLoader.cs` unchanged | `git diff src/PeakCan.Host.Infrastructure/HIL/Generators/GeneratorPluginLoader.cs` | no changes |
| 6 | `Program.cs` has `using var host` | grep `using var host` in `Program.cs` | 1 match |
| 7 | `CliArgsParser` 3构造点传 `GeneratorDir` | grep `GeneratorDir: generatorDir` in `CliArgs.cs` | 3 matches |
| 8 | `MatrixConfigLoader` 三层有默认值 | grep `externalGenerators = null` in `MatrixConfigLoader.cs` | 3 matches |

---

## Risk Notes

- **Roslyn 测试编译**: `TestPluginCompiler` 依赖 `Microsoft.CodeAnalysis.CSharp`（Central Package
  Management 加 `PackageVersion`，Infrastructure.Tests.csproj 加 `PackageReference`）。编译只需引用
  Core + System.Runtime 程序集；插件无 `.deps.json` 时 `AssemblyDependencyResolver.ResolveAssemblyToPath`
  返回 null，依赖全 fallback 默认 ALC —— 对引用 Core 的简单插件足够（与 spec E1 一致）。
- **ALC + xUnit**: xUnit 进程内 ALC 卸载可行但需注意 `GC.Collect()` 时机（M2）。集成测试中 `GC.Collect()+WaitForPendingFinalizers()` 确保旧 ALC 回收后再验证。
- **FileSystemWatcher 文件锁**: DLL 编译写入中触发 `Changed`，重试 200ms x 3 次。大 DLL 编译可能超过 600ms -- 测试用小 DLL（< 10KB）。
- **`volatile` + `Interlocked.Exchange`**: C# 编译器允许 `volatile` 字段作 `ref` 传给 `Interlocked` 方法，无 CS0420 警告。
- **插件依赖解析**: `AssemblyDependencyResolver` + `Load` 返回 null fallback 到默认 ALC，保证 `IEcuResponseGenerator` 类型同一性。插件无 `.deps.json` 时 `ResolveAssemblyToPath` 返回 null，全部 fallback -- 对引用 `PeakCan.Host.Core` 的简单插件足够。
