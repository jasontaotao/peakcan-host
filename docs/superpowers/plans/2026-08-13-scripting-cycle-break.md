# Scripting 循环依赖破环 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 消除 `ScriptEngine` ↔ `ScriptUtilities` 的构造期循环依赖，删除 `AppHostBuilder` 里的反射 hack，同时不破坏任何现有测试构造调用。

**Architecture:** 引入 `IScriptOutputSink` 接口作为脚本输出汇聚点，`ScriptEngine` 实现该接口、`ScriptUtilities` 依赖该接口（而非引擎全量）。`ScriptEngine` 对 `ScriptUtilities` 的依赖改用 `Lazy<ScriptUtilities>` 延迟注入，从 ctor 层面打破双向依赖。保留 4-arg back-compat 构造器使现有测试零改动。

**Tech Stack:** .NET 10, WPF, Microsoft.Extensions.DependencyInjection, xUnit + NSubstitute + FluentAssertions。

## Global Constraints

- 不动 `PeakCan.Host.Core` / `PeakCan.Host.Infrastructure` 层（NetArchTest 边界）。
- 现有测试构造调用 `new ScriptEngine(logger, null, null, null)` 与 `new ScriptEngine(logger, null, null, null, options)` **必须保持编译通过**（back-compat ctor 保证）。
- 生产代码注释：面向用户/业务逻辑用中文，技术 API/接口用英文。
- 提交信息用 conventional commits（`refactor:` / `test:` / `chore:`），不加 Co-Authored-By（全局已禁用 attribution）。

---

### Task 1: 新建 `IScriptOutputSink` 接口 + `ScriptUtilities` 依赖接口

**Files:**
- Create: `src/PeakCan.Host.App/Services/Scripting/IScriptOutputSink.cs`
- Modify: `src/PeakCan.Host.App/Services/Scripting/ScriptUtilities.cs`
- Test: `tests/PeakCan.Host.App.Tests/Services/Scripting/ScriptUtilitiesTests.cs`（新建）

**Interfaces:**
- Produces: `IScriptOutputSink { void EmitOutput(ScriptOutputLine line); }`（`ScriptOutputLine` 定义在 `ScriptEngine.cs`，已存在）
- Produces: `ScriptUtilities(ILogger<ScriptUtilities>, IScriptOutputSink)` 新 ctor 签名

- [ ] **Step 1: 写失败测试** —— 新建 `ScriptUtilitiesTests.cs`

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PeakCan.Host.App.Services.Scripting;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Scripting;

public sealed class ScriptUtilitiesTests
{
    private readonly ILogger<ScriptUtilities> _logger = Substitute.For<ILogger<ScriptUtilities>>();
    private readonly IScriptOutputSink _sink = Substitute.For<IScriptOutputSink>();

    [Fact]
    public void Log_EmitsInfoLine_ToSink()
    {
        var utils = new ScriptUtilities(_logger, _sink);

        utils.Log("hello");

        _sink.Received(1).EmitOutput(Arg.Is<ScriptOutputLine>(
            l => l.Level == ScriptOutputLevel.Info && l.Message == "hello"));
    }

    [Fact]
    public void Warn_EmitsWarningLine_ToSink()
    {
        var utils = new ScriptUtilities(_logger, _sink);

        utils.Warn("careful");

        _sink.Received(1).EmitOutput(Arg.Is<ScriptOutputLine>(
            l => l.Level == ScriptOutputLevel.Warning && l.Message == "careful"));
    }

    [Fact]
    public void Error_EmitsErrorLine_ToSink()
    {
        var utils = new ScriptUtilities(_logger, _sink);

        utils.Error("boom");

        _sink.Received(1).EmitOutput(Arg.Is<ScriptOutputLine>(
            l => l.Level == ScriptOutputLevel.Error && l.Message == "boom"));
    }
}
```

- [ ] **Step 2: 跑测试确认失败（编译失败）**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~ScriptUtilitiesTests"`
Expected: FAIL —— `IScriptOutputSink` 类型不存在，且 `ScriptUtilities` 尚无接受该类型的 ctor。

- [ ] **Step 3: 实现接口 + 改造 `ScriptUtilities`**

新建 `IScriptOutputSink.cs`：

```csharp
namespace PeakCan.Host.App.Services.Scripting;

/// <summary>
/// Sink for script output lines. Decouples <see cref="ScriptUtilities"/>
/// from the full <see cref="ScriptEngine"/> so the two no longer form a
/// constructor cycle — the engine is one implementation, tests substitute
/// a fake.
/// </summary>
public interface IScriptOutputSink
{
    void EmitOutput(ScriptOutputLine line);
}
```

修改 `ScriptUtilities.cs`（`_engine` 字段、ctor、3 处 `EmitOutput` 调用）：

