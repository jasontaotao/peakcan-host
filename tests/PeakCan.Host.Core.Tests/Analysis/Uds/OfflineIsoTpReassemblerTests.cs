using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Analysis.Uds;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Uds;

public sealed class OfflineIsoTpReassemblerTests
{
    private static readonly UdsOfflineAddress Addr = new(Channel: 1, RequestId: 0x7E0, ResponseId: 0x7E8, IsExtended: false);

    private static ReplayFrame F(uint id, params byte[] data)
        => new(Timestamp: data.Length, Id: id, Dlc: (byte)data.Length, Data: data,
               Flags: FrameFlags.None, IsExtended: false, Channel: 1, IsTransmit: null);

    private static UdsReassemblyResult Run(IReadOnlyList<ReplayFrame> frames)
    {
        var classes = new UdsFrameClassifier().Classify(frames, Addr);
        return new OfflineIsoTpReassembler().Reassemble(frames, classes, Addr);
    }

    [Fact]
    public void Single_frame_forms_complete_pdu()
    {
        var frames = new[] { F(0x7E0, 0x02, 0x10, 0x01) };
        var r = Run(frames);
        r.Pdus.Should().ContainSingle().Which.Should().Match<UdsOfflinePdu>(p =>
            p.Role == UdsDirectionRole.Request && p.IsComplete && !p.IsMultiFrame
            && p.Payload.SequenceEqual(new byte[] { 0x10, 0x01 }));
    }

    [Fact]
    public void First_and_consecutive_frames_reassemble()
    {
        // FF: 0x10 0x0A + 6 bytes; CF: 0x21 + remaining 4 bytes
        var frames = new[]
        {
            F(0x7E0, 0x10, 0x0A, 0x62, 0xF1, 0x90, 0x31, 0x32, 0x33),
            F(0x7E0, 0x21, 0x34, 0x35, 0x36, 0x37),
        };
        var r = Run(frames);
        var pdu = r.Pdus.Should().ContainSingle().Subject;
        pdu.IsComplete.Should().BeTrue();
        pdu.Payload.Should().Equal(0x62, 0xF1, 0x90, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37);
        pdu.FirstEvidenceIndex.Should().Be(0);
        pdu.LastEvidenceIndex.Should().Be(1);
    }

    [Fact]
    public void Orphan_consecutive_frame_is_noise_not_a_pdu()
    {
        var r = Run(new[] { F(0x7E0, 0x21, 0x01, 0x02) });
        r.Pdus.Should().BeEmpty();
        r.ExtraNoiseIndices.Should().ContainSingle().Which.Should().Be(0);
    }

    [Fact]
    public void Wrong_sequence_marks_that_frame_noise_and_keeps_partial_pdu()
    {
        var frames = new[]
        {
            F(0x7E0, 0x10, 0x0A, 0x62, 0xF1, 0x90, 0x31, 0x32, 0x33),
            F(0x7E0, 0x22, 0x34, 0x35, 0x36, 0x37), // 期望 0x21
        };
        var r = Run(frames);
        var pdu = r.Pdus.Should().ContainSingle().Subject;
        pdu.IsComplete.Should().BeFalse();
        r.ExtraNoiseIndices.Should().Contain(1);
    }

    [Fact]
    public void Flow_control_is_evidence_only_and_forms_no_pdu()
    {
        var frames = new[]
        {
            F(0x7E0, 0x10, 0x0A, 0x62, 0xF1, 0x90, 0x31, 0x32, 0x33),
            F(0x7E8, 0x30, 0x00, 0x00), // FC（响应方向）
            F(0x7E0, 0x21, 0x34, 0x35, 0x36, 0x37),
        };
        var r = Run(frames);
        var pdu = r.Pdus.Should().ContainSingle().Subject;
        pdu.Evidence.Should().Contain(1, "流控帧必须作为证据挂到多帧 PDU");
        r.ExtraNoiseIndices.Should().BeEmpty();
    }

    [Fact]
    public void Direction_unknown_frames_still_reassemble_by_can_id()
    {
        var frames = new[]
        {
            new ReplayFrame(0.0, 0x7E0, 3, new byte[] { 0x02, 0x10, 0x01 },
                            FrameFlags.None, IsExtended: false, Channel: 1, IsTransmit: null),
        };
        var r = Run(frames);
        r.Pdus.Should().ContainSingle().Which.Role.Should().Be(UdsDirectionRole.Request);
    }

    [Fact]
    public void Single_frame_arriving_mid_multi_frame_flushes_old_pdu_as_incomplete()
    {
        var frames = new[]
        {
            F(0x7E0, 0x10, 0x0A, 0x62, 0xF1, 0x90, 0x31, 0x32, 0x33), // FF, 未完成
            F(0x7E0, 0x02, 0x3E, 0x00),                              // 新 SF 打断
        };
        var r = Run(frames);
        r.Pdus.Should().HaveCount(2);
        r.Pdus.Should().Contain(p => !p.IsComplete && p.IsMultiFrame);
        r.Pdus.Should().Contain(p => p.IsComplete && !p.IsMultiFrame);
    }
}
