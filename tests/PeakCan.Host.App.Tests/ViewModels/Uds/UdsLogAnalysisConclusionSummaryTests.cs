using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Uds.Odx;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Uds;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.Core.Analysis.Uds;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Uds.Database;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Uds;

/// <summary>
/// 结论摘要区的文案。
/// <para>
/// <b>最重要的一条：部分报告不能显示「未发现异常」。</b>
/// 部分报告的 <c>Findings</c> 为空是因为 <c>diagnose</c> 没跑完，
/// 不是因为没有异常。把它渲染成绿色的「未发现异常」是伪造事实。
/// </para>
/// </summary>
public sealed class UdsLogAnalysisConclusionSummaryTests
{
    private sealed class StubLoader(ReplayFrame[] frames) : IUdsOfflineTraceLoader
    {
        public Task<UdsOfflineTraceLoadResult> LoadAsync(string path, CancellationToken ct = default)
            => Task.FromResult(new UdsOfflineTraceLoadResult(frames, "ASC"));
    }

    private sealed class NoopHandoff : ITraceViewerChatHandoffService
    {
        public void Handoff(UdsChatHandoffPayload payload) { }
    }

    private sealed class NoopDialogs : IFileDialogService
    {
        public string? ShowOpenDialog(string filter) => null;
        public string? ShowSaveDialog(string filter, string? defaultExt, string? initialDirectory) => null;
    }

    private sealed class NoopExport : IUdsExportPrompt
    {
        public string? ChooseSavePath(out bool isCsv)
        {
            isCsv = false;
            return null;
        }

        public bool ConfirmOverwrite(string path) => true;
        public UdsExportChoice AskRedaction() => UdsExportChoice.Cancel;
    }

    private sealed class NoopOdxService : IOdxImportService
    {
        public Task<OdxImportResult> ImportAsync(string path, CancellationToken ct = default)
            => Task.FromResult(OdxImportResult.Ok(0, 0, 0, Array.Empty<string>()));
    }

    private static UdsLogAnalysisViewModel Vm(params ReplayFrame[] frames)
    {
        var vm = new UdsLogAnalysisViewModel(new StubLoader(frames),
            new UdsOfflineAnalyzer(),
            new NoopHandoff(),
            new NoopDialogs(),
            new NoopExport(),
            new DidDatabase(userJsonPath: null),
            new RoutineDatabase(userJsonPath: null),
            new OdxImportViewModel(new NoopOdxService()));
        // 产品默认通道为空（必须显式选择）；本文件用例统一按通道 1 跑。
        vm.ChannelText = "1";
        return vm;
    }

    private static ReplayFrame Frame(double ts, uint id, params byte[] data)
        => new(ts, id, (byte)data.Length, data, FrameFlags.None, IsExtended: false,
            Channel: 1, IsTransmit: true);

    // ── 部分报告 ──────────────────────────────────────────────────────

    [Fact]
    public async Task Partial_report_never_claims_no_anomaly()
    {
        var vm = Vm(Frame(0.0, 0x7E0, 0x02, 0x10, 0x03));
        vm.FilePath = "x.asc";

        // 用一个一开始就取消的 token 强制产出部分报告。
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await vm.AnalyzeAsync(cts.Token);

        vm.Report.Should().NotBeNull();
        vm.Report!.IsPartial.Should().BeTrue();

        vm.NoFindingsHint.Should().BeNull("部分报告不能显示「未发现异常」");
        vm.PartialBanner.Should().NotBeNull("必须醒目提示报告不完整");
    }

    [Fact]
    public async Task Partial_report_does_not_claim_there_were_no_session_switches()
    {
        var vm = Vm(Frame(0.0, 0x7E0, 0x02, 0x10, 0x03));
        vm.FilePath = "x.asc";

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await vm.AnalyzeAsync(cts.Token);

        vm.SessionSummary.Should().NotContain("未出现会话切换",
            "诊断阶段没跑完时不能说「没有切换」——那是未知，不是没有");
    }

    // ── 完整报告 ──────────────────────────────────────────────────────

    [Fact]
    public async Task Clean_complete_report_says_no_anomaly()
    {
        var vm = Vm(
            Frame(0.0, 0x7E0, 0x03, 0x22, 0xF1, 0x90),
            Frame(0.01, 0x7E8, 0x03, 0x62, 0xF1, 0x90));
        vm.FilePath = "x.asc";

        await vm.AnalyzeAsync();

        vm.Report!.IsPartial.Should().BeFalse();
        vm.NoFindingsHint.Should().Be("未发现异常");
        vm.PartialBanner.Should().BeNull();
    }

