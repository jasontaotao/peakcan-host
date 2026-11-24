using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// <c>*.nmstd.yaml</c> 加载器（spec §5.4）。严格反序列化：未知字段、缺必填、
/// 语法错误一律抛 <see cref="NmProfileValidationException"/>，不静默忽略。
/// <para>
/// YAML 结构是 <c>standard:</c> + <c>overrides:</c> 两段，与 <see cref="NmStandardProfile"/>
/// 的扁平字段不对应，因此用显式 DTO 承接再映射。
/// </para>
/// </summary>
public static class NmStandardYaml
{
    private static readonly IDeserializer s_deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        // 拼错的字段必须报错：否则 profile 写了等于没写，且没有任何提示。
        .WithDuplicateKeyChecking()
        .Build();

    /// <summary>从 YAML 文本加载。失败抛 <see cref="NmProfileValidationException"/>。</summary>
    public static NmStandardProfile Load(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            throw new NmProfileValidationException("profile 内容为空");

        NmStandardYamlDto dto;
        try
        {
            dto = s_deserializer.Deserialize<NmStandardYamlDto>(yaml)
                  ?? throw new NmProfileValidationException("profile 反序列化结果为空");
        }
        catch (YamlException ex)
        {
            throw new NmProfileValidationException(
                $"YAML 解析失败（第 {ex.Start.Line} 行）：{ex.Message}", ex);
        }
        catch (NmProfileValidationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new NmProfileValidationException($"profile 反序列化失败：{ex.Message}", ex);
        }

        var profile = Map(dto);
        // 加载即校验：坏 profile 不允许离开这个入口（spec §8「加载/保存时校验」）。
        NmStandardValidator.Validate(profile);
        return profile;
    }

    /// <summary>从文件加载。IO 异常向上抛，格式/校验失败包成 <see cref="NmProfileValidationException"/>。</summary>
    public static NmStandardProfile LoadFile(string path)
        => Load(File.ReadAllText(path));

    /// <summary>
    /// 展平后校验：单层校验发现不了「base timeout=2000 + child cycle=3000」这类
    /// 组合非法，必须在展平结果上再校验一次（spec §8）。
    /// </summary>
    public static NmStandardFlattener.Flattened LoadAndFlatten(
        string yaml, IReadOnlyDictionary<string, NmStandardProfile> registry)
    {
        var profile = Load(yaml);
        var flat = NmStandardFlattener.Flatten(profile, registry);
        NmStandardValidator.ValidateFlattened(flat);
        return flat;
    }

    private static NmStandardProfile Map(NmStandardYamlDto dto)
    {
        if (dto.Standard is null)
            throw new NmProfileValidationException("缺少必填段：standard");

        var std = dto.Standard;
        if (string.IsNullOrWhiteSpace(std.Id))
            throw new NmProfileValidationException("standard.id 必填");

        var ov = dto.Overrides;
        return new NmStandardProfile
        {
            StandardId = std.Id,
            Vendor = std.Vendor,
            Version = VersionOf(std),
            // Trim：`extends: " a@1.0 "` 的空格会让注册表按 id@version 查找失败，
            // 整条继承链断裂（报错文案里还会把空格一起打出来，更难发现）。
            // 在**加载期**归一化，而不是在每个消费点各 Trim 一次——
            // Flatten、Locator、SelectLeaf 三处都要用这个值，漏一处就是新的不一致。
            Extends = string.IsNullOrWhiteSpace(std.Extends) ? null : std.Extends.Trim(),
            Source = std.Source is null
                ? null
                : new NmStandardSource(std.Source.Doc, std.Source.Revision),
            Overrides = ov is null ? null : new NmStandardOverrides
            {
                Timings = ov.Timings,
                Layout = MapLayout(ov.Layout),
                Features = ov.Features,
                Remove = ov.Remove?.ToArray(),
            },
            Messages = MapList(dto.Messages, "messages", m =>
                new NmStandardMessage(
                    RequireNonEmpty(m.Name, "messages[].name")!,
                    RequireNonEmpty(m.Id, "messages[].id")!,
                    m.Extended, m.Sender, m.Dlc)),
            Nodes = MapList(dto.Nodes, "nodes", n =>
                new NmStandardNode(
                    RequireNonEmpty(n.Name, "nodes[].name")!, n.StationAddress)),
            Rules = MapList(dto.Rules, "rules", MapRule),
            LoadAssumptions = CollectLoadAssumptions(dto, $"{std.Id}@{VersionOf(std)}"),
            UncoveredClauses = MapUncoveredClauses(dto.UncoveredClauses),
        };
    }

