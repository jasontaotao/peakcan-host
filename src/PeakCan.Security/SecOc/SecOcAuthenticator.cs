using System.Security.Cryptography;
using PeakCan.Security.Cmac;

namespace PeakCan.Security.SecOc;

/// <summary>
/// 单 PDU 的加签 / 验签器（spec §6.1）。
/// 实例持有 profile + 密钥 + freshness 计数器（RX 侧还维护 lastAcceptedFv），
/// 因此**每 PDU 一个实例**，调用须串行。
/// 与通道无关：只操作净荷字节，CanFrame 映射在 host 装饰器层（spec D2）。
/// </summary>
public sealed class SecOcAuthenticator
{
    private readonly SecOcProfile _profile;
    private readonly byte[] _key;
    private readonly IAesCmacProvider _cmac;
    private readonly FreshnessValueManager _txFv;
    private long _lastAcceptedFv = -1; // −1 = 尚未接收任何帧

    public SecOcAuthenticator(
        SecOcProfile profile, byte[] key, IAesCmacProvider cmac, uint initialFv = 0)
    {
        ValidateProfile(profile);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(cmac);
        _profile = profile;
        // M-2：防御性拷贝——不持有调用方数组引用，防外部变更静默换钥
        _key = (byte[])key.Clone();
        _cmac = cmac;
        _txFv = new FreshnessValueManager(initialFv);
    }

    private static void ValidateProfile(SecOcProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        // v1 实现边界（H-1 加固）：TruncatedFor/ReadTruncatedFv 基于 ushort，
        // fvLen>16 会静默截断（Verify 假 BadMac）或 Sign 切片越界崩溃——
        // 必须 fail-fast。24 位 freshness（样例矩阵 VCU_ChrgCtrlCmd 行）为 v2 扩展。
        if (profile.FvLenBits <= 0 || profile.FvLenBits % 8 != 0 || profile.FvLenBits > 16)
            throw new ArgumentException("FvLenBits 必须是 8 的倍数且 ≤ 16（v1）", nameof(profile));
        if (profile.MacLenBits <= 0 || profile.MacLenBits % 8 != 0 || profile.MacLenBits > 128)
            throw new ArgumentException("MacLenBits 必须是 8 的倍数且 ≤ 128", nameof(profile));
        if (profile.FvLenBits > profile.FvFullBits)
            throw new ArgumentException("FvLenBits 不能超过 FvFullBits", nameof(profile));
        // ComputeMac 固定按 32bit 完整 FV 序列化；≠32 会破坏 MAC 输入字节序
        if (profile.FvFullBits != 32)
            throw new ArgumentException("FvFullBits v1 固定为 32", nameof(profile));
    }

    /// <summary>加签后帧长 = 数据区 + fvLen/8 + macLen/8。</summary>
    public int FrameLength(int authenticDataLength)
        => authenticDataLength + _profile.FvLenBits / 8 + _profile.MacLenBits / 8;

    /// <summary>
    /// TX 加签：把 authenticData 组装为 Secured 帧 data‖TruncFV‖TruncMAC。
    /// MAC 输入 = DataId(16bit BE) ‖ authenticData ‖ CompleteFreshness(32bit BE)。
    /// </summary>
    /// <returns>写入 <paramref name="frame"/> 的总字节数。</returns>
    public int Sign(ReadOnlySpan<byte> authenticData, Span<byte> frame)
    {
        var fvBytes = _profile.FvLenBits / 8;
        var macBytes = _profile.MacLenBits / 8;
        var total = authenticData.Length + fvBytes + macBytes;
        if (frame.Length < total)
            throw new ArgumentException("frame 缓冲区不足", nameof(frame));

        var fv = _txFv.TakeNext();
        var fullMac = ComputeMac(authenticData, fv);

        authenticData.CopyTo(frame);
        // 截断 FV 按 fvBytes 精确字节数序列化（大端）；TruncatedFor 返回 2 字节，
        // 对 fvLen<16 必须截取低 fvBytes 字节，否则会覆盖帧内后继字段
        var truncatedFv = FreshnessValueManager.TruncatedFor(_profile.FvLenBits, fv)
            .ToBytesBigEndian();
        truncatedFv.AsSpan(truncatedFv.Length - fvBytes)
            .CopyTo(frame[authenticData.Length..]);
        fullMac.AsSpan(0, macBytes).CopyTo(frame[(authenticData.Length + fvBytes)..]);
        return total;
    }

