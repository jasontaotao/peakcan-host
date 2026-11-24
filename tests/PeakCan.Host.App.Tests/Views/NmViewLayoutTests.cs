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
    public void Panel_Legend_IsCollapsedByDefault()
    {
        var legend = Panel().Descendants(P + "Expander")
            .Single(e => (string?)e.Attribute("Header") == "图例");

        ((string?)legend.Attribute("IsExpanded")).Should().Be("False",
            "图例常驻四行会吃掉 360px 面板约 80px 垂直空间；默认折叠");

        // 折叠内容不能被删掉，且颜色必须与轨道同源（x:Static 笔刷），不能写死色值。
        // 四行 = 时间推断阶段 / NMSts 实测阶段（spec §7.5.4）/ 事件 / 整网行。
        legend.Descendants(P + "TextBlock").Should().HaveCount(4);
        var runs = legend.Descendants(P + "Run").ToList();
        runs.Count(r => ((string?)r.Attribute("Foreground"))?
                .StartsWith("{x:Static conv:NmSegmentBrushConverter.", StringComparison.Ordinal) == true)
            .Should().BeGreaterThanOrEqualTo(10,
                "图例色块必须引用转换器笔刷，否则时间条改色后图例会失真");
    }

    [Fact]
    public void Panel_TimelineAndEventTable_ShareRemainingHeight()
    {
        var root = Panel().Descendants(P + "Grid").First();
        var rows = root.Element(P + "Grid.RowDefinitions")!.Elements(P + "RowDefinition").ToList();

        // 11 行：选择器 / profile 摘要 / 冲突 / 语义横幅 / partial / 能力 chip /
        // 标定面板 / 时间条 / 筛选 / 动作 / 事件表。
        rows.Should().HaveCount(11);
        // 时间条与事件表都按比例分配；事件表不再固定 160px。
        ((string?)rows[7].Attribute("Height")).Should().Be("2*");
        ((string?)rows[7].Attribute("MinHeight")).Should().NotBeNull();
        ((string?)rows[10].Attribute("Height")).Should().Be("*");
        ((string?)rows[10].Attribute("MinHeight")).Should().NotBeNull();
    }

    [Fact]
    public void Panel_FiltersAndActions_WrapInsteadOfClipping()
    {
        var root = Panel().Descendants(P + "Grid").First();

        var filters = root.Elements()
            .Single(e => (string?)e.Attribute("Grid.Row") == "8");
        filters.Name.Should().Be(P + "WrapPanel", "360px 下横向 StackPanel 会把计数裁掉");

        var actions = root.Elements()
            .Single(e => (string?)e.Attribute("Grid.Row") == "9");
        actions.Name.Should().Be(P + "StackPanel");
        actions.Elements(P + "WrapPanel").Should().ContainSingle();
        // 状态文案与“面板=事实 / AI=解释”的分工说明各占一行，
        // 不跟随按钮换行被挤出面板。
        var actionTexts = actions.Elements(P + "TextBlock").ToList();
        actionTexts.Should().HaveCount(2);
        actionTexts.Select(t => (string?)t.Attribute("Text"))
            .Should().Contain("{Binding NmStatus}");
        actionTexts.Select(t => (string?)t.Attribute("Text"))
            .Should().Contain(t => !string.IsNullOrEmpty(t) && t.Contains("AI 负责解释"));
    }

    [Fact]
    public void Panel_ProfileBarAndEmptyHint_ReactToReportState()
    {
        var root = Panel().Descendants(P + "Grid").First();

        var profileBar = root.Elements(P + "Border")
            .Single(b => (string?)b.Attribute("Grid.Row") == "1");
        ((string?)profileBar.Attribute("Visibility")).Should()
            .Contain("NmHasReport", "分析前不能渲染空的 Profile 条/补全按钮");

        var hint = root.Elements(P + "TextBlock")
            .Single(t => (string?)t.Attribute("Grid.Row") == "7");
        ((string?)hint.Attribute("Text")).Should().Contain("尚未分析");
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
        var selectorBorder = Panel().Descendants(P + "Grid").First()
            .Elements(P + "Border")
            .Single(b => (string?)b.Attribute("Grid.Row") == "0");

        ((string?)selectorBorder.Attribute("Visibility")).Should().BeNull(
            "选择器不随分析状态隐藏");
        selectorBorder.Descendants(P + "ComboBox").Should().ContainSingle();
    }

    [Fact]
    public void Panel_ProfileSelector_ShowsHintAndCandidateSummary()
    {
        var selectorBorder = Panel().Descendants(P + "Grid").First()
            .Elements(P + "Border")
            .Single(b => (string?)b.Attribute("Grid.Row") == "0");

        var texts = selectorBorder.Descendants(P + "TextBlock")
            .Select(t => (string?)t.Attribute("Text"))
            .ToList();
        texts.Should().Contain("{Binding NmProfileSelectorHint}");
        texts.Should().Contain("{Binding NmProfileCandidatesSummary}");
    }

    // —— 标定面板（spec §10.2）——

    [Fact]
    public void Panel_HasCalibrationPanel_CollapsedByDefault()
    {
        var calibration = Panel().Descendants(P + "Expander")
            .Single(e => (string?)e.Attribute("Header") == "标定面板");

        ((string?)calibration.Attribute("IsExpanded")).Should().Be("False",
            "四块表常驻会吃掉 360px 面板的绝大部分垂直空间");
        ((string?)calibration.Attribute("Visibility")).Should()
            .Contain("NmHasCalibration", "无内容时整块隐藏，不留空壳");
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
        itemsSources.Should().Contain("{Binding NmCalibrationRows}");
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

        itemsSources.Should().Contain("{Binding NmCalibrationRows}");
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
    /// </summary>
    [Fact]
    public void Panel_CalibrationRuleSection_ShowsSummary()
    {
        var calibration = Panel().Descendants(P + "Expander")
            .Single(e => (string?)e.Attribute("Header") == "标定面板");

        calibration.Descendants(P + "TextBlock").Select(t => (string?)t.Attribute("Text"))
            .Should().Contain("{Binding NmCalibrationSummary}",
                "规则表要有一句话汇总，让「有没有问题」一眼可见");
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

    [Fact]
    public void Panel_ConflictBar_IsHeightCapped()
    {
        var conflictScroll = Panel().Descendants(P + "ScrollViewer")
            .Single(s => (string?)s.Attribute("MaxHeight") == "72");
        conflictScroll.Should().NotBeNull("冲突条数不可控，必须限高滚动");
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
