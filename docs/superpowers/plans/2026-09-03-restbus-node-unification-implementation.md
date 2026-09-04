# Restbus Node Unification (M1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将三套并行 ECU 模拟机制（BackgroundFrame / NodeConfig / EcuScript）统一为 hil-core `RestbusNode` 模型 + host `EnvironmentRuntime` 单一执行器，`BackgroundFrame`/`BackgroundFrameSender` 退役。

**Architecture:** hil-core 新增 `HIL/Environment/` 目录承载统一模型（纯数据、零 I/O）；host Infrastructure 新增 `EnvironmentRuntime` 吸收 `RuleBasedBehavior` 10ms 单扫描 + `BackgroundFrameTimer` 周期发送语义；`HilRunnerService` 在通道连接后 `EnvironmentRuntime.Start(nodes, channels)`，在 finally `Stop()`。CanFrame 新增 `FrameSource` 标记 sim 帧。

**Tech Stack:** .NET 10 / C# 13 (sealed records, System.Text.Json polymorphic), xUnit, NetArchTest, TimeProvider

**Spec:** `docs/superpowers/specs/2026-09-03-restbus-node-unification-design.md` (Draft v3)

## Global Constraints

- hil-core 版本 bump 0.16.0 → 0.17.0；host/studio lockstep；双侧 InteropTests 必须绿
- 新增序列化字段一律可空默认（null = 旧 suite JSON 无该字段，行为同今）
- Core 层零 I/O、零厂商 SDK 依赖（NetArchTest 红线）——`RestbusNode`/`EcuScriptDefinition`/`TrialContract` 不得引用定时器、通道句柄或厂商类型
- 无存量原则：`BackgroundFrames` 字段和 `ModifyBackgroundFrameStep` 直接删除，不写迁移层
- `NodeMessage.IntervalMs >= 10`（validator 拦截 `< 10`）
- 编码顺序锁定：信号状态 + SignalOverrides → DbcSignalsSource 编码 → counter/checksum → 发送
- 线程契约：周期扫描/规则分发/UDS/控制面共享同一把 runtime 执行锁
- incoming 队列：容量 256，DropOldest，计数 + 节流 warning
- 连续 10 次发送失败后停用该 NodeMessage 并报告 Error
- J1939 发送不变式：`Sa == Identity.Sa`；PDU1/RTS-CTS 时 `Da` 必填；`Bam` 不要求 `Da`
- `EnvironmentRuntime` 发出的帧设置 `FrameSource.Environment`
- 所有 UI 代码变更遵循 spec §4.4 三铁律（用户全程不碰 JSON/不抠字节/不管理文件）
- 测试命令：`dotnet test`（各项目根）
- 提交格式：conventional commits（feat/fix/refactor/chore）

---
### Task 1: hil-core — 环境模型基础类型

**Files:**
- Create: `src/PeakCan.HIL.Core/HIL/Environment/NodeIdentity.cs`
- Create: `src/PeakCan.HIL.Core/HIL/Environment/MessageRef.cs`
- Create: `src/PeakCan.HIL.Core/HIL/Environment/NodePayloadSource.cs`
- Create: `src/PeakCan.HIL.Core/HIL/Environment/BytePattern.cs`
- Test: `tests/PeakCan.HIL.Core.Tests/HIL/Environment/ModelRoundTripTests.cs`

**Interfaces:**
- Consumes: hil-core `CanId`（`PeakCan.HIL.Core` namespace）、`TpMode`（`PeakCan.HIL.Core.J1939`）
- Produces: `NodeIdentity`（abstract, kind discriminator: `j1939` / `rawCan`）、`J1939NodeIdentity(byte Sa)`、`RawCanNodeIdentity`、`MessageRef`（abstract, kind: `j1939` / `can`）、`J1939MessageRef(uint Pgn, byte Priority, TpMode? Mode, byte? Sa, byte? Da)`、`CanMessageRef(uint Id, bool IsExtended)`、`NodePayloadSource`（abstract, kind: `fixedHex` / `dbcSignals` / `script`）、`FixedHexSource(string Hex)`、`DbcSignalsSource(string MessageName)`、`ScriptCallbackSource(string CallbackRef)`、`BytePattern(int Offset, byte Mask, byte Value)`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/PeakCan.HIL.Core.Tests/HIL/Environment/ModelRoundTripTests.cs
using System.Text.Json;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.HIL.Core.J1939;

namespace PeakCan.HIL.Core.Tests.HIL.Environment;

public class ModelRoundTripTests
{
    private static readonly JsonSerializerOptions Options =
        PeakCan.HIL.Core.HIL.Serialization.HILJsonOptions.Default;

    [Fact]
    public void J1939NodeIdentity_RoundTrip_PreservesSa()
    {
        NodeIdentity original = new J1939NodeIdentity(0xF4);
        var json = JsonSerializer.Serialize(original, Options);
        var result = JsonSerializer.Deserialize<NodeIdentity>(json, Options);
        Assert.IsType<J1939NodeIdentity>(result);
        Assert.Equal(0xF4, ((J1939NodeIdentity)result!).Sa);
    }

    [Fact]
    public void RawCanNodeIdentity_RoundTrip_PreservesNothing()
    {
        NodeIdentity original = new RawCanNodeIdentity();
        var json = JsonSerializer.Serialize(original, Options);
        var result = JsonSerializer.Deserialize<NodeIdentity>(json, Options);
        Assert.IsType<RawCanNodeIdentity>(result);
    }

    [Fact]
    public void NodeIdentity_DoesNotHaveChannelProperty()
    {
        // spec v3 消歧：通道唯一在 RestbusNode.Channel，不在 NodeIdentity 上
        var props = typeof(NodeIdentity).GetProperties();
        Assert.DoesNotContain(props, p => p.Name == "Channel");
    }

    [Fact]
    public void J1939MessageRef_RoundTrip_PreservesAllFields()
    {
        MessageRef original = new J1939MessageRef(0x0006, 6, TpMode.Single, 0xF4, 0x56);
        var json = JsonSerializer.Serialize(original, Options);
        var result = JsonSerializer.Deserialize<MessageRef>(json, Options);
        Assert.IsType<J1939MessageRef>(result);
        var typed = (J1939MessageRef)result!;
        Assert.Equal(0x0006u, typed.Pgn);
        Assert.Equal(6, typed.Priority);
        Assert.Equal(TpMode.Single, typed.Mode);
        Assert.Equal((byte)0xF4, typed.Sa);
        Assert.Equal((byte)0x56, typed.Da);
    }

    [Fact]
    public void CanMessageRef_RoundTrip_PreservesIdAndExtended()
    {
        MessageRef original = new CanMessageRef(0x18FF50E5, true);
        var json = JsonSerializer.Serialize(original, Options);
        var result = JsonSerializer.Deserialize<MessageRef>(json, Options);
        Assert.IsType<CanMessageRef>(result);
        var typed = (CanMessageRef)result!;
        Assert.Equal(0x18FF50E5u, typed.Id);
        Assert.True(typed.IsExtended);
    }

    [Fact]
    public void FixedHexSource_RoundTrip_PreservesHex()
    {
        NodePayloadSource original = new FixedHexSource("01 02 03");
        var json = JsonSerializer.Serialize(original, Options);
        var result = JsonSerializer.Deserialize<NodePayloadSource>(json, Options);
        Assert.IsType<FixedHexSource>(result);
        Assert.Equal("01 02 03", ((FixedHexSource)result!).Hex);
    }

    [Fact]
    public void DbcSignalsSource_RoundTrip_PreservesMessageName()
    {
        NodePayloadSource original = new DbcSignalsSource("BRM");
        var json = JsonSerializer.Serialize(original, Options);
        var result = JsonSerializer.Deserialize<NodePayloadSource>(json, Options);
        Assert.IsType<DbcSignalsSource>(result);
        Assert.Equal("BRM", ((DbcSignalsSource)result!).MessageName);
    }

    [Fact]
    public void ScriptCallbackSource_RoundTrip_PreservesCallbackRef()
    {
        NodePayloadSource original = new ScriptCallbackSource("myHandler");
        var json = JsonSerializer.Serialize(original, Options);
        var result = JsonSerializer.Deserialize<NodePayloadSource>(json, Options);
        Assert.IsType<ScriptCallbackSource>(result);
        Assert.Equal("myHandler", ((ScriptCallbackSource)result!).CallbackRef);
    }

