using System.Globalization;
using FluentAssertions;
using PeakCan.HIL.Core.J1939;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.J1939;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// <see cref="TraceViewModel.J1939SessionEvents"/> 集合语义：追加、封顶截断、
/// 与 <see cref="TraceViewModel.Clear"/> 联动清空。
/// </summary>
public class TraceViewModelJ1939SessionTests
{
    private static J1939SessionEvent Event(string detail = "detail") =>
        new(SessionEventKind.PacketLoss, 0xF4, 0xFF, 0x000200, TpMode.Bam, detail);

    [Fact]
    public void Add_Appends_Row_With_Fields()
    {
        var vm = new TraceViewModel();

        vm.AddJ1939SessionEvent(Event());

        var row = vm.J1939SessionEvents.Should().ContainSingle().Subject;
        row.Kind.Should().Be(SessionEventKind.PacketLoss);
        row.Sa.Should().Be(0xF4);
        row.Da.Should().Be(0xFF);
        row.Pgn.Should().Be(0x000200);
        row.Mode.Should().Be(TpMode.Bam);
        row.Detail.Should().Be("detail");
    }

    [Fact]
    public void Add_Trims_Oldest_Beyond_Cap()
    {
        var vm = new TraceViewModel();

        for (int i = 0; i <= TraceViewModel.J1939SessionEventsCap; i++)
            vm.AddJ1939SessionEvent(Event(detail: i.ToString(CultureInfo.InvariantCulture)));

        vm.J1939SessionEvents.Should().HaveCount(TraceViewModel.J1939SessionEventsCap);
        vm.J1939SessionEvents[0].Detail.Should().Be("1");            // 最旧 0 被裁
        vm.J1939SessionEvents[^1].Detail.Should()
            .Be(TraceViewModel.J1939SessionEventsCap.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Clear_Empties_Session_Events()
    {
        var vm = new TraceViewModel();
        vm.AddJ1939SessionEvent(Event());

        vm.ClearCommand.Execute(null);

        vm.J1939SessionEvents.Should().BeEmpty();
    }
}
