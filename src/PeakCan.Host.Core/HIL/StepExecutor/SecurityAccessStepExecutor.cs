using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes SecurityAccess steps. Performs the full seed/key handshake via UDS.
/// </summary>
internal sealed class SecurityAccessStepExecutor : IStepExecutor
{
    private readonly UdsClient _uds;

    public SecurityAccessStepExecutor(UdsClient uds) => _uds = uds;
    public TestCaseStepKind Kind => TestCaseStepKind.SecurityAccess;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (SecurityAccessStep)step.Parameters;
        try
        {
            if (p.SeedOnly)
            {
                // SeedOnly：仅 fetch seed，不发 key（不解锁 ECU），供 invalid-key 负测试前置。
                var seed = await _uds.RequestSeedAsync(p.Level, ct);
                if (ctx is IStepVariableStore store)
                    store.Variables["security_seed"] = seed;
                return new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
                    $"SecurityAccess level {p.Level} seed fetched ({seed.Length} bytes)", null, null, 0);
            }

            // 完整 seed/key 握手（内部 ComputeKey 由注入的 IKeyDerivationAlgorithm 提供）
            await _uds.SecurityAccessAsync(p.Level, ct);
            if (ctx is IStepVariableStore store2)
                store2.Variables["security_level"] = new[] { p.Level };   // byte[] 统一，供 AssertDidValue 断言
            return new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
                $"SecurityAccess level {p.Level} authenticated", null, null, 0);
        }
        catch (UdsException ex)
        {
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"SecurityAccess level {p.Level} failed: {ex.Message}", null, null, 0);
        }
        catch (KeyAlgorithmNotConfiguredException ex)   // review H-2：headless 未注入密钥算法时给出明确失败
        {
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"SecurityAccess level {p.Level} not configured: {ex.Message}", null, null, 0);
        }
        catch (InvalidOperationException ex)            // 2 参构造缺 IKeyDerivationAlgorithm 的路径
        {
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"SecurityAccess level {p.Level} unavailable: {ex.Message}", null, null, 0);
        }
    }
}
