using IO = System.IO;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// 企标 profile 必须真正进入 NmProfile，否则规则引擎永远不跑（评审 R20）。
/// </summary>
public class NmStandardBindingTests
{
    // CA1861：常量数组不能直接作实参（TreatWarningsAsErrors）。
    private static readonly string[] TestReasons = ["test"];

    [Fact]
    public void Bind_FlattenedProfile_PopulatesAllThreeStandardFields()
    {
        var flat = new NmStandardFlattener.Flattened(
            Timings: new NmStandardTimings { CycleMs = 500, TimeoutMs = 2000, ToleranceRatio = 0.1 },
            Layout: new NmStandardLayout
            {
                NidByte = 0, CbvByte = 1,
                BitRoles = new[] { new NmBitRole(3, 0, "NmSts") },
            },
            Features: new NmStandardFeatures { PnEnabled = false },
            Rules: new[]
            {
                new NmRuleDef("NM_05", "预留位必须为 0", "normal",
                    new NmRuleWhen("bit_rule", Role: "RepeatMessageRequest"),
                    new NmRuleExpect("is_zero"), "NM_RMR_SET_WHILE_DISABLED"),
            },
            Messages: null, Nodes: null, Provenance: "autosar-cannm@4.1.3 < geely-cv@2.0");

        var bound = NmStandardProfileBinding.Bind(Project(), flat);

        Assert.Equal(2000, bound.StandardTimings!.TimeoutMs);
        Assert.Single(bound.StandardBitRoles!);
        Assert.Equal("NmSts", bound.StandardBitRoles![0].Name);
        Assert.Single(bound.StandardRules!);
        Assert.Equal("NM_05", bound.StandardRules![0].Id);
    }

    [Fact]
    public void Bind_ProjectMessageLevelCycle_WinsOverStandardTiming()
    {
        // spec §6.1 第 2 条：项目 > OEM > base。报文级 CycleMs 不得被企标覆盖。
        var project = Project() with
        {
            Messages = new[]
            {
                new NmMessageDef("0x500", false, "NM", "A", 8, true,
                    TestReasons, CycleMs: 640, TimeoutMs: 3000),
            },
        };
        var flat = new NmStandardFlattener.Flattened(
            new NmStandardTimings { CycleMs = 500, TimeoutMs = 2000 },
            null, null, null, null, null, "x@1");

        var bound = NmStandardProfileBinding.Bind(project, flat);

        // 报文级字段原样保留；企标只填 NmProfile 级别的 StandardTimings。
        Assert.Equal(640, bound.Messages[0].CycleMs);
        Assert.Equal(500, bound.StandardTimings!.CycleMs);
    }

    [Fact]
    public void Bind_NullFlat_LeavesStandardFieldsNull()
    {
        var project = Project();

        var bound = NmStandardProfileBinding.Bind(project, flat: null);

        // 无企标时必须完全保持 M1：同一个实例，而不是新建一个字段为 null 的副本。
        Assert.Same(project, bound);
        Assert.Null(bound.StandardTimings);
        Assert.Null(bound.StandardBitRoles);
        Assert.Null(bound.StandardRules);
    }

