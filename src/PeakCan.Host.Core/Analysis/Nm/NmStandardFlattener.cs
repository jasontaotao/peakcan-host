namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 三层继承展平（spec §6.1）：项目 &gt; OEM &gt; base，逐字段覆盖。
/// <para>
/// 列表字段按显式 key 合并而非整表替换（位角色按 (byte,bit)，消息按 id，
/// 节点按 name），这样 OEM 只需写差异。继承差异**不记冲突**——冲突只发生在
/// 同一事实的多个独立来源（DBC/sidecar/project）之间。
/// </para>
/// </summary>
public static class NmStandardFlattener
{
    /// <summary>展平结果：profile + 来源链（用于报告追溯）。</summary>
    public sealed record Flattened(
        NmStandardTimings? Timings,
        NmStandardLayout? Layout,
        NmStandardFeatures? Features,
        IReadOnlyList<NmRuleDef>? Rules,
        IReadOnlyList<NmStandardMessage>? Messages,
        IReadOnlyList<NmStandardNode>? Nodes,
        string Provenance)
    {
        /// <summary>
        /// 整条继承链的加载期提示（spec §5.3），按链序合并去重。
        /// <para>
        /// 必须在展平层汇总：加载期提示是**逐文件**产生的，而报告只拿得到选中的那一层
        /// （<see cref="NmStandardProfileLocator.Resolved.Selected"/>）。父层用旧键时
        /// 只读 Selected 会一条提示都没有。
        /// </para>
        /// </summary>
        public IReadOnlyList<string> LoadAssumptions { get; init; } = Array.Empty<string>();
    }

    public static Flattened Flatten(
        NmStandardProfile profile,
        IReadOnlyDictionary<string, NmStandardProfile> registry)
    {
        var chain = ResolveChain(profile, registry);

        // 从最底层（base）往上覆盖：父值先铺，子值后盖。
        NmStandardTimings? timings = null;
        NmStandardLayout? layout = null;
        NmStandardFeatures? features = null;
        var rules = new Dictionary<string, NmRuleDef>(StringComparer.Ordinal);
        // 合并键必须含扩展位：标准帧 0x400 与扩展帧 0x400 是两条不同报文（spec §6.1）。
        var messages = new Dictionary<(string Id, bool Extended), NmStandardMessage>();
        var nodes = new Dictionary<string, NmStandardNode>(StringComparer.Ordinal);

        foreach (var layer in chain)
        {
            timings = MergeTimings(timings, layer.Timings);
            layout = MergeLayout(layout, layer.Layout);
            features = MergeFeatures(features, layer.Features);

            foreach (var rule in layer.Rules ?? Array.Empty<NmRuleDef>())
                rules[rule.Id] = rule;
            foreach (var msg in layer.Messages ?? Array.Empty<NmStandardMessage>())
                messages[(msg.Id, msg.Extended)] = msg;
            foreach (var node in layer.Nodes ?? Array.Empty<NmStandardNode>())
                nodes[node.Name] = node;

            // 三态 patch 的 remove 半边：显式删除父级项（spec §6.2）。
            ApplyRemove(layer.Overrides?.Remove, layout, rules, messages, nodes, ref layout);
        }

        // 排序保证确定性（spec §10.1.1 canonicalization 要求按业务 key 排序）。
        return new Flattened(
            timings, layout, features,
            rules.Count == 0 ? null : rules.Values.OrderBy(r => r.Id, StringComparer.Ordinal).ToArray(),
            messages.Count == 0
                ? null
                : messages.Values.OrderBy(m => m.Id, StringComparer.Ordinal).ThenBy(m => m.Extended).ToArray(),
            nodes.Count == 0 ? null : nodes.Values.OrderBy(n => n.Name, StringComparer.Ordinal).ToArray(),
            string.Join(" < ", chain.Select(l => $"{l.StandardId}@{l.Version}")))
        {
            // 按链序（base → child）拼接并去重：同一句提示在多层重复时只留一份，
            // 否则报告会被同一行刷屏，读者学会忽略它。
            LoadAssumptions = chain
                .SelectMany(l => l.LoadAssumptions)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
        };
    }

