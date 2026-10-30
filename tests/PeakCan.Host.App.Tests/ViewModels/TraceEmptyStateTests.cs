using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// P0 空态判据（Trace）：两种"空"必须能区分，并且在状态变化时发出通知——
/// XAML 的 DataTrigger 只认属性通知，判据算对了但不通知，面板就永远不出现。
/// </summary>
public class TraceEmptyStateTests
{
    private static CanFrame Frame(uint id)
        => new(new CanId(id, FrameFormat.Standard),
               Array.Empty<byte>(),
               FrameFlags.None,
               ChannelId.None,
               Timestamp.FromMicroseconds(1_000_000UL));

    [Fact]
    public void IsTraceEmpty_Tracks_First_Batch_And_Clear()
    {
        var vm = new TraceViewModel();
        vm.IsTraceEmpty.Should().BeTrue("首帧之前表格是空的");
        vm.IsTraceFilteredEmpty.Should().BeFalse("没有帧时不该报「过滤掉了」");

        vm.AppendBatchCore(new[] { Frame(0x100) });

        vm.IsTraceEmpty.Should().BeFalse();
        vm.IsTraceFilteredEmpty.Should().BeFalse();

        vm.ClearCommand.Execute(null);

        vm.IsTraceEmpty.Should().BeTrue("清空帧后回到「还没有收到帧」");
        vm.IsTraceFilteredEmpty.Should().BeFalse();
    }

    [Fact]
    public void IsTraceFilteredEmpty_True_Only_When_Filter_Excludes_Everything()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x100), Frame(0x200) });

        // 白名单里放一个不存在的 ID → 一条都不放行。
        vm.IdListText = "0x999";

        vm.EntriesView.Count.Should().Be(0);
        vm.IsTraceEmpty.Should().BeFalse("帧确实收到过，不能说「还没有收到帧」");
        vm.IsTraceFilteredEmpty.Should().BeTrue();

        vm.ClearFiltersCommand.Execute(null);

        vm.IsTraceFilteredEmpty.Should().BeFalse("清除过滤后帧应该回来");
        vm.EntriesView.Count.Should().Be(2);
    }

    [Fact]
    public void Empty_State_Flags_Raise_PropertyChanged()
    {
        var vm = new TraceViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        vm.AppendBatchCore(new[] { Frame(0x100) });
        vm.IdListText = "0x999";

        changed.Should().Contain(nameof(TraceViewModel.IsTraceEmpty));
        changed.Should().Contain(nameof(TraceViewModel.IsTraceFilteredEmpty));
    }
}
