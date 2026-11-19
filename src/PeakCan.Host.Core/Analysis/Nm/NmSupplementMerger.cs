namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 逐字段合并用户补充输入。用户显式值优先于 DBC；空值不覆盖。
/// </summary>
public static class NmSupplementMerger
{
    public static NmProfile Merge(NmProfile profile, NmUserSupplement? supplement)
    {
        if (supplement is null) return profile;

        var source = profile.Source | NmProfileSource.Sidecar;

        var selected = supplement.NmMessageSelectors is { Count: > 0 } ids
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
            Layout = layout,
            Features = supplement.Features ?? profile.Features,
            IdMapping = supplement.IdMapping ?? profile.IdMapping,
            MissingFields = missing,
        };
    }
}
