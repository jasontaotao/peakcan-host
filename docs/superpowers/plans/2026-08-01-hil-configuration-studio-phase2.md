# HIL Configuration Studio — Phase 2 (Test Suite Builder) 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 HilStudioWindow 的 col2 实现 Test Suite Builder：从 Toolbox 构建测试用例步骤、参数从 DBC 独立下拉选择、SendFrame 用 DbcEncodeService 信号组合器生成 Data 字节、suite.json 用现有模型 round-trip 保存/加载。

**Architecture:** 编辑模型 = `Dictionary<string,object>`（`StepParametersFactory.Create` 期望形态），保存时复用工厂；新增 Core 类 `StepParametersExporter`（强类型→dict，工厂的逆操作）支持加载。属性面板 = 字段描述符驱动（每 kind 一个 `StepFieldDescriptor` 列表），通用 DataTemplate 按 FieldKind 渲染。Test Suite Builder 是 `HilStudioViewModel` 的 col2 子面板 VM（镜像 UdsViewModel 编排 6 个 panel 的先例）。

**Tech Stack:** WPF net10.0-windows，CommunityToolkit.Mvvm 8.4.2，`HILJsonOptions.Default` + `TestSuite/TestCase/TestCaseStep` 模型 + `StepParametersFactory` + `DbcEncodeService`（均已有）。

## Global Constraints

- **阶段边界**：本计划只做 Phase 2（col2 Test Suite Builder）。col3 ECU Simulator 仍是占位（Phase 3）。Phase 1 的 DBC Browser 不动（但 `HilStudioViewModel` 会加 `SuiteBuilder` 子 VM）。
- **round-trip 契约**：保存的 suite.json 必须被现有 runtime（`TestSuiteEngine`）直接消费——必须走 `TestSuite/TestCase/TestCaseStep` 强类型模型 + `HILJsonOptions.Default` 序列化，**禁止手拼 JSON / 序列化编辑模型**。
- **引擎零改动**：不触碰 `TestSuiteEngine` / 12 个 executor / `HILAssertionContext`。
- **独立下拉（用户决策 2026-08-01）**：参数控件从整个 `DbcService.Current` 拉独立下拉，不依赖 DBC Browser 选中。
- **SendFrame 内置信号组合器（用户决策）**：SendFrame 编辑内建"选报文→填信号→`DbcEncodeService.Encode` 生成 Data 字节"。
- **dict 类型契约（来自 `StepParametersFactory`）**：`Extended/Fd/ExpectPresent` 必须是 `bool`；`Data/DataMask/CorruptXorMask` 是 hex 字符串；`FaultType/Direction` 是枚举字符串；`CorruptByteIndices` 必须是 `IEnumerable<object>`（`(IEnumerable<object>)int[]` 运行时会 InvalidCastException）；`DtcCode/FaultId/DataMask/CorruptByteIndices` 是可选项（缺省用 TryGetValue）。
- **信号名格式**：`"{Msg}.{Sig}"` 全名（与 `IAssertionContext.GetSignalValue` 一致）。
- 新增 VM 用 `[ObservableProperty]`/`[RelayCommand]`；可编辑模型持 `Dictionary<string,object>`（无 INPC，字段只绑定一次）。

## File Structure

**新建（src/PeakCan.Host.Core/）**
- `HIL/StepParams/StepParametersExporter.cs` — 强类型→dict（工厂逆操作）

**新建（src/PeakCan.Host.App/ViewModels/TestSuiteBuilder/）**
- `EditableTestCaseStep.cs` — 可编辑步骤（Kind/Label/Params dict + ToStep/FromStep）
- `EditableTestCase.cs` — 可编辑用例（Id/Name/Description/Steps/Tags + ToCase/FromCase）
- `StepFieldDescriptor.cs` — 字段描述符（`StepFieldKind` 枚举 + record）
- `StepFieldDescriptors.cs` — 12 个 kind 的 descriptor 列表 + `StepDefaults.For(kind)`
- `SendFrameComposerViewModel.cs` — DbcEncodeService 信号组合器
- `TestSuiteBuilderViewModel.cs` + `RoundTripFlow.partial.cs` + `DbcOptionsFlow.partial.cs` — 主 VM（cases/steps/toolbox/load/save/DBC 下拉）

**修改**
- `src/PeakCan.Host.App/ViewModels/HilStudioViewModel.cs` — 加 `SuiteBuilder` 子 VM（ctor 注入 `DbcEncodeService`）
- `src/PeakCan.Host.App/Windows/HilStudioWindow.xaml` — col2 占位 Border 替换为 Test Suite Builder 面板
- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` — `HilStudioViewModel` ctor 新参自动解析（AddSingleton 无 factory，DI 自动）

**测试（tests/PeakCan.Host.Core.Tests/ + tests/PeakCan.Host.App.Tests/）**
- `HIL/StepParams/StepParametersExporterTests.cs`（Core，12 kind round-trip）
- `ViewModels/TestSuiteBuilder/EditableModelTests.cs`（App）
- `ViewModels/TestSuiteBuilder/TestSuiteBuilderViewModelTests.cs`（App，round-trip + DBC 下拉 + composer）

---

### Task 1: StepParametersExporter（Core，强类型→dict）

**Files:**
- Create: `src/PeakCan.Host.Core/HIL/StepParams/StepParametersExporter.cs`
- Test: `tests/PeakCan.Host.Core.Tests/HIL/StepParams/StepParametersExporterTests.cs`

**Interfaces:**
- Produces: `public static class StepParametersExporter { public static IReadOnlyDictionary<string, object> FromParameters(StepParameters p) }`
- Consumes: `StepParameters` 12 个子类 + `CanId`（`Raw`/`Format`）+ `FrameFormat`（均在 `PeakCan.Host.Core.HIL`）

- [ ] **Step 1: 写失败测试**

`tests/PeakCan.Host.Core.Tests/HIL/StepParams/StepParametersExporterTests.cs`:
```csharp
using FluentAssertions;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.Core.Tests.HIL.StepParams;

public class StepParametersExporterTests
{
    [Fact]
    public void RoundTrip_SendFrame()
    {
        var p = new SendFrameStep(new CanId(0x123, FrameFormat.Standard), new byte[] { 0x01, 0x02 }, Fd: true, Extended: false);
        var dict = StepParametersExporter.FromParameters(p);
        StepParametersFactory.Create(TestCaseStepKind.SendFrame, dict).Should().BeEquivalentTo(p);
    }

    [Fact]
    public void RoundTrip_ExpectFrame_With_DataMask()
    {
        var p = new ExpectFrameStep(new CanId(0x456, FrameFormat.Extended), new byte[] { 0x00, 0x7E }, 5000);
        StepParametersFactory.Create(TestCaseStepKind.WaitForFrame, StepParametersExporter.FromParameters(p))
            .Should().BeEquivalentTo(p);
    }

    [Fact]
    public void RoundTrip_ExpectFrame_Null_DataMask()
    {
        var p = new ExpectFrameStep(new CanId(0x456, FrameFormat.Standard), null, 5000);
        StepParametersFactory.Create(TestCaseStepKind.WaitForFrame, StepParametersExporter.FromParameters(p))
            .Should().BeEquivalentTo(p);
    }

    [Fact]
    public void RoundTrip_WaitForSignal()
    {
        var p = new WaitForSignalStep("BMS_Status.SOC", 80.5, 1.0, 3000);
        StepParametersFactory.Create(TestCaseStepKind.WaitForSignal, StepParametersExporter.FromParameters(p))
            .Should().BeEquivalentTo(p);
    }

    [Fact]
    public void RoundTrip_AssertSignal()
    {
        var p = new AssertSignalStep("M1.Speed", 100, 0.5);
        StepParametersFactory.Create(TestCaseStepKind.AssertSignal, StepParametersExporter.FromParameters(p))
            .Should().BeEquivalentTo(p);
    }

