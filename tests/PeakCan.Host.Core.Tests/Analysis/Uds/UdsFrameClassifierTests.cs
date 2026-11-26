using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Analysis.Uds;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Uds;

public sealed class UdsFrameClassifierTests
{
    private static readonly UdsOfflineAddress Addr = new(Channel: 1, RequestId: 0x7E0, ResponseId: 0x7E8, IsExtended: false);

    private static ReplayFrame Frame(uint id, byte[] data, FrameFlags flags = FrameFlags.None,
        ushort channel = 1, bool extended = false)
        => new(Timestamp: 0, Id: id, Dlc: (byte)data.Length, Data: data, Flags: flags,
               IsExtended: extended, Channel: channel);

    [Fact]
    public void Can_fd_is_unsupported_even_when_address_matches()
    {
        var frames = new[] { Frame(0x7E0, new byte[] { 0x02, 0x10, 0x01 }, FrameFlags.Fd) };
        var c = new UdsFrameClassifier().Classify(frames, Addr);
        c.Should().ContainSingle().Which.Class.Should().Be(UdsFrameClass.UnsupportedInput);
    }

    [Fact]
    public void Other_id_on_same_channel_is_non_diagnostic()
    {
        var frames = new[] { Frame(0x123, new byte[] { 0x01, 0x02 }) };
        var c = new UdsFrameClassifier().Classify(frames, Addr);
        c.Should().ContainSingle().Which.Class.Should().Be(UdsFrameClass.NonDiagnostic);
    }

    [Fact]
    public void Matching_request_frame_is_diagnostic_request()
    {
        var frames = new[] { Frame(0x7E0, new byte[] { 0x02, 0x10, 0x01 }) };
        var c = new UdsFrameClassifier().Classify(frames, Addr);
        var item = c.Should().ContainSingle().Subject;
        item.Class.Should().Be(UdsFrameClass.MatchedDiagnostic);
        item.Role.Should().Be(UdsDirectionRole.Request);
    }

    [Fact]
    public void Invalid_pci_is_noise_with_reason()
    {
        var frames = new[] { Frame(0x7E0, new byte[] { 0x0F, 0x10 }) }; // SF 声明 15 字节但只有 1 字节
        var c = new UdsFrameClassifier().Classify(frames, Addr);
        c.Should().ContainSingle().Which.Class.Should().Be(UdsFrameClass.Noise);
        c[0].NoiseReason.Should().Be(UdsNoiseReason.InvalidPci);
    }

    [Fact]
    public void Counts_close_on_mixed_log()
    {
        var frames = new[]
        {
            Frame(0x7E0, new byte[] { 0x02, 0x10, 0x01 }),          // matched
            Frame(0x123, new byte[] { 0x01 }),                       // non-diagnostic
            Frame(0x7E0, new byte[] { 0x0F, 0x10 }),                 // noise
            Frame(0x7E8, new byte[] { 0x01 }, FrameFlags.Fd),        // unsupported
        };
        var classes = new UdsFrameClassifier().Classify(frames, Addr);
        var counts = UdsFrameCounts.From(classes, frames.Length);
        counts.Closes.Should().BeTrue();
        counts.Input.Should().Be(4);
        counts.MatchedDiagnostic.Should().Be(1);
        counts.NonDiagnostic.Should().Be(1);
        counts.Noise.Should().Be(1);
        counts.UnsupportedInput.Should().Be(1);
    }
}
