namespace PeakCan.HIL.Core.HIL.Contracts;

using PeakCan.HIL.Core.Uds;

/// <summary>
/// Decouples HIL executors from the concrete UdsClient / IsoTpLayer dependency chain.
/// Adapter (UdsSessionAdapter) wraps UdsClient.
/// Task B（spec 2026-08-27 §Q1）：第一步补 DID 读写两方法 + 第二步补 SessionControl/
/// ClearDtc/RoutineControl/SecurityAccess/ECUReset/CommunicationControl/IOControl 8 方法，
/// 全部 UDS 类 executor 统一吃本接口，多通道路由（IUdsSessionResolver）由此前置。
/// </summary>
public interface IUdsSession
{
    Task<IReadOnlyList<DtcInfo>> ReadDtcInformation(byte statusMask, CancellationToken ct);
    Task SendRequestAsync(byte serviceId, byte[]? data, CancellationToken ct);

    /// <summary>读 DID（0x22）。返回响应数据（不含 SID/DID）。NRC 抛 UdsNrcException，传输失败抛 UdsSessionTransportException。</summary>
    Task<byte[]> ReadDataByIdentifierAsync(ushort did, CancellationToken ct);

    /// <summary>写 DID（0x2E）。NRC 抛 UdsNrcException，传输失败抛 UdsSessionTransportException。</summary>
    Task WriteDataByIdentifierAsync(ushort did, byte[] data, CancellationToken ct);

    /// <summary>DiagnosticSessionControl（0x10）。NRC 抛 UdsNrcException，传输失败抛 UdsSessionTransportException。</summary>
    Task<DiagnosticSessionResponse> DiagnosticSessionControlAsync(byte sessionType, CancellationToken ct);

    /// <summary>ClearDiagnosticInformation（0x14）。NRC 抛 UdsNrcException，传输失败抛 UdsSessionTransportException。</summary>
    Task ClearDiagnosticInformationAsync(uint groupOfDtc, CancellationToken ct);

    /// <summary>RoutineControl（0x31）。NRC 抛 UdsNrcException，传输失败抛 UdsSessionTransportException。</summary>
    Task<byte[]> RoutineControlAsync(byte routineControlType, ushort routineId, byte[]? data, CancellationToken ct);

    /// <summary>SecurityAccess（0x27）请求 seed。NRC 抛 UdsNrcException，传输失败抛 UdsSessionTransportException。</summary>
    Task<byte[]> RequestSeedAsync(byte level, CancellationToken ct);

    /// <summary>SecurityAccess（0x27）发送 key（第二参重载：仅 level 时由 client 内部按 level 求 key）。</summary>
    Task<byte[]> SecurityAccessAsync(byte level, CancellationToken ct);

    /// <summary>ECUReset（0x11）。NRC 抛 UdsNrcException，传输失败抛 UdsSessionTransportException。</summary>
    Task<byte> EcuResetAsync(byte resetType, CancellationToken ct);

    /// <summary>TesterPresent（0x3E）。NRC 抛 UdsNrcException，传输失败抛 UdsSessionTransportException。</summary>
    Task TesterPresentAsync(bool suppressPosResponse, CancellationToken ct);

    /// <summary>IOControl（0x2F）。NRC 抛 UdsNrcException，传输失败抛 UdsSessionTransportException。</summary>
    Task<byte[]> IOControlAsync(ushort did, byte controlType, byte[]? data, byte controlEnableMask = 0xFF, CancellationToken ct = default);
}
