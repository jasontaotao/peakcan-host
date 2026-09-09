using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;

namespace PeakCan.Security.Cmac;

/// <summary>
/// 基于 BouncyCastle CMac(AesEngine) 的实现（spec §7：密码原语只允许
/// 成熟实现，禁止自造）。spec §6.1 钉死 AES-128，密钥恒为 16 字节。
/// 注：API 面是 span 化的，内部因 BouncyCastle 仅接受 byte[] 有一次拷贝——
/// 非零分配（热路径优化见 spec §8 的 IMPROVE 期）。
/// </summary>
public sealed class BouncyCastleCmacProvider : IAesCmacProvider
{
    private const int MacSizeBytes = 16;

    public void Compute(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data, Span<byte> mac)
    {
        if (key.Length != 16)
            throw new ArgumentException("AES-128 密钥必须为 16 字节（spec §6.1）", nameof(key));
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