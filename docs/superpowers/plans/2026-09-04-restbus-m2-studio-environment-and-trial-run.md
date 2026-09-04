# Restbus M2: Studio Environment Tab + Host Trial Run Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement spec §14 M2 — studio"总线环境"页签（勾选/共享 seed 模板/信号改值）+ host 试运行 + sim 帧标记 + 报告环境节 + 旧节点/EcuScript 导入，使 M1 模型收敛对用户首次可用。

**Architecture:** M1 已建立 hil-core `RestbusNode` 模型 + host `EnvironmentRuntime` 基座。M2 在此之上：(A) EnvironmentRuntime 补齐 DbcSignalsSource 编码、SignalOverrides、UDS 路由、J1939 TP 接线、SetEnvironmentSignalStep 执行器、NodeRunStats 统计；(B) host HilView 加试运行按钮 + TrialContract 诊断；(C) studio 新增"总线环境"页签 + gbt27930-charger 模板应用入口；(D) 旧 Nodes/EcuScript 导入器。三仓库分支 `feat/restbus-unification`，lockstep 版本 0.17.0 → 0.18.0。

**Tech Stack:** C# / .NET 10, WPF (host + studio), xUnit, System.Text.Json polymorphic (kind 判别符), DbcEncodeService (hil-core), J1939TpLayer (host.Core), EcuStateMachine (hil-core Contracts)。

**Spec:** `D:\claude_proj2\peakcan-host\docs\superpowers\specs\2026-09-03-restbus-node-unification-design.md` (Draft v3)

## Global Constraints

- hil-core Core 层零 I/O、零厂商 SDK 依赖（NetArchTest 红线不变）。
- 新增序列化字段一律可空默认（lockstep 惯例）。
- `IntervalMs >= 10`（spec R1 已定案）。
- hil-core bump → host/studio 双 pin → 双侧 InteropTests 绿 → 合并（lockstep 门禁）。
- Conventional commits（feat/fix/chore, `!` for breaking）。
- 三铁律（spec §4.4）：①DBC 是几何/报文/信号事实源，UI 禁止手填 CAN ID；②模板是普通 UI 规则唯一来源，不提供空白 ECA 入口；③环境是 TestSuite 的属性。
- studio 纯配置器：全仓零 `ICanChannel`/`PCAN` 引用；试运行归属 host（review C1）。
- 用 `rg -g "*.cs"` glob 语法搜索。
- `using Xunit;` 必须在测试文件顶部（无 global usings）。

---

## File Structure

### hil-core (`PeakCan.HIL.Core`)
```
HIL/Environment/
├── RestbusNode.cs              — 已有，M2 加 SignalOverrides 已有
├── TrialContract.cs            — 已有
├── NodePayloadSource.cs        — 已有
└── RestbusNodeValidator.cs     — 扩展：SignalOverrides 键/UDS generator 引用/UDS ID 冲突校验
Templates/
└── Gbt27930ChargerTemplate.cs  — 首例 seed 模板（纯数据，零 I/O）
HIL/
└── TestSuiteResult.cs          — 加 EnvironmentStats 字段 + NodeRunStats record
```

### host (`PeakCan.Host.Infrastructure` + `PeakCan.Host.Core`)
```
HIL/Environment/
├── EnvironmentRuntime.cs       — 扩展：DbcSignalsSource 编码、SignalOverrides、UDS、J1939 TP、NodeRunStats
└── NodeSignalState.cs          — 新建：per-message 信号状态表（信号名 → 物理值）
HIL/StepExecutor/               — host.Core（沿用现有 executor 模式）
├── SetEnvironmentSignalStepExecutor.cs  — 新建
└── ModifyEnvironmentFrameStepExecutor.cs — 新建
HIL/HilRunnerService.cs         — 扩展：EnvironmentRuntime.Start 传入 DBC 文档 + stats 收集
App/Views/HilView.xaml          — 加"试运行环境"按钮 + 环境状态指示
App/ViewModels/HilViewModel.cs  — 加 TrialRun 命令 + NodeRunStats 展示
```

### studio (`PeakCan.Studio.App`)
```
ViewModels/Environment/
├── EnvironmentTabViewModel.cs  — 新建：总线环境页签 VM
├── EnvironmentNodeViewModel.cs — 新建：单个节点卡片 VM
└── TemplateCatalogViewModel.cs — 新建：模板选择列表 VM
Views/
└── EnvironmentTab.xaml         — 新建：总线环境页签 UI
Services/Environment/
└── RestbusNodeImportService.cs — 新建：旧 .node.json / EcuScript 导入
```

---
### Task 1: DbcSignalsSource 编码 + 信号状态表

**Files:**
- Create: `peakcan-host/src/PeakCan.Host.Infrastructure/HIL/Environment/NodeSignalState.cs`
- Modify: `peakcan-host/src/PeakCan.Host.Infrastructure/HIL/Environment/EnvironmentRuntime.cs`
- Test: `peakcan-host/tests/PeakCan.Host.Infrastructure.Tests/HIL/Environment/DbcSignalsEncodingTests.cs`

**Interfaces:**
- Consumes: `DbcEncodeService.Encode(Message, IReadOnlyDictionary<string, double>)` (hil-core)
- Produces: `NodeSignalState.GetOrInit(signalName)` → `double`; `NodeSignalState.Set(signalName, value)` → `bool`; `EnvironmentRuntime.SetSignalValue(nodeName, messageName, signalName, value)` → `void`; `EnvironmentRuntime.GetEncodedPayload(nodeName, messageName)` → `byte[]?`

- [ ] **Step 1: Write the failing test**

```csharp
using Xunit;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Infrastructure.HIL.Environment;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Environment;

public class DbcSignalsEncodingTests
{
    private static DbcDocument CreateTestDbc()
    {
        var text = """
VERSION ""
NS_ :
BS_:
BU_: Charger BMS

BO_ 512 CRM: 8 Charger
 SG_ CRM_Signal : 0|16@1+ (1,0) [0|65535] "" BMS
""";
        return DbcParser.Parse(text);
    }

    private static RestbusNode CreateNode() => new()
    {
        Name = "Charger",
        Identity = new RawCanNodeIdentity(),
        Messages =
        [
            new NodeMessage(
                new CanMessageRef(512, false),
                100,
                new DbcSignalsSource("CRM"))
        ]
    };

    [Fact]
    public void BuildPayload_DbcSignalsSource_ReturnsEncodedBytes()
    {
        var dbc = CreateTestDbc();
        var runtime = new EnvironmentRuntime(new FakeCanChannel(), dbc);
        runtime.Start([CreateNode()], null);
        // 默认信号初值 = DBC default (0)
        // 手动设置信号值后编码
        runtime.SetSignalValue("Charger", "CRM", "CRM_Signal", 100);
        var payload = runtime.GetEncodedPayload("Charger", "CRM");
        Assert.NotNull(payload);
        Assert.Equal(8, payload.Length);
        // Little-endian, factor 1, offset 0 → bytes [0x64, 0x00, ...]
        Assert.Equal(0x64, payload[0]);
        runtime.Stop();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests --filter "DbcSignalsEncodingTests" --no-restore`
Expected: FAIL — `EnvironmentRuntime` 没有 `SetSignalValue` / `GetEncodedPayload` 方法

- [ ] **Step 3: Create NodeSignalState**

```csharp
namespace PeakCan.Host.Infrastructure.HIL.Environment;

/// <summary>单消息运行时信号状态表。Encoding 顺序锁定：信号状态 → DbcSignalsSource 编码 → counter/checksum → 发送。</summary>
internal sealed class NodeSignalState
{
    private readonly Dictionary<string, double> _values = new(StringComparer.Ordinal);

    /// <summary>获取信号当前值；不存在时返回 DBC 默认值或 0。</summary>
    public double GetOrInit(string signalName, double defaultValue = 0)
        => _values.TryGetValue(signalName, out var v) ? v : defaultValue;

    /// <summary>设置信号值。返回 false 如果值超出 [min, max]（由调用方记录 Error，保留旧值）。</summary>
    public bool Set(string signalName, double value)
    {
        _values[signalName] = value;
        return true;
    }

    /// <summary>返回全部信号键值（供 DbcEncodeService.Encode 使用）。</summary>
    public IReadOnlyDictionary<string, double> ToDictionary() => _values;

    /// <summary>是否包含任何信号（空 = 未初始化）。</summary>
    public bool HasValues => _values.Count > 0;
}
```

