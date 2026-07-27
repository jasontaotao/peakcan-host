using System.Globalization;
using System.Xml.Linq;

namespace PeakCan.Host.Core.Uds.Odx;

/// <summary>
/// Extracts 0x27 SecurityAccess parameters from an ODX/ODX-D document.
/// Pure function (XDocument → SecurityAccessConfig?), no dependencies.
/// </summary>
public static class SecurityAccessExtractor
{
    /// <summary>UDS SecurityAccess service id.</summary>
    private const byte ServiceId_SecurityAccess = 0x27;

    /// <summary>
    /// Extract the SecurityAccess config from an ODX document.
    /// Returns null if the document contains no 0x27 service definition.
    /// </summary>
    /// <param name="xdoc">The ODX document.</param>
    /// <param name="ns">The XML namespace (already resolved by the caller).</param>
    /// <returns>SecurityAccessConfig or null.</returns>
    public static SecurityAccessConfig? Extract(XDocument xdoc, XNamespace ns)
    {
        ArgumentNullException.ThrowIfNull(xdoc);

        byte? level = null;
        int? seedLength = null;

        // L2: ReadServiceId / ReadSubfunctionParam / ReadBitLength / ParseByte are
        // duplicates of RequestBasedMappers' private methods. Kept separate to avoid
        // widening RequestBasedMappers' method visibility (minimal-change principle).

        // 1. Find all 0x27 REQUESTs and derive the level (smallest odd subfunction).
        // M5: 0x27 semantically has one seed response — first-match is correct (unlike
        // DID lengths where longest-match is used for multi-response DIDs).
        foreach (var req in xdoc.Descendants(ns + "REQUEST"))
        {
            var sid = ReadServiceId(req, ns);
            if (sid != ServiceId_SecurityAccess) continue;

            // Level comes from the SUBFUNCTION param (odd = RequestSeed).
            var sub = ReadSubfunctionParam(req, ns);
            if (sub % 2 == 1)  // odd subfunction = seed request
            {
                if (level is null || sub < level)
                    level = sub;
            }
        }

        if (level is null) return null;  // no 0x27 service found

        // 2. Derive seed length from POS-RESPONSE BIT-LENGTH.
        //    Walk DIAG-SERVICE → REQUEST-REF (0x27) → POS-RESPONSE-REF → POS-RESPONSE
        //    → PARAM SEMANTIC="DATA" → BIT-LENGTH.
        seedLength = TryExtractSeedLength(xdoc, ns);

        return new SecurityAccessConfig(level.Value, seedLength);
    }

    /// <summary>
    /// Attempt to extract seed byte length from the 0x27 POS-RESPONSE chain.
    /// Returns null if the chain is absent or unresolvable.
    /// </summary>
    private static int? TryExtractSeedLength(XDocument xdoc, XNamespace ns)
    {
        // Index REQUEST id → is-0x27.
        var req0x27ById = new Dictionary<string, bool>();
        foreach (var req in xdoc.Descendants(ns + "REQUEST"))
        {
            var reqId = (string?)req.Attribute("ID");
            if (reqId is null) continue;
            req0x27ById[reqId] = ReadServiceId(req, ns) == ServiceId_SecurityAccess;
        }

        // Index POS-RESPONSE id → element.
        var posById = new Dictionary<string, XElement>();
        foreach (var pos in xdoc.Descendants(ns + "POS-RESPONSE"))
        {
            var id = (string?)pos.Attribute("ID");
            if (id is not null) posById[id] = pos;
        }

        // Walk DIAG-SERVICEs; for each 0x27 REQUEST-REF, look for seed length.
        // Two ODX layouts supported:
        //   (a) POS-RESPONSE-REF indirect reference (Vector CANdelaStudio .odx-d)
        //   (b) inline POS-RESPONSE child element (some OEM tools)
        foreach (var svc in xdoc.Descendants(ns + "DIAG-SERVICE"))
        {
            var reqRefEl = svc.Element(ns + "REQUEST-REF");
            if (reqRefEl is null) continue;
            var reqRefId = (string?)reqRefEl.Attribute("ID-REF");
            if (reqRefId is null || !req0x27ById.GetValueOrDefault(reqRefId))
                continue;

            // (a) Indirect via POS-RESPONSE-REF.
            foreach (var posRef in svc.Elements(ns + "POS-RESPONSE-REFS")
                                      .Elements(ns + "POS-RESPONSE-REF"))
            {
                var posId = (string?)posRef.Attribute("ID-REF");
                if (posId is null || !posById.TryGetValue(posId, out var pos))
                    continue;

                var len = ExtractSeedBits(pos, ns);
                if (len > 0) return (len + 7) / 8;
            }

            // (b) Inline POS-RESPONSE child element.
            foreach (var pos in svc.Elements(ns + "POS-RESPONSE"))
            {
                var len = ExtractSeedBits(pos, ns);
                if (len > 0) return (len + 7) / 8;
            }
        }

        return null;

        // M4: assumes single SEMANTIC="DATA" PARAM per POS-RESPONSE (0x27 seed response
        // is semantically one field). If multiple DATA params exist, bits are summed
        // which may overestimate — acceptable fallback for unknown OEM schemas.
        static int ExtractSeedBits(XElement pos, XNamespace ns)
        {
            int totalBits = 0;
            int dataParams = 0;
            foreach (var param in pos.Descendants(ns + "PARAM"))
            {
                if ((string?)param.Attribute("SEMANTIC") != "DATA") continue;
                dataParams++;
                var bits = ReadBitLength(param, ns);
                if (bits is not null) totalBits += bits.Value;
            }
            return dataParams > 0 ? totalBits : 0;
        }
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

    private static byte ReadSubfunctionParam(XElement req, XNamespace ns)
    {
        var p = req.Descendants(ns + "PARAM")
            .FirstOrDefault(x => (string?)x.Attribute("SEMANTIC") == "SUBFUNCTION");
        return p is null ? (byte)0 : ParseByte(p) ?? (byte)0;
    }

    private static int? ReadBitLength(XElement parent, XNamespace ns)
    {
        var dct = parent.Descendants(ns + "DIAG-CODED-TYPE").FirstOrDefault();
        if (dct is null) return null;
        var bit = dct.Element(ns + "BIT-LENGTH");
        if (bit is null) return null;
        if (int.TryParse(bit.Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var n))
            return n;
        return null;
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
}
