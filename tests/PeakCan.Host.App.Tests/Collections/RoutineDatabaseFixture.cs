using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core.Path;
using PeakCan.HIL.Core.Uds.Database;

namespace PeakCan.Host.App.Tests.Collections;

/// <summary>
/// xUnit collection fixture that creates a single populated <see cref="RoutineDatabase"/>
/// for all tests in <c>[Collection("RoutineDatabase")]</c>-decorated test classes.
/// Eliminates per-test temp-file write/delete cycles and the
/// %TEMP% file leak risk if a test forgets to delete.
/// </summary>
public sealed class RoutineDatabaseFixture : IDisposable
{
    // 2026-09-06 PATCH: sandboxed CI/test hosts may not be able to write to
    // %LOCALAPPDATA%. Keep the production allowlist contract intact by using
    // a test-owned root and passing it explicitly to PathOptions.
    private static readonly string TestRoot =
        Path.Combine(AppContext.BaseDirectory, "TestUserData");

    public string TempJsonPath { get; }
    public RoutineDatabase Db { get; }

    public RoutineDatabaseFixture()
    {
        TempJsonPath = Path.Combine(TestRoot, $"uds-rt-collection-{Guid.NewGuid():N}.json");
        var parentDir = Path.GetDirectoryName(TempJsonPath);
        if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);
        File.WriteAllText(TempJsonPath,
            "{\"routines\":[{\"id\":\"0xFF00\",\"name\":\"Erase\",\"description\":\"d\",\"startable\":true,\"stoppable\":true}]}");
        Db = new RoutineDatabase(
            TempJsonPath,
            logger: NullLogger<RoutineDatabase>.Instance,
            options: new PathOptions(new List<string> { TestRoot }));
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
