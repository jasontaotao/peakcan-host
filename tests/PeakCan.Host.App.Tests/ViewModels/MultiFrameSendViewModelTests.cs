using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Threading;
using FluentAssertions;
using PeakCan.Host.App.Models;
using PeakCan.Host.App.Services.MultiFrame;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Core;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// v2.1.0 MINOR: tests for the multi-frame send window's ViewModel.
/// Covers row add/remove/duplicate/move, mode switch (concurrent vs
/// sequential), iteration count, and Send command integration with
/// <see cref="SequenceSendService"/>.
/// </summary>
public sealed class MultiFrameSendViewModelTests
{
    /// <summary>Recording ICanChannel — reused from SequenceSendServiceTests pattern.</summary>
    private sealed class RecordingChannel : ICanChannel
    {
        public ChannelId Id { get; }
        public bool IsConnected { get; private set; }
        public List<CanFrame> Written { get; } = new();
        public RecordingChannel(ChannelId id) { Id = id; IsConnected = true; }
#pragma warning disable CS0067
        public event Action<CanFrame>? FrameReceived;
#pragma warning restore CS0067
        public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
        { IsConnected = true; return Task.FromResult(Result<Unit>.Ok(default)); }
        public Task DisconnectAsync(CancellationToken ct = default)
        { IsConnected = false; return Task.CompletedTask; }
        public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
        { Written.Add(frame); return ValueTask.FromResult(Result<Unit>.Ok(default)); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static MultiFrameSendViewModel NewVm(out RecordingChannel ch, out PeakCan.Host.App.Services.SendService sendSvc)
    {
        ch = new RecordingChannel(ChannelId.None);
        sendSvc = new PeakCan.Host.App.Services.SendService(Microsoft.Extensions.Logging.Abstractions.NullLogger<PeakCan.Host.App.Services.SendService>.Instance);
        sendSvc.ActiveChannel = ch;
        var seqSvc = new SequenceSendService(sendSvc);
        return new MultiFrameSendViewModel(seqSvc);
    }

    [Fact]
    public void Ctor_SeedsWithOneRow()
    {
        var vm = NewVm(out _, out _);
        vm.Rows.Should().HaveCount(1);
        vm.Rows[0].Id.Should().Be((ushort)0x100);
        vm.Rows[0].DataHex.Should().Be("DEADBEEF");
    }

    [Fact]
    public void AddRowCommand_AppendsNewRow_AndSelectsIt()
    {
        var vm = NewVm(out _, out _);
        vm.AddRowCommand.Execute(null);
        vm.Rows.Should().HaveCount(2);
        vm.SelectedRow.Should().BeSameAs(vm.Rows[1]);
    }

    [Fact]
    public void RemoveRowCommand_RemovesSelectedRow()
    {
        var vm = NewVm(out _, out _);
        vm.SelectedRow = vm.Rows[0];
        vm.RemoveRowCommand.Execute(null);
        vm.Rows.Should().BeEmpty();
        vm.SelectedRow.Should().BeNull();
    }

    [Fact]
    public void DuplicateRowCommand_CopiesAllFields()
    {
        var vm = NewVm(out _, out _);
        var src = vm.Rows[0];
        src.IsExtended = true;
        src.IsFd = true;
        src.DataHex = "AABBCCDD";
        vm.SelectedRow = src;

        vm.DuplicateRowCommand.Execute(null);

        vm.Rows.Should().HaveCount(2);
        var dup = vm.Rows[1];
        dup.Id.Should().Be(src.Id);
        dup.DataHex.Should().Be("AABBCCDD");
        dup.IsExtended.Should().BeTrue();
        dup.IsFd.Should().BeTrue();
    }

    [Fact]
    public void MoveUp_DownCommand_ReordersRows()
    {
        var vm = NewVm(out _, out _);
        vm.AddRowCommand.Execute(null); vm.Rows[1].Id = 0x200;
        vm.AddRowCommand.Execute(null); vm.Rows[2].Id = 0x300;

        vm.SelectedRow = vm.Rows[2];
        vm.MoveUpCommand.Execute(null);
        vm.Rows.Select(r => r.Id).Should().Equal((ushort)0x100, (ushort)0x300, (ushort)0x200);

        vm.MoveDownCommand.Execute(null);
        vm.Rows.Select(r => r.Id).Should().Equal((ushort)0x100, (ushort)0x200, (ushort)0x300);
    }

    [Fact]
    public void ClearRowsCommand_EmptiesRows()
    {
        var vm = NewVm(out _, out _);
        vm.AddRowCommand.Execute(null);
        vm.Rows.Should().HaveCount(2);
        vm.ClearRowsCommand.Execute(null);
        vm.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task SendCommand_Concurrent_DispatchesAllFrames()
    {
        var vm = NewVm(out var ch, out _);
        vm.AddRowCommand.Execute(null); vm.Rows[^1].Id = 0x200;
        vm.AddRowCommand.Execute(null); vm.Rows[^1].Id = 0x300;
        vm.IsConcurrent = true;
        vm.Iterations = 2;

        await vm.SendCommand.ExecuteAsync(null);

        ch.Written.Should().HaveCount(6, "3 frames × 2 iterations");
        vm.StatusText.Should().Contain("Sent 6");
        vm.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task SendCommand_Sequential_FiresInOrder()
    {
        var vm = NewVm(out var ch, out _);
        vm.AddRowCommand.Execute(null); vm.Rows[^1].Id = 0x200;
        vm.AddRowCommand.Execute(null); vm.Rows[^1].Id = 0x300;
        vm.IsConcurrent = false;
        vm.DelayMs = 0;
        vm.Iterations = 1;

        await vm.SendCommand.ExecuteAsync(null);

        ch.Written.Select(f => f.Id.Raw).Should().Equal(0x100u, 0x200u, 0x300u);
    }

    [Fact]
    public async Task SendCommand_NoRows_SkipsSend_WithStatusMessage()
    {
        var vm = NewVm(out var ch, out _);
        vm.ClearRowsCommand.Execute(null);

        await vm.SendCommand.ExecuteAsync(null);

        ch.Written.Should().BeEmpty();
        vm.StatusText.Should().Contain("No frames");
    }

    [Fact]
    public async Task SendCommand_InvalidHexData_CountsAsRowFailure_DoesNotAbortSequence()
    {
        // v2.1.1 PATCH: the service does per-row TryBuildRow; an invalid
        // row counts as a failure but the sequence continues with the
        // remaining rows (per-row failure isolation). Pre-v2.1.1 the
        // VM did upfront pre-validation and aborted on the first error;
        // that behavior is gone because it broke mixed-raw-and-DBC
        // sequences where some rows are intentionally DBC-encoded
        // (no hex at all).
        var vm = NewVm(out var ch, out _);
        vm.Rows[0].DataHex = "ZZ";  // invalid hex
        vm.AddRowCommand.Execute(null);
        vm.Rows[1].DataHex = "AABB";  // valid
        vm.Rows[1].Id = 0x200;

        await vm.SendCommand.ExecuteAsync(null);

        ch.Written.Should().HaveCount(1,
            "only the valid row goes on the wire; invalid row is skipped");
        vm.StatusText.Should().Contain("failed");
    }

    [Fact]
    public void SendCommand_CanExecute_ReflectsIsRunning()
    {
        var vm = NewVm(out _, out _);
        vm.SendCommand.CanExecute(null).Should().BeTrue();
        vm.IsRunning = true;  // forces CanExecute re-eval
        vm.SendCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void ProgressMax_TracksRowsCountTimesIterations()
    {
        var vm = NewVm(out _, out _);
        vm.AddRowCommand.Execute(null);
        vm.AddRowCommand.Execute(null);
        vm.Iterations = 5;
        vm.ProgressMax.Should().Be(15, "3 rows × 5 iterations");
    }

    /// <summary>
    /// v3.0.9 PATCH: mirror the v3.0.8 SendViewModel + v3.0.9
    /// DbcSendViewModel pattern — expose
    /// <see cref="RateLimitedSendService.RejectedFrameCount"/> as
    /// <see cref="MultiFrameSendViewModel.RateLimitRejectedCount"/>.
    /// Multi-frame is the highest-throughput caller (iterations ×
    /// frames per second), so a too-tight MaxFramesPerSecond cap is
    /// most likely to be noticed here.
    /// </summary>
    [Fact]
    public void RateLimitRejectedCount_Defaults_To_Zero_When_Provider_Null()
    {
        var vm = NewVm(out _, out _);
        vm.RateLimitRejectedCount.Should().Be(0);
    }

    [Fact]
    public void RateLimitRejectedCount_Updates_From_Provider_After_Poll()
    {
        var vm = NewVm(out _, out _);
        vm.RateLimitRejectedCount.Should().Be(0);

        // NewVm returns a VM with a null provider; swap in a controllable one.
        // Reflection of the private field avoids exposing a test-only setter.
        var sourceField = typeof(MultiFrameSendViewModel)
            .GetField("_getRejectedCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        long source = 5;
        sourceField!.SetValue(vm, (Func<long>)(() => source));
        vm.Poll();
        vm.RateLimitRejectedCount.Should().Be(5);
    }

    [Fact]
    public void RateLimitRejectedCount_Stays_Zero_When_Provider_Returns_Zero()
    {
        var vm = NewVm(out _, out _);
        vm.Poll();
        vm.Poll();
        vm.Poll();
        vm.RateLimitRejectedCount.Should().Be(0);
    }

    [Fact]
    public void RateLimitRejectedCount_Raises_PropertyChanged_Only_When_Count_Changes()
    {
        long source = 7;
        var vm = new MultiFrameSendViewModel(
            new SequenceSendService(new PeakCan.Host.App.Services.SendService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PeakCan.Host.App.Services.SendService>.Instance)),
            dbcService: null,
            library: null,
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<MultiFrameSendViewModel>.Instance,
            rateLimitRejectedCountProvider: () => source);
        var initial = vm.RateLimitRejectedCount;
        initial.Should().Be(0);

        var changeCount = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.RateLimitRejectedCount))
                changeCount++;
        };

        vm.Poll();
        changeCount.Should().Be(1);
        vm.RateLimitRejectedCount.Should().Be(7);

        vm.Poll();
        changeCount.Should().Be(1);

        source = 12;
        vm.Poll();
        changeCount.Should().Be(2);
        vm.RateLimitRejectedCount.Should().Be(12);
    }
}