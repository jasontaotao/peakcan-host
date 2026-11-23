using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// NM M2 S1：企标 profile 的 YAML 加载。spec §5.4 / §6.2。
/// 用真实吉利 profile 片段锁住字段映射，避免加载器与文档脱节。
/// </summary>
public class NmStandardProfileYamlTests
{
    private const string GeelyMinimal = """
        standard:
          id: geely-cv
          vendor: geely
          version: "2.0"
        overrides:
          timings:
            cycle_ms: 500
            timeout_ms: 2000
            repeat_message_with_burst_ms: 1700
            repeat_message_without_burst_ms: 1500
            immediate_cycle_ms: 20
            immediate_count: 10
            tolerance_ratio: 0.1
          layout:
            nid_byte: 0
            cbv_byte: 1
            user_data_offset: 4
            user_data_length: 4
            bit_roles:
              - { byte: 1, bit: 0, name: RepeatMessageRequest, constraint: must_be_zero_while_disabled }
              - { byte: 1, bit: 4, name: ActiveWakeup, meaning: "0=被动唤醒, 1=主动唤醒" }
              - { byte: 1, bit: 6, name: Reserved, constraint: must_be_zero }
              - { byte: 3, bit: 0, name: NmSts }
          features:
            pn_enabled: false
        """;

    [Fact]
    public void Load_MapsTimingsAndLayout()
    {
        var profile = NmStandardYaml.Load(GeelyMinimal);

        Assert.Equal("geely-cv", profile.StandardId);
        Assert.Equal("geely", profile.Vendor);
        Assert.Equal("2.0", profile.Version);
        Assert.Equal(500, profile.Timings!.CycleMs);
        Assert.Equal(2000, profile.Timings.TimeoutMs);
        Assert.Equal(1700, profile.Timings.RepeatMessageWithBurstMs);
        Assert.Equal(1500, profile.Timings.RepeatMessageWithoutBurstMs);
        Assert.Equal(0.1, profile.Timings.ToleranceRatio);
        Assert.Equal((byte)0, profile.Layout!.NidByte);
        Assert.Equal((byte)1, profile.Layout.CbvByte);
        Assert.False(profile.Features!.PnEnabled);
    }

    [Fact]
    public void Load_MapsBitRolesWithConstraints()
    {
        var profile = NmStandardYaml.Load(GeelyMinimal);
        var roles = profile.Layout!.BitRoles!;

        Assert.Equal(4, roles.Count);

        var rmr = roles.Single(r => r.Name == "RepeatMessageRequest");
        Assert.Equal(1, rmr.Byte);
        Assert.Equal(0, rmr.Bit);
        Assert.Equal(NmBitConstraint.MustBeZeroWhileDisabled, rmr.Constraint);

        var awb = roles.Single(r => r.Name == "ActiveWakeup");
        Assert.Equal(4, awb.Bit);
        Assert.Equal(NmBitConstraint.None, awb.Constraint);
        Assert.Contains("主动唤醒", awb.Meaning);

        var reserved = roles.Single(r => r.Name == "Reserved");
        Assert.Equal(6, reserved.Bit);
        Assert.Equal(NmBitConstraint.MustBeZero, reserved.Constraint);

        var nmsts = roles.Single(r => r.Name == "NmSts");
        Assert.Equal(3, nmsts.Byte);
        Assert.Equal(0, nmsts.Bit);
        // NMSts 的 0/1 都合法，不得标零约束。
        Assert.Equal(NmBitConstraint.None, nmsts.Constraint);
    }

    [Fact]
    public void Load_RejectsUnknownTopLevelKey()
    {
        // 严格反序列化：拼错的字段必须报错，不能静默忽略（否则 profile 写了等于没写）。
        const string bad = """
            standard:
              id: x
              version: "1"
            overrides:
              timings:
                cycle_ms: 500
                cyclex_ms: 999
            """;

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardYaml.Load(bad));
        Assert.Contains("cyclex_ms", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_RejectsMissingStandardId()
    {
        const string bad = """
            standard:
              version: "1"
            """;

        Assert.Throws<NmProfileValidationException>(() => NmStandardYaml.Load(bad));
    }

    [Fact]
    public void Load_RejectsMalformedYaml()
    {
        const string bad = "standard: [unclosed";

        Assert.Throws<NmProfileValidationException>(() => NmStandardYaml.Load(bad));
    }

    [Fact]
    public void Load_RunsValidator_BadTimingRejectedAtEntry()
    {
        // spec §8：加载即校验。timeout <= cycle 的 profile 不能离开 Load。
        const string bad = """
            standard:
              id: x
            overrides:
              timings:
                cycle_ms: 2000
                timeout_ms: 2000
            """;

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardYaml.Load(bad));
        Assert.Contains("timeout", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_RejectsUnknownConstraintValue()
    {
        // 拼错的 constraint 若静默降级成 None，预留位检查会失效并漏报。
        const string bad = """
            standard:
              id: x
            overrides:
              layout:
                bit_roles:
                  - { byte: 1, bit: 6, name: Reserved, constraint: must_be_zer0 }
            """;

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardYaml.Load(bad));
        Assert.Contains("must_be_zer0", ex.Message);
    }

    [Fact]
    public void Load_RejectsUnknownObservabilityValue()
    {
        const string bad = """
            standard:
              id: x
            rules:
              - id: r1
                requirement: "5.5"
                when: { kind: any_nm_frame }
                expect: { kind: max_gap_ms }
                finding: NM_X
                observability: unobservale
            """;

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardYaml.Load(bad));
        Assert.Contains("unobservale", ex.Message);
    }

    [Fact]
    public void Load_RejectsNullListElement()
    {
        // null 元素不能穿透成 NullReferenceException。
        const string bad = """
            standard:
              id: x
            rules:
              - null
            """;

        Assert.Throws<NmProfileValidationException>(() => NmStandardYaml.Load(bad));
    }

    [Fact]
    public void Load_RejectsRuleMissingFinding()
    {
        const string bad = """
            standard:
              id: x
            rules:
              - id: r1
                requirement: "5.5"
                when: { kind: any_nm_frame }
                expect: { kind: max_gap_ms }
            """;

        Assert.Throws<NmProfileValidationException>(() => NmStandardYaml.Load(bad));
    }

    [Fact]
    public void Load_MapsRemoveList()
    {
        const string yaml = """
            standard:
              id: x
            overrides:
              remove:
                - bit_roles[1.6]
            """;

        var profile = NmStandardYaml.Load(yaml);

        var remove = profile.Overrides!.Remove!;
        Assert.Single(remove);
        Assert.Equal("bit_roles[1.6]", remove[0]);
    }
}
