using PeakCan.Host.Core.Analysis.Nm;
using IO = System.IO;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §10.1：profile 加载链路。
/// <list type="bullet">
/// <item>搜索顺序：会话显式选择 &gt; DBC 同目录 &gt; <c>%APPDATA%\PeakCan\profiles\</c> &gt; 安装目录 <c>profiles\</c></item>
/// <item>同 id@version 多份文件 → 报错让用户选，禁止静默择一</item>
/// <item>不同 id@version 多份文件 → 合法的多候选，列为候选而不自动选</item>
/// </list>
/// </summary>
public class NmProfileSearchTests
{
    private const string ProfileA = """
        standard:
          id: a
          version: "1.0"
        """;

    private const string ProfileB = """
        standard:
          id: b
          version: "1.0"
        """;

    private static string NewTempDir()
    {
        var dir = IO.Path.Combine(IO.Path.GetTempPath(), "nm-search-" + Guid.NewGuid().ToString("N"));
        IO.Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string dir, string name, string yaml)
        => IO.File.WriteAllText(IO.Path.Combine(dir, name), yaml);

    /// <summary>搜索选项：显式选择 + 两个额外根目录（AppData / 安装目录）。</summary>
    private static NmProfileSearchOptions Options(
        string? explicitPath = null, string? appDataRoot = null, string? installRoot = null)
        => new(ExplicitPath: explicitPath, AppDataProfilesRoot: appDataRoot,
            InstallProfilesRoot: installRoot);

    // —— 层级优先级 ——

    [Fact]
    public void ExplicitPath_WinsOverEverythingElse()
    {
        var dbcDir = NewTempDir();
        var explicitDir = NewTempDir();
        try
        {
            Write(dbcDir, "dbc-side@1.0.nmstd.yaml", ProfileA);
            Write(explicitDir, "explicit@1.0.nmstd.yaml", ProfileB);

            var result = NmStandardProfileLocator.Search(
                IO.Path.Combine(dbcDir, "bus.dbc"),
                Options(explicitPath: IO.Path.Combine(explicitDir, "explicit@1.0.nmstd.yaml")));

            Assert.Equal("b", result.Selected!.StandardId);
        }
        finally
        {
            IO.Directory.Delete(dbcDir, recursive: true);
            IO.Directory.Delete(explicitDir, recursive: true);
        }
    }

    /// <summary>
    /// 显式选择时下拉仍要列出全部候选，否则用户选了 geely 之后想换 autosar
    /// 必须先退回「未选择」再选一次（并多触发一次重算）。
    /// </summary>
    [Fact]
    public void ExplicitSelection_StillListsAllCandidates()
    {
        var dbcDir = NewTempDir();
        var appData = NewTempDir();
        try
        {
            Write(dbcDir, "a@1.0.nmstd.yaml", ProfileA);
            Write(appData, "b@1.0.nmstd.yaml", ProfileB);

            var result = NmStandardProfileLocator.Search(
                IO.Path.Combine(dbcDir, "bus.dbc"),
                Options(explicitPath: IO.Path.Combine(dbcDir, "a@1.0.nmstd.yaml"),
                    appDataRoot: appData));

            Assert.Equal("a", result.Selected!.StandardId);
            Assert.Equal(2, result.Candidates.Count);
            Assert.Contains(result.Candidates, c => c.StandardId == "a");
            Assert.Contains(result.Candidates, c => c.StandardId == "b");
        }
        finally
        {
            IO.Directory.Delete(dbcDir, recursive: true);
            IO.Directory.Delete(appData, recursive: true);
        }
    }