    [Fact]
    public void BoundProfile_FeedsRuleEngine_ProducesFinding()
    {
        // 端到端：绑定后的 profile 必须足以驱动规则引擎。
        // 注意：把规则引擎接进 NmAnalyzer 是 Task 7 的范围；本任务直接调引擎，
        // 避免在 Task 7 完成前留下一个注定变红的测试。
        var frames = new[]
        {
            new ReplayFrame(0.0, 0x500, 8,
                new byte[] { 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, FrameFlags.None),
        };
        var bound = NmStandardProfileBinding.Bind(Project(), FlatWithRmrRule());
        var nmFrames = frames.Select(f => new NmFrame(bound.Messages[0], f)).ToArray();
        var timing = NmTimingDefaults.FromProfile(bound.Timings, bound.StandardTimings);
        var episodes = NmEpisodeBuilder.Build(nmFrames, timing.TimeoutMs, 0, 1.0);

        var result = NmRuleEngine.Evaluate(bound.StandardRules!, new NmRuleContext(
            nmFrames, episodes, bound, timing, new NmWindow(0, 1.0, "full")));

        // 规则引擎真的跑了：byte1.bit0 = 1 且要求为 0。
        Assert.Contains(result.Findings, f => f.Code == "NM_RMR_SET_WHILE_DISABLED");
    }

    private static NmStandardFlattener.Flattened FlatWithRmrRule()
        => new(
            new NmStandardTimings { CycleMs = 500, TimeoutMs = 2000 },
            new NmStandardLayout
            {
                NidByte = 0, CbvByte = 1,
                BitRoles = new[]
                {
                    new NmBitRole(1, 0, "RepeatMessageRequest")
                        { Constraint = NmBitConstraint.MustBeZeroWhileDisabled },
                },
            },
            null,
            new[]
            {
                new NmRuleDef("NM_05", "预留位必须为 0", "normal",
                    new NmRuleWhen("bit_rule", Role: "RepeatMessageRequest"),
                    new NmRuleExpect("is_zero"), "NM_RMR_SET_WHILE_DISABLED"),
            },
            null, null, "x@1");

    private static NmProfile Project()
        => new(NmProfileSource.Dbc,
            new[]
            {
                new NmMessageDef("0x500", false, "NM", "A", 8, true,
                    TestReasons, CycleMs: 500, TimeoutMs: 2000),
            },
            new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(), new NmProvenance());
}

/// <summary>
/// 本版本只实现 spec §10.1 的「DBC 同目录」一级；其余层级明确留空。
/// </summary>
public class NmStandardProfileLocatorTests
{
    private const string MinimalYaml = """
        standard:
          id: test-std
          version: "1.0"
        overrides:
          timings:
            cycle_ms: 500
        """;

