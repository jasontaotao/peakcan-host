using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.Tests.HIL.Contracts;

public class FaultRuleTests
{
    [Fact]
    public void Drop_with_probability_1_always_drops()
    {
        var rule = new FaultRule { Type = FaultType.Drop, Probability = 1.0 };
        var frame = new CanFrame(new CanId(0x123, FrameFormat.Standard), new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 }), FrameFlags.None, ChannelId.None, new Timestamp(0));

        var result = rule.Apply(frame);

        Assert.Empty(result);
    }

    [Fact]
    public void Drop_with_probability_0_never_drops()
    {
        var rule = new FaultRule { Type = FaultType.Drop, Probability = 0.0 };
        var frame = new CanFrame(new CanId(0x123, FrameFormat.Standard), new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 }), FrameFlags.None, ChannelId.None, new Timestamp(0));

        var result = rule.Apply(frame);

        Assert.Single(result);
        Assert.Equal(frame.Id, result[0].Id);
    }

    [Fact]
    public void Corrupt_flips_specified_bytes()
    {
        var rule = new FaultRule
        {
            Type = FaultType.Corrupt,
            CorruptByteIndices = new[] { 0, 2 },
            CorruptXorMask = 0xFF
        };
        var frame = new CanFrame(new CanId(0x123, FrameFormat.Standard), new ReadOnlyMemory<byte>(new byte[] { 0xAA, 0xBB, 0xCC }), FrameFlags.None, ChannelId.None, new Timestamp(0));

        var result = rule.Apply(frame);

        Assert.Single(result);
        var data = result[0].Data.ToArray();
        Assert.Equal(0x55, data[0]); // 0xAA ^ 0xFF = 0x55
        Assert.Equal(0xBB, data[1]); // unchanged
        Assert.Equal(0x33, data[2]); // 0xCC ^ 0xFF = 0x33
    }

    [Fact]
    public void Duplicate_returns_two_frames()
    {
        var rule = new FaultRule { Type = FaultType.Duplicate };
        var frame = new CanFrame(new CanId(0x123, FrameFormat.Standard), new ReadOnlyMemory<byte>(new byte[] { 1 }), FrameFlags.None, ChannelId.None, new Timestamp(0));

        var result = rule.Apply(frame);

        Assert.Equal(2, result.Count);
        Assert.Equal(result[0].Id, result[1].Id);
    }

    [Fact]
    public void Matches_filters_by_TargetCanId()
    {
        var rule = new FaultRule { Type = FaultType.Drop, TargetCanId = 0x123 };

        var matchingFrame = new CanFrame(new CanId(0x123, FrameFormat.Standard), new ReadOnlyMemory<byte>() , FrameFlags.None, ChannelId.None, new Timestamp(0));
        var nonMatchingFrame = new CanFrame(new CanId(0x456, FrameFormat.Standard), new ReadOnlyMemory<byte>(), FrameFlags.None, ChannelId.None, new Timestamp(0));

        Assert.True(rule.Matches(matchingFrame));
        Assert.False(rule.Matches(nonMatchingFrame));

        // null TargetCanId = match all
        var matchAll = new FaultRule { Type = FaultType.Drop, TargetCanId = null };
        Assert.True(matchAll.Matches(matchingFrame));
        Assert.True(matchAll.Matches(nonMatchingFrame));
    }
}
