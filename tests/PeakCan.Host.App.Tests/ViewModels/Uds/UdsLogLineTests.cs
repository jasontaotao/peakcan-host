using FluentAssertions;
using PeakCan.Host.App.ViewModels.Uds;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Uds;

public sealed class UdsLogLineTests
{
    [Fact]
    public void Record_Exposes_Timestamp_Level_Message()
    {
        var line = new UdsLogLine("12:34:56.789", "Info", "hello");

        line.Timestamp.Should().Be("12:34:56.789");
        line.Level.Should().Be("Info");
        line.Message.Should().Be("hello");
    }

    [Fact]
    public void Record_Supports_Value_Equality()
    {
        var a = new UdsLogLine("t", "Warn", "m");
        var b = new UdsLogLine("t", "Warn", "m");
        var c = new UdsLogLine("t", "Error", "m");

        a.Should().Be(b);
        a.Should().NotBe(c);
    }
}
