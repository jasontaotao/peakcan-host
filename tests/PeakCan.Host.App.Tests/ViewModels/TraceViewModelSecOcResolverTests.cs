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
}