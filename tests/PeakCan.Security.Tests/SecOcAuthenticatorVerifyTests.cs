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