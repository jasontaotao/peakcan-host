using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Xcp.Scheduling;

/// <summary>
/// DAQ 轮转互斥观察口（T12 定死的让位规则，spec §3 Scheduling「并行互斥」）：
/// 轮询在 DAQ 轮转周期内让位——<see cref="IsRotationInProgress"/> 为 true 期间
/// <see cref="PollingScheduler"/> 整拍跳过（不排队、不等待、不发任何线上帧）。
/// <para>
/// 选择理由（报告同步落纸）：
/// 1. XcpMaster 单发单收（spec §1 无 INTERLEAVED），轮询插队只会在
///    ConfigureRotationAsync 中途抛 pending 冲突，拖垮轮转本身；
/// 2. 轮转空窗上界已由 T11 钉死（RotationGapWindow.ExpectedMaxDuration），
///    轮询插队会拉长空窗并违反换表时序假设；
/// 3. 轮询是低频兜底（spec §1），丢一拍无业务损失，下个周期自愈。
/// </para>
/// <para>
/// RotationScheduler 侧零改动：gate 由组合根（T13）在调用
/// <c>ConfigureRotationAsync</c> 前置位、finally 复位——比反向轮询 busy 标志
/// 少一条耦合边，且天然覆盖异常路径。
/// </para>
/// </summary>
public interface IXcpRotationGate
{
    /// <summary>true = 轮转（stop → 重写 → start）进行中，轮询必须让位。</summary>
    bool IsRotationInProgress { get; }
}

/// <summary>单拍轮询结果。</summary>
public enum PollingCycleOutcome
{
    /// <summary>本轮所有轮询条目已读取（可能带部分失败归因，见 Failures）。</summary>
    Executed,

    /// <summary>轮转进行中，本轮让位跳过（零线上流量，T12 定死规则）。</summary>
    SkippedRotation,

    /// <summary>方案无轮询条目（DAQ 全覆盖），恒不产生流量。</summary>
    NoEntries,
}

/// <summary>单条轮询读数。</summary>
public sealed record PolledValue(string ObjectName, byte[] Data);

/// <summary>单条目失败归因类型（T12 review F3 定案：归因随轮询结果出站，不另设回调）。</summary>
public enum PollingFailureKind
{
    /// <summary>XcpMaster T1 超时且重试用尽（同拍内后续命令前已插 ≥T1 quiesce）。</summary>
    Timeout,

    /// <summary>从机负响应（带内应答，无迟到风险，不插 quiesce）。</summary>
    NegativeResponse,

    /// <summary>写帧失败或应答形态违规（Detail 携带原始消息）。</summary>
    WriteFailed,
}

/// <summary>单条目失败归因值（T13 观察口：随 <see cref="PollingCycleResult"/> 出站）。</summary>
public sealed record PollingEntryFailure(
    string ObjectName,
    PollingFailureKind Kind,
    XcpError? ErrorCode,
    string Detail);

/// <summary>
/// 一轮轮询的结果（<see cref="Values"/> 为成功条目；<see cref="Failures"/> 为
/// 归因失败条目——两者互补且不重叠）。
/// </summary>
public sealed record PollingCycleResult(
    PollingCycleOutcome Outcome,
    IReadOnlyList<PolledValue> Values,
    IReadOnlyList<PollingEntryFailure> Failures)
{
    /// <summary>让位跳过（无读数、无失败）。</summary>
    public static readonly PollingCycleResult Skipped =
        new(PollingCycleOutcome.SkippedRotation, Array.Empty<PolledValue>(), Array.Empty<PollingEntryFailure>());

    /// <summary>无轮询条目（无读数、无失败）。</summary>
    public static readonly PollingCycleResult Empty =
        new(PollingCycleOutcome.NoEntries, Array.Empty<PolledValue>(), Array.Empty<PollingEntryFailure>());
}

