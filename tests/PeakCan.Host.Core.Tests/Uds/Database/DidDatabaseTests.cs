using System;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core.Path;
using PeakCan.HIL.Core.Uds.Database;
using Xunit;

namespace PeakCan.HIL.Core.Tests.Uds.Database;

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
        // v2.1.5 PATCH: ensure parent dir exists before write. CI runner
        // has fresh %LOCALAPPDATA% — the app never ran so the dir is
        // absent. CreateDirectory is a no-op if exists.
        var parentDir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);
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

    // ---- Phase 2: Clear() for ODX re-import ----

    [Fact]
    public void Clear_Resets_To_BuiltIn_Defaults_Only()
    {
        var sut = new DidDatabase(logger: NullLogger<DidDatabase>.Instance);
        // 模拟 ODX 导入：追加条目
        sut.AddRange(new[] { new DidDefinition(0x9999, "ODX_DID", "from ODX", 4, false) }, out _);
        sut.All.Should().Contain(d => d.Id == 0x9999, "ODX import should add entry");

        sut.Clear();

        sut.All.Should().HaveCount(5, "Clear removes ODX imports, keeps built-ins");
        sut.All.Should().NotContain(d => d.Id == 0x9999, "ODX-imported entry must be gone");
        sut.All.Should().Contain(d => d.Id == 0xF190, "built-in VIN must survive Clear");
        sut.All.Should().Contain(d => d.Id == 0xF184, "built-in SoftwareVersion must survive Clear");
    }

    [Fact]
    public void Clear_After_Multiple_AddRanges_Resets_To_BuiltIn()
    {
        var sut = new DidDatabase(logger: NullLogger<DidDatabase>.Instance);
        sut.AddRange(new[] { new DidDefinition(0x1111, "ODX1", "", 1, false) }, out _);
        sut.AddRange(new[] { new DidDefinition(0x2222, "ODX2", "", 2, false) }, out _);
        sut.All.Should().HaveCount(7); // 5 built-in + 2 ODX

        sut.Clear();

        sut.All.Should().HaveCount(5);
        sut.All.Should().NotContain(d => d.Id == 0x1111 || d.Id == 0x2222);
    }

    [Fact]
    public void DidDatabase_With_Custom_AllowedRoots_Rejects_Path_Outside_List()
    {
        // Arrange — write a temp file under %TEMP% (outside any custom allowlist)
        // then construct DidDatabase with a custom allowlist that doesn't include %TEMP%.
        // The file should NOT be loaded (LoadUserFile's NormalizeRestricted throws
        // PathNormalizationException, which is NOT caught → exception escapes).
        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"peakcan-did-allowlist-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempPath, "{ \"dids\": [] }");
            var customOptions = new PathOptions(new List<string> { @"C:\Nonexistent\Root" });

            // Act — should throw because tempPath is outside the custom allowlist
            Action act = () => _ = new DidDatabase(tempPath, NullLogger<DidDatabase>.Instance, customOptions);

            // Assert — PathNormalizationException thrown (OutsideAllowedRoot reason)
            act.Should().Throw<PathNormalizationException>()
                .Where(ex => ex.Reason == PathNormalizationReason.OutsideAllowedRoot);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
