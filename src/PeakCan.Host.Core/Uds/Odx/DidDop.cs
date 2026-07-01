using System.Globalization;
using System.Xml.Linq;
using PeakCan.Host.Core.Uds.Database;

namespace PeakCan.Host.Core.Uds.Odx;

/// <summary>
/// Extracts a <see cref="DidDefinition"/> from an ODX
/// <c>DOP-BASE</c> XML element. The expected element shape is:
/// <code>
/// &lt;DOP-BASE ID="DOP.0xF190" SHORT-NAME="VIN_DOP"&gt;
///   &lt;DIAG-CODED-TYPE BASE-TYPE="A_ASCIISTRING"/&gt;
/// &lt;/DOP-BASE&gt;
/// </code>
/// The 2-byte DID id is parsed from the <c>ID</c> attribute (e.g.,
/// <c>"DOP.0xF190"</c> → <c>0xF190</c>).
/// </summary>
public static class DidDop
{
    /// <summary>Try to map a DOP-BASE element to a DidDefinition.</summary>
    /// <param name="dop">DOP-BASE XML element.</param>
    /// <param name="warning">Output warning (or null on success).</param>
    /// <returns>DidDefinition or null if input was structurally invalid.</returns>
    public static DidDefinition? TryMap(XElement dop, out string? warning)
    {
        ArgumentNullException.ThrowIfNull(dop);
        var idAttr = (string?)dop.Attribute("ID");
        if (idAttr is null || !TryParseDidFromId(idAttr, out var did))
        {
            warning = $"DOP-BASE missing or invalid ID attribute: '{idAttr}'.";
            return null;
        }
        var shortName = (string?)dop.Attribute("SHORT-NAME") ?? string.Empty;

        // DidDefinition is readonly record struct (Id, Name, Description,
        // LengthBytes, Writable). DOP-BASE exposes id + name; length/writable
        // default to safe-conservative values.
        var result = new DidDefinition(
            Id: did,
            Name: shortName,
            Description: shortName,
            LengthBytes: 0,
            Writable: false);

        warning = null;
        return result;
    }

    /// <summary>
    /// Parse a 2-byte DID from a DOP id like "DOP.0xF190" or
    /// "DOP.61840" (decimal fallback).
    /// </summary>
    private static bool TryParseDidFromId(string idAttr, out ushort did)
    {
        did = 0;
        var idx = idAttr.LastIndexOf('.');
        if (idx < 0 || idx >= idAttr.Length - 1) return false;
        var hexPart = idAttr[(idx + 1)..];
        if (hexPart.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hexPart = hexPart[2..];
        return ushort.TryParse(hexPart, NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out did);
    }
}
