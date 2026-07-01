using System.Xml.Linq;

namespace PeakCan.Host.Core.Uds.Odx;

/// <summary>
/// Parses ODX 2.0.0 / 2.2.0 <c>XDocument</c> trees into the
/// in-memory <see cref="OdxDocument"/> model. Non-fatal schema
/// deviations (unsupported version, missing optional elements)
/// produce warnings via the <c>out warnings</c> parameter;
/// fatal deviations (missing root, malformed XML namespaces)
/// throw <see cref="OdxParseException"/>.
/// </summary>
public sealed class OdxParser
{
    /// <summary>ODX 2.0.0 XML namespace URI (ISO 22901).</summary>
    public const string OdxNamespace = "http://www.asam.net/xml/odx";

    /// <summary>Accepted schema version range (D5 spec).</summary>
    private static readonly Version MinVersion = new(2, 0, 0);
    private static readonly Version MaxVersion = new(2, 2, 0);

    /// <summary>
    /// Parse an ODX <c>XDocument</c> into an <see cref="OdxDocument"/>.
    /// </summary>
    /// <param name="xdoc">Input XML document.</param>
    /// <param name="warnings">Output list of non-fatal warning messages.</param>
    /// <returns>Parsed ODX document.</returns>
    /// <exception cref="OdxParseException">
    /// Thrown only on fatal schema violations (missing root element,
    /// wrong XML namespace).
    /// </exception>
    public OdxDocument Parse(XDocument xdoc, out IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(xdoc);
        if (xdoc.Root is null)
            throw new OdxParseException("ODX document has no root element.");

        var root = xdoc.Root;
        if (root.Name.NamespaceName != OdxNamespace)
            throw new OdxParseException(
                $"Root element namespace '{root.Name.NamespaceName}' is not ODX namespace '{OdxNamespace}'.");

        var version = (string?)root.Attribute("VERSION") ?? "unknown";
        var warnList = new List<string>();

        // D5 — warn if version outside accepted range, but still parse.
        if (Version.TryParse(version, out var v) &&
            (v < MinVersion || v > MaxVersion))
        {
            warnList.Add(
                $"ODX VERSION {version} outside accepted {MinVersion}-{MaxVersion} range; attempting parse anyway.");
        }

        var layers = ParseLayers(root, warnList);
        warnings = warnList;
        return new OdxDocument(version, layers);
    }

    private static IReadOnlyList<DiagLayer> ParseLayers(XElement root, List<string> warnings)
    {
        var container = root.Element(XName.Get("DIAG-LAYER-CONTAINER", OdxNamespace));
        if (container is null)
        {
            warnings.Add("ODX has no DIAG-LAYER-CONTAINER; no layers parsed.");
            return Array.Empty<DiagLayer>();
        }

        var layerElements = container.Elements(XName.Get("DIAG-LAYER", OdxNamespace));
        var layers = new List<DiagLayer>();
        foreach (var layerEl in layerElements)
        {
            var id = (string?)layerEl.Attribute("ID") ?? string.Empty;
            var shortName = (string?)layerEl.Attribute("SHORT-NAME") ?? string.Empty;
            var services = ParseServices(layerEl);
            layers.Add(new DiagLayer(id, shortName, services));
        }

        return layers;
    }

    private static IReadOnlyList<DiagService> ParseServices(XElement layerEl)
    {
        // DIAG-COMMS → DIAG-SERVICE per ISO 22901.
        var comms = layerEl.Element(XName.Get("DIAG-COMMS", OdxNamespace));
        if (comms is null) return Array.Empty<DiagService>();

        var services = new List<DiagService>();
        foreach (var svcEl in comms.Elements(XName.Get("DIAG-SERVICE", OdxNamespace)))
        {
            var id = (string?)svcEl.Attribute("ID") ?? string.Empty;
            var shortName = (string?)svcEl.Attribute("SHORT-NAME") ?? string.Empty;
            var reqRefEl = svcEl.Element(XName.Get("REQUEST-REF", OdxNamespace));
            var requestRefId = (string?)reqRefEl?.Attribute("ID-REF") ?? string.Empty;
            services.Add(new DiagService(id, shortName, requestRefId));
        }
        return services;
    }
}

/// <summary>
/// Fatal ODX schema violations. Non-fatal issues are reported
/// via the <c>warnings</c> parameter, not via exceptions.
/// </summary>
public sealed class OdxParseException : InvalidOperationException
{
    public OdxParseException(string message) : base(message) { }
}
