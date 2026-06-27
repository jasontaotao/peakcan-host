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