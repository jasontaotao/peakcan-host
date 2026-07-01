using FluentAssertions;
using PeakCan.Host.Core.Uds.Database;

namespace PeakCan.Host.Core.Tests.Uds.Database;

public class DatabaseAddRangeTests
{
    [Fact]
    public void DidDatabase_AddRange_NewDids_AppendsToAll()
    {
        // Arrange — IDs 0x9001 / 0x9002 NOT in BuiltInDefaults (F190/F187/F18A/F191/F184).
        // Dedup behavior is exercised in the Duplicate test below.
        var db = new DidDatabase(userJsonPath: null, logger: null);
        var before = db.All.Count;
        var newDids = new[]
        {
            new DidDefinition(0x9001, "ODX_DID_A", "ODX imported DID A", 4, true),
            new DidDefinition(0x9002, "ODX_DID_B", "ODX imported DID B", 8, false),
        };

        // Act
        db.AddRange(newDids, out _);

        // Assert
        db.All.Should().HaveCount(before + 2);
        db.Find(0x9001).Should().NotBeNull();
        db.Find(0x9002).Should().NotBeNull();
    }

    [Fact]
    public void DidDatabase_AddRange_DuplicateId_LastWins()
    {
        // Arrange
        var db = new DidDatabase(userJsonPath: null, logger: null);
        var newDids = new[]
        {
            new DidDefinition(0xF190, "newVIN", "new", 18, true),
        };

        // Act
        db.AddRange(newDids, out var warnings);

        // Assert
        db.Find(0xF190).Should().NotBeNull();
        db.Find(0xF190)!.Name.Should().Be("newVIN");
        warnings.Should().Contain(w => w.Contains("0xF190"));
        warnings.Should().Contain(w => w.Contains("0xF190"));
    }

    [Fact]
    public void RoutineDatabase_AddRange_NewRoutines_AppendsToAll()
    {
        // Arrange — use IDs unlikely to collide with any built-in routines.
        var db = new RoutineDatabase(userJsonPath: null, logger: null);
        var before = db.All.Count;
        var newRoutines = new[]
        {
            new RoutineDefinition(0xF200, "TestR1", "Idle", true, true),
            new RoutineDefinition(0xF201, "TestR2", "Idle", true, true),
        };

        // Act
        db.AddRange(newRoutines, out _);

        // Assert
        db.All.Should().HaveCount(before + 2);
        db.Find(0xF200).Should().NotBeNull();
        db.Find(0xF201).Should().NotBeNull();
    }
}
