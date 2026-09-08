using System.Globalization;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>
/// 只读扫描 ASC 文件确定总时长 + 帧数 + wall-clock origin，不保留帧对象。
/// 帧序假设时间有序（ASC 天然如此）；乱序文件的总时长为近似值。
/// 优化：只提取 timestamp，跳过完整帧解析（data bytes/channel/flags 均不分配）。
/// </summary>
public static class DurationScanner
{
    public static async Task<DurationScanResult> ScanAsync(
        Stream stream,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        long total = stream.CanSeek ? stream.Length : 0;
        long bytesRead = 0;
        long count = 0;
        double first = double.NaN, last = 0;
        DateTime? origin = null;

        using var reader = new StreamReader(stream, leaveOpen: true);
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            bytesRead += line.Length + 1;
            var span = line.AsSpan().Trim();
            if (span.StartsWith("date ", StringComparison.Ordinal))
                origin ??= AscFormat.TryParseDateHeader(span.ToString());

            // 轻量时间戳提取：只需第一个 token 是合法 double。
            // ASC data line 格式 "0.000000 1  100 Rx d 8 01 02..." — 第一个字段是时间戳。
            var spaceIdx = span.IndexOf(' ');
            if (spaceIdx > 0 &&
                double.TryParse(span.Slice(0, spaceIdx), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var ts))
            {
                if (count == 0) first = ts;
                last = ts;
                count++;
            }
            if (progress is not null && total > 0 && (count & 0xFFFF) == 0 && count > 0)
                progress.Report((double)bytesRead / total);
        }
        if (progress is not null && total > 0) progress.Report(1.0);

        double duration = count > 0 && !double.IsNaN(first) ? last - first : 0;
        return new DurationScanResult(duration, count, origin);
    }
}



