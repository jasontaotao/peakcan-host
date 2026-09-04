namespace PeakCan.Host.App.Services.Nodes;

/// <summary>报文到达记录（供行为引擎做触发匹配）。</summary>
/// <param name="Ref">报文引用（触发匹配用宽容匹配 <see cref="MessageRefMatcher.Matches"/>）。</param>
/// <param name="Sa">源地址（发送方 SA）。</param>
/// <param name="Payload">应用层载荷字节。</param>
/// <param name="TimestampSec">后端时间戳（秒）。</param>
public sealed record NodeMessageArrived(MessageRef Ref, byte Sa, byte[] Payload, double TimestampSec);
