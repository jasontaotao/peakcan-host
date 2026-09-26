namespace PeakCan.Host.Core.Xcp.Protocol;

/// <summary>
/// S6-T4 协议层读原语：SET_MTA + UPLOAD×⌈n/MaxUploadBytes⌉ 分块读（XCP UPLOAD 的
/// MTA 按 nbytes 自增，与 S5 T0 从机裁决一致）。<b>调用方须持</b>
/// <see cref="XcpMaster.EnterMemorySequenceAsync"/> 序列门（SET_MTA 之后的命令序列
/// 不可插队，S5-T8 评审口径）。零 DOWNLOAD——本原语只读。
/// </summary>
public static class XcpUploadReader
{
    /// <summary>单帧 UPLOAD 最大载荷（CTO 上限 - PID）。</summary>
    public static readonly int MaxUploadBytes = XcpCtoFrame.MaxByteLength - 1;

    public static async Task<byte[]> ReadAsync(XcpMaster master, uint address, int length, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length == 0)
            return [];

        var setMta = await master.SendAsync(XcpCommandEncoder.SetMta(0, address), ct).ConfigureAwait(false);
        XcpResponseDecoder.SetMta(setMta);

        var buffer = new byte[length];
        for (var offset = 0; offset < buffer.Length; offset += MaxUploadBytes)
        {
            var n = Math.Min(MaxUploadBytes, buffer.Length - offset);
            var response = await master.SendAsync(XcpCommandEncoder.Upload((byte)n), ct).ConfigureAwait(false);
            XcpResponseDecoder.Upload(response).CopyTo(buffer, offset);
        }
        return buffer;
    }
}
