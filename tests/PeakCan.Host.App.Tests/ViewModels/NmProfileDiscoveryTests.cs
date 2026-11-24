using System.IO;
using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// 企标 profile 的**默认发现路径**（spec §10.1 第三层：安装目录）。
/// <para>
/// <b>这组测试补的是一个真实缺口</b>：M2 的全部功能（规则引擎、条款覆盖、标定面板）
/// 都只在「选中了企标 profile」时才跑。而默认搜索的第三层是
/// <c>AppContext.BaseDirectory/profiles</c>——仓库里的 <c>profiles/</c> 在**仓库根目录**，
/// 从未被复制到输出目录。三个搜索层全部落空 → 选择器永远只有「未选择企标」一项 →
/// 用户看不到任何 M2 行为。
/// </para>
/// <para>
/// <b>为什么既有的 2000+ 条测试没抓住</b>：Core 测试用 <c>RepoPath()</c> 手工定位源码树里的
/// YAML，App 测试手工构造候选列表。**没有一条测试验证「App 运行时的默认路径下真的能找到
/// profile」**。这组测试就是那条缺失的断言。
/// </para>
/// </summary>
public class NmProfileDiscoveryTests
{
    /// <summary>
    /// 默认的安装目录根下必须存在至少一份可加载的企标 profile。
    /// <para>
    /// 断言的是**默认值**而不是可覆写属性：后者测试可以随便改，改了就不再代表
    /// 用户实际运行时的路径。这条测试若变红，说明打包配置漏了 profile，
    /// 用户开箱即用时整套 M2 功能是关闭的。
    /// </para>
    /// </summary>
    [Fact]
    public void DefaultInstallProfilesRoot_ContainsLoadableProfile()
    {
        // 与生产代码同一个表达式：不硬编码路径，否则两边会漂移。
        var root = Path.Combine(AppContext.BaseDirectory, "profiles");

        Assert.True(
            Directory.Exists(root),
            $"默认的企标 profile 目录不存在：{root}。" +
            "PeakCan.Host.App.csproj 必须把仓库根目录的 profiles/ 复制到输出目录，" +
            "否则用户运行时三个搜索层全部落空，M2 的规则引擎永远不会执行。");

        var yamlFiles = Directory.GetFiles(root, "*.nmstd.yaml", SearchOption.AllDirectories);
        Assert.NotEmpty(yamlFiles);
    }

    /// <summary>
    /// 复制过去的 profile 必须**真的能被加载并展平**——只断言文件存在，
    /// 复制了一份坏文件或漏了 base 父级，测试照样绿。
    /// </summary>
    [Fact]
    public void CopiedProfile_IsLoadableAndFlattens()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "profiles");
        var oemPath = Path.Combine(root, "oem", "geely-cv@2.0.nmstd.yaml");
        var basePath = Path.Combine(root, "base", "autosar-cannm@4.1.3.nmstd.yaml");

        Assert.True(File.Exists(oemPath), $"缺少 OEM profile：{oemPath}");
        Assert.True(File.Exists(basePath), $"缺少 base profile：{basePath}");

        var oem = NmStandardYaml.LoadFile(oemPath);
        var baseProfile = NmStandardYaml.LoadFile(basePath);
        var flat = NmStandardFlattener.Flatten(oem,
            new Dictionary<string, NmStandardProfile>(StringComparer.Ordinal)
            {
                ["autosar-cannm@4.1.3"] = baseProfile,
                ["geely-cv@2.0"] = oem,
            });

        // 展平后必须有规则——没有规则的 profile 跑不了任何企标判定。
        // 先断言非 null 再断言非空：直接 NotEmpty(null) 抛的是 ArgumentNullException，
        // 失败信息读不出「展平后没有规则」这个意图。
        Assert.NotNull(flat.Rules);
        Assert.NotEmpty(flat.Rules!);
    }

    /// <summary>
    /// <b>端到端</b>：用生产代码的默认搜索选项，在默认根目录下必须搜到 geely 候选。
    /// <para>
    /// 上一条证明「文件在、能加载」，这条证明「<see cref="NmStandardProfileLocator"/>
    /// 能按默认路径找到它」。两者缺一不可——文件放对了位置但 locator 的层级拼接写错，
    /// 用户依然看不到 profile。
    /// </para>
    /// <para>
    /// <b>断言的是「出现在候选里」，不是「自动选中」</b>：spec §10.1 要求选择器列出
    /// 「base + oem + 项目覆盖」，而 base 与 oem 是两份不同身份 → 按 spec 的多候选
    /// 语义不自动选。断言 Selected 会让这条测试与 spec 打架。
    /// </para>
    /// </summary>
    [Fact]
    public void DefaultSearch_FindsGeelyProfileInCandidates()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "profiles");

        // 与 TraceViewerViewModel.ProfileSearchOptions() 的默认值同构：
        // 无显式路径、无 AppData（置空避免依赖本机状态）、只有安装目录。
        var options = new NmProfileSearchOptions(
            ExplicitPath: null,
            AppDataProfilesRoot: null,
            InstallProfilesRoot: root);

        var result = NmStandardProfileLocator.Search(null, options);

        // spec §10.1：base 与 oem 都要能被选。
        Assert.Contains(result.Candidates, c => c.Id == "geely-cv@2.0");
        Assert.Contains(result.Candidates, c => c.Id == "autosar-cannm@4.1.3");

        // 默认必须选中 OEM（叶子），不是 base——否则用户装好软件打开 trace
        // 看到的仍是「未选择企标」，M2 全部功能处于关闭状态。
        Assert.NotNull(result.Selected);
        Assert.Equal("geely-cv@2.0",
            $"{result.Selected!.StandardId}@{result.Selected.Version}");
    }

    /// <summary>
    /// 用户在候选里选中 geely 之后，必须能展平出完整规则集——
    /// 这证明「发现」不只是列个名字，选完真的能跑企标判定。
    /// <para>
    /// 这条覆盖的是用户的实际操作路径：下拉里选 geely → 重分析 → 规则引擎执行。
    /// 前几条只证明候选存在，不证明选中后可用。
    /// </para>
    /// </summary>
    [Fact]
    public void SelectingGeelyCandidate_ProducesRunnableRules()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "profiles");
        var geelyPath = Path.Combine(root, "oem", "geely-cv@2.0.nmstd.yaml");
        Assert.True(File.Exists(geelyPath), $"缺少 OEM profile：{geelyPath}");

        // 模拟用户显式选中（选择器写回 NmExplicitProfilePath 的那条路径）。
        var options = new NmProfileSearchOptions(
            ExplicitPath: geelyPath,
            AppDataProfilesRoot: null,
            InstallProfilesRoot: root);

        var result = NmStandardProfileLocator.Search(null, options);

        Assert.NotNull(result.Selected);
        Assert.Equal("geely-cv@2.0",
            $"{result.Selected!.StandardId}@{result.Selected.Version}");

        // 选中后必须能展平出规则——否则规则引擎跑不起来，用户选了也白选。
        var flat = NmStandardFlattener.Flatten(result.Selected, result.Registry);
        Assert.NotEmpty(flat.Rules!);
    }
}