    /// <summary>
    /// 解析「已知未被检查的条款/半边」（spec §9.5）。
    /// <para>
    /// <b>为什么要有这一段</b>：<c>NM_BURST_SHAPE</c> 的 <c>clauses</c> 含 NM_03，
    /// 但它只验快发形态，AWB 半边从未被检查（<c>bit_rule</c> 只支持 <c>is_zero</c>）。
    /// 覆盖表会据此把 NM_03 整条报「已覆盖」——这正是 §9.5 要消灭的静默通过。
    /// 声明让 profile 自己说出「这半边我没验」，而不是让读者去猜。
    /// </para>
    /// <para>
    /// <b>条款号形状与规则的 <c>clauses</c> 同口径</b>：两者按字符串相等聚合，
    /// 形状不一致就永远对不上，声明写了等于没写。
    /// </para>
    /// </summary>
    private static IReadOnlyList<NmUncoveredClauseDef>? MapUncoveredClauses(
        List<UncoveredClauseDto>? dtos)
    {
        if (dtos is not { Count: > 0 }) return null;

        var result = new List<NmUncoveredClauseDef>(dtos.Count);
        foreach (var dto in dtos)
        {
            // 空列表项（YAML 里写 `-`）会反序列化成 null 元素。与相邻 MapList
            // 同约定：抛校验异常，不让 NRE 逃逸出去。
            if (dto is null)
            {
                throw new NmProfileValidationException(
                    "uncovered_clauses 含空项；每项至少要写 clause 与 reason_code");
            }

            var clause = RequireNonEmpty(dto.Clause, "uncovered_clauses[].clause")!;
            if (!NmStandardValidator.IsClauseIdShape(clause))
            {
                throw new NmProfileValidationException(
                    $"uncovered_clauses 的条款号「{clause}」不是合法条款号。" +
                    "合法形状：NM_01 / §5.4.2 / 附录A；" +
                    "「哪一半没验」用 facet 表达，不要拼进条款号（否则与规则的 clauses 对不上）");
            }

            // 与规则的 clauses 同口径：形状合法 ≠ 规范里存在。
            // NM_30 能过形状正则，但吉利只有 NM_01–NM_17——只查形状的话，
            // 覆盖表会多出一行「NM_30 未覆盖」，报一个规范里不存在的条款。
            if (NmClauseRegistry.Validate([clause]) is { } clauseError)
            {
                throw new NmProfileValidationException($"uncovered_clauses 的{clauseError}");
            }

            var reason = RequireNonEmpty(dto.ReasonCode, "uncovered_clauses[].reason_code")!;
            // 用 StaticGap 而不是 All：三个运行时码写进静态声明会让 AI 给出
            // 错误的修复指引（见 NmUnmodeledCodes.StaticGap 的注释）。
            if (!NmUnmodeledCodes.IsStaticGap(reason))
            {
                throw new NmProfileValidationException(
                    $"uncovered_clauses 的原因码「{reason}」不是合法的静态缺口码。合法值：" +
                    string.Join(" | ", NmUnmodeledCodes.StaticGap));
            }

            var facet = string.IsNullOrWhiteSpace(dto.Facet) ? null : dto.Facet.Trim();
            result.Add(new NmUncoveredClauseDef(clause, facet, reason));
        }
        return result;
    }

    /// <summary>版本缺省值。提示里的层标识与 profile 实际版本必须同源，不能各写一份。</summary>
    private static string VersionOf(StandardDto std)
        => string.IsNullOrWhiteSpace(std.Version) ? "1.0" : std.Version;

