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

    /// <summary>继承 <see cref="ProfileA"/> 的 OEM profile——用于验证「叶子」判定。</summary>
    private const string ProfileOem = """
        standard:
          id: oem
          version: "2.0"
          extends: a@1.0
        """;

    private const string ProfileOem2 = """
        standard:
          id: oem2
          version: "2.0"
          extends: a@1.0
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

    /// <summary>
    /// <b>子目录里的 profile 必须能被搜到</b>。spec §15 的目录约定是
    /// <c>profiles/&lt;vendor&gt;/&lt;name&gt;@&lt;version&gt;.nmstd.yaml</c>——
    /// base 在 <c>profiles/base/</c>、OEM 在 <c>profiles/oem/</c>，都不在根目录。
    /// <para>
    /// 首版只搜 <c>TopDirectoryOnly</c>，导致仓库里真实的两份 profile 一份都搜不到：
    /// 选择器永远只有「未选择企标」一项，M2 的规则引擎、条款覆盖、标定面板
    /// 全都不会执行。这是「测试全绿但功能不可达」的典型。
    /// </para>
    /// </summary>
    [Fact]
    public void ProfilesInVendorSubdirectory_AreFound()
    {
        var install = NewTempDir();
        try
        {
            // 模拟 spec §15 的目录约定：base/ 与 oem/ 两个子目录。
            IO.Directory.CreateDirectory(IO.Path.Combine(install, "base"));
            IO.Directory.CreateDirectory(IO.Path.Combine(install, "oem"));
            Write(IO.Path.Combine(install, "base"), "a@1.0.nmstd.yaml", ProfileA);
            Write(IO.Path.Combine(install, "oem"), "b@1.0.nmstd.yaml", ProfileB);

            var result = NmStandardProfileLocator.Search(null, Options(installRoot: install));

            Assert.Equal(2, result.Candidates.Count);
            Assert.Contains(result.Candidates, c => c.Id == "a@1.0");
            Assert.Contains(result.Candidates, c => c.Id == "b@1.0");
        }
        finally
        {
            IO.Directory.Delete(install, recursive: true);
        }
    }

    /// <summary>
    /// <b>DBC 同目录层必须只搜顶层</b>，不得递归（spec §10.1 字面：「DBC 同目录」）。
    /// <para>
    /// 递归会让工程目录下偶然存在的 <c>profiles/base/...</c> 命中并短路掉安装目录，
    /// 用户看到层级显示「DBC 同目录」、跑的却是那份旧 profile，且没有任何提示。
    /// 更坏的情况是自动选中一份无关企标。
    /// </para>
    /// </summary>
    [Fact]
    public void DbcDirectory_DoesNotRecurseIntoSubdirectories()
    {
        var dbcDir = NewTempDir();
        var install = NewTempDir();
        try
        {
            // DBC 同目录的子目录里放一份 profile：不得被搜到。
            var nested = IO.Path.Combine(dbcDir, "profiles", "base");
            IO.Directory.CreateDirectory(nested);
            Write(nested, "nested@1.0.nmstd.yaml", ProfileA);
            // 安装目录里放真正该用的那份。
            Write(install, "real@1.0.nmstd.yaml", ProfileB);

            var result = NmStandardProfileLocator.Search(
                IO.Path.Combine(dbcDir, "bus.dbc"),
                Options(installRoot: install));

            Assert.Equal("b", result.Selected!.StandardId);
            Assert.Equal(NmProfileSearchLevel.InstallDirectory, result.Level);
        }
        finally
        {
            IO.Directory.Delete(dbcDir, recursive: true);
            IO.Directory.Delete(install, recursive: true);
        }
    }

    /// <summary>
    /// 安装目录只递归**一层** <c>&lt;vendor&gt;/</c>（spec §15 的目录约定），
    /// 不无界递归——否则 <c>backup/</c>、<c>old/</c> 里的副本会被当成正式候选，
    /// 两份同 id@version 时直接抛异常让整次分析失败。
    /// </summary>
    [Fact]
    public void InstallDirectory_DoesNotRecurseBeyondVendorLevel()
    {
        var install = NewTempDir();
        try
        {
            var vendor = IO.Path.Combine(install, "oem");
            IO.Directory.CreateDirectory(vendor);
            Write(vendor, "good@1.0.nmstd.yaml", ProfileA);

            // 更深一层：spec §15 没有这个约定，不得被当成候选。
            var deep = IO.Path.Combine(vendor, "backup", "old");
            IO.Directory.CreateDirectory(deep);
            Write(deep, "stale@1.0.nmstd.yaml", ProfileB);

            var result = NmStandardProfileLocator.Search(null, Options(installRoot: install));

            Assert.Equal("a", result.Selected!.StandardId);
            Assert.DoesNotContain(result.Candidates, c => c.StandardId == "stale");
        }
        finally
        {
            IO.Directory.Delete(install, recursive: true);
        }
    }

    /// <summary>
    /// <b>默认选叶子 profile</b>：安装目录同时有 base 与 OEM 时，自动选中 OEM。
    /// <para>
    /// 「叶子」= 没有任何其它候选 <c>extends</c> 它。base 被 OEM 继承，所以不是叶子。
    /// 不这么做的话用户装好软件打开 trace，M2 全部功能仍是关闭的——
    /// 必须手动点一次下拉才会看到任何变化。
    /// </para>
    /// </summary>
    [Fact]
    public void BaseAndOemTogether_AutoSelectsLeafNotBase()
    {
        var install = NewTempDir();
        try
        {
            IO.Directory.CreateDirectory(IO.Path.Combine(install, "base"));
            IO.Directory.CreateDirectory(IO.Path.Combine(install, "oem"));
            Write(IO.Path.Combine(install, "base"), "a@1.0.nmstd.yaml", ProfileA);
            Write(IO.Path.Combine(install, "oem"), "oem@2.0.nmstd.yaml", ProfileOem);

            var result = NmStandardProfileLocator.Search(null, Options(installRoot: install));

            Assert.NotNull(result.Selected);
            Assert.Equal("oem", result.Selected!.StandardId);
            // 两份都仍要出现在候选里，用户能换回 base。
            Assert.Equal(2, result.Candidates.Count);
        }
        finally
        {
            IO.Directory.Delete(install, recursive: true);
        }
    }

    /// <summary>
    /// 多个**互不继承**的叶子（两个 OEM）→ 仍是多候选、不自动选。
    /// 猜哪个都不是用户想要的。
    /// </summary>
    [Fact]
    public void TwoIndependentLeaves_AreNotAutoSelected()
    {
        var install = NewTempDir();
        try
        {
            IO.Directory.CreateDirectory(IO.Path.Combine(install, "base"));
            IO.Directory.CreateDirectory(IO.Path.Combine(install, "oem"));
            Write(IO.Path.Combine(install, "base"), "base@1.0.nmstd.yaml", ProfileA);
            Write(IO.Path.Combine(install, "oem"), "oem1@2.0.nmstd.yaml", ProfileOem);
            Write(IO.Path.Combine(install, "oem"), "oem2@2.0.nmstd.yaml", ProfileOem2);

            var result = NmStandardProfileLocator.Search(null, Options(installRoot: install));

            Assert.Null(result.Selected);
            Assert.Equal(3, result.Candidates.Count);
        }
        finally
        {
            IO.Directory.Delete(install, recursive: true);
        }
    }

    /// <summary>
    /// <b>自继承不得让坏 profile 静默消失</b>：<c>extends</c> 写错成自己的 id 时，
    /// 若把自身算成「父」，它会被排除出叶子集，剩下的 base 被自动选中——
    /// 用户看到「跑到 base 上了」，而那份坏 OEM 被悄悄隐藏。
    /// </summary>
    [Fact]
    public void SelfInheritingProfile_IsNotExcludedAsItsOwnParent()
    {
        var install = NewTempDir();
        try
        {
            IO.Directory.CreateDirectory(IO.Path.Combine(install, "base"));
            IO.Directory.CreateDirectory(IO.Path.Combine(install, "oem"));
            Write(IO.Path.Combine(install, "base"), "base@1.0.nmstd.yaml", ProfileA);
            // 自继承：extends 指向自己。
            Write(IO.Path.Combine(install, "oem"), "oem@2.0.nmstd.yaml", """
                standard:
                  id: oem
                  version: "2.0"
                  extends: oem@2.0
                """);

            var result = NmStandardProfileLocator.Search(null, Options(installRoot: install));

            // 两份都算叶子 → 不自动选，交给用户。不能静默选 base。
            Assert.Null(result.Selected);
            Assert.Equal(2, result.Candidates.Count);
        }
        finally
        {
            IO.Directory.Delete(install, recursive: true);
        }
    }

    /// <summary>
    /// <b>悬空继承链的叶子不得被自动选中</b>：父文件被改名后，
    /// 自动选中它会让 <c>Flatten</c> 抛「继承链断裂」，整次分析失败。
    /// 改动前多候选不自动选，分析照常跑完——自动选不能把这个容错退掉。
    /// </summary>
    [Fact]
    public void LeafWithDanglingParent_IsNotAutoSelected()
    {
        var install = NewTempDir();
        try
        {
            IO.Directory.CreateDirectory(IO.Path.Combine(install, "base"));
            IO.Directory.CreateDirectory(IO.Path.Combine(install, "oem"));
            // base 文件名叫 base@1.0，但内容 id 是 a@1.0 —— LocateParent 按
            // `a@1.0.nmstd.yaml` 找不到它，继承链断在这里。
            Write(IO.Path.Combine(install, "base"), "base@1.0.nmstd.yaml", ProfileA);
            Write(IO.Path.Combine(install, "oem"), "oem@2.0.nmstd.yaml", ProfileOem);

            var result = NmStandardProfileLocator.Search(null, Options(installRoot: install));

            Assert.Null(result.Selected);
            // 两份都仍要列出来，用户能手动点选（点选时会得到明确的继承链错误）。
            Assert.Equal(2, result.Candidates.Count);
        }
        finally
        {
            IO.Directory.Delete(install, recursive: true);
        }
    }

    /// <summary>
    /// <c>extends</c> 带前后空格时父仍要被识别——否则两份都算叶子，
    /// 自动选静默失效，M2 又回到「感觉没变化」且无任何报错。
    /// </summary>
    [Fact]
    public void ExtendsWithWhitespace_StillRecognizesParent()
    {
        var install = NewTempDir();
        try
        {
            IO.Directory.CreateDirectory(IO.Path.Combine(install, "base"));
            IO.Directory.CreateDirectory(IO.Path.Combine(install, "oem"));
            Write(IO.Path.Combine(install, "base"), "a@1.0.nmstd.yaml", ProfileA);
            Write(IO.Path.Combine(install, "oem"), "oem@2.0.nmstd.yaml", """
                standard:
                  id: oem
                  version: "2.0"
                  extends: " a@1.0 "
                """);

            var result = NmStandardProfileLocator.Search(null, Options(installRoot: install));

            Assert.NotNull(result.Selected);
            Assert.Equal("oem", result.Selected!.StandardId);
        }
        finally
        {
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
