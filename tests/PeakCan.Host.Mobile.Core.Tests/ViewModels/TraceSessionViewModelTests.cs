using FluentAssertions;
using PeakCan.Host.Core.Replay;
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
        env.Vm.VisibleRows.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenAsync_PrefetchesFirstScreen_GoesReady()
    {
        var env = new Env();
        var frames = new AsyncFrameSeq(F(0, 1), F(0.5, 2));
        env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));

        await env.Vm.OpenAsync("foo.asc");

        env.Vm.State.Should().Be(SessionState.Ready);
        env.Vm.VisibleRows.Should().HaveCount(2);
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
    public async Task FrameEmitted_AccumulatesInPending_NotVisibleUntilDrainTick()
    {
        var env = new Env();
        env.Vm.MarkReadyForEmit(env.Player);
        env.Player.Emit(F(0.0, 0x100));

        env.Vm.VisibleRows.Should().BeEmpty();

        DrainTimer(env.Vm).Tick();
        env.Vm.VisibleRows.Should().HaveCount(1);
    }

    [Fact]
    public async Task RingBuffer_KeepsLatestN_FramesOnly()
    {
        var env = new Env();
        env.Vm.MarkReadyForEmit(env.Player);
        for (int i = 0; i < 5500; i++) env.Player.Emit(F(i * 0.01, (uint)i));

        DrainTimer(env.Vm).Tick();
        env.Vm.VisibleRows.Should().HaveCount(5000);
    }

    [Fact]
    public async Task IdFilter_ExcludesNonMatchingFrames()
    {
        var env = new Env();
        env.Vm.MarkReadyForEmit(env.Player);
        env.Vm.SetIdFilter("0x100");
        env.Player.Emit(F(0.0, 0x100));
        env.Player.Emit(F(0.1, 0x200));

        DrainTimer(env.Vm).Tick();
        env.Vm.VisibleRows.Should().ContainSingle().Which.Id.Should().Be(0x100u);
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
    public async Task TogglePlay_FromReady_ClearsPrefetchedRing()
    {
        var env = new Env();
        var frames = new AsyncFrameSeq(F(0, 1), F(0.5, 2));
        env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));

        await env.Vm.OpenAsync("foo.asc");
        env.Vm.VisibleRows.Should().HaveCount(2);

        env.Vm.TogglePlayCommand.Execute(null);
        env.Vm.State.Should().Be(SessionState.Playing);
        env.Vm.VisibleRows.Should().BeEmpty();
    }

    [Fact]
    public async Task SetIdFilter_ClearsExistingRows_AndAppliesToFutureFrames()
    {
        var env = new Env();
        env.Vm.MarkReadyForEmit(env.Player);
        env.Player.Emit(F(0.0, 0x100));
        env.Player.Emit(F(0.1, 0x200));
        DrainTimer(env.Vm).Tick();
        env.Vm.VisibleRows.Should().HaveCount(2);

        env.Vm.SetIdFilter("0x100");
        env.Vm.VisibleRows.Should().BeEmpty();

        env.Player.Emit(F(0.2, 0x100));
        DrainTimer(env.Vm).Tick();
        env.Vm.VisibleRows.Should().ContainSingle().Which.Id.Should().Be(0x100u);
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


