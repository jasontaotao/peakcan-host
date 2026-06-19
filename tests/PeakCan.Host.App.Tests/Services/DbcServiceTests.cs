using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Tests.Services;

/// <summary>
/// Task 15: verifies the <see cref="DbcService"/> async load pipeline.
/// <para>
/// <see cref="DbcService.LoadAsync"/> is made <c>virtual</c> so tests can
/// override the file-read + parse path with a stub returning a fixed
/// <see cref="DbcDocument"/>. The default path is covered by the
/// <c>LoadAsync_Valid_Dbc_Temp_File_Fires_DbcLoaded_With_NonNull_Document</c>
/// test which writes a real (tiny) DBC to a temp file.
/// </para>
/// </summary>
public class DbcServiceTests
{
    private static DbcService NewSvc() => new(NullLogger<DbcService>.Instance);

    [Fact]
    public void Current_Defaults_To_Null()
    {
        var svc = NewSvc();
        svc.Current.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_Nonexistent_Path_Fires_LoadFailed_With_IoError()
    {
        var svc = NewSvc();
        Error? captured = null;
        svc.LoadFailed += e => captured = e;

        await svc.LoadAsync("/this/path/does/not/exist/nope.dbc");

        captured.Should().NotBeNull();
        captured!.Code.Should().Be(ErrorCode.IoError);
    }

    [Fact]
    public async Task LoadAsync_Cancelled_Does_Not_Fire_LoadFailed()
    {
        // Use the default path (real file read) but pre-cancel the token.
        // The LoadFailed event must NOT fire on OperationCanceledException —
        // cancellation is a caller-initiated abort, not an error.
        var svc = NewSvc();
        Error? captured = null;
        svc.LoadFailed += e => captured = e;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await svc.LoadAsync("/tmp/nope.dbc", cts.Token);

        captured.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_Valid_Dbc_Temp_File_Fires_DbcLoaded_With_NonNull_Document()
    {
        // Tiny but valid DBC: one BU_ node, one BO_ with no signals, ; terminated.
        var path = Path.Combine(Path.GetTempPath(), $"peakcan-dbc-test-{Guid.NewGuid():N}.dbc");
        try
        {
            await File.WriteAllTextAsync(path,
                "VERSION \"\"\n" +
                "\n" +
                "NS_ :\n" +
                "\n" +
                "BS_ :\n" +
                "\n" +
                "BU_: ECU1\n" +
                "\n" +
                "BO_ 256 Msg: 8 ECU1\n");
            var svc = NewSvc();
            DbcDocument? captured = null;
            svc.DbcLoaded += doc => captured = doc;

            await svc.LoadAsync(path);

            captured.Should().NotBeNull();
            captured!.Messages.Should().HaveCount(1);
            svc.Current.Should().NotBeNull();
            svc.Current!.Messages.Should().HaveCount(1);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_Sets_Current_Before_DbcLoaded_Event_Fires()
    {
        // Contract: observers may rely on Current being non-null when
        // DbcLoaded fires. Assert via a subscriber that reads Current
        // synchronously inside the handler.
        var path = Path.Combine(Path.GetTempPath(), $"peakcan-dbc-test-{Guid.NewGuid():N}.dbc");
        try
        {
            await File.WriteAllTextAsync(path,
                "VERSION \"\"\nNS_ :\nBS_ :\nBU_: ECU1\nBO_ 100 M1: 4 ECU1\n");
            var svc = NewSvc();
            DbcDocument? currentAtEventTime = null;
            svc.DbcLoaded += _ => currentAtEventTime = svc.Current;

            await svc.LoadAsync(path);

            currentAtEventTime.Should().NotBeNull();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}