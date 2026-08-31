using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// 2026-08-31 P4：统计面板（spec §5.7 / §10.5）。收起不刷 / 展开即刷 / Top20 排序 /
/// DBC 名 / <see cref="TraceViewModel.SetFilterToIdCommand"/> 写 ID 字段（覆盖语义）。
/// </summary>
public class TraceStatsTests
{
    private static CanFrame Frame(uint id = 0x100, FrameFormat format = FrameFormat.Standard)
        => new(new CanId(id, format), new byte[1], FrameFlags.None,
            ChannelId.None, Timestamp.FromMicroseconds(1_000_000UL));

    private static DbcDocument Doc(params Message[] msgs) => new(
        "v1", Array.Empty<Node>(), msgs,
        msgs.ToDictionary(m => m.Id, m => m),
        new Dictionary<string, ValueTable>());

    private static Message ExtendedMsg(uint id, string name)
        => new(id | 0x8000_0000u, name, 8, "ECU", Array.Empty<Signal>(), false, null);

    // —— 收起不刷 ——

    [Fact]
    public void Stats_Not_Refreshed_When_Collapsed()
    {
        var vm = new TraceViewModel(); // StatsExpanded 默认 false（收起）
        vm.AppendBatchCore(new[] { Frame(0x100), Frame(0x200) });
        vm.StatsRows.Should().BeEmpty("收起时不刷统计");
    }

    // —— 展开即刷 ——

    [Fact]
    public void Stats_Refresh_On_Expand_And_On_Append_While_Expanded()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x100), Frame(0x200) });
        vm.StatsExpanded = true; // 展开瞬间刷一次
        vm.StatsRows.Should().HaveCount(2);

        // 展开状态下批次末刷新 → 新帧计入。
        vm.AppendBatchCore(new[] { Frame(0x100) });
        vm.StatsRows.Should().HaveCount(2);
        vm.StatsRows.Single(r => r.IdHex == "0x100").Count.Should().Be(2);
    }

    // —— Top20 排序（降序）——

    [Fact]
    public void Stats_Sorted_By_Count_Descending_Top20()
    {
        var vm = new TraceViewModel();
        var frames = new List<CanFrame>();
        for (int i = 0; i < 25; i++)
        {
            // ID 0x100 出现最多（25 次），其余各 1 次。
            frames.Add(Frame(0x100));
            if (i < 24) frames.Add(Frame((uint)(0x100 + i + 1)));
        }
        vm.AppendBatchCore(frames);
        vm.StatsExpanded = true;

        vm.StatsRows.Should().HaveCount(20, "Top-N 取 20");
        vm.StatsRows[0].IdHex.Should().Be("0x100", "计数最高者排第一");
        vm.StatsRows.Should().BeInDescendingOrder(r => r.Count);
    }

    // —— DBC 名解析 ——

    [Fact]
    public void Stats_Resolves_DbcName_From_Loaded_Dbc()
    {
        var vm = new TraceViewModel();
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        dbc.SetCurrentForTests(Doc(ExtendedMsg(0x18EAFF00, "EEC1")));
        vm.BindDbc(dbc);

        vm.AppendBatchCore(new[] { Frame(0x18EAFF00, FrameFormat.Extended) });
        vm.StatsExpanded = true;

        vm.StatsRows.Single(r => r.IdHex == "0x18EAFF00").DbcName.Should().Be("EEC1");
    }

    [Fact]
    public void Stats_DbcName_Empty_When_No_Dbc()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x100) });
        vm.StatsExpanded = true;
        vm.StatsRows.Single().DbcName.Should().BeNull();
    }

    // —— SetFilterToId 写 ID 字段（覆盖语义）——

    [Fact]
    public void SetFilterToId_Overwrites_IdListText_And_Filters()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x100), Frame(0x200) });
        vm.StatsExpanded = true;
        var row = vm.StatsRows.Single(r => r.IdHex == "0x100");

        vm.SetFilterToIdCommand.Execute(row);

        vm.IdListText.Should().Be("0x100");
        vm.EntriesView.Count.Should().Be(1, "统计行设过滤 → 只显示该 ID");
    }

    [Fact]
    public void SetFilterToId_Overrides_Previous_HandEntered_Ids()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x100), Frame(0x200), Frame(0x300) });
        vm.IdListText = "0x100,0x200";
        vm.EntriesView.Count.Should().Be(2);

        vm.StatsExpanded = true;
        var row = vm.StatsRows.Single(r => r.IdHex == "0x300");
        vm.SetFilterToIdCommand.Execute(row);

        vm.IdListText.Should().Be("0x300", "覆盖手填内容（刻意）");
        vm.EntriesView.Count.Should().Be(1);
    }
}
