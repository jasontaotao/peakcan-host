// src/PeakCan.Host.App/Services/RecordService/Format.partial.cs — v3.49.0 MINOR (T2 of 3)
// Q3: 改为委托给 PeakCan.Host.Core.Replay.AscFormat (writer 端单源)。
// 之前 67 LoC 拥有内联 WriteHeader/WriteFooter/WriteFrame/FormatFlags 4 个方法。
// 现在 ≈ 30 LoC，只保留 ASC 分支 delegate 给 AscFormat + CSV 分支保持内联 (CSV 用 `|` 分隔符，不同于 ASC 空格分隔)。
//
// W23 STRUCT-FABRACTION LESSON (recap): CanFrame.IsFd / IsError / Flags /
// Channel.Handle / Id.Raw / Dlc / Data + FrameFlags 5 个 bitflag 值 —
// 全部已通过 AscFormat 子方法 (FormatFlagsCompact + WriteDataLine) 间接验证。

using System.IO;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.Services;

public sealed partial class RecordService
{
    private void WriteHeader()
    {
        if (_writer is null) return;
        if (_format == RecordFormat.Csv)
        {
            _writer.WriteLine("timestamp,channel,id,dlc,data,flags");
        }
        else if (_writer is StreamWriter ascWriter)
        {
            AscFormat.WriteHeader(ascWriter, DateTime.UtcNow);
        }
    }

    private void WriteFooter()
    {
        if (_writer is null) return;
        if (_format == RecordFormat.Asc)
        {
            var elapsed = DateTime.UtcNow - _startTime;
            if (_writer is StreamWriter ascWriter)
            {
                AscFormat.WriteFooter(ascWriter, elapsed);
            }
        }
    }

    private void WriteFrame(CanFrame frame)
    {
        if (_writer is null) return;
        var elapsed = DateTime.UtcNow - _startTime;
        if (_format == RecordFormat.Csv)
        {
            var dataHex = Convert.ToHexString(frame.Data.Span);
            _writer.WriteLine(
                $"{elapsed.TotalSeconds:F6},{frame.Channel.Handle:X2},0x{frame.Id.Raw:X},{frame.Dlc},{dataHex},{FormatFlags(frame)}");
        }
        else if (_writer is StreamWriter ascWriter)
        {
            AscFormat.WriteDataLine(ascWriter, frame, elapsed);
        }
    }

    private static string FormatFlags(CanFrame frame)
    {
        // CSV 格式用 `|` 分隔符，与 ASC 空格分隔不同；保留内联实现。
        var flags = new List<string>();
        if (frame.IsFd) flags.Add("FD");
        if ((frame.Flags & FrameFlags.BitRateSwitch) != 0) flags.Add("BRS");
        if ((frame.Flags & FrameFlags.ErrorStateIndicator) != 0) flags.Add("ESI");
        if (frame.IsError) flags.Add("ERR");
        return string.Join("|", flags);
    }
}
