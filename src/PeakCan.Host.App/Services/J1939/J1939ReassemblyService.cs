using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.J1939;

namespace PeakCan.Host.App.Services.J1939;

/// <summary>离线重组状态。</summary>
public enum ReassemblyStatus : byte
{
    /// <summary>所有 TP.DT 包收齐，消息经 <see cref="J1939TpLayer.MessageReceived"/> 交付。</summary>
    Complete,

    /// <summary>序号连续但输入结束时包未收齐（录制截断）；缺失字节以 0xFF 填充。</summary>
    Truncated,

    /// <summary>检出 TP.DT 序号跳变；缺失包区域以 0xFF 填充（J1939-21 §8.7）。</summary>
    PacketLoss,

    /// <summary>会话被表满驱逐（EvictIfFull）——无消息产出，仅诊断行（发散审查 MEDIUM-3）。</summary>
    Evicted,

    /// <summary>会话被同 (SA,DA) 新 CM 取代（supersede）——无消息产出，仅诊断行（发散审查 MEDIUM-3）。</summary>
    Superseded,
}

/// <summary>
/// 重组视图行。真实消息（Complete/Truncated/PacketLoss）时 <see cref="Message"/> 非空；
/// 被驱逐/取代会话（Evicted/Superseded）时 <see cref="Message"/> 为 null、仅
/// <see cref="TimestampSec"/>/<see cref="Detail"/> 承载诊断信息。
/// </summary>
/// <param name="TimestampSec">bus 秒（真实消息 = 完成时刻；诊断行 = 事件引发时刻）。</param>
/// <param name="Detail">诊断行补充描述；真实消息为 null。</param>
public sealed record ReassembledJ1939Message(
    double TimestampSec,
    J1939Message? Message,
    ReassemblyStatus Status,
    string? Detail = null);

/// <summary>
/// Trace Viewer L2 重组消息视图（spec §9.2）。无 UI 依赖，可单测。
/// 内部驱动一个 OfflineMode 的 <see cref="J1939TpLayer"/> 新实例：不启 watchdog、sendAsync 恒失败兜底、
/// 完整性判定经 <see cref="J1939TpLayer.FlushPendingSessions"/>（spec 修订 6）。
/// <para>
/// 发散审查 MEDIUM-3：订阅离线层 <see cref="J1939TpLayer.SessionEvent"/>——EvictIfFull/supersede
/// 在离线模式照跑，此前不订阅导致被驱逐/取代的会话从面板与摘要日志中**静默消失**。
/// 现收集为 Evicted/Superseded 诊断行暴露。
/// </para>
/// <para>partial 为 <see cref="LoggerMessageAttribute"/> 源生成所必需（Task 9 adapter 同款约束）。</para>
/// </summary>
public sealed partial class J1939ReassemblyService
{
    private readonly ILogger<J1939ReassemblyService> _logger;

    /// <summary>
    /// Construct the service. <paramref name="logger"/> is optional to mirror
    /// the null-logger tolerance pattern used by
    /// <see cref="Composition.J1939TpSinkAdapter"/> (test fixtures / back-compat
    /// callers); production DI always supplies one.
    /// </summary>
    public J1939ReassemblyService(ILogger<J1939ReassemblyService>? logger = null)
        => _logger = logger ?? NullLogger<J1939ReassemblyService>.Instance;

