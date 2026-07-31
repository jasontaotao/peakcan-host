using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL.Generators;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Generators;

public class DidWriteGeneratorTests
{
    [Fact]
    public void DidWriteGenerator_WritesValue_ReturnsPositiveResponse()
    {
        var gen = new DidWriteGenerator();
        var context = new FakeContext();
        context.Set("DidValues", new Dictionary<ushort, byte[]> { [0xF190] = Array.Empty<byte>() });
        context.Set("WritableDids", new Dictionary<ushort, bool> { [0xF190] = true });

        var request = new byte[] { 0x2E, 0xF1, 0x90, 0x01, 0x02 };
        var response = gen.Generate(request, "default", context);

        Assert.Equal(new byte[] { 0x6E, 0xF1, 0x90 }, response);
        Assert.Equal(new byte[] { 0x01, 0x02 }, context.Get<Dictionary<ushort, byte[]>>("DidValues")![0xF190]);
    }

    [Fact]
    public void DidWriteGenerator_DidNotWritable_ReturnsNrc31()
    {
        var gen = new DidWriteGenerator();
        var context = new FakeContext();
        context.Set("DidValues", new Dictionary<ushort, byte[]> { [0xF190] = Array.Empty<byte>() });
        context.Set("WritableDids", new Dictionary<ushort, bool> { [0xF190] = false });

        var request = new byte[] { 0x2E, 0xF1, 0x90, 0x01, 0x02 };
        var response = gen.Generate(request, "default", context);

        Assert.Equal(new byte[] { 0x7F, 0x2E, 0x31 }, response);
    }
}
