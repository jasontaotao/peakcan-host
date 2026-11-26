using PeakCan.HIL.Core.Uds.Odx;

namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>
/// Fifth stage of the offline pipeline: turn a paired transaction into the
/// human-readable line the report shows. Every fallback carries an explicit
/// <see cref="UdsDegradeReason"/> — the interpreter never silently returns an
/// empty cell for a definition it could not find.
/// </summary>
public sealed class UdsServiceInterpreter
{
    private static readonly IReadOnlyDictionary<byte, string> Services = new Dictionary<byte, string>
    {
        [0x10] = "DiagnosticSessionControl",
        [0x11] = "ECUReset",
        [0x14] = "ClearDiagnosticInformation",
        [0x19] = "ReadDTCInformation",
        [0x22] = "ReadDataByIdentifier",
        [0x27] = "SecurityAccess",
        [0x28] = "CommunicationControl",
        [0x2E] = "WriteDataByIdentifier",
        [0x31] = "RoutineControl",
        [0x34] = "RequestDownload",
        [0x36] = "TransferData",
        [0x37] = "RequestTransferExit",
        [0x3E] = "TesterPresent",
        [0x7F] = "NegativeResponse",
    };

    public UdsInterpretation Interpret(UdsOfflineTransaction transaction, UdsOfflineDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var req = transaction.Request.Payload;
        var sid = req.Length == 0 ? (byte)0 : req[0];
        var display = Services.TryGetValue(sid, out var name) ? name : $"SID 0x{sid:X2}";

        if (transaction.FinalKind == UdsFinalKind.Negative)
            return new(display, null, null, null, null,
                transaction.FinalNrc is { } nrc ? $"NRC 0x{nrc:X2}" : null);

        if (sid == 0x22 && transaction.FinalKind == UdsFinalKind.Positive && transaction.Responses.Count > 0)
            return InterpretDid(transaction, diagnostics, display);

        if (sid == 0x31 && transaction.FinalKind == UdsFinalKind.Positive && req.Length >= 4)
        {
            var routineId = (ushort)((req[2] << 8) | req[3]);
            var routine = diagnostics.Routines.FirstOrDefault(r => r.Id == routineId);
            return new(display, null, routine?.Name, null, null,
                routine is null ? $"例程 0x{routineId:X4} 无定义" : null);
        }

        return new(display, null, null, null, null, null);
    }

    private static UdsInterpretation InterpretDid(
        UdsOfflineTransaction transaction, UdsOfflineDiagnostics diagnostics, string display)
    {
        var req = transaction.Request.Payload;
        if (req.Length < 3)
            return new(display, null, null, null, UdsDegradeReason.PayloadTooShort, "请求缺 DID");

        var did = (ushort)((req[1] << 8) | req[2]);
        var res = transaction.Responses[^1].Payload;
        if (res.Length < 3)
            return new(display, null, null, null, UdsDegradeReason.PayloadTooShort, "响应缺 DID");
        if (((res[1] << 8) | res[2]) != did)
            return new(display, null, null, null, UdsDegradeReason.IncompleteData, "响应 DID 回显不一致");

        var def = diagnostics.Dids.FirstOrDefault(d => d.Id == did);
        if (def is null)
        {
            var reason = diagnostics.OdxLoaded
                ? UdsDegradeReason.OdxMissingDefinition
                : UdsDegradeReason.NoOdx;
            return new(display, null, null, null, reason, $"DID 0x{did:X4} 无定义");
        }
        if (res.Length - 3 < def.LengthBytes)
            return new(display, def.Name, null, null, UdsDegradeReason.PayloadTooShort,
                $"数据 {res.Length - 3} 字节 < 定义 {def.LengthBytes} 字节");
        if (def.Fields.Count == 0)
            return new(display, def.Name, null, null, UdsDegradeReason.NoFieldTable, "定义没有字段表");

        var decoded = DidValueDecoder.Decode(res[3..], def.Fields);
        var text = string.Join("; ", decoded.Select(d =>
            string.IsNullOrEmpty(d.Unit) ? $"{d.Name}={d.PhysicalValue}" : $"{d.Name}={d.PhysicalValue} {d.Unit}"));
        return new(display, def.Name, null, text, null, null);
    }
}
