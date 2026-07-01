using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Core.Uds.Database;

namespace PeakCan.Host.Core.Tests.Uds.Database;

public class RoutineDatabaseNreTests
{
    [Fact]
    public void Ctor_WithNullLogger_DoesNotThrow()
    {
        // Arrange: pass null logger (the "do not log anything" case).
        // Spec §9.1: this must NOT throw — the logger parameter is optional.

        // Act
        Action act = () => _ = new RoutineDatabase(userJsonPath: null, logger: null);

        // Assert: no exception thrown.
        act.Should().NotThrow();
    }

    [Fact]
    public void Ctor_WithNullLoggerAndMissingPath_DoesNotThrow()
    {
        // Arrange: 1-arg ctor path-equivalent — non-null path that points at nothing.
        // This is the path taken by the legacy RoutineDatabase(ILogger?) ctor.

        // Act
        Action act = () => _ = new RoutineDatabase(
            userJsonPath: System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.json"),
            logger: null);

        // Assert: no exception thrown.
        act.Should().NotThrow();
    }

    [Fact]
    public void Ctor_WithNullLoggerAndNullPath_ReturnsEmptyList()
    {
        // Arrange + Act: even with null logger and null path, the database must
        // load without throwing. RoutineDatabase has NO built-in defaults
        // (routines are 100% OEM-defined), so All is expected to be empty.
        var db = new RoutineDatabase(userJsonPath: null, logger: null);

        // Assert: no built-in defaults, but no exception either.
        db.All.Should().BeEmpty();
    }
}
