using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Services.Uds;
using PeakCan.Host.Core.Analysis.Uds;
using PeakCan.Host.Core.Uds.Database;
using PeakCan.HIL.Core;

namespace PeakCan.Host.App.ViewModels.Uds;

/// <summary>
/// 日志分析页的 ViewModel。职责边界：文件选择、地址输入、触发分析、展示不可变报告、
/// 导出与 AI Handoff。所有分析逻辑在 Core；这里不解析报文、不碰硬件。
/// <para>
/// 重新分析开始时旧报告立即置空（D11）：分析失败时绝不能让上一份报告继续显示，
/// 否则用户会把两次分析的结果混在一起。
/// </para>
/// </summary>
public sealed partial class UdsLogAnalysisViewModel : ObservableObject
{
    private const string TraceFilter = "ASC/BLF (*.asc;*.blf)|*.asc;*.blf|所有文件 (*.*)|*.*";

    private readonly IUdsOfflineTraceLoader _loader;
    private readonly UdsOfflineAnalyzer _analyzer;
    private readonly ITraceViewerChatHandoffService _handoff;
    private readonly IFileDialogService _dialogs;
    private readonly IUdsExportPrompt _exportPrompt;
    private readonly DidDatabase _dids;
    private readonly RoutineDatabase _routines;
    private readonly OdxImportViewModel _odxImport;
    private CancellationTokenSource? _cts;
    // 运行代号：被新分析/关窗取代的旧任务不得再写 Report/Status/IsBusy。
    private int _analysisGeneration;

    // 从在线配置继承来的地址快照。只有当前输入框仍等于它（= 用户没改过）时，
    // 在线配置的变更才会同步过来——否则会冲掉用户为另一台 ECU 手输的地址。
    private (string RequestId, string ResponseId, string FunctionalId, bool IsExtended)? _inheritedAddress;

    // 分析进行中被推迟的地址同步；分析结束补上，避免这次更新永久丢失。
    private PeakCan.Host.App.Services.Uds.UdsTransportSettings? _pendingAddressSync;

    /// <param name="settings">
    /// 可选的通讯参数存储：用它初始化地址输入框，并订阅变更。用户的痛点是
    /// 「每次分析要手敲地址」——在线页配过的地址这里应该直接继承。
    /// </param>
    public UdsLogAnalysisViewModel(
        IUdsOfflineTraceLoader loader,
        UdsOfflineAnalyzer analyzer,
        ITraceViewerChatHandoffService handoff,
        IFileDialogService dialogs,
        IUdsExportPrompt exportPrompt,
        DidDatabase dids,
        RoutineDatabase routines,
        OdxImportViewModel odxImport,
        PeakCan.Host.App.Services.Uds.UdsTransportSettingsStore? settings = null)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _handoff = handoff ?? throw new ArgumentNullException(nameof(handoff));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _exportPrompt = exportPrompt ?? throw new ArgumentNullException(nameof(exportPrompt));
        _dids = dids ?? throw new ArgumentNullException(nameof(dids));
        _routines = routines ?? throw new ArgumentNullException(nameof(routines));
        _odxImport = odxImport ?? throw new ArgumentNullException(nameof(odxImport));

        if (settings is not null)
        {
            // 用持久化配置初始化地址（覆盖字段初始化器里的 "7E0"/"7E8"）。
            ApplyAddressFromSettings(settings.Current);
            // 记住「继承来的值」：只有用户没改过时才跟着在线配置走。
            _inheritedAddress = (RequestIdText, ResponseIdText, FunctionalIdText, IsExtended);

            settings.Changed += saved =>
            {
                // 用户在离线页改过地址 → 他在分析另一台 ECU，不能被在线配置覆盖。
                var current = (RequestIdText, ResponseIdText, FunctionalIdText, IsExtended);
                if (current != _inheritedAddress)
                {
                    // 但继承基线要更新：用户之后点「重新载入」类操作时用的是新值。
                    _inheritedAddress = FormatAddress(saved);
                    return;
                }

                // 分析进行中不同步：换地址会让当前报告与输入框对不上。
                // 记下待同步，分析结束时补上（否则这次更新永久丢失）。
                if (IsBusy)
                {
                    _pendingAddressSync = saved;
                    return;
                }

                ApplyAddressFromSettings(saved);
                _inheritedAddress = FormatAddress(saved);
            };

            // 分析结束后补上被推迟的同步。
            PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(IsBusy) || IsBusy) return;
                if (_pendingAddressSync is not { } pending) return;
                _pendingAddressSync = null;

