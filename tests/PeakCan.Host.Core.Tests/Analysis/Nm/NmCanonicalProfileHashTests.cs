using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §10.1.1：canonical hash 必须对「枚举顺序 / 缺省值与显式默认值 / 排除字段」稳定。
/// <para>
/// 这些性质是缓存正确性的前提：hash 抖动会让每次分析都重算（性能），
/// hash 漏字段会让切 profile 命中旧报告（正确性）。
/// </para>
/// </summary>
public class NmCanonicalProfileHashTests
{
    private static readonly string[] Reasons = ["MsgType=CANNM"];
    private static readonly string[] Clauses = ["§5.5"];
    private static readonly string[] ConflictValuesA = ["500", "640"];
    private static readonly string[] ConflictValuesB = ["640", "500"];
    private static readonly string[] ConflictSources = ["a", "b"];
    private static readonly string[] MissingSelection = ["nm_message_selection"];

    private static NmMessageDef Message(string id, bool extended = false)
        => new(id, extended, "NM_" + id, "ECU", 8, true, Reasons, 500, 2000);

    private static NmProfile Profile(
        IReadOnlyList<NmMessageDef>? messages = null,
        IReadOnlyList<NmNodeDef>? nodes = null,
        IReadOnlyList<NmConfigConflict>? conflicts = null,
        IReadOnlyList<string>? missing = null,
        NmTimings? timings = null,
        NmProvenance? provenance = null,
        NmProfileSource source = NmProfileSource.Dbc,
        IReadOnlyList<NmRuleDef>? rules = null,
        IReadOnlyList<NmBitRole>? bitRoles = null)
        => new(
            source,
            messages ?? [Message("0x500"), Message("0x501")],
            timings ?? new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            nodes ?? [new NmNodeDef("ECU_A", 1), new NmNodeDef("ECU_B", 2)],
            conflicts ?? [],
            missing ?? [],
            provenance ?? new NmProvenance(),
            StandardRules: rules,
            StandardBitRoles: bitRoles);

    private static NmRuleDef Rule(string id, int cycleMs = 500)
        => new(
            Id: id,
            Requirement: "req",
            DeclaredPhase: "normal",
            When: new NmRuleWhen("cycle_tolerance", CycleMs: cycleMs),
            Expect: new NmRuleExpect("within_pct", Pct: 0.1),
            FindingCode: "NM_CYCLE_DEVIATION")
        {
            Clauses = Clauses,
        };

    [Fact]
    public void Compute_SameProfile_IsStable()
    {
        Assert.Equal(
            NmCanonicalProfileHash.Compute(Profile()),
            NmCanonicalProfileHash.Compute(Profile()));
    }

    [Fact]
    public void Compute_MessageOrder_DoesNotChange()
    {
        // spec §10.1.1：有业务 key 的列表按 key 排序（messages → can_id + ext）。
        var ordered = NmCanonicalProfileHash.Compute(
            Profile(messages: [Message("0x500"), Message("0x501")]));
        var reversed = NmCanonicalProfileHash.Compute(
            Profile(messages: [Message("0x501"), Message("0x500")]));

        Assert.Equal(ordered, reversed);
    }

    [Fact]
    public void Compute_NodeOrder_DoesNotChange()
    {
        var ordered = NmCanonicalProfileHash.Compute(
            Profile(nodes: [new NmNodeDef("ECU_A", 1), new NmNodeDef("ECU_B", 2)]));
        var reversed = NmCanonicalProfileHash.Compute(
            Profile(nodes: [new NmNodeDef("ECU_B", 2), new NmNodeDef("ECU_A", 1)]));

        Assert.Equal(ordered, reversed);
    }

    [Fact]
    public void Compute_RuleOrder_DoesNotChange()
    {
        var ordered = NmCanonicalProfileHash.Compute(
            Profile(rules: [Rule("NM_05"), Rule("NM_06")]));
        var reversed = NmCanonicalProfileHash.Compute(
            Profile(rules: [Rule("NM_06"), Rule("NM_05")]));

        Assert.Equal(ordered, reversed);
    }

    [Fact]
    public void Compute_BitRoleOrder_DoesNotChange()
    {
        var ordered = NmCanonicalProfileHash.Compute(Profile(bitRoles:
            [new NmBitRole(1, 0, "Cbv"), new NmBitRole(3, 0, "NmSts")]));
        var reversed = NmCanonicalProfileHash.Compute(Profile(bitRoles:
            [new NmBitRole(3, 0, "NmSts"), new NmBitRole(1, 0, "Cbv")]));

        Assert.Equal(ordered, reversed);
    }

