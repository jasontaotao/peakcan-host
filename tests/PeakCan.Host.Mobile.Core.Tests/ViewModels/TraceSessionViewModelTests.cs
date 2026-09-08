using FluentAssertions;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Mobile.Core.Models;
using PeakCan.Host.Mobile.Core.Platform;
using PeakCan.Host.Mobile.Core.Tests.Fakes;
using PeakCan.Host.Mobile.Core.Services;
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

    private sealed class FakeCacheSink : ITraceCacheSink
    {
        public List<ReplayFrame> Frames { get; } = [];
        public long TraceId { get; } = 42;
        public bool IsEnabled { get; private set; } = true;
        public long WrittenFrames => Frames.Count;
        public long DroppedFrames { get; private set; }
        public Exception? Failure { get; private set; }
        public List<bool> ClosedStates { get; } = [];
        public TaskCompletionSource ClosedCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Enqueue(ReplayFrame frame) => Frames.Add(frame);

        public Task CloseAsync(bool markComplete, CancellationToken ct = default)
        {
            ClosedStates.Add(markComplete);
            IsEnabled = false;
            ClosedCompletion.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCacheSinkFactory : ITraceCacheSinkFactory
    {
        public FakeCacheSink? NextSink { get; set; } = new();
        public List<string> SourceNames { get; } = [];

        public Task<ITraceCacheSink?> StartAsync(string sourceName, long fileSizeBytes, CancellationToken ct = default)
        {
            SourceNames.Add(sourceName);
            return Task.FromResult<ITraceCacheSink?>(NextSink);
        }
    }

    private sealed class Env
    {
        public FakeUiDispatcher Ui { get; } = new();
        public FakeStreamingTracePlayer Player { get; } = new();
        public FakeSourceFactory SourceFactory { get; } = new();
        public FakeCacheSinkFactory CacheFactory { get; } = new();
        public TraceSessionViewModel Vm { get; }

        public TraceSessionViewModel CreateVm() =>
            new(Ui, SourceFactory, _ => Player, cacheSinkFactory: CacheFactory);

        public Env(bool useCache = true)
        {
            Vm = useCache
                ? CreateVm()
                : new TraceSessionViewModel(Ui, SourceFactory, _ => Player);
        }
    }

    [Fact]
    public void Drain_Shows_Skipped_Lines_From_Player()
    {
        var env = new Env();
        env.Vm.MarkReadyForEmit(env.Player);
        env.Player.SkippedLines = 7;

        env.Player.Emit(F(0, 0x100));
        DrainTimer(env.Vm).Tick();

        env.Vm.SkippedLinesText.Should().Be("已跳过 7 行");
    }

    [Fact]
    public void ClearPlaybackBuffer_Clears_Skipped_Lines_Text()
    {
        var env = new Env();
        env.Vm.MarkReadyForEmit(env.Player);
        env.Player.SkippedLines = 7;

        env.Player.Emit(F(0, 0x100));
        DrainTimer(env.Vm).Tick();
        env.Vm.SkippedLinesText.Should().NotBeEmpty();

        env.Vm.SetIdFilter("0x100");

        env.Vm.SkippedLinesText.Should().BeEmpty();
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

        await env.Vm.OpenAsync("foo.asc", "foo.asc", 0);

        env.Vm.State.Should().Be(SessionState.Ready);
        env.Vm.LatestVisibleRow!.Timestamp.Should().Be(0.5);
        env.Vm.VisibleRows.Should().Contain(r => !r.IsEmpty);
    }

    [Fact]
    public async Task OpenAsync_Starts_Cache_And_Emits_Unfiltered_Frames()
    {
        var env = new Env();
        var frames = new AsyncFrameSeq(F(0, 0x100), F(0.1, 0x200));
        env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));

        await env.Vm.OpenAsync("cached.asc", "a.asc", 123);
        env.Vm.TraceId.Should().Be(42);

        env.Vm.SetIdFilter("0x100");
        env.Player.Emit(F(0.2, 0x100));
        env.Player.Emit(F(0.3, 0x200));

        env.CacheFactory.NextSink!.Frames.Select(f => f.Id).Should().Equal([0x100u, 0x200u]);
        env.Vm.CacheStatusText.Should().BeEmpty();
    }

    [Fact]
    public async Task PlaybackEnded_Closes_Cache_As_Complete()
    {
        var env = new Env();
        var frames = new AsyncFrameSeq(F(0, 0x100));
        env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));
        await env.Vm.OpenAsync("cached.asc", "a.asc", 123);

        env.Player.EmitEof();
        await env.CacheFactory.NextSink!.ClosedCompletion.Task;
        env.CacheFactory.NextSink.ClosedStates.Should().Equal([true]);
    }

    [Fact]
    public async Task Cache_Factory_Returning_Null_Sets_Unavailable_But_Keeps_Playback()
    {
        var env = new Env();
        env.CacheFactory.NextSink = null;
        var frames = new AsyncFrameSeq(F(0, 0x100));
        env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));

        await env.Vm.OpenAsync("cached.asc", "a.asc", 123);

        env.Vm.State.Should().Be(SessionState.Ready);
        env.Vm.CacheStatusText.Should().Be("缓存不可用");
    }

    [Fact]
    public async Task OpenAsync_Disposes_OpenResult()
    {
        var env = new Env();
        var frames = new AsyncFrameSeq(F(0, 0x100));
        var openResult = frames.OpenResult;
        env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(openResult));

        await env.Vm.OpenAsync("cached.asc", "a.asc", 123);

        openResult.SourceStream!.CanRead.Should().BeFalse();
    }

    [Fact]
    public async Task TogglePlay_CallsPlayer_AndTransitionsToPlaying()
    {
        var env = new Env();
        var frames = new AsyncFrameSeq(F(0, 1));
        env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));
        await env.Vm.OpenAsync("foo.asc", "foo.asc", 0);

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
        await env.Vm.OpenAsync("foo.asc", "foo.asc", 0);

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
        await env.Vm.OpenAsync("foo.asc", "foo.asc", 0);

        env.Vm.SetSpeed(4.0);
        env.Player.Speed.Should().Be(4.0);
    }

    [Fact]
    public async Task SetDbc_SetsStatus_AndPrefetchesDecodedRows()
    {
        var env = new Env();
        var frames = new AsyncFrameSeq(F(0, 0x100), F(0.5, 0x101));
        env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));
        var catalog = DbcCatalog.Parse("""
            VERSION ""
            NS_ :
            BS_:
            BU_: ECM

            BO_ 256 EngineData: 8 ECM
             SG_ EngineSpeed : 0|16@1+ (0.25,0) [0|16000] "rpm" Vector__XXX
            """, "engine.dbc").Catalog!;

        env.Vm.SetDbc(catalog);
        await env.Vm.OpenAsync("foo.asc", "foo.asc", 0);

        env.Vm.DbcStatusText.Should().Be("DBC: engine.dbc");
        env.Vm.VisibleRows.First(r => !r.IsEmpty).SignalSummaryText.Should().Be("EngineSpeed=128.25rpm");
    }

    [Fact]
    public async Task SetDbc_ClearsStaleRows_AndAppliesToFutureReplayFrames()
    {
        var env = new Env();
        var catalog = DbcCatalog.Parse("""
            VERSION ""
            NS_ :
            BS_:
            BU_: ECM

            BO_ 256 EngineData: 8 ECM
             SG_ EngineSpeed : 0|16@1+ (0.25,0) [0|16000] "rpm" Vector__XXX
            """, "engine.dbc").Catalog!;
        env.Vm.SetDbc(catalog);
        env.Vm.MarkReadyForEmit(env.Player);
        env.Player.Emit(F(0, 0x100));
        DrainTimer(env.Vm).Tick();
        env.Vm.LatestVisibleRow!.SignalSummaryText.Should().Be("EngineSpeed=128.25rpm");

        env.Vm.SetDbc(null);

        env.Vm.Dbc.Should().BeNull();
        env.Vm.DbcStatusText.Should().Be("未加载 DBC");
        env.Vm.LatestVisibleRow.Should().BeNull();
        env.Vm.VisibleRows.Should().OnlyContain(r => r.IsEmpty);
        env.Player.Emit(F(1, 0x100));
        DrainTimer(env.Vm).Tick();
        env.Vm.LatestVisibleRow!.SignalSummaryText.Should().BeEmpty();
    }

    [Fact]
    public async Task TogglePlay_FromReady_ClearsPrefetchedViewport()
    {
        var env = new Env();
        var frames = new AsyncFrameSeq(F(0, 1), F(0.5, 2));
        env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));

        await env.Vm.OpenAsync("foo.asc", "foo.asc", 0);
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
        Frames = Yield(frames),
        Stats = new StreamingParseStats(),
        SourceStream = new MemoryStream([1, 2], writable: false),
    };

    private static async IAsyncEnumerable<ReplayFrame> Yield(
        ReplayFrame[] frames,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var f in frames)
        {
            ct.ThrowIfCancellationRequested();
            yield return f;
        }
    }
}
