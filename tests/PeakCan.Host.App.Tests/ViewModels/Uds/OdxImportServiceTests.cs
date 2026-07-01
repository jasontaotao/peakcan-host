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
}
