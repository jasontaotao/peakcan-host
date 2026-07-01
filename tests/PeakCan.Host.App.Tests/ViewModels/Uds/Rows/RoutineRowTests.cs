using FluentAssertions;
using PeakCan.Host.App.ViewModels.Uds.Rows;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Uds.Rows;

public sealed class RoutineRowTests
{
    [Fact]
    public void Status_Defaults_To_Idle()
    {
        new RoutineRow().Status.Should().Be("Idle");
    }

    [Fact]
    public void Status_PropertyChanged_Fires_When_Set()
    {
        var row = new RoutineRow();
        var fired = false;
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RoutineRow.Status)) fired = true;
        };
        row.Status = "Running";
        fired.Should().BeTrue();
        row.Status.Should().Be("Running");
    }
}
