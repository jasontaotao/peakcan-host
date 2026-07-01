using FluentAssertions;

namespace PeakCan.Host.Core.Tests.Uds.Odx;

public class OdxErrorCodeTests
{
    [Fact]
    public void OdxErrorCode_HasExpectedValues()
    {
        // Arrange + Act
        var values = Enum.GetValues<PeakCan.Host.Core.Uds.Odx.OdxErrorCode>();

        // Assert — these MUST exist (Task 2 spec).
        values.Should().Contain(PeakCan.Host.Core.Uds.Odx.OdxErrorCode.None);
        values.Should().Contain(PeakCan.Host.Core.Uds.Odx.OdxErrorCode.FileNotFound);
        values.Should().Contain(PeakCan.Host.Core.Uds.Odx.OdxErrorCode.ParseError);
        values.Should().Contain(PeakCan.Host.Core.Uds.Odx.OdxErrorCode.UnsupportedVersion);
        values.Should().Contain(PeakCan.Host.Core.Uds.Odx.OdxErrorCode.Refused);
        values.Should().Contain(PeakCan.Host.Core.Uds.Odx.OdxErrorCode.DuplicateId);
    }
}
