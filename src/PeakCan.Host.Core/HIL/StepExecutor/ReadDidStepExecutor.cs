using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.Uds;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes ReadDid steps. Reads a DID via UDS and stores the bytes into
/// <see cref="IStepVariableStore"/> for a later AssertDidValue step.
/// Task B 第一步（Q1，spec 2026-08-27）：依赖 IUdsSession 接口而非 concrete UdsClient
/// （多通道路由 IUdsSessionResolver 的前置统一）。异常契约：NRC → UdsNrcException、
/// 传输失败 → UdsSessionTransportException（均派生自 UdsSessionException，见 UdsSessionAdapter）。
/// </summary>
internal sealed class ReadDidStepExecutor : IStepExecutor
{
    private readonly IUdsSessionResolver _resolver;

    public ReadDidStepExecutor(IUdsSessionResolver resolver) => _resolver = resolver;
    public TestCaseStepKind Kind => TestCaseStepKind.ReadDid;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (ReadDidStep)step.Parameters;
        var session = _resolver.Resolve(p.TargetChannel);
        try
        {
            // UDS 超时由 UdsTimer（P2/P2*）管理，不传 timeoutMs；取消经 ct
            var data = await session.ReadDataByIdentifierAsync(p.Did, ct);
            var key = p.OutputVar ?? DidVariableKey.Format(p.Did);
            if (ctx is IStepVariableStore store)
                store.Variables[key] = data;
            return new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
                $"Read DID 0x{p.Did:X4}: {Convert.ToHexString(data)}", null, null, 0, Channel: p.TargetChannel);
        }
        catch (UdsSessionException ex)   // NRC（UdsNrcException）/ 传输失败（UdsSessionTransportException）
        {
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"ReadDID 0x{p.Did:X4} failed: {ex.Message}", null, null, 0, Channel: p.TargetChannel);
        }
    }
}
