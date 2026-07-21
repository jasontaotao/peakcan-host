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
            // v3.8.7 PATCH H1: wrap the per-document body in try/catch so a
            // single malformed XDocument (e.g. wrong root namespace,
            // producing OdxParseException) doesn't abort the entire import.
            // Pre-fix, the exception propagated out of ImportAsync and the
            // caller received OdxImportResult.Failed for the whole bundle
            // -- losing the user's other valid documents. The fix
            // continues past the bad document with a warning, matching
            // the existing "Non-throwing by design" contract of IOdxImportService.
            try
            {
                ParseAndIndexOneDocument(xdoc, didDefs, dtcDefs, routineDefs, warnings, ct);
            }
            catch (Exception ex)
            {
                warnings.Add($"ODX parse error in document: {ex.Message}");
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

    /// <summary>
    /// v3.8.7 PATCH H1: extract the per-document parse + indexing body so
    /// the foreach loop in <see cref="ImportAsync"/> can wrap the call
    /// in a single try/catch. The extracted method mutates the supplied
    /// collections in-place; no return value (callers aggregate the
    /// collections to check the D6 DoS guard at the end).
    /// </summary>
    private void ParseAndIndexOneDocument(
        XDocument xdoc,
        List<DidDefinition> didDefs,
        List<DtcDefinition> dtcDefs,
        List<RoutineDefinition> routineDefs,
        List<string> warnings,
        // v3.9.0 MINOR P6: thread the per-document CT so a 500 MB+
        // ODX parse can be cancelled mid-document (not just between
        // documents). Checked between the major DOP / DTC-DOP /
        // ECU-JOB walk segments — checking inside tight DOP loops
        // would add per-element overhead. The check raises
        // OperationCanceledException, which propagates up to the
        // foreach in ImportAsync; ImportAsync's caller (the VM) sees
        // OCE in its await and resets IsBusy (the finally block).
        CancellationToken ct)
    {
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
        // v3.9.0 MINOR P6: check CT after the DOP-BASE + DTC-DOP walks
        // (the two heavy DOP walks). The ECU-JOB walk is also heavy
        // (one walk + one TryMap per job), so check there too.
        ct.ThrowIfCancellationRequested();
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
        var didLengths = RequestBasedMappers.ExtractDidLengths(xdoc, ns);
        // v3.49.0 MINOR T2.2: 提取每个 DID 的字段类型表（复合 DID 含多个
        // DidField，含 Base-DATA-TYPE / COMPU-METHOD / UNIT / 字节偏移）。
        var didFields = RequestBasedMappers.ExtractDidFields(xdoc, ns);
        foreach (var (did, writable) in didsFromRequests)
        {
            // 优先用字段表累计字节作 LengthBytes（更精确覆盖复合 DID）；
            // 缺字段表时回退旧 ExtractDidLengths。
            IReadOnlyList<DidField> fields =
                didFields.TryGetValue(did, out var fl) ? fl : Array.Empty<DidField>();
            var lengthBytes = fields.Count > 0
                ? fields.Sum(f => f.BitLength > 0 ? (f.BitLength + 7) / 8 : 0)
                : (didLengths.TryGetValue(did, out var l) ? l : 0);
            didDefs.Add(new DidDefinition(
                Id: did,
                Name: $"DID_0x{did:X4}",
                Description: lengthBytes > 0
                    ? $"DID 0x{did:X4} ({(writable ? "R/W" : "R")}, {lengthBytes}B, {fields.Count} field(s))"
                    : $"DID 0x{did:X4} ({(writable ? "R/W" : "R")}, {fields.Count} field(s))",
                LengthBytes: lengthBytes,
                Writable: writable)
                with { Fields = fields });
        }
        var routinesFromRequests = RequestBasedMappers.ExtractRoutines(xdoc, ns);
        routineDefs.AddRange(routinesFromRequests);
    }
}