    [Fact]
    public void Compute_ConflictOrder_DoesNotChange()
    {
        var forward = NmCanonicalProfileHash.Compute(Profile(conflicts:
            [new NmConfigConflict("CycleMs", ConflictValuesA, ConflictSources)]));
        var backward = NmCanonicalProfileHash.Compute(Profile(conflicts:
            [new NmConfigConflict("CycleMs", ConflictValuesB, ConflictSources)]));

        Assert.Equal(forward, backward);
    }

    [Fact]
    public void Compute_ProvenancePathAndHash_AreExcluded()
    {
        // spec §10.1.1 排除字段：文件路径、ProfileHash。同一份 profile 换个路径
        // 不该产生新的缓存项。
        var withoutPath = NmCanonicalProfileHash.Compute(Profile(provenance: new NmProvenance()));
        var withPath = NmCanonicalProfileHash.Compute(Profile(
            provenance: new NmProvenance(@"C:\some\bus.dbc", "ABCDEF", "dbc < oem")));

        Assert.Equal(withoutPath, withPath);
    }

    [Fact]
    public void Compute_SourceFlag_DoesNotChange()
    {
        // spec §10.1：不同输入产生相同 profile 时不能被拆成两项。
        // 来源标志是「怎么来的」，不是内容。
        var fromDbc = NmCanonicalProfileHash.Compute(Profile(source: NmProfileSource.Dbc));
        var fromBoth = NmCanonicalProfileHash.Compute(
            Profile(source: NmProfileSource.Dbc | NmProfileSource.Sidecar));

        Assert.Equal(fromDbc, fromBoth);
    }