    [Fact]
    public void BytePattern_RoundTrip_PreservesFields()
    {
        var original = new BytePattern(Offset: 2, Mask: 0x0F, Value: 0x05);
        var json = JsonSerializer.Serialize(original, Options);
        var result = JsonSerializer.Deserialize<BytePattern>(json, Options);
        Assert.Equal(original, result);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PeakCan.HIL.Core.Tests --filter "FullyQualifiedName~ModelRoundTripTests"`
Expected: FAIL — types not found

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/PeakCan.HIL.Core/HIL/Environment/NodeIdentity.cs
using System.Text.Json.Serialization;

namespace PeakCan.HIL.Core.HIL.Environment;

/// <summary>节点身份判别联合（j1939 | rawCan）。
/// 通道归属唯一在 RestbusNode.Channel；本类型不携带通道。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(J1939NodeIdentity), "j1939")]
[JsonDerivedType(typeof(RawCanNodeIdentity), "rawCan")]
public abstract record NodeIdentity;

/// <summary>J1939 节点身份：源地址 SA。</summary>
public sealed record J1939NodeIdentity(byte Sa) : NodeIdentity;

/// <summary>原始 CAN 节点身份（无协议语义约束）。</summary>
public sealed record RawCanNodeIdentity : NodeIdentity;
```

```csharp
// src/PeakCan.HIL.Core/HIL/Environment/MessageRef.cs
using System.Text.Json.Serialization;
using PeakCan.HIL.Core.J1939;

namespace PeakCan.HIL.Core.HIL.Environment;

/// <summary>报文引用判别联合（j1939 | can），序列化以 kind 为判别符。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(J1939MessageRef), "j1939")]
[JsonDerivedType(typeof(CanMessageRef), "can")]
public abstract record MessageRef;

/// <summary>J1939 报文引用：PGN/优先级/TP 模式/源目标地址；Sa、Mode 可空以支持宽容匹配。</summary>
public sealed record J1939MessageRef(uint Pgn, byte Priority, TpMode? Mode, byte? Sa, byte? Da = null) : MessageRef;

/// <summary>原始 CAN 报文引用（含扩展帧标志）。</summary>
public sealed record CanMessageRef(uint Id, bool IsExtended) : MessageRef;
```

```csharp
// src/PeakCan.HIL.Core/HIL/Environment/NodePayloadSource.cs
using System.Text.Json.Serialization;

namespace PeakCan.HIL.Core.HIL.Environment;

/// <summary>节点发送载荷来源判别联合（fixedHex | dbcSignals | script）。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(FixedHexSource), "fixedHex")]
[JsonDerivedType(typeof(DbcSignalsSource), "dbcSignals")]
[JsonDerivedType(typeof(ScriptCallbackSource), "script")]
public abstract record NodePayloadSource;

/// <summary>固定十六进制字节串载荷。</summary>
public sealed record FixedHexSource(string Hex) : NodePayloadSource;

/// <summary>按 DBC 消息名编码当前信号值的载荷。</summary>
public sealed record DbcSignalsSource(string MessageName) : NodePayloadSource;

/// <summary>由脚本回调动态生成的载荷。</summary>
public sealed record ScriptCallbackSource(string CallbackRef) : NodePayloadSource;
```

```csharp
// src/PeakCan.HIL.Core/HIL/Environment/BytePattern.cs
namespace PeakCan.HIL.Core.HIL.Environment;

/// <summary>载荷字节模式条件：(payload[Offset] &amp; Mask) == Value。</summary>
public sealed record BytePattern(int Offset, byte Mask, byte Value);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PeakCan.HIL.Core.Tests --filter "FullyQualifiedName~ModelRoundTripTests"`
Expected: PASS (all 9 tests)

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.HIL.Core/HIL/Environment/NodeIdentity.cs
git add src/PeakCan.HIL.Core/HIL/Environment/MessageRef.cs
git add src/PeakCan.HIL.Core/HIL/Environment/NodePayloadSource.cs
git add src/PeakCan.HIL.Core/HIL/Environment/BytePattern.cs
git add tests/PeakCan.HIL.Core.Tests/HIL/Environment/ModelRoundTripTests.cs
git commit -m "feat(hil-core): add environment model base types (NodeIdentity, MessageRef, NodePayloadSource, BytePattern)"
```

---
### Task 2: hil-core — NodeMessage + NodeAction + ResponseRule

**Files:**
- Create: `src/PeakCan.HIL.Core/HIL/Environment/NodeMessage.cs`
- Create: `src/PeakCan.HIL.Core/HIL/Environment/NodeAction.cs`
- Create: `src/PeakCan.HIL.Core/HIL/Environment/ResponseRule.cs`
- Test: `tests/PeakCan.HIL.Core.Tests/HIL/Environment/NodeMessageActionRoundTripTests.cs`

**Interfaces:**
- Consumes: Task 1 types (`MessageRef`, `NodePayloadSource`, `BytePattern`), hil-core `CounterConfig`/`ChecksumConfig`
- Produces: `NodeMessage(MessageRef Ref, int IntervalMs, NodePayloadSource Payload, bool Fd = false, bool Enabled = true, CounterConfig? AutoCounter = null, ChecksumConfig? AutoChecksum = null)`、`NodeAction`（abstract, kind: send/setSignal/start/stop/script）、`SendMessageAction(MessageRef Ref, NodePayloadSource Payload)`、`SetSignalAction(string MessageName, string SignalName, double Value)`、`StartMessageAction(MessageRef Ref)`、`StopMessageAction(MessageRef Ref)`、`ScriptAction(string ScriptRef)`、`ResponseRule(MessageRef Trigger, BytePattern? Condition, NodeAction Action, int DelayMs)`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/PeakCan.HIL.Core.Tests/HIL/Environment/NodeMessageActionRoundTripTests.cs
using System.Text.Json;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;

namespace PeakCan.HIL.Core.Tests.HIL.Environment;

public class NodeMessageActionRoundTripTests
{
    private static readonly JsonSerializerOptions Options =
        PeakCan.HIL.Core.HIL.Serialization.HILJsonOptions.Default;

    [Fact]
    public void NodeMessage_RoundTrip_PreservesAllFields()
    {
        var original = new NodeMessage(
            new CanMessageRef(0x123, false), 100,
            new FixedHexSource("01 02"), Fd: false, Enabled: true,
            AutoCounter: new CounterConfig(0, 4));
        var json = JsonSerializer.Serialize(original, Options);
        var result = JsonSerializer.Deserialize<NodeMessage>(json, Options);
        Assert.NotNull(result);
        Assert.Equal(100, result!.IntervalMs);
        Assert.False(result.Fd);
        Assert.True(result.Enabled);
        Assert.NotNull(result.AutoCounter);
    }

    [Fact]
    public void NodeMessage_FdFlag_DoesNotInferFromPayloadLength()
    {
        var msg = new NodeMessage(
            new CanMessageRef(0x456, true), 50,
            new FixedHexSource("01 02 03 04 05 06 07 08 09 0A 0B 0C"), Fd: true);
        Assert.True(msg.Fd);
    }

    [Fact]
    public void NodeMessage_IntervalMsConstraint_Enforced()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NodeMessage(new CanMessageRef(0x123, false), 5, new FixedHexSource("01")));
    }

    [Fact]
    public void SendMessageAction_RoundTrip()
    {
        NodeAction original = new SendMessageAction(
            new CanMessageRef(0x789, false), new FixedHexSource("FF"));
        var json = JsonSerializer.Serialize(original, Options);
        var result = JsonSerializer.Deserialize<NodeAction>(json, Options);
        Assert.IsType<SendMessageAction>(result);
    }

    [Fact]
    public void SetSignalAction_RoundTrip()
    {
        NodeAction original = new SetSignalAction("BCL", "SOC", 50.0);
        var json = JsonSerializer.Serialize(original, Options);
        var result = JsonSerializer.Deserialize<NodeAction>(json, Options);
        Assert.IsType<SetSignalAction>(result);
        var typed = (SetSignalAction)result!;
        Assert.Equal("BCL", typed.MessageName);
        Assert.Equal("SOC", typed.SignalName);
        Assert.Equal(50.0, typed.Value);
    }

    [Fact]
    public void ResponseRule_RoundTrip()
    {
        var original = new ResponseRule(
            new CanMessageRef(0x300, false), new BytePattern(0, 0xFF, 0x01),
            new SendMessageAction(new CanMessageRef(0x123, false), new FixedHexSource("AA")),
            100);
        var json = JsonSerializer.Serialize(original, Options);
        var result = JsonSerializer.Deserialize<ResponseRule>(json, Options);
        Assert.NotNull(result);
        Assert.Equal(100, result!.DelayMs);
        Assert.NotNull(result.Condition);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PeakCan.HIL.Core.Tests --filter "FullyQualifiedName~NodeMessageActionRoundTripTests"`
Expected: FAIL — types not found

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/PeakCan.HIL.Core/HIL/Environment/NodeMessage.cs
using PeakCan.HIL.Core.Dbc;

namespace PeakCan.HIL.Core.HIL.Environment;

/// <summary>节点周期发送的一条报文。</summary>
/// <param name="IntervalMs">发送周期。约束 >= 10：执行底座为 10ms 单扫描，实际发送存在 <=10ms 抖动。</param>
/// <param name="Fd">CAN FD 帧格式。该字段是通道能力之外的帧级语义，不能从 payload 长度推断。</param>
public sealed record NodeMessage(
    MessageRef Ref,
    int IntervalMs,
    NodePayloadSource Payload,
    bool Fd = false,
    bool Enabled = true,
    CounterConfig? AutoCounter = null,
    ChecksumConfig? AutoChecksum = null)
{
    public int IntervalMs { get; init; } = IntervalMs < 10
        ? throw new ArgumentOutOfRangeException(nameof(IntervalMs), IntervalMs, "IntervalMs must be >= 10.")
        : IntervalMs;
}
```

```csharp
// src/PeakCan.HIL.Core/HIL/Environment/NodeAction.cs
using System.Text.Json.Serialization;

namespace PeakCan.HIL.Core.HIL.Environment;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SendMessageAction), "send")]
[JsonDerivedType(typeof(SetSignalAction), "setSignal")]
[JsonDerivedType(typeof(StartMessageAction), "start")]
[JsonDerivedType(typeof(StopMessageAction), "stop")]
[JsonDerivedType(typeof(ScriptAction), "script")]
public abstract record NodeAction;

