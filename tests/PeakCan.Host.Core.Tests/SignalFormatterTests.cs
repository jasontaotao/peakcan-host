using FluentAssertions;
using PeakCan.Host.Core.Dbc;
using Xunit;

namespace PeakCan.Host.Core.Tests;

/// <summary>
/// v3.50.6 PATCH: verifies SignalFormatter.ResolveDecimalDigits + FormatValue.
/// Sister of SignalDecoderTests.TryDecodeEnumTextTests (v3.50.5).
/// </summary>
public class SignalFormatterTests
{
    [Fact]
    public void ResolveDecimalDigits_HandlesZero()
    {
        SignalFormatter.ResolveDecimalDigits(0.0).Should().Be(0);
    }

    [Fact]
    public void ResolveDecimalDigits_HandlesNaN()
    {
        SignalFormatter.ResolveDecimalDigits(double.NaN).Should().Be(0);
        SignalFormatter.ResolveDecimalDigits(double.PositiveInfinity).Should().Be(0);
        SignalFormatter.ResolveDecimalDigits(double.NegativeInfinity).Should().Be(0);
    }

    [Fact]
    public void ResolveDecimalDigits_HandlesPowerOfTen()
    {
        SignalFormatter.ResolveDecimalDigits(0.001).Should().Be(3);
        SignalFormatter.ResolveDecimalDigits(0.01).Should().Be(2);
        SignalFormatter.ResolveDecimalDigits(0.1).Should().Be(1);
        SignalFormatter.ResolveDecimalDigits(1.0).Should().Be(0);
        SignalFormatter.ResolveDecimalDigits(10.0).Should().Be(0);
        SignalFormatter.ResolveDecimalDigits(100.0).Should().Be(0);
    }

    [Fact]
    public void ResolveDecimalDigits_HandlesFraction()
    {
        SignalFormatter.ResolveDecimalDigits(0.5).Should().Be(1);
        SignalFormatter.ResolveDecimalDigits(0.25).Should().Be(2);
        SignalFormatter.ResolveDecimalDigits(0.125).Should().Be(3);
    }

    [Fact]
    public void FormatValue_FormatsWithResolvedDigits()
    {
        SignalFormatter.FormatValue(0.001, 3.353).Should().Be("3.353");
        SignalFormatter.FormatValue(0.1, 23.5).Should().Be("23.5");
        SignalFormatter.FormatValue(1.0, 1200).Should().Be("1200");
        SignalFormatter.FormatValue(0.001, 0.0).Should().Be("0.000");
    }

    [Fact]
    public void FormatValue_FallsBackToF2ForUnknownFactor()
    {
        SignalFormatter.FormatValue(double.NaN, 1.23).Should().Be("1");
    }
}
