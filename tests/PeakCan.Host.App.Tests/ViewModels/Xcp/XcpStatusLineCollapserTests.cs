using System.Linq;
using FluentAssertions;
using PeakCan.Host.App.ViewModels.Xcp;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Xcp;

/// <summary>
/// 状态区折叠。真机样本（S32K148_EB_IarPrj_CANape.a2l，3000 个对象全地址 0x0）
/// 实测产生 3003 条 ValidationNote——逐条平铺会同时撑爆布局和渲染。
/// <para>
/// 折叠的铁律是<b>不许静默降级</b>：省略必须写在脸上（"另有 N 条已省略"），
/// 且单条分组要与改造前的输出逐字一致（否则正常 A2L 的状态区会凭空多出摘要行）。
/// </para>
/// </summary>
public sealed class XcpStatusLineCollapserTests
{
    private const string Warning = "Warning";

    [Fact]
    public void A_single_item_renders_exactly_as_before()
    {
        // 只有一条时不该冒出"× 1"这种摘要行——绝大多数 A2L 的每类都只有一条。
        var lines = XcpStatusLineCollapser.Collapse(new[]
        {
            ("ByteOrderConflict", Warning, "ByteOrderConflict: MOD_COMMON vs PROTOCOL_LAYER"),
        });

        lines.Should().Equal("[Warning] ByteOrderConflict: MOD_COMMON vs PROTOCOL_LAYER");
    }

    [Fact]
    public void Many_items_in_one_group_collapse_and_say_how_many_were_omitted()
    {
        var items = Enumerable.Range(0, 3000)
            .Select(i => ("MissingAddress", Warning, $"MissingAddress: object {i} has address 0x0"))
            .ToArray();

        var lines = XcpStatusLineCollapser.Collapse(items);

        lines.Should().HaveCount(1 + XcpStatusLineCollapser.MaxDetailsPerGroup + 1,
            "一条摘要 + 固定条数样例 + 一行省略说明");
        lines[0].Should().Contain("MissingAddress").And.Contain("× 3000");
        lines[^1].Should().Contain("2997",
            "省略条数必须写出来；只说'已折叠'等于让用户以为只有这几条");

        // 摘要与样例之外的行都被显式交代过，不是悄悄扔掉的。
        lines.Should().OnlyContain(l => l[0] == '[' || l.StartsWith("    "));
    }

    [Fact]
    public void A_group_within_the_detail_cap_has_no_omission_note()
    {
        var items = new[]
        {
            ("MissingAddress", Warning, "第一个对象"),
            ("MissingAddress", Warning, "第二个对象"),
        };

        var lines = XcpStatusLineCollapser.Collapse(items);

        lines.Should().Equal(
            "[Warning] MissingAddress × 2",
            "    第一个对象",
            "    第二个对象");
    }

    [Fact]
    public void A_small_actionable_group_is_listed_in_full()
    {
        // 「缺哪几条 REQUIRED_CMD」「哪些对象地址待回填」这类组条目不多，但每一条
        // 都是要人去干活的清单。截断后界面上没有取回路径（原始集合只有代码能读），
        // 所以 ≤ AlwaysShowAllUpTo 的组必须全列。
        var items = Enumerable.Range(0, XcpStatusLineCollapser.AlwaysShowAllUpTo - 7)
            .Select(i => ("REQUIRED_COMMAND_NOT_DECLARED", Warning, $"命令 {i}"))
            .ToArray();

        var lines = XcpStatusLineCollapser.Collapse(items);

        lines.Should().HaveCount(items.Length + 1, "全部明细 + 一行摘要");
        lines.Should().NotContain(l => l.Contains("已省略"));
    }

    [Fact]
    public void The_show_all_threshold_is_inclusive()
    {
        // 边界：<= 全列、> 才截断。写成 < 会让恰好 20 条的组凭空出现省略行。
        var atLimit = Enumerable.Range(0, XcpStatusLineCollapser.AlwaysShowAllUpTo)
            .Select(i => ("MISSING_FIELD", Warning, $"字段 {i}"))
            .ToArray();
        XcpStatusLineCollapser.Collapse(atLimit)
            .Should().NotContain(l => l.Contains("已省略"));

        var overLimit = Enumerable.Range(0, XcpStatusLineCollapser.AlwaysShowAllUpTo + 1)
            .Select(i => ("MISSING_FIELD", Warning, $"字段 {i}"))
            .ToArray();
        var lines = XcpStatusLineCollapser.Collapse(overLimit);
        lines.Should().Contain(l => l.Contains("已省略"));
        lines[^1].Should().Contain("18", "超出部分要算准");
    }

    [Fact]
    public void Hard_constraint_groups_are_never_truncated()
    {
        // COMMAND_DECLARED_NOT_MEASURED 是 Reject 级，每条指名一个不同的命令码——
        // 截掉就等于让人不知道要补哪几个命令。这类组条数天然有界，不必折叠。
        var items = Enumerable.Range(0, 30)
            .Select(i => ("COMMAND_DECLARED_NOT_MEASURED", "Reject", $"命令码 {i:X2} 未实测"))
            .ToArray();

        var lines = XcpStatusLineCollapser.Collapse(items);

        lines.Should().HaveCount(31, "30 条 + 一行摘要");
        lines.Should().NotContain(l => l.Contains("已省略"));
        lines[0].Should().StartWith("[Reject]");
    }

    [Fact]
    public void Groups_keep_the_order_they_first_appeared_in()
    {
        var items = new[]
        {
            ("MaxCtoPgmZero", Warning, "m1"),
            ("MissingAddress", Warning, "x1"),
            ("MaxCtoPgmZero", Warning, "m2"),
            ("MissingAddress", Warning, "x2"),
        };

        var lines = XcpStatusLineCollapser.Collapse(items);

        // 每组一行摘要 + 两条样例 = 6 行；组序按首次出现，不按字典序或条数。
        lines.Should().HaveCount(6);
        lines[0].Should().Be("[Warning] MaxCtoPgmZero × 2", "分组按首次出现排序，状态区顺序必须稳定");
        lines[3].Should().Be("[Warning] MissingAddress × 2");
    }

    [Fact]
    public void The_key_label_is_used_in_the_summary_header()
    {
        var items = new[]
        {
            ("MissingAddress", Warning, "a"),
            ("MissingAddress", Warning, "b"),
            ("MissingAddress", Warning, "c"),
            ("MissingAddress", Warning, "d"),
        };

        var lines = XcpStatusLineCollapser.Collapse(
            items, key => key == "MissingAddress" ? "地址缺失" : key);

        lines[0].Should().Be("[Warning] 地址缺失 × 4");
    }

    [Fact]
    public void An_empty_input_produces_no_lines()
    {
        XcpStatusLineCollapser.Collapse(System.Array.Empty<(string, string, string)>())
            .Should().BeEmpty("干净 A2L 不该凭空多出任何状态行");
    }
}
