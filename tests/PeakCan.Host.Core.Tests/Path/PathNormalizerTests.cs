using System;
using FluentAssertions;
using PeakCan.Host.Core.Path;
using Xunit;

namespace PeakCan.Host.Core.Tests.Path;

public class PathNormalizerTests
{
    [Fact]
    public void Normalize_AbsolutePath_ReturnsCanonical()
    {
        // Arrange
        const string input = @"C:\Users\Test\file.dbc";

        // Act
        var result = PathNormalizer.Normalize(input);

        // Assert
        result.Should().Be(@"C:\Users\Test\file.dbc");
    }

    [Fact]
    public void Normalize_RelativePath_Throws()
    {
        // Arrange
        const string input = "file.dbc";

        // Act
        Action act = () => PathNormalizer.Normalize(input);

        // Assert
        act.Should().Throw<PathNormalizationException>()
            .Which.Reason.Should().Be(PathNormalizationReason.RelativePath);
    }

    [Fact]
    public void Normalize_PathWithTraversalSegment_Throws()
    {
        // Arrange
        const string input = @"C:\foo\..\..\etc\passwd";

        // Act
        Action act = () => PathNormalizer.Normalize(input);

        // Assert
        act.Should().Throw<PathNormalizationException>()
            .Which.Reason.Should().Be(PathNormalizationReason.TraversalSegment);
    }

    [Fact]
    public void Normalize_PathWithNullByte_Throws()
    {
        // Arrange
        // Non-verbatim string so "\0" is a real NUL byte escape, not literal
        // backslash + zero (which is what @"..." would produce).
        const string input = "C:\\foo\\bad\0path.dbc";

        // Act
        Action act = () => PathNormalizer.Normalize(input);

        // Assert
        act.Should().Throw<PathNormalizationException>()
            .Which.Reason.Should().Be(PathNormalizationReason.NullByte);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Normalize_NullOrEmpty_Throws(string? input)
    {
        // Act
        Action act = () => PathNormalizer.Normalize(input!);

        // Assert
        act.Should().Throw<PathNormalizationException>()
            .Which.Reason.Should().BeOneOf(
                PathNormalizationReason.NullPath,
                PathNormalizationReason.EmptyPath);
    }

    [Fact]
    public void Normalize_AllowsForwardSlashes()
    {
        // Arrange
        const string input = @"C:/Users/Test/file.dbc";

        // Act
        var result = PathNormalizer.Normalize(input);

        // Assert
        result.Should().NotBeNullOrEmpty();
        // Windows accepts forward slashes; result is canonicalized.
        result.Should().Contain("file.dbc");
    }
}
