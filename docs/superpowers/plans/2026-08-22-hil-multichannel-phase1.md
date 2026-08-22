# HIL 多 CAN 通道支持（阶段一）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 一次 HIL run 内连接多路 CAN 通道，用例步骤可指定 `TargetChannel` 路由发送/监控到指定路，贯穿 case log 与报告的通道区分；旧单通道 suite 零回归。

**Architecture:** hil-core 包（0.11→0.12）加 `ChannelConfig` + 5 类步骤 `TargetChannel` + `StepResult.Channel`（dict→Factory 通道，Factory/Exporter 对称改）；host `PeakCan.Host.Core` 演进 `IAssertionContext`/`IFrameStatistics`/`IHasFrameSink` 多通道路由 + `PeakCanAssertionContext` 重构为 `SingleChannelContext`+`MultiChannelAssertionContext` 组合；host `PeakCan.Host.Infrastructure` 多 channel DI + per-channel DBC + `AscFileFormat` 带 channel 列 + `HtmlReportGenerator` 按通道 DBC 解码；studio step 编辑器透传 `TargetChannel`。

**Tech Stack:** .NET 10 / WPF / xUnit / FluentAssertions / System.Text.Json（polymorphic `[JsonDerivedType]`+dict→Factory 双通道）/ CommunityToolkit.Mvvm

**Spec:** [docs/superpowers/specs/2026-08-21-hil-multichannel-design.md](../specs/2026-08-21-hil-multichannel-design.md)

## Global Constraints

- **Spec 修正（实施前必读）**：spec §3.1 列的 6 类步骤含 `SendSequenceStep`，但 `SendSequenceStepExecutor` 是占位桩（`throw NotSupportedException`，`SendSequenceStep` 类型在包里不存在）。**MVP 实际 5 类步骤**：`SendFrame` / `ExpectFrame` / `AssertNoFrame` / `AssertFrameCount` / `AssertCycleTime`。本 plan 按此执行；实施首步更新 spec §3.1。
- **双仓库 bump**：hil-core 改动后，host + studio 两侧 `Directory.Packages.props` 必须同时 bump 到 `0.12.0` + restore，否则编译不过新 API（吸取 0.5.1 vs 0.6.0 pin 漂移教训）。
- **向后兼容（硬约束）**：所有新字段可空、缺省 = 单通道默认；旧 suite JSON（无 `Channels`/`TargetChannel`/`Channel`）反序列化 + 执行 + 报告行为**逐字节逐行为不变**。每个 hil-core 任务配套序列化兼容回归测试。
- **dict→Factory 对称**：`StepParametersFactory.Create`（读）与 `StepParametersExporter.FromParameters`（写）必须严格对称——Factory 缺键回落 null（兼容旧 JSON），Exporter null 值不写键（保持旧文件形状）。
- **单通道零回归**：`PeakCanAssertionContext` 重构为组合后，单通道场景（无 `Channels`）必须经现有 e2e 测试全绿才进下一任务。
- **编码注释**：业务/中文注释用中文，技术 API/协议字段用英文（用户全局规则）。
- **分支**：`feat/hil-multichannel`（spec 已在此分支）。

---

## File Structure

### hil-core 包（`D:\claude_proj2\peakcan-hil-core\src\PeakCan.HIL.Core\`）

| 文件 | 责任 | 改动 |
|---|---|---|
| `HIL/ChannelConfig.cs` | 新：通道逻辑名→物理配置映射 | Create |
| `HIL/TestSuite.cs` | suite 顶层加 `Channels` 可选字段 | Modify（加尾部参数） |
| `HIL/StepParams/SendFrameStep.cs` 等 5 个 | 加 `TargetChannel` 参数 | Modify |
| `HIL/StepParams/StepParametersFactory.cs` | dict→强类型读 `TargetChannel` | Modify（5 分支） |
| `HIL/StepParams/StepParametersExporter.cs` | 导出 `TargetChannel` 键 | Modify（5 helper） |
| `HIL/StepResult.cs` | 加 `Channel` 可选字段 | Modify |
| `tests/PeakCan.HIL.Core.Tests/` | 序列化兼容 + Factory/Exporter 往返测试 | Create |

### host `PeakCan.Host.Core`（`src\PeakCan.Host.Core\`）

| 文件 | 责任 | 改动 |
|---|---|---|
| `HIL/Contracts/IAssertionContext.cs` | 演进：加按通道重载 | Modify |
| `HIL/Contracts/IFrameStatistics.cs` | 演进：按 channelName 分桶 | Modify |
| `HIL/Contracts/IHasFrameSink.cs` | 演进：`SetFrameSink(channelName, sink)` | Modify |
| `HIL/Assertions/AssertionPrimitives.cs` | 按通道重载 | Modify |
| `HIL/HilRunRequest.cs` | 加 `HardwareChannels` 列表 | Modify |
| `HIL/StepExecutor/SendFrameStepExecutor.cs` 等 5 个 | 路由 `TargetChannel` + `StepResult.Channel` | Modify |

### host `PeakCan.Host.Infrastructure`（`src\PeakCan.Host.Infrastructure\`）

| 文件 | 责任 | 改动 |
|---|---|---|
| `HIL/SingleChannelContext.cs` | 新：从 `PeakCanAssertionContext` 提取，单通道语义不变 | Create（搬迁） |
| `HIL/MultiChannelAssertionContext.cs` | 新：组合 N 个 SingleChannelContext + 路由 | Create |
| `HIL/PeakCanAssertionContext.cs` | 单通道场景委托给 SingleChannelContext（或保留作单通道实现） | Modify |
| `HIL/HeadlessHostBuilder.cs` | 多 channel DI + per-channel DBC + 多通道 context | Modify |
| `HIL/AscFileFormat.cs` | `WriteFrameLine` 用 `frame.Channel` | Modify |
| `Cli/Reporting/HtmlReportGenerator.cs` | 按通道 DBC 解码 + 帧块标 channel | Modify |
| `HIL/Reporting/HilReportService.cs` | `Generate` 收 `Dictionary<string, DbcDocument>` | Modify |

### studio（独立 repo，本 plan 末尾的并行任务）

| 文件 | 责任 | 改动 |
|---|---|---|
| step 编辑 VM/描述符 | 透传 `TargetChannel` ComboBox | Modify |

---

### Task 1: 更新 spec §3.1（SendSequenceStep 修正）

