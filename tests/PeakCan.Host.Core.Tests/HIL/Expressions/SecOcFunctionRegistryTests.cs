using FluentAssertions;
using PeakCan.HIL.Core.HIL.Expressions;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.HIL.Expressions;
using Xunit;

namespace PeakCan.Host.Core.Tests.HIL.Expressions;

public class SecOcFunctionRegistryTests
{
    private sealed class FakeStats : ISecOcStats
    {
        public bool TryGet(uint canId, out SecOcVerdictBucket bucket)
        {
            if (canId == 0x123)
            {
                bucket = new SecOcVerdictBucket(2, 1, "BadMac");
                return true;
            }
            bucket = new SecOcVerdictBucket(0, 0, null);
            return false;
        }
    }

    private static ExpressionValue? Invoke(string name, params ExpressionValue[] args)
        => new SecOcFunctionRegistry(new FakeStats()).TryInvoke(name, args, out var r) ? r : null;

    [Theory]
    [InlineData("0x123", true)]
    [InlineData("0x456", false)]
    [InlineData("291", true)] // decimal 0x123
    public void secocAccepted_ReadsBucket(string id, bool expected)
        => Invoke("secocAccepted", ExpressionValue.FromString(id))!.Value.AsBool.Should().Be(expected);

    [Fact]
    public void secocRejected_UnknownCanId_IsFalse()
        => Invoke("secocRejected", ExpressionValue.FromString("0x456"))!.Value.AsBool.Should().BeFalse();

    [Fact]
    public void secocLastReason_ReturnsBucketReason()
        => Invoke("secocLastReason", ExpressionValue.FromLong(0x123))!.Value.AsString.Should().Be("BadMac");

    [Fact]
    public void secocLastReason_NoRejection_ReturnsEmptyString()
        => Invoke("secocLastReason", ExpressionValue.FromLong(0x456))!.Value.AsString.Should().Be("");

    [Fact]
    public void WrongArity_IsNotHandled()
        => Invoke("secocAccepted", ExpressionValue.FromLong(1), ExpressionValue.FromLong(2))
            .Should().BeNull();
}
