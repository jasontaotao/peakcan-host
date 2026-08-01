using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using DbcValueType = PeakCan.Host.Core.Dbc.ValueType;

namespace PeakCan.Host.App.Tests.ViewModels;

public class HilStudioViewModelTests
{
    private sealed class FakeFileDialogService : IFileDialogService
    {
        public string? NextResult { get; set; }
        public string? ShowOpenDialog(string filter) => NextResult;
        public string? ShowSaveDialog(string filter, string? defaultExt, string? initialDirectory) => NextResult;
    }

    private static HilStudioViewModel NewVm(DbcService svc, IFileDialogService? fileDialog = null)
        => new(svc, NullLogger<HilStudioViewModel>.Instance, fileDialog);

    private static DbcDocument DocWith(params Message[] messages) => new(
        Version: "", Nodes: new List<Node>(),
        Messages: messages, MessagesById: new Dictionary<uint, Message>(),
        ValueTables: new Dictionary<string, ValueTable>(),
        SourcePath: @"C:\test\example.dbc");

    private static void RaiseLoaded(DbcService svc, DbcDocument doc)
        => svc.GetType().GetEvent(nameof(DbcService.DbcLoaded))!.RaiseMethod(svc, doc);

    [Fact]
    public void Default_Status_Is_No_Dbc_Loaded()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var vm = NewVm(svc);

        vm.Status.Should().Be("No DBC loaded");
        vm.LoadedPath.Should().BeEmpty();
        vm.TotalMessages.Should().Be(0);
    }

    [Fact]
    public void DbcLoaded_Event_Populates_Messages_And_Counts()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var vm = NewVm(svc);
        var doc = DocWith(
            new Message(0x100, "M1", 8, "ECU1",
                new List<Signal> { new("S1", 0, 8, ByteOrder.LittleEndian, DbcValueType.Unsigned, 1, 0, 0, 255, "", Array.Empty<string>()) },
                IsMultiplexed: false, MultiplexorSignalIndex: null),
            new Message(0x200, "M2", 4, "ECU2", new List<Signal>(), false, null));

        RaiseLoaded(svc, doc);

        vm.Messages.Should().HaveCount(2);
        vm.FilteredMessages.Should().HaveCount(2);
        vm.TotalMessages.Should().Be(2);
        vm.TotalSignals.Should().Be(1);
        vm.LoadedPath.Should().Be(@"C:\test\example.dbc");
        vm.Status.Should().Contain("Loaded 2 messages");
    }

    [Fact]
    public void LoadFailed_Event_Sets_Status_To_FAIL()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var vm = NewVm(svc);

        svc.GetType().GetEvent(nameof(DbcService.LoadFailed))!
            .RaiseMethod(svc, new Error(ErrorCode.IoError, "missing file"));

        vm.Status.Should().StartWith("FAIL:");
        vm.Status.Should().Contain("missing file");
    }

    [Fact]
    public void Reload_Clears_And_Repopulates()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var vm = NewVm(svc);
        RaiseLoaded(svc, DocWith(new Message(0x100, "M1", 8, "ECU1", new List<Signal>(), false, null)));

        RaiseLoaded(svc, DocWith());

        vm.Messages.Should().BeEmpty();
        vm.TotalMessages.Should().Be(0);
    }

    [Fact]
    public void RefreshFromCurrent_Seeds_From_Service_Without_Event()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var doc = DocWith(new Message(0x100, "M1", 8, "ECU1", new List<Signal>(), false, null));
        svc.SetCurrentForTests(doc);
        var vm = NewVm(svc);

        vm.RefreshFromCurrent();

        vm.Messages.Should().HaveCount(1);
        vm.Messages[0].Name.Should().Be("M1");
    }

    [Fact]
    public async Task OpenAsync_When_User_Cancels_Does_Nothing()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var dialog = new FakeFileDialogService { NextResult = null };
        var vm = NewVm(svc, dialog);

        await vm.OpenCommand.ExecuteAsync(null);

        vm.Status.Should().Be("No DBC loaded");
    }
}