    [Fact]
    public void RoundTrip_AssertRange()
    {
        var p = new AssertRangeStep("M1.Temp", 10, 90);
        StepParametersFactory.Create(TestCaseStepKind.AssertRange, StepParametersExporter.FromParameters(p))
            .Should().BeEquivalentTo(p);
    }

    [Fact]
    public void RoundTrip_AssertResponseTime()
    {
        var p = new AssertResponseTimeStep(new CanId(0x7E0, FrameFormat.Standard), new CanId(0x7E8, FrameFormat.Standard), 100);
        StepParametersFactory.Create(TestCaseStepKind.AssertResponseTime, StepParametersExporter.FromParameters(p))
            .Should().BeEquivalentTo(p);
    }

    [Fact]
    public void RoundTrip_AssertDtc_With_And_Without_Code()
    {
        StepParametersFactory.Create(TestCaseStepKind.AssertDtc, StepParametersExporter.FromParameters(new AssertDtcStep(0x22, true)))
            .Should().BeEquivalentTo(new AssertDtcStep(0x22, true));
        StepParametersFactory.Create(TestCaseStepKind.AssertDtc, StepParametersExporter.FromParameters(new AssertDtcStep(null, false)))
            .Should().BeEquivalentTo(new AssertDtcStep(null, false));
    }

    [Fact]
    public void RoundTrip_AssertNrc()
    {
        var p = new AssertNrcStep(0x22, 0x31);
        StepParametersFactory.Create(TestCaseStepKind.AssertNrc, StepParametersExporter.FromParameters(p))
            .Should().BeEquivalentTo(p);
    }

    [Fact]
    public void RoundTrip_Delay()
    {
        var p = new DelayStep(250);
        StepParametersFactory.Create(TestCaseStepKind.Delay, StepParametersExporter.FromParameters(p))
            .Should().BeEquivalentTo(p);
    }

    [Fact]
    public void RoundTrip_Comment()
    {
        var p = new CommentStep("check engine on");
        StepParametersFactory.Create(TestCaseStepKind.Comment, StepParametersExporter.FromParameters(p))
            .Should().BeEquivalentTo(p);
    }

    [Fact]
    public void RoundTrip_InjectFault_All_Fields()
    {
        var p = new InjectFaultStep(
            new CanId(0x123, FrameFormat.Standard), FaultType.Corrupt, 0.5, 10,
            new[] { 0, 2 }, 0xFF, "fault1", FaultDirection.Both);
        StepParametersFactory.Create(TestCaseStepKind.InjectFault, StepParametersExporter.FromParameters(p))
            .Should().BeEquivalentTo(p);
    }

    [Fact]
    public void RoundTrip_InjectFault_Defaults_Optional()
    {
        var p = new InjectFaultStep(new CanId(0x123, FrameFormat.Standard), FaultType.Drop, 1.0, 0, null, 0xFF, null);
        StepParametersFactory.Create(TestCaseStepKind.InjectFault, StepParametersExporter.FromParameters(p))
            .Should().BeEquivalentTo(p);
    }

    [Fact]
    public void RoundTrip_ClearFault_With_And_Without_Id()
    {
        StepParametersFactory.Create(TestCaseStepKind.ClearFault, StepParametersExporter.FromParameters(new ClearFaultStep("f1")))
            .Should().BeEquivalentTo(new ClearFaultStep("f1"));
        StepParametersFactory.Create(TestCaseStepKind.ClearFault, StepParametersExporter.FromParameters(new ClearFaultStep(null)))
            .Should().BeEquivalentTo(new ClearFaultStep(null));
    }
}
```
注：若 `CanId` 不在 `PeakCan.Host.Core.HIL` 而在 `PeakCan.Host.Core`，测试加 `using PeakCan.Host.Core;`；`FrameFormat`/`CanId` 的实际命名空间以编译为准。

- [ ] **Step 2: 运行确认失败**
Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~StepParametersExporterTests"`
Expected: FAIL — `StepParametersExporter` 不存在。

- [ ] **Step 3: 实现 exporter**

`src/PeakCan.Host.Core/HIL/StepParams/StepParametersExporter.cs`:
```csharp
using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.Core.HIL;

/// <summary>
/// Inverse of <see cref="StepParametersFactory"/>: converts a strongly-typed
/// <see cref="StepParameters"/> back into the dictionary shape the factory
/// consumes. Guarantees Create(kind, FromParameters(p)) == p.
/// 键名/类型必须与 StepParametersFactory.Create 的读取逻辑严格一致。
/// </summary>
public static class StepParametersExporter
{
    public static IReadOnlyDictionary<string, object> FromParameters(StepParameters p) => p switch
    {
        SendFrameStep s => new Dictionary<string, object>
        {
            ["Id"] = CanIdHex(s.Id), ["Extended"] = s.Extended, ["Fd"] = s.Fd,
            ["Data"] = Convert.ToHexString(s.Data),
        },
        ExpectFrameStep e => Build(e.Id, d => d["TimeoutMs"] = e.TimeoutMs, e.DataMask),
        WaitForSignalStep w => new Dictionary<string, object>
        {
            ["SignalName"] = w.SignalName, ["Expected"] = w.Expected,
            ["Tolerance"] = w.Tolerance, ["TimeoutMs"] = w.TimeoutMs,
        },
        AssertSignalStep a => new Dictionary<string, object>
        {
            ["SignalName"] = a.SignalName, ["Expected"] = a.Expected, ["Tolerance"] = a.Tolerance,
        },
        AssertRangeStep r => new Dictionary<string, object>
        {
            ["SignalName"] = r.SignalName, ["Min"] = r.Min, ["Max"] = r.Max,
        },
        AssertResponseTimeStep t => new Dictionary<string, object>
        {
            ["ReqId"] = CanIdHex(t.ReqId), ["ReqExtended"] = IsExtended(t.ReqId),
            ["RespId"] = CanIdHex(t.RespId), ["RespExtended"] = IsExtended(t.RespId),
            ["MaxMs"] = t.MaxMs,
        },
        AssertDtcStep d => Build(d.ExpectPresent, d.DtcCode),
        AssertNrcStep n => new Dictionary<string, object>
        {
            ["ServiceId"] = (int)n.ServiceId, ["ExpectedNrc"] = (int)n.ExpectedNrc,
        },
        DelayStep dly => new Dictionary<string, object> { ["Milliseconds"] = dly.Milliseconds },
        CommentStep c => new Dictionary<string, object> { ["Text"] = c.Text },
        InjectFaultStep f => Build(f),
        ClearFaultStep cf => Build(cf),
        _ => throw new ArgumentException($"Unknown step parameters type: {p.GetType().Name}", nameof(p)),
    };

    private static Dictionary<string, object> Build(CanId id, Action<Dictionary<string, object>> extra, byte[]? dataMask)
    {
        var d = new Dictionary<string, object>
        {
            ["Id"] = CanIdHex(id), ["Extended"] = IsExtended(id),
            ["TimeoutMs"] = default(int),  // 占位, extra 覆盖
        };
        if (dataMask is { } mask) d["DataMask"] = Convert.ToHexString(mask);
        extra(d);
        return d;
    }

    private static Dictionary<string, object> Build(bool expectPresent, ushort? dtcCode)
    {
        var d = new Dictionary<string, object> { ["ExpectPresent"] = expectPresent };
        if (dtcCode is { } code) d["DtcCode"] = code;
        return d;
    }

    private static Dictionary<string, object> Build(InjectFaultStep f)
    {
        var d = new Dictionary<string, object>
        {
            ["CanId"] = CanIdHex(f.CanId), ["Extended"] = IsExtended(f.CanId),
            ["FaultType"] = f.FaultType.ToString(),
            ["Probability"] = f.Probability, ["DelayMs"] = f.DelayMs,
            ["CorruptXorMask"] = $"0x{f.CorruptXorMask:X2}",
            ["Direction"] = f.Direction.ToString(),
        };
        if (f.CorruptByteIndices is { Length: > 0 } idx)
            d["CorruptByteIndices"] = idx.Select(i => (object)i).ToArray(); // 必须 object[], 不能 int[] (运行时 cast 失败)
        if (f.FaultId is { } fid) d["FaultId"] = fid;
        return d;
    }

    private static Dictionary<string, object> Build(ClearFaultStep cf)
    {
        var d = new Dictionary<string, object>();
        if (cf.FaultId is { } fid) d["FaultId"] = fid;
        return d;
    }

    private static string CanIdHex(CanId id) => $"0x{id.Raw:X}";
    private static bool IsExtended(CanId id) => id.Format == FrameFormat.Extended;
}
```
> 注：上面 `ExpectFrameStep` 的 `Build` 辅助里用 `["TimeoutMs"] = default(int)` 占位再 `extra` 覆盖——为可读性可改为直接初始化 `["TimeoutMs"] = e.TimeoutMs` 内联（实现时选其一，保证 round-trip 通过即可）。