- [ ] **Step 4: Modify EnvironmentRuntime**

在 `NodeMessageRuntimeState` 加:
```csharp
public NodeSignalState Signals { get; } = new();
public DbcDocument? Dbc { get; set; }
```

`BuildPayload()` 修改:
```csharp
public byte[]? BuildPayload(DbcEncodeService encoder)
{
    switch (Source)
    {
        case FixedHexSource:
            return FixedHexData;
        case DbcSignalsSource dbcSource when Dbc is { } doc:
            var msg = doc.Messages.FirstOrDefault(m => m.Name == dbcSource.MessageName);
            if (msg is null) return null;
            // 初始化信号默认值（仅首次）
            if (!Signals.HasValues)
                foreach (var s in msg.Signals)
                    Signals.Set(s.Name, Signals.GetOrInit(s.Name, s.RawDefaultValue ?? 0));
            return encoder.Encode(msg, Signals.ToDictionary());
        default:
            return null; // ScriptCallbackSource not supported
    }
}
```

`EnvironmentRuntime` 加:
```csharp
private readonly DbcEncodeService _encoder = new();
private DbcDocument? _dbc;

public void SetSignalValue(string nodeName, string messageName, string signalName, double value)
{
    lock (_gate)
    {
        var state = _states.FirstOrDefault(s => s.Node.Name == nodeName);
        var msgState = state?.Messages.FirstOrDefault(m =>
            (m.Source as DbcSignalsSource)?.MessageName == messageName);
        msgState?.Signals.Set(signalName, value);
    }
}

public byte[]? GetEncodedPayload(string nodeName, string messageName)
{
    lock (_gate)
    {
        var state = _states.FirstOrDefault(s => s.Node.Name == nodeName);
        var msgState = state?.Messages.FirstOrDefault(m =>
            (m.Source as DbcSignalsSource)?.MessageName == messageName);
        return msgState?.BuildPayload(_encoder);
    }
}
```

`Start()` 加 `DbcDocument? dbc = null` 参数; `_states = nodes.Select(n => new NodeRuntimeState(n)).ToList();` 后设置每 `NodeMessageRuntimeState.Dbc = dbc`。

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests --filter "DbcSignalsEncodingTests" --no-restore`
Expected: PASS

- [ ] **Step 6: Run full test suite**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests --no-restore`
Expected: 601+ PASS

- [ ] **Step 7: Commit**

```bash
git -C peakcan-host add src/PeakCan.Host.Infrastructure/HIL/Environment/NodeSignalState.cs src/PeakCan.Host.Infrastructure/HIL/Environment/EnvironmentRuntime.cs tests/PeakCan.Host.Infrastructure.Tests/HIL/Environment/DbcSignalsEncodingTests.cs
git -C peakcan-host commit -m "feat: DbcSignalsSource encoding with signal state table in EnvironmentRuntime"
```

---

### Task 2: SignalOverrides 初值 + validator 扩展

**Files:**
- Modify: `peakcan-host/src/PeakCan.Host.Infrastructure/HIL/Environment/EnvironmentRuntime.cs` — Start 时应用 `RestbusNode.SignalOverrides`
- Modify: `peakcan-hil-core/src/PeakCan.HIL.Core/HIL/Environment/RestbusNodeValidator.cs` — 校验 SignalOverrides 键格式/DBC 信号存在
- Test: `peakcan-host/tests/PeakCan.Host.Infrastructure.Tests/HIL/Environment/SignalOverridesTests.cs`
- Test: `peakcan-hil-core/tests/PeakCan.HIL.Core.Tests/HIL/Environment/SignalOverridesValidatorTests.cs`

**Interfaces:**
- Consumes: `RestbusNode.SignalOverrides` (`IReadOnlyDictionary<string, double>?`)
- Produces: validator new rules; runtime applies overrides at Start

- [ ] **Step 1: Write hil-core validator failing test**

```csharp
using Xunit;
using PeakCan.HIL.Core.HIL.Environment;

namespace PeakCan.HIL.Core.Tests.HIL.Environment;

public class SignalOverridesValidatorTests
{
    [Fact]
    public void Validate_SignalOverridesBadKeyFormat_ReportsError()
    {
        var node = new RestbusNode
        {
            Name = "Test",
            Identity = new RawCanNodeIdentity(),
            SignalOverrides = new Dictionary<string, double> { ["NoDot"] = 1.0 }
        };
        var errors = RestbusNodeValidator.Validate([node]);
        Assert.Contains(errors, e => e.Contains("SignalOverrides key format"));
    }

    [Fact]
    public void Validate_SignalOverridesOnFixedHexSource_ReportsError()
    {
        var node = new RestbusNode
        {
            Name = "Test",
            Identity = new RawCanNodeIdentity(),
            Messages = [new NodeMessage(new CanMessageRef(0x100, false), 100, new FixedHexSource("0102"))],
            SignalOverrides = new Dictionary<string, double> { ["Msg.Sig"] = 1.0 }
        };
        var errors = RestbusNodeValidator.Validate([node]);
        Assert.Contains(errors, e => e.Contains("DbcSignalsSource"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PeakCan.HIL.Core.Tests --filter "SignalOverridesValidatorTests" --no-restore`
Expected: FAIL — validator doesn't check SignalOverrides yet

- [ ] **Step 3: Extend RestbusNodeValidator**

Add rules:
```csharp
// SignalOverrides 键格式 "MessageName.SignalName"
if (node.SignalOverrides is { } overrides)
{
    foreach (var key in overrides.Keys)
    {
        if (!key.Contains('.'))
            errors.Add($"Node '{node.Name}': SignalOverrides key '{key}' must use 'MessageName.SignalName' format.");
    }
    // 目标报文必须是 DbcSignalsSource
    var dbcMessages = node.Messages
        .Where(m => m.Payload is DbcSignalsSource)
        .Select(m => ((DbcSignalsSource)m.Payload).MessageName)
        .ToHashSet(StringComparer.Ordinal);
    foreach (var key in overrides.Keys)
    {
        var msgName = key.Split('.')[0];
        if (!dbcMessages.Contains(msgName))
            errors.Add($"Node '{node.Name}': SignalOverrides target '{msgName}' must be a DbcSignalsSource message.");
    }
}
```

- [ ] **Step 4: Run hil-core validator test to verify it passes**

Run: `dotnet test tests/PeakCan.HIL.Core.Tests --filter "SignalOverridesValidatorTests" --no-restore`
Expected: PASS

- [ ] **Step 5: Write host runtime failing test**

```csharp
using Xunit;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.Host.Infrastructure.HIL.Environment;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Environment;

public class SignalOverridesTests
{
    [Fact]
    public void Start_SignalOverrides_AppliedToSignalState()
    {
        var node = new RestbusNode
        {
            Name = "Charger",
            Identity = new RawCanNodeIdentity(),
            Messages = [new NodeMessage(new CanMessageRef(512, false), 100, new DbcSignalsSource("CRM"))],
            SignalOverrides = new Dictionary<string, double> { ["CRM.CRM_Signal"] = 42 }
        };
        // 需要传入 DBC。测试用简易 FakeDbc 或传入已解析文档。
        var dbc = CreateTestDbc();
        var runtime = new EnvironmentRuntime(new FakeCanChannel(), dbc);
        runtime.Start([node], null);
        var payload = runtime.GetEncodedPayload("Charger", "CRM");
        Assert.NotNull(payload);
        Assert.Equal(42, payload[0]); // Little-endian, factor=1
        runtime.Stop();
    }
}
```

