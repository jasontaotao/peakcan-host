using FluentAssertions;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Mobile.Core.Models;
using PeakCan.Host.Mobile.Core.Platform;
using PeakCan.Host.Mobile.Core.Tests.Fakes;
using NSubstitute;
using PeakCan.Host.Mobile.Core.ViewModels;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.ViewModels;

public class TraceSessionViewModelTests
{
    private static ReplayFrame F(double t, uint id) =>
        new(t, id, 2, new byte[] { 1, 2 }, default, false);

    private static FakeUiDispatcher.FakeTimer DrainTimer(TraceSessionViewModel vm)
    {
        var field = typeof(TraceSessionViewModel)
            .GetField("_drainTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (FakeUiDispatcher.FakeTimer)field!.GetValue(vm)!;
    }

    private sealed class FakeSourceFactory : IStreamingSourceFactory
    {
        public IStreamingTraceSource NextSource { get; } = Substitute.For<IStreamingTraceSource>();
        public IStreamingTraceSource LastSource => NextSource;
        public IStreamingTraceSource Create(string path) => NextSource;
    }

    private sealed class Env
    {
        public FakeUiDispatcher Ui { get; } = new();
        public FakeStreamingTracePlayer Player { get; } = new();
        public FakeSourceFactory SourceFactory { get; } = new();
        public TraceSessionViewModel Vm { get; }

        public Env()
        {
            Vm = new TraceSessionViewModel(Ui, SourceFactory, _ => Player);
        }
    }

    [Fact]
    public void InitialState_IsEmpty()
    {
        var env = new Env();
        env.Vm.State.Should().Be(SessionState.Empty);
        env.Vm.VisibleRows.Should().OnlyContain(r => r.IsEmpty);
        env.Vm.LatestVisibleRow.Should().BeNull();
    }

    [Fact]
    public async Task OpenAsync_PrefetchesFirstScreen_GoesReady()
    {
        var env = new Env();
        var frames = new AsyncFrameSeq(F(0, 1), F(0.5, 2));
        env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));

        await env.Vm.OpenAsync("foo.asc");

