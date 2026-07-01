using PeakCan.Host.Core.Uds;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds;

public class PlaceholderKeyAlgorithmTests
{
    [Fact]
    public void ComputeKey_Throws_KeyAlgorithmNotConfiguredException()
    {
        var sut = new PlaceholderKeyAlgorithm();
        var seed = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        var ex = Assert.Throws<KeyAlgorithmNotConfiguredException>(() =>
        {
            sut.ComputeKey(seed, 0x01);
        });

        Assert.Equal((byte)0x01, ex.SecurityLevel);
    }

    [Fact]
    public void ComputeKey_With_NullSeed_Throws_ArgumentNullException()
    {
        var sut = new PlaceholderKeyAlgorithm();

        Assert.Throws<ArgumentNullException>(() =>
        {
            sut.ComputeKey(null!, 0x01);
        });
    }

    [Fact]
    public void SecurityLevel_In_Exception_Message_For_Level_0x05()
    {
        var sut = new PlaceholderKeyAlgorithm();

        var ex = Assert.Throws<KeyAlgorithmNotConfiguredException>(() =>
        {
            sut.ComputeKey(new byte[] { 0x00 }, 0x05);
        });

        Assert.Contains("0x05", ex.Message);
    }

    [Fact]
    public void Implements_IKeyDerivationAlgorithm()
    {
        var sut = new PlaceholderKeyAlgorithm();

        Assert.IsAssignableFrom<IKeyDerivationAlgorithm>(sut);
    }
}