- [ ] **Step 4: 运行确认通过**
Run: 同上 filter。Expected: PASS（14 tests）

- [ ] **Step 5: Commit**
```bash
git add src/PeakCan.Host.Core/HIL/StepParams/StepParametersExporter.cs tests/PeakCan.Host.Core.Tests/HIL/StepParams/StepParametersExporterTests.cs
git commit -m "feat(studio): StepParametersExporter — inverse of StepParametersFactory for suite editing"
```

---

### Task 2: 可编辑编辑模型（EditableTestCaseStep + EditableTestCase）

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/TestSuiteBuilder/EditableTestCaseStep.cs`
- Create: `src/PeakCan.Host.App/ViewModels/TestSuiteBuilder/EditableTestCase.cs`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/TestSuiteBuilder/EditableModelTests.cs`

**Interfaces:**
- Consumes: `StepParametersExporter`（Task 1）、`StepParametersFactory`、`TestCaseStep.Create`、`TestCase`/`TestSuite` 记录
- Produces: `EditableTestCaseStep`（`TestCaseStepKind Kind`、`string? Label`、`Dictionary<string,object> Params`、`TestCaseStep ToStep()`、`static EditableTestCaseStep New(kind)`、`static FromStep(TestCaseStep)`）
- Produces: `EditableTestCase`（`Id/Name/Description/Tags` + `ObservableCollection<EditableTestCaseStep> Steps` + `TestCase ToCase()`、`static FromCase(TestCase)`）

- [ ] **Step 1: 写失败测试**

`tests/PeakCan.Host.App.Tests/ViewModels/TestSuiteBuilder/EditableModelTests.cs`:
```csharp
using FluentAssertions;
using PeakCan.Host.App.ViewModels.TestSuiteBuilder;
using PeakCan.Host.Core.HIL;

namespace PeakCan.Host.App.Tests.ViewModels.TestSuiteBuilder;

public class EditableModelTests
{
    [Fact]
    public void New_SendFrame_Has_Defaults_And_Builds_Valid_Step()
    {
        var step = EditableTestCaseStep.New(TestCaseStepKind.SendFrame);
        step.Kind.Should().Be(TestCaseStepKind.SendFrame);
        var built = step.ToStep();
        built.Kind.Should().Be(TestCaseStepKind.SendFrame);
        built.Parameters.Should().BeOfType<SendFrameStep>();
    }

    [Fact]
    public void FromStep_Then_ToStep_RoundTrips()
    {
        var original = TestCaseStep.Create(
            new AssertSignalStep("M1.Speed", 100, 0.5), "check speed");
        var editable = EditableTestCaseStep.FromStep(original);
        editable.ToStep().Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Editing_Params_Reflects_In_ToStep()
    {
        var step = EditableTestCaseStep.New(TestCaseStepKind.AssertSignal);
        step.Params["SignalName"] = "M1.Speed";
        step.Params["Expected"] = 88.0;
        step.ToStep().Parameters.Should().BeEquivalentTo(new AssertSignalStep("M1.Speed", 88.0, 0));
    }

    [Fact]
    public void TestCase_RoundTrip()
    {
        var c = new TestCase(
            Id: "case_1", Name: "TP", Description: "d", PreConditions: null,
            Steps: new List<TestCaseStep> { TestCaseStep.Create(new DelayStep(100)) },
            PostConditions: null, Tags: new[] { "smoke" }, TimeoutMs: 5000,
            CaseFixtureKeys: null);
        var editable = EditableTestCase.FromCase(c);
        editable.ToCase().Should().BeEquivalentTo(c);
    }
}
```

- [ ] **Step 2: 运行确认失败**
Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~EditableModelTests"`
Expected: FAIL — 类不存在。

- [ ] **Step 3: 实现**

`EditableTestCaseStep.cs`:
```csharp
using PeakCan.Host.Core.HIL;

namespace PeakCan.Host.App.ViewModels.TestSuiteBuilder;

/// <summary>
/// 可编辑测试步骤。Params 是 StepParametersFactory.Create 期望形态的 dict,
/// 保存时复用工厂构建强类型, 保证与 runtime 字节一致。
/// </summary>
public sealed class EditableTestCaseStep
{
    public TestCaseStepKind Kind { get; }
    public string? Label { get; set; }
    public Dictionary<string, object> Params { get; }

    public EditableTestCaseStep(TestCaseStepKind kind, string? label, Dictionary<string, object>? paramDefaults = null)
    {
        Kind = kind;
        Label = label;
        Params = paramDefaults ?? StepFieldDescriptors.DefaultsFor(kind);
    }

    public TestCaseStep ToStep()
        => TestCaseStep.Create(StepParametersFactory.Create(Kind, Params), Label);

    public static EditableTestCaseStep New(TestCaseStepKind kind)
        => new(kind, null);

    public static EditableTestCaseStep FromStep(TestCaseStep step)
        => new(step.Kind, step.Label,
            new Dictionary<string, object>(StepParametersExporter.FromParameters(step.Parameters)));
}
```

`EditableTestCase.cs`:
```csharp
using System.Collections.ObjectModel;
using PeakCan.Host.Core.HIL;

namespace PeakCan.Host.App.ViewModels.TestSuiteBuilder;

/// <summary>可编辑测试用例。</summary>
public sealed class EditableTestCase
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Tags { get; } = new();
    public ObservableCollection<EditableTestCaseStep> Steps { get; } = new();

    public TestCase ToCase() => new(
        Id, Name, Description,
        PreConditions: null,
        Steps: Steps.Select(s => s.ToStep()).ToList(),
        PostConditions: null,
        Tags: Tags.ToArray(),
        TimeoutMs: 0,
        CaseFixtureKeys: null);

    public static EditableTestCase FromCase(TestCase c) => new()
    {
        Id = c.Id, Name = c.Name, Description = c.Description ?? "",
        // Tags
    };
}
```
> 注：`FromCase` 需把 `c.Tags`（`IReadOnlyList<string>`?）填充进 `Tags` 列表；`PreConditions/PostConditions/CaseFixtureKeys/TimeoutMs` 在 Phase 2 只读保留（`ToCase` 回填 null/0 会导致这些字段丢失——见 Task 3 的 suite-level 保留策略）。若要求完全保真，需在 VM 层持有这些 pass-through 字段。实现时在 `EditableTestCase` 加 `PreConditions/PostConditions/TimeoutMs/CaseFixtureKeys` 属性并在 ToCase/FromCase 双向保留。