```csharp
// 字段：ScriptEngine _engine → IScriptOutputSink _sink
private readonly IScriptOutputSink _sink;

public ScriptUtilities(
    ILogger<ScriptUtilities> logger,
    IScriptOutputSink sink)
{
    ArgumentNullException.ThrowIfNull(logger);
    ArgumentNullException.ThrowIfNull(sink);

    _logger = logger;
    _sink = sink;
}

// Log/Warn/Error 内：_engine.EmitOutput(...) → _sink.EmitOutput(...)
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~ScriptUtilitiesTests"`
Expected: PASS（3 个测试全绿）。

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/Services/Scripting/IScriptOutputSink.cs \
        src/PeakCan.Host.App/Services/Scripting/ScriptUtilities.cs \
        tests/PeakCan.Host.App.Tests/Services/Scripting/ScriptUtilitiesTests.cs
git commit -m "refactor(scripting): extract IScriptOutputSink, decouple ScriptUtilities from ScriptEngine"
```

---

### Task 2: `ScriptEngine` 实现 `IScriptOutputSink` + `Lazy` 破环

**Files:**
- Modify: `src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs`
- Modify: `src/PeakCan.Host.App/Services/Scripting/ScriptEngine/ScriptHelpersFlow.cs`
- Modify: `src/PeakCan.Host.App/Services/Scripting/ScriptEngine/CreateEngineFlow.cs`
- Test: `tests/PeakCan.Host.App.Tests/Services/Scripting/ScriptEngineTests.cs`（追加契约测试）

**Interfaces:**
- Consumes: `IScriptOutputSink`（Task 1 产出）
- Produces: `ScriptEngine : IScriptOutputSink`；`_utilities` 字段类型 `Lazy<ScriptUtilities>?`

- [ ] **Step 1: 写失败测试** —— 在 `ScriptEngineTests.cs` 追加契约测试

```csharp
[Fact]
public void ScriptEngine_Implements_IScriptOutputSink()
{
    // Act — 4-arg back-compat ctor 仍可用（末参 null → null Lazy）
    var engine = new ScriptEngine(_logger, null, null, null);

    // Assert — engine 可作为 IScriptOutputSink 使用
    IScriptOutputSink sink = engine;
    Assert.NotNull(sink);
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~ScriptEngine_Implements_IScriptOutputSink"`
Expected: FAIL —— `ScriptEngine` 尚未实现 `IScriptOutputSink`。

- [ ] **Step 3: 实现**

`ScriptHelpersFlow.cs` —— `EmitOutput` 改 public（接口实现）：

```csharp
public void EmitOutput(ScriptOutputLine line)
{
    OutputReceived?.Invoke(line);
}
```

`ScriptEngine.cs` —— 类声明 + 字段 + ctor：

```csharp
public sealed partial class ScriptEngine : IDisposable, IScriptOutputSink
{
    // 字段：ScriptUtilities? _utilities → Lazy<ScriptUtilities>? _utilities
    private readonly Lazy<ScriptUtilities>? _utilities;

    // 4-arg back-compat ctor（签名不变，内部包装 Lazy）
    public ScriptEngine(
        ILogger<ScriptEngine> logger,
        CanApi? canApi,
        DbcApi? dbcApi,
        ScriptUtilities? utilities)
        : this(logger, canApi, dbcApi,
               utilities is null ? null : new Lazy<ScriptUtilities>(() => utilities),
               ScriptEngineOptions.Default)
    {
    }

    // 5-arg ctor：第 4 参 ScriptUtilities? → Lazy<ScriptUtilities>?
    internal ScriptEngine(
        ILogger<ScriptEngine> logger,
        CanApi? canApi,
        DbcApi? dbcApi,
        Lazy<ScriptUtilities>? utilities,
        ScriptEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _canApi = canApi;
        _dbcApi = dbcApi;
        _utilities = utilities;
        _options = options ?? ScriptEngineOptions.Default;
    }
}
```

`CreateEngineFlow.cs` —— `_utilities` 取值处加 `.Value`：

```csharp
if (_utilities is not null)
{
    var utils = _utilities.Value;  // 延迟解析，打破 ctor 循环
    engine.AddHostObject("log", (Action<string>)((msg) => utils.Log(msg)));
    engine.AddHostObject("warn", (Action<string>)((msg) => utils.Warn(msg)));
    engine.AddHostObject("error", (Action<string>)((msg) => utils.Error(msg)));
    engine.AddHostObject("delay", (Func<int, Task>)((ms) => utils.Delay(ms, ct)));
    engine.AddHostObject("hex", (Func<int, string?>?)((v) => utils.Hex(v)));
    engine.AddHostObject("toHex", (Func<byte[]?, string?>?)((b) => utils.ToHex(b)));
}
```

- [ ] **Step 4: 跑全量 Scripting 测试确认通过**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~Scripting"`
Expected: PASS —— 既有 `ScriptEngineTests` / `ScriptEngineSecurityTests` / `ScriptEngineReflectionGuardTests` 全绿（back-compat ctor 保证 4-arg/5-arg 的 `null` 实参零改动）。

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs \
        src/PeakCan.Host.App/Services/Scripting/ScriptEngine/ScriptHelpersFlow.cs \
        src/PeakCan.Host.App/Services/Scripting/ScriptEngine/CreateEngineFlow.cs \
        tests/PeakCan.Host.App.Tests/Services/Scripting/ScriptEngineTests.cs
git commit -m "refactor(scripting): make ScriptEngine implement IScriptOutputSink, break ctor cycle via Lazy"
```

---

### Task 3: DI 注册改 `Lazy` + 删除反射 hack

**Files:**
- Modify: `src/PeakCan.Host.App/Composition/AppHostBuilder.cs`（`Build()` 内 Scripting 注册块）

**Interfaces:**
- Consumes: `ScriptEngine` 的 5-arg ctor（`Lazy<ScriptUtilities>?` 第 4 参，Task 2 产出）、`IScriptOutputSink`（Task 1 产出）

- [ ] **Step 1: 删除反射 hack、替换为 Lazy 注入**

定位 `AppHostBuilder.cs` 内 `ScriptEngine` 的注册工厂（含 `GetField("_utilities", ...).SetValue` 的块，约 line 154-183），整体替换为：

```csharp
// v1.0.0: Scripting engine. ScriptEngine → ScriptUtilities 是单向依赖
// (CreateEngineFlow 暴露 log/warn/error 给 JS)；反向通过 Lazy<ScriptUtilities>
// 延迟解析，从 ctor 层面打破循环，替代旧的反射 field 注入。
builder.Services.AddSingleton<PeakCan.Host.App.Services.Scripting.ScriptEngine>(sp =>
    new PeakCan.Host.App.Services.Scripting.ScriptEngine(
        sp.GetRequiredService<ILogger<PeakCan.Host.App.Services.Scripting.ScriptEngine>>(),
        sp.GetService<PeakCan.Host.App.Services.Scripting.CanApi>(),
        sp.GetService<PeakCan.Host.App.Services.Scripting.DbcApi>(),
        new Lazy<PeakCan.Host.App.Services.Scripting.ScriptUtilities>(
            () => sp.GetRequiredService<PeakCan.Host.App.Services.Scripting.ScriptUtilities>()),
        sp.GetRequiredService<PeakCan.Host.App.Services.Scripting.ScriptEngineOptions>()));
builder.Services.AddSingleton<PeakCan.Host.App.Services.Scripting.CanApi>();
builder.Services.AddSingleton<PeakCan.Host.App.Services.Scripting.DbcApi>();
// IScriptOutputSink forward 到 ScriptEngine（单一实现）。
builder.Services.AddSingleton<PeakCan.Host.App.Services.Scripting.IScriptOutputSink>(sp =>
    sp.GetRequiredService<PeakCan.Host.App.Services.Scripting.ScriptEngine>());
builder.Services.AddSingleton<PeakCan.Host.App.Services.Scripting.ScriptUtilities>();
```

删除同一块内旧的 `ScriptUtilities` 反射创建逻辑（`new ScriptUtilities(..., engine)` + `GetField(...).SetValue(...)`）以及旧的 `ScriptUtilities` factory 注册（它用 `new ScriptUtilities(logger, engine)` 传 `ScriptEngine`，已不再匹配新 ctor）。

- [ ] **Step 2: 跑 AppHostBuilder + 全量测试确认通过**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~AppHostBuilder"`
Expected: PASS —— DI 组合不再抛循环依赖异常。

Run: `dotnet test PeakCan.Host.slnx -c Debug`（全量）
Expected: 全绿（约 2300 通过；无新增失败）。

- [ ] **Step 3: Commit**

```bash
git add src/PeakCan.Host.App/Composition/AppHostBuilder.cs
git commit -m "refactor(scripting): replace reflection field-injection with Lazy DI"
```

---

## Self-Review 记录

- **Spec 覆盖**：Phase 1（§4）三项改造点全部落到 Task 1-3：接口抽离（T1）、Lazy 破环 + 接口实现（T2）、DI 注册替换 + 删反射 hack（T3）。
- **Type 一致性**：`IScriptOutputSink.EmitOutput(ScriptOutputLine)` 签名在 T1/T2/T3 一致；`Lazy<ScriptUtilities>?` 在 T2/T3 一致。
- **兼容性验证**：back-compat 4-arg ctor 保证 `ScriptEngineTests.cs` 等 13+ 处 `new ScriptEngine(..., null)` 调用零改动（T2 Step 4 验证）。
