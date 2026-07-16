using FluentAssertions;
using PeakCan.Host.Core.Analysis;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis;

public class FaultEventTests
{
    [Fact]
    public void Constructor_Default500msWindow_PopulatesFields()
    {
        // Per spec D3: default ±500ms window
        var evt = new FaultEvent(
            CenterTimestampSeconds: 1.234,
            WindowBefore: TimeSpan.FromMilliseconds(500),
            WindowAfter: TimeSpan.FromMilliseconds(500),
            Description: "BmsFaultState fault",
            CreatedAtUtc: new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc));

        evt.WindowBefore.Should().Be(TimeSpan.FromMilliseconds(500));
        evt.WindowAfter.Should().Be(TimeSpan.FromMilliseconds(500));
        evt.Description.Should().Be("BmsFaultState fault");
    }
}