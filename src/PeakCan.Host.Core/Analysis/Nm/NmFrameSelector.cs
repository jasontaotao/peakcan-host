using PeakCan.HIL.Core;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Analysis.Nm;

public sealed record NmSelectionDiagnostics(
    int ErrorFrames,
    int RtrFrames,
    int DroppedFrames,
    int TransmitFrames,
    int ReceiveFrames,
    int DirectionUnknownFrames,
    bool TimestampRegression,
    bool MultiChannel,
    // 第一处时间戳回退的位置。spec §9.1 要求体检报告受影响区间，bool 装不下；
    // 只在 TimestampRegression 为 true 时非 null。
    double? TimestampRegressionAt = null);

/// <summary>
/// 把原始帧归一到"可分析的 NM 输入"：单通道、窗口内、时间升序、去掉错误帧与 RTR。
/// 不做 J1939 虚拟帧合并——分析只看总线上的真实帧。
/// </summary>
public static class NmFrameSelector
{
    public static IReadOnlyList<ReplayFrame> Select(
        NmAnalysisRequest request, out NmSelectionDiagnostics diagnostics)
    {
        var frames = request.Frames;

        var channels = frames.Select(f => f.Channel).Distinct().Take(2).ToArray();
        var multiChannel = request.Channel is null && channels.Length > 1;
        if (multiChannel)
        {
            diagnostics = new NmSelectionDiagnostics(0, 0, frames.Count, 0, 0, 0, false, true);
            return Array.Empty<ReplayFrame>();
        }

        var filtered = new List<ReplayFrame>(frames.Count);
        bool regression = false;
        double? firstRegressionAt = null;
        double previous = double.NegativeInfinity;
        int errors = 0, rtr = 0, tx = 0, rx = 0, unknown = 0, dropped = 0;

        foreach (var frame in frames)
        {
            if (request.Channel is { } channel && frame.Channel != channel) { dropped++; continue; }
            if (frame.Timestamp < request.Start || frame.Timestamp >= request.End) { dropped++; continue; }
            if ((frame.Flags & FrameFlags.ErrFrame) != 0) { errors++; continue; }
            if ((frame.Flags & FrameFlags.Rtr) != 0) { rtr++; continue; }
            if (frame.Timestamp < previous)
            {
                regression = true;
                // 只记第一处：体检报告的是「从哪开始不可靠」，不是回退次数。
                firstRegressionAt ??= frame.Timestamp;
            }
            previous = frame.Timestamp;
            if (frame.IsTransmit == true) tx++;
            else if (frame.IsTransmit == false) rx++;
            else unknown++;
            filtered.Add(frame);
        }

        filtered.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        diagnostics = new NmSelectionDiagnostics(
            errors, rtr, dropped, tx, rx, unknown, regression, false, firstRegressionAt);
        return filtered;
    }
}
