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
/// profile watcher 的 VM 接线（spec §10.1.2）。
/// <para>
/// 真 <see cref="FileSystemWatcher"/> 不参与测试：时序不可控、在 CI 上不稳定。
/// 这里用假 watcher 驱动 <c>Changed</c>，验证「失效后清缓存 + 重算 + 在途运行被丢弃」。
/// </para>
/// </summary>
public class NmProfileWatcherWiringTests
{
    private static readonly string[] Reasons = ["MsgType=CANNM"];

    /// <summary>可手动触发的 watcher 替身。</summary>
    private sealed class FakeProfileWatcher : INmProfileWatcher
    {
        public event Action<IReadOnlyList<string>>? Changed;
        public IReadOnlyList<NmProfileDependency> Dependencies { get; private set; }
            = Array.Empty<NmProfileDependency>();
        public bool Disposed { get; private set; }

        public void Track(IReadOnlyList<NmProfileDependency> dependencies)
            => Dependencies = dependencies;

        public void RaiseChanged(params string[] ids) => Changed?.Invoke(ids);

        public void Dispose() => Disposed = true;
    }

    private static NmReport Report(string? standardId = null)
        => new(
            new NmWindow(0, 10, "full"), "src", null,
            Array.Empty<NmEpisode>(), Array.Empty<NmMessageReport>(), Array.Empty<NmNodeReport>(),
            Array.Empty<NmEvent>(), Array.Empty<NmFinding>(), Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<NmCapabilityState>(), Array.Empty<NmInputMissing>(),
            Array.Empty<NmTruncation>(), StandardId: standardId);

    private static NmProfile Profile()
        => new(
            NmProfileSource.Dbc,
            [new NmMessageDef("0x500", false, "NM", "A", 8, true, Reasons, 500, 2000)],
            new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(), new NmProvenance());

    private static NmToolSnapshot Snapshot(
        string? standardId = null,
        IReadOnlyList<NmStandardProfileLocator.Candidate>? candidates = null)
        => new("src", null, null, null, Profile(), Report(standardId), Array.Empty<ReplayFrame>())
        {
            ProfileCandidates = candidates ?? Array.Empty<NmStandardProfileLocator.Candidate>(),
            ProfileLevel = candidates is { Count: > 0 } ? NmProfileSearchLevel.DbcDirectory : null,
        };

