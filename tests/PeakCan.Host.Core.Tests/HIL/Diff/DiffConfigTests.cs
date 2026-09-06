using PeakCan.Host.Core.HIL.Diff;
using Xunit;

namespace PeakCan.Host.Core.Tests.HIL.Diff;

public class DiffConfigTests
{
    [Fact]
    public void Validate_NearestNeighbor_ZeroWindow_Throws()
    {
        var config = new DiffConfig(Alignment: AlignStrategy.NearestNeighbor, NeighborWindowMs: 0);
        Assert.Throws<ArgumentException>(() => config.Validate());
    }

    [Fact]
    public void Validate_NearestNeighbor_NegativeWindow_Throws()
    {
        var config = new DiffConfig(Alignment: AlignStrategy.NearestNeighbor, NeighborWindowMs: -1);
        Assert.Throws<ArgumentException>(() => config.Validate());
    }

    [Fact]
    public void Validate_NearestNeighbor_PositiveWindow_OK()
    {
        var config = new DiffConfig(Alignment: AlignStrategy.NearestNeighbor, NeighborWindowMs: 100);
        config.Validate(); // No exception
    }

    [Fact]
    public void Validate_Timestamp_ZeroWindow_OK()
    {
        var config = new DiffConfig(Alignment: AlignStrategy.Timestamp, NeighborWindowMs: 0);
        config.Validate(); // No exception - window ignored for Timestamp
    }

    [Fact]
    public void Validate_NegativeAbsoluteTolerance_Throws()
    {
        var config = new DiffConfig(Tolerance: new ToleranceSpec(AbsoluteTolerance: -1.0));
        Assert.Throws<ArgumentException>(() => config.Validate());
    }

    [Fact]
    public void Validate_NegativeRelativeTolerance_Throws()
    {
        var config = new DiffConfig(Tolerance: new ToleranceSpec(RelativeTolerance: -0.1));
        Assert.Throws<ArgumentException>(() => config.Validate());
    }

    [Fact]
    public void Validate_AfterWithExpression_StillCatches()
    {
        var config = new DiffConfig() with
        {
            Alignment = AlignStrategy.NearestNeighbor,
            NeighborWindowMs = 0,
        };
        Assert.Throws<ArgumentException>(() => config.Validate());
    }

    [Fact]
    public void DefaultConstructor_ProducesValidConfig()
    {
        var config = new DiffConfig();
        config.Validate(); // No exception

        Assert.Equal(DiffGranularity.Frame, config.Granularity);
        Assert.Equal(AlignStrategy.Timestamp, config.Alignment);
        Assert.Equal(100, config.NeighborWindowMs);
    }
}
