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
    /// v3.49.0 MINOR: T2.1 — 从 0x22 / 0x2E REQUEST 链提取每个 DID 的
    /// <see cref="DidField"/> 字段表（复合 DID 含多个字段）。
    ///
    /// 链路与 <see cref="ExtractDidLengths"/> 一致：
    ///   DID ← REQUEST ← DIAG-SERVICE ← POS-RESPONSE-REF ← POS-RESPONSE
    ///   POS-RESPONSE → PARAM SEMANTIC="DATA" → DOP-REF → DATA-OBJECT-PROP
    /// 不同点是每个 SEMANTIC="DATA" PARAM 都产出一条 <see cref="DidField"/>，
    /// 含：
    ///   - ByteOffset : PARAM 的 BYTE-POSITION（缺失则按字段顺序累计当前已用
    ///                  字节作回退）
    ///   - BitLength  : DOP 的 DIAG-CODED-TYPE/BIT-LENGTH（缺失 0）
    ///   - BaseType   : DOP 的 DIAG-CODED-TYPE/BASE-DATA-TYPE（缺失 Unknown）
    ///   - Compu/Unit : DOP 的 COMPU-METHOD / UNIT-REF 或内嵌 UNIT
    ///                  （通过 <see cref="CompuMethodParser"/> 复用）
    /// DOP 无 DATA-OBJECT-PROP 本体时跳过该字段（无法定位类型元数据）。
    /// 同一 DID 多个 POS-RESPONSE 时取字段最多的（与 ExtractDidLengths 取
    /// 最长字节一致语义）。
    /// </summary>
    public static IReadOnlyDictionary<ushort, IReadOnlyList<DidField>> ExtractDidFields(
        XDocument xdoc, XNamespace ns)
    {
        ArgumentNullException.ThrowIfNull(xdoc);

        // 1. Index DATA-OBJECT-PROP / DOP by ID (real .odx-d uses
        //    DATA-OBJECT-PROP; canonical ODX 2.x uses DOP).
        var dopById = new Dictionary<string, XElement>();
        foreach (var el in xdoc.Descendants())
        {
            var localName = el.Name.LocalName;
            if (localName != "DATA-OBJECT-PROP" && localName != "DOP") continue;
            var id = (string?)el.Attribute("ID");
            if (id is not null) dopById[id] = el;
        }

        // 2. Index UNIT by ID（用于 UNIT-REF 解析）。
        var unitById = new Dictionary<string, XElement>();
        foreach (var u in xdoc.Descendants(ns + "UNIT"))
        {
            var id = (string?)u.Attribute("ID");
            if (id is not null) unitById[id] = u;
        }

        // 3. Index REQUEST id → DID id（仅 0x22 / 0x2E）。
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

        // 4. Index POS-RESPONSE id → element。
        var posById = new Dictionary<string, XElement>();
        foreach (var pos in xdoc.Descendants(ns + "POS-RESPONSE"))
        {
            var id = (string?)pos.Attribute("ID");
            if (id is not null) posById[id] = pos;
        }

        // 5. Walk DIAG-SERVICEs; for each 0x22/0x2E REQUEST-REF, enumerate
        //    SEMANTIC=DATA PARAMs in the matching POS-RESPONSE → build
        //    DidField[] keyed by DID id. Take the largest field set when
        //    a DID has multiple POS-RESPONSEs (mirrors ExtractDidLengths).
        var result = new Dictionary<ushort, IReadOnlyList<DidField>>();
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

                var fields = BuildFieldsFromPosResponse(pos, ns, dopById, unitById);
                if (fields.Count == 0) continue;
                if (!result.TryGetValue(did, out var prev) || fields.Count > prev.Count)
                    result[did] = fields;
            }
        }

        return result;
    }

    /// <summary>
    /// 遍历 POS-RESPONSE 内的 SEMANTIC="DATA" PARAM，每个产出一条
    /// <see cref="DidField"/>。ByteOffset 优先取 PARAM 的 BYTE-POSITION，
    /// 缺失则按已遍历字段的累计字节作回退（保证按序递增）。
    /// </summary>
    private static IReadOnlyList<DidField> BuildFieldsFromPosResponse(
        XElement pos,
        XNamespace ns,
        IReadOnlyDictionary<string, XElement> dopById,
        IReadOnlyDictionary<string, XElement> unitById)
    {
        var fields = new List<DidField>();
        int fallbackOffset = 0;
        foreach (var param in pos.Descendants(ns + "PARAM"))
        {
            if ((string?)param.Attribute("SEMANTIC") != "DATA") continue;

            var dopRef = param.Element(ns + "DOP-REF");
            var dopRefId = (string?)dopRef?.Attribute("ID-REF");
            XElement? dopEl = null;
            if (dopRefId is not null)
                dopById.TryGetValue(dopRefId, out dopEl);

            // ByteOffset: PARAM BYTE-POSITION 优先；缺失用累计回退。
            var byteOffset = ReadBytePosition(param, ns) ?? fallbackOffset;

            DidField field;
            if (dopEl is not null)
            {
                var baseType = ParseBaseTypeFromDop(dopEl, ns);
                var bitLength = ReadBitLength(dopEl, ns) ?? 0;
                var fieldBitLength = bitLength;
                var compu = CompuMethodParser.TryParseCompu(dopEl, ns);
                var unit = CompuMethodParser.TryParseUnit(dopEl, ns, unitById);

                // PARAM SHORT-NAME 作字段名（比 DOP SHORT-NAME 更贴合字段语义）
                var name = (string?)param.Element(ns + "SHORT-NAME")
                           ?? (string?)dopEl.Element(ns + "SHORT-NAME")
                           ?? string.Empty;
                field = new DidField(name, fieldBitLength, byteOffset,
                    baseType, compu, unit);
            }
            else
            {
                // 无 DOP 本体（仅内嵌 DIAG-CODED-TYPE 或无类型）：
                // 仍记录字段占位，但 BaseType=Unknown，无 Compu/Unit。
                var name = (string?)param.Element(ns + "SHORT-NAME") ?? string.Empty;
                var inlineBits = ReadBitLength(param, ns) ?? 0;
                field = new DidField(name, inlineBits, byteOffset,
                    DidBaseType.Unknown, null, null);
            }

            fields.Add(field);
            // 回退偏移推进：当前字段 byte 长度（>",=8）；若 BIT-LENGTH=0
            // 则推进 1 字节作最保守递增，避免偏移塌缩。
            var advance = field.BitLength > 0 ? (field.BitLength + 7) / 8 : 1;
            fallbackOffset = byteOffset + advance;
        }
        return fields;
    }

    /// <summary>读 PARAM 的 <c>BYTE-POSITION</c> 子元素（十进制）。</summary>
    private static int? ReadBytePosition(XElement param, XNamespace ns)
    {
        var bp = param.Element(ns + "BYTE-POSITION");
        if (bp is null) return null;
        if (int.TryParse(bp.Value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var n))
            return n;
        return null;
    }

    /// <summary>
    /// 从 DOP/DATA-OBJECT-PROP 元素的 DIAG-CODED-TYPE 读 BASE-DATA-TYPE
    /// → <see cref="DidBaseType"/>（和 DidDop.ParseBaseType 同语义，但
    /// DOP 本体路径找 DIAG-CODED-TYPE 子元素）。
    /// </summary>
    private static DidBaseType ParseBaseTypeFromDop(XElement dopEl, XNamespace ns)
    {
        var dct = dopEl.Element(ns + "DIAG-CODED-TYPE");
        if (dct is null) return DidBaseType.Unknown;
        var raw = (string?)dct.Attribute("BASE-DATA-TYPE")
                  ?? (string?)dct.Attribute("BASE-TYPE");
        return raw switch
        {
            "A_UINT32"          => DidBaseType.UInt32,
            "A_INT32"           => DidBaseType.Int32,
            "A_FLOAT64"         => DidBaseType.Float64,
            "A_ASCIISTRING"     => DidBaseType.AsciiString,
            "A_UNICODE2STRING"  => DidBaseType.Unicode2String,
            "A_BYTEFIELD"       => DidBaseType.ByteField,
            _                   => DidBaseType.Unknown,
        };
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
