using System.IO;
using PeakCan.HIL.Core.HIL;
using PeakCan.Host.Infrastructure.Cli.Reporting;
using PeakCan.Host.Infrastructure.HIL.Reporting;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Reporting;

/// <summary>
/// HilReportService unit tests — 生成 HTML + 落盘 + 趋势记录 + 目录/命名策略。
/// 用临时目录注入，不污染 %LocalAppData%。
/// </summary>
public sealed class HilReportServiceTests
{
    private static TestSuiteResult SampleResult() => new(
        "SampleSuite", TotalCases: 2, PassedCases: 1, FailedCases: 1, SkippedCases: 0,
        ElapsedMs: 100, SetupFailures: Array.Empty<string>(),
        CaseResults: new[]
        {
            new TestCaseResult("tc1", "Case1", true, null, 50, 1, 1, 0, 0, 0, Array.Empty<StepResult>()),
            new TestCaseResult("tc2", "Case2", false, "boom", 50, 1, 0, 1, 0, 0, Array.Empty<StepResult>()),
        });

    [Fact]
    public void Generate_ReturnsHtmlAndFilePath()
    {
        var dir = CreateTempDir();
        try
        {
            var svc = new HilReportService(dir);
            var result = svc.Generate(SampleResult());

            Assert.Contains("<div class=\"summary", result.Html);
            Assert.StartsWith(dir, result.FilePath);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Generate_WritesFileToDisk()
    {
        var dir = CreateTempDir();
        try
        {
            var svc = new HilReportService(dir);
            var result = svc.Generate(SampleResult());

            Assert.True(File.Exists(result.FilePath));
            Assert.Contains("<!DOCTYPE html>", File.ReadAllText(result.FilePath));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Generate_CreatesDirectoryIfMissing()
    {
        var dir = CreateTempDir();
        try
        {
            Assert.False(Directory.Exists(dir));
            var svc = new HilReportService(dir);
            svc.Generate(SampleResult());

            Assert.True(Directory.Exists(dir));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Generate_ConsecutiveCalls_ProduceUniqueFilePaths()
    {
        var dir = CreateTempDir();
        try
        {
            var svc = new HilReportService(dir);
            var r1 = svc.Generate(SampleResult());
            var r2 = svc.Generate(SampleResult());

            // 毫秒精度文件名：同秒内多次 Run 不覆盖（B4）。
            Assert.NotEqual(r1.FilePath, r2.FilePath);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Generate_RecordsTrendEntry()
    {
        var dir = CreateTempDir();
        try
        {
            var svc = new HilReportService(dir);
            var trendsPath = Path.Combine(dir, "hil-trends.json");

            svc.Generate(SampleResult());

            var entries = TrendTracker.Load(trendsPath);
            Assert.Single(entries);
            Assert.Equal("SampleSuite", entries[0].SuiteName);
            Assert.Equal(2, entries[0].TotalCases);
            Assert.Equal(1, entries[0].PassedCases);
            Assert.Equal(1, entries[0].FailedCases);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Generate_CustomDirectory_WritesToTempDir()
    {
        var dir = CreateTempDir();
        try
        {
            var svc = new HilReportService(dir);
            var result = svc.Generate(SampleResult());

            // 文件写入注入的报告目录（注入优先于默认 %LocalAppData% 路径）。
            // 注意：不能用 DoesNotContain(LocalAppData) —— %TEMP% 在 Windows 上位于
            // %LocalAppData%\Temp 之下，临时目录天然包含该子串。StartsWith(dir) 已足够。
            Assert.StartsWith(dir, result.FilePath);
        }
        finally { TryDelete(dir); }
    }

    private static string CreateTempDir()
        => Path.Combine(Path.GetTempPath(), "hil-report-tests-" + Guid.NewGuid().ToString("N"));

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }
}
