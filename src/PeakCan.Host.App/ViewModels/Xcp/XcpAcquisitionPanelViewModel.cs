using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using A2lEditor.Core.IfData;
using A2lEditor.Core.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.HIL.Core;
using PeakCan.Host.App.ViewModels.Xcp;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Xcp.Abstractions;
using PeakCan.Host.Core.Xcp.Capability;
using PeakCan.Host.Core.Xcp.Protocol;
using PeakCan.Host.Core.Xcp.Receive;
using PeakCan.Host.Core.Xcp.Scheduling;
using PeakCan.Host.Infrastructure.Xcp;

namespace PeakCan.Host.App.ViewModels.Xcp;

/// <summary>
/// XCP 采集面板 VM（S3-T7，spec D6 生命周期）：Start = 对账（CONNECT + 能力实测 +
/// <see cref="XcpCapabilityReconciler"/>，不匹配即停）→ Plan → ConfigureRotationAsync →
/// 轮询循环（options.Sink 接 T4 卡片 sink）→ MarkConnected；Stop = 停轮询（await
/// 会话静默，S2 gate/quiesce 契约的调用侧义务在 VM 内执行）→ Dispose 会话 →
/// MarkDisconnected。
/// <para>
/// 通道来源：T3 连接面板（Q2 定案，本面板不做连接控件）；transport 由
/// <see cref="XcpCanTransport"/> 包装 ICanChannel（App 允许依赖 Infrastructure 先例）。
/// 能力实测链（CONNECT/GET_DAQ_*_INFO/必要命令探测）照 XcpProbeCommand 的实测口径，
/// 但<b>绝不探测 DOWNLOAD</b>（spec §1 DOWNLOAD 零入口，T10 守卫钉住）：A2L 声明
/// DOWNLOAD 而从机实测清单缺它时对账会拒绝——宁可不采，不产生任何写流量。
/// </para>
/// </summary>
public partial class XcpAcquisitionPanelViewModel : ObservableObject, IDisposable
{
    private readonly XcpConnectionPanelViewModel? _connection;
    private readonly IXcpAcquisitionSink? _sink;
    private readonly Func<ICanChannel, IXcpTransport> _transportFactory;
    private readonly XcpAcquisitionSessionOptions? _sessionOptions;
    private readonly XcpMasterOptions? _masterOptionsOverride;
    private readonly TimeProvider _timeProvider;

    private XcpAcquisitionSession? _session;
    private IXcpTransport? _transport;
    private CancellationTokenSource? _pollCts;
    private Task? _pollLoop;