    [Fact]
    public async Task Negative_response_shows_up_as_a_finding_row()
    {
        // 用扩展会话（0x02）而非编程会话（0x03）：编程会话会触发刷写流程分析，
        // 多产出一条「刷写未完成」的结论，掩盖本测试要验的负响应 finding。
        var vm = Vm(
            Frame(0.0, 0x7E0, 0x02, 0x10, 0x02),
            Frame(0.01, 0x7E8, 0x06, 0x50, 0x02, 0x00, 0x32, 0x01, 0xF4),
            Frame(0.1, 0x7E0, 0x04, 0x2E, 0xF1, 0x90, 0x01),
            Frame(0.11, 0x7E8, 0x03, 0x7F, 0x2E, 0x33));
        vm.FilePath = "x.asc";

        await vm.AnalyzeAsync();

        vm.FindingRows.Should().ContainSingle();
        vm.FindingRows[0].BodyText.Should().Contain("DID F190");
        vm.FindingRows[0].SuspectText.Should().StartWith("优先排查：");
        vm.NoFindingsHint.Should().BeNull();
    }

    /// <summary>
    /// 事实行不能混进方向性判断（审查 H1）。
    /// </summary>
    [Fact]
    public async Task Finding_message_contains_no_directional_judgement()
    {
        var vm = Vm(
            Frame(0.0, 0x7E0, 0x04, 0x2E, 0xF1, 0x90, 0x01),
            Frame(0.01, 0x7E8, 0x03, 0x7F, 0x2E, 0x11));
        vm.FilePath = "x.asc";

        await vm.AnalyzeAsync();

        var message = vm.FindingRows.Single().BodyText;
        message.Should().NotContain("通常是");
        message.Should().NotContain("根因");
    }

    [Fact]
    public async Task Session_summary_counts_confirmed_switches_only()
    {
        // 一次确认切换 + 一次 ECU 复位：复位不是「会话切换」。
        var vm = Vm(
            Frame(0.0, 0x7E0, 0x02, 0x10, 0x03),
            Frame(0.01, 0x7E8, 0x06, 0x50, 0x03, 0x00, 0x32, 0x01, 0xF4),
            Frame(1.0, 0x7E0, 0x02, 0x11, 0x01),
            Frame(1.01, 0x7E8, 0x02, 0x51, 0x01));
        vm.FilePath = "x.asc";

        await vm.AnalyzeAsync();

        vm.SessionSummary.Should().Contain("切换 1 次");
        vm.SessionSummary.Should().Contain("复位后会话未知");
    }

    /// <summary>
    /// 抑制正响应的会话切换（<c>10 83</c>）发生在<b>日志中途</b>，不是日志开头。
    /// <para>
    /// 它落进「默认分支」时会被说成「日志开始时 ECU 已在某会话中」——与事实相反。
    /// 用户看到这句话会以为切换根本没发生，从而去查一个没问题的切换。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Suppressed_session_switch_is_not_reported_as_the_log_start()
    {
        var vm = Vm(
            Frame(0.0, 0x7E0, 0x03, 0x22, 0xF1, 0x90),
            Frame(0.01, 0x7E8, 0x03, 0x62, 0xF1, 0x90),
            Frame(1.0, 0x7E0, 0x02, 0x10, 0x83));
        vm.FilePath = "x.asc";

        await vm.AnalyzeAsync();

        vm.SessionSummary.Should().NotContain("日志开始时",
            "10 83 是日志中途的会话切换请求，不是日志起点");
        vm.SessionSummary.Should().Contain("抑制",
            "必须说明「ECU 按协议未回，会话很可能已切换但没有响应证据」");
        // 抑制切换本身不是「已确认」的切换，不能计入切换次数。
        vm.SessionSummary.Should().NotContain("切换 1 次",
            "没有响应证据的切换不能算作已确认的切换");

        // 右栏同一事实必须给同样的确定性：摘要说「没有响应证据」，
        // 右栏就不能把请求的会话当成已确认的事实报出来。
        vm.SelectedTransaction = vm.VisibleTransactions.Last();
        vm.SelectedSessionAtTime.Should().Contain("未确认",
            "抑制切换的会话是「请求的」值，ECU 没有确认过");
    }

    [Fact]
    public async Task Timing_summary_surfaces_p2_values()
    {
        var vm = Vm(
            Frame(0.0, 0x7E0, 0x02, 0x10, 0x03),
            Frame(0.01, 0x7E8, 0x06, 0x50, 0x03, 0x00, 0x32, 0x01, 0xF4));
        vm.FilePath = "x.asc";

        await vm.AnalyzeAsync();

        vm.TimingSummary.Should().Contain("P2=50");
        vm.TimingSummary.Should().Contain("P2*=5000");
    }

