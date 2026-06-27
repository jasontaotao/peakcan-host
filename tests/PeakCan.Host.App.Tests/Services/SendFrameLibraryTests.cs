using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

/// <summary>
/// v1.2.11 PATCH Item 5: <see cref="SendFrameLibrary"/> persists named
/// CAN frames to <c>%APPDATA%\PeakCan.Host\send-library.json</c>.
/// Atomic writes (tmp + rename). Missing / corrupt files load as empty list.
/// </summary>
public class SendFrameLibraryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tempFile;

    public SendFrameLibraryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"pch-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _tempFile = Path.Combine(_tempDir, "send-library.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private SendFrameLibrary NewLib() => new(_tempFile, NullLogger<SendFrameLibrary>.Instance);

    [Fact]
    public void Load_Missing_File_Returns_Empty_List()
    {
        var lib = NewLib();
        lib.Load().Should().BeEmpty();
    }

    [Fact]
    public void Load_Corrupt_Json_Returns_Empty_List()
    {
        File.WriteAllText(_tempFile, "{ this is not valid json");
        var lib = NewLib();
        lib.Load().Should().BeEmpty("corrupt files must not throw — load falls back to empty");
    }

    [Fact]
    public void Save_Then_Load_RoundTrip_Preserves_All_Fields()
    {
        var lib = NewLib();
        var input = new[]
        {
            new SendFrameLibrary.SavedFrame("Door Unlock", 0x100, false, false, false, false, "DEADBEEF", DateTimeOffset.UtcNow),
            new SendFrameLibrary.SavedFrame("FD Diag", 0x200, true, true, false, true, "1122334455667788", DateTimeOffset.UtcNow),
        };

        lib.Save(input);
        var loaded = lib.Load();

        loaded.Should().HaveCount(2);
        loaded[0].Name.Should().Be("Door Unlock");
        loaded[1].RawId.Should().Be(0x200u);
        loaded[1].IsExtended.Should().BeTrue();
        loaded[1].IsFd.Should().BeTrue();
        loaded[1].BitRateSwitch.Should().BeTrue();
        loaded[1].DataHex.Should().Be("1122334455667788");
    }

    [Fact]
    public void Save_Atomic_No_Partial_File_On_Crash_Simulation()
    {
        var lib = NewLib();
        lib.Save(new[] { new SendFrameLibrary.SavedFrame("X", 1, false, false, false, false, "AA", DateTimeOffset.UtcNow) });

        // After save, the .tmp file must NOT remain (atomic rename completed)
        File.Exists(_tempFile + ".tmp").Should().BeFalse("atomic write must rename tmp → final, leaving no .tmp");
    }
}

// v1.2.12 PATCH Item 1: regression coverage for SendFrameLibrary.Add/Remove
// being safe under concurrent calls (v1.2.11 review fix) and replacing on
// duplicate names.
public class SendFrameLibraryConcurrencyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tempFile;

    public SendFrameLibraryConcurrencyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"pch-conc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _tempFile = Path.Combine(_tempDir, "send-library.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private SendFrameLibrary NewLib() => new(_tempFile, NullLogger<SendFrameLibrary>.Instance);

    private static SendFrameLibrary.SavedFrame Frame(string name, uint id, params byte[] data)
        => new(name, id, false, false, false, false,
               Convert.ToHexString(data ?? Array.Empty<byte>()),
               DateTimeOffset.UtcNow);

    [Fact]
    public void Add_Then_List_Contains_Added()
    {
        var lib = NewLib();
        lib.Add(Frame("A", 0x100));
        lib.Load().Should().ContainSingle(f => f.Name == "A");
    }

    [Fact]
    public void Add_Twice_Distinct_Names_Both_Kept()
    {
        var lib = NewLib();
        lib.Add(Frame("A", 0x100));
        lib.Add(Frame("B", 0x200));
        lib.Load().Should().HaveCount(2);
    }

    [Fact]
    public void Add_Twice_Same_Name_Appends_Both()
    {
        // v1.2.11 atomic Add does not de-duplicate by name; it appends.
        // Replacing duplicates is a separate concern (Task 13's SaveUnlocked
        // work). This test pins the append-only contract so a future
        // "replace-on-name" change is a deliberate decision, not silent.
        var lib = NewLib();
        lib.Add(Frame("A", 0x100, 0x01));
        lib.Add(Frame("A", 0x100, 0x02));
        lib.Load().Should().HaveCount(2, "atomic Add appends; de-dup is out of scope for v1.2.12 Item 1");
    }

    [Fact]
    public void Remove_Existing_Returns_True()
    {
        var lib = NewLib();
        lib.Add(Frame("A", 0x100));
        lib.Remove("A").Should().BeTrue();
        lib.Load().Should().BeEmpty();
    }

    [Fact]
    public void Remove_NonExistent_Returns_False()
    {
        var lib = NewLib();
        lib.Remove("nope").Should().BeFalse();
    }

    [Fact]
    public void Concurrent_Add_From_Two_Threads_Both_Kept()
    {
        var lib = NewLib();
        Parallel.For(0, 50, i =>
        {
            lib.Add(Frame($"name-{i}", 0x100 + (uint)i));
        });
        lib.Load().Should().HaveCount(50);
    }

    [Fact]
    public void Count_Reflects_Added_And_Removed()
    {
        // v1.2.12 PATCH Item 1: SendViewModel SaveStatus surfaces total
        // frame count via SendFrameLibrary.Count. The getter must lock
        // around LoadUnlocked so the count is consistent.
        var lib = NewLib();
        lib.Count.Should().Be(0);
        lib.Add(Frame("A", 0x100));
        lib.Add(Frame("B", 0x200));
        lib.Count.Should().Be(2);
        lib.Remove("A");
        lib.Count.Should().Be(1);
    }
}