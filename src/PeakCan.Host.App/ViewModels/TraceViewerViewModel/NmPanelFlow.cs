using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Converters;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.App.Services.Nm;
using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Core.Analysis.Nm;

namespace PeakCan.Host.App.ViewModels;

public sealed record NmCapabilityChipVm(string Name, bool Available, string Tooltip);
public sealed record NmTimelineSegmentVm(double Left, double Width, string Kind,
    double Start, double End, string Tooltip = "");

/// <summary>时间条事件标记：设计坐标 X + 形状/颜色 kind；像素换算在 XAML 层完成。</summary>
public sealed record NmTimelineMarkerVm(double X, string Kind, double T, string Tooltip);

public sealed record NmTimelineRowVm(
    string Label,
    IReadOnlyList<NmTimelineSegmentVm> Segments,
    IReadOnlyList<NmTimelineMarkerVm> Markers)
{
    public NmTimelineRowVm(string label, IReadOnlyList<NmTimelineSegmentVm> segments)
        : this(label, segments, Array.Empty<NmTimelineMarkerVm>())
    {
    }

    /// <summary>无标记时折叠轨道，避免每行白白多出 12px。</summary>
    public bool HasMarkers => Markers.Count > 0;
}

public sealed record NmEventRowVm(double T, string TimeText, string Type, string Subject,
    string Detail, string EvidenceKind, int EpisodeIndex, string? CanIdHex = null)
{
    /// <summary>事件表展示用的中文类型名；筛选仍按原始 snake_case 契约。</summary>
    public string TypeLabel => NmTimelineText.EventType(Type);

    /// <summary>事件表展示用的中文证据级别；数据契约仍保留 measured/inferred。</summary>
    public string EvidenceLabel => NmTimelineText.EvidenceKind(EvidenceKind);
}

/// <summary>
/// 时间条/事件表的中文文案。引擎契约仍是 snake_case，只在展示层翻译，
/// 不把 UI 文案写进分析引擎。
/// </summary>
public static class NmTimelineText
{
    public static string SegmentKind(string kind) => kind switch
    {
        NmTimelineKinds.Wakeup => "唤醒",
        NmTimelineKinds.Active => "发送中",
        NmTimelineKinds.Released => "已释放",
        NmTimelineKinds.Silent or NmTimelineKinds.Silence => "静默",
        NmTimelineKinds.Episode => "活跃区间",
        NmTimelineKinds.Burst => "突发",
        NmTimelineKinds.Gap => "超时空档",
        NmTimelineKinds.BusGap => "总线无帧",
        NmTimelineKinds.Byte => "字节变化",
        NmTimelineKinds.Bit => "位变化",
        NmTimelineKinds.Signal => "信号变化",
        NmTimelineKinds.Boundary => "记录边界",
        _ => kind,
    };

    public static string EventType(string type) => type switch
    {
        "burst_observed" => "突发",
        "missed_frame" => "漏帧",
        "sender_silent" => "发送静默",
        "byte_change" => "字节变化",
        "bit_change" => "位变化",
        "signal_change" => "信号变化",
        "all_nm_silent" => "全网 NM 静默",
        "bus_wide_silence" => "总线无帧",
        _ => type,
    };

    public static string EvidenceKind(string kind) => kind switch
    {
        "measured" => "实测",
        "inferred" => "推断",
        "boundary" => "边界",
        _ => kind,
    };

    /// <summary>profile 来源标识（引擎契约）；未知值原样显示，不猜测。</summary>
    public static string Provenance(string resolvedFrom) => resolvedFrom switch
    {
        "dbc" => "DBC",
        "none" => "无",
        _ => resolvedFrom,
    };
}

/// <summary>
/// 事件表筛选（spec §9.1「筛选」）：类型分类 + 对象/详情关键字。
/// 纯函数，便于单测；面板只负责把结果放进集合。
/// </summary>
public static class NmEventFilters
{
    public const string All = "全部";

    /// <summary>下拉顺序与 spec §9.1 的类型口径一致。</summary>
    public static readonly IReadOnlyList<string> Categories =
        new[] { All, "周期", "空档", "突发", "字节", "信号", "漏帧" };

    /// <summary>按类型分类过滤单条事件类型字符串。</summary>
    public static bool MatchesCategory(string type, string? category)
    {
        if (string.IsNullOrEmpty(category) || string.Equals(category, All, StringComparison.Ordinal))
            return true;

        return category switch
        {
            // event type 契约是 snake_case（NmAnalyzer/NmContentAnalyzer）。
            // M1 引擎把周期偏差输出为 finding 而不是 event；「周期」当前能命中的
            // 实际事件是漏帧（间隔异常），未来的 cycle_* 事件也归入此分类。
            "周期" => type.Contains("cycle", StringComparison.OrdinalIgnoreCase)
                      || type.Contains("timing", StringComparison.OrdinalIgnoreCase)
                      || type == "missed_frame",
            "空档" => type is "all_nm_silent" or "sender_silent" or "bus_wide_silence",
            "突发" => type == "burst_observed",
            "字节" => type is "byte_change" or "bit_change",
            "信号" => type == "signal_change",
            "漏帧" => type == "missed_frame",
            _ => true,
        };
    }

