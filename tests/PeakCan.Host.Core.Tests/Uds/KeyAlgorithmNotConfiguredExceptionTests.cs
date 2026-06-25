using PeakCan.Host.Core.Uds;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds;

public class KeyAlgorithmNotConfiguredExceptionTests
{
    [Fact]
    public void Ctor_Stores_SecurityLevel()
    {
        var ex = new KeyAlgorithmNotConfiguredException(0x01);

        Assert.Equal((byte)0x01, ex.SecurityLevel);
    }

    [Fact]
    public void Message_Contains_Level_Hex_And_Registration_Hint()
    {
        var ex = new KeyAlgorithmNotConfiguredException(0x03);

        Assert.Contains("0x03", ex.Message);
        Assert.Contains("IKeyDerivationAlgorithm", ex.Message);
        Assert.Contains("Register", ex.Message);
    }

    [Fact]
    public void Is_Subclass_Of_Exception()
    {
        var ex = new KeyAlgorithmNotConfiguredException(0x01);

        Assert.IsAssignableFrom<Exception>(ex);
    }
}
