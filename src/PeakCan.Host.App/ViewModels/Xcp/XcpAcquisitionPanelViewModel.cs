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
/// 能力实测链（S3-T7b / D7 裁决）：下沉 Core <see cref="PeakCan.Host.Core.Xcp.Capability.XcpCapabilityProber"/>
/// 单源消费（CLI probe 同口径），含 0 字节 DOWNLOAD 良性探测（BYTE_COUNT=0，无数据可写，从机零效应——
/// 良性探测 ≠ 写流量，"DOWNLOAD 零入口"约束的是写数据路径）。App 层零 XcpCommandEncoder 引用，T10 守卫自然成立。
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

    /// <summary>
    /// H1 启动占位（SemaphoreSlim Wait(0) 语义）：入口原子占位——"启动中"（对账/规划/
    /// 轮转在途）与"运行中"都纳入门禁，并发第二个 StartAsync 立即拒绝；占位在退出
    /// 路径必然释放（含启动失败）。
    /// </summary>
    private readonly SemaphoreSlim _startGate = new(1, 1);

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

    /// <summary>S5-T4：当前采集会话的协议主站（写回共用同一连接；未采集 null）。</summary>
    public PeakCan.Host.Core.Xcp.Protocol.XcpMaster? ActiveMaster => _session?.Master;

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
        // H1：入口原子占位（Wait(0)，不等待）——并发第二个 StartAsync 立即拒绝；
        // 占位由 finally 统一释放（含启动失败路径）。
        if (!await _startGate.WaitAsync(0).ConfigureAwait(true))
        {
            ShowStatus("采集正在启动或运行中：并发启动被拒绝（占位门禁）。");
            return false;
        }

        try
        {
            return await StartCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>Start 主体（调用时已持有启动占位）。</summary>
    private async Task<bool> StartCoreAsync()
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

        IXcpTransport? transport = null;
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
            // M3（T7 评审）：会话构造失败也必须释放已构造的 transport（XcpMaster 事件
            // 订阅失败等路径），否则底层 channel 包装泄漏。
            if (transport is not null)
                await transport.DisposeAsync().ConfigureAwait(true);
            ShowStatus($"无法启动采集：会话构造失败（{ex.GetType().Name}: {ex.Message}）。");
            return false;
        }

        try
        {
            // ---- ① 对账：CONNECT + 能力实测 vs A2L 声明（不匹配即停，宁可不采）----
            // S3-T7b（D7）：实测链改调 Core XcpCapabilityProber（含 0 字节 DOWNLOAD
            // 良性探测，CLI probe 同源）；App 层 XcpCommandEncoder 引用清零。
            // L1：实测 CAN ID = 本 VM 传入的声明值（自证）——A-4 CAN_ID_MISMATCH 告警
            // 在 VM 路径退化为恒不触发（声明与实际使用天然一致）；声明 vs 线上真实 ID
            // 的独立核对归 CLI probe / T19 台架。
            var probe = await XcpCapabilityProber.ProbeAsync(
                session.Master, onCan.MasterCanIdRaw, onCan.SlaveCanIdRaw, CancellationToken.None);
            var measured = probe.Measured;
            var report = XcpCapabilityReconciler.Reconcile(
                loaded.IfData, loaded.ValidationNotes, measured);
            CapabilityReport = report;
            if (report.RejectedStart)
            {
                // L5（T7 评审）：拒绝路径必须清空旧覆盖清单——残留行会谎报本次采集覆盖面。
                CoverageEntries.Clear();
                ShowStatus(report.Findings);
                AppendQueryFailures(probe.QueryFailures);
                StatusLines.Add($"对账拒绝启动：{report.Findings.Count(f => f.Severity == XcpCapabilitySeverity.Reject)} 项硬约束不匹配。");
                await DisposeSessionAsync(session, transport);
                return false;
            }

            // 告警明细也要浮出（spec：告警 = 采集仍可启动的事实清单，不得静默）。
            StatusLines.Clear();
            foreach (var warning in report.Warnings)
                StatusLines.Add($"[Warning] {warning.Code}: {warning.Message}");
            AppendQueryFailures(probe.QueryFailures);

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
            // L5（T7 评审）：失败路径同样清空旧覆盖清单，与对账拒绝口径一致。
            CoverageEntries.Clear();
            StatusLines.Clear();
            StatusLines.Add($"无法启动采集：{ex.GetType().Name}: {ex.Message}");
            CapabilityReport = null;
            await DisposeSessionAsync(session, transport);
            return false;
        }
    }

    /// <summary>
    /// S4-T5（spec D5）：StopAsync 入口回调——先停记录再停采集，保证尾部样本落盘。
    /// 组合根/编排器接线到记录面板的 StopBeforeAcquisitionAsync；null = 无前置动作。
    /// </summary>
    public Func<Task>? BeforeStopAsync { get; set; }

    /// <summary>
    /// D6 Stop：停表（取消轮询并 await 会话静默——S2 gate/quiesce 契约的调用侧义务）
    /// → Dispose 会话 → MarkDisconnected。幂等：未启动时调用直接返回。
    /// </summary>
    [RelayCommand]
    public async Task StopAsync()
    {
        if (!IsAcquiring)
            return;

        if (BeforeStopAsync is { } beforeStop)
            await beforeStop().ConfigureAwait(true);

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
            try { await loop.ConfigureAwait(true); }
            catch { /* 静默收尾：循环内部已归因，Dispose 前 await 静默即可 */ }
        }
        cts?.Dispose();

        await DisposeSessionAsync(session, transport);

        _connection?.MarkDisconnected();
        StatusLines.Clear();
        StatusLines.Add("采集已停止，会话已释放（可再次 Start）。");
    }

    // ------------------------------------------------------------------
    // 状态区辅助
    // ------------------------------------------------------------------

    /// <summary>
    /// L2（T7 评审）：实测链单项查询失败归因出站——XcpCapabilityProber 的归因清单
    /// 原样转成状态区人读行（拒绝/放行两条路径都要浮出，不得静默）。
    /// </summary>
    private void AppendQueryFailures(IReadOnlyList<XcpCapabilityQueryFailure> failures)
    {
        foreach (var failure in failures)
            StatusLines.Add($"[ProbeFailure] {failure.Command}: {failure.ExceptionType} ({failure.Marker})");
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
                await Task.Delay(period, _timeProvider, ct).ConfigureAwait(true);
                await session.PollBeatAsync(ct).ConfigureAwait(true);
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
            await transport.DisposeAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 关闭路径静默开关（T8 评审 M2）：host.StopAsync 无 SyncContext，关闭期
    /// StopAsync 的延续落线程池——此时写 StatusLines 会触发跨线程
    /// CollectionChanged 异常且被 shutdown wrapper 吞掉（误导日志）。
    /// Shutdown service 在调 StopAsync 前置 true，跳过全部状态区刷新。
    /// </summary>
    internal bool SuppressStatusOutput { get; set; }

    private void ShowStatus(IEnumerable<XcpCapabilityFinding> findings)
    {
        if (SuppressStatusOutput) return;
        StatusLines.Clear();
        foreach (var finding in findings)
            StatusLines.Add($"[{finding.Severity}] {finding.Code}: {finding.Message}");
    }

    private void ShowStatus(string line)
    {
        if (SuppressStatusOutput) return;
        StatusLines.Clear();
        StatusLines.Add(line);
    }

    /// <summary>CAN ID 帧格式（probe FrameFormatOf 同口径：> 0x7FF = 扩展帧）。</summary>
    private static FrameFormat FrameFormatOf(uint canIdRaw) =>
        canIdRaw > 0x7FF ? FrameFormat.Extended : FrameFormat.Standard;

    /// <summary>
    /// CA1001 收尾口：T8 AppHostBuilder 接线时作为 singleton 挂到应用关闭路径（Shutdown）。
    /// H2 后 VM 内 await 全部 ConfigureAwait(true)（留在 Dispatcher）：Dispatcher 上下文下
    /// 同步等待 StopAsync 会死锁（其延续需回到正被阻塞的 Dispatcher），故应用关闭路径
    /// 采用 fire-and-forget 语义（丢弃返回的 Task——Stop 内部自持取消/释放流程，不依赖
    /// 调用方 await）；无 SyncContext 的测试/控制台路径保持同步排干，便于断言。
    /// </summary>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (!IsAcquiring)
            return;

        if (SynchronizationContext.Current is not null)
        {
            _ = StopAsync(); // fire-and-forget（L3/T8 接线挂 Shutdown，见上注）。
            return;
        }

        StopAsync().GetAwaiter().GetResult();
    }
}









