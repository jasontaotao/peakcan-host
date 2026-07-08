using System.ComponentModel;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Services;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// v3.14.0 MINOR A2+A3+A4 regression: when a VM subscribes to a
/// DI-singleton event in ctor and never cancels in Dispose, the
/// singleton holds a strong reference to the handler closure, pinning
/// the VM for the app lifetime. Each close+reopen leaks a full VM
/// (Frames list, Signals collection, ChartViewModel state).
///
/// These tests assert the post-Dispose contract: raising the
/// singleton's event must not trigger any side effect on a disposed VM.
/// </summary>
public sealed class EventSubscriptionLeakTests : IDisposable
{
    private readonly string _libraryPath =
        Path.Combine(Path.GetTempPath(), $"tmtrace-leak-{Guid.NewGuid():N}.tmtrace");
    private readonly string _recentPath =
        Path.Combine(Path.GetTempPath(), $"recent-leak-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { if (File.Exists(_libraryPath)) File.Delete(_libraryPath); } catch { /* best-effort */ }
        try { if (File.Exists(_recentPath)) File.Delete(_recentPath); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private static IFileDialogService NoopDialog() => Substitute.For<IFileDialogService>();

    private static TraceSessionLibrary NewLibrary(string path) =>
        new(path, NullLogger<TraceSessionLibrary>.Instance);

    private static RecentSessionsService NewRecent(string path) =>
        new(NullLogger<RecentSessionsService>.Instance, path);

    // ============================================================
    // A2 — ReplayViewModel disposes LoopRewound subscription.
    // ============================================================

    [Fact]
    public void ReplayViewModel_Dispose_CancelsIReplayServiceLoopRewoundSubscription()
    {
        // Build a real ReplayService (DI-singleton shape) wired to a
        // stub sink. The handler under test (OnLoopRewound) writes
        // StatusMessage when invoked. Post-Dispose the handler must
        // not fire — the singleton would otherwise pin the VM.
        var sink = Substitute.For<IReplayFrameSink>();
        var svc = new ReplayService(sink, NullLogger<ReplayService>.Instance);

        var vm = new ReplayViewModel(
            svc,
            NoopDialog(),
            Substitute.For<IAscContentHasher>(),
            Substitute.For<IAscLocator>(),
            NewLibrary(_libraryPath),
            NewRecent(_recentPath));

        // Snapshot StatusMessage so we can detect any change caused
        // by a late OnLoopRewound invocation. The ctor seeds it to
        // "Ready"; a live OnLoopRewound would overwrite it with a
        // "Rewind: loop region (...)" string.
        var statusBefore = vm.StatusMessage;
        statusBefore.Should().Be("Ready");

        vm.Dispose();

        // Post-Dispose contract: raising the singleton event must NOT
        // invoke OnLoopRewound on the disposed VM. LoopRewound is a
        // field-like event on ReplayService (no public raise method
        // exposed), so we raise it by invoking the backing delegate
        // field directly. Pre-fix the backing delegate still contains
        // the disposed VM's OnLoopRewound → it would re-enter the VM
        // and overwrite StatusMessage. Post-fix Dispose -= removed
        // the delegate, so DynamicInvoke sees an empty invocation
        // list and StatusMessage stays untouched.
        var field = typeof(ReplayService).GetField(
            nameof(IReplayService.LoopRewound),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "ReplayService.LoopRewound backing field not found");
        var del = field.GetValue(svc) as Delegate;
        if (del is not null)
        {
            // Invoke whatever handlers are currently subscribed.
            // Post-fix this should be null (or an empty MulticastDelegate).
            foreach (var sub in del.GetInvocationList())
            {
                sub.DynamicInvoke(svc, new LoopRegionRewoundEventArgs(0.0, 1.0));
            }
        }

        vm.StatusMessage.Should().Be(
            statusBefore,
            "disposed VM must not have its StatusMessage mutated by a late LoopRewound raise");
    }

    // ============================================================
    // A3 — ReplayViewModel disposes RecentSessionsService
    // PropertyChanged subscription (post-lambda-promotion).
    // ============================================================

    [Fact]
    public void ReplayViewModel_Dispose_CancelsRecentSessionsServicePropertyChangedSubscription()
    {
        // Build a real RecentSessionsService (DI-singleton shape) and
        // an IReplayService stub. Pre-fix the ctor used a lambda that
        // could not be -=ed, so every PropertyChanged after Dispose
        // would still re-enter the disposed VM and call
        // RefreshRecentEntries (which touches an ObservableCollection
        // bound to the now-dead VM). Post-fix the lambda was promoted
        // to OnRecentSessionsPropertyChanged and Dispose -=s it.
        var sink = Substitute.For<IReplayFrameSink>();
        var svc = Substitute.For<IReplayService>();
        svc.TotalDuration.Returns(10.0);
        var recent = NewRecent(_recentPath);

        // Confirm a probe can observe PropertyChanged at all (event
        // infrastructure sanity check). If this fails the test is
        // meaningless — bail out before exercising the leak path.
        var probeFired = false;
        PropertyChangedEventHandler probe = (_, _) => probeFired = true;
        recent.PropertyChanged += probe;
        recent.Add("/tmp/leak-probe.asc");
        probeFired.Should().BeTrue("Add() must raise PropertyChanged so the probe sanity-check is meaningful");
        recent.PropertyChanged -= probe;

        var vm = new ReplayViewModel(
            svc,
            NoopDialog(),
            Substitute.For<IAscContentHasher>(),
            Substitute.For<IAscLocator>(),
            NewLibrary(_libraryPath),
            recent);

        vm.Dispose();

        // Post-Dispose: any PropertyChanged raise on the singleton
        // must NOT have a path back into the disposed VM. We invoke
        // the PropertyChanged backing field directly (the public
        // event has no raise accessor).
        var field = typeof(RecentSessionsService).GetField(
            nameof(INotifyPropertyChanged.PropertyChanged),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "RecentSessionsService.PropertyChanged backing field not found");
        var del = field.GetValue(recent) as Delegate;
        // After Dispose, the singleton's PropertyChanged delegate
        // must NOT contain the disposed VM's
        // OnRecentSessionsPropertyChanged in its invocation list.
        // Iterate the live invocation list and assert.
        if (del is not null)
        {
            foreach (var sub in del.GetInvocationList())
            {
                // The disposed VM's method is bound to the VM target
                // instance. If any subscriber's Target is our VM, the
                // leak is open.
                sub.Target.Should().NotBeSameAs(
                    vm,
                    "Post-Dispose the singleton must not hold a delegate targeting the disposed VM");
            }
        }
    }

    // ============================================================
    // A4 — TraceViewerViewModel disposes DbcService.DbcLoaded
    // subscription.
    // ============================================================

    [Fact]
    public void TraceViewerViewModel_Dispose_CancelsDbcServiceDbcLoadedSubscription()
    {
        // Build a real DbcService (DI-singleton shape) and a TVM wired
        // to a stub registry + temp-path library. Pre-fix the ctor
        // xmldoc defended "no unsubscribe because DbcService is a DI
        // singleton" — backwards reasoning. Post-fix Dispose -=s the
        // DbcLoaded handler.
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>());

        var vm = new TraceViewerViewModel(
            registry,
            dbc,
            NullLogger<TraceViewerViewModel>.Instance,
            NewLibrary(_libraryPath));

        var signalsBefore = vm.Signals.Count;
        signalsBefore.Should().Be(0, "empty registry → no signals");

        vm.Dispose();

        // Post-Dispose contract: DbcLoaded raise must not reach the
        // disposed VM. DbcLoaded is a field-like event (no public
        // raise accessor), so we iterate the backing delegate field's
        // invocation list and assert no subscriber targets the
        // disposed VM.
        var field = typeof(DbcService).GetField(
            "DbcLoaded",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("DbcService.DbcLoaded backing field not found");
        var del = field.GetValue(dbc) as Delegate;
        if (del is not null)
        {
            foreach (var sub in del.GetInvocationList())
            {
                sub.Target.Should().NotBeSameAs(
                    vm,
                    "Post-Dispose the singleton must not hold a delegate targeting the disposed VM");
            }
        }
    }
}