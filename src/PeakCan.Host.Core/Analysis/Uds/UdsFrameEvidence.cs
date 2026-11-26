namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>
/// One row of the raw evidence table shown under a transaction / noise bucket:
/// only matched, noise and unsupported frames get a row; non-diagnostic traffic
/// is aggregated into a count to keep large logs renderable.
/// </summary>
public sealed record UdsFrameEvidence(
    int Index,
    double Timestamp,
    ushort Channel,
    uint Id,
    bool IsExtended,
    bool? IsTransmit,
    UdsFrameClass Class,
    UdsNoiseReason? NoiseReason,
    string FrameKind,
    byte Dlc,
    byte[] Data)
{
    public string Display =>
        $"{Index}  {Timestamp:F6}  ch{Channel}  0x{Id:X3}  {FrameKind}  {Convert.ToHexString(Data)}";
}
