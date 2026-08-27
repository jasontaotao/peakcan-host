namespace PeakCan.HIL.Core.HIL.Contracts;

/// <summary>
/// Decouples HIL executors from the concrete UdsClient / IsoTpLayer dependency chain.
/// Adapter (UdsSessionAdapter) wraps UdsClient.
/// Task B 第一步（Q1，spec 2026-08-27）：补 DID 读写两方法，ReadDid/WriteDid executor
/// 迁到本接口——多通道路由（IUdsSessionResolver）的前置统一。
/// </summary>
public interface IUdsSession
{
    Task<IReadOnlyList<DtcInfo>> ReadDtcInformation(byte statusMask, CancellationToken ct);
    Task SendRequestAsync(byte serviceId, byte[]? data, CancellationToken ct);

    /// <summary>读 DID（0x22）。返回响应数据（不含 SID/DID）。NRC 抛 UdsNrcException，传输失败抛 UdsSessionTransportException。</summary>
    Task<byte[]> ReadDataByIdentifierAsync(ushort did, CancellationToken ct);

    /// <summary>写 DID（0x2E）。NRC 抛 UdsNrcException，传输失败抛 UdsSessionTransportException。</summary>
    Task WriteDataByIdentifierAsync(ushort did, byte[] data, CancellationToken ct);
}
