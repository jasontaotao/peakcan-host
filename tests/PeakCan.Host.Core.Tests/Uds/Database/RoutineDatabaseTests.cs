using System;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Core.Path;
using PeakCan.Host.Core.Uds.Database;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds.Database;

public class RoutineDatabaseTests
{
    private static string TempJson(string contents)
    {
        // v1.6.4 PATCH: RoutineDatabase now routes user-JSON reads through
        // PathNormalizer.NormalizeRestricted with the %LOCALAPPDATA%\PeakCan.Host
        // allowlist. Test fixtures must therefore live under that root.
        var path = System.IO.Path.Combine(
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PeakCan.Host"),
            $"uds-routines-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void DefaultJsonPath_Is_Under_LocalAppData_PeakCanHost()
    {
        var path = RoutineDatabaseDefaults.DefaultJsonPath;

        Assert.Contains("PeakCan.Host", path);
        Assert.EndsWith("uds-routines.json", path);
    }

    [Fact]
    public void DefaultCtor_NoUserFile_Returns_Empty()
    {
        var sut = new RoutineDatabase(
            userJsonPath: System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.json"),
            logger: NullLogger<RoutineDatabase>.Instance);

        Assert.Empty(sut.All);
    }

    [Fact]
    public void UserJson_Populates_All()
    {
        var path = TempJson("""
        {
          "routines": [
            { "id": "0xFF00", "name": "EraseMemory",   "description": "Erase flash",     "startable": true,  "stoppable": true  },
            { "id": "0xFF01", "name": "CheckIntegrity", "description": "Integrity check", "startable": true,  "stoppable": false }
          ]
        }
        """);

        try
        {
            var sut = new RoutineDatabase(path, NullLogger<RoutineDatabase>.Instance);

            Assert.Equal(2, sut.All.Count);
            Assert.Equal("EraseMemory", sut.Find(0xFF00)?.Name);
            Assert.True(sut.Find(0xFF00)!.Stoppable);
            Assert.False(sut.Find(0xFF01)!.Stoppable);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UserJson_Malformed_Returns_Empty_And_Logs_Warning()
    {
        var path = TempJson("{ malformed");

        try
        {
            var sut = new RoutineDatabase(path, NullLogger<RoutineDatabase>.Instance);

            Assert.Empty(sut.All);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Find_MissingId_Returns_Null()
    {
        var sut = new RoutineDatabase(logger: NullLogger<RoutineDatabase>.Instance);

        Assert.Null(sut.Find(0xABCD));
    }

    [Fact]
    public void RoutineDatabase_With_Custom_AllowedRoots_Rejects_Path_Outside_List()
    {
        // Arrange
        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"peakcan-rt-allowlist-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempPath, "{ \"routines\": [] }");
            var customOptions = new PathOptions(new List<string> { @"C:\Nonexistent\Root" });

            // Act
            Action act = () => _ = new RoutineDatabase(tempPath, NullLogger<RoutineDatabase>.Instance, customOptions);

            // Assert
            act.Should().Throw<PathNormalizationException>()
                .Where(ex => ex.Reason == PathNormalizationReason.OutsideAllowedRoot);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
