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
    /// Resolve the data length (in bytes) for each DID extracted
    /// from 0x22/0x2E REQUESTs by walking the POS-RESPONSE chain:
    ///   DID ← REQUEST ← DIAG-SERVICE ← POS-RESPONSE-REF ← POS-RESPONSE
    ///   POS-RESPONSE → PARAM SEMANTIC="DATA" → DOP-REF → DOP /
    ///   DATA-OBJECT-PROP → DIAG-CODED-TYPE → BIT-LENGTH
    /// Returns a dictionary keyed by DID id, value = total data bytes
    /// (sum of all SEMANTIC="DATA" PARAM DOPs in the largest matching
    /// POS-RESPONSE). DIDs without a resolvable chain are absent.
    /// </summary>
    public static IReadOnlyDictionary<ushort, int> ExtractDidLengths(
        XDocument xdoc, XNamespace ns)
    {
        ArgumentNullException.ThrowIfNull(xdoc);

        // 1. Index DOP-like elements (DATA-OBJECT-PROP, DOP) id → bit length.
        // DATA-OBJECT-PROP is the Vector CANdelaStudio form; DOP is the
        // canonical ODX 2.x form. Both may have DIAG-CODED-TYPE/BIT-LENGTH.
        var dopBitLengths = new Dictionary<string, int>();
        foreach (var el in xdoc.Descendants())
        {
            var localName = el.Name.LocalName;
            if (localName != "DATA-OBJECT-PROP" && localName != "DOP") continue;
            var id = (string?)el.Attribute("ID");
            if (id is null) continue;
            var bits = ReadBitLength(el, ns);
            if (bits is not null) dopBitLengths[id] = bits.Value;
        }

        // 2. Index REQUEST id → DID id (only 0x22 / 0x2E).
        var didByReqId = new Dictionary<string, ushort>();
        foreach (var req in xdoc.Descendants(ns + "REQUEST"))
        {
            var sid = ReadServiceId(req, ns);
            if (sid != ServiceId_ReadDataByIdentifier &&
                sid != ServiceId_WriteDataByIdentifier)
                continue;
            var id = ReadIdParam(req, ns);
            var reqId = (string?)req.Attribute("ID");
            if (id is not null && reqId is not null)
                didByReqId[reqId] = id.Value;
        }

        // 3. Index POS-RESPONSE id → element.
        var posById = new Dictionary<string, XElement>();
        foreach (var pos in xdoc.Descendants(ns + "POS-RESPONSE"))
        {
            var id = (string?)pos.Attribute("ID");
            if (id is not null) posById[id] = pos;
        }

        // 4. Walk DIAG-SERVICEs. For each, if its REQUEST-REF points to
        //    a 0x22/0x2E REQUEST, look at the matching POS-RESPONSE-REFs
        //    and sum the BIT-LENGTHs of SEMANTIC="DATA" PARAM DOPs.
        var result = new Dictionary<ushort, int>();
        foreach (var svc in xdoc.Descendants(ns + "DIAG-SERVICE"))
        {
            var reqRefEl = svc.Element(ns + "REQUEST-REF");
            if (reqRefEl is null) continue;
            var reqRefId = (string?)reqRefEl.Attribute("ID-REF");
            if (reqRefId is null || !didByReqId.TryGetValue(reqRefId, out var did))
                continue;

            foreach (var posRef in svc.Elements(ns + "POS-RESPONSE-REFS")
                                      .Elements(ns + "POS-RESPONSE-REF"))
            {
                var posId = (string?)posRef.Attribute("ID-REF");
                if (posId is null || !posById.TryGetValue(posId, out var pos))
                    continue;

                int totalBits = 0;
                int dataParams = 0;
                foreach (var param in pos.Descendants(ns + "PARAM"))
                {
                    if ((string?)param.Attribute("SEMANTIC") != "DATA") continue;
                    dataParams++;
                    var dopRef = param.Element(ns + "DOP-REF");
                    if (dopRef is not null)
                    {
                        var dopRefId = (string?)dopRef.Attribute("ID-REF");
                        if (dopRefId is not null &&
                            dopBitLengths.TryGetValue(dopRefId, out var bits))
                            totalBits += bits;
                    }
                    else
                    {
                        // Inline DIAG-CODED-TYPE
                        var bits = ReadBitLength(param, ns);
                        if (bits is not null) totalBits += bits.Value;
                    }
                }
                if (dataParams > 0 && totalBits > 0)
                {
                    var lengthBytes = (totalBits + 7) / 8;
                    if (!result.TryGetValue(did, out var prev) || lengthBytes > prev)
                        result[did] = lengthBytes;
                }
            }
        }

        return result;
    }

    private static int? ReadBitLength(XElement parent, XNamespace ns)
    {
        var dct = parent.Descendants(ns + "DIAG-CODED-TYPE").FirstOrDefault();
        if (dct is null) return null;
        var bit = dct.Element(ns + "BIT-LENGTH");
        if (bit is null) return null;
        if (int.TryParse(bit.Value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var n))
            return n;
        return null;
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

            // v2.0.7 PATCH Bug-4: SHORT-NAME is canonically an ATTRIBUTE
            // per ODX 2.x schema, but real-world Vector CANdelaStudio
            // .odx-d exports (verified against Demo_Cdd.odx-d: 95 of 95
            // DIAG-SERVICEs use the child-element form) emit it as a
            // CHILD ELEMENT instead. Attribute-only lookup produced
            // empty Name fields for the entire UDS Routines panel.
            // Try attribute first, then fall back to child element.
            // (XAttribute has an implicit string conversion; we don't
            // need `as string` which would always null-out.)
            var shortName = svc.Attribute("SHORT-NAME")?.Value
                ?? svc.Element(ns + "SHORT-NAME")?.Value
                ?? string.Empty;
            var longName = svc.Attribute("LONG-NAME")?.Value
                ?? svc.Element(ns + "LONG-NAME")?.Value
                ?? shortName;
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
