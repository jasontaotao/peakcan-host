using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes AssertFrameCount steps. Counts frames of a CAN ID in the window
/// and asserts the count is within [MinCount, MaxCount].
/// </summary>
internal sealed class AssertFrameCountStepExecutor : IStepExecutor
{
    private readonly IFrameStatistics _stats;

    public AssertFrameCountStepExecutor(IFrameStatistics stats) => _stats = stats;
    public TestCaseStepKind Kind => TestCaseStepKind.AssertFrameCount;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (AssertFrameCountStep)step.Parameters;
        if (p.WindowMs <= 0 || p.MinCount > p.MaxCount)   // review L4: 非法参数 fail fast
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"Invalid params: WindowMs={p.WindowMs}, range=[{p.MinCount},{p.MaxCount}]", null, null, 0);

        var since = _stats.Now;
        await Task.Delay(p.WindowMs, ct);
        var now = _stats.Now;   // 上界收口（review M3）
        var count = _stats.CountSince(p.Id, since, now);
        bool pass = count >= p.MinCount && count <= p.MaxCount;
        return new StepResult(0, step.Kind, step.Label, pass ? StepStatus.Passed : StepStatus.Failed,
            pass
                ? $"Frame count {count} in [{p.MinCount},{p.MaxCount}]"
                : $"Frame count {count} outside [{p.MinCount},{p.MaxCount}] in {p.WindowMs}ms",
            null, null, 0);
    }
}
