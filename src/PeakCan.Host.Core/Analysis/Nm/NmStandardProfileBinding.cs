namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 把展平后的企标 profile 绑到项目 <see cref="NmProfile"/> 上（spec §6.1 第 5 条）。
/// <para>
/// 只填 <c>Standard*</c> 字段并更新来源链；**不改动项目层已有字段**——项目 &gt; OEM &gt; base
/// 的优先级由 <see cref="NmProfilePrecedence"/> 与 <see cref="NmTimingDefaults.FromProfile"/> /
/// <see cref="NmTiming.Resolve"/> 在读取时实现，绑定阶段不做合并，避免两处各写一份优先级规则。
/// </para>
/// </summary>
public static class NmStandardProfileBinding
{
    /// <param name="projectProfile">DBC/sidecar 构建的项目 profile。</param>
    /// <param name="flat">展平后的企标；null 表示未选企标。</param>
    public static NmProfile Bind(
        NmProfile projectProfile, NmStandardFlattener.Flattened? flat)
    {
        // 未选企标时原样返回：项目层 provenance 保持 "dbc"/"none"，不得被覆盖。
        if (flat is null) return projectProfile;

        return projectProfile with
        {
            StandardTimings = flat.Timings,
            StandardLayout = flat.Layout,
            StandardFeatures = flat.Features,
            StandardBitRoles = flat.Layout?.BitRoles,
            StandardRules = flat.Rules,
            // 链级加载期提示（spec §5.3）：只读 Selected 会丢掉父层的提示。
            StandardLoadAssumptions = flat.LoadAssumptions.Count == 0
                ? null
                : flat.LoadAssumptions,
            // spec §9.5：已知未被检查的条款半边。不接这一步，profile 里写的
            // uncovered_clauses 到不了报告，NM_03 会继续显示「已覆盖」。
            StandardUncoveredClauses = flat.UncoveredClauses,
            // 报告要能自述实际判定依据的继承链（base < OEM），否则 resolved_from 会
            // 一直谎报 "dbc"。ProfilePath/Hash 仍来自项目层，保留原值。
            Provenance = projectProfile.Provenance with { ResolvedFrom = flat.Provenance },
        };
    }
}
