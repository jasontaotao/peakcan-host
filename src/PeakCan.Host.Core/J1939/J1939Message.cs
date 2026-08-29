namespace PeakCan.HIL.Core.J1939;

/// <summary>TP 传输模式。<see cref="Single"/> 为不经 TP 的单帧应用报文（spec 修订 P0-1）。</summary>
public enum TpMode : byte
{
    Bam = 0,
    RtsCts = 1,
    Single = 2,
}

/// <summary>重组完成的应用消息。</summary>
/// <remarks>
/// <see cref="Da"/> 为传输层目标地址：BAM 广播恒 0xFF，RTS/CTS 为对端地址。
/// 两个时间戳均为 double 秒（实时=源通道基准；回放=录制起点），基准对齐由消费方负责。
/// </remarks>
public sealed record J1939Message(
    uint Pgn,
    byte Sa,
    byte Da,
    byte Priority,
    TpMode Mode,
    byte[] Payload,
    double FirstFrameTimestampSec,
    double CompletedTimestampSec);

/// <summary>会话异常事件种类。</summary>
public enum SessionEventKind : byte
{
    Superseded,
    PacketLoss,
    Timeout,
    Evicted,
}

/// <summary>会话异常事件（在调用 ProcessFrame 的线程同步引发）。</summary>
public sealed record J1939SessionEvent(
    SessionEventKind Kind,
    byte Sa,
    byte Da,
    uint Pgn,
    TpMode Mode,
    string Detail);

/// <summary>离线 flush 的未闭合会话结局。</summary>
public enum J1939SessionOutcome : byte
{
    /// <summary>输入结束时序号连续但包未收齐（录制截断）。</summary>
    Truncated,

    /// <summary>检出序号跳变，缺失字节以 0xFF 填充。</summary>
    PacketLoss,
}

/// <summary>离线 flush 返回的未闭合会话结果（spec 修订 6：离线结算 API）。</summary>
public sealed record J1939SessionResult(
    byte Sa,
    byte Da,
    byte Priority,
    uint Pgn,
    TpMode Mode,
    J1939SessionOutcome Outcome,
    byte[] PartialPayload,
    double FirstFrameTimestampSec,
    double LastFrameTimestampSec);
