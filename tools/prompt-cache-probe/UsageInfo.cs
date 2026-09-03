using System.Text.Json;

namespace PeakCan.Tools.PromptCacheProbe;

/// <summary>
/// Prompt usage for one chat completion, extracted from the provider's
/// response. The shipped NuGet package (PeakCan.HIL.Core 0.14.0) drops
/// the cache fields and never surfaces usage to callers; this tool
/// re-parses the raw response so cache hits are observable.
/// </summary>
public sealed record UsageInfo(
    int PromptTokens,
    int PromptCacheHitTokens,
    int PromptCacheMissTokens)
{
    /// <summary>Fraction of the prompt served from the provider's cache.</summary>
    public double HitRatio => PromptTokens == 0 ? 0 : (double)PromptCacheHitTokens / PromptTokens;

    /// <summary>
    /// Parse a usage payload. Accepts either the raw <c>usage</c> object
    /// ({"prompt_tokens":..., "prompt_cache_hit_tokens":...}) or a full
    /// chat-completion response with a nested <c>usage</c> property.
    /// Vendors that don't report cache fields simply yield zeros.
    /// </summary>
    public static UsageInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var usage = root.TryGetProperty("usage", out var nested) ? nested : root;

        // TryGetInt32: 非整型 Number (如 100.0, 某些代理会返回浮点) 或超
        // int 范围时返回 false, 兜底为 0 —— 诊断工具不应因供应商异常崩掉。
        int Int(string name) => usage.TryGetProperty(name, out var el) && el.TryGetInt32(out var v)
            ? v
            : 0;

        return new UsageInfo(
            PromptTokens: Int("prompt_tokens"),
            PromptCacheHitTokens: Int("prompt_cache_hit_tokens"),
            PromptCacheMissTokens: Int("prompt_cache_miss_tokens"));
    }
}

/// <summary>
/// DeepSeek 上下文缓存的块对齐规则: 命中只统计完整的 64-token 块,
/// 尾部不足一个块 (<64t) 的余数即使与历史字节级一致也记为 miss。
/// 这是"system≈384t=6块整 → round-2~4 命中恒 384"现象的解释
/// (参见 MaxAlignableTokens 测试中的实测数据)。
/// </summary>
public static class CacheBlockAlignment
{
    public const int BlockTokens = 64;

    /// <summary>按 64-token 块向下对齐后的理论最大命中数
    /// (= prompt - prompt mod 64)。实际 hit 不会超过它。
    /// 负数 prompt 防御: 视为无法命中, 返回 0。</summary>
    public static int MaxAlignableTokens(int promptTokens)
        => promptTokens < 0 ? 0 : promptTokens - promptTokens % BlockTokens;
}

/// <summary>Per-million-token pricing. DeepSeek cache hits are 1/10 the
/// miss price; defaults match deepseek-chat list pricing.</summary>
public sealed record LlmPricing(decimal HitPerMillion, decimal MissPerMillion)
{
    public static readonly LlmPricing DeepSeekChat = new(HitPerMillion: 0.2m, MissPerMillion: 2.0m);
}

public static class CostCalculator
{
    /// <summary>Cost assuming the whole prompt is billed at miss price.</summary>
    public static decimal CostWithoutCache(UsageInfo usage, LlmPricing pricing)
        => usage.PromptTokens / 1_000_000m * pricing.MissPerMillion;

    /// <summary>Cost with cache. If the provider omitted cache fields
    /// (hit+miss &lt; prompt), fall back to billing everything as miss —
    /// "no cache available" means no discount, not a zero bill.</summary>
    public static decimal CostWithCache(UsageInfo usage, LlmPricing pricing)
    {
        if (usage.PromptCacheHitTokens + usage.PromptCacheMissTokens >= usage.PromptTokens)
        {
            return usage.PromptCacheMissTokens / 1_000_000m * pricing.MissPerMillion
                 + usage.PromptCacheHitTokens / 1_000_000m * pricing.HitPerMillion;
        }
        return CostWithoutCache(usage, pricing);
    }

    public static decimal Savings(UsageInfo usage, LlmPricing pricing)
        => CostWithoutCache(usage, pricing) - CostWithCache(usage, pricing);
}

/// <summary>One chat message in the probe payload.</summary>
public sealed record ProbeMessage(string Role, string Content);

/// <summary>One simulated user/assistant exchange appended per round.</summary>
public sealed record ProbeTurn(string User, string Assistant);

/// <summary>Usage observed for one probe round.</summary>
public sealed record ProbeRound(int Round, int MessageCount, UsageInfo Usage);
