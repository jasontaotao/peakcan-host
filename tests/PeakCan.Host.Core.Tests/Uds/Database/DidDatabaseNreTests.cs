using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Core.Uds.Database;

namespace PeakCan.Host.Core.Tests.Uds.Database;

public class DidDatabaseNreTests
{
    [Fact]
    public void Ctor_WithNullLogger_DoesNotThrow()
    {
        // Arrange: pass null logger (the "do not log anything" case).
        // Spec §9.1: this must NOT throw — the logger parameter is optional.

        // Act
        Action act = () => _ = new DidDatabase(userJsonPath: null, logger: null);

        // Assert: no exception thrown.
        act.Should().NotThrow();
    }

    [Fact]
    public void Ctor_WithNullLoggerAndMissingPath_DoesNotThrow()
    {
        // Arrange: 1-arg ctor path-equivalent — non-null path that points at nothing.
        // This is the path taken by the legacy DidDatabase(ILogger?) ctor.

        // Act
        Action act = () => _ = new DidDatabase(
            userJsonPath: Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.json"),
            logger: null);

        // Assert: no exception thrown.
        act.Should().NotThrow();
    }

    [Fact]
    public void Ctor_WithNullLoggerAndValidBuiltins_ReturnsBuiltins()
    {
        // Arrange + Act: even with null logger, the built-in defaults must be loaded.
        var db = new DidDatabase(userJsonPath: null, logger: null);

        // Assert: built-in defaults are present (at least the spec-known seed DID exists).
        db.All.Should().NotBeEmpty();
    }
}
