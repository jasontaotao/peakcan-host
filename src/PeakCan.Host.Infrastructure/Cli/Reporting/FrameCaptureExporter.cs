using System.Text;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.Host.Infrastructure.HIL;

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

            var fileName = AscFileFormat.SanitizeFileName(c.TestCaseName) + ".asc";
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

        AscFileFormat.WriteHeader(sb);

        double timestampOffsetUs = 0;
        if (frames.Count > 0)
            timestampOffsetUs = frames[0].Timestamp.TotalMicroseconds;

        foreach (var frame in frames)
        {
            ct.ThrowIfCancellationRequested();

            var elapsedUs = frame.Timestamp.TotalMicroseconds - timestampOffsetUs;
            AscFileFormat.WriteFrameLine(sb, frame, elapsedUs);
        }

        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, ct).ConfigureAwait(false);
    }
}