**Files:**
- Modify: `docs/superpowers/specs/2026-08-21-hil-multichannel-design.md`（§3.1 MVP 步骤清单）

**Interfaces:** 无（文档修正）

- [ ] **Step 1: 修正 spec §3.1**

将 `SendSequenceStep.TargetChannel` 从 MVP 5 类步骤清单移除（`SendSequenceStepExecutor` 是 `NotSupportedException` 占位桩，类型不存在）。清单改为：`SendFrame` / `ExpectFrame` / `AssertNoFrame` / `AssertFrameCount` / `AssertCycleTime`。在 §3.1 加注释："`SendSequence` 为 Sprint 1 占位桩（`SendSequenceStepExecutor.cs:12` throw），本期排除"。

- [ ] **Step 2: Commit**

```bash
git add docs/superpowers/specs/2026-08-21-hil-multichannel-design.md
git commit -m "docs(spec): fix MVP step list — SendSequence is NotSupportedException stub"
```

---

### Task 2: hil-core — `ChannelConfig` + `TestSuite.Channels`

**Files:**
- Create: `D:\claude_proj2\peakcan-hil-core\src\PeakCan.HIL.Core\HIL\ChannelConfig.cs`
- Modify: `D:\claude_proj2\peakcan-hil-core\src\PeakCan.HIL.Core\HIL\TestSuite.cs`
- Test: `D:\claude_proj2\peakcan-hil-core\tests\PeakCan.HIL.Core.Tests\HIL\Multichannel\ChannelConfigTests.cs`

**Interfaces:**
- Consumes: `PeakCan.HIL.Core.BaudRate`（现有）、`PeakCan.HIL.Core.ChannelId`（现有）
- Produces: `ChannelConfig(string Name, string Handle, BaudRate? BaudRate, bool Fd, string? DbcPath, uint? UdsRequestId, uint? UdsResponseId)` record；`TestSuite.Channels : IReadOnlyList<ChannelConfig>?`（尾部可选参数，缺省 null）

- [ ] **Step 1: 写失败测试**

```csharp
// D:\claude_proj2\peakcan-hil-core\tests\PeakCan.HIL.Core.Tests\HIL\Multichannel\ChannelConfigTests.cs
using System.Text.Json;
using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Serialization;
using Xunit;

namespace PeakCan.HIL.Core.Tests.HIL.Multichannel;

public sealed class ChannelConfigTests
{
    [Fact]
    public void ChannelConfig_Records_All_Optional_Fields_Null_By_Default()
    {
        var c = new ChannelConfig("bus-a", "51", BaudRate.Can500kbps, Fd: false, null, null, null);
        c.Name.Should().Be("bus-a");
        c.Handle.Should().Be("51");
        c.DbcPath.Should().BeNull();
        c.UdsRequestId.Should().BeNull();
        c.UdsResponseId.Should().BeNull();
    }

    [Fact]
    public void TestSuite_Channels_Defaults_Null_For_Legacy_Json()
    {
        // 旧 suite JSON 无 channels 字段 → 反序列化后 Channels == null
        var legacyJson = """
            {"name":"s","cases":[],"globalCaseFixtureKeys":[],"suiteFixtureKeys":[],"config":{}}
            """;
        var suite = JsonSerializer.Deserialize<TestSuite>(legacyJson, HILJsonOptions.Default);
        suite!.Channels.Should().BeNull();
    }

    [Fact]
    public void TestSuite_Channels_Roundtrips_Through_Json()
    {
        var suite = new TestSuite("s", Array.Empty<TestCase>(), Array.Empty<string>(),
            Array.Empty<string>(), new TestSuiteConfig(), Channels: new[]
            {
                new ChannelConfig("bus-a", "51", BaudRate.Can500kbps, false, "a.dbc", null, null),
                new ChannelConfig("bus-b", "C600", BaudRate.CanFd1Mbps, true, "b.dbc", 0x7DF, 0x7E8),
            });
        var json = JsonSerializer.Serialize(suite, HILJsonOptions.Default);
        var back = JsonSerializer.Deserialize<TestSuite>(json, HILJsonOptions.Default);
        back!.Channels.Should().NotBeNull();
        back.Channels.Should().HaveCount(2);
        back.Channels[0].Name.Should().Be("bus-a");
        back.Channels[1].UdsResponseId.Should().Be(0x7E8u);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test D:\claude_proj2\peakcan-hil-core\tests\PeakCan.HIL.Core.Tests --filter ChannelConfigTests`
Expected: FAIL — `ChannelConfig` 未定义 + `TestSuite` 无 `Channels` 参数

- [ ] **Step 3: 实现 `ChannelConfig`**

```csharp
// D:\claude_proj2\peakcan-hil-core\src\PeakCan.HIL.Core\HIL\ChannelConfig.cs
namespace PeakCan.HIL.Core.HIL;

/// <summary>
/// One CAN channel declared at the suite level. <see cref="Name"/> is the
/// logical alias steps reference via TargetChannel; <see cref="Handle"/> is
/// the raw channel handle (hex string, e.g. "51" for PEAK / "C600" for ZLG).
/// All optional fields default to null — a suite without Channels is the
/// legacy single-channel shape.
/// </summary>
public sealed record ChannelConfig(
    string Name,
    string Handle,
    BaudRate? BaudRate,
    bool Fd,
    string? DbcPath = null,
    uint? UdsRequestId = null,
    uint? UdsResponseId = null);
```

- [ ] **Step 4: 给 `TestSuite` 加 `Channels` 尾部参数**

在 `TestSuite.cs` record 参数列表末尾（`DataSource` 之后）加：

```csharp
    /// <summary>多通道声明（spec §3.1，0.12.0）。可选；旧 suite JSON 无此字段 → null（单通道兼容）。</summary>
    IReadOnlyList<ChannelConfig>? Channels = null);
```

- [ ] **Step 5: 运行测试确认通过**

Run: `dotnet test D:\claude_proj2\peakcan-hil-core\tests\PeakCan.HIL.Core.Tests --filter ChannelConfigTests`
Expected: PASS

- [ ] **Step 6: 回归——现有 TestSuite 序列化测试全绿**

Run: `dotnet test D:\claude_proj2\peakcan-hil-core\tests\PeakCan.HIL.Core.Tests --filter TestSuite`
Expected: PASS（旧测试不应因加尾部可选参数失败）

- [ ] **Step 7: Commit（hil-core 仓库）**

