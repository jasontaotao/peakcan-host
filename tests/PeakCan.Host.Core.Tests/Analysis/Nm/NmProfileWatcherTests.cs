using PeakCan.Host.Core.Analysis.Nm;
using IO = System.IO;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// profile 文件变化失效（spec §10.1.2）。
/// <para>
/// 三条要求：
/// </para>
/// <list type="bullet">
/// <item>事件去抖（≥200ms）——编辑器保存常触发多次 FileSystemWatcher 事件。</item>
/// <item>profile 写入中不读——正在被写的文件读出来是半截 YAML。</item>
/// <item><c>extends</c> 父文件变化 → 整个依赖链失效。</item>
/// </list>
/// <para>
/// <b>测试不靠 Thread.Sleep</b>：去抖与「写入中」判定都抽成纯函数，
/// 由注入的 clock / delay 驱动；真 <see cref="FileSystemWatcher"/> 只留一条集成测试。
/// </para>
/// </summary>
public class NmProfileWatcherTests
{
    private const string ChildYaml = """
        standard:
          id: child
          version: "2.0"
          extends: base@1.0
        """;

    private const string BaseYaml = """
        standard:
          id: base
          version: "1.0"
        """;

    private static string NewTempDir()
    {
        var dir = IO.Path.Combine(IO.Path.GetTempPath(), "nm-watch-" + Guid.NewGuid().ToString("N"));
        IO.Directory.CreateDirectory(dir);
        return dir;
    }

    // —— 去抖：纯逻辑 ——

    [Fact]
    public void Debounce_FirstEventIsAcceptedImmediately()
    {
        var gate = new NmProfileChangeDebounce(TimeSpan.FromMilliseconds(200));

        Assert.True(gate.ShouldRaise(TimeSpan.Zero));
    }

    [Fact]
    public void Debounce_EventsWithinWindowAreSuppressed()
    {
        // 编辑器保存一次文件常产生 3~5 个事件；不去抖会重复失效/重算。
        var gate = new NmProfileChangeDebounce(TimeSpan.FromMilliseconds(200));
        Assert.True(gate.ShouldRaise(TimeSpan.Zero));
        gate.MarkRaised(TimeSpan.Zero);

        Assert.False(gate.ShouldRaise(TimeSpan.FromMilliseconds(50)));
        Assert.False(gate.ShouldRaise(TimeSpan.FromMilliseconds(150)));
    }

