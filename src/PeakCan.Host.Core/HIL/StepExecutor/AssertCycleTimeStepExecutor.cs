using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes AssertCycleTime steps. Measures inter-frame periods in the window.
/// 语义（plan 定稿）：窗口内帧数 &lt; MinSamples → Failed（不无限等待，窗口耗尽即报）；
/// 否则取 [since, since+window] 帧间隔，比对 [MinMs, MaxMs]。
/// </summary>
internal sealed class AssertCycleTimeStepExecutor : IStepExecutor
{
    private readonly IFrameStatistics _stats;

    public AssertCycleTimeStepExecutor(IFrameStatistics stats) => _stats = stats;
    public TestCaseStepKind Kind => TestCaseStepKind.AssertCycleTime;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (AssertCycleTimeStep)step.Parameters;
        if (p.WindowMs <= 0 || p.MinMs > p.MaxMs)   // review L4: 非法参数 fail fast
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"Invalid params: WindowMs={p.WindowMs}, range=[{p.MinMs},{p.MaxMs}]", null, null, 0);

        var since = _stats.Now;
        var windowEnd = since + p.WindowMs;   // 单调 ms 直接相加
        await Task.Delay(p.WindowMs, ct);

        var s = _stats.GetIntervalStats(p.Id, since, windowEnd);
        // review L1: 至少 2 帧才有间隔；同时满足 MinSamples
        if (s.SampleCount < 2 || s.SampleCount < p.MinSamples)
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"Only {s.SampleCount} frames for 0x{p.Id.Raw:X}, need >= {Math.Max(2, p.MinSamples)} (window {p.WindowMs}ms)", null, null, 0);

        // review M1: 逐区间判定（所有间隔落在 [MinMs, MaxMs]），而非均值掩盖个别越界周期
        bool pass = s.MinMs >= p.MinMs && s.MaxMs <= p.MaxMs;
        return new StepResult(0, step.Kind, step.Label, pass ? StepStatus.Passed : StepStatus.Failed,
            pass
                ? $"Cycle [{s.MinMs:F1},{s.MaxMs:F1}]ms in [{p.MinMs},{p.MaxMs}] (n={s.SampleCount})"
                : $"Cycle [{s.MinMs:F1},{s.MaxMs:F1}]ms outside [{p.MinMs},{p.MaxMs}] (n={s.SampleCount})",
            null, null, 0);
    }
}