/// <summary>轮询参数（低频兜底，周期可注入；spec §1）。</summary>
public sealed class PollingSchedulerOptions
{
    /// <summary>轮询周期。低频兜底，与 DAQ 事件节拍（100 Hz）无关；注入后构造期校验。</summary>
    public TimeSpan Period { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 超时后恢复命令前的 quiesce 间隙（T4 评审裁决，同 T11 模式）。null = 与 master
    /// 的 T1 同源（XcpMasterOptions.Timeout）；非负，0 仅测试可显式关闭。
    /// </summary>
    public TimeSpan? QuiesceDelay { get; init; }
}

/// <summary>
/// 轮询调度器（S2-T12，spec §1 获取方式 + §3 Scheduling）：
/// 读取 T9 planner 降级集合（DaqCapacityExceeded / ObjectTooLarge /
/// NotByteAlignedClass），是位域与 >4B 量的唯一获取路径。
/// <list type="bullet">
/// <item>读路径定死：物理地址 ≤24bit 且量 ≤7B → SHORT_UPLOAD 单帧（最省总线）；
/// 否则 SET_MTA（ADDR_EXT free 基线恒 0，EXTENSION≠0 段已被 planner 构造期
/// fail-loud 拦截——T12 review F1）+ UPLOAD 按 7B 分块（CTO 8B − PID，MTA 按
/// XCP 标准随 UPLOAD 后自增，不重发 SET_MTA；真机自增义务由 T19 用 SHORT_UPLOAD
/// 交叉验证首 chunk 地址——plan T19 附录 A）。</item>
/// <item>fail-loud（对齐 T11 先例）：物理地址未翻译或超 32 位线上地址空间的
/// 条目在构造期即拒绝，绝不上线后才发现配不齐。</item>
/// <item>节奏：周期可注入（<see cref="PollingSchedulerOptions.Period"/>），
/// <see cref="RunAsync"/> 经注入的 <see cref="TimeProvider"/> 计时，测试用
/// FakeTimeProvider 驱动；首拍前先等一个周期。</item>
/// <item>互斥（T12 定死）：轮转进行中（<see cref="IXcpRotationGate"/>）整拍让位
/// 跳过，规则与理由见接口文档。</item>
/// <item>失败路径（T12 review F3 定案）：单条目超时/负响应/写帧失败<b>归因出站
/// 不终止</b>——拍内逐条 catch 归因进 <see cref="PollingCycleResult.Failures"/>，
/// 一次超时/负响应不杀死 <see cref="RunAsync"/>。<b>退避语义定死：无额外指数退避</b>
/// ——低频轮询周期本身即节流，quiesce ≥T1 已封住迟到正响应窗口；连续失败拍计数
/// <see cref="ConsecutiveFailedCycles"/> 公开出站，由 T13/host 决定停采（S2 不自动
/// 放弃：持续兜底重试优于静默停采）。负响应是带内应答无迟到风险，不插 quiesce；
/// 超时拍内下一命令前与拍终返回前都插 ≥T1（T4 裁决，同 T11 模式，TimeProvider 注入
/// 与 XcpMaster T1 同钟源可测）。</item>
/// <item>单飞：同一实例并发轮询直接拒绝（XcpMaster 单发单收，排队即串扰）；
/// <see cref="WaitForQuietAsync"/> 是给轮转侧的静默握手（见其 XML doc）。</item>
/// </list>
/// </summary>
public sealed class PollingScheduler : IDisposable
{
    /// <summary>UPLOAD 单帧数据场上限：CTO 8B − PID 1B（spec §1）。</summary>
    private const int MaxUploadChunkBytes = 7;

    /// <summary>SHORT_UPLOAD 线上地址上限：addrExt(1B) + addr(3B)，24bit。</summary>
    private const uint ShortUploadMaxAddress = 0x00FF_FFFF;

    /// <summary>ADDR_EXT free 基线（与 T11 WRITE_DAQ/SET_DAQ_PTR 同基线，恒 0）。</summary>
    private const byte AddressExtension = 0x00;

    private readonly XcpMaster _master;
    private readonly PlannedAcquisitionMap _plan;
    private readonly IXcpRotationGate? _rotationGate;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _period;
    private readonly TimeSpan _quiesceDelay;

