namespace PeakCan.Security.Cmac;

/// <summary>
/// AES-CMAC (NIST SP 800-38B / RFC 4493) 原语提供者。
/// 抽象出具体算法库（Phase 1 实现为 BouncyCastle 封装），
/// 便于未来替换为 HSM 后端或硬件加速。
/// </summary>
public interface IAesCmacProvider
{
    /// <summary>
    /// 计算 128-bit AES-CMAC，写入 <paramref name="mac"/>（长度必须 ≥ 16）。
    /// </summary>
    /// <param name="key">AES-128 密钥，恒为 16 字节（spec §6.1）。</param>
    /// <param name="data">MAC 输入。</param>
    /// <param name="mac">输出缓冲区（span 写入）。</param>
    /// <exception cref="ArgumentException">key 长度非 16 或 mac 缓冲区不足。</exception>
    void Compute(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data, Span<byte> mac);
}