using System.Globalization;
using System.Xml.Linq;
using PeakCan.Host.Core.Uds.Database;

namespace PeakCan.Host.Core.Uds.Odx;

/// <summary>
/// Extracts UDS DIDs and Routines from <c>REQUEST</c> elements in
/// flat ODX-D layouts (Vector CANdelaStudio .odx-d exports).
///
/// Real OEM ODX-D files (e.g. Demo_Cdd.odx-d, 38kWh BMS) do not use
/// <c>DOP-BASE</c> or <c>ECU-JOB</c> elements. Instead, every UDS
/// service is represented as a top-level <c>REQUEST</c> with
/// <c>&lt;PARAM SEMANTIC="SERVICE-ID"&gt;</c> + sibling
/// <c>&lt;PARAM SEMANTIC="ID"&gt;</c> (or <c>SEMANTIC="SUBFUNCTION"</c>).
///
/// This mapper walks <c>REQUEST</c> elements and produces
/// <see cref="DidDefinition"/> / <see cref="RoutineDefinition"/> in
/// the same shape as <see cref="DidDop"/> / <see cref="EcuJob"/> so
/// the rest of the pipeline (DBs, DtcPanel) doesn't care which path
/// produced a definition.
/// </summary>
public static class RequestBasedMappers
{
    /// <summary>UDS ReadDataByIdentifier service id.</summary>
    private const byte ServiceId_ReadDataByIdentifier = 0x22;
    /// <summary>UDS WriteDataByIdentifier service id.</summary>
    private const byte ServiceId_WriteDataByIdentifier = 0x2E;
    /// <summary>UDS RoutineControl service id.</summary>
    private const byte ServiceId_RoutineControl = 0x31;

    /// <summary>
    /// Extract DIDs from REQUEST elements with SERVICE-ID 0x22 (R) or
    /// 0x2E (R/W). Returns a dictionary with the Writable flag merged
    /// by OR across all REQUESTs referencing the same DID id.
    /// </summary>
    public static IReadOnlyDictionary<ushort, bool> ExtractDids(
        XDocument xdoc, XNamespace ns)
    {
        ArgumentNullException.ThrowIfNull(xdoc);
        var dids = new Dictionary<ushort, bool>();

        foreach (var req in xdoc.Descendants(ns + "REQUEST"))
        {
            var sid = ReadServiceId(req, ns);
            if (sid != ServiceId_ReadDataByIdentifier &&
                sid != ServiceId_WriteDataByIdentifier)
                continue;

            var id = ReadIdParam(req, ns);
            if (id is null) continue;

            var writable = sid == ServiceId_WriteDataByIdentifier;
            if (dids.TryGetValue(id.Value, out var existing))
                dids[id.Value] = existing || writable; // OR merge
            else
                dids[id.Value] = writable;
        }

        return dids;
    }

    /// <summary>
    /// Extract routines from REQUEST elements with SERVICE-ID 0x31.
    /// </summary>
    public static IReadOnlyList<RoutineDefinition> ExtractRoutines(
        XDocument xdoc, XNamespace ns)
    {
        ArgumentNullException.ThrowIfNull(xdoc);

        // First pass: collect REQUESTs that are 0x31 (RoutineControl)
        // with their id + subfunction. Keyed by REQUEST id attribute.
        var byRequestId = new Dictionary<string, (ushort Id, byte Sub)>();
        foreach (var req in xdoc.Descendants(ns + "REQUEST"))
        {
            var sid = ReadServiceId(req, ns);
            if (sid != ServiceId_RoutineControl) continue;
            var id = ReadIdParam(req, ns);
            if (id is null) continue;
            var reqId = (string?)req.Attribute("ID");
            if (reqId is null) continue;
            var sub = ReadSubfunctionParam(req, ns);
            byRequestId[reqId] = (id.Value, sub);
        }

        // Second pass: for each DIAG-SERVICE, find REQUEST-REF; if
        // pointed REQUEST is 0x31, this is a routine. (Sessions,
        // ReadDataByIdentifier, etc. have REQUEST-REF to other REQUEST
        // ids that are NOT in byRequestId and are correctly excluded.)
        var routines = new Dictionary<ushort, RoutineDefinition>();
        foreach (var svc in xdoc.Descendants(ns + "DIAG-SERVICE"))
        {
            var reqRefEl = svc.Element(ns + "REQUEST-REF");
            if (reqRefEl is null) continue;
            var reqRefId = (string?)reqRefEl.Attribute("ID-REF");
            if (reqRefId is null || !byRequestId.TryGetValue(reqRefId, out var info))
                continue;

            var shortName = (string?)svc.Attribute("SHORT-NAME") ?? string.Empty;
            var longName = (string?)svc.Element(ns + "LONG-NAME") ?? shortName;
            var startable = shortName.EndsWith("_Start", StringComparison.Ordinal)
                            || info.Sub == 0x01;
            var stoppable = shortName.EndsWith("_Stop", StringComparison.Ordinal)
                            || info.Sub == 0x02;
            var queryable = shortName.EndsWith("_Results", StringComparison.Ordinal)
                            || info.Sub == 0x03;
            var desc = queryable ? $"{longName} (QueryResults)"
                : (stoppable ? $"{longName} (Stop)" : $"{longName} (Start)");

            // Last-wins on dedup by id (prefer the most specific
            // semantic from suffix-detected Start/Stop).
            routines[info.Id] = new RoutineDefinition(
                Id: info.Id,
                Name: shortName,
                Description: desc,
                Startable: startable,
                Stoppable: stoppable);
        }

        return routines.Values.ToList();
    }

    private static byte? ReadServiceId(XElement req, XNamespace ns)
    {
        var p = req.Elements(ns + "PARAMS")
            .Elements(ns + "PARAM")
            .FirstOrDefault(x => (string?)x.Attribute("SEMANTIC") == "SERVICE-ID")
            ?? req.Descendants(ns + "PARAM")
                .FirstOrDefault(x => (string?)x.Attribute("SEMANTIC") == "SERVICE-ID");
        if (p is null) return null;
        return ParseByte(p);
    }

    private static ushort? ReadIdParam(XElement req, XNamespace ns)
    {
        var p = req.Descendants(ns + "PARAM")
            .FirstOrDefault(x => (string?)x.Attribute("SEMANTIC") == "ID");
        if (p is null) return null;
        var raw = ParseUInt(p);
        if (raw is null) return null;
        // DID is 2 bytes (UDS spec). Routine is 2 bytes (ISO 14229 §9.4).
        if (raw > 0xFFFF) return null;
        return (ushort)raw.Value;
    }

    private static byte ReadSubfunctionParam(XElement req, XNamespace ns)
    {
        var p = req.Descendants(ns + "PARAM")
            .FirstOrDefault(x => (string?)x.Attribute("SEMANTIC") == "SUBFUNCTION");
        return p is null ? (byte)0 : ParseByte(p) ?? (byte)0;
    }

    private static byte? ParseByte(XElement p)
    {
        var v = (string?)p.Element((p.Name.Namespace) + "CODED-VALUE")
            ?? (string?)p.Element(XName.Get("CODED-VALUE", p.Name.Namespace.NamespaceName));
        if (v is null) return null;
        if (byte.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
            return b;
        return null;
    }

    private static uint? ParseUInt(XElement p)
    {
        var v = (string?)p.Element((p.Name.Namespace) + "CODED-VALUE")
            ?? (string?)p.Element(XName.Get("CODED-VALUE", p.Name.Namespace.NamespaceName));
        if (v is null) return null;
        if (uint.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return n;
        return null;
    }
}
