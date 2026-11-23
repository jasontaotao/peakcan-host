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
            // spec §7.5.1.2：NMSts 必须是无符号、长度恰好 1 的位。
            // profile 只描述单 bit，能校验的只有 bit ∈ [0,7]；位宽/符号要到 S6
            // 与 DBC 信号合并时才能判。先于通用位范围检查跑，保证报错点名 NMSts。
            if (string.Equals(role.Name, "NmSts", StringComparison.Ordinal)
                && role.Bit is < 0 or > 7)
            {
                throw new NmProfileValidationException(
                    $"NMSts 位的 bit（{role.Bit}）必须在 [0,7]");
            }

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

            // spec §3：U 级条款不可观测，产出 finding 就是编造结论。
            if (rule.Observability == NmObservability.Unobservable
                && !string.IsNullOrWhiteSpace(rule.FindingCode))
            {
                throw new NmProfileValidationException(
                    $"规则 {rule.Id} 标为 unobservable 却声明了 finding code「{rule.FindingCode}」；" +
                    "不可观测条款只能进 Unmodeled");
            }

            // spec §7.5.1.2：nmsts_transition 必须声明 byte 与 bit，
            // 否则原语无法定位状态位，会在运行期静默失效。
            if (string.Equals(rule.When.Kind, "nmsts_transition", StringComparison.Ordinal)
                && (rule.When.Byte is null || rule.When.Bit is null))
            {
                throw new NmProfileValidationException(
                    $"规则 {rule.Id} 的 nmsts_transition 必须声明 when.byte 与 when.bit");
            }

            // cycle_tolerance 的配置必须在加载期就能判：周期显式为 0/负数时算不出容差，
            // expect 也必须是唯一实现的 within_pct（spec §7.2）。否则规则会在运行期
            // 静默进 Unmodeled，用户会把「没检查」误读成「通过」。
            if (string.Equals(rule.When.Kind, "cycle_tolerance", StringComparison.Ordinal))
            {
                if (rule.When.CycleMs is { } cycleMs && cycleMs <= 0)
                {
                    throw new NmProfileValidationException(
                        $"规则 {rule.Id} 的 cycle_tolerance 的 when.cycle_ms 必须为正数，实际 {cycleMs}");
                }

                if (!string.Equals(rule.Expect.Kind, "within_pct", StringComparison.Ordinal))
                {
                    throw new NmProfileValidationException(
                        $"规则 {rule.Id} 的 cycle_tolerance 仅支持 expect.kind=within_pct，" +
                        $"实际为「{rule.Expect.Kind}」");
                }
            }
        }
    }
}
