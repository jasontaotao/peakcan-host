using System.Collections.ObjectModel;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.App.ViewModels.Uds.Rows;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.Database;
using PeakCan.Host.Core.Uds.IsoTp;
using PeakCan.Host.Core.Uds.Odx;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Uds;

/// <summary>
/// Tests for DidPanelViewModel. Covers ctor population from
/// DidDatabase.All, first-row selection, Read success / NRC / busy
/// flag lifecycle, and Write hex-parse success / FormatException path.
/// </summary>
public sealed class DidPanelViewModelTests
{
    private sealed class RecordingUdsClient : UdsClient
    {
        public Dictionary<ushort, byte[]> ReadsByDid { get; } = new();
        public List<(ushort Did, byte[] Data)> Writes { get; } = new();
        public bool ThrowNrcOnRead { get; set; }

        public RecordingUdsClient() : base(
            new IsoTpLayer(new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 }, _ => { }),
            new UdsTimer())
        { }

        public override Task<byte[]> ReadDataByIdentifierAsync(ushort did, CancellationToken ct = default)
        {
            if (ThrowNrcOnRead)
                throw new UdsNegativeResponseException(0x22, UdsNegativeResponseCode.RequestOutOfRange);
            return Task.FromResult(ReadsByDid.TryGetValue(did, out var v) ? v : new byte[] { 0xAA, 0xBB });
        }

