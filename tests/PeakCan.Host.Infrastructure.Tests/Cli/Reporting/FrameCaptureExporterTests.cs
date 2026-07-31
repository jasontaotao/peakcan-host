using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.Cli.Reporting;

namespace PeakCan.Host.Infrastructure.Tests.Cli.Reporting;

public class FrameCaptureExporterTests
{
    private static string GetTempDir() => Path.Combine(Path.GetTempPath(), $"hil_export_{Guid.NewGuid():N}");

    [Fact]
    public async Task FrameExporter_WritesAscFormat()
    {
        var dir = GetTempDir();
        var frame = new CanFrame(
            new CanId(0x123, FrameFormat.Standard),
            new ReadOnlyMemory<byte>(new byte[] { 0x01, 0x02, 0x03 }),
            FrameFlags.None, ChannelId.None, new Timestamp(1000000));

        var step = new StepResult(0, TestCaseStepKind.AssertSignal, "s1", StepStatus.Failed,
            "fail", null, null, 0, new[] { frame });

        var caseResult = new TestCaseResult("Fail_Case", "Fail_Case", false, "fail", 10, 1, 0, 1, 0, 0, new[] { step });
        var result = new TestSuiteResult("Suite", 1, 0, 1, 0, 100, Array.Empty<string>(), new[] { caseResult });

        try
        {
            await FrameCaptureExporter.ExportAsync(result, dir);

            var files = Directory.GetFiles(dir, "*.asc");
            Assert.Single(files);

            var content = await File.ReadAllTextAsync(files[0]);
            Assert.Contains("date", content);
            Assert.Contains("01 02 03", content); // frame data
            Assert.Contains("0.000000", content); // timestamp
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task FrameExporter_FramesCappedAt50()
    {
        var dir = GetTempDir();
        var frames = new List<CanFrame>();
        for (int i = 0; i < 60; i++)
        {
            frames.Add(new CanFrame(
                new CanId(0x123, FrameFormat.Standard),
                new ReadOnlyMemory<byte>(new byte[] { (byte)i }),
                FrameFlags.None, ChannelId.None, new Timestamp((ulong)i * 1000)));
        }

        var step = new StepResult(0, TestCaseStepKind.AssertSignal, "s1", StepStatus.Failed,
            "fail", null, null, 0, frames);

        var caseResult = new TestCaseResult("Fail_Case", "Fail_Case", false, "fail", 10, 1, 0, 1, 0, 0, new[] { step });
        var result = new TestSuiteResult("Suite", 1, 0, 1, 0, 100, Array.Empty<string>(), new[] { caseResult });

        try
        {
            await FrameCaptureExporter.ExportAsync(result, dir);

            var files = Directory.GetFiles(dir, "*.asc");
            Assert.Single(files);

            var lines = (await File.ReadAllLinesAsync(files[0]))
                .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("date", StringComparison.Ordinal) && !l.StartsWith("base", StringComparison.Ordinal) && !l.StartsWith("internal", StringComparison.Ordinal) && !l.StartsWith("//", StringComparison.Ordinal))
                .ToList();

            Assert.True(lines.Count <= 50, $"Expected <= 50 frame lines, got {lines.Count}");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
