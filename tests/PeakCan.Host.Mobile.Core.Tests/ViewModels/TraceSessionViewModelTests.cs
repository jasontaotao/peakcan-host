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

    private static DbcCatalog EngineDbc() => DbcCatalog.Parse("""
        VERSION ""
        NS_ :
        BS_:
        BU_: ECM

        BO_ 256 EngineData: 8 ECM
         SG_ EngineSpeed : 0|16@1+ (0.25,0) [0|16000] "rpm" Vector__XXX
        """).Catalog!;

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
        public bool CloseResult { get; set; } = true;
        public TaskCompletionSource ClosedCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Enqueue(ReplayFrame frame) => Frames.Add(frame);

        public Task<bool> CloseAsync(bool markComplete, CancellationToken ct = default)
        {
            ClosedStates.Add(markComplete);
            IsEnabled = false;
            ClosedCompletion.TrySetResult();
            return Task.FromResult(CloseResult);
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

        public Env(bool useCache = true, ITraceCacheStore? cacheStore = null)
        {
            Vm = useCache
                ? new TraceSessionViewModel(Ui, SourceFactory, _ => Player, cacheSinkFactory: CacheFactory, cacheStore: cacheStore)
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
    public async Task PlaybackEnded_Shows_Incomplete_When_Cache_Cannot_Mark_Complete()
    {
        var env = new Env();
        env.CacheFactory.NextSink!.CloseResult = false;
        var frames = new AsyncFrameSeq(F(0, 0x100));
        env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));
        await env.Vm.OpenAsync("cached.asc", "a.asc", 123);

        var statusPosted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Ui.PostExecuted += () =>
        {
            if (!string.IsNullOrEmpty(env.Vm.CacheStatusText))
                statusPosted.TrySetResult();
        };

        env.Player.EmitEof();
        await statusPosted.Task;

        env.Vm.CacheStatusText.Should().Be("缓存未完成");
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
    public void Chart_IsAvailable_After_Session_IsReady()
    {
        var env = new Env();

        env.Vm.MarkReadyForEmit(env.Player);

        env.Vm.Chart.Should().NotBeNull();
        env.Vm.Chart.Messages.Should().BeEmpty();
    }

    [Fact]
    public void SetDbc_Syncs_Chart_Catalog_And_Clears_Chart()
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

        env.Vm.Chart.Messages.Should().Contain(m => m.Name == "EngineData");
        env.Vm.Chart.Cursor.Should().BeNull();
    }

    [Fact]
    public void Drain_Accumulates_Selected_Signal_Samples_And_Cursor()
    {
        var env = new Env();
        env.Vm.SetDbc(DbcCatalog.Parse("""
            VERSION ""
            NS_ :
            BS_:
            BU_: ECM

            BO_ 256 EngineData: 8 ECM
             SG_ EngineSpeed : 0|16@1+ (0.25,0) [0|16000] "rpm" Vector__XXX
            """).Catalog!);
        env.Vm.Chart.Select(new SignalSelectionKey(0x100, false, "EngineData", "EngineSpeed")).Should().BeTrue();
        env.Vm.MarkReadyForEmit(env.Player);

        env.Player.Emit(F(1, 0x100));
        env.Player.Emit(F(2, 0x200));
        DrainTimer(env.Vm).Tick();

        env.Vm.Chart.SelectedSignals.Should().HaveCount(1);
        env.Vm.Chart.Cursor!.Value.Timestamp.Should().Be(2);
        env.Vm.Chart.RenderPoints.Should().ContainKey(new SignalSelectionKey(0x100, false, "EngineData", "EngineSpeed"));
    }

    [Fact]
    public void IdFilter_Excludes_Chart_Samples()
    {
        var env = new Env();
        env.Vm.SetDbc(DbcCatalog.Parse("""
            VERSION ""
            NS_ :
            BS_:
            BU_: ECM

            BO_ 256 EngineData: 8 ECM
             SG_ EngineSpeed : 0|16@1+ (0.25,0) [0|16000] "rpm" Vector__XXX
            """).Catalog!);
        env.Vm.Chart.Select(new SignalSelectionKey(0x100, false, "EngineData", "EngineSpeed")).Should().BeTrue();
        env.Vm.MarkReadyForEmit(env.Player);
        env.Vm.SetIdFilter("0x100");

        env.Player.Emit(F(1, 0x200));
        DrainTimer(env.Vm).Tick();

        env.Vm.Chart.Cursor.Should().BeNull();
    }

    [Fact]
    public async Task OpenAsync_Backfills_Selected_Signal_From_File_Start()
    {
        var env = new Env();
        var frames = new AsyncFrameSeq(F(0, 0x100), F(5, 0x100), F(9, 0x100));
        env.SourceFactory.LastSource
            .OpenAsync(default)
            .ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));
        env.Vm.SetDbc(DbcCatalog.Parse("""
            VERSION ""
            NS_ :
            BS_:
            BU_: ECM

            BO_ 256 EngineData: 8 ECM
             SG_ EngineSpeed : 0|16@1+ (0.25,0) [0|16000] "rpm" Vector__XXX
            """).Catalog!);
        env.Vm.Chart.Select(new SignalSelectionKey(0x100, false, "EngineData", "EngineSpeed"));

        await env.Vm.OpenAsync("foo.asc", "foo.asc", 0);
        await env.Vm.BackfillSelectedSignalsAsync();

        var points = env.Vm.Chart.RenderPoints[new SignalSelectionKey(0x100, false, "EngineData", "EngineSpeed")];
        points.Select(p => p.Timestamp).Should().Equal(0, 5, 9);
    }
    [Fact]
    public void Stop_Clears_Chart_Samples()
    {
        var env = new Env();
        env.Vm.SetDbc(DbcCatalog.Parse("""
            VERSION ""
            NS_ :
            BS_:
            BU_: ECM

            BO_ 256 EngineData: 8 ECM
             SG_ EngineSpeed : 0|16@1+ (0.25,0) [0|16000] "rpm" Vector__XXX
            """).Catalog!);
        var key = new SignalSelectionKey(0x100, false, "EngineData", "EngineSpeed");
        env.Vm.Chart.Select(key).Should().BeTrue();
        env.Vm.MarkReadyForEmit(env.Player);
        env.Player.Emit(F(1, 0x100));
        DrainTimer(env.Vm).Tick();

        env.Vm.StopCommand.Execute(null);

        env.Vm.Chart.RenderPoints.Should().BeEmpty();
        env.Vm.Chart.Cursor.Should().BeNull();
        env.Vm.Chart.SelectedSignals.Should().ContainSingle(i => i.Key == key);
    }

    [Fact]
    public async Task Stop_Restarts_Selected_Signal_Backfill()
    {
        var env = new Env();
        var frames = new AsyncFrameSeq(F(0, 0x100), F(5, 0x100));
        env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));
        var key = new SignalSelectionKey(0x100, false, "EngineData", "EngineSpeed");

        env.Vm.SetDbc(EngineDbc());
        env.Vm.Chart.Select(key).Should().BeTrue();
        await env.Vm.OpenAsync("foo.asc", "foo.asc", 0);
        await env.Vm.ChartBackfillTask!;

        env.Vm.StopCommand.Execute(null);
        await env.Vm.ChartBackfillTask!;

        env.Vm.Chart.RenderPoints[key].Select(p => p.Timestamp).Should().Equal(0, 5);
    }

    [Fact]
    public async Task TogglePlay_FromReady_Restarts_Selected_Signal_Backfill()
    {
        var env = new Env();
        var frames = new AsyncFrameSeq(F(0, 0x100), F(5, 0x100));
        env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));
        var key = new SignalSelectionKey(0x100, false, "EngineData", "EngineSpeed");

        env.Vm.SetDbc(EngineDbc());
        env.Vm.Chart.Select(key).Should().BeTrue();
        await env.Vm.OpenAsync("foo.asc", "foo.asc", 0);
        await env.Vm.ChartBackfillTask!;

        env.Vm.TogglePlayCommand.Execute(null);
        await env.Vm.ChartBackfillTask!;

        env.Vm.Chart.RenderPoints[key].Select(p => p.Timestamp).Should().Equal(0, 5);
    }

    [Fact]
    public async Task Replay_FromEnded_Restarts_Selected_Signal_Backfill()
    {
        var env = new Env(useCache: false);
        var frames = new AsyncFrameSeq(F(0, 0x100), F(5, 0x100));
        env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));
        var key = new SignalSelectionKey(0x100, false, "EngineData", "EngineSpeed");

        env.Vm.SetDbc(EngineDbc());
        env.Vm.Chart.Select(key).Should().BeTrue();
        await env.Vm.OpenAsync("foo.asc", "foo.asc", 0);
        await env.Vm.ChartBackfillTask!;
        env.Vm.MarkReadyForEmit(env.Player);
        env.Player.EmitEof();
        env.Player.Emit(F(0, 0x100));

        env.Vm.TogglePlayCommand.Execute(null);
        await env.Vm.ChartBackfillTask!;

        env.Vm.Chart.RenderPoints[key].Select(p => p.Timestamp).Should().Equal(0, 5);
    }

    [Fact]
    public async Task Seek_FromReady_Restarts_Selected_Signal_Backfill()
    {
        var env = new Env();
        var frames = new AsyncFrameSeq(F(0, 0x100), F(5, 0x100));
        env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));
        var key = new SignalSelectionKey(0x100, false, "EngineData", "EngineSpeed");

        env.Vm.SetDbc(EngineDbc());
        env.Vm.Chart.Select(key).Should().BeTrue();
        await env.Vm.OpenAsync("foo.asc", "foo.asc", 0);
        await env.Vm.ChartBackfillTask!;
        typeof(TraceSessionViewModel)
            .GetField("_durationKnownValue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(env.Vm, true);
        typeof(TraceSessionViewModel)
            .GetField("_duration", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(env.Vm, 10d);

        env.Vm.SeekToCommand.Execute(0.5);
        await env.Vm.ChartBackfillTask!;

        env.Vm.Chart.RenderPoints[key].Select(p => p.Timestamp).Should().Equal(0, 5);
    }

    [Fact]
    public async Task IdFilter_Change_Restarts_Selected_Signal_Backfill()
    {
        var env = new Env();
        var frames = new AsyncFrameSeq(F(0, 0x100), F(5, 0x100));
        env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));
        var key = new SignalSelectionKey(0x100, false, "EngineData", "EngineSpeed");

        env.Vm.SetDbc(EngineDbc());
        env.Vm.Chart.Select(key).Should().BeTrue();
        await env.Vm.OpenAsync("foo.asc", "foo.asc", 0);
        await env.Vm.ChartBackfillTask!;

        env.Vm.SetIdFilter("0x100");
        await env.Vm.ChartBackfillTask!;

        env.Vm.Chart.RenderPoints[key].Select(p => p.Timestamp).Should().Equal(0, 5);
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

    [Fact]
    public void SetAnchor_SetsTimestampAndRaisesChanges()
    {
        var env = new Env();
        var changed = new List<string?>();
        env.Vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        env.Vm.SetAnchor(12.345678);

        env.Vm.AnchorTimestamp.Should().Be(12.345678);
        env.Vm.HasAnchor.Should().BeTrue();
        env.Vm.AnchorText.Should().Be("⚑ 12.345678s");
        changed.Should().Contain(nameof(TraceSessionViewModel.AnchorTimestamp));
        changed.Should().Contain(nameof(TraceSessionViewModel.HasAnchor));
        changed.Should().Contain(nameof(TraceSessionViewModel.AnchorText));
    }

    [Fact]
    public void SetAnchor_NaNOrInfinity_Ignored()
    {
        var env = new Env();
        env.Vm.SetAnchor(3.0);

        env.Vm.SetAnchor(double.NaN);
        env.Vm.SetAnchor(double.PositiveInfinity);
        env.Vm.SetAnchor(double.NegativeInfinity);

        env.Vm.AnchorTimestamp.Should().Be(3.0);
        env.Vm.HasAnchor.Should().BeTrue();
    }

    [Fact]
    public void ClearAnchor_ResetsAll()
    {
        var env = new Env();
        env.Vm.SetAnchor(5.0);

        env.Vm.ClearAnchor();

        env.Vm.AnchorTimestamp.Should().BeNull();
        env.Vm.HasAnchor.Should().BeFalse();
        env.Vm.AnchorText.Should().BeEmpty();
        env.Vm.Chart.AnchorTimestamp.Should().BeNull();
    }

    [Fact]
    public void SetAnchor_SyncsChartAnchorTimestamp()
    {
        var env = new Env();

        env.Vm.SetAnchor(3.5);

        env.Vm.Chart.AnchorTimestamp.Should().Be(3.5);
    }

    [Fact]
    public void SetAnchor_SurvivesSeekAndStop()
    {
        var env = new Env(useCache: false);
        env.Vm.MarkReadyForEmit(env.Player);
        // DurationKnown 由后台 scan 线程置位；测试直接反射置位（对齐现有 Seek 测试模式）
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        typeof(TraceSessionViewModel).GetField("_durationKnownValue", flags)!.SetValue(env.Vm, true);
        typeof(TraceSessionViewModel).GetField("_durationKnown", flags)!.SetValue(env.Vm, true);
        typeof(TraceSessionViewModel).GetField("_duration", flags)!.SetValue(env.Vm, 10d);
        env.Vm.SetAnchor(4.0);

        env.Vm.SeekToCommand.Execute(0.5);
        env.Vm.StopCommand.Execute(null);
        env.Vm.TogglePlayCommand.Execute(null);

        env.Vm.AnchorTimestamp.Should().Be(4.0);
        env.Vm.HasAnchor.Should().BeTrue();
        env.Vm.AnchorText.Should().Be("⚑ 4.000000s");
        env.Vm.Chart.AnchorTimestamp.Should().Be(4.0);
    }

    [Fact]
    public async Task CreateAnchorValuesViewModel_UsesSessionCacheTraceAndDbc()
    {
        // Arrange: stub store 记录查询入参；fake sink 的 TraceId=42 即 session 的 traceId
        var store = Substitute.For<ITraceCacheStore>();
        store.GetOrCreateTraceAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(42L);
        var frame = new CachedFrame(0, 3.5, 0x100u, false, 2, [0x01, 0x00, 0, 0, 0, 0, 0, 0]);
        store.GetLatestFramesBeforeAsync(42, 3.5, Arg.Any<CancellationToken>())
            .Returns([frame]);
        var env = new Env(cacheStore: store);
        var frames = new AsyncFrameSeq(F(0, 0x100));
        env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));
        await env.Vm.OpenAsync("cached.asc", "a.asc", 123);
        env.Vm.SetDbc(EngineDbc());

        var values = env.Vm.CreateAnchorValuesViewModel();
        await values.LoadAsync(3.5);

        values.Rows.Should().HaveCount(1);
        values.Rows[0].MessageName.Should().Be("EngineData"); // DBC 解码路径生效
        values.Rows[0].SignalName.Should().Be("EngineSpeed");
        values.Rows[0].ValueText.Should().Be("0.25");         // little-endian: 0x0001 * 0.25
        await store.Received(1).GetLatestFramesBeforeAsync(42, 3.5, Arg.Any<CancellationToken>());
    }

    // ---- J1939 重组接线（Task 7）----

    private static ReplayFrame BamCm(double t, byte sa = 0xF4) =>
        new(t, PeakCan.Host.Core.J1939.J1939Id.Compose(6, 0x00EC00, sa, 0xFF), 8,
            PeakCan.Host.Core.J1939.TpCmMessage.Bam(14, 2, 0x000200).Encode(), default, true);

    private static ReplayFrame BamDt(double t, byte seq, byte sa = 0xF4)
    {
        var chunk = seq == 1 ? Enumerable.Range(0, 7).Select(i => (byte)(i + 1)).ToArray()
                             : Enumerable.Range(7, 7).Select(i => (byte)(i + 1)).ToArray();
        return new ReplayFrame(t, PeakCan.Host.Core.J1939.J1939Id.Compose(6, 0x00EB00, sa, 0xFF), 8,
            new PeakCan.Host.Core.J1939.TpDtMessage(seq, chunk).Encode(), default, true);
    }

    [Fact]
    public void FrameEmitted_FeedsReassemblerBeforeIdFilter()
    {
        // Arrange: 设 ID 过滤排除扩展 TP 帧；重组 tap 必须在过滤前
        var env = new Env(useCache: false);
        env.Vm.MarkReadyForEmit(env.Player);
        env.Vm.SetIdFilter("0x100");

        env.Player.Emit(BamCm(0.0));
        env.Player.Emit(BamDt(0.01, 1));
        env.Player.Emit(BamDt(0.02, 2));

        env.Vm.J1939.Rows.Should().ContainSingle(r => r.StatusText == "完成");
        env.Vm.LatestVisibleRow.Should().BeNull(); // 过滤帧不进表格
    }

    [Fact]
    public void Stop_FlushesThenResets()
    {
        // Arrange: 未闭合会话（CM + 1×DT）
        var env = new Env(useCache: false);
        env.Vm.MarkReadyForEmit(env.Player);
        env.Player.Emit(BamCm(0.0));
        env.Player.Emit(BamDt(0.01, 1));
        env.Vm.J1939.Rows.Should().BeEmpty();

        env.Vm.StopCommand.Execute(null);

        // Stop → Flush 结算未闭合会话 → 截断行留存可见（spec §6）
        env.Vm.J1939.Rows.Should().ContainSingle(r => r.StatusText == "截断");
        // 且已 Reset：此后新会话从零重组
        env.Player.Emit(BamCm(0.5));
        env.Player.Emit(BamDt(0.51, 1));
        env.Player.Emit(BamDt(0.52, 2));
        env.Vm.J1939.Rows.Should().ContainSingle(r => r.StatusText == "完成");
    }

    [Fact]
    public void Seek_FlushesThenResets()
    {
        // Arrange: 未闭合会话 + DurationKnown
        var env = new Env(useCache: false);
        env.Vm.MarkReadyForEmit(env.Player);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        typeof(TraceSessionViewModel).GetField("_durationKnownValue", flags)!.SetValue(env.Vm, true);
        typeof(TraceSessionViewModel).GetField("_durationKnown", flags)!.SetValue(env.Vm, true);
        typeof(TraceSessionViewModel).GetField("_duration", flags)!.SetValue(env.Vm, 10d);
        env.Player.Emit(BamCm(0.0));
        env.Player.Emit(BamDt(0.01, 1));

        env.Vm.SeekToCommand.Execute(0.5);

        // Seek → Flush 结算留存截断行（spec §6 "截断会话可见而非消失"）
        env.Vm.J1939.Rows.Should().ContainSingle(r => r.StatusText == "截断");
        env.Player.Seeks.Should().ContainSingle(s => s == 5.0);
        // 已 Reset：新会话从零重组
        env.Player.Emit(BamCm(0.5));
        env.Player.Emit(BamDt(0.51, 1));
        env.Player.Emit(BamDt(0.52, 2));
        env.Vm.J1939.Rows.Should().ContainSingle(r => r.StatusText == "完成");
    }

    [Fact]
    public async Task OpenNewTrace_ResetsAndClears()
    {
        // Arrange: 打开 A → 进行中 TP 会话
        var env = new Env();
        var framesA = new AsyncFrameSeq(F(0, 0x100));
        env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(framesA.OpenResult));
        await env.Vm.OpenAsync("a.asc", "a.asc", 100);
        env.Player.Emit(BamCm(0.0));
        env.Player.Emit(BamDt(0.01, 1));
        env.Vm.J1939.Rows.Should().BeEmpty();

        // Act: 打开 B
        var framesB = new AsyncFrameSeq(F(0, 0x200));
        env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(framesB.OpenResult));
        await env.Vm.OpenAsync("b.asc", "b.asc", 200);

        // J1939.Rows 已清空；reassembler 已 Reset（Flush 无未闭合会话可结算）
        env.Vm.J1939.Rows.Should().BeEmpty();
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var reassembler = (StreamingJ1939Reassembler)typeof(TraceSessionViewModel)
            .GetField("_j1939Reassembler", flags)!.GetValue(env.Vm)!;
        var settled = new List<J1939ReassembledRow>();
        reassembler.MessageReassembled += settled.Add;
        reassembler.Flush();
        settled.Should().BeEmpty();
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



