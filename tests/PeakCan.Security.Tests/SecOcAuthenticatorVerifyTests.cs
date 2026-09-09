using PeakCan.Security.Cmac;
using PeakCan.Security.SecOc;

namespace PeakCan.Security.Tests;

/// <summary>
/// spec §6.2 RX 验收语义：候选集重构 k∈{0,+1,−1}，
/// 按 MAC 通过者的位置分类 Accept/Replay/FvRollback/FvAnomaly/BadMac。
/// 攻击→分类预期见 spec §6.2 对照表。
/// </summary>
public sealed class SecOcAuthenticatorVerifyTests
{
    private static readonly byte[] Key = Convert.FromHexString("00112233445566778899aabbccddeeff");

    private static SecOcProfile Profile() => new()
    {
        DataId = 0x0001,
        FvLenBits = 16,
        MacLenBits = 24,
    };

    private static SecOcAuthenticator MakeAuth(uint initialFv = 0)
        => new(Profile(), Key, new BouncyCastleCmacProvider(), initialFv);

    private static byte[] Sign(SecOcAuthenticator auth, params byte[] authenticData)
    {
        var frame = new byte[auth.FrameLength(authenticData.Length)];
        auth.Sign(authenticData, frame);
        return frame;
    }

    /// <summary>取帧内 MAC 区字节（尾部 macLen/8 字节）。</summary>
    private static int MacOffset(SecOcProfile p)
        => 7 + p.FvLenBits / 8; // 7 字节净荷 + FV 区

    [Fact]
    public void accepts_sequential_frames()
    {
        // Arrange
        var auth = MakeAuth();
        var f0 = Sign(auth, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07);
        var f1 = Sign(auth, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07);

        // Act
        var r0 = auth.Verify(f0);
        var r1 = auth.Verify(f1);

        // Assert
        r0.Accepted.Should().BeTrue();
        r1.Accepted.Should().BeTrue();
    }

    [Fact]
    public void rejects_tampered_mac_as_badmac()
    {
        // Arrange
        var auth = MakeAuth();
        var frame = Sign(auth, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07);
        frame[^1] ^= 0xFF; // 篡改 MAC 末字节

        // Act
        var result = auth.Verify(frame);

        // Assert
        result.Accepted.Should().BeFalse();
        result.Reason.Should().Be(RejectReason.BadMac);
    }

    [Fact]
    public void rejects_forged_fv_as_badmac()
    {
        // Arrange（篡改 FV 字节：真实 FV 不在候选集，MAC 必败——spec §6.2 对照表）
        var auth = MakeAuth();
        var frame = Sign(auth, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07);
        frame[7] ^= 0x01; // FV 区首字节异或

        // Act
        var result = auth.Verify(frame);

        // Assert
        result.Accepted.Should().BeFalse();
        result.Reason.Should().Be(RejectReason.BadMac);
    }

    [Fact]
    public void detects_same_frame_replay()
    {
        // Arrange
        var auth = MakeAuth();
        var frame = Sign(auth, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07);
        auth.Verify(frame).Accepted.Should().BeTrue();

        // Act（重发同帧：cand == last → Replay）
        var result = auth.Verify(frame);

        // Assert
        result.Accepted.Should().BeFalse();
        result.Reason.Should().Be(RejectReason.Replay);
    }

    [Fact]
    public void detects_older_frame_as_fv_rollback()
    {
        // Arrange：依次接收 fv0/fv1/fv2，然后重放 fv1 的旧帧
        var auth = MakeAuth();
        var f1 = Sign(auth, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07); // fv0
        var f2 = Sign(auth, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07); // fv1
        var f3 = Sign(auth, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07); // fv2
        auth.Verify(f1); auth.Verify(f2); auth.Verify(f3);

        // Act（重放 fv1：cand=1 < last=2 且在窗口内 → FvRollback）
        var result = auth.Verify(f2);

        // Assert
        result.Accepted.Should().BeFalse();
        result.Reason.Should().Be(RejectReason.FvRollback);
    }

    [Fact]
    public void detects_far_older_frame_as_fv_anomaly()
    {
        // Arrange：fvLen=8 → Window=2^7=128；last=200 后重放 fv=0 帧（diff=200 > 128）
        // 注：不用 fvLen=16 是因为首帧只能从 recvLow（高位置零）重构，
        // 中途接入的观察者接不住高位非零的首帧——spec §6.2 的已知边界。
        var profile = new SecOcProfile { DataId = 0x0001, FvLenBits = 8, MacLenBits = 24 };
        var auth = new SecOcAuthenticator(profile, Key, new BouncyCastleCmacProvider());
        var frames = new List<byte[]>();
        for (var i = 0; i <= 200; i++)
            frames.Add(Sign(auth, 0x01));
        for (var i = 0; i < frames.Count; i++)
            auth.Verify(frames[i]).Accepted.Should().BeTrue($"frame {i} 应被接受");
        var ancient = new SecOcAuthenticator(profile, Key, new BouncyCastleCmacProvider());
        var oldFrame = Sign(ancient, 0x01); // 同键同数据、fv=0 的旧帧

        // Act
        var result = auth.Verify(oldFrame);

        // Assert
        result.Accepted.Should().BeFalse();
        result.Reason.Should().Be(RejectReason.FvAnomaly);
    }

