using FluentAssertions;
using PeakCan.Host.Core.Path;

namespace PeakCan.Host.Core.Tests.Path;

/// <summary>
/// v1.6.10 PATCH Item 2: verifies the <see cref="PathOptions"/> record
/// + <see cref="PathOptions.Default"/> back-compat semantics.
/// </summary>
public class PathOptionsTests
{
    [Fact]
    public void PathOptions_Default_Has_LocalAppDataPeakCanRoot()
    {
        // Arrange / Act
        var defaultRoots = PathOptions.Default.AllowedRoots;

        // Assert — exactly one root, matching v1.6.4 PATCH hardcoded value
        defaultRoots.Should().HaveCount(1);
        defaultRoots[0].Should().Be(PathNormalizer.LocalAppDataPeakCanRoot);
    }

    [Fact]
    public void PathOptions_RecordEquality_Works()
    {
        // Arrange
        var roots = new[] { @"C:\Allowed" };
        var a = new PathOptions(roots);
        var b = new PathOptions(roots);

        // Act / Assert — record equality is value-based
        a.Should().Be(b);
        a.Should().NotBe(new PathOptions([@"C:\Other"]));
    }
}