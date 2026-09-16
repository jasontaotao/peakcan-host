using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Services.SecOc;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Infrastructure.Channel.SecOc;
using PeakCan.Security.SecOc;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>TraceViewModel + SecOcBadgeJoiner 集成：resolver 赋值后徽章按 join 结果渲染。</summary>
public class TraceViewModelSecOcResolverTests
{
    private static readonly uint[] ProtectedIds = { 0x123u };

    private static CanFrame MakeFrame(uint id, ushort handle = 0x51) => new(
        new CanId(id, FrameFormat.Standard), new byte[] { 1, 2, 3, 4 },
        FrameFlags.None, new ChannelId(handle), Timestamp.FromMicroseconds(1_000_000UL));

    [Fact]
    public void AppendBatchCore_WithJoinerResolver_MarksAcceptedAndUnprotected()
    {
        var table = new SecOcVerdictTable();
        var joiner = new SecOcBadgeJoiner(table);
        joiner.Configure(0x51, ProtectedIds);
        table.Record(0x51, 1, new SecOcVerdict(0x123, Accepted: true, Reason: null));

        var vm = new TraceViewModel { SecOcBadgeResolver = joiner.Join };
        vm.AppendBatchCore(new[] { MakeFrame(0x123), MakeFrame(0x777) });

        vm.Entries.Should().HaveCount(2);
        vm.Entries[0].SecOcBadge.Text.Should().Be("✓");
        vm.Entries[1].SecOcBadge.Text.Should().Be("未保护");
    }

    // final review I1 锁定：暂停只是不显示，SecOC seq 对齐不能停——恢复后
    // 徽章必须仍对应当前帧（通道 seq=3）而非暂停前旧帧（seq=1）。
    [Fact]
    public void PausedProtectedFrames_StillAdvanceJoinerSeq_ResumeAlignsBadges()
    {
        var table = new SecOcVerdictTable();
        var joiner = new SecOcBadgeJoiner(table);
        joiner.Configure(0x51, ProtectedIds);
        var vm = new TraceViewModel { SecOcBadgeResolver = joiner.Join };

        vm.IsPaused = true;
        // 暂停期间两帧受保护帧：通道 seq=1,2 → 均 Rejected（BadMac）。
        table.Record(0x51, 1, new SecOcVerdict(0x123, Accepted: false, Reason: RejectReason.BadMac));
        table.Record(0x51, 2, new SecOcVerdict(0x123, Accepted: false, Reason: RejectReason.BadMac));
        vm.AppendBatchCore(new[] { MakeFrame(0x123) });
        vm.AppendBatchCore(new[] { MakeFrame(0x123) });
        vm.Entries.Should().BeEmpty("暂停期间不产生 trace 行");

        // 恢复后第 3 帧：通道 seq=3 → Accepted。若 pause 不推进 joiner 计数，
        // 这里会取到 seq=1 的 Rejected（✗）——断言 Accepted 即锁定对齐。
        vm.IsPaused = false;
        table.Record(0x51, 3, new SecOcVerdict(0x123, Accepted: true, Reason: null));
        vm.AppendBatchCore(new[] { MakeFrame(0x123) });

        vm.Entries.Should().ContainSingle();
        vm.Entries[0].SecOcBadge.Kind.Should().Be(SecOcBadgeKind.Accepted);
        vm.Entries[0].SecOcBadge.Text.Should().Be("✓");
    }
}