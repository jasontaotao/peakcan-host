using PeakCan.Host.Core.Uds.Database;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds.Database;

public class RoutineDefinitionTests
{
    [Fact]
    public void Record_Equality_Is_Value_Based()
    {
        var a = new RoutineDefinition(0xFF00, "Erase", "Erase memory", true, true);
        var b = new RoutineDefinition(0xFF00, "Erase", "Erase memory", true, true);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Properties_Are_Accessible()
    {
        var r = new RoutineDefinition(0xFF01, "Check", "Integrity check", true, false);

        Assert.Equal((ushort)0xFF01, r.Id);
        Assert.Equal("Check", r.Name);
        Assert.Equal("Integrity check", r.Description);
        Assert.True(r.Startable);
        Assert.False(r.Stoppable);
    }

    [Fact]
    public void Record_Inequality_When_Startable_Differs()
    {
        var a = new RoutineDefinition(0xFF00, "Erase", "d", true, true);
        var b = new RoutineDefinition(0xFF00, "Erase", "d", false, true);

        Assert.NotEqual(a, b);
    }
}
