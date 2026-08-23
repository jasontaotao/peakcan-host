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
        var chNum = ChannelIdToAscNumber(frame.Channel);
        sb.AppendLine($"{seconds,12:F6} {chNum}  {idStr,-12}x       Rx d {dlc} {dataHex}");
    }

    /// <summary>
    /// 将 ChannelId 映射到 PEAK .asc 文件的 channel 号。
    /// PEAK USB 通道 handle 0x51..0x60 → 1..16（handle - 0x50）；
    /// 其他（ZLG 0x8000+ 等）→ 3 + (handle 低字节)，保证 ≥3 且稳定（spec §7 开放项 2：ZLG 分配规则待精化）。
    /// 单通道（ChannelId.None=0）→ 1（与旧行为一致：硬编码 channel 1）。
    /// </summary>
    internal static int ChannelIdToAscNumber(ChannelId id)
    {
        var h = id.Handle;
        if (h >= 0x51 && h <= 0x60)
            return h - 0x50;       // PEAK USB1..USB16 → 1..16
        if (h == 0)
            return 1;              // None/单通道默认 → 1（旧硬编码值）
        return 3 + (h & 0xFF);     // ZLG/其他 → ≥3
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
