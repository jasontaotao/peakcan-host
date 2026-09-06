using System;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core.Path;
using PeakCan.HIL.Core.Uds.Database;
using Xunit;
using PeakCan.Host.Core.Path;
using PeakCan.Host.Core.Uds.Database;

namespace PeakCan.Host.Core.Tests.Uds.Database;

public class RoutineDatabaseTests
{
    // 2026-09-06 PATCH: sandboxed CI/test hosts may not be able to write to
    // %LOCALAPPDATA%. Keep the production allowlist contract intact by using
    // a test-owned root and passing it explicitly to PathOptions.
    private static readonly string TestRoot =
        System.IO.Path.Combine(AppContext.BaseDirectory, "TestUserData");

    private static PathOptions TestOptions => new(new List<string> { TestRoot });

    private static string TempJson(string contents)
    {
        var path = System.IO.Path.Combine(TestRoot, $"uds-routines-{Guid.NewGuid():N}.json");
        var parentDir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);
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
            var sut = new RoutineDatabase(path, NullLogger<RoutineDatabase>.Instance, TestOptions);

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
            var sut = new RoutineDatabase(path, NullLogger<RoutineDatabase>.Instance, TestOptions);

            Assert.Empty(sut.All);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- Phase 2: Clear() for ODX re-import ----

    [Fact]
    public void Clear_Empties_Database()
    {
        var sut = new RoutineDatabase();
        sut.AddRange(new[] { new RoutineDefinition(0x1234, "ODX_Routine", "d", true, false) }, out _);
        sut.All.Should().HaveCount(1);

        sut.Clear();

        sut.All.Should().BeEmpty("RoutineDatabase has no built-ins → Clear empties it");
    }

    [Fact]
    public void Clear_After_Multiple_AddRanges_Empties()
    {
        var sut = new RoutineDatabase();
        sut.AddRange(new[] { new RoutineDefinition(0x1111, "R1", "d", true, false) }, out _);
        sut.AddRange(new[] { new RoutineDefinition(0x2222, "R2", "d", true, false) }, out _);
        sut.All.Should().HaveCount(2);

        sut.Clear();

        sut.All.Should().BeEmpty();
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
        var tempPath = TempJson("{ \"routines\": [] }");
        try
        {
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
