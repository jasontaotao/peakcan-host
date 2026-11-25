using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace PeakCan.Host.App.Tests.Views;

/// <summary>
/// NM 面板与补全对话框的布局契约（结构断言）：
/// 右侧面板默认只有 360px、补全窗最小 900×560；内容再多也不能把“详情”列
/// 挤成 0 宽、把状态文案裁掉，或让主操作跟着编辑区滚出视野。
/// </summary>
public sealed class NmViewLayoutTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly string[] PinnedActions = { "试算当前窗口", "保存到项目", "取消" };

    private static XDocument Load(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
            dir = dir.Parent!;
        return XDocument.Load(Path.Combine(dir!.FullName, relativePath));
    }

    private static XDocument Panel() => Load("src/PeakCan.Host.App/Views/TraceViewerViewNmPanel.xaml");

    private static XDocument Wizard() => Load("src/PeakCan.Host.App/Views/NmInputWizardWindow.xaml");

    /// <summary>取某个 DataTrigger 的 Foreground Setter 色值；结构缺失时返回 null 让断言失败。</summary>
    private static string? ForegroundOf(IEnumerable<XElement> triggers, string value)
        => triggers.SingleOrDefault(t => (string?)t.Attribute("Value") == value)
            ?.Elements(P + "Setter")
            .SingleOrDefault(s => (string?)s.Attribute("Property") == "Foreground")
            ?.Attribute("Value")?.Value;

    [Fact]
    public void Panel_EventTable_LeavesWidthForDetailColumn()
    {
        var grid = Panel().Descendants(P + "DataGrid").Single();
        var columns = grid.Element(P + "DataGrid.Columns")!.Elements()
            .Where(e => e.Name == P + "DataGridTextColumn" || e.Name == P + "DataGridTemplateColumn")
            .ToList();

        // 360px 面板：固定列总和必须给 * 列留出可读宽度（≥ 80px）。
        var fixedWidth = columns
            .Select(c => (string?)c.Attribute("Width"))
            .Where(w => w is not null && w != "*")
            .Select(w => double.Parse(w!, CultureInfo.InvariantCulture))
            .Sum();

        (360 - fixedWidth).Should().BeGreaterThanOrEqualTo(80,
            "详情列是事件表的主要信息载体，不能被固定列挤成 0 宽");

        // 证据级别并入详情列：省掉 52px 固定列，详情列用模板列承载图标 + 文本。
        var detail = columns.Single(c => (string?)c.Attribute("Width") == "*");
        // 面板可拖到 MinWidth=280（TraceViewerView.xaml:226），360 只是默认值。
        // 固定列 234px，280px 下 * 列只剩约 46px——必须有 MinWidth 兜底。
        ((string?)detail.Attribute("MinWidth")).Should().NotBeNullOrEmpty(
            "面板可拖到 280px，详情列没有 MinWidth 时会被压成不可读宽度");
        detail.Name.Should().Be(P + "DataGridTemplateColumn",
            "详情列要在文本前放证据图标，必须是模板列");
        detail.Descendants(P + "TextBlock")
            .Should().Contain(t => (string?)t.Attribute("Text") == "{Binding EvidenceGlyph}");

        var detailText = detail.Descendants(P + "TextBlock")
            .Single(t => (string?)t.Attribute("Text") == "{Binding Detail}");
        ((string?)detailText.Attribute("TextTrimming")).Should().Be("CharacterEllipsis",
            "详情列是 * 列：长文本必须省略，不能撑出横向滚动");

        // DockPanel 默认 LastChildFill：详情必须是最后一个子元素，
        // 才能拿到剩余宽度让 TextTrimming 生效；图标必须停靠在左侧。
        var panel = detail.Descendants(P + "DockPanel").Single();
        var children = panel.Elements().ToList();
        children.Last().Should().Be(detailText);
        ((string?)children.First().Attribute("DockPanel.Dock")).Should().Be("Left");

        // 证据级别颜色语义：measured 绿 / inferred 橙；颜色对调或写错会静默失去区分度。
        var triggers = detail.Descendants(P + "DataTrigger")
            .Where(t => (string?)t.Attribute("Binding") == "{Binding EvidenceKind}")
            .ToList();
        ForegroundOf(triggers, "measured").Should().Be("#1A7F37");
        ForegroundOf(triggers, "inferred").Should().Be("#B26A00");
    }

    [Fact]
    public void Panel_EventTable_HasNoSeparateEvidenceColumn()
    {
        var grid = Panel().Descendants(P + "DataGrid").Single();
        var headers = grid.Descendants()
            .Select(e => (string?)e.Attribute("Header"))
            .Where(h => h is not null)
            .ToList();

        headers.Should().NotContain("证据",
            "证据列已并入详情列前的图标；再拆列会把详情挤窄");
    }

    [Fact]
    public void Panel_SeekStatus_IsOutsideCappedBand()
    {
        // 点击定位的唯一可见反馈不能待在 150px 封顶滚动区末尾：
        // 常见状态下（profile 摘要 + 提示行）它会被推到首屏之外，用户又觉得“点了没反应”。
        var seekStatus = Panel().Descendants(P + "TextBlock")
            .Single(t => ((string?)t.Attribute("Text"))?
                .Contains("NmSeekStatus", StringComparison.Ordinal) == true);

        seekStatus.Ancestors(P + "ScrollViewer").Should().BeEmpty(
            "定位反馈必须在固定区域常驻可见，不能依赖滚动才出现");
    }

    [Fact]
    public void Panel_Legend_IsCollapsedByDefault()
    {
        var legend = Panel().Descendants(P + "Expander")
            .Single(e => (string?)e.Attribute("Header") == "图例");

        ((string?)legend.Attribute("IsExpanded")).Should().Be("False",
            "图例常驻四行会吃掉 360px 面板约 80px 垂直空间；默认折叠");

        // 折叠内容不能被删掉，且颜色必须与轨道同源（x:Static 笔刷），不能写死色值。
        // 五行 = 时间推断阶段 / NMSts 实测阶段（spec §7.5.4）/ 事件 / 整网行 / 原因行。
        legend.Descendants(P + "TextBlock").Should().HaveCount(5);
        var runs = legend.Descendants(P + "Run").ToList();
        runs.Count(r => ((string?)r.Attribute("Foreground"))?
                .StartsWith("{x:Static conv:NmSegmentBrushConverter.", StringComparison.Ordinal) == true)
            .Should().BeGreaterThanOrEqualTo(10,
                "图例色块必须引用转换器笔刷，否则时间条改色后图例会失真");
    }

    [Fact]
    public void Panel_Legend_ListsSilencePhaseKinds()
    {
        var legend = Panel().Descendants(P + "Expander")
            .Single(e => (string?)e.Attribute("Header") == "图例");

        var runs = legend.Descendants(P + "Run").ToList();
        foreach (var label in new[] { "释放等待（推断）", "准备休眠（推断）", "总线睡眠（推断）", "无法判定（推断）" })
        {
            var run = runs.Single(r => ((string?)r.Attribute("Text"))?.Contains(label) == true);
            ((string?)run.Attribute("Foreground")).Should()
                .StartWith("{x:Static conv:NmSegmentBrushConverter.",
                    "图例色块必须引用转换器笔刷，否则时间条改色后图例会失真");
        }
    }

    [Fact]
    public void Panel_TimelineAndEventTable_ShareRemainingHeight()
    {
        var root = Panel().Descendants(P + "Grid").First();
        var rows = root.Element(P + "Grid.RowDefinitions")!.Elements(P + "RowDefinition").ToList();

        // 时间条与事件表都按比例分配；事件表不再固定 160px。
        // 行号不再写死：顶部信息带被收进封顶滚动区后行数会变，
        // 但「时间条 2* + 事件表 * 且各有 MinHeight」这条核心契约不变。
        var timelineRow = rows.Single(r => (string?)r.Attribute("Height") == "2*");
        timelineRow.Attribute("MinHeight").Should().NotBeNull(
            "矮窗口下时间条要保留最小可视高度");

        var tableRow = rows.Single(r => (string?)r.Attribute("Height") == "*");
        tableRow.Attribute("MinHeight").Should().NotBeNull(
            "矮窗口下事件表要保留最小可视行数");

        // 时间条必须排在事件表之前（自上而下的阅读顺序）。
        rows.IndexOf(timelineRow).Should().BeLessThan(rows.IndexOf(tableRow));
    }

    /// <summary>
    /// <b>顶部信息带必须封顶</b>：选择器 / profile 摘要 / 冲突 / 语义横幅 / partial /
    /// 能力 chip 六条 <c>Auto</c> 横带无上限堆叠，全可见时能把时间条与事件表压到
    /// 各自的 <c>MinHeight</c>——用户要看的证据区只剩底部一小条。
    /// <para>
    /// 只加 <c>ScrollViewer</c> 不设 <c>MaxHeight</c> 无效：<c>Auto</c> 行会被内容撑到
    /// 自然高度，滚动条永不出现。这与标定面板自身的约束是同一个道理。
    /// </para>
    /// </summary>
    [Fact]
    public void Panel_TopInfoBands_AreHeightCapped()
    {
        var root = Panel().Descendants(P + "Grid").First();

        // 顶部滚动区：直接挂在根 Grid 上（带 Grid.Row），且设了 MaxHeight。
        var capped = root.Elements(P + "ScrollViewer")
            .Where(s => s.Attribute("Grid.Row") is not null)
            .Where(s => (string?)s.Attribute("MaxHeight") is not null)
            .ToList();

        capped.Should().NotBeEmpty(
            "顶部六条 Auto 横带没有聚合上限时会把时间条与事件表压到 MinHeight");

        var top = capped[0];
        ((string?)top.Attribute("VerticalScrollBarVisibility")).Should().Be("Auto",
            "内容不溢出时不显示滚动条，否则常驻滚动条暗示下面还有东西");

        // 封顶区里必须装着选择器（行 0 的 ComboBox）——它是顶部最占位的一块。
        top.Descendants(P + "ComboBox")
            .Should().Contain(c => (string?)c.Attribute("ItemsSource") == "{Binding NmProfileChoices}",
                "企标选择器必须在封顶区内，否则它仍能无限撑高");

        // 时间条与事件表不能被卷进这个滚动区：它们要占满剩余空间，不能跟着滚。
        top.Descendants(P + "DataGrid").Should().BeEmpty(
            "事件表不能进顶部滚动区——它要吃掉剩余高度");
    }

    [Fact]
    public void Panel_FiltersAndActions_WrapInsteadOfClipping()
    {
        var root = Panel().Descendants(P + "Grid").First();
        // 按内容定位而非行号：顶部信息带收进封顶滚动区后行索引会变，
        // 但「筛选用 WrapPanel、动作用 StackPanel」这条契约不变。
        var filters = root.Elements()
            .Single(e => e.Descendants(P + "ComboBox")
                .Any(c => (string?)c.Attribute("ItemsSource") == "{Binding NmEventTypeFilters}"));
        filters.Name.Should().Be(P + "WrapPanel", "360px 下横向 StackPanel 会把计数裁掉");

        var actions = root.Elements()
            .Single(e => e.Descendants(P + "Button")
                .Any(b => (string?)b.Attribute("Command") == "{Binding AnalyzeNmFullCommand}"));
        actions.Name.Should().Be(P + "StackPanel");

        // 状态文案常驻在动作区下方，不跟随按钮换行被挤出面板。
        actions.Elements(P + "TextBlock")
            .Select(t => (string?)t.Attribute("Text"))
            .Should().Contain("{Binding NmStatus}");
    }

    /// <summary>
    /// <b>主动作与后续动作分行</b>：7 个按钮塞进一个 <c>WrapPanel</c> 时，
    /// 280px 下「分析全 trace / 分析当前窗口」会和「导出 / AI 总结 / 解读」
    /// 混在一起换行，用户分不清哪两个是主操作。
    /// <para>
    /// 第一行只放分析类（含取消），第二行放导出与 AI。
    /// </para>
    /// </summary>
    [Fact]
    public void Panel_ActionButtons_SplitIntoAnalysisAndFollowUpRows()
    {
        var root = Panel().Descendants(P + "Grid").First();
        var actions = root.Elements(P + "StackPanel")
            .Single(e => e.Descendants(P + "Button")
                .Any(b => (string?)b.Attribute("Command") == "{Binding AnalyzeNmFullCommand}"));

        var wraps = actions.Elements(P + "WrapPanel").ToList();
        wraps.Should().HaveCount(2, "分析类与导出/AI 类要分行，不能混在同一个换行流里");

        string[] CommandsOf(XElement wrap) => wrap.Elements(P + "Button")
            .Select(b => (string?)b.Attribute("Command") ?? "").ToArray();

        CommandsOf(wraps[0]).Should()
            .Contain("{Binding AnalyzeNmFullCommand}")
            .And.Contain("{Binding AnalyzeNmWindowCommand}")
            .And.Contain("{Binding CancelNmAnalysisCommand}");
        CommandsOf(wraps[0]).Should().NotContain("{Binding ExportNmReportCommand}",
            "导出不是分析动作，不该混进第一行");

        CommandsOf(wraps[1]).Should()
            .Contain("{Binding ExportNmReportCommand}")
            .And.Contain("{Binding SummarizeNmInAiCommand}")
            .And.Contain("{Binding InterpretNmEventInAiCommand}");

        // 反向也要守住：分析按钮不能同时出现在第二行。
        // 只查「第一行不含导出」时，把分析按钮复制一份到第二行测试照样通过——
        // 那样第一行会空掉、用户反而更找不到主操作。
        CommandsOf(wraps[1]).Should()
            .NotContain("{Binding AnalyzeNmFullCommand}")
            .And.NotContain("{Binding AnalyzeNmWindowCommand}");

        // 同理：AI 按钮也不能出现在第一行，否则「第一行只放分析类」被破坏
        // 而上面的断言全都还能过。
        CommandsOf(wraps[0]).Should()
            .NotContain("{Binding SummarizeNmInAiCommand}")
            .And.NotContain("{Binding InterpretNmEventInAiCommand}");
    }

    /// <summary>
    /// <b>AI 分工说明不能常驻</b>：那句话解释的是「AI 按钮是干嘛的」，
    /// 属于操作区里最贵的纵向空间上的固定噪音——用户看第二次就是浪费。
    /// 但分工本身仍要能查到，所以挪进 AI 按钮的 ToolTip。
    /// </summary>
    [Fact]
    public void Panel_AiRoleExplanation_LivesInTooltipNotAlwaysOn()
    {
        var root = Panel().Descendants(P + "Grid").First();

        // 不再有常驻的说明 TextBlock。
        root.Descendants(P + "TextBlock")
            .Select(t => (string?)t.Attribute("Text") ?? "")
            .Should().NotContain(t => t.Contains("AI 负责解释"),
                "常驻分工说明是固定噪音，应改为按钮 ToolTip");

        // 但 AI 按钮的 ToolTip 里必须保留这句话——删掉等于用户再也找不到分工说明。
        root.Descendants(P + "Button")
            .Where(b => ((string?)b.Attribute("Command") ?? "")
                .Contains("SummarizeNmInAi", StringComparison.Ordinal))
            .SelectMany(b => b.Descendants(P + "TextBlock")
                .Select(t => (string?)t.Attribute("Text") ?? "")
                .Concat(new[] { (string?)b.Attribute("ToolTip") ?? "" }))
            .Should().Contain(t => t.Contains("AI 负责解释"),
                "分工说明要挪进 ToolTip，不能直接删除");
    }

    [Fact]
    public void Panel_ProfileBarAndEmptyHint_ReactToReportState()
    {
        var root = Panel().Descendants(P + "Grid").First();

        // 按内容定位（补全按钮）而非行号；Profile 条现在嵌在顶部封顶滚动区内。
        var profileBar = root.Descendants(P + "Border")
            .Single(b => b.Descendants(P + "Button")
                .Any(x => (string?)x.Attribute("Command") == "{Binding OpenNmWizardCommand}"));
        ((string?)profileBar.Attribute("Visibility")).Should()
            .Contain("NmHasReport", "分析前不能渲染空的 Profile 条/补全按钮");

        var hint = root.Elements(P + "TextBlock")
            .Single(t => ((string?)t.Attribute("Text") ?? "").Contains("尚未分析"));
        hint.Descendants(P + "DataTrigger").Should()
            .Contain(t => (string?)t.Attribute("Binding") == "{Binding NmHasReport}");
    }

    // —— 企标选择器（spec §10.1）——

    [Fact]
    public void Panel_HasProfileSelector_BoundToChoices()
    {
        var combo = Panel().Descendants(P + "ComboBox")
            .Single(c => (string?)c.Attribute("ItemsSource") == "{Binding NmProfileChoices}");

        ((string?)combo.Attribute("SelectedItem")).Should().Be("{Binding NmSelectedProfile}");
        ((string?)combo.Attribute("DisplayMemberPath")).Should().Be("Display",
            "候选项是 NmProfileChoiceVm 记录，必须指定显示字段");
    }

    [Fact]
    public void Panel_ProfileSelector_IsAlwaysVisible()
    {
        // 与 profile 摘要条不同：用户可以先选企标再跑分析，
        // 所以选择器不能绑 NmHasReport 而隐藏。
        var selectorBorder = Panel().Descendants(P + "Border")
            .Single(b => b.Descendants(P + "ComboBox")
                .Any(c => (string?)c.Attribute("ItemsSource") == "{Binding NmProfileChoices}"));

        ((string?)selectorBorder.Attribute("Visibility")).Should().BeNull(
            "选择器不随分析状态隐藏");
        selectorBorder.Descendants(P + "ComboBox").Should().ContainSingle();
    }

    [Fact]
    public void Panel_ProfileSelector_ShowsHintAndCandidateSummary()
    {
        var selectorBorder = Panel().Descendants(P + "Border")
            .Single(b => b.Descendants(P + "ComboBox")
                .Any(c => (string?)c.Attribute("ItemsSource") == "{Binding NmProfileChoices}"));

        var texts = selectorBorder.Descendants(P + "TextBlock")
            .Select(t => (string?)t.Attribute("Text"))
            .ToList();
        texts.Should().Contain("{Binding NmProfileSelectorHint}");
        texts.Should().Contain("{Binding NmProfileCandidatesSummary}");
    }

    /// <summary>
    /// <b>能力 chip 不能平铺成一片</b>：10 个 chip 在 280px 面板下换行成 3-4 行、
    /// 吃掉 60-80px，而其中 8 个是「可用」——它们说的是同一件事，占的却是
    /// 最贵的纵向空间。默认只留一行摘要（可用 N/M），逐个能力移入折叠内容。
    /// </summary>
    [Fact]
    public void Panel_CapabilityChips_CollapseToSummaryByDefault()
    {
        // 摘要行常驻可见（在封顶区之外，见 Panel_CapabilitySummary_IsOutsideTheCappedBand）。
        Panel().Descendants(P + "TextBlock")
            .Select(t => (string?)t.Attribute("Text"))
            .Should().Contain("{Binding NmCapabilitySummary}",
                "默认只显示「能力 8/10 可用」，不给 10 个 chip 平铺的机会");

        // 逐个 chip 的 ItemsControl 必须默认不可见（收进折叠区）。
        var chips = Panel().Descendants(P + "ItemsControl")
            .Single(i => (string?)i.Attribute("ItemsSource") == "{Binding NmCapabilityChips}");
        var host = chips.Ancestors(P + "Expander").FirstOrDefault();
        host.Should().NotBeNull("chip 明细要收进 Expander，而不是直接平铺");
        ((string?)host!.Attribute("IsExpanded")).Should().Be("False",
            "10 个 chip 换行后吃掉 60-80px，默认展开会重新造成堆积");
    }

    /// <summary>
    /// <b>汇总必须贴着折叠头，不能随展开漂移</b>：<c>DockPanel</c> 里 Dock=Right 的
    /// 子元素拿到的是剩余区域的全高槽位，<c>VerticalAlignment="Center"</c> 是在
    /// 这个全高内居中。折叠时槽位≈折叠头高度看着正常；一旦展开，槽位变成
    /// 展开内容高度（上限 220），汇总文字会漂到面板中线附近，和「标定面板」
    /// 标题分离，看起来像一段无主标签。
    /// </summary>
    [Fact]
    public void Panel_CalibrationSummary_StaysAnchoredWhenExpanded()
    {
        var calibration = Panel().Descendants(P + "Expander")
            .Single(e => (string?)e.Attribute("Header") == "标定面板");

        var summary = calibration.Parent!.Elements(P + "TextBlock")
            .Single(t => (string?)t.Attribute("Text") == "{Binding NmCalibrationSummary}");

        ((string?)summary.Attribute("VerticalAlignment")).Should()
            .NotBe("Center",
                "Center 会在展开态把汇总漂到面板中线，与折叠头分离");
        ((string?)summary.Attribute("VerticalAlignment")).Should().Be("Top",
            "汇总必须锚在顶部，折叠与展开两种状态下都贴着折叠头");
    }

    /// <summary>
    /// <b>封顶区里不能再套限高滚动</b>：冲突条自带
    /// <c>ScrollViewer MaxHeight="72"</c>，它现在位于顶部封顶
    /// <c>ScrollViewer</c> 内部——鼠标在冲突列表上滚动时内层先吃掉滚轮事件
    /// （WPF 标记 Handled），到边界后外层不再跟随，用户会以为顶部卡住。
    /// 这与本次「标定面板留在滚动区外」遵循的是同一条防嵌套原则。
    /// </summary>
    [Fact]
    public void Panel_ConflictList_DoesNotNestAScrollerInsideTheCappedBand()
    {
        var root = Panel().Descendants(P + "Grid").First();
        var top = root.Elements(P + "ScrollViewer")
            .Single(s => s.Attribute("Grid.Row") is not null && s.Attribute("MaxHeight") is not null);

        // 冲突列表本身可以有滚动，但不能是「限高的内层 ScrollViewer」——
        // 外层 150px 已经统一限高，内层再限会争夺滚轮。
        var conflictList = top.Descendants(P + "ItemsControl")
            .Single(i => (string?)i.Attribute("ItemsSource") == "{Binding NmConflictLines}");
        var nested = conflictList.Ancestors(P + "ScrollViewer")
            .Where(s => s != top && (string?)s.Attribute("MaxHeight") is not null)
            .ToList();

        nested.Should().BeEmpty(
            "封顶区内再套限高滚动会争夺滚轮；冲突列表应跟随外层统一滚动");
    }

    /// <summary>
    /// <b>能力汇总必须一眼可见</b>：它排在封顶区的最后一项，而最重状态
    /// （选择器三条提示 + profile 摘要 + 冲突条 + 语义横幅 + partial）的自然高度
    /// 已超过 150px 上限——汇总会被推到首屏之外，要滚动才看得到，
    /// 与「用一行汇总替代 10 个 chip」的初衷相反。
    /// </summary>
    [Fact]
    public void Panel_CapabilitySummary_IsOutsideTheCappedBand()
    {
        var root = Panel().Descendants(P + "Grid").First();
        var top = root.Elements(P + "ScrollViewer")
            .Single(s => s.Attribute("Grid.Row") is not null && s.Attribute("MaxHeight") is not null);

        top.Descendants(P + "TextBlock")
            .Select(t => (string?)t.Attribute("Text"))
            .Should().NotContain("{Binding NmCapabilitySummary}",
                "能力汇总排在封顶区最后，最重状态下会被推到首屏之外");

        // 但也不能丢：必须仍出现在面板里（封顶区外常驻）。
        Panel().Descendants(P + "TextBlock")
            .Select(t => (string?)t.Attribute("Text"))
            .Should().Contain("{Binding NmCapabilitySummary}",
                "汇总要移到封顶区外常驻，不能直接删掉");
    }

    // —— 标定面板（spec §10.2）——

    [Fact]
    public void Panel_HasCalibrationPanel_CollapsedByDefault()
    {
        var calibration = Panel().Descendants(P + "Expander")
            .Single(e => (string?)e.Attribute("Header") == "标定面板");

        ((string?)calibration.Attribute("IsExpanded")).Should().Be("False",
            "四块表常驻会吃掉 360px 面板的绝大部分垂直空间");

        // Visibility 挂在外层 DockPanel 上（汇总行与折叠头是同一块）。
        var host = calibration.Parent!;
        ((string?)host.Attribute("Visibility")).Should()
            .Contain("NmHasCalibration", "无内容时整块隐藏，不留空壳");
    }

    /// <summary>
    /// <b>折叠态必须看得到汇总</b>：<c>NmCalibrationSummary</c>（「21 条 · 1 触发 · 2 未建模」）
    /// 是判断「有没有问题」的唯一一眼可见处。它原先在 <c>Expander</c> 内容里，
    /// 默认折叠时用户看不到——等于面板折叠后完全不知道这次分析有没有触发规则。
    /// </summary>
    [Fact]
    public void Panel_CalibrationSummary_IsVisibleWhenCollapsed()
    {
        var calibration = Panel().Descendants(P + "Expander")
            .Single(e => (string?)e.Attribute("Header") == "标定面板");

        // 在 Expander 内容里 → 折叠时不可见，不满足要求。
        calibration.Descendants(P + "TextBlock")
            .Select(t => (string?)t.Attribute("Text"))
            .Should().NotContain("{Binding NmCalibrationSummary}",
                "汇总放在展开内容里，默认折叠时完全看不见");

        // 必须是 Expander 的兄弟（同一 DockPanel 内），折叠态照样渲染。
        calibration.Parent!.Elements(P + "TextBlock")
            .Select(t => (string?)t.Attribute("Text"))
            .Should().Contain("{Binding NmCalibrationSummary}",
                "折叠头旁要常驻汇总，否则用户得先展开才知道有没有触发");
    }

    /// <summary>
    /// <b>标定面板必须能滚动</b>：四块表（规则 / 观测 vs 期望 / 体检 / 未建模条款）
    /// 全展开时内容高度远超 360px 面板。没有 <c>ScrollViewer</c> + <c>MaxHeight</c> 时，
    /// <c>Expander</c> 里的 <c>StackPanel</c> 会无限撑高，把下面的时间条与事件表挤到
    /// 各自的 <c>MinHeight</c>，超出的内容直接被裁掉——用户看不到也滚不到。
    /// <para>
    /// 断言 <c>MaxHeight</c> 而非只断言 <c>ScrollViewer</c> 存在：光有 ScrollViewer
    /// 而外层是 <c>Auto</c> 行时，它会被内容撑到自然高度，滚动条永不出现——
    /// 那是「加了控件但没解决问题」。
    /// </para>
    /// </summary>
    [Fact]
    public void Panel_CalibrationPanel_ScrollsWhenContentOverflows()
    {
        var calibration = Panel().Descendants(P + "Expander")
            .Single(e => (string?)e.Attribute("Header") == "标定面板");

        var scroller = calibration.Descendants(P + "ScrollViewer").FirstOrDefault();
        scroller.Should().NotBeNull(
            "四块表常驻会超出面板高度，没有 ScrollViewer 时超出的行被裁掉");

        ((string?)scroller!.Attribute("MaxHeight")).Should().NotBeNullOrEmpty(
            "只有 ScrollViewer 而无 MaxHeight 时它会被内容撑到自然高度，滚动条永不出现");

        var visibility = (string?)scroller.Attribute("VerticalScrollBarVisibility");
        visibility.Should().Be("Auto",
            "内容不溢出时不该出现滚动条——常驻滚动条会让人以为下面还有内容");

        // 四块表必须都在 ScrollViewer 内，否则滚不到的那部分照样被裁。
        var itemsSources = scroller.Descendants(P + "ItemsControl")
            .Select(i => (string?)i.Attribute("ItemsSource"))
            .ToList();
        itemsSources.Should().Contain("{Binding NmCalibrationGroups}");
        itemsSources.Should().Contain("{Binding NmCalibrationComparisons}");
        itemsSources.Should().Contain("{Binding NmDataQualityChecks}");
        itemsSources.Should().Contain("{Binding NmUnmodeledClauses}");
    }

    [Fact]
    public void Panel_CalibrationPanel_HasAllFourSections()
    {
        var calibration = Panel().Descendants(P + "Expander")
            .Single(e => (string?)e.Attribute("Header") == "标定面板");

        // spec §10.2 四块：规则表 / 观测 vs 期望 / 数据体检 / 未建模条款。
        var itemsSources = calibration.Descendants(P + "ItemsControl")
            .Select(i => (string?)i.Attribute("ItemsSource"))
            .ToList();

        itemsSources.Should().Contain("{Binding NmCalibrationGroups}");
        itemsSources.Should().Contain("{Binding NmCalibrationComparisons}");
        itemsSources.Should().Contain("{Binding NmDataQualityChecks}");
        itemsSources.Should().Contain("{Binding NmUnmodeledClauses}");
    }

    /// <summary>
    /// spec §10.2「点击 seek 到证据时刻」：规则行必须能跳转。
    /// <para>
    /// 无证据时刻的行不可点（Cursor 保持箭头）——否则「看起来能点但没反应」
    /// 会被当成 bug。
    /// </para>
    /// </summary>
    [Fact]
    public void Panel_CalibrationRuleRow_SeeksToEvidenceTime()
    {
        var calibration = Panel().Descendants(P + "Expander")
            .Single(e => (string?)e.Attribute("Header") == "标定面板");

        var mouseBindings = calibration.Descendants(P + "MouseBinding")
            .Where(m => ((string?)m.Attribute("Command") ?? "")
                .Contains("SeekToNmTimeOrIgnore", StringComparison.Ordinal))
            .ToList();

        mouseBindings.Should().NotBeEmpty("规则行必须能 seek 到证据时刻");
        mouseBindings.Should().Contain(m =>
            (((string?)m.Attribute("CommandParameter")) ?? "")
                .Contains("FirstEvidenceTime", StringComparison.Ordinal),
            "跳转时刻来自该规则的证据时间");

        // 可点性由 CanSeek 驱动，不能所有行都显示手型。
        calibration.Descendants(P + "DataTrigger").Should()
            .Contain(t => (string?)t.Attribute("Binding") == "{Binding CanSeek}");
    }

    /// <summary>
    /// <b>实测发现的空白行</b>：规则行原先只绑 <c>Reason</c>，而它仅「未建模」时非 null，
    /// 触发行在界面上除规则 ID 外一个字都没有——finding 原文一直躺在
    /// <see cref="Core.Analysis.Nm.NmCalibrationRow.Messages"/> 里没人渲染。
    /// </summary>
    [Fact]
    public void Panel_CalibrationRuleRow_RendersSummaryNotJustReason()
    {
        var calibration = Panel().Descendants(P + "Expander")
            .Single(e => (string?)e.Attribute("Header") == "标定面板");

        var texts = calibration.Descendants(P + "TextBlock")
            .Select(t => (string?)t.Attribute("Text"))
            .ToList();

        texts.Should().Contain("{Binding Summary}",
            "每行必须有说明：触发显示 finding 原文、未触发显示观测依据");
        texts.Should().Contain("{Binding Title}",
            "规则的中文含义要直接可见，不能只藏在 ToolTip 里");
        texts.Should().NotContain("{Binding Reason, TargetNullValue=''}",
            "只绑 Reason 会让触发/未触发行整行为空");
    }

    /// <summary>
    /// 「不适用」必须有自己的配色，不能与触发/未建模混用——
    /// 三者是不同结论：出问题 / 没查成 / 这次碰不到。
    /// </summary>
    [Fact]
    public void Panel_CalibrationRuleRow_StylesNotApplicableStatus()
    {
        var calibration = Panel().Descendants(P + "Expander")
            .Single(e => (string?)e.Attribute("Header") == "标定面板");

        var statusTriggers = calibration.Descendants(P + "DataTrigger")
            .Where(t => (string?)t.Attribute("Binding") == "{Binding StatusText}")
            .ToList();

        // 断言具体色值而不只是「互不相同」：三个色一起改错也能通过后者。
        ForegroundOf(statusTriggers, "触发").Should().Be("#C0392B");
        ForegroundOf(statusTriggers, "未建模").Should().Be("#B26A00");
        ForegroundOf(statusTriggers, "不适用").Should().Be("#4A6C8C");
    }

    /// <summary>
    /// 规则表按结构分组渲染：13 条预留位检查折叠成一行，否则占掉半屏
    /// 把触发行淹掉。折叠头必须带结论摘要与展开按钮。
    /// </summary>
    [Fact]
    public void Panel_CalibrationRules_GroupedWithCollapsibleHeader()
    {
        var calibration = Panel().Descendants(P + "Expander")
            .Single(e => (string?)e.Attribute("Header") == "标定面板");

        // 顶层绑定分组集合，而不是平铺行。
        calibration.Descendants(P + "ItemsControl")
            .Select(i => (string?)i.Attribute("ItemsSource"))
            .Should().Contain("{Binding NmCalibrationGroups}",
                "规则表要按结构分组渲染，才能折叠同质规则");

        // 折叠头：标题 + 摘要 + 展开按钮。
        calibration.Descendants(P + "TextBlock")
            .Select(t => (string?)t.Attribute("Text"))
            .Should().Contain("{Binding CollapsedHeader}");
        calibration.Descendants(P + "TextBlock")
            .Select(t => (string?)t.Attribute("Text"))
            .Should().Contain("{Binding CollapsedSummary}");
        calibration.Descendants(P + "Button")
            .Select(b => (string?)b.Attribute("Command"))
            .Should().Contain(c => (c ?? "").Contains("ToggleNmCalibrationGroup", StringComparison.Ordinal),
                "折叠组必须有展开/收起按钮");
    }

    /// <summary>
    /// 折叠头必须按组内结论变色：违规用红（与单行「触发」一致）、
    /// 未建模用琥珀（与单行「未建模」一致）。两者共用中性色会让
    /// 「有 13 条没查成」在折叠状态下看不出来。
    /// </summary>
    [Fact]
    public void Panel_CalibrationGroupHeader_ColorsByGroupStatus()
    {
        var calibration = Panel().Descendants(P + "Expander")
            .Single(e => (string?)e.Attribute("Header") == "标定面板");

        var violation = calibration.Descendants(P + "DataTrigger")
            .FirstOrDefault(t => (string?)t.Attribute("Binding") == "{Binding HasViolation}");
        var unmodeled = calibration.Descendants(P + "DataTrigger")
            .FirstOrDefault(t => (string?)t.Attribute("Binding") == "{Binding HasUnmodeled}");

        violation.Should().NotBeNull("折叠头要按 HasViolation 变色，否则违规被折叠藏起来");
        ForegroundOf([violation!], "True").Should().Be("#C0392B",
            "违规用与「触发」一致的红色");

        unmodeled.Should().NotBeNull("未建模的组也要变色，否则折叠后看不出没查成");
        ForegroundOf([unmodeled!], "True").Should().Be("#B26A00",
            "未建模用与单行一致的琥珀色");
    }

    /// <summary>
    /// 事件表「对象」列要显示「名字 (ID)」而不是只显示 ID，且宽度放不下时
    /// 靠 ToolTip 给全文——96px 装不下 <c>NM_Message (0x18FFFA27)</c>。
    /// </summary>
    [Fact]
    public void Panel_EventTable_SubjectColumn_ShowsMessageLabel()
    {
        var grid = Panel().Descendants(P + "DataGrid").Single();
        var subject = grid.Element(P + "DataGrid.Columns")!.Elements()
            .Single(c => (string?)c.Attribute("Header") == "对象");

        subject.Name.Should().Be(P + "DataGridTemplateColumn",
            "对象列要显示「名字 (ID)」并加省略号，纯文本列做不到");

        var text = subject.Descendants(P + "TextBlock")
            .Single(t => (string?)t.Attribute("Text") == "{Binding SubjectLabel}");
        text.Attribute("ToolTip").Should().NotBeNull(
            "列宽放不下完整标识，必须靠 ToolTip 给全文");
        text.Attribute("TextTrimming")?.Value.Should().Be("CharacterEllipsis");
    }

    /// <summary>
    /// 规则行的 ToolTip 必须能给出全文：正文 <c>Title</c> 用省略号截断，
    /// 截掉的尾部常常正是条款号，没有 ToolTip 就无处可看。
    /// </summary>
    [Fact]
    public void Panel_CalibrationRuleRow_ToolTipCarriesFullText()
    {
        var calibration = Panel().Descendants(P + "Expander")
            .Single(e => (string?)e.Attribute("Header") == "标定面板");

        calibration.Descendants(P + "Border")
            .Select(b => (string?)b.Attribute("ToolTip"))
            .Should().Contain("{Binding ToolTipText}",
                "ToolTip 要带规则 ID + 完整含义 + 条款号，不能只有 ID");
    }

    /// <summary>
    /// 规则表顶部必须有汇总（如「21 条 · 1 触发 · 18 未触发」）：
    /// 平铺时用户要自己数哪几条挂了。
    /// <para>
    /// 汇总已提到折叠头旁（<see cref="Panel_CalibrationSummary_IsVisibleWhenCollapsed"/>）——
    /// 展开后再重复一次是噪音，这里只断言全面板恰好一处。
    /// </para>
    /// </summary>
    [Fact]
    public void Panel_CalibrationRuleSection_ShowsSummary()
    {
        Panel().Descendants(P + "TextBlock")
            .Select(t => (string?)t.Attribute("Text"))
            .Should().ContainSingle(t => t == "{Binding NmCalibrationSummary}",
                "汇总只出现一次（折叠头旁），展开内容里不再重复");
    }

    /// <summary>
    /// spec §10.2「未建模条款：显式列出，附条款号」。
    /// <para>
    /// 多条款规则（<c>NM_STOP_SENDING → NM_09/NM_12</c>）只显示第一个会丢条款。
    /// </para>
    /// </summary>
    [Fact]
    public void Panel_UnmodeledClauseRow_ShowsClauseNumbers()
    {
        var calibration = Panel().Descendants(P + "Expander")
            .Single(e => (string?)e.Attribute("Header") == "标定面板");

        calibration.Descendants(P + "Run").Should()
            .Contain(r => (((string?)r.Attribute("Text")) ?? "")
                    .Contains("ClausesText", StringComparison.Ordinal),
                "未建模条款行必须显示条款号（spec §10.2）");
    }

    [Fact]
    public void Panel_CalibrationRuleTable_DistinguishesTriggeredStatus()
    {
        // 「触发」必须视觉上区别于「未触发」——否则标定面板最重要的一列没有信号。
        var calibration = Panel().Descendants(P + "Expander")
            .Single(e => (string?)e.Attribute("Header") == "标定面板");

        var statusTriggers = calibration.Descendants(P + "DataTrigger")
            .Where(t => (string?)t.Attribute("Binding") == "{Binding StatusText}")
            .ToList();

        statusTriggers.Should().NotBeEmpty("规则状态必须有颜色区分");
        statusTriggers.Should().Contain(t => (string?)t.Attribute("Value") == "触发");
        statusTriggers.Should().Contain(t => (string?)t.Attribute("Value") == "未建模");
    }

    /// <summary>
    /// 冲突条数不可控，必须有高度上限——但上限现在由外层顶部封顶区
    /// （<c>MaxHeight="150"</c>）统一提供，冲突条不再自带内层限高滚动。
    /// 内层再套一层会争夺滚轮：鼠标在冲突列表上滚动时内层先吃掉事件，
    /// 到边界后外层不跟随，用户会以为顶部卡住。
    /// </summary>
    [Fact]
    public void Panel_ConflictBar_IsHeightCapped()
    {
        var root = Panel().Descendants(P + "Grid").First();
        var top = root.Elements(P + "ScrollViewer")
            .Single(s => s.Attribute("Grid.Row") is not null && s.Attribute("MaxHeight") is not null);

        // 冲突列表必须在封顶区内，才受统一上限保护。
        top.Descendants(P + "ItemsControl")
            .Select(i => (string?)i.Attribute("ItemsSource"))
            .Should().Contain("{Binding NmConflictLines}",
                "冲突列表要在封顶区内，否则条数不可控时会撑高面板");

        // 但不能再有第二层滚动：冲突列表往上找祖先，封顶区之外不该再有 ScrollViewer。
        var conflictItems = top.Descendants(P + "ItemsControl")
            .Single(i => (string?)i.Attribute("ItemsSource") == "{Binding NmConflictLines}");
        conflictItems.Ancestors(P + "ScrollViewer")
            .Count(s => s != top).Should().Be(0,
                "冲突列表与外层封顶区之间不能再夹一层滚动，嵌套会争夺滚轮");
    }

    [Fact]
    public void Wizard_HasUsableMinimumSizeAndByteColumns()
    {
        var window = Wizard().Root!;
        double.Parse((string)window.Attribute("MinWidth")!, CultureInfo.InvariantCulture)
            .Should().BeGreaterThanOrEqualTo(900);
        double.Parse((string)window.Attribute("MinHeight")!, CultureInfo.InvariantCulture)
            .Should().BeGreaterThanOrEqualTo(560);

        var columns = window.Descendants(P + "Grid").First()
            .Element(P + "Grid.ColumnDefinitions")!.Elements(P + "ColumnDefinition").ToList();
        ((string?)columns[1].Attribute("Width")).Should().Be("360",
            "中栏要容纳 86px 标签 + 5~6 个 48px 字节按钮，330 会让每帧拆成两行");
    }

    [Fact]
    public void Wizard_PrimaryActions_ArePinnedOutsideEditorScroll()
    {
        var editorScroll = Wizard().Descendants(P + "ScrollViewer")
            .Single(s => (string?)s.Attribute("Grid.Row") == "2");
        editorScroll.Descendants(P + "Button")
            .Should().NotContain(b => (string?)b.Attribute("Content") == "保存到项目",
                "保存/取消不能跟着编辑区滚走");

        var actions = Wizard().Descendants(P + "WrapPanel")
            .Single(w => (string?)w.Attribute("Grid.Row") == "3");
        actions.Descendants(P + "Button")
            .Select(b => (string?)b.Attribute("Content"))
            .Should().Contain(PinnedActions);
    }
}
