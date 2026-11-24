using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Trace;

/// <summary>
/// spec §10.1：trace 内容版本进入报告缓存 key。
/// <para>
/// 本仓库的 <see cref="TraceSessionRegistry.LoadAsync"/> 每次分配新 sourceId，
/// 因此「同一 sourceId 重载」今天不会发生。但 <c>NmCacheKey</c> 的
/// <c>TraceLoadVersion</c> 必须有一个真实来源，否则它恒为 0、
/// 一旦将来有人复用 sourceId 就会静默复用旧报告。
/// </para>
/// <para>
/// 取「加载代数」而非「对全部帧做哈希」：后者成本随帧数线性增长，
/// spec §10.1 自身给了「或 load generation」这个替代方案。
/// </para>
/// </summary>
public class TraceSourceLoadVersionTests
{
    [Fact]
    public void NewSource_DefaultsToZero()
    {
        // 既有 16 个构造点不带该参数：必须保持编译且语义合理（首次加载 = 0）。
        var source = new TraceSource(
            "s", "t", "C:/t.asc", new ScottPlot.Color(), new ScottPlot.LineStyle());

        source.LoadVersion.Should().Be(0);
    }

    [Fact]
    public void ExplicitLoadVersion_IsPreserved()
    {
        var source = new TraceSource(
            "s", "t", "C:/t.asc", new ScottPlot.Color(), new ScottPlot.LineStyle(),
            loadVersion: 3);

        source.LoadVersion.Should().Be(3);
    }

    [Fact]
    public async Task LoadAsync_AssignsNonZeroVersion()
    {
        // 版本 0 与「没有版本」不可区分，会让 key 的该维度永远失效不了。
        // 加载产生的新 source 必须带一个真实代数。
        var registry = new TraceSessionRegistry(
            new TableauPalette(), NullLoggerFactory.Instance);

        var source = await registry.LoadAsync(WriteTwoFrameAsc());

        source.LoadVersion.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task LoadAsync_RepeatedLoadsOfSamePath_GetDistinctVersions()
    {
        // 同一文件加载两次是两个独立 source；版本必须单调递增，
        // 使「路径相同、内容可能已变」的两份 trace 不会撞同一个 key。
        var registry = new TraceSessionRegistry(
            new TableauPalette(), NullLoggerFactory.Instance);
        var path = WriteTwoFrameAsc();

        var first = await registry.LoadAsync(path);
        var second = await registry.LoadAsync(path);

        second.LoadVersion.Should().BeGreaterThan(first.LoadVersion);
    }

    private static string WriteTwoFrameAsc()
    {
        var dir = Path.Combine(Path.GetTempPath(), "peakcan-host-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "trace.asc");
        File.WriteAllText(path,
            "   0.000000 51  100  8  11 22 33 44 55 66 77 88\n" +
            "   1.000000 51  100  8  AA BB CC DD EE FF 00 11\n");
        return path;
    }
}
