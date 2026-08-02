# HIL Configuration Studio Phase 3 — ECU Simulator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fill col4 of `HilStudioWindow` with an ECU Simulator editor — form-based editing of `ecu-script.json` (states format), Import ODX button, and a read-only Canvas state-machine preview — whose saved JSON is consumed directly by the existing `StatefulVirtualEcu` runtime with zero engine changes.

**Architecture:** A new `EcuSimulatorViewModel` (mirroring the `TestSuiteBuilderViewModel` pattern) exposed as `HilStudioViewModel.EcuSimulator`. Form model `EditableEcuScript` holds **file-perspective** CAN IDs and states/transitions/DID values. Load goes through `EcuScriptLoader.Parse` (validate + `rules`→`states` migration) then reverse-swaps `CanIds`; save serializes a **file-perspective** JSON object with `HILJsonOptions.Default` (never back through `EcuScriptLoader.Parse`, per constraint #1). Responses serialize as `EcuResponse` (`$type` discriminator), so static/dynamic round-trip via existing strong types.

**Tech Stack:** WPF net10.0-windows, CommunityToolkit.Mvvm 8.4.2, `PeakCan.Host.Core.HIL.Contracts` (`EcuStateMachine`/`EcuStateTransition`/`EcuResponse`), `EcuScriptLoader`, `OdxEcuScriptImporter`, `HILJsonOptions.Default`.

## Global Constraints

- **#1 canId 视角单一化（致命项）**: file format = HIL perspective (requestId=0x7E0 / responseId=0x7E8); `EcuScriptLoader.ParseCanIds` swaps to ECU perspective in the in-memory `EcuScript.CanIds`. The editor form holds **file perspective**; load reverse-swaps (`file requestId = ecu.CanIds.ResponseId`, `file responseId = ecu.CanIds.RequestId`); save serializes file perspective; **never feed the in-memory model back through `EcuScriptLoader.Parse`** (would double-swap IDs). `OdxEcuScriptImporter.ImportToJson` already writes file-perspective IDs.
- **#2 响应强类型**: response editing must round-trip through `EcuResponse` (`StaticResponse(byte[])` / `DynamicResponse(string)`) + `HILJsonOptions.Default` — `{"$type":"static","data":[...]}` / `{"$type":"dynamic","generatorName":...}` produced by the serializer, never hand-built JSON.
- **#3 Import ODX 异常**: `OdxEcuScriptImporter.ImportToJson` throws `InvalidOperationException` when no UDS services found; ODX parsing can throw file/XML exceptions. UI must try/catch + show user-visible `ErrorMessage`, never crash.
- **#4 JSON↔表单单向**: form is source of truth; loading a `rules`-format script auto-migrates to `states`; no bidirectional JSON view.
- Engine zero-change: `EcuStateMachine.ProcessRequest`/`Reset`/`ReplaceGenerators` behavior must not change (only a read-only getter is added in Task 1).
- Code comments: 中文 = business/user-facing, 英文 = API/protocol.

---
---

### Task 1: `EcuStateMachine.Transitions` getter + `EditableEcuScript` form model + load

**Files:**
- Modify: `src/PeakCan.Host.Core/HIL/Contracts/EcuStateMachine.cs` (add getter)
- Create: `src/PeakCan.Host.App/ViewModels/EcuSimulator/EditableEcuNode.cs`
- Create: `src/PeakCan.Host.App/ViewModels/EcuSimulator/EditableEcuScript.cs`
- Create: `src/PeakCan.Host.App/ViewModels/EcuSimulator/EditableEcuState.cs`
- Create: `src/PeakCan.Host.App/ViewModels/EcuSimulator/EditableEcuTransition.cs`
- Create: `src/PeakCan.Host.App/ViewModels/EcuSimulator/EditableDidValue.cs`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/EcuSimulator/EditableEcuScriptTests.cs`

**Interfaces:**
- Produces (later tasks consume):
  - `EcuStateMachine.Transitions` — `public IReadOnlyList<EcuStateTransition> Transitions => _transitions;`
  - `EditableEcuScript.FromEcuScript(EcuScript script) → EditableEcuScript`
  - `EditableEcuScript.States` — `ObservableCollection<EditableEcuState>`
  - `EditableEcuScript.DidValues` — `ObservableCollection<EditableDidValue>`
  - `EditableEcuScript.Changed` — `public event Action? Changed` (fires on any property/collection mutation, bubbles from children)
  - `EditableEcuState.FromTransitions(string name, IEnumerable<EcuStateTransition> transitions) → EditableEcuState`
  - `EditableEcuTransition.FromTransition(EcuStateTransition t) → EditableEcuTransition`
  - `EditableEcuTransition` properties: `ServiceIdHex`(string "0x22"), `SubFunctionHex`(string?, ""=any), `DataMaskHex`(string "FF 00"), `DataPatternHex`(string), `ResponseMode`(enum `EcuResponseMode { Static, Dynamic }`), `StaticDataHex`(string), `GeneratorName`(string), `ToState`(string?, ""=stay), `ResponseDelayMs`(int)

- [ ] **Step 1: Add `Transitions` getter to `EcuStateMachine`**

In `EcuStateMachine.cs`, next to `public string CurrentState => _currentState;` add:

```csharp
    /// <summary>All transitions (read-only). Exposed for the studio editor; runtime logic reads via ProcessRequest only.</summary>
    public IReadOnlyList<EcuStateTransition> Transitions => _transitions;
```

- [ ] **Step 2: Write the failing tests for load/parse**

Create `tests/PeakCan.Host.App.Tests/ViewModels/EcuSimulator/EditableEcuScriptTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using PeakCan.Host.App.ViewModels.EcuSimulator;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.App.Tests.ViewModels.EcuSimulator;

public class EditableEcuScriptTests
{
    private const string StatesJson = """
    {
      "name": "Door",
      "initialState": "Locked",
      "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
      "states": [
        { "name": "Locked", "transitions": [
          { "serviceId": "0x27", "subFunction": "0x01",
            "dataMask": [255], "dataPattern": [1],
            "response": { "$type": "dynamic", "generatorName": "SecurityAccessSeed" },
            "toState": "Unlocked", "responseDelayMs": 10 } ] },
        { "name": "wildcard", "transitions": [
          { "serviceId": "0x3E", "subFunction": null,
            "response": { "$type": "static", "data": [126] },
            "responseDelayMs": 0 } ] }
      ]
    }
    """;

    [Fact]
    public void FromEcuScript_Reverses_CanIds_To_File_Perspective()
    {
        var script = EcuScriptLoader.Parse(StatesJson);
        var e = EditableEcuScript.FromEcuScript(script);

        // loader swapped: ECU.RequestId = file responseId(0x7E8), ECU.ResponseId = file requestId(0x7E0)
        e.RequestIdHex.Should().Be("0x7E0");
        e.ResponseIdHex.Should().Be("0x7E8");
        e.Name.Should().Be("Door");
        e.InitialState.Should().Be("Locked");
    }

    [Fact]
    public void FromEcuScript_Groups_Transitions_By_State_And_Reads_Response_Modes()
    {
        var e = EditableEcuScript.FromEcuScript(EcuScriptLoader.Parse(StatesJson));

        e.States.Should().HaveCount(2);
        var locked = e.States.First(s => s.Name == "Locked");
        locked.Transitions.Should().HaveCount(1);
        var t = locked.Transitions[0];
        t.ServiceIdHex.Should().Be("0x27");
        t.SubFunctionHex.Should().Be("0x01");
        t.DataMaskHex.Should().Be("FF");
        t.DataPatternHex.Should().Be("01");
        t.ResponseMode.Should().Be(EcuResponseMode.Dynamic);
        t.GeneratorName.Should().Be("SecurityAccessSeed");
        t.ToState.Should().Be("Unlocked");
        t.ResponseDelayMs.Should().Be(10);

        var w = e.States.First(s => s.Name == "wildcard");
        w.Transitions[0].ResponseMode.Should().Be(EcuResponseMode.Static);
        w.Transitions[0].StaticDataHex.Should().Be("7E");
        w.Transitions[0].SubFunctionHex.Should().BeNullOrEmpty();
    }

    [Fact]
    public void FromEcuScript_Loads_DidValues_And_Rules_Migrates_To_Wildcard()
    {
        const string rulesJson = """
        { "name": "B", "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
          "didValues": { "0xF190": [1, 2] },
          "rules": [ { "serviceId": "0x22", "responseData": [98, 241] } ] }
        """;
        var e = EditableEcuScript.FromEcuScript(EcuScriptLoader.Parse(rulesJson));

        e.States.Should().HaveCount(1);               // rules → wildcard 迁移
        e.States[0].Name.Should().Be("wildcard");
        e.States[0].Transitions[0].ServiceIdHex.Should().Be("0x22");
        e.DidValues.Should().ContainSingle();
        e.DidValues[0].KeyHex.Should().Be("0xF190");
        e.DidValues[0].BytesHex.Should().Be("01 02");
    }

    [Fact]
    public void Changing_A_Property_Raises_Changed_Event()
    {
        var e = EditableEcuScript.FromEcuScript(EcuScriptLoader.Parse(StatesJson));
        var raised = 0;
        e.Changed += () => raised++;
        e.States[0].Transitions[0].ServiceIdHex = "0x28";
        raised.Should().BeGreaterThan(0);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter "FullyQualifiedName~EditableEcuScriptTests" -v q`
Expected: FAIL — `EditableEcuScript`/`EditableEcuTransition`/`EcuResponseMode` not defined; getter `Transitions` missing.

- [ ] **Step 4: Implement the form model**

Create `src/PeakCan.Host.App/ViewModels/EcuSimulator/EditableEcuNode.cs`:

```csharp
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PeakCan.Host.App.ViewModels.EcuSimulator;

/// <summary>
/// 可编辑 ECU 脚本节点基类：属性/集合变化向上冒泡到 <see cref="EditableEcuScript.Changed"/>,
/// 供 VM 触发 HasUnsavedChanges 重估。
/// </summary>
public abstract class EditableEcuNode : ObservableObject
{
    internal Action? Notify;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        Notify?.Invoke();
    }

    internal void HookCollection(INotifyCollectionChanged c)
        => c.CollectionChanged += (_, _) => Notify?.Invoke();
}
```

Create `src/PeakCan.Host.App/ViewModels/EcuSimulator/EditableEcuScript.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.App.ViewModels.EcuSimulator;

/// <summary>
/// ECU 脚本表单模型（文件视角 canIds）。加载经 EcuScriptLoader 后反交换 CanIds,
/// 保存经 ToJson 序列化文件视角——绝不把内存模型再喂 EcuScriptLoader.Parse（约束 #1）。
/// </summary>
public sealed partial class EditableEcuScript : EditableEcuNode
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _requestIdHex = "0x7E0";      // 文件视角
    [ObservableProperty] private string _responseIdHex = "0x7E8";     // 文件视角
    [ObservableProperty] private bool _isExtendedFrame;
    [ObservableProperty] private string _initialState = "default";

    public ObservableCollection<EditableEcuState> States { get; } = new();
    public ObservableCollection<EditableDidValue> DidValues { get; } = new();

    public event Action? Changed;

    public EditableEcuScript()
    {
        Notify = () => Changed?.Invoke();
        HookCollection(States);
        HookCollection(DidValues);
    }

    public static EditableEcuScript FromEcuScript(EcuScript script)
    {
        var e = new EditableEcuScript
        {
            Name = script.Name,
            RequestIdHex = Hex(script.CanIds.ResponseId, script.CanIds.IsExtendedFrame),   // 反交换
            ResponseIdHex = Hex(script.CanIds.RequestId, script.CanIds.IsExtendedFrame),
            IsExtendedFrame = script.CanIds.IsExtendedFrame,
            InitialState = script.InitialState,
        };
        foreach (var group in script.StateMachine.Transitions.GroupBy(t => t.FromState ?? "wildcard"))
            e.States.Add(EditableEcuState.FromTransitions(group.Key, group, e));
        if (script.DidValues is { } dv)
            foreach (var (k, v) in dv)
                e.DidValues.Add(EditableDidValue.From(k, v, e));
        return e;
    }

    internal static string Hex(uint value, bool extended)
        => extended ? $"0x{value:X8}" : $"0x{value:X3}";

    internal static string ToHex(byte[] bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2")));

    /// <summary>空格分隔 hex 串 → byte[]；空/空白 → null。</summary>
    internal static byte[]? ParseHexBytes(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            bytes[i] = byte.Parse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return bytes;
    }
}
```

Create `src/PeakCan.Host.App/ViewModels/EcuSimulator/EditableEcuState.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.App.ViewModels.EcuSimulator;

/// <summary>一个状态 = 名称 + 该状态的转移列表。</summary>
public sealed partial class EditableEcuState : EditableEcuNode
{
    [ObservableProperty] private string _name = "";
    public ObservableCollection<EditableEcuTransition> Transitions { get; } = new();

    public static EditableEcuState FromTransitions(
        string name, IEnumerable<EcuStateTransition> transitions, EditableEcuScript owner)
    {
        var s = new EditableEcuState { Name = name };
        s.Notify = owner.Notify;
        s.HookCollection(s.Transitions);
        foreach (var t in transitions)
            s.Transitions.Add(EditableEcuTransition.FromTransition(t, owner.Notify));
        return s;
    }
}
```

Create `src/PeakCan.Host.App/ViewModels/EcuSimulator/EditableEcuTransition.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.App.ViewModels.EcuSimulator;

public enum EcuResponseMode { Static, Dynamic }

/// <summary>
/// 可编辑转移。hex 字段用空格分隔串编辑（"FF 00"）; ServiceId/SubFunction 用 "0x22" 式 hex。
/// 响应二选一: Static=固定字节, Dynamic=生成器名（约束 #2, 序列化走 EcuResponse $type）。
/// </summary>
public sealed partial class EditableEcuTransition : EditableEcuNode
{
    [ObservableProperty] private string _serviceIdHex = "0x22";
    [ObservableProperty] private string? _subFunctionHex;
    [ObservableProperty] private string _dataMaskHex = "";
    [ObservableProperty] private string _dataPatternHex = "";
    [ObservableProperty] private EcuResponseMode _responseMode = EcuResponseMode.Static;
    [ObservableProperty] private string _staticDataHex = "";
    [ObservableProperty] private string _generatorName = "";
    [ObservableProperty] private string? _toState;
    [ObservableProperty] private int _responseDelayMs;

    public static EditableEcuTransition FromTransition(EcuStateTransition t, Action? notify)
    {
        var e = new EditableEcuTransition { Notify = notify }
        {
            ServiceIdHex = $"0x{t.ServiceId:X2}",
            SubFunctionHex = t.SubFunction.HasValue ? $"0x{t.SubFunction.Value:X2}" : null,
            DataMaskHex = t.DataMask is { Length: > 0 } m ? EditableEcuScript.ToHex(m) : "",
            DataPatternHex = t.DataPattern is { Length: > 0 } p ? EditableEcuScript.ToHex(p) : "",
            ToState = t.ToState,
            ResponseDelayMs = t.ResponseDelayMs,
        };
        switch (t.Response)
        {
            case StaticResponse s:
                e.ResponseMode = EcuResponseMode.Static;
                e.StaticDataHex = EditableEcuScript.ToHex(s.Data);
                break;
            case DynamicResponse d:
                e.ResponseMode = EcuResponseMode.Dynamic;
                e.GeneratorName = d.GeneratorName;
                break;
        }
        return e;
    }
}
```

Create `src/PeakCan.Host.App/ViewModels/EcuSimulator/EditableDidValue.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;

namespace PeakCan.Host.App.ViewModels.EcuSimulator;

/// <summary>一个 DID 值: 键（"0xF190"）+ 字节（空格分隔 hex）。</summary>
public sealed partial class EditableDidValue : EditableEcuNode
{
    [ObservableProperty] private string _keyHex = "";
    [ObservableProperty] private string _bytesHex = "";

    public static EditableDidValue From(ushort key, byte[] bytes, EditableEcuScript owner)
        => new() { Notify = owner.Notify, KeyHex = $"0x{key:X4}", BytesHex = EditableEcuScript.ToHex(bytes) };
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter "FullyQualifiedName~EditableEcuScriptTests" -v q`
Expected: PASS (4/4). Note the `Transitions` getter is consumed transitively by `FromEcuScript` (line `script.StateMachine.Transitions`).

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.Core/HIL/Contracts/EcuStateMachine.cs src/PeakCan.Host.App/ViewModels/EcuSimulator tests/PeakCan.Host.App.Tests/ViewModels/EcuSimulator/EditableEcuScriptTests.cs
git commit -m "feat(studio): ECU form model (EditableEcuScript) + load via loader + CanId reverse-swap (Phase 3 T1)"
```

---

### Task 2: File-perspective serialization (`EditableEcuScript.ToJson`) + round-trip test

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/EcuSimulator/EditableEcuScript.cs` (add `ToJson`)
- Modify: `src/PeakCan.Host.App/ViewModels/EcuSimulator/EditableEcuTransition.cs` (add `ToTransitionObject`)
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/EcuSimulator/EditableEcuScriptTests.cs` (add tests)

**Interfaces:**
- Produces: `EditableEcuScript.ToJson() → string` (file-perspective JSON, indented, `HILJsonOptions.Default`)

- [ ] **Step 1: Write the failing round-trip test**

Append to `EditableEcuScriptTests.cs`:

```csharp
    [Fact]
    public void ToJson_RoundTrips_Through_Loader_Without_Data_Loss()
    {
        var e = EditableEcuScript.FromEcuScript(EcuScriptLoader.Parse(StatesJson));
        var outJson = e.ToJson();

        var reparsed = EcuScriptLoader.Parse(outJson);
        reparsed.Name.Should().Be("Door");
        reparsed.InitialState.Should().Be("Locked");
        // 文件视角 canIds 反交换回来 = 原文件视角
        reparsed.CanIds.RequestId.Should().Be(0x7E8);   // ECU 视角; 文件 requestId 0x7E0 → ECU ResponseId
        reparsed.CanIds.ResponseId.Should().Be(0x7E0);
        reparsed.StateMachine.Transitions.Should().BeEquivalentTo(
            EcuScriptLoader.Parse(StatesJson).StateMachine.Transitions);
    }

    [Fact]
    public void ToJson_Emits_Response_As_Type_Discriminator()
    {
        var e = EditableEcuScript.FromEcuScript(EcuScriptLoader.Parse(StatesJson));
        var outJson = e.ToJson();
        using var doc = System.Text.Json.JsonDocument.Parse(outJson);
        var resp = doc.RootElement.GetProperty("states")[0]
            .GetProperty("transitions")[0].GetProperty("response");
        resp.GetProperty("$type").GetString().Should().Be("dynamic");
        resp.GetProperty("generatorName").GetString().Should().Be("SecurityAccessSeed");
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter "FullyQualifiedName~ToJson" -v q`
Expected: FAIL — `ToJson()` not defined.

- [ ] **Step 3: Implement `ToTransitionObject` and `ToJson`**

Add to `EditableEcuTransition.cs`:

```csharp
    /// <summary>序列化为文件视角匿名对象（serviceId 用 hex 字符串, response 走 EcuResponse $type）。</summary>
    public object ToTransitionObject() => new
    {
        serviceId = ServiceIdHex,
        subFunction = string.IsNullOrEmpty(SubFunctionHex) ? null : SubFunctionHex,
        dataMask = EditableEcuScript.ParseHexBytes(DataMaskHex),
        dataPattern = EditableEcuScript.ParseHexBytes(DataPatternHex),
        response = ResponseMode == EcuResponseMode.Dynamic
            ? (EcuResponse)new DynamicResponse(GeneratorName)
            : new StaticResponse(EditableEcuScript.ParseHexBytes(StaticDataHex) ?? Array.Empty<byte>()),
        toState = string.IsNullOrEmpty(ToState) ? null : ToState,
        responseDelayMs = ResponseDelayMs,
    };
```

Add to `EditableEcuScript.cs` (inside the class):

```csharp
    /// <summary>序列化文件视角 JSON（约束 #1/#2: 不经 EcuScriptLoader.Parse, response 走 $type）。</summary>
    public string ToJson()
    {
        var script = new
        {
            name = Name,
            initialState = InitialState,
            canIds = new
            {
                requestId = RequestIdHex,
                responseId = ResponseIdHex,
                isExtendedFrame = IsExtendedFrame,
            },
            didValues = DidValues.Count > 0
                ? DidValues.ToDictionary(d => d.KeyHex, d => ParseHexBytes(d.BytesHex))
                : null,
            states = States.Select(s => new
            {
                name = s.Name,
                transitions = s.Transitions.Select(t => t.ToTransitionObject()).ToList(),
            }),
        };
        return System.Text.Json.JsonSerializer.Serialize(script, PeakCan.Host.Core.HIL.Serialization.HILJsonOptions.Default);
    }
```

Note: `didValues` keys are serialized verbatim (e.g. `"0xF190"`) — matches `EcuScriptLoader.ParseEcuScript` (strips `0x` prefix, hex-parses). `ByteArrayJsonConverter` writes `byte[]` as numeric arrays (verified) — matches loader's `EnumerateArray()`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter "FullyQualifiedName~EditableEcuScriptTests" -v q`
Expected: PASS (6/6).

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/EcuSimulator tests/PeakCan.Host.App.Tests/ViewModels/EcuSimulator/EditableEcuScriptTests.cs
git commit -m "feat(studio): file-perspective ToJson serialization round-trips via loader (Phase 3 T2)"
```

---

### Task 3: `EcuSimulatorViewModel` — contract + Open/Save/SaveAs

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/EcuSimulator/EcuSimulatorViewModel.cs`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/EcuSimulator/EcuSimulatorViewModelTests.cs`

**Interfaces:**
- Produces (consumed by Task 5 XAML via `SuiteBuilder`-style `EcuSimulator` path, and by Task 7 AppShell):
  - `EcuSimulatorViewModel.Script` — `EditableEcuScript`
  - `EcuSimulatorViewModel.States`/`DidValues` — expose `Script.States`/`Script.DidValues` passthrough collections for binding
  - `EcuSimulatorViewModel.GeneratorNames` — `IReadOnlyList<string>` (5 built-in names)
  - Commands: `OpenCommand`, `SaveCommand`, `SaveAsCommand`, `ImportOdxCommand`, `AddStateCommand`, `RemoveStateCommand`, `AddTransitionCommand`, `RemoveTransitionCommand`, `AddDidValueCommand`, `RemoveDidValueCommand`
  - Selected: `SelectedState`, `SelectedTransition`, `SelectedDidValue`
  - Contract (Task 7 consumes): `FilePath`/`IsValidEcuScript`/`StatusMessage`/`ErrorMessage` (observable), `HasUnsavedChanges`, `LoadInitialPath(string?)`, `LoadExternalAsync(string)`, `Reset()`
  - Import ODX inputs (Task 5 XAML): `OdxEcuName`, `OdxRequestIdHex`, `OdxResponseIdHex`

- [ ] **Step 1: Write the failing tests for the contract + save**

Create `tests/PeakCan.Host.App.Tests/ViewModels/EcuSimulator/EcuSimulatorViewModelTests.cs`:

```csharp
using System.IO;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels.EcuSimulator;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL.Serialization;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.App.Tests.ViewModels.EcuSimulator;

public class EcuSimulatorViewModelTests
{
    private const string StatesJson = """
    { "name": "Door", "initialState": "Locked",
      "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
      "states": [ { "name": "Locked", "transitions": [
        { "serviceId": "0x27", "response": { "$type": "dynamic", "generatorName": "SecurityAccessSeed" } } ] } ] }
    """;

    private sealed class FileDialogStub : IFileDialogService
    {
        public string? OpenResult { get; set; }
        public string? SaveResult { get; set; }
        public string? ShowOpenDialog(string filter) => OpenResult;
        public string? ShowSaveDialog(string filter, string? defaultExt, string? initialDirectory) => SaveResult;
    }

    private static EcuSimulatorViewModel NewVm(IFileDialogService? dlg = null)
        => new(NullLogger<EcuSimulatorViewModel>.Instance, dlg);

    [Fact]
    public void LoadFromText_Populates_Script_And_Marks_Valid()
    {
        var vm = NewVm();
        vm.LoadFromText(StatesJson).Should().BeTrue();
        vm.IsValidEcuScript.Should().BeTrue();
        vm.Script.Name.Should().Be("Door");
        vm.Script.States.Should().HaveCount(1);
    }

    [Fact]
    public void LoadFromText_Bad_Json_Returns_False_And_Sets_Error()
    {
        var vm = NewVm();
        vm.LoadFromText("{ not json").Should().BeFalse();
        vm.IsValidEcuScript.Should().BeFalse();
        vm.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void HasUnsavedChanges_Tracks_Edits_After_Load()
    {
        var vm = NewVm();
        vm.LoadFromText(StatesJson);
        vm.HasUnsavedChanges.Should().BeFalse();
        vm.Script.States[0].Transitions[0].ServiceIdHex = "0x28";
        vm.HasUnsavedChanges.Should().BeTrue();
    }

    [Fact]
    public async Task Open_Then_Save_Overwrites_Original_File()
    {
        var dir = Directory.CreateTempSubdirectory("ecusim-test");
        var path = Path.Combine(dir.FullName, "ecu.json");
        await File.WriteAllTextAsync(path, StatesJson);
        var vm = NewVm(new FileDialogStub { OpenResult = path });

        await vm.OpenCommand.ExecuteAsync(null);
        vm.Script.States[0].Transitions[0].ServiceIdHex = "0x29";
        vm.SaveCommand.Execute(null);

        var reparsed = EcuScriptLoader.Parse(File.ReadAllText(path));
        reparsed.StateMachine.Transitions[0].ServiceId.Should().Be(0x29);
        vm.StatusMessage.Should().Contain("Saved");
    }

    [Fact]
    public void GeneratorNames_Lists_Five_Builtin_Generators()
    {
        var vm = NewVm();
        vm.GeneratorNames.Should().Contain("SecurityAccessSeed");
        vm.GeneratorNames.Should().Contain("SecurityAccessVerifyKey");
        vm.GeneratorNames.Should().Contain("ClearDtc");
        vm.GeneratorNames.Should().Contain("DidReadout");
        vm.GeneratorNames.Should().Contain("DidWrite");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter "FullyQualifiedName~EcuSimulatorViewModelTests" -v q`
Expected: FAIL — `EcuSimulatorViewModel` not defined.

- [ ] **Step 3: Implement `EcuSimulatorViewModel`**

Create `src/PeakCan.Host.App/ViewModels/EcuSimulator/EcuSimulatorViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.HIL;
using PeakCan.Host.Infrastructure.HIL.Generators;

namespace PeakCan.Host.App.ViewModels.EcuSimulator;

/// <summary>
/// ECU Simulator 主 VM（HilStudioWindow col4）。表单编辑 EcuScript（文件视角）,
/// 保存走 ToJson 文件视角 round-trip（约束 #1/#2）。暴露 EcuScriptEditorViewModel 同款契约
/// 供 AppShell 三路同步（LoadInitialPath/LoadExternalAsync/Reset）。
/// </summary>
public sealed partial class EcuSimulatorViewModel : ObservableObject
{
    private readonly ILogger _logger;
    private readonly IFileDialogService _fileDialog;
    private string? _suitePath;
    private string _savedJson = "";

    public EditableEcuScript Script { get; } = new();
    public ObservableCollection<EditableEcuState> States => Script.States;
    public ObservableCollection<EditableDidValue> DidValues => Script.DidValues;
    public IReadOnlyList<string> GeneratorNames { get; }

    [ObservableProperty] private EditableEcuState? _selectedState;
    [ObservableProperty] private EditableEcuTransition? _selectedTransition;
    [ObservableProperty] private EditableDidValue? _selectedDidValue;
    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private bool _isValidEcuScript;
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private string? _errorMessage;

    // Import ODX 参数（col4 工具栏输入框）
    [ObservableProperty] private string _odxEcuName = "";
    [ObservableProperty] private string _odxRequestIdHex = "0x7E0";
    [ObservableProperty] private string _odxResponseIdHex = "0x7E8";

    public bool HasUnsavedChanges => !string.Equals(Script.ToJson(), _savedJson, StringComparison.Ordinal);

    public EcuSimulatorViewModel(ILogger logger, IFileDialogService? fileDialog = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _fileDialog = fileDialog ?? new WpfFileDialogService();
        GeneratorNames = BuiltInGenerators.CreateAll().Select(g => g.Name).ToList();
        Script.Changed += () => OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    /// <summary>反序列化并填充表单; 成功 true。加载经 EcuScriptLoader（校验 + rules 迁移 + CanIds 交换反推）。</summary>
    public bool LoadFromText(string json)
    {
        try
        {
            var script = EcuScriptLoader.Parse(json);
            _savedJson = json;
            Script.Name = script.Name;
            Script.RequestIdHex = EditableEcuScript.Hex(script.CanIds.ResponseId, script.CanIds.IsExtendedFrame);
            Script.ResponseIdHex = EditableEcuScript.Hex(script.CanIds.RequestId, script.CanIds.IsExtendedFrame);
            Script.IsExtendedFrame = script.CanIds.IsExtendedFrame;
            Script.InitialState = script.InitialState;
            Script.States.Clear();
            Script.DidValues.Clear();
            foreach (var group in script.StateMachine.Transitions.GroupBy(t => t.FromState ?? "wildcard"))
                Script.States.Add(EditableEcuState.FromTransitions(group.Key, group, Script));
            if (script.DidValues is { } dv)
                foreach (var (k, v) in dv)
                    Script.DidValues.Add(EditableDidValue.From(k, v, Script));
            SelectedState = Script.States.FirstOrDefault();
            SelectedTransition = null;
            IsValidEcuScript = true;
            ErrorMessage = null;
            StatusMessage = $"Loaded {Script.States.Count} state(s)";
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ECU script load failed");
            ErrorMessage = ex.Message;
            StatusMessage = "Load failed.";
            IsValidEcuScript = false;
            return false;
        }
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (HasUnsavedChanges)
        {
            var r = await _messageBoxConfirm();
            if (r is null or false) return;
        }
        var path = _fileDialog.ShowOpenDialog("ECU Script JSON|*.json|All Files|*.*");
        if (path is null) return;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            if (LoadFromText(json)) { _suitePath = path; FilePath = path; }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ECU script open failed");
            ErrorMessage = ex.Message;
        }
    }
    // _messageBoxConfirm defined in Task 4 (see below) — placeholder resolved there.

    [RelayCommand]
    private void Save() => SaveCore(_suitePath);

    [RelayCommand]
    private void SaveAs()
    {
        var dir = _suitePath is null ? null : Path.GetDirectoryName(_suitePath);
        var chosen = _fileDialog.ShowSaveDialog("ECU Script JSON|*.json", ".json", dir);
        if (chosen is null) return;
        SaveCore(chosen);
    }

    private void SaveCore(string? path)
    {
        if (string.IsNullOrEmpty(path)) { SaveAs(); return; }
        try
        {
            var json = Script.ToJson();
            File.WriteAllText(path, json);
            _savedJson = json;
            _suitePath = path;
            FilePath = path;
            IsValidEcuScript = true;
            ErrorMessage = null;
            StatusMessage = $"Saved {path}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ECU script save failed");
            ErrorMessage = ex.Message;
            StatusMessage = "Save failed.";
        }
    }

    [RelayCommand]
    private void AddState()
    {
        var s = EditableEcuState.FromTransitions($"State{States.Count + 1}", Array.Empty<EcuStateTransition>(), Script);
        Script.States.Add(s);
        SelectedState = s;
    }

    [RelayCommand]
    private void RemoveState()
    {
        if (SelectedState is null) return;
        Script.States.Remove(SelectedState);
        SelectedTransition = null;
        SelectedState = Script.States.LastOrDefault();
    }

    [RelayCommand]
    private void AddTransition()
    {
        if (SelectedState is null) return;
        var t = EditableEcuTransition.FromTransition(
            new EcuStateTransition { ServiceId = 0x22, Response = new StaticResponse(new byte[] { 0x7F, 0x22, 0x11 }) },
            Script.Notify);
        SelectedState.Transitions.Add(t);
        SelectedTransition = t;
    }

    [RelayCommand]
    private void RemoveTransition()
    {
        if (SelectedState is null || SelectedTransition is null) return;
        SelectedState.Transitions.Remove(SelectedTransition);
        SelectedTransition = SelectedState.Transitions.LastOrDefault();
    }

    [RelayCommand]
    private void AddDidValue()
    {
        var d = new EditableDidValue { Notify = Script.Notify, KeyHex = "0xF190", BytesHex = "00" };
        Script.DidValues.Add(d);
        SelectedDidValue = d;
    }

    [RelayCommand]
    private void RemoveDidValue()
    {
        if (SelectedDidValue is null) return;
        Script.DidValues.Remove(SelectedDidValue);
        SelectedDidValue = Script.DidValues.LastOrDefault();
    }

    // ---- 契约（Task 7 AppShell 消费） ----

    public void LoadInitialPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            var json = File.ReadAllText(path);
            if (LoadFromText(json)) { _suitePath = path; FilePath = path; }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    public async Task LoadExternalAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            if (LoadFromText(json)) { _suitePath = path; FilePath = path; }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    public void Reset()
    {
        Script.States.Clear();
        Script.DidValues.Clear();
        Script.Name = "";
        Script.RequestIdHex = "0x7E0";
        Script.ResponseIdHex = "0x7E8";
        Script.IsExtendedFrame = false;
        Script.InitialState = "default";
        SelectedState = null;
        SelectedTransition = null;
        SelectedDidValue = null;
        FilePath = null;
        _suitePath = null;
        _savedJson = "";
        IsValidEcuScript = false;
        ErrorMessage = null;
        StatusMessage = "Ready";
    }
}
```

> **Task 4 注**：`_messageBoxConfirm()` 占位——Task 4 实现 `OpenAsync` 的"丢修改确认"为**无阻塞确认**（不改签名）或用 `IMessageBoxPrompt`。见 Task 4 Step 3 处理：将 `_messageBoxConfirm` 替换为真实实现（本 Task 的 `OpenAsync` 调用它，先让 Task 3 测试避开确认分支——测试直接调用 `OpenCommand.ExecuteAsync` 且无未保存修改，不进确认分支）。

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter "FullyQualifiedName~EcuSimulatorViewModelTests" -v q`
Expected: PASS (5/5) — `OpenAsync` confirm branch is skipped because tests have no unsaved changes. If `_messageBoxConfirm` unresolved blocks compile, see Task 4 Step 3 (implement it there first, or make `_messageBoxConfirm` a `Task<bool?>` that returns `true` when `HasUnsavedChanges` is false — add that one-liner now).

> 若编译报 `_messageBoxConfirm` 未定义：在 VM 加 `private Task<bool?> _messageBoxConfirm() => Task.FromResult<bool?>(true);`（Task 4 再换成真实对话框实现）。

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/EcuSimulator/EcuSimulatorViewModel.cs tests/PeakCan.Host.App.Tests/ViewModels/EcuSimulator/EcuSimulatorViewModelTests.cs
git commit -m "feat(studio): EcuSimulatorViewModel — contract + Open/Save/SaveAs + edit commands (Phase 3 T3)"
```

---

### Task 4: Import ODX flow (try/catch + params)

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/EcuSimulator/EcuSimulatorViewModel.cs` (add `ImportOdxAsync`, real confirm)
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/EcuSimulator/EcuSimulatorViewModelTests.cs` (add Import ODX tests)

**Interfaces:**
- Consumes: `OdxEcuScriptImporter.ImportToJson(odxPath, ecuName, requestId, responseId)` (Infrastructure; throws `InvalidOperationException` on no UDS services); VM props `OdxEcuName`/`OdxRequestIdHex`/`OdxResponseIdHex`
- Produces: `ImportOdxCommand`

- [ ] **Step 1: Write the failing tests**

Append to `EcuSimulatorViewModelTests.cs`:

```csharp
    [Fact]
    public async Task ImportOdx_InvalidOperationException_Shows_Error_Not_Crash()
    {
        var dir = Directory.CreateTempSubdirectory("ecusim-odx");
        var odx = Path.Combine(dir.FullName, "empty.odx");   // 无 UDS 服务 → InvalidOperationException
        await File.WriteAllTextAsync(odx, "<empty/>");
        var vm = NewVm(new FileDialogStub { OpenResult = odx });
        vm.OdxEcuName = "ECU";
        vm.OdxRequestIdHex = "0x7E0";
        vm.OdxResponseIdHex = "0x7E8";

        var act = () => vm.ImportOdxCommand.ExecuteAsync(null);
        await act.Should().NotThrowAsync();

        vm.IsValidEcuScript.Should().BeFalse();      // 失败不清空原有脚本
        vm.ErrorMessage.Should().NotBeNullOrEmpty();
        vm.StatusMessage.Should().Contain("Import ODX failed");
    }

    [Fact]
    public void GeneratorNames_Comes_From_BuiltInGenerators()
    {
        var vm = NewVm();
        vm.GeneratorNames.Should().HaveCount(5);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter "FullyQualifiedName~ImportOdx_InvalidOperation" -v q`
Expected: FAIL — `ImportOdxCommand` not defined.

- [ ] **Step 3: Implement `ImportOdxAsync` + replace `_messageBoxConfirm`**

In `EcuSimulatorViewModel.cs`, add (uses `IMessageBoxPrompt` from `PeakCan.Host.Core`, injected via optional ctor param — see step 4 note):

```csharp
    [RelayCommand]
    private async Task ImportOdxAsync()
    {
        var path = _fileDialog.ShowOpenDialog("ODX Files|*.odx;*.pdx|All Files|*.*");
        if (path is null) return;
        try
        {
            var json = OdxEcuScriptImporter.ImportToJson(
                path, OdxEcuName,
                ParseHexUint(OdxRequestIdHex), ParseHexUint(OdxResponseIdHex));
            if (LoadFromText(json)) { _suitePath = path; FilePath = null; }
            StatusMessage = $"Imported {Path.GetFileName(path)}";
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "ODX import failed (no UDS services)");
            ErrorMessage = ex.Message;
            StatusMessage = "Import ODX failed.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ODX import failed");
            ErrorMessage = ex.Message;
            StatusMessage = "Import ODX failed.";
        }
    }

    private static uint ParseHexUint(string s)
    {
        var clean = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
        return uint.Parse(clean, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
    }
```

Replace the placeholder confirm with a real one. Change ctor to accept `IMessageBoxPrompt`:

```csharp
    private readonly IMessageBoxPrompt _messageBox;
    ...
    public EcuSimulatorViewModel(ILogger logger, IFileDialogService? fileDialog = null, IMessageBoxPrompt? messageBox = null)
    {
        ...
        _messageBox = messageBox ?? new MessageBoxPrompt();   // 若无默认实现, 用 NullMessageBoxPrompt (见 Step 4 note)
    }

    private async Task<bool?> _messageBoxConfirm()
    {
        if (!HasUnsavedChanges) return true;
        var r = await _messageBox.ShowAsync("Discard changes?",
            "Opening a file will discard unsaved changes. Continue?", null);
        return r == System.Windows.MessageBoxResult.Yes;
    }
```

> **Step 4 note（实现者必读）**: 若 `PeakCan.Host.Core` 无 `IMessageBoxPrompt` 的无参默认实现，看 `AppShellViewModel` 怎么拿 `IMessageBoxPrompt`（DI 注入）。测试注入 `null` 时用 `NullLogger` + 一个始终返回 Yes 的 stub。调整测试构造：`NewVm` 传一个 `messageBox` stub（`ShowAsync → Yes`），保证测试走确认分支时不卡。测试里 `ImportOdxCommand.ExecuteAsync` 需 `await`——用 `await vm.ImportOdxCommand.ExecuteAsync(null)` 而非 `act` 断言（见 Step 5）。

- [ ] **Step 4: Adjust test to await ImportOdx properly and wire messageBox**

Update the Import ODX test to await the command:

```csharp
    [Fact]
    public async Task ImportOdx_InvalidOperationException_Shows_Error_Not_Crash()
    {
        ...
        await vm.ImportOdxCommand.ExecuteAsync(null);
        ...
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter "FullyQualifiedName~EcuSimulatorViewModelTests" -v q`
Expected: PASS (7/7).

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/EcuSimulator/EcuSimulatorViewModel.cs tests/PeakCan.Host.App.Tests/ViewModels/EcuSimulator/EcuSimulatorViewModelTests.cs
git commit -m "feat(studio): Import ODX with try/catch (InvalidOperationException) + unsaved-changes confirm (Phase 3 T4)"
```

---

### Task 5: col4 UI — form + state/transition editor + DID + generator dropdown

**Files:**
- Modify: `src/PeakCan.Host.App/Windows/HilStudioWindow.xaml` (replace col4 placeholder Border with ECU Simulator panel; widen col4)
- Modify: `src/PeakCan.Host.App/ViewModels/HilStudioViewModel.cs` (add `EcuSimulator` property + ctor wiring)
- Modify: `tests/PeakCan.Host.App.Tests/ViewModels/HilStudioViewModelTests.cs` (NewVm unaffected — VM created inside ctor)

**Interfaces:**
- Consumes: `EcuSimulatorViewModel` from Task 3/4; `HilStudioViewModel.EcuSimulator` (new property)

- [ ] **Step 1: Expose `EcuSimulator` on `HilStudioViewModel`**

In `HilStudioViewModel.cs`, add a readonly property and construct it in the ctor (mirrors `SuiteBuilder`). Ctor already receives `IFileDialogService fileDialog` and `ILogger logger`:

```csharp
    public EcuSimulatorViewModel EcuSimulator { get; }
    // ctor:
    EcuSimulator = new EcuSimulatorViewModel(logger, fileDialog);
```

Add `using PeakCan.Host.App.ViewModels.EcuSimulator;`.

- [ ] **Step 2: Replace col4 placeholder in XAML**

In `HilStudioWindow.xaml`, widen col4 (`MinWidth="240"` → `MinWidth="340"`) and replace the placeholder `Border Grid.Column="4"` (currently `"ECU Simulator / (Phase 3)"`) with:

```xml
    <!-- ===== col 4: ECU Simulator（Phase 3） ===== -->
    <Grid Grid.Column="4" Margin="8">
      <Grid.RowDefinitions>
        <RowDefinition Height="Auto"/>
        <RowDefinition Height="Auto"/>
        <RowDefinition Height="Auto"/>
        <RowDefinition Height="*" MinHeight="150"/>
        <RowDefinition Height="Auto"/>
        <RowDefinition Height="*" MinHeight="120"/>
      </Grid.RowDefinitions>

      <!-- 工具栏 -->
      <StackPanel Orientation="Horizontal">
        <Button Content="Open" Command="{Binding EcuSimulator.OpenCommand}" Padding="6,1"/>
        <Button Content="Save" Command="{Binding EcuSimulator.SaveCommand}" Padding="6,1"/>
        <Button Content="SaveAs" Command="{Binding EcuSimulator.SaveAsCommand}" Padding="6,1"/>
      </StackPanel>
      <TextBlock Grid.Row="1" Text="{Binding EcuSimulator.StatusMessage}" Foreground="Gray" Margin="0,2,0,0"/>
      <TextBlock Grid.Row="1" Text="{Binding EcuSimulator.ErrorMessage}" Foreground="Firebrick" Margin="0,2,0,0"
                 TextTrimming="CharacterEllipsis" MaxWidth="320"/>

      <!-- 脚本属性表单 -->
      <Grid Grid.Row="2" Margin="0,6,0,2">
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/>
          <ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>
        <Grid.RowDefinitions>
          <RowDefinition Height="Auto"/><RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>
        <TextBlock Text="Name:" VerticalAlignment="Center"/>
        <TextBox Grid.Column="1" Text="{Binding EcuSimulator.Script.Name, UpdateSourceTrigger=PropertyChanged}"/>
        <TextBlock Grid.Row="1" Text="Initial state:" VerticalAlignment="Center"/>
        <TextBox Grid.Row="1" Grid.Column="1" Text="{Binding EcuSimulator.Script.InitialState, UpdateSourceTrigger=PropertyChanged}"/>
        <TextBlock Grid.Column="2" Text="ReqID:" VerticalAlignment="Center" Margin="8,0,0,0"/>
        <TextBox Grid.Column="3" Text="{Binding EcuSimulator.Script.RequestIdHex, UpdateSourceTrigger=PropertyChanged}" FontFamily="Consolas"/>
        <TextBlock Grid.Row="1" Grid.Column="2" Text="RespID:" VerticalAlignment="Center" Margin="8,0,0,0"/>
        <TextBox Grid.Row="1" Grid.Column="3" Text="{Binding EcuSimulator.Script.ResponseIdHex, UpdateSourceTrigger=PropertyChanged}" FontFamily="Consolas"/>
        <CheckBox Grid.Row="1" Grid.Column="3" Content="Extended" IsChecked="{Binding EcuSimulator.Script.IsExtendedFrame, Mode=TwoWay}" Margin="0,22,0,0"/>
      </Grid>

      <!-- 状态列表 + 选中状态转移编辑 -->
      <Grid Grid.Row="3">
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width="110"/>
          <ColumnDefinition Width="Auto"/>
          <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>
        <Grid Grid.Column="0">
          <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/>
          </Grid.RowDefinitions>
          <StackPanel Orientation="Horizontal">
            <Button Content="+S" Command="{Binding EcuSimulator.AddStateCommand}" Padding="4,0"/>
            <Button Content="-S" Command="{Binding EcuSimulator.RemoveStateCommand}" Padding="4,0" Margin="2,0,0,0"/>
          </StackPanel>
          <ListBox Grid.Row="1" ItemsSource="{Binding EcuSimulator.States}"
                   SelectedItem="{Binding EcuSimulator.SelectedState}" DisplayMemberPath="Name"/>
          <Button Grid.Row="2" Content="+Trans" Command="{Binding EcuSimulator.AddTransitionCommand}" Margin="0,2,0,0" Padding="4,0"/>
        </Grid>
        <GridSplitter Grid.Column="1" Width="5" Background="#CCCCCC" ResizeBehavior="PreviousAndNext"/>
        <!-- 转移列表 + 编辑面板 -->
        <Grid Grid.Column="2">
          <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/>
          </Grid.RowDefinitions>
          <StackPanel Orientation="Horizontal">
            <TextBlock Text="Transitions" FontWeight="SemiBold" VerticalAlignment="Center"/>
            <Button Content="-Trans" Command="{Binding EcuSimulator.RemoveTransitionCommand}" Margin="6,0,0,0" Padding="4,0"/>
          </StackPanel>
          <ListBox Grid.Row="1" ItemsSource="{Binding EcuSimulator.SelectedState.Transitions}"
                   SelectedItem="{Binding EcuSimulator.SelectedTransition}">
            <ListBox.ItemTemplate>
              <DataTemplate>
                <StackPanel Orientation="Horizontal">
                  <TextBlock Text="{Binding ServiceIdHex}" FontFamily="Consolas"/>
                  <TextBlock Text="{Binding ToState, StringFormat='  → {0}'}" Foreground="Gray" Margin="4,0,0,0"/>
                </StackPanel>
              </DataTemplate>
            </ListBox.ItemTemplate>
          </ListBox>
          <!-- 转移属性编辑 -->
          <ScrollViewer Grid.Row="2" VerticalScrollBarVisibility="Auto" MaxHeight="140">
            <StackPanel DataContext="{Binding EcuSimulator.SelectedTransition}">
              <StackPanel Orientation="Horizontal">
                <TextBlock Text="SID:" VerticalAlignment="Center"/>
                <TextBox Text="{Binding ServiceIdHex, UpdateSourceTrigger=PropertyChanged}" Width="52" FontFamily="Consolas" Margin="4,0,0,0"/>
                <TextBlock Text="Sub:" VerticalAlignment="Center" Margin="8,0,0,0"/>
                <TextBox Text="{Binding SubFunctionHex, UpdateSourceTrigger=PropertyChanged}" Width="52" FontFamily="Consolas" Margin="4,0,0,0"/>
                <TextBlock Text="Delay:" VerticalAlignment="Center" Margin="8,0,0,0"/>
                <TextBox Text="{Binding ResponseDelayMs, UpdateSourceTrigger=PropertyChanged}" Width="52" Margin="4,0,0,0"/>
              </StackPanel>
              <StackPanel Orientation="Horizontal" Margin="0,4,0,0">
                <TextBlock Text="Mask:" VerticalAlignment="Center"/>
                <TextBox Text="{Binding DataMaskHex, UpdateSourceTrigger=PropertyChanged}" Width="110" FontFamily="Consolas" Margin="4,0,0,0"/>
                <TextBlock Text="Pattern:" VerticalAlignment="Center" Margin="8,0,0,0"/>
                <TextBox Text="{Binding DataPatternHex, UpdateSourceTrigger=PropertyChanged}" Width="110" FontFamily="Consolas" Margin="4,0,0,0"/>
              </StackPanel>
              <StackPanel Orientation="Horizontal" Margin="0,4,0,0">
                <TextBlock Text="ToState:" VerticalAlignment="Center"/>
                <ComboBox ItemsSource="{Binding DataContext.EcuSimulator.States, RelativeSource={RelativeSource AncestorType=Window}}"
                          DisplayMemberPath="Name" SelectedValuePath="Name"
                          SelectedValue="{Binding ToState, Mode=TwoWay}" Width="90" Margin="4,0,0,0"/>
                <TextBlock Text="Response:" VerticalAlignment="Center" Margin="8,0,0,0"/>
                <ComboBox SelectedValuePath="Tag" SelectedValue="{Binding ResponseMode, Mode=TwoWay}" Width="70" Margin="4,0,0,0">
                  <ComboBoxItem Content="Static" Tag="Static"/>
                  <ComboBoxItem Content="Dynamic" Tag="Dynamic"/>
                </ComboBox>
              </StackPanel>
              <!-- 响应参数: 按 ResponseMode 显隐 -->
              <StackPanel Margin="0,4,0,0" Visibility="{Binding ResponseMode, Converter={StaticResource EcuResponseModeToVisibilityConverter}}">
                <TextBlock Text="Static data (hex):" VerticalAlignment="Center"/>
                <TextBox Text="{Binding StaticDataHex, UpdateSourceTrigger=PropertyChanged}" FontFamily="Consolas"/>
              </StackPanel>
              <StackPanel Margin="0,4,0,0" Visibility="{Binding ResponseMode, Converter={StaticResource EcuResponseModeToVisibilityConverter}, ConverterParameter=Dynamic}">
                <TextBlock Text="Generator:" VerticalAlignment="Center"/>
                <ComboBox ItemsSource="{Binding DataContext.EcuSimulator.GeneratorNames, RelativeSource={RelativeSource AncestorType=Window}}"
                          SelectedItem="{Binding GeneratorName, Mode=TwoWay}" Width="150" Margin="4,0,0,0"/>
              </StackPanel>
            </StackPanel>
          </ScrollViewer>
        </Grid>
      </Grid>

      <!-- DID 值编辑器 -->
      <StackPanel Grid.Row="4" Margin="0,6,0,2">
        <StackPanel Orientation="Horizontal">
          <TextBlock Text="DID Values" FontWeight="SemiBold" VerticalAlignment="Center"/>
          <Button Content="+DID" Command="{Binding EcuSimulator.AddDidValueCommand}" Margin="6,0,0,0" Padding="4,0"/>
          <Button Content="-DID" Command="{Binding EcuSimulator.RemoveDidValueCommand}" Margin="2,0,0,0" Padding="4,0"/>
        </StackPanel>
        <ItemsControl ItemsSource="{Binding EcuSimulator.DidValues}">
          <ItemsControl.ItemTemplate>
            <DataTemplate>
              <StackPanel Orientation="Horizontal" Margin="0,1">
                <TextBox Text="{Binding KeyHex, UpdateSourceTrigger=PropertyChanged}" Width="64" FontFamily="Consolas"/>
                <TextBox Text="{Binding BytesHex, UpdateSourceTrigger=PropertyChanged}" Width="150" FontFamily="Consolas" Margin="4,0,0,0"/>
              </StackPanel>
            </DataTemplate>
          </ItemsControl.ItemTemplate>
        </ItemsControl>
      </StackPanel>

      <!-- 只读图形预览（Task 6） -->
      <Border Grid.Row="5" BorderBrush="#DDDDDD" BorderThickness="1" Margin="0,6,0,0">
        <controls:EcuStatePreview DataContext="{Binding EcuSimulator.Script}"/>
      </Border>
    </Grid>
```

> **Key binding notes**: `EditableEcuState`/`EditableEcuTransition`/`EditableDidValue` are `ObservableObject` — all `[ObservableProperty]` bindings TwoWay by default where appropriate. `ResponseMode` ComboBox uses `SelectedValuePath="Tag"` (string "Static"/"Dynamic") — **do not use `SelectedItem` with hardcoded `ComboBoxItem`** (the InjectFault lesson from Phase 2). The `EcuResponseModeToVisibilityConverter` is defined in `Window.Resources` (Step 3).

- [ ] **Step 3: Add the `EcuResponseModeToVisibilityConverter` + `<controls:>` namespace**

In `HilStudioWindow.xaml`:
- Add namespace `xmlns:controls="clr-namespace:PeakCan.Host.App.Controls"` to `<Window ...>`.
- Add a value converter in `Window.Resources`:

```xml
    <local:EcuResponseModeToVisibilityConverter x:Key="EcuResponseModeToVisibilityConverter"/>
```

Create `src/PeakCan.Host.App/Controls/EcuResponseModeToVisibilityConverter.cs`:

```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using PeakCan.Host.App.ViewModels.EcuSimulator;

namespace PeakCan.Host.App.Controls;

/// <summary>Static→可见; Dynamic(参数=Dynamic)→可见。缺省参数时仅 Static 可见。</summary>
public sealed class EcuResponseModeToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var mode = value is EcuResponseMode m ? m : EcuResponseMode.Static;
        var showDynamic = string.Equals(parameter as string, "Dynamic", StringComparison.Ordinal);
        return (mode == EcuResponseMode.Dynamic) == showDynamic ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

- [ ] **Step 4: Verify the App builds and window opens**

Run: `dotnet build src/PeakCan.Host.App -v q` then launch app, open HIL Configuration Studio, confirm col4 renders (states list, transition editor, DID rows). The Canvas preview will render empty until Task 6.

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/Windows/HilStudioWindow.xaml src/PeakCan.Host.App/ViewModels/HilStudioViewModel.cs src/PeakCan.Host.App/Controls/EcuResponseModeToVisibilityConverter.cs
git commit -m "feat(studio): col4 ECU Simulator form UI — script props + state/transition editor + DID rows (Phase 3 T5)"
```

---

### Task 6: Read-only Canvas state-machine preview

**Files:**
- Create: `src/PeakCan.Host.App/Controls/EcuStatePreview.cs` (custom `Canvas` control)
- Modify: `src/PeakCan.Host.App/Windows/HilStudioWindow.xaml` (preview border already wired to `EcuStatePreview` in Task 5)

**Interfaces:**
- Consumes: `EcuSimulator.Script` (`EditableEcuScript` — `States` + `Transitions`)
- Produces: `EcuStatePreview` — renders state rounded-rects + condition arrows

- [ ] **Step 1: Implement the preview control**

Create `src/PeakCan.Host.App/Controls/EcuStatePreview.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PeakCan.Host.App.ViewModels.EcuSimulator;

namespace PeakCan.Host.App.Controls;

/// <summary>
/// 只读 ECU 状态机预览：按 States 顺序横排圆角矩形, 从 FromState 到 ToState 画条件箭头
/// （标 ServiceId hex）。数据来自 <see cref="EditableEcuScript"/>, 编辑变化时重绘。
/// </summary>
public sealed class EcuStatePreview : Canvas
{
    private const double NodeW = 120, NodeH = 40, Gap = 60, Top = 20;

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(EditableEcuScript), typeof(EcuStatePreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));

    public EditableEcuScript? Data
    {
        get => (EditableEcuScript?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (EcuStatePreview)d;
        if (e.OldValue is EditableEcuScript old) old.Changed -= c.Redraw;
        if (e.NewValue is EditableEcuScript n) n.Changed += c.Redraw;
        c.Redraw();
    }

    private void Redraw() { InvalidateMeasure(); InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (Data is not { States.Count: > 0 }) return;

        var positions = new Dictionary<string, Point>();
        double x = 10;
        foreach (var s in Data.States)
        {
            positions[s.Name] = new Point(x, Top);
            x += NodeW + Gap;
        }

        // 箭头: wildcard FromState(null) 不画入向箭头, 只画 ToState 出向
        foreach (var s in Data.States)
        foreach (var t in s.Transitions)
        {
            if (t.ToState is not null && positions.TryGetValue(t.ToState, out var to))
            {
                var from = positions[s.Name];
                var p1 = new Point(from.X + NodeW, from.Y + NodeH / 2);
                var p2 = new Point(to.X, to.Y + NodeH / 2);
                dc.DrawLine(new Pen(Brushes.Gray, 1), p1, p2);
                dc.DrawText(new FormattedText(
                    $"{t.ServiceIdHex}",
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Consolas"), 10, Brushes.DimGray),
                    new Point((p1.X + p2.X) / 2 - 20, (p1.Y + p2.Y) / 2 - 10));
            }
        }

        foreach (var s in Data.States)
        {
            var p = positions[s.Name];
            dc.DrawRoundedRectangle(Brushes.White, new Pen(Brushes.DarkSlateGray, 1.5),
                new Rect(p.X, p.Y, NodeW, NodeH), 6, 6);
            dc.DrawText(new FormattedText(s.Name,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.Black),
                new Point(p.X + 8, p.Y + 11));
        }
    }
}
```

- [ ] **Step 2: Wire DataContext → Data**

In `HilStudioWindow.xaml`, the preview Border from Task 5 binds `DataContext="{Binding EcuSimulator.Script}"`. The custom control reads `Data` from its `DataContext` via a binding:

```xml
    <Border Grid.Row="5" BorderBrush="#DDDDDD" BorderThickness="1" Margin="0,6,0,0">
      <controls:EcuStatePreview Data="{Binding}"/>
    </Border>
```

(`Data="{Binding}"` binds the Border's DataContext = `EcuSimulator.Script` into the `Data` DP.)

- [ ] **Step 3: Build + manual verify**

Run: `dotnet build src/PeakCan.Host.App -v q`. Launch, load a states script, confirm rounded-rects + arrows render and re-render as you edit state names / ToState.

- [ ] **Step 4: Commit**

```bash
git add src/PeakCan.Host.App/Controls/EcuStatePreview.cs src/PeakCan.Host.App/Windows/HilStudioWindow.xaml
git commit -m "feat(studio): read-only Canvas state-machine preview (Phase 3 T6)"
```

---

### Task 7: AppShell absorption wiring + end-to-end verification

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/AppShellViewModel/ViewSwitchFlow.cs` (3 sync sites → `_hilStudioViewModel.EcuSimulator`)
- Modify: `src/PeakCan.Host.App/AppShell.xaml` (menu: keep "ECU Script Editor" but point to Studio, or add note — see Step 1)
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/AppShellViewModelTests.cs` (compile-impact only if signature changes — minimize by using existing `_hilStudioViewModel`)

**Interfaces:**
- Consumes: `EcuSimulatorViewModel` contract from Task 3: `LoadInitialPath(string?)`, `LoadExternalAsync(string)`, `Reset()`, `FilePath`/`IsValidEcuScript` (`INotifyPropertyChanged`)

- [ ] **Step 1: Redirect the three sync sites to `EcuSimulator`**

In `ViewSwitchFlow.cs`, the AppShell already holds `_hilStudioViewModel`. Change:

```csharp
    private void OnEcuScriptPathSetExternally(string path)
    {
        // 吸收: BrowseEcu 外部加载同步进 Studio 的 ECU 面板（原 EcuScriptEditorWindow）
        if (_hilStudioWindow is not null)
            _ = _hilStudioViewModel.EcuSimulator.LoadExternalAsync(path);
    }

    private void OnEcuScriptEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EcuSimulatorViewModel.FilePath)
            or nameof(EcuSimulatorViewModel.IsValidEcuScript))
            SyncEcuScriptPath();
    }
```

Where `OnEcuScriptEditorPropertyChanged` is now subscribed to `_hilStudioViewModel.EcuSimulator` (see Step 2) — rename the method to `OnEcuSimulatorPropertyChanged` and update the subscription site.

Keep `ShowEcuScriptEditor` (menu) as-is for backward compatibility (independent editor still works); the **sync direction is absorbed by the Studio ECU panel**. Note this in the code comment.

- [ ] **Step 2: Wire the property subscription in the AppShell ctor**

Find where `_ecuScriptEditorViewModel.PropertyChanged += ...` is subscribed (likely ctor or a hook method) and add/subscribe the same to `_hilStudioViewModel.EcuSimulator.PropertyChanged += OnEcuSimulatorPropertyChanged;`. Keep the old `_ecuScriptEditorViewModel` subscription if `ShowEcuScriptEditor` is retained.

- [ ] **Step 3: Run full test suite + manual E2E**

Run: `dotnet test tests/PeakCan.Host.App.Tests -v q`
Expected: PASS (4 pre-existing Trace failures remain — not this branch's regression, verified on clean main).

Manual E2E:
1. Open Studio → col4 renders form + empty preview.
2. Load `tests/PeakCan.Host.Cli.Tests/Fixtures/e2e-ecu/bms_sim.json` (rules format) → migrates to wildcard state, DID/canIds populated (fixture has `rules`, no didValues).
3. Edit a transition SID → Save → reopen file in text editor → JSON is `states` format, `serviceId` hex, response `$type`.
4. Import ODX: pick a `.odx` without UDS services → red error text shown, no crash.
5. In main window, BrowseEcu select an ECU script → Studio ECU panel loads it (sync).
6. Run a suite with VirtualEcu mode against the saved `ecu-script.json` → engine consumes it (existing runtime, zero changes).

- [ ] **Step 4: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/AppShellViewModel/ViewSwitchFlow.cs
git commit -m "feat(studio): AppShell ECU sync absorbed by Studio ECU panel (Phase 3 T7)"
```

---

## Self-Review (done inline)

- **Spec coverage**: round-trip (T1/T2), form model Name/CanIds/StateMachine/DidValues/InitialState (T1/T3), state cards + transition table (T3/T5), DID byte editor (T5), static/dynamic response (T2/T5), Import ODX (T4), read-only preview (T6), absorb editor (T7), constraint #1 (T1/T2 reverse-swap + never-reparse), #2 ($type via EcuResponse), #3 (T4 try/catch), #4 (rules→wildcard migration in T1; no bidirectional JSON view — intentionally omitted, YAGNI).
- **Placeholder scan**: `_messageBoxConfirm` intentionally resolved in Task 4 with a compile-safe interim stub in Task 3. All code steps are concrete. The XAML col4 `WrapPanel?="False"` typo has been removed (it was invalid XAML).
- **Type consistency**: `FromEcuScript`/`ToJson`/`LoadFromText`/`GeneratorNames`/`EcuSimulator`/`EcuResponseMode` are used with identical names across Tasks 1–7. `HILJsonOptions.Default` referenced fully-qualified where needed.
