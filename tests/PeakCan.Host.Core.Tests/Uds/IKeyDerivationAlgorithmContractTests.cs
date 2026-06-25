using PeakCan.Host.Core.Uds;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds;

public class IKeyDerivationAlgorithmContractTests
{
    [Fact]
    public void Fake_Implements_Interface()
    {
        IKeyDerivationAlgorithm algo = new FakeKeyDerivationAlgorithm();

        Assert.NotNull(algo);
    }

    [Fact]
    public void Fake_ComputeKey_Receives_Seed_And_Level()
    {
        var fake = new FakeKeyDerivationAlgorithm();
        var seed = new byte[] { 0x12, 0x34, 0x56 };

        var key = fake.ComputeKey(seed, 0x05);

        Assert.Equal(seed, fake.LastSeed);
        Assert.Equal((byte)0x05, fake.LastSecurityLevel);
        Assert.Equal(3, key.Length);
        Assert.Equal((byte)(0x12 ^ 0x05), key[0]);
    }

    [Fact]
    public void Fake_CallCount_Increments()
    {
        var fake = new FakeKeyDerivationAlgorithm();

        fake.ComputeKey(new byte[] { 0x01 }, 0x01);
        fake.ComputeKey(new byte[] { 0x02 }, 0x01);

        Assert.Equal(2, fake.CallCount);
    }
}
