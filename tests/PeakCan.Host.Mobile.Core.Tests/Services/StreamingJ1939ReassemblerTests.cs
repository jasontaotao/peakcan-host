using FluentAssertions;
using PeakCan.HIL.Core.J1939;
using PeakCan.Host.Core.J1939;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Mobile.Core.Services;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.Services;

public class StreamingJ1939ReassemblerTests
{
    private static readonly byte[] Payload = Enumerable.Range(0, 14).Select(i => (byte)(i + 1)).ToArray(); // 14B → 2 包

    private static ReplayFrame Frame(double t, uint rawId, byte[] data) =>
        new(t, rawId, (byte)data.Length, data, default, true);

    private static ReplayFrame BamCm(double t, uint pgn, byte sa, ushort size, byte packets)
        => Frame(t, J1939Id.Compose(6, 0x00EC00, sa, 0xFF), TpCmMessage.Bam(size, packets, pgn).Encode());

    private static ReplayFrame BamDt(double t, byte sa, byte seq, byte[] chunk)
        => Frame(t, J1939Id.Compose(6, 0x00EB00, sa, 0xFF), new TpDtMessage(seq, chunk).Encode());

    private static List<ReplayFrame> BamSequence(double start, uint pgn, byte sa)
    {
        var frames = new List<ReplayFrame> { BamCm(start, pgn, sa, (ushort)Payload.Length, 2) };
        frames.Add(BamDt(start + 0.01, sa, 1, Payload[..7]));
        frames.Add(BamDt(start + 0.02, sa, 2, Payload[7..]));
        return frames;
    }

    [Fact]
    public void Ingest_CompleteBam_RaisesCompleteRow()
    {
        var reassembler = new StreamingJ1939Reassembler();
        var rows = new List<J1939ReassembledRow>();
        reassembler.MessageReassembled += rows.Add;

        foreach (var f in BamSequence(1.0, 0x000200, 0xF4))
            reassembler.Ingest(f);

        var row = rows.Should().ContainSingle().Subject;
        row.PgnText.Should().Be("0x000200");
        row.SaText.Should().Be("F4");
        row.DaText.Should().Be("FF");      // BAM 广播
        row.ModeText.Should().Be("BAM");
        row.LengthText.Should().Be("14");
        row.StatusText.Should().Be("完成");
        row.CompletedText.Should().Be("1.020000");
    }

    [Fact]
    public void Ingest_StandardFrame_Ignored()
    {
        var reassembler = new StreamingJ1939Reassembler();
        var rows = new List<J1939ReassembledRow>();
        reassembler.MessageReassembled += rows.Add;

        reassembler.Ingest(new ReplayFrame(0, 0x123, 2, [1, 2], default, false));

        rows.Should().BeEmpty();
    }

    [Fact]
    public void Flush_PendingSession_RaisesTruncatedRow()
    {
        var reassembler = new StreamingJ1939Reassembler();
        var rows = new List<J1939ReassembledRow>();
        reassembler.MessageReassembled += rows.Add;

        // 只喂 CM + 1×DT，不喂完
        reassembler.Ingest(BamCm(0.0, 0x000200, 0xF4, (ushort)Payload.Length, 2));
        reassembler.Ingest(BamDt(0.01, 0xF4, 1, Payload[..7]));
        reassembler.Flush();

        var row = rows.Should().ContainSingle().Subject;
        row.StatusText.Should().Be("截断");
        row.PgnText.Should().Be("0x000200");
        row.LengthText.Should().Be("14");  // payload 长度 = 声明总长
        row.CompletedText.Should().Be("0.010000");
    }

    [Fact]
    public void Flush_GapInSequence_RaisesPacketLossRow()
    {
        var reassembler = new StreamingJ1939Reassembler();
        var rows = new List<J1939ReassembledRow>();
        reassembler.MessageReassembled += rows.Add;

        // 跳过序号 2 直接发 3
        reassembler.Ingest(BamCm(0.0, 0x000200, 0xF4, 21, 3));
        reassembler.Ingest(BamDt(0.01, 0xF4, 1, new byte[7]));
        reassembler.Ingest(BamDt(0.02, 0xF4, 3, new byte[7]));
        reassembler.Flush();

        var row = rows.Should().ContainSingle().Subject;
        row.StatusText.Should().Be("丢包");
    }

    [Fact]
    public void Reset_DiscardsPendingSession()
    {
        var reassembler = new StreamingJ1939Reassembler();
        var rows = new List<J1939ReassembledRow>();
        reassembler.MessageReassembled += rows.Add;

        reassembler.Ingest(BamCm(0.0, 0x000200, 0xF4, (ushort)Payload.Length, 2));
        reassembler.Ingest(BamDt(0.01, 0xF4, 1, Payload[..7]));
        reassembler.Reset();
        reassembler.Flush();

        rows.Should().BeEmpty();
    }

    [Fact]
    public void Ingest_MalformedTp_SwallowedAndCounted()
    {
        var reassembler = new StreamingJ1939Reassembler();
        var rows = new List<J1939ReassembledRow>();
        reassembler.MessageReassembled += rows.Add;

        // DT 数据不足 8 字节（畸形）不抛；继续喂后续帧正常重组
        reassembler.Ingest(new ReplayFrame(0, J1939Id.Compose(6, 0x00EB00, 0xF4, 0xFF), 3, [0x01, 0x02, 0x03], default, true));
        reassembler.Ingest(BamCm(0.1, 0x000200, 0xF4, (ushort)Payload.Length, 2));
        reassembler.Ingest(BamDt(0.11, 0xF4, 1, Payload[..7]));
        reassembler.Ingest(BamDt(0.12, 0xF4, 2, Payload[7..]));

        rows.Should().ContainSingle();
        reassembler.MalformedCount.Should().BeGreaterThan(0);
    }
}