    [Fact]
    public void ExplicitSelection_OutsideSearchRoots_IsIncludedInCandidates()
    {
        // 用户在任意位置手选了一份 profile：它不在三个搜索根目录里，
        // 仍必须出现在候选列表（否则下拉显示不出的当前选中项）。
        var dbcDir = NewTempDir();
        var other = NewTempDir();
        try
        {
            Write(dbcDir, "a@1.0.nmstd.yaml", ProfileA);
            Write(other, "b@1.0.nmstd.yaml", ProfileB);

            var result = NmStandardProfileLocator.Search(
                IO.Path.Combine(dbcDir, "bus.dbc"),
                Options(explicitPath: IO.Path.Combine(other, "b@1.0.nmstd.yaml")));

            Assert.Equal("b", result.Selected!.StandardId);
            Assert.Contains(result.Candidates, c => c.StandardId == "b");
            Assert.Contains(result.Candidates, c => c.StandardId == "a");
        }
        finally
        {
            IO.Directory.Delete(dbcDir, recursive: true);
            IO.Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public void DbcDirectory_WinsOverAppDataAndInstall()
    {
        var dbcDir = NewTempDir();
        var appData = NewTempDir();
        var install = NewTempDir();
        try
        {
            Write(dbcDir, "a@1.0.nmstd.yaml", ProfileA);
            Write(appData, "b@1.0.nmstd.yaml", ProfileB);
            Write(install, "b@1.0.nmstd.yaml", ProfileB);

            var result = NmStandardProfileLocator.Search(
                IO.Path.Combine(dbcDir, "bus.dbc"),
                Options(appDataRoot: appData, installRoot: install));

            Assert.Equal("a", result.Selected!.StandardId);
            Assert.Equal(NmProfileSearchLevel.DbcDirectory, result.Level);
        }
        finally
        {
            IO.Directory.Delete(dbcDir, recursive: true);
            IO.Directory.Delete(appData, recursive: true);
            IO.Directory.Delete(install, recursive: true);
        }
    }

    [Fact]
    public void AppData_WinsOverInstall()
    {
        var dbcDir = NewTempDir();
        var appData = NewTempDir();
        var install = NewTempDir();
        try
        {
            Write(appData, "b@1.0.nmstd.yaml", ProfileB);
            Write(install, "a@1.0.nmstd.yaml", ProfileA);

            var result = NmStandardProfileLocator.Search(
                IO.Path.Combine(dbcDir, "bus.dbc"),
                Options(appDataRoot: appData, installRoot: install));

            Assert.Equal("b", result.Selected!.StandardId);
            Assert.Equal(NmProfileSearchLevel.AppData, result.Level);
        }
        finally
        {
            IO.Directory.Delete(dbcDir, recursive: true);
            IO.Directory.Delete(appData, recursive: true);
            IO.Directory.Delete(install, recursive: true);
        }
    }

    [Fact]
    public void InstallDirectory_UsedWhenNothingAboveHasProfile()
    {
        var dbcDir = NewTempDir();
        var install = NewTempDir();
        try
        {
            Write(install, "a@1.0.nmstd.yaml", ProfileA);

            var result = NmStandardProfileLocator.Search(
                IO.Path.Combine(dbcDir, "bus.dbc"),
                Options(installRoot: install));

            Assert.Equal("a", result.Selected!.StandardId);
            Assert.Equal(NmProfileSearchLevel.InstallDirectory, result.Level);
        }
        finally
        {
            IO.Directory.Delete(dbcDir, recursive: true);
            IO.Directory.Delete(install, recursive: true);
        }
    }

    // —— 多候选语义（spec §10.1 与既有实现的冲突点）——

    [Fact]
    public void MultipleDifferentIds_AreCandidates_NotAutoSelected()
    {
        // spec §10.1：选择器要「列出可用 profile」。同目录放两份不同 id@version
        // 是合法的多候选——不能抛异常，也不能静默择一。
        var dbcDir = NewTempDir();
        try
        {
            Write(dbcDir, "a@1.0.nmstd.yaml", ProfileA);
            Write(dbcDir, "b@1.0.nmstd.yaml", ProfileB);

            var result = NmStandardProfileLocator.Search(
                IO.Path.Combine(dbcDir, "bus.dbc"), Options());

            Assert.Null(result.Selected);
            Assert.Equal(2, result.Candidates.Count);
            Assert.Contains(result.Candidates, c => c.StandardId == "a");
            Assert.Contains(result.Candidates, c => c.StandardId == "b");
        }
        finally
        {
            IO.Directory.Delete(dbcDir, recursive: true);
        }
    }

    [Fact]
    public void SameIdVersionTwice_Throws()
    {
        // spec §10.1：同 id/version 多份文件 → 报错让用户选，禁止静默择一。
        // 两份文件声明同一个身份是事实源冲突，用户必须自己解决。
        var dbcDir = NewTempDir();
        try
        {
            Write(dbcDir, "a-copy1.nmstd.yaml", ProfileA);
            Write(dbcDir, "a-copy2.nmstd.yaml", ProfileA);

            var ex = Assert.Throws<NmProfileValidationException>(() =>
                NmStandardProfileLocator.Search(IO.Path.Combine(dbcDir, "bus.dbc"), Options()));
            Assert.Contains("a@1.0", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            IO.Directory.Delete(dbcDir, recursive: true);
        }
    }

    [Fact]
    public void SingleCandidate_IsAutoSelected()
    {
        var dbcDir = NewTempDir();
        try
        {
            Write(dbcDir, "a@1.0.nmstd.yaml", ProfileA);

            var result = NmStandardProfileLocator.Search(
                IO.Path.Combine(dbcDir, "bus.dbc"), Options());

            Assert.NotNull(result.Selected);
            Assert.Equal("a", result.Selected!.StandardId);
            Assert.Single(result.Candidates);
        }
        finally
        {
            IO.Directory.Delete(dbcDir, recursive: true);
        }
    }

    [Fact]
    public void NoProfileAnywhere_ReturnsEmptyResult()
    {
        var dbcDir = NewTempDir();
        try
        {
            var result = NmStandardProfileLocator.Search(
                IO.Path.Combine(dbcDir, "bus.dbc"), Options());

            Assert.Null(result.Selected);
            Assert.Empty(result.Candidates);
            Assert.Null(result.Level);
        }
        finally
        {
            IO.Directory.Delete(dbcDir, recursive: true);
        }
    }

    // —— 显式选择指向不存在的文件必须报错，不得静默回退 ——

    [Fact]
    public void ExplicitPathMissing_Throws()
    {
        // 「会话显式选择」是用户的明确意图：文件不在了必须说清楚，
        // 静默回退到同目录的另一份 profile 会让用户以为选中的是 A、实际跑的是 B。
        var dbcDir = NewTempDir();
        try
        {
            Write(dbcDir, "a@1.0.nmstd.yaml", ProfileA);

            var ex = Assert.Throws<NmProfileValidationException>(() =>
                NmStandardProfileLocator.Search(
                    IO.Path.Combine(dbcDir, "bus.dbc"),
                    Options(explicitPath: IO.Path.Combine(dbcDir, "gone.nmstd.yaml"))));
            Assert.Contains("gone.nmstd.yaml", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            IO.Directory.Delete(dbcDir, recursive: true);
        }
    }

    // —— extends 链在跨层级时仍要解析 ——

    [Fact]
    public void SelectedProfileFromAppData_ResolvesParentFromInstallRoot()
    {
        // OEM profile 放在 %APPDATA%，base 放在安装目录：extends 链必须跨层级解析。
        var dbcDir = NewTempDir();
        var appData = NewTempDir();
        var install = NewTempDir();
        try
        {
            Write(appData, "child@2.0.nmstd.yaml", """
                standard:
                  id: child
                  version: "2.0"
                  extends: base@1.0
                """);
            Write(install, "base@1.0.nmstd.yaml", """
                standard:
                  id: base
                  version: "1.0"
                overrides:
                  timings:
                    cycle_ms: 500
                """);

            var result = NmStandardProfileLocator.Search(
                IO.Path.Combine(dbcDir, "bus.dbc"),
                Options(appDataRoot: appData, installRoot: install));

            Assert.NotNull(result.Selected);
            Assert.True(result.Registry.ContainsKey("base@1.0"));
            Assert.True(result.Registry.ContainsKey("child@2.0"));
        }
        finally
        {
            IO.Directory.Delete(dbcDir, recursive: true);
            IO.Directory.Delete(appData, recursive: true);
            IO.Directory.Delete(install, recursive: true);
        }
    }

    [Fact]
    public void MissingDbcPath_StillSearchesGlobalRoots()
    {
        // 没有 DBC 时（例如先选企标再看数据）仍应能列出 AppData/安装目录的候选。
        var appData = NewTempDir();
        try
        {
            Write(appData, "b@1.0.nmstd.yaml", ProfileB);

            var result = NmStandardProfileLocator.Search(null, Options(appDataRoot: appData));

            Assert.Equal("b", result.Selected!.StandardId);
            Assert.Equal(NmProfileSearchLevel.AppData, result.Level);
        }
        finally
        {
            IO.Directory.Delete(appData, recursive: true);
        }
    }
}
