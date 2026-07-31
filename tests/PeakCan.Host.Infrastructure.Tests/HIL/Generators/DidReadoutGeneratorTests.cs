using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL.Generators;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Generators;

public class DidReadoutGeneratorTests
{
    [Fact]
    public void DidReadoutGenerator_DidFound_ReturnsPositiveResponse()
    {
        var gen = new DidReadoutGenerator();
        var context = new FakeContext();
        var didValues = new Dictionary<ushort, byte[]> { [0xF190] = new byte[] { 0x41, 0x42, 0x43 } };
        context.Set("DidValues", didValues);

        var request = new byte[] { 0x22, 0xF1, 0x90 };
        var response = gen.Generate(request, "default", context);

        Assert.Equal(new byte[] { 0x62, 0xF1, 0x90, 0x41, 0x42, 0x43 }, response);
    }

    [Fact]
    public void DidReadoutGenerator_DidNotFound_ReturnsNrc31ByteArray()
    {
        var gen = new DidReadoutGenerator();
        var context = new EcuContextStore();
        context.Set("DidValues", new Dictionary<ushort, byte[]>());

        var request = new byte[] { 0x22, 0xF1, 0x90 };
        var response = gen.Generate(request, "default", context);

        // NRC as byte[] (not exception) — IEcuResponseGenerator.Generate returns byte[]
        Assert.Equal(new byte[] { 0x7F, 0x22, 0x31 }, response);
    }
}
