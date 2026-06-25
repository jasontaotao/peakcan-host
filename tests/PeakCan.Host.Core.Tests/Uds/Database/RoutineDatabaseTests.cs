using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Core.Uds.Database;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds.Database;

public class RoutineDatabaseTests
{
    private static string TempJson(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(),
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
            userJsonPath: Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.json"),
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
}