    /// <summary>可空注入构造（沿 App VM 测试构造先例：无参可建、测试点零回归）。</summary>
    /// <param name="connection">T3 连接面板（LoadedResult / SelectedChannel / Mark*）。</param>
    /// <param name="sink">采集样本/归因出站口（T4 XcpCardPanelSink，spec D3）。</param>
    /// <param name="transportFactory">ICanChannel → IXcpTransport 包装；缺省 <see cref="XcpCanTransport"/>。</param>
    /// <param name="sessionOptions">S2 组合根参数（轮询/轮转/gapNotifier；Sink 由本 VM 注入）。</param>
    /// <param name="masterOptionsOverride">协议引擎参数覆盖（缺省按 A2L 声明 CAN ID + 默认 T1/重试）。</param>
    /// <param name="timeProvider">计时源（会话与轮询节拍共用，测试可注入虚拟时钟）。</param>
    public XcpAcquisitionPanelViewModel(
        XcpConnectionPanelViewModel? connection = null,
        IXcpAcquisitionSink? sink = null,
        Func<ICanChannel, IXcpTransport>? transportFactory = null,
        XcpAcquisitionSessionOptions? sessionOptions = null,
        XcpMasterOptions? masterOptionsOverride = null,
        TimeProvider? timeProvider = null)
    {
        _connection = connection;
        _sink = sink;
        _transportFactory = transportFactory ?? (channel => new XcpCanTransport(channel));
        _sessionOptions = sessionOptions;
        _masterOptionsOverride = masterOptionsOverride;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>采集会话运行标志（D6 门禁：运行中禁止重 Start——S2 并发契约的调用侧义务）。</summary>
    [ObservableProperty]
    private bool _isAcquiring;

    /// <summary>最近一次对账报告（拒绝时明细进状态区，形状原样暴露供上层消费）。</summary>
    public XcpCapabilityReport? CapabilityReport { get; private set; }

    /// <summary>状态区行（对账明细 / 失败提示 / 停启事件，人读文本）。</summary>
    public ObservableCollection<string> StatusLines { get; } = new();

    /// <summary>
    /// 采集覆盖清单（spec D6 / S2 ComputeCoverage）：Covered/PollingCause/MissingCause
    /// 三列直读展示，逐合同对象一行；仅 Plan 成功后有值，拒绝路径保持空。
    /// </summary>
    public ObservableCollection<AcquisitionCoverageEntry> CoverageEntries { get; } = new();

    partial void OnIsAcquiringChanged(bool value) => StartCommand.NotifyCanExecuteChanged();

    private bool CanStartAcquisition() => !IsAcquiring;

    /// <summary>
    /// D6 Start：对账 → Plan → ConfigureRotation → 轮询循环 → MarkConnected。
    /// 任何失败（对账拒绝/规划失败/轮转失败）都会释放会话并返回 false——
    /// 不准静默降级后照常启动。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartAcquisition))]
    public async Task<bool> StartAsync()
    {
        if (IsAcquiring)
        {
            ShowStatus("采集正在进行中：运行中禁止重 Start（先 Stop 再启动）。");
            return false;
        }

        if (_connection is not { CanStart: true } || _connection.LoadedResult is not { } loaded)
        {
            ShowStatus("无法启动采集：A2L 未加载或未选择通道。");
            return false;
        }

        var channel = _connection.SelectedChannel?.Channel;
        if (channel is null)
        {
            ShowStatus("无法启动采集：选中通道没有底层 ICanChannel 实例。");
            return false;
        }

        // XCP_ON_CAN 声明 CAN ID（主站发送 ID 的唯一声明侧来源，不猜）。
        if (loaded.IfData.OnCan.Count == 0)
        {
            ShowStatus("无法启动采集：A2L 无 XCP_ON_CAN 块，无法确定 CAN ID。");
            return false;
        }
        var onCan = loaded.IfData.OnCan[0];

        IXcpTransport transport;
        XcpAcquisitionSession session;
        try
        {
            transport = _transportFactory(channel);
            var masterOptions = _masterOptionsOverride ?? new XcpMasterOptions(
                new CanId(onCan.MasterCanIdRaw, FrameFormatOf(onCan.MasterCanIdRaw)));
            session = new XcpAcquisitionSession(
                transport, masterOptions, ComposeSessionOptions(), _timeProvider);
        }
        catch (Exception ex)
        {
            ShowStatus($"无法启动采集：会话构造失败（{ex.GetType().Name}: {ex.Message}）。");
            return false;
        }

        try
        {
            // ---- ① 对账：CONNECT + 能力实测 vs A2L 声明（不匹配即停，宁可不采）----
            var measured = await MeasureCapabilitiesAsync(session.Master, onCan, CancellationToken.None);
            var report = XcpCapabilityReconciler.Reconcile(
                loaded.IfData, loaded.ValidationNotes, measured);
            CapabilityReport = report;
            if (report.RejectedStart)
            {
                ShowStatus(report.Findings);
                StatusLines.Add($"对账拒绝启动：{report.Findings.Count(f => f.Severity == XcpCapabilitySeverity.Reject)} 项硬约束不匹配。");
                await DisposeSessionAsync(session, transport);
                return false;
            }

            // 告警明细也要浮出（spec：告警 = 采集仍可启动的事实清单，不得静默）。
            StatusLines.Clear();
            foreach (var warning in report.Warnings)
                StatusLines.Add($"[Warning] {warning.Code}: {warning.Message}");

            // ---- ② Plan（纯规划；未翻译地址等 fail-loud 由 planner/轮转负责）----
            var plan = session.Plan(loaded.Contracts, AcquisitionPlan.Build(loaded.Document));

            // ---- ③ 覆盖清单直读（三列：Covered/PollingCause/MissingCause）----
            var coverage = session.ComputeCoverage(loaded.Contracts, plan);
            CoverageEntries.Clear();
            foreach (var entry in coverage)
                CoverageEntries.Add(entry);

            // ---- ④ ConfigureRotation（stop → 重写 → start）----
            await session.ConfigureRotationAsync(plan, ct: CancellationToken.None);

            // ---- ⑤ 轮询循环（Sink 已随 session options 接入 T4 卡片管线）----
            _session = session;
            _transport = transport;
            _pollCts = new CancellationTokenSource();
            _pollLoop = Task.Run(() => RunPollingLoopAsync(session, _pollCts.Token));

            _connection.MarkConnected();
            IsAcquiring = true;
            StatusLines.Add(
                $"采集已启动：DAQ ODT {plan.Odts.Count} 个，轮询降级 {plan.PollingEntries.Count} 条，覆盖 {coverage.Count} 对象。");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusLines.Clear();
            StatusLines.Add($"无法启动采集：{ex.GetType().Name}: {ex.Message}");
            CapabilityReport = null;
            await DisposeSessionAsync(session, transport);
            return false;
        }
    }

    /// <summary>
    /// D6 Stop：停表（取消轮询并 await 会话静默——S2 gate/quiesce 契约的调用侧义务）
    /// → Dispose 会话 → MarkDisconnected。幂等：未启动时调用直接返回。
    /// </summary>
    [RelayCommand]
    public async Task StopAsync()
    {
        if (!IsAcquiring)
            return;

        IsAcquiring = false;
        var cts = _pollCts;
        var loop = _pollLoop;
        var session = _session;
        var transport = _transport;
        _pollCts = null;
        _pollLoop = null;
        _session = null;
        _transport = null;

        if (cts is not null)
            cts.Cancel();
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch { /* 静默收尾：循环内部已归因，Dispose 前 await 静默即可 */ }
        }
        cts?.Dispose();

        await DisposeSessionAsync(session, transport);

        _connection?.MarkDisconnected();
        StatusLines.Clear();
        StatusLines.Add("采集已停止，会话已释放（可再次 Start）。");
    }

