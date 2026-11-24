using PeakCan.Host.Core.Analysis.Nm;
using Xunit;
using IO = System.IO;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// 吉利 §5.4.3 / §5.4.4 的预留位覆盖完整性。
/// <para>
/// <b>为什么需要这组测试</b>：预留位是「必须为 0」的约束，漏掉一位的后果是
/// 该位填 1 时报告里完全没有痕迹——不是误报，是**漏检**，且没有任何信号。
/// 而 profile 的位角色与规则是两处手工维护的清单（<c>bit_roles</c> 与 <c>rules</c>），
/// 靠人记住「两边都要加」迟早会漏。§5.4.3 的 bit6 就是这样漏的：
/// 它原本是 base 的 AUTOSAR PNI 位，吉利 profile 用 <c>remove: bit_roles[1.6]</c>
/// 删掉后没有补回「必须为 0」的声明。
/// </para>
/// <para>
/// 判据来自规范原文（§5.4.3）：Byte1 的 Bit1/Bit2/Bit3/Bit5/Bit6/Bit7 均为
/// 「预留位填充 0」，Bit0 是重复报文请求、Bit4 是主动唤醒位。
/// </para>
/// </summary>
public class NmReservedBitCoverageTests
{
    /// <summary>§5.4.3 表格里 Byte1 的预留位（Bit0=RMR、Bit4=AWB 不是预留位）。</summary>
    private static readonly int[] CbvReservedBits = [1, 2, 3, 5, 6, 7];

    /// <summary>§5.4.4：NMSts 占 byte3.bit0，其 bit1–7 全部预留。</summary>
    private static readonly int[] UserDataReservedBits = [1, 2, 3, 4, 5, 6, 7];

    private static NmStandardFlattener.Flattened LoadGeelyFlattened()
    {
        var geely = NmStandardYaml.LoadFile(
            RepoPath("profiles/oem/geely-cv@2.0.nmstd.yaml"));
        var baseProfile = NmStandardYaml.LoadFile(
            RepoPath("profiles/base/autosar-cannm@4.1.3.nmstd.yaml"));
        var registry = new Dictionary<string, NmStandardProfile>(StringComparer.Ordinal)
        {
            ["autosar-cannm@4.1.3"] = baseProfile,
            ["geely-cv@2.0"] = geely,
        };
        return NmStandardFlattener.Flatten(geely, registry);
    }

    /// <summary>
    /// 每一位预留位都必须在 <c>bit_roles</c> 里声明为「必须为 0」的角色。
    /// 没有角色 → 求值期 <c>TryFindRole</c> 找不到 → 规则进 Unmodeled，
    /// 等于「写了规则但从没检查过」，比不写规则更危险。
    /// <para>
    /// <b>判据必须含 Constraint</b>：只看 (byte,bit) 会被父层角色顶替的情况骗过——
    /// base 的 <c>PartialNetworkInfo</c> 也占 (1,6) 且 Constraint=None，
    /// 子层漏声明预留位时它照样能被「找到」，测试假绿。
    /// </para>
    /// </summary>
    [Fact]
    public void Geely_CbvReservedBits_AllHaveBitRole()
    {
        var flat = LoadGeelyFlattened();
        var roles = flat.Layout!.BitRoles!;

        foreach (var bit in CbvReservedBits)
        {
            var role = Assert.Single(roles, r => r.Byte == 1 && r.Bit == bit);
            Assert.True(role.Constraint == NmBitConstraint.MustBeZero,
                $"§5.4.3 要求 CBV byte1.bit{bit} 必须为 0，但合并后的角色是 " +
                $"「{role.Name}」（Constraint={role.Constraint}）——" +
                "子层没有声明预留位，父层的同名位顶替了它。");
        }
    }

    /// <summary>
    /// 每一位预留位都必须有一条 <c>bit_rule</c> 规则真的去判它。
    /// 只有角色没有规则 = 声明了但从不检查，同样静默漏检。
    /// </summary>
    [Fact]
    public void Geely_CbvReservedBits_AllHaveBitRule()
    {
        var flat = LoadGeelyFlattened();
        var rules = flat.Rules!;

        foreach (var bit in CbvReservedBits)
        {
            // 判据必须含 expect 与 finding：只看 (kind,byte,bit) 时，把 finding
            // 改成另一个已注册码、或把 expect 换掉，规则仍在但结论失效——
            // 而干净 trace 上没有任何 finding，契约测试也兜不住。
            var rule = Assert.Single(rules, r =>
                string.Equals(r.When.Kind, "bit_rule", StringComparison.Ordinal)
                && r.When.Byte == 1 && r.When.Bit == bit);

            Assert.True(string.Equals(rule.Expect.Kind, "is_zero", StringComparison.Ordinal),
                $"§5.4.3 要求 CBV byte1.bit{bit} 必须为 0，但规则 {rule.Id} 的 " +
                $"expect.kind 是「{rule.Expect.Kind}」——该位填 1 时不会被判定。");
            Assert.True(string.Equals(rule.FindingCode, "NM_RESERVED_BIT_SET", StringComparison.Ordinal),
                $"规则 {rule.Id} 的 finding 是「{rule.FindingCode}」，应为 NM_RESERVED_BIT_SET" +
                "——否则报告里看不出这是预留位违规。");
        }
    }

