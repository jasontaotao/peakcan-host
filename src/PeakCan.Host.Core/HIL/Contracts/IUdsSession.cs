namespace PeakCan.Host.Core.HIL.Contracts;

/// <summary>
/// Decouples HIL executors from the concrete UdsClient / IsoTpLayer dependency chain.
/// Adapter (Infrastructure layer) wraps UdsClient.
/// </summary>
public interface IUdsSession
{
    Task<IReadOnlyList<DtcInfo>> ReadDtcInformation(byte statusMask, CancellationToken ct);
    Task SendRequestAsync(byte serviceId, byte[]? data, CancellationToken ct);
}