    [Fact]
    public void Compute_TimingChange_ChangesHash()
    {
        var baseline = NmCanonicalProfileHash.Compute(Profile());
        var changed = NmCanonicalProfileHash.Compute(
            Profile(timings: new NmTimings(CycleMs: 640, TimeoutMs: 2000)));

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void Compute_RuleParameterChange_ChangesHash()
    {
        // Id/Kind 都不变、只有参数变化时也必须失效（切 profile 必须重算）。
        var a = NmCanonicalProfileHash.Compute(Profile(rules: [Rule("NM_05", 500)]));
        var b = NmCanonicalProfileHash.Compute(Profile(rules: [Rule("NM_05", 640)]));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_StandardIdChange_ChangesHash()
    {
        var geely = NmCanonicalProfileHash.Compute(Profile(), "geely-cv@2.0");
        var autosar = NmCanonicalProfileHash.Compute(Profile(), "autosar-cannm@4.1.3");

        Assert.NotEqual(geely, autosar);
    }

    [Fact]
    public void Compute_NoStandardVersusStandard_ChangesHash()
    {
        // 未选企标与选中企标必须产生不同 key：报告里的 StandardId 与规则集都不同。
        var none = NmCanonicalProfileHash.Compute(Profile());
        var selected = NmCanonicalProfileHash.Compute(Profile(), "geely-cv@2.0");

        Assert.NotEqual(none, selected);
    }

    [Fact]
    public void Compute_MissingFieldsChange_ChangesHash()
    {
        // MissingFields 驱动报告的 MissingInputs 与自动补全，必须参与 key。
        var complete = NmCanonicalProfileHash.Compute(Profile());
        var incomplete = NmCanonicalProfileHash.Compute(Profile(missing: MissingSelection));

        Assert.NotEqual(complete, incomplete);
    }

    [Fact]
    public void Compute_ExtendedAndStandardSameId_Differ()
    {
        // 标准帧 0x500 与扩展帧 0x500 是两条不同报文（spec §6.1）。
        var standard = NmCanonicalProfileHash.Compute(
            Profile(messages: [Message("0x500", extended: false)]));
        var extended = NmCanonicalProfileHash.Compute(
            Profile(messages: [Message("0x500", extended: true)]));

        Assert.NotEqual(standard, extended);
    }

    // —— 以下用例迁移自 App 侧 NmSupplementHashTests（函数搬到 Core 后原文件删除）——
    // 它们覆盖的是「哪些 profile 字段必须参与 key」，与上面的 canonical 性质互补。

    [Fact]
    public void Compute_DifferentStandardTimings_Differ()
    {
        var a = NmCanonicalProfileHash.Compute(Profile() with
        {
            StandardTimings = new NmStandardTimings { CycleMs = 500, TimeoutMs = 2000 },
        });
        var b = NmCanonicalProfileHash.Compute(Profile() with
        {
            StandardTimings = new NmStandardTimings { CycleMs = 500, TimeoutMs = 2200 },
        });

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_DifferentStandardBitRoleConstraint_Differ()
    {
        var a = NmCanonicalProfileHash.Compute(Profile(bitRoles:
            [new NmBitRole(1, 0, "RepeatMessageRequest")]));
        var b = NmCanonicalProfileHash.Compute(Profile(bitRoles:
            [new NmBitRole(1, 0, "RepeatMessageRequest")
                { Constraint = NmBitConstraint.MustBeZeroWhileDisabled }]));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_DifferentStandardLayoutNidByte_Differ()
    {
        // StandardLayout 经 NmProfilePrecedence 驱动 nid/cbv/user_data 分析路径；
        // 只差一个字节也必须换 key，否则会命中旧报告。
        var a = NmCanonicalProfileHash.Compute(Profile() with
        {
            StandardLayout = new NmStandardLayout { NidByte = 0, CbvByte = 1 },
        });
        var b = NmCanonicalProfileHash.Compute(Profile() with
        {
            StandardLayout = new NmStandardLayout { NidByte = 1, CbvByte = 1 },
        });

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_DifferentStandardLayoutUserData_Differ()
    {
        var a = NmCanonicalProfileHash.Compute(Profile() with
        {
            StandardLayout = new NmStandardLayout { UserDataOffset = 4, UserDataLength = 4 },
        });
        var b = NmCanonicalProfileHash.Compute(Profile() with
        {
            StandardLayout = new NmStandardLayout { UserDataOffset = 4, UserDataLength = 2 },
        });

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_DifferentStandardFeaturesPnEnabled_Differ()
    {
        // StandardFeatures 驱动 pn_enabled 判定；只改这一个布尔也必须失效。
        var a = NmCanonicalProfileHash.Compute(Profile() with
        {
            StandardFeatures = new NmStandardFeatures { PnEnabled = false },
        });
        var b = NmCanonicalProfileHash.Compute(Profile() with
        {
            StandardFeatures = new NmStandardFeatures { PnEnabled = true },
        });

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_DifferentStandardFeaturesActiveWakeup_Differ()
    {
        var a = NmCanonicalProfileHash.Compute(Profile() with
        {
            StandardFeatures = new NmStandardFeatures { ActiveWakeupBitEnabled = true },
        });
        var b = NmCanonicalProfileHash.Compute(Profile() with
        {
            StandardFeatures = new NmStandardFeatures { ActiveWakeupBitEnabled = false },
        });

        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// spec §5.3：加载期提示（旧键 phase 改名）会进报告 Assumptions。它依赖 YAML
    /// 的**键拼写**而不是语义，所以改键名不改值时绑定后的 profile 逐字节相同——
    /// 缓存 key 不变就会命中旧报告，用户看到「修改没生效」。
    /// </summary>
    [Fact]
    public void Compute_StandardLoadAssumptions_ChangesHash()
    {
        var a = NmCanonicalProfileHash.Compute(
            Profile(rules: [Rule("NM_05", 500)]));
        var b = NmCanonicalProfileHash.Compute(
            (Profile(rules: [Rule("NM_05", 500)]) with
            {
                StandardLoadAssumptions = ["有 1 条规则使用了旧键 phase"],
            }));

        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// spec §4 / §19.2：<c>expect_nmsts</c> 改变判据（是否校验恢复后的目标状态）。
    /// 它走 <see cref="NmRuleExpect"/> 的逐字段投影进 key——但「自动纳入」是推断，
    /// 不是证据：若哪天换成手写序列化或漏了某个参数，只有这条测试能发现。
    /// </summary>
    [Fact]
    public void Compute_ExpectNmsts_ChangesHash()
    {
        var a = NmCanonicalProfileHash.Compute(Profile(rules: [RuleWithNmsts(expectNmsts: null)]));
        var b = NmCanonicalProfileHash.Compute(Profile(rules: [RuleWithNmsts(expectNmsts: 1)]));

        Assert.NotEqual(a, b);
    }

    private static NmRuleDef RuleWithNmsts(int? expectNmsts)
        => new("NM_RESUME", "requirement", "wakeup",
            new NmRuleWhen("normal_resume_shape", MinSilenceMs: 2000, CycleMs: 500),
            new NmRuleExpect("equals", ExpectNmsts: expectNmsts),
            "CODE")
        {
            Clauses = ["NM_10"],
        };
}
