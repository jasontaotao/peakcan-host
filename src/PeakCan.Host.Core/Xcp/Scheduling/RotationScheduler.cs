using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Xcp.Scheduling;

/// <summary>轮转状态机的 DAQ 表运行态（只随 START_STOP_DAQ_LIST 正应答迁移）。</summary>
public enum RotationTableState
{
    /// <summary>表处于 stop 态（初值；失败路径归因值，spec §1）。</summary>
    Stopped,

    /// <summary>表处于 start 态（采集运行中）。</summary>
    Running,
}

/// <summary>轮转状态机阶段（失败归因用）。</summary>
public enum RotationPhase
{
    /// <summary>START_STOP_DAQ_LIST(mode 0) 停表阶段。</summary>
    Stopping,

    /// <summary>SET_DAQ_PTR + WRITE_DAQ 条目重写阶段。</summary>
    Rewriting,

    /// <summary>START_STOP_DAQ_LIST(mode 1) 起表阶段。</summary>
    Starting,
}

/// <summary>重写失败后的恢复动作（spec §1：重试重写或整表重建，注入选择）。</summary>
public enum RotationRecoveryAction
{
    /// <summary>原位重发失败条目的 SET_DAQ_PTR + WRITE_DAQ。</summary>
    RetryRewrite,

    /// <summary>弃当前进度，从 ODT 0 整表重写（表保持 stop 态）。</summary>
    RebuildTable,
}

/// <summary>
/// 轮转失败归因值（spec §1 失败路径）。ErrorCode 为 null 表示非负响应失败
/// （T1 超时 / 写帧失败），Detail 携带原始消息。
/// </summary>
public sealed record RotationFailure(
    RotationPhase Phase,
    ushort OdtIndex,
    ushort EntryIndex,
    XcpError? ErrorCode,
    string Detail);

/// <summary>轮转（停表换表）结果：终态 + 成功恢复的失败清单。</summary>
public sealed record RotationResult(
    RotationTableState TableState,
    IReadOnlyList<RotationFailure> RecoveredFailures);

/// <summary>
/// 轮转终态失败（恢复耗尽 / stop、start 阶段失败）。
/// <see cref="TableState"/> 是归因值：stop 成功后的失败恒为 Stopped（spec §1）。
/// </summary>
public sealed class RotationFailedException : InvalidOperationException
{
    public RotationFailure Failure { get; }

    public RotationTableState TableState { get; }

    public RotationFailedException(RotationFailure failure, RotationTableState tableState)
        : base($"Rotation failed at phase {failure.Phase} (odt {failure.OdtIndex}, entry {failure.EntryIndex}, " +
               $"error {failure.ErrorCode?.ToString() ?? "n/a"}): table left {tableState}. {failure.Detail}")
    {
        Failure = failure;
        TableState = tableState;
    }
}

/// <summary>重写失败的恢复策略注入点（spec §1：两路径可注入选择）。</summary>
public interface IRotationRecoveryPolicy
{
    /// <summary>对一次重写失败决定恢复动作。轮转器按决定执行，耗尽上限后终态失败。</summary>
    RotationRecoveryAction OnRewriteFailure(RotationFailure failure);
}

/// <summary>固定恢复动作的测试/简单场景策略。</summary>
public sealed class FixedRotationRecoveryPolicy : IRotationRecoveryPolicy
{
    private readonly RotationRecoveryAction _action;

    public FixedRotationRecoveryPolicy(RotationRecoveryAction action) => _action = action;

    public RotationRecoveryAction OnRewriteFailure(RotationFailure failure) => _action;
}

/// <summary>
/// 换表"计划空窗"通知（T11 预留挂点，spec §3 Receive：计划内换表空窗；
/// 生产接线在 T15 Receive 层——本层只在 stop 应答落地后、首条重写前触发）。
/// </summary>
public sealed record RotationGapWindow(
    ushort OdtCount,
    int EntryCount,
    TimeSpan ExpectedMaxDuration);

