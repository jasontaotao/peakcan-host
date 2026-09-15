using System.Text;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Replay;

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
        // 委托给 Core 单源 formatter（F1-1）：之前内联格式发出 `0x` 前缀 id +
        // 空格分隔的独立 `x` token，AscParser 无法解析 → 导出的 .asc 无法回放。
        // 通道号经 ChannelIdToAscNumber 映射（PEAK USB1..16 → 1..16）。
        sb.AppendLine(AscFormat.FormatDataLine(
            frame, elapsedUs / 1_000_000.0, ChannelIdToAscNumber(frame.Channel)));
    }

    /// <summary>
    /// 将 ChannelId 映射到 PEAK .asc 文件的 channel 号。
    /// PEAK USB 通道 handle 0x51..0x60 → 1..16（handle - 0x50）；
    /// handle &lt; 0x51（含 None=0、TraceDrivenChannel placeholder=1 等）→ 1（单通道默认，与旧硬编码一致，零回归）；
    /// 其他（ZLG 0x8000+ 等）→ 3 + (handle 低字节)，保证 ≥3 且稳定（spec §7 开放项 2：ZLG 分配规则待精化）。
    /// </summary>
    internal static int ChannelIdToAscNumber(ChannelId id)
    {
        var h = id.Handle;
        if (h >= 0x51 && h <= 0x60)
            return h - 0x50;       // PEAK USB1..USB16 → 1..16
        if (h < 0x51)
            return 1;              // None/trace placeholder/小 handle → 1（单通道默认，旧硬编码值）
        return 3 + (h & 0xFF);     // ZLG/其他 ≥ 0x61 → ≥3
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
