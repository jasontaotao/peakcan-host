using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// NM M2 S1：三层继承展平（spec §6.1）。项目 &gt; OEM &gt; base，逐字段覆盖，
/// 继承差异不记冲突，循环继承必须报错。
/// </summary>
public class NmStandardInheritanceTests
{
    [Fact]
    public void Flatten_ChildOverridesParentFields()
    {
        var baseProfile = new NmStandardProfile
        {
            StandardId = "autosar",
            Overrides = new NmStandardOverrides
            {
                Timings = new NmStandardTimings { CycleMs = 500, TimeoutMs = 2000, ToleranceRatio = 0.2 },
            },
        };
        var child = new NmStandardProfile
        {
            StandardId = "geely",
            Extends = "autosar@1.0",
            Overrides = new NmStandardOverrides
            {
                Timings = new NmStandardTimings { ToleranceRatio = 0.1 },
            },
        };

        var flat = NmStandardFlattener.Flatten(child, new Dictionary<string, NmStandardProfile>
        {
            ["autosar@1.0"] = baseProfile,
        });

        // 子覆盖生效。
        Assert.Equal(0.1, flat.Timings!.ToleranceRatio);
        // 未覆盖字段继承父值。
        Assert.Equal(500, flat.Timings.CycleMs);
        Assert.Equal(2000, flat.Timings.TimeoutMs);
        Assert.Contains("geely", flat.Provenance);
        Assert.Contains("autosar", flat.Provenance);
    }

    [Fact]
    public void Flatten_BitRolesFromParentAndChildMerge()
    {
        var baseProfile = new NmStandardProfile
        {
            StandardId = "autosar",
            Overrides = new NmStandardOverrides
            {
                Layout = new NmStandardLayout
                {
                    NidByte = 0,
                    BitRoles = new[] { new NmBitRole(1, 6, "Pni") },
                },
            },
        };
        var child = new NmStandardProfile
        {
            StandardId = "geely",
            Extends = "autosar@1.0",
            Overrides = new NmStandardOverrides
            {
                Layout = new NmStandardLayout
                {
                    BitRoles = new[] { new NmBitRole(1, 4, "ActiveWakeup") },
                },
            },
        };

        var flat = NmStandardFlattener.Flatten(child, new Dictionary<string, NmStandardProfile>
        {
            ["autosar@1.0"] = baseProfile,
        });

        var roles = flat.Layout!.BitRoles!;
        // 父的 PNI 与子的 AWB 都在：按 (byte,bit) 合并，不是整表替换。
        Assert.Contains(roles, r => r.Name == "Pni");
        Assert.Contains(roles, r => r.Name == "ActiveWakeup");
        Assert.Equal((byte)0, flat.Layout.NidByte);
    }

    [Fact]
    public void Flatten_CyclicExtends_Throws()
    {
        var a = new NmStandardProfile { StandardId = "a", Extends = "b" };
        var b = new NmStandardProfile { StandardId = "b", Extends = "a" };

        var ex = Assert.Throws<NmProfileValidationException>(() =>
            NmStandardFlattener.Flatten(a, new Dictionary<string, NmStandardProfile>
            {
                ["a"] = a,
                ["b"] = b,
            }));

        Assert.Contains("循环", ex.Message);
    }

