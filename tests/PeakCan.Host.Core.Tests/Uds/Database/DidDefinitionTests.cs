using PeakCan.Host.Core.Uds.Database;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds.Database;

public class DidDefinitionTests
{
    [Fact]
    public void Record_Equality_Is_Value_Based()
    {
        var a = new DidDefinition(0xF190, "VIN", "desc", 17, false);
        var b = new DidDefinition(0xF190, "VIN", "desc", 17, false);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Record_Inequality_When_Id_Differs()
    {
        var a = new DidDefinition(0xF190, "VIN", "desc", 17, false);
        var b = new DidDefinition(0xF191, "VIN", "desc", 17, false);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Properties_Are_Accessible()
    {
        var did = new DidDefinition(0xF184, "SW Version", "ECU Software Version", 9, false);

        Assert.Equal((ushort)0xF184, did.Id);
        Assert.Equal("SW Version", did.Name);
        Assert.Equal("ECU Software Version", did.Description);
        Assert.Equal(9, did.LengthBytes);
        Assert.False(did.Writable);
    }

    [Fact]
    public void ToString_Includes_Id_And_Name()
    {
        var did = new DidDefinition(0xF190, "VIN", "desc", 17, false);

        var s = did.ToString();

        Assert.Contains("F190", s);
        Assert.Contains("VIN", s);
    }
}