```bash
cd D:\claude_proj2\peakcan-hil-core
git add src/PeakCan.HIL.Core/HIL/ChannelConfig.cs src/PeakCan.HIL.Core/HIL/TestSuite.cs tests/PeakCan.HIL.Core.Tests/HIL/Multichannel/ChannelConfigTests.cs
git commit -m "feat(hil-core): add ChannelConfig + TestSuite.Channels (multi-channel phase 1)"
```

---

### Task 3: hil-core — 5 类步骤加 `TargetChannel` + Factory/Exporter 对称改

**Files:**
- Modify: `D:\claude_proj2\peakcan-hil-core\src\PeakCan.HIL.Core\HIL\StepParams\SendFrameStep.cs`
- Modify: `...\StepParams\ExpectFrameStep.cs`
- Modify: `...\StepParams\AssertNoFrameStep.cs`
- Modify: `...\StepParams\AssertFrameCountStep.cs`
- Modify: `...\StepParams\AssertCycleTimeStep.cs`
- Modify: `...\StepParams\StepParametersFactory.cs`
- Modify: `...\StepParams\StepParametersExporter.cs`
- Test: `D:\claude_proj2\peakcan-hil-core\tests\PeakCan.HIL.Core.Tests\HIL\Multichannel\TargetChannelRoundtripTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `ChannelConfig`
- Produces: 5 个 step record 各加 `string? TargetChannel = null` 尾部参数；Factory 的 5 个 `Create` 分支用 `ToStringValueOrNull(p, "TargetChannel")` 读；Exporter 的 5 个 `Build` helper 在 `TargetChannel` 非空时写键

- [ ] **Step 1: 写失败测试（Factory→Exporter 往返 + 旧 JSON 兼容）**

```csharp
// D:\claude_proj2\peakcan-hil-core\tests\PeakCan.HIL.Core.Tests\HIL\Multichannel\TargetChannelRoundtripTests.cs
using System.Text.Json;
using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Serialization;
using Xunit;

namespace PeakCan.HIL.Core.Tests.HIL.Multichannel;

public sealed class TargetChannelRoundtripTests
{
    [Theory]
    [InlineData("bus-a")]
    [InlineData(null)]
    public void SendFrameStep_TargetChannel_Roundtrips_Through_Exporter_Factory(string? target)
    {
        var step = new SendFrameStep(new CanId(0x123, FrameFormat.Standard), new byte[]{0x01}, false, false)
        {
            TargetChannel = target,
        };
        var dict = StepParametersExporter.FromParameters(step);
        var back = (SendFrameStep)StepParametersFactory.Create(TestCaseStepKind.SendFrame, dict);
        back.TargetChannel.Should().Be(target);
    }

    [Fact]
    public void Legacy_Json_Without_TargetChannel_Deserializes_To_Null()
    {
        // 旧 JSON 无 targetChannel 键 → Factory 缺键回落 null
        var legacyJson = """
            {"$kind":"sendFrame","id":"0x123","data":"01","fd":false,"extended":false}
            """;
        var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(legacyJson)!;
        // Factory 单参入口需 $kind（已含）
        var step = (SendFrameStep)StepParametersFactory.Create(dict);
        step.TargetChannel.Should().BeNull();
    }

    [Fact]
    public void Exporter_Omits_TargetChannel_Key_When_Null()
    {
        var step = new SendFrameStep(new CanId(0x100, FrameFormat.Standard), new byte[]{0xAA}, false, false);
        var dict = StepParametersExporter.FromParameters(step);
        dict.ContainsKey("TargetChannel").Should().BeFalse();
    }

