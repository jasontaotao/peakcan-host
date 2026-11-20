using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.App.Services.Nm;
using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Core.Analysis.Nm;

namespace PeakCan.Host.App.ViewModels;

public sealed record NmCapabilityChipVm(string Name, bool Available, string Tooltip);
public sealed record NmTimelineSegmentVm(double Left, double Width, string Kind,
    double Start, double End, string Tooltip = "");
public sealed record NmTimelineRowVm(string Label, IReadOnlyList<NmTimelineSegmentVm> Segments);
public sealed record NmEventRowVm(double T, string TimeText, string Type, string Subject,
    string Detail, string EvidenceKind, int EpisodeIndex, string? CanIdHex = null);

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
    private const double NmTimelineWidth = 1000.0;

    [ObservableProperty] private bool _nmHasReport;
    [ObservableProperty] private bool _nmIsAnalyzing;
    [ObservableProperty] private bool _nmCanCancel;
    [ObservableProperty] private bool _nmIsPartial;
    [ObservableProperty] private bool _nmHasConflicts;
    [ObservableProperty] private string _nmStatus = "";
    [ObservableProperty] private string _nmProfileSummary = "";
    [ObservableProperty] private string _nmMissingText = "";
    [ObservableProperty] private string _nmSelectedEventTypeFilter = NmEventFilters.All;
    [ObservableProperty] private string _nmEventKeyword = "";
    /// <summary>筛选后的可见行数/总行数：避免状态栏的“共 N 条”被误读为表格行数。</summary>
    [ObservableProperty] private string _nmEventFilterSummary = "";

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
        => report.Messages.Select(m =>
        {
            var segments = m.Activity.Select(s =>
            {
                var left = ToX(s.Start, report.Window);
                var right = ToX(s.End, report.Window);
                var tooltip = string.Create(CultureInfo.InvariantCulture,
                    $"{s.Kind}  {s.Start:F3}s – {s.End:F3}s");
                return new NmTimelineSegmentVm(
                    left, Math.Max(right - left, 1), s.Kind, s.Start, s.End, tooltip);
            }).ToArray();
            // spec §9.1：时间条默认每个 NM 报文一行，行标签按 DBC 发送节点标注。
            // DBC 未给出 Name/Sender 时不渲染空括号内容，用 "—" 占位。
            var who = string.IsNullOrEmpty(m.Sender) ? "—" : m.Sender;
            var name = string.IsNullOrEmpty(m.Name) ? "" : " " + m.Name;
            return new NmTimelineRowVm($"{m.CanIdHex}{name} ({who})", segments);
        }).ToArray();

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


    private static double ToX(double t, NmWindow w)
    {
        var span = Math.Max(w.End - w.Start, 1e-9);
        return Math.Clamp((t - w.Start) / span * NmTimelineWidth, 0, NmTimelineWidth);
    }

    internal async Task RunNmAnalysisAsync((double Start, double End)? window)
    {
        // 旧请求只取消不 Dispose：取消令牌仍可能被在途的 Task.Run 使用。
        _nmCts?.Cancel();
        var cts = new CancellationTokenSource();
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
            // 在启动前被取消等外围取消。
            if (IsCurrentNmRun(cts)) NmStatus = "分析已取消";
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
        NmHasConflicts = false;
        NmProfileSummary = "";
        NmMissingText = "";
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
        NmProfileSummary = $"来源 {profile.Provenance.ResolvedFrom} · NM 报文 {profile.Messages.Count(m => m.IsNm)} · " +
            $"周期 {profile.Timings.CycleMs?.ToString(CultureInfo.InvariantCulture) ?? "—"}ms · 超时 {profile.Timings.TimeoutMs?.ToString(CultureInfo.InvariantCulture) ?? "—"}ms · " +
            $"识别依据 {BuildReasonsSummary(profile.Messages)}";
        NmMissingText = report.MissingInputs.Count == 0
            ? "输入齐备"
            : $"缺项 {report.MissingInputs.Count} → 补全";

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
        NmTimelineRows.Clear();
        foreach (var row in BuildTimelineRows(report)) NmTimelineRows.Add(row);

        _allNmEventRows.Clear();
        foreach (var e in report.Events)
        {
            _allNmEventRows.Add(new NmEventRowVm(e.T, TraceTimeFormatter.Format(e.T, null), e.Type,
                e.Node ?? e.CanIdHex ?? "", e.Detail, e.EvidenceKind, e.EpisodeIndex, e.CanIdHex));
        }
        RefreshNmEventRows();

        NmStatus = ResolveStatus(snapshot);
    }

    /// <summary>
    /// 面板状态文案优先级：partial &gt; 无 DBC &gt; DBC 无 NM 配置 &gt; 窗口内无 NM &gt; 计数。
    /// 空态必须陈述事实，不推断睡眠/掉线（spec §9.4）。
    /// </summary>
    private static string ResolveStatus(NmToolSnapshot snapshot)
    {
        var report = snapshot.Report;
        var profile = snapshot.Profile;
        if (report.IsPartial) return "部分结果（分析已取消）";
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
    private void CancelNmAnalysis() => _nmCts?.Cancel();

    [RelayCommand]
    private void SeekToNmEvent(NmEventRowVm? row)
    {
        if (row is not null) _masterService?.Seek(row.T);
    }

    [RelayCommand]
    private void SeekToNmTime(double t) => _masterService?.Seek(t);

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
