using System.Globalization;
using System.Xml.Linq;
using PeakCan.Host.Core.Uds.Database;

namespace PeakCan.Host.Core.Uds.Odx;

/// <summary>
/// Extracts <see cref="DtcDefinition"/> from ODX <c>DTC-DOP</c>
/// XML elements. Handles three real-world layouts:
/// <list type="number">
///   <item>Inline <c>&lt;DTC&gt;</c> child of <c>DTC-DOP</c> (canonical ODX 2.x).</item>
///   <item>Wrapped <c>&lt;DTCS&gt;&lt;DTC&gt;...&lt;/DTC&gt;&lt;/DTCS&gt;</c> (Vector CANdelaStudio .odx-d, primary DTC-DOP).</item>
///   <item><c>&lt;DTC-REF ID-REF="..."&gt;</c> referring to a DTC defined in another DTC-DOP (shared DTC pool, ODX 2.2+).</item>
/// </list>
/// </summary>
public static class DtcDop
{
    /// <summary>
    /// Enumerate all DTC definitions reachable from this DTC-DOP,
    /// including via DTC-REF cross-references. Returns
    /// (definition, warning?) pairs so callers can collect diagnostics.
    /// </summary>
    /// <param name="dop">DTC-DOP element.</param>
    /// <param name="dtcById">Cross-document lookup of DTC id → DTC element
    /// (must be built by the caller by walking all inline <c>&lt;DTC&gt;</c>
    /// elements before invoking this method).</param>
    public static IEnumerable<(DtcDefinition? Def, string? Warning)> Enumerate(
        XElement dop, IReadOnlyDictionary<string, XElement> dtcById)
    {
        ArgumentNullException.ThrowIfNull(dop);
        ArgumentNullException.ThrowIfNull(dtcById);
        var idAttr = (string?)dop.Attribute("ID");
        var ns = dop.Name.Namespace;

        // Layout 1: inline <DTC> direct children
        foreach (var dtcEl in dop.Descendants(ns + "DTC"))
        {
            if (dtcEl.Parent?.Name.LocalName == "DTCS")
            {
                // Layout 2: <DTCS><DTC>...</DTC></DTCS> (already covered
                // by Descendants but be explicit about the in-DTCS path).
            }
            yield return (TryMapSingle(dtcEl, ns), null);
        }

        // Layout 3: <DTC-REF ID-REF="..." /> references
        foreach (var dtcRef in dop.Descendants(ns + "DTC-REF"))
        {
            var refId = (string?)dtcRef.Attribute("ID-REF");
            if (refId is null || !dtcById.TryGetValue(refId, out var target))
            {
                yield return (null, $"DTC-DOP '{idAttr}' DTC-REF '{refId}' not found in document.");
                continue;
            }
            yield return (TryMapSingle(target, ns), null);
        }
    }

    /// <summary>
    /// Build a document-wide lookup of DTC id → DTC element. Walks all
    /// inline <c>&lt;DTC&gt;</c> elements anywhere in the document. Use
    /// this once before calling <see cref="Enumerate"/> on each DTC-DOP.
    /// </summary>
    public static IReadOnlyDictionary<string, XElement> IndexInlineDtcs(
        XDocument xdoc, XNamespace ns)
    {
        ArgumentNullException.ThrowIfNull(xdoc);
        var map = new Dictionary<string, XElement>();
        foreach (var dtc in xdoc.Descendants(ns + "DTC"))
        {
            var id = (string?)dtc.Attribute("ID");
            if (id is null) continue;
            map.TryAdd(id, dtc); // first wins
        }
        return map;
    }

    private static DtcDefinition? TryMapSingle(XElement dtcEl, XNamespace ns)
    {
        var codeText = ((string?)dtcEl.Element(ns + "TROUBLE-CODE"))?.Trim();
        if (codeText is null) return null;

        // Per ISO 22901 ODX, <TROUBLE-CODE> is decimal by default. Only
        // a "0x" prefix signals hex. Real OEM .odx-d files (Vector
        // CANdelaStudio) consistently emit decimal (e.g. 687361 = 0xA7D01).
        var hasHexPrefix = codeText.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        var style = hasHexPrefix ? NumberStyles.HexNumber : NumberStyles.Integer;
        var raw = hasHexPrefix ? codeText[2..] : codeText;
        if (!uint.TryParse(raw, style, CultureInfo.InvariantCulture, out var code))
            return null;

        var desc = ExtractFirstText(dtcEl, ns);
        return new DtcDefinition(
            Code: code,
            ShortName: (string?)dtcEl.Element(ns + "SHORT-NAME") ?? string.Empty,
            Description: desc,
            StatusMask: 0x2F); // Per ISO 14229-1 D.2 default visible status mask.
    }

    private static string ExtractFirstText(XElement dtcEl, XNamespace ns)
    {
        var tab = dtcEl.Element(ns + "TEXT")?
                          .Element(ns + "DTC-TAB");
        return ((string?)tab?.Attribute("SHORT-NAME")) ?? string.Empty;
    }
}
