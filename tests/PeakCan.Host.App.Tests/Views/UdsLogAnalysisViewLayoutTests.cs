using System.IO;
using System.Linq;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace PeakCan.Host.App.Tests.Views;

/// <summary>
/// 声明层守卫：日志分析页签/在线控件显隐/分析页命令绑定。绑定路径写错时
/// WPF 静默失败，按钮点了没反应——只能靠 XAML 断言兜住。
/// </summary>
public sealed class UdsLogAnalysisViewLayoutTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly string[] ExpectedRightPanelTabs = { "选中事务", "报告" };

    private static string RepoFile(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
            dir = dir.Parent;

        if (dir is null)
            throw new FileNotFoundException(
                $"找不到 PeakCan.Host.slnx —— 测试是从非仓库目录运行的？起始目录: {AppContext.BaseDirectory}");

        return Path.Combine(new[] { dir.FullName }.Concat(relative).ToArray());
    }

    private static XDocument LoadXaml(params string[] relative)
        => XDocument.Load(RepoFile(relative));

    [Fact]
    public void Window_has_log_analysis_tab_bound_to_child_view_model()
    {
        var doc = LoadXaml("src", "PeakCan.Host.App", "Windows", "UdsWindow.xaml");
        var tabs = doc.Descendants(P + "TabItem").ToList();
        tabs.Should().Contain(t => (string?)t.Attribute("Header") == "日志分析");

        var tab = tabs.Single(t => (string?)t.Attribute("Header") == "日志分析");
        tab.Descendants().Should().Contain(e =>
            (((string?)e.Attribute("DataContext")) ?? "").Contains("LogAnalysis", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Online_controls_are_hidden_when_log_analysis_tab_selected()
    {
        var doc = LoadXaml("src", "PeakCan.Host.App", "Windows", "UdsWindow.xaml");
        doc.Descendants(P + "Button").Should().Contain(b =>
            (string?)b.Attribute("Content") == "默认" &&
            (((string?)b.Attribute("Visibility")) ?? "").Contains("IsOnlineTab", System.StringComparison.Ordinal));

        // ODX 按钮不绑定在线页签显隐（加载 ODX 在离线页仍可用）
        var odx = doc.Descendants(P + "Button").Single(b => (string?)b.Attribute("Content") == "加载 ODX…");
        (((string?)odx.Attribute("Visibility")) ?? "").Should().NotContain("IsOnlineTab");
    }

    /// <summary>
    /// 筛选框必须自带可见说明。
    /// <para>
    /// 它原来是一个无标签的空框，唯一的说明在 ToolTip 里——用户不悬停就不知道它
    /// 是筛选，反而会当成「功能 ID」后面的第二个地址框。工具栏上其他输入框都有
    /// 标签，只有这个没有，光秃秃的框就是误导。
    /// </para>
    /// </summary>
    [Fact]
    public void Filter_box_explains_itself_without_hover()
    {
        var doc = LoadXaml("src", "PeakCan.Host.App", "Views", "UdsLogAnalysisView.xaml");

        var filterBox = doc.Descendants(P + "TextBox")
            .Single(t => ((string?)t.Attribute("Text"))?.Contains("FilterText") == true);

        var hint = filterBox.Parent!.Descendants(P + "TextBlock")
            .SingleOrDefault(t => ((string?)t.Attribute("Text"))
                ?.StartsWith("筛选", System.StringComparison.Ordinal) == true);

        hint.Should().NotBeNull("空框时必须显示可见提示，说明它能搜什么");

        ((string?)hint!.Attribute("IsHitTestVisible")).Should().Be("False",
            "提示覆盖在输入框上，不能抢点击或光标");

        hint.Descendants(P + "DataTrigger").Should().Contain(dt =>
                (((string?)dt.Attribute("Binding")) ?? "").Contains("FilterText", System.StringComparison.Ordinal),
            "提示必须在开始输入后消失，否则会压在输入的文字上");
    }

    /// <summary>
    /// 通道必须是可编辑下拉框（选项来自日志实际通道），不是纯手敲输入框。
    /// </summary>
    [Fact]
    public void Channel_input_is_an_editable_combo_box()
    {
        var doc = LoadXaml("src", "PeakCan.Host.App", "Views", "UdsLogAnalysisView.xaml");

        var combo = doc.Descendants(P + "ComboBox")
            .SingleOrDefault(c => (((string?)c.Attribute("Text")) ?? "")
                .Contains("ChannelText", System.StringComparison.Ordinal));

        combo.Should().NotBeNull("通道输入必须提供下拉选择");
        ((string?)combo!.Attribute("IsEditable")).Should().Be("True", "同时要允许手动输入未出现过的通道");
        ((string?)combo.Attribute("ItemsSource")).Should().Be("{Binding AvailableChannels}",
            "选项来自加载后统计到的实际通道");
    }

    /// <summary>
    /// 地址输入框必须容纳 8 位十六进制（29 位扩展帧）。
    /// 54px 时最后一个字符会被裁掉，用户会以为输入没生效或只支持 11 位。
    /// </summary>
    [Fact]
    public void Address_id_boxes_fit_eight_hex_digits()
    {
        var doc = LoadXaml("src", "PeakCan.Host.App", "Views", "UdsLogAnalysisView.xaml");
        string[] bindings = { "RequestIdText", "ResponseIdText", "FunctionalIdText" };

        var boxes = doc.Descendants(P + "TextBox")
            .Where(t => bindings.Any(b => (((string?)t.Attribute("Text")) ?? "")
                .Contains(b, System.StringComparison.Ordinal)))
            .ToList();

        boxes.Should().HaveCount(3, "请求/响应/功能 ID 三个输入框都必须检查");

        foreach (var box in boxes)
        {
            var width = double.Parse((string?)box.Attribute("Width") ?? "0",
                System.Globalization.CultureInfo.InvariantCulture);
            width.Should().BeGreaterThanOrEqualTo(72,
                "29 位 ID 是 8 位十六进制，输入框至少要显示完整 8 个字符");
        }
    }

    /// <summary>
    /// 通道误配提示条必须用 DockPanel：横向 StackPanel 以无限宽度测量，
    /// <c>TextWrapping</c> 失效，窗口收窄时右侧的一键切换按钮会被裁掉。
    /// </summary>
    [Fact]
    public void Address_hint_banner_wraps_text_and_keeps_the_action_visible()
    {
        var doc = LoadXaml("src", "PeakCan.Host.App", "Views", "UdsLogAnalysisView.xaml");

        var hint = doc.Descendants(P + "TextBlock")
            .Single(t => (((string?)t.Attribute("Text")) ?? "").Contains("AddressHint", System.StringComparison.Ordinal));

        ((string?)hint.Attribute("TextWrapping")).Should().Be("Wrap",
            "提示文案较长，窗口变窄时必须能换行");

        var panel = hint.Ancestors(P + "DockPanel").First();
        var button = panel.Elements(P + "Button")
            .SingleOrDefault(b => (((string?)b.Attribute("Command")) ?? "")
                .Contains("ApplySuggestedChannelCommand", System.StringComparison.Ordinal));

        button.Should().NotBeNull("一键切换按钮必须在提示条内，且命令名与 VM 一致");
        ((string?)button!.Attribute("DockPanel.Dock")).Should().Be("Right",
            "按钮固定在右侧，文字换行不会把它挤出可视区");
    }

    [Fact]
    public void Analysis_view_exposes_required_commands()
    {
        var doc = LoadXaml("src", "PeakCan.Host.App", "Views", "UdsLogAnalysisView.xaml");
        var commands = doc.Descendants(P + "Button").Select(b => (string?)b.Attribute("Command")).ToList();

        commands.Should().Contain("{Binding OpenLogCommand}");
        commands.Should().Contain("{Binding AnalyzeCommand}");
        commands.Should().Contain(c => c != null && c.Contains("Export"));
        // 通道误配提示的按钮：命令名写错时 WPF 静默失败，点了没反应。
        commands.Should().Contain("{Binding ApplySuggestedChannelCommand}");
    }

    [Fact]
    public void Analysis_view_shows_interpretation_degradation_counts_and_odx_state()
    {
        var doc = LoadXaml("src", "PeakCan.Host.App", "Views", "UdsLogAnalysisView.xaml");
        var attributes = new[] { "Text", "Binding", "ItemsSource", "Visibility" };
        var bindings = doc.Descendants()
            .SelectMany(e => attributes.Select(a => (string?)e.Attribute(a)))
            .Where(v => v != null)
            .ToList();

        // 解释结果走「自带标签、空则 null」的 VM 属性，不再直接绑 Interpretation.*
        // 再配 StringFormat——后者在 null 时会渲染成光秃秃的「DID：」，像加载失败。
        bindings.Should().Contain(b => b!.Contains("SelectedDidNameLine"));
        bindings.Should().Contain(b => b!.Contains("SelectedDecodedValueLine"));
        bindings.Should().Contain(b => b!.Contains("SelectedDegradeReasonLine"));

        // 问题帧（地址匹配但拼不成 PDU）必须可见。原来绑的是 Noise.Reasons，
        // 现在走 ProblemFrameReasons——语义没变，只是和背景流量分开了。
        bindings.Should().Contain(b => b!.Contains("ProblemFrameReasons"));

        // 背景流量改成按规律画像，不再是「Top-20 ID × 帧数」的裸列表。
        bindings.Should().Contain(b => b!.Contains("BusTrafficRows"));
        bindings.Should().Contain(b => b!.Contains("BusTrafficSummary"));

        bindings.Should().Contain(b => b!.Contains("IsOdxLoaded"));
        bindings.Should().Contain(b => b!.Contains("FilterEmptyHint"));
        bindings.Should().Contain(b => b!.Contains("Interpretation.IsUnknownService"));
        bindings.Should().Contain(b => b!.Contains("OrphanResponses.Count"));
        bindings.Should().Contain(b => b!.Contains("SelectedConclusion"));

        // spec §6.5 / §12 要求孤立响应和重复最终响应「单列计数和证据」。
        bindings.Should().Contain(b => b!.Contains("OrphanResponseRows"));
        bindings.Should().Contain(b => b!.Contains("DuplicateFinalResponseRows"));

        // spec §8.5 要求表外服务显示「未校验」。
        bindings.Should().Contain(b => b!.Contains("EchoCheckDisplay"));
    }

    /// <summary>
    /// 结论区必须限高内滚。刷写日志的 finding 能到十几条，自然撑开会把
    /// 下面的交易表推出窗口——那就等于没重排。
    /// </summary>
    [Fact]
    public void Conclusion_band_is_height_capped_with_internal_scrolling()
    {
        var doc = LoadXaml("src", "PeakCan.Host.App", "Views", "UdsLogAnalysisView.xaml");

        var conclusion = doc.Descendants(P + "Expander")
            .Single(e => e.Descendants(P + "Run").Any(r => (string?)r.Attribute("Text") == "结论"));

        var bordered = conclusion.Descendants(P + "Border").First();
        bordered.Attribute("MaxHeight").Should().NotBeNull("结论带必须限高，否则会挤走事务表");

        bordered.Descendants(P + "ScrollViewer").Should().NotBeEmpty(
            "限高后必须内部滚动，否则内容直接被裁掉");
    }

    /// <summary>
    /// 右栏必须是 TabControl（选中事务 / 报告），且 Tab 内不能再套外层 ScrollViewer——
    /// 那会让 DataGrid 失去虚拟化。
    /// </summary>
    [Fact]
    public void Right_panel_splits_selection_and_report_into_tabs()
    {
        var doc = LoadXaml("src", "PeakCan.Host.App", "Views", "UdsLogAnalysisView.xaml");

        var tabs = doc.Descendants(P + "TabControl").Single();
        var headers = tabs.Elements(P + "TabItem").Select(t => (string?)t.Attribute("Header")).ToList();

        headers.Should().BeEquivalentTo(ExpectedRightPanelTabs);
    }

    /// <summary>
    /// <b>DataTemplate 内不能出现裸 <c>{Binding}</c> 绑到对象上。</b>
    /// <para>
    /// WPF 对非字符串对象会走 <c>ToString()</c>。这些行模型
    /// （<c>WarningSummaryRow</c> / <c>BusTrafficRow</c> / <c>ValidationFailureRow</c> /
    /// <c>PduEvidenceRow</c>）都<b>没有重写 ToString</b>，只暴露了 <c>Display</c>。
    /// 绑成 <c>{Binding}</c> 时界面会显示类名重复 N 行——而 VM 测试断言的是
    /// <c>.Display</c>，所以测试全绿、界面全废。这条守卫就是为这个盲区写的。
    /// </para>
    /// </summary>
    [Fact]
    public void Row_templates_bind_display_not_the_object_itself()
    {
        var doc = LoadXaml("src", "PeakCan.Host.App", "Views", "UdsLogAnalysisView.xaml");

        var bareBindings = doc.Descendants(P + "TextBlock")
            .Where(t => (string?)t.Attribute("Text") == "{Binding}")
            .ToList();

        bareBindings.Should().BeEmpty(
            "行模型没有重写 ToString，裸 Binding 会渲染成类名；必须绑 Display");
    }

    /// <summary>
    /// 交易表固定列宽合计不能超过左栏可用宽度。
    /// 默认窗口 1100 − 右栏 340 − splitter ≈ 750，留一点余量。
    /// 超了会把 <c>Width="*"</c> 的「服务」列压到 MinWidth，最关键的一列反而不可读。
    /// </summary>
    [Fact]
    public void Transaction_table_fixed_column_widths_fit_the_left_pane()
    {
        const int DefaultWindowWidth = 1100;
        const int RightPaneWidth = 340;
        const int SplitterAndMargins = 40;
        const int Available = DefaultWindowWidth - RightPaneWidth - SplitterAndMargins;

        var doc = LoadXaml("src", "PeakCan.Host.App", "Views", "UdsLogAnalysisView.xaml");
        var grid = doc.Descendants(P + "DataGrid")
            .Single(d => ((string?)d.Attribute("ItemsSource"))?.Contains("VisibleTransactions") == true);

        var fixedTotal = grid.Descendants(P + "DataGridTextColumn")
            .Select(c => (string?)c.Attribute("Width"))
            .Where(w => w is not null && int.TryParse(w, out _))
            .Sum(w => int.Parse(w!, System.Globalization.CultureInfo.InvariantCulture));

        fixedTotal.Should().BeLessThan(Available,
            "固定列宽合计 " + fixedTotal + " 必须给「服务」列留出空间（可用约 " + Available + "）");
    }

    /// <summary>
    /// <b>可选字段不得用 <c>StringFormat='标签：{0}'</c> 绑定。</b>
    /// <para>
    /// WPF 对 null 仍会应用格式串：<c>最终 NRC：0x{0:X2}</c> 在 <c>FinalNrc == null</c>
    /// （正响应）时渲染成 <c>最终 NRC：0x</c>——用户会以为 NRC 是 <c>0x00</c>。
    /// 其余字段（<c>DID：</c> / <c>解码值：</c> / <c>降级原因：</c>）则渲染成光秃秃的标签，
    /// 看起来像「加载失败」。
    /// </para>
    /// <para>
    /// 可选字段必须绑一个<b>自带标签</b>的字符串属性，并用 NullToVisibility 控制显隐。
    /// </para>
    /// </summary>
    [Fact]
    public void Optional_fields_do_not_render_bare_labels_when_null()
    {
        // 这些属性在正响应 / 无 ODX 定义时合法地为 null。
        string[] optionalFields =
        {
            "SelectedTransaction.FinalNrc",
            "SelectedTransaction.NrcName",
            "SelectedTransaction.Interpretation.DidName",
            "SelectedTransaction.Interpretation.DecodedValue",
            "SelectedTransaction.Interpretation.DegradeReason",
            "SelectedTransaction.Interpretation.DegradeDetail",
        };

        var doc = LoadXaml("src", "PeakCan.Host.App", "Views", "UdsLogAnalysisView.xaml");

        // StringFormat 是 Binding 标记扩展里的参数，不是 TextBlock 的独立属性。
        // 直接从 Text 属性里解析：{Binding Path, StringFormat='...'}
        // 注意 FinalNrc 后面紧跟的是逗号（还有 StringFormat），其余字段也一样——
        // 两种分隔符都要认，否则漏判。
        var offenders = doc.Descendants(P + "TextBlock")
            .Select(t => (string?)t.Attribute("Text"))
            .Where(text => text is not null && text.Contains("StringFormat", StringComparison.Ordinal))
            .Where(text => optionalFields.Any(f =>
                text!.Contains("Binding " + f + ",", StringComparison.Ordinal)
                || text!.Contains("Binding " + f + " ", StringComparison.Ordinal)
                || text!.Contains("Binding " + f + "}", StringComparison.Ordinal)))
            .ToArray();

        offenders.Should().BeEmpty(
            "这些字段可为 null，用 StringFormat 拼标签会渲染成「最终 NRC：0x」或光秃秃的标签");
    }
}
