namespace PeakCan.Host.Core.Xcp.Protocol;

/// <summary>
/// 一帧一 CTO 的 XCP 帧（CTO 8B 硬上限，spec §1/§3）。
/// 载荷即线上字节，首字节为 PID。
/// </summary>
public readonly record struct XcpCtoFrame
{
    /// <summary>CTO 最大字节数（DTO/CTO 各 8B）。</summary>
    public const int MaxByteLength = 8;

    /// <summary>最小 1 字节（仅 PID，如 DISCONNECT 正响应）。</summary>
    public const int MinByteLength = 1;

    /// <summary>Raw wire bytes: PID first, then command/response payload.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    public XcpCtoFrame(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is < MinByteLength or > MaxByteLength)
            throw new ArgumentException($"CTO frame must be {MinByteLength}..{MaxByteLength} bytes, got {bytes.Length}.", nameof(bytes));

        Bytes = bytes;
    }

    /// <summary>PID（首字节，见 <see cref="XcpPid"/>）。</summary>
    public byte Pid => Bytes.Span[0];

    /// <summary>正响应流（PID 0xFF）。</summary>
    public bool IsPositiveResponse => Pid == XcpPid.PositiveResponse;

    /// <summary>错误响应流（PID 0xFE）。</summary>
    public bool IsError => Pid == XcpPid.Error;

    /// <summary>
    /// DAQ DTO 流（PID 0x00–0xFB 均为 ODT 号空间；0xFC/0xFD 为 EV/RQM 包，
    /// 不属于 DAQ DTO——S2-T1-review 修正原 `Pid &lt; 0xFE` 把 EV/RQM 误判为 DTO 的语义）。
    /// </summary>
    public bool IsDaqDto => Pid <= XcpPid.DaqDtoLast;

    /// <summary>Event packet 流（PID 0xFC；S2 不产生）。</summary>
    public bool IsEventPacket => Pid == XcpPid.EventPacket;

    /// <summary>Request packet 流（PID 0xFD；S2 不产生）。</summary>
    public bool IsRequestPacket => Pid == XcpPid.RequestPacket;
}
