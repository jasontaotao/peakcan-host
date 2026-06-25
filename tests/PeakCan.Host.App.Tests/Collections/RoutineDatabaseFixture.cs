using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Core.Uds.Database;

namespace PeakCan.Host.App.Tests.Collections;

/// <summary>
/// xUnit collection fixture that creates a single populated <see cref="RoutineDatabase"/>
/// for all tests in <c>[Collection("RoutineDatabase")]</c>-decorated test classes.
/// Eliminates per-test temp-file write/delete cycles and the
/// %TEMP% file leak risk if a test forgets to delete.
/// </summary>
public sealed class RoutineDatabaseFixture : IDisposable
{
    public string TempJsonPath { get; }
    public RoutineDatabase Db { get; }

    public RoutineDatabaseFixture()
    {
        TempJsonPath = Path.Combine(
            Path.GetTempPath(),
            $"uds-rt-collection-{Guid.NewGuid():N}.json");
        File.WriteAllText(TempJsonPath,
            "{\"routines\":[{\"id\":\"0xFF00\",\"name\":\"Erase\",\"description\":\"d\",\"startable\":true,\"stoppable\":true}]}");
        Db = new RoutineDatabase(TempJsonPath, logger: NullLogger<RoutineDatabase>.Instance);
    }

    public void Dispose()
    {
        if (File.Exists(TempJsonPath))
        {
            File.Delete(TempJsonPath);
        }
    }
}

[CollectionDefinition("RoutineDatabase")]
public sealed class RoutineDatabaseCollection : ICollectionFixture<RoutineDatabaseFixture>
{
}
