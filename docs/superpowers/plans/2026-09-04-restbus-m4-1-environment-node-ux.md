# Restbus M4.1: Environment Node UX Patch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Make generated Restbus nodes inspectable and editable in Studio: show message details, distinguish DBC-generated nodes from templates, support delete/message enable, and assign suite channels.

**Architecture:** Keep the hil-core `RestbusNode` model unchanged. Add presentation-only view models in Studio, mutate immutable records through `with`, bind the existing EnvironmentTab card list to expanded message rows, and wire suite-level channel names through `EnvironmentTabViewModel`.

**Tech Stack:** C# / .NET 10, WPF, CommunityToolkit.Mvvm, xUnit, PeakCan.HIL.Core 0.18.0.

**Spec:** `D:\claude_proj2\peakcan-host\docs\superpowers\specs\2026-09-03-restbus-node-unification-design.md` (§4.4 three UI iron laws, §14 M4, §15 R5 multi-channel)

## Global Constraints

- Do not change hil-core model serialization or bump package version.
- Do not add I/O or vendor SDK dependencies to Studio view models.
- Preserve the M4 rule: duplicate DBC generation must be rejected, not silently replace a node.
- Preserve ECA rules: DBC-generated nodes must keep `Rules = []`.
- `NodeMessage.Enabled` is per message; there is no node-level enabled field.
- `RestbusNode.Channel` must be null for a single-channel suite and must match a declared `ChannelConfig.Name` when suite channels exist.
- Conventional commits (`feat`, `fix`, `chore`).
- Studio tests use xUnit; `using Xunit;` is required.

## Repo / Preflight

- Repo: `D:\claude_proj2\peakcan-studio`
- Plan file lives in: `D:\claude_proj2\peakcan-host\docs\superpowers\plans\2026-09-04-restbus-m4-1-environment-node-ux.md`
- Start from clean `main`.

- [x] **Step 0.1: Verify branch and dependencies**

```powershell
git -C D:\claude_proj2\peakcan-studio status --short --branch
git -C D:\claude_proj2\peakcan-studio switch main
git -C D:\claude_proj2\peakcan-studio pull origin main
git -C D:\claude_proj2\peakcan-studio switch -c feat/restbus-env-node-ux
Test-Path D:\claude_proj2\peakcan-hil-core\src\PeakCan.HIL.Core\PeakCan.HIL.Core.csproj
```

Expected: clean status, new `feat/restbus-env-node-ux` branch, and `True` for the hil-core project path.

---

### Task 1: Message detail presentation model

**Files:**
- Create: `src/PeakCan.Studio.App/ViewModels/Restbus/EnvironmentNodeMessageViewModel.cs`
- Test: `tests/PeakCan.Studio.App.Tests/Restbus/EnvironmentNodeMessageViewModelTests.cs`

**Interfaces:**
- Consumes: `PeakCan.HIL.Core.HIL.Environment.NodeMessage`, `CanMessageRef`, `J1939MessageRef`, `FixedHexSource`, `DbcSignalsSource`, `ScriptCallbackSource`.
- Produces: `EnvironmentNodeMessageViewModel(NodeMessage message, int index, Action<int, bool> setEnabled)` with:
  - `string IdText`
  - `bool IsExtended`
  - `int IntervalMs`
  - `string PayloadText`
  - `string AutomationText`
  - `bool Enabled`
- `Enabled` setter must invoke `setEnabled(index, value)`.

- [x] **Step 1: Write failing tests**

Create `tests/PeakCan.Studio.App.Tests/Restbus/EnvironmentNodeMessageViewModelTests.cs`:

