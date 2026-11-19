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
    /// <returns>Signal-change events; empty when nothing can be decoded.</returns>
    public static IReadOnlyList<NmEvent> Build(DbcDocument dbc, IReadOnlyList<NmFrame> frames)
    {
        var events = new List<NmEvent>();
        foreach (var group in frames.GroupBy(f => f.Def.CanIdHex, StringComparer.Ordinal))
        {
            // CanIdHex 形如 "0x500"，去掉前缀后按十六进制解析。
            var id = uint.Parse(group.Key.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (!dbc.MessagesById.TryGetValue(id, out var message) || message.Signals.Count == 0)
                continue;

            var previous = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var frame in group.OrderBy(f => f.Frame.Timestamp))
            {
                foreach (var signal in message.Signals)
                {
                    // hil-core 的 API 是 Decode(data, signal) → double（无 TryDecode）。
                    var value = SignalDecoder.Decode(frame.Frame.Data, signal);
                    if (previous.TryGetValue(signal.Name, out var old))
                    {
                        if (Math.Abs(old - value) < 1e-9) continue;
                        events.Add(new NmEvent(frame.Frame.Timestamp, "signal_change", null, group.Key,
                            $"{signal.Name}: {old:F0} → {value:F0}", 0, "measured"));
                    }
                    previous[signal.Name] = value;
                }
            }
        }
        return events;
    }
}
