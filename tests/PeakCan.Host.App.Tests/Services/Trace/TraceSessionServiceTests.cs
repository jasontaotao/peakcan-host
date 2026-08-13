using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ScottPlot;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
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
}