```csharp
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.Studio.App.ViewModels.Restbus;
using Xunit;

namespace PeakCan.Studio.App.Tests.Restbus;

public class EnvironmentNodeMessageViewModelTests
{
    [Fact]
    public void FormatsCanRefPayloadCounterAndChecksum()
    {
        var message = new NodeMessage(
            new CanMessageRef(0x1F0, true),
            250,
            new DbcSignalsSource("CRM"),
            true,
            true,
            new CounterConfig(0, 4),
            new ChecksumConfig(8, 8));

        var vm = new EnvironmentNodeMessageViewModel(message, 2, (_, _) => { });

        Assert.Equal("0x1F0", vm.IdText);
        Assert.True(vm.IsExtended);
        Assert.Equal(250, vm.IntervalMs);
        Assert.Equal("DBC: CRM", vm.PayloadText);
        Assert.Contains("Counter: bit 0 x 4", vm.AutomationText);
        Assert.Contains("Checksum: bit 8 x 8", vm.AutomationText);
        Assert.True(vm.Enabled);
    }

    [Fact]
    public void EnabledSetter_InvokesParentCallback()
    {
        var called = false;
        var message = new NodeMessage(new CanMessageRef(0x123, false), 100, new DbcSignalsSource("CRM"));
        var vm = new EnvironmentNodeMessageViewModel(message, 0, (_, enabled) => called = enabled);

        vm.Enabled = false;

        Assert.False(vm.Enabled);
        Assert.True(called);
    }

    [Fact]
    public void FormatsFixedHexPayloadWithoutAutomation()
    {
        var message = new NodeMessage(new CanMessageRef(0x123, false), 20, new FixedHexSource("01 02"));

        var vm = new EnvironmentNodeMessageViewModel(message, 0, (_, _) => { });

        Assert.Equal("0x123", vm.IdText);
        Assert.False(vm.IsExtended);
        Assert.Equal("Hex: 01 02", vm.PayloadText);
        Assert.Equal("None", vm.AutomationText);
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

```powershell
dotnet test tests/PeakCan.Studio.App.Tests --filter "FullyQualifiedName~EnvironmentNodeMessageViewModelTests" --no-restore
```

Expected: compile failure because `EnvironmentNodeMessageViewModel` does not exist.

- [x] **Step 3: Implement the presentation model**

Create `src/PeakCan.Studio.App/ViewModels/Restbus/EnvironmentNodeMessageViewModel.cs`:

```csharp
using PeakCan.HIL.Core.HIL.Environment;

namespace PeakCan.Studio.App.ViewModels.Restbus;

/// <summary>Read-only presentation row for one NodeMessage. Enabled changes are delegated to the owning node.</summary>
public sealed class EnvironmentNodeMessageViewModel
{
    private readonly Action<int, bool> _setEnabled;
    private bool _enabled;

    public EnvironmentNodeMessageViewModel(NodeMessage message, int index, Action<int, bool> setEnabled)
    {
        Message = message;
        Index = index;
        _setEnabled = setEnabled;
        _enabled = message.Enabled;
    }

    public NodeMessage Message { get; }
    public int Index { get; }

    public string IdText => Message.Ref switch
    {
        CanMessageRef can => $"0x{can.Id:X}",
        J1939MessageRef j1939 => $"PGN 0x{j1939.Pgn:X}",
        _ => "Unknown ref"
    };

    public bool IsExtended => Message.Ref is CanMessageRef { IsExtended: true };
    public int IntervalMs => Message.IntervalMs;

    public string PayloadText => Message.Payload switch
    {
        DbcSignalsSource dbc => $"DBC: {dbc.MessageName}",
        FixedHexSource hex => $"Hex: {hex.Hex}",
        ScriptCallbackSource script => $"Script: {script.CallbackRef}",
        _ => "Unknown payload"
    };

