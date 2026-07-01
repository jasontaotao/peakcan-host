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

    [Fact]
    public void NormalizeRestricted_PathUnderAllowedRoot_ReturnsCanonical()
    {
        // Arrange — v1.6.4 PATCH Item 1: happy path. Path under the allowlist
        // root canonicalizes and passes through.
        var allowedRoots = new[] { @"C:\AppData\PeakCan.Host" };
        const string input = @"C:\AppData\PeakCan.Host\uds-dids.json";

        // Act
        var result = PathNormalizer.NormalizeRestricted(input, allowedRoots);

        // Assert
        result.Should().Be(@"C:\AppData\PeakCan.Host\uds-dids.json");
    }

    [Fact]
    public void NormalizeRestricted_PathOutsideAllowedRoot_Throws_OutsideAllowedRoot()
    {
        // Arrange — attacker-controlled absolute path under system dir must
        // be rejected with the new OutsideAllowedRoot enum value.
        var allowedRoots = new[] { @"C:\AppData\PeakCan.Host" };
        const string input = @"C:\Windows\System32\drivers\etc\hosts";

        // Act
        Action act = () => PathNormalizer.NormalizeRestricted(input, allowedRoots);

        // Assert
        act.Should().Throw<PathNormalizationException>()
            .Which.Reason.Should().Be(PathNormalizationReason.OutsideAllowedRoot);
    }

    [Fact]
    public void NormalizeRestricted_MultipleAllowedRoots_FirstMatchWins()
    {
        // Arrange — multi-root case: 1st root does not match, 2nd does.
        var allowedRoots = new[] { @"C:\AppData\PeakCan.Host", @"D:\OtherRoot" };
        const string input = @"D:\OtherRoot\file.json";

        // Act
        var result = PathNormalizer.NormalizeRestricted(input, allowedRoots);

        // Assert
        result.Should().Be(@"D:\OtherRoot\file.json");
    }

    [Fact]
    public void NormalizeRestricted_EmptyAllowedRoots_Throws_OutsideAllowedRoot()
    {
        // Arrange — empty allowlist = no root matches = reject everything.
        // This is the safe-by-default semantic: callers must opt in by
        // passing a non-empty allowlist.
        var allowedRoots = Array.Empty<string>();
        const string input = @"C:\AnyPath\file.json";

        // Act
        Action act = () => PathNormalizer.NormalizeRestricted(input, allowedRoots);

        // Assert
        act.Should().Throw<PathNormalizationException>()
            .Which.Reason.Should().Be(PathNormalizationReason.OutsideAllowedRoot);
    }

    [Fact]
    public void NormalizeRestricted_CaseInsensitivePrefixMatch()
    {
        // Arrange — Windows path semantics: case-insensitive prefix match
        // (case-insensitive is required for NTFS volume; case-sensitive FS
        // would reject but Windows is case-preserving not case-enforcing).
        var allowedRoots = new[] { @"C:\AppData\PeakCan.Host" };
        const string input = @"c:\appdata\peakcan.host\uds-dids.json";

        // Act
        var result = PathNormalizer.NormalizeRestricted(input, allowedRoots);

        // Assert — canonical form is returned (case-preserved from input on Windows).
        // Use Match() with OrdinalIgnoreCase since FluentAssertions StartWith has
        // no StringComparison overload.
        result.Should().Match(s => s.StartsWith(@"C:\AppData\PeakCan.Host", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NormalizeRestricted_UncPathUnderAllowedRoot_Passes()
    {
        // Arrange — UNC paths (\\server\share\...) must be supported by the
        // allowlist check, mirroring the existing Normalize() UNC support.
        var allowedRoots = new[] { @"\\server\share\PeakCan.Host" };
        const string input = @"\\server\share\PeakCan.Host\uds-dids.json";

        // Act
        var result = PathNormalizer.NormalizeRestricted(input, allowedRoots);

        // Assert
        result.Should().Be(@"\\server\share\PeakCan.Host\uds-dids.json");
    }

    [Fact]
    public void NormalizeRestricted_NullPath_StillThrows_NullPath()
    {
        // Arrange — null propagation through defense-in-depth. The allowlist
        // overload must not weaken the null/empty/relative/traversal checks;
        // it adds an additional prefix check on top.
        var allowedRoots = new[] { @"C:\AppData\PeakCan.Host" };
        const string? input = null;

        // Act
        Action act = () => PathNormalizer.NormalizeRestricted(input, allowedRoots);

        // Assert
        act.Should().Throw<PathNormalizationException>()
            .Which.Reason.Should().Be(PathNormalizationReason.NullPath);
    }
}
