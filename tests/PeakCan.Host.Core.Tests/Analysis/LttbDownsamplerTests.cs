using FluentAssertions;
using PeakCan.HIL.Core.Analysis;
using Xunit;

namespace PeakCan.HIL.Core.Tests.Analysis;

/// <summary>
/// v12 Step 2: TDD tests for <see cref="LttbDownsampler"/>.
/// LTTB (Largest Triangle Three Buckets) preserves extremes and
/// transition edges by selecting the point that forms the largest
/// triangle with the previous selected point and the next bucket's average.
/// </summary>
public class LttbDownsamplerTests
{
    private static readonly (double X, double Y)[] LinearRamp =
        Enumerable.Range(0, 100).Select(i => ((double)i, (double)i)).ToArray();

    [Fact]
    public void Input_Smaller_Than_MaxPoints_Returns_Input_Unchanged()
    {
        var points = new[] { (0.0, 0.0), (1.0, 1.0), (2.0, 2.0) };
        var result = LttbDownsampler.Downsample(points, maxPoints: 10);
        result.Should().HaveCount(3);
        result.Should().BeEquivalentTo(points);
    }

    [Fact]
    public void Input_Equal_To_MaxPoints_Returns_Input_Unchanged()
    {
        var points = LinearRamp.Take(10).ToArray();
        var result = LttbDownsampler.Downsample(points, maxPoints: 10);
        result.Should().HaveCount(10);
    }

    [Fact]
    public void Output_Count_Equals_MaxPoints()
    {
        var result = LttbDownsampler.Downsample(LinearRamp, maxPoints: 20);
        result.Should().HaveCount(20);
    }

    [Fact]
    public void First_And_Last_Points_Preserved()
    {
        var result = LttbDownsampler.Downsample(LinearRamp, maxPoints: 20);
        result[0].Should().Be(LinearRamp[0]);
        result[^1].Should().Be(LinearRamp[^1]);
    }

    [Fact]
    public void X_Values_Strictly_Increasing()
    {
        var result = LttbDownsampler.Downsample(LinearRamp, maxPoints: 15);
        for (int i = 1; i < result.Count; i++)
            result[i].X.Should().BeGreaterThan(result[i - 1].X);
    }

    [Fact]
    public void Spike_Is_Preserved()
    {
        // Flat line with a single spike at index 50.
        var points = Enumerable.Range(0, 101)
            .Select(i => ((double)i, i == 50 ? 100.0 : 0.0))
            .ToArray();
        var result = LttbDownsampler.Downsample(points, maxPoints: 11);
        // The spike (y=100) must appear in the output.
        result.Should().Contain(p => p.Y == 100.0);
    }

    [Fact]
    public void Sharp_Drop_Is_Preserved()
    {
        // Flat at 401, drops to 355 at index 50, flat at 355.
        var points = Enumerable.Range(0, 101)
            .Select(i => ((double)i, i < 50 ? 401.0 : 355.0))
            .ToArray();
        var result = LttbDownsampler.Downsample(points, maxPoints: 11);
        // Both levels (401 and 355) must appear.
        result.Should().Contain(p => p.Y == 401.0);
        result.Should().Contain(p => p.Y == 355.0);
    }

    [Fact]
    public void Constant_Signal_Stays_Constant()
    {
        var points = Enumerable.Range(0, 50)
            .Select(i => ((double)i, 42.0))
            .ToArray();
        var result = LttbDownsampler.Downsample(points, maxPoints: 5);
        result.Should().HaveCount(5);
        result.Should().OnlyContain(p => p.Y == 42.0);
    }

    [Fact]
    public void MaxPoints_Less_Than_3_Throws()
    {
        var points = new[] { (0.0, 0.0), (1.0, 1.0) };
        var act = () => LttbDownsampler.Downsample(points, maxPoints: 2);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Hand-computed: N=5, M=3.
    /// Input: (0,0), (1,1), (2,0), (3,1), (4,0)
    /// Bucket size = (5-2)/(3-2) = 3.
    /// One middle bucket: indices 1,2,3.
    /// Next avg = last point (4,0). Prev = first point (0,0).
    /// Areas:
    ///   (1,1): |((0-4)*(1-0) - (0-1)*(0-0))|/2 = |-4|/2 = 2
    ///   (2,0): 0 (collinear)
    ///   (3,1): |((0-4)*(1-0) - (0-3)*(0-0))|/2 = |-4|/2 = 2
    /// First max-area point is (1,1).
    /// Expected: (0,0), (1,1), (4,0)
    /// </summary>
    [Fact]
    public void HandComputed_N5_M3()
    {
        var points = new[] { (0.0, 0.0), (1.0, 1.0), (2.0, 0.0), (3.0, 1.0), (4.0, 0.0) };
        var result = LttbDownsampler.Downsample(points, maxPoints: 3);
        result.Should().HaveCount(3);
        result[0].Should().Be((0.0, 0.0));
        result[1].Should().Be((1.0, 1.0));
        result[2].Should().Be((4.0, 0.0));
    }

    /// <summary>
    /// Hand-computed: N=6, M=3.
    /// Input: (0,0), (1,3), (2,1), (3,2), (4,0), (5,0)
    /// Bucket size = (6-2)/(3-2) = 4.
    /// One middle bucket: indices 1,2,3,4.
    /// Next avg = last point (5,0). Prev = first point (0,0).
    /// Areas:
    ///   (1,3): |((0-5)*(3-0) - (0-1)*(0-0))|/2 = |-15|/2 = 7.5
    ///   (2,1): |((0-5)*(1-0) - (0-2)*(0-0))|/2 = |-5|/2 = 2.5
    ///   (3,2): |((0-5)*(2-0) - (0-3)*(0-0))|/2 = |-10|/2 = 5
    ///   (4,0): 0 (collinear with avg)
    /// Max is (1,3) with area 7.5.
    /// Expected: (0,0), (1,3), (5,0)
    /// </summary>
    [Fact]
    public void HandComputed_N6_M3()
    {
        var points = new[] { (0.0, 0.0), (1.0, 3.0), (2.0, 1.0), (3.0, 2.0), (4.0, 0.0), (5.0, 0.0) };
        var result = LttbDownsampler.Downsample(points, maxPoints: 3);
        result.Should().HaveCount(3);
        result[0].Should().Be((0.0, 0.0));
        result[1].Should().Be((1.0, 3.0));
        result[2].Should().Be((5.0, 0.0));
    }
}
