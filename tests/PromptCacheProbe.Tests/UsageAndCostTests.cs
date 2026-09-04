using FluentAssertions;
using PeakCan.Tools.PromptCacheProbe;

namespace PromptCacheProbe.Tests;

public class UsageInfoTests
{
    [Fact]
    public void Parse_Extracts_Hit_And_Miss_Tokens()
    {
        // Arrange — DeepSeek 非流式响应 usage 对象
        var json = """
            {
              "prompt_tokens": 120,
              "completion_tokens": 30,
              "total_tokens": 150,
              "prompt_cache_hit_tokens": 90,
              "prompt_cache_miss_tokens": 30
            }
            """;

        // Act
        var usage = UsageInfo.Parse(json);

        // Assert
        usage.PromptTokens.Should().Be(120);
        usage.PromptCacheHitTokens.Should().Be(90);
        usage.PromptCacheMissTokens.Should().Be(30);
        usage.HitRatio.Should().BeApproximately(0.75, 1e-9);
    }

    [Fact]
    public void Parse_Tolerates_Missing_Cache_Fields()
    {
        // Arrange — 某些厂商不返回 cache 字段，解析不应崩
        var json = """
            {
              "prompt_tokens": 50,
              "completion_tokens": 10,
              "total_tokens": 60
            }
            """;

        // Act
        var usage = UsageInfo.Parse(json);

        // Assert
        usage.PromptCacheHitTokens.Should().Be(0);
        usage.PromptCacheMissTokens.Should().Be(0);
        usage.HitRatio.Should().Be(0);
    }

    [Fact]
    public void Parse_Handles_Zero_Prompt_Tokens_Without_Division_Error()
    {
        // Arrange
        const string json = """{"prompt_tokens":0,"prompt_cache_hit_tokens":0}""";

        // Act
        var usage = UsageInfo.Parse(json);

        // Assert
        usage.HitRatio.Should().Be(0);
    }

    [Fact]
    public void Parse_Tolerates_Fractional_Number_Tokens()
    {
        // Arrange — 某些代理把 token 计为浮点 (100.0), 解析不应崩
        const string json = """
            {"prompt_tokens":100.0,"prompt_cache_hit_tokens":90.5,"prompt_cache_miss_tokens":9.5}
            """;

        // Act
        var usage = UsageInfo.Parse(json);

        // Assert — TryGetInt32 失败时兜底为 0, 工具继续运行
        usage.PromptTokens.Should().Be(0);
        usage.PromptCacheHitTokens.Should().Be(0);
        usage.PromptCacheMissTokens.Should().Be(0);
        usage.HitRatio.Should().Be(0);
    }

    [Fact]
    public void Parse_Handles_Nested_Usage_With_Fractional_Tokens()
    {
        // Arrange — 完整响应 + usage 子对象 + 浮点 token
        const string json = """
            {
              "id": "chatcmpl-9", "model": "deepseek-chat",
              "choices": [{ "index": 0, "message": { "role": "assistant", "content": "ok" } }],
              "usage": { "prompt_tokens": 200.0, "completion_tokens": 10, "prompt_cache_hit_tokens": 160.0 }
            }
            """;

        // Act
        var usage = UsageInfo.Parse(json);

        // Assert
        usage.PromptTokens.Should().Be(0);
        usage.PromptCacheHitTokens.Should().Be(0);
    }
}

public class CacheBlockAlignmentTests
{
    // 回归: DeepSeek 只对完整 64-token 块计命中。B 组实验 system≈384t(=6块),
    // round-2~4 命中恒 384 曾误导为"缓存封顶", 实际是尾部 (<64t) 即便字节一致
    // 也记为 miss。MaxAlignableTokens 给出理论最大命中, 用于解释该现象。
    [Theory]
    [InlineData(0, 0)]
    [InlineData(63, 0)]
    [InlineData(64, 64)]
    [InlineData(65, 64)]
    [InlineData(384, 384)]
    [InlineData(397, 384)]   // 实测: round-1 hit=384, 余 13 不计
    [InlineData(431, 384)]   // 实测: R2 同请求二次 hit=384, 余 47 不计
    [InlineData(1690, 1664)] // 实测: big #1 hit=1664 (=26块), 余 26 不计
    public void MaxAlignableTokens_Aligns_Down_To_Block_Token_Count(int prompt, int expected)
    {
        CacheBlockAlignment.MaxAlignableTokens(prompt).Should().Be(expected);
    }

    [Fact]
    public void BlockTokens_Is_64()
    {
        CacheBlockAlignment.BlockTokens.Should().Be(64);
    }
}

public class CostCalculatorTests
{
    private static readonly LlmPricing Pricing = new(HitPerMillion: 0.2m, MissPerMillion: 2.0m);

    [Fact]
    public void Cache_Hit_Is_Charged_At_One_Tenth_Of_Miss()
    {
        // Arrange — 全部命中: 1000 prompt tokens 全来自 cache
        var usage = new UsageInfo(PromptTokens: 1000, PromptCacheHitTokens: 1000, PromptCacheMissTokens: 0);

        // Act
        var withCache = CostCalculator.CostWithCache(usage, Pricing);
        var withoutCache = CostCalculator.CostWithoutCache(usage, Pricing);

        // Assert — 无缓存: 1000/1M * 2.0 = 0.002 元; 有缓存: 1000/1M * 0.2 = 0.0002 元
        withoutCache.Should().BeApproximately(0.002m, 1e-9m);
        withCache.Should().BeApproximately(0.0002m, 1e-9m);
    }

    [Fact]
    public void Savings_Is_Difference_Between_Miss_And_Hit_Pricing()
    {
        // Arrange — 50% 命中: 1000 tokens, 500 hit / 500 miss
        var usage = new UsageInfo(PromptTokens: 1000, PromptCacheHitTokens: 500, PromptCacheMissTokens: 500);

        // Act
        var savings = CostCalculator.Savings(usage, Pricing);

        // Assert
        // 无缓存: 1000/1M*2.0 = 0.002; 有缓存: 500/1M*2.0 + 500/1M*0.2 = 0.001 + 0.0001 = 0.0011
        // savings = 0.002 - 0.0011 = 0.0009
        savings.Should().BeApproximately(0.0009m, 1e-9m);
    }

    [Fact]
    public void No_Cache_Fields_Means_Zero_Savings()
    {
        var usage = new UsageInfo(PromptTokens: 100, PromptCacheHitTokens: 0, PromptCacheMissTokens: 0);
        CostCalculator.Savings(usage, Pricing).Should().Be(0);
    }

    [Fact]
    public void Partial_Cache_Fields_Falls_Back_To_Full_Miss_Pricing()
    {
        // Arrange — hit+miss < prompt (厂商只返回 hit 没返回 miss): 不能按
        // 超额计费, 应整体按 miss 计价, 节省为 0
        var usage = new UsageInfo(PromptTokens: 1000, PromptCacheHitTokens: 400, PromptCacheMissTokens: 0);

        // Act
        var withCache = CostCalculator.CostWithCache(usage, Pricing);

        // Assert — fallback 分支: 1000/1M * 2.0 = 0.002
        withCache.Should().BeApproximately(0.002m, 1e-9m);
        CostCalculator.Savings(usage, Pricing).Should().Be(0);
    }
}
