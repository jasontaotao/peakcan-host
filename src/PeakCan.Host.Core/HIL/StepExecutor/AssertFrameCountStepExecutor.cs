using System.Globalization;
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes AssertFrameCount steps. Counts frames of a CAN ID in the window
/// and asserts the count is within [MinCount, MaxCount].
/// B.5: WindowMs/MinCount/MaxCount are now string (supports ${name} interpolation).
/// </summary>
internal sealed class AssertFrameCountStepExecutor : IStepExecutor
{
    private readonly IFrameStatistics _stats;

    public AssertFrameCountStepExecutor(IFrameStatistics stats) => _stats = stats;
    public TestCaseStepKind Kind => TestCaseStepKind.AssertFrameCount;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (AssertFrameCountStep)step.Parameters;
        var windowMs = int.Parse(p.WindowMs, CultureInfo.InvariantCulture);
        var minCount = int.Parse(p.MinCount, CultureInfo.InvariantCulture);
        var maxCount = int.Parse(p.MaxCount, CultureInfo.InvariantCulture);

        if (windowMs <= 0 || minCount > maxCount)   // review L4: 非法参数 fail fast
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"Invalid params: WindowMs={windowMs}, range=[{minCount},{maxCount}]", null, null, 0);

        var since = _stats.Now;
        await Task.Delay(windowMs, ct);
        var now = _stats.Now;   // 上界收口（review M3）
        // channelName 路由：null = 默认通道（单通道零回归）。
        var count = _stats.CountSince(p.Id, since, now, p.TargetChannel);
        bool pass = count >= minCount && count <= maxCount;
        return new StepResult(0, step.Kind, step.Label, pass ? StepStatus.Passed : StepStatus.Failed,
            pass
                ? $"Frame count {count} in [{minCount},{maxCount}]"
                : $"Frame count {count} outside [{minCount},{maxCount}] in {windowMs}ms",
            null, null, 0, Channel: p.TargetChannel);
    }
}