    /// <summary>
    /// 输入为单个 trace 源的帧列表；多源会话由调用方逐源调用，本服务不做源合并。
    /// <para>
    /// 喂层前按 <see cref="ReplayFrame.Timestamp"/> 稳定升序预排序（LINQ OrderBy 稳定：
    /// 同刻帧保持输入顺序）——重组严格依赖到达顺序，本服务自身的排序即输出顺序的钉子。
    /// 返回列表按 <see cref="ReassembledJ1939Message.TimestampSec"/> 升序（稳定排序，同刻按输入顺序）。
    /// </para>
    /// </summary>
    public IReadOnlyList<ReassembledJ1939Message> Reassemble(IReadOnlyList<ReplayFrame> frames)
    {
        var results = new List<ReassembledJ1939Message>();
        var dropped = new List<J1939SessionEvent>();
        var layer = new J1939TpLayer(
            (_, _) => ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.InvalidState, "offline reassembly never sends")),
            J1939TpOptions.Offline);
        layer.MessageReceived += m => results.Add(new ReassembledJ1939Message(m.CompletedTimestampSec, m, ReassemblyStatus.Complete));
        // 发散审查 MEDIUM-3：收集被驱逐/取代会话（离线模式仅 Superseded/Evicted 会引发——
        // Timeout 需 watchdog、PacketLoss 在线才作废），转诊断行并入返回列表。
        layer.SessionEvent += evt => dropped.Add(evt);

        var ordered = frames.OrderBy(f => f.Timestamp).ToList();
        int malformed = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            try
            {
                layer.ProcessFrame(ToCanFrame(ordered[i]));
            }
            catch (ArgumentException)
            {
                malformed++;
                LogSkippedMalformed(_logger, i);
            }
        }

        // Task 8 review note：FlushPendingSessions 按 Dictionary 枚举顺序返回（未钉死），
        // 不得依赖其顺序——追加前以会话自身时间戳 + 会话键 (Sa, Da) 稳定预排序，
        // 使最终 OrderBy 的同刻并列顺序与字典枚举顺序无关（(Sa, Da) 唯一标识会话，为全序）。
        var pendingResults = layer.FlushPendingSessions()
            .OrderBy(r => r.LastFrameTimestampSec)
            .ThenBy(r => r.FirstFrameTimestampSec)
            .ThenBy(r => r.Sa)
            .ThenBy(r => r.Da);
        foreach (var pending in pendingResults)
        {
            var message = new J1939Message(
                pending.Pgn, pending.Sa, pending.Da, pending.Priority, pending.Mode,
                pending.PartialPayload, pending.FirstFrameTimestampSec, pending.LastFrameTimestampSec);
            results.Add(new ReassembledJ1939Message(
                pending.LastFrameTimestampSec,
                message,
                pending.Outcome == J1939SessionOutcome.PacketLoss ? ReassemblyStatus.PacketLoss : ReassemblyStatus.Truncated));
        }

        foreach (var evt in dropped)
        {
            var status = evt.Kind switch
            {
                SessionEventKind.Evicted => ReassemblyStatus.Evicted,
                SessionEventKind.Superseded => ReassemblyStatus.Superseded,
                _ => (ReassemblyStatus?)null,   // 离线不会触发其它种类；防御性跳过
            };
            if (status is null)
                continue;
            results.Add(new ReassembledJ1939Message(evt.TimestampSec, null, status.Value, evt.Detail));
        }

        var sorted = results.OrderBy(r => r.TimestampSec).ToList();
        LogSummary(_logger, sorted.Count,
            sorted.Count(r => r.Status == ReassemblyStatus.Complete),
            sorted.Count(r => r.Status == ReassemblyStatus.Truncated),
            sorted.Count(r => r.Status == ReassemblyStatus.PacketLoss),
            sorted.Count(r => r.Status is ReassemblyStatus.Evicted or ReassemblyStatus.Superseded));
        return sorted;
    }

    /// <summary>
    /// ReplayFrame → CanFrame 适配：与 Infrastructure/Channel/TraceDrivenChannel.ToCanFrame 转换逻辑相同
    /// （6 行纯函数；不提取公共 helper 的决策：跨层公开 API 变更不值得，双侧均有单测钉住）。
    /// </summary>
    private static CanFrame ToCanFrame(ReplayFrame frame)
    {
        var format = frame.IsExtended ? FrameFormat.Extended : FrameFormat.Standard;
        var totalUs = (ulong)(frame.Timestamp * 1_000_000.0);
        return new CanFrame(
            new CanId(frame.Id, format),
            frame.Data,
            frame.Flags,
            ChannelId.None,
            new Timestamp(totalUs));
    }

    /// <summary>EventId 9311: per-call reassembly summary（总数 + 三态各自条数 + 丢弃诊断行数）。</summary>
    [LoggerMessage(EventId = 9311, Level = LogLevel.Information, Message = "J1939 reassembly: {Total} messages ({Complete} complete / {Truncated} truncated / {PacketLoss} loss / {Dropped} dropped)")]
    private static partial void LogSummary(ILogger logger, int total, int complete, int truncated, int packetLoss, int dropped);

    /// <summary>EventId 9312: 畸形 TP 帧被跳过（Index = 时间戳排序后喂层次序中的位置）。</summary>
    [LoggerMessage(EventId = 9312, Level = LogLevel.Warning, Message = "J1939 reassembly skipped malformed TP frame at index {Index}")]
    private static partial void LogSkippedMalformed(ILogger logger, int index);
}
