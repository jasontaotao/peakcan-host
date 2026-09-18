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

    // ── 项 2（2026-09-17）：逐通道路由（方案 B「跟着步骤走」）──

    private sealed class ChannelKeyedStats : ISecOcStats
    {
        private readonly string _name;
        public ChannelKeyedStats(string name) => _name = name;
        public bool TryGet(uint canId, out SecOcVerdictBucket bucket)
        {
            // bus-b 上 0x123 被拒；bus-a / 默认上 0x123 正常通过。
            if (_name == "bus-b" && canId == 0x123)
            {
                bucket = new SecOcVerdictBucket(0, 1, "Replay");
                return true;
            }
            if (canId == 0x123)
            {
                bucket = new SecOcVerdictBucket(2, 0, null);
                return true;
            }
            bucket = new SecOcVerdictBucket(0, 0, null);
            return false;
        }
    }

    private static ExpressionValue? InvokeWithChannel(
        string channel, string name, params ExpressionValue[] args)
    {
        // 模拟引擎逐 step 设置「当前作用通道」：可变闭包 + Func 读。
        string? current = channel;
        // 多通道 resolver：按当前通道名选 stats；null/空 → 默认通道。
        Func<string?, ISecOcStats?> resolver = ch => ch switch
        {
            "bus-b" => new ChannelKeyedStats("bus-b"),
            "bus-a" => new ChannelKeyedStats("bus-a"),
            _ => new ChannelKeyedStats("default"),
        };
        var registry = new SecOcFunctionRegistry(resolver, () => current);
        return registry.TryInvoke(name, args, out var r) ? r : null;
    }

    [Theory]
    [InlineData("bus-b", true)]
    [InlineData("bus-a", false)]
    [InlineData("", false)]
    public void secocRejected_RoutesByCurrentChannel(string channel, bool expected)
        => InvokeWithChannel(channel, "secocRejected", ExpressionValue.FromString("0x123"))!
            .Value.AsBool.Should().Be(expected);

    [Fact]
    public void secocAccepted_RoutesByCurrentChannel()
    {
        // bus-b 上 0x123 被拒 → accepted=false；默认通道 accepted=true
        InvokeWithChannel("bus-b", "secocAccepted", ExpressionValue.FromString("0x123"))!
            .Value.AsBool.Should().BeFalse();
        InvokeWithChannel("", "secocAccepted", ExpressionValue.FromString("0x123"))!
            .Value.AsBool.Should().BeTrue();
    }

    [Fact]
    public void secocLastReason_RoutesByCurrentChannel()
    {
        InvokeWithChannel("bus-b", "secocLastReason", ExpressionValue.FromString("0x123"))!
            .Value.AsString.Should().Be("Replay");
        InvokeWithChannel("", "secocLastReason", ExpressionValue.FromString("0x123"))!
            .Value.AsString.Should().Be("");
    }

    [Fact]
    public void DefaultChannel_WhenCurrentCellUnset()
    {
        var registry = new SecOcFunctionRegistry(new FakeStats());
        registry.TryInvoke("secocAccepted", new[] { ExpressionValue.FromString("0x123") }, out var r)
            .Should().BeTrue();
        r.AsBool.Should().BeTrue("无 channel cell 时回落构造注入的 stats（单通道向后兼容）");
    }
}
