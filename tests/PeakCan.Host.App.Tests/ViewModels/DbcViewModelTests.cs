using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// Task 15: <see cref="DbcViewModel"/> owns the DBC tab's grid + status
/// surface. It is wired to <see cref="DbcService"/> via the
/// <c>DbcLoaded</c> + <c>LoadFailed</c> events.
/// <para>
/// <b>v0.7.0 IFileDialogService:</b> the previously-skipped
/// <c>OpenAsync_When_User_Cancels_Dialog_Does_Nothing</c> test is now
/// enabled by injecting a fake <see cref="IFileDialogService"/> that
/// simulates user cancellation.
/// </para>
/// </summary>
public class DbcViewModelTests
{
    /// <summary>
    /// Test double for <see cref="IFileDialogService"/> that returns
    /// a configurable path (or null to simulate cancellation).
    /// </summary>
    private sealed class FakeFileDialogService : IFileDialogService
    {
        public string? NextResult { get; set; }
        public string? ShowOpenDialog(string filter) => NextResult;
    }

    private static DbcViewModel NewVm(DbcService svc, IFileDialogService? fileDialog = null)
        => new(svc, new SignalViewModel(), NullLogger<DbcViewModel>.Instance, fileDialog);

    [Fact]
    public void Default_Status_Is_No_Dbc_Loaded()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var vm = NewVm(svc);
        vm.Status.Should().Be("No DBC loaded");
        vm.LoadedPath.Should().BeEmpty();
    }

    [Fact]
    public void Default_Messages_Is_Empty()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var vm = NewVm(svc);
        vm.Messages.Should().BeEmpty();
    }

    [Fact]
    public void DbcLoaded_Event_Populates_Messages_And_Sets_Status()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var vm = NewVm(svc);
        var doc = new DbcDocument(
            Version: "",
            Nodes: new List<Node>(),
            Messages: new List<Message>
            {
                new Message(0x100, "M1", 8, "ECU1", new List<Signal>(), false, null),
                new Message(0x200, "M2", 4, "ECU2", new List<Signal>(), false, null),
            },
            MessagesById: new Dictionary<uint, Message>(),
            ValueTables: new Dictionary<string, ValueTable>());

        svc.GetType().GetEvent(nameof(DbcService.DbcLoaded))!
            .RaiseMethod(svc, doc);

        vm.Messages.Should().HaveCount(2);
        vm.Messages[0].Name.Should().Be("M1");
        vm.Messages[1].Name.Should().Be("M2");
        vm.Status.Should().Contain("Loaded 2 messages");
    }

    [Fact]
    public void LoadFailed_Event_Sets_Status_To_FAIL_Message()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var vm = NewVm(svc);

        svc.GetType().GetEvent(nameof(DbcService.LoadFailed))!
            .RaiseMethod(svc, new Error(ErrorCode.IoError, "missing file"));

        vm.Status.Should().StartWith("FAIL:");
        vm.Status.Should().Contain("missing file");
    }

    [Fact]
    public void DbcLoaded_Event_Resets_SignalViewModel_Latest()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var signals = new SignalViewModel();
        signals.Latest.Add(new SignalEntry { Message = "OldM", Signal = "OldS" });
        signals.Latest.Should().HaveCount(1, "precondition");

        var vm = new DbcViewModel(svc, signals, NullLogger<DbcViewModel>.Instance);
        var doc = new DbcDocument(
            Version: "",
            Nodes: new List<Node>(),
            Messages: new List<Message>(),
            MessagesById: new Dictionary<uint, Message>(),
            ValueTables: new Dictionary<string, ValueTable>());

        svc.GetType().GetEvent(nameof(DbcService.DbcLoaded))!
            .RaiseMethod(svc, doc);

        signals.Latest.Should().BeEmpty("DbcLoaded must clear the decoded-signal table");
    }

    [Fact]
    public async Task OpenAsync_When_User_Cancels_Dialog_Does_Nothing()
    {
        // v0.7.0: previously skipped because OpenFileDialog requires STA.
        // Now enabled via IFileDialogService fake that returns null.
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var dialog = new FakeFileDialogService { NextResult = null };
        var vm = NewVm(svc, dialog);

        await vm.OpenCommand.ExecuteAsync(null);

        vm.Status.Should().Be("No DBC loaded", "cancel should leave VM unchanged");
        vm.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenAsync_With_FakeDialog_Loads_File()
    {
        // v0.7.0: end-to-end OpenCommand coverage via fake dialog.
        // Write a tiny DBC to a temp file, point the fake at it.
        var path = Path.Combine(Path.GetTempPath(), $"peakcan-test-{Guid.NewGuid():N}.dbc");
        try
        {
            await File.WriteAllTextAsync(path,
                "VERSION \"\"\nNS_ :\nBS_ :\nBU_: ECU1\nBO_ 256 Msg: 8 ECU1\n");
            var svc = new DbcService(NullLogger<DbcService>.Instance);
            var dialog = new FakeFileDialogService { NextResult = path };
            var vm = NewVm(svc, dialog);

            await vm.OpenCommand.ExecuteAsync(null);

            vm.Messages.Should().HaveCount(1);
            vm.Messages[0].Name.Should().Be("Msg");
            vm.Status.Should().Contain("Loaded 1 message");
            vm.LoadedPath.Should().Be(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

/// <summary>
/// Test helper: raise an event on an arbitrary object via reflection. Avoids
/// exposing public raise helpers on production types.
/// </summary>
internal static class EventRaiseExtensions
{
    public static void RaiseMethod<TArgs>(this System.Reflection.EventInfo ev, object target, TArgs args)
    {
        var field = ev.DeclaringType!.GetField(ev.Name,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Public);
        if (field == null)
        {
            // Try the auto-event backing field (k__BackingField on a public
            // event with a public delegate field fallback). The MVVM-style
            // public event Action<T> field is itself a multicast delegate
            // we can read directly.
            // For Task 15 DbcService, the events are public delegate fields
            // named exactly `DbcLoaded` / `LoadFailed`. They should already
            // match the simple field-getter path above.
            throw new InvalidOperationException($"No backing field for event {ev.Name}");
        }
        var del = (Delegate)field.GetValue(target)!;
        del.DynamicInvoke(args);
    }
}