using System.IO;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.Core.Services;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Trace;

/// <summary>
/// S3-T1 (D2): .tmtrace xcpWatch 区块。钉住三件事：
/// (a) <see cref="BundleXcpWatchDto"/>（name/category 两字段）+
/// <see cref="TraceSessionBundleDto.XcpWatch"/> 序列化 round-trip；
/// (b) 旧 bundle（无 xcpWatch 键）反序列化 → 空列表不炸（前向兼容）；
/// (c) <see cref="TraceSessionService"/> 的 BuildSnapshot 把
/// XcpWatchedObjects 收进 bundle、OpenSessionAsync 还原回集合（含空集）。
/// </summary>
public sealed class TraceBundleXcpWatchTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _files = new();

    public TraceBundleXcpWatchTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"xcp-watch-{Guid.NewGuid():N}");
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

    private (TraceSessionLibrary Library, string BundlePath) NewLibrary()
    {
        var path = NewTempFile($"bundle-{Guid.NewGuid():N}.tmtrace");
        return (new TraceSessionLibrary(path, NullLogger<TraceSessionLibrary>.Instance), path);
    }

    private TraceSessionService MakeService(
        ITraceSessionRegistry registry,
        TraceSessionLibrary library)
    {
        var dbcService = Substitute.For<DbcService>(NullLogger<DbcService>.Instance);
        var locator = Substitute.For<IAscLocator>();
        var hasher = Substitute.For<IAscContentHasher>();
        var builder = new TraceSessionSnapshotBuilder(hasher);
        return new TraceSessionService(
            registry, library, dbcService, locator, hasher, builder,
            NullLogger<TraceSessionService>.Instance);
    }

    // (a) DTO 形状 + round-trip

    [Fact]
    public void BundleXcpWatchDto_SerializesNameAndCategory()
    {
        var dto = new BundleXcpWatchDto { Name = "EngineRPM", Category = "MEASUREMENT" };

        var json = JsonSerializer.Serialize(dto);

        json.Should().Contain("\"name\":\"EngineRPM\"");
        json.Should().Contain("\"category\":\"MEASUREMENT\"");
    }

    [Fact]
    public void BundleXcpWatch_JsonRoundTrip_PreservesRows()
    {
        var snapshot = new TraceSessionBundleDto
        {
            XcpWatch = new List<BundleXcpWatchDto>
            {
                new() { Name = "EngineRPM", Category = "MEASUREMENT" },
                new() { Name = "TorqueLimit", Category = "CHARACTERISTIC" },
            },
        };

        var json = JsonSerializer.Serialize(snapshot);
        var loaded = JsonSerializer.Deserialize<TraceSessionBundleDto>(json);

        loaded.Should().NotBeNull();
        loaded!.XcpWatch.Should().HaveCount(2);
        loaded.XcpWatch[0].Name.Should().Be("EngineRPM");
        loaded.XcpWatch[0].Category.Should().Be("MEASUREMENT");
        loaded.XcpWatch[1].Name.Should().Be("TorqueLimit");
        loaded.XcpWatch[1].Category.Should().Be("CHARACTERISTIC");
    }

    // (b) 旧 bundle 前向兼容

    [Fact]
    public void Deserialize_OldBundleWithoutXcpWatchKey_YieldsEmptyList()
    {
        // 旧 bundle：无 xcpWatch 键（含 sources 先例字段，无新增内容）。
        const string legacyJson = """
            {
              "version": 1,
              "schema": "tmtrace/v1",
              "globalCanIdFilter": "",
              "sources": []
            }
            """;

        var dto = JsonSerializer.Deserialize<TraceSessionBundleDto>(legacyJson);

        dto.Should().NotBeNull();
        dto!.XcpWatch.Should().NotBeNull();
        dto.XcpWatch.Should().BeEmpty();
    }

    // (c) service 构建 / 还原

    [Fact]
    public void BuildSnapshot_CollectsXcpWatchedObjectsIntoBundle()
    {
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>());
        var (library, _) = NewLibrary();
        var sut = MakeService(registry, library);

        sut.XcpWatchedObjects.Add(new XcpWatchRow("EngineRPM", "MEASUREMENT"));
        sut.XcpWatchedObjects.Add(new XcpWatchRow("TorqueLimit", "CHARACTERISTIC"));

        var dto = sut.BuildSnapshot();

        dto.XcpWatch.Should().HaveCount(2);
        dto.XcpWatch[0].Name.Should().Be("EngineRPM");
        dto.XcpWatch[0].Category.Should().Be("MEASUREMENT");
        dto.XcpWatch[1].Name.Should().Be("TorqueLimit");
        dto.XcpWatch[1].Category.Should().Be("CHARACTERISTIC");
    }

    [Fact]
    public async Task OpenSessionAsync_RestoresXcpWatchedObjectsFromBundle()
    {
        var realPath = NewTempFile("traceA.asc");
        File.WriteAllText(realPath, "frames");
        var (library, bundlePath) = NewLibrary();

        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>());
        registry.LoadAsync(realPath)
            .Returns(new TraceSource("s1", "traceA", realPath, new ScottPlot.Color(), new ScottPlot.LineStyle()));

        library.Save(new TraceSessionBundleDto
        {
            Version = 1,
            Schema = "tmtrace/v1",
            Sources = new List<BundleSourceDto>
            {
                new() { SourceId = "old1", DisplayName = "traceA", Path = realPath },
            },
            XcpWatch = new List<BundleXcpWatchDto>
            {
                new() { Name = "EngineRPM", Category = "MEASUREMENT" },
                new() { Name = "TorqueLimit", Category = "CHARACTERISTIC" },
            },
        });

        var sut = MakeService(registry, library);
        sut.XcpWatchedObjects.Add(new XcpWatchRow("StaleObject", "MEASUREMENT"));

        var missing = await sut.OpenSessionAsync(bundlePath);

        missing.Should().BeEmpty();
        sut.XcpWatchedObjects.Should().HaveCount(2, "打开会话必须用 bundle 内容替换当前关注集");
        sut.XcpWatchedObjects[0].Name.Should().Be("EngineRPM");
        sut.XcpWatchedObjects[0].Category.Should().Be("MEASUREMENT");
        sut.XcpWatchedObjects[1].Name.Should().Be("TorqueLimit");
        sut.XcpWatchedObjects[1].Category.Should().Be("CHARACTERISTIC");
    }

    [Fact]
    public async Task OpenSessionAsync_BundleWithoutXcpWatch_ClearsCollection()
    {
        // 空集用例：旧 bundle 无 xcpWatch 键 → 集合清空、不抛异常。
        var realPath = NewTempFile("traceB.asc");
        File.WriteAllText(realPath, "frames");
        var (library, bundlePath) = NewLibrary();

        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>());
        registry.LoadAsync(realPath)
            .Returns(new TraceSource("s1", "traceB", realPath, new ScottPlot.Color(), new ScottPlot.LineStyle()));

        library.Save(new TraceSessionBundleDto
        {
            Version = 1,
            Schema = "tmtrace/v1",
            Sources = new List<BundleSourceDto>
            {
                new() { SourceId = "old1", DisplayName = "traceB", Path = realPath },
            },
        });

        var sut = MakeService(registry, library);
        sut.XcpWatchedObjects.Add(new XcpWatchRow("StaleObject", "MEASUREMENT"));

        var missing = await sut.OpenSessionAsync(bundlePath);

        missing.Should().BeEmpty();
        sut.XcpWatchedObjects.Should().BeEmpty("旧 bundle 无 xcpWatch → 关注集清空");
    }
}