    /// <summary>
    /// 对照组：预留位规则确实能抓到违规。上面两条只证明「声明存在」，
    /// 若求值器坏了，声明齐全也照样漏检。
    /// </summary>
    [Fact]
    public void Geely_CbvBit6Set_IsReportedAsReservedViolation()
    {
        var flat = LoadGeelyFlattened();
        var profile = NmStandardProfileBinding.Bind(
            MinimalProjectProfile(), flat);

        // byte1 = 0x40 → bit6 = 1，其余位（含 bit0 RMR / bit4 AWB）为 0。
        var frames = new[]
        {
            Frame(0.0, cbv: 0x40), Frame(0.5, cbv: 0x40), Frame(1.0, cbv: 0x40),
        };

        var report = NmAnalyzer.Analyze(
            new NmAnalysisRequest("reserved-b1-6", null, frames, null, profile, 0, 1.5));

        var finding = Assert.Single(report.Findings, f => f.Code == "NM_RESERVED_BIT_SET");
        Assert.Equal("NM_RESERVED_B1_6", finding.RuleId);
        Assert.Contains("bit6", finding.Message, StringComparison.Ordinal);
        Assert.Contains("§5.4.3", finding.Clauses);

        // 同一位不能同时被当成 PNI：吉利的 byte1.bit6 是预留位，不是 AUTOSAR 的 PNI。
        // pn_enabled=false + PNI 位为 1 本会触发 NM_PNI_WHILE_PN_DISABLED；
        // 角色改名后该检查不得执行，否则同一帧会被两条互斥的语义同时解释。
        Assert.DoesNotContain(report.Findings, f => f.Code == "NM_PNI_WHILE_PN_DISABLED");
    }

    /// <summary>§5.4.4：byte3.bit1–7 是 NMSts 所在字节的预留位，同样必须逐位覆盖。</summary>
    [Fact]
    public void Geely_NmStsByteReservedBits_AllHaveBitRoleAndRule()
    {
        var flat = LoadGeelyFlattened();
        var roles = flat.Layout!.BitRoles!;
        var rules = flat.Rules!;

        foreach (var bit in UserDataReservedBits)
        {
            var role = Assert.Single(roles, r => r.Byte == 3 && r.Bit == bit);
            Assert.True(role.Constraint == NmBitConstraint.MustBeZero,
                $"§5.4.4 要求 NMSts 所在 byte3.bit{bit} 必须为 0，但角色是 " +
                $"「{role.Name}」（Constraint={role.Constraint}）。");

            Assert.Contains(rules, r =>
                string.Equals(r.When.Kind, "bit_rule", StringComparison.Ordinal)
                && r.When.Byte == 3 && r.When.Bit == bit);
        }
    }

    /// <summary>
    /// <b>反向守卫</b>：bit0 是 RMR、bit4 是 AWB，二者都**不是**预留位。
    /// 若有人把整字节声明成「必须为 0」，合法的 AWB=1 会被误报。
    /// 这里钉住「这两位没有 is_zero 的 bit_rule」。
    /// </summary>
    [Fact]
    public void Geely_RmrAndAwbBits_AreNotReservedZeroRules()
    {
        var flat = LoadGeelyFlattened();
        var rules = flat.Rules!;

        foreach (var bit in new[] { 0, 4 })
        {
            Assert.DoesNotContain(rules, r =>
                string.Equals(r.When.Kind, "bit_rule", StringComparison.Ordinal)
                && r.When.Byte == 1 && r.When.Bit == bit
                && string.Equals(r.Expect.Kind, "is_zero", StringComparison.Ordinal)
                && string.Equals(r.FindingCode, "NM_RESERVED_BIT_SET", StringComparison.Ordinal));
        }
    }

    private static readonly string[] CannmReasons = ["MsgType=CANNM"];

    private static NmProfile MinimalProjectProfile() => new(
        NmProfileSource.Dbc,
        [
            new NmMessageDef("0x18FFFAF4", true, "NM_A", "A", 8, true,
                CannmReasons, CycleMs: 500, TimeoutMs: 2000),
        ],
        new NmTimings(CycleMs: 500, TimeoutMs: 2000),
        [new NmNodeDef("A", StationAddress: 0xF4, IsNmNode: true)],
        Array.Empty<NmConfigConflict>(),
        Array.Empty<string>(),
        new NmProvenance(),
        Layout: new NmLayout(NidByte: 0, CbvByte: 1),
        Features: new NmFeatures(PnEnabled: false));

    // byte0 = NID 0xF4（与 0x18FFFAF4 低字节一致）；byte1 = CBV；byte3 = NMSts=1。
    private static PeakCan.Host.Core.Replay.ReplayFrame Frame(double t, byte cbv)
        => new(t, 0x18FFFAF4, 8, new byte[] { 0xF4, cbv, 0x00, 0x01, 0, 0, 0, 0 },
            PeakCan.HIL.Core.FrameFlags.None, IsExtended: true);

    private static string RepoPath(string relative)
    {
        var dir = new IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !IO.File.Exists(IO.Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return IO.Path.Combine(dir!.FullName, relative);
    }
}