    /// <summary>
    /// 「这条请求发生在哪个会话」必须能查到——用户问的「会话模式」不只是
    /// 「切换了几次」，还包括「这条失败是在编程会话下发的，还是已经退回默认」。
    /// </summary>
    /// <summary>
    /// <b>正响应没有 NRC，右栏不能显示「最终 NRC：0x」。</b>
    /// <para>
    /// 改前 XAML 写 <c>StringFormat='最终 NRC：0x{0:X2}'</c>，<c>FinalNrc</c> 为 null 时
    /// WPF 仍应用格式串，渲染出裸的 <c>0x</c>——用户会以为 NRC 是 <c>0x00</c>。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_positive_response_shows_no_nrc_line_at_all()
    {
        var vm = Vm(
            Frame(0.0, 0x7E0, 0x03, 0x22, 0xF1, 0x90),
            Frame(0.01, 0x7E8, 0x03, 0x62, 0xF1, 0x90));
        vm.FilePath = "x.asc";

        await vm.AnalyzeAsync();
        vm.SelectedTransaction = vm.VisibleTransactions.Single();

        vm.SelectedNrcLine.Should().BeNull("正响应没有 NRC，整行应当消失");
        vm.SelectedNrcNameLine.Should().BeNull();
        vm.SelectedPendingLine.Should().BeNull("没有 0x78 时不该显示「0 次」");
    }

    [Fact]
    public async Task A_negative_response_shows_its_nrc()
    {
        var vm = Vm(
            Frame(0.0, 0x7E0, 0x04, 0x2E, 0xF1, 0x90, 0x01),
            Frame(0.01, 0x7E8, 0x03, 0x7F, 0x2E, 0x33));
        vm.FilePath = "x.asc";

        await vm.AnalyzeAsync();
        vm.SelectedTransaction = vm.VisibleTransactions.Single();

        vm.SelectedNrcLine.Should().Be("最终 NRC：0x33");
        vm.SelectedNrcNameLine.Should().Contain("SecurityAccessDenied");
    }

    [Fact]
    public async Task Selected_transaction_reports_its_session()
    {
        var vm = Vm(
            Frame(0.0, 0x7E0, 0x02, 0x10, 0x03),
            Frame(0.01, 0x7E8, 0x06, 0x50, 0x03, 0x00, 0x32, 0x01, 0xF4),
            Frame(0.1, 0x7E0, 0x04, 0x2E, 0xF1, 0x90, 0x01),
            Frame(0.11, 0x7E8, 0x03, 0x7F, 0x2E, 0x33));
        vm.FilePath = "x.asc";

        await vm.AnalyzeAsync();

        vm.SelectedTransaction = vm.VisibleTransactions.Last();
        vm.SelectedSessionAtTime.Should().Be("编程");
    }

    /// <summary>
    /// 日志从会话中途开始时，「未知」是唯一诚实的答案，不能填「默认」。
    /// </summary>
    [Fact]
    public async Task Session_at_time_is_unknown_when_log_starts_mid_session()
    {
        var vm = Vm(
            Frame(0.0, 0x7E0, 0x03, 0x22, 0xF1, 0x90),
            Frame(0.01, 0x7E8, 0x03, 0x62, 0xF1, 0x90));
        vm.FilePath = "x.asc";

        await vm.AnalyzeAsync();

        vm.SelectedTransaction = vm.VisibleTransactions.Single();
        vm.SelectedSessionAtTime.Should().Be("未知");
    }

    /// <summary>
    /// 「错误方是谁」由「直接动作」回答，但只能陈述观测事实。
    /// </summary>
    [Fact]
    public async Task Finding_row_states_the_observed_action_without_blame()
    {
        var vm = Vm(
            Frame(0.0, 0x7E0, 0x04, 0x2E, 0xF1, 0x90, 0x01),
            Frame(0.01, 0x7E8, 0x03, 0x7F, 0x2E, 0x33));
        vm.FilePath = "x.asc";

        await vm.AnalyzeAsync();

        var row = vm.FindingRows.Single();
        row.ObservedActionText.Should().Be("ECU 拒绝了请求");
        row.ObservedActionText.Should().NotContain("ECU 有错");
        row.ObservedActionText.Should().NotContain("根因");
    }

