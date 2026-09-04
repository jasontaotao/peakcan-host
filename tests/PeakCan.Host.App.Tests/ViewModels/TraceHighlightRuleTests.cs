using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// 2026-08-31 P3：多规则彩色高亮（spec §5.6 / §10.4）。规则求值（先匹配先赢 /
/// 全空=匹配全部 / 非法行跳过 / 禁用行跳过）；规则变更全量重算；新帧入列即带色。
/// </summary>
public class TraceHighlightRuleTests
{
    private static CanFrame Frame(uint id = 0x100, byte dlc = 1)
    {
        return new CanFrame(
            new CanId(id, FrameFormat.Standard),
            new byte[dlc], FrameFlags.None, ChannelId.None,
            Timestamp.FromMicroseconds(1_000_000UL));
    }

    /// <summary>构造一条仅用于谓词求值的 TraceEntry（无需入 VM）。</summary>
    private static TraceEntry Entry(uint id, byte[]? data = null)
        => new()
        {
            Timestamp = new Timestamp(0),
            Channel = ChannelId.None,
            Id = new CanId(id, FrameFormat.Standard),
            Dlc = (byte)(data?.Length ?? 0),
            DataHex = "",
            Data = data ?? Array.Empty<byte>(),
            IsError = false,
            IsFd = false,
            IsRtr = false,
        };

    // —— 新帧入列即带色（AppendBatchCore 调 EvaluateHighlight）——

    [Fact]
    public void New_Frame_Gets_Highlight_On_Append()
    {
        var vm = new TraceViewModel();
        var rule = new HighlightRuleRowViewModel { IdListText = "0x100", ColorIndex = 1 };
        vm.HighlightRules.Add(rule);

        vm.AppendBatchCore(new[] { Frame(0x100), Frame(0x200) });

        vm.Entries[0].HighlightColorIndex.Should().Be(1);
        vm.Entries[1].HighlightColorIndex.Should().Be(-1);
    }

    // —— 先匹配先赢 ——

    [Fact]
    public void First_Matching_Rule_Wins()
    {
        var vm = new TraceViewModel();
        vm.HighlightRules.Add(new HighlightRuleRowViewModel { IdListText = "0x100", ColorIndex = 1 });
        vm.HighlightRules.Add(new HighlightRuleRowViewModel { IdListText = "0x100", ColorIndex = 2 });

        vm.EvaluateHighlight(Entry(0x100)).Should().Be(1);
    }

    // —— 全空 = 匹配全部（兜底规则）——

    [Fact]
    public void MatchAll_Rule_Matches_Everything()
    {
        var vm = new TraceViewModel();
        vm.HighlightRules.Add(new HighlightRuleRowViewModel { ColorIndex = 3 });

        vm.EvaluateHighlight(Entry(0x100)).Should().Be(3);
        vm.EvaluateHighlight(Entry(0x200)).Should().Be(3);
    }

    // —— 非法行跳过 ——

    [Fact]
    public void Invalid_Rule_Is_Skipped()
    {
        var vm = new TraceViewModel();
        vm.HighlightRules.Add(new HighlightRuleRowViewModel { IdListText = "nothex", ColorIndex = 1 });
        vm.HighlightRules.Add(new HighlightRuleRowViewModel { IdListText = "0x100", ColorIndex = 2 });

        vm.EvaluateHighlight(Entry(0x100)).Should().Be(2);
    }

    // —— 禁用行跳过 ——

    [Fact]
    public void Disabled_Rule_Is_Skipped()
    {
        var vm = new TraceViewModel();
        vm.HighlightRules.Add(new HighlightRuleRowViewModel { IdListText = "0x100", ColorIndex = 1, Enabled = false });
        vm.HighlightRules.Add(new HighlightRuleRowViewModel { IdListText = "0x100", ColorIndex = 2 });

        vm.EvaluateHighlight(Entry(0x100)).Should().Be(2);
    }

    // —— 无命中 → -1 ——

    [Fact]
    public void No_Match_Returns_MinusOne()
    {
        var vm = new TraceViewModel();
        vm.HighlightRules.Add(new HighlightRuleRowViewModel { IdListText = "0x100", ColorIndex = 1 });

        vm.EvaluateHighlight(Entry(0x200)).Should().Be(-1);
    }

    // —— 规则变更全量重算 ——

    [Fact]
    public void Rule_Change_Recomputes_All_Existing_Entries()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { Frame(0x100), Frame(0x200) });

        // 初始无规则 → 全 -1。
        vm.Entries.All(e => e.HighlightColorIndex == -1).Should().BeTrue();

        // 添加规则 → 全量重算，命中行着色。
        vm.HighlightRules.Add(new HighlightRuleRowViewModel { IdListText = "0x100", ColorIndex = 1 });
        vm.RecomputeAllHighlights();

        vm.Entries[0].HighlightColorIndex.Should().Be(1);
        vm.Entries[1].HighlightColorIndex.Should().Be(-1);
    }

    // —— 摘要文本 ——

    [Fact]
    public void Summary_Text_Counts_Enabled_Rules()
    {
        var vm = new TraceViewModel();
        vm.HighlightRules.Add(new HighlightRuleRowViewModel { Enabled = true });
        vm.HighlightRules.Add(new HighlightRuleRowViewModel { Enabled = false });
        vm.RecomputeAllHighlights();

        vm.HighlightSummaryText.Should().Be("1 条规则生效");
    }
}
