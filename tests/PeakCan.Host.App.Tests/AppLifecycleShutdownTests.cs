using System.IO;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OxyPlot;
using PeakCan.Host.App;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Services;
using PeakCan.Host.App.Tests.Services.Trace;  // v3.7.2 PATCH: InMemoryPrefsStore
using SerilogLogger = Serilog.ILogger;
using SerilogNullLogger = Serilog.Core.Logger;
using PeakCan.Host.App.Services.AnalysisApiKey;

namespace PeakCan.Host.App.Tests;

/// <summary>
/// v3.6.2 PATCH: pins the auto-save-before-host-stop ordering
/// invariant on <see cref="App.OnExit"/>. The previous inline
/// OnExit implementation had no test coverage — a future refactor
/// could swap the order and silently break the auto-save feature
/// (because once StopAsync has run, the service provider is
/// disposed and <c>GetService&lt;TraceSessionAutoSaver&gt;()</c>
/// returns null). Extracting the sequence into
/// <see cref="App.RunShutdownAsync"/> lets us drive the seam with
/// a fake <see cref="IHost"/> that records <c>StopAsync</c> call
/// time + a real <see cref="TraceSessionAutoSaver"/> whose
/// <see cref="ITraceViewerViewModelProvider"/> records when the
/// pre-flush runs.
/// <para>
/// v3.7.0 MINOR Chunk 3: extended to also pin the Replay auto-save
/// ordering — Replay runs AFTER Trace but BEFORE host stop.
/// </para>
/// <para>
/// We cannot use NSubstitute for <see cref="IHost"/> directly —
/// <c>StopAsync</c> is not virtual in
/// <c>Microsoft.Extensions.Hosting.Abstractions</c>, so NSubstitute
/// throws on the configuration. Instead we ship a hand-rolled
/// <see cref="FakeHost"/> that owns a settable <c>Services</c>
/// provider + a delegate-driven <c>StopAsync</c> + a recorded
/// call-time.
/// </para>
/// <para>
/// All tests are deterministic (no <c>Task.Delay</c>, no wall
/// clock). The fake provider returns a small but valid VM so
/// <see cref="TraceSessionAutoSaver.TrySaveAutoSnapshotAsync"/>
/// reaches the actual save path on a per-test temp file.
/// </para>
/// </summary>
public class AppLifecycleShutdownTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _files = new();

    public AppLifecycleShutdownTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"shutdown-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in _files)
                if (File.Exists(f)) File.Delete(f);
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string NewAutoSavePath() =>
        Track(Path.Combine(_tempDir, $"auto-{Guid.NewGuid():N}.tmtrace"));

    private string Track(string p) { _files.Add(p); return p; }

    /// <summary>
    /// Hand-rolled <see cref="IHost"/> stub. NSubstitute cannot
    /// mock <see cref="IHost"/> because <c>StopAsync</c> is not
    /// virtual in <c>Microsoft.Extensions.Hosting.Abstractions</c>.
    /// The fake records the wall-clock time of the most recent
    /// <c>StopAsync</c> call so a test can assert the ordering
    /// invariant.
    /// </summary>
    private sealed class FakeHost : IHost
    {
        public IServiceProvider Services { get; set; } = null!;
        public DateTimeOffset? StopAsyncCalledAt { get; private set; }
        public CancellationToken LastStopToken { get; private set; }
        private Func<CancellationToken, Task>? _stopImpl;
        public int StopCallCount { get; private set; }

        public void SetStopImpl(Func<CancellationToken, Task> impl) => _stopImpl = impl;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCallCount++;
            LastStopToken = cancellationToken;
            StopAsyncCalledAt = DateTimeOffset.UtcNow;
            return _stopImpl?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public void Dispose() { /* no-op for tests */ }
    }

    /// <summary>
    /// Records the wall-clock time of the most recent
    /// <see cref="GetCurrent"/> call so a test can assert the
    /// auto-save-before-host-stop ordering invariant.
    /// </summary>
    private sealed class RecordingTraceVmProvider : ITraceViewerViewModelProvider
    {
        public DateTimeOffset? ResolvedAt { get; private set; }
        private readonly TraceViewerViewModel _vm;
        public RecordingTraceVmProvider(TraceViewerViewModel vm) => _vm = vm;
        public TraceViewerViewModel? GetCurrent()
        {
            ResolvedAt = DateTimeOffset.UtcNow;
            return _vm;
        }
    }

    /// <summary>
    /// v3.7.0 MINOR Chunk 3: mirror of <see cref="RecordingTraceVmProvider"/>
    /// for the Replay tab.
    /// </summary>
    private sealed class RecordingReplayVmProvider : IReplayViewModelProvider
    {
        public DateTimeOffset? ResolvedAt { get; private set; }
        private readonly ReplayViewModel _vm;
        public RecordingReplayVmProvider(ReplayViewModel vm) => _vm = vm;
        public ReplayViewModel? GetCurrent()
        {
            ResolvedAt = DateTimeOffset.UtcNow;
            return _vm;
        }
    }

    /// <summary>
    /// Builds a real <see cref="TraceSessionAutoSaver"/> wired to a
    /// recording provider so the test can observe when the
    /// pre-flush resolved the VM.
    /// </summary>
    private (TraceSessionAutoSaver Saver, RecordingTraceVmProvider Provider) MakeTraceSaver(string path)
    {
        var library = new TraceSessionLibrary(path, NullLogger<TraceSessionLibrary>.Instance);
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("src1", "shutdown-test", @"C:/rec.asc", OxyColors.Red),
        });
        var dbc = Substitute.For<DbcService>(Substitute.For<Microsoft.Extensions.Logging.ILogger<DbcService>>());
        var vm = new TraceViewerViewModel(
            registry, dbc, NullLogger<TraceViewerViewModel>.Instance, library, fileDialog: null,
            apiKeyManager: new PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyManager(
                Substitute.For<PeakCan.Host.Core.Analysis.ICredentialStore>(),
                Substitute.For<Microsoft.Extensions.Logging.ILogger<PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyManager>>()));
        var provider = new RecordingTraceVmProvider(vm);
        var prefs = new InMemoryPrefsStore();
        var prompt = Substitute.For<IMessageBoxPrompt>();
        return (new TraceSessionAutoSaver(
            provider, library, prefs, prompt,
            NullLogger<TraceSessionAutoSaver>.Instance, path),
            provider);
    }

    /// <summary>
    /// v3.7.0 MINOR Chunk 3: builds a real <see cref="ReplaySessionAutoSaver"/>
    /// wired to a recording provider. The fake Replay VM has a
    /// pre-set <c>LoadedFilePath</c> so the saver's early-out
    /// (<c>string.IsNullOrEmpty(LoadedFilePath)</c>) is skipped.
    /// </summary>
    private (ReplaySessionAutoSaver Saver, RecordingReplayVmProvider Provider) MakeReplaySaver(string path)
    {
        var library = new TraceSessionLibrary(path, NullLogger<TraceSessionLibrary>.Instance);
        var prefs = new InMemoryPrefsStore();
        var prompt = Substitute.For<IMessageBoxPrompt>();
        var vm = new ReplayViewModel(
            Substitute.For<IReplayService>(),
            Substitute.For<IFileDialogService>(),
            Substitute.For<IAscContentHasher>(),
            Substitute.For<IAscLocator>(),
            library,
            new RecentSessionsService(NullLogger<RecentSessionsService>.Instance,
                Path.Combine(_tempDir, $"recent-{Guid.NewGuid():N}.json")));
        // Pre-set the LoadedFilePath via reflection (the property has a
        // generated setter via CommunityToolkit.Mvvm; reflection is
        // the simplest test-only path).
        typeof(ReplayViewModel).GetProperty("LoadedFilePath")!
            .SetValue(vm, @"C:/replay.asc");
        var provider = new RecordingReplayVmProvider(vm);
        return (new ReplaySessionAutoSaver(
            provider, library, prefs, prompt,
            NullLogger<ReplaySessionAutoSaver>.Instance, path),
            provider);
    }

    [Fact]
    public async Task RunShutdownAsync_AutoSaverRunsBeforeHostStop()
    {
        // arrange
        var tracePath = NewAutoSavePath();
        var (traceSaver, traceProvider) = MakeTraceSaver(tracePath);
        var host = new FakeHost
        {
            Services = new ServiceCollection().BuildServiceProvider(),
        };

        // act
        await App.RunShutdownAsync(
            host,
            _ => traceSaver,
            _ => null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            SerilogNullLogger.None);

        // assert
        traceProvider.ResolvedAt.Should().NotBeNull(
            "the auto-save resolver must run so the VM is captured before the host is stopped");
        host.StopAsyncCalledAt.Should().NotBeNull(
            "StopAsync must be invoked exactly once during shutdown");
        traceProvider.ResolvedAt!.Value.Should().BeOnOrBefore(host.StopAsyncCalledAt!.Value,
            "the auto-save pre-flush must run BEFORE StopAsync — " +
            "otherwise the service provider is disposed and GetService returns null");
        host.StopCallCount.Should().Be(1);
        host.LastStopToken.CanBeCanceled.Should().BeTrue("the stop CTS must be cancellable so the timeout can fire");
    }

    [Fact]
    public async Task RunShutdownAsync_NullHost_ThrowsArgumentNull()
    {
        // arrange — host is null. The extracted method is defensive
        // and surfaces a clear ArgumentNullException rather than
        // NRE-ing on host.Services.
        var (saver, _) = MakeTraceSaver(NewAutoSavePath());

        // act
        var act = () => App.RunShutdownAsync(
            null!,
            _ => saver,
            _ => null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            SerilogNullLogger.None);

        // assert
        await act.Should().ThrowAsync<ArgumentNullException>("a null host is a programmer error, fail fast");
    }

    [Fact]
    public async Task RunShutdownAsync_NullAutoSaverResolver_SkipsPreFlush_StillStopsHost()
    {
        // arrange — auto-saver resolver returns null (e.g. DI does
        // not have TraceSessionAutoSaver registered). The host MUST
        // still be stopped — skipping auto-save must not abort the
        // shutdown sequence.
        var host = new FakeHost
        {
            Services = new ServiceCollection().BuildServiceProvider(),
        };

        // act
        await App.RunShutdownAsync(
            host,
            _ => null,
            _ => null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            SerilogNullLogger.None);

        // assert
        host.StopCallCount.Should().Be(1,
            "skipping auto-save (resolver returns null) must not abort the host stop");
    }

    [Fact]
    public async Task RunShutdownAsync_HostStopThrows_LogsError_DoesNotPropagate()
    {
        // arrange — host.StopAsync blows up. OnExit must NOT
        // propagate this exception (the process is exiting anyway;
        // rethrowing would crash the dispatcher after we've already
        // torn down state).
        var path = NewAutoSavePath();
        var (saver, _) = MakeTraceSaver(path);
        var host = new FakeHost
        {
            Services = new ServiceCollection().BuildServiceProvider(),
        };
        host.SetStopImpl(_ => Task.FromException(new InvalidOperationException("boom")));

        // act
        var act = () => App.RunShutdownAsync(
            host,
            _ => saver,
            _ => null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            SerilogNullLogger.None);

        // assert
        await act.Should().NotThrowAsync(
            "host teardown failures are tolerated on the exit path — the process is leaving");
        host.StopCallCount.Should().Be(1, "StopAsync is still invoked even when it ultimately throws");
    }

    [Fact]
    public async Task RunShutdownAsync_AutoSaveThrows_LogsWarning_DoesNotBlockHostStop()
    {
        // arrange — the resolver throws (mimicking a disposed
        // service provider or a misbehaving DI factory). Auto-save
        // failure must NOT abort the shutdown; the host still has
        // to stop.
        var host = new FakeHost
        {
            Services = new ServiceCollection().BuildServiceProvider(),
        };

        // act — resolver raises on every call.
        await App.RunShutdownAsync(
            host,
            _ => throw new InvalidOperationException("vm provider exploded"),
            _ => null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            SerilogNullLogger.None);

        // assert
        host.StopCallCount.Should().Be(1,
            "an auto-save failure must not abort the host stop");
    }

    // ========== v3.7.0 MINOR Chunk 3: Replay auto-save ordering ==========

    [Fact]
    public async Task RunShutdownAsync_BothAutoSaversRunBeforeHostStop()
    {
        // arrange — both Trace and Replay are wired. Both must run
        // BEFORE host stop; Trace runs first.
        var tracePath = NewAutoSavePath();
        var replayPath = NewAutoSavePath();
        var (traceSaver, traceProvider) = MakeTraceSaver(tracePath);
        var (replaySaver, replayProvider) = MakeReplaySaver(replayPath);
        var host = new FakeHost
        {
            Services = new ServiceCollection().BuildServiceProvider(),
        };

        // act
        await App.RunShutdownAsync(
            host,
            _ => traceSaver,
            _ => replaySaver,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            SerilogNullLogger.None);

        // assert
        traceProvider.ResolvedAt.Should().NotBeNull();
        replayProvider.ResolvedAt.Should().NotBeNull();
        traceProvider.ResolvedAt!.Value.Should().BeOnOrBefore(replayProvider.ResolvedAt!.Value,
            "Trace auto-save must run before Replay auto-save");
        replayProvider.ResolvedAt!.Value.Should().BeOnOrBefore(host.StopAsyncCalledAt!.Value,
            "Replay auto-save must run BEFORE host stop");
        host.StopCallCount.Should().Be(1);
    }

    [Fact]
    public async Task RunShutdownAsync_TraceSucceeds_ReplayThrows_StillStopsHost()
    {
        // arrange — Trace runs fine, Replay saver throws. Host must
        // still be stopped (exception isolation per saver).
        var tracePath = NewAutoSavePath();
        var replayPath = NewAutoSavePath();
        var (traceSaver, _) = MakeTraceSaver(tracePath);
        var badReplayProvider = Substitute.For<IReplayViewModelProvider>();
        badReplayProvider.GetCurrent()
            .Returns(_ => throw new InvalidOperationException("replay saver exploded"));
        var badReplaySaver = new ReplaySessionAutoSaver(
            badReplayProvider,
            new TraceSessionLibrary(replayPath, NullLogger<TraceSessionLibrary>.Instance),
            new InMemoryPrefsStore(),
            Substitute.For<IMessageBoxPrompt>(),
            NullLogger<ReplaySessionAutoSaver>.Instance,
            replayPath);
        var host = new FakeHost
        {
            Services = new ServiceCollection().BuildServiceProvider(),
        };

        // act
        await App.RunShutdownAsync(
            host,
            _ => traceSaver,
            _ => badReplaySaver,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            SerilogNullLogger.None);

        // assert — host still got stopped despite Replay throw
        host.StopCallCount.Should().Be(1,
            "a Replay auto-save throw must not abort the host stop");
        File.Exists(tracePath).Should().BeTrue("the Trace auto-save should have completed before the Replay throw");
    }

    [Fact]
    public async Task RunShutdownAsync_ReplayResolverReturnsNull_TraceStillRuns()
    {
        // arrange — Replay resolver returns null (not registered). Trace
        // must still run. Host must still be stopped.
        var tracePath = NewAutoSavePath();
        var (traceSaver, traceProvider) = MakeTraceSaver(tracePath);
        var host = new FakeHost
        {
            Services = new ServiceCollection().BuildServiceProvider(),
        };

        // act
        await App.RunShutdownAsync(
            host,
            _ => traceSaver,
            _ => null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            SerilogNullLogger.None);

        // assert
        traceProvider.ResolvedAt.Should().NotBeNull("Trace must still run when Replay is null");
        host.StopCallCount.Should().Be(1);
    }
}
