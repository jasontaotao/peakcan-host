using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL.Generators;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

public class GeneratorsTests
{
    private static EcuContextStore CreateContext() => new();

    [Fact]
    public void SecurityAccessSeed_GeneratesAndCachesSeed()
    {
        var gen = new SecurityAccessSeedGenerator();
        var ctx = CreateContext();

        var resp1 = gen.Generate(new byte[] { 0x27, 0x01 }, "locked", ctx);
        var resp2 = gen.Generate(new byte[] { 0x27, 0x01 }, "locked", ctx);

        // Both responses should be identical (seed cached)
        Assert.Equal(resp1, resp2);
        // Response format: [0x67, 0x01, seed[0], seed[1], seed[2], seed[3]]
        Assert.Equal(6, resp1.Length);
        Assert.Equal(0x67, resp1[0]);
        Assert.Equal(0x01, resp1[1]);
        // Seed bytes should be present
        var seed = ctx.Get<byte[]>("SecuritySeed");
        Assert.NotNull(seed);
        Assert.Equal(4, seed!.Length);
    }

    [Fact]
    public void SecurityAccessVerifyKey_ReturnsPositive_WhenKeyCorrect()
    {
        var gen = new SecurityAccessVerifyKeyGenerator();
        var ctx = CreateContext();
        var seed = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        ctx.Set("SecuritySeed", seed);

        // key[i] = seed[i] ^ 0xAA
        var key = seed.Select(b => (byte)(b ^ 0xAA)).ToArray();
        // Request: [SID=0x27, subFunc=0x02, key[0..3]]
        var request = new byte[] { 0x27, 0x02 }.Concat(key).ToArray();

        var response = gen.Generate(request, "seedSent", ctx);

        Assert.Equal(new byte[] { 0x67, 0x02 }, response);
        Assert.True(ctx.Get<bool>("SecurityUnlocked"));
    }

    [Fact]
    public void SecurityAccessVerifyKey_ReturnsNrc35_WhenKeyIncorrect()
    {
        var gen = new SecurityAccessVerifyKeyGenerator();
        var ctx = CreateContext();
        ctx.Set("SecuritySeed", new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });

        // Wrong key (all zeros)
        var request = new byte[] { 0x27, 0x02, 0x00, 0x00, 0x00, 0x00 };

        var response = gen.Generate(request, "seedSent", ctx);

        Assert.Equal(new byte[] { 0x7F, 0x27, 0x35 }, response);
    }

    [Fact]
    public void ClearDtc_SetsEmptyDtcList_AndReturnsPositive()
    {
        var gen = new ClearDtcGenerator();
        var ctx = CreateContext();

        var response = gen.Generate(new byte[] { 0x14, 0xFF, 0xFF, 0xFF }, "default", ctx);

        Assert.Equal(new byte[] { 0x54 }, response);
        var dtcList = ctx.Get<List<(uint Code, byte Status)>>("DtcList");
        Assert.NotNull(dtcList);
        Assert.Empty(dtcList!);
    }
}
