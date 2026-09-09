using PeakCan.Security.SecOc;

namespace PeakCan.Security.Tests;

/// <summary>spec §6.2：per-PDU 内部单调计数器语义。</summary>
public sealed class FreshnessValueManagerTests
{
    [Fact]
    public void starts_at_zero_and_increments_per_frame()
    {
        // Arrange
        var mgr = new FreshnessValueManager();

        // Act / Assert（首帧取 0，之后逐帧 +1）
        mgr.TakeNext().Should().Be(0);
        mgr.TakeNext().Should().Be(1);
        mgr.TakeNext().Should().Be(2);
    }

    [Fact]
    public void truncates_least_significant_bits()
    {
        // Arrange（fvLen=16：线上只带低 16 位）
        var mgr = new FreshnessValueManager(65535);
        var profile = new SecOcProfile { DataId = 1, FvLenBits = 16, MacLenBits = 24 };

        // Act
        var truncated = FreshnessValueManager.TruncatedFor(profile.FvLenBits, mgr.TakeNext());

        // Assert：65535 = 0xFFFF，低 16 位全 1
        truncated.Should().Be((ushort)0xFFFF);
    }

    [Fact]
    public void truncation_wraps_across_block_boundary()
    {
        // Arrange
        var mgr = new FreshnessValueManager(65536);

        // Act
        var truncated = FreshnessValueManager.TruncatedFor(16, mgr.TakeNext());

        // Assert：65536 = 0x10000，低 16 位为 0
        truncated.Should().Be((ushort)0);
    }

    [Fact]
    public void truncation_takes_low_bits_not_high_bits()
    {
        // Arrange（防截断方向反：高 16 位是 0x0001，低 16 位是 0x0000）
        var mgr = new FreshnessValueManager(65536);

        // Act
        var truncated = FreshnessValueManager.TruncatedFor(16, mgr.TakeNext());

        // Assert（若误取高 16 位会得到 1）
        truncated.Should().Be(0);
    }
}