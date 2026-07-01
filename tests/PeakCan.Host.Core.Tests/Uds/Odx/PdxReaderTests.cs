using System.IO.Compression;
using System.Xml.Linq;
using FluentAssertions;
using PeakCan.Host.Core.Uds.Odx;

namespace PeakCan.Host.Core.Tests.Uds.Odx;

public class PdxReaderTests : IDisposable
{
    private readonly string _tempDir;

    public PdxReaderTests()
    {
        _tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"odx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task LoadAsync_OdxFile_ReturnsSingleDocument()
    {
        // Arrange
        var odxPath = System.IO.Path.Combine(_tempDir, "single.odx");
        await File.WriteAllTextAsync(odxPath, """
            <?xml version="1.0" encoding="UTF-8"?>
            <ODX xmlns="http://www.asam.net/xml/odx" VERSION="2.0.0">
              <DIAG-LAYER-CONTAINER ID="DLC.x"/>
            </ODX>
            """);

        // Act
        var docs = await new PdxReader().LoadAsync(odxPath);

        // Assert
        docs.Should().ContainSingle();
        docs[0].Root!.Name.LocalName.Should().Be("ODX");
    }

    [Fact]
    public async Task LoadAsync_PdxFile_ReturnsAllOdxEntries()
    {
        // Arrange — create a .pdx (zip) with 2 .odx entries and 1 non-odx entry.
        var pdxPath = System.IO.Path.Combine(_tempDir, "bundle.pdx");
        var odx1Content = """<?xml version="1.0"?><ODX xmlns="http://www.asam.net/xml/odx" VERSION="2.0.0"><DIAG-LAYER-CONTAINER ID="DLC.a"/></ODX>""";
        var odx2Content = """<?xml version="1.0"?><ODX xmlns="http://www.asam.net/xml/odx" VERSION="2.0.0"><DIAG-LAYER-CONTAINER ID="DLC.b"/></ODX>""";

        using (var zip = ZipFile.Open(pdxPath, ZipArchiveMode.Create))
        {
            using (var s = zip.CreateEntry("a.odx").Open())
                s.Write(System.Text.Encoding.UTF8.GetBytes(odx1Content));
            using (var s = zip.CreateEntry("b.odx").Open())
                s.Write(System.Text.Encoding.UTF8.GetBytes(odx2Content));
            using (var s = zip.CreateEntry("README.txt").Open())
                s.Write(System.Text.Encoding.UTF8.GetBytes("ignored"));
        }

        // Act
        var docs = await new PdxReader().LoadAsync(pdxPath);

        // Assert — exactly 2 .odx docs; README.txt skipped.
        docs.Should().HaveCount(2);
        docs.All(d => d.Root!.Name.LocalName == "ODX").Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_MissingFile_ThrowsFileNotFoundException()
    {
        // Arrange
        var missing = System.IO.Path.Combine(_tempDir, "does-not-exist.odx");

        // Act
        Func<Task> act = async () => await new PdxReader().LoadAsync(missing);

        // Assert
        await act.Should().ThrowAsync<FileNotFoundException>();
    }
}
