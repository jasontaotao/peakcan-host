using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes SecurityAccess steps. Performs the full seed/key handshake via UDS.
/// </summary>
internal sealed class SecurityAccessStepExecutor : IStepExecutor
{
    private readonly IUdsSessionResolver _resolver;

    public SecurityAccessStepExecutor(IUdsSessionResolver resolver) => _resolver = resolver;
    public TestCaseStepKind Kind => TestCaseStepKind.SecurityAccess;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (SecurityAccessStep)step.Parameters;
        var session = _resolver.Resolve(p.TargetChannel);
        try
        {
            // 单一声明，两条分支共用；避免两个同作用域 pattern variable 重名（CS0136）。
            var store = ctx as IStepVariableStore;
            if (p.SeedOnly)
            {
                // SeedOnly：仅 fetch seed，不发 key（不解锁 ECU），供 invalid-key 负测试前置。
                var seed = await session.RequestSeedAsync(p.Level, ct);
                if (store is not null)
                    store.Variables["security_seed"] = seed;
                return new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
                    $"SecurityAccess level {p.Level} seed fetched ({seed.Length} bytes)", null, null, 0, Channel: p.TargetChannel);
            }

            // 完整 seed/key 握手（内部 ComputeKey 由注入的 IKeyDerivationAlgorithm 提供）
            await session.SecurityAccessAsync(p.Level, ct);
            if (store is not null)
                store.Variables["security_level"] = new[] { p.Level };   // byte[] 统一，供 AssertDidValue 断言
            return new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
                $"SecurityAccess level {p.Level} authenticated", null, null, 0, Channel: p.TargetChannel);
        }
        catch (UdsSessionException ex)
        {
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"SecurityAccess level {p.Level} failed: {ex.Message}", null, null, 0, Channel: p.TargetChannel);
        }
        catch (KeyAlgorithmNotConfiguredException ex)   // review H-2：headless 未注入密钥算法时给出明确失败
        {
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"SecurityAccess level {p.Level} not configured: {ex.Message}", null, null, 0, Channel: p.TargetChannel);
        }
        catch (InvalidOperationException ex)            // 2 参构造缺 IKeyDerivationAlgorithm 的路径
        {
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"SecurityAccess level {p.Level} unavailable: {ex.Message}", null, null, 0, Channel: p.TargetChannel);
        }
    }
}