    [Fact]
    public void mid_stream_observer_rejects_first_frame_with_nonzero_high_bits()
    {
        // Arrange：spec §6.2 首帧语义边界——中途接入者接不住首帧（文档化行为）
        var auth = MakeAuth(70000);
        var frame = Sign(auth, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07); // fv=70000

        // Act
        var result = auth.Verify(frame);

        // Assert：高位置零重构失败 → BadMac（v1 接受，需从同步点开始观察）
        result.Accepted.Should().BeFalse();
        result.Reason.Should().Be(RejectReason.BadMac);
    }

    [Fact]
    public void accepts_cross_block_frame_via_k_plus_one()
    {
        // Arrange：fv=65535 后紧跟 fv=65536（低 16 位回绕到 0，必须 k=+1 重构）
        var auth = MakeAuth(65535);
        var fA = Sign(auth, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07); // fv 65535
        var fB = Sign(auth, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07); // fv 65536
        auth.Verify(fA).Accepted.Should().BeTrue();

        // Act
        var result = auth.Verify(fB);

        // Assert：recvLow=0 且 cand==last 会被 k=0 先命中 → 需判定逻辑正确跳过
        result.Accepted.Should().BeTrue();
    }

    [Theory]
    [InlineData(24)]
    [InlineData(32)]
    public void rejects_fv_len_above_16(int fvLenBits)
    {
        // H-1：fvLen>16 在 v1 实现里 TX 崩溃 / RX 静默误判（ushort 截断、
        // Sign 切片越界），必须 fail-fast 拒绝而非放行后烂掉。
        // 24 位 freshness（样例矩阵 VCU_ChrgCtrlCmd 行）属 v2 扩展。
        var profile = new SecOcProfile { DataId = 1, FvLenBits = fvLenBits, MacLenBits = 24 };

        // Act
        var act = () => new SecOcAuthenticator(profile, Key, new BouncyCastleCmacProvider());

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void rejects_fv_full_bits_not_32()
    {
        // H-1：ComputeMac 固定写 4 字节 FV，FvFullBits≠32 时字节序/长度不一致
        var profile = new SecOcProfile { DataId = 1, FvLenBits = 16, MacLenBits = 24, FvFullBits = 64 };

        // Act
        var act = () => new SecOcAuthenticator(profile, Key, new BouncyCastleCmacProvider());

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void key_is_defensively_copied_on_construction()
    {
        // M-2：调用方构造后修改源数组不得影响已构造的加签器
        // Arrange：构造后、签名前就变异调用方数组——只有构造时拍了快照才会免疫
        var key = Convert.FromHexString("00112233445566778899aabbccddeeff");
        var auth = new SecOcAuthenticator(Profile(), key, new BouncyCastleCmacProvider());
        key[0] ^= 0xFF; // 模拟外部在 auth 存活期间改写了数组
        var baseline = new SecOcAuthenticator(
            Profile(), Convert.FromHexString("00112233445566778899aabbccddeeff"),
            new BouncyCastleCmacProvider());
        var f1 = Sign(auth, 0x01);       // fv=0
        var f2 = Sign(baseline, 0x01);   // fv=0，原 key

        // Assert：auth 用的是构造时快照（原 key）⇒ 与 baseline 的 MAC 逐字节一致
        //（无防御拷贝时 auth 持有的就是被改的数组，f1 的 MAC 会不同）
        f1.AsSpan(3).SequenceEqual(f2.AsSpan(3)).Should().BeTrue();
    }

    [Fact]
    public void classifies_window_boundary_and_previous_block_rollback()
    {
        // LOW 补全：窗口边界（diff==Window → FvRollback / diff==Window+1 → FvAnomaly）
        // 与 k=−1 作为首个通过者（跨块旧帧）。
        // fvLen=8 → Window=2^7=128；接受 0..260（含 256 跨块）
        var profile = new SecOcProfile { DataId = 0x0001, FvLenBits = 8, MacLenBits = 24 };
        var auth = new SecOcAuthenticator(profile, Key, new BouncyCastleCmacProvider());
        var frames = new List<byte[]>();
        for (var i = 0; i <= 260; i++)
            frames.Add(Sign(auth, 0x01));
        for (var i = 0; i < frames.Count; i++)
            auth.Verify(frames[i]).Accepted.Should().BeTrue($"frame {i} 应被接受");

        // last=260：132 → diff==128 恰在窗口内 → FvRollback
        var rBoundary = auth.Verify(frames[132]);
        rBoundary.Accepted.Should().BeFalse();
        rBoundary.Reason.Should().Be(RejectReason.FvRollback);

        // last=260：131 → diff==129 越出窗口 → FvAnomaly
        var rBeyond = auth.Verify(frames[131]);
        rBeyond.Accepted.Should().BeFalse();
        rBeyond.Reason.Should().Be(RejectReason.FvAnomaly);

        // 上一块旧帧：k=−1 作为首个通过者（recvLow=200 的高位组合）→ FvRollback
        var rPrevBlock = auth.Verify(frames[200]);
        rPrevBlock.Accepted.Should().BeFalse();
        rPrevBlock.Reason.Should().Be(RejectReason.FvRollback);
    }

    [Fact]
    public void rejects_malformed_frame()
    {
        // Arrange（帧长小于 FV+MAC 区总长）
        var auth = MakeAuth();
        var shortFrame = new byte[3];

        // Act
        var result = auth.Verify(shortFrame);

        // Assert
        result.Accepted.Should().BeFalse();
        result.Reason.Should().Be(RejectReason.Malformed);
    }
}