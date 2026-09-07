using FluentAssertions;
using PeakCan.Host.Mobile.Core.Models;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.Models;

public class FrameRowTests
{
    private static readonly byte[] Data = [1, 2, 3];

    [Fact]
    public void DisplayTexts_AreCached()
    {
        var row = new FrameRow(1.25, 0x103, false, 3, Data);

        row.TimeText.Should().BeSameAs(row.TimeText);
        row.IdText.Should().BeSameAs(row.IdText);
        row.DataText.Should().BeSameAs(row.DataText);
        row.TimeText.Should().Be("1.250000");
        row.IdText.Should().Be("103");
        row.DataText.Should().Be("01 02 03");
    }

    [Fact]
    public void ExtendedId_UsesEightHexDigits()
    {
        var row = new FrameRow(0, 0x18ff0103, true, 0, Data);
        row.IdText.Should().Be("18FF0103");
    }
}
