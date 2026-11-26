namespace PeakCan.Host.Core.Uds;

/// <summary>
/// UDS **客户端**超时管理：P2（请求→响应）与 P2*（收到 NRC 0x78 后的追加等待）。
/// <para>
/// 只保留客户端真正会读的两个值。此前的 <c>S3Timeout</c> 零读取（S3 由
/// <c>UdsSession.S3Timeout</c> 统一管理），<c>P3StarTimeout</c> 是**服务器侧**语义
/// （ECU 等客户端发下一帧的超时），客户端没有对应状态——留着只会让人以为支持。
/// </para>
/// </summary>
public sealed class UdsTimer
{
    /// <summary>P2 默认值：请求到响应的最大间隔（ISO 14229-1 <c>P2Server_max</c> 的客户端侧预算）。</summary>
    public static readonly TimeSpan DefaultP2Timeout = TimeSpan.FromMilliseconds(50);

    /// <summary>P2* 默认值：收到 NRC 0x78 后允许 ECU 继续处理的时间（<c>P2*Server_max</c>）。</summary>
    public static readonly TimeSpan DefaultP2StarTimeout = TimeSpan.FromMilliseconds(5000);

    /// <summary>P2 timeout (ms) — time between request and response.</summary>
    public TimeSpan P2Timeout { get; set; } = DefaultP2Timeout;

    /// <summary>P2* timeout (ms) — time after NRC 0x78.</summary>
    public TimeSpan P2StarTimeout { get; set; } = DefaultP2StarTimeout;

    /// <summary>
    /// 把 P2 / P2* 恢复成构造默认值。断连复位时由
    /// <c>UdsClient.ResetDiagnosticState</c> 调用——这是「真正生效」的那份超时
    /// （<c>TransportFlow</c> 的 <c>CancelAfter</c> 读的就是它），所以复位必须以
    /// 这里为准，而不是只改 <see cref="UdsSession"/> 的展示值。
    /// <para>
    /// 只复位 P2 / P2*：S3 由保活循环自己管理，P3* 是服务器侧语义，客户端没有
    /// 对应状态可复位。
    /// </para>
    /// </summary>
    public void ResetToDefaults()
    {
        P2Timeout = DefaultP2Timeout;
        P2StarTimeout = DefaultP2StarTimeout;
    }
}
