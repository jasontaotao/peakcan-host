using System.Text;
using FluentAssertions;
using PeakCan.Host.Mobile.Core.Services;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.Services;

public class DurationScannerTests
{
    private static MemoryStream Asc(string content) => new(Encoding.UTF8.GetBytes(content));

    [Fact]
    public async Task ScanAsync_ReportsDurationAndFrameCount()
    {
        const string asc = """
date Wed Jul 1 10:00:00.000 2026
 0.000000 51  100  2  01 02
 0.500000 51  200  2  03 04
 2.000000 51  100  2  05 06
""";
        var result = await DurationScanner.ScanAsync(Asc(asc));
        result.FrameCount.Should().Be(3);
        result.DurationSeconds.Should().Be(2.0);
        result.WallClockOrigin.Should().NotBeNull();
    }

    [Fact]
    public async Task ScanAsync_EmptyStream_ReturnsZero()
    {
        var result = await DurationScanner.ScanAsync(Asc(""));
        result.FrameCount.Should().Be(0);
        result.DurationSeconds.Should().Be(0);
    }
}