    /// <summary>应用 remove 列表。格式：<c>bit_roles[byte.bit]</c> / <c>rules[id]</c> / <c>messages[id]</c> / <c>nodes[name]</c>。</summary>
    private static void ApplyRemove(
        IReadOnlyList<string>? remove,
        NmStandardLayout? currentLayout,
        Dictionary<string, NmRuleDef> rules,
        Dictionary<(string, bool), NmStandardMessage> messages,
        Dictionary<string, NmStandardNode> nodes,
        ref NmStandardLayout? layoutOut)
    {
        if (remove is not { Count: > 0 }) return;

        var layout = layoutOut ?? currentLayout;
        List<NmBitRole>? roles = layout?.BitRoles?.ToList();

        foreach (var entry in remove)
        {
            // 空项不得静默跳过：用户写了 remove 却什么都没删，报告毫无迹象。
            // 与「指向不存在的目标必须报错」同一语义。
            if (string.IsNullOrWhiteSpace(entry))
                throw new NmProfileValidationException(
                    "remove 列表含空项，应为 <字段>[<键>]（如 rules[NM_CYCLE]）");

            var bracket = entry.IndexOf('[', StringComparison.Ordinal);
            if (bracket < 0 || !entry.EndsWith(']'))
                throw new NmProfileValidationException(
                    $"remove 项格式非法「{entry}」，应为 <字段>[<键>]");

            var field = entry[..bracket].Trim();
            var key = entry[(bracket + 1)..^1].Trim();

            switch (field)
            {
                case "bit_roles":
                    // 与 rules/messages/nodes 同语义：指向不存在的目标必须报错，
                    // 不能静默无效（spec §19.4 #14 的同一缺陷在四个分支上是同一种）。
                    if (roles is null)
                        throw new NmProfileValidationException(
                            $"remove 项「{entry}」指向位角色，但继承链上没有声明任何位角色");
                    var parts = key.Split('.');
                    if (parts.Length != 2
                        || !byte.TryParse(parts[0], out var rb)
                        || !int.TryParse(parts[1], out var rbit))
                    {
                        throw new NmProfileValidationException(
                            $"bit_roles remove 键格式非法「{key}」，应为 byte.bit（如 1.6）");
                    }
                    if (roles.RemoveAll(r => r.Byte == rb && r.Bit == rbit) == 0)
                        throw new NmProfileValidationException(
                            $"remove 项「{entry}」指向不存在的位角色 byte{rb}.bit{rbit}");
                    break;

                case "rules":
                    // spec §19.4 #14：key 不存在时静默无效。规则改名后旧的
                    // remove: rules[NM_06] 会变成 no-op——profile 看起来删了规则，
                    // 实际还在跑，且报告没有任何迹象。必须报错。
                    if (!rules.Remove(key))
                        throw new NmProfileValidationException(
                            $"remove 项「{entry}」指向不存在的规则「{key}」；" +
                            "规则改名后旧的 remove 会静默失效，请同步更新");
                    break;

                case "messages":
                    // 不区分扩展位：remove 一个 ID 即删除该 ID 的全部格式。
                    var matchedMessages = messages.Keys.Where(k => k.Item1 == key).ToList();
                    if (matchedMessages.Count == 0)
                        throw new NmProfileValidationException(
                            $"remove 项「{entry}」指向不存在的报文「{key}」");
                    foreach (var k in matchedMessages)
                        messages.Remove(k);
                    break;

                case "nodes":
                    if (!nodes.Remove(key))
                        throw new NmProfileValidationException(
                            $"remove 项「{entry}」指向不存在的节点「{key}」");
                    break;

                default:
                    throw new NmProfileValidationException(
                        $"remove 不支持字段「{field}」，合法值：bit_roles | rules | messages | nodes");
            }
        }

        if (layout is not null && roles is not null)
            layoutOut = layout with { BitRoles = roles.Count == 0 ? null : roles.ToArray() };
    }

