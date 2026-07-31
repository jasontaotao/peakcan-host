using System.Globalization;
using System.Text;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL;

namespace PeakCan.Host.Infrastructure.Cli.Reporting;

/// <summary>
/// Exports captured CAN frames around failure points to PEAK ASCII trace (.asc) files.
/// One file per failed case that has <see cref="StepResult.FramesAroundFailure"/>.
/// </summary>
public static class FrameCaptureExporter
{
    private const int MaxFramesPerFile = 50;

    /// <summary>
    /// Write .asc files for each case with failed steps that have captured frames.
    /// Creates <paramref name="directory"/> if it doesn't exist.
    /// </summary>
    public static async Task ExportAsync(TestSuiteResult result, string directory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);

        foreach (var c in result.CaseResults)
        {
            // Collect all frames from failed steps in this case
            var frames = new List<CanFrame>();
            foreach (var step in c.StepResults)
            {
                if (step.Status != StepStatus.Failed || step.FramesAroundFailure is null)
                    continue;

                foreach (var frame in step.FramesAroundFailure)
                {
                    if (frames.Count >= MaxFramesPerFile) break;
                    frames.Add(frame);
                }
                if (frames.Count >= MaxFramesPerFile) break;
            }

            if (frames.Count == 0) continue;

            var fileName = SanitizeFileName(c.TestCaseName) + ".asc";
            var filePath = Path.Combine(directory, fileName);
            await WriteAscFileAsync(filePath, frames, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Write frames in PEAK ASCII trace format.
    /// </summary>
    private static async Task WriteAscFileAsync(string path, List<CanFrame> frames, CancellationToken ct)
    {
        var sb = new StringBuilder();

        // Header
        sb.AppendLine($"date Fri Jan 01 00:00:00.000 {DateTime.Now:yyyy}");
        sb.AppendLine("base hex  timestamps absolute");
        sb.AppendLine("internal events logged");
        sb.AppendLine("// version 8.5.0");

        // Frames
        double timestampOffsetUs = 0;
        if (frames.Count > 0)
            timestampOffsetUs = frames[0].Timestamp.TotalMicroseconds;

        foreach (var frame in frames)
        {
            ct.ThrowIfCancellationRequested();

            var elapsedUs = frame.Timestamp.TotalMicroseconds - timestampOffsetUs;
            var seconds = elapsedUs / 1_000_000.0;

            // Format:   0.000000 1  18FEF100x       Rx d 8 01 02 03 04 05 06 07 08
            var idStr = frame.Id.IsExtended
                ? $"0x{frame.Id.Raw:X8}"
                : $"0x{frame.Id.Raw:X3}";

            var dlc = frame.Data.Length;
            var dataHex = BitConverter.ToString(frame.Data.Span.ToArray()).Replace("-", " ");

            sb.AppendLine(
                $"{seconds,12:F6} 1  {idStr,-12}x       Rx d {dlc} {dataHex}");
        }

        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Replace invalid filename characters with underscore.
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        }
        return sb.ToString();
    }
}
