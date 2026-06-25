using System.Collections.ObjectModel;
using FluentAssertions;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.App.ViewModels.Uds.Rows;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.IsoTp;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Uds;

/// <summary>
/// Tests for DtcPanelViewModel. Covers 4-byte DTC chunk parsing
/// (3-byte code + 1-byte status), empty-response clearing, NRC
/// leaving pre-existing DTCs intact, and Clear invoking
/// ClearDiagnosticInformation + emptying the collection.
/// </summary>
public sealed class DtcPanelViewModelTests
{
    private sealed class RecordingUdsClient : UdsClient
    {
        public byte[] NextReadResult { get; set; } = Array.Empty<byte>();
        public bool ClearCalled { get; set; }
        public bool ThrowNrc { get; set; }

        public RecordingUdsClient() : base(
            new IsoTpLayer(new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 }, _ => { }),
            new UdsTimer())
        { }

        public override Task<byte[]> ReadDtcInformationAsync(byte subFunction, byte statusMask, CancellationToken ct = default)
        {
            if (ThrowNrc) throw new UdsNegativeResponseException(0x19, UdsNegativeResponseCode.RequestOutOfRange);
            return Task.FromResult(NextReadResult);
        }

        public override Task ClearDiagnosticInformationAsync(uint groupOfDtc = 0xFFFFFF, CancellationToken ct = default)
        {
            ClearCalled = true;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ReadDtcsCommand_Parses_4Byte_Chunks_Into_DtcRows()
    {
        // Two DTCs: 0x0100AB (Chassis) and 0x0200CD (Body), with status bytes 0x08 and 0x04.
        var fake = new RecordingUdsClient
        {
            NextReadResult = new byte[] { 0x01, 0x00, 0xAB, 0x08, 0x02, 0x00, 0xCD, 0x04 }
        };
        var vm = new DtcPanelViewModel(fake);

        await vm.ReadDtcsCommand.ExecuteAsync(null);

        vm.Dtcs.Should().HaveCount(2);
        vm.Dtcs[0].Code.Should().Be(0x0100ABu);
        vm.Dtcs[0].Status.Should().Be((byte)0x08);
        vm.Dtcs[0].Description.Should().Be("Chassis"); // 0x010000..0x01FFFF
        vm.Dtcs[1].Code.Should().Be(0x0200CDu);
        vm.Dtcs[1].Status.Should().Be((byte)0x04);
        vm.Dtcs[1].Description.Should().Be("Body"); // 0x020000..0x02FFFF
    }

    [Fact]
    public async Task ReadDtcsCommand_With_Empty_Response_Clears_Dtcs()
    {
        var fake = new RecordingUdsClient { NextReadResult = Array.Empty<byte>() };
        var vm = new DtcPanelViewModel(fake);
        // Pre-populate
        vm.Dtcs.Add(new DtcRow { Code = 0x123456, Status = 0x01, Description = "old" });

        await vm.ReadDtcsCommand.ExecuteAsync(null);

        vm.Dtcs.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadDtcsCommand_With_UdsNegativeResponse_Logs_Warn_And_Leaves_Dtcs_Unchanged()
    {
        var fake = new RecordingUdsClient { ThrowNrc = true };
        var vm = new DtcPanelViewModel(fake);
        var log = new ObservableCollection<UdsLogLine>();
        vm.AttachLog(log);
        var preExisting = new DtcRow { Code = 0x999999, Status = 0x01, Description = "pre" };
        vm.Dtcs.Add(preExisting);

        await vm.ReadDtcsCommand.ExecuteAsync(null);

        log.Should().Contain(l => l.Level == "Warn" && l.Message.Contains("NRC"));
        vm.Dtcs.Should().ContainSingle().Which.Should().Be(preExisting);
    }

    [Fact]
    public async Task ClearDtcsCommand_Invokes_ClearDiagnosticInformation_And_Clears_Collection()
    {
        var fake = new RecordingUdsClient();
        var vm = new DtcPanelViewModel(fake);
        vm.Dtcs.Add(new DtcRow { Code = 0x123456, Status = 0x01, Description = "x" });
        var log = new ObservableCollection<UdsLogLine>();
        vm.AttachLog(log);

        await vm.ClearDtcsCommand.ExecuteAsync(null);

        fake.ClearCalled.Should().BeTrue();
        vm.Dtcs.Should().BeEmpty();
        log.Should().Contain(l => l.Level == "Info" && l.Message.Contains("cleared", StringComparison.OrdinalIgnoreCase));
    }
}
