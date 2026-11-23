namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 把展平后的企标 profile 绑到项目 <see cref="NmProfile"/> 上（spec §6.1 第 5 条）。
/// <para>
/// 只填三个 <c>Standard*</c> 字段；**不改动项目层已有字段**——项目 &gt; OEM &gt; base
/// 的优先级由 <see cref="NmTimingDefaults.FromProfile"/> 与 <see cref="NmTiming.Resolve"/>
/// 在读取时实现，绑定阶段不做合并，避免两处各写一份优先级规则。
/// </para>
/// </summary>
public static class NmStandardProfileBinding
{
    /// <param name="projectProfile">DBC/sidecar 构建的项目 profile。</param>
    /// <param name="flat">展平后的企标；null 表示未选企标。</param>
    public static NmProfile Bind(
        NmProfile projectProfile, NmStandardFlattener.Flattened? flat)
    {
        if (flat is null) return projectProfile;

        return projectProfile with
        {
            StandardTimings = flat.Timings,
            StandardBitRoles = flat.Layout?.BitRoles,
            StandardRules = flat.Rules,
        };
    }
}
