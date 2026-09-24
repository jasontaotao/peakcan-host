using A2lEditor.Core.Layout;
using PeakCan.Host.Core.Xcp.Abstractions;
using PeakCan.Host.Core.Xcp.Protocol;
using PeakCan.Host.Core.Xcp.Receive;

namespace PeakCan.Host.Core.Xcp.Scheduling;

/// <summary>
/// 采集覆盖条目（S2-T13 组合根出站，spec §5 验收 2）：
/// <list type="bullet">
/// <item>Covered = planner 自产方案承接（DAQ 打包或轮询降级集合二选一）。</item>
/// <item>PollingCause = planner 自产降级归因（ObjectTooLarge / NotByteAlignedClass /
/// DaqCapacityExceeded），DAQ 承接对象恒 null。</item>
/// <item>MissingCause = <b>包侧</b> A2lEditor.Core.Layout.MissingCause——仅对 planner
/// 未承接的对象出值（合同 Notes 首因，兜底 NotAcquired）；语义与 planner 归因枚举
/// 不复用（T9 定案）。</item>
/// </list>
/// 覆盖计算只消费 ContractSet + <see cref="PlannedAcquisitionMap"/>（planner 自产），
/// 与 MAP↔A2L 地址回填对账服务（IMapAlignmentService）零关系（spec §5 明令）。
/// </summary>
public sealed record AcquisitionCoverageEntry(
    string ObjectName,
    bool Covered,
    PlannedPollingCause? PollingCause,
    MissingCause? MissingCause);

/// <summary>组合根参数（全部可空注入，null = 各组件默认值）。</summary>
public sealed class XcpAcquisitionSessionOptions
{
    /// <summary>轮询节奏参数（低频兜底；周期默认 1s）。</summary>
    public PollingSchedulerOptions? Polling { get; init; }

    /// <summary>轮转状态机参数（恢复上限 / quiesce 间隙）。</summary>
    public RotationSchedulerOptions? Rotation { get; init; }

    /// <summary>计划空窗通知接收口（stop 后、首条重写前触发，T11 挂点）。</summary>
    public IXcpPlanGapNotifier? GapNotifier { get; init; }

    /// <summary>
    /// Receive 三件套接入口（T16，spec §5 验收 2；T15 评审钉死「必须同装」）：非 null 时
    /// 组合根在构造期装配 <see cref="PlanGapWatcher"/>（gapNotifier）、在
    /// <see cref="XcpAcquisitionSession.Plan"/> 时装配 <see cref="XcpReceiveLoop"/>，
    /// 其 SampleDecoded/Attributed 两个出站口经 <see cref="XcpAcquisitionSinkWiring"/>
    /// 全部接到本 sink——三件套缺一即拒装。null（默认）= 不接 Receive 层，保持
    /// T13 纯调度行为。
    /// </summary>
    public IXcpAcquisitionSink? Sink { get; init; }
}

/// <summary>
/// S2-T13 调度层组合根（spec §3 Scheduling）：把 T9 规划 → T11 轮转 → T12 轮询
/// 按既有组件契约组装成一个「规划 + 配置轮转 + 跑 N 拍轮询」的会话。
/// <list type="bullet">
/// <item>零策略复制：planner/轮转/轮询的顺序、失败路径、fail-loud 语义全部留在
/// 各组件内（本类不改 T9/T11/T12 产出文件）；本类只做组装与 gate 接线。</item>
/// <item>gate 接线顺序契约（T12 <see cref="PollingScheduler.WaitForQuietAsync"/> XML doc
/// 钉死）：置位 <see cref="IXcpRotationGate"/> → <c>WaitForQuietAsync</c> →
/// <c>ConfigureRotationAsync</c> → finally 复位。让位检查（gate 置位后）拦住后续新拍，
/// 握手封住已开跑的最后一拍，finally 复位覆盖异常路径。</item>
/// <item>轮询拍复用 <see cref="PollingScheduler.PollOnceAsync"/>（单飞/让位/失败归因
/// 均为 T12 语义）；RunPollingAsync 的拍间延迟与 T12 RunAsync 同口径：首拍前先等
/// 一个周期，经注入 TimeProvider 计时。</item>
/// <item>采集覆盖（<see cref="ComputeCoverage"/>）：planner 自产方案 + 包侧
/// MissingCause，不经 IMapAlignmentService（spec §5 验收 2）。</item>
/// <item>Receive 三件套（T16，spec §5 验收 2）：options.Sink 非 null 时，gapNotifier=
/// PlanGapWatcher（构造期）、SampleDecoded=sink 接线（先关窗后入队）、Attributed=sink
/// 归因接线（包侧 MissingCause 五值并入）三件同装于 Plan 时新建的 XcpReceiveLoop；
/// 重 Plan 即替换 loop（旧实例 Dispose）。Sink 为 null 时三件全不装（T13 原行为）。</item>
/// <item>DOWNLOAD 禁用（spec §0 / 决策 D2）：S2 编解码已实现但调度层无任何
/// DOWNLOAD 发送路径——本类不引用 XcpCommandEncoder 的 Download 命令，线上零 DOWNLOAD
/// 由 e2e 的 Spy 断言与 T18 静态守卫共同钉住。</item>
/// <item>并发模型：单轮转驱动 + 单轮询循环并发受支持（gate/WaitForQuiet 为此存在，XcpMaster 单发单收）；重复 Configure / 重 Plan / Dispose 不得与在途操作并发——Plan 重跑会替换
/// 轮询调度器（旧实例 Dispose）。Dispose 与 master 生命周期同管。</item>
/// </list>
/// </summary>
public sealed class XcpAcquisitionSession : IDisposable
{
    private readonly XcpMaster _master;
    private readonly XcpMasterOptions _masterOptions;
    private readonly RotationScheduler _rotation;
    private readonly XcpAcquisitionSessionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IXcpTransport _transport;
    private readonly RotationGate _gate = new();
    private PollingScheduler? _polling;
    private PlanGapWatcher? _gapWatcher;
    private XcpReceiveLoop? _receiveLoop;
    private bool _disposed;