    /// <summary>MAC 输入 = DataId‖data‖FV(完整, BE)，输出完整 16B CMAC。</summary>
    private byte[] ComputeMac(ReadOnlySpan<byte> authenticData, uint freshness)
    {
        var input = new byte[2 + authenticData.Length + _profile.FvFullBits / 8];
        input[0] = (byte)(_profile.DataId >> 8);
        input[1] = (byte)_profile.DataId;
        authenticData.CopyTo(input.AsSpan(2));
        freshness.ToBytesBigEndian().CopyTo(input.AsSpan(2 + authenticData.Length));
        var mac = new byte[16];
        _cmac.Compute(_key, input, mac);
        return mac;
    }

    /// <summary>
    /// RX 验签（spec §6.2 候选集重构）。帧结构 = data‖TruncFV‖TruncMAC，
    /// 按 k∈{0,+1,−1} 顺序尝试完整 freshness 候选，以 MAC 通过者的位置分类。
    /// </summary>
    public VerifyResult Verify(ReadOnlySpan<byte> frame)
    {
        var fvBytes = _profile.FvLenBits / 8;
        var macBytes = _profile.MacLenBits / 8;
        if (frame.Length < fvBytes + macBytes)
            return VerifyResult.Reject(RejectReason.Malformed);

        var authenticData = frame[..(frame.Length - fvBytes - macBytes)];
        var receivedLow = ReadTruncatedFv(frame[^ (fvBytes + macBytes) .. ^macBytes], fvBytes);
        var receivedMac = frame[^macBytes..];

        long window = 1L << (_profile.FvLenBits - 1);

        // 首帧：以 recvLow 的高位为 0 的完整值直接验收（spec §6.2）
        if (_lastAcceptedFv < 0)
        {
            if (TryVerifyMac(authenticData, (uint)receivedLow, receivedMac))
            {
                _lastAcceptedFv = receivedLow;
                return VerifyResult.Accept(receivedLow);
            }
            return VerifyResult.Reject(RejectReason.BadMac);
        }

        foreach (var k in new[] { 0, +1, -1 })
        {
            var candidate = ((_lastAcceptedFv >> _profile.FvLenBits) + k)
                * (1L << _profile.FvLenBits) | receivedLow;
            if (candidate < 0 || candidate > uint.MaxValue)
                continue;

            if (!TryVerifyMac(authenticData, (uint)candidate, receivedMac))
                continue;

            // 第一个 MAC 通过者定案（spec §6.2 分类表）
            if (candidate == _lastAcceptedFv)
                return VerifyResult.Reject(RejectReason.Replay);
            if (candidate < _lastAcceptedFv)
                return _lastAcceptedFv - candidate <= window
                    ? VerifyResult.Reject(RejectReason.FvRollback)
                    : VerifyResult.Reject(RejectReason.FvAnomaly);
            if (candidate - _lastAcceptedFv <= window)
            {
                _lastAcceptedFv = candidate;
                return VerifyResult.Accept((uint)candidate);
            }
            return VerifyResult.Reject(RejectReason.FvAnomaly);
        }

        return VerifyResult.Reject(RejectReason.BadMac);
    }

    private bool TryVerifyMac(ReadOnlySpan<byte> authenticData, uint freshness,
        ReadOnlySpan<byte> receivedMac)
    {
        // M-3：恒定时间比较，避免字节级 MAC 时序 oracle（候选至多 3 次）
        var fullMac = ComputeMac(authenticData, freshness);
        return CryptographicOperations.FixedTimeEquals(
            fullMac.AsSpan()[..(_profile.MacLenBits / 8)], receivedMac);
    }

    private static ushort ReadTruncatedFv(ReadOnlySpan<byte> bytes, int byteCount)
    {
        ushort value = 0;
        for (var i = 0; i < byteCount; i++)
            value = (ushort)((value << 8) | bytes[i]);
        return value;
    }
}

/// <summary>验签结果（spec §6.2 分类表映射）。</summary>
public readonly record struct VerifyResult(bool Accepted, RejectReason? Reason, uint FvUsed)
{
    public static VerifyResult Accept(uint fvUsed) => new(true, null, fvUsed);
    public static VerifyResult Reject(RejectReason reason) => new(false, reason, 0);
}

internal static class BinaryEndian
{
    public static byte[] ToBytesBigEndian(this ushort value)
        => [ (byte)(value >> 8), (byte)value ];

    public static byte[] ToBytesBigEndian(this uint value)
        => [ (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value ];
}