using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Services.SecOc;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Infrastructure.Channel.SecOc;
using PeakCan.Security.SecOc;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

public class SecOcBadgeJoinerTests
{
    private const ushort Handle = 0x51;
    private static readonly uint[] ProtectedIds = { 0x123, 0x456 };
    private static readonly uint[] SecondHandleIds = { 0x789 };

    private static CanFrame MakeFrame(uint id) => new(
        new CanId(id, FrameFormat.Standard),
        new byte[] { 1, 2, 3, 4 },
        FrameFlags.None,
        new ChannelId(Handle),
        Timestamp.FromMicroseconds(1_000_000UL));

    [Fact]
    public void Join_HandleNotConfigured_ReturnsOffline()
    {
        var joiner = new SecOcBadgeJoiner(new SecOcVerdictTable());
        joiner.Join(MakeFrame(0x123)).Should().Be(SecOcBadge.Offline);
    }

    [Fact]
    public void Join_UnprotectedId_ReturnsUnprotected_AndDoesNotConsumeSeq()
    {
        var table = new SecOcVerdictTable();
        var joiner = new SecOcBadgeJoiner(table);
        joiner.Configure(Handle, ProtectedIds);

        joiner.Join(MakeFrame(0x777)).Should().Be(SecOcBadge.Unprotected);

        // 未保护帧不消耗 seq：首个受保护帧仍与 seq=1 对齐。
        table.Record(Handle, 1, new SecOcVerdict(0x123, Accepted: true, Reason: null));
        joiner.Join(MakeFrame(0x123)).Should().Be(SecOcBadge.Accepted);
    }

    [Fact]
    public void Join_Accepted_ShowsCheckmark()
    {
        var table = new SecOcVerdictTable();
        var joiner = new SecOcBadgeJoiner(table);
        joiner.Configure(Handle, ProtectedIds);

        table.Record(Handle, 1, new SecOcVerdict(0x123, Accepted: true, Reason: null));
        var badge = joiner.Join(MakeFrame(0x123));

        badge.Kind.Should().Be(SecOcBadgeKind.Accepted);
        badge.Text.Should().Be("✓");
    }

    [Fact]
    public void Join_Rejected_ShowsReason_AndSeqAdvancesPerProtectedFrame()
    {
        var table = new SecOcVerdictTable();
        var joiner = new SecOcBadgeJoiner(table);
        joiner.Configure(Handle, ProtectedIds);

        table.Record(Handle, 1, new SecOcVerdict(0x123, Accepted: false, RejectReason.BadMac));
        table.Record(Handle, 2, new SecOcVerdict(0x456, Accepted: false, RejectReason.Replay));

        joiner.Join(MakeFrame(0x123)).Text.Should().Be("✗ BadMac");
        joiner.Join(MakeFrame(0x456)).Text.Should().Be("✗ Replay");
    }

    [Fact]
    public void Join_VerdictMissing_ReturnsOffline() // seq 错位或表已清
    {
        var joiner = new SecOcBadgeJoiner(new SecOcVerdictTable());
        joiner.Configure(Handle, ProtectedIds);
        joiner.Join(MakeFrame(0x123)).Should().Be(SecOcBadge.Offline);
    }

    [Fact]
    public void Reset_RemovesHandleState()
    {
        var table = new SecOcVerdictTable();
        var joiner = new SecOcBadgeJoiner(table);
        joiner.Configure(Handle, ProtectedIds);

        joiner.Reset(Handle);

        joiner.Join(MakeFrame(0x123)).Should().Be(SecOcBadge.Offline); // 断开后不残留配置
    }

    [Fact]
    public void ResetAll_ClearsAllHandles()
    {
        var table = new SecOcVerdictTable();
        var joiner = new SecOcBadgeJoiner(table);
        joiner.Configure(Handle, ProtectedIds);
        joiner.Configure(0x52, SecondHandleIds);

        joiner.ResetAll();

        joiner.Join(MakeFrame(0x123)).Should().Be(SecOcBadge.Offline);
        joiner.Join(new CanFrame(new CanId(0x789, FrameFormat.Standard), new byte[] { 1 },
            FrameFlags.None, new ChannelId(0x52), Timestamp.FromMicroseconds(1UL)))
            .Should().Be(SecOcBadge.Offline);
    }
}