    [Fact]
    public void Flatten_MissingParent_Throws()
    {
        var child = new NmStandardProfile { StandardId = "child", Extends = "missing" };

        var ex = Assert.Throws<NmProfileValidationException>(() =>
            NmStandardFlattener.Flatten(child, new Dictionary<string, NmStandardProfile>()));

        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public void Flatten_RemoveDeletesParentBitRole()
    {
        // spec §6.2 的三态 patch：OEM 用 remove 删除 base 的 PNI 位。
        var baseProfile = new NmStandardProfile
        {
            StandardId = "autosar",
            Overrides = new NmStandardOverrides
            {
                Layout = new NmStandardLayout
                {
                    BitRoles = new[] { new NmBitRole(1, 6, "Pni"), new NmBitRole(1, 4, "Awb") },
                },
            },
        };
        var child = new NmStandardProfile
        {
            StandardId = "geely",
            Extends = "autosar@1.0",
            Overrides = new NmStandardOverrides { Remove = new[] { "bit_roles[1.6]" } },
        };

        var flat = NmStandardFlattener.Flatten(child, new Dictionary<string, NmStandardProfile>
        {
            ["autosar@1.0"] = baseProfile,
        });

        var roles = flat.Layout!.BitRoles!;
        Assert.DoesNotContain(roles, r => r.Name == "Pni");
        Assert.Contains(roles, r => r.Name == "Awb");
    }

    // spec §19.4 #14 / §6.2「已知实现缺陷」：remove 的 key 不存在时静默无效。
    // 规则改名后旧的 remove: rules[NM_06] 会变成 no-op——profile 看起来删了规则，
    // 实际还在跑。必须报错，不能静默。
    [Theory]
    [InlineData("rules[NM_DOES_NOT_EXIST]", "NM_DOES_NOT_EXIST")]
    [InlineData("messages[0x999]", "0x999")]
    [InlineData("nodes[NoSuchNode]", "NoSuchNode")]
    public void Flatten_RemoveMissingKey_Throws(string removeEntry, string missingKey)
    {
        var child = new NmStandardProfile
        {
            StandardId = "c",
            Overrides = new NmStandardOverrides { Remove = new[] { removeEntry } },
        };

        var ex = Assert.Throws<NmProfileValidationException>(() =>
            NmStandardFlattener.Flatten(child, new Dictionary<string, NmStandardProfile>()));

        // 错误消息必须点名缺失的 key，否则用户不知道是哪一条 remove 失效了。
        Assert.Contains(missingKey, ex.Message, StringComparison.Ordinal);
        Assert.Contains("remove", ex.Message, StringComparison.Ordinal);
    }

    // spec §5.3：加载期提示是**链级**信息。只把 Selected 的提示带出去会丢掉父层的，
    // 而展平是唯一看得到整条链的地方。
    [Fact]
    public void Flatten_MergesLoadAssumptionsFromWholeChain()
    {
        var parent = new NmStandardProfile
        {
            StandardId = "p",
            LoadAssumptions = ["父层提示"],
        };
        var child = new NmStandardProfile
        {
            StandardId = "c",
            Extends = "p@1.0",
            LoadAssumptions = ["子层提示"],
        };

        var flat = NmStandardFlattener.Flatten(child, new Dictionary<string, NmStandardProfile>
        {
            ["p@1.0"] = parent,
        });

        Assert.Contains("父层提示", flat.LoadAssumptions);
        Assert.Contains("子层提示", flat.LoadAssumptions);
    }

    // 两层各有一条规则用旧键时，两份提示不能互相吞掉——否则报告说「1 条」，
    // 实际链上 2 条，用户按提示只改一处。层标识必须进句子。
    [Fact]
    public void Load_LegacyPhaseInBothLayers_ProducesDistinctAssumptions()
    {
        var baseProfile = NmStandardYaml.Load("""
            standard:
              id: autosar-cannm
              version: "4.1.3"
            rules:
              - id: R_BASE
                requirement: 周期
                phase: normal
                observability: observed
                clauses: []
                when:
                  kind: cycle_tolerance
                expect:
                  kind: within_pct
                finding: NM_CYCLE_DEVIATION
                source_ref: §9
            """);
        var child = NmStandardYaml.Load("""
            standard:
              id: geely-cv
              version: "2.0"
              extends: autosar-cannm@4.1.3
            rules:
              - id: R_CHILD
                requirement: 周期
                phase: normal
                observability: observed
                clauses: ["§5.5"]
                when:
                  kind: cycle_tolerance
                expect:
                  kind: within_pct
                finding: NM_CYCLE_DEVIATION
                source_ref: §5.5
            """);

        // 前置：两层各自都产了提示，否则下面的「两条」可能是空转。
        Assert.Single(baseProfile.LoadAssumptions);
        Assert.Single(child.LoadAssumptions);

        var flat = NmStandardFlattener.Flatten(child, new Dictionary<string, NmStandardProfile>
        {
            ["autosar-cannm@4.1.3"] = baseProfile,
        });

        // 两条都在，且各自点名所属层——用户才知道该去改哪个文件。
        Assert.Equal(2, flat.LoadAssumptions.Count);
        Assert.Contains(flat.LoadAssumptions, a => a.Contains("autosar-cannm@4.1.3", StringComparison.Ordinal));
        Assert.Contains(flat.LoadAssumptions, a => a.Contains("geely-cv@2.0", StringComparison.Ordinal));
    }

    // 反向守卫：同一句提示在多层重复时只保留一份，否则报告会被同一行刷屏。
    [Fact]
    public void Flatten_DeduplicatesIdenticalLoadAssumptions()
    {
        var parent = new NmStandardProfile { StandardId = "p", LoadAssumptions = ["同一句"] };
        var child = new NmStandardProfile
        {
            StandardId = "c",
            Extends = "p@1.0",
            LoadAssumptions = ["同一句"],
        };

        var flat = NmStandardFlattener.Flatten(child, new Dictionary<string, NmStandardProfile>
        {
            ["p@1.0"] = parent,
        });

        Assert.Single(flat.LoadAssumptions);
    }

    // 空 remove 项此前是静默 no-op，与本次「remove 指向不存在的目标必须报错」的
    // 语义直接冲突：用户写了 remove 却什么都没删，报告毫无迹象。
    [Fact]
    public void Flatten_BlankRemoveEntry_Throws()
    {
        var child = new NmStandardProfile
        {
            StandardId = "c",
            Overrides = new NmStandardOverrides { Remove = new[] { "  " } },
        };

        var ex = Assert.Throws<NmProfileValidationException>(() =>
            NmStandardFlattener.Flatten(child, new Dictionary<string, NmStandardProfile>()));

        Assert.Contains("remove", ex.Message, StringComparison.Ordinal);
    }

    // bit_roles 的两条报错路径各自独立：链上没有任何位角色 vs 有位角色但没有这一位。
    // 只测成功路径时，把 `roles is null` 改回 break 或删掉 RemoveAll==0 判断都不会红。
    [Fact]
    public void Flatten_RemoveBitRoleWithNoRolesDeclared_Throws()
    {
        var child = new NmStandardProfile
        {
            StandardId = "c",
            Overrides = new NmStandardOverrides { Remove = new[] { "bit_roles[1.6]" } },
        };

        var ex = Assert.Throws<NmProfileValidationException>(() =>
            NmStandardFlattener.Flatten(child, new Dictionary<string, NmStandardProfile>()));

        Assert.Contains("位角色", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Flatten_RemoveBitRoleNotPresent_Throws()
    {
        // 父层声明了别的位，但没有 1.6。
        var parent = new NmStandardProfile
        {
            StandardId = "p",
            Overrides = new NmStandardOverrides
            {
                Layout = new NmStandardLayout { BitRoles = new[] { new NmBitRole(1, 4, "Awb") } },
            },
        };
        var child = new NmStandardProfile
        {
            StandardId = "c",
            Extends = "p@1.0",
            Overrides = new NmStandardOverrides { Remove = new[] { "bit_roles[1.6]" } },
        };

        var ex = Assert.Throws<NmProfileValidationException>(() =>
            NmStandardFlattener.Flatten(child, new Dictionary<string, NmStandardProfile>
            {
                ["p@1.0"] = parent,
            }));

        Assert.Contains("1.6", ex.Message, StringComparison.Ordinal);
    }

    // 反向守卫：key 存在时不得误报。与上面成对，防止「一律报错」把合法 remove 也拦下。
    [Fact]
    public void Flatten_RemoveExistingRule_Succeeds()
    {
        var parent = new NmStandardProfile
        {
            StandardId = "p",
            Rules =
            [
                new NmRuleDef("NM_CYCLE", "周期", "normal",
                    new NmRuleWhen("cycle_tolerance"),
                    new NmRuleExpect("within_pct"), "NM_CYCLE_DEVIATION")
                {
                    SourceRef = "§5.5",
                    Clauses = ["§5.5"],
                },
            ],
        };
        var child = new NmStandardProfile
        {
            StandardId = "c",
            Extends = "p@1.0",
            Overrides = new NmStandardOverrides { Remove = new[] { "rules[NM_CYCLE]" } },
        };

        var flat = NmStandardFlattener.Flatten(child, new Dictionary<string, NmStandardProfile>
        {
            ["p@1.0"] = parent,
        });

        Assert.Null(flat.Rules);
    }

    [Fact]
    public void Flatten_SameIdDifferentVersion_NotTreatedAsCycle()
    {
        // geely-cv@2.0 extends geely-cv@1.5 是合法的版本链，不是循环。
        var v15 = new NmStandardProfile
        {
            StandardId = "geely-cv",
            Version = "1.5",
            Overrides = new NmStandardOverrides
            {
                Timings = new NmStandardTimings { CycleMs = 500 },
            },
        };
        var v20 = new NmStandardProfile
        {
            StandardId = "geely-cv",
            Version = "2.0",
            Extends = "geely-cv@1.5",
        };

        var flat = NmStandardFlattener.Flatten(v20, new Dictionary<string, NmStandardProfile>
        {
            ["geely-cv@1.5"] = v15,
        });

        Assert.Equal(500, flat.Timings!.CycleMs);
    }

    [Fact]
    public void Flatten_MessagesKeyedByExtendedFlag()
    {
        // 标准帧 0x400 与扩展帧 0x400 是两条不同报文，不能互相覆盖。
        var parent = new NmStandardProfile
        {
            StandardId = "p",
            Messages = new[] { new NmStandardMessage("Std", "0x400", Extended: false) },
        };
        var child = new NmStandardProfile
        {
            StandardId = "c",
            Extends = "p@1.0",
            Messages = new[] { new NmStandardMessage("Ext", "0x400", Extended: true) },
        };

        var flat = NmStandardFlattener.Flatten(child, new Dictionary<string, NmStandardProfile>
        {
            ["p@1.0"] = parent,
        });

        Assert.Equal(2, flat.Messages!.Count);
        Assert.Contains(flat.Messages, m => m.Name == "Std" && !m.Extended);
        Assert.Contains(flat.Messages, m => m.Name == "Ext" && m.Extended);
    }

    [Fact]
    public void Flatten_UnknownRemoveField_Throws()
    {
        var child = new NmStandardProfile
        {
            StandardId = "c",
            Overrides = new NmStandardOverrides { Remove = new[] { "bogus[x]" } },
        };

        var ex = Assert.Throws<NmProfileValidationException>(() =>
            NmStandardFlattener.Flatten(child, new Dictionary<string, NmStandardProfile>()));

        Assert.Contains("bogus", ex.Message);
    }

    [Fact]
    public void Flatten_NoExtends_ReturnsSelf()
    {
        var solo = new NmStandardProfile
        {
            StandardId = "solo",
            Overrides = new NmStandardOverrides
            {
                Timings = new NmStandardTimings { CycleMs = 500 },
            },
        };

        var flat = NmStandardFlattener.Flatten(solo, new Dictionary<string, NmStandardProfile>());

        Assert.Equal(500, flat.Timings!.CycleMs);
        Assert.Contains("solo", flat.Provenance);
    }
}
