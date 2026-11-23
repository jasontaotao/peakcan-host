namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 企标 profile 校验器（spec §8）。失败抛 <see cref="NmProfileValidationException"/>，
/// 不允许带错误的 profile 进入分析。
/// </summary>
public static class NmStandardValidator
{
    public static void Validate(NmStandardProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.StandardId))
            throw new NmProfileValidationException("standard.id 不能为空");

        ValidateTimings(profile.Timings);
        ValidateLayout(profile.Layout);
        ValidateRules(profile.Rules);
        ValidateMessages(profile.Messages);
    }

    /// <summary>
    /// 展平后校验（spec §8）。单层校验发现不了跨层组合非法
    /// （base timeout=2000 + child cycle=3000），必须在展平结果上再校验。
    /// </summary>
    public static void ValidateFlattened(NmStandardFlattener.Flattened flat)
    {
        ValidateTimings(flat.Timings);
        ValidateLayout(flat.Layout);
        ValidateRules(flat.Rules);
        ValidateMessages(flat.Messages);
    }

    private static void ValidateMessages(IReadOnlyList<NmStandardMessage>? messages)
    {
        if (messages is null) return;

        foreach (var message in messages)
        {
            if (message.Dlc is 0 or > 64)
                throw new NmProfileValidationException(
                    $"报文 {message.Name} 的 DLC（{message.Dlc}）必须在 1–64");

            // ID 必须是十六进制字符串（可带 0x 前缀），否则后续无法解析成 CAN ID。
            var id = message.Id.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? message.Id[2..]
                : message.Id;
            if (!uint.TryParse(id, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                throw new NmProfileValidationException(
                    $"报文 {message.Name} 的 id「{message.Id}」不是合法十六进制 CAN ID");
            }
        }
    }

    private static void ValidateTimings(NmStandardTimings? timings)
    {
        if (timings is null) return;

        // timeout 必须大于 cycle：否则每个正常周期都会被判成超时。
        if (timings is { CycleMs: { } cycle, TimeoutMs: { } timeout } && timeout <= cycle)
        {
            throw new NmProfileValidationException(
                $"timing.timeout_ms（{timeout}）必须大于 cycle_ms（{cycle}）");
        }

        // RMS 观察窗不能短于一个发送周期，否则正常发送都会被判成「RMS 已结束」。
        if (timings is { CycleMs: { } c2 } && timings.RepeatMessageWithBurstMs is { } withBurst
            && withBurst < c2)
        {
            throw new NmProfileValidationException(
                $"timing.repeat_message_with_burst_ms（{withBurst}）必须 ≥ cycle_ms（{c2}）");
        }
        if (timings is { CycleMs: { } c3 } && timings.RepeatMessageWithoutBurstMs is { } withoutBurst
            && withoutBurst < c3)
        {
            throw new NmProfileValidationException(
                $"timing.repeat_message_without_burst_ms（{withoutBurst}）必须 ≥ cycle_ms（{c3}）");
        }

        foreach (var (name, value) in new (string, int?)[]
        {
            ("cycle_ms", timings.CycleMs),
            ("timeout_ms", timings.TimeoutMs),
            ("wait_bus_sleep_ms", timings.WaitBusSleepMs),
            ("repeat_message_with_burst_ms", timings.RepeatMessageWithBurstMs),
            ("repeat_message_without_burst_ms", timings.RepeatMessageWithoutBurstMs),
            ("immediate_cycle_ms", timings.ImmediateCycleMs),
            ("immediate_count", timings.ImmediateCount),
            ("start_nm_tx_ms", timings.StartNmTxMs),
            ("wakeup_ms", timings.WakeUpMs),
        })
        {
            if (value is <= 0)
                throw new NmProfileValidationException($"timing.{name} 必须为正数，实际 {value}");
        }

        if (timings.ToleranceRatio is { } ratio && (ratio < 0 || ratio > 1))
        {
            throw new NmProfileValidationException(
                $"timing.tolerance_ratio 必须在 [0,1]，实际 {ratio}");
        }
    }

    private static void ValidateLayout(NmStandardLayout? layout)
    {
        if (layout is null) return;

        if (layout is { NidByte: { } nid, CbvByte: { } cbv } && nid == cbv)
        {
            throw new NmProfileValidationException(
                $"layout.nid_byte 与 cbv_byte 重叠（都是 byte {nid}）");
        }

        if (layout is { UserDataOffset: { } offset, UserDataLength: { } length } && length < 0)
        {
            throw new NmProfileValidationException(
                $"layout.user_data_length 不能为负，实际 {length}（offset={offset}）");
        }

        if (layout.BitRoles is not { } roles) return;

        var seen = new HashSet<(byte, int)>();
        foreach (var role in roles)
        {
            if (role.Bit is < 0 or > 7)
            {
                throw new NmProfileValidationException(
                    $"位角色 {role.Name} 的 bit（{role.Bit}）必须在 [0,7]");
            }

            if (!seen.Add((role.Byte, role.Bit)))
            {
                throw new NmProfileValidationException(
                    $"位角色重复声明：byte{role.Byte}.bit{role.Bit}（{role.Name}）");
            }
        }
    }

    private static void ValidateRules(IReadOnlyList<NmRuleDef>? rules)
    {
        if (rules is null) return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id))
                throw new NmProfileValidationException("rules[].id 不能为空");

            if (!seen.Add(rule.Id))
                throw new NmProfileValidationException($"规则 ID 重复：{rule.Id}");

            // 条款可追溯性是企标报告的硬要求（spec §6.5.6）。
            if (string.IsNullOrWhiteSpace(rule.Requirement))
                throw new NmProfileValidationException($"规则 {rule.Id} 缺少 requirement");
        }
    }
}