    [Theory]
    [InlineData("bus-b")]
    public void ExpectFrame_AssertNoFrame_AssertFrameCount_AssertCycleTime_TargetChannel_Roundtrip(string ch)
    {
        var expect = new ExpectFrameStep(new CanId(0x200, FrameFormat.Standard), null, "1000") { TargetChannel = ch };
        var noFrame = new AssertNoFrameStep(new CanId(0x300, FrameFormat.Standard), "500") { TargetChannel = ch };
        var count = new AssertFrameCountStep(new CanId(0x400, FrameFormat.Standard), "1000", "1", "5") { TargetChannel = ch };
        var cycle = new AssertCycleTimeStep(new CanId(0x500, FrameFormat.Standard), "1000", "9", "11", "3") { TargetChannel = ch };

        ((ExpectFrameStep)StepParametersFactory.Create(TestCaseStepKind.WaitForFrame,
            StepParametersExporter.FromParameters(expect))).TargetChannel.Should().Be(ch);
        ((AssertNoFrameStep)StepParametersFactory.Create(TestCaseStepKind.AssertNoFrame,
            StepParametersExporter.FromParameters(noFrame))).TargetChannel.Should().Be(ch);
        ((AssertFrameCountStep)StepParametersFactory.Create(TestCaseStepKind.AssertFrameCount,
            StepParametersExporter.FromParameters(count))).TargetChannel.Should().Be(ch);
        ((AssertCycleTimeStep)StepParametersFactory.Create(TestCaseStepKind.AssertCycleTime,
            StepParametersExporter.FromParameters(cycle))).TargetChannel.Should().Be(ch);
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test --filter TargetChannelRoundtripTests`
Expected: FAIL — record 无 `TargetChannel` 属性

- [ ] **Step 3: 给 5 个 step record 加 `TargetChannel`**

每个 record 末尾加 `string? TargetChannel = null`（用 init-only，对齐 record 参数风格）。以 `SendFrameStep` 为例：

```csharp
public record SendFrameStep(
    CanId Id,
    byte[] Data,
    bool Fd,
    bool Extended,
    CounterConfig? AutoCounter = null,
    ChecksumConfig? AutoChecksum = null)
    : StepParameters(TestCaseStepKind.SendFrame)
{
    public string? TargetChannel { get; init; }
}
```

其余 4 个（`ExpectFrameStep`/`AssertNoFrameStep`/`AssertFrameCountStep`/`AssertCycleTimeStep`）同样加 `public string? TargetChannel { get; init; }`。

- [ ] **Step 4: Factory 5 个分支读 `TargetChannel`**

在 `StepParametersFactory.Create` 的 5 个对应分支末尾加 `ToStringValueOrNull(p, "TargetChannel")`，用对象初始化器赋给 `TargetChannel`。以 `SendFrame` 为例：

```csharp
TestCaseStepKind.SendFrame => new SendFrameStep(
    ResolveCanId(p["Id"], BoolOr(p, "Extended")),
    ResolveBytes(p["Data"]),
    BoolOr(p, "Fd"),
    BoolOr(p, "Extended"),
    TryParseCounter(p, "AutoCounter"),
    TryParseChecksum(p, "AutoChecksum"))
{ TargetChannel = ToStringValueOrNull(p, "TargetChannel") },
```

其余 4 个分支同理（`WaitForFrame`/`AssertNoFrame`/`AssertFrameCount`/`AssertCycleTime`）。

- [ ] **Step 5: Exporter 5 个 `Build` helper 写 `TargetChannel`**

在 `StepParametersExporter` 的 `Build(SendFrameStep)`、`Build(CanId, string, byte[]?)`（ExpectFrame 用）、`Build(AssertNoFrameStep)`、`Build(AssertFrameCountStep)`、`Build(AssertCycleTimeStep)` 末尾加：

```csharp
if (s.TargetChannel is { } tc) d["TargetChannel"] = tc;
```

注意 `ExpectFrameStep` 当前走 `Build(e.Id, e.TimeoutMs, e.DataMask)` —— 该 helper 签名加 `string? targetChannel` 参数，或改为接收 `ExpectFrameStep`。优先改 helper 签名收 `ExpectFrameStep`（对称其他 4 个）。

- [ ] **Step 6: 运行测试确认通过**

Run: `dotnet test --filter TargetChannelRoundtripTests`
Expected: PASS

- [ ] **Step 7: 回归——所有现有 StepParametersFactory/Exporter 测试全绿**

Run: `dotnet test --filter StepParameters`
Expected: PASS（旧测试不应因加可选字段失败）

- [ ] **Step 8: Commit（hil-core）**

```bash
cd D:\claude_proj2\peakcan-hil-core
git add src/PeakCan.HIL.Core/HIL/StepParams/ tests/PeakCan.HIL.Core.Tests/HIL/Multichannel/TargetChannelRoundtripTests.cs
git commit -m "feat(hil-core): add TargetChannel to 5 step types + Factory/Exporter symmetry"
```

---

### Task 4: hil-core — `StepResult.Channel`

**Files:**
- Modify: `D:\claude_proj2\peakcan-hil-core\src\PeakCan.HIL.Core\HIL\StepResult.cs`
- Test: `D:\claude_proj2\peakcan-hil-core\tests\PeakCan.HIL.Core.Tests\HIL\Multichannel\StepResultChannelTests.cs`

**Interfaces:**
- Consumes: 无
- Produces: `StepResult.Channel : string?`（尾部可选参数，缺省 null）

- [ ] **Step 1: 写失败测试**

```csharp
// tests/.../HIL/Multichannel/StepResultChannelTests.cs
using System.Text.Json;
using FluentAssertions;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Serialization;
using Xunit;

namespace PeakCan.HIL.Core.Tests.HIL.Multichannel;

public sealed class StepResultChannelTests
{
    [Fact]
    public void StepResult_Channel_Defaults_Null()
    {
        var r = new StepResult(0, TestCaseStepKind.SendFrame, null, StepStatus.Passed, "ok", null, null, 0);
        r.Channel.Should().BeNull();
    }

    [Fact]
    public void StepResult_Channel_Roundtrips_Through_Json()
    {
        var r = new StepResult(0, TestCaseStepKind.SendFrame, "lbl", StepStatus.Failed, "timeout", null, null, 100)
        { Channel = "bus-b" };
        var json = JsonSerializer.Serialize(r, HILJsonOptions.Default);
        var back = JsonSerializer.Deserialize<StepResult>(json, HILJsonOptions.Default);
        back!.Channel.Should().Be("bus-b");
    }

    [Fact]
    public void Legacy_StepResult_Json_Deserializes_Channel_To_Null()
    {
        var legacyJson = """
            {"stepIndex":0,"kind":"SendFrame","status":"Passed","elapsedMs":0,"message":"ok"}
            """;
        var back = JsonSerializer.Deserialize<StepResult>(legacyJson, HILJsonOptions.Default);
        back!.Channel.Should().BeNull();
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test --filter StepResultChannelTests`
Expected: FAIL — `StepResult` 无 `Channel`

- [ ] **Step 3: 加 `Channel` 尾部参数**

在 `StepResult` record 参数列表末尾（`Iteration` 之后）加：

```csharp
    /// <summary>该步骤执行的通道别名（spec Q6，0.12.0）。null = 默认/唯一通道。旧 JSON 无此字段 → null。</summary>
    string? Channel = null);
```

- [ ] **Step 4: 运行测试确认通过 + 现有 StepResult 测试回归**

Run: `dotnet test --filter "StepResult|TestSuiteResult"`
Expected: PASS

- [ ] **Step 5: Commit（hil-core）**

```bash
cd D:\claude_proj2\peakcan-hil-core
git add src/PeakCan.HIL.Core/HIL/StepResult.cs tests/PeakCan.HIL.Core.Tests/HIL/Multichannel/StepResultChannelTests.cs
git commit -m "feat(hil-core): add StepResult.Channel for multi-channel attribution"
```

---

### Task 5: hil-core pack 0.12.0 + host/studio 双 bump

**Files:**
- Modify: `D:\claude_proj2\peakcan-hil-core\src\PeakCan.HIL.Core\PeakCan.HIL.Core.csproj`（版本→0.12.0）
- Modify: `D:\claude_proj2\peakcan-host\Directory.Packages.props`（Pin→0.12.0）
- Modify: `D:\claude_proj2\peakcan-studio\Directory.Packages.props`（Pin→0.12.0）

**Interfaces:** 无（pack/feed 操作）

- [ ] **Step 1: 核对当前双仓库 pin（Q4）**

Run: `grep PeakCan.HIL.Core D:\claude_proj2\peakcan-host\Directory.Packages.props D:\claude_proj2\peakcan-studio\Directory.Packages.props`
记录两侧当前版本（应都是 0.11.0；若不一致，先对齐再继续）。

- [ ] **Step 2: bump hil-core 版本到 0.12.0**

改 `PeakCan.HIL.Core.csproj` 的 `<Version>`/`<PackageVersion>`（按该 csproj 现有版本字段格式）到 `0.12.0`。

- [ ] **Step 3: pack 到本地 feed**

Run:
```bash
cd D:\claude_proj2\peakcan-hil-core\src\PeakCan.HIL.Core
dotnet pack -c Release
copy bin\Release\PeakCan.HIL.Core.0.12.0.nupkg D:\nuget-local\
```
确认 `D:\nuget-local\PeakCan.HIL.Core.0.12.0.nupkg` 存在。

- [ ] **Step 4: host bump + restore**

改 `D:\claude_proj2\peakcan-host\Directory.Packages.props`：`<PackageVersion Include="PeakCan.HIL.Core" Version="0.12.0" />`
Run: `cd D:\claude_proj2\peakcan-host && dotnet restore`
Expected: restore 成功，无 NU1605/NU1602 错误。

- [ ] **Step 5: studio bump + restore**

改 `D:\claude_proj2\peakcan-studio\Directory.Packages.props` 同 0.12.0。
Run: `cd D:\claude_proj2\peakcan-studio && dotnet restore`
Expected: restore 成功。

- [ ] **Step 6: 两侧 build 冒烟**

Run: `dotnet build D:\claude_proj2\peakcan-host\PeakCan.Host.sln` 和 studio sln
Expected: 编译通过（此时新 API 尚未被消费，仅验证 pack/bump 无破坏）。

- [ ] **Step 7: Commit（三个仓库各一）**

```bash
# hil-core
cd D:\claude_proj2\peakcan-hil-core && git add src/PeakCan.HIL.Core/PeakCan.HIL.Core.csproj && git commit -m "chore: bump 0.12.0 (multi-channel)"
# host
cd D:\claude_proj2\peakcan-host && git add Directory.Packages.props && git commit -m "chore: bump hil-core to 0.12.0"
# studio
cd D:\claude_proj2\peakcan-studio && git add Directory.Packages.props && git commit -m "chore: bump hil-core to 0.12.0"
```

---

### Task 6: host Core — `IAssertionContext` / `IFrameStatistics` / `IHasFrameSink` 演进

**Files:**
- Modify: `src\PeakCan.Host.Core\HIL\Contracts\IAssertionContext.cs`
- Modify: `src\PeakCan.Host.Core\HIL\Contracts\IFrameStatistics.cs`
- Modify: `src\PeakCan.Host.Core\HIL\Contracts\IHasFrameSink.cs`
- Test: `tests\PeakCan.Host.Core.Tests\HIL\Multichannel\AssertionContextContractTests.cs`

**Interfaces:**
- Consumes: `PeakCan.HIL.Core.CanFrame`、`PeakCan.HIL.Core.HIL.Contracts.DecodedFrame`
- Produces: `IAssertionContext` 加 3 个按通道重载（`channelName` null = 默认）；`IFrameStatistics` 的 3 方法加 `string? channelName = null`；`IHasFrameSink.SetFrameSink(string? channelName, IHilFrameSink? sink)`

- [ ] **Step 1: 写失败测试（契约形状验证）**

```csharp
// tests/PeakCan.Host.Core.Tests/HIL/Multichannel/AssertionContextContractTests.cs
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;
using Xunit;

namespace PeakCan.Host.Core.Tests.HIL.Multichannel;

public sealed class AssertionContextContractTests
{
    // Compile-time contract: interface methods exist with channelName overload.
    // If the new overloads are missing, this file fails to compile.
    [Fact]
    public void IAssertionContext_Has_MultiChannel_Overloads()
    {
        IAssertionContext ctx = null!;
        // These calls must resolve at compile time:
        Assert.True(typeof(IAssertionContext).GetMethod("SendFrameAsync",
            new[] { typeof(string), typeof(CanFrame), typeof(CancellationToken) }) is not null);
        Assert.True(typeof(IAssertionContext).GetMethod("SubscribeDecodedFrames",
            new[] { typeof(string), typeof(Action<DecodedFrame>) }) is not null);
        Assert.True(typeof(IAssertionContext).GetMethod("GetRecentDecodedFrames",
            new[] { typeof(string) }) is not null);
    }
}
```

- [ ] **Step 2: 运行确认失败（编译错）**

Run: `dotnet test --filter AssertionContextContractTests`
Expected: FAIL — 接口无新重载，编译失败。

- [ ] **Step 3: 演进 `IAssertionContext`**

在 `IAssertionContext.cs` 加 3 个按通道重载（用 default interface methods 转发到 null=默认，避免破坏现有实现者 `PeakCanAssertionContext`/`HILAssertionContext`——它们可在 Task 8 显式实现）：

```csharp
// 在接口内追加（default method 转发旧重载）：
/// <summary>按逻辑名路由发送（channelName null/空 = 默认/唯一通道）。</summary>
ValueTask<Result<Unit>> SendFrameAsync(string? channelName, CanFrame frame, CancellationToken ct)
    => SendFrameAsync(frame, ct);  // default = 忽略 channelName，转发旧重载（单通道兼容）

/// <summary>按逻辑名订阅解码帧流。</summary>
IDisposable SubscribeDecodedFrames(string? channelName, Action<DecodedFrame> onFrame)
    => SubscribeDecodedFrames(onFrame);

/// <summary>按通道桶查最近帧。</summary>
IReadOnlyList<DecodedFrame> GetRecentDecodedFrames(string? channelName)
    => GetRecentDecodedFrames();
```

- [ ] **Step 4: 演进 `IFrameStatistics`**

`IFrameStatistics.cs` 的 `CountSince`/`GetIntervalStats` 加 `string? channelName = null` 尾部参数（default method 转发）。

- [ ] **Step 5: 演进 `IHasFrameSink`**

`SetFrameSink(IHilFrameSink?)` 加重载 `SetFrameSink(string? channelName, IHilFrameSink? sink)`，default 转发到单参版。

- [ ] **Step 6: 运行测试确认通过 + 现有 context 测试回归**

Run: `dotnet test --filter "AssertionContext|HILAssertion|PeakCanAssertion"`
Expected: PASS（default method 转发保证旧实现者零改动通过）

- [ ] **Step 7: Commit（host）**

```bash
cd D:\claude_proj2\peakcan-host
git add src/PeakCan.Host.Core/HIL/Contracts/ tests/PeakCan.Host.Core.Tests/HIL/Multichannel/
git commit -m "feat(host-core): evolve IAssertionContext/IFrameStatistics/IHasFrameSink for multi-channel"
```

---

### Task 7: host Core — `AssertionPrimitives` 按通道重载 + `HilRunRequest.HardwareChannels`

**Files:**
- Modify: `src\PeakCan.Host.Core\HIL\Assertions\AssertionPrimitives.cs`
- Modify: `src\PeakCan.Host.Core\HIL\HilRunRequest.cs`
- Test: `tests\PeakCan.Host.Core.Tests\HIL\Multichannel\AssertionPrimitivesMultiChannelTests.cs`

**Interfaces:**
- Consumes: Task 6 的 `IAssertionContext` 按通道重载
- Produces: `AssertionPrimitives.WaitForFrameAsync(CanId, byte[]?, int, string? channelName, ct)` 等重载；`HilRunRequest.HardwareChannels : IReadOnlyList<ChannelConfig>?`

- [ ] **Step 1: 写失败测试**

测试 `AssertionPrimitives.WaitForFrameAsync` 按通道重载存在 + `HilRunRequest` 可带 `HardwareChannels`（构造一个 fake `IAssertionContext`，验证调用按通道重载）。

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: `AssertionPrimitives` 加按通道重载**

`WaitForFrameAsync` 等方法加 `string? channelName = null` 参数，内部调 `ctx.GetRecentDecodedFrames(channelName)` / `ctx.SubscribeDecodedFrames(channelName, ...)`。旧签名（无 channelName）保留，转发 `channelName: null`。

- [ ] **Step 4: `HilRunRequest` 加 `HardwareChannels`**

record 末尾加 `IReadOnlyList<ChannelConfig>? HardwareChannels = null`（null = 旧单通道 `HardwareChannel` 路径）。

- [ ] **Step 5: 运行测试 + 回归**

Run: `dotnet test --filter "AssertionPrimitives|HilRunRequest"`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.Core/HIL/Assertions/AssertionPrimitives.cs src/PeakCan.Host.Core/HIL/HilRunRequest.cs tests/
git commit -m "feat(host-core): AssertionPrimitives channel overloads + HilRunRequest.HardwareChannels"
```

---

### Task 8: host Infra — `PeakCanAssertionContext` 重构为 `SingleChannelContext` + `MultiChannelAssertionContext`

**Files:**
- Create: `src\PeakCan.Host.Infrastructure\HIL\SingleChannelContext.cs`
- Create: `src\PeakCan.Host.Infrastructure\HIL\MultiChannelAssertionContext.cs`
- Modify: `src\PeakCan.Host.Infrastructure\HIL\PeakCanAssertionContext.cs`（单通道场景委托给 `SingleChannelContext`）
- Test: `tests\PeakCan.Host.Infrastructure.Tests\HIL\Multichannel\SingleChannelContextTests.cs`、`MultiChannelAssertionContextTests.cs`

**Interfaces:**
- Consumes: Task 6 的演进后 `IAssertionContext`/`IHasFrameSink`、`PeakCan.HIL.Core.IDbcLookup`
- Produces: `SingleChannelContext`（单通道语义不变，搬迁自 `PeakCanAssertionContext`）；`MultiChannelAssertionContext`（组合 `Dictionary<string, SingleChannelContext>` + 路由）

- [ ] **Step 1: 写失败测试（`SingleChannelContext` 行为 = 旧 `PeakCanAssertionContext`）**

用现有 `PeakCanAssertionContext` 的测试用例（帧缓冲/信号缓存/解码/recent frames/sink/variables）拷到 `SingleChannelContextTests`，验证行为一致。这是"搬迁零回归"的回归基线。

- [ ] **Step 2: 运行确认失败（`SingleChannelContext` 未定义）**

- [ ] **Step 3: 提取 `SingleChannelContext`**

把 `PeakCanAssertionContext` 的全部实现**原样搬迁**到 `SingleChannelContext`（类名改、构造签名不变），保留 6 职责。`SingleChannelContext : IAssertionContext, IHasRecentFrames, IStepVariableStore, IHasFrameSink, IDisposable`，显式实现 Task 6 的 3 个按通道重载（单通道下 `channelName` 忽略，转发旧方法——但若 `channelName` 非空且 ≠ 本通道名，按通道重载应返回空订阅/空列表以避免误路由，由测试覆盖）。

- [ ] **Step 4: 写 `MultiChannelAssertionContext` 测试**

测试：构造 N=2 个 `SingleChannelContext`，`SendFrameAsync("bus-a", frame)` 转发到对应 context；`SubscribeDecodedFrames("bus-b", cb)` 只订 bus-b 的帧流；未声明 channelName 报错或回落默认。

- [ ] **Step 5: 实现 `MultiChannelAssertionContext`**

持有 `Dictionary<string, SingleChannelContext>` + 默认通道名（`suite.Channels[0].Name` 或唯一通道）。按 `channelName` 路由；`null`/空 = 默认通道。

- [ ] **Step 6: `PeakCanAssertionContext` 改为单通道委托**

保留 `PeakCanAssertionContext` 类（`HeadlessHostBuilder` 仍引用），内部委托给 `SingleChannelContext`，或直接把 `HeadlessHostBuilder` 单通道路径改用 `SingleChannelContext`（Task 9）。优先让 `PeakCanAssertionContext` 继承/包装 `SingleChannelContext` 以最小破坏面。

- [ ] **Step 7: 运行全部 HIL Infra 测试回归**

Run: `dotnet test tests/PeakCan.Host.Infrastructure.Tests --filter "HIL|Assertion"`
Expected: PASS（单通道零回归）

- [ ] **Step 8: Commit**

```bash
git add src/PeakCan.Host.Infrastructure/HIL/SingleChannelContext.cs src/PeakCan.Host.Infrastructure/HIL/MultiChannelAssertionContext.cs src/PeakCan.Host.Infrastructure/HIL/PeakCanAssertionContext.cs tests/
git commit -m "refactor(host-infra): PeakCanAssertionContext -> SingleChannelContext + MultiChannelAssertionContext composition"
```

---

### Task 9: host Infra — `HeadlessHostBuilder` 多 channel DI + per-channel DBC + 5 executor 路由

**Files:**
- Modify: `src\PeakCan.Host.Infrastructure\HIL\HeadlessHostBuilder.cs`
- Modify: `src\PeakCan.Host.Core\HIL\StepExecutor\SendFrameStepExecutor.cs`（5 个 executor）
- Test: `tests\PeakCan.Host.Infrastructure.Tests\HIL\Multichannel\HeadlessHostBuilderMultiChannelTests.cs`

**Interfaces:**
- Consumes: Task 8 的 `MultiChannelAssertionContext`/`SingleChannelContext`、Task 2 的 `ChannelConfig`、Task 7 的 `HilRunRequest.HardwareChannels`
- Produces: `HeadlessHostBuilder` 按 `HardwareChannels` 逐项打开 channel + per-channel `DbcDocument`/`IDbcLookup` + 构造 `MultiChannelAssertionContext`；5 个 executor 按 `step.TargetChannel` 路由 + 写 `StepResult.Channel`

- [ ] **Step 1: 写失败测试**

测试 `HeadlessHostBuilder.Build` 在 `HardwareChannels` 含 2 项时，DI 注册 2 个 `ICanChannel`（按 name 字典）、2 个 `DbcDocument`、`MultiChannelAssertionContext`。

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 改 `HeadlessHostBuilder` 硬件模式分支**

`args.HardwareChannels` 非空时：逐 `ChannelConfig` 经 `CompositeChannelFactory.Create(ChannelId)` 打开 → `RegisterChannel` 进 router → 解析 per-channel `DbcDocument` → 构造 `MultiChannelAssertionContext`。单通道（`HardwareChannels` null）路径不变（用 `SingleChannelContext`/`PeakCanAssertionContext`）。

- [ ] **Step 4: 5 个 executor 路由**

每个 executor 读 `p.TargetChannel`，调 `ctx.SendFrameAsync(p.TargetChannel, frame, ct)`（发送类）或 `AssertionPrimitives.WaitForFrameAsync(..., p.TargetChannel, ct)`（监控类），`StepResult.Channel = p.TargetChannel ?? default`。以 `SendFrameStepExecutor` 为例：

```csharp
var result = await ctx.SendFrameAsync(p.TargetChannel,
    new CanFrame(p.Id, payload, flags, ctx.ResolveChannelId(p.TargetChannel), default), ct);
return new StepResult(0, step.Kind, step.Label,
    result.IsSuccess ? StepStatus.Passed : StepStatus.Failed,
    ..., Channel: p.TargetChannel);
```

> `ResolveChannelId` 是 `MultiChannelAssertionContext` 新增方法（Task 8 Produces 补充），把逻辑名→`ChannelId`。

- [ ] **Step 5: `TestSuiteEngine` 加 channel validator（Q3）**

suite 无 `Channels` + 任一步骤 `TargetChannel` 非空 → 报错；`TargetChannel` 引用未声明通道名 → 报错。在 `StepValidatorRegistry` 加 `TargetChannelValidator`。

- [ ] **Step 6: 运行测试 + 全 HIL 回归**

Run: `dotnet test --filter HIL`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add src/PeakCan.Host.Infrastructure/HIL/HeadlessHostBuilder.cs src/PeakCan.Host.Core/HIL/StepExecutor/ tests/
git commit -m "feat(host): multi-channel DI + per-channel DBC + executor TargetChannel routing + validators"
```

---

### Task 10: host Infra — `AscFileFormat` 带 channel 列 + `FrameStatisticsCollector` 分桶

**Files:**
- Modify: `src\PeakCan.Host.Infrastructure\HIL\AscFileFormat.cs`
- Modify: `src\PeakCan.Host.Infrastructure\HIL\FrameStatisticsCollector.cs`
- Test: `tests\PeakCan.Host.Infrastructure.Tests\HIL\Multichannel\AscFileFormatChannelTests.cs`

**Interfaces:**
- Consumes: `PeakCan.HIL.Core.CanFrame.Channel`
- Produces: `WriteFrameLine` 用 `frame.Channel` 映射 asc channel 号；`FrameStatisticsCollector` 按 channelName 分桶

- [ ] **Step 1: 写失败测试**

测试 `WriteFrameLine` 输出含 `frame.Channel` 映射的 channel 号（PEAK 0x51→1、0x52→2；用一个 `ChannelIdToAscNumber` 映射函数覆盖）。

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 改 `WriteFrameLine`**

```csharp
public static void WriteFrameLine(StringBuilder sb, CanFrame frame, double elapsedUs)
{
    var seconds = elapsedUs / 1_000_000.0;
    var idStr = frame.Id.IsExtended ? $"0x{frame.Id.Raw:X8}" : $"0x{frame.Id.Raw:X3}";
    var dlc = frame.Data.Length;
    var dataHex = BitConverter.ToString(frame.Data.Span.ToArray()).Replace("-", " ");
    var chNum = ChannelIdToAscNumber(frame.Channel);
    sb.AppendLine($"{seconds,12:F6} {chNum}  {idStr,-12}x       Rx d {dlc} {dataHex}");
}
```

加 `internal static int ChannelIdToAscNumber(ChannelId id)` 映射（PEAK 低 handle→1/2，ZLG 0x8000+→按枚举顺序分配 ≥3）。

- [ ] **Step 4: `FrameStatisticsCollector` 分桶**

`IFrameStatistics` 实现按 `channelName` 维护多个 collector 实例（字典）；`null` = 默认。

- [ ] **Step 5: 运行测试 + 现有 FrameCaptureExporter/Asc 测试回归**

Run: `dotnet test --filter "Asc|FrameCapture|FrameStatistics"`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.Infrastructure/HIL/AscFileFormat.cs src/PeakCan.Host.Infrastructure/HIL/FrameStatisticsCollector.cs tests/
git commit -m "feat(host-infra): AscFileFormat channel column + FrameStatisticsCollector per-channel buckets"
```

---

### Task 11: host Infra — `HtmlReportGenerator` 按通道 DBC 解码 + `StepResult.Channel` 展示

**Files:**
- Modify: `src\PeakCan.Host.Infrastructure\Cli\Reporting\HtmlReportGenerator.cs`
- Modify: `src\PeakCan.Host.Infrastructure\HIL\Reporting\HilReportService.cs`
- Test: `tests\PeakCan.Host.Infrastructure.Tests\Cli\Reporting\HtmlReportGeneratorMultiChannelTests.cs`

**Interfaces:**
- Consumes: `StepResult.Channel`、`CanFrame.Channel`、`Dictionary<string, DbcDocument>`
- Produces: `RenderCase(c, dbcs)` 按帧 channel 选 DbcDocument；步骤结果行标 channel；`HilReportService.Generate(TestSuiteResult, Dictionary<string, DbcDocument>?)`

- [ ] **Step 1: 写失败测试**

测试 `RenderCase` 收 `Dictionary<string, DbcDocument>`，失败帧按 `frame.Channel` 选对应 DbcDocument 解码；`StepResult.Channel` 非空时步骤行含 `通道: bus-a`。

- [ ] **Step 2-6: 实现 + 测试 + 回归 + Commit**（同前模式）

改 `RenderCase`/`RenderDecodedSignals`/`RenderSignalTimeline` 收 `IReadOnlyDictionary<string, DbcDocument>?`，按 `frame.Channel` 查（null/默认→首 DbcDocument 兜底）。`RenderStepResult` 行加 `Channel` 标签。`HilReportService.Generate` 签名改收字典。

Commit: `feat(host-infra): HtmlReportGenerator per-channel DBC decode + StepResult.Channel display`

---

### Task 12: 端到端 — 双通道 loopback 用例 + e2e 测试

**Files:**
- Create: `tests\PeakCan.Host.Infrastructure.Tests\HIL\Multichannel\DualChannelLoopbackE2E.cs`
- Create: 测试用 suite JSON（2 channels + 跨通道 send/expect 步骤）

**Interfaces:**
- Consumes: Task 2-11 全部
- Produces: 双通道 loopback 用例（A 路发帧 → B 路 `ExpectFrame` 监控 → 报告/case log 标通道）

- [ ] **Step 1: 写 e2e 测试**

构造 `HilRunRequest`（`HardwareChannels` = 2 项，可用 `VirtualChannel` 作 loopback 双通道或 mock `ICanChannel`），suite 含 `Channels` 声明 + `SendFrame(TargetChannel="bus-a")` + `ExpectFrame(TargetChannel="bus-b")`。跑 `HilRunnerService.RunAsync`，验证 `StepResult.Channel` 正确、case log asc 行含两 channel 号、报告 HTML 含通道标签。

- [ ] **Step 2: 运行 + 调试直到 PASS**

- [ ] **Step 3: 单通道 e2e 回归（零回归）**

跑现有单通道 e2e 测试全绿。

- [ ] **Step 4: Commit**

```bash
git add tests/
git commit -m "test(hil): dual-channel loopback e2e + single-channel regression"
```

---

### Task 13: studio — step 编辑器透传 `TargetChannel`（并行任务）

**Files:**
- Modify: `D:\claude_proj2\peakcan-studio\` 下 step 编辑相关 VM/描述符/XAML

**Interfaces:**
- Consumes: Task 5 bump 后的 hil-core 0.12.0（5 步骤有 `TargetChannel`）
- Produces: `EditableTestCaseStep` 透传 `TargetChannel`；5 个步骤描述符加 TargetChannel ComboBox（选项来自 suite `Channels`，离线时选"默认通道"）；Copilot 模板同步

> 此任务在 studio 独立仓库，与 host 任务（9-12）无代码耦合，可并行。实施时参照 studio 现有 step 描述符注册模式（`StepFieldDescriptors` 等）。

- [ ] **Step 1-5: TDD 实现 5 步骤的 TargetChannel 字段 UI**（参照 studio 现有字段描述符模式，bite-sized）

- [ ] **Step 6: Commit（studio 仓库）**

```bash
cd D:\claude_proj2\peakcan-studio && git add . && git commit -m "feat(studio): expose TargetChannel field for 5 multi-channel step types"
```

---

## Self-Review

**1. Spec coverage:**
- §3.1 `ChannelConfig`/`TargetChannel`/`StepResult.Channel` → Task 2/3/4 ✓
- §3.2 `IAssertionContext`/`IFrameStatistics`/`IHasFrameSink` 演进 → Task 6 ✓
- §3.3 `AssertionPrimitives` 分桶 + `HilRunRequest.HardwareChannels` → Task 7 ✓
- §3.3 `PeakCanAssertionContext` 组合重构 → Task 8 ✓
- §3.4 多 channel DI + per-channel DBC + executor 路由 → Task 9 ✓
- §3.5 `AscFileFormat` channel 列 → Task 10 ✓
- §3.6 `HtmlReportGenerator` 按通道 DBC → Task 11 ✓
- §3.7 host HilView run 配置 → **缺口**：本 plan 未含 host `HilViewModel` 的通道列表编辑器 UI。因 MVP 先验证 CLI/Infra 链路（Task 12 e2e 用 `HilRunRequest` 直接构造），HilView UI 可后置。已在下方"缺口"标注。
- §4 实施顺序 → Task 1-13 按 §4 序列 ✓
- §3.7 studio → Task 13 ✓

**2. Placeholder scan:** 无 TBD/TODO；每个任务含真实测试代码 + 实现代码。Task 11/13 的 step 2-6 简写为"同前模式"——**修正**：这些是 bite-sized TDD 步骤的省略引用，实施时按 Task 2-10 的模式展开（写失败测试→确认失败→实现→确认通过→回归→commit）。Task 13 因 studio 模式未在本 session 调查，标"参照 studio 现有模式"——实施前需先调查 studio step 描述符注册点。

**3. Type consistency:** `ChannelConfig(Name, Handle, BaudRate?, Fd, DbcPath?, UdsRequestId?, UdsResponseId?)` — Task 2 定义，Task 5/9/13 消费，签名一致 ✓。`TargetChannel` — Task 3 定义为 `string?`，Task 7/9 消费一致 ✓。`StepResult.Channel` — Task 4 `string?`，Task 9/11 消费一致 ✓。`MultiChannelAssertionContext.ResolveChannelId` — Task 9 引用，需在 Task 8 Produces 明确补该方法（已注明）。

**缺口（已知，非 placeholder）：**
- host `HilViewModel` 通道列表编辑器 UI（§3.7）——MVP 用 `HilRunRequest` 直构造验证链路，UI 后置，不阻塞 Task 12 e2e。
- studio step 描述符注册点的精确路径——Task 13 实施前需调查 studio 仓库 `StepFieldDescriptors`/`EditableTestCaseStep` 结构。

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-08-22-hil-multichannel-phase1.md`. Two execution options:

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints

Which approach?

**Note:** Task 5（pack/bump）涉及三仓库 + 本地 feed 操作，建议人工监督执行（或 subagent 执行时明确给出 feed 路径 `D:\nuget-local`）。Task 13（studio）在独立仓库，可与 host 任务并行。
