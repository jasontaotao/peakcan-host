using FluentAssertions;
using PeakCan.Host.Core;
using Xunit;

namespace PeakCan.Host.Core.Tests;

public class TimestampTests
{
    [Fact]
    public void FromMillis_Composes_Millis_And_Micros()
        => Timestamp.FromMillis(123, 456).TotalMicroseconds.Should().Be(123_456UL);

    [Fact]
    public void FromMillis_Handles_Zero()
        => Timestamp.FromMillis(0, 0).TotalMicroseconds.Should().Be(0UL);

    [Fact]
    public void ToString_Formats_Zero_Time()
        => default(Timestamp).ToString().Should().Be("00:00:00.000000");

    [Fact]
    public void ToString_Formats_Hour_Minute_Second_Microseconds()
    {
        // 3 661 s = 01:01:01; +1 500 us = 01:01:01.001500
        Timestamp.FromMillis(3_661_000, 1_500).ToString()
            .Should().Be("01:01:01.001500");
    }

    [Fact]
    public void ToString_Formats_Sub_Second_Microseconds()
        => Timestamp.FromMillis(0, 1).ToString().Should().Be("00:00:00.000001");
}