using System.IO;
using System.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ScottPlot;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core.Replay;
using PeakCan.HIL.Core.Services;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Trace;

/// <summary>
/// v3.x (会话状态剥离 Task 1): 钉住 <see cref="TraceSessionService"/> 的
/// OpenSessionAsync（unload/load 序列、missing 收集、DisplayName/Color 重盖印、
/// locator 重定位、master 反查）与 BuildSnapshot（sources 数量 + master/filter/
/// watch/groups 字段）。library 用真实 <see cref="TraceSessionLibrary"/>（sealed，
/// 不可替身）写临时 .tmtrace；registry / dbcService / locator / hasher 用 NSubstitute。
/// </summary>
public sealed class TraceSessionServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _files = new();

    /// <summary>CA1861: hoisted 常量数组，避免重复 inline 数组参数。</summary>
    private static readonly IReadOnlyList<string> SampleGroupSignalKeys = new[] { "0x100.SigA" };

    public TraceSessionServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"trace-svc-{Guid.NewGuid():N}");
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

    private string NewTempFile(string name)
    {
        var p = Path.Combine(_tempDir, name);
        _files.Add(p);
        return p;
    }

    /// <summary>真实 library（sealed，不可替身）写临时 .tmtrace 文件。</summary>
    private (TraceSessionLibrary Library, string BundlePath) NewLibrary()
    {
        var path = NewTempFile($"bundle-{Guid.NewGuid():N}.tmtrace");
        return (new TraceSessionLibrary(path, NullLogger<TraceSessionLibrary>.Instance), path);
    }

    private TraceSessionService MakeService(
        ITraceSessionRegistry registry,
        TraceSessionLibrary library,
        DbcService? dbcService = null,
        IAscLocator? locator = null,
        IAscContentHasher? hasher = null)
    {
        dbcService ??= Substitute.For<DbcService>(NullLogger<DbcService>.Instance);
        locator ??= Substitute.For<IAscLocator>();
        hasher ??= Substitute.For<IAscContentHasher>();
        var builder = new TraceSessionSnapshotBuilder(hasher);
        return new TraceSessionService(
            registry, library, dbcService, locator, hasher, builder,
            NullLogger<TraceSessionService>.Instance);
    }

    private static TraceSource MakeSource(
        string id, string displayName, string path,
        byte r = 0, byte g = 0, byte b = 0, byte a = 0) =>
        new(id, displayName, path, new Color(r, g, b, a), new LineStyle());

    [Fact]
    public async Task OpenSessionAsync_CollectsMissingPaths_AndReStampsLoadedSource()
    {
        // arrange
        var realPath = NewTempFile("traceA.asc");
        File.WriteAllText(realPath, "frames");
        var missingPath = Path.Combine(_tempDir, "gone.asc");   // 不存在
        var (library, bundlePath) = NewLibrary();

        var existing = MakeSource("existing", "old", realPath);
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource> { existing });

        var loaded = MakeSource("srcA", "traceA", realPath);    // LoadAsync 默认 DisplayName = 文件名
        registry.LoadAsync(realPath).Returns(loaded);
        registry.LoadAsync(missingPath).Returns(Task.FromException<TraceSource>(new FileNotFoundException()));

        library.Save(new TraceSessionBundleDto
        {
            Version = 1,
            Schema = "tmtrace/v1",
            GlobalCanIdFilter = "0x100",
            Sources = new List<BundleSourceDto>
            {
                new()
                {
                    SourceId = "oldIdA",
                    DisplayName = "renamedA",   // 与文件名不同 → 重新盖印 DisplayName
                    Path = realPath,
                    ColorA = 255, ColorR = 10, ColorG = 20, ColorB = 30,
                    StrokeStyle = "Solid",
                    CanIdFilter = "0x100, 0x200",
                },
                new()
                {
                    SourceId = "oldIdB",
                    DisplayName = "gone",
                    Path = missingPath,
                },
            },
            Playback = null,
        });

        var sut = MakeService(registry, library);

        // act
        var missing = await sut.OpenSessionAsync(bundlePath);

        // assert
        missing.Should().ContainSingle().Which.Should().Be(missingPath);
        await registry.Received(1).UnloadAsync(existing.SourceId);
        await registry.Received(1).LoadAsync(realPath);
        await registry.Received(1).LoadAsync(missingPath);
        loaded.DisplayName.Should().Be("renamedA", "bundle DisplayName 与文件名不同 → 覆盖默认文件名");
        loaded.Color.Should().Be(new Color(10, 20, 30, 255));
        loaded.CanIdFilter.Should().Be("0x100, 0x200");
        sut.GlobalCanIdFilter.Should().Be("0x100");
        sut.HasContent.Should().BeTrue();
    }

    [Fact]
    public async Task OpenSessionAsync_KeepsFilenameDisplayName_WhenBundleNameMatches()
    {
        // arrange：bundle DisplayName == 文件名 → 不重新盖印，保留 registry LoadAsync 盖的默认名
        var realPath = NewTempFile("traceA.asc");
        File.WriteAllText(realPath, "frames");
        var (library, bundlePath) = NewLibrary();

        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>());
        var loaded = MakeSource("srcA", "traceA", realPath);
        registry.LoadAsync(realPath).Returns(loaded);

        library.Save(new TraceSessionBundleDto
        {
            Version = 1,
            Schema = "tmtrace/v1",
            Sources = new List<BundleSourceDto>
            {
                new() { SourceId = "oldIdA", DisplayName = "traceA", Path = realPath },
            },
            Playback = null,
        });

        var sut = MakeService(registry, library);

        // act
        var missing = await sut.OpenSessionAsync(bundlePath);

        // assert
        missing.Should().BeEmpty();
        loaded.DisplayName.Should().Be("traceA");
    }

    [Fact]
    public async Task OpenSessionAsync_RestoresMasterSourceId_ByDisplayName()
    {
        // arrange：bundle 里 master 是录制时的旧 id；load 后按 DisplayName 反查新 SourceId
        var realPath = NewTempFile("traceA.asc");
        File.WriteAllText(realPath, "frames");
        var (library, bundlePath) = NewLibrary();

        var current = new List<TraceSource>();
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(_ => current);   // 动态：load 后反映新 source
        registry.LoadAsync(realPath).Returns(_ =>
        {
            var src = MakeSource("newIdA", "traceA", realPath);
            current.Add(src);
            return src;
        });

        library.Save(new TraceSessionBundleDto
        {
            Version = 1,
            Schema = "tmtrace/v1",
            Sources = new List<BundleSourceDto>
            {
                new() { SourceId = "oldIdA", DisplayName = "traceA", Path = realPath },
            },
            Playback = new BundlePlaybackDto
            {
                MasterSourceId = "oldIdA", Loop = true, Speed = 2.0, ScrubberValue = 5.0,
            },
        });

        var sut = MakeService(registry, library);

        // act
        var missing = await sut.OpenSessionAsync(bundlePath);

        // assert
        missing.Should().BeEmpty();
        sut.MasterSourceId.Should().Be("newIdA", "bundle 的 master 旧 id 通过 DisplayName 反查到新 SourceId");
    }

    [Fact]
    public async Task OpenSessionAsync_LocatorRelocatesMissingAsc_ByContentHash()
    {
        // arrange：记录路径缺失 + contentHash 非空 → locator 按哈希找到重定位文件
        var relocatedPath = NewTempFile("relocated.asc");
        File.WriteAllText(relocatedPath, "frames");
        var missingPath = Path.Combine(_tempDir, "moved.asc");   // bundle 里记录的原路径已不存在
        var (library, bundlePath) = NewLibrary();

        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>());
        registry.LoadAsync(relocatedPath).Returns(MakeSource("s1", "moved", relocatedPath));

        var locator = Substitute.For<IAscLocator>();
        locator.LocateAsync("abc123", Arg.Any<CancellationToken>()).Returns(relocatedPath);

        library.Save(new TraceSessionBundleDto
        {
            Version = 1,
            Schema = "tmtrace/v1",
            Sources = new List<BundleSourceDto>
            {
                new() { SourceId = "old1", DisplayName = "moved", Path = missingPath, ContentHash = "abc123" },
            },
            Playback = null,
        });

        var sut = MakeService(registry, library, locator: locator);

        // act
        var missing = await sut.OpenSessionAsync(bundlePath);

        // assert
        missing.Should().BeEmpty();
        await registry.Received(1).LoadAsync(relocatedPath);
        await registry.DidNotReceive().LoadAsync(missingPath);
    }

    [Fact]
    public async Task OpenSessionAsync_ReloadsDbc_WhenBundleCarriesExistingPath()
    {
        // arrange：bundle 携带存在的 DBC 路径 → dbcService.LoadAsync 被调用且收到该 path
        var realPath = NewTempFile("traceA.asc");
        File.WriteAllText(realPath, "frames");
        var dbcPath = NewTempFile("vehicle.dbc");
        File.WriteAllText(dbcPath, "BO_ 100 MSG: 8 SG_ Sig : 0|8@1+ (1,0) [0|0] \"\" 0");
        var (library, bundlePath) = NewLibrary();

        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>());
        registry.LoadAsync(realPath).Returns(MakeSource("s1", "traceA", realPath));

        var dbcService = Substitute.For<DbcService>(NullLogger<DbcService>.Instance);

        library.Save(new TraceSessionBundleDto
        {
            Version = 1,
            Schema = "tmtrace/v1",
            DbcPath = dbcPath,
            Sources = new List<BundleSourceDto>
            {
                new() { SourceId = "old1", DisplayName = "traceA", Path = realPath },
            },
            Playback = null,
        });

        var sut = MakeService(registry, library, dbcService: dbcService);

        // act
        var missing = await sut.OpenSessionAsync(bundlePath);

        // assert
        missing.Should().BeEmpty();
        await dbcService.Received(1).LoadAsync(dbcPath, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenSessionAsync_UnreadableBundle_ReturnsEmpty()
    {
        // arrange：.tmtrace 文件不存在 → library.Load 返回 null → 空列表、不触 registry
        var (library, bundlePath) = NewLibrary();
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>());
        var sut = MakeService(registry, library);

        // act
        var missing = await sut.OpenSessionAsync(bundlePath);

        // assert
        missing.Should().BeEmpty();
        await registry.DidNotReceive().LoadAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task OpenSessionAsync_RestoresWatchedSignalsAndGroups_FromBundle()
    {
        // arrange：bundle 携带 watch 列表 + 分组。service 是这两个集合的 owner，
        // OpenSessionAsync 必须恢复它们（ledger Minor #2——原 VM.ApplySnapshotAsync
        // 的恢复逻辑迁到 service，否则会话打开后 watch list/groups 静默丢失）。
        var realPath = NewTempFile("traceA.asc");
        File.WriteAllText(realPath, "frames");
        var (library, bundlePath) = NewLibrary();

        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>());
        registry.LoadAsync(realPath).Returns(MakeSource("s1", "traceA", realPath));

        library.Save(new TraceSessionBundleDto
        {
            Version = 1,
            Schema = "tmtrace/v1",
            Sources = new List<BundleSourceDto>
            {
                new() { SourceId = "old1", DisplayName = "traceA", Path = realPath },
            },
            Playback = null,
            WatchedSignals = new List<BundleWatchedSignalDto>
            {
                new()
                {
                    CanIdHex = "0x100", MessageName = "MsgA", SignalName = "SigA",
                    Unit = "rpm", SourceId = "s1", Alias = "转速",
                },
            },
            Groups = new List<BundleGroupDto>
            {
                new() { Id = "g1", Name = "组A", Notes = "notes", SignalKeys = SampleGroupSignalKeys.ToList() },
            },
        });

        var sut = MakeService(registry, library);

        // act
        var missing = await sut.OpenSessionAsync(bundlePath);

        // assert
        missing.Should().BeEmpty();
        sut.WatchedSignals.Should().ContainSingle("bundle 的 watch 列表必须恢复到 service");
        var row = sut.WatchedSignals[0];
        row.CanIdHex.Should().Be("0x100");
        row.MessageName.Should().Be("MsgA");
        row.SignalName.Should().Be("SigA");
        row.Unit.Should().Be("rpm");
        row.SourceId.Should().Be("s1");
        row.Alias.Should().Be("转速", "bundle 的 Alias 非空时必须恢复");
        sut.SignalGroups.Should().ContainSingle("bundle 的分组必须恢复到 service");
        sut.SignalGroups[0].Id.Should().Be("g1");
        sut.SignalGroups[0].Name.Should().Be("组A");
        sut.SignalGroups[0].Notes.Should().Be("notes");
        sut.SignalGroups[0].SignalKeys.Should().BeEquivalentTo(SampleGroupSignalKeys);
    }

    /// <summary>
    /// v3.x (会话状态剥离 Task 5 final, Important #2): 钉住 SessionRestored 事件
    /// ——OpenSessionAsync 恢复完 watch 列表 + 分组后必须恰好触发一次。窗口开着时
    /// VM 靠它补刷 FrameCount / 锚点（恢复发生在最后一次 SourcesChanged 驱动的
    /// RefreshFrameCounts 之后）。
    /// </summary>
    [Fact]
    public async Task OpenSessionAsync_RaisesSessionRestored_AfterWatchRestore()
    {
        // arrange
        var realPath = NewTempFile("traceA.asc");
        File.WriteAllText(realPath, "frames");
        var (library, bundlePath) = NewLibrary();

        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>());
        registry.LoadAsync(realPath).Returns(MakeSource("s1", "traceA", realPath));

        library.Save(new TraceSessionBundleDto
        {
            Version = 1,
            Schema = "tmtrace/v1",
            Sources = new List<BundleSourceDto>
            {
                new() { SourceId = "old1", DisplayName = "traceA", Path = realPath },
            },
            Playback = null,
            WatchedSignals = new List<BundleWatchedSignalDto>
            {
                new() { CanIdHex = "0x100", MessageName = "MsgA", SignalName = "SigA" },
            },
        });

        var sut = MakeService(registry, library);
        var raised = 0;
        sut.SessionRestored += () => raised++;

        // act
        var missing = await sut.OpenSessionAsync(bundlePath);

        // assert
        missing.Should().BeEmpty();
        raised.Should().Be(1, "恢复完 watch/分组后必须触发 SessionRestored");
    }

    /// <summary>
    /// v3.x (独立 review I-4): 打开空 bundle（无 sources）后必须清空残留的
    /// MasterSourceId——否则旧 id 被 BuildSnapshot 写进 auto-save bundle。
    /// </summary>
    [Fact]
    public async Task OpenSessionAsync_EmptyBundle_ClearsStaleMasterSourceId()
    {
        // arrange：registry 先有一个 source（OpenSessionAsync 会卸载它），service 残留旧 master。
        var realPath = NewTempFile("traceA.asc");
        File.WriteAllText(realPath, "frames");
        var (library, bundlePath) = NewLibrary();

        var current = new List<TraceSource> { MakeSource("existing", "traceA", realPath) };
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(_ => current);   // 动态：unload 后反映空列表
        registry.UnloadAsync("existing").Returns(_ =>
        {
            current.RemoveAll(s => s.SourceId == "existing");
            return Task.CompletedTask;
        });

        library.Save(new TraceSessionBundleDto
        {
            Version = 1,
            Schema = "tmtrace/v1",
            Sources = new List<BundleSourceDto>(),   // 空 bundle
            Playback = new BundlePlaybackDto { MasterSourceId = "ghostId" },
        });

        var sut = MakeService(registry, library);
        sut.MasterSourceId = "oldId";   // 残留旧 master

        // act
        var missing = await sut.OpenSessionAsync(bundlePath);

        // assert
        missing.Should().BeEmpty();
        sut.MasterSourceId.Should().BeNull("空 bundle 打开后必须清空残留的 MasterSourceId");
    }

    /// <summary>
    /// v3.x (独立 review I-1, Important #1): 窗口开着时打开 bundle master = 第二个
    /// source，VM 的 _masterService 必须经 SessionRestored 重绑到恢复的 master。
    /// service 恢复 master 只改 service.MasterSourceId；VM 不重绑则 UI 显示 master=B
    /// 但播放/seek 仍驱动 source A。
    /// </summary>
    [Fact]
    public async Task OpenSessionAsync_RestoredNonFirstMaster_RebindsOpenWindowMasterService()
    {
        // arrange：两个 source 的 bundle，master 是第二个（录制 id oldIdB → 反查 newId2）。
        var realPathA = NewTempFile("traceA.asc");
        var realPathB = NewTempFile("traceB.asc");
        File.WriteAllText(realPathA, "frames");
        File.WriteAllText(realPathB, "frames");
        var (library, bundlePath) = NewLibrary();

        var svcA = Substitute.For<ITraceViewerService>();
        svcA.TotalDuration.Returns(10.0);
        var svcB = Substitute.For<ITraceViewerService>();
        svcB.TotalDuration.Returns(20.0);
        var registry = new SimulatedRegistry(id => id == "newId1" ? svcA : svcB);

        library.Save(new TraceSessionBundleDto
        {
            Version = 1,
            Schema = "tmtrace/v1",
            Sources = new List<BundleSourceDto>
            {
                new() { SourceId = "oldIdA", DisplayName = "traceA", Path = realPathA },
                new() { SourceId = "oldIdB", DisplayName = "traceB", Path = realPathB },
            },
            Playback = new BundlePlaybackDto { MasterSourceId = "oldIdB" },
        });

        var dbcService = Substitute.For<DbcService>(NullLogger<DbcService>.Instance);
        var sut = MakeService(registry, library, dbcService: dbcService);

        // 已开窗 VM（transient）：订阅真实 service 的 SessionRestored。
        var vm = new TraceViewerViewModel(
            sut, registry, dbcService, NullLogger<TraceViewerViewModel>.Instance, library);

        // act：service 恢复会话 → 窗口还开着 → VM 应重绑 master
        var missing = await sut.OpenSessionAsync(bundlePath);

        // assert
        missing.Should().BeEmpty();
        vm.MasterSourceId.Should().Be("newId2", "bundle master=第二个 source → 反查到 newId2");
        var masterField = typeof(TraceViewerViewModel).GetField(
            "_masterService",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TraceViewerViewModel._masterService field not found");
        masterField.GetValue(vm).Should().BeSameAs(svcB,
            "VM 的 _masterService 必须重绑到恢复的 master（source B），否则播放/seek 仍驱动 source A");
        vm.TotalDuration.Should().Be(20.0, "TotalDuration 跟随重绑后的 master service");

        vm.Dispose();
    }

    /// <summary>
    /// v3.x (会话状态剥离 Task 5 final, Critical #1): 钉住 OpenSessionAsync 的
    /// UI-thread 契约——全程 ConfigureAwait(true)，首个 Task.Run 之后的续延必须经
    /// 调用方 SynchronizationContext 调度。unload/load 循环与 watch/分组恢复都依赖
    /// 这点：registry 的 SourcesChanged 在调用者线程同步触发 VM 的绑定集合修改，
    /// 逃出 UI context 会抛 NotSupportedException。
    /// <para>
    /// 用 recording context 记录 Post（并立即执行回调避免死锁）。测试自身在调用
    /// service 完成同步启动（到达首个 await、捕获 context）后立即恢复原 context
    /// 再 await——测试自己的续延不经过被测 context，Post 计数只反映 service 内部
    /// 续延。契约破坏（改回 ConfigureAwait(false)）时续延直接在线程池跑、Post
    /// 计数为 0，测试失败。
    /// </para>
    /// </summary>
    [Fact]
    public async Task OpenSessionAsync_ContinuationsReturnToCallingSynchronizationContext()
    {
        // arrange
        var realPath = NewTempFile("traceA.asc");
        File.WriteAllText(realPath, "frames");
        var (library, bundlePath) = NewLibrary();

        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>());
        registry.LoadAsync(realPath).Returns(MakeSource("s1", "traceA", realPath));

        library.Save(new TraceSessionBundleDto
        {
            Version = 1,
            Schema = "tmtrace/v1",
            Sources = new List<BundleSourceDto>
            {
                new() { SourceId = "old1", DisplayName = "traceA", Path = realPath },
            },
            Playback = null,
            WatchedSignals = new List<BundleWatchedSignalDto>
            {
                new() { CanIdHex = "0x100", MessageName = "MsgA", SignalName = "SigA" },
            },
        });

        var sut = MakeService(registry, library);
        var ctx = new RecordingSynchronizationContext();
        var original = SynchronizationContext.Current;

        // act——service 同步启动到首个 await 时捕获 ctx；随后立即恢复原 context，
        // 使测试自身的 await 续延不经过被测 context（避免污染 Post 计数）。
        Task<IReadOnlyList<string>> openTask;
        SynchronizationContext.SetSynchronizationContext(ctx);
        try
        {
            openTask = sut.OpenSessionAsync(bundlePath);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }
        var missing = await openTask;

        // assert
        missing.Should().BeEmpty();
        ctx.PostCount.Should().BeGreaterThan(0,
            "OpenSessionAsync 全程 ConfigureAwait(true) → 首个 Task.Run 之后的续延经调用方 context 调度");
        sut.WatchedSignals.Should().ContainSingle(
            "watch 列表恢复发生在 context 往返之后，仍被正确应用");
    }

    /// <summary>
    /// 记录 <see cref="SynchronizationContext.Post"/> 调用并立即同步执行回调
    /// （避免测试死锁）。PostCount &gt; 0 即证明 ConfigureAwait(true) 的续延经
    /// 调用方 context 调度；ConfigureAwait(false) 时续延直接在线程池跑，Post
    /// 不会被调用。
    /// </summary>
    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;
        public int PostCount => _postCount;

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postCount);
            d(state);
        }
    }

    [Fact]
    public async Task BuildSnapshot_IncludesSessionState()
    {
        // arrange
        var realPath = NewTempFile("traceA.asc");
        File.WriteAllText(realPath, "frames");
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>
        {
            MakeSource("s1", "traceA", realPath, r: 50, g: 100, b: 150, a: 255),
        });

        var hasher = Substitute.For<IAscContentHasher>();
        hasher.ComputeAsync(realPath, Arg.Any<CancellationToken>()).Returns("abc123");
        var (library, _) = NewLibrary();
        var sut = MakeService(registry, library, hasher: hasher);

        sut.MasterSourceId = "s1";
        sut.GlobalCanIdFilter = "0x100, 0x200";
        sut.WatchedSignals.Add(new WatchedSignalRow("0x100", "MsgA", "SigA", "rpm", "s1"));
        sut.WatchedSignals.Add(new WatchedSignalRow("0x100", "MsgA", "SigA", "rpm", "s1", isPlaceholder: true));
        sut.SignalGroups.Add(new WatchedSignalGroup("g1", "组A", null, SampleGroupSignalKeys));

        // act
        var dto = sut.BuildSnapshot();

        // assert
        dto.Sources.Should().ContainSingle();
        dto.Sources[0].SourceId.Should().Be("s1");
        dto.Sources[0].DisplayName.Should().Be("traceA");
        dto.Sources[0].Path.Should().Be(realPath);
        dto.Sources[0].ColorA.Should().Be(255);
        dto.Sources[0].ColorR.Should().Be(50);
        dto.Sources[0].ColorG.Should().Be(100);
        dto.Sources[0].ColorB.Should().Be(150);
        dto.Sources[0].StrokeStyle.Should().Be(new LineStyle().ToString(), "照搬 VM：StrokeStyle = src.StrokeStyle.ToString()");
        dto.Sources[0].ContentHash.Should().Be("abc123");
        dto.Playback.Should().NotBeNull();
        dto.Playback!.MasterSourceId.Should().Be("s1");
        dto.Playback.Loop.Should().BeFalse("窗口级 loop 不在 service 范围 → 默认 false");
        dto.Playback.Speed.Should().Be(1.0);
        dto.Playback.ScrubberValue.Should().Be(0.0);
        dto.GlobalCanIdFilter.Should().Be("0x100, 0x200");
        dto.Viewports.Should().BeEmpty("窗口级 viewport 不在 service 范围 → 空列表");
        dto.WatchedSignals.Should().ContainSingle("占位行被过滤");
        dto.WatchedSignals[0].CanIdHex.Should().Be("0x100");
        dto.WatchedSignals[0].SignalName.Should().Be("SigA");
        dto.WatchedSignals[0].SourceId.Should().Be("s1");
        dto.Groups.Should().ContainSingle();
        dto.Groups[0].Name.Should().Be("组A");
        dto.Groups[0].SignalKeys.Should().BeEquivalentTo(SampleGroupSignalKeys);
    }

    [Fact]
    public async Task BuildSnapshot_IncludesOnlyNonPlaceholderWatchedRows()
    {
        // arrange
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>());
        var (library, _) = NewLibrary();
        var sut = MakeService(registry, library);

        sut.WatchedSignals.Add(new WatchedSignalRow("0x100", "", "SigA", "", isPlaceholder: true));
        sut.WatchedSignals.Add(new WatchedSignalRow("0x200", "MsgB", "SigB", "v", "s1"));

        // act
        var dto = sut.BuildSnapshot();

        // assert
        dto.WatchedSignals.Should().ContainSingle().Which.CanIdHex.Should().Be("0x200");
    }

    [Fact]
    public void HasContent_ReflectsRegistrySourceCount()
    {
        // arrange
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>());
        var (library, _) = NewLibrary();
        var sut = MakeService(registry, library);

        sut.HasContent.Should().BeFalse();

        // act：registry 出现 source 后立即反映
        var realPath = NewTempFile("traceA.asc");
        File.WriteAllText(realPath, "frames");
        registry.Sources.Returns(new List<TraceSource> { MakeSource("s1", "traceA", realPath) });

        sut.HasContent.Should().BeTrue();
    }

    /// <summary>
    /// I-1 回归测试用假 registry：镜像 <see cref="TraceSessionRegistry"/> 的行为——
    /// load 追加 source + service 并触发 <see cref="SourcesChanged"/>，unload 移除。
    /// 服务实例由构造传入的 factory 按 SourceId 提供（测试据此给两个 source 不同
    /// TotalDuration，便于断言 master 重绑后指向哪一个）。
    /// </summary>
    private sealed class SimulatedRegistry : ITraceSessionRegistry
    {
        private readonly List<TraceSource> _sources = new();
        private readonly Dictionary<string, ITraceViewerService> _services = new(StringComparer.Ordinal);
        private readonly Func<string, ITraceViewerService> _serviceFactory;

        public SimulatedRegistry(Func<string, ITraceViewerService> serviceFactory) =>
            _serviceFactory = serviceFactory;

        public IReadOnlyList<TraceSource> Sources => _sources;

        public event Action? SourcesChanged;

        public Task<TraceSource> LoadAsync(string path, CancellationToken ct = default)
        {
            var id = $"newId{_sources.Count + 1}";
            var src = new TraceSource(
                id,
                System.IO.Path.GetFileNameWithoutExtension(path),
                path,
                new Color(0, 0, 0, 255),
                new LineStyle());
            _services[id] = _serviceFactory(id);
            _sources.Add(src);
            SourcesChanged?.Invoke();
            return Task.FromResult(src);
        }

        public Task UnloadAsync(string sourceId)
        {
            _sources.RemoveAll(s => s.SourceId == sourceId);
            _services.Remove(sourceId);
            SourcesChanged?.Invoke();
            return Task.CompletedTask;
        }

        public IReadOnlyList<ReplayFrame> GetFrames(string sourceId) => Array.Empty<ReplayFrame>();

        public ITraceViewerService? GetService(string sourceId) =>
            _services.TryGetValue(sourceId, out var svc) ? svc : null;
    }
}