- [ ] **Step 4: 运行确认通过**
Run: 同上 filter。Expected: PASS（4 tests）

- [ ] **Step 5: Commit**
```bash
git add src/PeakCan.Host.App/ViewModels/TestSuiteBuilder/EditableTestCaseStep.cs src/PeakCan.Host.App/ViewModels/TestSuiteBuilder/EditableTestCase.cs tests/PeakCan.Host.App.Tests/ViewModels/TestSuiteBuilder/EditableModelTests.cs
git commit -m "feat(studio): editable test case/step models with factory round-trip"
```

---

### Task 3: 字段描述符 + 默认值（StepFieldDescriptor / StepFieldDescriptors）

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/TestSuiteBuilder/StepFieldDescriptor.cs`
- Create: `src/PeakCan.Host.App/ViewModels/TestSuiteBuilder/StepFieldDescriptors.cs`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/TestSuiteBuilder/EditableModelTests.cs`（追加）

**Interfaces:**
- Produces: `public enum StepFieldKind { Text, Number, Bool, Enum, CanId, DbcSignal, HexBytes, IntList }`
- Produces: `public sealed record StepFieldDescriptor(string Key, string Label, StepFieldKind Kind, string[]? EnumValues = null)`
- Produces: `public static class StepFieldDescriptors { IReadOnlyList<StepFieldDescriptor> For(TestCaseStepKind); Dictionary<string,object> DefaultsFor(TestCaseStepKind); IReadOnlyList<TestCaseStepKind> AllKinds }`

- [ ] **Step 1: 写测试**（追加到 EditableModelTests.cs）
```csharp
    [Fact]
    public void Every_Kind_Has_Descriptors_And_Defaults_That_Build()
    {
        foreach (var kind in StepFieldDescriptors.AllKinds)
        {
            StepFieldDescriptors.For(kind).Should().NotBeEmpty($"{kind} needs at least one field");
            var step = EditableTestCaseStep.New(kind);
            step.ToStep().Kind.Should().Be(kind, $"{kind} defaults must build a valid step");
        }
    }
```

- [ ] **Step 2: 运行确认失败**
Run: 同上 filter。Expected: FAIL — 类不存在。

- [ ] **Step 3: 实现**

`StepFieldDescriptor.cs`:
```csharp
namespace PeakCan.Host.App.ViewModels.TestSuiteBuilder;

public enum StepFieldKind { Text, Number, Bool, Enum, CanId, DbcSignal, HexBytes, IntList }

public sealed record StepFieldDescriptor(string Key, string Label, StepFieldKind Kind, string[]? EnumValues = null);
```

`StepFieldDescriptors.cs`:
```csharp
using PeakCan.Host.Core.HIL;

namespace PeakCan.Host.App.ViewModels.TestSuiteBuilder;

/// <summary>
/// 12 个 step kind 的字段描述符 + 默认 dict。键名必须与 StepParametersFactory.Create 一致。
/// </summary>
public static class StepFieldDescriptors
{
    public static IReadOnlyList<TestCaseStepKind> AllKinds { get; } = new[]
    {
        TestCaseStepKind.SendFrame, TestCaseStepKind.WaitForFrame,
        TestCaseStepKind.WaitForSignal, TestCaseStepKind.AssertSignal,
        TestCaseStepKind.AssertRange, TestCaseStepKind.AssertResponseTime,
        TestCaseStepKind.AssertDtc, TestCaseStepKind.AssertNrc,
        TestCaseStepKind.Delay, TestCaseStepKind.Comment,
        TestCaseStepKind.InjectFault, TestCaseStepKind.ClearFault,
    };

    private static readonly string[] FaultTypes = { "Drop", "Delay", "Corrupt", "Duplicate" };
    private static readonly string[] Directions = { "Send", "Receive", "Both" };

    public static IReadOnlyList<StepFieldDescriptor> For(TestCaseStepKind kind) => kind switch
    {
        TestCaseStepKind.SendFrame => new[]
        {
            new StepFieldDescriptor("Id", "CAN ID", StepFieldKind.CanId),
            new StepFieldDescriptor("Fd", "CAN FD", StepFieldKind.Bool),
            new StepFieldDescriptor("Extended", "Extended ID", StepFieldKind.Bool),
            new StepFieldDescriptor("Data", "Data (hex)", StepFieldKind.HexBytes),
        },
        TestCaseStepKind.WaitForFrame => new[]
        {
            new StepFieldDescriptor("Id", "CAN ID", StepFieldKind.CanId),
            new StepFieldDescriptor("DataMask", "Data mask (hex, optional)", StepFieldKind.HexBytes),
            new StepFieldDescriptor("TimeoutMs", "Timeout (ms)", StepFieldKind.Number),
        },
        TestCaseStepKind.WaitForSignal => new[]
        {
            new StepFieldDescriptor("SignalName", "Signal", StepFieldKind.DbcSignal),
            new StepFieldDescriptor("Expected", "Expected", StepFieldKind.Number),
            new StepFieldDescriptor("Tolerance", "Tolerance", StepFieldKind.Number),
            new StepFieldDescriptor("TimeoutMs", "Timeout (ms)", StepFieldKind.Number),
        },
        TestCaseStepKind.AssertSignal => new[]
        {
            new StepFieldDescriptor("SignalName", "Signal", StepFieldKind.DbcSignal),
            new StepFieldDescriptor("Expected", "Expected", StepFieldKind.Number),
            new StepFieldDescriptor("Tolerance", "Tolerance", StepFieldKind.Number),
        },
        TestCaseStepKind.AssertRange => new[]
        {
            new StepFieldDescriptor("SignalName", "Signal", StepFieldKind.DbcSignal),
            new StepFieldDescriptor("Min", "Min", StepFieldKind.Number),
            new StepFieldDescriptor("Max", "Max", StepFieldKind.Number),
        },
        TestCaseStepKind.AssertResponseTime => new[]
        {
            new StepFieldDescriptor("ReqId", "Request ID", StepFieldKind.CanId),
            new StepFieldDescriptor("RespId", "Response ID", StepFieldKind.CanId),
            new StepFieldDescriptor("MaxMs", "Max (ms)", StepFieldKind.Number),
        },
        TestCaseStepKind.AssertDtc => new[]
        {
            new StepFieldDescriptor("DtcCode", "DTC (hex, optional)", StepFieldKind.Text),
            new StepFieldDescriptor("ExpectPresent", "Expect present", StepFieldKind.Bool),
        },
        TestCaseStepKind.AssertNrc => new[]
        {
            new StepFieldDescriptor("ServiceId", "Service ID (hex)", StepFieldKind.Text),
            new StepFieldDescriptor("ExpectedNrc", "Expected NRC (hex)", StepFieldKind.Text),
        },
        TestCaseStepKind.Delay => new[]
        {
            new StepFieldDescriptor("Milliseconds", "Delay (ms)", StepFieldKind.Number),
        },
        TestCaseStepKind.Comment => new[]
        {
            new StepFieldDescriptor("Text", "Comment", StepFieldKind.Text),
        },
        TestCaseStepKind.InjectFault => new[]
        {
            new StepFieldDescriptor("CanId", "CAN ID", StepFieldKind.CanId),
            new StepFieldDescriptor("FaultType", "Fault type", StepFieldKind.Enum, FaultTypes),
            new StepFieldDescriptor("Probability", "Probability (0-1)", StepFieldKind.Number),
            new StepFieldDescriptor("DelayMs", "Delay (ms)", StepFieldKind.Number),
            new StepFieldDescriptor("CorruptByteIndices", "Corrupt byte indices (csv)", StepFieldKind.IntList),
            new StepFieldDescriptor("CorruptXorMask", "Corrupt XOR mask (hex)", StepFieldKind.HexBytes),
            new StepFieldDescriptor("FaultId", "Fault ID (optional)", StepFieldKind.Text),
            new StepFieldDescriptor("Direction", "Direction", StepFieldKind.Enum, Directions),
        },
        TestCaseStepKind.ClearFault => new[]
        {
            new StepFieldDescriptor("FaultId", "Fault ID (empty=all)", StepFieldKind.Text),
        },
        _ => Array.Empty<StepFieldDescriptor>(),
    };

    /// <summary>每 kind 的可构建默认 dict（键名与 StepParametersFactory.Create 一致）。</summary>
    public static Dictionary<string, object> DefaultsFor(TestCaseStepKind kind) => kind switch
    {
        TestCaseStepKind.SendFrame => new() { ["Id"] = "0x0", ["Extended"] = false, ["Fd"] = false, ["Data"] = "" },
        TestCaseStepKind.WaitForFrame => new() { ["Id"] = "0x0", ["Extended"] = false, ["TimeoutMs"] = 5000 },
        TestCaseStepKind.WaitForSignal => new() { ["SignalName"] = "", ["Expected"] = 0d, ["Tolerance"] = 0d, ["TimeoutMs"] = 5000 },
        TestCaseStepKind.AssertSignal => new() { ["SignalName"] = "", ["Expected"] = 0d, ["Tolerance"] = 0d },
        TestCaseStepKind.AssertRange => new() { ["SignalName"] = "", ["Min"] = 0d, ["Max"] = 0d },
        TestCaseStepKind.AssertResponseTime => new() { ["ReqId"] = "0x7E0", ["ReqExtended"] = false, ["RespId"] = "0x7E8", ["RespExtended"] = false, ["MaxMs"] = 100 },
        TestCaseStepKind.AssertDtc => new() { ["ExpectPresent"] = true },
        TestCaseStepKind.AssertNrc => new() { ["ServiceId"] = 0, ["ExpectedNrc"] = 0 },
        TestCaseStepKind.Delay => new() { ["Milliseconds"] = 100 },
        TestCaseStepKind.Comment => new() { ["Text"] = "" },
        TestCaseStepKind.InjectFault => new() { ["CanId"] = "0x0", ["Extended"] = false, ["FaultType"] = "Drop", ["Probability"] = 1d, ["DelayMs"] = 0, ["CorruptXorMask"] = "0xFF", ["Direction"] = "Send" },
        TestCaseStepKind.ClearFault => new() { },
        _ => new Dictionary<string, object>(),
    };
}
```
> 注：`AssertNrc` 的 `ServiceId`/`ExpectedNrc` 默认用 `int`，工厂 `Convert.ToByte` 可处理；`Number` 字段 dict 存 `double`，Text 字段存 `string`，Bool 存 `bool`——与工厂读取类型一致。`IntList` 的 `CorruptByteIndices` 默认省略（工厂 `TryGetValue` 兜底 null）。