    /// <summary>
    /// 加载期提示汇总（spec §5.3）。目前只有一条：规则用了旧键 <c>phase</c>。
    /// <para>
    /// 按「是否有规则用了旧键」判定，而不是按规则条数逐条产出——同一条提示重复
    /// 十几遍会让读者学会忽略它，真正需要看到的那一条也就失效了。
    /// </para>
    /// <para>
    /// <b>必须带层标识</b>：展平会把整条链的提示按整句去重。不带层名时，
    /// 「base 有 1 条用旧键」与「OEM 也有 1 条」生成逐字节相同的句子，去重后
    /// 只剩一条——报告说「有 1 条」，实际链上 2 条，用户按提示只改一处。
    /// 计数不同时反而两条都出现，行为不对称。
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> CollectLoadAssumptions(NmStandardYamlDto dto, string layerKey)
    {
        var legacy = dto.Rules?.Count(r => r.DeclaredPhase is null && r.Phase is not null) ?? 0;
        if (legacy == 0) return Array.Empty<string>();

        return new[]
        {
            $"{layerKey} 有 {legacy} 条规则使用了旧键 phase：该键已改名 declared_phase，" +
            "且它只是声明性元数据，不驱动求值作用域（引擎对全部规则在全窗口求值）。",
        };
    }

    /// <summary>
    /// 未知取值必须报错：拼错的 <c>unobservale</c> 若静默降级成 Observed，
    /// 不可观测条款就可能产出 finding（spec §3 禁止）。
    /// </summary>
    private static NmObservability ParseObservability(string? value)
    {
        if (value is null) return NmObservability.Observed;
        return value.ToLowerInvariant() switch
        {
            "observed" => NmObservability.Observed,
            "partial" => NmObservability.Partial,
            "unobservable" => NmObservability.Unobservable,
            _ => throw new NmProfileValidationException(
                $"未知 observability 取值「{value}」，合法值：observed | partial | unobservable"),
        };
    }

    private static NmStandardLayout? MapLayout(LayoutDto? dto)
    {
        if (dto is null) return null;

        IReadOnlyList<NmBitRole>? roles = dto.BitRoles?
            .Select(r => new NmBitRole(r.Byte, r.Bit, r.Name)
            {
                Meaning = r.Meaning,
                Constraint = ParseConstraint(r.Constraint),
            })
            .ToArray();

        return new NmStandardLayout
        {
            NidByte = dto.NidByte,
            CbvByte = dto.CbvByte,
            UserDataOffset = dto.UserDataOffset,
            UserDataLength = dto.UserDataLength,
            BitRoles = roles,
        };
    }

    /// <summary>
    /// 未知取值必须报错：拼错的 <c>must_be_zer0</c> 若静默降级成 None，
    /// 预留位检查会失效并漏报（spec §5.4.3）。
    /// </summary>
    /// <summary>
    /// 列表映射统一入口：null 元素不能穿透成 NullReferenceException
    /// （调用方按 <see cref="NmProfileValidationException"/> catch 会漏接）。
    /// </summary>
    private static IReadOnlyList<T>? MapList<TSrc, T>(
        List<TSrc>? source, string field, Func<TSrc, T> map) where TSrc : class
    {
        if (source is null) return null;

        var result = new List<T>(source.Count);
        for (var i = 0; i < source.Count; i++)
        {
            var item = source[i]
                ?? throw new NmProfileValidationException($"{field}[{i}] 不能为空");
            result.Add(map(item));
        }
        return result;
    }

    [return: System.Diagnostics.CodeAnalysis.NotNull]
    private static string? RequireNonEmpty(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new NmProfileValidationException($"{field} 必填");
        return value;
    }

    private static NmBitConstraint ParseConstraint(string? value)
    {
        if (value is null) return NmBitConstraint.None;
        return value.ToLowerInvariant() switch
        {
            "none" => NmBitConstraint.None,
            "must_be_zero" => NmBitConstraint.MustBeZero,
            "must_be_zero_while_disabled" => NmBitConstraint.MustBeZeroWhileDisabled,
            _ => throw new NmProfileValidationException(
                $"未知 bit constraint 取值「{value}」，合法值：none | must_be_zero | must_be_zero_while_disabled"),
        };
    }

