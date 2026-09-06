using System.Globalization;
using PeakCan.HIL.Core;

namespace PeakCan.Host.Infrastructure.Statistics;

/// <summary>
/// Maps <see cref="BaudRate"/> presets to their nominal (arbitration-phase)
/// bitrate in bits/second, for <see cref="BusStatisticsCollector.SetBitrate"/>.
/// <para>
/// FD presets carry a 1 Mbps nominal phase by construction (see the
/// descriptor strings in Core) — only the data phase differs, and the load
/// formula deliberately normalizes against the nominal rate (see the FD
/// caveat in <see cref="BusStatisticsCollector"/>). Unknown/custom rates
/// (e.g. <see cref="BaudRate.FromFdDescriptor"/>) fall back to a
/// name-string parse, then to the 1 Mbps default — never throw.
/// </para>
/// </summary>
public static class BaudRateMap
{
    /// <summary>Fallback used when a preset is unrecognized and its name cannot be parsed.</summary>
    public const long DefaultBps = 1_000_000;

    /// <summary>
    /// Resolve the nominal bitrate for a preset. Preset matching is by
    /// record equality (all seven presets in Core); unknown names go
    /// through a lenient parse ("125 kbps" → 125_000, "2 Mbps (FD)" →
    /// 2_000_000 reads the data phase but FD callers are matched by
    /// preset anyway) and finally fall back to <see cref="DefaultBps"/>.
    /// </summary>
    public static long NominalBps(BaudRate rate)
    {
        if (rate == BaudRate.Can125kbps) return 125_000;
        if (rate == BaudRate.Can250kbps) return 250_000;
        if (rate == BaudRate.Can500kbps) return 500_000;
        if (rate == BaudRate.Can1Mbps) return 1_000_000;
        if (rate == BaudRate.CanFd1Mbps || rate == BaudRate.CanFd2Mbps || rate == BaudRate.CanFd5Mbps)
            return 1_000_000; // FD 预设的标称相位恒为 1 Mbps（见 BaudRate 描述符）

        // 未知/自定义预设：宽松解析 Name（"500 kbps" / "2 Mbps (FD)"）。
        // InvariantCulture：Name 是协议描述串，不受用户区域小数符影响
        //（"1.5 Mbps" 在逗号小数文化下若用当前文化解析会静默失败）。
        // IsFinite + 上界护栏：防 "1e300 kbps" 之类病态名溢出 long —— 本
        // 方法承诺不抛异常，越界一律回退默认 1 Mbps。
        var name = rate.Name.AsSpan();
        var space = name.IndexOf(' ');
        if (space > 0
            && double.TryParse(name[..space], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value)
            && value > 0
            && value <= 1_000_000)
        {
            var unit = name[(space + 1)..];
            if (unit.StartsWith("kbps", StringComparison.OrdinalIgnoreCase)) return (long)(value * 1_000);
            if (unit.StartsWith("Mbps", StringComparison.OrdinalIgnoreCase)) return (long)(value * 1_000_000);
        }
        return DefaultBps;
    }
}
