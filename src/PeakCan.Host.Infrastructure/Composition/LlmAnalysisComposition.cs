using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Polly;
using PeakCan.HIL.Core.Analysis;
using PeakCan.Host.Core.HIL.Analysis;
using PeakCan.Host.Infrastructure.HIL.Analysis;

namespace PeakCan.Host.Infrastructure.Composition;

/// <summary>
/// P1-1（2026-09-06，组合根裁决）：LLM 分析栈的公共组合切片。
/// <para>
/// <b>裁决：双组合根（AppHostBuilder / HeadlessHostBuilder）保留</b>——二者传输
/// 接缝刻意不同（App 走 CoreSendService 限速装饰器 + WPF 生命周期；Headless 走
/// ICanChannel + 每 run 重建），整根合并不成立。但<strong>传输无关</strong>的
/// 注册必须单源，消除双根手写漂移（此前 retry 策略 + 超时算术在两根各一份，
/// 且与 App 命名 "LlmClient" 的策略已漂移出差异——429 处理缺失）。
/// </para>
/// <para>
/// 本扩展注册：LlmOptions 绑定（传 configuration 时）、ICredentialStore 兜底
/// （<c>TryAdd</c>——App 先注册 ChainedCredentialStore 则跳过）、
/// IHilAnalysisService typed client（共享 retry 策略唯一源）。
/// </para>
/// </summary>
public static class LlmAnalysisComposition
{
    /// <summary>安装 LLM 分析栈公共注册。幂等（TryAdd）；可重复调用无副作用。</summary>
    public static IServiceCollection AddLlmAnalysis(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configuration is not null)
            services.Configure<LlmOptions>(configuration.GetSection("Llm"));

        // 兜底凭据（env var / ~/.hil/credentials）。App 组合根先注册
        // ChainedCredentialStore（WCM 优先）→ TryAdd 跳过，保留链式实现。
        services.TryAddSingleton<ICredentialStore, SimpleCredentialStore>();

        services.AddHttpClient<IHilAnalysisService, HilAnalysisService>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds * 5);
        })
        .AddPolicyHandler(GetRetryPolicy());

        return services;
    }

    /// <summary>
    /// 重试策略唯一源：transient HTTP 错误 + 429 限流，指数退避 1s → 2s → 4s。
    /// （此前在 App / Headless 各手写一份，且 App 命名 "LlmClient" 的版本缺 429。）
    /// public：App 组合根的命名 "LlmClient"（AI Chat）也复用本策略。
    /// </summary>
    public static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
        => Polly.Extensions.Http.HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(r => (int)r.StatusCode == 429)
            .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)));
}