    // ==== YAML DTO（严格匹配文档结构，字段名用 UnderscoredNamingConvention）====

    private sealed class NmStandardYamlDto
    {
        public StandardDto? Standard { get; set; }
        public OverridesDto? Overrides { get; set; }
        public List<MessageDto>? Messages { get; set; }
        public List<NodeDto>? Nodes { get; set; }
        public List<RuleDto>? Rules { get; set; }
        public List<UncoveredClauseDto>? UncoveredClauses { get; set; }
    }

    /// <summary>spec §9.5：声明「这条条款有一部分已知没被检查」。</summary>
    private sealed class UncoveredClauseDto
    {
        public string? Clause { get; set; }
        public string? Facet { get; set; }
        public string? ReasonCode { get; set; }
    }

    private sealed class StandardDto
    {
        public string? Id { get; set; }
        public string? Vendor { get; set; }
        public string? Version { get; set; }
        public string? Extends { get; set; }
        public SourceDto? Source { get; set; }
    }

    /// <summary>
    /// <see cref="NmStandardSource"/> 是带位置参数的 record，YamlDotNet 无法直接构造
    /// （没有无参构造器），因此用可变类承接再映射。
    /// </summary>
    private sealed class SourceDto
    {
        public string? Doc { get; set; }
        public string? Revision { get; set; }
    }

    private sealed class OverridesDto
    {
        public NmStandardTimings? Timings { get; set; }
        public LayoutDto? Layout { get; set; }
        public NmStandardFeatures? Features { get; set; }
        /// <summary>显式删除列表（spec §6.2 三态 patch 的 remove 半边）。</summary>
        public List<string>? Remove { get; set; }
    }

    /// <summary>
    /// 布局 DTO：<see cref="NmBitRole"/> 是带位置参数的 record，YamlDotNet
    /// 无法直接构造到 <c>IReadOnlyList</c>，因此用可变类承接再映射。
    /// </summary>
    private sealed class LayoutDto
    {
        public byte? NidByte { get; set; }
        public byte? CbvByte { get; set; }
        public byte? UserDataOffset { get; set; }
        public int? UserDataLength { get; set; }
        public List<BitRoleDto>? BitRoles { get; set; }
    }

    private sealed class BitRoleDto
    {
        public byte Byte { get; set; }
        public int Bit { get; set; }
        public string Name { get; set; } = "";
        public string? Meaning { get; set; }
        // YAML 里是 snake_case 字符串（must_be_zero_while_disabled），
        // 不能靠默认枚举转换（会按名字匹配失败）。
        public string? Constraint { get; set; }
    }

    private sealed class MessageDto
    {
        public string? Name { get; set; }
        public string? Id { get; set; }
        public bool Extended { get; set; }
        public string? Sender { get; set; }
        public byte Dlc { get; set; } = 8;
    }

    private sealed class NodeDto
    {
        public string? Name { get; set; }
        public byte? StationAddress { get; set; }
    }

    private sealed class RuleDto
    {
        public string? Id { get; set; }
        public string? Requirement { get; set; }
        public string? DeclaredPhase { get; set; }
        // 兼容旧键：spec §5.3 裁决 phase → declared_phase。
        public string? Phase { get; set; }
        public List<string>? Clauses { get; set; }
        public RuleWhenDto? When { get; set; }
        public RuleExpectDto? Expect { get; set; }
        public string? Finding { get; set; }
        public string? SourceRef { get; set; }
        public string? Observability { get; set; }
    }

    private sealed class RuleWhenDto
    {
        public string? Kind { get; set; }
        public byte? Byte { get; set; }
        public int? Bit { get; set; }
        public int? Length { get; set; }
        public string? Role { get; set; }
        public string? LatencyKind { get; set; }
        public int? MinSilenceMs { get; set; }
        public int? CycleMs { get; set; }
        public double? Pct { get; set; }
        public int? MinFrames { get; set; }
        public int? TimeoutMs { get; set; }
        public string? MinId { get; set; }
        public string? MaxId { get; set; }
        public bool? Extended { get; set; }
    }

