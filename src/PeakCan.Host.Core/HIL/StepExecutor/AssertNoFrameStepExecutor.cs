using System.Globalization;
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes AssertNoFrame steps. Forward-looking silence check: records a
/// start timestamp, waits the window, then counts frames arriving since start.
/// B.5: WindowMs is now string (supports ${name} interpolation).
/// </summary>
internal sealed class AssertNoFrameStepExecutor : IStepExecutor
{
    private readonly IFrameStatistics _stats;

    public AssertNoFrameStepExecutor(IFrameStatistics stats) => _stats = stats;
    public TestCaseStepKind Kind => TestCaseStepKind.AssertNoFrame;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (AssertNoFrameStep)step.Parameters;
        var windowMs = int.Parse(p.WindowMs, CultureInfo.InvariantCulture);
        if (windowMs <= 0)   // review L4: 非法窗口 fail fast，而非 Task.Delay 抛异常
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"WindowMs must be > 0 (got {windowMs})", null, null, 0);

        var since = _stats.Now;
        await Task.Delay(windowMs, ct);
        var now = _stats.Now;   // 实际窗口终点（Delay 返回后）——上界收口，排除尾部间隙帧（review M3）
        // channelName 路由：null = 默认通道（单通道零回归）。
        var count = _stats.CountSince(p.Id, since, now, p.TargetChannel);
        return count == 0
            ? new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
                $"No frame 0x{p.Id.Raw:X} in {windowMs}ms", null, null, 0, Channel: p.TargetChannel)
            : new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"Expected 0 frames for 0x{p.Id.Raw:X} in {windowMs}ms, got {count}", null, null, 0, Channel: p.TargetChannel);
    }
}