    [Fact]
    public void Debounce_EventAfterWindowIsAccepted()
    {
        var gate = new NmProfileChangeDebounce(TimeSpan.FromMilliseconds(200));
        Assert.True(gate.ShouldRaise(TimeSpan.Zero));
        gate.MarkRaised(TimeSpan.Zero);

        Assert.True(gate.ShouldRaise(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void Debounce_IsNotSlidingWindow()
    {
        // 判定必须基于「上次**接受**」而不是「上次收到」：
        // 滑窗会让持续刷新的目录永远不触发（每 50ms 一个事件，永不到 200ms）。
        var gate = new NmProfileChangeDebounce(TimeSpan.FromMilliseconds(200));
        Assert.True(gate.ShouldRaise(TimeSpan.Zero));
        gate.MarkRaised(TimeSpan.Zero);
        // 窗口内的事件不推进窗口。
        Assert.False(gate.ShouldRaise(TimeSpan.FromMilliseconds(100)));
        Assert.False(gate.ShouldRaise(TimeSpan.FromMilliseconds(150)));

        // 距上次**接受**（0ms）已过 200ms → 必须放行。
        Assert.True(gate.ShouldRaise(TimeSpan.FromMilliseconds(200)));
    }

    /// <summary>
    /// <b>关键场景</b>：调用方判定「文件正在写」而跳过本次时，**不得消耗去抖窗口**。
    /// <para>
    /// 否则：第一个事件因文件被锁而跳过（却推进了窗口）→ 写完后的下一个事件
    /// 落在 200ms 内被吞 → 整次修改都不会触发失效。这是「保存一次 profile
    /// 面板没反应」的直接成因。
    /// </para>
    /// <para>
    /// 这就是 <c>ShouldRaise</c> 只判定、<c>MarkRaised</c> 才推进的原因。
    /// </para>
    /// </summary>
    [Fact]
    public void Debounce_SkippedEvent_DoesNotConsumeWindow()
    {
        var gate = new NmProfileChangeDebounce(TimeSpan.FromMilliseconds(200));

        // 事件 A：判定通过，但调用方因「文件被锁」决定不抛 —— 不调 MarkRaised。
        Assert.True(gate.ShouldRaise(TimeSpan.Zero));

        // 事件 B（文件写完，100ms 后）：必须仍然放行。
        Assert.True(gate.ShouldRaise(TimeSpan.FromMilliseconds(100)),
            "被跳过的事件不得消耗去抖窗口");

        gate.MarkRaised(TimeSpan.FromMilliseconds(100));
        // 真正抛出之后，窗口才开始生效。
        Assert.False(gate.ShouldRaise(TimeSpan.FromMilliseconds(200)));
    }

    // —— 「写入中不读」：纯逻辑 ——

    [Fact]
    public void TryReadAllText_FileLocked_ReturnsFalse()
    {
        // profile 正在被写：IO.FileShare.None 打不开，必须报「读不到」而不是
        // 读到半截内容。spec §10.1.2 明确要求「先试 IO.FileShare.None」。
        var dir = NewTempDir();
        try
        {
            var path = IO.Path.Combine(dir, "a@1.nmstd.yaml");
            IO.File.WriteAllText(path, ChildYaml);

            // 持有独占句柄模拟「写入中」。
            using var writer = new IO.FileStream(path, IO.FileMode.Open, IO.FileAccess.Write, IO.FileShare.None);

            Assert.False(NmProfileFileReader.TryRead(path, out _));
        }
        finally
        {
            IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryReadAllText_FileReadable_ReturnsContent()
    {
        var dir = NewTempDir();
        try
        {
            var path = IO.Path.Combine(dir, "a@1.nmstd.yaml");
            IO.File.WriteAllText(path, ChildYaml);

            Assert.True(NmProfileFileReader.TryRead(path, out var content));
            Assert.Contains("child", content, StringComparison.Ordinal);
        }
        finally
        {
            IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryReadAllText_MissingFile_ReturnsFalse()
    {
        var dir = NewTempDir();
        try
        {
            Assert.False(NmProfileFileReader.TryRead(
                IO.Path.Combine(dir, "gone.nmstd.yaml"), out _));
        }
        finally
        {
            IO.Directory.Delete(dir, recursive: true);
        }
    }

    // —— extends 链失效 ——

    [Fact]
    public void AffectedProfiles_ParentChange_InvalidatesWholeChain()
    {
        // spec §10.1.2：父文件变化 → 整个依赖链失效。
        // 只失效直接改动的那个文件，会让子 profile 继续用旧的展平结果。
        var chain = new[]
        {
            new NmProfileDependency("child@2.0", ["base@1.0"]),
            new NmProfileDependency("base@1.0", []),
            new NmProfileDependency("unrelated@1.0", []),
        };

        var affected = NmProfileDependencyGraph.DependentsOf("base@1.0", chain);

        Assert.Contains("base@1.0", affected);
        Assert.Contains("child@2.0", affected);
        Assert.DoesNotContain("unrelated@1.0", affected);
    }

    [Fact]
    public void AffectedProfiles_DeepChain_InvalidatesAllAncestors()
    {
        var chain = new[]
        {
            new NmProfileDependency("c@1", ["b@1"]),
            new NmProfileDependency("b@1", ["a@1"]),
            new NmProfileDependency("a@1", []),
        };

        var affected = NmProfileDependencyGraph.DependentsOf("a@1", chain);

        Assert.Equal(3, affected.Count);
    }

    [Fact]
    public void AffectedProfiles_LeafChange_DoesNotInvalidateParent()
    {
        // 反向不成立：子 profile 改了不影响父 profile。
        var chain = new[]
        {
            new NmProfileDependency("child@2.0", ["base@1.0"]),
            new NmProfileDependency("base@1.0", []),
        };

        var affected = NmProfileDependencyGraph.DependentsOf("child@2.0", chain);

        Assert.Equal(["child@2.0"], affected);
    }

    [Fact]
    public void AffectedProfiles_CycleInGraph_DoesNotHang()
    {
        // 坏 profile 可能写出循环 extends；失效计算不能因此死循环。
        var chain = new[]
        {
            new NmProfileDependency("a@1", ["b@1"]),
            new NmProfileDependency("b@1", ["a@1"]),
        };

        var affected = NmProfileDependencyGraph.DependentsOf("a@1", chain);

        Assert.Contains("a@1", affected);
        Assert.Contains("b@1", affected);
    }
}