- [ ] **Step 4: 运行确认通过**
Run: 同上 filter。Expected: PASS（5 tests）

- [ ] **Step 5: Commit**
```bash
git add src/PeakCan.Host.App/ViewModels/TestSuiteBuilder/StepFieldDescriptor.cs src/PeakCan.Host.App/ViewModels/TestSuiteBuilder/StepFieldDescriptors.cs tests/PeakCan.Host.App.Tests/ViewModels/TestSuiteBuilder/EditableModelTests.cs
git commit -m "feat(studio): step field descriptors + defaults for all 12 kinds"
```

---

### Task 4: TestSuiteBuilderViewModel（round-trip + cases/steps/toolbox + DBC 下拉）

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/TestSuiteBuilder/TestSuiteBuilderViewModel.cs`
- Create: `src/PeakCan.Host.App/ViewModels/TestSuiteBuilder/RoundTripFlow.partial.cs`
- Create: `src/PeakCan.Host.App/ViewModels/TestSuiteBuilder/DbcOptionsFlow.partial.cs`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/TestSuiteBuilder/TestSuiteBuilderViewModelTests.cs`

**Interfaces:**
- Consumes: `EditableTestCaseStep/EditableTestCase`（Task 2）、`StepFieldDescriptors`（Task 3）、`HILJsonOptions.Default`、`TestSuite` 记录、`DbcService`（`Current`/`DbcLoaded`）、`IFileDialogService`
- Produces: `TestSuiteBuilderViewModel`（`ObservableCollection<EditableTestCase> Cases`、`SelectedCase`、`SelectedStep`、`Status`、`ErrorMessage`、`AvailableKinds`、`DbcMessages: IReadOnlyList<DbcMessageOption>`、`DbcSignals: IReadOnlyList<string>`、命令 `AddStep/RemoveStep/MoveStepUp/MoveStepDown/AddCase/RemoveCase/OpenAsync/SaveAsync/SaveAsAsync`、`LoadFromText(string)/ToSuite()`）

- [ ] **Step 1: 写失败测试**

`tests/PeakCan.Host.App.Tests/ViewModels/TestSuiteBuilder/TestSuiteBuilderViewModelTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels.TestSuiteBuilder;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Serialization;

namespace PeakCan.Host.App.Tests.ViewModels.TestSuiteBuilder;

public class TestSuiteBuilderViewModelTests
{
    private const string SampleSuite = """
    {
      "name": "Smoke",
      "cases": [ { "id": "c1", "name": "TP", "steps": [ { "parameters": { "$kind": "delay", "Milliseconds": 100 } } ] } ],
      "config": { "failurePolicy": "ContinueAll", "continueAfterSetupFailure": true }
    }
    """;

    [Fact]
    public void LoadFromText_Populates_Cases()
    {
        var vm = NewVm();
        vm.LoadFromText(SampleSuite);
        vm.Cases.Should().HaveCount(1);
        vm.Cases[0].Steps.Should().HaveCount(1);
        vm.Cases[0].Steps[0].Kind.Should().Be(TestCaseStepKind.Delay);
    }

    [Fact]
    public void ToSuite_RoundTrips_Through_HILJsonOptions()
    {
        var vm = NewVm();
        vm.LoadFromText(SampleSuite);
        var json = System.Text.Json.JsonSerializer.Serialize(vm.ToSuite(), HILJsonOptions.Default);
        var reparsed = System.Text.Json.JsonSerializer.Deserialize<TestSuite>(json, HILJsonOptions.Default);
        reparsed!.Cases.Should().HaveCount(1);
        reparsed.Cases[0].Steps[0].Parameters.Should().BeOfType<DelayStep>();
    }

    [Fact]
    public void AddStep_Appends_To_Selected_Case()
    {
        var vm = NewVm();
        vm.LoadFromText(SampleSuite);
        vm.SelectedCase = vm.Cases[0];
        vm.AddStepCommand.Execute(TestCaseStepKind.AssertSignal);
        vm.SelectedCase.Steps.Should().HaveCount(2);
    }

    [Fact]
    public void MoveStepUp_Reorders()
    {
        var vm = NewVm();
        vm.LoadFromText(SampleSuite);
        vm.SelectedCase = vm.Cases[0];
        vm.AddStepCommand.Execute(TestCaseStepKind.Delay);
        vm.SelectedStep = vm.SelectedCase.Steps[1];
        vm.MoveStepUpCommand.Execute(null);
        vm.SelectedCase.Steps[0].Kind.Should().Be(TestCaseStepKind.Delay);
    }

    [Fact]
    public void DbcLoaded_Refreshes_Signal_Options()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var doc = new PeakCan.Host.Core.Dbc.DbcDocument(
            Version: "", Nodes: new List<PeakCan.Host.Core.Dbc.Node>(),
            Messages: new List<PeakCan.Host.Core.Dbc.Message>
            {
                new(0x100, "M1", 8, "ECU1",
                    new List<PeakCan.Host.Core.Dbc.Signal> { new("Speed", 0, 16, PeakCan.Host.Core.Dbc.ByteOrder.LittleEndian, PeakCan.Host.Core.Dbc.ValueType.Unsigned, 1, 0, 0, 6553.5, "", Array.Empty<string>()) },
                    IsMultiplexed: false, MultiplexorSignalIndex: null),
            },
            MessagesById: new Dictionary<uint, PeakCan.Host.Core.Dbc.Message>(),
            ValueTables: new Dictionary<string, PeakCan.Host.Core.Dbc.ValueTable>());
        svc.SetCurrentForTests(doc);
        var vm = new TestSuiteBuilderViewModel(svc, NullLogger<TestSuiteBuilderViewModel>.Instance, null);

        vm.DbcSignals.Should().Contain("M1.Speed");
    }

    private static TestSuiteBuilderViewModel NewVm()
        => new(new DbcService(NullLogger<DbcService>.Instance),
            NullLogger<TestSuiteBuilderViewModel>.Instance, null);
}
```

