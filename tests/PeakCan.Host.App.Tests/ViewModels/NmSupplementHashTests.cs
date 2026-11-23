using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// spec §10.1：企标 profile 必须参与报告缓存 key，否则切换 profile（或选中/未选中）
/// 会得到相同的 key，命中旧报告且没有任何提示。
/// </summary>
public class NmSupplementHashTests
{
    // CA1861：常量数组不能直接作实参（TreatWarningsAsErrors）。
    private static readonly string[] Reasons = ["test"];

    [Fact]
    public void Hash_NoStandardProfile_IsStable()
    {
        Assert.Equal(
            TraceViewerViewModel.NmSupplementHash(Profile()),
            TraceViewerViewModel.NmSupplementHash(Profile()));
    }

    [Fact]
    public void Hash_StandardProfilePresence_ChangesHash()
    {
        var without = TraceViewerViewModel.NmSupplementHash(Profile());
        var with = TraceViewerViewModel.NmSupplementHash(
            Profile() with { StandardRules = new[] { Rule("NM_05", cycleMs: 500) } });

        Assert.NotEqual(without, with);
    }

    [Fact]
    public void Hash_DifferentStandardRules_Differ()
    {
        var a = Hash(rules: new[] { Rule("NM_05", cycleMs: 500) });
        var b = Hash(rules: new[] { Rule("NM_06", cycleMs: 500) });

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Hash_SameRuleDifferentParameters_Differ()
    {
        // Id/Kind/FindingCode 都不变、只有参数变化时也必须失效。
        var a = Hash(rules: new[] { Rule("NM_05", cycleMs: 500) });
        var b = Hash(rules: new[] { Rule("NM_05", cycleMs: 640) });

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Hash_RuleOrder_DoesNotChange()
    {
        // spec §10.1.1：有业务 key 的列表按 key 排序，枚举顺序不得影响 hash。
        var ordered = new[] { Rule("NM_05", cycleMs: 500), Rule("NM_06", cycleMs: 500) };
        var reversed = ordered.Reverse().ToArray();

        Assert.Equal(Hash(rules: ordered), Hash(rules: reversed));
    }

    [Fact]
    public void Hash_DifferentStandardTimings_Differ()
    {
        var a = TraceViewerViewModel.NmSupplementHash(Profile() with
        {
            StandardTimings = new NmStandardTimings { CycleMs = 500, TimeoutMs = 2000 },
        });
        var b = TraceViewerViewModel.NmSupplementHash(Profile() with
        {
            StandardTimings = new NmStandardTimings { CycleMs = 500, TimeoutMs = 2200 },
        });

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Hash_DifferentStandardBitRoles_Differ()
    {
        var a = Hash(roles: new[] { new NmBitRole(1, 0, "RepeatMessageRequest") });
        var b = Hash(roles: new[] { new NmBitRole(1, 0, "RepeatMessageRequest")
            { Constraint = NmBitConstraint.MustBeZeroWhileDisabled } });

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Hash_BitRoleOrder_DoesNotChange()
    {
        var ordered = new[] { new NmBitRole(1, 0, "A"), new NmBitRole(3, 0, "B") };
        var reversed = ordered.Reverse().ToArray();

        Assert.Equal(Hash(roles: ordered), Hash(roles: reversed));
    }

    [Fact]
    public void Hash_DifferentStandardLayoutNidByte_Differ()
    {
        // StandardLayout 现在经 NmProfilePrecedence 驱动 nid/cbv/user_data 分析路径；
        // 只差一个字节也必须换 key，否则会命中旧报告。
        var a = TraceViewerViewModel.NmSupplementHash(Profile() with
        {
            StandardLayout = new NmStandardLayout { NidByte = 0, CbvByte = 1 },
        });
        var b = TraceViewerViewModel.NmSupplementHash(Profile() with
        {
            StandardLayout = new NmStandardLayout { NidByte = 1, CbvByte = 1 },
        });

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Hash_DifferentStandardLayoutUserData_Differ()
    {
        var a = TraceViewerViewModel.NmSupplementHash(Profile() with
        {
            StandardLayout = new NmStandardLayout { UserDataOffset = 4, UserDataLength = 4 },
        });
        var b = TraceViewerViewModel.NmSupplementHash(Profile() with
        {
            StandardLayout = new NmStandardLayout { UserDataOffset = 4, UserDataLength = 2 },
        });

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Hash_DifferentStandardFeaturesPnEnabled_Differ()
    {
        // StandardFeatures 驱动 pn_enabled 判定；只改这一个布尔也必须失效。
        var a = TraceViewerViewModel.NmSupplementHash(Profile() with
        {
            StandardFeatures = new NmStandardFeatures { PnEnabled = false },
        });
        var b = TraceViewerViewModel.NmSupplementHash(Profile() with
        {
            StandardFeatures = new NmStandardFeatures { PnEnabled = true },
        });

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Hash_DifferentStandardFeaturesActiveWakeup_Differ()
    {
        var a = TraceViewerViewModel.NmSupplementHash(Profile() with
        {
            StandardFeatures = new NmStandardFeatures { ActiveWakeupBitEnabled = true },
        });
        var b = TraceViewerViewModel.NmSupplementHash(Profile() with
        {
            StandardFeatures = new NmStandardFeatures { ActiveWakeupBitEnabled = false },
        });

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Hash_DifferentStandardId_Differ()
    {
        // 展平语义相同但身份不同的两个 profile 不得碰撞：报告 StandardId 会因此陈旧。
        var a = TraceViewerViewModel.NmSupplementHash(Profile(), "autosar-cannm@4.1.3");
        var b = TraceViewerViewModel.NmSupplementHash(Profile(), "geely-cv@2.0");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Hash_StandardIdPresence_ChangesHash()
    {
        var without = TraceViewerViewModel.NmSupplementHash(Profile());
        var with = TraceViewerViewModel.NmSupplementHash(Profile(), "autosar-cannm@4.1.3");

        Assert.NotEqual(without, with);
    }

    private static string Hash(
        IReadOnlyList<NmRuleDef>? rules = null,
        IReadOnlyList<NmBitRole>? roles = null)
        => TraceViewerViewModel.NmSupplementHash(
            Profile() with { StandardRules = rules, StandardBitRoles = roles });

    private static NmRuleDef Rule(string id, int cycleMs)
        => new(id, "requirement-" + id, "normal",
            new NmRuleWhen("cycle_tolerance", CycleMs: cycleMs),
            new NmRuleExpect("within_pct", Pct: 0.1),
            "CODE_" + id);

    private static NmProfile Profile()
        => new(NmProfileSource.Dbc,
            new[]
            {
                new NmMessageDef("0x500", false, "NM", "A", 8, true,
                    Reasons, CycleMs: 500, TimeoutMs: 2000),
            },
            new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(), new NmProvenance());
}
