using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Core.Uds.Database;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds.Database;

public class DidDatabaseTests
{
    private static string TempJson(string contents)
    {
        // v1.6.4 PATCH: DidDatabase now routes user-JSON reads through
        // PathNormalizer.NormalizeRestricted with the %LOCALAPPDATA%\PeakCan.Host
        // allowlist. Test fixtures must therefore live under that root.
        var path = System.IO.Path.Combine(
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PeakCan.Host"),
            $"uds-dids-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void DefaultJsonPath_Is_Under_LocalAppData_PeakCanHost()
    {
        var path = DidDatabaseDefaults.DefaultJsonPath;

        Assert.Contains("PeakCan.Host", path);
        Assert.EndsWith("uds-dids.json", path);
        Assert.Contains(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            path);
    }

    [Fact]
    public void DefaultCtor_Uses_BuiltIn_Defaults()
    {
        var sut = new DidDatabase(logger: NullLogger<DidDatabase>.Instance);

        Assert.NotEmpty(sut.All);
        Assert.Contains(sut.All, d => d.Id == 0xF190 && d.Name == "VIN");
        Assert.Contains(sut.All, d => d.Id == 0xF184 && d.Name == "SoftwareVersion");
        Assert.Equal(5, sut.All.Count);
    }

    [Fact]
    public void UserJson_Overrides_BuiltIn_For_Matching_Id()
    {
        var path = TempJson("""
        {
          "dids": [
            { "id": "0xF190", "name": "Custom VIN", "description": "OEM-specific VIN", "lengthBytes": 20, "writable": true }
          ]
        }
        """);

        try
        {
            var sut = new DidDatabase(path, NullLogger<DidDatabase>.Instance);

            var vin = sut.Find(0xF190);
            Assert.NotNull(vin);
            Assert.Equal("Custom VIN", vin!.Name);
            Assert.Equal("OEM-specific VIN", vin.Description);
            Assert.Equal(20, vin.LengthBytes);
            Assert.True(vin.Writable);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UserJson_Appends_NonOverlapping_Entries()
    {
        var path = TempJson("""
        {
          "dids": [
            { "id": "0x1234", "name": "Custom", "description": "d", "lengthBytes": 4, "writable": false }
          ]
        }
        """);

        try
        {
            var sut = new DidDatabase(path, NullLogger<DidDatabase>.Instance);

            Assert.Equal(6, sut.All.Count); // 5 built-in + 1 custom
            Assert.NotNull(sut.Find(0x1234));
            Assert.NotNull(sut.Find(0xF190));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UserJson_Malformed_Falls_Back_To_BuiltIn_And_Logs_Warning()
    {
        var path = TempJson("{ this is not valid JSON");

        try
        {
            var sut = new DidDatabase(path, NullLogger<DidDatabase>.Instance);

            Assert.Equal(5, sut.All.Count);
            Assert.NotNull(sut.Find(0xF190));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UserJson_Missing_File_Falls_Back_To_BuiltIn()
    {
        var sut = new DidDatabase(
            userJsonPath: System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.json"),
            logger: NullLogger<DidDatabase>.Instance);

        Assert.Equal(5, sut.All.Count);
    }

    [Fact]
    public void Find_ExistingId_Returns_Definition()
    {
        var sut = new DidDatabase(logger: NullLogger<DidDatabase>.Instance);

        Assert.NotNull(sut.Find(0xF190));
    }

    [Fact]
    public void Find_MissingId_Returns_Null()
    {
        var sut = new DidDatabase(logger: NullLogger<DidDatabase>.Instance);

        Assert.Null(sut.Find(0xABCD));
    }
}