    /// <summary>
    /// 解析 extends 链，返回 base → child 顺序。循环或缺失父级抛异常。
    /// <para>
    /// 判环用 <c>id@version</c> 而不是裸 <c>StandardId</c>：同一个 standard 的
    /// 新版本继承旧版本（<c>geely-cv@2.0 extends geely-cv@1.5</c>）是合法的，
    /// 用裸 id 会误判为循环。registry 的 key 语义固定为 <c>id@version</c>。
    /// </para>
    /// </summary>
    private static List<NmStandardProfile> ResolveChain(
        NmStandardProfile profile,
        IReadOnlyDictionary<string, NmStandardProfile> registry)
    {
        var chain = new List<NmStandardProfile>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = profile;

        while (true)
        {
            var key = KeyOf(current);
            if (!visited.Add(key))
            {
                throw new NmProfileValidationException(
                    $"继承链存在循环：{string.Join(" -> ", chain.Select(KeyOf))} -> {key}");
            }

            chain.Add(current);

            if (string.IsNullOrWhiteSpace(current.Extends)) break;

            if (!registry.TryGetValue(current.Extends, out var parent))
            {
                throw new NmProfileValidationException(
                    $"继承链断裂：{key} 的 extends「{current.Extends}」未找到");
            }

            current = parent;
        }

        // 反转成 base → child，覆盖时父先子后。
        chain.Reverse();
        return chain;
    }

    private static string KeyOf(NmStandardProfile p) => $"{p.StandardId}@{p.Version}";

    private static NmStandardTimings? MergeTimings(NmStandardTimings? parent, NmStandardTimings? child)
    {
        if (child is null) return parent;
        if (parent is null) return child;

        // 逐字段：子值非空则覆盖，否则继承父值。
        return new NmStandardTimings
        {
            CycleMs = child.CycleMs ?? parent.CycleMs,
            TimeoutMs = child.TimeoutMs ?? parent.TimeoutMs,
            WaitBusSleepMs = child.WaitBusSleepMs ?? parent.WaitBusSleepMs,
            RepeatMessageWithBurstMs = child.RepeatMessageWithBurstMs ?? parent.RepeatMessageWithBurstMs,
            RepeatMessageWithoutBurstMs = child.RepeatMessageWithoutBurstMs ?? parent.RepeatMessageWithoutBurstMs,
            ImmediateCycleMs = child.ImmediateCycleMs ?? parent.ImmediateCycleMs,
            ImmediateCount = child.ImmediateCount ?? parent.ImmediateCount,
            StartNmTxMs = child.StartNmTxMs ?? parent.StartNmTxMs,
            WakeUpMs = child.WakeUpMs ?? parent.WakeUpMs,
            ToleranceRatio = child.ToleranceRatio ?? parent.ToleranceRatio,
            ToleranceAbsMs = child.ToleranceAbsMs ?? parent.ToleranceAbsMs,
        };
    }

    private static NmStandardLayout? MergeLayout(NmStandardLayout? parent, NmStandardLayout? child)
    {
        if (child is null) return parent;
        if (parent is null) return child;

        // 位角色按 (byte,bit) 合并：同一位子覆盖父，不同位两者都保留。
        // 这允许 OEM 只声明自己关心的位（如 AWB），同时继承 base 的 PNI。
        IReadOnlyList<NmBitRole>? roles = null;
        var parentRoles = parent.BitRoles;
        var childRoles = child.BitRoles;
        if (parentRoles is not null || childRoles is not null)
        {
            var merged = new Dictionary<(byte, int), NmBitRole>();
            foreach (var role in parentRoles ?? Array.Empty<NmBitRole>()) merged[(role.Byte, role.Bit)] = role;
            foreach (var role in childRoles ?? Array.Empty<NmBitRole>()) merged[(role.Byte, role.Bit)] = role;
            roles = merged.Values.OrderBy(r => r.Byte).ThenBy(r => r.Bit).ToArray();
        }

        return new NmStandardLayout
        {
            NidByte = child.NidByte ?? parent.NidByte,
            CbvByte = child.CbvByte ?? parent.CbvByte,
            UserDataOffset = child.UserDataOffset ?? parent.UserDataOffset,
            UserDataLength = child.UserDataLength ?? parent.UserDataLength,
            BitRoles = roles,
        };
    }

    private static NmStandardFeatures? MergeFeatures(NmStandardFeatures? parent, NmStandardFeatures? child)
    {
        if (child is null) return parent;
        if (parent is null) return child;

        return new NmStandardFeatures
        {
            ActiveWakeupBitEnabled = child.ActiveWakeupBitEnabled ?? parent.ActiveWakeupBitEnabled,
            RepeatMsgIndEnabled = child.RepeatMsgIndEnabled ?? parent.RepeatMsgIndEnabled,
            PnEnabled = child.PnEnabled ?? parent.PnEnabled,
        };
    }
}