    /// <summary>
    /// 拍互斥闩（T12 review F4 定形）：PollOnceAsync 用 try-0 语义做单飞拒绝，
    /// WaitForQuietAsync 用阻塞等待做静默握手——同一闩，两用。
    /// </summary>
    private readonly SemaphoreSlim _cycleGate = new(1, 1);

    private int _consecutiveFailedCycles;

    /// <summary>生效轮询周期（注入值回读，测试断言用）。</summary>
    public TimeSpan Period => _period;

    /// <summary>
    /// 连续失败拍计数（T12 review F3 定案）：Failures 非空即计一拍（部分成功也计），
    /// 全成功归零。只增不减由调用方消费（T13/host 决定停采），本层不自动放弃。
    /// </summary>
    public int ConsecutiveFailedCycles => Volatile.Read(ref _consecutiveFailedCycles);

    /// <summary>
    /// 构造并 fail-loud 校验轮询条目（线上帧零发出时即暴露配不齐的方案）。
    /// T1/quiesce 同源取用 <paramref name="masterOptions"/>（T11 先例：与 XcpMaster
    /// 超时策略永不脱钩）。
    /// </summary>
    public PollingScheduler(
        XcpMaster master,
        XcpMasterOptions masterOptions,
        PlannedAcquisitionMap plan,
        PollingSchedulerOptions? options = null,
        IXcpRotationGate? rotationGate = null,
        TimeProvider? timeProvider = null)
    {
        _master = master ?? throw new ArgumentNullException(nameof(master));
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _rotationGate = rotationGate;
        _timeProvider = timeProvider ?? TimeProvider.System;

        var effective = options ?? new PollingSchedulerOptions();
        var period = effective.Period;
        if (period <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), period, "Polling period must be positive.");
        _period = period;

        var quiesce = effective.QuiesceDelay ?? (masterOptions ?? throw new ArgumentNullException(nameof(masterOptions))).Timeout;
        if (quiesce < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), quiesce, "QuiesceDelay must be non-negative.");
        _quiesceDelay = quiesce;