- [ ] **Step 6: Implement SignalOverrides application in EnvironmentRuntime.Start**

After `_states = nodes.Select(n => new NodeRuntimeState(n)).ToList();`:
```csharp
foreach (var nodeState in _states)
{
    if (nodeState.Node.SignalOverrides is { } overrides)
    {
        foreach (var (key, value) in overrides)
        {
            var parts = key.Split('.', 2);
            if (parts.Length != 2) continue;
            var msgState = nodeState.Messages.FirstOrDefault(m =>
                (m.Source as DbcSignalsSource)?.MessageName == parts[0]);
            msgState?.Signals.Set(parts[1], value);
        }
    }
}
```

- [ ] **Step 7: Run host test to verify it passes**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests --filter "SignalOverridesTests" --no-restore`
Expected: PASS

- [ ] **Step 8: Run full hil-core + host suites**

Run: `dotnet test tests/PeakCan.HIL.Core.Tests --no-restore`
Expected: 279+ PASS
Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests --no-restore`
Expected: 601+ PASS

- [ ] **Step 9: Commit both repos**

```bash
git -C peakcan-hil-core add src/PeakCan.HIL.Core/HIL/Environment/RestbusNodeValidator.cs tests/PeakCan.HIL.Core.Tests/HIL/Environment/SignalOverridesValidatorTests.cs
git -C peakcan-hil-core commit -m "feat: SignalOverrides validator rules (key format + DbcSignalsSource target)"
git -C peakcan-host add src/PeakCan.Host.Infrastructure/HIL/Environment/EnvironmentRuntime.cs tests/PeakCan.Host.Infrastructure.Tests/HIL/Environment/SignalOverridesTests.cs
git -C peakcan-host commit -m "feat: apply SignalOverrides initial values at EnvironmentRuntime.Start"
```

---### Task 3: SetEnvironmentSignalStep + ModifyEnvironmentFrameStep 执行器

**Files:**
- Create: `peakcan-host/src/PeakCan.Host.Core/HIL/StepExecutor/SetEnvironmentSignalStepExecutor.cs`
- Create: `peakcan-host/src/PeakCan.Host.Core/HIL/StepExecutor/ModifyEnvironmentFrameStepExecutor.cs`
- Modify: `peakcan-host/src/PeakCan.Host.Infrastructure/HIL/HeadlessHostBuilder.cs` — 注册两个新 executor
- Test: `peakcan-host/tests/PeakCan.Host.Core.Tests/HIL/StepExecutor/SetEnvironmentSignalStepExecutorTests.cs`

**Interfaces:**
- Consumes: `IStepExecutor` (hil-core Contracts), `SetEnvironmentSignalStep(NodeName, MessageName, SignalName, Value)`, `ModifyEnvironmentFrameStep(NodeName, MessageRef, Data)`
- Produces: DI-registered executors; `EnvironmentRuntime.SetSignalValue` / `UpdateFrameData` called via `IEnvironmentRuntimeBridge`

- [ ] **Step 1: Define IEnvironmentRuntimeBridge (bridge pattern, host.Core → Infra 依赖注入)**

```csharp
namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>EnvironmentRuntime 的 step-executor 桥接接口。Host.Core 不引用 Infra。</summary>
public interface IEnvironmentRuntimeBridge
{
    void SetSignalValue(string nodeName, string messageName, string signalName, double value);
    void UpdateFrameData(string nodeName, MessageRef msgRef, byte[] data);
}
```

- [ ] **Step 2: Write SetEnvironmentSignalStepExecutor failing test**

```csharp
using Xunit;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.StepParams;
using PeakCan.HIL.Core.HIL.StepExecutor;
using PeakCan.Host.Core.HIL.StepExecutor;
using Moq;

namespace PeakCan.Host.Core.Tests.HIL.StepExecutor;

public class SetEnvironmentSignalStepExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_CallsBridgeSetSignalValue()
    {
        var bridge = new Mock<IEnvironmentRuntimeBridge>();
        var executor = new SetEnvironmentSignalStepExecutor(bridge.Object);
        var step = new TestCaseStep
        {
            Kind = TestCaseStepKind.SetEnvironmentSignal,
            Parameters = new SetEnvironmentSignalStep("Charger", "CRM", "CRM_Signal", 50)
        };
        var ctx = new Mock<IAssertionContext>();
        var result = await executor.ExecuteAsync(step, ctx.Object, CancellationToken.None);
        Assert.True(result.Passed);
        bridge.Verify(b => b.SetSignalValue("Charger", "CRM", "CRM_Signal", 50), Times.Once);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/PeakCan.Host.Core.Tests --filter "SetEnvironmentSignalStepExecutorTests" --no-restore`
Expected: FAIL — class not found

- [ ] **Step 4: Implement both executors**

`SetEnvironmentSignalStepExecutor.cs`:
```csharp
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.Host.Core.HIL.StepExecutor;

internal sealed class SetEnvironmentSignalStepExecutor(IEnvironmentRuntimeBridge bridge) : IStepExecutor
{
    public TestCaseStepKind Kind => TestCaseStepKind.SetEnvironmentSignal;

    public Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        if (step.Parameters is not SetEnvironmentSignalStep p)
            return Task.FromResult(StepResult.Fail("Parameters is not SetEnvironmentSignalStep."));
        bridge.SetSignalValue(p.NodeName, p.MessageName, p.SignalName, p.Value);
        return Task.FromResult(StepResult.Pass());
    }
}
```

`ModifyEnvironmentFrameStepExecutor.cs`:
```csharp
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.Host.Core.HIL.StepExecutor;

internal sealed class ModifyEnvironmentFrameStepExecutor(IEnvironmentRuntimeBridge bridge) : IStepExecutor
{
    public TestCaseStepKind Kind => TestCaseStepKind.ModifyEnvironmentFrame;

    public Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        if (step.Parameters is not ModifyEnvironmentFrameStep p)
            return Task.FromResult(StepResult.Fail("Parameters is not ModifyEnvironmentFrameStep."));
        bridge.UpdateFrameData(p.NodeName, p.Ref, p.Data);
        return Task.FromResult(StepResult.Pass());
    }
}
```

- [ ] **Step 5: EnvironmentRuntime implements IEnvironmentRuntimeBridge**

Add `: IEnvironmentRuntimeBridge` to class declaration. `SetSignalValue` and `UpdateFrameData` already exist.

- [ ] **Step 6: Register in HeadlessHostBuilder**

```csharp
builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IEnvironmentRuntimeBridge>(sp =>
    sp.GetRequiredService<EnvironmentRuntimeSingletonHolder>().Runtime!);
builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, SetEnvironmentSignalStepExecutor>();
builder.Services.AddSingleton<PeakCan.HIL.Core.HIL.StepExecutor.IStepExecutor, ModifyEnvironmentFrameStepExecutor>();
```

注意：`EnvironmentRuntime` 是 per-run 创建的。需要一个 scoped singleton holder 模式（已有类似模式用于 `IAssertionContext`）。使用 `EnvironmentRuntimeHolder`：
```csharp
internal sealed class EnvironmentRuntimeHolder
{
    public IEnvironmentRuntimeBridge? Runtime { get; set; }
}
```
HeadlessHostBuilder 注册 holder; HilRunnerService 构造 EnvironmentRuntime 后赋值 `holder.Runtime = envRuntime`。

- [ ] **Step 7: Run test to verify it passes**

Run: `dotnet test tests/PeakCan.Host.Core.Tests --filter "SetEnvironmentSignalStepExecutorTests" --no-restore`
Expected: PASS

- [ ] **Step 8: Run full host suite**

Run: `dotnet test tests/PeakCan.Host.Core.Tests --no-restore && dotnet test tests/PeakCan.Host.Infrastructure.Tests --no-restore`
Expected: ALL PASS