/// <summary>计划空窗通知接收口。实现方不得抛异常（轮转中途异常会让表停在 stop 态无归因）。</summary>
public interface IXcpPlanGapNotifier
{
    void OnPlanGapWindow(RotationGapWindow window);
}

/// <summary>轮转状态机参数。</summary>
public sealed class RotationSchedulerOptions
{
    /// <summary>
    /// 空窗时长上界的单命令预算（标称值，非协议超时——协议 T1 由 XcpMaster 管理）。
    /// 空窗上界 = 重写+start 命令数 × 本值；T15 生产端据此升级断流判据。
    /// </summary>
    public TimeSpan PerCommandTimeout { get; init; } = XcpMasterOptions.DefaultTimeout;

    /// <summary>单次轮转允许的恢复决定次数上限（防"永远重建"活锁），耗尽即终态失败。</summary>
    public int MaxRecoveryDecisions { get; init; } = 3;
}

/// <summary>
/// S2-T11 停表换表状态机（spec §1 轮转形状，顺序写死）：
/// <list type="bullet">
/// <item>顺序写死：START_STOP_DAQ_LIST(stop) 正应答 → 逐条目 SET_DAQ_PTR +
/// WRITE_DAQ（ODT 升序、条目升序）→ START_STOP_DAQ_LIST(start)。
/// 表运行中绝不发 WRITE_DAQ（从机负响应拒绝，ERR_DAQ_ACTIVE）。</item>
/// <item>模式位语义：仅 mode 0/1 直控 list 0；mode 2(select)+START_STOP_SYNCH
/// 组合路径不使用（编码器层 mode&gt;1 直接拒绝，本层只产 0/1）。</item>
/// <item>失败路径：stop 成功但重写负响应 → 按注入策略恢复（重试重写/整表重建），
/// 恢复耗尽抛 <see cref="RotationFailedException"/>，<see cref="TableState"/>
/// 恒为 Stopped（归因值），绝不带半成品条目 start。</item>
/// <item>单飞：同一实例并发轮转请求直接拒绝（XcpMaster 单发单收，排队无意义）。</item>
/// </list>
/// </summary>
public sealed class RotationScheduler
{
    private readonly XcpMaster _master;
    private readonly RotationSchedulerOptions _options;
    private readonly IXcpPlanGapNotifier? _gapNotifier;
    private int _busy;

    /// <summary>当前 DAQ 表运行态（只随 START_STOP 正应答迁移；失败归因消费）。</summary>
    public RotationTableState TableState { get; private set; } = RotationTableState.Stopped;

    public RotationScheduler(
        XcpMaster master,
        RotationSchedulerOptions? options = null,
        IXcpPlanGapNotifier? gapNotifier = null)
    {
        _master = master ?? throw new ArgumentNullException(nameof(master));
        _options = options ?? new RotationSchedulerOptions();
        _gapNotifier = gapNotifier;
    }