        foreach (var entry in _plan.PollingEntries)
        {
            if (entry.ByteLength < 1)
                throw new InvalidOperationException(
                    $"Polling entry '{entry.ObjectName}' has non-positive byte length {entry.ByteLength}.");
            if (entry.PhysicalAddress is not { } physical)
                throw new InvalidOperationException(
                    $"Polling entry '{entry.ObjectName}' has no translated physical address; " +
                    "UPLOAD cannot be issued (spec §1/[H1]).");
            if (physical > uint.MaxValue)
                throw new InvalidOperationException(
                    $"Physical address 0x{physical:X} of polling entry '{entry.ObjectName}' exceeds " +
                    "the 32-bit SET_MTA address space.");
        }
    }

    /// <summary>
    /// 执行单拍轮询：逐条目读取（ShortUpload/SetMta+Upload 路径见类型文档）。
    /// 轮转进行中整拍让位跳过；无条目零流量。
    /// <para>
    /// <b>异常集（T12 review F3 钉死）</b>：本方法只抛
    /// <see cref="OperationCanceledException"/>（调用方取消）——条目级失败一律归因进
    /// <see cref="PollingCycleResult.Failures"/>（Timeout / NegativeResponse /
    /// WriteFailed），单拍失败不外溢终止 <see cref="RunAsync"/>。构造期校验（地址
    /// 未翻译/超 32 位/非正长度）在构造函数完成，运行期不再抛 precondition 异常。
    /// </para>
    /// <para>
    /// <b>quiesce 义务（T4 裁决自守，调用方无需另行等待）</b>：拍内发生
    /// XcpTimeoutException 后，同拍下一条命令前插 ≥T1；拍终（有过超时）返回前
    /// 再插 ≥T1——返回即安全，任何后续命令（本拍/下一拍/调用方侧）都不会与
    /// 迟到正响应错配。负响应为带内应答，无迟到风险，不插。
    /// 取消边界（R4）：若 <see cref="OperationCanceledException"/> 打断了拍内或
    /// 拍终 quiesce，T4 义务随调用方重启流程负责——取消后重新发起采集/命令的
    /// 调用侧必须在第一条命令前自行保持 ≥T1 间隙。
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">同实例并发第二轮询（单飞拒绝）。</exception>
    /// <exception cref="OperationCanceledException">调用方取消。</exception>
    public async Task<PollingCycleResult> PollOnceAsync(CancellationToken ct = default)
    {
        if (_plan.PollingEntries.Count == 0)
            return PollingCycleResult.Empty;

        // 单飞：try-0 抢闩失败即拒绝（排队即与 XcpMaster 单发单收语义串扰）。
        if (!await _cycleGate.WaitAsync(0, ct).ConfigureAwait(false))
            throw new InvalidOperationException(
                "PollingScheduler is single-flight: a polling cycle is already in progress " +
                "(XcpMaster is single-request; queueing would interleave command/response pairs).");

        try
        {
            // 互斥规则（T12 定死）：轮转进行中整拍让位——不排队不发帧。
            // R1 顺序定死（review）：先抢闩、后查 gate——"拍未上闩 ⇒ 必然看到
            // gate 已置位"，WaitForQuietAsync 握手因此无洞：gate 置位后开启的
            // 握手只可能被 (a) 已过 gate 检查的在途拍或 (b) 无在途拍（立即返回）
            // 压住，绝不存在"gate 检查通过但闩还没上"的中间态溜进轮转期。
            if (_rotationGate is { IsRotationInProgress: true })
                return PollingCycleResult.Skipped;

            var values = new List<PolledValue>(_plan.PollingEntries.Count);
            var failures = new List<PollingEntryFailure>();
            var timeoutSeen = false;

            foreach (var entry in _plan.PollingEntries)
            {
                try
                {
                    values.Add(await ReadEntryAsync(entry, ct).ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    throw; // 调用方取消不是条目失败，照常外溢。
                }
                catch (XcpTimeoutException ex)
                {
                    timeoutSeen = true;
                    failures.Add(new PollingEntryFailure(
                        entry.ObjectName, PollingFailureKind.Timeout, null, ex.Message));
                    // T4 裁决：超时后同拍下一条命令前保持 ≥T1（迟到正响应窗口）。
                    await QuiesceAsync(ct).ConfigureAwait(false);
                }
                catch (XcpErrorResponseException ex)
                {
                    // 负响应是带内应答：无迟到风险，归因后继续，不插 quiesce。
                    failures.Add(new PollingEntryFailure(
                        entry.ObjectName, PollingFailureKind.NegativeResponse, ex.Response.Code, ex.Message));
                }
                catch (InvalidOperationException ex)
                {
                    // 写帧失败（XcpMaster）或应答形态违规（ExtractPayload）：归因出站。
                    failures.Add(new PollingEntryFailure(
                        entry.ObjectName, PollingFailureKind.WriteFailed, null, ex.Message));
                }
            }

            if (timeoutSeen)
            {
                // 拍终再插 ≥T1：本方法返回后，下一拍入口/调用方侧任何命令都安全。
                await QuiesceAsync(ct).ConfigureAwait(false);
            }

            // 连续失败拍计数：Failures 非空即一拍（部分成功也计），全成功归零。
            var failedCycle = failures.Count > 0;
            if (failedCycle)
                Interlocked.Increment(ref _consecutiveFailedCycles);
            else
                Interlocked.Exchange(ref _consecutiveFailedCycles, 0);

            return new PollingCycleResult(PollingCycleOutcome.Executed, values, failures);
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    /// <summary>
    /// 静默握手（T12 review F4 定形，给 T13 轮转接线用）：
    /// <b>T13 接线顺序（钉死）</b>：置位 <see cref="IXcpRotationGate"/> gate →
    /// <c>await WaitForQuietAsync</c> → <c>ConfigureRotationAsync</c> →
    /// finally 复位 gate。等待在途拍完成后返回；无在途拍立即返回。让位检查
    /// （gate 置位后）天然拦住后续新拍，本方法封住已开跑的最后一拍。
    /// </summary>
    public async Task WaitForQuietAsync(CancellationToken ct = default)
    {
        await _cycleGate.WaitAsync(ct).ConfigureAwait(false);
        _cycleGate.Release();
    }

    /// <summary>
    /// 周期循环：先等一个周期再轮询（首拍前不轮询），每拍把结果（含失败归因）
    /// 回调给 <paramref name="onCycle"/>。经注入的 <see cref="TimeProvider"/> 计时，
    /// 测试用 FakeTimeProvider 虚拟时钟推进驱动。
    /// <para>
    /// 条目级失败不终止循环（见 <see cref="PollOnceAsync"/> 异常集）；
    /// 仅 <see cref="OperationCanceledException"/>（调用方取消）外溢。
    /// </para>
    /// </summary>
    public async Task RunAsync(Action<PollingCycleResult>? onCycle = null, CancellationToken ct = default)
    {
        while (true)
        {
            await Task.Delay(_period, _timeProvider, ct).ConfigureAwait(false);
            var result = await PollOnceAsync(ct).ConfigureAwait(false);
            onCycle?.Invoke(result);
        }
    }

    /// <summary>释放拍互斥闩。调用方保证停轮询后 Dispose（与 XcpMaster 生命周期同管）。</summary>
    public void Dispose() => _cycleGate.Dispose();

    private async Task QuiesceAsync(CancellationToken ct) =>
        await Task.Delay(_quiesceDelay, _timeProvider, ct).ConfigureAwait(false);

    private async Task<PolledValue> ReadEntryAsync(PlannedPollingEntry entry, CancellationToken ct)
    {
        // 构造期已 fail-loud，此处为类型收窄。
        var address = entry.PhysicalAddress!.Value;

        byte[] data;
        if (entry.ByteLength <= MaxUploadChunkBytes && address <= ShortUploadMaxAddress)
        {
            // 路径一：SHORT_UPLOAD 单帧（addrExt + 24bit 地址 + ≤7B）。
            var response = await _master.SendAsync(
                XcpCommandEncoder.ShortUpload((byte)entry.ByteLength, (uint)address, AddressExtension), ct)
                .ConfigureAwait(false);
            data = ExtractPayload(response, entry.ByteLength, entry.ObjectName);
        }
        else
        {
            // 路径二：SET_MTA 一次 + UPLOAD 按 7B 分块（MTA 随 UPLOAD 后自增，XCP 标准）。
            // S5 评审 P1-2：MTA 是共享态——多命令序列整体持有 master 内存序列门，
            // 防止与写回序列交错劫持 MTA（写错地址）。
            using var sequence = await _master.EnterMemorySequenceAsync(ct).ConfigureAwait(false);
            await _master.SendAsync(XcpCommandEncoder.SetMta(AddressExtension, (uint)address), ct)
                .ConfigureAwait(false);
            var chunks = new List<byte>(entry.ByteLength);
            var remaining = entry.ByteLength;
            while (remaining > 0)
            {
                var chunk = Math.Min(remaining, MaxUploadChunkBytes);
                var response = await _master.SendAsync(XcpCommandEncoder.Upload((byte)chunk), ct)
                    .ConfigureAwait(false);
                chunks.AddRange(ExtractPayload(response, chunk, entry.ObjectName));
                remaining -= chunk;
            }
            data = chunks.ToArray();
        }

        return new PolledValue(entry.ObjectName, data);
    }

    /// <summary>
    /// 正响应载荷切片（T12 review F2 定案）：正响应允许带 DLC 8B padding——
    /// 帧长 ≥ 1+expectedBytes 且 PID 0xFF 即取前 expectedBytes；短帧仍 fail-loud
    /// （载荷不足 = 从机违约，静默截取会错采）。
    /// </summary>
    private static byte[] ExtractPayload(byte[] response, int expectedBytes, string objectName)
    {
        if (response.Length < 1 + expectedBytes || response[0] != XcpPid.PositiveResponse)
            throw new InvalidOperationException(
                $"Malformed UPLOAD response for polling entry '{objectName}': expected " +
                $">= [FF + {expectedBytes}B], got {response.Length}B with PID 0x{response[0]:X2}.");
        var data = new byte[expectedBytes];
        Array.Copy(response, 1, data, 0, expectedBytes);
        return data;
    }
}
