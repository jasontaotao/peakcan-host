using System.Collections.Specialized;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Uds;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.Core.Analysis.Uds;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Uds.Database;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Uds.Database;
using PeakCan.HIL.Core.Uds.Odx;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Uds;

public sealed class UdsLogAnalysisViewModelTests
{
    private static ReplayFrame Frame(uint id, params byte[] data)
        => new(Timestamp: 0, Id: id, Dlc: (byte)data.Length, Data: data,
               Flags: FrameFlags.None, IsExtended: false, Channel: 1, IsTransmit: null);

    private static ReplayFrame[] ThreeFrameLog() => new[]
    {
        Frame(0x7E0, 0x02, 0x10, 0x01),
        Frame(0x7E8, 0x06, 0x50, 0x01, 0x00, 0x32, 0x01, 0xF4),
        Frame(0x123, 0xAA),
    };

    private static UdsLogAnalysisViewModel Vm(
        IUdsOfflineTraceLoader loader, ITraceViewerChatHandoffService? handoff = null,
        IUdsExportPrompt? exportPrompt = null, OdxImportViewModel? odxImport = null,
        PeakCan.Host.App.Services.Uds.UdsTransportSettingsStore? settings = null,
        string? channel = "1")
    {
        var vm = new UdsLogAnalysisViewModel(loader, new UdsOfflineAnalyzer(),
            handoff ?? new FakeHandoff(), new FakeDialogs(),
            exportPrompt ?? new FakeExportPrompt(UdsExportChoice.Cancel),
            new DidDatabase(userJsonPath: null), new RoutineDatabase(userJsonPath: null),
            odxImport ?? new OdxImportViewModel(
                new FakeOdxService(OdxImportResult.Ok(0, 0, 0, Array.Empty<string>()))),
            settings);
        // 产品默认通道为空（必须显式选择）；既有用例统一按通道 1 跑。
        if (channel is not null) vm.ChannelText = channel;
        return vm;
    }

    // ========================================================================
    // 离线页地址记忆：在线页配过的地址这里直接继承，省得每次手敲。
    // ========================================================================

    [Fact]
    public void Address_Fields_Are_Seeded_From_Persisted_Settings()
    {
        var store = new PeakCan.Host.App.Services.Uds.UdsTransportSettingsStore(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PeakCan.Host.App.Services.Uds.UdsTransportSettingsStore>.Instance,
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"offline-{Guid.NewGuid():N}.json"));
        store.Save(PeakCan.Host.App.Services.Uds.UdsTransportSettings.Default with
        {
            RequestId = 0x714,
            ResponseId = 0x760,
        });

        var vm = Vm(new FakeLoader(ThreeFrameLog()), settings: store);

