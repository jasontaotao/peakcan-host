using FluentAssertions;
using PeakCan.Host.Mobile.Core.Models;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.Models;

public class FrameRowSlotTests
{
    private static readonly byte[] Data = [1, 2, 3];

    [Fact]
    public void UpdateFrom_PopulatesDisplayFields()
    {
        var slot = new FrameRowSlot();
        slot.IsEmpty.Should().BeTrue();

        slot.UpdateFrom(new FrameRow(1.25, 0x103, false, 3, Data));

        slot.IsEmpty.Should().BeFalse();
        slot.TimeText.Should().Be("1.250000");
        slot.IdText.Should().Be("103");
        slot.Dlc.Should().Be(3);
        slot.DataText.Should().Be("01 02 03");
    }

    [Fact]
    public void Clear_ResetsSlot()
    {
        var slot = new FrameRowSlot();
        slot.UpdateFrom(new FrameRow(1, 2, false, 3, Data));

        slot.Clear();

        slot.IsEmpty.Should().BeTrue();
        slot.IdText.Should().BeEmpty();
    }
}