- [ ] **Step 2: 运行确认失败**
Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~TestSuiteBuilderViewModelTests"`
Expected: FAIL — VM 不存在。

- [ ] **Step 3: 实现**

`TestSuiteBuilderViewModel.cs`（主文件）:
```csharp
using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Serialization;

namespace PeakCan.Host.App.ViewModels.TestSuiteBuilder;

public sealed partial class TestSuiteBuilderViewModel : ObservableObject
{
    private readonly DbcService _svc;
    private readonly IFileDialogService _fileDialog;
    private readonly ILogger<TestSuiteBuilderViewModel> _logger;
    private string? _suitePath;

    // suite-level pass-through 字段（保真）
    public IReadOnlyList<string> GlobalCaseFixtureKeys { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> SuiteFixtureKeys { get; private set; } = Array.Empty<string>();
    public int TimeoutMs { get; private set; }

    public ObservableCollection<EditableTestCase> Cases { get; } = new();
    public IReadOnlyList<TestCaseStepKind> AvailableKinds => StepFieldDescriptors.AllKinds;

    [ObservableProperty] private EditableTestCase? _selectedCase;
    [ObservableProperty] private EditableTestCaseStep? _selectedStep;
    [ObservableProperty] private string _status = "No suite loaded";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _suiteName = "";

    // DBC 下拉（DbcOptionsFlow）
    [ObservableProperty] private IReadOnlyList<DbcMessageOption> _dbcMessages = Array.Empty<DbcMessageOption>();
    [ObservableProperty] private IReadOnlyList<string> _dbcSignals = Array.Empty<string>();

    public TestSuiteBuilderViewModel(
        DbcService svc, ILogger<TestSuiteBuilderViewModel> logger, IFileDialogService? fileDialog = null)
    {
        _svc = svc ?? throw new ArgumentNullException(nameof(svc));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _fileDialog = fileDialog ?? new WpfFileDialogService();
        _svc.DbcLoaded += (doc) => ((Action)RefreshDbcOptions).RunOnUi();
        RefreshDbcOptions();
    }

    [RelayCommand]
    private void AddStep(TestCaseStepKind kind)
    {
        if (SelectedCase is null) return;
        var step = EditableTestCaseStep.New(kind);
        SelectedCase.Steps.Add(step);
        SelectedStep = step;
    }

    [RelayCommand]
    private void RemoveStep()
    {
        if (SelectedCase is null || SelectedStep is null) return;
        var idx = SelectedCase.Steps.IndexOf(SelectedStep);
        SelectedCase.Steps.RemoveAt(idx);
        SelectedStep = idx < SelectedCase.Steps.Count ? SelectedCase.Steps[idx] : SelectedCase.Steps.LastOrDefault();
    }

    [RelayCommand]
    private void MoveStepUp() => MoveStep(-1);

    [RelayCommand]
    private void MoveStepDown() => MoveStep(+1);

    private void MoveStep(int delta)
    {
        if (SelectedCase is null || SelectedStep is null) return;
        var idx = SelectedCase.Steps.IndexOf(SelectedStep);
        var target = idx + delta;
        if (target < 0 || target >= SelectedCase.Steps.Count) return;
        SelectedCase.Steps.Move(idx, target);
        SelectedStep = SelectedCase.Steps[target];
    }

    [RelayCommand]
    private void AddCase()
    {
        var c = new EditableTestCase { Id = $"case_{Cases.Count + 1}", Name = "New Case" };
        Cases.Add(c);
        SelectedCase = c;
    }

    [RelayCommand]
    private void RemoveCase()
    {
        if (SelectedCase is null) return;
        Cases.Remove(SelectedCase);
        SelectedCase = Cases.LastOrDefault();
    }

    public TestSuite ToSuite() => new(
        SuiteName ?? "Untitled",
        Cases.Select(c => c.ToCase()).ToList(),
        GlobalCaseFixtureKeys, SuiteFixtureKeys,
        new TestSuiteConfig(FailurePolicy: TestSuiteConfig.FailurePolicyKind.ContinueAll, ContinueAfterSetupFailure: true),
        TimeoutMs);
}
```
> 注：`TestSuiteConfig` 构造签名/枚举名以编译为准（`FailurePolicy` 枚举在 `TestSuiteConfig` 内 or 顶层——实现时按实际调整）。若 `TestSuiteConfig` 有别的必需字段，补默认。

`RoundTripFlow.partial.cs`:
```csharp
using System.Text.Json;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Serialization;

namespace PeakCan.Host.App.ViewModels.TestSuiteBuilder;

public sealed partial class TestSuiteBuilderViewModel
{
    public void LoadFromText(string json)
    {
        try
        {
            var suite = JsonSerializer.Deserialize<TestSuite>(json, HILJsonOptions.Default)
                ?? throw new InvalidDataException("suite.json is empty");
            Cases.Clear();
            foreach (var c in suite.Cases) Cases.Add(EditableTestCase.FromCase(c));
            SuiteName = suite.Name;
            GlobalCaseFixtureKeys = suite.GlobalCaseFixtureKeys ?? Array.Empty<string>();
            SuiteFixtureKeys = suite.SuiteFixtureKeys ?? Array.Empty<string>();
            TimeoutMs = suite.TimeoutMs;
            SelectedCase = Cases.FirstOrDefault();
            SelectedStep = null;
            Status = $"Loaded {Cases.Count} case(s) from {_suitePath ?? "(text)"}";
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Status = "Load failed.";
        }
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        var path = _fileDialog.ShowOpenDialog("Test Suite JSON|*.json|All Files|*.*");
        if (path is null) return;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            _suitePath = path;
            LoadFromText(json);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Status = "Open failed.";
        }
    }

    [RelayCommand]
    private void Save() => SaveCore(_suitePath);

    [RelayCommand]
    private void SaveAs()
    {
        var dir = _suitePath is null ? null : Path.GetDirectoryName(_suitePath);
        var chosen = _fileDialog.ShowSaveDialog("Test Suite JSON|*.json", ".json", dir);
        if (chosen is null) return;
        SaveCore(chosen);
    }

    private void SaveCore(string? path)
    {
        if (string.IsNullOrEmpty(path)) { SaveAs(); return; }
        try
        {
            var json = JsonSerializer.Serialize(ToSuite(), HILJsonOptions.Default);
            File.WriteAllText(path, json);
            _suitePath = path;
            Status = $"Saved {path}";
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Status = "Save failed.";
        }
    }
}
```