    public string AutomationText
    {
        get
        {
            if (Message.AutoCounter is null && Message.AutoChecksum is null)
                return "None";

            var parts = new List<string>();
            if (Message.AutoCounter is { } counter)
                parts.Add($"Counter: bit {counter.StartBit} x {counter.Length}");
            if (Message.AutoChecksum is { } checksum)
                parts.Add($"Checksum: bit {checksum.StartBit} x {checksum.Length}");
            return string.Join("; ", parts);
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            _setEnabled(Index, value);
        }
    }
}
```

- [x] **Step 4: Run tests**

```powershell
dotnet test tests/PeakCan.Studio.App.Tests --filter "FullyQualifiedName~EnvironmentNodeMessageViewModelTests" --no-restore
```

Expected: 3/3 PASS.

- [x] **Step 5: Commit**

```powershell
git add src/PeakCan.Studio.App/ViewModels/Restbus/EnvironmentNodeMessageViewModel.cs tests/PeakCan.Studio.App.Tests/Restbus/EnvironmentNodeMessageViewModelTests.cs
git commit -m "feat(studio): format environment node message details"
```

---

### Task 2: Node wrapper mutation, source label, and message toggles

**Files:**
- Modify: `src/PeakCan.Studio.App/ViewModels/Restbus/EnvironmentNodeViewModel.cs`
- Test: `tests/PeakCan.Studio.App.Tests/Restbus/EnvironmentNodeViewModelTests.cs`

**Interfaces:**
- Consumes: `RestbusNode`, `NodeMessage`, `EnvironmentNodeMessageViewModel` from Task 1.
- Produces:
  - `string SourceLabel`
  - `string? Channel { get; set; }`
  - `ObservableCollection<string> AvailableChannels`
  - `IReadOnlyList<EnvironmentNodeMessageViewModel> Messages`
  - `void SetAvailableChannels(IReadOnlyList<string> channels)`
  - `void SetMessageEnabled(int index, bool enabled)`
  - `RestbusNode ToNode()` returns the latest immutable node.

- [x] **Step 1: Write failing tests**

Create `tests/PeakCan.Studio.App.Tests/Restbus/EnvironmentNodeViewModelTests.cs`:

```csharp
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.Studio.App.ViewModels.Restbus;
using Xunit;

namespace PeakCan.Studio.App.Tests.Restbus;

public class EnvironmentNodeViewModelTests
{
    private static RestbusNode CreateNode() => new()
    {
        Name = "Charger",
        Identity = new RawCanNodeIdentity(),
        Messages =
        [
            new NodeMessage(new CanMessageRef(0x100, false), 100, new DbcSignalsSource("CRM")),
            new NodeMessage(new CanMessageRef(0x101, false), 200, new DbcSignalsSource("BCL")),
        ],
    };

    [Fact]
    public void SourceLabel_DistinguishesDbcFromTemplate()
    {
        var dbc = new EnvironmentNodeViewModel(CreateNode());
        Assert.Equal("Source: DBC", dbc.SourceLabel);

        var template = new EnvironmentNodeViewModel(CreateNode() with
        {
            Trial = new TrialContract("gbt27930-charger", [], [])
        });
        Assert.Equal("Template: gbt27930-charger", template.SourceLabel);
    }

    [Fact]
    public void SetMessageEnabled_ProducesUpdatedImmutableNode()
    {
        var vm = new EnvironmentNodeViewModel(CreateNode());

        vm.SetMessageEnabled(1, false);

        Assert.True(vm.Messages[0].Enabled);
        Assert.False(vm.Messages[1].Enabled);
        Assert.False(vm.ToNode().Messages[1].Enabled);
    }

    [Fact]
    public void SetAvailableChannels_SelectsFirstChannelAndPreservesValidChannel()
    {
        var vm = new EnvironmentNodeViewModel(CreateNode());

        vm.SetAvailableChannels(["bus-a", "bus-b"]);
        Assert.Equal("bus-a", vm.Channel);

        vm.Channel = "bus-b";
        vm.SetAvailableChannels(["bus-a", "bus-b"]);
        Assert.Equal("bus-b", vm.Channel);
        Assert.Equal("bus-b", vm.ToNode().Channel);
    }

