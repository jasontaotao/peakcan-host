using PeakCan.Security.Cmac;
using PeakCan.Security.SecOc;
using Xunit;

namespace PeakCan.Security.Tests;

/// <summary>devlog M2 遗留 #5：SecOcAuthenticator 内部密钥克隆零化 API（卫生级）。</summary>
public class SecOcAuthenticatorWipeTests
{
    private static readonly byte[] Key = Convert.FromHexString("00112233445566778899aabbccddeeff");

    private static SecOcProfile Profile() => new()
    {
        DataId = 0x0001,
        FvLenBits = 8,
        MacLenBits = 24,
    };

    [Fact]
    public void Wipe_AfterSign_SubsequentOperations_Throw()
    {
        var auth = new SecOcAuthenticator(Profile(), Key, new BouncyCastleCmacProvider());
        var frame = new byte[auth.FrameLength(4)];
        var written = auth.Sign(new byte[] { 1, 2, 3, 4 }, frame);
        Assert.Equal(auth.FrameLength(4), written);

        auth.Wipe();

        Assert.Throws<ObjectDisposedException>(
            () => auth.Sign(new byte[] { 1, 2, 3, 4 }, frame));
        Assert.Throws<ObjectDisposedException>(() => auth.Verify(frame));
    }

    [Fact]
    public void Wipe_IsIdempotent()
    {
        var auth = new SecOcAuthenticator(Profile(), Key, new BouncyCastleCmacProvider());
        auth.Wipe();
        auth.Wipe();
        Assert.Throws<ObjectDisposedException>(() => auth.Verify(new byte[4]));
    }

    [Fact]
    public void Wipe_DoesNotAffectCallerKeyArray()
    {
        var key = (byte[])Key.Clone();
        var auth = new SecOcAuthenticator(Profile(), key, new BouncyCastleCmacProvider());
        auth.Wipe();
        // M-2 防御性拷贝语义：Wipe 只清内部克隆，不动调用方数组
        Assert.Equal(Key, key);
    }
}