    /// <summary>
    /// 传输警告必须按原因聚合。一次刷写中断会产生几十条
    /// <c>OrphanConsecutiveFrame</c>——它们说明的是同一件事，逐帧列出
    /// 会把一条结论稀释成几十行噪声。
    /// </summary>
    [Fact]
    public async Task Transport_warnings_are_aggregated_by_reason()
    {
        // 日志从多帧传输中途开始：30 个连续帧都没有首帧可挂。
        // PCI 低半字节是序号，高半字节固定 0x2（连续帧类型）。
        var frames = new List<ReplayFrame>();
        for (var i = 0; i < 30; i++)
            frames.Add(Frame(i * 0.01, 0x7E8, (byte)(0x20 | ((i + 1) & 0x0F)), 0xAA, 0xBB));

        var vm = Vm(frames.ToArray());
        vm.FilePath = "x.asc";
        await vm.AnalyzeAsync();

        vm.Report!.Warnings.Count.Should().BeGreaterThan(0, "先确认警告确实产生了");
        vm.HasWarnings.Should().BeTrue();
        vm.WarningRows.Should().ContainSingle("30 条同原因警告应聚合为 1 行");
        vm.WarningRows[0].Count.Should().Be(30);
        vm.WarningRows[0].Description.Should().Contain("孤立连续帧");
        vm.WarningRows[0].Display.Should().Contain("× 30");
    }

    [Fact]
    public async Task Warning_row_shows_the_evidence_range_when_aggregated()
    {
        var frames = new List<ReplayFrame>();
        for (var i = 0; i < 5; i++)
            frames.Add(Frame(i * 0.01, 0x7E8, (byte)(0x20 | ((i + 1) & 0x0F)), 0xAA));

        var vm = Vm(frames.ToArray());
        vm.FilePath = "x.asc";
        await vm.AnalyzeAsync();

        var row = vm.WarningRows.Single();
        row.FirstEvidenceIndex.Should().Be(0);
        row.LastEvidenceIndex.Should().Be(4);
        row.Display.Should().Contain("#0").And.Contain("#4");
    }

    /// <summary>
    /// 结论区收起后，标题栏仍要能看出「有没有问题」。
    /// </summary>
    [Fact]
    public async Task Conclusion_header_summarises_the_state_without_expanding()
    {
        var clean = Vm(
            Frame(0.0, 0x7E0, 0x03, 0x22, 0xF1, 0x90),
            Frame(0.01, 0x7E8, 0x03, 0x62, 0xF1, 0x90));
        clean.FilePath = "x.asc";
        await clean.AnalyzeAsync();
        clean.ConclusionHeader.Should().Contain("未发现异常");

        var failing = Vm(
            Frame(0.0, 0x7E0, 0x04, 0x2E, 0xF1, 0x90, 0x01),
            Frame(0.01, 0x7E8, 0x03, 0x7F, 0x2E, 0x33));
        failing.FilePath = "x.asc";
        await failing.AnalyzeAsync();
        failing.ConclusionHeader.Should().Contain("1 项需关注");
    }

    /// <summary>
    /// 背景流量必须给出「多忙、什么规律」，不能只列 ID × 帧数。
    /// 截图里那 20 个 ID 全是正常周期广播，裸列表说明不了任何事。
    /// </summary>
    [Fact]
    public async Task Bus_traffic_summary_reports_rate_and_pattern()
    {
        var frames = new List<ReplayFrame>();
        for (var i = 0; i < 50; i++)
            frames.Add(Frame(i * 0.1, 0x1881D0F3, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22));

        var vm = Vm(frames.ToArray());
        vm.FilePath = "x.asc";
        await vm.AnalyzeAsync();

        vm.HasBusTraffic.Should().BeTrue();
        vm.BusTrafficSummary.Should().Contain("帧/秒");
        vm.BusTrafficSummary.Should().Contain("周期 1");
        vm.BusTrafficRows.Should().ContainSingle();
        vm.BusTrafficRows[0].PatternText.Should().Be("周期");
        vm.BusTrafficRows[0].IdText.Should().Be("0x1881D0F3", "29 位 ID 要用 8 位十六进制");
    }

    /// <summary>
    /// 偶发流量要能被识别出来并排在前面——它比周期报文更可能和故障相关。
    /// </summary>
    [Fact]
    public async Task Sporadic_traffic_is_flagged_and_sorted_first()
    {
        var frames = new List<ReplayFrame>();
        for (var i = 0; i < 50; i++)
            frames.Add(Frame(i * 0.1, 0x200, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22));
        // 偶发：间隔忽长忽短
        frames.Add(Frame(0.0, 0x300, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22));
        frames.Add(Frame(0.02, 0x300, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22));
        frames.Add(Frame(2.0, 0x300, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22));
        frames.Add(Frame(2.05, 0x300, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22));

        var vm = Vm(frames.OrderBy(f => f.Timestamp).ToArray());
        vm.FilePath = "x.asc";
        await vm.AnalyzeAsync();

        vm.BusTrafficRows[0].IsSporadic.Should().BeTrue("偶发流量排最前");
        vm.BusTrafficRows[0].PatternText.Should().Be("偶发");
        vm.BusTrafficSummary.Should().Contain("偶发 1");
    }

