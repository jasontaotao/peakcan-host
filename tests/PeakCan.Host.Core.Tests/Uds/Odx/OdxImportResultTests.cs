using FluentAssertions;
using PeakCan.Host.Core.Uds.Odx;

namespace PeakCan.Host.Core.Tests.Uds.Odx;

public class OdxImportResultTests
{
    private static readonly string[] Warn1 = new[] { "warn-1" };

    [Fact]
    public void Ok_WithCounts_ProducesSuccessResult()
    {
        // Arrange + Act
        var result = OdxImportResult.Ok(
            dids: 5,
            routines: 3,
            dtcs: 10,
            warnings: Warn1);

        // Assert
        result.DidCount.Should().Be(5);
        result.RoutineCount.Should().Be(3);
        result.DtcCount.Should().Be(10);
        result.Warnings.Should().BeEquivalentTo(Warn1);
        result.HasError.Should().BeFalse();
        result.ErrorCode.Should().Be(OdxErrorCode.None);
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Failed_WithErrorCode_ProducesFailureResult()
    {
        // Arrange + Act
        var result = OdxImportResult.Failed(
            OdxErrorCode.FileNotFound,
            "C:/missing.odx");

        // Assert
        result.HasError.Should().BeTrue();
        result.ErrorCode.Should().Be(OdxErrorCode.FileNotFound);
        result.ErrorMessage.Should().Be("C:/missing.odx");
        result.DidCount.Should().Be(0);
        result.RoutineCount.Should().Be(0);
        result.DtcCount.Should().Be(0);
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Ok_WithNullWarnings_DefaultsToEmpty()
    {
        // Arrange + Act
        var result = OdxImportResult.Ok(0, 0, 0, null!);

        // Assert
        result.Warnings.Should().BeEmpty();
    }
}
