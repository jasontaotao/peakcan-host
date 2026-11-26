using System.Collections.Specialized;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Tests.Collections;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core.Dbc;
using Xunit;
using DbcValueType = PeakCan.HIL.Core.Dbc.ValueType;

namespace PeakCan.Host.App.Tests.ViewModels;

public class DbcLayoutViewModelTests
{
    // Timer 用例依赖 RunOnUiPost 的“无 Application → 就地执行”路径；清掉兄弟测试类
    // 可能泄漏的 Application，避免封送到无人泵的 dispatcher（与 DbcDecodeBackgroundServiceTests 同口径）。
    public DbcLayoutViewModelTests() => LeakedApplicationReset.CleanupLeakedApplication();

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

    private static Message SimpleMessage()
        => new(0x100, "Simple", 8, "ECU", new List<Signal>
        {
            Sig("Bits", 0, 8),
        }, false, null);

    private static void RaiseLoaded(DbcService svc, params Message[] messages)
    {
        var doc = new DbcDocument(
            "", new List<Node>(), messages,
            new Dictionary<uint, Message>(),
            new Dictionary<string, ValueTable>());
        svc.GetType().GetEvent(nameof(DbcService.DbcLoaded))!.RaiseMethod(svc, doc);
    }

    private static DbcViewModel NewVm(out DbcService svc, LiveFrameSlot? liveSlot = null)
    {
        svc = new DbcService(NullLogger<DbcService>.Instance);
        return new DbcViewModel(
            svc, new SignalViewModel(), NullLogger<DbcViewModel>.Instance,
            liveSlot: liveSlot);
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
    public void AllSignalsOutOfDlc_ShowsCountAndPerSignalDiagnostic()
    {
        var vm = NewVm(out var svc);
        RaiseLoaded(svc, new Message(
            0x400, "Short", 1, "ECU",
            new List<Signal> { Sig("S", 16, 8) }, false, null));

        vm.SelectedMessage = vm.Messages.Single();

        vm.LayoutEmptyText.Should().Contain("1 个信号均无可绘制位");
        vm.VisibleSignals.Single().DiagnosticText.Should().Contain("全部超出 DLC");
    }

    [Fact]
    public void OverlappingCellTooltip_ListsTheLoserSignal()
    {
        var vm = NewVm(out var svc);
        RaiseLoaded(svc, new Message(
            0x500, "Overlap", 8, "ECU", new List<Signal>
            {
                Sig("CellVolt_2", 24, 16),
                Sig("BalanceFlags", 39, 8, ByteOrder.BigEndian),
            }, false, null));

        vm.SelectedMessage = vm.Messages.Single();

        var cell = vm.LayoutRows[4].Cells.Single(c => c.BitNumber == 0);
        cell.OwnerSignal.Should().Be("BalanceFlags");
        cell.Tooltip.Should().Contain("与 CellVolt_2 重叠");
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
    public void Search_Keeps_SelectedMux_When_Message_Still_Visible()
    {
        var vm = NewVm(out var svc);
        RaiseLoaded(svc, MuxMessage());
        vm.SelectedMessage = vm.Messages.Single();
        vm.SelectedMuxValue = 1;
        vm.LayoutSnapshot!.EffectiveMuxValue.Should().Be((ushort?)1, "precondition");

        vm.FilteredMessages.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
                vm.SelectedMessage = null;
        };

        vm.SearchText = "Mux";

        vm.SelectedMessage.Should().NotBeNull();
        vm.SelectedMuxValue.Should().Be((ushort?)1);
        vm.LayoutSnapshot!.EffectiveMuxValue.Should().Be((ushort?)1);
        vm.VisibleSignals.Select(s => s.Name).Should().BeEquivalentTo("Mode", "B");
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

    [Fact]
    public void New_Layout_Cells_Start_Without_Live_State()
    {
        var vm = NewVm(out var svc);
        RaiseLoaded(svc, SimpleMessage());
        vm.SelectedMessage = vm.Messages.Single();

        vm.LayoutRows.SelectMany(r => r.Cells).Should().OnlyContain(c => c.IsSet == null);
        vm.VisibleSignals.Should().OnlyContain(s => s.LiveValue == "—");
    }

    [Fact]
    public void Live_Payload_Lights_Cells_And_Decodes_Signal_Value()
    {
        var slot = new LiveFrameSlot();
        using var vm = NewVm(out var svc, slot);
        RaiseLoaded(svc, SimpleMessage());
        vm.SelectedMessage = vm.Messages.Single();
        vm.StopLiveTimerForTest();

        slot.Publish(0x100, new byte[] { 0x05 });
        vm.RefreshLiveLayoutForTest();

        var cells = vm.LayoutRows[0].Cells;
        cells.Single(c => c.BitNumber == 0).IsSet.Should().BeTrue();
        cells.Single(c => c.BitNumber == 2).IsSet.Should().BeTrue();
        cells.Single(c => c.BitNumber == 1).IsSet.Should().BeFalse();
        vm.VisibleSignals.Single().LiveValue.Should().Be("5");
    }

    [Fact]
    public void Live_Without_New_Frame_Keeps_Dashes()
    {
        var slot = new LiveFrameSlot();
        using var vm = NewVm(out var svc, slot);
        RaiseLoaded(svc, SimpleMessage());
        vm.SelectedMessage = vm.Messages.Single();
        vm.StopLiveTimerForTest();

        vm.RefreshLiveLayoutForTest();

        vm.VisibleSignals.Single().LiveValue.Should().Be("—");
        vm.LayoutRows[0].Cells.Should().OnlyContain(c => c.IsSet == null);
    }

    [Fact]
    public void Short_Frame_Leaves_Bytes_Beyond_Payload_Without_Live_State()
    {
        var slot = new LiveFrameSlot();
        using var vm = NewVm(out var svc, slot);
        var msg = new Message(0x100, "Two", 8, "ECU", new List<Signal>
        {
            Sig("Low", 0, 8),
            Sig("High", 8, 8),
        }, false, null);
        RaiseLoaded(svc, msg);
        vm.SelectedMessage = vm.Messages.Single();
        vm.StopLiveTimerForTest();

        slot.Publish(0x100, new byte[] { 0xFF });
        vm.RefreshLiveLayoutForTest();

        vm.VisibleSignals.Single(s => s.Name == "Low").LiveValue.Should().Be("255");
        vm.VisibleSignals.Single(s => s.Name == "High").LiveValue.Should()
            .Be("—", "载荷只有 1 字节，不能把缺失字节当 0 解码");
        vm.LayoutRows[1].Cells.Should().OnlyContain(c => c.IsSet == null);
    }

    [Fact]
    public void Mux_Mismatch_Shows_Dash_For_Muxed_Signals()
    {
        var slot = new LiveFrameSlot();
        using var vm = NewVm(out var svc, slot);
        RaiseLoaded(svc, MuxMessage());
        vm.SelectedMessage = vm.Messages.Single();
        vm.StopLiveTimerForTest();

        // 帧里 mux=1（Mode 低 4 位 = 1），mux=0 的 A 在本帧不生效
        slot.Publish(0x100, new byte[] { 0x01, 0x2A, 0x63 });
        vm.RefreshLiveLayoutForTest();

        vm.VisibleSignals.Single(s => s.Name == "Mode").LiveValue.Should().Be("1");
        vm.VisibleSignals.Single(s => s.Name == "A").LiveValue.Should().Be("—");
        vm.LayoutRows[1].Cells.Single(c => c.BitNumber == 1).IsSet.Should()
            .BeTrue("位图仍按载荷位点亮，与 mux 分支无关");
    }

    [Fact]
    public void Switching_Mux_Reapplies_Live_Values_For_New_Branch()
    {
        var slot = new LiveFrameSlot();
        using var vm = NewVm(out var svc, slot);
        RaiseLoaded(svc, MuxMessage());
        vm.SelectedMessage = vm.Messages.Single();
        vm.StopLiveTimerForTest();
        slot.Publish(0x100, new byte[] { 0x01, 0x2A, 0x63 });
        vm.RefreshLiveLayoutForTest();

        vm.SelectedMuxValue = 1;
        vm.StopLiveTimerForTest();

        vm.VisibleSignals.Select(s => s.Name).Should().BeEquivalentTo("Mode", "B");
        vm.VisibleSignals.Single(s => s.Name == "B").LiveValue.Should()
            .Be("99", "切换 mux 后必须立即按新分支重解，不能等下一次 tick");
    }

    [Fact]
    public void Fd_Sixty_Four_Byte_Payload_Lights_Last_Byte()
    {
        var slot = new LiveFrameSlot();
        using var vm = NewVm(out var svc, slot);
        var msg = new Message(0x100, "Fd", 64, "ECU", new List<Signal>
        {
            Sig("Top", 504, 8),
        }, false, null);
        RaiseLoaded(svc, msg);
        vm.SelectedMessage = vm.Messages.Single();
        vm.StopLiveTimerForTest();

        var payload = new byte[64];
        payload[63] = 0x80;
        slot.Publish(0x100, payload);
        vm.RefreshLiveLayoutForTest();

        vm.LayoutRows.Should().HaveCount(64);
        vm.LayoutRows[63].Cells.Single(c => c.BitNumber == 7).IsSet.Should().BeTrue();
        vm.VisibleSignals.Single().LiveValue.Should().Be("128");
    }

    [Fact]
    public void Deselecting_Message_Clears_Live_Target()
    {
        var slot = new LiveFrameSlot();
        using var vm = NewVm(out var svc, slot);
        RaiseLoaded(svc, SimpleMessage());
        vm.SelectedMessage = vm.Messages.Single();
        vm.StopLiveTimerForTest();
        slot.Publish(0x100, new byte[] { 0xFF });

        vm.SelectedMessage = null;

        slot.TryRead(0x100, out _).Should().BeFalse("取消选中后后台不再为该消息保留载荷");
    }

    [Fact]
    public void Dbc_Reload_Clears_Live_Target()
    {
        var slot = new LiveFrameSlot();
        using var vm = NewVm(out var svc, slot);
        RaiseLoaded(svc, SimpleMessage());
        vm.SelectedMessage = vm.Messages.Single();
        vm.StopLiveTimerForTest();
        slot.Publish(0x100, new byte[] { 0xFF });

        RaiseLoaded(svc, SimpleMessage());

        vm.SelectedMessage.Should().BeNull();
        slot.TryRead(0x100, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Timer_Tick_Applies_Live_Values_Without_Manual_Refresh()
    {
        var slot = new LiveFrameSlot();
        using var vm = NewVm(out var svc, slot);
        RaiseLoaded(svc, SimpleMessage());
        vm.SelectedMessage = vm.Messages.Single();

        slot.Publish(0x100, new byte[] { 0x05 });

        // 不调用 RefreshLiveLayoutForTest：这里钉的是 33ms Timer 投递机制本身。
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (vm.VisibleSignals.Single().LiveValue == "—" && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        vm.VisibleSignals.Single().LiveValue.Should()
            .Be("5", "33ms tick 必须自行把单槽载荷投递到 UI");
    }

    [Fact]
    public void Payload_Bytes_Beyond_Declared_Dlc_Do_Not_Produce_Values()
    {
        var slot = new LiveFrameSlot();
        using var vm = NewVm(out var svc, slot);
        var msg = new Message(0x100, "Short", 2, "ECU", new List<Signal>
        {
            Sig("Outside", 40, 8),
        }, false, null);
        RaiseLoaded(svc, msg);
        vm.SelectedMessage = vm.Messages.Single();
        vm.StopLiveTimerForTest();

        slot.Publish(0x100, new byte[] { 0, 0, 0, 0, 0, 0x2A, 0, 0 });
        vm.RefreshLiveLayoutForTest();

        vm.VisibleSignals.Single().LiveValue.Should()
            .Be("—", "信号在声明 DLC 之外，不能因为总线帧更长就显示数值");
    }

    [Fact]
    public async Task Hiding_The_View_Stops_Refreshes_And_Keeps_Last_Value()
    {
        var slot = new LiveFrameSlot();
        using var vm = NewVm(out var svc, slot);
        RaiseLoaded(svc, SimpleMessage());
        vm.SelectedMessage = vm.Messages.Single();
        slot.Publish(0x100, new byte[] { 0x05 });
        vm.RefreshLiveLayoutForTest();
        vm.VisibleSignals.Single().LiveValue.Should().Be("5");

        vm.SetLiveVisible(false);
        vm.IsLiveTicking.Should().BeFalse("切走标签页后定时器必须停止");
        slot.Publish(0x100, new byte[] { 0x7F });
        await Task.Delay(200);   // 6 个 33ms 周期，足够暴露“没停表”

        vm.VisibleSignals.Single().LiveValue.Should()
            .Be("5", "切走标签页后必须停表，并保留最后显示（不能闪空）");
    }

    [Fact]
    public void Showing_The_View_Resumes_Live_Refreshes()
    {
        var slot = new LiveFrameSlot();
        using var vm = NewVm(out var svc, slot);
        RaiseLoaded(svc, SimpleMessage());
        vm.SelectedMessage = vm.Messages.Single();
        vm.SetLiveVisible(false);

        slot.Publish(0x100, new byte[] { 0x05 });
        vm.SetLiveVisible(true);

        vm.IsLiveTicking.Should().BeTrue("切回标签页后必须重启定时器");
        vm.VisibleSignals.Single().LiveValue.Should()
            .Be("5", "切回标签页必须立即按最新帧恢复，而不是等下一次 tick");
    }

    [Fact]
    public void Showing_Without_Selection_Is_NoOp()
    {
        var slot = new LiveFrameSlot();
        using var vm = NewVm(out var svc, slot);

        vm.SetLiveVisible(false);
        vm.SetLiveVisible(true);

        vm.LayoutSnapshot.Should().BeNull();
        vm.IsLiveTicking.Should().BeFalse("没有选中消息时不能启动空转定时器");
    }

    [Fact]
    public void Selecting_Starts_And_Deselecting_Stops_The_Live_Timer()
    {
        var slot = new LiveFrameSlot();
        using var vm = NewVm(out var svc, slot);
        RaiseLoaded(svc, SimpleMessage());

        vm.SelectedMessage = vm.Messages.Single();
        vm.IsLiveTicking.Should().BeTrue("选中消息后必须启动 33ms 刷新");

        vm.SelectedMessage = null;
        vm.IsLiveTicking.Should().BeFalse("取消选中后必须停表");
    }

    [Fact]
    public void Reload_While_Hidden_Drops_The_Live_Payload()
    {
        var slot = new LiveFrameSlot();
        using var vm = NewVm(out var svc, slot);
        RaiseLoaded(svc, SimpleMessage());
        vm.SelectedMessage = vm.Messages.Single();
        vm.SetLiveVisible(false);
        slot.Publish(0x100, new byte[] { 0xFF });

        RaiseLoaded(svc, SimpleMessage());   // 隐藏状态下重载

        slot.TryRead(0x100, out _).Should()
            .BeFalse("重载后旧文档的帧不能留给新文档同 ID 消息");
    }
}
