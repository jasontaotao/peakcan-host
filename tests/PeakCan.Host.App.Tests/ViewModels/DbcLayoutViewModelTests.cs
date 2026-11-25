using System.Collections.Specialized;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core.Dbc;
using Xunit;
using DbcValueType = PeakCan.HIL.Core.Dbc.ValueType;

namespace PeakCan.Host.App.Tests.ViewModels;

public class DbcLayoutViewModelTests
{
    private static Signal Sig(
        string name, ushort start, byte len,
        ByteOrder order = ByteOrder.LittleEndian,
        bool muxor = false, bool muxed = false, ushort? mux = null)
        => new(name, start, len, order, DbcValueType.Unsigned, 1, 0, 0, 0,
               "", Array.Empty<string>(), muxor, muxed, mux);

    private static Message MuxMessage()
        => new(0x100, "MuxMsg", 8, "ECU", new List<Signal>
        {
            Sig("Mode", 0, 4, muxor: true),
            Sig("A", 8, 8, muxed: true, mux: 0),
            Sig("B", 16, 8, muxed: true, mux: 1),
        }, true, 0);

    private static void RaiseLoaded(DbcService svc, params Message[] messages)
    {
        var doc = new DbcDocument(
            "", new List<Node>(), messages,
            new Dictionary<uint, Message>(),
            new Dictionary<string, ValueTable>());
        svc.GetType().GetEvent(nameof(DbcService.DbcLoaded))!.RaiseMethod(svc, doc);
    }

    private static DbcViewModel NewVm(out DbcService svc)
    {
        svc = new DbcService(NullLogger<DbcService>.Instance);
        return new DbcViewModel(
            svc, new SignalViewModel(), NullLogger<DbcViewModel>.Instance);
    }

    [Fact]
    public void Selecting_MuxMessage_Populates_MuxValues_And_DefaultsToMinimum()
    {
        var vm = NewVm(out var svc);
        RaiseLoaded(svc, MuxMessage());

        vm.SelectedMessage = vm.Messages.Single();

        vm.HasMux.Should().BeTrue();
        vm.MuxValues.Should().Equal((ushort)0, (ushort)1);
        vm.SelectedMuxValue.Should().Be((ushort?)0);
        vm.LayoutSnapshot.Should().NotBeNull();
        vm.LayoutSnapshot!.EffectiveMuxValue.Should().Be((ushort?)0);
        vm.VisibleSignals.Select(s => s.Name).Should().BeEquivalentTo("Mode", "A");
        vm.LayoutRows.Should().HaveCount(8);
    }

    [Fact]
    public void Switching_Mux_Recomputes_Snapshot_And_VisibleSignals()
    {
        var vm = NewVm(out var svc);
        RaiseLoaded(svc, MuxMessage());
        vm.SelectedMessage = vm.Messages.Single();

        vm.SelectedMuxValue = 1;

        vm.LayoutSnapshot!.EffectiveMuxValue.Should().Be((ushort?)1);
        vm.VisibleSignals.Select(s => s.Name).Should().BeEquivalentTo("Mode", "B");
    }

    [Fact]
    public void Selecting_Nothing_Clears_Layout()
    {
        var vm = NewVm(out var svc);
        RaiseLoaded(svc, MuxMessage());
        vm.SelectedMessage = vm.Messages.Single();

        vm.SelectedMessage = null;

        vm.LayoutSnapshot.Should().BeNull();
        vm.LayoutRows.Should().BeEmpty();
        vm.VisibleSignals.Should().BeEmpty();
        vm.HasMux.Should().BeFalse();
        vm.SelectedMuxValue.Should().BeNull();
    }

    [Fact]
    public void NoMuxMessage_Reports_HasMuxFalse()
    {
        var vm = NewVm(out var svc);
        RaiseLoaded(svc, new Message(
            0x200, "Plain", 8, "ECU",
            new List<Signal> { Sig("S", 0, 8) }, false, null));

        vm.SelectedMessage = vm.Messages.Single();

        vm.HasMux.Should().BeFalse();
        vm.SelectedMuxValue.Should().BeNull();
        vm.LayoutSnapshot!.EffectiveMuxValue.Should().BeNull();
    }

