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
using SerilogLogger = Serilog.ILogger;
using SerilogNullLogger = Serilog.Core.Logger;

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
    private sealed class RecordingVmProvider : ITraceViewerViewModelProvider
    {
        public DateTimeOffset? ResolvedAt { get; private set; }
        private readonly TraceViewerViewModel _vm;
        public RecordingVmProvider(TraceViewerViewModel vm) => _vm = vm;
        public TraceViewerViewModel? GetCurrent()
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
    private (TraceSessionAutoSaver Saver, RecordingVmProvider Provider) MakeSaver(string path)
    {
        var library = new TraceSessionLibrary(path, NullLogger<TraceSessionLibrary>.Instance);
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("src1", "shutdown-test", @"C:/rec.asc", OxyColors.Red),
        });
        var dbc = Substitute.For<DbcService>(Substitute.For<Microsoft.Extensions.Logging.ILogger<DbcService>>());
        var vm = new TraceViewerViewModel(
            registry, dbc, NullLogger<TraceViewerViewModel>.Instance, library, fileDialog: null);
        var provider = new RecordingVmProvider(vm);
        var prefs = Substitute.For<IAutoSavePrefsStore>();
        var prompt = Substitute.For<IMessageBoxPrompt>();
        return (new TraceSessionAutoSaver(
            provider, library, prefs, prompt,
            NullLogger<TraceSessionAutoSaver>.Instance, path),
            provider);
    }

    [Fact]
    public async Task RunShutdownAsync_AutoSaverRunsBeforeHostStop()
    {
        // arrange
        var path = NewAutoSavePath();
        var (saver, provider) = MakeSaver(path);
        var host = new FakeHost
        {
            Services = new ServiceCollection().BuildServiceProvider(),
        };

        // act
        await App.RunShutdownAsync(
            host,
            _ => saver,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            SerilogNullLogger.None);

        // assert
        provider.ResolvedAt.Should().NotBeNull(
            "the auto-save resolver must run so the VM is captured before the host is stopped");
        host.StopAsyncCalledAt.Should().NotBeNull(
            "StopAsync must be invoked exactly once during shutdown");
        provider.ResolvedAt!.Value.Should().BeOnOrBefore(host.StopAsyncCalledAt!.Value,
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
        var (saver, _) = MakeSaver(NewAutoSavePath());

        // act
        var act = () => App.RunShutdownAsync(
            null!,
            _ => saver,
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
        var (saver, _) = MakeSaver(path);
        var host = new FakeHost
        {
            Services = new ServiceCollection().BuildServiceProvider(),
        };
        host.SetStopImpl(_ => Task.FromException(new InvalidOperationException("boom")));

        // act
        var act = () => App.RunShutdownAsync(
            host,
            _ => saver,
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
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            SerilogNullLogger.None);

        // assert
        host.StopCallCount.Should().Be(1,
            "an auto-save failure must not abort the host stop");
    }
}