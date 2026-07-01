using System.Globalization;
using System.Xml.Linq;
using PeakCan.Host.Core.Uds.Database;

namespace PeakCan.Host.Core.Uds.Odx;

/// <summary>
/// Extracts a <see cref="RoutineDefinition"/> from an ODX
/// <c>ECU-JOB</c> XML element.
/// </summary>
public static class EcuJob
{
    public static RoutineDefinition? TryMap(XElement job, out string? warning)
    {
        ArgumentNullException.ThrowIfNull(job);
        var idAttr = (string?)job.Attribute("ID");
        var shortName = (string?)job.Attribute("SHORT-NAME") ?? string.Empty;
        if (!TryParseId(idAttr, out var rid))
        {
            warning = $"ECU-JOB '{idAttr}' has invalid ID; skipping.";
            return null;
        }

        var result = new RoutineDefinition(
            Id: rid,
            Name: shortName,
            Description: string.Empty,
            Startable: true,
            Stoppable: true);

        warning = null;
        return result;
    }

    private static bool TryParseId(string? idAttr, out ushort id)
    {
        id = 0;
        if (idAttr is null) return false;
        var idx = idAttr.LastIndexOf('.');
        var hex = idx >= 0 && idx < idAttr.Length - 1 ? idAttr[(idx + 1)..] : idAttr;
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        return ushort.TryParse(hex, NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out id);
    }
}