        public override Task WriteDataByIdentifierAsync(ushort did, byte[] data, CancellationToken ct = default)
        {
            Writes.Add((did, data));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Ctor_Populates_Dids_From_DidDatabase_All()
    {
        var fake = new RecordingUdsClient();
        var db = new DidDatabase(userJsonPath: null, logger: NullLogger<DidDatabase>.Instance);

        var vm = new DidPanelViewModel(fake, db);

        vm.Dids.Should().HaveCount(5);
        vm.Dids[0].Id.Should().Be((ushort)0xF190);
    }

    [Fact]
    public void Ctor_Selects_First_Did_As_SelectedDid()
    {
        var fake = new RecordingUdsClient();
        var db = new DidDatabase(userJsonPath: null, logger: NullLogger<DidDatabase>.Instance);

        var vm = new DidPanelViewModel(fake, db);

        vm.SelectedDid.Should().NotBeNull();
        vm.SelectedDid!.Id.Should().Be((ushort)0xF190);
    }

    [Fact]
    public async Task ReadDidCommand_Populates_SelectedDid_ReadValue_And_LastResult()
    {
        var fake = new RecordingUdsClient
        {
            ReadsByDid = { [0xF190] = new byte[] { 0x31, 0x32, 0x33 } }
        };
        var db = new DidDatabase(userJsonPath: null, logger: NullLogger<DidDatabase>.Instance);
        var vm = new DidPanelViewModel(fake, db);
        var log = new ObservableCollection<UdsLogLine>();
        vm.AttachLog(log);

        await vm.ReadDidCommand.ExecuteAsync(null);

        vm.SelectedDid!.ReadValue.Should().Be("31 32 33");
        vm.LastResult.Should().Be("31 32 33");
        log.Should().Contain(l => l.Level == "Info" && l.Message.Contains("0xF190", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReadDidCommand_With_UdsNegativeResponse_Logs_Warn_And_Clears_IsReading()
    {
        var fake = new RecordingUdsClient { ThrowNrcOnRead = true };
        var db = new DidDatabase(userJsonPath: null, logger: NullLogger<DidDatabase>.Instance);
        var vm = new DidPanelViewModel(fake, db);
        var log = new ObservableCollection<UdsLogLine>();
        vm.AttachLog(log);

        await vm.ReadDidCommand.ExecuteAsync(null);

        log.Should().Contain(l => l.Level == "Warn" && l.Message.Contains("NRC", StringComparison.OrdinalIgnoreCase));
        vm.SelectedDid!.IsReading.Should().BeFalse();
    }

    [Fact]
    public async Task ReadDidCommand_Sets_IsReading_True_During_Execution()
    {
        var fake = new RecordingUdsClient();
        var db = new DidDatabase(userJsonPath: null, logger: NullLogger<DidDatabase>.Instance);
        var vm = new DidPanelViewModel(fake, db);
        var observed = new List<bool>();
        vm.SelectedDid!.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DidRow.IsReading))
                observed.Add(vm.SelectedDid.IsReading);
        };

        await vm.ReadDidCommand.ExecuteAsync(null);

        observed.Should().Contain(true, "IsReading must be true during the command");
        observed.Should().Contain(false, "IsReading must be reset to false after completion");
    }

    [Fact]
    public async Task WriteDidCommand_Validates_Hex_Input_And_Invokes_WriteDataByIdentifier()
    {
        var fake = new RecordingUdsClient();
        var db = new DidDatabase(userJsonPath: null, logger: NullLogger<DidDatabase>.Instance);
        var vm = new DidPanelViewModel(fake, db);
        vm.WriteValue = "DE AD BE EF";

        await vm.WriteDidCommand.ExecuteAsync(null);

        fake.Writes.Should().ContainSingle(w => w.Did == 0xF190);
        fake.Writes[0].Data.Should().Equal(0xDE, 0xAD, 0xBE, 0xEF);
    }

    [Fact]
    public async Task WriteDidCommand_With_Invalid_Hex_Logs_FormatException_Without_Crash()
    {
        var fake = new RecordingUdsClient();
        var db = new DidDatabase(userJsonPath: null, logger: NullLogger<DidDatabase>.Instance);
        var vm = new DidPanelViewModel(fake, db);
        var log = new ObservableCollection<UdsLogLine>();
        vm.AttachLog(log);
        vm.WriteValue = "ZZ";

        await vm.WriteDidCommand.ExecuteAsync(null);

        log.Should().Contain(l => l.Level == "Error" && l.Message.Contains("invalid hex", StringComparison.OrdinalIgnoreCase));
        fake.Writes.Should().BeEmpty();
    }

    // v3.49.0 MINOR T4.3 — ReadDid 命中后若该 DID 带 ODX 字段类型表,
    // 应用 DidValueDecoder 解码为 DecodedField,填 row.DecodedFields,
    // LastResult / DecodedSummary 展示物理值而非纯 hex。
    [Fact]
    public async Task ReadDidCommand_AsciiField_DecodesToStringValue()
    {
        var fake = new RecordingUdsClient
        {
            ReadsByDid = { [0xF190] = Encoding.ASCII.GetBytes("1HGCM82633A123456") }
        };
        var db = new DidDatabase(userJsonPath: null, logger: NullLogger<DidDatabase>.Instance);
        // 覆盖 VIN 为带 ASCII[17B] 字段表
        db.AddRange(new[]
        {
            new DidDefinition(Id: 0xF190, Name: "VIN", Description: "VIN",
                LengthBytes: 17, Writable: false)
            with { Fields = new[]
            {
                new DidField("VIN", 17 * 8, 0, DidBaseType.AsciiString, null, null),
            } },
        }, out _);
        var vm = new DidPanelViewModel(fake, db);
        var log = new ObservableCollection<UdsLogLine>();
        vm.AttachLog(log);
        vm.SelectedDid = vm.Dids.Single(d => d.Id == 0xF190);

        await vm.ReadDidCommand.ExecuteAsync(null);

        vm.SelectedDid!.DecodedFields.Should().ContainSingle();
        vm.SelectedDid.DecodedFields[0].PhysicalValue.Should().Be("1HGCM82633A123456");
        vm.LastResult.Should().Be("1HGCM82633A123456",
            "LastResult 在有字段时显示解码物理值, 不再是 hex");
    }

    [Fact]
    public async Task ReadDidCommand_NoFields_FallsBackToHex_NoDecode()
    {
        var fake = new RecordingUdsClient
        {
            ReadsByDid = { [0xF190] = new byte[] { 0x31, 0x32, 0x33 } }
        };
        // 内置 VIN 无 Fields → 不解码,维持既有 hex 行为。
        var db = new DidDatabase(userJsonPath: null, logger: NullLogger<DidDatabase>.Instance);
        var vm = new DidPanelViewModel(fake, db);
        vm.AttachLog(new ObservableCollection<UdsLogLine>());

        await vm.ReadDidCommand.ExecuteAsync(null);

        vm.SelectedDid!.DecodedFields.Should().BeEmpty("no field table → no decode");
        vm.SelectedDid.ReadValue.Should().Be("31 32 33");
        vm.LastResult.Should().Be("31 32 33");
    }
}
