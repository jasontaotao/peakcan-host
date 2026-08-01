using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels.TestSuiteBuilder;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Tests.ViewModels.TestSuiteBuilder;

public class SendFrameComposerViewModelTests
{
    private static DbcService SvcWithMsgs()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var doc = new DbcDocument("", new List<Node>(),
            new List<Message>
            {
                new(0x100, "M1", 2, "ECU1",
                    new List<Signal>
                    {
                        new("Speed", 0, 16, ByteOrder.LittleEndian, PeakCan.Host.Core.Dbc.ValueType.Unsigned, 1, 0, 0, 6553.5, "", Array.Empty<string>()),
                    },
                    IsMultiplexed: false, MultiplexorSignalIndex: null),
            },
            new Dictionary<uint, Message>(), new Dictionary<string, ValueTable>());
        svc.SetCurrentForTests(doc);
        return svc;
    }

    [Fact]
    public void ComposeHex_Encodes_Signal_Value_Into_Bytes()
    {
        var svc = SvcWithMsgs();
        var vm = new SendFrameComposerViewModel(svc, new DbcEncodeService(), NullLogger<SendFrameComposerViewModel>.Instance);
        vm.SelectedMessage = vm.DbcMessages[0];
        vm.SetSignalValue("Speed", 513.0);

        vm.ComposeHex().Should().Be("0102"); // 16-bit LE: 513 = 0x0201
    }

    [Fact]
    public void DbcLoaded_Without_Selection_Composes_Empty()
    {
        var vm = new SendFrameComposerViewModel(
            new DbcService(NullLogger<DbcService>.Instance), new DbcEncodeService(),
            NullLogger<SendFrameComposerViewModel>.Instance);
        vm.ComposeHex().Should().BeEmpty();
    }
}
