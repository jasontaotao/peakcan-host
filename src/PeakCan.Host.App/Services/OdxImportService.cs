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

            // Resolve the file's namespace once per document. ODX 2.x
            // (with xmlns) and ODX-D (no xmlns) differ here.
            var ns = xdoc.Root?.Name.Namespace ?? (XNamespace)OdxParser.OdxNamespace;

            // DOPs / DTC-DOPs / ECU-JOBs may live anywhere in the
            // document tree: inside DIAG-LAYER (canonical ODX 2.x) OR
            // inside ECU-SHARED-DATA (Vector CANdelaStudio .odx-d
            // layout). Walk the whole document so both layouts are
            // supported without per-shape branching.
            foreach (var dop in xdoc.Descendants(ns + "DOP-BASE"))
            {
                var def = DidDop.TryMap(dop, out var w);
                if (def is { } d) didDefs.Add(d);
                if (w is not null) warnings.Add(w);
            }

            // DTCs: build a document-wide index of inline <DTC> elements
            // first so DTC-REFs in secondary DTC-DOPs can resolve.
            var dtcIndex = DtcDop.IndexInlineDtcs(xdoc, ns);
            var seenDtcCodes = new HashSet<uint>();
            foreach (var dop in xdoc.Descendants(ns + "DTC-DOP"))
            {
                foreach (var (def, w) in DtcDop.Enumerate(dop, dtcIndex))
                {
                    if (w is not null) warnings.Add(w);
                    if (def is { } d && seenDtcCodes.Add(d.Code))
                        dtcDefs.Add(d);
                }
            }
            foreach (var job in xdoc.Descendants(ns + "ECU-JOB"))
            {
                var def = EcuJob.TryMap(job, out var w);
                if (def is not null) routineDefs.Add(def);
                if (w is not null) warnings.Add(w);
            }

            // Flat ODX-D layouts (Vector CANdelaStudio .odx-d) do not
            // use DOP-BASE / ECU-JOB. DIDs and routines are inline in
            // <REQUEST> elements with SERVICE-ID + ID PARAMs.
            var didsFromRequests = RequestBasedMappers.ExtractDids(xdoc, ns);
            foreach (var (did, writable) in didsFromRequests)
            {
                didDefs.Add(new DidDefinition(
                    Id: did,
                    Name: $"DID_0x{did:X4}",
                    Description: $"DID 0x{did:X4} ({(writable ? "R/W" : "R")})",
                    LengthBytes: 0, // P6 will populate from DOP-REF / DIAG-CODED-TYPE
                    Writable: writable));
            }
            var routinesFromRequests = RequestBasedMappers.ExtractRoutines(xdoc, ns);
            routineDefs.AddRange(routinesFromRequests);
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