                var current = (RequestIdText, ResponseIdText, FunctionalIdText, IsExtended);
                if (current != _inheritedAddress) return; // 用户期间改过，尊重用户

                ApplyAddressFromSettings(pending);
                _inheritedAddress = FormatAddress(pending);
            };
        }

        // 离线页看不到在线输出日志，ODX 导入结果必须在这里单独可见。
        _odxImport.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(OdxImportViewModel.LastStatus) or nameof(OdxImportViewModel.HasSuccessfulImport))
            {
                OnPropertyChanged(nameof(OdxStatusSummary));
                OnPropertyChanged(nameof(OdxReloadHint));
            }
        };
    }

    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private string _requestIdText = "7E0";
    [ObservableProperty] private string _responseIdText = "7E8";
    /// <summary>
    /// 功能寻址（广播）CAN ID，如 <c>7DF</c>。留空 = 这份日志不按功能寻址解析。
    /// <para>
    /// <b>不配的代价：</b>测试仪用功能寻址发出的请求会被当背景流量丢掉，
    /// 而 ECU 的响应因为 ID 匹配被收下——报告里凭空多出「孤立响应」，且说不清来源。
    /// </para>
    /// </summary>
    [ObservableProperty] private string _functionalIdText = "";
    [ObservableProperty] private string _channelText = "1";
    [ObservableProperty] private bool _isExtended;
    [ObservableProperty] private string _status = "未选择文件";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isPartial;
    [ObservableProperty] private UdsOfflineReport? _report;
    [ObservableProperty] private UdsOfflineTransaction? _selectedTransaction;
    [ObservableProperty] private string _filterText = "";

    public ObservableCollection<UdsOfflineTransaction> VisibleTransactions { get; } = new();
    public ObservableCollection<UdsFrameEvidence> SelectedEvidence { get; } = new();

    public UdsNoiseSummary? Noise => Report?.Noise;
    public bool HasReport => Report is not null;
    public bool IsOdxLoaded => Report?.Diagnostics.OdxLoaded ?? false;

    /// <summary>当前定义来源（最近一次 ODX 导入状态）；离线页必须显式可见。</summary>
    public string OdxStatusSummary =>
        _odxImport.LastStatus
        ?? (_odxImport.HasSuccessfulImport ? "ODX 已加载" : "未加载 ODX");

    /// <summary>导入新 ODX 后提示重新分析；旧报告不会热更新。</summary>
    public string? OdxReloadHint =>
        _odxImport.HasSuccessfulImport ? "已加载 ODX，重新分析以应用新定义（当前报告不变）" : null;

    /// <summary>有报告、但当前筛选无可见行时与“无匹配事务”区分开。</summary>
    public string? FilterEmptyHint =>
        HasReport && Report!.Transactions.Count > 0 && VisibleTransactions.Count == 0
            ? "当前筛选没有可见事务（清空筛选可恢复）"
            : null;

    public bool IsFilterEmpty => FilterEmptyHint is not null;

    /// <summary>选中事务的结论状态：把 NRC / 未闭合 / 归属歧义说清楚，不只靠 FinalKind 枚举名。</summary>
    public string? SelectedConclusion => SelectedTransaction switch
    {
        null => null,
        { HasDuplicateFinalResponse: true } => "归属歧义：收到重复最终响应，不判确定成功",
        { IsAmbiguous: true } => "归属歧义：同 SID 请求重叠，配对仅供参考",
        { FinalKind: UdsFinalKind.None } => "未闭合：日志结束前未收到通过校验的响应",
        // 抑制了正响应的请求没有响应是规范要求，不能显示成「未闭合」——
        // 那会让用户以为 ECU 出了问题。
        { FinalKind: UdsFinalKind.Suppressed } => "响应已抑制（请求声明 suppressPosRspMsgIndicationBit，ECU 按协议不回正响应）",
        { FinalKind: UdsFinalKind.Negative } t => $"最终 NRC 0x{t.FinalNrc:X2}",
        _ => null,
    };

    // ── 右栏详情行 ────────────────────────────────────────────────────
    //
    // 每条都**自带标签**，且**值为空时返回 null**。
    //
    // 为什么不能在 XAML 里写 StringFormat='标签：{0}'：WPF 对 null 仍会应用格式串。
    // `最终 NRC：0x{0:X2}` 在正响应（FinalNrc == null）时渲染成「最终 NRC：0x」——
    // 用户会以为 NRC 是 0x00；其余字段则渲染成光秃秃的「DID：」，像加载失败。
    // 这些行用 NullToVisibility 控制显隐，null 时整行消失。

    public string? SelectedNrcLine => SelectedTransaction?.FinalNrc is { } nrc
        ? $"最终 NRC：0x{nrc:X2}" : null;

    public string? SelectedNrcNameLine => SelectedTransaction?.NrcName is { } name
        ? $"NRC 含义：{name}" : null;

    public string? SelectedPendingLine => SelectedTransaction is { PendingCount: > 0 } t
        ? $"0x78 pending 次数：{t.PendingCount}" : null;

    public string? SelectedDidNameLine => SelectedTransaction?.Interpretation?.DidName is { } v
        ? $"DID：{v}" : null;

    public string? SelectedDecodedValueLine => SelectedTransaction?.Interpretation?.DecodedValue is { } v
        ? $"解码值：{v}" : null;

    public string? SelectedDegradeReasonLine => SelectedTransaction?.Interpretation?.DegradeReason is { } v
        ? $"降级原因：{v}" : null;

    public string? SelectedDegradeDetailLine => SelectedTransaction?.Interpretation?.DegradeDetail is { } v
        ? $"降级详情：{v}" : null;

    // ── 结论摘要区（默认可见）────────────────────────────────────────────
    //
    // 用户的原话是「看不大出来问题分析」。让他自己去事务表里逐行拼，或者
    // 去点 AI，都不算解决。这里把 Core 的 finding 直接渲染成三行：
    // 会话 / 流程 / 独立异常。没有异常时也必须显示「未发现异常」而不是留空
    // ——留空会被读成「分析失败」。

    /// <summary>结论区是否可见：有报告（含部分报告，后者会额外显示不完整横幅）。</summary>
    public bool HasReportForConclusions => Report is not null;

    /// <summary>
    /// Expander 标题栏的一句话摘要。收起结论区后仍要能一眼看出「有没有问题」——
    /// 只写「结论」两个字的标题等于逼用户展开才知道要不要展开。
    /// </summary>
    public string ConclusionHeader => Report switch
    {
        null => "",
        { IsPartial: true } => "（分析未完成）",
        { Findings.Count: 0 } => "（未发现异常）",
        var r => $"（{r.Findings.Count} 项需关注）",
    };

    /// <summary>会话摘要：换了几次、最后确认停在哪个。</summary>
    public string SessionSummary
    {
        get
        {
            if (Report is not { } report) return "";

            // 诊断阶段没跑完时，「没有切换」是未知，不是事实。
            if (report.IsPartial) return "会话：分析未完成，暂无会话结论";

            var timeline = report.SessionTimeline;
            if (timeline.Count == 0) return "会话：日志中未出现会话切换";

            // 只统计「已确认」的切换。ECU 复位不是切换——把它算进去会让
            // 「切换 1 次 + 一次复位」显示成「切换 2 次」。
            var switches = timeline.Count(i => i.Observation == UdsSessionObservation.Confirmed);
            var last = timeline[^1];
            var lastText = last switch
            {
                { Observation: UdsSessionObservation.Confirmed, SessionType: { } s }
                    => $"最后确认处于{UdsDisplayText.Session(s)}会话",
                { Observation: UdsSessionObservation.ResetMayHaveChanged }
                    => "最后一次 ECU 复位后会话未知",
                { Observation: UdsSessionObservation.RequestedNotConfirmed }
                    => "最后一次会话切换未收到确认响应，ECU 会话状态未知",
                // 抑制了正响应的切换（10 83）：ECU 按协议不回是正确行为。
                // 落进下面的默认分支会被说成「日志开始时 ECU 已在某会话中」——
                // 而这条恰恰发生在日志中途，句子与事实反向。
                { Observation: UdsSessionObservation.RequestedResponseSuppressed, SessionType: { } s2 }
                    => $"最后一次会话切换请求了 {UdsDisplayText.Session(s2)} 会话但抑制了正响应，" +
                       "ECU 按协议未回，会话很可能已切换但没有响应证据",
                // 默认分支必须<b>限定为「日志起点未知」</b>。原来它是万能兜底，
                // 任何新增的观测值都会落进来被说成「日志开始时 ECU 已在某会话中」——
                // 本轮修的那个 bug 就是这么产生的。宁可说「未知」也不要说错。
                { Observation: UdsSessionObservation.UnknownAtLogStart }
                    => "日志开始时 ECU 已在某会话中，具体是哪个无法从日志确定",
                _ => "最后一次会话状态无法从日志确定",
            };

            return switches == 0
                ? $"会话：{lastText}"
                : $"会话：切换 {switches} 次，{lastText}";
        }
    }

    /// <summary>刷写流程摘要；非刷写日志返回 null（UI 据此隐藏该行）。</summary>
    public string? FlashSummary
    {
        get
        {
            if (Report?.FlashFlow is not { } flow) return null;
            if (flow.Completed) return "流程：刷写已完成（0x37 成功，且之后有校验或复位）";

            var blocking = flow.BlockingStage is { } b ? b.ToString() : "未知步骤";
            var blocks = flow.TransferBlockCount > 0
                ? $"（已传 {flow.TransferBlockCount} 块）"
                : "";
            return $"流程：刷写未完成 — 卡在 {blocking}{blocks}";
        }
    }

    /// <summary>
    /// 结论列表。<b>可能有多条互不相关</b>——刻意不做单一根因链，
    /// 见 2026-11-26 设计文档 §2.3。
    /// </summary>
    public IReadOnlyList<UdsFindingRow> FindingRows =>
        Report is null ? Array.Empty<UdsFindingRow>()
            : Report.Findings.Select(UdsFindingRow.From).ToArray();

    /// <summary>
    /// 选中事务发生时的会话。用户问「会话模式」时，光有摘要行不够——
    /// 他还要知道「这条 0x2E 是在编程会话下发的，还是在退回默认之后」。
    /// 「未知」是真实答案：日志通常从会话中途开始，不能编造「默认会话」。
    /// </summary>
    public string? SelectedSessionAtTime => SessionAt(SelectedTransaction);

    private string? SessionAt(UdsOfflineTransaction? transaction)
    {
        if (Report is null || transaction is null) return null;

        var t = transaction.FirstTimestamp;
        var interval = Report.SessionTimeline
            .LastOrDefault(i => i.StartTimestamp <= t
                                && (i.EndTimestamp is null || t < i.EndTimestamp));

        if (interval?.SessionType is not { } s) return "未知";

        // 抑制正响应的切换：会话类型是<b>请求的</b>值，ECU 没确认过。
        // 不带后缀会与结论摘要里「很可能已切换但没有响应证据」互相矛盾——
        // 同一份报告对同一事实不能给两种确定性。
        return interval.Observation == UdsSessionObservation.RequestedResponseSuppressed
            ? $"{UdsDisplayText.Session(s)}（未确认）"
            : UdsDisplayText.Session(s);
    }

    /// <summary>
    /// 无 finding 时的占位文案：不能留空，留空会被读成「分析失败」。
    /// <para>
    /// <b>部分报告绝不能返回「未发现异常」</b>：那时 <c>Findings</c> 为空是因为
    /// <c>diagnose</c> 没跑完，不是因为没有异常。把它渲染成绿色的「未发现异常」
    /// 是伪造事实，恰好违反设计文档验收第 10 条的初衷。
    /// </para>
    /// </summary>
    public string? NoFindingsHint => Report switch
    {
        null => null,
        { IsPartial: true } => null,
        { Findings.Count: 0 } => "未发现异常",
        _ => null,
    };

    /// <summary>报告不完整时的醒目提示（此前只藏在 Status 文案里）。</summary>
    public string? PartialBanner => Report is { IsPartial: true } r
        ? $"报告不完整：{r.PartialReason ?? "原因未记录"} — 以下结论仅基于已完成阶段"
        : null;

    /// <summary>P2 / P2* 阈值行；此前只在导出文件里有。</summary>
    public string TimingSummary => Report?.Timing is { } t
        ? $"P2={t.P2Ms} ms，P2*={t.P2StarMs} ms（来源：{t.Source}）"
        : "未取得 ECU 时序（P2/P2* 未知）";

    /// <summary>
    /// 回显校验失败明细。改前报告只有「响应校验失败 N」这个总数，
    /// 用户知道有几帧没过校验，但不知道失败在哪一帧、哪个字段。
    /// </summary>
    public IReadOnlyList<ValidationFailureRow> ValidationFailureRows =>
        Report is null
            ? Array.Empty<ValidationFailureRow>()
            : Report.ValidationFailures
                .Select(f => new ValidationFailureRow(f.FailedCheck, f.ResponseEvidenceIndex))
                .ToArray();

    public bool HasValidationFailures => ValidationFailureRows.Count > 0;

    /// <summary>
    /// 孤立响应明细。spec §6.5 / §12 要求「单列计数和证据」——
    /// 只有计数时，用户无法判断这 3 条是集中在某一刻（像并行诊断源）
    /// 还是散布全程（更像地址配错）。
    /// </summary>
    public IReadOnlyList<PduEvidenceRow> OrphanResponseRows =>
        Report is null
            ? Array.Empty<PduEvidenceRow>()
            : Report.OrphanResponses.Select(PduEvidenceRow.From).ToArray();

    public bool HasOrphanResponses => OrphanResponseRows.Count > 0;

    /// <summary>重复最终响应明细（同一请求收到的第二个最终响应）。</summary>
    public IReadOnlyList<PduEvidenceRow> DuplicateFinalResponseRows =>
        Report is null
            ? Array.Empty<PduEvidenceRow>()
            : Report.DuplicateFinalResponsePdus.Select(PduEvidenceRow.From).ToArray();

    public bool HasDuplicateFinalResponses => DuplicateFinalResponseRows.Count > 0;

    /// <summary>
    /// 传输警告，<b>按原因聚合</b>。
    /// <para>
    /// 逐帧列出是错的：一次刷写中断会产生几十条 <c>OrphanConsecutiveFrame</c>，
    /// 它们说明的是同一件事。几十行相同文字把结论稀释成噪声，用户反而看不出
    /// 「多帧传输在哪个区间断了」。
    /// </para>
    /// </summary>
    public IReadOnlyList<WarningSummaryRow> WarningRows =>
        Report is null
            ? Array.Empty<WarningSummaryRow>()
            : Report.Warnings
                .GroupBy(w => w.Reason)
                .Select(g => new WarningSummaryRow(
                    g.Key, g.Count(),
                    g.Min(w => w.EvidenceIndex),
                    g.Max(w => w.EvidenceIndex)))
                .OrderByDescending(r => r.Count)
                .ThenBy(r => r.Reason, StringComparer.Ordinal)
                .ToArray();

    public bool HasWarnings => WarningRows.Count > 0;

    // ── 背景流量 ──────────────────────────────────────────────────────
    //
    // 原来的「噪声（按 CAN ID）」列 20 个 ID × 帧数，说明不了任何事：
    // 那些是其它 ECU 的正常周期广播。现在给的是「多忙、什么规律」，
    // 并把偶发流量排到前面——它比周期报文更可能和故障相关。

    public UdsBusTrafficProfile? BusTraffic => Report?.BusTraffic;

    /// <summary>一行说清总线有多忙。</summary>
    public string BusTrafficSummary
    {
        get
        {
            if (BusTraffic is not { TotalFrames: > 0 } p) return "";

            var parts = new List<string>
            {
                $"其它 ECU 报文 {p.TotalFrames} 帧 / {p.DistinctIds} 个 ID",
                $"平均 {p.FramesPerSecond:F0} 帧/秒",
            };
            if (p.PeriodicCount > 0) parts.Add($"周期 {p.PeriodicCount}");
            if (p.SporadicCount > 0) parts.Add($"偶发 {p.SporadicCount}");
            if (p.SingleShotCount > 0) parts.Add($"零星 {p.SingleShotCount}");

            return string.Join("，", parts);
        }
    }

    /// <summary>
    /// 总线级静默窗口。<b>刷写期间其它 ECU 报文集体消失</b>正是用户要看的现象，
    /// 不能散在各 ID 的首末时间里让人自己去推。
    /// </summary>
    public string BusSilenceSummary
    {
        get
        {
            if (BusTraffic is not { } p || !p.HasSilence) return "";

            var windows = p.SilenceWindows.Select(w => string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{w.Start:F2}–{w.End:F2}s"));
            var joined = string.Join("，", windows);

            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"总线静默 {p.SilenceWindows.Count} 段，共 {p.TotalSilenceSeconds:F1}s：{joined}");
        }
    }

    public bool HasBusSilence => BusTraffic is { HasSilence: true };

    /// <summary>背景流量列表；偶发排前面。</summary>
    public IReadOnlyList<BusTrafficRow> BusTrafficRows =>
        BusTraffic is not { } p
            ? Array.Empty<BusTrafficRow>()
            : p.Ids.Select(BusTrafficRow.From).ToArray();

    public bool HasBusTraffic => BusTrafficRows.Count > 0;

    /// <summary>
    /// 地址匹配但无法使用的帧（坏 PCI、孤立 CF、序号错、校验失败）。
    /// <b>这才是「噪声」</b>，和背景流量分开显示。
    /// </summary>
    public IReadOnlyList<UdsNoiseReasonCount> ProblemFrameReasons =>
        Report?.Noise.Reasons ?? Array.Empty<UdsNoiseReasonCount>();

    public bool HasProblemFrames => ProblemFrameReasons.Count > 0;

    [RelayCommand]
    private async Task OpenLogAsync()
    {
        var path = _dialogs.ShowOpenDialog(TraceFilter);
        if (path is null) return;
        FilePath = path;
        await AnalyzeAsync();
    }

    [RelayCommand]
    private Task Analyze() => AnalyzeAsync();

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void InterpretSelected()
    {
        if (Report is null) return;
        _handoff.Handoff(UdsOfflineAiPromptBuilder.Build(Report, SelectedTransaction));
    }

    [RelayCommand]
    private Task Export() => ExportAsync();

    public async Task AnalyzeAsync(CancellationToken externalToken = default)
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            Status = "未选择文件";
            return;
        }

        _cts?.Cancel();
        var generation = ++_analysisGeneration;
        Report = null;
        VisibleTransactions.Clear();
        SelectedEvidence.Clear();
        IsBusy = true;
        IsPartial = false;
        Status = "分析中…";

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        _cts = cts;
        try
        {
            var path = FilePath;
            var address = ParseAddress();
            Status = "读取文件中…";
            var frames = await _loader.LoadAsync(path, cts.Token).ConfigureAwait(true);
            if (generation != _analysisGeneration) return;
            Status = "分析中…";
            var request = new UdsOfflineAnalysisRequest(
                Guid.NewGuid(),
                Path.GetFileName(path),
                path,
                frames.Frames,
                address,
                SnapshotDiagnostics(),
                EngineVersion());

            // 不在 Task.Run 上挂 token：token 已取消时也要让分析器产出部分报告，
            // 而不是直接 TaskCanceledException 丢失已完成阶段。
            var report = await Task.Run(() => _analyzer.Analyze(request, cts.Token))
                .ConfigureAwait(true);

            // 已被更新的分析取代：这次结果作废，不得覆盖新报告/状态。
            if (generation != _analysisGeneration) return;

            Report = report;
            IsPartial = report.IsPartial;
            RefreshVisible();
            Status = report.IsPartial
                ? $"部分报告：{report.PartialReason}"
                : report.Transactions.Count == 0
                    ? (report.Counts.Input == 0
                        ? "日志为空：没有任何 CAN 帧"
                        : "当前设置下未识别到诊断事务")
                    : $"分析完成：{report.Transactions.Count} 个事务";
        }
        catch (OperationCanceledException)
        {
            if (generation == _analysisGeneration) Status = "已取消";
        }
        catch (UdsOfflineLoadException ex)
        {
            if (generation == _analysisGeneration) Status = "读取失败：" + ex.Message;
        }
        catch (FormatException)
        {
            if (generation == _analysisGeneration)
                // 必须点名功能 ID：用户可能只填了它，提示里不提就会以为改的是别的框。
                Status = "地址格式错误：请用十六进制填写通道 / 请求 ID / 响应 ID / 功能 ID";
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            if (generation == _analysisGeneration) IsBusy = false;
        }
    }

    public async Task ExportAsync()
    {
        if (Report is null)
        {
            Status = "没有可导出的报告";
            return;
        }

        var path = _exportPrompt.ChooseSavePath(out var isCsv);
        if (path is null) return;
        if (File.Exists(path) && !_exportPrompt.ConfirmOverwrite(path)) return;

        var choice = _exportPrompt.AskRedaction();
        if (choice == UdsExportChoice.Cancel) return;

        try
        {
            var redact = choice == UdsExportChoice.Redacted;
            var text = isCsv
                ? UdsOfflineReportWriter.WriteCsv(Report, redact)
                : UdsOfflineReportWriter.WriteMarkdown(Report, redact);
            await File.WriteAllTextAsync(path, text, Encoding.UTF8).ConfigureAwait(true);
            Status = (redact ? "已脱敏导出：" : "已导出：") + path;
        }
        catch (Exception ex)
        {
            Status = "导出失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 关窗路径（UdsWindow.Unloaded 调用）：取消在途分析并丢弃报告。
    /// 报告只在窗口存续期间有效（spec §11.3）；重开窗口必须是新的空状态。
    /// 不弹任何对话框。
    /// </summary>
    public void StopForWindowClose()
    {
        _analysisGeneration++;   // 让在途任务的完成回调全部作废
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        Report = null;
        VisibleTransactions.Clear();
        SelectedEvidence.Clear();
        SelectedTransaction = null;
        IsPartial = false;
        IsBusy = false;
        FilterText = "";
        Status = "未选择文件";
    }

    partial void OnReportChanged(UdsOfflineReport? value)
    {
        OnPropertyChanged(nameof(Noise));
        OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(IsOdxLoaded));
        OnPropertyChanged(nameof(FilterEmptyHint));
        OnPropertyChanged(nameof(IsFilterEmpty));

        // 结论摘要区：报告换了，所有派生属性都要通知。
        OnPropertyChanged(nameof(HasReportForConclusions));
        OnPropertyChanged(nameof(ConclusionHeader));
        OnPropertyChanged(nameof(SessionSummary));
        OnPropertyChanged(nameof(FlashSummary));
        OnPropertyChanged(nameof(FindingRows));
        OnPropertyChanged(nameof(NoFindingsHint));
        OnPropertyChanged(nameof(PartialBanner));
        OnPropertyChanged(nameof(TimingSummary));
        OnPropertyChanged(nameof(ValidationFailureRows));
        OnPropertyChanged(nameof(HasValidationFailures));
        OnPropertyChanged(nameof(OrphanResponseRows));
        OnPropertyChanged(nameof(HasOrphanResponses));
        OnPropertyChanged(nameof(DuplicateFinalResponseRows));
        OnPropertyChanged(nameof(HasDuplicateFinalResponses));
        OnPropertyChanged(nameof(WarningRows));
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(BusTraffic));
        OnPropertyChanged(nameof(BusTrafficSummary));
        OnPropertyChanged(nameof(BusSilenceSummary));
        OnPropertyChanged(nameof(HasBusSilence));
        OnPropertyChanged(nameof(BusTrafficRows));
        OnPropertyChanged(nameof(HasBusTraffic));
        OnPropertyChanged(nameof(ProblemFrameReasons));
        OnPropertyChanged(nameof(HasProblemFrames));
        // 报告换了，选中事务的「发生时会话」也要按新报告重算。
        OnPropertyChanged(nameof(SelectedSessionAtTime));
    }

    partial void OnFilterTextChanged(string value) => RefreshVisible();

    partial void OnSelectedTransactionChanged(UdsOfflineTransaction? value)
    {
        RefreshSelectedEvidence();
        OnPropertyChanged(nameof(SelectedConclusion));
        OnPropertyChanged(nameof(SelectedSessionAtTime));
        // 右栏详情行。漏一条就会显示上一条事务的值——比空白更危险。
        OnPropertyChanged(nameof(SelectedNrcLine));
        OnPropertyChanged(nameof(SelectedNrcNameLine));
        OnPropertyChanged(nameof(SelectedPendingLine));
        OnPropertyChanged(nameof(SelectedDidNameLine));
        OnPropertyChanged(nameof(SelectedDecodedValueLine));
        OnPropertyChanged(nameof(SelectedDegradeReasonLine));
        OnPropertyChanged(nameof(SelectedDegradeDetailLine));
    }

    private void RefreshSelectedEvidence()
    {
        SelectedEvidence.Clear();
        if (Report is null || SelectedTransaction is null) return;

        var indices = SelectedTransaction.Request.Evidence
            .Concat(SelectedTransaction.Responses.SelectMany(r => r.Evidence))
            .ToHashSet();
        foreach (var evidence in Report.Evidence.Where(e => indices.Contains(e.Index)))
            SelectedEvidence.Add(evidence);
    }

    private void RefreshVisible()
    {
        VisibleTransactions.Clear();
        if (Report is null) return;

        foreach (var transaction in Report.Transactions)
        {
            if (FilterText.Length == 0 || MatchesFilter(transaction))
                VisibleTransactions.Add(transaction);
        }

        OnPropertyChanged(nameof(FilterEmptyHint));
        OnPropertyChanged(nameof(IsFilterEmpty));
    }

    /// <summary>
    /// 筛选判据：按<b>表格里看得见的文字</b>匹配，同时保留枚举名。
    /// <para>
    /// 原来只匹配 <c>FinalKind.ToString()</c>（Positive/Negative）和裸 hex（7F）。
    /// 但结果列显示的是中文「正响应」，NRC 列显示 <c>0x22</c>——用户照着表里的值
    /// 输入会得到空表，看起来像筛选坏了。现在两种写法都能命中：
    /// 显示文本（含 NRC 含义列）优先，枚举名与裸 hex 作为兼容保留。
    /// </para>
    /// </summary>
    private bool MatchesFilter(UdsOfflineTransaction transaction)
    {
        bool Contains(string? text) =>
            !string.IsNullOrEmpty(text)
            && text.Contains(FilterText, StringComparison.OrdinalIgnoreCase);

        if (Contains(transaction.Interpretation?.ServiceDisplay)) return true;
        if (Contains(transaction.FinalKindDisplay)) return true;
        if (Contains(transaction.FinalKind.ToString())) return true;
        if (Contains(transaction.NrcName)) return true;

        if (transaction.FinalNrc is { } nrc)
        {
            var hex = nrc.ToString("X2", CultureInfo.InvariantCulture);
            if (Contains(hex) || Contains("0x" + hex)) return true;
        }

        return false;
    }

    private UdsOfflineDiagnostics SnapshotDiagnostics() => new(
        _dids.All.ToArray(),
        _routines.All.ToArray(),
        _odxImport.HasSuccessfulImport,
        _dids.UserJsonLoadStatus,
        _routines.UserJsonLoadStatus,
        SnapshotId: $"{_dids.UserJsonLoadStatus}/{_routines.UserJsonLoadStatus}");

    private static string EngineVersion()
        => typeof(UdsLogAnalysisViewModel).Assembly.GetName().Version?.ToString() ?? "unknown";

    /// <summary>
    /// 用持久化的通讯参数填充地址输入框。
    /// <para>
    /// <b>刻意只读不写：</b>离线页的地址只是「这台 ECU 的地址是什么」，用户在这里
    /// 临时改成别的 ECU 时不应污染在线诊断的地址配置。
    /// </para>
    /// </summary>
    private void ApplyAddressFromSettings(PeakCan.Host.App.Services.Uds.UdsTransportSettings settings)
    {
        var (req, resp, fid, ext) = FormatAddress(settings);
        RequestIdText = req;
        ResponseIdText = resp;
        FunctionalIdText = fid;
        IsExtended = ext;
    }

    private static (string RequestId, string ResponseId, string FunctionalId, bool IsExtended) FormatAddress(
        PeakCan.Host.App.Services.Uds.UdsTransportSettings settings)
        => (settings.RequestId.ToString("X", CultureInfo.InvariantCulture),
            settings.ResponseId.ToString("X", CultureInfo.InvariantCulture),
            settings.FunctionalId?.ToString("X", CultureInfo.InvariantCulture) ?? "",
            settings.IsExtendedFrame);

    private UdsOfflineAddress ParseAddress()
    {
        static uint ParseHex(string text)
        {
            var cleaned = text.Replace("0x", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (!uint.TryParse(cleaned, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                throw new FormatException($"不是合法的十六进制值：{text}");
            return value;
        }

        if (!ushort.TryParse(ChannelText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel))
            throw new FormatException($"不是合法的通道号：{ChannelText}");

        // 功能寻址留空 = 不解析。填了就必须是合法 hex，不能静默当成没填——
        // 那样用户以为配上了，报告却继续把广播请求当背景流量。
        uint? functional = null;
        if (!string.IsNullOrWhiteSpace(FunctionalIdText))
            functional = ParseHex(FunctionalIdText);

        return new UdsOfflineAddress(
            channel, ParseHex(RequestIdText), ParseHex(ResponseIdText), IsExtended, functional);
    }
}