    [Fact]
    public void SelectedMuxWithoutBranch_ProducesWarning()
    {
        var vm = NewVm(out var svc);
        RaiseLoaded(svc, MuxMessage());
        vm.SelectedMessage = vm.Messages.Single();

        vm.SelectedMuxValue = 9;

        vm.LayoutSnapshot!.Warnings.Should().Contain(w => w.Code == "mux_no_match");
        vm.VisibleSignals.Select(s => s.Name).Should().BeEquivalentTo("Mode");
    }

    [Fact]
    public void LayoutRows_Are_Ordered_Bit7_To_Bit0()
    {
        var vm = NewVm(out var svc);
        RaiseLoaded(svc, MuxMessage());
        vm.SelectedMessage = vm.Messages.Single();

        vm.LayoutRows[0].Cells.Select(c => c.BitNumber)
            .Should().Equal(7, 6, 5, 4, 3, 2, 1, 0);
    }

    [Fact]
    public void VisibleSignalPalette_MatchesOwnedCells()
    {
        var vm = NewVm(out var svc);
        RaiseLoaded(svc, MuxMessage());
        vm.SelectedMessage = vm.Messages.Single();

        var mode = vm.VisibleSignals.Single(s => s.Name == "Mode");
        var a = vm.VisibleSignals.Single(s => s.Name == "A");

        mode.PaletteIndex.Should().Be(0);
        a.PaletteIndex.Should().Be(1);
        vm.LayoutRows[1].Cells.Should().OnlyContain(c => c.PaletteIndex == a.PaletteIndex);
    }

    [Fact]
    public void MessageWithoutSignals_ShowsEmptyPlaceholder()
    {
        var vm = NewVm(out var svc);
        RaiseLoaded(svc, new Message(
            0x300, "Empty", 8, "ECU", new List<Signal>(), false, null));

        vm.SelectedMessage = vm.Messages.Single();

        vm.LayoutEmptyText.Should().Contain("无信号");
    }

    [Fact]
    public void Reload_Clears_Selection_And_Layout()
    {
        var vm = NewVm(out var svc);
        RaiseLoaded(svc, MuxMessage());
        vm.SelectedMessage = vm.Messages.Single();
        vm.LayoutSnapshot.Should().NotBeNull("precondition");

        RaiseLoaded(svc, new Message(
            0x200, "Other", 8, "ECU",
            new List<Signal> { Sig("S", 0, 8) }, false, null));

        vm.SelectedMessage.Should().BeNull();
        vm.LayoutSnapshot.Should().BeNull();
        vm.LayoutRows.Should().BeEmpty();
    }

    [Fact]
    public void Search_Keeps_Selection_When_Message_Still_Visible()
    {
        var vm = NewVm(out var svc);
        RaiseLoaded(svc,
            new Message(0x100, "Alpha", 8, "ECU",
                new List<Signal> { Sig("S", 0, 8) }, false, null),
            new Message(0x200, "Beta", 8, "ECU",
                new List<Signal> { Sig("S", 0, 8) }, false, null));
        vm.SelectedMessage = vm.Messages.Single(m => m.Name == "Alpha");

        // 模拟 DataGrid TwoWay 绑定：FilteredMessages 被 Reset 时选中项会被清空，
        // 单测里没有 DataGrid，就手动复现这一步，否则“搜索后保持选中”会假绿。
        vm.FilteredMessages.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
                vm.SelectedMessage = null;
        };

        vm.SearchText = "Alpha";

        vm.SelectedMessage.Should().NotBeNull();
        vm.SelectedMessage!.Name.Should().Be("Alpha");
        vm.LayoutSnapshot.Should().NotBeNull();
    }

    [Fact]
    public void Search_Clears_Selection_When_Message_Is_Filtered_Out()
    {
        var vm = NewVm(out var svc);
        RaiseLoaded(svc,
            new Message(0x100, "Alpha", 8, "ECU",
                new List<Signal> { Sig("S", 0, 8) }, false, null),
            new Message(0x200, "Beta", 8, "ECU",
                new List<Signal> { Sig("S", 0, 8) }, false, null));
        vm.SelectedMessage = vm.Messages.Single(m => m.Name == "Alpha");

        vm.SearchText = "Beta";

        vm.SelectedMessage.Should().BeNull();
        vm.LayoutSnapshot.Should().BeNull();
    }
}
