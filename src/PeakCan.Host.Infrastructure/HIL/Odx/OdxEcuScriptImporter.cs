using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using PeakCan.Host.Core.HIL.Serialization;

namespace PeakCan.Host.Infrastructure.HIL.Odx;

/// <summary>
/// Imports ODX (Open Diagnostic Data Exchange) files and converts them to
/// peakcan-hil ECU script JSON format. Extracts UDS services and their
/// positive responses from ODX 2.0/2.2 documents.
/// </summary>
public static class OdxEcuScriptImporter
{
    /// <summary>
    /// Import an ODX file and convert to ECU script JSON.
    /// </summary>
    /// <param name="odxPath">Path to the .odx file.</param>
    /// <param name="ecuName">Name for the generated ECU script.</param>
    /// <param name="requestId">UDS request CAN ID (HIL sends to this ID).</param>
    /// <param name="responseId">UDS response CAN ID (HIL receives from this ID).</param>
    /// <returns>JSON string in ECU script format.</returns>
    /// <exception cref="InvalidOperationException">No UDS services found in ODX.</exception>
    public static string ImportToJson(
        string odxPath, string ecuName, uint requestId, uint responseId)
    {
        var doc = XDocument.Load(odxPath);
        var services = ParseOdxServices(doc);

        if (services.Count == 0)
            throw new InvalidOperationException($"No UDS services found in ODX file: {odxPath}");

        var rules = services.Select(s => new
        {
            serviceId = $"0x{s.Sid:X2}",
            subFunction = s.SubFunction,
            responseData = s.PositiveResponseBytes,
            responseDelayMs = 10
        });

        var script = new
        {
            name = ecuName,
            canIds = new { requestId = $"0x{requestId:X3}", responseId = $"0x{responseId:X3}" },
            rules
        };

        // Use HILJsonOptions for consistent formatting (camelCase, ByteArrayJsonConverter)
        return JsonSerializer.Serialize(script, HILJsonOptions.Default);
    }

    private record OdxService(byte Sid, byte? SubFunction, byte[] PositiveResponseBytes);

    /// <summary>
    /// Parse ODX DIAG-COMM elements to extract UDS services.
    /// Supports ODX 2.0/2.2 format. Unknown elements are skipped.
    /// </summary>
    private static List<OdxService> ParseOdxServices(XDocument doc)
    {
        var services = new List<OdxService>();

        // ODX 2.0/2.2: services defined under <DIAG-COMM-SPEC>/<DIAG-COMM>
        var diagComms = doc.Descendants().Where(e =>
            e.Name.LocalName == "DIAG-COMM" || e.Name.LocalName == "DIAG-COMM-SPEC");

        foreach (var comm in diagComms)
        {
            // Extract SID from <REQUEST-REF> or <DIAG-SERVICE> elements
            var requestRef = comm.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "REQUEST-REF");
            if (requestRef is null) continue;

            var sidAttr = requestRef.Attribute("ID-REF");
            if (sidAttr is null) continue;

            // Parse SID from ODX service ID format (e.g., "SID_0x22" or hex value)
            var sid = ParseSidFromOdx(sidAttr.Value);
            if (sid is null) continue;

            // Extract positive response bytes from <POS-RESPONSE> elements
            var responseBytes = ParsePositiveResponseBytes(comm);

            services.Add(new OdxService(sid.Value, null, responseBytes));
        }

        return services;
    }

    private static byte? ParseSidFromOdx(string odxId)
    {
        // ODX format: "SID_0x22" or just "0x22"
        if (odxId.StartsWith("SID_", StringComparison.OrdinalIgnoreCase))
            odxId = odxId[4..];
        if (odxId.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return byte.Parse(odxId[2..], NumberStyles.HexNumber);
        return byte.TryParse(odxId, out var sid) ? sid : null;
    }

    private static byte[] ParsePositiveResponseBytes(XElement comm)
    {
        // Extract response bytes from <POS-RESPONSE> or <RESPONSE> elements
        var responseEl = comm.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "POS-RESPONSE");
        if (responseEl is null) return Array.Empty<byte>();

        // Parse byte pattern from response element
        // (simplified: extract from <PARAM> elements with coded value)
        var bytes = new List<byte>();
        foreach (var param in responseEl.Descendants().Where(e => e.Name.LocalName == "PARAM"))
        {
            var codedValue = param.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "CODED-VALUE");
            if (codedValue is not null && byte.TryParse(codedValue.Value, out var b))
                bytes.Add(b);
        }
        return bytes.ToArray();
    }
}
