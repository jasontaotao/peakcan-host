using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// profile 语义摘要（spec §10.1.1），参与报告缓存 key。
/// <para>
/// <b>为什么不能直接序列化 <see cref="NmProfile"/></b>：它的 <see cref="NmProvenance"/>
/// 带文件路径与 DBC hash（同一份内容换个路径会拆成两个缓存项），
/// <see cref="NmProfile.Source"/> 是「怎么来的」而非内容，列表字段的插入顺序
/// 也不该影响 hash（spec §10.1.1「列表有业务 key 的按 key 排序」）。
/// 因此先投影成显式 DTO，再按固定 JSON 规则序列化。
/// </para>
/// <para>
/// <b>为什么用 JSON 而不是分隔符拼接</b>：自由文本（finding code、assumption、节点名）
/// 含分隔符时会拼出同一串（<c>["x;y"]</c> 与 <c>["x","y"]</c>），造成「内容不同、
/// hash 相同」的假命中。JSON 的引号与转义把边界表达清楚。
/// </para>
/// </summary>
public static class NmCanonicalProfileHash
{
    /// <summary>spec §10.1.1 的固定序列化规则：snake_case、无缩进、不做文化相关转换。</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        // 数值统一走 invariant：默认序列化已如此，显式声明是为了防止将来有人加 converter。
        NumberHandling = JsonNumberHandling.Strict,
    };

    /// <param name="profile">绑定企标后的项目 profile。</param>
    /// <param name="standardId">
    /// 企标身份 <c>id@version</c>；null = 未选企标。报告会把它写进 <c>StandardId</c>，
    /// 因此必须参与 key——否则「选中/未选中同一份 profile」会命中同一份旧报告。
    /// </param>
    public static string Compute(NmProfile profile, string? standardId = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var payload = new CanonicalProfile(
            StandardId: standardId ?? string.Empty,
            // 按业务 key 排序（spec §10.1.1）：messages → can_id + ext，nodes → name。
            Messages: profile.Messages
                .Select(m => new CanonicalMessage(
                    m.CanIdHex, m.IsExtended, m.Name, m.Sender, m.Dlc, m.IsNm,
                    // Reasons 是字符串集合、无业务 key：排序而非保序，否则 DBC 枚举顺序漂移
                    // 会让「同一份 DBC」产生不同 hash。空数组与 null 等价（都序列化为 []）。
                    (m.Reasons ?? Array.Empty<string>()).Order(StringComparer.Ordinal).ToArray(),
                    m.CycleMs, m.TimeoutMs, m.MarkerAuthoritative))
                .OrderBy(m => m.CanIdHex, StringComparer.Ordinal)
                .ThenBy(m => m.IsExtended)
                .ToArray(),
            Timings: new CanonicalTimings(
                profile.Timings.CycleMs, profile.Timings.TimeoutMs,
                profile.Timings.RepeatMessageMs, profile.Timings.WaitBusSleepMs,
                profile.Timings.ImmediateCycleMs, profile.Timings.ImmediateCount),
            Nodes: profile.Nodes
                .Select(n => new CanonicalNode(n.Name, n.StationAddress, n.NodeIdentifier, n.IsNmNode))
                .OrderBy(n => n.Name, StringComparer.Ordinal)
                .ToArray(),
            // 冲突的值/来源顺序也是「同一事实的不同表述」，排序后参与 hash。
            Conflicts: profile.Conflicts
                .Select(c => new CanonicalConflict(
                    c.Field,
                    (c.Values ?? Array.Empty<string>()).Order(StringComparer.Ordinal).ToArray(),
                    (c.Sources ?? Array.Empty<string>()).Order(StringComparer.Ordinal).ToArray()))
                .OrderBy(c => c.Field, StringComparer.Ordinal)
                .ToArray(),
            // MissingFields 驱动 MissingInputs 与自动补全，必须参与（排序同 Reasons）。
            MissingFields: (profile.MissingFields ?? Array.Empty<string>())
                .Order(StringComparer.Ordinal).ToArray(),
            IdMapping: profile.IdMapping?.ToString(),
            Layout: profile.Layout is { } layout
                ? new CanonicalLayout(layout.NidByte, layout.CbvByte,
                    layout.UserDataOffset, layout.UserDataLength)
                : null,
            Features: profile.Features is { } features
                ? new CanonicalFeatures(features.ActiveWakeupBitEnabled,
                    features.RepeatMsgIndEnabled, features.PnEnabled)
                : null,
            StandardTimings: profile.StandardTimings is { } st
                ? new CanonicalStandardTimings(
                    st.CycleMs, st.TimeoutMs, st.WaitBusSleepMs,
                    st.RepeatMessageWithBurstMs, st.RepeatMessageWithoutBurstMs,
                    st.ImmediateCycleMs, st.ImmediateCount, st.StartNmTxMs,
                    st.WakeUpMs, st.ToleranceRatio, st.ToleranceAbsMs)
                : null,
            // 位角色按 (byte,bit) 排序（spec §10.1.1 的业务 key）。
            StandardBitRoles: profile.StandardBitRoles is { } roles
                ? roles
                    .Select(r => new CanonicalBitRole(
                        r.Byte, r.Bit, r.Name, r.Meaning, (int)r.Constraint))
                    .OrderBy(r => r.Byte).ThenBy(r => r.Bit)
                    .ToArray()
                : null,
            // 规则整条投影（含 when/expect 参数）：只比 Id+Kind 会在只改参数时漏失效。
            StandardRules: profile.StandardRules is { } rules
                ? rules.Select(CanonicalRule.From)
                    .OrderBy(r => r.Id, StringComparer.Ordinal)
                    .ToArray()
                : null,
            StandardLayout: profile.StandardLayout is { } sl
                ? new CanonicalStandardLayout(
                    sl.NidByte, sl.CbvByte, sl.UserDataOffset, sl.UserDataLength,
                    sl.BitRoles is { } slRoles
                        ? slRoles
                            .Select(r => new CanonicalBitRole(
                                r.Byte, r.Bit, r.Name, r.Meaning, (int)r.Constraint))
                            .OrderBy(r => r.Byte).ThenBy(r => r.Bit)
                            .ToArray()
                        : null)
                : null,
            StandardFeatures: profile.StandardFeatures is { } sf
                ? new CanonicalFeatures(sf.ActiveWakeupBitEnabled,
                    sf.RepeatMsgIndEnabled, sf.PnEnabled)
                : null,
            // 加载期提示依赖 YAML 键拼写而非语义，改键名不改值时其余字段逐字节相同，
            // 不参与 hash 就会命中旧报告（用户看到「修改没生效」）。排序同 Reasons。
            StandardLoadAssumptions: profile.StandardLoadAssumptions is { Count: > 0 } assumptions
                ? assumptions.Order(StringComparer.Ordinal).ToArray()
                : null,
            // spec §9.5：未覆盖声明直接改变条款覆盖表的结论（Covered → Partial）。
            // 不参与 hash 的后果最坏——用户改了 profile 却看到「已覆盖」，
            // 且报告是旧的，无从发现。排序同 Reasons：声明顺序不影响语义。
            StandardUncoveredClauses: profile.StandardUncoveredClauses is { Count: > 0 } uncovered
                ? uncovered
                    .Select(d => new CanonicalUncoveredClause(d.Clause, d.Facet, d.ReasonCode))
                    .OrderBy(d => d.Clause, StringComparer.Ordinal)
                    .ThenBy(d => d.Facet ?? string.Empty, StringComparer.Ordinal)
                    .ToArray()
                : null);

        var json = JsonSerializer.Serialize(payload, Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    // —— 以下是 canonical DTO：字段集合即「什么算 profile 语义」的唯一清单。——
    // 新增 NmProfile 字段时必须同步这里，否则该字段改动不会失效缓存。

    private sealed record CanonicalProfile(
        string StandardId,
        CanonicalMessage[] Messages,
        CanonicalTimings Timings,
        CanonicalNode[] Nodes,
        CanonicalConflict[] Conflicts,
        string[] MissingFields,
        string? IdMapping,
        CanonicalLayout? Layout,
        CanonicalFeatures? Features,
        CanonicalStandardTimings? StandardTimings,
        CanonicalBitRole[]? StandardBitRoles,
        CanonicalRule[]? StandardRules,
        CanonicalStandardLayout? StandardLayout,
        CanonicalFeatures? StandardFeatures,
        string[]? StandardLoadAssumptions,
        CanonicalUncoveredClause[]? StandardUncoveredClauses);

    /// <summary>spec §9.5 未覆盖声明。Facet 为 null 表示整条条款无规则覆盖。</summary>
    private sealed record CanonicalUncoveredClause(
        string Clause, string? Facet, string ReasonCode);

    private sealed record CanonicalMessage(
        string CanIdHex,
        bool IsExtended,
        string? Name,
        string? Sender,
        byte Dlc,
        bool IsNm,
        string[] Reasons,
        int? CycleMs,
        int? TimeoutMs,
        bool MarkerAuthoritative);

    private sealed record CanonicalTimings(
        int? CycleMs, int? TimeoutMs, int? RepeatMessageMs,
        int? WaitBusSleepMs, int? ImmediateCycleMs, int? ImmediateCount);

    private sealed record CanonicalNode(
        string Name, byte? StationAddress, byte? NodeIdentifier, bool IsNmNode);

    private sealed record CanonicalConflict(string Field, string[] Values, string[] Sources);

    private sealed record CanonicalLayout(
        byte? NidByte, byte? CbvByte, byte? UserDataOffset, int? UserDataLength);

    private sealed record CanonicalFeatures(
        bool? ActiveWakeupBitEnabled, bool? RepeatMsgIndEnabled, bool? PnEnabled);

    private sealed record CanonicalStandardTimings(
        int? CycleMs, int? TimeoutMs, int? WaitBusSleepMs,
        int? RepeatMessageWithBurstMs, int? RepeatMessageWithoutBurstMs,
        int? ImmediateCycleMs, int? ImmediateCount, int? StartNmTxMs,
        int? WakeUpMs, double? ToleranceRatio, int? ToleranceAbsMs);

    private sealed record CanonicalBitRole(
        byte Byte, int Bit, string Name, string? Meaning, int Constraint);

    private sealed record CanonicalStandardLayout(
        byte? NidByte, byte? CbvByte, byte? UserDataOffset, int? UserDataLength,
        CanonicalBitRole[]? BitRoles);

    private sealed record CanonicalRule(
        string Id, string Requirement, string DeclaredPhase,
        CanonicalRuleWhen When, CanonicalRuleExpect Expect,
        string FindingCode, string? SourceRef, int Observability, string[] Clauses)
    {
        public static CanonicalRule From(NmRuleDef rule) => new(
            rule.Id, rule.Requirement, rule.DeclaredPhase,
            CanonicalRuleWhen.From(rule.When), CanonicalRuleExpect.From(rule.Expect),
            rule.FindingCode, rule.SourceRef, (int)rule.Observability,
            // 条款是多条款规则的完整列表；排序保证 YAML 声明顺序不影响 hash。
            (rule.Clauses ?? Array.Empty<string>()).Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// 规则的 when 参数全集。逐字段显式列出而非序列化原 record：
    /// 后者会随新增字段自动变化（想要的）但也可能带进不该参与 hash 的派生属性（不想要的），
    /// 这里取「显式清单」，新增原语参数时必须同步。
    /// </summary>
    private sealed record CanonicalRuleWhen(
        string Kind, byte? Byte, int? Bit, int? Length, string? Role,
        string? LatencyKind, int? MinSilenceMs, int? CycleMs, double? Pct,
        int? MinFrames, int? TimeoutMs, string? MinId, string? MaxId, bool? Extended)
    {
        public static CanonicalRuleWhen From(NmRuleWhen w) => new(
            w.Kind, w.Byte, w.Bit, w.Length, w.Role, w.LatencyKind, w.MinSilenceMs,
            w.CycleMs, w.Pct, w.MinFrames, w.TimeoutMs, w.MinId, w.MaxId, w.Extended);
    }

    private sealed record CanonicalRuleExpect(
        string Kind, string? Value, byte? Byte, int? Bit, int? Count, int? IntervalMs,
        double? Pct, int? MinMs, int? MaxMs, string? Anchor, bool? WithBurst,
        int? ExpectNmsts)
    {
        public static CanonicalRuleExpect From(NmRuleExpect e) => new(
            e.Kind, e.Value, e.Byte, e.Bit, e.Count, e.IntervalMs, e.Pct,
            e.MinMs, e.MaxMs, e.Anchor, e.WithBurst, e.ExpectNmsts);
    }
}
