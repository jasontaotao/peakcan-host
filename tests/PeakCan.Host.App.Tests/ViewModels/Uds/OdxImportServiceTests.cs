using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.Core.Uds.Database;
using PeakCan.Host.Core.Uds.Odx;

namespace PeakCan.Host.App.Tests.ViewModels.Uds;

public class OdxImportServiceTests : IDisposable
{
    private readonly string _tempDir;

    public OdxImportServiceTests()
    {
        _tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"odx-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task ImportAsync_ValidOdxFile_PopulatesDatabases()
    {
        // Arrange — write a minimal ODX with 1 DID + 1 DTC + 1 Routine.
        var odxPath = System.IO.Path.Combine(_tempDir, "valid.odx");
        await File.WriteAllTextAsync(odxPath, """
            <?xml version="1.0" encoding="UTF-8"?>
            <ODX xmlns="http://www.asam.net/xml/odx" VERSION="2.0.0">
              <DIAG-LAYER-CONTAINER ID="DLC.x">
                <DIAG-LAYER ID="DL.base" SHORT-NAME="Base">
                  <DOP-BASE ID="DOP.0xF190" SHORT-NAME="VIN">
                    <DIAG-COMMS>
                      <DIAG-SERVICE ID="svc.r" SHORT-NAME="R">
                        <REQUEST-REF ID-REF="DOP.0xF190"/>
                      </DIAG-SERVICE>
                    </DIAG-COMMS>
                  </DOP-BASE>
                  <DTC-DOP ID="DTC.P0123" SHORT-NAME="P0123">
                    <DTC><TROUBLE-CODE>0x012356</TROUBLE-CODE></DTC>
                  </DTC-DOP>
                  <ECU-JOB ID="JOB.0x1234" SHORT-NAME="erase"/>
                </DIAG-LAYER>
              </DIAG-LAYER-CONTAINER>
            </ODX>
            """);
        var dids = new DidDatabase(userJsonPath: null, logger: null);
        var routines = new RoutineDatabase(userJsonPath: null, logger: null);
        var dtcs = new DtcDatabase();
        var svc = new OdxImportService(
            dids, routines, dtcs,
            new PdxReader(), new OdxParser(),
            NullLogger<OdxImportService>.Instance);

        // Act
        var result = await svc.ImportAsync(odxPath);

        // Assert
        result.HasError.Should().BeFalse();
        result.DidCount.Should().BeGreaterThan(0);
        result.DtcCount.Should().BeGreaterThan(0);
        result.RoutineCount.Should().BeGreaterThan(0);
        dtcs.All.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ImportAsync_MissingFile_ReturnsFailureResult()
    {
        // Arrange
        var svc = new OdxImportService(
            new DidDatabase(userJsonPath: null, logger: null),
            new RoutineDatabase(userJsonPath: null, logger: null),
            new DtcDatabase(),
            new PdxReader(), new OdxParser(),
            NullLogger<OdxImportService>.Instance);

        // Act
        var result = await svc.ImportAsync(System.IO.Path.Combine(_tempDir, "missing.odx"));

        // Assert — non-throwing; carries HasError + FileNotFound.
        result.HasError.Should().BeTrue();
        result.ErrorCode.Should().Be(OdxErrorCode.FileNotFound);
    }

    // ---------- v3.8.7 PATCH H1: per-document try/catch tolerance ----------

    /// <summary>
    /// v3.8.7 PATCH H1: a single malformed document inside a multi-document
    /// PDX/ODX bundle MUST NOT abort the entire import. Pre-fix, the per-doc
    /// foreach loop had no try/catch around <c>_parser.Parse</c> + the DOP-DTC-
    /// ECU-JOB walk -- a single bad XDocument (e.g. wrong root namespace)
    /// threw <c>OdxParseException</c> and propagated out of <c>ImportAsync</c>,
    /// returning a top-level <c>OdxImportResult.Failed</c> instead of the
    /// user's other valid documents.
    /// <para>
    /// Fix: wrap the per-doc body in try/catch and append to the warnings
    /// list. <see cref="OdxImportResult.Ok"/> already carries warnings, so
    /// the user sees which documents were skipped without losing the
    /// good ones.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ImportAsync_OneMalformedDocInBundle_ContinuesAndAddsWarning()
    {
        // Construct an .odx whose root element is NOT ODX-namespaced --
        // OdxParser.Parse throws OdxParseException at line 49 ("Root element
        // namespace 'foo' is not ODX namespace ..."). The fix wraps this
        // call site in try/catch and adds the exception message to warnings
        // rather than aborting.
        var odxPath = System.IO.Path.Combine(_tempDir, "wrong-root.odx");
        await File.WriteAllTextAsync(odxPath, """
            <?xml version="1.0" encoding="UTF-8"?>
            <WrongNamespace xmlns="http://example.com/not/odx">
              <DIAG-LAYER-CONTAINER ID="DLC.bad"/>
            </WrongNamespace>
            """);
        var dids = new DidDatabase(userJsonPath: null, logger: null);
        var routines = new RoutineDatabase(userJsonPath: null, logger: null);
        var dtcs = new DtcDatabase();
        var svc = new OdxImportService(
            dids, routines, dtcs,
            new PdxReader(), new OdxParser(),
            NullLogger<OdxImportService>.Instance);

        // Act
        var result = await svc.ImportAsync(odxPath);

        // Post-fix: the import returns Ok (not Failed), with the parser's
        // root-namespace warning captured. Pre-fix: this threw and the
        // caller received a top-level OdxImportResult.Failed instead.
        result.HasError.Should().BeFalse(
            "import must continue past a single malformed document and surface the parser warning");
        result.Warnings.Should().Contain(w => w.Contains("not ODX namespace") || w.Contains("namespace"),
            "the parser exception message must be captured in the warnings list");
        result.DidCount.Should().Be(0,
            "no DIDs are produced when the only document failed to parse");
    }
}
