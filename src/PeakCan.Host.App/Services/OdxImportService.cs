using System.IO;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Uds.Database;
using PeakCan.Host.Core.Uds.Odx;

namespace PeakCan.Host.App.Services;

/// <summary>
/// Orchestrates ODX / PDX import: load via <see cref="PdxReader"/>,
/// parse via <see cref="OdxParser"/>, map via DidDop / DtcDop /
/// EcuJob, persist to the injected databases. Non-throwing by
/// design — every failure mode returns an <see cref="OdxImportResult"/>.
/// </summary>
public sealed class OdxImportService : IOdxImportService
{
    /// <summary>D6 spec DoS guard — refuse imports over this size per type.</summary>
    public const int MaxItemsPerType = 10_000;

    private readonly DidDatabase _dids;
    private readonly RoutineDatabase _routines;
    private readonly DtcDatabase _dtcs;
    private readonly PdxReader _reader;
    private readonly OdxParser _parser;
    private readonly ILogger<OdxImportService>? _logger;

    public OdxImportService(
        DidDatabase dids,
        RoutineDatabase routines,
        DtcDatabase dtcs,
        PdxReader reader,
        OdxParser parser,
        ILogger<OdxImportService>? logger = null)
    {
        _dids = dids;
        _routines = routines;
        _dtcs = dtcs;
        _reader = reader;
        _parser = parser;
        _logger = logger;
    }

    public async Task<OdxImportResult> ImportAsync(
        string odxPath, CancellationToken ct = default)
    {
        if (!File.Exists(odxPath))
            return OdxImportResult.Failed(OdxErrorCode.FileNotFound, odxPath);

        IReadOnlyList<XDocument> xdocs;
        try
        {
            xdocs = await _reader.LoadAsync(odxPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return OdxImportResult.Failed(OdxErrorCode.ParseError, ex.Message);
        }

        var warnings = new List<string>();
        var didDefs = new List<DidDefinition>();
        var routineDefs = new List<RoutineDefinition>();
        var dtcDefs = new List<DtcDefinition>();

        foreach (var xdoc in xdocs)
        {
            ct.ThrowIfCancellationRequested();
            _parser.Parse(xdoc, out var parseWarnings);
            warnings.AddRange(parseWarnings);

            foreach (var layerEl in xdoc.Descendants(XName.Get("DIAG-LAYER", OdxParser.OdxNamespace)))
            {
                foreach (var dop in layerEl.Descendants(XName.Get("DOP-BASE", OdxParser.OdxNamespace)))
                {
                    var def = DidDop.TryMap(dop, out var w);
                    if (def is { } d) didDefs.Add(d);
                    if (w is not null) warnings.Add(w);
                }
                foreach (var dop in layerEl.Descendants(XName.Get("DTC-DOP", OdxParser.OdxNamespace)))
                {
                    var def = DtcDop.TryMap(dop, out var w);
                    if (def is { } d) dtcDefs.Add(d);
                    if (w is not null) warnings.Add(w);
                }
                foreach (var job in layerEl.Descendants(XName.Get("ECU-JOB", OdxParser.OdxNamespace)))
                {
                    var def = EcuJob.TryMap(job, out var w);
                    if (def is not null) routineDefs.Add(def);
                    if (w is not null) warnings.Add(w);
                }
            }
        }

        // D6 — DoS guard.
        if (didDefs.Count > MaxItemsPerType || dtcDefs.Count > MaxItemsPerType ||
            routineDefs.Count > MaxItemsPerType)
        {
            return OdxImportResult.Failed(
                OdxErrorCode.Refused,
                $"ODX exceeded {MaxItemsPerType} items per type " +
                $"(DID={didDefs.Count}, DTC={dtcDefs.Count}, Routine={routineDefs.Count}); refused.");
        }

        _dids.AddRange(didDefs, out var didWarn);
        warnings.AddRange(didWarn);
        _routines.AddRange(routineDefs, out var routineWarn);
        warnings.AddRange(routineWarn);
        _dtcs.AddRange(dtcDefs, out var dtcWarn);
        warnings.AddRange(dtcWarn);

        return OdxImportResult.Ok(didDefs.Count, routineDefs.Count, dtcDefs.Count, warnings);
    }
}
