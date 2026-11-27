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
    /// <summary>
    /// 日志记录的方向。<c>null</c> = 日志没标（不是「未知就算请求」）。
    /// spec §6.2 要求证据行保留它：只看 hex 分不出哪条是请求、哪条是响应。
    /// </summary>
    public string DirectionText => IsTransmit switch
    {
        true => "TX",
        false => "RX",
        null => "—",
    };

    /// <summary>
    /// 最终分类桶。噪声帧要同时给出原因，否则「为什么这条不能用」又得回头猜。
    /// </summary>
    public string ClassText => Class switch
    {
        UdsFrameClass.MatchedDiagnostic => "诊断",
        UdsFrameClass.Noise => NoiseReason is { } r ? $"噪声（{r}）" : "噪声",
        UdsFrameClass.UnsupportedInput => "未支持",
        _ => "非诊断",
    };

    /// <summary>
    /// 行首写 <c>证据 #N</c>，与报告明细（校验失败 / 传输警告）用同一词表——
    /// 两边都是同一个帧级序号，用户照着报告里的号能在这一列找回原帧。
    /// </summary>
    public string Display =>
        $"证据 #{Index}  {Timestamp:F6}  ch{Channel}  {DirectionText}  0x{Id:X3}  {ClassText}  {FrameKind}  {Convert.ToHexString(Data)}";
}
