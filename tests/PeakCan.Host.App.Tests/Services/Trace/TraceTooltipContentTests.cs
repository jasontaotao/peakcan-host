using FluentAssertions;
using PeakCan.Host.App.Services.Trace;

namespace PeakCan.Host.App.Tests.Services.Trace;

public class TraceTooltipContentTests
{
    [Fact]
    public void Format_UsesTraceTimeFormatter_And_AppendsUnit()
    {
        var content = TraceTooltipContent.Format(
            "Engine.RPM",
            158340.51012,
            new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc),
            2450.5,
            "rpm");

        content.DisplayName.Should().Be("Engine.RPM");
        content.Time.Should().Be("158340.5101");
        content.Value.Should().Be("2450.50 rpm");
    }

    [Fact]
    public void Format_OmitsUnit_WhenUnitIsMissing()
    {
        var content = TraceTooltipContent.Format("State", 12.3456, null, 1.234, null);

        content.Value.Should().Be("1.23");
    }

    [Fact]
    public void Format_TrimsWhitespaceUnit()
    {
        var content = TraceTooltipContent.Format("Speed", 0, null, -2, " km/h ");

        content.Value.Should().Be("-2.00 km/h");
    }
}