`DbcOptionsFlow.partial.cs`:
```csharp
using PeakCan.Host.App.Services;

namespace PeakCan.Host.App.ViewModels.TestSuiteBuilder;

public sealed partial class TestSuiteBuilderViewModel
{
    private void RefreshDbcOptions()
    {
        var doc = _svc.Current;
        DbcMessages = doc?.Messages.Select(m => new DbcMessageOption(m.Id, $"0x{m.Id:X} {m.Name}")).ToList()
            ?? Array.Empty<DbcMessageOption>();
        DbcSignals = doc?.Messages
            .SelectMany(m => m.Signals.Select(s => $"{m.Name}.{s.Name}"))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? Array.Empty<string>();
    }
}

/// <summary>DBC 消息下拉选项。</summary>
public sealed record DbcMessageOption(uint Id, string Display)
{
    /// <summary>工厂期望的 CAN ID hex 字符串（"0x123"）。</summary>
    public string Hex => $"0x{Id:X}";
}
```
> 注：`DbcMessageOption.Id` 带 IDE bit（`Message.Id` 是合并后的 uint）——下拉选 CanId 字段时，ComboBox `SelectedValuePath="Hex"` 写回 `Params["Id"]`，与 `StepParametersFactory` 的 `StripHexPrefix`+`Convert.ToUInt32(...,16)` 一致。扩展帧选项的 IDE bit 需在 `Display`/`Hex` 里体现（如 `0x00000123`），实现时与 Phase 1 的 ID 格式化一致。

- [ ] **Step 4: 运行确认通过**
Run: 同上 filter。Expected: PASS（5 tests）

- [ ] **Step 5: Commit**
```bash
git add src/PeakCan.Host.App/ViewModels/TestSuiteBuilder/TestSuiteBuilderViewModel.cs src/PeakCan.Host.App/ViewModels/TestSuiteBuilder/RoundTripFlow.partial.cs src/PeakCan.Host.App/ViewModels/TestSuiteBuilder/DbcOptionsFlow.partial.cs tests/PeakCan.Host.App.Tests/ViewModels/TestSuiteBuilder/TestSuiteBuilderViewModelTests.cs
git commit -m "feat(studio): TestSuiteBuilderViewModel — suite round-trip + cases/steps/toolbox + DBC dropdowns"
```

---

### Task 5: SendFrame 信号组合器（DbcEncodeService）

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/TestSuiteBuilder/SendFrameComposerViewModel.cs`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/TestSuiteBuilder/SendFrameComposerViewModelTests.cs`

**Interfaces:**
- Consumes: `DbcEncodeService.Encode(Message, IReadOnlyDictionary<string,double>)`、`DbcService.Current`、`EditableTestCaseStep`（SendFrame 的 `Params`）
- Produces: `SendFrameComposerViewModel`（`DbcMessages`、`SelectedMessage`、`SignalValues`（每信号一个 double 编辑入口）、`ComposeHex()` 返回 hex 字符串写入 `Params["Data"]`）

- [ ] **Step 1: 写失败测试**

`tests/PeakCan.Host.App.Tests/ViewModels/TestSuiteBuilder/SendFrameComposerViewModelTests.cs`:
```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels.TestSuiteBuilder;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Tests.ViewModels.TestSuiteBuilder;

public class SendFrameComposerViewModelTests
{
    private static DbcService SvcWithMsgs()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var doc = new DbcDocument("", new List<Node>(),
            new List<Message>
            {
                new(0x100, "M1", 2, "ECU1",
                    new List<Signal>
                    {
                        new("Speed", 0, 16, ByteOrder.LittleEndian, ValueType.Unsigned, 1, 0, 0, 6553.5, "", Array.Empty<string>()),
                    },
                    IsMultiplexed: false, MultiplexorSignalIndex: null),
            },
            new Dictionary<uint, Message>(), new Dictionary<string, ValueTable>());
        svc.SetCurrentForTests(doc);
        return svc;
    }

    [Fact]
    public void ComposeHex_Encodes_Signal_Value_Into_Bytes()
    {
        var svc = SvcWithMsgs();
        var vm = new SendFrameComposerViewModel(svc, new DbcEncodeService(), NullLogger<SendFrameComposerViewModel>.Instance);
        vm.SelectedMessage = vm.DbcMessages[0];
        vm.SetSignalValue("Speed", 513.0);

        vm.ComposeHex().Should().Be("0102"); // 16-bit LE: 513 = 0x0201
    }

    [Fact]
    public void DbcLoaded_Without_Selection_Composes_Empty()
    {
        var vm = new SendFrameComposerViewModel(new DbcService(NullLogger<DbcService>.Instance), new DbcEncodeService(), NullLogger<SendFrameComposerViewModel>.Instance);
        vm.ComposeHex().Should().BeEmpty();
    }
}
```

- [ ] **Step 2: 运行确认失败**
Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~SendFrameComposerViewModelTests"`
Expected: FAIL — 类不存在。

- [ ] **Step 3: 实现**

`SendFrameComposerViewModel.cs`:
```csharp
using PeakCan.Host.App.Services;
using PeakCan.Host.Core.Dbc;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.App.ViewModels.TestSuiteBuilder;

/// <summary>
/// SendFrame 信号组合器：选 DBC 报文 → 填信号工程值 → DbcEncodeService.Encode → hex Data。
/// </summary>
public sealed class SendFrameComposerViewModel
{
    private readonly DbcService _svc;
    private readonly DbcEncodeService _encode;
    private readonly ILogger<SendFrameComposerViewModel> _logger;

    public IReadOnlyList<DbcMessageOption> DbcMessages { get; private set; } = Array.Empty<DbcMessageOption>();
    public DbcMessageOption? SelectedMessage { get; set; }
    private Message? _current;
    public IReadOnlyList<Signal> Signals { get; private set; } = Array.Empty<Signal>();
    private readonly Dictionary<string, double> _values = new(StringComparer.Ordinal);

    public SendFrameComposerViewModel(DbcService svc, DbcEncodeService encode, ILogger<SendFrameComposerViewModel> logger)
    {
        _svc = svc; _encode = encode; _logger = logger;
        RefreshMessages();
    }

    public void RefreshMessages()
    {
        var doc = _svc.Current;
        DbcMessages = doc?.Messages.Select(m => new DbcMessageOption(m.Id, $"0x{m.Id:X} {m.Name}")).ToList()
            ?? Array.Empty<DbcMessageOption>();
        SelectedMessage = DbcMessages.FirstOrDefault();
        OnSelectedMessageChanged();
    }

    public void SetSignalValue(string name, double value) => _values[name] = value;

    private void OnSelectedMessageChanged()
    {
        var doc = _svc.Current;
        _current = SelectedMessage is null || doc is null
            ? null : doc.Messages.FirstOrDefault(m => m.Id == SelectedMessage.Id);
        Signals = _current?.Signals ?? Array.Empty<Signal>();
        _values.Clear();
    }

    /// <summary>组合 Data 字节为 hex 字符串（"0102"）；无选中/无 DBC 返回空串。</summary>
    public string ComposeHex()
    {
        if (_current is null) return "";
        try
        {
            var bytes = _encode.Encode(_current, _values);
            return Convert.ToHexString(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SendFrame compose failed");
            return "";
        }
    }
}
```
> 注：`DbcMessageOption` 复用 Task 4 的 record（`Id`/`Display`/`Hex`）。`SetSignalValue` 是测试入口；UI 侧信号值编辑绑定到每信号的 value（见 Task 6）。

- [ ] **Step 4: 运行确认通过**
Run: 同上 filter。Expected: PASS（2 tests）

- [ ] **Step 5: Commit**
```bash
git add src/PeakCan.Host.App/ViewModels/TestSuiteBuilder/SendFrameComposerViewModel.cs tests/PeakCan.Host.App.Tests/ViewModels/TestSuiteBuilder/SendFrameComposerViewModelTests.cs
git commit -m "feat(studio): SendFrame signal composer via DbcEncodeService"
```

---

