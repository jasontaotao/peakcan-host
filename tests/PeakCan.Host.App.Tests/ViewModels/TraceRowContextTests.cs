using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// Trace 页行右键菜单的 VM 命令（P0-2）：只看 / 排除 / 高亮当前 ID。
/// 参照 <see cref="TraceHighlightRuleTests"/> 的 MTA 直驱模式——不依赖
/// WPF Application，<see cref="TraceViewModel.AppendBatchCore"/> 直接喂帧。
/// 断言覆盖 EntriesView 的最终过滤结果，而不只是属性写入。
/// </summary>
public class TraceRowContextTests
{
    private static readonly uint[] OnlySecondErrorFrame = { 0x200u };
    private static readonly uint[] RemainingFramesAfterExclude = { 0x456u, 0x789u };
    private static readonly uint[] OnlyLastFrame = { 0x789u };

    private static CanFrame Frame(uint id = 0x100, bool isError = false)
    {
        return new CanFrame(
            new CanId(id, FrameFormat.Standard),
            Array.Empty<byte>(),
            isError ? FrameFlags.ErrFrame : FrameFlags.None,
            ChannelId.None,
            Timestamp.FromMicroseconds(1_000_000UL));
    }

    [Fact]
    public void ShowOnlyThisId_Filters_View_To_That_Id()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x123), Frame(0x456) });

        vm.ShowOnlyThisIdCommand.Execute(vm.Entries[0]);

        vm.IdListText.Should().Be("0x123");
        vm.ExcludeMatch.Should().BeFalse("“只看”语义必须清掉黑名单开关");
        vm.EntriesView.Count.Should().Be(1);
    }

    [Fact]
    public void ShowOnlyThisId_Clears_DbcMessageName_So_Union_Does_Not_Leak()
    {
        var vm = new TraceViewModel();
        // 无 DBC 时 parser 会报错，但字段确实被写入——用于验证命令会清空它。
        vm.DbcMessageName = "EEC1";
        vm.AppendBatchCore(new[] { Frame(0x123), Frame(0x456) });

        vm.ShowOnlyThisIdCommand.Execute(vm.Entries[0]);

        vm.DbcMessageName.Should().BeEmpty("DBC 消息名会并集扩进 allow-list，与“只看”矛盾");
        vm.EntriesView.Count.Should().Be(1);
    }

    [Fact]
    public void ExcludeThisId_Preserves_Other_Filters_Instead_Of_Inverting_Them()
    {
        // H1 回归：旧实现把 ExcludeMatch 置 true，会连同“仅错误帧”一起反转，
        // 导致普通帧涌入。新实现走独立 ExcludedIds 维度。
        var vm = new TraceViewModel { ShowErrorsOnly = true };
        vm.AppendBatchCore(new[]
        {
            Frame(0x100, isError: true),
            Frame(0x200, isError: true),
            Frame(0x300), // 普通帧：绝不能在“仅错误帧”下出现
        });

        vm.ExcludeThisIdCommand.Execute(vm.Entries.Single(e => e.Id.Raw == 0x100));

        vm.ExcludeMatch.Should().BeFalse("行右键排除不应改动整体取反开关");
        vm.IdListText.Should().BeEmpty("排除不应覆盖 ID allow-list");
        vm.EntriesView.Cast<TraceEntry>().Select(e => e.Id.Raw)
            .Should().BeEquivalentTo(OnlySecondErrorFrame);
    }

    [Fact]
    public void ExcludeThisId_Is_Idempotent()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x123), Frame(0x456), Frame(0x789) });

        vm.ExcludeThisIdCommand.Execute(vm.Entries[0]);
        vm.ExcludeThisIdCommand.Execute(vm.Entries[0]); // 重复执行不报错、不改变结果

        vm.EntriesView.Cast<TraceEntry>().Select(e => e.Id.Raw)
            .Should().BeEquivalentTo(RemainingFramesAfterExclude);
    }

    [Fact]
    public void ExcludeThisId_Accumulates_Multiple_Ids()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x123), Frame(0x456), Frame(0x789) });

        vm.ExcludeThisIdCommand.Execute(vm.Entries[0]);
        vm.ExcludeThisIdCommand.Execute(vm.Entries[1]);

        vm.EntriesView.Cast<TraceEntry>().Select(e => e.Id.Raw)
            .Should().BeEquivalentTo(OnlyLastFrame);
        vm.StatusText.Should().Contain("已排除 2 个 ID", "排除集合必须在状态栏可见");
    }

    [Fact]
    public void ClearExcludedIds_Restores_Excluded_Rows()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x123), Frame(0x456) });
        vm.ExcludeThisIdCommand.Execute(vm.Entries[0]);
        vm.EntriesView.Count.Should().Be(1);

        vm.ClearExcludedIdsCommand.Execute(null);

        vm.EntriesView.Count.Should().Be(2);
        vm.StatusText.Should().NotContain("已排除");
    }

    [Fact]
    public void HighlightThisId_AddsRule_And_Recomputes_ExistingEntries()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x123), Frame(0x456) });

        vm.HighlightThisIdCommand.Execute(vm.Entries[0]);

        vm.HighlightRules.Should().HaveCount(1);
        vm.HighlightRules[0].IdListText.Should().Be("0x123");
        vm.Entries[0].HighlightColorIndex.Should().Be(0);
        vm.Entries[1].HighlightColorIndex.Should().Be(-1);
        vm.HighlightSummaryText.Should().Be("1 条规则生效");
    }

    [Fact]
    public void HighlightThisId_Inserts_Before_MatchAll_Rule_So_It_Takes_Effect()
    {
        var vm = new TraceViewModel();
        vm.HighlightRules.Add(new HighlightRuleRowViewModel { ColorIndex = 3 }); // 匹配全部兜底
        vm.AppendBatchCore(new[] { Frame(0x123) });

        vm.HighlightThisIdCommand.Execute(vm.Entries[0]);

        vm.HighlightRules.Should().HaveCount(2);
        vm.HighlightRules[0].IdListText.Should().Be("0x123", "必须排在兜底规则之前");
        vm.Entries[0].HighlightColorIndex.Should().Be(1);
    }

    [Fact]
    public void HighlightThisId_Does_Not_Duplicate_Same_Id()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x123) });

        vm.HighlightThisIdCommand.Execute(vm.Entries[0]);
        vm.HighlightThisIdCommand.Execute(vm.Entries[0]);

        vm.HighlightRules.Should().HaveCount(1);
    }

    [Fact]
    public void Exclude_And_Clear_Work_Even_When_Filter_Text_Is_Invalid()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x123), Frame(0x456) });
        vm.IdListText = "nothex"; // 非法文本 → TryRebuildSpec 早退
        vm.FilterErrorText.Should().NotBeNull();

        vm.ExcludeThisIdCommand.Execute(vm.Entries[0]);
        vm.EntriesView.Count.Should().Be(1, "非法文本态下排除也必须生效");

        vm.ClearExcludedIdsCommand.Execute(null);
        vm.EntriesView.Count.Should().Be(2, "清除排除不能被非法文本态卡住");
        vm.StatusText.Should().NotContain("已排除");
    }

    [Fact]
    public void HighlightThisId_Adds_New_Rule_When_Existing_One_Is_Disabled()
    {
        var vm = new TraceViewModel();
        vm.HighlightRules.Add(new HighlightRuleRowViewModel
        {
            IdListText = "0x123",
            Enabled = false,
            ColorIndex = 2,
        });
        vm.AppendBatchCore(new[] { Frame(0x123) });

        vm.HighlightThisIdCommand.Execute(vm.Entries[0]);

        vm.HighlightRules.Should().HaveCount(2, "禁用的同名规则不应阻止重新高亮");
        vm.Entries[0].HighlightColorIndex.Should().NotBe(-1);
    }

    [Fact]
    public void Null_Entry_Is_NoOp()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x123) });

        var act = () =>
        {
            vm.ShowOnlyThisIdCommand.Execute(null);
            vm.ExcludeThisIdCommand.Execute(null);
            vm.HighlightThisIdCommand.Execute(null);
        };

        act.Should().NotThrow();
        vm.IdListText.Should().BeEmpty();
        vm.ExcludeMatch.Should().BeFalse();
        vm.HighlightRules.Should().BeEmpty();
        vm.EntriesView.Count.Should().Be(1);
    }
}
