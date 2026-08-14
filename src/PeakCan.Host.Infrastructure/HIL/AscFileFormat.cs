using System.Text;
using PeakCan.HIL.Core;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>PEAK ASCII (.asc) 文件格式共享 helper。FrameCaptureExporter（CLI）与 AscFrameSink（WPF 流式）同源，
/// 逐字节一致。internal，同程序集可见。</summary>
internal static class AscFileFormat
{
    public static void WriteHeader(StringBuilder sb)
    {
        sb.AppendLine($"date Fri Jan 01 00:00:00.000 {DateTime.Now:yyyy}");
        sb.AppendLine("base hex  timestamps absolute");
        sb.AppendLine("internal events logged");
        sb.AppendLine("// version 8.5.0");
    }

    public static void WriteFrameLine(StringBuilder sb, CanFrame frame, double elapsedUs)
    {
        var seconds = elapsedUs / 1_000_000.0;
        var idStr = frame.Id.IsExtended ? $"0x{frame.Id.Raw:X8}" : $"0x{frame.Id.Raw:X3}";
        var dlc = frame.Data.Length;
        var dataHex = BitConverter.ToString(frame.Data.Span.ToArray()).Replace("-", " ");
        sb.AppendLine($"{seconds,12:F6} 1  {idStr,-12}x       Rx d {dlc} {dataHex}");
    }

    public static string SanitizeFileName(string name, int maxLength = int.MaxValue)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        if (sb.Length > maxLength) sb.Length = maxLength;
        return sb.ToString();
    }
}
