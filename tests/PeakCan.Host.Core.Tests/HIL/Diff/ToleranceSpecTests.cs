using PeakCan.Host.Core.HIL.Diff;
using Xunit;

namespace PeakCan.Host.Core.Tests.HIL.Diff;

public class ToleranceSpecTests
{
    [Theory]
    [InlineData(10.0, 10.0, true)]     // exact match
    [InlineData(10.0, 10.5, true)]     // within absolute tolerance
    [InlineData(10.0, 11.0, true)]     // at absolute tolerance boundary
    [InlineData(10.0, 11.1, false)]    // outside absolute tolerance
    [InlineData(0.0, 0.0, true)]       // zero exact
    public void IsWithin_AbsoluteTolerance(double expected, double actual, bool expectedResult)
    {
        var spec = new ToleranceSpec(AbsoluteTolerance: 1.0, RelativeTolerance: 0.0);
        Assert.Equal(expectedResult, spec.IsWithin(expected, actual));
    }

    [Theory]
    [InlineData(100.0, 105.0, true)]   // within 5%
    [InlineData(100.0, 110.0, true)]   // at 10% boundary
    [InlineData(100.0, 111.0, false)]  // outside 10%
    [InlineData(0.0, 0.0, true)]       // zero with relative
    public void IsWithin_RelativeTolerance(double expected, double actual, bool expectedResult)
    {
        var spec = new ToleranceSpec(AbsoluteTolerance: 0.0, RelativeTolerance: 0.10);
        Assert.Equal(expectedResult, spec.IsWithin(expected, actual));
    }

    [Fact]
    public void IsWithin_BothTolerances_True_WhenEitherSatisfied()
    {
        // Absolute fails but relative passes
        var spec = new ToleranceSpec(AbsoluteTolerance: 0.5, RelativeTolerance: 0.10);
        Assert.True(spec.IsWithin(100.0, 108.0)); // 8 > 0.5 abs, but 8% < 10% rel
    }

    [Fact]
    public void IsWithin_Symmetric_AroundExpected()
    {
        var spec = new ToleranceSpec(AbsoluteTolerance: 1.0, RelativeTolerance: 0.0);
        Assert.True(spec.IsWithin(10.0, 9.0));
        Assert.True(spec.IsWithin(10.0, 11.0));
        Assert.False(spec.IsWithin(10.0, 8.9));
    }

    [Fact]
    public void Exact_ZeroTolerance()
    {
        var spec = ToleranceSpec.Exact;
        Assert.True(spec.IsWithin(5.0, 5.0));
        Assert.False(spec.IsWithin(5.0, 5.0001));
    }
}