### Task 6: col2 UI（Test Suite Builder 面板）

**Files:**
- Modify: `src/PeakCan.Host.App/Windows/HilStudioWindow.xaml`（col2 占位 Border 替换为 Suite Builder）
- Modify: `src/PeakCan.Host.App/ViewModels/HilStudioViewModel.cs`（加 `SuiteBuilder` 子 VM + ctor `DbcEncodeService`）

**Interfaces:**
- Consumes: `HilStudioViewModel.SuiteBuilder`（`TestSuiteBuilderViewModel`）、`StepFieldDescriptors.For(kind)`、`SendFrameComposerViewModel`
- Produces: col2 面板 XAML

- [ ] **Step 1: HilStudioViewModel 挂子 VM**

`ViewModels/HilStudioViewModel.cs`：
- ctor 加 `DbcEncodeService encodeService` 参数；暴露 `public TestSuiteBuilderViewModel SuiteBuilder { get; }`，ctor 内 `SuiteBuilder = new TestSuiteBuilderViewModel(svc, logger, _fileDialog);`（composer 作为 SendFrame 面板子 VM，可在 UI 层构造或由 SuiteBuilder 暴露——实现时选择：若 composer 需要 encode，则 `SuiteBuilder` 构造时传 `encodeService`）。
> 简化：`TestSuiteBuilderViewModel` ctor 加可选 `DbcEncodeService? encodeService = null`，构造 `SendFrameComposerViewModel(svc, encodeService, logger)` 并暴露 `Composer`；SendFrame 面板绑定 `SuiteBuilder.Composer`。
- `HilStudioViewModel` ctor 签名变 `(DbcService, ILogger<HilStudioViewModel>, DbcEncodeService, IFileDialogService? = null)`；DI 自动解析（`AddSingleton<HilStudioViewModel>()` 无 factory）。**这会让 8 处测试调用点再次破坏**——同 commit 同步更新（用 `new DbcEncodeService()` 实参，注意测试文件里 `HilStudioViewModel` 构造处）。

- [ ] **Step 2: col2 面板 XAML**（`HilStudioWindow.xaml` col2 占位 Border 替换为：）

结构：
```
Grid（2 列：左 cases/toolbox，右 steps/属性）
├─ 左 StackPanel/DockPanel
│  ├─ 工具栏: Open / Save / SaveAs / AddCase / RemoveCase
│  ├─ Suite Name TextBox（绑 SuiteBuilder.SuiteName）
│  ├─ "Test Cases" ListBox（ItemsSource=Cases, SelectedItem=SelectedCase, Display=Name）
│  └─ "Toolbox" ItemsControl（AvailableKinds → 每个 Button Content=Kind, Command=AddStepCommand, CommandParameter=Kind）
├─ GridSplitter（垂直）
└─ 右 Grid（2 行：steps 上 / 属性下）
   ├─ 行0 "Steps" ListBox（ItemsSource=SelectedCase.Steps, SelectedItem=SelectedStep, 显示 Kind+Label）
   │   旁: Remove/MoveUp/MoveDown 按钮
   └─ 行1 属性面板（当 SelectedStep.Kind==SendFrame 时显示 Composer, 否则显示 descriptor 面板）
        ├─ SendFrame composer（绑定 SuiteBuilder.Composer: 报文 ComboBox + 信号值 DataGrid + "Compose" 按钮写 Params["Data"]）
        └─ descriptor 面板: ItemsControl over StepFieldDescriptors.For(SelectedStep.Kind)
            每字段 DataTemplate 按 FieldKind 切换:
              Text → TextBox {Binding SelectedStep.Params[key]}
              Number → TextBox（绑定 + 数字解析）
              Bool → CheckBox {Binding SelectedStep.Params[key]}
              Enum → ComboBox ItemsSource=EnumValues, SelectedItem={Binding Params[key]}
              CanId → ComboBox ItemsSource=SuiteBuilder.DbcMessages, DisplayMemberPath=Display, SelectedValuePath=Hex, SelectedValue={Binding Params["Id"]}
              DbcSignal → ComboBox ItemsSource=SuiteBuilder.DbcSignals, SelectedItem={Binding Params["SignalName"]}
              HexBytes → TextBox（monospace）
              IntList → TextBox（csv）
```
> 关键绑定技巧：WPF `{Binding SelectedStep.Params[key]}` 的 indexer 绑定可直接用；`CanId` 字段用 `SelectedValuePath=Hex` + `SelectedValue` 绑 `Params["Id"]`（DbcMessageOption.Hex 与工厂期望的 "0x..." 一致）。属性面板 DataTemplate 用 `DataTrigger` 按 `FieldKind` 切换控件（或把每字段包成 `FieldEditorViewModel` 再按 Kind 选模板——实现时选简洁可维护方案）。XAML 本任务不单测，靠构建 + 手动验收。

- [ ] **Step 3: 构建**
Run: `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 4: 修测试调用点 + 全量测试**
Run: `dotnet build PeakCan.Host.slnx` + `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj`
（`HilStudioViewModel` ctor 变体的 8 处测试调用点补 `new DbcEncodeService()`）

- [ ] **Step 5: Commit**
```bash
git add src/PeakCan.Host.App/ViewModels/HilStudioViewModel.cs src/PeakCan.Host.App/ViewModels/TestSuiteBuilder/TestSuiteBuilderViewModel.cs src/PeakCan.Host.App/Windows/HilStudioWindow.xaml tests/...
git commit -m "feat(studio): Test Suite Builder UI in col2 (toolbox + steps + descriptor property panel + composer)"
```

---

### Task 7: 端到端验证

**Files:** 无代码变更

- [ ] **Step 1: 全量测试**
Run: `dotnet test PeakCan.Host.slnx` — 无新增失败（4 个既有 TraceViewer/schema 失败除外）
- [ ] **Step 2: 手动验收**
  1. Studio col2 出现 Test Suite Builder；col3 仍 "(Phase 3)"
  2. AddCase → 命名 → Toolbox 点 AssertSignal → 属性面板出现 Signal/Expected/Tolerance
  3. 从 DbcSignals 下拉选 "Msg.Sig"，Expected 填值 → 保存 suite.json → 文件内容 `$kind:"assertSignal"` 合法
  4. SendFrame 步骤 → Composer 选报文 → 填 Speed=513 → Compose → `Params["Data"]="0102"`（对照 SignalDecoder 反解）
  5. MoveUp/Down 排序；RemoveStep/RemoveCase
  6. 重新 Open 保存的 suite.json → 步骤/参数完整回读（round-trip）
  7. 在 HIL view 以 VirtualEcu 模式加载该 suite.json 跑一遍（现有引擎消费）
- [ ] **Step 3: 有失败项则定位修复重跑**

---

## Self-Review（写完即查）

- **Spec 覆盖**：Phase 2 全部概要 + 4 条用户决策 → Task 1-7。约束 #1-4（canId 视角等）是 Phase 3 项，不涉及。
- **Placeholder 扫描**：Task 1/2/3/4/5 有完整可编译代码；Task 6 XAML 是结构级（XAML 不单测，标注靠构建+手动）。"以编译为准"的 3 处（`CanId` 命名空间、`TestSuiteConfig` 构造、`HilStudioViewModel` ctor 变体）是代码事实，实现时按编译器报错修正，非占位。
- **类型一致性**：`StepParametersExporter.FromParameters`（Task 1）→ `EditableTestCaseStep.FromStep`（Task 2）→ `TestSuiteBuilderViewModel.LoadFromText/ToSuite`（Task 4）→ `HILJsonOptions.Default` round-trip 同一 `StepParameters` 模型。dict 键名/类型与 `StepParametersFactory.Create`（已核）严格一致。`DbcMessageOption` 在 Task 4 定义、Task 5/6 复用。
