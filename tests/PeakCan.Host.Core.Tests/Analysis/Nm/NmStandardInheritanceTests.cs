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
