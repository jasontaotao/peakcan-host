using System.Collections.ObjectModel;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.App.Services.Nm;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// VM 生命周期资源释放（spec §10.1.2）。
/// <para>
/// profile watcher 持有 <c>FileSystemWatcher</c> 句柄并订阅本 VM：不随 Dispose 释放
/// 会让「关窗 → 改 profile → 后台重算」继续作用在已关闭窗口的 VM 上，
/// 并且每开关一次 Trace Viewer 泄漏一个 watcher + 一个被它钉住的 VM。
/// </para>
/// </summary>
public class NmViewModelDisposeTests
{
    private static readonly string[] Reasons = ["MsgType=CANNM"];

    private sealed class FakeProfileWatcher : INmProfileWatcher
    {
        public event Action<IReadOnlyList<string>>? Changed;
        public bool Disposed { get; private set; }

        public void Track(IReadOnlyList<NmProfileDependency> dependencies) { }

        public void RaiseChanged(params string[] ids) => Changed?.Invoke(ids);

        public void Dispose() => Disposed = true;
    }

    private static NmReport Report()
        => new(
            new NmWindow(0, 10, "full"), "src", null,
            Array.Empty<NmEpisode>(), Array.Empty<NmMessageReport>(), Array.Empty<NmNodeReport>(),
            Array.Empty<NmEvent>(), Array.Empty<NmFinding>(), Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<NmCapabilityState>(), Array.Empty<NmInputMissing>(),
            Array.Empty<NmTruncation>(), StandardId: "a@1");

    private static NmProfile Profile()
        => new(
            NmProfileSource.Dbc,
            [new NmMessageDef("0x500", false, "NM", "A", 8, true, Reasons, 500, 2000)],
            new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(), new NmProvenance());

    private static TraceViewerViewModel NewVm()
    {
        var session = Substitute.For<ITraceSessionService>();
        session.WatchedSignals.Returns(new ObservableCollection<WatchedSignalRow>());
        session.SignalGroups.Returns(new ObservableCollection<WatchedSignalGroup>());
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>());
        var dbcService = Substitute.For<DbcService>(NullLogger<DbcService>.Instance);
        var libPath = Path.Combine(Path.GetTempPath(), $"nm-dispose-{Guid.NewGuid():N}.tmtrace");
        return new TraceViewerViewModel(session, registry, dbcService,
            NullLogger<TraceViewerViewModel>.Instance,
            new TraceSessionLibrary(libPath, NullLogger<TraceSessionLibrary>.Instance));
    }

    [Fact]
    public void Dispose_ReleasesProfileWatcher()
    {
        var fake = new FakeProfileWatcher();
        var vm = NewVm();
        vm.NmProfileWatcherFactory = _ => fake;
        vm.ApplyNmReport(new NmToolSnapshot(
            "src", null, null, null, Profile(), Report(), Array.Empty<ReplayFrame>())
        {
            ProfileCandidates =
            [
                new NmStandardProfileLocator.Candidate(
                    @"C:\profiles\a@1.nmstd.yaml", "a", "1", NmProfileSearchLevel.DbcDirectory),
            ],
            ProfileLevel = NmProfileSearchLevel.DbcDirectory,
        });

        vm.Dispose();

        fake.Disposed.Should().BeTrue(
            "关窗必须释放 watcher，否则句柄泄漏且会继续在已关闭窗口的 VM 上重算");
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        // WPF 关窗路径与测试都可能重复 Dispose；重复调用不得抛异常
        // （FileSystemWatcher 二次 Dispose 本身安全，但订阅/退订顺序要能承受）。
        var vm = NewVm();
        vm.Dispose();
        vm.Dispose();
    }
}
