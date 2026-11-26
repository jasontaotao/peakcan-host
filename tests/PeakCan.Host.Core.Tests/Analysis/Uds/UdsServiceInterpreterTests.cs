using FluentAssertions;
using PeakCan.HIL.Core.Uds.Database;
using PeakCan.HIL.Core.Uds.Odx;
using PeakCan.Host.Core.Analysis.Uds;
using PeakCan.Host.Core.Uds.Database;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Uds;

public sealed class UdsServiceInterpreterTests
{
    private static UdsOfflinePdu Pdu(UdsDirectionRole role, double ts, params byte[] payload)
        => new(role, payload, ts, ts, (int)(ts * 1000), (int)(ts * 1000), new[] { (int)(ts * 1000) }, false, true, Array.Empty<string>());

    private static UdsOfflineTransaction ReadDid(ushort did, params byte[] data)
    {
        var pdus = new[]
        {
            Pdu(UdsDirectionRole.Request, 0.0, 0x22, (byte)(did >> 8), (byte)did),
            Pdu(UdsDirectionRole.Response, 0.01, new[] { (byte)0x62, (byte)(did >> 8), (byte)did }.Concat(data).ToArray()),
        };
        return new UdsTransactionBuilder().Build(pdus).Transactions.Single();
    }

    private static UdsOfflineTransaction RequestOnly(byte sid)
    {
        var pdus = new[] { Pdu(UdsDirectionRole.Request, 0.0, sid) };
        return new UdsTransactionBuilder().Build(pdus).Transactions.Single();
    }

    private static UdsOfflineDiagnostics Diag(
        DidDefinition[]? dids = null, bool odx = false)
        => new(
            dids ?? Array.Empty<DidDefinition>(),
            Array.Empty<RoutineDefinition>(),
            OdxLoaded: odx,
            odx ? UserJsonLoadStatus.Missing : UserJsonLoadStatus.NotConfigured,
            UserJsonLoadStatus.NotConfigured,
            "snap");

    [Fact]
    public void No_odx_and_no_definition_reports_NoOdx()
    {
        var i = new UdsServiceInterpreter().Interpret(ReadDid(0x1234, 0x01), Diag());
        i.DegradeReason.Should().Be(UdsDegradeReason.NoOdx);
        i.DecodedValue.Should().BeNull("原始值由报告层保留");
    }

    [Fact]
    public void Builtin_did_without_fields_reports_NoFieldTable()
    {
        var vin = new DidDefinition(0xF190, "VIN", "Vehicle Identification Number", 17, false);
        var i = new UdsServiceInterpreter().Interpret(
            ReadDid(0xF190, Enumerable.Repeat((byte)'A', 17).ToArray()),
            Diag(new[] { vin }));

        i.DegradeReason.Should().Be(UdsDegradeReason.NoFieldTable);
        i.DidName.Should().Be("VIN");
    }

    [Fact]
    public void Odx_loaded_but_missing_did_reports_OdxMissingDefinition()
    {
        var i = new UdsServiceInterpreter().Interpret(ReadDid(0x1234, 0x01), Diag(odx: true));
        i.DegradeReason.Should().Be(UdsDegradeReason.OdxMissingDefinition);
    }

    [Fact]
    public void Unknown_sid_falls_back_to_hex_without_throwing()
    {
        var i = new UdsServiceInterpreter().Interpret(RequestOnly(0xF0), Diag());
        i.ServiceDisplay.Should().Be("SID 0xF0");
    }

    [Fact]
    public void Did_with_field_table_decodes_physical_text()
    {
        var field = new DidField("VIN", BitLength: 136, ByteOffset: 0,
            BaseType: DidBaseType.AsciiString, Compu: null, Unit: null);
        var vin = new DidDefinition(0xF190, "VIN", "Vehicle Identification Number", 17, false)
        {
            Fields = new[] { field },
        };

        var i = new UdsServiceInterpreter().Interpret(
            ReadDid(0xF190, Enumerable.Repeat((byte)'A', 17).ToArray()),
            Diag(new[] { vin }));

        i.DegradeReason.Should().BeNull();
        i.DidName.Should().Be("VIN");
        i.DecodedValue.Should().Be("VIN=AAAAAAAAAAAAAAAAA");
    }

    [Fact]
    public void Short_did_response_reports_PayloadTooShort()
    {
        var vin = new DidDefinition(0xF190, "VIN", "Vehicle Identification Number", 17, false);
        var i = new UdsServiceInterpreter().Interpret(ReadDid(0xF190, 0x01, 0x02), Diag(new[] { vin }));

        i.DegradeReason.Should().Be(UdsDegradeReason.PayloadTooShort);
        i.DidName.Should().Be("VIN");
    }
}