    // ------------------------------------------------------------------
    // 对账：CONNECT + 能力实测（XcpProbeCommand 实测口径，零 DOWNLOAD 探测）
    // ------------------------------------------------------------------

    private async Task<XcpMeasuredCapabilities> MeasureCapabilitiesAsync(
        XcpMaster master, XcpOnCan onCan, CancellationToken ct)
    {
        var maxObservedResponseLength = 0;
        var measuredCommands = new List<string>();

        async Task<byte[]> SendAsync(XcpCtoFrame command)
        {
            var response = await master.SendAsync(command, ct).ConfigureAwait(false);
            if (response.Length > maxObservedResponseLength)
                maxObservedResponseLength = response.Length;
            return response;
        }

        // CONNECT 失败即断链：不进对账（无实测可比）。
        _ = XcpResponseDecoder.Connect(await SendAsync(XcpCommandEncoder.Connect()).ConfigureAwait(false));

        // 能力查询全链（probe 同口径）：单项失败不终止——用默认值进对账，
        // 硬约束字段"实测缺"会以拒绝/失配形状浮出，绝不静默放行。
        var (processor, okProcessor) = await TryQueryAsync(
            SendAsync, XcpCommandEncoder.GetDaqProcessorInfo(), response => XcpResponseDecoder.GetDaqProcessorInfo(response));
        if (okProcessor) measuredCommands.Add("GET_DAQ_PROCESSOR_INFO");
        var (resolution, okResolution) = await TryQueryAsync(
            SendAsync, XcpCommandEncoder.GetDaqResolutionInfo(), response => XcpResponseDecoder.GetDaqResolutionInfo(response));
        if (okResolution) measuredCommands.Add("GET_DAQ_RESOLUTION_INFO");
        var (listInfo, okList) = await TryQueryAsync(
            SendAsync, XcpCommandEncoder.GetDaqListInfo(0), response => XcpResponseDecoder.GetDaqListInfo(response));
        if (okList) measuredCommands.Add("GET_DAQ_LIST_INFO");
        var (eventInfo, okEvent) = await TryQueryAsync(
            SendAsync, XcpCommandEncoder.GetDaqEventInfo(0), response => XcpResponseDecoder.GetDaqEventInfo(response));
        if (okEvent) measuredCommands.Add("GET_DAQ_EVENT_INFO");
        var (_, okComm) = await TryQueryAsync(
            SendAsync, XcpCommandEncoder.GetCommModeInfo(), response => XcpResponseDecoder.GetCommModeInfo(response));
        if (okComm) measuredCommands.Add("GET_COMM_MODE_INFO");

        // OPTIONAL_CMD 逐命令良性探测（probe 同款帧，0 效应）。
        // DOWNLOAD 不探测：spec §1 DOWNLOAD 零入口——声明了 DOWNLOAD 的 A2L
        // 会以 COMMAND_DECLARED_NOT_MEASURED 拒绝启动，这是设计内行为。
        var probes = new (string Name, XcpCtoFrame Frame)[]
        {
            ("SET_MTA", XcpCommandEncoder.SetMta(0x00, 0x00000000)),
            ("UPLOAD", XcpCommandEncoder.Upload(1)),
            ("SHORT_UPLOAD", XcpCommandEncoder.ShortUpload(1, 0x00000000, 0x00)),
            ("SET_DAQ_PTR", XcpCommandEncoder.SetDaqPtr(0x00, 0x00000000)),
            ("WRITE_DAQ", XcpCommandEncoder.WriteDaq(0, 1, 0x00, 0x00000000)),
            ("CLEAR_DAQ_LIST", XcpCommandEncoder.ClearDaqList(0x00, 0)),
            ("START_STOP_DAQ_LIST", XcpCommandEncoder.StartStopDaqList(0x00, 0)),
            ("START_STOP_SYNCH", XcpCommandEncoder.StartStopSynch()),
        };
        foreach (var (name, frame) in probes)
        {
            try
            {
                await SendAsync(frame).ConfigureAwait(false);
            }
            catch (XcpErrorResponseException ex) when (ex.Response.Code == XcpError.CmdUnknown)
            {
                continue; // 从机不支持该命令：不进实测清单，交对账按声明比对。
            }

            measuredCommands.Add(name);
        }

        return new XcpMeasuredCapabilities(
            MaxDaq: processor.MaxDaq,
            MaxEventChannel: processor.MaxEventChannel,
            MinDaq: processor.MinDaq,
            MaxOdt: listInfo.MaxOdt,
            MaxCto: (byte)Math.Max(maxObservedResponseLength, 1),
            MaxDto: (byte)XcpCtoFrame.MaxByteLength,
            MaxOdtEntrySizeDaq: resolution.MaxOdtEntrySizeDaq,
            EventPeriodMicroseconds: XcpWireTimeUnit.TryConvertMicroseconds(
                eventInfo.EventCycle, eventInfo.EventChannelTimeUnit, out var measuredPeriodUs)
                ? measuredPeriodUs
                : null,
            OptionalCommands: measuredCommands,
            SlaveCanIdRaw: onCan.SlaveCanIdRaw,
            MasterCanIdRaw: onCan.MasterCanIdRaw);
    }

