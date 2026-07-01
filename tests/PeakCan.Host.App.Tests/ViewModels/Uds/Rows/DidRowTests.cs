using FluentAssertions;
using PeakCan.Host.App.ViewModels.Uds.Rows;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Uds.Rows;

public sealed class DidRowTests
{
    [Fact]
    public void WritableDisplay_Returns_RW_When_Writable_True()
    {
        var row = new DidRow { Writable = true };
        row.WritableDisplay.Should().Be("R/W");
    }

    [Fact]
    public void WritableDisplay_Returns_RO_When_Writable_False()
    {
        var row = new DidRow { Writable = false };
        row.WritableDisplay.Should().Be("R/O");
    }

    [Fact]
    public void IsReading_Defaults_To_False()
    {
        new DidRow().IsReading.Should().BeFalse();
    }

    [Fact]
    public void IsReading_PropertyChanged_Fires_When_Set()
    {
        var row = new DidRow();
        var fired = false;
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DidRow.IsReading)) fired = true;
        };
        row.IsReading = true;
        fired.Should().BeTrue();
    }
}
