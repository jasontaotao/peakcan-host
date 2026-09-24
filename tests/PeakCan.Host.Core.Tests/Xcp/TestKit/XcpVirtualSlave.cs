using System.Collections.Concurrent;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Xcp.Abstractions;
using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Tests.Xcp.TestKit;

/// <summary>
/// XcpVirtualSlave 脚本化模拟从机（S2-T6，测试基础设施）：实现 IXcpTransport，
/// 默认按 XcpGoldenSamples 回放 spec §1 硬约束的正响应（MAX_DAQ=1、15 ODT、
/// CTO/DTO 8B、GET_DAQ_EVENT_INFO 周期 100Hz 均编码于样本内）；
/// 未知命令码回 ERR_CMD_UNKNOWN 负响应。
/// <para>
/// 脚本化能力：篡改正/负响应（T7 对账用）、silence（超时）、延迟注入、
/// 错误码注入、DTO/错位帧注入、丢帧计数注入（T13/T16 端到端用）。
/// </para>
/// <para>
/// 响应帧（PID 0xFF/0xFE）与 DTO（PID 0x00-0xFB）共用同一 CAN_ID_SLAVE 出帧
/// （spec 写死按 PID 首字节分流，禁止按 CAN ID 分流）。
/// FramesDropped 语义与 IXcpTransport 契约一致：只有 DTO 流丢弃计数；
/// silence 掉的响应帧与负响应帧永不计入。
/// </para>
/// <para>
/// 测试替身简化：响应在 WriteAsync 内同步派发（订阅者不阻塞，等效满足契约）；
/// 延迟注入在发送线程上等待（XcpMaster 的 T1 由其自身 TimeProvider 驱动）。
/// </para>
/// </summary>
public sealed class XcpVirtualSlave : IXcpTransport
{
    /// <summary>台架基线 CAN ID：master→slave 0x18FFF667 / slave→master 0x18FFF666（29 位扩展，A-4 待核实）。</summary>
    public static readonly CanId DefaultMasterCanId = new(0x18FFF667, FrameFormat.Extended);
    public static readonly CanId DefaultSlaveCanId = new(0x18FFF666, FrameFormat.Extended);

    /// <summary>ODT 数据场硬上限：DTO 8B − PID 1B（spec §1）。</summary>
    private const int DtoMaxPayloadLength = 7;

    private const int MaxFrameLength = 8;

    private readonly object _gate = new();
    private readonly CanId _slaveCanId;
    private readonly Dictionary<byte, Rule> _rules = new();
    private long _framesDropped;

    /// <summary>单条命令的脚本化规则；未命中即走黄金样本默认值。</summary>
    private sealed record Rule(byte[]? OverrideResponse = null, bool Silent = false, TimeSpan Delay = default);

    /// <summary>黄金样本全量命令 → 正响应映射（默认应答表）。</summary>
    private static readonly Dictionary<byte, byte[]> DefaultResponses = new()
    {
        [XcpPid.Connect] = XcpGoldenSamples.ConnectPositiveResponse.ToArray(),
        [XcpPid.Disconnect] = XcpGoldenSamples.DisconnectPositiveResponse.ToArray(),
        [XcpPid.GetStatus] = XcpGoldenSamples.GetStatusPositiveResponse.ToArray(),
        [XcpPid.Synch] = XcpGoldenSamples.SynchPositiveResponse.ToArray(),
        [XcpPid.GetCommModeInfo] = XcpGoldenSamples.GetCommModeInfoPositiveResponse.ToArray(),
        [XcpPid.SetMta] = XcpGoldenSamples.SetMtaPositiveResponse.ToArray(),
        [XcpPid.Upload] = XcpGoldenSamples.UploadPositiveResponse.ToArray(),
        [XcpPid.ShortUpload] = XcpGoldenSamples.ShortUploadPositiveResponse.ToArray(),
        [XcpPid.Download] = XcpGoldenSamples.DownloadPositiveResponse.ToArray(),
        [XcpPid.ClearDaqList] = XcpGoldenSamples.ClearDaqListPositiveResponse.ToArray(),
        [XcpPid.SetDaqPtr] = XcpGoldenSamples.SetDaqPtrPositiveResponse.ToArray(),
        [XcpPid.WriteDaq] = XcpGoldenSamples.WriteDaqPositiveResponse.ToArray(),
        [XcpPid.StartStopDaqList] = XcpGoldenSamples.StartStopDaqListPositiveResponse.ToArray(),
        [XcpPid.StartStopSynch] = XcpGoldenSamples.StartStopSynchPositiveResponse.ToArray(),
        [XcpPid.GetDaqProcessorInfo] = XcpGoldenSamples.GetDaqProcessorInfoPositiveResponse.ToArray(),
        [XcpPid.GetDaqResolutionInfo] = XcpGoldenSamples.GetDaqResolutionInfoPositiveResponse.ToArray(),
        [XcpPid.GetDaqListInfo] = XcpGoldenSamples.GetDaqListInfoPositiveResponse.ToArray(),
        [XcpPid.GetDaqEventInfo] = XcpGoldenSamples.GetDaqEventInfoPositiveResponse.ToArray(),
    };