    private static TraceViewerViewModel NewVm()
    {
        var session = Substitute.For<ITraceSessionService>();
        session.WatchedSignals.Returns(new ObservableCollection<WatchedSignalRow>());
        session.SignalGroups.Returns(new ObservableCollection<WatchedSignalGroup>());
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>());
        var dbcService = Substitute.For<DbcService>(NullLogger<DbcService>.Instance);
        var libPath = Path.Combine(Path.GetTempPath(), $"nm-watch-vm-{Guid.NewGuid():N}.tmtrace");
        return new TraceViewerViewModel(session, registry, dbcService,
            NullLogger<TraceViewerViewModel>.Instance,
            new TraceSessionLibrary(libPath, NullLogger<TraceSessionLibrary>.Instance));
    }

    [Fact]
    public void ApplyNmReport_WithCandidate_StartsWatcherOnItsDirectory()
    {
        var created = new List<string>();
        var fake = new FakeProfileWatcher();
        var vm = NewVm();
        vm.NmProfileWatcherFactory = dir =>
        {
            created.Add(dir);
            return fake;
        };

        vm.ApplyNmReport(Snapshot("a@1",
        [
            new NmStandardProfileLocator.Candidate(
                @"C:\profiles\a@1.nmstd.yaml", "a", "1", NmProfileSearchLevel.DbcDirectory),
        ]));

        created.Should().ContainSingle().Which.Should().Be(@"C:\profiles");
        fake.Dependencies.Select(d => d.Id).Should().Contain("a@1");
    }

    [Fact]
    public void ApplyNmReport_NoCandidate_DoesNotStartWatcher()
    {
        var created = new List<string>();
        var vm = NewVm();
        vm.NmProfileWatcherFactory = dir =>
        {
            created.Add(dir);
            return new FakeProfileWatcher();
        };

        vm.ApplyNmReport(Snapshot());

        created.Should().BeEmpty("没有 profile 就没有要监听的目录");
    }

    [Fact]
    public void ApplyNmReport_SameDirectoryTwice_ReusesWatcher()
    {
        var created = new List<string>();
        var vm = NewVm();
        vm.NmProfileWatcherFactory = dir =>
        {
            created.Add(dir);
            return new FakeProfileWatcher();
        };
        var candidate = new[]
        {
            new NmStandardProfileLocator.Candidate(
                @"C:\profiles\a@1.nmstd.yaml", "a", "1", NmProfileSearchLevel.DbcDirectory),
        };

        vm.ApplyNmReport(Snapshot("a@1", candidate));
        vm.ApplyNmReport(Snapshot("a@1", candidate));

        created.Should().ContainSingle("同目录不该重建 FileSystemWatcher");
    }

    [Fact]
    public void ApplyNmReport_DirectoryChanges_ReplacesWatcher()
    {
        var fakes = new List<FakeProfileWatcher>();
        var vm = NewVm();
        vm.NmProfileWatcherFactory = _ =>
        {
            var fake = new FakeProfileWatcher();
            fakes.Add(fake);
            return fake;
        };

        vm.ApplyNmReport(Snapshot("a@1",
        [
            new NmStandardProfileLocator.Candidate(
                @"C:\p1\a@1.nmstd.yaml", "a", "1", NmProfileSearchLevel.DbcDirectory),
        ]));
        vm.ApplyNmReport(Snapshot("b@1",
        [
            new NmStandardProfileLocator.Candidate(
                @"C:\p2\b@1.nmstd.yaml", "b", "1", NmProfileSearchLevel.DbcDirectory),
        ]));

        fakes.Should().HaveCount(2);
        fakes[0].Disposed.Should().BeTrue("旧目录的监听器必须释放，否则句柄泄漏");
    }

    [Fact]
    public void WatcherFactoryThrows_ReportsStatusInsteadOfSilentFailure()
    {
        // 目录不存在/无权限时不能静默不监听：用户会以为改了 profile 会自动生效。
        var vm = NewVm();
        vm.NmProfileWatcherFactory = _ => throw new IOException("目录不可访问");

        vm.ApplyNmReport(Snapshot("a@1",
        [
            new NmStandardProfileLocator.Candidate(
                @"C:\profiles\a@1.nmstd.yaml", "a", "1", NmProfileSearchLevel.DbcDirectory),
        ]));

        vm.NmProfileWatcherStatus.Should().Contain("无法监听").And.Contain("目录不可访问");
    }

    [Fact]
    public void ClearNmReport_DisposesWatcher()
    {
        var fake = new FakeProfileWatcher();
        var vm = NewVm();
        vm.NmProfileWatcherFactory = _ => fake;
        vm.ApplyNmReport(Snapshot("a@1",
        [
            new NmStandardProfileLocator.Candidate(
                @"C:\profiles\a@1.nmstd.yaml", "a", "1", NmProfileSearchLevel.DbcDirectory),
        ]));

        vm.ClearNmReportPublic();

        fake.Disposed.Should().BeTrue();
    }

    [Fact]
    public void ProfileChange_InvalidatesCachedReports()
    {
        // spec §10.1.2：文件变化必须让旧报告不再命中。
        var fake = new FakeProfileWatcher();
        var vm = NewVm();
        vm.NmProfileWatcherFactory = _ => fake;
        vm.ApplyNmReport(Snapshot("a@1",
        [
            new NmStandardProfileLocator.Candidate(
                @"C:\profiles\a@1.nmstd.yaml", "a", "1", NmProfileSearchLevel.DbcDirectory),
        ]));

        var key = new NmCacheKey(
            "src", null, 0, 10, "full", 10, 1, "hash", NmAnalyzer.EngineVersion);
        vm.StoreNmReport(key, Report());
        vm.TryGetCachedNmReport(key, out _).Should().BeTrue();

        // 无 UI dispatcher 时重算会直接触发；测试里重算因没有 trace 源而失败，
        // 但缓存失效这件事已经发生，这是本条断言的对象。
        fake.RaiseChanged("a@1");

        vm.TryGetCachedNmReport(key, out _).Should().BeFalse();
    }

    [Fact]
    public void ProfileChange_DiscardsInFlightRunResult()
    {
        // 双条件校验的第二条：profile 变了，即使 CTS 仍是「当前」，
        // 在途运行的结果也不得应用（它基于旧 profile）。
        var fake = new FakeProfileWatcher();
        var vm = NewVm();
        vm.NmProfileWatcherFactory = _ => fake;
        vm.ApplyNmReport(Snapshot("a@1",
        [
            new NmStandardProfileLocator.Candidate(
                @"C:\profiles\a@1.nmstd.yaml", "a", "1", NmProfileSearchLevel.DbcDirectory),
        ]));

        // 记下当前代数，模拟「watcher 失效后」。
        var before = vm.NmProfileGenerationForTest;
        fake.RaiseChanged("a@1");

        vm.NmProfileGenerationForTest.Should().BeGreaterThan(before,
            "失效后必须递增代数，使在途运行的回写比对失败");
    }
}