    [Fact]
    public void TryLoad_NoProfileNextToDbc_ReturnsNull()
    {
        var dir = NewTempDir();
        try
        {
            Assert.Null(NmStandardProfileLocator.TryLoad(IO.Path.Combine(dir, "bus.dbc")));
        }
        finally
        {
            IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryLoad_NullDbcPath_ReturnsNull()
        => Assert.Null(NmStandardProfileLocator.TryLoad(null));

    [Fact]
    public void TryLoad_SingleProfileNextToDbc_LoadsIt()
    {
        var dir = NewTempDir();
        try
        {
            IO.File.WriteAllText(IO.Path.Combine(dir, "test@1.0.nmstd.yaml"), MinimalYaml);

            var resolved = NmStandardProfileLocator.TryLoad(IO.Path.Combine(dir, "bus.dbc"));

            Assert.NotNull(resolved);
            Assert.Equal("test-std", resolved.Selected.StandardId);
            Assert.True(resolved.Registry.ContainsKey("test-std@1.0"));
        }
        finally
        {
            IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryLoad_MultipleProfiles_ThrowsInsteadOfSilentlyPickingOne()
    {
        var dir = NewTempDir();
        try
        {
            IO.File.WriteAllText(IO.Path.Combine(dir, "a@1.0.nmstd.yaml"), MinimalYaml);
            IO.File.WriteAllText(IO.Path.Combine(dir, "b@1.0.nmstd.yaml"), MinimalYaml);

            // spec §10.1：多份候选必须让用户选，禁止静默择一。
            var ex = Assert.Throws<NmProfileValidationException>(
                () => NmStandardProfileLocator.TryLoad(IO.Path.Combine(dir, "bus.dbc")));
            Assert.Contains("a@1.0.nmstd.yaml", ex.Message, StringComparison.Ordinal);
            Assert.Contains("b@1.0.nmstd.yaml", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            IO.Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>父级按目录约定定位；注册表必须同时含选中 profile 与已解析的祖先。</summary>
    [Fact]
    public void TryLoad_ProfileWithExtends_ResolvesParentChain()
    {
        var root = NewTempDir();
        try
        {
            CopyGeelyLayout(root);

            var resolved = NmStandardProfileLocator.TryLoad(
                IO.Path.Combine(root, "profiles", "oem", "bus.dbc"));

            Assert.NotNull(resolved);
            Assert.Equal("geely-cv@2.0",
                $"{resolved.Selected.StandardId}@{resolved.Selected.Version}");
            // 链注册表必须同时含子与父；只登记自身会导致 Flatten 报「继承链断裂」。
            Assert.True(resolved.Registry.ContainsKey("geely-cv@2.0"));
            Assert.True(resolved.Registry.ContainsKey("autosar-cannm@4.1.3"));
        }
        finally
        {
            IO.Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 端到端：定位 → 展平 → 校验。这是证明主目标 Geely profile 真能加载的测试。
    /// </summary>
    [Fact]
    public void TryLoad_GeelyOverAutosar_FlattensSuccessfully()
    {
        var root = NewTempDir();
        try
        {
            CopyGeelyLayout(root);

            var resolved = NmStandardProfileLocator.TryLoad(
                IO.Path.Combine(root, "profiles", "oem", "bus.dbc"));
            Assert.NotNull(resolved);

            var flat = NmStandardFlattener.Flatten(resolved.Selected, resolved.Registry);
            NmStandardValidator.ValidateFlattened(flat);

            // 吉利覆盖 base 的容差 0.2。
            Assert.Equal(0.1, flat.Timings!.ToleranceRatio);
            // base 的 PNI 位被 remove 删除，证明父级确实参与合并（不只是子层自洽）。
            Assert.DoesNotContain(flat.Layout!.BitRoles!, r => r.Name == "PartialNetworkInfo");
            Assert.Contains(flat.Layout.BitRoles!, r => r.Name == "NmSts");
            // 未覆盖字段继承父值。
            Assert.Equal(500, flat.Timings.CycleMs);
            Assert.Equal(2000, flat.Timings.TimeoutMs);
        }
        finally
        {
            IO.Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 父级文件不存在时不得静默成功：注册表缺少该 key，Flatten 必须以
    /// 「继承链断裂」点名缺失的父级。
    /// </summary>
    [Fact]
    public void TryLoad_MissingParent_RegistryOmitsIt()
    {
        var dir = NewTempDir();
        try
        {
            IO.File.WriteAllText(IO.Path.Combine(dir, "orphan@1.0.nmstd.yaml"), """
                standard:
                  id: orphan
                  version: "1.0"
                  extends: missing@9.9
                overrides:
                  timings:
                    cycle_ms: 500
                """);

            var resolved = NmStandardProfileLocator.TryLoad(IO.Path.Combine(dir, "bus.dbc"));

            Assert.NotNull(resolved);
            Assert.False(resolved.Registry.ContainsKey("missing@9.9"));

            var ex = Assert.Throws<NmProfileValidationException>(
                () => NmStandardFlattener.Flatten(resolved.Selected, resolved.Registry));
            Assert.Contains("继承链断裂", ex.Message, StringComparison.Ordinal);
            Assert.Contains("missing@9.9", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            IO.Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>把仓库真实的 profiles/{base,oem} 布局复制到临时根目录。</summary>
    private static void CopyGeelyLayout(string root)
    {
        var baseDir = IO.Path.Combine(root, "profiles", "base");
        var oemDir = IO.Path.Combine(root, "profiles", "oem");
        IO.Directory.CreateDirectory(baseDir);
        IO.Directory.CreateDirectory(oemDir);
        IO.File.Copy(RepoPath("profiles/base/autosar-cannm@4.1.3.nmstd.yaml"),
            IO.Path.Combine(baseDir, "autosar-cannm@4.1.3.nmstd.yaml"));
        IO.File.Copy(RepoPath("profiles/oem/geely-cv@2.0.nmstd.yaml"),
            IO.Path.Combine(oemDir, "geely-cv@2.0.nmstd.yaml"));
    }

    private static string RepoPath(string relative)
    {
        var dir = new IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !IO.File.Exists(IO.Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return IO.Path.Combine(dir!.FullName, relative);
    }

    private static string NewTempDir()
    {
        var dir = IO.Path.Combine(IO.Path.GetTempPath(), "nm-locator-" + Guid.NewGuid().ToString("N"));
        IO.Directory.CreateDirectory(dir);
        return dir;
    }
}
