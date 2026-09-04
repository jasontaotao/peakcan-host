using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using PeakCan.HIL.Core.Replay;

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
}

/// <summary>重组视图行。</summary>
/// <param name="Message">重组得到的应用消息（未闭合会话为 0xFF 填充的部分载荷）。</param>
/// <param name="Status">完整性判定。</param>
public sealed record ReassembledJ1939Message(J1939Message Message, ReassemblyStatus Status);

/// <summary>
/// Trace Viewer L2 重组消息视图（spec §9.2）。无 UI 依赖，可单测。
/// 内部驱动一个 OfflineMode 的 <see cref="J1939TpLayer"/> 新实例：不启 watchdog、sendAsync 恒失败兜底、
/// 完整性判定经 <see cref="J1939TpLayer.FlushPendingSessions"/>（spec 修订 6）。
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
    /// 返回列表按 CompletedTimestampSec 升序（稳定排序，同刻按输入顺序）。
    /// </para>
    /// </summary>
    public IReadOnlyList<ReassembledJ1939Message> Reassemble(IReadOnlyList<ReplayFrame> frames)
    {
        var results = new List<ReassembledJ1939Message>();
        var layer = new J1939TpLayer(
            (_, _) => ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.InvalidState, "offline reassembly never sends")),
            J1939TpOptions.Offline);
        layer.MessageReceived += m => results.Add(new ReassembledJ1939Message(m, ReassemblyStatus.Complete));

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
                message,
                pending.Outcome == J1939SessionOutcome.PacketLoss ? ReassemblyStatus.PacketLoss : ReassemblyStatus.Truncated));
        }

        var sorted = results.OrderBy(r => r.Message.CompletedTimestampSec).ToList();
        LogSummary(_logger, sorted.Count,
            sorted.Count(r => r.Status == ReassemblyStatus.Complete),
            sorted.Count(r => r.Status == ReassemblyStatus.Truncated),
            sorted.Count(r => r.Status == ReassemblyStatus.PacketLoss));
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

    /// <summary>EventId 9311: per-call reassembly summary（总数 + 三态各自条数）。</summary>
    [LoggerMessage(EventId = 9311, Level = LogLevel.Information, Message = "J1939 reassembly: {Total} messages ({Complete} complete / {Truncated} truncated / {PacketLoss} loss)")]
    private static partial void LogSummary(ILogger logger, int total, int complete, int truncated, int packetLoss);

    /// <summary>EventId 9312: 畸形 TP 帧被跳过（Index = 时间戳排序后喂层次序中的位置）。</summary>
    [LoggerMessage(EventId = 9312, Level = LogLevel.Warning, Message = "J1939 reassembly skipped malformed TP frame at index {Index}")]
    private static partial void LogSkippedMalformed(ILogger logger, int index);
}
