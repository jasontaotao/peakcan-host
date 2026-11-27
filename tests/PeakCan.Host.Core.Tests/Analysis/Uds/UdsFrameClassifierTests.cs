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
    public void Zero_length_single_frame_is_noise_not_a_silent_frame()
    {
        var frames = new[] { Frame(0x7E0, new byte[] { 0x00 }) };
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

    /// <summary>
    /// <b>功能寻址的请求必须被识别成「功能寻址请求」，不能当成背景流量丢掉。</b>
    /// <para>
    /// 真实日志 <c>150635_log.asc</c> 里测试仪用 <c>7DF</c> 发了三条功能广播
    /// （<c>10 03</c> / <c>85 02</c> / <c>28 03 01</c>），ECU 全部在 <c>71E</c> 上回了正响应。
    /// 只认物理请求 ID 时，请求被判非诊断丢弃、响应却被收下——于是凭空出现三条
    /// 「孤立响应」，报告还说不清它们从哪来。用户会以为 ECU 在发没人要的响应。
    /// </para>
    /// </summary>
    [Fact]
    public void Functional_broadcast_request_is_matched_not_dropped_as_background()
    {
        var addr = new UdsOfflineAddress(
            Channel: 1, RequestId: 0x7E0, ResponseId: 0x7E8,
            IsExtended: false, FunctionalId: 0x7DF);

        var frames = new[] { Frame(0x7DF, new byte[] { 0x02, 0x10, 0x03 }) };
        var c = new UdsFrameClassifier().Classify(frames, addr);

        c.Should().ContainSingle().Which.Class.Should().Be(UdsFrameClass.MatchedDiagnostic);
        c[0].Role.Should().Be(UdsDirectionRole.Request,
            "功能广播是测试仪发出的请求，方向不能判成响应");
    }

    /// <summary>没配 <c>FunctionalId</c> 时行为不变——<c>7DF</c> 仍是背景流量。</summary>
    [Fact]
    public void Without_a_functional_id_the_broadcast_id_stays_non_diagnostic()
    {
        var frames = new[] { Frame(0x7DF, new byte[] { 0x02, 0x10, 0x03 }) };
        var c = new UdsFrameClassifier().Classify(frames, Addr);

        c.Should().ContainSingle().Which.Class.Should().Be(UdsFrameClass.NonDiagnostic);
    }
}
