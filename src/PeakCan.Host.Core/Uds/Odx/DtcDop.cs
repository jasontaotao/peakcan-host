using System.Globalization;
using System.Xml.Linq;
using PeakCan.Host.Core.Uds.Database;

namespace PeakCan.Host.Core.Uds.Odx;

/// <summary>
/// Extracts a <see cref="DtcDefinition"/> from an ODX
/// <c>DTC-DOP</c> XML element. Minimal schema — DTC + optional
/// short-name + text.
/// </summary>
public static class DtcDop
{
    public static DtcDefinition? TryMap(XElement dop, out string? warning)
    {
        ArgumentNullException.ThrowIfNull(dop);
        var idAttr = (string?)dop.Attribute("ID");
        var shortName = (string?)dop.Attribute("SHORT-NAME") ?? string.Empty;
        var dtcEl = dop.Element(XName.Get("DTC", OdxParser.OdxNamespace));
        if (dtcEl is null)
        {
            warning = $"DTC-DOP '{idAttr}' missing DTC child element; skipping.";
            return null;
        }

        var codeText = ((string?)dtcEl.Element(XName.Get("TROUBLE-CODE", OdxParser.OdxNamespace)))?.Trim();
        if (codeText is null || !uint.TryParse(codeText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? codeText[2..] : codeText,
                NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
        {
            warning = $"DTC-DOP '{idAttr}' has invalid TROUBLE-CODE '{codeText}'; skipping.";
            return null;
        }

        var desc = ExtractFirstText(dtcEl);

        var result = new DtcDefinition(
            Code: code,
            ShortName: shortName,
            Description: desc,
            StatusMask: 0x2F); // Per ISO 14229-1 D.2 default visible status mask.

        warning = null;
        return result;
    }

    private static string ExtractFirstText(XElement dtcEl)
    {
        var tab = dtcEl.Element(XName.Get("TEXT", OdxParser.OdxNamespace))?
                          .Element(XName.Get("DTC-TAB", OdxParser.OdxNamespace));
        return ((string?)tab?.Attribute("SHORT-NAME")) ?? string.Empty;
    }
}
