using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
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

    [Fact]
    public void Count_After_N_Adds_Equals_N_Concurrent_With_Count()
    {
        // v1.2.13 PATCH Item 7: Count must be O(1) and consistent with
        // concurrent Add/Remove. Hammer 200 mixed ops across 4 threads
        // and check that the cache stays in sync with on-disk state.
        var lib = NewLib();
        Parallel.For(0, 200, i =>
        {
            var op = i % 4;
            switch (op)
            {
                case 0:
                    lib.Add(Frame($"add-{i}", 0x100 + (uint)(i & 0xFF)));
                    break;
                case 1:
                    _ = lib.Count;
                    break;
                case 2:
                    lib.Add(Frame($"add2-{i}", 0x200 + (uint)(i & 0xFF)));
                    break;
                case 3:
                    lib.Remove($"add-{i - 1}");
                    break;
            }
        });

        // Final ground-truth read via Load (bypasses cache on purpose) so
        // the test asserts cache correctness, not self-consistency with
        // the cache it is verifying.
        var groundTruth = lib.Load().Count;
        lib.Count.Should().Be(groundTruth,
            "cached Count must stay in sync with the on-disk library after mixed ops");
    }

    [Fact]
    public void Count_Before_Any_Mutation_Loads_File_Once()
    {
        // v1.2.13 PATCH Item 7: Count should use a lazy cached field
        // populated by EnsureLoaded. The first read on a fresh instance
        // is the only cache miss; subsequent reads reuse the cached value.
        File.WriteAllText(_tempFile,
            "{\"version\":1,\"frames\":[" +
            "{\"Name\":\"A\",\"RawId\":256,\"IsExtended\":false,\"IsFd\":false," +
            "\"IsRtr\":false,\"BitRateSwitch\":false,\"DataHex\":\"01\",\"SavedAt\":\"2026-01-01T00:00:00+00:00\"}]}");

        var lib = NewLib();
        lib.CacheMissesForTesting.Should().Be(0,
            "no method has been called yet, so EnsureLoaded has not run");

        _ = lib.Count;
        lib.CacheMissesForTesting.Should().Be(1, "first Count must lazy-load from disk");

        _ = lib.Count;
        _ = lib.Count;
        _ = lib.Count;
        lib.CacheMissesForTesting.Should().Be(1,
            "subsequent Counts must hit the cache without re-reading disk");

        lib.Add(Frame("B", 0x200));
        lib.CacheMissesForTesting.Should().Be(1,
            "Add must keep the cache warm and not re-load from disk");

        _ = lib.Count;
        lib.CacheMissesForTesting.Should().Be(1,
            "Count after Add must still be served from the warm cache");
    }
}

// v1.2.12 PATCH Item 13: SaveUnlocked atomicity (File.Replace + UTF-8 BOM)
// and typed-catch error handling.
public class SendFrameLibrarySaveTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tempFile;

    public SendFrameLibrarySaveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"pch-save-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _tempFile = Path.Combine(_tempDir, "send-library.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static SendFrameLibrary.SavedFrame Frame(string name, uint id, params byte[] data)
        => new(name, id, false, false, false, false,
               Convert.ToHexString(data ?? Array.Empty<byte>()),
               DateTimeOffset.UtcNow);

    [Fact]
    public void Save_Writes_Utf8Bom()
    {
        var lib = new SendFrameLibrary(_tempFile, Substitute.For<ILogger<SendFrameLibrary>>());
        lib.Add(Frame("A", 0x100));

        var bytes = File.ReadAllBytes(_tempFile);
        bytes[0].Should().Be(0xEF, "UTF-8 BOM byte 0 — Notepad must recognize as UTF-8");
        bytes[1].Should().Be(0xBB, "UTF-8 BOM byte 1");
        bytes[2].Should().Be(0xBF, "UTF-8 BOM byte 2");
    }

    [Fact]
    public void Save_Uses_FileReplace_Not_FileMove()
    {
        // Write a known file first so the second save must REPLACE it
        // (File.Move overwrite=true vs File.Replace atomic rename semantics).
        File.WriteAllText(_tempFile, "preexisting");
        var lib = new SendFrameLibrary(_tempFile, Substitute.For<ILogger<SendFrameLibrary>>());
        lib.Add(Frame("A", 0x100));

        // After save, no .tmp file should remain (File.Replace is atomic;
        // it never leaves a visible .tmp once the swap completes).
        var tmpFiles = Directory.GetFiles(_tempDir, Path.GetFileName(_tempFile) + "*.tmp*");
        tmpFiles.Should().BeEmpty("File.Replace completes atomically — no orphan tmp");

        // The file should now contain our JSON, not "preexisting".
        var content = File.ReadAllText(_tempFile);
        content.Should().Contain("\"frames\"", "File.Replace should have replaced the preexisting file");
    }

    [Fact]
    public void Save_Failure_Logs_Error_And_Rethrows()
    {
        // Use a writable parent directory, then mark the destination file
        // ReadOnly so SaveUnlocked fails when trying to File.Replace into
        // it. Add must complete before we flip ReadOnly (Add itself does
        // a load-modify-save).
        var dir = Path.Combine(Path.GetTempPath(), $"pch-fail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "library.json");

        var logger = Substitute.For<ILogger<SendFrameLibrary>>();
        // NSubstitute returns false for IsEnabled by default, which
        // gates the source-generated LoggerMessage call. Force it on.
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);

        var lib = new SendFrameLibrary(path, logger);
        lib.Add(Frame("A", 0x100));

        // Now lock the destination so the next Save fails.
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            var act = () => lib.Save();
            act.Should().Throw<Exception>()
                .Which.Should().Match(e =>
                    e is IOException || e is UnauthorizedAccessException);
            logger.Received().Log(
                LogLevel.Error,
                Arg.Any<EventId>(),
                Arg.Any<object>(),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Save_Failure_Cleans_Tmp_File()
    {
        // Add the frame while the file is still writeable (Add does its
        // own load-modify-save), then mark the destination ReadOnly so
        // the subsequent explicit Save() call fails inside File.Replace.
        var lib = new SendFrameLibrary(_tempFile, Substitute.For<ILogger<SendFrameLibrary>>());
        lib.Add(Frame("A", 0x100));

        File.SetAttributes(_tempFile, FileAttributes.ReadOnly);

        try
        {
            var act = () => lib.Save();
            act.Should().Throw();
        }
        finally
        {
            File.SetAttributes(_tempFile, FileAttributes.Normal);
        }

        var tmpFiles = Directory.GetFiles(_tempDir, "*.tmp*");
        tmpFiles.Should().BeEmpty("typed catch must delete orphaned .tmp on failure");
    }
}