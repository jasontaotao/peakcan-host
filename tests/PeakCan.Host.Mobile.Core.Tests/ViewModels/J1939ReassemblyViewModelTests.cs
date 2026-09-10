using FluentAssertions;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Mobile.Core.Services;
using PeakCan.Host.Mobile.Core.Tests.Fakes;
using PeakCan.Host.Mobile.Core.ViewModels;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.ViewModels;

public class J1939ReassemblyViewModelTests
{
    [Fact]
    public void Attach_ReassembledEvent_AppendsRowOnUiThread()
    {
        var ui = new FakeUiDispatcher();
        var vm = new J1939ReassemblyViewModel(ui);
        var reassembler = new StreamingJ1939Reassembler();
        vm.Attach(reassembler);

        reassembler.Ingest(CmFrame());
        reassembler.Ingest(DtFrame(1));
        reassembler.Ingest(DtFrame(2));

        vm.Rows.Should().HaveCount(1);
        vm.Rows[0].StatusText.Should().Be("完成");
    }

    [Fact]
    public void Clear_EmptiesRows()
    {
        var ui = new FakeUiDispatcher();
        var vm = new J1939ReassemblyViewModel(ui);
        var reassembler = new StreamingJ1939Reassembler();
        vm.Attach(reassembler);
        reassembler.Ingest(CmFrame());
        reassembler.Ingest(DtFrame(1));
        reassembler.Ingest(DtFrame(2));

        vm.Clear();

        vm.Rows.Should().BeEmpty();
    }

    private static ReplayFrame CmFrame()
    {
        var pgn = 0x000200u;
        var data = new byte[14];
        return new ReplayFrame(0.0, PeakCan.Host.Core.J1939.J1939Id.Compose(6, 0x00EC00, 0xF4, 0xFF), 8,
            PeakCan.Host.Core.J1939.TpCmMessage.Bam(14, 2, pgn).Encode(), default, true);
    }

    private static ReplayFrame DtFrame(byte seq)
    {
        var chunk = seq == 1 ? Enumerable.Range(0, 7).Select(i => (byte)(i + 1)).ToArray()
                             : Enumerable.Range(7, 7).Select(i => (byte)(i + 1)).ToArray();
        return new ReplayFrame(0.01 * seq, PeakCan.Host.Core.J1939.J1939Id.Compose(6, 0x00EB00, 0xF4, 0xFF), 8,
            new PeakCan.Host.Core.J1939.TpDtMessage(seq, chunk).Encode(), default, true);
    }
}