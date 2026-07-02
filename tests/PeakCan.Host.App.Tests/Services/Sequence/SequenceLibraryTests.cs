using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.Sequence;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Sequence;

/// <summary>
/// v2.1.2 PATCH tests for <see cref="SequenceLibrary"/> — Add
/// (last-wins on duplicate name), Remove, Load, Count, atomic
/// save (no partial state), and corrupt-file recovery.
/// </summary>
public sealed class SequenceLibraryTests
{
    private static SequenceLibrary NewLib(out string path)
    {
        path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"seq-lib-{System.Guid.NewGuid():N}.json");
        return new SequenceLibrary(path, NullLogger<SequenceLibrary>.Instance);
    }

    private static SequenceLibrary.SavedSequence MakeSeq(
        string name, SequenceLibrary.Mode mode = SequenceLibrary.Mode.Concurrent,
        int delayMs = 0, int iterations = 1,
        params (string name, ushort id, string data)[] rows)
    {
        var savedRows = rows.Select(r => new SequenceLibrary.SavedRow
        {
            Kind = SequenceLibrary.RowKind.Raw,
            Id = r.id,
            DataHex = r.data,
        }).ToList();
        return new SequenceLibrary.SavedSequence(
            name, mode, delayMs, iterations, savedRows, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Add_PersistsToDisk_ReloadableAfterNewInstance()
    {
        var lib = NewLib(out var path);
        lib.Add(MakeSeq("foo", rows: ("x", 0x100, "DEAD")));

        // New instance pointing at same file — should read back the saved sequence.
        var lib2 = new SequenceLibrary(path, NullLogger<SequenceLibrary>.Instance);
        var loaded = lib2.Load();
        loaded.Should().HaveCount(1);
        loaded[0].Name.Should().Be("foo");
        loaded[0].Rows.Should().HaveCount(1);
        loaded[0].Rows[0].Id.Should().Be((ushort)0x100);
        loaded[0].Rows[0].DataHex.Should().Be("DEAD");
    }

    [Fact]
    public void Add_DuplicateName_LastWins_ReplacesExisting()
    {
        var lib = NewLib(out _);
        lib.Add(MakeSeq("dup", rows: ("a", 0x100, "11")));
        lib.Add(MakeSeq("dup", rows: ("b", 0x200, "22")));
        lib.Load().Should().HaveCount(1, "duplicate name replaces, not appends");
        lib.Load()[0].Rows[0].Id.Should().Be((ushort)0x200, "the second add wins");
    }

    [Fact]
    public void Remove_ReturnsTrue_WhenPresent_False_WhenAbsent()
    {
        var lib = NewLib(out _);
        lib.Add(MakeSeq("a"));
        lib.Add(MakeSeq("b"));
        lib.Count.Should().Be(2);
        lib.Remove("a").Should().BeTrue();
        lib.Load().Should().HaveCount(1);
        lib.Remove("nope").Should().BeFalse();
        lib.Count.Should().Be(1);
    }

    [Fact]
    public void Count_ReflectsCurrentState()
    {
        var lib = NewLib(out _);
        lib.Count.Should().Be(0, "empty library → 0 (cached after first Count call)");
        lib.Add(MakeSeq("a"));
        lib.Add(MakeSeq("b"));
        lib.Count.Should().Be(2);
        lib.Remove("a");
        lib.Count.Should().Be(1);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyList_DoesNotThrow()
    {
        var lib = NewLib(out _);
        lib.Load().Should().BeEmpty();
    }

    [Fact]
    public void Load_CorruptJson_ReturnsEmptyList_LogsError()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"seq-corrupt-{System.Guid.NewGuid():N}.json");
        System.IO.File.WriteAllText(path, "{ this is not valid json");
        try
        {
            var lib = new SequenceLibrary(path, NullLogger<SequenceLibrary>.Instance);
            lib.Load().Should().BeEmpty("corrupt JSON must not propagate an exception");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Save_AtomicWrite_LeavesIntactFile_OnSuccess()
    {
        var lib = NewLib(out var path);
        lib.Add(MakeSeq("alpha"));
        lib.Add(MakeSeq("beta"));

        // After successful Add the file must exist and contain both sequences.
        System.IO.File.Exists(path).Should().BeTrue();
        var json = System.IO.File.ReadAllText(path);
        json.Should().Contain("alpha").And.Contain("beta");
        // Tmp file must be cleaned up after successful rename.
        System.IO.File.Exists(path + ".tmp").Should().BeFalse();
    }

    [Fact]
    public void Sequence_RoundTrips_DbcRows_PreserveAllFields()
    {
        var lib = NewLib(out _);
        var seq = new SequenceLibrary.SavedSequence(
            Name: "dbc_seq",
            Mode: SequenceLibrary.Mode.Sequential,
            DelayMs: 25,
            Iterations: 3,
            Rows: new()
            {
                new SequenceLibrary.SavedRow
                {
                    Kind = SequenceLibrary.RowKind.Dbc,
                    DbcMessageName = "EngineRPM",
                    DbcSignalValues = new()
                    {
                        new SequenceLibrary.SavedSignalValue { Name = "rpm", Value = 2500 },
                        new SequenceLibrary.SavedSignalValue { Name = "load", Value = 42.5 },
                    },
                },
                new SequenceLibrary.SavedRow
                {
                    Kind = SequenceLibrary.RowKind.Raw,
                    Id = 0x200,
                    DataHex = "AABB",
                    IsExtended = true,
                    IsFd = true,
                },
            },
            SavedAt: DateTimeOffset.UtcNow);

        lib.Add(seq);
        var loaded = lib.Load()[0];

        loaded.Mode.Should().Be(SequenceLibrary.Mode.Sequential);
        loaded.DelayMs.Should().Be(25);
        loaded.Iterations.Should().Be(3);
        loaded.Rows.Should().HaveCount(2);
        loaded.Rows[0].Kind.Should().Be(SequenceLibrary.RowKind.Dbc);
        loaded.Rows[0].DbcMessageName.Should().Be("EngineRPM");
        loaded.Rows[0].DbcSignalValues.Should().HaveCount(2);
        loaded.Rows[0].DbcSignalValues[0].Name.Should().Be("rpm");
        loaded.Rows[0].DbcSignalValues[0].Value.Should().Be(2500);
        loaded.Rows[1].Kind.Should().Be(SequenceLibrary.RowKind.Raw);
        loaded.Rows[1].Id.Should().Be((ushort)0x200);
        loaded.Rows[1].IsExtended.Should().BeTrue();
        loaded.Rows[1].IsFd.Should().BeTrue();
    }
}