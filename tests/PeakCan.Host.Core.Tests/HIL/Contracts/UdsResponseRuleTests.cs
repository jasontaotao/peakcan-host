using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.Core.Tests.HIL.Contracts;

public class UdsResponseRuleTests
{
    [Fact]
    public void TryMatch_returns_true_when_SID_matches()
    {
        var rule = new UdsResponseRule { ServiceId = 0x22, ResponseData = new byte[] { 0x62 } };
        var request = new byte[] { 0x22, 0xF1, 0x90 };

        var result = rule.TryMatch(request, out var responseData);

        Assert.True(result);
        Assert.Equal(new byte[] { 0x62 }, responseData);
    }

    [Fact]
    public void TryMatch_checks_subFunction()
    {
        var rule = new UdsResponseRule { ServiceId = 0x19, SubFunction = 0x02, ResponseData = new byte[] { 0x59 } };

        // Matching sub-function
        Assert.True(rule.TryMatch(new byte[] { 0x19, 0x02 }, out _));

        // Non-matching sub-function
        Assert.False(rule.TryMatch(new byte[] { 0x19, 0x0A }, out _));

        // Request too short (no sub-function byte)
        Assert.False(rule.TryMatch(new byte[] { 0x19 }, out _));
    }

    [Fact]
    public void TryMatch_checks_DataMask()
    {
        var rule = new UdsResponseRule
        {
            ServiceId = 0x22,
            DataMask = new byte[] { 0xFF, 0xFF },
            DataPattern = new byte[] { 0xF1, 0x90 },
            ResponseData = new byte[] { 0x62, 0xF1, 0x90 }
        };

        // Matching data pattern
        Assert.True(rule.TryMatch(new byte[] { 0x22, 0x00, 0xF1, 0x90 }, out _));

        // Non-matching data pattern
        Assert.False(rule.TryMatch(new byte[] { 0x22, 0x00, 0xF1, 0x91 }, out _));

        // Request too short for mask
        Assert.False(rule.TryMatch(new byte[] { 0x22, 0x00, 0xF1 }, out _));
    }

    [Fact]
    public void TryMatch_returns_false_when_SID_mismatch()
    {
        var rule = new UdsResponseRule { ServiceId = 0x22, ResponseData = new byte[] { 0x62 } };
        var request = new byte[] { 0x10 };

        var result = rule.TryMatch(request, out var responseData);

        Assert.False(result);
        Assert.Empty(responseData);
    }
}
