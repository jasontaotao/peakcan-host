using System.Globalization;
using PeakCan.HIL.Core.Dbc;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 复用 hil-core 的 <see cref="SignalDecoder"/> 解码 NM 报文上的 DBC 信号，
/// 只在值变化时产出事件。不解码时返回空，不报错。
/// </summary>
public static class NmSignalSummary
{
    /// <summary>
    /// Decode every DBC signal of each NM message and emit one
    /// <c>signal_change</c> event per value transition. Messages without
    /// signals, and CAN IDs absent from the DBC, contribute nothing.
    /// </summary>
    /// <param name="dbc">Parsed DBC document; <see cref="DbcDocument.MessagesById"/> is keyed by the bare CAN ID.</param>
    /// <param name="frames">NM frames already selected from the trace.</param>
    /// <returns>
    /// Signal-change events ordered by timestamp ascending (stable). Empty when
    /// nothing can be decoded.
    /// </returns>
    public static IReadOnlyList<NmEvent> Build(DbcDocument dbc, IReadOnlyList<NmFrame> frames)
        => Build(dbc, frames, int.MaxValue, out _);

    /// <summary>
    /// Same as <see cref="Build(DbcDocument, IReadOnlyList{NmFrame})"/>, but stops materialising
    /// events once <paramref name="maxEventsPerMessage"/> is reached for a CAN ID. Every
    /// transition still counts towards the attempted total, so the truncation record
    /// reflects how many events were dropped rather than silently shrinking the count.
    /// </summary>
    /// <param name="truncation">
    /// First CAN ID that hit the cap, with returned/attempted counts; null when nothing was dropped.
    /// </param>
    public static IReadOnlyList<NmEvent> Build(
        DbcDocument dbc,
        IReadOnlyList<NmFrame> frames,
        int maxEventsPerMessage,
        out NmTruncation? truncation)
    {
        var events = new List<NmEvent>();
        truncation = null;
        foreach (var group in frames.GroupBy(f => f.Def.CanIdHex, StringComparer.Ordinal))
        {
            // CanIdHex 形如 "0x500"，去掉前缀后按十六进制解析。
            var id = uint.Parse(group.Key.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (!dbc.MessagesById.TryGetValue(id, out var message) || message.Signals.Count == 0)
                continue;

            var previous = new Dictionary<string, double>(StringComparer.Ordinal);
            var emitted = 0;
            var attempted = 0;
            foreach (var frame in group.OrderBy(f => f.Frame.Timestamp))
            {
                foreach (var signal in message.Signals)
                {
                    // hil-core 的 API 是 Decode(data, signal) → double（无 TryDecode）。
                    var value = SignalDecoder.Decode(frame.Frame.Data, signal);
                    if (previous.TryGetValue(signal.Name, out var old))
                    {
                        if (double.IsNaN(old) && double.IsNaN(value)) continue;
                        if (Math.Abs(old - value) < 1e-9) continue;
                        attempted++;
                        // 达到上限后不再分配事件对象，但仍继续解码计数。
                        if (emitted < maxEventsPerMessage)
                        {
                            events.Add(new NmEvent(frame.Frame.Timestamp, "signal_change", null, group.Key,
                                $"{signal.Name}: {Format(old)} → {Format(value)}", 0, "measured"));
                            emitted++;
                        }
                    }
                    previous[signal.Name] = value;
                }
            }

            if (attempted > emitted)
                truncation ??= new NmTruncation("events:signals:" + group.Key, emitted, attempted);
        }
        // 契约：返回按时间升序；LINQ OrderBy 是稳定排序。
        return events.OrderBy(e => e.T).ToList();
    }

    // 定点小数格式：触发判据是 1e-9 绝对容差，文本不能再四舍五入到整数位，
    // 否则 0.1 级的变化会显示成 "85 → 85"。
    private static string Format(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);
}
