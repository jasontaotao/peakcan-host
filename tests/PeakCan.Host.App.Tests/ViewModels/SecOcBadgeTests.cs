using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.Channel.SecOc;
using PeakCan.Security.SecOc;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// M2.4b（spec §5-D6.7 三态显式化）：trace SecOC 徽标 join + 断开清理钩子。
/// 徽标三态：join 命中 ✓/✗、未保护、离线不验（缺省，禁止"无标注"）。
/// 清理钩子：ChannelConnectionCoordinator.DisconnectAllAsync 清空旁路表。
/// </summary>
public class SecOcBadgeTests
{
    private static CanFrame MakeFrame(uint id = 0x123, ushort channelHandle = 0x51)
        => new CanFrame(
            new CanId(id, FrameFormat.Standard),
            new byte[] { 1, 2, 3, 4 }, FrameFlags.None,
            new ChannelId(channelHandle),
            Timestamp.FromMicroseconds(1_000_000UL));

    [Fact]
    public void AppendBatchCore_WithoutResolver_Badge_Is_Offline()
    {
        var vm = new TraceViewModel();
        vm.AppendBatchCore(new[] { MakeFrame() });
        vm.Entries.Should().ContainSingle();
        vm.Entries[0].SecOcBadge.Should().Be(SecOcBadge.Offline);
        vm.Entries[0].SecOcBadge.Text.Should().Be("离线不验");
    }

    [Fact]
    public void AppendBatchCore_ResolverJoin_Accepted_Shows_Checkmark()
    {
        var vm = new TraceViewModel
        {
            SecOcBadgeResolver = f => f.Id.Raw == 0x123 ? SecOcBadge.Accepted : SecOcBadge.Unprotected,
        };
        vm.AppendBatchCore(new[] { MakeFrame(id: 0x123), MakeFrame(id: 0x456) });
        vm.Entries[0].SecOcBadge.Should().Be(SecOcBadge.Accepted);
        vm.Entries[0].SecOcBadge.Text.Should().Be("✓");
        vm.Entries[1].SecOcBadge.Should().Be(SecOcBadge.Unprotected);
        vm.Entries[1].SecOcBadge.Text.Should().Be("未保护");
    }

    [Fact]
    public void AppendBatchCore_ResolverJoin_Rejected_Shows_Reason()
    {
        var vm = new TraceViewModel
        {
            SecOcBadgeResolver = _ => SecOcBadge.Rejected("BadMac"),
        };
        vm.AppendBatchCore(new[] { MakeFrame() });
        vm.Entries[0].SecOcBadge.Kind.Should().Be(SecOcBadgeKind.Rejected);
        vm.Entries[0].SecOcBadge.Text.Should().Be("✗ BadMac");
    }

    [Fact]
    public void TraceEntry_SecOcBadge_Raises_PropertyChanged_Only_On_Change()
    {
        var entry = new TraceEntry
        {
            Timestamp = Timestamp.FromMicroseconds(1),
            Channel = new ChannelId(0x51),
            Id = new CanId(0x123, FrameFormat.Standard),
        };
        int fired = 0;
        entry.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TraceEntry.SecOcBadge)) fired++; };

        entry.SecOcBadge = SecOcBadge.Accepted;
        entry.SecOcBadge = SecOcBadge.Accepted; // 同值不触发
        entry.SecOcBadge = SecOcBadge.Rejected("FvRollback");

        fired.Should().Be(2);
        entry.SecOcBadge.Text.Should().Be("✗ FvRollback");
    }

    [Fact]
    public async Task DisconnectAllAsync_Clears_SecOcVerdictTable()
    {
        var verdicts = new SecOcVerdictTable();
        verdicts.Record(0x51, 1, new SecOcVerdict(0x123, Accepted: false, RejectReason.BadMac));
        verdicts.Count.Should().BeGreaterThan(0);

        var coordinator = new ChannelConnectionCoordinator(
            Substitute.For<PeakCan.Host.Core.IChannelFactory>(),
            new ChannelRouter(NullLogger<ChannelRouter>.Instance),
            new SendService(NullLogger<SendService>.Instance),
            secOcVerdicts: verdicts);

        await coordinator.DisconnectAllAsync();

        verdicts.Count.Should().Be(0);
    }
}