    /// <summary>
    /// 停表换表一轮：stop → 重写全部条目 → start。成功返回终态 Running 与
    /// 成功恢复的失败清单；恢复耗尽抛 <see cref="RotationFailedException"/>。
    /// </summary>
    public async Task<RotationResult> ConfigureRotationAsync(
        PlannedAcquisitionMap plan,
        IRotationRecoveryPolicy? recoveryPolicy = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException(
                "RotationScheduler is single-flight: a rotation is already in progress.");

        try
        {
            // 恢复策略默认重试重写（最小破坏面）；整表重建必须显式注入选择。
            var policy = recoveryPolicy ?? new FixedRotationRecoveryPolicy(RotationRecoveryAction.RetryRewrite);

            // 发线上帧前 fail-loud：地址未翻译/超 32 位线上地址空间的一律拒绝
            //（宁可不采不错采，且绝不把表打停后再发现配不齐条目）。
            Validate(plan);

            await StopAsync(plan, ct).ConfigureAwait(false);
            NotifyPlanGap(plan);
            var failures = await RewriteAsync(plan, policy, ct).ConfigureAwait(false);
            await StartAsync(plan, ct).ConfigureAwait(false);
            return new RotationResult(TableState, failures);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>线上帧前的静态校验：单表 list 0、条目尺寸类、物理地址可上 32 位线上地址。</summary>
    private static void Validate(PlannedAcquisitionMap plan)
    {
        if (plan.DaqNumber != 0)
            throw new InvalidOperationException(
                $"Rotation controls DAQ list 0 only (spec §1); plan declares {plan.DaqNumber}.");
        if (plan.Odts.Count is < 1 or > AcquisitionPlanner.MaxOdts)
            throw new InvalidOperationException(
                $"Plan must declare 1..{AcquisitionPlanner.MaxOdts} ODTs; got {plan.Odts.Count}.");

        foreach (var odt in plan.Odts)
        {
            foreach (var entry in odt.Entries)
            {
                if (entry.ByteLength is < 1 or > AcquisitionPlanner.MaxEntryBytes)
                    throw new InvalidOperationException(
                        $"Entry byte length {entry.ByteLength} violates the 1..{AcquisitionPlanner.MaxEntryBytes} " +
                        $"wire constraint (odt {odt.OdtIndex}, entry {entry.EntryIndex}).");
                if (entry.PhysicalAddress is not { } physical)
                    throw new InvalidOperationException(
                        $"Entry '{entry.ObjectName}' (odt {odt.OdtIndex}, entry {entry.EntryIndex}) has no " +
                        "translated physical address; WRITE_DAQ cannot be issued (spec §1/[H1]).");
                if (physical > uint.MaxValue)
                    throw new InvalidOperationException(
                        $"Physical address 0x{physical:X} of '{entry.ObjectName}' exceeds the 32-bit " +
                        "WRITE_DAQ address space.");
            }
        }
    }

    private async Task StopAsync(PlannedAcquisitionMap plan, CancellationToken ct)
    {
        try
        {
            // mode 0 = stop（协议字段），直控 list 0；mode 2 select 路径 S2 不使用。
            await _master.SendAsync(XcpCommandEncoder.StartStopDaqList(mode: 0x00, plan.DaqNumber), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is XcpErrorResponseException or XcpTimeoutException or InvalidOperationException)
        {
            // stop 失败：表仍在从机侧运行，状态不迁移（归因 Running）。
            throw ToFailure(RotationPhase.Stopping, odt: 0, entry: 0, ex, TableState);
        }

        TableState = RotationTableState.Stopped;
    }

    private async Task StartAsync(PlannedAcquisitionMap plan, CancellationToken ct)
    {
        try
        {
            await _master.SendAsync(XcpCommandEncoder.StartStopDaqList(mode: 0x01, plan.DaqNumber), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is XcpErrorResponseException or XcpTimeoutException or InvalidOperationException)
        {
            // start 失败：条目已写齐但表未上线，归因 Stopped（安全侧）。
            throw ToFailure(RotationPhase.Starting, odt: 0, entry: 0, ex, TableState);
        }

        TableState = RotationTableState.Running;
    }

    /// <summary>
    /// 重写主循环：游标 (odt, entry) 推进；失败时按注入策略决定
    /// RetryRewrite（游标停在失败条目，原位重发）或 RebuildTable（游标归零整表重来）。
    /// 恢复决定次数耗尽抛终态失败（表恒处 stop 态）。
    /// </summary>
    private async Task<List<RotationFailure>> RewriteAsync(
        PlannedAcquisitionMap plan, IRotationRecoveryPolicy policy, CancellationToken ct)
    {
        var failures = new List<RotationFailure>();
        var decisionsLeft = _options.MaxRecoveryDecisions;
        var odt = 0;
        var entry = 0;

        while (true)
        {
            try
            {
                for (; odt < plan.Odts.Count; odt++)
                {
                    var current = plan.Odts[odt];
                    for (; entry < current.Entries.Count; entry++)
                        await WriteEntryAsync(plan, current, current.Entries[entry], ct).ConfigureAwait(false);
                    entry = 0;
                }
                return failures;
            }
            catch (RewriteBailout bailout)
            {
                failures.Add(bailout.Failure);
                if (decisionsLeft == 0)
                    throw new RotationFailedException(bailout.Failure, TableState);
                decisionsLeft--;

                if (policy.OnRewriteFailure(bailout.Failure) == RotationRecoveryAction.RebuildTable)
                {
                    odt = 0;
                    entry = 0;
                }
                // RetryRewrite：游标已停在失败条目上，原位重发。
            }
        }
    }

    private async Task WriteEntryAsync(
        PlannedAcquisitionMap plan, PlannedOdt odt, PlannedDaqEntry entry, CancellationToken ct)
    {
        try
        {
            // SET_DAQ_PTR 指向待写条目（S2 元素指针约定钉死：daq<<16 | odt<<8 | entry）。
            await _master.SendAsync(
                XcpCommandEncoder.SetDaqPtr(
                    addressExtension: 0x00,
                    address: ElementPointer(plan.DaqNumber, odt.OdtIndex, entry.EntryIndex)),
                ct).ConfigureAwait(false);

            // WRITE_DAQ 定义条目：bit offset 0（spec §1 无位偏置）、entry size = 条目宽、
            // 地址为 planner 已翻译的物理地址（uint 上限已 Validate）。
            await _master.SendAsync(
                XcpCommandEncoder.WriteDaq(
                    bitOffset: 0x00,
                    entrySize: (byte)entry.ByteLength,
                    addressExtension: 0x00,
                    address: (uint)entry.PhysicalAddress!.Value),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is XcpErrorResponseException or XcpTimeoutException or InvalidOperationException)
        {
            throw new RewriteBailout(ToFailureValue(RotationPhase.Rewriting, odt.OdtIndex, entry.EntryIndex, ex));
        }
    }

    /// <summary>空窗通知挂点：stop 应答刚落地、首条重写前（T11 测试钉时序，生产端 T15）。</summary>
    private void NotifyPlanGap(PlannedAcquisitionMap plan)
    {
        if (_gapNotifier is null)
            return;

        var entryCount = plan.Odts.Sum(o => o.Entries.Count);
        // 空窗覆盖重写 + start：命令数 = 2×条目 + 1，预算按标称单命令超时线性放大。
        var commandCount = 2 * entryCount + 1;
        var budget = TimeSpan.FromTicks(commandCount * _options.PerCommandTimeout.Ticks);
        _gapNotifier.OnPlanGapWindow(new RotationGapWindow(plan.OdtCount, entryCount, budget));
    }

    private static RotationFailedException ToFailure(
        RotationPhase phase, ushort odt, ushort entry, Exception ex, RotationTableState tableState) =>
        new(ToFailureValue(phase, odt, entry, ex), tableState);

    private static RotationFailure ToFailureValue(RotationPhase phase, ushort odt, ushort entry, Exception ex) =>
        new(phase, odt, entry, CodeOf(ex), ex.Message);

    private static XcpError? CodeOf(Exception ex) =>
        ex is XcpErrorResponseException negative ? negative.Response.Code : null;

    /// <summary>SET_DAQ_PTR 的 DAQ 元素指针（S2 钉死约定，台架 A-11 待核实项见 spec §1）。</summary>
    private static uint ElementPointer(byte daqNumber, ushort odtIndex, ushort entryIndex) =>
        ((uint)daqNumber << 16) | ((uint)odtIndex << 8) | entryIndex;

    /// <summary>重写条目失败的内部跃迁载体（携带归因值上抛给恢复循环）。</summary>
    private sealed class RewriteBailout : Exception
    {
        public RotationFailure Failure { get; }

        public RewriteBailout(RotationFailure failure) => Failure = failure;
    }
}