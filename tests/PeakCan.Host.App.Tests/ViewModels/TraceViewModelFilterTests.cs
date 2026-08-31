using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// 2026-08-31 P1：视图层过滤核心行为（spec §10.3）。MTA 直驱
/// <see cref="TraceViewModel.AppendBatchCore"/>（同步核心，无 dispatcher 依赖）：
/// 非破坏性入列（过滤不丢帧）/ 视图可见计数 / Refresh 语义 / IsPaused 入口级 /
/// MaxRows trim 与校验 / 状态文本。
/// </summary>
public class TraceViewModelFilterTests
{
    private static CanFrame Frame(uint id = 0x123, ushort channel = 0x51, byte dlc = 1,
        bool error = false)
    {
        byte[] payload = dlc == 0 ? Array.Empty<byte>() : new byte[dlc];
        var flags = FrameFlags.None;
        if (error) flags |= FrameFlags.ErrFrame;
        return new CanFrame(
            new CanId(id, FrameFormat.Standard),
            payload, flags,
            new ChannelId(channel),
            Timestamp.FromMicroseconds(1_000_000UL));
    }

    // —— 非破坏性：过滤不丢帧 ——

    [Fact]
    public void AppendBatchCore_All_Frames_Enter_Entries_Even_When_Filtered()
    {
        // 入口过滤 → 视图层过滤：即使 ID 过滤隐藏了某些帧，Entries 仍全量入列。
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x100), Frame(0x200), Frame(0x300) });
        vm.Entries.Should().HaveCount(3);
        vm.TotalFrameCount.Should().Be(3);

        // 设 ID allow-list 过滤 → 视图只显示匹配，但 Entries 未丢帧。
        vm.IdListText = "0x100";
        vm.EntriesView.Count.Should().Be(1);
        vm.Entries.Should().HaveCount(3);

        // 改条件找回被隐藏帧（非破坏性钉）。
        vm.IdListText = "0x200";
        vm.EntriesView.Count.Should().Be(1);
        vm.Entries.Should().HaveCount(3);

        // 清空过滤 → 全部可见。
        vm.ClearFiltersCommand.Execute(null);
        vm.EntriesView.Count.Should().Be(3);
    }

    [Fact]
    public void EntriesView_Filters_By_IdAllowList()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x100), Frame(0x200) });
        vm.IdListText = "0x100";
        vm.EntriesView.Count.Should().Be(1);
    }

    [Fact]
    public void EntriesView_Filters_By_Channel()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[]
        {
            Frame(0x100, channel: 0x51),
            Frame(0x200, channel: 0x52),
        });
        vm.ChannelFilter = new ChannelId(0x51);
        vm.EntriesView.Count.Should().Be(1);
        vm.EntriesView.GetItemAt(0).Should().Be(vm.Entries[0]);
    }

    // —— 视图可见计数 / 状态文本 ——

    [Fact]
    public void StatusText_Reflects_Visible_Total_And_Cap()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x100), Frame(0x200), Frame(0x300) });
        vm.StatusText.Should().Be("显示 3 / 共 3（上限 5000）｜总收 3");

        vm.IdListText = "0x100";
        vm.StatusText.Should().Be("显示 1 / 共 3（上限 5000）｜总收 3");
    }

    // —— IsPaused 入口级（仍计数，不入列）——

    [Fact]
    public void IsPaused_Still_Counts_But_Does_Not_Append()
    {
        var vm = new TraceViewModel { IsPaused = true };
        vm.AppendBatchCore(new[] { Frame(0x100), Frame(0x200) });
        vm.TotalFrameCount.Should().Be(2);
        vm.Entries.Should().BeEmpty();
        vm.EntriesView.Count.Should().Be(0);
    }

    // —— MaxRows trim ——

    [Fact]
    public void MaxRows_Trims_Oldest_On_Next_Batch()
    {
        var vm = new TraceViewModel { MaxRows = 2 };
        vm.AppendBatchCore(new[] { Frame(0x100), Frame(0x200), Frame(0x300) });
        // 调低到 2 → 下一批次截断，保留最新 2 条。
        vm.Entries.Should().HaveCount(2);
        vm.Entries[0].Id.Raw.Should().Be(0x200);
        vm.Entries[1].Id.Raw.Should().Be(0x300);
    }

    // —— 过滤字段非法 → 沿用上一有效 spec ——

    [Fact]
    public void Invalid_Filter_Field_Keeps_Previous_Valid_Spec()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x100), Frame(0x200) });

        vm.IdListText = "0x100";
        vm.EntriesView.Count.Should().Be(1);

        // 输入非法 → 报错并沿用上一有效 spec（不静默放宽成全显）。
        vm.PgnText = "zz";
        vm.FilterErrorText.Should().NotBeNullOrEmpty();
        vm.EntriesView.Count.Should().Be(1, "非法 PGN 应沿用上一有效 spec（ID=0x100）");

        // 修正非法字段 → 恢复。
        vm.PgnText = "";
        vm.FilterErrorText.Should().BeNull();
    }

    // —— Refresh / 过滤后行序 ——

    [Fact]
    public void ClearFilters_Resets_To_Show_All()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x100), Frame(0x200), Frame(0x300) });
        vm.IdListText = "0x100";
        vm.ExcludeMatch = true;
        vm.EntriesView.Count.Should().Be(2);

        vm.ClearFiltersCommand.Execute(null);
        vm.EntriesView.Count.Should().Be(3);
        vm.FilterErrorText.Should().BeNull();
    }

    // 2026-08-31 review HIGH: ClearFilters 之前只清文本字段+Exclude，漏掉并入 spec 的
    // ChannelFilter/ShowErrorsOnly——下次编辑任一过滤字段时旧值被重新 merge 进 spec
    // 静默复活。清除必须一并重置这两个并入字段。
    [Fact]
    public void ClearFilters_Also_Resets_Channel_And_ErrorsOnly()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x100, error: true), Frame(0x200, error: false) });
        vm.ChannelFilter = new ChannelId(0x51);
        vm.ShowErrorsOnly = true;
        vm.EntriesView.Count.Should().Be(1, "通道 0x51 + 仅错误 → 只显示错误帧");

        vm.ClearFiltersCommand.Execute(null);

        vm.ChannelFilter.Should().BeNull("清除过滤必须重置通道");
        vm.ShowErrorsOnly.Should().BeFalse("清除过滤必须重置仅错误帧开关");
        vm.EntriesView.Count.Should().Be(2, "重置后全显");
        // 复用点：清完后编辑任一文本字段 → spec 重建不复活已清除的并入条件
        // （ChannelFilter/ShowErrorsOnly 已是 null/false，merge 后仍全显）。
        vm.IdListText = "0x100";
        vm.EntriesView.Count.Should().Be(1, "仅按新 ID 条件收窄，通道/错误旧值不复活");
    }

    // —— MaxRows 校验（spec §5.8）——

    [Fact]
    public void MaxRows_Valid_Input_Applies()
    {
        var vm = new TraceViewModel();
        vm.MaxRowsText = "100";
        vm.MaxRows.Should().Be(100);
        vm.MaxRowsErrorText.Should().BeNull();
        vm.MaxRowsText.Should().Be("100");
    }

    [Fact]
    public void MaxRows_Invalid_Input_Keeps_Old_Value_And_Reverts_Text()
    {
        var vm = new TraceViewModel();
        vm.MaxRowsText = "abc";
        vm.MaxRows.Should().Be(5_000, "非法输入应保持旧值");
        vm.MaxRowsErrorText.Should().NotBeNullOrEmpty();
        vm.MaxRowsText.Should().Be("5000", "文本应回退旧值");
    }

    [Fact]
    public void MaxRows_Out_Of_Range_Is_Rejected()
    {
        var vm = new TraceViewModel();
        vm.MaxRowsText = "10";     // < 100
        vm.MaxRows.Should().Be(5_000);
        vm.MaxRowsText = "99999";  // > 50000
        vm.MaxRows.Should().Be(5_000);
        vm.MaxRowsErrorText.Should().NotBeNullOrEmpty();
    }
}
