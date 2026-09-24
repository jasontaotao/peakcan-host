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
/// <c>ConfigureRotationAsync</c> 前置位、finally 复位——比让轮转器反向通知
/// 轮询器少一条耦合边，且天然覆盖异常路径。
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
    /// <summary>本轮所有轮询条目已读取。</summary>
    Executed,

    /// <summary>轮转进行中，本轮让位跳过（零线上流量，T12 定死规则）。</summary>
    SkippedRotation,

    /// <summary>方案无轮询条目（DAQ 全覆盖），恒不产生流量。</summary>
    NoEntries,
}

/// <summary>单条轮询读数。</summary>
public sealed record PolledValue(string ObjectName, byte[] Data);

/// <summary>一轮轮询的结果（<see cref="Values"/> 仅在 Executed 时非空）。</summary>
public sealed record PollingCycleResult(PollingCycleOutcome Outcome, IReadOnlyList<PolledValue> Values)
{
    /// <summary>让位跳过（无读数）。</summary>
    public static readonly PollingCycleResult Skipped =
        new(PollingCycleOutcome.SkippedRotation, Array.Empty<PolledValue>());

    /// <summary>无轮询条目（无读数）。</summary>
    public static readonly PollingCycleResult Empty =
        new(PollingCycleOutcome.NoEntries, Array.Empty<PolledValue>());
}

/// <summary>轮询参数（低频兜底，周期可注入；spec §1）。</summary>
public sealed class PollingSchedulerOptions
{
    /// <summary>轮询周期。低频兜底，与 DAQ 事件节拍（100 Hz）无关；注入后构造期校验。</summary>
    public TimeSpan Period { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// 轮询调度器（S2-T12，spec §1 获取方式 + §3 Scheduling）：
/// 读取 T9 planner 降级集合（DaqCapacityExceeded / ObjectTooLarge /
/// NotByteAlignedClass），是位域与 >4B 量的唯一获取路径。
/// <list type="bullet">
/// <item>读路径定死：物理地址 ≤24bit 且量 ≤7B → SHORT_UPLOAD 单帧（最省总线）；
/// 否则 SET_MTA（ADDR_EXT free 基线恒 0）+ UPLOAD 按 7B 分块（CTO 8B − PID，
/// MTA 按 XCP 标准随 UPLOAD 后自增，不重发 SET_MTA）。</item>
/// <item>fail-loud（对齐 T11 先例）：物理地址未翻译或超 32 位线上地址空间的
/// 条目在构造期即拒绝，绝不上线后才发现配不齐。</item>
/// <item>节奏：周期可注入（<see cref="PollingSchedulerOptions.Period"/>），
/// <see cref="RunAsync"/> 经注入的 <see cref="TimeProvider"/> 计时，测试用
/// FakeTimeProvider 驱动；首拍前先等一个周期。</item>
/// <item>互斥（T12 定死）：轮转进行中（<see cref="IXcpRotationGate"/>）整拍让位
/// 跳过，规则与理由见接口文档。</item>
/// <item>单飞：同一实例并发轮询直接拒绝（与 RotationScheduler 同型；
/// XcpMaster 单发单收，排队即串扰）。</item>
/// </list>
/// </summary>
public sealed class PollingScheduler
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
    private int _busy;

    /// <summary>生效轮询周期（注入值回读，测试断言用）。</summary>
    public TimeSpan Period => _period;

    /// <summary>
    /// 构造并 fail-loud 校验轮询条目（线上帧零发出时即暴露配不齐的方案）。
    /// </summary>
    public PollingScheduler(
        XcpMaster master,
        PlannedAcquisitionMap plan,
        PollingSchedulerOptions? options = null,
        IXcpRotationGate? rotationGate = null,
        TimeProvider? timeProvider = null)
    {
        _master = master ?? throw new ArgumentNullException(nameof(master));
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _rotationGate = rotationGate;
        _timeProvider = timeProvider ?? TimeProvider.System;

        var period = (options ?? new PollingSchedulerOptions()).Period;
        if (period <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), period, "Polling period must be positive.");
        _period = period;

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
    /// </summary>
    /// <exception cref="InvalidOperationException">同实例并发第二轮询（单飞拒绝）。</exception>
    public async Task<PollingCycleResult> PollOnceAsync(CancellationToken ct = default)
    {
        if (_plan.PollingEntries.Count == 0)
            return PollingCycleResult.Empty;

        // 互斥规则（T12 定死）：轮转进行中整拍让位——不排队不发帧。
        if (_rotationGate is { IsRotationInProgress: true })
            return PollingCycleResult.Skipped;

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException(
                "PollingScheduler is single-flight: a polling cycle is already in progress " +
                "(XcpMaster is single-request; queueing would interleave command/response pairs).");

        try
        {
            var values = new List<PolledValue>(_plan.PollingEntries.Count);
            foreach (var entry in _plan.PollingEntries)
                values.Add(await ReadEntryAsync(entry, ct).ConfigureAwait(false));
            return new PollingCycleResult(PollingCycleOutcome.Executed, values);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>
    /// 周期循环：先等一个周期再轮询（首拍前不轮询），每拍把结果回调给
    /// <paramref name="onCycle"/>。经注入的 <see cref="TimeProvider"/> 计时，
    /// 测试用 FakeTimeProvider 虚拟时钟推进驱动。
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

    private static byte[] ExtractPayload(byte[] response, int expectedBytes, string objectName)
    {
        if (response.Length != 1 + expectedBytes || response[0] != XcpPid.PositiveResponse)
            throw new InvalidOperationException(
                $"Malformed UPLOAD response for polling entry '{objectName}': expected " +
                $"[FF + {expectedBytes}B], got {response.Length}B with PID 0x{response[0]:X2}.");
        var data = new byte[expectedBytes];
        Array.Copy(response, 1, data, 0, expectedBytes);
        return data;
    }
}