        env.Vm.State.Should().Be(SessionState.Ready);
        env.Vm.LatestVisibleRow!.Timestamp.Should().Be(0.5);
        env.Vm.VisibleRows.Should().Contain(r => !r.IsEmpty);
    }

    [Fact]
    public async Task TogglePlay_CallsPlayer_AndTransitionsToPlaying()
    {
        var env = new Env();
        var frames = new AsyncFrameSeq(F(0, 1));
        env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));
        await env.Vm.OpenAsync("foo.asc");

        env.Vm.TogglePlayCommand.Execute(null);

        env.Player.PlayCount.Should().Be(1);
        env.Vm.State.Should().Be(SessionState.Playing);
    }

    [Fact]
    public void FrameEmitted_AccumulatesInPending_NotVisibleUntilDrainTick()
    {
        var env = new Env();
        env.Vm.MarkReadyForEmit(env.Player);
        env.Player.Emit(F(0.0, 0x100));

        env.Vm.LatestVisibleRow.Should().BeNull();

        DrainTimer(env.Vm).Tick();
        env.Vm.LatestVisibleRow!.Id.Should().Be(0x100u);
    }

    [Fact]
    public void RingBuffer_KeepsLatestN_FramesOnly()
    {
        var env = new Env();
        env.Vm.MarkReadyForEmit(env.Player);
        for (int i = 0; i < 5500; i++) env.Player.Emit(F(i * 0.01, (uint)i));

        DrainTimer(env.Vm).Tick();

        env.Vm.LatestVisibleRow!.Timestamp.Should().Be(54.99);
    }

    [Fact]
    public void VisibleRows_AreStableSlots_AcrossDrains()
    {
        var env = new Env();
        env.Vm.MarkReadyForEmit(env.Player);
        var before = env.Vm.VisibleRows;

        env.Player.Emit(F(0, 1));
        DrainTimer(env.Vm).Tick();

        env.Vm.VisibleRows.Should().BeSameAs(before);
        before.Should().Contain(r => !r.IsEmpty);
    }

    [Fact]
    public void Drain_DoesNotRaiseVisibleRowsPropertyChanged()
    {
        var env = new Env();
        env.Vm.MarkReadyForEmit(env.Player);
        var changed = new List<string>();
        env.Vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        env.Player.Emit(F(0, 1));
        DrainTimer(env.Vm).Tick();

        changed.Should().NotContain(nameof(TraceSessionViewModel.VisibleRows));
        env.Vm.LatestVisibleRow.Should().NotBeNull();
    }

    [Fact]
    public void DrainTimer_UsesThrottledUiRefreshInterval()
    {
        var env = new Env();
        env.Vm.MarkReadyForEmit(env.Player);

        DrainTimer(env.Vm).Period.Should().Be(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void IdFilter_ExcludesNonMatchingFrames()
    {
        var env = new Env();
        env.Vm.MarkReadyForEmit(env.Player);
        env.Vm.SetIdFilter("0x100");
        env.Player.Emit(F(0.0, 0x100));
        env.Player.Emit(F(0.1, 0x200));

        DrainTimer(env.Vm).Tick();

        env.Vm.LatestVisibleRow!.Id.Should().Be(0x100u);
    }

    [Fact]
    public async Task PlaybackEnded_Eof_TransitionsToEnded()
    {
        var env = new Env();
        var frames = new AsyncFrameSeq(F(0, 1));
        env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));
        await env.Vm.OpenAsync("foo.asc");

        env.Vm.TogglePlayCommand.Execute(null);
        env.Player.EmitEof();

        env.Vm.State.Should().Be(SessionState.Ended);
    }

    [Fact]
    public async Task SetSpeed_ForwardsToPlayer()
    {
        var env = new Env();
        var frames = new AsyncFrameSeq(F(0, 1));
        env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));
        await env.Vm.OpenAsync("foo.asc");

        env.Vm.SetSpeed(4.0);
        env.Player.Speed.Should().Be(4.0);
    }

    [Fact]
    public async Task TogglePlay_FromReady_ClearsPrefetchedViewport()
    {
        var env = new Env();
        var frames = new AsyncFrameSeq(F(0, 1), F(0.5, 2));
        env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));

        await env.Vm.OpenAsync("foo.asc");
        env.Vm.LatestVisibleRow.Should().NotBeNull();

        env.Vm.TogglePlayCommand.Execute(null);
        env.Vm.State.Should().Be(SessionState.Playing);
        env.Vm.LatestVisibleRow.Should().BeNull();
        env.Vm.VisibleRows.Should().OnlyContain(r => r.IsEmpty);
    }

    [Fact]
    public void SetIdFilter_ClearsExistingRows_AndAppliesToFutureFrames()
    {
        var env = new Env();
        env.Vm.MarkReadyForEmit(env.Player);
        env.Player.Emit(F(0.0, 0x100));
        env.Player.Emit(F(0.1, 0x200));
        DrainTimer(env.Vm).Tick();
        env.Vm.LatestVisibleRow.Should().NotBeNull();

        env.Vm.SetIdFilter("0x100");
        env.Vm.LatestVisibleRow.Should().BeNull();
        env.Vm.VisibleRows.Should().OnlyContain(r => r.IsEmpty);

        env.Player.Emit(F(0.2, 0x100));
        DrainTimer(env.Vm).Tick();
        env.Vm.LatestVisibleRow!.Id.Should().Be(0x100u);
    }
}

// 测试用：可控的惰性帧流
internal sealed class AsyncFrameSeq(params ReplayFrame[] frames)
{
    public StreamingTraceOpenResult OpenResult => new()
    {
        Frames = Yield(),
        Stats = new StreamingParseStats(),
    };

    private async IAsyncEnumerable<ReplayFrame> Yield(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var f in frames)
        {
            ct.ThrowIfCancellationRequested();
            yield return f;
        }
    }
}