    /// <summary>
    /// <b>有断档的 ID 必须显示分段，不能用「首–末」概括。</b>
    /// 用户截图里的「2 帧，0.54–25.41 s」看起来像整段都在活动，
    /// 实际中间 24.9 秒什么都没发——而那正是要看的现象。
    /// </summary>
    [Fact]
    public async Task Id_with_a_gap_shows_its_segments_not_a_span()
    {
        var frames = new List<ReplayFrame>
        {
            Frame(0.54, 0x18F1D0F3, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22),
            Frame(25.41, 0x18F1D0F3, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22),
        };

        var vm = Vm(frames.ToArray());
        vm.FilePath = "x.asc";
        await vm.AnalyzeAsync();

        var row = vm.BusTrafficRows.Single();
        row.HasGaps.Should().BeTrue();
        row.NeedsAttention.Should().BeTrue();

        // 必须出现两段的起点，且不能出现「0.54–25.41」这种跨越静默的区间。
        row.Detail.Should().Contain("0.54s");
        row.Detail.Should().Contain("25.41s");
        row.Detail.Should().Contain("分 2 段");
        row.Detail.Should().Contain("最长静默");
        row.Detail.Should().NotContain("0.54–25.41");
    }

    /// <summary>
    /// 总线级静默必须单独显示——刷写期间其它 ECU 报文集体消失是现象，
    /// 散在各 ID 的首末时间里没人推得出来。
    /// </summary>
    [Fact]
    public async Task Bus_wide_silence_is_surfaced_in_the_summary()
    {
        var frames = new List<ReplayFrame>();
        for (var t = 0.0; t < 2.0; t += 0.1)
            frames.Add(Frame(t, 0x200, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22));
        for (var t = 20.0; t < 22.0; t += 0.1)
            frames.Add(Frame(t, 0x200, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22));

        var vm = Vm(frames.ToArray());
        vm.FilePath = "x.asc";
        await vm.AnalyzeAsync();

        vm.HasBusSilence.Should().BeTrue();
        vm.BusSilenceSummary.Should().Contain("总线静默 1 段");
        vm.BusSilenceSummary.Should().Contain("18");
    }

    /// <summary>
    /// 连续发送的 ID 不该显示分段文案，避免每行都很长。
    /// </summary>
    [Fact]
    public async Task Continuously_periodic_id_does_not_show_segments()
    {
        var frames = new List<ReplayFrame>();
        for (var t = 0.0; t < 5.0; t += 0.1)
            frames.Add(Frame(t, 0x200, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11, 0x22));

        var vm = Vm(frames.ToArray());
        vm.FilePath = "x.asc";
        await vm.AnalyzeAsync();

        var row = vm.BusTrafficRows.Single();
        row.HasGaps.Should().BeFalse();
        row.Detail.Should().Contain("连续");
        row.Detail.Should().NotContain("分 ");
    }

    /// <summary>
    /// 诊断地址自身的帧不能算进背景流量。
    /// </summary>
    [Fact]
    public async Task Diagnostic_frames_are_not_counted_as_background_traffic()
    {
        var vm = Vm(
            Frame(0.0, 0x7E0, 0x03, 0x22, 0xF1, 0x90),
            Frame(0.01, 0x7E8, 0x03, 0x62, 0xF1, 0x90));
        vm.FilePath = "x.asc";

        await vm.AnalyzeAsync();

        vm.HasBusTraffic.Should().BeFalse("没有其它 ECU 的报文");
        vm.BusTrafficSummary.Should().BeEmpty();
    }

    [Fact]
    public async Task Validation_failures_surface_individual_details()
    {
        var vm = Vm(
            Frame(0.0, 0x7E0, 0x03, 0x22, 0xF1, 0x90),
            Frame(0.01, 0x7E8, 0x03, 0x62, 0xF1, 0x91)); // DID 回显不符
        vm.FilePath = "x.asc";

        await vm.AnalyzeAsync();

        vm.HasValidationFailures.Should().BeTrue();
        vm.ValidationFailureRows.Should().ContainSingle();
        vm.ValidationFailureRows[0].FailedCheck.Should().Be("DidEcho");
        vm.ValidationFailureRows[0].Description.Should().Contain("DID");
    }
}