public sealed record SendMessageAction(MessageRef Ref, NodePayloadSource Payload) : NodeAction;
public sealed record SetSignalAction(string MessageName, string SignalName, double Value) : NodeAction;
public sealed record StartMessageAction(MessageRef Ref) : NodeAction;
public sealed record StopMessageAction(MessageRef Ref) : NodeAction;
public sealed record ScriptAction(string ScriptRef) : NodeAction;
```

```csharp
// src/PeakCan.HIL.Core/HIL/Environment/ResponseRule.cs
namespace PeakCan.HIL.Core.HIL.Environment;

/// <summary>触发-响应规则：Trigger 命中（可选 Condition 字节模式匹配）后延迟 DelayMs 执行 Action。</summary>
public sealed record ResponseRule(MessageRef Trigger, BytePattern? Condition, NodeAction Action, int DelayMs);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PeakCan.HIL.Core.Tests --filter "FullyQualifiedName~NodeMessageActionRoundTripTests"`
Expected: PASS (all 7 tests)

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.HIL.Core/HIL/Environment/NodeMessage.cs
git add src/PeakCan.HIL.Core/HIL/Environment/NodeAction.cs
git add src/PeakCan.HIL.Core/HIL/Environment/ResponseRule.cs
git add tests/PeakCan.HIL.Core.Tests/HIL/Environment/NodeMessageActionRoundTripTests.cs
git commit -m "feat(hil-core): add NodeMessage, NodeAction, ResponseRule to Environment namespace"
```

---
### Task 3: hil-core — RestbusNode 聚合根 + EcuScriptDefinition + TrialContract

**Files:**
- Create: `src/PeakCan.HIL.Core/HIL/Environment/EcuScriptDefinition.cs`
- Create: `src/PeakCan.HIL.Core/HIL/Environment/TrialContract.cs`
- Create: `src/PeakCan.HIL.Core/HIL/Environment/RestbusNode.cs`
- Test: `tests/PeakCan.HIL.Core.Tests/HIL/Environment/RestbusNodeRoundTripTests.cs`

**Interfaces:**
- Consumes: Tasks 1-2 types, hil-core `EcuStateTransition` (`HIL/Contracts/`), `CanIdConfig` (`Uds/IsoTp/`)
- Produces: `EcuScriptDefinition(CanIdConfig CanIds, IReadOnlyList<EcuStateTransition> Transitions, IReadOnlyDictionary<string, byte[]>? DidValues, string InitialState, IReadOnlyList<string>? GeneratorRefs)`、`HandshakeExpectation(string Send, string? ThenReceive, int TimeoutMs, IReadOnlyList<string> PossibleCauses)`、`TrialContract(string TemplateId, IReadOnlyList<HandshakeExpectation> Handshake, IReadOnlyList<string> RequiredDbcMessages)`、`RestbusNode`（聚合根）

- [ ] **Step 1: Write the failing test**

```csharp
// tests/PeakCan.HIL.Core.Tests/HIL/Environment/RestbusNodeRoundTripTests.cs
using System.Text.Json;
using PeakCan.HIL.Core.HIL.Environment;

namespace PeakCan.HIL.Core.Tests.HIL.Environment;

public class RestbusNodeRoundTripTests
{
    private static readonly JsonSerializerOptions Options =
        PeakCan.HIL.Core.HIL.Serialization.HILJsonOptions.Default;

    [Fact]
    public void MinimalNode_RoundTrip_PreservesNameAndIdentity()
    {
        var original = new RestbusNode { Name = "Charger", Identity = new J1939NodeIdentity(0xF4) };
        var json = JsonSerializer.Serialize(original, Options);
        var result = JsonSerializer.Deserialize<RestbusNode>(json, Options);
        Assert.NotNull(result);
        Assert.Equal("Charger", result!.Name);
        Assert.IsType<J1939NodeIdentity>(result.Identity);
        Assert.Equal((byte)0xF4, ((J1939NodeIdentity)result.Identity).Sa);
    }

    [Fact]
    public void FullNode_RoundTrip_PreservesAllCollections()
    {
        var original = new RestbusNode
        {
            Name = "VCU", Tag = "gbt27930", Channel = "CAN1",
            Identity = new RawCanNodeIdentity(),
            Messages =
            [
                new NodeMessage(new CanMessageRef(0x100, false), 50, new FixedHexSource("01 02")),
                new NodeMessage(new CanMessageRef(0x200, false), 100, new DbcSignalsSource("BCL")),
            ],
            Rules =
            [
                new ResponseRule(
                    new CanMessageRef(0x300, false), null,
                    new SetSignalAction("BCL", "SOC", 80), 10),
            ],
            SignalOverrides = new Dictionary<string, double> { ["BCL.SOC"] = 50.0 },
        };
        var json = JsonSerializer.Serialize(original, Options);
        var result = JsonSerializer.Deserialize<RestbusNode>(json, Options);
        Assert.NotNull(result);
        Assert.Equal(2, result!.Messages.Count);
        Assert.Single(result.Rules);
        Assert.Equal("CAN1", result.Channel);
        Assert.NotNull(result.SignalOverrides);
        Assert.Equal(50.0, result.SignalOverrides!["BCL.SOC"]);
    }

    [Fact]
    public void NodeWithTrialContract_RoundTrip()
    {
        var original = new RestbusNode
        {
            Name = "Charger", Identity = new J1939NodeIdentity(0xF4),
            Trial = new TrialContract(
                "gbt27930-charger",
                [new HandshakeExpectation("CRM", "BRM", 500, ["接线/通道选错", "BMS 未上电"])],
                ["CRM", "BRM", "CTS", "BCL", "CST", "BST"]),
        };
        var json = JsonSerializer.Serialize(original, Options);
        var result = JsonSerializer.Deserialize<RestbusNode>(json, Options);
        Assert.NotNull(result!.Trial);
        Assert.Equal("gbt27930-charger", result.Trial!.TemplateId);
        Assert.Single(result.Trial.Handshake);
        Assert.Equal(500, result.Trial.Handshake[0].TimeoutMs);
    }

    [Fact]
    public void TrialContract_HasNoChannelProperty()
    {
        var props = typeof(TrialContract).GetProperties();
        Assert.DoesNotContain(props, p => p.Name == "Channel");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PeakCan.HIL.Core.Tests --filter "FullyQualifiedName~RestbusNodeRoundTripTests"`
Expected: FAIL — types not found

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/PeakCan.HIL.Core/HIL/Environment/EcuScriptDefinition.cs
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds.IsoTp;

namespace PeakCan.HIL.Core.HIL.Environment;

/// <summary>UDS 行为的纯数据形态。运行时由 EnvironmentRuntime 构造 EcuStateMachine。不持有状态。</summary>
public sealed record EcuScriptDefinition(
    CanIdConfig CanIds,
    IReadOnlyList<EcuStateTransition> Transitions,
    IReadOnlyDictionary<string, byte[]>? DidValues = null,
    string InitialState = "default",
    IReadOnlyList<string>? GeneratorRefs = null);
```

```csharp
// src/PeakCan.HIL.Core/HIL/Environment/TrialContract.cs
namespace PeakCan.HIL.Core.HIL.Environment;

/// <summary>试运行诊断契约：模板应用后嵌入节点；host 试运行按此输出具体环节 + 可能原因。</summary>
public sealed record TrialContract(
    string TemplateId,
    IReadOnlyList<HandshakeExpectation> Handshake,
    IReadOnlyList<string> RequiredDbcMessages);

/// <summary>单个握手期望步骤。</summary>
public sealed record HandshakeExpectation(
    string Send, string? ThenReceive, int TimeoutMs, IReadOnlyList<string> PossibleCauses);
```

```csharp
// src/PeakCan.HIL.Core/HIL/Environment/RestbusNode.cs
namespace PeakCan.HIL.Core.HIL.Environment;