    private sealed class RuleExpectDto
    {
        public string? Kind { get; set; }
        public string? Value { get; set; }
        public byte? Byte { get; set; }
        public int? Bit { get; set; }
        public int? Count { get; set; }
        public int? IntervalMs { get; set; }
        public double? Pct { get; set; }
        public int? MinMs { get; set; }
        public int? MaxMs { get; set; }
        public string? Anchor { get; set; }
        public bool? WithBurst { get; set; }
        /// <summary>normal_resume_shape 的目标 NMSts（0=RMS / 1=NOS，spec §4）。</summary>
        public int? ExpectNmsts { get; set; }
    }

    private static NmRuleDef MapRule(RuleDto r)
    {
        RequireNonEmpty(r.Id, "rules[].id");
        RequireNonEmpty(r.Requirement, $"规则 {r.Id} 的 requirement（条款可追溯性）");
        // 缺 when/expect 的规则无法执行，也不能被当成「未知原语」静默进 Unmodeled。
        RequireNonEmpty(r.When?.Kind, $"规则 {r.Id} 的 when.kind");
        RequireNonEmpty(r.Expect?.Kind, $"规则 {r.Id} 的 expect.kind");

        var w = r.When!;
        var e = r.Expect!;

        // 闭集校验：未知原语在加载期报错，不静默进 Unmodeled（spec §7.2）。
        // 刻意排在 finding 检查之前：同一规则同时拼错原语又缺 finding 时，
        // 先报闭集错误，用户补完一次即可；反序会让用户修两轮。
        if (!NmRulePrimitives.WhenKinds.Contains(w.Kind!))
        {
            throw new NmProfileValidationException(
                $"规则 {r.Id} 的 when.kind「{w.Kind}」不在闭集内，合法值：" +
                string.Join(" | ", NmRulePrimitives.WhenKinds.Order(StringComparer.Ordinal)));
        }
        if (!NmRulePrimitives.ExpectKinds.Contains(e.Kind!))
        {
            throw new NmProfileValidationException(
                $"规则 {r.Id} 的 expect.kind「{e.Kind}」不在闭集内，合法值：" +
                string.Join(" | ", NmRulePrimitives.ExpectKinds.Order(StringComparer.Ordinal)));
        }

        // observe 规则按定义不产 violation，finding 可省略或为空串——
        // 逼它编一个 finding code 只会让引擎产出必须再抑制的假告警。
        var isObserve = string.Equals(e.Kind, "observe", StringComparison.Ordinal);
        var finding = isObserve && string.IsNullOrWhiteSpace(r.Finding)
            ? string.Empty
            : RequireNonEmpty(r.Finding, $"规则 {r.Id} 的 finding code")!;

        // 两键并存时旧键的值会被静默丢弃——加载器在其它地方（未知字段、拼错取值）
        // 一律报错，这里不能例外。用户以为改的是 phase，实际生效的是 declared_phase。
        if (r.DeclaredPhase is not null && r.Phase is not null)
        {
            throw new NmProfileValidationException(
                $"规则 {r.Id} 同时声明了 declared_phase 与旧键 phase；" +
                "旧键的值会被忽略，请只保留 declared_phase");
        }

        // 旧键 phase 仍接受，但语义是「声明性元数据」。
        // 提示由 Map 汇总产出（见 CollectLoadAssumptions），不在这里逐条抛。
        var declaredPhase = r.DeclaredPhase ?? r.Phase ?? "normal";

        return new NmRuleDef(
            r.Id!, r.Requirement!, declaredPhase,
            new NmRuleWhen(w.Kind!, w.Byte, w.Bit, w.Length, w.Role, w.LatencyKind,
                w.MinSilenceMs, w.CycleMs, w.Pct, w.MinFrames, w.TimeoutMs,
                w.MinId, w.MaxId, w.Extended),
            new NmRuleExpect(e.Kind!, e.Value, e.Byte, e.Bit, e.Count, e.IntervalMs,
                e.Pct, e.MinMs, e.MaxMs, e.Anchor, e.WithBurst, e.ExpectNmsts),
            finding)
        {
            SourceRef = r.SourceRef,
            Observability = ParseObservability(r.Observability),
            Clauses = r.Clauses ?? [],
        };
    }
}
