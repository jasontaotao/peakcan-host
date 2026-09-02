using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using PeakCan.HIL.Core.Replay;
using PeakCan.Host.App.Services.J1939;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.J1939;

public class J1939ReassemblyServiceTests
{
    private static readonly byte[] Payload = Enumerable.Range(0, 49).Select(i => (byte)(i + 1)).ToArray();
    private static readonly J1939ReassemblyService Service = new();

    private static List<ReplayFrame> BamFrames(byte[] payload, uint pgn, byte sa, double startSec = 1.0, int dropIndex = -1)
    {
        var frames = new List<ReplayFrame>();
        var cmId = J1939Id.Compose(6, 0x00EC00, sa, 0xFF);
        var dtId = J1939Id.Compose(6, 0x00EB00, sa, 0xFF);
        frames.Add(new ReplayFrame(startSec, cmId, 8, TpCmMessage.Bam((ushort)payload.Length, (byte)((payload.Length + 6) / 7), pgn).Encode(), FrameFlags.None, true));
        int packets = (payload.Length + 6) / 7;
        for (int i = 0; i < packets; i++)
        {
            if (i + 1 == dropIndex) continue;   // 造丢包/截断
            int take = Math.Min(7, payload.Length - i * 7);
            var chunk = new byte[take];
            Array.Copy(payload, i * 7, chunk, 0, take);
            frames.Add(new ReplayFrame(startSec + (i + 1) * 0.01, dtId, 8, new TpDtMessage((byte)(i + 1), chunk).Encode(), FrameFlags.None, true));
        }
        return frames;
    }

    /// <summary>
    /// RTS/CTS 点对点会话帧序列（非标 27930 设备：多帧走 RTS/CTS 而非 BAM 广播，
    /// TP.DT 的目标地址是实际对端地址而非 0xFF）。离线重组必须对任意 (SA, DA) 的
    /// RTS/CTS 会话建会话重组——否则这类 log 一个虚拟帧都不产生，多帧信号取不到。
    /// </summary>
    private static List<ReplayFrame> RtsCtsFrames(byte[] payload, uint pgn, byte sa, byte da, double startSec = 1.0)
    {
        var frames = new List<ReplayFrame>();
        int packets = (payload.Length + 6) / 7;
        // RTS：SA→DA（点对点，PS=DA）
        frames.Add(new ReplayFrame(startSec, J1939Id.Compose(6, 0x00EC00, sa, da), 8,
            TpCmMessage.Rts((ushort)payload.Length, (byte)packets, 0xFF, pgn).Encode(), FrameFlags.None, true));
        // CTS：对端回（SA=da, DA=sa）；离线无发送会话，此帧被静默忽略，仅保真模拟总线
        frames.Add(new ReplayFrame(startSec + 0.01, J1939Id.Compose(6, 0x00EC00, da, sa), 8,
            TpCmMessage.Cts(0xFF, 1, pgn).Encode(), FrameFlags.None, true));
        // TP.DT：SA→DA
        for (int i = 0; i < packets; i++)
        {
            int take = Math.Min(7, payload.Length - i * 7);
            var chunk = new byte[take];
            Array.Copy(payload, i * 7, chunk, 0, take);
            frames.Add(new ReplayFrame(startSec + (i + 2) * 0.01, J1939Id.Compose(6, 0x00EB00, sa, da), 8,
                new TpDtMessage((byte)(i + 1), chunk).Encode(), FrameFlags.None, true));
        }
        return frames;
    }

    [Fact]
    public void Complete_Bam_Produces_Single_Row()
    {
        var result = Service.Reassemble(BamFrames(Payload, 0x000200, 0xF4));

        result.Should().ContainSingle();
        result[0].Status.Should().Be(ReassemblyStatus.Complete);
        result[0].Message.Payload.Should().Equal(Payload);
        result[0].Message.CompletedTimestampSec.Should().Be(1.07);
    }

    [Fact]
    public void Missing_Trailing_Packets_Reports_Truncated()
    {
        var result = Service.Reassemble(BamFrames(Payload, 0x000200, 0xF4, dropIndex: 6).Take(4).ToList());

        result.Should().ContainSingle();
        result[0].Status.Should().Be(ReassemblyStatus.Truncated);
        result[0].Message.Payload.Should().HaveCount(49);   // 部分载荷保留（0xFF 填充）
    }

    [Fact]
    public void Sequence_Gap_Reports_PacketLoss()
    {
        var result = Service.Reassemble(BamFrames(Payload, 0x000200, 0xF4, dropIndex: 3));

        result.Should().ContainSingle();
        result[0].Status.Should().Be(ReassemblyStatus.PacketLoss);
    }

    [Fact]
    public void Multiple_Messages_Sorted_By_Completion()
    {
        var frames = new List<ReplayFrame>();
        frames.AddRange(BamFrames(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 0x00F001, 0x11, startSec: 2.0));
        frames.AddRange(BamFrames(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 0x00F002, 0x22, startSec: 1.0));

        var result = Service.Reassemble(frames);

        result.Select(r => r.Message.Pgn).Should().ContainInOrder(0x00F002, 0x00F001);
    }

    [Fact]
    public void Complete_RtsCts_Session_Is_Reassembled_In_Offline_Mode()
    {
        // 回归（乌海吉高高压盒真实 log）：27930 多帧走 RTS/CTS 点对点（DA=0x56 非 0xFF）。
        // 修复前 HandleRts 要求 DA ∈ _localAddresses（离线恒空）→ RTS 不建会话 → TP.DT 全丢 →
        // 无虚拟帧 → 图表/watch 多帧信号全空。离线模式必须照常重组此类会话。
        var frames = RtsCtsFrames(Payload, 0x000200, 0xF4, 0x56);

        var result = Service.Reassemble(frames);

        result.Should().ContainSingle();
        result[0].Status.Should().Be(ReassemblyStatus.Complete);
        result[0].Message.Mode.Should().Be(TpMode.RtsCts);
        result[0].Message.Payload.Should().Equal(Payload);
        result[0].Message.CompletedTimestampSec.Should().Be(1.08);
    }

    [Fact]
    public void Malformed_Tp_Frame_Is_Skipped_Not_Thrown()
    {
        var frames = new List<ReplayFrame>
        {
            new(1.0, J1939Id.Compose(6, 0x00EC00, 0xF4, 0xFF), 8, new byte[] { 0x99, 0, 0, 0, 0, 0, 2, 0 }, FrameFlags.None, true),
        };
        frames.AddRange(BamFrames(Payload, 0x000200, 0xF4, startSec: 2.0));

        var result = Service.Reassemble(frames);

        result.Should().ContainSingle();   // 畸形帧跳过（LogWarning 9312），不中断
    }

    [Fact]
    public void Non_Tp_Frames_Are_Ignored()
    {
        var result = Service.Reassemble(new List<ReplayFrame>
        {
            new(1.0, 0x180256F4, 8, new byte[8], FrameFlags.None, true),
            new(2.0, 0x123, 2, new byte[] { 1, 2 }, FrameFlags.None, false),
        });

        result.Should().BeEmpty();
    }
}