    /// <summary>协议引擎（CONNECT/能力对账等由调用方在会话外驱动；本类只管采集调度）。</summary>
    public XcpMaster Master => _master;

    /// <summary>轮转状态机当前表运行态（透传 T11；只随 START_STOP 正应答迁移）。</summary>
    public RotationTableState RotationTableState => _rotation.TableState;

    /// <summary>
    /// Receive 线程（T16 接线证据；Sink 未配置或尚未 Plan 时为 null）。
    /// 只读暴露：loop 归组合根生命周期管，调用方不得 Dispose。
    /// </summary>
    public XcpReceiveLoop? ReceiveLoop => _receiveLoop;

    public XcpAcquisitionSession(
        IXcpTransport transport,
        XcpMasterOptions masterOptions,
        XcpAcquisitionSessionOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? new XcpAcquisitionSessionOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _masterOptions = masterOptions ?? throw new ArgumentNullException(nameof(masterOptions));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));

        // Receive 三件套之一（T16/T15 评审义务）：sink 在场即装配 watcher，并作为
        // 轮转的空窗通知口；调用方另给 GapNotifier 时 fan-out 兼容（两者都收到通知）。
        IXcpPlanGapNotifier? gapNotifier = _options.GapNotifier;
        if (_options.Sink is not null)
        {
            _gapWatcher = new PlanGapWatcher(_options.Sink, _timeProvider);
            gapNotifier = gapNotifier is null
                ? _gapWatcher
                : new FanOutGapNotifier(_gapWatcher, gapNotifier);
        }

        _master = new XcpMaster(_transport, masterOptions, _timeProvider);
        _rotation = new RotationScheduler(_master, masterOptions, _options.Rotation, gapNotifier, _timeProvider);
    }

    /// <summary>
    /// 规划采集方案（T9）。纯规划：轮询调度器惰性绑定到首次使用的方案（
    /// <see cref="ConfigureRotationAsync"/>）——未翻译地址的方案（fail-loud 前置）
    /// 也能先做 <see cref="ComputeCoverage"/> 覆盖对账，把"缺什么"说出口后再拒绝上线。
    /// 重复调用以最后一次方案为准（旧轮询绑定作废）。
    /// </summary>
    public PlannedAcquisitionMap Plan(ContractSet contracts, AcquisitionPlan placeholders)
    {
        ThrowIfDisposed();
        var plan = AcquisitionPlanner.Plan(contracts, placeholders);
        _polling?.Dispose();
        _polling = null;

        // Receive 三件套之二/三（T16）：方案产出即反查索引就位——loop 的
        // SampleDecoded/Attributed 两个出站口全部接到 sink（先关计划空窗再入队 /
        // 包侧 MissingCause 五值并入），不接即静默黑洞（T14 fail-loud 同源纪律）。
        if (_options.Sink is not null)
        {
            _receiveLoop?.Dispose();
            _receiveLoop = new XcpReceiveLoop(_transport, new XcpReceiveOptions
            {
                Map = plan,
                Contracts = contracts,
                SampleDecoded = XcpAcquisitionSinkWiring.SampleDecoded(_options.Sink, _gapWatcher),
                Attributed = XcpAcquisitionSinkWiring.Attributed(_options.Sink),
            });
        }

        return plan;
    }

    /// <summary>
    /// 配置轮转一轮（T11：stop → 重写 → start），前置 T12 gate 接线顺序契约：
    /// 置位 gate → <see cref="PollingScheduler.WaitForQuietAsync"/> →
    /// <see cref="RotationScheduler.ConfigureRotationAsync"/> → finally 复位。
    /// 尚未绑定轮询调度器（未 <see cref="Plan"/> 过）时按传入方案惰性创建（握手需要拍互斥闩）。
    /// </summary>
    public async Task<RotationResult> ConfigureRotationAsync(
        PlannedAcquisitionMap plan,
        IRotationRecoveryPolicy? recoveryPolicy = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        _polling ??= new PollingScheduler(_master, _masterOptions, plan, _options.Polling, _gate, _timeProvider);

        // T12 契约钉死顺序：置位 gate → WaitForQuietAsync → ConfigureRotationAsync → finally 复位。
        _gate.Set();
        try
        {
            await _polling.WaitForQuietAsync(ct).ConfigureAwait(false);
            return await _rotation.ConfigureRotationAsync(plan, recoveryPolicy, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Reset();
        }
    }

    /// <summary>跑一拍轮询（T12 PollOnceAsync 原样出站：让位跳过 / 无条目 / 已执行）。</summary>
    public Task<PollingCycleResult> PollBeatAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return RequirePolling().PollOnceAsync(ct);
    }

    /// <summary>
    /// 跑 N 拍轮询并逐拍出站结果。拍间延迟与 T12 RunAsync 同口径（首拍前先等一个
    /// 周期，经注入 TimeProvider 计时）；条目级失败不终止（T12 语义），仅取消外溢。
    /// </summary>
    public async Task<IReadOnlyList<PollingCycleResult>> RunPollingAsync(int beats, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(beats);
        var polling = RequirePolling();
        var results = new List<PollingCycleResult>(beats);
        for (var i = 0; i < beats; i++)
        {
            await Task.Delay(polling.Period, _timeProvider, ct).ConfigureAwait(false);
            results.Add(await polling.PollOnceAsync(ct).ConfigureAwait(false));
        }
        return results;
    }

    /// <summary>
    /// 采集覆盖全量对账（spec §5 验收 2）：逐合同对象判定 planner 方案承接情况。
    /// 只消费 <see cref="ContractSet"/> 与 planner 自产 <see cref="PlannedAcquisitionMap"/>；
    /// 未承接对象的归因取包侧 MissingCause（合同 Notes 首因，兜底 NotAcquired），
    /// 与 IMapAlignmentService（MAP↔A2L 地址回填对账）零关系。
    /// </summary>
    public IReadOnlyList<AcquisitionCoverageEntry> ComputeCoverage(ContractSet contracts, PlannedAcquisitionMap plan)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        ArgumentNullException.ThrowIfNull(plan);
        ThrowIfDisposed();

        var daqObjects = new HashSet<string>(
            plan.Odts.SelectMany(o => o.Entries).Select(e => e.ObjectName), StringComparer.Ordinal);
        var pollingCauses = plan.PollingEntries
            .GroupBy(e => e.ObjectName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Cause, StringComparer.Ordinal);

        var coverage = new List<AcquisitionCoverageEntry>(contracts.All.Count);
        foreach (var contract in contracts.All)
        {
            if (daqObjects.Contains(contract.ObjectName))
            {
                coverage.Add(new AcquisitionCoverageEntry(contract.ObjectName, Covered: true, PollingCause: null, MissingCause: null));
                continue;
            }
            if (pollingCauses.TryGetValue(contract.ObjectName, out var pollingCause))
            {
                coverage.Add(new AcquisitionCoverageEntry(contract.ObjectName, Covered: true, pollingCause, MissingCause: null));
                continue;
            }

            // planner 未承接：归因用包侧 MissingCause（合同 Notes 首因；兜底 NotAcquired）。
            var missing = contract.Notes.Count > 0 ? contract.Notes[0].Cause : MissingCause.NotAcquired;
            coverage.Add(new AcquisitionCoverageEntry(contract.ObjectName, Covered: false, PollingCause: null, missing));
        }
        return coverage;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _receiveLoop?.Dispose();
        _gapWatcher?.Dispose();
        _polling?.Dispose();
        _master.Dispose();
    }

    private PollingScheduler RequirePolling() =>
        _polling
            ?? throw new InvalidOperationException(
                "XcpAcquisitionSession.Polling requires ConfigureRotationAsync(map) to lazily bind the polling scheduler first (Plan() alone does not bind).");

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>组合根持有的多路空窗通知 fan-out（watcher + 调用方 GapNotifier 并存时）。</summary>
    private sealed class FanOutGapNotifier(params IXcpPlanGapNotifier[] targets) : IXcpPlanGapNotifier
    {
        public void OnPlanGapWindow(RotationGapWindow window)
        {
            foreach (var target in targets)
            {
                try
                {
                    target.OnPlanGapWindow(window);
                }
                catch
                {
                    // 同 PlanGapWatcher 通知契约（T11/T15 同源纪律）：不得抛异常——
                    // 穿透会让 ConfigureRotationAsync 在 stop 后裸中断、表停无归因。
                    // 吞掉并放弃"该目标该条"，其余目标照常收到通知（次序无关）。
                }
            }
        }
    }

    /// <summary>组合根持有的 gate 实现（T12 让位观察口；置位/复位只发生在轮转接线内）。</summary>
    private sealed class RotationGate : IXcpRotationGate
    {
        public bool IsRotationInProgress { get; private set; }

        public void Set() => IsRotationInProgress = true;

        public void Reset() => IsRotationInProgress = false;
    }
}