    /// <summary>单项能力查询的独立异常边界（probe TryQueryAsync 同款语义：不终止链）。</summary>
    private static async Task<(T Value, bool Ok)> TryQueryAsync<T>(
        Func<XcpCtoFrame, Task<byte[]>> send,
        XcpCtoFrame command,
        Func<byte[], T> decode)
    {
        try
        {
            return (decode(await send(command).ConfigureAwait(false)), true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (default!, false);
        }
    }

    // ------------------------------------------------------------------
    // 生命周期内部件
    // ------------------------------------------------------------------

    /// <summary>组合根 options：透传用户参数，Sink 由本 VM 注入（三件套同装义务）。</summary>
    private XcpAcquisitionSessionOptions ComposeSessionOptions() => new()
    {
        Polling = _sessionOptions?.Polling,
        Rotation = _sessionOptions?.Rotation,
        GapNotifier = _sessionOptions?.GapNotifier,
        Sink = _sink,
    };

    /// <summary>
    /// 轮询循环（T12 同口径：首拍前先等一个周期；拍间延迟同周期）。
    /// 取消/会话释放路径静默退出——归因已由调度层出站，不在此二次处理。
    /// </summary>
    private async Task RunPollingLoopAsync(XcpAcquisitionSession session, CancellationToken ct)
    {
        var period = _sessionOptions?.Polling?.Period ?? new PollingSchedulerOptions().Period;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(period, _timeProvider, ct).ConfigureAwait(false);
                await session.PollBeatAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>会话 + transport 释放（transport 不拥有底层 channel，不 Dispose channel）。</summary>
    private static async Task DisposeSessionAsync(XcpAcquisitionSession? session, IXcpTransport? transport)
    {
        session?.Dispose();
        if (transport is not null)
            await transport.DisposeAsync().ConfigureAwait(false);
    }

    private void ShowStatus(IEnumerable<XcpCapabilityFinding> findings)
    {
        StatusLines.Clear();
        foreach (var finding in findings)
            StatusLines.Add($"[{finding.Severity}] {finding.Code}: {finding.Message}");
    }

    private void ShowStatus(string line)
    {
        StatusLines.Clear();
        StatusLines.Add(line);
    }

    /// <summary>CAN ID 帧格式（probe FrameFormatOf 同口径：> 0x7FF = 扩展帧）。</summary>
    private static FrameFormat FrameFormatOf(uint canIdRaw) =>
        canIdRaw > 0x7FF ? FrameFormat.Extended : FrameFormat.Standard;

    /// <summary>
    /// CA1001 收尾口：T8 AppHostBuilder 接线时作为 singleton 挂到应用关闭路径。
    /// 会话仍在跑则同步排干 StopAsync（阻塞仅发生在应用关闭线程，采集命令层
    /// 有界超时，不会无限等待）。
    /// </summary>
    public void Dispose()
    {
        
        GC.SuppressFinalize(this);if (!IsAcquiring)
            return;
        StopAsync().GetAwaiter().GetResult();
    }
}