        vm.RequestIdText.Should().Be("714",
            "离线页要继承在线配置，而不是硬编码的 7E0");
        vm.ResponseIdText.Should().Be("760");
    }

    [Fact]
    public void Address_Fields_Stay_At_Defaults_When_No_Store_Is_Injected()
    {
        var vm = Vm(new FakeLoader(ThreeFrameLog()));

        vm.RequestIdText.Should().Be("7E0");
        vm.ResponseIdText.Should().Be("7E8");
    }

    [Fact]
    public void Offline_Edits_Do_Not_Write_Back_To_The_Store()
    {
        var store = new PeakCan.Host.App.Services.Uds.UdsTransportSettingsStore(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PeakCan.Host.App.Services.Uds.UdsTransportSettingsStore>.Instance,
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"offline-{Guid.NewGuid():N}.json"));
        var vm = Vm(new FakeLoader(ThreeFrameLog()), settings: store);

        vm.RequestIdText = "999"; // 用户临时分析另一台 ECU

        store.Current.RequestId.Should().Be(0x7E0,
            "离线页的临时改动不能污染在线诊断的地址配置");
    }

    [Fact]
    public async Task Analyze_produces_report_and_visible_rows()
    {
        var vm = Vm(new FakeLoader(ThreeFrameLog()));
        vm.FilePath = "fake.asc";
        await vm.AnalyzeAsync();

        vm.Report.Should().NotBeNull();
        vm.VisibleTransactions.Should().ContainSingle();
        vm.Status.Should().Contain("分析完成");
    }

    [Fact]
    public async Task No_matching_frames_shows_address_specific_empty_state()
    {
        var vm = Vm(new FakeLoader(new[] { Frame(0x123, 0x01) }));
        vm.FilePath = "fake.asc";
        await vm.AnalyzeAsync();

        vm.Status.Should().Contain("当前设置下未识别到诊断事务");
        vm.Report.Should().NotBeNull();
        vm.Report!.Transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_produces_partial_report()
    {
        var vm = Vm(new FakeLoader(ThreeFrameLog()));
        vm.FilePath = "fake.asc";
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await vm.AnalyzeAsync(cts.Token);

        vm.IsPartial.Should().BeTrue();
        vm.Status.Should().Contain("部分");
    }

    [Fact]
    public async Task Interpret_selected_sends_handoff_without_provider_call()
    {
        var handoff = new FakeHandoff();
        var vm = Vm(new FakeLoader(ThreeFrameLog()), handoff);
        vm.FilePath = "fake.asc";
        await vm.AnalyzeAsync();
        vm.SelectedTransaction = vm.Report!.Transactions[0];

        vm.InterpretSelectedCommand.Execute(null);

        handoff.Payload.Should().NotBeNull();
        handoff.Payload!.Prompt.Should().Contain(vm.Report.ReportId.ToString());
    }

    [Fact]
    public async Task Export_cancel_writes_nothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "r.md");
        var vm = Vm(new FakeLoader(ThreeFrameLog()),
            exportPrompt: new FakeExportPrompt(UdsExportChoice.Cancel, savePath: path));
        vm.FilePath = "fake.asc";
        await vm.AnalyzeAsync();

        await vm.ExportAsync();

        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task Export_raw_writes_markdown_and_reports_path()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "r.md");
        var vm = Vm(new FakeLoader(ThreeFrameLog()),
            exportPrompt: new FakeExportPrompt(UdsExportChoice.Raw, savePath: path));
        vm.FilePath = "fake.asc";
        await vm.AnalyzeAsync();

        await vm.ExportAsync();

        File.Exists(path).Should().BeTrue();
        File.ReadAllText(path).Should().Contain("# UDS 离线分析报告");
        vm.Status.Should().Contain("已导出");
    }

    [Fact]
    public async Task Failed_load_clears_the_previous_report()
    {
        var loader = new FakeLoader(ThreeFrameLog());
        var vm = Vm(loader);
        vm.FilePath = "fake.asc";
        await vm.AnalyzeAsync();
        vm.Report.Should().NotBeNull();

        loader.FailNext = true;
        await vm.AnalyzeAsync();

        vm.Report.Should().BeNull("旧报告不能在新分析失败后继续冒充当前报告");
        vm.Status.Should().Contain("读取失败");
    }

    [Fact]
    public async Task Stale_run_cannot_overwrite_the_newer_report()
    {
        var loader = new StagedLoader();
        var vm = Vm(loader);
        vm.FilePath = "slow.asc";

        var slowRun = vm.AnalyzeAsync();
        vm.FilePath = "fast.asc";
        await vm.AnalyzeAsync();
        vm.Status.Should().Contain("分析完成");

        loader.ReleaseSlow();
        await slowRun;

        vm.Status.Should().Contain("分析完成", "旧任务被取消后不得把自己的状态写回去");
        vm.Report!.SourcePath.Should().Be("fast.asc");
    }

    /// <summary>
    /// 地址填对、但通道选错时，不能只丢一句「未识别到诊断事务」——日志里的帧
    /// 全在另一个通道，界面必须把这个事实说出来并给一键切换。
    /// <para>
    /// 真实案例：正向刷写日志的 63 万帧全在通道 2，界面按通道 1 解析，
    /// 结果 0 事务、全部进背景流量，用户以为是「不支持 29 位扩展帧」。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Zero_matches_on_the_wrong_channel_points_at_the_channel_that_has_data()
    {
        var frames = new[]
        {
            new ReplayFrame(0, 0x18DAD1F1, 3, new byte[] { 0x02, 0x10, 0x01 },
                FrameFlags.None, IsExtended: true, Channel: 2, IsTransmit: true),
            new ReplayFrame(0.01, 0x18DAF1D1, 7, new byte[] { 0x06, 0x50, 0x01, 0x00, 0x32, 0x01, 0xF4 },
                FrameFlags.None, IsExtended: true, Channel: 2, IsTransmit: false),
        };
        var vm = Vm(new FakeLoader(frames));
        vm.FilePath = "ext.asc";
        vm.RequestIdText = "18DAD1F1";
        vm.ResponseIdText = "18DAF1D1";
        vm.IsExtended = true;
        vm.ChannelText = "1";

        await vm.AnalyzeAsync();

        vm.VisibleTransactions.Should().BeEmpty();
        vm.AddressHint.Should().Contain("通道 2", "必须点名数据实际所在的通道");
        vm.SuggestedChannelText.Should().Be("2");

        await vm.ApplySuggestedChannelCommand.ExecuteAsync(null);

        vm.ChannelText.Should().Be("2");
        vm.VisibleTransactions.Should().ContainSingle();
        vm.AddressHint.Should().BeNull("匹配成功后就不要再挂着警告");
    }

    /// <summary>
    /// 两个通道都出现同一组 ID（多路录制）时，只列事实、不给建议：
    /// 换错总线的代价比多看一眼大。
    /// </summary>
    [Fact]
    public async Task Address_ids_on_two_channels_are_listed_without_a_suggestion()
    {
        var frames = new[]
        {
            new ReplayFrame(0, 0x18DAD1F1, 3, new byte[] { 0x02, 0x10, 0x01 },
                FrameFlags.None, IsExtended: true, Channel: 2, IsTransmit: true),
            new ReplayFrame(0.01, 0x18DAD1F1, 3, new byte[] { 0x02, 0x10, 0x01 },
                FrameFlags.None, IsExtended: true, Channel: 3, IsTransmit: true),
        };
        var vm = Vm(new FakeLoader(frames));
        vm.FilePath = "x.asc";
        vm.RequestIdText = "18DAD1F1";
        vm.ResponseIdText = "18DAF1D1";
        vm.IsExtended = true;
        vm.ChannelText = "1";

        await vm.AnalyzeAsync();

        vm.AddressHint.Should().Contain("通道 2").And.Contain("通道 3");
        vm.SuggestedChannelText.Should().BeNull("多通道都命中时不替用户挑一个");

        // 没有建议时命令必须是空操作，不能抛异常或被点出副作用。
        await vm.ApplySuggestedChannelCommand.ExecuteAsync(null);
        vm.ChannelText.Should().Be("1");
    }

    [Fact]
    public void Channel_defaults_to_empty()
    {
        var vm = Vm(new FakeLoader(ThreeFrameLog()), channel: null);

        vm.ChannelText.Should().BeEmpty("默认不能替用户猜通道——猜错时界面会静默出 0 条事务");
    }

    /// <summary>
    /// 打开另一份「通道集合不同」的日志时，用户已选的通道不能被下拉框清空。
    /// <para>
    /// WPF 可编辑 ComboBox 在 ItemsSource 重建时会把 Text 写回空串（实测），
    /// 双向绑定随即清掉 VM 的值——下一次分析就变成「请选择通道」。
    /// 这里模拟控件的清除行为，锁住 VM 的恢复逻辑。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Loading_another_log_keeps_the_channel_the_user_picked()
    {
        var loader = new FakeLoader(ThreeFrameLog());
        var vm = Vm(loader);
        vm.FilePath = "a.asc";
        await vm.AnalyzeAsync();

        vm.ChannelText = "2"; // 用户从下拉里选中通道 2

        // 模拟可编辑 ComboBox：ItemsSource 被 Clear 时把 Text 写回空串。
        vm.AvailableChannels.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
                vm.ChannelText = "";
        };

        loader.Frames = new[]
        {
            new ReplayFrame(0, 0x7E0, 3, new byte[] { 0x02, 0x10, 0x01 },
                FrameFlags.None, IsExtended: false, Channel: 2, IsTransmit: true),
            new ReplayFrame(0.01, 0x7E8, 7, new byte[] { 0x06, 0x50, 0x01, 0x00, 0x32, 0x01, 0xF4 },
                FrameFlags.None, IsExtended: false, Channel: 2, IsTransmit: false),
            new ReplayFrame(0.02, 0x123, 1, new byte[] { 0xAA },
                FrameFlags.None, IsExtended: false, Channel: 3, IsTransmit: null),
        };

        await vm.AnalyzeAsync();

        vm.ChannelText.Should().Be("2", "打开另一份日志不能丢掉用户已选的通道");
        vm.AvailableChannels.Should().Equal("2", "3");
        vm.VisibleTransactions.Should().NotBeEmpty("恢复通道后本次分析必须继续");
    }

    /// <summary>
    /// 通道未选时分析只读取日志并列出实际通道，不能出报告、也不能退回默认通道。
    /// </summary>
    [Fact]
    public async Task Analyzing_without_a_channel_only_loads_and_asks_to_pick_one()
    {
        var vm = Vm(new FakeLoader(ThreeFrameLog()), channel: null);
        vm.FilePath = "fake.asc";

        await vm.AnalyzeAsync();

        vm.Report.Should().BeNull("没选通道不能出结论");
        vm.VisibleTransactions.Should().BeEmpty();
        // 下拉里要能直接选日志实际通道
        vm.AvailableChannels.Should().Equal("1");
        vm.Status.Should().Contain("请选择通道");
    }

    /// <summary>
    /// 通道下拉框的选项来自日志里实际出现的通道，而不是让用户手敲猜。
    /// </summary>
    [Fact]
    public async Task Available_channels_are_populated_from_the_loaded_log()
    {
        var frames = new[]
        {
            new ReplayFrame(0, 0x123, 2, new byte[] { 0xAA }, FrameFlags.None, false, Channel: 3),
            new ReplayFrame(0.01, 0x123, 2, new byte[] { 0xAA }, FrameFlags.None, false, Channel: 1),
            new ReplayFrame(0.02, 0x123, 2, new byte[] { 0xAA }, FrameFlags.None, false, Channel: 2),
        };
        var vm = Vm(new FakeLoader(frames));
        vm.FilePath = "x.asc";

        await vm.AnalyzeAsync();

        // 按数值排序（而不是出现顺序），方便在下拉框里找。
        vm.AvailableChannels.Should().Equal("1", "2", "3");
    }

    /// <summary>
    /// 判据必须按「ID 出现在哪个通道」而不是「哪个通道帧最多」。
    /// 背景流量通道帧数压倒性更多时，也不能把用户指到背景总线上去。
    /// </summary>
    [Fact]
    public async Task Busy_background_channel_does_not_mask_the_channel_with_the_address_ids()
    {
        var frames = new List<ReplayFrame>();
        // 通道 2：一千帧无关背景流量（帧数最多）
        for (var i = 0; i < 1000; i++)
            frames.Add(new ReplayFrame(i * 0.001, 0x123, 2, new byte[] { 0xAA, 0xBB },
                FrameFlags.None, IsExtended: false, Channel: 2, IsTransmit: null));
        // 通道 3：少量目标 ID
        frames.Add(new ReplayFrame(0, 0x18DAD1F1, 3, new byte[] { 0x02, 0x10, 0x01 },
            FrameFlags.None, IsExtended: true, Channel: 3, IsTransmit: true));
        frames.Add(new ReplayFrame(0.01, 0x18DAF1D1, 7, new byte[] { 0x06, 0x50, 0x01, 0x00, 0x32, 0x01, 0xF4 },
            FrameFlags.None, IsExtended: true, Channel: 3, IsTransmit: false));

        var vm = Vm(new FakeLoader(frames.ToArray()));
        vm.FilePath = "x.asc";
        vm.RequestIdText = "18DAD1F1";
        vm.ResponseIdText = "18DAF1D1";
        vm.IsExtended = true;
        vm.ChannelText = "1";

        await vm.AnalyzeAsync();

        vm.SuggestedChannelText.Should().Be("3",
            "应指向承载目标 ID 的通道，而不是帧数最多的背景通道");
    }

    /// <summary>目标 ID 就在当前通道上、但仍 0 事务（如 PCI 全坏）：换通道救不了，不能提示。</summary>
    [Fact]
    public async Task Address_ids_on_the_current_channel_yield_no_channel_suggestion()
    {
        var frames = new[]
        {
            // 合法 ID、非法 PCI（0x0F 不是合法 PCI 类型）→ 不产生事务
            new ReplayFrame(0, 0x18DAD1F1, 2, new byte[] { 0x0F, 0x00 },
                FrameFlags.None, IsExtended: true, Channel: 1, IsTransmit: true),
        };
        var vm = Vm(new FakeLoader(frames));
        vm.FilePath = "x.asc";
        vm.RequestIdText = "18DAD1F1";
        vm.ResponseIdText = "18DAF1D1";
        vm.IsExtended = true;
        vm.ChannelText = "1";

        await vm.AnalyzeAsync();

        vm.VisibleTransactions.Should().BeEmpty();
        vm.AddressHint.Should().BeNull("ID 就在当前通道，问题不是通道选错");
        vm.SuggestedChannelText.Should().BeNull();
    }

    /// <summary>日志里根本没有目标 ID：那是 ID 填错，不是通道问题，不能给通道建议。</summary>
    [Fact]
    public async Task Absent_address_ids_yield_no_channel_suggestion()
    {
        var frames = new[]
        {
            new ReplayFrame(0, 0x123, 2, new byte[] { 0xAA, 0xBB },
                FrameFlags.None, IsExtended: false, Channel: 2, IsTransmit: null),
        };
        var vm = Vm(new FakeLoader(frames));
        vm.FilePath = "x.asc";
        vm.RequestIdText = "18DAD1F1";
        vm.ResponseIdText = "18DAF1D1";
        vm.IsExtended = true;
        vm.ChannelText = "1";

        await vm.AnalyzeAsync();

        vm.AddressHint.Should().BeNull("ID 不存在时提示通道会误导");
        vm.SuggestedChannelText.Should().BeNull();
    }

    /// <summary>
    /// 用户取消得到的是部分报告：事务为空是「没分析完」，不是「通道选错」。
    /// 不能在部分报告上弹出误导性的通道提示。
    /// </summary>
    [Fact]
    public async Task Cancelled_partial_report_does_not_claim_the_channel_is_wrong()
    {
        var frames = new[]
        {
            new ReplayFrame(0, 0x18DAD1F1, 3, new byte[] { 0x02, 0x10, 0x01 },
                FrameFlags.None, IsExtended: true, Channel: 2, IsTransmit: true),
            new ReplayFrame(0.01, 0x18DAF1D1, 7, new byte[] { 0x06, 0x50, 0x01, 0x00, 0x32, 0x01, 0xF4 },
                FrameFlags.None, IsExtended: true, Channel: 2, IsTransmit: false),
        };
        var vm = Vm(new FakeLoader(frames));
        vm.FilePath = "x.asc";
        vm.RequestIdText = "18DAD1F1";
        vm.ResponseIdText = "18DAF1D1";
        vm.IsExtended = true;
        vm.ChannelText = "1";

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await vm.AnalyzeAsync(cts.Token);

        vm.Report!.IsPartial.Should().BeTrue();
        vm.AddressHint.Should().BeNull("部分报告不能冒充「通道选错」的结论");
        vm.SuggestedChannelText.Should().BeNull();
    }

    /// <summary>
    /// 29 位扩展帧必须从界面一路接到分析器：请求/响应 ID 用 8 位 hex 输入，
    /// 勾上「扩展帧」后，物理寻址与功能寻址都要能配对。
    /// </summary>
    [Fact]
    public async Task Extended_29bit_address_is_wired_through_the_page()
    {
        var frames = new[]
        {
            new ReplayFrame(0, 0x18DA10F1, 3, new byte[] { 0x02, 0x10, 0x01 },
                FrameFlags.None, IsExtended: true, Channel: 1, IsTransmit: null),
            new ReplayFrame(0.01, 0x18DAF110, 7, new byte[] { 0x06, 0x50, 0x01, 0x00, 0x32, 0x01, 0xF4 },
                FrameFlags.None, IsExtended: true, Channel: 1, IsTransmit: null),
            new ReplayFrame(0.02, 0x18DB33F1, 3, new byte[] { 0x02, 0x3E, 0x00 },
                FrameFlags.None, IsExtended: true, Channel: 1, IsTransmit: null),
            new ReplayFrame(0.03, 0x18DAF110, 3, new byte[] { 0x02, 0x7E, 0x00 },
                FrameFlags.None, IsExtended: true, Channel: 1, IsTransmit: null),
        };
        var vm = Vm(new FakeLoader(frames));
        vm.FilePath = "ext.asc";
        vm.RequestIdText = "18DA10F1";
        vm.ResponseIdText = "18DAF110";
        vm.FunctionalIdText = "18DB33F1";
        vm.IsExtended = true;

        await vm.AnalyzeAsync();

        vm.Report!.Counts.NonDiagnostic.Should().Be(0, "29 位帧必须按扩展帧被识别，而不是全判背景流量");
        vm.VisibleTransactions.Should().HaveCount(2, "物理请求和功能广播各配对一次");
        vm.VisibleTransactions.Should().OnlyContain(t => t.FinalKind == UdsFinalKind.Positive);
    }

    /// <summary>
    /// 用户是按表格里看得见的文字搜的：结果列是中文（FinalKindDisplay），NRC 列带
    /// <c>0x</c> 前缀。只匹配枚举名 / 裸 hex 时，输入「正响应」或「0x22」会得到空表，
    /// 用户会以为筛选坏了。
    /// </summary>
    [Fact]
    public async Task Filter_matches_the_text_the_table_displays()
    {
        var frames = new[]
        {
            Frame(0x7E0, 0x02, 0x10, 0x01),
            Frame(0x7E8, 0x06, 0x50, 0x01, 0x00, 0x32, 0x01, 0xF4),
            Frame(0x7E0, 0x03, 0x22, 0xF1, 0x90),
            Frame(0x7E8, 0x03, 0x7F, 0x22, 0x22),
        };
        var vm = Vm(new FakeLoader(frames));
        vm.FilePath = "fake.asc";
        await vm.AnalyzeAsync();

        vm.FilterText = "正响应";
        vm.VisibleTransactions.Should().ContainSingle("结果列显示「正响应」，筛选必须按显示文本命中")
            .Which.FinalKindDisplay.Should().Be("正响应");

        vm.FilterText = "0x22";
        vm.VisibleTransactions.Should().ContainSingle("NRC 列显示「0x22」，筛选必须接受带前缀的写法")
            .Which.FinalNrc.Should().Be(0x22);

        // 旧语义必须保留：英文枚举名与裸 hex 是既有用户已经在用的写法，
        // 改判据时不能顺手删掉，否则他们会静默搜不到。
        vm.FilterText = "Negative";
        vm.VisibleTransactions.Should().ContainSingle("英文枚举名是旧写法，不能回归")
            .Which.FinalNrc.Should().Be(0x22);

        vm.FilterText = "22";
        vm.VisibleTransactions.Should().ContainSingle("裸 hex 是旧写法，不能回归")
            .Which.FinalNrc.Should().Be(0x22);
    }

    [Fact]
    public async Task Filter_with_no_match_shows_a_distinct_hint()
    {
        var vm = Vm(new FakeLoader(ThreeFrameLog()));
        vm.FilePath = "fake.asc";
        await vm.AnalyzeAsync();

        vm.FilterText = "no-such-service";

        vm.VisibleTransactions.Should().BeEmpty();
        vm.FilterEmptyHint.Should().NotBeNullOrWhiteSpace();
        vm.Status.Should().Contain("分析完成", "筛选无结果不改写分析状态");
    }

    [Fact]
    public async Task Odx_import_updates_status_and_asks_for_reanalysis()
    {
        var odx = new OdxImportViewModel(
            new FakeOdxService(OdxImportResult.Ok(1, 0, 0, Array.Empty<string>())));
        var vm = Vm(new FakeLoader(ThreeFrameLog()), odxImport: odx);

        await odx.ImportAsync("x.odx");

        vm.OdxStatusSummary.Should().Contain("ODX");
        vm.OdxReloadHint.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Closing_the_window_cancels_and_discards_the_report()
    {
        var vm = Vm(new FakeLoader(ThreeFrameLog()));
        vm.FilePath = "fake.asc";
        await vm.AnalyzeAsync();
        vm.Report.Should().NotBeNull();

        vm.StopForWindowClose();

        vm.Report.Should().BeNull("重开窗口必须是新的空状态");
        vm.VisibleTransactions.Should().BeEmpty();
        vm.Status.Should().Be("未选择文件");
        vm.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Status_distinguishes_reading_the_file_from_analyzing()
    {
        var loader = new StagedLoader();
        var vm = Vm(loader);
        vm.FilePath = "slow.asc";

        var run = vm.AnalyzeAsync();
        await loader.Started;
        vm.Status.Should().Contain("读取文件");

        loader.ReleaseSlow();
        await run;
    }

    [Fact]
    public async Task Unclosed_transaction_shows_an_explicit_conclusion()
    {
        // 只有请求、没有响应：必须说明“未闭合”，而不是留一个 None。
        var vm = Vm(new FakeLoader(new[] { Frame(0x7E0, 0x02, 0x10, 0x03) }));
        vm.FilePath = "fake.asc";
        await vm.AnalyzeAsync();
        vm.SelectedTransaction = vm.Report!.Transactions.Single();

        vm.SelectedConclusion.Should().Contain("未闭合");
    }

    private sealed class StagedLoader : IUdsOfflineTraceLoader
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _first = true;

        public Task Started => _started.Task;

        public void ReleaseSlow() => _gate.TrySetResult();

        public async Task<UdsOfflineTraceLoadResult> LoadAsync(string path, CancellationToken ct = default)
        {
            if (!_first) return new UdsOfflineTraceLoadResult(ThreeFrameLog(), "ASC");
            _first = false;
            _started.TrySetResult();
            await _gate.Task;
            return new UdsOfflineTraceLoadResult(ThreeFrameLog(), "ASC");
        }
    }

    private sealed class FakeLoader(ReplayFrame[] initial) : IUdsOfflineTraceLoader
    {
        public bool FailNext { get; set; }

        /// <summary>可在两次分析之间替换，模拟「打开另一份日志」。</summary>
        public ReplayFrame[] Frames { get; set; } = initial;

        public Task<UdsOfflineTraceLoadResult> LoadAsync(string path, CancellationToken ct = default)
            => FailNext
                ? Task.FromException<UdsOfflineTraceLoadResult>(new UdsOfflineLoadException("文件不存在：fake.asc"))
                : Task.FromResult(new UdsOfflineTraceLoadResult(Frames, "ASC"));
    }

    private sealed class FakeHandoff : ITraceViewerChatHandoffService
    {
        public UdsChatHandoffPayload? Payload { get; private set; }
        public void Handoff(UdsChatHandoffPayload payload) => Payload = payload;
    }

    private sealed class FakeDialogs : IFileDialogService
    {
        public string? ShowOpenDialog(string filter) => null;
        public string? ShowSaveDialog(string filter, string? defaultExt, string? initialDirectory) => null;
    }

    private sealed class FakeExportPrompt(UdsExportChoice choice, string? savePath = null) : IUdsExportPrompt
    {
        public string? ChooseSavePath(out bool isCsv)
        {
            isCsv = false;
            return savePath;
        }

        public bool ConfirmOverwrite(string path) => true;
        public UdsExportChoice AskRedaction() => choice;
    }

    private sealed class FakeOdxService(OdxImportResult result) : IOdxImportService
    {
        public Task<OdxImportResult> ImportAsync(string odxPath, CancellationToken ct = default)
            => Task.FromResult(result);
    }
}
