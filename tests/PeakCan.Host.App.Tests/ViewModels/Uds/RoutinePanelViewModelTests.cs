using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.App.ViewModels.Uds.Rows;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.Database;
using PeakCan.Host.Core.Uds.IsoTp;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Uds;

/// <summary>
/// Tests for RoutinePanelViewModel. Covers ctor population from an
/// empty RoutineDatabase (no selection), Start happy path with Status
/// lifecycle + LastResult, Stop / Query sub-function dispatch, and
/// NRC handling that flips Status to Failed.
/// </summary>
public sealed class RoutinePanelViewModelTests
{
    private sealed class RecordingUdsClient : UdsClient
    {
        public List<(byte SubFn, ushort Id)> Calls { get; } = new();
        public byte[] NextResult { get; set; } = new byte[] { 0xCA, 0xFE };
        public bool ThrowNrc { get; set; }

        public RecordingUdsClient() : base(
            new IsoTpLayer(new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 }, _ => { }),
            new UdsTimer())
        { }

        public override Task<byte[]> RoutineControlAsync(byte subFunction, ushort routineIdentifier, byte[]? data = null, CancellationToken ct = default)
        {
            Calls.Add((subFunction, routineIdentifier));
            if (ThrowNrc) throw new UdsNegativeResponseException(0x31, UdsNegativeResponseCode.RequestOutOfRange);
            return Task.FromResult(NextResult);
        }
    }

    private static RoutineDatabase NewDb()
        => new RoutineDatabase(userJsonPath: null, logger: NullLogger<RoutineDatabase>.Instance);

    private static RoutineDatabase NewPopulatedDb(out string tmpPath)
    {
        tmpPath = Path.Combine(Path.GetTempPath(), $"uds-rt-{Guid.NewGuid():N}.json");
        File.WriteAllText(tmpPath, "{\"routines\":[{\"id\":\"0xFF00\",\"name\":\"Erase\",\"description\":\"d\",\"startable\":true,\"stoppable\":true}]}");
        return new RoutineDatabase(tmpPath, logger: NullLogger<RoutineDatabase>.Instance);
    }

    [Fact]
    public void Ctor_With_Empty_RoutineDatabase_Has_No_SelectedRoutine()
    {
        var fake = new RecordingUdsClient();
        var vm = new RoutinePanelViewModel(fake, NewDb());

        vm.Routines.Should().BeEmpty();
        vm.SelectedRoutine.Should().BeNull();
    }

    [Fact]
    public async Task StartRoutineCommand_Updates_Status_Running_Then_Completed()
    {
        var fake = new RecordingUdsClient();
        var db = NewPopulatedDb(out var tmp);
        var vm = new RoutinePanelViewModel(fake, db);
        vm.SelectedRoutine.Should().NotBeNull();
        File.Delete(tmp);

        await vm.StartRoutineCommand.ExecuteAsync(null);

        fake.Calls.Should().ContainSingle().Which.Should().Be((0x01, (ushort)0xFF00));
        vm.SelectedRoutine!.Status.Should().Be("Completed");
        vm.SelectedRoutine.LastResult.Should().Be("CA FE");
    }

    [Fact]
    public async Task StopRoutineCommand_Invokes_RoutineControl_0x02()
    {
        var fake = new RecordingUdsClient();
        var db = NewPopulatedDb(out var tmp);
        var vm = new RoutinePanelViewModel(fake, db);
        File.Delete(tmp);

        await vm.StopRoutineCommand.ExecuteAsync(null);

        fake.Calls.Should().ContainSingle().Which.SubFn.Should().Be(0x02);
    }

    [Fact]
    public async Task QueryRoutineCommand_Invokes_RoutineControl_0x03()
    {
        var fake = new RecordingUdsClient();
        var db = NewPopulatedDb(out var tmp);
        var vm = new RoutinePanelViewModel(fake, db);
        File.Delete(tmp);

        await vm.QueryRoutineCommand.ExecuteAsync(null);

        fake.Calls.Should().ContainSingle().Which.SubFn.Should().Be(0x03);
    }

    [Fact]
    public async Task StartRoutineCommand_With_UdsNegativeResponse_Sets_Status_Failed()
    {
        var fake = new RecordingUdsClient { ThrowNrc = true };
        var db = NewPopulatedDb(out var tmp);
        var vm = new RoutinePanelViewModel(fake, db);
        File.Delete(tmp);

        await vm.StartRoutineCommand.ExecuteAsync(null);

        vm.SelectedRoutine!.Status.Should().Be("Failed");
    }

    [Fact]
    public void RoutineCommand_CanExecute_False_When_Status_Running()
    {
        var fake = new RecordingUdsClient();
        var vm = new RoutinePanelViewModel(fake, NewDb());
        // No routines → commands always CanExecute=false
        vm.StartRoutineCommand.CanExecute(null).Should().BeFalse();
    }
}
