using FluentAssertions;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Dbc;
using Xunit;
using ValueType = PeakCan.Host.Core.Dbc.ValueType;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// v3.16.0 MINOR: tests for the DBC tree picker view-model used by
/// the + Add to watch… dialog.
/// </summary>
public class DbcTreePickerViewModelTests
{
    private static DbcDocument DocWithTwoMessages() => new(
        Version: "",
        Nodes: System.Array.Empty<Node>(),
        Messages: new[]
        {
            new Message(Id: 0x100, Name: "M_RPM", Dlc: 8, Sender: "ECU",
                Signals: new[]
                {
                    new Signal(Name: "RPM", StartBit: 0, Length: 16,
                               Order: ByteOrder.LittleEndian,
                               ValueType: ValueType.Unsigned,
                               Factor: 1.0, Offset: 0.0,
                               Min: 0, Max: 1000, Unit: "rpm",
                               Receivers: System.Array.Empty<string>()),
                    new Signal(Name: "Speed", StartBit: 16, Length: 16,
                               Order: ByteOrder.LittleEndian,
                               ValueType: ValueType.Unsigned,
                               Factor: 1.0, Offset: 0.0,
                               Min: 0, Max: 200, Unit: "kph",
                               Receivers: System.Array.Empty<string>()),
                },
                IsMultiplexed: false, MultiplexorSignalIndex: null),
            new Message(Id: 0x200, Name: "M_TEMP", Dlc: 8, Sender: "ECU",
                Signals: new[]
                {
                    new Signal(Name: "Temp", StartBit: 0, Length: 16,
                               Order: ByteOrder.LittleEndian,
                               ValueType: ValueType.Unsigned,
                               Factor: 1.0, Offset: 0.0,
                               Min: -50, Max: 200, Unit: "C",
                               Receivers: System.Array.Empty<string>()),
                },
                IsMultiplexed: false, MultiplexorSignalIndex: null),
        },
        MessagesById: new Dictionary<uint, Message>(),
        ValueTables: new Dictionary<string, ValueTable>());

    [Fact]
    public void BuildTree_WalksDbcMessages_AsHierarchicalTree()
    {
        var vm = new DbcTreePickerViewModel(DocWithTwoMessages());

        vm.Roots.Should().HaveCount(2);
        vm.Roots[0].MessageName.Should().Be("M_RPM");
        vm.Roots[0].Children.Should().HaveCount(2);  // RPM + Speed
        vm.Roots[1].MessageName.Should().Be("M_TEMP");
        vm.Roots[1].Children.Should().HaveCount(1);  // Temp
    }

    [Fact]
    public void BuildTree_NoDbcLoaded_LeavesTreeEmpty()
    {
        var vm = new DbcTreePickerViewModel(null);
        vm.Roots.Should().BeEmpty();
    }

    [Fact]
    public void ToggleSelection_AddsThenRemovesSignalFromSelection()
    {
        var vm = new DbcTreePickerViewModel(DocWithTwoMessages());
        var sigNode = vm.Roots[0].Children[0];

        vm.ToggleSelection(sigNode);
        vm.SelectedSignals.Should().ContainSingle();
        vm.ToggleSelection(sigNode);
        vm.SelectedSignals.Should().BeEmpty();
    }

    [Fact]
    public void ToggleSelection_IgnoresMessageNodes()
    {
        var vm = new DbcTreePickerViewModel(DocWithTwoMessages());
        var msgNode = vm.Roots[0];  // message node, not signal

        vm.ToggleSelection(msgNode);
        vm.SelectedSignals.Should().BeEmpty(
            "v3.16.0 MINOR: only signal nodes are selectable, not message headers");
    }

    [Fact]
    public void GetSelectedTuples_ReturnsCanIdAndSignalName()
    {
        var vm = new DbcTreePickerViewModel(DocWithTwoMessages());
        vm.ToggleSelection(vm.Roots[0].Children[0]);  // RPM
        vm.ToggleSelection(vm.Roots[0].Children[1]);  // Speed

        var tuples = vm.GetSelectedTuples();
        tuples.Should().HaveCount(2);
        tuples.Should().Contain((0x100u, "RPM"));
        tuples.Should().Contain((0x100u, "Speed"));
    }

    [Fact]
    public void DbcTreeNode_Matches_SearchFiltersByName()
    {
        var vm = new DbcTreePickerViewModel(DocWithTwoMessages());
        var rpmNode = vm.Roots[0].Children[0];

        rpmNode.Matches("").Should().BeTrue("empty search matches all");
        rpmNode.Matches("RPM").Should().BeTrue();
        rpmNode.Matches("rpm").Should().BeTrue("case-insensitive");
        rpmNode.Matches("Speed").Should().BeFalse();
    }
}