    [Fact]
    public void SetAvailableChannels_EmptyList_ClearsChannel()
    {
        var vm = new EnvironmentNodeViewModel(CreateNode() with { Channel = "bus-a" });

        vm.SetAvailableChannels([]);

        Assert.Null(vm.Channel);
        Assert.Null(vm.ToNode().Channel);
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

```powershell
dotnet test tests/PeakCan.Studio.App.Tests --filter "FullyQualifiedName~EnvironmentNodeViewModelTests" --no-restore
```

Expected: compile failures for `SourceLabel`, `Messages`, `SetAvailableChannels`, and `SetMessageEnabled`.

- [x] **Step 3: Implement node mutation and presentation**

Replace `src/PeakCan.Studio.App/ViewModels/Restbus/EnvironmentNodeViewModel.cs` with:

```csharp
using System.Collections.ObjectModel;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.Studio.App.Services.Restbus;

namespace PeakCan.Studio.App.ViewModels.Restbus;

/// <summary>Editable presentation wrapper for one immutable RestbusNode.</summary>
public sealed class EnvironmentNodeViewModel
{
    private RestbusNode _node;

    public EnvironmentNodeViewModel(RestbusNode node, IReadOnlyList<string>? availableChannels = null)
    {
        _node = node;
        Messages = [.. node.Messages.Select((message, index) =>
            new EnvironmentNodeMessageViewModel(message, index, SetMessageEnabled))];

        if (availableChannels is not null)
            SetAvailableChannels(availableChannels);
    }

    public string Name => _node.Name;
    public string? Tag => _node.Tag;
    public int MessageCount => _node.Messages.Count;
    public int RuleCount => _node.Rules.Count;
    public string? TemplateId => _node.Trial?.TemplateId;

    public string SourceLabel => TemplateId is null ? "Source: DBC" : $"Template: {TemplateId}";

    public string? Channel
    {
        get => _node.Channel;
        set
        {
            if (_node.Channel == value) return;
            _node = _node with { Channel = value };
        }
    }

    public ObservableCollection<string> AvailableChannels { get; } = [];
    public IReadOnlyList<EnvironmentNodeMessageViewModel> Messages { get; }

    public void SetAvailableChannels(IReadOnlyList<string> channels)
    {
        AvailableChannels.Clear();
        foreach (var channel in channels)
            AvailableChannels.Add(channel);

        if (channels.Count == 0)
        {
            Channel = null;
            return;
        }

        if (Channel is null || !channels.Contains(Channel))
            Channel = channels[0];
    }

    public void SetMessageEnabled(int index, bool enabled)
    {
        if (index < 0 || index >= _node.Messages.Count) return;
        _node = _node with
        {
            Messages = [.. _node.Messages.Select((message, i) =>
                i == index ? message with { Enabled = enabled } : message)]
        };
    }

    public RestbusNode ToNode() => _node;

    public RestbusNode SaveAsTemplate(RestbusTemplateLibrary lib, bool keepSignalOverrides = false)
    {
        var trial = _node.Trial;
        var shortId = Guid.NewGuid().ToString("N")[..8];
        var templateId = $"user-{_node.Name.ToLowerInvariant()}-{shortId}";
        var template = _node with
        {
            SignalOverrides = keepSignalOverrides ? _node.SignalOverrides : null,
            Trial = new TrialContract(templateId, trial?.Handshake ?? [], trial?.RequiredDbcMessages ?? [])
        };
        lib.Save(template);
        return template;
    }
}
```

- [x] **Step 4: Run Restbus tests**

```powershell
dotnet test tests/PeakCan.Studio.App.Tests --filter "FullyQualifiedName~Restbus" --no-restore
```

Expected: all Restbus tests PASS. Existing generation tests must remain green.

- [x] **Step 5: Commit**

```powershell
git add src/PeakCan.Studio.App/ViewModels/Restbus/EnvironmentNodeViewModel.cs tests/PeakCan.Studio.App.Tests/Restbus/EnvironmentNodeViewModelTests.cs
git commit -m "feat(studio): expose environment node source and message toggles"
```

---

### Task 3: Wire channels, generation defaults, and save reminder

**Files:**
- Modify: `src/PeakCan.Studio.App/ViewModels/Restbus/EnvironmentTabViewModel.cs`
- Test: `tests/PeakCan.Studio.App.Tests/Restbus/EnvironmentTabChannelTests.cs`

**Interfaces:**
- Consumes: `EnvironmentNodeViewModel.SetAvailableChannels`, `EnvironmentNodeViewModel.Channel`, `RestbusNodeValidator.Validate`, `ChannelConfig`.
- Produces:
  - `EnvironmentTabViewModel.SetAvailableChannels(IReadOnlyList<string> channels)`
  - On `LoadFromSuite`, channel names come from `suite.Channels`.
  - On generation/apply with channels, a node defaults to the first channel.
  - Generated status includes the save reminder: `Use Save/SaveAs to persist nodes in the suite.`

- [x] **Step 1: Write failing tests**

Create `tests/PeakCan.Studio.App.Tests/Restbus/EnvironmentTabChannelTests.cs`:

```csharp
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.Studio.App.ViewModels.Restbus;
using Xunit;

namespace PeakCan.Studio.App.Tests.Restbus;

public class EnvironmentTabChannelTests
{
    private static DbcDocument Parse(string dbc)
    {
        var result = DbcParser.Parse(dbc);
        Assert.True(result.IsSuccess);
        return result.Value!;
    }

    private static DbcDocument ChargerDbc => Parse("""
BU_: Charger BMS;

BO_ 512 CRM: 8 Charger
 SG_ S1 : 0|8@1+ (1,0) [0|255] "" BMS

BA_DEF_ BO_ "GenMsgCycleTime" INT 0 10000;
BA_ "GenMsgCycleTime" BO_ 512 250;
""");

    [Fact]
    public void GenerateFromDbc_WithChannels_AssignsFirstChannel()
    {
        var vm = new EnvironmentTabViewModel(seedProvider: () => []);
        vm.SetAvailableChannels(["bus-a", "bus-b"]);

        Assert.True(vm.GenerateFromDbc(ChargerDbc, "Charger"));

        Assert.Equal("bus-a", vm.Nodes[0].Channel);
        Assert.Equal("bus-a", vm.Nodes[0].ToNode().Channel);
    }

    [Fact]
    public void GenerateFromDbc_WithoutChannels_KeepsChannelNull()
    {
        var vm = new EnvironmentTabViewModel(seedProvider: () => []);

        Assert.True(vm.GenerateFromDbc(ChargerDbc, "Charger"));

        Assert.Null(vm.Nodes[0].Channel);
        Assert.Contains("Use Save/SaveAs", vm.GenerationStatus);
    }

    [Fact]
    public void LoadFromSuite_PropagatesChannelsToNodeCards()
    {
        var node = new RestbusNode
        {
            Name = "Charger",
            Identity = new RawCanNodeIdentity(),
            Messages =
            [
                new NodeMessage(new CanMessageRef(0x100, false), 100, new DbcSignalsSource("CRM"))
            ],
        };
        var suite = new TestSuite("Test", [], [], [], null)
        {
            Channels =
            [
                new ChannelConfig("bus-a", "", null, false),
                new ChannelConfig("bus-b", "", null, false),
            ],
            Environment = [node],
        };
        var vm = new EnvironmentTabViewModel(seedProvider: () => []);

        vm.LoadFromSuite(suite);

        var card = Assert.Single(vm.Nodes);
        Assert.Equal("bus-a", card.Channel);
        Assert.Equal(["bus-a", "bus-b"], card.AvailableChannels);
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

```powershell
dotnet test tests/PeakCan.Studio.App.Tests --filter "FullyQualifiedName~EnvironmentTabChannelTests" --no-restore
```

Expected: compile failure for `SetAvailableChannels`; after adding only a stub, assertions for default channel/save reminder fail.

- [x] **Step 3: Implement channel wiring**

In `src/PeakCan.Studio.App/ViewModels/Restbus/EnvironmentTabViewModel.cs`:

Add this field next to `_currentDbc`:

```csharp
private IReadOnlyList<string> _availableChannels = [];
```

Add this method after `SetDbc(DbcDocument? dbc)`:

```csharp
/// <summary>Sets suite channel names available to every node card. Empty = single-channel suite.</summary>
public void SetAvailableChannels(IReadOnlyList<string> channels)
{
    _availableChannels = [.. channels.Distinct()];
    foreach (var node in Nodes)
        node.SetAvailableChannels(_availableChannels);
}
```

Change `LoadFromSuite` to set channels before adding node cards:

```csharp
public void LoadFromSuite(TestSuite suite)
{
    Nodes.Clear();
    SetAvailableChannels(suite.Channels?.Select(c => c.Name).ToArray() ?? []);
    foreach (var n in suite.Environment ?? [])
        Nodes.Add(new EnvironmentNodeViewModel(n, _availableChannels));
}
```

Change `ApplyTemplate` to create the card with channels:

```csharp
Nodes.Add(new EnvironmentNodeViewModel(node, _availableChannels));
```

Change the private `GenerateFromDbc` validation and node creation:

```csharp
var channel = _availableChannels.Count == 0 ? null : _availableChannels[0];
var candidate = result.Node with { Channel = channel };
var channels = _availableChannels.Count == 0
    ? null
    : _availableChannels.Select(name => new ChannelConfig(name, "", null, false)).ToArray();
var validationErrors = RestbusNodeValidator.Validate([candidate], channels, null);
```

After validation succeeds, replace the existing node-creation block with:

```csharp
Nodes.Add(new EnvironmentNodeViewModel(candidate, _availableChannels));
var status = $"Generated '{candidate.Name}'. Use Save/SaveAs to persist nodes in the suite.";
if (result.Warnings.Count > 0)
    status += " " + string.Join(" ", result.Warnings);
```

Keep the duplicate-name rejection unchanged.

- [x] **Step 4: Run Restbus tests**

```powershell
dotnet test tests/PeakCan.Studio.App.Tests --filter "FullyQualifiedName~Restbus" --no-restore
```

Expected: all Restbus tests PASS, including previous generation/template tests.

- [x] **Step 5: Commit**

```powershell
git add src/PeakCan.Studio.App/ViewModels/Restbus/EnvironmentTabViewModel.cs tests/PeakCan.Studio.App.Tests/Restbus/EnvironmentTabChannelTests.cs
git commit -m "feat(studio): assign suite channels to generated environment nodes"
```

---

### Task 4: Bind details, channel selector, and delete button in XAML

**Files:**
- Modify: `src/PeakCan.Studio.App/Views/EnvironmentTab.xaml`

**Interfaces:**
- Consumes: `EnvironmentNodeViewModel.SourceLabel`, `Messages`, `AvailableChannels`, `Channel`; `EnvironmentTabViewModel.RemoveNode(string nodeName)`.
- Produces: WPF bindings for expanded message rows, channel selection, and node deletion.

- [x] **Step 1: Replace the node card template**

In `src/PeakCan.Studio.App/Views/EnvironmentTab.xaml`, replace the current `ItemsControl` under `已启用环境节点` with:

```xml
<ItemsControl ItemsSource="{Binding Nodes}">
  <ItemsControl.ItemTemplate>
    <DataTemplate>
      <Border BorderBrush="Gray" BorderThickness="1" CornerRadius="4" Padding="8" Margin="0,0,0,4">
        <StackPanel>
          <StackPanel Orientation="Horizontal">
            <TextBlock Text="{Binding Name}" FontWeight="Bold" Margin="0,0,8,0"/>
            <TextBlock Text="{Binding MessageCount, StringFormat={}{0} 条周期帧}" Margin="0,0,8,0"/>
            <TextBlock Text="{Binding SourceLabel}" Foreground="Gray" Margin="0,0,8,0"/>
            <TextBlock Text="通道:" VerticalAlignment="Center" Margin="0,0,4,0"/>
            <ComboBox ItemsSource="{Binding AvailableChannels}"
                      SelectedItem="{Binding Channel, Mode=TwoWay}"
                      MinWidth="90"/>
            <Button Content="删除"
                    Command="{Binding DataContext.RemoveNodeCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
                    CommandParameter="{Binding Name}"
                    Padding="6,1" Margin="12,0,0,0"/>
          </StackPanel>

          <Expander Header="报文明细" Margin="0,8,0,0">
            <ItemsControl ItemsSource="{Binding Messages}" Margin="12,4,0,0">
              <ItemsControl.ItemTemplate>
                <DataTemplate>
                  <Border BorderBrush="LightGray" BorderThickness="0,0,0,1" Padding="0,4">
                    <StackPanel Orientation="Horizontal">
                      <CheckBox IsChecked="{Binding Enabled, Mode=TwoWay}" VerticalAlignment="Center"/>
                      <TextBlock Text="{Binding IdText}" FontWeight="SemiBold" Margin="8,0,8,0"/>
                      <TextBlock Text="{Binding IntervalMs, StringFormat={}{0} ms}" Margin="0,0,8,0"/>
                      <TextBlock Text="{Binding PayloadText}" Margin="0,0,8,0"/>
                      <TextBlock Text="{Binding AutomationText}" Foreground="Gray"/>
                    </StackPanel>
                  </Border>
                </DataTemplate>
              </ItemsControl.ItemTemplate>
            </ItemsControl>
          </Expander>
        </StackPanel>
      </Border>
    </DataTemplate>
  </ItemsControl.ItemTemplate>
</ItemsControl>
```

Also add this status text directly below the current generation status text:

```xml
<TextBlock Text="生成后请 Save/SaveAs，节点才会写入 suite JSON。"
           Foreground="Gray" TextWrapping="Wrap" Margin="0,4,0,0"/>
```

- [x] **Step 2: Build the app**

```powershell
dotnet build src/PeakCan.Studio.App --no-restore
```

Expected: build PASS with 0 errors. Existing unrelated WPF warnings may remain.

- [x] **Step 3: Commit**

```powershell
git add src/PeakCan.Studio.App/Views/EnvironmentTab.xaml
git commit -m "feat(studio): add environment node details, channel selector, and delete"
```

---

### Task 5: Regression and PR

- [x] **Step 1: Run all Restbus tests and full Studio app test filter**

```powershell
dotnet test tests/PeakCan.Studio.App.Tests --filter "FullyQualifiedName~Restbus" --no-restore
dotnet build src/PeakCan.Studio.App --no-restore
```

Expected: all Restbus tests PASS and app build PASS.

- [x] **Step 2: Run host infrastructure regression**

From `D:\claude_proj2\peakcan-host`:

```powershell
dotnet test tests/PeakCan.Host.Infrastructure.Tests --no-restore
```

Expected: 612 passed / 2 hardware-specific skipped. This confirms Studio-only changes did not affect the runtime contract.

- [x] **Step 3: Commit any formatting-only fixes if required**

```powershell
git diff --check
git add -A
git commit -m "chore(studio): final restbus ux patch verification"
```

If `git status --porcelain` is empty, skip this commit.

- [x] **Step 4: Push and open PR**

```powershell
git -C D:\claude_proj2\peakcan-studio push -u origin feat/restbus-env-node-ux
gh pr create --repo jasontaotao/peakcan-studio --base main --head feat/restbus-env-node-ux --title "feat: environment node details and channel UX" --body "Adds message details, DBC/source label, message enable toggles, channel assignment, and node deletion to the Restbus environment tab."
```

Expected: PR URL printed.
