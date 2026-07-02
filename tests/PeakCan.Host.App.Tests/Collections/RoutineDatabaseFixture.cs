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
        // v1.6.4 PATCH: RoutineDatabase now routes user-JSON reads through
        // PathNormalizer.NormalizeRestricted with the %LOCALAPPDATA%\PeakCan.Host
        // allowlist. The collection fixture's shared temp file must therefore
        // live under that root.
        TempJsonPath = Path.Combine(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PeakCan.Host"),
            $"uds-rt-collection-{Guid.NewGuid():N}.json");
        // v2.1.5 PATCH: ensure parent dir exists before write. CI runner
        // has fresh %LOCALAPPDATA% — the app never ran so the dir is
        // absent. Local dev boxes usually have the dir from previous
        // runs which masked this. CreateDirectory is a no-op if exists.
        var parentDir = Path.GetDirectoryName(TempJsonPath);
        if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);
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
