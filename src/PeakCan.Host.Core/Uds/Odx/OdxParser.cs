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

    /// <summary>
    /// Empty namespace = no xmlns declared. ISO 22901 ODX-D files
    /// (Vector CANdelaStudio .odx-d exports) commonly ship with
    /// <c>xsi:noNamespaceSchemaLocation="odx.xsd"</c> and no default
    /// namespace. We accept that as a valid ODX variant.
    /// </summary>
    public const string NoNamespace = "";

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
        if (root.Name.NamespaceName != OdxNamespace &&
            root.Name.NamespaceName != NoNamespace)
            throw new OdxParseException(
                $"Root element namespace '{root.Name.NamespaceName}' is not ODX namespace '{OdxNamespace}' or empty (no-namespace ODX-D).");

        // Resolve the actual namespace once. Files using ODX 2.x with
        // xmlns="http://www.asam.net/xml/odx" get the ODX namespace;
        // ODX-D (Vector CANdelaStudio .odx-d) gets an empty XNamespace
        // (no xmlns). All descendant element lookups must use this same
        // namespace to match.
        var ns = root.Name.Namespace;

        var version = (string?)root.Attribute("VERSION") ?? "unknown";
        var warnList = new List<string>();

        // D5 — warn if version outside accepted range, but still parse.
        if (Version.TryParse(version, out var v) &&
            (v < MinVersion || v > MaxVersion))
        {
            warnList.Add(
                $"ODX VERSION {version} outside accepted {MinVersion}-{MaxVersion} range; attempting parse anyway.");
        }

        var layers = ParseLayers(root, warnList, ns);
        warnings = warnList;
        return new OdxDocument(version, layers);
    }

    private static IReadOnlyList<DiagLayer> ParseLayers(XElement root, List<string> warnings, XNamespace ns)
    {
        var container = root.Element(ns + "DIAG-LAYER-CONTAINER");
        if (container is null)
        {
            warnings.Add("ODX has no DIAG-LAYER-CONTAINER; no layers parsed.");
            return Array.Empty<DiagLayer>();
        }

        // ISO 22901 ODX 2.x supports two layer-element shapes:
        //   - <DIAG-LAYER>          : direct child of DIAG-LAYER-CONTAINER
        //   - <BASE-VARIANTS><BASE-VARIANT>...</BASE-VARIANT></BASE-VARIANTS>
        //                             : the BASE-VARIANTS plural wrapper
        // Vector CANdelaStudio (.odx-d) commonly uses the latter form.
        // Both are treated as layer-equivalent for downstream extraction.
        var layers = new List<DiagLayer>();

        foreach (var layerEl in container.Elements(ns + "DIAG-LAYER"))
        {
            layers.Add(BuildLayer(layerEl, ns));
        }

        foreach (var bvWrapper in container.Elements(ns + "BASE-VARIANTS"))
        {
            foreach (var layerEl in bvWrapper.Elements(ns + "BASE-VARIANT"))
            {
                layers.Add(BuildLayer(layerEl, ns));
            }
        }

        return layers;
    }

    private static DiagLayer BuildLayer(XElement layerEl, XNamespace ns)
    {
        var id = (string?)layerEl.Attribute("ID") ?? string.Empty;
        var shortName = (string?)layerEl.Attribute("SHORT-NAME") ?? string.Empty;
        var services = ParseServices(layerEl, ns);
        return new DiagLayer(id, shortName, services);
    }

    private static IReadOnlyList<DiagService> ParseServices(XElement layerEl, XNamespace ns)
    {
        // DIAG-COMMS → DIAG-SERVICE per ISO 22901.
        var comms = layerEl.Element(ns + "DIAG-COMMS");
        if (comms is null) return Array.Empty<DiagService>();

        var services = new List<DiagService>();
        foreach (var svcEl in comms.Elements(ns + "DIAG-SERVICE"))
        {
            var id = (string?)svcEl.Attribute("ID") ?? string.Empty;
            var shortName = (string?)svcEl.Attribute("SHORT-NAME") ?? string.Empty;
            var reqRefEl = svcEl.Element(ns + "REQUEST-REF");
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
