namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 逐字段合并用户补充输入。用户显式值优先于 DBC；空值不覆盖。
/// </summary>
public static class NmSupplementMerger
{
    public static NmProfile Merge(
        NmProfile profile, NmUserSupplement? supplement,
        bool applyLayout = true, bool applySelectors = true)
    {
        if (supplement is null) return profile;

        var source = profile.Source | NmProfileSource.Sidecar;

        // sidecar 属于另一份 DBC 时，selectors 也是按旧 DBC 勾选的，不能继续生效；
        // 否则旧 ID 会被误标成 NM，而用户看不到任何冲突提示。
        var selected = applySelectors && supplement.NmMessageSelectors is { Count: > 0 } ids
            ? new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase)
            : null;

        var messages = profile.Messages
            .Select(m =>
            {
                // DBC 已有证据的报文不重复打标；仅把用户刚勾选的报文补成 NM。
                if (selected is not null && selected.Contains(m.CanIdHex) && !m.IsNm)
                {
                    return m with
                    {
                        IsNm = true,
                        Reasons = m.Reasons.Append("UserSelected").ToArray(),
                        MarkerAuthoritative = false,
                    };
                }
                return m;
            })
            // spec §6.6.2：用户显式 Timings 优先于 DBC，且逐报文覆盖；
            // 用户没给的字段保持 DBC 值。只覆盖 NM 报文，避免把 NM 的周期
            // 套到普通应用报文上，污染 profile 的事实数据。
            .Select(m => m.IsNm
                ? m with
                {
                    CycleMs = supplement.Timings?.CycleMs ?? m.CycleMs,
                    TimeoutMs = supplement.Timings?.TimeoutMs ?? m.TimeoutMs,
                }
                : m)
            .ToArray();

        var mergedLayout = new NmLayout(
            NidByte: supplement.NidByte ?? profile.Layout?.NidByte,
            CbvByte: supplement.CbvByte ?? profile.Layout?.CbvByte,
            UserDataOffset: supplement.UserDataOffset ?? profile.Layout?.UserDataOffset,
            UserDataLength: supplement.UserDataLength ?? profile.Layout?.UserDataLength);

        // 原 profile 没有布局信息、且合并后所有字段仍为空时保持 null：
        // 不能把“无布局信息”伪装成“有布局但字段全空”。
        var layout = profile.Layout is null
            && mergedLayout.NidByte is null
            && mergedLayout.CbvByte is null
            && mergedLayout.UserDataOffset is null
            && mergedLayout.UserDataLength is null
                ? null
                : mergedLayout;

        var timings = supplement.Timings is null
            ? profile.Timings
            : new NmTimings(
                CycleMs: supplement.Timings.CycleMs ?? profile.Timings.CycleMs,
                TimeoutMs: supplement.Timings.TimeoutMs ?? profile.Timings.TimeoutMs,
                RepeatMessageMs: supplement.Timings.RepeatMessageMs ?? profile.Timings.RepeatMessageMs,
                WaitBusSleepMs: supplement.Timings.WaitBusSleepMs ?? profile.Timings.WaitBusSleepMs,
                ImmediateCycleMs: supplement.Timings.ImmediateCycleMs ?? profile.Timings.ImmediateCycleMs,
                ImmediateCount: supplement.Timings.ImmediateCount ?? profile.Timings.ImmediateCount);

        var nodes = supplement.Nodes is { Count: > 0 } ? supplement.Nodes.ToArray() : profile.Nodes;

        // 只清除补充输入真正解决的缺口：
        // - "dbc"：补充输入无法提供 DBC，始终保留；
        // - "nm_message_selection"：仅当用户的报文选择器至少命中一条报文才算解决
        //   （选择器填了但一条都没命中时，缺口依然存在）。
        var selectionResolved = selected is not null
            && profile.Messages.Any(m => selected.Contains(m.CanIdHex));
        var missing = profile.MissingFields
            .Where(m => !(m == "nm_message_selection" && selectionResolved))
            .ToArray();

        return profile with
        {
            Source = source,
            Messages = messages,
            Timings = timings,
            Nodes = nodes,
            Layout = applyLayout ? layout : profile.Layout,
            Features = supplement.Features ?? profile.Features,
            IdMapping = supplement.IdMapping ?? profile.IdMapping,
            MissingFields = missing,
        };
    }
}