    /// <inheritdoc />
    public event Action<CanFrame>? FrameReceived;

    /// <summary>
    /// DTO 流丢弃计数（脚本注入的本机丢帧模拟）。响应帧永不计入——
    /// 与 IXcpTransport 契约一致（契约：响应帧不得经过可丢帧队列）。
    /// </summary>
    public long FramesDropped => Interlocked.Read(ref _framesDropped);

    public XcpVirtualSlave(CanId? slaveCanId = null)
        => _slaveCanId = slaveCanId ?? DefaultSlaveCanId;

    // ---- 脚本化规则（命令应答）----

    /// <summary>篡改正响应（整帧字节含 PID 0xFF）——T7 对账用篡改声明值的入口。</summary>
    public void OverrideResponse(byte commandPid, params byte[] response)
    {
        ValidateFrameBytes(response, nameof(response));
        Mutate(commandPid, r => r with { OverrideResponse = (byte[])response.Clone(), Silent = false });
    }

    /// <summary>注入负响应 [FE, code]——脚本化错误码。</summary>
    public void OverrideNegative(byte commandPid, XcpError error)
        => OverrideResponse(commandPid, XcpPid.Error, (byte)error);

    /// <summary>silence：命令被消费但永不应答（master 侧 T1 超时路径）。</summary>
    public void Silence(byte commandPid)
        => Mutate(commandPid, r => r with { Silent = true });

    /// <summary>延迟注入：默认/篡改响应在发送线程上延迟 <paramref name="delay"/> 后出帧。</summary>
    public void DelayResponse(byte commandPid, TimeSpan delay)
    {
        if (delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "Delay must be non-negative.");
        Mutate(commandPid, r => r with { Delay = delay });
    }

    // ---- 帧注入（slave→master）----

    /// <summary>注入一帧 DAQ DTO：[odt, payload...]；payload 硬上限 7B（DTO 8B − PID）。</summary>
    public void InjectDto(byte odt, params byte[] payload)
    {
        if (payload.Length > DtoMaxPayloadLength)
            throw new ArgumentException($"DTO payload must be at most {DtoMaxPayloadLength} bytes (DTO 8B minus PID), got {payload.Length}.", nameof(payload));
        Emit(new[] { odt }.Concat(payload).ToArray());
    }

    /// <summary>注入任意 slave→master 字节流（错位帧/垃圾 PID/EV 帧）。</summary>
    public void InjectFrame(params byte[] data)
    {
        ValidateFrameBytes(data, nameof(data));
        Emit((byte[])data.Clone());
    }

    /// <summary>
    /// 注入本机丢帧：递增 <see cref="FramesDropped"/> 而不出帧——只允许计 DTO 流丢弃，
    /// 与 IXcpTransport 契约"响应帧不计入"对齐。
    /// </summary>
    public void InjectDroppedDto(int count = 1)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Count must be positive.");
        Interlocked.Add(ref _framesDropped, count);
    }

    // ---- IXcpTransport ----

    /// <inheritdoc />
    public async ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
    {
        if (frame.Data.Length < 1)
            throw new ArgumentException("XCP request frame must carry at least the PID byte.", nameof(frame));

        Rule? rule;
        var commandPid = frame.Data.Span[0];
        lock (_gate)
            _rules.TryGetValue(commandPid, out rule);

        if (rule is not null && rule.Delay > TimeSpan.Zero)
            await Task.Delay(rule.Delay, ct).ConfigureAwait(false);

        if (rule is { Silent: true })
            return Result<Unit>.Ok(default);

        var response = rule?.OverrideResponse
            ?? (DefaultResponses.TryGetValue(commandPid, out var golden)
                ? golden
                : new byte[] { XcpPid.Error, (byte)XcpError.CmdUnknown });
        Emit(response);
        return Result<Unit>.Ok(default);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void Emit(byte[] data)
        => FrameReceived?.Invoke(new CanFrame(
            _slaveCanId,
            new ReadOnlyMemory<byte>(data),
            FrameFlags.None,
            ChannelId.None,
            default));

    private void Mutate(byte commandPid, Func<Rule, Rule> update)
    {
        lock (_gate)
        {
            _rules.TryGetValue(commandPid, out var current);
            _rules[commandPid] = update(current ?? new Rule());
        }
    }

    private static void ValidateFrameBytes(byte[] data, string paramName)
    {
        if (data.Length is < 1 or > MaxFrameLength)
            throw new ArgumentException($"Frame data must be 1..{MaxFrameLength} bytes, got {data.Length}.", paramName);
    }
}
