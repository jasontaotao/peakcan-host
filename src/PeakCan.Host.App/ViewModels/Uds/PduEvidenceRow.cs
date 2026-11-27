using System.Globalization;
using PeakCan.Host.Core.Analysis.Uds;

namespace PeakCan.Host.App.ViewModels.Uds;

/// <summary>
/// 孤立响应 / 重复最终响应的一行。
/// <para>
/// <b>为什么不能只给计数：</b>spec §6.5 与 §12 都要求「单列<b>计数和证据</b>」。
/// 「孤立响应 3」告诉用户有三条没配对上，但说不清是哪三条、什么时候来的——
/// 而「三条孤立响应集中在 12.4s」和「散布在整段日志」是完全不同的诊断线索
/// （前者像并行诊断源，后者像地址配错）。
/// </para>
/// </summary>
public sealed class PduEvidenceRow
{
    private PduEvidenceRow(UdsOfflinePdu source) => Source = source;

    public UdsOfflinePdu Source { get; }

    /// <summary>证据序号，可直接回到原始帧。</summary>
    public int EvidenceIndex => Source.FirstEvidenceIndex;

    public double Timestamp => Source.FirstTimestamp;

    /// <summary>响应的 SID；空 PDU 时显示「—」，不猜。</summary>
    public string SidText => Source.Payload.Length == 0
        ? "—"
        : $"0x{Source.Payload[0]:X2}";

    /// <summary>多帧响应拼完没有。孤立响应常常是「日志从传输中途开始」的产物。</summary>
    public string CompletenessText => Source.IsComplete ? "完整" : "不完整";

    public string Display => string.Create(CultureInfo.InvariantCulture,
        $"证据 #{EvidenceIndex}  {Timestamp:F3}s  SID {SidText}  {Source.Payload.Length} 字节  {CompletenessText}");

    public static PduEvidenceRow From(UdsOfflinePdu pdu)
    {
        ArgumentNullException.ThrowIfNull(pdu);
        return new(pdu);
    }
}
