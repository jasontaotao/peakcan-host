using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;

namespace PeakCan.Security.Cmac;

/// <summary>
/// 基于 BouncyCastle CMac(AesEngine) 的实现（spec §7：密码原语只允许
/// 成熟实现，禁止自造）。
/// </summary>
public sealed class BouncyCastleCmacProvider : IAesCmacProvider
{
    private const int MacSizeBytes = 16;

    public void Compute(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data, Span<byte> mac)
    {
        if (key.Length is not (16 or 24 or 32))
            throw new ArgumentException("AES 密钥长度必须为 16/24/32 字节", nameof(key));
        if (mac.Length < MacSizeBytes)
            throw new ArgumentException($"输出缓冲区必须 ≥ {MacSizeBytes}", nameof(mac));

        var cmac = new CMac(new AesEngine(), MacSizeBytes * 8);
        cmac.Init(new KeyParameter(key.ToArray()));
        cmac.BlockUpdate(data.ToArray(), 0, data.Length);
        // BC 2.x: DoFinal 写入输出数组并返回实际字节数（恒为 16）
        var result = new byte[MacSizeBytes];
        cmac.DoFinal(result, 0);
        result.CopyTo(mac);
    }
}