using PeakCan.Host.Core.Uds.Odx;

namespace PeakCan.Host.App.Services;

/// <summary>
/// Service interface for ODX / PDX import operations. Consumed
/// by <c>OdxImportViewModel</c> via DI; not exposed to V8 scripts.
/// </summary>
public interface IOdxImportService
{
    /// <summary>
    /// Import an .odx or .pdx file. Mutates the injected
    /// databases; non-throwing — returns <see cref="OdxImportResult"/>
    /// carrying counts + warnings + optional error code.
    /// </summary>
    Task<OdxImportResult> ImportAsync(string odxPath, CancellationToken ct = default);
}
