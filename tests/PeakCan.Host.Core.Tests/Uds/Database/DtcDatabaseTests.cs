using FluentAssertions;
using PeakCan.Host.Core.Uds.Database;

namespace PeakCan.Host.Core.Tests.Uds.Database;

public class DtcDatabaseTests
{
    [Fact]
    public void Ctor_Empty_HasNoDtcs()
    {
        // Arrange + Act
        var db = new DtcDatabase();

        // Assert
        db.All.Should().BeEmpty();
    }

    [Fact]
    public void AddRange_NewCodes_PopulatesAll()
    {
        // Arrange
        var db = new DtcDatabase();
        var defs = new[]
        {
            new DtcDefinition(0x1, "P0001", "X", 0x2F),
            new DtcDefinition(0x2, "P0002", "Y", 0x2F),
        };

        // Act
        db.AddRange(defs, out var warnings);

        // Assert
        db.All.Should().HaveCount(2);
        db.FindByCode(0x1).Should().NotBeNull();
        db.FindByCode(0x2).Should().NotBeNull();
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void AddRange_DuplicateCode_LastWins()
    {
        // Arrange
        var db = new DtcDatabase(new[]
        {
            new DtcDefinition(0x1, "old", "old desc", 0x0F),
        });
        var dup = new DtcDefinition(0x1, "new", "new desc", 0x2F);

        // Act
        db.AddRange(new[] { dup }, out var warnings);

        // Assert
        db.All.Should().ContainSingle(); // count unchanged
        db.FindByCode(0x1)!.Value.ShortName.Should().Be("new");
        warnings.Should().Contain(w => w.Contains("0x1")); // warning about duplicate
    }

    [Fact]
    public void FindByCode_Missing_ReturnsNull()
    {
        // Arrange
        var db = new DtcDatabase();

        // Act
        var result = db.FindByCode(0x999);

        // Assert
        result.Should().BeNull();
    }
}