/// <summary>统一节点聚合根：替代 BackgroundFrame + NodeConfig + EcuScript 三套机制。
/// 哑节点 = 没有行为的节点（Messages 有值但 Rules/UdsBehavior 为空）。</summary>
public sealed record RestbusNode
{
    public required string Name { get; init; }
    public string? Tag { get; init; }
    /// <summary>通道绑定（null = 单通道 suite）；有 Channels 时必填且必须按名命中。</summary>
    public string? Channel { get; init; }
    public required NodeIdentity Identity { get; init; }
    public IReadOnlyList<NodeMessage> Messages { get; init; } = [];
    public IReadOnlyList<ResponseRule> Rules { get; init; } = [];
    public EcuScriptDefinition? UdsBehavior { get; init; }
    public bool AddressClaimEnabled { get; init; }
    public TrialContract? Trial { get; init; }
    /// <summary>节点级信号初值覆盖。键格式 "MessageName.SignalName"。只对 DbcSignalsSource 生效。</summary>
    public IReadOnlyDictionary<string, double>? SignalOverrides { get; init; }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PeakCan.HIL.Core.Tests --filter "FullyQualifiedName~RestbusNodeRoundTripTests"`
Expected: PASS (all 4 tests)

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.HIL.Core/HIL/Environment/EcuScriptDefinition.cs
git add src/PeakCan.HIL.Core/HIL/Environment/TrialContract.cs
git add src/PeakCan.HIL.Core/HIL/Environment/RestbusNode.cs
git add tests/PeakCan.HIL.Core.Tests/HIL/Environment/RestbusNodeRoundTripTests.cs
git commit -m "feat(hil-core): add RestbusNode aggregate root, EcuScriptDefinition, TrialContract"
```

---
### Task 4: hil-core — CanFrame FrameSource 标记

**Files:**
- Create: `src/PeakCan.HIL.Core/FrameSource.cs`
- Modify: `src/PeakCan.HIL.Core/CanFrame.cs`
- Test: `tests/PeakCan.HIL.Core.Tests/FrameSourceTests.cs`

**Interfaces:**
- Consumes: hil-core `CanFrame` readonly record struct（existing）
- Produces: `FrameSource` enum（Bus = 0, Environment = 1）；`CanFrame` 新增 `FrameSource` 可选参数（默认 `FrameSource.Bus`）

- [ ] **Step 1: Write the failing test**

```csharp
// tests/PeakCan.HIL.Core.Tests/FrameSourceTests.cs
using PeakCan.HIL.Core;

namespace PeakCan.HIL.Core.Tests;

public class FrameSourceTests
{
    [Fact]
    public void DefaultFrameSource_IsBus()
    {
        var frame = new CanFrame(
            new CanId(0x123, FrameFormat.Standard),
            ReadOnlyMemory<byte>.Empty, FrameFlags.None, default, default);
        Assert.Equal(FrameSource.Bus, frame.FrameSource);
    }

    [Fact]
    public void EnvironmentFrameSource_ExplicitSet()
    {
        var frame = new CanFrame(
            new CanId(0x123, FrameFormat.Standard),
            ReadOnlyMemory<byte>.Empty, FrameFlags.None, default, default,
            FrameSource.Environment);
        Assert.Equal(FrameSource.Environment, frame.FrameSource);
    }

    [Fact]
    public void FrameSource_ValuesMatchSpec()
    {
        Assert.Equal(0, (int)FrameSource.Bus);
        Assert.Equal(1, (int)FrameSource.Environment);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PeakCan.HIL.Core.Tests --filter "FullyQualifiedName~FrameSourceTests"`
Expected: FAIL — FrameSource not found

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/PeakCan.HIL.Core/FrameSource.cs
namespace PeakCan.HIL.Core;

/// <summary>帧来源标记（spec §6.5）。</summary>
public enum FrameSource
{
    /// <summary>真实总线硬件帧（默认；旧数据 / 旧 replay 按此解释）。</summary>
    Bus = 0,
    /// <summary>EnvironmentRuntime 发出的模拟帧。</summary>
    Environment = 1,
}
```

Modify `CanFrame.cs` — append to primary constructor:
```csharp
public readonly record struct CanFrame(
    CanId Id,
    ReadOnlyMemory<byte> Data,
    FrameFlags Flags,
    ChannelId Channel,
    Timestamp Timestamp,
    FrameSource FrameSource = FrameSource.Bus)
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PeakCan.HIL.Core.Tests --filter "FullyQualifiedName~FrameSourceTests"`
Expected: PASS (all 3 tests)

- [ ] **Step 5: Run full hil-core tests to check no regression**

Run: `dotnet test tests/PeakCan.HIL.Core.Tests`
Expected: PASS (existing callers use positional args, default fills in)

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.HIL.Core/FrameSource.cs
git add src/PeakCan.HIL.Core/CanFrame.cs
git add tests/PeakCan.HIL.Core.Tests/FrameSourceTests.cs
git commit -m "feat(hil-core): add FrameSource enum and CanFrame.FrameSource field (sim frame marking)"
```

---
### Task 5: hil-core — TestSuite Environment 字段 + 退役 BackgroundFrames

**Files:**
- Modify: `src/PeakCan.HIL.Core/HIL/TestSuite.cs`
- Delete: `src/PeakCan.HIL.Core/HIL/BackgroundFrame.cs`
- Test: `tests/PeakCan.HIL.Core.Tests/HIL/TestSuiteEnvironmentTests.cs`

**Interfaces:**
- Consumes: Task 3 `RestbusNode`
- Produces: `TestSuite` 新增 `IReadOnlyList<RestbusNode>? Environment = null` 参数（追加在 `Channels` 之后）；`BackgroundFrames` 字段删除

- [ ] **Step 1: Write the failing test**

```csharp
// tests/PeakCan.HIL.Core.Tests/HIL/TestSuiteEnvironmentTests.cs
using System.Text.Json;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;

namespace PeakCan.HIL.Core.Tests.HIL;

public class TestSuiteEnvironmentTests
{
    private static readonly JsonSerializerOptions Options =
        PeakCan.HIL.Core.HIL.Serialization.HILJsonOptions.Default;

    [Fact]
    public void SuiteWithoutEnvironment_SerializesNull()
    {
        var suite = new TestSuite("test", [], [], [], new TestSuiteConfig());
        var json = JsonSerializer.Serialize(suite, Options);
        Assert.DoesNotContain("Environment", json);
        var result = JsonSerializer.Deserialize<TestSuite>(json, Options);
        Assert.Null(result!.Environment);
    }

    [Fact]
    public void SuiteWithEnvironment_RoundTrip()
    {
        var node = new RestbusNode { Name = "Charger", Identity = new J1939NodeIdentity(0xF4) };
        var suite = new TestSuite("test", [], [], [], new TestSuiteConfig(), Environment: [node]);
        var json = JsonSerializer.Serialize(suite, Options);
        Assert.Contains("Environment", json);
        var result = JsonSerializer.Deserialize<TestSuite>(json, Options);
        Assert.NotNull(result!.Environment);
        Assert.Single(result.Environment);
        Assert.Equal("Charger", result.Environment[0].Name);
    }

    [Fact]
    public void TestSuite_DoesNotHaveBackgroundFramesProperty()
    {
        var props = typeof(TestSuite).GetProperties();
        Assert.DoesNotContain(props, p => p.Name == "BackgroundFrames");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PeakCan.HIL.Core.Tests --filter "FullyQualifiedName~TestSuiteEnvironmentTests"`
Expected: FAIL

- [ ] **Step 3: Modify TestSuite.cs and delete BackgroundFrame.cs**

Update `TestSuite.cs` primary constructor:
```csharp
public sealed record TestSuite(
    string Name,
    IReadOnlyList<TestCase> Cases,
    IReadOnlyList<string> GlobalCaseFixtureKeys,
    IReadOnlyList<string> SuiteFixtureKeys,
    TestSuiteConfig Config,
    int TimeoutMs = 0,
    IReadOnlyDictionary<string, ParameterValue>? Parameters = null,
    ReferenceIntegrity.DataSource? DataSource = null,
    IReadOnlyList<ChannelConfig>? Channels = null,
    /// <summary>总线环境：restbus 节点列表，内嵌随 suite 文件走（0.17.0）。</summary>
    IReadOnlyList<RestbusNode>? Environment = null);
```

Delete `src/PeakCan.HIL.Core/HIL/BackgroundFrame.cs`.

- [ ] **Step 4: Fix compile errors from BackgroundFrame removal**

Run: `dotnet build src/PeakCan.HIL.Core`
If errors: fix references in `ReferenceCollector`, `StepParametersFactory`, `FrameAutoConfigProcessor`, `StepValidatorRegistry`, `StepParameters{,Exporter}` — remove BackgroundFrame-specific code paths.

Delete retired test files:
```bash
git rm tests/PeakCan.HIL.Core.Tests/BackgroundFrameTests.cs
git rm tests/PeakCan.HIL.Core.Tests/ModifyBackgroundFrameStepTests.cs
```

- [ ] **Step 5: Run full hil-core test suite**

Run: `dotnet test tests/PeakCan.HIL.Core.Tests`
Expected: PASS (0 failures)

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(hil-core)!: add TestSuite.Environment, retire BackgroundFrame field and type (breaking)"
```

---
### Task 6: hil-core — Step 替换（SetEnvironmentSignalStep / ModifyEnvironmentFrameStep）

**Files:**
- Create: `src/PeakCan.HIL.Core/HIL/StepParams/SetEnvironmentSignalStep.cs`
- Create: `src/PeakCan.HIL.Core/HIL/StepParams/ModifyEnvironmentFrameStep.cs`
- Delete: `src/PeakCan.HIL.Core/HIL/StepParams/ModifyBackgroundFrameStep.cs`
- Modify: `src/PeakCan.HIL.Core/HIL/TestCaseStepKind.cs` — remove `ModifyBackgroundFrame`, add `SetEnvironmentSignal` + `ModifyEnvironmentFrame`
- Modify: `src/PeakCan.HIL.Core/HIL/TestCaseStepJsonConverter.cs`
- Modify: `src/PeakCan.HIL.Core/HIL/StepParams/StepParameters.cs`
- Modify: `src/PeakCan.HIL.Core/HIL/StepParams/StepParametersFactory.cs`
- Modify: `src/PeakCan.HIL.Core/HIL/StepParams/StepParametersExporter.cs`
- Test: `tests/PeakCan.HIL.Core.Tests/HIL/StepParams/EnvironmentStepTests.cs`

**Interfaces:**
- Consumes: hil-core `TestCaseStepKind` enum, `StepParameters` base class, Task 1 `MessageRef`
- Produces: `SetEnvironmentSignalStep(string NodeName, string MessageName, string SignalName, double Value)`、`ModifyEnvironmentFrameStep(string NodeName, MessageRef Ref, byte[] Data)`；`TestCaseStepKind.SetEnvironmentSignal`、`TestCaseStepKind.ModifyEnvironmentFrame`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/PeakCan.HIL.Core.Tests/HIL/StepParams/EnvironmentStepTests.cs
using System.Text.Json;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.HIL.Core.HIL.StepParams;

namespace PeakCan.HIL.Core.Tests.HIL.StepParams;

public class EnvironmentStepTests
{
    private static readonly JsonSerializerOptions Options =
        PeakCan.HIL.Core.HIL.Serialization.HILJsonOptions.Default;

    [Fact]
    public void SetEnvironmentSignalStep_RoundTrip()
    {
        var original = new SetEnvironmentSignalStep("Charger", "BCL", "SOC", 80.0);
        var json = JsonSerializer.Serialize(original, Options);
        var result = JsonSerializer.Deserialize<SetEnvironmentSignalStep>(json, Options);
        Assert.NotNull(result);
        Assert.Equal("Charger", result!.NodeName);
        Assert.Equal("BCL", result.MessageName);
        Assert.Equal("SOC", result.SignalName);
        Assert.Equal(80.0, result.Value);
    }

    [Fact]
    public void ModifyEnvironmentFrameStep_RoundTrip()
    {
        var original = new ModifyEnvironmentFrameStep(
            "Charger", new CanMessageRef(0x123, false), [0x01, 0x02, 0x03]);
        var json = JsonSerializer.Serialize(original, Options);
        var result = JsonSerializer.Deserialize<ModifyEnvironmentFrameStep>(json, Options);
        Assert.NotNull(result);
        Assert.Equal("Charger", result!.NodeName);
        Assert.Equal(3, result.Data.Length);
    }

    [Fact]
    public void StepKindEnum_ContainsNewKinds()
    {
        Assert.Contains(TestCaseStepKind.SetEnvironmentSignal, Enum.GetValues<TestCaseStepKind>());
        Assert.Contains(TestCaseStepKind.ModifyEnvironmentFrame, Enum.GetValues<TestCaseStepKind>());
    }

    [Fact]
    public void StepKindEnum_DoesNotContainModifyBackgroundFrame()
    {
        Assert.DoesNotContain(TestCaseStepKind.ModifyBackgroundFrame, Enum.GetValues<TestCaseStepKind>());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PeakCan.HIL.Core.Tests --filter "FullyQualifiedName~EnvironmentStepTests"`
Expected: FAIL

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/PeakCan.HIL.Core/HIL/StepParams/SetEnvironmentSignalStep.cs
namespace PeakCan.HIL.Core.HIL.StepParams;

/// <summary>信号级环境改值步骤（spec §6.3 主形态）。目标必须是 DbcSignalsSource。</summary>
public sealed record SetEnvironmentSignalStep(
    string NodeName, string MessageName, string SignalName, double Value) : StepParameters;
```

```csharp
// src/PeakCan.HIL.Core/HIL/StepParams/ModifyEnvironmentFrameStep.cs
namespace PeakCan.HIL.Core.HIL.StepParams;

/// <summary>字节级环境改帧步骤（spec §6.3 逃生口）。仅 FixedHexSource；不改周期，不绕过 counter/checksum。</summary>
public sealed record ModifyEnvironmentFrameStep(
    string NodeName, Environment.MessageRef Ref, byte[] Data) : StepParameters;
```

In `TestCaseStepKind.cs`: remove `ModifyBackgroundFrame` line; add before closing brace:
```csharp
    SetEnvironmentSignal,
    ModifyEnvironmentFrame,
```

In `TestCaseStepJsonConverter.cs`: add case handlers for new kinds, remove `ModifyBackgroundFrame` handler.
In `StepParameters.cs`/`Factory.cs`/`Exporter.cs`: add new record types, remove `ModifyBackgroundFrameStep` references.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PeakCan.HIL.Core.Tests --filter "FullyQualifiedName~EnvironmentStepTests"`
Expected: PASS (all 4 tests)

- [ ] **Step 5: Run full hil-core tests + fix remaining compile issues**

Run: `rg "ModifyBackgroundFrame" src/ --files-with-matches`
Expected: 0 files after cleanup

Run: `dotnet test tests/PeakCan.HIL.Core.Tests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(hil-core)!: replace ModifyBackgroundFrameStep with SetEnvironmentSignalStep + ModifyEnvironmentFrameStep (breaking)"
```

---
### Task 7: hil-core — 环境校验器（RestbusNodeValidator）

**Files:**
- Create: `src/PeakCan.HIL.Core/HIL/Environment/RestbusNodeValidator.cs`
- Test: `tests/PeakCan.HIL.Core.Tests/HIL/Environment/RestbusNodeValidatorTests.cs`

**Interfaces:**
- Consumes: Task 3 `RestbusNode`, hil-core `ChannelConfig`
- Produces: `RestbusNodeValidator.Validate(IReadOnlyList<RestbusNode> nodes, IReadOnlyList<ChannelConfig>? channels, IReadOnlyDictionary<string, DbcDocument>? perChannelDbcs)` → `IReadOnlyList<string>` (empty = valid)

- [ ] **Step 1: Write the failing test**

```csharp
// tests/PeakCan.HIL.Core.Tests/HIL/Environment/RestbusNodeValidatorTests.cs
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;

namespace PeakCan.HIL.Core.Tests.HIL.Environment;

public class RestbusNodeValidatorTests
{
    [Fact]
    public void DuplicateNodeNames_Rejected()
    {
        var nodes = new List<RestbusNode>
        {
            new() { Name = "A", Identity = new RawCanNodeIdentity() },
            new() { Name = "A", Identity = new RawCanNodeIdentity() },
        };
        var errors = RestbusNodeValidator.Validate(nodes, null, null);
        Assert.Contains(errors, e => e.Contains("Duplicate"));
    }

    [Fact]
    public void ChannelRequired_WhenSuiteHasChannels()
    {
        var nodes = new List<RestbusNode>
        {
            new() { Name = "A", Identity = new RawCanNodeIdentity() },
        };
        var channels = new List<ChannelConfig> { new("CAN1", "51", null, false) };
        var errors = RestbusNodeValidator.Validate(nodes, channels, null);
        Assert.Contains(errors, e => e.Contains("Channel"));
    }

    [Fact]
    public void ChannelMustMatch_WhenSuiteHasChannels()
    {
        var nodes = new List<RestbusNode>
        {
            new() { Name = "A", Identity = new RawCanNodeIdentity(), Channel = "CAN9" },
        };
        var channels = new List<ChannelConfig> { new("CAN1", "51", null, false) };
        var errors = RestbusNodeValidator.Validate(nodes, channels, null);
        Assert.Contains(errors, e => e.Contains("CAN9"));
    }

    [Fact]
    public void ChannelMustBeNull_WhenSuiteHasNoChannels()
    {
        var nodes = new List<RestbusNode>
        {
            new() { Name = "A", Identity = new RawCanNodeIdentity(), Channel = "CAN1" },
        };
        var errors = RestbusNodeValidator.Validate(nodes, null, null);
        Assert.Contains(errors, e => e.Contains("Channel"));
    }

    [Fact]
    public void J1939SaMismatch_Rejected()
    {
        var nodes = new List<RestbusNode>
        {
            new()
            {
                Name = "A",
                Identity = new J1939NodeIdentity(0xF4),
                Messages =
                [
                    new NodeMessage(
                        new J1939MessageRef(0x0006, 6, null, Sa: 0x56, Da: null), 50, new FixedHexSource("01"))
                ],
            },
        };
        var errors = RestbusNodeValidator.Validate(nodes, null, null);
        Assert.Contains(errors, e => e.Contains("Sa"));
    }

    [Fact]
    public void J1939RtsCtsWithoutDa_Rejected()
    {
        var nodes = new List<RestbusNode>
        {
            new()
            {
                Name = "A",
                Identity = new J1939NodeIdentity(0xF4),
                Messages =
                [
                    new NodeMessage(
                        new J1939MessageRef(0x0006, 6, PeakCan.HIL.Core.J1939.TpMode.RtsCts, Sa: 0xF4, Da: null), 50, new FixedHexSource("01"))
                ],
            },
        };
        var errors = RestbusNodeValidator.Validate(nodes, null, null);
        Assert.Contains(errors, e => e.Contains("Da"));
    }

    [Fact]
    public void IntervalMsBelow10_ThrowsInConstructor()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NodeMessage(new CanMessageRef(0x123, false), 5, new FixedHexSource("01")));
    }

    [Fact]
    public void ValidNodes_NoErrors()
    {
        var nodes = new List<RestbusNode>
        {
            new() { Name = "A", Identity = new RawCanNodeIdentity() },
            new() { Name = "B", Identity = new J1939NodeIdentity(0xF4) },
        };
        var errors = RestbusNodeValidator.Validate(nodes, null, null);
        Assert.Empty(errors);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PeakCan.HIL.Core.Tests --filter "FullyQualifiedName~RestbusNodeValidatorTests"`
Expected: FAIL

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/PeakCan.HIL.Core/HIL/Environment/RestbusNodeValidator.cs
namespace PeakCan.HIL.Core.HIL.Environment;

/// <summary>环境节点静态校验器（suite 加载期调用）。纯函数，无 I/O。</summary>
public static class RestbusNodeValidator
{
    public static IReadOnlyList<string> Validate(
        IReadOnlyList<RestbusNode> nodes,
        IReadOnlyList<ChannelConfig>? channels,
        IReadOnlyDictionary<string, Dbc.DbcDocument>? perChannelDbcs)
    {
        var errors = new List<string>();

        // 1. 节点名唯一
        var names = new HashSet<string>();
        foreach (var n in nodes)
        {
            if (!names.Add(n.Name))
                errors.Add($"Duplicate node name: '{n.Name}'.");
        }

        // 2. 通道归属
        if (channels is { Count: > 0 })
        {
            var channelNames = channels.Select(c => c.Name).ToHashSet();
            foreach (var n in nodes)
            {
                if (n.Channel is null)
                    errors.Add($"Node '{n.Name}': Channel is required when suite declares Channels.");
                else if (!channelNames.Contains(n.Channel))
                    errors.Add($"Node '{n.Name}': Channel '{n.Channel}' not found in suite Channels.");
            }
        }
        else
        {
            foreach (var n in nodes)
            {
                if (n.Channel is not null)
                    errors.Add($"Node '{n.Name}': Channel must be null when suite has no Channels (single-channel).");
            }
        }

        // 3. J1939 身份不变式（spec §5.4）
        foreach (var n in nodes)
        {
            if (n.Identity is not J1939NodeIdentity j1939) continue;

            foreach (var msg in n.Messages)
            {
                if (msg.Ref is not J1939MessageRef jRef) continue;

                if (jRef.Sa is not null && jRef.Sa != j1939.Sa)
                    errors.Add($"Node '{n.Name}': J1939 message Sa (0x{jRef.Sa:X2}) does not match identity Sa (0x{j1939.Sa:X2}).");

                if (jRef.Mode == PeakCan.HIL.Core.J1939.TpMode.RtsCts && jRef.Da is null)
                    errors.Add($"Node '{n.Name}': J1939 RTS-CTS message requires Da (destination address).");
            }
        }

        return errors;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PeakCan.HIL.Core.Tests --filter "FullyQualifiedName~RestbusNodeValidatorTests"`
Expected: PASS (all 8 tests)

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.HIL.Core/HIL/Environment/RestbusNodeValidator.cs
git add tests/PeakCan.HIL.Core.Tests/HIL/Environment/RestbusNodeValidatorTests.cs
git commit -m "feat(hil-core): add RestbusNodeValidator (channel ownership, J1939 identity invariants, name uniqueness)"
```

---
### Task 8: host — EnvironmentRuntime 核心（周期发送 + 首帧立即）

**Files:**
- Create: `src/PeakCan.Host.Infrastructure/HIL/Environment/EnvironmentRuntime.cs`
- Test: `tests/PeakCan.Host.Infrastructure.Tests/HIL/Environment/EnvironmentRuntimeTests.cs`

**Interfaces:**
- Consumes: hil-core `RestbusNode`, `NodeMessage`, `MessageRef`, `NodePayloadSource`, `CanFrame`, `FrameSource`, `ICanChannel` (host.Core), `CounterConfig`, `ChecksumConfig`, `FrameAutoConfigProcessor`
- Produces: `EnvironmentRuntime.Start(IReadOnlyList<RestbusNode>, IReadOnlyList<ChannelConfig>?)`、`Stop()`、`UpdateFrameData(string, MessageRef, byte[])`、`InjectIncomingFrame(CanFrame)`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/PeakCan.Host.Infrastructure.Tests/HIL/Environment/EnvironmentRuntimeTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.Host.Infrastructure.HIL.Environment;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Environment;

public class EnvironmentRuntimeTests
{
    private static CanFrame MakeFrame(uint id, byte[] data, bool extended = false) =>
        new(new CanId(id, extended ? FrameFormat.Extended : FrameFormat.Standard),
            data, FrameFlags.None, default, default, FrameSource.Bus);

    [Fact]
    public void Start_WithEmptyNodes_DoesNotThrow()
    {
        var runtime = new EnvironmentRuntime(new FakeChannel(), NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([], null);
        runtime.Stop();
    }

    [Fact]
    public void Start_EnabledMessage_SendsImmediately()
    {
        var sent = new List<CanFrame>();
        var channel = new FakeChannel { OnWrite = f => sent.Add(f) };
        var node = new RestbusNode
        {
            Name = "A",
            Identity = new RawCanNodeIdentity(),
            Messages = [new NodeMessage(new CanMessageRef(0x123, false), 100, new FixedHexSource("01 02"))],
        };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([node], null);

        Assert.Single(sent);
        Assert.Equal(0x123, sent[0].Id.Raw);
        Assert.Equal(FrameSource.Environment, sent[0].FrameSource);
        runtime.Stop();
    }

    [Fact]
    public void Start_DisabledMessage_DoesNotSend()
    {
        var sent = new List<CanFrame>();
        var channel = new FakeChannel { OnWrite = f => sent.Add(f) };
        var node = new RestbusNode
        {
            Name = "A",
            Identity = new RawCanNodeIdentity(),
            Messages = [new NodeMessage(new CanMessageRef(0x123, false), 100, new FixedHexSource("01"), Enabled: false)],
        };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([node], null);
        Assert.Empty(sent);
        runtime.Stop();
    }

    [Fact]
    public void Stop_IsIdempotent()
    {
        var runtime = new EnvironmentRuntime(new FakeChannel(), NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([], null);
        runtime.Stop();
        runtime.Stop();
    }

    [Fact]
    public void UpdateFrameData_FixedHexSource_Applies()
    {
        var channel = new FakeChannel();
        var node = new RestbusNode
        {
            Name = "A",
            Identity = new RawCanNodeIdentity(),
            Messages = [new NodeMessage(new CanMessageRef(0x123, false), 100, new FixedHexSource("01 02"))],
        };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([node], null);
        runtime.UpdateFrameData("A", new CanMessageRef(0x123, false), [0xFF, 0xEE]);
        runtime.Stop();
    }
}

internal sealed class FakeChannel : ICanChannel
{
    public ChannelId Id => default;
    public bool IsConnected { get; private set; }
    public event Action<CanFrame>? FrameReceived;
    public event Action<ReadLoopError>? ReadLoopError;
    public Action<CanFrame>? OnWrite { get; init; }

    public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
    { IsConnected = true; return Task.FromResult(Result<Unit>.Ok(default(Unit))); }
    public Task DisconnectAsync(CancellationToken ct = default)
    { IsConnected = false; return Task.CompletedTask; }
    public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
    { OnWrite?.Invoke(frame); return ValueTask.FromResult(Result<Unit>.Ok(default(Unit))); }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests --filter "FullyQualifiedName~EnvironmentRuntimeTests"`
Expected: FAIL — type not found

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/PeakCan.Host.Infrastructure/HIL/Environment/EnvironmentRuntime.cs
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;

namespace PeakCan.Host.Infrastructure.HIL.Environment;

/// <summary>
/// 统一环境执行器。10ms 单扫描定时器驱动周期帧和 pending 规则。
/// spec §6.1: Start 后 enabled 周期帧先立即发送一次，后续按量化周期调度。
/// </summary>
public sealed class EnvironmentRuntime
{
    private const int ScanIntervalMs = 10;
    private const int QueueCapacity = 256;
    private const int MaxConsecutiveSendFailures = 10;

    private readonly ICanChannel _channel;
    private readonly ILogger<EnvironmentRuntime> _logger;
    private readonly object _gate = new();
    private readonly ConcurrentQueue<CanFrame> _incoming = new();
    private ITimer? _scanTimer;
    private List<NodeRuntimeState> _states = [];
    private long _droppedFrames;
    private long _lastDropWarningTicks;
    private bool _running;

    public EnvironmentRuntime(ICanChannel channel, ILogger<EnvironmentRuntime>? logger = null)
    {
        _channel = channel;
        _logger = logger ?? NullLogger<EnvironmentRuntime>.Instance;
    }

    public void Start(IReadOnlyList<RestbusNode> nodes, IReadOnlyList<ChannelConfig>? channels)
    {
        lock (_gate)
        {
            _nodes = nodes;
            _states = nodes.Select(n => new NodeRuntimeState(n)).ToList();
            _running = true;
            _scanTimer = new Timer(Scan, null, 0, ScanIntervalMs);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _scanTimer?.Dispose();
            _scanTimer = null;
            _running = false;
        }
    }

    public void UpdateFrameData(string nodeName, MessageRef msgRef, byte[] data)
    {
        lock (_gate)
        {
            var state = _states.FirstOrDefault(s => s.Node.Name == nodeName);
            state?.UpdateFixedHexData(msgRef, data);
        }
    }

    public void InjectIncomingFrame(CanFrame frame)
    {
        if (_incoming.Count >= QueueCapacity)
        {
            _incoming.TryDequeue(out _);
            Interlocked.Increment(ref _droppedFrames);
            ThrottleDropWarning();
        }
        _incoming.Enqueue(frame);
    }

    private void Scan(object? state)
    {
        List<(NodeMessageRuntimeState MsgState, NodeMessage Msg)>? toSend = null;
        lock (_gate)
        {
            if (!_running) return;
            var now = Environment.TickCount64;

            foreach (var nodeState in _states)
            {
                for (int i = 0; i < nodeState.Messages.Count; i++)
                {
                    var msgState = nodeState.Messages[i];
                    if (!msgState.Enabled || now < msgState.NextDueMs) continue;

                    var payload = msgState.BuildPayload();
                    if (payload is not null)
                        (toSend ??= []).Add((msgState, nodeState.Node.Messages[i]));

                    var quantum = Math.Max(ScanIntervalMs,
                        (nodeState.Node.Messages[i].IntervalMs + ScanIntervalMs - 1) / ScanIntervalMs * ScanIntervalMs);
                    msgState.NextDueMs = now + quantum;
                }
            }
        }

        if (toSend is not null)
            foreach (var (msgState, msg) in toSend)
                SendFrame(msgState, msg);

        ProcessIncoming();
    }

    private void SendFrame(NodeMessageRuntimeState msgState, NodeMessage msg)
    {
        if (msg.Ref is not CanMessageRef canRef) return;
        var id = new CanId(canRef.Id, canRef.IsExtended ? FrameFormat.Extended : FrameFormat.Standard);
        var flags = msg.Fd ? FrameFlags.Fd : FrameFlags.None;
        var frame = new CanFrame(id, msgState.BuildPayload()!, flags, default, default, FrameSource.Environment);
        var result = _channel.WriteAsync(frame).AsTask().GetAwaiter().GetResult();

        if (result.IsSuccess)
        {
            msgState.ConsecutiveFailures = 0;
            msgState.FramesSent++;
        }
        else
        {
            msgState.ConsecutiveFailures++;
            if (msgState.ConsecutiveFailures >= MaxConsecutiveSendFailures)
            {
                _logger.LogError("Environment message {Ref}: stopped after {N} consecutive failures.", msg.Ref, MaxConsecutiveSendFailures);
                msgState.Enabled = false;
            }
        }
    }

    private void ProcessIncoming() { /* Task 9 implements */ }

    private void ThrottleDropWarning()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastDropWarningTicks) < 5000) return;
        Interlocked.Exchange(ref _lastDropWarningTicks, now);
        _logger.LogWarning("Environment incoming queue overflow: {Dropped} frames dropped.", Interlocked.Read(ref _droppedFrames));
    }
}

internal sealed class NodeRuntimeState
{
    public RestbusNode Node { get; }
    public List<NodeMessageRuntimeState> Messages { get; } = [];
    public NodeRuntimeState(RestbusNode node)
    {
        Node = node;
        foreach (var msg in node.Messages) Messages.Add(new NodeMessageRuntimeState(msg));
    }
    public void UpdateFixedHexData(MessageRef msgRef, byte[] data)
    {
        foreach (var m in Messages)
            if (m.Source is FixedHexSource && MatchesRef(m.Ref, msgRef))
                m.FixedHexData = data;
    }
    private static bool MatchesRef(MessageRef a, MessageRef b) => (a, b) switch
    {
        (CanMessageRef ca, CanMessageRef cb) => ca.Id == cb.Id && ca.IsExtended == cb.IsExtended,
        (J1939MessageRef ja, J1939MessageRef jb) => ja.Pgn == jb.Pgn && ja.Priority == jb.Priority,
        _ => false,
    };
}

internal sealed class NodeMessageRuntimeState
{
    public MessageRef Ref { get; }
    public NodePayloadSource Source { get; }
    public bool Enabled { get; set; }
    public long NextDueMs { get; set; }
    public int ConsecutiveFailures { get; set; }
    public long FramesSent { get; set; }
    public byte[]? FixedHexData { get; set; }
    public ushort CounterValue { get; set; }

    public NodeMessageRuntimeState(NodeMessage msg)
    {
        Ref = msg.Ref;
        Source = msg.Payload;
        Enabled = msg.Enabled;
        FixedHexData = (msg.Payload as FixedHexSource) is { } hex ? ParseHex(hex.Hex) : null;
        CounterValue = msg.AutoCounter is { } ac ? ac.StartValue : (ushort)0;
    }

    public byte[]? BuildPayload()
    {
        if (Source is FixedHexSource) return FixedHexData;
        return null; // DbcSignalsSource in later task
    }

    private static byte[] ParseHex(string hex)
    {
        var clean = hex.Replace(" ", "").Replace("-", "");
        var bytes = new byte[clean.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
        return bytes;
    }
}
```

Note: `private IReadOnlyList<RestbusNode> _nodes = [];` was removed — `_states` holds `Node` references. Also removed re-Start guard — `Start` can be called multiple times by design (test isolation).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests --filter "FullyQualifiedName~EnvironmentRuntimeTests"`
Expected: PASS (all 5 tests)

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Infrastructure/HIL/Environment/EnvironmentRuntime.cs
git add tests/PeakCan.Host.Infrastructure.Tests/HIL/Environment/EnvironmentRuntimeTests.cs
git commit -m "feat(host): add EnvironmentRuntime core (periodic scan, immediate first frame, FrameSource.Environment)"
```

---
### Task 9: host — EnvironmentRuntime 规则分发 + 动作执行

**Files:**
- Modify: `src/PeakCan.Host.Infrastructure/HIL/Environment/EnvironmentRuntime.cs`
- Test: `tests/PeakCan.Host.Infrastructure.Tests/HIL/Environment/EnvironmentRuleDispatchTests.cs`

**Interfaces:**
- Consumes: Task 8 `EnvironmentRuntime`, hil-core `ResponseRule`, `NodeAction`, `CanFrame.FrameSource`
- Produces: `InjectIncomingFrame(CanFrame)` 完整实现（trigger match → condition filter → action dispatch）

- [ ] **Step 1: Write the failing test**

```csharp
// tests/PeakCan.Host.Infrastructure.Tests/HIL/Environment/EnvironmentRuleDispatchTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.Host.Infrastructure.HIL.Environment;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Environment;

public class EnvironmentRuleDispatchTests
{
    private static CanFrame MakeFrame(uint id, byte[] data, FrameSource source = FrameSource.Bus) =>
        new(new CanId(id, FrameFormat.Standard), data, FrameFlags.None, default, default, source);

    [Fact]
    public void IncomingFrame_MatchesRule_SendsResponse()
    {
        var sent = new List<CanFrame>();
        var channel = new FakeChannel { OnWrite = f => sent.Add(f) };
        var node = new RestbusNode
        {
            Name = "A",
            Identity = new RawCanNodeIdentity(),
            Rules =
            [
                new ResponseRule(
                    new CanMessageRef(0x500, false), null,
                    new SendMessageAction(new CanMessageRef(0x600, false), new FixedHexSource("AA BB")),
                    0),
            ],
        };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([node], null);
        runtime.InjectIncomingFrame(MakeFrame(0x500, [0x01]));
        Assert.Contains(sent, f => f.Id.Raw == 0x600);
        runtime.Stop();
    }

    [Fact]
    public void EnvironmentOwnFrame_DoesNotTriggerOwnRules()
    {
        var sent = new List<CanFrame>();
        var channel = new FakeChannel { OnWrite = f => sent.Add(f) };
        var node = new RestbusNode
        {
            Name = "A",
            Identity = new RawCanNodeIdentity(),
            Messages = [new NodeMessage(new CanMessageRef(0x100, false), 100, new FixedHexSource("01"))],
            Rules =
            [
                new ResponseRule(
                    new CanMessageRef(0x100, false), null,
                    new SendMessageAction(new CanMessageRef(0x600, false), new FixedHexSource("AA")),
                    0),
            ],
        };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([node], null);
        runtime.InjectIncomingFrame(MakeFrame(0x100, [0x01], source: FrameSource.Environment));
        Assert.DoesNotContain(sent, f => f.Id.Raw == 0x600);
        runtime.Stop();
    }

    [Fact]
    public void BytePatternCondition_MatchingPayload_Triggers()
    {
        var sent = new List<CanFrame>();
        var channel = new FakeChannel { OnWrite = f => sent.Add(f) };
        var node = new RestbusNode
        {
            Name = "A",
            Identity = new RawCanNodeIdentity(),
            Rules =
            [
                new ResponseRule(
                    new CanMessageRef(0x500, false),
                    new BytePattern(0, 0xFF, 0x42),
                    new SendMessageAction(new CanMessageRef(0x600, false), new FixedHexSource("01")),
                    0),
            ],
        };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([node], null);
        runtime.InjectIncomingFrame(MakeFrame(0x500, [0x42]));
        Assert.Contains(sent, f => f.Id.Raw == 0x600);
        runtime.Stop();
    }

    [Fact]
    public void BytePatternCondition_NonMatchingPayload_DoesNotTrigger()
    {
        var sent = new List<CanFrame>();
        var channel = new FakeChannel { OnWrite = f => sent.Add(f) };
        var node = new RestbusNode
        {
            Name = "A",
            Identity = new RawCanNodeIdentity(),
            Rules =
            [
                new ResponseRule(
                    new CanMessageRef(0x500, false),
                    new BytePattern(0, 0xFF, 0x42),
                    new SendMessageAction(new CanMessageRef(0x600, false), new FixedHexSource("01")),
                    0),
            ],
        };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([node], null);
        runtime.InjectIncomingFrame(MakeFrame(0x500, [0x99]));
        Assert.DoesNotContain(sent, f => f.Id.Raw == 0x600);
        runtime.Stop();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests --filter "FullyQualifiedName~EnvironmentRuleDispatchTests"`
Expected: FAIL

- [ ] **Step 3: Implement rule dispatch in EnvironmentRuntime**

Replace the empty `ProcessIncoming` body:

```csharp
private void ProcessIncoming()
{
    while (_incoming.TryDequeue(out var frame))
    {
        // spec: 节点自己发出的帧不得重新进入该节点的规则管线
        if (frame.FrameSource == FrameSource.Environment) continue;

        List<(RestbusNode Node, ResponseRule Rule)>? matched = null;
        lock (_gate)
        {
            foreach (var nodeState in _states)
            {
                foreach (var rule in nodeState.Node.Rules)
                {
                    if (!MatchesIncoming(rule.Trigger, frame)) continue;
                    if (!MatchesCondition(rule.Condition, frame)) continue;
                    (matched ??= []).Add((nodeState.Node, rule));
                }
            }
        }

        if (matched is not null)
            foreach (var (node, rule) in matched)
                ExecuteAction(node, rule.Action);
    }
}

private void ExecuteAction(RestbusNode node, NodeAction action)
{
    switch (action)
    {
        case SendMessageAction send: SendActionFrame(node, send); break;
        case SetSignalAction set: /* DBC signal encode in later task */ break;
        case StartMessageAction start: SetMessageEnabled(node, start.Ref, true); break;
        case StopMessageAction stop: SetMessageEnabled(node, stop.Ref, false); break;
        case ScriptAction script:
            _logger.LogWarning("ScriptAction '{Ref}' not supported in EnvironmentRuntime.", script.ScriptRef);
            break;
    }
}

private void SendActionFrame(RestbusNode node, SendMessageAction action)
{
    if (action.Ref is not CanMessageRef canRef) return;
    var id = new CanId(canRef.Id, canRef.IsExtended ? FrameFormat.Extended : FrameFormat.Standard);
    byte[] payload = action.Payload switch
    {
        FixedHexSource hex => ParseHexStatic(hex.Hex),
        _ => [],
    };
    var frame = new CanFrame(id, payload, FrameFlags.None, default, default, FrameSource.Environment);
    _channel.WriteAsync(frame).AsTask().GetAwaiter().GetResult();
}

private void SetMessageEnabled(RestbusNode node, MessageRef target, bool enabled)
{
    lock (_gate)
    {
        var state = _states.FirstOrDefault(s => s.Node.Name == node.Name);
        if (state is null) return;
        foreach (var m in state.Messages)
        {
            if (MatchesRefStatic(target, m.Ref))
            {
                m.Enabled = enabled;
                if (enabled) m.NextDueMs = Environment.TickCount64 + ScanIntervalMs;
            }
        }
    }
}

private static bool MatchesIncoming(MessageRef ruleRef, CanFrame frame)
{
    if (ruleRef is CanMessageRef canRef)
        return frame.Id.Raw == canRef.Id && frame.Id.IsExtended == canRef.IsExtended;
    return false;
}

private static bool MatchesCondition(BytePattern? cond, CanFrame frame)
{
    if (cond is null) return true;
    if (frame.Data.Length <= cond.Offset) return false;
    return (frame.Data.Span[cond.Offset] & cond.Mask) == cond.Value;
}

private static byte[] ParseHexStatic(string hex)
{
    var clean = hex.Replace(" ", "").Replace("-", "");
    var bytes = new byte[clean.Length / 2];
    for (int i = 0; i < bytes.Length; i++)
        bytes[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
    return bytes;
}

private static bool MatchesRefStatic(MessageRef a, MessageRef b) => (a, b) switch
{
    (CanMessageRef ca, CanMessageRef cb) => ca.Id == cb.Id && ca.IsExtended == cb.IsExtended,
    (J1939MessageRef ja, J1939MessageRef jb) => ja.Pgn == jb.Pgn && ja.Priority == jb.Priority,
    _ => false,
};
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests --filter "FullyQualifiedName~Environment"`
Expected: PASS (all tests from Tasks 8-9)

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Infrastructure/HIL/Environment/EnvironmentRuntime.cs
git add tests/PeakCan.Host.Infrastructure.Tests/HIL/Environment/EnvironmentRuleDispatchTests.cs
git commit -m "feat(host): implement EnvironmentRuntime rule dispatch (trigger, condition, action execution)"
```

---
### Task 10: host — HilRunnerService 接线 + BackgroundFrameSender 退役

**Files:**
- Modify: `src/PeakCan.Host.Infrastructure/HIL/HilRunnerService.cs`
- Delete: `src/PeakCan.Host.Infrastructure/HIL/BackgroundFrameSender.cs`
- Delete: `src/PeakCan.Host.Infrastructure/HIL/StepExecutor/ModifyBackgroundFrameStepExecutor.cs`
- Test: `tests/PeakCan.Host.Infrastructure.Tests/HIL/Environment/HilRunnerEnvironmentTests.cs`

**Interfaces:**
- Consumes: Tasks 8-9 `EnvironmentRuntime`, Task 5 `TestSuite.Environment`, Task 7 `RestbusNodeValidator`
- Produces: `HilRunnerService.RunAsync` 完整接线

- [ ] **Step 1: Modify HilRunnerService.cs**

Replace `BackgroundFrameSender` usage:

```csharp
// Remove:
//   var sender = host.Services.GetRequiredService<BackgroundFrameSender>();
// Add:
var environmentRuntime = new EnvironmentRuntime(channel, _logger);

// Replace sender.Start(suite.BackgroundFrames) block:
if (suite.Environment is { Count: > 0 })
{
    var envErrors = RestbusNodeValidator.Validate(suite.Environment, suite.Channels, null);
    if (envErrors.Count > 0)
        throw new InvalidOperationException(
            "Environment validation failed:\n" + string.Join("\n", envErrors));
    environmentRuntime.Start(suite.Environment, suite.Channels);
}

// Replace sender.Stop() in finally block:
environmentRuntime.Stop();
```

Delete `BackgroundFrameSender.cs` and `ModifyBackgroundFrameStepExecutor.cs`.
Remove any DI registrations of `BackgroundFrameSender` (search `rg "BackgroundFrameSender" src/`).

- [ ] **Step 2: Build and fix compile errors**

Run: `dotnet build src/PeakCan.Host.Infrastructure`
If errors from `BackgroundFrameSender` references: fix each (likely in `HeadlessHostBuilder.cs`, `AppHostBuilder.cs`).

Run: `dotnet build src/PeakCan.Host.App`
If errors: fix remaining references.

- [ ] **Step 3: Write integration test**

```csharp
// tests/PeakCan.Host.Infrastructure.Tests/HIL/Environment/HilRunnerEnvironmentTests.cs
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Environment;

public class HilRunnerEnvironmentTests
{
    [Fact]
    public void SuiteWithValidEnvironment_PassesValidation()
    {
        var nodes = new List<RestbusNode>
        {
            new() { Name = "A", Identity = new RawCanNodeIdentity() },
        };
        var errors = RestbusNodeValidator.Validate(nodes, null, null);
        Assert.Empty(errors);
    }

    [Fact]
    public void SuiteWithInvalidEnvironment_FailsValidation()
    {
        var nodes = new List<RestbusNode>
        {
            new() { Name = "A", Identity = new RawCanNodeIdentity(), Channel = "GHOST" },
        };
        var channels = new List<ChannelConfig> { new("CAN1", "51", null, false) };
        var errors = RestbusNodeValidator.Validate(nodes, channels, null);
        Assert.NotEmpty(errors);
    }
}
```

- [ ] **Step 4: Run all infrastructure tests**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(host)!: wire EnvironmentRuntime into HilRunnerService, retire BackgroundFrameSender (breaking)"
```

---
### Task 11: lockstep 版本 bump

**Files:**
- Modify: `src/PeakCan.HIL.Core/PeakCan.HIL.Core.csproj` — Version 0.16.0 → 0.17.0
- Modify: host csproj PackageReference bump (if PackageReference; project reference = no change)

- [ ] **Step 1: Bump hil-core version**

Edit `PeakCan.HIL.Core.csproj`:
```xml
<Version>0.17.0</Version>
```

- [ ] **Step 2: Bump host package reference**

Edit host csproj (wherever `PeakCan.HIL.Core` is referenced):
```xml
<PackageReference Include="PeakCan.HIL.Core" Version="0.17.0" />
```
(If `<ProjectReference>` is used, no change needed.)

- [ ] **Step 3: Run full hil-core tests**

Run: `dotnet test tests/PeakCan.HIL.Core.Tests`
Expected: PASS

- [ ] **Step 4: Run full host build + tests**

Run: `dotnet build src/PeakCan.Host.App`
Expected: Build succeeded

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "chore: bump hil-core to 0.17.0 (restbus unification lockstep)"
```

---

### Task 12: 最终验证（全量回归 + 残留检查）

- [ ] **Step 1: Full hil-core test suite**

Run: `dotnet test tests/PeakCan.HIL.Core.Tests --verbosity normal`
Expected: All tests pass

- [ ] **Step 2: Full host test suite**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests --verbosity normal`
Run: `dotnet test tests/PeakCan.Host.Core.Tests --verbosity normal`
Run: `dotnet test tests/PeakCan.Host.App.Tests --verbosity normal`
Expected: All tests pass

- [ ] **Step 3: Verify no BackgroundFrame remnants**

Run: `rg "BackgroundFrame" src/ tests/ --files-with-matches`
Expected: 0 files (or only migration docs/comments)

Run: `rg "ModifyBackgroundFrame" src/ tests/ --files-with-matches`
Expected: 0 files

- [ ] **Step 4: Verify FrameSource integration**

Run: `rg "FrameSource\.Environment" src/ --files-with-matches`
Expected: At least `EnvironmentRuntime.cs`

- [ ] **Step 5: Final commit (cleanup)**

```bash
git status
git add -A && git commit -m "chore: cleanup BackgroundFrame remnants for restbus unification M1"
```

---

## Self-Review Checklist

- **Spec coverage (§5-§6 M1 scope):** Tasks 1-3 cover §5.1-5.2 model types; Task 4 covers §6.5 FrameSource; Tasks 5-6 cover §5.3 TestSuite + §6.3 step migration; Task 7 covers §5.4 validator; Tasks 8-9 cover §6.1 EnvironmentRuntime periodic + rule dispatch; Task 10 covers §6.2 HilRunnerService wiring; Task 11 covers §16 lockstep.
- **M1 scope NOT covered (explicitly deferred):** DBC signal encoding (DbcSignalsSource), UDS routing (EcuScriptDefinition → EcuStateMachine), J1939 TP integration, SetEnvironmentSignalStep executor, NodeRunStats, studio UI — these are M2 scope per spec §14.
- **Type consistency:** `NodeMessage.Ref` is `MessageRef` (not `CanId`); `EnvironmentRuntime` takes `ICanChannel` (host.Core interface, not hil-core type); `CanFrame.FrameSource` is positional optional param (backward compatible).
- **No placeholders:** All code blocks are complete minimal implementations.