    /// <summary>过滤事件行：类型分类 + 对象/详情关键字（大小写不敏感）。</summary>
    public static IReadOnlyList<NmEventRowVm> Filter(
        IEnumerable<NmEventRowVm> rows, string? category, string? keyword)
    {
        var term = keyword?.Trim();
        return rows
            .Where(r => MatchesCategory(r.Type, category))
            .Where(r => string.IsNullOrEmpty(term)
                || r.Subject.Contains(term, StringComparison.OrdinalIgnoreCase)
                || r.Detail.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}

public sealed partial class TraceViewerViewModel
{
    /// <summary>时间条设计宽度：活动条 Canvas 与准星反算共用这一常量。</summary>
    public const double NmTimelineWidth = 1000.0;
    /// <summary>spec §9.4：分析超过该时长按超时结束，保留 partial 结果。</summary>
    internal static readonly TimeSpan NmAnalysisTimeout = TimeSpan.FromSeconds(30);

    [ObservableProperty] private bool _nmHasReport;
    [ObservableProperty] private bool _nmIsAnalyzing;
    [ObservableProperty] private bool _nmCanCancel;
    [ObservableProperty] private bool _nmIsPartial;
    /// <summary>partial 徽标文案：区分用户取消与分析超时（spec §9.4）。</summary>
    [ObservableProperty] private string _nmPartialText = "";
    [ObservableProperty] private bool _nmHasConflicts;
    /// <summary>语义能力横幅：只有存在不可用项时才显示（补全后不再谎称“不做判定”）。</summary>
    [ObservableProperty] private string _nmSemanticsBanner = "";
    [ObservableProperty] private bool _nmHasSemanticsBanner;
    [ObservableProperty] private string _nmStatus = "";
    [ObservableProperty] private string _nmProfileSummary = "";
    /// <summary>窗口内观测/未出现的 NM 报文明细：解释为什么时间条只有部分报文行。</summary>
    [ObservableProperty] private string _nmObservedSummary = "";
    // 时间轴三档刻度：设计坐标把窗口起止映射到 0/1000，中点正好 50%，
    // 因此左/中/右对齐的三个标签与轨道位置一致，无需像素换算。
    [ObservableProperty] private string _nmTimeAxisStart = "";
    [ObservableProperty] private string _nmTimeAxisMid = "";
    [ObservableProperty] private string _nmTimeAxisEnd = "";
    /// <summary>补全按钮文案；分析前也有可读标签，避免空按钮被压成一条缝。</summary>
    [ObservableProperty] private string _nmMissingText = "补全";
    [ObservableProperty] private string _nmSelectedEventTypeFilter = NmEventFilters.All;
    [ObservableProperty] private string _nmEventKeyword = "";
    /// <summary>筛选后的可见行数/总行数：避免状态栏的“共 N 条”被误读为表格行数。</summary>
    [ObservableProperty] private string _nmEventFilterSummary = "";

    // 时间条 hover（spec §9.1 交互强化）：准星时间读数 + 活动段信息卡。
    // 这两个状态由 View 的鼠标事件驱动，VM 只负责把像素/区间换算成展示文案。
    [ObservableProperty] private bool _nmHoverActive;
    [ObservableProperty] private string _nmHoverTimeText = "";
    [ObservableProperty] private string _nmHoverKind = "";
    [ObservableProperty] private string _nmHoverTitle = "";
    [ObservableProperty] private string _nmHoverRange = "";
    [ObservableProperty] private string _nmHoverHint = "";
    // 最近一次报告的窗口边界：准星像素 → 秒需要它；报告清空时同步清零。
    private double _nmWindowStart;
    private double _nmWindowEnd;

    /// <summary>事件表当前选中行；「在 AI 中解读」按钮的 CanExecute 依赖它。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InterpretNmEventInAiCommand))]
    private NmEventRowVm? _nmSelectedEvent;

    public ObservableCollection<NmCapabilityChipVm> NmCapabilityChips { get; } = new();
    public ObservableCollection<NmTimelineRowVm> NmTimelineRows { get; } = new();
    public ObservableCollection<NmEventRowVm> NmEventRows { get; } = new();
    public ObservableCollection<string> NmEventTypeFilters { get; } = new(NmEventFilters.Categories);
    public ObservableCollection<string> NmConflictLines { get; } = new();

    // 全部事件（上限内），筛选后只把匹配项放进 NmEventRows 供 DataGrid 绑定。
    private readonly List<NmEventRowVm> _allNmEventRows = new();
    // 自动打开补全对话框的去重键（dbc_hash）：同一份 DBC 不重复弹。
    private readonly HashSet<string> _nmAutoWizardShown = new(StringComparer.OrdinalIgnoreCase);

    private Views.TraceViewerViewNmPanel? _nmPanelContent;
    private CancellationTokenSource? _nmCts;
    /// <summary>本次分析的取消是用户发起还是超时：null 状态文案要据此区分。</summary>
    /// <summary>默认按“用户取消”处理：直接 Apply 外部 partial 报告时无法得知真实原因。</summary>
    private bool _nmCancelledByUser = true;
    // 面板最近一次成功分析得到的快照：导出优先导出用户正在看的那一份报告。
    private NmToolSnapshot? _lastNmSnapshot;
    public Views.TraceViewerViewNmPanel? NmPanelContent
    {
        get
        {
            if (_nmPanelContent is null)
            {
                _nmPanelContent = new Views.TraceViewerViewNmPanel { DataContext = this };
                _ = RunNmAnalysisAsync(null);
            }
            return _nmPanelContent;
        }
    }

    public static IReadOnlyList<NmTimelineRowVm> BuildTimelineRows(NmReport report)
    {
        var rows = new List<NmTimelineRowVm>(report.Messages.Count + 1);

        foreach (var m in report.Messages)
        {
            var segments = m.Activity.Select(s =>
            {
                var left = ToX(s.Start, report.Window);
                var right = ToX(s.End, report.Window);
                var tooltip = string.Create(CultureInfo.InvariantCulture,
                    $"{NmTimelineText.SegmentKind(s.Kind)}  {s.Start:F3}s – {s.End:F3}s");
                return new NmTimelineSegmentVm(
                    left, Math.Max(right - left, 1), s.Kind, s.Start, s.End, tooltip);
            }).ToList();

            // spec §9.1：时间条要标记观察突发、超时空档与原始字节/位变化。
            // 事件本身仍留在事件表里；标记独立成轨道（形状+颜色），不再塞进活动条。
            var markers = new List<NmTimelineMarkerVm>();
            foreach (var e in report.Events)
            {
                if (MarkerKind(e.Type) is not { } kind) continue;
                if (!string.Equals(e.CanIdHex, m.CanIdHex, StringComparison.Ordinal)) continue;
                if (e.IsExtended != m.IsExtended) continue;

                markers.Add(new NmTimelineMarkerVm(
                    ToX(e.T, report.Window), kind, e.T,
                    string.Create(CultureInfo.InvariantCulture,
                        $"{NmTimelineText.EventType(e.Type)}  {e.T:F3}s  {e.Detail}")));
            }

            // spec §9.1：时间条默认每个 NM 报文一行，行标签按 DBC 发送节点标注。
            // DBC 未给出 Name/Sender 时不渲染空括号内容，用 "—" 占位。
            var who = string.IsNullOrEmpty(m.Sender) ? "—" : m.Sender;
            var name = string.IsNullOrEmpty(m.Name) ? "" : " " + m.Name;
            rows.Add(new NmTimelineRowVm($"{m.CanIdHex}{name} ({who})", segments, markers));
        }

        if (BuildNetworkTimelineRow(report) is { } network)
            rows.Add(network);

        return rows;
    }

    /// <summary>事件→时间条标记的 Kind；不参与时间条的事件返回 null。</summary>
    private static string? MarkerKind(string type) => type switch
    {
        "burst_observed" => NmTimelineKinds.Burst,
        "missed_frame" or "sender_silent" => NmTimelineKinds.Gap,
        "byte_change" => NmTimelineKinds.Byte,
        "bit_change" => NmTimelineKinds.Bit,
        "signal_change" => NmTimelineKinds.Signal,
        _ => null,
    };

    /// <summary>
    /// 网络级时间条：episode 活动段、静默带、边界/空档标记。
    /// spec §9.1 要求「活动段/静默段/边界」可见；这些不属于单条报文行。
    /// <para>
    /// 2026-11-20 PATCH：<c>bus_wide_silence</c> 可以落在 episode 内部（全网
    /// 断帧短于 NM 超时时 episode 不切分）。这种事件只留「总线无帧」标记：
    /// 事件没有结构化的结束时间，若按「到下个 episode/窗口末尾」补区间，会
    /// 画出一条与本次空档无关的尾段静默（引擎并未判定该尾段静默）。
    /// </para>
    /// <para>
    /// 同一段静默可能有多个来源（头部边界带 + 事件带）；统一合并，避免半透明
    /// 灰叠加成更深的色块，也避免出现两个可点击的重复区间。
    /// </para>
    /// </summary>
    private static NmTimelineRowVm? BuildNetworkTimelineRow(NmReport report)
    {
        var segments = new List<NmTimelineSegmentVm>();
        var markers = new List<NmTimelineMarkerVm>();
        var silenceBands = new List<(double Start, double End, string Tooltip)>();

        foreach (var episode in report.Episodes)
        {
            var left = ToX(episode.Start, report.Window);
            var right = ToX(episode.End, report.Window);
            segments.Add(new NmTimelineSegmentVm(
                left, Math.Max(right - left, 1), NmTimelineKinds.Episode,
                episode.Start, episode.End,
                string.Create(CultureInfo.InvariantCulture,
                    $"活跃区间 {episode.Index}  {episode.Start:F3}s – {episode.End:F3}s")));

            if (episode.BoundaryStart)
                markers.Add(BoundaryMarker(episode.Start, report.Window, "边界：窗口开始时 NM 已在活动"));
            if (episode.BoundaryEnd)
                markers.Add(BoundaryMarker(episode.End, report.Window, "边界：窗口结束时 NM 仍未静默"));
        }

        // 头部静默：NmGapAnalyzer 在「窗口开头到首个 NM 帧 ≥ 超时」时产
        // NM_TRACE_BOUNDARY，证据正是 [窗口起点, 首个 episode 起点]。
        // 同名 finding 还有一条整窗边界（证据 [窗口起点, 窗口终点]），用
        // 「证据终点 == 首个 episode 起点」把它排除，不依赖文案。
        // 首帧恰在窗口终点时两条 finding 证据相同；此时画 [起点, 终点] 仍是
        // 事实（该区间内没有任何 NM 帧），不会产生错误信息。
        foreach (var finding in report.Findings)
        {
            if (!string.Equals(finding.Code, "NM_TRACE_BOUNDARY", StringComparison.Ordinal)) continue;
            if (finding.EvidenceTimes.Count < 2 || report.Episodes.Count == 0) continue;

            var from = finding.EvidenceTimes[0];
            var to = finding.EvidenceTimes[1];
            if (to <= from) continue;
            if (Math.Abs(to - report.Episodes[0].Start) > 1e-6) continue;

            silenceBands.Add((from, to, string.Create(CultureInfo.InvariantCulture,
                $"记录边界：窗口开头无 NM 报文  {from:F3}s – {to:F3}s")));
        }

        foreach (var e in report.Events)
        {
            if (e.Type is not ("all_nm_silent" or "bus_wide_silence")) continue;

            // 用闭开区间 [Start, End)：断帧起点恰好等于 episode 首帧时刻时也
            // 属于 episode 内部，只留标记，不得派生与该空档无关的尾段静默带。
            if (report.Episodes.Any(ep => ep.Start <= e.T && e.T < ep.End))
            {
                var kind = e.Type == "bus_wide_silence"
                    ? NmTimelineKinds.BusGap
                    : NmTimelineKinds.Gap;
                markers.Add(new NmTimelineMarkerVm(
                    ToX(e.T, report.Window), kind, e.T,
                    string.Create(CultureInfo.InvariantCulture,
                        $"{NmTimelineText.EventType(e.Type)}  起点 {e.T:F3}s  {e.Detail}")));
                continue;
            }

            var end = report.Episodes
                .Where(ep => ep.Start > e.T)
                .Select(ep => ep.Start)
                .DefaultIfEmpty(report.Window.End)
                .Min();
            if (end <= e.T) end = report.Window.End;

            // all_nm_silent 契约上只出现在 episode 间隙/尾部；减法作为防御，
            // 保证任何未来事件类型都不会把静默带画到活动段上。
            foreach (var (pieceStart, pieceEnd) in SubtractEpisodes(e.T, end, report.Episodes))
            {
                silenceBands.Add((pieceStart, pieceEnd, string.Create(CultureInfo.InvariantCulture,
                    $"无 NM 报文  {pieceStart:F3}s – {pieceEnd:F3}s")));
            }
        }

        foreach (var (start, end, tooltip) in MergeSilenceBands(silenceBands))
        {
            var left = ToX(start, report.Window);
            var right = ToX(end, report.Window);
            segments.Add(new NmTimelineSegmentVm(
                left, Math.Max(right - left, 1), NmTimelineKinds.Silence, start, end, tooltip));
        }

        if (segments.Count == 0 && markers.Count == 0) return null;
        return new NmTimelineRowVm("整网状态", segments, markers);
    }

    /// <summary>区间减去 episode 覆盖后的剩余片段（静默带不能压在活动段上）。</summary>
    internal static IReadOnlyList<(double Start, double End)> SubtractEpisodes(
        double start, double end, IReadOnlyList<NmEpisode> episodes)
    {
        var pieces = new List<(double Start, double End)> { (start, end) };
        foreach (var episode in episodes)
        {
            if (episode.End <= start || episode.Start >= end) continue;

            var next = new List<(double Start, double End)>(pieces.Count + 1);
            foreach (var (pieceStart, pieceEnd) in pieces)
            {
                if (episode.End <= pieceStart || episode.Start >= pieceEnd)
                {
                    next.Add((pieceStart, pieceEnd));
                    continue;
                }
                if (pieceStart < episode.Start) next.Add((pieceStart, episode.Start));
                if (episode.End < pieceEnd) next.Add((episode.End, pieceEnd));
            }
            pieces = next;
        }

        return pieces.Where(p => p.End - p.Start > 1e-9).ToArray();
    }

    /// <summary>
    /// 按起点排序并合并重叠/首尾相接的静默带，保留起点更早那条的说明。
    /// 头部边界带（「记录边界」）与事件带可能覆盖同一段；不合并会被半透明灰
    /// 叠加成更深的色块，并产生两个可点击的重复区间。
    /// </summary>
    internal static IReadOnlyList<(double Start, double End, string Tooltip)> MergeSilenceBands(
        IEnumerable<(double Start, double End, string Tooltip)> bands)
    {
        var merged = new List<(double Start, double End, string Tooltip)>();
        foreach (var band in bands.OrderBy(b => b.Start).ThenBy(b => b.End))
        {
            if (merged.Count == 0 || band.Start > merged[^1].End + 1e-9)
            {
                merged.Add(band);
                continue;
            }

            var last = merged[^1];
            merged[^1] = (last.Start, Math.Max(last.End, band.End), last.Tooltip);
        }
        return merged;
    }

    private static NmTimelineMarkerVm BoundaryMarker(double t, NmWindow window, string tooltip)
        => new(ToX(t, window), NmTimelineKinds.Boundary, t, tooltip);

    /// <summary>能力 chip 的中文短名（spec §9.1）；枚举名只用于数据契约。</summary>
    public static string CapabilityShortName(NmCapability capability) => capability switch
    {
        NmCapability.IdentifyMessages => "识别",
        NmCapability.Timing => "周期",
        NmCapability.Activity => "活动",
        NmCapability.RawContent => "原始内容",
        NmCapability.SignalDecode => "信号解码",
        NmCapability.NidCheck => "NID",
        NmCapability.CbvDecode => "CBV",
        NmCapability.PnCheck => "PN",
        NmCapability.UserData => "用户数据",
        NmCapability.OemRules => "企标",
        _ => capability.ToString(),
    };

    public static IReadOnlyList<NmCapabilityChipVm> BuildCapabilityChips(NmReport report)
        => report.Capabilities.Select(c => new NmCapabilityChipVm(
            CapabilityShortName(c.Capability), c.Available,
            c.Available ? "可用" : string.Join("; ", c.Missing.Select(m => m.Why)))).ToArray();

    /// <summary>
    /// Profile 条「识别依据」：把每条 NM 报文的 Reasons 汇总成
    /// “MsgType=CANNM ×2 / 用户勾选 ×1”（spec §9.1）。
    /// </summary>
    public static string BuildReasonsSummary(IReadOnlyList<NmMessageDef> messages)
    {
        var counts = messages
            .Where(m => m.IsNm)
            .SelectMany(m => m.Reasons)
            .GroupBy(ReasonLabel, StringComparer.Ordinal)
            .Select(g => $"{g.Key} ×{g.Count()}")
            .ToArray();
        return counts.Length == 0 ? "—" : string.Join(" / ", counts);
    }

    private static string ReasonLabel(string reason)
        => string.Equals(reason, "UserSelected", StringComparison.Ordinal) ? "用户勾选" : reason;

    /// <summary>
    /// 窗口内观测到多少条已识别 NM 报文、哪些没出现。
    /// <para>
    /// 时间条只对 <see cref="NmReport.Messages"/> 建行（引擎对从未出现的报文
    /// 只产 NM_MESSAGE_ABSENT finding），摘要行把差额补上，避免「2 个节点却
    /// 只有 1 行」看起来像绘图丢失。
    /// </para>
    /// </summary>
    internal static string BuildObservedSummary(NmProfile profile, NmReport report)
    {
        var defs = profile.Messages.Where(m => m.IsNm).ToArray();
        if (defs.Length == 0) return "";

        var observed = report.Messages
            .Select(m => (m.CanIdHex, m.IsExtended))
            .ToHashSet();
        var present = defs.Count(d => observed.Contains((d.CanIdHex, d.IsExtended)));
        var missing = defs
            .Where(d => !observed.Contains((d.CanIdHex, d.IsExtended)))
            .Select(DescribeNmMessage)
            .ToArray();

        var tail = missing.Length == 0 ? " · 全部出现" : " · 未出现：" + string.Join("、", missing);
        return $"窗口内观测 {present}/{defs.Length} 条 NM 报文{tail}";
    }

    private static string DescribeNmMessage(NmMessageDef def)
    {
        var name = string.IsNullOrEmpty(def.Name) ? "" : " " + def.Name;
        return def.CanIdHex + name + (def.IsExtended ? "(ext)" : "");
    }


    private static double ToX(double t, NmWindow w)
    {
        var span = Math.Max(w.End - w.Start, 1e-9);
        return Math.Clamp((t - w.Start) / span * NmTimelineWidth, 0, NmTimelineWidth);
    }

    internal async Task RunNmAnalysisAsync((double Start, double End)? window)
    {
        // 旧请求只取消不 Dispose：取消令牌仍可能被在途的 Task.Run 使用。
        _nmCts?.Cancel();
        // spec §9.4：取消与超时都走同一条 partial 路径；用标志区分状态文案。
        _nmCancelledByUser = false;
        var cts = new CancellationTokenSource(NmAnalysisTimeout);
        _nmCts = cts;
        var ct = cts.Token;

        NmIsAnalyzing = true;
        NmCanCancel = true;
        // 进度文案由 XAML 按 NmIsAnalyzing 绑定（不确定进度条 + “分析中…（可取消）”）；
        // 这里清掉上一次状态文案，避免与进度提示叠加。NmIsPartial 等新结果应用时
        // 再改：中途失败/被替换时，屏幕上仍是最初那份（部分）报告，徽标要跟着它。
        NmStatus = "";
        try
        {
            var snapshot = await ((IChatToolContext)this).GetNmSnapshotAsync(new NmToolQuery(
                TStart: window?.Start, TEnd: window?.End,
                WindowRef: window is null ? "full" : "viewport"), ct);
            // 被更新一次分析替换（或窗口已关闭）时丢弃结果，避免旧报告覆盖新报告。
            if (IsCurrentNmRun(cts))
            {
                ApplyNmReport(snapshot);
                // 自动补全是模态对话框：先结束“分析中”状态，否则进度条/取消按钮
                // 会在弹窗背后一直挂着（modal loop 期间 finally 还不会执行）。
                NmIsAnalyzing = false;
                NmCanCancel = false;
                // spec §9.4：有全局 NM 属性但无 NM 标记时自动进补全（同一 DBC 只弹一次）。
                await TryAutoOpenNmWizardAsync(snapshot);
            }
        }
        catch (OperationCanceledException)
        {
            // 引擎内取消会返回 partial 报告（走 ApplyNmReport）；这里只剩 Task.Run
            // 在启动前被取消等外围取消。超时与分析超时都表现为 OCE，靠用户标志区分。
            if (IsCurrentNmRun(cts))
                NmStatus = _nmCancelledByUser ? "分析已取消" : "分析超时（已保留部分结果）";
        }
        catch (Exception ex)
        {
            if (IsCurrentNmRun(cts))
            {
                NmStatus = "分析失败：" + ex.Message;
                ClearNmReport();
            }
        }
        finally
        {
            // 只有当前分析才回写状态：被新一次分析替换的旧任务不得把按钮/状态改回去。
            if (IsCurrentNmRun(cts))
            {
                NmIsAnalyzing = false;
                NmCanCancel = false;
            }
        }
    }

    /// <summary>当前分析是否仍是最近一次（窗口 Dispose 后 _nmCts 置 null，一律不算）。</summary>
    private bool IsCurrentNmRun(CancellationTokenSource cts) => ReferenceEquals(_nmCts, cts);

    /// <summary>失败时清空旧报告，避免换 trace 后残留上一次的段/事件/能力/摘要。</summary>
    private void ClearNmReport()
    {
        _lastNmSnapshot = null;
        NmHasReport = false;
        NmIsPartial = false;
        NmPartialText = "";
        NmHasConflicts = false;
        NmProfileSummary = "";
        NmObservedSummary = "";
        NmTimeAxisStart = "";
        NmTimeAxisMid = "";
        NmTimeAxisEnd = "";
        NmHoverActive = false;
        NmHoverTimeText = "";
        NmHoverKind = "";
        NmHoverTitle = "";
        NmHoverRange = "";
        NmHoverHint = "";
        _nmWindowStart = 0;
        _nmWindowEnd = 0;
        NmMissingText = "补全";
        NmSemanticsBanner = "";
        NmHasSemanticsBanner = false;
        NmCapabilityChips.Clear();
        NmTimelineRows.Clear();
        NmEventRows.Clear();
        NmConflictLines.Clear();
        _allNmEventRows.Clear();
        NmEventFilterSummary = "";
        NmSelectedEvent = null;
    }

    internal void ApplyNmReport(NmToolSnapshot snapshot)
    {
        _lastNmSnapshot = snapshot;
        var report = snapshot.Report;
        var profile = snapshot.Profile;
        NmHasReport = true;
        NmIsPartial = report.IsPartial;
        NmPartialText = report.IsPartial
            ? (_nmCancelledByUser ? "部分结果（分析已取消）" : "部分结果（分析超时）")
            : "";
        NmProfileSummary = $"来源 {NmTimelineText.Provenance(profile.Provenance.ResolvedFrom)} · NM 报文 {profile.Messages.Count(m => m.IsNm)} · " +
            $"周期 {profile.Timings.CycleMs?.ToString(CultureInfo.InvariantCulture) ?? "—"}ms · 超时 {profile.Timings.TimeoutMs?.ToString(CultureInfo.InvariantCulture) ?? "—"}ms · " +
            $"识别依据 {BuildReasonsSummary(profile.Messages)}";
        NmMissingText = report.MissingInputs.Count == 0
            ? "输入齐备"
            : $"缺项 {report.MissingInputs.Count} → 补全";
        // 时间条只给「窗口内观测到」的报文建行；缺报文必须在同一区域说清楚，
        // 否则用户会以为面板漏画了节点。
        NmObservedSummary = BuildObservedSummary(profile, report);
        _nmWindowStart = report.Window.Start;
        _nmWindowEnd = report.Window.End;
        var mid = (report.Window.Start + report.Window.End) / 2;
        NmTimeAxisStart = string.Create(CultureInfo.InvariantCulture, $"{report.Window.Start:F2}s");
        NmTimeAxisMid = string.Create(CultureInfo.InvariantCulture, $"{mid:F2}s");
        NmTimeAxisEnd = string.Create(CultureInfo.InvariantCulture, $"{report.Window.End:F2}s");

        // spec §9.1：顶部黄色条展示 DBC/补全输入之间的配置冲突；无冲突隐藏。
        NmHasConflicts = profile.Conflicts.Count > 0;
        NmConflictLines.Clear();
        foreach (var c in profile.Conflicts)
        {
            NmConflictLines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{c.Field}: {string.Join(" / ", c.Values)}（来源 {string.Join(" / ", c.Sources)}）"));
        }

        NmCapabilityChips.Clear();
        foreach (var chip in BuildCapabilityChips(report)) NmCapabilityChips.Add(chip);

        // spec §9.1 的“语义未配置”横幅只在对应能力不可用时出现；
        // 用户补全 NID/CBV/PN/用户数据后，引擎已能产结论，不能再宣称不做判定。
        NmSemanticsBanner = BuildSemanticsBanner(report);
        NmHasSemanticsBanner = NmSemanticsBanner.Length > 0;
        NmTimelineRows.Clear();
        foreach (var row in BuildTimelineRows(report)) NmTimelineRows.Add(row);

        _allNmEventRows.Clear();
        foreach (var e in report.Events)
        {
            _allNmEventRows.Add(new NmEventRowVm(e.T, TraceTimeFormatter.Format(e.T, null), e.Type,
                e.Node ?? e.CanIdHex ?? "", e.Detail, e.EvidenceKind, e.EpisodeIndex, e.CanIdHex));
        }
        RefreshNmEventRows();

        NmStatus = ResolveStatus(snapshot, _nmCancelledByUser);
    }

    /// <summary>
    /// spec §9.1：不可用的语义能力才进横幅；全部可用时返回空串（横幅隐藏）。
    /// 只列 NID/CBV/PN/用户数据四类——识别/周期/内容属于基础能力。
    /// </summary>
    internal static string BuildSemanticsBanner(NmReport report)
    {
        var required = new[]
        {
            NmCapability.NidCheck, NmCapability.CbvDecode,
            NmCapability.PnCheck, NmCapability.UserData,
        };
        var unavailable = required
            .Where(cap => !report.Capabilities.Any(c => c.Capability == cap && c.Available))
            .Select(CapabilityShortName)
            .ToArray();

        return unavailable.Length == 0
            ? ""
            : $"语义未配置：{string.Join("/", unavailable)} 不做判定（可在「补全」中提供输入）";
    }

    /// <summary>
    /// 面板状态文案优先级：partial &gt; 无 DBC &gt; DBC 无 NM 配置 &gt; 窗口内无 NM &gt; 计数。
    /// 空态必须陈述事实，不推断睡眠/掉线（spec §9.4）。
    /// </summary>
    internal static string ResolveStatus(NmToolSnapshot snapshot, bool cancelledByUser = true)
    {
        var report = snapshot.Report;
        var profile = snapshot.Profile;
        if (report.IsPartial)
            return cancelledByUser ? "部分结果（分析已取消）" : "部分结果（分析超时）";
        if (report.MissingInputs.Any(m => string.Equals(m.InputKey, "dbc", StringComparison.Ordinal)))
            return "请先加载 DBC（NM 分析需要 DBC 约束）";
        if (report.Messages.Count == 0 && IsDbcWithoutNmConfig(profile))
            return "该 DBC 未包含 NM 配置";
        if (report.Messages.Count == 0)
        {
            if (profile.Source.HasFlag(NmProfileSource.Dbc) && HasGlobalNmAttributes(profile))
                return "DBC 有 NM 全局属性但未标记 NM 报文，请在「补全」中选择报文";
            return "本窗口未发现 NM 帧";
        }
        return report.Events.Count > 500
            ? $"事件 {report.Events.Count} 条（表格最多显示 500 条）"
            : $"事件 {report.Events.Count} 条";
    }

    /// <summary>筛选条件变化时重建显示集合；内存里保留全部事件，切回「全部」无需重算。</summary>
    private void RefreshNmEventRows()
    {
        var filtered = NmEventFilters.Filter(
            _allNmEventRows, NmSelectedEventTypeFilter, NmEventKeyword);
        NmEventRows.Clear();
        foreach (var row in filtered.Take(500))
        {
            NmEventRows.Add(row);
        }
        NmEventFilterSummary = string.Create(CultureInfo.InvariantCulture,
            $"显示 {Math.Min(filtered.Count, 500)} / 共 {_allNmEventRows.Count}");
    }

    partial void OnNmSelectedEventTypeFilterChanged(string value) => RefreshNmEventRows();

    partial void OnNmEventKeywordChanged(string value) => RefreshNmEventRows();

    /// <summary>profile 是否带任何全局 NM 时序属性（自动补全的前置条件）。</summary>
    internal static bool HasGlobalNmAttributes(NmProfile profile)
        => profile.Timings.CycleMs is not null
           || profile.Timings.TimeoutMs is not null
           || profile.Timings.RepeatMessageMs is not null
           || profile.Timings.WaitBusSleepMs is not null
           || profile.Timings.ImmediateCycleMs is not null
           || profile.Timings.ImmediateCount is not null;

    /// <summary>
    /// DBC 完全没有 NM 属性：无全局时序、无报文标记、无节点 NM 属性。
    /// 此时不猜 ID 区间（spec §9.4）。
    /// </summary>
    internal static bool IsDbcWithoutNmConfig(NmProfile profile)
        => profile.Source.HasFlag(NmProfileSource.Dbc)
           && !HasGlobalNmAttributes(profile)
           && !profile.Messages.Any(m => m.IsNm
               || m.Reasons.Any(r => !string.Equals(r, "UserSelected", StringComparison.Ordinal)))
           && !profile.Nodes.Any(n => n.IsNmNode
               || n.StationAddress is not null
               || n.NodeIdentifier is not null);

    /// <summary>
    /// spec §9.4：IdentifyMessages 缺项且有全局 NM 属性 → 自动打开补全。
    /// partial 报告与非 DBC 场景不触发。
    /// </summary>
    internal static bool ShouldAutoOpenNmWizard(NmToolSnapshot snapshot)
        => !snapshot.Report.IsPartial
           && !snapshot.Report.MissingInputs.Any(m =>
               string.Equals(m.InputKey, "dbc", StringComparison.Ordinal))
           && snapshot.Report.MissingInputs.Any(m =>
               string.Equals(m.InputKey, "nm_message_selection", StringComparison.Ordinal))
           && HasGlobalNmAttributes(snapshot.Profile);

    /// <summary>按 dbc_hash 去重：同一份 DBC 本次会话只自动弹一次。</summary>
    internal bool ClaimNmWizardAutoOpen(string? dbcHash)
    {
        if (string.IsNullOrWhiteSpace(dbcHash)
            || string.Equals(dbcHash, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(dbcHash, "unreadable", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        // "inline"（内存 DBC）也应参与去重：本次会话只自动弹一次。
        return _nmAutoWizardShown.Add(dbcHash);
    }

    private async Task TryAutoOpenNmWizardAsync(NmToolSnapshot snapshot)
    {
        if (!ShouldAutoOpenNmWizard(snapshot)) return;
        if (!ClaimNmWizardAutoOpen(snapshot.DbcHash)) return;
        try
        {
            await ShowNmWizardAsync(snapshot);
        }
        catch (Exception ex)
        {
            // 弹窗失败时回滚去重键：用户可能手动重试，或修好环境后再次分析。
            if (!string.IsNullOrEmpty(snapshot.DbcHash)) _nmAutoWizardShown.Remove(snapshot.DbcHash);
            // 自动弹窗失败不能静默：面板必须给出原因，用户仍可手动点「补全」。
            NmStatus = "自动打开补全失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private Task AnalyzeNmFullAsync() => RunNmAnalysisAsync(null);

    [RelayCommand]
    private Task AnalyzeNmWindowAsync()
    {
        var viewports = ChartViewModel.CaptureViewports();
        var vp = viewports.Count > 0 ? viewports[0] : null;
        return RunNmAnalysisAsync(vp is null ? null : (vp.XMin, vp.XMax));
    }

    /// <summary>导出 NM 报告：按用户选择的 Filter/扩展名写 Markdown 或 CSV。</summary>
    [RelayCommand]
    private async Task ExportNmReportAsync()
    {
        const string markdownExt = ".md";
        const string csvExt = ".csv";
        var owner = System.Windows.Application.Current?.MainWindow;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Markdown (*.md)|*.md|CSV (*.csv)|*.csv",
            FileName = "nm-report.md",
            DefaultExt = markdownExt,
            // 扩展名由下面的 FilterIndex 逻辑补齐；不要让系统一律补 .md。
            AddExtension = false,
        };
        var chosen = owner is null ? dlg.ShowDialog() : dlg.ShowDialog(owner);
        if (chosen != true) return;

        // FilterIndex=2（CSV）或用户手输了 .csv 都按 CSV 导出，否则 Markdown。
        var isCsv = dlg.FilterIndex == 2 ||
            dlg.FileName.EndsWith(csvExt, StringComparison.OrdinalIgnoreCase);
        var path = dlg.FileName;
        if (isCsv && path.EndsWith(markdownExt, StringComparison.OrdinalIgnoreCase))
        {
            // 用户选了 CSV 但文件名仍带 .md：改写扩展名，避免 CSV 正文写进 .md 文件。
            path = Path.ChangeExtension(path, csvExt);
        }
        else if (!Path.HasExtension(path))
        {
            path += isCsv ? csvExt : markdownExt;
        }

        // 扩展名改写后的最终路径没有经过 SaveFileDialog 的覆盖确认，必须再确认一次，
        // 否则会静默覆盖已有的 .csv/.md。
        if (!ConfirmNmExportOverwrite(path, dlg.FileName, owner)) return;

        // spec §9.3：导出前一次对话框询问 是/否/取消（是=脱敏，否=原样，取消=中止）；
        // 不再做会话级“只问一次”，每次导出都让用户明确选择。
        const string exportPrompt =
            "导出内容包含 DBC 路径、节点名与事件详情。\n\n" +
            "是：脱敏导出（节点名 → NODE_1..，DBC 报文名 → MSG_<CAN ID>，保留 CAN ID 与数字）\n" +
            "否：原样导出（包含真实节点/报文名）\n" +
            "取消：不导出";
        var answer = owner is null
            ? System.Windows.MessageBox.Show(exportPrompt, "导出选项",
                System.Windows.MessageBoxButton.YesNoCancel, System.Windows.MessageBoxImage.Question)
            : System.Windows.MessageBox.Show(owner, exportPrompt, "导出选项",
                System.Windows.MessageBoxButton.YesNoCancel, System.Windows.MessageBoxImage.Question);
        // 只接受明确的“是/否”；关闭窗口（None）与取消都视为中止，避免误按原样导出。
        if (answer is not (System.Windows.MessageBoxResult.Yes or System.Windows.MessageBoxResult.No))
            return;
        var redact = answer == System.Windows.MessageBoxResult.Yes;

        try
        {
            // 导出面板正在显示的那份报告；面板从未成功分析时才现取快照。
            // 不等待 _nmCts：即使面板分析被取消，导出本身仍应完成。
            // partial 报告也会原样进入导出（spec §9.4 保留部分结果）。
            var snapshot = _lastNmSnapshot;
            // 换 trace 后 _lastNmSnapshot 属于旧 source：不能把旧报告导出成新 trace 的结果。
            if (snapshot is not null && !string.IsNullOrEmpty(MasterSourceId)
                && !string.Equals(snapshot.SourceId, MasterSourceId, StringComparison.Ordinal))
            {
                snapshot = null;
            }
            snapshot ??= await ((IChatToolContext)this).GetNmSnapshotAsync(
                new NmToolQuery(), CancellationToken.None);
            var text = isCsv
                ? NmReportWriter.WriteCsv(snapshot.Report, redact)
                : NmReportWriter.WriteMarkdown(snapshot, redact);
            await File.WriteAllTextAsync(path, text, Encoding.UTF8);
            NmStatus = (redact ? "已脱敏导出：" : "已导出：") + path;
        }
        catch (Exception ex)
        {
            // 导出失败（无 trace、无写权限等）必须在面板上可见，禁止静默吞掉。
            NmStatus = "导出失败：" + ex.Message;
        }
    }

    /// <summary>扩展名改写后目标文件可能已存在；SaveFileDialog 没有为这个新路径确认过。</summary>
    private static bool ConfirmNmExportOverwrite(
        string path, string dialogPath, System.Windows.Window? owner)
    {
        if (string.Equals(path, dialogPath, StringComparison.OrdinalIgnoreCase)) return true;
        if (!File.Exists(path)) return true;
        const string prompt = "目标文件已存在，是否覆盖？\n\n";
        var answer = owner is null
            ? System.Windows.MessageBox.Show(prompt + path, "覆盖确认",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning)
            : System.Windows.MessageBox.Show(owner, prompt + path, "覆盖确认",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        return answer == System.Windows.MessageBoxResult.Yes;
    }

    /// <summary>打开 M1 补全对话框；关闭后清缓存并重算面板，让 sidecar 生效。</summary>
    [RelayCommand]
    private async Task OpenNmWizardAsync()
    {
        try
        {
            var snapshot = await ((IChatToolContext)this).GetNmSnapshotAsync(
                new NmToolQuery(), CancellationToken.None);
            await ShowNmWizardAsync(snapshot);
        }
        catch (Exception ex)
        {
            // 取快照失败（无 trace）或建窗失败都必须在面板上可见。
            NmStatus = "打开补全失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 创建并显示补全对话框；关闭后清缓存并重算面板。
    /// 手动按钮与 §9.4 自动弹窗共用这一条路径。
    /// </summary>
    private async Task ShowNmWizardAsync(NmToolSnapshot snapshot)
    {
        // 注入当前 DBC：试算需要信号解码与 DLC 口径，不能只靠 DbcPath 懒解析
        // （路径不可读或已换文件时试算会静默降级）。
        var vm = new NmInputWizardViewModel(snapshot, _dbcService.Current);
        var window = new Views.NmInputWizardWindow(vm)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        window.ShowDialog();
        // 用户可能在对话框里保存了 sidecar；无论是否保存都重算，
        // 保证面板展示的是当前输入下的结果。
        _nmCache.Invalidate();
        await RunNmAnalysisAsync(null);
    }

    /// <summary>取消面板分析；只取消 NM 通道，不影响聊天请求。</summary>
    [RelayCommand]
    private void CancelNmAnalysis()
    {
        _nmCancelledByUser = true;
        _nmCts?.Cancel();
    }

    [RelayCommand]
    private void SeekToNmEvent(NmEventRowVm? row)
    {
        if (row is not null) _masterService?.Seek(row.T);
    }

    [RelayCommand]
    private void SeekToNmTime(double t) => _masterService?.Seek(t);

    /// <summary>
    /// 时间条准星：设计坐标（0..1000）→ 当前窗口秒数文案。
    /// 报告窗口未知时不启动准星，避免显示凭空推算的时间。
    /// </summary>
    internal void SetNmHoverDesignX(double designX)
    {
        if (_nmWindowEnd <= _nmWindowStart) return;

        var t = _nmWindowStart
                + Math.Clamp(designX, 0, NmTimelineWidth) / NmTimelineWidth
                * (_nmWindowEnd - _nmWindowStart);
        NmHoverTimeText = string.Create(CultureInfo.InvariantCulture, $"{t:F2}s");
        NmHoverActive = true;
    }

    internal void ClearNmHoverTime()
    {
        NmHoverActive = false;
        NmHoverTimeText = "";
    }

    /// <summary>
    /// 活动段 hover 信息卡文案：阶段、报文/整网行、起止与持续时长。
    /// 只描述数据事实，不推断节点状态。
    /// </summary>
    internal void SetNmSegmentHover(string kind, string rowLabel, double start, double end)
    {
        var who = string.IsNullOrEmpty(rowLabel) ? "" : " · " + rowLabel;
        NmHoverKind = kind;
        NmHoverTitle = NmTimelineText.SegmentKind(kind) + who;
        NmHoverRange = string.Create(CultureInfo.InvariantCulture,
            $"{start:F2}s – {end:F2}s · 持续 {Math.Max(end - start, 0):F2}s");
        NmHoverHint = string.Create(CultureInfo.InvariantCulture, $"点击跳转到 {start:F2}s");
    }

    internal void ClearNmSegmentHover()
    {
        NmHoverKind = "";
        NmHoverTitle = "";
        NmHoverRange = "";
        NmHoverHint = "";
    }

    private bool CanInterpretNmEvent() => NmSelectedEvent is not null;

    /// <summary>
    /// spec §9.1「在 AI 中解读选中事件」：生成结构化提示并填入聊天输入框，
    /// 不自动发送——用户仍需在 AI 面板确认后手动发送。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInterpretNmEvent))]
    private void InterpretNmEventInAi()
    {
        if (NmSelectedEvent is null) return;
        PrefillChatInput(BuildNmEventAiPrompt(NmSelectedEvent, NmIsPartial));
    }

    /// <summary>把选中事件格式化为 AI 可读的结构化提示（纯函数，便于单测）。</summary>
    internal static string BuildNmEventAiPrompt(NmEventRowVm row, bool isPartial)
    {
        // spec §11：发给 LLM 的详情必须走与 get_nm_events 相同的脱敏边界，
        // 面板本地展示的原文不能直接进入提示（byte_change 等含原始字节值）。
        var detail = NmEventPrivacy.RedactDetail(row.Type, row.Detail);
        var sb = new StringBuilder();
        sb.AppendLine("请解读这条 NM 事件。只依据下列数据，明确区分实测事实/推断/不可知，不要臆造节点状态：");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 时间: {row.TimeText}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 报文 ID: {row.CanIdHex ?? "—"}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 对象: {row.Subject}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 类型: {row.Type}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 详情: {detail}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 证据级别: {row.EvidenceKind}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- 结果状态: {(isPartial ? "部分结果（分析已取消）" : "完整")}");
        return sb.ToString().TrimEnd();
    }
}