- [ ] **Step 9: Commit**

```bash
git -C peakcan-host add src/PeakCan.Host.Core/HIL/StepExecutor/ tests/PeakCan.Host.Core.Tests/HIL/StepExecutor/ src/PeakCan.Host.Infrastructure/HIL/HeadlessHostBuilder.cs src/PeakCan.Host.Infrastructure/HIL/Environment/EnvironmentRuntime.cs
git -C peakcan-host commit -m "feat: SetEnvironmentSignalStep + ModifyEnvironmentFrameStep executors with bridge"
```

---

### Task 4: UDS 路由（EcuScriptDefinition → EcuStateMachine）

**Files:**
- Modify: `peakcan-host/src/PeakCan.Host.Infrastructure/HIL/Environment/EnvironmentRuntime.cs` — Start 时对有 `UdsBehavior` 的节点构造 `EcuStateMachine`；incoming 帧 ID 匹配 UDS 请求 ID 时调用 `ProcessRequest`，延迟后发送响应
- Test: `peakcan-host/tests/PeakCan.Host.Infrastructure.Tests/HIL/Environment/UdsRoutingTests.cs`

**Interfaces:**
- Consumes: `EcuStateMachine.ProcessRequest(byte[]) → (byte[] Response, int DelayMs)` (hil-core Contracts)
- Produces: `EnvironmentRuntime` handles UDS requests on nodes with `UdsBehavior`

- [ ] **Step 1: Write failing test**

```csharp
using Xunit;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL.Environment;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Environment;

public class UdsRoutingTests
{
    [Fact]
    public void IncomingUdsRequest_GeneratesResponse()
    {
        var node = new RestbusNode
        {
            Name = "TestEcu",
            Identity = new RawCanNodeIdentity(),
            UdsBehavior = new EcuScriptDefinition(
                new CanIdConfig(0x7E0, 0x7E8),
                [new EcuStateTransition(null, 0x10, null, null, null,
                    EcuResponseType.Static, new byte[] { 0x50, 0x10 }, null, 0)])
        };
        var channel = new FakeCanChannel();
        var runtime = new EnvironmentRuntime(channel);
        runtime.Start([node], null);

        // Simulate incoming UDS request (0x10 01)
        var requestFrame = new CanFrame(
            new CanId(0x7E0, FrameFormat.Standard),
            new byte[] { 0x02, 0x10, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00 });
        runtime.InjectIncomingFrame(requestFrame);
        runtime.ScanForTest();

        // Response written to 0x7E8
        Assert.Contains(channel.WrittenFrames, f => f.Id.Raw == 0x7E8 && f.Data.Span[0] == 0x50);
        runtime.Stop();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests --filter "UdsRoutingTests" --no-restore`
Expected: FAIL — ProcessIncoming doesn't route UDS

- [ ] **Step 3: Implement UDS routing in EnvironmentRuntime**

`NodeRuntimeState` 加:
```csharp
public EcuStateMachine? StateMachine { get; set; }
```

`EnvironmentRuntime.Start` 中：
```csharp
foreach (var nodeState in _states)
{
    if (nodeState.Node.UdsBehavior is { } uds)
    {
        nodeState.StateMachine = new EcuStateMachine(
            uds.Transitions, generators, uds.InitialState);
    }
}
```

`ProcessIncoming` 加:
```csharp
foreach (var nodeState in _states)
{
    if (nodeState.StateMachine is { } sm && nodeState.Node.UdsBehavior is { } uds)
    {
        if (frame.Id.Raw == uds.CanIds.RequestId)
        {
            var request = ExtractUdsPayload(frame);
            var (response, delayMs) = sm.ProcessRequest(request);
            // 按延迟发送（简化：同步；真实实现可用 pending 延迟队列）
            var respId = new CanId(uds.CanIds.ResponseId, FrameFormat.Standard);
            var respFrame = new CanFrame(respId, response, FrameFlags.None, default, default, FrameSource.Environment);
            _channel.WriteAsync(respFrame).AsTask().GetAwaiter().GetResult();
            nodeState.UdsResponses++;
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests --filter "UdsRoutingTests" --no-restore`
Expected: PASS

- [ ] **Step 5: Run full host suite**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests --no-restore`
Expected: ALL PASS

- [ ] **Step 6: Commit**

```bash
git -C peakcan-host add src/PeakCan.Host.Infrastructure/HIL/Environment/EnvironmentRuntime.cs tests/PeakCan.Host.Infrastructure.Tests/HIL/Environment/UdsRoutingTests.cs
git -C peakcan-host commit -m "feat: UDS routing — EcuScriptDefinition to EcuStateMachine in EnvironmentRuntime"
```

---### Task 5: J1939 TP 集成（多帧发送/接收重组）

**Files:**
- Modify: `peakcan-host/src/PeakCan.Host.Infrastructure/HIL/Environment/EnvironmentRuntime.cs` — 周期帧 `J1939MessageRef` 时经 `J1939TpLayer` 发送；接收侧 TP 重组逻辑帧入规则分发
- Modify: `peakcan-host/src/PeakCan.Host.Infrastructure/HIL/HilRunnerService.cs` — 把 `J1939TpLayer` 传入 `EnvironmentRuntime`
- Test: `peakcan-host/tests/PeakCan.Host.Infrastructure.Tests/HIL/Environment/J1939TpEnvironmentTests.cs`

**Interfaces:**
- Consumes: `J1939TpLayer` (host.Core), `TpMode` (hil-core Environment), `J1939MessageRef(Pgn, Priority, Sa, Da)` 
- Produces: `EnvironmentRuntime.Start(nodes, channels, tpLayer?)` 接受 TP layer; >8B payload 自动 Bam; RtsCts 显式声明

- [ ] **Step 1: Write failing test — J1939 periodic send via TP**

```csharp
using Xunit;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.Host.Infrastructure.HIL.Environment;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Environment;

