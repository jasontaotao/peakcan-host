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
            Version = string.IsNullOrWhiteSpace(std.Version) ? "1.0" : std.Version,
            Extends = std.Extends,
            Source = std.Source,
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
    }

    private sealed class StandardDto
    {
        public string? Id { get; set; }
        public string? Vendor { get; set; }
        public string? Version { get; set; }
        public string? Extends { get; set; }
        public NmStandardSource? Source { get; set; }
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
        public string? Phase { get; set; }
        public RuleClauseDto? When { get; set; }
        public RuleClauseDto? Expect { get; set; }
        public string? Finding { get; set; }
        public string? SourceRef { get; set; }
        public string? Observability { get; set; }
    }

    private static NmRuleDef MapRule(RuleDto r)
    {
        RequireNonEmpty(r.Id, "rules[].id");
        RequireNonEmpty(r.Requirement, $"规则 {r.Id} 的 requirement（条款可追溯性）");
        // 缺 when/expect/finding 的规则无法执行，也不能被当成「未知原语」静默进 Unmodeled。
        RequireNonEmpty(r.When?.Kind, $"规则 {r.Id} 的 when.kind");
        RequireNonEmpty(r.Expect?.Kind, $"规则 {r.Id} 的 expect.kind");
        RequireNonEmpty(r.Finding, $"规则 {r.Id} 的 finding code");

        return new NmRuleDef(
            r.Id!, r.Requirement!, r.Phase ?? "normal",
            r.When!.Kind!, r.Expect!.Kind!, r.Finding!)
        {
            SourceRef = r.SourceRef,
            Observability = ParseObservability(r.Observability),
        };
    }

    private sealed class RuleClauseDto
    {
        public string? Kind { get; set; }
    }
}
