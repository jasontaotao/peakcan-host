using System.IO;
using PeakCan.Host.Core;

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
        else
        {
            _writer.WriteLine($"date {DateTime.UtcNow:ddd MMM dd HH:mm:ss yyyy}");
            _writer.WriteLine($"base hex  timestamps absolute");
            _writer.WriteLine($"no internal events logged");
        }
    }

    private void WriteFooter()
    {
        if (_writer is null) return;
        if (_format == RecordFormat.Asc)
        {
            var elapsed = DateTime.UtcNow - _startTime;
            _writer.WriteLine();
            _writer.WriteLine($"// {elapsed.TotalSeconds:F3} s");
        }
    }

    private void WriteFrame(CanFrame frame)
    {
        if (_writer is null) return;
        var elapsed = DateTime.UtcNow - _startTime;
        var dataHex = Convert.ToHexString(frame.Data.Span);

        if (_format == RecordFormat.Csv)
        {
            _writer.WriteLine(
                $"{elapsed.TotalSeconds:F6},{frame.Channel.Handle:X2},0x{frame.Id.Raw:X},{frame.Dlc},{dataHex},{FormatFlags(frame)}");
        }
        else
        {
            // ASC format: timestamp channel ID dlc data flags
            var fdFlag = frame.IsFd ? "  fd" : "";
            var brsFlag = (frame.Flags & FrameFlags.BitRateSwitch) != 0 ? " brs" : "";
            var esiFlag = (frame.Flags & FrameFlags.ErrorStateIndicator) != 0 ? " esi" : "";
            var errFlag = frame.IsError ? " error" : "";
            _writer.WriteLine(
                $"{elapsed.TotalSeconds:F6} {frame.Channel.Handle:X2}  {frame.Id.Raw:X}  {frame.Dlc}  {dataHex}{fdFlag}{brsFlag}{esiFlag}{errFlag}");
        }
    }

    private static string FormatFlags(CanFrame frame)
    {
        var flags = new List<string>();
        if (frame.IsFd) flags.Add("FD");
        if ((frame.Flags & FrameFlags.BitRateSwitch) != 0) flags.Add("BRS");
        if ((frame.Flags & FrameFlags.ErrorStateIndicator) != 0) flags.Add("ESI");
        if (frame.IsError) flags.Add("ERR");
        return string.Join("|", flags);
    }
}