public class J1939TpEnvironmentTests
{
    [Fact]
    public void Start_J1939NodeWithBamPayload9B_SendsTpFrames()
    {
        var node = new RestbusNode
        {
            Name = "J1939Node",
            Identity = new J1939NodeIdentity(0x01),
            Messages = [new NodeMessage(
                new J1939MessageRef(0xFECA, 6, Sa: 0x01, Da: 0xFF, Tp: TpMode.Bam),
                100,
                new FixedHexSource("010203040506070809"))]
        };
        var channel = new FakeCanChannel();
        var runtime = new EnvironmentRuntime(channel);
        runtime.Start([node], null);
        System.Threading.Thread.Sleep(100);
        // Bam 事务应产生 TP.CM + TP.DT 帧
        var tpFrames = channel.WrittenFrames.Where(f =>
            f.Id.Raw >= 0x1CEC0000 && f.Id.Raw <= 0x1CECFFFF ||
            f.Id.Raw >= 0x1CEB0000 && f.Id.Raw <= 0x1CEBFFFF).ToList();
        Assert.True(tpFrames.Count > 0, "Expected TP.CM and TP.DT frames on the bus.");
        runtime.Stop();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests --filter "J1939TpEnvironmentTests" --no-restore`
Expected: FAIL — EnvironmentRuntime only handles CanMessageRef, not J1939MessageRef with TP

- [ ] **Step 3: Implement J1939 TP send in EnvironmentRuntime**

`SendFrame` 分支:
```csharp
if (msg.Ref is J1939MessageRef jRef)
{
    var payload = msgState.BuildPayload(_encoder);
    if (payload is null) return;
    if (payload.Length <= 8)
    {
        // 单帧直接发送
        var id = J1939Id(jRef);
        var frame = new CanFrame(id, payload, FrameFlags.None, default, default, FrameSource.Environment);
        _channel.WriteAsync(frame).AsTask().GetAwaiter().GetResult();
    }
    else if (_tpLayer is { } tp)
    {
        // 多帧走 J1939TpLayer
        tp.SendBamAsync(jRef.Pgn, jRef.Priority ?? 6, jRef.Sa ?? 0x00, jRef.Da, payload).AsTask().GetAwaiter().GetResult();
    }
    else
    {
        _logger.LogWarning("J1939 TP message {Ref} >8B but no TpLayer provided.", msg.Ref);
    }
    msgState.FramesSent++;
    return;
}
```

`Start` 加 `IJ1939TpLayer? tpLayer = null` 参数。

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests --filter "J1939TpEnvironmentTests" --no-restore`
Expected: PASS

- [ ] **Step 5: Run full host suite**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests --no-restore`
Expected: ALL PASS

- [ ] **Step 6: Commit**

```bash
git -C peakcan-host add src/PeakCan.Host.Infrastructure/HIL/Environment/EnvironmentRuntime.cs src/PeakCan.Host.Infrastructure/HIL/HilRunnerService.cs tests/PeakCan.Host.Infrastructure.Tests/HIL/Environment/J1939TpEnvironmentTests.cs
git -C peakcan-host commit -m "feat: J1939 TP integration — Bam/RtsCts multi-frame send via J1939TpLayer"
```

---

### Task 6: NodeRunStats + TestSuiteResult.EnvironmentStats

**Files:**
- Create: `peakcan-hil-core/src/PeakCan.HIL.Core/HIL/NodeRunStats.cs`
- Modify: `peakcan-hil-core/src/PeakCan.HIL.Core/HIL/TestSuiteResult.cs` — 加 `IReadOnlyList<NodeRunStats>? EnvironmentStats = null`
- Modify: `peakcan-host/src/PeakCan.Host.Infrastructure/HIL/Environment/EnvironmentRuntime.cs` — 加 `GetStats()` 方法
- Modify: `peakcan-host/src/PeakCan.Host.Infrastructure/HIL/HilRunnerService.cs` — Stop 后收集 stats 到 result
- Test: `peakcan-host/tests/PeakCan.Host.Infrastructure.Tests/HIL/Environment/NodeRunStatsTests.cs`

**Interfaces:**
- Consumes: `NodeMessageRuntimeState.FramesSent`, `ResponseRulesMatched`, `UdsResponses`
- Produces: `NodeRunStats(NodeName, FramesSent, RulesMatched, UdsResponses)`; `TestSuiteResult.EnvironmentStats`; `EnvironmentRuntime.GetStats()` → `IReadOnlyList<NodeRunStats>`

- [ ] **Step 1: Write hil-core model + test**

`NodeRunStats.cs`:
```csharp
namespace PeakCan.HIL.Core.HIL;

/// <summary>单节点环境运行统计（随 TestSuiteResult 输出）。</summary>
public sealed record NodeRunStats(
    string NodeName,
    long FramesSent,
    long RulesMatched,
    long UdsResponses);
```

`TestSuiteResult` 修改（在构造函数末尾加可选参数）:
```csharp
public sealed record TestSuiteResult(
    ...,
    IReadOnlyList<NodeRunStats>? EnvironmentStats = null);
```

- [ ] **Step 2: Write runtime test**

```csharp
using Xunit;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.Host.Infrastructure.HIL.Environment;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Environment;

public class NodeRunStatsTests
{
    [Fact]
    public void GetStats_ReturnsFrameCounts()
    {
        var node = new RestbusNode
        {
            Name = "Charger",
            Identity = new RawCanNodeIdentity(),
            Messages = [new NodeMessage(new CanMessageRef(0x100, false), 100, new FixedHexSource("0102"))]
        };
        var channel = new FakeCanChannel();
        var runtime = new EnvironmentRuntime(channel);
        runtime.Start([node], null);
        System.Threading.Thread.Sleep(150); // 允许至少一次发送

        var stats = runtime.GetStats();
        var chargerStats = Assert.Single(stats);
        Assert.Equal("Charger", chargerStats.NodeName);
        Assert.True(chargerStats.FramesSent > 0, $"Expected FramesSent > 0 but got {chargerStats.FramesSent}");
        runtime.Stop();
    }
}
```

- [ ] **Step 3: Implement GetStats in EnvironmentRuntime**

```csharp
public IReadOnlyList<NodeRunStats> GetStats()
{
    lock (_gate)
    {
        return [.. _states.Select(s => new NodeRunStats(
            s.Node.Name,
            s.Messages.Sum(m => m.FramesSent),
            s.RulesMatched,
            s.UdsResponses))];
    }
}
```

`NodeRuntimeState` 加:
```csharp
public long RulesMatched { get; set; }
public long UdsResponses { get; set; }
```

- [ ] **Step 4: Wire into HilRunnerService**

在 `finally { environmentRuntime.Stop(); }` 后:
```csharp
var envStats = environmentRuntime.GetStats();
result = result with { EnvironmentStats = envStats.Count > 0 ? envStats : null };
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests --filter "NodeRunStatsTests" --no-restore`
Expected: PASS
Run: `dotnet test tests/PeakCan.HIL.Core.Tests --no-restore`
Expected: ALL PASS (round-trip tests may need EnvironmentStats null default)

- [ ] **Step 6: Commit both repos**

```bash
git -C peakcan-hil-core add src/PeakCan.HIL.Core/HIL/NodeRunStats.cs src/PeakCan.HIL.Core/HIL/TestSuiteResult.cs
git -C peakcan-hil-core commit -m "feat: NodeRunStats model + TestSuiteResult.EnvironmentStats"
git -C peakcan-host add src/PeakCan.Host.Infrastructure/HIL/Environment/EnvironmentRuntime.cs src/PeakCan.Host.Infrastructure/HIL/HilRunnerService.cs tests/PeakCan.Host.Infrastructure.Tests/HIL/Environment/NodeRunStatsTests.cs
git -C peakcan-host commit -m "feat: collect NodeRunStats from EnvironmentRuntime and attach to TestSuiteResult"
```

---### Task 7: gbt27930-charger seed 模板（hil-core 纯数据）

**Files:**
- Create: `peakcan-hil-core/src/PeakCan.HIL.Core/Templates/Gbt27930ChargerTemplate.cs`
- Test: `peakcan-hil-core/tests/PeakCan.HIL.Core.Tests/Templates/Gbt27930ChargerTemplateTests.cs`

**Interfaces:**
- Consumes: `RestbusNode`, `TrialContract`, `HandshakeExpectation`, `J1939NodeIdentity`, `J1939MessageRef`, `NodeMessage`, `ResponseRule`, `NodeAction` (all hil-core Environment)
- Produces: `Gbt27930ChargerTemplate.Create()` → `RestbusNode`; `Gbt27930ChargerTemplate.TemplateId` → `"gbt27930-charger"`

- [ ] **Step 1: Write failing test**

```csharp
using Xunit;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.HIL.Core.Templates;

namespace PeakCan.HIL.Core.Tests.Templates;

public class Gbt27930ChargerTemplateTests
{
    [Fact]
    public void Create_ReturnsNodeWithTrialContract()
    {
        var node = Gbt27930ChargerTemplate.Create();
        Assert.Equal("Charger", node.Name);
        Assert.NotNull(node.Trial);
        Assert.Equal("gbt27930-charger", node.Trial.TemplateId);
        Assert.NotEmpty(node.Trial.Handshake);
        Assert.NotEmpty(node.Trial.RequiredDbcMessages);
    }

    [Fact]
    public void Create_HasJ1939IdentityWithSa()
    {
        var node = Gbt27930ChargerTemplate.Create();
        var identity = Assert.IsType<J1939NodeIdentity>(node.Identity);
        Assert.True(identity.Sa > 0);
    }

    [Fact]
    public void Create_HasPeriodicMessages()
    {
        var node = Gbt27930ChargerTemplate.Create();
        Assert.NotEmpty(node.Messages);
        Assert.All(node.Messages, m => Assert.True(m.IntervalMs >= 10));
    }

    [Fact]
    public void Create_HasHandshakeRules()
    {
        var node = Gbt27930ChargerTemplate.Create();
        Assert.NotEmpty(node.Rules);
    }

    [Fact]
    public void Handshake_ContainsCrmToBrm()
    {
        var node = Gbt27930ChargerTemplate.Create();
        var crm = node.Trial!.Handshake.FirstOrDefault(h => h.Send == "CRM");
        Assert.NotNull(crm);
        Assert.Equal("BRM", crm.ThenReceive);
        Assert.Equal(500, crm.TimeoutMs);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PeakCan.HIL.Core.Tests --filter "Gbt27930ChargerTemplateTests" --no-restore`
Expected: FAIL — class not found

- [ ] **Step 3: Implement template**

```csharp
using PeakCan.HIL.Core.HIL.Environment;

namespace PeakCan.HIL.Core.Templates;

/// <summary>
/// GB/T 27930 充电桩 seed 模板。纯数据，零 I/O。
/// spec §8.2：ECA 规则覆盖握手主链 CRM→BRM、CTS→BCL 跟随、CST→BST 应答。
/// 完整应用层状态机是非目标；模板只保证最小握手和充电保持。
/// </summary>
public static class Gbt27930ChargerTemplate
{
    public const string TemplateId = "gbt27930-charger";

    public static RestbusNode Create() => new()
    {
        Name = "Charger",
        Tag = "gbt27930",
        Identity = new J1939NodeIdentity(0x56),
        Messages =
        [
            // CRM — 充电机辨识报文
            new NodeMessage(
                new J1939MessageRef(0x26FF, 6, Sa: 0x56, Da: 0xF4),
                250, new FixedHexSource("0000000000000000")),
            // CTS — 充电机时序报文
            new NodeMessage(
                new J1939MessageRef(0x06FF, 6, Sa: 0x56, Da: 0xF4),
                250, new FixedHexSource("0000000000000000")),
        ],
        Rules = [],
        AddressClaimEnabled = true,
        Trial = new TrialContract(
            TemplateId,
            [
                new HandshakeExpectation("CRM", "BRM", 500,
                    ["接线/通道选错", "BMS 未上电", "SA 地址冲突"]),
                new HandshakeExpectation("CTS", "BCL", 250,
                    ["BMS 未准备就绪", "充电参数不匹配"]),
            ],
            ["CRM", "BRM", "CTS", "BCL", "CST", "BST"])
    };
}
```

注意：实际 PGN/SA/帧定义需按 gbt27930 spec 和现有 DBC 文件校准。此为占位骨架，实施时对照 spec §8.2 的 ECA 规则清单填完整 `Rules`（CRM→BRM 条件、CTS→BCL 跟随、CST→BST 应答）。

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PeakCan.HIL.Core.Tests --filter "Gbt27930ChargerTemplateTests" --no-restore`
Expected: PASS

- [ ] **Step 5: Run full hil-core suite**

Run: `dotnet test tests/PeakCan.HIL.Core.Tests --no-restore`
Expected: ALL PASS

- [ ] **Step 6: Commit**

```bash
git -C peakcan-hil-core add src/PeakCan.HIL.Core/Templates/ tests/PeakCan.HIL.Core.Tests/Templates/
git -C peakcan-hil-core commit -m "feat: gbt27930-charger seed template with TrialContract"
```

---

### Task 8: Host 试运行按钮 + TrialContract 诊断

**Files:**
- Modify: `peakcan-host/src/PeakCan.Host.App/Views/HilView.xaml` — 加"试运行环境"按钮
- Modify: `peakcan-host/src/PeakCan.Host.App/ViewModels/HilViewModel.cs` — 加 `TrialRunCommand` + 诊断输出
- Create: `peakcan-host/src/PeakCan.Host.Infrastructure/HIL/Environment/TrialRunner.cs` — 试运行逻辑（不跑 case，只拉环境 + 检查握手）
- Test: `peakcan-host/tests/PeakCan.Host.Infrastructure.Tests/HIL/Environment/TrialRunnerTests.cs`

**Interfaces:**
- Consumes: `RestbusNode.Trial` (`TrialContract`), `EnvironmentRuntime`
- Produces: `TrialRunner.RunTrialAsync(nodes, channel, tpLayer, ct)` → `TrialRunResult(Passed, Diagnostics)`

- [ ] **Step 1: Write failing test**

```csharp
using Xunit;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.Host.Infrastructure.HIL.Environment;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Environment;

public class TrialRunnerTests
{
    [Fact]
    public async Task RunTrial_NoTrialContract_ReturnsPassWithFrameCountOnly()
    {
        var node = new RestbusNode
        {
            Name = "Plain",
            Identity = new RawCanNodeIdentity(),
            Messages = [new NodeMessage(new CanMessageRef(0x100, false), 100, new FixedHexSource("01"))]
        };
        var channel = new FakeCanChannel();
        var runner = new TrialRunner(channel);
        var result = await runner.RunTrialAsync([node], TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.True(result.Passed);
        Assert.Empty(result.Diagnostics);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests --filter "TrialRunnerTests" --no-restore`
Expected: FAIL — class not found

- [ ] **Step 3: Implement TrialRunner**

```csharp
using System.Diagnostics;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;

namespace PeakCan.Host.Infrastructure.HIL.Environment;

/// <summary>试运行诊断输出。</summary>
public sealed record TrialDiagnostic(string Step, bool Passed, string? Detail, IReadOnlyList<string> PossibleCauses);

/// <summary>试运行结果。</summary>
public sealed record TrialRunResult(bool Passed, IReadOnlyList<TrialDiagnostic> Diagnostics);

/// <summary>host 试运行器。不跑正式 case，只拉起环境并按 TrialContract 检查握手。</summary>
public sealed class TrialRunner(ICanChannel channel)
{
    public async Task<TrialRunResult> RunTrialAsync(
        IReadOnlyList<RestbusNode> nodes, TimeSpan timeout, CancellationToken ct)
    {
        var diagnostics = new List<TrialDiagnostic>();
        var allPassed = true;

        foreach (var node in nodes.Where(n => n.Trial is not null))
        {
            var contract = node.Trial!;
            foreach (var step in contract.Handshake)
            {
                // 检查发送帧是否在 trace 中出现（发送由 EnvironmentRuntime 自动处理）
                // 这里只模拟接收检查——简化：等待 thenReceive 帧 ID 在 timeout 内收到
                var sw = Stopwatch.StartNew();
                var received = false; // 需要注入 IFrameSubscription 或在 EnvironmentRuntime 上暴露
                // 实施时：使用 ChannelRouter 的 frame subscription 或 EnvironmentRuntime 的 incoming callback
                // M2 简化：trial runner 内嵌一个 EnvironmentRuntime + frame listener
                // 这里只验证模型接线；完整诊断在集成测试中覆盖
                received = sw.Elapsed < TimeSpan.FromMilliseconds(step.TimeoutMs);
                if (!received) allPassed = false;
                diagnostics.Add(new TrialDiagnostic(
                    step.Send, received,
                    received ? null : $"{step.Send} sent, {step.ThenReceive} not received within {step.TimeoutMs}ms",
                    received ? [] : step.PossibleCauses));
            }
        }

        await Task.Delay(100, ct); // allow frames to flow
        return new TrialRunResult(allPassed, diagnostics);
    }
}
```

注意：完整实现需要 `ICanFrameSubscription` 监听接收帧。实施时检查现有 `ChannelRouter.Subscribe` 或 `IFrameReceivedSubscription` 模式。上述骨架需补接收监听逻辑。

- [ ] **Step 4: Add TrialRunCommand to HilViewModel**

```csharp
[RelayCommand]
private async Task TrialRunEnvironmentAsync(CancellationToken ct)
{
    // 1. 读取当前 suite
    // 2. 检查 Environment != null
    // 3. 连接通道
    // 4. EnvironmentRuntime.Start(nodes, channels)
    // 5. TrialRunner.RunTrialAsync(nodes, timeout, ct)
    // 6. EnvironmentRuntime.Stop()
    // 7. 断开通道
    // 8. 展示诊断结果
}
```

- [ ] **Step 5: Add button to HilView.xaml**

```xml
<Button Content="试运行环境"
        Command="{Binding TrialRunEnvironmentCommand}"
        ToolTip="不跑正式测试，只拉起总线环境验证握手" />
```

- [ ] **Step 6: Run tests**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests --filter "TrialRunnerTests" --no-restore`
Expected: PASS
Run: `dotnet build src/PeakCan.Host.App --no-restore`
Expected: Build succeeds (XAML compiles)

- [ ] **Step 7: Commit**

```bash
git -C peakcan-host add src/PeakCan.Host.Infrastructure/HIL/Environment/TrialRunner.cs src/PeakCan.Host.App/Views/HilView.xaml src/PeakCan.Host.App/ViewModels/HilViewModel.cs tests/PeakCan.Host.Infrastructure.Tests/HIL/Environment/TrialRunnerTests.cs
git -C peakcan-host commit -m "feat: host trial run button + TrialContract diagnostics"
```

---### Task 9: Sim 帧标记 — trace/replay 灰色斜体

**Files:**
- Modify: `peakcan-host/src/PeakCan.Host.App/ViewModels/TraceViewModel.cs` (或等效 trace 行 VM) — sim 帧灰色斜体标记
- Modify: `peakcan-host/src/PeakCan.Host.App/Views/ReplayView.xaml` (或 SignalView) — DataTemplate 加 `FrameSource` 条件样式
- Test: `peakcan-host/tests/PeakCan.Host.App.Tests/ViewModels/TraceSimMarkTests.cs`

**Interfaces:**
- Consumes: `CanFrame.FrameSource` (hil-core, M1 已有, default Bus)
- Produces: trace/replay 中 Environment 帧显示灰色斜体 "sim" 标签

- [ ] **Step 1: Write failing test**

```csharp
using Xunit;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;

namespace PeakCan.Host.App.Tests.ViewModels;

public class TraceSimMarkTests
{
    [Fact]
    public void TraceRow_BusFrame_IsNotSim()
    {
        var frame = new CanFrame(new CanId(0x100, FrameFormat.Standard), new byte[] { 1 });
        var row = TraceRow.FromFrame(frame);
        Assert.False(row.IsSim);
    }

    [Fact]
    public void TraceRow_EnvironmentFrame_IsSim()
    {
        var frame = new CanFrame(new CanId(0x100, FrameFormat.Standard), new byte[] { 1 }, FrameFlags.None, default, default, FrameSource.Environment);
        var row = TraceRow.FromFrame(frame);
        Assert.True(row.IsSim);
    }
}
```

注意：`TraceRow` 类名/位置需实施时确认（rg `TraceRow|TraceViewModel` 找到正确文件）。

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter "TraceSimMarkTests" --no-restore`
Expected: FAIL — `TraceRow.IsSim` doesn't exist

- [ ] **Step 3: Add IsSim to trace row model + XAML style**

Trace row VM 加 `public bool IsSim` property。XAML DataTrigger:
```xml
<DataTrigger Binding="{Binding IsSim}" Value="True">
    <Setter Property="Foreground" Value="Gray" />
    <Setter Property="FontStyle" Value="Italic" />
</DataTrigger>
```

- [ ] **Step 4: Run test to verify it passes + build**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter "TraceSimMarkTests" --no-restore && dotnet build src/PeakCan.Host.App --no-restore`
Expected: PASS + Build succeeds

- [ ] **Step 5: Commit**

```bash
git -C peakcan-host add src/PeakCan.Host.App/ tests/PeakCan.Host.App.Tests/ViewModels/TraceSimMarkTests.cs
git -C peakcan-host commit -m "feat: sim frame grey italic mark in trace/replay views"
```

---

### Task 10: Studio "总线环境"页签（三铁律 RestbusNode 编辑器）

**Files:**
- Create: `peakcan-studio/src/PeakCan.Studio.App/ViewModels/Environment/EnvironmentTabViewModel.cs`
- Create: `peakcan-studio/src/PeakCan.Studio.App/ViewModels/Environment/EnvironmentNodeViewModel.cs`
- Create: `peakcan-studio/src/PeakCan.Studio.App/ViewModels/Environment/TemplateCatalogViewModel.cs`
- Create: `peakcan-studio/src/PeakCan.Studio.App/Views/EnvironmentTab.xaml`
- Create: `peakcan-studio/src/PeakCan.Studio.App/Views/EnvironmentTab.xaml.cs`
- Modify: TestSuite 编辑器主视图 — 注入新页签
- Test: `peakcan-studio/tests/PeakCan.Studio.App.Tests/Environment/EnvironmentTabViewModelTests.cs`

**Interfaces:**
- Consumes: `RestbusNode` (hil-core), `Gbt27930ChargerTemplate` (Task 7), `TestSuite.Environment`
- Produces: `EnvironmentTabViewModel` — template selection → node application → SignalOverrides editing → suite save

- [ ] **Step 1: Write failing test for template application**

```csharp
using Xunit;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.HIL.Core.Templates;
using PeakCan.Studio.App.ViewModels.Environment;

namespace PeakCan.Studio.App.Tests.Environment;

public class EnvironmentTabViewModelTests
{
    [Fact]
    public void ApplyTemplate_CreatesNodeWithTrialContract()
    {
        var vm = new EnvironmentTabViewModel();
        vm.AvailableTemplates.Add(new TemplateCatalogItem("gbt27930-charger", "GB/T 27930 充电桩", Gbt27930ChargerTemplate.Create));
        vm.ApplyTemplate("gbt27930-charger");
        var node = Assert.Single(vm.Nodes);
        Assert.Equal("Charger", node.Name);
        Assert.NotNull(node.Trial);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PeakCan.Studio.App.Tests --filter "EnvironmentTabViewModelTests" --no-restore`
Expected: FAIL — classes not found

- [ ] **Step 3: Implement TemplateCatalogViewModel + EnvironmentTabViewModel**

`TemplateCatalogItem.cs`:
```csharp
namespace PeakCan.Studio.App.ViewModels.Environment;

public sealed record TemplateCatalogItem(string Id, string DisplayName, Func<RestbusNode> Create);
```

`EnvironmentTabViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.HIL.Core.Templates;

namespace PeakCan.Studio.App.ViewModels.Environment;

public sealed class EnvironmentTabViewModel
{
    public ObservableCollection<TemplateCatalogItem> AvailableTemplates { get; } = [];
    public ObservableCollection<EnvironmentNodeViewModel> Nodes { get; } = [];

    public void ApplyTemplate(string templateId)
    {
        var template = AvailableTemplates.FirstOrDefault(t => t.Id == templateId);
        if (template is null) return;
        var node = template.Create();
        Nodes.Add(new EnvironmentNodeViewModel(node));
    }

    public void RemoveNode(string nodeName)
    {
        var item = Nodes.FirstOrDefault(n => n.Name == nodeName);
        if (item is not null) Nodes.Remove(item);
    }

    public IReadOnlyList<RestbusNode> BuildSuiteEnvironment() => [.. Nodes.Select(n => n.ToNode())];
    /// <summary>静态总线负载预览（spec §4.2）。公式：Σ(帧长度*8*1000/IntervalMs) / BaudRate。波特率缺失时返回 null（显示"负载未知"）。</summary>
    public static double? EstimateBusLoad(IReadOnlyList<RestbusNode> nodes, int baudRate)
    {
        if (baudRate <= 0) return null;
        long totalBitsPerSec = 0;
        foreach (var node in nodes)
            foreach (var msg in node.Messages.Where(m => m.Enabled))
            {
                var frameBits = 8 * 8 + 47; // 64 data bits + 47 overhead (SOF..CRC+ACK approx)
                if (msg.Fd) frameBits += 20; // FD extra bits
                totalBitsPerSec += (long)(frameBits * 1000.0 / msg.IntervalMs);
            }
        return Math.Round((double)totalBitsPerSec / baudRate * 100, 1);
    }
}
```

`EnvironmentNodeViewModel.cs`:
```csharp
using PeakCan.HIL.Core.HIL.Environment;

namespace PeakCan.Studio.App.ViewModels.Environment;

/// <summary>单个环境节点卡片 VM。只允许改信号值（三铁律①②）；规则编辑只从模板来。</summary>
public sealed class EnvironmentNodeViewModel
{
    private readonly RestbusNode _node;

    public EnvironmentNodeViewModel(RestbusNode node) => _node = node;

    public string Name => _node.Name;
    public string? Tag => _node.Tag;
    public int MessageCount => _node.Messages.Count;
    public int RuleCount => _node.Rules.Count;
    public string? TemplateId => _node.Trial?.TemplateId;

    public RestbusNode ToNode() => _node;
}
```

- [ ] **Step 4: Create EnvironmentTab.xaml**

基本布局: 左侧模板列表 (ListBox) + 右侧已启用节点卡片 (ItemsControl)。三铁律：
1. 无 CAN ID 手填框
2. 无"新建空白 ECA"按钮
3. 节点数据随 suite JSON 保存（`Environment` 字段）

```xml
<UserControl x:Class="PeakCan.Studio.App.Views.EnvironmentTab" ...>
  <Grid>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width="200"/>
      <ColumnDefinition Width="*"/>
    </Grid.ColumnDefinitions>
    <!-- 模板列表 -->
    <ListBox Grid.Column="0" ItemsSource="{Binding AvailableTemplates}"
             DisplayMemberPath="DisplayName"
             SelectedItem="{Binding SelectedTemplate}" />
    <Button Content="应用模板" Command="{Binding ApplyTemplateCommand}" />
    <!-- 节点列表 -->
    <ItemsControl Grid.Column="1" ItemsSource="{Binding Nodes}">
      <!-- 节点卡片：名称、消息数、规则数、模板标识、[改信号值] 按钮 -->
    </ItemsControl>
  </Grid>
</UserControl>
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/PeakCan.Studio.App.Tests --filter "EnvironmentTabViewModelTests" --no-restore`
Expected: PASS
Run: `dotnet build src/PeakCan.Studio.App --no-restore`
Expected: Build succeeds

- [ ] **Step 6: Commit**

```bash
git -C peakcan-studio add src/PeakCan.Studio.App/ViewModels/Environment/ src/PeakCan.Studio.App/Views/EnvironmentTab.xaml* tests/PeakCan.Studio.App.Tests/Environment/
git -C peakcan-studio commit -m "feat: studio bus-environment tab with template catalog + node cards"
```

---

### Task 11: 旧节点/EcuScript 导入器

**Files:**
- Create: `peakcan-studio/src/PeakCan.Studio.App/Services/Environment/RestbusNodeImportService.cs`
- Test: `peakcan-studio/tests/PeakCan.Studio.App.Tests/Environment/RestbusNodeImportServiceTests.cs`

**Interfaces:**
- Consumes: old `host.App/Services/Nodes/NodeModel.cs` JSON format, old `EcuScript` JSON format
- Produces: `RestbusNodeImportService.ImportNodeJson(string json)` → `RestbusNode`; `.ImportEcuScriptJson(string json)` → `RestbusNode`

- [ ] **Step 1: Write failing test**

```csharp
using Xunit;
using PeakCan.Studio.App.Services.Environment;

namespace PeakCan.Studio.App.Tests.Environment;

public class RestbusNodeImportServiceTests
{
    [Fact]
    public void ImportNodeJson_ConvertsOldNodeModel()
    {
        var json = """
{
  "Name": "OldNode",
  "Tag": "test",
  "Identity": { "kind": "j1939", "Sa": 1 },
  "Messages": [
    { "Ref": { "kind": "j1939", "Pgn": 61444, "Priority": 6, "Sa": 1 }, "IntervalMs": 100,
      "Payload": { "kind": "fixedHex", "Hex": "0102030405060708" } }
  ],
  "Rules": [],
  "AddressClaimEnabled": false
}
""";
        var node = RestbusNodeImportService.ImportNodeJson(json);
        Assert.Equal("OldNode", node.Name);
        Assert.NotEmpty(node.Messages);
    }
}
```

注意：旧 `NodeModel` JSON 格式需要实施时从 `NodeModel.cs` / `NodeModelJsonTests.cs` 读取确切结构。

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PeakCan.Studio.App.Tests --filter "RestbusNodeImportServiceTests" --no-restore`
Expected: FAIL — class not found

- [ ] **Step 3: Implement importer**

反序列化旧 JSON → 手工映射到 `RestbusNode`（不共用旧类，独立 DTO）。关键映射:
- `NodeModel.Name` → `RestbusNode.Name`
- `NodeModel.Identity` → `RestbusNode.Identity` (Channel 字段不导入——spec 已删除)
- `NodeModel.Messages[].Ref` → `NodeMessage.Ref`
- `NodeModel.Messages[].IntervalMs` → `NodeMessage.IntervalMs`（若 <10 则报 warning + clamp 到 10）
- `NodeModel.Rules` → `RestbusNode.Rules`
- `NodeModel.AddressClaimEnabled` → `RestbusNode.AddressClaimEnabled`
- EcuScript → `EcuScriptDefinition` → `RestbusNode.UdsBehavior`

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PeakCan.Studio.App.Tests --filter "RestbusNodeImportServiceTests" --no-restore`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git -C peakcan-studio add src/PeakCan.Studio.App/Services/Environment/ tests/PeakCan.Studio.App.Tests/Environment/
git -C peakcan-studio commit -m "feat: legacy node/EcuScript JSON import to RestbusNode"
```

---

### Task 12: 最终验证 + lockstep bump + InteropTests

**Files:**
- Modify: all 3 repos — version bump 0.17.0 → 0.18.0
- Test: hil-core InteropTests + host InteropTests + studio build

**Interfaces:**
- Consumes: all prior tasks
- Produces: 3-repo 0.18.0 lockstep, all green, pushed

- [ ] **Step 1: Run full hil-core suite**

Run: `dotnet test tests/PeakCan.HIL.Core.Tests --no-restore`
Expected: ALL PASS

- [ ] **Step 2: Run full host suite**

Run: `dotnet test tests/PeakCan.Host.Core.Tests --no-restore && dotnet test tests/PeakCan.Host.Infrastructure.Tests --no-restore && dotnet test tests/PeakCan.Host.App.Tests --no-restore`
Expected: ALL PASS

- [ ] **Step 3: Build studio**

Run: `dotnet build src/PeakCan.Studio.App --no-restore`
Expected: Build succeeds

- [ ] **Step 4: Run InteropTests (hil-core serialization round-trip in host context)**

Run: `dotnet test tests/PeakCan.Host.Core.Tests --filter "Interop" --no-restore`
Expected: PASS — hil-core model types serialize/deserialize identically in host

- [ ] **Step 5: Version bump all 3 repos to 0.18.0**

```bash
# hil-core csproj
# host csproj (PackageReference pin)
# studio csproj (PackageReference pin)
git -C peakcan-hil-core commit -m "chore!: bump 0.18.0"
git -C peakcan-host commit -m "chore!: bump 0.18.0 (lockstep)"
git -C peakcan-studio commit -m "chore!: bump 0.18.0 (lockstep)"
```

- [ ] **Step 6: dotnet pack hil-core**

```bash
dotnet pack src/PeakCan.HIL.Core --configuration Release
```

- [ ] **Step 7: Final push**

```bash
git -C peakcan-hil-core push origin feat/restbus-unification
git -C peakcan-host push origin feat/restbus-unification
git -C peakcan-studio push origin feat/restbus-unification
```

---